using System.Linq;
using System.Runtime.InteropServices;
using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Core.Navigation;
using POE2Radar.Core.Gear;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Native;
using POE2Radar.Overlay.Navigation;
using POE2Radar.Overlay.Overlay;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Overlay;

/// <summary>
/// Drives the PoE2 radar: per-tick resolve chain → read player/entities/terrain/map → render.
/// Read-only. Render rate is configurable (RadarSettings.FpsCap, default 60 Hz; player blip tracks
/// live); the heavier entity/terrain walk runs at ~30 Hz. Projection scale/offset are tweakable live
/// via hotkeys for calibration.
/// </summary>
public sealed class RadarApp : IDisposable
{
    private const int WorldHz = 30;

    private readonly ProcessHandle _process;
    // Three INDEPENDENT reader stacks over the one shared ProcessHandle (ReadProcessMemory is itself
    // concurrency-safe; the per-instance buffers + caches in MemoryReader/Poe2Live are NOT). Each thread
    // owns its own so nothing mutable is shared: _live = world thread (entity/terrain/landmark walk),
    // _liveRender = render thread (player/vitals/camera/map + HP-bar live reads), _liveApi = HTTP thread
    // (tile-path scans). _atlas is internally locked, so it's shared across all three.
    private readonly MemoryReader _reader;        // world thread
    private readonly Poe2Live _live;              // world thread
    private readonly MemoryReader _readerRender;  // render thread
    private readonly Poe2Live _liveRender;        // render thread
    private readonly MemoryReader _readerApi;     // HTTP/API thread
    private readonly Poe2Live _liveApi;           // HTTP/API thread
    private readonly Poe2Atlas _atlas;
    private readonly Poe2Runeforge _runeforge;    // world thread (reads the rune-crafting reward panel)
    private readonly OverlayWindow _window;
    private readonly OverlayRenderer _renderer;
    private readonly ApiServer _api;
    private readonly RadarSettings _settings;
    private readonly HiddenEntities _hidden;
    private readonly WatchedEntities _watched;
    private readonly LandmarkPatterns _landmarkPatterns;
    private readonly DisplayRules _displayRules;
    // Cached delegates for the per-frame RenderContext, so we don't allocate a method-group delegate +
    // closure every render frame. Bound once after _displayRules is constructed.
    private Func<Poe2Live.EntityDot, DisplayRule?>? _resolveEntity;
    private Func<string, DisplayRule?>? _resolveTileDraw;
    private readonly LandmarkStore _landmarkStore;
    private readonly ModCatalog _modCatalog;
    private readonly SeenPoiLog _seenPoiLog;
    private readonly EntityAtlasLog _entityAtlas;
    private readonly EntityNameStore _entityNameStore;
    private readonly GearWeightStore _gearWeights;
    private volatile GearSnapshot? _gearSnapshot;   // God-Roll Detector (experimental); null when off
    private int _gearTickCounter;
    private int _landmarkGen;
    private int _displayRulesGen;
    private int _landmarkStoreGen;
    private int _appliedClusterGap;
    private bool _appliedExcludeFromCapture;
    private nint _areaInstanceForApi;   // current AreaInstance, for the /api/tiles tile-path lookup
    private nint _inGameStateForApi;    // current InGameState, for the /api/atlas node read
    private volatile RadarState _state = RadarState.Empty;

    // ── Atlas overlay: live node highlights (takes precedence over the radar when the atlas is open). ──
    // The render-consumed outputs (open flag + marks + route) are published as ONE immutable record the
    // world thread swaps atomically and the render thread reads lock-free — same lock-free-snapshot idiom
    // as _world / _state below.
    // A route/marker point: the node's UiElement (so the render thread re-reads live RelativePos per frame →
    // smooth pan) PLUS the world-walk's baked position as a fallback when that read is rejected (stale/freed
    // element scrolled off-screen, or garbage) — without the fallback those bad reads streaked lines.
    private readonly record struct AtlasPoint(nint El, float Bx, float By);
    private sealed record AtlasAutoSpec(IReadOnlyList<AtlasPoint> Points, string? Color, int Hops);
    private sealed record AtlasRender(bool Open, IReadOnlyList<AtlasMark> Marks, AtlasPoint? Start, AtlasPoint? End, IReadOnlyList<AtlasPoint>? Route, AtlasPoint? Current, IReadOnlyList<AtlasAutoSpec> AutoRoutes)
    {
        public static readonly AtlasRender Closed = new(false, Array.Empty<AtlasMark>(), null, null, null, null, Array.Empty<AtlasAutoSpec>());
    }
    private volatile AtlasRender _atlasRender = AtlasRender.Closed;
    private readonly List<AtlasMark> _atlasMarkFrame = new();   // render-thread scratch: marks with per-frame-fresh relPos

    // ── Runeforge ("Runeshape Combinations") priced-reward labels: same lock-free published-record idiom.
    //    Built on the world thread (panel read + price lookup), read by the render thread. ──
    private sealed record RuneRender(bool Open, IReadOnlyList<RuneLabel> Labels)
    {
        public static readonly RuneRender Closed = new(false, Array.Empty<RuneLabel>());
    }
    private volatile RuneRender _runeRender = RuneRender.Closed;

    // ── Ritual tribute-shop priced-reward labels: same lock-free published-record idiom. Built on the world
    //    thread (read the 5 reward tiles' item entities + price each), drawn by the render thread on each tile.
    private sealed record RitualRender(bool Open, IReadOnlyList<RitualLabel> Labels)
    {
        public static readonly RitualRender Closed = new(false, Array.Empty<RitualLabel>());
    }
    private volatile RitualRender _ritualRender = RitualRender.Closed;

    // ── Loot-tag value chips: the WORLD thread scans the visible UI tree for tag text, matches each to a
    //    priced item by name, and publishes a spec per match (the tag's UiElement address + value). The
    // ── Runeshape monoliths (priced offered rewards, read off the in-world device — works area-wide,
    //    before the panel is opened). World-space markers; published per area-hash for the zone-load guard. ──
    private readonly RuneMonolithCatalog _monoCatalog = RuneMonolithCatalog.Instance;
    private sealed record MonolithRender(uint AreaHash, IReadOnlyList<MonolithMarker> Markers)
    {
        public static readonly MonolithRender Empty = new(0, Array.Empty<MonolithMarker>());
    }
    private volatile MonolithRender _monoRender = MonolithRender.Empty;

    private readonly object _atlasLock = new();
    private readonly HashSet<nint> _atlasSel = new();   // selected node element addresses (from the dashboard)
    private DateTime _nextInspectAt = DateTime.MinValue; // F10 hotkey debounce (render thread)
    // F10 route workflow (manual, no memory-marker dependency): 1st F10 sets START tile, 2nd sets END tile
    // (and routes between them through the connection graph), 3rd resets. Stored by GRID coord so they
    // survive pan/zoom and the tiles going off-screen. Written by F10 (render thread), read by UpdateAtlas
    // (world thread) — guarded by _atlasLock (nullable int-tuples aren't torn-read-safe).
    private (int X, int Y)? _atlasStartGrid;
    private (int X, int Y)? _atlasGoalGrid;
    private DateTime _atlasGoodAt = DateTime.MinValue; // last tick we read nodes — debounces transient misses (world)
    private long _lastAtlasSig;          // view+inputs signature — when unchanged, marks/route stay frozen (no arrow jitter)
    private bool _builtAtlasOnce;        // marks built at least once this atlas session (world)
    // Live atlas zoom (= canvas/node scale @ +0x130; 0.85 max-out … larger zoomed in). relPos is read
    // live (pan baked in) and the projection scales by this zoom, so rings track pan AND zoom.
    private volatile float _atlasZoom = 0.85f;
    private volatile UpdateChecker.Result? _update;   // GitHub version check (best-effort, set async at startup)
    // Atlas projection is derived live from the game window height (UIscale = winH/1600 × live zoom) in
    // AtlasProjection — resolution-correct, no calibration. (The 1080p reference: scale = (1080/1600)×0.85
    // ≈ 0.574 at max zoom-out, offset 0.)

