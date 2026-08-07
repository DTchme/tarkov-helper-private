# TarkovHelper 1.5.10 BUILD_READY v4

## Wiki 우선 퀘스트 동기화

- 현재 퀘스트 존재 여부와 기본 정보의 우선 기준을 tarkov.dev에서 Escape from Tarkov Japan Wiki(wikiwiki.jp/eft)로 변경
- Prapor, Therapist, Fence, Skier, Peacekeeper, Mechanic, Ragman, Jaeger, Lightkeeper, Ref, BTR Driver 페이지의 퀘스트 표를 읽어 현재 퀘스트를 보완
- 1.0 이후 Story 퀘스트 목록도 별도 수집
- Wiki에 있으나 로컬 DB에 없는 퀘스트는 새로 추가
- 기존 퀘스트는 Wiki 링크/상인/맵 정보를 갱신
- 기존 DB에 목표가 하나도 없는 퀘스트만 Wiki 목표 텍스트로 보완하여 기존 구조화 목표 및 맵 좌표를 보존
- tarkov.dev는 BSG ID/구조화 아이템 정보 등의 보조 소스로 유지
- tarkov.dev가 일시 장애/스키마 오류를 반환해도 Wiki 갱신은 계속 진행
- Wiki 반영은 임시 DB에서 처리 후 검증/백업/원자적 교체
- Wiki/API 서버 부담을 줄이기 위해 자동 확인 주기를 5분에서 30분으로 조정

## 안전장치

- Wiki에서 파싱된 퀘스트가 250개 미만이면 비정상 응답으로 보고 기존 DB 유지
- 동기화 전 `Backups/tarkov_data_before_wiki_*.db` 자동 생성
- 기존 QuestObjectives가 존재하는 퀘스트는 Wiki 목표로 덮어쓰지 않음
