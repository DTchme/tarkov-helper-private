using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

/// <summary>
/// Keeps the current 1.1 Metal Birds quest attached to the stable game identity
/// that was previously published as Lend-Lease - Part 1. The English Wiki is
/// authoritative for the current name/trader/objectives, while the historical
/// BSG ID remains authoritative for game-log synchronization.
/// </summary>
public static class MetalBirdsQuestPolicy
{
    public const string GameQuestId = "5b4794cb86f774598100d5d4";
    public const string CanonicalQuestId =
        "aHR0cHM6Ly9lc2NhcGVmcm9tdGFya292LmZhbmRvbS5jb20vd2lraS9MZW5kLUxlYXNlXy1fUGFydF8x";
    public const string DuplicateWikiQuestId = "fandom_a7f60f33c8964ff952414645";
    public const string CurrentName = "Metal Birds";
    public const string CurrentNormalizedName = "metal-birds";

    private const string MotorItemId =
        "aHR0cHM6Ly9lc2NhcGVmcm9tdGFya292LmZhbmRvbS5jb20vd2lraS9Nb3Rvcl9Db250cm9sbGVyXzE";
    private const string GyroscopeItemId =
        "aHR0cHM6Ly9lc2NhcGVmcm9tdGFya292LmZhbmRvbS5jb20vd2lraS9TaW5nbGUtYXhpc19GaWJlcl9PcHRpY19HeXJvc2NvcGVfMQ";

    private const string MotorAcquireObjectiveId = "0BlpeIPS1xfwFMYIHimXQ0";
    private const string MotorHandOverObjectiveId = "a0mhb9rAKa2H2W7MmhaD9N";
    private const string GyroscopeAcquireObjectiveId = "H3DhLmGBJGOcxwBZu5UFae";
    private const string GyroscopeHandOverObjectiveId = "z9FAJ4YjFJ2Zx_BdGdWSI3";

    private const string MotorRequiredItemId = "bxNhjSHR5AxROEQK-6HeMc";
    private const string GyroscopeRequiredItemId = "SmSKpP-ERcY1neVJ_S50Er";

    private const string DefaultMotorLocation =
        "[{\"X\":230.64030000000002,\"Y\":1,\"Z\":-70.0657000000001,\"FloorId\":null}]";
    private const string DefaultGyroscopeLocation =
        "[{\"X\":56.00770000000011,\"Y\":1,\"Z\":-51.79919999999993,\"FloorId\":null}]";

