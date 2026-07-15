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

    [Theory]
    [MemberData(nameof(SlugData))]
    public void PalettePreviewsMapCoversEverySlug(string slug)
    {
        var js = Js();
        // Accept either 'slug': [ or "slug": [ as an object key form.
        var pattern = new Regex("['\"]" + Regex.Escape(slug) + "['\"]\\s*:\\s*\\[");
        Assert.True(pattern.IsMatch(js),
            $"PALETTE_PREVIEWS map is missing an entry for slug '{slug}'.");
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