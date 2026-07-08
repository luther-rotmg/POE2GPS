using POE2Radar.Overlay.Web;

namespace POE2Radar.Tests.Overlay;

// EC2-UI (v0.21 Guided Campaign, Task 6): the Zone Plan card in the "director" view exposes three
// CampaignGuide-driven surfaces — a step-text row (#guideStep), a top-of-panel graceful-degradation
// badge (#guideDegradeBadge), and a persistent bottom-of-panel syrairc attribution (#guideAttribution).
// This file locks in the markup shape + the JS↔SSE binding so a future edit to `renderDirectorQueue()`
// or the Zone Plan card can't silently regress the surface. Spec sections 4.1 (badge top), 6 (verbatim
// copy), 7.1 (attribution bottom) are all pinned here.
public class DashboardHtmlCampaignGuideTests
{
    private const string Html = DashboardHtml.Page;

    [Fact]
    public void ZonePlanCard_ContainsGuideStepContainer()
    {
        Assert.Contains("id=\"guideStep\"", Html);
    }

    [Fact]
    public void ZonePlanCard_ContainsDegradationBadgeWithVerbatimSpecCopy()
    {
        Assert.Contains("id=\"guideDegradeBadge\"", Html);
        // Verbatim copy from spec section 6 — mdash, not hyphen; curly right-single-quote entities.
        Assert.Contains(
            "Some steps require v0.22&rsquo;s quest-flag reader &mdash; they&rsquo;ll advance at zone boundary until then.",
            Html);
    }

    [Fact]
    public void ZonePlanCard_ContainsSyraircAttributionAnchor()
    {
        Assert.Contains("id=\"guideAttribution\"", Html);
        Assert.Contains("href=\"https://github.com/syrairc/ExileCampaigns2\"", Html);
        Assert.Contains("Campaign step guide by syrairc", Html);
        Assert.Contains("ExileCampaigns2 &mdash; click to view", Html);
        Assert.Contains("target=\"_blank\"", Html);
        Assert.Contains("rel=\"noopener noreferrer\"", Html);
    }

    [Fact]
    public void RenderDirectorQueue_ReadsCampaignGuideFromState()
    {
        // Confirms the JS binding is wired to the new SSE key. The client reads state.campaignGuide
        // (populated by /state; ApiServer projects RadarState.CampaignGuide → camelCase JSON).
        Assert.Contains("state.campaignGuide", Html);
        Assert.Contains("guideStep", Html);
        Assert.Contains("guideDegradeBadge", Html);
    }

    [Fact]
    public void DegradationBadge_IsPositionedAboveDirQueue()
    {
        int badgeIdx = Html.IndexOf("id=\"guideDegradeBadge\"", System.StringComparison.Ordinal);
        int queueIdx = Html.IndexOf("id=\"dirQueue\"", System.StringComparison.Ordinal);
        Assert.True(badgeIdx > 0 && queueIdx > 0 && badgeIdx < queueIdx,
            "Degradation badge must render above dirQueue (top-of-panel per spec 4.1).");
    }

    [Fact]
    public void Attribution_IsPositionedBelowDirQueue()
    {
        int attrIdx  = Html.IndexOf("id=\"guideAttribution\"", System.StringComparison.Ordinal);
        int queueIdx = Html.IndexOf("id=\"dirQueue\"", System.StringComparison.Ordinal);
        Assert.True(attrIdx > queueIdx && queueIdx > 0,
            "Attribution must render below dirQueue (persistent bottom-of-panel per spec 7.1).");
    }

    [Fact]
    public void GuideStep_And_DegradeBadge_Start_Hidden()
    {
        // Zero-cost-when-off gate: both DOM subtrees must ship the `hidden` attribute so the
        // initial paint (before /state arrives with CampaignGuide=null) draws nothing.
        int stepIdx  = Html.IndexOf("id=\"guideStep\"", System.StringComparison.Ordinal);
        int badgeIdx = Html.IndexOf("id=\"guideDegradeBadge\"", System.StringComparison.Ordinal);
        Assert.True(stepIdx  > 0);
        Assert.True(badgeIdx > 0);

        // Look ahead a small slice from the element opening for the `hidden` attribute.
        var stepSlice  = Html.Substring(stepIdx, System.Math.Min(240, Html.Length - stepIdx));
        var badgeSlice = Html.Substring(badgeIdx, System.Math.Min(240, Html.Length - badgeIdx));
        Assert.Contains(" hidden ", stepSlice);
        Assert.Contains(" hidden ", badgeSlice);
    }
}
