using System.Collections.Generic;

namespace POE2Radar.Core.SessionWidget;

/// <summary>
/// Position of the session stat widget overlay on screen.
/// </summary>
public sealed record WidgetPosition(int X, int Y);

/// <summary>
/// Full configuration for the session stat widget, persisted as a single
/// JSON file at <c>config/session-widget.json</c>.
/// </summary>
public sealed record SessionWidgetConfig(
    int SchemaVersion,
    WidgetPosition Position,
    IReadOnlyList<string> Chips);