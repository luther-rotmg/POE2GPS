# Affix Nameplates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An opt-in overlay feature that draws an elite monster's chosen modifiers as floating, tier-colored text above its head in screen space, reusing the camera world→screen projection and the HP-bar spec/render path.

**Architecture:** A pure Core `MonsterAffixCatalog` (curated id→{name,tier} JSON + auto-prettify + filter, unit-tested) is the heart. The world thread builds an `AffixNameplateSpec` per qualifying monster (mirroring `BuildHpSpecs`) and publishes it in `WorldSnapshot`; the render thread re-reads each mob's live world position via `TryLiveBarAt` and draws the tiered text with the same camera matrix HP bars use. All gated behind a default-OFF setting.

**Tech Stack:** C#/.NET 10 (`net10.0-windows`, x64), xUnit (Core-only test project), Vortice.Direct2D1 (`DrawText`), embedded JSON resource (same mechanism as `ItemModTranslator`).

## Global Constraints

- **Strictly read-only.** Only existing reads (monster mods via `ReadMods`, camera matrix via `CameraMatrix`, world pos via `TryLiveBarAt`) + draw text. NO new offsets, NO input/process-write APIs.
- **No Core→Vortice dependency.** `MonsterAffixCatalog` is pure Core.
- **Default OFF.** `AffixNameplateSettings.Enabled = false`. The card ships collapsed.
- **Loopback-gated config writes.** Every settings POST calls `IsLoopbackHost` before mutating.
- **Gates stay green:** `scripts/compliance-gate.ps1` and `scripts/scrub-strings.ps1 -SelfTest`. `Program.cs` console title untouched.
- **README badge stays `0.5.4`.** Version → `0.12.0` in `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj`.
- **Camera projection arithmetic is copied verbatim** from `DrawNameplates` (non-standard index layout — see Task 5). The render thread uses `_liveRender`, never `_live`. `Vector3` is `POE2Radar.Core.Game.Vector3` (blittable), not `System.Numerics`.

### Build & test commands

- Build Core: `dotnet build src/POE2Radar.Core/POE2Radar.Core.csproj -c Release` → 0/0.
- Build Overlay: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release` → 0/0.
- Tests: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Release -p:Platform=x64` → all pass.

---

## File structure

| File | Responsibility | Task |
|---|---|---|
| `src/POE2Radar.Core/Game/MonsterAffixCatalog.cs` (new) | curated+prettify resolve, filter Select, Lazy singleton | 1 |
| `src/POE2Radar.Core/Game/poe2_monster_mod_names.json` (new) | curated id→{name,tier} starter masterlist | 1 |
| `src/POE2Radar.Core/POE2Radar.Core.csproj` | register the embedded JSON | 1 |
| `tests/POE2Radar.Tests/MonsterAffixCatalogTests.cs` (new) | Resolve + Select unit tests | 1 |
| `src/POE2Radar.Overlay/Config/RadarSettings.cs` | `AffixNameplateSettings` + property | 2 |
| `src/POE2Radar.Overlay/RadarApp.cs` | `AffixNameplateSpec`, `BuildAffixSpecs`, `WorldSnapshot` field, RenderContext wiring | 3, 4 |
| `src/POE2Radar.Overlay/Overlay/RenderContext.cs` | `AffixSpecs` + `AffixNameplates` fields | 4 |
| `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` | `DrawAffixNameplates` + Render() call | 5 |
| `src/POE2Radar.Overlay/Web/ApiServer.cs` | `/api/affix-nameplates` + `/api/affix-catalog` | 6 |
| `src/POE2Radar.Overlay/Web/DashboardHtml.cs` | card + override editor JS | 7 |
| `CLAUDE.md`, `CHANGELOG.md`, `*.csproj` | doc fix + release | 8 |

---

## Task 1: Core `MonsterAffixCatalog` + embedded masterlist + tests (the heart)

**Files:**
- Create: `src/POE2Radar.Core/Game/MonsterAffixCatalog.cs`
- Create: `src/POE2Radar.Core/Game/poe2_monster_mod_names.json`
- Modify: `src/POE2Radar.Core/POE2Radar.Core.csproj` (one `<EmbeddedResource>` line)
- Create: `tests/POE2Radar.Tests/MonsterAffixCatalogTests.cs`

**Interfaces produced (consumed by Tasks 3, 5, 6):**
- `enum AffixTier { Minor = 0, Notable = 1, Deadly = 2 }`
- `readonly record struct AffixInfo(string Name, AffixTier Tier)`
- `readonly record struct AffixLine(string Name, AffixTier Tier)`
- `readonly record struct AffixFilter(AffixTier Threshold, IReadOnlySet<string> AlwaysShow, IReadOnlySet<string> Hide, bool DisplayAll, int MaxLines)`
- `MonsterAffixCatalog.Shared` (singleton) with `AffixInfo Resolve(string modId)`, `IReadOnlyList<AffixLine> Select(IReadOnlyList<string> mobModIds, AffixFilter f)`, and `IReadOnlyDictionary<string,AffixInfo> Curated` (for the API catalog merge).

