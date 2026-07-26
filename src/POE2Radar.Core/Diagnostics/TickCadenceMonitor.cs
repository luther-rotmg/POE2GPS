using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// v0.42 C1: monitors the world-tick fingerprint stream for staleness and dynamically adapts the
/// render FPS cap when reads appear to be outrunning the game's render cadence (controller-mode
/// FPS-mismatch symptom). Thread-safe: <see cref="RecordWorldTick"/> is called from the WorldLoop
/// thread; <see cref="AdaptedFpsCap"/>, <see cref="EffectiveWorldHz"/>, and <see cref="Snapshot"/>
/// are read from the render thread.
/// </summary>
public sealed class TickCadenceMonitor
{
    // ── Configurable thresholds (set-able; defaults match Decision 5). ──
    /// <summary>Consecutive world ticks with byte-identical state fingerprint before
    /// the adaptive throttle engages. 15 = ~500 ms at WorldHz=30.</summary>
    public int StaleFingerprintTickThreshold { get; set; } = 15;

    /// <summary>Seconds to wait between throttle adjustments (up or down). Prevents oscillation.</summary>
    public int StaleAdaptCoolDownSeconds { get; set; } = 10;

    /// <summary>Minimum seconds the cap must stay RESTORED before a new stale run may re-engage
    /// the throttle. Without this floor the engage gate — measured from the last engage — is
    /// already satisfied the instant a restore happens, so a semi-quiet scene re-throttles
    /// ~<see cref="StaleFingerprintTickThreshold"/> ticks later and the cap sits at
    /// <see cref="MinAdaptedFps"/> almost continuously (the "overlay stopped working" report on
    /// high-refresh monitors). 5s with the default 10s cooldown caps the throttled duty cycle at
    /// ~2/3 instead of ~95%. Set to 0 to re-engage immediately after a restore.</summary>
    public int ReEngageCoolDownSeconds { get; set; } = 5;

    /// <summary>Never throttle below this FPS (30 = the world-loop baseline).</summary>
    public int MinAdaptedFps { get; set; } = 30;

    // ── State (single writer: WorldLoop thread). Readers see via volatile / lock-free snapshot. ──
    private volatile int _adaptedFpsCap = int.MaxValue;
    // v0.42.3: track whether the FIRST fingerprint has been seen, so the initial call with
    // fingerprint=0 doesn't accidentally match _lastFingerprint=default(int)=0 and prime
    // _staleTicks off-by-one. Without this, the very first RecordWorldTick call after startup
    // (or after Clear()) treated any zero-fingerprint as a repeat.
    private bool _hasFirstFingerprint;
    private int _lastFingerprint;
    private int _staleTicks;
    // v0.42.3: split the single _lastActionTicks into engage- and restore-specific stamps so
    // a fresh over-polling event can re-throttle sooner after a restore, instead of
    // being gated by another StaleAdaptCoolDownSeconds window that we're not actually in.
    // v0.42.4: _lastRestoreTicks is no longer merely informational — it enforces the
    // ReEngageCoolDownSeconds dwell floor that keeps a semi-quiet scene from re-throttling
    // ~500 ms after every restore.
    private long _lastThrottleTicks;  // Stopwatch ticks of the last throttle-engage action
    private long _lastRestoreTicks;   // Stopwatch ticks of the last cap-restore action
    private bool _isThrottled;

    // Sliding window: timestamps (Stopwatch.GetTimestamp()) of fingerprint-CHANGE events.
    // Written from WorldLoop thread; read under lock for EffectiveWorldHz / Snapshot.
    private readonly object _changeLock = new();
    private readonly Queue<long> _changeTimestamps = new();
    private static readonly long WindowTicks = Stopwatch.Frequency; // 1 second

    /// <summary>
    /// The adapted FPS cap. <see cref="int.MaxValue"/> when no throttle is active.
    /// The render loop uses <c>Math.Min(configuredCap, AdaptedFpsCap)</c>.
    /// </summary>
    public int AdaptedFpsCap => _adaptedFpsCap;

    /// <summary>
    /// Observed unique-fingerprint rate over the last 1-second sliding window (Hz).
    /// Recomputes from the change-timestamp queue. Thread-safe (lock acquired internally).
    /// </summary>
    public double EffectiveWorldHz
    {
        get
        {
            lock (_changeLock)
            {
                var cutoff = Stopwatch.GetTimestamp() - WindowTicks;
                while (_changeTimestamps.Count > 0 && _changeTimestamps.Peek() < cutoff)
                    _changeTimestamps.Dequeue();
                return _changeTimestamps.Count;
            }
        }
    }

