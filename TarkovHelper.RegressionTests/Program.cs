using Microsoft.Data.Sqlite;
using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Map;

var failures = new List<string>();

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
