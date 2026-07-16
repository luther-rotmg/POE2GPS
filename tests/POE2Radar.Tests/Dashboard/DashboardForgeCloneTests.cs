using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace POE2Radar.Tests.Dashboard;

public class DashboardForgeCloneTests
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
    public void JsDefinesForgeUniqueNameWithIncrementLoop()
    {
        var js = Js();
        Assert.Matches(@"forgeUniqueName[\s\S]{0,600}?while", js);
    }

    [Fact]
    public void JsContainsUserPresetsLocalStorageKey()
    {
        var js = Js();
        Assert.Contains("'poe2gps.forge.userPresets'", js);
    }

    [Fact]
    public void JsHasClickHandlerOnForgePresetGalleryWithSourceSlug()
    {
        var js = Js();
        // Find the block that contains both addEventListener('click' and forgePresetGallery and sourceSlug
        var clickBlock = Regex.Match(js, @"addEventListener\('click'[\s\S]{0,400}?(forgePresetGallery|sourceSlug)[\s\S]{0,400}?(forgePresetGallery|sourceSlug)");
        Assert.True(clickBlock.Success, "Expected addEventListener('click' block to reference both forgePresetGallery and sourceSlug");
    }

    [Fact]
    public void JsDispatchesForgePresetClonedCustomEvent()
    {
        var js = Js();
        Assert.Contains("CustomEvent('forge:preset-cloned'", js);
        Assert.Contains("bubbles: true", js);
    }

    [Fact]
    public void JsHasKeyboardHandlerForEnterAndSpace()
    {
        var js = Js();
        var kdBlock = Regex.Match(js, @"addEventListener\('keydown'[\s\S]{0,400}?'Enter'[\s\S]{0,400}?' '");
        Assert.True(kdBlock.Success, "Expected addEventListener('keydown' block to reference both 'Enter' and ' ' (Space)");
    }

    [Fact]
    public void ForgeBuiltinSlugsArrayRemainsUnchanged()
    {
        var js = Js();
        // The 10-slug literal block from G1 must still appear verbatim.
        var slugBlock = Regex.Match(js,
            @"const FORGE_BUILTIN_SLUGS\s*=\s*\[[^\]]+?\](?:\s*;|(?=\s))");
        Assert.True(slugBlock.Success, "FORGE_BUILTIN_SLUGS declaration not found");
        var raw = slugBlock.Value;

        // Assert all 10 slugs present in the declaration
        Assert.Contains("'kalguuran'", raw);
        Assert.Contains("'terminal'", raw);
        Assert.Contains("'ultimatum-red'", raw);
        Assert.Contains("'sanctum-cream'", raw);
        Assert.Contains("'necropolis-amethyst'", raw);
        Assert.Contains("'delirium-static'", raw);
        Assert.Contains("'legion-bronze'", raw);
        Assert.Contains("'ritual-blood'", raw);
        Assert.Contains("'trial-ordeal'", raw);
        Assert.Contains("'blight-bloom'", raw);

        // Exactly 10 slugs — no more, no fewer
        var count = Regex.Matches(raw, "'[a-z-]+'").Count;
        Assert.Equal(10, count);
    }
}