- [ ] **Step 1: Write the failing tests.** Create `tests/POE2Radar.Tests/MonsterAffixCatalogTests.cs`:
```csharp
using POE2Radar.Core.Game;

public class MonsterAffixCatalogTests
{
    static AffixFilter F(AffixTier t, bool all = false, int max = 4, string[]? show = null, string[]? hide = null)
        => new(t, new HashSet<string>(show ?? System.Array.Empty<string>()),
                  new HashSet<string>(hide ?? System.Array.Empty<string>()), all, max);

    [Fact] public void Resolve_prettifies_unknown_id()
    {
        var a = MonsterAffixCatalog.Shared.Resolve("MonsterExtraFast1");
        Assert.Equal("Extra Fast", a.Name);
        Assert.Equal(AffixTier.Minor, a.Tier);   // uncurated → Minor
    }

    [Fact] public void Select_tier_threshold_excludes_below()
    {
        // a Minor-tier (prettified) id under a Deadly threshold → not shown
        var lines = MonsterAffixCatalog.Shared.Select(new[] { "MonsterExtraFast1" }, F(AffixTier.Deadly));
        Assert.Empty(lines);
    }

    [Fact] public void Select_alwaysShow_promotes_below_threshold()
    {
        var lines = MonsterAffixCatalog.Shared.Select(new[] { "MonsterExtraFast1" },
            F(AffixTier.Deadly, show: new[] { "MonsterExtraFast1" }));
        Assert.Single(lines);
        Assert.Equal("Extra Fast", lines[0].Name);
    }

    [Fact] public void Select_hide_suppresses_even_under_displayAll()
    {
        var lines = MonsterAffixCatalog.Shared.Select(new[] { "MonsterExtraFast1" },
            F(AffixTier.Minor, all: true, hide: new[] { "MonsterExtraFast1" }));
        Assert.Empty(lines);
    }

    [Fact] public void Select_caps_at_maxLines()
    {
        var ids = new[] { "MonsterExtraFast1", "MonsterExtraFast2", "MonsterExtraFast3", "MonsterExtraFast4" };
        var lines = MonsterAffixCatalog.Shared.Select(ids, F(AffixTier.Minor, max: 2));
        Assert.Equal(2, lines.Count);
    }

    [Fact] public void Select_dedupes_by_name_and_orders_deadly_first()
    {
        // two ids resolving to the same prettified name collapse to one line
        var lines = MonsterAffixCatalog.Shared.Select(new[] { "MonsterExtraFast1", "MonsterExtraFast1" },
            F(AffixTier.Minor, max: 4));
        Assert.Single(lines);
    }
}
```

- [ ] **Step 2: Run the tests to confirm they fail.** `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Release -p:Platform=x64` → FAIL (`MonsterAffixCatalog` not defined).

- [ ] **Step 3: Create the starter curated masterlist** `src/POE2Radar.Core/Game/poe2_monster_mod_names.json`. Author a starter set of the well-known dangerous monster mods. **Validate/extend these ids against the real observed ids in `config/known_mods.json` (or a `--mods` run) during implementation** — any id that doesn't match simply isn't curated and auto-prettifies, so an imperfect starter table is safe:
```json
{
  "MonsterVolatile1": { "name": "Volatile (explodes on death)", "tier": "Deadly" },
  "MonsterDamageReflection1": { "name": "Reflects Damage", "tier": "Deadly" },
  "MonsterCannotBeStunned1": { "name": "Cannot be Stunned", "tier": "Notable" },
  "MonsterExtraFast1": { "name": "Extra Fast", "tier": "Notable" },
  "MonsterExtraLife1": { "name": "Extra Life", "tier": "Minor" },
  "MonsterPhysicalDamageAura1": { "name": "Physical Damage Aura", "tier": "Notable" },
  "MonsterColdDamageAura1": { "name": "Cold Damage Aura", "tier": "Notable" },
  "MonsterFireDamageAura1": { "name": "Fire Damage Aura", "tier": "Notable" },
  "MonsterLightningDamageAura1": { "name": "Lightning Damage Aura", "tier": "Notable" },
  "MonsterEnergyShieldAura1": { "name": "Energy Shield Aura", "tier": "Minor" },
  "MonsterManaSiphon1": { "name": "Mana Siphoner", "tier": "Notable" },
  "MonsterAlliesCannotDie1": { "name": "Allies cannot Die", "tier": "Deadly" },
  "MonsterReducedAreaOfEffect1": { "name": "Reduced Player AoE", "tier": "Minor" },
  "MonsterCriticalStrikes1": { "name": "Extra Critical Strikes", "tier": "Notable" }
}
```

- [ ] **Step 4: Register the embedded resource.** In `src/POE2Radar.Core/POE2Radar.Core.csproj`, inside the existing `<ItemGroup>` that already has the other `Game\*.json` `<EmbeddedResource>` entries, add one line:
```xml
    <EmbeddedResource Include="Game\poe2_monster_mod_names.json" />
```

