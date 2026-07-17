using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardRadarFilterTabTests
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
    public void RadarFilterTabButtonExistsInHtml()
    {
        var html = Html();
        Assert.Contains("data-tab=\"radar-filter\"", html);
        Assert.Contains("<button class=\"tab\" data-tab=\"radar-filter\">Radar Filter</button>", html);
    }

    [Fact]
    public void RadarFilterViewSectionExistsInHtml()
    {
        var html = Html();
        Assert.Contains("data-view=\"radar-filter\"", html);
        Assert.Contains("<section class=\"view\" data-view=\"radar-filter\" hidden>", html);
    }

    [Fact]
    public void RadarFilterStructuralIdsPresent()
    {
        var html = Html();
        Assert.Contains("id=\"btnRfNewPreset\"", html);
        Assert.Contains("id=\"rfCapChip\"", html);
        Assert.Contains("id=\"rfPresetList\"", html);
        // The supporter-hint mount point is the existing #radarFilterHint div from S3;
        // tab activation renders into it for non-supporters.
        Assert.Contains("id=\"radarFilterHint\"", html);
    }

    [Fact]
    public void DashboardJsHasRadarFilterSymbols()
    {
        var js = Js();
        // The Radar Filter IIFE defines loadPresets / savePresets (IIFE-scoped — the
        // pre-existing top-level palette-presets loadPresets is a separate function).
        Assert.Contains("loadPresets", js);
        Assert.Contains("savePresets", js);
        Assert.Contains("/api/radar-filters", js);
        // The /entities endpoint backs the "Add current-zone entity" dropdown.
        Assert.Contains("/entities", js);
    }

    [Fact]
    public void DashboardJsMountsSupporterHint()
    {
        var js = Js();
        // The IIFE references the #radarFilterHint mount point by string literal and
        // delegates to window.__supporterHint.render() for non-supporters.
        Assert.Contains("radarFilterHint", js);
        Assert.Contains("window.__supporterHint", js);
    }

    [Fact]
    public void DashboardCssHasRadarFilterSelectors()
    {
        var css = Css();
        Assert.Contains(".rf-card", css);
        Assert.Contains(".radar-filter-tab", css);
        Assert.Contains(".rf-header", css);
        Assert.Contains(".rf-chip-list", css);
        Assert.Contains(".rf-chip", css);
        Assert.Contains(".rf-add-input", css);
    }

    [Fact]
    public void RadarFilterAdditionsHaveNoMojibake()
    {
        // Garbled UTF-8 from copy-paste (e.g. the spec's ≡ƒÜ¿ / ╬ô├ç├╢) must not leak into
        // the shipped assets. Scan the radar-filter regions for the known mojibake byte
        // sequences. ASCII punctuation/HTML entities are fine.
        var html = Html();
        var css = Css();
        var js = Js();

        // Match any of: the Unicode replacement char (U+FFFD), or the common
        // Latin-1-misread-of-UTF-8 artifacts (the "Greek Xi" prefix, the black
        // diamond, the curly-quote muddle). ASCII punctuation and HTML entities
        // (which is what the assets actually use for arrows/dashes) are NOT matched.
        // Patterns written as \uXXXX escapes so this source file stays pure ASCII.
        var mojibake = new Regex("\uFFFD|\u00C3\u0082|\u00C3\u00A3|\u00E2\u0080\u009C|\u00E2\u0080\u009D");
        Assert.DoesNotMatch(mojibake, html);
        Assert.DoesNotMatch(mojibake, css);
        Assert.DoesNotMatch(mojibake, js);

        // The radar-filter tab button label must be plain ASCII "Radar Filter".
        Assert.Contains("<button class=\"tab\" data-tab=\"radar-filter\">Radar Filter</button>", html);
    }
}
