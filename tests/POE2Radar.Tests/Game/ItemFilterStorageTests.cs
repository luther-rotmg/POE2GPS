using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Game;

public class ItemFilterStorageTests
{
    private static string FreshPath()
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try { if (File.Exists(p)) File.Delete(p); } catch { }
        return p;
    }

    [Fact]
    public void Load_missing_file_seeds_from_embedded_presets()
    {
        var path = FreshPath();
        try
        {
            var engine = new ItemFilterEngine(path);
            var all = engine.All;
            Assert.True(all.Count > 0, "Missing file should seed from embedded default_item_filters.json");
            foreach (var f in all)
                Assert.False(f.Enabled, $"Seeded preset '{f.Id}' must be disabled by default");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Load_missing_file_writes_seed_to_disk()
    {
        var path = FreshPath();
        try
        {
            var _ = new ItemFilterEngine(path);
            Assert.True(File.Exists(path), "After ctor with missing file, the file should exist on disk (seed materialized)");
            var reloaded = new ItemFilterEngine(path);
            Assert.Equal(_.All.Count, reloaded.All.Count);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Malformed_file_yields_empty_list()
    {
        var path = FreshPath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json");
            var engine = new ItemFilterEngine(path);
            Assert.Empty(engine.All);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Add_and_reload_roundtrips()
    {
        var path = FreshPath();
        try
        {
            var engine = new ItemFilterEngine(path);
            engine.Replace(Array.Empty<FilterRule>());
            var rule = new FilterRule("test-add", "Test Add", true, "#123456", 42, new[]
            {
                new FilterRequirement("some_stat", ">=", 10, null, null, null)
            });
            engine.Add(rule);

            var reloaded = new ItemFilterEngine(path);
            Assert.Contains(reloaded.All, r => r.Id == "test-add");
            var got = reloaded.All.First(r => r.Id == "test-add");
            Assert.Equal("Test Add", got.Name);
            Assert.Equal(42, got.Priority);
            Assert.Equal("#123456", got.Color);
            Assert.True(got.Enabled);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void RemoveAt_and_reload_roundtrips()
    {
        var path = FreshPath();
        try
        {
            var engine = new ItemFilterEngine(path);
            engine.Replace(Array.Empty<FilterRule>());
            engine.Add(new FilterRule("keep-me", "Keep", true, "#111111", 100, Array.Empty<FilterRequirement>()));
            engine.Add(new FilterRule("test-x", "Remove", true, "#222222", 100, Array.Empty<FilterRequirement>()));
            var idx = engine.All.Select((r, i) => (r, i)).First(x => x.r.Id == "test-x").i;
            engine.RemoveAt(idx);

            var reloaded = new ItemFilterEngine(path);
            Assert.DoesNotContain(reloaded.All, r => r.Id == "test-x");
            Assert.Contains(reloaded.All, r => r.Id == "keep-me");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void RestoreStarterPresets_is_additive()
    {
        var path = FreshPath();
        try
        {
            // Fresh engine has the shipped presets. Note their ids.
            var seeded = new ItemFilterEngine(path);
            var seedIds = seeded.All.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            Assert.NotEmpty(seedIds);

            // Wipe everything, then restore — should re-populate exactly the seed ids.
            seeded.Replace(Array.Empty<FilterRule>());
            Assert.Empty(seeded.All);
            seeded.RestoreStarterPresets();
            var restoredIds = seeded.All.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(seedIds, restoredIds);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
