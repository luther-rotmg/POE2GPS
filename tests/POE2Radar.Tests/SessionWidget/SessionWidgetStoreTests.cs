using System;
using System.IO;
using System.Linq;
using POE2Radar.Core.SessionWidget;
using Xunit;

namespace POE2Radar.Tests.SessionWidget;

public sealed class SessionWidgetStoreTests : IDisposable
{
    private readonly string _configDir;

    public SessionWidgetStoreTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "poe2gps-sessionwidget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // --- Tests ---

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var config = SessionWidgetStore.Load(_configDir);
        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal(20, config.Position.X);
        Assert.Equal(20, config.Position.Y);
        Assert.Empty(config.Chips);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var config = new SessionWidgetConfig(
            1,
            new WidgetPosition(100, 200),
            new[] { "drops", "xp-gained", "time-in-zone" });

        SessionWidgetStore.Save(_configDir, config);

        var loaded = SessionWidgetStore.Load(_configDir);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(100, loaded.Position.X);
        Assert.Equal(200, loaded.Position.Y);
        Assert.Equal(3, loaded.Chips.Count);
        Assert.Contains("drops", loaded.Chips);
        Assert.Contains("xp-gained", loaded.Chips);
        Assert.Contains("time-in-zone", loaded.Chips);
    }

    [Fact]
    public void Save_AllValidChips_RoundTrips()
    {
        var allChips = new[] { "drops", "xp-gained", "bosses-killed", "deaths", "time-in-zone", "avg-map-clear-time" };
        var config = new SessionWidgetConfig(1, new WidgetPosition(20, 20), allChips);

        SessionWidgetStore.Save(_configDir, config);

        var loaded = SessionWidgetStore.Load(_configDir);
        Assert.Equal(6, loaded.Chips.Count);
        foreach (var chip in allChips)
            Assert.Contains(chip, loaded.Chips);
    }

    [Fact]
    public void Save_UnknownChip_Throws()
    {
        var config = new SessionWidgetConfig(
            1,
            new WidgetPosition(20, 20),
            new[] { "drops", "not-a-real-chip" });

        var ex = Assert.Throws<ArgumentException>(() => SessionWidgetStore.Save(_configDir, config));
        Assert.Contains("not-a-real-chip", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_DuplicateChip_Throws()
    {
        var config = new SessionWidgetConfig(
            1,
            new WidgetPosition(20, 20),
            new[] { "drops", "drops" });

        var ex = Assert.Throws<ArgumentException>(() => SessionWidgetStore.Save(_configDir, config));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_EmptyChips_Works()
    {
        var config = new SessionWidgetConfig(1, new WidgetPosition(20, 20), Array.Empty<string>());

        SessionWidgetStore.Save(_configDir, config);

        var loaded = SessionWidgetStore.Load(_configDir);
        Assert.Empty(loaded.Chips);
    }

    [Fact]
    public void ValidateConfig_NullChips_Throws()
    {
        var config = new SessionWidgetConfig(1, new WidgetPosition(20, 20), null!);

        var ex = Assert.Throws<ArgumentException>(() => SessionWidgetStore.ValidateConfig(config));
        Assert.Contains("Chips", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowedChips_HasExactly6Entries()
    {
        Assert.Equal(6, SessionWidgetStore.AllowedChips.Count);
    }

    [Theory]
    [InlineData("drops")]
    [InlineData("xp-gained")]
    [InlineData("bosses-killed")]
    [InlineData("deaths")]
    [InlineData("time-in-zone")]
    [InlineData("avg-map-clear-time")]
    public void AllowedChips_ContainsExpectedNames(string chip)
    {
        Assert.Contains(chip, SessionWidgetStore.AllowedChips);
    }

    [Fact]
    public void Save_AtomicWrite_LeavesNoTmpFile()
    {
        var config = new SessionWidgetConfig(1, new WidgetPosition(20, 20), new[] { "drops" });

        SessionWidgetStore.Save(_configDir, config);

        var tmpPath = Path.Combine(_configDir, "session-widget.json.tmp");
        Assert.False(File.Exists(tmpPath), "Temporary file should not exist after Save completes.");
    }
}