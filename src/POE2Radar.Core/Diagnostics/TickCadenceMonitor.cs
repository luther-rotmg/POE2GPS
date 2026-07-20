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

    /// <summary>Never throttle below this FPS (30 = the world-loop baseline).</summary>
    public int MinAdaptedFps { get; set; } = 30;

    // ── State (single writer: WorldLoop thread). Readers see via volatile / lock-free snapshot. ──
    private volatile int _adaptedFpsCap = int.MaxValue;
    private int _lastFingerprint;
    private int _staleTicks;
    private long _lastActionTicks;    // Stopwatch ticks of last throttle or restore action
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

        if (fingerprint == _lastFingerprint)
        {
            // ── Same fingerprint — staleness growing ──
            _staleTicks++;
            var threshold = StaleFingerprintTickThreshold;

            if (_staleTicks >= threshold && !_isThrottled)
            {
                var cooldownTicks = (long)Stopwatch.Frequency * StaleAdaptCoolDownSeconds;
                if (now - _lastActionTicks >= cooldownTicks)
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
                    _lastActionTicks = now;
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

            // Check if we should restore the cap
            if (_isThrottled)
            {
                var cooldownTicks = (long)Stopwatch.Frequency * StaleAdaptCoolDownSeconds;
                if (now - _lastActionTicks >= cooldownTicks)
                {
                    _adaptedFpsCap = int.MaxValue;
                    _isThrottled = false;
                    _lastActionTicks = now;
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
        _lastFingerprint = 0;
        _staleTicks = 0;
        _adaptedFpsCap = int.MaxValue;
        _lastActionTicks = 0;
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