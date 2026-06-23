# POE2GPS — Community Pipeline (Contribute v2 + Moderation) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Re-architect Contribute so the PROJECT hosts one Worker (URL baked into the app → end-users click once), the Worker auto-filters junk + files reviewable GitHub Issues, and a maintainer merge script folds APPROVED submissions into the shipped tables.

**Architecture:** Mostly infra/tooling — an upgraded `cloudflare-worker/worker.js` (auto-filter + review summary), a new `merge_community.py` (approved-issue → embedded-table merge), a tiny shipped-app change (a baked-in default `ContributeUrl` + a tooltip), and docs. The shipped overlay stays read-only/GGG-compliant.

**Tech Stack:** Cloudflare Worker (JS), Python merge tooling, C# / .NET 10 (the tiny app change), vanilla-JS dashboard.

## Global Constraints

- **.NET 10, x64.** `TreatWarningsAsErrors=true`, `Nullable=enable` → the shipped app builds **0W/0E**.
- **Read-only / GGG-compliant (shipped app).** The app change is a settings default + a tooltip + the existing outbound POST. No input/process-write/pricing. `compliance-gate.ps1` PASS. **No identifying data leaves the client** (the pack is the verified `{names, objectives}` only).
- **Worker + merge script are infra/dev tooling** (not compiled into the overlay, outside the gate's .NET scan). The Worker's GitHub token stays an `env` secret — NEVER in any committed file.
- Build: `dotnet build POE2Radar.slnx`. Test: `dotnet test POE2Radar.slnx`. Gate: `compliance-gate.ps1`.

---

## File Structure

**New:** `resources/poe2-data/merge_community.py`, `docs/community-pipeline.md`.
**Modified:** `cloudflare-worker/worker.js`, `cloudflare-worker/README.md`, `src/POE2Radar.Overlay/Config/RadarSettings.cs`, `src/POE2Radar.Overlay/Web/DashboardHtml.cs`, `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (version, T4).

---

## Task 1: Worker v2 — auto-filter + review summary

**Files:** Modify `cloudflare-worker/worker.js`, `cloudflare-worker/README.md`.

- [ ] **Step 1: Replace `cloudflare-worker/worker.js` with the v2**

```javascript
// POE2GPS community-pack collector (v2). Receives a non-identifying {names, objectives} pack, AUTO-FILTERS
// junk (profanity / gibberish / over-length / dupes / identifying), and files clean submissions as a
// reviewable GitHub Issue (community-pack + needs-review) with a parsed summary. The GitHub token is a
// Worker SECRET (env.GITHUB_TOKEN) — never in the client.
const REPO = 'luther-rotmg/POE2GPS';
const MAX_BYTES = 262144; // 256 KB
const CORS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'POST, OPTIONS',
  'Access-Control-Allow-Headers': 'Content-Type',
};
// Conservative profanity/slur list (word-boundary, case-insensitive). Keep it tight to avoid false
// positives on legit PoE terms; extend as the community surfaces abuse. (Maintainer-editable.)
const PROFANITY = ['nigger', 'faggot', 'retard', 'kike', 'spic', 'chink', 'cunt'];

