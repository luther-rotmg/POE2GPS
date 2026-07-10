using POE2Radar.Core.Session;

// Ring-buffer facet of SessionTracker (Threshold — THR-XP-TRACKER).
// Locks the 9-arg Update overload, XpPerHour + XpWindowMinutes properties,
// zone/town semantics, zero-currentXp skip, Reset re-seed, and the 4 canonical
// XP fields on SessionStats (XpPerHour, CurrentXp, SessionXpDelta, RingFilling).
public class SessionTrackerXpRingTests
{
    private static long T(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    // 9-arg feeder (alive, non-town by default)
    private static SessionStats Feed(SessionTracker t, long currentXp, long nowTicks,
        uint areaHash = 1, bool excludeTowns = false, bool isTown = false,
        string areaCode = "G1_1")
        => t.Update(areaHash, areaCode, areaLevel: 50, playerLevel: 85,
                    hpPct: 100f, nowTicks: nowTicks,
                    excludeTowns: excludeTowns, isTown: isTown,
                    currentXp: currentXp);

    [Fact]
    public void XpWindowMinutes_DefaultsToFive()
    {
        var t = new SessionTracker();
        Assert.Equal(5, t.XpWindowMinutes);
    }

    [Fact]
    public void XpWindowMinutes_ClampsSetterToOneThroughSixty()
    {
        var t = new SessionTracker();
        t.XpWindowMinutes = 0;
        Assert.Equal(1, t.XpWindowMinutes);
        t.XpWindowMinutes = 999;
        Assert.Equal(60, t.XpWindowMinutes);
    }

    [Fact]
    public void XpRing_FallbackWhileFilling_UsesSessionDeltaOverSessionHours()
    {
        var t = new SessionTracker { XpWindowMinutes = 5 };  // slots = max(12, 60) = 60
        Feed(t, currentXp: 1_000_000, nowTicks: T(0));       // seeds baseline
        var stats = Feed(t, currentXp: 1_100_000, nowTicks: T(60));  // +100k over 60s
        // 100_000 xp / (60 s / 3600) = 6_000_000 xp/hr
        Assert.InRange(t.XpPerHour, 5_999_000f, 6_001_000f);
        Assert.InRange(stats.XpPerHour, 5_999_000f, 6_001_000f);
        Assert.Equal(1_100_000L, stats.CurrentXp);
        Assert.Equal(100_000L,   stats.SessionXpDelta);
        Assert.True(stats.RingFilling);
    }

    [Fact]
    public void XpRing_ZeroCurrentXp_SkipsAppendAndPreservesPriorRate()
    {
        var t = new SessionTracker { XpWindowMinutes = 5 };
        Feed(t, currentXp: 1_000_000, nowTicks: T(0));
        var priorStats = Feed(t, currentXp: 1_100_000, nowTicks: T(60));
        float rateBefore = t.XpPerHour;
        Assert.True(rateBefore > 0f);
        // player component unresolved this frame — must NOT append or zero the rate
        var stats = Feed(t, currentXp: 0, nowTicks: T(120));
        Assert.Equal(rateBefore, t.XpPerHour);
        Assert.Equal(rateBefore, stats.XpPerHour);
        // SessionStats.CurrentXp reflects the last GOOD reading (skip-append path).
        Assert.Equal(priorStats.CurrentXp,      stats.CurrentXp);
        Assert.Equal(priorStats.SessionXpDelta, stats.SessionXpDelta);
        Assert.False(float.IsNaN(stats.XpPerHour));
    }

    [Fact]
    public void XpRing_TownFrame_ExcludeTownsTrue_DoesNotAppend()
    {
        var t = new SessionTracker { XpWindowMinutes = 5 };
        Feed(t, currentXp: 1_000_000, nowTicks: T(0));
        Feed(t, currentXp: 1_100_000, nowTicks: T(60));
        float before = t.XpPerHour;
        // Fabricated huge jump — if this appended, rate would spike wildly.
        var stats = Feed(t, currentXp: 9_999_999, nowTicks: T(120),
             excludeTowns: true, isTown: true, areaCode: "G1_town");
        Assert.Equal(before, t.XpPerHour);
        Assert.Equal(before, stats.XpPerHour);
    }

    [Fact]
    public void XpRing_SurvivesZoneCrossing_RateStaysConsistent()
    {
        var t = new SessionTracker { XpWindowMinutes = 5 };
        Feed(t, currentXp: 1_000_000, nowTicks: T(0),  areaHash: 1);
        Feed(t, currentXp: 1_100_000, nowTicks: T(60), areaHash: 1);
        float before = t.XpPerHour;
        // Zone change — hash differs — ring must NOT reset. Steady rate continues.
        Feed(t, currentXp: 1_200_000, nowTicks: T(120), areaHash: 2);
        Assert.InRange(t.XpPerHour, before - 100_000f, before + 100_000f);
    }

    [Fact]
    public void XpRing_ResetClearsRing_FirstPostResetTickReadsZeroRate()
    {
        var t = new SessionTracker { XpWindowMinutes = 5 };
        Feed(t, currentXp: 1_000_000, nowTicks: T(0));
        Feed(t, currentXp: 1_100_000, nowTicks: T(60));
        Assert.True(t.XpPerHour > 0f);
        t.Reset(T(120));
        // Baseline reseeds to whatever XP the first post-reset tick brings — delta=0.
        var stats = Feed(t, currentXp: 1_100_000, nowTicks: T(120));
        Assert.Equal(0f, t.XpPerHour);
        Assert.Equal(0f, stats.XpPerHour);
        Assert.Equal(0L, stats.SessionXpDelta);
        Assert.Equal(1_100_000L, stats.CurrentXp);
        Assert.True(stats.RingFilling);
    }

    [Fact]
    public void XpRing_FullWindow_ComputesRateFromOldestInWindow()
    {
        // 1-minute window -> slots = max(12, 12) = 12. Fill fully at 5s cadence.
        var t = new SessionTracker { XpWindowMinutes = 1 };
        long xp = 1_000_000;
        for (int i = 0; i < 12; i++)
        {
            Feed(t, currentXp: xp, nowTicks: T(i * 5));
            xp += 10_000; // +10k xp per 5s -> 7,200,000/hr steady rate
        }
        // 13th sample rotates the head; oldest becomes the T(5) sample.
        var stats = Feed(t, currentXp: xp, nowTicks: T(60));
        Assert.InRange(t.XpPerHour, 7_100_000f, 7_300_000f);
        Assert.InRange(stats.XpPerHour, 7_100_000f, 7_300_000f);
        Assert.False(stats.RingFilling);
    }

    [Fact]
    public void SessionStats_ExposesFourCanonicalXpFields()
    {
        // Contract lock for Task 8 renderer: sess.XpPerHour, sess.CurrentXp,
        // sess.SessionXpDelta, sess.RingFilling must all be readable off the record.
        var t = new SessionTracker { XpWindowMinutes = 5 };
        var stats = Feed(t, currentXp: 500_000, nowTicks: T(0));
        _ = stats.XpPerHour;
        _ = stats.CurrentXp;
        _ = stats.SessionXpDelta;
        _ = stats.RingFilling;
        Assert.Equal(500_000L, stats.CurrentXp);
        Assert.Equal(0L,       stats.SessionXpDelta);
        Assert.True(stats.RingFilling);
    }

    [Fact]
    public void EightArgUpdate_StillCompilesAndReturnsSessionStats_XpFieldsZeroed()
    {
        // Backward-compat lock: the shipped 8-arg Update signature is unchanged.
        var t = new SessionTracker();
        var stats = t.Update(
            areaHash: 1, areaCode: "G1_1", areaLevel: 1, playerLevel: 1,
            hpPct: 100f, nowTicks: T(0), excludeTowns: false, isTown: false);
        Assert.Equal(0f,   stats.XpPerHour);
        Assert.Equal(0L,   stats.CurrentXp);
        Assert.Equal(0L,   stats.SessionXpDelta);
        Assert.True(stats.RingFilling); // no XP samples yet -> ring not full
    }

    // Threshold — THR-XP-DEDUP regression guard.
    // The renderer feeds Update ~60 Hz but the world-thread accessor for cumulativeXp only
    // refreshes every ~5 s, so between refreshes the same currentXp value arrives ~300 times.
    // If the ring appended each duplicate, all 60 slots at the default XpWindowMinutes=5 (60
    // slots) would fill with identical values inside a second, the effective smoothing window
    // would collapse from 5 minutes to <1 s, and any second frame with an unchanged value
    // would compute delta=0 → rate=0.  Feed 100 identical samples after seeding a real delta
    // and assert the rate does NOT drop to zero: the dedup guard must skip identical samples
    // so the first real (t=0, xp=X0) sample survives as the ring's "oldest" comparand.
    [Fact]
    public void XpRing_DuplicateSamples_DoNotCollapseRate()
    {
        var t = new SessionTracker { XpWindowMinutes = 5 };  // slots = 60
        Feed(t, currentXp: 1_000_000, nowTicks: T(0));       // seeds baseline
        var afterFresh = Feed(t, currentXp: 1_100_000, nowTicks: T(60));  // real delta over 60s
        var rateAfterFresh = afterFresh.XpPerHour;
        Assert.True(rateAfterFresh > 0f);

        // Flood the tracker with 200 duplicates of the last value across a 3s span (mimics
        // ~60 Hz calls between two 5-second world-thread refreshes).
        SessionStats last = afterFresh;
        for (int i = 1; i <= 200; i++)
        {
            long tick = T(60) + (long)(i * TimeSpan.TicksPerMillisecond * 15); // 15 ms apart
            last = Feed(t, currentXp: 1_100_000, nowTicks: tick);
        }

        // Rate must NOT have collapsed to zero — the dedup guard skipped every duplicate,
        // so the ring's oldest comparand is still the seeded (t=0, 1_000_000) sample.
        Assert.True(last.XpPerHour > 0f,
            $"Rate collapsed to {last.XpPerHour}f — dedup guard failed to skip duplicate samples.");
        // CurrentXp / SessionXpDelta must still reflect the last good reading.
        Assert.Equal(1_100_000L, last.CurrentXp);
        Assert.Equal(100_000L,   last.SessionXpDelta);
    }
}
