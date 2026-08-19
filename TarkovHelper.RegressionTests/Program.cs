using Microsoft.Data.Sqlite;
using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Models.Map;
using TarkovHelper.Services;
using TarkovHelper.Services.Map;

var failures = new List<string>();

if (args is ["--remove-arena-quests", var databasePath])
{
    using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
    connection.Open();
    using var transaction = connection.BeginTransaction();
    var removed = ArenaQuestExclusionPolicy.RemoveExcludedRowsAsync(connection, transaction)
        .GetAwaiter()
        .GetResult();
    transaction.Commit();
    using (var compact = connection.CreateCommand())
    {
        compact.CommandText = "PRAGMA optimize; VACUUM;";
        compact.ExecuteNonQuery();
    }
    Console.WriteLine($"Removed {removed} Arena-only quests from {databasePath}");
    return 0;
}

Run("startup initialization applies the complete database schema", () =>
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "TarkovHelperRegression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    AppEnv.ConfigPath = tempRoot;
    var databasePath = Path.Combine(tempRoot, "user_data.db");

    try
    {
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = @"
                CREATE TABLE QuestProgress (
                    Id TEXT PRIMARY KEY,
                    NormalizedName TEXT,
                    Status TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                )";
            create.ExecuteNonQuery();
        }

        UserDataDbService.Instance.EnsureInitialized();

        using var verified = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        verified.Open();
        Assert(TableExists(verified, "RaidHistory"), "RaidHistory must exist after startup initialization");
        Assert(TableExists(verified, "CustomMapMarkers"), "CustomMapMarkers must exist after startup initialization");
        Assert(File.Exists(databasePath + ".bak"), "startup must create a consistent database backup");
        verified.Dispose();

        var archive = QuestLogArchiveService.Instance;
        archive.InitializeAsync().GetAwaiter().GetResult();
        archive.ArchiveEventsAsync(new[]
        {
            new QuestLogEvent
            {
                QuestId = "old-wipe-quest",
                EventType = QuestEventType.Completed,
                SourceProfile = LogProfileKind.Pve,
                CharacterProfileId = "aaaaaaaaaaaaaaaaaaaaaaaa",
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010).LocalDateTime
            }
        }).GetAwaiter().GetResult();
        archive.ArchiveEventsAsync(new[]
        {
            new QuestLogEvent
            {
                QuestId = "new-wipe-quest",
                EventType = QuestEventType.Started,
                SourceProfile = LogProfileKind.Pve,
                CharacterProfileId = "bbbbbbbbbbbbbbbbbbbbbbbb",
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_011).LocalDateTime
            }
        }).GetAwaiter().GetResult();

        var currentEvents = archive.LoadEventsAsync(ProfileType.Pve).GetAwaiter().GetResult();
        Assert(currentEvents.Count == 1, "only the active character generation must be loaded");
        Assert(currentEvents[0].QuestId == "new-wipe-quest", "old-wipe events must stay archived but inactive");
        Assert(archive.CachedStats.InactiveGenerations == 1, "the previous generation must remain retained");

        archive.StartNewGenerationAsync(ProfileType.Pve).GetAwaiter().GetResult();
        currentEvents = archive.LoadEventsAsync(ProfileType.Pve).GetAwaiter().GetResult();
        Assert(currentEvents.Count == 0, "a manual new-wipe generation must start empty");

        var logFolder = Path.Combine(tempRoot, "Logs", "session-1");
        Directory.CreateDirectory(logFolder);
        File.WriteAllText(
            Path.Combine(logFolder, "application.log"),
            "SelectProfile ProfileId:cccccccccccccccccccccccc AccountId:12345\nSession mode: Pve\n");
        var notificationLog = Path.Combine(logFolder, "push-notifications.log");
        File.WriteAllText(
            notificationLog,
            "connected wsn-pve-live\n" + QuestJson("incremental-1", 10, 1_700_000_020) + "\n");

        var firstScan = LogSyncService.Instance
            .ArchiveExistingQuestLogsAsync(Path.Combine(tempRoot, "Logs"))
            .GetAwaiter().GetResult();
        Assert(firstScan.EventsAdded == 1, "the first scan must archive the initial event");

        File.AppendAllText(notificationLog, QuestJson("incremental-2", 12, 1_700_000_021) + "\n");
        var secondScan = LogSyncService.Instance
            .ArchiveExistingQuestLogsAsync(Path.Combine(tempRoot, "Logs"))
            .GetAwaiter().GetResult();
        Assert(secondScan.EventsAdded == 1, "an appended scan must archive only the new event");

        var unchangedScan = LogSyncService.Instance
            .ArchiveExistingQuestLogsAsync(Path.Combine(tempRoot, "Logs"))
            .GetAwaiter().GetResult();
        Assert(unchangedScan.FilesSkippedUnchanged == 1, "an unchanged file must be skipped by its cursor");
        var checkpoint = archive.GetFileCheckpointAsync(notificationLog).GetAwaiter().GetResult();
        Assert(checkpoint?.LastReadOffset == new FileInfo(notificationLog).Length, "the file cursor must reach EOF");

        currentEvents = archive.LoadEventsAsync(ProfileType.Pve).GetAwaiter().GetResult();
        Assert(currentEvents.Count == 2, "the active generation must contain both incremental events once");
        Assert(
            currentEvents.All(evt => evt.CharacterProfileId == "cccccccccccccccccccccccc"),
            "application-log profile IDs must be attached to archived events");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(tempRoot, recursive: true);
    }
});

