using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using TarkovDBEditor.Services;

namespace TarkovDBEditor.Models
{
    /// <summary>
    /// 위치 좌표 포인트 (다각형 꼭짓점)
    /// </summary>
    public class LocationPoint : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private double _x;
        public double X
        {
            get => _x;
            set { _x = value; OnPropertyChanged(); }
        }

        private double _y;
        public double Y
        {
            get => _y;
            set { _y = value; OnPropertyChanged(); }
        }

        private double _z;
        public double Z
        {
            get => _z;
            set { _z = value; OnPropertyChanged(); }
        }

        private string? _floorId;
        /// <summary>
        /// 이 포인트가 속한 층의 ID (예: "main", "basement", "level2")
        /// null이면 단일 층 맵이거나 층 정보가 없는 레거시 데이터
        /// </summary>
        public string? FloorId
        {
            get => _floorId;
            set { _floorId = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
        }

        [JsonIgnore]
        public string Display => FloorId != null
            ? $"({X:F1}, {Y:F1}, {Z:F1}) [{FloorId}]"
            : $"({X:F1}, {Y:F1}, {Z:F1})";

        public LocationPoint() { }

        public LocationPoint(double x, double y, double z = 0, string? floorId = null)
        {
            X = x;
            Y = y;
            Z = z;
            FloorId = floorId;
        }
    }


    /// <summary>
    /// Quest Objective UI 바인딩용 ViewModel 클래스
    /// </summary>
    public class QuestObjectiveItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public QuestObjectiveItem()
        {
            // 컬렉션 변경 시 관련 프로퍼티 알림
            _locationPoints.CollectionChanged += LocationPoints_CollectionChanged;
            _optionalPoints.CollectionChanged += OptionalPoints_CollectionChanged;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public string Id { get; set; } = "";
        public string QuestId { get; set; } = "";

        private int _sortOrder;
        public int SortOrder
        {
            get => _sortOrder;
            set { _sortOrder = value; OnPropertyChanged(); }
        }

        private string _objectiveType = "Custom";
        public string ObjectiveType
        {
            get => _objectiveType;
            set { _objectiveType = value; OnPropertyChanged(); OnPropertyChanged(nameof(TypeDisplay)); }
        }

        private string _description = "";
        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        // 타겟 정보
        private string? _targetType;
        public string? TargetType
        {
            get => _targetType;
            set { _targetType = value; OnPropertyChanged(); }
        }

