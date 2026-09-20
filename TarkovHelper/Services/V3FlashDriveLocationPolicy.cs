using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

/// <summary>
/// Authoritative map locations for the Secure Flash drive V3 objective shared by
/// Getting Acquainted and Make Amends. The English Wiki currently lists three
/// indoor spawns in the black (southern) chalet and one spawn in the tennis-court
/// tent. A single chalet marker represents the three indoor laptop positions.
/// </summary>
public static class V3FlashDriveLocationPolicy
{
    public const string GettingAcquaintedBsgId = "625d700cc48e6c62a440fab5";
    public const string MakeAmendsBsgId = "6391d90f4ed9512be67647df";
    public const string MapName = "Lighthouse";
    public const string LocationName = "검은색(남쪽) 샬레 내부 3곳 / 테니스 코트 텐트 내부 1곳";

    public static string OptionalPointsJson { get; } = JsonSerializer.Serialize(new[]
    {
        // Black (southern) chalet. Three Wiki spawns are inside this building.
        new MapPoint(-123.6628770633024d, 1d, 104.05403435728749d, null),
        // Tennis court tent. One Wiki spawn is on the laptop inside the tent.
        new MapPoint(-71.172d, 1d, 131.3706d, null)
    });

    public static bool IsTargetObjective(
        string? questBsgId,
        string? questName,
        string? objectiveType,
        string? description,
        string? itemName)
    {
        if (!IsTargetQuest(questBsgId, questName))
            return false;

        var normalizedType = Normalize(objectiveType);
        var normalizedText = Normalize((description ?? string.Empty) + " " + (itemName ?? string.Empty));
        if (!normalizedText.Contains("v3flashdrive", StringComparison.Ordinal) &&
            !normalizedText.Contains("secureflashdrivev3", StringComparison.Ordinal))
        {
            return false;
        }

        // The same quests can also contain a hand-over row for the V3 drive.
        // Only the acquisition row belongs on the map.
        if (normalizedType.Contains("handover", StringComparison.Ordinal) ||
            normalizedText.Contains("handover", StringComparison.Ordinal) ||
            normalizedText.Contains("건네", StringComparison.Ordinal))
        {
            return false;
        }

        return normalizedType.Contains("collect", StringComparison.Ordinal) ||
               normalizedText.Contains("obtain", StringComparison.Ordinal) ||
               normalizedText.Contains("find", StringComparison.Ordinal) ||
               normalizedText.Contains("획득", StringComparison.Ordinal);
    }

    /// <summary>
    /// Replaces obsolete water-treatment markers after either API or Wiki refresh.
    /// The caller owns the transaction so refreshes remain atomic.
    /// </summary>
    public static async Task<int> ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var targets = new List<string>();
        await using (var select = new SqliteCommand(@"
            SELECT objective.Id,
                   quest.BsgId,
                   COALESCE(NULLIF(quest.NameEN, ''), quest.Name, ''),
                   objective.ObjectiveType,
                   objective.Description,
                   objective.ItemName
            FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId", connection, transaction))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var objectiveId = reader.GetString(0);
                var bsgId = reader.IsDBNull(1) ? null : reader.GetString(1);
                var questName = reader.IsDBNull(2) ? null : reader.GetString(2);
                var objectiveType = reader.IsDBNull(3) ? null : reader.GetString(3);
                var description = reader.IsDBNull(4) ? null : reader.GetString(4);
                var itemName = reader.IsDBNull(5) ? null : reader.GetString(5);
                if (IsTargetObjective(bsgId, questName, objectiveType, description, itemName))
                    targets.Add(objectiveId);
            }
        }

        var changed = 0;
        foreach (var objectiveId in targets)
        {
            await using var update = new SqliteCommand(@"
                UPDATE QuestObjectives
                SET MapName=@mapName,
                    LocationName=@locationName,
                    LocationPoints=NULL,
                    OptionalPoints=@optionalPoints
                WHERE Id=@id", connection, transaction);
            update.Parameters.AddWithValue("@mapName", MapName);
            update.Parameters.AddWithValue("@locationName", LocationName);
            update.Parameters.AddWithValue("@optionalPoints", OptionalPointsJson);
            update.Parameters.AddWithValue("@id", objectiveId);
            changed += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return changed;
    }

    private static bool IsTargetQuest(string? questBsgId, string? questName)
    {
        if (questBsgId?.Equals(GettingAcquaintedBsgId, StringComparison.OrdinalIgnoreCase) == true ||
            questBsgId?.Equals(MakeAmendsBsgId, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        var normalizedName = Normalize(questName);
        return normalizedName is "gettingacquainted" or
            "makeamends" or
            "tothelightgettingacquainted";
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private sealed record MapPoint(double X, double Y, double Z, string? FloorId);
}
