using POE2Radar.Core.Game;

public class EntityNameResolverOverrideTests
{
    // A stable embedded sample, documented in EntityNameResolver's own summary.
    private const string EmbeddedKey = "Metadata/Monsters/Wraith/WraithSpookyLightning";
    private const string EmbeddedName = "Lightning Wraith";

    [Fact]
    public void UserOverride_BeatsEmbedded()
    {
        var r = EntityNameResolver.Shared;
        try { r.SetUserOverrides(new Dictionary<string, string> { [EmbeddedKey] = "Custom Wraith" });
              Assert.Equal("Custom Wraith", r.Resolve(EmbeddedKey)); }
        finally { r.SetUserOverrides(null); }
    }

    [Fact]
    public void Embedded_StillResolves_WithoutOverride()
    {
        var r = EntityNameResolver.Shared;
        r.SetUserOverrides(null);
        Assert.Equal(EmbeddedName, r.Resolve(EmbeddedKey));
    }

    [Fact]
    public void OverrideOnlyKey_Resolves()
    {
        var r = EntityNameResolver.Shared;
        try { r.SetUserOverrides(new Dictionary<string, string> { ["Metadata/Made/Up/Thing"] = "My Thing" });
              Assert.Equal("My Thing", r.Resolve("Metadata/Made/Up/Thing")); }
        finally { r.SetUserOverrides(null); }
    }

    [Fact]
    public void Override_PrefixFallback_Applies()
    {
        var r = EntityNameResolver.Shared;
        try { r.SetUserOverrides(new Dictionary<string, string> { ["Metadata/Made/Up"] = "Base Thing" });
              Assert.Equal("Base Thing", r.Resolve("Metadata/Made/Up/Variant")); }
        finally { r.SetUserOverrides(null); }
    }

    [Fact]
    public void BlankOverride_DoesNotShadowEmbedded()
    {
        var r = EntityNameResolver.Shared;
        try { r.SetUserOverrides(new Dictionary<string, string> { [EmbeddedKey] = "" });
              Assert.Equal(EmbeddedName, r.Resolve(EmbeddedKey)); }
        finally { r.SetUserOverrides(null); }
    }
}
