// v0.22 campaign-probe — PROBE-UI verify gate.
// String-search assertions over the raw-string DashboardHtml.Page constant. Pins the four
// user-visible surfaces (Settings toggle, reset button, Contribute-trace button, one-shot
// onboarding toast) so downstream copy drift trips the suite instead of leaking to prod.
namespace POE2Radar.Tests.Campaign.Probe;

using POE2Radar.Overlay.Web;
using Xunit;

public class DashboardHtmlProbeTests
{
    [Fact]
    public void SettingsToggle_ProbeToggleBoundToEnableCampaignProbe()
    {
        // Row copy per Task 8 brief §4. Toggle is auto-wired via existing [data-set] machinery.
        Assert.Contains("Campaign trace probe", DashboardHtml.Page);
        Assert.Contains("helps POE2GPS&rsquo;s Campaign Director learn campaign routes from your play", DashboardHtml.Page);
        Assert.Contains("data-set=\"enableCampaignProbe\"", DashboardHtml.Page);
    }

    [Fact]
    public void SettingsToggle_ResetInstallIdButtonPresent()
    {
        Assert.Contains("id=\"tpResetInstall\"", DashboardHtml.Page);
        Assert.Contains("Reset trace session id", DashboardHtml.Page);
        Assert.Contains("/api/probe/reset-install-id", DashboardHtml.Page);
    }

    [Fact]
    public void ZonePlanCard_ContributeTraceButtonPresent()
    {
        // Button lives in the Zone Plan card (data-view="director" #dirQueueCard).
        var page = DashboardHtml.Page;
        var zpIdx = page.IndexOf("id=\"dirQueueCard\"", System.StringComparison.Ordinal);
        var zpEnd = page.IndexOf("id=\"guideAttribution\"", zpIdx, System.StringComparison.Ordinal);
        Assert.True(zpIdx > 0 && zpEnd > zpIdx, "Zone Plan card anchor not found");
        var slice = page.Substring(zpIdx, zpEnd - zpIdx);
        Assert.Contains("id=\"tpContribute\"", slice);
        Assert.Contains("Contribute trace", slice);
        Assert.Contains("id=\"savedMsgTp\"", slice);
    }

    [Fact]
    public void ContributeTrace_HandlerPostsToContributeTraceEndpoint()
    {
        Assert.Contains("$('#tpContribute')", DashboardHtml.Page);
        Assert.Contains("/api/contribute-trace", DashboardHtml.Page);
    }

    [Fact]
    public void SyncContribVisibility_HidesContributeTraceWhenProbeOff()
    {
        // syncContribVisibility() must gate #tpContribute on data-set="enableCampaignProbe".
        var page = DashboardHtml.Page;
        var fnIdx = page.IndexOf("function syncContribVisibility()", System.StringComparison.Ordinal);
        var fnEnd = page.IndexOf("}", fnIdx, System.StringComparison.Ordinal);
        Assert.True(fnIdx > 0 && fnEnd > fnIdx);
        var body = page.Substring(fnIdx, fnEnd - fnIdx);
        Assert.Contains("[data-set=\"enableCampaignProbe\"]", body);
        Assert.Contains("#tpContribute", body);
    }

    [Fact]
    public void OnboardingToast_VerbatimCopyPerSpec()
    {
        // Pin the user-visible wording. If any copy drifts, this test flags it before ship.
        Assert.Contains("Campaign trace probe is on.", DashboardHtml.Page);
        Assert.Contains("Your zone traversals get logged to a local file (nothing uploads).", DashboardHtml.Page);
        Assert.Contains("One-click", DashboardHtml.Page);
        Assert.Contains("Contribute trace", DashboardHtml.Page);
        Assert.Contains("in the Campaign panel shares a session so POE2GPS", DashboardHtml.Page);
        Assert.Contains("Campaign Director gets smarter with more players", DashboardHtml.Page);
        Assert.Contains("The shared pool is public.", DashboardHtml.Page);
        Assert.Contains("Turn off in", DashboardHtml.Page);
        Assert.Contains("Settings", DashboardHtml.Page);
        Assert.Contains("Campaign trace probe.", DashboardHtml.Page);
        Assert.Contains("Got it", DashboardHtml.Page);
    }

    [Fact]
    public void OnboardingToast_OneShotGatedOnFlags()
    {
        // showProbeOnboardingIfNeeded fires only when EnableCampaignProbe && !ProbeOnboardingSeen.
        Assert.Contains("function showProbeOnboardingIfNeeded(", DashboardHtml.Page);
        Assert.Contains("s.enableCampaignProbe", DashboardHtml.Page);
        Assert.Contains("s.probeOnboardingSeen", DashboardHtml.Page);
        // Got-it click sets ProbeOnboardingSeen=true via /api/settings POST.
        Assert.Contains("probeOnboardingSeen:true", DashboardHtml.Page);
    }

    [Fact]
    public void OnboardingToast_InvokedFromLoadSettingsChain()
    {
        // loadSettings() tail must invoke the one-shot after syncContribVisibility so the
        // toast can read the freshly-loaded settings blob.
        Assert.Contains("showProbeOnboardingIfNeeded(s)", DashboardHtml.Page);
    }
}
