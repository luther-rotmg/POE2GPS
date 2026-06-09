using System.Globalization;
using System.Numerics;
using POE2Radar.Core.Game;
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

    // Per-icon-name geometry, built lazily from the SVG IconLibrary and cached for the renderer's
    // lifetime. A name that can't be resolved/parsed is mapped to the Circle geometry so something
    // always draws; the cache may therefore point several keys at one instance (deduped on Dispose).
    private readonly Dictionary<string, ID2D1PathGeometry?> _geoCache = new(StringComparer.OrdinalIgnoreCase);

    private ID2D1SolidColorBrush? _bPlayer, _bOther, _bText, _bPanel, _bLandmark;
    private ID2D1SolidColorBrush? _bPath;  // recolored per route via SetColor
    private ID2D1SolidColorBrush? _bStyle; // scratch brush for config-driven icons / HP bars (recolored per draw)
    private IDWriteTextFormat? _tf;
    private bool _ready;

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
                DrawNameplates(rt, ctx);                   // world-space HP bars over hostile mobs
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
        if (start is null && end is null && (route is null || route.Count == 0)) return;
        float h0 = ctx.AtlasScale, h1 = ctx.AtlasShearX, h2 = ctx.AtlasOffX,
              h3 = ctx.AtlasShearY, h4 = ctx.AtlasScaleY, h5 = ctx.AtlasOffY,
              h6 = ctx.AtlasPersX, h7 = ctx.AtlasPersY;
        NumVec2 Proj(NumVec2 p) { var w = h6 * p.X + h7 * p.Y + 1f; if (MathF.Abs(w) < 1e-6f) w = 1f; return new NumVec2((h0 * p.X + h1 * p.Y + h2) / w, (h3 * p.X + h4 * p.Y + h5) / w); }

        var dark = new Color4(0f, 0f, 0f, 0.6f);
        var bright = new Color4(0.235f, 0.86f, 1f, 0.95f);   // cyan
        var green = new Color4(0.43f, 0.91f, 0.53f, 1f);
        var gold = new Color4(0.878f, 0.702f, 0.255f, 1f);

        if (route is { Count: >= 2 })
        {
            // Graph polyline: dark underlay then bright line (cheap outline for contrast over the atlas), hop dots.
            var pts = new NumVec2[route.Count];
            for (var i = 0; i < route.Count; i++) pts[i] = Proj(route[i]);
            _bStyle!.Color = dark; for (var i = 1; i < pts.Length; i++) rt.DrawLine(pts[i - 1], pts[i], _bStyle, 7f);
            _bStyle.Color = bright; for (var i = 1; i < pts.Length; i++) rt.DrawLine(pts[i - 1], pts[i], _bStyle, 3.5f);
            _bStyle.Color = bright; for (var i = 1; i < pts.Length - 1; i++) rt.DrawEllipse(new Ellipse(pts[i], 4f, 4f), _bStyle, 2f);
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
                if (n.Arrow) DrawAtlasArrow(rt, sx, sy, ccx, ccy, W, H, col, n.Label);
                continue;
            }

            var c = new NumVec2(sx, sy);
            if (n.Selected || n.Arrow)
            {
                // Tracked / arrowed map on-screen → ring in its rule's category colour.
                _bStyle!.Color = col;
                rt.DrawEllipse(new Ellipse(c, 18f, 18f), _bStyle, 3f);
                rt.DrawEllipse(new Ellipse(c, 9f, 9f), _bStyle, 2f);
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
            var label = n.Label ?? (n.IconType > 0 ? n.IconType.ToString() : null);
            if (label != null)
                rt.DrawText(label, _tf!, new Rect(sx + 11f, sy - 9f, sx + 220f, sy + 11f), _bText!, DrawTextOptions.Clip);
        }
    }

    /// <summary>Draw an edge arrow pointing from screen-centre toward an OFF-SCREEN atlas map (sx,sy), so
    /// you can pan toward high-value maps you can't zoom out far enough to see. Clamped to a screen-edge
    /// inset, coloured by the rule, labelled with the map/content.</summary>
    private void DrawAtlasArrow(ID2D1RenderTarget rt, float sx, float sy, float cx, float cy, float W, float H, Color4 col, string? label)
    {
        float dx = sx - cx, dy = sy - cy;
        float len = MathF.Sqrt(dx * dx + dy * dy); if (len < 1f) return;
        float ux = dx / len, uy = dy / len;
        const float margin = 46f;
        float tX = MathF.Abs(ux) > 1e-4f ? (W * 0.5f - margin) / MathF.Abs(ux) : 1e9f;
        float tY = MathF.Abs(uy) > 1e-4f ? (H * 0.5f - margin) / MathF.Abs(uy) : 1e9f;
        float t = MathF.Min(tX, tY);
        float ex = cx + ux * t, ey = cy + uy * t;     // point on the inset screen edge
        float px = -uy, py = ux;                       // perpendicular
        var tip = new NumVec2(ex + ux * 11f, ey + uy * 11f);
        var bl = new NumVec2(ex - ux * 9f + px * 10f, ey - uy * 9f + py * 10f);
        var br = new NumVec2(ex - ux * 9f - px * 10f, ey - uy * 9f - py * 10f);
        _bStyle!.Color = col;
        rt.DrawLine(tip, bl, _bStyle, 4f);
        rt.DrawLine(tip, br, _bStyle, 4f);
        rt.DrawLine(bl, br, _bStyle, 4f);              // close the arrowhead triangle
        if (label != null)
        {
            float lx = ex - ux * 56f, ly = ey - uy * 18f;
            rt.DrawText(label, _tf!, new Rect(lx - 95f, ly - 8f, lx + 95f, ly + 10f), _bText!, DrawTextOptions.Clip);
        }
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

    /// <summary>Unpack a 0xAARRGGBB color (precomputed in RadarApp.BuildHpSpecs) to a Color4 — no string
    /// parse or allocation, runs per bar per frame.</summary>
    private static Color4 ColorFromU(uint u)
        => new(((u >> 16) & 0xFF) / 255f, ((u >> 8) & 0xFF) / 255f, (u & 0xFF) / 255f, ((u >> 24) & 0xFF) / 255f);

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

        // Ground plane height = the player entity's world Z (paths sit at the player's feet).
        var z = 0f;
        foreach (var e in ctx.Entities)
            if (e.Category == Poe2Live.EntityCategory.Player) { z = e.World.Z; break; }

        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count == 0) continue;
            _bPath!.Color = PathColor(path.ColorSlot);

            NumVec2? prev = null;
            foreach (var (gx, gy) in path.Points)
            {
                float wx = gx * GridConstants.GridToWorld, wy = gy * GridConstants.GridToWorld;
                var cw = wx * m[3] + wy * m[7] + z * m[11] + m[15];
                if (cw <= 0.0001f) { prev = null; continue; } // waypoint behind camera — break the line
                var cxp = wx * m[0] + wy * m[4] + z * m[8] + m[12];
                var cyp = wx * m[1] + wy * m[5] + z * m[9] + m[13];
                var p = new NumVec2((cxp / cw / 2f + 0.5f) * W, (0.5f - cyp / cw / 2f) * H);
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

    private void DrawMap(ID2D1RenderTarget rt, RenderContext ctx)
    {
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
            var ci = ParseColor(ctx.TerrainStyle.InteriorColor, ctx.TerrainStyle.InteriorOpacity);
            var ce = ParseColor(ctx.TerrainStyle.EdgeColor, ctx.TerrainStyle.EdgeOpacity);
            var terrainStyle = new TerrainBitmap.TerrainStyle(
                ToByte(ci.B), ToByte(ci.G), ToByte(ci.R), ToByte(ci.A),
                ToByte(ce.B), ToByte(ce.G), ToByte(ce.R), ToByte(ce.A));
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
            _bStyle!.Color = ParseColor(rule.Color, rule.Opacity);
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
                var color = tr != null ? ParseColor(tr.Color, tr.Opacity) : defColor;
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
            if (path.Points.Count < 2) continue;
            _bPath!.Color = PathColor(path.ColorSlot);
            NumVec2? prev = null;
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
        const string chip = "POE2Radar";
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
    }
}
