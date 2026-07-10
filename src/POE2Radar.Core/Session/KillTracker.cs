using POE2Radar.Core.Game;   // Poe2Live, Poe2Live.Rarity
namespace POE2Radar.Core.Session;

/// <summary>Counts monster deaths we actually observe: an entity whose HP we saw >0 then ≤0.
/// Pure; fed per world tick from the entity list (HP already read for HP bars — no new reads).
/// Undercounts kills we never see alive (far off-screen / culled before HP hits 0) — by design,
/// the honest no-offset trade (counting "vanished" entities over-counts badly).</summary>
public sealed class KillTracker
{
    private readonly Dictionary<nint, bool> _sawAlive = new();   // address → we've seen it alive this life
    private int _n, _m, _r, _u;
    // Chorus — v0.25 (CHOR-23): mirror the session counters for the current zone only. Reset in
    // ClearZone(); increment alongside session counters on each observed kill. Feeds the Zone
    // Summary HUD's new kills-this-zone chip.
    private int _nZone, _mZone, _rZone, _uZone;

    public (int normal, int magic, int rare, int unique) Counts => (_n, _m, _r, _u);
    public (int normal, int magic, int rare, int unique) ZoneCounts => (_nZone, _mZone, _rZone, _uZone);

    /// <summary>Observe one monster this tick. Only HP→0 (after being seen alive) counts a kill.</summary>
    public void Observe(nint address, Poe2Live.Rarity rarity, int hpCur, int hpMax)
    {
        if (hpMax <= 0 || rarity == Poe2Live.Rarity.NonMonster) return;   // not a real monster with life
        if (hpCur > 0) { _sawAlive[address] = true; return; }             // alive → remember
        // hpCur <= 0: a death only counts if we saw this address alive (not a first-seen corpse / reused addr)
        if (_sawAlive.TryGetValue(address, out var alive) && alive)
        {
            _sawAlive[address] = false;   // counted; don't recount while it lingers dead
            switch (rarity)
            {
                case Poe2Live.Rarity.Normal: _n++; _nZone++; break;
                case Poe2Live.Rarity.Magic:  _m++; _mZone++; break;
                case Poe2Live.Rarity.Rare:   _r++; _rZone++; break;
                case Poe2Live.Rarity.Unique: _u++; _uZone++; break;
            }
        }
    }

    /// <summary>Drop per-entity tracking on zone change (addresses are reused across zones). Keeps session
    /// totals but zeros the zone counters — the Zone Summary chip resets on zone entry.</summary>
    public void ClearZone()
    {
        _sawAlive.Clear();
        _nZone = _mZone = _rZone = _uZone = 0;
    }

    /// <summary>Full reset (Ctrl+Alt+R): zero the counts + tracking.</summary>
    public void Reset() { _sawAlive.Clear(); _n = _m = _r = _u = 0; _nZone = _mZone = _rZone = _uZone = 0; }
}
