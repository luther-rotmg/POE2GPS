#!/usr/bin/env python3
"""Fold APPROVED community-pack issues into the embedded tables.

Maintainer ritual (per release): review the `community-pack` + `needs-review` issues, label the good ones
`approved` (or close to reject), then run:

    python resources/poe2-data/merge_community.py [--dry-run]

Pulls APPROVED issues via `gh`, parses the pack JSON from each issue body, and folds:
  - names    -> src/POE2Radar.Core/Game/entity_names.json   (curated names win; keys normalized)
  - novel categories -> src/POE2Radar.Overlay/Web/labels.json (appended under a "Community" group)

Read-only / data-only: edits local JSON data files only; never touches the game.
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

REPO = "luther-rotmg/POE2GPS"
ENTITY_NAMES = Path("src/POE2Radar.Core/Game/entity_names.json")
LABELS = Path("src/POE2Radar.Overlay/Web/labels.json")


def normalize_key(raw: str) -> str:
    k = raw.strip()
    at = k.find("@")
    if at >= 0:
        k = k[:at]
    return k.lower()


def fetch_approved_bodies() -> list[str]:
    out = subprocess.run(
        ["gh", "issue", "list", "-R", REPO, "--label", "community-pack", "--label", "approved",
         "--state", "all", "--limit", "1000", "--json", "body"],
        capture_output=True, text=True, check=True).stdout
    return [i.get("body", "") for i in json.loads(out)]


def extract_pack(body: str) -> dict | None:
    m = re.search(r"```json\s*(\{.*?\})\s*```", body, re.S)
    if not m:
        return None
    try:
        return json.loads(m.group(1))
    except json.JSONDecodeError:
        return None


def main(argv: list[str]) -> int:
    dry = "--dry-run" in argv
    bodies = fetch_approved_bodies()
    names: dict[str, str] = {}
    cats: set[str] = set()
    packs = 0
    for b in bodies:
        pack = extract_pack(b)
        if not pack:
            continue
        packs += 1
        for k, v in (pack.get("names") or {}).items():
            if isinstance(v, str) and v.strip():
                names[normalize_key(k)] = v.strip()
        for o in (pack.get("objectives") or []):
            c = (o.get("category") or "").strip() if isinstance(o, dict) else ""
            if c:
                cats.add(c)

    # Fold names (curated wins).
    table = json.loads(ENTITY_NAMES.read_text(encoding="utf-8-sig"))
    added = 0
    for k, v in names.items():
        if k not in table:
            table[k] = v
            added += 1

    # Fold novel categories into labels.json under a "Community" group.
    labels = json.loads(LABELS.read_text(encoding="utf-8-sig"))
    existing = {l for arr in labels.values() for l in arr}
    novel = sorted(c for c in cats if c not in existing)
    if novel:
        labels.setdefault("Community", [])
        for c in novel:
            if c not in labels["Community"]:
                labels["Community"].append(c)

    print(f"{packs} approved pack(s): +{added} names, novel labels: {novel or 'none'}")
    if dry:
        print("(dry-run — no files written)")
        return 0
    ENTITY_NAMES.write_text(json.dumps(table, ensure_ascii=False, sort_keys=True, separators=(",", ":")), encoding="utf-8")
    LABELS.write_text(json.dumps(labels, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {ENTITY_NAMES} and {LABELS}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