- [ ] **Step 5: Implement `MonsterAffixCatalog`.** Create `src/POE2Radar.Core/Game/MonsterAffixCatalog.cs`. Mirror `ItemModTranslator`'s embedded-load + `Lazy<T>` singleton pattern:
```csharp
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace POE2Radar.Core.Game;

public enum AffixTier { Minor = 0, Notable = 1, Deadly = 2 }

public readonly record struct AffixInfo(string Name, AffixTier Tier);
public readonly record struct AffixLine(string Name, AffixTier Tier);

public readonly record struct AffixFilter(
    AffixTier Threshold, IReadOnlySet<string> AlwaysShow, IReadOnlySet<string> Hide, bool DisplayAll, int MaxLines);

/// <summary>Maps monster affix mod ids to a readable name + danger tier (curated table + auto-prettify
/// fallback), and selects the display lines for a mob given a filter. Pure (no memory access).</summary>
public sealed class MonsterAffixCatalog
{
    private static readonly Lazy<MonsterAffixCatalog> _shared =
        new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);
    public static MonsterAffixCatalog Shared => _shared.Value;

    private readonly Dictionary<string, AffixInfo> _curated;
    public IReadOnlyDictionary<string, AffixInfo> Curated => _curated;

    private MonsterAffixCatalog(Dictionary<string, AffixInfo> curated) => _curated = curated;

    private static readonly Regex SplitBoundary =
        new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=[0-9])", RegexOptions.Compiled);

    public AffixInfo Resolve(string modId)
    {
        if (_curated.TryGetValue(modId, out var info)) return info;
        return new AffixInfo(Prettify(modId), AffixTier.Minor);
    }

    /// <summary>Strip a leading "Monster" prefix, split camelCase/digit boundaries, title-case.</summary>
    public static string Prettify(string modId)
    {
        var s = modId.StartsWith("Monster", StringComparison.Ordinal) ? modId.Substring(7) : modId;
        if (s.Length == 0) return modId;
        var parts = SplitBoundary.Split(s).Where(p => p.Length > 0);
        var titled = parts.Select(p => p.Length == 1 ? p.ToUpperInvariant()
            : char.ToUpperInvariant(p[0]) + p.Substring(1));
        var name = string.Join(' ', titled).Trim();
        return name.Length == 0 ? modId : name;
    }

    public IReadOnlyList<AffixLine> Select(IReadOnlyList<string> mobModIds, AffixFilter f)
    {
        var picked = new List<AffixLine>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in mobModIds)
        {
            if (f.Hide.Contains(id)) continue;                 // hide overrides everything
            var info = Resolve(id);
            bool show = f.DisplayAll || f.AlwaysShow.Contains(id) || info.Tier >= f.Threshold;
            if (!show) continue;
            if (!seenNames.Add(info.Name)) continue;           // de-dup by display name
            picked.Add(new AffixLine(info.Name, info.Tier));
        }
        picked.Sort((a, b) => a.Tier != b.Tier
            ? b.Tier.CompareTo(a.Tier)                          // Deadly → Minor
            : string.CompareOrdinal(a.Name, b.Name));
        if (picked.Count > f.MaxLines) picked.RemoveRange(f.MaxLines, picked.Count - f.MaxLines);
        return picked;
    }

    private static MonsterAffixCatalog LoadEmbedded()
    {
        var curated = new Dictionary<string, AffixInfo>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.Contains("poe2_monster_mod_names", StringComparison.Ordinal));
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
                        var tier = tierStr switch { "Deadly" => AffixTier.Deadly, "Notable" => AffixTier.Notable, _ => AffixTier.Minor };
                        curated[p.Name] = new AffixInfo(nm, tier);
                    }
                }
            }
        }
        catch { /* fall back to empty curated table; everything prettifies */ }
        return new MonsterAffixCatalog(curated);
    }
}
```

- [ ] **Step 6: Run tests to confirm green.** `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Release -p:Platform=x64` → all pass (the 6 new tests + the existing suite).

- [ ] **Step 7: Commit.**
```bash
git add src/POE2Radar.Core/Game/MonsterAffixCatalog.cs src/POE2Radar.Core/Game/poe2_monster_mod_names.json src/POE2Radar.Core/POE2Radar.Core.csproj tests/POE2Radar.Tests/MonsterAffixCatalogTests.cs
git commit -m "feat(core): MonsterAffixCatalog — curated affix names+tiers, prettify, tier/override filter"
```

---

## Task 2: `AffixNameplateSettings` + RadarSettings property

**Files:** Modify `src/POE2Radar.Overlay/Config/RadarSettings.cs` (new sub-class near `HpBarSettings` ~line 402; property near `HpBars` ~line 213).

**Interfaces produced (consumed by Tasks 3, 4, 6):** `RadarSettings.AffixNameplates` of type `AffixNameplateSettings { bool Enabled; string Tier; List<string> AlwaysShow; List<string> Hide; bool DisplayAll; bool ShowOnRare; bool ShowOnUnique; bool ShowOnMagic; int MaxLines; float OffsetY; string DeadlyColor; string NotableColor; string MinorColor; }`.

