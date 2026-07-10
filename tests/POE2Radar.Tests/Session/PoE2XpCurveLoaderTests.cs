using System;
using POE2Radar.Core.Session;
using Xunit;

namespace POE2Radar.Tests;

public class PoE2XpCurveLoaderTests
{
    [Fact]
    public void Cumulative_LoadsExactlyOneHundredEntries()
    {
        var arr = PoE2XpCurveLoader.Cumulative;
        Assert.Equal(100, arr.Length);
        Assert.Equal(0L, arr[0]);
        Assert.Equal(4_250_334_444L, arr[99]);
    }

    // At the exact L20 threshold, 187,025 XP remain to reach L21.
    [Fact]
    public void XpToNextLevel_L20_KnownDelta()
    {
        var need = PoE2XpCurveLoader.XpToNextLevel(20, 843_709L);
        Assert.Equal(187_025L, need);
    }

    // At the exact L50 threshold, 5,957,868 XP remain to reach L51.
    [Fact]
    public void XpToNextLevel_L50_KnownDelta()
    {
        var need = PoE2XpCurveLoader.XpToNextLevel(50, 54_607_467L);
        Assert.Equal(5_957_868L, need);
    }

    // At the exact L90 threshold, 160,890,604 XP remain to reach L91.
    [Fact]
    public void XpToNextLevel_L90_KnownDelta()
    {
        var need = PoE2XpCurveLoader.XpToNextLevel(90, 1_934_009_687L);
        Assert.Equal(160_890_604L, need);
    }

    [Fact]
    public void XpToNextLevel_L100_ReturnsNull()
    {
        Assert.Null(PoE2XpCurveLoader.XpToNextLevel(100, 4_250_334_444L));
    }

    [Fact]
    public void XpToNextLevel_InvalidLevel_ReturnsNull()
    {
        Assert.Null(PoE2XpCurveLoader.XpToNextLevel(0, 100L));
        Assert.Null(PoE2XpCurveLoader.XpToNextLevel(-3, 100L));
    }

    // Overshoot clamps to zero rather than emitting a negative remaining.
    [Fact]
    public void XpToNextLevel_OvershotThreshold_ClampsToZero()
    {
        var need = PoE2XpCurveLoader.XpToNextLevel(20, 2_000_000L);
        Assert.Equal(0L, need);
    }

    // 187,025 XP left at 100,000 XP/h == 1.87025 hours (~1h 52m 12.9s).
    [Fact]
    public void TimeToNextLevel_L20_AtHundredKPerHour()
    {
        var ttn = PoE2XpCurveLoader.TimeToNextLevel(20, 843_709L, 100_000f);
        Assert.NotNull(ttn);
        Assert.Equal(TimeSpan.FromHours(187_025.0 / 100_000.0), ttn!.Value);
    }

    [Fact]
    public void TimeToNextLevel_NonPositiveRate_ReturnsNull()
    {
        Assert.Null(PoE2XpCurveLoader.TimeToNextLevel(20, 843_709L, 0f));
        Assert.Null(PoE2XpCurveLoader.TimeToNextLevel(20, 843_709L, -50f));
    }

    [Fact]
    public void TimeToNextLevel_MaxLevel_ReturnsNull()
    {
        Assert.Null(PoE2XpCurveLoader.TimeToNextLevel(100, 5_000_000_000L, 1_000_000f));
    }
}
