using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

public sealed record HideoutWikiRefreshResult(bool WasChanged, int Stations, int Levels, int Requirements,
    int Updated, int Added, int Removed, string? BackupPath, IReadOnlyList<string> UnregisteredStations);

public sealed class HideoutWikiRefreshService(HttpClient client)
{
    public const string SourceUrl = "https://escapefromtarkov.fandom.com/wiki/Hideout";
    public static string SnapshotPath => Path.Combine(AppContext.BaseDirectory, "Assets", "DB", "Data", "hideout_wiki_snapshot.json");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private sealed record Item(string Id, string Name, string? Ko, string? Ja, string? Icon);
    private sealed record Existing(string Id, string Station, int Level, string ItemId, int Count, bool Fir);
    private sealed record Planned(string Station, int Level, Item Item, HideoutWikiItem Requirement, int Sort);

    public async Task<HideoutWikiRefreshResult> RefreshAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://escapefromtarkov.fandom.com/api.php?action=query&prop=revisions&titles=Hideout%7CTemplate%3ASeasonal_change%7CSeasons&rvprop=ids%7Ctimestamp%7Ccontent&rvslots=main&format=json&formatversion=2");
        request.Headers.UserAgent.ParseAdd("TarkovHelper/1.5.42 (+English Wiki hideout sync)");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var snapshot = HideoutWikiParser.ParseApiResponse(await response.Content.ReadAsStringAsync(cancellationToken));
        return await ApplyAsync(databasePath, snapshot, false, cancellationToken);
    }

    public static async Task<HideoutWikiRefreshResult?> ApplyBundledAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SnapshotPath)) return null;
        var snapshot = JsonSerializer.Deserialize<HideoutWikiSnapshot>(await File.ReadAllTextAsync(SnapshotPath, cancellationToken))
            ?? throw new InvalidDataException("은신처 검증 자료가 비어 있습니다.");
        return await ApplyAsync(databasePath, snapshot, true, cancellationToken);
    }

    public static async Task<HideoutSeasonRule?> ReadSeasonRuleAsync(SqliteConnection connection)
    {
        if (!await TableExists(connection, "HideoutWikiSyncState")) return null;
        await using var command = new SqliteCommand("SELECT SeasonRuleJson FROM HideoutWikiSyncState WHERE Id=1", connection);
        var value = await command.ExecuteScalarAsync();
        if (value is not string json) return null;
        try { return JsonSerializer.Deserialize<HideoutSeasonRule>(json); }
        catch (JsonException ex)
        {
            Logging.Log.For<HideoutWikiRefreshService>().Warning($"Invalid seasonal hideout metadata; using base FIR rules: {ex.Message}");
            return null;
        }
    }

    public static async Task<HideoutWikiRefreshResult> ApplyAsync(string databasePath, HideoutWikiSnapshot snapshot,
        bool bundled = false, CancellationToken cancellationToken = default)
    {
        if (snapshot.FormatVersion != HideoutWikiParser.FormatVersion || snapshot.Revision <= 0 || snapshot.Stations.Count == 0)
            throw new InvalidDataException("지원하지 않는 은신처 검증 자료입니다.");
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
            await connection.OpenAsync(cancellationToken);
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot))));
            var seasonActive = !HideoutWikiParser.RequiresFir(true, Models.ProfileType.SeasonalPvp, snapshot.SeasonRule, DateTimeOffset.UtcNow);
            long savedRevision = 0;
            int savedFormat = 0;
            string? savedSignature = null;
            bool savedActive = false;
            if (await TableExists(connection, "HideoutWikiSyncState"))
            {
                await using var state = new SqliteCommand("SELECT Revision,FormatVersion,Signature,SeasonActive FROM HideoutWikiSyncState WHERE Id=1", connection);
                await using var reader = await state.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    savedRevision = reader.GetInt64(0); savedFormat = reader.GetInt32(1);
                    savedSignature = reader.GetString(2); savedActive = reader.GetInt32(3) != 0;
                }
            }
            if ((bundled && savedRevision >= snapshot.Revision && savedFormat >= snapshot.FormatVersion) || savedRevision > snapshot.Revision)
                return new(false, 0, 0, 0, 0, 0, 0, null, Array.Empty<string>());

            var aliases = new Dictionary<string, HashSet<string>>();
            var items = new Dictionary<string, Item>();
            var canonicalIds = new Dictionary<string, string>();
            void Alias(string? name, string id)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                var key = HideoutWikiParser.Key(name);
                if (!aliases.TryGetValue(key, out var ids)) aliases[key] = ids = new();
                ids.Add(id);
            }
            await using (var select = new SqliteCommand("SELECT Id,BsgId,Name,NameEN,NameKO,NameJA,WikiPageLink,IconUrl FROM Items", connection))
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1)) ? reader.GetString(0) : reader.GetString(1);
                    var name = reader.IsDBNull(3) ? reader.GetString(2) : reader.GetString(3);
                    items[id] = new(id, name, Text(reader, 4), Text(reader, 5), Text(reader, 7));
                    canonicalIds[id] = id; canonicalIds[reader.GetString(0)] = id;
                    Alias(name, id); Alias(reader.GetString(2), id);
                    var wiki = Text(reader, 6);
                    if (wiki?.Contains("/wiki/", StringComparison.Ordinal) == true)
                        Alias(wiki[(wiki.IndexOf("/wiki/", StringComparison.Ordinal) + 6)..].Split('#','?')[0], id);
                }
            var existing = new Dictionary<(string Station, int Level, string Item), Existing>();
            await using (var select = new SqliteCommand("SELECT Id,StationId,Level,ItemId,ItemName,Count,FoundInRaid,ItemNameKO,ItemNameJA,IconLink FROM HideoutItemRequirements", connection))
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetString(3);
                    id = canonicalIds.GetValueOrDefault(id, id);
                    // Legacy hideout rows use game IDs even when the Items catalog has
                    // only Wiki-backed IDs. Match unique existing names, never rewrite
                    // the stored requirement ID/ItemId merely to normalize that legacy shape.
                    if (!items.ContainsKey(id) && aliases.TryGetValue(HideoutWikiParser.Key(reader.GetString(4)),out var named) && named.Count == 1)
                        id = named.Single();
                    if (!items.ContainsKey(id)) items[id] = new(id,reader.GetString(4),Text(reader,7),Text(reader,8),Text(reader,9));
                    var row = new Existing(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), id, reader.GetInt32(5), reader.GetInt32(6) != 0);
                    if (!existing.TryAdd((row.Station,row.Level,id), row)) throw new InvalidDataException("은신처 재료 중복 ID를 먼저 확인해야 합니다.");
                    Alias(reader.GetString(4), id);
                }
            var stations = new Dictionary<string, (string Id, string Name, HashSet<int> Levels)>();
            await using (var select = new SqliteCommand("SELECT s.Id,s.Name,l.Level FROM HideoutStations s JOIN HideoutLevels l ON l.StationId=s.Id", connection))
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    var key = HideoutWikiParser.Key(reader.GetString(1));
                    if (!stations.ContainsKey(key)) stations[key] = (reader.GetString(0),reader.GetString(1),new());
                    stations[key].Levels.Add(reader.GetInt32(2));
                }
            if (stations.Count == 0) throw new InvalidDataException("은신처 기본 시설 자료가 없습니다.");
            var wikiStations = snapshot.Stations.ToDictionary(station => HideoutWikiParser.Key(station.Name));
            var planned = new Dictionary<(string Station, int Level, string Item), Planned>();
            foreach (var (key, station) in stations)
            {
                if (!wikiStations.TryGetValue(key, out var wiki) || !station.Levels.SetEquals(wiki.Levels.Select(level => level.Level)) ||
                    wiki.Levels.Select(level => level.Level).Distinct().Count() != wiki.Levels.Count)
                    throw new InvalidDataException($"Wiki 시설/단계 구성이 달라 재확인이 필요합니다: {station.Name}");
                foreach (var level in wiki.Levels)
                {
                    if (level.Items.Count == 0 && existing.Values.Any(row => row.Station == station.Id && row.Level == level.Level))
                        throw new InvalidDataException($"Wiki 재료 표가 비어 있어 기존 자료를 유지합니다: {station.Name} {level.Level}");
                    for (var sort = 0; sort < level.Items.Count; sort++)
                    {
                        var requirement = level.Items[sort];
                        if (requirement.Count <= 0 || !aliases.TryGetValue(HideoutWikiParser.Key(requirement.Name), out var ids) ||
                            ids.Count != 1 || !items.TryGetValue(ids.Single(), out var item))
                            throw new InvalidDataException($"안전하게 연결하지 못한 Wiki 재료: {station.Name} / {requirement.Name}");
                        if (!planned.TryAdd((station.Id,level.Level,item.Id), new(station.Id,level.Level,item,requirement,sort)))
                            throw new InvalidDataException($"Wiki 재료가 중복되었습니다: {station.Name} / {requirement.Name}");
                    }
                }
            }
            if (planned.Count < existing.Count * 0.6) throw new InvalidDataException("Wiki 재료가 대량으로 누락된 것으로 보여 갱신하지 않았습니다.");
            var updates = planned.Where(pair => existing.TryGetValue(pair.Key,out var old) &&
                (old.Count != pair.Value.Requirement.Count || old.Fir != pair.Value.Requirement.FoundInRaid)).ToList();
            var additions = planned.Where(pair => !existing.ContainsKey(pair.Key)).ToList();
            var removals = existing.Where(pair => !planned.ContainsKey(pair.Key)).ToList();
            var unknown = snapshot.Stations.Where(station => !stations.ContainsKey(HideoutWikiParser.Key(station.Name))).Select(station => station.Name).ToArray();
            var levelCount = stations.Values.Sum(station => station.Levels.Count);
            if (updates.Count + additions.Count + removals.Count == 0 && savedSignature == signature && savedActive == seasonActive)
                return new(false, stations.Count, levelCount, planned.Count, 0, 0, 0, null, unknown);

            // A consistent SQLite backup includes WAL contents and is created before the first write.
            var backupDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "Backups");
            Directory.CreateDirectory(backupDirectory);
            var backup = Path.Combine(backupDirectory, $"tarkov_data.pre-hideout-wiki-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
            await using (var destination = new SqliteConnection($"Data Source={backup}"))
            {
                await destination.OpenAsync(cancellationToken);
                connection.BackupDatabase(destination);
                await using var check = new SqliteCommand("PRAGMA quick_check", destination);
                if (!Equals(await check.ExecuteScalarAsync(cancellationToken), "ok")) throw new InvalidDataException("은신처 DB 백업 검증 실패");
            }
            await using var transaction = connection.BeginTransaction();
            var at = DateTime.UtcNow.ToString("o");
            foreach (var pair in updates.Concat(additions))
            {
                var plan = pair.Value;
                var old = existing.GetValueOrDefault(pair.Key);
                var sql = old != null ? "UPDATE HideoutItemRequirements SET Count=@count,FoundInRaid=@fir,UpdatedAt=@at WHERE Id=@id" : @"
                    INSERT INTO HideoutItemRequirements (Id,StationId,Level,ItemId,ItemName,ItemNameKO,ItemNameJA,IconLink,Count,FoundInRaid,SortOrder,UpdatedAt)
                    VALUES (@id,@station,@level,@item,@name,@ko,@ja,@icon,@count,@fir,@sort,@at)";
                await using var update = new SqliteCommand(sql, connection, transaction);
                update.Parameters.AddWithValue("@id", old?.Id ?? $"{plan.Station}_{plan.Level}_{plan.Item.Id}");
                update.Parameters.AddWithValue("@count", plan.Requirement.Count);
                update.Parameters.AddWithValue("@fir", plan.Requirement.FoundInRaid ? 1 : 0);
                update.Parameters.AddWithValue("@at", at);
                update.Parameters.AddWithValue("@station", plan.Station); update.Parameters.AddWithValue("@level", plan.Level);
                update.Parameters.AddWithValue("@item", plan.Item.Id); update.Parameters.AddWithValue("@name", plan.Item.Name);
                update.Parameters.AddWithValue("@ko", (object?)plan.Item.Ko ?? DBNull.Value);
                update.Parameters.AddWithValue("@ja", (object?)plan.Item.Ja ?? DBNull.Value);
                update.Parameters.AddWithValue("@icon", (object?)plan.Item.Icon ?? DBNull.Value); update.Parameters.AddWithValue("@sort", plan.Sort);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var old in removals.Select(pair => pair.Value))
            {
                await using var delete = new SqliteCommand("DELETE FROM HideoutItemRequirements WHERE Id=@id", connection, transaction);
                delete.Parameters.AddWithValue("@id",old.Id); await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var state = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS HideoutWikiSyncState (Id INTEGER PRIMARY KEY CHECK(Id=1),Revision INTEGER NOT NULL,
                    FormatVersion INTEGER NOT NULL,Signature TEXT NOT NULL,SeasonRuleJson TEXT,SeasonActive INTEGER NOT NULL,
                    SourceUrl TEXT NOT NULL,SourceUpdatedAt TEXT NOT NULL,CheckedAt TEXT NOT NULL);
                INSERT OR REPLACE INTO HideoutWikiSyncState VALUES (1,@revision,@format,@signature,@season,@active,@url,@sourceAt,@at);", connection, transaction))
            {
                state.Parameters.AddWithValue("@revision",snapshot.Revision); state.Parameters.AddWithValue("@format",snapshot.FormatVersion);
                state.Parameters.AddWithValue("@signature",signature);
                state.Parameters.AddWithValue("@season",snapshot.SeasonRule == null ? DBNull.Value : JsonSerializer.Serialize(snapshot.SeasonRule));
                state.Parameters.AddWithValue("@active",seasonActive ? 1 : 0); state.Parameters.AddWithValue("@url",SourceUrl);
                state.Parameters.AddWithValue("@sourceAt",snapshot.SourceUpdatedAt); state.Parameters.AddWithValue("@at",at);
                await state.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            return new(true, stations.Count,levelCount,planned.Count,updates.Count,additions.Count,removals.Count,backup,unknown);
        }
        finally { Gate.Release(); }
    }

    private static string? Text(SqliteDataReader reader,int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static async Task<bool> TableExists(SqliteConnection connection,string table)
    {
        await using var command = new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name",connection);
        command.Parameters.AddWithValue("@name",table);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }
}
