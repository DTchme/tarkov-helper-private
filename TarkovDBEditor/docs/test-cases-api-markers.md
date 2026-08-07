# API Markers Integration Test Cases

## 원본 요구사항 vs 구현 상태

| # | 요구사항 | 구현 상태 | 비고 |
|---|---------|---------|-----|
| 1 | Map Transfer에서 내가 찍은 마커와 API에서 불러온 마커가 화면 상에서 구분 되어야함 | ✅ 구현됨 | DB 마커: 파란색, API 마커: 별도 스타일 |
| 2 | Map Transfer에서 API 에서 불러온 마커의 퀘스트, Objective가 뭔지 사용자가 알 수 있어야함 | ✅ 구현됨 | 마커 리스트에 Quest/Objective 정보 표시 |
| 3 | DB에 저장 눌렀을 때 Quest Requirement Validator 에서 매칭 | ✅ 구현됨 | ApiMarkers 테이블 저장 → Quest Validator에서 조회 |
| 4 | Map Editor 및 Quest Requirement Validator 에서 API 마커 구분 | ⚠️ 부분 구현 | Quest Validator: ✅, Map Preview: ✅, Map Editor: 미구현 |
| 5 | Quest Requirement Validator 에서 API 마커 Approved 상태 관리 | ✅ 구현됨 | ApiMarkers.IsApproved, ApprovedAt 필드 추가 |
| 6 | 퀘스트 매칭 시 BSG ID 우선, EN name fallback | ✅ 구현됨 | LoadApiMarkers()에서 구현 |

---

## Test Case 1: Map Transfer - API/DB 마커 구분

**목적**: Map Transfer 창에서 DB 마커와 API 마커가 시각적으로 구분되는지 확인

**사전 조건**:
- DB에 MapMarkers 데이터 존재
- Tarkov Market API 접근 가능

**테스트 단계**:
1. Tools > Map Transfer 열기
2. 맵 선택 (예: Customs)
3. "Fetch from API" 클릭
4. DB 마커와 API 마커의 시각적 차이 확인

**예상 결과**:
- DB 마커: 파란색/기존 스타일
- API 마커: 다른 색상 또는 아이콘으로 구분

**실제 결과**: [테스트 필요]

---

## Test Case 2: Map Transfer - 퀘스트/Objective 정보 표시

**목적**: API 마커에서 퀘스트명과 Objective 정보가 표시되는지 확인

**사전 조건**:
- Tarkov Market API에서 퀘스트 관련 마커 fetch

**테스트 단계**:
1. Map Transfer에서 "Fetch from API" 클릭
2. 마커 리스트에서 Quests 카테고리 마커 선택
3. 퀘스트명, Objective 설명 표시 확인

**예상 결과**:
- 마커 정보에 Quest Name, Objective Description 표시

**실제 결과**: [테스트 필요]

---

## Test Case 3: DB 저장 → ApiMarkers 테이블

**목적**: "Import to DB" 시 ApiMarkers 테이블에 정상 저장되는지 확인

**사전 조건**:
- API 마커 fetch 완료

**테스트 단계**:
1. Map Transfer에서 API 마커 선택
2. "Import Selected to DB" 클릭
3. DB에서 ApiMarkers 테이블 조회

**예상 결과**:
- ApiMarkers 테이블에 레코드 생성
- QuestBsgId, QuestNameEn, ObjectiveDescription 필드 채워짐

**실제 결과**: [테스트 필요]

---

## Test Case 4: Quest Validator - API Reference 탭

**목적**: Quest Requirement Validator에서 API Reference 탭이 정상 동작하는지 확인

**사전 조건**:
- ApiMarkers 테이블에 데이터 존재

**테스트 단계**:
1. Tools > Quest Requirements Validator 열기
2. 퀘스트 선택 (ApiMarkers와 매칭되는 퀘스트)
3. "API Reference" 탭 클릭
4. 매칭된 마커 목록 확인

**예상 결과**:
- 해당 퀘스트의 BSG ID 또는 EN name으로 매칭된 API 마커 표시
- 마커별 좌표, 카테고리, Import 시간 표시

**실제 결과**: [테스트 필요]

---

## Test Case 5: BSG ID / EN Name 매칭 우선순위

**목적**: API 마커 매칭 시 BSG ID 우선, EN name fallback 로직 확인

**사전 조건**:
- ApiMarkers에 QuestBsgId가 있는 마커와 QuestNameEn만 있는 마커 존재

**테스트 단계**:
1. BSG ID가 있는 퀘스트 선택 → API Reference 탭 확인
2. BSG ID가 없고 EN name만 있는 퀘스트 선택 → API Reference 탭 확인

**예상 결과**:
- BSG ID 매칭 우선
- BSG ID 없을 경우 EN name으로 fallback 매칭

**실제 결과**: [테스트 필요]

---

## Test Case 6: Apply Location 버튼

**목적**: API 마커의 좌표를 Objective에 적용하는 기능 확인

**사전 조건**:
- Quest Validator에서 API Reference 탭에 마커 표시

**테스트 단계**:
1. API Reference 탭에서 마커의 "Apply Location" 클릭
2. Objective 선택 (여러 개일 경우 다이얼로그)
3. Objectives 탭에서 해당 Objective의 좌표 확인

**예상 결과**:
- Objective의 LocationPoints에 API 마커 좌표 추가
- DB에 저장됨

**실제 결과**: [테스트 필요]

---

## Test Case 7: Map Preview - API 마커 레이어

**목적**: Map Preview에서 API 마커가 별도 레이어로 표시되는지 확인

**사전 조건**:
- ApiMarkers 테이블에 데이터 존재

**테스트 단계**:
1. Tools > Map Preview 열기
2. 맵 선택
3. "API Reference" 체크박스 토글
4. API 마커 표시 확인

**예상 결과**:
- 주황색 오각형 마커로 API 마커 표시
- "API" 뱃지와 카테고리 정보 표시
- 상태바에 API 마커 개수 표시

**실제 결과**: [테스트 필요]

---

## Test Case 8: 요구사항 5 - Approved 상태 관리

**목적**: API 마커의 Approved 상태 관리 기능 확인

**현재 상태**: ✅ 구현됨

**테스트 단계**:
1. Quest Validator에서 API Reference 탭 열기
2. API 마커의 "Approved" 체크박스 클릭
3. 체크박스 상태 변경 확인
4. DB에서 IsApproved, ApprovedAt 필드 확인

**예상 결과**:
- 체크박스 토글 시 상태 즉시 반영
- DB에 IsApproved = 1, ApprovedAt = 현재시간 저장
- 해제 시 IsApproved = 0, ApprovedAt = NULL

**실제 결과**: [테스트 필요]

---

## 발견된 이슈

### Issue 1: Map Editor에서 API 마커 미표시 (요구사항 4 부분)
- **설명**: Map Editor에서는 API 마커가 표시되지 않음 (Map Preview에서만 표시)
- **영향**: Map Editor에서 API 마커 참조 불가
- **해결 방안**: Map Editor에 API 마커 레이어 추가 (선택적 - 사용자 필요시 구현)

---

## 테스트 실행 체크리스트

- [ ] TC1: Map Transfer API/DB 구분
- [ ] TC2: Map Transfer 퀘스트 정보
- [ ] TC3: ApiMarkers DB 저장
- [ ] TC4: Quest Validator API Reference 탭
- [ ] TC5: BSG ID/EN name 매칭
- [ ] TC6: Apply Location 버튼
- [ ] TC7: Map Preview API 레이어
- [ ] TC8: Approved 상태 관리 (미구현 확인)
