using System.Text.Json;
using POE2Radar.Core.Campaign.Probe;

namespace POE2Radar.Tests.Campaign.Probe;

// v0.22 campaign-probe — spec §4 (world-thread safety) + §11 (opt-off = zero cost).
// Covers the six behaviours the Task 4 verify gate names: per-boot file rotation,
// 32-event batch flush, timer flush, IsDisabled graceful degrade, DisposeAsync drain,
// and MostRecentCompletePath prior-boot selection.
public sealed class EventWriterTests : IDisposable
{
    private readonly string _baseDir;

    public EventWriterTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "poe2gps-writer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static EventEnvelope Envelope(string uuid, long boot, long ts, string eventType = "zone_entered") => new(
        TsEpochMs:       ts,
        InstallUuid:     uuid,
        BootId:          boot.ToString("D"),
        EventType:       eventType,
        ProbeCapability: "live",
        SchemaVersion:   1,
        ActHint:         "act1",
        AreaName:        "The Riverbank");

    private static ZoneEnteredEvent Zone(string uuid, long boot, long ts, string area = "The Riverbank") =>
        new(
            Envelope:       Envelope(uuid, boot, ts) with { AreaName = area },
            AreaLevel:      3,
            AreaIdHash:     "abc0123456789def",
            IsTown:         false,
            IsHideout:      false,
            PlayerWorldPos: new WorldPos(100f, 200f));

    // ── File path shape ────────────────────────────────────────────────────

    [Fact]
    public void CurrentFilePath_uses_install_uuid_and_boot_epoch_verbatim_under_baseDirectory()
    {
        const string uuid = "11111111-1111-4111-8111-111111111111";
        const long boot = 1_720_000_000_000L;
        using var _ = new WriterHandle(new EventWriter(uuid, boot, _baseDir));
        Assert.Equal(Path.Combine(_baseDir, $"{uuid}_{boot}.jsonl"), _.Writer.CurrentFilePath);
        Assert.Equal(boot.ToString("D"), _.Writer.CurrentBootId);
        Assert.False(_.Writer.IsDisabled);
    }

    // ── Per-boot rotation ──────────────────────────────────────────────────

    [Fact]
    public async Task Different_boot_epochs_rotate_to_different_files()
    {
        const string uuid = "22222222-2222-4222-8222-222222222222";
        var w1 = new EventWriter(uuid, 1_720_000_000_000L, _baseDir);
        w1.Enqueue(Zone(uuid, 1_720_000_000_000L, 1_720_000_000_100L, "Boot1Area"));
        await w1.DisposeAsync();

        var w2 = new EventWriter(uuid, 1_720_000_099_999L, _baseDir);
        w2.Enqueue(Zone(uuid, 1_720_000_099_999L, 1_720_000_099_500L, "Boot2Area"));
        await w2.DisposeAsync();

        Assert.NotEqual(w1.CurrentFilePath, w2.CurrentFilePath);
        Assert.True(File.Exists(w1.CurrentFilePath));
        Assert.True(File.Exists(w2.CurrentFilePath));
        Assert.Contains("Boot1Area", File.ReadAllText(w1.CurrentFilePath));
        Assert.Contains("Boot2Area", File.ReadAllText(w2.CurrentFilePath));
        Assert.DoesNotContain("Boot2Area", File.ReadAllText(w1.CurrentFilePath));
    }

    // ── Batch flush at 32 events ───────────────────────────────────────────

    [Fact]
    public async Task Batch_flush_lands_bytes_at_32_events_before_the_timer_ticks()
    {
        const string uuid = "33333333-3333-4333-8333-333333333333";
        const long boot = 1_720_000_000_001L;
        // Use a long timer interval so the ONLY way bytes hit disk before Dispose is
        // via the batch trigger.
        var w = new EventWriter(uuid, boot, _baseDir, flushInterval: TimeSpan.FromSeconds(30));
        try
        {
            for (int i = 0; i < 32; i++) w.Enqueue(Zone(uuid, boot, 1_720_000_000_100L + i));

            // Poll well under the 30 s timer — the batch-of-32 flush must land bytes on disk.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(w.CurrentFilePath) && new FileInfo(w.CurrentFilePath).Length > 0) break;
                await Task.Delay(20);
            }
            Assert.True(File.Exists(w.CurrentFilePath));
            Assert.True(new FileInfo(w.CurrentFilePath).Length > 0,
                "32-event batch trigger must flush before the 30s timer");
            Assert.Equal(32, w.EventsWritten);
        }
        finally { await w.DisposeAsync(); }
    }

    // ── Timer flush below the batch threshold ──────────────────────────────

    [Fact]
    public async Task Timer_flush_lands_bytes_below_the_batch_threshold()
    {
        const string uuid = "44444444-4444-4444-8444-444444444444";
        const long boot = 1_720_000_000_002L;
        var w = new EventWriter(uuid, boot, _baseDir, flushInterval: TimeSpan.FromMilliseconds(150));
        try
        {
            w.Enqueue(Zone(uuid, boot, 1_720_000_000_100L));

            // One event is far below the 32-batch threshold; only the 150 ms coalescing
            // timer can flush this to disk. Poll up to 3 s of slack for CI VM jitter.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(w.CurrentFilePath) && new FileInfo(w.CurrentFilePath).Length > 0) break;
                await Task.Delay(25);
            }
            Assert.True(File.Exists(w.CurrentFilePath));
            Assert.True(new FileInfo(w.CurrentFilePath).Length > 0,
                "sub-batch timer flush must land bytes without waiting for Dispose");
            Assert.Equal(1, w.EventsWritten);
        }
        finally { await w.DisposeAsync(); }
    }

    // ── IsDisabled graceful degrade ────────────────────────────────────────

    [Fact]
    public async Task File_open_failure_disables_writer_and_Enqueue_becomes_a_no_op()
    {
        // Point baseDirectory at an existing FILE so Directory.CreateDirectory throws
        // IOException — matches the real-world "path shadowed by a file" install error.
        var bogusFile = Path.Combine(_baseDir, "iamafile.txt");
        File.WriteAllText(bogusFile, "not a directory");

        var w = new EventWriter("55555555-5555-4555-8555-555555555555", 1_720_000_000_003L, bogusFile);
        try
        {
            Assert.True(w.IsDisabled);
            for (int i = 0; i < 100; i++) w.Enqueue(Zone("55555555-5555-4555-8555-555555555555", 1_720_000_000_003L, 1_720_000_000_100L + i));
            await Task.Delay(50); // give any hypothetical pump a chance to increment — it must not
            Assert.Equal(0, w.EventsWritten);
        }
        finally { await w.DisposeAsync(); }
    }

    // ── DisposeAsync drain ─────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_flushes_all_queued_events_before_returning()
    {
        const string uuid = "66666666-6666-4666-8666-666666666666";
        const long boot = 1_720_000_000_004L;
        var w = new EventWriter(uuid, boot, _baseDir, flushInterval: TimeSpan.FromMinutes(5));
        w.Enqueue(Zone(uuid, boot, 1_720_000_000_100L, "Area1"));
        w.Enqueue(Zone(uuid, boot, 1_720_000_000_101L, "Area2"));
        w.Enqueue(Zone(uuid, boot, 1_720_000_000_102L, "Area3"));
        w.Enqueue(Zone(uuid, boot, 1_720_000_000_103L, "Area4"));
        w.Enqueue(Zone(uuid, boot, 1_720_000_000_104L, "Area5"));

        await w.DisposeAsync();

        var lines = File.ReadAllLines(w.CurrentFilePath);
        Assert.Equal(5, lines.Length);
        Assert.Equal(5, w.EventsWritten);
        // Each line is a self-contained JSON object with the envelope fields inlined.
        for (int i = 0; i < 5; i++)
        {
            using var doc = JsonDocument.Parse(lines[i]);
            Assert.Equal("zone_entered", doc.RootElement.GetProperty("event_type").GetString());
            Assert.Equal(uuid, doc.RootElement.GetProperty("install_uuid").GetString());
            Assert.Equal($"Area{i + 1}", doc.RootElement.GetProperty("area_name").GetString());
        }
    }

    // ── MostRecentCompletePath ─────────────────────────────────────────────

    [Fact]
    public async Task MostRecentCompletePath_returns_highest_bootEpoch_prior_file_excluding_current()
    {
        const string uuid = "77777777-7777-4777-8777-777777777777";
        // Seed three prior-boot files with distinct bootEpochMs suffixes.
        var priorLow    = Path.Combine(_baseDir, $"{uuid}_1000.jsonl");
        var priorMid    = Path.Combine(_baseDir, $"{uuid}_2000.jsonl");
        var priorHigh   = Path.Combine(_baseDir, $"{uuid}_3000.jsonl");
        File.WriteAllText(priorLow, "seed\n");
        File.WriteAllText(priorMid, "seed\n");
        File.WriteAllText(priorHigh, "seed\n");

        // A file for a DIFFERENT install must be ignored entirely.
        File.WriteAllText(Path.Combine(_baseDir, "88888888-8888-4888-8888-888888888888_9999.jsonl"), "other\n");

        // Fourth (current) boot has the highest suffix. MostRecentCompletePath must
        // exclude it and return priorHigh.
        var w = new EventWriter(uuid, 4000L, _baseDir);
        try
        {
            var picked = w.MostRecentCompletePath();
            Assert.Equal(priorHigh, picked);
            Assert.NotEqual(w.CurrentFilePath, picked);
        }
        finally { await w.DisposeAsync(); }
    }

    [Fact]
    public async Task MostRecentCompletePath_returns_null_when_no_prior_boots_exist()
    {
        var w = new EventWriter("99999999-9999-4999-8999-999999999999", 5000L, _baseDir);
        try { Assert.Null(w.MostRecentCompletePath()); }
        finally { await w.DisposeAsync(); }
    }

    // ── FlushAsync / FlushSync sync semantics ──────────────────────────────

    [Fact]
    public async Task FlushAsync_waits_for_enqueued_events_and_lands_them_on_disk()
    {
        const string uuid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        const long boot = 1_720_000_000_005L;
        // Long timer so the only path to disk before Dispose is FlushAsync itself.
        var w = new EventWriter(uuid, boot, _baseDir, flushInterval: TimeSpan.FromMinutes(5));
        try
        {
            for (int i = 0; i < 3; i++) w.Enqueue(Zone(uuid, boot, 1_720_000_000_100L + i));
            await w.FlushAsync();
            Assert.Equal(3, w.EventsWritten);
            Assert.True(new FileInfo(w.CurrentFilePath).Length > 0);
            Assert.Equal(3, ReadJsonLinesWhileOpen(w.CurrentFilePath).Length);
        }
        finally { await w.DisposeAsync(); }
    }

    [Fact]
    public async Task FlushSync_waits_for_enqueued_events_and_lands_them_on_disk()
    {
        const string uuid = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        const long boot = 1_720_000_000_006L;
        var w = new EventWriter(uuid, boot, _baseDir, flushInterval: TimeSpan.FromMinutes(5));
        try
        {
            for (int i = 0; i < 4; i++) w.Enqueue(Zone(uuid, boot, 1_720_000_000_100L + i));
            w.FlushSync();
            Assert.Equal(4, w.EventsWritten);
            Assert.Equal(4, ReadJsonLinesWhileOpen(w.CurrentFilePath).Length);
        }
        finally { await w.DisposeAsync(); }
    }

    // The writer holds the file open with FileAccess.Write + FileShare.Read. A test
    // that reads the file WHILE the writer is still alive must open with
    // FileShare.ReadWrite so Windows doesn't reject the second handle for
    // wanting-to-block a concurrent writer — the /submit-trace pack path relies on
    // this same sharing contract.
    private static string[] ReadJsonLinesWhileOpen(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null) lines.Add(line);
        return lines.ToArray();
    }

    // ── Test helper: guarantees DisposeAsync fires even on assertion failure. ──

    private sealed class WriterHandle : IDisposable
    {
        public EventWriter Writer { get; }
        public WriterHandle(EventWriter w) { Writer = w; }
        public void Dispose() => Writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
