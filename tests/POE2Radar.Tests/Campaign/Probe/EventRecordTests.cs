using System.Text.Json;
using POE2Radar.Core.Campaign.Probe;

namespace POE2Radar.Tests.Campaign.Probe;

// v0.22 campaign-probe — spec §3 event schema is the shipping wire-format.
// Snake_case JSON keys are byte-for-byte per the spec; any rename requires a schema_version bump.
// Envelope is INLINED at top-level of each JSONL row (not nested) — asserted below.
public class EventRecordTests
{
    private static EventEnvelope SampleEnvelope(string eventType, string capability = "live") => new(
        TsEpochMs:       1_720_400_000_000L,
        InstallUuid:     "00000000-0000-4000-8000-000000000001",
        BootId:          "00000000-0000-4000-8000-000000000002",
        EventType:       eventType,
        ProbeCapability: capability,
        SchemaVersion:   1,
        ActHint:         "act1",
        AreaName:        "The Riverbank");

    private static ZoneEnteredEvent SampleZoneEntered() => new(
        Envelope:       SampleEnvelope("zone_entered"),
        AreaLevel:      2,
        AreaIdHash:     "abcdef0123456789",
        IsTown:         false,
        IsHideout:      false,
        PlayerWorldPos: new WorldPos(120.5f, -45.25f));

    [Fact]
    public void ZoneEntered_serializes_all_snake_case_keys()
    {
        var json = EventRecordJson.Serialize(SampleZoneEntered());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("zone_entered", root.GetProperty("event_type").GetString());
        Assert.Equal(1_720_400_000_000L, root.GetProperty("ts_epoch_ms").GetInt64());
        Assert.Equal("00000000-0000-4000-8000-000000000001", root.GetProperty("install_uuid").GetString());
        Assert.Equal("00000000-0000-4000-8000-000000000002", root.GetProperty("boot_id").GetString());
        Assert.Equal("live", root.GetProperty("probe_capability").GetString());
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("act1", root.GetProperty("act_hint").GetString());
        Assert.Equal("The Riverbank", root.GetProperty("area_name").GetString());
        Assert.Equal(2, root.GetProperty("area_level").GetInt32());
        Assert.Equal("abcdef0123456789", root.GetProperty("area_id_hash").GetString());
        Assert.False(root.GetProperty("is_town").GetBoolean());
        Assert.False(root.GetProperty("is_hideout").GetBoolean());
        var pos = root.GetProperty("player_world_pos");
        Assert.Equal(120.5f, pos.GetProperty("x").GetSingle());
        Assert.Equal(-45.25f, pos.GetProperty("y").GetSingle());
    }

    [Fact]
    public void Envelope_is_inlined_at_top_level_not_nested()
    {
        var json = EventRecordJson.Serialize(SampleZoneEntered());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Spec §3: envelope fields sit at the top level alongside event-specific fields — no wrapper.
        Assert.False(root.TryGetProperty("envelope", out _),
            "envelope must be inlined at top level, not nested under an 'envelope' key");

        // Every envelope key present at top level.
        foreach (var key in new[]
                 {
                     "ts_epoch_ms", "install_uuid", "boot_id", "event_type",
                     "probe_capability", "schema_version", "act_hint", "area_name",
                 })
        {
            Assert.True(root.TryGetProperty(key, out _), $"envelope key '{key}' must live at top level");
        }
    }

    [Fact]
    public void WorldPos_serializes_as_lowercase_xy_object()
    {
        var evt = new ZoneEnteredEvent(
            SampleEnvelope("zone_entered"), 1, "0000000000000000", false, false, new WorldPos(1.5f, 2.5f));
        using var doc = JsonDocument.Parse(EventRecordJson.Serialize(evt));
        var pos = doc.RootElement.GetProperty("player_world_pos");
        Assert.Equal(JsonValueKind.Object, pos.ValueKind);
        Assert.Equal(1.5f, pos.GetProperty("x").GetSingle());
        Assert.Equal(2.5f, pos.GetProperty("y").GetSingle());
    }

    [Fact]
    public void ZoneEntered_round_trips()
    {
        var src = SampleZoneEntered();
        var back = EventRecordJson.Deserialize<ZoneEnteredEvent>(EventRecordJson.Serialize(src));
        Assert.Equal(src, back);
    }

