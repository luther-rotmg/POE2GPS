using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardLayoutsTabTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "POE2Radar.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Root() => RepoRoot();

    private static string AssetPath(string file)
        => Path.Combine(Root(), "src", "POE2Radar.Overlay", "Web", "Assets", file);

    private static string Html() => File.ReadAllText(AssetPath("dashboard.html"));
    private static string Css() => File.ReadAllText(AssetPath("dashboard.css"));
    private static string Js()  => File.ReadAllText(AssetPath("dashboard.js"));

    [Fact]
    public void LayoutsTabButtonExistsInHtml()
    {
        var html = Html();
        Assert.Contains("data-tab=\"layouts\"", html);
        Assert.Contains("<button class=\"tab\" data-tab=\"layouts\">Layouts</button>", html);
    }

    [Fact]
    public void LayoutsViewSectionExistsInHtml()
    {
        var html = Html();
        Assert.Contains("data-view=\"layouts\"", html);
        Assert.Contains("<section class=\"view\" data-view=\"layouts\" hidden>", html);
    }

    [Fact]
    public void LayoutsStructuralIdsPresent()
    {
        var html = Html();
        Assert.Contains("id=\"btnLoNewLayout\"", html);
        Assert.Contains("id=\"btnLoCapture\"", html);
        Assert.Contains("id=\"loList\"", html);
        Assert.Contains("id=\"loCapChip\"", html);
        // The supporter-hint mount point is the existing #overlayLayoutHint div from S3;
        // tab activation renders into it for non-supporters.
        Assert.Contains("id=\"overlayLayoutHint\"", html);
    }

    [Fact]
    public void DashboardJsHasLayoutsSymbols()
    {
        var js = Js();
        // The Overlay Layouts IIFE defines loadLayouts / captureCurrent / saveAllLayouts
        // and talks to the B_API endpoint.
        Assert.Contains("loadLayouts", js);
        Assert.Contains("captureCurrent", js);
        Assert.Contains("saveAllLayouts", js);
        Assert.Contains("/api/overlay-layouts", js);
    }

    [Fact]
    public void DashboardJsUsesPanelInventoryInLayouts()
    {
        var js = Js();
        // Scope to the Overlay Layouts IIFE region (between the START/END markers) so the
        // panelInventory reference is genuinely in the layouts module, not a different IIFE.
        var startIdx = js.IndexOf("OVERLAY-LAYOUTS-JS-START");
        var endIdx = js.IndexOf("OVERLAY-LAYOUTS-JS-END");
        Assert.True(startIdx >= 0 && endIdx > startIdx, "Expected OVERLAY-LAYOUTS-JS-START/END markers in dashboard.js");
        var region = js.Substring(startIdx, endIdx - startIdx);
        Assert.Contains("window.__panelInventory", region);
        Assert.Contains("loadLayouts", region);
    }

    [Fact]
    public void DashboardJsMountsSupporterHint_OverlayLayoutHint()
    {
        var js = Js();
        // The IIFE references the #overlayLayoutHint mount point by string literal and
        // delegates to window.__supporterHint.render() for non-supporters.
        Assert.Contains("overlayLayoutHint", js);
        Assert.Contains("window.__supporterHint", js);
    }

    [Fact]
    public void DashboardJsReloadsLayoutsAfterSave()
    {
        var js = Js();
        // On a successful save, the editor calls the B3 reload hook so the live auto-swap
        // picks up the new presets.
        var startIdx = js.IndexOf("OVERLAY-LAYOUTS-JS-START");
        var endIdx = js.IndexOf("OVERLAY-LAYOUTS-JS-END");
        Assert.True(startIdx >= 0 && endIdx > startIdx, "Expected OVERLAY-LAYOUTS-JS-START/END markers in dashboard.js");
        var region = js.Substring(startIdx, endIdx - startIdx);
        Assert.Contains("window.__reloadOverlayLayouts", region);
    }

    [Fact]
    public void DashboardCssHasLayoutsSelectors()
    {
        var css = Css();
        Assert.Contains(".lo-card", css);
        Assert.Contains(".layouts-tab", css);
        Assert.Contains(".lo-header", css);
        Assert.Contains(".lo-panel-row", css);
        Assert.Contains(".lo-panel-key", css);
    }

    [Fact]
    public void LayoutsAdditionsHaveNoMojibake()
    {
        // Garbled UTF-8 from copy-paste (e.g. the spec's emoji / curly-quote muddle) must
        // not leak into the shipped assets. Scan the layouts regions for the known mojibake
        // byte sequences. ASCII punctuation/HTML entities are fine.
        var html = Html();
        var css = Css();
        var js = Js();

        // Match any of: the Unicode replacement char (U+FFFD), or the common
        // Latin-1-misread-of-UTF-8 artifacts. ASCII punctuation and HTML entities
        // (which is what the assets actually use for arrows/dashes) are NOT matched.
        // Patterns written as \uXXXX escapes so this source file stays pure ASCII.
        var mojibake = new Regex("\uFFFD|\u00C3\u0082|\u00C3\u00A3|\u00E2\u0080\u009C|\u00E2\u0080\u009D");
        Assert.DoesNotMatch(mojibake, html);
        Assert.DoesNotMatch(mojibake, css);
        Assert.DoesNotMatch(mojibake, js);

        // The layouts tab button label must be plain ASCII "Layouts".
        Assert.Contains("<button class=\"tab\" data-tab=\"layouts\">Layouts</button>", html);
    }
}
