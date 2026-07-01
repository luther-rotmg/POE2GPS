namespace POE2Radar.Core.Game;

/// <summary>Pure geometry for edge arrows: maps an (often off-screen) target point to the point on the
/// inset window border in its direction, plus the unit direction. Extracted from the atlas arrow so both
/// the atlas overlay and off-screen entity arrows share it (and it's unit-testable).</summary>
public static class EdgeArrow
{
    /// <returns>(ex, ey) border point + (ux, uy) unit direction from centre. Degenerate (target == centre)
    /// returns (cx, cy, 0, 0).</returns>
    public static (float ex, float ey, float ux, float uy) BorderPoint(
        float sx, float sy, float cx, float cy, float w, float h, float margin)
    {
        float dx = sx - cx, dy = sy - cy;
        float len = System.MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return (cx, cy, 0f, 0f);
        float ux = dx / len, uy = dy / len;
        float tX = System.MathF.Abs(ux) > 1e-4f ? (w * 0.5f - margin) / System.MathF.Abs(ux) : 1e9f;
        float tY = System.MathF.Abs(uy) > 1e-4f ? (h * 0.5f - margin) / System.MathF.Abs(uy) : 1e9f;
        float t = System.MathF.Min(tX, tY);
        return (cx + ux * t, cy + uy * t, ux, uy);
    }
}
