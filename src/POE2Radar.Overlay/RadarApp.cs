using System.Linq;
using System.Runtime.InteropServices;
using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using POE2Radar.Overlay.Native;
using POE2Radar.Overlay.Navigation;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Overlay;

/// <summary>
/// Drives the PoE2 radar: per-tick resolve chain → read player/entities/terrain/map → render.
/// Read-only. Render rate ~144 Hz (player blip tracks live); the heavier entity/terrain walk
/// runs at ~30 Hz. Projection scale/offset are tweakable live via hotkeys for calibration.
/// </summary>
public sealed class RadarApp : IDisposable
{
    private const int TargetHz = 144;
    private const int WorldHz = 30;

    private readonly ProcessHandle _process;
    private readonly MemoryReader _reader;
    private readonly Poe2Live _live;
    private readonly OverlayWindow _window;
    private readonly OverlayRenderer _renderer;
    private readonly ApiServer _api;
    private readonly RadarSettings _settings;
    private volatile RadarState _state = RadarState.Empty;

    private DateTime _worldAt = DateTime.MinValue;
    private List<Poe2Live.EntityDot> _entities = new();
    private IReadOnlyList<Poe2Live.Landmark> _landmarks = Array.Empty<Poe2Live.Landmark>();
    private Poe2Live.TerrainData? _terrain;
    private uint _areaHash;
    private nint _lastAreaInstance;
    private nint _gameHwnd;
    private volatile bool _shutdown;

    // ── Auto-flask (opt-in input). Foreground + in-game gated; F8 master kill-switch.
    //    Flask keys are configurable in RadarSettings (LifeKey/ManaKey). ──
    private bool _autoFlask = true;                        // auto-on; toggle with F8
    private DateTime _lifeFiredAt = DateTime.MinValue, _manaFiredAt = DateTime.MinValue;
    private DateTime _nextToggleAt = DateTime.MinValue;
    private DateTime _nextPathKeyAt = DateTime.MinValue;
    private DateTime _nextBrowserAt = DateTime.MinValue;
    private float _hpPct = 100f, _manaPct = 100f;
    private string _flaskNote = "";
    private string _areaCode = "", _charName = "";
    private int _charLevel;
    private float[]? _cameraMatrix;

    // ── Phase 1: exploration fog + draw-only path guidance (all gated by RadarSettings flags). ──
    // Unified navigation targets: a single list built each world tick from BOTH terrain-tile
    // landmarks AND entity POIs (bosses, expedition, waypoints…), each addressed by a STABLE STRING
    // id ("t:<path>" / "e:<entityId>"). Multi-select: each selected target draws its OWN full A*
    // route in its OWN color (by selection-order slot). F6 adds the nearest not-yet-selected target;
    // F7 clears the whole selection; clicking a legend row toggles that target. Selection is capped
    // at the palette size so colors stay distinct (and per-tick planning stays bounded). On a zone
    // change the selection is cleared, then the persistent auto-nav patterns re-select matching
    // targets in the new zone.
    private const int AddNearestVk = 0x75; // F6
    private const int ClearPathsVk = 0x76; // F7
    private const int MaxSelectedTargets = 8; // == OverlayRenderer.PathPalette.Length
    // Background A* replanner (single reused PathPlanner on a worker thread) + one RouteTracker per
    // selected id. The tick thread does only CHEAP per-tick maintenance (cursor advance) and rebuilds
    // _selectedPaths from the trackers; the worker owns all A*. See BackgroundReplanner / RouteTracker.
    private readonly BackgroundReplanner _replanner = new();
    private readonly Dictionary<string, RouteTracker> _trackers = new(); // one per selected id; OWNED by the tick thread
    private List<NavTarget> _navTargets = new();                         // unified targets, rebuilt each world tick
    // The ONLY state shared with the HTTP/API thread. Every read/iterate/mutate of _selectedIds is
    // done under _navLock (snapshot to a local, then work outside the lock). Trackers are reconciled
    // from this list on the tick thread only — mutators (in-game + API) just edit _selectedIds.
    private readonly object _navLock = new();
    private readonly List<string> _selectedIds = new();                  // selected target ids (order drives the color slot)
    private List<SelectedPath> _selectedPaths = new();                   // one route per selected target (from trackers)
    private bool _selectionCapWarned;                                    // log the "cap reached" notice once
    private nint _navTargetsArea = -1;                                   // AreaInstance the auto-nav was applied for

