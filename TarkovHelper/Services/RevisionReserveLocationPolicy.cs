using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// The English Wiki (revision 362756) identifies four BMP-2s: Scav Lands,
/// White Knight, the RB-ST room, and Checkpoint Fence. These world positions
/// match their inspection/marker zones in the structured game data.
/// </summary>
public static class RevisionReserveLocationPolicy
{
    public const string QuestBsgId = "6086c852c945025d41566124";
    public const string MapName = "Reserve";

    private static readonly VehicleLocation[] Vehicles =
    {
        new("BMP-2 1 · Scav Lands 협동 탈출구 인근",
            new(-100.4, -4.32, -155.69, "main"),
            new(-100.62, -3.72, -155.97, "main")),
        new("BMP-2 2 · White Knight 건물 내부",
            new(79.01, -4.63, -31.55, "main"),
            new(79.03, -5.44, -31.79, "main")),
        new("BMP-2 3 · RB-ST 열쇠로 여는 방 내부",
            new(103.38, -4.63, 63.87, "main"),
            new(101.73, -5.59, 64.44, "main")),
        new("BMP-2 4 · Checkpoint Fence Scav 탈출구 인근",
            new(53.83, -4.14, 122.03, "main"),
            new(53.62, -4, 122.66, "main"))
    };

    public static int GetVehicleNumber(
        string? questBsgId,
        string? questName,
        string? objectiveType,
        string? description)
    {
        var isQuest = !string.IsNullOrWhiteSpace(questBsgId)
            ? questBsgId.Equals(QuestBsgId, StringComparison.OrdinalIgnoreCase)
            : Normalize(questName) == "revisionreserve";
        if (!isQuest || Normalize(objectiveType) is not ("visit" or "mark"))
            return 0;

        var text = Normalize(description);
        var ordinals = new[]
        {
            new[] { "first", "1st", "첫번째" },
            new[] { "second", "2nd", "두번째" },
            new[] { "third", "3rd", "세번째" },
            new[] { "fourth", "4th", "네번째" }
        };
        for (var index = 0; index < ordinals.Length; index++)
        {
            if (ordinals[index].Any(ordinal => text.Contains(ordinal + "bmp2", StringComparison.Ordinal)))
                return index + 1;
        }

        return 0;
    }

    /// <summary>
    /// Corrects both inspection and marking objectives after either refresh,
    /// retaining their IDs and separate completion state. All four vehicles
    /// are on the ground floor despite Reserve's negative world Y values.
    /// </summary>
    public static async Task<int> ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? eligibleQuestIds = null)
    {
        var targets = new List<(string Id, int Vehicle, bool IsMark)>();
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
                var type = reader.IsDBNull(3) ? null : reader.GetString(3);
                var vehicle = GetVehicleNumber(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2), type,
                    reader.IsDBNull(4) ? null : reader.GetString(4));
                if (vehicle > 0)
                    targets.Add((reader.GetString(0), vehicle, Normalize(type) == "mark"));
            }
        }

        var changed = 0;
        foreach (var target in targets)
        {
            var location = Vehicles[target.Vehicle - 1];
            var point = target.IsMark ? location.Mark : location.Inspect;
            await using var update = new SqliteCommand(@"
                UPDATE QuestObjectives
                SET MapName=@map, LocationName=@location,
                    LocationPoints=@points, OptionalPoints=NULL
                WHERE Id=@id", connection, transaction);
            update.Parameters.AddWithValue("@map", MapName);
            update.Parameters.AddWithValue("@location", location.Name);
            update.Parameters.AddWithValue("@points", JsonSerializer.Serialize(new[] { point }));
            update.Parameters.AddWithValue("@id", target.Id);
            changed += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return changed;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private sealed record VehicleLocation(string Name, LocationPoint Inspect, LocationPoint Mark);
}
