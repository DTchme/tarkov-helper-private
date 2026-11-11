using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovHelper.Services;

/// <summary>Compares the part of a Wiki revision that can invalidate map data.</summary>
internal static partial class WikiMapEvidence
{
    internal const string ReviewedAt = "2026-11-11";
    internal sealed record Page(string Title, long Revision, string Content);
    internal sealed record Evidence(long Revision, string Fingerprint, string? CalibrationFingerprint);

    private static readonly Lazy<Dictionary<string, Evidence>> Bundled = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, Evidence>>(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Assets", "DB", "Data", "wiki_map_evidence.json")))
        ?? throw new InvalidDataException("지도 Wiki 검증 자료가 비어 있습니다."));

    internal static Evidence Baseline(string title) => Bundled.Value[title];

    internal static IEnumerable<Page> ReadPages(string response)
    {
        using var json = JsonDocument.Parse(response);
        foreach (var page in json.RootElement.GetProperty("query").GetProperty("pages").EnumerateArray())
        {
            if (!page.TryGetProperty("revisions", out var revisions)) continue;
            var title = page.GetProperty("title").GetString()!;
            foreach (var revision in revisions.EnumerateArray())
                yield return new Page(title, revision.GetProperty("revid").GetInt64(),
                    revision.GetProperty("slots").GetProperty("main").GetProperty("content").GetString()
                    ?? throw new InvalidDataException($"Wiki 본문 없음: {title}"));
        }
    }

    internal static Evidence Describe(Page page)
    {
        if (page.Title.StartsWith("Map:", StringComparison.Ordinal))
        {
            using var doc = JsonDocument.Parse(page.Content);
            var root = doc.RootElement;
            var markers = root.GetProperty("markers").EnumerateArray().ToArray();
            string HashMarkers(Func<string?, bool> match)
            {
                // Frame changes matter even if the marker's local x/y is unchanged.
                var frame = new[] { "mapImage", "coordinateOrder", "mapBounds", "origin" }
                    .Select(key => JsonSerializer.Serialize(root.GetProperty(key)));
                var rows = markers.Where(marker => match(marker.GetProperty("categoryId").GetString()))
                    .Select(marker => JsonSerializer.Serialize(new[] {
                        marker.GetProperty("categoryId"), marker.GetProperty("position"), marker.GetProperty("popup") }))
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (rows.Length == 0) throw new InvalidDataException($"Wiki 지도 마커 없음: {page.Title}");
                return Hash(string.Join('\n', frame.Concat(rows)));
            }

            return new Evidence(page.Revision, HashMarkers(category => category == "exfil_transit"),
                HashMarkers(category => category is "exfil_pmc" or "exfil_scav"));
        }

        if (page.Title is "Customs" or "Lighthouse")
        {
            var section = Section(page.Content, "Extractions");
            if (section == null || !section.Contains("{|", StringComparison.Ordinal))
                throw new InvalidDataException($"Wiki 탈출구 표 없음: {page.Title}");
            return new Evidence(page.Revision, Hash(section), null);
        }

        var fingerprint = QuestFingerprint(page.Content)
            ?? throw new InvalidDataException($"Wiki 퀘스트 목표/공략 없음: {page.Title}");
        return new Evidence(page.Revision, fingerprint, null);
    }

    internal static string? QuestFingerprint(string wikiText)
    {
        var objectives = Section(wikiText, "Objectives");
        var guide = Section(wikiText, "Guide");
        if (objectives == null || guide == null) return null;
        var location = InfoboxLocation().Match(wikiText);
        return Hash($"{location.Groups[1].Value.Trim()}\n{objectives}\n{guide}");
    }

    private static string? Section(string wikiText, string title)
    {
        var sections = Heading().Matches(wikiText);
        for (var i = 0; i < sections.Count; i++)
        {
            var start = sections[i];
            if (!start.Groups[1].Value.Trim().Equals(title, StringComparison.OrdinalIgnoreCase)) continue;
            var end = i + 1 < sections.Count ? sections[i + 1].Index : wikiText.Length;
            var body = Comments().Replace(wikiText[(start.Index + start.Length)..end], "");
            // Trailing article categories and language links are not part of a guide.
            return string.Join('\n', body.Split('\n').Select(line => line.Trim()).Where(line =>
                line.Length != 0 && !ArticleMetadata().IsMatch(line)));
        }
        return null;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex(@"(?m)^[ \t]*==(?!=)[ \t]*([^=\r\n]+?)==(?!=)[ \t]*\r?$")]
    private static partial Regex Heading();
    [GeneratedRegex(@"(?mi)^[ \t]*\|[ \t]*location[ \t]*=[ \t]*(.*)$")]
    private static partial Regex InfoboxLocation();
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();
    [GeneratedRegex(@"^\[\[(Category|de|es|fr|it|ja|ko|pl|pt|ru|tr|uk|zh(?:-[a-z]+)?):", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleMetadata();
}
