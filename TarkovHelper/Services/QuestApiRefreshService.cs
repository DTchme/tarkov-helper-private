using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Models.Map;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Rebuilds the quest-related part of tarkov_data.db from the current tarkov.dev GraphQL API.
/// The regular and PvE datasets are requested separately through the API gameMode argument.
/// Existing map marker coordinates are retained when the quest BSG id and English objective
/// description still match.
/// </summary>
public sealed class QuestApiRefreshService
{
    private const string GraphQlEndpoint = "https://api.tarkov.dev/graphql";
    private const string TrackerQuestEndpoint =
        "https://raw.githubusercontent.com/TarkovTracker/tarkovdata/refs/heads/master/quests.json";
    private const int MinimumExpectedQuestCount = 300;

    private static readonly ILogger _log = Log.For<QuestApiRefreshService>();
    private static readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly HttpClient _httpClient;

    public QuestApiRefreshService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<QuestApiRefreshResult> RefreshAsync(
        ProfileType profileType,
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        string? tempPath = null;

        try
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("tarkov_data.db를 찾을 수 없습니다.", databasePath);

            // tarkov.dev currently exposes regular/PvE structured task datasets.
            // Seasonal PvP shares the regular base quest set; season-exclusive presence is
            // supplied by the Wiki overlay and filtered separately by profile.
            var mode = profileType == ProfileType.Pve ? "pve" : "regular";
            _log.Info($"Fetching live quest data from tarkov.dev (profile={profileType}, mode={mode})");

            var apiJson = await FetchQuestDataAsync(mode, cancellationToken);
            using var document = JsonDocument.Parse(apiJson);

            if (document.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var messages = errors.EnumerateArray()
                    .Select(e => GetString(e, "message"))
                    .Where(m => !string.IsNullOrWhiteSpace(m));
                throw new InvalidOperationException("tarkov.dev GraphQL 오류: " + string.Join(" | ", messages));
            }

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("en", out var englishTasks) ||
                englishTasks.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("tarkov.dev 응답에 퀘스트 데이터가 없습니다.");
            }

            var apiQuestCount = englishTasks.GetArrayLength();
            if (apiQuestCount < MinimumExpectedQuestCount)
            {
                throw new InvalidOperationException(
                    $"API 퀘스트 수가 비정상적으로 적습니다 ({apiQuestCount}). 기존 DB를 유지합니다.");
            }

            // tarkov.dev tasks currently contain quest/objective metadata but not the community
            // map pin percentages used by its SVG maps. Fetch those separately and enrich only
            // objectives that can be matched conservatively. A coordinate-source outage must not
            // block the quest database refresh.
            var coordinateCatalog = await TryFetchCoordinateCatalogAsync(databasePath, cancellationToken);

            var databaseDirectory = Path.GetDirectoryName(databasePath)
                ?? AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(databaseDirectory);

            tempPath = Path.Combine(databaseDirectory, $"tarkov_data.refresh.{Guid.NewGuid():N}.tmp");
            File.Copy(databasePath, tempPath, true);

