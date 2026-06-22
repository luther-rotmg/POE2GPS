# Entity Atlas Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A capture → name/classify → publish loop that grows comprehensive PoE2 entity coverage for the GPS — every entity gets a friendly name (radar legibility) and the notable ones become Director objectives — community-growable, fully read-only.

**Architecture:** A hardened per-tick accumulator (`EntityAtlasLog`, mirroring the post-v0.1.2 `SeenPoiLog`) logs the full distinct-entity census to `config/entity_atlas.json`. A dashboard "Entity Atlas" tab surfaces unnamed and notable-uncatalogued entries. Naming writes a runtime override (`config/entity_names_user.json`) that a new override layer in `EntityNameResolver` consults *before* the embedded table — so names go live with no rebuild. Classifying reuses the Director's `CampaignObjectives`. Export/import shares packs.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`, x64), `System.Text.Json`, `System.Net.HttpListener`, xUnit. Solution `POE2Radar.slnx`.

## Global Constraints

- **Read-only.** No input-emission or process-write APIs. `scripts/compliance-gate.ps1` must print `PASS`.
- **`TreatWarningsAsErrors=true`, `Nullable=enable`.** Every build must be **0 Warning(s), 0 Error(s)**.
- **No identifying data** (no character name) in any dashboard payload.
- **Reuse, don't duplicate:** the census accumulator mirrors the hardened `SeenPoiLog`; "notable" reuses `PoiCandidate.IsCandidate`; "covered" reuses `ObjectiveCatalog.Covers`/`CampaignObjectives`; naming reuses `EntityNameResolver`; the tab reuses the Director-tab pattern.
- **Name-clash guard:** the endgame **Atlas map** feature already owns `data-tab="atlas"`, the `atlasProvider`/`_atlas` ApiServer members, and `AtlasJson`. The Entity Atlas uses distinct names everywhere: `entatlas` / `data-view="entatlas"`, `entityAtlasProvider`, `_entityAtlas`, `_entityAtlasEntries`.
- **Pure logic in Core** (`tests/POE2Radar.Tests` references `POE2Radar.Core` only). Overlay-layer accumulators (which reference Overlay types) are verified by build + gate, not unit tests.
- **ConfigDir:** all JSON files live under `Path.Combine(AppContext.BaseDirectory, "config")` (the `RadarApp.ConfigDir` property).

---

## File Structure

- `src/POE2Radar.Core/Campaign/AtlasEntry.cs` — **NEW.** The `AtlasEntry` record + `AtlasCensus` (pure census filter + dedup signature). Unit-tested.
- `src/POE2Radar.Core/Game/EntityNameResolver.cs` — **MODIFY.** Add the live user-override layer (`SetUserOverrides` + override-first resolution). Unit-tested.
- `src/POE2Radar.Overlay/Web/EntityAtlasLog.cs` — **NEW.** The census accumulator (mirrors hardened `SeenPoiLog`).
- `src/POE2Radar.Overlay/Web/EntityNameStore.cs` — **NEW.** Owns `config/entity_names_user.json`; installs overrides live + persists.
- `src/POE2Radar.Overlay/Web/CampaignObjectives.cs` — **MODIFY.** One-line `Covers(in EntityDot)` forwarder.
- `src/POE2Radar.Overlay/Web/ApiServer.cs` — **MODIFY.** Ctor params + 4 endpoints + 2 apply-helpers.
- `src/POE2Radar.Overlay/Web/DashboardHtml.cs` — **MODIFY.** The "Entity Atlas" tab.
- `src/POE2Radar.Overlay/RadarApp.cs` — **MODIFY.** Construct the two stores, observe pre-cull, flush on dispose, pass into the `ApiServer` ctor.
- `docs/upstream-merge.md`, `docs/release-checklist.md` — **MODIFY.** Hook list + manual checklist item.
- `tests/POE2Radar.Tests/AtlasCensusTests.cs`, `tests/POE2Radar.Tests/EntityNameResolverOverrideTests.cs` — **NEW.**

---

## Task 1: Census model + filter (Core, pure, TDD)

**Files:**
- Create: `src/POE2Radar.Core/Campaign/AtlasEntry.cs`
- Test: `tests/POE2Radar.Tests/AtlasCensusTests.cs`

**Interfaces:**
- Consumes: `Poe2Live.EntityDot` (`readonly record struct`: `(uint Id, nint Address, Vector2 Grid, Vector3 World, EntityCategory Category, string Metadata, int HpCur, int HpMax, bool Poi, byte Reaction, Rarity Rarity, bool Opened, …)`); `Poe2Live.EntityCategory { Player, Monster, Npc, Chest, Transition, Object, Other }`; `Poe2Live.Rarity { Normal=0, Magic=1, Rare=2, Unique=3, NonMonster=-1 }`; `JunkFilter.IsJunk(string)`.
- Produces: `record AtlasEntry(string Metadata, string Category, string Rarity, bool Poi, string FirstZone, int Count, DateTime FirstSeenUtc, DateTime LastSeenUtc)`; `static bool AtlasCensus.IsCensusEntity(in Poe2Live.EntityDot)`; `static string AtlasCensus.Signature(in Poe2Live.EntityDot)`.

- [ ] **Step 1: Write the failing test**

Create `tests/POE2Radar.Tests/AtlasCensusTests.cs`:

