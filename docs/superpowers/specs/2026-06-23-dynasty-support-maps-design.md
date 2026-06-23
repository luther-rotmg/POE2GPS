# POE2GPS — Dynasty-Support Map Highlighting

**Date:** 2026-06-23
**Status:** Design — approved direction, pending spec review
**Targets release:** v0.2.1 (point release; first community-requested feature)

## Why

A user asked POE2GPS to surface the endgame maps whose **Anomaly bosses drop Lineage/Dynasty Support
Gems** — the way it already surfaces Citadels. Today POE2GPS shows **The Jade Isles** (code
`MapUberBoss_JadeCitadel` — its code contains "Citadel", so `Poe2Atlas.Classify` already tags it
Citadel-gold) but **not** Sealed Vault / Sacred Reservoir / Derelict Mansion (their codes are "Normal"
to the classifier, so nothing highlights them). This feature adds a curated **Dynasty** map set, matched
by code, surfaced with the same ring/arrow/track machinery Citadels use — behind a toggle.

## Global constraints

- **.NET 10, `net10.0-windows`, x64.** `TreatWarningsAsErrors=true`, `Nullable=enable` — 0 warnings, 0 errors.
- **Strictly read-only / GGG-compliant.** Atlas read + a dictionary lookup + drawing. No input emission,
  no process writes, no pricing. `scripts/compliance-gate.ps1` PASS. No identifying data on any wire.
- **Syncability.** New logic in new/owned files (`Core/Game/DynastyMaps.cs` + the embedded JSON) with thin
  hooks into the shared atlas files (`Poe2Atlas`/`RadarApp`/`RenderContext`/`OverlayRenderer`/`RadarSettings`).
- Tests reference `POE2Radar.Core` only.

## Data — the curated table (verified)

A new embedded `src/POE2Radar.Core/Game/dynasty_maps.json`, **keyed by in-game `MapCode`** (NOT the
display name — they differ wildly). Hand-curated from poe2db (Lineage Supports) cross-referenced with the
embedded `world_areas.json` for the code↔name pairing. Small (4 entries today); committed directly,
refreshed per patch when GGG adds dynasty maps. Shape:

```json
{
  "MapVaalVault":            { "name": "Sealed Vault",     "boss": "Maztli / Ytzara",   "gems": ["Atalui's Bloodletting", "Paquate's Pact", "Xibaqua's Rending"] },
  "MapDerelictMansion":      { "name": "Derelict Mansion", "boss": "Varloch / Avelyne", "gems": ["Ailith's Chimes", "Rigwald's Ferocity", "Einhar's Beastrite"] },
  "MapCavernCity":           { "name": "Sacred Reservoir", "boss": "Zahmir",            "gems": ["Varashta's Blessing", "Zarokh's Refrain", "Garukhan's Resolve", "Khatal's Rejuvenation"] },
  "MapUberBoss_JadeCitadel": { "name": "The Jade Isles",   "boss": "Manoki",            "gems": ["Rakiata's Flow", "Tawhoa's Tending", "Kaom's Madness", "Tasalio's Rhythm"] }
}
```

