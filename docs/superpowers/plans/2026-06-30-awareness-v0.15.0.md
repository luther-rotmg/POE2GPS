# Situational Awareness (v0.15.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Ship two low-risk awareness features on data the overlay already reads — off-screen entity arrows (edge arrows to notable off-screen entities) + Session HUD v2 (observed kills by rarity, maps/hr, XP-efficiency).

**Architecture:** New pure Core units (`KillTracker`, `EdgeArrow`) + extended `SessionTracker`/`SessionStats`; the off-screen arrows mirror the existing HP-bar frame pattern (world-thread spec → `WorldSnapshot` → render-thread projection) and reuse a generalized `DrawEdgeArrow`. Zero new offsets.

**Tech Stack:** C#/.NET 10 (x64), Vortice.Direct2D1, xUnit (Core-only tests), vanilla-JS dashboard.

## Global Constraints

- **Strictly read-only** — uses existing RPM reads only; NO new memory reads, NO `WriteProcessMemory`/`SendInput`/etc., NO pricing/reward-values. `compliance-gate.ps1` + scrub stay green.
- `TreatWarningsAsErrors=true` → 0/0. README badge stays `0.5.4`. Version → `0.15.0`.
- **Zero new offsets** (`Poe2Offsets.cs` unchanged).
- **Validated seams (from grounding):** `EntityDot(uint Id, nint Address, Vector2 Grid, Vector3 World, EntityCategory Category, string Metadata, int HpCur, int HpMax, bool Poi, byte Reaction, Rarity Rarity, bool Opened, …)` with `IsAlive => HpMax<=0 || HpCur>0`, `IsFriendly => (Reaction&0x7F)==1`, `HpFraction`. `Rarity`: Normal=0/Magic=1/Rare=2/Unique=3/NonMonster=-1. `DisplayRules.Resolve(EntityDot) → DisplayRule?`. Camera: `Poe2Live.CameraMatrix(nint inGameState) → float[16]?`; project `cw=w.X*m[3]+w.Y*m[7]+w.Z*m[11]+m[15]; cx=…m[0/4/8/12]; cy=…m[1/5/9/13]; sx=(cx/cw/2+0.5)*W; sy=(0.5-cy/cw/2)*H`; off-screen ⇔ `cw<=0 || sx<0 || sx>W || sy<0 || sy>H`.

### Build & test
- Core: `dotnet build src/POE2Radar.Core/POE2Radar.Core.csproj -c Release` → 0/0.
- Overlay: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release` → 0/0.
- Tests: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Release -p:Platform=x64` → all pass (current baseline 244).
- **Note:** the overlay may be running locally (file lock on `Overlay.dll`) — that's a copy-lock, not a code error; CI builds clean.

---

## File structure

| File | Action | Task |
|---|---|---|
| `src/POE2Radar.Core/Session/KillTracker.cs` | new (pure) | 1 |
| `tests/POE2Radar.Tests/KillTrackerTests.cs` | new | 1 |
| `src/POE2Radar.Core/Session/SessionTracker.cs` | extend (kills/maps/xp) | 2 |
| `tests/POE2Radar.Tests/SessionTrackerV2Tests.cs` | new | 2 |
| `src/POE2Radar.Core/Game/EdgeArrow.cs` | new (pure) | 3 |
| `tests/POE2Radar.Tests/EdgeArrowTests.cs` | new | 3 |
| `src/POE2Radar.Overlay/Config/RadarSettings.cs` | ShowKills + EntityArrows + DisplayRule.OffScreenArrow | 4,5 |
| `src/POE2Radar.Overlay/RadarApp.cs` | feed trackers + arrow frame + publish | 4,5 |
| `src/POE2Radar.Overlay/Overlay/RenderContext.cs` | session fields + EntityArrows field | 4,5 |
| `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` | DrawSessionHud lines; DrawEdgeArrow + DrawEntityArrows | 4,6 |
| `src/POE2Radar.Overlay/Web/DisplayRules.cs` | DisplayRule.OffScreenArrow | 5 |
| `src/POE2Radar.Overlay/Web/ApiServer.cs` | settings round-trip (both) | 4,5 |
| `src/POE2Radar.Overlay/Web/DashboardHtml.cs` | HUD toggle + per-rule arrow checkbox + EntityArrows card | 4,5 |
| `CHANGELOG.md`, `README.md`, `*.csproj` | release | 7 |

