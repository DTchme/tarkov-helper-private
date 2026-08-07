using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TarkovDBEditor.Models;

/// <summary>
/// 맵 마커 타입
/// </summary>
public enum MapMarkerType
{
    /// <summary>
    /// PMC 스폰 지점
    /// </summary>
    PmcSpawn,

    /// <summary>
    /// Scav 스폰 지점
    /// </summary>
    ScavSpawn,

    /// <summary>
    /// PMC 탈출구
    /// </summary>
    PmcExtraction,

    /// <summary>
    /// Scav 탈출구
    /// </summary>
    ScavExtraction,

    /// <summary>
    /// 공용 탈출구 (PMC + Scav)
    /// </summary>
    SharedExtraction,

    /// <summary>
    /// Transit (맵 이동)
    /// </summary>
    Transit,

    /// <summary>
    /// 보스 스폰 지점
    /// </summary>
    BossSpawn,

    /// <summary>
    /// 레이더 스폰 지점
    /// </summary>
    RaiderSpawn,

    /// <summary>
    /// 레버/스위치
    /// </summary>
    Lever,

    /// <summary>
    /// 키 필요 장소
    /// </summary>
    Keys,

    /// <summary>
    /// 로그 스폰 지점
    /// </summary>
    RogueSpawn,

    /// <summary>
    /// 컬티스트 스폰 지점
    /// </summary>
    CultistSpawn,

    /// <summary>
    /// 스나이퍼 스캐브 스폰 지점
    /// </summary>
    SniperScavSpawn
}

/// <summary>
/// 맵 마커 정보
/// </summary>
public class MapMarker : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private string _id = Guid.NewGuid().ToString();
    /// <summary>
    /// 마커 고유 ID
    /// </summary>
    public string Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    private string _name = string.Empty;
    /// <summary>
    /// 마커 이름 (영어)
    /// </summary>
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
    }

    private string? _nameKo;
    /// <summary>
    /// 마커 이름 (한국어)
    /// </summary>
    public string? NameKo
    {
        get => _nameKo;
        set { _nameKo = value; OnPropertyChanged(); }
    }

    private MapMarkerType _markerType = MapMarkerType.PmcExtraction;
    /// <summary>
    /// 마커 타입
    /// </summary>
    public MapMarkerType MarkerType
    {
        get => _markerType;
        set { _markerType = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
    }

    private string _mapKey = string.Empty;
    /// <summary>
    /// 맵 키 (예: "Woods", "Customs")
    /// </summary>
    public string MapKey
    {
        get => _mapKey;
        set { _mapKey = value; OnPropertyChanged(); }
    }

    private double _x;
    /// <summary>
    /// X 좌표 (게임 좌표)
    /// </summary>
    public double X
    {
        get => _x;
        set { _x = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
    }

    private double _y;
    /// <summary>
    /// Y 좌표 (높이)
    /// </summary>
    public double Y
    {
        get => _y;
        set { _y = value; OnPropertyChanged(); }
    }

    private double _z;
    /// <summary>
    /// Z 좌표 (게임 좌표)
    /// </summary>
    public double Z
    {
        get => _z;
        set { _z = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
    }

    private string? _floorId;
    /// <summary>
    /// 층 ID (다층 맵용)
    /// </summary>
    public string? FloorId
    {
        get => _floorId;
        set { _floorId = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string Display => $"[{MarkerType}] {Name} ({X:F1}, {Z:F1})";

    /// <summary>
    /// 마커 타입에 따른 색상 반환
    /// </summary>
    public static (byte R, byte G, byte B) GetMarkerColor(MapMarkerType type)
    {
        return type switch
        {
            MapMarkerType.PmcSpawn => (76, 175, 80),         // Green
            MapMarkerType.PmcExtraction => (76, 175, 80),    // Green
            MapMarkerType.SharedExtraction => (76, 175, 80), // Green
            MapMarkerType.ScavSpawn => (180, 180, 180),      // Light Gray
            MapMarkerType.ScavExtraction => (180, 180, 180), // Light Gray
            MapMarkerType.Transit => (255, 152, 0),          // Orange
            MapMarkerType.BossSpawn => (244, 67, 54),        // Red
            MapMarkerType.RaiderSpawn => (156, 39, 176),     // Purple
            MapMarkerType.Lever => (255, 235, 59),           // Yellow
            MapMarkerType.Keys => (33, 150, 243),            // Blue
            MapMarkerType.RogueSpawn => (0, 150, 136),       // Teal
            MapMarkerType.CultistSpawn => (63, 81, 181),     // Indigo
            MapMarkerType.SniperScavSpawn => (121, 85, 72),  // Brown
            _ => (158, 158, 158)                              // Gray
        };
    }

    /// <summary>
    /// 마커 타입 표시명
    /// </summary>
    public static string GetMarkerTypeName(MapMarkerType type)
    {
        return type switch
        {
            MapMarkerType.PmcSpawn => "PMC Spawn",
            MapMarkerType.ScavSpawn => "Scav Spawn",
            MapMarkerType.PmcExtraction => "PMC Extraction",
            MapMarkerType.ScavExtraction => "Scav Extraction",
            MapMarkerType.SharedExtraction => "Shared Extraction",
            MapMarkerType.Transit => "Transit",
            MapMarkerType.BossSpawn => "Boss Spawn",
            MapMarkerType.RaiderSpawn => "Raider Spawn",
            MapMarkerType.Lever => "Lever",
            MapMarkerType.Keys => "Keys",
            MapMarkerType.RogueSpawn => "Rogue Spawn",
            MapMarkerType.CultistSpawn => "Cultist Spawn",
            MapMarkerType.SniperScavSpawn => "Sniper Scav Spawn",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// 마커 타입에 해당하는 아이콘 파일명 반환
    /// </summary>
    public static string GetIconFileName(MapMarkerType type)
    {
        return type switch
        {
            MapMarkerType.PmcSpawn => "PMC Spawn.webp",
            MapMarkerType.ScavSpawn => "SCAV Spawn.webp",
            MapMarkerType.PmcExtraction => "PMC Extraction.webp",
            MapMarkerType.ScavExtraction => "SCAV Extraction.webp",
            MapMarkerType.SharedExtraction => "PMC Extraction.webp", // Use PMC icon for shared
            MapMarkerType.Transit => "Transit.webp",
            MapMarkerType.BossSpawn => "BOSS Spawn.webp",
            MapMarkerType.RaiderSpawn => "Raider Spawn.webp",
            MapMarkerType.Lever => "Lever.webp",
            MapMarkerType.Keys => "Keys.webp",
            MapMarkerType.RogueSpawn => "Raider Spawn.webp",      // Use Raider icon for now
            MapMarkerType.CultistSpawn => "BOSS Spawn.webp",      // Use Boss icon for now
            MapMarkerType.SniperScavSpawn => "SCAV Spawn.webp",   // Use Scav icon for now
            _ => "PMC Spawn.webp"
        };
    }
}
