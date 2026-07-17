using System;
using System.Collections.Generic;

namespace POE2Radar.Core.NavDestinations;

/// <summary>
/// A saved navigation destination: a named (x,y) grid coordinate in a specific zone.
/// </summary>
public sealed record NavDestination(Guid Id, string ZoneCode, string Name, int X, int Y);

/// <summary>
/// On-disk envelope for the nav-destinations.json file.
/// </summary>
public sealed record NavDestinationFile(int SchemaVersion, IReadOnlyList<NavDestination> Destinations);