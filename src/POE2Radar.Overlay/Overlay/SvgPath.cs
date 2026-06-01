using System.Globalization;
using System.Numerics;

namespace POE2Radar.Overlay;

/// <summary>
/// Minimal, dependency-free SVG path (<c>d</c> attribute) parser. Produces a flat list of
/// <see cref="SvgFigure"/> in the SVG's own user/viewBox coordinate space — callers normalize.
/// Supports the M/L/H/V/C/S/Q/T/Z and A commands (absolute + relative); arcs are flattened into
/// cubic Béziers so consumers only ever see line/cubic/quadratic segments. No Direct2D types here,
/// so the parser is rendering-agnostic (the overlay turns figures into geometry; nothing else needs to).
/// </summary>
internal static class SvgPath
{
    public enum SegKind { Line, Cubic, Quad }

    /// <summary>One segment from the current point. Line uses <see cref="End"/>; Quad uses
    /// <see cref="C1"/>+<see cref="End"/>; Cubic uses <see cref="C1"/>+<see cref="C2"/>+<see cref="End"/>.</summary>
    public readonly struct Seg
    {
        public readonly SegKind Kind;
        public readonly Vector2 C1, C2, End;
        public Seg(SegKind kind, Vector2 c1, Vector2 c2, Vector2 end) { Kind = kind; C1 = c1; C2 = c2; End = end; }
    }

    public sealed class SvgFigure
    {
        public Vector2 Start;
        public readonly List<Seg> Segs = new();
        public bool Closed;
    }

