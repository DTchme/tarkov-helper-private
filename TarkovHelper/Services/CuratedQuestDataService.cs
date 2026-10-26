using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

/// <summary>Version-bound corrections with durable source and review status.</summary>
public static class CuratedQuestDataService
{
    private sealed record VerifiedPage(string Name, string Page, long Revision);
    private static readonly VerifiedPage[] Pages =
    {
        new("Metal Birds", "Metal Birds", 362629),
        new("Fuel Crisis", "Fuel Crisis", 364920),
        new("Postman Pat - Part 1", "Postman Pat - Part 1", 363268),
        new("Revision - Reserve", "Revision - Reserve", 362756),
        new("Make Amends", "Make Amends", 359464),
        new("To the Light - Getting Acquainted", "To the Light - Getting Acquainted", 360181),
        new("Getting Acquainted", "To the Light - Getting Acquainted", 360181),
        // A stale API can rename the stable Metal Birds identity back to Lend-Lease.
        new("Lend-Lease - Part 1", "Metal Birds", 362629)
    };

    public static async Task<int> ApplyAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        await using (var schema = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS CuratedQuestVerification (
                QuestId TEXT PRIMARY KEY, SourceUrl TEXT NOT NULL,
                VerifiedRevision INTEGER NOT NULL, ObservedRevision INTEGER NOT NULL,
                VerifiedAt TEXT NOT NULL, Status TEXT NOT NULL);", connection, transaction))
            await schema.ExecuteNonQueryAsync(cancellationToken);

        var quests = new List<(string Id, string Name, long Revision)>();
        await using (var select = new SqliteCommand(@"
            SELECT q.Id, COALESCE(NULLIF(q.NameEN,''),q.Name), o.Conditions
            FROM Quests q LEFT JOIN QuestObjectives o ON o.QuestId=q.Id", connection, transaction))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(1);
                if (!Pages.Any(page => page.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                long revision = 0;
                if (!reader.IsDBNull(2))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(reader.GetString(2));
                        if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("revision", out var value) && value.ValueKind == JsonValueKind.Number)
                            value.TryGetInt64(out revision);
                    }
                    catch (JsonException) { }
                }
                quests.Add((reader.GetString(0), name, revision));
            }
        }

        var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in quests.GroupBy(quest => quest.Id))
        {
            var page = Pages.First(page => page.Name.Equals(group.First().Name, StringComparison.OrdinalIgnoreCase));
            var observed = group.Max(quest => quest.Revision);
            await using (var previous = new SqliteCommand(
                "SELECT ObservedRevision FROM CuratedQuestVerification WHERE QuestId=@id", connection, transaction))
            {
                previous.Parameters.AddWithValue("@id", group.Key);
                var value = await previous.ExecuteScalarAsync(cancellationToken);
                if (value != null) observed = Math.Max(observed, Convert.ToInt64(value));
            }
            // Sync-state survives an API rewrite of the objective Conditions field.
            await using (var exists = new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE name='WikiQuestSyncState'", connection, transaction))
            {
                if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) > 0)
                {
                    await using var state = new SqliteCommand("SELECT RevisionId FROM WikiQuestSyncState WHERE PageTitle=@page", connection, transaction);
                    state.Parameters.AddWithValue("@page", page.Page);
                    var value = await state.ExecuteScalarAsync(cancellationToken);
                    if (value != null) observed = Math.Max(observed, Convert.ToInt64(value));
                }
            }
            var current = observed <= page.Revision;
            if (current) eligible.Add(group.Key);
            else
            {
                // A changed guide can move coordinates without changing goal text.
                // Show the goals as unverified instead of overwriting newer data.
                await using var clear = new SqliteCommand("UPDATE QuestObjectives SET LocationPoints=NULL,OptionalPoints=NULL WHERE QuestId=@id", connection, transaction);
                clear.Parameters.AddWithValue("@id", group.Key);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var save = new SqliteCommand(@"
                INSERT INTO CuratedQuestVerification VALUES (@id,@url,@verified,@observed,@date,@status)
                ON CONFLICT(QuestId) DO UPDATE SET SourceUrl=excluded.SourceUrl,
                    VerifiedRevision=excluded.VerifiedRevision, ObservedRevision=excluded.ObservedRevision,
                    VerifiedAt=excluded.VerifiedAt, Status=excluded.Status", connection, transaction);
            save.Parameters.AddWithValue("@id", group.Key);
            save.Parameters.AddWithValue("@url", "https://escapefromtarkov.fandom.com/wiki/" + page.Page.Replace(' ', '_'));
            save.Parameters.AddWithValue("@verified", page.Revision);
            save.Parameters.AddWithValue("@observed", observed);
            save.Parameters.AddWithValue("@date", "2026-10-26");
            save.Parameters.AddWithValue("@status", current ? "verified" : "review_required");
            await save.ExecuteNonQueryAsync(cancellationToken);
        }

        var changed = await V3FlashDriveLocationPolicy.ApplyAsync(connection, transaction, cancellationToken, eligible);
        changed += await FuelCrisisLocationPolicy.ApplyAsync(connection, transaction, cancellationToken, eligible);
        changed += await MetalBirdsQuestPolicy.ApplyAsync(connection, transaction, cancellationToken, eligible);
        changed += await PostmanPatLocationPolicy.ApplyAsync(connection, transaction, cancellationToken, eligible);
        changed += await RevisionReserveLocationPolicy.ApplyAsync(connection, transaction, cancellationToken, eligible);
        return changed;
    }
}
