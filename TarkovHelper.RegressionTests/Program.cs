using Microsoft.Data.Sqlite;
using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Models.Map;
using TarkovHelper.Services;
using TarkovHelper.Services.Map;

var failures = new List<string>();

if (args.Length == 2 &&
    args[0].Equals("--validate-static-feed", StringComparison.OrdinalIgnoreCase))
{
    var directory = args[1];
    string Read(string name) => File.ReadAllText(Path.Combine(directory, name + ".json"));
    var adapted = TarkovDevStaticTaskAdapter.Adapt(
        Read("tasks"), Read("tasks_en"), Read("tasks_ko"), Read("tasks_ja"),
        Read("traders"), Read("traders_en"), Read("maps"), Read("maps_en"), Read("items_en"));
    using var document = System.Text.Json.JsonDocument.Parse(adapted);
    var tasks = document.RootElement.GetProperty("data").GetProperty("en");
    var supervisor = tasks.EnumerateArray().Single(task =>
        task.GetProperty("id").GetString() == "5ae449d986f774453a54a7e1");
    var objectiveCount = supervisor.GetProperty("objectives").GetArrayLength();
    if (objectiveCount != 6)
        throw new InvalidOperationException($"Expected 6 current Supervisor objectives, got {objectiveCount}.");
    Console.WriteLine(
        $"Validated {tasks.GetArrayLength()} live tasks; Supervisor has {objectiveCount} current objectives.");
    return 0;
}

if (args.Length == 3 &&
    args[0].Equals("--refresh-quest-data", StringComparison.OrdinalIgnoreCase))
{
    var profile = args[1].Equals("pve", StringComparison.OrdinalIgnoreCase)
        ? ProfileType.Pve
        : ProfileType.Pvp;
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var result = new QuestApiRefreshService(httpClient)
        .RefreshAsync(profile, args[2])
        .GetAwaiter()
        .GetResult();
    Console.WriteLine(
        $"Refreshed {result.QuestCount} quests and {result.ObjectiveCount} objectives " +
        $"from json.tarkov.dev ({result.GameMode}). Backup: {result.BackupPath}");
    return 0;
}

if (args.Length == 2 &&
    args[0].Equals("--refresh-wiki-quest-data", StringComparison.OrdinalIgnoreCase))
{
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    var result = new WikiQuestRefreshService(httpClient)
        .RefreshAsync(args[1])
        .GetAwaiter()
        .GetResult();
    Console.WriteLine(
        $"Refreshed {result.WikiQuestCount} Wiki quests from {result.IndividualPageCount} " +
        $"changed individual pages; added {result.AddedQuestCount}, replaced " +
        $"{result.ObjectivesFilledCount} objectives, {result.PrerequisiteCount} prerequisites, " +
        $"and {result.RequiredItemCount} required items. " +
        $"Backup: {result.BackupPath}");
    return 0;
}

if (args.Length == 2 &&
    args[0].Equals("--apply-v3-location-correction", StringComparison.OrdinalIgnoreCase))
{
    using var connection = new SqliteConnection($"Data Source={args[1]};Mode=ReadWrite");
    connection.Open();
    using var transaction = connection.BeginTransaction();
    var changed = V3FlashDriveLocationPolicy.ApplyAsync(connection, transaction)
        .GetAwaiter()
        .GetResult();
    transaction.Commit();
    Console.WriteLine($"Corrected {changed} Secure Flash drive V3 location objectives.");
    return 0;
}

