# Director v2 — Implementation Spec

**Goal:** Add richer in-zone objective tiers with a visible, live-refreshing zone plan (Part A), a suggest-you-approve POI auto-classifier (Part C), and two bundled QoL fixes (stealth `.exe` cleanup robustness, atlas node centering). Strictly read-only overlay; compliance gate stays green.

The tier order is exact and user-locked: `SeasonalEvent > SideBoss > Bonus > SideZone > Exit`. Tier becomes the primary sort key in `ObjectiveCatalog.Rank`, ahead of the existing numeric `Priority` and `DistanceSq`.

**Scope:** Four independent workstreams in one release — **Part A** (tiers + zone-plan UI, touching Core/Overlay/Web), **Part C** (classifier + dashboard wiring, touching Core/Web), **Fix 1** (stealth hardlink sweep, touching the Overlay entry point), **Fix 2** (atlas centering, touching Overlay render path). There is intentionally no "Part B" — the part letters are inherited from an earlier triage and are kept stable for cross-reference. Fix 1 (stealth/security) and Fix 2 (atlas rendering) share no surface with Parts A/C and could each ship alone; they are bundled here only for release convenience. No new memory writes, no input automation, no injection.

**Build/verify:**
- Build: `dotnet build POE2Radar.slnx -c Release`
- Tests: `dotnet test POE2Radar.slnx`
- Compliance gate: `scripts/compliance-gate.ps1`
- String scrub: `scripts/scrub-strings.ps1 -SelfTest`

---

## Part A — Richer in-zone tiers + sequencing

### What changes and why

`ObjectiveCatalog.Rank` today sorts by Priority descending then DistanceSq ascending (`CampaignObjective.cs:75-78`). There is no notion of tier; a Priority=50 breach objective and a Priority=50 exit portal sort by distance alone. Part A introduces a `Tier` enum that expresses strategic weight above the numeric Priority knob, without breaking the existing JSON catalog or requiring migration.

The dashboard's Director tab shows a static catalog list and a candidate worklist, but does not show the live ranked queue that the sidebar already renders. Part A adds a live "Zone Plan" card in that tab.

**Priority vs. Tier — relationship (read this before editing):** Once `Tier` is the primary sort key, the seeded `Priority` numbers (League 100 > SideBoss 80 > SideZone 60 > Main 10) become **redundant for cross-tier ordering** — the tiers reproduce that exact order on their own. `Priority` survives only as a **within-tier tiebreak** (two objectives sharing the same tier still sort by Priority desc, then distance). The seed JSON (§8) keeps the original priority numbers so existing within-tier behavior is unchanged, but implementers should understand that, post-change, two objectives in *different* tiers can never be reordered by Priority.

### 1. Tier enum

Add in `src/POE2Radar.Core/Campaign/CampaignObjective.cs`, before the `CampaignObjective` record:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObjectiveTier
{
    SeasonalEvent = 4,
    SideBoss      = 3,
    Bonus         = 2,
    SideZone      = 1,
    Exit          = 0,
}
```

Integer values are the sort key (descending). The order is exact and user-locked: `SeasonalEvent > SideBoss > Bonus > SideZone > Exit`.

`System.Text.Json` serializes enum values as integers by default. The `[JsonConverter(typeof(JsonStringEnumConverter))]` attribute makes the JSON round-trip as human-readable strings (`"SideBoss"`, `"Exit"`, etc.) so users can hand-edit `config/campaign_objectives.json`. Ensure `using System.Text.Json.Serialization;` is present in the file.

### 2. Extend CampaignObjective record

File: `src/POE2Radar.Core/Campaign/CampaignObjective.cs`, line 14.

Current primary constructor signature:

```csharp
public sealed record CampaignObjective(
    string Id, string Label, string Category, int Priority, bool Enabled = true,
    List<string>? Metadata = null, List<string>? Categories = null,
    string? Poi = null, string? Rarity = null, List<string>? LandmarkPath = null)
```

Add `ObjectiveTier? Tier = null` as an optional positional parameter after `Priority`:

```csharp
public sealed record CampaignObjective(
    string Id, string Label, string Category, int Priority,
    ObjectiveTier? Tier = null,
    bool Enabled = true,
    List<string>? Metadata = null, List<string>? Categories = null,
    string? Poi = null, string? Rarity = null, List<string>? LandmarkPath = null)
```

`ObjectiveTier?` nullable with default `null` means existing JSON that has no `"tier"` key deserializes to `null` via System.Text.Json's tolerant default behavior (confirmed: `CampaignObjectives.cs:65` uses standard `JsonSerializer.Deserialize` with no strict options; missing properties resolve to C# defaults). No migration of `config/campaign_objectives.json` is required.

### 3. Tier-from-Category default mapping

Category is a plain free string (`CampaignObjective.cs:18`), **not** the `EntityCategory` enum. The four seeded values are `"League"`, `"SideBoss"`, `"SideZone"`, `"MainProgression"`; users can define arbitrary values. There is no existing lookup table. Add a static helper in `CampaignObjective.cs`:

```csharp
public static ObjectiveTier DefaultTierForCategory(string? category) =>
    category switch
    {
        "League"          => ObjectiveTier.SeasonalEvent,
        "SideBoss"        => ObjectiveTier.SideBoss,
        "SideZone"        => ObjectiveTier.SideZone,
        "MainProgression" => ObjectiveTier.Exit,
        _                 => ObjectiveTier.Exit,
    };
