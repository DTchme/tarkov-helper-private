# Tarkov Market Markers API 분석

## API Endpoint

```
GET https://tarkov-market.com/api/be/markers/list?map={mapName}
```

### 지원하는 맵 이름
- `customs`
- `factory`
- `interchange`
- `labs`
- `lighthouse`
- `reserve`
- `shoreline`
- `streets`
- `woods`
- `ground-zero`

## Response 구조

```json
{
  "markers": "<OBFUSCATED_BASE64_STRING>"
}
```

`markers` 필드는 난독화된 Base64 문자열로, 디코딩이 필요함.

## 디코딩 알고리즘

### 1. 난독화 제거

원본 문자열에서 **index 5~9 (5글자)를 제거**:

```
원본: JTVCJNjMkgTdCJTIydWl...
       ^^^^^
       제거할 부분 (index 5-9)

결과: JTVCJTdCJTIydWl...
```

```csharp
string processed = encoded.Substring(0, 5) + encoded.Substring(10);
```

### 2. Base64 디코드

```csharp
byte[] bytes = Convert.FromBase64String(processed);
string urlEncoded = Encoding.UTF8.GetString(bytes);
```

### 3. URL 디코드

```csharp
string json = Uri.UnescapeDataString(urlEncoded);
```

### 4. JSON 파싱

```csharp
var markers = JsonSerializer.Deserialize<List<TarkovMarketMarker>>(json);
```

## 전체 디코딩 코드 (C#)

```csharp
public static List<TarkovMarketMarker>? DecodeMarkers(string encoded)
{
    try
    {
        // 1. index 5~9 (5글자) 제거
        var processed = encoded.Substring(0, 5) + encoded.Substring(10);

        // 2. Base64 디코드
        var bytes = Convert.FromBase64String(processed);
        var urlEncoded = Encoding.UTF8.GetString(bytes);

        // 3. URL 디코드
        var json = Uri.UnescapeDataString(urlEncoded);

        // 4. JSON 파싱
        return JsonSerializer.Deserialize<List<TarkovMarketMarker>>(json);
    }
    catch
    {
        return null;
    }
}
```

## 전체 디코딩 코드 (JavaScript)

```javascript
function decodeMarkers(encoded) {
    // 1. index 5~9 (5글자) 제거
    const processed = encoded.substring(0, 5) + encoded.substring(10);

    // 2. Base64 디코드
    const urlEncoded = atob(processed);

    // 3. URL 디코드
    const json = decodeURIComponent(urlEncoded);

    // 4. JSON 파싱
    return JSON.parse(json);
}
```

## Marker 데이터 구조

```json
{
  "uid": "ae6c753c-757b-41da-a28e-9eb5fd2d38aa",
  "category": "Quests",
  "subCategory": "Quest",
  "name": "Obtain the Registered Letter",
  "desc": null,
  "map": "streets",
  "level": 1,
  "geometry": {
    "x": 111.5829,
    "y": 43.342
  },
  "questUid": "3e143ab4-54d0-4ada-830d-7873031920f9",
  "itemsUid": null,
  "imgs": [
    {
      "img": "https://cdn.tarkov-market.app/maps/images/streets/markers/ae6c753c-757b-41da-a28e-9eb5fd2d38aa_0.webp?c=1701345784401",
      "name": "The post office",
      "desc": "In the top left slightly opened drawer..."
    }
  ],
  "updated": "2024-07-13T13:33:10.707Z",
  "name_l10n": { "ru": "" },
  "desc_l10n": { "ru": "" }
}
```

### 필드 설명