- [ ] **Step 1: Add the sub-class** (near `HpBarSettings`):
```csharp
public sealed class AffixNameplateSettings
{
    public bool Enabled { get; set; } = false;            // opt-in
    public string Tier { get; set; } = "Deadly";          // threshold: Deadly | NotableAndAbove | All
    public List<string> AlwaysShow { get; set; } = new(); // mod ids always shown
    public List<string> Hide { get; set; } = new();       // mod ids never shown
    public bool DisplayAll { get; set; } = false;         // ignore threshold/overrides, show every affix
    public bool ShowOnRare { get; set; } = true;
    public bool ShowOnUnique { get; set; } = true;
    public bool ShowOnMagic { get; set; } = false;
    public int MaxLines { get; set; } = 4;
    public float OffsetY { get; set; } = -46f;            // px above the mob (clears the -30 HP bar)
    public string DeadlyColor { get; set; } = "#FF3333";
    public string NotableColor { get; set; } = "#FF9900";
    public string MinorColor { get; set; } = "#AAAAAA";
}
```
- [ ] **Step 2: Add the property on `RadarSettings`** (next to `public HpBarSettings HpBars { get; set; } = new();`):
```csharp
public AffixNameplateSettings AffixNameplates { get; set; } = new();
```
- [ ] **Step 3: Build Overlay.** `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release` → 0/0. (Round-trips via the existing camelCase serializer; no Load/Save change.)
- [ ] **Step 4: Commit.**
```bash
git add src/POE2Radar.Overlay/Config/RadarSettings.cs
git commit -m "feat(config): AffixNameplateSettings (default off) on RadarSettings"
```

---

## Task 3: `AffixNameplateSpec` + `BuildAffixSpecs` + WorldSnapshot wiring

**Files:** Modify `src/POE2Radar.Overlay/RadarApp.cs` (`AffixNameplateSpec` near `HpBarSpec` ~line 179; `BuildAffixSpecs` after `BuildHpSpecs` ~line 1570; `WorldSnapshot` record ~200-216 + `Empty`; the call ~line 1352 and publish ~line 1462).

**Interfaces produced (consumed by Task 4):** `private readonly record struct AffixNameplateSpec(nint Render, AffixLine[] Lines);` and `WorldSnapshot.AffixSpecs` (`IReadOnlyList<AffixNameplateSpec>`, positioned after `ItemLabels`).

- [ ] **Step 1: Add the spec record** below `HpBarSpec` (~line 179):
```csharp
private readonly record struct AffixNameplateSpec(nint Render, AffixLine[] Lines);
```
- [ ] **Step 2: Add `BuildAffixSpecs()`** immediately after `BuildHpSpecs()`:
```csharp
private List<AffixNameplateSpec> BuildAffixSpecs()
{
    var specs = new List<AffixNameplateSpec>();
    var cfg = _settings.AffixNameplates;
    if (!cfg.Enabled) return specs;
    var threshold = cfg.Tier switch
    {
        "All" => AffixTier.Minor,
        "NotableAndAbove" => AffixTier.Notable,
        _ => AffixTier.Deadly,
    };
    var filter = new AffixFilter(threshold,
        new HashSet<string>(cfg.AlwaysShow), new HashSet<string>(cfg.Hide),
        cfg.DisplayAll, Math.Clamp(cfg.MaxLines, 1, 10));
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
        if (e.ModList.Count == 0) continue;
        var lines = MonsterAffixCatalog.Shared.Select(e.ModList, filter);
        if (lines.Count == 0) continue;
        if (!_live.TryBarComponents(e.Address, out var render, out _)) continue;
        specs.Add(new AffixNameplateSpec(render, System.Linq.Enumerable.ToArray(lines)));
    }
    return specs;
}
```
Add `using POE2Radar.Core.Game;` at the top of `RadarApp.cs` if `AffixTier`/`AffixFilter`/`MonsterAffixCatalog` aren't already in scope.

- [ ] **Step 3: Add `AffixSpecs` to `WorldSnapshot`.** In the record, insert `IReadOnlyList<AffixNameplateSpec> AffixSpecs,` immediately after `IReadOnlyList<ItemLabelSpec> ItemLabels,`. In `WorldSnapshot.Empty`, insert `Array.Empty<AffixNameplateSpec>(),` in the matching position (after the `ItemLabelSpec` empty array).

- [ ] **Step 4: Build + publish.** In `WorldTick`, after `var hpSpecs = BuildHpSpecs();` (and after the shared `_resolveCache` is populated), add `var affixSpecs = BuildAffixSpecs();`. Update the single `_world = new WorldSnapshot(...)` publish call to pass `affixSpecs` in the new slot (after `itemLabels`):
```csharp
_world = new WorldSnapshot(true, areaHash, areaLevel, areaCode, _charLevel,
    _entities, _landmarks, _terrain, hpSpecs, itemLabels, affixSpecs, _selectedPaths, _legend, _selectedSnapshot);
```
- [ ] **Step 5: Build Overlay.** `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release` → 0/0 (compiler will flag any missed `WorldSnapshot` arg position).
- [ ] **Step 6: Run tests.** `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Release -p:Platform=x64` → pass.
- [ ] **Step 7: Commit.**
```bash
git add src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(radar): world-thread BuildAffixSpecs + publish AffixSpecs in WorldSnapshot"
```

---

## Task 4: AffixNameplateTarget frame + RenderContext plumbing

**Files:** Modify `src/POE2Radar.Overlay/RadarApp.cs` (new `AffixNameplateTarget` type beside `HpBarTarget`; new `_affixFrame` field; build it in `Tick()` from `snap.AffixSpecs`) and `src/POE2Radar.Overlay/Overlay/RenderContext.cs` (two trailing optional params).