```

The unknown/null branch maps to `Exit` (the lowest tier), which is a safe non-hijacking default: an uncategorized objective will not displace a confirmed seasonal event or boss. `"MainProgression"` maps to `Exit` because main-story transitions are route exits, not bonus content.

> **Note on `Category` vocabulary.** `Category` is a free user string. The four arms above match the seeded vocabulary; anything else (including the classifier's `"Transition"`, `"Shrine"`, `"Treasure"`, `"NPC"` suggestions from Part C) falls through to the `_ => Exit` default. There is intentionally **no** dedicated `"Transition"` arm — a `"Transition"`-categorized objective is a route exit and `Exit` is the correct tier for it via the default. Do not add `EntityCategory` enum names here; this switch operates on the free `Category` string only.

**Where the effective-tier fallback lives (important — corrects the v1 draft):** The fallback expression `objective.Tier ?? DefaultTierForCategory(objective.Category)` is applied **exactly once, in `Consider()` (§4)**, at the moment a `RankedObjective` is constructed. It is **NOT** applied in the `Rank` comparator (§5): by the time `Rank` runs, every `RankedObjective.Tier` is already a resolved, non-nullable `ObjectiveTier`. The comparator reads `a.Tier`/`b.Tier` directly. This is the single source of the resolved tier.

### 4. Extend RankedObjective and resolve Tier in Consider()

File: `src/POE2Radar.Core/Campaign/CampaignObjective.cs`, line 29.

Current:

```csharp
public readonly record struct RankedObjective(
    string Id, string Label, string Category, int Priority, float DistanceSq)
```

Add `ObjectiveTier Tier` (non-nullable; always resolved before construction). It is inserted **before** `DistanceSq` in the positional record:

```csharp
public readonly record struct RankedObjective(
    string Id, string Label, string Category, int Priority,
    ObjectiveTier Tier, float DistanceSq)
```

**`RankedObjective` is a positional record, so the constructor call in `Consider()` is positional — it must be rewritten, not patched with an object initializer.** The current `Consider()` (`CampaignObjective.cs:111-114`) is:

```csharp
private static void Consider(Dictionary<string, RankedObjective> best, string id, CampaignObjective o, float distSq)
{
    if (best.TryGetValue(id, out var cur) && cur.Priority >= o.Priority) return;
    best[id] = new RankedObjective(id, o.Label, o.Category, o.Priority, distSq);
}
```

Replace the construction line so the new `Tier` argument is supplied positionally, between `Priority` and `distSq`, with the fallback resolved inline:

```csharp
private static void Consider(Dictionary<string, RankedObjective> best, string id, CampaignObjective o, float distSq)
{
    if (best.TryGetValue(id, out var cur) && cur.Priority >= o.Priority) return;
    best[id] = new RankedObjective(
        id, o.Label, o.Category, o.Priority,
        o.Tier ?? DefaultTierForCategory(o.Category),
        distSq);
}
```

This is the **only** place `DefaultTierForCategory` is called for ranking. (Do not write `Tier = o.Tier ?? ...` as a named initializer — `RankedObjective` is a positional record and there is no parameterless constructor to attach an initializer to.)

### 5. Update Rank sort

File: `src/POE2Radar.Core/Campaign/CampaignObjective.cs`, lines 75-78.

Current sort (a 2-key ternary): Priority descending, then DistanceSq ascending.

New sort: Tier descending (by integer value), then Priority descending, then DistanceSq ascending. The comparator reads the **already-resolved** `RankedObjective.Tier` (non-nullable) set in `Consider()`; there is no fallback expression here:

```csharp
list.Sort((a, b) =>
{
    int tc = ((int)b.Tier).CompareTo((int)a.Tier);   // tier desc
    if (tc != 0) return tc;
    int pc = b.Priority.CompareTo(a.Priority);        // priority desc
    if (pc != 0) return pc;
    return a.DistanceSq.CompareTo(b.DistanceSq);       // nearest asc
});
```

### 6. Propagate Tier to the /state API

File: `src/POE2Radar.Overlay/Web/ApiServer.cs`, line 195.

Current projection:

```csharp
director = (s.Director ?? Array.Empty<RankedObjective>())
    .Select(o => new { id = o.Id, label = o.Label, category = o.Category, priority = o.Priority })
```

Add `tier` (string, via the `JsonStringEnumConverter`-friendly `ToString()`):

```csharp
director = (s.Director ?? Array.Empty<RankedObjective>())
    .Select(o => new { id = o.Id, label = o.Label, category = o.Category,
                       priority = o.Priority, tier = o.Tier.ToString() })
```

### 7. Sanitize Tier on POST /api/objectives

File: `src/POE2Radar.Overlay/Web/ApiServer.cs`, line 1063.

`SanitizeObjective` uses a `o with { ... }` with-expression. Add Tier validation alongside the existing `OneOf` calls. Parse case-insensitively from the incoming string; an unparseable or absent value becomes `null` (meaning "derive from Category at runtime"):

```csharp
Tier = Enum.TryParse<ObjectiveTier>(o.Tier?.ToString(), ignoreCase: true, out var t)
           ? t
           : (ObjectiveTier?)null,
```

Both `POST /api/objectives` (`ApiServer.cs:1002`) and the import path (`ApiServer.cs:1043`) call `SanitizeObjective`, so both are covered.

### 8. Update default seed JSON

File: `src/POE2Radar.Overlay/Web/default_campaign_objectives.json`.

Add `"tier"` to each of the four seeded entries, consistent with the `DefaultTierForCategory` mapping:

```json
[
  { "id": "league",          "label": "League Mechanic", "category": "League",          "priority": 100, "tier": "SeasonalEvent", "enabled": true },
  { "id": "sideboss",        "label": "Side Boss",       "category": "SideBoss",        "priority": 80,  "tier": "SideBoss",      "enabled": true },
  { "id": "sidezone",        "label": "Side Zone",       "category": "SideZone",        "priority": 60,  "tier": "SideZone",      "enabled": true },
  { "id": "mainprogression", "label": "Main Path",       "category": "MainProgression", "priority": 10,  "tier": "Exit",          "enabled": true }
]
```

The explicit `"tier"` per entry makes the `priority` numbers redundant for cross-tier ordering (see "Priority vs. Tier" above) — they remain only as within-tier tiebreaks. This file is loaded only when `config/campaign_objectives.json` is absent or empty (`CampaignObjectives.cs:29, 73-89`). Existing user catalogs are unaffected.

### 9. Dashboard: live Zone Plan card in the Director tab

File: `src/POE2Radar.Overlay/Web/DashboardHtml.cs`.

**Refresh-cadence reality (corrects the v1 draft):** The Director tab's `renderDirector()` (line 1295) is invoked **only** from `loadDirector()` (line 1285), which runs on tab-switch (line 774) and after `postObjectives()` (line 1293). The 1-second `tick()` poll (lines 783-790) calls **only** `renderState()` (line 1687) — it updates the global `state` (including `state.director`) every second but **never** re-renders the Director tab's DOM. Therefore, if the Zone Plan card is rendered solely inside `renderDirector()`, it will show **stale** ranked-queue data until the user re-enters the tab or adds/removes an objective.

To deliver a genuinely live card we add a dedicated `renderDirectorQueue()` and call it from **both** `renderDirector()` (initial paint on tab open) **and** `renderState()` (the 1-second poll). `renderDirectorQueue()` is a no-op when its container is absent, so calling it from `renderState()` while a different tab is active is safe and cheap.

**HTML (line 693 area, `data-view='director'` section):**

Add a new card above `#dirCandidates`:

