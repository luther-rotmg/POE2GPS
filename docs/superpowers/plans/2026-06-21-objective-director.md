# Objective Director Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only, per-zone Objective Director to POE2GPS that ranks live in-zone objectives from a community catalog and routes you to the highest-priority one via the existing id-selection → A* → renderer pipeline, advancing as objectives complete.

**Architecture:** Pure ranking + decision logic lives in `Core/Campaign` (unit-tested against `tests/POE2Radar.Tests`). A `CampaignObjectives` store (modeled on `WatchedEntities`) ships an editable JSON catalog. `RadarApp` calls the pure pieces inside its existing `_navLock` and mutates `_selectedIds` — reusing routing/drawing entirely. A dashboard panel + settings toggle round out the surface.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`, x64), `.slnx`, xUnit (existing `tests/POE2Radar.Tests`), the existing nav pipeline (`RouteTracker`/`BackgroundReplanner`/`PathPlanner`), `System.Net.HttpListener` dashboard.

## Global Constraints

- **Read-only.** No `SendInput`/`WriteProcessMemory`/etc. The director only edits the `_selectedIds` string list and reads state. `scripts/compliance-gate.ps1` must still PASS.
- **Reuse routing, never rebuild it.** Select by id (`e:<entityId>` / `t:<landmarkKey>`); never call `PathPlanner`/`BackgroundReplanner`/`RouteTracker` directly.
- **Default routing flow** (seed catalog): **seasonal event (priority 100) → side bosses (80) → side zones (60) → main progression (10, always-present `Transition` catch-all)**. Within a tier, nearest wins.
- **Selection mutation only under `_navLock`** (the field at `RadarApp.cs:212`). The pure decision is computed inside the lock from a snapshot.
- **Respect manual override:** the director owns the selection only when it is empty or exactly `[its last active id]`; any other selection means a manual pick and the director stands down.
- **Allocation discipline:** entity matching runs per-entity on the 30 Hz world thread — the matcher is compiled/allocation-free (mirror `DisplayRules.Compiled`); a small per-tick result list + sort is acceptable.
- **No identifying data in the dashboard payload** (the character name was removed for stealth — keep it out).
- **Build:** `dotnet build POE2Radar.slnx` (0W/0E; `TreatWarningsAsErrors=true`, `Nullable=enable`). **Test:** `dotnet test POE2Radar.slnx`. **Gate (local):** `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1` (no `pwsh` locally).
- Types nest under `Poe2Live` (`Poe2Live.EntityDot`, `.Landmark`, `.EntityCategory`, `.Rarity`) in `POE2Radar.Core.Game`. The real source is under `src/` — ignore `.reference/`.

---

## File structure

**New (isolated):**
- `src/POE2Radar.Core/Campaign/CampaignObjective.cs` — the objective record + compiled matcher + `ObjectiveCatalog.Rank(...)`. Pure → **unit-tested** (Task 1).
- `src/POE2Radar.Core/Campaign/ObjectiveDirector.cs` — the `Decide(...)` selection state machine + `Queue`/`ResetZone`. Pure → **unit-tested** (Task 3).
- `src/POE2Radar.Overlay/Web/CampaignObjectives.cs` — the editable store (embedded seed → `ConfigDir`, lock + volatile snapshot), modeled on `WatchedEntities.cs` (Task 2).
- `src/POE2Radar.Overlay/Web/default_campaign_objectives.json` — the 4-tier seed (Task 2).
- `tests/POE2Radar.Tests/ObjectiveCatalogTests.cs`, `ObjectiveDirectorTests.cs` (Tasks 1, 3).

**Edited (the whole blast radius into shared files):**
- `src/POE2Radar.Overlay/Config/RadarSettings.cs` — `EnableDirector` bool (Task 4).
- `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` — embed the seed json (Task 2).
- `src/POE2Radar.Overlay/RadarApp.cs` — construct the store + director; gate the `OnAreaChanged` auto-add; one `WorldTick` reconcile; publish the queue (Tasks 4, 5).
- `src/POE2Radar.Overlay/Web/ApiServer.cs` — settings round-trip + `RadarState.Director` + `/state` projection (Task 5).
- `src/POE2Radar.Overlay/Web/DashboardHtml.cs` — settings toggle row + sidebar card + render block (Task 5).
- `docs/upstream-merge.md` (Task 6).

---

## Task 1: Objective catalog — record, matcher, ranking (Core, TDD)

**Files:**
- Create: `src/POE2Radar.Core/Campaign/CampaignObjective.cs`
- Test: `tests/POE2Radar.Tests/ObjectiveCatalogTests.cs`

**Interfaces:**
- Produces:
  - `POE2Radar.Core.Campaign.CampaignObjective` record (data).
  - `readonly record struct RankedObjective(string Id, string Label, string Category, int Priority, float DistanceSq)`.
  - `ObjectiveCatalog(IEnumerable<CampaignObjective>)` with `IReadOnlyList<RankedObjective> Rank(IReadOnlyList<Poe2Live.EntityDot> entities, IReadOnlyList<Poe2Live.Landmark> landmarks, System.Numerics.Vector2 player)`.

- [ ] **Step 1: Write the failing tests**

`tests/POE2Radar.Tests/ObjectiveCatalogTests.cs`:

```csharp
using System.Numerics;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

