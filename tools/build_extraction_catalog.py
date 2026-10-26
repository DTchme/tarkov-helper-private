"""Build a reviewed extraction snapshot from saved English Wiki and game-source responses.

Wiki membership/factions take priority over scene-only extraction objects. Game
coordinates supplement the Wiki. A missing vehicle zone is located by a fitted
Wiki-map transform, with calibration residuals included in the generated data.
"""
import hashlib
import json
import re
import sys
from pathlib import Path

import numpy as np

source, destination = map(Path, sys.argv[1:3])
wiki = json.loads((source / 'wiki-maps.json').read_text(encoding='utf-8-sig'))
interactive = json.loads((source / 'wiki-policies.json').read_text(encoding='utf-8-sig'))
game = json.loads((source / 'maps-pve.json').read_text(encoding='utf-8-sig'))['data']['maps']
english = json.loads((source / 'maps-en.json').read_text(encoding='utf-8-sig'))['data']


def clean(value):
    value = re.sub(r'\[\[File:(\d+) Roubles\.png[^]]*\]\]', r'\1 RUB', value, flags=re.I)
    value = re.sub(r'\[\[File:ZB-1013 key icon\.png[^]]*\]\]', 'ZB-1013 key', value, flags=re.I)
    value = value.split('\n|}')[0]
    value = re.sub(r'\[\[File:.*?\]\]', '', value, flags=re.I | re.S)
    value = re.sub(r'\[\[([^]|]+)\|([^]]+)\]\]', r'\2', value)
    value = re.sub(r'\[\[([^]]+)\]\]', r'\1', value)
    value = re.sub(r'<br\s*/?>', '\n', value, flags=re.I)
    value = re.sub(r'<[^>]+>', '', value).replace("'''", '').replace('✔', '').replace('✘', '')
    return re.sub(r'\n\s*\n', '\n', value).strip(' |\n')


def key(value):
    return re.sub('[^a-z0-9]', '', value.lower())


