using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using POE2Radar.Core.Tracks;
using Xunit;

namespace POE2Radar.Tests.Tracks;

public sealed class TrackStoreTests
{
    private static string FreshTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "trackstore-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Append_NewFile_CreatesJsonlLine()
    {
        var dir = FreshTempDir();
        try
        {
            var ok = TrackStore.Append(dir, "Alice", "act1_town", new TrackSample(0, 1.5f, 2.5f));
            Assert.True(ok);
            var path = Path.Combine(dir, "tracks", "Alice", "act1_town.jsonl");
            Assert.True(File.Exists(path));
            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            Assert.Contains("\"t\":0", lines[0]);
            Assert.Contains("\"x\":1.5", lines[0]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Append_MultipleSamples_AllPresent()
    {
        var dir = FreshTempDir();
        try
        {
            for (var i = 0; i < 5; i++)
                TrackStore.Append(dir, "Alice", "act1", new TrackSample(i * 1000L, i, i * 2));
            var samples = TrackStore.Load(dir, "Alice", "act1");
            Assert.Equal(5, samples.Count);
            Assert.Equal(0L, samples[0].T);
            Assert.Equal(4000L, samples[4].T);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var dir = FreshTempDir();
        try
        {
            var samples = TrackStore.Load(dir, "Nobody", "no_zone");
            Assert.Empty(samples);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RoundTrip_SaveThenLoad_PreservesFields()
    {
        var dir = FreshTempDir();
        try
        {
            var original = new TrackSample(12345L, 3.14f, -2.71f);
            TrackStore.Append(dir, "Alice", "act2", original);
            var loaded = TrackStore.Load(dir, "Alice", "act2");
            Assert.Single(loaded);
            Assert.Equal(original.T, loaded[0].T);
            Assert.Equal(original.X, loaded[0].X);
            Assert.Equal(original.Y, loaded[0].Y);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Load_MalformedLineMidFile_SkipsAndContinues()
    {
        var dir = FreshTempDir();
        try
        {
            TrackStore.Append(dir, "Alice", "act3", new TrackSample(0, 0, 0));
            var path = Path.Combine(dir, "tracks", "Alice", "act3.jsonl");
            File.AppendAllText(path, "this is not JSON\n");
            TrackStore.Append(dir, "Alice", "act3", new TrackSample(100, 1, 1));
            var samples = TrackStore.Load(dir, "Alice", "act3");
            Assert.Equal(2, samples.Count);
            Assert.Equal(0L, samples[0].T);
            Assert.Equal(100L, samples[1].T);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Append_EmptyCharacter_ReturnsFalse()
    {
        var dir = FreshTempDir();
        try { Assert.False(TrackStore.Append(dir, "", "z", new TrackSample(0, 0, 0))); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Append_CharacterOnlySpecials_ReturnsFalse()
    {
        var dir = FreshTempDir();
        try { Assert.False(TrackStore.Append(dir, "!!!", "z", new TrackSample(0, 0, 0))); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Append_EmptyZone_ReturnsFalse()
    {
        var dir = FreshTempDir();
        try { Assert.False(TrackStore.Append(dir, "Alice", "", new TrackSample(0, 0, 0))); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Append_SanitizesCharacterName()
    {
        var dir = FreshTempDir();
        try
        {
            TrackStore.Append(dir, "Player/1", "act1", new TrackSample(0, 0, 0));
            var path = Path.Combine(dir, "tracks", "Player_1", "act1.jsonl");
            Assert.True(File.Exists(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Append_SanitizesZoneCode()
    {
        var dir = FreshTempDir();
        try
        {
            TrackStore.Append(dir, "Alice", "act1/town", new TrackSample(0, 0, 0));
            var path = Path.Combine(dir, "tracks", "Alice", "act1_town.jsonl");
            Assert.True(File.Exists(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Append_AtRingCap_DropsOldestSamples()
    {
        var dir = FreshTempDir();
        try
        {
            // Fast path: seed the on-disk file directly with 10000 pre-serialized lines so we
            // don't pay the full I/O tax of 10000 sequential Appends just to reach ring-cap
            // (each Append flushes the whole file on close — measured >100ms/sample; batching
            // would take ~20 min on CI). Then invoke Append(one more sample) → triggers
            // EnsureUnderRingCap → we assert the trim landed.
            var charDir = Path.Combine(dir, "tracks", "Alice");
            Directory.CreateDirectory(charDir);
            var path = Path.Combine(charDir, "big.jsonl");
            var seed = new System.Text.StringBuilder();
            for (var i = 0; i < 10000; i++)
                seed.Append("{\"t\":").Append(i).Append(",\"x\":0,\"y\":0}\n");
            File.WriteAllText(path, seed.ToString());

            var ok = TrackStore.Append(dir, "Alice", "big", new TrackSample(99999L, 42, 42));
            Assert.True(ok);

            var samples = TrackStore.Load(dir, "Alice", "big");
            Assert.True(samples.Count <= 9500, $"After ring trim, count should be well under 10000; got {samples.Count}");
            Assert.Equal(99999L, samples.Last().T);
            Assert.True(samples.First().T > 500, "Oldest samples should be dropped after ring trim");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ListZones_EmptyDir_ReturnsEmpty()
    {
        var dir = FreshTempDir();
        try { Assert.Empty(TrackStore.ListZones(dir, "Nobody")); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ListZones_MultipleZones_ReturnsAllSorted()
    {
        var dir = FreshTempDir();
        try
        {
            TrackStore.Append(dir, "Alice", "zone_b", new TrackSample(0, 0, 0));
            TrackStore.Append(dir, "Alice", "zone_a", new TrackSample(0, 0, 0));
            var zones = TrackStore.ListZones(dir, "Alice");
            Assert.Equal(2, zones.Count);
            Assert.Equal("zone_a", zones[0]);
            Assert.Equal("zone_b", zones[1]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ListCharacters_EmptyRoot_ReturnsEmpty()
    {
        var dir = FreshTempDir();
        try { Assert.Empty(TrackStore.ListCharacters(dir)); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ListCharacters_MultipleCharacters_ReturnsAllSorted()
    {
        var dir = FreshTempDir();
        try
        {
            TrackStore.Append(dir, "Bob", "z", new TrackSample(0, 0, 0));
            TrackStore.Append(dir, "Alice", "z", new TrackSample(0, 0, 0));
            var chars = TrackStore.ListCharacters(dir);
            Assert.Equal(2, chars.Count);
            Assert.Equal("Alice", chars[0]);
            Assert.Equal("Bob", chars[1]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Perf_100AppendsUnderBudget()
    {
        // v1.0 sample rate is 1 Hz, so 100 appends represents ~100 seconds of play —
        // even 10s wall time (~100ms/sample) is fine at that cadence. Target 5s ceiling.
        var dir = FreshTempDir();
        try
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++)
                TrackStore.Append(dir, "Alice", "perf", new TrackSample(i, i, i));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 5000, $"100 appends took {sw.ElapsedMilliseconds}ms — expected < 5000ms at 1-Hz cadence budget");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
