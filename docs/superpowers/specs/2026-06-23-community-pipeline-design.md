# POE2GPS — Community Pipeline: Contribute v2 + Moderation (sub-project 2 of 2)

**Date:** 2026-06-23
**Status:** Design — approved direction, pending spec review
**Targets release:** v0.3.0

## Why

The v0.2.0 Contribute model made **every user** deploy their own Worker — the hoop the user wanted gone.
This re-architects it: **the project hosts ONE collector** (the user deploys it once, holds the scoped
token), the app **ships that Worker's URL baked in** as the default, so **end-users click once with zero
setup** and their finds land in **our master list**. The Worker **auto-filters junk** and files clean
submissions as reviewable GitHub Issues; a **human review gate** (the maintainer approves) precedes the
merge into the shipped tables. Builds on sub-project 1 (the rich label vocabulary): users' custom labels
ride in the pack and grow the curated set on approval.

## Global constraints

- **.NET 10, x64.** `TreatWarningsAsErrors=true`, `Nullable=enable` — 0W/0E for the shipped app.
- **Strictly read-only / GGG-compliant.** The app change is a settings default + the existing outbound
  POST. No input/process-write/pricing. Gate PASS. **No identifying data** ever leaves the client (the
  pack is the verified `{names, objectives}` only).
- **Privacy / opt-in.** Contributing is user-initiated (button click) + the existing one-time confirm;
  the payload is non-identifying. The Worker URL is the project's (baked in), but nothing is sent until
  the user clicks.
- The Worker + merge tooling are **infra/dev tooling, not shipped overlay code** (outside the compliance
  gate's .NET scan).

## Architecture / data flow

```
end-user labels a find (v0.2.2 vocabulary) → clicks Contribute
  → app POSTs the {names, objectives} pack to the baked-in project Worker URL (server-side via /api/contribute)
  → Worker AUTO-FILTERS (shape, size, identifying, profanity, gibberish, dedup)
  → files a labeled GitHub Issue (community-pack + needs-review) with a PARSED SUMMARY
  → MAINTAINER reviews the issue → labels it `approved`
  → maintainer runs the merge script: pulls approved issues → folds names → entity_names.json,
    novel labels → labels.json (curated growth) → commits → next release ships to everyone
```

## Components

### 1. The Worker v2 (`cloudflare-worker/worker.js` — upgrade)

Keep the v0.2.0 base (CORS, 256 KB cap, shape check, identifying-field reject, token from `env.GITHUB_TOKEN`,
issue filing). **Add the auto-filter intelligence + a review-friendly summary:**
- **Profanity filter:** a small embedded word list; reject a submission whose names/labels contain a slur/
  profanity (case-insensitive, word-boundary). (Conservative list — avoid false positives on legit terms.)
- **Gibberish / sanity per entry:** reject names that are empty, `< 2` or `> 60` chars, all-symbols / no
  letters, or pure random-char runs (e.g. a simple heuristic: a long token with no vowels / a very high
  unique-char ratio). Reject objectives whose `label`/`category` are empty/garbage.
- **Dedup within submission:** collapse exact-duplicate name entries.
- **Per-field length caps** (name ≤ 60, label ≤ 60, category ≤ 40) — drop offenders, keep the rest.
- **Parsed summary** in the issue body (so review is one glance): `N names, M objectives`; a sample of
  names; the distinct categories used; and **NEW labels** (categories not in the shipped `labels.json` —
  the Worker holds a copy of the curated list, or the merge step flags them) called out for promotion.
- **Labels:** `community-pack` + `needs-review`. Rate-limited per IP (Cloudflare).
- A submission that's entirely junk after filtering → `400` (don't file an empty issue).

Update `cloudflare-worker/README.md`: the project-hosted-collector framing + the **moderation workflow**
(review the `needs-review` issues → label `approved` when good → close junk → run the merge script).

### 2. The shipped app (minimal)

- **`RadarSettings.ContributeUrl` default** → a single, clearly-marked baked-in constant for the project
  Worker URL (a placeholder like `https://poe2gps-contribute.<deploy>.workers.dev`, with a code comment
  "set this to the deployed project Worker URL to enable one-click community contribute"). Until it's a
  real URL, the existing fallback (the GitHub issue-template) applies (v0.2.0 behavior) — so the build
  isn't blocked on the deploy. When the user fills it + ships, end-users get one-click.
- The existing Contribute button + `POST /api/contribute` + one-time confirm + green toast (v0.2.0/v0.2.1)
  are unchanged; the pack is the unchanged non-identifying `{names, objectives}` (custom labels ride in
  the objectives' `category`). A small dashboard copy tweak: the button tooltip → "Contribute your finds
  to the community master list."

### 3. The merge tooling (maintainer-run, dev-only)

A new `resources/poe2-data/merge_community.py` (or extend `merge_atlas_packs.py`) — Python, like the
existing merge script:
- Pull **approved** issues via `gh api` (filter: label `community-pack` AND `approved`); parse the JSON
  pack out of each issue body.
- Fold `names` into `src/POE2Radar.Core/Game/entity_names.json` (reuse the existing `merge_atlas_packs`
  logic — curated wins, normalize keys, sorted output).
- **NEW:** fold **novel labels** (objective categories not already in `Overlay/Web/labels.json`) into
  `labels.json` (under a curated group or an "Community" group), so the vocabulary grows from approved
  community use.
- Deterministic, sorted output; `--dry-run` like the existing script.
- Document the per-release maintainer ritual in `docs/community-pipeline.md` (deploy once → end-users
  contribute → review issues → label approved → run the merge → commit → release).

## Testing

- The shipped-app change is a settings default + a tooltip — covered by the existing settings round-trip
  + a manual smoke (with a real Worker URL, one click files an issue; with the placeholder, the issue-form
  fallback opens). No new .NET unit test needed.
- The Worker (JS) and merge script (Python) are infra — verified by **manual smoke**: deploy the Worker,
  submit a clean pack (→ filed issue with summary) and a junk pack (→ rejected); approve an issue + run the
  merge (→ names + novel labels folded). The release checklist documents this.
- If a small pure helper is worth testing (e.g. the gibberish/profanity check), the Worker can keep it as
  a pure JS function with inline self-tests, but no .NET test project covers JS.

## File map

**New:** `resources/poe2-data/merge_community.py`; `docs/community-pipeline.md`.
**Modified:** `cloudflare-worker/worker.js` (auto-filter + summary), `cloudflare-worker/README.md` (workflow),
`src/POE2Radar.Overlay/Config/RadarSettings.cs` (`ContributeUrl` default constant),
`src/POE2Radar.Overlay/Web/DashboardHtml.cs` (button tooltip copy), `POE2Radar.Overlay.csproj` (version).

## Out of scope

- **Auto-merge / reputation / voting** — the human review gate is the moderator; no auto-accept (the user
  chose the review gate).
- **A submissions admin UI** — review happens in the GitHub Issues UI (no custom dashboard).
- **Non-{names,objectives} payloads** (seen-pois, full census) — the pack stays the curated export.

## Success criteria

1. With the project Worker URL baked in, an end-user clicks Contribute once (no setup) → the pack is
   filed as a `community-pack` + `needs-review` GitHub Issue with a parsed summary; obvious junk (profanity/
   gibberish/identifying/oversized) is auto-rejected, never filed.
2. The maintainer labels an issue `approved` and runs `merge_community.py` → the approved names land in
   `entity_names.json` and novel labels in `labels.json`; the ritual is documented.
3. The shipped app stays 100% read-only / GGG-compliant (no identifying data leaves the client; gate PASS);
   with the placeholder URL the issue-form fallback still works.