public class ObjectiveCatalogTests
{
    private static Poe2Live.EntityDot Ent(uint id, string metadata, Vector2 grid,
        Poe2Live.EntityCategory cat = Poe2Live.EntityCategory.Monster, bool poi = false,
        Poe2Live.Rarity rarity = Poe2Live.Rarity.Normal)
        => new(id, 0, grid, default, cat, metadata, 1, 1, poi, 0, rarity, false);

    private static Poe2Live.Landmark Lm(string path, Vector2 center)
        => new("name", path, center, 1);

    private static readonly CampaignObjective[] Seed =
    {
        new("event", "Event", "League", 100, true, Metadata: new() { "RunesOfAldur" }),
        new("boss", "Boss", "SideBoss", 80, true, Metadata: new() { "*Ascendancy*" }),
        new("exit", "Continue", "MainProgression", 10, true,
            Categories: new() { "Transition" }),
    };

    [Fact]
    public void Rank_OrdersByPriorityThenDistance()
    {
        var cat = new ObjectiveCatalog(Seed);
        var entities = new[]
        {
            Ent(1, "Metadata/Transition/Door", new Vector2(1, 0), Poe2Live.EntityCategory.Transition), // exit, prio 10, near
            Ent(2, "Metadata/Events/RunesOfAldur", new Vector2(50, 0)),                                 // event, prio 100, far
            Ent(3, "Metadata/Bosses/AscendancyTrial", new Vector2(5, 0)),                               // boss, prio 80
        };
        var ranked = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), Vector2.Zero);
        Assert.Equal(new[] { "e:2", "e:3", "e:1" }, ranked.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Rank_NearestWinsWithinTier()
    {
        var cat = new ObjectiveCatalog(Seed);
        var entities = new[]
        {
            Ent(10, "Metadata/Transition/A", new Vector2(20, 0), Poe2Live.EntityCategory.Transition),
            Ent(11, "Metadata/Transition/B", new Vector2(3, 0), Poe2Live.EntityCategory.Transition),
        };
        var ranked = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), Vector2.Zero);
        Assert.Equal("e:11", ranked[0].Id); // nearer transition wins the MainProgression tier
    }

    [Fact]
    public void Rank_SkipsDisabledAndUnmatched()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            new CampaignObjective("event", "Event", "League", 100, Enabled: false,
                Metadata: new() { "RunesOfAldur" }),
        });
        var entities = new[] { Ent(1, "Metadata/Events/RunesOfAldur", Vector2.Zero) };
        Assert.Empty(cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), Vector2.Zero));
    }

    [Fact]
    public void Rank_MatchesLandmarkByPath()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            new CampaignObjective("arena", "Arena", "Bosses", 70, true,
                LandmarkPath: new() { "*Arena*" }),
        });
        var landmarks = new[] { Lm("Maps/Arena/Boss.tdt", new Vector2(4, 0)) };
        var ranked = cat.Rank(Array.Empty<Poe2Live.EntityDot>(), landmarks, Vector2.Zero);
        Assert.Single(ranked);
        Assert.StartsWith("t:Maps/Arena/Boss.tdt@", ranked[0].Id);
    }

    [Fact]
    public void Rank_KeepsHighestPriorityWhenEntityMatchesMultiple()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            new CampaignObjective("a", "A", "X", 10, true, Metadata: new() { "Boss" }),
            new CampaignObjective("b", "B", "Y", 90, true, Metadata: new() { "Boss" }),
        });
        var entities = new[] { Ent(1, "Metadata/Boss", Vector2.Zero) };
        var ranked = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), Vector2.Zero);
        Assert.Single(ranked);
        Assert.Equal(90, ranked[0].Priority);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test POE2Radar.slnx`
Expected: FAIL — `CampaignObjective`/`ObjectiveCatalog`/`RankedObjective` do not exist (`CS0246`).

- [ ] **Step 3: Implement `CampaignObjective.cs`**

`src/POE2Radar.Core/Campaign/CampaignObjective.cs`:

```csharp
using System.Numerics;
using System.Text.RegularExpressions;
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign;

/// <summary>
/// One catalog objective: a MATCHER over the live entity/landmark data the radar already reads,
/// plus a <see cref="Priority"/> (higher = routed first) and a <see cref="Category"/> label.
/// Matcher fields are ANY-of; an empty/null field is "any". Entity objectives set
/// <see cref="Metadata"/>/<see cref="Categories"/>/<see cref="Poi"/>/<see cref="Rarity"/>; tile
/// objectives set <see cref="LandmarkPath"/>. JSON-serialized (camelCase) by the store.
/// </summary>
public sealed record CampaignObjective(
    string Id,
    string Label,
    string Category,
    int Priority,
    bool Enabled = true,
    List<string>? Metadata = null,    // entity metadata terms (substring, or glob if it has * / ?)
    List<string>? Categories = null,  // EntityCategory names
    string? Poi = null,               // "Yes" | "No"
    string? Rarity = null,            // Normal | Magic | Rare | Unique
    List<string>? LandmarkPath = null // terrain-tile .tdt path terms (substring/glob)
);

/// <summary>A matched, rankable objective in the current zone. <see cref="Id"/> is the stable
/// nav-selection id ("e:&lt;entityId&gt;" / "t:&lt;landmarkKey&gt;").</summary>
public readonly record struct RankedObjective(string Id, string Label, string Category, int Priority, float DistanceSq);

