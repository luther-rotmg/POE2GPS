namespace POE2Radar.Core.Input;

/// <summary>
/// Tap-and-hold-repeat state machine for cycle inputs. Pure + clock-injected (every method takes the
/// current time) so it is fully unit-testable. A tap (the held direction changing from 0 to ±1) fires
/// one step immediately; holding the same direction past <c>initialDelay</c> repeats one step every
/// <c>interval</c>. Releasing (heldDir 0) resets. No game/UI/input-send dependency.
/// </summary>
public sealed class HoldRepeat
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _interval;
    private int _dir;               // direction currently held (0 none, -1 prev, +1 next)
    private DateTime _holdStart;
    private DateTime _lastFire;

    public HoldRepeat(TimeSpan initialDelay, TimeSpan interval)
    {
        _initialDelay = initialDelay;
        _interval = interval;
    }

    /// <summary>Returns the number of cycle steps to fire this poll (0 or 1). <paramref name="heldDir"/>:
    /// -1 = prev held, +1 = next held, 0 = nothing held.</summary>
    public int Update(int heldDir, DateTime now)
    {
        if (heldDir == 0) { _dir = 0; return 0; }            // released → reset
        if (heldDir != _dir)                                  // fresh press / direction flip → tap
        {
            _dir = heldDir;
            _holdStart = now;
            _lastFire = now;
            return 1;
        }
        if (now - _holdStart < _initialDelay) return 0;       // still in the tap window
        if (now - _lastFire >= _interval) { _lastFire = now; return 1; }
        return 0;
    }
}
