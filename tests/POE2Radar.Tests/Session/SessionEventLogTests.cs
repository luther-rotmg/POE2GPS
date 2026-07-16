using System;
using System.IO;
using System.Linq;
using POE2Radar.Core.Session;
using Xunit;

namespace POE2Radar.Tests.Session;

public class SessionEventLogTests : IDisposable
{
    private readonly string _root;

    public SessionEventLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poe2radar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static CodexEvent Ev(long ts, string zone = "MapUberBoss_Arbiter", int level = 82)
        => new LevelUpEvent(ts, 0xdeadbeef, zone, level);

    private static string PathFor(string root, string name) => Path.Combine(root, "codex", name + ".jsonl");

    [Fact]
    public void Record_dropped_before_any_stable_name()
    {
        var log = new SessionEventLog(_root);
        Assert.False(log.Record(Ev(1)));
        Assert.Null(log.OpenCharacter);
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void Opens_file_only_after_N_stable_ticks_of_same_nonempty_name()
    {
        var log = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks - 1; i++) log.ObservePlayerName("Alice");
        Assert.Null(log.OpenCharacter);
        Assert.False(log.Record(Ev(1)));

        log.ObservePlayerName("Alice");
        Assert.Equal("Alice", log.OpenCharacter);
        Assert.True(log.Record(Ev(2)));
        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void Flicker_to_empty_resets_stability_counter_but_does_not_close_open_file()
    {
        var log = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("Alice");
        Assert.Equal("Alice", log.OpenCharacter);
        log.Record(Ev(1));

        log.ObservePlayerName("");
        log.ObservePlayerName(null);
        Assert.Equal("Alice", log.OpenCharacter); // stays open across blank flicker

        log.ObservePlayerName("Alice"); // only 1 stable tick now — no re-open, still Alice
        Assert.Equal("Alice", log.OpenCharacter);
        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void Character_swap_switches_file_after_new_name_is_stable()
    {
        var log = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("Alice");
        log.Record(Ev(1));
        log.Flush();

        // One-tick flicker of Bob does NOT switch.
        log.ObservePlayerName("Bob");
        Assert.Equal("Alice", log.OpenCharacter);

        for (int i = 1; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("Bob");
        Assert.Equal("Bob", log.OpenCharacter);
        Assert.Empty(log.Snapshot()); // Bob starts fresh — no Alice bleedthrough

        log.Record(Ev(2));
        log.Flush();

        Assert.True(File.Exists(PathFor(_root, "Alice")));
        Assert.True(File.Exists(PathFor(_root, "Bob")));
    }

    [Fact]
    public void Ring_cap_truncates_oldest_first()
    {
        var log = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("Alice");
        for (long t = 1; t <= SessionEventLog.MaxEntriesPerCharacter + 25; t++)
            log.Record(Ev(t));
        var snap = log.Snapshot();
        Assert.Equal(SessionEventLog.MaxEntriesPerCharacter, snap.Count);
        Assert.Equal(26L, snap[0].Ts); // first 25 evicted
        Assert.Equal((long)SessionEventLog.MaxEntriesPerCharacter + 25, snap[^1].Ts);
    }

    [Fact]
    public void Snapshot_returns_independent_array()
    {
        var log = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("Alice");
        log.Record(Ev(1));
        var s1 = log.Snapshot();
        log.Record(Ev(2));
        Assert.Single(s1); // caller's copy unaffected
        Assert.Equal(2, log.Snapshot().Count);
    }

    [Fact]
    public void Generation_increments_on_each_accepted_record()
    {
        var log = new SessionEventLog(_root);
        var g0 = log.Generation;
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("Alice");
        var gOpen = log.Generation; // open bumps generation (state changed)
        Assert.True(gOpen > g0);
        log.Record(Ev(1));
        log.Record(Ev(2));
        Assert.Equal(gOpen + 2, log.Generation);
    }

    [Fact]
    public void Missing_file_yields_empty_state_and_never_throws()
    {
        var log = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("NoOne");
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void Malformed_single_line_does_not_nuke_valid_neighbors()
    {
        // v0.37 codex JSONL hardening: per-line try/catch keeps good rows.
        var dir = Path.Combine(_root, "codex");
        Directory.CreateDirectory(dir);
        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var goodA = System.Text.Json.JsonSerializer.Serialize<CodexEvent>(new LevelUpEvent(1, 0x1, "Z", 80), opts);
        var goodB = System.Text.Json.JsonSerializer.Serialize<CodexEvent>(new LevelUpEvent(3, 0x1, "Z", 81), opts);
        File.WriteAllText(PathFor(_root, "Alice"), goodA + "\n{garbage}\n" + goodB + "\n");

        var log = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("Alice");
        var snap = log.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal(1L, snap[0].Ts);
        Assert.Equal(3L, snap[1].Ts);
    }

    [Fact]
    public void Flush_then_reload_roundtrips_polymorphic_events()
    {
        var path = PathFor(_root, "Alice");
        {
            var log = new SessionEventLog(_root);
            for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("Alice");
            log.Record(new LevelUpEvent(1, 0xdead, "Zone1", 80));
            log.Record(new DeathEvent(2, 0xbeef, "Zone2", 82, 96));
            log.Record(new BossKillEvent(3, 0xfeed, "MapUberBoss", "arbiter", "Arbiter"));
            log.Record(new NotableDropEvent(4, 0xcafe, "MapCanal", "Headhunter", "Unique", "hh-belt"));
            log.Flush();
        }
        Assert.True(File.Exists(path));
        // JSONL: exactly 4 lines.
        var lines = File.ReadAllLines(path);
        Assert.Equal(4, lines.Length);

        var reloaded = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) reloaded.ObservePlayerName("Alice");
        var snap = reloaded.Snapshot();
        Assert.Equal(4, snap.Count);
        Assert.IsType<LevelUpEvent>(snap[0]);
        Assert.IsType<DeathEvent>(snap[1]);
        Assert.IsType<BossKillEvent>(snap[2]);
        Assert.IsType<NotableDropEvent>(snap[3]);
        Assert.Equal("Headhunter", ((NotableDropEvent)snap[3]).Name);
    }

    [Fact]
    public void Filename_sanitizes_unsafe_characters()
    {
        var log = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("../weird name!");
        log.Record(Ev(1));
        log.Flush();
        // No file may escape the codex/ subdirectory.
        var codexDir = Path.Combine(_root, "codex");
        Assert.True(Directory.Exists(codexDir));
        var files = Directory.GetFiles(codexDir);
        Assert.Single(files);
        Assert.StartsWith(codexDir, Path.GetFullPath(files[0]));
        Assert.EndsWith(".jsonl", files[0]);
    }

    [Fact]
    public void SnapshotForCharacter_reads_arbitrary_character_without_swapping_open()
    {
        // Preload Alice's file
        var log1 = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log1.ObservePlayerName("Alice");
        log1.Record(new LevelUpEvent(1, 0x1, "Zone", 80));
        log1.Record(new DeathEvent(2, 0x1, "Zone", 80, 96));
        log1.Flush();

        // A different log instance opens Bob; SnapshotForCharacter("Alice") reads Alice's file
        // WITHOUT changing Bob's open state.
        var log2 = new SessionEventLog(_root);
        for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log2.ObservePlayerName("Bob");
        Assert.Equal("Bob", log2.OpenCharacter);

        var alice = log2.SnapshotForCharacter("Alice");
        Assert.Equal(2, alice.Count);
        Assert.IsType<LevelUpEvent>(alice[0]);
        Assert.IsType<DeathEvent>(alice[1]);

        // Bob is still open, no crosstalk
        Assert.Equal("Bob", log2.OpenCharacter);
    }

    [Fact]
    public void SnapshotForCharacter_missing_file_returns_empty()
    {
        var log = new SessionEventLog(_root);
        Assert.Empty(log.SnapshotForCharacter("NoSuch"));
        Assert.Empty(log.SnapshotForCharacter(""));
    }

    [Fact]
    public void Dispose_flushes()
    {
        var path = PathFor(_root, "Alice");
        using (var log = new SessionEventLog(_root))
        {
            for (int i = 0; i < SessionEventLog.NameStabilityTicks; i++) log.ObservePlayerName("Alice");
            log.Record(Ev(1));
        }
        Assert.True(File.Exists(path));
    }
}
