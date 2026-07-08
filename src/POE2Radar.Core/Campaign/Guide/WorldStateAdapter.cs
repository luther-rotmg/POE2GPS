using System;
using System.Collections.Generic;
using POE2Radar.Core.Game;
using Vector2 = System.Numerics.Vector2;

namespace POE2Radar.Core.Campaign.Guide;

/// <summary>
/// v0.21 <see cref="IWorldState"/> adapter — a read-only view over the snapshot last handed in
/// via <see cref="Refresh"/>. The world thread drains <see cref="Poe2Live"/> once per tick and
/// hands the derived values (area code, player grid, entity list) to this adapter; the adapter
/// never touches native memory itself. The only exception is <see cref="LootSatisfied"/>, which
/// reuses the shipped <see cref="Poe2Live.ReadInventory(nint)"/> chain — also world-thread-owned.
/// <para>
/// Four signals are live in v0.21: <see cref="InAreaSatisfied"/>, <see cref="ProximitySatisfied"/>,
/// <see cref="KillProgress"/>, and <see cref="LootSatisfied"/> against the player inventory. The
/// remaining five interface methods (<see cref="QuestFlagSatisfied"/>, <see cref="WaypointPulsed"/>,
/// <see cref="SatisfiedFlagCount"/>, <see cref="TalkProgress"/>, <see cref="InteractProgress"/>) plus
/// the private <see cref="LootSatisfied_QuestInventoryStub"/> route are graceful-degradation stubs
/// hard-returning <c>false</c>/<c>0</c> until PMS-4's quest-flag reader ships in v0.22. Each stub is
/// a distinct method so the v0.22 swap-in is a body-only edit per signal.
/// </para>
/// <para>
/// Constructed unconditionally at startup — the runtime toggle of <c>EnableCampaignGps</c> is gated
/// at the <c>CampaignReconcile</c> call site (see task 5), never here, so lazy init can't race a
/// mid-session flip. Zero per-tick allocation when <see cref="Refresh"/> isn't called.
/// </para>
/// </summary>
public sealed class WorldStateAdapter : IWorldState
{
    private readonly Func<nint, IReadOnlyList<Poe2Live.InventoryItem>> _readInventory;
    private readonly EntityNameResolver _nameResolver;

    // Snapshot cached by Refresh(). Read and written on the world thread only.
    private nint _areaInstance;
    private nint _localPlayer;
    private string _areaCode = "";
    private Vector2 _playerGrid;
    private IReadOnlyList<Poe2Live.EntityDot> _entities = Array.Empty<Poe2Live.EntityDot>();

    /// <summary>Production ctor. Captures the shipped <see cref="Poe2Live.ReadInventory(nint)"/>
    /// delegate so the adapter has no <see cref="Poe2Live"/> field of its own — the reader is only
    /// invoked via the captured method group.</summary>
    public WorldStateAdapter(Poe2Live live)
        : this((live ?? throw new ArgumentNullException(nameof(live))).ReadInventory,
               EntityNameResolver.Shared) { }

    /// <summary>Test seam: inject a fake inventory reader (and optionally an override name resolver)
    /// so unit tests never need a real <see cref="Poe2Live"/> bound to a live process.</summary>
    internal WorldStateAdapter(
        Func<nint, IReadOnlyList<Poe2Live.InventoryItem>> readInventory,
        EntityNameResolver? nameResolver = null)
    {
        _readInventory = readInventory ?? throw new ArgumentNullException(nameof(readInventory));
        _nameResolver = nameResolver ?? EntityNameResolver.Shared;
    }

    /// <summary>Last <see cref="Refresh"/>ed area code (empty until first refresh). Consumed by
    /// <c>RouteCursor</c> for the area-change forward-snap.</summary>
    public string CurrentAreaCode => _areaCode;

    /// <summary>Called from the world thread by <c>RadarApp.CampaignReconcile</c> each tick when
    /// <c>EnableCampaignGps</c> is on. References only — no defensive copies; the caller owns
    /// entity-list stability for the tick.</summary>
    public void Refresh(nint areaInstance, nint localPlayer, string currentAreaCode,
        Vector2 playerGrid, IReadOnlyList<Poe2Live.EntityDot> entities)
    {
        _areaInstance = areaInstance;
        _localPlayer = localPlayer;
        _areaCode = currentAreaCode ?? "";
        _playerGrid = playerGrid;
        _entities = entities ?? Array.Empty<Poe2Live.EntityDot>();
    }

    // ------------------------------------------------------------ live signals