    [Fact]
    public void All_12_event_types_expose_expected_event_type_string()
    {
        var pairs = new (EventRecord Sample, string Expected)[]
        {
            (new ZoneEnteredEvent(SampleEnvelope("zone_entered"),
                                   1, "0000000000000000", false, false, new WorldPos(0, 0)),
                "zone_entered"),

            (new AreaTransitionUsedEvent(SampleEnvelope("area_transition_used"),
                                   "A", "B", "Metadata/Terrain/Transition", new WorldPos(0, 0)),
                "area_transition_used"),

            (new BossEncounteredEvent(SampleEnvelope("boss_encountered"),
                                   "Metadata/Monsters/Boss/X", "The Boss", new WorldPos(0, 0), true),
                "boss_encountered"),

            (new CheckpointTouchedEvent(SampleEnvelope("checkpoint_touched"),
                                   "Metadata/Terrain/Checkpoint", new WorldPos(0, 0)),
                "checkpoint_touched"),

            (new WaypointUnlockedEvent(SampleEnvelope("waypoint_unlocked"),
                                   "Metadata/Terrain/Waypoint", new WorldPos(0, 0)),
                "waypoint_unlocked"),

            (new PlayerDeathEvent(SampleEnvelope("player_death"),
                                   "Metadata/Monsters/Attacker", 12),
                "player_death"),

            (new WaypointTravelEvent(SampleEnvelope("waypoint_travel"),
                                   "A", "B", 0),
                "waypoint_travel"),

            (new NpcDialogueStartedEvent(SampleEnvelope("npc_dialogue_started"),
                                   "abcdef0123456789", "Metadata/Npc/X",
                                   new WorldPos(0, 0), "fedcba9876543210", 3),
                "npc_dialogue_started"),

            (new NpcDialogueOptionSelectedEvent(SampleEnvelope("npc_dialogue_option_selected"),
                                   "abcdef0123456789", 1, "1111222233334444", 2),
                "npc_dialogue_option_selected"),

            (new QuestRewardSelectedEvent(SampleEnvelope("quest_reward_selected"),
                                   "Metadata/Items/Ring", "aaaabbbbccccdddd", 0, 3, false),
                "quest_reward_selected"),

            (new PassiveAllocatedEvent(SampleEnvelope("passive_allocated"),
                                   1, "eeeeffff00001111", 15),
                "passive_allocated"),

            (new LevelUpEvent(SampleEnvelope("level_up"),
                                   20, 1_234_567L, "The Marketplace"),
                "level_up"),
        };
        Assert.Equal(12, pairs.Length);
        foreach (var (sample, expected) in pairs)
        {
            using var doc = JsonDocument.Parse(EventRecordJson.Serialize(sample));
            Assert.Equal(expected, doc.RootElement.GetProperty("event_type").GetString());
        }
    }

