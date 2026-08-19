using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

/// <summary>
/// Keeps Escape from Tarkov: Arena-only quests out of the EFT quest tracker.
/// Main-game Ref quests are intentionally retained.
/// </summary>
public static class ArenaQuestExclusionPolicy
{
    public static bool IsArenaLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return false;

        return location
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Equals("Arena", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Identifies Arena rows already stored in tarkov_data.db. The second clause covers
    /// Arena quest lines that the Wiki importer previously added without a map or BSG id.
    /// Structured/main-game Ref quests have a BSG id and are not excluded by that clause.
    /// </summary>
    public static bool IsExcludedStoredQuest(
        string? id,
        string? bsgId,
        string? trader,
        string? location,
        bool isApproved)
    {
        if (IsArenaLocation(location))
            return true;

        return !isApproved &&
               string.IsNullOrWhiteSpace(bsgId) &&
               id?.StartsWith("fandom_", StringComparison.OrdinalIgnoreCase) == true &&
               trader?.Equals("Ref", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// New Ref rows found only on the Wiki are Arena quest lines. Existing structured
    /// Ref quests may still receive Wiki text updates.
    /// </summary>
    public static bool IsExcludedWikiQuest(
        string? trader,
        string? location,
        bool hasStructuredQuest)
    {
        return IsArenaLocation(location) ||
               (!hasStructuredQuest && trader?.Equals("Ref", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Removes excluded quests and every quest-owned or cross-quest relationship in one transaction.
    /// The caller owns the transaction so this can be composed with API/Wiki refresh operations.
    /// </summary>
    public static async Task<int> RemoveExcludedRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var excluded = new List<(string Id, string? BsgId, string Name)>();
        await using (var select = new SqliteCommand(
                         "SELECT Id, BsgId, COALESCE(NameEN, Name, ''), Trader, Location, IsApproved FROM Quests",
                         connection,
                         transaction))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                var bsgId = reader.IsDBNull(1) ? null : reader.GetString(1);
                var name = reader.GetString(2);
                var trader = reader.IsDBNull(3) ? null : reader.GetString(3);
                var location = reader.IsDBNull(4) ? null : reader.GetString(4);
                var isApproved = !reader.IsDBNull(5) && reader.GetInt32(5) == 1;

                if (IsExcludedStoredQuest(id, bsgId, trader, location, isApproved))
                    excluded.Add((id, bsgId, name));
            }
        }

        if (excluded.Count == 0)
            return 0;

        var idParameters = excluded.Select((_, index) => $"@id{index}").ToArray();
        var idList = string.Join(", ", idParameters);

        foreach (var sql in new[]
                 {
                     $"DELETE FROM OptionalQuests WHERE QuestId IN ({idList}) OR AlternativeQuestId IN ({idList})",
                     $"DELETE FROM QuestRequiredItems WHERE QuestId IN ({idList})",
                     $"DELETE FROM QuestObjectives WHERE QuestId IN ({idList})",
                     $"DELETE FROM QuestRequirements WHERE QuestId IN ({idList}) OR RequiredQuestId IN ({idList})"
                 })
        {
            await ExecuteForExcludedIdsAsync(
                connection,
                transaction,
                sql,
                excluded,
                cancellationToken);
        }

        if (await TableExistsAsync(connection, transaction, "ApiMarkers", cancellationToken))
        {
            var bsgIds = excluded
                .Select(item => item.BsgId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var names = excluded
                .Select(item => item.Name)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await DeleteMarkerMatchesAsync(
                connection,
                transaction,
                "QuestBsgId",
                bsgIds,
                cancellationToken);
            await DeleteMarkerMatchesAsync(
                connection,
                transaction,
                "QuestNameEn",
                names,
                cancellationToken);
        }

        await ExecuteForExcludedIdsAsync(
            connection,
            transaction,
            $"DELETE FROM Quests WHERE Id IN ({idList})",
            excluded,
            cancellationToken);
        return excluded.Count;
    }

    private static async Task ExecuteForExcludedIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<(string Id, string? BsgId, string Name)> excluded,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        for (var index = 0; index < excluded.Count; index++)
            command.Parameters.AddWithValue($"@id{index}", excluded[index].Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteMarkerMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string column,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0)
            return;

        var parameterNames = values.Select((_, index) => $"@value{index}").ToArray();
        await using var command = new SqliteCommand(
            $"DELETE FROM ApiMarkers WHERE {column} IN ({string.Join(", ", parameterNames)})",
            connection,
            transaction);
        for (var index = 0; index < values.Count; index++)
            command.Parameters.AddWithValue(parameterNames[index], values[index]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name",
            connection,
            transaction);
        command.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }
}
