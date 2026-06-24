# Session HUD — Design Spec

**Goal:** Add a lightweight, read-only session tracker that displays pace, zone context, and deaths in a compact overlay HUD and a live dashboard panel. All stat groups default to off.

---

## Why / Scope

POE2GPS is a read-only GPS overlay. The Session HUD adds zero writes, zero injections, and zero emitted input. Every stat is derived from values the render/world loops already read (area hash, HP, area code, area level). No new memory reads are introduced. The entire feature is off by default; enabling any part requires deliberate user action in the dashboard Settings tab.

---

## Architecture

### Files to create

**`src/POE2Radar.Core/Session/SessionTracker.cs`**
Pure, dependency-free class in `POE2Radar.Core`. Contains all counter logic and emits immutable `SessionStats` snapshots. No memory reads, no rendering, no I/O. Lives in Core because `POE2Radar.Tests` references only `POE2Radar.Core` (verified: `tests/POE2Radar.Tests/POE2Radar.Tests.csproj` line 23).

**`src/POE2Radar.Core/Session/SessionStats.cs`**
Immutable record carrying all snapshot fields consumed by the renderer and the API.

**`tests/POE2Radar.Tests/SessionTrackerTests.cs`**
xUnit test class (no namespace, `[Fact]` only, `Assert.*`, concrete types). See Testing section.

### Files to modify

**`src/POE2Radar.Overlay/Config/RadarSettings.cs`** (after line 189)
Add `public SessionHudSettings SessionHud { get; set; } = new();` property and the `SessionHudSettings` sealed class in the same file, following the pattern of `MonolithSettings` at line 396.

**`src/POE2Radar.Overlay/RadarApp.cs`**
- Declare `private readonly SessionTracker _session = new();` and `private volatile SessionStats? _sessionSnapshot;`.
- Feed `SessionTracker` once per `Tick()` from a **single consistent area source** (see "Consistent area source" below) — gated on the existing `worldFresh` guard (RadarApp.cs line 880) so the live area hash and the snapshot's area code/level always describe the same zone.
- Pass `Session: _sessionSnapshot` to the `new RadarState(...)` constructor call at line 893.
- Add Ctrl+Alt+R reset block inside `HandleHotkeys()` (line 1296 region).
- Add the two new `RenderContext` fields (`Session`, `SessionHudSettings`) to the `RenderContext` constructor call inside `Tick()`.

**`src/POE2Radar.Overlay/Overlay/RenderContext.cs`**
Add two fields to the `RenderContext` record (see "RenderContext additions" under Display). RenderContext mirrors individual sub-sections as discrete fields (e.g. `Styles`, `HpBars`, `TerrainStyle`, `NavMenuCorner`); it has **no whole-`RadarSettings` member**. The new fields follow the same discrete-field convention.

**`src/POE2Radar.Overlay/Web/ApiServer.cs`**
- Append `SessionStats? Session = null` to the `RadarState` record at line 1206 (before the closing paren). This is a new optional parameter with a default so no existing call site changes.
- In `ReadSettings()` at line 617: add the **eight** `SessionHud` leaf fields to the anonymous object (Enabled, ShowPace, ShowZoneContext, ShowDeaths, Anchor, OffsetX, OffsetY, ExcludeTownsFromPace).
- In `ApplySettings()` at line 652: add **eight** `case` entries switching on `"sessionHudEnabled"`, `"sessionHudShowPace"`, `"sessionHudShowZoneContext"`, `"sessionHudShowDeaths"`, `"sessionHudAnchor"`, `"sessionHudOffsetX"`, `"sessionHudOffsetY"`, `"sessionHudExcludeTowns"` — each mutates `_settings.SessionHud.*` and the final `_settings.Save()` at line 708 persists them. Use the flat-key pattern (like `showMonolithPanel` mapping to `_settings.Monoliths.ShowPanel`) not the whole-object TryParse pattern, as the section is small.
- In the `case "/state":` handler at line 166: add a `session` projection after the `director` projection at line 194.

**`src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs`**
Add `DrawSessionHud(ID2D1RenderTarget rt, RenderContext ctx)` private method. Insert `DrawSessionHud(rt, ctx);` inside the trailing `if (ctx.Active && ctx.InGame)` block at line 131 (after `DrawMonolithPanel`).

**`src/POE2Radar.Overlay/Web/DashboardHtml.cs`**
- Settings tab: add Session HUD card with boolean toggles and anchor/offset controls (see Display section).
- `renderState()` function (called by `tick()` at line 736): add a Session panel reading `state.session`.

---

## SessionTracker Contract

### Location

`POE2Radar.Core.Session.SessionTracker` — no constructor arguments, no dependencies.

### Consistent area source