all_rows = []
for page in wiki['query']['pages']:
    map_name = page['title']
    revision = page['revisions'][0]
    text = revision['slots']['main']['content']
    table = re.search(r'==\s*Extractions\s*==(.*?)(?=\n==[^=]|\Z)', text, re.S).group(1)
    definitions = []
    for block in re.split(r'\n\|-\s*\n', table)[1:]:
        name = re.search(r'!\s*style="text-align:\s*left;"\s*\|([^\n]+)', block)
        if not name:
            continue
        fields = re.split(r'\n\|(?=[^}\-])', block[name.end():].lstrip('\n|'), maxsplit=5)
        faction = fields[0].strip()
        # Fields are faction, availability, single-use, requirements and notes.
        requirements = clean(fields[3]) if len(fields) > 3 else ''
        notes = clean(fields[4]) if len(fields) > 4 else ''
        definitions.append((clean(name.group(1)), faction, requirements, notes))
    assert len(definitions) >= 10, (map_name, definitions)
    gm = next(value for value in game.values() if value['normalizedName'] == map_name.lower())
    zones = {}
    for zone in gm['extracts']:
        title = english.get(zone['name'], zone['name'])
        zones.setdefault(key(title), []).append(zone)
    mp = next(p for p in interactive['query']['pages'] if p['title'] == 'Map:' + map_name)
    markers = [marker for marker in json.loads(mp['revisions'][0]['slots']['main']['content'])['markers']
               if marker['categoryId'] in ('exfil_pmc', 'exfil_scav')]
    anchors = []
    for marker in markers:
        candidates = zones.get(key(marker['popup']['title'].strip()), [])
        faction = 'pmc' if marker['categoryId'] == 'exfil_pmc' else 'scav'
        candidates = [zone for zone in candidates if zone['faction'] in (faction, 'shared')]
        if candidates:
            position = candidates[0]['position']
            anchors.append((marker['position'], (position['x'], position['z'])))
    matrix = np.array([[p[0], p[1], 1] for p, _ in anchors])
    targets = np.array([p for _, p in anchors])
    transform, *_ = np.linalg.lstsq(matrix, targets, rcond=None)
    residuals = np.linalg.norm(matrix @ transform - targets, axis=1)
    # Robustly reject an artist label offset or a relocated zone before fitting.
    mask = residuals <= max(15, float(np.median(residuals)) * 2)
    transform, *_ = np.linalg.lstsq(matrix[mask], targets[mask], rcond=None)
    max_error = float(np.max(np.linalg.norm(matrix[mask] @ transform - targets[mask], axis=1)))
    assert mask.sum() >= 6 and max_error < 20, (map_name, max_error)
    for name, faction, requirements, notes in definitions:
        candidates = zones.get(key(name), [])
        basis = 'json.tarkov.dev/pve/maps'
        error = 0.0
        if candidates:
            position = candidates[0]['position']
        else:
            marker = next((m for m in markers if key(m['popup']['title']) == key(name)), None)
            assert marker is not None, (map_name, name, 'missing coordinate source')
            x, z = np.array([*marker['position'], 1]) @ transform
            position = {'x': round(float(x), 3), 'y': 0, 'z': round(float(z), 3)}
            basis = 'English Wiki interactive map / calibrated world coordinates'
            error = round(max_error, 2)
        kind = 'SharedExtraction' if faction == 'All' else 'PmcExtraction' if faction == 'PMC' else 'ScavExtraction'
        floor = 'basement' if map_name == 'Customs' and name in ('Boiler Room Basement (Co-op)', 'ZB-1011', 'ZB-1013', "Smugglers' Bunker (ZB-1012)", 'Old Gas Station') else 'main'
        all_rows.append(dict(Id='wiki-extract-' + hashlib.sha256((map_name + '|' + name).encode()).hexdigest()[:20],
            Map=map_name, Name=name, Type=kind, X=position['x'], Y=position['y'], Z=position['z'], Floor=floor,
            Requirements=requirements, Notes=notes, SourceUrl='https://escapefromtarkov.fandom.com/wiki/' + map_name,
            WikiRevision=revision['revid'], MapRevision=mp['revisions'][0]['revid'], PositionSource=basis, PositionErrorMeters=error,
            VerifiedAt='2026-10-26', GameModes='all', Status='verified'))
    # RUAF remains in the PvE scene; retain it explicitly labelled pending
    # rather than asserting either removal or current availability from the Wiki.
    if map_name == 'Customs':
        zone = zones[key('RUAF Roadblock')][0]
        p = zone['position']
        all_rows.append(dict(Id='wiki-extract-customs-ruaf-pve', Map=map_name,
            Name='RUAF Roadblock (PVE · 확인 필요)', Type='SharedExtraction',
            X=p['x'], Y=p['y'], Z=p['z'], Floor='main', GameModes='pve',
            Requirements='PVE 게임 데이터에 남아 있으나 현재 영문 Wiki 탈출구 목록에는 없습니다. 게임의 탈출구 목록을 확인하세요.',
            Notes='', SourceUrl='https://escapefromtarkov.fandom.com/wiki/Customs', WikiRevision=revision['revid'],
            PositionSource='json.tarkov.dev/pve/maps', PositionErrorMeters=0, VerifiedAt='2026-10-26', Status='review_required'))
    for transit in gm.get('transits', []):
        target = game[transit['map']]
        target_name = english.get(target['name'], target.get('normalizedName', transit['map']))
        p = transit['position']
        all_rows.append(dict(Id=f"wiki-transit-{map_name}-{transit['id']}", Map=map_name,
            Name='Transit to ' + target_name, Type='Transit', X=p['x'], Y=p['y'], Z=p['z'], Floor='main',
            Requirements='맵 간 이동 지점 · 이용 조건은 게임 내 목록 확인', Notes=english.get(transit.get('description', ''), ''),
            SourceUrl='https://escapefromtarkov.fandom.com/wiki/Map:' + map_name,
            WikiRevision=mp['revisions'][0]['revid'], MapRevision=mp['revisions'][0]['revid'], PositionSource='json.tarkov.dev/pve/maps',
            PositionErrorMeters=0, VerifiedAt='2026-10-26', GameModes='all', Status='verified'))
    print(map_name, 'Wiki extractions:', len(definitions), 'calibration anchors:', int(mask.sum()), 'maximum residual:', round(max_error, 2))
destination.write_text(json.dumps({'Version': 2, 'VerifiedAt': '2026-10-26', 'Markers': all_rows}, ensure_ascii=False, indent=2), encoding='utf-8')
print('Catalog markers:', len(all_rows))