/// <summary>
/// Compiles a set of <see cref="CampaignObjective"/>s and ranks the ones present in the current
/// zone. Pure + allocation-free per-entity matching (mirrors <c>DisplayRules.Compiled</c>): the
/// per-tick allocation is only the small result list. Highest priority first, nearest as tiebreak.
/// </summary>
public sealed class ObjectiveCatalog
{
    private readonly Compiled[] _compiled;

    public ObjectiveCatalog(IEnumerable<CampaignObjective> objectives)
        => _compiled = objectives.Where(o => o.Enabled).Select(o => new Compiled(o)).ToArray();

    public IReadOnlyList<RankedObjective> Rank(
        IReadOnlyList<Poe2Live.EntityDot> entities,
        IReadOnlyList<Poe2Live.Landmark> landmarks,
        Vector2 player)
    {
        var best = new Dictionary<string, RankedObjective>(StringComparer.Ordinal);

        for (var i = 0; i < entities.Count; i++)
        {
            ref readonly var e = ref AsRef(entities, i);
            Compiled? top = null;
            foreach (var c in _compiled)
                if (c.MatchesEntity(in e) && (top is null || c.Obj.Priority > top.Obj.Priority))
                    top = c;
            if (top is null) continue;
            var id = "e:" + e.Id;
            Consider(best, id, top.Obj, Vector2.DistanceSquared(e.Grid, player));
        }

        for (var i = 0; i < landmarks.Count; i++)
        {
            var lm = landmarks[i];
            Compiled? top = null;
            foreach (var c in _compiled)
                if (c.MatchesLandmark(lm.Path) && (top is null || c.Obj.Priority > top.Obj.Priority))
                    top = c;
            if (top is null) continue;
            var id = "t:" + lm.Key;
            Consider(best, id, top.Obj, Vector2.DistanceSquared(lm.Center, player));
        }

        var list = new List<RankedObjective>(best.Values);
        list.Sort((a, b) => a.Priority != b.Priority
            ? b.Priority.CompareTo(a.Priority)        // priority desc
            : a.DistanceSq.CompareTo(b.DistanceSq));   // nearest asc
        return list;
    }

    private static void Consider(Dictionary<string, RankedObjective> best, string id, CampaignObjective o, float distSq)
    {
        if (best.TryGetValue(id, out var cur) && cur.Priority >= o.Priority) return;
        best[id] = new RankedObjective(id, o.Label, o.Category, o.Priority, distSq);
    }

    // Index a List/IReadOnlyList by ref without copying the (largish) EntityDot struct per access.
    private static ref readonly Poe2Live.EntityDot AsRef(IReadOnlyList<Poe2Live.EntityDot> list, int i)
    {
        if (list is List<Poe2Live.EntityDot> l)
            return ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(l)[i];
        _scratch = list[i];
        return ref _scratch;
    }

    [ThreadStatic] private static Poe2Live.EntityDot _scratch;

    private sealed class Compiled
    {
        public readonly CampaignObjective Obj;
        private readonly bool _anyCat;
        private readonly bool[] _catMask = new bool[7]; // EntityCategory has 7 members
        private readonly (string sub, Regex? glob)[]? _meta;
        private readonly bool _anyRarity;
        private readonly Poe2Live.Rarity _rarity;
        private readonly int _poi; // 0 any / 1 Yes / 2 No
        private readonly (string sub, Regex? glob)[]? _landmark;
        private readonly bool _hasEntityMatcher;