    [Fact]
    public void PlayerDeath_null_damage_source_serializes_as_json_null()
    {
        var evt = new PlayerDeathEvent(
            SampleEnvelope("player_death"),
            LastDamageSourceMetadataPath: null,
            CharacterLevel: 15);
        using var doc = JsonDocument.Parse(EventRecordJson.Serialize(evt));
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("last_damage_source_metadata_path").ValueKind);
        Assert.Equal(15, doc.RootElement.GetProperty("character_level").GetInt32());
    }

    [Fact]
    public void PlayerDeath_null_damage_source_round_trips()
    {
        var src = new PlayerDeathEvent(SampleEnvelope("player_death"), null, 15);
        var back = EventRecordJson.Deserialize<PlayerDeathEvent>(EventRecordJson.Serialize(src));
        Assert.Equal(src, back);
        Assert.Null(back.LastDamageSourceMetadataPath);
    }

    [Fact]
    public void LevelUp_pending_capability_preserved_in_envelope()
    {
        // spec §3 note: probe_capability envelope field can carry "v0.22_pending" for stub emissions.
        var evt = new LevelUpEvent(
            Envelope:            SampleEnvelope("level_up", capability: "v0.22_pending"),
            NewLevel:            20,
            XpAtLevel:           0L,
            AreaNameWhenLeveled: "The Marketplace");
        using var doc = JsonDocument.Parse(EventRecordJson.Serialize(evt));
        Assert.Equal("v0.22_pending", doc.RootElement.GetProperty("probe_capability").GetString());
        Assert.Equal(20, doc.RootElement.GetProperty("new_level").GetInt32());
        Assert.Equal(0L, doc.RootElement.GetProperty("xp_at_level").GetInt64());
        Assert.Equal("The Marketplace", doc.RootElement.GetProperty("area_name_when_leveled").GetString());
    }

    [Fact]
    public void All_12_events_round_trip_byte_for_byte()
    {
        var samples = new EventRecord[]
        {
            new ZoneEnteredEvent(SampleEnvelope("zone_entered"),
                                   3, "aaaa000000000000", true, false, new WorldPos(1f, 2f)),
            new AreaTransitionUsedEvent(SampleEnvelope("area_transition_used"),
                                   "The Riverbank", "Clearfell",
                                   "Metadata/Terrain/Act1/Transition", new WorldPos(3f, 4f)),
            new BossEncounteredEvent(SampleEnvelope("boss_encountered"),
                                   "Metadata/Monsters/Boss/BloatedMiller", "The Bloated Miller",
                                   new WorldPos(5f, 6f), true),
            new CheckpointTouchedEvent(SampleEnvelope("checkpoint_touched"),
                                   "Metadata/Terrain/Checkpoint", new WorldPos(7f, 8f)),
            new WaypointUnlockedEvent(SampleEnvelope("waypoint_unlocked"),
                                   "Metadata/Terrain/Waypoint", new WorldPos(9f, 10f)),
            new PlayerDeathEvent(SampleEnvelope("player_death"),
                                   "Metadata/Monsters/BeastOfTheField", 12),
            new WaypointTravelEvent(SampleEnvelope("waypoint_travel"),
                                   "Clearfell", "Ogham Farmlands", 3),
            new NpcDialogueStartedEvent(SampleEnvelope("npc_dialogue_started"),
                                   "abcdef0123456789", "Metadata/Npc/Renly",
                                   new WorldPos(11f, 12f), "fedcba9876543210", 4),
            new NpcDialogueOptionSelectedEvent(SampleEnvelope("npc_dialogue_option_selected"),
                                   "abcdef0123456789", 1, "1111222233334444", 3),
            new QuestRewardSelectedEvent(SampleEnvelope("quest_reward_selected"),
                                   "Metadata/Items/Ring", "aaaabbbbccccdddd", 0, 3, false),
            new PassiveAllocatedEvent(SampleEnvelope("passive_allocated"),
                                   12345, "eeeeffff00001111", 15),
            new LevelUpEvent(SampleEnvelope("level_up"),
                                   20, 1_234_567L, "The Marketplace"),
        };
        Assert.Equal(12, samples.Length);

        foreach (var sample in samples)
        {
            var json = EventRecordJson.Serialize(sample);
            var back = (EventRecord)typeof(EventRecordJson)
                .GetMethod(nameof(EventRecordJson.Deserialize))!
                .MakeGenericMethod(sample.GetType())
                .Invoke(null, new object[] { json })!;
            Assert.Equal(sample, back);
        }
    }

    [Fact]
    public void Snake_case_extra_field_keys_match_spec_section_3_byte_for_byte()
    {
        // One assertion per (event_type, extra_field) pair, transcribed from spec §3 table.
        // Any rename here is a shipping-contract bug — bump schema_version if it must change.
        var expectations = new (EventRecord Sample, string[] ExpectedExtraKeys)[]
        {
            (new ZoneEnteredEvent(SampleEnvelope("zone_entered"),
                    1, "0", false, false, new WorldPos(0, 0)),
                new[] { "area_level", "area_id_hash", "is_town", "is_hideout", "player_world_pos" }),

            (new AreaTransitionUsedEvent(SampleEnvelope("area_transition_used"),
                    "", "", "", new WorldPos(0, 0)),
                new[] { "source_area", "destination_area", "transition_entity_metadata_path", "transition_world_pos" }),

            (new BossEncounteredEvent(SampleEnvelope("boss_encountered"),
                    "", "", new WorldPos(0, 0), false),
                new[] { "boss_metadata_path", "boss_display_name", "boss_world_pos", "is_first_encounter" }),

            (new CheckpointTouchedEvent(SampleEnvelope("checkpoint_touched"),
                    "", new WorldPos(0, 0)),
                new[] { "checkpoint_metadata_path", "world_pos" }),

            (new WaypointUnlockedEvent(SampleEnvelope("waypoint_unlocked"),
                    "", new WorldPos(0, 0)),
                new[] { "waypoint_entity_metadata_path", "world_pos" }),

            (new PlayerDeathEvent(SampleEnvelope("player_death"),
                    "", 0),
                new[] { "last_damage_source_metadata_path", "character_level" }),

            (new WaypointTravelEvent(SampleEnvelope("waypoint_travel"),
                    "", "", 0),
                new[] { "source_area", "destination_area", "waypoint_menu_row_index" }),

            (new NpcDialogueStartedEvent(SampleEnvelope("npc_dialogue_started"),
                    "", "", new WorldPos(0, 0), "", 0),
                new[] { "npc_name_hash", "npc_metadata_path", "npc_world_pos", "dialogue_text_hash", "option_count" }),

            (new NpcDialogueOptionSelectedEvent(SampleEnvelope("npc_dialogue_option_selected"),
                    "", 0, "", 0),
                new[] { "npc_name_hash", "option_index", "option_text_hash", "remaining_option_count" }),

            (new QuestRewardSelectedEvent(SampleEnvelope("quest_reward_selected"),
                    "", "", 0, 0, false),
                new[] { "reward_metadata_path", "reward_display_name_hash", "offer_index", "total_offers", "was_skipped" }),

            (new PassiveAllocatedEvent(SampleEnvelope("passive_allocated"),
                    0, "", 0),
                new[] { "node_id", "node_display_name_hash", "character_level" }),

            (new LevelUpEvent(SampleEnvelope("level_up"),
                    0, 0L, ""),
                new[] { "new_level", "xp_at_level", "area_name_when_leveled" }),
        };

        Assert.Equal(12, expectations.Length);
        foreach (var (sample, expectedKeys) in expectations)
        {
            using var doc = JsonDocument.Parse(EventRecordJson.Serialize(sample));
            foreach (var key in expectedKeys)
            {
                Assert.True(doc.RootElement.TryGetProperty(key, out _),
                    $"{sample.GetType().Name} missing extra field '{key}' (spec §3 drift)");
            }
        }
    }
}
