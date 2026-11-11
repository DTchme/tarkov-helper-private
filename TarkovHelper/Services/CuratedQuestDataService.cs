using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

/// <summary>Version-bound corrections with durable source and review status.</summary>
public static class CuratedQuestDataService
{
    public static async Task<int> RestoreBundledLocationsAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var state = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS MapLocationRepairState (Version INTEGER PRIMARY KEY);
            INSERT OR IGNORE INTO MapLocationRepairState VALUES (1);", connection, transaction);
        if (await state.ExecuteNonQueryAsync(cancellationToken) == 0) return 0;
        var repaired = await ApplyAsync(connection, transaction, cancellationToken, locationsOnly: true);
        await transaction.CommitAsync(cancellationToken);
        return repaired;
    }

    private sealed record VerifiedPage(string Name, string Page);
    private static readonly VerifiedPage[] Pages =
    {
        new("Metal Birds", "Metal Birds"),
        new("Fuel Crisis", "Fuel Crisis"),
        new("Postman Pat - Part 1", "Postman Pat - Part 1"),
        new("Revision - Reserve", "Revision - Reserve"),
        new("Make Amends", "Make Amends"),
        new("To the Light - Getting Acquainted", "To the Light - Getting Acquainted"),
        new("Getting Acquainted", "To the Light - Getting Acquainted"),
        // A stale API can rename the stable Metal Birds identity back to Lend-Lease.
        new("Lend-Lease - Part 1", "Metal Birds")
    };

    internal static bool TracksSource(string title) => Pages.Any(page => page.Page == title);

    public static async Task<int> ApplyAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken = default, bool locationsOnly = false)
    {
        await using (var schema = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS CuratedQuestVerification (
                QuestId TEXT PRIMARY KEY, SourceUrl TEXT NOT NULL,
                VerifiedRevision INTEGER NOT NULL, ObservedRevision INTEGER NOT NULL,
                VerifiedAt TEXT NOT NULL, Status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS CuratedQuestWikiEvidence (
                PageTitle TEXT PRIMARY KEY, RevisionId INTEGER NOT NULL, Fingerprint TEXT NOT NULL);", connection, transaction))
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
        await using var syncTable = new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE name='WikiQuestSyncState'", connection, transaction);
        var hasSyncState = Convert.ToInt32(await syncTable.ExecuteScalarAsync(cancellationToken)) > 0;
        foreach (var group in quests.GroupBy(quest => quest.Id))
        {
            var page = Pages.First(page => page.Name.Equals(group.First().Name, StringComparison.OrdinalIgnoreCase));
            var baseline = WikiMapEvidence.Baseline(page.Page);
            var observed = group.Max(quest => quest.Revision);
            await using (var previous = new SqliteCommand(
                "SELECT ObservedRevision FROM CuratedQuestVerification WHERE QuestId=@id", connection, transaction))
            {
                previous.Parameters.AddWithValue("@id", group.Key);
                var value = await previous.ExecuteScalarAsync(cancellationToken);
                if (value != null) observed = Math.Max(observed, Convert.ToInt64(value));
            }
            // Sync-state survives an API rewrite of the objective Conditions field.
            if (hasSyncState)
            {
                await using var state = new SqliteCommand("SELECT RevisionId FROM WikiQuestSyncState WHERE PageTitle=@page", connection, transaction);
                state.Parameters.AddWithValue("@page", page.Page);
                var value = await state.ExecuteScalarAsync(cancellationToken);
                if (value != null) observed = Math.Max(observed, Convert.ToInt64(value));
            }

            bool? matchesObserved = null;
            await using (var evidence = new SqliteCommand(
                "SELECT RevisionId,Fingerprint FROM CuratedQuestWikiEvidence WHERE PageTitle=@page", connection, transaction))
            {
                evidence.Parameters.AddWithValue("@page", page.Page);
                await using var reader = await evidence.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var revision = reader.GetInt64(0);
                    if (revision >= observed)
                    {
                        observed = revision;
                        matchesObserved = reader.GetString(1) == baseline.Fingerprint;
                    }
                }
            }
            var current = matchesObserved ?? observed <= baseline.Revision;
            if (current) eligible.Add(group.Key);
            // Do not clear a quest's coordinates. A newer guide blocks this
            // overlay until verified; other sources may already have good pins.
            await using var save = new SqliteCommand(@"
                INSERT INTO CuratedQuestVerification (QuestId,SourceUrl,VerifiedRevision,ObservedRevision,VerifiedAt,Status)
                VALUES (@id,@url,@verified,@observed,@date,@status)
                ON CONFLICT(QuestId) DO UPDATE SET SourceUrl=excluded.SourceUrl,
                    VerifiedRevision=excluded.VerifiedRevision, ObservedRevision=excluded.ObservedRevision,
                    VerifiedAt=excluded.VerifiedAt, Status=excluded.Status", connection, transaction);
            save.Parameters.AddWithValue("@id", group.Key);
            save.Parameters.AddWithValue("@url", "https://escapefromtarkov.fandom.com/wiki/" + page.Page.Replace(' ', '_'));
            save.Parameters.AddWithValue("@verified", baseline.Revision);
            save.Parameters.AddWithValue("@observed", observed);
            save.Parameters.AddWithValue("@date", WikiMapEvidence.ReviewedAt);
            save.Parameters.AddWithValue("@status", current ? "verified" : "review_required");
            await save.ExecuteNonQueryAsync(cancellationToken);
        }

        var changed = await V3FlashDriveLocationPolicy.ApplyAsync(connection, transaction, cancellationToken, eligible);
        changed += await FuelCrisisLocationPolicy.ApplyAsync(connection, transaction, cancellationToken, eligible);
        changed += locationsOnly
            ? await MetalBirdsQuestPolicy.RestoreLocationsAsync(connection, transaction, eligible, cancellationToken)
            : await MetalBirdsQuestPolicy.ApplyAsync(connection, transaction, cancellationToken, eligible);
        changed += await PostmanPatLocationPolicy.ApplyAsync(connection, transaction, cancellationToken, eligible);
        changed += await RevisionReserveLocationPolicy.ApplyAsync(connection, transaction, cancellationToken, eligible);
        return changed;
    }

    internal static async Task SaveEvidenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        string page, long revision, string? fingerprint, CancellationToken cancellationToken)
    {
        if (!TracksSource(page) || revision <= 0) return;
        await using var save = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS CuratedQuestWikiEvidence (
                PageTitle TEXT PRIMARY KEY, RevisionId INTEGER NOT NULL, Fingerprint TEXT NOT NULL);
            INSERT INTO CuratedQuestWikiEvidence VALUES (@page,@revision,@fingerprint)
            ON CONFLICT(PageTitle) DO UPDATE SET RevisionId=excluded.RevisionId,Fingerprint=excluded.Fingerprint
            WHERE excluded.RevisionId >= CuratedQuestWikiEvidence.RevisionId;", connection, transaction);
        save.Parameters.AddWithValue("@page", page);
        save.Parameters.AddWithValue("@revision", revision);
        save.Parameters.AddWithValue("@fingerprint", fingerprint ?? "");
        await save.ExecuteNonQueryAsync(cancellationToken);
    }
}