    private static readonly IReadOnlyDictionary<string, string> ObjectiveIdMigrations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fandomobj_770a194e667df32c86d1ce7f"] = MotorAcquireObjectiveId,
            ["fandomobj_c7339a8bd7e22b267478dffe"] = MotorHandOverObjectiveId,
            ["fandomobj_20aaf31bfe5af7cbd7bbf09"] = GyroscopeAcquireObjectiveId
        };

    /// <summary>
    /// Applies the current quest identity and objective set to tarkov_data.db.
    /// Safe to call after either API or Wiki refresh; repeated calls are idempotent.
    /// </summary>
    public static async Task<int> ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        // The Wiki refresh normally creates these tables first, but the API
        // refresh also calls this policy. Keep the policy safe for databases
        // created before Wiki metadata support was introduced.
        await EnsureMetadataTablesAsync(connection, transaction, cancellationToken);

        var canonicalQuestId = await FindCanonicalQuestIdAsync(
            connection,
            transaction,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(canonicalQuestId))
            return 0;

        var motorLocation = await LoadLocationAsync(
            connection,
            transaction,
            canonicalQuestId,
            "Motor Controller%",
            DefaultMotorLocation,
            cancellationToken);
        var gyroscopeLocation = await LoadLocationAsync(
            connection,
            transaction,
            canonicalQuestId,
            "Single-axis Fiber Optic Gyroscope%",
            DefaultGyroscopeLocation,
            cancellationToken);

        var duplicateIds = await LoadDuplicateQuestIdsAsync(
            connection,
            transaction,
            canonicalQuestId,
            cancellationToken);

        foreach (var duplicateId in duplicateIds)
        {
            await RepointQuestReferencesAsync(
                connection,
                transaction,
                duplicateId,
                canonicalQuestId,
                cancellationToken);
        }

        var updatedAt = DateTime.UtcNow.ToString("o");
        await ExecuteAsync(
            connection,
            transaction,
            @"UPDATE Quests
              SET Name=@name,
                  NameEN=@name,
                  NameKO=NULL,
                  NameJA=NULL,
                  Trader='Peacekeeper',
                  Location='Woods',
                  WikiPageLink='https://escapefromtarkov.fandom.com/wiki/Metal_Birds',
                  KappaRequired=0,
                  UpdatedAt=@updatedAt
              WHERE Id=@questId",
            cancellationToken,
            ("@name", CurrentName),
            ("@updatedAt", updatedAt),
            ("@questId", canonicalQuestId));

        if (await ColumnExistsAsync(connection, transaction, "Quests", "NormalizedName", cancellationToken))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE Quests SET NormalizedName=@normalized WHERE Id=@questId",
                cancellationToken,
                ("@normalized", CurrentNormalizedName),
                ("@questId", canonicalQuestId));
        }

        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM QuestObjectives WHERE QuestId=@questId",
            cancellationToken,
            ("@questId", canonicalQuestId));
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM QuestRequiredItems WHERE QuestId=@questId",
            cancellationToken,
            ("@questId", canonicalQuestId));

        await InsertObjectiveAsync(
            connection, transaction, MotorAcquireObjectiveId, canonicalQuestId, 0,
            "Collect", "삼림에서 모터 컨트롤러를 찾아 획득하십시오",
            MotorItemId, "Motor Controller 1", "Woods", motorLocation,
            updatedAt, cancellationToken);
        await InsertObjectiveAsync(
            connection, transaction, MotorHandOverObjectiveId, canonicalQuestId, 1,
            "HandOver", "획득한 모터 컨트롤러를 건네주십시오",
            MotorItemId, "Motor Controller 1", null, null,
            updatedAt, cancellationToken);
        await InsertObjectiveAsync(
            connection, transaction, GyroscopeAcquireObjectiveId, canonicalQuestId, 2,
            "Collect", "삼림에서 단축 광섬유 자이로스코프를 찾아 획득하십시오",
            GyroscopeItemId, "Single-axis Fiber Optic Gyroscope 1", "Woods", gyroscopeLocation,
            updatedAt, cancellationToken);
        await InsertObjectiveAsync(
            connection, transaction, GyroscopeHandOverObjectiveId, canonicalQuestId, 3,
            "HandOver", "획득한 단축 광섬유 자이로스코프를 건네주십시오",
            GyroscopeItemId, "Single-axis Fiber Optic Gyroscope 1", null, null,
            updatedAt, cancellationToken);

        await InsertRequiredItemAsync(
            connection, transaction, MotorRequiredItemId, canonicalQuestId,
            MotorItemId, "Motor Controller 1", 0, updatedAt, cancellationToken);
        await InsertRequiredItemAsync(
            connection, transaction, GyroscopeRequiredItemId, canonicalQuestId,
            GyroscopeItemId, "Single-axis Fiber Optic Gyroscope 1", 1, updatedAt, cancellationToken);

        foreach (var duplicateId in duplicateIds)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM WikiQuestMetadata WHERE QuestId=@duplicateId",
                cancellationToken,
                ("@duplicateId", duplicateId));
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM QuestIdentityAliases WHERE QuestId=@duplicateId",
                cancellationToken,
                ("@duplicateId", duplicateId));
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM Quests WHERE Id=@duplicateId",
                cancellationToken,
                ("@duplicateId", duplicateId));
        }

        await ExecuteAsync(
            connection,
            transaction,
            @"INSERT INTO QuestIdentityAliases (QuestId, GameQuestId, Source, UpdatedAt)
              VALUES (@questId, @gameQuestId, 'metal-birds-1.1-rename', @updatedAt)
              ON CONFLICT(QuestId, GameQuestId) DO UPDATE SET
                  Source=excluded.Source,
                  UpdatedAt=excluded.UpdatedAt",
            cancellationToken,
            ("@questId", canonicalQuestId),
            ("@gameQuestId", GameQuestId),
            ("@updatedAt", updatedAt));

        return 1 + duplicateIds.Count;
    }

    private static Task<int> EnsureMetadataTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            @"CREATE TABLE IF NOT EXISTS WikiQuestMetadata (
                  QuestId TEXT PRIMARY KEY,
                  RequirementNotes TEXT,
                  HasUnverifiedRequirements INTEGER NOT NULL DEFAULT 0,
                  UpdatedAt TEXT NOT NULL
              );

              CREATE TABLE IF NOT EXISTS QuestIdentityAliases (
                  QuestId TEXT NOT NULL,
                  GameQuestId TEXT NOT NULL,
                  Source TEXT NOT NULL DEFAULT 'release',
                  UpdatedAt TEXT NOT NULL,
                  PRIMARY KEY (QuestId, GameQuestId)
              );

              CREATE INDEX IF NOT EXISTS IX_QuestIdentityAliases_GameQuestId
                  ON QuestIdentityAliases(GameQuestId);",
            cancellationToken);

    /// <summary>
    /// Moves progress saved against the duplicate Wiki-only row to the canonical
    /// game-backed quest without losing a completed state or objective checkmarks.
    /// </summary>
    public static async Task<int> MigrateUserProgressAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var rows = new List<ProgressRow>();
            await using (var select = new SqliteCommand(@"
                SELECT Id, ProfileType, NormalizedName, Status, UpdatedAt
                FROM QuestProgress
                WHERE Id IN (@canonicalId, @duplicateId, @gameQuestId)", connection, transaction))
            {
                select.Parameters.AddWithValue("@canonicalId", CanonicalQuestId);
                select.Parameters.AddWithValue("@duplicateId", DuplicateWikiQuestId);
                select.Parameters.AddWithValue("@gameQuestId", GameQuestId);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new ProgressRow(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4)));
                }
            }

            var changed = 0;
            foreach (var profileRows in rows.GroupBy(row => row.ProfileType))
            {
                var selected = profileRows
                    .OrderByDescending(row => row.Status.Equals("Done", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(row => ParseUpdatedAt(row.UpdatedAt))
                    .First();

                await ExecuteAsync(
                    connection,
                    transaction,
                    @"INSERT INTO QuestProgress (Id, ProfileType, NormalizedName, Status, UpdatedAt)
                      VALUES (@id, @profileType, @normalizedName, @status, @updatedAt)
                      ON CONFLICT(Id, ProfileType) DO UPDATE SET
                          NormalizedName=excluded.NormalizedName,
                          Status=excluded.Status,
                          UpdatedAt=excluded.UpdatedAt",
                    cancellationToken,
                    ("@id", CanonicalQuestId),
                    ("@profileType", profileRows.Key),
                    ("@normalizedName", CurrentNormalizedName),
                    ("@status", selected.Status),
                    ("@updatedAt", selected.UpdatedAt));
                changed++;
            }

            await ExecuteAsync(
                connection,
                transaction,
                @"DELETE FROM QuestProgress
                  WHERE Id IN (@duplicateId, @gameQuestId)",
                cancellationToken,
                ("@duplicateId", DuplicateWikiQuestId),
                ("@gameQuestId", GameQuestId));

            await ExecuteAsync(
                connection,
                transaction,
                @"UPDATE ObjectiveProgress
                  SET QuestId=@canonicalId
                  WHERE QuestId IN (@duplicateId, @gameQuestId)",
                cancellationToken,
                ("@canonicalId", CanonicalQuestId),
                ("@duplicateId", DuplicateWikiQuestId),
                ("@gameQuestId", GameQuestId));

            foreach (var mapping in ObjectiveIdMigrations)
            {
                changed += await MigrateObjectiveProgressAsync(
                    connection,
                    transaction,
                    mapping.Key,
                    mapping.Value,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return changed;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<string?> FindCanonicalQuestIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
            SELECT Id
            FROM Quests
            WHERE BsgId=@bsgId OR Id=@canonicalId
            ORDER BY CASE WHEN BsgId=@bsgId THEN 0 ELSE 1 END
            LIMIT 1", connection, transaction);
        command.Parameters.AddWithValue("@bsgId", GameQuestId);
        command.Parameters.AddWithValue("@canonicalId", CanonicalQuestId);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<List<string>> LoadDuplicateQuestIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string canonicalQuestId,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await using var command = new SqliteCommand(@"
            SELECT Id
            FROM Quests
            WHERE Id<>@canonicalId
              AND (Id=@duplicateId OR
                   (lower(COALESCE(NULLIF(NameEN, ''), Name))='metal birds' AND
                    (BsgId IS NULL OR BsgId='')))", connection, transaction);
        command.Parameters.AddWithValue("@canonicalId", canonicalQuestId);
        command.Parameters.AddWithValue("@duplicateId", DuplicateWikiQuestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task RepointQuestReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string duplicateId,
        string canonicalQuestId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection, transaction,
            "UPDATE QuestRequirements SET QuestId=@canonicalId WHERE QuestId=@duplicateId",
            cancellationToken,
            ("@canonicalId", canonicalQuestId), ("@duplicateId", duplicateId));
        await ExecuteAsync(
            connection, transaction,
            "UPDATE QuestRequirements SET RequiredQuestId=@canonicalId WHERE RequiredQuestId=@duplicateId",
            cancellationToken,
            ("@canonicalId", canonicalQuestId), ("@duplicateId", duplicateId));
        await ExecuteAsync(
            connection, transaction,
            "UPDATE OptionalQuests SET QuestId=@canonicalId WHERE QuestId=@duplicateId",
            cancellationToken,
            ("@canonicalId", canonicalQuestId), ("@duplicateId", duplicateId));
        await ExecuteAsync(
            connection, transaction,
            "UPDATE OptionalQuests SET AlternativeQuestId=@canonicalId WHERE AlternativeQuestId=@duplicateId",
            cancellationToken,
            ("@canonicalId", canonicalQuestId), ("@duplicateId", duplicateId));
    }

    private static async Task<LocationMetadata> LoadLocationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string questId,
        string itemNamePattern,
        string fallbackPoints,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
            SELECT LocationName, LocationPoints, OptionalPoints
            FROM QuestObjectives
            WHERE QuestId=@questId
              AND ItemName LIKE @itemName
              AND (MapName='Woods' OR MapName LIKE '%Woods%')
              AND LocationPoints IS NOT NULL
              AND LocationPoints<>''
            ORDER BY SortOrder
            LIMIT 1", connection, transaction);
        command.Parameters.AddWithValue("@questId", questId);
        command.Parameters.AddWithValue("@itemName", itemNamePattern);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new LocationMetadata(null, fallbackPoints, null);

        return new LocationMetadata(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? fallbackPoints : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task InsertObjectiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string questId,
        int sortOrder,
        string objectiveType,
        string description,
        string itemId,
        string itemName,
        string? mapName,
        LocationMetadata? location,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            @"INSERT INTO QuestObjectives (
                  Id, QuestId, SortOrder, ObjectiveType, Description,
                  ItemId, ItemName, RequiresFIR, MapName, LocationName,
                  LocationPoints, OptionalPoints, Conditions, IsApproved, UpdatedAt)
              VALUES (
                  @id, @questId, @sortOrder, @objectiveType, @description,
                  @itemId, @itemName, 0, @mapName, @locationName,
                  @locationPoints, @optionalPoints, @conditions, 1, @updatedAt)",
            cancellationToken,
            ("@id", id),
            ("@questId", questId),
            ("@sortOrder", sortOrder),
            ("@objectiveType", objectiveType),
            ("@description", description),
            ("@itemId", itemId),
            ("@itemName", itemName),
            ("@mapName", mapName),
            ("@locationName", location?.LocationName),
            ("@locationPoints", location?.LocationPoints),
            ("@optionalPoints", location?.OptionalPoints),
            ("@conditions", "{\"source\":\"metal-birds-1.1-policy\"}"),
            ("@updatedAt", updatedAt));
    }

    private static Task<int> InsertRequiredItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string questId,
        string itemId,
        string itemName,
        int sortOrder,
        string updatedAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            @"INSERT INTO QuestRequiredItems (
                  Id, QuestId, ItemId, ItemName, Count, RequiresFIR,
                  RequirementType, SortOrder, IsApproved, UpdatedAt)
              VALUES (
                  @id, @questId, @itemId, @itemName, 1, 0,
                  'Handover', @sortOrder, 1, @updatedAt)",
            cancellationToken,
            ("@id", id),
            ("@questId", questId),
            ("@itemId", itemId),
            ("@itemName", itemName),
            ("@sortOrder", sortOrder),
            ("@updatedAt", updatedAt));

    private static async Task<int> MigrateObjectiveProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string oldId,
        string newId,
        CancellationToken cancellationToken)
    {
        var rows = new List<ObjectiveProgressRow>();
        await using (var select = new SqliteCommand(@"
            SELECT ProfileType, IsCompleted, UpdatedAt
            FROM ObjectiveProgress
            WHERE Id=@oldId", connection, transaction))
        {
            select.Parameters.AddWithValue("@oldId", oldId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ObjectiveProgressRow(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2)));
            }
        }

        foreach (var row in rows)
        {
            await ExecuteAsync(
                connection,
                transaction,
                @"INSERT INTO ObjectiveProgress (
                      Id, ProfileType, QuestId, IsCompleted, UpdatedAt)
                  VALUES (@id, @profileType, @questId, @completed, @updatedAt)
                  ON CONFLICT(Id, ProfileType) DO UPDATE SET
                      QuestId=excluded.QuestId,
                      IsCompleted=MAX(ObjectiveProgress.IsCompleted, excluded.IsCompleted),
                      UpdatedAt=CASE
                          WHEN excluded.UpdatedAt>ObjectiveProgress.UpdatedAt THEN excluded.UpdatedAt
                          ELSE ObjectiveProgress.UpdatedAt
                      END",
                cancellationToken,
                ("@id", newId),
                ("@profileType", row.ProfileType),
                ("@questId", CanonicalQuestId),
                ("@completed", row.IsCompleted),
                ("@updatedAt", row.UpdatedAt));
        }

        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM ObjectiveProgress WHERE Id=@oldId",
            cancellationToken,
            ("@oldId", oldId));
        return rows.Count;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(
            $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=@column",
            connection,
            transaction);
        command.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset ParseUpdatedAt(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private sealed record LocationMetadata(
        string? LocationName,
        string LocationPoints,
        string? OptionalPoints);

    private sealed record ProgressRow(
        string Id,
        int ProfileType,
        string? NormalizedName,
        string Status,
        string UpdatedAt);

    private sealed record ObjectiveProgressRow(
        int ProfileType,
        int IsCompleted,
        string UpdatedAt);
}