    // ── Collapsible "POE2Radar" navigation menu widget state (drawn always-on; persisted corner). ──
    private bool _navMenuExpanded;                                       // dropdown open? (default collapsed)

    public void RequestShutdown() => _shutdown = true;

    public RadarApp(ProcessHandle process, MemoryReader reader, nint gameStateSlot)
    {
        _process = process;
        _reader = reader;
        _settings = RadarSettings.Load();
        Console.WriteLine($"Settings: {RadarSettings.FilePath}");
        _live = new Poe2Live(reader, gameStateSlot);
        _window = OverlayWindow.Create();
        _renderer = new OverlayRenderer(_window);
        // Clicking a legend row toggles that landmark in the path selection. Purely local UI — the
        // click lands on our own overlay window (never forwarded to the game). See UpdateClickThrough.
        _window.OnClientClick = OnOverlayClick;
        _api = new ApiServer(() => _state, _settings, GetNavSelection, ToggleNavTarget, ClearNavSelection, _settings.ApiPort);
        try { _api.Start(); Console.WriteLine($"API on http://localhost:{_settings.ApiPort} (dashboard at /)"); }
        catch (Exception ex) { Console.Error.WriteLine($"API server disabled: {ex.Message}"); }
        Console.WriteLine("Hotkeys: F6=add nearest path target  F7=clear path targets  "
                          + "F8=auto-flask  F9=quit  F12=open dashboard");
    }

    public void Run()
    {
        var targetMs = 1000 / TargetHz;
        _gameHwnd = OverlayNative.FindWindowForProcess(_process.ProcessId);
        while (!_shutdown)
        {
            if (_gameHwnd == 0) _gameHwnd = OverlayNative.FindWindowForProcess(_process.ProcessId);
            if (_gameHwnd != 0) _window.TrackGameWindow(_gameHwnd);
            if (!_window.PumpMessages()) break;
            Tick();
            Thread.Sleep(targetMs);
        }
    }