```html
<div class="card" id="dirQueueCard">
  <h3>Zone Plan <small>live ranked queue for this area</small></h3>
  <div id="dirQueue"></div>
</div>
```

**JS — add a standalone `renderDirectorQueue()` function:**

```js
function renderDirectorQueue(){
  const dq = document.getElementById('dirQueue');
  if (!dq) return;                                  // tab not mounted — safe no-op
  const dir = (state && state.director) || [];
  if (dir.length === 0){
    dq.innerHTML = '<div style="opacity:.5;padding:4px 0">No active objectives in this zone</div>';
    return;
  }
  dq.innerHTML = dir.map((o, i) =>
    `<div class="navrow" style="font-weight:${i===0?'600':'400'};opacity:${i===0?'1':'.75'}">` +
    `${i===0 ? '▶ ' : ''}` +                   // ▶ leading arrow on the active target
    `<span class="navname">${esc(o.label)}</span>` +
    `<span class="navtag">${esc(o.tier||o.category)}</span>` +
    `<span class="navdist">P${o.priority}</span>` +
    `</div>`
  ).join('');
}
```

**JS — wire it into both call sites:**

1. In `renderDirector()` (line 1295), add a call to `renderDirectorQueue();` so the card paints immediately on tab open.
2. In `renderState()` (line 1687) — which `tick()` already calls every second — add a call to `renderDirectorQueue();` so the card live-refreshes on each poll. Because `renderDirectorQueue()` early-returns when `#dirQueue` is absent, it costs nothing when the Director tab is not the active view.

With both call sites wired, the Zone Plan card paints on tab open **and** refreshes every second while the tab is open, tracking `state.director` as the player moves through the zone.

The `.navrow`, `.navname`, `.navtag`, `.navdist` CSS classes are already defined in the dashboard stylesheet (`DashboardHtml.cs:339`). No new CSS is needed.

Nav stays single-active: the first item in the list (`i===0`) is the active target displayed with a leading arrow. The list is display-only. No routing logic is changed.

### 10. Unit tests — Part A

File: extend existing test file in `tests/POE2Radar.Tests/` (same project that contains `RandomNameTests.cs`).

New test class `ObjectiveTierRankTests`. Framework: xUnit, no mocking library, bare `[Fact]` attributes, `Assert.True`/`Assert.Equal`/`Assert.All` only.

Tests to write:

**Tier ordering (desc)**
- Given three `RankedObjective`s with identical Priority and DistanceSq but tiers `Exit`, `SideBoss`, `SeasonalEvent`, `Rank()` sorts them `SeasonalEvent`, `SideBoss`, `Exit`.

**Priority within same tier**
- Given two objectives with tier `SideBoss` and priorities 80 and 40, the priority-80 one ranks first.

**Distance within same tier and priority**
- Given two `SideZone` objectives at same priority, the nearer one (smaller DistanceSq) ranks first.

**Tier dominates Priority across tiers**
- Given a `SideZone` objective with Priority 999 and a `SideBoss` objective with Priority 1, the `SideBoss` one ranks first (confirms Tier is the primary key and Priority cannot cross tiers).

**DefaultTierForCategory mapping**
- Assert `DefaultTierForCategory("League") == ObjectiveTier.SeasonalEvent`.
- Assert `DefaultTierForCategory("SideBoss") == ObjectiveTier.SideBoss`.
- Assert `DefaultTierForCategory("SideZone") == ObjectiveTier.SideZone`.
- Assert `DefaultTierForCategory("MainProgression") == ObjectiveTier.Exit`.
- Assert `DefaultTierForCategory("UnknownCategory") == ObjectiveTier.Exit`.
- Assert `DefaultTierForCategory(null) == ObjectiveTier.Exit`.

**Null Tier resolves in Consider()**
- A `CampaignObjective` with `Tier = null` and `Category = "League"`, ranked through the catalog, produces a `RankedObjective` with `Tier == ObjectiveTier.SeasonalEvent` (confirms the fallback is applied at construction, not in the comparator).

**Explicit Tier overrides Category default**
- A `CampaignObjective` with `Tier = ObjectiveTier.Bonus` and `Category = "League"` produces a `RankedObjective` with `Tier == ObjectiveTier.Bonus` (explicit value wins over the category default).

**Backward-compatible deserialization**
- Deserialize a JSON string `[{"id":"x","label":"X","category":"SideBoss","priority":80}]` (no `"tier"` key) into `List<CampaignObjective>` using the same STJ options as `CampaignObjectives.Load()`. Assert `result[0].Tier == null`.

**Tier round-trips as a string**
- Serialize a `CampaignObjective` with `Tier = ObjectiveTier.SideBoss` and assert the JSON contains `"SideBoss"` (string), not `3` (integer), confirming the `JsonStringEnumConverter` attribute is honored.

---

## Part C — Suggest-you-approve auto-classifier

### What changes and why

