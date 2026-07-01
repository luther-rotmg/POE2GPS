using POE2Radar.Core.Game;   // Poe2Live, Poe2Live.Rarity
namespace POE2Radar.Core.Session;

/// <summary>
/// Pure, dependency-free session counter. Fed once per render frame (only when the world snapshot is
/// fresh) with the current area identity, HP percentage, and a caller-supplied tick clock. Emits an
/// immutable <see cref="SessionStats"/>. No memory reads, no rendering, no I/O — lives in
/// POE2Radar.Core so the Core-only test project can cover it.
///
/// HP is the [0,100] percentage (Poe2Live.Vitals.HpPct). Death = exact 0f. A failed vitals read
/// returns the 100f "alive" fallback upstream, so this class never fabricates a death from a bad read.
/// </summary>
public sealed class SessionTracker
{
    private long   _sessionStartTicks;     // set on first Update and on Reset
    private long   _zoneStartTicks;
    private uint   _lastAreaHash;          // 0 = no zone seen yet
    private bool   _firstAreaSeen;         // false until the first Update establishes the initial zone
    private int    _zonesEntered;
    private int    _deaths;
    private int    _deathsThisZone;
    private bool   _hpObservedAboveZero;   // defeats the zone-load HP=0 flash
    private bool   _awaitingRespawn;       // blocks a second death count until HP recovers
    private string _currentZoneName = "";
    private int    _currentAreaLevel;
    // v2 fields
    private readonly KillTracker _kills = new();
    private int    _mapZonesEntered;
    private int    _xpEfficiency;

    /// <summary>Feed observed kill data into the kill tracker. Call once per entity per world tick.</summary>
    public void ObserveKill(nint address, Poe2Live.Rarity rarity, int hpCur, int hpMax)
        => _kills.Observe(address, rarity, hpCur, hpMax);

    /// <summary>
    /// Applies one frame of observation and returns the current snapshot. Call once per render frame,
    /// ONLY when the world snapshot is fresh, so areaHash/areaCode/areaLevel describe the same zone.
    /// </summary>
    public SessionStats Update(
        uint   areaHash,
        string areaCode,
        int    areaLevel,
        int    playerLevel,
        float  hpPct,
        long   nowTicks,
        bool   excludeTowns,
        bool   isTown)
    {
        _currentZoneName  = areaCode;
        _currentAreaLevel = areaLevel;
        _xpEfficiency     = playerLevel - areaLevel;

        if (!_firstAreaSeen)
        {
            // First call: the player was already in this zone before the session began. Establish the
            // initial zone WITHOUT counting it as an entry.
            _firstAreaSeen       = true;
            _lastAreaHash        = areaHash;
            _sessionStartTicks   = nowTicks;
            _zoneStartTicks      = nowTicks;
            _hpObservedAboveZero = false;
            _awaitingRespawn     = false;
            _deathsThisZone      = 0;
        }
        else if (areaHash != _lastAreaHash)
        {
            // Zone change: reset per-zone state first so death detection below sees a clean slate.
            _lastAreaHash        = areaHash;
            _zoneStartTicks      = nowTicks;
            _hpObservedAboveZero = false;
            _awaitingRespawn     = false;
            _deathsThisZone      = 0;
            if (!(excludeTowns && isTown))
                _zonesEntered++;
            if (!isTown)
                _mapZonesEntered++;
            _kills.ClearZone();
        }

        // Death detection (runs after zone-change reset). HP is [0,100]; "zero" is an exact 0f.
        if (hpPct > 0f && !_hpObservedAboveZero)
        {
            _hpObservedAboveZero = true;   // first valid "alive" observation; defeats load-screen 0-flash
        }
        if (_hpObservedAboveZero && !_awaitingRespawn && hpPct == 0f)
        {
            _deaths++;
            _deathsThisZone++;
            _awaitingRespawn = true;
        }
        else if (_awaitingRespawn && hpPct > 0f)
        {
            _awaitingRespawn = false;      // recovered — allow the next death to register
        }

        return Snapshot(nowTicks);
    }

    /// <summary>
    /// Clears all counters to construction-time state, using <paramref name="nowTicks"/> as the new
    /// session/zone start. Keeps the current zone identity (so the next same-zone Update is NOT a new
    /// entry) — _firstAreaSeen stays true and _lastAreaHash is unchanged.
    /// </summary>
    public void Reset(long nowTicks)
    {
        _sessionStartTicks   = nowTicks;
        _zoneStartTicks      = nowTicks;
        _zonesEntered        = 0;
        _deaths              = 0;
        _deathsThisZone      = 0;
        _hpObservedAboveZero = false;
        _awaitingRespawn     = false;
        _firstAreaSeen       = true;       // current zone stays "seen" — no spurious increment next frame
        // _lastAreaHash intentionally left as the current value.
        _kills.Reset();
        _mapZonesEntered = 0;
    }

    private SessionStats Snapshot(long nowTicks)
    {
        double sessionHours  = (nowTicks - _sessionStartTicks) / (double)TimeSpan.TicksPerHour;
        float  zonesPerHour  = sessionHours < (1.0 / 60.0)
            ? 0f
            : (float)(_zonesEntered / sessionHours);
        float  mapsPerHour   = sessionHours < (1.0 / 60.0)
            ? 0f
            : (float)(_mapZonesEntered / sessionHours);

        var (kn, km, kr, ku) = _kills.Counts;

        return new SessionStats(
            SessionElapsed:   TimeSpan.FromTicks(nowTicks - _sessionStartTicks),
            ZoneElapsed:      TimeSpan.FromTicks(nowTicks - _zoneStartTicks),
            ZonesEntered:     _zonesEntered,
            ZonesPerHour:     zonesPerHour,
            CurrentZoneName:  _currentZoneName,
            CurrentAreaLevel: _currentAreaLevel,
            Deaths:           _deaths,
            DeathsThisZone:   _deathsThisZone,
            KillsNormal:      kn,
            KillsMagic:       km,
            KillsRare:        kr,
            KillsUnique:      ku,
            MapsPerHour:      mapsPerHour,
            MapZonesEntered:  _mapZonesEntered,
            XpEfficiency:     _xpEfficiency);
    }
}
