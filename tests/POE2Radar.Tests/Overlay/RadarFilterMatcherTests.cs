using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using POE2Radar.Core.RadarFilters;
using POE2Radar.Overlay.Overlay;
using Xunit;

namespace POE2Radar.Tests.Overlay;

public sealed class RadarFilterMatcherTests
{
    private static RadarFilterPreset MakePreset(string match, string[]? whitelist = null, string[]? blacklist = null)
    {
        return new RadarFilterPreset(
            match,
            whitelist?.ToList().AsReadOnly() ?? Array.Empty<string>().AsReadOnly(),
            blacklist?.ToList().AsReadOnly() ?? Array.Empty<string>().AsReadOnly());
    }

    [Fact]
    public void CompileBlacklist_EmptyFile_ReturnsEmpty()
    {
        var file = new RadarFilterFile(1, Array.Empty<RadarFilterPreset>());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Empty(result);
    }

    [Fact]
    public void CompileBlacklist_NoMatchingPreset_ReturnsEmpty()
    {
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("Map*", blacklist: ["Metadata/Effects/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Empty(result);
    }

    [Fact]
    public void CompileBlacklist_ExactZoneMatch_ReturnsPresetPatterns()
    {
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("G1_town", blacklist: ["Metadata/Effects/*", "Metadata/NPC/Traders/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void CompileBlacklist_WildcardZoneMatch_ReturnsPresetPatterns()
    {
        // "*_town" should match "G1_town"
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("*_town", blacklist: ["Metadata/Effects/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Single(result);
    }

    [Fact]
    public void CompileBlacklist_FirstMatchingPresetWins()
    {
        // Two presets both match "G1_town"; first one's blacklist should be returned.
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("*_town", blacklist: ["Metadata/Effects/*"]),
            MakePreset("G1_*", blacklist: ["Metadata/NPC/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Single(result);

        // The first preset's pattern should match "Metadata/Effects/Foo"
        Assert.True(result[0].IsMatch("Metadata/Effects/Foo"));
        Assert.False(result[0].IsMatch("Metadata/NPC/Trader"));
    }

    [Fact]
    public void CompileBlacklist_EmptyBlacklist_ReturnsEmpty()
    {
        // Preset matches zone but blacklist is empty.
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("*_town", whitelist: ["Metadata/NPC/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Empty(result);
    }

    [Fact]
    public void CompileBlacklist_PatternsMatchMetadata()
    {
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("*_town", blacklist: ["Metadata/Effects/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Single(result);

        Assert.True(result[0].IsMatch("Metadata/Effects/Foo"));
        Assert.True(result[0].IsMatch("Metadata/Effects/SomeEffect"));
        Assert.False(result[0].IsMatch("Metadata/NPC/Trader"));
        Assert.False(result[0].IsMatch("Metadata/Monsters/Zombie"));
    }

    [Fact]
    public void CompileBlacklist_PatternsCaseInsensitive()
    {
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("*_town", blacklist: ["metadata/effects/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Single(result);

        // Should match regardless of case
        Assert.True(result[0].IsMatch("Metadata/Effects/Foo"));
        Assert.True(result[0].IsMatch("metadata/effects/foo"));
        Assert.True(result[0].IsMatch("METADATA/EFFECTS/BAR"));
    }

    [Fact]
    public void CompileBlacklist_MultiplePatterns_AllCompiled()
    {
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("*_town", blacklist: ["Metadata/Effects/*", "Metadata/NPC/Traders/*", "Metadata/Ambient/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Equal(3, result.Length);

        // All three regexes should be usable
        Assert.True(result[0].IsMatch("Metadata/Effects/Foo"));
        Assert.True(result[1].IsMatch("Metadata/NPC/Traders/Bar"));
        Assert.True(result[2].IsMatch("Metadata/Ambient/Baz"));
    }

    [Fact]
    public void CompileBlacklist_InvalidRegexPattern_GlobEscapesDot()
    {
        // A literal "." in the pattern should be escaped (match literal dot, not any char).
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("*", blacklist: ["Metadata.NPC.Trader"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Single(result);

        // Should match the exact literal (with dots as literal dots)
        Assert.True(result[0].IsMatch("Metadata.NPC.Trader"));
        // Should NOT match where dots are different chars
        Assert.False(result[0].IsMatch("MetadataXNPCXTrader"));
    }

    [Fact]
    public void CompileBlacklist_NullFile_ReturnsEmpty()
    {
        var result = RadarFilterMatcher.CompileBlacklist(null!, "G1_town");
        Assert.Empty(result);
    }

    [Fact]
    public void CompileBlacklist_NullOrEmptyZoneCode_ReturnsEmpty()
    {
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("*", blacklist: ["Metadata/Effects/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());

        Assert.Empty(RadarFilterMatcher.CompileBlacklist(file, null!));
        Assert.Empty(RadarFilterMatcher.CompileBlacklist(file, ""));
    }

    [Fact]
    public void CompileBlacklist_MultiplePatterns_EachMatchesCorrectly()
    {
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("*_town", blacklist: ["Metadata/Effects/*", "Metadata/NPC/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var result = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Equal(2, result.Length);

        Assert.True(result[0].IsMatch("Metadata/Effects/Fire"));
        Assert.True(result[1].IsMatch("Metadata/NPC/Trader"));
        Assert.False(result[0].IsMatch("Metadata/Monsters/Zombie"));
        Assert.False(result[1].IsMatch("Metadata/Monsters/Zombie"));
    }

    [Fact]
    public void Perf_100EntitiesX1000Iter_UnderCiSlackBudget()
    {
        // Build a synthetic 100-entity metadata list: 50 blacklisted, 50 not.
        var metadataList = new List<string>();
        for (int i = 0; i < 50; i++)
            metadataList.Add($"Metadata/Effects/Effect_{i:D3}");
        for (int i = 0; i < 50; i++)
            metadataList.Add($"Metadata/NPC/Trader_{i:D3}");

        // Compile a blacklist that matches "Metadata/Effects/*"
        var presets = new List<RadarFilterPreset>
        {
            MakePreset("*_town", blacklist: ["Metadata/Effects/*"]),
        };
        var file = new RadarFilterFile(1, presets.AsReadOnly());
        var regexes = RadarFilterMatcher.CompileBlacklist(file, "G1_town");
        Assert.Single(regexes);

        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < 1000; iter++)
        {
            foreach (var meta in metadataList)
            {
                // Walk the blacklist regexes (same logic as hot path)
                for (int b = 0; b < regexes.Length; b++)
                {
                    if (regexes[b].IsMatch(meta))
                        break;
                }
            }
        }
        sw.Stop();

        // Assert total wall time under 500ms (10× CI slack for 1000 × 100 = 100K matches).
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Perf budget exceeded: {sw.ElapsedMilliseconds}ms (expected < 500ms for 100K matches)");
    }
}