Run("profile markers are applied in sequence", () =>
{
    var content = string.Join('\n',
        "connected wsn-pve-live",
        QuestJson("pve-quest", 10, 1_700_000_000),
        "connected wsn-pvp-live",
        QuestJson("pvp-quest", 12, 1_700_000_001));
    var parsed = LogSyncService.Instance.ParseLogContentBatch(
        content,
        "push-notifications.log",
        LogProfileKind.Unknown);

    Assert(parsed.Events.Count == 2, "expected two quest events");
    Assert(parsed.Events[0].SourceProfile == LogProfileKind.Pve, "first event must be PVE");
    Assert(parsed.Events[1].SourceProfile == LogProfileKind.Pvp, "second event must be PVP");
    Assert(parsed.FinalSourceProfile == LogProfileKind.Pvp, "final cursor profile must be PVP");
});

Run("Arena-only quests are excluded without hiding main-game Ref quests", () =>
{
    Assert(
        ArenaQuestExclusionPolicy.IsExcludedStoredQuest(
            "quest-arena",
            "bsg-arena",
            "Ref",
            "Arena",
            isApproved: true),
        "an explicitly Arena-located quest must be excluded");
    Assert(
        ArenaQuestExclusionPolicy.IsExcludedStoredQuest(
            "fandom_arena_business",
            null,
            "Ref",
            null,
            isApproved: false),
        "a Wiki-only Ref quest must be excluded as an Arena quest line");
    Assert(
        !ArenaQuestExclusionPolicy.IsExcludedStoredQuest(
            "easy-money-part-1",
            "6658a15615cbb1b876c4d754",
            "Ref",
            "Customs",
            isApproved: true),
        "a structured Ref quest completed in EFT must remain visible");
    Assert(
        !ArenaQuestExclusionPolicy.IsExcludedWikiQuest(
            "Ref",
            "Customs",
            hasStructuredQuest: true),
        "the Wiki may update an existing structured main-game Ref quest");
    Assert(
        ArenaQuestExclusionPolicy.IsArenaLocation("Customs; Arena"),
        "Arena must be recognized in a multi-location value");
});

