# Catalog Builder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Objective Director's detection & cataloging system — a `ModCatalog`-style persistent seen-POI log that surfaces *uncatalogued* in-game content in a new dashboard "Director" tab, where one click turns it into a Director objective in the existing `CampaignObjectives` catalog.

**Architecture:** Pure filter + classification logic lives in `Core/Campaign` (unit-tested). A `SeenPoiLog` accumulator (mirroring `ModCatalog`) records candidates each world tick. New `ApiServer` endpoints expose the seen-list (each tagged covered/uncatalogued via a new `ObjectiveCatalog.Covers`) and CRUD the catalog. A dashboard "Director" tab drives the loop. All read-only and additive.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`, x64), `.slnx`, xUnit (`tests/POE2Radar.Tests`), `System.Net.HttpListener` dashboard, the existing `ModCatalog`/`ObjectiveCatalog`/`CampaignObjectives`/`EntityNameResolver`/`ZoneGuide`.

## Global Constraints

- **Read-only.** Reads entity/landmark data; writes only local config JSON. No `SendInput`/`WriteProcessMemory`/etc. `scripts/compliance-gate.ps1` must stay PASS.
- **Reuse, no duplication.** Accumulator mirrors `ModCatalog`; classification reuses `ObjectiveCatalog`'s `Compiled` matcher (`Covers`); names reuse `EntityNameResolver.Shared.ResolveOrShorten` + `ZoneGuide.Shared.FriendlyName`; catalog edits go through the existing `CampaignObjectives` store.
- **No identifying data** in any dashboard payload (no character name).
- **Candidate filter (spec §6):** keep entities with `Poi==true`, `Rarity==Unique`, or `Category` ∈ { `Npc`, `Chest`, `Transition`, `Object` }; **skip** everything else (ordinary monsters, FX). Plus all tile landmarks. Dedup by signature.
- `TreatWarningsAsErrors=true`, `Nullable=enable` — warning-clean, null-annotated.
- Types: `Poe2Live.EntityDot`/`.Landmark`/`.EntityCategory`/`.Rarity` in `POE2Radar.Core.Game`; the director catalog types in `POE2Radar.Core.Campaign`.
- **Build:** `dotnet build POE2Radar.slnx`. **Test:** `dotnet test POE2Radar.slnx`. **Gate (local):** `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1` (no `pwsh`).

---

## File structure

**New:**
- `src/POE2Radar.Core/Campaign/PoiCandidate.cs` — `SeenPoi` record + pure `IsCandidate`/`Signature` helpers. **Unit-tested** (Task 1).
- `src/POE2Radar.Overlay/Web/SeenPoiLog.cs` — the accumulator (I/O), mirrors `ModCatalog` (Task 3).
- `tests/POE2Radar.Tests/PoiCandidateTests.cs`, `ObjectiveCatalogCoversTests.cs` (Tasks 1, 2).

**Edited:**
- `src/POE2Radar.Core/Campaign/CampaignObjective.cs` — add `ObjectiveCatalog.Covers(...)` (Task 2).
- `src/POE2Radar.Overlay/Web/CampaignObjectives.cs` — forward `Covers(...)` (Task 2).
- `src/POE2Radar.Overlay/RadarApp.cs` — construct `_seenPoiLog`, `Observe` in `WorldTick`, `Flush` in `Dispose`, pass it + `_campaign` to `ApiServer` (Tasks 3, 4).
- `src/POE2Radar.Overlay/Web/ApiServer.cs` — ctor params + `/api/seen-pois` + `/api/objectives` (Task 4).
- `src/POE2Radar.Overlay/Web/DashboardHtml.cs` — "Director" tab (Task 5).
- `docs/upstream-merge.md`, `docs/release-checklist.md` (Task 6).

---

## Task 1: PoiCandidate — filter + SeenPoi record (Core, TDD)

**Files:**
- Create: `src/POE2Radar.Core/Campaign/PoiCandidate.cs`
- Test: `tests/POE2Radar.Tests/PoiCandidateTests.cs`

**Interfaces:**
- Produces: `POE2Radar.Core.Campaign.SeenPoi` record; `PoiCandidate.IsCandidate(in Poe2Live.EntityDot) : bool`; `PoiCandidate.EntitySignature(in Poe2Live.EntityDot) : string`; `PoiCandidate.LandmarkSignature(Poe2Live.Landmark) : string`.

- [ ] **Step 1: Write the failing tests**

`tests/POE2Radar.Tests/PoiCandidateTests.cs`:

```csharp
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

public class PoiCandidateTests
{
    private static Poe2Live.EntityDot E(Poe2Live.EntityCategory cat, string meta = "m",
        bool poi = false, Poe2Live.Rarity rarity = Poe2Live.Rarity.Normal)
        => new(1, 0, default, default, cat, meta, 1, 1, poi, 0, rarity, false);

