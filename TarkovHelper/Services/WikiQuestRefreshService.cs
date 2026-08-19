using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Uses the official English Escape from Tarkov Fandom Wiki as the live quest source.
/// tarkov.dev remains the preferred structured source; the Wiki fills new or missing
/// quests/objectives and refreshes the Collector hand-in list.
/// </summary>
public sealed class WikiQuestRefreshService
{
    private const string FandomApi = "https://escapefromtarkov.fandom.com/api.php";
    private const string FandomPageBase = "https://escapefromtarkov.fandom.com/wiki/";
    private const int MinimumExpectedQuestCount = 250;
    private const int MinimumCollectorItemCount = 40;
    private const int MaximumCollectorItemCount = 70;

    private static readonly ILogger _log = Log.For<WikiQuestRefreshService>();
    private static readonly string[] TraderTableOrder =
    {
        "Prapor", "Therapist", "Fence", "Skier", "Peacekeeper", "Mechanic",
        "Ragman", "Jaeger", "Ref", "Lightkeeper", "BTR Driver"
    };

    private readonly HttpClient _httpClient;
    private readonly EnglishWikiQuestTranslator _translator = new();

    public WikiQuestRefreshService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<WikiQuestRefreshResult> RefreshAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("tarkov_data.db를 찾을 수 없습니다.", databasePath);

        var questsTask = FetchParsedPageAsync("Quests", cancellationToken);
        var storyTask = FetchParsedPageAsync("Story chapters", cancellationToken);
        await Task.WhenAll(questsTask, storyTask);

        var parsed = ParseQuestTables(questsTask.Result);
        var rows = parsed.Rows;
        rows.AddRange(ParseStoryChapterLinks(storyTask.Result));

        rows = rows
            .GroupBy(r => (NormalizeQuestName(r.Name), r.Trader), StringTupleComparer.Instance)
            .Select(g => g.OrderByDescending(x => x.Objectives.Count).First())
            .ToList();

        if (rows.Count < MinimumExpectedQuestCount)
        {
            throw new InvalidOperationException(
                $"Official Wiki quest count is unexpectedly low ({rows.Count}). The existing DB was kept.");
        }

        if (parsed.CollectorItems.Count is < MinimumCollectorItemCount or > MaximumCollectorItemCount)
        {
            throw new InvalidOperationException(
                $"Official Wiki Collector item count is invalid ({parsed.CollectorItems.Count}). The existing DB was kept.");
        }

        var databaseDirectory = Path.GetDirectoryName(databasePath) ?? AppDomain.CurrentDomain.BaseDirectory;
        var tempPath = Path.Combine(databaseDirectory, $"tarkov_data.fandom.{Guid.NewGuid():N}.tmp");
        File.Copy(databasePath, tempPath, true);