**This mirrors the proven HP-bar render path EXACTLY** (the world-thread `AffixNameplateSpec` from Task 3 STAYS private — no visibility change): `snap.AffixSpecs` → `Tick()` reads each mob's live world pos via `_liveRender.TryLiveBarAt` and builds a reusable `_affixFrame` of `AffixNameplateTarget` → `RenderContext.AffixTargets` → the renderer (Task 5) just projects the pre-read world pos. This is identical to `snap.HpSpecs → _hpFrame (HpBarTarget) → ctx.HpBarTargets`.

**Interfaces produced (consumed by Task 5):** `AffixNameplateTarget(POE2Radar.Core.Game.Vector3 World, AffixLine[] Lines)` (mirror `HpBarTarget`'s visibility/location); `RenderContext.AffixTargets` (`IReadOnlyList<AffixNameplateTarget>?`) and `RenderContext.AffixNameplates` (`Config.AffixNameplateSettings?`).

- [ ] **Step 1: Define `AffixNameplateTarget`.** Find `HpBarTarget`'s definition (the accessible render-target type carried by `RenderContext.HpBarTargets`) and add an analogous type right beside it, SAME visibility + namespace/location:
```csharp
... AffixNameplateTarget(POE2Radar.Core.Game.Vector3 World, AffixLine[] Lines);
```
Use the Core blittable `Vector3` (exactly what `TryLiveBarAt` outputs) and `AffixLine` from `POE2Radar.Core.Game`.

- [ ] **Step 2: Build the per-frame target list in `Tick()`.** Find the existing `_hpFrame` reusable-list field and its build loop in `Tick()` (it `_hpFrame.Clear()`s, loops `snap.HpSpecs`, calls `_liveRender.TryLiveBarAt(spec.Render, spec.Life, out var w, ...)`, adds `HpBarTarget`s). Add a sibling field `private readonly List<AffixNameplateTarget> _affixFrame = new();` and, immediately after the `_hpFrame` loop, an analogous loop (life arg = 0 → position only, skips the HP read):
```csharp
_affixFrame.Clear();
if (snap.AffixSpecs is { Count: > 0 } affixSpecs && _settings.AffixNameplates.Enabled)
{
    foreach (var spec in affixSpecs)
        if (_liveRender.TryLiveBarAt(spec.Render, 0, out var w, out _, out _))
            _affixFrame.Add(new AffixNameplateTarget(w, spec.Lines));
}
```
Match the EXACT `_hpFrame` clear/guard idiom (e.g. if `_hpFrame` is only built inside an `in-game`/`worldFresh` block, place the `_affixFrame` build in the same block).

- [ ] **Step 3: Add two trailing optional params to `RenderContext`** (after `ZoneSummaryHud`, so existing call sites compile):
```csharp
    IReadOnlyList<AffixNameplateTarget>?  AffixTargets       = null,
    Config.AffixNameplateSettings?        AffixNameplates    = null);
```
- [ ] **Step 4: Wire the `Tick()` RenderContext construction** — mirror exactly how `HpBarTargets: _hpFrame` is passed; add the two named args:
```csharp
    AffixTargets: _affixFrame,
    AffixNameplates: _settings.AffixNameplates,
```
- [ ] **Step 5: Build Overlay.** `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release` → 0/0.
- [ ] **Step 6: Commit.**
```bash
git add src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Overlay/RenderContext.cs
git commit -m "feat(render): AffixNameplateTarget frame + carry it in RenderContext (HP-bar pattern)"
```

---

## Task 5: `DrawAffixNameplates` + Render() call

**Files:** Modify `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (new `DrawAffixNameplates` method; one call in `Render()` ~line 130).

**Interfaces consumed:** `RenderContext.AffixTargets` (already world-positioned in Tick — Task 4), `RenderContext.AffixNameplates`, `RenderContext.CameraMatrix`. `_tf` (12pt Consolas), `_bPanel`, `_bStyle` brushes. NO `TryLiveBarAt` call in the renderer — the world pos is pre-read in Tick.

- [ ] **Step 1: Add `DrawAffixNameplates`.** Copy the camera-projection block **verbatim** from `DrawNameplates` (the index layout is non-standard — `cw` uses `m[3]/m[7]/m[11]/m[15]`, `cx` uses `m[0]/m[4]/m[8]/m[12]`, `cy` uses `m[1]/m[5]/m[9]/m[13]`):
```csharp
private void DrawAffixNameplates(ID2D1RenderTarget rt, RenderContext ctx)
{
    var cfg = ctx.AffixNameplates;
    if (cfg is null || !cfg.Enabled) return;
    if (ctx.CameraMatrix is not { } m || ctx.AffixTargets is not { Count: > 0 } targets) return;
    float W = ctx.WindowWidth, H = ctx.WindowHeight;
    var deadly  = ColorFromHex(cfg.DeadlyColor,  ColItemText);
    var notable = ColorFromHex(cfg.NotableColor, ColItemText);
    var minor   = ColorFromHex(cfg.MinorColor,   ColItemText);
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
        var longest = 0; foreach (var l in lines) if (l.Name.Length > longest) longest = l.Name.Length;
        float panelW = MathF.Max(60f, 4.5f * longest + 8f);
        float topY = sy + cfg.OffsetY - lines.Length * lineH;     // stack upward from OffsetY
        var panel = new Vortice.RawRectF(sx - panelW/2f, topY, sx + panelW/2f, topY + lines.Length * lineH);
        rt.FillRectangle(panel, _bPanel!);
        float cy = topY;
        foreach (var line in lines)
        {
            _bStyle!.Color = line.Tier switch
            {
                AffixTier.Deadly  => deadly,
                AffixTier.Notable => notable,
                _                 => minor,
            };
            rt.DrawText(line.Name, _tf!,
                new Rect(sx - panelW/2f + 3f, cy + 1f, sx + panelW/2f - 2f, cy + lineH),
                _bStyle, DrawTextOptions.Clip);
            cy += lineH;
        }
    }
}
```
If the file lacks a hex→`Color4` helper, add a small private `ColorFromHex(string hex, Color4 fallback)` (or reuse the existing one — `DrawItemLabels`/`PackColor` show the codebase already parses hex; use whatever exists, e.g. `ColorFromU(PackColor(hex))`). Use `Rect` for `DrawText`, `RawRectF` for `FillRectangle` (the codebase distinction). Add `using POE2Radar.Core.Game;` for `AffixTier` if needed.

- [ ] **Step 2: Call it in `Render()`** — inside the `else if (ctx.Active && ctx.InGame)` block, immediately after `DrawItemLabels(rt, ctx);`:
```csharp
            DrawItemLabels(rt, ctx);
            DrawAffixNameplates(rt, ctx);
