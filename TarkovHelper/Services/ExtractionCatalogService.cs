using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

public sealed class ExtractionCatalog
{
    public int Version { get; set; }
    public string VerifiedAt { get; set; } = "";
    public List<ExtractionCatalogEntry> Markers { get; set; } = new();
}

public sealed class ExtractionCatalogEntry
{
    public string Id { get; set; } = "";
    public string Map { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public string Floor { get; set; } = "main";
    public string Requirements { get; set; } = "";
    public string Notes { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public long WikiRevision { get; set; }
    public long MapRevision { get; set; }
    public string PositionSource { get; set; } = "";
    public double PositionErrorMeters { get; set; }
    public string VerifiedAt { get; set; } = "";
    public string GameModes { get; set; } = "all";
    public string Status { get; set; } = "verified";

    public bool IsVisibleFor(Models.ProfileType profile) => GameModes == "all" ||
        (GameModes == "pve" && profile == Models.ProfileType.Pve);

    public string ToTooltip() => string.Join("\n", new[]
    {
        Name, $"{Type switch { "PmcExtraction" => "PMC", "ScavExtraction" => "SCAV", "Transit" => "맵 이동", _ => "PMC / SCAV" }} · {Floor}",
        Requirements == "-" ? "" : Requirements, Notes == "-" ? "" : Notes,
        Status == "review_required" ? "확인 필요: 현재 게임의 탈출구 목록과 Wiki를 다시 확인하세요." : "",
        PositionErrorMeters > 0 ? $"Wiki 지도 보정 위치 (기준점 최대 잔차 {PositionErrorMeters:0.0}m; 실제 오차 보증 아님)" : "",
        $"검증일: {VerifiedAt} · Wiki 판 {WikiRevision}", SourceUrl
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

/// <summary>
/// Reviewed, versioned overlays. Never touches quest IDs or user progress.
/// A newer Wiki revision is recorded for review, not silently overwritten by an old overlay.
/// </summary>
public static class ExtractionCatalogService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string CatalogPath => Path.Combine(AppContext.BaseDirectory, "Assets", "DB", "Data", "extraction_catalog.json");

    public static async Task<int> ApplyBundledAsync(string databasePath, string? catalogPath = null,
        CancellationToken cancellationToken = default)
    {
        var path = catalogPath ?? CatalogPath;
        if (!File.Exists(path)) return 0;
        var catalog = JsonSerializer.Deserialize<ExtractionCatalog>(await File.ReadAllTextAsync(path, cancellationToken))
            ?? throw new InvalidDataException("탈출구 검증 자료가 비어 있습니다.");
        Validate(catalog);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
            await connection.OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            await using (var schema = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS MapExtractionDetails (MarkerId TEXT PRIMARY KEY, DetailsJson TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS MapExtractionCatalogState (Id INTEGER PRIMARY KEY CHECK(Id=1), Version INTEGER NOT NULL);",
                connection, transaction)) await schema.ExecuteNonQueryAsync(cancellationToken);
            await using (var version = new SqliteCommand("SELECT Version FROM MapExtractionCatalogState WHERE Id=1", connection, transaction))
                if (Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken) ?? 0) >= catalog.Version) return 0;

            var oldIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using (var old = new SqliteCommand(@"
                SELECT Id,MapKey,Name FROM MapMarkers WHERE MapKey IN ('Customs','Lighthouse')
                AND MarkerType IN ('PmcExtraction','ScavExtraction','SharedExtraction','Transit') ORDER BY Id", connection, transaction))
            await using (var reader = await old.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) oldIds.TryAdd(Key(reader.GetString(1), reader.GetString(2)), reader.GetString(0));
            await using (var delete = new SqliteCommand(@"
                DELETE FROM MapExtractionDetails WHERE MarkerId IN (SELECT Id FROM MapMarkers WHERE MapKey IN ('Customs','Lighthouse')
                    AND MarkerType IN ('PmcExtraction','ScavExtraction','SharedExtraction','Transit'));
                DELETE FROM MapMarkers WHERE MapKey IN ('Customs','Lighthouse')
                    AND MarkerType IN ('PmcExtraction','ScavExtraction','SharedExtraction','Transit');", connection, transaction))
                await delete.ExecuteNonQueryAsync(cancellationToken);
            foreach (var marker in catalog.Markers)
            {
                marker.Id = oldIds.GetValueOrDefault(Key(marker.Map, marker.Name), marker.Id);
                await using var insert = new SqliteCommand(@"
                    INSERT INTO MapMarkers (Id,Name,NameKo,MarkerType,MapKey,X,Y,Z,FloorId,CreatedAt,UpdatedAt)
                    VALUES (@id,@name,NULL,@type,@map,@x,@y,@z,@floor,@date,@date);
                    INSERT OR REPLACE INTO MapExtractionDetails VALUES (@id,@details);", connection, transaction);
                insert.Parameters.AddWithValue("@id", marker.Id);
                insert.Parameters.AddWithValue("@name", marker.Name);
                insert.Parameters.AddWithValue("@type", marker.Type);
                insert.Parameters.AddWithValue("@map", marker.Map);
                insert.Parameters.AddWithValue("@x", marker.X);
                insert.Parameters.AddWithValue("@y", marker.Y);
                insert.Parameters.AddWithValue("@z", marker.Z);
                insert.Parameters.AddWithValue("@floor", marker.Floor);
                insert.Parameters.AddWithValue("@date", catalog.VerifiedAt);
                insert.Parameters.AddWithValue("@details", JsonSerializer.Serialize(marker));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var save = new SqliteCommand("INSERT OR REPLACE INTO MapExtractionCatalogState VALUES (1,@version)", connection, transaction))
            {
                save.Parameters.AddWithValue("@version", catalog.Version);
                await save.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
            return catalog.Markers.Count;
        }
        finally { Gate.Release(); }
    }

    public static void Validate(ExtractionCatalog catalog)
    {
        if (catalog.Version <= 0 || catalog.Markers.Count == 0 ||
            catalog.Markers.Select(marker => marker.Id).Distinct().Count() != catalog.Markers.Count)
            throw new InvalidDataException("탈출구 자료 버전 또는 ID를 확인하세요.");
        foreach (var marker in catalog.Markers)
            if (marker.Map is not ("Customs" or "Lighthouse") ||
                marker.Type is not ("PmcExtraction" or "ScavExtraction" or "SharedExtraction" or "Transit") ||
                !double.IsFinite(marker.X) || !double.IsFinite(marker.Y) || !double.IsFinite(marker.Z) ||
                string.IsNullOrWhiteSpace(marker.Name) || marker.WikiRevision <= 0)
                throw new InvalidDataException($"잘못된 탈출구 자료: {marker.Name}");
    }

    // Source changes need human/coordinate review: revision checks never claim automatic verification.
    public static async Task<int> CheckWikiRevisionsAsync(string databasePath, HttpClient client,
        CancellationToken cancellationToken = default)
    {
        using var json = JsonDocument.Parse(await client.GetStringAsync(
            "https://escapefromtarkov.fandom.com/api.php?action=query&prop=revisions&titles=Customs%7CLighthouse%7CMap%3ACustoms%7CMap%3ALighthouse&rvprop=ids&format=json&formatversion=2", cancellationToken));
        var revisions = json.RootElement.GetProperty("query").GetProperty("pages").EnumerateArray()
            .Where(page => page.TryGetProperty("revisions", out _)).ToDictionary(page => page.GetProperty("title").GetString()!,
                page => page.GetProperty("revisions")[0].GetProperty("revid").GetInt64());
        if (revisions.Count != 4) throw new InvalidDataException("탈출구 Wiki 판 정보를 모두 가져오지 못했습니다.");
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
        await connection.OpenAsync(cancellationToken);
        await using var tx = connection.BeginTransaction();
        var changed = new List<ExtractionCatalogEntry>();
        await using (var select = new SqliteCommand("SELECT DetailsJson FROM MapExtractionDetails", connection, tx))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var entry = JsonSerializer.Deserialize<ExtractionCatalogEntry>(reader.GetString(0));
                if (entry == null || entry.Status == "review_required") continue;
                var page = entry.Type == "Transit" ? "Map:" + entry.Map : entry.Map;
                if (revisions[page] > entry.WikiRevision ||
                    (entry.PositionErrorMeters > 0 && revisions["Map:" + entry.Map] > entry.MapRevision))
                {
                    entry.Status = "review_required";
                    changed.Add(entry);
                }
            }
        foreach (var entry in changed)
        {
            await using var update = new SqliteCommand("UPDATE MapExtractionDetails SET DetailsJson=@json WHERE MarkerId=@id", connection, tx);
            update.Parameters.AddWithValue("@json", JsonSerializer.Serialize(entry));
            update.Parameters.AddWithValue("@id", entry.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        tx.Commit();
        return changed.Count;
    }

    private static string Key(string map, string name)
    {
        var normalized = Regex.Replace(name.Split(" (PVE")[0], @"\s*\((Paid|Secret)\)", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized.ToLowerInvariant(), "[^a-z0-9]", "");
        if (map == "Customs") normalized = normalized switch
        {
            "railroadtomilitarybase" => "railroadtoreserve", "militarybasecp" => "reservecheckpoint", _ => normalized
        };
        return map + "|" + normalized;
    }
}
