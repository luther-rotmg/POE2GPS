using System.Collections.Generic;

namespace POE2Radar.Core.Palettes;

/// <summary>
/// A user-authored palette: a named set of 13 CSS custom properties (--gold, --ink, --bg, etc.)
/// persisted as a JSON file in the config/palettes/ directory. The <see cref="Preview"/> list
/// is auto-derived from the four most visually distinct vars for dashboard chip rendering.
/// </summary>
public sealed record UserPalette(
    string Slug,
    string DisplayName,
    IReadOnlyDictionary<string, string> Vars,
    IReadOnlyList<string> Preview,
    DateTime CreatedUtc);