| 필드 | 타입 | 설명 |
|------|------|------|
| `uid` | string | 마커 고유 ID (UUID) |
| `category` | string | 주요 카테고리 |
| `subCategory` | string | 세부 카테고리 |
| `name` | string | 마커 이름 (영문) |
| `desc` | string? | 마커 설명 |
| `map` | string | 맵 이름 (소문자) |
| `level` | int? | 층 번호 (다층 맵용) |
| `geometry` | object | 좌표 (x, y) |
| `questUid` | string? | 연관된 퀘스트 ID |
| `itemsUid` | string[]? | 연관된 아이템 ID 목록 |
| `imgs` | object[] | 마커 이미지 목록 |
| `updated` | datetime | 마지막 업데이트 시간 |
| `name_l10n` | object | 다국어 이름 |
| `desc_l10n` | object | 다국어 설명 |

### 카테고리 종류

| Category | SubCategory | 설명 |
|----------|-------------|------|
| Quests | Quest | 퀘스트 목표 위치 |
| Extractions | PMC Extraction | PMC 탈출구 |
| Extractions | Scav Extraction | Scav 탈출구 |
| Extractions | Co-op Extraction | 협동 탈출구 |
| Spawns | PMC Spawn | PMC 스폰 포인트 |
| Spawns | Scav Spawn | Scav 스폰 포인트 |
| Spawns | Boss Spawn | 보스 스폰 포인트 |
| Keys | Key | 열쇠 사용 위치 |
| Loot | Cache | 은닉 장소 |
| Miscellaneous | Lever, Switch | 레버, 스위치 등 |

## 좌표 시스템

`geometry.x`와 `geometry.y`는 **SVG viewBox 좌표계**를 사용함.

맵 이미지의 실제 픽셀 좌표로 변환하려면 `SvgBounds`를 사용:

```csharp
// SvgBounds: [[maxLat, minLng], [minLat, maxLng]]
var svgMinX = config.SvgBounds[0][1]; // minLng
var svgMaxX = config.SvgBounds[1][1]; // maxLng
var svgMinY = config.SvgBounds[1][0]; // minLat
var svgMaxY = config.SvgBounds[0][0]; // maxLat

// SVG 좌표를 0~1 범위로 정규화
var normalizedX = (marketX - svgMinX) / (svgMaxX - svgMinX);
var normalizedY = (marketY - svgMinY) / (svgMaxY - svgMinY);

// 픽셀 좌표로 변환
var pixelX = normalizedX * imageWidth;
var pixelY = normalizedY * imageHeight;
```

## 원본 난독화 코드 분석

Tarkov Market 웹사이트의 난독화된 JavaScript 코드:

```javascript
let r = null;
try {
    let a = e;  // e = markers 문자열
    a = j7(a) + a.substring(n.dlpKY(3, 2) - 2);  // j7(a) + a.substring(10)
    a = window[Bn](a);  // atob(a)
    r = JSON.parse(n.EOxWH(A7)(a));  // JSON.parse(decodeURIComponent(a))
} catch {}
return r;
```

### 분석된 함수들

| 난독화 | 실제 값 |
|--------|---------|
| `Bn` | `"atob"` |
| `window[Bn]` | `atob` (Base64 디코드) |
| `n.EOxWH(A7)` | `decodeURIComponent` |
| `j7(a)` | `a.substring(0, 5)` |
| `n.dlpKY(3, 2) - 2` | `10` |

### j7 함수 분석

```javascript
j7 = e => e.substring(...$7()[ya(282)]()[ya(296)](1))
// $7() = [5, 0, 36]
// ya(282) = "reverse"
// ya(296) = "slice"
// 결과: e.substring(...[36, 0, 5].slice(1)) = e.substring(0, 5)
```

## Quests List API

퀘스트 목록을 가져오는 API. 마커의 `questUid`와 tarkov.dev의 task ID를 매칭하는 데 사용.

### Endpoint

```
GET https://tarkov-market.com/api/be/quests/list
```

### Response 구조

```json
{
  "result": "ok",
  "user": "<OBFUSCATED_STRING>",
  "quests": [...]
}
```

`quests` 필드도 markers와 동일한 난독화 방식 적용 (index 5~9 제거 후 Base64 + URL 디코드).

### Quest 데이터 구조

