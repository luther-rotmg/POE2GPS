using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardCartographerHintTests
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
    public void CartographerHintCardExistsInHtml()
    {
        // Card lives directly inside <main> (unconditional first-load hint) rather than
        // inside a per-tab view section — the dashboard has no data-view="overlay"
        // section to attach to, so tab-scoped visibility would hide the card entirely.
        var html = Html();
        Assert.Contains("id=\"cartographerHint\"", html);
        Assert.Contains("class=\"cartographer-hint\"", html);
    }

    [Fact]
    public void CartographerHintCloseButtonExistsInHtml()
    {
        var html = Html();
        Assert.Contains("id=\"cartographerHintClose\"", html);
    }

    [Fact]
    public void DashboardJsWiresCartographerHintDismiss()
    {
        var js = Js();
        Assert.Contains("'cartographer-hint-dismissed'", js);
    }
}