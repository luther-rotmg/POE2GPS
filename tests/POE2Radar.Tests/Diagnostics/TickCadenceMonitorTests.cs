using POE2Radar.Core.Diagnostics;

namespace POE2Radar.Tests.Diagnostics;

/// <summary>
/// v0.42 C1: comprehensive tests for <see cref="TickCadenceMonitor"/>. Each test creates its own
/// instance (no shared mutable state). All use plain xUnit <c>Assert.*</c> — no mocking framework.
/// </summary>
public class TickCadenceMonitorTests
{
    [Fact]
    public void RecordWorldTick_UniqueFingerprints_NoThrottle()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 15;

        for (int i = 0; i < 100; i++)
        {
            mon.RecordWorldTick(i);
            Assert.Equal(int.MaxValue, mon.AdaptedFpsCap);
        }
    }

    [Fact]
    public void RecordWorldTick_IdenticalFingerprintBelowThreshold_NoThrottle()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 15;

        // Exactly threshold - 1 identical — should NOT engage
        for (int i = 0; i < 14; i++)
            mon.RecordWorldTick(42);

        Assert.Equal(int.MaxValue, mon.AdaptedFpsCap);
    }

    [Fact]
    public void RecordWorldTick_IdenticalFingerprintAtThreshold_ThrottleEngages()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 15;
        mon.MinAdaptedFps = 30;
        mon.StaleAdaptCoolDownSeconds = 0; // disable cooldown for deterministic test

        // Feed identical fingerprints until threshold trips.
        // Call 1 sets the fingerprint; calls 2..16 trigger StaleFingerprintTickThreshold=15 stale ticks.
        for (int i = 0; i < 16; i++)
            mon.RecordWorldTick(42);

        // After threshold, AdaptedFpsCap should be less than int.MaxValue
        Assert.True(mon.AdaptedFpsCap < int.MaxValue,
            $"Expected throttle to engage, but cap stayed at {mon.AdaptedFpsCap}");
    }

    [Fact]
    public void RecordWorldTick_ChangedFingerprint_ResetsStaleCounter()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 15;
        mon.StaleAdaptCoolDownSeconds = 0;

        // Feed 14 identical (1 below threshold)
        for (int i = 0; i < 14; i++)
            mon.RecordWorldTick(42);

        Assert.Equal(int.MaxValue, mon.AdaptedFpsCap);

        // Feed a different fingerprint — should reset stale counter
        mon.RecordWorldTick(99);

        // Snapshot should show staleTicks == 0 after change
        var snap = mon.Snapshot();
        Assert.Equal(0, snap.StaleTicks);
    }

    [Fact]
    public void AdaptedFpsCap_NeverBelowMinAdaptedFps()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 1;   // engage immediately
        mon.MinAdaptedFps = 30;
        mon.StaleAdaptCoolDownSeconds = 0;

        // Feed identical fingerprints to force throttle with zero effective Hz
        for (int i = 0; i < 5; i++)
            mon.RecordWorldTick(42);

        Assert.True(mon.AdaptedFpsCap >= mon.MinAdaptedFps,
            $"AdaptedFpsCap {mon.AdaptedFpsCap} should be >= MinAdaptedFps {mon.MinAdaptedFps}");
    }

    [Fact]
    public void AdaptCooldown_PreventsRapidReadjust()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 5;
        mon.StaleAdaptCoolDownSeconds = 0; // first throttle: no cooldown
        mon.MinAdaptedFps = 30;

        // Trigger throttle
        for (int i = 0; i < 6; i++)
            mon.RecordWorldTick(42);

        int firstCap = mon.AdaptedFpsCap;
        Assert.True(firstCap < int.MaxValue, "Throttle should have engaged");

        // Now set cooldown to 10 seconds and feed more identical fingerprints
        mon.StaleAdaptCoolDownSeconds = 10;
        for (int i = 0; i < 20; i++)
            mon.RecordWorldTick(42);

        // The cap should remain at the first adapted value (cooldown prevents re-adjust)
        Assert.Equal(firstCap, mon.AdaptedFpsCap);
    }

    [Fact]
    public void Restoration_AfterCooldownExpires_UnthrottlesWhenChangeResumes()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 5;
        mon.StaleAdaptCoolDownSeconds = 0; // immediate cooldown so restore fires
        mon.MinAdaptedFps = 30;

        // Trigger throttle
        for (int i = 0; i < 6; i++)
            mon.RecordWorldTick(42);

        Assert.NotEqual(int.MaxValue, mon.AdaptedFpsCap);

        // Feed diverse fingerprints (different each time) — should trigger restore
        for (int i = 0; i < 10; i++)
            mon.RecordWorldTick(100 + i);

        // After cooldown expires (0 seconds) and changes resume, the cap should restore
        Assert.Equal(int.MaxValue, mon.AdaptedFpsCap);
    }

    [Fact]
    public void EffectiveWorldHz_ApproximatesActualChangeRate()
    {
        var mon = new TickCadenceMonitor();

        // Feed 30 unique fingerprints (simulating 30 Hz world loop)
        for (int i = 0; i < 30; i++)
            mon.RecordWorldTick(i);

        // EffectiveWorldHz should be approximately 30
        var effHz = mon.EffectiveWorldHz;
        Assert.True(effHz >= 28.0 && effHz <= 32.0,
            $"Expected EffectiveWorldHz ≈ 30, got {effHz}");
    }

    [Fact]
    public void Snapshot_ReflectsCurrentState_WithoutRaceOrTearing()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 3;
        mon.StaleAdaptCoolDownSeconds = 0;

        // Start by feeding unique fingerprints
        for (int i = 0; i < 5; i++)
            mon.RecordWorldTick(i);

        // Feed 1000 unique fingerprints from a SINGLE writer (thread-safe contract:
        // RecordWorldTick is single-writer; AdaptedFpsCap is lock-free reader).
        for (int i = 0; i < 1000; i++)
            mon.RecordWorldTick(1000 + i);

        // Snapshot on the main thread should return internally-consistent values
        var snap = mon.Snapshot();

        // After all those changes, stale ticks should be 0
        Assert.Equal(0, snap.StaleTicks);

        // EffectiveWorldHz should be positive (we recorded many changes)
        Assert.True(snap.EffectiveWorldHz > 0);
    }

    [Fact]
    public void Clear_ResetsAllState()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 3;
        mon.StaleAdaptCoolDownSeconds = 0;

        // Populate and throttle
        for (int i = 0; i < 10; i++)
            mon.RecordWorldTick(42);

        Assert.NotEqual(int.MaxValue, mon.AdaptedFpsCap);

        mon.Clear();

        var snap = mon.Snapshot();
        Assert.Equal(int.MaxValue, snap.AdaptedFpsCap);
        Assert.Equal(0, snap.StaleTicks);
        Assert.Equal(0, snap.EffectiveWorldHz);
    }

    [Fact]
    public void Fingerprint_Zero_IsValidStaleSignal()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 3;
        mon.StaleAdaptCoolDownSeconds = 0;

        // Feed fingerprint 0 repeatedly (0 is a valid fingerprint — no special-case exclusion)
        for (int i = 0; i < 5; i++)
            mon.RecordWorldTick(0);

        // Should engage throttle at threshold
        Assert.True(mon.AdaptedFpsCap < int.MaxValue,
            "Throttle should engage with fingerprint=0 at threshold");
    }

    [Fact]
    public void Concurrent_ReadDuringWrite_AdaptedFpsCap_NeverGarbage()
    {
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 15;
        mon.StaleAdaptCoolDownSeconds = 0;
        mon.MinAdaptedFps = 30;

        var seenValues = new System.Collections.Concurrent.ConcurrentBag<int>();

        // Single writer at approximately 30 Hz (the contract: RecordWorldTick is single-writer).
        var writer = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int tick = 0;
            while (sw.ElapsedMilliseconds < 1000)
            {
                // About 30 ticks/wave: 15 identical to trigger throttle, then 15 diverse to restore
                if ((tick % 30) < 15)
                    mon.RecordWorldTick(42);  // identical → trigger stale
                else
                    mon.RecordWorldTick(tick); // diverse → restore
                tick++;
                // Pace to ~30 Hz (every ~33 ms)
                var elapsed = sw.Elapsed.TotalMilliseconds;
                var targetMs = tick * (1000.0 / 30.0);
                var sleepMs = targetMs - elapsed;
                if (sleepMs > 1) Thread.Sleep((int)sleepMs);
            }
        });

        // High-frequency reader (1000+ Hz) — reads AdaptedFpsCap lock-free.
        var reader = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                seenValues.Add(mon.AdaptedFpsCap);
                Thread.SpinWait(4);
            }
        });

        Task.WaitAll(writer, reader);

        // Every observed value must be either int.MaxValue or in [MinAdaptedFps, 144]
        foreach (var val in seenValues)
        {
            Assert.True(val == int.MaxValue || (val >= 30 && val <= 144),
                $"AdaptedFpsCap had invalid value: {val}");
        }
    }
}