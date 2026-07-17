using System;
using POE2Radar.Core.SessionWidget;
using Xunit;

namespace POE2Radar.Tests.SessionWidget;

public sealed class SessionMetricProvidersTests
{
    // ── Drops ──────────────────────────────────────────────────────────────

    [Fact]
    public void Drops_Zero_ReturnsZero()
    {
        var result = SessionMetricProviders.Drops(0);
        Assert.Equal("drops", result.Id);
        Assert.Equal("0", result.DisplayText);
    }

    [Fact]
    public void Drops_Positive_ReturnsFormatted()
    {
        var result = SessionMetricProviders.Drops(42);
        Assert.Equal("42", result.DisplayText);
    }

    [Fact]
    public void Drops_LargeNumber_UsesThousandSeparator()
    {
        var result = SessionMetricProviders.Drops(12345);
        Assert.Equal("12,345", result.DisplayText);
    }

    // ── XpGained ───────────────────────────────────────────────────────────

    [Fact]
    public void XpGained_Zero_ReturnsZero()
    {
        var result = SessionMetricProviders.XpGained(0);
        Assert.Equal("xp-gained", result.Id);
        Assert.Equal("0", result.DisplayText);
    }

    [Fact]
    public void XpGained_Positive_ReturnsPlusFormatted()
    {
        var result = SessionMetricProviders.XpGained(1500);
        Assert.Equal("+1,500", result.DisplayText);
    }

    [Fact]
    public void XpGained_Negative_ReturnsMinusFormatted()
    {
        var result = SessionMetricProviders.XpGained(-500);
        Assert.Equal("-500", result.DisplayText);
    }

    // ── BossesKilled ──────────────────────────────────────────────────────

    [Fact]
    public void BossesKilled_Zero()
    {
        var result = SessionMetricProviders.BossesKilled(0);
        Assert.Equal("bosses-killed", result.Id);
        Assert.Equal("0", result.DisplayText);
    }

    [Fact]
    public void BossesKilled_Positive()
    {
        var result = SessionMetricProviders.BossesKilled(7);
        Assert.Equal("7", result.DisplayText);
    }

    // ── Deaths ─────────────────────────────────────────────────────────────

    [Fact]
    public void Deaths_Zero()
    {
        var result = SessionMetricProviders.Deaths(0);
        Assert.Equal("deaths", result.Id);
        Assert.Equal("0", result.DisplayText);
    }

    [Fact]
    public void Deaths_Positive()
    {
        var result = SessionMetricProviders.Deaths(3);
        Assert.Equal("3", result.DisplayText);
    }

    // ── TimeInZone ─────────────────────────────────────────────────────────

    [Fact]
    public void TimeInZone_ZeroSeconds()
    {
        var result = SessionMetricProviders.TimeInZone(TimeSpan.Zero);
        Assert.Equal("time-in-zone", result.Id);
        Assert.Equal("0:00", result.DisplayText);
    }

    [Fact]
    public void TimeInZone_Under60Seconds()
    {
        var result = SessionMetricProviders.TimeInZone(TimeSpan.FromSeconds(45));
        Assert.Equal("0:45", result.DisplayText);
    }

    [Fact]
    public void TimeInZone_UnderOneHour()
    {
        var result = SessionMetricProviders.TimeInZone(
            new TimeSpan(0, 12, 34));
        Assert.Equal("12:34", result.DisplayText);
    }

    [Fact]
    public void TimeInZone_ExactlyOneHour_UsesHourFormat()
    {
        var result = SessionMetricProviders.TimeInZone(TimeSpan.FromHours(1));
        Assert.Equal("1h 0m", result.DisplayText);
    }

    [Fact]
    public void TimeInZone_OverOneHour()
    {
        var result = SessionMetricProviders.TimeInZone(
            new TimeSpan(2, 30, 0));
        Assert.Equal("2h 30m", result.DisplayText);
    }

    // ── AvgMapClearTime ────────────────────────────────────────────────────

    [Fact]
    public void AvgMapClearTime_ZeroMapsPerHour_ReturnsEmDash()
    {
        var result = SessionMetricProviders.AvgMapClearTime(0f);
        Assert.Equal("avg-map-clear-time", result.Id);
        Assert.Equal("\u2014", result.DisplayText);
    }

    [Fact]
    public void AvgMapClearTime_TinyMapsPerHour_ReturnsEmDash()
    {
        var result = SessionMetricProviders.AvgMapClearTime(0.005f);
        Assert.Equal("\u2014", result.DisplayText);
    }

    [Fact]
    public void AvgMapClearTime_TenMapsPerHour_Returns6Min()
    {
        var result = SessionMetricProviders.AvgMapClearTime(10f);
        Assert.Equal("6:00", result.DisplayText);
    }

    [Fact]
    public void AvgMapClearTime_HalfHourAvg_Returns30Min()
    {
        var result = SessionMetricProviders.AvgMapClearTime(2f);
        Assert.Equal("30:00", result.DisplayText);
    }

    [Fact]
    public void AvgMapClearTime_UnderTenSeconds_Format()
    {
        // 400 mph → 3600 / 400 = 9 seconds → "0:09"
        var result = SessionMetricProviders.AvgMapClearTime(400f);
        Assert.Equal("0:09", result.DisplayText);
    }

    // ── Id matching ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("drops")]
    [InlineData("xp-gained")]
    [InlineData("bosses-killed")]
    [InlineData("deaths")]
    [InlineData("time-in-zone")]
    [InlineData("avg-map-clear-time")]
    public void Id_MatchesAllowedChip_ForEachProvider(string expectedId)
    {
        SessionMetricValue result = expectedId switch
        {
            "drops" => SessionMetricProviders.Drops(0),
            "xp-gained" => SessionMetricProviders.XpGained(0),
            "bosses-killed" => SessionMetricProviders.BossesKilled(0),
            "deaths" => SessionMetricProviders.Deaths(0),
            "time-in-zone" => SessionMetricProviders.TimeInZone(TimeSpan.Zero),
            "avg-map-clear-time" => SessionMetricProviders.AvgMapClearTime(0f),
            _ => throw new InvalidOperationException($"Unexpected chip id: {expectedId}"),
        };
        Assert.Equal(expectedId, result.Id);
    }
}