`Update` must receive `areaHash`, `areaCode`, and `areaLevel` that all describe the **same zone**. The render loop already computes a live hash each frame (`_areaHash = _liveRender.AreaHash(...)`, line 812) but carries `AreaCode`/`AreaLevel` from the lagging `WorldSnapshot` (published by the world tick), and it guards rendering of snapshot data on `worldFresh = inGame && snap.InGame && snap.AreaHash == _areaHash` (line 880). Because the snapshot can lag by up to one world pass, a live hash paired with a stale code/level would let a zone change be counted against the OLD zone's code/level/isTown for the transition frame.

**Rule:** Only call `SessionTracker.Update` when `worldFresh` is true, and pass `snap.AreaHash`/`snap.AreaCode`/`snap.AreaLevel` (all from the same snapshot) rather than the live `_areaHash`. When `worldFresh` is false (the snapshot has not yet caught up to the live zone), skip the `Update` call for that frame and reuse the last published `_sessionSnapshot` in `RadarState`. This guarantees all four area-derived values are internally consistent. `hpPct` continues to come from the render-frame `_hpPct` (it is per-zone-guarded separately by the death-flash logic, so a one-pass lag there is harmless).

### Update signature

```csharp
public SessionStats Update(
    uint    areaHash,       // snap.AreaHash (world snapshot — same source as areaCode/areaLevel)
    string  areaCode,       // snap.AreaCode
    int     areaLevel,      // snap.AreaLevel
    float   hpPct,          // _hpPct — the 0..100 value from Poe2Live.Vitals.HpPct (see HP units below)
    long    nowTicks,       // DateTime.UtcNow.Ticks, passed in so logic is deterministic in tests
    bool    excludeTowns,   // from _settings.SessionHud.ExcludeTownsFromPace
    bool    isTown          // resolved by caller via ZoneGuide — see "Town identification" below
)
```

`Update` is called once per render frame from `Tick()` **only when `worldFresh` is true**. It returns the current `SessionStats` snapshot after applying any state transitions. The caller stores the return value in `_sessionSnapshot` (the volatile field), which is then included in `RadarState`.

#### HP units (read carefully)

`hpPct` is the value returned by `Poe2Live.Vitals.HpPct`, which is a **percentage in `[0, 100]`** — `HpPct => HpUnreserved > 0 ? 100f * HpCur / HpUnreserved : 100f` (Poe2Live.cs line 217). A fully-alive player reads ~100, **not 1**. Two consequences the implementer and tests must honor:

- Death detection compares against an **exact `0f`** (`hpPct == 0f`); this is valid at the zero boundary regardless of the upper bound being 100.
- On a read failure where `HpUnreserved == 0`, `HpPct` returns the `100f` fallback — i.e. a failed/unknown vitals read reads as **alive, not dead**. This is the safe direction (it never fabricates a death) and is relied upon by the death-flash guard.

All death tests in this spec pass `hpPct: 100f` for "alive" (not `1f`).

#### Town identification (v1 rule — decided)

The caller resolves `isTown` via the authoritative `ZoneGuide` table only:

```csharp
bool isTown = ZoneGuide.Shared.Area(areaCode)?.Town ?? false;
```

**Hideouts are NOT treated as towns in v1.** The verified findings confirm hideouts are not flagged in `ZoneGuide.Town`, and there is no documented hideout area-code string to match on — a `areaCode.Contains("Hideout")` fallback would be an unvalidated guess. v1 therefore excludes only zones `ZoneGuide` marks as towns from pace; hideouts (if any are entered) count as normal zones. Adding hideout exclusion is explicitly out of scope (see Out of Scope) and would require either a new `Hideout` flag in `ZoneGuide` or a validated hideout code pattern.

### SessionStats record

```csharp
public sealed record SessionStats(
    TimeSpan SessionElapsed,      // wall time since tracker was constructed (app launch)
    TimeSpan ZoneElapsed,         // wall time since last zone entry
    int      ZonesEntered,        // count of qualifying zone entries (excludes towns if ExcludeTowns)
    float    ZonesPerHour,        // ZonesEntered / SessionElapsed.TotalHours, 0 when < 1 min elapsed
    string   CurrentZoneName,     // areaCode as-is (display formatting is the renderer's concern)
    int      CurrentAreaLevel,
    int      Deaths,              // lifetime deaths this session
    int      DeathsThisZone
);
```

### Internal state fields

```csharp
private long _sessionStartTicks;     // set on first Update and on Reset
private long _zoneStartTicks;
private uint _lastAreaHash;          // 0 = no zone seen yet
private bool _firstAreaSeen;         // false until the first Update establishes the initial zone
private int  _zonesEntered;
private int  _deaths;
private int  _deathsThisZone;
private bool _hpObservedAboveZero;   // defeats the zone-load HP=0 flash
private bool _awaitingRespawn;       // blocks a second death count until HP recovers
private string _currentZoneName = "";
private int  _currentAreaLevel;
```

