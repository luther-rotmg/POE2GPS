#!/usr/bin/env python3
"""Fold community-submitted Entity Atlas packs into the embedded name table.

The Entity Atlas (in the overlay) lets players name the entities they encounter and **Export pack** an
``atlas-pack.json`` ( ``{"names": {metadata: name, ...}, "objectives": [...]}`` ). Players submit those
packs (see ``.github/ISSUE_TEMPLATE/entity-name-submission.yml``); each release a maintainer runs this
script to merge the accumulated ``names`` into ``src/POE2Radar.Core/Game/entity_names.json`` so the
coverage ships to everyone. Over many releases this is how we map the whole game.

Read-only / data-only: this only edits a local JSON data file. It never touches the game.

Usage:
    python resources/poe2-data/merge_atlas_packs.py \
        src/POE2Radar.Core/Game/entity_names.json  pack1.json [pack2.json ...] [--overwrite] [--dry-run]

By default an existing name is KEPT (curated names win over submissions); pass ``--overwrite`` to let
later packs replace existing names. Keys are lower-cased and ``@<level>`` suffixes stripped to match how
``EntityNameResolver`` looks them up. The table is written back sorted + compact (deterministic, so diffs
stay clean across releases).
"""
from __future__ import annotations

import json
import sys
from pathlib import Path


def normalize_key(raw: str) -> str:
    """Match EntityNameResolver: lower-case, drop a trailing runtime ``@<level>`` annotation, strip."""
    k = raw.strip()
    at = k.find("@")
    if at >= 0:
        k = k[:at]
    return k.lower()


def load_names(pack: dict) -> dict[str, str]:
    """A pack is ``{"names": {...}, ...}``; also accept a bare ``{metadata: name}`` map."""
    names = pack.get("names") if isinstance(pack, dict) and "names" in pack else pack
    if not isinstance(names, dict):
        return {}
    return names


def main(argv: list[str]) -> int:
    args = [a for a in argv if not a.startswith("--")]
    overwrite = "--overwrite" in argv
    dry_run = "--dry-run" in argv

    if len(args) < 2:
        print(__doc__)
        print("error: need the table path + at least one pack file", file=sys.stderr)
        return 2

    table_path = Path(args[0])
    pack_paths = [Path(p) for p in args[1:]]

    table: dict[str, str] = json.loads(table_path.read_text(encoding="utf-8-sig"))
    before = len(table)
    added = updated = skipped = invalid = 0

    for pp in pack_paths:
        try:
            pack = json.loads(pp.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError) as e:
            print(f"  ! skipping {pp.name}: {e}", file=sys.stderr)
            continue
        for raw_k, raw_v in load_names(pack).items():
            if not isinstance(raw_k, str) or not isinstance(raw_v, str):
                invalid += 1
                continue
            key, val = normalize_key(raw_k), raw_v.strip()
            if not key or not val:
                invalid += 1
                continue
            if key in table:
                if overwrite and table[key] != val:
                    table[key] = val
                    updated += 1
                else:
                    skipped += 1
            else:
                table[key] = val
                added += 1
        print(f"  + {pp.name}: merged")

    ordered = dict(sorted(table.items()))
    out = json.dumps(ordered, ensure_ascii=False, sort_keys=True, separators=(",", ":"))

    print(
        f"\n{table_path.name}: {before} -> {len(ordered)} names "
        f"(+{added} new, ~{updated} updated, {skipped} kept, {invalid} invalid)"
    )
    if dry_run:
        print("--dry-run: not writing.")
        return 0
    table_path.write_text(out, encoding="utf-8")
    print(f"wrote {table_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
