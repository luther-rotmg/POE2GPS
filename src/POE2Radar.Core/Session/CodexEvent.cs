using System.Text.Json.Serialization;

namespace POE2Radar.Core.Session;

/// <summary>
/// v0.37 Book of the Exile: per-character on-disk event journal.
/// Polymorphic base for the four event kinds the codex persists. Serialized as one
/// JSON object per line (JSONL) so each Append is a single append-only write.
/// The Kind discriminator is set by the base ctor call in each subtype and rides on
/// the wire as a `kind` field via System.Text.Json's polymorphic serializer.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LevelUpEvent),     "level")]
[JsonDerivedType(typeof(BossKillEvent),    "boss")]
[JsonDerivedType(typeof(DeathEvent),       "death")]
[JsonDerivedType(typeof(NotableDropEvent), "drop")]
public abstract record CodexEvent(long Ts, [property: JsonIgnore] string Kind, uint AreaHash, string Zone);

public sealed record LevelUpEvent(long Ts, uint AreaHash, string Zone, int Level)
    : CodexEvent(Ts, "level", AreaHash, Zone);

public sealed record BossKillEvent(long Ts, uint AreaHash, string Zone, string BossKey, string BossLabel)
    : CodexEvent(Ts, "boss", AreaHash, Zone);

public sealed record DeathEvent(long Ts, uint AreaHash, string Zone, int AreaLevel, int PlayerLevel)
    : CodexEvent(Ts, "death", AreaHash, Zone);

public sealed record NotableDropEvent(long Ts, uint AreaHash, string Zone, string Name, string Rarity, string? Art)
    : CodexEvent(Ts, "drop", AreaHash, Zone);
