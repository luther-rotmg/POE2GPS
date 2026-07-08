using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Task 11 CF-DASH-BUTTONS — verifies that <see cref="ApiServer.SiblingContributeUrl"/>
/// rewrites a user's Contribute-URL base onto one of the Worker's sibling submit-* routes.</summary>
public class ContributeSiblingRouteTests
{
    [Theory]
    [InlineData("https://x.workers.dev",                 "buffs",   "https://x.workers.dev/submit-buffs")]
    [InlineData("https://x.workers.dev/",                "buffs",   "https://x.workers.dev/submit-buffs")]
    [InlineData("https://x.workers.dev/submit-atlas",    "buffs",   "https://x.workers.dev/submit-buffs")]
    [InlineData("https://x.workers.dev/submit-atlas/",   "buffs",   "https://x.workers.dev/submit-buffs")]
    [InlineData("https://x.workers.dev/submit-anything", "preload", "https://x.workers.dev/submit-preload")]
    [InlineData("https://x.workers.dev/submit-buffs",    "preload", "https://x.workers.dev/submit-preload")]
    [InlineData("https://x.workers.dev/api/v1",          "preload", "https://x.workers.dev/api/v1/submit-preload")]
    public void SiblingContributeUrl_rewrites_trailing_submit_segment_or_appends(string url, string sibling, string expected)
    {
        Assert.Equal(expected, ApiServer.SiblingContributeUrl(url, sibling));
    }

    [Theory]
    [InlineData(null,   "buffs",   "/submit-buffs")]
    [InlineData("",     "buffs",   "/submit-buffs")]
    [InlineData("   ",  "preload", "   /submit-preload")]
    public void SiblingContributeUrl_tolerates_null_and_blank_bases(string? url, string sibling, string expected)
    {
        Assert.Equal(expected, ApiServer.SiblingContributeUrl(url!, sibling));
    }
}
