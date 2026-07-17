using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using POE2Radar.Core.RadarFilters;
using Xunit;

namespace POE2Radar.Tests.RadarFilters;

public sealed class RadarFilterStoreTests
{
    private static string NewConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "poe2gps-filters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string configDir)
    {
        try { Directory.Delete(configDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static RadarFilterPreset MakePreset(string match, string[]? whitelist = null, string[]? blacklist = null)
    {
        return new RadarFilterPreset(
            match,
            whitelist?.ToList().AsReadOnly() ?? Array.Empty<string>().AsReadOnly(),
            blacklist?.ToList().AsReadOnly() ?? Array.Empty<string>().AsReadOnly());
    }

    // --- Tests ---

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var configDir = NewConfigDir();
        try
        {
            var file = RadarFilterStore.Load(configDir);
            Assert.Equal(1, file.SchemaVersion);
            Assert.Empty(file.Presets);
        }
        finally
        {
            Cleanup(configDir);
        }
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var configDir = NewConfigDir();
        try
        {
            var presets = new List<RadarFilterPreset>
            {
                MakePreset("Map*", ["Metadata/NPC/Traders/*"], ["Metadata/Effects/*"]),
                MakePreset("*_town", ["Metadata/NPC/*"], []),
            };

            RadarFilterStore.Save(configDir, new RadarFilterFile(1, presets.AsReadOnly()));

            var loaded = RadarFilterStore.Load(configDir);
            Assert.Equal(1, loaded.SchemaVersion);
            Assert.Equal(2, loaded.Presets.Count);

            Assert.Equal("Map*", loaded.Presets[0].Match);
            Assert.Single(loaded.Presets[0].Whitelist);
            Assert.Equal("Metadata/NPC/Traders/*", loaded.Presets[0].Whitelist[0]);
            Assert.Single(loaded.Presets[0].Blacklist);
            Assert.Equal("Metadata/Effects/*", loaded.Presets[0].Blacklist[0]);

            Assert.Equal("*_town", loaded.Presets[1].Match);
            Assert.Single(loaded.Presets[1].Whitelist);
            Assert.Equal("Metadata/NPC/*", loaded.Presets[1].Whitelist[0]);
            Assert.Empty(loaded.Presets[1].Blacklist);
        }
        finally
        {
            Cleanup(configDir);
        }
    }

    [Fact]
    public void Save_EmptyPresetsList_Works()
    {
        var configDir = NewConfigDir();
        try
        {
            var file = new RadarFilterFile(1, Array.Empty<RadarFilterPreset>().AsReadOnly());
            RadarFilterStore.Save(configDir, file);

            var loaded = RadarFilterStore.Load(configDir);
            Assert.Equal(1, loaded.SchemaVersion);
            Assert.Empty(loaded.Presets);
        }
        finally
        {
            Cleanup(configDir);
        }
    }

    [Fact]
    public void Save_Over20Presets_Throws()
    {
        var configDir = NewConfigDir();
        try
        {
            var presets = new List<RadarFilterPreset>();
            for (int i = 0; i < 21; i++)
                presets.Add(MakePreset("preset" + i));

            var ex = Assert.Throws<ArgumentException>(() =>
                RadarFilterStore.Save(configDir, new RadarFilterFile(1, presets.AsReadOnly())));
            Assert.Contains("20", ex.Message);
        }
        finally
        {
            Cleanup(configDir);
        }
    }

    [Fact]
    public void Save_PresetWith51WhitelistEntries_Throws()
    {
        var configDir = NewConfigDir();
        try
        {
            var whitelist = new List<string>();
            for (int i = 0; i < 51; i++)
                whitelist.Add("Metadata/Pattern" + i);

            var preset = MakePreset("test", whitelist.ToArray());
            var ex = Assert.Throws<ArgumentException>(() =>
                RadarFilterStore.Save(configDir, new RadarFilterFile(1, new[] { preset }.AsReadOnly())));
            Assert.Contains("50", ex.Message);
        }
        finally
        {
            Cleanup(configDir);
        }
    }

    [Fact]
    public void Save_PresetWith51BlacklistEntries_Throws()
    {
        var configDir = NewConfigDir();
        try
        {
            var blacklist = new List<string>();
            for (int i = 0; i < 51; i++)
                blacklist.Add("Metadata/Pattern" + i);

            var preset = MakePreset("test", blacklist: blacklist.ToArray());
            var ex = Assert.Throws<ArgumentException>(() =>
                RadarFilterStore.Save(configDir, new RadarFilterFile(1, new[] { preset }.AsReadOnly())));
            Assert.Contains("50", ex.Message);
        }
        finally
        {
            Cleanup(configDir);
        }
    }

    [Fact]
    public void Save_AtomicWrite_LeavesNoTmpFile()
    {
        var configDir = NewConfigDir();
        try
        {
            var preset = MakePreset("*", ["Metadata/NPC/*"]);
            RadarFilterStore.Save(configDir, new RadarFilterFile(1, new[] { preset }.AsReadOnly()));

            var tmpPath = Path.Combine(configDir, "radar-filters.json.tmp");
            Assert.False(File.Exists(tmpPath), "Temporary file should have been removed after atomic write.");
        }
        finally
        {
            Cleanup(configDir);
        }
    }

    [Fact]
    public void Save_MalformedRuleFailsValidation_Throws()
    {
        var configDir = NewConfigDir();
        try
        {
            // Empty match string fails validation
            var preset = MakePreset("", ["Metadata/NPC/*"]);
            var ex = Assert.Throws<ArgumentException>(() =>
                RadarFilterStore.Save(configDir, new RadarFilterFile(1, new[] { preset }.AsReadOnly())));
            Assert.Contains("match", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(configDir);
        }
    }

    [Fact]
    public void Save_WhitelistEntryTooLong_Throws()
    {
        var configDir = NewConfigDir();
        try
        {
            var longEntry = new string('x', 129);
            var preset = MakePreset("*", [longEntry]);
            var ex = Assert.Throws<ArgumentException>(() =>
                RadarFilterStore.Save(configDir, new RadarFilterFile(1, new[] { preset }.AsReadOnly())));
            Assert.Contains("128", ex.Message);
        }
        finally
        {
            Cleanup(configDir);
        }
    }

    [Fact]
    public void ValidatePreset_EmptyMatch_Throws()
    {
        var preset = MakePreset("", ["Metadata/NPC/*"]);
        var ex = Assert.Throws<ArgumentException>(() => RadarFilterStore.ValidatePreset(preset));
        Assert.Contains("match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatePreset_WhitespaceOnlyEntry_Throws()
    {
        var preset = MakePreset("*", ["   "]);
        var ex = Assert.Throws<ArgumentException>(() => RadarFilterStore.ValidatePreset(preset));
        Assert.Contains("whitespace", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatePreset_MatchTooLong_Throws()
    {
        var longMatch = new string('a', 65);
        var preset = MakePreset(longMatch);
        var ex = Assert.Throws<ArgumentException>(() => RadarFilterStore.ValidatePreset(preset));
        Assert.Contains("64", ex.Message);
    }

    [Fact]
    public void Load_MalformedJson_ThrowsInvalidData()
    {
        var configDir = NewConfigDir();
        try
        {
            var path = Path.Combine(configDir, "radar-filters.json");
            File.WriteAllText(path, "this is not valid json {{{");

            var ex = Assert.Throws<InvalidDataException>(() => RadarFilterStore.Load(configDir));
            Assert.Contains("radar-filters.json", ex.Message);
        }
        finally
        {
            Cleanup(configDir);
        }
    }

    [Fact]
    public void Save_WildcardPatterns_ValidateOK()
    {
        var configDir = NewConfigDir();
        try
        {
            var presets = new List<RadarFilterPreset>
            {
                MakePreset("*_town", ["Metadata/NPC/*", "Metadata/Monsters/Ambient/*"]),
                MakePreset("Map*_*", ["Metadata/*"], ["Metadata/Effects/*"]),
            };

            // Should not throw
            RadarFilterStore.Save(configDir, new RadarFilterFile(1, presets.AsReadOnly()));

            var loaded = RadarFilterStore.Load(configDir);
            Assert.Equal(2, loaded.Presets.Count);
            Assert.Equal("*_town", loaded.Presets[0].Match);
            Assert.Equal("Map*_*", loaded.Presets[1].Match);
        }
        finally
        {
            Cleanup(configDir);
        }
    }
}