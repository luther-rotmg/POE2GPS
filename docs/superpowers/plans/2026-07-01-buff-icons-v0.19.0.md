# Buff Icons v0.19.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Show combat-relevant buffs above elite monsters as short tier-colored text tags, read from the validated Buffs component — opt-in, stealth-gated, read-only.

**Architecture:** A direct clone of the shipped affix-nameplate feature. Core: a validated `Poe2Live.Buffs()` read + a pure `BuffCatalog` (embedded curated JSON + heuristic tiering, unit-tested). Overlay: a `BuffNameplateSpec` built at world rate → published in `WorldSnapshot` → per-frame position re-read → `DrawBuffNameplates` via the camera matrix (same pipeline as affix nameplates), plus settings/API/dashboard. Default OFF; when off it reads nothing (`EnableBuffReads` gate, exactly like `EnableModReads`).

**Tech Stack:** C# / .NET 10 (`net10.0-windows`, x64). `System.Text.Json`, xUnit (Core-only test project), Vortice.Direct2D1.

## Global Constraints

- **Strictly read-only of the game.** No writes/injection/input/pricing. The ONLY new memory access is the validated Buffs component — **no new offset discovery** (offsets are `✓` from the `--buffs` probe).
- **Invisible/stealth (Perf v3):** feature **defaults OFF**; `EnableBuffReads` gates the read so a default install adds **zero reads**; when on, reads run only for **elite (Rare/Unique/Boss) monsters** passing the rarity gate, and each buff's **id is cached per StatusEffect** (only the timer float re-reads).
- **Validated Buffs layout (`✓` 2026-07-01):** `ResolveComponent(entity,"Buffs")`; `Buffs+0x160` = `StdVector<StatusEffect*>` (First/Last/End, stride 8); `StatusEffect+0x08` = Definition ptr → `+0x00` = ptr to UTF-16 id; `StatusEffect+0x18` = timer float (`∞`/Inf = permanent).
- Platform: `net10.0-windows`, x64, `Nullable` enable, `TreatWarningsAsErrors=true` — warning-clean.
- **Tests:** `tests/POE2Radar.Tests` references **Core only**. `BuffCatalog` (pure) is unit-tested; the `Poe2Live.Buffs` read + all Overlay wiring are validated by **build (0 CS errors) + live smoke**. The existing **268 tests stay green**.
- README badge stays `0.5.4`. App `<Version>` → `0.19.0` (final task only).
- **Build note:** a running overlay locks `Overlay.dll`/`POE2Radar.Core.dll` → MSB3026/MSB3027 are copy-lock errors, not code errors; success = 0 CS errors.
- Full design + validated-layout rationale: `docs/superpowers/specs/2026-07-01-buff-icons-v0.19.0-design.md`.

---

### Task 1: BuffCatalog (Core, pure, unit-tested)

**Files:**
- Create: `src/POE2Radar.Core/Game/BuffCatalog.cs`
- Create: `src/POE2Radar.Core/Game/poe2_notable_buffs.json`
- Modify: `src/POE2Radar.Core/POE2Radar.Core.csproj` (embed the JSON)
- Create: `tests/POE2Radar.Tests/BuffCatalogTests.cs`