    /// <summary>
    /// Record a world-tick fingerprint. Called from the WorldLoop thread (single writer).
    /// Non-blocking on the hot path — no locks when the fingerprint matches the previous one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordWorldTick(int fingerprint)
    {
        var now = Stopwatch.GetTimestamp();

        // v0.42.3: FIRST fingerprint is treated as a change (not a stale-match), regardless
        // of its numeric value. Prevents the init-zero misprime where the first
        // RecordWorldTick(0) call would collide with _lastFingerprint's default zero and
        // start _staleTicks off-by-one.
        if (!_hasFirstFingerprint)
        {
            _hasFirstFingerprint = true;
            _lastFingerprint = fingerprint;
            _staleTicks = 0;
            lock (_changeLock)
            {
                _changeTimestamps.Enqueue(now);
                var cutoff = now - WindowTicks;
                while (_changeTimestamps.Count > 0 && _changeTimestamps.Peek() < cutoff)
                    _changeTimestamps.Dequeue();
            }
            return;
        }

        if (fingerprint == _lastFingerprint)
        {
            // ── Same fingerprint — staleness growing ──
            _staleTicks++;
            var threshold = StaleFingerprintTickThreshold;

            if (_staleTicks >= threshold && !_isThrottled)
            {
                // v0.42.3: the anti-oscillation window applies engage-to-engage, so a restored cap
                // isn't held hostage by a cooldown window we're not actually in.
                // v0.42.4: BUT a bare engage-to-engage gate is already satisfied the moment a
                // restore happens, which let a semi-quiet scene re-throttle ~500 ms later and pinned
                // the cap at MinAdaptedFps ~95% of the time. Require BOTH the engage-to-engage
                // window AND a minimum restored-dwell so the cap actually stays released a while.
                var cooldownTicks = (long)Stopwatch.Frequency * StaleAdaptCoolDownSeconds;
                var reEngageTicks = (long)Stopwatch.Frequency * ReEngageCoolDownSeconds;
                if (now - _lastThrottleTicks >= cooldownTicks &&
                    now - _lastRestoreTicks >= reEngageTicks)
                {
                    // Compute effective Hz from recent fingerprint changes
                    int changeCount;
                    lock (_changeLock)
                    {
                        var cutoff = now - WindowTicks;
                        while (_changeTimestamps.Count > 0 && _changeTimestamps.Peek() < cutoff)
                            _changeTimestamps.Dequeue();
                        changeCount = _changeTimestamps.Count;
                    }

                    var newCap = Math.Max(MinAdaptedFps, changeCount);
                    _adaptedFpsCap = newCap;
                    _isThrottled = true;
                    _lastThrottleTicks = now;
                }
            }
        }
        else
        {
            // ── Fingerprint changed — reset staleness, record change ──
            _lastFingerprint = fingerprint;
            _staleTicks = 0;

            // Record the change timestamp for the sliding window
            lock (_changeLock)
            {
                _changeTimestamps.Enqueue(now);
                var cutoff = now - WindowTicks;
                while (_changeTimestamps.Count > 0 && _changeTimestamps.Peek() < cutoff)
                    _changeTimestamps.Dequeue();
            }

            // Check if we should restore the cap. Gate on time since THROTTLE (not since
            // last restore), matching the semantics "we've been throttled long enough now."
            if (_isThrottled)
            {
                var cooldownTicks = (long)Stopwatch.Frequency * StaleAdaptCoolDownSeconds;
                if (now - _lastThrottleTicks >= cooldownTicks)
                {
                    _adaptedFpsCap = int.MaxValue;
                    _isThrottled = false;
                    _lastRestoreTicks = now;
                }
            }
        }
    }

    /// <summary>
    /// Thread-safe snapshot of all diagnostic fields for /api/state exposure.
    /// </summary>
    public TickCadenceSnapshot Snapshot(int configuredFpsCap = 0, int monitorHz = 0)
    {
        double effectiveHz;
        lock (_changeLock)
        {
            var cutoff = Stopwatch.GetTimestamp() - WindowTicks;
            while (_changeTimestamps.Count > 0 && _changeTimestamps.Peek() < cutoff)
                _changeTimestamps.Dequeue();
            effectiveHz = _changeTimestamps.Count;
        }

        return new TickCadenceSnapshot(
            WorldHz: 30,
            EffectiveWorldHz: effectiveHz,
            StaleTicks: _staleTicks,
            AdaptedFpsCap: _adaptedFpsCap,
            ConfiguredFpsCap: configuredFpsCap,
            MonitorHz: monitorHz);
    }

    /// <summary>Test-only reset. Resets all internal state.</summary>
    public void Clear()
    {
        _hasFirstFingerprint = false;
        _lastFingerprint = 0;
        _staleTicks = 0;
        _adaptedFpsCap = int.MaxValue;
        _lastThrottleTicks = 0;
        _lastRestoreTicks = 0;
        _isThrottled = false;
        lock (_changeLock)
        {
            _changeTimestamps.Clear();
        }
    }
}

/// <summary>
/// Thread-safe snapshot of <see cref="TickCadenceMonitor"/> diagnostic fields.
/// </summary>
public sealed record TickCadenceSnapshot(
    int WorldHz,
    double EffectiveWorldHz,
    int StaleTicks,
    int AdaptedFpsCap,
    int ConfiguredFpsCap,
    int MonitorHz);