    [Fact] public void Keeps_PoiFlaggedEntity()
        => Assert.True(PoiCandidate.IsCandidate(E(Poe2Live.EntityCategory.Monster, poi: true)));

    [Fact] public void Keeps_UniqueMonster()
        => Assert.True(PoiCandidate.IsCandidate(E(Poe2Live.EntityCategory.Monster, rarity: Poe2Live.Rarity.Unique)));

    [Theory]
    [InlineData(Poe2Live.EntityCategory.Npc)]
    [InlineData(Poe2Live.EntityCategory.Chest)]
    [InlineData(Poe2Live.EntityCategory.Transition)]
    [InlineData(Poe2Live.EntityCategory.Object)]
    public void Keeps_NotableCategories(Poe2Live.EntityCategory cat)
        => Assert.True(PoiCandidate.IsCandidate(E(cat)));

    [Fact] public void Skips_OrdinaryMonster()
        => Assert.False(PoiCandidate.IsCandidate(E(Poe2Live.EntityCategory.Monster)));

    [Fact] public void Skips_PlainOther()
        => Assert.False(PoiCandidate.IsCandidate(E(Poe2Live.EntityCategory.Other)));

    [Fact] public void EntitySignature_KeysByMetadata()
    {
        Assert.Equal(PoiCandidate.EntitySignature(E(Poe2Live.EntityCategory.Object, "Metadata/X")),
                     PoiCandidate.EntitySignature(E(Poe2Live.EntityCategory.Object, "Metadata/X")));
        Assert.NotEqual(PoiCandidate.EntitySignature(E(Poe2Live.EntityCategory.Object, "Metadata/X")),
                        PoiCandidate.EntitySignature(E(Poe2Live.EntityCategory.Object, "Metadata/Y")));
    }

    [Fact] public void LandmarkSignature_KeysByPath()
        => Assert.Equal("t:Maps/A.tdt", PoiCandidate.LandmarkSignature(new Poe2Live.Landmark("n", "Maps/A.tdt", default, 1)));
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test POE2Radar.slnx`
Expected: FAIL — `PoiCandidate`/`SeenPoi` don't exist (`CS0246`).

- [ ] **Step 3: Implement `PoiCandidate.cs`**

`src/POE2Radar.Core/Campaign/PoiCandidate.cs`:

```csharp
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign;

/// <summary>One distinct catalog candidate the overlay has encountered. An entity entry has
/// <see cref="Metadata"/> (and <see cref="LandmarkPath"/> null); a tile entry has
/// <see cref="LandmarkPath"/> (and <see cref="Metadata"/> null, <see cref="Category"/> = "Tile").</summary>
public sealed record SeenPoi(
    string Signature,
    string? Metadata,
    string? LandmarkPath,
    string Category,
    bool Poi,
    string Rarity,
    string FriendlyName,
    string FirstZone,
    int Count,
    System.DateTime LastSeenUtc);

/// <summary>Pure rules for what's worth logging as a Director-objective candidate, and how to
/// dedup it. Allocation-light (enum compares); used per-tick by <c>SeenPoiLog</c>.</summary>
public static class PoiCandidate
{
    /// <summary>Keep POI-flagged entities, uniques, and notable categories (NPC/chest/transition/
    /// object). Skip ordinary monsters, players, and plain "Other" (FX/junk).</summary>
    public static bool IsCandidate(in Poe2Live.EntityDot e)
    {
        if (e.Poi) return true;
        if (e.Rarity == Poe2Live.Rarity.Unique) return true;
        return e.Category is Poe2Live.EntityCategory.Npc
            or Poe2Live.EntityCategory.Chest
            or Poe2Live.EntityCategory.Transition
            or Poe2Live.EntityCategory.Object;
    }