        private int? _targetCount;
        public int? TargetCount
        {
            get => _targetCount;
            set { _targetCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CountDisplay)); }
        }

        // 아이템 정보
        private string? _itemId;
        public string? ItemId
        {
            get => _itemId;
            set { _itemId = value; OnPropertyChanged(); OnPropertyChanged(nameof(ItemIconPath)); OnPropertyChanged(nameof(HasItem)); }
        }

        private string? _itemName;
        public string? ItemName
        {
            get => _itemName;
            set { _itemName = value; OnPropertyChanged(); }
        }

        private bool _requiresFIR;
        public bool RequiresFIR
        {
            get => _requiresFIR;
            set { _requiresFIR = value; OnPropertyChanged(); OnPropertyChanged(nameof(FIRDisplay)); }
        }

        // 맵/위치 정보
        private string? _mapName;
        public string? MapName
        {
            get => _mapName;
            set { _mapName = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLocation)); OnPropertyChanged(nameof(EffectiveMapName)); }
        }

        private string? _locationName;
        public string? LocationName
        {
            get => _locationName;
            set { _locationName = value; OnPropertyChanged(); }
        }

        // 퀘스트의 Location (Fallback용 - Quest Item의 경우 Quest Location을 맵으로 사용)
        private string? _questLocation;
        /// <summary>
        /// 퀘스트 자체의 Location. Quest Item(예: Lab Journal)의 경우
        /// MapName이 없어도 QuestLocation을 맵으로 사용하여 위치 포인트를 찍을 수 있습니다.
        /// </summary>
        public string? QuestLocation
        {
            get => _questLocation;
            set { _questLocation = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLocation)); OnPropertyChanged(nameof(EffectiveMapName)); }
        }

        // 퀘스트 이름 정보 (JOIN으로 로드)
        private string? _questName;
        /// <summary>
        /// 퀘스트 이름 (DB JOIN으로 로드됨)
        /// </summary>
        public string? QuestName
        {
            get => _questName;
            set { _questName = value; OnPropertyChanged(); }
        }

        private string? _questNameEN;
        /// <summary>
        /// 퀘스트 영문 이름 (DB JOIN으로 로드됨)
        /// </summary>
        public string? QuestNameEN
        {
            get => _questNameEN;
            set { _questNameEN = value; OnPropertyChanged(); }
        }

        private string? _wikiPageLink;
        /// <summary>
        /// 퀘스트 위키 페이지 링크 (DB JOIN으로 로드됨)
        /// </summary>
        public string? WikiPageLink
        {
            get => _wikiPageLink;
            set { _wikiPageLink = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 실제 사용할 맵 이름 (MapName이 있으면 MapName, 없으면 QuestLocation)
        /// </summary>
        public string? EffectiveMapName => !string.IsNullOrEmpty(MapName) ? MapName : QuestLocation;

        // 좌표 집합 (다각형 영역 정의)
        private ObservableCollection<LocationPoint> _locationPoints = new();
        public ObservableCollection<LocationPoint> LocationPoints
        {
            get => _locationPoints;
            set
            {
                if (_locationPoints != null)
                    _locationPoints.CollectionChanged -= LocationPoints_CollectionChanged;

                _locationPoints = value;

                if (_locationPoints != null)
                    _locationPoints.CollectionChanged += LocationPoints_CollectionChanged;

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCoordinates));
                OnPropertyChanged(nameof(CoordinatesDisplay));
                OnPropertyChanged(nameof(PolygonType));
            }
        }

        private void LocationPoints_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(LocationPoints));
            OnPropertyChanged(nameof(HasCoordinates));
            OnPropertyChanged(nameof(CoordinatesDisplay));
            OnPropertyChanged(nameof(PolygonType));
        }

        // JSON 문자열로 직렬화/역직렬화
        public string? LocationPointsJson
        {
            get => LocationPoints.Count > 0 ? JsonSerializer.Serialize(LocationPoints) : null;
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    try
                    {
                        var points = JsonSerializer.Deserialize<List<LocationPoint>>(value);
                        LocationPoints = new ObservableCollection<LocationPoint>(points ?? new List<LocationPoint>());
                    }
                    catch
                    {
                        LocationPoints = new ObservableCollection<LocationPoint>();
                    }
                }
                else
                {
                    LocationPoints = new ObservableCollection<LocationPoint>();
                }
            }
        }

        // Optional 좌표 집합 (여러 위치 중 한 곳 - OR 관계)
        private ObservableCollection<LocationPoint> _optionalPoints = new();
        /// <summary>
        /// 여러 위치 중 한 곳을 나타내는 포인트 목록 (OR 관계)
        /// LocationPoints가 범위(영역)를 정의하는 반면, OptionalPoints는 개별 선택지 위치들을 나타냅니다.
        /// </summary>
        public ObservableCollection<LocationPoint> OptionalPoints
        {
            get => _optionalPoints;
            set
            {
                if (_optionalPoints != null)
                    _optionalPoints.CollectionChanged -= OptionalPoints_CollectionChanged;

                _optionalPoints = value;

                if (_optionalPoints != null)
                    _optionalPoints.CollectionChanged += OptionalPoints_CollectionChanged;

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasOptionalPoints));
                OnPropertyChanged(nameof(OptionalPointsDisplay));
            }
        }

        private void OptionalPoints_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(OptionalPoints));
            OnPropertyChanged(nameof(HasOptionalPoints));
            OnPropertyChanged(nameof(OptionalPointsDisplay));
        }

        // Optional Points JSON 직렬화/역직렬화
        public string? OptionalPointsJson
        {
            get => OptionalPoints.Count > 0 ? JsonSerializer.Serialize(OptionalPoints) : null;
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    try
                    {
                        var points = JsonSerializer.Deserialize<List<LocationPoint>>(value);
                        OptionalPoints = new ObservableCollection<LocationPoint>(points ?? new List<LocationPoint>());
                    }
                    catch
                    {
                        OptionalPoints = new ObservableCollection<LocationPoint>();
                    }
                }
                else
                {
                    OptionalPoints = new ObservableCollection<LocationPoint>();
                }
            }
        }

        // 조건
        private string? _conditions;
        public string? Conditions
        {
            get => _conditions;
            set { _conditions = value; OnPropertyChanged(); }
        }

        // 승인 상태
        private bool _isApproved;
        public bool IsApproved
        {
            get => _isApproved;
            set
            {
                if (_isApproved != value)
                {
                    _isApproved = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ApprovalStatus));
                }
            }
        }

        private DateTime? _approvedAt;
        public DateTime? ApprovedAt
        {
            get => _approvedAt;
            set { _approvedAt = value; OnPropertyChanged(); }
        }

        // 계산 속성
        public string TypeDisplay => ObjectiveType switch
        {
            "Kill" => "Kill",
            "Collect" => "Collect",
            "HandOver" => "Hand Over",
            "Visit" => "Visit",
            "Mark" => "Mark",
            "Stash" => "Stash",
            "Survive" => "Survive",
            "Build" => "Build",
            "Task" => "Task",
            _ => "Custom"
        };

        public string CountDisplay => TargetCount.HasValue ? $"x{TargetCount}" : "";

        public string FIRDisplay => RequiresFIR ? "FIR" : "";

        public bool HasItem => !string.IsNullOrEmpty(ItemId) || !string.IsNullOrEmpty(ItemName);

        /// <summary>
        /// 맵 위치가 있는지 여부. MapName이 있거나 QuestLocation이 있으면 true
        /// </summary>
        public bool HasLocation => !string.IsNullOrEmpty(EffectiveMapName);

        public bool HasCoordinates => LocationPoints.Count > 0;

        public string CoordinatesDisplay => HasCoordinates
            ? $"{PolygonType} ({LocationPoints.Count} points)"
            : "";

        public string PolygonType => LocationPoints.Count switch
        {
            0 => "",
            1 => "Point",
            2 => "Line",
            3 => "Triangle",
            4 => "Quad",
            _ => $"Polygon"
        };

        public bool HasOptionalPoints => OptionalPoints.Count > 0;

        public string OptionalPointsDisplay => HasOptionalPoints
            ? $"{OptionalPoints.Count} locations"
            : "";

        public string ApprovalStatus => IsApproved ? "Approved" : "Pending";

        // 아이템 아이콘 경로 (wiki_data/icons/{ItemId}.png)
        public string? ItemIconPath
        {
            get
            {
                if (string.IsNullOrEmpty(ItemId))
                    return null;

                var basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wiki_data", "icons");
                var extensions = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

                foreach (var ext in extensions)
                {
                    var path = Path.Combine(basePath, $"{ItemId}{ext}");
                    if (File.Exists(path))
                        return path;
                }

                return null;
            }
        }

        // 아이템 아이콘 이미지 (바인딩용)
        private BitmapImage? _itemIcon;
        public BitmapImage? ItemIcon
        {
            get
            {
                if (_itemIcon == null && !string.IsNullOrEmpty(ItemIconPath))
                {
                    try
                    {
                        _itemIcon = new BitmapImage();
                        _itemIcon.BeginInit();
                        _itemIcon.CacheOption = BitmapCacheOption.OnLoad;
                        _itemIcon.UriSource = new Uri(ItemIconPath);
                        _itemIcon.DecodePixelWidth = 64; // 성능을 위한 크기 제한
                        _itemIcon.EndInit();
                        _itemIcon.Freeze();
                    }
                    catch
                    {
                        _itemIcon = null;
                    }
                }
                return _itemIcon;
            }
        }

        /// <summary>
        /// ParsedObjective로부터 QuestObjectiveItem 생성
        /// </summary>
        public static QuestObjectiveItem FromParsed(ParsedObjective parsed, string questId)
        {
            return new QuestObjectiveItem
            {
                QuestId = questId,
                SortOrder = parsed.SortOrder,
                ObjectiveType = parsed.Type.ToString(),
                Description = parsed.Description,
                TargetType = parsed.TargetType,
                TargetCount = parsed.TargetCount,
                ItemName = parsed.ItemName,
                RequiresFIR = parsed.RequiresFIR,
                MapName = parsed.MapName,
                LocationName = parsed.LocationName,
                Conditions = parsed.Conditions
            };
        }
    }
}