export default {
  async fetch(request, env) {
    if (request.method === 'OPTIONS') return new Response(null, { headers: CORS });
    if (request.method !== 'POST') return json(405, { error: 'method not allowed' });

    const body = await request.text();
    if (new TextEncoder().encode(body).length > MAX_BYTES) return json(413, { error: 'payload too large' });

    let pack;
    try { pack = JSON.parse(body); } catch { return json(400, { error: 'invalid json' }); }
    if (!pack || typeof pack !== 'object'
        || typeof pack.names !== 'object' || Array.isArray(pack.names)
        || !Array.isArray(pack.objectives))
      return json(400, { error: 'expected {names:object, objectives:array}' });

    // Defense-in-depth: reject anything that smells identifying.
    const forbidden = ['charname', 'character', 'account', 'accountname', 'address', 'ip'];
    if (forbidden.some(k => JSON.stringify(pack).toLowerCase().includes('"' + k + '"')))
      return json(400, { error: 'identifying field present' });

    const f = filterPack(pack);
    if (f.names.length === 0 && f.objectives.length === 0)
      return json(400, { error: 'nothing valid after filtering' });

    const gh = await fetch(`https://api.github.com/repos/${REPO}/issues`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${env.GITHUB_TOKEN}`,
        'Accept': 'application/vnd.github+json',
        'User-Agent': 'poe2gps-contribute-worker',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(buildIssue(f)),
    });
    if (!gh.ok) return json(502, { error: 'github rejected', status: gh.status });
    return json(200, { ok: true, accepted: { names: f.names.length, objectives: f.objectives.length } });
  },
};

// ── filtering ──
function cleanLabel(raw, max) {
  if (typeof raw !== 'string') return null;
  const s = raw.trim();
  if (s.length < 2 || s.length > max) return null;
  if (!/[a-zA-Z]/.test(s)) return null;            // must contain a letter
  return s;
}
function isProfane(s) {
  const l = ' ' + s.toLowerCase() + ' ';
  return PROFANITY.some(w => new RegExp('\\b' + w + '\\b').test(l));
}
function isGibberish(s) {
  const t = s.replace(/[^a-zA-Z]/g, '');
  if (t.length >= 8 && !/[aeiou]/i.test(t)) return true;                     // long run, no vowels
  const uniq = new Set(t.toLowerCase()).size;
  if (t.length >= 12 && uniq / t.length > 0.85) return true;                 // near-random
  return false;
}
function filterPack(pack) {
  const seen = new Set();
  const names = [];
  for (const [meta, raw] of Object.entries(pack.names)) {
    const nm = cleanLabel(raw, 60);
    if (!nm || isProfane(nm) || isGibberish(nm)) continue;
    const key = String(meta).slice(0, 200);
    if (seen.has(key)) continue;
    seen.add(key);
    names.push([key, nm]);
  }
  const objectives = [];
  for (const o of (pack.objectives || [])) {
    if (!o || typeof o !== 'object') continue;
    const label = cleanLabel(o.label, 60);
    const category = cleanLabel(o.category, 40);
    if (!label && !category) continue;
    if ((label && (isProfane(label) || isGibberish(label))) || (category && isProfane(category))) continue;
    objectives.push({ ...o, label, category });
  }
  return { names, objectives };
}
function buildIssue(f) {
  const cats = [...new Set(f.objectives.map(o => o.category).filter(Boolean))].sort();
  const sample = f.names.slice(0, 10).map(([, v]) => '- `' + v + '`').join('\n');
  const full = JSON.stringify({ names: Object.fromEntries(f.names), objectives: f.objectives }, null, 2).slice(0, MAX_BYTES);
  const body = [
    `**${f.names.length} names, ${f.objectives.length} objectives** (auto-filtered)`,
    cats.length ? 'Categories used: ' + cats.map(c => '`' + c + '`').join(', ') : '',
    f.names.length ? '\nSample names:\n' + sample : '',
    '\n<details><summary>Full pack JSON</summary>\n\n```json\n' + full + '\n```\n</details>',
    '\n*Review, then label `approved` to fold into the next release (or close to reject).*',
  ].filter(Boolean).join('\n');
  return {
    title: `Community pack: ${f.names.length} names, ${f.objectives.length} objectives`,
    body,
    labels: ['community-pack', 'needs-review'],
  };
}

function json(status, obj) {
  return new Response(JSON.stringify(obj), { status, headers: { 'Content-Type': 'application/json', ...CORS } });
}
```

- [ ] **Step 2: Update `cloudflare-worker/README.md` — project-hosted framing + moderation workflow**

Rewrite the README to say: this is the **project's single collector** (the maintainer deploys it once; end-users never touch it). Deploy steps unchanged (`wrangler deploy`, `wrangler secret put GITHUB_TOKEN` with a fine-grained token scoped to **Issues: Read and write** on `luther-rotmg/POE2GPS`). Add the **moderation workflow**: submissions arrive as `community-pack` + `needs-review` issues with a parsed summary; review → label **`approved`** when good (or **close** to reject); each release run `python resources/poe2-data/merge_community.py` to fold approved ones in. Note the auto-filter (profanity/gibberish/identifying/oversized are rejected before filing).

- [ ] **Step 3: Smoke-test the filter functions (node, if available) + verify the repo build/gate unaffected**

Run (best-effort — the Worker isn't compiled into the app):
```bash
node -e "$(sed -n '/^function cleanLabel/,/^}/p;/^function isGibberish/,/^}/p;/^function isProfane/,/^}/p' cloudflare-worker/worker.js); console.assert(cleanLabel('Sealed Vault',60)==='Sealed Vault'); console.assert(cleanLabel('x',60)===null); console.assert(isGibberish('xkcdzqwt')===true); console.assert(isGibberish('Waypoint')===false); console.assert(isProfane('retard')===true); console.assert(isProfane('Sealed Vault')===false); console.log('filter smoke OK');"
```
(If `node` is unavailable, inspect the functions instead and note it.) Then confirm the shipped app is unaffected: `dotnet build POE2Radar.slnx` → 0W/0E; `compliance-gate.ps1` → PASS (worker.js is JS — not scanned; confirm no token literal in worker.js).

- [ ] **Step 4: Commit**

```bash
git add cloudflare-worker/worker.js cloudflare-worker/README.md
git commit -m "feat(worker): v2 — auto-filter (profanity/gibberish/dupes) + parsed review summary + needs-review label"
```

---

## Task 2: Shipped app — baked-in default `ContributeUrl` + tooltip

**Files:** Modify `src/POE2Radar.Overlay/Config/RadarSettings.cs`, `src/POE2Radar.Overlay/Web/DashboardHtml.cs`.

- [ ] **Step 1: The baked-in default constant**

In `RadarSettings.cs`, change the `ContributeUrl` default (currently `= "";`) to a clearly-marked project default. Keep it a placeholder so the build isn't blocked on the deploy — when empty/placeholder, the existing issue-form fallback applies:
```csharp
    // Community Contribute: the project's Cloudflare Worker URL the dashboard uploads your non-identifying
    // pack to (one-click for everyone). SET THIS to the deployed project Worker URL before a release to
    // enable one-click community contribute; left as the placeholder, the Contribute button falls back to
    // the GitHub issue-submission form (no upload).
    public const string DefaultContributeUrl = ""; // ← paste the deployed Worker URL here, e.g. "https://poe2gps-contribute.<you>.workers.dev"
    public string ContributeUrl { get; set; } = DefaultContributeUrl;
```

- [ ] **Step 2: Tooltip copy**

In `DashboardHtml.cs`, update the `#eaContribute` button's `title` to: `"Contribute your discovered names + labels to the community master list (one click). With no Contribute URL set, opens the submission form instead."`

- [ ] **Step 3: Build + gate**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Test: `dotnet test POE2Radar.slnx` (87/87). Gate → PASS.

- [ ] **Step 4: Commit**

```bash
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(contribute): baked-in default ContributeUrl (project Worker) + community tooltip"
```

---

## Task 3: Maintainer merge tooling — `merge_community.py` + docs

**Files:** Create `resources/poe2-data/merge_community.py`, `docs/community-pipeline.md`.

- [ ] **Step 1: Create `resources/poe2-data/merge_community.py`**

```python
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
```

- [ ] **Step 2: Smoke-test the parse/fold logic on a local sample (no live `gh`)**

Create a throwaway sample + verify `extract_pack` + the fold logic without hitting GitHub:
```bash
python -c "
import importlib.util, json
spec = importlib.util.spec_from_file_location('m','resources/poe2-data/merge_community.py'); m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
body = 'noise\n\`\`\`json\n{\"names\":{\"Metadata/X@5\":\"Cool Boss\"},\"objectives\":[{\"category\":\"FreshLabel\"}]}\n\`\`\`\nmore'
p = m.extract_pack(body); assert p and p['names'] and p['objectives'], 'extract failed'
assert m.normalize_key('Metadata/X@5')=='metadata/x', 'normalize failed'
print('merge smoke OK', p)
"
```
(Then a real `python resources/poe2-data/merge_community.py --dry-run` is the maintainer's live check — it needs `gh` auth; note it in the report, don't require it in CI.)

- [ ] **Step 3: Create `docs/community-pipeline.md`**

Document the end-to-end ritual: (1) deploy the Worker once (`cloudflare-worker/`), paste its URL into `RadarSettings.DefaultContributeUrl`, ship a release → end-users get one-click Contribute; (2) submissions arrive as `community-pack` + `needs-review` issues (auto-filtered, with a parsed summary); (3) review → label `approved` (or close to reject); (4) per release, run `python resources/poe2-data/merge_community.py` → fold approved names + novel labels → commit → release. Note the existing `merge_atlas_packs.py` remains for direct file-attached packs.

- [ ] **Step 4: Commit**

```bash
git add resources/poe2-data/merge_community.py docs/community-pipeline.md
git commit -m "feat(tooling): merge_community.py (approved issues -> embedded tables) + pipeline docs"
```

---

## Task 4: Release v0.3.0

**Files:** Modify `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (version), `docs/release-checklist.md`.

- [ ] **Step 1: Bump version** `<Version>0.2.2</Version>` → `<Version>0.3.0</Version>`.

- [ ] **Step 2: Release-checklist item**

Append: with a real `DefaultContributeUrl` (deployed Worker), clicking Contribute files a `community-pack` + `needs-review` issue with a summary; junk (profanity/gibberish/identifying/oversized) is auto-rejected; with the placeholder URL the issue-form fallback opens. `merge_community.py --dry-run` reports approved packs.

- [ ] **Step 3: Full verification sweep**

```bash
dotnet build POE2Radar.slnx -c Release      # 0W/0E
dotnet test  POE2Radar.slnx                  # all pass (87/87)
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1          # PASS
powershell -ExecutionPolicy Bypass -File scripts/scrub-strings.ps1 -SelfTest  # PASSED
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "release: v0.3.0 — community pipeline (project-hosted one-click contribute + moderation)"
```

---

## Self-Review (author checklist — completed)

- **Spec coverage:** Worker v2 auto-filter + summary (T1); baked-in default URL + tooltip (T2); merge_community.py + docs (T3); release (T4). ✓
- **Security:** the Worker token stays `env.GITHUB_TOKEN` (no literal); the smoke-test step confirms no token in worker.js; the merge script is read-only-to-the-game (edits local JSON via `gh` reads). ✓
- **No placeholders:** complete worker.js, merge_community.py, the app edits, and concrete node/python smoke commands. The profanity list is a real (conservative) starter the maintainer extends. ✓
- **No shipped breakage:** the app change is a default value + a tooltip; the existing button/POST/fallback (v0.2.0/v0.2.1) are unchanged; `category` still rides in objectives. 87/87 expected. ✓
- **Compliance:** shipped app read-only; Worker/merge are infra; gate run on the .NET build each code task. ✓
