using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovDBEditor.Models;

/// <summary>
/// Tarkov Market API 마커 응답
/// </summary>
public class TarkovMarketMarkersResponse
{
    [JsonPropertyName("markers")]
    public string Markers { get; set; } = "";
}

/// <summary>
/// Nullable int를 문자열, 숫자, null 모두 처리하는 컨버터
/// </summary>
public class FlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.GetInt32();
            case JsonTokenType.String:
                var str = reader.GetString();
                if (string.IsNullOrWhiteSpace(str))
                    return null;
                if (int.TryParse(str, out var result))
                    return result;
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}

/// <summary>
/// Nullable double를 문자열, 숫자, null 모두 처리하는 컨버터
/// </summary>
public class FlexibleDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return 0;
            case JsonTokenType.Number:
                return reader.GetDouble();
            case JsonTokenType.String:
                var str = reader.GetString();
                if (string.IsNullOrWhiteSpace(str))
                    return 0;
                if (double.TryParse(str, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var result))
                    return result;
                return 0;
            default:
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// Tarkov Market API 마커 데이터
/// </summary>
public class TarkovMarketMarker
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("subCategory")]
    public string SubCategory { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    [JsonPropertyName("map")]
    public string Map { get; set; } = "";

    [JsonPropertyName("level")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Level { get; set; }

    [JsonPropertyName("geometry")]
    public TarkovMarketGeometry? Geometry { get; set; }

    [JsonPropertyName("questUid")]
    public string? QuestUid { get; set; }

    [JsonPropertyName("itemsUid")]
    public List<string>? ItemsUid { get; set; }

    [JsonPropertyName("imgs")]
    public List<TarkovMarketImage>? Imgs { get; set; }

    [JsonPropertyName("updated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedStr { get; set; }

    [JsonIgnore]
    public DateTime? Updated => string.IsNullOrEmpty(UpdatedStr) ? null :
        DateTime.TryParse(UpdatedStr, out var dt) ? dt : null;

    [JsonPropertyName("name_l10n")]
    public Dictionary<string, string>? NameL10n { get; set; }

    [JsonPropertyName("desc_l10n")]
    public Dictionary<string, string>? DescL10n { get; set; }

    /// <summary>
    /// 변환된 게임 좌표 (계산 후 설정)
    /// </summary>
    [JsonIgnore]
    public double? GameX { get; set; }

    [JsonIgnore]
    public double? GameZ { get; set; }

    [JsonIgnore]
    public string? FloorId { get; set; }

    /// <summary>
    /// 우리 DB의 MarkerType으로 변환
    /// </summary>
    [JsonIgnore]
    public MapMarkerType? MappedMarkerType
    {
        get
        {
            return (Category, SubCategory) switch
            {
                ("Extractions", "PMC Extraction") => MapMarkerType.PmcExtraction,
                ("Extractions", "Scav Extraction") => MapMarkerType.ScavExtraction,
                ("Extractions", "Co-op Extraction") => MapMarkerType.SharedExtraction,
                ("Spawns", "PMC Spawn") => MapMarkerType.PmcSpawn,
                ("Spawns", "Scav Spawn") => MapMarkerType.ScavSpawn,
                ("Spawns", "Boss Spawn") => MapMarkerType.BossSpawn,
                ("Spawns", "Raider Spawn") => MapMarkerType.RaiderSpawn,
                ("Keys", _) => MapMarkerType.Keys,
                ("Miscellaneous", "Lever") => MapMarkerType.Lever,
                ("Miscellaneous", "Switch") => MapMarkerType.Lever,
                _ => null
            };
        }
    }
}

/// <summary>
/// Tarkov Market 좌표 (SVG 좌표계)
/// </summary>
public class TarkovMarketGeometry
{
    [JsonPropertyName("x")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double X { get; set; }

    [JsonPropertyName("y")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double Y { get; set; }
}

/// <summary>
/// Tarkov Market 마커 이미지
/// </summary>
public class TarkovMarketImage
{
    [JsonPropertyName("img")]
    public string Img { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("desc")]
    public string? Desc { get; set; }
}

/// <summary>
/// 마커 매칭 결과
/// </summary>
public class MarkerMatchResult
{
    /// <summary>
    /// 우리 DB 마커
    /// </summary>
    public MapMarker DbMarker { get; set; } = null!;

    /// <summary>
    /// Tarkov Market API 마커
    /// </summary>
    public TarkovMarketMarker ApiMarker { get; set; } = null!;

    /// <summary>
    /// 이름 유사도 점수 (0~1)
    /// </summary>
    public double NameSimilarity { get; set; }

    /// <summary>
    /// 변환 후 거리 오차 (게임 좌표 단위)
    /// </summary>
    public double? DistanceError { get; set; }

    /// <summary>
    /// 참조점으로 사용 여부
    /// </summary>
    public bool IsReferencePoint { get; set; }

    /// <summary>
    /// 사용자가 수동으로 매칭했는지 여부
    /// </summary>
    public bool IsManualMatch { get; set; }
}

/// <summary>
/// 맵별 SVG→Game 좌표 변환 설정
/// </summary>
public class MapSvgTransform
{
    /// <summary>
    /// 맵 키 (Customs, Woods 등)
    /// </summary>
    [JsonPropertyName("mapKey")]
    public string MapKey { get; set; } = "";

    /// <summary>
    /// SVG 좌표 → 게임 좌표 변환 행렬 [a, b, c, d, tx, ty]
    /// gameX = a * svgX + b * svgY + tx
    /// gameZ = c * svgX + d * svgY + ty
    /// </summary>
    [JsonPropertyName("svgToGameTransform")]
    public double[]? SvgToGameTransform { get; set; }

    /// <summary>
    /// 계산 시점
    /// </summary>
    [JsonPropertyName("calculatedAt")]
    public DateTime? CalculatedAt { get; set; }

    /// <summary>
    /// 사용된 참조점 수
    /// </summary>
    [JsonPropertyName("referencePointCount")]
    public int ReferencePointCount { get; set; }

    /// <summary>
    /// 평균 오차 (게임 좌표 단위)
    /// </summary>
    [JsonPropertyName("averageError")]
    public double AverageError { get; set; }

    /// <summary>
    /// SVG 좌표를 게임 좌표로 변환
    /// </summary>
    public (double gameX, double gameZ) SvgToGame(double svgX, double svgY)
    {
        if (SvgToGameTransform == null || SvgToGameTransform.Length < 6)
        {
            // Fallback: 변환 없이 그대로 반환
            return (svgX, svgY);
        }

        var a = SvgToGameTransform[0];
        var b = SvgToGameTransform[1];
        var c = SvgToGameTransform[2];
        var d = SvgToGameTransform[3];
        var tx = SvgToGameTransform[4];
        var ty = SvgToGameTransform[5];

        var gameX = a * svgX + b * svgY + tx;
        var gameZ = c * svgX + d * svgY + ty;

        return (gameX, gameZ);
    }
}

/// <summary>
/// 맵별 SVG 변환 설정 목록
/// </summary>
public class MapSvgTransformList
{
    [JsonPropertyName("transforms")]
    public List<MapSvgTransform> Transforms { get; set; } = new();

    /// <summary>
    /// 맵 키로 변환 설정 찾기
    /// </summary>
    public MapSvgTransform? FindByMapKey(string mapKey)
    {
        return Transforms.FirstOrDefault(t =>
            string.Equals(t.MapKey, mapKey, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Tarkov Market API 퀘스트 응답
/// </summary>
public class TarkovMarketQuestsResponse
{
    [JsonPropertyName("quests")]
    public string Quests { get; set; } = "";
}

/// <summary>
/// Tarkov Market API 퀘스트 데이터
/// </summary>
public class TarkovMarketQuest
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = "";

    [JsonPropertyName("bsgId")]
    public string? BsgId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    [JsonPropertyName("trader")]
    public string? Trader { get; set; }

    [JsonPropertyName("map")]
    public string? Map { get; set; }

    [JsonPropertyName("enObjectives")]
    public List<string>? EnObjectives { get; set; }

    [JsonPropertyName("name_l10n")]
    public Dictionary<string, string>? NameL10n { get; set; }

    [JsonPropertyName("desc_l10n")]
    public Dictionary<string, string>? DescL10n { get; set; }

    /// <summary>
    /// EN 퀘스트명 (name_l10n.en 또는 name)
    /// </summary>
    [JsonIgnore]
    public string NameEn => NameL10n?.GetValueOrDefault("en") ?? Name;

    /// <summary>
    /// KO 퀘스트명
    /// </summary>
    [JsonIgnore]
    public string? NameKo => NameL10n?.GetValueOrDefault("ko");
}
