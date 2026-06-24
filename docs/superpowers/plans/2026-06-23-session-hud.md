# Session HUD Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only, off-by-default Session HUD that tracks pace (session/zone clocks, zones-per-hour), zone context (area name + level), and deaths, derived entirely from values the existing render/world loops already read — drawn as a compact overlay panel and mirrored in a live dashboard panel.

**Architecture:** A pure `SessionTracker` class in `POE2Radar.Core` consumes per-frame area/HP values and emits an immutable `SessionStats` snapshot; the only task with an automated test harness. The Overlay project wires that snapshot through config (`SessionHudSettings`), the HTTP `/state` API, `RenderContext`, a `DrawSessionHud` Direct2D method, a `Ctrl+Alt+R` read-only reset hotkey, and a vanilla-JS dashboard card + live panel. Zero new memory reads, zero input emission, zero process writes.

**Tech Stack:** .NET 10 (net10.0-windows, x64), C#, xUnit, Vortice.Direct2D1, vanilla-JS dashboard embedded as a C# string.

## Global Constraints
- Platform: .NET 10, net10.0-windows, x64 only.
- Strictly READ-ONLY: introduce NO SendInput / PostMessage / keybd_event / mouse_event / SendMessage; NO WriteProcessMemory / VirtualProtectEx / VirtualAllocEx / CreateRemoteThread / injection; OpenProcess never requests write access. The compliance gate (scripts/compliance-gate.ps1) MUST stay green.
- Zero new memory reads: every value fed to SessionTracker is already read by the loops (snap.AreaHash / snap.AreaCode / snap.AreaLevel from the WorldSnapshot, and _hpPct on the render thread). No new ReadProcessMemory calls anywhere.
- SessionTracker is PURE and lives in POE2Radar.Core (no rendering, no I/O, no memory access) so the Core-only test project can cover it.
- All HUD VISIBILITY toggles default to false (Enabled / ShowPace / ShowZoneContext / ShowDeaths) — the HUD is off by default. Note: `ExcludeTownsFromPace` is a behavior-tuning flag, NOT a visibility toggle, and deliberately defaults to **true** (towns are excluded from pace by default). Do NOT "fix" it to false.
- Pace excludes only ZoneGuide-flagged towns: isTown = ZoneGuide.Shared.Area(areaCode)?.Town ?? false. Hideouts are NOT excluded (no validated detection).
- HP is the [0,100] HpPct percentage value (Poe2Live.Vitals.HpPct), NOT [0,1]. Death = exact `hpPct == 0f`. A failed vitals read returns the 100f fallback (alive) and must never fabricate a death.
- SessionTracker.Update is called once per render frame ONLY when worldFresh is true; nowTicks is passed IN (DateTime.UtcNow.Ticks) so the logic is deterministic in tests.
- NO XP / Experience / --xp / PlayerComponent.Experience work anywhere — XP/hour is deferred entirely to a separate follow-up (it appears only as an Out-of-Scope note).
- Reset hotkey Ctrl+Alt+R uses ONLY GetAsyncKeyState (read-only polling) + GetForegroundWindow; introduces no input-emission API.
- Verification commands: build = `dotnet build POE2Radar.slnx -c Release`; tests = `dotnet test POE2Radar.slnx`; gate = `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1`; scrub = `powershell -ExecutionPolicy Bypass -File scripts/scrub-strings.ps1 -SelfTest`.

---

### Task 1: Core — SessionStats + SessionTracker (+ unit tests)
**Files:**
- Create `src/POE2Radar.Core/Session/SessionStats.cs`
- Create `src/POE2Radar.Core/Session/SessionTracker.cs`
- Test `tests/POE2Radar.Tests/SessionTrackerTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces:
  - `POE2Radar.Core.Session.SessionStats` — `sealed record SessionStats(TimeSpan SessionElapsed, TimeSpan ZoneElapsed, int ZonesEntered, float ZonesPerHour, string CurrentZoneName, int CurrentAreaLevel, int Deaths, int DeathsThisZone)`.
  - `POE2Radar.Core.Session.SessionTracker` — `public SessionStats Update(uint areaHash, string areaCode, int areaLevel, float hpPct, long nowTicks, bool excludeTowns, bool isTown)` and `public void Reset(long nowTicks)`; parameterless constructor.

**Steps:**

- [ ] Write the failing test file. Create `tests/POE2Radar.Tests/SessionTrackerTests.cs` with this EXACT content (the full retained suite — no XP tests, no xp param):

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

- [ ] Run it to verify it FAILS. Command: `dotnet test POE2Radar.slnx`. Expected failure: build errors `CS0246: The type or namespace name 'SessionTracker' could not be found` and `'SessionStats' could not be found` (because `src/POE2Radar.Core/Session/` does not exist yet). The test run aborts at compile; no tests execute.

- [ ] Implement `SessionStats`. Create `src/POE2Radar.Core/Session/SessionStats.cs` with this EXACT content:

```csharp
namespace POE2Radar.Core.Session;

/// <summary>
/// Immutable snapshot of session counters emitted by <see cref="SessionTracker"/>. Consumed by the
/// overlay renderer (DrawSessionHud) and the HTTP /state API. Pure data — no memory access, no I/O.
/// </summary>
public sealed record SessionStats(
    TimeSpan SessionElapsed,      // wall time since tracker was constructed (app launch) / last Reset
    TimeSpan ZoneElapsed,         // wall time since last zone entry
    int      ZonesEntered,        // count of qualifying zone entries (excludes towns if ExcludeTowns)
    float    ZonesPerHour,        // ZonesEntered / SessionElapsed.TotalHours, 0 when < 1 min elapsed
    string   CurrentZoneName,     // areaCode as-is (display formatting is the renderer's concern)
    int      CurrentAreaLevel,
    int      Deaths,              // lifetime deaths this session
    int      DeathsThisZone);