`_sessionStartTicks` is captured on the very first `Update` (using that call's `nowTicks`) so `SessionElapsed` starts at 0 rather than at an arbitrary `DateTime.MinValue` origin.

### First-call / zone-change logic

On each call, capture `nowTicks` and set `_currentZoneName = areaCode; _currentAreaLevel = areaLevel`.

**First call** (`!_firstAreaSeen`): treat as initial zone entry — set `_firstAreaSeen = true`, `_lastAreaHash = areaHash`, `_sessionStartTicks = nowTicks`, `_zoneStartTicks = nowTicks`, `_hpObservedAboveZero = false`, `_awaitingRespawn = false`, `_deathsThisZone = 0`. **Do not** increment `_zonesEntered` (the player was already in this zone before the session started).

**Zone change** (`_firstAreaSeen && areaHash != _lastAreaHash`):
1. Set `_lastAreaHash = areaHash`.
2. Reset `_zoneStartTicks = nowTicks`.
3. Reset `_hpObservedAboveZero = false` (defeats zone-load death flash in the new zone).
4. Reset `_awaitingRespawn = false`.
5. Reset `_deathsThisZone = 0`.
6. If `!(excludeTowns && isTown)`: increment `_zonesEntered`. Otherwise skip the increment (town excluded from pace).

### Death-detection logic

Death detection runs after zone-change detection (so a zone transition resets the per-zone state first). HP is the `[0, 100]` value; "zero" is an exact `0f` comparison:

1. If `hpPct > 0f` and `!_hpObservedAboveZero`: set `_hpObservedAboveZero = true`. This is the first valid "alive" observation in the zone, defeating the load-screen 0-flash.
2. If `_hpObservedAboveZero && !_awaitingRespawn && hpPct == 0f`: increment `_deaths` and `_deathsThisZone`; set `_awaitingRespawn = true`.
3. If `_awaitingRespawn && hpPct > 0f`: clear `_awaitingRespawn = false`. This allows the next death to register.
4. No other transitions modify the death counters.

**Edge cases:**
- Zone-load HP=0 flash: defeated by the `_hpObservedAboveZero` guard. A death in zone N does not carry `_awaitingRespawn` into zone N+1; zone change resets both `_hpObservedAboveZero` and `_awaitingRespawn`.
- Rapid 0-to-0: `_awaitingRespawn` blocks a second count until HP is observed above 0 again.
- Failed vitals read: `HpPct` returns the `100f` fallback (> 0), so a failed read is treated as alive and never fabricates a death.

### Pace math

```
sessionHours = (nowTicks - _sessionStartTicks) / (double)TimeSpan.TicksPerHour
ZonesPerHour = sessionHours < (1.0/60.0) ? 0f : (float)(ZonesEntered / sessionHours)
```

`ZoneElapsed = TimeSpan.FromTicks(nowTicks - _zoneStartTicks)` — for the current zone only, regardless of whether it is a town.

`SessionElapsed = TimeSpan.FromTicks(nowTicks - _sessionStartTicks)`.

### Reset (Ctrl+Alt+R)

Public method `void Reset(long nowTicks)` reinitializes all counters to their construction-time state, using `nowTicks` as the new session start:

`_sessionStartTicks = nowTicks`, `_zoneStartTicks = nowTicks`, `_zonesEntered = 0`, `_deaths = 0`, `_deathsThisZone = 0`, `_hpObservedAboveZero = false`, `_awaitingRespawn = false`, `_firstAreaSeen = true`, `_lastAreaHash` left as the current value.

After Reset, `_firstAreaSeen` stays `true` and `_lastAreaHash` keeps the current zone's hash, so the **next** `Update` for the same zone is NOT treated as a new zone entry (no spurious increment).

---

## Config

### RadarSettings.SessionHudSettings

Add after the existing nested section properties in `src/POE2Radar.Overlay/Config/RadarSettings.cs` (line 176 region):

```csharp
public SessionHudSettings SessionHud { get; set; } = new();
```

Define the class in the same file, following the `MonolithSettings` sealed class pattern at line 396:

```csharp
public sealed class SessionHudSettings
{
    public bool   Enabled               { get; set; } = false;
    public bool   ShowPace              { get; set; } = false;
    public bool   ShowZoneContext       { get; set; } = false;
    public bool   ShowDeaths            { get; set; } = false;
    public string Anchor                { get; set; } = "TopLeft";
    // Legal values: "TopLeft", "TopRight", "BottomLeft", "BottomRight"
    // Mirrors NavMenuCorner (RadarSettings.cs line 55) — plain string, no C# enum.
    public int    OffsetX               { get; set; } = 0;
    public int    OffsetY               { get; set; } = 0;
    public bool   ExcludeTownsFromPace  { get; set; } = true;
}
```

Persistence is automatic: `RadarSettings.Save()` at line 295 serializes the whole object (camelCase, `WriteIndented=true`). On load, `RadarSettings.Load()` at the FilePath `config/radar_settings.json` (line 199) deserializes tolerantly; missing keys default-initialize to the values above.

### API leaf-key mapping (eight keys)

The flat-key API pattern (mirroring `showMonolithPanel` → `_settings.Monoliths.ShowPanel`) maps these POST body keys. All eight must be wired as cases in `ApplySettings()` and as fields in `ReadSettings()`:

| JSON key | C# property |
|---|---|
| `sessionHudEnabled` | `SessionHud.Enabled` |
| `sessionHudShowPace` | `SessionHud.ShowPace` |
| `sessionHudShowZoneContext` | `SessionHud.ShowZoneContext` |
| `sessionHudShowDeaths` | `SessionHud.ShowDeaths` |
| `sessionHudAnchor` | `SessionHud.Anchor` |
| `sessionHudOffsetX` | `SessionHud.OffsetX` |
| `sessionHudOffsetY` | `SessionHud.OffsetY` |
| `sessionHudExcludeTowns` | `SessionHud.ExcludeTownsFromPace` |

`_settings.Save()` is called once after any key was applied (existing pattern at line 708).

---

## Display

### On-screen overlay HUD

#### Draw method

Add `private void DrawSessionHud(ID2D1RenderTarget rt, RenderContext ctx)` in `OverlayRenderer.cs`.

Call it inside the trailing `if (ctx.Active && ctx.InGame)` block at line 131, after `DrawMonolithPanel(rt, ctx)`:

```csharp
if (ctx.Active && ctx.InGame)
{
    DrawRuneforge(rt, ctx);
    DrawRitualRewards(rt, ctx);
    DrawMonolithPanel(rt, ctx);
    DrawSessionHud(rt, ctx);   // new
}
```

#### Early-exit

`RenderContext` exposes the two new fields directly — there is **no `ctx.Settings` member**. Read `ctx.SessionHudSettings` and `ctx.Session`:

```csharp
var hud = ctx.SessionHudSettings;
if (!hud.Enabled) return;
var sess = ctx.Session;
if (sess == null) return;
```

Neither branch allocates strings or touches memory.

#### Lines to draw

Build only the enabled stat rows. Construct each line as a pre-formatted string. Compute the rendered **line count** (not group count) up front so the panel height is correct:

```
[If ShowPace]            → 3 lines
  "Session  HH:MM:SS"
  "Zone     HH:MM:SS"
  "Zones    {ZonesEntered}   {ZonesPerHour:F1}/hr"

[If ShowZoneContext]     → 2 lines
  "Area     {CurrentZoneName}"
  "Level    {CurrentAreaLevel}"

[If ShowDeaths]          → 1 line
  "Deaths   {Deaths} ({DeathsThisZone} here)"
```

`enabledRowCount` = the sum of rendered lines above (e.g. ShowPace+ShowDeaths = 4). If no stat group is enabled, `enabledRowCount == 0`; `DrawSessionHud` returns immediately after building zero lines without touching the render target.

#### Text draw idiom

Mirrors `DrawMonolithPanel` (line 519):

- `const float panelW = 240f;` (narrower than the 248f monolith panel).
- `const float pad = 6f, lineH = 15f;` (all rows are data rows — there is no title row).
- Panel height: `float panelH = enabledRowCount * lineH + pad * 2;`
- Background fill: `rt.FillRectangle(panelRect, _bPanel!)` using `ColPanel = new(0.05f, 0.05f, 0.05f, 0.78f)` (line 27).
- Each row: `rt.DrawText(line, _tf!, new Rect(x + pad, cy, x + panelW - pad, cy + lineH), _bText!, DrawTextOptions.Clip); cy += lineH;`
- `_tf` is the single `IDWriteTextFormat` (Consolas 12pt Normal) created at line 85 — use it as-is, do not create a new format.
- Label/value text uses `_bText` (white) by default. For an optional colored value, recolor the scratch `_bStyle` brush before drawing: `_bStyle!.Color = ...; rt.DrawText(..., _bStyle!, ...);`. v1 coloring is intentionally minimal: draw the death-count line in yellow (`new Color4(1f, 0.85f, 0.2f, 1f)`) when `Deaths > 0`, otherwise white; all other rows are white.

#### Corner anchoring

No anchoring helper exists. Replicate the inline arithmetic from `DrawNavMenu` at line 835, using `panelW` (declared above) and `hud.OffsetX/OffsetY` as signed deltas applied after the corner base, then clamp:

```csharp
var corner = hud.Anchor;
bool isRight  = corner is "TopRight"   or "BottomRight";
bool isBottom = corner is "BottomLeft" or "BottomRight";
const float margin = 10f;

float left = isRight
    ? ctx.WindowWidth  - margin - panelW + hud.OffsetX
    : margin + hud.OffsetX;
float top  = isBottom
    ? ctx.WindowHeight - margin - panelH + hud.OffsetY
    : margin + hud.OffsetY;
left = Math.Clamp(left, margin, ctx.WindowWidth  - margin - panelW);
top  = Math.Clamp(top,  margin, ctx.WindowHeight - margin - panelH);
```

`ctx.WindowWidth` and `ctx.WindowHeight` are the game window dimensions already carried in `RenderContext`.

#### RenderContext additions

`RenderContext` carries two new discrete fields (consistent with how it already mirrors `Styles`, `HpBars`, `TerrainStyle`, `NavMenuCorner` — there is no whole-`RadarSettings` member to reuse):

```csharp
SessionStats?       Session             // from _sessionSnapshot (volatile read in Tick)
SessionHudSettings  SessionHudSettings  // from _settings.SessionHud
```

Add both to the `RenderContext` record definition (RenderContext.cs) and pass them in the `RenderContext` constructor call inside `RadarApp.Tick()`. `DrawSessionHud` reads them as `ctx.Session` and `ctx.SessionHudSettings`.

### Dashboard: Settings tab

Add a "Session HUD" card to the Settings tab in `DashboardHtml.cs` (after line 563 region). Use the verified boolean toggle and number/select patterns:

```html
<div class="card">
  <div class="card-title">Session HUD</div>

  <div class="row"><div class="rl">Enable HUD<small>Show session stats overlay</small></div>
    <label class="sw"><input type="checkbox" data-set="sessionHudEnabled">
      <span class="track"></span><span class="knob"></span></label></div>

  <div class="row"><div class="rl">Pace stats<small>Clock / zones / rate</small></div>
    <label class="sw"><input type="checkbox" data-set="sessionHudShowPace">
      <span class="track"></span><span class="knob"></span></label></div>

  <div class="row"><div class="rl">Zone context<small>Area name + level</small></div>
    <label class="sw"><input type="checkbox" data-set="sessionHudShowZoneContext">
      <span class="track"></span><span class="knob"></span></label></div>

  <div class="row"><div class="rl">Deaths<small>Session + per-zone counter</small></div>
    <label class="sw"><input type="checkbox" data-set="sessionHudShowDeaths">
      <span class="track"></span><span class="knob"></span></label></div>

  <div class="row"><div class="rl">Exclude towns<small>Omit towns from pace</small></div>
    <label class="sw"><input type="checkbox" data-set="sessionHudExcludeTowns">
      <span class="track"></span><span class="knob"></span></label></div>

  <div class="row"><div class="rl">Anchor corner</div>
    <select class="numin" data-set="sessionHudAnchor">
      <option value="TopLeft">Top Left</option>
      <option value="TopRight">Top Right</option>
      <option value="BottomLeft">Bottom Left</option>
      <option value="BottomRight">Bottom Right</option>
    </select></div>

  <div class="row"><div class="rl">Offset X</div>
    <input class="numin" type="number" step="1" data-set="sessionHudOffsetX"></div>

  <div class="row"><div class="rl">Offset Y</div>
    <input class="numin" type="number" step="1" data-set="sessionHudOffsetY"></div>
</div>
```

`wireSettings()` at line 794 auto-wires all `[data-set]` elements. The `SELECT` branch at line 798 handles `sessionHudAnchor`. No JS changes to `wireSettings` or `loadSettings` are required — they operate generically on `[data-set]` attributes.

### Dashboard: live Session panel

Add a read-only Session panel to the status area. The existing `tick()` at line 736 polls `/state` every 1000ms via `setInterval(tick, 1000)` at line 1691 and stores the result in the module-level `state` variable. No new `setInterval` or `fetch` call is needed.

Inside `renderState()` (called at line 741 from `tick()`), read `state.session`:

```javascript
function renderSessionPanel() {
    const s = state && state.session;
    const el = document.getElementById('session-panel');
    if (!el) return;
    if (!s) { el.style.display = 'none'; return; }
    el.style.display = '';
    document.getElementById('sp-session').textContent = s.sessionElapsed || '—';
    document.getElementById('sp-zone').textContent    = s.zoneElapsed    || '—';
    document.getElementById('sp-zones').textContent   = s.zonesEntered != null
        ? `${s.zonesEntered}  (${(s.zonesPerHour||0).toFixed(1)}/hr)` : '—';
    document.getElementById('sp-area').textContent    = s.currentZoneName || '—';
    document.getElementById('sp-level').textContent   = s.currentAreaLevel ?? '—';
    document.getElementById('sp-deaths').textContent  = s.deaths != null
        ? `${s.deaths} (${s.deathsThisZone} here)` : '—';
}
```

Call `renderSessionPanel()` at the end of `renderState()`. The panel's HTML is a simple `<div id="session-panel">` card with labelled `<span>` elements.

### /state JSON addition

In `ApiServer.cs` at line 166, `case "/state":`, append after the `director` projection at line 194:

```csharp
session = s.Session == null ? (object?)null : new {
    sessionElapsed    = FormatTimeSpan(s.Session.SessionElapsed),
    zoneElapsed       = FormatTimeSpan(s.Session.ZoneElapsed),
    zonesEntered      = s.Session.ZonesEntered,
    zonesPerHour      = s.Session.ZonesPerHour,
    currentZoneName   = s.Session.CurrentZoneName,
    currentAreaLevel  = s.Session.CurrentAreaLevel,
    deaths            = s.Session.Deaths,
    deathsThisZone    = s.Session.DeathsThisZone,
},
```

`FormatTimeSpan` is a local helper formatting as `"HH:MM:SS"` — add it as a local static inside the `case "/state":` block or as a private method on `ApiServer`.

---

## Reset Hotkey (Ctrl+Alt+R)

The reset hotkey is a pure in-process counter clear. It introduces no input-emission API.

Add inside `HandleHotkeys()` at line 1296, following the Ctrl+Alt+M idiom (line 1355):

```csharp
// Ctrl+Alt+R — reset session counters
if (_settings.SessionHud.Enabled
    && DateTime.UtcNow >= _nextSessionResetAt
    && _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd
    && Down(0x11) && Down(0x12) && Down(0x52))  // VK_CONTROL=0x11, VK_MENU=0x12, VK_R=0x52
{
    _session.Reset(DateTime.UtcNow.Ticks);
    _nextSessionResetAt = DateTime.UtcNow.AddMilliseconds(500);
}
```

Declare `private DateTime _nextSessionResetAt = DateTime.MinValue;` as a field.

`Down()` is the read-only `GetAsyncKeyState` wrapper at line 2300. `GetForegroundWindow()` is the existing P/Invoke already used by all other hotkeys. No `SendInput`, `keybd_event`, `mouse_event`, `PostMessage`, `SendMessage`, or any other input-emission symbol is introduced. The compliance gate (line 34-38 of `compliance-gate.ps1`) will remain green.

---

## Performance

**Zero new memory reads.** Every value fed to `SessionTracker` on a `worldFresh` frame is already read:

| Value | Existing read | Where |
|---|---|---|
| `hpPct` | `_live.PlayerVitals()` result cached in `_hpPct` (called inside the render thread's `Tick()`) | RadarApp.cs line 818 |
| `areaHash` | `snap.AreaHash` from the world tick's `WorldSnapshot` | RadarApp.cs line 999 region |
| `areaCode` | `snap.AreaCode` from the same snapshot | RadarApp.cs line 999 region |
| `areaLevel` | `snap.AreaLevel` from the same snapshot | RadarApp.cs line 999 region |

(`hpPct` is read via `_live` — the world-thread reader stack — inside the render thread's `Tick()`; the cached `_hpPct/_manaPct/_esPct` floats are written and read on the render thread only, so there is no data race. The three area values all come from the single `WorldSnapshot`, guaranteeing internal consistency.)

**Counter updates are string-free.** `SessionTracker.Update()` performs only integer arithmetic, tick comparisons, and boolean state transitions. No `string.Format`, `StringBuilder`, or `DateTime.ToString` call occurs inside `Update`. String formatting happens only in `DrawSessionHud` and the `/state` serializer, and only when those paths are actually invoked.

**Draw path is gated.** `DrawSessionHud` returns immediately if `!hud.Enabled` or `ctx.Session == null`. When enabled, it draws only the rows whose toggle is on. The existing `if (ctx.Active && ctx.InGame)` guard (line 109/131) already ensures the method is not called when the overlay is hidden.

**Snapshot publication.** `_sessionSnapshot` is a `volatile SessionStats?` reference. `SessionStats` is an immutable record. The render thread reads the reference once per frame and includes it in the `RadarState` record (which is itself a volatile reference). No new locks, no new `Interlocked`, no contention.

---

## Testing

File: `tests/POE2Radar.Tests/SessionTrackerTests.cs`

Top-level class, no namespace. Uses only `POE2Radar.Core.Session`. `[Fact]` only. `Assert.*` only. No mocking. **HP values are `[0,100]` — `100f` = alive, `0f` = dead.**

```csharp
using POE2Radar.Core.Session;

public class SessionTrackerTests
{
    // Helper: ticks representing N seconds from a fixed origin
    private static long T(double seconds) =>
        (long)(seconds * TimeSpan.TicksPerSecond);

    // Helper: call Update with default/pass-through values for fields under test.
    // hpPct defaults to 100f (alive) — HpPct is a [0,100] percentage, not [0,1].
    private static SessionStats Step(SessionTracker t,
        uint areaHash = 1, string areaCode = "G1_1", int areaLevel = 1,
        float hpPct = 100f, long nowTicks = 0,
        bool excludeTowns = false, bool isTown = false)
        => t.Update(areaHash, areaCode, areaLevel, hpPct, nowTicks, excludeTowns, isTown);
```

### Zone counting

```csharp
    [Fact]
    public void FirstUpdate_DoesNotIncrementZones()
    {
        var t = new SessionTracker();
        var s = Step(t, areaHash: 1, nowTicks: T(0));
        Assert.Equal(0, s.ZonesEntered);
    }

    [Fact]
    public void ZoneChange_IncrementsZones()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        var s = Step(t, areaHash: 2, nowTicks: T(10));
        Assert.Equal(1, s.ZonesEntered);
    }

    [Fact]
    public void SameHash_DoesNotIncrementZones()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 1, nowTicks: T(5));
        var s = Step(t, areaHash: 1, nowTicks: T(10));
        Assert.Equal(0, s.ZonesEntered);
    }

    [Fact]
    public void TwoZoneChanges_CountsTwo()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 2, nowTicks: T(10));
        var s = Step(t, areaHash: 3, nowTicks: T(20));
        Assert.Equal(2, s.ZonesEntered);
    }
```

### Town exclusion

```csharp
    [Fact]
    public void TownEntry_ExcludeEnabled_DoesNotCount()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        var s = Step(t, areaHash: 2, areaCode: "G1_town", isTown: true,
                     excludeTowns: true, nowTicks: T(10));
        Assert.Equal(0, s.ZonesEntered);
    }

    [Fact]
    public void TownEntry_ExcludeDisabled_Counts()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        var s = Step(t, areaHash: 2, areaCode: "G1_town", isTown: true,
                     excludeTowns: false, nowTicks: T(10));
        Assert.Equal(1, s.ZonesEntered);
    }

    [Fact]
    public void NonTownAfterTown_ExcludeEnabled_Counts()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 2, areaCode: "G1_town", isTown: true,
             excludeTowns: true, nowTicks: T(10));
        var s = Step(t, areaHash: 3, areaCode: "G1_1", isTown: false,
                     excludeTowns: true, nowTicks: T(20));
        Assert.Equal(1, s.ZonesEntered);
    }
```

### Death edge cases

```csharp
    [Fact]
    public void DeathFlashOnLoad_IsIgnored()
    {
        // HP is 0 on first update (zone load), then recovers — must NOT count as death
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0));
        var s = Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1));
        Assert.Equal(0, s.Deaths);
    }

    [Fact]
    public void Death_AfterObservedAlive_Counts()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0)); // load flash — ignored
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1)); // alive observed
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(2)); // death
        var s = Step(t, areaHash: 1, hpPct: 0f, nowTicks: T(3)); // still dead
        Assert.Equal(1, s.Deaths);
    }

    [Fact]
    public void BackToBackDeaths_RequireRespawnBetween()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0));
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1));
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(2)); // death 1
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(3)); // still 0 — no second count
        var s = Step(t, areaHash: 1, hpPct: 0f, nowTicks: T(4));
        Assert.Equal(1, s.Deaths);
    }

    [Fact]
    public void TwoDeaths_AfterRespawnBetween_CountsTwo()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0));
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1));
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(2)); // death 1
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(3)); // respawn
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(4)); // death 2
        var s = Step(t, areaHash: 1, hpPct: 0f, nowTicks: T(5));
        Assert.Equal(2, s.Deaths);
    }

    [Fact]
    public void ZoneChange_ResetsPerZoneDeaths_AndDeathFlashGuard()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0));
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1));
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(2)); // death in zone 1
        Step(t, areaHash: 2, hpPct: 0f,   nowTicks: T(3)); // zone change; load-flash 0
        var s = Step(t, areaHash: 2, hpPct: 100f, nowTicks: T(4)); // alive
        Assert.Equal(1, s.Deaths);          // session total preserved
        Assert.Equal(0, s.DeathsThisZone);  // per-zone reset
    }

    [Fact]
    public void DeathFlashInNewZone_AfterZoneChange_IsIgnored()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(0));
        Step(t, areaHash: 2, hpPct: 0f,   nowTicks: T(1)); // zone change with HP=0 flash
        var s = Step(t, areaHash: 2, hpPct: 100f, nowTicks: T(2));
        Assert.Equal(0, s.Deaths);
    }
```

### Pace math

```csharp
    [Fact]
    public void ZonesPerHour_ZeroWhenUnderOneMinute()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 2, nowTicks: T(30));   // 30 seconds in
        var s = Step(t, areaHash: 2, nowTicks: T(59));
        Assert.Equal(0f, s.ZonesPerHour);
    }

    [Fact]
    public void ZonesPerHour_CorrectAfterOneMinute()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 2, nowTicks: T(60));
        var s = Step(t, areaHash: 2, nowTicks: T(60));
        // 1 zone entered at T=60 => 1 / (60/3600) = 60 zones/hr
        Assert.Equal(60f, s.ZonesPerHour, precision: 0);
    }

    [Fact]
    public void SessionElapsed_MatchesWallTime()
    {
        var t = new SessionTracker();
        Step(t, nowTicks: T(0));
        var s = Step(t, nowTicks: T(90));
        Assert.Equal(TimeSpan.FromSeconds(90), s.SessionElapsed);
    }

    [Fact]
    public void ZoneElapsed_ResetsOnZoneChange()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 1, nowTicks: T(50));
        Step(t, areaHash: 2, nowTicks: T(60)); // zone change at T=60
        var s = Step(t, areaHash: 2, nowTicks: T(70));
        Assert.Equal(TimeSpan.FromSeconds(10), s.ZoneElapsed);
    }
```

### Reset

```csharp
    [Fact]
    public void Reset_ClearsAllCounters()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0));
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1));
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(2)); // death
        Step(t, areaHash: 2, nowTicks: T(10));             // zone
        t.Reset(T(10));
        var s = Step(t, areaHash: 2, nowTicks: T(20));
        Assert.Equal(0, s.Deaths);
        Assert.Equal(0, s.ZonesEntered);
        Assert.Equal(TimeSpan.FromSeconds(10), s.SessionElapsed);
    }

    [Fact]
    public void Reset_NextAreaHash_DoesNotIncrementZones()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 2, nowTicks: T(10));
        t.Reset(T(10));
        // After reset, areaHash=2 is the "current" hash; same-hash Update must not increment.
        var s = Step(t, areaHash: 2, nowTicks: T(20));
        Assert.Equal(0, s.ZonesEntered);
    }
}
```

---

## Compliance

**Read-only invariant.** `SessionTracker`, `SessionStats`, `DrawSessionHud`, and all API additions perform zero writes to the PoE2 process. `WriteProcessMemory`, `VirtualAllocEx`, `VirtualProtectEx`, `CreateRemoteThread`, and all related symbols (compliance-gate.ps1 line 28-33) are not introduced anywhere in `src/` excluding Research.

**No input emission.** The Ctrl+Alt+R hotkey uses only `GetAsyncKeyState` (read-only polling, VK_R = 0x52) and `GetForegroundWindow` (read-only query), both already present in the codebase. `SendInput`, `keybd_event`, `mouse_event`, `PostMessage`, `SendMessage`, and all other forbidden input symbols (compliance-gate.ps1 line 34-38) are not introduced (verified against the full forbidden list; no symbol-name collision, so no allowlist entry is needed).

**No POE2Radar.Research or Poe2Offsets changes are part of this feature.**

**Gate scope (unchanged elsewhere).** Beyond the input/write symbol lists, the gate also blocks `PROCESS_VM_WRITE`/`PROCESS_VM_OPERATION` on `OpenProcess` and guards the gutted pricing layer (`class PriceBook`, `new PriceBook`, `"/api/prices"`, the `Pricing/` directory). None of those are touched by this feature, so the gate stays green and no `scripts/compliance-allowlist.txt` change is required.

---

## Out of Scope (v1)

- **XP/hour stat.** Deferred entirely to a separate follow-up change, which will land the `--xp` Research probe, the validated `Experience` offset, the read plumbing, and the toggle/UI together. None of that is in this feature.
- **Hideout exclusion from pace.** v1 excludes only `ZoneGuide`-flagged towns. Hideouts are not flagged in `ZoneGuide.Town` and have no validated code pattern, so they count as normal zones. Adding hideout exclusion would require a new `ZoneGuide` flag or a validated hideout code match.
- On-disk run history or CSV export of session data.
- Per-map vs. per-zone stat breakdown (e.g. "deaths in this map layout").
- Persistent stats across app restarts (session is in-memory only, cleared on exit).
- Kill counter or mob-clear rate (no entity death events are tracked).
- Multiple named sessions or session bookmarking.
- Estimated time-to-level (requires XP-to-next-level table, not yet present).
- Dashboard graph of rate stats over time.
- Alert / notification on death (sound, system tray).
- Integration with the Director feature's objective scoring.
- Dashboard control of the Anchor corner via in-game click actions (the dashboard `select` is the sole UI for `SessionHud.Anchor`; `NavMenuCorner`'s in-game click flow is not reused).
