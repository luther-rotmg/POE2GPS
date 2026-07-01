using System.Globalization;
using System.Numerics;
using POE2Radar.Core.Game;
using POE2Radar.Core.Health;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Overlay.Config;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using NumVec2 = System.Numerics.Vector2;
using GameVec2 = POE2Radar.Core.Game.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// PoE2 radar overlay. When the large map is open, draws the walkable-terrain bitmap, entity
/// dots (enemies, NPCs, etc.), and the player blip, projected player-centered onto the map with
/// the same isometric math the PoE Radar plugin uses. Projection scale/offset are calibratable
/// at runtime (see <see cref="RadarApp"/>).
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    // Entity dot colors now live per-item in RadarSettings.Styles (these were the old hardcoded
    // values, preserved as the style defaults). Only the HUD/nav/landmark-label colors remain here.
    private static readonly Color4 ColPlayer  = new(0.30f, 0.95f, 1.00f, 1.00f);
    private static readonly Color4 ColOther   = new(0.70f, 0.70f, 0.70f, 0.60f);
    private static readonly Color4 ColText    = new(1f, 1f, 1f, 1f);
    private static readonly Color4 ColPanel   = new(0.05f, 0.05f, 0.05f, 0.78f);
    private static readonly Color4 ColLandmark = new(0.95f, 0.35f, 0.95f, 1f); // magenta — static tile landmarks
    private static readonly Color4 ColTargetMark = new(1f, 1f, 1f, 1f);          // active-target highlight in the legend
    private static readonly Color4 ColLowHp   = new(1.00f, 0.20f, 0.20f, 0.95f); // HP-bar fill when below 30% (rarity-independent)

    // Distinct, evenly-spread hues for per-landmark guidance paths / legend swatches.
    private static readonly Color4[] PathPalette =
    {
        new(0.20f, 0.90f, 0.40f, 1f), // green
        new(1.00f, 0.55f, 0.10f, 1f), // orange
        new(0.30f, 0.70f, 1.00f, 1f), // sky blue
        new(1.00f, 0.30f, 0.70f, 1f), // pink
        new(0.95f, 0.90f, 0.20f, 1f), // yellow
        new(0.60f, 0.40f, 1.00f, 1f), // violet
        new(0.20f, 1.00f, 0.85f, 1f), // teal
        new(1.00f, 0.40f, 0.40f, 1f), // salmon
    };

    /// <summary>The color a guidance path (and its legend swatch) draws in, by selection-order color slot.</summary>
    private static Color4 PathColor(int slot) => PathPalette[((slot % PathPalette.Length) + PathPalette.Length) % PathPalette.Length];

    /// <summary>
    /// Screen rectangles of the interactive navigation-menu widget, rebuilt every frame the widget
    /// draws (and cleared when the overlay isn't Active/InGame). RadarApp hit-tests pointer clicks
    /// against these to toggle the menu, pin a corner, or toggle a path target. Each entry pairs a
    /// rect with an Action string: <c>"menu-toggle"</c>, <c>"corner:TopLeft|TopRight|BottomLeft|
    /// BottomRight"</c>, or <c>"target:&lt;navTargetId&gt;"</c> (dropdown rows, only when expanded).
    /// </summary>
    public IReadOnlyList<(Vortice.RawRectF Rect, string Action)> LegendRowRects => _legendRowRects;
    private readonly List<(Vortice.RawRectF Rect, string Action)> _legendRowRects = new();

    private readonly OverlayWindow _window;
    private TerrainBitmap? _terrain;
    private AtlasIconCache? _atlasIcons;   // #5: decoded atlas content-icon bitmaps (lazy per render target)

    // Per-icon-name geometry, built lazily from the SVG IconLibrary and cached for the renderer's
    // lifetime. A name that can't be resolved/parsed is mapped to the Circle geometry so something
    // always draws; the cache may therefore point several keys at one instance (deduped on Dispose).
    private readonly Dictionary<string, ID2D1PathGeometry?> _geoCache = new(StringComparer.OrdinalIgnoreCase);

    private ID2D1SolidColorBrush? _bPlayer, _bOther, _bText, _bPanel, _bLandmark;
    private ID2D1SolidColorBrush? _bPath;  // recolored per route via SetColor
    private ID2D1SolidColorBrush? _bStyle; // scratch brush for config-driven icons / HP bars (recolored per draw)
    private IDWriteTextFormat? _tf;
    private bool _ready;

    // ── D1: Session-HUD line cache (render-thread-owned). Rebuilt only when any input changes. ──
    private (string text, bool isDeath)[]? _hudLines;
    private int _hudCacheSessionSec, _hudCacheZoneSec, _hudCacheZones, _hudCacheAreaLevel, _hudCacheDeaths, _hudCacheDeathsHere;
    private float _hudCacheZonesPerHr;
    private string? _hudCacheZoneName;
    private bool _hudCacheShowPace, _hudCacheShowZone, _hudCacheShowDeaths;
    private int _hudCacheKillsN, _hudCacheKillsM, _hudCacheKillsR, _hudCacheKillsU;
    private float _hudCacheMapsHr;
    private int _hudCacheXpEff;
    private bool _hudCacheShowKills;

    // ── D2: Terrain color cache + per-rule color memo (render-thread-owned). ──
    private string _terrainCacheIntHex = "", _terrainCacheEdgeHex = "";
    private float  _terrainCacheIntOp = -1f, _terrainCacheEdgeOp = -1f;
    private Color4 _terrainColorInterior, _terrainColorEdge;
    private readonly Dictionary<Web.DisplayRule, Color4> _ruleColorMemo = new(ReferenceEqualityComparer.Instance);
    private int _ruleColorGen = -1;

    public OverlayRenderer(OverlayWindow window) { _window = window; }

    private void EnsureResources()
    {
        if (_ready) return;
        var rt = _window.RenderTarget;
        _bPlayer   = rt.CreateSolidColorBrush(ColPlayer);
        _bOther    = rt.CreateSolidColorBrush(ColOther);
        _bText     = rt.CreateSolidColorBrush(ColText);
        _bPanel    = rt.CreateSolidColorBrush(ColPanel);
        _bLandmark = rt.CreateSolidColorBrush(ColLandmark);
        _bPath     = rt.CreateSolidColorBrush(PathPalette[0]);
        _bStyle    = rt.CreateSolidColorBrush(ColText);
        _tf = _window.DWriteFactory.CreateTextFormat("Consolas", null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 12f, "en-us");
        _atlasIcons = new AtlasIconCache();   // decode embedded atlas content PNGs once (#5)
        _ready = true;
    }

    public void Render(RenderContext ctx)
    {
        if (!_window.IsValid) return;
        EnsureResources();
        var rt = _window.RenderTarget;
        rt.BeginDraw();
        rt.Clear(new Color4(0f, 0f, 0f, 0f));
        rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        try
        {
            // Draw nothing unless PoE2 is the foreground window — so the overlay never shows
            // over other apps when you alt-tab. (The cleared frame above hides prior content.)
            if (ctx.Active && ctx.InGame && ctx.AtlasOpen)
            {
                // The Atlas screen is open: draw the highlight rings + off-screen arrows and the F10 route,
                // never the world radar/minimap (meaningless over the atlas).
                DrawAtlasRoute(rt, ctx);                   // F10 route line + START/END markers (under the rings)
                DrawAtlas(rt, ctx);                        // tracked-map rings + off-screen arrows
                _legendRowRects.Clear();
            }
            else if (ctx.Active && ctx.InGame)
            {
                DrawCycleIndicator(rt, ctx);               // transient active-target indicator (post-cycle flash)
                DrawNameplates(rt, ctx);                   // world-space HP bars over hostile mobs
                DrawEntityArrows(rt, ctx);                 // off-screen entity edge arrows (uniques/bosses/etc.)
                DrawItemLabels(rt, ctx);                   // priced unique drops over their loot icons
                DrawAffixNameplates(rt, ctx);              // tiered affix text above elite mobs
                if (ctx.Map.IsVisible)
                    DrawMap(rt, ctx);                      // terrain + dots + on-map path polylines
                else
                    DrawPathsWorld(rt, ctx);               // ground waypoints + lines when the map is closed

                // The navigation-menu widget is ALWAYS interactive in-game (map open or not). It
                // (re)builds _legendRowRects, so it must run last; nothing else touches those rects now.
                DrawNavMenu(rt, ctx);
            }
            else
            {
                _legendRowRects.Clear(); // not active/in-game: no stale click rects
            }

            // Rune-crafting reward prices: screen-space labels drawn on top of whatever's below (radar
            // or atlas), gated only on the panel being open (RuneLabels populated). No-op otherwise.
            if (ctx.Active && ctx.InGame)
            {
                DrawRuneforge(rt, ctx);
                DrawRitualRewards(rt, ctx);            // name labels on the ritual tribute-shop tiles (screen-space)
                DrawMonolithPanel(rt, ctx);            // nearby-monolith reward list (screen-space)
                DrawSessionHud(rt, ctx);               // session pace/zone/death HUD (screen-space)
                DrawZoneSummary(rt, ctx);              // opt-in zone summary panel (rares/chests/exits)
                DrawPreloadPanel(rt, ctx);             // opt-in zone-entry preload alert (pinnacle/mechanic content)
                DrawCampaignGps(rt, ctx);              // compact campaign GPS instruction line (top strip)
            }

            // Patch-resilience banner: top strip drawn on top of everything whenever the overlay is active
            // and the health monitor has something to say (connecting / out-of-date / stale reads).
            if (ctx.Active && ctx.HealthMessage != null)
                DrawHealthBanner(rt, ctx);
        }
        finally { rt.EndDraw(); }
        _window.Present();
    }

    /// <summary>The F10 atlas route: the shortest path (through the node connection graph) from the player's
    /// current node to the picked destination. Each canvas-space (relPos) waypoint is projected with the same
    /// atlas homography as the rings, then drawn as a connected polyline (dark underlay + bright cyan line for
    /// contrast over the busy atlas), with a node dot at each hop, a green START disc and a gold GOAL ring.
    /// Off-screen segments are simply clipped by Direct2D, so the route still reads when the destination has
    /// been panned off-screen. Drawn UNDER the highlight rings.</summary>
    private void DrawAtlasRoute(ID2D1RenderTarget rt, RenderContext ctx)
    {
        var start = ctx.AtlasStart; var end = ctx.AtlasEnd; var route = ctx.AtlasRoute;
        var autos = ctx.AtlasAutoRoutes; var current = ctx.AtlasCurrent;
        bool hasAuto = autos is { Count: > 0 };
        if (start is null && end is null && (route is null || route.Count == 0) && !hasAuto && current is null) return;
        float h0 = ctx.AtlasScale, h1 = ctx.AtlasShearX, h2 = ctx.AtlasOffX,
              h3 = ctx.AtlasShearY, h4 = ctx.AtlasScaleY, h5 = ctx.AtlasOffY,
              h6 = ctx.AtlasPersX, h7 = ctx.AtlasPersY;
        NumVec2 Proj(NumVec2 p) { var w = h6 * p.X + h7 * p.Y + 1f; if (MathF.Abs(w) < 1e-6f) w = 1f; return new NumVec2((h0 * p.X + h1 * p.Y + h2) / w, (h3 * p.X + h4 * p.Y + h5) / w); }
        // Off-screen (culled) atlas nodes report NOISY relPos, so segments touching them wobble and their
        // chevrons spin in random directions. Gate on projected screen position: draw a line only when a
        // segment is at least partly on-screen, and chevrons only when BOTH endpoints are on-screen (so the
        // arrow direction is trustworthy). W/H from the context; margins keep edge-crossing routes visible.
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        bool On(NumVec2 p, float m) => p.X >= -m && p.X <= W + m && p.Y >= -m && p.Y <= H + m;

        var dark = new Color4(0f, 0f, 0f, 0.6f);
        var bright = new Color4(0.235f, 0.86f, 1f, 0.95f);   // cyan
        var green = new Color4(0.43f, 0.91f, 0.53f, 1f);
        var gold = new Color4(0.878f, 0.702f, 0.255f, 1f);

        // ── Auto-routes (improvement 1): one polyline per tracked tile, in its rule colour, with a hop chip
        // at the target + directional chevrons (#4). Drawn UNDER the manual F10 route + the marks. ──
        if (hasAuto)
        {
            // #4 per-edge interleaving: routes from the accessible frontier share their first segments
            // constantly, so a shared edge gets each route's chevrons at a distinct phase slot — overlapping
            // colours stay visible instead of the last-drawn one overpainting. Keyed by canvas-space coords.
            var edgeRoutes = new Dictionary<(long, long, long, long), List<int>>();
            for (var ri = 0; ri < autos!.Count; ri++)
            {
                if (autos[ri].Points is not { Count: >= 2 } rp) continue;
                for (var i = 1; i < rp.Count; i++)
                {
                    var k = AtlasEdgeKey(rp[i - 1], rp[i]);
                    if (!edgeRoutes.TryGetValue(k, out var l)) edgeRoutes[k] = l = new List<int>();
                    l.Add(ri);
                }
            }
            float spacingMul = MathF.Max(1.5f, ctx.AtlasRouteArrowSpacing);
            const float chevron = 7f;
            float spacing = chevron * spacingMul;
            for (var ri = 0; ri < autos!.Count; ri++)
            {
                var ar = autos[ri];
                if (ar.Points is not { Count: >= 2 } rp) continue;
                // Softer + thinner than the manual F10 route: auto-routes are ambient guides to every tracked
                // tile, so they shouldn't dominate the screen as a thick web. Lower alpha + lighter underlay.
                var col = string.IsNullOrEmpty(ar.Color) ? new Color4(0.235f, 0.86f, 1f, 0.65f) : ParseColor(ar.Color, 0.65f);
                var pts = new NumVec2[rp.Count];
                for (var i = 0; i < rp.Count; i++) pts[i] = Proj(rp[i]);
                for (var i = 1; i < pts.Length; i++)
                {
                    var a = pts[i - 1]; var b = pts[i];
                    if (!On(a, 64f) && !On(b, 64f)) continue;   // fully off-screen segment → skip (relPos is noise)
                    _bStyle!.Color = new Color4(0f, 0f, 0f, 0.4f); rt.DrawLine(a, b, _bStyle, 3f);
                    _bStyle.Color = col; rt.DrawLine(a, b, _bStyle, 1.75f);
                    if (!On(a, 0f) || !On(b, 0f)) continue;     // chevron direction only trustworthy fully on-screen
                    var key = AtlasEdgeKey(rp[i - 1], rp[i]);
                    float phase = 0.5f;
                    if (edgeRoutes.TryGetValue(key, out var sh) && sh.Count > 0)
                    {
                        var local = sh.IndexOf(ri); if (local < 0) local = 0;
                        phase = (local + 0.5f) / sh.Count;
                    }
                    var carry = spacing * phase;   // reset per segment so the phase is honoured on every edge
                    DrawAtlasChevrons(rt, a, b, col, chevron, spacing, ref carry);
                }
                // Hop-count chip at the target end (only when the target is on-screen).
                var tgt = pts[^1];
                if (On(tgt, 0f))
                {
                    string ht = ar.Hops.ToString();
                    rt.FillRectangle(new Vortice.RawRectF(tgt.X - 11f, tgt.Y - 26f, tgt.X + 11f, tgt.Y - 10f), _bPanel!);
                    rt.DrawText(ht, _tf!, new Rect(tgt.X - 9f, tgt.Y - 26f, tgt.X + 11f, tgt.Y - 10f), _bText!, DrawTextOptions.Clip);
                }
            }
        }

        if (route is { Count: >= 2 })
        {
            // Graph polyline: dark underlay then bright line (cheap outline for contrast over the atlas), hop dots.
            var pts = new NumVec2[route.Count];
            for (var i = 0; i < route.Count; i++) pts[i] = Proj(route[i]);
            float mspacing = 9f * MathF.Max(1.5f, ctx.AtlasRouteArrowSpacing);
            for (var i = 1; i < pts.Length; i++)
            {
                var a = pts[i - 1]; var b = pts[i];
                if (!On(a, 64f) && !On(b, 64f)) continue;   // fully off-screen segment → skip (relPos is noise)
                _bStyle!.Color = dark; rt.DrawLine(a, b, _bStyle, 7f);
                _bStyle.Color = bright; rt.DrawLine(a, b, _bStyle, 3.5f);
                if (!On(a, 0f) || !On(b, 0f)) continue;     // chevrons + hop dots only when fully on-screen
                if (i < pts.Length - 1) rt.DrawEllipse(new Ellipse(b, 4f, 4f), _bStyle, 2f);
                var carry = mspacing * 0.5f; DrawAtlasChevrons(rt, a, b, dark, 9f, mspacing, ref carry);
            }
        }
        else if (start is { } sa && end is { } eb)
        {
            // No graph path between the two — draw a direct dashed-ish straight line so the link is still shown.
            var a = Proj(sa); var b = Proj(eb);
            _bStyle!.Color = dark; rt.DrawLine(a, b, _bStyle, 6f);
            _bStyle.Color = gold; rt.DrawLine(a, b, _bStyle, 2.5f);
        }

        // START (green disc) + END (gold ring) markers — drawn whenever set, even before a path exists.
        if (start is { } s) { var p = Proj(s); _bStyle!.Color = green; rt.DrawEllipse(new Ellipse(p, 8f, 8f), _bStyle, 3f); rt.DrawEllipse(new Ellipse(p, 3f, 3f), _bStyle, 2f); }
        if (end is { } e) { var p = Proj(e); _bStyle!.Color = gold; rt.DrawEllipse(new Ellipse(p, 11f, 11f), _bStyle, 3f); rt.DrawEllipse(new Ellipse(p, 4f, 4f), _bStyle, 2f); }

        // "YOU ARE HERE" — the player's current atlas node (improvement 1): a cyan double-ring with a dark
        // outline + filled centre so it reads as the route origin regardless of the tile underneath.
        if (current is { } cur)
        {
            var p = Proj(cur);
            _bStyle!.Color = new Color4(0f, 0f, 0f, 0.7f); rt.DrawEllipse(new Ellipse(p, 11f, 11f), _bStyle, 4.5f);
            _bStyle.Color = new Color4(0.3f, 0.95f, 1f, 1f);
            rt.DrawEllipse(new Ellipse(p, 11f, 11f), _bStyle, 2.5f);
            rt.FillEllipse(new Ellipse(p, 3f, 3f), _bStyle);
        }
    }

    /// <summary>
    /// Atlas overlay: highlight atlas map nodes on the open Atlas screen. Each node's canvas-space
    /// position (RelativePos) is projected to screen via the atlas transform. Tracked/arrowed maps draw a
    /// ring in their rule colour; off-screen arrowed maps get an edge arrow pointing toward them.
    /// </summary>
    private void DrawAtlas(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.AtlasNodes is not { Count: > 0 } marks) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        // Homography: w = h6·x + h7·y + 1; screen = (h0·x+h1·y+h2, h3·x+h4·y+h5) / w. (shear/persp 0 ⇒ affine)
        float h0 = ctx.AtlasScale, h1 = ctx.AtlasShearX, h2 = ctx.AtlasOffX,
              h3 = ctx.AtlasShearY, h4 = ctx.AtlasScaleY, h5 = ctx.AtlasOffY,
              h6 = ctx.AtlasPersX, h7 = ctx.AtlasPersY;
        float ccx = W * 0.5f, ccy = H * 0.5f;
        foreach (var n in marks)
        {
            var w = h6 * n.X + h7 * n.Y + 1f;
            if (MathF.Abs(w) < 1e-6f) continue;
            var sx = (h0 * n.X + h1 * n.Y + h2) / w;
            var sy = (h3 * n.X + h4 * n.Y + h5) / w;
            var onScreen = sx >= 0 && sx <= W && sy >= 0 && sy <= H;
            var col = string.IsNullOrEmpty(n.Color) ? new Color4(0.235f, 0.86f, 1f, 1f) : ParseColor(n.Color, 1f);

            // OFF-SCREEN: if this map has the arrow rule, draw an edge arrow pointing toward it; else skip.
            if (!onScreen)
            {
                if (n.Arrow) DrawEdgeArrow(rt, sx, sy, ccx, ccy, W, H, col, n.Label);
                continue;
            }

            var c = new NumVec2(sx, sy);
            if (n.Selected || n.Arrow || n.Nav)
            {
                // ONE clean ring in the rule colour (Citadel gold / Boss red / …) + a small filled centre.
                // Biome (when enabled) is conveyed by the CENTRE-DOT colour rather than a second full ring,
                // so a tracked node reads as a single target instead of a stack of concentric rings. Primary
                // targets (ring/arrow rules) are drawn a touch larger than nav-only route endpoints.
                var r = (n.Selected || n.Arrow) ? 12f : 9f;
                _bStyle!.Color = col;
                rt.DrawEllipse(new Ellipse(c, r, r), _bStyle, 2.5f);
                _bStyle.Color = ctx.AtlasBiomeBorder ? BiomeColor(n.Biome) : col;
                rt.FillEllipse(new Ellipse(c, 3f, 3f), _bStyle);
            }
            else if (n.IconType > 0) // DEBUG (AtlasDrawAll): content-tag element
            {
                _bStyle!.Color = new Color4(1f, 0.9f, 0.2f, 0.9f);
                rt.DrawEllipse(new Ellipse(c, 7f, 7f), _bStyle, 2f);
            }
            else if (n.Visited)
            {
                _bStyle!.Color = new Color4(1f, 0.2f, 1f, 1f);
                rt.DrawEllipse(new Ellipse(c, 16f, 16f), _bStyle, 3f);
                rt.DrawEllipse(new Ellipse(c, 8f, 8f), _bStyle, 2f);
            }
            else
            {
                _bStyle!.Color = n.HasContent ? new Color4(1f, 0.62f, 0.26f, 0.95f)
                               : new Color4(0.43f, 0.91f, 0.53f, 0.85f);
                rt.DrawEllipse(new Ellipse(c, 11f, 11f), _bStyle, 2f);
            }
            // #5 content icons: drawn in a row above the node, but ONLY on FOGGED nodes (the game already
            // renders its own content icons on revealed ones, so we'd double up). Tracked rings + icons can
            // co-exist on a fogged tracked node. Ring radius below mirrors the ring drawn above.
            if (ctx.AtlasContentIcons && !n.Visible && n.ContentIcons is { Count: > 0 } && _atlasIcons != null)
            {
                float ringR = (n.Selected || n.Arrow) ? 12f : (n.Nav ? 9f : 0f);
                DrawAtlasContentIcons(rt, n.ContentIcons, sx, sy - ringR, ctx.AtlasContentIconSize);
            }

            var label = n.Label ?? (n.IconType > 0 ? n.IconType.ToString() : null);
            if (label != null)
            {
                // Backing chip behind the label (improvement 2) so map names stay readable over the busy
                // atlas art. Width is estimated from the label length (Direct2D layout measuring is heavier
                // than it's worth here); the chip sits just right of the ring.
                float lx = sx + 24f, ly = sy - 9f;
                float lw = label.Length * 7.0f + 8f;
                rt.FillRectangle(new Vortice.RawRectF(lx - 4f, ly - 1f, lx + lw, ly + 17f), _bPanel!);
                rt.DrawText(label, _tf!, new Rect(lx, ly, lx + lw + 40f, ly + 18f), _bText!, DrawTextOptions.Clip);
            }
        }
    }

    /// <summary>Draw a centered row of content icons (#5) above a node. <paramref name="cx"/> is the node's
    /// screen X; <paramref name="topY"/> is the node ring's top — icons sit just above it. Square cells of
    /// <paramref name="iconH"/> px; only basenames present in the cache draw. A dark backing keeps the row
    /// readable over the busy atlas art. Called only for FOGGED nodes (the game draws its own on revealed ones).</summary>
    private void DrawAtlasContentIcons(ID2D1RenderTarget rt, IReadOnlyList<string> basenames, float cx, float topY, float iconH)
    {
        if (iconH < 6f) iconH = 6f;
        var cnt = 0;
        foreach (var bn in basenames) if (_atlasIcons!.Get(rt, bn) != null) cnt++;
        if (cnt == 0) return;
        const float gap = 3f;
        float totalW = cnt * iconH + (cnt - 1) * gap;
        float ix = cx - totalW * 0.5f;
        float iy = topY - iconH - 5f;
        rt.FillRectangle(new Vortice.RawRectF(ix - 3f, iy - 2f, ix + totalW + 3f, iy + iconH + 2f), _bPanel!);
        foreach (var bn in basenames)
        {
            var bmp = _atlasIcons!.Get(rt, bn);
            if (bmp == null) continue;
            rt.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, new Rect(ix, iy, ix + iconH, iy + iconH));
            ix += iconH + gap;
        }
    }

    /// <summary>Lay stroked arrowhead chevrons (a row of "&gt;" pointing a→b) at <paramref name="spacing"/>
    /// intervals along the segment (#4). <paramref name="carry"/> holds the leftover distance into the next
    /// segment so spacing stays even across a multi-segment route. Ported from the GameHelper2 Atlas plugin's
    /// DrawChevrons (filled triangles → stroked chevrons here, cheaper in Direct2D and reads the same).</summary>
    private void DrawAtlasChevrons(ID2D1RenderTarget rt, NumVec2 a, NumVec2 b, Color4 color, float size, float spacing, ref float carry)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-3f) { return; }
        var dir = d / len;
        var perp = new NumVec2(-dir.Y, dir.X);
        float half = size * 0.5f;
        _bStyle!.Color = color;
        float t = carry;
        while (t < len)
        {
            var p = a + dir * t;
            var tip = p + dir * half;
            var baseMid = p - dir * half;
            rt.DrawLine(tip, baseMid + perp * half, _bStyle, 1.5f);
            rt.DrawLine(tip, baseMid - perp * half, _bStyle, 1.5f);
            t += spacing;
        }
        carry = t - len;
    }

    /// <summary>Direction-independent key for a route edge (canvas-space endpoints rounded to int), so a
    /// segment shared by two routes hashes the same regardless of which way each route walks it (#4).</summary>
    private static (long, long, long, long) AtlasEdgeKey(NumVec2 a, NumVec2 b)
    {
        long ax = (long)MathF.Round(a.X), ay = (long)MathF.Round(a.Y);
        long bx = (long)MathF.Round(b.X), by = (long)MathF.Round(b.Y);
        bool aFirst = ax < bx || (ax == bx && ay <= by);
        return aFirst ? (ax, ay, bx, by) : (bx, by, ax, ay);
    }

    // Atlas biome index (0..12) → border colour (improvement 2). Order matches the dashboard BIOMES list:
    // Grass, Sand, Swamp, Forest, Snow, Stone, Volcanic, Coast, Cave, Vaal, Water, Desert, Special.
    private static readonly Color4[] BiomeColors =
    {
        new(0.45f, 0.78f, 0.36f, 1f), // 0 Grass
        new(0.85f, 0.74f, 0.36f, 1f), // 1 Sand
        new(0.40f, 0.62f, 0.35f, 1f), // 2 Swamp
        new(0.22f, 0.55f, 0.30f, 1f), // 3 Forest
        new(0.80f, 0.86f, 0.92f, 1f), // 4 Snow
        new(0.62f, 0.60f, 0.58f, 1f), // 5 Stone
        new(0.86f, 0.40f, 0.25f, 1f), // 6 Volcanic
        new(0.38f, 0.70f, 0.82f, 1f), // 7 Coast
        new(0.55f, 0.45f, 0.65f, 1f), // 8 Cave
        new(0.80f, 0.30f, 0.40f, 1f), // 9 Vaal
        new(0.30f, 0.55f, 0.85f, 1f), // 10 Water
        new(0.88f, 0.78f, 0.45f, 1f), // 11 Desert
        new(0.75f, 0.55f, 0.85f, 1f), // 12 Special
    };
    private static Color4 BiomeColor(int b) => (b >= 0 && b < BiomeColors.Length) ? BiomeColors[b] : new Color4(0.6f, 0.6f, 0.6f, 1f);

    /// <summary>Draw an edge arrow pointing from screen-centre toward an OFF-SCREEN target at (sx,sy),
    /// clamped to the inset screen edge, coloured by <paramref name="col"/> and optionally labelled.
    /// Used for both atlas off-screen maps and off-screen entity arrows.</summary>
    private void DrawEdgeArrow(ID2D1RenderTarget rt, float sx, float sy, float cx, float cy, float W, float H,
        Color4 col, string? label, float head = 11f, float margin = 46f)
    {
        var (ex, ey, ux, uy) = EdgeArrow.BorderPoint(sx, sy, cx, cy, W, H, margin);
        if (ux == 0f && uy == 0f) return;
        float px = -uy, py = ux;                       // perpendicular
        var tip = new NumVec2(ex + ux * head, ey + uy * head);
        var bl = new NumVec2(ex - ux * 9f + px * 10f, ey - uy * 9f + py * 10f);
        var br = new NumVec2(ex - ux * 9f - px * 10f, ey - uy * 9f - py * 10f);
        _bStyle!.Color = col;
        rt.DrawLine(tip, bl, _bStyle, 4f);
        rt.DrawLine(tip, br, _bStyle, 4f);
        rt.DrawLine(bl, br, _bStyle, 4f);              // close the arrowhead triangle
        if (label != null)
        {
            // Pull the label fully on-screen (inset from the arrow toward centre) and back it with a chip so
            // it stays readable over the map art and doesn't clip off the edge like the bare text did.
            float lw = label.Length * 7.0f + 8f;
            float lx = ex - ux * 30f - lw * 0.5f, ly = ey - uy * 22f - 9f;
            lx = Math.Clamp(lx, 2f, W - lw - 2f);
            ly = Math.Clamp(ly, 2f, H - 20f);
            rt.FillRectangle(new Vortice.RawRectF(lx - 3f, ly - 1f, lx + lw, ly + 17f), _bPanel!);
            rt.DrawText(label, _tf!, new Rect(lx, ly, lx + lw + 40f, ly + 18f), _bText!, DrawTextOptions.Clip);
        }
    }

    /// <summary>Off-screen entity arrows: projects each <see cref="EntityArrowTarget"/> via the camera
    /// matrix; draws an edge arrow for any that land outside the screen rect (skipping targets that are
    /// only just off-screen by less than <see cref="RenderContext.EntityArrowMinEdgePx"/> px). Drawn
    /// nearest-first, capped at <see cref="RenderContext.EntityArrowMax"/>.</summary>
    private void DrawEntityArrows(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (!ctx.EntityArrowsEnabled || ctx.CameraMatrix is not { } m || ctx.EntityArrows is not { Count: > 0 } targets) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight, cx = W * 0.5f, cy = H * 0.5f;
        float minEdge = ctx.EntityArrowMinEdgePx;

        var offs = new List<(float sx, float sy, float d, uint col, string? label)>();
        foreach (var t in targets)
        {
            var w = t.World;
            var cw = w.X * m[3] + w.Y * m[7] + w.Z * m[11] + m[15];
            if (cw <= 0.0001f) continue;   // behind camera
            var px2 = w.X * m[0] + w.Y * m[4] + w.Z * m[8] + m[12];
            var py2 = w.X * m[1] + w.Y * m[5] + w.Z * m[9] + m[13];
            float sx = (px2 / cw / 2f + 0.5f) * W, sy = (0.5f - py2 / cw / 2f) * H;
            bool onScreen = sx >= 0 && sx <= W && sy >= 0 && sy <= H;
            if (onScreen) continue;        // already visible as a dot

            // MinEdgePx: skip targets whose projected point is within minEdge px of the screen rect
            // (i.e., only marginally off-screen — avoids cluttering the edge for barely-missed targets).
            float distFromEdge = MathF.Max(0f, MathF.Max(
                MathF.Max(-sx, sx - W),
                MathF.Max(-sy, sy - H)));
            if (distFromEdge < minEdge) continue;

            float d = MathF.Sqrt((sx - cx) * (sx - cx) + (sy - cy) * (sy - cy));
            offs.Add((sx, sy, d, t.Color, ctx.EntityArrowShowLabel ? t.Label : null));
        }
        offs.Sort((a, b) => a.d.CompareTo(b.d));
        int drawn = 0;
        foreach (var o in offs)
        {
            if (drawn >= ctx.EntityArrowMax) break;
            DrawEdgeArrow(rt, o.sx, o.sy, cx, cy, W, H, ColorFromU(o.col), o.label, ctx.EntityArrowSize);
            drawn++;
        }
    }

    /// <summary>The transient "active target" indicator drawn briefly after a Quick-Target cycle.</summary>
    private void DrawCycleIndicator(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.CycleIndicator is not { } ci) return;
        var text = ci.Category.Length > 0
            ? $"▸ {ci.Pos}/{ci.Total}  {ci.Name}  ({ci.Category})"
            : $"▸ {ci.Pos}/{ci.Total}  {ci.Name}";
        rt.DrawText(text, _tf!, new Rect(12f, 12f, ctx.WindowWidth - 12f, 34f), _bText!, DrawTextOptions.Clip);
    }

    /// <summary>Top-strip status/health banner — drawn whenever the overlay is active and the health monitor
    /// has a message. Red for a confirmed can't-read-the-game; amber for connecting / soft warnings.</summary>
    private void DrawHealthBanner(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.HealthMessage is not { Length: > 0 } msg) return;
        rt.FillRectangle(new Vortice.RawRectF(0f, 0f, ctx.WindowWidth, 30f), _bPanel!);
        _bStyle!.Color = ctx.Health == HealthState.Broken
            ? new Color4(1f, 0.20f, 0.20f, 0.95f)   // red — confirmed break
            : new Color4(1f, 0.85f, 0.20f, 1f);     // amber — connecting / soft warning
        rt.DrawText("⚠ " + msg, _tf!, new Rect(12f, 7f, ctx.WindowWidth - 12f, 30f), _bStyle!, DrawTextOptions.Clip);
    }

    /// <summary>Compact Campaign GPS instruction line — a second top strip (below the health banner when
    /// both are visible) drawn whenever a non-empty instruction is published by the GPS engine.</summary>
    private void DrawCampaignGps(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.CampaignGps is not { Length: > 0 } msg) return;
        rt.FillRectangle(new Vortice.RawRectF(0f, 34f, ctx.WindowWidth, 58f), _bPanel!);
        _bStyle!.Color = new Color4(0.85f, 0.72f, 0.30f, 1f);   // campaign gold
        rt.DrawText(msg, _tf!, new Rect(12f, 38f, ctx.WindowWidth - 12f, 58f), _bStyle!, DrawTextOptions.Clip);
    }

    /// <summary>
    /// World-space HP bars over monsters, projected via the camera WorldToScreen matrix. Drawn whether
    /// or not the big map is open (it's a heads-up combat overlay). HP bars are a MONSTER-ONLY concept,
    /// gated entirely by the per-rarity on/off toggles in Settings (HpBarNormal/Magic/Rare/Unique) — they
    /// are NOT a display-rule concern. The resolved rule is still consulted for two things: it must not be
    /// a Hide rule (no bars over hidden mobs), and the bar FILL follows the mob's dot color. Bar GEOMETRY
    /// (width/border/offset) is per-rarity from HpBars.
    /// </summary>
    private void DrawNameplates(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { } m || ctx.HpBarTargets is not { Count: > 0 } bars) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        var hb = ctx.HpBars;
        var bh = hb.Height;
        // All the expensive per-entity decisions (rarity gate, rule resolve, colour parse) were done at
        // world rate in RadarApp.BuildHpSpecs; here we only project the LIVE position (refreshed this frame
        // so the bar tracks the moving mob) and fill. fill/border are pre-packed 0xAARRGGBB.
        foreach (var t in bars)
        {
            var w = t.World;
            var cw = w.X*m[3] + w.Y*m[7] + w.Z*m[11] + m[15];
            if (cw <= 0.0001f) continue;
            var cx = w.X*m[0] + w.Y*m[4] + w.Z*m[8] + m[12];
            var cy = w.X*m[1] + w.Y*m[5] + w.Z*m[9] + m[13];
            var sx = (cx/cw/2f + 0.5f) * W;
            var sy = (0.5f - cy/cw/2f) * H;
            if (sx < 0 || sx > W || sy < 0 || sy > H) continue;

            var bw = t.Width;
            var bx = sx - bw / 2f + hb.OffsetX;
            var by = sy + hb.OffsetY; // OffsetY is relative to the mob (negative = above)
            var barRect = new Vortice.RawRectF(bx, by, bx + bw, by + bh);
            rt.FillRectangle(barRect, _bPanel!);
            _bStyle!.Color = t.Frac < 0.3f ? ColLowHp : ColorFromU(t.Fill);
            rt.FillRectangle(new Vortice.RawRectF(bx, by, bx + bw * t.Frac, by + bh), _bStyle);
            if (t.BorderWidth > 0f)
            {
                _bStyle.Color = ColorFromU(t.Border);
                rt.DrawRectangle(barRect, _bStyle, t.BorderWidth);
            }
        }
    }

    /// <summary>
    /// Priced unique ground-item labels drawn over their in-world loot icons (projected via the camera
    /// WorldToScreen matrix, same as HP bars). Each label shows the resolved unique NAME (revealing
    /// unidentified uniques) + value on a backing panel; items above the value threshold get a gold
    /// border. Drawn whether the big map is open or not — it's a heads-up loot overlay.
    /// </summary>
    private void DrawItemLabels(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { } m || ctx.ItemLabels is not { Count: > 0 } labels) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        foreach (var it in labels)
        {
            var w = it.World;
            var cw = w.X*m[3] + w.Y*m[7] + w.Z*m[11] + m[15];
            if (cw <= 0.0001f) continue;                       // behind the camera
            var cx = w.X*m[0] + w.Y*m[4] + w.Z*m[8] + m[12];
            var cy = w.X*m[1] + w.Y*m[5] + w.Z*m[9] + m[13];
            var sx = (cx/cw/2f + 0.5f) * W;
            var sy = (0.5f - cy/cw/2f) * H;
            if (sx < 0 || sx > W || sy < 0 || sy > H) continue;

            if (it.ShowName)
            {
                // UNIDENTIFIED unique: two stacked lines — resolved NAME over VALUE — on a backing panel
                // (the game hides the unID name, so we reveal it). Border when high-value.
                var text = $"{it.Name}\n{it.Value}";
                var halfW = MathF.Max(48f, 4.5f * MathF.Max(it.Name.Length, it.Value.Length + 3));
                const float halfH = 19f;
                var panel = new Vortice.RawRectF(sx - halfW, sy - halfH, sx + halfW, sy + halfH);
                rt.FillRectangle(panel, _bPanel!);
                if (it.Highlight) { _bStyle!.Color = ColItemHi; rt.DrawRectangle(panel, _bStyle, 2.5f); }
                _bStyle!.Color = it.Highlight ? ColItemHi : ColItemText;
                rt.DrawText(text, _tf!, new Rect(sx - halfW + 4f, sy - halfH + 2f, sx + halfW - 2f, sy + halfH - 1f),
                    _bStyle, DrawTextOptions.Clip);
            }
            else
            {
                // Identified uniques + runes/essences/currency/…: VALUE-only compact chip (the game already
                // shows the item's name on its loot tag). Border when high-value.
                var halfW = MathF.Max(26f, 4.5f * (it.Value.Length + 1));
                const float halfH = 11f;
                var panel = new Vortice.RawRectF(sx - halfW, sy - halfH, sx + halfW, sy + halfH);
                rt.FillRectangle(panel, _bPanel!);
                if (it.Highlight) { _bStyle!.Color = ColItemHi; rt.DrawRectangle(panel, _bStyle, 2f); }
                _bStyle!.Color = it.Highlight ? ColItemHi : ColItemText;
                rt.DrawText(it.Value, _tf!, new Rect(sx - halfW + 3f, sy - halfH + 1f, sx + halfW - 2f, sy + halfH),
                    _bStyle, DrawTextOptions.Clip);
            }
        }
    }

    private static readonly Color4 ColItemHi   = new(1.00f, 0.80f, 0.20f, 1.0f);  // gold — above-threshold name + border
    private static readonly Color4 ColItemText = new(0.92f, 0.92f, 0.92f, 1.0f);  // off-white — below-threshold label

    /// <summary>
    /// Tiered affix labels drawn above each elite mob's head. World-projected via the same camera
    /// matrix as HP bars. Lines are pre-filtered and pre-ordered by BuildAffixSpecs (world rate);
    /// here we just project the pre-read world position and stack the text upward from OffsetY.
    /// </summary>
    private void DrawAffixNameplates(ID2D1RenderTarget rt, RenderContext ctx)
    {
        var cfg = ctx.AffixNameplates;
        if (cfg is null || !cfg.Enabled) return;
        if (ctx.CameraMatrix is not { } m || ctx.AffixTargets is not { Count: > 0 } targets) return;
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

    /// <summary>Rune-crafting reward prices: a small value box just outside the right edge of each visible
    /// reward row in the "Runeshape Combinations" panel. Rects are screen-space (already scaled in
    /// Poe2Runeforge); text + tier color are precomputed in RadarApp. Screen-space, so no world projection.</summary>
    private void DrawRuneforge(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.RuneLabels is not { Count: > 0 } labels) return;
        const float gap = 8f, boxW = 96f, boxH = 22f;
        foreach (var r in labels)
        {
            var lx = r.X + r.W + gap;             // just past the row's right edge
            var cy = r.Y + r.H * 0.5f;            // vertically centered on the row
            var box = new Vortice.RawRectF(lx, cy - boxH * 0.5f, lx + boxW, cy + boxH * 0.5f);
            rt.FillRectangle(box, _bPanel!);
            _bStyle!.Color = ColorFromU(r.Color);
            rt.DrawText(r.Text, _tf!, new Rect(lx + 5f, cy - boxH * 0.5f + 2f, lx + boxW - 2f, cy + boxH * 0.5f - 1f),
                _bStyle, DrawTextOptions.Clip);
        }
    }

    /// <summary>Ritual tribute-shop reward values: a value chip centered on the bottom edge of each reward
    /// tile in the open shop. Rects are screen-space (already scaled in Poe2Live.ReadRitualRewards); text +
    /// tier color are precomputed in RadarApp. High-value rewards get a gold border. No world projection.</summary>
    private void DrawRitualRewards(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.RitualRewards is not { Count: > 0 } labels) return;
        const float boxH = 20f;
        foreach (var r in labels)
        {
            var boxW = MathF.Max(44f, 7.5f * (r.Text.Length + 1));
            var cx = r.X + r.W * 0.5f;
            var top = r.Y + r.H - boxH;            // sit on the tile's bottom edge
            var box = new Vortice.RawRectF(cx - boxW * 0.5f, top, cx + boxW * 0.5f, top + boxH);
            rt.FillRectangle(box, _bPanel!);
            if (r.Highlight) { _bStyle!.Color = ColItemHi; rt.DrawRectangle(box, _bStyle, 2f); }
            _bStyle!.Color = ColorFromU(r.Color);
            rt.DrawText(r.Text, _tf!, new Rect(box.Left + 3f, top + 1f, box.Right - 2f, top + boxH - 1f),
                _bStyle, DrawTextOptions.Clip);
        }
    }

    /// <summary>Unpack a 0xAARRGGBB color (precomputed in RadarApp.BuildHpSpecs) to a Color4 — no string
    /// parse or allocation, runs per bar per frame.</summary>
    private static Color4 ColorFromU(uint u)
        => new(((u >> 16) & 0xFF) / 255f, ((u >> 8) & 0xFF) / 255f, (u & 0xFF) / 255f, ((u >> 24) & 0xFF) / 255f);

    /// <summary>Runeshape-monolith map markers: a value-coloured ring with the hole count N inside, and a
    /// "{best} ex · {reward}" label to the right. Drawn on the big map (grid → screen via the same
    /// projection as entity dots / landmarks). Augments the generic POI dot with the monolith's value.</summary>
    private void DrawMonoliths(ID2D1RenderTarget rt, RenderContext ctx, NumVec2 player, NumVec2 center, float scale)
    {
        if (ctx.Monoliths is not { Count: > 0 } monos) return;
        foreach (var m in monos)
        {
            var p = Project(new NumVec2(m.Grid.X, m.Grid.Y), player, center, scale);
            _bStyle!.Color = ColorFromU(m.Color);
            rt.DrawEllipse(new Ellipse(p, 9f, 9f), _bStyle, 2.4f);          // value-coloured ring
            rt.DrawText(m.Holes.ToString(), _tf!,                           // N badge (white) inside the ring
                new Rect(p.X - 4f, p.Y - 8f, p.X + 10f, p.Y + 8f), _bText!, DrawTextOptions.Clip);
            var label = m.BestEx > 0 ? $"{m.BestEx:F0}ex · {m.BestName}" : $"{m.AnchorName} {m.Holes}h";
            rt.DrawText(label, _tf!, new Rect(p.X + 13f, p.Y - 8f, p.X + 340f, p.Y + 9f), _bStyle, DrawTextOptions.Clip);
        }
    }

    /// <summary>The nearby-monolith reward panel: a screen-space list (top-right) of the area's monoliths
    /// sorted by best value, each with its anchor + N + top priced rewards. Draws even with the big map
    /// closed (the values are read area-wide off the persistent devices).</summary>
    private void DrawMonolithPanel(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (!ctx.ShowMonolithPanel || ctx.Monoliths is not { Count: > 0 } monos) return;
        // D3: use the pre-sorted, capped-to-6 Top list instead of per-frame OrderByDescending+Take+ToList.
        var list = ctx.MonolithsTop ?? (IReadOnlyList<MonolithMarker>)Array.Empty<MonolithMarker>();
        const float w = 248f, pad = 6f, lineH = 15f, headH = 17f, titleH = 18f;

        float h = pad * 2f + titleH;
        foreach (var m in list)
        {
            var rows = 0; foreach (var r in m.Rewards) if (r.Ex > 0 && rows < 3) rows++;
            h += headH + lineH * rows;
        }
        float x = ctx.WindowWidth - w - 10f, y = 90f;
        rt.FillRectangle(new Vortice.RawRectF(x, y, x + w, y + h), _bPanel!);

        float cy = y + pad;
        rt.DrawText($"Monoliths ({monos.Count})", _tf!, new Rect(x + pad, cy, x + w - pad, cy + titleH), _bText!, DrawTextOptions.Clip);
        cy += titleH;
        foreach (var m in list)
        {
            _bStyle!.Color = ColorFromU(m.Color);
            var hdr = m.BestEx > 0 ? $"{m.BestEx:F0}ex · {m.AnchorName} {m.Holes}h" : $"{m.AnchorName} {m.Holes}h";
            rt.DrawText(hdr, _tf!, new Rect(x + pad, cy, x + w - pad, cy + headH), _bStyle, DrawTextOptions.Clip);
            cy += headH;
            var shown = 0;
            foreach (var r in m.Rewards)
            {
                if (r.Ex <= 0 || shown >= 3) continue;
                rt.DrawText($"  {r.Ex,4:F0}  {r.Name}", _tf!, new Rect(x + pad, cy, x + w - pad, cy + lineH), _bText!, DrawTextOptions.Clip);
                cy += lineH; shown++;
            }
        }
    }

    private void DrawSessionHud(ID2D1RenderTarget rt, RenderContext ctx)
    {
        var hud = ctx.SessionHudSettings;
        if (hud == null || !hud.Enabled) return;
        var sess = ctx.Session;
        if (sess == null) return;

        // D1: cache-key comparands — rebuild the line array only when something actually changed.
        var sessionSec  = (int)sess.SessionElapsed.TotalSeconds;
        var zoneSec     = (int)sess.ZoneElapsed.TotalSeconds;
        if (_hudLines == null
            || sessionSec          != _hudCacheSessionSec
            || zoneSec             != _hudCacheZoneSec
            || sess.ZonesEntered   != _hudCacheZones
            || sess.ZonesPerHour   != _hudCacheZonesPerHr
            || sess.CurrentZoneName != _hudCacheZoneName
            || sess.CurrentAreaLevel != _hudCacheAreaLevel
            || sess.Deaths         != _hudCacheDeaths
            || sess.DeathsThisZone != _hudCacheDeathsHere
            || hud.ShowPace        != _hudCacheShowPace
            || hud.ShowZoneContext != _hudCacheShowZone
            || hud.ShowDeaths      != _hudCacheShowDeaths
            || sess.KillsNormal    != _hudCacheKillsN
            || sess.KillsMagic     != _hudCacheKillsM
            || sess.KillsRare      != _hudCacheKillsR
            || sess.KillsUnique    != _hudCacheKillsU
            || sess.MapsPerHour    != _hudCacheMapsHr
            || sess.XpEfficiency   != _hudCacheXpEff
            || hud.ShowKills       != _hudCacheShowKills)
        {
            _hudCacheSessionSec   = sessionSec;
            _hudCacheZoneSec      = zoneSec;
            _hudCacheZones        = sess.ZonesEntered;
            _hudCacheZonesPerHr   = sess.ZonesPerHour;
            _hudCacheZoneName     = sess.CurrentZoneName;
            _hudCacheAreaLevel    = sess.CurrentAreaLevel;
            _hudCacheDeaths       = sess.Deaths;
            _hudCacheDeathsHere   = sess.DeathsThisZone;
            _hudCacheShowPace     = hud.ShowPace;
            _hudCacheShowZone     = hud.ShowZoneContext;
            _hudCacheShowDeaths   = hud.ShowDeaths;
            _hudCacheKillsN       = sess.KillsNormal;
            _hudCacheKillsM       = sess.KillsMagic;
            _hudCacheKillsR       = sess.KillsRare;
            _hudCacheKillsU       = sess.KillsUnique;
            _hudCacheMapsHr       = sess.MapsPerHour;
            _hudCacheXpEff        = sess.XpEfficiency;
            _hudCacheShowKills    = hud.ShowKills;

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
            if (hud.ShowKills)
            {
                lines.Add(($"Kills    N{sess.KillsNormal} M{sess.KillsMagic} R{sess.KillsRare} U{sess.KillsUnique}", false));
                lines.Add(($"Maps/hr  {sess.MapsPerHour:F1}", false));
                lines.Add(($"XP eff   {sess.XpEfficiency:+#;-#;0}", false));
            }
            _hudLines = lines.ToArray();
        }

        var cachedLines = _hudLines;
        int enabledRowCount = cachedLines.Length;
        if (enabledRowCount == 0) return;   // nothing enabled — touch nothing

        const float panelW = 240f;          // narrower than the 248f monolith panel
        const float pad = 6f, lineH = 15f;  // all rows are data rows — no title row
        float panelH = enabledRowCount * lineH + pad * 2;

        // Corner anchoring — inline arithmetic with signed offsets + clamp.
        var corner = hud.Anchor;
        bool isRight  = corner is "TopRight"   or "BottomRight";
        bool isBottom = corner is "BottomLeft" or "BottomRight";
        const float margin = 10f;

        float left = isRight
            ? ctx.WindowWidth  - margin - panelW - hud.OffsetX
            : margin + hud.OffsetX;
        float top  = isBottom
            ? ctx.WindowHeight - margin - panelH - hud.OffsetY
            : margin + hud.OffsetY;
        left = Math.Clamp(left, margin, ctx.WindowWidth  - margin - panelW);
        top  = Math.Clamp(top,  margin, ctx.WindowHeight - margin - panelH);

        // Panel fill uses RawRectF (every FillRectangle in this file takes RawRectF; Rect is DrawText-only).
        rt.FillRectangle(new Vortice.RawRectF(left, top, left + panelW, top + panelH), _bPanel!);

        float cy = top + pad;
        foreach (var (text, isDeath) in cachedLines)
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

    /// <summary>Opt-in zone summary panel: live counts of rares/elites, chests, exits, and landmarks.
    /// Mirrored corner anchoring from <see cref="DrawSessionHud"/>.</summary>
    private void DrawZoneSummary(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.ZoneSummaryHud is not { Enabled: true } hud || ctx.ZoneSummary is not { } z) return;

        // Build row strings: fixed rows first, then conditional mechanic rows (only when count > 0).
        var chestTotal = z.ChestsOpen + z.ChestsClosed;
        var rows = new System.Collections.Generic.List<string>
        {
            $"Rares/Elites  {z.RareEliteAlive}",
            $"Monsters      {z.MonstersAlive}",
            $"Chests        {z.ChestsOpen}/{chestTotal}",
            $"Exits         {z.Transitions}",
        };
        if (z.ExpeditionCount > 0) rows.Add($"Expedition    {z.ExpeditionCount}");
        if (z.RitualCount     > 0) rows.Add($"Ritual        {z.RitualCount}");
        if (z.BreachCount     > 0) rows.Add($"Breach        {z.BreachCount}");
        if (z.StrongboxCount  > 0) rows.Add($"Strongbox     {z.StrongboxCount}");
        if (z.EssenceCount    > 0) rows.Add($"Essence       {z.EssenceCount}");
        if (z.ShrineCount     > 0) rows.Add($"Shrine        {z.ShrineCount}");

        const float panelW = 200f;
        const float pad = 6f, titleH = 16f, lineH = 15f;
        int rowCount = rows.Count;
        float panelH = titleH + rowCount * lineH + pad * 2;

        // Corner anchoring — same math as DrawSessionHud.
        var corner = hud.Anchor;
        bool isRight  = corner is "TopRight"   or "BottomRight";
        bool isBottom = corner is "BottomLeft" or "BottomRight";
        const float margin = 10f;

        float left = isRight
            ? ctx.WindowWidth  - margin - panelW - hud.OffsetX
            : margin + hud.OffsetX;
        float top  = isBottom
            ? ctx.WindowHeight - margin - panelH - hud.OffsetY
            : margin + hud.OffsetY;
        left = Math.Clamp(left, margin, ctx.WindowWidth  - margin - panelW);
        top  = Math.Clamp(top,  margin, ctx.WindowHeight - margin - panelH);

        rt.FillRectangle(new Vortice.RawRectF(left, top, left + panelW, top + panelH), _bPanel!);

        float cy = top + pad;
        // Title row.
        rt.DrawText("Zone", _tf!, new Rect(left + pad, cy, left + panelW - pad, cy + titleH), _bText!, DrawTextOptions.Clip);
        cy += titleH;
        // Data rows.
        foreach (var row in rows)
        {
            rt.DrawText(row, _tf!, new Rect(left + pad, cy, left + panelW - pad, cy + lineH), _bText!, DrawTextOptions.Clip);
            cy += lineH;
        }
    }

    /// <summary>Opt-in zone-entry preload panel: lists the preloaded content hits (pinnacle bosses,
    /// mechanics, etc.) grouped by tier (pinnacle → high → mechanic → interactable), each line
    /// coloured by the hit's configured color. Mirrors the corner-anchoring idiom of
    /// <see cref="DrawZoneSummary"/> and <see cref="DrawSessionHud"/>.</summary>
    private void DrawPreloadPanel(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (!ctx.PreloadEnabled || ctx.PreloadHits is not { Count: > 0 } preloadHits) return;

        // Sort hits by tier rank descending: pinnacle(3) → high(2) → mechanic(1) → interactable(0).
        static int TierRank(string? tier) => tier switch
        {
            "pinnacle"     => 3,
            "high"         => 2,
            "mechanic"     => 1,
            "interactable" => 0,
            _              => 0,
        };

        var sorted = new System.Collections.Generic.List<PreloadHit>(preloadHits);
        sorted.Sort((a, b) => TierRank(b.Tier).CompareTo(TierRank(a.Tier)));

        const float panelW = 220f;
        const float pad = 6f, titleH = 16f, lineH = 15f;
        float panelH = titleH + sorted.Count * lineH + pad * 2;

        // Corner anchoring — same math as DrawZoneSummary / DrawSessionHud.
        // PreloadAnchor uses lowercase-hyphenated format: "top-right", "bottom-left", etc.
        var corner = ctx.PreloadAnchor ?? "top-right";
        bool isRight  = corner.Contains("right",  StringComparison.OrdinalIgnoreCase);
        bool isBottom = corner.Contains("bottom", StringComparison.OrdinalIgnoreCase);
        const float margin = 10f;

        float left = isRight
            ? ctx.WindowWidth  - margin - panelW - ctx.PreloadOffsetX
            : margin + ctx.PreloadOffsetX;
        float top  = isBottom
            ? ctx.WindowHeight - margin - panelH - ctx.PreloadOffsetY
            : margin + ctx.PreloadOffsetY;
        left = Math.Clamp(left, margin, ctx.WindowWidth  - margin - panelW);
        top  = Math.Clamp(top,  margin, ctx.WindowHeight - margin - panelH);

        rt.FillRectangle(new Vortice.RawRectF(left, top, left + panelW, top + panelH), _bPanel!);

        float cy = top + pad;
        // Header line.
        rt.DrawText("PRELOAD", _tf!, new Rect(left + pad, cy, left + panelW - pad, cy + titleH), _bText!, DrawTextOptions.Clip);
        cy += titleH;
        // Per-hit lines, each coloured by the hit's Color field.
        foreach (var hit in sorted)
        {
            var col = ParseColor(hit.Color, 1f);
            _bStyle!.Color = col;
            var text = $"● {hit.Label}";
            rt.DrawText(text, _tf!, new Rect(left + pad, cy, left + panelW - pad, cy + lineH), _bStyle, DrawTextOptions.Clip);
            cy += lineH;
        }
    }

    /// <summary>
    /// Draw-only guidance routes rendered on the WORLD GROUND, shown when the big map is CLOSED.
    /// Each selected target's smoothed grid waypoints are converted to world space
    /// (grid × <see cref="GridConstants.GridToWorld"/>, at the player's world-Z plane) and projected
    /// via the camera WorldToScreen matrix — the same projection used for nameplates. Lines connect
    /// consecutive waypoints; a marker dot sits on each. Z is approximated by the player's height, so
    /// the line sits at the player's feet plane (it can float/sink on steep slopes — height TBD).
    /// </summary>
    private void DrawPathsWorld(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { } m || ctx.SelectedPaths.Count == 0) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;

        // Ground plane height = the LIVE player feet Z (read this frame, not from the world-rate entity
        // list — the local player is filtered out of that). Paths sit at the player's feet.
        var z = ctx.PlayerWorld?.Z ?? 0f;

        // Project a ground-plane world point to screen; null when it's behind the camera.
        NumVec2? Proj(float wx, float wy)
        {
            var cw = wx * m[3] + wy * m[7] + z * m[11] + m[15];
            if (cw <= 0.0001f) return null;
            var cxp = wx * m[0] + wy * m[4] + z * m[8] + m[12];
            var cyp = wx * m[1] + wy * m[5] + z * m[9] + m[13];
            return new NumVec2((cxp / cw / 2f + 0.5f) * W, (0.5f - cyp / cw / 2f) * H);
        }

        // The line head is pinned to the player's LIVE world position every frame, so the first segment is
        // always (you → next waypoint) and tracks you smoothly between world-rate path updates.
        NumVec2? anchor = ctx.PlayerWorld is { } pw ? Proj(pw.X, pw.Y) : null;

        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count == 0) continue;
            _bPath!.Color = PathColor(path.ColorSlot);

            NumVec2? prev = anchor;
            foreach (var (gx, gy) in path.Points)
            {
                float wx = gx * GridConstants.GridToWorld, wy = gy * GridConstants.GridToWorld;
                if (Proj(wx, wy) is not { } p) { prev = null; continue; } // waypoint behind camera — break the line
                if (prev is { } pr) rt.DrawLine(pr, p, _bPath, 3f);
                rt.FillEllipse(new Ellipse(p, 4f, 4f), _bPath);
                prev = p;
            }
        }
    }

    /// <summary>
    /// The unit-space (≈[-1,1], centered) path geometry for a named library icon, built once and cached.
    /// Each library path's <c>d</c> is parsed (<see cref="SvgPath"/>) and normalized from its viewBox into
    /// the unit space the old hardcoded geometries used, so <see cref="DrawIcon"/> can stamp it with the
    /// same scale+translate transform. Unknown/unparseable names fall back to "Circle".
    /// </summary>
    private ID2D1PathGeometry? GetGeometry(string? name)
    {
        name ??= "Circle";
        if (_geoCache.TryGetValue(name, out var cached)) return cached;

        var built = BuildGeometry(name);
        if (built is null && !name.Equals("Circle", StringComparison.OrdinalIgnoreCase))
            built = GetGeometry("Circle"); // shared fallback instance (deduped on Dispose)
        _geoCache[name] = built;
        return built;
    }

    private ID2D1PathGeometry? BuildGeometry(string name)
    {
        if (!IconLibrary.Map.TryGetValue(name, out var def)) return null;

        // viewBox → unit space: center on the viewBox, uniform-scale so the larger half-extent maps to 1
        // (aspect-preserving; a square 0 0 24 24 box puts an edge point at ±1, matching the old shapes).
        float cx = def.VbX + def.VbW / 2f, cy = def.VbY + def.VbH / 2f;
        float scale = 2f / MathF.Max(def.VbW, def.VbH);
        NumVec2 N(NumVec2 p) => new((p.X - cx) * scale, (p.Y - cy) * scale);

        var factory = (ID2D1Factory)_window.RenderTarget.Factory;
        var geo = factory.CreatePathGeometry();
        bool any = false;
        using (var sink = geo.Open())
        {
            foreach (var d in def.Paths)
                foreach (var fig in SvgPath.Parse(d))
                {
                    sink.BeginFigure(N(fig.Start), FigureBegin.Filled);
                    foreach (var seg in fig.Segs)
                    {
                        switch (seg.Kind)
                        {
                            case SvgPath.SegKind.Line: sink.AddLine(N(seg.End)); break;
                            case SvgPath.SegKind.Cubic: sink.AddBezier(new BezierSegment { Point1 = N(seg.C1), Point2 = N(seg.C2), Point3 = N(seg.End) }); break;
                            case SvgPath.SegKind.Quad: sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = N(seg.C1), Point2 = N(seg.End) }); break;
                        }
                    }
                    sink.EndFigure(fig.Closed ? FigureEnd.Closed : FigureEnd.Open);
                    any = true;
                }
            sink.Close();
        }
        if (any) return geo;
        geo.Dispose();
        return null;
    }

    /// <summary>Draw a named library icon at screen point p with radius r, by stamping its cached unit
    /// geometry via a per-call scale+translate transform.</summary>
    private void DrawIcon(ID2D1RenderTarget rt, string shape, NumVec2 p, float r, ID2D1SolidColorBrush brush, bool filled)
    {
        var geo = GetGeometry(shape);
        if (geo is null) return;
        var prev = rt.Transform;
        rt.Transform = new Matrix3x2(r, 0f, 0f, r, p.X, p.Y);
        if (filled) rt.FillGeometry(geo, brush); else rt.DrawGeometry(geo, brush, 1.5f / r);
        rt.Transform = prev;
    }

    /// <summary>Parse a <c>#RRGGBB</c> color + 0..1 opacity into a Color4 (falls back to opaque white).</summary>
    private static Color4 ParseColor(string hex, float opacity)
    {
        var a = Math.Clamp(opacity, 0f, 1f);
        if (hex is { Length: >= 7 } && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return new Color4(r / 255f, g / 255f, b / 255f, a);
        return new Color4(1f, 1f, 1f, a);
    }

    /// <summary>Clamp a 0..1 channel to a 0..255 byte (rounded).</summary>
    private static byte ToByte(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);

    /// <summary>D2: Ensure the cached terrain Color4 values are up-to-date. No-op when style is unchanged.</summary>
    private void EnsureTerrainColors(RenderContext ctx)
    {
        var ts = ctx.TerrainStyle;
        if (ts.InteriorColor == _terrainCacheIntHex && ts.InteriorOpacity == _terrainCacheIntOp
            && ts.EdgeColor == _terrainCacheEdgeHex && ts.EdgeOpacity == _terrainCacheEdgeOp) return;
        _terrainCacheIntHex = ts.InteriorColor; _terrainCacheIntOp = ts.InteriorOpacity;
        _terrainCacheEdgeHex = ts.EdgeColor;    _terrainCacheEdgeOp = ts.EdgeOpacity;
        _terrainColorInterior = ParseColor(ts.InteriorColor, ts.InteriorOpacity);
        _terrainColorEdge     = ParseColor(ts.EdgeColor,     ts.EdgeOpacity);
    }

    /// <summary>D2: Return a cached Color4 for a display rule, parsing only on first use (or after ruleset change).</summary>
    private Color4 GetRuleColor(Web.DisplayRule rule)
    {
        if (!_ruleColorMemo.TryGetValue(rule, out var c)) { c = ParseColor(rule.Color, rule.Opacity); _ruleColorMemo[rule] = c; }
        return c;
    }

    private void DrawMap(ID2D1RenderTarget rt, RenderContext ctx)
    {
        // D2: invalidate the per-rule color memo when the display ruleset has changed.
        if (ctx.DisplayRulesGen != _ruleColorGen) { _ruleColorMemo.Clear(); _ruleColorGen = ctx.DisplayRulesGen; }

        // MapCenter = window center + DefaultShift(0,-20) + Shift + manual offset.
        var center = new NumVec2(
            ctx.WindowWidth  * 0.5f + ctx.Map.ShiftX + ctx.OffsetX,
            ctx.WindowHeight * 0.5f + ctx.Map.ShiftY - 20f + ctx.OffsetY);
        var scale = ctx.Map.Zoom * (ctx.WindowHeight / 677f) * ctx.ScaleMul;
        var player = ctx.PlayerGrid;

        // Terrain bitmap, projected via the same affine grid→screen transform.
        if (ctx.ShowTerrain && ctx.Terrain is { } t)
        {
            _terrain ??= new TerrainBitmap(rt);
            EnsureTerrainColors(ctx);   // D2: parse only when style changes
            var terrainStyle = new TerrainBitmap.TerrainStyle(
                ToByte(_terrainColorInterior.B), ToByte(_terrainColorInterior.G), ToByte(_terrainColorInterior.R), ToByte(_terrainColorInterior.A),
                ToByte(_terrainColorEdge.B),     ToByte(_terrainColorEdge.G),     ToByte(_terrainColorEdge.R),     ToByte(_terrainColorEdge.A));
            _terrain.EnsureBuiltRaw(t.Walkable, t.Width, t.Height, ctx.AreaHash, inTransition: false, terrainStyle);
            if (_terrain.Bitmap is { } bmp)
            {
                var p00 = Project(new NumVec2(0, 0), player, center, scale);
                var p10 = Project(new NumVec2(t.Width, 0), player, center, scale);
                var p01 = Project(new NumVec2(0, t.Height), player, center, scale);
                var ex = (p10 - p00) / t.Width;
                var ey = (p01 - p00) / t.Height;
                var prev = rt.Transform;
                rt.Transform = new Matrix3x2(ex.X, ex.Y, ey.X, ey.Y, p00.X, p00.Y);
                rt.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, new Rect(0, 0, t.Width, t.Height));
                rt.Transform = prev;
            }
        }

        // Entity dots, decided by the UNIFIED display ruleset (single source of truth). Resolve picks
        // the first enabled rule that matches the entity (top-down, explicit precedence); null or a
        // Hide rule → not drawn; otherwise draw the rule's shape/color/size + optional label. (Junk is
        // still a pre-filter in Phase 1; the API serves every entity regardless for troubleshooting.)
        foreach (var e in ctx.Entities)
        {
            if (ctx.HideJunk && JunkFilter.IsJunk(e.Metadata)) continue;

            var rule = ctx.Resolve?.Invoke(e);
            if (rule is null || rule.Hide) continue;

            var p = Project(new NumVec2(e.Grid.X, e.Grid.Y), player, center, scale);
            _bStyle!.Color = GetRuleColor(rule);   // D2: memoized
            DrawIcon(rt, rule.Shape, p, rule.Size, _bStyle, filled: true);
            if (!string.IsNullOrEmpty(rule.Label))
                rt.DrawText(rule.Label, _tf!, new Rect(p.X + 7, p.Y - 7, p.X + 240, p.Y + 9), _bStyle, DrawTextOptions.Clip);
        }

        // Static tile landmarks (boss arena, treasure, …). Each is styled by its matching "Tile"
        // display rule (unified ruleset) when one applies — shape/color/size/label, or hidden; with no
        // matching tile rule it falls back to the default Landmark style. The layer draws when the
        // default style is enabled OR a tile resolver is wired (so tile rules work even if the default
        // landmark icon is turned off).
        var lmStyle = ctx.Styles.Landmark;
        if (lmStyle.Enabled || ctx.ResolveTile != null)
        {
            var defColor = ParseColor(lmStyle.Color, lmStyle.Opacity);
            foreach (var lm in ctx.Landmarks)
            {
                var tr = ctx.ResolveTile?.Invoke(lm.Path);
                if (tr is { Hide: true }) continue;                 // a tile rule hides this landmark
                if (tr is null && !lmStyle.Enabled) continue;       // no rule + default layer off → skip
                var shape = tr?.Shape ?? lmStyle.Shape;
                var color = tr != null ? GetRuleColor(tr) : defColor;   // D2: memoized for tile rules
                var size  = tr?.Size ?? lmStyle.Size;
                var p = Project(new NumVec2(lm.Center.X, lm.Center.Y), player, center, scale);
                _bStyle!.Color = color;
                DrawIcon(rt, shape, p, size, _bStyle, filled: true);
                // Rule label wins; else curated friendly label (if enabled); else the derived name.
                var label = tr?.Label is { Length: > 0 } rl ? rl
                          : (ctx.UseCuratedLandmarks && lm.CuratedName is { } c ? c : lm.Name);
                rt.DrawText(label, _tf!, new Rect(p.X + 7, p.Y - 7, p.X + 240, p.Y + 9), _bStyle, DrawTextOptions.Clip);
            }
        }

        // Draw-only guidance routes: one full smoothed A* polyline per selected landmark, each in its
        // own legend color (precomputed by RadarApp). Drawn whenever a target is selected (selecting =
        // intent to navigate); not gated on ShowPath.
        DrawPaths(rt, ctx, player, center, scale);

        // Runeshape monoliths: value-coloured ring + N badge + value/reward label.
        DrawMonoliths(rt, ctx, player, center, scale);

        // Player blip on top (toggleable — some prefer no self-marker).
        if (ctx.ShowPlayerBlip)
            rt.FillEllipse(new Ellipse(center, 5f, 5f), _bPlayer!);
    }

    /// <summary>
    /// Draw-only guidance routes. Each selected landmark gets its own smoothed walkable A* polyline
    /// (grid → screen), drawn in that landmark's legend color (<see cref="PathColor"/>). Routes are
    /// precomputed per-target by RadarApp; here we just project and stroke them.
    /// </summary>
    private void DrawPaths(ID2D1RenderTarget rt, RenderContext ctx, NumVec2 player, NumVec2 center, float scale)
    {
        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count < 1) continue;
            _bPath!.Color = PathColor(path.ColorSlot);
            // Anchor the line head at the live player marker (center) so the route stays attached to the
            // player every frame, even between world-rate cursor updates / replans.
            NumVec2? prev = center;
            foreach (var (gx, gy) in path.Points)
            {
                var p = Project(new NumVec2(gx, gy), player, center, scale);
                if (prev is { } pr) rt.DrawLine(pr, p, _bPath, 2.4f);
                prev = p;
            }
        }
    }

    // ── Navigation-menu widget geometry (all in client/device pixels at 96 DPI). ──
    private const float NavPad = 6f, NavRowH = 18f, NavHeaderH = 22f, NavSwatch = 10f, NavPanelW = 230f;
    private const float NavMargin = 6f;                 // gap from the screen edge when pinned
    // ASCII corner buttons (Consolas lacks reliable ↖↗↙↘ glyphs, so we use these per spec).
    private static readonly (string Label, string Corner)[] NavCorners =
    {
        ("[TL]", "TopLeft"), ("[TR]", "TopRight"), ("[BL]", "BottomLeft"), ("[BR]", "BottomRight"),
    };

    /// <summary>
    /// The collapsible, corner-pinnable "POE2Radar" navigation menu. Always drawn (map open or not)
    /// while the overlay is Active + InGame; replaces the old status line AND the bottom-left legend.
    ///
    /// <para>COLLAPSED: a "POE2Radar" chip plus four corner buttons (<c>[TL] [TR] [BL] [BR]</c>).
    /// EXPANDED: the navigation targets (ctx.Legend) under the chip — a colored swatch + curated name
    /// per row; selected rows show a filled swatch in their route color + a highlight, unselected rows
    /// a dim outline swatch. No hotkey-hint line.</para>
    ///
    /// <para>Pinned via <see cref="RenderContext.NavMenuCorner"/>. The panel is anchored at the chosen
    /// corner and CLAMPED so the whole expanded dropdown stays on-screen: <c>*Right</c> right-aligns,
    /// <c>Bottom*</c> grows upward (chip pinned to the bottom edge, rows stacked above it). Every
    /// clickable rect is recorded into <see cref="LegendRowRects"/> with its Action string.</para>
    /// </summary>
    private void DrawNavMenu(ID2D1RenderTarget rt, RenderContext ctx)
    {
        _legendRowRects.Clear();

        var expanded = ctx.NavMenuExpanded;
        var rowCount = expanded ? ctx.Legend.Count : 0;
        var panelW   = NavPanelW;
        var panelH   = NavHeaderH + rowCount * NavRowH + NavPad * 2;

        // Anchor at the chosen corner, then clamp so the whole panel (incl. dropdown) is on-screen.
        var corner   = ctx.NavMenuCorner;
        var isRight  = corner is "TopRight" or "BottomRight";
        var isBottom = corner is "BottomLeft" or "BottomRight";

        var left = isRight ? ctx.WindowWidth - NavMargin - panelW : NavMargin;
        var top  = isBottom ? ctx.WindowHeight - NavMargin - panelH : NavMargin;
        // Clamp into the window in case it's narrower/shorter than the panel.
        left = Math.Clamp(left, NavMargin, Math.Max(NavMargin, ctx.WindowWidth  - NavMargin - panelW));
        top  = Math.Clamp(top,  NavMargin, Math.Max(NavMargin, ctx.WindowHeight - NavMargin - panelH));

        rt.FillRectangle(new Vortice.RawRectF(left, top, left + panelW, top + panelH), _bPanel!);

        // Header row: when pinned to a Bottom corner the dropdown grows UPWARD, so the chip sits at
        // the BOTTOM of the panel and rows stack above it; otherwise the chip is at the top.
        var headerY = isBottom && expanded ? top + panelH - NavPad - NavHeaderH : top + NavPad;

        // "POE2Radar" chip (click → toggle dropdown). Sized to its text so the corner buttons sit after it.
        const string chip = "POE2GPS";
        var chipW = chip.Length * 7.3f + 8f;
        var chipRect = new Vortice.RawRectF(left + NavPad, headerY, left + NavPad + chipW, headerY + NavHeaderH - 2f);
        rt.FillRectangle(chipRect, _bPanel!);
        rt.DrawRectangle(chipRect, _bPlayer!, 1f);
        rt.DrawText((expanded ? "v " : "> ") + chip, _tf!,
            new Rect(chipRect.Left + 4f, headerY + 2f, chipRect.Right, headerY + NavHeaderH), _bText!, DrawTextOptions.Clip);
        _legendRowRects.Add((chipRect, "menu-toggle"));

        // Four corner buttons after the chip. The one matching the current corner is highlighted.
        var bx = chipRect.Right + 6f;
        foreach (var (label, c) in NavCorners)
        {
            var bw = label.Length * 7.3f + 4f;
            var bRect = new Vortice.RawRectF(bx, headerY, bx + bw, headerY + NavHeaderH - 2f);
            var sel = c == corner;
            rt.DrawText(label, _tf!, new Rect(bRect.Left, headerY + 2f, bRect.Right + 6f, headerY + NavHeaderH),
                sel ? _bPlayer! : _bOther!, DrawTextOptions.Clip);
            _legendRowRects.Add((bRect, "corner:" + c));
            bx += bw + 4f;
        }

        if (!expanded) return;

        // Dropdown rows. For Bottom corners the chip is at the bottom, so rows fill from the panel top.
        var rowTop = isBottom ? top + NavPad : headerY + NavHeaderH;
        var y = rowTop;
        foreach (var row in ctx.Legend)
        {
            var rowRect = new Vortice.RawRectF(left, y, left + panelW, y + NavRowH);
            _legendRowRects.Add((rowRect, "target:" + row.Target.Id)); // click → TogglePathTarget(id)

            // Swatch: selected rows fill with their selection-order route color (matches DrawPaths);
            // unselected rows get just a dim outline so the click target is still visible.
            var swatchRect = new Vortice.RawRectF(left + NavPad, y + 3f, left + NavPad + NavSwatch, y + 3f + NavSwatch);
            if (row.IsSelected)
            {
                _bPath!.Color = PathColor(row.ColorSlot);
                rt.FillRectangle(swatchRect, _bPath);
            }
            else
            {
                _bPath!.Color = WithAlpha(_bOther!.Color, 0.45f);
                rt.DrawRectangle(swatchRect, _bPath, 1f);
            }

            // Selected rows get a "> " marker + the highlight color. Entity POIs get a "*" prefix so
            // they're distinguishable from tile landmarks at a glance. Name is already prettified/curated.
            var prefix = row.IsSelected ? "> " : (row.Target.IsEntity ? "* " : "  ");
            var text = prefix + row.Target.Name;
            var textBrush = row.IsSelected ? _bPlayer! : (row.Target.IsEntity ? _bLandmark! : _bText!);
            rt.DrawText(text, _tf!, new Rect(left + NavPad + NavSwatch + 5f, y, left + panelW - 4f, y + NavRowH), textBrush, DrawTextOptions.Clip);
            y += NavRowH;
        }
    }

    private static Color4 WithAlpha(Color4 c, float a) => new(c.R, c.G, c.B, a);

    private static NumVec2 Project(NumVec2 cell, NumVec2 player, NumVec2 center, float scale)
    {
        var d = cell - player;
        var md = MapProjection.GridDeltaToMapDelta(new GameVec2 { X = d.X, Y = d.Y }, scale);
        return new NumVec2(center.X + md.X, center.Y + md.Y);
    }

    public void Dispose()
    {
        _bPlayer?.Dispose(); _bOther?.Dispose(); _bText?.Dispose(); _bPanel?.Dispose(); _bLandmark?.Dispose();
        _bPath?.Dispose(); _bStyle?.Dispose();
        foreach (var geo in _geoCache.Values.Where(g => g is not null).Distinct()) geo!.Dispose();
        _geoCache.Clear();
        _tf?.Dispose();
        _terrain?.Dispose();
        _atlasIcons?.Dispose();
    }
}
