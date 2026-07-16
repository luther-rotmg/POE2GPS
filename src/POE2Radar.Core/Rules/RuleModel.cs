using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Rules;

/// <summary>
/// v0.39 Rules Engine: on-disk rules file envelope.
/// </summary>
public sealed record RulesFile(int SchemaVersion, IReadOnlyList<RuleRecord> Rules);

/// <summary>
/// A single rule: an id, user-facing metadata, a selector (when), and a list of effects (then).
/// </summary>
public sealed record RuleRecord(
    Guid Id,
    string Name,
    int Priority,
    bool Enabled,
    Selector When,
    IReadOnlyList<Effect> Then);

/// <summary>
/// Selector predicates for matching entities. All fields are optional; an empty selector
/// matches everything. Fields are AND-ed together (v1, flat AND, no nesting).
/// </summary>
public sealed record Selector(
    string? Metadata,
    string? Token,
    string? Rarity,
    string? ZoneCode,
    bool? InHideout,
    int? MinLevel,
    int? MaxLevel,
    string? HasBuff);

/// <summary>
/// Polymorphic base for all effect kinds. The <see cref="Kind"/> discriminator rides on the
/// wire as a <c>kind</c> field via System.Text.Json's polymorphic serializer.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HideEffect), "hide")]
[JsonDerivedType(typeof(TintEffect), "tint")]
[JsonDerivedType(typeof(RingEffect), "ring")]
[JsonDerivedType(typeof(LabelEffect), "label")]
[JsonDerivedType(typeof(SoundEffect), "sound")]
[JsonDerivedType(typeof(PulseEffect), "pulse")]
public abstract record Effect([property: JsonIgnore] string Kind);

/// <summary>Hides the entity from the overlay entirely.</summary>
public sealed record HideEffect() : Effect("hide");

/// <summary>Tints the entity dot with the given hex color.</summary>
public sealed record TintEffect(string Color) : Effect("tint");

/// <summary>Draws a ring around the entity with the given hex color.</summary>
public sealed record RingEffect(string Color) : Effect("ring");

/// <summary>Draws a text label above the entity.</summary>
public sealed record LabelEffect(string Text) : Effect("label");

/// <summary>Plays a sound file from the config/sounds/ directory.</summary>
public sealed record SoundEffect(string File) : Effect("sound");

/// <summary>Pulses the entity dot at the given speed.</summary>
public sealed record PulseEffect(string Speed) : Effect("pulse");