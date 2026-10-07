using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

/// <summary>
/// Authoritative Interchange tanker locations for Fuel Crisis. The English Wiki
/// groups the four former Blood of War - Part 1 tankers into two alternatives:
/// markers 1/3 satisfy the power-station objective and markers 2/4 satisfy the
/// northern-territory objective.
/// </summary>
public static class FuelCrisisLocationPolicy
{
    public const string QuestName = "Fuel Crisis";
    public const string MapName = "Interchange";
    public const string PowerStationLocationName = "발전소 인근 유조차 2곳 중 1곳";
    public const string NorthernTerritoryLocationName = "북부 구역 유조차 2곳 중 1곳";

    public static string PowerStationPointsJson { get; } = JsonSerializer.Serialize(new[]
    {
        // Wiki marker 1: behind Goshan, near the former Hole in the Fence area.
        new MapPoint(-202.29849999999988d, 1d, -83.96000000000004d, "main"),
        // Wiki marker 3: inside the power-station grounds.
        new MapPoint(-172.4855d, 1d, -353.4388d, "main")
    });

    public static string NorthernTerritoryPointsJson { get; } = JsonSerializer.Serialize(new[]
    {
        // Wiki marker 2: west of the highway near the Path to River extraction.
        new MapPoint(424.63030000000003d, 1d, 128.73759999999993d, "main"),
        // Wiki marker 4: south of the trailer park / along the OLI outer road.
        new MapPoint(42.702999999999975d, 1d, 305.91859999999997d, "main")
    });

    public static FuelCrisisArea GetTargetArea(
        string? questName,
        string? objectiveType,
        string? description)
    {
        if (Normalize(questName) != "fuelcrisis")
            return FuelCrisisArea.None;

        var normalizedType = Normalize(objectiveType);
        if (normalizedType.Length > 0 && normalizedType != "mark")
            return FuelCrisisArea.None;

        var normalizedDescription = Normalize(description);
        if (normalizedDescription.Contains("powerstation", StringComparison.Ordinal))
            return FuelCrisisArea.PowerStation;
        if (normalizedDescription.Contains("northernterritory", StringComparison.Ordinal))
            return FuelCrisisArea.NorthernTerritory;

        return FuelCrisisArea.None;
    }

    /// <summary>
    /// Restores both valid marker alternatives after either API or Wiki refresh.
    /// The caller owns the transaction so refreshes remain atomic.
    /// </summary>
    public static async Task<int> ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var targets = new List<(string Id, FuelCrisisArea Area)>();
        await using (var select = new SqliteCommand(@"
            SELECT objective.Id,
                   COALESCE(NULLIF(quest.NameEN, ''), quest.Name, ''),
                   objective.ObjectiveType,
                   objective.Description
            FROM QuestObjectives objective
            JOIN Quests quest ON quest.Id=objective.QuestId", connection, transaction))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var objectiveId = reader.GetString(0);
                var questName = reader.IsDBNull(1) ? null : reader.GetString(1);
                var objectiveType = reader.IsDBNull(2) ? null : reader.GetString(2);
                var description = reader.IsDBNull(3) ? null : reader.GetString(3);
                var area = GetTargetArea(questName, objectiveType, description);
                if (area != FuelCrisisArea.None)
                    targets.Add((objectiveId, area));
            }
        }

        var changed = 0;
        foreach (var target in targets)
        {
            var locationName = target.Area == FuelCrisisArea.PowerStation
                ? PowerStationLocationName
                : NorthernTerritoryLocationName;
            var pointsJson = target.Area == FuelCrisisArea.PowerStation
                ? PowerStationPointsJson
                : NorthernTerritoryPointsJson;

            await using var update = new SqliteCommand(@"
                UPDATE QuestObjectives
                SET MapName=@mapName,
                    LocationName=@locationName,
                    LocationPoints=NULL,
                    OptionalPoints=@optionalPoints
                WHERE Id=@id", connection, transaction);
            update.Parameters.AddWithValue("@mapName", MapName);
            update.Parameters.AddWithValue("@locationName", locationName);
            update.Parameters.AddWithValue("@optionalPoints", pointsJson);
            update.Parameters.AddWithValue("@id", target.Id);
            changed += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return changed;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private sealed record MapPoint(double X, double Y, double Z, string? FloorId);
}

public enum FuelCrisisArea
{
    None,
    PowerStation,
    NorthernTerritory
}
