# Campaign GPS (Quest-aware Director, Part B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn-by-turn cross-zone campaign navigation — point the player to the next critical-path zone and route them to the in-zone exit that leads there, reusing the existing nav/A* pipeline.

**Architecture:** A pure Core engine (`CampaignGps`) consumes an `IQuestProgress` abstraction over a hand-authored `campaign_route.json` (current-zone → next-zone + exit hint). v1 implementation `ZoneOrderProgress` infers position from the current zone code + a forward-only latch (zero memory reads). `RadarApp` calls it each world tick (gated on `EnableCampaignGps`), sets the campaign-forward exit as the active nav target via the existing `SetActiveTarget`, and publishes an instruction string to the dashboard. The quest-completion memory read is a separate, gated `IQuestProgress` implementation built only after the in-game probe pins the offsets — the feature ships fully without it.

**Tech Stack:** .NET 10, C# (net10.0-windows, x64), xUnit, embedded-JSON resources, vanilla-JS dashboard in `DashboardHtml.cs`.

## Global Constraints

- **Strictly read-only.** No `SendInput`/`PostMessage`/`SendMessage`/`keybd_event`/`WriteProcessMemory`/injection. No forbidden symbol names anywhere (incl. comments). All additions are reads / offline data.
- **The isolation principle:** the zone-order GPS (Tasks 1–6) is the shipping deliverable and never depends on the quest-memory read. The quest read is a separate gated `IQuestProgress` impl (post-session section) behind `EnableQuestMemory` (default false); no quest-state stub ships enabled.
- **Nav id format:** the existing pipeline resolves selection ids of the form `t:<landmarkKey>` (tile landmark) and `e:<entityId>` (entity). `CampaignGps` emits exactly these; `SetActiveTarget(id)` consumes them.
- **S4 of zone identity:** the player's current zone is the validated `AreaInfo` code (`AreaCode(areaInstance)`), already read each world tick.
- **Both new toggles default OFF (experimental):** `EnableCampaignGps`, `EnableQuestMemory`.
- **Data is a draft for review:** `campaign_route.json` is hand-authored from in-repo sources; correctness is the user's review gate (the spec says so).
- **`tests/POE2Radar.Tests` references Core only** — `CampaignRoute` / `ZoneOrderProgress` / `CampaignGps` live in Core and are unit-tested; `MemoryQuestProgress` (post-session) is integration/probe-validated.
- CI green: `compliance-gate.ps1`, `scrub-strings.ps1 -SelfTest`, full xUnit suite.

---

### Task 1: `campaign_route.json` + `CampaignRoute` loader (Core, pure)

**Files:**
- Create: `src/POE2Radar.Core/Game/campaign_route.json`
- Modify: `src/POE2Radar.Core/POE2Radar.Core.csproj` (register the embedded resource)
- Create: `src/POE2Radar.Core/Game/CampaignRoute.cs`
- Test: `tests/POE2Radar.Tests/CampaignRouteTests.cs`

**Interfaces:**
- Produces: `readonly record struct CampaignStep(string Zone, int Act, string Name, string? Next, string? ExitHint)`; `class CampaignRoute` with `static CampaignRoute Shared`, `static CampaignRoute FromJson(string json)`, `IReadOnlyList<CampaignStep> Steps`, `CampaignStep? StepFor(string zoneCode)`, `CampaignStep? NextStep(CampaignStep step)`, `int IndexOf(string zoneCode)`, `string? CodeForName(string name)`.

- [ ] **Step 1: Author `campaign_route.json` (draft)**

A JSON **array** of steps in critical-path order. Author it from the in-repo sources — primarily `src/POE2Radar.Core/Game/zone_notes.json` (its per-zone `notes` contain explicit `"Exit > <Zone>"` traversal prose — the authoritative ordering), cross-checked against `src/POE2Radar.Core/Game/world_areas.json` (names + codes) and `src/POE2Radar.Core/Game/CustomLandmarks.json` (the curated exit *labels* per zone, which become `exitHint`). Cover every campaign zone (G\*/P\* codes) across all acts; for the Act-6 interlude branches (P1/P2/P3) author each branch in order. `exitHint` is the **friendly destination name** of the exit that leads to `next` (match it to a `CustomLandmarks` label so `CampaignGps` can find the exit); set it `null` where no curated label exists. Set `next: null` at an act's final zone / campaign end. Where an edge is uncertain, leave `exitHint: null` (the engine falls back to the generic exit) — do not guess. Schema + the first two verified entries:

```json
[
  { "zone": "G1_1", "act": 1, "name": "The Riverbank", "next": "G1_2", "exitHint": "Clearfell" },
  { "zone": "G1_2", "act": 1, "name": "Clearfell", "next": "G1_4", "exitHint": "The Grelwood" }
]
```

