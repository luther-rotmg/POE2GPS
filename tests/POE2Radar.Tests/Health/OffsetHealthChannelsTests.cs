using System;
using POE2Radar.Core.Health;
using Xunit;

namespace POE2Radar.Tests.Health;

public sealed class OffsetHealthChannelsTests
{
    private const string ChannelId = "TestFamily";

    [Fact]
    public void FromRatio_BelowMinObservations_NotSilentCritical()
    {
        // totalObserved < minObservations → not silent-critical
        var result = OffsetHealthChannels.FromRatio(
            ChannelId,
            totalObserved: 5,
            matchingCount: 0,
            minObservations: 10,
            thresholdRatio: 0.8,
            sustained: TimeSpan.FromSeconds(30),
            ifSilentMessage: "Test family appears to have drifted.");

        Assert.False(result.IsSilentCritical);
        Assert.Contains("not enough data", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromRatio_AboveThresholdMatching_NotSilentCritical()
    {
        // matchingCount is high enough that non-matching ratio < thresholdRatio → not critical
        var result = OffsetHealthChannels.FromRatio(
            ChannelId,
            totalObserved: 100,
            matchingCount: 90,
            minObservations: 10,
            thresholdRatio: 0.8,
            sustained: TimeSpan.FromSeconds(30),
            ifSilentMessage: "Test family appears to have drifted.");

        Assert.False(result.IsSilentCritical);
        // 90/100 matching → 10% non-matching. thresholdRatio=0.8 → needs 80%+ non-matching for critical
        // 10% < 80% → not critical
        Assert.Contains("passed plausibility", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromRatio_BelowThresholdPlusSustained_SilentCritical()
    {
        // Very few matching → non-matching ratio >= thresholdRatio → critical
        var result = OffsetHealthChannels.FromRatio(
            ChannelId,
            totalObserved: 100,
            matchingCount: 5,
            minObservations: 10,
            thresholdRatio: 0.8,
            sustained: TimeSpan.FromSeconds(60),
            ifSilentMessage: "Test family appears to have drifted.");

        Assert.True(result.IsSilentCritical);
        Assert.Equal("Test family appears to have drifted.", result.Message);
    }

    [Fact]
    public void FromRatio_TimeSpanZeroSustained_IsSilentCritical()
    {
        // TimeSpan.Zero sustained should still produce silent-critical when ratio condition is met
        var result = OffsetHealthChannels.FromRatio(
            ChannelId,
            totalObserved: 100,
            matchingCount: 0,
            minObservations: 10,
            thresholdRatio: 0.8,
            sustained: TimeSpan.Zero,
            ifSilentMessage: "Zero sustained, but all reads failed.");

        Assert.True(result.IsSilentCritical);
        Assert.Equal("Zero sustained, but all reads failed.", result.Message);
    }

    [Fact]
    public void FromRatio_ExactlyAtMinObservationsAndFails_IsSilentCritical()
    {
        // Exactly minObservations, all failing
        var result = OffsetHealthChannels.FromRatio(
            ChannelId,
            totalObserved: 10,
            matchingCount: 0,
            minObservations: 10,
            thresholdRatio: 0.8,
            sustained: TimeSpan.FromSeconds(5),
            ifSilentMessage: "All reads at min threshold failed.");

        Assert.True(result.IsSilentCritical);
    }

    [Fact]
    public void FromRatio_AllMatching_NotSilentCritical()
    {
        // All observations matching → certainly not critical
        var result = OffsetHealthChannels.FromRatio(
            ChannelId,
            totalObserved: 50,
            matchingCount: 50,
            minObservations: 10,
            thresholdRatio: 0.8,
            sustained: TimeSpan.Zero,
            ifSilentMessage: "Should not happen.");

        Assert.False(result.IsSilentCritical);
    }

    [Fact]
    public void FromRatio_ChannelIdAppearsInMessage()
    {
        var result = OffsetHealthChannels.FromRatio(
            "MyCustomChannel",
            totalObserved: 100,
            matchingCount: 95,
            minObservations: 10,
            thresholdRatio: 0.8,
            sustained: TimeSpan.Zero,
            ifSilentMessage: "Ignored.");

        Assert.Contains("MyCustomChannel", result.Message);
        Assert.Equal("MyCustomChannel", result.ChannelId);
    }
}