    public static string EntitySignature(in Poe2Live.EntityDot e) => "e:" + e.Metadata;
    public static string LandmarkSignature(Poe2Live.Landmark lm) => "t:" + lm.Path;
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test POE2Radar.slnx` → all `PoiCandidateTests` pass; solution builds 0W/0E.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Campaign/PoiCandidate.cs tests/POE2Radar.Tests/PoiCandidateTests.cs
git commit -m "feat(catalog): PoiCandidate filter + SeenPoi record (Core, tested)"
```

---

## Task 2: ObjectiveCatalog.Covers + store forwarder (Core/Overlay, TDD)

**Files:**
- Modify: `src/POE2Radar.Core/Campaign/CampaignObjective.cs` (the `ObjectiveCatalog` class)
- Modify: `src/POE2Radar.Overlay/Web/CampaignObjectives.cs`
- Test: `tests/POE2Radar.Tests/ObjectiveCatalogCoversTests.cs`

**Interfaces:**
- Consumes: `SeenPoi` (Task 1), the existing `ObjectiveCatalog`/`Compiled` matcher, `CampaignObjective`.
- Produces: `ObjectiveCatalog.Covers(in Poe2Live.EntityDot)`, `Covers(string landmarkPath)`, `Covers(SeenPoi)`; `CampaignObjectives.Covers(SeenPoi)`.

- [ ] **Step 1: Write the failing tests**

`tests/POE2Radar.Tests/ObjectiveCatalogCoversTests.cs`:

```csharp
using POE2Radar.Core.Campaign;

public class ObjectiveCatalogCoversTests
{
    private static SeenPoi EntitySeen(string metadata, string category = "Object", bool poi = false, string rarity = "Normal")
        => new("e:" + metadata, metadata, null, category, poi, rarity, "name", "zone", 1, System.DateTime.UnixEpoch);
    private static SeenPoi TileSeen(string path)
        => new("t:" + path, null, path, "Tile", false, "", "name", "zone", 1, System.DateTime.UnixEpoch);

    private static readonly ObjectiveCatalog Cat = new(new[]
    {
        new CampaignObjective("event", "Event", "League", 100, true, Metadata: new() { "RunesOfAldur" }),
        new CampaignObjective("exit", "Exit", "MainProgression", 10, true, Categories: new() { "Transition" }),
        new CampaignObjective("arena", "Arena", "Bosses", 70, true, LandmarkPath: new() { "*Arena*" }),
        new CampaignObjective("off", "Off", "X", 50, false, Metadata: new() { "ShouldNotMatch" }),
    });

    [Fact] public void Covers_MatchingEntityMetadata()
        => Assert.True(Cat.Covers(EntitySeen("Metadata/Events/RunesOfAldur")));

    [Fact] public void Covers_MatchingCategory()
        => Assert.True(Cat.Covers(EntitySeen("Metadata/Transition/Door", category: "Transition")));

    [Fact] public void Covers_MatchingLandmarkPath()
        => Assert.True(Cat.Covers(TileSeen("Maps/Arena/Boss.tdt")));

    [Fact] public void DoesNotCover_Unmatched()
        => Assert.False(Cat.Covers(EntitySeen("Metadata/Misc/Rock")));

    [Fact] public void DoesNotCover_DisabledObjective()
        => Assert.False(Cat.Covers(EntitySeen("Metadata/ShouldNotMatch")));

    [Fact] public void EntityObjective_DoesNotCoverLandmark()
        => Assert.False(Cat.Covers(TileSeen("Metadata/Events/RunesOfAldur"))); // event is entity-only
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test POE2Radar.slnx`
Expected: FAIL — `ObjectiveCatalog.Covers` does not exist.

- [ ] **Step 3: Add `Covers` to `ObjectiveCatalog`** (`CampaignObjective.cs`)

Add these public methods inside the `ObjectiveCatalog` class (e.g. right after `Rank`). `_compiled` is already enabled-only, so no extra enabled check:

```csharp
    /// <summary>True if any enabled objective matches this entity (reuses the compiled matcher).</summary>
    public bool Covers(in Poe2Live.EntityDot e)
    {
        foreach (var c in _compiled)
            if (c.MatchesEntity(in e)) return true;
        return false;
    }

    /// <summary>True if any enabled objective matches this terrain-tile landmark path.</summary>
    public bool Covers(string landmarkPath)
    {
        foreach (var c in _compiled)
            if (c.MatchesLandmark(landmarkPath)) return true;
        return false;
    }

    /// <summary>True if any enabled objective would route to this logged candidate. Tile entries
    /// match by path; entity entries match via a synthetic <see cref="Poe2Live.EntityDot"/> carrying
    /// the catalog-relevant fields (category/metadata/poi/rarity).</summary>
    public bool Covers(SeenPoi p)
    {
        if (p.LandmarkPath is { Length: > 0 } path) return Covers(path);
        var cat = Enum.TryParse<Poe2Live.EntityCategory>(p.Category, ignoreCase: true, out var c)
            ? c : Poe2Live.EntityCategory.Other;
        var rar = Enum.TryParse<Poe2Live.Rarity>(p.Rarity, ignoreCase: true, out var r)
            ? r : Poe2Live.Rarity.NonMonster;
        var e = new Poe2Live.EntityDot(0, 0, default, default, cat, p.Metadata ?? "", 0, 0, p.Poi, 0, rar, false);
        return Covers(in e);
    }
```

- [ ] **Step 4: Forward `Covers` from the store** (`CampaignObjectives.cs`)

Add next to the existing `Rank` forwarder (lock-free `_snapshot` read):

```csharp
    /// <summary>True if any enabled objective already covers this seen candidate (lock-free).</summary>
    public bool Covers(SeenPoi p) => _snapshot.Covers(p);
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test POE2Radar.slnx` → all `ObjectiveCatalogCoversTests` pass; 0W/0E.

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Core/Campaign/CampaignObjective.cs src/POE2Radar.Overlay/Web/CampaignObjectives.cs tests/POE2Radar.Tests/ObjectiveCatalogCoversTests.cs
git commit -m "feat(catalog): ObjectiveCatalog.Covers (coverage classification, tested)"
```

---

## Task 3: SeenPoiLog accumulator + RadarApp wiring

**Files:**
- Create: `src/POE2Radar.Overlay/Web/SeenPoiLog.cs`
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (field ~50; ctor ~488; `WorldTick` ~957; `Dispose` ~2149)

**Interfaces:**
- Consumes: `PoiCandidate`/`SeenPoi` (Task 1), `EntityNameResolver`, `ZoneGuide`.
- Produces: `SeenPoiLog` with `Observe(IReadOnlyList<Poe2Live.EntityDot>, IReadOnlyList<Poe2Live.Landmark>, string areaCode)`, `IReadOnlyList<SeenPoi> All`, `Flush()`.

- [ ] **Step 1: Create `SeenPoiLog.cs`** (mirrors `ModCatalog`: `_gate` + debounced flush)

`src/POE2Radar.Overlay/Web/SeenPoiLog.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Persistent accumulator of distinct "notable" entities/landmarks the overlay has encountered
/// (the catalog-candidate worklist). Mirrors <see cref="ModCatalog"/>: mutations under <c>_gate</c>,
/// a debounced flush to <c>config/seen_pois.json</c>. Read-only w.r.t. the game.
/// </summary>
public sealed class SeenPoiLog
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, SeenPoi> _seen = new(StringComparer.Ordinal); // under _gate
    private readonly Stopwatch _sinceDirty = Stopwatch.StartNew();
    private bool _dirty;
    private const long FlushAfterMs = 4000;
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SeenPoiLog(string filePath) { _filePath = filePath; Load(); }