```json
{
  "uid": "7cc9e263-2f04-4ff9-b1fd-3781c028ba9f",
  "bsgId": "626148251ed3bb5bcc5bd9ed",
  "active": true,
  "name": "Make Amends - Buyout",
  "ruName": "Поправки - Выкуп",
  "trader": "Mechanic",
  "type": "Loyalty",
  "wikiUrl": "https://escapefromtarkov.fandom.com/wiki/Make_Amends_-_Buyout",
  "reqLevel": null,
  "reqLL": null,
  "reqRep": null,
  "requiredForKappa": false,
  "objectives": [],
  "enObjectives": ["Hand over 1,000,000 RUB"],
  "ruObjectives": ["Передать 1 000 000 RUB"],
  "updated": "2025-07-17T11:38:01.143Z"
}
```

### 필드 설명

| 필드 | 타입 | 설명 |
|------|------|------|
| `uid` | string | Tarkov Market 내부 퀘스트 ID (UUID) |
| `bsgId` | string | **BSG 공식 퀘스트 ID (tarkov.dev의 task.ids와 일치)** |
| `active` | bool | 퀘스트 활성화 여부 |
| `name` | string | 퀘스트 이름 (영문) |
| `ruName` | string | 퀘스트 이름 (러시아어) |
| `trader` | string | 퀘스트 제공 트레이더 |
| `type` | string | 퀘스트 유형 (Elimination, Loyalty 등) |
| `wikiUrl` | string | 위키 페이지 URL |
| `reqLevel` | int? | 필요 레벨 |
| `reqLL` | int? | 필요 로열티 레벨 |
| `reqRep` | float? | 필요 평판 |
| `requiredForKappa` | bool | 카파 컨테이너 필요 여부 |
| `objectives` | array | 목표 상세 정보 |
| `enObjectives` | string[] | 목표 설명 (영문) |
| `ruObjectives` | string[] | 목표 설명 (러시아어) |
| `updated` | datetime | 마지막 업데이트 시간 |

## tarkov.dev API와의 매칭

### 핵심 매칭 키

**Tarkov Market `bsgId` = tarkov.dev `task.ids`**

```
Tarkov Market: bsgId = "626148251ed3bb5bcc5bd9ed"
tarkov.dev:    ids = ["626148251ed3bb5bcc5bd9ed"]
```

### 매칭 흐름

```
1. markers/list API에서 퀘스트 마커 가져오기
   └─ marker.questUid 획득

2. quests/list API에서 퀘스트 목록 가져오기
   └─ questUid로 quest 찾기
   └─ quest.bsgId 획득

3. tarkov.dev tasks.json에서 bsgId로 task 매칭
   └─ task.ids 배열에 bsgId 포함 여부 확인

4. 매칭된 task의 objective와 marker 연결
   └─ marker.geometry (SVG 좌표) 사용하여 정확한 위치 표시
```

### 매칭 예시

```csharp
// Tarkov Market marker
var marker = new {
    questUid = "8eb8ddd9-487a-4a52-9654-dbe8a61fb248",
    name = "Obtain Secure Folder 0013",
    geometry = new { x = -140.0316, y = 493.4423 }
};

// Tarkov Market quest (quests/list에서 조회)
var quest = new {
    uid = "8eb8ddd9-487a-4a52-9654-dbe8a61fb248",
    bsgId = "5979ecc086f77426d702a0f1",  // Chemical - Part 1
    name = "Chemical - Part 1"
};

// tarkov.dev task 매칭
var task = tasks.FirstOrDefault(t => t.Ids.Contains(quest.bsgId));
// task.Name = "Chemical - Part 1" ✓
```

## 참고

- API는 별도의 인증 없이 접근 가능
- Rate limiting이 있을 수 있으므로 요청 간 적절한 딜레이 권장
- 난독화 방식은 향후 변경될 수 있음
- `bsgId`는 BSG(Battlestate Games) 공식 ID로, tarkov.dev API와 동일한 ID 체계 사용
