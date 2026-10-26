using System.Text.RegularExpressions;
using TarkovHelper.Models.Map;

namespace TarkovHelper.Services;

public sealed record ObjectiveMetadataCandidate(string Description, string Type, string? Map, string? ItemName);

/// <summary>Conservative identity matching; uncertain goals do not inherit IDs or coordinates.</summary>
public static class ObjectiveMetadataMatcher
{
    private const string MapPattern = "Streets of Tarkov|Ground Zero|The Labyrinth|The Lab|Customs|Factory|Interchange|Lighthouse|Reserve|Shoreline|Woods|Icebreaker|Terminal";

    public static int FindMatch(string translated, string original, IReadOnlyList<ObjectiveMetadataCandidate> candidates)
    {
        var text = Normalize(translated);
        var raw = Normalize(original);
        var exact = Enumerable.Range(0, candidates.Count)
            .Where(index => Normalize(candidates[index].Description) is var candidate && (candidate == text || candidate == raw))
            .ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return -1;

        var type = InferType(original);
        var ordinal = Ordinals(original);
        var map = InferMap(original);
        var matches = new List<(int Index, double Score)>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var candidateType = InferType(candidate.Description);
            if (candidateType == "Custom") candidateType = candidate.Type;
            if (type == "Custom" || !type.Equals(candidateType, StringComparison.OrdinalIgnoreCase)) continue;
            if (ordinal != Ordinals(candidate.Description)) continue;
            if (map != null && candidate.Map != null &&
                !MapConfig.SplitMapExpression(candidate.Map).Any(value => MapConfig.MapNamesMatch(value, map))) continue;
            if (!string.IsNullOrWhiteSpace(candidate.ItemName) &&
                !Normalize(original).Replace(" ", "").Contains(Normalize(candidate.ItemName).Replace(" ", ""), StringComparison.Ordinal)) continue;
            var score = Math.Max(Similarity(text, Normalize(candidate.Description)), Similarity(raw, Normalize(candidate.Description)));
            if (score >= 0.82) matches.Add((index, score));
        }
        var ranked = matches.OrderByDescending(match => match.Score).ToList();
        return ranked.Count > 0 && (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= 0.15)
            ? ranked[0].Index : -1;
    }

    public static string? InferMap(string text)
    {
        var matches = Regex.Matches(text, @"\b(?:on|in)\s+(?:the\s+)?(" + MapPattern + @")\b", RegexOptions.IgnoreCase);
        if (matches.Count > 0) return CanonicalMap(matches[^1].Groups[1].Value);
        var transit = Regex.Match(text, @"\b(?:transit|extract)\s+from\s+(" + MapPattern + @")\b", RegexOptions.IgnoreCase);
        return transit.Success ? CanonicalMap(transit.Groups[1].Value) : null;
    }

    private static string CanonicalMap(string value) => value.ToLowerInvariant() switch
    {
        "streets of tarkov" => "StreetsOfTarkov", "ground zero" => "GroundZero",
        "the lab" => "The Lab", "the labyrinth" => "The Labyrinth",
        _ => char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant()
    };

    public static string InferType(string text)
    {
        var value = text.ToLowerInvariant();
        if (Regex.IsMatch(value, @"\b(eliminate|kill)\b") || value.Contains("처치")) return "Kill";
        if (Regex.IsMatch(value, @"\b(stash|plant|place|install)\b") || value.Contains("숨겨") || value.Contains("설치")) return "Stash";
        if (Regex.IsMatch(value, @"\b(hand over|handover|deliver)\b") || value.Contains("건네") || value.Contains("전달")) return "HandOver";
        if (Regex.IsMatch(value, @"\bmark\b") || value.Contains("표시")) return "Mark";
        if (Regex.IsMatch(value, @"\b(find|obtain|acquire)\b") || value.Contains("획득")) return "Collect";
        if (Regex.IsMatch(value, @"\b(survive|extract|transit)\b") || value.Contains("탈출") || value.Contains("환승")) return "Survive";
        if (Regex.IsMatch(value, @"\b(visit|locate|inspect|scout|check|reach)\b") || value.Contains("조사") || value.Contains("확인")) return "Visit";
        return "Custom";
    }

    private static string Ordinals(string text)
    {
        var normalized = text.ToLowerInvariant();
        var words = new[] { "first|1st|첫\\s*번째", "second|2nd|두\\s*번째", "third|3rd|세\\s*번째", "fourth|4th|네\\s*번째", "fifth|5th|다섯\\s*번째", "sixth|6th|여섯\\s*번째" };
        return string.Join(",", Enumerable.Range(0, words.Length).Where(i => Regex.IsMatch(normalized, @"(?:\b(?:" + words[i] + @")\b)" )).Select(i => i + 1));
    }

    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9가-힣]+", " ").Trim();
    private static double Similarity(string left, string right)
    {
        var a = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var b = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