            var stats = await RewriteQuestTablesAsync(tempPath, data, coordinateCatalog, cancellationToken);
            if (stats.QuestCount < MinimumExpectedQuestCount || stats.ObjectiveCount <= 0)
            {
                throw new InvalidOperationException(
                    $"갱신 결과 검증 실패: 퀘스트 {stats.QuestCount}, 목표 {stats.ObjectiveCount}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var backupDirectory = Path.Combine(databaseDirectory, "Backups");
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(
                backupDirectory,
                $"tarkov_data_{mode}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");

            SqliteConnection.ClearAllPools();
            File.Copy(databasePath, backupPath, true);
            File.Move(tempPath, databasePath, true);
            tempPath = null;
            SqliteConnection.ClearAllPools();

            _log.Info(
                $"Quest DB refresh completed: quests={stats.QuestCount}, " +
                $"requirements={stats.RequirementCount}, objectives={stats.ObjectiveCount}, " +
                $"items={stats.RequiredItemCount}, refreshedMarkers={stats.RefreshedMarkerCount}, " +
                $"preservedMarkers={stats.PreservedMarkerCount}");

            return new QuestApiRefreshResult(
                stats.QuestCount,
                stats.RequirementCount,
                stats.ObjectiveCount,
                stats.RequiredItemCount,
                stats.RefreshedMarkerCount,
                stats.PreservedMarkerCount,
                backupPath,
                mode);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }

            _refreshLock.Release();
        }
    }

    private async Task<string> FetchQuestDataAsync(string mode, CancellationToken cancellationToken)
    {
        const string query = """
            query QuestRefresh($mode: GameMode!) {
              en: tasks(lang: en, gameMode: $mode) {
                id
                name
                normalizedName
                wikiLink
                minPlayerLevel
                kappaRequired
                factionName
                availableDelaySecondsMin
                requiredPrestige { prestigeLevel }
                trader { name }
                map { name normalizedName }
                taskRequirements {
                  task { id }
                  status
                }
                objectives {
                  id
                  type
                  description
                  maps { name normalizedName }
                  optional
                  ... on TaskObjectiveItem {
                    items { id name normalizedName }
                    count
                    foundInRaid
                    dogTagLevel
                  }
                  ... on TaskObjectiveShoot {
                    targetNames
                    count
                  }
                  ... on TaskObjectiveExtract {
                    count
                  }
                  ... on TaskObjectiveExperience {
                    count
                  }
                  ... on TaskObjectiveQuestItem {
                    questItem { id name normalizedName }
                    count
                  }
                  ... on TaskObjectiveUseItem {
                    useAny { id name normalizedName }
                    count
                  }
                  ... on TaskObjectiveBuildItem {
                    item { id name normalizedName }
                  }
                }
                failConditions {
                  id
                  type
                  description
                  ... on TaskObjectiveTaskStatus {
                    task { id }
                    status
                  }
                }
              }
              ko: tasks(lang: ko, gameMode: $mode) { id name }
              ja: tasks(lang: ja, gameMode: $mode) { id name }
            }
            """;

        var requestBody = JsonSerializer.Serialize(new
        {
            query,
            variables = new { mode }
        });

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            HttpResponseMessage response;
            string body;

            try
            {
                using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync(GraphQlEndpoint, content, cancellationToken);
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= maxAttempts)
                {
                    throw new TarkovDevUnavailableException(
                        "tarkov.dev에 연결할 수 없습니다. 기존 퀘스트 DB를 유지합니다. 잠시 후 다시 시도하세요.",
                        ex);
                }

                _log.Warning(
                    $"tarkov.dev network request failed (attempt {attempt}/{maxAttempts}); retrying. {ex.Message}");
                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                    return body;

                if (!IsTransientGraphQlFailure(response.StatusCode, body))
                {
                    throw new HttpRequestException(
                        $"tarkov.dev 요청 실패: {(int)response.StatusCode} {response.ReasonPhrase}\n{body}");
                }

                if (attempt >= maxAttempts)
                {
                    throw new TarkovDevUnavailableException(
                        $"tarkov.dev GraphQL 서버가 일시적으로 사용할 수 없습니다 " +
                        $"({(int)response.StatusCode} {response.ReasonPhrase}). " +
                        "기존 퀘스트 DB를 유지합니다. 잠시 후 다시 시도하세요.");
                }

                _log.Warning(
                    $"tarkov.dev GraphQL server unavailable " +
                    $"(attempt {attempt}/{maxAttempts}, {(int)response.StatusCode}); retrying.");
            }

            await Task.Delay(GetRetryDelay(attempt), cancellationToken);
        }

        throw new TarkovDevUnavailableException(
            "tarkov.dev GraphQL 서버가 일시적으로 사용할 수 없습니다. 기존 퀘스트 DB를 유지합니다.");
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        return attempt switch
        {
            1 => TimeSpan.FromSeconds(1),
            _ => TimeSpan.FromSeconds(3)
        };
    }

    private static bool IsTransientGraphQlFailure(
        System.Net.HttpStatusCode statusCode,
        string responseBody)
    {
        var numericStatus = (int)statusCode;
        if (numericStatus is 408 or 425 or 429 or 500 or 502 or 503 or 504)
            return true;

        // tarkov.dev may return HTTP 422 while its GraphQL backend is temporarily unavailable.
        // Treat only explicit outage wording as transient so real query/schema errors remain visible.
        return responseBody.Contains("GraphQL server unavailable", StringComparison.OrdinalIgnoreCase) ||
               responseBody.Contains("server unavailable", StringComparison.OrdinalIgnoreCase) ||
               responseBody.Contains("try again later", StringComparison.OrdinalIgnoreCase);
    }


    private async Task<TrackerCoordinateCatalog> TryFetchCoordinateCatalogAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TrackerQuestEndpoint);
            request.Headers.UserAgent.ParseAdd("TarkovHelper/1.5.10");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"좌표 데이터 요청 실패: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            using var document = JsonDocument.Parse(json);
            var configs = LoadMapConfigs(databasePath);
            var catalog = BuildTrackerCoordinateCatalog(document.RootElement, configs);
            _log.Info(
                $"Loaded community quest coordinates: quests={catalog.QuestCount}, " +
                $"points={catalog.PointCount}, maps={catalog.MapConfigCount}");
            return catalog;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(
                "Quest coordinate refresh source could not be loaded; " +
                $"legacy coordinates will be preserved where possible. {ex.Message}");
            return TrackerCoordinateCatalog.Empty;
        }
    }

    private static List<MapConfig> LoadMapConfigs(string databasePath)
    {
        var databaseDirectory = Path.GetDirectoryName(databasePath)
            ?? AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(databaseDirectory, "DB", "Data", "map_configs.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "DB", "Data", "map_configs.json")
        };

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;

            var json = File.ReadAllText(path, Encoding.UTF8);
            var root = JsonSerializer.Deserialize<MapConfigList>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (root?.Maps.Count > 0)
                return root.Maps;
        }

        throw new FileNotFoundException("map_configs.json을 찾을 수 없습니다.");
    }

    private static TrackerCoordinateCatalog BuildTrackerCoordinateCatalog(
        JsonElement root,
        IReadOnlyList<MapConfig> mapConfigs)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("퀘스트 좌표 JSON 형식이 올바르지 않습니다.");

        var quests = new Dictionary<string, List<TrackerGpsPoint>>(StringComparer.OrdinalIgnoreCase);
        var pointCount = 0;

        foreach (var quest in root.EnumerateArray())
        {
            var gameId = GetString(quest, "gameId");
            if (string.IsNullOrWhiteSpace(gameId) ||
                !quest.TryGetProperty("objectives", out var objectives) ||
                objectives.ValueKind != JsonValueKind.Array)
                continue;

            var points = new List<TrackerGpsPoint>();
            var objectiveOrder = 0;
            foreach (var objective in objectives.EnumerateArray())
            {
                if (!objective.TryGetProperty("gps", out var gps) ||
                    gps.ValueKind != JsonValueKind.Object ||
                    !TryGetDouble(gps, "leftPercent", out var leftPercent) ||
                    !TryGetDouble(gps, "topPercent", out var topPercent))
                {
                    objectiveOrder++;
                    continue;
                }

                if (leftPercent < 0 || leftPercent > 100 || topPercent < 0 || topPercent > 100)
                {
                    objectiveOrder++;
                    continue;
                }

                var locationId = GetNullableInt(objective, "location") ?? -1;
                points.Add(new TrackerGpsPoint(
                    objectiveOrder,
                    GetString(objective, "type") ?? string.Empty,
                    locationId,
                    leftPercent,
                    topPercent,
                    GetString(gps, "floor")));
                pointCount++;
                objectiveOrder++;
            }

            if (points.Count > 0)
                quests[gameId] = points;
        }

        return new TrackerCoordinateCatalog(quests, mapConfigs.ToList(), pointCount);
    }

    private static Dictionary<string, string> BuildCoordinateAssignments(
        JsonElement task,
        TrackerCoordinateCatalog catalog)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var questBsgId = GetString(task, "id");
        if (string.IsNullOrWhiteSpace(questBsgId) ||
            !catalog.Quests.TryGetValue(questBsgId, out var trackerPoints) ||
            trackerPoints.Count == 0 ||
            !task.TryGetProperty("objectives", out var objectives) ||
            objectives.ValueKind != JsonValueKind.Array)
            return result;

        var apiPoints = new List<ApiCoordinateObjective>();
        var apiOrder = 0;
        foreach (var objective in objectives.EnumerateArray())
        {
            var id = GetString(objective, "id");
            var type = GetString(objective, "type") ?? string.Empty;
            var description = GetString(objective, "description") ?? string.Empty;
            var mapName = FirstMapName(objective);
            if (!string.IsNullOrWhiteSpace(id) &&
                !string.IsNullOrWhiteSpace(mapName) &&
                IsCoordinateCapableObjective(type, description))
            {
                apiPoints.Add(new ApiCoordinateObjective(id, apiOrder, type, description, mapName));
            }
            apiOrder++;
        }

        if (apiPoints.Count == 0)
            return result;

        var pairs = new List<(ApiCoordinateObjective Api, TrackerGpsPoint Tracker)>();

        // Safest case: both datasets expose the same number of location-bearing objectives.
        // Their quest objective order is stable even when localized descriptions differ.
        if (apiPoints.Count == trackerPoints.Count)
        {
            for (var i = 0; i < apiPoints.Count; i++)
                pairs.Add((apiPoints[i], trackerPoints[i]));
        }
        else if (trackerPoints.Select(PointSignature).Distinct().Count() == 1)
        {
            // Some quests repeat one physical point for locate + mark/stash objectives.
            // If the coordinate source contains exactly one distinct point, using it for all
            // mapped objectives cannot move them to different locations accidentally.
            foreach (var apiPoint in apiPoints)
                pairs.Add((apiPoint, trackerPoints[0]));
        }
        else
        {
            // Conservative fallback: only accept an unambiguous type-compatible match.
            var unused = new List<TrackerGpsPoint>(trackerPoints);
            foreach (var apiPoint in apiPoints)
            {
                var matches = unused
                    .Where(point => AreCoordinateTypesCompatible(apiPoint.Type, point.Type))
                    .ToList();
                if (matches.Count != 1)
                    continue;
                pairs.Add((apiPoint, matches[0]));
                unused.Remove(matches[0]);
            }
        }

        foreach (var pair in pairs)
        {
            var mapConfig = catalog.MapConfigs.FirstOrDefault(config =>
                config.MatchesMapName(pair.Api.MapName));
            if (mapConfig == null || mapConfig.ImageWidth <= 0 || mapConfig.ImageHeight <= 0)
                continue;

            var expectedMapName = TrackerMapName(pair.Tracker.LocationId);
            if (!string.IsNullOrWhiteSpace(expectedMapName) &&
                !mapConfig.MatchesMapName(expectedMapName))
                continue;

            var screenX = pair.Tracker.LeftPercent / 100d * mapConfig.ImageWidth;
            var screenY = pair.Tracker.TopPercent / 100d * mapConfig.ImageHeight;
            var (gameX, gameZ) = mapConfig.ScreenToGameForPlayer(screenX, screenY);
            if (!double.IsFinite(gameX) || !double.IsFinite(gameZ))
                continue;

            result[pair.Api.Id] = JsonSerializer.Serialize(new[]
            {
                new
                {
                    X = Math.Round(gameX, 6),
                    Y = 0d,
                    Z = Math.Round(gameZ, 6),
                    FloorId = pair.Tracker.Floor
                }
            });
        }

        return result;
    }

    private static void ApplyKnownCoordinateCorrections(
        JsonElement task,
        IDictionary<string, string> coordinateAssignments)
    {
        const string topSecretQuestId = "626bd75d5bef5d7d590bd415";
        if (!string.Equals(GetString(task, "id"), topSecretQuestId, StringComparison.OrdinalIgnoreCase) ||
            !task.TryGetProperty("objectives", out var objectives) ||
            objectives.ValueKind != JsonValueKind.Array)
            return;

        foreach (var objective in objectives.EnumerateArray())
        {
            var objectiveId = GetString(objective, "id");
            var description = NormalizeText(GetString(objective, "description") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(objectiveId) ||
                !description.Contains("military hdd with archived flight routes", StringComparison.Ordinal))
                continue;

            // TarkovTracker's legacy Lighthouse percentage point uses an obsolete map canvas.
            // This world coordinate matches the radar commandant office quest marker and the
            // commandant-room key marker bundled from the current Lighthouse map dataset.
            coordinateAssignments[objectiveId] = JsonSerializer.Serialize(new[]
            {
                new
                {
                    X = 343.402d,
                    Y = 1d,
                    Z = 544.7055d,
                    FloorId = (string?)null
                }
            });
            break;
        }
    }

    private static bool IsCoordinateCapableObjective(string apiType, string description)
    {
        var value = NormalizeText(apiType + " " + description);
        return value.Contains("visit", StringComparison.Ordinal) ||
               value.Contains("locate", StringComparison.Ordinal) ||
               value.Contains("mark", StringComparison.Ordinal) ||
               value.Contains("plant", StringComparison.Ordinal) ||
               value.Contains("stash", StringComparison.Ordinal) ||
               value.Contains("place", StringComparison.Ordinal) ||
               value.Contains("questitem", StringComparison.Ordinal) ||
               value.Contains("pickup", StringComparison.Ordinal) ||
               value.Contains("extract", StringComparison.Ordinal) ||
               value.Contains("useitem", StringComparison.Ordinal);
    }

    private static bool AreCoordinateTypesCompatible(string apiType, string trackerType)
    {
        var api = NormalizeCoordinateType(apiType);
        var tracker = NormalizeCoordinateType(trackerType);
        if (api == tracker)
            return true;

        return (tracker == "locate" && api == "visit") ||
               (tracker == "mark" && (api == "mark" || api == "plant" || api == "useitem")) ||
               (tracker == "place" && (api == "plant" || api == "useitem")) ||
               (tracker == "pickup" && (api == "questitem" || api == "visit"));
    }

    private static string NormalizeCoordinateType(string value)
    {
        var normalized = NormalizeText(value);
        if (normalized.Contains("locate", StringComparison.Ordinal)) return "locate";
        if (normalized.Contains("visit", StringComparison.Ordinal)) return "visit";
        if (normalized.Contains("mark", StringComparison.Ordinal)) return "mark";
        if (normalized.Contains("plant", StringComparison.Ordinal) || normalized.Contains("stash", StringComparison.Ordinal)) return "plant";
        if (normalized.Contains("useitem", StringComparison.Ordinal)) return "useitem";
        if (normalized.Contains("pickup", StringComparison.Ordinal)) return "pickup";
        if (normalized.Contains("questitem", StringComparison.Ordinal)) return "questitem";
        if (normalized.Contains("extract", StringComparison.Ordinal)) return "extract";
        return normalized;
    }

    private static string PointSignature(TrackerGpsPoint point) =>
        $"{point.LocationId}:{point.LeftPercent:F4}:{point.TopPercent:F4}:{point.Floor}";

    private static string? TrackerMapName(int locationId) => locationId switch
    {
        0 => "Factory",
        1 => "Customs",
        2 => "Woods",
        3 => "Shoreline",
        4 => "Interchange",
        5 => "Laboratory",
        6 => "Reserve",
        7 => "Lighthouse",
        8 => "Streets of Tarkov",
        9 => "Ground Zero",
        _ => null
    };

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value);
    }

    private async Task<RewriteStats> RewriteQuestTablesAsync(
        string databasePath,
        JsonElement data,
        TrackerCoordinateCatalog coordinateCatalog,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await EnsureColumnAsync(
                connection,
                transaction,
                "Quests",
                "NormalizedName",
                "TEXT",
                cancellationToken);

            var existingQuestIds = await LoadExistingQuestIdsAsync(connection, transaction, cancellationToken);
            var existingObjectives = await LoadExistingObjectivesAsync(connection, transaction, cancellationToken);
            var itemIds = await LoadItemIdsAsync(connection, transaction, cancellationToken);
            var ensuredItemMappings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var koNames = ReadTranslatedNames(data, "ko");
            var jaNames = ReadTranslatedNames(data, "ja");
            var englishTasks = data.GetProperty("en");

            await ExecuteAsync(connection, transaction, "DELETE FROM OptionalQuests", cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM QuestRequiredItems", cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM QuestObjectives", cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM QuestRequirements", cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM Quests", cancellationToken);

            var questIdByBsg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedInternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var taskElements = englishTasks.EnumerateArray().ToList();
            var updatedAt = DateTime.UtcNow.ToString("o");

            foreach (var task in taskElements)
            {
                var bsgId = GetString(task, "id");
                if (string.IsNullOrWhiteSpace(bsgId))
                    continue;

                var name = GetString(task, "name") ?? bsgId;
                var normalizedName = GetString(task, "normalizedName") ?? BuildNormalizedName(name);
                var trader = GetNestedString(task, "trader", "name") ?? string.Empty;
                var wikiLink = GetString(task, "wikiLink");
                var location = CollectQuestMaps(task);
                if (ArenaQuestExclusionPolicy.IsArenaLocation(location))
                {
                    _log.Debug($"Excluded Arena quest from tarkov.dev refresh: {name} ({bsgId})");
                    continue;
                }

                var internalId = BuildInternalQuestId(task, bsgId, existingQuestIds, usedInternalIds);
                questIdByBsg[bsgId] = internalId;

                var minLevel = GetNullableInt(task, "minPlayerLevel");
                var kappaRequired = GetBool(task, "kappaRequired") ? 1 : 0;
                var faction = GetString(task, "factionName");
                var requiredPrestigeLevel = GetNestedNullableInt(task, "requiredPrestige", "prestigeLevel");

                await using var command = new SqliteCommand(@"
                    INSERT INTO Quests (
                        Id, BsgId, Name, NameEN, NameKO, NameJA, NormalizedName, WikiPageLink,
                        Trader, Location, MinLevel, MinLevelApproved,
                        MinScavKarma, MinScavKarmaApproved, UpdatedAt,
                        KappaRequired, Faction, IsApproved,
                        RequiredEdition, RequiredEditionApproved,
                        ExcludedEdition, ExcludedEditionApproved,
                        RequiredDecodeCount, RequiredDecodeCountApproved,
                        RequiredPrestigeLevel, RequiredPrestigeLevelApproved)
                    VALUES (
                        @id, @bsgId, @name, @nameEn, @nameKo, @nameJa, @normalizedName, @wiki,
                        @trader, @location, @minLevel, 0,
                        NULL, 0, @updatedAt,
                        @kappa, @faction, 0,
                        NULL, 0, NULL, 0, NULL, 0, @requiredPrestigeLevel, 0)",
                    connection, transaction);

                command.Parameters.AddWithValue("@id", internalId);
                command.Parameters.AddWithValue("@bsgId", bsgId);
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@nameEn", name);
                command.Parameters.AddWithValue("@nameKo", ValueOrDbNull(koNames.GetValueOrDefault(bsgId)));
                command.Parameters.AddWithValue("@nameJa", ValueOrDbNull(jaNames.GetValueOrDefault(bsgId)));
                command.Parameters.AddWithValue("@normalizedName", normalizedName);
                command.Parameters.AddWithValue("@wiki", ValueOrDbNull(wikiLink));
                command.Parameters.AddWithValue("@trader", trader);
                command.Parameters.AddWithValue("@location", location);
                command.Parameters.AddWithValue("@minLevel", minLevel.HasValue ? (object)minLevel.Value : DBNull.Value);
                command.Parameters.AddWithValue("@updatedAt", updatedAt);
                command.Parameters.AddWithValue("@kappa", kappaRequired);
                command.Parameters.AddWithValue("@faction", ValueOrDbNull(faction));
                command.Parameters.AddWithValue("@requiredPrestigeLevel", requiredPrestigeLevel.HasValue ? (object)requiredPrestigeLevel.Value : DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var requirementCount = 0;
            var objectiveCount = 0;
            var requiredItemCount = 0;
            var refreshedMarkerCount = 0;
            var preservedMarkerCount = 0;
            var alternativePairs = new HashSet<(string QuestId, string AlternativeId)>();

            foreach (var task in taskElements)
            {
                var questBsgId = GetString(task, "id");
                if (string.IsNullOrWhiteSpace(questBsgId) ||
                    !questIdByBsg.TryGetValue(questBsgId, out var questId))
                    continue;

                var delaySeconds = GetNullableInt(task, "availableDelaySecondsMin");
                var delayMinutes = delaySeconds.HasValue
                    ? (int?)Math.Ceiling(delaySeconds.Value / 60d)
                    : null;

                if (task.TryGetProperty("taskRequirements", out var requirements) &&
                    requirements.ValueKind == JsonValueKind.Array)
                {
                    foreach (var requirement in requirements.EnumerateArray())
                    {
                        var requiredBsgId = GetNestedString(requirement, "task", "id");
                        if (string.IsNullOrWhiteSpace(requiredBsgId) ||
                            !questIdByBsg.TryGetValue(requiredBsgId, out var requiredQuestId))
                            continue;

                        var statuses = ReadStringArray(requirement, "status");
                        var requirementType = MapRequirementType(statuses);

                        // The live API does not expose a prerequisite OR-group identifier.
                        // Do not carry group IDs over from the pre-1.1 database because that can
                        // recreate obsolete quest-line relationships after a major overhaul.
                        const int groupId = 0;
                        var rowId = StableId("req", questBsgId, requiredBsgId, requirementType, groupId.ToString());

                        await using var command = new SqliteCommand(@"
                            INSERT INTO QuestRequirements (
                                Id, QuestId, RequiredQuestId, RequirementType,
                                DelayMinutes, GroupId, IsApproved, UpdatedAt)
                            VALUES (@id, @questId, @requiredQuestId, @type, @delayMinutes, @groupId, 0, @updatedAt)",
                            connection, transaction);
                        command.Parameters.AddWithValue("@id", rowId);
                        command.Parameters.AddWithValue("@questId", questId);
                        command.Parameters.AddWithValue("@requiredQuestId", requiredQuestId);
                        command.Parameters.AddWithValue("@type", requirementType);
                        command.Parameters.AddWithValue("@delayMinutes", delayMinutes.HasValue ? (object)delayMinutes.Value : DBNull.Value);
                        command.Parameters.AddWithValue("@groupId", groupId);
                        command.Parameters.AddWithValue("@updatedAt", updatedAt);
                        await command.ExecuteNonQueryAsync(cancellationToken);
                        requirementCount++;
                    }
                }

                if (task.TryGetProperty("objectives", out var objectives) &&
                    objectives.ValueKind == JsonValueKind.Array)
                {
                    var coordinateAssignments = BuildCoordinateAssignments(task, coordinateCatalog);
                    ApplyKnownCoordinateCorrections(task, coordinateAssignments);
                    var sortOrder = 0;
                    foreach (var objective in objectives.EnumerateArray())
                    {
                        var description = GetString(objective, "description")?.Trim();
                        if (string.IsNullOrWhiteSpace(description))
                            continue;

                        var apiObjectiveId = GetString(objective, "id") ?? sortOrder.ToString();
                        var normalizedDescription = NormalizeText(description);
                        existingObjectives.TryGetValue((questBsgId, normalizedDescription), out var existingObjective);

                        var objectiveId = existingObjective?.Id
                            ?? StableId("obj", questBsgId, apiObjectiveId);
                        var apiType = GetString(objective, "type") ?? "custom";
                        var objectiveType = MapObjectiveType(apiType, description);
                        var count = GetNullableInt(objective, "count");
                        var targetNames = ReadStringArray(objective, "targetNames");
                        var targetType = targetNames.Count > 0 ? string.Join(", ", targetNames) : null;
                        var isOptional = GetBool(objective, "optional");
                        var mapName = FirstMapName(objective) ?? existingObjective?.MapName;
                        var hasRefreshedCoordinates = coordinateAssignments.TryGetValue(
                            apiObjectiveId,
                            out var refreshedLocationPoints);
                        var locationPoints = hasRefreshedCoordinates
                            ? refreshedLocationPoints
                            : existingObjective?.LocationPoints;
                        var optionalPoints = existingObjective?.OptionalPoints;
                        var locationName = existingObjective?.LocationName;

                        if (hasRefreshedCoordinates)
                        {
                            refreshedMarkerCount++;
                        }
                        else if (!string.IsNullOrWhiteSpace(locationPoints) ||
                                 !string.IsNullOrWhiteSpace(optionalPoints))
                        {
                            preservedMarkerCount++;
                        }

                        string? firstItemId = null;
                        string? firstItemName = null;
                        var requiresFir = GetBool(objective, "foundInRaid");
                        var dogTagLevel = GetNullableInt(objective, "dogTagLevel");

                        var objectiveItems = EnumerateObjectiveItems(objective);
                        if (objectiveItems.Count > 0)
                        {
                            var itemSortOrder = 0;
                            foreach (var item in objectiveItems)
                            {
                                var itemBsgId = GetString(item, "id");
                                var itemName = GetString(item, "name") ?? itemBsgId ?? "Unknown item";
                                var itemNormalizedName = GetString(item, "normalizedName")
                                    ?? BuildNormalizedName(itemName);

                                var internalItemId = ResolveItemId(
                                    itemIds,
                                    itemBsgId,
                                    itemNormalizedName,
                                    itemName);

                                // The bundled legacy item table has no BSG IDs for most rows.
                                // Match by canonical name first, then create a minimal item row for
                                // genuinely new 1.1 items so required-item objectives are not dropped.
                                if (string.IsNullOrWhiteSpace(internalItemId))
                                {
                                    internalItemId = !string.IsNullOrWhiteSpace(itemBsgId)
                                        ? $"api-item:{itemBsgId}"
                                        : StableId("api-item", itemNormalizedName, itemName);
                                }

                                var mappingKey = $"{internalItemId}|{itemBsgId}";
                                if (ensuredItemMappings.Add(mappingKey))
                                {
                                    await EnsureItemMappingAsync(
                                        connection,
                                        transaction,
                                        internalItemId,
                                        itemBsgId,
                                        itemName,
                                        updatedAt,
                                        cancellationToken);

                                    AddItemLookupKeys(itemIds, internalItemId, itemBsgId, itemNormalizedName, itemName);
                                }

                                firstItemId ??= internalItemId;
                                firstItemName ??= itemName;

                                var itemCount = count.GetValueOrDefault(1);
                                var requiredItemRowId = StableId(
                                    "item", questBsgId, apiObjectiveId, itemBsgId ?? itemName, itemSortOrder.ToString());

                                await using var itemCommand = new SqliteCommand(@"
                                    INSERT INTO QuestRequiredItems (
                                        Id, QuestId, ItemId, ItemName, Count, RequiresFIR,
                                        RequirementType, SortOrder, DogtagMinLevel,
                                        IsApproved, UpdatedAt)
                                    VALUES (@id, @questId, @itemId, @itemName, @count, @fir,
                                        'Required', @sortOrder, @dogtagMinLevel, 0, @updatedAt)",
                                    connection, transaction);
                                itemCommand.Parameters.AddWithValue("@id", requiredItemRowId);
                                itemCommand.Parameters.AddWithValue("@questId", questId);
                                itemCommand.Parameters.AddWithValue("@itemId", ValueOrDbNull(internalItemId));
                                itemCommand.Parameters.AddWithValue("@itemName", itemName);
                                itemCommand.Parameters.AddWithValue("@count", itemCount);
                                itemCommand.Parameters.AddWithValue("@fir", requiresFir ? 1 : 0);
                                itemCommand.Parameters.AddWithValue("@sortOrder", itemSortOrder++);
                                itemCommand.Parameters.AddWithValue("@dogtagMinLevel", dogTagLevel.HasValue ? (object)dogTagLevel.Value : DBNull.Value);
                                itemCommand.Parameters.AddWithValue("@updatedAt", updatedAt);
                                await itemCommand.ExecuteNonQueryAsync(cancellationToken);
                                requiredItemCount++;
                            }
                        }

                        var conditions = JsonSerializer.Serialize(new
                        {
                            source = "tarkov.dev",
                            apiObjectiveId,
                            apiType,
                            optional = isOptional
                        });

                        await using var objectiveCommand = new SqliteCommand(@"
                            INSERT INTO QuestObjectives (
                                Id, QuestId, SortOrder, ObjectiveType, Description,
                                TargetType, TargetCount, ItemId, ItemName, RequiresFIR,
                                MapName, LocationName, LocationPoints, Conditions,
                                IsApproved, UpdatedAt, OptionalPoints, DogtagMinLevel)
                            VALUES (
                                @id, @questId, @sortOrder, @objectiveType, @description,
                                @targetType, @targetCount, @itemId, @itemName, @fir,
                                @mapName, @locationName, @locationPoints, @conditions,
                                0, @updatedAt, @optionalPoints, @dogtagMinLevel)",
                            connection, transaction);
                        objectiveCommand.Parameters.AddWithValue("@id", objectiveId);
                        objectiveCommand.Parameters.AddWithValue("@questId", questId);
                        objectiveCommand.Parameters.AddWithValue("@sortOrder", sortOrder++);
                        objectiveCommand.Parameters.AddWithValue("@objectiveType", objectiveType);
                        objectiveCommand.Parameters.AddWithValue("@description", description);
                        objectiveCommand.Parameters.AddWithValue("@targetType", ValueOrDbNull(targetType));
                        objectiveCommand.Parameters.AddWithValue("@targetCount", count.HasValue ? (object)count.Value : DBNull.Value);
                        objectiveCommand.Parameters.AddWithValue("@itemId", ValueOrDbNull(firstItemId));
                        objectiveCommand.Parameters.AddWithValue("@itemName", ValueOrDbNull(firstItemName));
                        objectiveCommand.Parameters.AddWithValue("@fir", requiresFir ? 1 : 0);
                        objectiveCommand.Parameters.AddWithValue("@mapName", ValueOrDbNull(mapName));
                        objectiveCommand.Parameters.AddWithValue("@locationName", ValueOrDbNull(locationName));
                        objectiveCommand.Parameters.AddWithValue("@locationPoints", ValueOrDbNull(locationPoints));
                        objectiveCommand.Parameters.AddWithValue("@conditions", conditions);
                        objectiveCommand.Parameters.AddWithValue("@updatedAt", updatedAt);
                        objectiveCommand.Parameters.AddWithValue("@optionalPoints", ValueOrDbNull(optionalPoints));
                        objectiveCommand.Parameters.AddWithValue("@dogtagMinLevel", dogTagLevel.HasValue ? (object)dogTagLevel.Value : DBNull.Value);
                        await objectiveCommand.ExecuteNonQueryAsync(cancellationToken);
                        objectiveCount++;
                    }
                }

                if (task.TryGetProperty("failConditions", out var failConditions) &&
                    failConditions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var condition in failConditions.EnumerateArray())
                    {
                        var alternativeBsgId = GetNestedString(condition, "task", "id");
                        var statuses = ReadStringArray(condition, "status");
                        if (string.IsNullOrWhiteSpace(alternativeBsgId) ||
                            !statuses.Any(s =>
                                s.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                                s.Equals("fail", StringComparison.OrdinalIgnoreCase)))
                            continue;

                        alternativePairs.Add((questBsgId, alternativeBsgId));
                        alternativePairs.Add((alternativeBsgId, questBsgId));
                    }
                }
            }

            var optionalCount = 0;
            foreach (var pair in alternativePairs)
            {
                if (!questIdByBsg.TryGetValue(pair.QuestId, out var questId) ||
                    !questIdByBsg.TryGetValue(pair.AlternativeId, out var alternativeId) ||
                    questId.Equals(alternativeId, StringComparison.OrdinalIgnoreCase))
                    continue;

                await using var command = new SqliteCommand(@"
                    INSERT OR IGNORE INTO OptionalQuests (
                        Id, QuestId, AlternativeQuestId, IsApproved, UpdatedAt)
                    VALUES (@id, @questId, @alternativeId, 0, @updatedAt)",
                    connection, transaction);
                command.Parameters.AddWithValue("@id", StableId("alt", pair.QuestId, pair.AlternativeId));
                command.Parameters.AddWithValue("@questId", questId);
                command.Parameters.AddWithValue("@alternativeId", alternativeId);
                command.Parameters.AddWithValue("@updatedAt", updatedAt);
                optionalCount += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new RewriteStats(
                questIdByBsg.Count,
                requirementCount,
                objectiveCount,
                requiredItemCount,
                optionalCount,
                refreshedMarkerCount,
                preservedMarkerCount);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<Dictionary<string, string>> LoadExistingQuestIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqliteCommand(
            "SELECT BsgId, Id FROM Quests WHERE BsgId IS NOT NULL AND BsgId != ''",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private static async Task<Dictionary<(string QuestId, string Description), ExistingObjective>> LoadExistingObjectivesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, string), ExistingObjective>();
        const string sql = @"
            SELECT q.BsgId, o.Id, o.Description, o.MapName, o.LocationName,
                   o.LocationPoints, o.OptionalPoints
            FROM QuestObjectives o
            JOIN Quests q ON q.Id = o.QuestId
            WHERE q.BsgId IS NOT NULL";
        await using var command = new SqliteCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var questBsgId = reader.GetString(0);
            var description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(description))
                continue;

            result[(questBsgId, NormalizeText(description))] = new ExistingObjective(
                reader.GetString(1),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6));
        }
        return result;
    }

    private static async Task<Dictionary<string, string>> LoadItemIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqliteCommand(
            "SELECT Id, BsgId, Name FROM Items",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var internalId = reader.GetString(0);
            var bsgId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var name = reader.IsDBNull(2) ? null : reader.GetString(2);
            AddItemLookupKeys(result, internalId, bsgId, null, name);
        }
        return result;
    }

    private static string? ResolveItemId(
        IReadOnlyDictionary<string, string> lookup,
        string? bsgId,
        string? normalizedName,
        string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(bsgId) &&
            lookup.TryGetValue("bsg:" + bsgId, out var byBsg))
            return byBsg;

        foreach (var candidate in new[] { normalizedName, displayName })
        {
            var canonical = CanonicalItemKey(candidate);
            if (!string.IsNullOrEmpty(canonical) &&
                lookup.TryGetValue("name:" + canonical, out var byName))
                return byName;
        }

        return null;
    }

    private static void AddItemLookupKeys(
        IDictionary<string, string> lookup,
        string internalId,
        string? bsgId,
        string? normalizedName,
        string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(bsgId))
            lookup["bsg:" + bsgId] = internalId;

        foreach (var candidate in new[] { normalizedName, displayName })
        {
            var canonical = CanonicalItemKey(candidate);
            if (!string.IsNullOrEmpty(canonical))
                lookup.TryAdd("name:" + canonical, internalId);
        }
    }

    private static async Task EnsureItemMappingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string internalId,
        string? bsgId,
        string itemName,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(@"
            INSERT INTO Items (Id, BsgId, Name, NameEN, UpdatedAt)
            VALUES (@id, @bsgId, @name, @name, @updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                BsgId = CASE
                    WHEN Items.BsgId IS NULL OR Items.BsgId = '' THEN excluded.BsgId
                    ELSE Items.BsgId
                END,
                NameEN = CASE
                    WHEN Items.NameEN IS NULL OR Items.NameEN = '' THEN excluded.NameEN
                    ELSE Items.NameEN
                END,
                UpdatedAt = excluded.UpdatedAt",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", internalId);
        command.Parameters.AddWithValue("@bsgId", ValueOrDbNull(bsgId));
        command.Parameters.AddWithValue("@name", itemName);
        command.Parameters.AddWithValue("@updatedAt", updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Dictionary<string, string> ReadTranslatedNames(JsonElement data, string propertyName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!data.TryGetProperty(propertyName, out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var task in tasks.EnumerateArray())
        {
            var id = GetString(task, "id");
            var name = GetString(task, "name");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                result[id] = name;
        }
        return result;
    }

    private static string BuildInternalQuestId(
        JsonElement task,
        string bsgId,
        IReadOnlyDictionary<string, string> existingQuestIds,
        ISet<string> usedIds)
    {
        string candidate;
        if (existingQuestIds.TryGetValue(bsgId, out var existingId))
        {
            candidate = existingId;
        }
        else
        {
            var wikiLink = GetString(task, "wikiLink");
            candidate = !string.IsNullOrWhiteSpace(wikiLink)
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(wikiLink))
                : $"api:{bsgId}";
        }

        if (usedIds.Add(candidate))
            return candidate;

        var fallback = $"api:{bsgId}";
        usedIds.Add(fallback);
        return fallback;
    }

    private static string CollectQuestMaps(JsonElement task)
    {
        var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (task.TryGetProperty("map", out var taskMap) && taskMap.ValueKind == JsonValueKind.Object)
        {
            var mapped = MapName(taskMap);
            if (!string.IsNullOrWhiteSpace(mapped)) maps.Add(mapped);
        }

        if (task.TryGetProperty("objectives", out var objectives) && objectives.ValueKind == JsonValueKind.Array)
        {
            foreach (var objective in objectives.EnumerateArray())
            {
                if (!objective.TryGetProperty("maps", out var objectiveMaps) ||
                    objectiveMaps.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var map in objectiveMaps.EnumerateArray())
                {
                    var mapped = MapName(map);
                    if (!string.IsNullOrWhiteSpace(mapped)) maps.Add(mapped);
                }
            }
        }

        return maps.Count == 0 ? "Any" : string.Join(", ", maps.OrderBy(x => x));
    }

    private static string? FirstMapName(JsonElement objective)
    {
        if (!objective.TryGetProperty("maps", out var maps) || maps.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var map in maps.EnumerateArray())
        {
            var mapped = MapName(map);
            if (!string.IsNullOrWhiteSpace(mapped)) return mapped;
        }
        return null;
    }

    private static string? MapName(JsonElement map)
    {
        var normalized = GetString(map, "normalizedName")?.ToLowerInvariant();
        var name = GetString(map, "name")?.ToLowerInvariant();
        var source = normalized ?? name;
        if (string.IsNullOrWhiteSpace(source)) return null;

        if (source.Contains("custom")) return "Customs";
        if (source.Contains("factory")) return "Factory";
        if (source.Contains("ground-zero") || source.Contains("ground zero")) return "GroundZero";
        if (source.Contains("interchange")) return "Interchange";
        if (source.Contains("labyrinth")) return "Labyrinth";
        if (source is "the-lab" or "the lab" || source.Contains("laboratory")) return "Labs";
        if (source.Contains("lighthouse")) return "Lighthouse";
        if (source.Contains("reserve")) return "Reserve";
        if (source.Contains("shoreline")) return "Shoreline";
        if (source.Contains("street")) return "StreetsOfTarkov";
        if (source.Contains("woods")) return "Woods";
        if (source.Contains("arena")) return "Arena";
        return null;
    }

    private static string MapRequirementType(IReadOnlyCollection<string> statuses)
    {
        if (statuses.Any(status =>
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("fail", StringComparison.OrdinalIgnoreCase)))
            return "Fail";

        if (statuses.Any(status =>
            status.Equals("active", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("started", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("start", StringComparison.OrdinalIgnoreCase)))
            return "Accept";

        return "Complete";
    }

    private static List<JsonElement> EnumerateObjectiveItems(JsonElement objective)
    {
        foreach (var propertyName in new[] { "items", "useAny" })
        {
            if (objective.TryGetProperty(propertyName, out var array) &&
                array.ValueKind == JsonValueKind.Array)
                return array.EnumerateArray().ToList();
        }

        foreach (var propertyName in new[] { "questItem", "item" })
        {
            if (objective.TryGetProperty(propertyName, out var item) &&
                item.ValueKind == JsonValueKind.Object)
                return new List<JsonElement> { item };
        }

        return new List<JsonElement>();
    }

    private static string MapObjectiveType(string apiType, string description)
    {
        var value = apiType.ToLowerInvariant();
        if (value.Contains("shoot") || value.Contains("kill")) return "Kill";
        if (value.Contains("item")) return description.Contains("hand over", StringComparison.OrdinalIgnoreCase)
            ? "HandOver" : "Collect";
        if (value.Contains("mark")) return "Mark";
        if (value.Contains("visit") || value.Contains("locat")) return "Visit";
        if (value.Contains("extract") || value.Contains("survive")) return "Survive";
        if (value.Contains("build")) return "Build";
        if (value.Contains("taskstatus")) return "Task";
        return "Custom";
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string columnType,
        CancellationToken cancellationToken)
    {
        var exists = false;
        await using (var checkCommand = new SqliteCommand(
            $"PRAGMA table_info([{tableName}])",
            connection,
            transaction))
        await using (var reader = await checkCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"ALTER TABLE [{tableName}] ADD COLUMN [{columnName}] {columnType}",
                cancellationToken);
        }
    }

    private static string CanonicalItemKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
        }
        return builder.ToString();
    }

    private static string BuildNormalizedName(string name)
    {
        return name.Trim().ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("'", "")
            .Replace("’", "")
            .Replace(".", "")
            .Replace(",", "")
            .Replace("?", "")
            .Replace("!", "")
            .Replace(":", "")
            .Replace("\"", "");
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string StableId(string prefix, params string[] values)
    {
        var input = prefix + "|" + string.Join("|", values);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return prefix + ":" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
            else if (char.IsWhiteSpace(character)) builder.Append(' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? GetNestedNullableInt(JsonElement element, string propertyName, string childName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Object)
            return null;
        return GetNullableInt(property, childName);
    }

    private static string? GetNestedString(JsonElement element, string propertyName, string childName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Object
            ? GetString(property, childName)
            : null;
    }

    private static int? GetNullableInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;
        return null;
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.True;
    }

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();
    }

    private static object ValueOrDbNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private sealed record ExistingObjective(
        string Id,
        string? MapName,
        string? LocationName,
        string? LocationPoints,
        string? OptionalPoints);

    private sealed record TrackerGpsPoint(
        int ObjectiveOrder,
        string Type,
        int LocationId,
        double LeftPercent,
        double TopPercent,
        string? Floor);

    private sealed record ApiCoordinateObjective(
        string Id,
        int ObjectiveOrder,
        string Type,
        string Description,
        string MapName);

    private sealed record TrackerCoordinateCatalog(
        IReadOnlyDictionary<string, List<TrackerGpsPoint>> Quests,
        IReadOnlyList<MapConfig> MapConfigs,
        int PointCount)
    {
        public static TrackerCoordinateCatalog Empty { get; } = new(
            new Dictionary<string, List<TrackerGpsPoint>>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<MapConfig>(),
            0);

        public int QuestCount => Quests.Count;
        public int MapConfigCount => MapConfigs.Count;
    }

    private sealed record RewriteStats(
        int QuestCount,
        int RequirementCount,
        int ObjectiveCount,
        int RequiredItemCount,
        int OptionalQuestCount,
        int RefreshedMarkerCount,
        int PreservedMarkerCount);
}

public sealed record QuestApiRefreshResult(
    int QuestCount,
    int RequirementCount,
    int ObjectiveCount,
    int RequiredItemCount,
    int RefreshedMarkerCount,
    int PreservedMarkerCount,
    string BackupPath,
    string GameMode);