    private void Tick()
    {
        HandleHotkeys();

        var inGame = _live.TryResolve(out var inGameState, out var areaInstance, out var localPlayer);
        var player = NumVec2.Zero;
        var map = default(Poe2Live.MapUi);
        var areaLevel = 0;

        if (inGame)
        {
            // AreaInstance is a fresh object per area — use its address to invalidate per-area caches.
            if (areaInstance != _lastAreaInstance) { _terrain = null; _lastAreaInstance = areaInstance; }
            _areaHash = _live.AreaHash(areaInstance);
            areaLevel = _live.AreaLevel(areaInstance);

            player = _live.PlayerGrid(localPlayer) ?? NumVec2.Zero;
            map = _live.ReadMap(inGameState, areaInstance);
            _areaCode = _live.AreaCode(areaInstance);
            _charName = _live.PlayerName(localPlayer);
            _charLevel = _live.PlayerLevel(localPlayer);
            _cameraMatrix = _live.CameraMatrix(inGameState);
            TickAutoFlask(localPlayer);

            var now = DateTime.UtcNow;
            if ((now - _worldAt).TotalMilliseconds >= 1000.0 / WorldHz)
            {
                _worldAt = now;
                _terrain ??= _live.Terrain(areaInstance);
                _entities = _live.Entities(areaInstance);
                _landmarks = _live.Landmarks(areaInstance); // cached per area in Poe2Live

                // Rebuild the unified navigation-target list (tiles + entity POIs) for this tick.
                _navTargets = BuildNavTargets(player);

                // On a zone change: drop the (now-stale) selection, then apply the persistent
                // auto-nav patterns against the new zone's targets. Keyed off the AreaInstance
                // address (a fresh object per area), same signal the per-area caches use.
                if (areaInstance != _navTargetsArea)
                {
                    _navTargetsArea = areaInstance;
                    OnAreaChanged();
                }

                // Per-tick route maintenance (draw-only, NO A* on this thread). For each selected
                // target: cheaply advance its cursor; fire a BACKGROUND replan only on a real trigger.
                // Then drain finished routes and rebuild _selectedPaths from the trackers' cursors.
                MaintainRoutes(player);
            }
        }
        else
        {
            _selectedPaths = new List<SelectedPath>();
        }

        _state = new RadarState(inGame, _areaHash, areaLevel, map.IsVisible, map.Zoom, player, _entities, _landmarks,
            _hpPct, _manaPct, _autoFlask, _flaskNote, _areaCode, _charName, _charLevel);

        // Snapshot the selection ONCE per frame (under the lock) into a set the render path can probe
        // freely without holding the lock during the (longer) render. The API thread may be mutating
        // _selectedIds concurrently; this captures a consistent view for this frame.
        var selectedSnapshot = SnapshotSelection();

        var ctx = new RenderContext(
            InGame: inGame,
            Active: _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd,
            WindowWidth: _window.Width,
            WindowHeight: _window.Height,
            PlayerGrid: player,
            Map: map,
            Entities: _entities,
            Landmarks: _landmarks,
            AreaHash: _areaHash,
            Terrain: _terrain,
            ScaleMul: _settings.ScaleMul,
            OffsetX: _settings.OffX,
            OffsetY: _settings.OffY,
            HpPct: _hpPct,
            ManaPct: _manaPct,
            FlaskNote: _flaskNote,
            AreaCode: _areaCode,
            CharLevel: _charLevel,
            CameraMatrix: _cameraMatrix,
            HideJunk: _settings.HideJunk,
            ShowPath: _settings.ShowPath,
            UseCuratedLandmarks: _settings.UseCuratedLandmarks,
            ShowMonsters: _settings.ShowMonsters,
            ShowTerrain: _settings.ShowTerrain,
            HpBarNormal: _settings.HpBarNormal,
            HpBarMagic: _settings.HpBarMagic,
            HpBarRare: _settings.HpBarRare,
            HpBarUnique: _settings.HpBarUnique,
            SelectedPaths: _selectedPaths,
            IsSelected: selectedSnapshot.Contains,
            Legend: BuildLegend(selectedSnapshot),
            NavMenuExpanded: _navMenuExpanded,
            NavMenuCorner: _settings.NavMenuCorner,
            Styles: _settings.Styles,
            HpBars: _settings.HpBars,
            TerrainStyle: _settings.Terrain);
        _renderer.Render(ctx);

        // Make the overlay grab clicks only while the cursor is over a clickable legend row;
        // otherwise stay click-through so the game receives the clicks. Runs after Render so
        // LegendRowRects reflects the frame just drawn.
        UpdateClickThrough(ctx.Active);
    }

    /// <summary>
    /// Per-frame click-through toggle. The overlay captures clicks (click-through OFF) only while the
    /// overlay is active (PoE2 foreground) AND the cursor is currently over a legend row. In every
    /// other case — overlay hidden, PoE2 not foreground, or the map closed (legend empty) — it stays
    /// click-through so we never eat the user's game clicks. Reads only the cursor; sends nothing.
    /// </summary>
    private void UpdateClickThrough(bool active)
    {
        var overWidget = active
                         && _renderer.LegendRowRects.Count > 0
                         && OverlayNative.GetCursorPos(out var pt)
                         && HitTestWidget(ScreenToClientPoint(pt)) is not null;
        _window.SetClickThrough(!overWidget);
    }

    /// <summary>Convert a screen-space cursor point to the overlay window's client coords.</summary>
    private (int X, int Y) ScreenToClientPoint(OverlayNative.POINT screen)
    {
        var p = screen;
        OverlayNative.ScreenToClient(_window.Handle, ref p);
        return (p.X, p.Y);
    }

    /// <summary>
    /// Hit-test a client-space point against the renderer's navigation-menu rects. Returns the
    /// matched Action string (e.g. "menu-toggle", "corner:TopRight", "target:e:123") or null if the
    /// point is over no widget rect. LegendRowRects are in overlay client pixels (D2D renders at
    /// 96 DPI into a DIB sized to the game window's physical client rect, so 1 DIP == 1 device
    /// pixel == 1 client pixel), the same space ScreenToClient yields.
    /// </summary>
    private string? HitTestWidget((int X, int Y) p)
    {
        foreach (var (rect, action) in _renderer.LegendRowRects)
            if (p.X >= rect.Left && p.X < rect.Right && p.Y >= rect.Top && p.Y < rect.Bottom)
                return action;
        return null;
    }