        public Compiled(CampaignObjective o)
        {
            Obj = o;
            _anyCat = o.Categories is not { Count: > 0 };
            if (!_anyCat)
                foreach (var c in o.Categories!)
                    if (Enum.TryParse<Poe2Live.EntityCategory>(c, ignoreCase: true, out var ec)) _catMask[(int)ec] = true;
            _meta = Compile(o.Metadata);
            _anyRarity = string.IsNullOrEmpty(o.Rarity);
            _rarity = _anyRarity ? default
                : Enum.TryParse<Poe2Live.Rarity>(o.Rarity, ignoreCase: true, out var rr) ? rr : (Poe2Live.Rarity)int.MaxValue;
            _poi = string.IsNullOrEmpty(o.Poi) ? 0
                 : string.Equals(o.Poi, "Yes", StringComparison.OrdinalIgnoreCase) ? 1
                 : string.Equals(o.Poi, "No", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            _landmark = Compile(o.LandmarkPath);
            _hasEntityMatcher = _meta != null || !_anyCat || _poi != 0 || !_anyRarity;
        }

        public bool MatchesEntity(in Poe2Live.EntityDot e)
        {
            if (!_hasEntityMatcher) return false; // landmark-only objective never matches an entity
            if (!_anyCat) { var ci = (int)e.Category; if ((uint)ci >= (uint)_catMask.Length || !_catMask[ci]) return false; }
            if (_meta != null && !Any(_meta, e.Metadata)) return false;
            if (!_anyRarity && e.Rarity != _rarity) return false;
            if (_poi == 1 && !e.Poi) return false;
            if (_poi == 2 && e.Poi) return false;
            return true;
        }

        public bool MatchesLandmark(string path) => _landmark != null && Any(_landmark, path);

        private static (string, Regex?)[]? Compile(List<string>? terms)
        {
            if (terms is not { Count: > 0 }) return null;
            var arr = terms.Where(t => !string.IsNullOrEmpty(t)).Select(CompileTerm).ToArray();
            return arr.Length == 0 ? null : arr;
        }

        private static (string, Regex?) CompileTerm(string term)
        {
            if (term.IndexOf('*') < 0 && term.IndexOf('?') < 0) return (term, null);
            var rx = "^" + Regex.Escape(term).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return (term, new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        private static bool Any((string sub, Regex? glob)[] terms, string value)
        {
            foreach (var (sub, glob) in terms)
            {
                if (glob != null) { if (glob.IsMatch(value)) return true; }
                else if (value.Contains(sub, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test POE2Radar.slnx`
Expected: all `ObjectiveCatalogTests` pass; whole solution builds 0W/0E.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Campaign/CampaignObjective.cs tests/POE2Radar.Tests/ObjectiveCatalogTests.cs
git commit -m "feat(director): objective catalog matcher + ranking (Core, tested)"
```

---

## Task 2: Catalog store + seed (Overlay)

**Files:**
- Create: `src/POE2Radar.Overlay/Web/CampaignObjectives.cs`
- Create: `src/POE2Radar.Overlay/Web/default_campaign_objectives.json`
- Modify: `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (EmbeddedResource ItemGroup, currently lines 27-29)

**Interfaces:**
- Consumes: `ObjectiveCatalog`, `CampaignObjective` (Task 1).
- Produces: `CampaignObjectives(string filePath)` with `IReadOnlyList<RankedObjective> Rank(entities, landmarks, player)` (delegates to a volatile `ObjectiveCatalog` snapshot), `All`, `Add/Update/Remove`.

- [ ] **Step 1: Create the seed `default_campaign_objectives.json`** (the §6 4-tier flow)

`src/POE2Radar.Overlay/Web/default_campaign_objectives.json`:

```json
[
  { "id": "league-event", "label": "Seasonal event", "category": "League",
    "priority": 100, "enabled": true, "metadata": ["RunesOfAldur"], "poi": "Yes" },
  { "id": "side-boss", "label": "Side boss / passive trial", "category": "SideBoss",
    "priority": 80, "enabled": true, "metadata": ["*Ascendancy*", "*Trial*"] },
  { "id": "side-zone", "label": "Optional side area", "category": "SideZone",
    "priority": 60, "enabled": true, "categories": ["Transition"], "metadata": ["*Optional*", "*Side*"] },
  { "id": "main-progression", "label": "Continue (zone exit)", "category": "MainProgression",
    "priority": 10, "enabled": true, "categories": ["Transition"] }
]
```

- [ ] **Step 2: Register the embedded resource** (`POE2Radar.Overlay.csproj`)

Change the EmbeddedResource ItemGroup from:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Web\default_watched.json" />
  </ItemGroup>
```

to:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Web\default_watched.json" />
    <EmbeddedResource Include="Web\default_campaign_objectives.json" />
  </ItemGroup>
```

- [ ] **Step 3: Create the store `CampaignObjectives.cs`** (modeled on `WatchedEntities.cs`)

`src/POE2Radar.Overlay/Web/CampaignObjectives.cs`:

```csharp
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// User-managed campaign-objective catalog: a priority-ranked list of matchers over the live
/// entity/landmark data, persisted to <c>config/campaign_objectives.json</c> and seeded from the
/// embedded <c>default_campaign_objectives.json</c> on first run. Same lock+snapshot discipline as
/// <see cref="WatchedEntities"/>: mutations under <c>_gate</c>, a volatile immutable
/// <see cref="ObjectiveCatalog"/> snapshot for lock-free <see cref="Rank"/> on the world thread.
/// </summary>
public sealed class CampaignObjectives
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, CampaignObjective> _entries = new(StringComparer.OrdinalIgnoreCase); // under _gate
    private volatile ObjectiveCatalog _snapshot = new(Array.Empty<CampaignObjective>());                     // immutable
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public CampaignObjectives(string filePath)
    {
        _filePath = filePath;
        Load();
        if (_entries.Count == 0) { LoadDefaults(); Save(); }
        Rebuild();
    }

    /// <summary>Rank the objectives present in the current zone (lock-free: reads the snapshot).</summary>
    public IReadOnlyList<RankedObjective> Rank(
        IReadOnlyList<Poe2Live.EntityDot> entities, IReadOnlyList<Poe2Live.Landmark> landmarks, Vector2 player)
        => _snapshot.Rank(entities, landmarks, player);

    public IReadOnlyList<CampaignObjective> All { get { lock (_gate) return _entries.Values.ToArray(); } }

    public void Add(CampaignObjective o)
    {
        if (o is null || string.IsNullOrWhiteSpace(o.Id)) return;
        lock (_gate) { _entries[o.Id] = o; Rebuild(); Save(); }
    }

    public void Remove(string id)
    {
        lock (_gate) { if (_entries.Remove(id)) { Rebuild(); Save(); } }
    }

    /// <summary>Rebuild the immutable catalog snapshot. Call under <see cref="_gate"/>.</summary>
    private void Rebuild() => _snapshot = new ObjectiveCatalog(_entries.Values.ToArray());

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<CampaignObjective>>(File.ReadAllText(_filePath), Json);
            if (list == null) return;
            foreach (var o in list)
                if (!string.IsNullOrWhiteSpace(o.Id)) _entries[o.Id] = o;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Campaign objectives load failed: {ex.Message}"); }
    }

    private void LoadDefaults()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("default_campaign_objectives"));
            if (resName == null) return;
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) return;
            using var sr = new StreamReader(stream);
            var list = JsonSerializer.Deserialize<List<CampaignObjective>>(sr.ReadToEnd(), Json);
            if (list == null) return;
            foreach (var o in list)
                if (!string.IsNullOrWhiteSpace(o.Id)) _entries[o.Id] = o;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Campaign objectives defaults failed: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries.Values.ToList(), Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Campaign objectives save failed: {ex.Message}"); }
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build POE2Radar.slnx`
Expected: 0W/0E. (The store isn't unit-tested — it's thin I/O cloned from the proven `WatchedEntities`; its `Rank` delegates to the Task-1-tested catalog. Build + the manual smoke in Task 5 cover it.)

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Web/CampaignObjectives.cs src/POE2Radar.Overlay/Web/default_campaign_objectives.json src/POE2Radar.Overlay/POE2Radar.Overlay.csproj
git commit -m "feat(director): campaign-objectives store + seed catalog"
```

---

## Task 3: Director decision state machine (Core, TDD)

**Files:**
- Create: `src/POE2Radar.Core/Campaign/ObjectiveDirector.cs`
- Test: `tests/POE2Radar.Tests/ObjectiveDirectorTests.cs`

**Interfaces:**
- Consumes: `RankedObjective` (Task 1).
- Produces:
  - `readonly record struct DirectorDecision(bool ChangeSelection, string? DesiredActiveId)`.
  - `ObjectiveDirector` with `DirectorDecision Decide(IReadOnlyList<RankedObjective> ranked, IReadOnlyList<string> currentSelectedIds)`, `void ResetZone()`, and `IReadOnlyList<RankedObjective> Queue { get; }`.

The director owns the selection only when it's empty or exactly `[its last active id]`; otherwise (a manual pick) it stands down. It never reorders an existing exactly-`[desired]` selection.

- [ ] **Step 1: Write the failing tests**

`tests/POE2Radar.Tests/ObjectiveDirectorTests.cs`:

```csharp
using POE2Radar.Core.Campaign;

public class ObjectiveDirectorTests
{
    private static RankedObjective R(string id, int prio) => new(id, id, "c", prio, 0f);
    private static readonly string[] None = System.Array.Empty<string>();

    [Fact]
    public void SelectsTop_WhenSelectionEmpty()
    {
        var d = new ObjectiveDirector();
        var dec = d.Decide(new[] { R("e:1", 100), R("e:2", 10) }, None);
        Assert.True(dec.ChangeSelection);
        Assert.Equal("e:1", dec.DesiredActiveId);
    }

    [Fact]
    public void NoChange_WhenAlreadyOnTop()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100) }, None);            // selects e:1
        var dec = d.Decide(new[] { R("e:1", 100) }, new[] { "e:1" });
        Assert.False(dec.ChangeSelection);
    }

    [Fact]
    public void Advances_WhenActiveCompletes()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100), R("e:2", 80) }, None); // active e:1
        // e:1 completed → no longer ranked; selection cleared by PruneCompletedTargets → empty
        var dec = d.Decide(new[] { R("e:2", 80) }, None);
        Assert.True(dec.ChangeSelection);
        Assert.Equal("e:2", dec.DesiredActiveId);
    }

    [Fact]
    public void StandsDown_OnManualOverride()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100) }, None);                 // active e:1
        var dec = d.Decide(new[] { R("e:1", 100) }, new[] { "e:9" }); // user picked e:9
        Assert.False(dec.ChangeSelection);
    }

    [Fact]
    public void StandsDown_WhenUserAddedSecondTarget()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100) }, None);
        var dec = d.Decide(new[] { R("e:1", 100) }, new[] { "e:1", "e:7" });
        Assert.False(dec.ChangeSelection);
    }

    [Fact]
    public void ResetZone_LetsDirectorReacquire()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100) }, new[] { "e:9" }); // stood down (manual)
        d.ResetZone();
        var dec = d.Decide(new[] { R("e:5", 100) }, None);  // new zone, empty selection
        Assert.True(dec.ChangeSelection);
        Assert.Equal("e:5", dec.DesiredActiveId);
    }

    [Fact]
    public void ClearsToEmpty_WhenNothingRankedAndDirectorOwns()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100) }, None);  // active e:1
        var dec = d.Decide(None, new[] { "e:1" }); // nothing present now
        Assert.True(dec.ChangeSelection);
        Assert.Null(dec.DesiredActiveId);
    }

    [Fact]
    public void Queue_ExposesRanked()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100), R("e:2", 80) }, None);
        Assert.Equal(new[] { "e:1", "e:2" }, d.Queue.Select(q => q.Id).ToArray());
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test POE2Radar.slnx`
Expected: FAIL — `ObjectiveDirector`/`DirectorDecision` do not exist.

- [ ] **Step 3: Implement `ObjectiveDirector.cs`**

`src/POE2Radar.Core/Campaign/ObjectiveDirector.cs`:

```csharp
namespace POE2Radar.Core.Campaign;

/// <summary>Outcome of one director evaluation: whether the nav selection should change, and to
/// which single active objective id (null = clear).</summary>
public readonly record struct DirectorDecision(bool ChangeSelection, string? DesiredActiveId);

/// <summary>
/// Per-zone objective director (pure). Each evaluation ranks → picks the single top objective as the
/// active route, but only when it "owns" the selection: the selection is empty, or exactly the id the
/// director last set. Any other selection is a manual override and the director stands down until the
/// selection returns to its control or <see cref="ResetZone"/> is called (on zone change).
/// </summary>
public sealed class ObjectiveDirector
{
    private string? _lastActiveId;

    /// <summary>The most recently ranked objectives (active first), for the dashboard panel.</summary>
    public IReadOnlyList<RankedObjective> Queue { get; private set; } = System.Array.Empty<RankedObjective>();

    /// <summary>Forget the active objective so the director re-acquires on the next evaluation
    /// (call on zone change, alongside the selection clear).</summary>
    public void ResetZone() => _lastActiveId = null;

    public DirectorDecision Decide(IReadOnlyList<RankedObjective> ranked, IReadOnlyList<string> currentSelectedIds)
    {
        Queue = ranked;
        var desired = ranked.Count > 0 ? ranked[0].Id : null;

        var owns = currentSelectedIds.Count == 0
            || (currentSelectedIds.Count == 1 && currentSelectedIds[0] == _lastActiveId);
        if (!owns) return new DirectorDecision(false, null);

        // Already exactly on the desired selection? nothing to do.
        var alreadyThere = desired == null
            ? currentSelectedIds.Count == 0
            : currentSelectedIds.Count == 1 && currentSelectedIds[0] == desired;
        if (alreadyThere) return new DirectorDecision(false, desired);

        _lastActiveId = desired;
        return new DirectorDecision(true, desired);
    }
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test POE2Radar.slnx`
Expected: all `ObjectiveDirectorTests` pass.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Campaign/ObjectiveDirector.cs tests/POE2Radar.Tests/ObjectiveDirectorTests.cs
git commit -m "feat(director): selection state machine with manual-override + advance (Core, tested)"
```

---

## Task 4: Wire the director into RadarApp + settings flag

**Files:**
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs` (after line 30, the display toggles)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (ctor ~256; fields ~212; `OnAreaChanged` 1331-1364; `WorldTick` ~1019-1024)

**Interfaces:**
- Consumes: `CampaignObjectives` (Task 2), `ObjectiveDirector` (Task 3).
- Produces: a `_directorQueue` (`volatile IReadOnlyList<RankedObjective>`) for Task 5's dashboard payload; director-driven `_selectedIds` when `EnableDirector`.

- [ ] **Step 1: Add the settings flag** (`RadarSettings.cs`)

After `public bool ShowPlayerBlip { get; set; } = true;` add:

```csharp
    // Objective Director: when on, auto-select + route to the highest-priority in-zone objective
    // (catalog-ranked), advancing as objectives complete. Read-only — only changes the nav selection.
    public bool EnableDirector { get; set; } = false;
```

- [ ] **Step 2: Add fields in `RadarApp.cs`** (next to `_selectedIds`, ~line 215)

```csharp
    private readonly CampaignObjectives _campaign;
    private readonly POE2Radar.Core.Campaign.ObjectiveDirector _director = new();
```
(The `_directorQueue` publish field is added in Task 5, where it is read — adding it here would be an assigned-but-never-read field and fail the build under `TreatWarningsAsErrors`.)

- [ ] **Step 3: Construct the store in the ctor** (`RadarApp.cs`, after the `_watched = new WatchedEntities(...)` line ~256)

```csharp
        _campaign = new CampaignObjectives(Path.Combine(ConfigDir, "campaign_objectives.json"));
```

- [ ] **Step 4: Gate the `OnAreaChanged` auto-add + reset the director on zone change**

In `OnAreaChanged` (the first-visit `else` branch, ~1353-1365), the auto-add loop must not run when the director owns selection, and the director must forget its active id on zone change. Change the `else` block from:

```csharp
            else
            {
                // First visit to this instance: auto-select every target whose display rule opted into
                // auto-pathing (the per-rule "Auto-path" flag), capped so colors/planning stay bounded.
                foreach (var t in _navTargets)
                {
                    if (_selectedIds.Count >= MaxSelectedTargets) break;
                    if (t.AutoPath && !_selectedIds.Contains(t.Id))
                        _selectedIds.Add(t.Id);
                }
            }
```

to:

```csharp
            else if (!_settings.EnableDirector)
            {
                // First visit to this instance: auto-select every target whose display rule opted into
                // auto-pathing (the per-rule "Auto-path" flag), capped so colors/planning stay bounded.
                foreach (var t in _navTargets)
                {
                    if (_selectedIds.Count >= MaxSelectedTargets) break;
                    if (t.AutoPath && !_selectedIds.Contains(t.Id))
                        _selectedIds.Add(t.Id);
                }
            }
            // else: the Objective Director owns auto-selection this zone (DirectorReconcile, WorldTick).
```

Then, immediately after the `lock (_navLock) { ... }` block in `OnAreaChanged` (before `_selectedPaths = new List<SelectedPath>();`), add:

```csharp
        _director.ResetZone();
```

- [ ] **Step 5: Add the reconcile call in `WorldTick`** (between `PruneCompletedTargets();` at ~1019 and `MaintainRoutes(player);` at ~1024)

```csharp
        // Objective Director: when enabled, rank the zone's catalog objectives and (if the director
        // owns the selection) route to the top one. Read-only — only edits _selectedIds.
        if (_settings.EnableDirector) DirectorReconcile(player);
```

- [ ] **Step 6: Add the `DirectorReconcile` method** (place near `OnAreaChanged`, e.g. after it)

```csharp
    /// <summary>
    /// Rank the catalog objectives present in the current zone and, when the director owns the
    /// selection (empty or exactly its last active id), set the route to the single top objective.
    /// Reuses the existing id-selection → routing pipeline; never builds a path itself. Read-only.
    /// </summary>
    private void DirectorReconcile(NumVec2 player)
    {
        var ranked = _campaign.Rank(_entities, _landmarks, player);
        lock (_navLock)
        {
            var decision = _director.Decide(ranked, _selectedIds);
            if (decision.ChangeSelection)
            {
                _selectedIds.Clear();
                _selectionCapWarned = false;
                if (decision.DesiredActiveId != null) _selectedIds.Add(decision.DesiredActiveId);
            }
        }
        // The ranked queue lives in _director.Queue; Task 5 publishes it to a field for the dashboard.
    }
```

- [ ] **Step 7: Build + gate**

Run: `dotnet build POE2Radar.slnx`
Expected: 0W/0E. (The ranked queue is held in `_director.Queue`; it isn't published to a field until Task 5, so there is no assigned-but-never-read field here.)
Run: `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1`
Expected: PASS (no input/write symbols added).

- [ ] **Step 8: Commit**

```bash
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(director): wire director into WorldTick + gate AutoPath when enabled"
```

---

## Task 5: Dashboard + API surface (toggle, /state payload, panel)

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (RadarState 881-910; `/state` 138-167; `ReadSettings` 454-477; `ApplySettings` ~493-508)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (the `_state = new RadarState(...)` site, 820-822)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (settings card 508-524; sidebar ~390-393; renderState ~1318-1333)

**Interfaces:**
- Consumes: `_director.Queue` + `EnableDirector` (Task 4). Declares + publishes the `_directorQueue` field here (where it is first read).

- [ ] **Step 1: Add `Director` to the `RadarState` record** (`ApiServer.cs`, after the `Monoliths` optional param, before `float Fps = 0`)

```csharp
    // Objective Director queue (active objective first) for the dashboard panel; null/empty when off.
    IReadOnlyList<POE2Radar.Core.Campaign.RankedObjective>? Director = null,
```

(It's optional/defaulted, so `RadarState.Empty` needs no change. Place it before `float Fps = 0` to keep `Fps` last, matching the existing construction-site order.)

- [ ] **Step 2: Declare + publish the queue field, then pass it at the construction site** (`RadarApp.cs`)

First add the publish field next to `_director` (~line 215):

```csharp
    private volatile IReadOnlyList<POE2Radar.Core.Campaign.RankedObjective> _directorQueue =
        Array.Empty<POE2Radar.Core.Campaign.RankedObjective>();
```

Then publish the ranked queue at the end of `DirectorReconcile` (replacing the Task-4 placeholder comment after the `lock` block):

```csharp
        _directorQueue = _director.Queue;
```

Then change the `_state = new RadarState(...)` construction (820-822):

```csharp
        _state = new RadarState(inGame, snap.AreaHash, snap.AreaLevel, map.IsVisible, map.Zoom, player,
            snap.Entities, snap.Landmarks, _hpPct, _manaPct, _esPct,
            snap.AreaCode, "", snap.CharLevel, _worldMs, _renderMs, mr.Markers, _directorQueue, _fps);
```

(`Director` is the param immediately before `Fps`, mirroring its record position.)

- [ ] **Step 3: Project it into `/state`** (`ApiServer.cs`, inside the `/state` anonymous object, after the `monoliths = (...)` projection)

```csharp
                    // Objective Director: active objective + queue for the dashboard panel (read-only).
                    director = (s.Director ?? Array.Empty<POE2Radar.Core.Campaign.RankedObjective>())
                        .Select(o => new { id = o.Id, label = o.Label, category = o.Category, priority = o.Priority }),
```

- [ ] **Step 4: Round-trip the toggle** (`ApiServer.cs`)

In `ReadSettings()` add (alongside `showPlayerBlip`):

```csharp
        enableDirector = _settings.EnableDirector,
```

In `ApplySettings()` add (alongside the `showPlayerBlip` case):

```csharp
                case "enableDirector" when TryBool(p.Value, out var b): _settings.EnableDirector = b; applied.Add(p.Name); break;
```

- [ ] **Step 5: Add the settings toggle row** (`DashboardHtml.cs`, inside the `Radar Display` card, after the `showPath` row)

```html
            <div class="row"><div class="rl">Objective Director<small>auto-route campaign objectives: event &rarr; bosses &rarr; side zones &rarr; exit</small></div>
              <label class="sw"><input type="checkbox" data-set="enableDirector"><span class="track"></span><span class="knob"></span></label></div>
```

(No JS change: `loadSettings()`/`wireSettings()` handle any `data-set` checkbox generically.)

- [ ] **Step 6: Add the sidebar card** (`DashboardHtml.cs`, next to `monoCard` ~393)

```html
      <div id="dirCard" hidden>
        <div class="sect">Objective Director</div>
        <div id="dirList" class="znotes" style="display:block"></div>
      </div>
```

- [ ] **Step 7: Render it** (`DashboardHtml.cs`, in `renderState()`, next to the monolith block ~1333)

```javascript
  // Objective Director (from /state): the active objective then the queued ones, priority order.
  const dc=$('#dirCard'), dl=$('#dirList');
  const dir=(s.director||[]);
  if(dir.length){
    dc.hidden=false;
    dl.innerHTML = dir.map((o,i)=>
      '<div style="display:flex;justify-content:space-between;gap:8px'+(i===0?';font-weight:600':';opacity:.75')+'">'
      + '<span>'+(i===0?'▶ ':'')+esc(o.label)+'</span>'
      + '<span style="opacity:.7">'+esc(o.category)+'</span></div>').join('');
  } else { dc.hidden=true; dl.innerHTML=''; }
```

- [ ] **Step 8: Build + gate + test**

Run: `dotnet build POE2Radar.slnx` → 0W/0E.
Run: `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1` → PASS.
Run: `dotnet test POE2Radar.slnx` → all pass.

- [ ] **Step 9: Manual smoke (no game needed)**

Run the overlay with PoE2 closed (it exits "Game not running" — fine) OR start it, open `http://localhost:7777`, Settings → toggle "Objective Director" on/off, confirm it persists (GET `/api/settings` shows `enableDirector`). Full in-game behavior is the release-checklist item.

- [ ] **Step 10: Commit**

```bash
git add src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(director): dashboard toggle, /state queue payload, sidebar panel"
```

---

## Task 6: Docs — upstream-merge + release checklist

**Files:**
- Modify: `docs/upstream-merge.md`
- Modify: `docs/release-checklist.md`

- [ ] **Step 1: Add the director hook sites to `docs/upstream-merge.md`** (under "What POE2GPS adds on top of Sikaka")

```markdown
- **Objective Director** (`Core/Campaign/CampaignObjective.cs`, `ObjectiveDirector.cs`,
  `Overlay/Web/CampaignObjectives.cs` + `default_campaign_objectives.json`). Shared-file hooks to
  re-apply on merge: `RadarSettings.EnableDirector`; the `RadarApp` ctor store construction; the
  `!_settings.EnableDirector` gate on the `OnAreaChanged` AutoPath auto-add + the `_director.ResetZone()`
  call; the `DirectorReconcile(player)` call in `WorldTick` (after `PruneCompletedTargets`, before
  `MaintainRoutes`); the `RadarState.Director` field + its construction arg + the `/state` `director`
  projection; the `enableDirector` settings round-trip; the dashboard toggle row + `dirCard` + render block.
```

- [ ] **Step 2: Add a release-checklist item to `docs/release-checklist.md`** (under the manual live-game section)

```markdown
- [ ] **Objective Director:** enable it in the dashboard; enter a zone containing a catalog objective
      (seasonal event / side boss / transition) and confirm the overlay auto-routes to the
      highest-priority one, advances when it's completed, and falls back to the zone exit once optional
      content is cleared. Confirm a manual target pick (F6) overrides it until the next zone, and that
      `/state` carries no character name.
```

- [ ] **Step 3: Commit**

```bash
git add docs/upstream-merge.md docs/release-checklist.md
git commit -m "docs(director): upstream-merge hook list + release-checklist item"
```

---

## Self-Review

**Spec coverage:**
- §2 invariants (read-only, reuse routing, sync-safe footprint, no identifying data) → Tasks 1-5 use only id-selection; gate runs in Tasks 4-5; payload excludes char name (Step 5.3 uses only id/label/category/priority). ✓
- §3 integration (id-selection, AutoPath analog, WorldTick hook, match surface, store pattern, settings/dashboard) → Tasks 2/4/5 use the verified sites verbatim. ✓
- §4 data flow (rank → set selection → reuse MaintainRoutes) → Task 4 `DirectorReconcile`. ✓
- §5 components (4 new files + the listed edits) → Tasks 1-5; the spec's "ObjectiveDirector.cs in Overlay/Navigation" is placed in `Core/Campaign` instead so it's unit-testable (the reconcile/locking stays in RadarApp). ✓ (documented deviation)
- §6 catalog + default flow (seasonal 100 → side boss 80 → side zone 60 → main progression 10 catch-all) → Task 2 seed + Task 1 ranking. ✓
- §7 logic (rank, top-1 select, advance, manual override, supersede AutoPath) → Tasks 1/3/4. ✓
- §8 UX (toggle, route reuse, panel) → Task 5. ✓
- §9 compliance/sync → gate in 4/5, hooks doc in Task 6. ✓
- §10 testing (catalog + director unit tests; manual live item) → Tasks 1, 3, 6. ✓

**Placeholder scan:** none — every step has full code or exact edits. The two grep-free edits (OnAreaChanged gate, WorldTick insert) show verbatim before/after.

**Type consistency:** `CampaignObjective`, `RankedObjective`, `ObjectiveCatalog.Rank`, `ObjectiveDirector.Decide`/`Queue`/`ResetZone`, `DirectorDecision` are defined in Tasks 1/3 and used identically in Tasks 2/4/5. The nav-selection id format (`e:<id>` / `t:<key>`) matches `TryResolveTargetGrid`. `RadarState.Director` (Task 5.1) matches the construction arg (5.2) and `/state` read (5.3).

---

## Notes for the implementer

- **Live in-game behavior is not CI-testable** — the unit tests cover ranking + the decision state machine; routing/drawing is the existing (already-shipped) pipeline. Use the Task 6 release-checklist item for the rest.
- **Line numbers are from plan-writing time** — anchor edits on the verbatim code shown, not raw line numbers (Task 4/5 sites are in the 2400-line `RadarApp.cs`).
- **Never add an input or memory-write API.** The director only edits `_selectedIds` (a list of strings) under `_navLock` and reads state. If a build error tempts you toward anything else, stop.
- **Detection coverage caveat (from spec §6/§11):** an objective only routes if its entity/landmark is in the live set. Verify the seed catalog's patterns (`RunesOfAldur`, `*Ascendancy*`, `Transition`) actually match in-game during the release check; adjust the seed JSON (data only) as needed — no code change.