Two dashboard worklists — the Director tab's "Needs cataloguing" (`#dirCandidates`, fed by `/api/seen-pois`) and the Entity Atlas tab's "Notable, uncatalogued" (`#eaNotable`, fed by `/api/entity-atlas`) — currently render with blank category and tier inputs (confirmed: `DashboardHtml.cs:1326` and `DashboardHtml.cs:1381` use `placeholder=` with no `value=` attribute). The user must type a category before clicking Accept.

Part C adds a pure `ObjectiveClassifier` in `POE2Radar.Core` that guesses `(ObjectiveTier, label, confidence)` from the available fields. The dashboard pre-fills the guess into the existing inputs. The user can edit or accept with one click. The classifier never routes, never auto-adds, and a wrong guess has zero effect until the user clicks Accept.

### 1. ObjectiveClassifier — pure class contract

File (new): `src/POE2Radar.Core/Campaign/ObjectiveClassifier.cs`

```csharp
namespace POE2Radar.Core.Campaign;

public enum ClassifyConfidence { High, Medium, Low }

public readonly record struct ClassifyResult(
    ObjectiveTier Tier,
    string SuggestedCategory,
    ClassifyConfidence Confidence);

public static class ObjectiveClassifier
{
    public static ClassifyResult? Classify(
        string? metadata,
        string entityCategory,   // EntityCategory.ToString() — Monster/Npc/Chest/Transition/Object/Other
        bool poi,
        string rarity)           // Rarity.ToString() — Normal/Magic/Rare/Unique/NonMonster
    { ... }
}
```

`Classify` returns `null` when no pattern matches (caller shows blank inputs, same as today). It never throws; all inputs are treated as nullable strings internally.

### 2. Pattern table

The classifier applies rules in priority order (first match wins). The table is a static ordered array of `(predicate, result)` pairs evaluated against the four inputs. The implementer encodes it as a private `static readonly` array of value-tuple or small record entries — no external file, no runtime parsing.

**Rule table (ordered, first match wins):**

| # | Condition on metadata (case-insensitive substring) | EntityCategory | Poi | Rarity | Tier | SuggestedCategory | Confidence |
|---|---|---|---|---|---|---|---|
| 1 | contains `"Breach"` | any | any | any | Bonus | League | High |
| 2 | contains `"Ritual"` | any | any | any | Bonus | League | High |
| 3 | contains `"Expedition"` | any | any | any | Bonus | League | High |
| 4 | contains `"Delirium"` | any | any | any | Bonus | League | High |
| 5 | contains `"Abyss"` | any | any | any | Bonus | League | High |
| 6 | contains `"Sanctum"` | any | any | any | Bonus | League | High |
| 7 | contains `"Heist"` | any | any | any | Bonus | League | High |
| 8 | contains `"Affliction"` | any | any | any | Bonus | League | High |
| 9 | contains `"BossArena"` or `"Boss"` | Monster | any | Unique | SideBoss | SideBoss | High |
| 10 | contains `"Boss"` | Monster | true | any | SideBoss | SideBoss | Medium |
| 11 | contains `"Transition"` | Transition | any | any | Exit | Transition | High |
| 12 | contains `"Exit"` | Transition | any | any | Exit | Transition | High |
| 13 | contains `"WorldArea"` or `"AreaTransition"` | Transition | any | any | Exit | Transition | High |
| 14 | any | Transition | any | any | Exit | Transition | Medium |
| 15 | contains `"Shrine"` or `"Altar"` | Object | any | any | Bonus | Shrine | Medium |
| 16 | contains `"Chest"` and contains `"Unique"` in metadata | Chest | any | any | Bonus | Treasure | Medium |
| 17 | any | any | true | Unique | SideBoss | SideBoss | Medium |
| 18 | any | Npc | true | any | Bonus | NPC | Low |
| 19 | any | Monster | any | Unique | SideBoss | SideBoss | Low |

Rules 1-8 fire on league-mechanic metadata fragments regardless of category, because the `Metadata` field carries the entity's full game asset path (e.g. `"Metadata/Monsters/Breach/BreachMonsters/..."`) and league names are stable path segments. Rule 9 combines boss path segment + Unique rarity for High confidence. Rules 11-14 cover all `Transition` category entries with decreasing specificity. Rules 17-19 are fallback heuristics.

For rule 16, "contains `Unique` in metadata" means the metadata string itself contains the literal substring `Unique` (asset path convention), not the rarity field.

> **`SuggestedCategory` ↔ tier defaulting.** The categories this classifier emits (`League`, `SideBoss`, `Transition`, `Shrine`, `Treasure`, `NPC`) are free strings written into the objective's `Category`. The classifier always emits an **explicit** `Tier` alongside, so the objective's stored `Tier` is non-null and `DefaultTierForCategory` (Part A §3) is never consulted for classifier-created objectives. If a user later clears the tier, `DefaultTierForCategory` falls through to `Exit` for the non-seeded category names (`Shrine`/`Treasure`/`NPC`/`Transition`) — acceptable, since `Exit` is the safe non-hijacking default.

### 3. Server-side classification — the chosen site

**Decision: classify server-side. There is no client-side option.** The classifier needs `rarity` as an input, and the existing `/api/seen-pois` JSON projection (`ApiServer.cs:378-384`) does **not** expose a `rarity` field to the client (`SeenPoi.Rarity` exists server-side — `PoiCandidate.cs:14` — but is not projected). Rather than widen the wire contract just to classify on the client, run the classifier inside the API handlers where all four inputs (`Metadata`, `Category`, `Poi`, `Rarity`) are already in scope, and emit only the three `guessed*` result fields. The client reads those fields and never sees raw `rarity`. (This also keeps the rule table in one compiled place rather than duplicated in JS.)

**`/api/seen-pois` handler (`ApiServer.cs:373`):**

In the row projection, add:

```csharp
var guess = ObjectiveClassifier.Classify(p.Metadata, p.Category, p.Poi, p.Rarity);
```

