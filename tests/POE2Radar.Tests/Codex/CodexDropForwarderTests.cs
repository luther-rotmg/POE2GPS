using System.Collections.Generic;
using POE2Radar.Core.Codex;
using POE2Radar.Core.Session;
using Xunit;

namespace POE2Radar.Tests.Codex;

public class CodexDropForwarderTests
{
    [Fact]
    public void Unique_drop_forwards_as_NotableDropEvent()
    {
        var events = new List<CodexEvent>();
        var f = new CodexDropForwarder(events.Add);

        f.OnDropRecorded(new DropEntry(100, "Unique", "Headhunter", "MapCanal", "Alice", 42u));

        var e = Assert.Single(events);
        var drop = Assert.IsType<NotableDropEvent>(e);
        Assert.Equal("Headhunter", drop.Name);
        Assert.Equal("Unique", drop.Rarity);
        Assert.Equal("MapCanal", drop.Zone);
        Assert.Equal(100L, drop.Ts);
    }

    [Fact]
    public void Non_unique_drop_is_ignored()
    {
        var events = new List<CodexEvent>();
        var f = new CodexDropForwarder(events.Add);

        f.OnDropRecorded(new DropEntry(100, "Rare", "Yellow-item", "Zone", "Alice", 1u));
        f.OnDropRecorded(new DropEntry(101, "Magic", "Blue-item", "Zone", "Alice", 2u));
        f.OnDropRecorded(new DropEntry(102, "Normal", "White-item", "Zone", "Alice", 3u));

        Assert.Empty(events);
    }

    [Fact]
    public void Duplicate_unique_for_same_character_is_deduped()
    {
        var events = new List<CodexEvent>();
        var f = new CodexDropForwarder(events.Add);

        f.OnDropRecorded(new DropEntry(100, "Unique", "Headhunter", "Zone1", "Alice", 1u));
        f.OnDropRecorded(new DropEntry(200, "Unique", "Headhunter", "Zone2", "Alice", 2u));
        f.OnDropRecorded(new DropEntry(300, "Unique", "Headhunter", "Zone3", "Alice", 3u));

        Assert.Single(events);
    }

    [Fact]
    public void Same_unique_for_different_characters_forwards_each_first()
    {
        var events = new List<CodexEvent>();
        var f = new CodexDropForwarder(events.Add);

        f.OnDropRecorded(new DropEntry(100, "Unique", "Headhunter", "Zone", "Alice", 1u));
        f.OnDropRecorded(new DropEntry(200, "Unique", "Headhunter", "Zone", "Bob",   2u));
        f.OnDropRecorded(new DropEntry(300, "Unique", "Headhunter", "Zone", "Alice", 3u)); // Alice dup
        f.OnDropRecorded(new DropEntry(400, "Unique", "Headhunter", "Zone", "Bob",   4u)); // Bob dup

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Null_entry_or_empty_name_silently_ignored()
    {
        var events = new List<CodexEvent>();
        var f = new CodexDropForwarder(events.Add);

        f.OnDropRecorded(null!);
        f.OnDropRecorded(new DropEntry(100, "Unique", "", "Zone", "Alice", 1u));

        Assert.Empty(events);
    }

    [Fact]
    public void Wires_to_DropTimeline_Recorded_event()
    {
        var events = new List<CodexEvent>();
        var f = new CodexDropForwarder(events.Add);
        var tmpFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "droptimeline-test-" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var dt = new DropTimeline(tmpFile);
            dt.Recorded += f.OnDropRecorded;
            dt.Record(100, "Unique", "Headhunter", "Zone", "Alice", 42u);
            dt.Record(200, "Rare", "Yellow", "Zone", "Alice", 43u);
            Assert.Single(events);
        }
        finally
        {
            try { System.IO.File.Delete(tmpFile); } catch { }
        }
    }
}
