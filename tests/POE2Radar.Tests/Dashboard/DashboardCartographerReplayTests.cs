using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardCartographerReplayTests
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
    public void CartographerPlaybackControlsExistInHtml()
    {
        var html = Html();
        Assert.Contains("id=\"cartoPlay\"", html);
        Assert.Contains("id=\"cartoPause\"", html);
        Assert.Contains("id=\"cartoFirst\"", html);
        Assert.Contains("id=\"cartoLast\"", html);
        Assert.Contains("id=\"cartoScrub\"", html);
        Assert.Contains("id=\"cartoSpeed\"", html);
        Assert.Contains("id=\"cartoTimestamp\"", html);
    }

    [Fact]
    public void DashboardJsHasCartographerPlaybackSymbols()
    {
        var js = Js();
        Assert.Contains("renderRoute", js);
        Assert.Contains("_playbackIndex", js);
        Assert.Contains("requestAnimationFrame", js);
    }

    [Fact]
    public void DashboardJsHandlesCartographerSpeeds()
    {
        var js = Js();
        // At least 3 of the 4 speed values must be present in dashboard.js
        // Check for speed preset values used in the JS code
        var has1 = js.Contains("'1'") || js.Contains("=\"1\"");
        var has4 = js.Contains("'4'") || js.Contains("=\"4\"");
        var has16 = js.Contains("'16'") || js.Contains("=\"16\"");
        var hasMax = js.Contains("'max'") || js.Contains("=\"max\"") || js.Contains("\"max\"");
        var count = (has1 ? 1 : 0) + (has4 ? 1 : 0) + (has16 ? 1 : 0) + (hasMax ? 1 : 0);
        Assert.True(count >= 3, "Expected at least 3 of the 4 speed preset values (1, 4, 16, max) in dashboard.js; got " + count);
    }

    [Fact]
    public void DashboardCssHasCartoPlaybackSelector()
    {
        var css = Css();
        Assert.Contains(".carto-playback", css);
    }
}