> Note: `world_areas.json`'s `act` field is unreliable for some zones — use the `zone_notes.json` traversal order for sequencing and set `act` from the well-known campaign act (Act 1 = G1\_\*, etc.), not blindly from `world_areas`. This file is a **draft for the user to review**; flag any uncertain sequencing inline is not possible in JSON, so list uncertain edges in your task report.

- [ ] **Step 2: Register the embedded resource**

In `src/POE2Radar.Core/POE2Radar.Core.csproj`, inside the existing `<ItemGroup>` that lists the `Game\*.json` resources (alongside `world_areas.json`), add:

```xml
    <EmbeddedResource Include="Game\campaign_route.json" />
```

- [ ] **Step 3: Write the failing tests**

Create `tests/POE2Radar.Tests/CampaignRouteTests.cs`:

```csharp
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class CampaignRouteTests
{
    // A tiny fixture so the loader logic is tested independently of the shipped (draft) data.
    private const string Json = """
    [
      { "zone": "Z1", "act": 1, "name": "Zone One", "next": "Z2", "exitHint": "Zone Two" },
      { "zone": "Z2", "act": 1, "name": "Zone Two", "next": "Z3", "exitHint": null },
      { "zone": "Z3", "act": 2, "name": "Zone Three", "next": null, "exitHint": null }
    ]
    """;
    private static CampaignRoute R() => CampaignRoute.FromJson(Json);

    [Fact] public void StepFor_returns_the_matching_step()
    {
        var s = R().StepFor("Z2");
        Assert.NotNull(s);
        Assert.Equal("Zone Two", s!.Value.Name);
        Assert.Equal("Z3", s.Value.Next);
    }

    [Fact] public void StepFor_is_case_insensitive_and_null_on_miss()
    {
        Assert.NotNull(R().StepFor("z1"));
        Assert.Null(R().StepFor("nope"));
    }

    [Fact] public void NextStep_resolves_the_next_code()
    {
        var r = R();
        var s1 = r.StepFor("Z1")!.Value;
        Assert.Equal("Zone Two", r.NextStep(s1)!.Value.Name);
        var s3 = r.StepFor("Z3")!.Value;
        Assert.Null(r.NextStep(s3));   // next == null → campaign end
    }

    [Fact] public void IndexOf_returns_ordinal_or_minus_one()
    {
        var r = R();
        Assert.Equal(0, r.IndexOf("Z1"));
        Assert.Equal(2, r.IndexOf("Z3"));
        Assert.Equal(-1, r.IndexOf("nope"));
    }

    [Fact] public void CodeForName_reverse_maps_name_to_code_case_insensitively()
    {
        var r = R();
        Assert.Equal("Z2", r.CodeForName("Zone Two"));
        Assert.Equal("Z2", r.CodeForName("zone two"));
        Assert.Null(r.CodeForName("Unknown"));
    }

    [Fact] public void Shared_loads_the_embedded_table_nonempty()
    {
        Assert.True(CampaignRoute.Shared.Steps.Count > 0);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter CampaignRouteTests`
Expected: FAIL — `CampaignRoute` / `CampaignStep` do not exist.

- [ ] **Step 5: Implement `CampaignRoute.cs`**

Create `src/POE2Radar.Core/Game/CampaignRoute.cs` (mirrors the `ZoneGuide` embedded-resource pattern):

```csharp
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

/// <summary>One critical-path campaign step: a zone, the next zone to head to, and the friendly name
/// of the exit that leads there (matched against the curated CustomLandmarks exit labels). Pure data.</summary>
public readonly record struct CampaignStep(string Zone, int Act, string Name, string? Next, string? ExitHint);

/// <summary>
/// Static campaign critical-path route, loaded once from the embedded <c>campaign_route.json</c>
/// (mirrors <see cref="ZoneGuide"/>). Maps the player's current zone code to where they should go next.
/// Read-only; no memory access. The quest-completion read (if added later) refines the inferred step
/// but this table is the always-available baseline.
/// </summary>
public sealed class CampaignRoute
{
    private readonly List<CampaignStep> _steps = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);   // zone code → ordinal
    private readonly Dictionary<string, string> _nameToCode = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CampaignStep> Steps => _steps;

    /// <summary>The shared route, loaded once from the embedded table.</summary>
    public static CampaignRoute Shared { get; } = LoadEmbedded();

    public CampaignStep? StepFor(string zoneCode) =>
        _index.TryGetValue(zoneCode, out var i) ? _steps[i] : null;

    public CampaignStep? NextStep(CampaignStep step) =>
        step.Next is { } n ? StepFor(n) : null;

    public int IndexOf(string zoneCode) => _index.TryGetValue(zoneCode, out var i) ? i : -1;

    /// <summary>Reverse map: a route zone's friendly name → its code (for matching curated exit labels
    /// back to a destination code). Null on miss. Case-insensitive.</summary>
    public string? CodeForName(string name) => _nameToCode.TryGetValue(name, out var c) ? c : null;

    /// <summary>Parse a route from a JSON array of steps. Used by <see cref="Shared"/> and by tests.</summary>
    public static CampaignRoute FromJson(string json)
    {
        var route = new CampaignRoute();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return route;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var zone = Str(e, "zone");
            if (zone.Length == 0) continue;
            var step = new CampaignStep(
                Zone: zone,
                Act: e.TryGetProperty("act", out var a) && a.TryGetInt32(out var ai) ? ai : 0,
                Name: Str(e, "name"),
                Next: NullableStr(e, "next"),
                ExitHint: NullableStr(e, "exitHint"));
            route._index[zone] = route._steps.Count;
            route._steps.Add(step);
            if (step.Name.Length > 0) route._nameToCode[step.Name] = zone;   // first wins on dup names
        }
        return route;
    }

    private static CampaignRoute LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("campaign_route"));
            if (name == null) return new CampaignRoute();
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return new CampaignRoute();
            using var r = new StreamReader(s);
            return FromJson(r.ReadToEnd());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CampaignRoute load failed: {ex.Message}");
            return new CampaignRoute();
        }
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? NullableStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter CampaignRouteTests`
Expected: PASS (6/6).