---

## Task 1: Core `KillTracker` (pure, observed HP→0 kills)

**Files:** create `src/POE2Radar.Core/Session/KillTracker.cs`, `tests/POE2Radar.Tests/KillTrackerTests.cs`.

**Produces:** `KillTracker` with `Observe(nint address, Rarity rarity, int hpCur, int hpMax)`, `void ClearZone()`, `void Reset()`, and `(int normal,int magic,int rare,int unique) Counts`. Consumed by Task 2/4.

- [ ] **Step 1: Failing tests.** `tests/POE2Radar.Tests/KillTrackerTests.cs`:
```csharp
using POE2Radar.Core.Game;      // Rarity
using POE2Radar.Core.Session;

public class KillTrackerTests
{
    [Fact] public void Hp_to_zero_counts_one_kill_by_rarity()
    {
        var t = new KillTracker();
        t.Observe(0x100, Rarity.Rare, 50, 100);   // alive
        t.Observe(0x100, Rarity.Rare, 0, 100);    // died
        Assert.Equal((0,0,1,0), t.Counts);
    }
    [Fact] public void Death_counts_only_once_even_if_observed_dead_repeatedly()
    {
        var t = new KillTracker();
        t.Observe(0x1, Rarity.Unique, 10, 10);
        t.Observe(0x1, Rarity.Unique, 0, 10);
        t.Observe(0x1, Rarity.Unique, 0, 10);     // still dead, re-observed
        Assert.Equal((0,0,0,1), t.Counts);
    }
    [Fact] public void Eviction_does_not_count_as_a_kill()
    {
        var t = new KillTracker();
        t.Observe(0x2, Rarity.Magic, 30, 30);     // alive, then never seen again
        t.ClearZone();                             // zone change drops it, uncounted
        t.Observe(0x2, Rarity.Magic, 0, 30);      // a NEW entity reusing addr, first-seen dead → not a kill
        Assert.Equal((0,0,0,0), t.Counts);
    }
    [Fact] public void Non_monster_and_zero_max_ignored()
    {
        var t = new KillTracker();
        t.Observe(0x3, Rarity.NonMonster, 0, 0);
        Assert.Equal((0,0,0,0), t.Counts);
    }
    [Fact] public void Reset_clears_counts_and_tracking()
    {
        var t = new KillTracker();
        t.Observe(0x4, Rarity.Rare, 5, 5); t.Observe(0x4, Rarity.Rare, 0, 5);
        t.Reset();
        Assert.Equal((0,0,0,0), t.Counts);
    }
}
```
- [ ] **Step 2: Run red** — `dotnet test … -p:Platform=x64` → fails (type missing).
- [ ] **Step 3: Implement.** `src/POE2Radar.Core/Session/KillTracker.cs`:
```csharp
using POE2Radar.Core.Game;   // Rarity
namespace POE2Radar.Core.Session;

/// <summary>Counts monster deaths we actually observe: an entity whose HP we saw >0 then ≤0.
/// Pure; fed per world tick from the entity list (HP already read for HP bars — no new reads).
/// Undercounts kills we never see alive (far off-screen / culled before HP hits 0) — by design,
/// the honest no-offset trade (counting "vanished" entities over-counts badly).</summary>
public sealed class KillTracker
{
    private readonly Dictionary<nint, bool> _sawAlive = new();   // address → we've seen it alive this life
    private int _n, _m, _r, _u;

    public (int normal, int magic, int rare, int unique) Counts => (_n, _m, _r, _u);

    /// <summary>Observe one monster this tick. Only HP→0 (after being seen alive) counts a kill.</summary>
    public void Observe(nint address, Rarity rarity, int hpCur, int hpMax)
    {
        if (hpMax <= 0 || rarity == Rarity.NonMonster) return;   // not a real monster with life
        if (hpCur > 0) { _sawAlive[address] = true; return; }    // alive → remember
        // hpCur <= 0: a death only counts if we saw this address alive (not a first-seen corpse / reused addr)
        if (_sawAlive.TryGetValue(address, out var alive) && alive)
        {
            _sawAlive[address] = false;   // counted; don't recount while it lingers dead
            switch (rarity)
            {
                case Rarity.Normal: _n++; break;
                case Rarity.Magic:  _m++; break;
                case Rarity.Rare:   _r++; break;
                case Rarity.Unique: _u++; break;
            }
        }
    }

    /// <summary>Drop per-entity tracking on zone change (addresses are reused across zones). Keeps totals.</summary>
    public void ClearZone() => _sawAlive.Clear();

    /// <summary>Full reset (Ctrl+Alt+R): zero the counts + tracking.</summary>
    public void Reset() { _sawAlive.Clear(); _n = _m = _r = _u = 0; }
}
```
- [ ] **Step 4: Run green.** build Core 0/0; `dotnet test` pass (5 new).
- [ ] **Step 5: Commit** — `feat(core): KillTracker — observed HP→0 kills by rarity`.

