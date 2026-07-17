using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using POE2Radar.Core.OverlayLayouts;
using Xunit;

namespace POE2Radar.Tests.OverlayLayouts;

public sealed class OverlayLayoutStoreTests : IDisposable
{
    private readonly string _configDir;

    public OverlayLayoutStoreTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "poe2gps-layouts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // --- Helpers ---

    private static OverlayLayoutPreset MakePreset(string name, string match, IReadOnlyDictionary<string, PanelState>? panels = null)
    {
        return new OverlayLayoutPreset(name, match, panels ?? new Dictionary<string, PanelState>());
    }

    private static OverlayLayoutFile MakeFile(params OverlayLayoutPreset[] presets)
    {
        return new OverlayLayoutFile(1, presets.ToList().AsReadOnly());
    }

    // --- Tests ---

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var file = OverlayLayoutStore.Load(_configDir);
        Assert.Equal(1, file.SchemaVersion);
        Assert.Empty(file.Presets);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_WithMixedPanelStates()
    {
        // Three presets: visible-only, visible+xy, xy-only
        var preset1 = new OverlayLayoutPreset(
            "town",
            "*_town",
            new Dictionary<string, PanelState>
            {
                ["dropTimeline"] = new PanelState(Visible: false, X: null, Y: null),
                ["bossHp"] = new PanelState(Visible: false, X: null, Y: null),
            });

        var preset2 = new OverlayLayoutPreset(
            "hideout",
            "*_hideout",
            new Dictionary<string, PanelState>
            {
                ["xpChart"] = new PanelState(Visible: true, X: 100, Y: 200),
            });

        var preset3 = new OverlayLayoutPreset(
            "boss",
            "*_boss*",
            new Dictionary<string, PanelState>
            {
                ["minimap"] = new PanelState(Visible: null, X: 50, Y: 75),
            });

        var file = MakeFile(preset1, preset2, preset3);
        OverlayLayoutStore.Save(_configDir, file);

        var loaded = OverlayLayoutStore.Load(_configDir);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(3, loaded.Presets.Count);

        // Verify preset1 (visible-only)
        Assert.Equal("town", loaded.Presets[0].Name);
        Assert.Equal("*_town", loaded.Presets[0].Match);
        Assert.Equal(2, loaded.Presets[0].Panels.Count);
        Assert.False(loaded.Presets[0].Panels["dropTimeline"].Visible);
        Assert.Null(loaded.Presets[0].Panels["dropTimeline"].X);
        Assert.Null(loaded.Presets[0].Panels["dropTimeline"].Y);
        Assert.False(loaded.Presets[0].Panels["bossHp"].Visible);
        Assert.Null(loaded.Presets[0].Panels["bossHp"].X);
        Assert.Null(loaded.Presets[0].Panels["bossHp"].Y);

        // Verify preset2 (visible+xy)
        Assert.Equal("hideout", loaded.Presets[1].Name);
        Assert.True(loaded.Presets[1].Panels["xpChart"].Visible);
        Assert.Equal(100, loaded.Presets[1].Panels["xpChart"].X);
        Assert.Equal(200, loaded.Presets[1].Panels["xpChart"].Y);

        // Verify preset3 (xy-only)
        Assert.Equal("boss", loaded.Presets[2].Name);
        Assert.Null(loaded.Presets[2].Panels["minimap"].Visible);
        Assert.Equal(50, loaded.Presets[2].Panels["minimap"].X);
        Assert.Equal(75, loaded.Presets[2].Panels["minimap"].Y);
    }

    [Fact]
    public void Save_EmptyPresets_Works()
    {
        var file = MakeFile();
        OverlayLayoutStore.Save(_configDir, file);

        var loaded = OverlayLayoutStore.Load(_configDir);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Empty(loaded.Presets);
    }

    [Fact]
    public void Save_Over10Presets_Throws()
    {
        var presets = new List<OverlayLayoutPreset>();
        for (int i = 0; i < 11; i++)
            presets.Add(MakePreset($"preset{i}", "*"));

        var file = MakeFile(presets.ToArray());
        var ex = Assert.Throws<ArgumentException>(() => OverlayLayoutStore.Save(_configDir, file));
        Assert.Contains("10", ex.Message);
    }

    [Fact]
    public void Save_DuplicateNamesCaseInsensitive_Throws()
    {
        var file = MakeFile(
            MakePreset("Town", "*_town*"),
            MakePreset("town", "*_town*")); // Same name, different case

        var ex = Assert.Throws<ArgumentException>(() => OverlayLayoutStore.Save(_configDir, file));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_EmptyName_Throws()
    {
        var file = MakeFile(MakePreset("", "*"));
        var ex = Assert.Throws<ArgumentException>(() => OverlayLayoutStore.Save(_configDir, file));
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_NameOver40Chars_Throws()
    {
        var name = new string('x', 41);
        var file = MakeFile(MakePreset(name, "*"));
        var ex = Assert.Throws<ArgumentException>(() => OverlayLayoutStore.Save(_configDir, file));
        Assert.Contains("40", ex.Message);
    }

    [Fact]
    public void Save_EmptyMatch_Throws()
    {
        var file = MakeFile(MakePreset("valid", ""));
        var ex = Assert.Throws<ArgumentException>(() => OverlayLayoutStore.Save(_configDir, file));
        Assert.Contains("match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_MatchOver64Chars_Throws()
    {
        var match = new string('x', 65);
        var file = MakeFile(MakePreset("valid", match));
        var ex = Assert.Throws<ArgumentException>(() => OverlayLayoutStore.Save(_configDir, file));
        Assert.Contains("64", ex.Message);
    }

    [Fact]
    public void Save_PanelsDictOver32Entries_Throws()
    {
        var panels = new Dictionary<string, PanelState>();
        for (int i = 0; i < 33; i++)
            panels[$"panel{i}"] = new PanelState(Visible: true, X: null, Y: null);

        var file = MakeFile(MakePreset("valid", "*", panels));
        var ex = Assert.Throws<ArgumentException>(() => OverlayLayoutStore.Save(_configDir, file));
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void Save_AtomicWrite_LeavesNoTmpFile()
    {
        var file = MakeFile(MakePreset("default", "*"));
        OverlayLayoutStore.Save(_configDir, file);

        var tmpPath = Path.Combine(_configDir, "overlay-layouts.json.tmp");
        Assert.False(File.Exists(tmpPath), "Temporary .tmp file should not exist after save.");

        var finalPath = Path.Combine(_configDir, "overlay-layouts.json");
        Assert.True(File.Exists(finalPath), "Final file should exist after save.");
    }

    [Fact]
    public void Load_MalformedJson_ThrowsInvalidData()
    {
        var path = Path.Combine(_configDir, "overlay-layouts.json");
        File.WriteAllText(path, "this is not valid json {{{");

        var ex = Assert.Throws<InvalidDataException>(() => OverlayLayoutStore.Load(_configDir));
        Assert.Contains("overlay-layouts.json", ex.Message);
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void ValidatePreset_HappyPath_NoThrow()
    {
        var preset = MakePreset("town", "*_town", new Dictionary<string, PanelState>
        {
            ["dropTimeline"] = new PanelState(Visible: false, X: null, Y: null),
        });

        // Should not throw.
        OverlayLayoutStore.ValidatePreset(preset);
    }
}