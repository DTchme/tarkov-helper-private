# TarkovHelper 1.5.10 BUILD_READY v3

## tarkov.dev 일시 장애 처리 보강

- HTTP 408/425/429/500/502/503/504를 일시 장애로 분류하고 최대 3회 재시도합니다.
- tarkov.dev가 HTTP 422와 `GraphQL server unavailable. Try again later.`를 반환하는 경우에만 일시 장애로 분류합니다.
- 실제 GraphQL 쿼리/스키마 오류인 다른 422 응답은 재시도하지 않고 기존처럼 오류로 표시합니다.
- 일시 장애 시 현재 `tarkov_data.db`를 그대로 유지하며 DB 파일과 버전 파일을 변경하지 않습니다.
- UI에서는 일시 장애를 빨간 오류가 아닌 주황색 재시도 안내로 표시합니다.
- 로그 수준도 ERROR가 아닌 WARNING으로 낮춰 로컬 앱 고장과 외부 서버 장애를 구분합니다.

## 확인된 로그 원인

사용자 로그의 응답은 다음과 같습니다.

```text
422 Unprocessable Entity
{"errors":["GraphQL server unavailable. Try again later."]}
```

이는 앱의 DB 변환 단계에 도달하기 전에 tarkov.dev 백엔드가 거부한 응답이며, 기존 DB는 교체되지 않습니다.