**Interfaces:**
- Produces: `BuffTier { Minor, Notable, Deadly }`; `readonly record struct BuffInfo(string Name, BuffTier Tier)`; `readonly record struct BuffLine(string Text, BuffTier Tier)`; `readonly record struct BuffFilter(BuffTier Threshold, IReadOnlySet<string> AlwaysShow, IReadOnlySet<string> Hide, bool DisplayAll, int MaxLines)`; `BuffCatalog.Shared`; `BuffInfo Resolve(string id)`; `IReadOnlyList<BuffLine> Select(IReadOnlyList<Poe2Live.BuffState> buffs, BuffFilter f)`.
- Consumes (Task 2 defines it, but Task 1's `Select` signature references it): `Poe2Live.BuffState(string Id, float Timer, bool Permanent)`. **To avoid a Task-ordering cycle, Task 1 defines `Select` over a minimal local shape** — take the three fields directly: `Select(IReadOnlyList<(string Id, float Timer, bool Permanent)> buffs, BuffFilter f)`. Task 3 adapts `BuffState` → the tuple at the call site (a 1-liner). This keeps `BuffCatalog` a pure Core type with no dependency on `Poe2Live`.

- [ ] **Step 1: Write the failing tests.** Create `tests/POE2Radar.Tests/BuffCatalogTests.cs`:

```csharp
using POE2Radar.Core.Game;

public class BuffCatalogTests
{
    static BuffFilter F(BuffTier t, bool all = false, int max = 4, string[]? show = null, string[]? hide = null)
        => new(t, new HashSet<string>(show ?? System.Array.Empty<string>()),
                  new HashSet<string>(hide ?? System.Array.Empty<string>()), all, max);

    static (string, float, bool) B(string id, float timer = 0f, bool perm = true) => (id, timer, perm);

    [Fact] public void Prettify_snake_case_to_title()
        => Assert.Equal("Igniting Presence Aura", BuffCatalog.Prettify("igniting_presence_aura"));

    [Fact] public void Resolve_uncurated_uses_heuristic_tier()
    {
        // "*aura*" heuristic → Notable
        Assert.Equal(BuffTier.Notable, BuffCatalog.Shared.Resolve("some_fire_aura").Tier);
        // enrage → Deadly
        Assert.Equal(BuffTier.Deadly, BuffCatalog.Shared.Resolve("monster_enrage").Tier);
        // plain → Minor
        Assert.Equal(BuffTier.Minor, BuffCatalog.Shared.Resolve("something_plain").Tier);
    }

    [Fact] public void Select_junk_suppressed_by_default_shown_under_displayAll()
    {
        var junk = new[] { B("enemies_in_presence_events_tracker") };
        Assert.Empty(BuffCatalog.Shared.Select(junk, F(BuffTier.Minor)));
        Assert.Single(BuffCatalog.Shared.Select(junk, F(BuffTier.Minor, all: true)));
    }

    [Fact] public void Select_tier_threshold_excludes_below()
        => Assert.Empty(BuffCatalog.Shared.Select(new[] { B("something_plain") }, F(BuffTier.Deadly)));

    [Fact] public void Select_appends_timer_for_temporary_buffs()
    {
        var lines = BuffCatalog.Shared.Select(new[] { B("some_fire_aura", timer: 3.2f, perm: false) }, F(BuffTier.Minor));
        Assert.Single(lines);
        Assert.EndsWith("4s", lines[0].Text);   // ceil(3.2) = 4
        Assert.DoesNotContain("s", lines[0].Text.Replace("4s", ""));  // only the timer suffix carries an 's'... (sanity)
    }

    [Fact] public void Select_permanent_has_no_timer_suffix()
    {
        var lines = BuffCatalog.Shared.Select(new[] { B("some_fire_aura", perm: true) }, F(BuffTier.Minor));
        Assert.False(lines[0].Text.EndsWith("s") && char.IsDigit(lines[0].Text[^2]));
    }

    [Fact] public void Select_caps_at_maxLines_and_orders_deadly_first()
    {
        var buffs = new[] { B("something_plain"), B("monster_enrage"), B("a_shield_x"), B("b_aura_y"), B("c_haste_z") };
        var lines = BuffCatalog.Shared.Select(buffs, F(BuffTier.Minor, max: 2));
        Assert.Equal(2, lines.Count);
        Assert.Equal(BuffTier.Deadly, lines[0].Tier);   // enrage sorts first
    }

    [Fact] public void Select_hide_suppresses_even_under_displayAll()
        => Assert.Empty(BuffCatalog.Shared.Select(new[] { B("some_fire_aura") },
              F(BuffTier.Minor, all: true, hide: new[] { "some_fire_aura" })));
}
```

- [ ] **Step 2: Run tests to verify they fail.** `dotnet test tests/POE2Radar.Tests --filter BuffCatalogTests` → FAIL (BuffCatalog not defined).

- [ ] **Step 3: Create the catalog.** Create `src/POE2Radar.Core/Game/BuffCatalog.cs`:

```csharp
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

public enum BuffTier { Minor = 0, Notable = 1, Deadly = 2 }

public readonly record struct BuffInfo(string Name, BuffTier Tier);
public readonly record struct BuffLine(string Text, BuffTier Tier);

public readonly record struct BuffFilter(
    BuffTier Threshold, IReadOnlySet<string> AlwaysShow, IReadOnlySet<string> Hide, bool DisplayAll, int MaxLines);

/// <summary>Maps monster buff ids (snake_case internal names) to a readable name + danger tier (curated
/// table + substring-heuristic fallback), suppresses engine-junk ids, and selects the display lines for a
/// mob given a filter. Pure (no memory access). Mirrors <see cref="MonsterAffixCatalog"/>.</summary>
public sealed class BuffCatalog
{
    private static readonly Lazy<BuffCatalog> _shared =
        new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);
    public static BuffCatalog Shared => _shared.Value;

    private readonly Dictionary<string, BuffInfo> _curated;
    public IReadOnlyDictionary<string, BuffInfo> Curated => _curated;
    private BuffCatalog(Dictionary<string, BuffInfo> curated) => _curated = curated;

    // Engine/internal buffs that are never player-relevant — suppressed unless DisplayAll (diagnostic).
    private static readonly string[] JunkSubstrings =
        { "_tracker", "should_aim", "_reservation", "presence_events", "_debug", "_internal" };
    private static bool IsJunk(string id)
    {
        foreach (var j in JunkSubstrings) if (id.Contains(j, StringComparison.Ordinal)) return true;
        return false;
    }

    public BuffInfo Resolve(string id)
    {
        if (_curated.TryGetValue(id, out var info)) return info;
        return new BuffInfo(Prettify(id), HeuristicTier(id));
    }

    /// <summary>snake_case internal id → Title Case display name. ("igniting_presence_aura" → "Igniting Presence Aura")</summary>
    public static string Prettify(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var parts = id.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p.Substring(1));
        var name = string.Join(' ', parts).Trim();
        return name.Length == 0 ? id : name;
    }

    /// <summary>Best-effort danger tier for an uncatalogued id, from substring signals.</summary>
    public static BuffTier HeuristicTier(string id)
    {
        var s = id.ToLowerInvariant();
        if (s.Contains("enrage") || s.Contains("berserk") || s.Contains("frenzied") || s.Contains("empower")) return BuffTier.Deadly;
        if (s.Contains("aura") || s.Contains("shield") || s.Contains("fortif") || s.Contains("speed")
            || s.Contains("haste") || s.Contains("damage") || s.Contains("regen") || s.Contains("resist")) return BuffTier.Notable;
        return BuffTier.Minor;
    }

    public IReadOnlyList<BuffLine> Select(IReadOnlyList<(string Id, float Timer, bool Permanent)> buffs, BuffFilter f)
    {
        var picked = new List<BuffLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in buffs)
        {
            if (f.Hide.Contains(b.Id)) continue;                         // hide overrides everything
            if (IsJunk(b.Id) && !f.DisplayAll && !f.AlwaysShow.Contains(b.Id)) continue;
            var info = Resolve(b.Id);
            bool show = f.DisplayAll || f.AlwaysShow.Contains(b.Id) || info.Tier >= f.Threshold;
            if (!show) continue;
            var text = b.Permanent || b.Timer <= 0f ? info.Name
                : $"{info.Name} {(int)MathF.Ceiling(b.Timer)}s";
            if (!seen.Add(text)) continue;                              // de-dup by display text
            picked.Add(new BuffLine(text, info.Tier));
        }
        picked.Sort((a, b) => a.Tier != b.Tier ? b.Tier.CompareTo(a.Tier) : string.CompareOrdinal(a.Text, b.Text));
        if (picked.Count > f.MaxLines) picked.RemoveRange(f.MaxLines, picked.Count - f.MaxLines);
        return picked;
    }

    private static BuffCatalog LoadEmbedded()
    {
        var curated = new Dictionary<string, BuffInfo>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.Contains("poe2_notable_buffs", StringComparison.Ordinal));
            if (name != null)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null)
                {
                    using var doc = JsonDocument.Parse(s);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        var nm = p.Value.TryGetProperty("name", out var n2) ? n2.GetString() ?? p.Name : p.Name;
                        var tierStr = p.Value.TryGetProperty("tier", out var t2) ? t2.GetString() : "Minor";
                        var tier = tierStr switch { "Deadly" => BuffTier.Deadly, "Notable" => BuffTier.Notable, _ => BuffTier.Minor };
                        curated[p.Name] = new BuffInfo(nm, tier);
                    }
                }
            }
        }
        catch { /* empty curated table → everything prettifies + heuristic-tiers */ }
        return new BuffCatalog(curated);
    }
}
```

- [ ] **Step 4: Create the starter JSON.** Create `src/POE2Radar.Core/Game/poe2_notable_buffs.json` (schema `{ id: { name, tier } }` — starter set; grown from the diagnostic feed):

```json
{
  "igniting_presence_aura": { "name": "Igniting Presence", "tier": "Notable" },
  "chilling_presence_aura": { "name": "Chilling Presence", "tier": "Notable" },
  "shocking_presence_aura": { "name": "Shocking Presence", "tier": "Notable" },
  "physical_damage_aura": { "name": "Physical Aura", "tier": "Notable" },
  "monster_enrage": { "name": "Enraged", "tier": "Deadly" },
  "monster_berserk": { "name": "Berserk", "tier": "Deadly" },
  "damage_reflection": { "name": "Reflects Damage", "tier": "Deadly" },
  "energy_shield_regen": { "name": "Shielded", "tier": "Notable" },
  "unwavering_stance": { "name": "Unstoppable", "tier": "Notable" },
  "temporal_bubble": { "name": "Temporal Bubble", "tier": "Deadly" },
  "haste_aura": { "name": "Haste", "tier": "Notable" },
  "grace_aura": { "name": "Grace", "tier": "Minor" }
}
```

- [ ] **Step 5: Embed it.** In `src/POE2Radar.Core/POE2Radar.Core.csproj`, add inside the `<ItemGroup>` at lines 14–30 (after the `preload_catalog.json` line):

```xml
    <EmbeddedResource Include="Game\poe2_notable_buffs.json" />
```

- [ ] **Step 6: Run tests to verify they pass.** `dotnet test tests/POE2Radar.Tests --filter BuffCatalogTests` → all green.

- [ ] **Step 7: Commit.**
```bash
git add src/POE2Radar.Core/Game/BuffCatalog.cs src/POE2Radar.Core/Game/poe2_notable_buffs.json src/POE2Radar.Core/POE2Radar.Core.csproj tests/POE2Radar.Tests/BuffCatalogTests.cs
git commit -m "feat(buffs): BuffCatalog (curated + heuristic tiering, junk suppression) + starter JSON + tests"
```

---

### Task 2: Buffs offsets + Poe2Live.Buffs read (Core)

**Files:**
- Modify: `src/POE2Radar.Core/Game/Poe2Offsets.cs` (add `BuffsComponent`/`StatusEffect`/`BuffDefinition` after the `ModsComponent` block)
- Modify: `src/POE2Radar.Core/Game/Poe2Live.cs` (add `BuffState` record, `EnableBuffReads`, `_buffsAddr`/`_buffId` caches + clears, `Buffs()` method)

**Interfaces:**
- Produces: `Poe2Live.BuffState(string Id, float Timer, bool Permanent)` (public readonly record struct); `Poe2Live.EnableBuffReads` (bool, default true); `IReadOnlyList<BuffState> Poe2Live.Buffs(nint entity)`.
- Consumes: existing `Ptr(nint)`, `_reader.TryReadStruct<T>`, `_reader.ReadStringUtf16`, `ResolveComponent(nint,string)`.

- [ ] **Step 1: Add offsets.** In `Poe2Offsets.cs`, immediately after the `ModsComponent` static class block, add (values `✓` validated live 2026-07-01 via `--buffs`):

```csharp
    /// <summary>Buffs component — the entity's active status-effect list. ✓ validated live 2026-07-01
    /// (Research --buffs): +0x160 is a StdVector&lt;StatusEffect*&gt; (First/Last/End, stride 8).</summary>
    public static class BuffsComponent
    {
        public const int BuffVector = 0x160; // ✓ StdVector<StatusEffect*> (First @ +0x160, Last @ +0x168)
    }

    /// <summary>One active buff/debuff. ✓ validated live 2026-07-01. +0x08 → Definition; +0x18 timer float
    /// (Inf/∞ = permanent aura, finite = temporary — the popped Life flask read 3.2).</summary>
    public static class StatusEffect
    {
        public const int Definition = 0x08; // ✓ ptr → BuffDefinition
        public const int Timer      = 0x18; // ✓ float — remaining time; Inf = permanent
        public const int MaxTimer   = 0x1C; // float — total/base (semantics unconfirmed; not shipped)
        public const int Charges    = 0x40; // int — stack/charge count (not shipped)
    }

    /// <summary>Buff definition row. ✓ validated live 2026-07-01: +0x00 = ptr to the UTF-16 internal id.</summary>
    public static class BuffDefinition
    {
        public const int IdPtr = 0x00; // ✓ ptr → UTF-16 buff id string (e.g. "igniting_presence_aura")
    }
```

- [ ] **Step 2: Add the `BuffState` record + caches + gate.** In `Poe2Live.cs`, near the `_mods`/`EnableModReads` fields (~44–55), add:

```csharp
    /// <summary>One active buff on an entity. Id = internal name (e.g. "igniting_presence_aura");
    /// Timer = seconds remaining (0 when Permanent); Permanent = the timer read as Inf/≤0.</summary>
    public readonly record struct BuffState(string Id, float Timer, bool Permanent);

    /// <summary>When false, the Buffs component is never read (no consumer). Set by RadarApp each world tick
    /// from the Buff-nameplate feature state. Default true = fail-safe. Mirrors <see cref="EnableModReads"/>.</summary>
    public bool EnableBuffReads { get; set; } = true;

    private readonly Dictionary<nint, nint> _buffsAddr = new();    // entity → Buffs component (0 = none)
    private readonly Dictionary<nint, string> _buffId = new();     // StatusEffect addr → id (static per instance)
```

- [ ] **Step 3: Wire cache clears.** In `Poe2Live.cs`, add `_buffsAddr` + `_buffId` to the same three clear/remove sites `_mods` uses:
  - The per-area clear (~line 471, the `_mods.Clear(); ... _monolithCache.Clear();` line): append `_buffsAddr.Clear(); _buffId.Clear();`
  - `Rebind()` (~line 107, the `_itemIdent.Clear(); _idAt.Clear(); _monolithCache.Clear();` line): append `_buffsAddr.Clear(); _buffId.Clear();`
  - `EvictEntity()` (~line 565, the `_mods.Remove(entity); ... _monolithCache.Remove(entity);` line): append `_buffsAddr.Remove(entity);` (leave `_buffId` — keyed by StatusEffect addr, bounded per zone, cleared on zone change).

- [ ] **Step 4: Add the `Buffs()` method.** In `Poe2Live.cs` (near `ReadMods`, ~700), add:

```csharp
    /// <summary>Active buffs on an entity: walk the Buffs component's StatusEffect vector, decode each buff's
    /// internal id (cached per StatusEffect — static per instance) + timer. Empty when the feature is off
    /// (EnableBuffReads) or the entity has no Buffs component. Read-only; the only new memory read this release.</summary>
    public IReadOnlyList<BuffState> Buffs(nint entity)
    {
        var result = new List<BuffState>();
        if (!EnableBuffReads) return result;
        if (!_buffsAddr.TryGetValue(entity, out var comp)) { comp = ResolveComponent(entity, "Buffs"); _buffsAddr[entity] = comp; }
        if (comp == 0) return result;

        var first = Ptr(comp + Poe2.BuffsComponent.BuffVector);
        if (first == 0 || !_reader.TryReadStruct<nint>(comp + Poe2.BuffsComponent.BuffVector + 8, out var last) || last == 0) return result;
        var count = (int)(((long)last - (long)first) / 8);
        if (count <= 0 || count > 128) return result;   // sanity bound

        for (var i = 0; i < count; i++)
        {
            var se = Ptr(first + (nint)(i * 8));
            if (se == 0) continue;
            if (!_buffId.TryGetValue(se, out var id))
            {
                var def = Ptr(se + Poe2.StatusEffect.Definition);
                var idPtr = def == 0 ? 0 : Ptr(def + Poe2.BuffDefinition.IdPtr);
                id = idPtr == 0 ? "" : _reader.ReadStringUtf16(idPtr, 128);
                _buffId[se] = id;
            }
            if (string.IsNullOrEmpty(id)) continue;
            _reader.TryReadStruct<float>(se + Poe2.StatusEffect.Timer, out var t);
            var perm = float.IsInfinity(t) || float.IsNaN(t) || t <= 0f;
            result.Add(new BuffState(id, perm ? 0f : t, perm));
        }
        return result;
    }
```

- [ ] **Step 5: Build + tests.** `dotnet build src/POE2Radar.Overlay -c Debug` → 0 CS errors. `dotnet test tests/POE2Radar.Tests` → 268 + BuffCatalog green (no regressions).

- [ ] **Step 6: Commit.**
```bash
git add src/POE2Radar.Core/Game/Poe2Offsets.cs src/POE2Radar.Core/Game/Poe2Live.cs
git commit -m "feat(buffs): validated Buffs/StatusEffect offsets + Poe2Live.Buffs read (id cache + EnableBuffReads gate)"
```

---

### Task 3: World-rate spec + snapshot + render-frame (Overlay)

**Files:**
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (`BuffNameplateSpec` + `_buffFrame`; `BuildBuffSpecs()`; `WorldSnapshot.BuffSpecs`; call site; Tick conversion + else-clear; `RenderContext` args; diagnostic accumulator for `/api/buffs`)
- Modify: `src/POE2Radar.Overlay/Overlay/RenderContext.cs` (`BuffNameplateTarget` + `BuffTargets`/`BuffNameplates` fields)

**Interfaces:**
- Consumes: `BuffCatalog.Shared.Select`, `Poe2Live.Buffs`, `Poe2Live.BuffState`, `_live.EnableBuffReads`, `_live.TryBarComponents`, `_settings.BuffNameplates` (Task 5 defines the settings type — see note).
- Produces: `WorldSnapshot.BuffSpecs`, `RenderContext.BuffTargets` (`IReadOnlyList<BuffNameplateTarget>`), `BuffNameplateTarget(Vector3 World, BuffLine[] Lines)`; a `Func<object> _buffsDiag` provider (observed ids+tiers) for Task 5.

**Note on ordering:** Task 3 references `_settings.BuffNameplates` (defined in Task 5). To let Task 3 build standalone, **Task 3 also adds the minimal `BuffNameplateSettings` class + the `RadarSettings.BuffNameplates` property** (the fields Task 5 will finalize). If the class already exists when Task 3 runs, extend it; else create it here with: `Enabled=false`, `Tier="NotableAndAbove"`, `ShowOnRare=true`, `ShowOnUnique=true`, `ShowOnMagic=false`, `DisplayAll=false`, `AlwaysShow=new()`, `Hide=new()`, `MaxLines=4`, `OffsetY=18f`, `DeadlyColor="#FF3333"`, `NotableColor="#FF9900"`, `MinorColor="#66CCFF"`. (Task 5 adds the API/dashboard for it.)

- [ ] **Step 1: Add the settings class + property** (moved here so Task 3 builds). In `src/POE2Radar.Overlay/Config/RadarSettings.cs`, near the `AffixNameplates` property (~241):

```csharp
    // ── Buff icons: opt-in tier-colored buff tags below elite monsters. Off by default. ──
    public BuffNameplateSettings BuffNameplates { get; set; } = new();
```
and near the `AffixNameplateSettings` class (~457):

```csharp
public sealed class BuffNameplateSettings
{
    public bool Enabled { get; set; } = false;                 // opt-in
    public string Tier { get; set; } = "NotableAndAbove";      // Deadly | NotableAndAbove | All
    public List<string> AlwaysShow { get; set; } = new();      // buff ids always shown
    public List<string> Hide { get; set; } = new();            // buff ids never shown
    public bool DisplayAll { get; set; } = false;              // diagnostic: show every buff id (incl. junk)
    public bool ShowOnRare { get; set; } = true;
    public bool ShowOnUnique { get; set; } = true;
    public bool ShowOnMagic { get; set; } = false;
    public int MaxLines { get; set; } = 4;
    public float OffsetY { get; set; } = 18f;                  // px BELOW the mob (affixes sit above)
    public string DeadlyColor { get; set; } = "#FF3333";
    public string NotableColor { get; set; } = "#FF9900";
    public string MinorColor { get; set; } = "#66CCFF";
}
```

- [ ] **Step 2: Add the spec record + render scratch.** In `RadarApp.cs`, beside `AffixNameplateSpec`/`_affixFrame` (~198–199):

```csharp
    private readonly record struct BuffNameplateSpec(nint Render, POE2Radar.Core.Game.BuffLine[] Lines);
    private readonly List<BuffNameplateTarget> _buffFrame = new();   // render-thread scratch (rebuilt per frame)
    private volatile IReadOnlyList<(string Id, string Tier)> _buffsSeen = System.Array.Empty<(string, string)>(); // /api/buffs diagnostic
```

- [ ] **Step 3: Add `BuildBuffSpecs()`.** In `RadarApp.cs`, beside `BuildAffixSpecs` (~1959):

```csharp
    private List<BuffNameplateSpec> BuildBuffSpecs()
    {
        var specs = new List<BuffNameplateSpec>();
        var cfg = _settings.BuffNameplates;
        _live.EnableBuffReads = cfg.Enabled;      // gate the read (stealth): off → Poe2Live.Buffs no-ops
        if (!cfg.Enabled) { _buffsSeen = System.Array.Empty<(string, string)>(); return specs; }
        var threshold = cfg.Tier switch
        {
            "All" => POE2Radar.Core.Game.BuffTier.Minor,
            "NotableAndAbove" => POE2Radar.Core.Game.BuffTier.Notable,
            _ => POE2Radar.Core.Game.BuffTier.Deadly,
        };
        var filter = new POE2Radar.Core.Game.BuffFilter(threshold,
            new HashSet<string>(cfg.AlwaysShow), new HashSet<string>(cfg.Hide),
            cfg.DisplayAll, Math.Clamp(cfg.MaxLines, 1, 10));
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);   // diagnostic: id → tier
        foreach (var e in _entities)
        {
            if (e.Category != Poe2Live.EntityCategory.Monster) continue;
            var on = e.Rarity switch
            {
                Poe2Live.Rarity.Magic  => cfg.ShowOnMagic,
                Poe2Live.Rarity.Rare   => cfg.ShowOnRare,
                Poe2Live.Rarity.Unique => cfg.ShowOnUnique,
                _                      => false,
            };
            if (!on) continue;
            var buffs = _live.Buffs(e.Address);
            if (buffs.Count == 0) continue;
            // adapt BuffState → the catalog's tuple shape
            var tuples = new (string, float, bool)[buffs.Count];
            for (var i = 0; i < buffs.Count; i++) { var b = buffs[i]; tuples[i] = (b.Id, b.Timer, b.Permanent); seen[b.Id] = POE2Radar.Core.Game.BuffCatalog.Shared.Resolve(b.Id).Tier.ToString(); }
            var lines = POE2Radar.Core.Game.BuffCatalog.Shared.Select(tuples, filter);
            if (lines.Count == 0) continue;
            if (!_live.TryBarComponents(e.Address, out var render, out _)) continue;
            specs.Add(new BuffNameplateSpec(render, System.Linq.Enumerable.ToArray(lines)));
        }
        _buffsSeen = seen.Select(kv => (kv.Key, kv.Value)).ToArray();   // publish for /api/buffs diagnostic
        return specs;
    }
```

- [ ] **Step 4: Call it + publish in the snapshot.** In `RadarApp.cs` beside `var affixSpecs = BuildAffixSpecs();` (~1710) add `var buffSpecs = BuildBuffSpecs();`. Add `BuffSpecs` to the `WorldSnapshot` record (after `AffixSpecs`, as a trailing optional param to avoid churn) and pass `buffSpecs` in the `new WorldSnapshot(...)` construction (~1821) and add `Array.Empty<BuffNameplateSpec>()` to `WorldSnapshot.Empty`:
  - Record decl: add `IReadOnlyList<BuffNameplateSpec>? BuffSpecs = null,` in the optional-params section (beside `EntityArrowSpecs`).
  - Construction: add `BuffSpecs: buffSpecs` to the named args of `new WorldSnapshot(...)`.
  - `Empty`: add `BuffSpecs: Array.Empty<BuffNameplateSpec>()`.

- [ ] **Step 5: Convert to render frame in `Tick()`.** In `RadarApp.cs`, right after the affix-frame block (~1321), add:

```csharp
            // Buff nameplates: same HP-bar pattern — re-read each mob's live world pos this frame.
            _buffFrame.Clear();
            if (snap.BuffSpecs is { Count: > 0 } buffSpecs && _settings.BuffNameplates.Enabled)
            {
                foreach (var spec in buffSpecs)
                    if (_liveRender.TryLiveBarAt(spec.Render, 0, out var w, out _, out _))
                        _buffFrame.Add(new BuffNameplateTarget(w, spec.Lines));
            }
```
And in the not-fresh `else` block (~1391) append: `if (_buffFrame.Count > 0) _buffFrame.Clear();`

- [ ] **Step 6: Pass into RenderContext.** In `RadarApp.cs` beside `AffixTargets:`/`AffixNameplates:` (~1529): add `BuffTargets: _buffFrame, BuffNameplates: _settings.BuffNameplates,`. In `RenderContext.cs`, after the `AffixNameplates` field (~254) add:

```csharp
    IReadOnlyList<BuffNameplateTarget>?   BuffTargets        = null,
    Config.BuffNameplateSettings?         BuffNameplates     = null,
```
and after the `AffixNameplateTarget` record (~41) add:

```csharp
/// <summary>One mob whose buff tags should be drawn this frame. World = live position (re-read each frame);
/// Lines = pre-filtered buff labels (with timers) from BuildBuffSpecs. Drawn BELOW the mob.</summary>
public readonly record struct BuffNameplateTarget(Vector3 World, POE2Radar.Core.Game.BuffLine[] Lines);
```

- [ ] **Step 7: Build.** `dotnet build src/POE2Radar.Overlay -c Debug` → 0 CS errors (DrawBuffNameplates doesn't exist yet — that's Task 4; `_buffFrame`/`BuffTargets` are just carried, not drawn, so this builds).

- [ ] **Step 8: Commit.**
```bash
git add src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Overlay/RenderContext.cs src/POE2Radar.Overlay/Config/RadarSettings.cs
git commit -m "feat(buffs): BuildBuffSpecs (stealth-gated, elites-only) + snapshot/frame plumbing + settings"
```

---

### Task 4: Render the buff tags (Overlay)

**Files:**
- Modify: `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (`DrawBuffNameplates` + call site)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (SR-5 CameraMatrix gate)

**Interfaces:**
- Consumes: `RenderContext.BuffTargets`, `RenderContext.BuffNameplates`, `ctx.CameraMatrix`, `BuffLine`, `BuffTier`, existing `_bPanel`/`_bStyle`/`_tf`/`ParseColor`.

- [ ] **Step 1: Add `DrawBuffNameplates`.** In `OverlayRenderer.cs`, after `DrawAffixNameplates` (~711), add (clone; stacks DOWNWARD from `OffsetY` so it sits below the mob, below the HP bar/affixes):

```csharp
    /// <summary>Tier-colored buff tags drawn BELOW each elite mob (affixes sit above). Same camera-matrix
    /// projection as affix nameplates; lines pre-filtered/pre-formatted (with timers) by BuildBuffSpecs.</summary>
    private void DrawBuffNameplates(ID2D1RenderTarget rt, RenderContext ctx)
    {
        var cfg = ctx.BuffNameplates;
        if (cfg is null || !cfg.Enabled) return;
        if (ctx.CameraMatrix is not { } m || ctx.BuffTargets is not { Count: > 0 } targets) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        var deadly  = ParseColor(cfg.DeadlyColor,  1f);
        var notable = ParseColor(cfg.NotableColor, 1f);
        var minor   = ParseColor(cfg.MinorColor,   1f);
        const float lineH = 15f;
        foreach (var t in targets)
        {
            var w = t.World;
            var cw = w.X*m[3] + w.Y*m[7] + w.Z*m[11] + m[15];
            if (cw <= 0.0001f) continue;
            var cxp = w.X*m[0] + w.Y*m[4] + w.Z*m[8] + m[12];
            var cyp = w.X*m[1] + w.Y*m[5] + w.Z*m[9] + m[13];
            var sx = (cxp/cw/2f + 0.5f) * W;
            var sy = (0.5f - cyp/cw/2f) * H;
            if (sx < 0 || sx > W || sy < 0 || sy > H) continue;

            var lines = t.Lines;
            if (lines.Length == 0) continue;
            var longest = 0; foreach (var l in lines) if (l.Text.Length > longest) longest = l.Text.Length;
            float panelW = MathF.Max(60f, 4.5f * longest + 8f);
            float topY = sy + cfg.OffsetY;                          // stack DOWNWARD from OffsetY (below the mob)
            var panel = new Vortice.RawRectF(sx - panelW/2f, topY, sx + panelW/2f, topY + lines.Length * lineH);
            rt.FillRectangle(panel, _bPanel!);
            float cy = topY;
            foreach (var line in lines)
            {
                _bStyle!.Color = line.Tier switch
                {
                    POE2Radar.Core.Game.BuffTier.Deadly  => deadly,
                    POE2Radar.Core.Game.BuffTier.Notable => notable,
                    _                                    => minor,
                };
                rt.DrawText(line.Text, _tf!,
                    new Rect(sx - panelW/2f + 3f, cy + 1f, sx + panelW/2f - 2f, cy + lineH),
                    _bStyle, DrawTextOptions.Clip);
                cy += lineH;
            }
        }
    }
```

- [ ] **Step 2: Call it.** In `OverlayRenderer.cs` after `DrawAffixNameplates(rt, ctx);` (~136), add `DrawBuffNameplates(rt, ctx);`

- [ ] **Step 3: Extend the CameraMatrix gate.** In `RadarApp.cs` (~1287), add `|| _settings.BuffNameplates.Enabled` to the `_cameraMatrix = (...)` condition (so the matrix is read when buff tags need it):
```csharp
                || _settings.AffixNameplates.Enabled || _settings.BuffNameplates.Enabled || _settings.GroundItems.Enabled
```

- [ ] **Step 4: Build.** `dotnet build src/POE2Radar.Overlay -c Debug` → 0 CS errors.

- [ ] **Step 5: Commit.**
```bash
git add src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(buffs): DrawBuffNameplates (tier-colored tags below elites) + CameraMatrix gate"
```
**Live smoke:** enable Buff Icons, stand near a rare/unique with an aura → tier-colored tag(s) appear below it.

---

### Task 5: API round-trip + diagnostic feed (Overlay)

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (`/api/buff-nameplates` route + `TryParseBuffNameplates`; `/api/buffs` diagnostic route + constructor provider)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (pass a `buffsDiag` provider into `new ApiServer(...)`)

**Interfaces:**
- Consumes: `_settings.BuffNameplates`, `BuffNameplateSettings`, existing `IsLoopbackHost`, `Write`, `Json`, `ValidHexOr`, `SanitizeStringList`, `ReadBody`.
- Produces: `GET/POST /api/buff-nameplates`; `GET /api/buffs` (diagnostic: `[{id,tier}]`).

- [ ] **Step 1: Add the settings route.** In `ApiServer.Handle` beside `case "/api/affix-nameplates":` (~949), add:

```csharp
            case "/api/buff-nameplates":
            {
                if (ctx.Request.HttpMethod == "GET")
                    Write(ctx, 200, JsonSerializer.Serialize(_settings.BuffNameplates, Json));
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                    if (TryParseBuffNameplates(ReadBody(ctx), out var bn))
                    {
                        _settings.BuffNameplates = bn; _settings.Save();
                        Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, buffNameplates = bn }, Json));
                    }
                    else Write(ctx, 400, JsonSerializer.Serialize(new { error = "bad body" }, Json));
                }
                else Write(ctx, 405, JsonSerializer.Serialize(new { error = "method" }, Json));
                break;
            }
            case "/api/buffs":
                if (ctx.Request.HttpMethod != "GET") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                Write(ctx, 200, JsonSerializer.Serialize(_buffsDiag(), Json));
                break;
