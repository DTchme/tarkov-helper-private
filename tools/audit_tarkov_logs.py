#!/usr/bin/env python3
"""Audit Tarkov push-notification logs without modifying Tarkov Helper data."""
from __future__ import annotations

import argparse
import csv
import json
import re
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

PROFILE_PATTERNS = (
    ("seasonal-pvp", "wsn-pvp-season-"),
    ("pve", "wsn-pve-"),
    ("pvp", "wsn-pvp-"),
)
EVENT_NAMES = {10: "started", 11: "failed", 12: "completed"}


def detect_profile(text: str) -> str:
    for name, marker in PROFILE_PATTERNS:
        if marker.lower() in text.lower():
            return name
    return "unknown"


def iter_json_blocks(text: str):
    lines = text.splitlines()
    buf: list[str] = []
    depth = 0
    active = False
    in_string = False
    escaped = False

    for line in lines:
        stripped = line.lstrip()
        if not active and stripped.startswith("{"):
            active = True
            buf = []
            depth = 0
            in_string = False
            escaped = False
        if not active:
            continue

        buf.append(line)
        for char in line:
            if escaped:
                escaped = False
                continue
            if char == "\\" and in_string:
                escaped = True
                continue
            if char == '"':
                in_string = not in_string
                continue
            if not in_string:
                if char == "{":
                    depth += 1
                elif char == "}":
                    depth -= 1
        if depth == 0:
            raw = "\n".join(buf)
            try:
                yield json.loads(raw)
            except json.JSONDecodeError:
                pass
            active = False


def is_dynamic_operational_template(template_id: str) -> bool:
    tokens = template_id.split()
    return (
        len(tokens) >= 4
        and re.fullmatch(r"[0-9a-fA-F]{24}", tokens[2]) is not None
        and tokens[3].isdigit()
    )


def parse_file(path: Path):
    text = path.read_text(encoding="utf-8", errors="replace")
    profile = detect_profile(text[:65536])
    for block in iter_json_blocks(text):
        if block.get("type") != "new_message":
            continue
        message = block.get("message") or {}
        message_type = message.get("type")
        if message_type not in EVENT_NAMES:
            continue
        template_id = str(message.get("templateId") or "")
        if not template_id or is_dynamic_operational_template(template_id):
            continue
        quest_id = template_id.split()[0]
        timestamp = message.get("dt")
        try:
            local_time = datetime.fromtimestamp(int(timestamp), timezone.utc).isoformat()
        except Exception:
            local_time = ""
        yield {
            "file": path.name,
            "profile": profile,
            "quest_id": quest_id,
            "event": EVENT_NAMES[message_type],
            "message_type": message_type,
            "timestamp_utc": local_time,
        }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("paths", nargs="+", help="Log files or directories")
    parser.add_argument("--output", default="tarkov_log_audit.csv")
    args = parser.parse_args()

    files: list[Path] = []
    for raw in args.paths:
        path = Path(raw)
        if path.is_dir():
            files.extend(path.rglob("*push-notifications*.log"))
        elif path.is_file():
            files.append(path)

    events = []
    for path in sorted(set(files)):
        events.extend(parse_file(path))

    output = Path(args.output)
    with output.open("w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.DictWriter(handle, fieldnames=[
            "file", "profile", "quest_id", "event", "message_type", "timestamp_utc"
        ])
        writer.writeheader()
        writer.writerows(events)

    final_states = defaultdict(dict)
    for event in events:
        final_states[event["profile"]][event["quest_id"]] = event["event"]

    print(f"files={len(files)} events={len(events)} output={output}")
    for profile, states in sorted(final_states.items()):
        counts = defaultdict(int)
        for state in states.values():
            counts[state] += 1
        print(profile, dict(sorted(counts.items())))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