- [ ] **Step 7: Commit**

```bash
git add src/POE2Radar.Core/Game/campaign_route.json src/POE2Radar.Core/POE2Radar.Core.csproj src/POE2Radar.Core/Game/CampaignRoute.cs tests/POE2Radar.Tests/CampaignRouteTests.cs
git commit -m "feat(gps): campaign_route.json + CampaignRoute loader + tests"
```

---

### Task 2: `IQuestProgress` + `ZoneOrderProgress` (Core, pure)

**Files:**
- Create: `src/POE2Radar.Core/Campaign/QuestProgress.cs`
- Test: `tests/POE2Radar.Tests/ZoneOrderProgressTests.cs`

**Interfaces:**
- Consumes: `CampaignRoute`, `CampaignStep` (Task 1).
- Produces: `interface IQuestProgress { CampaignStep CurrentStep(string currentZoneCode); }`; `sealed class ZoneOrderProgress : IQuestProgress` with `ZoneOrderProgress(CampaignRoute route)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/POE2Radar.Tests/ZoneOrderProgressTests.cs`:

```csharp
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class ZoneOrderProgressTests
{
    private const string Json = """
    [
      { "zone": "Z1", "act": 1, "name": "Zone One",   "next": "Z2", "exitHint": "Zone Two" },
      { "zone": "Z2", "act": 1, "name": "Zone Two",   "next": "Z3", "exitHint": "Zone Three" },
      { "zone": "Z3", "act": 2, "name": "Zone Three", "next": null, "exitHint": null }
    ]
    """;
    private static ZoneOrderProgress P() => new(CampaignRoute.FromJson(Json));

    [Fact] public void Current_zone_on_path_becomes_the_target()
    {
        var p = P();
        Assert.Equal("Z2", p.CurrentStep("Z2").Zone);
    }

    [Fact] public void Latch_advances_forward_only_and_does_not_rewind_on_backtrack()
    {
        var p = P();
        Assert.Equal("Z2", p.CurrentStep("Z2").Zone);   // advance to Z2
        Assert.Equal("Z2", p.CurrentStep("Z1").Zone);   // backtrack to Z1 → target stays the furthest (Z2)
    }

    [Fact] public void Off_path_zone_returns_the_latched_target()
    {
        var p = P();
        p.CurrentStep("Z2");                            // latch at Z2
        Assert.Equal("Z2", p.CurrentStep("SideZoneX").Zone);  // unknown zone → latched target
    }

    [Fact] public void Initial_target_is_the_first_step()
    {
        Assert.Equal("Z1", P().CurrentStep("UnknownStart").Zone);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter ZoneOrderProgressTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement `QuestProgress.cs`**

Create `src/POE2Radar.Core/Campaign/QuestProgress.cs`:

```csharp
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign;

/// <summary>Abstraction over "which campaign step is the player currently working toward". The
/// isolation seam between the always-available zone-order inference and the optional, gated
/// quest-completion memory read. Pure — no memory access in the interface contract.</summary>
public interface IQuestProgress
{
    /// <summary>The step the player should be heading toward, given their current zone.</summary>
    CampaignStep CurrentStep(string currentZoneCode);
}