```

- [ ] Implement `SessionTracker`. Create `src/POE2Radar.Core/Session/SessionTracker.cs` with this EXACT content (full method bodies derived from the spec's contract; there is no `xpNow` param):

```csharp
namespace POE2Radar.Core.Session;

/// <summary>
/// Pure, dependency-free session counter. Fed once per render frame (only when the world snapshot is
/// fresh) with the current area identity, HP percentage, and a caller-supplied tick clock. Emits an
/// immutable <see cref="SessionStats"/>. No memory reads, no rendering, no I/O — lives in
/// POE2Radar.Core so the Core-only test project can cover it.
///
/// HP is the [0,100] percentage (Poe2Live.Vitals.HpPct). Death = exact 0f. A failed vitals read
/// returns the 100f "alive" fallback upstream, so this class never fabricates a death from a bad read.
/// </summary>
public sealed class SessionTracker
{
    private long   _sessionStartTicks;     // set on first Update and on Reset
    private long   _zoneStartTicks;
    private uint   _lastAreaHash;          // 0 = no zone seen yet
    private bool   _firstAreaSeen;         // false until the first Update establishes the initial zone
    private int    _zonesEntered;
    private int    _deaths;
    private int    _deathsThisZone;
    private bool   _hpObservedAboveZero;   // defeats the zone-load HP=0 flash
    private bool   _awaitingRespawn;       // blocks a second death count until HP recovers
    private string _currentZoneName = "";
    private int    _currentAreaLevel;

    /// <summary>
    /// Applies one frame of observation and returns the current snapshot. Call once per render frame,
    /// ONLY when the world snapshot is fresh, so areaHash/areaCode/areaLevel describe the same zone.
    /// </summary>
    public SessionStats Update(
        uint   areaHash,
        string areaCode,
        int    areaLevel,
        float  hpPct,
        long   nowTicks,
        bool   excludeTowns,
        bool   isTown)
    {
        _currentZoneName  = areaCode;
        _currentAreaLevel = areaLevel;

        if (!_firstAreaSeen)
        {
            // First call: the player was already in this zone before the session began. Establish the
            // initial zone WITHOUT counting it as an entry.
            _firstAreaSeen       = true;
            _lastAreaHash        = areaHash;
            _sessionStartTicks   = nowTicks;
            _zoneStartTicks      = nowTicks;
            _hpObservedAboveZero = false;
            _awaitingRespawn     = false;
            _deathsThisZone      = 0;
        }
        else if (areaHash != _lastAreaHash)
        {
            // Zone change: reset per-zone state first so death detection below sees a clean slate.
            _lastAreaHash        = areaHash;
            _zoneStartTicks      = nowTicks;
            _hpObservedAboveZero = false;
            _awaitingRespawn     = false;
            _deathsThisZone      = 0;
            if (!(excludeTowns && isTown))
                _zonesEntered++;
        }

        // Death detection (runs after zone-change reset). HP is [0,100]; "zero" is an exact 0f.
        if (hpPct > 0f && !_hpObservedAboveZero)
        {
            _hpObservedAboveZero = true;   // first valid "alive" observation; defeats load-screen 0-flash
        }
        if (_hpObservedAboveZero && !_awaitingRespawn && hpPct == 0f)
        {
            _deaths++;
            _deathsThisZone++;
            _awaitingRespawn = true;
        }
        else if (_awaitingRespawn && hpPct > 0f)
        {
            _awaitingRespawn = false;      // recovered — allow the next death to register
        }

        return Snapshot(nowTicks);
    }

    /// <summary>
    /// Clears all counters to construction-time state, using <paramref name="nowTicks"/> as the new
    /// session/zone start. Keeps the current zone identity (so the next same-zone Update is NOT a new
    /// entry) — _firstAreaSeen stays true and _lastAreaHash is unchanged.
    /// </summary>
    public void Reset(long nowTicks)
    {
        _sessionStartTicks   = nowTicks;
        _zoneStartTicks      = nowTicks;
        _zonesEntered        = 0;
        _deaths              = 0;
        _deathsThisZone      = 0;
        _hpObservedAboveZero = false;
        _awaitingRespawn     = false;
        _firstAreaSeen       = true;       // current zone stays "seen" — no spurious increment next frame
        // _lastAreaHash intentionally left as the current value.
    }

