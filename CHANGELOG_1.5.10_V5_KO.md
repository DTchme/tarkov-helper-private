# TarkovHelper 1.5.10 BUILD_READY v5

- 상단 프로필 선택을 PVP / PVE / PVP S(Seasonal PvP) 3개로 분리
- SeasonalPvp를 ProfileType=2로 추가하여 퀘스트/목표/아이템/은신처/설정 진행도를 영구 PVP/PVE와 독립 저장
- 로그의 wsn-pvp-season-* 이벤트를 PVP S 프로필에만 반영
- KORD BREACH 명칭의 시즌 전용 퀘스트는 일반 PVP/PVE 목록에서 제외하고 PVP S에서만 노출
- 시즌 PVP는 일반 퀘스트 풀 + 시즌 전용 퀘스트를 함께 계산
- 일본 EFT Wiki 시즌 계정 페이지에서 KORD BREACH 링크가 추가될 경우 자동 수집하도록 보강
- tarkov.dev는 Seasonal PvP에서 regular 구조화 데이터만 보조적으로 사용하고 시즌 존재 여부는 Wiki/프로필 필터가 담당
- Kappa 필수 배지, Kappa 전용 필터, Kappa 진행 게이지, Collector Kappa 선행 진행 표시를 UI에서 비활성화
- 수집가 탭 이름을 `수집가 - 카파`에서 `수집가`로 단순화하고 내부 Kappa 배지를 숨김
- 레벨/스캐브 우호도/DSP 해독 수/진영/프레스티지 값을 프로필별로 분리하여 시즌 캐릭터 계산에 사용
- 추천 퀘스트 점수에서 KappaRequired 가중치 및 Kappa 전용 추천 분기를 제거

주의: 2026-08-07 현재 일본 Wiki의 시즌 계정 페이지는 KORD BREACH 신규 스토리 태스크 상세가 아직 작성 중이므로, 시즌 전용 퀘스트의 상세 목표는 Wiki에 등재되는 즉시 후속 동기화에서 보완됩니다.
