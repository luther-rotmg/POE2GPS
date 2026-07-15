namespace POE2Radar.Core.Themes;

/// <summary>
/// Resolved hex color set for the Session Recap PNG raster renderer.
/// Values are raw #RRGGBB strings (no CSS vars) — the recap draws to a
/// canvas, not a themed DOM tree, so it needs concrete hex.
/// </summary>
public sealed record PaletteColorSet(string Accent, string Panel, string Text, string Border);