if (args.Length == 2 &&
    args[0].Equals("--validate-wiki-lighthouse-data", StringComparison.OrdinalIgnoreCase))
{
    using var connection = new SqliteConnection($"Data Source={args[1]};Mode=ReadOnly");
    connection.Open();
    var expected = new Dictionary<string, (int Objectives, string? Previous, string Trader, string Location)>(StringComparer.OrdinalIgnoreCase)
    {
        ["Fog of War"] = (3, null, "Prapor", "Lighthouse"),
        ["Number Temporarily Unavailable"] = (3, "Fog of War", "Prapor", "Lighthouse"),
        ["All-Inclusive Support"] = (3, "Number Temporarily Unavailable", "Prapor", "Lighthouse"),
        ["Invasive Therapy"] = (5, null, "Therapist", "Lighthouse"),
        ["To the Light - The Other Side"] = (3, "To the Light - A Time to Gather Stones", "Mechanic", "Reserve"),
        ["To the Light - Getting Acquainted"] = (4, "To the Light - The Other Side", "Mechanic", "Lighthouse")
    };

    foreach (var item in expected)
    {
        using var quest = connection.CreateCommand();
        quest.CommandText = @"
            SELECT q.Id, q.Trader, q.Location,
                   (SELECT COUNT(*) FROM QuestObjectives o WHERE o.QuestId=q.Id)
            FROM Quests q
            WHERE lower(COALESCE(NULLIF(q.NameEN, ''), q.Name))=lower(@name)
            LIMIT 1";
        quest.Parameters.AddWithValue("@name", item.Key);
        using var reader = quest.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"Missing Wiki quest: {item.Key}");
        var questId = reader.GetString(0);
        var trader = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var location = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        var objectiveCount = reader.GetInt32(3);
        if (objectiveCount != item.Value.Objectives)
            throw new InvalidOperationException(
                $"{item.Key}: expected {item.Value.Objectives} objectives, got {objectiveCount}.");
        if (!trader.Equals(item.Value.Trader, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{item.Key}: expected trader {item.Value.Trader}, got {trader}.");
        if (!location.Contains(item.Value.Location, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{item.Key}: expected location containing {item.Value.Location}, got {location}.");

        if (item.Value.Previous != null)
        {
            using var prerequisite = connection.CreateCommand();
            prerequisite.CommandText = @"
                SELECT COUNT(*)
                FROM QuestRequirements r
                JOIN Quests p ON p.Id=r.RequiredQuestId
                WHERE r.QuestId=@id
                  AND lower(COALESCE(NULLIF(p.NameEN, ''), p.Name))=lower(@previous)
                  AND lower(r.RequirementType)='complete'";
            prerequisite.Parameters.AddWithValue("@id", questId);
            prerequisite.Parameters.AddWithValue("@previous", item.Value.Previous);
            if (Convert.ToInt32(prerequisite.ExecuteScalar()) != 1)
                throw new InvalidOperationException(
                    $"{item.Key}: missing prerequisite {item.Value.Previous}.");
        }

        Console.WriteLine($"Validated {item.Key}: {trader}, {location}, objectives={objectiveCount}");
    }

    var newQuestPages = new[]
    {
        "A Familiar Face...",
        "Pay the Fare!",
        "Price for Information",
        "In the Name of Humanity...",
        "To the Light - False Call",
        "To the Light - Trust but Verify",
        "To the Light - Clip Their Wings",
        "To the Light - Dangerous Ambitions",
        "To the Light - Fallen Bird",
        "To the Light - Bite the Dust",
        "To the Light - A time to Throw Stones",
        "To the Light - A Time to Gather Stones",
        "...for the Good of the Chosen"
    };
    foreach (var questName in newQuestPages)
    {
        using var present = connection.CreateCommand();
        present.CommandText = @"
            SELECT COUNT(*) FROM Quests
            WHERE lower(COALESCE(NULLIF(NameEN, ''), Name))=lower(@name)";
        present.Parameters.AddWithValue("@name", questName);
        if (Convert.ToInt32(present.ExecuteScalar()) != 1)
        {
            using var similar = connection.CreateCommand();
            similar.CommandText = @"
                SELECT group_concat(COALESCE(NULLIF(NameEN, ''), Name), ' | ')
                FROM Quests
                WHERE lower(COALESCE(NULLIF(NameEN, ''), Name)) LIKE '%throw%stones%'";
            var similarNames = similar.ExecuteScalar()?.ToString() ?? "none";
            throw new InvalidOperationException(
                $"Missing new English Wiki quest: {questName}. Similar rows: {similarNames}");
        }
    }

    using (var excluded = connection.CreateCommand())
    {
        excluded.CommandText = @"
            SELECT COUNT(*) FROM Quests
            WHERE lower(replace(replace(COALESCE(NULLIF(NameEN, ''), Name), ' ', ''), '-', ''))='swiftone'
               OR lower(COALESCE(Location, ''))='arena'";
        if (Convert.ToInt32(excluded.ExecuteScalar()) != 0)
            throw new InvalidOperationException("Excluded Swift One or Arena quests reappeared after Wiki refresh.");
    }

    using (var coordinates = connection.CreateCommand())
    {
        coordinates.CommandText = @"
            SELECT COUNT(*) FROM QuestObjectives
            WHERE NULLIF(LocationPoints, '') IS NOT NULL
               OR NULLIF(OptionalPoints, '') IS NOT NULL";
        var coordinateCount = Convert.ToInt32(coordinates.ExecuteScalar());
        if (coordinateCount == 0)
            throw new InvalidOperationException("Wiki refresh erased every supplemental map coordinate.");
        Console.WriteLine($"Preserved supplemental coordinates on {coordinateCount} objectives.");
    }

    Console.WriteLine("English Wiki Lighthouse quest validation passed.");
    return 0;
}

if (args.Length == 2 &&
    (args[0].Equals("--remove-excluded-quests", StringComparison.OrdinalIgnoreCase) ||
     args[0].Equals("--remove-arena-quests", StringComparison.OrdinalIgnoreCase)))
{
    var databasePath = args[1];
    using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
    connection.Open();
    using var transaction = connection.BeginTransaction();
    var removed = QuestExclusionPolicy.RemoveExcludedRowsAsync(connection, transaction)
        .GetAwaiter()
        .GetResult();
    transaction.Commit();
    using (var compact = connection.CreateCommand())
    {
        compact.CommandText = "PRAGMA optimize; VACUUM;";
        compact.ExecuteNonQuery();
    }
    Console.WriteLine($"Removed {removed} excluded quests from {databasePath}");
    return 0;
}

Run("V3 flash drive location policy targets acquisition objectives only", () =>
{
    Assert(V3FlashDriveLocationPolicy.IsTargetObjective(
            V3FlashDriveLocationPolicy.MakeAmendsBsgId,
            "Make Amends",
            "Collect",
            "Obtain the V3 flash drive on Lighthouse",
            "Secure Flash drive V3"),
        "the Make Amends acquisition objective must receive the current Wiki locations");
    Assert(!V3FlashDriveLocationPolicy.IsTargetObjective(
            V3FlashDriveLocationPolicy.MakeAmendsBsgId,
            "Make Amends",
            "HandOver",
            "Hand over the V3 flash drive",
            "Secure Flash drive V3"),
        "the V3 hand-over objective must not create duplicate map markers");
    Assert(!V3FlashDriveLocationPolicy.IsTargetObjective(
            null,
            "Unrelated Quest",
            "Collect",
            "Obtain a V3 flash drive",
            "Secure Flash drive V3"),
        "an unrelated quest must not receive Make Amends markers");
    using var points = System.Text.Json.JsonDocument.Parse(
        V3FlashDriveLocationPolicy.OptionalPointsJson);
    Assert(points.RootElement.GetArrayLength() == 2,
        "the current Wiki guide must produce one chalet marker and one tennis-court marker");
});

Run("supported static tarkov.dev feed preserves current Supervisor objectives", () =>
{
    const string taskId = "5ae449d986f774453a54a7e1";
    const string traderId = "5ac3b934156ae10c4430e83c";
    const string mapId = "5714dbc024597771384a510d";
    const string itemId = "5ad7247386f7747487619dc3";
    const string stashObjectiveId = "6a60aab8ada1a08abcaa3649";
    const string findObjectiveId = "5ae9e55886f77445315f662a";

    var adapted = TarkovDevStaticTaskAdapter.Adapt(
        $$$$$"""
        {"data":{"tasks":{"{{{{{taskId}}}}}":{"id":"{{{{{taskId}}}}}","name":"{{{{{taskId}}}}} name","trader":"{{{{{traderId}}}}}","map":"{{{{{mapId}}}}}","wikiLink":"https://escapefromtarkov.fandom.com/wiki/Supervisor","minPlayerLevel":0,"kappaRequired":false,"taskRequirements":[],"objectives":[{"id":"{{{{{stashObjectiveId}}}}}","description":"{{{{{stashObjectiveId}}}}}","type":"plantItem","count":1,"optional":false,"items":["{{{{{itemId}}}}}"],"maps":["{{{{{mapId}}}}}"]},{"id":"{{{{{findObjectiveId}}}}}","description":"{{{{{findObjectiveId}}}}}","type":"findItem","count":1,"optional":true,"items":["{{{{{itemId}}}}}"],"maps":[]}],"failConditions":[],"normalizedName":"supervisor","factionName":"Any"}}}}
        """,
        $$$$$"""{"data":{"{{{{{taskId}}}}} name":"Supervisor","{{{{{stashObjectiveId}}}}}":"Stash the Goshan cash register key at the BIZARRO store fitting rooms on Interchange","{{{{{findObjectiveId}}}}}":"Obtain the Goshan cash register key"}}""",
        $$$$$"""{"data":{"{{{{{taskId}}}}} name":"감독관"}}""",
        $$$$$"""{"data":{"{{{{{taskId}}}}} name":"監督者"}}""",
        $$$$$"""{"data":{"{{{{{traderId}}}}}":{"id":"{{{{{traderId}}}}}","name":"{{{{{traderId}}}}} Nickname","normalizedName":"ragman"}}}""",
        $$$$$"""{"data":{"{{{{{traderId}}}}} Nickname":"Ragman"}}""",
        $$$$$"""{"data":{"maps":{"{{{{{mapId}}}}}":{"id":"{{{{{mapId}}}}}","name":"{{{{{mapId}}}}} Name","normalizedName":"interchange"}}}}""",
        $$$$$"""{"data":{"{{{{{mapId}}}}} Name":"Interchange"}}""",
        $$$$$"""{"data":{"{{{{{itemId}}}}} Name":"Goshan cash register key"}}"""
    );

    using var document = System.Text.Json.JsonDocument.Parse(adapted);
    var data = document.RootElement.GetProperty("data");
    var task = data.GetProperty("en")[0];
    Assert(task.GetProperty("name").GetString() == "Supervisor", "Supervisor name must be translated");
    Assert(task.GetProperty("trader").GetProperty("name").GetString() == "Ragman", "trader ID must be resolved");
    Assert(task.GetProperty("map").GetProperty("name").GetString() == "Interchange", "map ID must be resolved");
    var objectives = task.GetProperty("objectives");
    Assert(objectives.GetArrayLength() == 2, "mandatory and optional objectives must both survive adaptation");
    Assert(objectives[0].GetProperty("description").GetString()!.Contains("BIZARRO"), "current stash objective must be translated");
    Assert(!objectives[0].GetProperty("optional").GetBoolean(), "Wiki-listed stash objective must be mandatory");
    Assert(objectives[1].GetProperty("optional").GetBoolean(), "key acquisition helper objective must stay optional");
    Assert(objectives[0].GetProperty("items")[0].GetProperty("name").GetString() == "Goshan cash register key", "item ID must be resolved");
    Assert(data.GetProperty("ko")[0].GetProperty("name").GetString() == "감독관", "Korean quest name must be retained");
});

Run("English Wiki metadata preserves OR prerequisites and uncertain requirements", () =>
{
    var prerequisites = WikiQuestMetadataParser.ParsePrerequisites(
        "[[First Path]]<br/>or<br/>[[Second Path]] and [[Shared Gate]]",
        "Current Quest");
    Assert(prerequisites.IsResolved, "plain linked prerequisites must be fully resolved");
    Assert(prerequisites.Prerequisites.Count == 3, "all prerequisite links must be retained");
    Assert(prerequisites.Prerequisites[0].GroupId > 0, "the first OR option must have a group ID");
    Assert(prerequisites.Prerequisites[0].GroupId == prerequisites.Prerequisites[1].GroupId,
        "adjacent OR options must share one group ID");
    Assert(prerequisites.Prerequisites[2].GroupId == 0,
        "an AND prerequisite must remain mandatory");

    var unresolved = WikiQuestMetadataParser.ParsePrerequisites("See the event requirements", "Current Quest");
    Assert(!unresolved.IsResolved, "unstructured requirement prose must not erase stored prerequisites");

    var requirements = WikiQuestMetadataParser.ParseRequirements(new[]
    {
        "Must reach PMC level 17",
        "Must reach Loyalty Level 3 with Therapist"
    });
    Assert(requirements.MinimumLevel == 17, "explicit PMC level must be parsed");
    Assert(requirements.UnverifiedRequirements.Count == 1 &&
           requirements.UnverifiedRequirements[0].Contains("Loyalty Level 3", StringComparison.OrdinalIgnoreCase),
        "non-level conditions must remain visible as unverified notes");
});

Run("English Wiki objectives produce complete item requirements", () =>
{
    var catalog = new[]
    {
        new WikiItemCatalogEntry("p22", "P22 stimulant injector"),
        new WikiItemCatalogEntry("xtg", "XTG-12 antidote injector"),
        new WikiItemCatalogEntry("obdolbos", "Obdolbos cocktail injector"),
        new WikiItemCatalogEntry("roubles", "Roubles"),
        new WikiItemCatalogEntry("v3", "Secure Flash drive V3"),
        new WikiItemCatalogEntry("svds", "SVDS 7.62x54R sniper rifle")
    };
    var items = WikiQuestMetadataParser.ParseRequiredItems(new[]
    {
        "Hand over 5 found in raid P22 stimulant injectors",
        "Hand over 5 found in raid XTG-12 antidote injectors",
        "Stash an Obdolbos cocktail injector in the first location",
        "Stash an Obdolbos cocktail injector in the second location",
        "Stash an Obdolbos cocktail injector in the third location",
        "Hand over 1,000,000 Roubles to Mechanic",
        "Obtain Secure Flash drive V3 on Lighthouse",
        "Hand over Secure Flash drive V3 to Mechanic",
        "Hand over the 4 found in raid SVDS 7.62x54R sniper rifles"
    }, catalog);

    Assert(items.Count == 6, "all Invasive Therapy and Make Amends item types must be resolved");
    Assert(items.Single(item => item.ItemId == "p22").Count == 5, "P22 count must be five");
    Assert(items.Single(item => item.ItemId == "p22").RequiresFir, "P22 must require FIR");
    Assert(items.Single(item => item.ItemId == "xtg").Count == 5, "XTG-12 count must be five");
    var obd = items.Single(item => item.ItemId == "obdolbos");
    Assert(obd.Count == 3 && obd.RequirementType == "Stash",
        "three individual Obdolbos stash goals must aggregate to three");
    Assert(items.Single(item => item.ItemId == "roubles").Count == 1_000_000,
        "Make Amends - Buyout must require one million roubles");
    var v3 = items.Single(item => item.ItemId == "v3");
    Assert(v3.Count == 1 && v3.RequiresFir,
        "the Make Amends V3 flash drive must be inferred from its obtain and hand-over objectives");
    var svds = items.Single(item => item.ItemId == "svds");
    Assert(svds.Count == 4 && svds.RequiresFir,
        "Make Amends - Equipment must require four FIR SVDS rifles");
    Assert(WikiQuestMetadataParser.ParseObjectiveCount("Eliminate 25 Raiders on Reserve") == 25,
        "explicit kill counts must be parsed");
    Assert(WikiQuestMetadataParser.ParseObjectiveCount("Obtain Secure Flash drive V3 on Lighthouse") == null,
        "the V3 model suffix must never be mistaken for an objective count");
});

Run("Wiki objective IDs recover from database-wide collisions", () =>
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "TarkovHelperWikiObjectiveRegression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    var databasePath = Path.Combine(tempRoot, "objective-collision.db");

    try
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = @"
                CREATE TABLE QuestObjectives (
                    Id TEXT PRIMARY KEY,
                    QuestId TEXT NOT NULL
                );
                INSERT INTO QuestObjectives (Id, QuestId)
                VALUES ('shared-objective-id', 'older-quest');";
            create.ExecuteNonQuery();
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = WikiQuestRefreshService.ResolveUniqueObjectiveIdAsync(
                connection,
                null,
                "shared-objective-id",
                "new-quest",
                0,
                "Locate the objective",
                usedIds)
            .GetAwaiter()
            .GetResult();

        Assert(resolved != "shared-objective-id",
            "an ID owned by another quest must receive a quest-scoped fallback");
        Assert(resolved.StartsWith("fandomobj_", StringComparison.Ordinal),
            "the fallback objective ID must use the Wiki objective namespace");
        Assert(usedIds.Contains(resolved),
            "the resolved ID must be reserved for the remainder of the quest refresh");

        var freeId = WikiQuestRefreshService.ResolveUniqueObjectiveIdAsync(
                connection,
                null,
                "free-objective-id",
                "new-quest",
                1,
                "Survive and extract",
                usedIds)
            .GetAwaiter()
            .GetResult();
        Assert(freeId == "free-objective-id",
            "an available existing objective ID must be preserved for progress continuity");
    }
    finally
    {
        try { Directory.Delete(tempRoot, recursive: true); } catch { }
    }
});

Run("quest list keeps log identity warnings in details only", () =>
{
    var questPage = File.ReadAllText(Path.Combine("TarkovHelper", "Pages", "QuestListPage.xaml"));
    Assert(!questPage.Contains("LOG?", StringComparison.Ordinal),
        "the quest list must not repeat the ambiguous LOG? badge");
    Assert(questPage.Contains("x:Name=\"LogSyncNotice\"", StringComparison.Ordinal),
        "the quest detail panel must retain a log synchronization notice");
    Assert(questPage.Contains("로그 자동 동기화 미확인", StringComparison.Ordinal),
        "the detail notice must use a clear Korean label");
});

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

Run("removed and Arena-only quests are excluded without hiding live EFT quests", () =>
{
    Assert(
        QuestExclusionPolicy.IsExcludedStoredQuest(
            "quest-arena",
            "bsg-arena",
            "Arena Task",
            "Ref",
            "Arena",
            isApproved: true),
        "an explicitly Arena-located quest must be excluded");
    Assert(
        QuestExclusionPolicy.IsExcludedStoredQuest(
            "fandom_arena_business",
            null,
            "Arena Business",
            "Ref",
            null,
            isApproved: false),
        "a Wiki-only Ref quest must be excluded as an Arena quest line");
    Assert(
        !QuestExclusionPolicy.IsExcludedStoredQuest(
            "easy-money-part-1",
            "6658a15615cbb1b876c4d754",
            "Easy Money - Part 1",
            "Ref",
            "Customs",
            isApproved: true),
        "a structured Ref quest completed in EFT must remain visible");
    Assert(
        !QuestExclusionPolicy.IsExcludedWikiQuest(
            "Easy Money - Part 1",
            "Ref",
            "Customs",
            hasStructuredQuest: true),
        "the Wiki may update an existing structured main-game Ref quest");
    Assert(
        QuestExclusionPolicy.IsArenaLocation("Customs; Arena"),
        "Arena must be recognized in a multi-location value");
    Assert(
        QuestExclusionPolicy.IsExplicitlyRemovedQuest("60e729cf5698ee7b05057439", "outdated label"),
        "Swift One must be excluded by its stable BSG id");
    Assert(
        QuestExclusionPolicy.IsExplicitlyRemovedQuest(null, "Swift One"),
        "a stale Wiki-only Swift One row must be excluded by exact normalized name");
    Assert(
        !QuestExclusionPolicy.IsExplicitlyRemovedQuest(null, "Swift Strike"),
        "similar live quest names must not be excluded");
    Assert(
        QuestExclusionPolicy.IsExcludedApiQuest(
            "60e729cf5698ee7b05057439",
            "Swift One",
            "Woods"),
        "a stale structured API payload must not re-add Swift One");
});

Run("excluded quest cleanup removes dependencies and preserves live EFT quests", () =>
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
                    ('swift-one', '60e729cf5698ee7b05057439', 'Swift One', 'Swift One', 'Jaeger', 'Woods', 1),
                    ('eft-ref', 'eft-bsg', 'Easy Money - Part 1', 'Easy Money - Part 1', 'Ref', 'Customs', 1),
                    ('after-arena', 'after-bsg', 'After Arena', 'After Arena', 'Prapor', 'Customs', 1),
                    ('after-swift', 'after-swift-bsg', 'After Swift', 'After Swift', 'Jaeger', 'Woods', 1);
                INSERT INTO QuestRequirements VALUES
                    ('after-arena', 'arena'), ('eft-ref', 'after-arena'), ('after-swift', 'swift-one');
                INSERT INTO QuestObjectives VALUES ('arena'), ('fandom_arena'), ('swift-one'), ('eft-ref');
                INSERT INTO QuestRequiredItems VALUES ('arena'), ('eft-ref');
                INSERT INTO OptionalQuests VALUES ('arena', 'eft-ref'), ('eft-ref', 'fandom_arena');
                INSERT INTO ApiMarkers VALUES
                    ('arena-bsg', 'Arena Task'),
                    ('60e729cf5698ee7b05057439', 'Swift One'),
                    ('eft-bsg', 'Easy Money - Part 1');";
            schema.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        var removed = QuestExclusionPolicy.RemoveExcludedRowsAsync(connection, transaction)
            .GetAwaiter()
            .GetResult();
        transaction.Commit();

        Assert(removed == 3, "both Arena representations and Swift One must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM Quests WHERE Id='eft-ref'") == 1,
            "the main-game Ref quest must remain");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM QuestRequirements WHERE RequiredQuestId='arena'") == 0,
            "incoming prerequisite links to deleted Arena quests must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM QuestRequirements WHERE RequiredQuestId='swift-one'") == 0,
            "incoming prerequisite links to deleted Swift One must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM QuestObjectives WHERE QuestId IN ('arena','fandom_arena','swift-one')") == 0,
            "excluded quest objectives must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM QuestRequiredItems WHERE QuestId='arena'") == 0,
            "Arena required items must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM OptionalQuests") == 0,
            "both sides of optional Arena links must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM ApiMarkers WHERE QuestBsgId='arena-bsg'") == 0,
            "Arena API markers must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM ApiMarkers WHERE QuestBsgId='60e729cf5698ee7b05057439'") == 0,
            "Swift One API markers must be removed");
        Assert(ScalarCount(connection, "SELECT COUNT(*) FROM ApiMarkers WHERE QuestBsgId='eft-bsg'") == 1,
            "main-game API markers must remain");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(tempRoot, recursive: true);
    }
});