```csharp
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

public class AtlasCensusTests
{
    private static Poe2Live.EntityDot Dot(string metadata,
        Poe2Live.EntityCategory cat = Poe2Live.EntityCategory.Monster)
        => new(0, 0, default, default, cat, metadata, 0, 0, false, 0, Poe2Live.Rarity.Normal, false);

    [Fact] public void Keeps_Monster()
        => Assert.True(AtlasCensus.IsCensusEntity(Dot("Metadata/Monsters/Wraith/Wraith1")));

    [Fact] public void Keeps_Npc()
        => Assert.True(AtlasCensus.IsCensusEntity(Dot("Metadata/NPC/Act1/Una", Poe2Live.EntityCategory.Npc)));

    [Fact] public void Skips_Player()
        => Assert.False(AtlasCensus.IsCensusEntity(Dot("Metadata/Player", Poe2Live.EntityCategory.Player)));

    [Fact] public void Skips_Junk_Fx()
        => Assert.False(AtlasCensus.IsCensusEntity(Dot("Metadata/Effects/fx/Spell/Foo")));

    [Fact] public void Skips_Junk_Daemon()
        => Assert.False(AtlasCensus.IsCensusEntity(Dot("Metadata/Monsters/Daemon/SomeDaemon")));

    [Fact] public void Skips_EmptyMetadata()
        => Assert.False(AtlasCensus.IsCensusEntity(Dot("")));

    [Fact] public void Signature_StripsLevelSuffix()
        => Assert.Equal("Metadata/Monsters/Wraith/Wraith1",
                        AtlasCensus.Signature(Dot("Metadata/Monsters/Wraith/Wraith1@34")));

    [Fact] public void Signature_DedupsAcrossLevels()
        => Assert.Equal(AtlasCensus.Signature(Dot("Metadata/M/Foo@34")),
                        AtlasCensus.Signature(Dot("Metadata/M/Foo@45")));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test POE2Radar.slnx`
Expected: FAIL to compile — `AtlasCensus`/`AtlasEntry` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/POE2Radar.Core/Campaign/AtlasEntry.cs`:

```csharp
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign;

/// <summary>One distinct entity in the Atlas census: a metadata path the overlay has encountered,
/// with how it was categorized, how often, and where first seen. Naming and objective-classification
/// are derived at read time (resolver hit / catalog coverage), never stored here.</summary>
public sealed record AtlasEntry(
    string Metadata,
    string Category,
    string Rarity,
    bool Poi,
    string FirstZone,
    int Count,
    System.DateTime FirstSeenUtc,
    System.DateTime LastSeenUtc);

/// <summary>Pure rules for which entities belong in the full Atlas census and how to dedup them.
/// Allocation-light (enum compares + substring); used per-tick by <c>EntityAtlasLog</c>.</summary>
public static class AtlasCensus
{
    /// <summary>Keep every real entity EXCEPT the local/party player and <see cref="JunkFilter"/>
    /// noise (FX / audio / daemon / MTX / clone / attachment nodes). Ordinary monsters ARE kept —
    /// the Atlas names everything, not just objective candidates.</summary>
    public static bool IsCensusEntity(in Poe2Live.EntityDot e)
    {
        if (e.Category == Poe2Live.EntityCategory.Player) return false;
        if (string.IsNullOrEmpty(e.Metadata)) return false;
        return !JunkFilter.IsJunk(e.Metadata);
    }