The `name` is the curated display name (the game's, from `world_areas.json`) — preferred over
`Poe2Atlas.Prettify(code)` (which would render `MapVaalVault`→"Vaal Vault", wrong). The `gems`/`boss` feed
the label + the dashboard reference.

**Validation (plan task, low-risk):** the code↔name chain is poe2db-name → `world_areas.json` name → code;
all four display names matched `world_areas.json` exactly, so the chain holds. A one-shot live check (the
user runs `Research --atlas-probe` / F10 over a Sealed Vault node and confirms `code=MapVaalVault`)
de-risks it but is not a blocker.

## Recognition (Core)

`src/POE2Radar.Core/Game/DynastyMaps.cs` — a load-once embedded-resource loader mirroring `ModRanges` /
`StarterWeights`:
- `DynastyMaps.Shared`, `int Count`, `bool TryGet(string mapCode, out DynastyInfo info)`.
- `DynastyInfo(string Name, string Boss, IReadOnlyList<string> Gems)`.
- `Load()` via `Assembly.GetManifestResourceStream` + a `Contains("dynasty_maps")` name lookup; returns an
  empty table (never throws) on any failure.

## Surfacing (Overlay)

**Toggle:** `RadarSettings.HighlightDynastyMaps` (bool, **default false**) + a `highlightDynastyMaps`
`/api/settings` round-trip + a Settings toggle row. When **off**, the feature is inert.

**Atlas integration:** the atlas node walk (where `AtlasNodeLive`/the per-node `AtlasMark` is built —
`Poe2Atlas`/`RadarApp`) checks `DynastyMaps.Shared.TryGet(node.MapCode)`. A hit attaches the dynasty
flag + the `DynastyInfo` (name + gems) to the node's render mark. When `HighlightDynastyMaps` is **on**,
dynasty nodes get the **full Citadel treatment**, reusing the existing tag/kind highlight + arrow + track
machinery (`AtlasHighlightTags`/`AtlasArrowTags`/`AtlasNavTags`/`AtlasHighlightColors` + the
"track every Citadel" auto-track path in `RadarApp`):
- a **distinct ring color** — **purple/violet** `#A55CFF` (stands apart from Citadel-gold / Boss-red),
- a **label** (see below),
- **auto-track + off-screen arrows**, so you can find them anywhere on the atlas.

**Implementation approach (plan decides the exact seam, two clean options):**
(a) treat "Dynasty" as a first-class **atlas kind/tag** matched by the curated code set — so the existing
`AtlasHighlightTags`/arrow/track resolution and the seed-defaults logic handle it (seed "Dynasty" into the
highlight+arrow tag sets + a purple color when `HighlightDynastyMaps` flips on, remove when off); or
(b) a dedicated `HighlightDynastyMaps` branch in the atlas highlight/track resolution that treats
dynasty-flagged nodes as highlighted+arrowed+tracked. The plan picks the one that reuses the most existing
code with the least churn to the shared atlas files; **(a) is preferred** (it's exactly the Citadel pattern).

**Label:** since each map drops a **set** of 3–4 gems, the on-atlas label stays compact — the curated map
**name** + a gem-count suffix, e.g. **`Sealed Vault · 3 dynasty gems`** in the dynasty color. The full gem
list lives in the dashboard reference (below) + the F10 tile-inspect tooltip already prints map details.

**Jade Isles dual-classification (edge case):** `MapUberBoss_JadeCitadel` is BOTH a Citadel (existing
gold) and a dynasty map. It stays Citadel-gold (already tracked/visible); the dynasty pass only adds its
gem label/reference. Pure dynasty maps (the other three) get the purple dynasty treatment. The plan keeps
the Citadel treatment authoritative for color when a node is both, and just augments the label.

## Dashboard

A small **"Dynasty Maps" reference card** (in the Atlas tab, or its own section): the curated table —
map name · boss · the gems it drops — so a player knows what each map yields, plus the
**"Highlight dynasty-support maps"** toggle (mirrors the Settings toggle). Read-only; served from a new
`GET /api/dynasty-maps` (or folded into an existing atlas payload) that returns the curated table (no
identifying data).

## Compliance

100% read-only — an embedded lookup + the existing atlas draw/track path. No input/process-write/pricing.
The toggle defaults off (no behavior change unless opted in). Gate stays green.

## Testing

- `DynastyMaps` loader (Core): the embedded table parses, is nonempty, and `TryGet("MapVaalVault")` returns
  `name="Sealed Vault"` with a nonempty `gems` list.
- The match is a pure dictionary lookup on `MapCode`; covered by the loader test + the manual live check
  (toggle on with the atlas open → Sealed Vault / Sacred Reservoir / Derelict Mansion ring purple + arrow +
  track; toggle off → gone; Jade Isles stays Citadel-gold).

## File map

**New:**
- `resources/poe2-data/dynasty_maps.json` (curated source) → embedded `src/POE2Radar.Core/Game/dynasty_maps.json`.
- `src/POE2Radar.Core/Game/DynastyMaps.cs` + `tests/POE2Radar.Tests/DynastyMapsTests.cs`.

**Thin hooks:**
- `src/POE2Radar.Core/Game/POE2Radar.Core.csproj` — embed `dynasty_maps.json`.
- `src/POE2Radar.Core/Game/Poe2Atlas.cs` and/or `src/POE2Radar.Overlay/RadarApp.cs` — flag dynasty nodes +
  attach gem info to the atlas mark; route into the highlight/arrow/track resolution when the toggle is on.
- `src/POE2Radar.Overlay/Overlay/RenderContext.cs` / `OverlayRenderer.cs` — carry + draw the dynasty
  color + gem-count label (the `AtlasMark` already carries a label/color).
- `src/POE2Radar.Overlay/Config/RadarSettings.cs` — `HighlightDynastyMaps`.
- `src/POE2Radar.Overlay/Web/ApiServer.cs` — `highlightDynastyMaps` round-trip + `GET /api/dynasty-maps`.
- `src/POE2Radar.Overlay/Web/DashboardHtml.cs` — Settings toggle + the Dynasty Maps reference card.

## Out of scope

- **Per-gem filter** ("only highlight maps dropping Rakiata's Flow") — deferred; v1 shows the gem set on
  every dynasty map, which covers the "what does this drop" need.
- **Non-atlas dynasty sources** — the pinnacle/special drops (Abyssal Depths large troves, The Trialmaster,
  Atziri's Temple, Olroth) aren't regular atlas map nodes; they may get a dashboard *mention* but are not
  atlas-highlighted here.
- **Community-contributed dynasty list** — folds into the separate Community-Pipeline project; for now the
  list is hand-curated + committed.

## Success criteria

1. With **Highlight dynasty-support maps** on, the atlas surfaces Sealed Vault, Sacred Reservoir, and
   Derelict Mansion with a purple ring + label (`<name> · N dynasty gems`) + auto-track + off-screen arrows,
   exactly like Citadels; the Jade Isles stays Citadel-gold (already shown). With the toggle off, nothing
   changes.
2. The dashboard "Dynasty Maps" card lists each map · boss · gems.
3. Build 0W/0E, tests pass, compliance gate PASS, no identifying data, anti-detection preserved.