Run("Arena database cleanup removes dependencies and preserves EFT Ref quests", () =>
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "TarkovHelperArenaRegression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    var databasePath = Path.Combine(tempRoot, "arena-cleanup.db");

    try
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = @"
                CREATE TABLE Quests (
                    Id TEXT PRIMARY KEY, BsgId TEXT, Name TEXT, NameEN TEXT,
                    Trader TEXT, Location TEXT, IsApproved INTEGER NOT NULL);
                CREATE TABLE QuestRequirements (QuestId TEXT, RequiredQuestId TEXT);
                CREATE TABLE QuestObjectives (QuestId TEXT);
                CREATE TABLE QuestRequiredItems (QuestId TEXT);
                CREATE TABLE OptionalQuests (QuestId TEXT, AlternativeQuestId TEXT);
                CREATE TABLE ApiMarkers (QuestBsgId TEXT, QuestNameEn TEXT);

                INSERT INTO Quests VALUES
                    ('arena', 'arena-bsg', 'Arena Task', 'Arena Task', 'Ref', 'Arena', 1),
                    ('fandom_arena', NULL, 'Arena Business', 'Arena Business', 'Ref', NULL, 0),
                    ('eft-ref', 'eft-bsg', 'Easy Money - Part 1', 'Easy Money - Part 1', 'Ref', 'Customs', 1),
                    ('after-arena', 'after-bsg', 'After Arena', 'After Arena', 'Prapor', 'Customs', 1);
                INSERT INTO QuestRequirements VALUES ('after-arena', 'arena'), ('eft-ref', 'after-arena');
                INSERT INTO QuestObjectives VALUES ('arena'), ('fandom_arena'), ('eft-ref');
                INSERT INTO QuestRequiredItems VALUES ('arena'), ('eft-ref');
                INSERT INTO OptionalQuests VALUES ('arena', 'eft-ref'), ('eft-ref', 'fandom_arena');
                INSERT INTO ApiMarkers VALUES ('arena-bsg', 'Arena Task'), ('eft-bsg', 'Easy Money - Part 1');";
            schema.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        var removed = ArenaQuestExclusionPolicy.RemoveExcludedRowsAsync(connection, transaction)
            .GetAwaiter()
            .GetResult();
        transaction.Commit();

        Assert(removed == 2, "both Arena quest representations must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM Quests WHERE Id='eft-ref'") == 1,
            "the main-game Ref quest must remain");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM QuestRequirements WHERE RequiredQuestId='arena'") == 0,
            "incoming prerequisite links to deleted Arena quests must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM QuestObjectives WHERE QuestId IN ('arena','fandom_arena')") == 0,
            "Arena objectives must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM QuestRequiredItems WHERE QuestId='arena'") == 0,
            "Arena required items must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM OptionalQuests") == 0,
            "both sides of optional Arena links must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM ApiMarkers WHERE QuestBsgId='arena-bsg'") == 0,
            "Arena API markers must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM ApiMarkers WHERE QuestBsgId='eft-bsg'") == 1,
            "main-game API markers must remain");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(tempRoot, recursive: true);
    }
});

Run("packaged quest database contains no Arena-only rows or orphaned links", () =>
{
    var databasePath = Path.GetFullPath(Path.Combine("TarkovHelper", "Assets", "tarkov_data.db"));
    Assert(File.Exists(databasePath), $"the packaged quest database must exist: {databasePath}");

    using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
    connection.Open();
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT Id, BsgId, Trader, Location, IsApproved FROM Quests";
        using var reader = command.ExecuteReader();
        var excludedNames = new List<string>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var bsgId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var trader = reader.IsDBNull(2) ? null : reader.GetString(2);
            var location = reader.IsDBNull(3) ? null : reader.GetString(3);
            var isApproved = !reader.IsDBNull(4) && reader.GetInt32(4) == 1;
            if (ArenaQuestExclusionPolicy.IsExcludedStoredQuest(
                    id,
                    bsgId,
                    trader,
                    location,
                    isApproved))
            {
                excludedNames.Add(id);
            }
        }

        Assert(excludedNames.Count == 0,
            $"the packaged database still contains Arena quests: {string.Join(", ", excludedNames)}");
    }

    Assert(ScalarCount(connection,
            "SELECT COUNT(*) FROM Quests WHERE NameEN='Provide Viewership' AND Trader='Ref' AND Location='Customs'") == 1,
        "the EFT Customs quest Provide Viewership must remain packaged");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM QuestRequirements requirement
            LEFT JOIN Quests quest ON quest.Id=requirement.QuestId
            LEFT JOIN Quests required ON required.Id=requirement.RequiredQuestId
            WHERE quest.Id IS NULL OR required.Id IS NULL") == 0,
        "quest prerequisites must not reference deleted Arena quests");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM OptionalQuests optionalQuest
            LEFT JOIN Quests quest ON quest.Id=optionalQuest.QuestId
            LEFT JOIN Quests alternative ON alternative.Id=optionalQuest.AlternativeQuestId
            WHERE quest.Id IS NULL OR alternative.Id IS NULL") == 0,
        "optional quest links must not reference deleted Arena quests");
});