---

## Task 2: Extend `SessionTracker`/`SessionStats` (kills + maps/hr + xp-eff)

**Files:** modify `src/POE2Radar.Core/Session/SessionTracker.cs`; create `tests/POE2Radar.Tests/SessionTrackerV2Tests.cs`.

**Consumes:** `KillTracker` (Task 1). **Produces:** `SessionStats` gains `KillsNormal/Magic/Rare/Unique, MapsPerHour, XpEfficiency`; `SessionTracker.Update(...)` gains a `playerLevel` param + owns a `KillTracker`; new `ObserveKill(nint,Rarity,int,int)` + `MapsEntered`.

- [ ] **Step 1: Failing tests.** `tests/POE2Radar.Tests/SessionTrackerV2Tests.cs`:
```csharp
using POE2Radar.Core.Game;
using POE2Radar.Core.Session;

public class SessionTrackerV2Tests
{
    const long Hour = TimeSpan.TicksPerHour;
    [Fact] public void Xp_efficiency_is_player_minus_area_level()
    {
        var t = new SessionTracker();
        var s = t.Update(areaHash: 1, "map", areaLevel: 70, playerLevel: 74, hpPct: 100, nowTicks: 0, excludeTowns: true, isTown: false);
        Assert.Equal(4, s.XpEfficiency);
    }
    [Fact] public void Maps_per_hour_counts_only_non_town_zone_entries()
    {
        var t = new SessionTracker();
        t.Update(1, "town",  60, 70, 100, 0,      true, isTown: true);   // town: not a map
        t.Update(2, "mapA",  70, 70, 100, 0,      true, isTown: false);  // map entry 1
        var s = t.Update(3, "mapB", 70, 70, 100, Hour/2, true, isTown: false); // map entry 2 @ 0.5h
        Assert.Equal(2, s.MapZonesEntered);
        Assert.True(s.MapsPerHour > 3.9f && s.MapsPerHour < 4.1f);       // 2 / 0.5h ≈ 4
    }
    [Fact] public void Kills_flow_through_to_stats()
    {
        var t = new SessionTracker();
        t.ObserveKill(0x10, Rarity.Rare, 5, 5); t.ObserveKill(0x10, Rarity.Rare, 0, 5);
        var s = t.Update(1, "map", 70, 74, 100, 0, true, false);
        Assert.Equal(1, s.KillsRare);
    }
    [Fact] public void Reset_zeroes_kills_and_maps()
    {
        var t = new SessionTracker();
        t.ObserveKill(0x1, Rarity.Unique, 5, 5); t.ObserveKill(0x1, Rarity.Unique, 0, 5);
        t.Update(2, "mapA", 70, 74, 100, 0, true, false);
        t.Reset(0);
        var s = t.Update(3, "mapB", 70, 74, 100, 0, true, false);
        Assert.Equal(0, s.KillsUnique);
        Assert.Equal(1, s.MapZonesEntered);   // the post-reset entry
    }
}
```
- [ ] **Step 2: Run red.**
- [ ] **Step 3: Implement.** In `SessionTracker.cs`: add fields `private readonly KillTracker _kills = new(); private int _mapZonesEntered; private int _xpEfficiency;`. Add `public void ObserveKill(nint a, Rarity r, int cur, int max) => _kills.Observe(a, r, cur, max);`. Change `Update(...)` signature to add `int playerLevel` (after `areaLevel`). Inside `Update`, on a NEW-zone transition (the existing `_zonesEntered++` path): if `!isTown` (a map) increment `_mapZonesEntered`; and call `_kills.ClearZone()` (addresses reused per zone). Compute `_xpEfficiency = playerLevel - areaLevel` each Update. In `Snapshot`: compute `mapsPerHour = sessionHours < (1.0/60.0) ? 0f : (float)(_mapZonesEntered / sessionHours)`; read `var (kn,km,kr,ku) = _kills.Counts;` and add all new fields to the `SessionStats` ctor. Extend `Reset` to `_kills.Reset(); _mapZonesEntered = 0;`. Add to the `SessionStats` record: `int KillsNormal, int KillsMagic, int KillsRare, int KillsUnique, float MapsPerHour, int MapZonesEntered, int XpEfficiency` (append; keep existing fields/order).
- [ ] **Step 4: Run green** (fix any existing `SessionStats` construction call sites — the RadarApp feed + any test — to pass the new `playerLevel` arg / read new fields). build Core 0/0; tests pass.
- [ ] **Step 5: Commit** — `feat(core): SessionTracker v2 — kills, maps/hr, xp-efficiency`.

