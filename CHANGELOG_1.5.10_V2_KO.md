# Tarkov Helper 1.5.10 BUILD_READY v2

## v2에서 추가로 정리한 항목

- `BUILD_NOW.cmd`의 UTF-8 BOM을 제거해 일부 Windows CMD에서 첫 명령이 깨지는 문제를 예방했습니다.
- 빌드 결과 파일명을 `TarkovHelper_v1.5.10_1.1_windows_v2.zip`으로 구분했습니다.
- 구형 Windows PowerShell에서도 공식 .NET 설치 스크립트 다운로드가 되도록 TLS 1.2를 명시했습니다.
- `BUILD_STATUS_KO.txt`를 실제 1.5.10 내용으로 갱신했습니다.
- 프로젝트 파일의 깨진 주석을 정리했습니다.
- `NullToVisibilityConverter.ConvertBack`이 실수로 호출돼도 앱이 종료되지 않도록 `Binding.DoNothing`을 반환하게 했습니다.
- 퀘스트 DB 갱신 취소 요청이 좌표 다운로드 단계에서 무시되지 않도록 취소 예외를 다시 전달합니다.
- DB 교체 직전에 취소 상태를 재확인하고, 백업 파일명에 밀리초를 넣어 연속 갱신 시 덮어쓰기 가능성을 낮췄습니다.

## 유지되는 1.5.10 핵심 기능

- tarkov.dev의 현재 `regular` / `pve` 퀘스트 데이터 갱신
- 퀘스트·선행조건·목표·제출 아이템·선택형 퀘스트 갱신
- TarkovTracker GPS 좌표를 앱 좌표계로 변환해 지도 목표 위치 갱신
- 좌표 매칭이 불확실하거나 좌표 서버가 실패하면 기존 좌표 보존
- 등대 Drug Trafficking 두 목표의 수정 좌표 포함
- 진행도 재구축 전 DB 백업, 미리보기, 실패 시 복원 보호

## 검증

- 포함 SQLite DB `PRAGMA integrity_check`: `ok`
- 포함 퀘스트 수: 488
- 포함 목표 수: 1514
- Drug Trafficking 두 목표 좌표: `X -252.65 / Z -1731.45`
- 프로젝트 XML 파싱 및 필수 빌드 파일 존재 확인

실제 WPF self-contained publish는 Windows에서 `BUILD_NOW.cmd`로 수행하십시오.
