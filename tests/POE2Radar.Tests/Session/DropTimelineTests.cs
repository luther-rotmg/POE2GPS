using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using POE2Radar.Core.Session;

namespace POE2Radar.Tests.Session;

public class DropTimelineTests
{
    private readonly string _testFilePath;

    public DropTimelineTests()
    {
        _testFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    }

    [Fact]
    public void Record_new_drop_captures_entry()
    {
        // Arrange
        var timeline = new DropTimeline(_testFilePath);

        // Act
        timeline.Record(1000, "Rare", "Sword", "Zone1", "Player1", 12345);

        // Assert
        var snapshot = timeline.Snapshot();
        Assert.Single(snapshot);
        var entry = snapshot[0];
        Assert.Equal(1000, entry.TimestampSec);
        Assert.Equal("Rare", entry.Rarity);
        Assert.Equal("Sword", entry.Name);
        Assert.Equal("Zone1", entry.AreaCode);
        Assert.Equal("Player1", entry.CharacterName);
        Assert.Equal((uint)12345, entry.EntityId);
    }

    [Fact]
    public void Record_null_or_empty_name_is_noop()
    {
        // Arrange
        var timeline = new DropTimeline(_testFilePath);

        // Act
        timeline.Record(1000, "Rare", null, "Zone1", "Player1", 12345);
        timeline.Record(1001, "Magic", "", "Zone2", "Player1", 12346);

        // Assert
        var snapshot = timeline.Snapshot();
        Assert.Empty(snapshot);
    }

    [Fact]
    public void Record_duplicate_entityId_within_session_is_noop()
    {
        // Arrange
        var timeline = new DropTimeline(_testFilePath);

        // Act
        timeline.Record(1000, "Rare", "Sword", "Zone1", "Player1", 12345);
        timeline.Record(1001, "Magic", "Axe", "Zone2", "Player1", 12345); // Same entityId

        // Assert
        var snapshot = timeline.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("Sword", snapshot[0].Name);
    }

    [Fact]
    public void Ring_buffer_caps_at_MaxEntries()
    {
        // Arrange
        var timeline = new DropTimeline(_testFilePath);

        // Act
        // Record MaxEntries + 5 entries with unique IDs
        for (int i = 0; i < DropTimeline.MaxEntries + 5; i++)
        {
            timeline.Record(1000 + i, "Rare", $"Item{i}", "Zone1", "Player1", (uint)i);
        }

        // Assert
        var snapshot = timeline.Snapshot();
        Assert.Equal(DropTimeline.MaxEntries, snapshot.Count);
        
        // The first 5 entries should be dropped (oldest entries)
        Assert.Equal((uint)5, snapshot[0].EntityId); // First entry should be the 6th recorded
    }

    [Fact]
    public void Snapshot_returns_independent_copy()
    {
        // Arrange
        var timeline = new DropTimeline(_testFilePath);
        timeline.Record(1000, "Rare", "Sword", "Zone1", "Player1", 12345);

        // Act
        var snapshot1 = timeline.Snapshot();
        timeline.Record(1001, "Magic", "Axe", "Zone2", "Player1", 12346);
        var snapshot2 = timeline.Snapshot();

        // Assert
        Assert.Single(snapshot1); // Should still have only 1 entry
        Assert.Equal(2, snapshot2.Count); // Should have 2 entries now
    }

    [Fact]
    public void Load_missing_file_starts_empty()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        // Act
        var timeline = new DropTimeline(nonExistentPath);

        // Assert
        var snapshot = timeline.Snapshot();
        Assert.Empty(snapshot);
    }

    [Fact]
    public void Load_corrupt_file_starts_empty_no_throw()
    {
        // Arrange
        File.WriteAllText(_testFilePath, "invalid json content");

        // Act
        var timeline = new DropTimeline(_testFilePath);

        // Assert
        var snapshot = timeline.Snapshot();
        Assert.Empty(snapshot);
    }

    [Fact]
    public void Flush_writes_then_reload_returns_same_entries()
    {
        // Arrange
        var timeline1 = new DropTimeline(_testFilePath);
        timeline1.Record(1000, "Rare", "Sword", "Zone1", "Player1", 12345);
        timeline1.Record(1001, "Magic", "Axe", "Zone2", "Player1", 12346);

        // Act
        timeline1.Flush();

        // Create a new timeline instance to load the data
        var timeline2 = new DropTimeline(_testFilePath);

        // Assert
        var snapshot1 = timeline1.Snapshot();
        var snapshot2 = timeline2.Snapshot();

        Assert.Equal(snapshot1.Count, snapshot2.Count);
        for (int i = 0; i < snapshot1.Count; i++)
        {
            Assert.Equal(snapshot1[i].TimestampSec, snapshot2[i].TimestampSec);
            Assert.Equal(snapshot1[i].Rarity, snapshot2[i].Rarity);
            Assert.Equal(snapshot1[i].Name, snapshot2[i].Name);
            Assert.Equal(snapshot1[i].AreaCode, snapshot2[i].AreaCode);
            Assert.Equal(snapshot1[i].CharacterName, snapshot2[i].CharacterName);
            Assert.Equal(snapshot1[i].EntityId, snapshot2[i].EntityId);
        }
    }
}