    /// <summary>Snapshot of all seen candidates (locked; safe from the API thread).</summary>
    public IReadOnlyList<SeenPoi> All { get { lock (_gate) return _seen.Values.ToArray(); } }

    /// <summary>Record this tick's candidates. Resolves a friendly name only on first sighting;
    /// repeat sightings just bump the count. Call from the world thread.</summary>
    public void Observe(IReadOnlyList<Poe2Live.EntityDot> entities, IReadOnlyList<Poe2Live.Landmark> landmarks, string areaCode)
    {
        lock (_gate)
        {
            foreach (var e in entities)
            {
                if (!PoiCandidate.IsCandidate(in e)) continue;
                // Copy metadata to a value local — a foreach var is capture-safe, but resolve the name
                // lazily (thunk runs only on first sighting). Iterating by value is trivially cheap here.
                var meta = e.Metadata;
                Upsert(PoiCandidate.EntitySignature(in e), meta, null, e.Category.ToString(),
                       e.Poi, e.Rarity.ToString(), () => EntityNameResolver.Shared.ResolveOrShorten(meta), areaCode);
            }
            foreach (var lm in landmarks)
                Upsert(PoiCandidate.LandmarkSignature(lm), null, lm.Path, "Tile",
                       false, "", () => lm.CuratedName ?? lm.Name, areaCode);
        }
        MaybeFlush();
    }

    // Call under _gate. friendlyName is a thunk so we only resolve a name on first insert.
    private void Upsert(string sig, string? metadata, string? landmarkPath, string category,
                        bool poi, string rarity, Func<string> friendlyName, string areaCode)
    {
        if (_seen.TryGetValue(sig, out var cur))
        {
            _seen[sig] = cur with { Count = cur.Count + 1, LastSeenUtc = DateTime.UtcNow };
        }
        else
        {
            _seen[sig] = new SeenPoi(sig, metadata, landmarkPath, category, poi, rarity,
                friendlyName(), ZoneGuide.Shared.FriendlyName(areaCode), 1, DateTime.UtcNow);
        }
        if (!_dirty) { _dirty = true; _sinceDirty.Restart(); }
    }

    private void MaybeFlush()
    {
        lock (_gate)
        {
            if (!_dirty || _sinceDirty.ElapsedMilliseconds < FlushAfterMs) return;
            _dirty = false; Save();
        }
    }

