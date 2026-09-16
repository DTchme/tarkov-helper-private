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
/// Individual Wiki quest pages are authoritative for quest text, objectives and
/// prerequisite links. Existing structured data is retained only as supplemental
/// metadata for item IDs, objective types and map coordinates.
/// </summary>
public sealed class WikiQuestRefreshService
{
    private const string FandomApi = "https://escapefromtarkov.fandom.com/api.php";
    private const string FandomPageBase = "https://escapefromtarkov.fandom.com/wiki/";
    private const int MinimumExpectedQuestCount = 250;
    private const int MinimumCollectorItemCount = 40;
    private const int MaximumCollectorItemCount = 70;
    private const int PageBatchSize = 20;
    private const string QuestOverviewStateKey = "__wiki_overview__:Quests";
    private const string StoryOverviewStateKey = "__wiki_overview__:Story chapters";
    private const string OverlaySchemaStateKey = "__wiki_overlay_schema__";
    private const long OverlaySchemaVersion = 2;

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
        var revisionsTask = FetchQuestPageRevisionsAsync(cancellationToken);
        var existingNamesTask = LoadExistingQuestNamesAsync(databasePath, cancellationToken);
        var syncStateTask = LoadKnownRevisionsAsync(databasePath, cancellationToken);
        await Task.WhenAll(questsTask, storyTask, revisionsTask, existingNamesTask, syncStateTask);

        var parsed = ParseQuestTables(questsTask.Result);
        var rows = parsed.Rows;
        rows.AddRange(ParseStoryChapterLinks(storyTask.Result));

        var knownState = syncStateTask.Result;
        var forceFullRefresh =
            !knownState.Revisions.TryGetValue(OverlaySchemaStateKey, out var overlaySchemaVersion) ||
            overlaySchemaVersion != OverlaySchemaVersion;
        var activeNames = rows
            .Select(row => NormalizeQuestName(row.Name))
            .Concat(existingNamesTask.Result)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var discoveryCutoff = knownState.LastSuccessfulSyncUtc ?? DateTimeOffset.UtcNow.AddDays(-7);
        var recentPages = await FetchRecentChangedPagesAsync(discoveryCutoff, cancellationToken);
        var categoryTitles = revisionsTask.Result
            .Select(page => page.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidatePages = revisionsTask.Result
            .Concat(recentPages.Where(page => !categoryTitles.Contains(page.Title)))
            .GroupBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(page => page.RevisionId).First())
            .ToList();
        var changedPages = candidatePages
            .Where(page => forceFullRefresh ||
                           !knownState.Revisions.TryGetValue(page.Title, out var revision) ||
                           revision != page.RevisionId)
            .Where(page =>
                knownState.Revisions.ContainsKey(page.Title) ||
                activeNames.Contains(NormalizeQuestName(page.Title)) ||
                page.UpdatedAtUtc >= discoveryCutoff)
            .ToList();
        var individualFetch = await FetchIndividualQuestRowsAsync(changedPages, cancellationToken);
        var individualRows = individualFetch.Rows;
        rows.AddRange(individualRows);

        rows = rows
            .GroupBy(r => NormalizeQuestName(r.Name), StringComparer.OrdinalIgnoreCase)
            .Select(MergeQuestRows)
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

        var overviewPages = new List<WikiPageRevision>();
        if (forceFullRefresh ||
            !knownState.Revisions.TryGetValue(QuestOverviewStateKey, out var questOverviewRevision) ||
            questOverviewRevision != questsTask.Result.RevisionId)
        {
            overviewPages.Add(new WikiPageRevision(
                QuestOverviewStateKey,
                questsTask.Result.RevisionId,
                DateTimeOffset.UtcNow));
        }
        if (forceFullRefresh ||
            !knownState.Revisions.TryGetValue(StoryOverviewStateKey, out var storyOverviewRevision) ||
            storyOverviewRevision != storyTask.Result.RevisionId)
        {
            overviewPages.Add(new WikiPageRevision(
                StoryOverviewStateKey,
                storyTask.Result.RevisionId,
                DateTimeOffset.UtcNow));
        }
        if (forceFullRefresh)
        {
            overviewPages.Add(new WikiPageRevision(
                OverlaySchemaStateKey,
                OverlaySchemaVersion,
                DateTimeOffset.UtcNow));
        }

        if (individualFetch.ProcessedPages.Count == 0 && overviewPages.Count == 0)
        {
            _log.Info("Official Wiki quest data is unchanged; database replacement skipped");
            return new WikiQuestRefreshResult(
                rows.Count,
                0,
                0,
                0,
                0,
                0,
                0,
                string.Empty,
                false);
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
                individualFetch.ProcessedPages.Concat(overviewPages).ToList(),
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
                $"updated={stats.Updated}, individualPages={individualRows.Count}, " +
                $"objectivesReplaced={stats.ObjectivesFilled}, prerequisites={stats.Prerequisites}, " +
                $"requiredItems={stats.RequiredItems}, " +
                $"collectorItems={stats.CollectorItems}");

