using System;
using System.IO;
using System.Numerics;
using System.Threading;
using POE2Radar.Core.Tracks;
using Xunit;

namespace POE2Radar.Tests.Tracks;

public sealed class TrackRecorderTests
{
    private static string FreshTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "trackrecorder-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TrackFilePath(string configDir, string character, string zone)
        => Path.Combine(configDir, "tracks", character, zone + ".jsonl");

    [Fact]
    public void ObserveTick_NullPlayerName_NoOp()
    {
        var dir = FreshTempDir();
        try
        {
            var recorder = new TrackRecorder(dir);
            for (var i = 0; i < 100; i++)
                recorder.ObserveTick(null, "zone1", Vector2.Zero);
            var path = TrackFilePath(dir, "Alice", "zone1");
            Assert.False(File.Exists(path), "No file should exist when playerName is null");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ObserveTick_UnstablePlayerName_NoWriteUntil30Ticks()
    {
        var dir = FreshTempDir();
        try
        {
            var recorder = new TrackRecorder(dir);

            // 29 ticks with the same name → still no file (stability not reached).
            for (var i = 0; i < 29; i++)
                recorder.ObserveTick("Alice", "zone1", Vector2.Zero);
            var path = TrackFilePath(dir, "Alice", "zone1");
            Assert.False(File.Exists(path), "No file should exist after 29 stable ticks");

            // 30th tick passes the stability gate → first sample written.
            recorder.ObserveTick("Alice", "zone1", Vector2.Zero);
            Assert.True(File.Exists(path), "File should exist after 30th stable tick");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ObserveTick_DownsamplesToOneHz()
    {
        var dir = FreshTempDir();
        try
        {
            var recorder = new TrackRecorder(dir);

            // 30 ticks to stabilize.
            for (var i = 0; i < 30; i++)
                recorder.ObserveTick("Alice", "zone1", Vector2.Zero);

            // 100 more ticks with 10ms delay between each → at most 2 samples.
            for (var i = 0; i < 100; i++)
            {
                recorder.ObserveTick("Alice", "zone1", Vector2.Zero);
                Thread.Sleep(10);
            }

            var samples = TrackStore.Load(dir, "Alice", "zone1");
            Assert.True(samples.Count <= 2, $"Expected at most 2 samples after 1-second window, got {samples.Count}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ObserveTick_ZoneChange_ResetsClock()
    {
        var dir = FreshTempDir();
        try
        {
            var recorder = new TrackRecorder(dir);

            // Stabilize on "Alice".
            for (var i = 0; i < 30; i++)
                recorder.ObserveTick("Alice", "zone_a", Vector2.Zero);

            // First sample in zone_a at t=0.
            recorder.ObserveTick("Alice", "zone_a", Vector2.Zero);
            Thread.Sleep(100);

            // Change to zone_b → writes sample at t=0 for zone_b.
            recorder.ObserveTick("Alice", "zone_b", Vector2.One);

            // Both zones should have exactly 1 sample, each with t ≈ 0.
            var zoneASamples = TrackStore.Load(dir, "Alice", "zone_a");
            Assert.Single(zoneASamples);
            Assert.True(zoneASamples[0].T < 50, $"zone_a t should be near 0, got {zoneASamples[0].T}");

            var zoneBSamples = TrackStore.Load(dir, "Alice", "zone_b");
            Assert.Single(zoneBSamples);
            Assert.True(zoneBSamples[0].T < 50, $"zone_b t should be near 0, got {zoneBSamples[0].T}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ObserveTick_SwallowsAppendException()
    {
        // Use a garbage configDir that will cause TrackStore.Append to fail.
        // The recorder must swallow any exception and never throw.
        try
        {
            var recorder = new TrackRecorder("::invalid||path\t\x00");

            // 30 ticks to stabilize, then 1 more to write.
            for (var i = 0; i < 31; i++)
                recorder.ObserveTick("Alice", "zone1", Vector2.Zero);

            // If we reach here, the exception was swallowed — test passes.
            Assert.True(true);
        }
        catch
        {
            Assert.Fail("TrackRecorder should swallow all exceptions, including from TrackStore.Append");
        }
    }

    [Fact]
    public void ObserveTick_MissingGrid_NoOp()
    {
        var dir = FreshTempDir();
        try
        {
            var recorder = new TrackRecorder(dir);
            for (var i = 0; i < 100; i++)
                recorder.ObserveTick("Alice", "zone1", null);
            var path = TrackFilePath(dir, "Alice", "zone1");
            Assert.False(File.Exists(path), "No file should exist when playerGrid is null");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ObserveTick_EmptyZone_NoOp()
    {
        var dir = FreshTempDir();
        try
        {
            var recorder = new TrackRecorder(dir);
            // Pass stability gate first.
            for (var i = 0; i < 100; i++)
                recorder.ObserveTick("Alice", "", Vector2.Zero);
            var path = TrackFilePath(dir, "Alice", "");
            Assert.False(File.Exists(path), "No file should exist when zone is empty");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ObserveTick_NullZone_NoOp()
    {
        var dir = FreshTempDir();
        try
        {
            var recorder = new TrackRecorder(dir);
            for (var i = 0; i < 100; i++)
                recorder.ObserveTick("Alice", null, Vector2.Zero);
            var path = TrackFilePath(dir, "Alice", "zone1");
            Assert.False(File.Exists(path), "No file should exist when zone is null");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ObserveTick_FullFlow_WritesSample()
    {
        var dir = FreshTempDir();
        try
        {
            var recorder = new TrackRecorder(dir);

            // 30 stability ticks.
            for (var i = 0; i < 30; i++)
                recorder.ObserveTick("Alice", "zone1", new Vector2(1.5f, 2.5f));

            // 31st tick writes the first sample.
            recorder.ObserveTick("Alice", "zone1", new Vector2(1.5f, 2.5f));

            var path = TrackFilePath(dir, "Alice", "zone1");
            Assert.True(File.Exists(path), "File should exist after stability + write");

            var samples = TrackStore.Load(dir, "Alice", "zone1");
            Assert.Single(samples);
            Assert.Equal(0L, samples[0].T);
            Assert.Equal(1.5f, samples[0].X);
            Assert.Equal(2.5f, samples[0].Y);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}