---

## Task 3: Core `EdgeArrow` border helper (pure)

**Files:** create `src/POE2Radar.Core/Game/EdgeArrow.cs`, `tests/POE2Radar.Tests/EdgeArrowTests.cs`.

**Produces:** `EdgeArrow.BorderPoint(float sx, float sy, float cx, float cy, float w, float h, float margin) → (float ex, float ey, float ux, float uy)` — the pure math extracted from `DrawAtlasArrow`. Consumed by Task 6.

- [ ] **Step 1: Failing tests.** `tests/POE2Radar.Tests/EdgeArrowTests.cs`:
```csharp
using POE2Radar.Core.Game;

public class EdgeArrowTests
{
    // W=1000,H=800 → centre (500,400); margin 46 → inset half-extents (454, 354)
    [Fact] public void Point_far_right_lands_on_right_inset_edge()
    {
        var (ex, ey, ux, uy) = EdgeArrow.BorderPoint(5000, 400, 500, 400, 1000, 800, 46);
        Assert.True(System.Math.Abs(ex - (500 + 454)) < 0.5);   // right inset edge x
        Assert.True(System.Math.Abs(ey - 400) < 0.5);           // same height
        Assert.True(ux > 0.99 && System.Math.Abs(uy) < 0.01);   // unit dir points right
    }
    [Fact] public void Point_top_lands_on_top_inset_edge()
    {
        var (ex, ey, _, uy) = EdgeArrow.BorderPoint(500, -5000, 500, 400, 1000, 800, 46);
        Assert.True(System.Math.Abs(ey - (400 - 354)) < 0.5);   // top inset edge y
        Assert.True(uy < -0.99);                                 // points up
    }
    [Fact] public void Degenerate_same_point_returns_centre_zero_dir()
    {
        var (ex, ey, ux, uy) = EdgeArrow.BorderPoint(500, 400, 500, 400, 1000, 800, 46);
        Assert.Equal(500, ex, 3); Assert.Equal(400, ey, 3);
        Assert.Equal(0, ux, 3);  Assert.Equal(0, uy, 3);
    }
}
```
- [ ] **Step 2: Run red.**
- [ ] **Step 3: Implement.** `src/POE2Radar.Core/Game/EdgeArrow.cs` (math lifted verbatim from `OverlayRenderer.DrawAtlasArrow` lines 463-471):
```csharp
namespace POE2Radar.Core.Game;

/// <summary>Pure geometry for edge arrows: maps an (often off-screen) target point to the point on the
/// inset window border in its direction, plus the unit direction. Extracted from the atlas arrow so both
/// the atlas overlay and off-screen entity arrows share it (and it's unit-testable).</summary>
public static class EdgeArrow
{
    /// <returns>(ex, ey) border point + (ux, uy) unit direction from centre. Degenerate (target == centre)
    /// returns (cx, cy, 0, 0).</returns>
    public static (float ex, float ey, float ux, float uy) BorderPoint(
        float sx, float sy, float cx, float cy, float w, float h, float margin)
    {
        float dx = sx - cx, dy = sy - cy;
        float len = System.MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return (cx, cy, 0f, 0f);
        float ux = dx / len, uy = dy / len;
        float tX = System.MathF.Abs(ux) > 1e-4f ? (w * 0.5f - margin) / System.MathF.Abs(ux) : 1e9f;
        float tY = System.MathF.Abs(uy) > 1e-4f ? (h * 0.5f - margin) / System.MathF.Abs(uy) : 1e9f;
        float t = System.MathF.Min(tX, tY);
        return (cx + ux * t, cy + uy * t, ux, uy);
    }
}
```
- [ ] **Step 4: Run green.** build + test.
- [ ] **Step 5: Commit** — `feat(core): EdgeArrow border-point helper (shared arrow geometry)`.