Run("incomplete JSON is carried into the next chunk", () =>
{
    var json = QuestJson("chunked-quest", 10, 1_700_000_002);
    var splitAt = json.Length / 2;
    var first = LogSyncService.Instance.ParseLogContentBatch(
        "connected wsn-pve-live\n" + json[..splitAt],
        "push-notifications.log",
        LogProfileKind.Unknown);
    Assert(first.Events.Count == 0, "partial JSON must not emit an event");
    Assert(first.PendingText.Length > 0, "partial JSON must be retained");

    var second = LogSyncService.Instance.ParseLogContentBatch(
        first.PendingText + json[splitAt..],
        "push-notifications.log",
        first.FinalSourceProfile);
    Assert(second.Events.Count == 1, "completed JSON must emit one event");
    Assert(second.Events[0].QuestId == "chunked-quest", "quest ID must survive chunking");
    Assert(second.Events[0].SourceProfile == LogProfileKind.Pve, "profile cursor must survive chunking");
});

Run("archive keys isolate profile families", () =>
{
    var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_003).LocalDateTime;
    var pve = new QuestLogEvent
    {
        QuestId = "same-quest",
        EventType = QuestEventType.Completed,
        SourceProfile = LogProfileKind.Pve,
        Timestamp = timestamp
    };
    var pvp = new QuestLogEvent
    {
        QuestId = "same-quest",
        EventType = QuestEventType.Completed,
        SourceProfile = LogProfileKind.Pvp,
        Timestamp = timestamp
    };

    Assert(
        QuestLogArchiveService.GetEventKey(pve) != QuestLogArchiveService.GetEventKey(pvp),
        "PVE and PVP events must not share an archive key");
});

Run("current EFT screenshot filenames expose coordinates", () =>
{
    var parser = new ScreenshotCoordinateParser();
    var parsed = parser.TryParse(
        "2026-08-13[19-57]_-169.17, 6.19, -475.66_0.08716, 0.80955, -0.12443, 0.56705_19.68 (0).png",
        out var position);

    Assert(parsed, "the current EFT screenshot filename format must be recognized");
    Assert(position != null, "a recognized screenshot must return coordinates");
    Assert(Math.Abs(position!.X - (-169.17)) < 0.001, "the screenshot X coordinate must be preserved");
    Assert(Math.Abs(position.Y - 6.19) < 0.001, "the screenshot height coordinate must be preserved");
    Assert(Math.Abs((position.Z ?? 0) - (-475.66)) < 0.001, "the screenshot Z coordinate must be preserved");
    Assert(position.Angle.HasValue, "the screenshot quaternion must produce a facing angle");
});

Run("screenshot watcher emits a parsed position for a new EFT screenshot", () =>
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "TarkovHelperScreenshotRegression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);

    try
    {
        var detected = new TaskCompletionSource<TarkovHelper.Models.Map.EftPosition>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new ScreenshotWatcherService(new ScreenshotCoordinateParser())
        {
            DebounceDelayMs = 50
        };
        watcher.PositionDetected += (_, args) => detected.TrySetResult(args.Position);

        Assert(watcher.StartWatching(tempRoot), "the screenshot watcher must start for an existing folder");
        var fileName = "2026-08-13[19-57]_-169.17, 6.19, -475.66_0.08716, 0.80955, -0.12443, 0.56705_19.68 (0).png";
        File.WriteAllText(Path.Combine(tempRoot, fileName), "regression");

        var position = detected.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert(Math.Abs(position.X - (-169.17)) < 0.001, "the watcher must emit the parsed X coordinate");
        Assert(Math.Abs((position.Z ?? 0) - (-475.66)) < 0.001, "the watcher must emit the parsed Z coordinate");
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
});

Run("multi-map and all-map kill objectives apply without map markers", () =>
{
    var labs = new MapConfig
    {
        Key = "Labs",
        DisplayName = "The Lab",
        Aliases = new List<string> { "labs", "lab" }
    };
    var streets = new MapConfig
    {
        Key = "StreetsOfTarkov",
        DisplayName = "Streets of Tarkov",
        Aliases = new List<string> { "streets" }
    };

    Assert(labs.MatchesMapExpression("Reserve, The Lab"), "a multi-map expression must match The Lab");
    Assert(streets.MatchesMapExpression("Streets of Tarkov, Interchange"), "spaced map names must match map keys");
    Assert(labs.MatchesMapExpression("Any"), "Any must match every map");

    var listOnlyKill = new TaskObjectiveWithLocation
    {
        ObjectiveId = "kill-on-labs",
        Type = "kill",
        ApplicableMapNames = new List<string> { "The Lab", "Reserve" }
    };
    Assert(listOnlyKill.Locations.Count == 0, "a list-only kill objective must not invent a marker location");
    Assert(listOnlyKill.AppliesToMap("Labs", labs), "a list-only kill objective must appear on its matching map");
    Assert(!listOnlyKill.AppliesToMap("StreetsOfTarkov", streets), "a list-only kill objective must stay off unrelated maps");

    listOnlyKill.AppliesToAllMaps = true;
    Assert(listOnlyKill.AppliesToMap("StreetsOfTarkov", streets), "an all-map kill objective must appear on every map");
});

