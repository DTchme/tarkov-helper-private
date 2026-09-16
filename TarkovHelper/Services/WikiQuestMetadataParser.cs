using System.Net;
using System.Text.RegularExpressions;

namespace TarkovHelper.Services;

/// <summary>
/// Parses the small amount of structured metadata that the English Wiki embeds in
/// quest infoboxes and objective text. Wiki text remains authoritative; this parser
/// only turns explicit wording into fields used by the rest of the application.
/// </summary>
public static class WikiQuestMetadataParser
{
    private static readonly Regex WikiLinkRegex = new(
        @"\[\[(?<target>[^\]|#]+)(?:#[^\]|]*)?(?:\|(?<label>[^\]]+))?\]\]",
        RegexOptions.Compiled);

    private static readonly Regex ItemActionRegex = new(
        @"\b(?<action>hand\s+over|deliver|give|turn\s+in|stash|plant|place)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static WikiPrerequisiteParseResult ParsePrerequisites(
        string? wikiText,
        string currentQuestName)
    {
        if (string.IsNullOrWhiteSpace(wikiText))
            return new WikiPrerequisiteParseResult(Array.Empty<WikiPrerequisite>(), true);

        var links = WikiLinkRegex.Matches(wikiText)
            .Cast<Match>()
            .Select(match => new LinkToken(
                match,
                CleanWikiText(match.Groups["label"].Success
                    ? match.Groups["label"].Value
                    : match.Groups["target"].Value)))
            .Where(token =>
                !string.IsNullOrWhiteSpace(token.Name) &&
                !token.Name.Equals("See requirements", StringComparison.OrdinalIgnoreCase) &&
                !token.Name.Equals(currentQuestName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var remainingText = WikiLinkRegex.Replace(wikiText, " ");
        remainingText = Regex.Replace(remainingText, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        remainingText = Regex.Replace(remainingText, @"\{\{[^{}]*\}\}", " ");
        remainingText = Regex.Replace(
            CleanWikiText(remainingText),
            @"\b(?:and|or|after|complete|completed|completing|completion|of|none|n/a)\b|[/,;:&+\-]",
            " ",
            RegexOptions.IgnoreCase);
        var isResolved = string.IsNullOrWhiteSpace(Regex.Replace(remainingText, @"\s+", " ").Trim());

        if (links.Count == 0)
            return new WikiPrerequisiteParseResult(Array.Empty<WikiPrerequisite>(), isResolved);

        var parents = Enumerable.Range(0, links.Count).ToArray();
        for (var index = 0; index < links.Count - 1; index++)
        {
            var left = links[index].Match;
            var right = links[index + 1].Match;
            var separator = wikiText.Substring(
                left.Index + left.Length,
                right.Index - (left.Index + left.Length));
            separator = CleanWikiText(separator);
            if (Regex.IsMatch(separator, @"\bor\b|/", RegexOptions.IgnoreCase))
                Union(parents, index, index + 1);
        }

        var componentSizes = Enumerable.Range(0, links.Count)
            .GroupBy(index => Find(parents, index))
            .ToDictionary(group => group.Key, group => group.Count());
        var groupIds = new Dictionary<int, int>();
        var nextGroupId = 1;
        var result = new List<WikiPrerequisite>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < links.Count; index++)
        {
            if (!seen.Add(links[index].Name))
                continue;

            var root = Find(parents, index);
            var groupId = 0;
            if (componentSizes[root] > 1)
            {
                if (!groupIds.TryGetValue(root, out groupId))
                {
                    groupId = nextGroupId++;
                    groupIds[root] = groupId;
                }
            }

            result.Add(new WikiPrerequisite(links[index].Name, groupId));
        }

        return new WikiPrerequisiteParseResult(result, isResolved);
    }

    public static WikiRequirementSummary ParseRequirements(IEnumerable<string> requirements)
    {
        int? minimumLevel = null;
        var unverified = new List<string>();

        foreach (var rawRequirement in requirements)
        {
            var requirement = CleanWikiText(rawRequirement);
            if (string.IsNullOrWhiteSpace(requirement))
                continue;

            var levelMatch = Regex.Match(
                requirement,
                @"\b(?:be|reach)(?:\s+PMC)?\s+level\s+(?<level>\d+)\b",
                RegexOptions.IgnoreCase);
            if (levelMatch.Success && int.TryParse(levelMatch.Groups["level"].Value, out var level))
            {
                minimumLevel = minimumLevel.HasValue
                    ? Math.Max(minimumLevel.Value, level)
                    : level;

                var remainder = requirement.Remove(levelMatch.Index, levelMatch.Length);
                remainder = Regex.Replace(
                    remainder,
                    @"\b(?:must|to|obtain|this|quest|and)\b|[.,;:()\-]",
                    " ",
                    RegexOptions.IgnoreCase);
                if (string.IsNullOrWhiteSpace(Regex.Replace(remainder, @"\s+", " ").Trim()))
                    continue;
            }

            unverified.Add(requirement);
        }

        return new WikiRequirementSummary(
            minimumLevel,
            unverified.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public static IReadOnlyList<WikiRequiredItem> ParseRequiredItems(
        IEnumerable<string> rawObjectives,
        IEnumerable<WikiItemCatalogEntry> itemCatalog)
    {
        var catalog = itemCatalog
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new CatalogItem(item.Id, item.Name, Normalize(item.Name)))
            .Where(item => item.NormalizedName.Length >= 4)
            .OrderByDescending(item => item.NormalizedName.Length)
            .ToList();
        var parsed = new List<WikiRequiredItem>();

        foreach (var rawObjective in rawObjectives)
        {
            var cleanObjective = CleanWikiText(rawObjective);
            var actionMatch = ItemActionRegex.Match(cleanObjective);
            if (!actionMatch.Success)
                continue;

            var normalizedObjective = Normalize(cleanObjective);
            var matchedItem = catalog.FirstOrDefault(item =>
                normalizedObjective.Contains(item.NormalizedName, StringComparison.OrdinalIgnoreCase));
            if (matchedItem == null)
                continue;

            var countMatch = Regex.Match(
                cleanObjective[actionMatch.Index..],
                @"^(?:hand\s+over|deliver|give|turn\s+in|stash|plant|place)\s+(?:any\s+)?(?<count>\d[\d,]*)?",
                RegexOptions.IgnoreCase);
            var count = countMatch.Success &&
                        int.TryParse(countMatch.Groups["count"].Value.Replace(",", string.Empty), out var parsedCount)
                ? Math.Max(1, parsedCount)
                : 1;
            var requiresFir = Regex.IsMatch(cleanObjective, @"\bfound\s+in\s+raid\b|\bin\s+raid\b", RegexOptions.IgnoreCase);
            var action = Regex.Replace(actionMatch.Groups["action"].Value, @"\s+", " ").ToLowerInvariant();
            var requirementType = action is "stash" or "plant" or "place" ? "Stash" : "Handover";

            parsed.Add(new WikiRequiredItem(
                matchedItem.Id,
                matchedItem.Name,
                count,
                requiresFir,
                requirementType));
        }

        return parsed
            .GroupBy(
                item => $"{item.ItemId}\u001f{item.RequirementType}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToList();
                if (entries[0].RequirementType == "Handover")
                {
                    return new WikiRequiredItem(
                        entries[0].ItemId,
                        entries[0].ItemName,
                        entries.Max(item => item.Count),
                        entries.Any(item => item.RequiresFir),
                        "Handover");
                }

                return new WikiRequiredItem(
                    entries[0].ItemId,
                    entries[0].ItemName,
                    entries.Sum(item => item.Count),
                    entries.Any(item => item.RequiresFir),
                    "Stash");
            })
            .ToList();
    }

    public static bool IsRequiredItemObjective(string? rawObjective) =>
        !string.IsNullOrWhiteSpace(rawObjective) && ItemActionRegex.IsMatch(CleanWikiText(rawObjective));

    private static string CleanWikiText(string value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<ref\b[^>]*>.*?</ref>|<ref\b[^>]*/>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        text = WikiLinkRegex.Replace(text, match =>
            match.Groups["label"].Success
                ? match.Groups["label"].Value
                : match.Groups["target"].Value);
        text = Regex.Replace(text, @"\{\{[^{}]*\}\}", " ");
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
        return Regex.Replace(text, @"\s+", " ").Trim().TrimEnd('.');
    }

    private static string Normalize(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);

    private static int Find(int[] parents, int value)
    {
        while (parents[value] != value)
        {
            parents[value] = parents[parents[value]];
            value = parents[value];
        }
        return value;
    }

    private static void Union(int[] parents, int left, int right)
    {
        var leftRoot = Find(parents, left);
        var rightRoot = Find(parents, right);
        if (leftRoot != rightRoot)
            parents[rightRoot] = leftRoot;
    }

    private sealed record LinkToken(Match Match, string Name);
    private sealed record CatalogItem(string Id, string Name, string NormalizedName);
}

public sealed record WikiPrerequisite(string Name, int GroupId);
public sealed record WikiPrerequisiteParseResult(
    IReadOnlyList<WikiPrerequisite> Prerequisites,
    bool IsResolved);
public sealed record WikiRequirementSummary(
    int? MinimumLevel,
    IReadOnlyList<string> UnverifiedRequirements);
public sealed record WikiItemCatalogEntry(string Id, string Name);
public sealed record WikiRequiredItem(
    string ItemId,
    string ItemName,
    int Count,
    bool RequiresFir,
    string RequirementType);
