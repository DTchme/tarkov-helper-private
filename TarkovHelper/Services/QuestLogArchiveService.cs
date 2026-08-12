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
        return new SqliteConnectionStringBuilder
        {
            DataSource = UserDataDbService.Instance.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
            Pooling = true,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString;
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
                    TraderId TEXT,
                    EventTimestampUnix INTEGER NOT NULL,
                    SourceFile TEXT,
                    ArchivedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS QuestLogArchiveFiles (
                    FilePath TEXT PRIMARY KEY COLLATE NOCASE,
                    FileLength INTEGER NOT NULL,
                    LastWriteUtcTicks INTEGER NOT NULL,
                    ArchivedAt TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_quest_event_archive_profile_time
                    ON QuestEventArchive(SourceProfile, EventTimestampUnix);
                CREATE INDEX IF NOT EXISTS idx_quest_event_archive_quest
                    ON QuestEventArchive(QuestId);
            ";
            await command.ExecuteNonQueryAsync();

            _isInitialized = true;
            await RefreshStatsAsync();
        }
        finally
        {
            _initializeLock.Release();
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

    public async Task<QuestLogArchiveWriteResult> ArchiveFileEventsAsync(
        string filePath,
        long fileLength,
        long lastWriteUtcTicks,
        IEnumerable<QuestLogEvent> events)
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
                    INSERT INTO QuestLogArchiveFiles (FilePath, FileLength, LastWriteUtcTicks, ArchivedAt)
                    VALUES (@filePath, @fileLength, @lastWriteUtcTicks, @archivedAt)
                    ON CONFLICT(FilePath) DO UPDATE SET
                        FileLength = excluded.FileLength,
                        LastWriteUtcTicks = excluded.LastWriteUtcTicks,
                        ArchivedAt = excluded.ArchivedAt";
                fileCommand.Parameters.AddWithValue("@filePath", Path.GetFullPath(filePath));
                fileCommand.Parameters.AddWithValue("@fileLength", fileLength);
                fileCommand.Parameters.AddWithValue("@lastWriteUtcTicks", lastWriteUtcTicks);
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

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT OR IGNORE INTO QuestEventArchive
                    (EventKey, QuestId, EventType, SourceProfile, TraderId,
                     EventTimestampUnix, SourceFile, ArchivedAt)
                VALUES
                    (@eventKey, @questId, @eventType, @sourceProfile, @traderId,
                     @eventTimestampUnix, @sourceFile, @archivedAt)";
            command.Parameters.AddWithValue("@eventKey", GetEventKey(evt));
            command.Parameters.AddWithValue("@questId", evt.QuestId);
            command.Parameters.AddWithValue("@eventType", (int)evt.EventType);
            command.Parameters.AddWithValue("@sourceProfile", (int)evt.SourceProfile);
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

    public async Task<List<QuestLogEvent>> LoadEventsAsync()
    {
        await InitializeAsync();

        var events = new List<QuestLogEvent>();
        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT QuestId, EventType, SourceProfile, TraderId, EventTimestampUnix, SourceFile
            FROM QuestEventArchive
            ORDER BY EventTimestampUnix, EventKey";

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
                SourceFile = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return events;
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

        await using var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                SELECT SourceProfile, COUNT(*)
                FROM QuestEventArchive
                GROUP BY SourceProfile";
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

        var databaseSize = File.Exists(UserDataDbService.Instance.DatabasePath)
            ? new FileInfo(UserDataDbService.Instance.DatabasePath).Length
            : 0;
        CachedStats = new QuestLogArchiveStats(
            total,
            counts.GetValueOrDefault(LogProfileKind.Pvp),
            counts.GetValueOrDefault(LogProfileKind.Pve),
            counts.GetValueOrDefault(LogProfileKind.SeasonalPvp),
            trackedFiles,
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
    long UserDatabaseSizeBytes)
{
    public static QuestLogArchiveStats Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public sealed record QuestLogFolderArchiveResult(
    int FilesFound,
    int FilesScanned,
    int FilesSkippedUnchanged,
    int EventsFound,
    int EventsAdded,
    int DuplicateEvents,
    int EventsSkippedUnknownProfile);
