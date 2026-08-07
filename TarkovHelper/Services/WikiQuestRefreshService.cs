using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Uses the actively maintained Japanese EFT Wiki as the authoritative quest-presence/basic-info source.
/// tarkov.dev remains useful for BSG ids and structured item metadata, but wiki data wins for quest
/// existence, trader, map and missing objective text.
/// </summary>
public sealed class WikiQuestRefreshService
{
    private const string WikiBase = "https://wikiwiki.jp/eft/";
    private static readonly ILogger _log = Log.For<WikiQuestRefreshService>();

    private static readonly (string Trader, string Page)[] TraderPages =
    {
        ("Prapor", "Prapor"),
        ("Therapist", "Therapist"),
        ("Fence", "Fence"),
        ("Skier", "Skier"),
        ("Peacekeeper", "Peacekeeper"),
        ("Mechanic", "Mechanic"),
        ("Ragman", "Ragman"),
        ("Jaeger", "Jaeger"),
        ("Lightkeeper", "Lightkeeper"),
        ("Ref", "Ref"),
        ("BTR Driver", "BTR%20Driver")
    };

    private readonly HttpClient _httpClient;

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

        var pages = await FetchTraderPagesAsync(cancellationToken);
        var rows = new List<WikiQuestRow>();
        foreach (var page in pages)
            rows.AddRange(ParseQuestTable(page.Trader, page.Url, page.Html));

        var storyPage = await FetchPageAsync("Story", Uri.EscapeDataString("ストーリータスク"), cancellationToken);
        rows.AddRange(ParseStoryQuestLinks(storyPage.Url, storyPage.Html));

        // Season 1 uses a separate Seasonal PvP character. The Japanese Wiki's season page
        // is still being populated, so harvest KORD BREACH links opportunistically without
        // making this page a hard dependency for the normal quest refresh.
        try
        {
            var seasonalPage = await FetchPageAsync("Season", Uri.EscapeDataString("シーズンアカウント"), cancellationToken);
            rows.AddRange(ParseSeasonQuestLinks(seasonalPage.Url, seasonalPage.Html));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning($"Season Wiki page unavailable; continuing with normal quests: {ex.Message}");
        }

        rows = rows
            .GroupBy(r => (NormalizeQuestName(r.Name), r.Trader), StringTupleComparer.Instance)
            .Select(g => g.OrderByDescending(x => x.Objectives.Count).First())
            .ToList();

        if (rows.Count < 250)
            throw new InvalidOperationException($"Wiki 퀘스트 수가 비정상적으로 적습니다 ({rows.Count}). 기존 DB를 유지합니다.");

        var databaseDirectory = Path.GetDirectoryName(databasePath) ?? AppDomain.CurrentDomain.BaseDirectory;
        var tempPath = Path.Combine(databaseDirectory, $"tarkov_data.wiki.{Guid.NewGuid():N}.tmp");
        File.Copy(databasePath, tempPath, true);
        try
        {
            var stats = await ApplyWikiOverlayAsync(tempPath, rows, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var backupDirectory = Path.Combine(databaseDirectory, "Backups");
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, $"tarkov_data_before_wiki_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");
            SqliteConnection.ClearAllPools();
            File.Copy(databasePath, backupPath, true);
            File.Move(tempPath, databasePath, true);
            SqliteConnection.ClearAllPools();

            _log.Info($"Wiki quest overlay completed: wiki={rows.Count}, added={stats.Added}, updated={stats.Updated}, objectivesFilled={stats.ObjectivesFilled}");
            return new WikiQuestRefreshResult(rows.Count, stats.Added, stats.Updated, stats.ObjectivesFilled, backupPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private async Task<List<WikiPageResponse>> FetchTraderPagesAsync(CancellationToken cancellationToken)
    {
        var result = new List<WikiPageResponse>();
        using var gate = new SemaphoreSlim(4, 4);

        var tasks = TraderPages.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var url = WikiBase + item.Page;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("TarkovHelper/1.5.10 (+wiki quest sync)");
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"EFT Wiki {item.Trader} 페이지 요청 실패: {(int)response.StatusCode} {response.ReasonPhrase}");
                return new WikiPageResponse(item.Trader, url, html);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        foreach (var page in await Task.WhenAll(tasks))
            result.Add(page);
        return result;
    }

    private async Task<WikiPageResponse> FetchPageAsync(string trader, string page, CancellationToken cancellationToken)
    {
        var url = WikiBase + page;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("TarkovHelper/1.5.10 (+wiki quest sync)");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"EFT Wiki {trader} 페이지 요청 실패: {(int)response.StatusCode} {response.ReasonPhrase}");
        return new WikiPageResponse(trader, url, html);
    }