    /// <summary>Dedup key = the metadata path with any trailing runtime "@&lt;level&gt;" annotation
    /// stripped (so a monster at @34 and @45 collapse to one census entry, matching how
    /// <see cref="EntityNameResolver"/> keys names).</summary>
    public static string Signature(in Poe2Live.EntityDot e)
    {
        var m = e.Metadata;
        var at = m.IndexOf('@');
        return at >= 0 ? m[..at] : m;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test POE2Radar.slnx`
Expected: PASS — all existing tests + the 8 new `AtlasCensusTests`.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Campaign/AtlasEntry.cs tests/POE2Radar.Tests/AtlasCensusTests.cs
git commit -m "feat(atlas): AtlasEntry record + AtlasCensus filter (Core, tested)"
```

---

## Task 2: EntityNameResolver live override layer (Core, TDD)

**Files:**
- Modify: `src/POE2Radar.Core/Game/EntityNameResolver.cs`
- Test: `tests/POE2Radar.Tests/EntityNameResolverOverrideTests.cs`

**Interfaces:**
- Consumes: the existing `EntityNameResolver.Shared` singleton + its embedded table (a stable documented sample key: `Metadata/Monsters/Wraith/WraithSpookyLightning` → `Lightning Wraith`).
- Produces: `void EntityNameResolver.SetUserOverrides(IReadOnlyDictionary<string,string>? overrides)` (atomic, thread-safe, blank values dropped, null clears); `Resolve` now checks overrides (exact-then-prefix) **before** the embedded table.

- [ ] **Step 1: Write the failing test**

Create `tests/POE2Radar.Tests/EntityNameResolverOverrideTests.cs`:

```csharp
using POE2Radar.Core.Game;

public class EntityNameResolverOverrideTests
{
    // A stable embedded sample, documented in EntityNameResolver's own summary.
    private const string EmbeddedKey = "Metadata/Monsters/Wraith/WraithSpookyLightning";
    private const string EmbeddedName = "Lightning Wraith";

    [Fact]
    public void UserOverride_BeatsEmbedded()
    {
        var r = EntityNameResolver.Shared;
        try { r.SetUserOverrides(new Dictionary<string, string> { [EmbeddedKey] = "Custom Wraith" });
              Assert.Equal("Custom Wraith", r.Resolve(EmbeddedKey)); }
        finally { r.SetUserOverrides(null); }
    }

    [Fact]
    public void Embedded_StillResolves_WithoutOverride()
    {
        var r = EntityNameResolver.Shared;
        r.SetUserOverrides(null);
        Assert.Equal(EmbeddedName, r.Resolve(EmbeddedKey));
    }

    [Fact]
    public void OverrideOnlyKey_Resolves()
    {
        var r = EntityNameResolver.Shared;
        try { r.SetUserOverrides(new Dictionary<string, string> { ["Metadata/Made/Up/Thing"] = "My Thing" });
              Assert.Equal("My Thing", r.Resolve("Metadata/Made/Up/Thing")); }
        finally { r.SetUserOverrides(null); }
    }

    [Fact]
    public void Override_PrefixFallback_Applies()
    {
        var r = EntityNameResolver.Shared;
        try { r.SetUserOverrides(new Dictionary<string, string> { ["Metadata/Made/Up"] = "Base Thing" });
              Assert.Equal("Base Thing", r.Resolve("Metadata/Made/Up/Variant")); }
        finally { r.SetUserOverrides(null); }
    }

    [Fact]
    public void BlankOverride_DoesNotShadowEmbedded()
    {
        var r = EntityNameResolver.Shared;
        try { r.SetUserOverrides(new Dictionary<string, string> { [EmbeddedKey] = "" });
              Assert.Equal(EmbeddedName, r.Resolve(EmbeddedKey)); }
        finally { r.SetUserOverrides(null); }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test POE2Radar.slnx`
Expected: FAIL to compile — `SetUserOverrides` does not exist.

- [ ] **Step 3: Modify `EntityNameResolver.cs`**

Add the override field next to `_names` (after the `private readonly Dictionary<string, string> _names;` line):

```csharp
    private volatile IReadOnlyDictionary<string, string>? _overrides;
```

Replace the entire existing `public string? Resolve(string metadataPath)` method (the one that strips `@` then does the exact + drop-trailing-segments loop) with this — it factors the lookup into a shared helper and consults overrides first:

```csharp
    /// <summary>
    /// Resolve a metadata path to a friendly name, or null if unknown. Consults the live user
    /// override layer first (exact, then progressively dropping trailing segments), then the embedded
    /// table the same way. Strips a trailing runtime area-level annotation ("...MonkeyJungle@34") since
    /// table keys never carry it.
    /// </summary>
    public string? Resolve(string metadataPath)
    {
        if (string.IsNullOrEmpty(metadataPath)) return null;
        var at = metadataPath.IndexOf('@');
        var path = at >= 0 ? metadataPath[..at] : metadataPath;

        var ov = _overrides; // volatile read once
        if (ov != null && LookupWithFallback(ov, path) is { } o) return o;
        return LookupWithFallback(_names, path);
    }

    // Exact match, then drop trailing "/segment"s and retry. Bounded by path depth.
    private static string? LookupWithFallback(IReadOnlyDictionary<string, string> table, string path)
    {
        if (table.TryGetValue(path, out var name)) return name;
        var probe = path;
        int slash;
        while ((slash = probe.LastIndexOf('/')) > 0)
        {
            probe = probe[..slash];
            if (table.TryGetValue(probe, out name)) return name;
        }
        return null;
    }

    /// <summary>
    /// Install a user override table (metadata→name), consulted BEFORE the embedded table by
    /// <see cref="Resolve"/>. Atomic swap (volatile) — lock-free for the many reader threads
    /// (world/render/API); call from the rare writer (startup load / a name edit). Null clears all
    /// overrides. Blank names are dropped so an empty override never shadows a real embedded name.
    /// </summary>
    public void SetUserOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0) { _overrides = null; return; }
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in overrides)
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v)) copy[k] = v;
        _overrides = copy.Count > 0 ? copy : null;
    }
```

(`ResolveOrShorten` is unchanged — it still calls `Resolve`.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test POE2Radar.slnx`
Expected: PASS — all existing tests + the 5 new override tests.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Game/EntityNameResolver.cs tests/POE2Radar.Tests/EntityNameResolverOverrideTests.cs
git commit -m "feat(atlas): live user-override layer in EntityNameResolver (tested)"
```

---

## Task 3: EntityAtlasLog accumulator + RadarApp wiring (Overlay)

**Files:**
- Create: `src/POE2Radar.Overlay/Web/EntityAtlasLog.cs`
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (field, construction, pre-cull observe, dispose flush)

**Interfaces:**
- Consumes: `AtlasEntry`, `AtlasCensus.IsCensusEntity`/`Signature` (Task 1); `Poe2Live.EntityDot`; `ZoneGuide.Shared.FriendlyName(string areaCode)`.
- Produces: `EntityAtlasLog` with `Observe(IReadOnlyList<Poe2Live.EntityDot> entities, string areaCode)`, `IReadOnlyList<AtlasEntry> All`, `void Flush()`.

- [ ] **Step 1: Create `EntityAtlasLog.cs`**

Create `src/POE2Radar.Overlay/Web/EntityAtlasLog.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Persistent accumulator of the full distinct-entity census (every non-junk, non-player metadata
/// path the overlay has encountered) — the Atlas naming/coverage worklist. Mirrors the hardened
/// <see cref="SeenPoiLog"/>: mutations under <c>_gate</c>, periodic flush only on a NEW signature,
/// count drift persisted at shutdown. Read-only w.r.t. the game.
/// </summary>
public sealed class EntityAtlasLog
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, AtlasEntry> _seen = new(StringComparer.Ordinal); // under _gate
    private readonly Stopwatch _sinceDirty = Stopwatch.StartNew();
    private bool _dirty;        // a NEW census signature arrived → arm the periodic debounced flush
    private bool _countsDirty;  // only repeat-sighting drift → persisted at shutdown, never on the loop
    private const long FlushAfterMs = 4000;
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public EntityAtlasLog(string filePath) { _filePath = filePath; Load(); }

    /// <summary>Snapshot of the whole census (locked; safe from the API thread).</summary>
    public IReadOnlyList<AtlasEntry> All { get { lock (_gate) return _seen.Values.ToArray(); } }

    /// <summary>Record this tick's entities into the census. Skips the player + JunkFilter noise via
    /// <see cref="AtlasCensus.IsCensusEntity"/>; dedups by metadata signature. Call from the world
    /// thread with the PRE-cull entity list (so user-hidden entities still get catalogued).</summary>
    public void Observe(IReadOnlyList<Poe2Live.EntityDot> entities, string areaCode)
    {
        lock (_gate)
        {
            foreach (var e in entities)
            {
                if (!AtlasCensus.IsCensusEntity(in e)) continue;
                var sig = AtlasCensus.Signature(in e);
                if (_seen.TryGetValue(sig, out var cur))
                {
                    // Repeat sighting: bump in memory, but do NOT arm the periodic flush (mirrors the
                    // hardened SeenPoiLog — avoids an every-4s whole-file rewrite for the whole session).
                    _seen[sig] = cur with { Count = cur.Count + 1, LastSeenUtc = DateTime.UtcNow };
                    _countsDirty = true;
                }
                else
                {
                    var now = DateTime.UtcNow;
                    _seen[sig] = new AtlasEntry(sig, e.Category.ToString(), e.Rarity.ToString(), e.Poi,
                        ZoneGuide.Shared.FriendlyName(areaCode), 1, now, now);
                    if (!_dirty) { _dirty = true; _sinceDirty.Restart(); }
                }
            }
        }
        MaybeFlush();
    }

    private void MaybeFlush()
    {
        lock (_gate)
        {
            if (!_dirty || _sinceDirty.ElapsedMilliseconds < FlushAfterMs) return;
            _dirty = false; _countsDirty = false; Save();
        }
    }

    public void Flush() { lock (_gate) { if (_dirty || _countsDirty) { _dirty = false; _countsDirty = false; Save(); } } }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<AtlasEntry>>(File.ReadAllText(_filePath), Json);
            if (list == null) return;
            foreach (var a in list)
                if (!string.IsNullOrEmpty(a.Metadata)) _seen[a.Metadata] = a;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Entity atlas load failed: {ex.Message}"); }
    }

    private void Save() // under _gate
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_seen.Values.ToList(), Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Entity atlas save failed: {ex.Message}"); }
    }
}
```

- [ ] **Step 2: Declare the field in `RadarApp.cs`**

Find the `_seenPoiLog` field declaration (near the other store fields, e.g. `private readonly SeenPoiLog _seenPoiLog;`) and add directly below it:

```csharp
    private readonly EntityAtlasLog _entityAtlas;
```

- [ ] **Step 3: Construct it in `RadarApp.cs`**

Find `_seenPoiLog = new SeenPoiLog(Path.Combine(ConfigDir, "seen_pois.json"));` (~line 490) and add directly below it:

```csharp
        _entityAtlas = new EntityAtlasLog(Path.Combine(ConfigDir, "entity_atlas.json"));
```

- [ ] **Step 4: Observe the PRE-cull entity list in `WorldTick`**

Find `_entities = _live.Entities(areaInstance);` (~line 946). Insert directly below it (this is BEFORE the `_entities.RemoveAll(...)` cull, so hidden entities are still catalogued; `AtlasCensus` skips the player + junk itself):

```csharp
        // Atlas census: catalog EVERY distinct entity for naming/coverage. Runs on the PRE-cull list
        // (before the local-player + user-hidden RemoveAll below) so hiding a dot doesn't erase it from
        // the name database. AtlasCensus skips Player + JunkFilter noise.
        _entityAtlas.Observe(_entities, areaCode);
```

- [ ] **Step 5: Flush on shutdown in `RadarApp.cs`**

Find `_seenPoiLog.Flush();` in `Dispose` (~line 2160) and add directly below it:

```csharp
        _entityAtlas.Flush();
```

- [ ] **Step 6: Build, gate, commit**

```bash
dotnet build POE2Radar.slnx
```
Expected: Build succeeded. 0 Warning(s), 0 Error(s).

```bash
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1
```
Expected: `COMPLIANCE GATE: PASS`.

```bash
git add src/POE2Radar.Overlay/Web/EntityAtlasLog.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(atlas): EntityAtlasLog census accumulator wired into the world tick"
```

---

## Task 4: EntityNameStore + RadarApp wiring (Overlay)

**Files:**
- Create: `src/POE2Radar.Overlay/Web/EntityNameStore.cs`
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (field, construction, dispose flush)

**Interfaces:**
- Consumes: `EntityNameResolver.Shared.SetUserOverrides(...)` (Task 2).
- Produces: `EntityNameStore` with `void Add(string metadata, string name)` (blank name clears), `void Merge(IReadOnlyDictionary<string,string> names)`, `IReadOnlyDictionary<string,string> All`, `void Flush()`.

- [ ] **Step 1: Create `EntityNameStore.cs`**

Create `src/POE2Radar.Overlay/Web/EntityNameStore.cs`:

```csharp
using System.Text.Json;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Owns the user's friendly-name overrides (<c>config/entity_names_user.json</c>, a flat
/// metadata→name map) and keeps them live in <see cref="EntityNameResolver"/>. Naming an entity
/// updates the map, re-installs the overrides (radar/legend reflect it immediately), and saves.
/// Writes are rare (user actions), so the save is immediate (no debounce). Read-only w.r.t. the game.
/// </summary>
public sealed class EntityNameStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase); // under _gate
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public EntityNameStore(string filePath) { _filePath = filePath; Load(); Publish(); }

    /// <summary>Snapshot of the user name map (locked; for export).</summary>
    public IReadOnlyDictionary<string, string> All
    { get { lock (_gate) return new Dictionary<string, string>(_names, StringComparer.OrdinalIgnoreCase); } }

    /// <summary>Set/clear one friendly name; installs it live + saves. Blank name reverts to embedded.</summary>
    public void Add(string metadata, string name)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return;
        lock (_gate)
        {
            var key = metadata.Trim();
            if (string.IsNullOrWhiteSpace(name)) _names.Remove(key); else _names[key] = name.Trim();
            Save();
        }
        Publish();
    }

    /// <summary>Merge a batch (community import); installs + saves once. Blank value clears that key.</summary>
    public void Merge(IReadOnlyDictionary<string, string> names)
    {
        if (names is not { Count: > 0 }) return;
        lock (_gate)
        {
            foreach (var (k, v) in names)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                var key = k.Trim();
                if (string.IsNullOrWhiteSpace(v)) _names.Remove(key); else _names[key] = v.Trim();
            }
            Save();
        }
        Publish();
    }

    /// <summary>Save anything pending (Dispose parity; immediate-save makes this a safety net).</summary>
    public void Flush() { lock (_gate) Save(); }

    // Install the current map as the resolver's override layer (atomic swap inside the resolver).
    private void Publish()
    {
        Dictionary<string, string> copy;
        lock (_gate) copy = new Dictionary<string, string>(_names, StringComparer.OrdinalIgnoreCase);
        EntityNameResolver.Shared.SetUserOverrides(copy);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath), Json);
            if (map == null) return;
            foreach (var (k, v) in map)
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v)) _names[k] = v;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Entity name store load failed: {ex.Message}"); }
    }

    private void Save() // under _gate
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_names, Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Entity name store save failed: {ex.Message}"); }
    }
}
```

- [ ] **Step 2: Declare the field in `RadarApp.cs`**

Below the `_entityAtlas` field added in Task 3, add:

```csharp
    private readonly EntityNameStore _entityNameStore;
```

- [ ] **Step 3: Construct it in `RadarApp.cs`**

Below the `_entityAtlas = new EntityAtlasLog(...)` line added in Task 3, add:

```csharp
        _entityNameStore = new EntityNameStore(Path.Combine(ConfigDir, "entity_names_user.json"));
```

- [ ] **Step 4: Flush on shutdown in `RadarApp.cs`**

Below the `_entityAtlas.Flush();` line added in Task 3, add:

```csharp
        _entityNameStore.Flush();
```

- [ ] **Step 5: Build, gate, commit**

```bash
dotnet build POE2Radar.slnx
```
Expected: Build succeeded. 0 Warning(s), 0 Error(s).

```bash
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1
```
Expected: `COMPLIANCE GATE: PASS`.

```bash
git add src/POE2Radar.Overlay/Web/EntityNameStore.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(atlas): EntityNameStore drives the live name-override layer"
```

---

## Task 5: ApiServer endpoints + ctor wiring (Overlay)

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/CampaignObjectives.cs` (one-line forwarder)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (ctor params, fields, 4 cases, 2 helpers)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (the `new ApiServer(...)` call)

**Interfaces:**
- Consumes: `EntityAtlasLog.All` (Task 3); `EntityNameStore` (Task 4); `EntityNameResolver.Shared.Resolve`/`ResolveOrShorten` (Task 2); `PoiCandidate.IsCandidate(in EntityDot)`; `ObjectiveCatalog.Covers(in EntityDot)`; existing `CampaignObjectives` (`All`, `Add`, the existing `Covers(SeenPoi)`); existing `IsLoopbackHost`, `ReadBody`, `Write`, `SanitizeObjective`, `Json`.
- Produces: `GET /api/entity-atlas`, `POST /api/entity-atlas/name`, `GET /api/entity-atlas/export`, `POST /api/entity-atlas/import`; `CampaignObjectives.Covers(in Poe2Live.EntityDot)`.

- [ ] **Step 1: Add the `Covers(in EntityDot)` forwarder to `CampaignObjectives.cs`**

Find the existing line `public bool Covers(SeenPoi p) => _snapshot.Covers(p);` and add directly below it:

```csharp
    /// <summary>True if any enabled objective matches this live entity (lock-free; mirrors Rank).</summary>
    public bool Covers(in Poe2Live.EntityDot e) => _snapshot.Covers(in e);
```

- [ ] **Step 2: Add the two ctor parameters + fields in `ApiServer.cs`**

In the `ApiServer` ctor signature, find these two consecutive lines:

```csharp
        CampaignObjectives objectives,
        Func<IReadOnlyList<SeenPoi>> seenPoisProvider,
```

and insert two new REQUIRED parameters immediately after `seenPoisProvider` (i.e., still before the first optional `Func<object>? atlasProvider = null` parameter):

```csharp
        CampaignObjectives objectives,
        Func<IReadOnlyList<SeenPoi>> seenPoisProvider,
        Func<IReadOnlyList<AtlasEntry>> entityAtlasProvider,
        EntityNameStore entityNames,
```

Add the backing fields next to `private readonly Func<IReadOnlyList<SeenPoi>> _seenPois;`:

```csharp
    private readonly Func<IReadOnlyList<AtlasEntry>> _entityAtlasEntries;
    private readonly EntityNameStore _entityNames;
```

Assign them in the ctor body next to `_seenPois = seenPoisProvider;`:

```csharp
        _entityAtlasEntries = entityAtlasProvider;
        _entityNames = entityNames;
```

- [ ] **Step 3: Add the four endpoint cases in `ApiServer.cs`**

Find the `case "/api/seen-pois":` block (the one ending in `break;` before `case "/api/version":`). Add these four cases directly after that block's `break;` (keeping `/api/entity-atlas*` grouped):

```csharp
            case "/api/entity-atlas":
                // The full entity census, each entry tagged whether it already has a friendly NAME
                // (resolver hit) and whether a Director objective already COVERS it. Unnamed entries and
                // notable-uncatalogued entries are the worklists. Read-only; no identifying data.
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    entries = _entityAtlasEntries().Select(a =>
                    {
                        var cat = Enum.TryParse<Poe2Live.EntityCategory>(a.Category, ignoreCase: true, out var c)
                            ? c : Poe2Live.EntityCategory.Other;
                        var rar = Enum.TryParse<Poe2Live.Rarity>(a.Rarity, ignoreCase: true, out var r)
                            ? r : Poe2Live.Rarity.NonMonster;
                        var e = new Poe2Live.EntityDot(0, 0, default, default, cat, a.Metadata, 0, 0, a.Poi, 0, rar, false);
                        var named = EntityNameResolver.Shared.Resolve(a.Metadata);
                        return new
                        {
                            metadata = a.Metadata,
                            name = named ?? EntityNameResolver.Shared.ResolveOrShorten(a.Metadata),
                            named = named != null,
                            category = a.Category, rarity = a.Rarity, poi = a.Poi,
                            zone = a.FirstZone, count = a.Count,
                            notable = PoiCandidate.IsCandidate(in e),
                            covered = _objectives.Covers(in e),
                        };
                    }),
                }, Json));
                break;

            case "/api/entity-atlas/name":
            {
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                ApplyAtlasName(ReadBody(ctx));
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true }, Json));
                break;
            }

            case "/api/entity-atlas/export":
                // A shareable pack: your captured names + the Director objectives. No identifying data.
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    names = _entityNames.All,
                    objectives = _objectives.All,
                }, Json));
                break;

            case "/api/entity-atlas/import":
            {
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                ApplyAtlasImport(ReadBody(ctx));
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, names = _entityNames.All.Count, objectives = _objectives.All.Count }, Json));
                break;
            }
