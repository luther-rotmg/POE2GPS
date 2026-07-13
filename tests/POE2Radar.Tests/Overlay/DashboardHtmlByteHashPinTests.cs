using System.Security.Cryptography;
using System.Text;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Tests.Overlay;

// v0.33 #29 dashboard-extraction arc — temporary byte-parity guard.
//
// Substring tests (DashboardHtmlProbeTests / DashboardHtmlCampaignGuideTests /
// DashboardContribFallbackTests) do NOT catch subtle byte-drift like a leaked BOM,
// a CRLF→LF flip, an extra whitespace at a splice site, or a doubled newline.
// Any of these would silently mutate the served HTML while leaving all `Assert.Contains`
// checks green.
//
// This test pins the SHA256 of `DashboardHtml.Page` bytes so ANY drift during #29b (JS
// extraction) or #29c (HTML residue extraction) fails CI immediately. The expected hash
// was computed at #29a merge from the pre-refactor const-string bytes.
//
// REMOVE THIS FILE at #29c completion. The final DashboardHtml.Page (fully-extracted
// dashboard.html + dashboard.css + dashboard.js) will have a new SHA256 that we can pin
// there if we want; otherwise the guard's job is done once the extraction arc is complete.
public class DashboardHtmlByteHashPinTests
{
    // Pinned at #29a merge (2026-07-13). If this test fails after a #29b or #29c merge,
    // byte drift has occurred — revert the offending bead per the design rollback plan.
    private const string ExpectedSha256 =
        "88c385508fa8882124953e267de69505a38fa0b891c63cbfd64bb19f5ce6bfc2";

    [Fact]
    public void Page_bytes_match_pinned_sha256()
    {
        var bytes = Encoding.UTF8.GetBytes(DashboardHtml.Page);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(ExpectedSha256, hash);
    }
}