---

## Task 4: Session HUD v2 wiring (feed trackers + render + settings)

**Files:** `RadarApp.cs`, `RadarSettings.cs`, `RenderContext.cs`, `OverlayRenderer.cs`, `ApiServer.cs`, `DashboardHtml.cs`.

- [ ] **Step 1: Settings.** In `RadarSettings.SessionHudSettings` add `public bool ShowKills { get; set; }` (default false).
- [ ] **Step 2: Feed the trackers in WorldTick.** In `RadarApp.WorldTick`, after `_entities` is built, feed kills: `foreach (var e in _entities) if (e.Category == Poe2Live.EntityCategory.Monster) _session.ObserveKill(e.Address, e.Rarity, e.HpCur, e.HpMax);` (KillTracker ignores non-life/non-monster internally). Pass the player level into the session `Update` call (grounding ~line 1267): add the player level arg — the player level is read as part of vitals; find the field the overlay already has (it feeds `_hpPct`; the player Player-component level is read near there — grounding notes `CurrentAreaLevel` comes from `snap.AreaLevel`; the PLAYER level is on the Player component. If a `_charLevel`/player-level field already exists on the world snapshot or RadarApp — the grounding shows `_charLevel` used in the WorldSnapshot ctor — pass `snap.CharLevel`/`_charLevel`). Use the existing character-level value (`_charLevel` / `snap.CharLevel`) for `playerLevel`.
- [ ] **Step 3: RenderContext already carries `SessionStats?` — no signature change** (the extended record flows through). Confirm `ctx.Session` now exposes the new fields.
- [ ] **Step 4: DrawSessionHud.** In `OverlayRenderer.DrawSessionHud`, after the `ShowDeaths` block, add (with matching cache-key comparands so the panel rebuilds when they change):
```csharp
if (hud.ShowKills)
{
    lines.Add(($"Kills    N{sess.KillsNormal} M{sess.KillsMagic} R{sess.KillsRare} U{sess.KillsUnique}", false));
    lines.Add(($"Maps/hr  {sess.MapsPerHour:F1}", false));
    lines.Add(($"XP eff   {sess.XpEfficiency:+#;-#;0}", false));
}
```
Add `_hudCacheKills{N,M,R,U}` + `_hudCacheMapsHr` + `_hudCacheXpEff` to the cache-key `if` (mirror the existing `_hudCache*` comparands).
- [ ] **Step 5: API + dashboard.** `ApiServer.ReadSettings()` add `sessionHudShowKills = _settings.SessionHud.ShowKills`; `ApplySettings` add `case "sessionHudShowKills" when TryBool(...)`. `DashboardHtml` Session HUD card: add a `data-set="sessionHudShowKills"` checkbox labelled "Kills / Maps-hr / XP-eff".
- [ ] **Step 6: Build + test** (0/0; 244+ pass). **Commit** — `feat(overlay): Session HUD v2 — kills/maps-hr/xp-eff lines + wiring`.