/// <summary>
/// v1 progress: infer the campaign step from the current zone + a forward-only latch. Entering a
/// later critical-path zone advances the latch; backtracking or wandering off-path keeps pointing at
/// the furthest reached step. Zero memory reads. Stateful (the latch) — one instance per session,
/// touched only by the world thread.
/// </summary>
public sealed class ZoneOrderProgress : IQuestProgress
{
    private readonly CampaignRoute _route;
    private int _furthest;   // ordinal of the furthest critical-path step reached

    public ZoneOrderProgress(CampaignRoute route) => _route = route;

    public CampaignStep CurrentStep(string currentZoneCode)
    {
        var idx = _route.IndexOf(currentZoneCode);
        if (idx > _furthest) _furthest = idx;   // advance forward only
        var steps = _route.Steps;
        if (steps.Count == 0) return default;
        return steps[Math.Min(_furthest, steps.Count - 1)];
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter ZoneOrderProgressTests`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Campaign/QuestProgress.cs tests/POE2Radar.Tests/ZoneOrderProgressTests.cs
git commit -m "feat(gps): IQuestProgress + ZoneOrderProgress latch + tests"
```

---

### Task 3: `CampaignGps` engine (Core, pure)

**Files:**
- Create: `src/POE2Radar.Core/Campaign/CampaignGps.cs`
- Test: `tests/POE2Radar.Tests/CampaignGpsTests.cs`

**Interfaces:**
- Consumes: `CampaignRoute`, `CampaignStep`, `IQuestProgress` (Tasks 1–2); `Poe2Live.Landmark` (`Name`, `Path`, `Center`, `CuratedName`, `Key`), `Poe2Live.EntityDot` (`Id`, `Category`, `Grid`), `Poe2Live.EntityCategory.Transition`.
- Produces: `readonly record struct GpsInstruction(string? ExitObjectiveId, string TargetName, int Act, string Text)`; `static class CampaignGps` with `GpsInstruction Decide(string currentZoneCode, IQuestProgress progress, CampaignRoute route, IReadOnlyList<Poe2Live.Landmark> landmarks, IReadOnlyList<Poe2Live.EntityDot> entities, System.Numerics.Vector2 player)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/POE2Radar.Tests/CampaignGpsTests.cs`:

```csharp
using System.Collections.Generic;
using System.Numerics;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class CampaignGpsTests
{
    private const string Json = """
    [
      { "zone": "Z1", "act": 1, "name": "Zone One", "next": "Z2", "exitHint": "Zone Two" },
      { "zone": "Z2", "act": 1, "name": "Zone Two", "next": "Z3", "exitHint": "Zone Three" },
      { "zone": "Z3", "act": 2, "name": "Zone Three", "next": null, "exitHint": null }
    ]
    """;
    private static CampaignRoute R() => CampaignRoute.FromJson(Json);
    private static readonly IReadOnlyList<Poe2Live.EntityDot> NoEntities = new List<Poe2Live.EntityDot>();

    private static Poe2Live.Landmark Lm(string path, string? curated, float x, float y) =>
        new("derived", path, new Vector2(x, y), 1, curated);

    [Fact] public void In_target_zone_routes_to_the_exit_toward_next_by_exitHint()
    {
        var lms = new List<Poe2Live.Landmark> {
            Lm("exit_a.tdt", "Zone Three", 10, 10),   // the onward exit (matches Z2.exitHint)
            Lm("exit_b.tdt", "Somewhere Else", 5, 5),
        };
        var ins = CampaignGps.Decide("Z2", new ZoneOrderProgress(R()), R(), lms, NoEntities, new Vector2(0, 0));
        Assert.Equal("t:exit_a.tdt@10,10", ins.ExitObjectiveId);
        Assert.Contains("Zone Three", ins.Text);
    }

    [Fact] public void Off_target_zone_routes_back_toward_the_target_by_name()
    {
        var p = new ZoneOrderProgress(R());
        p.CurrentStep("Z2");   // latch at Z2
        var lms = new List<Poe2Live.Landmark> { Lm("back.tdt", "Zone Two", 3, 4) };
        var ins = CampaignGps.Decide("SideZone", p, R(), lms, NoEntities, new Vector2(0, 0));
        Assert.Equal("t:back.tdt@3,4", ins.ExitObjectiveId);
        Assert.Contains("Zone Two", ins.Text);
    }

    [Fact] public void No_matching_label_falls_back_to_nearest_transition_entity()
    {
        var entities = new List<Poe2Live.EntityDot> {
            new(42u, default, new Vector2(2, 0), default, Poe2Live.EntityCategory.Transition, "Metadata/.../AreaTransition", 0, 0, false, 0, Poe2Live.Rarity.NonMonster, false),
        };
        var ins = CampaignGps.Decide("Z2", new ZoneOrderProgress(R()), R(), new List<Poe2Live.Landmark>(), entities, new Vector2(0, 0));
        Assert.Equal("e:42", ins.ExitObjectiveId);
    }

    [Fact] public void No_exit_anywhere_yields_null_objective_but_still_an_instruction()
    {
        var ins = CampaignGps.Decide("Z2", new ZoneOrderProgress(R()), R(), new List<Poe2Live.Landmark>(), NoEntities, new Vector2(0, 0));
        Assert.Null(ins.ExitObjectiveId);
        Assert.False(string.IsNullOrEmpty(ins.Text));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter CampaignGpsTests`
Expected: FAIL — `CampaignGps` does not exist.

- [ ] **Step 3: Implement `CampaignGps.cs`**

Create `src/POE2Radar.Core/Campaign/CampaignGps.cs`:

```csharp
using System.Numerics;
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign;

/// <summary>The cross-zone navigation instruction for the current tick. <see cref="ExitObjectiveId"/>
/// is a nav-selection id ("t:&lt;landmarkKey&gt;" / "e:&lt;entityId&gt;") the existing routing pipeline
/// resolves, or null when no usable exit is visible in this zone.</summary>
public readonly record struct GpsInstruction(string? ExitObjectiveId, string TargetName, int Act, string Text);

/// <summary>
/// Pure cross-zone campaign GPS. Given the player's current zone, the (zone-order or quest-aware)
/// progress, and the live in-zone landmarks/entities, decide which exit to route toward to advance the
/// campaign — and the human instruction text. No memory access; no state.
/// </summary>
public static class CampaignGps
{
    public static GpsInstruction Decide(string currentZoneCode, IQuestProgress progress, CampaignRoute route,
        IReadOnlyList<Poe2Live.Landmark> landmarks, IReadOnlyList<Poe2Live.EntityDot> entities, Vector2 player)
    {
        var target = progress.CurrentStep(currentZoneCode);
        var inTarget = string.Equals(currentZoneCode, target.Zone, StringComparison.OrdinalIgnoreCase);

        // Where are we trying to go FROM this zone?
        //  - in the target zone → forward to the next step (its name + the target's exitHint).
        //  - off the target zone → back toward the target zone (by the target's own name).
        string? wantName; string? wantCode; string? exitHint;
        if (inTarget)
        {
            var next = route.NextStep(target);
            if (next is not { } n)
                return new GpsInstruction(null, target.Name, target.Act, $"Act {target.Act} · {target.Name} — campaign route complete");
            wantName = n.Name; wantCode = n.Zone; exitHint = target.ExitHint;
        }
        else
        {
            wantName = target.Name; wantCode = target.Zone; exitHint = target.Name;
        }

        var (exitId, exitName) = PickExit(route, landmarks, entities, player, exitHint, wantCode);
        var via = exitName is { Length: > 0 } ? $" · take the {exitName} exit" : "";
        return new GpsInstruction(exitId, wantName ?? target.Name, target.Act, $"Act {target.Act} · → {wantName}{via}");
    }

    // Precedence: (1) landmark whose CuratedName == exitHint; (2) landmark whose CuratedName resolves
    // (via route.CodeForName) to wantCode; (3) nearest Transition entity; (4) none.
    private static (string? id, string? name) PickExit(CampaignRoute route,
        IReadOnlyList<Poe2Live.Landmark> landmarks, IReadOnlyList<Poe2Live.EntityDot> entities, Vector2 player,
        string? exitHint, string? wantCode)
    {
        foreach (var lm in landmarks)
            if (exitHint != null && string.Equals(lm.CuratedName, exitHint, StringComparison.OrdinalIgnoreCase))
                return ($"t:{lm.Key}", lm.CuratedName);

        foreach (var lm in landmarks)
            if (lm.CuratedName is { } cn && wantCode != null
                && string.Equals(route.CodeForName(cn), wantCode, StringComparison.OrdinalIgnoreCase))
                return ($"t:{lm.Key}", cn);

        Poe2Live.EntityDot? nearest = null; var bestSq = float.MaxValue;
        foreach (var en in entities)
        {
            if (en.Category != Poe2Live.EntityCategory.Transition) continue;
            var dsq = Vector2.DistanceSquared(en.Grid, player);
            if (dsq < bestSq) { bestSq = dsq; nearest = en; }
        }
        if (nearest is { } e) return ($"e:{e.Id}", null);

        return (null, null);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter CampaignGpsTests`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Campaign/CampaignGps.cs tests/POE2Radar.Tests/CampaignGpsTests.cs
git commit -m "feat(gps): CampaignGps cross-zone exit-selection engine + tests"
```

---

### Task 4: Settings flags + API round-trip

**Files:**
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs`
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (`ReadSettings` + `ApplySettings`)

**Interfaces:**
- Produces: `RadarSettings.EnableCampaignGps` (bool, default false), `RadarSettings.EnableQuestMemory` (bool, default false); `/api/settings` round-trip keys `enableCampaignGps`, `enableQuestMemory`.

- [ ] **Step 1: Add the settings**

In `src/POE2Radar.Overlay/Config/RadarSettings.cs`, immediately after the `EnableDirector` property (line ~35), add:

```csharp
    // Campaign GPS (Quest-aware Director, Part B): when on, route cross-zone toward the next campaign
    // critical-path zone (current-zone + the embedded route table). Read-only — only changes nav selection.
    public bool EnableCampaignGps { get; set; } = false;
    // Quest-memory precision layer for Campaign GPS: only meaningful once the quest-completion offsets
    // are validated in-game; reads quest flags to refine the inferred step. Off until validated.
    public bool EnableQuestMemory { get; set; } = false;
```

- [ ] **Step 2: Expose them in `ReadSettings`**

In `src/POE2Radar.Overlay/Web/ApiServer.cs` `ReadSettings()`, after the `enableDirector = _settings.EnableDirector,` line, add:

```csharp
        enableCampaignGps = _settings.EnableCampaignGps,
        enableQuestMemory = _settings.EnableQuestMemory,
```

- [ ] **Step 3: Parse them in `ApplySettings`**

In `ApplySettings()`'s `switch`, after the `case "enableDirector"` line, add:

```csharp
                case "enableCampaignGps" when TryBool(p.Value, out var b): _settings.EnableCampaignGps = b; applied.Add(p.Name); break;
                case "enableQuestMemory" when TryBool(p.Value, out var b): _settings.EnableQuestMemory = b; applied.Add(p.Name); break;
```

- [ ] **Step 4: Build**

Run: `dotnet build POE2Radar.slnx -c Debug`
Expected: 0W/0E.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Web/ApiServer.cs
git commit -m "feat(gps): EnableCampaignGps + EnableQuestMemory settings + API round-trip"
```

---

### Task 5: `RadarApp` wiring + `RadarState.CampaignGps` + `/state`

**Files:**
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (fields, `CampaignReconcile`, WorldTick wiring, `_state` publish)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (`RadarState` record, `/state` serialization)

**Interfaces:**
- Consumes: `CampaignRoute`, `CampaignGps`, `ZoneOrderProgress`, `GpsInstruction` (Tasks 1–3); `SetActiveTarget(string?)`, `_entities`, `_landmarks`, `_settings.EnableCampaignGps`/`EnableDirector` (existing).
- Produces: `RadarState.CampaignGps` (string?, default null); `/state` `campaignGps` field.

- [ ] **Step 1: Add RadarApp fields**

In `src/POE2Radar.Overlay/RadarApp.cs`, with the director fields (near `_directorQueue`, ~line 258), add:

```csharp
    private readonly POE2Radar.Core.Campaign.ZoneOrderProgress _questProgress =
        new(POE2Radar.Core.Game.CampaignRoute.Shared);
    private volatile string? _campaignGps;   // current cross-zone instruction (null when off / none)
```

- [ ] **Step 2: Add `CampaignReconcile`**

Add next to `DirectorReconcile` (~line 1757):

```csharp
    /// <summary>Campaign GPS reconcile (world thread, gated on EnableCampaignGps). Decides the campaign-
    /// forward exit for this zone and, when one is visible, sets it as the active nav target (the existing
    /// A* pipeline draws the route). Publishes the instruction string. Returns true when it owns the
    /// selection this tick (so the in-zone Director stands down).</summary>
    private bool CampaignReconcile(string areaCode, NumVec2 player)
    {
        var ins = POE2Radar.Core.Campaign.CampaignGps.Decide(
            areaCode, _questProgress, POE2Radar.Core.Game.CampaignRoute.Shared, _landmarks, _entities, player);
        _campaignGps = ins.Text;
        if (ins.ExitObjectiveId != null) { SetActiveTarget(ins.ExitObjectiveId); return true; }
        return false;
    }
```

- [ ] **Step 3: Wire into WorldTick**

In `WorldTick`, replace the director call line (`if (_settings.EnableDirector) DirectorReconcile(player);`, ~line 1269) with:

```csharp
        // Campaign GPS (cross-zone) takes precedence when it actively owns the selection; otherwise the
        // in-zone Objective Director runs. Both read-only — only edit _selectedIds.
        var gpsOwned = false;
        if (_settings.EnableCampaignGps) gpsOwned = CampaignReconcile(areaCode, player);
        else _campaignGps = null;
        if (!gpsOwned && _settings.EnableDirector) DirectorReconcile(player);
```

(`areaCode` is the local already computed in WorldTick via `_live.AreaCode(areaInstance)`; confirm it's in scope at this point — if the local is named differently, use that name.)

- [ ] **Step 4: Publish on `_state`**

In `Tick`, change the `_state = new RadarState(...)` tail (the `Health: ..., HealthMessage: ...` line, ~line 1018) to append the new field:

```csharp
            Session: _sessionSnapshot, Health: _healthState, HealthMessage: _healthMessage, CampaignGps: _campaignGps);
```

- [ ] **Step 5: Add the `RadarState` field**

In `src/POE2Radar.Overlay/Web/ApiServer.cs`, change the `RadarState` record's last parameter (`string? HealthMessage = null`) to add a trailing param:

```csharp
    HealthMessage = null,
    // Campaign GPS cross-zone instruction for the dashboard banner; null when off / no instruction.
    string? CampaignGps = null)
