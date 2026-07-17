using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Config;
using POE2Radar.Core.Game;
using POE2Radar.Core.Health;
using POE2Radar.Core.Input;
using POE2Radar.Core.Icons;
using POE2Radar.Core.Remote;
using POE2Radar.Core.OverlayLayouts;
using POE2Radar.Core.RadarFilters;
using POE2Radar.Core.NavDestinations;
using POE2Radar.Core.Rules;
using POE2Radar.Core.Session;
using POE2Radar.Core.Tracks;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Tiny read-only HTTP API for live troubleshooting (the PoE2 stand-in for POEMCP). Serves the
/// latest <see cref="RadarState"/> published by <see cref="RadarApp"/> each world tick.
///
/// Endpoints (localhost:7777, read-only — no CORS header, so only the same-origin dashboard can read them):
///   GET /                         — the web dashboard (see <see cref="DashboardHtml"/>)
///   GET /health                   — liveness probe
///   GET /state                    — player, area, map visibility, entity counts by category
///   GET /entities                 — all entities (id, category, metadata, pos, hp, dist)
///       ?category=Monster         — filter by category (case-insensitive)
///       &amp;alive=true               — only entities with HP &gt; 0
///       &amp;radius=80                — only within N grid units of the player
///       &amp;limit=50                 — cap results (default 500)
///   GET  /api/icons               — the icon library (name + viewBox + paths) for the dashboard pickers
///   GET  /api/settings            — current radar/visual settings (+ read-only flask mirror)
///   POST /api/settings            — write whitelisted radar/visual settings only (flags + calibration);
///                                   loopback-Host-gated; never exposes flask/automation writes
///   GET  /api/nav                 — current navigation-target selection (ids + color slots)
///   POST /api/nav                 — toggle/clear a navigation target (draw-only; never sends input to
///                                   the game); loopback-Host-gated like POST /api/settings
///   GET  /api/hidden              — user cull patterns (entities matching these are hidden everywhere)
///   POST /api/hidden              — add/remove/clear a cull pattern ({add|remove|clear}); loopback-Host-gated
///   GET  /api/watched             — user highlight rules (pattern/label/color/shape/size/enabled)
///   POST /api/watched             — add/update/remove a highlight rule; loopback-Host-gated
///   GET  /api/zone                — static zone reference: friendly name, act/level, flags, leveling notes
///   GET  /api/landmark-patterns   — user tile-path patterns surfaced as landmarks (pattern/label/enabled)
///   POST /api/landmark-patterns   — add/update/remove a landmark pattern; loopback-Host-gated
/// </summary>
public sealed class ApiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<RadarState> _state;
    private readonly RadarSettings _settings;
    // Navigation selection controller, supplied by RadarApp. These only mutate the draw-only path
    // selection — they NEVER send input to the game.
    private readonly Func<IReadOnlyList<(string Id, int Slot)>> _navGet;
    private readonly Action<string> _navToggle;
    private readonly Action _navClear;
    private readonly HiddenEntities _hidden;
    private readonly DisplayRules _displayRules;
    private readonly LandmarkStore _landmarkStore;
    private readonly Func<IReadOnlyList<string>> _tiles;
    // Persistent catalog of every monster affix-mod id ever seen — the vocabulary the rule editor
    // browses to author a Mods matcher. Read-only provider supplied by RadarApp.
    private readonly Func<IReadOnlyList<string>> _knownMods;
    // Director catalog: user-managed objectives + the seen-POI log (candidates that lack an objective).
    private readonly CampaignObjectives _objectives;
    private readonly Func<IReadOnlyList<SeenPoi>> _seenPois;
    // Entity Atlas census: every distinct entity metadata key seen across sessions + user-friendly name overrides.
    private readonly Func<IReadOnlyList<AtlasEntry>> _entityAtlasEntries;
    private readonly EntityNameStore _entityNames;
    private readonly Func<object> _gear;
    private readonly Func<object> _preload;
    private readonly Func<object> _buffsDiag;
    private readonly GearWeightStore _gearWeights;
    // Atlas map-data provider (catalog + current-region map set). Read-only, computed on demand (it
    // scans memory + caches), returns a JSON-ready object. Null when atlas reading is unavailable.
    private readonly Func<object>? _atlas;
    // Atlas node selection (element addresses) to highlight in-game; draw-only, loopback-gated.
    private readonly Action<IReadOnlyList<long>>? _atlasSelect;
    // Atlas highlight rules (tag + colour + track/arrow) — only matching nodes draw in-game; loopback-gated.
    private readonly Action<IReadOnlyList<(string tag, string color, bool track, bool nav, bool arrow)>>? _atlasHighlight;
    // Version/update info provider ({current, latest, updateAvailable, url}) for the dashboard banner.
    private readonly Func<object>? _version;
    private readonly Action? _rescan;
    // Delegate wired from RadarApp to play a named audio cue ("monster"|"item"|"objective").
    // POST /api/audio-test invokes it for dashboard test buttons — loopback-gated.
    private readonly Action<string>? _audioTest;
    // Delegate wired from RadarApp to rebuild audio cues when volume/tone settings change.
    // POST /api/settings invokes it when any audioAlert* or audioTone* key is applied.
    private readonly Action? _rebuildAudio;
    private readonly PresetStore _presetStore;
    private volatile bool _running;
    private Task? _loopTask;
    private readonly bool _allowLanAccess;   // bound all-interfaces when true (opt-in LAN view)
    private readonly int _port;              // stored for /api/lan-info + loopback fallback
    private volatile bool _lanBindFailed;    // true if http://+:port bind threw and we fell back to loopback
    private readonly Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)>? _terrainProvider;
    private uint _mapCacheHash;      // /api/map: 1-entry payload cache keyed by area hash (API loop is single-threaded)
    private string? _mapCacheJson;
    // v0.20.0 T5: browser-view infrastructure. Both null when EnableWebMap and EnableWebObs are off —
    // the switch arms feature-gate on these so `/map`, `/obs`, `/stream`, and `/assets/*` all 404
    // without dereferencing either. RadarApp wires real instances when either toggle is on.
    private readonly SseChannel? _sse;
    private readonly AssetHost? _assetHost;
    // v0.22 PROBE-CONTRIBUTE: per-boot JSONL sink shared with CampaignProbe. Null when the writer
    // failed to open at RadarApp construction time (or in tests that don't need probe wiring) —
    // /api/contribute-trace short-circuits to a clean 400 in that case.
    private readonly POE2Radar.Core.Campaign.Probe.EventWriter? _traceWriter;
    private readonly Func<object>? _wipeLog;   // v0.30 Instinct: /api/wipe-log payload provider
    private readonly ItemFilterEngine? _itemFilters;   // v0.31 Prospector: /api/item-filters engine
    private readonly Func<object>? _itemFilterMatches; // v0.31 Prospector: /api/item-filters/matches counter
    private readonly Func<object>? _panelState;              // v0.32 Panorama: /api/panels provider (character/inventory/stash open state)
    private readonly Func<object>? _dropsProvider;           // v0.33 Drop Timeline: /api/drops payload
    private readonly Func<string, object>? _codexProvider;   // v0.37 Character Codex: /api/codex?character=<name>
    // v0.36 W1: user icon registry (FileSystemWatcher over config/icons/). Null when unconfigured.
    private readonly IconRegistry? _iconRegistry;
    // v0.39 Rules Engine: config directory for rules.json persistence.
    private readonly string _rulesConfigDir;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public ApiServer(
        Func<RadarState> state,
        RadarSettings settings,
        Func<IReadOnlyList<(string Id, int Slot)>> navGet,
        Action<string> navToggle,
        Action navClear,
        HiddenEntities hidden,
        DisplayRules displayRules,
        LandmarkStore landmarkStore,
        Func<IReadOnlyList<string>> tilesProvider,
        Func<IReadOnlyList<string>> knownModsProvider,
        CampaignObjectives objectives,
        Func<IReadOnlyList<SeenPoi>> seenPoisProvider,
        Func<IReadOnlyList<AtlasEntry>> entityAtlasProvider,
        EntityNameStore entityNames,
        Func<object> gearProvider,
        Func<object> preloadProvider,
        Func<object> buffsDiagProvider,
        GearWeightStore gearWeights,
        Func<object>? atlasProvider = null,
        Action<IReadOnlyList<long>>? atlasSelect = null,
        Action<IReadOnlyList<(string tag, string color, bool track, bool nav, bool arrow)>>? atlasHighlight = null,
        Func<object>? versionProvider = null,
        Action? rescan = null,
        Action<string>? audioTest = null,
        Action? rebuildAudio = null,
        PresetStore? presetStore = null,
        Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)>? terrainProvider = null,
        bool allowLanAccess = false,
        int port = 7777,
        SseChannel? sse = null,
        AssetHost? assetHost = null,
        POE2Radar.Core.Campaign.Probe.EventWriter? traceWriter = null,
        // v0.30 Instinct: per-character boss wipe log (persistent) — served at /api/wipe-log to
        // populate the "Your wipes" dashboard card. Null → the endpoint returns an empty payload.
        Func<object>? wipeLogProvider = null,
        // v0.31 Prospector: user's item filter ruleset (persistent) — served at /api/item-filters.
        // Null → the endpoint returns an empty envelope. The matches provider counts items matching
        // any filter on the ground / equipped / inventory surfaces for the dashboard card display.
        ItemFilterEngine? itemFilters = null,
        Func<object>? itemFilterMatchesProvider = null,
        Func<object>? panelStateProvider = null,
        Func<object>? dropsProvider = null,
        // v0.36 W1: user icon registry for the /api/user-icons manifest endpoint.
        IconRegistry? iconRegistry = null,
        // v0.37 A1: character-codex event journal reader. Called with the ?character=<name> query
        // param; returns the JSON-shaped payload for that character (empty envelope if unknown).
        Func<string, object>? codexProvider = null,
        // v0.39 Rules Engine: config directory for rules.json persistence.
        string? rulesConfigDir = null)
    {
        _state = state;
        _atlas = atlasProvider;
        _atlasSelect = atlasSelect;
        _atlasHighlight = atlasHighlight;
        _version = versionProvider;
        _rescan = rescan;
        _audioTest = audioTest;
        _rebuildAudio = rebuildAudio;
        _settings = settings;
        _navGet = navGet;
        _navToggle = navToggle;
        _navClear = navClear;
        _hidden = hidden;
        _displayRules = displayRules;
        _landmarkStore = landmarkStore;
        _tiles = tilesProvider;
        _knownMods = knownModsProvider;
        _objectives = objectives;
        _seenPois = seenPoisProvider;
        _entityAtlasEntries = entityAtlasProvider;
        _entityNames = entityNames;
        _gear = gearProvider;
        _preload = preloadProvider;
        _buffsDiag = buffsDiagProvider;
        _gearWeights = gearWeights;
        _presetStore = presetStore ?? new PresetStore();
        _terrainProvider = terrainProvider;
        _allowLanAccess = allowLanAccess;
        _port = port;
        _sse = sse;
        _assetHost = assetHost;
        _traceWriter = traceWriter;
        _wipeLog = wipeLogProvider;
        _itemFilters = itemFilters;
        _itemFilterMatches = itemFilterMatchesProvider;
        _panelState = panelStateProvider;
        _dropsProvider = dropsProvider;
        _iconRegistry = iconRegistry;
        _codexProvider = codexProvider;
        _rulesConfigDir = rulesConfigDir ?? Path.Combine(AppContext.BaseDirectory, "config");
        _listener.Prefixes.Add(ApiPrefix.Build(allowLanAccess, port));
    }

    public void Start()
    {
        try { _listener.Start(); }
        catch (HttpListenerException) when (_allowLanAccess)
        {
            // Binding all interfaces (http://+:port) failed — usually missing admin rights / no urlacl
            // reservation. Fall back to loopback so the local dashboard still works; surfaced to the
            // user via /api/lan-info (bindFailed).
            _lanBindFailed = true;
            Console.Error.WriteLine($"[LAN] http://+:{_port}/ bind failed — falling back to loopback-only. " +
                "Open the dashboard's Remote Access (LAN) card for details (try running as administrator).");
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(ApiPrefix.Build(false, _port));
            _listener.Start();
        }
        _running = true;
        // v0.20.0 T6: LoopAsync now uses GetContextAsync + Task.Run per request, so /stream no longer
        // serializes the loop and other requests can interleave.
        _loopTask = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_running)
        {
            try
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) when (!_running) { return; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(async () =>
                {
                    try { await Handle(ctx).ConfigureAwait(false); }
                    catch (System.Exception ex)
                    {
                        // Don't take the loop down on a per-request fault.
                        try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
                        Console.Error.WriteLine($"api: {ex.Message}");
                    }
                });
            }
            catch (Exception ex) when (_running)
            {
                Console.Error.WriteLine($"api: accept-loop transient fault: {ex.Message}");
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var q = ctx.Request.QueryString;
        var s = _state();

        switch (path)
        {
            case "/":
                WriteHtml(ctx, DashboardHtml.Page);
                break;

            case "/obs":
                // v0.20.0 T5: gated on EnableWebObs + assetHost — both null when the toggle is off,
                // so this arm 404s without touching `_assetHost` (the ?. is defence-in-depth for RadarApp
                // wiring drift, not a null-safety hedge on the runtime contract).
                // v0.35: ?mode=safe branches into the SafeObsTransform (client-side redaction pipeline);
                // no server-side data reduction — SseChannel keeps its normal firehose. Optional ?delay=<sec>
                // overrides RadarSettings.WebObsSafeDelaySec, clamped [0, 600].
                if (!_settings.EnableWebObs || _assetHost == null) { NotFound(ctx); break; }
                var mode = ctx.Request.QueryString["mode"];
                if (string.Equals(mode, "safe", System.StringComparison.OrdinalIgnoreCase))
                {
                    int delaySec = _settings.WebObsSafeDelaySec;
                    var delayParam = ctx.Request.QueryString["delay"];
                    if (int.TryParse(delayParam, out var qd)) delaySec = System.Math.Clamp(qd, 0, 600);
                    var safe = new POE2Radar.Overlay.Web.SafeModeOptions(
                        DelaySec:       delaySec,
                        MaskZoneName:   _settings.WebObsSafeMaskZoneName,
                        HideoutBlur:    _settings.WebObsSafeHideoutBlur,
                        EntityNameFog:  _settings.WebObsSafeEntityNameFog);
                    _assetHost.ServeObs(ctx, safe);
                }
                else
                {
                    _assetHost.ServeObs(ctx);
                }
                break;

            case "/map":
                if (!_settings.EnableWebMap || _assetHost == null) { NotFound(ctx); break; }
                _assetHost.ServeMap(ctx);
                break;

            case "/stream":
                // Push channel for /map + /obs. Gated on EITHER toggle so a user who only enables /obs
                // still gets 30 Hz updates. `_sse` is null when both are off — the check short-circuits
                // BEFORE the null-forgiving await so we never dereference a null SseChannel.
                if ((!_settings.EnableWebMap && !_settings.EnableWebObs) || _sse == null) { NotFound(ctx); break; }
                await _sse.HandleSubscribe(ctx).ConfigureAwait(false);
                break;

            case "/health":
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, inGame = s.InGame }, Json));
                break;

            case "/state":
            {
                var counts = s.Entities.GroupBy(e => e.Category)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    // Character name + level intentionally omitted (privacy: this endpoint is local
                    // but unauthenticated, and screenshots/streams shouldn't leak the character).
                    s.InGame, areaCode = s.AreaCode, areaHash = s.AreaHash, areaLevel = s.AreaLevel,
                    areaName = ZoneGuide.Shared.FriendlyName(s.AreaCode),
                    areaAct = ZoneGuide.Shared.Area(s.AreaCode)?.Act ?? 0,
                    mapVisible = s.MapVisible, zoom = s.Zoom,
                    hpPct = s.HpPct, manaPct = s.ManaPct, esPct = s.EsPct,
                    player = new { x = s.Player.X, y = s.Player.Y },
                    entityCount = s.Entities.Count,
                    poiCount = s.Entities.Count(e => e.Poi),
                    landmarkCount = s.Landmarks.Count,
                    counts,
                    worldMs = s.WorldMs, renderMs = s.RenderMs, fps = s.Fps, rpmPerSec = s.RpmPerSec,
                    healthState = s.Health.ToString().ToLowerInvariant(),
                    healthMessage = s.HealthMessage,
                    // Runeshape monoliths in the area (slot count + anchor + priced reward set) for the
                    // dashboard's Monolith Rewards card. Rewards are pre-sorted by value, server-side.
                    monoliths = (s.Monoliths ?? Array.Empty<MonolithMarker>()).Select(m => new
                    {
                        holes = m.Holes, unique = m.IsUnique, collected = m.Collected, anchor = m.AnchorName,
                        bestEx = m.BestEx, bestName = m.BestName, color = m.Color,
                        rewards = m.Rewards.Select(r => new { name = r.Name, count = r.Count, ex = r.Ex, size = r.Size, runes = r.Runes }),
                    }),
                    // Objective Director: active objective + queue for the dashboard panel (read-only).
                    director = (s.Director ?? Array.Empty<POE2Radar.Core.Campaign.RankedObjective>())
                        .Select(o => new { id = o.Id, label = o.Label, category = o.Category,
                                           priority = o.Priority, tier = o.Tier.ToString() }),
                    campaignGps = s.CampaignGps,
                    // v0.21 EC2 Guided Campaign — additive. Null when EnableCampaignGps=false, the
                    // embedded route failed to load, or the cursor walked off the end. v0.20.x dashboards
                    // never read this key; the JS in DashboardHtml.cs guards on `state.campaignGuide`
                    // before touching the DOM, so hiding it is free when off.
                    campaignGuide = s.CampaignGuide is null ? (object?)null : new
                    {
                        stepId            = s.CampaignGuide.Value.StepId,
                        text              = s.CampaignGuide.Value.Text,
                        areaId            = s.CampaignGuide.Value.AreaId,
                        act               = s.CampaignGuide.Value.Act,
                        ordinal           = s.CampaignGuide.Value.Ordinal,
                        totalSteps        = s.CampaignGuide.Value.TotalSteps,
                        optional          = s.CampaignGuide.Value.Optional,
                        stalled           = s.CampaignGuide.Value.Stalled,
                        available         = s.CampaignGuide.Value.Available,
                        degradationReason = s.CampaignGuide.Value.DegradationReason,
                    },
                    // Session HUD: elapsed times, zone pace, deaths. Null when tracker not running.
                    session = s.Session == null ? (object?)null : new {
                        sessionElapsed    = FormatTimeSpan(s.Session.SessionElapsed),
                        zoneElapsed       = FormatTimeSpan(s.Session.ZoneElapsed),
                        zonesEntered      = s.Session.ZonesEntered,
                        zonesPerHour      = s.Session.ZonesPerHour,
                        currentZoneName   = s.Session.CurrentZoneName,
                        currentAreaLevel  = s.Session.CurrentAreaLevel,
                        deaths            = s.Session.Deaths,
                        deathsThisZone    = s.Session.DeathsThisZone,
                        // v2 session fields: kill breakdown, maps/hr pace, XP level-delta.
                        killsNormal       = s.Session.KillsNormal,
                        killsMagic        = s.Session.KillsMagic,
                        killsRare         = s.Session.KillsRare,
                        killsUnique       = s.Session.KillsUnique,
                        mapsPerHour       = s.Session.MapsPerHour,
                        xpEfficiency      = s.Session.XpEfficiency,
                    },
                }, Json));
                break;
            }

            case "/api/icons":
            {
                // Read-only icon library for the dashboard's icon picker previews (name + viewBox + paths).
                var icons = IconLibrary.Ordered.Select(d => new { name = d.Name, viewBox = d.ViewBox, paths = d.Paths });
                Write(ctx, 200, JsonSerializer.Serialize(icons, Json));
                break;
            }

            case "/api/user-icons":
            {
                // v0.36 W1: user icon manifest for map.js (and dashboard preview).
                // Data source is IconRegistry (I1) which owns disk I/O + FileSystemWatcher
                // over config/icons/. ETag is keyed on snapshot version so a file drop
                // bumps the version and forces map.js to refetch.
                var snap = _iconRegistry?.Current ?? IconRegistry.Snapshot.Empty;
                var version = snap.Version;
                var etag = "\"sha1-user-icons-v" + version + "\"";
                var inm = ctx.Request.Headers["If-None-Match"];
                if (!string.IsNullOrEmpty(inm) && inm == etag)
                {
                    ctx.Response.StatusCode = 304;
                    ctx.Response.Headers["ETag"] = etag;
                    ctx.Response.Close();
                    break;
                }
                var payload = snap.Icons.Values.Select(e => new
                {
                    name          = e.Name,
                    category      = (string?)null,
                    rarity        = (string?)null,
                    metadataGlob  = (string?)null,
                    dataUri       = "data:image/png;base64," + Convert.ToBase64String(e.PngBytes),
                });
                ctx.Response.Headers["ETag"] = etag;
                Write(ctx, 200, JsonSerializer.Serialize(payload, Json));
                break;
            }

            case "/api/lan-info":
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    port = _port,
                    bound = (_allowLanAccess && !_lanBindFailed) ? "lan" : "localhost",
                    bindFailed = _lanBindFailed,
                    addresses = LanAddresses(),
                }, Json));
                break;

            case "/landmarks":
            {
                // v0.20.0 T5: gate on EITHER toggle. map.js (shared by /map and /obs) fetches this to
                // draw label pins, so enabling only /obs must not 404 it.
                if (!_settings.EnableWebMap && !_settings.EnableWebObs) { NotFound(ctx); break; }
                var list = s.Landmarks
                    .OrderBy(l => Dist(l.Center, s.Player))
                    .Select(l => new
                    {
                        name = l.Name, curatedName = l.CuratedName, path = l.Path, tiles = l.TileCount,
                        x = l.Center.X, y = l.Center.Y, dist = (int)Dist(l.Center, s.Player),
                    });
                var json = JsonSerializer.SerializeToUtf8Bytes(list, Json);
                WriteMaybeGzipped(ctx, json, "application/json; charset=utf-8");
                break;
            }

            case "/entities":
            {
                var category = q["category"];
                var aliveOnly = string.Equals(q["alive"], "true", StringComparison.OrdinalIgnoreCase);
                _ = float.TryParse(q["radius"], out var radius);
                _ = int.TryParse(q["limit"], out var limit);
                if (limit <= 0) limit = 500;

                IEnumerable<Poe2Live.EntityDot> q2 = s.Entities;
                if (!string.IsNullOrEmpty(category))
                    q2 = q2.Where(e => string.Equals(e.Category.ToString(), category, StringComparison.OrdinalIgnoreCase));
                if (aliveOnly) q2 = q2.Where(e => e.HpCur > 0);
                if (radius > 0) q2 = q2.Where(e => Dist(e.Grid, s.Player) <= radius);

                var list = q2
                    .OrderBy(e => Dist(e.Grid, s.Player))
                    .Take(limit)
                    .Select(e => new
                    {
                        id = e.Id, category = e.Category.ToString(), metadata = e.Metadata,
                        name = EntityNameResolver.Shared.ResolveOrShorten(e.Metadata),
                        poi = e.Poi, iconComplete = e.IconComplete, opened = e.Opened, reaction = e.Reaction, friendly = e.IsFriendly, rarity = e.Rarity.ToString(),
                        mods = e.ModList, itemArt = e.ItemArt, itemName = e.ItemName,
                        x = e.Grid.X, y = e.Grid.Y, hpCur = e.HpCur, hpMax = e.HpMax,
                        alive = e.HpMax <= 0 || e.HpCur > 0,
                        dist = (int)Dist(e.Grid, s.Player),
                    });
                Write(ctx, 200, JsonSerializer.Serialize(list, Json));
                break;
            }

            case "/api/settings":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(ReadSettings(), Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    // CSRF / DNS-rebinding guard: only honor writes whose Host header is the loopback
                    // name we bind to. A page on another origin that rebinds DNS to 127.0.0.1 still
                    // sends its own hostname in Host, so this rejects drive-by writes. (Reads are
                    // already unreadable cross-origin since we emit no Access-Control-Allow-Origin.)
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    var applied = ApplySettings(ReadBody(ctx));
                    var restartKeys = new[] { "allowLanAccess", "enableWebMap", "enableWebObs", "updateChannel", "updateUrl" };
                    var restartRequired = System.Array.FindAll(applied, k => System.Array.IndexOf(restartKeys, k) >= 0);
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, applied, restartRequired, settings = ReadSettings() }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/nav":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(new { selected = NavSelection() }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    // Same CSRF / DNS-rebinding guard as POST /api/settings: only honor writes whose
                    // Host header is our loopback name. (This is draw-only selection — it never sends
                    // input to the game — but we still gate it so a cross-origin page can't drive it.)
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    ApplyNav(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, selected = NavSelection() }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/rescan":
            {
                if (ctx.Request.HttpMethod != "POST")
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                    break;
                }
                if (!IsLoopbackHost(ctx.Request))
                {
                    Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                    break;
                }
                _rescan?.Invoke();
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true }, Json));
                break;
            }

            case "/api/quickstart/dismiss":
            {
                // POST-only, loopback-gated. Marks the first-run card as seen without applying settings.
                if (ctx.Request.HttpMethod != "POST")
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                    break;
                }
                if (!IsLoopbackHost(ctx.Request))
                {
                    Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                    break;
                }
                _settings.FirstRunSeen = true;
                _settings.Save();
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true }, Json));
                break;
            }

            case "/api/quickstart/apply":
            {
                // POST-only, loopback-gated. Applies the recommended setup bundle and marks the card seen.
                // The four keys (zoneSummaryEnabled, hpBarRare, hpBarUnique, groundItems) are all whitelisted
                // in ApplySettings — no game write, no input.
                if (ctx.Request.HttpMethod != "POST")
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                    break;
                }
                if (!IsLoopbackHost(ctx.Request))
                {
                    Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                    break;
                }
                ApplySettings("{\"zoneSummaryEnabled\":true,\"hpBarRare\":true,\"hpBarUnique\":true,\"groundItems\":{\"enabled\":true,\"categories\":[\"Uniques\",\"Currency\",\"Runes\",\"SoulCores\"]}}");
                _settings.FirstRunSeen = true;
                _settings.Save();
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true }, Json));
                break;
            }

            case "/api/audio-test":
            {
                // POST-only, loopback-gated. Triggers a local audio cue for dashboard test buttons.
                // Only plays output sound — no game input, no injection.
                if (ctx.Request.HttpMethod != "POST")
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                    break;
                }
                if (!IsLoopbackHost(ctx.Request))
                {
                    Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                    break;
                }
                var audioBody = ReadBody(ctx);
                string cue = "";
                if (!string.IsNullOrWhiteSpace(audioBody))
                {
                    try
                    {
                        using var audioDoc = JsonDocument.Parse(audioBody);
                        if (audioDoc.RootElement.TryGetProperty("cue", out var cueEl))
                            cue = cueEl.GetString() ?? "";
                    }
                    catch { /* malformed body — cue stays empty */ }
                }
                _audioTest?.Invoke(cue);
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true }, Json));
                break;
            }

            case "/api/hidden":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(new { patterns = _hidden.All }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    // Same CSRF / DNS-rebinding guard as the other writes: loopback Host only.
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    ApplyHidden(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, patterns = _hidden.All }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/zone":
            {
                // Static zone reference for the current area: friendly name, act/level, flags, and
                // optional leveling notes (zone-specific, else act fallback). Read-only.
                var area = ZoneGuide.Shared.Area(s.AreaCode);
                var notes = ZoneGuide.Shared.Notes(s.AreaCode);
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    code = s.AreaCode,
                    name = ZoneGuide.Shared.FriendlyName(s.AreaCode),
                    act = area?.Act ?? 0,
                    level = area?.Level ?? s.AreaLevel,
                    waypoint = area?.Waypoint ?? false,
                    town = area?.Town ?? false,
                    title = notes?.Title ?? "",
                    notes = notes?.Notes ?? "",
                }, Json));
                break;
            }

            case "/api/tiles":
                // Distinct terrain-tile paths in the current area — the add-rule picker browses these
                // so a Tile rule can target any tile. Read-only.
                Write(ctx, 200, JsonSerializer.Serialize(new { tiles = _tiles() }, Json));
                break;

            case "/api/mods":
                // Every monster affix-mod id ever seen (persistent catalog) — the add-rule picker browses
                // these so a Mods matcher can target any known aura/buff. Read-only.
                Write(ctx, 200, JsonSerializer.Serialize(new { mods = _knownMods() }, Json));
                break;

            case "/api/seen-pois":
                // Distinct notable entities/landmarks seen this session, each tagged whether the
                // Director catalog already covers it (uncatalogued ones are the worklist). Read-only.
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    pois = _seenPois().Select(p =>
                    {
                        var guess = ObjectiveClassifier.Classify(p.Metadata, p.Category, p.Poi, p.Rarity);
                        return new
                        {
                            signature = p.Signature, name = p.FriendlyName, category = p.Category,
                            zone = p.FirstZone, count = p.Count, poi = p.Poi,
                            metadata = p.Metadata, landmarkPath = p.LandmarkPath,
                            covered = _objectives.Covers(p),
                            guessedTier     = guess?.Tier.ToString(),
                            guessedCategory = guess?.SuggestedCategory,
                            guessedConf     = guess?.Confidence.ToString(),
                        };
                    }),
                }, Json));
                break;

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
                        var guess = ObjectiveClassifier.Classify(a.Metadata, a.Category, a.Poi, a.Rarity);
                        return new
                        {
                            metadata = a.Metadata,
                            name = named ?? EntityNameResolver.Shared.ResolveOrShorten(a.Metadata),
                            named = named != null,
                            category = a.Category, rarity = a.Rarity, poi = a.Poi,
                            zone = a.FirstZone, count = a.Count,
                            notable = PoiCandidate.IsCandidate(in e),
                            covered = _objectives.Covers(in e),
                            guessedTier     = guess?.Tier.ToString(),
                            guessedCategory = guess?.SuggestedCategory,
                            guessedConf     = guess?.Confidence.ToString(),
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

            case "/api/contribute":
            {
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                var url = _settings.ContributeUrl?.Trim() ?? "";
                if (url.Length == 0) { Write(ctx, 400, JsonSerializer.Serialize(new { error = "no contribute url configured" }, Json)); break; }
                // The same non-identifying pack as /api/entity-atlas/export — names + objectives only.
                var pack = JsonSerializer.Serialize(new { names = _entityNames.All, objectives = _objectives.All }, Json);
                var (ok, status) = ContributeForward(SiblingContributeUrl(url, "atlas"), pack).GetAwaiter().GetResult();
                Write(ctx, ok ? 200 : 502, JsonSerializer.Serialize(new { ok, status }, Json));
                break;
            }

            case "/api/contribute-buffs":
            {
                // v0.21 CF-DASH-BUTTONS: one-click submission of the observed buff-id list to the
                // Worker's /submit-buffs sibling route. Loopback-Host-gated (mirror of /api/contribute).
                // Non-identifying — the same {id,tier} projection that /api/buffs already exposes.
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                var url = _settings.ContributeUrl?.Trim() ?? "";
                if (url.Length == 0) { Write(ctx, 400, JsonSerializer.Serialize(new { error = "no contribute url configured" }, Json)); break; }
                var pack = JsonSerializer.Serialize(new { buffs = BuildBuffsPack() }, Json);
                var (ok, status) = ContributeForward(SiblingContributeUrl(url, "buffs"), pack).GetAwaiter().GetResult();
                Write(ctx, ok ? 200 : 502, JsonSerializer.Serialize(new { ok, status }, Json));
                break;
            }

            case "/api/contribute-preload":
            {
                // v0.21 CF-DASH-BUTTONS: one-click submission of the Diagnostic-mode preload path
                // frequency table to the Worker's /submit-preload sibling route. Loopback-Host-gated.
                // Non-identifying — metadata paths + zone frequency only (bare .dds/.ao are Worker-rejected).
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                var url = _settings.ContributeUrl?.Trim() ?? "";
                if (url.Length == 0) { Write(ctx, 400, JsonSerializer.Serialize(new { error = "no contribute url configured" }, Json)); break; }
                var pack = JsonSerializer.Serialize(new { preloads = BuildPreloadsPack() }, Json);
                var (ok, status) = ContributeForward(SiblingContributeUrl(url, "preload"), pack).GetAwaiter().GetResult();
                Write(ctx, ok ? 200 : 502, JsonSerializer.Serialize(new { ok, status }, Json));
                break;
            }

            case "/api/contribute-trace":
            {
                // Task 7 PROBE-CONTRIBUTE: one-click submission of one boot's worth of anonymized
                // campaign-probe events to the Worker's /submit-trace sibling route. Loopback-Host-
                // gated (mirror of /api/contribute-{atlas,buffs,preload}). No PII beyond
                // install_uuid + boot_id per spec §2. Zero-cost-when-off short-circuit hits BEFORE
                // any file I/O so the probe-disabled path allocates nothing here either.
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                if (!_settings.EnableCampaignProbe || _traceWriter == null)
                { Write(ctx, 400, JsonSerializer.Serialize(new { error = "campaign probe disabled" }, Json)); break; }
                var url = _settings.ContributeUrl?.Trim() ?? "";
                if (url.Length == 0) { Write(ctx, 400, JsonSerializer.Serialize(new { error = "no contribute url configured" }, Json)); break; }
                var installUuid = _settings.ProbeInstallId?.Trim() ?? "";
                if (installUuid.Length != 36) { Write(ctx, 400, JsonSerializer.Serialize(new { error = "no install uuid" }, Json)); break; }

                // Flush the async writer so we read a complete boot file, then pick current-or-most-recent.
                _traceWriter.FlushSync();
                var tracePath = SelectTraceFileForContribute(
                    _traceWriter.CurrentFilePath,
                    _traceWriter.EventsWritten,
                    _traceWriter.MostRecentCompletePath());
                if (tracePath == null || !System.IO.File.Exists(tracePath))
                { Write(ctx, 400, JsonSerializer.Serialize(new { error = "no trace to contribute" }, Json)); break; }

                var jsonlBytes = System.IO.File.ReadAllBytes(tracePath);
                if (jsonlBytes.Length == 0)
                { Write(ctx, 400, JsonSerializer.Serialize(new { error = "trace file empty" }, Json)); break; }

                // Count events = newline count (JSONL is one record per line). Cheap + accurate.
                long eventCount = 0;
                for (int i = 0; i < jsonlBytes.Length; i++) if (jsonlBytes[i] == (byte)'\n') eventCount++;

                var pack = BuildTracePack(installUuid, _traceWriter.CurrentBootId, eventCount, jsonlBytes);
                var packBytes = System.Text.Encoding.UTF8.GetByteCount(pack);
                // 256 KB Worker MAX_BYTES (spec §11 file-size risk). Enforced here to give the user a
                // clean 413 with the file path so they know which boot was too big to share.
                if (packBytes > 262144)
                { Write(ctx, 413, JsonSerializer.Serialize(new { error = "payload too large after gzip", bytes = packBytes, path = tracePath }, Json)); break; }

                var (ok, status) = ContributeForward(SiblingContributeUrl(url, "trace"), pack).GetAwaiter().GetResult();
                Write(ctx, ok ? 200 : 502, JsonSerializer.Serialize(new { ok, status, event_count = eventCount, bytes = packBytes }, Json));
                break;
            }

            case "/api/probe/reset-install-id":
            {
                // Task 7 PROBE-CONTRIBUTE: dashboard "Reset trace session id" button. Loopback-Host-
                // gated per audit finding #12 — a stable install_uuid is the ONLY correlation handle
                // between contributed traces, so the reset MUST NOT be exposed to LAN peers. Delegates
                // to RadarSettings.ResetTraceSession() (Task 5), which mints a fresh v4 UUID and
                // persists synchronously. The response returns the new uuid so the dashboard can
                // update its display without a follow-up GET.
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                _settings.ResetTraceSession();
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, new_install_uuid = _settings.ProbeInstallId }, Json));
                break;
            }

            case "/api/gear":
                // God-Roll Detector (experimental, default off): the scored inventory snapshot. Item stats
                // + scores only — no character/account data. {enabled:false,items:[]} when off.
                Write(ctx, 200, JsonSerializer.Serialize(_gear(), Json));
                break;

            case "/api/preload":
                // Preload Alert (experimental): current zone's hit list + (when Diagnostic) the path
                // frequency table. GET-only; paths + hit counts only — no character/account data.
                if (ctx.Request.HttpMethod != "GET") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                Write(ctx, 200, JsonSerializer.Serialize(_preload(), Json));
                break;

            case "/api/gear-weights":
            {
                if (ctx.Request.HttpMethod == "GET") { Write(ctx, 200, JsonSerializer.Serialize(_gearWeights.View(), Json)); }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                    ApplyGearWeights(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, weights = _gearWeights.View() }, Json));
                }
                else { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); }
                break;
            }

            case "/api/version":
                // This build's version + latest known on GitHub + download URL (for the update banner).
                Write(ctx, 200, JsonSerializer.Serialize(_version?.Invoke() ?? new { current = "?", latest = (string?)null, updateAvailable = false, url = "" }, Json));
                break;

            case "/api/about":
                // EC2 (ExileCampaigns2) attribution surface — mirrored from DashboardHtml constants
                // so the DRAFT-phase sentinels flow through a single source of truth. EC2-UI (Task 6)
                // echoes these fields into the SSE `CampaignGuide` payload; EC2-ATTR-FORMALIZE
                // grep-and-swaps the two `TODO(syrairc-*)` sentinels for the real values.
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    campaignGuideAttribution = DashboardHtml.CampaignGuideAttribution,
                    campaignGuideUpstream    = DashboardHtml.CampaignGuideUpstreamUrl,
                    campaignGuideLicense     = DashboardHtml.CampaignGuideLicense, // TODO(syrairc-license)
                    campaignGuideCommit      = DashboardHtml.CampaignGuideCommit,  // TODO(syrairc-hash)
                }, Json));
                break;

            case "/api/atlas":
                // v0.20.0 T5: same OR-gate as /landmarks — map.js needs atlas data whether /map or /obs
                // is the entry point. Off when both toggles are off.
                if (!_settings.EnableWebMap && !_settings.EnableWebObs) { NotFound(ctx); break; }
                // Inspection view of the atlas map-data we can read (catalog + current-region map set).
                // Read-only; the provider scans + caches, so the first call after entering the atlas
                // may take a moment. Returns {located:false,...} when the catalog can't be found.
                {
                    var json = JsonSerializer.SerializeToUtf8Bytes(_atlas?.Invoke() ?? new { located = false, note = "atlas reader unavailable" }, Json);
                    WriteMaybeGzipped(ctx, json, "application/json; charset=utf-8");
                }
                break;

            case "/api/atlas-select":
            {
                // Set which atlas nodes (by element address) to highlight in-game. Draw-only; loopback-gated.
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                var els = new List<long>();
                try
                {
                    using var doc = JsonDocument.Parse(ReadBody(ctx));
                    if (doc.RootElement.TryGetProperty("els", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var e in arr.EnumerateArray())
                            if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var v)) els.Add(v);
                            else if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) els.Add(n);
                }
                catch (JsonException) { }
                _atlasSelect?.Invoke(els);
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, count = els.Count }, Json));
                break;
            }

            case "/api/atlas-highlight":
            {
                // Set the active atlas highlight rules (content tags). Only matching nodes draw in-game.
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                var rules = new List<(string tag, string color, bool track, bool nav, bool arrow)>();
                try
                {
                    using var doc = JsonDocument.Parse(ReadBody(ctx));
                    // "rules":[{ "tag":"…", "color":"#RRGGBB", "track":true, "nav":false, "arrow":false }].
                    if (doc.RootElement.TryGetProperty("rules", out var rs) && rs.ValueKind == JsonValueKind.Array)
                        foreach (var r in rs.EnumerateArray())
                        {
                            var tg = r.TryGetProperty("tag", out var tv) ? tv.GetString() : null;
                            var col = r.TryGetProperty("color", out var cv) ? cv.GetString() : null;
                            var track = !r.TryGetProperty("track", out var tk) || tk.ValueKind != JsonValueKind.False; // default true
                            var nav = r.TryGetProperty("nav", out var nv) && nv.ValueKind == JsonValueKind.True;
                            var arrow = r.TryGetProperty("arrow", out var aw) && aw.ValueKind == JsonValueKind.True;
                            if (!string.IsNullOrEmpty(tg)) rules.Add((tg!, col ?? "", track, nav, arrow));
                        }
                    else if (doc.RootElement.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var t in arr.EnumerateArray())
                            if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } tg) rules.Add((tg, "", true, false, false));
                }
                catch (JsonException) { }
                _atlasHighlight?.Invoke(rules);
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, count = rules.Count }, Json));
                break;
            }

            case "/api/landmarks":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    // ?export=1 → the effective merged table as a clean JSON (for download / submission).
                    if (ctx.Request.QueryString["export"] != null)
                        Write(ctx, 200, _landmarkStore.ExportJson());
                    else
                        Write(ctx, 200, JsonSerializer.Serialize(new { entries = _landmarkStore.All() }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    ApplyLandmarks(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, entries = _landmarkStore.All() }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/display-rules":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(new { rules = _displayRules.All }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    ApplyDisplayRules(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, rules = _displayRules.All }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/objectives":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(new { objectives = _objectives.All }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                    ApplyObjectives(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, objectives = _objectives.All }, Json));
                }
                else { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); }
                break;
            }

            case "/api/dynasty-maps":
                // Read-only reference: the curated dynasty-support map table (no identifying data).
                Write(ctx, 200, JsonSerializer.Serialize(
                    POE2Radar.Core.Game.DynastyMaps.Shared.All.Select(kv => new
                    {
                        code = kv.Key, name = kv.Value.Name, boss = kv.Value.Boss, gems = kv.Value.Gems
                    }), Json));
                break;

            case "/api/labels":
                // Read-only: the curated classification label vocabulary (grouped). No identifying data.
                Write(ctx, 200, JsonSerializer.Serialize(LabelVocabulary.Shared.Groups, Json));
                break;

            case "/api/preset/export":
                // Read-only: build the live PresetBundle (visual config + display rules) and return it
                // as both a human-readable JSON blob and a compact share-code. No identity/operational
                // fields — only what PresetBundle captures (Styles/HpBars/Terrain/GroundItems/bools/rules).
                Write(ctx, 200, JsonSerializer.Serialize(BuildPresetExport(), Json));
                break;

            case "/api/preset/import":
            {
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                // 2 MB cap on the untrusted community share-code. Check the declared length first, then
                // the actual body length — ContentLength64 is -1 for chunked requests, so the post-read
                // guard is what actually enforces the cap regardless of transfer encoding.
                if (ctx.Request.ContentLength64 > 2_000_000) { Write(ctx, 413, JsonSerializer.Serialize(new { error = "request too large" }, Json)); break; }
                var presetBody = ReadBody(ctx);
                if (presetBody.Length > 2_000_000) { Write(ctx, 413, JsonSerializer.Serialize(new { error = "request too large" }, Json)); break; }
                ApplyPresetImport(presetBody);
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, rules = _displayRules.All.Count }, Json));
                break;
            }

            case "/api/preset/list":
                // Read-only: returns built-in presets (builtIn=true) first, then user presets sorted by
                // name, excluding the auto-backup. Consistent with /api/preset/export (no loopback gate).
                Write(ctx, 200, JsonSerializer.Serialize(new { presets = _presetStore.List() }, Json));
                break;

            case "/api/preset/save":
            {
                // POST + loopback only. Builds the current look server-side (client supplies only {name}).
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                string saveBody = ReadBody(ctx);
                string saveName;
                try
                {
                    using var doc = JsonDocument.Parse(saveBody);
                    saveName = doc.RootElement.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                }
                catch { Write(ctx, 400, JsonSerializer.Serialize(new { error = "malformed JSON" }, Json)); break; }
                var safeName = PresetName.Sanitize(saveName);
                if (safeName == PresetName.Fallback && string.IsNullOrWhiteSpace(saveName))
                {
                    Write(ctx, 400, JsonSerializer.Serialize(new { error = "name is required" }, Json)); break;
                }
                if (_presetStore.IsBuiltIn(safeName)) { Write(ctx, 409, JsonSerializer.Serialize(new { error = "name conflicts with a built-in" }, Json)); break; }
                var bundleJson = BuildPresetBundleJson(safeName);
                _presetStore.Save(safeName, bundleJson);
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, name = safeName }, Json));
                break;
            }

            case "/api/preset/apply":
            {
                // POST + loopback only. Body: {name}. Applies the named preset (built-in or user).
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                string applyBody = ReadBody(ctx);
                string applyName;
                try
                {
                    using var doc = JsonDocument.Parse(applyBody);
                    applyName = doc.RootElement.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                }
                catch { Write(ctx, 400, JsonSerializer.Serialize(new { error = "malformed JSON" }, Json)); break; }
                if (string.IsNullOrWhiteSpace(applyName)) { Write(ctx, 400, JsonSerializer.Serialize(new { error = "name is required" }, Json)); break; }
                if (!_presetStore.TryGet(applyName, out var presetJson)) { Write(ctx, 404, JsonSerializer.Serialize(new { error = "preset not found" }, Json)); break; }
                // ApplyPresetImport accepts raw bundle JSON (takes the non-code branch automatically).
                ApplyPresetImport(presetJson);
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, rules = _displayRules.All.Count }, Json));
                break;
            }

            case "/api/preset/delete":
            {
                // POST + loopback only. Body: {name}. Refuses built-ins.
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                string deleteBody = ReadBody(ctx);
                string deleteName;
                try
                {
                    using var doc = JsonDocument.Parse(deleteBody);
                    deleteName = doc.RootElement.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                }
                catch { Write(ctx, 400, JsonSerializer.Serialize(new { error = "malformed JSON" }, Json)); break; }
                if (string.IsNullOrWhiteSpace(deleteName)) { Write(ctx, 400, JsonSerializer.Serialize(new { error = "name is required" }, Json)); break; }
                if (_presetStore.IsBuiltIn(deleteName)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "cannot delete a built-in preset" }, Json)); break; }
                _presetStore.Delete(deleteName);
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true }, Json));
                break;
            }

            case "/api/keybinds":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(GetKeybindsList(), Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    var kbBody = ReadBody(ctx);
                    string? kbAction = null;
                    int kbVk = 0;
                    try
                    {
                        using var doc = JsonDocument.Parse(kbBody);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("action", out var av)) kbAction = av.GetString();
                        if (root.TryGetProperty("vk", out var vv) && vv.TryGetInt32(out var vn)) kbVk = vn;
                    }
                    catch (JsonException) { Write(ctx, 400, JsonSerializer.Serialize(new { error = "malformed JSON" }, Json)); break; }
                    var kbErr = ApplyKeybind(kbAction, kbVk);
                    if (kbErr != null)
                        Write(ctx, kbErr == "conflict" ? 409 : 400, JsonSerializer.Serialize(new { error = kbErr }, Json));
                    else
                        Write(ctx, 200, JsonSerializer.Serialize(new { ok = true }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/keybinds/reset":
            {
                if (ctx.Request.HttpMethod != "POST")
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                    break;
                }
                if (!IsLoopbackHost(ctx.Request))
                {
                    Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                    break;
                }
                _settings.Keybinds = new KeybindsSettings();
                _settings.Save();
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true }, Json));
                break;
            }

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

            case "/api/affix-catalog":
            {
                var cat = POE2Radar.Core.Game.MonsterAffixCatalog.Shared;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var items = new List<object>();
                foreach (var kv in cat.Curated)
                { seen.Add(kv.Key); items.Add(new { modId = kv.Key, name = kv.Value.Name, tier = kv.Value.Tier.ToString(), curated = true }); }
                foreach (var id in _knownMods())
                { if (!seen.Add(id)) continue; var info = cat.Resolve(id); items.Add(new { modId = id, name = info.Name, tier = info.Tier.ToString(), curated = false }); }
                Write(ctx, 200, JsonSerializer.Serialize(new { affixes = items }, Json));
                break;
            }

            case "/api/paths":
            {
                // v0.20.1 T9: current selected-target route polylines for /map and /obs. Same OR-gate
                // as the other data endpoints so /obs alone can fetch paths without enabling /map.
                // Payload matches the `paths` field on /stream (SseChannel.SnapshotForBrowser) so
                // clients can seed on connect + stay in sync from the SSE deltas.
                if (!_settings.EnableWebMap && !_settings.EnableWebObs) { NotFound(ctx); break; }
                var pathsJson = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    paths = s.Paths.Select(p => new { points = p.Points.Select(pt => new { x = pt.x, y = pt.y }) })
                }, Json);
                WriteMaybeGzipped(ctx, pathsJson, "application/json; charset=utf-8");
                break;
            }

            case "/api/waystone/parse":
            {
                // Reach — CHOR-41 (v0.26): parse a clipboard-copied PoE2 waystone item text and
                // return the tiered mod-risk breakdown. POST body: { "text": "<clipboard blob>" }.
                // Loopback-Host gated to keep local-only.
                if (ctx.Request.HttpMethod != "POST") { NotFound(ctx); break; }
                if (!IsLoopbackHost(ctx.Request)) { NotFound(ctx); break; }
                string body;
                using (var sr = new System.IO.StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    body = sr.ReadToEnd();
                string blob;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    blob = doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                        ? t.GetString() ?? "" : "";
                }
                catch { blob = ""; }
                var risk = POE2Radar.Core.Game.WaystoneModRisk.Shared.Parse(blob);
                var wsJson = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    isWaystone = risk.IsWaystone,
                    tier       = risk.Tier,
                    rarity     = risk.Rarity,
                    totalScore = risk.TotalScore,
                    shouldSkip = risk.ShouldSkip,
                    skipThreshold = POE2Radar.Core.Game.WaystoneModRisk.SkipThreshold,
                    mods       = risk.Mods.Select(m => new { line = m.Line, key = m.ModKey, name = m.Name, tier = m.Tier.ToString(), weight = m.Weight }),
                    combos     = risk.Combos.Select(c => new { label = c.Label, bonus = c.Bonus, keys = c.Keys }),
                }, Json);
                WriteMaybeGzipped(ctx, wsJson, "application/json; charset=utf-8");
                break;
            }

            case "/api/supporters":
            {
                // Reach — v0.26 (LO ask): serve the embedded supporters.json to the dashboard
                // Supporters card. No feature gate — the roll is community-facing.
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("supporters"));
                if (resName == null) { Write(ctx, 200, "{\"supporters\":[]}"); break; }
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream == null) { Write(ctx, 200, "{\"supporters\":[]}"); break; }
                using var reader = new System.IO.StreamReader(stream);
                var supJson = System.Text.Encoding.UTF8.GetBytes(reader.ReadToEnd());
                WriteMaybeGzipped(ctx, supJson, "application/json; charset=utf-8");
                break;
            }

            case "/api/bosses":
            {
                // Reach — CHOR-42 (v0.26): serve the shipped BossEncounterCatalog to the dashboard
                // Bosses tab. No feature gate — cheat-sheet data is user-facing reference material,
                // always safe to expose. Client renders the entries in-place.
                var cat = POE2Radar.Core.Game.BossEncounterCatalog.Shared;
                var bossesJson = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    entries = cat.Entries.Select(e => new
                    {
                        key           = e.Key,
                        label         = e.Label,
                        matchMetadata = e.MatchMetadata,
                        zoneCodes     = e.ZoneCodes,
                        tier          = e.Tier,
                        category      = e.Category,
                        damageTypes   = new { phys = e.DamageTypes.Phys, fire = e.DamageTypes.Fire, cold = e.DamageTypes.Cold, lightning = e.DamageTypes.Lightning, chaos = e.DamageTypes.Chaos },
                        oneShots      = e.OneShots,
                        overcap       = e.Overcap,
                        flaskNotes    = e.FlaskNotes,
                        phases        = e.Phases.Select(p => new { cue = p.Cue, note = p.Note }),
                    })
                }, Json);
                WriteMaybeGzipped(ctx, bossesJson, "application/json; charset=utf-8");
                break;
            }

            case "/api/wipe-log":
            {
                // v0.30 Instinct: per-character boss wipe log. Populated by the render thread every
                // time a boss cheat-sheet is up AND a fresh death is detected — persistent across
                // sessions in ConfigDir/boss_wipe_log.json. Payload shape (from RadarApp wipeLogProvider):
                //   { character: "Name", wipes: { boss_key: count, ... }, total: N, allCharacters: [...] }
                // Empty envelope when the provider isn't wired (defensive).
                object payload = _wipeLog?.Invoke() ?? new { character = "", wipes = new Dictionary<string, int>(), total = 0, allCharacters = Array.Empty<string>() };
                WriteMaybeGzipped(ctx, JsonSerializer.SerializeToUtf8Bytes(payload, Json), "application/json; charset=utf-8");
                break;
            }

            case "/api/item-filters":
            {
                // v0.31 Prospector: GET returns the current user filter list; POST replaces it wholesale.
                // Missing engine (headless / test scaffolding) → empty envelope, POST silently no-ops.
                if (_itemFilters is null)
                {
                    WriteMaybeGzipped(ctx, System.Text.Encoding.UTF8.GetBytes("{\"filters\":[]}"), "application/json; charset=utf-8");
                    break;
                }
                if (ctx.Request.HttpMethod == "POST")
                {
                    try
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream);
                        var body = reader.ReadToEnd();
                        var payload = JsonSerializer.Deserialize<ItemFiltersEnvelope>(body, Json);
                        if (payload?.Filters is not null) _itemFilters.Replace(payload.Filters);
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"/api/item-filters POST failed: {ex.Message}"); }
                }
                var envelope = new ItemFiltersEnvelope(_itemFilters.All);
                WriteMaybeGzipped(ctx, JsonSerializer.SerializeToUtf8Bytes(envelope, Json), "application/json; charset=utf-8");
                break;
            }

            case "/api/item-filters/restore-presets":
            {
                // v0.31 Prospector: append any shipped starter preset ids not currently in the list.
                if (ctx.Request.HttpMethod != "POST") { NotFound(ctx); break; }
                _itemFilters?.RestoreStarterPresets();
                Write(ctx, 200, "{\"ok\":true}");
                break;
            }

            case "/api/item-filters/matches":
            {
                // v0.31 Prospector: per-surface match count (ground / equipped / inventory).
                // Empty envelope when the provider isn't wired.
                object matchesPayload = _itemFilterMatches?.Invoke() ?? new { ground = 0, equipped = 0, inventory = 0 };
                WriteMaybeGzipped(ctx, JsonSerializer.SerializeToUtf8Bytes(matchesPayload, Json), "application/json; charset=utf-8");
                break;
            }

            case "/api/panels":
            {
                // v0.32 Panorama: which of the three main panels are currently open (visible + resolved).
                // Empty envelope (all-false) when the provider isn't wired.
                object panelPayload = _panelState?.Invoke() ?? new { character = false, inventory = false, stash = false };
                WriteMaybeGzipped(ctx, JsonSerializer.SerializeToUtf8Bytes(panelPayload, Json), "application/json; charset=utf-8");
                break;
            }

            case "/api/drops":
            {
                // v0.33 Drop Timeline: current snapshot of recorded ground drops. Empty envelope
                // (drops:[]) when the provider isn't wired.
                object dropsPayload = _dropsProvider?.Invoke() ?? new { drops = Array.Empty<object>() };
                WriteMaybeGzipped(ctx, JsonSerializer.SerializeToUtf8Bytes(dropsPayload, Json), "application/json; charset=utf-8");
                break;
            }

            case "/api/codex":
            {
                // v0.37 A1: per-character codex event journal. Loopback-Host-gated because the
                // character-name query param leaks PII (character identity is never in /state).
                if (!IsLoopbackHost(ctx.Request))
                {
                    Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                    break;
                }
                var character = ctx.Request.QueryString["character"];
                if (string.IsNullOrWhiteSpace(character))
                {
                    Write(ctx, 400, JsonSerializer.Serialize(new { error = "missing character query param" }, Json));
                    break;
                }
                object codexPayload = _codexProvider?.Invoke(character) ?? new { events = Array.Empty<object>() };
                WriteMaybeGzipped(ctx, JsonSerializer.SerializeToUtf8Bytes(codexPayload, Json), "application/json; charset=utf-8");
                break;
            }

            case "/api/tracks":
            {
                // v0.40 Cartographer: per-character zone track samples. Loopback-Host-gated because
                // the character-name query param identifies the player character.
                if (ctx.Request.HttpMethod != "GET")
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                    break;
                }
                if (!IsLoopbackHost(ctx.Request))
                {
                    Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                    break;
                }
                var character = ctx.Request.QueryString["character"];
                if (string.IsNullOrWhiteSpace(character))
                {
                    Write(ctx, 400, JsonSerializer.Serialize(new { error = "missing character" }, Json));
                    break;
                }
                var zone = ctx.Request.QueryString["zone"];
                if (string.IsNullOrWhiteSpace(zone))
                {
                    Write(ctx, 400, JsonSerializer.Serialize(new { error = "missing zone" }, Json));
                    break;
                }
                var samples = TrackStore.Load(_rulesConfigDir, character, zone);
                Write(ctx, 200, JsonSerializer.Serialize(new { samples }, Json));
                break;
            }

            case "/api/tracks/characters":
            {
                // v0.40 Cartographer: list of all tracked characters. Loopback-Host-gated.
                if (ctx.Request.HttpMethod != "GET")
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                    break;
                }
                if (!IsLoopbackHost(ctx.Request))
                {
                    Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                    break;
                }
                var characters = TrackStore.ListCharacters(_rulesConfigDir);
                Write(ctx, 200, JsonSerializer.Serialize(new { characters }, Json));
                break;
            }

            case "/api/tracks/zones":
            {
                // v0.40 Cartographer: list of zones tracked for a character. Loopback-Host-gated.
                if (ctx.Request.HttpMethod != "GET")
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                    break;
                }
                if (!IsLoopbackHost(ctx.Request))
                {
                    Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                    break;
                }
                var character = ctx.Request.QueryString["character"];
                if (string.IsNullOrWhiteSpace(character))
                {
                    Write(ctx, 400, JsonSerializer.Serialize(new { error = "missing character" }, Json));
                    break;
                }
                var zones = TrackStore.ListZones(_rulesConfigDir, character);
                Write(ctx, 200, JsonSerializer.Serialize(new { zones }, Json));
                break;
            }

            case "/api/palettes":
            {
                // v0.38 F1: Color Forge — user-authored palette CRUD. GET (ungated) lists all
                // palettes; POST (loopback-gated) saves a new palette.
                if (ctx.Request.HttpMethod == "GET")
                {
                    var palettes = POE2Radar.Core.Palettes.UserPaletteStore.List();
                    var payload = palettes.Select(p => new
                    {
                        slug = p.Slug,
                        displayName = p.DisplayName,
                        vars = p.Vars,
                        preview = p.Preview,
                    });
                    Write(ctx, 200, JsonSerializer.Serialize(new { palettes = payload }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                        break;
                    }
                    try
                    {
                        var body = ReadBody(ctx);
                        var doc = JsonDocument.Parse(body);
                        var slug = doc.RootElement.GetProperty("slug").GetString() ?? "";
                        var displayName = doc.RootElement.GetProperty("displayName").GetString() ?? "";
                        var varsEl = doc.RootElement.GetProperty("vars");
                        var vars = new Dictionary<string, string>();
                        foreach (var prop in varsEl.EnumerateObject())
                            vars[prop.Name] = prop.Value.GetString() ?? "";
                        var p = new POE2Radar.Core.Palettes.UserPalette(
                            slug, displayName, vars, Array.Empty<string>(), DateTime.UtcNow);
                        if (POE2Radar.Core.Palettes.UserPaletteStore.Get(slug) != null)
                        {
                            Write(ctx, 409, JsonSerializer.Serialize(new { error = "slug already exists", slug }, Json));
                            break;
                        }
                        var saved = POE2Radar.Core.Palettes.UserPaletteStore.Save(p);
                        Write(ctx, 200, JsonSerializer.Serialize(new
                        {
                            slug = saved.Slug,
                            displayName = saved.DisplayName,
                            vars = saved.Vars,
                            preview = saved.Preview,
                        }, Json));
                    }
                    catch (ArgumentException ex)
                    {
                        Write(ctx, 400, JsonSerializer.Serialize(new { error = ex.Message }, Json));
                    }
                    catch (JsonException)
                    {
                        Write(ctx, 400, JsonSerializer.Serialize(new { error = "invalid JSON body" }, Json));
                    }
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case string p when p.StartsWith("/api/palettes/", StringComparison.Ordinal) && p.Length > "/api/palettes/".Length:
            {
                var slug = p["/api/palettes/".Length..];
                if (ctx.Request.HttpMethod == "GET")
                {
                    var palette = POE2Radar.Core.Palettes.UserPaletteStore.Get(slug);
                    if (palette == null)
                    {
                        Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found" }, Json));
                        break;
                    }
                    Write(ctx, 200, JsonSerializer.Serialize(new
                    {
                        slug = palette.Slug,
                        displayName = palette.DisplayName,
                        vars = palette.Vars,
                        preview = palette.Preview,
                    }, Json));
                }
                else if (ctx.Request.HttpMethod == "DELETE")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                        break;
                    }
                    var deleted = POE2Radar.Core.Palettes.UserPaletteStore.Delete(slug);
                    if (!deleted)
                    {
                        Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found" }, Json));
                        break;
                    }
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, slug }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/rules":
            {
                // v0.39 Rules Engine: user-authored display rules CRUD. GET (ungated) lists all
                // rules; POST (loopback-gated) creates or updates a rule. Body is a RuleRecord JSON.
                if (ctx.Request.HttpMethod == "GET")
                {
                    var file = RulesFileStore.Load(_rulesConfigDir);
                    var payload = file.Rules.Select(r => new
                    {
                        r.Id, r.Name, r.Priority, r.Enabled, r.When, r.Then,
                    });
                    Write(ctx, 200, JsonSerializer.Serialize(new { rules = payload }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                        break;
                    }
                    try
                    {
                        var body = ReadBody(ctx);
                        var rule = JsonSerializer.Deserialize<RuleRecord>(body, Json)
                                   ?? throw new JsonException("null body");

                        // Duplicate-name check only at create (Id empty).
                        if (rule.Id == Guid.Empty)
                        {
                            var existing = RulesFileStore.Load(_rulesConfigDir).Rules;
                            if (existing.Any(r => string.Equals(r.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                Write(ctx, 409, JsonSerializer.Serialize(new { error = "rule name already exists", name = rule.Name }, Json));
                                break;
                            }
                        }

                        var saved = RulesFileStore.Upsert(_rulesConfigDir, rule);
                        Write(ctx, 200, JsonSerializer.Serialize(new
                        {
                            saved.Id, saved.Name, saved.Priority, saved.Enabled, saved.When, saved.Then,
                        }, Json));
                    }
                    catch (JsonException)
                    {
                        Write(ctx, 400, JsonSerializer.Serialize(new { error = "invalid JSON body" }, Json));
                    }
                    catch (ArgumentException ex)
                    {
                        // Distinguish rule-cap overflow from validation errors.
                        var msg = ex.Message.Contains("exceeds maximum of 100", StringComparison.Ordinal)
                            ? "rule cap reached (100)"
                            : ex.Message;
                        Write(ctx, 400, JsonSerializer.Serialize(new { error = msg }, Json));
                    }
                    catch (System.Exception)
                    {
                        Write(ctx, 500, JsonSerializer.Serialize(new { error = "internal" }, Json));
                    }
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/radar-filters":
            {
                // v0.41 A_API: user radar filter presets CRUD. GET (ungated) lists all
                // presets; POST (loopback-gated) replaces the entire file.
                if (ctx.Request.HttpMethod == "GET")
                {
                    var file = RadarFilterStore.Load(_rulesConfigDir);
                    var payload = file.Presets.Select(p => new
                    {
                        match = p.Match,
                        whitelist = p.Whitelist,
                        blacklist = p.Blacklist,
                    });
                    Write(ctx, 200, JsonSerializer.Serialize(new { presets = payload }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                        break;
                    }
                    try
                    {
                        var body = ReadBody(ctx);
                        var file = JsonSerializer.Deserialize<RadarFilterFile>(body, Json)
                                   ?? throw new JsonException("null body");
                        RadarFilterStore.Save(_rulesConfigDir, file);
                        var payload = file.Presets.Select(p => new
                        {
                            match = p.Match,
                            whitelist = p.Whitelist,
                            blacklist = p.Blacklist,
                        });
                        Write(ctx, 200, JsonSerializer.Serialize(new { presets = payload }, Json));
                    }
                    catch (JsonException)
                    {
                        Write(ctx, 400, JsonSerializer.Serialize(new { error = "invalid JSON body" }, Json));
                    }
                    catch (ArgumentException ex)
                    {
                        Write(ctx, 400, JsonSerializer.Serialize(new { error = ex.Message }, Json));
                    }
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/overlay-layouts":
            {
                // v0.41 B_API: user overlay layout presets CRUD. GET (ungated) lists all
                // presets; POST (loopback-gated) replaces the entire file.
                if (ctx.Request.HttpMethod == "GET")
                {
                    var file = OverlayLayoutStore.Load(_rulesConfigDir);
                    var payload = file.Presets.Select(p => new
                    {
                        name = p.Name,
                        match = p.Match,
                        panels = p.Panels,
                    });
                    Write(ctx, 200, JsonSerializer.Serialize(new { presets = payload }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                        break;
                    }
                    try
                    {
                        var body = ReadBody(ctx);
                        var file = JsonSerializer.Deserialize<OverlayLayoutFile>(body, Json)
                                   ?? throw new JsonException("null body");
                        OverlayLayoutStore.Save(_rulesConfigDir, file);
                        var payload = file.Presets.Select(p => new
                        {
                            name = p.Name,
                            match = p.Match,
                            panels = p.Panels,
                        });
                        Write(ctx, 200, JsonSerializer.Serialize(new { presets = payload }, Json));
                    }
                    catch (JsonException)
                    {
                        Write(ctx, 400, JsonSerializer.Serialize(new { error = "invalid JSON body" }, Json));
                    }
                    catch (ArgumentException ex)
                    {
                        Write(ctx, 400, JsonSerializer.Serialize(new { error = ex.Message }, Json));
                    }
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case string p when p.StartsWith("/api/rules/", StringComparison.Ordinal) && p.Length > "/api/rules/".Length:
            {
                var idSegment = p["/api/rules/".Length..];
                if (!Guid.TryParse(idSegment, out var id))
                {
                    Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found" }, Json));
                    break;
                }

                if (ctx.Request.HttpMethod == "GET")
                {
                    var file = RulesFileStore.Load(_rulesConfigDir);
                    var rule = file.Rules.FirstOrDefault(r => r.Id == id);
                    if (rule == null)
                    {
                        Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found" }, Json));
                        break;
                    }
                    Write(ctx, 200, JsonSerializer.Serialize(new
                    {
                        rule.Id, rule.Name, rule.Priority, rule.Enabled, rule.When, rule.Then,
                    }, Json));
                }
                else if (ctx.Request.HttpMethod == "DELETE")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                        break;
                    }
                    var deleted = RulesFileStore.Delete(_rulesConfigDir, id);
                    if (!deleted)
                    {
                        Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found" }, Json));
                        break;
                    }
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, id }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/nav-destinations":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    var zone = q["zone"];
                    if (!string.IsNullOrEmpty(zone))
                    {
                        var filtered = NavDestinationStore.LoadForZone(_rulesConfigDir, zone);
                        Write(ctx, 200, JsonSerializer.Serialize(new { destinations = filtered.Select(d => new { d.Id, d.ZoneCode, d.Name, d.X, d.Y }) }, Json));
                    }
                    else
                    {
                        var file = NavDestinationStore.Load(_rulesConfigDir);
                        Write(ctx, 200, JsonSerializer.Serialize(new { destinations = file.Destinations.Select(d => new { d.Id, d.ZoneCode, d.Name, d.X, d.Y }) }, Json));
                    }
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                        break;
                    }
                    try
                    {
                        var body = ReadBody(ctx);
                        var destination = JsonSerializer.Deserialize<NavDestination>(body, Json)
                                       ?? throw new JsonException("null body");

                        // Pre-assign a new Guid if empty so we can return the assigned id.
                        var toUpsert = destination.Id == Guid.Empty
                            ? destination with { Id = Guid.NewGuid() }
                            : destination;

                        NavDestinationStore.Upsert(_rulesConfigDir, toUpsert);
                        Write(ctx, 200, JsonSerializer.Serialize(new { toUpsert.Id, toUpsert.ZoneCode, toUpsert.Name, toUpsert.X, toUpsert.Y }, Json));
                    }
                    catch (JsonException)
                    {
                        Write(ctx, 400, JsonSerializer.Serialize(new { error = "invalid JSON body" }, Json));
                    }
                    catch (ArgumentException ex)
                    {
                        Write(ctx, 400, JsonSerializer.Serialize(new { error = ex.Message }, Json));
                    }
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case string p when p.StartsWith("/api/nav-destinations/", StringComparison.Ordinal) && p.Length > "/api/nav-destinations/".Length:
            {
                var idSegment = p["/api/nav-destinations/".Length..];
                if (!Guid.TryParse(idSegment, out var id))
                {
                    Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found" }, Json));
                    break;
                }

                if (ctx.Request.HttpMethod == "DELETE")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "loopback-only" }, Json));
                        break;
                    }
                    var deleted = NavDestinationStore.Delete(_rulesConfigDir, id);
                    if (!deleted)
                    {
                        Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found" }, Json));
                        break;
                    }
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, id }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/map":
            {
                // v0.20.0 T5: same OR-gate — the shared renderer needs terrain from either entry point.
                if (!_settings.EnableWebMap && !_settings.EnableWebObs) { NotFound(ctx); break; }
                var (walk, w, h, hash) = _terrainProvider?.Invoke() ?? (null, 0, 0, 0u);
                if (walk == null || w <= 0 || h <= 0) { Write(ctx, 200, "{\"ready\":false}"); break; }
                // The ready-guard above ensures we never cache a payload for an unloaded zone, so the _mapCacheHash==0 default can't collide with a real (loaded) zone here.
                if (_mapCacheJson == null || _mapCacheHash != hash)
                {
                    _mapCacheJson = TerrainMapPayload.ToJson(walk, w, h, hash);
                    _mapCacheHash = hash;
                }
                var json = Encoding.UTF8.GetBytes(_mapCacheJson);
                WriteMaybeGzipped(ctx, json, "application/json; charset=utf-8");
                break;
            }

            default:
                // v0.20.0 T5: /assets/* prefix falls through here. Gated on EITHER toggle + non-null
                // assetHost — with both off, `_assetHost` is null and we skip to NotFound without ever
                // dereferencing it, preserving the zero-cost-when-off contract.
                if ((_settings.EnableWebMap || _settings.EnableWebObs)
                    && _assetHost != null
                    && path.StartsWith("/assets/", System.StringComparison.Ordinal))
                {
                    _assetHost.ServeAsset(ctx, path.Substring("/assets/".Length));
                    break;
                }
                NotFound(ctx);
                break;
        }
    }

    // v0.20.0 T5: minimal 404 for the feature-gated arms. Empty body — the older `Write(ctx, 404, json)`
    // path is kept for legacy non-gated routes so their contract stays the same.
    private static void NotFound(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.OutputStream.Close();
    }

    // Actions that carry the Ctrl+Alt modifier (their label prefix reflects this in the UI).
    private static readonly HashSet<string> CtrlAltActions = new(StringComparer.Ordinal)
        { "CycleNext", "CyclePrev", "NavMenuToggle", "SessionReset" };

    // VK codes accepted for keybind assignment: F1–F12, A–Z, 0–9, common bracket/punct keys.
    private static bool IsAllowedVk(int vk) =>
        (vk >= 0x70 && vk <= 0x7B) || // F1–F12
        (vk >= 0x41 && vk <= 0x5A) || // A–Z
        (vk >= 0x30 && vk <= 0x39) || // 0–9
        vk is 0xDD or 0xDB or 0xBA or 0xBF or 0xBD or 0xBB or 0xC0 or 0xDE or 0xDC or 0xBC or 0xBE or 0x20;

    /// <summary>Build the list of all 9 keybind actions with their current VK + computed label.</summary>
    private object[] GetKeybindsList()
    {
        var kb = _settings.Keybinds;
        return new[]
        {
            BuildKbEntry("Quit",           kb.Quit),
            BuildKbEntry("OpenDashboard",  kb.OpenDashboard),
            BuildKbEntry("AtlasInspect",   kb.AtlasInspect),
            BuildKbEntry("AddNearest",     kb.AddNearest),
            BuildKbEntry("ClearRoutes",    kb.ClearRoutes),
            BuildKbEntry("CycleNext",      kb.CycleNext),
            BuildKbEntry("CyclePrev",      kb.CyclePrev),
            BuildKbEntry("NavMenuToggle",  kb.NavMenuToggle),
            BuildKbEntry("SessionReset",   kb.SessionReset),
        };
    }

    private static object BuildKbEntry(string action, int vk)
    {
        var modifier = CtrlAltActions.Contains(action) ? "Ctrl+Alt" : "";
        var label = (modifier.Length > 0 ? "Ctrl+Alt+" : "") + KeyNames.Format(vk);
        return new { action, vk, label, modifier };
    }

    /// <summary>Validate and apply a single keybind change. Returns null on success, or an error string
    /// ("unknown action", "invalid vk", "conflict") on failure.</summary>
    private string? ApplyKeybind(string? action, int vk)
    {
        if (string.IsNullOrWhiteSpace(action)) return "unknown action";
        if (!IsAllowedVk(vk)) return "invalid vk";

        var kb = _settings.Keybinds;

        // Determine which modifier group the action belongs to and check for duplicates in the same group.
        var isCtrlAlt = CtrlAltActions.Contains(action);

        // Explicit action→get/set map (reflection-free).
        switch (action)
        {
            case "Quit":          break;
            case "OpenDashboard": break;
            case "AtlasInspect":  break;
            case "AddNearest":    break;
            case "ClearRoutes":   break;
            case "CycleNext":     break;
            case "CyclePrev":     break;
            case "NavMenuToggle": break;
            case "SessionReset":  break;
            default: return "unknown action";
        }

        // Duplicate check: scan every action in the SAME modifier group.
        static bool SameGroup(string a, bool ctrlAlt) => CtrlAltActions.Contains(a) == ctrlAlt;

        string? conflictWith = null;
        if (SameGroup("Quit", isCtrlAlt)          && kb.Quit          == vk && action != "Quit")          conflictWith = "Quit";
        if (SameGroup("OpenDashboard", isCtrlAlt)  && kb.OpenDashboard == vk && action != "OpenDashboard") conflictWith = "OpenDashboard";
        if (SameGroup("AtlasInspect", isCtrlAlt)   && kb.AtlasInspect  == vk && action != "AtlasInspect")  conflictWith = "AtlasInspect";
        if (SameGroup("AddNearest", isCtrlAlt)     && kb.AddNearest    == vk && action != "AddNearest")    conflictWith = "AddNearest";
        if (SameGroup("ClearRoutes", isCtrlAlt)    && kb.ClearRoutes   == vk && action != "ClearRoutes")   conflictWith = "ClearRoutes";
        if (SameGroup("CycleNext", isCtrlAlt)      && kb.CycleNext     == vk && action != "CycleNext")     conflictWith = "CycleNext";
        if (SameGroup("CyclePrev", isCtrlAlt)      && kb.CyclePrev     == vk && action != "CyclePrev")     conflictWith = "CyclePrev";
        if (SameGroup("NavMenuToggle", isCtrlAlt)  && kb.NavMenuToggle == vk && action != "NavMenuToggle") conflictWith = "NavMenuToggle";
        if (SameGroup("SessionReset", isCtrlAlt)   && kb.SessionReset  == vk && action != "SessionReset")  conflictWith = "SessionReset";

        if (conflictWith != null) return "conflict";

        // Apply.
        switch (action)
        {
            case "Quit":          kb.Quit          = vk; break;
            case "OpenDashboard": kb.OpenDashboard = vk; break;
            case "AtlasInspect":  kb.AtlasInspect  = vk; break;
            case "AddNearest":    kb.AddNearest    = vk; break;
            case "ClearRoutes":   kb.ClearRoutes   = vk; break;
            case "CycleNext":     kb.CycleNext     = vk; break;
            case "CyclePrev":     kb.CyclePrev     = vk; break;
            case "NavMenuToggle": kb.NavMenuToggle = vk; break;
            case "SessionReset":  kb.SessionReset  = vk; break;
        }
        _settings.Save();
        return null;
    }

    /// <summary>
    /// The settings the dashboard may read AND write. Covers radar/visual options.
    /// All writes are loopback-Host-gated (see Handle), so a cross-origin site can't reach them.
    /// The API port is read-only here (changing it needs a restart).
    /// This object also doubles as the GET payload.
    /// </summary>
    private object ReadSettings() => new
    {
        hideJunk = _settings.HideJunk,
        showPath = _settings.ShowPath,
        alwaysShowOverlay = _settings.AlwaysShowOverlay,
        useCuratedLandmarks = _settings.UseCuratedLandmarks,
        landmarkClusterGap = _settings.LandmarkClusterGap,
        showMonsters = _settings.ShowMonsters,
        showTerrain = _settings.ShowTerrain,
        showPlayerBlip = _settings.ShowPlayerBlip,
        enableDirector = _settings.EnableDirector,
        enableCampaignGps = _settings.EnableCampaignGps,
        enableQuestMemory = _settings.EnableQuestMemory,
        excludeFromCapture = _settings.ExcludeFromCapture,
        enableGearScorer = _settings.EnableGearScorer,
        enableTargetHotkeys = _settings.EnableTargetHotkeys,
        enableControllerCycle = _settings.EnableControllerCycle,
        intelligentTargetCycling = _settings.IntelligentTargetCycling,
        showMonolithPanel = _settings.Monoliths.ShowPanel,
        fpsCap = _settings.FpsCap,
        hpBarNormal = _settings.HpBarNormal,
        hpBarMagic = _settings.HpBarMagic,
        hpBarRare = _settings.HpBarRare,
        hpBarUnique = _settings.HpBarUnique,
        iconTintByRarity = _settings.IconTintByRarity,
        scaleMul = _settings.ScaleMul,
        offX = _settings.OffX,
        offY = _settings.OffY,
        apiPort = _settings.ApiPort, // display only — changing it needs a restart
        allowLanAccess = _settings.AllowLanAccess, // opt-in LAN view binding; needs a restart to apply
        enableWebMap = _settings.EnableWebMap,
        enableWebObs = _settings.EnableWebObs,
        webObsSafeDelaySec       = _settings.WebObsSafeDelaySec,
        webObsSafeMaskZoneName   = _settings.WebObsSafeMaskZoneName,
        webObsSafeHideoutBlur    = _settings.WebObsSafeHideoutBlur,
        webObsSafeEntityNameFog  = _settings.WebObsSafeEntityNameFog,
        enableDropTimeline = _settings.EnableDropTimeline,
        enableItemFilterLiveCounters = _settings.EnableItemFilterLiveCounters,
        enableInventoryHighlights = _settings.EnableInventoryHighlights,
        updateChannel = _settings.UpdateChannel, // stable|preview — restart to apply
        updateUrl = _settings.UpdateUrl,         // null (default) = built-in GitHub API; restart to apply
        styles = _settings.Styles,   // per-item icon shapes/colors/sizes + mechanic overrides
        hpBars = _settings.HpBars,   // monster HP-bar geometry (width/height/offset)
        terrain = _settings.Terrain, // walkable-terrain bitmap colors/transparency
        groundItems = _settings.GroundItems, // ground-item value overlay (enabled / highlight threshold / league)
        contributeUrl = _settings.ContributeUrl,
        defaultContributeUrl = RadarSettings.DefaultContributeUrl, // Restore-default toast (CF-FALLBACK-UX)
        // Support — v0.27 (LO ask): supporter code + cosmetic dashboard palette + optional overlay badge.
        // isSupporter is a derived read-only mirror of the honor-system check — the dashboard uses it
        // to gate palette + badge UI, no reveal of the actual code or hash list.
        supporterCode      = _settings.SupporterCode,
        dashboardPalette   = _settings.DashboardPalette,
        paletteColors = POE2Radar.Core.Themes.RecapPaletteMap.Resolve(_settings.DashboardPalette),
        showSupporterBadge = _settings.ShowSupporterBadge,
        isSupporter        = POE2Radar.Core.Support.SupporterCodeValidator.IsSupporter(_settings.SupporterCode),
        highlightDynastyMaps = _settings.HighlightDynastyMaps,
        atlasHideCompleted   = _settings.AtlasHideCompleted,
        atlasHideAccessible  = _settings.AtlasHideAccessible,
        preloadEnabled           = _settings.PreloadAlert.Enabled,
        preloadMinTier           = _settings.PreloadAlert.MinTier,
        preloadAudioTier         = _settings.PreloadAlert.AudioTier,
        preloadDiagnostic        = _settings.PreloadAlert.Diagnostic,
        preloadCommonThreshold   = _settings.PreloadAlert.CommonThreshold,
        preloadWarmupZones       = _settings.PreloadAlert.WarmupZones,
        preloadAnchor            = _settings.PreloadAlert.Anchor,
        preloadOffsetX           = _settings.PreloadAlert.OffsetX,
        preloadOffsetY           = _settings.PreloadAlert.OffsetY,
        sessionHudEnabled        = _settings.SessionHud.Enabled,
        sessionHudShowPace       = _settings.SessionHud.ShowPace,
        sessionHudShowZoneContext= _settings.SessionHud.ShowZoneContext,
        sessionHudShowDeaths     = _settings.SessionHud.ShowDeaths,
        sessionHudShowKills      = _settings.SessionHud.ShowKills,
        sessionHudShowXpRate     = _settings.SessionHud.ShowXpRate,
        sessionHudXpWindowMinutes= _settings.SessionHud.XpWindowMinutes,
        sessionHudAnchor         = _settings.SessionHud.Anchor,
        sessionHudOffsetX        = _settings.SessionHud.OffsetX,
        sessionHudOffsetY        = _settings.SessionHud.OffsetY,
        sessionHudExcludeTowns   = _settings.SessionHud.ExcludeTownsFromPace,
        // Zone summary panel settings
        zoneSummaryEnabled       = _settings.ZoneSummary.Enabled,
        zoneSummaryAnchor        = _settings.ZoneSummary.Anchor,
        // Audio alert settings (5 fields; master gate defaults OFF)
        enableAudioAlerts        = _settings.EnableAudioAlerts,
        audioAlertRareUnique     = _settings.AudioAlertRareUnique,
        audioAlertUniqueDrop     = _settings.AudioAlertUniqueDrop,
        audioAlertObjective      = _settings.AudioAlertObjective,
        audioAlertRadiusCells    = _settings.AudioAlertRadiusCells,
        audioAlertVolume         = _settings.AudioAlertVolume,
        audioToneMonster         = _settings.AudioToneMonster,
        audioToneItem            = _settings.AudioToneItem,
        audioToneObjective       = _settings.AudioToneObjective,
        audioAlertMechanic       = _settings.AudioAlertMechanic,
        audioToneMechanic        = _settings.AudioToneMechanic,
        webMapRevealRadiusCells  = _settings.WebMapRevealRadiusCells,
        firstRunSeen             = _settings.FirstRunSeen,
        // Atlas colour groups (#7): the full group list so the dashboard can render + edit them.
        atlasGroups              = _settings.AtlasGroups,
        atlasRouteArrowSpacing   = _settings.AtlasRouteArrowSpacing,
        atlasShowContentIcons    = _settings.AtlasShowContentIcons,
        atlasContentIconSize     = _settings.AtlasContentIconSize,
        atlasShowRoute           = _settings.AtlasShowRoute,
        atlasAutoRoute           = _settings.AtlasAutoRoute,
        atlasAutoRouteMaxHops    = _settings.AtlasAutoRouteMaxHops,
        atlasShowBiomeBorder     = _settings.AtlasShowBiomeBorder,
        // Off-screen entity arrows: whole settings object + seeded flag (so dashboard can read state).
        entityArrows             = _settings.EntityArrows,
        entityArrowsSeeded       = _settings.AppliedMigrations.Contains("seed:entity-arrows"),
        // OBS overlay + Discord Presence settings. ClientId is intentionally omitted from the GET
        // response — it is write-only (set via POST) and must not appear in screenshots or streams.
        obsOverlay               = _settings.ObsOverlay,
        autoUpdate               = _settings.AutoUpdate,
        discordPresence          = new {
            enabled         = _settings.DiscordPresence.Enabled,
            // clientId intentionally omitted — write-only, never echoed
            detailsTemplate = _settings.DiscordPresence.DetailsTemplate,
            stateTemplate   = _settings.DiscordPresence.StateTemplate,
            showTimer       = _settings.DiscordPresence.ShowTimer,
        },
    };

    /// <summary>Apply only whitelisted radar/visual keys from a posted JSON object; persists on change.</summary>
    private string[] ApplySettings(string body)
    {
        var applied = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return applied.ToArray();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return applied.ToArray();

        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "hideJunk" when TryBool(p.Value, out var b): _settings.HideJunk = b; applied.Add(p.Name); break;
                case "showPath" when TryBool(p.Value, out var b): _settings.ShowPath = b; applied.Add(p.Name); break;
                case "alwaysShowOverlay" when TryBool(p.Value, out var b): _settings.AlwaysShowOverlay = b; applied.Add(p.Name); break;
                case "useCuratedLandmarks" when TryBool(p.Value, out var b): _settings.UseCuratedLandmarks = b; applied.Add(p.Name); break;
                case "landmarkClusterGap" when TryInt(p.Value, out var n): _settings.LandmarkClusterGap = Math.Clamp(n, 0, 64); applied.Add(p.Name); break;
                case "scaleMul" when TryFloat(p.Value, out var f): _settings.ScaleMul = f; applied.Add(p.Name); break;
                case "offX" when TryFloat(p.Value, out var f): _settings.OffX = f; applied.Add(p.Name); break;
                case "offY" when TryFloat(p.Value, out var f): _settings.OffY = f; applied.Add(p.Name); break;
                case "showMonsters" when TryBool(p.Value, out var b): _settings.ShowMonsters = b; applied.Add(p.Name); break;
                case "showTerrain" when TryBool(p.Value, out var b): _settings.ShowTerrain = b; applied.Add(p.Name); break;
                case "showPlayerBlip" when TryBool(p.Value, out var b): _settings.ShowPlayerBlip = b; applied.Add(p.Name); break;
                case "enableDirector" when TryBool(p.Value, out var b): _settings.EnableDirector = b; applied.Add(p.Name); break;
                case "enableCampaignGps" when TryBool(p.Value, out var b): _settings.EnableCampaignGps = b; applied.Add(p.Name); break;
                case "enableQuestMemory" when TryBool(p.Value, out var b): _settings.EnableQuestMemory = b; applied.Add(p.Name); break;
                case "excludeFromCapture" when TryBool(p.Value, out var b): _settings.ExcludeFromCapture = b; applied.Add(p.Name); break;
                case "enableGearScorer" when TryBool(p.Value, out var b): _settings.EnableGearScorer = b; applied.Add(p.Name); break;
                case "enableTargetHotkeys" when TryBool(p.Value, out var b): _settings.EnableTargetHotkeys = b; applied.Add(p.Name); break;
                case "enableControllerCycle" when TryBool(p.Value, out var b): _settings.EnableControllerCycle = b; applied.Add(p.Name); break;
                case "intelligentTargetCycling" when TryBool(p.Value, out var b): _settings.IntelligentTargetCycling = b; applied.Add(p.Name); break;
                case "showMonolithPanel" when TryBool(p.Value, out var b): _settings.Monoliths.ShowPanel = b; applied.Add(p.Name); break;
                case "contributeUrl" when TryString(p.Value, out var s): _settings.ContributeUrl = s.Trim(); applied.Add(p.Name); break;
                // Support — v0.27: supporter code + cosmetic palette + badge toggle.
                case "supporterCode"      when TryString(p.Value, out var s): _settings.SupporterCode      = s.Trim(); applied.Add(p.Name); break;
                case "dashboardPalette"   when TryString(p.Value, out var s): _settings.DashboardPalette   = s.Trim(); applied.Add(p.Name); break;
                case "showSupporterBadge" when TryBool(p.Value, out var b):   _settings.ShowSupporterBadge = b;        applied.Add(p.Name); break;
                // Auto-update channel + URL override (v0.20.1). Both restart-required — the updater
                // reads them once during Program startup. Channel is whitelisted to {stable,preview}
                // so a malformed POST can't wedge the updater on an unknown mode; a blank/whitespace
                // updateUrl POST is normalised to null so an empty dashboard field resets the override.
                case "updateChannel" when TryString(p.Value, out var uc) && (uc == "stable" || uc == "preview"):
                    _settings.UpdateChannel = uc; applied.Add(p.Name); break;
                case "updateUrl" when TryStringOrNull(p.Value, out var uu):
                    _settings.UpdateUrl = string.IsNullOrWhiteSpace(uu) ? null : uu!.Trim();
                    applied.Add(p.Name); break;
                case "fpsCap" when TryInt(p.Value, out var n): _settings.FpsCap = Math.Clamp(n, 15, 360); applied.Add(p.Name); break;
                case "hpBarNormal" when TryBool(p.Value, out var b): _settings.HpBarNormal = b; applied.Add(p.Name); break;
                case "hpBarMagic" when TryBool(p.Value, out var b): _settings.HpBarMagic = b; applied.Add(p.Name); break;
                case "hpBarRare" when TryBool(p.Value, out var b): _settings.HpBarRare = b; applied.Add(p.Name); break;
                case "hpBarUnique" when TryBool(p.Value, out var b): _settings.HpBarUnique = b; applied.Add(p.Name); break;
                case "iconTintByRarity" when TryBool(p.Value, out var b): _settings.IconTintByRarity = b; applied.Add(p.Name); break;
                // Whole-object writes (the dashboard re-POSTs the full sub-object on edit). Parsed,
                // sanitized/clamped, then swapped in. A malformed sub-object is skipped, not fatal.
                case "styles" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseStyles(p.Value, out var styles)) { _settings.Styles = styles; applied.Add(p.Name); }
                    break;
                case "hpBars" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseHpBars(p.Value, out var hpBars)) { _settings.HpBars = hpBars; applied.Add(p.Name); }
                    break;
                case "terrain" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseTerrain(p.Value, out var terrain)) { _settings.Terrain = terrain; applied.Add(p.Name); }
                    break;
                case "groundItems" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseGroundItems(p.Value, out var gi)) { _settings.GroundItems = gi; applied.Add(p.Name); }
                    break;
                case "highlightDynastyMaps" when TryBool(p.Value, out var b): _settings.HighlightDynastyMaps = b; applied.Add(p.Name); break;
                case "atlasHideCompleted"   when TryBool(p.Value, out var b): _settings.AtlasHideCompleted = b; applied.Add(p.Name); break;
                case "atlasHideAccessible"  when TryBool(p.Value, out var b): _settings.AtlasHideAccessible = b; applied.Add(p.Name); break;
                case "sessionHudEnabled" when TryBool(p.Value, out var b): _settings.SessionHud.Enabled = b; applied.Add(p.Name); break;
                case "sessionHudShowPace" when TryBool(p.Value, out var b): _settings.SessionHud.ShowPace = b; applied.Add(p.Name); break;
                case "sessionHudShowZoneContext" when TryBool(p.Value, out var b): _settings.SessionHud.ShowZoneContext = b; applied.Add(p.Name); break;
                case "sessionHudShowDeaths" when TryBool(p.Value, out var b): _settings.SessionHud.ShowDeaths = b; applied.Add(p.Name); break;
                case "sessionHudShowKills" when TryBool(p.Value, out var b): _settings.SessionHud.ShowKills = b; applied.Add(p.Name); break;
                case "sessionHudShowXpRate" when TryBool(p.Value, out var b): _settings.SessionHud.ShowXpRate = b; applied.Add(p.Name); break;
                case "sessionHudXpWindowMinutes" when TryInt(p.Value, out var n): _settings.SessionHud.XpWindowMinutes = n; applied.Add(p.Name); break;
                case "sessionHudExcludeTowns" when TryBool(p.Value, out var b): _settings.SessionHud.ExcludeTownsFromPace = b; applied.Add(p.Name); break;
                case "sessionHudAnchor" when TryString(p.Value, out var s): _settings.SessionHud.Anchor = s.Trim(); applied.Add(p.Name); break;
                case "sessionHudOffsetX" when TryInt(p.Value, out var n): _settings.SessionHud.OffsetX = n; applied.Add(p.Name); break;
                case "sessionHudOffsetY" when TryInt(p.Value, out var n): _settings.SessionHud.OffsetY = n; applied.Add(p.Name); break;
                // Preload Alert settings
                case "preloadEnabled" when TryBool(p.Value, out var b): _settings.PreloadAlert.Enabled = b; applied.Add(p.Name); break;
                case "preloadDiagnostic" when TryBool(p.Value, out var b): _settings.PreloadAlert.Diagnostic = b; applied.Add(p.Name); break;
                case "preloadMinTier" when TryString(p.Value, out var s):
                {
                    var v = s.Trim().ToLowerInvariant();
                    if (v is "pinnacle" or "high" or "mechanic" or "interactable")
                    { _settings.PreloadAlert.MinTier = v; applied.Add(p.Name); }
                    break;
                }
                case "preloadAudioTier" when TryString(p.Value, out var s):
                {
                    var v = s.Trim().ToLowerInvariant();
                    if (v is "pinnacle" or "high" or "mechanic" or "interactable" or "off")
                    { _settings.PreloadAlert.AudioTier = v; applied.Add(p.Name); }
                    break;
                }
                case "preloadAnchor" when TryString(p.Value, out var s): _settings.PreloadAlert.Anchor = s.Trim(); applied.Add(p.Name); break;
                case "preloadCommonThreshold" when TryFloat(p.Value, out var f): _settings.PreloadAlert.CommonThreshold = Math.Clamp(f, 0.0, 1.0); applied.Add(p.Name); break;
                case "preloadWarmupZones" when TryInt(p.Value, out var n): _settings.PreloadAlert.WarmupZones = Math.Clamp(n, 1, 50); applied.Add(p.Name); break;
                case "preloadOffsetX" when TryInt(p.Value, out var n): _settings.PreloadAlert.OffsetX = n; applied.Add(p.Name); break;
                case "preloadOffsetY" when TryInt(p.Value, out var n): _settings.PreloadAlert.OffsetY = n; applied.Add(p.Name); break;
                // Zone summary panel settings
                case "zoneSummaryEnabled" when TryBool(p.Value, out var b): _settings.ZoneSummary.Enabled = b; applied.Add(p.Name); break;
                case "zoneSummaryAnchor"  when TryString(p.Value, out var s):
                    _settings.ZoneSummary.Anchor = s.Trim() is "TopLeft" or "TopRight" or "BottomLeft" or "BottomRight" ? s.Trim() : "TopRight";
                    applied.Add(p.Name); break;
                // Audio alert settings
                case "enableAudioAlerts" when TryBool(p.Value, out var b): _settings.EnableAudioAlerts = b; applied.Add(p.Name); break;
                case "audioAlertRareUnique" when TryBool(p.Value, out var b): _settings.AudioAlertRareUnique = b; applied.Add(p.Name); break;
                case "audioAlertUniqueDrop" when TryBool(p.Value, out var b): _settings.AudioAlertUniqueDrop = b; applied.Add(p.Name); break;
                case "audioAlertObjective" when TryBool(p.Value, out var b): _settings.AudioAlertObjective = b; applied.Add(p.Name); break;
                case "audioAlertRadiusCells" when TryInt(p.Value, out var n): _settings.AudioAlertRadiusCells = Math.Clamp(n, 10, 200); applied.Add(p.Name); break;
                case "webMapRevealRadiusCells" when TryInt(p.Value, out var n): _settings.WebMapRevealRadiusCells = Math.Clamp(n, 20, 200); applied.Add(p.Name); break;
                case "audioAlertVolume"   when TryInt(p.Value, out var n): _settings.AudioAlertVolume = Math.Clamp(n, 0, 100); applied.Add(p.Name); break;
                case "audioToneMonster"   when TryString(p.Value, out var s): _settings.AudioToneMonster   = s.Trim(); applied.Add(p.Name); break;
                case "audioToneItem"      when TryString(p.Value, out var s): _settings.AudioToneItem      = s.Trim(); applied.Add(p.Name); break;
                case "audioToneObjective" when TryString(p.Value, out var s): _settings.AudioToneObjective = s.Trim(); applied.Add(p.Name); break;
                case "audioAlertMechanic" when TryBool(p.Value, out var b): _settings.AudioAlertMechanic = b; applied.Add(p.Name); break;
                case "audioToneMechanic"  when TryString(p.Value, out var s): _settings.AudioToneMechanic  = s.Trim(); applied.Add(p.Name); break;
                case "firstRunSeen" when TryBool(p.Value, out var b): _settings.FirstRunSeen = b; applied.Add(p.Name); break;
                case "allowLanAccess" when TryBool(p.Value, out var lan): _settings.AllowLanAccess = lan; applied.Add(p.Name); break;
                case "enableWebMap" when TryBool(p.Value, out var em): _settings.EnableWebMap = em; applied.Add(p.Name); break;
                case "enableWebObs" when TryBool(p.Value, out var eo): _settings.EnableWebObs = eo; applied.Add(p.Name); break;
                case "webObsSafeDelaySec" when TryInt(p.Value, out var wsd):
                    _settings.WebObsSafeDelaySec = System.Math.Clamp(wsd, 0, 600); applied.Add(p.Name); break;
                case "webObsSafeMaskZoneName" when TryBool(p.Value, out var wsm):
                    _settings.WebObsSafeMaskZoneName = wsm; applied.Add(p.Name); break;
                case "webObsSafeHideoutBlur" when TryBool(p.Value, out var wsh):
                    _settings.WebObsSafeHideoutBlur = wsh; applied.Add(p.Name); break;
                case "webObsSafeEntityNameFog" when TryBool(p.Value, out var wsf):
                    _settings.WebObsSafeEntityNameFog = wsf; applied.Add(p.Name); break;
                case "enableDropTimeline" when TryBool(p.Value, out var dt): _settings.EnableDropTimeline = dt; applied.Add(p.Name); break;
                case "enableItemFilterLiveCounters" when TryBool(p.Value, out var lc): _settings.EnableItemFilterLiveCounters = lc; applied.Add(p.Name); break;
                case "enableInventoryHighlights" when TryBool(p.Value, out var ih): _settings.EnableInventoryHighlights = ih; applied.Add(p.Name); break;
                // Atlas colour groups (#7): the dashboard re-POSTs the full array on edit.
                case "atlasGroups" when p.Value.ValueKind == JsonValueKind.Array:
                    if (TryParseAtlasGroups(p.Value, out var atlasGrps)) {
                        _settings.AtlasGroups = atlasGrps;
                        if (!_settings.AppliedMigrations.Contains("seed:atlas-groups"))
                            _settings.AppliedMigrations.Add("seed:atlas-groups");
                        applied.Add(p.Name);
                    }
                    break;
                case "atlasRouteArrowSpacing" when TryFloat(p.Value, out var f): _settings.AtlasRouteArrowSpacing = Math.Clamp(f, 2f, 60f); applied.Add(p.Name); break;
                case "atlasShowContentIcons" when TryBool(p.Value, out var b): _settings.AtlasShowContentIcons = b; applied.Add(p.Name); break;
                case "atlasContentIconSize" when TryFloat(p.Value, out var f): _settings.AtlasContentIconSize = Math.Clamp(f, 8f, 64f); applied.Add(p.Name); break;
                case "atlasShowRoute" when TryBool(p.Value, out var b): _settings.AtlasShowRoute = b; applied.Add(p.Name); break;
                case "atlasAutoRoute" when TryBool(p.Value, out var b): _settings.AtlasAutoRoute = b; applied.Add(p.Name); break;
                case "atlasAutoRouteMaxHops" when TryInt(p.Value, out var n): _settings.AtlasAutoRouteMaxHops = Math.Clamp(n, 0, 32); applied.Add(p.Name); break;
                case "atlasShowBiomeBorder" when TryBool(p.Value, out var b): _settings.AtlasShowBiomeBorder = b; applied.Add(p.Name); break;
                // Off-screen entity arrows: whole-object write (the dashboard POSTs the full sub-object).
                case "entityArrows" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseEntityArrows(p.Value, out var ea)) { _settings.EntityArrows = ea; applied.Add(p.Name); }
                    break;
                // OBS overlay: whole-object write; PanelOpacity clamped 0-100, Scale 0.5-3.0, TextColor validated, Corner sanitized.
                case "obsOverlay" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseObsOverlay(p.Value, out var obs)) { _settings.ObsOverlay = obs; applied.Add(p.Name); }
                    break;
                // Discord Presence: whole-object write; ClientId sanitized to digits only (≤32); templates capped at 128 chars.
                // ClientId is never logged — only stored and passed to the SDK.
                case "discordPresence" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseDiscordPresence(p.Value, out var dp))
                    {
                        // ClientId is omitted from the GET (write-only, stream-safe), so the dashboard POSTs it
                        // blank when the user edits other RP fields. An empty incoming id must PRESERVE the saved
                        // one (not wipe it) — to stop RP you toggle Enabled off, you don't blank the id.
                        if (string.IsNullOrEmpty(dp.ClientId)) dp.ClientId = _settings.DiscordPresence.ClientId;
                        _settings.DiscordPresence = dp; applied.Add(p.Name);
                    }
                    break;
                // Auto-update: whole-object write; Mode validated against the allowed set.
                case "autoUpdate" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseAutoUpdate(p.Value, out var au)) { _settings.AutoUpdate = au; applied.Add(p.Name); }
                    break;
                // Anything else (apiPort, unknown keys) is ignored by design.
            }
        }

        if (applied.Any(k => k.StartsWith("audioAlert", StringComparison.Ordinal) || k.StartsWith("audioTone", StringComparison.Ordinal)))
            _rebuildAudio?.Invoke();
        if (applied.Count > 0) _settings.Save();
        return applied.ToArray();
    }

    private static string FormatTimeSpan(TimeSpan t) =>
        $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

    private static readonly Regex HexColor = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    /// <summary>Deserialize + sanitize a full <see cref="RadarStyles"/> from posted JSON. Returns false
    /// (and leaves settings untouched) if the JSON can't be parsed.</summary>
    private static bool TryParseStyles(JsonElement el, out RadarStyles styles)
    {
        styles = new RadarStyles();
        try
        {
            var parsed = JsonSerializer.Deserialize<RadarStyles>(el.GetRawText(), Json);
            if (parsed == null) return false;
            foreach (var ic in new[] { parsed.MonsterNormal, parsed.MonsterMagic, parsed.MonsterRare, parsed.MonsterUnique,
                                       parsed.Player, parsed.Npc, parsed.ChestRare, parsed.ChestUnique, parsed.Transition, parsed.Poi, parsed.Landmark })
                SanitizeIcon(ic);
            parsed.Mechanics ??= new List<MechanicStyle>();
            if (parsed.Mechanics.Count > 24) parsed.Mechanics = parsed.Mechanics.Take(24).ToList();
            foreach (var m in parsed.Mechanics)
            {
                m.Shape = IconLibrary.Canonical(m.Shape) ?? "Circle";
                m.Color = m.Color != null && HexColor.IsMatch(m.Color) ? m.Color.ToUpperInvariant() : "#FFFFFF";
                m.Opacity = Math.Clamp(m.Opacity, 0f, 1f);
                m.Size = Math.Clamp(m.Size, 0.5f, 40f);
                m.Name = (m.Name ?? "").Trim();
                if (m.Name.Length > 40) m.Name = m.Name[..40];
                m.Match ??= new List<string>();
                m.Match = m.Match.Where(x => !string.IsNullOrWhiteSpace(x))
                                 .Select(x => x.Trim() is var t && t.Length > 64 ? t[..64] : x.Trim())
                                 .Take(8).ToList();
                // Keep only valid EntityCategory names (canonicalized), deduped. Empty = applies to all.
                m.Categories ??= new List<string>();
                m.Categories = m.Categories
                    .Select(x => Enum.TryParse<Poe2Live.EntityCategory>(x, ignoreCase: true, out var c) ? c.ToString() : null)
                    .Where(x => x != null).Distinct().ToList()!;
            }
            styles = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static void SanitizeIcon(IconStyle s)
    {
        s.Shape = IconLibrary.Canonical(s.Shape) ?? "Circle";
        s.Color = s.Color != null && HexColor.IsMatch(s.Color) ? s.Color.ToUpperInvariant() : "#FFFFFF";
        s.Opacity = Math.Clamp(s.Opacity, 0f, 1f);
        s.Size = Math.Clamp(s.Size, 0.5f, 40f);
    }

    /// <summary>Return <paramref name="c"/> upper-cased if it's a valid #RRGGBB, else <paramref name="fallback"/>.</summary>
    private static string ValidHexOr(string? c, string fallback)
        => c != null && HexColor.IsMatch(c) ? c.ToUpperInvariant() : fallback;

    /// <summary>Deserialize + clamp a full <see cref="HpBarSettings"/> from posted JSON.</summary>
    private static bool TryParseHpBars(JsonElement el, out HpBarSettings hp)
    {
        hp = new HpBarSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<HpBarSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.Height = Math.Clamp(parsed.Height, 1f, 30f);
            parsed.OffsetX = Math.Clamp(parsed.OffsetX, -200f, 200f);
            parsed.OffsetY = Math.Clamp(parsed.OffsetY, -200f, 200f);
            parsed.WidthNormal = Math.Clamp(parsed.WidthNormal, 4f, 400f);
            parsed.WidthMagic = Math.Clamp(parsed.WidthMagic, 4f, 400f);
            parsed.WidthRare = Math.Clamp(parsed.WidthRare, 4f, 400f);
            parsed.WidthUnique = Math.Clamp(parsed.WidthUnique, 4f, 400f);
            parsed.BorderNormal = Math.Clamp(parsed.BorderNormal, 0f, 20f);
            parsed.BorderMagic = Math.Clamp(parsed.BorderMagic, 0f, 20f);
            parsed.BorderRare = Math.Clamp(parsed.BorderRare, 0f, 20f);
            parsed.BorderUnique = Math.Clamp(parsed.BorderUnique, 0f, 20f);
            parsed.BorderColorNormal = ValidHexOr(parsed.BorderColorNormal, "#FF3333");
            parsed.BorderColorMagic = ValidHexOr(parsed.BorderColorMagic, "#73A6FF");
            parsed.BorderColorRare = ValidHexOr(parsed.BorderColorRare, "#FFD926");
            parsed.BorderColorUnique = ValidHexOr(parsed.BorderColorUnique, "#FF7300");
            hp = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Deserialize + sanitize a full <see cref="TerrainSettings"/> from posted JSON. Colors are
    /// validated as #RRGGBB (falling back to the defaults) and opacities clamped to 0..1.</summary>
    private static bool TryParseTerrain(JsonElement el, out TerrainSettings t)
    {
        t = new TerrainSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<TerrainSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.InteriorColor = parsed.InteriorColor != null && HexColor.IsMatch(parsed.InteriorColor) ? parsed.InteriorColor.ToUpperInvariant() : "#506482";
            parsed.EdgeColor = parsed.EdgeColor != null && HexColor.IsMatch(parsed.EdgeColor) ? parsed.EdgeColor.ToUpperInvariant() : "#3CDCFF";
            parsed.InteriorOpacity = Math.Clamp(parsed.InteriorOpacity, 0f, 1f);
            parsed.EdgeOpacity = Math.Clamp(parsed.EdgeOpacity, 0f, 1f);
            t = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryParseGroundItems(JsonElement el, out GroundItemSettings g)
    {
        g = new GroundItemSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<GroundItemSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.Categories = (parsed.Categories ?? new())
                .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
            g = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Parse the atlas colour-groups array (#7) the dashboard re-POSTs on edit: each entry is
    /// <c>{ name, color (#RRGGBB), maps[] }</c>. Sanitized + capped at 32 groups; a malformed entry is
    /// skipped. Never throws — returns false only on a top-level JSON exception.</summary>
    private static bool TryParseAtlasGroups(JsonElement el, out List<AtlasMapGroup> groups)
    {
        groups = new List<AtlasMapGroup>();
        try
        {
            foreach (var g in el.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                var name = g.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? (nv.GetString() ?? "").Trim() : "";
                if (name.Length == 0) continue;   // skip nameless entries
                if (name.Length > 64) name = name[..64];
                var color = g.TryGetProperty("color", out var cv) && cv.ValueKind == JsonValueKind.String ? (cv.GetString() ?? "") : "";
                color = ValidHexOr(color, "#E0B341");
                var maps = new List<string>();
                if (g.TryGetProperty("maps", out var mv) && mv.ValueKind == JsonValueKind.Array)
                    foreach (var m in mv.EnumerateArray())
                        if (m.ValueKind == JsonValueKind.String && m.GetString() is { } ms && maps.Count < 200)
                        {
                            var s = ms.Trim();
                            if (string.IsNullOrWhiteSpace(s)) continue;
                            if (s.Length > 64) s = s[..64];
                            maps.Add(s);
                        }
                groups.Add(new AtlasMapGroup { Name = name, Color = color, Maps = maps });
                if (groups.Count >= 32) break;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Deserialize + sanitize a full <see cref="AffixNameplateSettings"/> from a posted JSON body.
    /// Clamps MaxLines (1..10) and OffsetY (-200..200); coerces Tier to a valid value; validates colors;
    /// sanitizes AlwaysShow/Hide lists (trim, drop empty, dedupe, cap 128, max 64 chars each).
    /// Returns false on malformed JSON — never throws.</summary>
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
            p.AlwaysShow = SanitizeStringList(p.AlwaysShow);
            p.Hide = SanitizeStringList(p.Hide);
            an = p;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Deserialize + sanitize a full <see cref="BuffNameplateSettings"/> from a posted JSON body.
    /// Clamps MaxLines (1..10) and OffsetY (-200..200); coerces Tier to a valid value; validates colors;
    /// sanitizes AlwaysShow/Hide lists (trim, drop empty, dedupe, cap 128, max 64 chars each).
    /// Returns false on malformed JSON — never throws.</summary>
    private static bool TryParseBuffNameplates(string body, out BuffNameplateSettings bn)
    {
        bn = new BuffNameplateSettings();
        try
        {
            var p = JsonSerializer.Deserialize<BuffNameplateSettings>(body, Json);
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

    /// <summary>Deserialize + sanitize a full <see cref="Config.EntityArrowSettings"/> from a JSON element.
    /// Clamps Size (4..40), MaxArrows (1..40), MinEdgeDistancePx (0..200). Returns false on malformed input
    /// — never throws.</summary>
    private static bool TryParseEntityArrows(JsonElement el, out Config.EntityArrowSettings ea)
    {
        ea = new Config.EntityArrowSettings();
        try
        {
            var p = JsonSerializer.Deserialize<Config.EntityArrowSettings>(el.GetRawText(), Json);
            if (p == null) return false;
            p.Size = Math.Clamp(p.Size, 4f, 40f);
            p.MaxArrows = Math.Clamp(p.MaxArrows, 1, 40);
            p.MinEdgeDistancePx = Math.Clamp(p.MinEdgeDistancePx, 0, 200);
            ea = p;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Deserialize + sanitize a full <see cref="Config.ObsOverlaySettings"/> from a JSON element.
    /// Clamps PanelOpacity to 0-100, Scale to 0.5-3.0; validates TextColor as #RRGGBB (fallback #FFFFFF);
    /// canonicalizes Corner to one of the four legal values. Returns false on malformed JSON.</summary>
    private static bool TryParseObsOverlay(JsonElement el, out Config.ObsOverlaySettings obs)
    {
        obs = new Config.ObsOverlaySettings();
        try
        {
            var p = JsonSerializer.Deserialize<Config.ObsOverlaySettings>(el.GetRawText(), Json);
            if (p == null) return false;
            p.PanelOpacity = Math.Clamp(p.PanelOpacity, 0, 100);
            p.Scale        = Math.Clamp(p.Scale, 0.5f, 3.0f);
            p.TextColor    = ValidHexOr(p.TextColor, "#FFFFFF");
            p.Corner       = p.Corner?.Trim().ToLowerInvariant() switch
            {
                "top-left"     => "top-left",
                "top-right"    => "top-right",
                "bottom-left"  => "bottom-left",
                "bottom-right" => "bottom-right",
                _              => "top-left",
            };
            obs = p;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Deserialize + sanitize a full <see cref="Config.DiscordPresenceSettings"/> from a JSON element.
    /// ClientId is trimmed and reduced to decimal digits only, capped at 32 chars (Discord snowflake format).
    /// Template strings are trimmed and capped at 128 chars. ClientId is NEVER logged or echoed.
    /// Returns false on malformed JSON.</summary>
    private static bool TryParseDiscordPresence(JsonElement el, out Config.DiscordPresenceSettings dp)
    {
        dp = new Config.DiscordPresenceSettings();
        try
        {
            var p = JsonSerializer.Deserialize<Config.DiscordPresenceSettings>(el.GetRawText(), Json);
            if (p == null) return false;
            // Strip all non-digit chars then cap at 32 (Discord snowflake max length). Never logged.
            var rawId = (p.ClientId ?? "").Trim();
            var digitsOnly = new string(rawId.Where(char.IsDigit).ToArray());
            p.ClientId = digitsOnly.Length > 32 ? digitsOnly[..32] : digitsOnly;
            // Templates: trim whitespace, cap at 128 chars.
            p.DetailsTemplate = Cap128(p.DetailsTemplate);
            p.StateTemplate   = Cap128(p.StateTemplate);
            dp = p;
            return true;
        }
        catch (JsonException) { return false; }

        static string Cap128(string? s)
        {
            var t = (s ?? "").Trim();
            return t.Length > 128 ? t[..128] : t;
        }
    }

    private static bool TryParseAutoUpdate(JsonElement e, out Config.AutoUpdateSettings v)
    {
        v = new Config.AutoUpdateSettings();
        var mode = e.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (mode is not ("off" or "notify" or "silent")) return false;
        v.Mode = mode;
        return true;
    }

    private static List<string> SanitizeStringList(List<string>? raw)
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

    /// <summary>The navigation selection as a list of {id, slot} objects (for the GET/POST payloads).</summary>
    private object[] NavSelection()
        => _navGet().Select(s => (object)new { id = s.Id, slot = s.Slot }).ToArray();

    /// <summary>Apply a posted nav command: {"toggle":"&lt;id&gt;"} toggles that target; {"clear":true}
    /// clears the whole selection. Anything else is ignored. Draw-only — sends nothing to the game.</summary>
    private void ApplyNav(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True)
        {
            _navClear();
            return;
        }
        if (root.TryGetProperty("toggle", out var toggle) && toggle.ValueKind == JsonValueKind.String)
        {
            var id = toggle.GetString();
            if (!string.IsNullOrEmpty(id)) _navToggle(id);
        }
    }

    /// <summary>Apply a posted hidden-list command: {"add":"&lt;pattern&gt;"} adds a cull pattern,
    /// {"remove":"&lt;pattern&gt;"} removes one, {"clear":true} clears all. A pattern may be a literal
    /// substring or a <c>*</c>/<c>?</c> glob. Affects only what the overlay draws/serves — never the game.</summary>
    private void ApplyHidden(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True)
        {
            _hidden.Clear();
            return;
        }
        if (root.TryGetProperty("add", out var add) && add.ValueKind == JsonValueKind.String)
        {
            var p = add.GetString();
            if (!string.IsNullOrWhiteSpace(p)) _hidden.Add(p);
        }
        if (root.TryGetProperty("remove", out var remove) && remove.ValueKind == JsonValueKind.String)
        {
            var p = remove.GetString();
            if (!string.IsNullOrWhiteSpace(p)) _hidden.Remove(p);
        }
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string SanitizeColor(string? c)
        => c != null && HexColor.IsMatch(c) ? c.ToUpperInvariant() : "#FFFFFF";

    private static string SanitizeShape(string? s)
        => IconLibrary.Canonical(s ?? "") ?? "Diamond";

    private static float SanitizeSize(JsonElement o, float fallback)
        => o.TryGetProperty("size", out var v) && TryFloat(v, out var f) ? Math.Clamp(f, 0.5f, 40f) : fallback;

    /// <summary>Replace the entire ordered display ruleset from a POST <c>{"rules":[...]}</c> — the
    /// dashboard owns the array and re-posts it on every edit (add / remove / reorder / toggle / field
    /// change), the same whole-object pattern <c>styles</c> uses. Each rule is sanitized. Also accepts
    /// <c>{"clear":true}</c>. Render-only — never touches the game.</summary>
    private void ApplyDisplayRules(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("clear", out var cl) && TryBool(cl, out var c) && c)
        {
            _displayRules.Replace(Array.Empty<DisplayRule>());
            return;
        }
        if (!root.TryGetProperty("rules", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        var list = JsonSerializer.Deserialize<List<DisplayRule>>(arr.GetRawText(), Json) ?? new();
        if (list.Count > 300) list = list.GetRange(0, 300); // sanity cap
        foreach (var r in list) SanitizeRule(r);
        _displayRules.Replace(list);
    }

    /// <summary>Apply a Gear-tab weight edit: {"setWeight":{"statId":..,"weight":n}} / {"target":n} /
    /// {"threshold":n} / {"reset":"starter"} / {"setNorm":{"statId":..,"norm":n}}.
    /// Edits the local stat-weight config only — never the game.</summary>
    private void ApplyGearWeights(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (root.TryGetProperty("reset", out var rv) && rv.GetString() == "starter")
                _gearWeights.LoadStarter();

            if (root.TryGetProperty("setWeight", out var sw) && sw.ValueKind == JsonValueKind.Object
                && sw.TryGetProperty("statId", out var sid) && sid.GetString() is { Length: > 0 } statId
                && sw.TryGetProperty("weight", out var wv) && wv.TryGetDouble(out var weight))
            {
                _gearWeights.SetWeight(statId, weight);
                if (sw.TryGetProperty("norm", out var nv) && nv.TryGetDouble(out var norm))
                    _gearWeights.SetNorm(statId, norm);
            }

            if (root.TryGetProperty("setNorm", out var sn) && sn.ValueKind == JsonValueKind.Object
                && sn.TryGetProperty("statId", out var nsid) && nsid.GetString() is { Length: > 0 } normStatId
                && sn.TryGetProperty("norm", out var nnv) && nnv.TryGetDouble(out var normVal))
                _gearWeights.SetNorm(normStatId, normVal);

            if (root.TryGetProperty("target", out var tv) && tv.TryGetDouble(out var target))
                _gearWeights.SetTarget(target);
            if (root.TryGetProperty("threshold", out var thv) && thv.TryGetDouble(out var threshold))
                _gearWeights.SetThreshold(threshold);
        }
        catch (JsonException) { /* malformed body → ignore, like the atlas handlers */ }
    }

    /// <summary>Apply a Director-tab command: {"add":{objective}} upserts; {"remove":{"id":...}} deletes.
    /// Edits the local catalog only — never the game.</summary>
    private void ApplyObjectives(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("remove", out var rem) && rem.TryGetProperty("id", out var idEl)
            && idEl.GetString() is { Length: > 0 } id)
        {
            _objectives.Remove(id);
            return;
        }
        if (root.TryGetProperty("add", out var add) && add.ValueKind == JsonValueKind.Object)
        {
            var o = JsonSerializer.Deserialize<CampaignObjective>(add.GetRawText(), Json);
            if (o != null) _objectives.Add(SanitizeObjective(o));
        }
    }

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

    /// <summary>Build the current look as a <see cref="PresetBundle"/>, serialize it, and return
    /// <c>{ name, json, code }</c> — ready to copy-paste or save as a .poe2preset file.</summary>
    private object BuildPresetExport()
    {
        var json = BuildPresetBundleJson("exported");
        var code = PresetCodec.Encode(json);
        return new { name = "exported", json, code };
    }

    /// <summary>Serialize the current look as a <see cref="PresetBundle"/> JSON string with the
    /// given <paramref name="name"/>. Shared by <see cref="BuildPresetExport"/> (export endpoint)
    /// and <c>/api/preset/save</c> (server-side save — client supplies only the name).</summary>
    private string BuildPresetBundleJson(string name)
    {
        var bundle = new PresetBundle
        {
            Schema        = 1,
            Name          = name,
            Author        = "",
            AppVersion    = UpdateChecker.Current,
            CreatedAtUtc  = DateTime.UtcNow.ToString("o"),
            Styles        = _settings.Styles,
            HpBars        = _settings.HpBars,
            Terrain       = _settings.Terrain,
            GroundItems   = _settings.GroundItems,
            ShowMonsters  = _settings.ShowMonsters,
            ShowTerrain   = _settings.ShowTerrain,
            ShowPlayerBlip = _settings.ShowPlayerBlip,
            HpBarNormal   = _settings.HpBarNormal,
            HpBarMagic    = _settings.HpBarMagic,
            HpBarRare     = _settings.HpBarRare,
            HpBarUnique   = _settings.HpBarUnique,
            DisplayRules  = _displayRules.All.ToList(),
        };
        return JsonSerializer.Serialize(bundle, JsonWhenWritingNull);
    }

    /// <summary>Apply an imported preset: accepts <c>{"code":"POE2GPS-..."}</c> or a raw
    /// <see cref="PresetBundle"/> JSON body. Sanitizes ALL fields through the existing validators;
    /// auto-backs up the current look before applying; then saves settings atomically.</summary>
    private void ApplyPresetImport(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;

        // ── 1. Resolve body → bundle JSON (unwrap code-envelope if present) ──
        string bundleJson;
        try
        {
            using var probe = JsonDocument.Parse(body);
            var root = probe.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("code", out var codeProp)
                && codeProp.ValueKind == JsonValueKind.String
                && codeProp.GetString() is { Length: > 0 } code)
            {
                if (!PresetCodec.TryDecode(code, out bundleJson))
                {
                    Console.Error.WriteLine("Preset import: invalid share-code (TryDecode failed).");
                    return;
                }
            }
            else
            {
                bundleJson = body; // body IS the bundle JSON
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Preset import: malformed JSON envelope — {ex.Message}");
            return;
        }

        // ── 2. Deserialize PresetBundle; reject unknown schema versions ──
        PresetBundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<PresetBundle>(bundleJson, JsonWhenWritingNull)
                     ?? throw new InvalidOperationException("null bundle");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Preset import: bundle parse failed — {ex.Message}");
            return;
        }
        if (bundle.Schema > 1)
        {
            Console.Error.WriteLine($"Preset import: unsupported schema {bundle.Schema} (max 1).");
            return;
        }

        // ── 3. Auto-backup current look before we touch anything ──
        try
        {
            var backupPath = Path.Combine(AppContext.BaseDirectory, "config", "presets",
                                          "backup-before-import.poe2preset");
            var current = new PresetBundle
            {
                Schema       = 1,
                Name         = "backup",
                AppVersion   = UpdateChecker.Current,
                CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                Styles       = _settings.Styles,
                HpBars       = _settings.HpBars,
                Terrain      = _settings.Terrain,
                GroundItems  = _settings.GroundItems,
                ShowMonsters  = _settings.ShowMonsters,
                ShowTerrain   = _settings.ShowTerrain,
                ShowPlayerBlip = _settings.ShowPlayerBlip,
                HpBarNormal  = _settings.HpBarNormal,
                HpBarMagic   = _settings.HpBarMagic,
                HpBarRare    = _settings.HpBarRare,
                HpBarUnique  = _settings.HpBarUnique,
                DisplayRules = _displayRules.All.ToList(),
            };
            JsonStore.AtomicWrite(backupPath, current, JsonWhenWritingNull);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Preset import: backup failed — {ex.Message}");
            // Do NOT abort — the backup failure is logged but import continues.
        }

        // ── 4. Sanitize + apply only present (non-null) fields ──
        // Styles/HpBars/Terrain/GroundItems go through the existing TryParse* validators.
        // Nullable bools applied directly when non-null.
        if (bundle.Styles != null)
        {
            var raw = JsonSerializer.Serialize(bundle.Styles, JsonWhenWritingNull);
            using var el = JsonDocument.Parse(raw);
            if (TryParseStyles(el.RootElement, out var sanitized)) _settings.Styles = sanitized;
        }
        if (bundle.HpBars != null)
        {
            var raw = JsonSerializer.Serialize(bundle.HpBars, JsonWhenWritingNull);
            using var el = JsonDocument.Parse(raw);
            if (TryParseHpBars(el.RootElement, out var sanitized)) _settings.HpBars = sanitized;
        }
        if (bundle.Terrain != null)
        {
            var raw = JsonSerializer.Serialize(bundle.Terrain, JsonWhenWritingNull);
            using var el = JsonDocument.Parse(raw);
            if (TryParseTerrain(el.RootElement, out var sanitized)) _settings.Terrain = sanitized;
        }
        if (bundle.GroundItems != null)
        {
            var raw = JsonSerializer.Serialize(bundle.GroundItems, JsonWhenWritingNull);
            using var el = JsonDocument.Parse(raw);
            if (TryParseGroundItems(el.RootElement, out var sanitized)) _settings.GroundItems = sanitized;
        }
        if (bundle.ShowMonsters.HasValue)   _settings.ShowMonsters   = bundle.ShowMonsters.Value;
        if (bundle.ShowTerrain.HasValue)    _settings.ShowTerrain    = bundle.ShowTerrain.Value;
        if (bundle.ShowPlayerBlip.HasValue) _settings.ShowPlayerBlip = bundle.ShowPlayerBlip.Value;
        if (bundle.HpBarNormal.HasValue)    _settings.HpBarNormal    = bundle.HpBarNormal.Value;
        if (bundle.HpBarMagic.HasValue)     _settings.HpBarMagic     = bundle.HpBarMagic.Value;
        if (bundle.HpBarRare.HasValue)      _settings.HpBarRare      = bundle.HpBarRare.Value;
        if (bundle.HpBarUnique.HasValue)    _settings.HpBarUnique    = bundle.HpBarUnique.Value;

        // DisplayRules: every rule goes through SanitizeRule before Replace.
        if (bundle.DisplayRules is { Count: > 0 } importedRules)
        {
            var list = importedRules.Take(300).ToList(); // sanity cap (matches ApplyDisplayRules)
            foreach (var r in list) SanitizeRule(r);
            _displayRules.Replace(list); // Generation bump auto-propagates to both threads
        }

        // ── 5. Persist settings atomically ──
        _settings.Save();
    }

    // camelCase + WhenWritingNull — used for PresetBundle serialization (export payload + backup).
    private static readonly JsonSerializerOptions JsonWhenWritingNull = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Forward the non-identifying export pack to the configured Worker URL. Returns (ok, HTTP status).</summary>
    private static async Task<(bool ok, int status)> ContributeForward(string url, string jsonPack)
    {
        try
        {
            using var content = new System.Net.Http.StringContent(jsonPack, System.Text.Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content);
            return ((int)resp.StatusCode is >= 200 and < 300, (int)resp.StatusCode);
        }
        catch { return (false, 0); }
    }

    /// <summary>Rewrites a Contribute worker URL onto a sibling route. Accepts a base URL or any
    /// existing <c>/submit-*</c> suffix, strips a trailing slash, then either replaces the terminal
    /// <c>/submit-*</c> segment or appends <c>/submit-&lt;sibling&gt;</c> when none is present.
    /// <para>A single user-configured <see cref="RadarSettings.ContributeUrl"/> therefore reaches all
    /// three of the Worker's sibling routes (<c>/submit-atlas</c>, <c>/submit-buffs</c>,
    /// <c>/submit-preload</c>) without asking the user to configure each one separately.</para>
    /// Internal for testability.</summary>
    internal static string SiblingContributeUrl(string url, string sibling)
    {
        var trimmed = (url ?? "").TrimEnd('/');
        var re = new System.Text.RegularExpressions.Regex(
            @"/submit-[a-z]+$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return re.IsMatch(trimmed) ? re.Replace(trimmed, "/submit-" + sibling) : trimmed + "/submit-" + sibling;
    }

    /// <summary>Chooses which per-boot JSONL trace file to hand to /api/contribute-trace.
    /// Rule (spec §4.2, §10 "no cross-boot correlation"): prefer the CURRENT boot's file
    /// when at least one event has been written, otherwise fall back to the newest already-
    /// closed boot's file. Null return = nothing worth sharing yet.
    /// Internal for testability — the caller composes with EventWriter properties.</summary>
    internal static string? SelectTraceFileForContribute(string? currentPath, long currentEventCount, string? mostRecentComplete)
    {
        if (!string.IsNullOrEmpty(currentPath) && currentEventCount > 0) return currentPath;
        if (!string.IsNullOrEmpty(mostRecentComplete)) return mostRecentComplete;
        return null;
    }

    /// <summary>Serializes the {install_uuid, boot_id, event_count, jsonl_gzip_b64} envelope
    /// the Worker's /submit-trace route expects. The JSONL bytes are gzipped and base64-encoded
    /// inline; the Worker unzips and validates envelope shape downstream. snake_case keys are
    /// byte-for-byte per spec §4.3. Internal for testability.</summary>
    internal static string BuildTracePack(string installUuid, string bootId, long eventCount, byte[] jsonlBytes)
    {
        using var ms = new System.IO.MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(jsonlBytes, 0, jsonlBytes.Length);
        }
        var b64 = System.Convert.ToBase64String(ms.ToArray());
        return JsonSerializer.Serialize(new
        {
            install_uuid    = installUuid,
            boot_id         = bootId,
            event_count     = eventCount,
            jsonl_gzip_b64  = b64,
        });
    }

    /// <summary>Projects <c>_buffsDiag()</c> into the Worker's <c>/submit-buffs</c> pack shape:
    /// <c>[{path, tier}, ...]</c>. Empty-list-safe (returns [] when the provider is null, throws, or
    /// returns a value without a <c>buffs</c> array).</summary>
    private List<object> BuildBuffsPack()
    {
        var list = new List<object>();
        if (_buffsDiag == null) return list;
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(_buffsDiag(), Json));
            if (doc.RootElement.TryGetProperty("buffs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in arr.EnumerateArray())
                {
                    if (b.ValueKind != JsonValueKind.Object) continue;
                    var id = b.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() ?? "" : "";
                    if (id.Length == 0) continue;
                    string? tier = null;
                    if (b.TryGetProperty("tier", out var tEl))
                        tier = tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : tEl.ToString();
                    list.Add(new { path = id, tier });
                }
            }
        }
        catch { /* diagnostic-only source; degrade to empty pack, Worker rejects and button alerts */ }
        return list;
    }

    /// <summary>Projects the <c>diagnostic</c> array of <c>_preload()</c> into the Worker's
    /// <c>/submit-preload</c> pack shape: <c>[{path, freq}, ...]</c>. Empty-list-safe. When
    /// Preload Alert's Diagnostic switch is off, the source's <c>diagnostic</c> is null and this
    /// returns [] — the Worker will 400 on empty and the button alerts the user.</summary>
    private List<object> BuildPreloadsPack()
    {
        var list = new List<object>();
        if (_preload == null) return list;
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(_preload(), Json));
            if (doc.RootElement.TryGetProperty("diagnostic", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in arr.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var path = p.TryGetProperty("path", out var pEl) && pEl.ValueKind == JsonValueKind.String
                        ? pEl.GetString() ?? "" : "";
                    if (path.Length == 0) continue;
                    double? freq = null;
                    if (p.TryGetProperty("freq", out var fEl) && fEl.ValueKind == JsonValueKind.Number)
                        freq = fEl.GetDouble();
                    list.Add(new { path, freq });
                }
            }
        }
        catch { /* diagnostic-only source; degrade to empty pack, Worker rejects and button alerts */ }
        return list;
    }

    /// <summary>Clamp/validate a posted objective: non-blank Id/Label/Category, priority 0..1000,
    /// trimmed match lists (max 32 terms), valid Rarity/Poi (else null = "any").</summary>
    private static CampaignObjective SanitizeObjective(CampaignObjective o)
    {
        static List<string>? CleanTerms(List<string>? terms) =>
            terms is { Count: > 0 }
                ? terms.Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Take(32).ToList()
                : null;

        var id = (o.Id ?? "").Trim();
        return o with
        {
            Id = id.Length > 80 ? id[..80] : id,
            Label = (o.Label ?? "").Trim() is { Length: > 0 } lbl ? (lbl.Length > 60 ? lbl[..60] : lbl) : id,
            Category = string.IsNullOrWhiteSpace(o.Category) ? "Other" : o.Category.Trim(),
            Priority = Math.Clamp(o.Priority, 0, 1000),
            Metadata = CleanTerms(o.Metadata),
            Categories = CleanTerms(o.Categories),
            LandmarkPath = CleanTerms(o.LandmarkPath),
            Rarity = OneOf(o.Rarity, "Normal", "Magic", "Rare", "Unique"),
            Poi = OneOf(o.Poi, "Yes", "No"),
            Tier = Enum.TryParse<ObjectiveTier>(o.Tier?.ToString(), ignoreCase: true, out var t)
                       ? t
                       : (ObjectiveTier?)null,
        };
    }

    // Valid rule categories: the entity categories plus the pseudo-category "Tile" (matches terrain
    // tiles by path instead of an entity).
    private static readonly string[] CategoryNames = Enum.GetNames<Poe2Live.EntityCategory>().Append("Tile").ToArray();

    /// <summary>Clamp/validate a posted rule in place: known icon shape, #RRGGBB color, 0..1 opacity,
    /// size 0.5..40, valid category names, valid condition enums (else null = "any"), trimmed text.</summary>
    private static void SanitizeRule(DisplayRule r)
    {
        r.Name = (r.Name ?? "").Trim();
        if (r.Name.Length > 60) r.Name = r.Name[..60];
        r.Categories = (r.Categories ?? new())
            .Select(c => CategoryNames.FirstOrDefault(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c != null).Select(c => c!).Distinct().ToList();
        r.Match = (r.Match ?? new()).Select(m => (m ?? "").Trim()).Where(m => m.Length > 0).Take(32).ToList();
        r.Rarity    = OneOf(r.Rarity, "Normal", "Magic", "Rare", "Unique");
        r.Reaction  = OneOf(r.Reaction, "Hostile", "Friendly");
        r.Life      = OneOf(r.Life, "Alive", "Dead");
        r.Chest     = OneOf(r.Chest, "Opened", "Unopened");
        r.Poi       = OneOf(r.Poi, "Yes", "No");
        r.Encounter = OneOf(r.Encounter, "Active", "Complete");
        r.Shape = SanitizeShape(r.Shape);
        r.Color = SanitizeColor(r.Color);
        r.Opacity = Math.Clamp(r.Opacity, 0f, 1f);
        r.Size = Math.Clamp(r.Size, 0.5f, 40f);
        r.Label = string.IsNullOrWhiteSpace(r.Label) ? null : r.Label.Trim();
        if (r.Label is { Length: > 60 }) r.Label = r.Label[..60];
    }

    private static string? OneOf(string? v, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        foreach (var a in allowed) if (string.Equals(v, a, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    /// <summary>Apply a Landmarks-tab command to the curated-label overlay:
    /// <list type="bullet">
    /// <item>{"set":{area,pattern,label}} — add / rename (string label) or suppress (null/blank label)</item>
    /// <item>{"remove":{area,pattern}} — delete the user entry (reverts to the baked label, if any)</item>
    /// <item>{"import":{area:{pattern:label|null}}} — replace the whole user overlay</item>
    /// </list>
    /// Edits curated labels only — never the game.</summary>
    private void ApplyLandmarks(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("import", out var imp) && imp.ValueKind == JsonValueKind.Object)
        {
            try { _landmarkStore.Import(JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(imp.GetRawText())); }
            catch { /* ignore malformed import */ }
            return;
        }
        if (root.TryGetProperty("set", out var set) && set.ValueKind == JsonValueKind.Object)
        {
            var area = Str(set, "area"); var pattern = Str(set, "pattern");
            var label = set.TryGetProperty("label", out var lv) && lv.ValueKind == JsonValueKind.String ? lv.GetString() : null;
            label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();   // blank → suppress
            if (!string.IsNullOrWhiteSpace(area) && !string.IsNullOrWhiteSpace(pattern))
                _landmarkStore.Set(area!.Trim(), pattern!.Trim(), label);
        }
        if (root.TryGetProperty("remove", out var rem) && rem.ValueKind == JsonValueKind.Object)
        {
            var area = Str(rem, "area"); var pattern = Str(rem, "pattern");
            if (!string.IsNullOrWhiteSpace(area) && !string.IsNullOrWhiteSpace(pattern))
                _landmarkStore.Remove(area!.Trim(), pattern!.Trim());
        }
    }

    private static bool TryBool(JsonElement e, out bool v)
    {
        if (e.ValueKind == JsonValueKind.True) { v = true; return true; }
        if (e.ValueKind == JsonValueKind.False) { v = false; return true; }
        v = false; return false;
    }

    private static bool TryString(JsonElement e, out string v)
    {
        if (e.ValueKind == JsonValueKind.String) { v = e.GetString() ?? ""; return true; }
        v = ""; return false;
    }

    /// <summary>Nullable-string variant of <see cref="TryString"/>: accepts a JSON string OR JSON null.
    /// Used for optional-override settings (e.g. <c>updateUrl</c>) where the dashboard clears the field
    /// by POSTing <c>null</c> (or an empty string, normalised by the caller). Existing <c>TryString</c>
    /// callers still require a non-null value, so their contract is unchanged.</summary>
    private static bool TryStringOrNull(JsonElement e, out string? v)
    {
        if (e.ValueKind == JsonValueKind.Null)   { v = null; return true; }
        if (e.ValueKind == JsonValueKind.String) { v = e.GetString(); return true; }
        v = null; return false;
    }

    private static bool TryFloat(JsonElement e, out float v)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out v)) return true;
        v = 0f; return false;
    }

    private static bool TryInt(JsonElement e, out int v)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v)) return true;
        v = 0; return false;
    }


    /// <summary>Best-effort list of this machine's LAN IPv4 addresses (for the dashboard's "your LAN URL"
    /// display). Skips loopback, down interfaces, and APIPA link-local (169.254.*). Never throws.</summary>
    private static string[] LanAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254", StringComparison.Ordinal)) continue; // APIPA link-local
                    list.Add(ip);
                }
            }
        }
        catch { /* odd adapters can throw during enumeration — best-effort */ }
        return list.ToArray();
    }

    private static bool IsLoopbackHost(HttpListenerRequest req)
    {
        // Authoritative check: the TCP source must actually be loopback. RemoteEndPoint.Address is set by
        // the OS from the accepted socket and CANNOT be forged by the client — unlike the Host header. This
        // is what keeps writes machine-local even when AllowLanAccess binds the listener to all interfaces
        // (a LAN peer spoofing "Host: localhost" still has a non-loopback RemoteEndPoint and is rejected).
        var remote = req.RemoteEndPoint?.Address;
        if (remote == null || !System.Net.IPAddress.IsLoopback(remote)) return false;
        // Defense-in-depth (DNS-rebinding): also require a loopback Host header.
        var host = req.UserHostName; // includes port, e.g. "localhost:7777"
        if (string.IsNullOrEmpty(host)) return false;
        var name = host.Split(':')[0];
        return name is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    private static string ReadBody(HttpListenerContext ctx)
    {
        using var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        return r.ReadToEnd();
    }

    private static float Dist(System.Numerics.Vector2 a, System.Numerics.Vector2 b)
        => (a - b).Length();

    private static void Write(HttpListenerContext ctx, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        // Read-only API: no Access-Control-Allow-Origin header, so a browser on another origin
        // cannot read these responses. The dashboard is served same-origin from "/".
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void WriteHtml(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void TryWrite(HttpListenerContext ctx, int status, string body)
    {
        try { Write(ctx, status, body); } catch { /* client gone */ }
    }

    static bool WantsGzip(HttpListenerRequest req)
    {
        var enc = req.Headers["Accept-Encoding"];
        return enc != null && enc.IndexOf("gzip", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static void WriteMaybeGzipped(HttpListenerContext ctx, byte[] payload, string mime)
    {
        ctx.Response.ContentType = mime;
        if (WantsGzip(ctx.Request))
        {
            ctx.Response.Headers["Content-Encoding"] = "gzip";
            using (var gz = new System.IO.Compression.GZipStream(ctx.Response.OutputStream,
                                                                System.IO.Compression.CompressionLevel.Fastest,
                                                                leaveOpen: false))
            {
                gz.Write(payload, 0, payload.Length);
            }
        }
        else
        {
            ctx.Response.ContentLength64 = payload.Length;
            ctx.Response.OutputStream.Write(payload, 0, payload.Length);
            ctx.Response.OutputStream.Close();
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }

    /// <summary>v0.31 Prospector: envelope for /api/item-filters GET + POST payloads.</summary>
    private sealed record ItemFiltersEnvelope(
        [property: System.Text.Json.Serialization.JsonPropertyName("filters")] IReadOnlyList<FilterRule> Filters);
}

/// <summary>One selected navigation route projected to a wire-format polyline. Grid-space coordinates
/// (matches how the native Direct2D overlay draws in <c>OverlayRenderer.DrawPaths</c>); consumers on the
/// browser side project them the same way the terrain map is projected. Additive wire field — v0.20.0
/// clients ignore it silently, so the record is defined here alongside <see cref="RadarState"/>.</summary>
public sealed record PathPolyline(IReadOnlyList<(float x, float y)> Points);

/// <summary>Immutable snapshot published by the tick loop for the API to serve.</summary>
public sealed record RadarState(
    bool InGame,
    uint AreaHash,
    int AreaLevel,
    bool MapVisible,
    float Zoom,
    System.Numerics.Vector2 Player,
    IReadOnlyList<Poe2Live.EntityDot> Entities,
    IReadOnlyList<Poe2Live.Landmark> Landmarks,
    float HpPct,
    float ManaPct,
    float EsPct,
    string AreaCode,
    string CharName,
    int CharLevel,
    // Threading validation timers: the last world-pass duration (background thread) and the last
    // render-frame duration (render thread), in milliseconds. Surfaced via /state for stress-testing.
    float WorldMs = 0,
    float RenderMs = 0,
    // Runeshape monoliths in the current area (slot count, anchor, best value + full priced reward set) —
    // served to the dashboard's Monolith Rewards card. Empty when none / feature disabled.
    IReadOnlyList<MonolithMarker>? Monoliths = null,
    // Objective Director queue (active objective first) for the dashboard panel; null/empty when off.
    IReadOnlyList<POE2Radar.Core.Campaign.RankedObjective>? Director = null,
    // Measured effective render FPS (rolling window) — for verifying the overlay actually hits FpsCap.
    float Fps = 0,
    // Session HUD data: elapsed times, pace, zone context, deaths. Null when tracker not running.
    SessionStats? Session = null,
    // Patch-resilience health: State drives the dashboard Status ticks; Message is the banner/Status text
    // (null when healthy / benign). Optional + trailing so RadarState.Empty (positional) is unaffected.
    HealthState Health = HealthState.Searching,
    string? HealthMessage = null,
    // Campaign GPS cross-zone instruction for the dashboard banner; null when off / no instruction.
    string? CampaignGps = null,
    float RpmPerSec = 0,
    // v0.21 EC2 Guided Campaign — additive-only. Null when EnableCampaignGps is off, the embedded route
    // failed to load, or the cursor has walked off the end. v0.20.x clients see a null/omitted key and
    // ignore it — the record's positional 13-arg RadarState.Empty ctor call below stays untouched, so
    // wire-format compat is guaranteed by construction.
    POE2Radar.Core.Campaign.Guide.CampaignStepInstruction? CampaignGuide = null)
{
    public static readonly RadarState Empty =
        new(false, 0, 0, false, 0, System.Numerics.Vector2.Zero,
            Array.Empty<Poe2Live.EntityDot>(), Array.Empty<Poe2Live.Landmark>(), 100, 100, 100, "", "", 0);

    /// <summary>v0.20.1 T9: selected-target route polylines, projected from
    /// <c>OverlayRenderer.ctx.SelectedPaths</c> in <c>RadarApp.Tick()</c> without any new memory reads.
    /// Defaults to an empty array (never null) so wire-format projection is O(1) when no targets are
    /// selected. Set via object initializer on the <see cref="RadarState"/> construction site.</summary>
    public IReadOnlyList<PathPolyline> Paths { get; init; } = Array.Empty<PathPolyline>();
}
