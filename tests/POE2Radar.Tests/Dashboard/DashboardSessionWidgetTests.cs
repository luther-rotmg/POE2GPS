using System;
using System.IO;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardSessionWidgetTests
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
    public void HtmlHasSessionWidgetMount()
    {
        var html = Html();
        Assert.Contains("id=\"sessionWidget\"", html);
    }

    [Fact]
    public void HtmlHasWidgetTabButton()
    {
        var html = Html();
        Assert.Contains("data-tab=\"widget\"", html);
    }

    [Fact]
    public void HtmlHasWidgetEditorStructuralIds()
    {
        var html = Html();
        Assert.Contains("id=\"btnWidgetSave\"", html);
        Assert.Contains("id=\"widgetX\"", html);
        Assert.Contains("id=\"widgetY\"", html);
        Assert.Contains("data-widget-chip=\"drops\"", html);
        Assert.Contains("data-widget-chip=\"xp-gained\"", html);
        Assert.Contains("data-widget-chip=\"bosses-killed\"", html);
        Assert.Contains("data-widget-chip=\"deaths\"", html);
        Assert.Contains("data-widget-chip=\"time-in-zone\"", html);
        Assert.Contains("data-widget-chip=\"avg-map-clear-time\"", html);
    }

    [Fact]
    public void DashboardJsHasSessionWidgetSymbols()
    {
        var js = Js();
        Assert.Contains("refreshWidget", js);
        Assert.Contains("loadWidgetEditor", js);
        Assert.Contains("saveWidgetEditor", js);
        Assert.Contains("CHIP_DEFS", js);
    }

    [Fact]
    public void DashboardJsFetchesSessionWidgetEndpoint()
    {
        var js = Js();
        Assert.Contains("/api/session-widget", js);
    }

    [Fact]
    public void DashboardJsMountsSessionWidgetHint()
    {
        var js = Js();
        Assert.Contains("sessionWidgetHint", js);
    }

    [Fact]
    public void DashboardJsHasWidgetRefreshInterval()
    {
        var js = Js();
        Assert.Contains("setInterval(refreshWidget, 2000)", js);
    }

    [Fact]
    public void DashboardCssHasSessionWidgetSelector()
    {
        var css = Css();
        Assert.Contains(".session-widget", css);
    }

    [Fact]
    public void DashboardCssHasSwChipClass()
    {
        var css = Css();
        Assert.Contains(".sw-chip", css);
    }

    [Fact]
    public void DashboardCssHasWidgetEditorRowClass()
    {
        var css = Css();
        Assert.Contains(".widget-editor-row", css);
    }
}