    /// <summary>Directory holding the user config files (shared with <see cref="RadarSettings"/>).</summary>
    private static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "config");

    // ── World-thread working fields (written ONLY by the world tick; never read by the render thread —
    //    the render thread reads the published _world snapshot instead). ──
    private Thread? _worldThread;                           // the ~30 Hz background world loop (self-paced)
    private List<Poe2Live.EntityDot> _entities = new();     // world only
    // Monster HP-bar pipeline: the SPEC (style + which mobs get a bar + their component addresses) is
    // rebuilt at WORLD rate; _hpFrame (live position + HP) is rebuilt every RENDER frame from cheap per-mob
    // reads (via the spec's captured addresses) so bars track moving monsters smoothly without re-walking.
    private readonly record struct HpBarSpec(nint Entity, nint Render, nint Life, float Width, uint Fill, float BorderWidth, uint Border);
    private readonly List<HpBarTarget> _hpFrame = new();   // render-thread scratch (rebuilt per frame)
    // Ground-item label SPEC (world rate): the priced facts + the item's Render component address. Its
    // live world position is re-read every RENDER frame into _itemFrame so the label tracks smoothly
    // (dropped items bob, so a 30 Hz-sampled position aliases/jitters when projected at render rate —
    // same reason HP bars re-read per frame).
    private readonly record struct ItemLabelSpec(nint Render, string Name, string Value, bool Highlight, bool ShowName);
    private readonly List<ItemLabel> _itemFrame = new();   // render-thread scratch (rebuilt per frame)
    private IReadOnlyList<Poe2Live.Landmark> _landmarks = Array.Empty<Poe2Live.Landmark>(); // world only
    private Poe2Live.TerrainData? _terrain;                 // world only
    private int _charLevel;                                 // world only (published in the snapshot)
    private nint _lastAreaInstance;                         // world only: terrain-cache invalidation + atlas anchor

    // ── Published lock-free snapshot: the world tick swaps this whole immutable record; the render thread
    //    reads it once per frame. Same idiom as _state / _atlasRender. ──
    private sealed record WorldSnapshot(
        bool InGame, uint AreaHash, int AreaLevel, string AreaCode, int CharLevel,
        IReadOnlyList<Poe2Live.EntityDot> Entities,
        IReadOnlyList<Poe2Live.Landmark> Landmarks,
        Poe2Live.TerrainData? Terrain,
        IReadOnlyList<HpBarSpec> HpSpecs,
        IReadOnlyList<ItemLabelSpec> ItemLabels,
        IReadOnlyList<SelectedPath> SelectedPaths,
        IReadOnlyList<LegendEntry> Legend,
        IReadOnlyList<string> SelectedSnapshot)
    {
        public static readonly WorldSnapshot Empty = new(
            false, 0, 0, "", 0, Array.Empty<Poe2Live.EntityDot>(), Array.Empty<Poe2Live.Landmark>(), null,
            Array.Empty<HpBarSpec>(), Array.Empty<ItemLabelSpec>(), Array.Empty<SelectedPath>(),
            Array.Empty<LegendEntry>(), Array.Empty<string>());
    }
    private volatile WorldSnapshot _world = WorldSnapshot.Empty;
    private NumVec2 _worldPlayer;          // the world tick's current player grid (for off-thread replans)
    private volatile float _worldMs, _renderMs;  // last world-pass / render-frame durations (ms) — /state timers
    private volatile float _fps;                  // measured render FPS (effective, over a rolling window) — /state
    private int _autoHz = 144;                     // detected refresh of the monitor the game is on (FpsCap=0 → auto; 144 = pre-detection fallback)
    private bool _autoHzLogged;                     // log the first successful detection (proves it read the monitor, not the fallback)
    private DateTime _nextHzCheckUtc = DateTime.MinValue;

    private uint _areaHash;        // render thread: live area hash (RadarState + zone-load draw gate)
    private nint _gameHwnd;
    private volatile bool _shutdown;

    // ── Read-only player vitals readout (HP/Mana/ES) for the HUD + dashboard. ──
    private DateTime _nextPathKeyAt = DateTime.MinValue;
    private DateTime _nextBrowserAt = DateTime.MinValue;
    private float _hpPct = 100f, _manaPct = 100f, _esPct = 100f;
    private float[]? _cameraMatrix;

    // Render inputs rebuilt at world rate (30 Hz), not per render frame: they only change with the
    // selection / nav-target list. _overlayHadContent gates the present so we skip the (resolution-
    // proportional) UpdateLayeredWindow blit while PoE2 isn't foreground — but still push ONE blank
    // frame on focus-loss so a stale overlay never lingers over other apps.
    private List<string> _selectedSnapshot = new();
    private IReadOnlyList<LegendEntry> _legend = Array.Empty<LegendEntry>();
    private bool _overlayHadContent;

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
    private readonly Dictionary<string, RouteTracker> _trackers = new(); // one per selected id; OWNED by the world thread
    // Built wholesale by the world tick; read by reference from the render thread (F6 add-nearest) and the
    // API thread (TargetLabel). volatile so those readers always see a fully-built list, never a torn one.
    private volatile List<NavTarget> _navTargets = new();                // unified targets, rebuilt each world tick
    private volatile IReadOnlyList<RankedTarget> _rankedTargets = System.Array.Empty<RankedTarget>();
    private string? _activeTargetId;          // the cycler's current single active target (render thread)
    private CycleIndicator? _cycleIndicator;  // transient overlay indicator (render thread)
    private DateTime _nextCycleAt = DateTime.MinValue;
    private readonly POE2Radar.Overlay.Input.ControllerCycler _controllerCycler = new();
    private enum CycleAction { Next, Prev, Clear }
    // The ONLY state shared with the HTTP/API thread. Every read/iterate/mutate of _selectedIds is
    // done under _navLock (snapshot to a local, then work outside the lock). Trackers are reconciled
    // from this list on the tick thread only — mutators (in-game + API) just edit _selectedIds.
    private readonly object _navLock = new();
    private readonly List<string> _selectedIds = new();                  // selected target ids (order drives the color slot)
    private List<SelectedPath> _selectedPaths = new();                   // one route per selected target (from trackers)
    private bool _selectionCapWarned;                                    // log the "cap reached" notice once
    private readonly CampaignObjectives _campaign;
    private readonly POE2Radar.Core.Campaign.ObjectiveDirector _director = new();
    private volatile IReadOnlyList<POE2Radar.Core.Campaign.RankedObjective> _directorQueue =
        Array.Empty<POE2Radar.Core.Campaign.RankedObjective>();
    private nint _navTargetsArea = -1;                                   // AreaInstance the auto-nav was applied for
    // Per-instance nav memory: the nav selection for each AreaInstance hash, so returning to a zone
    // (e.g. after a town trip, which re-resolves a fresh AreaInstance) RESTORES what was selected
    // instead of clearing it. AreaHash is the stable per-instance id (same instance → same hash;
    // a re-rolled map → new hash → fresh auto-nav). In-session only and capped (LRU) so a long
    // session can't grow it unbounded. _selectionAreaHash is the hash _selectedIds belong to now.
    private readonly Dictionary<uint, List<string>> _zoneSelections = new();
    private readonly List<uint> _zoneOrder = new();                      // insertion order, for LRU eviction
    private uint _selectionAreaHash;
    private const int MaxRememberedZones = 64;

    // ── Collapsible "POE2GPS" navigation menu widget state (drawn always-on; persisted corner). ──
    private bool _navMenuExpanded;                                       // dropdown open? (default collapsed)
    private DateTime _nextMenuAt = DateTime.MinValue;                    // Ctrl+Alt+M menu-toggle debounce

    public void RequestShutdown() => _shutdown = true;

    public RadarApp(ProcessHandle process, MemoryReader reader, nint gameStateSlot)
    {
        _process = process;
        _reader = reader;
        _settings = RadarSettings.Load();
        ConsoleTheme.Section("POE2GPS");
        ConsoleTheme.Kv("settings", RadarSettings.FilePath);
        ConsoleTheme.Kv("entity names", $"{EntityNameResolver.Shared.Count} mappings · {ZoneGuide.Shared.Count} zones");
        _live = new Poe2Live(reader, gameStateSlot);
        // Independent reader stacks for the render + API threads (see the field declarations): each owns
        // its own MemoryReader/Poe2Live so the world walk, the render-frame reads, and the API tile scan
        // never share the non-thread-safe per-instance buffers/caches. All read the one shared handle.
        _readerRender = new MemoryReader(process);
        _liveRender = new Poe2Live(_readerRender, gameStateSlot);
        _readerApi = new MemoryReader(process);
        _liveApi = new Poe2Live(_readerApi, gameStateSlot);
        _atlas = new Poe2Atlas(reader);
        _runeforge = new Poe2Runeforge(reader);   // world-thread reader stack
        _window = OverlayWindow.Create();
        _window.SetCaptureExclusion(_settings.ExcludeFromCapture);   // stealth: hide from screen capture by default
        _appliedExcludeFromCapture = _settings.ExcludeFromCapture;
        _renderer = new OverlayRenderer(_window);
        // Clicking a legend row toggles that landmark in the path selection. Purely local UI — the
        // click lands on our own overlay window (never forwarded to the game). See UpdateClickThrough.
        _window.OnClientClick = OnOverlayClick;
        _hidden = new HiddenEntities(Path.Combine(ConfigDir, "hidden_entities.json"));
        _watched = new WatchedEntities(Path.Combine(ConfigDir, "watched_entities.json"));
        _campaign = new CampaignObjectives(Path.Combine(ConfigDir, "campaign_objectives.json"));
        _landmarkPatterns = new LandmarkPatterns(Path.Combine(ConfigDir, "landmark_patterns.json"));
        _live.CustomLandmarkMatch = TileLandmarkMatch; // surface tiles via landmark patterns + Tile rules
        _landmarkGen = _landmarkPatterns.Generation;
        _live.LandmarkClusterGap = _settings.LandmarkClusterGap;
        _appliedClusterGap = _settings.LandmarkClusterGap;
        // Unified display ruleset — single source of truth for the entity dot decision. On first run
        // (no display_rules.json) seed it from the legacy category styles + mechanics + watched rules
        // so behavior is identical; thereafter it's the authoritative, editable, ordered ruleset.
        _displayRules = new DisplayRules(Path.Combine(ConfigDir, "display_rules.json"));
        _resolveEntity = _displayRules.Resolve;
        _resolveTileDraw = p => _displayRules.ResolveTile(p, requireMatch: false);
        if (_displayRules.Count == 0)
        {
            _displayRules.Replace(DisplayRules.BuildDefault(
                _settings.Styles, _settings.ShowMonsters, _watched.All));
            Console.WriteLine($"Display rules: seeded {_displayRules.Count} from legacy config (first run).");
        }
        // One-time: fold any user landmark-tile patterns into Tile display rules (the unified system),
        // then clear the old config so it's retired and won't double-apply or re-migrate.
        if (_landmarkPatterns.All.Count > 0)
        {
            var rules = _displayRules.All.ToList();
            var seen = new HashSet<string>(
                rules.Where(r => r.Categories.Contains("Tile")).SelectMany(r => r.Match), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var lp in _landmarkPatterns.All)
            {
                if (!seen.Add(lp.Pattern)) continue;
                rules.Add(new DisplayRule
                {
                    Enabled = lp.Enabled, Name = string.IsNullOrWhiteSpace(lp.Label) ? lp.Pattern : lp.Label,
                    Categories = new() { "Tile" }, Match = new() { lp.Pattern },
                    Shape = "Diamond", Color = "#F259F2", Opacity = 1f, Size = 5f, Navigable = true,
                    Label = string.IsNullOrWhiteSpace(lp.Label) ? null : lp.Label,
                });
                added++;
            }
            if (added > 0) _displayRules.Replace(rules);
            foreach (var lp in _landmarkPatterns.All.ToList()) _landmarkPatterns.Remove(lp.Pattern);
            Console.WriteLine($"Migrated {added} landmark-tile pattern(s) into Tile display rules.");
        }
        // One-time: fold the old AutoNavPatterns list onto matching rules' Auto-path flag (a rule auto-
        // paths when one of its match terms overlaps a pattern), then retire the list. Preserves the
        // "auto-path to the expedition encounter on zone entry" default.
        if (_settings.AutoNavPatterns.Count > 0)
        {
            var rules = _displayRules.All.ToList();
            var pats = _settings.AutoNavPatterns;
            var changed = false;
            foreach (var r in rules)
            {
                if (r.Navigable) continue;
                if (r.Match.Any(m => pats.Any(p =>
                        m.Contains(p, StringComparison.OrdinalIgnoreCase) || p.Contains(m, StringComparison.OrdinalIgnoreCase))))
                { r.Navigable = true; changed = true; }
            }
            if (changed) _displayRules.Replace(rules);
            _settings.AutoNavPatterns = new(); _settings.Save();
            Console.WriteLine("Migrated auto-path patterns onto display rules' Auto-path flag.");
        }
        // One-time: seed a default rule that flags Abyss "Lightless" (Amanamu void) monsters — the
        // dangerous void-cloud mobs. Same idea as the community AmanamuVoidAlert plugin's PRIMARY detector:
        // match the monster's affix-mod ids (we read ObjectMagicProperties+Mods). Placed before the generic
        // "Monster · <rarity>" rules so it wins. Guarded by a seed flag so deleting it from the dashboard
        // sticks. NOTE: this matches on the affix-mod ids we read (+0x168); if the abyss faction mod proves
        // to live in the ModNames vector (+0x150) we don't currently read, this won't fire and we extend the
        // read — verify live via the dashboard Entities view on an Abyss monster.
        if (!_settings.AbyssRuleSeeded)
        {
            const string abyssRuleName = "Abyss Lightless (Void)";
            var rules = _displayRules.All.ToList();
            if (!rules.Any(r => string.Equals(r.Name, abyssRuleName, StringComparison.Ordinal)))
            {
                var idx = rules.FindIndex(r => r.Name.StartsWith("Monster ·", StringComparison.Ordinal));
                var abyssRule = new DisplayRule
                {
                    Name = abyssRuleName,
                    Categories = new() { "Monster" },
                    Mods = new() { "AbyssLightless", "LightlessWell", "Lightless" }, // affix-mod id terms (ANY-of)
                    Shape = "Exclamation", Color = "#B450FF", Opacity = 1f, Size = 6f, Label = "VOID",
                };
                if (idx >= 0) rules.Insert(idx, abyssRule); else rules.Add(abyssRule);
                _displayRules.Replace(rules);
                Console.WriteLine("Display rules: seeded default Abyss Lightless (Void) monster rule.");
            }
            _settings.AbyssRuleSeeded = true; _settings.Save();
        }
        // One-time (v2): apply the curated icon glyphs to the STOCK display rules. Names are matched
        // SEPARATOR-INSENSITIVELY (normalized to lowercase alphanumerics) because the stock names contain a
        // "·" whose code point didn't match a literal key in the v1 pass — silently skipping Monster·Unique
        // and the chests. Each entry only retouches a rule still on its OLD default shape, so user
        // customizations are preserved; idempotent (already-applied rules no longer match their old shape).
        if (!_settings.IconDefaultsApplied2)
        {
            static string Norm(string s)
            {
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                return sb.ToString();
            }
            var iconMap = new Dictionary<string, (string from, string to)>(StringComparer.Ordinal)
            {
                ["monsterunique"]      = ("Star", "Skull"),
                ["monstermagic"]       = ("Diamond", "Fang"),
                ["monsterrare"]        = ("Triangle", "Claw"),
                ["player"]             = ("Circle", "Person"),
                ["npc"]                = ("Plus", "Chat"),
                ["chestrare"]          = ("Square", "Chest"),
                ["chestunique"]        = ("Square", "Crown"),
                ["transition"]         = ("Diamond", "Stairs"),
                ["pointofinterest"]    = ("Circle", "MapPin"),
                ["breach"]             = ("Diamond", "Portal"),
                ["essence"]            = ("Triangle", "Flask"),
                ["expedition"]         = ("Plus", "Flag"),
                ["strongbox"]          = ("Square", "Chest"),
                ["abysslightlessvoid"] = ("Diamond", "Exclamation"),
            };
            var rules = _displayRules.All.ToList();
            var changed = 0;
            foreach (var r in rules)
                if (iconMap.TryGetValue(Norm(r.Name), out var m) && string.Equals(r.Shape, m.from, StringComparison.OrdinalIgnoreCase))
                { r.Shape = m.to; changed++; }
            if (changed > 0) _displayRules.Replace(rules);
            _settings.IconDefaultsApplied = true; _settings.IconDefaultsApplied2 = true; _settings.Save();
            Console.WriteLine($"Display rules: applied curated icon glyphs to {changed} stock rule(s).");
        }
        // One-time cleanup: the legacy "watched" defaults were seeded as Diamond, (any)-category rules placed
        // BEFORE the mechanic rules, so they shadowed them (everything drew as a diamond) — and the bare
        // "Ritual"/"Breach"/"Essence" mechanic matches with no category gate tagged the leagues' MONSTERS.
        if (!_settings.RuleCleanupV1)
        {
            static bool AnyCat(DisplayRule r) => r.Categories is null or { Count: 0 };
            var rules = _displayRules.All.ToList();
            var before = rules.Count;
            // a) Drop the stale Diamond duplicates that shadow a mechanic rule (matched by their target path).
            var dupMatches = new[] { "LeagueRitual", "Expedition2/Expedition2Encounter", "StrongBoxes", "Metadata/Shrines/" };
            rules.RemoveAll(r => string.Equals(r.Shape, "Diamond", StringComparison.OrdinalIgnoreCase)
                && AnyCat(r) && r.Match is { Count: > 0 } && r.Match.Any(m => dupMatches.Contains(m)));
            // b) Gate the broad mechanic rules to the marker (Object/Other) so they never tag monsters.
            foreach (var r in rules)
                if (AnyCat(r) && (string.Equals(r.Name, "Ritual", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(r.Name, "Breach", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(r.Name, "Essence", StringComparison.OrdinalIgnoreCase)))
                    r.Categories = new List<string> { "Object", "Other" };
            // c) Reskin the remaining navigation-POI diamonds to sensible glyphs (only if still Diamond).
            var poiIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Waypoint"] = "MapPin", ["Checkpoint"] = "Flag", ["Entrance"] = "Stairs", ["Stash"] = "Chest",
                ["Portal"] = "Portal", ["Town Portal"] = "Portal", ["Quest Chest"] = "Chest", ["Quest Object"] = "Exclamation",
                ["Quest Marker"] = "Exclamation", ["Reforging Bench"] = "Coin", ["Crafting Bench"] = "Coin", ["Abyss Crack"] = "Exclamation",
            };
            foreach (var r in rules)
                if (string.Equals(r.Shape, "Diamond", StringComparison.OrdinalIgnoreCase) && poiIcons.TryGetValue(r.Name, out var ic))
                    r.Shape = ic;
            _displayRules.Replace(rules);
            _settings.RuleCleanupV1 = true; _settings.Save();
            Console.WriteLine($"Display rules: cleanup removed {before - rules.Count} stale duplicate(s), gated mechanic rules, reskinned POIs.");
        }
        // One-time: give the non-monster mechanic/special rules a default in-game LABEL where they had none,
        // so their marker shows text (Expedition/Ritual/Breach already had labels from the legacy watched set;
        // Strongbox/Essence/Shrine/Transition/chests did not). Only fills an empty label (never overwrites).
        if (!_settings.MechanicLabelsV1)
        {
            static string Norm(string s)
            {
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                return sb.ToString();
            }
            var labelMap = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["strongbox"] = "Strongbox", ["essence"] = "Essence", ["shrine"] = "Shrine",
                ["transition"] = "Transition", ["chestunique"] = "Unique Chest", ["chestrare"] = "Rare Chest",
            };
            var rules = _displayRules.All.ToList();
            var n = 0;
            foreach (var r in rules)
                if (string.IsNullOrEmpty(r.Label) && labelMap.TryGetValue(Norm(r.Name), out var lbl)) { r.Label = lbl; n++; }
            if (n > 0) _displayRules.Replace(rules);
            _settings.MechanicLabelsV1 = true; _settings.Save();
            Console.WriteLine($"Display rules: added default labels to {n} non-monster rule(s).");
        }
        // One-time: broaden the ground-item categories from the old 4 to the full high-value set (now that
        // non-uniques actually price + draw). Only replaces the EXACT old default, so a custom set is kept.
        if (!_settings.GroundDefaultsV2)
        {
            var cur = _settings.GroundItems.Categories ?? new();
            var old = new HashSet<string>(new[] { "Uniques", "Runes", "Essences", "Currency" }, StringComparer.OrdinalIgnoreCase);
            if (cur.Count == old.Count && cur.All(old.Contains))
            {
                _settings.GroundItems.Categories = new GroundItemSettings().Categories; // the new broad default
                Console.WriteLine("Ground items: broadened category set to the full high-value default.");
            }
            _settings.GroundDefaultsV2 = true; _settings.Save();
        }
        // One-time: bump monster Magic/Rare/Unique rule sizes — the Fang/Claw/Skull glyphs are far less
        // legible than the old flat shapes at the same radar size. Only retouches a rule still on its OLD
        // default size (within a small epsilon), so a size you've customized is preserved.
        if (!_settings.IconSizesV1)
        {
            static string Norm(string s)
            {
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                return sb.ToString();
            }
            var sizeMap = new Dictionary<string, (float from, float to)>(StringComparer.Ordinal)
            {
                ["monstermagic"]  = (3.4f, 5.5f),
                ["monsterrare"]   = (5.5f, 7.5f),
                ["monsterunique"] = (6.5f, 8.0f),
            };
            var rules = _displayRules.All.ToList();
            var n = 0;
            foreach (var r in rules)
                if (sizeMap.TryGetValue(Norm(r.Name), out var m) && Math.Abs(r.Size - m.from) < 0.05f)
                { r.Size = m.to; n++; }
            if (n > 0) _displayRules.Replace(rules);
            _settings.IconSizesV1 = true; _settings.Save();
            Console.WriteLine($"Display rules: bumped {n} monster icon size(s) for glyph legibility.");
        }
        _displayRulesGen = _displayRules.Generation;
        // User-editable overlay on the baked curated landmark table (the "Landmarks" tab). Inject its
        // lookup so the landmark scan honors user edits on top of the shipped community data.
        _landmarkStore = new LandmarkStore(Path.Combine(ConfigDir, "landmarks.json"));
        _live.CuratedLookup = _landmarkStore.Lookup;
        _landmarkStoreGen = _landmarkStore.Generation;
        _modCatalog = new ModCatalog(Path.Combine(ConfigDir, "known_mods.json"));
        _seenPoiLog = new SeenPoiLog(Path.Combine(ConfigDir, "seen_pois.json"));
        _entityAtlas = new EntityAtlasLog(Path.Combine(ConfigDir, "entity_atlas.json"));
        _entityNameStore = new EntityNameStore(Path.Combine(ConfigDir, "entity_names_user.json"));
        _gearWeights = new GearWeightStore(Path.Combine(ConfigDir, "stat_weights.json"));
        ConsoleTheme.Kv("rules", $"{_hidden.Count} hidden · {_displayRules.Count} display · {_modCatalog.Count} mods");
        _api = new ApiServer(() => _state, _settings, GetNavSelection, ToggleNavTarget, ClearNavSelection,
                             _hidden, _displayRules, _landmarkStore, CurrentTilePaths, () => _modCatalog.All,
                             _campaign, () => _seenPoiLog.All, () => _entityAtlas.All, _entityNameStore,
                             GearJson, _gearWeights,
                             AtlasJson, SetAtlasSelection,
                             SetAtlasHighlight, VersionJson, _settings.ApiPort);
        try { _api.Start(); ConsoleTheme.Kv("dashboard", $"http://localhost:{_settings.ApiPort}  (F12)"); }
        catch (Exception ex) { Console.Error.WriteLine($"API server disabled: {ex.Message}"); }
        ConsoleTheme.Hotkeys();
        // Best-effort version check against GitHub (non-blocking; never fails startup). The only outbound
        // request the overlay makes beyond loopback — opt out via CheckForUpdates for zero network egress.
        if (_settings.CheckForUpdates)
        {
            _ = Task.Run(async () =>
            {
                var u = await UpdateChecker.CheckAsync();
                _update = u;
                if (u.UpdateAvailable)
                    ConsoleTheme.WarnLine($"\n*** UPDATE AVAILABLE: {u.Latest} — you have v{u.Current}. Download: {u.Url} ***\n");
                else
                    ConsoleTheme.Accent($"POE2GPS v{u.Current}" + (u.Latest != null ? " (up to date)." : " (update check unavailable)."));
            });
        }
    }

    /// <summary>API (/api/version): this build's version + the latest known on GitHub + a download URL.
    /// Lets the dashboard show an "update available" banner. Null-ish until the async check completes.</summary>
    /// <summary>API (/api/gear): the scored inventory snapshot (item stats + scores only; no identifying
    /// data). {enabled:false, items:[]} when the experimental scorer is off.</summary>
    private object GearJson()
    {
        var snap = _gearSnapshot;
        if (!_settings.EnableGearScorer || snap == null)
            return new { enabled = _settings.EnableGearScorer, items = System.Array.Empty<object>() };
        return new
        {
            enabled = true,
            items = snap.Items.OrderByDescending(i => i.Score).Select(i => new
            {
                name = i.Name, rarity = i.Rarity, identified = i.Identified, inventoryId = i.InventoryId,
                score = Math.Round(i.Score, 1), godRoll = i.IsGodRoll,
                affixes = i.Affixes.Select(a =>
                {
                    int? pctOfMax = null; int? tier = null; int? tierCount = null;
                    if (a.ModId.Length > 0 && POE2Radar.Core.Game.ModRanges.Shared.TryGet(a.ModId, out var ri))
                    {
                        tier = ri.Tier; tierCount = ri.TierCount;
                        // Match the affix's stat to a stat range; use the first stat with a real range.
                        var sr = ri.Stats.FirstOrDefault(x => a.StatIds.Contains(x.Id));
                        if (sr.Id == null) sr = ri.Stats.Count > 0 ? ri.Stats[0] : default;
                        if (sr.Id != null && sr.Max > sr.Min)
                            pctOfMax = (int)Math.Round(Math.Clamp((a.Value - sr.Min) / (sr.Max - sr.Min) * 100.0, 0, 100));
                        else if (sr.Id != null) pctOfMax = 100; // fixed roll (min==max)
                    }
                    return new { line = a.Line, statIds = a.StatIds, value = a.Value, weight = a.Weight, points = Math.Round(a.Points, 2), pctOfMax, tier, tierCount };
                }),
            }),
        };
    }

    private object VersionJson()
    {
        var u = _update;
        return new
        {
            current = u?.Current ?? UpdateChecker.Current,
            latest = u?.Latest,
            updateAvailable = u?.UpdateAvailable ?? false,
            url = u?.Url ?? UpdateChecker.ReleasesPage,
        };
    }

    public void Run()
    {
        _gameHwnd = OverlayNative.FindWindowForProcess(_process.ProcessId);
        // The heavy world-rate walk runs on its OWN thread (Phase 3); the render loop below only does
        // fast per-frame reads + draw, so a slow world pass (big pack, zone load) never hitches frames.
        _worldThread = new Thread(WorldLoop) { IsBackground = true, Name = "POE2Radar.World" };
        _worldThread.Start();
        timeBeginPeriod(1);   // 1 ms timer resolution → the frame pacer below can actually hit FpsCap
        try
        {
            var frameSw = System.Diagnostics.Stopwatch.StartNew();
            var fpsSw = System.Diagnostics.Stopwatch.StartNew();
            var fpsFrames = 0;
            while (!_shutdown)
            {
                frameSw.Restart();
                if (_gameHwnd == 0) _gameHwnd = OverlayNative.FindWindowForProcess(_process.ProcessId);
                if (_gameHwnd != 0) _window.TrackGameWindow(_gameHwnd);
                if (!_window.PumpMessages()) break;
                Tick();

                // Effective render FPS over a rolling ~500 ms window (actual loop iterations/sec, after
                // pacing) — exposed via /state so we can verify we're truly hitting FpsCap, not just asking.
                if (++fpsFrames >= 1 && fpsSw.ElapsedMilliseconds >= 500)
                {
                    _fps = (float)(fpsFrames * 1000.0 / fpsSw.Elapsed.TotalMilliseconds);
                    fpsFrames = 0; fpsSw.Restart();
                }
                // Pace to the configured cap against ELAPSED time (incl. the Tick render cost), not a fixed
                // sleep on top of it — otherwise effective fps = 1000/(budget+renderMs), always below the cap.
                // FpsCap <= 0 means "auto-match the monitor the game is on" (re-detected ~1/s; logged on change).
                // Read live so dashboard edits apply immediately.
                int hz;
                if (_settings.FpsCap > 0) hz = Math.Clamp(_settings.FpsCap, 15, 360);
                else
                {
                    var nowHz = DateTime.UtcNow;
                    if (nowHz >= _nextHzCheckUtc)
                    {
                        _nextHzCheckUtc = nowHz.AddSeconds(1);
                        var det = DetectGameMonitorHz(_gameHwnd);
                        if (det > 0)
                        {
                            var clamped = Math.Clamp(det, 30, 360);
                            if (clamped != _autoHz || !_autoHzLogged)
                            {
                                _autoHz = clamped; _autoHzLogged = true;
                                Console.WriteLine($"Auto FPS cap: {_autoHz} Hz (game monitor refresh).");
                            }
                        }
                    }
                    hz = _autoHz;
                }
                var budgetMs = 1000.0 / hz;
                var remaining = budgetMs - frameSw.Elapsed.TotalMilliseconds;
                // Coarse-sleep most of the remainder (1 ms accurate now), then spin the last ~1.5 ms for a
                // tight, low-jitter frame interval — what high-refresh tracking needs.
                if (remaining > 2.0) Thread.Sleep((int)(remaining - 1.5));
                while (frameSw.Elapsed.TotalMilliseconds < budgetMs) Thread.SpinWait(64);
            }
        }
        finally { timeEndPeriod(1); }
    }

    /// <summary>The background world loop (~<see cref="WorldHz"/> Hz, adaptive): resolve the chain on its
    /// own reader stack and run <see cref="WorldTick"/>, then sleep the remainder of the frame budget. All
    /// heavy reads live here so the render thread is never blocked. Never throws out (a read failure mid
    /// zone-load just publishes nothing this pass).</summary>
    private void WorldLoop()
    {
        var sw = new System.Diagnostics.Stopwatch();
        var budgetMs = 1000 / WorldHz;
        while (!_shutdown)
        {
            sw.Restart();
            try
            {
                if (_live.TryResolve(out var inGameState, out var areaInstance, out var localPlayer))
                    WorldTick(inGameState, areaInstance, localPlayer);
                else
                    PublishEmptyWorld();
            }
            catch (Exception ex) { Console.Error.WriteLine($"World tick error: {ex.Message}"); }
            _worldMs = (float)sw.Elapsed.TotalMilliseconds;
            Thread.Sleep(Math.Max(1, budgetMs - (int)sw.ElapsedMilliseconds));
        }
    }

    /// <summary>Not in game: publish an empty world snapshot + closed atlas so the render thread draws no
    /// stale entities/route (the selection itself is left intact so a loading screen keeps it).</summary>
    private void PublishEmptyWorld()
    {
        if (!ReferenceEquals(_world, WorldSnapshot.Empty)) _world = WorldSnapshot.Empty;
        if (!ReferenceEquals(_atlasRender, AtlasRender.Closed))
        {
            _atlasRender = AtlasRender.Closed;
            _builtAtlasOnce = false; _lastAtlasSig = 0;
        }
        if (!ReferenceEquals(_runeRender, RuneRender.Closed)) _runeRender = RuneRender.Closed;
    }

    /// <summary>Read the open "Runeshape Combinations" panel (cheap when closed) and publish a priced label
    /// per visible reward (stack-total = unit × count, in Exalted, colored by value tier) for the renderer
    /// to draw on each row. Screen rects are scaled for the current game-window size. World thread.</summary>
    private void UpdateRuneforge(nint inGameState)
    {
        var cfg = _settings.GroundItems;
        if (!cfg.Enabled)
        {
            if (!ReferenceEquals(_runeRender, RuneRender.Closed)) _runeRender = RuneRender.Closed;
            return;
        }
        var rewards = _runeforge.ReadRewards(inGameState, _window.Width, _window.Height);
        if (!_runeforge.PanelOpen || rewards.Count == 0)
        {
            if (!ReferenceEquals(_runeRender, RuneRender.Closed)) _runeRender = RuneRender.Closed;
            return;
        }
        var labels = new List<RuneLabel>(rewards.Count);
        foreach (var r in rewards)
        {
            var text = r.Count > 1 ? $"{r.Name} ×{r.Count}" : r.Name;
            labels.Add(new RuneLabel(r.X, r.Y, r.W, r.H, text, 0xFFE6C84Du));
        }
        _runeRender = new RuneRender(labels.Count > 0, labels);
    }

    /// <summary>Read the open ritual tribute shop (cheap when closed — gated on a shop-signature text element)
    /// and publish a priced value label per offered reward for the renderer to draw on each tile. Uniques
    /// price by 2D-art basename, everything else by base-type name (same rule as ground items). Shares the
    /// ground-item pricing toggle + league + value floor. World thread.</summary>
    private void UpdateRitualRewards(nint inGameState)
    {
        var cfg = _settings.GroundItems;
        if (!cfg.Enabled)
        {
            if (!ReferenceEquals(_ritualRender, RitualRender.Closed)) _ritualRender = RitualRender.Closed;
            return;
        }
        var rewards = _live.ReadRitualRewards(inGameState, _window.Width, _window.Height);
        if (rewards.Count == 0)
        {
            if (!ReferenceEquals(_ritualRender, RitualRender.Closed)) _ritualRender = RitualRender.Closed;
            return;
        }
        var labels = new List<RitualLabel>(rewards.Count);
        foreach (var r in rewards)
        {
            var name = r.Name is { Length: > 0 } ? r.Name : (r.Art ?? "");
            if (name.Length == 0) continue;
            labels.Add(new RitualLabel(r.X, r.Y, r.W, r.H, name, 0xFFE6C84Du, false));
        }
        _ritualRender = new RitualRender(labels.Count > 0, labels);
    }

    /// <summary>Resolve every runeshape-monolith device in the area (the persistent Expedition2Encounter
    /// POI entities, already in <see cref="_entities"/>), read its hole count + anchor rune off the device→
    /// station chain, compute the rewards it will offer (<see cref="RuneMonolithCatalog"/>, level-gated),
    /// price each via the PriceBook, and publish a value-coloured <see cref="MonolithRender"/> bundle. Works
    /// area-wide and BEFORE the panel is opened (the station persists out of the network bubble).</summary>
    private void UpdateMonoliths(nint areaInstance, int areaLevel, uint areaHash)
    {
        var cfg = _settings.Monoliths;
        if (!cfg.Enabled || !_monoCatalog.IsLoaded)
        {
            if (_monoRender.Markers.Count > 0) _monoRender = MonolithRender.Empty;
            return;
        }

        var markers = new List<MonolithMarker>();
        foreach (var e in _entities)
        {
            if (e.Metadata.IndexOf("Expedition2Encounter", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var m = _live.ReadMonolith(e.Address);
            if (!m.Resolved) continue;
            if (cfg.HideCollected && m.Collected) continue;

            var offers = _monoCatalog.Offers(m.AnchorIdx, m.AnchorPos, m.HoleCount, m.IsUnique, areaLevel);
            var rewards = new List<MonolithReward>(offers.Count);
            foreach (var o in offers)
                rewards.Add(new MonolithReward(o.Name.Length > 0 ? o.Name : o.Description, o.Count, 0, o.Size, o.Runes));

            var anchor = m.IsUnique ? "Unique" : m.AnchorIdx >= 0 ? _monoCatalog.RuneName(m.AnchorIdx) : "?";
            var headline = rewards.Count > 0 ? rewards[0].Name : anchor;
            markers.Add(new MonolithMarker(
                e.Grid, m.HoleCount, m.IsUnique, m.Collected, anchor, 0, headline, 0xFFE6C84Du, rewards));
        }
        _monoRender = new MonolithRender(areaHash, markers);
    }

    /// <summary>One RENDER frame (render thread): fast per-frame reads on the render reader stack
    /// (player/vitals/camera/map + HP-bar live pos), then draw from the lock-free world
    /// snapshot. The heavy walk is on <see cref="WorldLoop"/>.</summary>
    private void Tick()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();   // no per-frame Stopwatch allocation
        HandleHotkeys();

        // Live-apply the capture-exclusion toggle (Settings tab / config). Only fires the Win32 call on change.
        if (_settings.ExcludeFromCapture != _appliedExcludeFromCapture)
        {
            _appliedExcludeFromCapture = _settings.ExcludeFromCapture;
            _window.SetCaptureExclusion(_appliedExcludeFromCapture);
        }

        var inGame = _liveRender.TryResolve(out var inGameState, out var areaInstance, out var localPlayer);
        var player = NumVec2.Zero;
        POE2Radar.Core.Game.Vector3? playerWorld = null;   // live player feet (incl. Z) for the world-ground route anchor
        var map = default(Poe2Live.MapUi);
        // Atlas routes/markers re-read per frame (positions from node elements) so they track pan at full FPS.
        NumVec2? atlasStart = null, atlasEnd = null, atlasCurrent = null;
        List<NumVec2>? atlasRoute = null;
        List<AtlasRouteInfo>? atlasAutoRoutes = null;

        // One lock-free read each of the two published snapshots — everything drawn this frame comes from
        // these two + the live render-rate reads below.
        var snap = _world;
        var ar = _atlasRender;
        var rr = _runeRender;
        var rit = _ritualRender;
        var mr = _monoRender;

        if (inGame)
        {
            _areaInstanceForApi = areaInstance; // for /api/tiles (read by _liveApi on the HTTP thread)
            _inGameStateForApi = inGameState;   // for /api/atlas + F10 route pick
            _areaHash = _liveRender.AreaHash(areaInstance);

            player = _liveRender.PlayerGrid(localPlayer) ?? NumVec2.Zero;
            playerWorld = _liveRender.PlayerWorld(localPlayer);   // same Render read PlayerGrid uses; live each frame
            map = _liveRender.ReadMap(inGameState, areaInstance);
            _cameraMatrix = _liveRender.CameraMatrix(inGameState);
            if (_live.PlayerVitals(localPlayer) is { } v) { _hpPct = v.HpPct; _manaPct = v.ManaPct; _esPct = v.EsPct; }

            // Refresh each HP-bar mob's live position + HP from the world tick's spec (which captured the
            // mob's Render/Life component addresses) using the RENDER reader — so bars track moving mobs
            // smoothly with no shared cache. Cheap: two tiny reads per bar, only the ~dozens of bar mobs.
            _hpFrame.Clear();
            foreach (var spec in snap.HpSpecs)
            {
                if (!_liveRender.TryLiveBarAt(spec.Render, spec.Life, out var w, out var cur, out var max) || max <= 0 || cur <= 0) continue;
                _hpFrame.Add(new HpBarTarget(w, Math.Clamp((float)cur / max, 0f, 1f), spec.Width, spec.Fill, spec.BorderWidth, spec.Border));
            }

            // Ground-item labels: re-read each priced item's live world position THIS frame (dropped items
            // bob), so the renderer projects a current position — the same per-frame reposition that keeps
            // HP bars smooth. life arg 0 → world-pos only.
            _itemFrame.Clear();
            foreach (var s in snap.ItemLabels)
            {
                if (!_liveRender.TryLiveBarAt(s.Render, 0, out var w, out _, out _)) continue;
                _itemFrame.Add(new ItemLabel(w, s.Name, s.Value, s.Highlight, s.ShowName));
            }

            // Atlas marks/routes: re-read each node's live RelativePos this frame so the rings + route lines
            // track the atlas pan at full FPS (the world walk only refreshes baked positions ~30 Hz). Cheap —
            // only the handful of DRAWN marks + route points, one atomic 8-byte read each; a stale/closed node
            // falls back to its baked position (marks) or drops out (routes).
            _atlasMarkFrame.Clear();
            if (ar.Open)
            {
                foreach (var m in ar.Marks)
                    _atlasMarkFrame.Add(m.Element != 0 && _liveRender.TryRelPos(m.Element, out var mx, out var my) ? m with { X = mx, Y = my } : m);

                // Fresh live pos when the read validates, else the world-walk's baked pos — never a garbage
                // coordinate (which would streak route lines off-screen). Points stay contiguous (no dropping).
                NumVec2 Pt(AtlasPoint p) => p.El != 0 && _liveRender.TryRelPos(p.El, out var rx, out var ry) ? new NumVec2(rx, ry) : new NumVec2(p.Bx, p.By);
                atlasStart = ar.Start is { } sp ? Pt(sp) : (NumVec2?)null;
                atlasEnd = ar.End is { } ep ? Pt(ep) : (NumVec2?)null;
                atlasCurrent = ar.Current is { } cp ? Pt(cp) : (NumVec2?)null;
                if (ar.Route is { Count: >= 2 })
                {
                    var rl = new List<NumVec2>(ar.Route.Count);
                    foreach (var p in ar.Route) rl.Add(Pt(p));
                    atlasRoute = rl;
                }
                if (ar.AutoRoutes is { Count: > 0 })
                {
                    atlasAutoRoutes = new List<AtlasRouteInfo>(ar.AutoRoutes.Count);
                    foreach (var spec in ar.AutoRoutes)
                    {
                        var pl = new List<NumVec2>(spec.Points.Count);
                        foreach (var p in spec.Points) pl.Add(Pt(p));
                        if (pl.Count >= 2) atlasAutoRoutes.Add(new AtlasRouteInfo(pl, spec.Color, spec.Hops));
                    }
                }
            }
        }
        else { if (_hpFrame.Count > 0) _hpFrame.Clear(); if (_itemFrame.Count > 0) _itemFrame.Clear(); if (_atlasMarkFrame.Count > 0) _atlasMarkFrame.Clear(); }

        // Zone-load guard: the world snapshot lags the live chain by up to one world pass, so right after a
        // zone change its entities/terrain/route still belong to the PREVIOUS area. Only draw them once the
        // snapshot's area hash matches the live one; otherwise draw none this frame (player blip + map still
        // draw). The API still serves the latest snapshot regardless (no visual artifact there).
        var worldFresh = inGame && snap.InGame && snap.AreaHash == _areaHash;
        var entities = worldFresh ? snap.Entities : Array.Empty<Poe2Live.EntityDot>();
        var landmarks = worldFresh ? snap.Landmarks : Array.Empty<Poe2Live.Landmark>();
        var terrain = worldFresh ? snap.Terrain : null;
        var selectedPaths = worldFresh ? snap.SelectedPaths : Array.Empty<SelectedPath>();
        var legend = worldFresh ? snap.Legend : (IReadOnlyList<LegendEntry>)Array.Empty<LegendEntry>();
        var hpTargets = worldFresh ? (IReadOnlyList<HpBarTarget>)_hpFrame : Array.Empty<HpBarTarget>();
        var itemLabels = worldFresh ? (IReadOnlyList<ItemLabel>)_itemFrame : Array.Empty<ItemLabel>();
        // Monoliths are world-space (grid) → gate on the same zone-load guard, and only when the bundle's
        // own area hash matches the live area (the bundle is published independently of the world snapshot).
        var monoliths = worldFresh && mr.AreaHash == _areaHash
            ? mr.Markers : (IReadOnlyList<MonolithMarker>)Array.Empty<MonolithMarker>();

        _state = new RadarState(inGame, snap.AreaHash, snap.AreaLevel, map.IsVisible, map.Zoom, player,
            snap.Entities, snap.Landmarks, _hpPct, _manaPct, _esPct,
            snap.AreaCode, "", snap.CharLevel, _worldMs, _renderMs, mr.Markers, _directorQueue, _fps);

        var realActive = _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd;
        // "Always show" draws the overlay even when PoE2 isn't focused (for dashboard calibration).
        var drawActive = realActive || _settings.AlwaysShowOverlay;
        var atlasProj = AtlasProjection(); // resolution-correct (auto from window height) or manual calib
        var ctx = new RenderContext(
            InGame: inGame,
            Active: drawActive,
            WindowWidth: _window.Width,
            WindowHeight: _window.Height,
            PlayerGrid: player,
            PlayerWorld: playerWorld,
            Map: map,
            Entities: entities,
            Landmarks: landmarks,
            AreaHash: _areaHash,
            Terrain: terrain,
            ScaleMul: _settings.ScaleMul,
            OffsetX: _settings.OffX,
            OffsetY: _settings.OffY,
            HpPct: _hpPct,
            ManaPct: _manaPct,
            EsPct: _esPct,
            AreaCode: snap.AreaCode,
            CharLevel: snap.CharLevel,
            CameraMatrix: _cameraMatrix,
            CycleIndicator: (_cycleIndicator is { } ci && DateTime.UtcNow < ci.Expiry) ? ci : null,
            HideJunk: _settings.HideJunk,
            ShowPath: _settings.ShowPath,
            UseCuratedLandmarks: _settings.UseCuratedLandmarks,
            ShowMonsters: _settings.ShowMonsters,
            ShowTerrain: _settings.ShowTerrain,
            ShowPlayerBlip: _settings.ShowPlayerBlip,
            HpBarNormal: _settings.HpBarNormal,
            HpBarMagic: _settings.HpBarMagic,
            HpBarRare: _settings.HpBarRare,
            HpBarUnique: _settings.HpBarUnique,
            SelectedPaths: selectedPaths,
            Legend: legend,
            NavMenuExpanded: _navMenuExpanded,
            NavMenuCorner: _settings.NavMenuCorner,
            Styles: _settings.Styles,
            HpBars: _settings.HpBars,
            HpBarTargets: hpTargets,
            TerrainStyle: _settings.Terrain,
            ItemLabels: itemLabels,
            Resolve: _resolveEntity,
            ResolveTile: _resolveTileDraw,
            AtlasOpen: ar.Open,
            AtlasNodes: _atlasMarkFrame,   // marks with per-frame-fresh relPos (smooth pan), not the baked world-walk positions
            // Projection: derived live from the window height (UIscale = winH/1600) × live zoom. relPos is
            // read live so pan is already handled; the zoom term is folded into the scale. atlasProj is the
            // 8-coeff homography layout {h0..h7}. This is what makes non-1080p resolutions line up.
            AtlasScale: (float)atlasProj[0],
            AtlasScaleY: (float)atlasProj[4],
            AtlasOffX: (float)atlasProj[2],
            AtlasOffY: (float)atlasProj[5],
            AtlasShearX: (float)atlasProj[1],
            AtlasShearY: (float)atlasProj[3],
            AtlasPersX: (float)atlasProj[6],
            AtlasPersY: (float)atlasProj[7],
            // F10 route: START/END markers + the graph path between them (from the atlas render bundle).
            AtlasStart: (ar.Open && _settings.AtlasShowRoute) ? atlasStart : null,
            AtlasEnd: (ar.Open && _settings.AtlasShowRoute) ? atlasEnd : null,
            AtlasRoute: (ar.Open && _settings.AtlasShowRoute) ? atlasRoute : null,
            // Auto-route from the current node to tracked tiles + the "you are here" marker (improvement 1).
            AtlasCurrent: (ar.Open && _settings.AtlasShowRoute) ? atlasCurrent : null,
            AtlasAutoRoutes: (ar.Open && _settings.AtlasShowRoute && _settings.AtlasAutoRoute) ? atlasAutoRoutes : null,
            AtlasBiomeBorder: _settings.AtlasShowBiomeBorder,
            // Rune-crafting reward prices (screen-space; only when the panel is open).
            RuneLabels: rr.Open ? rr.Labels : null,
            // Ritual tribute-shop reward prices (screen-space; only when the shop is open).
            RitualRewards: rit.Open ? rit.Labels : null,
            // Runeshape monoliths: value-coloured map markers + nearby reward panel (world-space).
            Monoliths: monoliths,
            ShowMonolithPanel: _settings.Monoliths.ShowPanel);
        // The overlay is only visible while PoE2 is foreground (Render draws nothing otherwise). Skip
        // the whole draw + UpdateLayeredWindow blit when unfocused — but render once on the focus-loss
        // transition so the last visible frame is cleared rather than left frozen on screen.
        if (ctx.Active || _overlayHadContent)
        {
            _renderer.Render(ctx);
            _overlayHadContent = ctx.Active;
        }

        // Make the overlay grab clicks only while the cursor is over a clickable legend row;
        // otherwise stay click-through so the game receives the clicks. Runs after Render so
        // LegendRowRects reflects the frame just drawn. Gate on REAL focus (never grab clicks when
        // PoE2 isn't foreground, even if "always show overlay" is keeping it drawn).
        UpdateClickThrough(realActive);
        _renderMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>
    /// The world-rate pass (~30 Hz), run on the dedicated <see cref="WorldLoop"/> thread: the heavy
    /// entity/terrain/landmark walk + mod catalog + HP-bar specs + item labels + atlas update +
    /// nav-target/route maintenance, on the world reader stack (<see cref="_live"/>). Publishes an
    /// immutable <see cref="WorldSnapshot"/> at the end for the render thread to consume lock-free.
    /// </summary>
    private void WorldTick(nint inGameState, nint areaInstance, nint localPlayer)
    {
        // AreaInstance is a fresh object per area — use its address to invalidate per-area caches.
        if (areaInstance != _lastAreaInstance) { _terrain = null; _lastAreaInstance = areaInstance; }
        var areaHash = _live.AreaHash(areaInstance);
        var areaLevel = _live.AreaLevel(areaInstance);
        var areaCode = _live.AreaCode(areaInstance);
        var player = _live.PlayerGrid(localPlayer) ?? NumVec2.Zero;
        _worldPlayer = player;   // for off-thread replans (EnqueueReplan)

        // Tick the player's vitals on THIS (world) reader too — not for the flask (that's on the render
        // thread's _liveRender), but for the side effect: it self-heals _live's Health-offset (drift) which
        // backs the monster HP reads in Entities()/ReadHp. Pre-split the one _live instance did both, so the
        // heal benefited monster bars; with split readers, _live must heal independently. Result discarded.
        _ = _live.PlayerVitals(localPlayer);
        _charLevel = _live.PlayerLevel(localPlayer);   // changes ~never; 30 Hz is plenty
        _terrain ??= _live.Terrain(areaInstance);
        _entities = _live.Entities(areaInstance);
        // Atlas census: catalog EVERY distinct entity for naming/coverage. Runs on the PRE-cull list
        // (before the local-player + user-hidden RemoveAll below) so hiding a dot doesn't erase it from
        // the name database. AtlasCensus skips Player + JunkFilter noise.
        _entityAtlas.Observe(_entities, areaCode);

        // God-Roll Detector (experimental, default OFF): when enabled, score the inventory on a SLOW
        // cadence (~every 30 world ticks ≈ 1 Hz — inventory changes slowly). When off, read nothing.
        if (_settings.EnableGearScorer)
        {
            if (_gearTickCounter++ % 30 == 0)
            {
                var weights = _gearWeights.Snapshot();
                var inv = _live.ReadInventory(areaInstance);
                var scored = new List<ScoredItem>(inv.Count);
                foreach (var it in inv)
                {
                    var affixes = new List<Affix>(it.Affixes.Count);
                    foreach (var ra in it.Affixes)
                    {
                        var line = string.Join("; ", ItemModTranslator.Shared.RenderMod(ra.ModId, ra.Values));
                        var ids = ItemModTranslator.Shared.StatIdsFor(ra.ModId) ?? System.Array.Empty<string>();
                        var val = ra.Values.Count > 0 ? ra.Values.Max() : 0;
                        affixes.Add(new Affix(line, ids, val, ra.ModId));
                    }
                    var gs = GearScorer.Score(affixes, weights);
                    scored.Add(new ScoredItem(it.Name, it.Rarity, it.Identified, it.InventoryId, gs.Score, gs.IsGodRoll, gs.Affixes));
                }
                _gearSnapshot = new GearSnapshot(scored);
            }
        }
        else if (_gearSnapshot != null) { _gearSnapshot = null; }
        // Drop the local player's own entity — it lives in the AwakeEntities map like any
        // other Player, but the dedicated center blip already represents "you" (gated by
        // ShowPlayerBlip). Without this, a Player-category dot renders at map-center even with
        // the blip off. Filtering here (not the renderer) keeps the nav builder and HTTP API
        // consistent, and still leaves party members visible as Player dots.
        // Drop user-hidden entities + the local player's own entity in ONE in-place pass (the list is a
        // fresh List from Entities() and isn't published yet) — so the renderer, nav-target builder, and the
        // published RadarState (HTTP API) all see the same filtered list, without copying it twice.
        var culling = _hidden.Count > 0;
        if (localPlayer != 0 || culling)
            _entities.RemoveAll(e => e.Address == localPlayer || (culling && _hidden.IsHidden(e.Metadata)));
        // Accumulate any newly-seen monster mod ids into the persistent catalog (debounced write)
        // so the dashboard rule editor can offer them and they survive restarts / new content.
        _modCatalog.Observe(_entities);
        // (SeenPoiLog.Observe is called below, AFTER _landmarks is refreshed this tick — see ~the
        //  _live.Landmarks(...) assignment — so it logs THIS tick's landmarks, not last tick's.)
        // If the user edited the custom landmark patterns, drop the cached per-area scan so it
        // rebuilds with the new patterns this tick (otherwise it only refreshes on zone change).
        if (_landmarkPatterns.Generation != _landmarkGen)
        {
            _landmarkGen = _landmarkPatterns.Generation;
            _live.InvalidateLandmarks();
        }
        // A changed display ruleset can add/remove "Tile" rules that surface tiles — rebuild.
        if (_displayRules.Generation != _displayRulesGen)
        {
            _displayRulesGen = _displayRules.Generation;
            _live.InvalidateLandmarks();
        }
        // Curated-landmark edits (Landmarks tab) change what surfaces + the labels — rebuild.
        if (_landmarkStore.Generation != _landmarkStoreGen)
        {
            _landmarkStoreGen = _landmarkStore.Generation;
            _live.InvalidateLandmarks();
        }
        // Live-apply a changed cluster radius (dashboard/config edit) the same way.
        if (_settings.LandmarkClusterGap != _appliedClusterGap)
        {
            _appliedClusterGap = _settings.LandmarkClusterGap;
            _live.LandmarkClusterGap = _appliedClusterGap;
            _live.InvalidateLandmarks();
        }
        _landmarks = _live.Landmarks(areaInstance); // cached per area in Poe2Live

        // Accumulate notable POIs/landmarks seen this zone into the catalog-candidate log (debounced).
        // Must run AFTER _landmarks is refreshed above so it sees THIS tick's landmarks (on zone entry
        // the pre-refresh list still held the prior zone's tiles).
        _seenPoiLog.Observe(_entities, _landmarks, areaCode);

        // Decide which mobs get an HP bar + their style ONCE here (rule resolve + colour parse) —
        // the per-render-frame path then only re-reads position/HP for this small set. Returns a fresh
        // immutable list so the render thread can read it lock-free off the published snapshot.
        var hpSpecs = BuildHpSpecs();

        // Name labels for already-named ground drops (read-only; no economy lookups).
        var itemLabels = BuildItemLabels();

        // Atlas F10 route — ReadNodes is cheap when the atlas is closed (it gates on the atlas
        // panel's visible bit before any whole-tree scan), so this is safe each world tick. Publishes
        // its own _atlasRender bundle.
        UpdateAtlas(inGameState);

        // Rune-crafting reward prices — cheap when the panel is closed (the fingerprint walk bails at the
        // visible-gate step). Publishes its own _runeRender bundle.
        UpdateRuneforge(inGameState);
        UpdateRitualRewards(inGameState);

        // Runeshape monolith rewards — resolve each in-world monolith device + price its offered rewards
        // (area-wide, before the panel is opened). Publishes its own _monoRender bundle.
        UpdateMonoliths(areaInstance, areaLevel, areaHash);


        // Rebuild the unified navigation-target list (tiles + entity POIs) for this tick.
        _navTargets = BuildNavTargets(player);
        // Publish the priority-then-distance ranked target list for the Quick-Target Cycler (read-only;
        // computed world-side where the catalog + entities live). Only when a cycler input is enabled.
        _rankedTargets = (_settings.EnableTargetHotkeys || _settings.EnableControllerCycle)
            ? BuildRankedTargets(player) : System.Array.Empty<RankedTarget>();

        // On a zone change: drop the (now-stale) selection, then apply the persistent
        // auto-nav patterns against the new zone's targets. Keyed off the AreaInstance
        // address (a fresh object per area), same signal the per-area caches use.
        if (areaInstance != _navTargetsArea)
        {
            _navTargetsArea = areaInstance;
            OnAreaChanged(areaHash);
        }

        // Auto-deselect entity targets the game has marked complete (e.g. a looted expedition):
        // they're already gone from the map + nav-target list, but the still-present (faded)
        // entity would otherwise keep resolving, so the route would keep pathing to it.
        PruneCompletedTargets();

        // Objective Director: when enabled, rank the zone's catalog objectives and (if the director
        // owns the selection) route to the top one. Read-only — only edits _selectedIds.
        if (_settings.EnableDirector) DirectorReconcile(player);

        // Per-tick route maintenance (draw-only, NO A* on this thread). For each selected
        // target: cheaply advance its cursor; fire a BACKGROUND replan only on a real trigger.
        // Then drain finished routes and rebuild _selectedPaths from the trackers' cursors.
        MaintainRoutes(player);

        // Selection snapshot + legend are render inputs that change only with the selection /
        // nav-target list — rebuild them here (30 Hz) rather than every render frame.
        _selectedSnapshot = SnapshotSelection();
        _legend = BuildLegend(_selectedSnapshot);

        // Publish the whole immutable world snapshot atomically for the render thread.
        _world = new WorldSnapshot(true, areaHash, areaLevel, areaCode, _charLevel,
            _entities, _landmarks, _terrain, hpSpecs, itemLabels, _selectedPaths, _legend, _selectedSnapshot);
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

    /// <summary>Decide which monsters get an HP bar and precompute each bar's style (width + packed
    /// fill/border colours) at WORLD rate. This is the work that used to run per entity per render frame in
    /// the renderer (rarity gate + rule resolve + colour parse); doing it once per world tick — only for
    /// mobs with a live HP pool — leaves the render-frame path to just re-read position/HP and draw, which
    /// is what keeps 50–100 bars smooth without re-resolving thousands of entities every frame.</summary>
    private List<HpBarSpec> BuildHpSpecs()
    {
        var specs = new List<HpBarSpec>();
        var hb = _settings.HpBars;
        foreach (var e in _entities)
        {
            if (!e.IsAlive || e.HpMax <= 0) continue;                 // needs a live HP pool
            var on = e.Rarity switch                                   // per-rarity master toggle (Settings)
            {
                Poe2Live.Rarity.Normal => _settings.HpBarNormal,
                Poe2Live.Rarity.Magic  => _settings.HpBarMagic,
                Poe2Live.Rarity.Rare   => _settings.HpBarRare,
                Poe2Live.Rarity.Unique => _settings.HpBarUnique,
                _                      => false,
            };
            if (!on) continue;
            var rule = _displayRules.Resolve(e);
            if (rule is null || rule.Hide) continue;                   // no bars over hidden mobs
            var (bw, fillHex, borderW, borderHex) = e.Rarity switch    // geometry per rarity; fill = dot colour
            {
                Poe2Live.Rarity.Normal => (hb.WidthNormal, rule.Color, hb.BorderNormal, hb.BorderColorNormal),
                Poe2Live.Rarity.Magic  => (hb.WidthMagic,  rule.Color, hb.BorderMagic,  hb.BorderColorMagic),
                Poe2Live.Rarity.Rare   => (hb.WidthRare,   rule.Color, hb.BorderRare,   hb.BorderColorRare),
                Poe2Live.Rarity.Unique => (hb.WidthUnique, rule.Color, hb.BorderUnique, hb.BorderColorUnique),
                _                      => (0f, "#FFFFFF", 0f, "#FFFFFF"),
            };
            if (bw <= 0f) continue;
            // Capture the mob's Render/Life component addresses (resolved by the Entities() walk just now,
            // on THIS world reader) so the render thread can read live pos/HP off its own reader stack.
            if (!_live.TryBarComponents(e.Address, out var render, out var life)) continue;
            specs.Add(new HpBarSpec(e.Address, render, life, bw, PackColor(fillHex), borderW, PackColor(borderHex)));
        }
        return specs;
    }

    /// <summary>
    /// Build the priced ground-item label set (world rate): for each dropped UNIQUE (its art basename
    /// read by Poe2Live), look up the name + Exalted value in the PriceBook and emit a label at the item's
    /// world position. The label shows the resolved unique name (so UNIDENTIFIED uniques reveal what they
    /// are) + value, and flags Highlight when the value clears the configured threshold (→ border). Gated
    /// by the GroundItems setting; cheap (a dictionary lookup per drop, no memory reads here).
    /// </summary>
    private List<ItemLabelSpec> BuildItemLabels()
    {
        var labels = new List<ItemLabelSpec>();
        var cfg = _settings.GroundItems;
        if (!cfg.Enabled) return labels;
        foreach (var e in _entities)
        {
            if (e.ItemName is not { Length: > 0 } name) continue;   // name comes from game memory only
            if (!_live.TryBarComponents(e.Address, out var render, out _)) continue;
            labels.Add(new ItemLabelSpec(render, name, "", false, ShowName: true));
        }
        return labels;
    }

    /// <summary>Parse a "#RRGGBB" hex colour to packed 0xFFRRGGBB once (opacity = 1, matching the old
    /// per-frame ParseColor(hex, 1f) for HP bars). Falls back to opaque white on a malformed string.</summary>
    private static uint PackColor(string hex)
    {
        if (hex is { Length: >= 7 } && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        return 0xFFFFFFFFu;
    }

    /// <summary>Poll overlay hotkeys: F9 quit, F12 dashboard, F6/F7 path targets.
    /// Map calibration is web-config-only (no in-game keys, to avoid accidental presses).</summary>
    private void HandleHotkeys()
    {
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

        // Atlas tile inspector: F10 = dump the tile under the cursor (map/content/biome/flags) as an
        // on-atlas tooltip so you can see what to set as a web-UI filter.
        if (Down(0x79) && DateTime.UtcNow >= _nextInspectAt) // F10
        {
            _nextInspectAt = DateTime.UtcNow.AddMilliseconds(250);
            AtlasRoutePick();
        }

        // Quick-Target Cycler (keyboard): Ctrl+Alt+ ] next, [ prev, 1-9 jump-to-slot, 0 clear.
        // Foreground-gated + debounced. Reads keys to change the overlay's active target — sends nothing
        // to the game. Cycle keys are [ ] (0xDB/0xDD), NOT arrows (Ctrl+Alt+Arrow rotates Intel displays).
        if (_settings.EnableTargetHotkeys && DateTime.UtcNow >= _nextCycleAt
            && _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd
            && Down(0x11) && Down(0x12))   // Ctrl + Alt held
        {
            var fired = true;
            if (Down(0xDD)) Cycle(CycleAction.Next);          // ]
            else if (Down(0xDB)) Cycle(CycleAction.Prev);     // [
            else if (Down(0x30)) Cycle(CycleAction.Clear);    // 0
            else
            {
                fired = false;
                for (var n = 1; n <= 9; n++)
                    if (Down(0x30 + n)) { CycleToIndex(n); fired = true; break; }   // 1..9
            }
            if (fired) _nextCycleAt = DateTime.UtcNow.AddMilliseconds(250);
        }
        // Nav-menu toggle (keyboard): Ctrl+Alt+M flips the top-left nav-menu dropdown. Foreground-gated +
        // debounced. Reads keys only — sends nothing to the game.
        if (_settings.EnableTargetHotkeys && DateTime.UtcNow >= _nextMenuAt
            && _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd
            && Down(0x11) && Down(0x12) && Down(0x4D))   // Ctrl + Alt + M
        {
            _navMenuExpanded = !_navMenuExpanded;
            _nextMenuAt = DateTime.UtcNow.AddMilliseconds(300);
        }
        // Quick-Target Cycler + nav-menu chord (controller): L3 = prev, R3 = next, L3+R3 = toggle the nav
        // menu. Poll every frame to keep edge state fresh; only ACT while PoE2 is foreground. The chord
        // suppresses the single-stick cycle so opening the menu never also flips the active target.
        // Read-only XInput — sends nothing to the game.
        if (_settings.EnableControllerCycle)
        {
            var input = _controllerCycler.Poll();
            if (_gameHwnd != 0 && GetForegroundWindow() == _gameHwnd)
            {
                if (input.MenuToggle) _navMenuExpanded = !_navMenuExpanded;
                else if (input.Cycle != 0) Cycle(input.Cycle < 0 ? CycleAction.Prev : CycleAction.Next);
            }
        }
    }

    /// <summary>F10: pick the atlas tile under the cursor and advance the route workflow (START → END → reset).
    /// Inverts the same projection the renderer draws with (relPos = screen / scale) to map the cursor into
    /// canvas space, then picks the tile whose box CONTAINS it (fallback: nearest centre). Stores the pick by
    /// GRID coord so the route survives pan/zoom and tiles going off-screen. (No on-screen tile-details
    /// tooltip — that interfered with the point-to-point selection; the pick is just echoed to the console.)</summary>
    private void AtlasRoutePick()
    {
        if (_inGameStateForApi == 0 || !GetCursorPos(out var pt)) { Console.WriteLine("\n[atlas route] not in game."); return; }
        // Invert the shared projection: for screen = relPos × scale (offset/shear/persp = 0), relPos = screen/scale.
        var proj = AtlasProjection();
        double scaleX = Math.Abs(proj[0]) > 1e-6 ? proj[0] : 1, scaleY = Math.Abs(proj[4]) > 1e-6 ? proj[4] : 1;
        double curX = pt.X / scaleX, curY = pt.Y / scaleY; // cursor in canvas/relPos units

        Poe2Atlas.AtlasNodeLive? bestIn = null, bestAny = null; double bdIn = 1e18, bdAny = 1e18;
        foreach (var n in _atlas.ReadNodes(_inGameStateForApi))
        {
            // Consider EVERY node (not just the local-Visible ones): the game leaves the visible bit OFF for
            // undiscovered / fog-of-war tiles even though it draws them at a valid relPos, so filtering it made
            // F10 skip fogged tiles and snap to the nearest visible neighbour. Routing must reach those tiles.
            if (!float.IsFinite(n.X) || !float.IsFinite(n.Y)) continue;
            double dx = curX - n.X, dy = curY - n.Y, d = dx * dx + dy * dy;
            if (d < bdAny) { bdAny = d; bestAny = n; }     // nearest centre (fallback)
            double hw = (n.W > 1 ? n.W : 40) * 0.5, hh = (n.H > 1 ? n.H : 40) * 0.5; // tile half-extents (canvas units)
            if (Math.Abs(dx) <= hw && Math.Abs(dy) <= hh && d < bdIn) { bdIn = d; bestIn = n; } // cursor inside the tile box
        }
        if ((bestIn ?? bestAny) is not { } b) { Console.WriteLine("\n[atlas route] no tile under cursor (is the Atlas open?)."); return; }

        // Dump the hovered tile's full identity so the user can set web-UI filters even when the display
        // name is unusual: the REAL map name (WorldAreas +0x08), the raw internal code (never localized,
        // always a safe match key), the rolled content tags, biome and grid coord.
        var content = b.Tags.Count > 0 ? string.Join(", ", b.Tags) : "(none)";
        Console.WriteLine($"\n[atlas tile] \"{b.MapName}\"  code={b.MapCode}  kind={b.Kind}  grid={b.Grid}  biome={b.Biome}");
        // Status cross-validation (improvement 1): deeper-model accessible/completed vs the element flag bits.
        Console.WriteLine($"            accessible={b.Accessible} completed={b.Completed}  (elem flags=0x{b.Flags:X2}: unlocked={b.Unlocked} visited={b.Visited})");
        Console.WriteLine($"             content: {content}");
        Console.WriteLine($"             web-UI filters -> Map: \"{b.MapName}\"" + (b.Tags.Count > 0 ? $"   Content: {content}" : ""));

        // 1st press → set START · 2nd press → set END (route computed each tick) · 3rd → reset. The grids
        // are read by the world thread (UpdateAtlas/BuildAtlasRoute), so mutate them under _atlasLock —
        // a nullable int-tuple isn't a torn-read-safe field.
        string stage;
        lock (_atlasLock)
        {
            if (_atlasStartGrid is null) { _atlasStartGrid = b.Grid; _atlasGoalGrid = null; stage = $"START = {b.Grid} '{b.MapName}'  (F10 another tile to set END)"; }
            else if (_atlasGoalGrid is null) { _atlasGoalGrid = b.Grid; stage = $"END = {b.Grid} '{b.MapName}'  (routing from {_atlasStartGrid}; F10 again to reset)"; }
            else { _atlasStartGrid = null; _atlasGoalGrid = null; stage = "route RESET (F10 a tile to set a new START)"; }
        }
        Console.WriteLine($"\n[atlas route] {stage}");
    }

    /// <summary>The atlas projection, derived LIVE from the game window height and live atlas zoom:
    /// screen = relPos × (UIscale×zoom), UIscale = winH/1600. Pure uniform scale, NO offset — relPos
    /// already has pan baked in and the canvas origin sits at screen (0,0) (the long-proven 1080p default
    /// was scale≈0.572 / offset 0). This is what lines up at any resolution with no hand-calibration.
    /// Returned in the 8-coeff homography layout (shear + perspective + offset = 0).</summary>
    private double[] AtlasProjection()
    {
        float uiScale = _window.Height > 0 ? _window.Height / 1600f : 1080f / 1600f;
        float scale = uiScale * (_atlasZoom > 0.01f ? _atlasZoom : 0.85f);
        return new double[] { scale, 0, 0, 0, scale, 0, 0, 0 };
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
    /// Build the unified navigation-target list for this world tick: every tile landmark first, then
    /// qualifying entity POIs nearest-first. An entity qualifies (is selectable) when it's alive AND
    /// (game-flagged POI, OR a unique monster, OR its display rule has the Auto-path flag). Each target
    /// carries <see cref="NavTarget.AutoPath"/> — true when its display rule opts into auto-pathing —
    /// which drives the zone-entry auto-selection (replacing the old AutoNavPatterns list). Deduped by id.
    /// </summary>
    private List<NavTarget> BuildNavTargets(NumVec2 player)
    {
        var targets = new List<NavTarget>(_landmarks.Count + 16);
        var seen = new HashSet<string>();

        // (a) Tile landmarks — id "t:<key>" (per-cluster). Auto-path when a Tile rule opts in.
        foreach (var lm in _landmarks)
        {
            var id = "t:" + lm.Key;
            if (!seen.Add(id)) continue;
            var autoPath = _displayRules.ResolveTile(lm.Path, requireMatch: false)?.Navigable ?? false;
            targets.Add(new NavTarget(id, LandmarkLabel(lm), lm.Center, lm.Path, IsEntity: false, AutoPath: autoPath));
        }

        // (b) Entity POIs — id "e:<entityId>", nearest-first. Selectable if POI/unique/Auto-path rule;
        // AutoPath true only when the matched rule's Auto-path flag is set.
        var pois = _entities
            .Where(e => e.IsAlive && !e.IconComplete)
            .Select(e => (e, nav: _displayRules.Resolve(e)?.Navigable ?? false))
            .Where(x => x.e.Poi
                        || (x.e.Category == Poe2Live.EntityCategory.Monster && x.e.Rarity == Poe2Live.Rarity.Unique)
                        || x.nav)
            .OrderBy(x => NumVec2.DistanceSquared(x.e.Grid, player));
        foreach (var (e, nav) in pois)
        {
            var id = "e:" + e.Id;
            if (!seen.Add(id)) continue;
            targets.Add(new NavTarget(id, EntityLabel(e.Metadata), e.Grid, e.Metadata, IsEntity: true, AutoPath: nav));
        }

        return targets;
    }

    /// <summary>Rank the current nav targets by catalog priority (desc) then distance (asc): reuse the
    /// Director's Rank for covered content, then append uncatalogued targets by distance. World-thread.</summary>
    private IReadOnlyList<RankedTarget> BuildRankedTargets(NumVec2 player)
    {
        var nav = _navTargets;
        if (nav.Count == 0) return System.Array.Empty<RankedTarget>();
        var result = new List<RankedTarget>(nav.Count);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in _campaign.Rank(_entities, _landmarks, player))   // priority desc, distance asc; covered only
            if (covered.Add(r.Id)) result.Add(new RankedTarget(r.Id, r.Label, r.Category));
        foreach (var t in nav.Where(t => !covered.Contains(t.Id))
                             .OrderBy(t => NumVec2.DistanceSquared(t.Grid, player)))
            result.Add(new RankedTarget(t.Id, t.Name, ""));
        return result;
    }

    /// <summary>Apply a cycle action (render thread): pick the new active id and route to it (single-active).</summary>
    private void Cycle(CycleAction action)
    {
        var ranked = _rankedTargets;
        var ids = new List<string>(ranked.Count);
        foreach (var r in ranked) ids.Add(r.Id);
        var next = action switch
        {
            CycleAction.Next => TargetCycler.Next(ids, _activeTargetId),
            CycleAction.Prev => TargetCycler.Prev(ids, _activeTargetId),
            _                => null,   // Clear
        };
        ApplyActive(next, ranked);
    }

    /// <summary>Jump to 1-based slot N (render thread).</summary>
    private void CycleToIndex(int oneBased)
    {
        var ranked = _rankedTargets;
        var ids = new List<string>(ranked.Count);
        foreach (var r in ranked) ids.Add(r.Id);
        ApplyActive(TargetCycler.AtIndex(ids, oneBased), ranked);
    }

    private void ApplyActive(string? id, IReadOnlyList<RankedTarget> ranked)
    {
        _activeTargetId = id;
        SetActiveTarget(id);   // single-active: replace _selectedIds with just this one (or none)
        if (id is null) { _cycleIndicator = null; return; }
        var pos = 0;
        for (var i = 0; i < ranked.Count; i++) if (ranked[i].Id == id) { pos = i; break; }
        var rt = ranked[pos];
        _cycleIndicator = new CycleIndicator(pos + 1, ranked.Count, rt.Name, rt.Category, DateTime.UtcNow.AddSeconds(2));
    }

    /// <summary>Single-active selection: clear the nav selection and add this one id (or none). Only edits
    /// _selectedIds under _navLock — trackers/routes reconcile on the tick thread (like ToggleSelectionCore).</summary>
    private void SetActiveTarget(string? id)
    {
        lock (_navLock)
        {
            _selectedIds.Clear();
            if (id is not null) _selectedIds.Add(id);
        }
    }

    /// <summary>Zone change: remember the leaving zone's selection (by its instance hash), then either
    /// RESTORE the selection we previously had for the zone we're entering (so a town round-trip keeps
    /// your pathing) or — on a first visit — seed it from the persistent auto-nav patterns. Trackers are
    /// NOT touched here — the per-tick reconciliation (ReconcileTrackers) syncs them to _selectedIds.</summary>
    private void OnAreaChanged(uint areaHash)
    {
        int count; bool restored;
        lock (_navLock)
        {
            // Save what was selected in the zone we're leaving, keyed by ITS instance hash.
            if (_selectionAreaHash != 0) RememberZoneSelection(_selectionAreaHash, _selectedIds);

            _selectedIds.Clear();
            _selectionCapWarned = false;
            _selectionAreaHash = areaHash;

            // Returning to a remembered instance → restore its selection verbatim (the user's explicit
            // choices win, including an intentionally-empty one, so a zone they cleared stays cleared).
            List<string>? remembered = null;
            restored = areaHash != 0 && _zoneSelections.TryGetValue(areaHash, out remembered);
            if (restored)
            {
                foreach (var id in remembered!)
                {
                    if (_selectedIds.Count >= MaxSelectedTargets) break;
                    if (!_selectedIds.Contains(id)) _selectedIds.Add(id);
                }
            }
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
            count = _selectedIds.Count;
        }
        _director.ResetZone();
        _selectedPaths = new List<SelectedPath>();

        if (count > 0)
            Console.WriteLine($"\nNav: {(restored ? "restored" : "auto-selected")} {count} target(s) on zone change.");
    }

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
        _directorQueue = _director.Queue;
    }

    /// <summary>
    /// Drop selected ENTITY targets the game has marked complete (IconComplete — e.g. a claimed
    /// expedition / used incursion device). Such an entity is hidden from the map and excluded from
    /// the nav-target list, but it lingers (faded) in the live entity set, so <see cref="TryResolveTargetGrid"/>
    /// would still resolve it and the route would keep pathing there. Pruning the id stops the route
    /// (its tracker is removed by the next ReconcileTrackers) and "sticks" via the per-zone memory.
    /// <para>Only prunes targets whose entity is PRESENT-and-complete — an entity merely out of network
    /// range (temporarily absent) is left selected so it resumes when you return to it.</para>
    /// </summary>
    private void PruneCompletedTargets()
    {
        lock (_navLock)
        {
            if (_selectedIds.Count == 0) return;
            _selectedIds.RemoveAll(id =>
            {
                if (!id.StartsWith("e:", StringComparison.Ordinal) || !uint.TryParse(id.AsSpan(2), out var eid))
                    return false;
                foreach (var e in _entities)
                    if (e.Id == eid) return e.IconComplete; // present → prune iff completed; else keep
                return false; // absent (out of range) → keep; it may return
            });
        }
    }

    /// <summary>Store a copy of <paramref name="ids"/> under <paramref name="hash"/>, evicting the
    /// oldest remembered zone when the table is full. Call under <see cref="_navLock"/>.</summary>
    private void RememberZoneSelection(uint hash, List<string> ids)
    {
        if (!_zoneSelections.ContainsKey(hash))
        {
            if (_zoneOrder.Count >= MaxRememberedZones)
            {
                _zoneSelections.Remove(_zoneOrder[0]);
                _zoneOrder.RemoveAt(0);
            }
            _zoneOrder.Add(hash);
        }
        _zoneSelections[hash] = new List<string>(ids);
    }

    /// <summary>Surfacing matcher fed to Poe2Live: a terrain tile surfaces as a landmark when a user
    /// landmark pattern matches OR a (non-hide) "Tile" display rule with explicit match terms matches.
    /// Returns the label to show (empty string = use the tile's derived name), or null to not surface.</summary>
    private string? TileLandmarkMatch(string tilePath)
    {
        var tr = _displayRules.ResolveTile(tilePath, requireMatch: true);
        return tr is { Hide: false } ? (tr.Label ?? "") : null;
    }

    /// <summary>Distinct terrain-tile paths for the current area (served by /api/tiles for the add-rule
    /// picker). Empty when not in game. Cached per area inside Poe2Live. Runs on the HTTP thread, so it
    /// uses the API's OWN reader stack (_liveApi) — never the world thread's _live.</summary>
    private IReadOnlyList<string> CurrentTilePaths()
        => _areaInstanceForApi != 0 ? _liveApi.TilePaths(_areaInstanceForApi) : Array.Empty<string>();


    /// <summary>F6 (render thread): add the nearest navigation target not already selected into the
    /// selection.</summary>
    private void AddNearestPathTarget()
    {
        var targets = _navTargets;   // one volatile read — work off this fully-built list
        if (targets.Count == 0) return;
        var player = _state.Player;

        // _navTargets isn't fully distance-sorted (tiles come first), so scan for the nearest
        // unselected target by grid distance. Snapshot the selection to test membership.
        var selected = SnapshotSelection();
        var bestId = (string?)null;
        var bestD = float.MaxValue;
        foreach (var t in targets)
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
            var key = id[2..];
            foreach (var lm in _landmarks)
                if (lm.Key == key) { grid = lm.Center; return true; }
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

        // (b) Drain completed background routes FIRST, then maintain. Applying a fresh path resets the
        //     cursor to 0; advancing it (in (c) below) in the SAME tick — before RebuildSelectedPaths
        //     reads CurrentPoints — prevents a one-frame "backward tail" pop when the waypoints swap.
        if (_replanner.TryDrainResults(out var results))
        {
            foreach (var r in results)
            {
                if (!_trackers.TryGetValue(r.TargetId, out var tracker)) continue; // deselected → ignore
                tracker.ApplyResult(r.Waypoints, new NumVec2(r.Goal.x, r.Goal.y));
            }
        }

        // (c) Maintain (advance cursor, cheap) + trigger replans. Resolve each id to its live grid; if it
        //     doesn't resolve this tick (despawned / not yet present) keep it selected but skip planning.
        foreach (var id in selected)
        {
            if (!_trackers.TryGetValue(id, out var tracker)) continue;
            tracker.Maintain(player);
            if (!TryResolveTargetGrid(id, out var goal)) continue;
            if (!tracker.ReplanInFlight && tracker.ShouldReplan(player, goal))
                EnqueueReplan(id, tracker, goal);
        }

        // (d) Cheap rebuild of the draw list from each tracker's current (cursor-advanced) points.
        RebuildSelectedPaths(selected);
    }

    /// <summary>Snapshot the immutable terrain + player/goal and hand a replan request to the worker
    /// (marks the tracker in-flight). No A* on this thread.</summary>
    private void EnqueueReplan(string id, RouteTracker tracker, NumVec2 goal)
    {
        if (_terrain is not { } terrain) return; // can't plan without terrain yet
        var player = _worldPlayer;   // the world tick's current player (this all runs on the world thread)
        tracker.MarkReplanRequested(player);
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

    /// <summary>Display label for a selected id (its NavTarget name if still present, else the raw id).
    /// Callable from the API thread (via ToggleSelectionCore), so it reads the volatile _navTargets once.</summary>
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

        // Prefer a curated friendly name from the entity-name table when one exists
        // (e.g. "Lightning Wraith"); fall back to the path-derived prettifier below.
        if (EntityNameResolver.Shared.Resolve(metadata) is { Length: > 0 } resolved)
            return resolved;

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

    /// <summary>API (/api/atlas): a JSON-ready snapshot of the atlas map-data we can read — the full
    /// map-archetype catalog and the set of map types present in the current atlas region. Inspection /
    /// validation only (no spatial graph yet — see resources/atlas-research-notes.md). The reader scans
    /// + caches, so the first call after entering the atlas may take a moment; called on the API thread.</summary>
    private object AtlasJson()
    {
        // Anchor the scan to the live game-heap slab (the catalog shares the arena with AreaInstance).
        var d = _atlas.Read(_lastAreaInstance);
        // Live node graph (atlas nodes are UiElements) — summary + the locally-visible highlight set.
        var nodes = _inGameStateForApi != 0 ? _atlas.ReadNodes(_inGameStateForApi) : new List<Poe2Atlas.AtlasNodeLive>();
        var vis = nodes.Where(n => n.Visible).ToList();
        return new
        {
            located = d.Located,
            note = d.Note,
            catalogAddr = $"0x{d.CatalogAddr:X}",
            catalogCount = d.CatalogCount,
            regionCount = d.Region.Count,
            catalog = d.Catalog.Select(m => new { id = m.Id, code = m.Code, name = m.Name, kind = m.Kind, parsedObj = $"0x{m.ParsedObj:X}" }),
            region = d.Region.Select(r => new { code = r.Code, name = r.Name, kind = r.Kind }),
            nodes = new
            {
                total = nodes.Count,
                visible = vis.Count,
                hasContent = nodes.Count(n => n.HasContent),
                unvisited = nodes.Count(n => !n.Visited),
                unlocked = nodes.Count(n => n.Unlocked),
                biomes = nodes.GroupBy(n => (int)n.Biome).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            },
            // Every distinct content tag currently on the atlas (+ count), for the dashboard's filter /
            // highlight-rule pickers. These are the readable content/mechanic names (Powerful Map Boss,
            // Breach, Delirium, …) resolved from each node's EndgameMapAtlas row.
            allTags = nodes.SelectMany(n => n.Tags).GroupBy(t => t).OrderByDescending(g => g.Count())
                .Select(g => new { tag = g.Key, count = g.Count() }),
            // Distinct MAP NAMES (Sun Temple, Precursor Tower, Vaal City, …) — the separate "Map" filter
            // group, so towers/temples/specific maps are highlightable independently of rolled content.
            allMaps = nodes.Where(n => !string.IsNullOrEmpty(n.MapName)).GroupBy(n => n.MapName)
                .OrderBy(g => g.Key).Select(g => new { tag = g.Key, count = g.Count() }),
            // Map-archetype KINDS present (Citadel / Boss / Tower / Unique / Merchant) — first-class track
            // targets (improvement 3): tracking "Tower" rings/routes EVERY tower without listing each name.
            allKinds = nodes.Where(n => !string.IsNullOrEmpty(n.Kind) && n.Kind != "Normal").GroupBy(n => n.Kind)
                .OrderByDescending(g => g.Count()).Select(g => new { tag = g.Key, count = g.Count() }),
            // The currently active rules (persisted): tracked tags (rings) + arrow tags (off-screen
            // direction). Match against BOTH content tags and map names.
            highlightTags = _settings.AtlasHighlightTags,
            navTags = _settings.AtlasNavTags,
            arrowTags = _settings.AtlasArrowTags,
            // The individual live nodes for the dashboard's grid. On-screen first, then content/unvisited.
            nodeList = nodes
                .OrderByDescending(n => n.Visible).ThenByDescending(n => n.HasContent).ThenByDescending(n => !n.Visited)
                .Take(2000)
                .Select(n => new
                {
                    el = ((long)n.Element).ToString(), // unique stable key (element address) for selection
                    id = n.Id, biome = (int)n.Biome, type = n.IconType, hasContent = n.HasContent,
                    unlocked = n.Unlocked, visited = n.Visited, visible = n.Visible,
                    accessible = n.Accessible, completed = n.Completed, kind = n.Kind,
                    x = (int)n.X, y = (int)n.Y, map = n.MapName, tags = n.Tags,
                }),
        };
    }

    /// <summary>Read the live atlas nodes and rebuild the highlight marks + F10 route, publishing them as a
    /// single immutable <see cref="AtlasRender"/> the render thread reads lock-free. Runs on the world thread.
    /// Cheap when the atlas is closed (ReadNodes returns empty via its visibility gate). Rides over transient
    /// empty reads so the route doesn't flicker; freezes the marks when the view is static (no arrow jitter).</summary>
    private void UpdateAtlas(nint inGameState)
    {
        var nodes = _atlas.ReadNodes(inGameState);
        if (nodes.Count == 0)
        {
            // Empty read: ride over TRANSIENT misses (a node read hiccupping ~1×/sec was the ~0.1s route
            // flicker) so the route doesn't blink out. Treat the atlas as CLOSED only when the panel's visible
            // bit reads closed AND we've had no good read for a short grace — that absorbs both the node-read
            // miss and a racy visible-bit read, while still clearing promptly on a real close.
            var stillOpen = _atlas.IsAtlasOpen(inGameState) || (DateTime.UtcNow - _atlasGoodAt).TotalSeconds < 0.4;
            if (_atlasRender.Open && stillOpen) return;      // keep last marks/route — no flicker
            _builtAtlasOnce = false; _lastAtlasSig = 0;      // force a rebuild on reopen
            if (!ReferenceEquals(_atlasRender, AtlasRender.Closed)) _atlasRender = AtlasRender.Closed;
            return;                                          // (manual START/END grids persist)
        }
        _atlasGoodAt = DateTime.UtcNow;
        // Live zoom = the nodes' shared canvas scale (+0x130). Use the median (robust to a stray 0/odd node).
        // Drives both the ring projection and the route projection (relPos × winH/1600 × zoom).
        var scales = nodes.Where(n => n.Scale > 0.01f).Select(n => n.Scale).OrderBy(s => s).ToList();
        if (scales.Count > 0) _atlasZoom = scales[scales.Count / 2];

        // Snapshot the cross-thread inputs ONCE under the lock: the F10 START/END grids (written by the
        // render thread) and the dashboard node selection (written by the API thread).
        (int X, int Y)? startGrid, goalGrid; HashSet<nint> sel;
        lock (_atlasLock) { startGrid = _atlasStartGrid; goalGrid = _atlasGoalGrid; sel = new HashSet<nint>(_atlasSel); }
        // The player's CURRENT atlas node (the route source for auto-navigation). Changes only on zone
        // change, but it MUST feed the freeze signature so the auto-routes re-solve as the player advances.
        var curGrid = _atlas.CurrentNodeGrid();

        // ARROW JITTER FIX — freeze the marks when the atlas view is static. PoE2 doesn't keep CULLED
        // (off-screen) UI elements' relPos cleanly updated, so off-screen nodes' positions are noisy — which
        // is why the off-screen ARROWS jitter while on-screen rings (properly laid out) stay still. Arrows
        // are only a direction hint, so we don't need to re-read them every tick: build a signature from the
        // live zoom + the centroid of FIRMLY on-screen nodes (stable when idle) + the inputs that affect the
        // marks (route endpoints, rule/selection counts). If it's unchanged, KEEP the previous marks/route
        // frozen → no jitter. Rebuild only when the view pans/zooms or an input changes. (Stay live until tag
        // resolution finishes so all default highlights get seeded first.)
        float pscale = (_window.Height > 0 ? _window.Height / 1600f : 0.675f) * (_atlasZoom > 0.01f ? _atlasZoom : 0.85f);
        double cxSum = 0, cySum = 0; int onCount = 0; float vw = _window.Width, vh = _window.Height; const float vm = 80f;
        foreach (var n in nodes)
        {
            float sx = n.X * pscale, sy = n.Y * pscale;
            if (sx > vm && sx < vw - vm && sy > vm && sy < vh - vm) { cxSum += n.X; cySum += n.Y; onCount++; }
        }
        long viewSig = onCount == 0 ? 0
            : (long)Math.Round(cxSum / onCount) * 73856093L
            ^ (long)Math.Round(cySum / onCount) * 19349663L
            ^ (long)Math.Round(_atlasZoom * 2000f) * 83492791L;
        long inputSig = (long)(startGrid?.GetHashCode() ?? 0)
            ^ ((long)(goalGrid?.GetHashCode() ?? 0) << 1)
            ^ ((long)(_settings.AtlasHighlightTags?.Count ?? 0) << 20)
            ^ ((long)(_settings.AtlasArrowTags?.Count ?? 0) << 28)
            ^ ((long)sel.Count << 36)
            ^ (_settings.AtlasDrawAll ? 1L << 44 : 0L);
        long sig = viewSig * 2654435761L ^ inputSig;
        sig = sig * 1000003L ^ (curGrid?.GetHashCode() ?? 0);                       // re-solve routes as the player moves
        sig = sig * 1000003L ^ (_settings.AtlasAutoRoute ? 1L : 0L) ^ ((long)_settings.AtlasAutoRouteMaxHops << 1);
        sig = sig * 1000003L ^ (long)(_settings.AtlasNavTags?.Count ?? 0);          // re-solve when the nav set changes
        if (_builtAtlasOnce && _atlas.AllTagsResolved && sig == _lastAtlasSig)
            return;   // view + inputs unchanged → marks/route stay frozen (off-screen arrows don't jitter)
        _lastAtlasSig = sig; _builtAtlasOnce = true;

        // One-time default: track + arrow every Citadel (high-value, usually off-screen) until the user
        // edits the rules from the dashboard. Boss is intentionally NOT defaulted (too common). Wait until
        // tag resolution has caught up (it's budget-limited per tick) so we seed ALL citadels, not just the
        // first batch resolved.
        if (!_settings.AtlasRulesInitialized && _atlas.AllTagsResolved)
        {
            var cit = nodes.Where(n => !string.IsNullOrEmpty(n.MapName) && n.MapName.Contains("Citadel", StringComparison.OrdinalIgnoreCase))
                           .Select(n => n.MapName).Distinct().ToList();
            if (cit.Count > 0)
            {
                _settings.AtlasHighlightTags = new List<string>(cit); // ring
                _settings.AtlasNavTags = new List<string>(cit);       // + auto-route to them
                _settings.AtlasArrowTags = new List<string>(cit);     // + off-screen arrow
                foreach (var c in cit) _settings.AtlasHighlightColors[c] = "#e0b341"; // Citadel gold
                _settings.AtlasRulesInitialized = true;
                _settings.Save();
            }
        }

        // A node matches a rule set if its map name or one of its content tags is in the set; returns the
        // matched tag (drives label + colour). Track set ⇒ draw a ring; Arrow set ⇒ off-screen edge arrow.
        var hlTrack = new HashSet<string>(_settings.AtlasHighlightTags ?? new(), StringComparer.OrdinalIgnoreCase);
        var hlNav = new HashSet<string>(_settings.AtlasNavTags ?? new(), StringComparer.OrdinalIgnoreCase);
        var hlArrow = new HashSet<string>(_settings.AtlasArrowTags ?? new(), StringComparer.OrdinalIgnoreCase);
        // A node matches a rule set if its map name, one of its content tags, OR its map-archetype KIND
        // (Citadel/Boss/Tower/Unique/Merchant — improvement 3) is in the set. Returns the matched token.
        static string? Match(HashSet<string> set, in Poe2Atlas.AtlasNodeLive nd)
        {
            if (set.Count == 0) return null;
            if (!string.IsNullOrEmpty(nd.MapName) && set.Contains(nd.MapName)) return nd.MapName;
            if (nd.Tags is { Count: > 0 }) foreach (var t in nd.Tags) if (set.Contains(t)) return t;
            if (!string.IsNullOrEmpty(nd.Kind) && nd.Kind != "Normal" && set.Contains(nd.Kind)) return nd.Kind;
            return null;
        }
        var marks = new List<AtlasMark>(128);
        // Routing inputs gathered alongside the marks: grid→node ELEMENT (so the render thread re-reads each
        // route point's live relPos per frame), the tracked tiles to route to, and each tile's ring colour.
        var gridToPoint = new Dictionary<(int, int), AtlasPoint>(nodes.Count);
        var trackedGrids = new List<(int, int)>();
        var gridColor = new Dictionary<(int, int), string?>();
        foreach (var n in nodes) gridToPoint[n.Grid] = new AtlasPoint(n.Element, n.X, n.Y);
        foreach (var n in nodes)
        {
            var selected = sel.Contains(n.Element);
            var mTrack = Match(hlTrack, n);
            var mNav = Match(hlNav, n);
            var mArrow = Match(hlArrow, n);
            var isTracked = selected || mTrack != null;   // Highlight (ring)
            var isNav = mNav != null;                     // Nav-to (route line)
            var isArrow = mArrow != null;                 // Arrow (off-screen pointer)
            // ONLY highlighted/nav/arrow maps are drawn (the point: surface content the game hides).
            // AtlasDrawAll debug overrides this to draw every node.
            if (!_settings.AtlasDrawAll && !isTracked && !isNav && !isArrow) continue;
            var matched = mTrack ?? mNav ?? mArrow;
            var label = matched ?? (n.Tags is { Count: > 0 } ? n.Tags[0] : (string.IsNullOrEmpty(n.MapName) ? null : n.MapName));
            string? color = matched != null && _settings.AtlasHighlightColors.TryGetValue(matched, out var c) ? c : null;
            // Route to NAV tiles that aren't already done — independent of the ring/arrow toggles.
            if (isNav && !n.Completed) { trackedGrids.Add(n.Grid); gridColor[n.Grid] = color; }
            marks.Add(new AtlasMark(n.X, n.Y, isTracked, n.HasContent, n.Visited, n.Unlocked, n.Biome, n.IconType, label, color, isArrow, isNav, n.Element));
        }

        // ── Auto-routing (improvement 1): "you are here" + a route to each tracked tile ──────────────
        // Sources = the player's CURRENT atlas node when known, else the accessible-now frontier (every
        // map you can run right now). One multi-source BFS gives the fewest-hops route to each tracked tile.
        AtlasPoint? currentPt = (curGrid is { } cg && gridToPoint.TryGetValue(cg, out var ce)) ? ce : null;
        var autoRoutes = new List<AtlasAutoSpec>();
        if (_settings.AtlasAutoRoute && trackedGrids.Count > 0)
        {
            // Sources = the ACCESSIBLE-NOW frontier (every map you can run right now) — fixed regardless of
            // the mouse. We deliberately do NOT seed from the current-node marker: on some patches that
            // element tracks the HOVERED tile, which made every route re-origin from the cursor ("navs to
            // whatever I mouse over"). Fall back to the marker only when no accessible node is known (rare).
            var sources = new List<(int, int)>();
            foreach (var n in nodes) if (n.Accessible) sources.Add(n.Grid);
            if (sources.Count == 0 && curGrid is { } c0 && _atlas.GraphHas(c0)) sources.Add(c0);
            if (sources.Count > 0)
            {
                foreach (var kv in _atlas.RoutesFromSources(sources, trackedGrids).OrderBy(r => r.Value.Count))
                {
                    int hops = kv.Value.Count - 1;
                    if (hops <= 0) continue;                                       // already on the tile
                    if (_settings.AtlasAutoRouteMaxHops > 0 && hops > _settings.AtlasAutoRouteMaxHops) continue;
                    var pts = new List<AtlasPoint>(kv.Value.Count);
                    foreach (var gp in kv.Value) if (gridToPoint.TryGetValue(gp, out var ep)) pts.Add(ep);
                    if (pts.Count < 2) continue;
                    gridColor.TryGetValue(kv.Key, out var col);
                    autoRoutes.Add(new AtlasAutoSpec(pts, col, hops));
                    if (autoRoutes.Count >= 30) break;                            // keep the view readable
                }
            }
        }

        var (start, end, route) = BuildAtlasRoute(nodes, startGrid, goalGrid);
        _atlasRender = new AtlasRender(true, marks, start, end, route, currentPt, autoRoutes);   // publish atomically
    }

    /// <summary>Resolve the F10 START/END grid coords to canvas-space (relPos) points for the markers, and —
    /// when both are set — A* through the connection graph for the route polyline. All keyed by grid coord,
    /// so the markers + route survive pan/zoom and tiles going off-screen (every canvas child is in
    /// <paramref name="nodes"/>, so its relPos is available even when off-screen). Returns (startPt, endPt,
    /// route) for the caller to fold into the published <see cref="AtlasRender"/>. Logs once when a freshly-set
    /// END produces (or fails to produce) a path, so we can see whether the graph connected the two.</summary>
    private (AtlasPoint? Start, AtlasPoint? End, List<AtlasPoint> Route) BuildAtlasRoute(
        IReadOnlyList<Poe2Atlas.AtlasNodeLive> nodes, (int X, int Y)? startGrid, (int X, int Y)? goalGrid)
    {
        var route = new List<AtlasPoint>();
        AtlasPoint? startPt = null, endPt = null;
        if (nodes.Count == 0) return (null, null, route);

        var gridToPoint = new Dictionary<(int, int), AtlasPoint>(nodes.Count);
        foreach (var n in nodes) gridToPoint[n.Grid] = new AtlasPoint(n.Element, n.X, n.Y);

        if (startGrid is { } s && gridToPoint.TryGetValue(s, out var sp)) startPt = sp;
        if (goalGrid is { } g && gridToPoint.TryGetValue(g, out var gp)) endPt = gp;

        if (startGrid is { } start && goalGrid is { } goal)
        {
            var path = _atlas.FindPath(start, goal);
            if (path != null) foreach (var p in path) if (gridToPoint.TryGetValue(p, out var rp)) route.Add(rp);
            // Log once per (start,goal) pair so we can see graph connectivity (or the lack of it).
            if (_loggedRoute != (start, goal))
            {
                _loggedRoute = (start, goal);
                Console.WriteLine($"[atlas route] {start}→{goal}: {(path == null ? $"NO graph path (graph has {_atlas.GraphNodeCount} nodes; start in graph={_atlas.GraphHas(start)}, goal in graph={_atlas.GraphHas(goal)})" : $"{path.Count} hops")}");
            }
        }
        else _loggedRoute = null;
        return (startPt, endPt, route);
    }
    private (( int, int) s, (int, int) g)? _loggedRoute;

    /// <summary>API: set the dashboard-selected atlas nodes (by element address) to highlight in-game.
    /// Draw-only — never sends input to the game. Safe to call from the API thread.</summary>
    public void SetAtlasSelection(IReadOnlyList<long> els)
    {
        lock (_atlasLock) { _atlasSel.Clear(); foreach (var e in els) _atlasSel.Add((nint)e); }
    }

    /// <summary>API: set the active atlas highlight rules (tag + ring colour). Only nodes whose content
    /// tags or map name match one of these are drawn in-game, in the rule's colour. Persisted; applied on
    /// the next world tick. Draw-only.</summary>
    public void SetAtlasHighlight(IReadOnlyList<(string tag, string color, bool track, bool nav, bool arrow)> rules)
    {
        var tags = new List<string>(); var navs = new List<string>(); var arrows = new List<string>();
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, color, track, nav, arrow) in rules)
        {
            if (string.IsNullOrWhiteSpace(tag) || !seen.Add(tag)) continue;
            if (track) tags.Add(tag);
            if (nav) navs.Add(tag);
            if (arrow) arrows.Add(tag);
            if (!string.IsNullOrWhiteSpace(color)) colors[tag] = color;
        }
        _settings.AtlasHighlightTags = tags;
        _settings.AtlasNavTags = navs;
        _settings.AtlasArrowTags = arrows;
        _settings.AtlasHighlightColors = colors;
        _settings.AtlasRulesInitialized = true;   // any explicit edit locks out the Citadel default-seed
        _settings.Save();
    }

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

    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint p);

    // Raise the system timer resolution to 1 ms so the render-loop frame pacer (Thread.Sleep) is accurate.
    // Without this, Windows' default ~15.6 ms timer granularity caps ANY Thread.Sleep-paced loop at ~64 fps —
    // which made the overlay judder on high-refresh monitors regardless of FpsCap.
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    // Detect the refresh rate of the monitor the game window is currently on, so FpsCap=0 ("auto") matches it.
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfoEx lpmi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    [StructLayout(LayoutKind.Sequential)] private struct DisplayRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint cbSize;
        public DisplayRect rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    // Only the head of DEVMODEW matters up to dmDisplayFrequency; the layout below mirrors DEVMODEW exactly
    // through that field (the display union is dmPosition(8)+orientation(4)+fixedOutput(4)).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    /// <summary>Refresh rate (Hz) of the monitor the game window is on, or 0 if it can't be determined
    /// (e.g. dmDisplayFrequency reports 0/1 = "default"). Used only when FpsCap is set to 0 (auto-match).</summary>
    private int DetectGameMonitorHz(nint hwnd)
    {
        try
        {
            if (hwnd == 0) return 0;
            var hmon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (hmon == 0) return 0;
            var mi = new MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfoW(hmon, ref mi)) return 0;
            var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettingsW(mi.szDevice, -1 /* ENUM_CURRENT_SETTINGS */, ref dm)) return 0;
            return dm.dmDisplayFrequency > 1 ? (int)dm.dmDisplayFrequency : 0;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        _shutdown = true;
        _worldThread?.Join(1000);   // let the background world loop observe _shutdown and exit
        _modCatalog.Flush(); // persist any mods seen since the last debounced write
        _seenPoiLog.Flush();
        _entityAtlas.Flush();
        _entityNameStore.Flush();
        _replanner.Dispose();
        _api.Dispose();
        _renderer.Dispose();
        _window.Dispose();
    }
}
