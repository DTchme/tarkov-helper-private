using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Stores only compact quest start/completion/failure events in user_data.db.
/// The original EFT log files can then be removed without losing events that
/// TarkovHelper has already observed.
/// </summary>
public sealed class QuestLogArchiveService
{
    private static readonly ILogger _log = Log.For<QuestLogArchiveService>();
    private static QuestLogArchiveService? _instance;
    public static QuestLogArchiveService Instance => _instance ??= new QuestLogArchiveService();

    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _isInitialized;

    public QuestLogArchiveStats CachedStats { get; private set; } = QuestLogArchiveStats.Empty;

    private QuestLogArchiveService()
    {
    }

    private string GetConnectionString()
    {
        return UserDataDbService.BuildApplicationConnectionString(
            UserDataDbService.Instance.DatabasePath);
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        await _initializeLock.WaitAsync();
        try
        {
            if (_isInitialized)
                return;

            await UserDataDbService.Instance.InitializeAsync();

            await using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS QuestEventArchive (
                    EventKey TEXT PRIMARY KEY,
                    QuestId TEXT NOT NULL,
                    EventType INTEGER NOT NULL,
                    SourceProfile INTEGER NOT NULL,
                    GenerationId TEXT NOT NULL DEFAULT '',
                    CharacterProfileId TEXT,
                    TraderId TEXT,
                    EventTimestampUnix INTEGER NOT NULL,
                    SourceFile TEXT,
                    ArchivedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS QuestLogArchiveFiles (
                    FilePath TEXT PRIMARY KEY COLLATE NOCASE,
                    FileLength INTEGER NOT NULL,
                    LastWriteUtcTicks INTEGER NOT NULL,
                    LastReadOffset INTEGER NOT NULL DEFAULT 0,
                    LastSourceProfile INTEGER NOT NULL DEFAULT 0,
                    PendingText TEXT NOT NULL DEFAULT '',
                    ArchivedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS QuestArchiveGenerations (
                    Id TEXT PRIMARY KEY,
                    SourceProfile INTEGER NOT NULL,
                    CharacterProfileId TEXT,
                    StartedAt TEXT NOT NULL,
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    CreatedReason TEXT NOT NULL DEFAULT 'automatic'
                );

                CREATE INDEX IF NOT EXISTS idx_quest_event_archive_profile_time
                    ON QuestEventArchive(SourceProfile, EventTimestampUnix);
                CREATE INDEX IF NOT EXISTS idx_quest_event_archive_quest
                    ON QuestEventArchive(QuestId);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_quest_archive_one_active_generation
                    ON QuestArchiveGenerations(SourceProfile) WHERE IsActive = 1;
            ";
            await command.ExecuteNonQueryAsync();

            await EnsureColumnAsync(connection, "QuestEventArchive", "GenerationId", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(connection, "QuestEventArchive", "CharacterProfileId", "TEXT");
            await EnsureColumnAsync(connection, "QuestLogArchiveFiles", "LastReadOffset", "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(connection, "QuestLogArchiveFiles", "LastSourceProfile", "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(connection, "QuestLogArchiveFiles", "PendingText", "TEXT NOT NULL DEFAULT ''");
            await MigrateLegacyArchiveAsync(connection);

            await using var generationIndex = connection.CreateCommand();
            generationIndex.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_quest_event_archive_generation_time
                ON QuestEventArchive(GenerationId, EventTimestampUnix)";
            await generationIndex.ExecuteNonQueryAsync();

            _isInitialized = true;
            await RefreshStatsAsync();
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await check.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}";
        await alter.ExecuteNonQueryAsync();
    }

    private static async Task MigrateLegacyArchiveAsync(SqliteConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var profile in new[]
                     {
                         LogProfileKind.Pvp,
                         LogProfileKind.Pve,
                         LogProfileKind.SeasonalPvp
                     })
            {
                var generationId = $"legacy-{(int)profile}";
                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = (SqliteTransaction)transaction;
                    insert.CommandText = @"
                        INSERT OR IGNORE INTO QuestArchiveGenerations
                            (Id, SourceProfile, CharacterProfileId, StartedAt, IsActive, CreatedReason)
                        SELECT @id, @profile, '', @startedAt, 1, 'v1.5.19 migration'
                        WHERE EXISTS (
                            SELECT 1 FROM QuestEventArchive WHERE SourceProfile = @profile)
                          AND NOT EXISTS (
                            SELECT 1 FROM QuestArchiveGenerations WHERE SourceProfile = @profile)";
                    insert.Parameters.AddWithValue("@id", generationId);
                    insert.Parameters.AddWithValue("@profile", (int)profile);
                    insert.Parameters.AddWithValue("@startedAt", DateTime.UtcNow.ToString("o"));
                    await insert.ExecuteNonQueryAsync();
                }

                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = (SqliteTransaction)transaction;
                    update.CommandText = @"
                        UPDATE QuestEventArchive
                        SET GenerationId = COALESCE((
                            SELECT Id FROM QuestArchiveGenerations
                            WHERE SourceProfile = @profile AND IsActive = 1
                            LIMIT 1), @legacyId)
                        WHERE SourceProfile = @profile
                          AND (GenerationId IS NULL OR GenerationId = '')";
                    update.Parameters.AddWithValue("@profile", (int)profile);
                    update.Parameters.AddWithValue("@legacyId", generationId);
                    await update.ExecuteNonQueryAsync();
                }
            }

            // v1.5.18 stored the complete file length after a successful scan. Preserve
            // that point as the first incremental cursor so old logs are not reparsed.
            await using (var cursorMigration = connection.CreateCommand())
            {
                cursorMigration.Transaction = (SqliteTransaction)transaction;
                cursorMigration.CommandText = @"
                    UPDATE QuestLogArchiveFiles
                    SET LastReadOffset = FileLength
                    WHERE LastReadOffset = 0 AND FileLength > 0";
                await cursorMigration.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<bool> IsFileCurrentAsync(string filePath, long fileLength, long lastWriteUtcTicks)
    {
        await InitializeAsync();

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*)
            FROM QuestLogArchiveFiles
            WHERE FilePath = @filePath
              AND FileLength = @fileLength
              AND LastWriteUtcTicks = @lastWriteUtcTicks";
        command.Parameters.AddWithValue("@filePath", Path.GetFullPath(filePath));
        command.Parameters.AddWithValue("@fileLength", fileLength);
        command.Parameters.AddWithValue("@lastWriteUtcTicks", lastWriteUtcTicks);

        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    public async Task<QuestLogFileCheckpoint?> GetFileCheckpointAsync(string filePath)
    {
        await InitializeAsync();

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT FileLength, LastWriteUtcTicks, LastReadOffset, LastSourceProfile, PendingText
            FROM QuestLogArchiveFiles
            WHERE FilePath = @filePath";
        command.Parameters.AddWithValue("@filePath", Path.GetFullPath(filePath));

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new QuestLogFileCheckpoint(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            (LogProfileKind)reader.GetInt32(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4));
    }

    public async Task<QuestLogArchiveWriteResult> ArchiveFileEventsAsync(
        string filePath,
        long fileLength,
        long lastWriteUtcTicks,
        IEnumerable<QuestLogEvent> events,
        long? lastReadOffset = null,
        LogProfileKind lastSourceProfile = LogProfileKind.Unknown,
        string? pendingText = null)
    {
        await InitializeAsync();

        var eventList = events.ToList();
        await _writeLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var result = await InsertEventsAsync(connection, (SqliteTransaction)transaction, eventList);

                await using var fileCommand = connection.CreateCommand();
                fileCommand.Transaction = (SqliteTransaction)transaction;
                fileCommand.CommandText = @"
                    INSERT INTO QuestLogArchiveFiles
                        (FilePath, FileLength, LastWriteUtcTicks, LastReadOffset,
                         LastSourceProfile, PendingText, ArchivedAt)
                    VALUES
                        (@filePath, @fileLength, @lastWriteUtcTicks, @lastReadOffset,
                         @lastSourceProfile, @pendingText, @archivedAt)
                    ON CONFLICT(FilePath) DO UPDATE SET
                        FileLength = excluded.FileLength,
                        LastWriteUtcTicks = excluded.LastWriteUtcTicks,
                        LastReadOffset = excluded.LastReadOffset,
                        LastSourceProfile = excluded.LastSourceProfile,
                        PendingText = excluded.PendingText,
                        ArchivedAt = excluded.ArchivedAt";
                fileCommand.Parameters.AddWithValue("@filePath", Path.GetFullPath(filePath));
                fileCommand.Parameters.AddWithValue("@fileLength", fileLength);
                fileCommand.Parameters.AddWithValue("@lastWriteUtcTicks", lastWriteUtcTicks);
                fileCommand.Parameters.AddWithValue("@lastReadOffset", lastReadOffset ?? fileLength);
                fileCommand.Parameters.AddWithValue("@lastSourceProfile", (int)lastSourceProfile);
                fileCommand.Parameters.AddWithValue("@pendingText", pendingText ?? string.Empty);
                fileCommand.Parameters.AddWithValue("@archivedAt", DateTime.UtcNow.ToString("o"));
                await fileCommand.ExecuteNonQueryAsync();

                await transaction.CommitAsync();
                await RefreshStatsAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<QuestLogArchiveWriteResult> ArchiveEventsAsync(IEnumerable<QuestLogEvent> events)
    {
        await InitializeAsync();

        var eventList = events.ToList();
        await _writeLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var result = await InsertEventsAsync(connection, (SqliteTransaction)transaction, eventList);
                await transaction.CommitAsync();
                await RefreshStatsAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<QuestLogArchiveWriteResult> InsertEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<QuestLogEvent> events)
    {
        var added = 0;
        var duplicates = 0;
        var skippedUnknownProfile = 0;
        var archivedAt = DateTime.UtcNow.ToString("o");

        foreach (var evt in events)
        {
            if (evt.SourceProfile == LogProfileKind.Unknown || string.IsNullOrWhiteSpace(evt.QuestId))
            {
                skippedUnknownProfile++;
                continue;
            }

            var generationId = await ResolveGenerationAsync(
                connection,
                transaction,
                evt.SourceProfile,
                evt.CharacterProfileId,
                "automatic character detection");
            evt.ArchiveGenerationId = generationId;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT OR IGNORE INTO QuestEventArchive
                    (EventKey, QuestId, EventType, SourceProfile, GenerationId,
                     CharacterProfileId, TraderId, EventTimestampUnix, SourceFile, ArchivedAt)
                VALUES
                    (@eventKey, @questId, @eventType, @sourceProfile, @generationId,
                     @characterProfileId, @traderId, @eventTimestampUnix, @sourceFile, @archivedAt)";
            command.Parameters.AddWithValue("@eventKey", GetEventKey(evt));
            command.Parameters.AddWithValue("@questId", evt.QuestId);
            command.Parameters.AddWithValue("@eventType", (int)evt.EventType);
            command.Parameters.AddWithValue("@sourceProfile", (int)evt.SourceProfile);
            command.Parameters.AddWithValue("@generationId", generationId);
            command.Parameters.AddWithValue("@characterProfileId", evt.CharacterProfileId ?? string.Empty);
            command.Parameters.AddWithValue("@traderId", evt.TraderId ?? string.Empty);
            command.Parameters.AddWithValue("@eventTimestampUnix", ToUnixTimeSeconds(evt.Timestamp));
            command.Parameters.AddWithValue("@sourceFile", evt.SourceFile ?? string.Empty);
            command.Parameters.AddWithValue("@archivedAt", archivedAt);

            if (await command.ExecuteNonQueryAsync() > 0)
                added++;
            else
                duplicates++;
        }

        return new QuestLogArchiveWriteResult(events.Count, added, duplicates, skippedUnknownProfile);
    }

    private static async Task<string> ResolveGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LogProfileKind sourceProfile,
        string? characterProfileId,
        string reason)
    {
        string? activeId = null;
        string? activeCharacterId = null;

        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = @"
                SELECT Id, CharacterProfileId
                FROM QuestArchiveGenerations
                WHERE SourceProfile = @profile AND IsActive = 1
                LIMIT 1";
            select.Parameters.AddWithValue("@profile", (int)sourceProfile);
            await using var reader = await select.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                activeId = reader.GetString(0);
                activeCharacterId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        var normalizedCharacterId = characterProfileId?.Trim() ?? string.Empty;
        if (activeId != null &&
            (string.IsNullOrEmpty(normalizedCharacterId) ||
             string.Equals(activeCharacterId, normalizedCharacterId, StringComparison.OrdinalIgnoreCase)))
        {
            return activeId;
        }

        if (activeId != null && string.IsNullOrEmpty(activeCharacterId))
        {
            await using var bind = connection.CreateCommand();
            bind.Transaction = transaction;
            bind.CommandText = @"
                UPDATE QuestArchiveGenerations
                SET CharacterProfileId = @characterProfileId
                WHERE Id = @id";
            bind.Parameters.AddWithValue("@characterProfileId", normalizedCharacterId);
            bind.Parameters.AddWithValue("@id", activeId);
            await bind.ExecuteNonQueryAsync();
            return activeId;
        }

        if (activeId != null)
        {
            await using var deactivate = connection.CreateCommand();
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE QuestArchiveGenerations SET IsActive = 0 WHERE Id = @id";
            deactivate.Parameters.AddWithValue("@id", activeId);
            await deactivate.ExecuteNonQueryAsync();
        }

        var newGenerationId = Guid.NewGuid().ToString("N");
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = @"
            INSERT INTO QuestArchiveGenerations
                (Id, SourceProfile, CharacterProfileId, StartedAt, IsActive, CreatedReason)
            VALUES
                (@id, @profile, @characterProfileId, @startedAt, 1, @reason)";
        insert.Parameters.AddWithValue("@id", newGenerationId);
        insert.Parameters.AddWithValue("@profile", (int)sourceProfile);
        insert.Parameters.AddWithValue("@characterProfileId", normalizedCharacterId);
        insert.Parameters.AddWithValue("@startedAt", DateTime.UtcNow.ToString("o"));
        insert.Parameters.AddWithValue("@reason", reason);
        await insert.ExecuteNonQueryAsync();
        return newGenerationId;
    }

    public async Task<List<QuestLogEvent>> LoadEventsAsync(ProfileType profileType)
    {
        await InitializeAsync();

        var sourceProfile = ToLogProfile(profileType);
        var events = new List<QuestLogEvent>();
        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT e.QuestId, e.EventType, e.SourceProfile, e.TraderId,
                   e.EventTimestampUnix, e.SourceFile, e.CharacterProfileId, e.GenerationId
            FROM QuestEventArchive e
            INNER JOIN QuestArchiveGenerations g ON g.Id = e.GenerationId
            WHERE e.SourceProfile = @profile AND g.IsActive = 1
            ORDER BY e.EventTimestampUnix, e.EventKey";
        command.Parameters.AddWithValue("@profile", (int)sourceProfile);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new QuestLogEvent
            {
                QuestId = reader.GetString(0),
                EventType = (QuestEventType)reader.GetInt32(1),
                SourceProfile = (LogProfileKind)reader.GetInt32(2),
                TraderId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).LocalDateTime,
                SourceFile = reader.IsDBNull(5) ? null : reader.GetString(5),
                CharacterProfileId = reader.IsDBNull(6) ? null : reader.GetString(6),
                ArchiveGenerationId = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        return events;
    }

    public async Task<string> StartNewGenerationAsync(ProfileType profileType, string reason = "manual wipe reset")
    {
        await InitializeAsync();
        var sourceProfile = ToLogProfile(profileType);

        await _writeLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await using (var deactivate = connection.CreateCommand())
                {
                    deactivate.Transaction = (SqliteTransaction)transaction;
                    deactivate.CommandText = @"
                        UPDATE QuestArchiveGenerations
                        SET IsActive = 0
                        WHERE SourceProfile = @profile AND IsActive = 1";
                    deactivate.Parameters.AddWithValue("@profile", (int)sourceProfile);
                    await deactivate.ExecuteNonQueryAsync();
                }

                var generationId = await ResolveGenerationAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    sourceProfile,
                    null,
                    reason);
                await transaction.CommitAsync();
                await RefreshStatsAsync();
                return generationId;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static LogProfileKind ToLogProfile(ProfileType profileType)
    {
        return profileType switch
        {
            ProfileType.Pvp => LogProfileKind.Pvp,
            ProfileType.Pve => LogProfileKind.Pve,
            ProfileType.SeasonalPvp => LogProfileKind.SeasonalPvp,
            _ => LogProfileKind.Unknown
        };
    }

    public async Task<QuestLogArchiveStats> RefreshStatsAsync()
    {
        if (!_isInitialized)
        {
            await InitializeAsync();
            return CachedStats;
        }

        var counts = new Dictionary<LogProfileKind, int>();
        var total = 0;
        var trackedFiles = 0;
        var inactiveGenerations = 0;

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                SELECT e.SourceProfile, COUNT(*)
                FROM QuestEventArchive e
                INNER JOIN QuestArchiveGenerations g ON g.Id = e.GenerationId
                WHERE g.IsActive = 1
                GROUP BY e.SourceProfile";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var profile = (LogProfileKind)reader.GetInt32(0);
                var count = reader.GetInt32(1);
                counts[profile] = count;
                total += count;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM QuestLogArchiveFiles";
            trackedFiles = Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM QuestArchiveGenerations WHERE IsActive = 0";
            inactiveGenerations = Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        var databaseSize = File.Exists(UserDataDbService.Instance.DatabasePath)
            ? new FileInfo(UserDataDbService.Instance.DatabasePath).Length
            : 0;
        CachedStats = new QuestLogArchiveStats(
            total,
            counts.GetValueOrDefault(LogProfileKind.Pvp),
            counts.GetValueOrDefault(LogProfileKind.Pve),
            counts.GetValueOrDefault(LogProfileKind.SeasonalPvp),
            trackedFiles,
            inactiveGenerations,
            databaseSize);
        return CachedStats;
    }

    public int GetCachedEventCount(ProfileType profileType)
    {
        return profileType switch
        {
            ProfileType.Pvp => CachedStats.PvpEvents,
            ProfileType.Pve => CachedStats.PveEvents,
            ProfileType.SeasonalPvp => CachedStats.SeasonalPvpEvents,
            _ => 0
        };
    }

    public static string GetEventKey(QuestLogEvent evt)
    {
        var stableValue = string.Join(
            "|",
            (int)evt.SourceProfile,
            evt.QuestId.Trim(),
            (int)evt.EventType,
            ToUnixTimeSeconds(evt.Timestamp),
            evt.TraderId?.Trim() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableValue))).ToLowerInvariant();
    }

    private static long ToUnixTimeSeconds(DateTime timestamp)
    {
        return new DateTimeOffset(timestamp).ToUnixTimeSeconds();
    }
}

public sealed record QuestLogArchiveWriteResult(
    int Examined,
    int Added,
    int Duplicates,
    int SkippedUnknownProfile);

public sealed record QuestLogArchiveStats(
    int TotalEvents,
    int PvpEvents,
    int PveEvents,
    int SeasonalPvpEvents,
    int TrackedFiles,
    int InactiveGenerations,
    long UserDatabaseSizeBytes)
{
    public static QuestLogArchiveStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

public sealed record QuestLogFileCheckpoint(
    long FileLength,
    long LastWriteUtcTicks,
    long LastReadOffset,
    LogProfileKind LastSourceProfile,
    string PendingText);

public sealed record QuestLogFolderArchiveResult(
    int FilesFound,
    int FilesScanned,
    int FilesSkippedUnchanged,
    int EventsFound,
    int EventsAdded,
    int DuplicateEvents,
    int EventsSkippedUnknownProfile);
