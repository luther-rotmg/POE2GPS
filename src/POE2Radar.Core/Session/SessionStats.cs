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
    int      DeathsThisZone,
    // v2 — kills, maps/hr, xp-efficiency
    int      KillsNormal,         // observed kills by rarity this session
    int      KillsMagic,
    int      KillsRare,
    int      KillsUnique,
    float    MapsPerHour,         // non-town map zone entries / session hours (0 when < 1 min elapsed)
    int      MapZonesEntered,     // count of non-town zone entries
    int      XpEfficiency,        // playerLevel − areaLevel (positive = over-levelled, negative = under)
    // Threshold — THR-XP-TRACKER: XP-ring chip. Renderer reads all four idempotently.
    float    XpPerHour,           // rate from ring buffer (or session fallback while filling); 0f when no signal
    long     CurrentXp,           // snapshot at this Update call (0 when player component unresolved / no XP fed yet)
    long     SessionXpDelta,      // cumulative delta since construction / Reset(now) — fallback rate source
    bool     RingFilling);        // true until ring holds one full window (renderer splits row to 2 lines while true)
