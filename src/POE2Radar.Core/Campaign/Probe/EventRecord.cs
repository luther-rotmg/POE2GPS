// v0.22 campaign-probe — spec §3 event schema.
// Envelope + 12 concrete event records + discriminated-union serializer.
// JSON keys are snake_case byte-for-byte per the spec; do NOT rename without a schema_version bump.
// Envelope is INLINED at the top-level of each JSON object (not nested under an "envelope" key)
// so JSONL rows match the wire-format spec §3 verbatim. This is enforced by EventRecordConverter.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Campaign.Probe;

/// <summary>Player world position as {x,y} float pair. Not System.Numerics.Vector2 — the record
/// shape here matches the spec §3 wire-format byte-for-byte via EventRecordConverter.</summary>
public readonly record struct WorldPos(float X, float Y);

/// <summary>Common envelope carried by every probe event. Inlined into JSON at the top level
/// (not nested) by <see cref="EventRecordConverter"/>. <see cref="ProbeCapability"/> ships as
/// <c>"live"</c> for all 12 events at v1; the field remains for forward-compat schema evolution.
/// <see cref="SchemaVersion"/> starts at 1 and only bumps on breaking wire-format changes.</summary>
public readonly record struct EventEnvelope(
    long TsEpochMs,
    string InstallUuid,
    string BootId,
    string EventType,
    string ProbeCapability,
    int SchemaVersion,
    string ActHint,
    string AreaName);

/// <summary>Abstract base for the 12 concrete probe events. Reference type required for
/// polymorphic discriminated-union serialization via <see cref="EventRecordConverter"/>.</summary>
[JsonConverter(typeof(EventRecordConverter))]
public abstract record EventRecord(EventEnvelope Envelope);

public sealed record ZoneEnteredEvent(
    EventEnvelope Envelope,
    int AreaLevel,
    string AreaIdHash,
    bool IsTown,
    bool IsHideout,
    WorldPos PlayerWorldPos) : EventRecord(Envelope);

public sealed record AreaTransitionUsedEvent(
    EventEnvelope Envelope,
    string SourceArea,
    string DestinationArea,
    string TransitionEntityMetadataPath,
    WorldPos TransitionWorldPos) : EventRecord(Envelope);

public sealed record BossEncounteredEvent(
    EventEnvelope Envelope,
    string BossMetadataPath,
    string BossDisplayName,
    WorldPos BossWorldPos,
    bool IsFirstEncounter) : EventRecord(Envelope);

public sealed record CheckpointTouchedEvent(
    EventEnvelope Envelope,
    string CheckpointMetadataPath,
    WorldPos WorldPos) : EventRecord(Envelope);

public sealed record WaypointUnlockedEvent(
    EventEnvelope Envelope,
    string WaypointEntityMetadataPath,
    WorldPos WorldPos) : EventRecord(Envelope);

/// <summary><see cref="LastDamageSourceMetadataPath"/> is nullable — the last damage source
/// may be unknown when the player dies to a delayed effect (bleed, poison) after the source
/// entity has already been despawned. JSON emits <c>null</c> in that case.</summary>
public sealed record PlayerDeathEvent(
    EventEnvelope Envelope,
    string? LastDamageSourceMetadataPath,
    int CharacterLevel) : EventRecord(Envelope);

public sealed record WaypointTravelEvent(
    EventEnvelope Envelope,
    string SourceArea,
    string DestinationArea,
    int WaypointMenuRowIndex) : EventRecord(Envelope);

public sealed record NpcDialogueStartedEvent(
    EventEnvelope Envelope,
    string NpcNameHash,
    string NpcMetadataPath,
    WorldPos NpcWorldPos,
    string DialogueTextHash,
    int OptionCount) : EventRecord(Envelope);

public sealed record NpcDialogueOptionSelectedEvent(
    EventEnvelope Envelope,
    string NpcNameHash,
    int OptionIndex,
    string OptionTextHash,
    int RemainingOptionCount) : EventRecord(Envelope);

public sealed record QuestRewardSelectedEvent(
    EventEnvelope Envelope,
    string RewardMetadataPath,
    string RewardDisplayNameHash,
    int OfferIndex,
    int TotalOffers,
    bool WasSkipped) : EventRecord(Envelope);

