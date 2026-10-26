using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services;

internal static class HideoutWikiRegressionChecks
{
    private static void Check(bool condition,string message) { if (!condition) throw new Exception(message); }
    private static HideoutWikiSnapshot Clone(HideoutWikiSnapshot snapshot) => JsonSerializer.Deserialize<HideoutWikiSnapshot>(JsonSerializer.Serialize(snapshot))!;

    public static void Run()
    {
        var snapshot = JsonSerializer.Deserialize<HideoutWikiSnapshot>(File.ReadAllText(HideoutWikiRefreshService.SnapshotPath))!;
        Check(snapshot.Stations.Count == 26 && snapshot.Stations.Sum(station => station.Levels.Count) == 68 &&
            snapshot.Stations.Sum(station => station.Levels.Sum(level => level.Items.Count)) == 317,"reviewed snapshot must contain all 26 stations / 68 levels / 317 mandatory requirements");
        Check(snapshot.Stations.SelectMany(station => station.Levels).SelectMany(level => level.Items).Count(item => item.FoundInRaid) == 76,"29 obsolete FIR flags must be removed, not all FIR requirements");
        var api = FakeApi(snapshot);
        var parsed = HideoutWikiParser.ParseApiResponse(api);
        Check(parsed.Stations.Sum(station => station.Levels.Sum(level => level.Items.Count)) == 317,"optional wall cleaning and holiday station must not inflate mandatory quantities");
        var rule = parsed.SeasonRule!;
        Check(!HideoutWikiParser.RequiresFir(true,ProfileType.SeasonalPvp,rule,new DateTimeOffset(2026,10,26,0,0,0,TimeSpan.Zero)),"current seasonal FIR waiver must apply");
        Check(HideoutWikiParser.RequiresFir(true,ProfileType.Pve,rule,DateTimeOffset.UtcNow) && HideoutWikiParser.RequiresFir(true,ProfileType.Pvp,rule,DateTimeOffset.UtcNow),"seasonal waiver must never leak into PVE or permanent PVP");
        Check(HideoutWikiParser.RequiresFir(true,ProfileType.SeasonalPvp,rule,new DateTimeOffset(2026,12,7,0,0,0,TimeSpan.Zero)),"waiver must expire on the published season end date");
        Check(HideoutWikiParser.RequiresFir(true,ProfileType.SeasonalPvp,rule,new DateTimeOffset(2026,8,2,0,0,0,TimeSpan.Zero)),"waiver must not apply before the season starts");

        var directory = Path.Combine(Path.GetTempPath(),"tarkov-hideout-wiki-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory,"data.db");
        try
        {
            File.Copy(Path.Combine("TarkovHelper","Assets","tarkov_data.db"),database);
            var identities = Rows(database,"SELECT Id,StationId,Level,ItemId,Count FROM HideoutItemRequirements ORDER BY Id");
            var otherData = Fingerprint(database,true);
            using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                connection.Open();
                using var corrupt = new SqliteCommand(@"UPDATE HideoutItemRequirements SET FoundInRaid=1 WHERE StationId=(SELECT Id FROM HideoutStations WHERE Name='Workbench') AND Level=2 AND ItemName='Toolset'",connection);
                corrupt.ExecuteNonQuery();
            }
            using var client = new HttpClient(new WikiHandler(api));
            var partial = DatabaseUpdateService.RefreshSourcesAsync(database,client).GetAwaiter().GetResult();
            Check(partial.Success && partial.IsWarning && partial.WasUpdated,"one unavailable source must not roll back the successfully refreshed hideout");
            Check(identities == Rows(database,"SELECT Id,StationId,Level,ItemId,Count FROM HideoutItemRequirements ORDER BY Id"),"refresh must preserve requirement IDs, item IDs and unchanged quantities");
            Check(otherData == Fingerprint(database,true),"hideout refresh must not modify quests, maps, station identities or prerequisites");
            var repeated = new HideoutWikiRefreshService(client).RefreshAsync(database).GetAwaiter().GetResult();
            Check(!repeated.WasChanged && repeated.Updated == 0,"same Wiki data must be idempotent");
            Check(Directory.GetFiles(Path.Combine(directory,"Backups"),"*.db").Length >= 1,"first write must have a SQLite backup");
            var backup = Directory.GetFiles(Path.Combine(directory,"Backups"),"*.db").First();
            Check(Rows(backup,"PRAGMA quick_check").Contains("ok"),"backup must be a valid database");

            var beforeFailure = Fingerprint(database,false);
            var unknown = Clone(snapshot);
            unknown.Stations[0].Levels[0].Items[0] = new("Unresolved future material",1,false);
            ExpectFailure(() => HideoutWikiRefreshService.ApplyAsync(database,unknown).GetAwaiter().GetResult());
            Check(beforeFailure == Fingerprint(database,false),"unresolved items must leave the whole database untouched");
            var incomplete = Clone(snapshot); incomplete.Stations.RemoveAt(0);
            ExpectFailure(() => HideoutWikiRefreshService.ApplyAsync(database,incomplete).GetAwaiter().GetResult());
            Check(beforeFailure == Fingerprint(database,false),"partial Wiki tables must not erase requirements");
            using (var failed = new HttpClient(new WikiHandler("invalid JSON")))
                ExpectFailure(() => new HideoutWikiRefreshService(failed).RefreshAsync(database).GetAwaiter().GetResult());
            Check(beforeFailure == Fingerprint(database,false),"malformed HTTP response must preserve existing data");

            CheckProductionLoader(database);

            var newer = Clone(snapshot) with { Revision = snapshot.Revision+1 };
            newer.Stations[0].Levels[0].Items[0] = newer.Stations[0].Levels[0].Items[0] with { Count = newer.Stations[0].Levels[0].Items[0].Count+1 };
            var quantityChange = HideoutWikiRefreshService.ApplyAsync(database,newer).GetAwaiter().GetResult();
            Check(quantityChange.Updated == 1 && quantityChange.Added == 0 && quantityChange.Removed == 0,"a real future quantity change must update just its existing row");
            var previousCpuFanId = Rows(database,"SELECT DISTINCT ItemId FROM HideoutItemRequirements WHERE ItemName='CPU fan'");
            var addedMaterial = Clone(newer) with { Revision = newer.Revision+1 };
            addedMaterial.Stations.Single(station => station.Name=="Heating").Levels.Single(level => level.Level==1).Items.Add(new("CPU fan",2,false));
            var addition = HideoutWikiRefreshService.ApplyAsync(database,addedMaterial).GetAwaiter().GetResult();
            Check(addition.Added==1 && addition.Removed==0,"new material at an existing facility must be imported safely");
            Check(Rows(database,"SELECT ItemId FROM HideoutItemRequirements WHERE StationId=(SELECT Id FROM HideoutStations WHERE Name='Heating') AND Level=1 AND ItemName='CPU fan'")==previousCpuFanId,
                "new requirements must reuse the existing inventory key rather than mix Wiki IDs with legacy game IDs");
            var afterNewer = Fingerprint(database,false);
            Check(!HideoutWikiRefreshService.ApplyAsync(database,snapshot,true).GetAwaiter().GetResult().WasChanged && afterNewer == Fingerprint(database,false),"old bundled data must never overwrite a newer Wiki refresh");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory,true); }
    }

    private static void CheckProductionLoader(string database)
    {
        var loader = HideoutDbService.Instance;
        var profile = ProfileService.Instance;
        var profileField = typeof(ProfileService).GetField("_currentProfile",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var fields = new[] { "_databasePath","_allStations","_stationsById","_isLoaded" }.Select(name => typeof(HideoutDbService).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!).ToArray();
        var oldFields = fields.Select(field => field.GetValue(loader)).ToArray();
        var oldProfile = profileField.GetValue(profile);
        var progress = new HideoutProgressService();
        var progressFields = BindingFlags.Instance|BindingFlags.NonPublic;
        var savedProgress = new HideoutProgress(); savedProgress.Modules["workbench"] = 1;
        typeof(HideoutProgressService).GetField("_progress",progressFields)!.SetValue(progress,savedProgress);
        try
        {
            fields[0].SetValue(loader,database);
            int? total = null;
            foreach (var current in new[] { ProfileType.Pve,ProfileType.SeasonalPvp,ProfileType.Pve })
            {
                profileField.SetValue(profile,current);
                Check(loader.LoadStationsAsync().GetAwaiter().GetResult(),"production hideout loader must load refreshed data");
                var requiredFir = loader.AllStations.SelectMany(station => station.Levels).SelectMany(level => level.ItemRequirements).Count(item => item.FoundInRaid);
                var seasonActive = !HideoutWikiParser.RequiresFir(true,current,JsonSerializer.Deserialize<HideoutWikiSnapshot>(File.ReadAllText(HideoutWikiRefreshService.SnapshotPath))!.SeasonRule,DateTimeOffset.UtcNow);
                Check(requiredFir == (seasonActive ? 0 : 76),"profile switches must affect displayed FIR only, not stored base rules");
                typeof(HideoutProgressService).GetField("_allModules",progressFields)!.SetValue(progress,loader.AllStations.ToList());
                typeof(HideoutProgressService).GetField("_loadedProfile",progressFields)!.SetValue(progress,current);
                typeof(HideoutProgressService).GetMethod("OnHideoutDataRefreshed",progressFields)!.Invoke(progress,new object?[] { null,EventArgs.Empty });
                var items = progress.GetAllRemainingItemRequirements();
                var count = items.Values.Sum(item => item.HideoutCount);
                total ??= count;
                Check(total == count && progress.GetCurrentLevel("workbench") == 1,"profile policy and live rebind must preserve quantities and built levels");
                Check(!seasonActive || items.Values.All(item => item.HideoutFIRCount == 0),"seasonal aggregated item requirements must not retain stale FIR quantities");
            }
        }
        finally
        {
            loader.DataRefreshed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler),progress,typeof(HideoutProgressService).GetMethod("OnHideoutDataRefreshed",progressFields)!);
            for(var index=0;index<fields.Length;index++) fields[index].SetValue(loader,oldFields[index]);
            profileField.SetValue(profile,oldProfile);
        }
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (Exception) { return; }
        throw new Exception("Expected a fail-closed refresh failure");
    }
    private static string Rows(string path,string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly"); connection.Open();
        using var command = new SqliteCommand(sql,connection); using var reader = command.ExecuteReader();
        var rows = new List<object[]>(); while(reader.Read()) {var values=new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values);}
        return JsonSerializer.Serialize(rows);
    }
    private static string Fingerprint(string path,bool excludeRequirements)
    {
        using var connection=new SqliteConnection($"Data Source={path};Mode=ReadOnly");connection.Open();
        using var select=new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name",connection);
        using var reader=select.ExecuteReader();var tables=new List<string>();while(reader.Read()) tables.Add(reader.GetString(0));
        var text=new StringBuilder();foreach(var table in tables)
            if (!excludeRequirements || table is not ("HideoutItemRequirements" or "HideoutWikiSyncState")) text.Append(table).Append(Rows(path,"SELECT * FROM \""+table.Replace("\"","\"\"")+"\" ORDER BY rowid"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    private static string FakeApi(HideoutWikiSnapshot snapshot)
    {
        var text=new StringBuilder("{{Seasonal change|no items required for module upgrades need to be found in raid.}}\n");
        foreach(var station in snapshot.Stations)
        {
            text.Append("{| class=\"wikitable\"\n! colspan=4 | ").Append(station.Name).Append("\n|-\n!Level\n!Requirements\n|-\n");
            foreach(var level in station.Levels)
            {
                text.Append('!').Append(level.Level).Append("\n|\n");
                foreach(var item in level.Items) text.Append("* ").Append(item.Count.ToString("N0",CultureInfo.InvariantCulture)).Append(" [[").Append(item.Name).Append("]]").Append(item.FoundInRaid ? " found [[Found in raid|in raid]]" : "").Append('\n');
                if(station.Name=="Defective Wall" && level.Level is >=2 and <=5) text.Append("* Optional Task:\n* 1 [[Fleece fabric]]\n");
                text.Append("|\n* Not an item requirement\n| Instant\n|-\n");
            }
            text.Append("|}\n");
        }
        text.Append("{| class=\"wikitable\"\n! colspan=4 | Christmas Tree\n|-\n!1\n|\n* 1 [[Unknown ornament]]\n|\nHoliday\n|}\n");
        var rule=snapshot.SeasonRule!;
        object Page(string title,string body,long revision) => new { title,revisions=new[]{new{revid=revision,timestamp=snapshot.SourceUpdatedAt,slots=new{main=new{content=body}}}}};
        var season=$"=={rule.Name}==\nSeason 1 started on {rule.StartsOn.ToString("MMMM d, yyyy",CultureInfo.InvariantCulture)} and ends on {rule.EndsOnExclusive.ToString("MMMM d, yyyy",CultureInfo.InvariantCulture)}.\n* [[Hideout]] zones do not require items to be [[found in raid]].\n";
        return JsonSerializer.Serialize(new{query=new{pages=new[]{Page("Hideout",text.ToString(),snapshot.Revision),Page("Seasons",season,rule.SourceRevision),Page("Template:Seasonal change",$"[[Seasons#Season_1|{rule.Name}]]",1)}}});
    }
    private sealed class WikiHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Query.Contains("titles=Hideout")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(response)});
            throw new HttpRequestException("Simulated unrelated source outage");
        }
    }
}