```

- [ ] **Step 2: Add `TryParseBuffNameplates`.** In `ApiServer.cs` beside `TryParseAffixNameplates` (~1495):

```csharp
    private static bool TryParseBuffNameplates(string body, out Config.BuffNameplateSettings bn)
    {
        bn = new Config.BuffNameplateSettings();
        try
        {
            var p = JsonSerializer.Deserialize<Config.BuffNameplateSettings>(body, Json);
            if (p == null) return false;
            p.MaxLines = Math.Clamp(p.MaxLines, 1, 10);
            p.OffsetY = Math.Clamp(p.OffsetY, -200f, 200f);
            p.Tier = p.Tier is "All" or "NotableAndAbove" or "Deadly" ? p.Tier : "NotableAndAbove";
            p.DeadlyColor = ValidHexOr(p.DeadlyColor, "#FF3333");
            p.NotableColor = ValidHexOr(p.NotableColor, "#FF9900");
            p.MinorColor = ValidHexOr(p.MinorColor, "#66CCFF");
            p.AlwaysShow = SanitizeStringList(p.AlwaysShow);
            p.Hide = SanitizeStringList(p.Hide);
            bn = p;
            return true;
        }
        catch (JsonException) { return false; }
    }
```
(Confirm `Config.BuffNameplateSettings` is the right namespace — mirror how `TryParseAffixNameplates` references `AffixNameplateSettings`; if that one uses an unqualified name, match it.)

- [ ] **Step 3: Add the diagnostic provider field + constructor param.** In `ApiServer.cs`, add a field `private readonly Func<object> _buffsDiag;` beside the other `Func<object>` providers, a constructor parameter `Func<object> buffsDiag` (place beside the other provider params; give it a default `= null!` only if needed to avoid reordering — prefer adding as a required param and updating the one call site), and assign `_buffsDiag = buffsDiag;` in the constructor body.

- [ ] **Step 4: Wire the provider from RadarApp.** In `RadarApp.cs` `new ApiServer(...)`, add the argument `buffsDiag: () => new { buffs = _buffsSeen.Select(x => new { id = x.Id, tier = x.Tier }) }` (matching the named-arg style used for the other providers).

- [ ] **Step 5: Build.** `dotnet build src/POE2Radar.Overlay -c Debug` → 0 CS errors.

- [ ] **Step 6: Commit.**
```bash
git add src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(buffs): /api/buff-nameplates round-trip + /api/buffs diagnostic feed"
```

---

### Task 6: Dashboard "Buff Icons" card (Overlay)

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (card HTML + `bn` JS: load/render/save/wire + diagnostic list)

**Interfaces:** Consumes `/api/buff-nameplates` (GET/POST) + `/api/buffs` (diagnostic). Mirrors the `an` affix pattern with a `bn` object + `data-bn` attributes.

- [ ] **Step 1: Add the card HTML.** In `DashboardHtml.cs`, beside the affix-nameplates card (~835–850), add:

```html
          <div class="card collapsed" data-card="buff-nameplates">
            <h3>Buff icons <small class="tag">opt-in</small></h3>
            <div class="row"><div class="rl">Show buffs on elite monsters<small>tier-colored tags below the mob — off by default; reads nothing when off</small></div>
              <label class="sw"><input type="checkbox" data-bn="enabled"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Danger tier</div>
              <select class="numin" data-bn="tier"><option value="Deadly">Deadly only</option><option value="NotableAndAbove">Deadly + Notable</option><option value="All">All buffs</option></select></div>
            <div class="row"><div class="rl">Display ALL buff ids<small>diagnostic — show every buff (incl. engine junk) to help grow the catalog</small></div>
              <label class="sw"><input type="checkbox" data-bn="displayAll"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">On Rare</div><label class="sw"><input type="checkbox" data-bn="showOnRare"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">On Unique</div><label class="sw"><input type="checkbox" data-bn="showOnUnique"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">On Magic</div><label class="sw"><input type="checkbox" data-bn="showOnMagic"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Max lines</div><input type="number" class="numin" data-bn="maxLines" min="1" max="10"></div>
            <div class="row"><div class="rl hint-row">Observed buffs this session (from nearby elites) — turn on "Display ALL" to populate:</div></div>
            <div id="bnObserved" style="max-height:200px;overflow:auto"></div>
          </div>
