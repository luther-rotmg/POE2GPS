# POE2GPS — Label Vocabulary v2 (Community Pipeline, sub-project 1 of 2)

**Date:** 2026-06-23
**Status:** Design — approved direction, pending spec review
**Targets release:** v0.2.2

## Why

When classifying an entity/POI in the dashboard, the category picker is a **hardcoded 7-option
`<select>`** (`DashboardHtml.cs` `eaClassRow`: `League / PermanentUpgrade / GemSource / Boss / SideZone /
SideBoss / Other`). So waypoints, shrines, breach, strongboxes, checkpoints, etc. have no label — the
user's complaint. The backend already accepts **any** `category` string (`POST /api/objectives` →
`category: cat`), so the only limitation is the UI. This replaces the thin hardcoded list with a curated
**rich, grouped vocabulary** + a **free-text** fallback.

This is sub-project 1 of the Community Pipeline (the foundation); sub-project 2 (Contribute v2 +
moderation) carries these labels + grows the set from community free-text suggestions.

## Global constraints

- **.NET 10, `net10.0-windows`, x64.** `TreatWarningsAsErrors=true`, `Nullable=enable` — 0 warnings, 0 errors.
- **Read-only / GGG-compliant.** Dashboard/UI + a curated embedded JSON. No input/process-write/pricing.
  `scripts/compliance-gate.ps1` PASS. No identifying data on any wire.
- **Syncability.** New embedded `Web/labels.json` + a small loader + a route + a dashboard change. Thin
  hooks into the shared files.

## Data — the curated vocabulary

A new embedded `src/POE2Radar.Overlay/Web/labels.json` (mirrors `default_campaign_objectives.json` /
`default_watched.json` — embedded in `POE2Radar.Overlay.csproj`, loaded via `GetManifestResourceStream` +
`Contains("labels")`). Grouped for readability; the dashboard flattens it into a datalist. Starter set
(extensible — refreshed per patch / grown by sub-project 2):

```json
{
  "Progression":        ["MainProgression", "Transition", "Checkpoint", "Waypoint", "SideZone", "Optional"],
  "Rewards & Upgrades": ["Reward", "PermanentUpgrade", "GemSource", "Vendor", "Merchant", "Currency"],
  "Bosses":             ["Boss", "SideBoss", "Pinnacle", "Citadel"],
  "League & Seasonal":  ["League", "Seasonal", "Event"],
  "Mechanics":          ["Breach", "Expedition", "Ritual", "Delirium", "Strongbox", "Essence", "Abyss", "Shrine", "Trial", "Sanctum"],
  "Atlas":              ["Tower", "Temple", "Vault", "Unique"],
  "Entities":           ["NPC", "Chest", "Door", "Other"]
}
```

(The existing 7 options are a subset, so no existing classification breaks. `category` stays a free string
in `CampaignObjective`; the Director's priority is the user-set `priority`, independent of the label — so a
new label like "Waypoint" never changes routing behavior.)

## Loader + route

- A small loader (`src/POE2Radar.Overlay/Web/LabelVocabulary.cs`) — `LabelVocabulary.Shared` loads the
  embedded `labels.json` once (resilient: returns an empty/minimal set on failure) and exposes the grouped
  structure + a flat list.
- `GET /api/dynasty-maps`-style read route **`GET /api/labels`** returns the grouped vocabulary (read-only,
  no identifying data).

## Dashboard

- On load (or atlas/entity-atlas tab open), fetch `/api/labels` and build a single
  `<datalist id="labelOptions">` of all label strings (datalists are flat — the browser filters as you
  type; grouping is only for the JSON's organization).
- Change the classify pickers from a hardcoded `<select>` to a **datalist-backed `<input>`** (a combobox):
  - Entity Atlas `eaClassRow` (`DashboardHtml.cs:1322-1330`): `<input class="numin ea-cat" list="labelOptions"
    placeholder="label…">` instead of the `<select>` with the 7 hardcoded `opts`. The `.ea-cat` read at
    line 1307 (`el.querySelector('.ea-cat').value`) already reads `.value`, so it works unchanged.
  - The Director-tab classify picker (the equivalent `cat` input near `DashboardHtml.cs:1251`) gets the same
    datalist-backed input.
- Result: the user picks a curated label from the dropdown suggestions **or** types any custom label; the
  POST is unchanged (any string).

## Out of scope (→ sub-project 2)

- **Suggestion capture / contribution** — recording a user's custom (non-curated) labels and shipping them
  to the project to grow the curated set. That's the Contribute v2 + moderation pipeline.
- **Routing/priority changes** — `priority` is user-set and independent; labels are descriptive + grouping.
- **Per-label colors/icons** — the existing `CATCOL` badge map can be extended later; not required here.

## Compliance / Testing

- 100% read-only (embedded vocabulary + a read route + a UI change). Gate green.
- The vocabulary lives in `POE2Radar.Overlay` (like the other classification defaults), so it's covered by a
  manual check, not a Core unit test: `GET /api/labels` returns the grouped set; the classify pickers show
  the rich list and accept a typed custom label; an existing classification (e.g. "Boss") still works.
  (A release-checklist `curl /api/labels` line documents the smoke check.)

## File map

**New:**
- `src/POE2Radar.Overlay/Web/labels.json` (embedded).
- `src/POE2Radar.Overlay/Web/LabelVocabulary.cs` (loader).

**Thin hooks:**
- `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` — embed `labels.json`.
- `src/POE2Radar.Overlay/Web/ApiServer.cs` — `GET /api/labels`.
- `src/POE2Radar.Overlay/Web/DashboardHtml.cs` — load labels → datalist; classify pickers → datalist input.

## Success criteria

1. The Entity Atlas + Director classify pickers offer the curated rich vocabulary (Waypoint, Shrine, etc.)
   AND accept a typed custom label; the POST/category round-trip is unchanged.
2. `GET /api/labels` serves the grouped vocabulary; no identifying data.
3. Build 0W/0E, gate PASS, existing classifications unaffected.
