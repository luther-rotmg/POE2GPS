using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;
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
    private volatile bool _running;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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
        GearWeightStore gearWeights,
        Func<object>? atlasProvider = null,
        Action<IReadOnlyList<long>>? atlasSelect = null,
        Action<IReadOnlyList<(string tag, string color, bool track, bool nav, bool arrow)>>? atlasHighlight = null,
        Func<object>? versionProvider = null,
        int port = 7777)
    {
        _state = state;
        _atlas = atlasProvider;
        _atlasSelect = atlasSelect;
        _atlasHighlight = atlasHighlight;
        _version = versionProvider;
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
        _gearWeights = gearWeights;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        var t = new Thread(Loop) { IsBackground = true, Name = "POE2Radar.Api" };
        t.Start();
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { return; } // listener stopped
            try { Handle(ctx); }
            catch (Exception ex)
            {
                // Log the detail locally, but return a GENERIC body — raw exception messages can carry
                // memory-read internals (addresses / struct names) we don't want on the wire.
                Console.Error.WriteLine($"API handler error: {ex.Message}");
                TryWrite(ctx, 500, JsonSerializer.Serialize(new { error = "internal error" }, Json));
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var q = ctx.Request.QueryString;
        var s = _state();

        switch (path)
        {
            case "/":
                WriteHtml(ctx, DashboardHtml.Page);
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
                    worldMs = s.WorldMs, renderMs = s.RenderMs, fps = s.Fps,
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
                        .Select(o => new { id = o.Id, label = o.Label, category = o.Category, priority = o.Priority }),
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

            case "/landmarks":
            {
                var list = s.Landmarks
                    .OrderBy(l => Dist(l.Center, s.Player))
                    .Select(l => new
                    {
                        name = l.Name, curatedName = l.CuratedName, path = l.Path, tiles = l.TileCount,
                        x = l.Center.X, y = l.Center.Y, dist = (int)Dist(l.Center, s.Player),
                    });
                Write(ctx, 200, JsonSerializer.Serialize(list, Json));
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
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, applied, settings = ReadSettings() }, Json));
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
                    pois = _seenPois().Select(p => new
                    {
                        signature = p.Signature, name = p.FriendlyName, category = p.Category,
                        zone = p.FirstZone, count = p.Count, poi = p.Poi,
                        metadata = p.Metadata, landmarkPath = p.LandmarkPath,
                        covered = _objectives.Covers(p),
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
                        return new
                        {
                            metadata = a.Metadata,
                            name = named ?? EntityNameResolver.Shared.ResolveOrShorten(a.Metadata),
                            named = named != null,
                            category = a.Category, rarity = a.Rarity, poi = a.Poi,
                            zone = a.FirstZone, count = a.Count,
                            notable = PoiCandidate.IsCandidate(in e),
                            covered = _objectives.Covers(in e),
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

            case "/api/gear":
                // God-Roll Detector (experimental, default off): the scored inventory snapshot. Item stats
                // + scores only — no character/account data. {enabled:false,items:[]} when off.
                Write(ctx, 200, JsonSerializer.Serialize(_gear(), Json));
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

            case "/api/atlas":
                // Inspection view of the atlas map-data we can read (catalog + current-region map set).
                // Read-only; the provider scans + caches, so the first call after entering the atlas
                // may take a moment. Returns {located:false,...} when the catalog can't be found.
                Write(ctx, 200, JsonSerializer.Serialize(_atlas?.Invoke() ?? new { located = false, note = "atlas reader unavailable" }, Json));
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

            default:
                Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found", path }, Json));
                break;
        }
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
        excludeFromCapture = _settings.ExcludeFromCapture,
        checkForUpdates = _settings.CheckForUpdates,
        enableGearScorer = _settings.EnableGearScorer,
        enableTargetHotkeys = _settings.EnableTargetHotkeys,
        enableControllerCycle = _settings.EnableControllerCycle,
        showMonolithPanel = _settings.Monoliths.ShowPanel,
        fpsCap = _settings.FpsCap,
        hpBarNormal = _settings.HpBarNormal,
        hpBarMagic = _settings.HpBarMagic,
        hpBarRare = _settings.HpBarRare,
        hpBarUnique = _settings.HpBarUnique,
        scaleMul = _settings.ScaleMul,
        offX = _settings.OffX,
        offY = _settings.OffY,
        apiPort = _settings.ApiPort, // display only — changing it needs a restart
        styles = _settings.Styles,   // per-item icon shapes/colors/sizes + mechanic overrides
        hpBars = _settings.HpBars,   // monster HP-bar geometry (width/height/offset)
        terrain = _settings.Terrain, // walkable-terrain bitmap colors/transparency
        groundItems = _settings.GroundItems, // ground-item value overlay (enabled / highlight threshold / league)
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
                case "excludeFromCapture" when TryBool(p.Value, out var b): _settings.ExcludeFromCapture = b; applied.Add(p.Name); break;
                case "checkForUpdates" when TryBool(p.Value, out var b): _settings.CheckForUpdates = b; applied.Add(p.Name); break;
                case "enableGearScorer" when TryBool(p.Value, out var b): _settings.EnableGearScorer = b; applied.Add(p.Name); break;
                case "enableTargetHotkeys" when TryBool(p.Value, out var b): _settings.EnableTargetHotkeys = b; applied.Add(p.Name); break;
                case "enableControllerCycle" when TryBool(p.Value, out var b): _settings.EnableControllerCycle = b; applied.Add(p.Name); break;
                case "showMonolithPanel" when TryBool(p.Value, out var b): _settings.Monoliths.ShowPanel = b; applied.Add(p.Name); break;
                case "fpsCap" when TryInt(p.Value, out var n): _settings.FpsCap = Math.Clamp(n, 15, 360); applied.Add(p.Name); break;
                case "hpBarNormal" when TryBool(p.Value, out var b): _settings.HpBarNormal = b; applied.Add(p.Name); break;
                case "hpBarMagic" when TryBool(p.Value, out var b): _settings.HpBarMagic = b; applied.Add(p.Name); break;
                case "hpBarRare" when TryBool(p.Value, out var b): _settings.HpBarRare = b; applied.Add(p.Name); break;
                case "hpBarUnique" when TryBool(p.Value, out var b): _settings.HpBarUnique = b; applied.Add(p.Name); break;
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
                // Anything else (apiPort, unknown keys) is ignored by design.
            }
        }

        if (applied.Count > 0) _settings.Save();
        return applied.ToArray();
    }

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
    /// {"threshold":n}. Edits the local stat-weight config only — never the game.</summary>
    private void ApplyGearWeights(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("setWeight", out var sw) && sw.ValueKind == JsonValueKind.Object
            && sw.TryGetProperty("statId", out var sid) && sid.GetString() is { Length: > 0 } statId
            && sw.TryGetProperty("weight", out var wv) && wv.TryGetDouble(out var weight))
            _gearWeights.SetWeight(statId, weight);
        if (root.TryGetProperty("target", out var tv) && tv.TryGetDouble(out var target))
            _gearWeights.SetTarget(target);
        if (root.TryGetProperty("threshold", out var thv) && thv.TryGetDouble(out var threshold))
            _gearWeights.SetThreshold(threshold);
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


    private static bool IsLoopbackHost(HttpListenerRequest req)
    {
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

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}

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
    float Fps = 0)
{
    public static readonly RadarState Empty =
        new(false, 0, 0, false, 0, System.Numerics.Vector2.Zero,
            Array.Empty<Poe2Live.EntityDot>(), Array.Empty<Poe2Live.Landmark>(), 100, 100, 100, "", "", 0);
}