    /// <summary>Parse a path <c>d</c> string. Returns an empty list on null/garbage (never throws).</summary>
    public static List<SvgFigure> Parse(string? d)
    {
        var figs = new List<SvgFigure>();
        if (string.IsNullOrWhiteSpace(d)) return figs;

        int i = 0, n = d.Length;
        char cmd = '\0';
        Vector2 cur = default, start = default;
        Vector2 prevCubicCtrl = default, prevQuadCtrl = default;
        bool hadCubic = false, hadQuad = false;
        SvgFigure? fig = null;

        void EnsureFig()
        {
            if (fig is null) { fig = new SvgFigure { Start = cur }; figs.Add(fig); }
        }

        while (i < n)
        {
            SkipSep(d, ref i);
            if (i >= n) break;

            var c = d[i];
            if (char.IsLetter(c)) { cmd = c; i++; SkipSep(d, ref i); }
            else if (cmd == '\0') { i++; continue; } // leading garbage

            var up = char.ToUpperInvariant(cmd);
            bool rel = char.IsLower(cmd);

            switch (up)
            {
                case 'M':
                {
                    if (!ReadPoint(d, ref i, cur, rel, out var p)) { i++; break; }
                    cur = p; start = p;
                    fig = new SvgFigure { Start = cur }; figs.Add(fig);
                    cmd = rel ? 'l' : 'L'; // subsequent implicit pairs are line-to
                    hadCubic = hadQuad = false;
                    break;
                }
                case 'L':
                {
                    if (!ReadPoint(d, ref i, cur, rel, out var p)) { i++; break; }
                    EnsureFig(); fig!.Segs.Add(new Seg(SegKind.Line, default, default, p));
                    cur = p; hadCubic = hadQuad = false;
                    break;
                }
                case 'H':
                {
                    if (!TryReadFloat(d, ref i, out var x)) { i++; break; }
                    var p = new Vector2(rel ? cur.X + x : x, cur.Y);
                    EnsureFig(); fig!.Segs.Add(new Seg(SegKind.Line, default, default, p));
                    cur = p; hadCubic = hadQuad = false;
                    break;
                }
                case 'V':
                {
                    if (!TryReadFloat(d, ref i, out var y)) { i++; break; }
                    var p = new Vector2(cur.X, rel ? cur.Y + y : y);
                    EnsureFig(); fig!.Segs.Add(new Seg(SegKind.Line, default, default, p));
                    cur = p; hadCubic = hadQuad = false;
                    break;
                }
                case 'C':
                {
                    if (!ReadPoint(d, ref i, cur, rel, out var c1) ||
                        !ReadPoint(d, ref i, cur, rel, out var c2) ||
                        !ReadPoint(d, ref i, cur, rel, out var p)) { i++; break; }
                    EnsureFig(); fig!.Segs.Add(new Seg(SegKind.Cubic, c1, c2, p));
                    prevCubicCtrl = c2; cur = p; hadCubic = true; hadQuad = false;
                    break;
                }
                case 'S':
                {
                    if (!ReadPoint(d, ref i, cur, rel, out var c2) ||
                        !ReadPoint(d, ref i, cur, rel, out var p)) { i++; break; }
                    var c1 = hadCubic ? cur + (cur - prevCubicCtrl) : cur; // reflect previous control
                    EnsureFig(); fig!.Segs.Add(new Seg(SegKind.Cubic, c1, c2, p));
                    prevCubicCtrl = c2; cur = p; hadCubic = true; hadQuad = false;
                    break;
                }
                case 'Q':
                {
                    if (!ReadPoint(d, ref i, cur, rel, out var c1) ||
                        !ReadPoint(d, ref i, cur, rel, out var p)) { i++; break; }
                    EnsureFig(); fig!.Segs.Add(new Seg(SegKind.Quad, c1, default, p));
                    prevQuadCtrl = c1; cur = p; hadQuad = true; hadCubic = false;
                    break;
                }
                case 'T':
                {
                    if (!ReadPoint(d, ref i, cur, rel, out var p)) { i++; break; }
                    var c1 = hadQuad ? cur + (cur - prevQuadCtrl) : cur;
                    EnsureFig(); fig!.Segs.Add(new Seg(SegKind.Quad, c1, default, p));
                    prevQuadCtrl = c1; cur = p; hadQuad = true; hadCubic = false;
                    break;
                }
                case 'A':
                {
                    if (!TryReadFloat(d, ref i, out var rx) || !TryReadFloat(d, ref i, out var ry) ||
                        !TryReadFloat(d, ref i, out var rot) || !TryReadFloat(d, ref i, out var laf) ||
                        !TryReadFloat(d, ref i, out var sf) || !ReadPoint(d, ref i, cur, rel, out var p))
                    { i++; break; }
                    EnsureFig();
                    foreach (var seg in ArcToCubics(cur, rx, ry, rot, laf != 0f, sf != 0f, p))
                        fig!.Segs.Add(seg);
                    cur = p; hadCubic = hadQuad = false;
                    break;
                }
                case 'Z':
                {
                    if (fig is not null) { fig.Closed = true; cur = start; }
                    fig = null; hadCubic = hadQuad = false;
                    break;
                }
                default:
                    i++; // unknown command — skip a char and continue
                    break;
            }
        }
        return figs;
    }

    // ── tokenizing ──

