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
        Check(MapObjectiveSemantics.IsKill("Visit", "위치를 확인하십시오: and neutralize Reshala") &&
            MapObjectiveSemantics.IsKill("Visit", "Locate and shoot down all toy ducks") &&
            !MapObjectiveSemantics.IsKill("Visit", "Locate the room and mark the crates"), "legacy kill goals are list-only, not missing visit coordinates");
        const string questText = "| location = [[Reserve]]\n==Objectives==\n* Inspect the vehicle\n==Rewards==\n* 10 XP\n==Guide==\nThe vehicle is by the barn.";
        var proof = WikiMapEvidence.QuestFingerprint(questText);
        Check(proof != null && proof == WikiMapEvidence.QuestFingerprint(questText.Replace("10 XP", "50 XP")) &&
            proof != WikiMapEvidence.QuestFingerprint(questText.Replace("the barn", "the gate")) &&
            proof != WikiMapEvidence.QuestFingerprint(questText.Replace("[[Reserve]]", "[[Woods]]")),
            "non-map edits are harmless, but guide and map changes invalidate evidence");

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
                var reserve = WikiMapEvidence.Baseline("Revision - Reserve");
                using (var tx = connection.BeginTransaction())
                {
                    using var clear = new SqliteCommand(@"
                        UPDATE QuestObjectives SET LocationPoints=NULL,OptionalPoints=NULL WHERE QuestId IN
                            (SELECT Id FROM Quests WHERE NameEN='Revision - Reserve');", connection, tx);
                    clear.ExecuteNonQuery();
                    CuratedQuestDataService.SaveEvidenceAsync(connection, tx, "Revision - Reserve", 999998, reserve.Fingerprint, default).GetAwaiter().GetResult();
                    CuratedQuestDataService.ApplyAsync(connection, tx).GetAwaiter().GetResult();
                    tx.Commit();
                }
                using var check = new SqliteCommand(@"
                    SELECT COUNT(*) FROM QuestObjectives WHERE QuestId IN
                        (SELECT Id FROM Quests WHERE NameEN='Revision - Reserve') AND LocationPoints IS NOT NULL", connection);
                var restored = Convert.ToInt32(check.ExecuteScalar());
                Check(restored > 0, "equivalent newer revisions must restore known pins");
                using (var tx = connection.BeginTransaction())
                {
                    using var sentinel = new SqliteCommand(@"
                        UPDATE QuestObjectives SET LocationPoints='[{""X"":1000,""Z"":2000}]' WHERE Id=(
                            SELECT o.Id FROM QuestObjectives o JOIN Quests q ON q.Id=o.QuestId
                            WHERE q.NameEN='Revision - Reserve' AND o.LocationPoints IS NOT NULL LIMIT 1);", connection, tx);
                    sentinel.ExecuteNonQuery();
                    CuratedQuestDataService.SaveEvidenceAsync(connection, tx, "Revision - Reserve", 999999, "different guide", default).GetAwaiter().GetResult();
                    CuratedQuestDataService.SaveEvidenceAsync(connection, tx, "Revision - Reserve", 999998, reserve.Fingerprint, default).GetAwaiter().GetResult();
                    CuratedQuestDataService.ApplyAsync(connection, tx).GetAwaiter().GetResult();
                    tx.Commit();
                }
                Check(Convert.ToInt32(check.ExecuteScalar()) == restored, "a changed guide must never clear an entire quest's coordinates");
                using var retained = new SqliteCommand("SELECT COUNT(*) FROM QuestObjectives WHERE LocationPoints='[{\"X\":1000,\"Z\":2000}]'", connection);
                Check(Convert.ToInt32(retained.ExecuteScalar()) == 1, "a changed guide must not reapply stale overlay points");
            }
            ExtractionCatalogService.ApplyBundledAsync(temporary).GetAwaiter().GetResult();
            Check(ExtractionCatalogService.ApplyBundledAsync(temporary).GetAwaiter().GetResult() == 0, "catalog import must be idempotent");
            var titles = new[] { "Customs", "Lighthouse", "Map:Customs", "Map:Lighthouse" };
            var baselines = titles.ToDictionary(title => title, title => WikiMapEvidence.Describe(
                new WikiMapEvidence.Page(title, 500000, TestWikiText(title, false, false).Replace("edited keys", "old keys"))));
            int CheckExtractions(long revision, bool changeTable, bool changeTransit = false)
            {
                using var client = new HttpClient(new WikiHandler(titles, revision, changeTable, changeTransit));
                return ExtractionCatalogService.CheckWikiRevisionsAsync(temporary, client, title => baselines[title]).GetAwaiter().GetResult();
            }
            Check(CheckExtractions(500001, false) == 0, "unrelated key edits and newer revisions must not flag exits");
            Check(CheckExtractions(500002, true) == 12, "a changed Lighthouse exit table flags Lighthouse exits only");
            Check(CheckExtractions(500001, false) == 0, "stale Wiki responses cannot clear review flags");
            Check(CheckExtractions(500003, false, true) == 16, "restored table clears 12 flags; changed transit position flags 4");
            Check(CheckExtractions(500004, false) == 4, "matching contents clear transit flags without affecting pending RUAF");
            Check(ExtractionCatalogService.ApplyBundledAsync(temporary).GetAwaiter().GetResult() == 0,
                "a previously applied old bundle must not erase newer review flags");
            using (var connection = new SqliteConnection($"Data Source={temporary}"))
            {
                connection.Open();
                using var check = new SqliteCommand("SELECT COUNT(*) FROM MapMarkers WHERE MapKey IN ('Customs','Lighthouse') AND MarkerType IN ('PmcExtraction','ScavExtraction','SharedExtraction','Transit')", connection);
                Check(Convert.ToInt32(check.ExecuteScalar()) == 37, "obsolete extraction duplicates must not remain");
                using var corrupt = new SqliteCommand("UPDATE QuestObjectives SET LocationPoints='[{\"X\":\"bad\",\"Z\":0}]' WHERE Id=(SELECT Id FROM QuestObjectives LIMIT 1)", connection);
                corrupt.ExecuteNonQuery();
                using var legacyKill = new SqliteCommand(@"
                    UPDATE QuestObjectives SET ObjectiveType='Visit',Description='위치를 확인하십시오: and neutralize test target',
                        MapName='Customs',LocationPoints=NULL,OptionalPoints=NULL
                    WHERE Id=(SELECT Id FROM QuestObjectives ORDER BY Id DESC LIMIT 1);", connection);
                legacyKill.ExecuteNonQuery();
            }
            var report = MapDataAuditService.InspectAsync(temporary).GetAwaiter().GetResult();
            Check(report.Issues.Any(issue => issue.Kind == "좌표 형식 확인"), "audit must detect malformed coordinates without crashing");
            Check(report.Issues.Any(issue => issue.Kind == "최신 Wiki 변경 재검증"), "future Wiki changes must appear in the report");
            Check(report.Issues.Count(issue => issue.Kind == "탈출구 재확인") == 1, "PvE RUAF remains independently unverified");
            Check(!report.Issues.Any(issue => issue.Kind == "위치 미확인" && issue.Description.Contains("neutralize test target")),
                "a legacy Visit row with a kill action is not a missing location");
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

    private static string TestWikiText(string title, bool changeTable, bool changeTransit)
    {
        if (title.StartsWith("Map:"))
        {
            var marker = changeTransit && title == "Map:Lighthouse" ? "[3,2]" : "[1,2]";
            return """
                {"mapImage":"test","mapBounds":[[0,0],[10,10]],"coordinateOrder":"xy","origin":[0,0],"markers":[
                {"categoryId":"exfil_pmc","position":[1,2],"popup":{"title":"Test exit"}},
                {"categoryId":"exfil_transit","position":POS,"popup":{"title":"Test transit"}}]}
                """.Replace("POS", marker);
        }
        return "==Keys==\n" + (changeTable ? "one key" : "edited keys") + "\n==Extractions==\n{|\n| " +
            (changeTable && title == "Lighthouse" ? "different exit" : "same exit") + "\n|}";
    }

    private sealed class WikiHandler(string[] titles, long revision, bool changeTable, bool changeTransit) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { query = new { pages = titles.Select(title => new
                {
                    title,
                    revisions = new[] { new { revid = revision, slots = new { main = new { content = TestWikiText(title, changeTable, changeTransit) } } } }
                }) } }))
            });
    }
}