```

(`RadarState.Empty` is unaffected — it constructs positionally with required params only.)

- [ ] **Step 6: Serialize on `/state`**

In the `/state` handler, after the `director = (...)` block (~line 200), add:

```csharp
                    campaignGps = s.CampaignGps,
```

- [ ] **Step 7: Build + full suite + compliance**

Run: `dotnet build POE2Radar.slnx -c Debug` · `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj` · `pwsh scripts/compliance-gate.ps1`
Expected: 0W/0E, suite green, compliance PASS. (Manual: enable Campaign GPS, `curl /state` shows `campaignGps` text + the route draws to the exit.)

- [ ] **Step 8: Commit**

```bash
git add src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Web/ApiServer.cs
git commit -m "feat(gps): RadarApp CampaignReconcile wiring + /state campaignGps"
```

---

### Task 6: Dashboard — Campaign GPS banner + toggle

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs`

**Interfaces:**
- Consumes: `/state` `campaignGps` (Task 5); `enableCampaignGps`/`enableQuestMemory` settings (Task 4).

- [ ] **Step 1: Add the GPS banner to the Zone Plan card**

In `src/POE2Radar.Overlay/Web/DashboardHtml.cs`, change the Zone Plan card (`#dirQueueCard`, ~line 706) to add an instruction banner above `#dirQueue`:

