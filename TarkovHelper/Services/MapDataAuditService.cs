using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

public sealed record MapDataIssue(string Quest, string Map, string Kind, string Description);
public sealed record MapDataAuditReport(int QuestCount, int ObjectiveCount, IReadOnlyList<MapDataIssue> Issues)
{
    public string ToDisplayText()
    {
        var text = new StringBuilder();
        text.AppendLine($"퀘스트 {QuestCount}개 / 목표 {ObjectiveCount}개 / 확인 대상 {Issues.Count}개");
        text.AppendLine("확인 대상은 오류 확정 건수가 아닙니다. 좌표가 없는 목표는 지도 목록의 '위치 미확인'으로 표시됩니다.");
        text.AppendLine("게임의 진행 상태나 완료 체크는 이 점검으로 변경되지 않습니다.\n");
        foreach (var group in Issues.GroupBy(issue => issue.Kind))
        {
            text.AppendLine($"[{group.Key}] {group.Count()}개");
            foreach (var issue in group.OrderBy(issue => issue.Map).ThenBy(issue => issue.Quest))
                text.AppendLine($"• {issue.Map} | {issue.Quest}\n  {issue.Description}");
            text.AppendLine();
        }
        return text.ToString();
    }
}

public static class MapDataAuditService
{
    public static async Task<MapDataAuditReport> InspectAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken);
        var issues = new List<MapDataIssue>();
        var quests = new HashSet<string>();
        var count = 0;
        await using (var command = new SqliteCommand(@"
            SELECT q.Id,COALESCE(NULLIF(q.NameEN,''),q.Name),
                   COALESCE(NULLIF(o.MapName,''),q.Location,''),o.ObjectiveType,o.Description,
                   o.LocationPoints,o.OptionalPoints,o.Conditions
            FROM QuestObjectives o JOIN Quests q ON q.Id=o.QuestId", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                quests.Add(reader.GetString(0));
                count++;
                var name = reader.GetString(1);
                var map = reader.GetString(2);
                var type = reader.IsDBNull(3) ? "Custom" : reader.GetString(3);
                var description = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var pointCount = 0;
                var invalid = false;
                foreach (var column in new[] { 5, 6 })
                {
                    if (reader.IsDBNull(column) || string.IsNullOrWhiteSpace(reader.GetString(column))) continue;
                    try
                    {
                        using var json = JsonDocument.Parse(reader.GetString(column));
                        if (json.RootElement.ValueKind != JsonValueKind.Array) { invalid = true; continue; }
                        foreach (var point in json.RootElement.EnumerateArray())
                        {
                            if (point.ValueKind != JsonValueKind.Object ||
                                !point.TryGetProperty("X", out var x) || !x.TryGetDouble(out var xd) ||
                                !point.TryGetProperty("Z", out var z) || !z.TryGetDouble(out var zd) ||
                                !double.IsFinite(xd) || !double.IsFinite(zd)) invalid = true;
                            else pointCount++;
                        }
                    }
                    catch (JsonException) { invalid = true; }
                    catch (InvalidOperationException) { invalid = true; }
                }
                if (invalid) issues.Add(new(name, map, "좌표 형식 확인", description));
                else if (pointCount == 0 && (type.ToLowerInvariant() is "visit" or "mark" or "stash") &&
                    !string.IsNullOrWhiteSpace(map) && !Models.Map.MapConfig.IsAnyMapName(map))
                    issues.Add(new(name, map, "위치 미확인", description));
                if (!reader.IsDBNull(7))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(reader.GetString(7));
                        if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("metadataMatch", out var match) &&
                            match.ValueKind == JsonValueKind.String && match.GetString() == "unverified")
                            issues.Add(new(name, map, "목표 연결 재확인", description));
                    }
                    catch (JsonException) { }
                }
            }
        }
        await using var extracts = new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE name='MapExtractionDetails'", connection);
        if (Convert.ToInt32(await extracts.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            await using var select = new SqliteCommand("SELECT DetailsJson FROM MapExtractionDetails", connection);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var entry = JsonSerializer.Deserialize<ExtractionCatalogEntry>(reader.GetString(0));
                if (entry?.Status == "review_required")
                    issues.Add(new(entry.Name, entry.Map, "탈출구 재확인", entry.ToTooltip()));
            }
        }
        await using var exists = new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE name='CuratedQuestVerification'", connection);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            await using var review = new SqliteCommand(@"
                SELECT COALESCE(NULLIF(q.NameEN,''),q.Name),COALESCE(q.Location,''),v.SourceUrl
                FROM CuratedQuestVerification v JOIN Quests q ON q.Id=v.QuestId WHERE v.Status='review_required'", connection);
            await using var reader = await review.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                issues.Add(new(reader.GetString(0), reader.GetString(1), "최신 Wiki 변경 재검증", reader.GetString(2)));
        }
        return new(quests.Count, count, issues);
    }
}