    /// <summary>True when the last-refreshed area code matches the pattern (literal = case-insensitive
    /// substring, regex = compiled). Empty patterns never match.</summary>
    public bool InAreaSatisfied(Pattern area)
    {
        if (area is null || string.IsNullOrEmpty(area.Value)) return false;
        return new PatternMatcher(area).IsMatch(_areaCode);
    }

    /// <summary>True when any entity matching any of <paramref name="entities"/> is within
    /// <paramref name="distance"/> grid units of the player. The tiles branch is not implemented
    /// in v0.21 (landmark-anchored proximity ships in a later task); a non-null tiles list is
    /// currently ignored.</summary>
    public bool ProximitySatisfied(IReadOnlyList<EntityMatcher>? entities, IReadOnlyList<Pattern>? tiles, float distance)
    {
        if (entities is null || entities.Count == 0) return false;
        var r2 = distance * distance;
        foreach (var e in _entities)
        {
            if (!MatchesAny(e, entities)) continue;
            var dx = e.Grid.X - _playerGrid.X;
            var dy = e.Grid.Y - _playerGrid.Y;
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    /// <summary>Count of dead monsters in the last-refreshed entity set that match any of
    /// <paramref name="entities"/>. Mirrors the entity-death edge from
    /// <c>PruneCompletedTargets</c>: an entity is "dead" when its life component reports
    /// non-positive HP (see <see cref="Poe2Live.EntityDot.IsAlive"/>).</summary>
    public int KillProgress(IReadOnlyList<EntityMatcher>? entities)
    {
        if (entities is null || entities.Count == 0) return 0;
        var n = 0;
        foreach (var e in _entities)
        {
            if (e.Category != Poe2Live.EntityCategory.Monster) continue;
            if (e.IsAlive) continue;
            if (MatchesAny(e, entities)) n++;
        }
        return n;
    }

    /// <summary>True when every matcher in <paramref name="items"/> is satisfied by the player's
    /// inventory (each matcher's <see cref="ItemMatcher.Count"/> or 1 items whose rendered
    /// base-type name matches the pattern). When a matcher misses the player-inventory pass,
    /// control falls to <see cref="LootSatisfied_QuestInventoryStub"/> — that path stays a hard
    /// <c>false</c> until PMS-4 lands the quest-inventory bucket walk.</summary>
    public bool LootSatisfied(IReadOnlyList<ItemMatcher>? items)
    {
        if (items is null || items.Count == 0) return false;
        var inv = _readInventory(_areaInstance);
        foreach (var m in items)
        {
            if (m is null || m.Match is null || string.IsNullOrEmpty(m.Match.Value)) return false;
            if (!PlayerInventoryHas(m, inv) && !LootSatisfied_QuestInventoryStub(m))
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------ stubs (v0.22)

    // Stubbed until PMS-4 quest-flag reader ships in v0.22.
    public bool QuestFlagSatisfied(Pattern flag) => false;

    // Stubbed until PMS-4 waypoint-pulse edge detection ships in v0.22.
    public bool WaypointPulsed() => false;

    // Stubbed until PMS-4 quest-flag reader ships in v0.22 (per-flag count comes from the same source).
    public int SatisfiedFlagCount(IReadOnlyList<Pattern> flags) => 0;

    // Stubbed until the NpcDialog offset chain is graduated from Research/Program.cs.
    public int TalkProgress(IReadOnlyList<EntityMatcher>? entities) => 0;

    // Stubbed until the Targetable byte reader is graduated from Research/Program.cs.
    public int InteractProgress(IReadOnlyList<EntityMatcher>? entities) => 0;

    // Stubbed until the quest-inventory bucket walk lands in v0.22 (separate InventoryStruct chain
    // from the currently-shipped player-inventory bag walk).
    private static bool LootSatisfied_QuestInventoryStub(ItemMatcher matcher) => false;

    // ------------------------------------------------------------ matcher helpers

    private bool PlayerInventoryHas(ItemMatcher m, IReadOnlyList<Poe2Live.InventoryItem> inv)
    {
        var pm = new PatternMatcher(m.Match);
        var have = 0;
        foreach (var it in inv)
            if (pm.IsMatch(it.Name)) have++;
        return have >= (m.Count > 0 ? m.Count : 1);
    }

    private bool MatchesAny(in Poe2Live.EntityDot e, IReadOnlyList<EntityMatcher> matchers)
    {
        foreach (var m in matchers)
        {
            if (m is null || m.Match is null || string.IsNullOrEmpty(m.Match.Value)) continue;
            var candidate = m.MatchBy == MatchKind.Name
                ? (_nameResolver.Resolve(e.Metadata) ?? e.Metadata)
                : e.Metadata;
            if (new PatternMatcher(m.Match).IsMatch(candidate)) return true;
        }
        return false;
    }
}