    /// <summary>
    /// WM_LBUTTONDOWN handler (wired to <see cref="OverlayWindow.OnClientClick"/>): dispatch the
    /// click on the navigation-menu widget. "menu-toggle" flips the dropdown; "corner:X" pins the
    /// widget to that screen corner (persisted); "target:&lt;id&gt;" toggles that nav target's selection.
    /// Client coords arrive directly from the window, in the same space as LegendRowRects. Purely
    /// local UI — nothing is ever sent to the game.
    /// </summary>
    private void OnOverlayClick(int clientX, int clientY)
    {
        var action = HitTestWidget((clientX, clientY));
        if (action is null) return;

        if (action == "menu-toggle")
        {
            _navMenuExpanded = !_navMenuExpanded;
        }
        else if (action.StartsWith("corner:", StringComparison.Ordinal))
        {
            _settings.NavMenuCorner = action.Substring("corner:".Length);
            _settings.Save();
        }
        else if (action.StartsWith("target:", StringComparison.Ordinal))
        {
            TogglePathTarget(action.Substring("target:".Length));
        }
    }

    /// <summary>
    /// Auto-flask: press the life/mana flask key when the corresponding pool drops below its
    /// threshold. Hard-gated: enabled + PoE2 is the foreground window + per-flask cooldown.
    /// </summary>
    private void TickAutoFlask(nint localPlayer)
    {
        if (_live.PlayerVitals(localPlayer) is not { } v) return;
        _hpPct = v.HpPct; _manaPct = v.ManaPct;

        if (!_autoFlask) { _flaskNote = "OFF (F8)"; return; }
        if (GetForegroundWindow() != _gameHwnd) { _flaskNote = "paused (PoE2 not focused)"; return; }
        _flaskNote = "armed";

        var now = DateTime.UtcNow;
        if (v.HpPct < _settings.LifeThresholdPct &&
            now - _lifeFiredAt >= TimeSpan.FromMilliseconds(_settings.LifeCooldownMs))
        {
            SendInputNative.Tap((ushort)_settings.LifeKey); _lifeFiredAt = now; _flaskNote = $"life@{v.HpPct:F0}%";
        }
        if (v.ManaPct < _settings.ManaThresholdPct &&
            now - _manaFiredAt >= TimeSpan.FromMilliseconds(_settings.ManaCooldownMs))
        {
            SendInputNative.Tap((ushort)_settings.ManaKey); _manaFiredAt = now; _flaskNote = $"mana@{v.ManaPct:F0}%";
        }
    }

    /// <summary>Poll overlay hotkeys: F8 auto-flask toggle, F9 quit, F12 dashboard, F6/F7 path targets.
    /// Map calibration is web-config-only (no in-game keys, to avoid accidental presses).</summary>
    private void HandleHotkeys()
    {
        // F8 master kill-switch for auto-flask (debounced).
        if (Down(0x77) && DateTime.UtcNow >= _nextToggleAt)
        {
            _autoFlask = !_autoFlask;
            _nextToggleAt = DateTime.UtcNow.AddMilliseconds(300);
            Console.WriteLine($"\nAuto-flask: {(_autoFlask ? "ON" : "OFF")}");
        }
        // F9 quits the overlay (besides the tray-icon Exit).
        if (Down(0x78)) { Console.WriteLine("\nF9 — exiting."); RequestShutdown(); }

        // F12 opens the web dashboard in the default browser — only while PoE2 is the foreground
        // window (debounced). Purely launches a browser; sends nothing to the game.
        if (Down(0x7B) && DateTime.UtcNow >= _nextBrowserAt
            && _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd)
        {
            _nextBrowserAt = DateTime.UtcNow.AddMilliseconds(800);
            OpenDashboard();
        }

        // F6 adds the nearest not-yet-selected landmark to the path selection; F7 clears it.
        // Both debounced.
        if (DateTime.UtcNow >= _nextPathKeyAt)
        {
            if (Down(AddNearestVk))
            {
                AddNearestPathTarget();
                _nextPathKeyAt = DateTime.UtcNow.AddMilliseconds(300);
            }
            else if (Down(ClearPathsVk))
            {
                ClearPathTargets();
                _nextPathKeyAt = DateTime.UtcNow.AddMilliseconds(300);
            }
        }

    }

