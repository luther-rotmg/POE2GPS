using System;
using System.IO;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardSupporterHintTests
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
    public void DashboardHtmlHasPaletteHintMountPoint()
    {
        var html = Html();
        Assert.Contains("id=\"dashboardPaletteHint\"", html);
    }

    [Fact]
    public void DashboardHtmlHasV041HintMountPoints()
    {
        var html = Html();
        Assert.Contains("id=\"radarFilterHint\"", html);
        Assert.Contains("id=\"overlayLayoutHint\"", html);
        Assert.Contains("id=\"navDestinationHint\"", html);
        Assert.Contains("id=\"sessionWidgetHint\"", html);
    }

    [Fact]
    public void DashboardCssHasSupporterHintSelector()
    {
        var css = Css();
        Assert.Contains(".supporter-hint {", css);
    }

    [Fact]
    public void DashboardJsHasSupporterHintRender()
    {
        var js = Js();
        Assert.Contains("window.__supporterHint", js);
    }

    [Fact]
    public void DashboardJsAppliesHintToPalettePicker()
    {
        var js = Js();
        Assert.Contains("dashboardPaletteHint", js);
    }
}