            return new WikiQuestRefreshResult(
                rows.Count,
                stats.Added,
                stats.Updated,
                stats.ObjectivesFilled,
                individualRows.Count,
                stats.Prerequisites,
                stats.RequiredItems,
                backupPath,
                true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private async Task<List<WikiPageRevision>> FetchQuestPageRevisionsAsync(
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, WikiPageRevision>(StringComparer.OrdinalIgnoreCase);
        string? continuation = null;

        do
        {
            var apiUrl = FandomApi +
                "?action=query&generator=categorymembers&gcmtitle=Category%3AQuests" +
                "&gcmnamespace=0&gcmlimit=max&prop=revisions&rvprop=ids%7Ctimestamp" +
                "&format=json&formatversion=2" +
                (string.IsNullOrWhiteSpace(continuation)
                    ? string.Empty
                    : "&gcmcontinue=" + Uri.EscapeDataString(continuation));

            using var request = CreateWikiRequest(apiUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("query", out var query) &&
                query.TryGetProperty("pages", out var pages) &&
                pages.ValueKind == JsonValueKind.Array)
            {
                foreach (var page in pages.EnumerateArray())
                {
                    var title = page.TryGetProperty("title", out var titleElement)
                        ? titleElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(title) ||
                        !page.TryGetProperty("revisions", out var revisions) ||
                        revisions.ValueKind != JsonValueKind.Array ||
                        revisions.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var revision = revisions[0];
                    if (!revision.TryGetProperty("revid", out var revisionElement) ||
                        !revisionElement.TryGetInt64(out var revisionId))
                    {
                        continue;
                    }

                    var updatedAt = revision.TryGetProperty("timestamp", out var timestampElement) &&
                                    DateTimeOffset.TryParse(timestampElement.GetString(), out var parsedTimestamp)
                        ? parsedTimestamp
                        : DateTimeOffset.MinValue;
                    result[title] = new WikiPageRevision(title, revisionId, updatedAt);
                }
            }

            continuation = document.RootElement.TryGetProperty("continue", out var continueElement) &&
                           continueElement.TryGetProperty("gcmcontinue", out var gcmContinue)
                ? gcmContinue.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(continuation));

        if (result.Count < MinimumExpectedQuestCount)
        {
            throw new InvalidOperationException(
                $"Official Wiki quest category count is unexpectedly low ({result.Count}).");
        }

        return result.Values.OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<WikiPageRevision>> FetchRecentChangedPagesAsync(
        DateTimeOffset requestedCutoff,
        CancellationToken cancellationToken)
    {
        // MediaWiki only retains a bounded recent-change window. The packaged DB is
        // refreshed at release time, so 29 days is enough for long-offline clients.
        var cutoff = requestedCutoff < DateTimeOffset.UtcNow.AddDays(-29)
            ? DateTimeOffset.UtcNow.AddDays(-29)
            : requestedCutoff;
        var scanStartedAt = DateTimeOffset.UtcNow;
        var result = new Dictionary<string, WikiPageRevision>(StringComparer.OrdinalIgnoreCase);
        string? continuation = null;

        do
        {
            var apiUrl = FandomApi +
                "?action=query&list=recentchanges&rcnamespace=0&rctype=edit%7Cnew%7Clog" +
                "&rcprop=title%7Cids%7Ctimestamp%7Cloginfo&rclimit=max&rcdir=older" +
                "&rcstart=" + Uri.EscapeDataString(scanStartedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")) +
                "&rcend=" + Uri.EscapeDataString(cutoff.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")) +
                "&format=json&formatversion=2" +
                (string.IsNullOrWhiteSpace(continuation)
                    ? string.Empty
                    : "&rccontinue=" + Uri.EscapeDataString(continuation));

            using var request = CreateWikiRequest(apiUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("query", out var query) &&
                query.TryGetProperty("recentchanges", out var changes) &&
                changes.ValueKind == JsonValueKind.Array)
            {
                foreach (var change in changes.EnumerateArray())
                {
                    var title = change.TryGetProperty("title", out var titleElement)
                        ? titleElement.GetString()
                        : null;
                    if (change.TryGetProperty("logtype", out var logType) &&
                        logType.GetString()?.Equals("move", StringComparison.OrdinalIgnoreCase) == true &&
                        change.TryGetProperty("logparams", out var logParameters) &&
                        logParameters.TryGetProperty("target_title", out var targetTitle) &&
                        !string.IsNullOrWhiteSpace(targetTitle.GetString()))
                    {
                        title = targetTitle.GetString();
                    }
                    if (string.IsNullOrWhiteSpace(title) ||
                        !change.TryGetProperty("revid", out var revisionElement) ||
                        !revisionElement.TryGetInt64(out var revisionId) ||
                        revisionId <= 0)
                    {
                        continue;
                    }

                    var updatedAt = change.TryGetProperty("timestamp", out var timestampElement) &&
                                    DateTimeOffset.TryParse(timestampElement.GetString(), out var parsedTimestamp)
                        ? parsedTimestamp
                        : DateTimeOffset.MinValue;
                    if (!result.TryGetValue(title, out var current) || revisionId > current.RevisionId)
                        result[title] = new WikiPageRevision(title, revisionId, updatedAt);
                }
            }

            continuation = document.RootElement.TryGetProperty("continue", out var continueElement) &&
                           continueElement.TryGetProperty("rccontinue", out var rcContinue)
                ? rcContinue.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(continuation));

        return result.Values.ToList();
    }

    private static async Task<HashSet<string>> LoadExistingQuestNamesAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();

        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqliteCommand(
            "SELECT COALESCE(NULLIF(NameEN, ''), Name) FROM Quests",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
                result.Add(NormalizeQuestName(reader.GetString(0)));
        }

        return result;
    }

    private static async Task<WikiSyncStateSnapshot> LoadKnownRevisionsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();

        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var exists = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WikiQuestSyncState'",
            connection);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
            return new WikiSyncStateSnapshot(result, null);

        await using var command = new SqliteCommand(
            "SELECT PageTitle, RevisionId, UpdatedAt FROM WikiQuestSyncState",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        DateTimeOffset? lastSuccessfulSync = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                result[reader.GetString(0)] = reader.GetInt64(1);
            if (!reader.IsDBNull(2) &&
                DateTimeOffset.TryParse(reader.GetString(2), out var updatedAt) &&
                (!lastSuccessfulSync.HasValue || updatedAt > lastSuccessfulSync.Value))
            {
                lastSuccessfulSync = updatedAt;
            }
        }

        return new WikiSyncStateSnapshot(result, lastSuccessfulSync);
    }

    private async Task<QuestPageFetchResult> FetchIndividualQuestRowsAsync(
        IReadOnlyList<WikiPageRevision> pages,
        CancellationToken cancellationToken)
    {
        var rows = new List<WikiQuestRow>();
        var processedPages = new List<WikiPageRevision>();
        for (var offset = 0; offset < pages.Count; offset += PageBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = pages.Skip(offset).Take(PageBatchSize).ToList();
            var titles = string.Join("|", batch.Select(page => page.Title));
            var apiUrl = FandomApi +
                "?action=query&prop=revisions&rvprop=ids%7Ctimestamp%7Ccontent&rvslots=main" +
                "&redirects=1&format=json&formatversion=2&titles=" + Uri.EscapeDataString(titles);

            using var request = CreateWikiRequest(apiUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pageElements) ||
                pageElements.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            processedPages.AddRange(batch);

            foreach (var pageElement in pageElements.EnumerateArray())
            {
                var row = ParseIndividualQuestPage(pageElement);
                if (row != null)
                    rows.Add(row);
            }
        }

        return new QuestPageFetchResult(rows, processedPages);
    }

    private WikiQuestRow? ParseIndividualQuestPage(JsonElement page)
    {
        var title = page.TryGetProperty("title", out var titleElement)
            ? titleElement.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(title) ||
            !page.TryGetProperty("revisions", out var revisions) ||
            revisions.ValueKind != JsonValueKind.Array ||
            revisions.GetArrayLength() == 0)
        {
            return null;
        }

        var revision = revisions[0];
        if (!revision.TryGetProperty("revid", out var revisionElement) ||
            !revisionElement.TryGetInt64(out var revisionId) ||
            !revision.TryGetProperty("slots", out var slots) ||
            !slots.TryGetProperty("main", out var main) ||
            !main.TryGetProperty("content", out var contentElement))
        {
            return null;
        }

        var wikiText = contentElement.GetString() ?? string.Empty;
        if (!Regex.IsMatch(wikiText, @"\{\{\s*Infobox\s+quest\b", RegexOptions.IgnoreCase))
            return null;

        var traderValue = ExtractInfoboxField(wikiText, "given\\s*by");
        var locationValue = ExtractInfoboxField(wikiText, "location");
        var previousValue = ExtractInfoboxField(wikiText, "previous");
        var kappaValue = ExtractInfoboxField(wikiText, "reqkappa");
        var rawObjectives = ExtractWikiObjectives(wikiText);
        var rawRequirements = ExtractWikiRequirements(wikiText);
        var objectives = rawObjectives
            .Select(_translator.TranslateObjective)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var prerequisiteResult = WikiQuestMetadataParser.ParsePrerequisites(previousValue.Value, title);
        var requirementSummary = WikiQuestMetadataParser.ParseRequirements(rawRequirements);
        var trader = NormalizeTrader(CleanWikiText(traderValue.Value));
        var infoboxMap = NormalizeMap(CleanWikiText(locationValue.Value));
        var map = string.IsNullOrWhiteSpace(infoboxMap)
            ? NormalizeMap(string.Join(" ", rawObjectives))
            : infoboxMap;
        var wikiLink = FandomPageBase + BuildWikiSlug(title);
        bool? kappaRequired = kappaValue.Exists
            ? Regex.IsMatch(CleanWikiText(kappaValue.Value), @"\byes\b", RegexOptions.IgnoreCase)
            : null;

        return new WikiQuestRow(
            title,
            trader,
            map,
            wikiLink,
            objectives,
            Prerequisites: prerequisiteResult.Prerequisites.ToList(),
            IsIndividualPage: true,
            HasPreviousField: previousValue.Exists,
            PrerequisitesResolved: prerequisiteResult.IsResolved,
            RevisionId: revisionId,
            KappaRequired: kappaRequired,
            RawObjectives: rawObjectives,
            MinimumLevel: requirementSummary.MinimumLevel,
            UnverifiedRequirements: requirementSummary.UnverifiedRequirements.ToList());
    }

