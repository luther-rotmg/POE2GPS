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
            if (ctx.Active && ctx.InGame)
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

    /// <summary>
    /// World-space HP bars over Magic/Rare/Unique monsters, projected via the camera WorldToScreen
    /// matrix. Drawn whether or not the big map is open (it's a heads-up combat overlay).
    /// </summary>
    private void DrawNameplates(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { } m) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        var hb = ctx.HpBars;
        var st = ctx.Styles;
        foreach (var e in ctx.Entities)
        {
            if (e.Category != Poe2Live.EntityCategory.Monster || !e.IsAlive || e.HpMax <= 0) continue;
            if (e.IsFriendly) continue; // hostile monsters only — no bars over allied/minion mobs

            // Per-rarity enable + width; tier (0..3) drives the scaling border decoration; the bar color
            // is the matching monster icon color so "rare = gold" stays a single setting (NonMonster → no bar).
            var (showBar, bw, colorHex, tier) = e.Rarity switch
            {
                Poe2Live.Rarity.Normal => (ctx.HpBarNormal, hb.WidthNormal, st.MonsterNormal.Color, 0),
                Poe2Live.Rarity.Magic  => (ctx.HpBarMagic,  hb.WidthMagic,  st.MonsterMagic.Color,  1),
                Poe2Live.Rarity.Rare   => (ctx.HpBarRare,   hb.WidthRare,   st.MonsterRare.Color,   2),
                Poe2Live.Rarity.Unique => (ctx.HpBarUnique, hb.WidthUnique, st.MonsterUnique.Color,  3),
                _                      => (false, 0f, "#FFFFFF", 0),
            };
            if (!showBar || bw <= 0f) continue;

            var w = e.World;
            var cw = w.X*m[3] + w.Y*m[7] + w.Z*m[11] + m[15];
            if (cw <= 0.0001f) continue;
            var cx = w.X*m[0] + w.Y*m[4] + w.Z*m[8] + m[12];
            var cy = w.X*m[1] + w.Y*m[5] + w.Z*m[9] + m[13];
            var sx = (cx/cw/2f + 0.5f) * W;
            var sy = (0.5f - cy/cw/2f) * H;
            if (sx < 0 || sx > W || sy < 0 || sy > H) continue;

            var bh = hb.Height;
            var bx = sx - bw / 2f + hb.OffsetX;
            var by = sy + hb.OffsetY; // OffsetY is relative to the mob (negative = above)
            var frac = e.HpFraction;
            var col = ParseColor(colorHex, 1f);
            var barRect = new Vortice.RawRectF(bx, by, bx + bw, by + bh);
            rt.FillRectangle(barRect, _bPanel!);
            _bStyle!.Color = frac < 0.3f ? ColLowHp : col;
            rt.FillRectangle(new Vortice.RawRectF(bx, by, bx + bw * frac, by + bh), _bStyle);
            DrawHpDecoration(rt, tier, barRect, col);
        }
    }

    /// <summary>
    /// Rarity cue on an HP bar: a border whose weight scales with rarity. Tier 0 (Normal) draws nothing,
    /// 1 (Magic) a thin border, 2 (Rare) a thick border, 3 (Unique) a double border. Color is the
    /// rarity color passed in.
    /// </summary>
    private void DrawHpDecoration(ID2D1RenderTarget rt, int tier, Vortice.RawRectF r, Color4 col)
    {
        if (tier <= 0) return; // Normal: undecorated
        _bStyle!.Color = col;
        switch (tier)
        {
            case 1: rt.DrawRectangle(r, _bStyle, 1f); break; // Magic: thin
            case 2: rt.DrawRectangle(r, _bStyle, 2f); break; // Rare: thick
            default:                                          // Unique: double border
                rt.DrawRectangle(r, _bStyle, 1.5f);
                rt.DrawRectangle(new Vortice.RawRectF(r.Left - 3f, r.Top - 3f, r.Right + 3f, r.Bottom + 3f), _bStyle, 1f);
                break;
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

    /// <summary>A resolved, ready-to-draw icon: library icon name, pixel size, and color.</summary>
    private readonly record struct DrawStyle(string Shape, float Size, Color4 Color);

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

    private static DrawStyle ToDrawStyle(IconStyle s) => new(s.Shape, s.Size, ParseColor(s.Color, s.Opacity));

    /// <summary>Clamp a 0..1 channel to a 0..255 byte (rounded).</summary>
    private static byte ToByte(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);

    /// <summary>
    /// Resolve the icon an entity should draw with, or null to skip it. Order: corpses/used chests are
    /// dropped first; then an enabled "mechanic" rule whose match substring is in the metadata wins
    /// (force-drawn, overriding category/ShowMonsters suppression); otherwise the per-category /
    /// per-rarity style applies, honoring each style's own Enabled flag.
    /// </summary>
    private static DrawStyle? ResolveStyle(RenderContext ctx, Poe2Live.EntityDot e)
    {
        // Never draw corpses or already-opened chests, even if a mechanic would otherwise match.
        if (e.Category == Poe2Live.EntityCategory.Monster && !e.IsAlive) return null;
        if (e.Category == Poe2Live.EntityCategory.Chest && e.Opened) return null;

        var st = ctx.Styles;

        // Mechanic overrides — first enabled match wins. Force-draws (e.g. a flagged Expedition marker
        // shows even if its category would normally be filtered).
        foreach (var mech in st.Mechanics)
        {
            if (!mech.Enabled || mech.Match.Count == 0) continue;
            foreach (var key in mech.Match)
            {
                if (!string.IsNullOrEmpty(key) && e.Metadata.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return new DrawStyle(mech.Shape, mech.Size, ParseColor(mech.Color, mech.Opacity));
            }
        }

        IconStyle style;
        switch (e.Category)
        {
            case Poe2Live.EntityCategory.Monster:
                if (!ctx.ShowMonsters) return null;
                style = e.Rarity switch
                {
                    Poe2Live.Rarity.Unique => st.MonsterUnique,
                    Poe2Live.Rarity.Rare   => st.MonsterRare,
                    Poe2Live.Rarity.Magic  => st.MonsterMagic,
                    _                      => st.MonsterNormal,
                };
                break;
            case Poe2Live.EntityCategory.Player:     style = st.Player; break;
            case Poe2Live.EntityCategory.Npc:        style = st.Npc; break;
            case Poe2Live.EntityCategory.Chest:
                if (e.Rarity == Poe2Live.Rarity.Unique) style = st.ChestUnique;
                else if (e.Rarity == Poe2Live.Rarity.Rare) style = st.ChestRare;
                else return null; // rare+ chests only
                break;
            case Poe2Live.EntityCategory.Transition: style = st.Transition; break;
            default:
                if (!e.Poi) return null; // Object/Other → only game-flagged POIs
                style = st.Poi;
                break;
        }
        return style.Enabled ? ToDrawStyle(style) : null;
    }

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

        // Entity dots. Props (Object/Other) and dead monsters are filtered out — they're the
        // clutter; the API still serves them for troubleshooting. Game-flagged POIs (entities
        // with a MinimapIcon component) always draw with a white ring, even if their category
        // would otherwise be filtered (waypoints, checkpoints, shrines, …).
        foreach (var e in ctx.Entities)
        {
            if (ctx.HideJunk && JunkFilter.IsJunk(e.Metadata)) continue;
            if (ResolveStyle(ctx, e) is not { } ds) continue; // null = filtered out
            var p = Project(new NumVec2(e.Grid.X, e.Grid.Y), player, center, scale);
            _bStyle!.Color = ds.Color;
            DrawIcon(rt, ds.Shape, p, ds.Size, _bStyle, filled: true);
        }

        // Static tile landmarks (boss arena, treasure, …) — configured marker + label at the centroid.
        var lmStyle = ctx.Styles.Landmark;
        if (lmStyle.Enabled)
        {
            var lmIcon = lmStyle.Shape;
            var lmColor = ParseColor(lmStyle.Color, lmStyle.Opacity);
            foreach (var lm in ctx.Landmarks)
            {
                var p = Project(new NumVec2(lm.Center.X, lm.Center.Y), player, center, scale);
                _bStyle!.Color = lmColor;
                DrawIcon(rt, lmIcon, p, lmStyle.Size, _bStyle, filled: true);
                // Prefer the curated friendly label when enabled and present; else the derived name.
                var label = ctx.UseCuratedLandmarks && lm.CuratedName is { } c ? c : lm.Name;
                rt.DrawText(label, _tf!, new Rect(p.X + 7, p.Y - 7, p.X + 240, p.Y + 9), _bStyle, DrawTextOptions.Clip);
            }
        }

        // Draw-only guidance routes: one full smoothed A* polyline per selected landmark, each in its
        // own legend color (precomputed by RadarApp). Drawn whenever a target is selected (selecting =
        // intent to navigate); not gated on ShowPath.
        DrawPaths(rt, ctx, player, center, scale);

        // Player blip on top.
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
