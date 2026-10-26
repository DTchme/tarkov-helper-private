using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

public sealed record HideoutWikiItem(string Name, int Count, bool FoundInRaid);
public sealed record HideoutWikiLevel(int Level, List<HideoutWikiItem> Items);
public sealed record HideoutWikiStation(string Name, List<HideoutWikiLevel> Levels);
public sealed record HideoutSeasonRule(string Name, DateOnly StartsOn, DateOnly EndsOnExclusive, long SourceRevision);
public sealed record HideoutWikiSnapshot(int FormatVersion, long Revision, string SourceUpdatedAt,
    List<HideoutWikiStation> Stations, HideoutSeasonRule? SeasonRule);

public static class HideoutWikiParser
{
    public const int FormatVersion = 1;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static string Key(string value) => string.Concat(WebUtility.HtmlDecode(Uri.UnescapeDataString(value))
        .Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string Clean(string value)
    {
        value = Regex.Replace(value, @"\[\[([^]|]+)\|([^]]+)\]\]", "$2", RegexOptions.None, Timeout);
        value = Regex.Replace(value, @"\[\[([^]]+)\]\]", "$1", RegexOptions.None, Timeout);
        return WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", "", RegexOptions.None, Timeout)).Trim();
    }

    public static HideoutWikiSnapshot ParseApiResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var pages = document.RootElement.GetProperty("query").GetProperty("pages").EnumerateArray()
            .ToDictionary(page => page.GetProperty("title").GetString()!, page => page);
        (string Text, long Revision, string Timestamp) Read(string name)
        {
            var revision = pages[name].GetProperty("revisions")[0];
            return (revision.GetProperty("slots").GetProperty("main").GetProperty("content").GetString()!,
                revision.GetProperty("revid").GetInt64(), revision.GetProperty("timestamp").GetString()!);
        }
        var hideout = Read("Hideout");
        var seasons = Read("Seasons");
        var template = Read("Template:Seasonal change");
        return new(FormatVersion, hideout.Revision, hideout.Timestamp, ParseStations(hideout.Text),
            ParseSeasonRule(hideout.Text, template.Text, seasons.Text, seasons.Revision));
    }

    public static List<HideoutWikiStation> ParseStations(string text)
    {
        var stations = new List<HideoutWikiStation>();
        foreach (Match table in Regex.Matches(text,
            "\\{\\| class=\"wikitable\"\\s*\\n! colspan=4 \\|([^\\n]+)\\n(.*?)\\n\\|\\}", RegexOptions.Singleline, Timeout))
        {
            var name = Clean(table.Groups[1].Value.Split('<')[0]);
            // A limited holiday module must not become a permanent requirement.
            if (Key(name) == "christmastree") continue;
            var levels = new List<HideoutWikiLevel>();
            foreach (var row in Regex.Split(table.Groups[2].Value, @"\n\|-\s*\n", RegexOptions.None, Timeout))
            {
                var level = Regex.Match(row, @"^!\s*(\d+)\s*\n", RegexOptions.None, Timeout);
                if (!level.Success) continue;
                var columns = Regex.Split(row, @"\n\|", RegexOptions.None, Timeout);
                if (columns.Length < 2) throw new InvalidDataException($"Wiki 요구 사항 열 누락: {name}");
                var items = new List<HideoutWikiItem>();
                var optional = false;
                foreach (var line in columns[1].Split('\n').Select(line => line.TrimEnd('\r')))
                {
                    if (!line.StartsWith('*') || line.StartsWith("**")) continue;
                    if (line.Contains("Optional Task:", StringComparison.OrdinalIgnoreCase)) { optional = true; continue; }
                    if (optional) continue;
                    var item = Regex.Match(line, @"^\*\s*([\d,]+)\s+\[\[([^]|]+)(?:\|[^]]*)?\]\](.*)", RegexOptions.None, Timeout);
                    if (!item.Success)
                    {
                        if (Regex.IsMatch(line, @"^\*\s*\d", RegexOptions.None, Timeout))
                            throw new InvalidDataException($"해석하지 못한 Wiki 재료: {name}: {Clean(line)}");
                        continue; // Edition, skill, trader and station prerequisites are not material quantities.
                    }
                    var count = int.Parse(item.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
                    if (count <= 0) throw new InvalidDataException($"잘못된 Wiki 재료 수량: {name}");
                    var tail = item.Groups[3].Value;
                    if (tail.Contains("{{") && !Regex.IsMatch(tail, @"\{\{\s*FIR\s*\}\}", RegexOptions.IgnoreCase, Timeout))
                        throw new InvalidDataException($"새로운 Wiki 재료 조건은 재확인이 필요합니다: {name}");
                    var fir = Regex.IsMatch(tail, @"\bfound[\s_]+in[\s_]+raid\b|\{\{\s*FIR\s*\}\}", RegexOptions.IgnoreCase, Timeout);
                    items.Add(new(Clean(item.Groups[2].Value), count, fir));
                }
                levels.Add(new(int.Parse(level.Groups[1].Value, CultureInfo.InvariantCulture), items));
            }
            if (levels.Count > 0) stations.Add(new(name, levels));
        }
        if (stations.Count == 0) throw new InvalidDataException("Wiki 은신처 표를 읽지 못했습니다. 기존 자료를 유지합니다.");
        if (stations.Select(station => Key(station.Name)).Distinct().Count() != stations.Count)
            throw new InvalidDataException("Wiki 시설 표가 중복되어 기존 자료를 유지합니다.");
        return stations;
    }

    public static HideoutSeasonRule? ParseSeasonRule(string hideout, string template, string seasons, long revision)
    {
        if (!Regex.IsMatch(hideout, @"\{\{Seasonal change\|no items required for module upgrades need to be found in raid\.", RegexOptions.IgnoreCase, Timeout))
            return null;
        var title = Regex.Match(template, @"\[\[Seasons#[^|]+\|([^]]+)\]\]", RegexOptions.None, Timeout);
        foreach (Match section in Regex.Matches(seasons, @"(?:^|\n)==([^=\n]+)==\s*\n(.*?)(?=\n==[^=]|\z)", RegexOptions.Singleline, Timeout))
        {
            if (!title.Success || Key(section.Groups[1].Value) != Key(title.Groups[1].Value)) continue;
            var body = Clean(section.Groups[2].Value);
            var dates = Regex.Match(body, @"started on (\w+ \d{1,2}, \d{4}) and ends on (\w+ \d{1,2}, \d{4})", RegexOptions.IgnoreCase, Timeout);
            if (dates.Success && Key(body).Contains("hideoutzonesdonotrequireitemstobefoundinraid") &&
                DateOnly.TryParseExact(dates.Groups[1].Value, "MMMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) &&
                DateOnly.TryParseExact(dates.Groups[2].Value, "MMMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) && start < end)
                return new(Clean(section.Groups[1].Value), start, end, revision);
        }
        throw new InvalidDataException("Wiki 시즌 FIR 예외의 적용 기간을 검증하지 못했습니다. 기존 자료를 유지합니다.");
    }

    public static bool RequiresFir(bool baseRequirement, ProfileType profile, HideoutSeasonRule? rule, DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        var waived = profile == ProfileType.SeasonalPvp && rule != null && date >= rule.StartsOn && date < rule.EndsOnExclusive;
        return baseRequirement && !waived;
    }
}
