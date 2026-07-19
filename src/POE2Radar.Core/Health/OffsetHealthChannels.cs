using System;

namespace POE2Radar.Core.Health;

/// <summary>
/// Verdict from an offset-health channel. When <see cref="IsSilentCritical"/> is <c>true</c>,
/// the channel has detected that a family of offsets is returning implausible values for a
/// sustained period — likely a patch drift. Each channel is independent (one broken hook does
/// not mask others).
/// </summary>
/// <param name="ChannelId">The probe family / channel identifier, e.g. <c>"AreaInstance"</c>, <c>"Entity"</c>.</param>
/// <param name="Message">Human-readable description of the condition, shown in the drift report.</param>
/// <param name="IsSilentCritical"><c>true</c> when the health condition has been met (enough observations,
/// enough mismatches, sustained long enough).</param>
public sealed record SilentCriticalVerdict(string ChannelId, string Message, bool IsSilentCritical);

/// <summary>
/// Helpers for offset health channels. Each probe family (B1..B8) uses these to report whether
/// its offsets appear to have drifted after a game patch. Independent channels — one family's
/// broken state does not mask others.
/// </summary>
public static class OffsetHealthChannels
{
    /// <summary>
    /// Compute a <see cref="SilentCriticalVerdict"/> from a ratio of matching observations.
    /// "Matching" means the decoded value passed the family-specific plausibility gate.
    /// The verdict is silent-critical when:
    /// <list type="bullet">
    ///   <item><c>totalObserved &gt;= minObservations</c> (enough data to be confident), AND</item>
    ///   <item><c>matchingCount / totalObserved &lt;= 1 - thresholdRatio</c> (too many mismatches)</item>
    /// </list>
    /// The <paramref name="sustained"/> parameter is informational — the caller manages the timer;
    /// this helper reports the condition as silent-critical if the above ratio check passes,
    /// regardless of <paramref name="sustained"/> duration (the caller gates the overall verdict
    /// by whether the condition has persisted long enough).
    /// </summary>
    /// <param name="channelId">The channel identifier (probe family name).</param>
    /// <param name="totalObserved">Total number of observations (reads attempted).</param>
    /// <param name="matchingCount">Number of observations that passed the plausibility gate (matching expected values).</param>
    /// <param name="minObservations">Minimum observations required before a silent-critical verdict can be returned.</param>
    /// <param name="thresholdRatio">The threshold ratio of non-matching to total observations (if non-matching / total &gt;= threshold, we consider it critical).</param>
    /// <param name="sustained">How long the condition has been detected. The caller owns the timer; the helper includes it in the message but does not gate on it.</param>
    /// <param name="ifSilentMessage">Message template to use when the verdict is silent-critical.</param>
    /// <returns>A <see cref="SilentCriticalVerdict"/> with <c>IsSilentCritical=true</c> when the condition is met.</returns>
    public static SilentCriticalVerdict FromRatio(
        string channelId,
        int totalObserved,
        int matchingCount,
        int minObservations,
        double thresholdRatio,
        TimeSpan sustained,
        string ifSilentMessage)
    {
        if (totalObserved < minObservations)
        {
            return new SilentCriticalVerdict(
                channelId,
                $"Channel '{channelId}': {totalObserved}/{minObservations} observations — not enough data yet.",
                false);
        }

        var nonMatchingRatio = 1.0 - (double)matchingCount / totalObserved;
        var isSilent = nonMatchingRatio >= thresholdRatio;

        var message = isSilent
            ? ifSilentMessage
            : $"Channel '{channelId}': {matchingCount}/{totalObserved} passed plausibility (non-matching ratio {nonMatchingRatio:P1} < threshold {thresholdRatio:P1}).";

        return new SilentCriticalVerdict(channelId, message, isSilent);
    }
}