```html
          <div class="card" id="dirQueueCard">
            <h3>Zone Plan <small>live ranked queue for this area</small></h3>
            <div id="gpsBanner" hidden style="padding:8px 10px;margin:0 0 8px;border:1px solid var(--gold-deep);border-radius:3px;color:var(--gold-bright);font-size:13px"></div>
            <div id="dirQueue"></div>
          </div>
```

- [ ] **Step 2: Render the banner each tick**

In `renderDirectorQueue()` (~line 1300), after `if (!dq) return;`, add:

```javascript
  const gb = document.getElementById('gpsBanner');
  if (gb) {
    const g = state && state.campaignGps;
    if (g) { gb.hidden = false; gb.textContent = '🧭 ' + g; }   // 🧭
    else { gb.hidden = true; gb.textContent = ''; }
  }
```

- [ ] **Step 3: Add the settings toggles**

In the Settings view, after the `enableDirector` toggle row (~line 567), add two rows in the same `Radar Display` card:

```html
            <div class="row"><div class="rl">Campaign GPS (experimental)<small>cross-zone &mdash; routes you toward the next campaign zone's exit. Off by default.</small></div>
              <label class="sw"><input type="checkbox" data-set="enableCampaignGps"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Quest-memory precision<small>only effective once quest offsets are validated in-game; refines Campaign GPS.</small></div>
              <label class="sw"><input type="checkbox" data-set="enableQuestMemory"><span class="track"></span><span class="knob"></span></label></div>
```

