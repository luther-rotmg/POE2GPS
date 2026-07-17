using System.Collections.Generic;

namespace POE2Radar.Core.OverlayLayouts;

/// <summary>
/// Per-panel state override for a zone-aware layout preset. All fields are optional;
/// an absent field means "don't change from current." Persisted in camelCase JSON.
/// </summary>
public sealed record PanelState(bool? Visible, int? X, int? Y);

/// <summary>
/// A single zone-aware layout preset: a named match pattern and a set of panel state overrides.
/// The <see cref="Match"/> field is a glob pattern evaluated by <c>ZoneCodeMatcher</c>.
/// </summary>
public sealed record OverlayLayoutPreset(
    string Name,
    string Match,
    IReadOnlyDictionary<string, PanelState> Panels);

/// <summary>
/// On-disk overlay layouts file envelope. Written atomically to <c>config/overlay-layouts.json</c>.
/// </summary>
public sealed record OverlayLayoutFile(
    int SchemaVersion,
    IReadOnlyList<OverlayLayoutPreset> Presets);