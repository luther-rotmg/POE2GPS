using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace POE2Radar.Tests.Overlay;

public sealed class StarterIconMappingTests
{
    private static readonly string[] FrozenKeys = new[]
    {
        "monster-normal","monster-magic","monster-rare","monster-unique",
        "chest-closed","chest-opened","npc","transition","waystone",
        "breach","boss","shrine","ritual","currency-drop","unique-drop",
        "rare-drop","magic-drop","friendly","hostile","entity-generic"
    };

    private static Assembly OverlayAsm =>
        typeof(POE2Radar.Overlay.AtlasIconCache).Assembly;

    private static string ReadResource(string name)
    {
        using var s = OverlayAsm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException(name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [Fact]
    public void MappingJson_Parses_And_Uses_Only_Frozen_Icon_Keys()
    {
        // v0.36 locked schema: nested categories.rarity + optional metadataGlobs array (see plan).
        // Every icon-name value in every section must be one of the FrozenKeys embedded PNGs.
        var json = ReadResource("POE2Radar.Overlay.StarterIcons.mapping.json");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        void AssertFrozen(string? iconKey)
        {
            Assert.NotNull(iconKey);
            Assert.Contains(iconKey!, FrozenKeys);
        }

        if (root.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.String)
            AssertFrozen(d.GetString());

        Assert.True(root.TryGetProperty("categories", out var cats), "mapping.json missing 'categories'");
        var sawAny = false;
        foreach (var cat in cats.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.Object, cat.Value.ValueKind);
            foreach (var rar in cat.Value.EnumerateObject())
            {
                Assert.Equal(JsonValueKind.String, rar.Value.ValueKind);
                AssertFrozen(rar.Value.GetString());
                sawAny = true;
            }
        }
        Assert.True(sawAny, "categories block must contain at least one binding");

        if (root.TryGetProperty("categoryRarity", out var cr))
            foreach (var kv in cr.EnumerateObject())
                AssertFrozen(kv.Value.GetString());

        if (root.TryGetProperty("metadataGlobs", out var mg))
            foreach (var el in mg.EnumerateArray())
                AssertFrozen(el.GetProperty("icon").GetString());
    }

    [Fact]
    public void Every_Frozen_Icon_Ships_As_Embedded_Png_At_Both_Sizes()
    {
        var names = OverlayAsm.GetManifestResourceNames();
        foreach (var key in FrozenKeys)
        {
            Assert.Contains($"POE2Radar.Overlay.StarterIcons.{key}@32.png", names);
            Assert.Contains($"POE2Radar.Overlay.StarterIcons.{key}@64.png", names);
        }
    }

    [Fact]
    public void Attribution_Ships_As_Embedded_Resource()
    {
        var names = OverlayAsm.GetManifestResourceNames();
        Assert.Contains("POE2Radar.Overlay.StarterIcons.ATTRIBUTION.md", names);
    }
}