    private SessionStats Snapshot(long nowTicks)
    {
        double sessionHours = (nowTicks - _sessionStartTicks) / (double)TimeSpan.TicksPerHour;
        float  zonesPerHour = sessionHours < (1.0 / 60.0)
            ? 0f
            : (float)(_zonesEntered / sessionHours);

        return new SessionStats(
            SessionElapsed:   TimeSpan.FromTicks(nowTicks - _sessionStartTicks),
            ZoneElapsed:      TimeSpan.FromTicks(nowTicks - _zoneStartTicks),
            ZonesEntered:     _zonesEntered,
            ZonesPerHour:     zonesPerHour,
            CurrentZoneName:  _currentZoneName,
            CurrentAreaLevel: _currentAreaLevel,
            Deaths:           _deaths,
            DeathsThisZone:   _deathsThisZone);
    }
}
```

- [ ] Run tests to verify PASS. Command: `dotnet test POE2Radar.slnx`. Expected output: `Passed!  - Failed:     0, Passed:    19, Skipped:     0` for the test assembly (19 = the retained SessionTracker facts), and the run exits 0. (If the solution has other Core tests, the total is higher; the key signal is 0 failed and all 19 `SessionTracker*` tests passing.)

- [ ] Commit. Command:
```
git checkout -b feature/session-hud
git add src/POE2Radar.Core/Session/SessionStats.cs src/POE2Radar.Core/Session/SessionTracker.cs tests/POE2Radar.Tests/SessionTrackerTests.cs
git commit -m "feat(core): SessionStats record + pure SessionTracker with unit tests"
```

---

### Task 2: Config + API plumbing
**Files:**
- Modify `src/POE2Radar.Overlay/Config/RadarSettings.cs` (add `SessionHud` property after line 189 region; add `SessionHudSettings` sealed class following the `MonolithSettings` pattern at line 396)
- Modify `src/POE2Radar.Overlay/Web/ApiServer.cs` (RadarState record — append the new param after its LAST parameter `float Fps = 0` at line 1231; `/state` case at line 166/194; `ReadSettings()` at line 617; `ApplySettings()` switch at line 652; add `FormatTimeSpan` helper)

**Interfaces:**
- Consumes: `POE2Radar.Core.Session.SessionStats` (Task 1) — `sealed record SessionStats(TimeSpan SessionElapsed, TimeSpan ZoneElapsed, int ZonesEntered, float ZonesPerHour, string CurrentZoneName, int CurrentAreaLevel, int Deaths, int DeathsThisZone)`.
- Produces:
  - `RadarSettings.SessionHud` of type `SessionHudSettings` with fields `Enabled, ShowPace, ShowZoneContext, ShowDeaths : bool`, `Anchor : string`, `OffsetX, OffsetY : int`, `ExcludeTownsFromPace : bool`.
  - `RadarState` record gains a final optional parameter `SessionStats? Session = null`.
  - The eight API leaf keys (`sessionHudEnabled`, `sessionHudShowPace`, `sessionHudShowZoneContext`, `sessionHudShowDeaths`, `sessionHudAnchor`, `sessionHudOffsetX`, `sessionHudOffsetY`, `sessionHudExcludeTowns`) readable via `ReadSettings()` and writable via `ApplySettings()`.

**Steps:**

- [ ] Open `src/POE2Radar.Overlay/Config/RadarSettings.cs` and read the `MonolithSettings` sealed class around line 396 and the nested-section property block around line 176-189 to confirm exact surrounding syntax (brace style, `get; set;` formatting).

- [ ] Add the `SessionHud` property. In `src/POE2Radar.Overlay/Config/RadarSettings.cs`, immediately after the existing nested-section property block (line 189 region, alongside the other `public XxxSettings Xxx { get; set; } = new();` lines), insert:

```csharp
public SessionHudSettings SessionHud { get; set; } = new();
```

- [ ] Add the `SessionHudSettings` class. In the same file, alongside the other nested sealed setting classes (next to `MonolithSettings` near line 396), insert:

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
    // Behavior-tuning flag (NOT a visibility toggle): defaults TRUE so towns are excluded from pace.
    public bool   ExcludeTownsFromPace  { get; set; } = true;
}
```

- [ ] Open `src/POE2Radar.Overlay/Web/ApiServer.cs` and read these regions to confirm exact syntax: the `RadarState` record declaration (opens at line 1206, with its LAST parameter `float Fps = 0` on line 1231 and `RadarState.Empty` at lines 1233-1235), the `case "/state":` block at lines 166-200 (the `director` projection ends ~line 194), the `ReadSettings()` anonymous object at line 617, the `ApplySettings()` switch at line 652-705, and the `_settings.Save()` call at line 708. Note the `ApplySettings` idiom: each case is `case "key" when Try*(p.Value, out var x): _settings.X = x; applied.Add(p.Name); break;` operating on the `JsonProperty p` from `root.EnumerateObject()` — helpers are `TryBool` / `TryInt` / `TryString` (verified at lines 665-688). Confirm `using POE2Radar.Core.Session;` is present at the top; if absent, add it.

- [ ] Add the `using` (only if not already present). At the top of `src/POE2Radar.Overlay/Web/ApiServer.cs`, with the other `using` directives, add:

```csharp
using POE2Radar.Core.Session;
```

- [ ] Add the optional `Session` parameter to `RadarState`. The `RadarState` record's LAST parameter is `float Fps = 0` (line 1231). Give it a trailing comma and append the new optional parameter on the next line, before the closing `)`. Optional params must be last, so this is the correct anchor:

```csharp
    // ... existing parameters ...
    float Fps = 0,
    SessionStats? Session = null);
```

  `RadarState.Empty` (lines 1233-1235) uses a positional `new(...)` that still compiles with a trailing optional added — no change needed there.

- [ ] Add the `/state` session projection. Inside `case "/state":` (line 166), immediately after the `director` projection (the object/property emitted around line 194), add this property to the anonymous response object (mind the comma after the preceding property):

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

- [ ] Add the `FormatTimeSpan` helper. Add this as a `private static` method on the `ApiServer` class (place it near the other private helpers in `ApiServer.cs`):

```csharp
private static string FormatTimeSpan(TimeSpan t) =>
    $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
```

- [ ] Add the eight `SessionHud` leaf fields to `ReadSettings()`. In `ReadSettings()` (line 617), add these eight properties to the returned anonymous settings object (match the surrounding key style / trailing commas):

```csharp
sessionHudEnabled        = _settings.SessionHud.Enabled,
sessionHudShowPace       = _settings.SessionHud.ShowPace,
sessionHudShowZoneContext= _settings.SessionHud.ShowZoneContext,
sessionHudShowDeaths     = _settings.SessionHud.ShowDeaths,
sessionHudAnchor         = _settings.SessionHud.Anchor,
sessionHudOffsetX        = _settings.SessionHud.OffsetX,
sessionHudOffsetY        = _settings.SessionHud.OffsetY,
sessionHudExcludeTowns   = _settings.SessionHud.ExcludeTownsFromPace,
```

