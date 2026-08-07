namespace TarkovDBEditor.Models;

/// <summary>
/// Tarkov Market API에서 가져온 참조용 마커
/// DB Quests/QuestObjectives 테이블과 런타임 매칭에 사용
/// </summary>
public class ApiMarker
{
    /// <summary>
    /// 고유 ID (GUID)
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Tarkov Market API marker uid (중복 import 방지용)
    /// </summary>
    public string TarkovMarketUid { get; set; } = "";

    // ─────────────────────────────────────────────
    // 마커 기본 정보
    // ─────────────────────────────────────────────

    /// <summary>
    /// 마커명 (EN)
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 마커명 (KO)
    /// </summary>
    public string? NameKo { get; set; }

    /// <summary>
    /// 카테고리 (Extractions, Spawns, Quests, Keys, Loot, Miscellaneous)
    /// </summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// 서브 카테고리 (PMC Extraction, Quest Objective 등)
    /// </summary>
    public string? SubCategory { get; set; }

    // ─────────────────────────────────────────────
    // 위치 정보
    // ─────────────────────────────────────────────

    /// <summary>
    /// 맵 키 (Customs, Woods 등)
    /// </summary>
    public string MapKey { get; set; } = "";

    /// <summary>
    /// 게임 X 좌표 (SVG→Game 변환 후)
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// 게임 Y 좌표 (높이, 보통 0)
    /// </summary>
    public double? Y { get; set; }

    /// <summary>
    /// 게임 Z 좌표 (SVG→Game 변환 후)
    /// </summary>
    public double Z { get; set; }

    /// <summary>
    /// 층 ID (multi-floor 맵용)
    /// </summary>
    public string? FloorId { get; set; }

    // ─────────────────────────────────────────────
    // 퀘스트 연관 정보 (DB Quests 테이블과 매칭용)
    // ─────────────────────────────────────────────

    /// <summary>
    /// BSG ID (DB Quests.BsgId와 매칭)
    /// </summary>
    public string? QuestBsgId { get; set; }

    /// <summary>
    /// 퀘스트명 EN (DB Quests.NameEN과 fallback 매칭)
    /// </summary>
    public string? QuestNameEn { get; set; }

    /// <summary>
    /// Objective 설명 (DB QuestObjectives.Description과 매칭)
    /// </summary>
    public string? ObjectiveDescription { get; set; }

    // ─────────────────────────────────────────────
    // 메타 정보
    // ─────────────────────────────────────────────

    /// <summary>
    /// Import 시점
    /// </summary>
    public DateTime ImportedAt { get; set; }

    // ─────────────────────────────────────────────
    // 승인 상태
    // ─────────────────────────────────────────────

    /// <summary>
    /// 승인 여부 (사용자가 검증 완료했는지)
    /// </summary>
    public bool IsApproved { get; set; }

    /// <summary>
    /// 승인 시점
    /// </summary>
    public DateTime? ApprovedAt { get; set; }
}
