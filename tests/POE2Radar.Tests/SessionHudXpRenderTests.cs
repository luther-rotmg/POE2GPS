using POE2Radar.Overlay.Overlay;
using POE2Radar.Overlay.Config;

// Threshold — THR-XP-RENDER: pure-formatter + zero-cost-when-off gate coverage.
// The renderer (OverlayRenderer.DrawSessionHud) delegates rate/humanization to
// SessionHudXpFormatter so the humanization thresholds + split-vs-single-line
// branch can be exercised without pulling Direct2D. The extension-gate spy
// asserts the same predicate the WorldTick call site uses so the "when the row
// is off, _live.PlayerExperience is never invoked" contract is regression-locked.
public class SessionHudXpRenderTests
{
    // ── Humanization thresholds (spec §4.4):
    //    <1K   → raw digits
    //    <10K  → "1.24K" (two decimals)
    //    <1M   → "245K"  (integer)
    //    <1B   → "1.24M" (two decimals)
    //    else  → "2.10B" (two decimals) ──
    [Theory]
    [InlineData(0L,          "0")]
    [InlineData(1L,          "1")]
    [InlineData(999L,        "999")]
    [InlineData(1_000L,      "1.00K")]
    [InlineData(1_240L,      "1.24K")]
    [InlineData(9_999L,      "9.99K")]
    [InlineData(10_000L,     "10K")]
    [InlineData(245_000L,    "245K")]
    [InlineData(999_999L,    "999K")]
    [InlineData(1_000_000L,  "1.00M")]
    [InlineData(1_240_000L,  "1.24M")]
    [InlineData(999_999_999L,"999M")]
    [InlineData(1_000_000_000L, "1.00B")]
    [InlineData(2_100_000_000L, "2.10B")]
    public void Humanize_MatchesThresholdTable(long value, string expected)
    {
        Assert.Equal(expected, SessionHudXpFormatter.Humanize(value));
    }

    [Fact]
    public void Humanize_NegativeIsClampedToZero()
    {
        Assert.Equal("0", SessionHudXpFormatter.Humanize(-42));
    }

    // ── FormatXpRow — single line when both fit, split while ring is still filling. ──

    [Fact]
    public void FormatXpRow_SingleLine_WhenRingFilled_AndTtlPresent()
    {
        var r = SessionHudXpFormatter.FormatXpRow(
            xpPerHour:    1_240_000f,
            currentLevel: 85,
            currentXp:    1_000_000L,
            ringFilling:  false,
            timeToNextResolver: (_, _, _) => TimeSpan.FromMinutes(12));

        Assert.Equal("XP/hr    1.24M  (12m to L86)", r.primary);
        Assert.Null(r.secondary);
        Assert.False(r.noData);
    }

    [Fact]
    public void FormatXpRow_SplitsTwoLines_WhileRingFilling()
    {
        var r = SessionHudXpFormatter.FormatXpRow(
            xpPerHour:    245_000f,
            currentLevel: 84,
            currentXp:    500_000L,
            ringFilling:  true,
            timeToNextResolver: (_, _, _) => TimeSpan.FromMinutes(45));

        Assert.Equal("XP/hr    245K", r.primary);
        Assert.Equal("(45m to L85)", r.secondary);
        Assert.False(r.noData);
    }

    [Fact]
    public void FormatXpRow_SuppressesTtl_WhenResolverReturnsNull()
    {
        var r = SessionHudXpFormatter.FormatXpRow(
            xpPerHour:    100f,
            currentLevel: 100,   // no next level — resolver returns null
            currentXp:    4_000_000_000L,
            ringFilling:  false,
            timeToNextResolver: (_, _, _) => null);

        Assert.Equal("XP/hr    100", r.primary);
        Assert.Null(r.secondary);
        Assert.False(r.noData);
    }

    [Fact]
    public void FormatXpRow_NoData_WhenXpPerHourZero()
    {
        var r = SessionHudXpFormatter.FormatXpRow(
            xpPerHour:    0f,
            currentLevel: 85,
            currentXp:    1_000_000L,
            ringFilling:  false,
            timeToNextResolver: (_, _, _) => null);

        Assert.Equal("XP/hr    --", r.primary);
        Assert.Null(r.secondary);
        Assert.True(r.noData);   // yellow-tint tell — matches deaths no-data pattern
    }

    [Fact]
    public void FormatXpRow_TtlHours_FormatsAsHoursAndMinutes()
    {
        var r = SessionHudXpFormatter.FormatXpRow(
            xpPerHour:    50_000f,
            currentLevel: 90,
            currentXp:    2_000_000L,
            ringFilling:  false,
            timeToNextResolver: (_, _, _) => TimeSpan.FromMinutes(135));  // 2h 15m

        Assert.Equal("XP/hr    50K  (2h 15m to L91)", r.primary);
    }

    [Fact]
    public void FormatXpRow_TtlDays_FormatsAsDaysAndHours()
    {
        var r = SessionHudXpFormatter.FormatXpRow(
            xpPerHour:    1_000f,
            currentLevel: 95,
            currentXp:    2_000_000_000L,
            ringFilling:  false,
            timeToNextResolver: (_, _, _) => TimeSpan.FromHours(50));   // 2d 2h

        Assert.Equal("XP/hr    1.00K  (2d 2h to L96)", r.primary);
    }

    // ── Zero-cost-when-off gate: RadarApp.WorldTick MUST skip the _live.PlayerExperience
    //    accessor when hud.Enabled == false OR hud.ShowXpRate == false. Assert (a) the
    //    extension gate returns false, (b) the caller spy never fires, (c) zero managed
    //    allocations across 1000 disabled ticks. The gate helper is the exact predicate
    //    the WorldTick call site uses — one grep, one contract. ──

    [Fact]
    public void ShouldReadXpRate_FalseWhenHudDisabled()
    {
        var hud = new SessionHudSettings { Enabled = false, ShowXpRate = true };
        Assert.False(hud.ShouldReadXpRate());
    }

    [Fact]
    public void ShouldReadXpRate_FalseWhenShowXpRateOff()
    {
        var hud = new SessionHudSettings { Enabled = true, ShowXpRate = false };
        Assert.False(hud.ShouldReadXpRate());
    }

    [Fact]
    public void ShouldReadXpRate_TrueOnlyWhenBothOn()
    {
        var hud = new SessionHudSettings { Enabled = true, ShowXpRate = true };
        Assert.True(hud.ShouldReadXpRate());
    }

    [Fact]
    public void ZeroCostWhenOff_1000Ticks_NoAllocations_NoAccessorCalls()
    {
        var hud = new SessionHudSettings { Enabled = false, ShowXpRate = false };
        int spyCalls = 0;

        // Warm up JIT + settle any static-init allocation off the measured window.
        for (int i = 0; i < 16; i++) { if (hud.ShouldReadXpRate()) spyCalls++; }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            // Mirror the RadarApp.WorldTick call site pattern EXACTLY: gate first,
            // never invoke the accessor (spy stands in for _live.PlayerExperience).
            if (hud.ShouldReadXpRate()) { spyCalls++; }
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, spyCalls);
        Assert.Equal(0, after - before);
    }
}