```

- [ ] **Step 4: Add the two apply-helpers in `ApiServer.cs`**

Directly after the existing `private void ApplyObjectives(string body) { … }` method, add:

```csharp
    /// <summary>Set one friendly name from the Atlas tab: {"metadata":"…","name":"…"}. Blank name
    /// reverts to the embedded table. Edits the local override file only — never the game.</summary>
    private void ApplyAtlasName(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        var metadata = root.TryGetProperty("metadata", out var m) ? m.GetString() : null;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (!string.IsNullOrWhiteSpace(metadata))
            _entityNames.Add(metadata.Trim(), (name ?? "").Trim());
    }

    /// <summary>Merge a shared Atlas pack: {"names":{meta:name,…},"objectives":[CampaignObjective,…]}.
    /// Names layer into the override store; objectives upsert into the catalog. Additive (never deletes).</summary>
    private void ApplyAtlasImport(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in names.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v)
                    map[p.Name] = v;
            _entityNames.Merge(map);
        }
        if (root.TryGetProperty("objectives", out var objs) && objs.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in objs.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var o = JsonSerializer.Deserialize<CampaignObjective>(el.GetRawText(), Json);
                if (o != null) _objectives.Add(SanitizeObjective(o));
            }
        }
    }
```

- [ ] **Step 5: Update the `new ApiServer(...)` call in `RadarApp.cs`**

Find the call (~line 492). Insert `() => _entityAtlas.All, _entityNameStore,` immediately after `() => _seenPoiLog.All,` and before `AtlasJson` — matching the new ctor parameter order:

```csharp
        _api = new ApiServer(() => _state, _settings, GetNavSelection, ToggleNavTarget, ClearNavSelection,
                             _hidden, _displayRules, _landmarkStore, CurrentTilePaths, () => _modCatalog.All,
                             _campaign, () => _seenPoiLog.All, () => _entityAtlas.All, _entityNameStore,
                             AtlasJson, SetAtlasSelection,
                             SetAtlasHighlight, VersionJson, _settings.ApiPort);
