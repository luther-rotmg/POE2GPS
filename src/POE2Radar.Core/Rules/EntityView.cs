using System.Collections.Generic;

namespace POE2Radar.Core.Rules;

/// <summary>
/// Lightweight, allocation-cheap view of an entity passed to <see cref="CompiledRuleSet.TryMatch"/>.
/// Populated by the caller (renderer/filter) with only the fields the rules engine needs — this
/// decouples the match path from the render-hot live <c>Entity</c> allocation shape.
/// </summary>
public readonly record struct EntityView(
    string Metadata,           // full path e.g. "Metadata/Monsters/Uniques/Foo"
    string Token,              // last segment
    string Rarity,             // "unique"|"rare"|"magic"|"normal"
    int Level,                 // entity level
    IReadOnlyCollection<string> Buffs);  // active buff names, may be empty

/// <summary>
/// Lightweight view of the world snapshot passed to <see cref="CompiledRuleSet.TryMatch"/>.
/// </summary>
public readonly record struct WorldSnapshotView(
    string ZoneCode,           // area code
    bool InHideout);