- [ ] Add the eight `ApplySettings()` cases. In `ApplySettings()` (the `switch (p.Name)` over `root.EnumerateObject()` at line 663), add these eight `case` entries, using the EXACT codebase idiom verified at lines 665-688: a `when Try*(p.Value, out var x)` guard, the `_settings.SessionHud.*` mutation, then `applied.Add(p.Name); break;`. The `applied.Add(p.Name)` call is MANDATORY — `_settings.Save()` at line 708 is gated on `applied.Count > 0`, so omitting it means the setting never persists. Bool keys use `TryBool`, the string key uses `TryString`, the int keys use `TryInt`:

```csharp
case "sessionHudEnabled" when TryBool(p.Value, out var b): _settings.SessionHud.Enabled = b; applied.Add(p.Name); break;
case "sessionHudShowPace" when TryBool(p.Value, out var b): _settings.SessionHud.ShowPace = b; applied.Add(p.Name); break;
case "sessionHudShowZoneContext" when TryBool(p.Value, out var b): _settings.SessionHud.ShowZoneContext = b; applied.Add(p.Name); break;
case "sessionHudShowDeaths" when TryBool(p.Value, out var b): _settings.SessionHud.ShowDeaths = b; applied.Add(p.Name); break;
case "sessionHudExcludeTowns" when TryBool(p.Value, out var b): _settings.SessionHud.ExcludeTownsFromPace = b; applied.Add(p.Name); break;
case "sessionHudAnchor" when TryString(p.Value, out var s): _settings.SessionHud.Anchor = s.Trim(); applied.Add(p.Name); break;
case "sessionHudOffsetX" when TryInt(p.Value, out var n): _settings.SessionHud.OffsetX = n; applied.Add(p.Name); break;
case "sessionHudOffsetY" when TryInt(p.Value, out var n): _settings.SessionHud.OffsetY = n; applied.Add(p.Name); break;
```

  Note: the `var b` / `var s` / `var n` pattern-variable names match the neighbouring cases (e.g. `showMonolithPanel` → `_settings.Monoliths.ShowPanel` at line 682, `contributeUrl` → `TryString(... out var s)` at line 683). Each `var` is case-scoped, so reusing `b`/`s`/`n` across multiple cases is fine — copy a neighbouring case verbatim and change only the key string + target member if uncertain. Do NOT invent a `ToBool(value)` helper; it does not exist.

- [ ] Build to verify. Command: `dotnet build POE2Radar.slnx -c Release`. Expected output: `Build succeeded.` with `0 Error(s)` (a non-zero `Warning(s)` count is acceptable if pre-existing). Live `/state` verification is deferred to Task 3 once `RadarApp` actually populates `Session`.

- [ ] Commit. Command:
```
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Web/ApiServer.cs
git commit -m "feat(overlay): SessionHudSettings config + /state session projection + 8 API leaf keys"
```

---

### Task 3: RadarApp wiring + RenderContext + reset hotkey
**Files:**
- Modify `src/POE2Radar.Overlay/Overlay/RenderContext.cs` (add two discrete fields to the `RenderContext` record: `SessionStats? Session`, `SessionHudSettings SessionHudSettings`)
- Modify `src/POE2Radar.Overlay/RadarApp.cs` (fields `_session`, `_sessionSnapshot`, `_nextSessionResetAt`; feed `_session.Update` on `worldFresh` frames at the line 880 guard; pass `Session: _sessionSnapshot` to `new RadarState(...)` at line 893; pass the two new fields to the `RenderContext` ctor in `Tick()`; add the `Ctrl+Alt+R` block in `HandleHotkeys()` at line 1296)

**Interfaces:**
- Consumes:
  - `POE2Radar.Core.Session.SessionTracker` (Task 1) — `public SessionStats Update(uint areaHash, string areaCode, int areaLevel, float hpPct, long nowTicks, bool excludeTowns, bool isTown)`, `public void Reset(long nowTicks)`, parameterless ctor.
  - `POE2Radar.Core.Session.SessionStats` (Task 1).
  - `RadarSettings.SessionHud` / `SessionHudSettings` (Task 2) — `Enabled, ShowPace, ShowZoneContext, ShowDeaths : bool`, `Anchor : string`, `OffsetX, OffsetY : int`, `ExcludeTownsFromPace : bool`.
  - `RadarState(..., SessionStats? Session = null)` (Task 2).
  - Existing in `RadarApp`/`Poe2Live`: `worldFresh` (line 880), `snap.AreaHash`/`snap.AreaCode`/`snap.AreaLevel` (the published `WorldSnapshot`), `_hpPct` (render-thread cached vitals, line 818), `ZoneGuide.Shared.Area(string)?.Town`, the `Down(int)` `GetAsyncKeyState` wrapper (line 2300), `GetForegroundWindow()`, `_gameHwnd`.
