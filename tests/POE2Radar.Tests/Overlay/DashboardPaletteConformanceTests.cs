using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Overlay;

public class DashboardPaletteConformanceTests
{
    // v0.35 signature palette pack — locked slug list. Any addition/removal must update this
    // list AND dashboard.css AND dashboard.html AND (P3) the PALETTE_PREVIEWS map in dashboard.js.
    private static readonly string[] Slugs =
    {
        "kalguuran", "terminal",
        "ultimatum-red", "sanctum-cream", "necropolis-amethyst", "delirium-static",
        "legion-bronze", "ritual-blood", "trial-ordeal", "blight-bloom",
    };

    // Every palette CSS block must define all 13 vars. Missing vars fall through to :root
    // defaults and clash with the palette aesthetic — treated as a regression.
    private static readonly string[] RequiredVars =
    {
        "--gold", "--gold-bright", "--gold-deep",
        "--ink", "--ink-dim", "--ink-faint",
        "--panel", "--panel2", "--bg", "--bg-alt",
        "--line", "--line-soft",
        "--good",
    };

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "POE2Radar.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    internal static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void EveryHtmlOptionSlugHasCssBlock()
    {
        var html = Read("src/POE2Radar.Overlay/Web/Assets/dashboard.html");
        var css  = Read("src/POE2Radar.Overlay/Web/Assets/dashboard.css");

        // Scope to the dashboardPalette <select> only — other selects have their own values.
        var selectMatch = Regex.Match(html,
            "<select[^>]*data-set=\"dashboardPalette\"[^>]*>(?<body>.*?)</select>",
            RegexOptions.Singleline);
        Assert.True(selectMatch.Success, "Palette <select data-set=\"dashboardPalette\"> not found.");

        var optionMatches = Regex.Matches(selectMatch.Groups["body"].Value,
            "<option\\s+value=\"([^\"]*)\">[^<]*</option>");
        var htmlSlugs = optionMatches
            .Select(m => m.Groups[1].Value)
            .Where(v => v.Length > 0) // empty value is the Default option — no CSS block expected
            .ToArray();

        Assert.NotEmpty(htmlSlugs);
        foreach (var slug in htmlSlugs)
        {
            Assert.True(css.Contains($"body[data-palette=\"{slug}\"]"),
                $"dashboard.css is missing body[data-palette=\"{slug}\"] {{ ... }} for HTML option value=\"{slug}\".");
        }
    }

    [Theory]
    [MemberData(nameof(SlugData))]
    public void EveryPaletteBlockDefinesAllThirteenVars(string slug)
    {
        var css = Read("src/POE2Radar.Overlay/Web/Assets/dashboard.css");
        var blockRegex = new Regex(
            "body\\[data-palette=\"" + Regex.Escape(slug) + "\"\\]\\s*\\{(?<body>[^}]*)\\}",
            RegexOptions.Singleline);
        var block = blockRegex.Match(css);
        Assert.True(block.Success, $"Missing CSS block for palette '{slug}'.");
        var body = block.Groups["body"].Value;
        foreach (var v in RequiredVars)
        {
            Assert.True(body.Contains(v + ":"),
                $"Palette '{slug}' block is missing required var {v}.");
        }
    }

    public static TheoryData<string> SlugData()
    {
        var data = new TheoryData<string>();
        foreach (var s in Slugs) data.Add(s);
        return data;
    }

    [Fact]
    public void DashboardJsStillGatesPaletteOnIsSupporter()
    {
        // Regression guard — this is the ONLY runtime enforcement of the supporter-only
        // palette rule. If it moves or is removed, non-supporters could apply supporter
        // palettes. Substring lifted verbatim from dashboard.js:2144.
        var js = Read("src/POE2Radar.Overlay/Web/Assets/dashboard.js");
        Assert.Contains("s.isSupporter ? (s.dashboardPalette", js);
    }
}