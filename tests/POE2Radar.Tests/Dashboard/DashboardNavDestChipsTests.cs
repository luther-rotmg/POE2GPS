using System;
using System.IO;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardNavDestChipsTests
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
    public void HtmlHasNavDestChipsMountPoint()
    {
        var html = Html();
        Assert.Contains("id=\"navDestChips\"", html);
    }

    [Fact]
    public void CssHasNavDestChipsClass()
    {
        var css = Css();
        Assert.Contains(".nav-dest-chips", css);
    }

    [Fact]
    public void CssHasNavDestChipStyleClass()
    {
        var css = Css();
        Assert.Contains(".nav-dest-chip", css);
    }

    [Fact]
    public void DashboardJsHasRefreshNavDestChipsSymbol()
    {
        var js = Js();
        Assert.Contains("refreshNavDestChips", js);
    }

    [Fact]
    public void DashboardJsFetchesNavDestinationsZoneEndpoint()
    {
        var js = Js();
        Assert.Contains("/api/nav-destinations?zone=", js);
    }

    [Fact]
    public void DashboardJsSupporterGatesNavDestChips()
    {
        var js = Js();
        // The NavDestChips region should reference __supporterGate.isSupporter for gating
        Assert.Contains("__supporterGate.isSupporter", js);
    }
}