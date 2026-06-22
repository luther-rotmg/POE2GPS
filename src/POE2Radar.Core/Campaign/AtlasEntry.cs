using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign;

/// <summary>One distinct entity in the Atlas census: a metadata path the overlay has encountered,
/// with how it was categorized, how often, and where first seen. Naming and objective-classification
/// are derived at read time (resolver hit / catalog coverage), never stored here.</summary>
public sealed record AtlasEntry(
    string Metadata,
    string Category,
    string Rarity,
    bool Poi,
    string FirstZone,
    int Count,
    System.DateTime FirstSeenUtc,
    System.DateTime LastSeenUtc);

/// <summary>Pure rules for which entities belong in the full Atlas census and how to dedup them.
/// Allocation-light (enum compares + substring); used per-tick by <c>EntityAtlasLog</c>.</summary>
public static class AtlasCensus
{
    /// <summary>Keep every real entity EXCEPT the local/party player and <see cref="JunkFilter"/>
    /// noise (FX / audio / daemon / MTX / clone / attachment nodes). Ordinary monsters ARE kept —
    /// the Atlas names everything, not just objective candidates.</summary>
    public static bool IsCensusEntity(in Poe2Live.EntityDot e)
    {
        if (e.Category == Poe2Live.EntityCategory.Player) return false;
        if (string.IsNullOrEmpty(e.Metadata)) return false;
        return !JunkFilter.IsJunk(e.Metadata);
    }

    /// <summary>Dedup key = the metadata path with any trailing runtime "@&lt;level&gt;" annotation
    /// stripped (so a monster at @34 and @45 collapse to one census entry, matching how
    /// <see cref="EntityNameResolver"/> keys names).</summary>
    public static string Signature(in Poe2Live.EntityDot e)
    {
        var m = e.Metadata;
        var at = m.IndexOf('@');
        return at >= 0 ? m[..at] : m;
    }
}
