using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardRulesTabTests
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
    public void RulesTabButtonExistsInHtml()
    {
        var html = Html();
        Assert.Contains("data-tab=\"rules\"", html);
        Assert.Contains("<button class=\"tab\" data-tab=\"rules\">Rule Engine</button>", html);
    }

    [Fact]
    public void RulesViewSectionExistsInHtml()
    {
        var html = Html();
        Assert.Contains("data-view=\"rules\"", html);
        Assert.Contains("<section class=\"view\" data-view=\"rules\" hidden>", html);
    }

    [Fact]
    public void RulesEditorStructuralIdsPresent()
    {
        var html = Html();
        Assert.Contains("id=\"ruleNameInput\"", html);
        Assert.Contains("id=\"rulePriorityInput\"", html);
        Assert.Contains("id=\"ruleEnabledInput\"", html);
        Assert.Contains("id=\"selectorRows\"", html);
        Assert.Contains("id=\"effectChips\"", html);
        Assert.Contains("id=\"btnAddEffect\"", html);
        Assert.Contains("id=\"btnSaveRule\"", html);
        Assert.Contains("id=\"btnCancelRule\"", html);
        Assert.Contains("id=\"ruleEditorStatus\"", html);
        // header + list container too
        Assert.Contains("id=\"btnNewRule\"", html);
        Assert.Contains("id=\"ruleCapChip\"", html);
        Assert.Contains("id=\"rulesList\"", html);
        Assert.Contains("id=\"rulesEditor\"", html);
    }

    [Fact]
    public void DashboardJsHasRulesTabFunctionSymbols()
    {
        var js = Js();
        Assert.Contains("loadRules", js);
        Assert.Contains("openEditor", js);
        Assert.Contains("saveRule", js);
        Assert.Contains("deleteRule", js);
    }

    [Fact]
    public void DashboardJsFiresRulesChangedEvent()
    {
        var js = Js();
        // Must dispatch a 'rules-changed' CustomEvent for R3/R4 subscription.
        Assert.True(js.Contains("'rules-changed'") || js.Contains("\"rules-changed\""),
            "dashboard.js must fire a 'rules-changed' CustomEvent for R3/R4 recompile subscription.");
    }

    [Fact]
    public void DashboardCssHasRulesTabSelectors()
    {
        var css = Css();
        Assert.Contains(".rule-card", css);
        Assert.Contains(".rules-tab", css);
        Assert.Contains(".rules-editor", css);
        Assert.Contains(".selector-row", css);
        Assert.Contains(".effect-chip", css);
    }
}