    private static void SkipSep(string s, ref int i)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c is ' ' or ',' or '\t' or '\n' or '\r') i++; else break;
        }
    }

    private static bool PeekIsNumber(string s, int i)
    {
        SkipSep(s, ref i);
        if (i >= s.Length) return false;
        var c = s[i];
        return char.IsDigit(c) || c is '+' or '-' or '.';
    }

    private static bool TryReadFloat(string s, ref int i, out float v)
    {
        v = 0f;
        SkipSep(s, ref i);
        int n = s.Length, st = i;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        bool any = false;
        while (i < n && char.IsDigit(s[i])) { i++; any = true; }
        if (i < n && s[i] == '.') { i++; while (i < n && char.IsDigit(s[i])) { i++; any = true; } }
        if (any && i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < n && (s[i] == '+' || s[i] == '-')) i++;
            while (i < n && char.IsDigit(s[i])) i++;
        }
        if (!any) { i = st; return false; }
        return float.TryParse(s.AsSpan(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    private static bool ReadPoint(string s, ref int i, Vector2 cur, bool rel, out Vector2 p)
    {
        p = default;
        if (!TryReadFloat(s, ref i, out var x)) return false;
        if (!TryReadFloat(s, ref i, out var y)) return false;
        p = rel ? new Vector2(cur.X + x, cur.Y + y) : new Vector2(x, y);
        return true;
    }

    // ── arc → cubic flattening (endpoint parameterization per the SVG spec) ──

    private static IEnumerable<Seg> ArcToCubics(Vector2 p0, float rx, float ry, float xRotDeg, bool largeArc, bool sweep, Vector2 p1)
    {
        rx = MathF.Abs(rx); ry = MathF.Abs(ry);
        if (rx < 1e-6f || ry < 1e-6f || p0 == p1)
        {
            yield return new Seg(SegKind.Line, default, default, p1);
            yield break;
        }

        var phi = xRotDeg * MathF.PI / 180f;
        float cosP = MathF.Cos(phi), sinP = MathF.Sin(phi);

        // Step 1: transform to the ellipse's coordinate system.
        float dx = (p0.X - p1.X) / 2f, dy = (p0.Y - p1.Y) / 2f;
        float x1 = cosP * dx + sinP * dy;
        float y1 = -sinP * dx + cosP * dy;

        // Correct out-of-range radii.
        float lambda = (x1 * x1) / (rx * rx) + (y1 * y1) / (ry * ry);
        if (lambda > 1f) { var s = MathF.Sqrt(lambda); rx *= s; ry *= s; }

        // Step 2: center.
        float rxsq = rx * rx, rysq = ry * ry, x1sq = x1 * x1, y1sq = y1 * y1;
        float num = MathF.Max(0f, rxsq * rysq - rxsq * y1sq - rysq * x1sq);
        float den = rxsq * y1sq + rysq * x1sq;
        float coef = (largeArc != sweep ? 1f : -1f) * MathF.Sqrt(den <= 0f ? 0f : num / den);
        float cxp = coef * (rx * y1) / ry;
        float cyp = coef * -(ry * x1) / rx;

        float cx = cosP * cxp - sinP * cyp + (p0.X + p1.X) / 2f;
        float cy = sinP * cxp + cosP * cyp + (p0.Y + p1.Y) / 2f;

        // Step 3: angles.
        float ux = (x1 - cxp) / rx, uy = (y1 - cyp) / ry;
        float vx = (-x1 - cxp) / rx, vy = (-y1 - cyp) / ry;
        float theta1 = Angle(1f, 0f, ux, uy);
        float dtheta = Angle(ux, uy, vx, vy);
        if (!sweep && dtheta > 0f) dtheta -= 2f * MathF.PI;
        else if (sweep && dtheta < 0f) dtheta += 2f * MathF.PI;

        int segs = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(dtheta) / (MathF.PI / 2f)));
        float delta = dtheta / segs;
        float t = 8f / 3f * MathF.Sin(delta / 4f) * MathF.Sin(delta / 4f) / MathF.Sin(delta / 2f);

        float ang = theta1;
        var from = p0;
        for (int s = 0; s < segs; s++)
        {
            float a2 = ang + delta;
            float cosA = MathF.Cos(ang), sinA = MathF.Sin(ang);
            float cosB = MathF.Cos(a2), sinB = MathF.Sin(a2);

            var e = EllipsePt(cx, cy, rx, ry, cosP, sinP, cosB, sinB);
            var c1 = new Vector2(
                from.X + t * (cosP * (-rx * sinA) - sinP * (ry * cosA)),
                from.Y + t * (sinP * (-rx * sinA) + cosP * (ry * cosA)));
            var c2 = new Vector2(
                e.X - t * (cosP * (-rx * sinB) - sinP * (ry * cosB)),
                e.Y - t * (sinP * (-rx * sinB) + cosP * (ry * cosB)));

            yield return new Seg(SegKind.Cubic, c1, c2, e);
            from = e; ang = a2;
        }
    }

    private static Vector2 EllipsePt(float cx, float cy, float rx, float ry, float cosP, float sinP, float cosA, float sinA)
        => new(cx + cosP * (rx * cosA) - sinP * (ry * sinA),
               cy + sinP * (rx * cosA) + cosP * (ry * sinA));

    private static float Angle(float ux, float uy, float vx, float vy)
    {
        float dot = ux * vx + uy * vy;
        float len = MathF.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        float a = MathF.Acos(Math.Clamp(len <= 0f ? 1f : dot / len, -1f, 1f));
        return (ux * vy - uy * vx) < 0f ? -a : a;
    }
}
