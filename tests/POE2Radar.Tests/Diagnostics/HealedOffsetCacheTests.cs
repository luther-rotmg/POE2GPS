using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using POE2Radar.Core.Diagnostics;
using Xunit;

namespace POE2Radar.Tests.Diagnostics;

public sealed class HealedOffsetCacheTests : IDisposable
{
    private readonly string _configDir;

    public HealedOffsetCacheTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "poe2gps-healed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        HealedOffsetCache.Clear();
        try { Directory.Delete(_configDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>Returns the cache to a known clean state.</summary>
    private void ResetCache()
    {
        HealedOffsetCache.Clear();
        HealedOffsetCache.Load(_configDir);
    }

    [Fact]
    public void Resolve_Untouched_ReturnsConfigured()
    {
        ResetCache();

        var result = HealedOffsetCache.Resolve("Poe2.AwakeEntities", 0x6D8);
        Assert.Equal(0x6D8, result);
    }

    [Fact]
    public void Resolve_AfterSetHealed_ReturnsHealed()
    {
        ResetCache();

        HealedOffsetCache.SetHealed("Poe2.AwakeEntities", 0x6F0);
        var result = HealedOffsetCache.Resolve("Poe2.AwakeEntities", 0x6D8);

        Assert.Equal(0x6F0, result);
    }

    [Fact]
    public void SetHealed_Twice_LogsBothTimes()
    {
        ResetCache();

        HealedOffsetCache.SetHealed("Poe2.TestField", 0x100);
        HealedOffsetCache.SetHealed("Poe2.TestField", 0x200);

        var logPath = Path.Combine(_configDir, "healed-offsets.log");
        Assert.True(File.Exists(logPath));

        var lines = File.ReadAllLines(logPath);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void SetHealed_WritesPersistenceFile()
    {
        ResetCache();

        HealedOffsetCache.SetHealed("Poe2.AreaInstance.Terrain", 0x8D0);

        var persistPath = Path.Combine(_configDir, "healed-offsets.json");
        Assert.True(File.Exists(persistPath));

        var loaded = HealedOffsetsFile.Load(_configDir);
        Assert.Single(loaded.Healed);
        Assert.Equal("Poe2.AreaInstance.Terrain", loaded.Healed[0].Symbol);
        Assert.Equal(0x8D0, loaded.Healed[0].Healed);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        HealedOffsetCache.Clear();
        var emptyDir = Path.Combine(Path.GetTempPath(), "poe2gps-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            HealedOffsetCache.Load(emptyDir);

            // No exceptions should occur, and All should be empty
            Assert.Empty(HealedOffsetCache.All);
        }
        finally
        {
            try { Directory.Delete(emptyDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Load_ExistingFile_HydratesCache()
    {
        ResetCache();

        // Create persistence data via SetHealed
        HealedOffsetCache.SetHealed("Poe2.Entity.Life", 0x1C0);

        // Clear and re-Load into a fresh state
        HealedOffsetCache.Clear();
        HealedOffsetCache.Load(_configDir);

        var resolved = HealedOffsetCache.Resolve("Poe2.Entity.Life", 0x1B0);
        Assert.Equal(0x1C0, resolved);
    }

    [Fact]
    public void Load_MalformedJson_Throws()
    {
        HealedOffsetCache.Clear();
        var badPath = Path.Combine(_configDir, "healed-offsets.json");
        File.WriteAllText(badPath, "{invalid json}");

        var ex = Assert.Throws<InvalidDataException>(() =>
            HealedOffsetCache.Load(_configDir));
        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidateStale_Over30Days_DropsEntry()
    {
        ResetCache();

        // Write an old entry directly to the file
        var oldTime = DateTime.UtcNow.AddDays(-31);
        var oldEntry = new HealedEntry("Poe2.StaleField", 0, 0x100, oldTime);
        var content = new HealedOffsetsFileContent(1, new[] { oldEntry });
        HealedOffsetsFile.Save(_configDir, content);

        // Reload with 30-day maxAge — the old entry should be dropped
        HealedOffsetCache.Clear();
        HealedOffsetCache.Load(_configDir, maxAge: TimeSpan.FromDays(30));

        // The old entry should have been dropped
        Assert.Empty(HealedOffsetCache.All);
        // Resolve should now return the configured value
        var resolved = HealedOffsetCache.Resolve("Poe2.StaleField", 0x50);
        Assert.Equal(0x50, resolved);
    }

    [Fact]
    public void InvalidateStale_Under30Days_KeepsEntry()
    {
        ResetCache();

        var recentTime = DateTime.UtcNow.AddDays(-5);
        var entry = new HealedEntry("Poe2.FreshField", 0, 0x200, recentTime);
        var content = new HealedOffsetsFileContent(1, new[] { entry });
        HealedOffsetsFile.Save(_configDir, content);

        HealedOffsetCache.Clear();
        HealedOffsetCache.Load(_configDir, maxAge: TimeSpan.FromDays(30));

        Assert.Single(HealedOffsetCache.All);
        var resolved = HealedOffsetCache.Resolve("Poe2.FreshField", 0x50);
        Assert.Equal(0x200, resolved);
    }

    [Fact]
    public void InvalidateStale_ConfiguredNowPassesGate_DropsEntry()
    {
        ResetCache();

        HealedOffsetCache.SetHealed("Poe2.GatedField", 0x300);

        // Clear and reload with a signature gate that says "configured passes" for this symbol
        HealedOffsetCache.Clear();
        HealedOffsetCache.Load(_configDir, signaturePassesGate: (symbol, configured) =>
            symbol == "Poe2.GatedField");

        Assert.Empty(HealedOffsetCache.All);
    }

    [Fact]
    public void SetHealed_ConcurrentWrites_NoRaceOrLoss()
    {
        ResetCache();

        var symbols = Enumerable.Range(0, 100).Select(i => $"Poe2.Concurrent.Field{i}").ToArray();

        Parallel.For(0, 100, i =>
        {
            HealedOffsetCache.SetHealed(symbols[i], 0x100 + i);
        });

        // All 100 should be in the cache
        Assert.Equal(100, HealedOffsetCache.All.Count);

        // And all should be persisted
        var loaded = HealedOffsetsFile.Load(_configDir);
        Assert.Equal(100, loaded.Healed.Count);
    }

    [Fact]
    public void SetHealed_LogFormat()
    {
        ResetCache();

        HealedOffsetCache.SetHealed("Poe2.FormatTest", 0xABC);

        var logPath = Path.Combine(_configDir, "healed-offsets.log");
        Assert.True(File.Exists(logPath));

        var lastLine = File.ReadAllLines(logPath).Last();
        // Expected format: <isoUtcZ>\t<symbol>\t0x<hex>\t0x<hex>
        var pattern = @"^[0-9T:\-.]+Z\t[^\t]+\t0x[0-9A-F]+\t0x[0-9A-F]+$";
        Assert.Matches(pattern, lastLine);
    }

    [Fact]
    public void WasHealed_ReturnsTrueAfterSetHealed()
    {
        ResetCache();

        Assert.False(HealedOffsetCache.WasHealed("Poe2.NotYetSet"));

        HealedOffsetCache.SetHealed("Poe2.NotYetSet", 0x400);
        Assert.True(HealedOffsetCache.WasHealed("Poe2.NotYetSet"));
    }

    [Fact]
    public void InvalidateStale_CallsOnReValidateForDroppedEntries()
    {
        ResetCache();

        // Create an old entry by directly writing to file
        var oldTime = DateTime.UtcNow.AddDays(-40);
        var oldEntry = new HealedEntry("Poe2.DropNow", 0, 0x600, oldTime);
        HealedOffsetsFile.Save(_configDir, new HealedOffsetsFileContent(1, new[] { oldEntry }));

        // Load it with a large maxAge so InvalidateStale is the one that drops it
        HealedOffsetCache.Clear();
        HealedOffsetCache.Load(_configDir, maxAge: TimeSpan.MaxValue);

        // Now call InvalidateStale with 7-day maxAge — should drop the entry
        var called = false;
        HealedOffsetCache.InvalidateStale(TimeSpan.FromDays(7), (symbol, configured) =>
        {
            called = true;
            Assert.Equal("Poe2.DropNow", symbol);
        });

        Assert.True(called);
        Assert.Empty(HealedOffsetCache.All);
    }

    [Fact]
    public void Load_WithMaxAge_NoDelegate_NoStaleEntries()
    {
        HealedOffsetCache.Clear();

        // Fresh entry (today)
        var freshTime = DateTime.UtcNow.AddHours(-1);
        var freshEntry = new HealedEntry("Poe2.FreshOne", 0, 0x100, freshTime);
        var content = new HealedOffsetsFileContent(1, new[] { freshEntry });
        HealedOffsetsFile.Save(_configDir, content);

        HealedOffsetCache.Load(_configDir, maxAge: TimeSpan.FromDays(30));
        Assert.Single(HealedOffsetCache.All);
        Assert.Equal(0x100, HealedOffsetCache.Resolve("Poe2.FreshOne", 0));
    }

    [Fact]
    public void SetHealed_Persistence_SurvivesAppRestart()
    {
        ResetCache();

        HealedOffsetCache.SetHealed("Poe2.Survivor", 0x777);

        // Clear and re-load from persistence (simulating app restart)
        HealedOffsetCache.Clear();
        HealedOffsetCache.Load(_configDir);

        Assert.True(HealedOffsetCache.WasHealed("Poe2.Survivor"));
        Assert.Equal(0x777, HealedOffsetCache.Resolve("Poe2.Survivor", 0x100));
    }
}