    public void Flush() { lock (_gate) { if (_dirty) { _dirty = false; Save(); } } }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<SeenPoi>>(File.ReadAllText(_filePath), Json);
            if (list == null) return;
            foreach (var p in list)
                if (!string.IsNullOrEmpty(p.Signature)) _seen[p.Signature] = p;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Seen-POI log load failed: {ex.Message}"); }
    }

    private void Save() // under _gate
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_seen.Values.ToList(), Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Seen-POI log save failed: {ex.Message}"); }
    }
}
```

(Note: `Upsert`'s `count++` updates `_dirty` every tick a candidate is present, but `MaybeFlush` only writes once per 4 s — same debounce as `ModCatalog`. The `friendlyName` thunk is invoked only on first insert, so `ResolveOrShorten`/`CuratedName` don't run every tick.)

- [ ] **Step 2: Wire into `RadarApp.cs`** — field (near `_modCatalog`, ~line 50):

```csharp
    private readonly SeenPoiLog _seenPoiLog;
```

- [ ] **Step 3: Construct it** (in the ctor, right after the `_modCatalog = new ModCatalog(...)` line ~488):

```csharp
        _seenPoiLog = new SeenPoiLog(Path.Combine(ConfigDir, "seen_pois.json"));
```

- [ ] **Step 4: Observe each world tick** (right after `_modCatalog.Observe(_entities);` ~line 957):

```csharp
        // Accumulate notable POIs/landmarks seen this zone into the catalog-candidate log (debounced).
        _seenPoiLog.Observe(_entities, _landmarks, areaCode);
```

(`_entities`, `_landmarks`, and `areaCode` are all in scope here — confirm `areaCode` is the local already computed in `WorldTick`; if the local has a different name, use it.)

- [ ] **Step 5: Flush on shutdown** (in `Dispose`, next to `_modCatalog.Flush();` ~line 2149):

```csharp
        _seenPoiLog.Flush();
```

- [ ] **Step 6: Build + gate**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. (`SeenPoiLog`'s pure filter is covered by Task 1's tests; the accumulator I/O mirrors the proven `ModCatalog` and is verified by build + the manual smoke in Task 5.)
Run: `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1` → PASS.

- [ ] **Step 7: Commit**

```bash
git add src/POE2Radar.Overlay/Web/SeenPoiLog.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(catalog): SeenPoiLog accumulator wired into the world tick"
```

---

## Task 4: ApiServer endpoints (`/api/seen-pois`, `/api/objectives`) + wiring

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (ctor ~68; fields ~49-54; handler switch; helpers near `SanitizeRule` ~747)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (the `new ApiServer(...)` call ~490-492)

**Interfaces:**
- Consumes: `CampaignObjectives` (with `All`/`Add`/`Remove`/`Covers`), `SeenPoiLog.All`, `SeenPoi`, `CampaignObjective`.
- Produces: `GET /api/seen-pois` → `{ pois: [{ signature, name, category, zone, count, poi, metadata, landmarkPath, covered }] }`; `GET /api/objectives` → `{ objectives: [...] }`; `POST /api/objectives` `{add:{...}}` / `{remove:{id}}`.

- [ ] **Step 1: Add ctor params + fields** (`ApiServer.cs`)

Add two parameters to the `ApiServer` ctor right after `Func<IReadOnlyList<string>> knownModsProvider,` (line 78):

```csharp
        CampaignObjectives objectives,
        Func<IReadOnlyList<POE2Radar.Core.Campaign.SeenPoi>> seenPoisProvider,
```

Add fields (next to `_knownMods` ~54) and assign them in the ctor body (next to `_knownMods = knownModsProvider;` ~98):

```csharp
    private readonly CampaignObjectives _objectives;
    private readonly Func<IReadOnlyList<POE2Radar.Core.Campaign.SeenPoi>> _seenPois;
```
```csharp
        _objectives = objectives;
        _seenPois = seenPoisProvider;
```

(`using POE2Radar.Core.Campaign;` at the top lets you drop the namespace qualifier if you prefer.)

- [ ] **Step 2: Add the read endpoint `/api/seen-pois`** (a case in the `Handle` switch, near `/api/mods` ~327)

```csharp
            case "/api/seen-pois":
                // Distinct notable entities/landmarks seen this session, each tagged whether the
                // Director catalog already covers it (uncatalogued ones are the worklist). Read-only.
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    pois = _seenPois().Select(p => new
                    {
                        signature = p.Signature, name = p.FriendlyName, category = p.Category,
                        zone = p.FirstZone, count = p.Count, poi = p.Poi,
                        metadata = p.Metadata, landmarkPath = p.LandmarkPath,
                        covered = _objectives.Covers(p),
                    }),
                }, Json));
                break;