```

- [ ] **Step 6: Build, gate, test, commit**

```bash
dotnet build POE2Radar.slnx
```
Expected: Build succeeded. 0 Warning(s), 0 Error(s).

```bash
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1
```
Expected: `COMPLIANCE GATE: PASS`.

```bash
dotnet test POE2Radar.slnx
```
Expected: PASS (no test change; verifies nothing regressed).

```bash
git add src/POE2Radar.Overlay/Web/CampaignObjectives.cs src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(atlas): /api/entity-atlas + name/export/import endpoints"
```

---

## Task 6: Dashboard "Entity Atlas" tab (Overlay)

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs`

**Interfaces:**
- Consumes: `GET /api/entity-atlas`, `POST /api/entity-atlas/name`, `GET /api/entity-atlas/export`, `POST /api/entity-atlas/import`, `POST /api/objectives` (Task 5). Existing JS helpers `$`, `$$`, `getJSON`, `esc`, `cssEsc` (all already in the file). `DashboardHtml.Page` is a C# **raw string literal** (`public const string Page = """ … """`), so the HTML/JS below inserts **verbatim — no escaping**.

- [ ] **Step 1: Add the tab button**

Find the `.tabs` button row and the `<button class="tab" data-tab="director">Director</button>` line (added by the Catalog Builder). Add directly after it:

```html
            <button class="tab" data-tab="entatlas">Entity Atlas</button>
```

- [ ] **Step 2: Add the view section**

Find the Director section (`<section class="view" data-view="director" hidden>` … its closing `</section>`). Add this new section directly after the Director section's closing `</section>`:

```html
        <section class="view" data-view="entatlas" hidden>
          <div class="card">
            <h3>Entity Atlas <small>name every entity you've seen; classify the notable ones</small></h3>
            <div class="row">
              <input id="eaSearch" class="numin" type="text" placeholder="filter…" style="width:200px">
              <button class="numin" id="eaExport">Export pack</button>
              <label class="numin" style="cursor:pointer">Import pack<input id="eaImport" type="file" accept="application/json" style="display:none"></label>
            </div>
          </div>
          <div class="card">
            <h3>Needs a name <small>entities with no friendly name yet (shows the raw path)</small></h3>
            <div id="eaUnnamed" class="znotes" style="display:block"></div>
          </div>
          <div class="card">
            <h3>Notable, uncatalogued <small>named/notable entities no objective covers yet</small></h3>
            <div id="eaNotable" class="znotes" style="display:block"></div>
          </div>
        </section>
```

- [ ] **Step 3: Hook the tab into the switch JS**

Find the tab-switch closure with the `if(activeTab==='director') loadDirector();` line. Add directly after it:

```javascript
    if(activeTab==='entatlas') loadEntAtlas();
```

- [ ] **Step 4: Add the Entity Atlas JS block**

Place this near the Director JS (`loadDirector`/`renderDirector`), e.g. directly after the Director tab's `$('#dirSearch')?.addEventListener(...)` line:

```javascript
/* ── entity atlas tab: name everything + classify the notable ── */
let eaEntries=[], eaQ='';
async function loadEntAtlas(){
  try{ const s=await getJSON('/api/entity-atlas'); eaEntries=s.entries||[]; }catch(e){ eaEntries=[]; }
  renderEntAtlas();
}
async function postAtlasName(metadata, name){
  try{ await fetch('/api/entity-atlas/name',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({metadata,name})}); }catch(e){}
  loadEntAtlas();
}
function eaMatch(a){ return !eaQ || ((a.name+' '+a.metadata+' '+a.category).toLowerCase().includes(eaQ)); }
function renderEntAtlas(){
  const un=$('#eaUnnamed');
  if(un){
    const rows=eaEntries.filter(a=>!a.named).filter(eaMatch).sort((x,y)=>y.count-x.count);
    un.innerHTML = rows.length ? rows.map(eaNameRow).join('')
      : '<div class="row"><div class="rl hint-row">Everything in view has a name — explore more, or clear the filter.</div></div>';
    rows.forEach(a=>{
      const el=un.querySelector('[data-m="'+cssEsc(a.metadata)+'"]'); if(!el) return;
      el.querySelector('.ea-save').onclick=()=>{ const v=el.querySelector('.ea-name').value.trim(); if(v) postAtlasName(a.metadata, v); };
    });
  }
  const nt=$('#eaNotable');
  if(nt){
    const rows=eaEntries.filter(a=>a.notable && !a.covered).filter(eaMatch).sort((x,y)=>y.count-x.count);
    nt.innerHTML = rows.length ? rows.map(eaClassRow).join('')
      : '<div class="row"><div class="rl hint-row">No uncatalogued notable entities in view.</div></div>';
    rows.forEach(a=>{
      const el=nt.querySelector('[data-m="'+cssEsc(a.metadata)+'"]'); if(!el) return;
      el.querySelector('.ea-add').onclick=async()=>{
        const cat=el.querySelector('.ea-cat').value;
        const prio=parseInt(el.querySelector('.ea-prio').value,10)||50;
        try{ await fetch('/api/objectives',{method:'POST',headers:{'Content-Type':'application/json'},
             body:JSON.stringify({add:{id:'e:'+a.metadata,label:a.name,category:cat,priority:prio,enabled:true,metadata:[a.metadata]}})}); }catch(e){}
        loadEntAtlas();
      };
    });
  }
}
function eaNameRow(a){
  return '<div class="row" data-m="'+esc(a.metadata)+'">'
    + '<div class="rl">'+esc(a.name)+'<small>'+esc(a.category)+' · '+esc(a.zone||'?')+' · ×'+a.count+'</small></div>'
    + '<input class="numin ea-name" type="text" placeholder="friendly name" style="width:160px">'
    + '<button class="delbtn ea-save">Save</button></div>';
}
function eaClassRow(a){
  const opts=['League','PermanentUpgrade','GemSource','Boss','SideZone','SideBoss','Other']
    .map(c=>'<option value="'+c+'">'+c+'</option>').join('');
  return '<div class="row" data-m="'+esc(a.metadata)+'">'
    + '<div class="rl">'+esc(a.name)+'<small>'+esc(a.category)+' · '+esc(a.zone||'?')+' · ×'+a.count+'</small></div>'
    + '<select class="numin selin ea-cat">'+opts+'</select>'
    + '<input class="numin ea-prio" type="number" min="0" max="1000" value="50" style="width:64px">'
    + '<button class="delbtn ea-add">Classify</button></div>';
}
$('#eaSearch')?.addEventListener('input',e=>{ eaQ=e.target.value.toLowerCase(); renderEntAtlas(); });
$('#eaExport')?.addEventListener('click',async()=>{
  try{ const p=await getJSON('/api/entity-atlas/export');
    const blob=new Blob([JSON.stringify(p,null,2)],{type:'application/json'});
    const u=URL.createObjectURL(blob); const a=document.createElement('a');
    a.href=u; a.download='atlas-pack.json'; a.click(); URL.revokeObjectURL(u);
  }catch(e){}
});
$('#eaImport')?.addEventListener('change',e=>{
  const f=e.target.files&&e.target.files[0]; if(!f) return;
  const rd=new FileReader();
  rd.onload=async()=>{ try{ const pack=JSON.parse(rd.result);
      await fetch('/api/entity-atlas/import',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(pack)});
      loadEntAtlas();
    }catch(err){} e.target.value=''; };
  rd.readAsText(f);
});
```

