using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovHelper.Services;

/// <summary>
/// Converts the supported json.tarkov.dev static task feed into the compact shape used by
/// QuestApiRefreshService. The former api.tarkov.dev GraphQL endpoint is deprecated.
/// </summary>
public static class TarkovDevStaticTaskAdapter
{
    public static string Adapt(
        string tasksJson,
        string tasksEnglishJson,
        string tasksKoreanJson,
        string tasksJapaneseJson,
        string tradersJson,
        string tradersEnglishJson,
        string mapsJson,
        string mapsEnglishJson,
        string itemsEnglishJson)
    {
        var tasks = RequiredObject(Parse(tasksJson), "data", "tasks");
        var english = RequiredObject(Parse(tasksEnglishJson), "data");
        var korean = RequiredObject(Parse(tasksKoreanJson), "data");
        var japanese = RequiredObject(Parse(tasksJapaneseJson), "data");
        var traders = RequiredObject(Parse(tradersJson), "data");
        var traderEnglish = RequiredObject(Parse(tradersEnglishJson), "data");
        var maps = RequiredObject(Parse(mapsJson), "data", "maps");
        var mapEnglish = RequiredObject(Parse(mapsEnglishJson), "data");
        var itemEnglish = RequiredObject(Parse(itemsEnglishJson), "data");

        var traderCatalog = BuildReferenceCatalog(traders, traderEnglish);
        var mapCatalog = BuildReferenceCatalog(maps, mapEnglish);
        var englishTasks = new JsonArray();
        var koreanTasks = new JsonArray();
        var japaneseTasks = new JsonArray();

        foreach (var pair in tasks)
        {
            if (pair.Value is not JsonObject source)
                continue;

            var task = (JsonObject)source.DeepClone();
            var id = StringValue(task["id"]) ?? pair.Key;
            task["id"] = id;
            task["name"] = Translate(english, StringValue(task["name"]), id);

            ReplaceReference(task, "trader", traderCatalog);
            ReplaceReference(task, "map", mapCatalog);
            ReplacePrestige(task);
            ReplaceTaskReferences(task, "taskRequirements");
            ReplaceTaskReferences(task, "failConditions");
            ReplaceObjectives(task, english, mapCatalog, itemEnglish);

            englishTasks.Add(task);
            koreanTasks.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = Translate(korean, StringValue(source["name"]), task["name"]?.GetValue<string>() ?? id)
            });
            japaneseTasks.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = Translate(japanese, StringValue(source["name"]), task["name"]?.GetValue<string>() ?? id)
            });
        }

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["en"] = englishTasks,
                ["ko"] = koreanTasks,
                ["ja"] = japaneseTasks
            }
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonObject Parse(string json) =>
        JsonNode.Parse(json) as JsonObject
        ?? throw new InvalidOperationException("json.tarkov.dev 응답 형식이 올바르지 않습니다.");

    private static JsonObject RequiredObject(JsonObject root, params string[] path)
    {
        JsonNode? current = root;
        foreach (var segment in path)
            current = current?[segment];
        return current as JsonObject
            ?? throw new InvalidOperationException($"json.tarkov.dev 응답에 {string.Join('.', path)} 데이터가 없습니다.");
    }

    private static Dictionary<string, JsonObject> BuildReferenceCatalog(
        JsonObject source,
        JsonObject translations)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            if (pair.Value is not JsonObject value)
                continue;

            var id = StringValue(value["id"]) ?? pair.Key;
            var rawName = StringValue(value["name"]);
            var name = Translate(translations, rawName, id);
            var normalizedName = StringValue(value["normalizedName"]) ?? NormalizeName(name);
            result[id] = new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["normalizedName"] = normalizedName
            };
        }
        return result;
    }

    private static void ReplaceReference(
        JsonObject owner,
        string propertyName,
        IReadOnlyDictionary<string, JsonObject> catalog)
    {
        var id = StringValue(owner[propertyName]);
        if (string.IsNullOrWhiteSpace(id))
        {
            owner[propertyName] = null;
            return;
        }

        owner[propertyName] = catalog.TryGetValue(id, out var reference)
            ? reference.DeepClone()
            : new JsonObject { ["id"] = id, ["name"] = id, ["normalizedName"] = NormalizeName(id) };
    }

    private static void ReplacePrestige(JsonObject task)
    {
        if (task["requiredPrestige"] is JsonValue value && value.TryGetValue<int>(out var level))
            task["requiredPrestige"] = new JsonObject { ["prestigeLevel"] = level };
    }

    private static void ReplaceTaskReferences(JsonObject task, string propertyName)
    {
        if (task[propertyName] is not JsonArray requirements)
            return;

        foreach (var node in requirements)
        {
            if (node is not JsonObject requirement)
                continue;
            var taskId = StringValue(requirement["task"]);
            if (!string.IsNullOrWhiteSpace(taskId))
                requirement["task"] = new JsonObject { ["id"] = taskId };
        }
    }

    private static void ReplaceObjectives(
        JsonObject task,
        JsonObject translations,
        IReadOnlyDictionary<string, JsonObject> mapCatalog,
        JsonObject itemTranslations)
    {
        if (task["objectives"] is not JsonArray objectives)
            return;

        foreach (var node in objectives)
        {
            if (node is not JsonObject objective)
                continue;

            var objectiveId = StringValue(objective["id"]);
            objective["description"] = Translate(
                translations,
                StringValue(objective["description"]),
                objectiveId ?? "Objective");

            if (objective["maps"] is JsonArray mapIds)
            {
                var mapped = new JsonArray();
                foreach (var mapNode in mapIds)
                {
                    var mapId = StringValue(mapNode);
                    if (string.IsNullOrWhiteSpace(mapId))
                        continue;
                    mapped.Add(mapCatalog.TryGetValue(mapId, out var map)
                        ? map.DeepClone()
                        : new JsonObject { ["id"] = mapId, ["name"] = mapId, ["normalizedName"] = NormalizeName(mapId) });
                }
                objective["maps"] = mapped;
            }

            ReplaceItemArray(objective, "items", itemTranslations);
            ReplaceItemArray(objective, "useAny", itemTranslations);
            ReplaceItem(objective, "questItem", itemTranslations);
            ReplaceItem(objective, "item", itemTranslations);
        }
    }

    private static void ReplaceItemArray(JsonObject objective, string propertyName, JsonObject translations)
    {
        if (objective[propertyName] is not JsonArray itemIds)
            return;

        var items = new JsonArray();
        foreach (var node in itemIds)
        {
            var id = StringValue(node);
            if (!string.IsNullOrWhiteSpace(id))
                items.Add(BuildItem(id, translations));
        }
        objective[propertyName] = items;
    }

    private static void ReplaceItem(JsonObject objective, string propertyName, JsonObject translations)
    {
        var id = StringValue(objective[propertyName]);
        if (!string.IsNullOrWhiteSpace(id))
            objective[propertyName] = BuildItem(id, translations);
    }

    private static JsonObject BuildItem(string id, JsonObject translations)
    {
        var name = Translate(translations, id + " Name", id);
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["normalizedName"] = NormalizeName(name)
        };
    }

    private static string Translate(JsonObject translations, string? key, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(key) &&
            translations[key] is JsonValue translated &&
            translated.TryGetValue<string>(out var value) &&
            !string.IsNullOrWhiteSpace(value))
            return value;
        return fallback;
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string NormalizeName(string value)
    {
        var result = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (result.Contains("--", StringComparison.Ordinal))
            result = result.Replace("--", "-", StringComparison.Ordinal);
        return result.Trim('-');
    }
}
