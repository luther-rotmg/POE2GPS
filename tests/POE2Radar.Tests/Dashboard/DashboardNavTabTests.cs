using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardNavTabTests
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
    public void NavTabButtonExistsInHtml()
    {
        var html = Html();
        Assert.Contains("data-tab=\"nav\"", html);
        Assert.Contains("<button class=\"tab\" data-tab=\"nav\">Nav</button>", html);
    }

    [Fact]
    public void NavViewSectionExistsInHtml()
    {
        var html = Html();
        Assert.Contains("data-view=\"nav\"", html);
        Assert.Contains("<section class=\"view\" data-view=\"nav\" hidden>", html);
    }

    [Fact]
    public void NavStructuralIdsPresent()
    {
        var html = Html();
        Assert.Contains("id=\"btnNavNew\"", html);
        Assert.Contains("id=\"btnNavCapture\"", html);
        Assert.Contains("id=\"navCapChip\"", html);
        Assert.Contains("id=\"navList\"", html);
        // The supporter-hint mount point is the existing #navDestinationHint div from S3;
        // tab activation renders into it for non-supporters.
        Assert.Contains("id=\"navDestinationHint\"", html);
    }

    [Fact]
    public void DashboardJsHasNavSymbols()
    {
        var js = Js();
        // The Nav Destinations IIFE defines loadDestinations / captureCurrentPosition / saveDestination.
        Assert.Contains("loadDestinations", js);
        Assert.Contains("captureCurrentPosition", js);
        Assert.Contains("/api/nav-destinations", js);
        // The NavDestinations module must export renderHintOrList for tab activation.
        Assert.Contains("renderHintOrList", js);
        // Supporters gate check.
        Assert.Contains("__supporterGate", js);
    }

    [Fact]
    public void DashboardJsMountsNavSupporterHint()
    {
        var js = Js();
        // The IIFE references the #navDestinationHint mount point by string literal and
        // delegates to window.__supporterHint.render() for non-supporters.
        Assert.Contains("navDestinationHint", js);
        Assert.Contains("window.__supporterHint", js);
    }

    [Fact]
    public void DashboardCssHasNavSelectors()
    {
        var css = Css();
        Assert.Contains(".nav-tab", css);
        Assert.Contains(".nav-header", css);
        Assert.Contains(".nav-row", css);
        Assert.Contains(".nav-cell", css);
        Assert.Contains("#navCapChip", css);
    }

    [Fact]
    public void NavAdditionsHaveNoMojibake()
    {
        // Garbled UTF-8 from copy-paste (e.g. the spec's ≡ƒÜ¿ / ╬ô├ç├╢) must not leak into
        // the shipped assets. Scan the nav-destinations regions for the known mojibake byte
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

        // The nav tab button label must be plain ASCII "Nav".
        Assert.Contains("<button class=\"tab\" data-tab=\"nav\">Nav</button>", html);
    }
}
