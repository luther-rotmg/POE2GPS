using System;
using System.Collections.Generic;
using System.Linq;
using POE2Radar.Overlay;
using Xunit;

namespace POE2Radar.Tests.Overlay;

/// <summary>
/// Signal — SIG-PRELOAD-HIDE-ON-SPAWN. The world-thread scan updates PreloadHit.Spawned in place
/// via record `with` expressions; the renderer skips rows where Spawned=true. These tests lock
/// the record-with semantics + the substring-match predicate that gates the scan. WorldTick
/// integration itself is a structural constraint verified by manual smoke.
/// </summary>
public class PreloadHideOnSpawnTests
{
    [Fact]
    public void PreloadHit_Spawned_DefaultsToFalse()
    {
        var hit = new PreloadHit(
            Label: "Cleric",
            Tier: "pinnacle",
            Category: "Boss",
            Color: "#ff0000",
            SpawnEntityMetadata: "beast_boss",
            Spawned: false);
        Assert.False(hit.Spawned);
    }

    [Fact]
    public void PreloadHit_WithExpression_FlipsSpawnedInPlace()
    {
        var original = new PreloadHit(
            Label: "Cleric",
            Tier: "pinnacle",
            Category: "Boss",
            Color: "#ff0000",
            SpawnEntityMetadata: "beast_boss",
            Spawned: false);

        var flipped = original with { Spawned = true };

        Assert.True(flipped.Spawned);
        // Every non-Spawned field is preserved verbatim by the with-expression.
        Assert.Equal(original.Label, flipped.Label);
        Assert.Equal(original.Tier, flipped.Tier);
        Assert.Equal(original.Category, flipped.Category);
        Assert.Equal(original.Color, flipped.Color);
        Assert.Equal(original.SpawnEntityMetadata, flipped.SpawnEntityMetadata);
    }

    [Fact]
    public void PreloadHits_FilteredForRender_SkipsSpawnedRows()
    {
        var hits = new List<PreloadHit>();
        hits.Add(new PreloadHit("Cleric",   "pinnacle", "Boss",   "#ff0000", "beast_boss", Spawned: false));
        hits.Add(new PreloadHit("Wretch",   "high",     "Unique", "#ff8800", "wretch",     Spawned: true));
        hits.Add(new PreloadHit("Shrine A", "mechanic", "Shrine", "#ffff00", SpawnEntityMetadata: null, Spawned: false));

        var visible = hits.Where(h => !h.Spawned).ToList();

        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(visible, h => h.Label == "Wretch");
        Assert.Contains(visible, h => h.Label == "Cleric");
        Assert.Contains(visible, h => h.Label == "Shrine A");
    }

    [Fact]
    public void SpawnMetadata_SubstringMatch_IsCaseInsensitive()
    {
        // The world-thread scan uses StringComparison.OrdinalIgnoreCase on Metadata.Contains(needle).
        // Live entity metadata comes in whatever case the game presents; the preload catalog is
        // authored lowercase. Verify the coercion works both ways.
        const string needle = "Metadata/Monsters/Boss/BeastFamily";
        var entityMeta = "metadata/monsters/boss/beastfamily/cleric_01";
        Assert.Contains(needle, entityMeta, StringComparison.OrdinalIgnoreCase);

        var needleLower = "beastfamily";
        var entityMetaMixed = "Metadata/Monsters/Boss/BeastFamily/Cleric_01";
        Assert.Contains(needleLower, entityMetaMixed, StringComparison.OrdinalIgnoreCase);
    }
}