---

## Task 5: Off-screen arrows data (rule flag + frame + settings + seed)

**Files:** `DisplayRules.cs`, `RadarSettings.cs`, `RadarApp.cs`, `RenderContext.cs`, `ApiServer.cs`, `DashboardHtml.cs`.

**Produces:** `EntityArrowTarget(Vector3 World, uint Color, string? Label)` in `RenderContext`; consumed by Task 6.

- [ ] **Step 1: Rule flag.** In `Web/DisplayRules.cs` `DisplayRule`, add `public bool OffScreenArrow { get; set; }` (before `Navigable`).
- [ ] **Step 2: Settings.** In `RadarSettings.cs` add `public EntityArrowSettings EntityArrows { get; set; } = new();` + `public bool EntityArrowsSeeded { get; set; }` + the class:
```csharp
public sealed class EntityArrowSettings
{
    public bool Enabled { get; set; } = true;         // master (arrows only fire for rule-flagged, off-screen entities)
    public float Size { get; set; } = 11f;            // arrowhead size px
    public bool ShowLabel { get; set; } = true;       // draw the rule name by the arrow
    public int MaxArrows { get; set; } = 12;          // cap (nearest-first) to avoid edge clutter
    public int MinEdgeDistancePx { get; set; } = 24;  // skip targets whose projection is within this of the edge
}
```
- [ ] **Step 3: RenderContext.** In `RenderContext.cs` add `public readonly record struct EntityArrowTarget(System.Numerics.Vector3 World, uint Color, string? Label);` and ctx fields `IReadOnlyList<EntityArrowTarget>? EntityArrows = null, bool EntityArrowsEnabled = false, float EntityArrowSize = 11f, bool EntityArrowShowLabel = true, int EntityArrowMax = 12, int EntityArrowMinEdgePx = 24` (mirror the HP-bar ctx fields).
- [ ] **Step 4: World-thread spec + snapshot (mirror HP-bar frame).** In `RadarApp`: add `_entityArrowFrame` (zone-scoped? no — per-tick like HP). Follow the HP-bar pattern exactly: build an `EntityArrowSpec(nint Render, uint Color, string? Label)` list in the world tick for each entity whose `rule = _displayRules.Resolve(e)` is non-null, `!rule.Hide`, `rule.OffScreenArrow`, and `EntityArrows.Enabled` — capture the Render component addr (via the same `_live.TryBarComponents(e.Address, out render, out _)` used for HP specs; Render alone suffices for live world pos), color `PackColor(rule.Color)`, label `rule.OffScreenArrow ? (rule.Label ?? rule.Name) : null` (only when ShowLabel). Add `IReadOnlyList<EntityArrowSpec> EntityArrowSpecs` to `WorldSnapshot`. In `Tick()`, mirror the HP-bar loop: `foreach spec → _liveRender.TryLiveBarAt(spec.Render, 0, out var w, out _, out _) → _entityArrowFrame.Add(new EntityArrowTarget(w, spec.Color, spec.Label))`; pass `EntityArrows: worldFresh ? _entityArrowFrame : null` + the settings ctx fields.
- [ ] **Step 5: One-time seed.** In the display-rules seed path (where `DisplayRules.BuildDefault`/first-run seeding happens), gated by `!_settings.EntityArrowsSeeded`: set `OffScreenArrow = true` on the default rules whose Rarity == "Unique" OR whose Name contains "Boss"/"Citadel" (case-insensitive); set `EntityArrowsSeeded = true`; `_settings.Save()`. (Additive — don't touch other rules.)
- [ ] **Step 6: API + dashboard.** `ApiServer.ReadSettings()`: add `entityArrows = _settings.EntityArrows`. `ApplySettings`: whole-object `case "entityArrows" when …Object:` (parse Enabled/Size/ShowLabel/MaxArrows/MinEdgeDistancePx with clamps) OR individual `entityArrowsEnabled`(TryBool)/`entityArrowSize`(TryFloat 4-40)/`entityArrowMax`(TryInt 1-40)/… cases — match the sibling idiom. The per-rule `OffScreenArrow` rides the existing display-rules POST (rules are saved as a whole array; confirm the rule serializer includes the new bool — System.Text.Json includes it automatically). `DashboardHtml`: add an **Entity Arrows** settings card (enable + size + show-label + max) and an "off-screen arrow" checkbox to the **rule editor** row (next to the shape/color controls).
- [ ] **Step 7: Build + test** (0/0; 244+). **Commit** — `feat(overlay): off-screen entity arrow data (rule flag + frame + settings + seed)`.

---

## Task 6: Off-screen arrows render (`DrawEdgeArrow` + `DrawEntityArrows`)

**Files:** `OverlayRenderer.cs`.

- [ ] **Step 1: Generalize `DrawAtlasArrow` → `DrawEdgeArrow`.** Refactor `OverlayRenderer.DrawAtlasArrow` (lines 461-490): rename to `DrawEdgeArrow(rt, sx, sy, cx, cy, W, H, col, label, float head = 11f, float margin = 46f)`, and replace the inline border math with `var (ex, ey, ux, uy) = EdgeArrow.BorderPoint(sx, sy, cx, cy, W, H, margin); if (ux == 0 && uy == 0) return;`. Keep the arrowhead + label drawing as-is (use `head` for the tip length). Update the atlas call site (`if (n.Arrow) DrawAtlasArrow(...)` ~line 321) to `DrawEdgeArrow(...)`.
- [ ] **Step 2: `DrawEntityArrows`.** Add:
```csharp
private void DrawEntityArrows(ID2D1RenderTarget rt, RenderContext ctx)
{
    if (!ctx.EntityArrowsEnabled || ctx.CameraMatrix is not { } m || ctx.EntityArrows is not { Count: > 0 } targets) return;
    float W = ctx.WindowWidth, H = ctx.WindowHeight, cx = W * 0.5f, cy = H * 0.5f;
    // Project + collect off-screen ones with distance, nearest-first, capped.
    var offs = new List<(float sx, float sy, float d, uint col, string? label)>();
    foreach (var t in targets)
    {
        var w = t.World;
        var cw = w.X*m[3] + w.Y*m[7] + w.Z*m[11] + m[15];
        if (cw <= 0.0001f) continue;   // behind camera — treat as not shown
        var px = w.X*m[0] + w.Y*m[4] + w.Z*m[8] + m[12];
        var py = w.X*m[1] + w.Y*m[5] + w.Z*m[9] + m[13];
        float sx = (px/cw/2f + 0.5f) * W, sy = (0.5f - py/cw/2f) * H;
        bool onScreen = sx >= 0 && sx <= W && sy >= 0 && sy <= H;
        if (onScreen) continue;        // already a visible dot
        float d = MathF.Sqrt((sx-cx)*(sx-cx) + (sy-cy)*(sy-cy));
        offs.Add((sx, sy, d, t.Color, ctx.EntityArrowShowLabel ? t.Label : null));
    }
    offs.Sort((a, b) => a.d.CompareTo(b.d));
    int drawn = 0;
    foreach (var o in offs)
    {
        if (drawn >= ctx.EntityArrowMax) break;
        DrawEdgeArrow(rt, o.sx, o.sy, cx, cy, W, H, ColorFromPacked(o.col), o.label, ctx.EntityArrowSize);
        drawn++;
    }
}
```
Use the existing packed-uint→`Color4` helper (the one HP bars use for `Fill`/`Border`; grep `ColorFromPacked`/`Color4` around `DrawNameplates` and reuse it — if none, unpack inline `0xAARRGGBB`). Call `DrawEntityArrows(rt, ctx);` in `Render()` right after `DrawNameplates` (only when PoE2 focused, same gate as the other world-space draws).
- [ ] **Step 3: Build + test** (0/0; 244+). **Commit** — `feat(overlay): DrawEntityArrows + generalized DrawEdgeArrow`.

**Live check (Task 7):** off-screen uniques/bosses show edge arrows pointing toward them (colour = rule); on-screen ones don't; cap + nearest-first hold in dense packs.

---

## Task 7: Integration sweep + release

**Files:** `*.csproj` (version), `CHANGELOG.md`, `README.md`.

- [ ] **Step 1:** Full Core + Overlay build (0/0; note the local overlay-running lock is a copy-lock not a code error — confirm via `dotnet test` which builds test refs), `dotnet test` (all pass), `compliance-gate.ps1` + `scrub -SelfTest` (green). Confirm branch diff has NO input/process-write/pricing symbols and `Poe2Offsets.cs` unchanged.
- [ ] **Step 2:** Version → `0.15.0`.
- [ ] **Step 3:** CHANGELOG `## [0.15.0]` (ALL-OUT themed — bold/emoji/symmetry) covering off-screen entity arrows + Session HUD v2 (kills-observed / maps-hr / xp-eff); README feature bullets. Badge stays `0.5.4`.
- [ ] **Step 4:** Final whole-branch review (Sonnet). Then live smoke (arrows + HUD v2 in a real map), commit `chore(release): v0.15.0 - Situational Awareness`, merge to `main` (handle stale tag), tag `v0.15.0`, push (CI release), themed Discord post, memory update.

---

## Self-review

**Spec coverage:** off-screen arrows (rule flag T5 + frame T5 + render T6 + EdgeArrow T3) ✓; alert-rules = DisplayRule extension (T5) ✓; Session HUD v2 kills (KillTracker T1 + wire T4), maps/hr + xp-eff (SessionTracker T2 + render T4) ✓; settings/API/dashboard (T4/T5) ✓; seed on Unique+Boss (T5) ✓; reset via Ctrl+Alt+R (T2 Reset extended) ✓; release T7. All spec sections covered.

**Placeholder scan:** No TBD/TODO. Novel Core units (KillTracker, EdgeArrow, SessionTracker extension) carry full code + test cases. Overlay wiring names the exact HP-bar sibling to mirror (frame → WorldSnapshot → Tick → RenderContext → renderer) with grounding line anchors, not vague prose. One flagged unknown (T4 Step 2): the exact player-level field name — resolved by "use the existing `_charLevel`/`snap.CharLevel` value the WorldSnapshot already carries" (grounding confirms `_charLevel` feeds the WorldSnapshot ctor).

**Type consistency:** `KillTracker.Observe(nint,Rarity,int,int)` + `.Counts (int,int,int,int)` + `ClearZone()`/`Reset()` (T1) → used T2/T4. `SessionStats` new fields `Kills{Normal,Magic,Rare,Unique}/MapsPerHour/MapZonesEntered/XpEfficiency` (T2) → read T4 DrawSessionHud. `SessionTracker.Update(...,int playerLevel,...)` new param (T2) → passed T4. `EdgeArrow.BorderPoint(...) → (ex,ey,ux,uy)` (T3) → used T6 DrawEdgeArrow. `EntityArrowTarget(Vector3,uint,string?)` + `EntityArrowSpec(nint,uint,string?)` (T5) → consumed T6. `DisplayRule.OffScreenArrow` (T5) → read T5 world loop. `EntityArrowSettings` fields (T5) → ctx (T5) → renderer (T6). Consistent throughout.
