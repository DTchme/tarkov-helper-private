using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Models.Map;
using TarkovHelper.Services;

internal static class MapDataRegressionChecks
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var first = new ObjectiveMetadataCandidate("Locate the first BMP-2 on Reserve", "Visit", "Reserve", null);
        var second = new ObjectiveMetadataCandidate("Locate the second BMP-2 on Reserve", "Visit", "Reserve", null);
        Check(ObjectiveMetadataMatcher.FindMatch("", "Locate the second BMP-2 vehicle on Reserve", new[] { first }) == -1,
            "an ordinal mismatch must never reuse the first vehicle coordinates");
        Check(ObjectiveMetadataMatcher.FindMatch("", second.Description, new[] { first, second }) == 1, "exact second objective must keep its identity");
        Check(ObjectiveMetadataMatcher.FindMatch("", "Mark the first BMP-2 on Reserve", new[] { first }) == -1, "inspection cannot become marking by token similarity");
        Check(ObjectiveMetadataMatcher.FindMatch("", "Locate the first BMP-2 vehicle on Customs", new[] { first }) == -1, "coordinates from another map must not be inherited");
        Check(ObjectiveMetadataMatcher.FindMatch("", first.Description, new[] { first, first }) == -1, "duplicate exact goals are ambiguous");
        Check(ObjectiveMetadataMatcher.InferMap("Locate the tanker trucks on Interchange") == "Interchange", "explicit Wiki map must override stale API map");
        Check(ObjectiveMetadataMatcher.FindMatch("", "Locate the first BMP-2 vehicle on Reserve", new[] { first }) == 0, "a unique compatible conservative match should retain coordinates");

        var catalog = JsonSerializer.Deserialize<ExtractionCatalog>(File.ReadAllText(ExtractionCatalogService.CatalogPath))!;
        ExtractionCatalogService.Validate(catalog);
        Check(catalog.Markers.Count == 37, "Customs 16 + pending RUAF + 4 transits; Lighthouse 12 + 4 transits");
        var ruaf = catalog.Markers.Single(entry => entry.Name.StartsWith("RUAF"));
        Check(ruaf.Status == "review_required" && ruaf.IsVisibleFor(ProfileType.Pve) && !ruaf.IsVisibleFor(ProfileType.Pvp), "unconfirmed RUAF must not leak into PVP");
        Check(catalog.Markers.Any(entry => entry.Map == "Lighthouse" && entry.Name == "Transit to Icebreaker"), "new Lighthouse transit must be included");
        Check(catalog.Markers.Single(entry => entry.Name == "Dorms V-Ex").Requirements.Contains("5000 RUB"), "vehicle extraction price must not be stripped with a Wiki image");
        Check(catalog.Markers.Single(entry => entry.Name == "Boiler Room Basement (Co-op)").Floor == "basement", "basement co-op must retain its floor");
        Check(catalog.Markers.Single(entry => entry.Name == "Road to Military Base V-Ex").PositionErrorMeters > 0, "Wiki-calibrated vehicle position must expose its uncertainty");

        var temporary = Path.Combine(Path.GetTempPath(), "tarkov-map-data-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            File.Copy(Path.Combine("TarkovHelper", "Assets", "tarkov_data.db"), temporary);
            using (var connection = new SqliteConnection($"Data Source={temporary}"))
            {
                connection.Open();
                using (var tx = connection.BeginTransaction())
                {
                    CuratedQuestDataService.ApplyAsync(connection, tx).GetAwaiter().GetResult();
                    tx.Commit();
                }
                using (var future = new SqliteCommand(@"
                    UPDATE CuratedQuestVerification SET ObservedRevision=999999 WHERE QuestId IN
                        (SELECT Id FROM Quests WHERE NameEN='Revision - Reserve');", connection)) future.ExecuteNonQuery();
                using (var tx = connection.BeginTransaction())
                {
                    CuratedQuestDataService.ApplyAsync(connection, tx).GetAwaiter().GetResult();
                    tx.Commit();
                }
                using var check = new SqliteCommand(@"
                    SELECT COUNT(*) FROM QuestObjectives WHERE QuestId IN
                        (SELECT Id FROM Quests WHERE NameEN='Revision - Reserve') AND LocationPoints IS NOT NULL", connection);
                Check(Convert.ToInt32(check.ExecuteScalar()) == 0, "an older curated correction must not overwrite a newer Wiki guide");
            }
            ExtractionCatalogService.ApplyBundledAsync(temporary).GetAwaiter().GetResult();
            Check(ExtractionCatalogService.ApplyBundledAsync(temporary).GetAwaiter().GetResult() == 0, "catalog import must be idempotent");
            using (var client = new HttpClient(new FutureWikiHandler()))
                Check(ExtractionCatalogService.CheckWikiRevisionsAsync(temporary, client).GetAwaiter().GetResult() == 36,
                    "newer map guides must mark all previously verified exits for review");
            Check(ExtractionCatalogService.ApplyBundledAsync(temporary).GetAwaiter().GetResult() == 0,
                "a previously applied old bundle must not erase newer review flags");
            using (var connection = new SqliteConnection($"Data Source={temporary}"))
            {
                connection.Open();
                using var check = new SqliteCommand("SELECT COUNT(*) FROM MapMarkers WHERE MapKey IN ('Customs','Lighthouse') AND MarkerType IN ('PmcExtraction','ScavExtraction','SharedExtraction','Transit')", connection);
                Check(Convert.ToInt32(check.ExecuteScalar()) == 37, "obsolete extraction duplicates must not remain");
                using var corrupt = new SqliteCommand("UPDATE QuestObjectives SET LocationPoints='[{\"X\":\"bad\",\"Z\":0}]' WHERE Id=(SELECT Id FROM QuestObjectives LIMIT 1)", connection);
                corrupt.ExecuteNonQuery();
            }
            var report = MapDataAuditService.InspectAsync(temporary).GetAwaiter().GetResult();
            Check(report.Issues.Any(issue => issue.Kind == "좌표 형식 확인"), "audit must detect malformed coordinates without crashing");
            Check(report.Issues.Any(issue => issue.Kind == "최신 Wiki 변경 재검증"), "future Wiki changes must appear in the report");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(temporary);
        }

        var service = QuestObjectiveDbService.Instance;
        Check(service.LoadObjectivesAsync().GetAwaiter().GetResult(), "production map objective loader must work");
        Check(service.AllObjectives.Any(objective => !objective.HasCoordinates && !objective.HasOptionalPoints &&
            objective.ObjectiveType is QuestObjectiveType.Visit or QuestObjectiveType.Mark or QuestObjectiveType.Stash),
            "unmapped location goals must remain in the map drawer instead of disappearing");
        var packaged = MapDataAuditService.InspectAsync(Path.Combine("TarkovHelper", "Assets", "tarkov_data.db")).GetAwaiter().GetResult();
        Check(!packaged.Issues.Any(issue => issue.Kind == "좌표 형식 확인"), "packaged objective coordinates must be well-formed");
    }

    private sealed class FutureWikiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"query":{"pages":[
                    {"title":"Customs","revisions":[{"revid":999999}]},
                    {"title":"Lighthouse","revisions":[{"revid":999999}]},
                    {"title":"Map:Customs","revisions":[{"revid":999999}]},
                    {"title":"Map:Lighthouse","revisions":[{"revid":999999}]}]}}
                    """)
            });
    }
}