Then include in the anonymous object:

```csharp
guessedTier     = guess?.Tier.ToString(),
guessedCategory = guess?.SuggestedCategory,
guessedConf     = guess?.Confidence.ToString(),
```

**`/api/entity-atlas` handler (`ApiServer.cs:388`):**

Same pattern; `AtlasEntry` carries `Metadata`, `Category`, `Rarity`, `Poi` — all four classifier inputs are available server-side. Add the identical `guess` call and the three `guessed*` fields to that projection.

In both handlers the `guessed*` fields are `null` when `Classify` returns `null`; the client treats `null` as "no guess" and renders blank inputs exactly as today.

### 4. Dashboard wiring — Director tab candRow()

File: `src/POE2Radar.Overlay/Web/DashboardHtml.cs`, `candRow(p)` function (line 1323).

Current category input:

```js
<input class='numin dir-cat' list='labelVocab' placeholder='label…'>
```

Change to pre-fill from classifier output:

```js
`<input class='numin dir-cat' list='labelVocab'
    placeholder='label…'
    value='${esc(p.guessedCategory||"")}'
    title='${p.guessedConf ? "Classifier: "+p.guessedConf : ""}'>`
```

Add a tier `<select>` immediately before the category input. The tier selector uses the five tier names as options, pre-selected to `p.guessedTier` when present, else `Exit`:

```js
const tierOpts = ['SeasonalEvent','SideBoss','Bonus','SideZone','Exit']
    .map(t => `<option value="${t}"${t===(p.guessedTier||'Exit')?'selected':''}>${t}</option>`)
    .join('');
const tierSel = `<select class='numin dir-tier'>${tierOpts}</select>`;
```

Update the `.dir-add` onclick at `DashboardHtml.cs:1305` to read `el.querySelector('.dir-tier').value` and include it in the POST payload:

```js
const tier = el.querySelector('.dir-tier').value || undefined;
postObjectives({add: Object.assign(
    {id:p.signature, label:p.name, category:cat, priority:prio, enabled:true, tier:tier},
    match)});
```

### 5. Dashboard wiring — Entity Atlas eaClassRow()

File: `src/POE2Radar.Overlay/Web/DashboardHtml.cs`, `eaClassRow(a)` function (line 1378).

Apply the same pattern: pre-fill `.ea-cat` input with `a.guessedCategory`, add a `.ea-tier` select pre-selected to `a.guessedTier` (else `Exit`). Update the `.ea-add` onclick at `DashboardHtml.cs:1362` to read `.ea-tier` and include `tier` in the POST body.

### 6. Suggest-only guarantee

The classifier runs at read time, **server-side** in the two API handlers (§3), and its output is delivered only as the `guessed*` fields the client pre-fills into editable inputs. The classifier:

- never calls `postObjectives`;
- never modifies `_objectives`;
- never calls `ObjectiveDirector.Decide`;
- reads no process memory, writes no state, makes no network or disk calls.

A wrong guess is corrected by the user editing the pre-filled input before clicking Accept. If the user clicks Accept with a wrong guess, the result is a user-accepted objective — the user explicitly confirmed it. Nothing in this part adds an automatic action.

### 7. Files to create/modify — Part C

| Action | File | Notes |
|---|---|---|
| Create | `src/POE2Radar.Core/Campaign/ObjectiveClassifier.cs` | Pure static class; no I/O; references only `ObjectiveTier`, `ClassifyConfidence` |
| Modify | `src/POE2Radar.Overlay/Web/ApiServer.cs` line 373 | Add classifier call + `guessed*` fields to `/api/seen-pois` projection |
| Modify | `src/POE2Radar.Overlay/Web/ApiServer.cs` line 388 | Add classifier call + `guessed*` fields to `/api/entity-atlas` projection |
| Modify | `src/POE2Radar.Overlay/Web/DashboardHtml.cs` line 1323 | `candRow(p)` — pre-fill category + add tier select |
| Modify | `src/POE2Radar.Overlay/Web/DashboardHtml.cs` line 1305 | Dir-add onclick — include tier in POST payload |
| Modify | `src/POE2Radar.Overlay/Web/DashboardHtml.cs` line 1378 | `eaClassRow(a)` — pre-fill category + add tier select |
| Modify | `src/POE2Radar.Overlay/Web/DashboardHtml.cs` line 1362 | EA-add onclick — include tier in POST payload |

### 8. Unit tests — Part C

New test class `ObjectiveClassifierTests` in `tests/POE2Radar.Tests/`.

Tests to write (one `[Fact]` per logical property):

- `Classify_BreachMetadata_ReturnsBonusLeagueHigh` — metadata `"Metadata/Monsters/Breach/BreachOpener"`, category `"Monster"`, poi `false`, rarity `"Unique"` → Tier `Bonus`, SuggestedCategory `"League"`, Confidence `High`. (Rule 1 fires before rule 9.)
- `Classify_BossMonsterUnique_ReturnsSideBossHigh` — metadata `"Metadata/Monsters/BossArena/ActBoss"`, category `"Monster"`, poi `false`, rarity `"Unique"` → Tier `SideBoss`, Confidence `High`.
- `Classify_TransitionCategory_ReturnsExitHigh` — metadata `"Metadata/Terrain/Transition/AreaTransition"`, category `"Transition"`, poi `false`, rarity `"Normal"` → Tier `Exit`, SuggestedCategory `"Transition"`, Confidence `High`.
- `Classify_UnknownObject_ReturnsNull` — metadata `"Metadata/Terrain/Rock"`, category `"Object"`, poi `false`, rarity `"Normal"` → `null` (no rule matches).
- `Classify_NullMetadata_DoesNotThrow` — metadata `null`, category `"Other"`, poi `false`, rarity `"Normal"` → `null` (no exception).
- `Classify_UniquePoiMonster_FallbackMedium` — metadata `"Metadata/Monsters/SomeRare/SomeBoss"`, category `"Monster"`, poi `true`, rarity `"Normal"` → Tier `SideBoss`, Confidence `Medium` (rule 10 fires: contains `"Boss"` + poi = true).
- `Classify_AllTransitionCategoryNoMetadataMatch_ExitMedium` — metadata `"Metadata/Terrain/Waypoint"`, category `"Transition"`, poi `false`, rarity `"Normal"` → Tier `Exit`, Confidence `Medium` (rule 14: catch-all Transition).

