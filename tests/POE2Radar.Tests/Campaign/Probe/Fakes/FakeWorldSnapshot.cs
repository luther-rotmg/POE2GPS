// v0.22 campaign-probe — PROBE-TESTS §9 fixture.
// Concrete IWorldSnapshot implementation used by the integration tests. Mutable so a single
// instance can be flipped between ticks to drive every diff-observer edge (zone change, level
// edge, passive set diff, dialog panel open, etc.) without allocating a new snapshot each time.
//
// CampaignProbe.Tick takes an `in CampaignProbeSnap` value (concrete struct, by ref) — the
// interface exists to name the world-thread contract, but the probe consumes the struct so
// zero-alloc-when-off holds. ToSnap() bridges the fixture to the struct the probe wants; the
// interface impl proves this fake honours every member the shipped IWorldSnapshot exposes.
namespace POE2Radar.Tests.Campaign.Probe.Fakes;

using System.Collections.Generic;
using POE2Radar.Core.Campaign.Probe;
using POE2Radar.Core.Game;

/// <summary>Mutable <see cref="IWorldSnapshot"/> fixture for probe integration tests. Every
/// property is <c>{ get; set; }</c> so a single instance can be reused across ticks — the probe
/// re-reads state each call and the diff observers key off value comparisons, not reference
/// identity. Call <see cref="ToSnap"/> to materialise the <see cref="CampaignProbeSnap"/> value
/// the probe consumes.</summary>
public sealed class FakeWorldSnapshot : IWorldSnapshot
{
    public string AreaCode { get; set; } = "G1_1";
    public string AreaName { get; set; } = "The Riverbank";
    public uint AreaHash { get; set; } = 0xABCDEF01u;
    public int AreaLevel { get; set; } = 1;
    public bool IsTown { get; set; }
    public bool IsHideout { get; set; }
    public WorldPos PlayerWorldPos { get; set; } = new(0f, 0f);
    public int CharacterLevel { get; set; } = 1;
    public long CurrentXp { get; set; }
    public bool IsPlayerAlive { get; set; } = true;
    public IReadOnlyList<nint> UiTreeRoots { get; set; } = System.Array.Empty<nint>();
    public IReadOnlyList<Poe2Live.EntityDot> Entities { get; set; } = System.Array.Empty<Poe2Live.EntityDot>();
    public IReadOnlyList<ushort> AllocatedPassiveNodeIds { get; set; } = System.Array.Empty<ushort>();
    public string? LastDamageSourceMetadata { get; set; }
    public nint AreaInstance { get; set; } = 0x2000;
    public nint InGameState { get; set; } = 0x1000;
    public nint LocalPlayer { get; set; } = 0x3000;

    /// <summary>Snap the current field state into the record struct <see cref="CampaignProbe.Tick"/>
    /// consumes. Fresh copy each call so mutating the fake between ticks doesn't retroactively
    /// change a snap the probe has already read.</summary>
    public CampaignProbeSnap ToSnap() => new(
        InGameState:              InGameState,
        AreaInstance:             AreaInstance,
        LocalPlayer:              LocalPlayer,
        AreaCode:                 AreaCode,
        AreaName:                 AreaName,
        AreaHash:                 AreaHash,
        AreaLevel:                AreaLevel,
        IsTown:                   IsTown,
        IsHideout:                IsHideout,
        PlayerWorldPos:           PlayerWorldPos,
        CharacterLevel:           CharacterLevel,
        CurrentXp:                CurrentXp,
        IsPlayerAlive:            IsPlayerAlive,
        UiTreeRoots:              UiTreeRoots,
        Entities:                 Entities,
        AllocatedPassiveNodeIds:  AllocatedPassiveNodeIds,
        LastDamageSourceMetadata: LastDamageSourceMetadata);
}