- Produces:
  - `RenderContext.Session` (`SessionStats?`) and `RenderContext.SessionHudSettings` (`SessionHudSettings`) — consumed by Task 4.
  - The live `_sessionSnapshot` flowing into `/state` (validates Task 2's projection).

**Steps:**

- [ ] Add the two `RenderContext` fields. In `src/POE2Radar.Overlay/Overlay/RenderContext.cs`, the `RenderContext` record's LAST parameter is currently `bool ShowMonolithPanel = true);` (line 188). Insert the two new optional parameters BEFORE that closing line so they remain among the optional trailing params (give `ShowMonolithPanel` a trailing comma):

```csharp
    bool ShowMonolithPanel = true,
    // ── Session HUD (read-only pace/zone/death overlay). Both discrete fields, mirroring how
    // RenderContext carries Styles/HpBars/TerrainStyle/NavMenuCorner — there is no whole-RadarSettings
    // member. Session is null when the snapshot has not been published yet. ──
    POE2Radar.Core.Session.SessionStats?  Session            = null,
    Config.SessionHudSettings             SessionHudSettings = null!);
```

  Note: `SessionHudSettings` is non-nullable in use but defaulted `null!` so existing `RenderContext(...)` call sites that don't pass it still compile; `RadarApp.Tick()` (next steps) ALWAYS passes a real instance, so the renderer never sees null in practice. `Config.SessionHudSettings` and `POE2Radar.Core.Session.SessionStats` are fully qualified to avoid touching the file's `using` block; confirm `using POE2Radar.Overlay.Config;` already exists at line 2 (it does) so `SessionHudSettings` could also be written unqualified — fully-qualified is used here for clarity.

- [ ] Open `src/POE2Radar.Overlay/RadarApp.cs` and read: the field-declaration region near the top of the class, the `Tick()` body around lines 812-900 (especially `_areaHash` at line 812, `_hpPct` at line 818, the `worldFresh` computation at line 880, the `new RadarState(...)` call at line 893, and the `new RenderContext(...)` call within `Tick()`), and `HandleHotkeys()` around line 1296-1360 (the `Ctrl+Alt+M` idiom near line 1355 and the `Down(...)` wrapper at line 2300). Confirm `using POE2Radar.Core.Session;` is present; add it if missing.

- [ ] Add the `using` (only if missing). At the top of `src/POE2Radar.Overlay/RadarApp.cs`, with the other `using` directives, add:

```csharp
using POE2Radar.Core.Session;
```

- [ ] Declare the three fields. In `src/POE2Radar.Overlay/RadarApp.cs`, with the other private instance fields near the top of the class, add:

```csharp
private readonly SessionTracker  _session = new();
private volatile SessionStats?   _sessionSnapshot;
private DateTime                 _nextSessionResetAt = DateTime.MinValue;
```

- [ ] Feed `SessionTracker` on fresh frames. In `Tick()`, locate the `worldFresh` guard at line 880 (`bool worldFresh = inGame && snap.InGame && snap.AreaHash == _areaHash;`). Immediately AFTER `worldFresh` is computed and BEFORE the `new RadarState(...)` call at line 893, insert:

```csharp
// Session HUD: feed the pure tracker only on fresh frames, so areaHash/areaCode/areaLevel all
// describe the SAME zone (snapshot-consistent). Skip on stale frames and reuse the last snapshot.
// Zero new memory reads: every value below is already read by the loops.
if (worldFresh)
{
    bool isTown = ZoneGuide.Shared.Area(snap.AreaCode)?.Town ?? false;
    _sessionSnapshot = _session.Update(
        snap.AreaHash,
        snap.AreaCode,
        snap.AreaLevel,
        _hpPct,
        DateTime.UtcNow.Ticks,
        _settings.SessionHud.ExcludeTownsFromPace,
        isTown);
}
```

  Note: use the EXACT member access the surrounding code uses for the published snapshot (the local is named `snap` per the spec's line-880 guard) and for the cached HP (`_hpPct`, line 818). If the snapshot local has a different name in the actual file, substitute it consistently in all three `snap.AreaHash/AreaCode/AreaLevel` references and the `worldFresh` expression you read.

- [ ] Pass `Session` to `RadarState`. At the `new RadarState(...)` call (line 893), add `Session: _sessionSnapshot` as a named argument (place it after the existing final argument; because `Session` is the last optional parameter, naming it is safe regardless of ordering):

```csharp
// ... existing RadarState arguments ...,
Session: _sessionSnapshot);
```

- [ ] Pass the two new fields to `RenderContext`. At the `new RenderContext(...)` call inside `Tick()`, add these two NAMED arguments (named so position is irrelevant; place them after the existing final argument):

```csharp
// ... existing RenderContext arguments ...,
Session: _sessionSnapshot,
SessionHudSettings: _settings.SessionHud);
```

  Note: `_settings.SessionHud` is always a real `SessionHudSettings` instance (default-constructed by the property initializer in Task 2), so the renderer never receives null even though the record default is `null!`.

- [ ] Add the `Ctrl+Alt+R` reset block. In `HandleHotkeys()` (line 1296), following the `Ctrl+Alt+M` idiom near line 1355, insert:

```csharp
// Ctrl+Alt+R — reset session counters (read-only: GetAsyncKeyState polling + GetForegroundWindow,
// no input emission, no process write).
if (_settings.SessionHud.Enabled
    && DateTime.UtcNow >= _nextSessionResetAt
    && _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd
    && Down(0x11) && Down(0x12) && Down(0x52))  // VK_CONTROL=0x11, VK_MENU=0x12, VK_R=0x52
{
    _session.Reset(DateTime.UtcNow.Ticks);
    _nextSessionResetAt = DateTime.UtcNow.AddMilliseconds(500);
}
```

  Note: `Down`, `_gameHwnd`, and `GetForegroundWindow` are existing members used by every other hotkey (the `Down` wrapper is at line 2300). Do NOT introduce any new P/Invoke. If the local guard variable name differs (e.g. the foreground HWND field is named differently than `_gameHwnd`), match the EXACT name used by the neighbouring `Ctrl+Alt+M` block you read.

- [ ] Build to verify. Command: `dotnet build POE2Radar.slnx -c Release`. Expected output: `Build succeeded.` with `0 Error(s)`.

- [ ] Run the compliance gate. Command: `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1`. Expected output: a PASS line and exit code 0 (no forbidden input/write symbols — the hotkey uses only `GetAsyncKeyState` via `Down(...)` and `GetForegroundWindow`, both already present and allowed). Confirm exit 0 with `echo $LASTEXITCODE` (PowerShell) showing `0`.

- [ ] Commit. Command:
```
git add src/POE2Radar.Overlay/Overlay/RenderContext.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(overlay): wire SessionTracker into RadarApp + RenderContext + Ctrl+Alt+R reset hotkey"
```

---

### Task 4: OverlayRenderer.DrawSessionHud
**Files:**
- Modify `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (add `private void DrawSessionHud(ID2D1RenderTarget rt, RenderContext ctx)`; add the call site after `DrawMonolithPanel(rt, ctx)` inside the `if (ctx.Active && ctx.InGame)` block at line 131)

**Interfaces:**
- Consumes:
  - `RenderContext.Session` (`SessionStats?`) and `RenderContext.SessionHudSettings` (`SessionHudSettings`) (Task 3).
  - `SessionStats` fields (Task 1): `SessionElapsed, ZoneElapsed : TimeSpan`, `ZonesEntered : int`, `ZonesPerHour : float`, `CurrentZoneName : string`, `CurrentAreaLevel : int`, `Deaths, DeathsThisZone : int`.
  - `SessionHudSettings` fields (Task 2): `Enabled, ShowPace, ShowZoneContext, ShowDeaths : bool`, `Anchor : string`, `OffsetX, OffsetY : int`.
  - Existing renderer members (read from the file): `_bPanel`, `_bText`, `_bStyle` brushes; `_tf` text format (Consolas 12pt, line 85); `ctx.WindowWidth`, `ctx.WindowHeight`; the Vortice `RawRectF` (for FillRectangle) and `Rect` (for DrawText) types; `Color4`; `DrawTextOptions.Clip`. (`ColPanel` is the panel colour the `_bPanel` brush is built from, line 27.)
- Produces: on-screen Session HUD panel (no new types).

**Steps:**

- [ ] Open `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` and read: the `if (ctx.Active && ctx.InGame)` block at line 131 (confirm `DrawMonolithPanel(rt, ctx);` is the last call there), the `DrawMonolithPanel` method at lines 519-551 (note: it fills with `rt.FillRectangle(new Vortice.RawRectF(x, y, x + w, y + h), _bPanel!)` at line 532 and uses `new Rect(...)` ONLY for the per-row `rt.DrawText(...)` calls — this is the exact pattern to mirror), the `DrawNavMenu` corner arithmetic at line 835, the brush fields (`_bPanel`, `_bText`, `_bStyle`) and `ColPanel` (line 27), and the `_tf` text format created at line 85. Confirm the exact `using` for `Vortice.Direct2D1` / `Vortice.Mathematics` so `RawRectF`, `Rect`, `Color4`, `DrawTextOptions` resolve (they already do for `DrawMonolithPanel`).

- [ ] Add the call site. Inside the `if (ctx.Active && ctx.InGame)` block at line 131, immediately after `DrawMonolithPanel(rt, ctx);`, add:

```csharp
DrawSessionHud(rt, ctx);   // new
```

  Resulting block:

```csharp
if (ctx.Active && ctx.InGame)
{
    DrawRuneforge(rt, ctx);
    DrawRitualRewards(rt, ctx);
    DrawMonolithPanel(rt, ctx);
    DrawSessionHud(rt, ctx);   // new
}
```

  Note: match the EXACT set of calls already present in that block in the file; only the trailing `DrawSessionHud(rt, ctx);` is new.

- [ ] Add the `DrawSessionHud` method. Place it next to `DrawMonolithPanel` in `OverlayRenderer.cs` with this EXACT content (note: panel fill uses `Vortice.RawRectF` exactly like `DrawMonolithPanel` line 532; `Rect` is used ONLY for the per-row `DrawText` calls):

```csharp
private void DrawSessionHud(ID2D1RenderTarget rt, RenderContext ctx)
{
    var hud = ctx.SessionHudSettings;
    if (hud == null || !hud.Enabled) return;
    var sess = ctx.Session;
    if (sess == null) return;

    // Build only the enabled rows (pre-formatted strings). Line count drives the panel height.
    var lines = new List<(string text, bool isDeath)>(6);
    if (hud.ShowPace)
    {
        lines.Add(($"Session  {(int)sess.SessionElapsed.TotalHours:D2}:{sess.SessionElapsed.Minutes:D2}:{sess.SessionElapsed.Seconds:D2}", false));
        lines.Add(($"Zone     {(int)sess.ZoneElapsed.TotalHours:D2}:{sess.ZoneElapsed.Minutes:D2}:{sess.ZoneElapsed.Seconds:D2}", false));
        lines.Add(($"Zones    {sess.ZonesEntered}   {sess.ZonesPerHour:F1}/hr", false));
    }
    if (hud.ShowZoneContext)
    {
        lines.Add(($"Area     {sess.CurrentZoneName}", false));
        lines.Add(($"Level    {sess.CurrentAreaLevel}", false));
    }
    if (hud.ShowDeaths)
    {
        lines.Add(($"Deaths   {sess.Deaths} ({sess.DeathsThisZone} here)", sess.Deaths > 0));
    }

    int enabledRowCount = lines.Count;
    if (enabledRowCount == 0) return;   // nothing enabled — touch nothing

    const float panelW = 240f;          // narrower than the 248f monolith panel
    const float pad = 6f, lineH = 15f;  // all rows are data rows — no title row
    float panelH = enabledRowCount * lineH + pad * 2;

    // Corner anchoring — inline arithmetic mirroring DrawNavMenu (line 835), with signed offsets + clamp.
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

    // Panel fill uses RawRectF (every FillRectangle in this file takes RawRectF; Rect is DrawText-only).
    rt.FillRectangle(new Vortice.RawRectF(left, top, left + panelW, top + panelH), _bPanel!);

    float cy = top + pad;
    foreach (var (text, isDeath) in lines)
    {
        var rowRect = new Rect(left + pad, cy, left + panelW - pad, cy + lineH);
        if (isDeath)
        {
            _bStyle!.Color = new Color4(1f, 0.85f, 0.2f, 1f);   // yellow when Deaths > 0
            rt.DrawText(text, _tf!, rowRect, _bStyle!, DrawTextOptions.Clip);
        }
        else
        {
            rt.DrawText(text, _tf!, rowRect, _bText!, DrawTextOptions.Clip);
        }
        cy += lineH;
    }
}
```

  Notes: (a) `_bPanel`, `_bText`, `_bStyle`, and `_tf` are the EXACT existing renderer fields used by `DrawMonolithPanel` — read that method and match the precise field names (if any differ, e.g. the scratch brush is not literally `_bStyle`, substitute the real names; do NOT create new brushes or text formats). (b) The panel fill passes a `Vortice.RawRectF(left, top, right, bottom)` to `FillRectangle` — identical to `DrawMonolithPanel` line 532 — while `new Rect(left, top, right, bottom)` is used ONLY for the per-row `DrawText` calls (`Rect` has no `FillRectangle` overload in this file). Confirm both constructor signatures against `DrawMonolithPanel` and match them. (c) There is no XP row and no K/M/B number formatter anywhere in this method.

- [ ] Build to verify. Command: `dotnet build POE2Radar.slnx -c Release`. Expected output: `Build succeeded.` with `0 Error(s)`.

- [ ] Inspect to verify behaviour (concrete manual check, no test harness). Read the final `DrawSessionHud` in `OverlayRenderer.cs` and confirm ALL of:
  1. The method returns before any `rt.*` call when `hud == null`, `!hud.Enabled`, `sess == null`, or `enabledRowCount == 0` (four early-exit guards, all before the first `FillRectangle`).
  2. Only enabled groups append rows: `ShowPace` → 3 rows, `ShowZoneContext` → 2 rows, `ShowDeaths` → 1 row; with all toggles off, `lines.Count == 0` → returns.
  3. The death row passes `isDeath: sess.Deaths > 0`, and that row alone recolors `_bStyle` to `(1f, 0.85f, 0.2f, 1f)` (yellow); every other row uses `_bText` (white).
  4. The panel fill uses `new Vortice.RawRectF(...)` (NOT `Rect`) for `FillRectangle`, and `Rect` appears only inside the per-row `DrawText` loop.
  5. No XP string, no `K`/`M`/`B` suffix formatting, and no `string.Format` of large numbers appears.

- [ ] Commit. Command:
```
git add src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs
git commit -m "feat(overlay): DrawSessionHud panel — enabled-row build, corner anchoring, death-row tint"
```

---

### Task 5: Dashboard (Settings card + live panel)
**Files:**
- Modify `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (Settings tab: add the Session HUD card with the 8 `data-set` controls; status area: add the live `#session-panel` markup + `renderSessionPanel()` JS; call `renderSessionPanel()` from `renderState()`)

**Interfaces:**
- Consumes:
  - The eight API keys from Task 2 (these `data-set` values MUST match exactly): `sessionHudEnabled`, `sessionHudShowPace`, `sessionHudShowZoneContext`, `sessionHudShowDeaths`, `sessionHudExcludeTowns`, `sessionHudAnchor`, `sessionHudOffsetX`, `sessionHudOffsetY`.
  - The `/state` `session` JSON shape from Task 2: `sessionElapsed, zoneElapsed : string`, `zonesEntered : int`, `zonesPerHour : float`, `currentZoneName : string`, `currentAreaLevel : int`, `deaths, deathsThisZone : int` (or `session == null`).
  - Existing dashboard JS: the module-level `state` variable populated by `tick()` (line 736) every 1000ms (`setInterval(tick, 1000)` line 1691), `renderState()` (called from `tick()` at line 741), and the generic `wireSettings()` (line 794) that auto-wires every `[data-set]` element (its `SELECT` branch at line 798 handles `sessionHudAnchor`).
- Produces: dashboard Settings card + live Session panel (no new types; no new `setInterval`/`fetch`).

**Steps:**

- [ ] Open `src/POE2Radar.Overlay/Web/DashboardHtml.cs` and read: the Settings-tab card region around line 563 (to find where to insert a new `<div class="card">`), the status-area markup that `renderState()` updates, the `renderState()` function body and its call site in `tick()` (line 741), and `wireSettings()` at line 794 (confirm it iterates `[data-set]` generically and the `SELECT` branch at line 798). The whole dashboard is a C# string literal — match its quoting/escaping convention exactly (verbatim `@"..."` vs interpolated; double up `"` if verbatim).

- [ ] Add the Settings card. In the Settings-tab markup (after the line 563 card region), insert this HTML block (matching the file's string-literal escaping):

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

  Note: `wireSettings()` (line 794) and `loadSettings()` auto-wire all `[data-set]` elements generically — NO changes to those JS functions are needed. The eight `data-set` values above must be byte-for-byte identical to the Task 2 API keys.

- [ ] Add the live Session panel markup. In the status area (next to the other live status cards rendered by `renderState()`), insert this `#session-panel` card (matching the file's escaping). It starts hidden; `renderSessionPanel()` shows it when `state.session` is present:

```html
<div id="session-panel" class="card" style="display:none">
  <div class="card-title">Session</div>
  <div class="row"><div class="rl">Session</div><span id="sp-session">—</span></div>
  <div class="row"><div class="rl">Zone</div><span id="sp-zone">—</span></div>
  <div class="row"><div class="rl">Zones</div><span id="sp-zones">—</span></div>
  <div class="row"><div class="rl">Area</div><span id="sp-area">—</span></div>
  <div class="row"><div class="rl">Level</div><span id="sp-level">—</span></div>
  <div class="row"><div class="rl">Deaths</div><span id="sp-deaths">—</span></div>
</div>
```

- [ ] Add the `renderSessionPanel()` JS function. In the dashboard `<script>` (near `renderState()`), add:

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

  Note: there is NO `xp`/`formatXp` reference anywhere in this function. If the dashboard string literal is verbatim (`@"..."`), the JS template-literal backticks need no escaping, but every `"` inside JS must be doubled — match the file's existing convention (e.g. how the existing `renderState()` body is quoted) exactly.

- [ ] Call `renderSessionPanel()` from `renderState()`. At the END of the `renderState()` function body (the function called from `tick()` at line 741), add:

```javascript
renderSessionPanel();
```

- [ ] Build to verify. Command: `dotnet build POE2Radar.slnx -c Release`. Expected output: `Build succeeded.` with `0 Error(s)` (the dashboard is a compiled-in C# string, so a malformed literal would be a build error here).

- [ ] Inspect to verify key parity (concrete manual check). Read the new card + JS in `DashboardHtml.cs` and confirm:
  1. The eight `data-set` attributes are EXACTLY `sessionHudEnabled`, `sessionHudShowPace`, `sessionHudShowZoneContext`, `sessionHudShowDeaths`, `sessionHudExcludeTowns`, `sessionHudAnchor`, `sessionHudOffsetX`, `sessionHudOffsetY` — identical to the Task 2 `ApplySettings`/`ReadSettings` keys (no typos, no casing drift).
  2. `renderSessionPanel()` reads only `state.session` fields named in the Task 2 `/state` projection (`sessionElapsed`, `zoneElapsed`, `zonesEntered`, `zonesPerHour`, `currentZoneName`, `currentAreaLevel`, `deaths`, `deathsThisZone`) — no `xp` field, no `formatXp` call.
  3. `renderSessionPanel()` is invoked at the end of `renderState()`, and there is no new `setInterval`/`fetch` (it reuses the existing 1000ms `tick()` poll).

- [ ] Commit. Command:
```
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(dashboard): Session HUD settings card + live session panel from /state"
```

---

### Task 6: Integration verification & smoke checklist
**Files:** none (no code). Optionally modify a docs/ledger file if the repo tracks one.

**Interfaces:**
- Consumes: all prior tasks' outputs end-to-end (`SessionTracker` → `RadarApp` → `RenderContext`/`/state` → `OverlayRenderer`/dashboard).
- Produces: a verified, mergeable feature branch.

**Steps:**

- [ ] Full Release build. Command: `dotnet build POE2Radar.slnx -c Release`. Expected output: `Build succeeded.` with `0 Error(s)`.

- [ ] Run the Core test suite. Command: `dotnet test POE2Radar.slnx`. Expected output: `Failed:     0` and all 19 `SessionTracker*` facts in `Passed`; the run exits 0.

- [ ] Run the compliance gate. Command: `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1`. Expected output: PASS line, exit code 0 (no `SendInput`/`PostMessage`/`WriteProcessMemory`/`VirtualProtectEx`/etc. introduced; the reset hotkey is `GetAsyncKeyState` + `GetForegroundWindow` only; no pricing layer touched).

- [ ] Run the scrub self-test. Command: `powershell -ExecutionPolicy Bypass -File scripts/scrub-strings.ps1 -SelfTest`. Expected output: self-test PASS, exit code 0.

- [ ] MANUAL smoke checklist (requires the live PoE2 client + a character logged in; launch the built overlay `.exe` and open the dashboard at `http://localhost:7777`). Verify each item:
  1. Dashboard Settings tab shows the new "Session HUD" card; toggling "Enable HUD" + "Pace stats" + "Zone context" + "Deaths" makes the on-screen panel appear and each toggle adds/removes exactly its rows (Pace = 3 rows, Zone context = 2 rows, Deaths = 1 row).
  2. Cycle the "Anchor corner" select through Top Left / Top Right / Bottom Left / Bottom Right and confirm the panel jumps to each corner; nudge Offset X / Offset Y and confirm it shifts and stays clamped on-screen.
  3. Change zones (enter a new map/area) and watch `Zones` increment and the zones-per-hour value update; enter a TOWN with "Exclude towns" ON and confirm `Zones` does NOT increment for the town, then a non-town entry DOES increment.
  4. Die once and confirm the `Deaths` counter ticks exactly once (not twice while lying dead, not on the next zone-load HP flash), turns yellow, and `(N here)` reflects per-zone deaths; on a fresh zone the per-zone count resets while session total persists.
  5. With PoE2 focused, press `Ctrl+Alt+R` and confirm all counters zero (session/zone clocks restart, zones = 0, deaths = 0) and the current zone is NOT re-counted as a new entry on the next frame.
  6. Confirm the dashboard live "Session" panel mirrors the on-screen values (it updates on the 1000ms poll) and hides when the overlay reports no session.

- [ ] Commit any doc/ledger updates (skip if none). Command:
```
git add -A
git commit -m "docs: record Session HUD verification + smoke results"
```

- [ ] Finalize the branch per the team's flow (open a PR from `feature/session-hud` or merge to `main`), confirming the build, `dotnet test`, compliance gate, and scrub self-test were all green before integration.

---

Plan file paths referenced (all absolute):
- `C:/Users/minec/Documents/Projects/POE2GPS/src/POE2Radar.Core/Session/SessionStats.cs` (create)
- `C:/Users/minec/Documents/Projects/POE2GPS/src/POE2Radar.Core/Session/SessionTracker.cs` (create)
- `C:/Users/minec/Documents/Projects/POE2GPS/tests/POE2Radar.Tests/SessionTrackerTests.cs` (create)
- `C:/Users/minec/Documents/Projects/POE2GPS/src/POE2Radar.Overlay/Config/RadarSettings.cs` (modify)
- `C:/Users/minec/Documents/Projects/POE2GPS/src/POE2Radar.Overlay/Web/ApiServer.cs` (modify)
- `C:/Users/minec/Documents/Projects/POE2GPS/src/POE2Radar.Overlay/Overlay/RenderContext.cs` (modify)
- `C:/Users/minec/Documents/Projects/POE2GPS/src/POE2Radar.Overlay/RadarApp.cs` (modify)
- `C:/Users/minec/Documents/Projects/POE2GPS/src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (modify)
- `C:/Users/minec/Documents/Projects/POE2GPS/src/POE2Radar.Overlay/Web/DashboardHtml.cs` (modify)