- [ ] **Step 5: Build, gate, test**

```bash
dotnet build POE2Radar.slnx
```
Expected: Build succeeded. 0 Warning(s), 0 Error(s).

```bash
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1
```
Expected: `COMPLIANCE GATE: PASS`.

```bash
dotnet test POE2Radar.slnx
```
Expected: PASS (the JS is a string literal; no test change).

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(atlas): dashboard Entity Atlas tab (name + classify + share)"
```

---

## Task 7: Docs — upstream-merge + release checklist

**Files:**
- Modify: `docs/upstream-merge.md`, `docs/release-checklist.md`

- [ ] **Step 1: `docs/upstream-merge.md`** — under "What POE2GPS adds on top of Sikaka", after the **Catalog Builder** bullet, add:

```markdown
- **Entity Atlas** (`Core/Campaign/AtlasEntry.cs` + `AtlasCensus`, `Overlay/Web/EntityAtlasLog.cs`,
  `Overlay/Web/EntityNameStore.cs`, the `EntityNameResolver` user-override layer). Hooks: the
  `_entityAtlas` + `_entityNameStore` fields + ctor construction; `_entityAtlas.Observe(_entities,
  areaCode)` in `WorldTick` **before** the user-hidden cull; `_entityAtlas.Flush()` +
  `_entityNameStore.Flush()` in `Dispose`; the two `ApiServer` ctor params (`Func<…AtlasEntry>
  entityAtlasProvider`, `EntityNameStore entityNames`) + the `/api/entity-atlas`, `/api/entity-atlas/name`,
  `/api/entity-atlas/export`, `/api/entity-atlas/import` cases + `ApplyAtlasName`/`ApplyAtlasImport`; the
  `CampaignObjectives.Covers(in EntityDot)` forwarder; the `new ApiServer(...)` args
  `() => _entityAtlas.All, _entityNameStore`; the Dashboard "Entity Atlas" tab (`data-tab="entatlas"` /
  `data-view="entatlas"` + `loadEntAtlas`). Note the name-clash guard vs. the endgame Atlas-map feature.
```

- [ ] **Step 2: `docs/release-checklist.md`** — under the manual live-game section, add:

```markdown
- [ ] **Entity Atlas:** in a zone, open the dashboard → Entity Atlas tab; confirm entities populate
      "Needs a name" (raw paths) and "Notable, uncatalogued"; typing a name + Save removes it from
      "Needs a name" AND shows that name on the radar/legend immediately; "Classify" adds a Director
      objective (it leaves the list). "Export pack" downloads `atlas-pack.json`; "Import pack" merges one
      back. Confirm `/api/entity-atlas` carries no character name.
```

- [ ] **Step 3: Commit**

```bash
git add docs/upstream-merge.md docs/release-checklist.md
git commit -m "docs(atlas): upstream-merge hooks + release-checklist item"
```

---

## Self-Review

**Spec coverage** (§ = spec section):
- §4 data flow (Observe → census → /api/entity-atlas tagged → name/classify → export/import) → Tasks 3/5/6. ✓
- §5 components (AtlasEntry, EntityAtlasLog, EntityNameStore, resolver override, endpoints, tab, RadarApp hooks) → Tasks 1–6. ✓
- §6 census filter (skip Player + JunkFilter; dedup; **pre-cull** observe) → Task 1 `AtlasCensus` + Task 3 Step 4. ✓
- §7 classification (named? via `Resolve`; covered? via synthetic `EntityDot` + `Covers`; notable via `PoiCandidate.IsCandidate`) → Task 5 Step 3 (+ Task 5 Step 1 forwarder). ✓
- §8 live name-override layer (user → embedded → shortened; atomic swap) → Task 2 + Task 4. ✓
- §9 export/import (`{names, objectives}`; additive merge; loopback-gated) → Task 5 Steps 3–4. ✓
- §10 compliance/sync (read-only, gate, hooks doc) → gate in 3/4/5/6, Task 7. ✓
- §11 testing (census filter + resolver override units; rest manual) → Tasks 1, 2, 7. ✓
- §12 out of scope (no inspect hotkey, no patch/league tagging, no auto-suggest) → not built. ✓

**Placeholder scan:** none — every step has full code or exact edits with verbatim anchors; no "TBD"/"similar to".

**Type consistency:** `AtlasEntry` (Task 1) is used identically in `EntityAtlasLog` (Task 3) and the `/api/entity-atlas` projection (Task 5). `AtlasCensus.IsCensusEntity`/`Signature` (Task 1) match the `EntityAtlasLog.Observe` calls (Task 3). `EntityNameResolver.SetUserOverrides` (Task 2) matches `EntityNameStore.Publish` (Task 4). `EntityNameStore.All`/`Add`/`Merge` (Task 4) match the endpoints (Task 5). The two new `ApiServer` ctor params (Task 5 Step 2) match the call-site args (Task 5 Step 5). `CampaignObjectives.Covers(in EntityDot)` (Task 5 Step 1) matches its use in `/api/entity-atlas` (Task 5 Step 3). The dashboard fetches `{entries}` / `{names,objectives}` matching the endpoint shapes (Task 6 vs Task 5).