```

- [ ] **Step 2: Declare + load `bn`.** In `DashboardHtml.cs` beside `let an=null, anCatalog=[];` (~1160) add `let bn=null;`. In `loadSettings()` beside the affix fetch (~1127) add:
```javascript
    bn = await getJSON('/api/buff-nameplates').catch(()=>null); renderBuffNameplates();
```

- [ ] **Step 3: Add render/save/wire + diagnostic.** In `DashboardHtml.cs` beside the affix JS block (~2318–2356), add:

```javascript
/* ── buff icons card (own endpoint /api/buff-nameplates) ── */
function renderBuffNameplates(){
  if(!bn) return;
  document.querySelectorAll('[data-bn]').forEach(el=>{
    const k=el.dataset.bn;
    if(el.type==='checkbox') el.checked=!!bn[k];
    else if(bn[k]!==undefined) el.value=bn[k];
  });
}
async function saveBuffNameplates(){
  try{ await fetch('/api/buff-nameplates',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(bn)});
    const m=$('#savedMsg'); if(m){m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100);} }catch(e){}
}
function wireBuffNameplates(){
  document.querySelectorAll('[data-bn]').forEach(el=>{
    const k=el.dataset.bn;
    el.onchange=()=>{ bn = bn||{}; bn[k] = el.type==='checkbox'?el.checked : (el.type==='number'?parseInt(el.value||'0',10):el.value); saveBuffNameplates(); };
  });
}
async function renderBnObserved(){
  const box=document.getElementById('bnObserved'); if(!box) return;
  try{ const r=await getJSON('/api/buffs'); const list=r.buffs||[];
    box.innerHTML = list.length ? list.slice(0,200).map(b=>`<div class="row"><div class="rl">${b.id} <small>${b.tier}</small></div></div>`).join('')
                                : '<div class="row"><div class="rl"><small>none observed yet</small></div></div>';
  }catch(e){}
}
```
Add `wireBuffNameplates();` to the wiring line (~2356, beside `wireAffixNameplates();`). Hook `renderBnObserved()` into the existing dashboard poll (mirror how the preload diagnostic panel refreshes — call it in the same interval that refreshes other diagnostic panels, or on settings-tab activation).

- [ ] **Step 4: Build.** `dotnet build src/POE2Radar.Overlay -c Debug` → 0 CS errors.

- [ ] **Step 5: Commit.**
```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(buffs): dashboard Buff Icons card + observed-buffs diagnostic panel"
```
**Live smoke:** open dashboard → Buff Icons card toggles/saves; "Display ALL" + stand near elites → observed ids populate.

---

### Task 7: Integration + version + compliance

**Files:** Modify `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (`<Version>` → `0.19.0`)

