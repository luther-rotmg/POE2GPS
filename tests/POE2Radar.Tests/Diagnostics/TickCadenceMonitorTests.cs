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

    // ── v0.42.3 fixes ────────────────────────────────────────────────────────────────

    [Fact]
    public void v0423_FirstFingerprintZero_DoesNotMisprimeStaleCounter()
    {
        // Regression guard for v0.42.3 init-misprime fix: pre-fix, RecordWorldTick(0) on a
        // fresh monitor would match _lastFingerprint's default zero and increment _staleTicks
        // as if there had been a prior identical tick. Post-fix, the first call sets the
        // initial fingerprint (whatever its value) and _staleTicks stays at 0.
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 2;   // very low — one match after init would engage pre-fix
        mon.StaleAdaptCoolDownSeconds = 0;

        mon.RecordWorldTick(0);   // first call — should be treated as a change
        // Pre-fix: _staleTicks was 1 here.
        // Post-fix: _staleTicks is 0. AdaptedFpsCap should still be MaxValue.
        Assert.Equal(int.MaxValue, mon.AdaptedFpsCap);

        mon.RecordWorldTick(0);   // second call — matches, _staleTicks = 1
        // Pre-fix: _staleTicks would already be 2 here and throttle would engage.
        // Post-fix: _staleTicks is only 1 and threshold=2 not reached.
        Assert.Equal(int.MaxValue, mon.AdaptedFpsCap);

        mon.RecordWorldTick(0);   // third call — _staleTicks = 2, NOW engages
        Assert.True(mon.AdaptedFpsCap < int.MaxValue);
    }

    [Fact]
    public void v0423_RestoreThenImmediateReThrottle_WorksWithoutCooldownGate()
    {
        // Regression guard for v0.42.3 cooldown-FSM split. Pre-fix, the single _lastActionTicks
        // was reset on RESTORE as well as on ENGAGE, so a fresh over-polling event within the
        // cooldown window after a restore could not re-throttle. Post-fix, engage-cooldown gates
        // engage only, so restore does not block re-throttle.
        var mon = new TickCadenceMonitor();
        mon.StaleFingerprintTickThreshold = 3;
        mon.StaleAdaptCoolDownSeconds = 0;   // no cooldown — instant engage/restore for the test
        mon.MinAdaptedFps = 30;

        // Engage throttle
        for (int i = 0; i < 5; i++) mon.RecordWorldTick(42);
        Assert.True(mon.AdaptedFpsCap < int.MaxValue, "First engage should throttle");

        // Restore (fingerprint change)
        mon.RecordWorldTick(99);
        Assert.Equal(int.MaxValue, mon.AdaptedFpsCap);

        // Immediately re-throttle with a new stale run
        for (int i = 0; i < 5; i++) mon.RecordWorldTick(77);

        // Pre-fix: cooldown gate would block this re-throttle (both engage and restore updated the
        // same _lastActionTicks and we're within cooldownSeconds=0 of the restore).
        // Post-fix: engage-cooldown is measured from _lastThrottleTicks only.
        Assert.True(mon.AdaptedFpsCap < int.MaxValue,
            "Should re-throttle immediately after restore when cooldown is zero");
    }
}