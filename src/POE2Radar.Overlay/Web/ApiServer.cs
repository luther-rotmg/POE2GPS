using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private volatile bool _running;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ApiServer(
        Func<RadarState> state,
        RadarSettings settings,
        Func<IReadOnlyList<(string Id, int Slot)>> navGet,
        Action<string> navToggle,
        Action navClear,
        int port = 7777)
    {
        _state = state;
        _settings = settings;
        _navGet = navGet;
        _navToggle = navToggle;
        _navClear = navClear;
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
            catch (Exception ex) { TryWrite(ctx, 500, JsonSerializer.Serialize(new { error = ex.Message }, Json)); }
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
                    mapVisible = s.MapVisible, zoom = s.Zoom,
                    hpPct = s.HpPct, manaPct = s.ManaPct, autoFlask = s.AutoFlask, flask = s.FlaskNote,
                    player = new { x = s.Player.X, y = s.Player.Y },
                    entityCount = s.Entities.Count,
                    poiCount = s.Entities.Count(e => e.Poi),
                    landmarkCount = s.Landmarks.Count,
                    counts,
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
                        id = e.Id, addr = $"0x{e.Address:X}", category = e.Category.ToString(), metadata = e.Metadata,
                        poi = e.Poi, reaction = e.Reaction, friendly = e.IsFriendly, rarity = e.Rarity.ToString(),
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

            default:
                Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found", path }, Json));
                break;
        }
    }

    /// <summary>
    /// The settings the dashboard may read AND write. Covers radar/visual options plus auto-flask
    /// tuning (thresholds, cooldowns, keys). All writes are loopback-Host-gated (see Handle), so a
    /// cross-origin site can't reach them. The API port is read-only here (changing it needs a
    /// restart). This object also doubles as the GET payload.
    /// </summary>
    private object ReadSettings() => new
    {
        hideJunk = _settings.HideJunk,
        showPath = _settings.ShowPath,
        useCuratedLandmarks = _settings.UseCuratedLandmarks,
        showMonsters = _settings.ShowMonsters,
        showTerrain = _settings.ShowTerrain,
        hpBarNormal = _settings.HpBarNormal,
        hpBarMagic = _settings.HpBarMagic,
        hpBarRare = _settings.HpBarRare,
        hpBarUnique = _settings.HpBarUnique,
        scaleMul = _settings.ScaleMul,
        offX = _settings.OffX,
        offY = _settings.OffY,
        lifeThresholdPct = _settings.LifeThresholdPct,
        manaThresholdPct = _settings.ManaThresholdPct,
        lifeCooldownMs = _settings.LifeCooldownMs,
        manaCooldownMs = _settings.ManaCooldownMs,
        lifeKey = _settings.LifeKey,
        manaKey = _settings.ManaKey,
        apiPort = _settings.ApiPort, // display only — changing it needs a restart
        styles = _settings.Styles,   // per-item icon shapes/colors/sizes + mechanic overrides
        hpBars = _settings.HpBars,   // monster HP-bar geometry (width/height/offset)
        terrain = _settings.Terrain, // walkable-terrain bitmap colors/transparency
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
                case "useCuratedLandmarks" when TryBool(p.Value, out var b): _settings.UseCuratedLandmarks = b; applied.Add(p.Name); break;
                case "scaleMul" when TryFloat(p.Value, out var f): _settings.ScaleMul = f; applied.Add(p.Name); break;
                case "offX" when TryFloat(p.Value, out var f): _settings.OffX = f; applied.Add(p.Name); break;
                case "offY" when TryFloat(p.Value, out var f): _settings.OffY = f; applied.Add(p.Name); break;
                case "showMonsters" when TryBool(p.Value, out var b): _settings.ShowMonsters = b; applied.Add(p.Name); break;
                case "showTerrain" when TryBool(p.Value, out var b): _settings.ShowTerrain = b; applied.Add(p.Name); break;
                case "hpBarNormal" when TryBool(p.Value, out var b): _settings.HpBarNormal = b; applied.Add(p.Name); break;
                case "hpBarMagic" when TryBool(p.Value, out var b): _settings.HpBarMagic = b; applied.Add(p.Name); break;
                case "hpBarRare" when TryBool(p.Value, out var b): _settings.HpBarRare = b; applied.Add(p.Name); break;
                case "hpBarUnique" when TryBool(p.Value, out var b): _settings.HpBarUnique = b; applied.Add(p.Name); break;
                case "lifeThresholdPct" when TryFloat(p.Value, out var f): _settings.LifeThresholdPct = Math.Clamp(f, 0f, 100f); applied.Add(p.Name); break;
                case "manaThresholdPct" when TryFloat(p.Value, out var f): _settings.ManaThresholdPct = Math.Clamp(f, 0f, 100f); applied.Add(p.Name); break;
                case "lifeCooldownMs" when TryInt(p.Value, out var n): _settings.LifeCooldownMs = Math.Clamp(n, 0, 60000); applied.Add(p.Name); break;
                case "manaCooldownMs" when TryInt(p.Value, out var n): _settings.ManaCooldownMs = Math.Clamp(n, 0, 60000); applied.Add(p.Name); break;
                case "lifeKey" when TryInt(p.Value, out var n): _settings.LifeKey = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                case "manaKey" when TryInt(p.Value, out var n): _settings.ManaKey = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
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
    bool AutoFlask,
    string FlaskNote,
    string AreaCode,
    string CharName,
    int CharLevel)
{
    public static readonly RadarState Empty =
        new(false, 0, 0, false, 0, System.Numerics.Vector2.Zero,
            Array.Empty<Poe2Live.EntityDot>(), Array.Empty<Poe2Live.Landmark>(), 100, 100, false, "", "", "", 0);
}