```
- [ ] **Step 3: Build Overlay.** `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release` → 0/0.
- [ ] **Step 4: Commit.**
```bash
git add src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs
git commit -m "feat(overlay): DrawAffixNameplates — tiered affix text above elite mobs (world-projected)"
```
**Live check (Task 8):** enable the feature; a Deadly-tier affix on a rare draws red text above its head, tracking the mob as it moves; nothing draws when disabled/unfocused; independent of HP bars.

---

## Task 6: API — `/api/affix-nameplates` (GET/POST) + `/api/affix-catalog`

**Files:** Modify `src/POE2Radar.Overlay/Web/ApiServer.cs` (two new `case` blocks in the request handler; a `TryParseAffixNameplates` helper). Mirrors `/api/keybinds` (loopback POST) + the `hpBars` whole-object parse pattern.

- [ ] **Step 1: Add the settings endpoint pair.** In the handler `switch`:
```csharp
case "/api/affix-nameplates":
{
    if (ctx.Request.HttpMethod == "GET")
        Write(ctx, 200, JsonSerializer.Serialize(_settings.AffixNameplates, Json));
    else if (ctx.Request.HttpMethod == "POST")
    {
        if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
        if (TryParseAffixNameplates(ReadBody(ctx), out var an))
        {
            _settings.AffixNameplates = an; _settings.Save();
            Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, affixNameplates = an }, Json));
        }
        else Write(ctx, 400, JsonSerializer.Serialize(new { error = "bad body" }, Json));
    }
    else Write(ctx, 405, JsonSerializer.Serialize(new { error = "method" }, Json));
    break;
}
case "/api/affix-catalog":
{
    var cat = POE2Radar.Core.Game.MonsterAffixCatalog.Shared;
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var items = new List<object>();
    foreach (var kv in cat.Curated)
    { seen.Add(kv.Key); items.Add(new { modId = kv.Key, name = kv.Value.Name, tier = kv.Value.Tier.ToString(), curated = true }); }
    foreach (var id in _knownMods())   // observed vocabulary (ModCatalog.All)
    { if (!seen.Add(id)) continue; var info = cat.Resolve(id); items.Add(new { modId = id, name = info.Name, tier = info.Tier.ToString(), curated = false }); }
    Write(ctx, 200, JsonSerializer.Serialize(new { affixes = items }, Json));
    break;
}
```
- [ ] **Step 2: Add `TryParseAffixNameplates`** (mirror `TryParseHpBars`: deserialize, clamp, sanitize, never throw):
```csharp
private static bool TryParseAffixNameplates(string body, out AffixNameplateSettings an)
{
    an = new AffixNameplateSettings();
    try
    {
        var p = JsonSerializer.Deserialize<AffixNameplateSettings>(body, Json);
        if (p == null) return false;
        p.MaxLines = Math.Clamp(p.MaxLines, 1, 10);
        p.OffsetY = Math.Clamp(p.OffsetY, -200f, 200f);
        p.Tier = p.Tier is "All" or "NotableAndAbove" or "Deadly" ? p.Tier : "Deadly";
        p.DeadlyColor = ValidHexOr(p.DeadlyColor, "#FF3333");
        p.NotableColor = ValidHexOr(p.NotableColor, "#FF9900");
        p.MinorColor = ValidHexOr(p.MinorColor, "#AAAAAA");
        an.AlwaysShow = Sanitize(p.AlwaysShow);   // trim, drop empties, dedupe, cap 128, max 64 chars
        an.Hide = Sanitize(p.Hide);
        p.AlwaysShow = an.AlwaysShow; p.Hide = an.Hide;
        an = p;
        return true;
    }
    catch (JsonException) { return false; }
}

