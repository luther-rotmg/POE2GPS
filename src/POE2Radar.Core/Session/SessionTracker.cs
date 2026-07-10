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

    // ---- XP ring buffer (Threshold — THR-XP-TRACKER) -----------------------------------------
    // Fixed-slot (nowTicks, cumulativeXp) samples for the XP/hour session HUD chip. Zero per-tick
    // allocation once sized (slots array is a one-shot lazy allocation on first real sample; every
    // subsequent append is a round-robin write into that same array). Ring survives zone crossings —
    // XP/hour is a grind metric, not a zone metric. Town frames don't append (reuses the shipped
    // ExcludeTownsFromPace toggle — no new knob). Reset clears the ring so the first post-reset
    // XP-bearing Update re-seeds a delta=0 baseline (no synthetic megaburst).
    private long[]? _xpTicks;
    private long[]? _xpValues;
    private int     _xpSlots;              // 0 until first append sizes the ring
    private int     _xpCount;              // samples written (capped at _xpSlots)
    private int     _xpHead;               // next slot to write; also == oldest slot when full
    private int     _xpWindowMinutes = 5;
    private long    _sessionStartXp;       // baseline for fallback rate (SessionXpDelta / sessionHours)
    private bool    _sessionXpSeen;
    private long    _lastCurrentXp;        // last GOOD currentXp reading (preserved across zero-skip frames)
    private float   _xpPerHour;            // last computed rate; preserved on zero-currentXp frames

    /// <summary>Last computed XP/hour rate. 0f until the ring or the session-fallback delta yields
    /// a meaningful positive value. Preserved verbatim across zero-currentXp frames (skip-append).</summary>
    public float XpPerHour => _xpPerHour;

    /// <summary>
    /// Sliding-window size in minutes for the XP/hour rate. Setter clamps to [1,60] and on-change
    /// drops any accumulated ring state so the new window is populated fresh. The session-average
    /// fallback baseline (_sessionStartXp / _sessionXpSeen) is intentionally preserved: session
    /// baseline is a session concept, not a window concept.
    /// </summary>
    public int XpWindowMinutes
    {
        get => _xpWindowMinutes;
        set
        {
            int v = value < 1 ? 1 : (value > 60 ? 60 : value);
            if (v == _xpWindowMinutes) return;
            _xpWindowMinutes = v;
            _xpTicks  = null;
            _xpValues = null;
            _xpSlots  = 0;
            _xpCount  = 0;
            _xpHead   = 0;
        }
    }

    /// <summary>Feed observed kill data into the kill tracker. Call once per entity per world tick.</summary>
    public void ObserveKill(nint address, Poe2Live.Rarity rarity, int hpCur, int hpMax)
        => _kills.Observe(address, rarity, hpCur, hpMax);

    /// <summary>
    /// 9-arg overload of <see cref="Update(uint,string,int,int,float,long,bool,bool)"/> that also
    /// feeds a cumulative XP sample into the ring buffer. <paramref name="currentXp"/> of 0 (or
    /// negative) means the player-component read failed this frame — the append is skipped and the
    /// prior <see cref="XpPerHour"/> value is preserved (no NaN, no zero-spike). Town frames with
    /// <paramref name="excludeTowns"/>=true are also skipped, so the rate freezes on hideout entry
    /// then decays as older samples age out of the window. The XP sample is folded in BEFORE the
    /// underlying 8-arg call so the returned <see cref="SessionStats"/> reflects this tick's XP.
    /// </summary>
    public SessionStats Update(
        uint   areaHash,
        string areaCode,
        int    areaLevel,
        int    playerLevel,
        float  hpPct,
        long   nowTicks,
        bool   excludeTowns,
        bool   isTown,
        long   currentXp)
    {
        AppendXpSample(currentXp, nowTicks, excludeTowns, isTown);
        return Update(areaHash, areaCode, areaLevel, playerLevel, hpPct, nowTicks, excludeTowns, isTown);
    }

    // Ring-append hot path. Zero per-tick allocations once the ring is sized (the two long[] arrays
    // are a one-shot lazy alloc on first real sample and after XpWindowMinutes changes; every other
    // frame is a round-robin write into that same fixed storage).
    private void AppendXpSample(long currentXp, long nowTicks, bool excludeTowns, bool isTown)
    {
        // Skip on unresolved player component or a hideout/town frame with the exclude toggle on —
        // preserves prior rate + prior CurrentXp / SessionXpDelta reading in Snapshot.
        if (currentXp <= 0)         return;
        if (excludeTowns && isTown) return;

        // Threshold — THR-XP-DEDUP: the caller feeds this method every render frame (~60Hz) but
        // the underlying accessor is only refreshed on a ~5 s cadence, so the same currentXp value
        // arrives ~300 times between fresh reads. Without dedup, the ring fills with duplicate
        // values inside one second, collapsing the effective smoothing span from XpWindowMinutes
        // to sub-second and driving the reported rate to zero during any inter-refresh gap.
        // Skipping identical consecutive samples keeps the ring cadence honest at ~5 s per slot
        // (matching the _xpSlots = XpWindowMinutes * 12 sizing formula).
        if (currentXp == _lastCurrentXp) return;

        // Lazy-allocate the ring on first real sample or after XpWindowMinutes changed.
        if (_xpTicks is null || _xpValues is null || _xpSlots == 0)
        {
            _xpSlots  = Math.Max(12, _xpWindowMinutes * 12); // ~5s cadence assumption
            _xpTicks  = new long[_xpSlots];
            _xpValues = new long[_xpSlots];
            _xpCount  = 0;
            _xpHead   = 0;
        }

        if (!_sessionXpSeen)
        {
            _sessionStartXp = currentXp;
            _sessionXpSeen  = true;
        }

        _lastCurrentXp     = currentXp;
        _xpTicks[_xpHead]  = nowTicks;
        _xpValues[_xpHead] = currentXp;
        _xpHead = (_xpHead + 1) % _xpSlots;
        if (_xpCount < _xpSlots) _xpCount++;

        if (_xpCount >= _xpSlots)
        {
            // Ring full: window-based rate. Oldest sample lives at _xpHead (next-write slot).
            int  oldest = _xpHead;
            long span   = nowTicks - _xpTicks[oldest];
            if (span > 0)
            {
                double windowHours = span / (double)TimeSpan.TicksPerHour;
                long   delta       = currentXp - _xpValues[oldest];
                float  rate        = (float)(delta / windowHours);
                _xpPerHour = rate > 0f ? rate : 0f;
            }
        }
        else
        {
            // Fallback: session-average rate while the ring is still filling.
            long sessionSpan = nowTicks - _sessionStartTicks;
            if (sessionSpan > 0)
            {
                double sessionHours = sessionSpan / (double)TimeSpan.TicksPerHour;
                long   delta        = currentXp - _sessionStartXp;
                float  rate         = (float)(delta / sessionHours);
                _xpPerHour = rate > 0f ? rate : 0f;
            }
        }
    }

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
        // XP ring: clear + drop baseline so the next XP-bearing Update reseeds a delta=0 sample.
        _xpTicks         = null;
        _xpValues        = null;
        _xpSlots         = 0;
        _xpCount         = 0;
        _xpHead          = 0;
        _sessionStartXp  = 0;
        _sessionXpSeen   = false;
        _lastCurrentXp   = 0;
        _xpPerHour       = 0f;
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

        // XP-ring snapshot fields. When no 9-arg call has fed us a sample yet (_sessionXpSeen=false),
        // CurrentXp and SessionXpDelta both read 0 so downstream consumers can distinguish "no XP
        // signal" from "XP signal at zero delta". RingFilling stays true until the round-robin ring
        // has captured one full window's worth of samples.
        long  currentXpOut      = _sessionXpSeen ? _lastCurrentXp : 0L;
        long  sessionXpDeltaOut = _sessionXpSeen ? (_lastCurrentXp - _sessionStartXp) : 0L;
        bool  ringFillingOut    = _xpSlots == 0 || _xpCount < _xpSlots;

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
            XpEfficiency:     _xpEfficiency,
            XpPerHour:        _xpPerHour,
            CurrentXp:        currentXpOut,
            SessionXpDelta:   sessionXpDeltaOut,
            RingFilling:      ringFillingOut);
    }
}