```

- [ ] **Step 3: Add the GET/POST endpoint `/api/objectives`** (model: `/api/display-rules` ~422)

```csharp
            case "/api/objectives":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(new { objectives = _objectives.All }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                    ApplyObjectives(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, objectives = _objectives.All }, Json));
                }
                else { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); }
                break;
            }
```

- [ ] **Step 4: Add `ApplyObjectives` + `SanitizeObjective`** (near `SanitizeRule` ~747; model on `ApplyDisplayRules`/`OneOf`)

```csharp
    /// <summary>Apply a Director-tab command: {"add":{objective}} upserts; {"remove":{"id":...}} deletes.
    /// Edits the local catalog only — never the game.</summary>
    private void ApplyObjectives(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("remove", out var rem) && rem.TryGetProperty("id", out var idEl)
            && idEl.GetString() is { Length: > 0 } id)
        {
            _objectives.Remove(id);
            return;
        }
        if (root.TryGetProperty("add", out var add) && add.ValueKind == JsonValueKind.Object)
        {
            var o = JsonSerializer.Deserialize<POE2Radar.Core.Campaign.CampaignObjective>(add.GetRawText(), Json);
            if (o != null) _objectives.Add(SanitizeObjective(o));
        }
    }

    /// <summary>Clamp/validate a posted objective: non-blank Id/Label/Category, priority 0..1000,
    /// trimmed match lists (max 32 terms), valid Rarity/Poi (else null = "any").</summary>
    private static POE2Radar.Core.Campaign.CampaignObjective SanitizeObjective(POE2Radar.Core.Campaign.CampaignObjective o)
    {
        static List<string>? CleanTerms(List<string>? terms) =>
            terms is { Count: > 0 }
                ? terms.Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Take(32).ToList()
                : null;

        var id = (o.Id ?? "").Trim();
        return o with
        {
            Id = id.Length > 80 ? id[..80] : id,
            Label = (o.Label ?? "").Trim() is { Length: > 0 } lbl ? (lbl.Length > 60 ? lbl[..60] : lbl) : id,
            Category = string.IsNullOrWhiteSpace(o.Category) ? "Other" : o.Category.Trim(),
            Priority = Math.Clamp(o.Priority, 0, 1000),
            Metadata = CleanTerms(o.Metadata),
            Categories = CleanTerms(o.Categories),
            LandmarkPath = CleanTerms(o.LandmarkPath),
            Rarity = OneOf(o.Rarity, "Normal", "Magic", "Rare", "Unique"),
            Poi = OneOf(o.Poi, "Yes", "No"),
        };
    }
```

(If `ApplyObjectives` produces an objective with a blank `Id`, `CampaignObjectives.Add` already ignores it — but the dashboard always sends an Id.)

- [ ] **Step 5: Pass the new args in the `new ApiServer(...)` call** (`RadarApp.cs` ~490-492)

Insert `_campaign, () => _seenPoiLog.All,` right after the `() => _modCatalog.All,` argument:

```csharp
        _api = new ApiServer(() => _state, _settings, GetNavSelection, ToggleNavTarget, ClearNavSelection,
                             _hidden, _displayRules, _landmarkStore, CurrentTilePaths, () => _modCatalog.All,
                             _campaign, () => _seenPoiLog.All, AtlasJson, SetAtlasSelection,
                             SetAtlasHighlight, VersionJson, _settings.ApiPort);
```

- [ ] **Step 6: Build + gate**

Run: `dotnet build POE2Radar.slnx` → 0W/0E (positional ctor args must line up — the two new params sit between `knownModsProvider` and `atlasProvider`, matching the call). Run the gate → PASS.

- [ ] **Step 7: Commit**

```bash
git add src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(catalog): /api/seen-pois + /api/objectives endpoints"
```

---

## Task 5: Dashboard "Director" tab

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (tabs row ~404; a new view section; the tab-switch JS ~624-633; a new JS block)

**Interfaces:**
- Consumes: `GET /api/seen-pois`, `GET/POST /api/objectives` (Task 4).

- [ ] **Step 1: Add the tab button** (in the `.tabs` row ~404-409, after the Settings button)

```html
            <button class="tab" data-tab="director">Director</button>
```

- [ ] **Step 2: Add the view section** (after the settings `<section>`; before the `.tabs`-closing area — anywhere among the sibling `<section class="view">` blocks)

```html
        <section class="view" data-view="director" hidden>
          <div class="card">
            <h3>Needs cataloguing <small>notable POIs/landmarks you've seen that no objective covers yet</small></h3>
            <div class="row"><input id="dirSearch" class="numin" type="text" placeholder="filter…" style="width:200px"></div>
            <div id="dirCandidates" class="znotes" style="display:block"></div>
          </div>
          <div class="card">
            <h3>Catalog <small>active Director objectives (priority order)</small></h3>
            <div id="dirCatalog" class="znotes" style="display:block"></div>
          </div>
        </section>