    // ── Unified navigation-target selection (draw-only guidance, multi-select). ──────────────
    // Model: _navTargets is one list built each world tick from BOTH tile landmarks AND entity POIs,
    // each addressed by a STABLE STRING id ("t:<path>" / "e:<entityId>"). _selectedIds is the ordered
    // set of selected ids; an id's position in that list is its color SLOT (0..7), so each selected
    // target draws its own A* route + legend swatch in its own color. F6 adds the nearest not-yet-
    // selected target; F7 clears all; clicking a legend row toggles that target. The selection is
    // capped at MaxSelectedTargets (palette size) so colors stay distinct and per-tick planning is
    // bounded. On a zone change the selection is cleared and the persistent auto-nav patterns re-
    // select matching targets.

    /// <summary>
    /// Build the unified navigation-target list for this world tick: every tile landmark first
    /// (stable order from the cached landmark list), then qualifying entity POIs ordered nearest-
    /// first. An entity qualifies as a POI when it's alive AND (game-flagged POI via MinimapIcon,
    /// OR a unique monster, OR its metadata matches an auto-nav pattern). Deduped by stable id.
    /// </summary>
    private List<NavTarget> BuildNavTargets(NumVec2 player)
    {
        var targets = new List<NavTarget>(_landmarks.Count + 16);
        var seen = new HashSet<string>();

        // (a) Tile landmarks — id "t:<path>".
        foreach (var lm in _landmarks)
        {
            var id = "t:" + lm.Path;
            if (!seen.Add(id)) continue;
            targets.Add(new NavTarget(id, LandmarkLabel(lm), lm.Center, lm.Path, IsEntity: false));
        }

        // (b) Entity POIs — id "e:<entityId>", nearest-first.
        var pois = _entities
            .Where(e => e.IsAlive &&
                        (e.Poi
                         || (e.Category == Poe2Live.EntityCategory.Monster && e.Rarity == Poe2Live.Rarity.Unique)
                         || MatchesAutoNav(e.Metadata)))
            .OrderBy(e => NumVec2.DistanceSquared(e.Grid, player));
        foreach (var e in pois)
        {
            var id = "e:" + e.Id;
            if (!seen.Add(id)) continue;
            targets.Add(new NavTarget(id, EntityLabel(e.Metadata), e.Grid, e.Metadata, IsEntity: true));
        }

        return targets;
    }

    /// <summary>Zone change: clear the (stale) selection, then apply the persistent auto-nav patterns
    /// against the new zone's <see cref="_navTargets"/> so e.g. the expedition encounter is auto-pathed.</summary>
    private void OnAreaChanged()
    {
        // Drop the stale selection (under the lock), then apply the persistent auto-nav patterns
        // against the new zone's targets. Trackers are NOT touched here — the per-tick reconciliation
        // (ReconcileTrackers) creates/removes them to match _selectedIds; in-flight results for the
        // now-removed ids are ignored on drain (no matching tracker).
        int count;
        lock (_navLock)
        {
            _selectedIds.Clear();
            _selectionCapWarned = false;

            if (_settings.AutoNavPatterns.Count > 0)
            {
                foreach (var t in _navTargets)
                {
                    if (_selectedIds.Count >= MaxSelectedTargets) break;
                    if (MatchesAutoNav(t.MatchKey) && !_selectedIds.Contains(t.Id))
                        _selectedIds.Add(t.Id);
                }
            }
            count = _selectedIds.Count;
        }
        _selectedPaths = new List<SelectedPath>();

        if (count > 0)
            Console.WriteLine($"\nAuto-nav: selected {count} target(s) on zone change.");
    }

