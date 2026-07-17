using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardCartographerTabTests
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
    public void CartographerTabButtonExistsInHtml()
    {
        var html = Html();
        Assert.Contains("data-tab=\"cartographer\"", html);
        Assert.Contains("<button class=\"tab\" data-tab=\"cartographer\">Cartographer</button>", html);
    }

    [Fact]
    public void CartographerViewSectionExistsInHtml()
    {
        var html = Html();
        Assert.Contains("data-view=\"cartographer\"", html);
        Assert.Contains("<section class=\"view\" data-view=\"cartographer\" hidden>", html);
    }

    [Fact]
    public void CartographerStructuralIdsPresent()
    {
        var html = Html();
        Assert.Contains("id=\"cartoCharSelect\"", html);
        Assert.Contains("id=\"cartoZoneSelect\"", html);
        Assert.Contains("id=\"cartoCanvas\"", html);
        Assert.Contains("id=\"cartoInfo\"", html);
    }

    [Fact]
    public void DashboardJsHasCartographerFunctionSymbols()
    {
        var js = Js();
        Assert.Contains("loadCharacters", js);
        Assert.Contains("loadZones", js);
        Assert.Contains("loadTracks", js);
        Assert.Contains("renderHeatmap", js);
    }

    [Fact]
    public void DashboardJsFetchesTracksEndpoints()
    {
        var js = Js();
        Assert.Contains("/api/tracks/characters", js);
        Assert.Contains("/api/tracks/zones", js);
        Assert.Contains("/api/tracks?", js);
    }

    [Fact]
    public void DashboardCssHasCartographerSelectors()
    {
        var css = Css();
        Assert.Contains(".carto-canvas", css);
    }
}