private static List<string> Sanitize(List<string>? raw)
{
    var outp = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var x in raw ?? new List<string>())
    {
        var t = (x ?? "").Trim();
        if (t.Length is 0 or > 64) continue;
        if (seen.Add(t)) outp.Add(t);
        if (outp.Count >= 128) break;
    }
    return outp;
}
```
Use the existing `ValidHexOr` helper (already used by `TryParseHpBars`); `_knownMods` is the existing injected `Func<IReadOnlyList<string>>` field. Add `using ... .Config;` if `AffixNameplateSettings` isn't in scope.
- [ ] **Step 3: Build + test.** Overlay build 0/0; `dotnet test ... -p:Platform=x64` pass.
- [ ] **Step 4: Commit.**
```bash
git add src/POE2Radar.Overlay/Web/ApiServer.cs
git commit -m "feat(api): /api/affix-nameplates (GET/POST loopback) + /api/affix-catalog masterlist"
```

---

## Task 7: Dashboard card + override editor

**Files:** Modify `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (card HTML after the HP Bars card; JS: `loadSettings` hook, `renderAffixNameplates`, `saveAffixNameplates`, `wireAffixNameplates`, `loadAffixCatalog`).

- [ ] **Step 1: Add the card HTML** in the Settings view, after the HP Bars card, default-collapsed:
```html
<div class="card collapsed" data-card="affix-nameplates">
  <h3>Affix nameplates <small class="tag">opt-in</small></h3>
  <div class="row"><div class="rl">Show affixes above elite monsters<small>floating text on the mob's head — off by default</small></div>
    <label class="sw"><input type="checkbox" data-an="enabled"><span class="track"></span><span class="knob"></span></label></div>
  <div class="row"><div class="rl">Danger tier</div>
    <select class="numin" data-an="tier"><option value="Deadly">Deadly only</option><option value="NotableAndAbove">Deadly + Notable</option><option value="All">All affixes</option></select></div>
  <div class="row"><div class="rl">Display ALL affixes<small>ignore the filter — show every affix on the mob</small></div>
    <label class="sw"><input type="checkbox" data-an="displayAll"><span class="track"></span><span class="knob"></span></label></div>
  <div class="row"><div class="rl">On Rare</div><label class="sw"><input type="checkbox" data-an="showOnRare"><span class="track"></span><span class="knob"></span></label></div>
  <div class="row"><div class="rl">On Unique</div><label class="sw"><input type="checkbox" data-an="showOnUnique"><span class="track"></span><span class="knob"></span></label></div>
  <div class="row"><div class="rl">On Magic</div><label class="sw"><input type="checkbox" data-an="showOnMagic"><span class="track"></span><span class="knob"></span></label></div>
  <div class="row"><div class="rl">Max lines</div><input type="number" class="numin" data-an="maxLines" min="1" max="10"></div>
  <div class="row"><div class="rl">Per-affix overrides<small>search the masterlist; mark Always-show or Hide</small></div></div>
  <input id="anSearch" class="numin" placeholder="filter affixes…" style="width:100%">
  <div id="anOverrides" style="max-height:240px;overflow:auto"></div>
</div>
```
- [ ] **Step 2: Add the JS.** Declare `let an=null, anCatalog=[];`. In `loadSettings()` add: `an = await getJSON('/api/affix-nameplates').catch(()=>null); renderAffixNameplates();`. Implement:
```javascript
function renderAffixNameplates(){
  if(!an) return;
  document.querySelectorAll('[data-an]').forEach(el=>{
    const k=el.dataset.an;
    if(el.type==='checkbox') el.checked=!!an[k];
    else if(an[k]!==undefined) el.value=an[k];
  });
  renderAnOverrides();
}
async function saveAffixNameplates(){
  try{ await fetch('/api/affix-nameplates',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(an)});
    const m=$('#savedMsg'); if(m){m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100);} }catch(e){}
}
function wireAffixNameplates(){
  document.querySelectorAll('[data-an]').forEach(el=>{
    const k=el.dataset.an;
    const upd=()=>{ an = an||{}; an[k] = el.type==='checkbox'?el.checked : (el.type==='number'?parseInt(el.value||'0',10):el.value); saveAffixNameplates(); };
    if(el.type==='checkbox'||el.tagName==='SELECT') el.onchange=upd; else el.onchange=upd;
  });
  const s=document.getElementById('anSearch'); if(s) s.oninput=renderAnOverrides;
}
async function loadAffixCatalog(){ try{ const r=await getJSON('/api/affix-catalog'); anCatalog=r.affixes||[]; renderAnOverrides(); }catch(e){} }
function renderAnOverrides(){
  const box=document.getElementById('anOverrides'); if(!box||!an) return;
  const q=(document.getElementById('anSearch')?.value||'').toLowerCase();
  const always=new Set(an.alwaysShow||[]), hide=new Set(an.hide||[]);
  box.innerHTML = anCatalog.filter(a=>!q||a.name.toLowerCase().includes(q)||a.modId.toLowerCase().includes(q)).slice(0,300).map(a=>{
    const st = always.has(a.modId)?'show':hide.has(a.modId)?'hide':'';
    return `<div class="row" style="gap:6px"><div class="rl">${a.name} <small>${a.tier}${a.curated?'':' · seen'}</small></div>
      <select class="numin" data-anov="${a.modId}"><option value=""${st===''?' selected':''}>—</option><option value="show"${st==='show'?' selected':''}>Always</option><option value="hide"${st==='hide'?' selected':''}>Hide</option></select></div>`;
  }).join('');
  box.querySelectorAll('[data-anov]').forEach(sel=>{ sel.onchange=()=>{
    const id=sel.dataset.anov; an.alwaysShow=(an.alwaysShow||[]).filter(x=>x!==id); an.hide=(an.hide||[]).filter(x=>x!==id);
    if(sel.value==='show') an.alwaysShow.push(id); else if(sel.value==='hide') an.hide.push(id);
    saveAffixNameplates();
  };});
}
```
Call `wireAffixNameplates()` next to `wireSettings()` at init, and `loadAffixCatalog()` when the Settings tab activates (next to the other settings-tab loads). Use the existing `$`/`getJSON` helpers.
- [ ] **Step 3: Build Overlay.** `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release` → 0/0 (DashboardHtml is a C# string — a build is the syntax check).
- [ ] **Step 4: Commit.**
```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(dashboard): Affix nameplates card + per-affix override editor"
```
**Live check (Task 8):** the card is present (collapsed, opt-in badge), the master toggle + tier + display-all + rarity + max-lines persist across reload; the override search lists the masterlist and Always/Hide marks take effect.

---

## Task 8: Integration sweep, doc fix, release

**Files:** `CLAUDE.md` (camera-matrix doc fix), `CHANGELOG.md`, `README.md` (feature note), `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (version).

- [ ] **Step 1: Full build + tests + gates.**
```bash
dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Release -p:Platform=x64
```
Then via PowerShell: `scripts/compliance-gate.ps1` and `scripts/scrub-strings.ps1 -SelfTest` — both green.
- [ ] **Step 2: Fix the stale CLAUDE.md note.** In `CLAUDE.md`, the **"Still TBD:"** line lists "camera world→screen matrix (for world-space nameplates)". The matrix is validated + in production (HP bars, item labels, now affix nameplates). Replace that clause: note the camera matrix is **resolved** (`InGameState +0x368` → Camera `+0x1A0`, 64-byte row-major Matrix4x4, used by `OverlayRenderer.DrawNameplates`/`DrawAffixNameplates`), and remove it from "Still TBD".
- [ ] **Step 3: Bump version.** `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj`: `<Version>0.12.0</Version>`.
- [ ] **Step 4: CHANGELOG + README.** Add a `## [0.12.0]` section describing the opt-in affix nameplates (curated names + danger tiers, tier/override filter, display-all, default off) and the folded-in RuneStation offset fix. Add a short README feature bullet. **Badge stays `0.5.4`.**
- [ ] **Step 5: Live validation (user, on the live client).** Enable the card; confirm Task 5 + Task 7 live checks: tiered affix text draws above rares/uniques, tracks moving mobs, tier threshold + overrides + display-all behave, nothing draws when off/unfocused, no flicker. Cross-check curated ids against `config/known_mods.json` and extend `poe2_monster_mod_names.json` with any high-value observed ids that prettify poorly.
- [ ] **Step 6: Commit + release.**
```bash
git add -A
git commit -m "chore(release): v0.12.0 — affix nameplates (opt-in) + RuneStation offset fold-in"
```
Then the standard flow: merge to `main`, handle the stale Sikaka `v0.12.0` local-tag collision (`git tag -d` then recreate at HEAD), push `main` + tag (CI builds + releases), and draft the Discord post (+ Ko-fi).

---

## Self-review

**Spec coverage:** Curated masterlist + prettify → Task 1 (`poe2_monster_mod_names.json` + `Resolve`). Tier threshold + per-affix overrides + display-all → Task 1 (`Select`/`AffixFilter`) + Task 2 (settings) + Task 6/7 (API/UI). Default OFF → Task 2 (`Enabled=false`) + Task 7 (collapsed card). Elite = Rare+Unique, Magic optional → Task 3 (rarity gate). World-space text above head → Tasks 3–5 (spec path + camera projection). Dashboard editor browsing curated+observed → Task 6 (`/api/affix-catalog`) + Task 7. Doc fix (camera not TBD) → Task 8. All spec sections covered.

**Placeholder scan:** No TBD/TODO. Every code step has real code. The "validate curated ids against known_mods.json" steps are deliberate validation actions (auto-prettify is the safety net), not placeholders. The two "confirm against the real `HpBarSpec`/`ValidHexOr`/hex-helper" notes are concrete verification steps with the fallback named.

**Type consistency:** `AffixTier`/`AffixInfo`/`AffixLine`/`AffixFilter` defined in Task 1, used identically in Tasks 3/5/6. `AffixNameplateSpec(nint Render, AffixLine[] Lines)` defined in Task 3, consumed in Tasks 4/5. `AffixNameplateSettings` field names (`Enabled`/`Tier`/`AlwaysShow`/`Hide`/`DisplayAll`/`ShowOnRare`/`ShowOnUnique`/`ShowOnMagic`/`MaxLines`/`OffsetY`/`DeadlyColor`/`NotableColor`/`MinorColor`) consistent across Tasks 2/3/6/7. `MonsterAffixCatalog.Shared.Select/Resolve/Curated` consistent. `WorldSnapshot.AffixSpecs` and `RenderContext.AffixSpecs`/`AffixNameplates` positions consistent across Tasks 3/4/5.
