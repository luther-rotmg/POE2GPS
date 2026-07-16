using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardRulesMigrationTests
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
    private static string Js()  => File.ReadAllText(AssetPath("dashboard.js"));

    [Fact]
    public void AffixNameplatesTab_HasMigrateButton()
    {
        var html = Html();
        // btnMigrateAffix must exist in the affix nameplates card section.
        Assert.Contains("id=\"btnMigrateAffix\"", html);
        Assert.Contains("Migrate to Rule Engine", html);
    }

    [Fact]
    public void BuffNameplatesTab_HasMigrateButton()
    {
        var html = Html();
        // btnMigrateBuff must exist in the buff nameplates card section.
        Assert.Contains("id=\"btnMigrateBuff\"", html);
        Assert.Contains("Migrate to Rule Engine", html);
    }

    [Fact]
    public void FiltersTab_HasMigrateButton()
    {
        var html = Html();
        // btnMigrateFilter must exist in the filters tab section.
        Assert.Contains("id=\"btnMigrateFilter\"", html);
        Assert.Contains("Migrate to Rule Engine", html);
    }

    [Fact]
    public void DashboardJsHasSwitchToRulesEnginePrefillListener()
    {
        var js = Js();
        // dashboard.js must contain the switch-to-rules-engine-with-prefill event string.
        Assert.True(
            js.Contains("'switch-to-rules-engine-with-prefill'") ||
            js.Contains("\"switch-to-rules-engine-with-prefill\""),
            "dashboard.js must subscribe to 'switch-to-rules-engine-with-prefill' event."
        );
    }
}