    private static IEnumerable<WikiQuestRow> ParseStoryQuestLinks(string pageUrl, string html)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(html, "<a\\b[^>]*href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (!href.Contains("ストーリータスク/", StringComparison.OrdinalIgnoreCase) &&
                !href.Contains("%E3%82%B9%E3%83%88%E3%83%BC%E3%83%AA%E3%83%BC%E3%82%BF%E3%82%B9%E3%82%AF/", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = CleanHtml(match.Groups["text"].Value);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || !seen.Add(name))
                continue;

            var wikiLink = pageUrl;
            if (Uri.TryCreate(new Uri(WikiBase), href, out var absolute))
                wikiLink = absolute.ToString();
            yield return new WikiQuestRow(name, "Story", string.Empty, wikiLink, new List<string>());
        }
    }

    private static IEnumerable<WikiQuestRow> ParseSeasonQuestLinks(string pageUrl, string html)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(html, "<a\\b[^>]*href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var name = CleanHtml(match.Groups["text"].Value);
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            var seasonal = name.StartsWith("[KORD BREACH]", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith("KORD BREACH:", StringComparison.OrdinalIgnoreCase);
            if (!seasonal || string.IsNullOrWhiteSpace(name) || name.Length > 160 || !seen.Add(name))
                continue;

            var wikiLink = pageUrl;
            if (Uri.TryCreate(new Uri(WikiBase), href, out var absolute))
                wikiLink = absolute.ToString();

            yield return new WikiQuestRow(name, "Season", string.Empty, wikiLink, new List<string>());
        }
    }

    private static IEnumerable<WikiQuestRow> ParseQuestTable(string trader, string pageUrl, string html)
    {
        foreach (Match rowMatch in Regex.Matches(html, @"<tr\b[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var cells = Regex.Matches(rowMatch.Groups["row"].Value, @"<t[dh]\b[^>]*>(?<cell>.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Cast<Match>()
                .Select(m => m.Groups["cell"].Value)
                .ToList();
            if (cells.Count < 3)
                continue;

            var firstText = CleanHtml(cells[0]);
            if (string.IsNullOrWhiteSpace(firstText) ||
                firstText.Equals("タイトル", StringComparison.OrdinalIgnoreCase) ||
                firstText.Equals("Title", StringComparison.OrdinalIgnoreCase))
                continue;

            var linkMatch = Regex.Match(cells[0], "href\\s*=\\s*[\"'](?<href>[^\"']+)[\"']", RegexOptions.IgnoreCase);
            var wikiLink = pageUrl;
            if (linkMatch.Success)
            {
                var href = WebUtility.HtmlDecode(linkMatch.Groups["href"].Value);
                if (Uri.TryCreate(new Uri(WikiBase), href, out var absolute))
                    wikiLink = absolute.ToString();
            }

            var map = NormalizeMap(CleanHtml(cells[1]));
            var objectives = ExtractListItems(cells[2]);
            if (objectives.Count == 0)
            {
                var fallback = CleanHtml(cells[2]);
                if (!string.IsNullOrWhiteSpace(fallback))
                    objectives.Add(fallback);
            }

            yield return new WikiQuestRow(firstText.Trim(), trader, map, wikiLink, objectives);
        }
    }

    private static List<string> ExtractListItems(string html)
    {
        var result = new List<string>();
        foreach (Match match in Regex.Matches(html, @"<li\b[^>]*>(?<item>.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
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
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string NormalizeMap(string map)
    {
        if (string.IsNullOrWhiteSpace(map))
            return string.Empty;
        var upper = map.ToUpperInvariant();
        var known = new[] { "GROUND ZERO", "STREETS OF TARKOV", "INTERCHANGE", "CUSTOMS", "FACTORY", "WOODS", "SHORELINE", "RESERVE", "LIGHTHOUSE", "THE LAB", "THE LABYRINTH", "TERMINAL", "ICEBREAKER" };
        return string.Join(", ", known.Where(k => upper.Contains(k, StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<OverlayStats> ApplyWikiOverlayAsync(
        string databasePath,
        IReadOnlyList<WikiQuestRow> rows,
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
            var existing = new Dictionary<string, ExistingQuest>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new SqliteCommand("SELECT Id, NameEN, Name, Trader, Location FROM Quests", connection, tx))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetString(0);
                    var name = !reader.IsDBNull(1) ? reader.GetString(1) : reader.GetString(2);
                    var key = NormalizeQuestName(name);
                    if (!existing.ContainsKey(key))
                        existing[key] = new ExistingQuest(id, name);
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
                string questId;

                if (existing.TryGetValue(key, out var current))
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
                    questId = "wiki_" + StableHash(row.Trader + "|" + row.Name);
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
                    existing[key] = new ExistingQuest(questId, row.Name);
                    added++;
                }

                if (row.Objectives.Count == 0)
                    continue;

                var existingObjectiveCount = 0;
                await using (var count = new SqliteCommand("SELECT COUNT(*) FROM QuestObjectives WHERE QuestId=@id", connection, tx))
                {
                    count.Parameters.AddWithValue("@id", questId);
                    existingObjectiveCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
                }

                // Keep tarkov.dev structured objectives when they exist. Wiki becomes authoritative
                // for missing quests/objective-less rows, avoiding duplicate objective lists and
                // preserving existing map-marker coordinates.
                if (existingObjectiveCount > 0)
                    continue;

                var sort = 0;
                foreach (var objective in row.Objectives)
                {
                    await using var insertObjective = new SqliteCommand(@"
                        INSERT INTO QuestObjectives (
                            Id, QuestId, SortOrder, ObjectiveType, Description,
                            TargetCount, RequiresFIR, MapName, IsApproved, UpdatedAt)
                        VALUES (@id, @questId, @sort, 'Wiki', @description,
                            NULL, 0, @map, 0, @updatedAt)", connection, tx);
                    insertObjective.Parameters.AddWithValue("@id", "wikiobj_" + StableHash(questId + "|" + sort + "|" + objective));
                    insertObjective.Parameters.AddWithValue("@questId", questId);
                    insertObjective.Parameters.AddWithValue("@sort", sort++);
                    insertObjective.Parameters.AddWithValue("@description", objective);
                    insertObjective.Parameters.AddWithValue("@map", string.IsNullOrWhiteSpace(row.Map) ? DBNull.Value : row.Map);
                    insertObjective.Parameters.AddWithValue("@updatedAt", updatedAt);
                    await insertObjective.ExecuteNonQueryAsync(cancellationToken);
                    objectivesFilled++;
                }
            }

            await tx.CommitAsync(cancellationToken);
            return new OverlayStats(added, updated, objectivesFilled);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static string NormalizeQuestName(string value)
    {
        value = WebUtility.HtmlDecode(value ?? string.Empty).Trim().ToLowerInvariant();
        value = value.Replace('–', '-').Replace('—', '-').Replace('’', '\'').Replace('“', '"').Replace('”', '"');
        return Regex.Replace(value, @"[^a-z0-9]+", string.Empty);
    }

    private static string StableHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }

    private sealed record WikiPageResponse(string Trader, string Url, string Html);
    private sealed record WikiQuestRow(string Name, string Trader, string Map, string WikiLink, List<string> Objectives);
    private sealed record ExistingQuest(string Id, string Name);
    private sealed record OverlayStats(int Added, int Updated, int ObjectivesFilled);

    private sealed class StringTupleComparer : IEqualityComparer<(string Name, string Trader)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string Name, string Trader) x, (string Name, string Trader) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name) && StringComparer.OrdinalIgnoreCase.Equals(x.Trader, y.Trader);
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