    private static InfoboxField ExtractInfoboxField(string wikiText, string fieldPattern)
    {
        var infoboxStart = Regex.Match(
            wikiText,
            @"\{\{\s*Infobox\s+quest\b",
            RegexOptions.IgnoreCase);
        if (!infoboxStart.Success)
            return new InfoboxField(false, string.Empty);

        var infoboxAndLead = wikiText[infoboxStart.Index..];
        var nextSection = Regex.Match(infoboxAndLead, @"(?m)^\s*==");
        var block = nextSection.Success
            ? infoboxAndLead[..nextSection.Index]
            : infoboxAndLead;
        var match = Regex.Match(
            block,
            $@"(?ims)(?:^|\|)\s*(?:{fieldPattern})\s*=\s*(?<value>.*?)(?=\|\s*[a-z][a-z0-9 _-]*\s*=|\}}\}}(?:\s|')|\z)");
        return match.Success
            ? new InfoboxField(true, match.Groups["value"].Value.Trim())
            : new InfoboxField(false, string.Empty);
    }

    private static List<string> ExtractWikiObjectives(string wikiText)
    {
        var section = Regex.Match(
            wikiText,
            @"(?ims)^==\s*Objectives\s*==\s*(?<body>.*?)(?=^==\s*[^=].*?==\s*$|\z)");
        if (!section.Success)
            return new List<string>();

        return Regex.Matches(section.Groups["body"].Value, @"(?m)^\*(?!\*)\s*(?<value>.+?)\s*$")
            .Cast<Match>()
            .Select(match => CleanWikiText(match.Groups["value"].Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractWikiRequirements(string wikiText)
    {
        var section = Regex.Match(
            wikiText,
            @"(?ims)^==\s*Requirements\s*==\s*(?<body>.*?)(?=^==\s*[^=].*?==\s*$|\z)");
        if (!section.Success)
            return new List<string>();

        return Regex.Matches(section.Groups["body"].Value, @"(?m)^\*(?!\*)\s*(?<value>.+?)\s*$")
            .Cast<Match>()
            .Select(match => CleanWikiText(match.Groups["value"].Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractWikiLinks(string wikiText)
    {
        return Regex.Matches(wikiText ?? string.Empty, @"\[\[(?<target>[^\]|#]+)(?:#[^\]|]*)?(?:\|(?<label>[^\]]+))?\]\]")
            .Cast<Match>()
            .Select(match => CleanWikiText(
                match.Groups["label"].Success
                    ? match.Groups["label"].Value
                    : match.Groups["target"].Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static string CleanWikiText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = Regex.Replace(value, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<ref\b[^>]*>.*?</ref>|<ref\b[^>]*/>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\[\[(?<target>[^\]|]+)(?:\|(?<label>[^\]]+))?\]\]", match =>
            match.Groups["label"].Success ? match.Groups["label"].Value : match.Groups["target"].Value);
        text = Regex.Replace(text, @"\{\{[^{}]*\}\}", " ");
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = text.Replace("'''", string.Empty).Replace("''", string.Empty);
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return Regex.Replace(text, @"\s+([,.;:!?])", "$1");
    }

    private static string NormalizeTrader(string value)
    {
        var known = new[]
        {
            "Prapor", "Therapist", "Fence", "Skier", "Peacekeeper", "Mechanic",
            "Ragman", "Jaeger", "Ref", "Lightkeeper", "BTR Driver"
        };
        return known.FirstOrDefault(trader => value.Contains(trader, StringComparison.OrdinalIgnoreCase))
               ?? value.Trim();
    }

    private static HttpRequestMessage CreateWikiRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("TarkovHelper/1.5.27 (+English-Fandom-Wiki quest sync)");
        return request;
    }

    private async Task<FandomPageResponse> FetchParsedPageAsync(
        string page,
        CancellationToken cancellationToken)
    {
        var apiUrl = FandomApi +
            "?action=parse&prop=text%7Crevid&format=json&formatversion=2&page=" +
            Uri.EscapeDataString(page);

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.UserAgent.ParseAdd("TarkovHelper/1.5.27 (+official wiki sync)");
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
        var revisionId = parse.TryGetProperty("revid", out var revisionElement) &&
                         revisionElement.TryGetInt64(out var parsedRevisionId)
            ? parsedRevisionId
            : 0;
        var displayUrl = FandomPageBase + BuildWikiSlug(title);
        return new FandomPageResponse(
            title,
            displayUrl,
            textElement.GetString() ?? string.Empty,
            revisionId);
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

                rows.Add(new WikiQuestRow(
                    name,
                    trader,
                    map,
                    wikiLink,
                    objectives,
                    RawObjectives: rawObjectives));
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
        IReadOnlyList<WikiPageRevision> processedPages,
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
            await EnsureWikiSyncStateTableAsync(connection, tx, cancellationToken);
            await EnsureWikiMetadataTablesAsync(connection, tx, cancellationToken);

            var removedExcludedQuestCount = await QuestExclusionPolicy.RemoveExcludedRowsAsync(
                connection,
                tx,
                cancellationToken);
            if (removedExcludedQuestCount > 0)
                _log.Info($"Removed {removedExcludedQuestCount} excluded quests before Wiki overlay");

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
                    if (QuestExclusionPolicy.IsExcludedStoredQuest(
                            id,
                            bsgId,
                            name,
                            trader,
                            location,
                            isApproved))
                    {
                        continue;
                    }

                    var key = NormalizeQuestName(name);
                    if (!existing.ContainsKey(key))
                        existing[key] = new ExistingQuest(
                            id,
                            name,
                            bsgId,
                            !string.IsNullOrWhiteSpace(bsgId) || isApproved);
                }
            }

            var added = 0;
            var updated = 0;
            var objectivesFilled = 0;
            var prerequisites = 0;
            var requiredItems = 0;
            var updatedAt = DateTime.UtcNow.ToString("o");

            // Pass 1: create/update every quest first. This lets prerequisite links
            // resolve even when the required quest appeared later in the Wiki batch.
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = NormalizeQuestName(row.Name);
                existing.TryGetValue(key, out var current);
                if (QuestExclusionPolicy.IsExcludedWikiQuest(
                        row.Name,
                        row.Trader,
                        row.Map,
                        current?.IsStructured == true))
                {
                    _log.Debug($"Excluded quest from Wiki refresh: {row.Name}");
                    continue;
                }

                string questId;

                if (current != null)
                {
                    questId = current.Id;
                    await using var update = new SqliteCommand(@"
                        UPDATE Quests
                        SET NameEN=@name,
                            Name=CASE WHEN Name IS NULL OR Name='' THEN @name ELSE Name END,
                            WikiPageLink=@wiki,
                            Trader=CASE WHEN @trader<>'' THEN @trader ELSE Trader END,
                             Location=CASE WHEN @location<>'' THEN @location ELSE Location END,
                             MinLevel=CASE WHEN @minLevelProvided=1 THEN @minLevel ELSE MinLevel END,
                             KappaRequired=CASE WHEN @kappaProvided=1 THEN @kappa ELSE KappaRequired END,
                            UpdatedAt=@updatedAt
                        WHERE Id=@id", connection, tx);
                    update.Parameters.AddWithValue("@name", row.Name);
                    update.Parameters.AddWithValue("@wiki", row.WikiLink);
                    update.Parameters.AddWithValue("@trader", row.Trader);
                    update.Parameters.AddWithValue("@location", row.Map);
                    update.Parameters.AddWithValue("@minLevelProvided", row.MinimumLevel.HasValue ? 1 : 0);
                    AddNullable(update, "@minLevel", row.MinimumLevel);
                    update.Parameters.AddWithValue("@kappaProvided", row.KappaRequired.HasValue ? 1 : 0);
                    update.Parameters.AddWithValue("@kappa", row.KappaRequired == true ? 1 : 0);
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
                             @trader, @location, @minLevel, 0, NULL, 0, @updatedAt, @kappa, NULL, 0,
                            0, 0, 0, 0)", connection, tx);
                    insert.Parameters.AddWithValue("@id", questId);
                    insert.Parameters.AddWithValue("@name", row.Name);
                    insert.Parameters.AddWithValue("@wiki", row.WikiLink);
                    insert.Parameters.AddWithValue("@trader", row.Trader);
                    insert.Parameters.AddWithValue("@location", row.Map);
                    AddNullable(insert, "@minLevel", row.MinimumLevel);
                    insert.Parameters.AddWithValue("@kappa", row.KappaRequired == true ? 1 : 0);
                    insert.Parameters.AddWithValue("@updatedAt", updatedAt);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                    existing[key] = new ExistingQuest(questId, row.Name, null, false);
                    added++;
                }

                await UpsertWikiQuestMetadataAsync(
                    connection,
                    tx,
                    questId,
                    row.UnverifiedRequirementTexts,
                    updatedAt,
                    cancellationToken);
            }

            var itemCatalog = await LoadItemCatalogAsync(connection, tx, cancellationToken);

            // Pass 2: an individual Wiki page is authoritative for visible objectives
            // and prerequisite links. Overview-table rows only update basic metadata.
            foreach (var row in rows.Where(item => item.IsIndividualPage))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!existing.TryGetValue(NormalizeQuestName(row.Name), out var current))
                    continue;

                if (QuestExclusionPolicy.IsExcludedWikiQuest(
                        row.Name,
                        row.Trader,
                        row.Map,
                        current.IsStructured))
                {
                    continue;
                }

                objectivesFilled += await ReplaceObjectivesFromWikiAsync(
                    connection,
                    tx,
                    current.Id,
                    row,
                    updatedAt,
                    cancellationToken);

                prerequisites += await ReplacePrerequisitesFromWikiAsync(
                    connection,
                    tx,
                    current.Id,
                    row,
                    existing,
                    updatedAt,
                    cancellationToken);

                requiredItems += await ReplaceRequiredItemsFromWikiAsync(
                    connection,
                    tx,
                    current.Id,
                    row,
                    itemCatalog,
                    updatedAt,
                    cancellationToken);

            }

            foreach (var page in processedPages)
                await UpsertWikiRevisionAsync(connection, tx, page, updatedAt, cancellationToken);

            var collectorCount = await SyncCollectorItemsAsync(
                connection,
                tx,
                collectorItems,
                updatedAt,
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return new OverlayStats(added, updated, objectivesFilled, prerequisites, requiredItems, collectorCount);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task EnsureWikiSyncStateTableAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS WikiQuestSyncState (
                PageTitle TEXT PRIMARY KEY,
                RevisionId INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL
            )", connection, tx);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureWikiMetadataTablesAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS WikiQuestMetadata (
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
                ON QuestIdentityAliases(GameQuestId);", connection, tx);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertWikiQuestMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string questId,
        IReadOnlyList<string> unverifiedRequirements,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        var notes = string.Join(
            Environment.NewLine,
            unverifiedRequirements
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase));

        await using var command = new SqliteCommand(@"
            INSERT INTO WikiQuestMetadata (
                QuestId, RequirementNotes, HasUnverifiedRequirements, UpdatedAt)
            VALUES (@questId, @notes, @hasUnverified, @updatedAt)
            ON CONFLICT(QuestId) DO UPDATE SET
                RequirementNotes=excluded.RequirementNotes,
                HasUnverifiedRequirements=excluded.HasUnverifiedRequirements,
                UpdatedAt=excluded.UpdatedAt", connection, tx);
        command.Parameters.AddWithValue("@questId", questId);
        AddNullable(command, "@notes", string.IsNullOrWhiteSpace(notes) ? null : notes);
        command.Parameters.AddWithValue("@hasUnverified", string.IsNullOrWhiteSpace(notes) ? 0 : 1);
        command.Parameters.AddWithValue("@updatedAt", updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<WikiItemCatalogEntry>> LoadItemCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        CancellationToken cancellationToken)
    {
        var items = new List<WikiItemCatalogEntry>();
        await using var command = new SqliteCommand(
            "SELECT Id, COALESCE(NULLIF(NameEN, ''), Name) FROM Items",
            connection,
            tx);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;

            var id = reader.GetString(0);
            var name = reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                items.Add(new WikiItemCatalogEntry(id, name));
        }

        return items;
    }

    private static async Task UpsertWikiRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        WikiPageRevision page,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
            INSERT INTO WikiQuestSyncState (PageTitle, RevisionId, UpdatedAt)
            VALUES (@title, @revision, @updatedAt)
            ON CONFLICT(PageTitle) DO UPDATE SET
                RevisionId=excluded.RevisionId,
                UpdatedAt=excluded.UpdatedAt", connection, tx);
        command.Parameters.AddWithValue("@title", page.Title);
        command.Parameters.AddWithValue("@revision", page.RevisionId);
        command.Parameters.AddWithValue("@updatedAt", updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReplaceObjectivesFromWikiAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string questId,
        WikiQuestRow row,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        // A page without an Objectives section is probably incomplete or malformed.
        // Keep the existing rows instead of erasing useful data.
        if (row.Objectives.Count == 0)
            return 0;

        var previous = new List<ExistingObjectiveMetadata>();
        await using (var select = new SqliteCommand(@"
            SELECT Id, ObjectiveType, Description, TargetType, TargetCount,
                   ItemId, ItemName, RequiresFIR, MapName, LocationName,
                   LocationPoints, OptionalPoints, Conditions, DogtagMinLevel,
                   DogtagFaction, ContentHash, IsApproved, ApprovedAt
            FROM QuestObjectives
            WHERE QuestId=@questId
            ORDER BY SortOrder", connection, tx))
        {
            select.Parameters.AddWithValue("@questId", questId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                previous.Add(new ExistingObjectiveMetadata(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "Custom" : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    DbString(reader, 3),
                    DbInt(reader, 4),
                    DbString(reader, 5),
                    DbString(reader, 6),
                    !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
                    DbString(reader, 8),
                    DbString(reader, 9),
                    DbString(reader, 10),
                    DbString(reader, 11),
                    DbString(reader, 12),
                    DbInt(reader, 13),
                    DbString(reader, 14),
                    DbString(reader, 15),
                    !reader.IsDBNull(16) && reader.GetInt32(16) == 1,
                    DbString(reader, 17)));
            }
        }

        await using (var delete = new SqliteCommand(
                         "DELETE FROM QuestObjectives WHERE QuestId=@questId",
                         connection,
                         tx))
        {
            delete.Parameters.AddWithValue("@questId", questId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var sort = 0; sort < row.Objectives.Count; sort++)
        {
            var objective = row.Objectives[sort];
            var rawObjective = sort < row.RawObjectiveTexts.Count
                ? row.RawObjectiveTexts[sort]
                : objective;
            var metadata = FindBestObjectiveMetadata(objective, rawObjective, previous, usedIds);
            var objectiveId = metadata?.Id
                              ?? "fandomobj_" + StableHash(questId + "|" + sort + "|" + objective);
            if (!usedIds.Add(objectiveId))
                objectiveId = "fandomobj_" + StableHash(questId + "|" + sort + "|" + objective);

            var mapName = metadata?.MapName;
            if (string.IsNullOrWhiteSpace(mapName))
                mapName = string.IsNullOrWhiteSpace(row.Map) ? null : row.Map;
            var conditions = JsonSerializer.Serialize(new
            {
                source = "fandom-wiki",
                revision = row.RevisionId,
                supplementalMetadataPreserved = metadata != null
            });

            await using var insert = new SqliteCommand(@"
                INSERT INTO QuestObjectives (
                    Id, QuestId, SortOrder, ObjectiveType, Description,
                    TargetType, TargetCount, ItemId, ItemName, RequiresFIR,
                    MapName, LocationName, LocationPoints, OptionalPoints,
                    Conditions, DogtagMinLevel, DogtagFaction, ContentHash,
                    IsApproved, ApprovedAt, UpdatedAt)
                VALUES (
                    @id, @questId, @sort, @type, @description,
                    @targetType, @targetCount, @itemId, @itemName, @fir,
                    @map, @locationName, @locationPoints, @optionalPoints,
                    @conditions, @dogtagMinLevel, @dogtagFaction, @contentHash,
                    @approved, @approvedAt, @updatedAt)", connection, tx);
            insert.Parameters.AddWithValue("@id", objectiveId);
            insert.Parameters.AddWithValue("@questId", questId);
            insert.Parameters.AddWithValue("@sort", sort);
            insert.Parameters.AddWithValue("@type", metadata?.ObjectiveType ?? InferObjectiveType(objective));
            insert.Parameters.AddWithValue("@description", objective);
            AddNullable(insert, "@targetType", metadata?.TargetType);
            AddNullable(insert, "@targetCount", metadata?.TargetCount ?? InferTargetCount(rawObjective));
            AddNullable(insert, "@itemId", metadata?.ItemId);
            AddNullable(insert, "@itemName", metadata?.ItemName);
            var requiresFir = metadata?.RequiresFir == true ||
                              rawObjective.Contains("found in raid", StringComparison.OrdinalIgnoreCase);
            insert.Parameters.AddWithValue("@fir", requiresFir ? 1 : 0);
            AddNullable(insert, "@map", mapName);
            AddNullable(insert, "@locationName", metadata?.LocationName);
            AddNullable(insert, "@locationPoints", metadata?.LocationPoints);
            AddNullable(insert, "@optionalPoints", metadata?.OptionalPoints);
            insert.Parameters.AddWithValue("@conditions", conditions);
            AddNullable(insert, "@dogtagMinLevel", metadata?.DogtagMinLevel);
            AddNullable(insert, "@dogtagFaction", metadata?.DogtagFaction);
            AddNullable(insert, "@contentHash", metadata?.ContentHash);
            insert.Parameters.AddWithValue("@approved", metadata?.IsApproved == true ? 1 : 0);
            AddNullable(insert, "@approvedAt", metadata?.ApprovedAt);
            insert.Parameters.AddWithValue("@updatedAt", updatedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return row.Objectives.Count;
    }

    private static async Task<int> ReplacePrerequisitesFromWikiAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string questId,
        WikiQuestRow row,
        IReadOnlyDictionary<string, ExistingQuest> quests,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        // If the Wiki field could not be interpreted safely, retain the last known
        // prerequisite graph rather than silently turning an unknown requirement
        // into "conditions met".
        if (!row.HasPreviousField || !row.PrerequisitesResolved)
            return 0;

        await using (var delete = new SqliteCommand(
                         "DELETE FROM QuestRequirements WHERE QuestId=@questId",
                         connection,
                         tx))
        {
            delete.Parameters.AddWithValue("@questId", questId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var inserted = 0;
        foreach (var prerequisite in row.PrerequisiteLinks)
        {
            var previousName = prerequisite.Name;
            if (!quests.TryGetValue(NormalizeQuestName(previousName), out var required) ||
                required.Id.Equals(questId, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warning($"Wiki prerequisite was not found in the quest database: {row.Name} <- {previousName}");
                continue;
            }

            await using var insert = new SqliteCommand(@"
                INSERT OR IGNORE INTO QuestRequirements (
                    Id, QuestId, RequiredQuestId, RequirementType,
                    DelayMinutes, GroupId, IsApproved, UpdatedAt)
                VALUES (@id, @questId, @requiredQuestId, 'Complete',
                    NULL, @groupId, 0, @updatedAt)", connection, tx);
            insert.Parameters.AddWithValue(
                "@id",
                "fandomreq_" + StableHash(
                    questId + "|" + required.Id + "|complete|" + prerequisite.GroupId));
            insert.Parameters.AddWithValue("@questId", questId);
            insert.Parameters.AddWithValue("@requiredQuestId", required.Id);
            insert.Parameters.AddWithValue("@groupId", prerequisite.GroupId);
            insert.Parameters.AddWithValue("@updatedAt", updatedAt);
            inserted += await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return inserted;
    }

    private static async Task<int> ReplaceRequiredItemsFromWikiAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string questId,
        WikiQuestRow row,
        IReadOnlyList<WikiItemCatalogEntry> itemCatalog,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        var candidateObjectives = row.RawObjectiveTexts
            .Where(WikiQuestMetadataParser.IsRequiredItemObjective)
            .ToList();
        if (candidateObjectives.Count == 0 || candidateObjectives.Any(objective =>
                WikiQuestMetadataParser.ParseRequiredItems(new[] { objective }, itemCatalog).Count == 0))
        {
            return 0;
        }

        var parsed = WikiQuestMetadataParser.ParseRequiredItems(candidateObjectives, itemCatalog);

        // Only replace the existing item rows when the Wiki objectives produced a
        // complete, resolvable item set. This protects richer structured rows from
        // being erased by prose-only or temporarily malformed Wiki pages.
        if (parsed.Count == 0)
            return 0;

        await using (var delete = new SqliteCommand(
                         "DELETE FROM QuestRequiredItems WHERE QuestId=@questId",
                         connection,
                         tx))
        {
            delete.Parameters.AddWithValue("@questId", questId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var sort = 0; sort < parsed.Count; sort++)
        {
            var item = parsed[sort];
            await using var insert = new SqliteCommand(@"
                INSERT INTO QuestRequiredItems (
                    Id, QuestId, ItemId, ItemName, Count, RequiresFIR,
                    RequirementType, SortOrder, IsApproved, UpdatedAt)
                VALUES (@id, @questId, @itemId, @itemName, @count, @fir,
                    @type, @sort, 0, @updatedAt)", connection, tx);
            insert.Parameters.AddWithValue(
                "@id",
                "fandomitem_" + StableHash(
                    questId + "|" + item.ItemId + "|" + item.RequirementType));
            insert.Parameters.AddWithValue("@questId", questId);
            insert.Parameters.AddWithValue("@itemId", item.ItemId);
            insert.Parameters.AddWithValue("@itemName", item.ItemName);
            insert.Parameters.AddWithValue("@count", item.Count);
            insert.Parameters.AddWithValue("@fir", item.RequiresFir ? 1 : 0);
            insert.Parameters.AddWithValue("@type", item.RequirementType);
            insert.Parameters.AddWithValue("@sort", sort);
            insert.Parameters.AddWithValue("@updatedAt", updatedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return parsed.Count;
    }

    private static ExistingObjectiveMetadata? FindBestObjectiveMetadata(
        string objective,
        string rawObjective,
        IEnumerable<ExistingObjectiveMetadata> candidates,
        ISet<string> usedIds)
    {
        var normalized = NormalizeObjectiveText(objective);
        var normalizedRaw = NormalizeObjectiveText(rawObjective);
        var available = candidates.Where(item => !usedIds.Contains(item.Id)).ToList();
        var exact = available.FirstOrDefault(item =>
        {
            var candidate = NormalizeObjectiveText(item.Description);
            return candidate == normalized || candidate == normalizedRaw;
        });
        if (exact != null)
            return exact;

        return available
            .Select(item =>
            {
                var candidate = NormalizeObjectiveText(item.Description);
                return (Item: item, Score: Math.Max(
                    TokenSimilarity(normalized, candidate),
                    TokenSimilarity(normalizedRaw, candidate)));
            })
            .Where(match => match.Score >= 0.55)
            .OrderByDescending(match => match.Score)
            .Select(match => match.Item)
            .FirstOrDefault();
    }

    private static double TokenSimilarity(string left, string right)
    {
        var leftTokens = Regex.Matches(left, @"[a-z0-9가-힣]+")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightTokens = Regex.Matches(right, @"[a-z0-9가-힣]+")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0;

        var intersection = leftTokens.Count(rightTokens.Contains);
        var union = leftTokens.Count + rightTokens.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static string NormalizeObjectiveText(string value) =>
        Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9가-힣]+", " ").Trim();

    private static string InferObjectiveType(string objective)
    {
        var value = objective.ToLowerInvariant();
        if (Regex.IsMatch(value, @"\b(eliminate|kill)\b") || value.Contains("처치")) return "Kill";
        if (Regex.IsMatch(value, @"\b(stash|plant)\b") || value.Contains("숨기") || value.Contains("설치")) return "Stash";
        if (Regex.IsMatch(value, @"\b(hand over|deliver)\b") || value.Contains("전달")) return "HandOver";
        if (Regex.IsMatch(value, @"\b(find|obtain|acquire)\b") || value.Contains("획득") || value.Contains("찾")) return "Collect";
        if (Regex.IsMatch(value, @"\b(mark)\b") || value.Contains("표시")) return "Mark";
        if (Regex.IsMatch(value, @"\b(survive|extract|transit)\b") || value.Contains("탈출") || value.Contains("환승")) return "Survive";
        if (Regex.IsMatch(value, @"\b(visit|locate|scout|check|reach)\b") || value.Contains("방문") || value.Contains("정찰")) return "Visit";
        return "Custom";
    }

    private static int? InferTargetCount(string objective)
    {
        var match = Regex.Match(objective ?? string.Empty, @"\b(?<count>\d{1,4})\b");
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count)
            ? count
            : null;
    }

    private static string? DbString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static int? DbInt(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetInt32(index);

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

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

    private static WikiQuestRow MergeQuestRows(IEnumerable<WikiQuestRow> source)
    {
        var rows = source.ToList();
        var preferred = rows
            .OrderByDescending(row => row.IsIndividualPage)
            .ThenByDescending(row => row.Objectives.Count)
            .First();
        var fallback = rows
            .OrderByDescending(row => !string.IsNullOrWhiteSpace(row.Trader))
            .ThenByDescending(row => !string.IsNullOrWhiteSpace(row.Map))
            .First();

        return preferred with
        {
            Trader = string.IsNullOrWhiteSpace(preferred.Trader) ? fallback.Trader : preferred.Trader,
            Map = string.IsNullOrWhiteSpace(preferred.Map) ? fallback.Map : preferred.Map,
            WikiLink = string.IsNullOrWhiteSpace(preferred.WikiLink) ? fallback.WikiLink : preferred.WikiLink
        };
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

    private sealed record FandomPageResponse(string Title, string Url, string Html, long RevisionId);
    private sealed record QuestParseResult(List<WikiQuestRow> Rows, List<string> CollectorItems);
    private sealed record WikiPageRevision(string Title, long RevisionId, DateTimeOffset UpdatedAtUtc);
    private sealed record WikiSyncStateSnapshot(
        IReadOnlyDictionary<string, long> Revisions,
        DateTimeOffset? LastSuccessfulSyncUtc);
    private sealed record QuestPageFetchResult(
        List<WikiQuestRow> Rows,
        List<WikiPageRevision> ProcessedPages);
    private sealed record InfoboxField(bool Exists, string Value);
    private sealed record WikiQuestRow(
        string Name,
        string Trader,
        string Map,
        string WikiLink,
        List<string> Objectives,
        List<string>? Previous = null,
        bool IsIndividualPage = false,
        bool HasPreviousField = false,
        long? RevisionId = null,
        bool? KappaRequired = null,
        List<string>? RawObjectives = null,
        List<WikiPrerequisite>? Prerequisites = null,
        bool PrerequisitesResolved = true,
        int? MinimumLevel = null,
        List<string>? UnverifiedRequirements = null)
    {
        public IReadOnlyList<string> PreviousQuestNames =>
            Previous is null ? Array.Empty<string>() : Previous;
        public IReadOnlyList<WikiPrerequisite> PrerequisiteLinks =>
            Prerequisites is null
                ? PreviousQuestNames.Select(name => new WikiPrerequisite(name, 0)).ToList()
                : Prerequisites;
        public IReadOnlyList<string> RawObjectiveTexts =>
            RawObjectives is null ? Array.Empty<string>() : RawObjectives;
        public IReadOnlyList<string> UnverifiedRequirementTexts =>
            UnverifiedRequirements is null ? Array.Empty<string>() : UnverifiedRequirements;
    }
    private sealed record ExistingQuest(string Id, string Name, string? BsgId, bool IsStructured);
    private sealed record ExistingObjectiveMetadata(
        string Id,
        string ObjectiveType,
        string Description,
        string? TargetType,
        int? TargetCount,
        string? ItemId,
        string? ItemName,
        bool RequiresFir,
        string? MapName,
        string? LocationName,
        string? LocationPoints,
        string? OptionalPoints,
        string? Conditions,
        int? DogtagMinLevel,
        string? DogtagFaction,
        string? ContentHash,
        bool IsApproved,
        string? ApprovedAt);
    private sealed record OverlayStats(
        int Added,
        int Updated,
        int ObjectivesFilled,
        int Prerequisites,
        int RequiredItems,
        int CollectorItems);
}

public sealed record WikiQuestRefreshResult(
    int WikiQuestCount,
    int AddedQuestCount,
    int UpdatedQuestCount,
    int ObjectivesFilledCount,
    int IndividualPageCount,
    int PrerequisiteCount,
    int RequiredItemCount,
    string BackupPath,
    bool WasChanged);
