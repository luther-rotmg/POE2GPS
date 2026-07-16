using System;
using System.IO;
using POE2Radar.Core.Game;
using POE2Radar.Core.Icons;
using Xunit;

namespace POE2Radar.Tests.Icons;

public class IconRegistryTests
{
    private static string MakeTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "iconreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void WritePng(string dir, string name)
        => File.WriteAllBytes(Path.Combine(dir, name + ".png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });

    [Fact]
    public void EmptyDirectory_ReturnsEmptySnapshot()
    {
        var d = MakeTempDir();
        using var r = new IconRegistry();
        r.LoadFrom(d);
        Assert.Empty(r.Current.Icons);
        Assert.Empty(r.Current.Rules);
    }

    [Fact]
    public void Resolve_ReturnsDefault_WhenNoRuleMatches()
    {
        var d = MakeTempDir();
        WritePng(d, "circle");
        File.WriteAllText(Path.Combine(d, "mapping.json"), "{\"default\":\"circle\"}");
        using var r = new IconRegistry();
        r.LoadFrom(d);
        var e = r.Resolve(Poe2Live.EntityCategory.Monster, Poe2Live.Rarity.Normal, "Metadata/Monsters/Foo");
        Assert.NotNull(e);
        Assert.Equal("circle", e!.Name);
    }

    [Theory]
    [InlineData(Poe2Live.EntityCategory.Monster, "skull")]
    [InlineData(Poe2Live.EntityCategory.Chest, "chest")]
    [InlineData(Poe2Live.EntityCategory.Npc, "circle")]
    public void Resolve_MatchesByCategory(Poe2Live.EntityCategory cat, string expected)
    {
        var d = MakeTempDir();
        foreach (var n in new[] { "circle", "skull", "chest" }) WritePng(d, n);
        // v0.36 locked schema: nested categories with "default" per-category key.
        File.WriteAllText(Path.Combine(d, "mapping.json"),
            "{\"default\":\"circle\",\"categories\":{\"Monster\":{\"default\":\"skull\"},\"Chest\":{\"default\":\"chest\"}}}");
        using var r = new IconRegistry();
        r.LoadFrom(d);
        Assert.Equal(expected, r.Resolve(cat, Poe2Live.Rarity.Normal, "")!.Name);
    }

    [Fact]
    public void Resolve_CategoryRarity_BeatsPlainCategory()
    {
        var d = MakeTempDir();
        foreach (var n in new[] { "skull", "orange-skull" }) WritePng(d, n);
        // v0.36 locked schema: rarity keys nested inside the category dict.
        File.WriteAllText(Path.Combine(d, "mapping.json"),
            "{\"categories\":{\"Monster\":{\"default\":\"skull\",\"Unique\":\"orange-skull\"}}}");
        using var r = new IconRegistry();
        r.LoadFrom(d);
        Assert.Equal("orange-skull", r.Resolve(Poe2Live.EntityCategory.Monster, Poe2Live.Rarity.Unique, "")!.Name);
        Assert.Equal("skull",        r.Resolve(Poe2Live.EntityCategory.Monster, Poe2Live.Rarity.Rare,   "")!.Name);
    }

    [Fact]
    public void Resolve_MetadataGlob_BeatsEverything()
    {
        var d = MakeTempDir();
        foreach (var n in new[] { "skull", "boss-crown" }) WritePng(d, n);
        // v0.36 locked schema: JSON field is `metadataGlobs`.
        File.WriteAllText(Path.Combine(d, "mapping.json"),
            "{\"categories\":{\"Monster\":{\"default\":\"skull\"}},\"metadataGlobs\":[{\"glob\":\"Metadata/Monsters/Bosses/*\",\"icon\":\"boss-crown\"}]}");
        using var r = new IconRegistry();
        r.LoadFrom(d);
        Assert.Equal("boss-crown", r.Resolve(Poe2Live.EntityCategory.Monster, Poe2Live.Rarity.Unique, "Metadata/Monsters/Bosses/Kitava")!.Name);
        Assert.Equal("skull",       r.Resolve(Poe2Live.EntityCategory.Monster, Poe2Live.Rarity.Unique, "Metadata/Monsters/Regular/Foo")!.Name);
    }

    [Fact]
    public void Resolve_LegacyFlatCategoryString_StillWorks()
    {
        // Backwards compat: users with the pre-lock flat form don't get bricked.
        var d = MakeTempDir();
        WritePng(d, "skull");
        File.WriteAllText(Path.Combine(d, "mapping.json"),
            "{\"categories\":{\"Monster\":\"skull\"}}");
        using var r = new IconRegistry();
        r.LoadFrom(d);
        Assert.Equal("skull", r.Resolve(Poe2Live.EntityCategory.Monster, Poe2Live.Rarity.Normal, "")!.Name);
    }

    [Fact]
    public void MalformedMapping_PreservesIcons_EmptiesRules()
    {
        var d = MakeTempDir();
        WritePng(d, "circle");
        File.WriteAllText(Path.Combine(d, "mapping.json"), "{not-json");
        using var r = new IconRegistry();
        r.LoadFrom(d);
        Assert.Single(r.Current.Icons);
        Assert.Empty(r.Current.Rules);
    }

    [Fact]
    public void Snapshot_IsImmutable_AndVersionBumps()
    {
        var d = MakeTempDir();
        using var r = new IconRegistry();
        r.LoadFrom(d);
        var s1 = r.Current;
        WritePng(d, "star");
        r.LoadFrom(d);
        var s2 = r.Current;
        Assert.NotSame(s1, s2);
        Assert.Empty(s1.Icons);
        Assert.Single(s2.Icons);
        Assert.True(s2.Version > s1.Version);
    }

    [Fact]
    public void Resolve_NonPngFilesIgnored()
    {
        var d = MakeTempDir();
        WritePng(d, "circle");
        File.WriteAllBytes(Path.Combine(d, "junk.svg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(d, "junk.txt"), new byte[] { 1 });
        using var r = new IconRegistry();
        r.LoadFrom(d);
        Assert.Single(r.Current.Icons);
        Assert.Contains("circle", r.Current.Icons.Keys);
    }
}