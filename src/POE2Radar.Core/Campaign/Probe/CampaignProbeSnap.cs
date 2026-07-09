// v0.22 campaign-probe — spec §4 (world-thread safety), §11 (opt-off = zero cost).
// One tick's read-only slice handed to CampaignProbe.Tick from RadarApp's world thread.
// Value type (readonly record struct) so a nominal-value snap doesn't box the entity list
// / passive vector references — RadarApp materialises the same list references it already
// owns for the SSE snapshot; the probe never copies them. The interface exists to name the
// shape (spec §4.3) so a future Fake/adapter could substitute cleanly.
//
// Zero-alloc contract: CampaignProbe.Tick takes `in CampaignProbeSnap` (concrete type) NOT
// `IWorldSnapshot` so a struct passed through the parameter never boxes. See the deviation
// note in CampaignProbe.Tick — the canonical map lists IWorldSnapshot as the parameter type,
// but honouring that AND the zero-alloc gate is impossible without generics; we keep the
// concrete param and preserve the interface for shape documentation.
using System.Collections.Generic;
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign.Probe;

/// <summary>
/// Read-only world-thread slice consumed by <see cref="CampaignProbe.Tick"/>. Concrete impl
/// is the <see cref="CampaignProbeSnap"/> record struct; the interface exists to name the
/// shape (spec §4.3) so tests / future adapters can substitute cleanly without pulling in the
/// full struct definition.
///
/// <para>All members are references / value types owned by the caller for the duration of the
/// tick — the probe never copies them and never retains them past <see cref="CampaignProbe.Tick"/>
/// returning.</para>
/// </summary>
public interface IWorldSnapshot
{
    /// <summary>GGG area code (e.g. <c>G1_1</c>, <c>C_G1_1</c>, <c>P1_Town</c>). Drives the
    /// zone-change edge for <c>zone_entered</c> and derives the <c>act_hint</c> envelope field.</summary>
    string AreaCode { get; }

    /// <summary>Friendly area name resolved from <see cref="ZoneGuide"/>. Falls back to
    /// <see cref="AreaCode"/> when the ZoneGuide has no entry.</summary>
    string AreaName { get; }

    /// <summary>Stable per-instance hash of the current AreaInstance object. Same instance →
    /// same hash; a re-rolled map / new zone → new hash.</summary>
    uint AreaHash { get; }

    /// <summary>Monster level of the current zone (spec §3 <c>area_level</c>).</summary>
    int AreaLevel { get; }

    /// <summary>True when the current area is flagged as a town in <see cref="ZoneGuide"/>.</summary>
    bool IsTown { get; }

    /// <summary>True when the friendly area name contains "Hideout" (v1 heuristic; upgraded
    /// via a dedicated ZoneGuide flag in a follow-up).</summary>
    bool IsHideout { get; }

    /// <summary>Player grid position on the current world map (spec §3 <c>player_world_pos</c>).</summary>
    WorldPos PlayerWorldPos { get; }

    /// <summary>Local player level (spec §3 character-level fields on multiple events).</summary>
    int CharacterLevel { get; }

    /// <summary>Player current experience widened to <c>long</c> per Task 1 (uint32 upstream).
    /// Consumed by <c>level_up.xp_at_level</c>.</summary>
    long CurrentXp { get; }

    /// <summary>True when the player is alive this tick. Edge to <c>false</c> emits <c>player_death</c>.</summary>
    bool IsPlayerAlive { get; }

    /// <summary>Roots to feed to the UI-tree walker for panel-signature detection. In v1 this is a
    /// single-element list holding the InGameState's UiRoot; the list-of-roots shape leaves room
    /// for a future direct-panel-root cache without changing the probe surface.</summary>
    IReadOnlyList<nint> UiTreeRoots { get; }

    /// <summary>The world-tick entity list. Owned by RadarApp for the tick — the probe iterates it
    /// once and never retains references.</summary>
    IReadOnlyList<Poe2Live.EntityDot> Entities { get; }

    /// <summary>Allocated passive-tree node ids, pre-materialised by RadarApp from Task 1's
    /// <c>Poe2Live.AllocatedPassiveNodeIds</c>. Fires <c>passive_allocated</c> on set-diff.</summary>
    IReadOnlyList<ushort> AllocatedPassiveNodeIds { get; }

    /// <summary>Best-effort metadata path of the entity that most-recently damaged the player, or
    /// <c>null</c> when unknown. Consumed by <c>player_death</c>.</summary>
    string? LastDamageSourceMetadata { get; }

    /// <summary>Cached handle to the current AreaInstance object. Used by Try-out accessors that
    /// hop through server-data (quest flags, passive tree).</summary>
    nint AreaInstance { get; }

    /// <summary>Cached handle to the current InGameState. Used to reach the UI root and hover
    /// tracker via Task 1's <see cref="Poe2Live.HoveredEntityViaTracker"/>.</summary>
    nint InGameState { get; }

    /// <summary>Cached handle to the local player entity.</summary>
    nint LocalPlayer { get; }
}

/// <summary>
/// Concrete snapshot value handed to <see cref="CampaignProbe.Tick"/>. Positional record struct
/// — RadarApp constructs a fresh one per world tick from the same handles / lists it uses to
/// publish the SSE snapshot, so nothing new is allocated on the tick path (the entity list +
/// passive vector are the SAME references, not copies).
/// </summary>
public readonly record struct CampaignProbeSnap(
    nint InGameState,
    nint AreaInstance,
    nint LocalPlayer,
    string AreaCode,
    string AreaName,
    uint AreaHash,
    int AreaLevel,
    bool IsTown,
    bool IsHideout,
    WorldPos PlayerWorldPos,
    int CharacterLevel,
    long CurrentXp,
    bool IsPlayerAlive,
    IReadOnlyList<nint> UiTreeRoots,
    IReadOnlyList<Poe2Live.EntityDot> Entities,
    IReadOnlyList<ushort> AllocatedPassiveNodeIds,
    string? LastDamageSourceMetadata) : IWorldSnapshot;
