using System;
using System.IO;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardPanelInventoryTests
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

    const string TabStrip = "dashboard-nav-tabstrip";
    const string PalettePicker = "dashboard-palette-picker";
    const string PalettePreview = "dashboard-palette-preview";
    const string RulesList = "dashboard-rules-list";
    const string ForgePanel = "dashboard-forge-panel";
    const string PresetGallery = "dashboard-preset-gallery";
    const string CartographerCanvas = "dashboard-cartographer-canvas";
    const string CodexTab = "dashboard-codex-tab";
    const string RadarFilterList = "dashboard-radar-filter-list";
    const string HintSupporter = "dashboard-hint-supporter";
    const string HintCartographer = "dashboard-hint-cartographer";
    const string UpdateBanner = "dashboard-update-banner";

    private static readonly string[] AllSlugs =
    [
        TabStrip, PalettePicker, PalettePreview, RulesList, ForgePanel,
        PresetGallery, CartographerCanvas, CodexTab, RadarFilterList,
        HintSupporter, HintCartographer, UpdateBanner
    ];

    [Fact]
    public void Html_TabStripHasPanelId()
    {
        var html = Html();
        Assert.Contains("data-panel-id=\"" + TabStrip + "\"", html);
    }

    [Fact]
    public void Html_PalettePickerHasPanelId()
    {
        var html = Html();
        Assert.Contains("data-panel-id=\"" + PalettePicker + "\"", html);
    }

    [Theory]
    [InlineData("dashboard-nav-tabstrip")]
    [InlineData("dashboard-palette-picker")]
    [InlineData("dashboard-palette-preview")]
    [InlineData("dashboard-rules-list")]
    [InlineData("dashboard-forge-panel")]
    [InlineData("dashboard-preset-gallery")]
    [InlineData("dashboard-cartographer-canvas")]
    [InlineData("dashboard-codex-tab")]
    [InlineData("dashboard-radar-filter-list")]
    [InlineData("dashboard-hint-supporter")]
    [InlineData("dashboard-hint-cartographer")]
    [InlineData("dashboard-update-banner")]
    public void Html_All12CanonicalSlugsPresent(string slug)
    {
        var html = Html();
        Assert.Contains("data-panel-id=\"" + slug + "\"", html);
    }

    [Fact]
    public void Html_ExistingIdsPreserved()
    {
        var html = Html();
        // Spot-check: palettePreview id still exists alongside its new data-panel-id
        Assert.Contains("id=\"palettePreview\"", html);
        Assert.Contains("data-panel-id=\"dashboard-palette-preview\"", html);

        // cartoCanvas id still exists
        Assert.Contains("id=\"cartoCanvas\"", html);
        Assert.Contains("data-panel-id=\"dashboard-cartographer-canvas\"", html);

        // updateBanner id still exists
        Assert.Contains("id=\"updateBanner\"", html);
        Assert.Contains("data-panel-id=\"dashboard-update-banner\"", html);
    }

    [Fact]
    public void DashboardJsHasPanelInventoryHelper()
    {
        var js = Js();
        Assert.Contains("window.__panelInventory", js);
    }

    [Fact]
    public void DashboardJsPanelInventoryHasListAndGet()
    {
        var js = Js();
        Assert.Contains("list:", js);
        Assert.Contains("get:", js);
    }
}