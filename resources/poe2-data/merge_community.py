#!/usr/bin/env python3
"""Fold APPROVED community-pack issues into the embedded tables + sidecars.

Maintainer ritual (per release): review the ``community-pack`` + ``needs-review`` issues, label the
good ones ``approved`` (or close to reject), then run:

    python resources/poe2-data/merge_community.py [--dry-run] [--preload] [--buffs]

Pulls APPROVED issues via ``gh`` (body + author + number + url + labels so we can emit a credit
block and dispatch on the Worker's sub-labels), parses the pack JSON from each issue body, and folds:

  - atlas   (no ``buffs`` / ``preload`` label) -> ``names`` into ``entity_names.json`` (curated wins);
                                                  novel categories into ``labels.json`` under a
                                                  ``Community`` group. Legacy default so v0.20-shape
                                                  packs (no sub-label) still fold.
  - preload (``preload`` label, ``--preload``) -> raw ``{preloads:[{path,freq}]}`` aggregated into
                                                  ``poe2_notable_paths_community.json`` sidecar
                                                  (``{paths:[{path,count,freq_sum}]}``). Consumed by
                                                  the v0.22 PreloadCatalog seed loader — ships now
                                                  to unblock CF-DASH-BUTTONS.
  - buffs   (``buffs`` label, ``--buffs``)     -> raw ``{buffs:[{path,tier}]}`` aggregated into
                                                  ``poe2_notable_buffs_community.json`` sidecar
                                                  (``{buffs:[{path,count,tiers}]}``). Same wiring
                                                  pattern as preload; concrete BuffCatalog seed
                                                  loader lands with SL #9 in v0.22.

Read-only / data-only: never touches the game process.

Testing hook: setting ``POE2GPS_MERGE_GH_JSON`` to a filesystem path makes ``fetch_approved_issues``
read the JSON array from that file instead of shelling out to ``gh``. Used by
``MergeCommunityPreloadFoldTests`` / ``MergeCommunityBuffsFoldTests``.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from pathlib import Path

REPO = "luther-rotmg/POE2GPS"
ENTITY_NAMES = Path("src/POE2Radar.Core/Game/entity_names.json")
LABELS = Path("src/POE2Radar.Overlay/Web/labels.json")
PRELOAD_SIDECAR = Path("src/POE2Radar.Core/Game/poe2_notable_paths_community.json")
BUFFS_SIDECAR = Path("src/POE2Radar.Core/Game/poe2_notable_buffs_community.json")


def normalize_key(raw: str) -> str:
    k = raw.strip()
    at = k.find("@")
    if at >= 0:
        k = k[:at]
    return k.lower()


def fetch_approved_issues() -> list[dict]:
    override = os.environ.get("POE2GPS_MERGE_GH_JSON")
    if override:
        return json.loads(Path(override).read_text(encoding="utf-8"))
    out = subprocess.run(
        ["gh", "issue", "list", "-R", REPO, "--label", "community-pack", "--label", "approved",
         "--state", "all", "--limit", "1000", "--json", "body,author,number,url,labels"],
        capture_output=True, text=True, check=True).stdout
    return json.loads(out)


def extract_pack(body: str) -> dict | None:
    m = re.search(r"```json\s*(\{.*?\})\s*```", body, re.S)
    if not m:
        return None
    try:
        return json.loads(m.group(1))
    except json.JSONDecodeError:
        return None


def issue_labels(issue: dict) -> set[str]:
    out = set()
    for lbl in issue.get("labels") or []:
        if isinstance(lbl, dict):
            name = lbl.get("name")
        else:
            name = lbl
        if isinstance(name, str) and name.strip():
            out.add(name.strip())
    return out


def dispatch_kind(labels: set[str], pack: dict) -> str:
    # Worker attaches ``buffs`` / ``preload`` as a sub-label on top of the base
    # ``community-pack`` + ``needs-review`` pair. Fall back to the pack's ``kind`` string
    # (older v0.20-shape submissions), then default to atlas so historical packs still fold.
    if "buffs" in labels:
        return "buffs"
    if "preload" in labels:
        return "preload"
    k = (pack.get("kind") or "").strip().lower() if isinstance(pack, dict) else ""
    if k in ("buffs", "preload"):
        return k
    return "atlas"


def fold_atlas(pack: dict, names: dict[str, str], cats: set[str]) -> None:
    for k, v in (pack.get("names") or {}).items():
        if isinstance(v, str) and v.strip():
            names[normalize_key(k)] = v.strip()
    for o in (pack.get("objectives") or []):
        c = (o.get("category") or "").strip() if isinstance(o, dict) else ""
        if c:
            cats.add(c)


def fold_preload(pack: dict, hits: dict[str, dict]) -> None:
    src = pack.get("preloads")
    if not isinstance(src, list):
        return
    for p in src:
        if not isinstance(p, dict):
            continue
        path = p.get("path")
        if not isinstance(path, str) or not path.strip():
            continue
        key = path.strip().lower()
        entry = hits.setdefault(key, {"count": 0, "freq_sum": 0.0})
        entry["count"] += 1
        freq = p.get("freq")
        if isinstance(freq, (int, float)):
            entry["freq_sum"] += float(freq)


def fold_buffs(pack: dict, buffs: dict[str, dict]) -> None:
    src = pack.get("buffs")
    if not isinstance(src, list):
        return
    for b in src:
        if not isinstance(b, dict):
            continue
        path = b.get("path")
        if not isinstance(path, str) or not path.strip():
            continue
        key = path.strip().lower()
        entry = buffs.setdefault(key, {"count": 0, "tiers": {}})
        entry["count"] += 1
        tier = b.get("tier")
        if isinstance(tier, (str, int)):
            t = str(tier).strip()
            if t:
                entry["tiers"][t] = entry["tiers"].get(t, 0) + 1


def build_credit_block(contributors: list[tuple[str, list[tuple[int, str]]]]) -> str:
    """Deterministic markdown credit block.

    ``contributors`` is a list of ``(handle, [(issue_number, issue_url), ...])`` tuples.
    Handles are expected pre-sorted case-insensitively; issues per handle pre-sorted
    ascending by number. Rendered as a ``### Community contributors`` H3 block plus
    one bullet per contributor, ready to paste above the themed body of CHANGELOG.md.
    """
    lines = ["### Community contributors", ""]
    for handle, issues in contributors:
        refs = ", ".join(f"[#{n}]({u})" for n, u in issues)
        lines.append(f"- @{handle} — {refs}")
    lines.append("")
    return "\n".join(lines)


def _write_preload_sidecar(hits: dict[str, dict]) -> None:
    sidecar = {
        "paths": [
            {"path": p, "count": h["count"], "freq_sum": round(h["freq_sum"], 4)}
            for p, h in sorted(hits.items())
        ]
    }
    PRELOAD_SIDECAR.parent.mkdir(parents=True, exist_ok=True)
    PRELOAD_SIDECAR.write_text(
        json.dumps(sidecar, ensure_ascii=False, indent=2), encoding="utf-8")


def _write_buffs_sidecar(buffs: dict[str, dict]) -> None:
    sidecar = {
        "buffs": [
            {"path": p, "count": e["count"], "tiers": e["tiers"]}
            for p, e in sorted(buffs.items())
        ]
    }
    BUFFS_SIDECAR.parent.mkdir(parents=True, exist_ok=True)
    BUFFS_SIDECAR.write_text(
        json.dumps(sidecar, ensure_ascii=False, indent=2), encoding="utf-8")


def main(argv: list[str]) -> int:
    dry = "--dry-run" in argv
    do_preload = "--preload" in argv
    do_buffs = "--buffs" in argv

    issues = fetch_approved_issues()

    names: dict[str, str] = {}
    cats: set[str] = set()
    preload_hits: dict[str, dict] = {}
    buff_paths: dict[str, dict] = {}
    # handle -> dict[int, str]  (number -> url), naturally dedup'd per-issue
    credit: dict[str, dict[int, str]] = {}
    packs = 0

    for issue in issues:
        body = issue.get("body") or ""
        pack = extract_pack(body)
        if not pack:
            continue
        packs += 1

        author = issue.get("author") or {}
        handle = (author.get("login") if isinstance(author, dict) else None) or ""
        handle = handle.strip() or "unknown"
        number = int(issue.get("number") or 0)
        url = (issue.get("url") or "").strip()
        if number:
            credit.setdefault(handle, {})[number] = url

        kind = dispatch_kind(issue_labels(issue), pack)
        if kind == "preload" and do_preload:
            fold_preload(pack, preload_hits)
        elif kind == "buffs" and do_buffs:
            fold_buffs(pack, buff_paths)
        elif kind == "atlas":
            fold_atlas(pack, names, cats)
        # else: sub-label pack folded off — skip silently (opt-in per --preload/--buffs flag).

    # Atlas fold (curated wins) — always runs (atlas packs may coexist with sub-label runs).
    if ENTITY_NAMES.exists():
        table = json.loads(ENTITY_NAMES.read_text(encoding="utf-8-sig"))
    else:
        table = {}
    for k, v in names.items():
        if k not in table:
            table[k] = v

    if LABELS.exists():
        labels_json = json.loads(LABELS.read_text(encoding="utf-8-sig"))
    else:
        labels_json = {}
    existing = {l for arr in labels_json.values() for l in arr}
    novel = sorted(c for c in cats if c not in existing)
    if novel:
        labels_json.setdefault("Community", [])
        for c in novel:
            if c not in labels_json["Community"]:
                labels_json["Community"].append(c)

    # Input-derived stats (stable across reruns against the same issue set — so the
    # idempotency test can compare full stdout byte-for-byte). Delta info (+added
    # names, novel labels) surfaces via ``git diff`` on the written data files.
    print(
        f"atlas fold: {packs} approved pack(s), {len(names)} unique names, "
        f"{len(cats)} unique categories")

    if do_preload:
        total_hits = sum(h["count"] for h in preload_hits.values())
        print(f"preload fold: {len(preload_hits)} unique paths (aggregated across {total_hits} submissions)")
        if preload_hits and not dry:
            _write_preload_sidecar(preload_hits)
            print(f"wrote {PRELOAD_SIDECAR}")

    if do_buffs:
        total_hits = sum(h["count"] for h in buff_paths.values())
        print(f"buffs fold: {len(buff_paths)} unique paths (aggregated across {total_hits} submissions)")
        if buff_paths and not dry:
            _write_buffs_sidecar(buff_paths)
            print(f"wrote {BUFFS_SIDECAR}")

    # Deterministic credit block: handles sorted case-insensitively, issues per
    # handle sorted ascending by number. Rerunning against the same issue set
    # produces byte-identical stdout — safe to pipe into CHANGELOG.md.
    contributors = [
        (h, sorted(nu.items()))
        for h, nu in sorted(credit.items(), key=lambda kv: kv[0].lower())
    ]
    if contributors:
        print()
        print("--- credit block (paste above the themed body in CHANGELOG.md) ---")
        print(build_credit_block(contributors))
        print("--- end credit block ---")

    if dry:
        print("(dry-run — no data-file writes)")
        return 0

    ENTITY_NAMES.parent.mkdir(parents=True, exist_ok=True)
    ENTITY_NAMES.write_text(
        json.dumps(table, ensure_ascii=False, sort_keys=True, separators=(",", ":")),
        encoding="utf-8")
    LABELS.parent.mkdir(parents=True, exist_ok=True)
    LABELS.write_text(
        json.dumps(labels_json, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {ENTITY_NAMES} and {LABELS}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