- [ ] **Step 4: Build**

Run: `dotnet build POE2Radar.slnx -c Debug`
Expected: 0W/0E. (Manual: F12 → Settings shows the two toggles; with Campaign GPS on + in a campaign zone, the Zone Plan card shows the 🧭 banner.)

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(gps): dashboard Campaign GPS banner + toggles"
```

---

### Task 7: Overlay instruction line (optional / cuttable)

**Files:**
- Modify: `src/POE2Radar.Overlay/Overlay/RenderContext.cs` (one trailing param)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (pass it)
- Modify: `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (draw a compact line)

**Interfaces:**
- Consumes: `_campaignGps` (Task 5).
- Produces: `RenderContext.CampaignGps` (string?, default null).

The A* route to the exit already draws on the overlay (Task 5 sets the nav target). This task adds a small text line for clarity. It is **cuttable** — the route line + dashboard banner already deliver the GPS.

- [ ] **Step 1: Add the RenderContext param**

In `src/POE2Radar.Overlay/Overlay/RenderContext.cs`, append after the last parameter (the health params added earlier):

```csharp
    string? CampaignGps = null);
```

(Change the prior last param's `)` to `,` accordingly.)

- [ ] **Step 2: Pass it at construction**

In `RadarApp.Tick`'s `new RenderContext(...)`, append (after the health args):

```csharp
            CampaignGps: _campaignGps,
```

- [ ] **Step 3: Draw it**

In `OverlayRenderer.cs`, add a method (after `DrawHealthBanner`) and call it inside the `if (ctx.Active && ctx.InGame)` block (where `DrawSessionHud` is called):

```csharp
    private void DrawCampaignGps(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.CampaignGps is not { Length: > 0 } msg) return;
        rt.FillRectangle(new Vortice.RawRectF(0f, 34f, ctx.WindowWidth, 58f), _bPanel!);
        _bStyle!.Color = new Color4(0.85f, 0.72f, 0.30f, 1f);   // campaign gold
        rt.DrawText("🧭 " + msg, _tf!, new Rect(12f, 38f, ctx.WindowWidth - 12f, 58f), _bStyle!, DrawTextOptions.Clip);
    }
```

Add the call in `Render`, in the `if (ctx.Active && ctx.InGame)` block:

```csharp
                DrawCampaignGps(rt, ctx);
```

- [ ] **Step 4: Build**

Run: `dotnet build POE2Radar.slnx -c Debug`
Expected: 0W/0E.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Overlay/RenderContext.cs src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs
git commit -m "feat(gps): compact overlay Campaign GPS instruction line"
```

---

## Post-session (GATED, non-blocking) — quest-memory precision layer

**Do NOT build this until the in-game `--quest` / `--serverdata-diff` session pins the quest-completion offsets.** The feature ships fully on Tasks 1–6 without it. This slots into the `IQuestProgress` seam built in Task 2.

When the session validates the layout (the `ServerDataStructure +0x3030` block, the `0xB4000000` sentinel semantics, the per-quest key):

1. **Core read:** add `Poe2Live.TryReadQuestState(nint areaInstance, out IReadOnlySet<string> completedQuestIds)` — resolves `AreaInstance+0x598 → ServerData+0x48 vec[0] → ServerDataStructure`, reads the validated quest block, returns the completed-quest id set; returns `false`/empty on any read failure. Add the pinned offsets to `Poe2Offsets.cs` with the `✓` marker + bump the CLAUDE.md "Still TBD" note.
2. **Core impl:** add `MemoryQuestProgress : IQuestProgress` that wraps `ZoneOrderProgress` and, given the completed-quest set, overrides the inferred step (skip a step whose quest is complete; surface an out-of-order required quest). Pure given the set; the set is injected each tick from `TryReadQuestState`.
3. **Wire:** in `CampaignReconcile`, choose the progress impl by `_settings.EnableQuestMemory && questReadValidated`; feed the read set in. Default stays `ZoneOrderProgress`.
4. **Validate** in-game, then enable `EnableQuestMemory` by default only if robust. **If the RE does not pin cleanly, stop** — ship zone-order; leave this section as the resume point (the Island Rumours lesson: no indefinite grind).

---

## Post-implementation

- Bump `<Version>` in `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (0.6.0 → 0.7.0, minor feature) in the final pass; then the release ritual (ff-merge, tag-collision check, push, `release.yml`, `gh release edit --repo luther-rotmg/POE2GPS`, Discord + Ko-fi plug, memory update).

## Self-Review

**Spec coverage:** `campaign_route.json` + reverse map → Task 1. `IQuestProgress` + `ZoneOrderProgress` latch → Task 2. `CampaignGps` engine (in-target / off-target / fallback) → Task 3. Settings flags → Task 4. RadarApp wiring + `/state` + the GPS-vs-Director precedence + the `SetActiveTarget`/A* reuse → Task 5. Dashboard banner + toggle → Task 6. Overlay line → Task 7. Quest-memory read isolated + gated + non-blocking → Post-session section. Off-by-default → Task 4. Tests reference Core only → Tasks 1–3 live in Core.

**Placeholder scan:** No TBD/TODO in shippable tasks; the only conditional is the explicitly-gated post-session section (not a coded task). `campaign_route.json` content is real authoring (draft for review per spec), not a placeholder.

**Type consistency:** `CampaignStep(Zone, Act, Name, Next, ExitHint)` and `GpsInstruction(ExitObjectiveId, TargetName, Act, Text)` defined in Tasks 1/3 and consumed verbatim in Tasks 3/5. `CampaignGps.Decide(currentZoneCode, IQuestProgress, CampaignRoute, IReadOnlyList<Landmark>, IReadOnlyList<EntityDot>, Vector2)` matches the call in `CampaignReconcile` (Task 5). Nav id format `t:<Key>` / `e:<Id>` matches `RankedObjective.Id` and `SetActiveTarget`. `RadarState.CampaignGps` (Task 5) consumed by `/state` (Task 5) + dashboard (Task 6) + RenderContext (Task 7).