    /// <summary>case-insensitive Contains of ANY configured auto-nav pattern against a metadata/path.</summary>
    private bool MatchesAutoNav(string metadataOrPath)
    {
        if (string.IsNullOrEmpty(metadataOrPath)) return false;
        foreach (var pat in _settings.AutoNavPatterns)
            if (!string.IsNullOrEmpty(pat) && metadataOrPath.Contains(pat, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>F6: add the nearest navigation target not already selected into the selection.</summary>
    private void AddNearestPathTarget()
    {
        if (_navTargets.Count == 0) return;
        var player = _state.Player;

        // _navTargets isn't fully distance-sorted (tiles come first), so scan for the nearest
        // unselected target by grid distance. Snapshot the selection to test membership.
        var selected = SnapshotSelection();
        var bestId = (string?)null;
        var bestD = float.MaxValue;
        foreach (var t in _navTargets)
        {
            if (selected.Contains(t.Id)) continue;
            var d = NumVec2.DistanceSquared(t.Grid, player);
            if (d < bestD) { bestD = d; bestId = t.Id; }
        }
        if (bestId is not null) ToggleSelectionCore(bestId); // shares the cap check + locked mutate + log
    }

    /// <summary>F7: clear the entire path selection. Only edits _selectedIds (under the lock); the
    /// per-tick reconciliation removes the now-orphaned trackers.</summary>
    private void ClearPathTargets()
    {
        bool wasEmpty;
        lock (_navLock)
        {
            wasEmpty = _selectedIds.Count == 0;
            _selectedIds.Clear();
            _selectionCapWarned = false;
        }
        if (!wasEmpty) Console.WriteLine("\nPath targets: cleared");
    }

    /// <summary>
    /// Toggle a navigation target by its stable id (legend-row click / F6 / API). Delegates to the
    /// single locked toggle core so in-game and API mutations share identical semantics.
    /// </summary>
    private void TogglePathTarget(string id) => ToggleSelectionCore(id);

    /// <summary>
    /// THE one place the selection set is mutated. Adds the id if absent (unless at the cap), removes
    /// it if present — all under <see cref="_navLock"/>. Does NOT touch trackers (those are created/
    /// removed by the tick-thread reconciliation from _selectedIds), so it is safe to call from the
    /// HTTP thread. Returns the new selection labels for logging.
    /// </summary>
    private void ToggleSelectionCore(string id)
    {
        if (string.IsNullOrEmpty(id)) return;

        bool changed;
        string labels;
        lock (_navLock)
        {
            if (_selectedIds.Remove(id))
            {
                _selectionCapWarned = false;
                changed = true;
            }
            else if (_selectedIds.Count >= MaxSelectedTargets)
            {
                if (!_selectionCapWarned)
                {
                    Console.WriteLine($"\nPath targets: selection full ({MaxSelectedTargets}); ignoring add.");
                    _selectionCapWarned = true;
                }
                return; // over cap — ignore the add
            }
            else
            {
                _selectedIds.Add(id);
                changed = true;
            }

            labels = _selectedIds.Count == 0 ? "none" : string.Join(", ", _selectedIds.Select(TargetLabel));
        }

        if (changed) Console.WriteLine($"\nPath targets: {labels}");
    }

    /// <summary>Snapshot the current selection ids (under the lock) into a fresh list — the standard
    /// way every reader observes the selection without holding the lock during its work.</summary>
    private List<string> SnapshotSelection()
    {
        lock (_navLock) return new List<string>(_selectedIds);
    }

    /// <summary>
    /// Tick-thread tracker reconciliation: bring the (tick-thread-owned) <see cref="_trackers"/> map in
    /// line with the selection. Creates a <see cref="RouteTracker"/> (and enqueues its initial replan)
    /// for any selected id lacking one, and removes trackers whose id is no longer selected (their
    /// in-flight results are ignored on drain). This is the ONLY code that adds/removes trackers, so
    /// API-thread selection edits never race the tracker map. Takes a selection snapshot.
    /// </summary>
    private void ReconcileTrackers(List<string> selected)
    {
        // Remove trackers no longer selected.
        if (_trackers.Count > 0)
        {
            var live = new HashSet<string>(selected);
            var stale = _trackers.Keys.Where(k => !live.Contains(k)).ToList();
            foreach (var id in stale) _trackers.Remove(id);
        }

        // Create trackers for newly-selected ids and kick off their first plan.
        foreach (var id in selected)
        {
            if (_trackers.ContainsKey(id)) continue;
            var tracker = new RouteTracker();
            _trackers[id] = tracker;
            if (TryResolveTargetGrid(id, out var grid))
                EnqueueReplan(id, tracker, grid);
        }
    }

    /// <summary>
    /// Resolve ANY selected id to its current goal grid against the live world (not just the curated
    /// <see cref="_navTargets"/> menu), so the dashboard can navigate to any entity/landmark:
    /// <list type="bullet">
    /// <item>"t:&lt;path&gt;" → the landmark in <see cref="_landmarks"/> whose Path matches; grid = Center.</item>
    /// <item>"e:&lt;id&gt;" → the entity in <see cref="_entities"/> whose Id matches; grid = Grid.</item>
    /// </list>
    /// Returns false if the id is malformed or the target isn't present this tick (despawned / other
    /// zone) — callers keep the id selected and simply skip planning until it resolves.
    /// </summary>
    private bool TryResolveTargetGrid(string id, out NumVec2 grid)
    {
        grid = default;
        if (string.IsNullOrEmpty(id) || id.Length < 2) return false;

        if (id.StartsWith("t:", StringComparison.Ordinal))
        {
            var path = id[2..];
            foreach (var lm in _landmarks)
                if (lm.Path == path) { grid = lm.Center; return true; }
            return false;
        }

        if (id.StartsWith("e:", StringComparison.Ordinal))
        {
            if (!uint.TryParse(id[2..], out var entityId)) return false;
            foreach (var e in _entities)
                if (e.Id == entityId) { grid = e.Grid; return true; }
            return false;
        }

        return false;
    }

    /// <summary>
    /// Per-tick route maintenance — runs on the tick thread, NEVER calls A*. Snapshots the selection
    /// (once, under the lock), reconciles the tracker map to it, then for each selected target:
    /// advance its cursor (cheap), and if a trigger fires and no replan is in flight, enqueue a
    /// BACKGROUND replan toward the target's resolved grid. Then drain finished routes into the
    /// trackers and rebuild <see cref="_selectedPaths"/> from the trackers' cursors.
    /// </summary>
    private void MaintainRoutes(NumVec2 player)
    {
        // Snapshot the selection ONCE; everything below works off this local list (tick-thread only).
        var selected = SnapshotSelection();

        // (a) Bring the tick-thread-owned tracker map in line with the selection (create/remove).
        ReconcileTrackers(selected);

        // (b) Maintain + trigger replans. Resolve each id to its live grid; if it doesn't resolve this
        //     tick (despawned / not yet present) keep it selected but skip planning.
        foreach (var id in selected)
        {
            if (!_trackers.TryGetValue(id, out var tracker)) continue;
            tracker.Maintain(player);
            if (!TryResolveTargetGrid(id, out var goal)) continue;
            if (!tracker.ReplanInFlight && tracker.ShouldReplan(player, goal))
                EnqueueReplan(id, tracker, goal);
        }

        // (c) Drain completed background routes; apply only those still tracked.
        if (_replanner.TryDrainResults(out var results))
        {
            foreach (var r in results)
            {
                if (!_trackers.TryGetValue(r.TargetId, out var tracker)) continue; // deselected → ignore
                tracker.ApplyResult(r.Waypoints, new NumVec2(r.Goal.x, r.Goal.y));
                Console.WriteLine($"replan: {TargetLabel(r.TargetId)} = {r.Waypoints.Count} waypoints");
            }
        }

        // (d) Cheap rebuild of the draw list from each tracker's current (cursor-advanced) points.
        RebuildSelectedPaths(selected);
    }

    /// <summary>Snapshot the immutable terrain + player/goal and hand a replan request to the worker
    /// (marks the tracker in-flight). No A* on this thread.</summary>
    private void EnqueueReplan(string id, RouteTracker tracker, NumVec2 goal)
    {
        if (_terrain is not { } terrain) return; // can't plan without terrain yet
        var player = _state.Player;
        tracker.MarkReplanRequested();
        _replanner.Enqueue(new BackgroundReplanner.Request(
            id, terrain, ((int)player.X, (int)player.Y), ((int)goal.X, (int)goal.Y)));
    }

    /// <summary>Rebuild <see cref="_selectedPaths"/> from the trackers' CurrentPoints, each colored by
    /// its id's selection-order slot (capped at the palette size). CHEAP — no A*. Takes a selection
    /// snapshot so it never touches _selectedIds directly.</summary>
    private void RebuildSelectedPaths(List<string> selected)
    {
        var paths = new List<SelectedPath>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            if (!_trackers.TryGetValue(selected[i], out var tracker)) continue;
            var pts = tracker.CurrentPoints;
            if (pts.Count > 0) paths.Add(new SelectedPath(Math.Min(i, MaxSelectedTargets - 1), pts));
        }
        _selectedPaths = paths;
    }

    /// <summary>Display label for a selected id (its NavTarget name if still present, else the raw id).</summary>
    private string TargetLabel(string id)
    {
        foreach (var t in _navTargets) if (t.Id == id) return t.Name;
        return id;
    }

    /// <summary>Friendly display label for a tile landmark (curated if enabled + present, else derived).</summary>
    private string LandmarkLabel(Poe2Live.Landmark lm)
        => _settings.UseCuratedLandmarks && lm.CuratedName is { } c ? c : lm.Name;

    /// <summary>
    /// Turn an entity metadata path into a readable label: take the last '/'-segment, strip a trailing
    /// "_NN"/digit run, and insert spaces before interior capitals
    /// (e.g. ".../Expedition2/Expedition2Encounter" → "Expedition Encounter";
    /// "Waypoint_LongActivationRadius" → "Waypoint Long Activation Radius").
    /// </summary>
    private static string EntityLabel(string metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return "(entity)";
        var slash = metadata.LastIndexOf('/');
        var seg = slash >= 0 ? metadata[(slash + 1)..] : metadata;

        // Strip a trailing "_NN" or trailing digit run (e.g. "Expedition2Encounter" keeps the
        // interior "2"; "Encounter_03" → "Encounter").
        var end = seg.Length;
        while (end > 0 && char.IsDigit(seg[end - 1])) end--;
        if (end > 0 && seg[end - 1] == '_') end--;
        if (end > 0) seg = seg[..end];

        // Insert spaces before interior capitals / before a digit-to-letter or letter-to-digit edge.
        var sb = new System.Text.StringBuilder(seg.Length + 8);
        for (var i = 0; i < seg.Length; i++)
        {
            var ch = seg[i];
            if (i > 0)
            {
                var prev = seg[i - 1];
                var boundary = (char.IsUpper(ch) && (char.IsLower(prev) || char.IsDigit(prev)))
                               || (char.IsDigit(ch) && char.IsLetter(prev) && !char.IsDigit(prev));
                if (boundary && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            sb.Append(ch);
        }
        var label = sb.ToString().Trim();
        return label.Length == 0 ? "(entity)" : label;
    }

    /// <summary>Build the legend rows (one per unified navigation target), marking the selected targets
    /// and their selection-order color slot (-1 when unselected). Takes a selection snapshot so it
    /// doesn't touch _selectedIds while the API thread may be mutating it.</summary>
    private List<LegendEntry> BuildLegend(List<string> selected)
    {
        var legend = new List<LegendEntry>(_navTargets.Count);
        foreach (var t in _navTargets)
        {
            var slot = selected.IndexOf(t.Id);
            legend.Add(new LegendEntry(t, slot, slot >= 0));
        }
        return legend;
    }

    // ── Public navigation accessors (callable from the API/HTTP thread; all _navLock-guarded). ──

    /// <summary>API: a snapshot of the selected ids with their slot (index in selection order).
    /// Safe to call concurrently with the tick loop.</summary>
    public IReadOnlyList<(string Id, int Slot)> GetNavSelection()
    {
        lock (_navLock)
        {
            var list = new List<(string, int)>(_selectedIds.Count);
            for (var i = 0; i < _selectedIds.Count; i++) list.Add((_selectedIds[i], i));
            return list;
        }
    }

    /// <summary>API: toggle a nav target by id — add if absent (respecting the cap), remove if present.
    /// Shares the exact locked core the in-game toggle uses; only edits _selectedIds (trackers are
    /// reconciled on the tick thread). Safe to call concurrently with the tick loop.</summary>
    public void ToggleNavTarget(string id) => ToggleSelectionCore(id);

    /// <summary>API: clear the whole nav selection. Safe to call concurrently with the tick loop.</summary>
    public void ClearNavSelection() => ClearPathTargets();

    /// <summary>Open the web dashboard in the user's default browser (F12). Launches a browser only —
    /// nothing is sent to the game.</summary>
    private void OpenDashboard()
    {
        var url = $"http://localhost:{_settings.ApiPort}/";
        try
        {
            Console.WriteLine($"F12 — opening {url}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Console.Error.WriteLine($"Open dashboard failed: {ex.Message}"); }
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    public void Dispose()
    {
        _replanner.Dispose();
        _api.Dispose();
        _renderer.Dispose();
        _window.Dispose();
    }
}
