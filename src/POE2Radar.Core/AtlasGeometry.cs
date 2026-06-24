namespace POE2Radar.Core;

/// <summary>Pure atlas-coordinate helpers (no rendering dependency).</summary>
public static class AtlasGeometry
{
    /// <summary>Returns the center coordinate given a top-left position and dimension.
    /// Falls back to <paramref name="fallback"/> (nominally 40f, the node tile size)
    /// when <paramref name="size"/> is not greater than 1f (unread / zero / NaN).</summary>
    public static float AtlasCentre(float pos, float size, float fallback = 40f) =>
        pos + (size > 1f ? size : fallback) * 0.5f;
}
