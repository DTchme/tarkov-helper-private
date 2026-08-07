using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Separates seasonal-only task lines from the permanent PvP/PvE quest lists.
/// The current season is KORD BREACH. Seasonal characters still use the normal
/// quest pool, with season-exclusive tasks layered on top.
/// </summary>
public static class QuestProfileScopeService
{
    private static readonly string[] SeasonalNameMarkers =
    {
        "[KORD BREACH]",
        "KORD BREACH"
    };

    public static bool IsSeasonalOnly(TarkovTask task)
    {
        var names = new[] { task.Name, task.NameKo, task.NameJa, task.NormalizedName };
        return names.Any(name => !string.IsNullOrWhiteSpace(name) &&
            SeasonalNameMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsVisibleForProfile(TarkovTask task, ProfileType profileType)
    {
        if (profileType == ProfileType.SeasonalPvp)
            return true;

        return !IsSeasonalOnly(task);
    }
}
