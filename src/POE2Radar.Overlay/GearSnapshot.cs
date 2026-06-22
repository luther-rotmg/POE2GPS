using POE2Radar.Core.Gear;

namespace POE2Radar.Overlay;

/// <summary>One scored inventory item for the Gear tab (no identifying data — item stats only).</summary>
public sealed record ScoredItem(
    string Name,
    string Rarity,
    bool Identified,
    int InventoryId,
    double Score,
    bool IsGodRoll,
    IReadOnlyList<AffixContribution> Affixes);

/// <summary>Immutable, lock-free-published snapshot of the scored inventory (God-Roll Detector).
/// Null/empty when the feature is off.</summary>
public sealed record GearSnapshot(IReadOnlyList<ScoredItem> Items);