```

- [ ] **Step 3: Hook the tab into the switch JS** (the closure at ~624-633 — add one line alongside the other `if(activeTab===…)` calls)

```javascript
    if(activeTab==='director') loadDirector();
```

- [ ] **Step 4: Add the Director JS** (place near `loadLandmarks`/`postLandmarks`, modeled on them)

```javascript
/* ── director tab: catalog builder (seen-POIs → objectives) ── */
let dirSeen=[], dirObjs=[], dirQ='';
async function loadDirector(){
  try{ const s=await getJSON('/api/seen-pois'); dirSeen=s.pois||[]; }catch(e){ dirSeen=[]; }
  try{ const o=await getJSON('/api/objectives'); dirObjs=o.objectives||[]; }catch(e){ dirObjs=[]; }
  renderDirector();
}
async function postObjectives(body){
  try{ const r=await fetch('/api/objectives',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
       const j=await r.json(); if(j&&j.objectives) dirObjs=j.objectives; }catch(e){}
  loadDirector();
}
function renderDirector(){
  const cand=$('#dirCandidates');
  if(cand){
    const rows=dirSeen.filter(p=>!p.covered)
      .filter(p=>!dirQ || ((p.name+' '+(p.metadata||p.landmarkPath||'')+' '+p.category).toLowerCase().includes(dirQ)))
      .sort((a,b)=>b.count-a.count);
    cand.innerHTML = rows.length ? rows.map(candRow).join('')
      : '<div class="row"><div class="rl hint-row">Nothing uncatalogued in view — explore more, or clear the filter.</div></div>';
    rows.forEach(p=>{
      const el=cand.querySelector('[data-sig="'+cssEsc(p.signature)+'"]'); if(!el) return;
      el.querySelector('.dir-add').onclick=()=>{
        const cat=el.querySelector('.dir-cat').value;
        const prio=parseInt(el.querySelector('.dir-prio').value,10)||50;
        const match = p.landmarkPath ? {landmarkPath:[p.landmarkPath]} : {metadata:[p.metadata]};
        postObjectives({add:Object.assign({id:p.signature,label:p.name,category:cat,priority:prio,enabled:true},match)});
      };
    });
  }
  const cat=$('#dirCatalog');
  if(cat){
    const objs=dirObjs.slice().sort((a,b)=>(b.priority||0)-(a.priority||0));
    cat.innerHTML = objs.length ? objs.map(o=>
      '<div class="row" data-id="'+esc(o.id)+'"><div class="rl">'+esc(o.label)+'<small>'+esc(o.category)+' · prio '+(o.priority||0)+(o.enabled?'':' · off')+'</small></div>'
      + '<button class="delbtn dir-del">Remove</button></div>').join('')
      : '<div class="row"><div class="rl hint-row">No objectives yet.</div></div>';
    objs.forEach(o=>{ const el=cat.querySelector('[data-id="'+cssEsc(o.id)+'"]'); if(el) el.querySelector('.dir-del').onclick=()=>postObjectives({remove:{id:o.id}}); });
  }
}
function candRow(p){
  const opts=['League','SideBoss','SideZone','MainProgression','Bosses','Other']
    .map(c=>'<option value="'+c+'">'+c+'</option>').join('');
  return '<div class="row" data-sig="'+esc(p.signature)+'">'
    + '<div class="rl">'+esc(p.name)+'<small>'+esc(p.category)+' · '+esc(p.zone||'?')+' · ×'+p.count+'</small></div>'
    + '<select class="numin selin dir-cat">'+opts+'</select>'
    + '<input class="numin dir-prio" type="number" min="0" max="1000" value="50" style="width:64px">'
    + '<button class="delbtn dir-add">Add</button></div>';
}
function cssEsc(s){ return (s||'').replace(/["\\\]]/g,'\\$&'); }
$('#dirSearch')?.addEventListener('input',e=>{ dirQ=e.target.value.toLowerCase(); renderDirector(); });
```

(`esc`, `$`, `$$`, `getJSON` already exist in this file — reuse them. `cssEsc` is added for safe attribute selectors since signatures contain `/` and `:`.)

- [ ] **Step 5: Build + gate + test**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Gate → PASS. `dotnet test POE2Radar.slnx` → all pass (the JS is a string literal; no test change).

- [ ] **Step 6: Manual smoke (no game needed for the UI)**

Start the overlay, open `http://localhost:7777`, click the **Director** tab; confirm it loads (empty lists are fine without a game). Full behavior — candidates appearing, "Add" creating an objective, "Remove" deleting — is the release-checklist item.

- [ ] **Step 7: Commit**

```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(catalog): dashboard Director tab (candidates → objectives)"
```

---

## Task 6: Docs — upstream-merge + release checklist

**Files:**
- Modify: `docs/upstream-merge.md`, `docs/release-checklist.md`

- [ ] **Step 1: `docs/upstream-merge.md`** — under "What POE2GPS adds on top of Sikaka", add:

```markdown
- **Catalog Builder** (`Core/Campaign/PoiCandidate.cs`, `ObjectiveCatalog.Covers`, `Overlay/Web/SeenPoiLog.cs`).
  Hooks: the `_seenPoiLog` field + ctor construction; `_seenPoiLog.Observe(...)` in `WorldTick` (next to
  `_modCatalog.Observe`); `_seenPoiLog.Flush()` in `Dispose`; the two new `ApiServer` ctor params
  (`CampaignObjectives objectives`, `Func<…SeenPoi> seenPoisProvider`) + the `/api/seen-pois` and
  `/api/objectives` cases + `ApplyObjectives`/`SanitizeObjective`; the `new ApiServer(...)` call args
  `_campaign, () => _seenPoiLog.All`; the Dashboard "Director" tab (button + `data-view="director"` section + `loadDirector`).
```

- [ ] **Step 2: `docs/release-checklist.md`** — under the manual live-game section, add:

```markdown
- [ ] **Catalog Builder:** in a zone, open the dashboard → Director tab; confirm uncatalogued POIs/
      landmarks appear under "Needs cataloguing" with friendly names; "Add" (pick category + priority)
      makes it disappear from the list and show under "Catalog" + drive the Director; "Remove" deletes it.
      Confirm `/api/seen-pois` carries no character name.
```

- [ ] **Step 3: Commit**

```bash
git add docs/upstream-merge.md docs/release-checklist.md
git commit -m "docs(catalog): upstream-merge hooks + release-checklist item"
```

---

## Self-Review

**Spec coverage:**
- §4 data flow (Observe → seen-log → /api/seen-pois classified → add → catalog) → Tasks 3/4/5. ✓
- §5 components (PoiCandidate, SeenPoiLog, Covers, store forward, endpoints, dashboard tab, RadarApp hooks) → Tasks 1-5. ✓
- §6 filter (POI/Unique/Npc/Chest/Transition/Object; skip rest; dedup) → Task 1 `IsCandidate`/`Signature`. ✓
- §7 classification (synthetic EntityDot for entity entries; path for tiles; off hot path) → Task 2 `Covers(SeenPoi)`, called in `/api/seen-pois` (Task 4). ✓
- §8 friendly names (`ResolveOrShorten` / `CuratedName` / `ZoneGuide.FriendlyName`) → Task 3 `Upsert`. ✓
- §9 compliance/sync (read-only, gate, hooks doc) → gate in 3/4/5, Task 6. ✓
- §10 testing (PoiCandidate + Covers unit tests; manual UI) → Tasks 1, 2, 5/6. ✓
- §12 deferred (no overlay badge, no auto-categorization) → not built. ✓

**Placeholder scan:** none — full code for new files, exact edits with verbatim anchors, no "similar to".

**Type consistency:** `SeenPoi` (Task 1) used identically in `Covers(SeenPoi)` (Task 2), `SeenPoiLog` (Task 3), and the `/api/seen-pois` projection (Task 4). `CampaignObjectives.Covers`/`All`/`Add`/`Remove` (Task 2 + existing) match the endpoint calls (Task 4). The `ApiServer` ctor's two new params (Task 4 Step 1) match the call-site args (Task 4 Step 5). The dashboard fetches `{pois}`/`{objectives}` matching the endpoint shapes.

---

## Notes for the implementer

- **Line numbers are from plan-writing time** — anchor edits on the verbatim code shown / the named neighbors (`_modCatalog`, `/api/mods`, `/api/display-rules`, `SanitizeRule`, the `.tabs` row).
- **Never add an input or memory-write API.** This whole feature reads entity data + writes local JSON.
- **`SeenPoiLog.Observe` iterates by `foreach`** (value copy) so the friendly-name lambda can capture a value local — a `ref readonly` local (e.g. via `CollectionsMarshal.AsSpan`) **cannot** be captured by a lambda (CS8175). The per-element struct copy on the 30 Hz world thread is negligible (this is how `ModCatalog.Observe` iterates too).
- **Live behavior isn't CI-testable** — the pure filter + coverage classification are unit-tested; the accumulator/endpoints/tab are build- + gate- + manual-verified per `docs/release-checklist.md`.
