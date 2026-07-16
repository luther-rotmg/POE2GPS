using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Overlay;

public class DashboardPalettePreviewTests
{
    // Must stay in lockstep with DashboardPaletteConformanceTests.Slugs (P2). Duplicated
    // intentionally so this test class is self-contained and either bead can land first
    // if the pipeline ever reorders.
    private static readonly string[] Slugs =
    {
        "kalguuran", "terminal",
        "ultimatum-red", "sanctum-cream", "necropolis-amethyst", "delirium-static",
        "legion-bronze", "ritual-blood", "trial-ordeal", "blight-bloom",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "POE2Radar.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Js() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "POE2Radar.Overlay", "Web", "Assets", "dashboard.js"));

    [Fact]
    public void PalettePreviewsMapDeclared()
    {
        Assert.Contains("PALETTE_PREVIEWS", Js());
    }

    [Fact]
    public void PalettePreviewsMapCoversEverySlug()
    {
        var js = Js();
        // Extract all keys from PALETTE_PREVIEWS object.
        var pattern = new Regex("['\"]([^'\"]*?)['\"]\\s*:\\s*\\[");
        var matches = pattern.Matches(js);
        var actualKeys = new HashSet<string>();
        foreach (Match m in matches) actualKeys.Add(m.Groups[1].Value);

        // Built-in slugs: 10 named + empty-string Default.
        var expectedBuiltins = new HashSet<string>(Slugs) { "" };

        // (a) All 11 built-in slugs must be present.
        foreach (var slug in expectedBuiltins)
        {
            Assert.True(actualKeys.Contains(slug),
                $"PALETTE_PREVIEWS map is missing an entry for slug '{slug}'.");
        }

        // (b) Any additional keys must match user-<slug> format (future-proofing
        //     for when the fixture is regenerated from a live dashboard with forge presets).
        var extraKeys = new HashSet<string>(actualKeys);
        extraKeys.ExceptWith(expectedBuiltins);
        foreach (var key in extraKeys)
        {
            Assert.Matches(new Regex("^user-[a-z0-9-]{1,32}$"), key);
        }
    }

    [Fact]
    public void PalettePreviewsMapCoversDefault()
    {
        // Empty-string Default entry so the strip renders a Default chip too.
        var js = Js();
        Assert.True(js.Contains("'': [") || js.Contains("\"\": ["),
            "PALETTE_PREVIEWS must include the empty-string Default entry ('' : [...]).");
    }

    [Fact]
    public void RenderPalettePreviewFunctionExists()
    {
        Assert.Matches(new Regex("function\\s+renderPalettePreview\\s*\\("), Js());
    }

    [Fact]
    public void PreviewChipClickDispatchesChangeEvent()
    {
        // The widget must NOT duplicate the supporter gate. Clicking a chip must set
        // sel.value then dispatch a bubbling 'change' event so the existing
        // applySupporterCosmetics hook (dashboard.js:2197-2202) fires and re-gates.
        var js = Js();
        Assert.Contains("new Event('change', { bubbles: true })", js);
    }

    public static TheoryData<string> SlugData()
    {
        var data = new TheoryData<string>();
        foreach (var s in Slugs) data.Add(s);
        return data;
    }
}