- [ ] **Step 1: Bump version** `0.18.0` → `0.19.0`.
- [ ] **Step 2: Full build** `dotnet build -c Debug` → 0 CS errors (all projects).
- [ ] **Step 3: Full tests** `dotnet test tests/POE2Radar.Tests` → all green (268 + BuffCatalog).
- [ ] **Step 4: Compliance** `powershell -File scripts/compliance-gate.ps1` → PASS; `scripts/scrub-strings.ps1 -SelfTest` → PASS.
- [ ] **Step 5: Commit.**
```bash
git add src/POE2Radar.Overlay/POE2Radar.Overlay.csproj
git commit -m "chore(release): bump version to 0.19.0 (Buff Icons)"
```

---

## Self-Review

**Spec coverage:** validated read (T2 offsets+Poe2Live.Buffs) ✓; curated catalog + heuristic + junk + prettify + diagnostic-grow (T1 BuffCatalog+JSON) ✓; stealth gate default-OFF/elites-only/id-cache (T2 EnableBuffReads + T3 BuildBuffSpecs) ✓; text tags via camera pipeline below the mob (T3 spec/frame + T4 DrawBuffNameplates) ✓; settings + API + diagnostic feed (T5) ✓; dashboard card + observed panel (T6) ✓; version/compliance (T7) ✓. Unit tests: BuffCatalog (T1). Live smoke: T4/T6.

**Placeholder scan:** none — every code step is complete. The two cross-task notes (T1 tuple shape to avoid a Poe2Live dependency cycle; T3 defining the settings class early so it builds standalone) are explicit decisions, not gaps.

**Type consistency:** `BuffTier`/`BuffInfo`/`BuffLine`/`BuffFilter` (Core, T1); `BuffCatalog.Select` takes `(string Id, float Timer, bool Permanent)` tuples (T1) which T3 adapts from `Poe2Live.BuffState` (T2); `BuffNameplateSpec(Render, BuffLine[])` (T3); `BuffNameplateTarget(World, BuffLine[])` (T3); `BuffNameplateSettings` (T3, consumed T4/T5/T6); `EnableBuffReads`/`Buffs()`/`BuffState` (T2); `_buffsDiag` provider (T3 publishes `_buffsSeen`, T5 serves it). Offsets `Poe2.BuffsComponent.BuffVector`/`Poe2.StatusEffect.{Definition,Timer}`/`Poe2.BuffDefinition.IdPtr` (T2) used by `Buffs()` (T2). Consistent.