---

## Fix 1 — Stealth .exe cleanup

### Root cause

`Program.cs` lines 54-67 (the post-run cleanup) run only after `app.Run()` returns gracefully. On window-close, task-kill, or crash the child process exits without reaching line 54, leaving the random-named hardlink beside `Overlay.exe`. Each launch that crashes adds another orphan. Over time these accumulate.

Additionally, the current cleanup identifies hardlink candidates by name heuristic (`name != "Overlay" && f != self`, line 63). If a legitimate unrelated `.exe` lives in the same folder, or if `Environment.ProcessPath` returns null, the predicate is unreliable.

### Fix design

**Primary sweep: at startup, before CreateHardLink.**

In the `!args.Contains("--launched")` branch (line 8), after computing `currentDir` and `currentExe` but before calling `CreateHardLink` (line 18), insert a startup sweep that deletes all prior hardlink orphans. At this moment no stealth copy is running (the launcher is not yet re-launched), so all prior copies are deletable.

**File identity: GetFileInformationByHandle inode equality.**

Two files are hardlinks to the same inode when their `BY_HANDLE_FILE_INFORMATION` structs agree on `dwVolumeSerialNumber`, `nFileIndexHigh`, and `nFileIndexLow`. This is the only correct way to identify hardlinks on NTFS; name patterns are insufficient.

### P/Invoke additions

Add alongside the existing `CreateHardLink` declaration (`Program.cs:71-72`):

```csharp
[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetFileInformationByHandle(
    IntPtr hFile,
    out BY_HANDLE_FILE_INFORMATION lpFileInformation);

[StructLayout(LayoutKind.Sequential)]
struct BY_HANDLE_FILE_INFORMATION
{
    public uint  dwFileAttributes;
    public ComTypes.FILETIME ftCreationTime;
    public ComTypes.FILETIME ftLastAccessTime;
    public ComTypes.FILETIME ftLastWriteTime;
    public uint  dwVolumeSerialNumber;
    public uint  nFileSizeHigh;
    public uint  nFileSizeLow;
    public uint  nNumberOfLinks;
    public uint  nFileIndexHigh;
    public uint  nFileIndexLow;
}
```

`ComTypes` = `System.Runtime.InteropServices.ComTypes`.

### Pure predicate (unit-testable)

Extract file-identity comparison as a pure method with no Win32 dependency in its signature:

```csharp
// In POE2Radar.Core.Stealth or Program.cs (file-local static method)
internal static bool SameFileId(
    uint volA, uint idxHiA, uint idxLoA,
    uint volB, uint idxHiB, uint idxLoB) =>
    volA == volB && idxHiA == idxHiB && idxLoA == idxLoB;
```

The Win32 call is a thin wrapper that extracts the three values and calls `SameFileId`. Keep Win32 calls in `Program.cs`; keep the pure predicate in `POE2Radar.Core` or as a file-local static so tests can reference it without P/Invoke.

### Startup sweep implementation

```csharp
// Before CreateHardLink at Program.cs line 18
static void SweepOldHardlinks(string dir, string baseExe)
{
    if (!GetByHandle(baseExe, out var baseInfo)) return;

    foreach (var f in Directory.GetFiles(dir, "*.exe"))
    {
        // Never delete the canonical base exe
        if (string.Equals(f, baseExe, StringComparison.OrdinalIgnoreCase)) continue;
        // Never delete a file literally named "Overlay.exe"
        if (string.Equals(Path.GetFileNameWithoutExtension(f), "Overlay",
                          StringComparison.OrdinalIgnoreCase)) continue;

        if (!GetByHandle(f, out var fi)) continue;
        if (!SameFileId(baseInfo.dwVolumeSerialNumber, baseInfo.nFileIndexHigh, baseInfo.nFileIndexLow,
                        fi.dwVolumeSerialNumber,  fi.nFileIndexHigh,  fi.nFileIndexLow)) continue;

        try { File.Delete(f); } catch { /* locked or already gone — skip */ }
    }
}

static bool GetByHandle(string path, out BY_HANDLE_FILE_INFORMATION info)
{
    info = default;
    try
    {
        using var fs = File.OpenRead(path);
        return GetFileInformationByHandle(fs.SafeFileHandle.DangerousGetHandle(), out info);
    }
    catch { return false; }
}
```

Call `SweepOldHardlinks(currentDir, currentExe)` immediately before the `CreateHardLink` call at line 18.

**Keep the post-run sweep (lines 55-67) as a best-effort secondary.** It handles the normal graceful-exit case and may catch files the startup sweep could not open (e.g. a copy left by a concurrent prior session still shutting down). Do not remove it. Optionally upgrade it to also use `SameFileId` for consistency, but the name-based guard is acceptable there since it is a secondary path.

### Files to modify — Fix 1

| File | Change |
|---|---|
| `src/POE2Radar.Overlay/Program.cs` | Add `GetFileInformationByHandle` P/Invoke + `BY_HANDLE_FILE_INFORMATION` struct; add `SweepOldHardlinks` and `GetByHandle` helpers; call `SweepOldHardlinks` before line 18 |
| `src/POE2Radar.Core/Stealth/` (or `Program.cs` file-local) | `SameFileId` pure predicate |

### Unit tests — Fix 1

New test class `HardlinkIdentityTests` in `tests/POE2Radar.Tests/`. Follow the `RandomNameTests.cs` style: bare `[Fact]`, no mocking, `Assert.True`/`Assert.False`.

Tests to write:

