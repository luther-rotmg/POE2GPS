using System;
using System.IO;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardLayoutAutoSwapTests
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

    private static string Js() => File.ReadAllText(AssetPath("dashboard.js"));

    [Fact]
    public void DashboardJsHasLayoutAutoSwapModule()
    {
        var js = Js();
        Assert.True(
            js.Contains("applyLayoutForZone") || js.Contains("refreshLayouts"),
            "dashboard.js must contain applyLayoutForZone or refreshLayouts (LayoutAutoSwap module)."
        );
    }

    [Fact]
    public void DashboardJsFetchesOverlayLayoutsEndpoint()
    {
        var js = Js();
        Assert.Contains("/api/overlay-layouts", js);
    }

    [Fact]
    public void DashboardJsFetchesStateForZone()
    {
        var js = Js();
        Assert.Contains("/api/state", js);
    }

    [Fact]
    public void DashboardJsUsesPanelInventory()
    {
        var js = Js();
        Assert.Contains("window.__panelInventory", js);
    }

    [Fact]
    public void DashboardJsGatesOnSupporterGate()
    {
        var js = Js();
        Assert.Contains("__supporterGate.isSupporter", js);
    }

    [Fact]
    public void DashboardJsExposesReloadOverlayLayouts()
    {
        var js = Js();
        Assert.Contains("__reloadOverlayLayouts", js);
    }

    [Fact]
    public void DashboardJsPollsAtTwoSecondCadence()
    {
        var js = Js();
        // Look for 2000 near a setInterval call in the layout swap IIFE region
        // (after the last existing IIFE boundary marker)
        var idx = js.LastIndexOf("RADAR-FILTER-JS-END");
        Assert.True(idx >= 0, "Expected RADAR-FILTER-JS-END marker in dashboard.js");
        var tail = js.Substring(idx);
        Assert.Contains("2000", tail);
        Assert.Contains("setInterval", tail);
    }

    [Fact]
    public void DashboardJsWildcardStarConvertsToRegexAny()
    {
        var js = Js();
        // Look for glob→regex conversion: replace('*' or .replace(/\*/
        var idx = js.LastIndexOf("RADAR-FILTER-JS-END");
        Assert.True(idx >= 0, "Expected RADAR-FILTER-JS-END marker in dashboard.js");
        var tail = js.Substring(idx);
        Assert.True(
            tail.Contains(@".replace(/\*") || tail.Contains("replace('*'"),
            "LayoutAutoSwap IIFE must contain a glob→regex conversion (replace('*') or .replace(/\\*/)."
        );
    }
}