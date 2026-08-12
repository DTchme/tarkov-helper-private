namespace TarkovHelper.Services;

/// <summary>
/// Quest-related localization strings for LocalizationService.
/// Includes: In-Progress Quest Input, Quest Recommendations, etc.
/// </summary>
public partial class LocalizationService
{
    #region In-Progress Quest Input

    public string InProgressQuestInputButton => "진행중 퀘스트 입력";
    public string InProgressQuestInputTitle => "진행중 퀘스트 입력";
    public string QuestSelection => "퀘스트 선택";
    public string SearchQuestsPlaceholder => "퀘스트 검색...";
    public string TraderFilter => "트레이더:";
    public string AllTraders => "전체";
    public string ActiveCorrectionPreview => "진행 중 교정 미리보기";
    public string ActiveCorrectionDescription => "체크한 퀘스트만 현재 진행 중으로 저장합니다.\n완료/실패로 잘못 저장된 항목도 진행 중으로 교정하며, 체크하지 않은 기존 기록은 바꾸지 않습니다.";
    public string SelectedQuestsCount => "선택된 퀘스트: {0}개";
    public string CorrectedQuestsCount => "완료/실패에서 교정: {0}개";
    public string QuestDataNotLoaded => "퀘스트 데이터가 로드되지 않았습니다. 먼저 데이터를 새로고침 해주세요.";
    public string NoQuestsSelected => "선택된 퀘스트가 없습니다.";
    public string QuestsAppliedSuccess => "{0}개의 퀘스트를 현재 진행 중으로 저장했습니다. 그중 {1}개는 잘못된 완료/실패 상태에서 교정했습니다.";

    #endregion

    #region Quest Recommendations

    public string RecommendedQuests => "추천 퀘스트";
    public string ReadyToComplete => "지금 완료 가능";
    public string ItemHandInOnly => "아이템 제출만";
    public string KappaPriority => "카파 필수";
    public string UnlocksMany => "다수 해금";
    public string EasyQuest => "쉬운 퀘스트";
    public string NoRecommendations => "현재 추천 퀘스트가 없습니다";
    public string ItemsOwned => "보유";
    public string ItemsNeeded => "필요";
    public string UnlocksQuests => "개 퀘스트 해금";

    #endregion
}
