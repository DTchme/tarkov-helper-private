using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Restores the letter pickup from the English Wiki's Factory 1986 bunker guide.
/// Wiki text refreshes can otherwise lose the structured pickup coordinates.
/// </summary>
public static class PostmanPatLocationPolicy
{
    public const string QuestBsgId = "59675ea386f77414b32bded2";
    public const string MapName = "Factory";
    public const string LocationName = "지상층 1986 벙커 내부, 배달부 시신 옆 바닥의 편지";

    // Both day/night Factory share this pickup in the structured game data.
    public static string LocationPointsJson { get; } = JsonSerializer.Serialize(new[]
    {
        new LocationPoint(-19.9610023d, 0.047d, 21.9430027d, "main")
    });

    public static bool IsTargetObjective(
        string? questBsgId,
        string? questName,
        string? objectiveType,
        string? description)
    {
        var name = Normalize(questName);
        var isQuest = !string.IsNullOrWhiteSpace(questBsgId)
            ? questBsgId.Equals(QuestBsgId, StringComparison.OrdinalIgnoreCase)
            : name is "postmanpatpart1" or "thepostmanpatpart1";
        if (!isQuest)
            return false;

        var type = Normalize(objectiveType);
        if (type is "collect" or "findquestitem")
            return true;

        // Some Wiki revisions call the pickup a visit to the dead messenger.
        // Never attach the pickup to survival, delivery, or Part 2 completion.
        if (type is not ("visit" or "custom" or ""))
            return false;

        var text = Normalize(description);
        return !text.Contains("handover", StringComparison.Ordinal) &&
               !text.Contains("deliver", StringComparison.Ordinal) &&
               !text.Contains("건네", StringComparison.Ordinal) &&
               !text.Contains("전달", StringComparison.Ordinal) &&
               (text.Contains("letter", StringComparison.Ordinal) ||
                text.Contains("messenger", StringComparison.Ordinal) ||
                text.Contains("편지", StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs in the caller's refresh transaction without changing objective IDs
    /// or user completion state. Repeated applications produce the same marker.
    /// </summary>
    public static async Task<int> ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? eligibleQuestIds = null)
    {
        var targets = new List<string>();
        await using (var select = new SqliteCommand(@"
            SELECT objective.Id, quest.BsgId,
                   COALESCE(NULLIF(quest.NameEN, ''), quest.Name, ''),
                   objective.ObjectiveType, objective.Description, objective.QuestId
            FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId", connection, transaction))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (eligibleQuestIds != null && !eligibleQuestIds.Contains(reader.GetString(5))) continue;
                if (IsTargetObjective(
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4)))
                    targets.Add(reader.GetString(0));
            }
        }

        var changed = 0;
        foreach (var id in targets)
        {
            await using var update = new SqliteCommand(@"
                UPDATE QuestObjectives
                SET MapName=@map, LocationName=@location,
                    LocationPoints=@points, OptionalPoints=NULL
                WHERE Id=@id", connection, transaction);
            update.Parameters.AddWithValue("@map", MapName);
            update.Parameters.AddWithValue("@location", LocationName);
            update.Parameters.AddWithValue("@points", LocationPointsJson);
            update.Parameters.AddWithValue("@id", id);
            changed += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return changed;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