public sealed record PassiveAllocatedEvent(
    EventEnvelope Envelope,
    int NodeId,
    string NodeDisplayNameHash,
    int CharacterLevel) : EventRecord(Envelope);

public sealed record LevelUpEvent(
    EventEnvelope Envelope,
    int NewLevel,
    long XpAtLevel,
    string AreaNameWhenLeveled) : EventRecord(Envelope);

/// <summary>Discriminated-union serializer. Public API for PROBE-WRITER (JSONL sink),
/// PROBE-CORE (world-thread emitter), PROBE-CONTRIBUTE (payload build), and round-trip tests.
/// Delegates to <see cref="EventRecordConverter"/>, which inlines envelope fields at top-level
/// and dispatches concrete extra-field schemas on runtime type / event_type discriminator.</summary>
public static class EventRecordJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new EventRecordConverter() },
    };

    /// <summary>Serialize any <see cref="EventRecord"/> to a single-line JSON object matching
    /// spec §3 wire-format. No trailing newline — the caller (PROBE-WRITER) appends <c>\n</c> for JSONL.</summary>
    public static string Serialize(EventRecord record)
        => JsonSerializer.Serialize(record, typeof(EventRecord), Options);

    /// <summary>Deserialize a spec §3 JSON object to a concrete <see cref="EventRecord"/> subtype.
    /// The <c>event_type</c> field in the JSON discriminates; a mismatch against <typeparamref name="T"/>
    /// throws <see cref="JsonException"/>.</summary>
    public static T Deserialize<T>(string line) where T : EventRecord
        => (T)(JsonSerializer.Deserialize(line, typeof(T), Options)
               ?? throw new JsonException($"null deserialization result for {typeof(T).Name}"));
}

/// <summary>Custom converter that inlines envelope fields into the top-level JSON object
/// (matching the spec §3 flat shape) and dispatches concrete extra-fields on the runtime type
/// (serialize) or on the <c>event_type</c> discriminator (deserialize).
/// Snake_case JSON keys are byte-for-byte per spec §3 — any drift is a shipping-contract bug.</summary>
internal sealed class EventRecordConverter : JsonConverter<EventRecord>
{
    public override bool CanConvert(Type typeToConvert)
        => typeof(EventRecord).IsAssignableFrom(typeToConvert);

    public override EventRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var envelope = ReadEnvelope(root);

        EventRecord result = envelope.EventType switch
        {
            "zone_entered" => new ZoneEnteredEvent(
                envelope,
                GetInt(root, "area_level"),
                GetString(root, "area_id_hash"),
                GetBool(root, "is_town"),
                GetBool(root, "is_hideout"),
                ReadWorldPos(root.GetProperty("player_world_pos"))),

            "area_transition_used" => new AreaTransitionUsedEvent(
                envelope,
                GetString(root, "source_area"),
                GetString(root, "destination_area"),
                GetString(root, "transition_entity_metadata_path"),
                ReadWorldPos(root.GetProperty("transition_world_pos"))),

            "boss_encountered" => new BossEncounteredEvent(
                envelope,
                GetString(root, "boss_metadata_path"),
                GetString(root, "boss_display_name"),
                ReadWorldPos(root.GetProperty("boss_world_pos")),
                GetBool(root, "is_first_encounter")),

            "checkpoint_touched" => new CheckpointTouchedEvent(
                envelope,
                GetString(root, "checkpoint_metadata_path"),
                ReadWorldPos(root.GetProperty("world_pos"))),

            "waypoint_unlocked" => new WaypointUnlockedEvent(
                envelope,
                GetString(root, "waypoint_entity_metadata_path"),
                ReadWorldPos(root.GetProperty("world_pos"))),

            "player_death" => new PlayerDeathEvent(
                envelope,
                GetNullableString(root, "last_damage_source_metadata_path"),
                GetInt(root, "character_level")),

            "waypoint_travel" => new WaypointTravelEvent(
                envelope,
                GetString(root, "source_area"),
                GetString(root, "destination_area"),
                GetInt(root, "waypoint_menu_row_index")),

            "npc_dialogue_started" => new NpcDialogueStartedEvent(
                envelope,
                GetString(root, "npc_name_hash"),
                GetString(root, "npc_metadata_path"),
                ReadWorldPos(root.GetProperty("npc_world_pos")),
                GetString(root, "dialogue_text_hash"),
                GetInt(root, "option_count")),

            "npc_dialogue_option_selected" => new NpcDialogueOptionSelectedEvent(
                envelope,
                GetString(root, "npc_name_hash"),
                GetInt(root, "option_index"),
                GetString(root, "option_text_hash"),
                GetInt(root, "remaining_option_count")),

            "quest_reward_selected" => new QuestRewardSelectedEvent(
                envelope,
                GetString(root, "reward_metadata_path"),
                GetString(root, "reward_display_name_hash"),
                GetInt(root, "offer_index"),
                GetInt(root, "total_offers"),
                GetBool(root, "was_skipped")),

            "passive_allocated" => new PassiveAllocatedEvent(
                envelope,
                GetInt(root, "node_id"),
                GetString(root, "node_display_name_hash"),
                GetInt(root, "character_level")),

            "level_up" => new LevelUpEvent(
                envelope,
                GetInt(root, "new_level"),
                GetLong(root, "xp_at_level"),
                GetString(root, "area_name_when_leveled")),

            _ => throw new JsonException($"Unknown event_type '{envelope.EventType}'"),
        };

