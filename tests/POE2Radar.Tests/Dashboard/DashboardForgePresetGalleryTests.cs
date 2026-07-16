using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardForgePresetGalleryTests
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
    public void ForgePresetGalleryContainerExistsInHtml()
    {
        var html = Html();
        Assert.Contains("id=\"forgePresetGallery\"", html);
        Assert.Contains("class=\"forge-preset-gallery\"", html);
        Assert.Contains("role=\"list\"", html);
    }

    [Fact]
    public void ForgePresetGalleryCssSelectorsExist()
    {
        var css = Css();
        Assert.Contains(".forge-preset-gallery", css);
        Assert.Contains(".forge-preset-card", css);
        Assert.Contains(".forge-preset-card .fp-sw", css);
    }

    [Fact]
    public void JsFunctionsAndAllBuiltinSlugsExist()
    {
        var js = Js();
        Assert.Contains("function readPaletteVarsFromCss", js);
        Assert.Contains("function renderForgePresetGallery", js);
        // All 10 built-in slugs as string literals.
        Assert.Contains("'kalguuran'", js);
        Assert.Contains("'terminal'", js);
        Assert.Contains("'ultimatum-red'", js);
        Assert.Contains("'sanctum-cream'", js);
        Assert.Contains("'necropolis-amethyst'", js);
        Assert.Contains("'delirium-static'", js);
        Assert.Contains("'legion-bronze'", js);
        Assert.Contains("'ritual-blood'", js);
        Assert.Contains("'trial-ordeal'", js);
        Assert.Contains("'blight-bloom'", js);
    }

    [Fact]
    public void JsDoesNotIntroduceNewHardcodedHexMap()
    {
        var js = Js();
        // The gallery must NOT add a static hex map object; swatch colors must come
        // exclusively from readPaletteVarsFromCss walking the stylesheet.
        Assert.DoesNotContain("FORGE_PRESET_HEX", js);
        // FORGE_VAR_NAMES and FORGE_PREVIEW_VARS are arrays of CSS var names (strings),
        // not a hex map — that's fine. The forbidden pattern is a map of slug -> hex tuples.
        // No const/let/var that looks like a hex tuple map for all 13 vars.
        Assert.DoesNotContain("const FORGE_PRESET_HEX", js);
        Assert.DoesNotContain("var FORGE_PRESET_HEX", js);
        Assert.DoesNotContain("let FORGE_PRESET_HEX", js);
    }
}