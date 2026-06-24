namespace POE2Radar.Core.Session;

/// <summary>
/// Immutable snapshot of session counters emitted by <see cref="SessionTracker"/>. Consumed by the
/// overlay renderer (DrawSessionHud) and the HTTP /state API. Pure data — no memory access, no I/O.
/// </summary>
public sealed record SessionStats(
    TimeSpan SessionElapsed,      // wall time since tracker was constructed (app launch) / last Reset
    TimeSpan ZoneElapsed,         // wall time since last zone entry
    int      ZonesEntered,        // count of qualifying zone entries (excludes towns if ExcludeTowns)
    float    ZonesPerHour,        // ZonesEntered / SessionElapsed.TotalHours, 0 when < 1 min elapsed
    string   CurrentZoneName,     // areaCode as-is (display formatting is the renderer's concern)
    int      CurrentAreaLevel,
    int      Deaths,              // lifetime deaths this session
    int      DeathsThisZone);