        // Sanity — if the caller asked for a concrete subtype but the JSON was another shape, refuse.
        if (typeToConvert != typeof(EventRecord) && !typeToConvert.IsInstanceOfType(result))
            throw new JsonException(
                $"event_type '{envelope.EventType}' produced {result.GetType().Name}, "
                + $"but caller requested {typeToConvert.Name}.");

        return result;
    }

    public override void Write(Utf8JsonWriter writer, EventRecord value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteEnvelope(writer, value.Envelope);

        switch (value)
        {
            case ZoneEnteredEvent z:
                writer.WriteNumber("area_level", z.AreaLevel);
                writer.WriteString("area_id_hash", z.AreaIdHash);
                writer.WriteBoolean("is_town", z.IsTown);
                writer.WriteBoolean("is_hideout", z.IsHideout);
                writer.WritePropertyName("player_world_pos");
                WriteWorldPos(writer, z.PlayerWorldPos);
                break;

            case AreaTransitionUsedEvent a:
                writer.WriteString("source_area", a.SourceArea);
                writer.WriteString("destination_area", a.DestinationArea);
                writer.WriteString("transition_entity_metadata_path", a.TransitionEntityMetadataPath);
                writer.WritePropertyName("transition_world_pos");
                WriteWorldPos(writer, a.TransitionWorldPos);
                break;

            case BossEncounteredEvent b:
                writer.WriteString("boss_metadata_path", b.BossMetadataPath);
                writer.WriteString("boss_display_name", b.BossDisplayName);
                writer.WritePropertyName("boss_world_pos");
                WriteWorldPos(writer, b.BossWorldPos);
                writer.WriteBoolean("is_first_encounter", b.IsFirstEncounter);
                break;

            case CheckpointTouchedEvent c:
                writer.WriteString("checkpoint_metadata_path", c.CheckpointMetadataPath);
                writer.WritePropertyName("world_pos");
                WriteWorldPos(writer, c.WorldPos);
                break;

            case WaypointUnlockedEvent wu:
                writer.WriteString("waypoint_entity_metadata_path", wu.WaypointEntityMetadataPath);
                writer.WritePropertyName("world_pos");
                WriteWorldPos(writer, wu.WorldPos);
                break;

            case PlayerDeathEvent pd:
                if (pd.LastDamageSourceMetadataPath is null)
                    writer.WriteNull("last_damage_source_metadata_path");
                else
                    writer.WriteString("last_damage_source_metadata_path", pd.LastDamageSourceMetadataPath);
                writer.WriteNumber("character_level", pd.CharacterLevel);
                break;

            case WaypointTravelEvent wt:
                writer.WriteString("source_area", wt.SourceArea);
                writer.WriteString("destination_area", wt.DestinationArea);
                writer.WriteNumber("waypoint_menu_row_index", wt.WaypointMenuRowIndex);
                break;

            case NpcDialogueStartedEvent nds:
                writer.WriteString("npc_name_hash", nds.NpcNameHash);
                writer.WriteString("npc_metadata_path", nds.NpcMetadataPath);
                writer.WritePropertyName("npc_world_pos");
                WriteWorldPos(writer, nds.NpcWorldPos);
                writer.WriteString("dialogue_text_hash", nds.DialogueTextHash);
                writer.WriteNumber("option_count", nds.OptionCount);
                break;

            case NpcDialogueOptionSelectedEvent nos:
                writer.WriteString("npc_name_hash", nos.NpcNameHash);
                writer.WriteNumber("option_index", nos.OptionIndex);
                writer.WriteString("option_text_hash", nos.OptionTextHash);
                writer.WriteNumber("remaining_option_count", nos.RemainingOptionCount);
                break;

            case QuestRewardSelectedEvent qr:
                writer.WriteString("reward_metadata_path", qr.RewardMetadataPath);
                writer.WriteString("reward_display_name_hash", qr.RewardDisplayNameHash);
                writer.WriteNumber("offer_index", qr.OfferIndex);
                writer.WriteNumber("total_offers", qr.TotalOffers);
                writer.WriteBoolean("was_skipped", qr.WasSkipped);
                break;

            case PassiveAllocatedEvent pa:
                writer.WriteNumber("node_id", pa.NodeId);
                writer.WriteString("node_display_name_hash", pa.NodeDisplayNameHash);
                writer.WriteNumber("character_level", pa.CharacterLevel);
                break;

            case LevelUpEvent lu:
                writer.WriteNumber("new_level", lu.NewLevel);
                writer.WriteNumber("xp_at_level", lu.XpAtLevel);
                writer.WriteString("area_name_when_leveled", lu.AreaNameWhenLeveled);
                break;

            default:
                throw new JsonException($"Unsupported EventRecord subtype {value.GetType().Name}");
        }

        writer.WriteEndObject();
    }

    // ── Envelope helpers ─────────────────────────────────────────────────────

    private static void WriteEnvelope(Utf8JsonWriter writer, EventEnvelope e)
    {
        writer.WriteNumber("ts_epoch_ms", e.TsEpochMs);
        writer.WriteString("install_uuid", e.InstallUuid);
        writer.WriteString("boot_id", e.BootId);
        writer.WriteString("event_type", e.EventType);
        writer.WriteString("probe_capability", e.ProbeCapability);
        writer.WriteNumber("schema_version", e.SchemaVersion);
        writer.WriteString("act_hint", e.ActHint);
        writer.WriteString("area_name", e.AreaName);
    }

    private static EventEnvelope ReadEnvelope(JsonElement root)
        => new(
            TsEpochMs:       GetLong(root,   "ts_epoch_ms"),
            InstallUuid:     GetString(root, "install_uuid"),
            BootId:          GetString(root, "boot_id"),
            EventType:       GetString(root, "event_type"),
            ProbeCapability: GetString(root, "probe_capability"),
            SchemaVersion:   GetInt(root,    "schema_version"),
            ActHint:         GetString(root, "act_hint"),
            AreaName:        GetString(root, "area_name"));

    // ── WorldPos helpers ─────────────────────────────────────────────────────

    private static void WriteWorldPos(Utf8JsonWriter writer, WorldPos p)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", p.X);
        writer.WriteNumber("y", p.Y);
        writer.WriteEndObject();
    }

    private static WorldPos ReadWorldPos(JsonElement el)
        => new(el.GetProperty("x").GetSingle(), el.GetProperty("y").GetSingle());

    // ── Primitive helpers ────────────────────────────────────────────────────

    private static string GetString(JsonElement root, string key)
        => root.GetProperty(key).GetString() ?? "";

    private static string? GetNullableString(JsonElement root, string key)
    {
        var el = root.GetProperty(key);
        return el.ValueKind == JsonValueKind.Null ? null : el.GetString();
    }

    private static int  GetInt(JsonElement root, string key)  => root.GetProperty(key).GetInt32();
    private static long GetLong(JsonElement root, string key) => root.GetProperty(key).GetInt64();
    private static bool GetBool(JsonElement root, string key) => root.GetProperty(key).GetBoolean();
}
