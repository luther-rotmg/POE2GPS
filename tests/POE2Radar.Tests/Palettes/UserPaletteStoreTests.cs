using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using POE2Radar.Core.Palettes;
using Xunit;

namespace POE2Radar.Tests.Palettes;

public sealed class UserPaletteStoreTests : IDisposable
{
    private readonly string _rootDir;

    public UserPaletteStoreTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "poe2gps-forge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // Helper to build a valid default vars dictionary.
    private static Dictionary<string, string> DefaultVars() => new()
    {
        ["--gold"] = "#c8a050",
        ["--gold-bright"] = "#e8c060",
        ["--gold-deep"] = "#a08030",
        ["--ink"] = "#e0d8c8",
        ["--ink-dim"] = "#a09888",
        ["--ink-faint"] = "#605850",
        ["--panel"] = "#1a1e28",
        ["--panel2"] = "#141820",
        ["--bg"] = "#0d0f14",
        ["--bg-alt"] = "#0a0c10",
        ["--line"] = "#3a3e48",
        ["--line-soft"] = "#2a2e38",
        ["--good"] = "#88c040",
    };

    [Fact]
    public void Round_trip_save_returns_equal_palette()
    {
        var vars = DefaultVars();
        var now = new DateTime(2026, 7, 16, 12, 34, 56, DateTimeKind.Utc);
        var p = new UserPalette("my-custom", "My Custom", vars, Array.Empty<string>(), now);

        var saved = UserPaletteStore.Save(_rootDir, p);
        var loaded = UserPaletteStore.Get(_rootDir, "my-custom");

        Assert.NotNull(loaded);
        Assert.Equal(saved.Slug, loaded!.Slug);
        Assert.Equal(saved.DisplayName, loaded.DisplayName);
        Assert.Equal(saved.Vars.Count, loaded.Vars.Count);
        foreach (var kvp in saved.Vars)
            Assert.Equal(kvp.Value, loaded.Vars[kvp.Key]);
        // Preview is auto-derived, not from the input.
        Assert.Equal(4, loaded.Preview.Count);
        Assert.Equal(vars["--bg"], loaded.Preview[0]);
        Assert.Equal(vars["--panel"], loaded.Preview[1]);
        Assert.Equal(vars["--gold"], loaded.Preview[2]);
        Assert.Equal(vars["--ink"], loaded.Preview[3]);
    }

    [Fact]
    public void List_enumerates_three_saved_palettes()
    {
        var now = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc);
        var vars = DefaultVars();

        UserPaletteStore.Save(_rootDir, new UserPalette("alpha", "Alpha", vars, Array.Empty<string>(), now));
        UserPaletteStore.Save(_rootDir, new UserPalette("beta", "Beta", vars, Array.Empty<string>(), now));
        UserPaletteStore.Save(_rootDir, new UserPalette("gamma", "Gamma", vars, Array.Empty<string>(), now));

        var list = UserPaletteStore.List(_rootDir);
        Assert.Equal(3, list.Count);
        var slugs = list.Select(p => p.Slug).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, slugs);
    }

    [Fact]
    public void Delete_removes_file_and_returns_true_missing_slug_returns_false()
    {
        var vars = DefaultVars();
        var now = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc);
        UserPaletteStore.Save(_rootDir, new UserPalette("to-delete", "To Delete", vars, Array.Empty<string>(), now));

        // File exists before delete.
        Assert.NotNull(UserPaletteStore.Get(_rootDir, "to-delete"));

        // Delete returns true.
        Assert.True(UserPaletteStore.Delete(_rootDir, "to-delete"));

        // File is gone.
        Assert.Null(UserPaletteStore.Get(_rootDir, "to-delete"));

        // Missing slug returns false.
        Assert.False(UserPaletteStore.Delete(_rootDir, "nonexistent"));
    }

    [Fact]
    public void Reserved_built_in_slug_throws_ArgumentException()
    {
        var vars = DefaultVars();
        var now = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc);

        var ex = Assert.Throws<ArgumentException>(() =>
            UserPaletteStore.Save(_rootDir, new UserPalette("kalguuran", "Bad", vars, Array.Empty<string>(), now)));
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_var_throws_ArgumentException()
    {
        var vars = DefaultVars();
        vars.Remove("--gold"); // Remove one required key.
        var now = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc);

        var ex = Assert.Throws<ArgumentException>(() =>
            UserPaletteStore.Save(_rootDir, new UserPalette("missing-var", "Missing", vars, Array.Empty<string>(), now)));
        Assert.Contains("Missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bad_hex_throws_ArgumentException()
    {
        var vars = DefaultVars();
        vars["--gold"] = "not-a-hex"; // Invalid hex value.
        var now = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc);

        var ex = Assert.Throws<ArgumentException>(() =>
            UserPaletteStore.Save(_rootDir, new UserPalette("bad-hex", "Bad Hex", vars, Array.Empty<string>(), now)));
        Assert.Contains("invalid hex", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_is_auto_derived_from_bg_panel_gold_ink()
    {
        var vars = DefaultVars();
        vars["--bg"] = "#111111";
        vars["--panel"] = "#222222";
        vars["--gold"] = "#333333";
        vars["--ink"] = "#444444";
        var now = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc);

        var saved = UserPaletteStore.Save(_rootDir, new UserPalette("preview-test", "Preview", vars, Array.Empty<string>(), now));
        Assert.Equal(4, saved.Preview.Count);
        Assert.Equal("#111111", saved.Preview[0]);
        Assert.Equal("#222222", saved.Preview[1]);
        Assert.Equal("#333333", saved.Preview[2]);
        Assert.Equal("#444444", saved.Preview[3]);
    }
}