- `SameFileId_IdenticalValues_ReturnsTrue` — `SameFileId(1u, 2u, 3u, 1u, 2u, 3u)` is `true`.
- `SameFileId_DifferentVolume_ReturnsFalse` — `SameFileId(1u, 2u, 3u, 2u, 2u, 3u)` is `false`.
- `SameFileId_DifferentIndexHigh_ReturnsFalse` — `SameFileId(1u, 2u, 3u, 1u, 9u, 3u)` is `false`.
- `SameFileId_DifferentIndexLow_ReturnsFalse` — `SameFileId(1u, 2u, 3u, 1u, 2u, 9u)` is `false`.
- `SameFileId_AllZeros_ReturnsTrue` — `SameFileId(0u, 0u, 0u, 0u, 0u, 0u)` is `true`. (Validates zero-value behavior; callers guard against `GetByHandle` returning false before using zero-filled structs.)

No integration test for the actual file-system sweep is required; the Win32 path is thin and the pure predicate is fully covered.

---

## Fix 2 — Atlas off-center draw

### Root cause

`Poe2.UiElement.RelativePos` (`+0x118`) is the **top-left corner** of the 40x40 node icon, not its center. This is confirmed by the F10 pick hit-test at `RadarApp.cs:1434`, which measures `±hw` / `±hh` from `n.X, n.Y` using half-extents — only correct if `n.X, n.Y` is the top-left corner. `SizeW` (`+0x288`) and `SizeH` (`+0x28C`) are read into `AtlasNodeLive.W` and `AtlasNodeLive.H` (`Poe2Atlas.cs:403-404`), and nodes are nominally 40x40 with that fallback at `RadarApp.cs:1434`.

Every downstream coordinate stored in `AtlasMark.X/Y` and `AtlasPoint.Bx/By` is the raw top-left. The renderer at `OverlayRenderer.cs:237-249` draws rings, labels, and route markers centered on these projected coordinates, so all drawn elements are shifted approximately +W/2, +H/2 (~+20px at 1:1 zoom, proportionally more at higher zoom) to the down-right of the true node center.

No memory offset changes and no recalibration are needed. The fix is purely in the coordinate stored at construction time.

### Central helper

Add a static method (file-local in `RadarApp.cs` or a small static class in `POE2Radar.Core`):

```csharp
// Returns the center coordinate given a top-left position and dimension.
// Falls back to 40f (nominal node tile size) when size <= 1f (unread/zero).
internal static float AtlasCentre(float pos, float size, float fallback = 40f) =>
    pos + (size > 1f ? size : fallback) * 0.5f;
```

This is the single expression that documents the invariant. It has one trivial unit test (see below).

### Four spots to fix

All four are in `src/POE2Radar.Overlay/RadarApp.cs` and `src/POE2Radar.Overlay/Overlay/RenderContext.cs`.

**Spot 1 — AtlasMark build (RadarApp.cs:2218)**

`AtlasMark` has no `W`/`H` fields today. To allow the render-thread live re-read (Spot 3) to also center, add `float W` and `float H` to the `AtlasMark` record at `RenderContext.cs:51`:

```csharp
public readonly record struct AtlasMark(
    float X, float Y, float W, float H,
    bool Selected, bool HasContent, bool Visited, bool Unlocked,
    int Biome, int IconType,
    string? Label = null, string? Color = null,
    bool Arrow = false, bool Nav = false, nint Element = 0)
```

Then at `RadarApp.cs:2218`, apply centering at construction and store the raw dimensions:

```csharp
marks.Add(new AtlasMark(
    AtlasCentre(n.X, n.W),
    AtlasCentre(n.Y, n.H),
    n.W, n.H,
    isTracked, n.HasContent, n.Visited, n.Unlocked,
    n.Biome, n.IconType, label, color, isArrow, isNav, n.Element));
```

**Spot 2 — gridToPoint AtlasPoint build (RadarApp.cs:2191 and duplicate at 2270)**

`AtlasPoint` has no `W`/`H` today. Add them:

```csharp
private readonly record struct AtlasPoint(nint El, float Bx, float By, float W = 40f, float H = 40f)
```

At `RadarApp.cs:2191`:

```csharp
foreach (var n in nodes)
    gridToPoint[n.Grid] = new AtlasPoint(
        n.Element,
        AtlasCentre(n.X, n.W),
        AtlasCentre(n.Y, n.H),
        n.W, n.H);
```

Apply the identical change at `RadarApp.cs:2270` (the duplicate inside `BuildAtlasRoute`).

**Spot 3 — Render-thread live RelativePos re-read (RadarApp.cs:852-853)**

Current:

```csharp
_atlasMarkFrame.Add(
    m.Element != 0 && _liveRender.TryRelPos(m.Element, out var mx, out var my)
        ? m with { X = mx, Y = my }
        : m);
```

`TryRelPos` returns the top-left. With `W`/`H` now on `AtlasMark`, apply centering:

```csharp
_atlasMarkFrame.Add(
    m.Element != 0 && _liveRender.TryRelPos(m.Element, out var mx, out var my)
        ? m with { X = AtlasCentre(mx, m.W), Y = AtlasCentre(my, m.H) }
        : m);
```

**Spot 4 — Pt() helper for current-location marker (RadarApp.cs:857)**

Current:

```csharp
NumVec2 Pt(AtlasPoint p) =>
    p.El != 0 && _liveRender.TryRelPos(p.El, out var rx, out var ry)
        ? new NumVec2(rx, ry)
        : new NumVec2(p.Bx, p.By);
```

With `W`/`H` on `AtlasPoint`:

```csharp
NumVec2 Pt(AtlasPoint p) =>
    p.El != 0 && _liveRender.TryRelPos(p.El, out var rx, out var ry)
        ? new NumVec2(AtlasCentre(rx, p.W), AtlasCentre(ry, p.H))
        : new NumVec2(p.Bx, p.By);  // Bx/By already centered at construction
```

`p.Bx`/`p.By` are already centered (set at Spot 2), so the baked-coordinate branch needs no change.

### What does NOT change