        try
        {
            var stats = await ApplyFandomOverlayAsync(
                tempPath,
                rows,
                parsed.CollectorItems,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var backupDirectory = Path.Combine(databaseDirectory, "Backups");
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(
                backupDirectory,
                $"tarkov_data_before_fandom_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");

            SqliteConnection.ClearAllPools();
            File.Copy(databasePath, backupPath, true);
            File.Move(tempPath, databasePath, true);
            SqliteConnection.ClearAllPools();

            _log.Info(
                $"Official Wiki overlay completed: wiki={rows.Count}, added={stats.Added}, " +
                $"updated={stats.Updated}, objectivesFilled={stats.ObjectivesFilled}, " +
                $"collectorItems={stats.CollectorItems}");

            return new WikiQuestRefreshResult(
                rows.Count,
                stats.Added,
                stats.Updated,
                stats.ObjectivesFilled,
                backupPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private async Task<FandomPageResponse> FetchParsedPageAsync(
        string page,
        CancellationToken cancellationToken)
    {
        var apiUrl = FandomApi +
            "?action=parse&prop=text&format=json&formatversion=2&page=" +
            Uri.EscapeDataString(page);

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.UserAgent.ParseAdd("TarkovHelper/1.5.10 (+official wiki sync)");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Official Wiki request failed for {page}: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("parse", out var parse) ||
            !parse.TryGetProperty("text", out var textElement))
        {
            throw new InvalidOperationException($"Official Wiki returned no parsed content for {page}.");
        }

        var title = parse.TryGetProperty("title", out var titleElement)
            ? titleElement.GetString() ?? page
            : page;
        var displayUrl = FandomPageBase + BuildWikiSlug(title);
        return new FandomPageResponse(title, displayUrl, textElement.GetString() ?? string.Empty);
    }

    private QuestParseResult ParseQuestTables(FandomPageResponse page)
    {
        var rows = new List<WikiQuestRow>();
        var collectorItems = new List<string>();

        for (var tableIndex = 1; tableIndex <= TraderTableOrder.Length; tableIndex++)
        {
            var tableMatch = Regex.Match(
                page.Html,
                $@"<table\b[^>]*id=[""']tpt-{tableIndex}[""'][^>]*>(?<table>.*?)</table>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!tableMatch.Success)
                continue;

            var trader = TraderTableOrder[tableIndex - 1];
            foreach (Match rowMatch in Regex.Matches(
                         tableMatch.Groups["table"].Value,
                         @"<tr\b[^>]*>(?<row>.*?)</tr>",
                         RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var cells = Regex.Matches(
                        rowMatch.Groups["row"].Value,
                        @"<t[dh]\b[^>]*>(?<cell>.*?)</t[dh]>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline)
                    .Cast<Match>()
                    .Select(m => m.Groups["cell"].Value)
                    .ToList();
                if (cells.Count < 3)
                    continue;

                var questLink = Regex.Match(
                    cells[1],
                    "<a\\b[^>]*href=[\"'](?<href>/wiki/[^\"']+)[\"'][^>]*>(?<name>.*?)</a>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!questLink.Success)
                    continue;

                var name = CleanHtml(questLink.Groups["name"].Value);
                if (string.IsNullOrWhiteSpace(name) || name.Equals("Quest", StringComparison.OrdinalIgnoreCase))
                    continue;

                var href = WebUtility.HtmlDecode(questLink.Groups["href"].Value);
                var wikiLink = new Uri(new Uri(FandomPageBase), href).ToString();
                var rawObjectives = ExtractListItems(cells[2]);

                if (name.Equals("Collector", StringComparison.OrdinalIgnoreCase))
                    collectorItems.AddRange(ExtractCollectorItems(rawObjectives));

                var objectives = rawObjectives
                    .Select(_translator.TranslateObjective)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var map = NormalizeMap(string.Join(" ", rawObjectives));

                rows.Add(new WikiQuestRow(name, trader, map, wikiLink, objectives));
            }
        }

        return new QuestParseResult(
            rows,
            collectorItems.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static IEnumerable<WikiQuestRow> ParseStoryChapterLinks(FandomPageResponse page)
    {
        var tableMatch = Regex.Match(
            page.Html,
            @"<table\b[^>]*class=[""'][^""']*table-progress-tracking[^""']*[""'][^>]*>(?<table>.*?)</table>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!tableMatch.Success)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match rowMatch in Regex.Matches(
                     tableMatch.Groups["table"].Value,
                     @"<tr\b[^>]*>(?<row>.*?)</tr>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var cells = Regex.Matches(
                    rowMatch.Groups["row"].Value,
                    @"<t[dh]\b[^>]*>(?<cell>.*?)</t[dh]>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Cast<Match>()
                .Select(m => m.Groups["cell"].Value)
                .ToList();
            if (cells.Count < 3)
                continue;

            var linkMatch = Regex.Match(
                cells[2],
                "<a\\b[^>]*href=[\"'](?<href>/wiki/[^\"']+)[\"'][^>]*>(?<name>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!linkMatch.Success)
                continue;

            var name = CleanHtml(linkMatch.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(name) || name.Equals("Chapter", StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
                continue;

            var href = WebUtility.HtmlDecode(linkMatch.Groups["href"].Value);
            var wikiLink = new Uri(new Uri(FandomPageBase), href).ToString();
            yield return new WikiQuestRow(name, "Story", string.Empty, wikiLink, new List<string>());
        }
    }

    private static List<string> ExtractCollectorItems(IEnumerable<string> objectives)
    {
        var result = new List<string>();
        foreach (var objective in objectives)
        {
            var match = Regex.Match(objective, @"^Find (?<item>.+?) in raid$", RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            var item = match.Groups["item"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(item))
                result.Add(item);
        }
        return result;
    }

    private static List<string> ExtractListItems(string html)
    {
        var result = new List<string>();
        foreach (Match match in Regex.Matches(
                     html,
                     @"<li\b[^>]*>(?<item>.*?)</li>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var text = CleanHtml(match.Groups["item"].Value);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (!result.Contains(text, StringComparer.OrdinalIgnoreCase))
                result.Add(text);
        }
        return result;
    }

    private static string CleanHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = Regex.Replace(html, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<script\b[^>]*>.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<style\b[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"\s+([,.;:!?])", "$1");
        text = Regex.Replace(text, @"\s+'s\b", "'s", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"([([{])\s+", "$1");
        return Regex.Replace(text, @"\s+([)\]}])", "$1");
    }

    private static string NormalizeMap(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var known = new[]
        {
            "Ground Zero", "Streets of Tarkov", "Interchange", "Customs", "Factory",
            "Woods", "Shoreline", "Reserve", "Lighthouse", "The Labyrinth", "The Lab",
            "Terminal", "Icebreaker", "Arena"
        };
        return string.Join(", ", known.Where(map => text.Contains(map, StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<OverlayStats> ApplyFandomOverlayAsync(
        string databasePath,
        IReadOnlyList<WikiQuestRow> rows,
        IReadOnlyList<string> collectorItems,
        CancellationToken cancellationToken)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        }.ToString();

        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var removedArenaQuestCount = await ArenaQuestExclusionPolicy.RemoveExcludedRowsAsync(
                connection,
                tx,
                cancellationToken);
            if (removedArenaQuestCount > 0)
                _log.Info($"Removed {removedArenaQuestCount} Arena-only quests before Wiki overlay");

            var existing = new Dictionary<string, ExistingQuest>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new SqliteCommand(
                             "SELECT Id, NameEN, Name, BsgId, IsApproved, Trader, Location FROM Quests",
                             connection,
                             tx))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetString(0);
                    var name = !reader.IsDBNull(1) ? reader.GetString(1) : reader.GetString(2);
                    var bsgId = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var isApproved = !reader.IsDBNull(4) && reader.GetInt32(4) == 1;
                    var trader = reader.IsDBNull(5) ? null : reader.GetString(5);
                    var location = reader.IsDBNull(6) ? null : reader.GetString(6);
                    if (ArenaQuestExclusionPolicy.IsExcludedStoredQuest(
                            id,
                            bsgId,
                            trader,
                            location,
                            isApproved))
                    {
                        continue;
                    }

                    var key = NormalizeQuestName(name);
                    if (!existing.ContainsKey(key))
                        existing[key] = new ExistingQuest(id, name, !string.IsNullOrWhiteSpace(bsgId) || isApproved);
                }
            }

            var added = 0;
            var updated = 0;
            var objectivesFilled = 0;
            var updatedAt = DateTime.UtcNow.ToString("o");

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = NormalizeQuestName(row.Name);
                existing.TryGetValue(key, out var current);
                if (ArenaQuestExclusionPolicy.IsExcludedWikiQuest(
                        row.Trader,
                        row.Map,
                        current?.IsStructured == true))
                {
                    _log.Debug($"Excluded Arena quest from Wiki refresh: {row.Name}");
                    continue;
                }

                string questId;

                if (current != null)
                {
                    questId = current.Id;
                    await using var update = new SqliteCommand(@"
                        UPDATE Quests
                        SET WikiPageLink=@wiki,
                            Trader=CASE WHEN @trader<>'' THEN @trader ELSE Trader END,
                            Location=CASE WHEN @location<>'' THEN @location ELSE Location END,
                            UpdatedAt=@updatedAt
                        WHERE Id=@id", connection, tx);
                    update.Parameters.AddWithValue("@wiki", row.WikiLink);
                    update.Parameters.AddWithValue("@trader", row.Trader);
                    update.Parameters.AddWithValue("@location", row.Map);
                    update.Parameters.AddWithValue("@updatedAt", updatedAt);
                    update.Parameters.AddWithValue("@id", questId);
                    await update.ExecuteNonQueryAsync(cancellationToken);
                    updated++;
                }
                else
                {
                    questId = "fandom_" + StableHash(row.Trader + "|" + row.Name);
                    await using var insert = new SqliteCommand(@"
                        INSERT INTO Quests (
                            Id, BsgId, Name, NameEN, NameKO, NameJA, WikiPageLink,
                            Trader, Location, MinLevel, MinLevelApproved, MinScavKarma,
                            MinScavKarmaApproved, UpdatedAt, KappaRequired, Faction, IsApproved,
                            RequiredEditionApproved, ExcludedEditionApproved,
                            RequiredDecodeCountApproved, RequiredPrestigeLevelApproved)
                        VALUES (@id, NULL, @name, @name, NULL, NULL, @wiki,
                            @trader, @location, NULL, 0, NULL, 0, @updatedAt, 0, NULL, 0,
                            0, 0, 0, 0)", connection, tx);
                    insert.Parameters.AddWithValue("@id", questId);
                    insert.Parameters.AddWithValue("@name", row.Name);
                    insert.Parameters.AddWithValue("@wiki", row.WikiLink);
                    insert.Parameters.AddWithValue("@trader", row.Trader);
                    insert.Parameters.AddWithValue("@location", row.Map);
                    insert.Parameters.AddWithValue("@updatedAt", updatedAt);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                    existing[key] = new ExistingQuest(questId, row.Name, false);
                    added++;
                }

                if (row.Objectives.Count == 0)
                    continue;

                var existingObjectiveCount = await ExecuteCountAsync(
                    connection,
                    tx,
                    "SELECT COUNT(*) FROM QuestObjectives WHERE QuestId=@id",
                    questId,
                    cancellationToken);
                var wikiObjectiveCount = await ExecuteCountAsync(
                    connection,
                    tx,
                    "SELECT COUNT(*) FROM QuestObjectives WHERE QuestId=@id AND ObjectiveType IN ('Wiki', 'FandomWiki')",
                    questId,
                    cancellationToken);

                // Keep structured tarkov.dev objectives. Replace old Japanese Wiki rows and
                // earlier Fandom rows so a refresh immediately migrates visible text.
                if (existingObjectiveCount > 0 && wikiObjectiveCount == 0)
                    continue;

                if (wikiObjectiveCount > 0)
                {
                    await using var deleteWiki = new SqliteCommand(
                        "DELETE FROM QuestObjectives WHERE QuestId=@id AND ObjectiveType IN ('Wiki', 'FandomWiki')",
                        connection,
                        tx);
                    deleteWiki.Parameters.AddWithValue("@id", questId);
                    await deleteWiki.ExecuteNonQueryAsync(cancellationToken);
                }

                var sort = 0;
                foreach (var objective in row.Objectives)
                {
                    await using var insertObjective = new SqliteCommand(@"
                        INSERT INTO QuestObjectives (
                            Id, QuestId, SortOrder, ObjectiveType, Description,
                            TargetCount, RequiresFIR, MapName, IsApproved, UpdatedAt)
                        VALUES (@id, @questId, @sort, 'FandomWiki', @description,
                            NULL, 0, @map, 0, @updatedAt)", connection, tx);
                    insertObjective.Parameters.AddWithValue(
                        "@id",
                        "fandomobj_" + StableHash(questId + "|" + sort + "|" + objective));
                    insertObjective.Parameters.AddWithValue("@questId", questId);
                    insertObjective.Parameters.AddWithValue("@sort", sort++);
                    insertObjective.Parameters.AddWithValue("@description", objective);
                    insertObjective.Parameters.AddWithValue(
                        "@map",
                        string.IsNullOrWhiteSpace(row.Map) ? DBNull.Value : row.Map);
                    insertObjective.Parameters.AddWithValue("@updatedAt", updatedAt);
                    await insertObjective.ExecuteNonQueryAsync(cancellationToken);
                    objectivesFilled++;
                }
            }

            var collectorCount = await SyncCollectorItemsAsync(
                connection,
                tx,
                collectorItems,
                updatedAt,
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return new OverlayStats(added, updated, objectivesFilled, collectorCount);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<int> SyncCollectorItemsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        IReadOnlyList<string> collectorItems,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        string? collectorQuestId = null;
        await using (var collectorCommand = new SqliteCommand(@"
            SELECT Id
            FROM Quests
            WHERE lower(COALESCE(NULLIF(NameEN, ''), Name))='collector'
            LIMIT 1", connection, tx))
        {
            collectorQuestId = (string?)await collectorCommand.ExecuteScalarAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(collectorQuestId))
            throw new InvalidOperationException("Collector quest was not found in the database.");

        var itemIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var itemCommand = new SqliteCommand(
                         "SELECT Id, COALESCE(NULLIF(NameEN, ''), Name) FROM Items",
                         connection,
                         tx))
        await using (var reader = await itemCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = NormalizeItemName(reader.GetString(1));
                if (!itemIds.ContainsKey(key))
                    itemIds[key] = reader.GetString(0);
            }
        }

        var resolved = new List<(string Id, string Name)>();
        foreach (var itemName in collectorItems)
        {
            var key = NormalizeItemName(itemName);
            if (!itemIds.TryGetValue(key, out var itemId))
            {
                var wikiLink = FandomPageBase + BuildWikiSlug(itemName);
                itemId = Convert.ToBase64String(Encoding.UTF8.GetBytes(wikiLink));

                await using var insertItem = new SqliteCommand(@"
                    INSERT OR IGNORE INTO Items (
                        Id, BsgId, Name, NameEN, NameKO, NameJA,
                        WikiPageLink, IconUrl, Category, Categories, UpdatedAt)
                    VALUES (@id, NULL, @name, @name, NULL, NULL,
                        @wiki, NULL, 'Other', NULL, @updatedAt)", connection, tx);
                insertItem.Parameters.AddWithValue("@id", itemId);
                insertItem.Parameters.AddWithValue("@name", itemName);
                insertItem.Parameters.AddWithValue("@wiki", wikiLink);
                insertItem.Parameters.AddWithValue("@updatedAt", updatedAt);
                await insertItem.ExecuteNonQueryAsync(cancellationToken);
                itemIds[key] = itemId;
            }

            resolved.Add((itemId, itemName));
        }

        await using (var deleteItems = new SqliteCommand(
                         "DELETE FROM QuestRequiredItems WHERE QuestId=@questId",
                         connection,
                         tx))
        {
            deleteItems.Parameters.AddWithValue("@questId", collectorQuestId);
            await deleteItems.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var sort = 0; sort < resolved.Count; sort++)
        {
            var item = resolved[sort];
            await using var insertRequirement = new SqliteCommand(@"
                INSERT INTO QuestRequiredItems (
                    Id, QuestId, ItemId, ItemName, Count, RequiresFIR,
                    RequirementType, SortOrder, IsApproved, UpdatedAt)
                VALUES (@id, @questId, @itemId, @itemName, 1, 1,
                    'Handover', @sort, 0, @updatedAt)", connection, tx);
            insertRequirement.Parameters.AddWithValue(
                "@id",
                "fandomitem_" + StableHash(collectorQuestId + "|" + item.Name));
            insertRequirement.Parameters.AddWithValue("@questId", collectorQuestId);
            insertRequirement.Parameters.AddWithValue("@itemId", item.Id);
            insertRequirement.Parameters.AddWithValue("@itemName", item.Name);
            insertRequirement.Parameters.AddWithValue("@sort", sort);
            insertRequirement.Parameters.AddWithValue("@updatedAt", updatedAt);
            await insertRequirement.ExecuteNonQueryAsync(cancellationToken);
        }

        return resolved.Count;
    }

    private static async Task<int> ExecuteCountAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sql,
        string questId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection, tx);
        command.Parameters.AddWithValue("@id", questId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string NormalizeQuestName(string value)
    {
        value = WebUtility.HtmlDecode(value ?? string.Empty).Trim().ToLowerInvariant();
        return Regex.Replace(value, @"[^a-z0-9]+", string.Empty);
    }

    private static string NormalizeItemName(string value)
    {
        value = WebUtility.HtmlDecode(value ?? string.Empty).Trim().ToLowerInvariant();
        return Regex.Replace(value, @"[^a-z0-9]+", string.Empty);
    }

    private static string BuildWikiSlug(string value) =>
        Uri.EscapeDataString(value.Replace(' ', '_')).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    private static string StableHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }

    private sealed record FandomPageResponse(string Title, string Url, string Html);
    private sealed record QuestParseResult(List<WikiQuestRow> Rows, List<string> CollectorItems);
    private sealed record WikiQuestRow(string Name, string Trader, string Map, string WikiLink, List<string> Objectives);
    private sealed record ExistingQuest(string Id, string Name, bool IsStructured);
    private sealed record OverlayStats(int Added, int Updated, int ObjectivesFilled, int CollectorItems);

    private sealed class StringTupleComparer : IEqualityComparer<(string Name, string Trader)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Name, string Trader) x, (string Name, string Trader) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Trader, y.Trader);

        public int GetHashCode((string Name, string Trader) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Trader));
    }
}

public sealed record WikiQuestRefreshResult(
    int WikiQuestCount,
    int AddedQuestCount,
    int UpdatedQuestCount,
    int ObjectivesFilledCount,
    string BackupPath);
