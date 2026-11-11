using System.Text.RegularExpressions;

namespace TarkovHelper.Services;

internal static partial class MapObjectiveSemantics
{
    internal static bool IsKill(string? objectiveType, string? description) =>
        string.Equals(objectiveType, "kill", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(description) && KillAction().IsMatch(description));

    // Some existing Wiki rows say '위치를 확인하십시오: and neutralize ...'
    // and were imported as Visit. Recognize the action, not a location word.
    [GeneratedRegex(@"^\s*(?:(?:\[선택\]|Optional\s*[:\-]?)\s*)?(?:(?:(?:find|locate|위치를 확인하십시오|찾으십시오)\s*:?[ \t]+)?(?:and\s+)?(?:kill|eliminate|neutralize|shoot\s+down)\b|처치하십시오|사살하십시오)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KillAction();
}