Run("packaged quest database contains no excluded rows or orphaned links", () =>
{
    var databasePath = Path.GetFullPath(Path.Combine("TarkovHelper", "Assets", "tarkov_data.db"));
    Assert(File.Exists(databasePath), $"the packaged quest database must exist: {databasePath}");

    using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
    connection.Open();
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT Id, BsgId, COALESCE(NameEN, Name, ''), Trader, Location, IsApproved FROM Quests";
        using var reader = command.ExecuteReader();
        var excludedNames = new List<string>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var bsgId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var name = reader.GetString(2);
            var trader = reader.IsDBNull(3) ? null : reader.GetString(3);
            var location = reader.IsDBNull(4) ? null : reader.GetString(4);
            var isApproved = !reader.IsDBNull(5) && reader.GetInt32(5) == 1;
            if (QuestExclusionPolicy.IsExcludedStoredQuest(
                    id,
                    bsgId,
                    name,
                    trader,
                    location,
                    isApproved))
            {
                excludedNames.Add(id);
            }
        }

        Assert(excludedNames.Count == 0,
            $"the packaged database still contains excluded quests: {string.Join(", ", excludedNames)}");
    }

    Assert(ScalarCount(connection,
            "SELECT COUNT(*) FROM Quests WHERE NameEN='Provide Viewership' AND Trader='Ref' AND Location='Customs'") == 1,
        "the EFT Customs quest Provide Viewership must remain packaged");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId
            WHERE quest.BsgId='5ae449d986f774453a54a7e1'") == 3,
        "packaged Supervisor must contain exactly the three current Wiki stash goals");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId
            WHERE quest.BsgId='5ae449d986f774453a54a7e1'
              AND objective.ObjectiveType='Stash'
              AND objective.MapName='Interchange'") == 3,
        "the three Wiki-listed Supervisor goals must be Interchange stash objectives");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM QuestRequiredItems item
            JOIN Quests quest ON quest.Id=item.QuestId
            WHERE quest.BsgId='5ae449d986f774453a54a7e1'") == 3,
        "Supervisor must require exactly the three keys used by its Wiki objectives");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId
            WHERE quest.BsgId='5ae449d986f774453a54a7e1'
              AND objective.Description LIKE 'Hand over%'") == 0,
        "the obsolete Supervisor hand-over objective must not remain");
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

    Assert(TableExists(connection, "WikiQuestMetadata"),
        "the packaged database must include Wiki requirement metadata");
    Assert(TableExists(connection, "QuestIdentityAliases"),
        "the packaged database must support future game-log ID aliases");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM QuestRequiredItems item
            JOIN Quests quest ON quest.Id=item.QuestId
            WHERE quest.NameEN='Invasive Therapy'
              AND item.ItemName LIKE 'P22%'
              AND item.Count=5
              AND item.RequiresFIR=1
              AND item.RequirementType='Handover'") == 1,
        "Invasive Therapy must require five FIR P22 injectors");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM QuestRequiredItems item
            JOIN Quests quest ON quest.Id=item.QuestId
            WHERE quest.NameEN='Invasive Therapy'
              AND item.ItemName LIKE '%XTG-12%'
              AND item.Count=5
              AND item.RequiresFIR=1
              AND item.RequirementType='Handover'") == 1,
        "Invasive Therapy must require five FIR XTG-12 injectors");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM QuestRequiredItems item
            JOIN Quests quest ON quest.Id=item.QuestId
            WHERE quest.NameEN='Invasive Therapy'
              AND item.ItemName LIKE 'Obdolbos%'
              AND item.Count=3
              AND item.RequirementType='Stash'") == 1,
        "Invasive Therapy must require three Obdolbos stash items");
    Assert(ScalarCount(connection, "SELECT COUNT(*) FROM QuestRequirements WHERE GroupId > 0") > 0,
        "Wiki OR prerequisite groups must be preserved in the packaged database");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM WikiQuestMetadata metadata
            JOIN Quests quest ON quest.Id=metadata.QuestId
            WHERE quest.NameEN='Invasive Therapy'
              AND metadata.HasUnverifiedRequirements=1
              AND metadata.RequirementNotes LIKE '%Loyalty Level 3%Therapist%'") == 1,
        "Invasive Therapy must retain the Therapist loyalty requirement restored by the current English Wiki");

    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM Quests
            WHERE NameEN IN (
                'Make Amends', 'Make Amends - Buyout', 'Make Amends - Equipment',
                'Make Amends - Quarantine', 'Make Amends - Security',
                'Make Amends - Software', 'Make Amends - Sweep Up')") == 7,
        "the complete seven-quest Make Amends chain must be packaged");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId
            WHERE quest.NameEN='Make Amends'") == 4,
        "Make Amends must contain the four current Wiki objectives");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId
            WHERE quest.NameEN='Make Amends'
              AND objective.ObjectiveType='Collect'
              AND objective.Description LIKE '%V3%'
              AND objective.MapName='Lighthouse'
              AND objective.LocationPoints IS NULL
              AND json_array_length(objective.OptionalPoints)=2
              AND objective.LocationName LIKE '%샬레%테니스%' ") == 1,
        "Make Amends must use the two current Wiki V3 drive areas");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId
            JOIN json_each(objective.OptionalPoints) point
            WHERE quest.NameEN='Make Amends'
              AND objective.ObjectiveType='Collect'
              AND objective.Description LIKE '%V3%'
              AND (
                  (ABS(json_extract(point.value, '$.X') - (-123.6628770633024)) < 0.000001
                   AND ABS(json_extract(point.value, '$.Z') - 104.05403435728749) < 0.000001)
                  OR
                  (ABS(json_extract(point.value, '$.X') - (-71.172)) < 0.000001
                   AND ABS(json_extract(point.value, '$.Z') - 131.3706) < 0.000001)
              )") == 2,
        "Make Amends must mark the southern chalet and tennis-court tent");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*)
            FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId
            WHERE quest.NameEN IN (
                    'Getting Acquainted',
                    'Make Amends',
                    'To the Light - Getting Acquainted')
              AND objective.Description LIKE '%V3%'
              AND objective.ObjectiveType<>'HandOver'
              AND objective.LocationPoints IS NULL
              AND json_array_length(objective.OptionalPoints)=2") == 3,
        "all V3 drive acquisition quests must share the corrected Wiki locations");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM QuestRequiredItems item
            JOIN Quests quest ON quest.Id=item.QuestId
            WHERE quest.NameEN='Make Amends'
              AND item.ItemName='Secure Flash drive V3'
              AND item.Count=1
              AND item.RequiresFIR=1
              AND item.RequirementType='Handover'") == 1,
        "Make Amends must require exactly one FIR Secure Flash drive V3");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM QuestRequiredItems item
            JOIN Quests quest ON quest.Id=item.QuestId
            WHERE quest.NameEN='Make Amends'") == 1,
        "obsolete Make Amends key requirements must not remain");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM QuestRequiredItems item
            JOIN Quests quest ON quest.Id=item.QuestId
            WHERE quest.NameEN='Make Amends - Buyout'
              AND item.ItemName='Roubles'
              AND item.Count=1000000
              AND item.RequirementType='Handover'") == 1,
        "Make Amends - Buyout must require 1,000,000 RUB");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM QuestRequiredItems item
            JOIN Quests quest ON quest.Id=item.QuestId
            WHERE quest.NameEN='Make Amends - Equipment'
              AND item.ItemName LIKE 'SVDS%'
              AND item.Count=4
              AND item.RequiresFIR=1
              AND item.RequirementType='Handover'") == 1,
        "Make Amends - Equipment must require four FIR SVDS rifles");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM QuestRequiredItems item
            JOIN Quests quest ON quest.Id=item.QuestId
            WHERE quest.NameEN='Make Amends - Security'
              AND item.ItemName LIKE '%Camera%'
              AND item.Count=4
              AND item.RequirementType='Stash'") == 1,
        "Make Amends - Security must require four camera placements");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM QuestRequiredItems item
            JOIN Quests quest ON quest.Id=item.QuestId
            WHERE quest.NameEN='Make Amends - Software'
              AND item.ItemName='Secure Flash drive'
              AND item.Count=15
              AND item.RequiresFIR=1
              AND item.RequirementType='Handover'") == 1,
        "Make Amends - Software must require fifteen FIR Secure Flash drives");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId
            WHERE quest.NameEN='Make Amends - Sweep Up'
              AND objective.ObjectiveType='Kill'
              AND objective.TargetCount=25") == 1,
        "Make Amends - Sweep Up must require 25 Raider eliminations");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM Quests
            WHERE (NameEN='Make Amends - Buyout' AND RequiredDecodeCount=1)
               OR (NameEN='Make Amends - Security' AND RequiredDecodeCount=2)
               OR (NameEN='Make Amends - Software' AND RequiredDecodeCount=3)") == 3,
        "the three decoded transmitter branches must use decode counts 1, 2, and 3");
    Assert(ScalarCount(connection, @"
            SELECT COUNT(*) FROM QuestRequirements requirement
            JOIN Quests quest ON quest.Id=requirement.QuestId
            JOIN Quests required ON required.Id=requirement.RequiredQuestId
            WHERE quest.NameEN='Make Amends'
              AND required.NameEN IN (
                  'Make Amends - Equipment',
                  'Make Amends - Quarantine',
                  'Make Amends - Sweep Up')
              AND requirement.GroupId > 0") == 3,
        "Make Amends must retain all three alternative branch prerequisites");
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