- `OverlayRenderer.cs:237-249` (`DrawAtlas`) — projects whatever `(X, Y)` it receives. Correct behavior is automatic once inputs are centered. No change.
- `OverlayRenderer.cs:157` (`DrawAtlasRoute Proj()`) — same; projects raw input coordinates. No change.
- `OverlayRenderer.cs:285` (label offset `lx = sx + 24f`) — this is a relative offset from the projected center; once the center is correct the label offset is correct. No change.
- `RadarApp.cs:1434` (F10 pick hit-test) — already uses half-extents correctly for hit-testing; its input `n.X`/`n.Y` comes directly from `AtlasNodeLive` (not from the corrected `AtlasMark`/`AtlasPoint`), so it is unaffected. No change.
- `Poe2.UiElement.RelativePos` offset `+0x118` — no offset change.
- Atlas calibration / `AtlasHomography` — no recalibration.

### Files to modify — Fix 2

| File | Change |
|---|---|
| `src/POE2Radar.Overlay/Overlay/RenderContext.cs` line 51 | Add `float W, float H` to `AtlasMark` |
| `src/POE2Radar.Overlay/RadarApp.cs` line 81 | Add `float W = 40f, float H = 40f` to `AtlasPoint` |
| `src/POE2Radar.Overlay/RadarApp.cs` line 2218 | Apply `AtlasCentre` and pass `n.W, n.H` |
| `src/POE2Radar.Overlay/RadarApp.cs` line 2191 | Apply `AtlasCentre` and pass `n.W, n.H` |
| `src/POE2Radar.Overlay/RadarApp.cs` line 2270 | Same as 2191 (duplicate gridToPoint build) |
| `src/POE2Radar.Overlay/RadarApp.cs` line 852 | Use `AtlasCentre(mx, m.W)` / `AtlasCentre(my, m.H)` |
| `src/POE2Radar.Overlay/RadarApp.cs` line 857 | Use `AtlasCentre(rx, p.W)` / `AtlasCentre(ry, p.H)` in the live-read branch |
| `src/POE2Radar.Overlay/RadarApp.cs` (file-local or Core) | Add `AtlasCentre` static helper |

### Unit test — Fix 2

```csharp
[Fact]
public void AtlasCentre_AddsHalfSize()
{
    Assert.Equal(30f, AtlasCentre(10f, 40f));        // 10 + 40/2 = 30
    Assert.Equal(30f, AtlasCentre(10f, 0f));         // fallback: 10 + 40/2 = 30
    Assert.Equal(30f, AtlasCentre(10f, 1f));         // size <= 1 triggers fallback
    Assert.Equal(30f, AtlasCentre(10f, float.NaN));  // NaN > 1 is false → fallback (verify no throw)
    Assert.Equal(25f, AtlasCentre(5f, 40f));         // 5 + 40/2 = 25
    Assert.Equal(25f, AtlasCentre(5f, 0f));          // fallback path
}
```

---

## Testing

All tests run via `dotnet test POE2Radar.slnx`. No new test projects are introduced. All new test classes live in `tests/POE2Radar.Tests/`.

**New test classes:**

| Class | Tests | Dependency |
|---|---|---|
| `ObjectiveTierRankTests` | Tier ordering; tier-dominates-priority; priority-within-tier; distance tiebreak; `DefaultTierForCategory`; null Tier resolved in Consider(); explicit Tier override; backward-compat deserialization; string round-trip | `POE2Radar.Core` only |
| `ObjectiveClassifierTests` | One fact per rule path; null metadata; unknown entity | `POE2Radar.Core` only |
| `HardlinkIdentityTests` | `SameFileId` pure predicate — 5 facts | `SameFileId` static method; no P/Invoke |
| `AtlasCentreTests` | `AtlasCentre` helper — one fact, 6 assertions | `RadarApp.cs` file-local or `POE2Radar.Core` |

All tests reference `POE2Radar.Core` only (or file-local statics extracted from `Program.cs`). No test references `POE2Radar.Overlay` internals directly.

---

## Compliance

`scripts/compliance-gate.ps1` must remain green. Verify each of these:

- **No new input APIs.** `Fix 1` adds `GetFileInformationByHandle` (a read-only file metadata query) and `File.Delete`. Neither is in the `SendInput`/`PostMessage`/`keybd_event`/`mouse_event` family. The compliance gate's input-API check passes.
- **No process-write APIs.** `Fix 1` adds no `WriteProcessMemory`, `VirtualProtectEx`, or `CreateRemoteThread` call. `OpenProcess` usage is unchanged (read-only, existing). Gate passes.
- **No injection.** No DLL or code injection anywhere in this release. Gate passes.
- **Hardlink cleanup deletes only file-index-verified hardlinks.** `SweepOldHardlinks` opens each candidate with `File.OpenRead` (no write handle), calls `GetFileInformationByHandle`, and deletes only files whose `(dwVolumeSerialNumber, nFileIndexHigh, nFileIndexLow)` triple matches the base exe. It never deletes based on name pattern alone. An unrelated `.exe` in the same directory with a different inode is never touched.
- **Classifier is pure read.** `ObjectiveClassifier.Classify` takes primitive inputs and returns a record. It reads no memory, modifies no state, and makes no network or disk calls. It runs server-side inside the read-only `/api/seen-pois` and `/api/entity-atlas` handlers.
- **`scrub-strings.ps1 -SelfTest`** — no new identifying strings are added. `ObjectiveTier` enum values and classifier pattern strings are game-metadata substrings (asset path fragments), not personal identifiers.

---

## Out of scope — deferred

**Quest-aware cross-zone Director (Director v3 candidate).** The Director roadmap includes detecting in-game quest state (quest memory probe), cross-zone route guidance, and quest-triggered objective sequencing. These require a new in-game quest-memory probe (`POE2Radar.Research`) to locate and validate the quest-completion structure in the PoE2 process — an offset discovery effort that must come before implementation. This work is explicitly deferred and will be tracked separately. No stub or placeholder for quest state is introduced in this release.
