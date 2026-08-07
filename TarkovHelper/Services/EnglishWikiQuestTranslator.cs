using System.Net;
using System.Text.RegularExpressions;

namespace TarkovHelper.Services;

/// <summary>
/// Translates only unambiguous English objective shapes into Tarkov-style Korean.
/// Targets, item names, and clauses remain in their official English form when a
/// reliable Korean rendering cannot be inferred without changing the meaning.
/// </summary>
internal sealed class EnglishWikiQuestTranslator
{
    private static readonly (Regex Pattern, string Replacement)[] ObjectivePatterns =
    {
        Rule(@"^Find (?<target>.+?) in raid$", "레이드에서 찾으십시오: ${target}"),
        Rule(@"^Locate and mark (?:the )?(?<target>.+)$", "찾아 표시하십시오: ${target}"),
        Rule(@"^Find and mark (?:the )?(?<target>.+)$", "찾아 표시하십시오: ${target}"),
        Rule(@"^Locate and inspect (?:the )?(?<target>.+)$", "찾아 조사하십시오: ${target}"),
        Rule(@"^Locate and scout (?:the )?(?<target>.+)$", "찾아 정찰하십시오: ${target}"),
        Rule(@"^Hand over (?:the )?(?<target>.+)$", "건네주십시오: ${target}"),
        Rule(@"^Handover (?:the )?(?<target>.+)$", "건네주십시오: ${target}"),
        Rule(@"^Locate and obtain (?:the )?(?<target>.+)$", "찾아 획득하십시오: ${target}"),
        Rule(@"^Locate (?:the )?(?<target>.+)$", "위치를 확인하십시오: ${target}"),
        Rule(@"^Obtain (?:the )?(?<target>.+)$", "획득하십시오: ${target}"),
        Rule(@"^Eliminate (?<target>.+)$", "처치하십시오: ${target}"),
        Rule(@"^Survive and extract from (?<target>.+)$", "생존하여 탈출하십시오: ${target}"),
        Rule(@"^Extract from (?<target>.+)$", "탈출하십시오: ${target}"),
        Rule(@"^Find (?<target>.+)$", "찾으십시오: ${target}"),
        Rule(@"^Reach (?<target>.+)$", "달성하십시오: ${target}"),
        Rule(@"^Mark (?<target>.+)$", "표시하십시오: ${target}"),
        Rule(@"^Install (?<target>.+)$", "설치하십시오: ${target}"),
        Rule(@"^Plant (?<target>.+)$", "설치하십시오: ${target}"),
        Rule(@"^Place (?<target>.+)$", "배치하십시오: ${target}"),
        Rule(@"^Stash (?<target>.+)$", "숨겨두십시오: ${target}"),
        Rule(@"^Secure (?<target>.+)$", "확보하십시오: ${target}"),
        Rule(@"^Modify (?<target>.+)$", "개조하십시오: ${target}"),
        Rule(@"^Use (?<target>.+)$", "사용하십시오: ${target}"),
        Rule(@"^Repair (?<target>.+)$", "수리하십시오: ${target}"),
        Rule(@"^Recon (?<target>.+)$", "정찰하십시오: ${target}"),
        Rule(@"^Return to (?<target>.+)$", "돌아가십시오: ${target}")
    };

    public string TranslateObjective(string source)
    {
        var original = Normalize(source);
        if (string.IsNullOrWhiteSpace(original))
            return string.Empty;

        var optional = original.StartsWith("Optional", StringComparison.OrdinalIgnoreCase);
        var objective = optional
            ? original["Optional".Length..].TrimStart(' ', ':', '-')
            : original;

        foreach (var (pattern, replacement) in ObjectivePatterns)
        {
            if (!pattern.IsMatch(objective))
                continue;

            var translated = pattern.Replace(objective, replacement).Trim();
            return optional ? $"[선택] {translated}" : translated;
        }

        // Ambiguous wording stays as the official English Wiki text.
        return original;
    }

    private static (Regex Pattern, string Replacement) Rule(string pattern, string replacement) =>
        (new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), replacement);

    private static string Normalize(string value)
    {
        var normalized = WebUtility.HtmlDecode(value ?? string.Empty)
            .Replace('\u00a0', ' ');
        return Regex.Replace(normalized, @"\s+", " ").Trim().TrimEnd('.');
    }
}