Run("screenshot cleanup preview only selects safe top-level PNG files", () =>
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "TarkovHelperCleanupRegression", Guid.NewGuid().ToString("N"));
    var screenshotFolder = Path.Combine(tempRoot, "Screenshots");
    var nestedFolder = Path.Combine(screenshotFolder, "nested");
    Directory.CreateDirectory(nestedFolder);

    try
    {
        var oldPng = Path.Combine(screenshotFolder, "old.png");
        var uppercasePng = Path.Combine(screenshotFolder, "older.PNG");
        var recentPng = Path.Combine(screenshotFolder, "recent.png");
        File.WriteAllText(oldPng, "old");
        File.WriteAllText(uppercasePng, "older");
        File.WriteAllText(recentPng, "recent");
        File.WriteAllText(Path.Combine(screenshotFolder, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(nestedFolder, "keep.png"), "keep");

        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(oldPng, now.AddMinutes(-2));
        File.SetLastWriteTimeUtc(uppercasePng, now.AddMinutes(-1));
        File.SetLastWriteTimeUtc(recentPng, now);

        var valid = ScreenshotFolderCleanupService.TryCreatePreview(
            screenshotFolder,
            out var preview,
            out var error,
            now);

        Assert(valid, $"a valid Screenshots folder must be accepted: {error}");
        Assert(preview != null, "cleanup preview must be returned");
        Assert(preview!.Files.Count == 2, "only old top-level PNG files must be selected");
        Assert(preview.SkippedRecentCount == 1, "a recently written PNG must be protected");
        Assert(preview.Files.All(path => Path.GetDirectoryName(path) == screenshotFolder), "nested files must never be selected");

        Assert(
            !ScreenshotFolderCleanupService.TryCreatePreview(tempRoot, out _, out _, now),
            "a folder not named Screenshots must be rejected");
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
});

Run("user database read and write connections use isolated caches", () =>
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "TarkovHelperSqliteRegression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    var databasePath = Path.Combine(tempRoot, "user_data.db");
    var readOnly = new SqliteConnectionStringBuilder(
        UserDataDbService.BuildApplicationConnectionString(databasePath, readOnly: true));
    var writable = new SqliteConnectionStringBuilder(
        UserDataDbService.BuildApplicationConnectionString(databasePath));

    Assert(readOnly.Mode == SqliteOpenMode.ReadOnly, "read connections must remain read-only");
    Assert(writable.Mode == SqliteOpenMode.ReadWriteCreate, "archive connections must remain writable");
    Assert(readOnly.Cache == SqliteCacheMode.Private, "read connections must use a private cache");
    Assert(writable.Cache == SqliteCacheMode.Private, "write connections must use a private cache");

    try
    {
        using (var create = new SqliteConnection(writable.ConnectionString))
        {
            create.Open();
            using var command = create.CreateCommand();
            command.CommandText = "CREATE TABLE ArchiveProbe (Id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
        }

        using (var read = new SqliteConnection(readOnly.ConnectionString))
        {
            read.Open();
            using var command = read.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ArchiveProbe";
            _ = command.ExecuteScalar();
        }

        using (var write = new SqliteConnection(writable.ConnectionString))
        {
            write.Open();
            using var command = write.CreateCommand();
            command.CommandText = "INSERT INTO ArchiveProbe DEFAULT VALUES";
            command.ExecuteNonQuery();
        }
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(tempRoot, recursive: true);
    }
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Regression checks failed ({failures.Count}):");
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine("All TarkovHelper regression checks passed.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static bool TableExists(SqliteConnection connection, string tableName)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
    command.Parameters.AddWithValue("@name", tableName);
    return Convert.ToInt32(command.ExecuteScalar()) == 1;
}

static int ScalarCount(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt32(command.ExecuteScalar());
}

static string QuestJson(string questId, int messageType, long timestamp)
{
    return $$"""
        {
          "type": "new_message",
          "dialogId": "test-trader",
          "message": {
            "type": {{messageType}},
            "templateId": "{{questId}}",
            "dt": {{timestamp}}
          }
        }
        """;
}
