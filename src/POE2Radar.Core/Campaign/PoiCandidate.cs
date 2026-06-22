using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign;

/// <summary>One distinct catalog candidate the overlay has encountered. An entity entry has
/// <see cref="Metadata"/> (and <see cref="LandmarkPath"/> null); a tile entry has
/// <see cref="LandmarkPath"/> (and <see cref="Metadata"/> null, <see cref="Category"/> = "Tile").</summary>
public sealed record SeenPoi(
    string Signature,
    string? Metadata,
    string? LandmarkPath,
    string Category,
    bool Poi,
    string Rarity,
    string FriendlyName,
    string FirstZone,
    int Count,
    System.DateTime LastSeenUtc);

/// <summary>Pure rules for what's worth logging as a Director-objective candidate, and how to
/// dedup it. Allocation-light (enum compares); used per-tick by <c>SeenPoiLog</c>.</summary>
public static class PoiCandidate
{
    /// <summary>Keep POI-flagged entities, uniques, and notable categories (NPC/chest/transition/
    /// object). Skip ordinary monsters, players, and plain "Other" (FX/junk).</summary>
    public static bool IsCandidate(in Poe2Live.EntityDot e)
    {
        if (e.Poi) return true;
        if (e.Rarity == Poe2Live.Rarity.Unique) return true;
        return e.Category is Poe2Live.EntityCategory.Npc
            or Poe2Live.EntityCategory.Chest
            or Poe2Live.EntityCategory.Transition
            or Poe2Live.EntityCategory.Object;
    }

    public static string EntitySignature(in Poe2Live.EntityDot e) => "e:" + e.Metadata;
    public static string LandmarkSignature(Poe2Live.Landmark lm) => "t:" + lm.Path;
}
