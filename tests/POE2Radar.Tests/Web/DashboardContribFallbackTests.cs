using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

public class DashboardContribFallbackTests
{
    // Anchor: current silent window.open fallback lives at DashboardHtml.cs:1881 (post-Task-11 re-grep).
    // CF-FALLBACK-UX removes it; the string must not survive the diff — anywhere on the page,
    // so the buff + preload Contribute handlers (Task 11) that share the same fallback URL must
    // also get the sentinel-split treatment.
    [Fact]
    public void EaContribute_NoLongerOpensGithubTemplateSilently()
    {
        Assert.DoesNotContain(
            "github.com/luther-rotmg/POE2GPS/issues/new?template=entity-name-submission.yml",
            DashboardHtml.Page);
    }

    [Fact]
    public void EaContribute_SentinelsAreSplit()
    {
        // Two distinct code paths must be reachable by name.
        Assert.Contains("settingsFetchFailed", DashboardHtml.Page);
        Assert.Contains("contributeUrlEmpty", DashboardHtml.Page);
    }

    [Fact]
    public void EaContribute_ToastHelperPresent()
    {
        // Minimal toast helper the empty-URL and fetch-failed paths call into.
        Assert.Contains("function showToast(", DashboardHtml.Page);
        // Restore-default action button copy is user-visible; pin it.
        Assert.Contains("Restore default URL", DashboardHtml.Page);
    }

    [Fact]
    public void EaContribute_ButtonTitleDescribesToastFlow()
    {
        // Old title claimed "opens the submission form instead". Kill that copy — it lied.
        Assert.DoesNotContain("opens the submission form instead", DashboardHtml.Page);
        // New title should reference the toast/restore flow.
        Assert.Contains("Restore-default toast", DashboardHtml.Page);
    }

    [Fact]
    public void EaContribute_NoAtlasPackDownloadOnEmptyUrl()
    {
        // Verify gate: no atlas-pack.json download initiated on the fallback path.
        // The only place that string may appear is the existing #eaExport handler.
        var page = DashboardHtml.Page;
        var idxContribute = page.IndexOf("$('#eaContribute')");
        var idxAfter = page.IndexOf("/* ── gear tab", idxContribute);
        Assert.True(idxContribute > 0 && idxAfter > idxContribute);
        var contributeSlice = page.Substring(idxContribute, idxAfter - idxContribute);
        Assert.DoesNotContain("atlas-pack.json", contributeSlice);
        Assert.DoesNotContain("a.download", contributeSlice);
    }
}
