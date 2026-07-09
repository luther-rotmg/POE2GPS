using POE2Radar.Core.Game;

namespace POE2Radar.Tests.CampaignProbe;

/// <summary>
/// PROBE-OFFSETS constant floor. Every value here is transcribed from
/// scratchpad/campaign-probe-offsets.md (imkk000/poe2-offsets extraction, 2026-07-08).
/// If any assert fails, either the constant drifted or the upstream extraction was misread —
/// re-open the scratchpad and reconcile before touching the test.
/// </summary>
public class Poe2CampaignProbeOffsetsTests
{
    [Fact]
    public void PlayerComponent_CurrentExperience_matches_upstream()
        => Assert.Equal(0x1D8, Poe2.PlayerComponent.CurrentExperience);

    [Fact]
    public void PlayerServerData_QuestFlags_matches_upstream()
        => Assert.Equal(0x230, Poe2.PlayerServerData.QuestFlags);

    [Fact]
    public void Quest_definition_and_entry_offsets_match_upstream()
    {
        Assert.Equal(0x2E0, Poe2.Quest.DefinitionPtr);
        Assert.Equal(0x2F0, Poe2.Quest.EntryPtr);
        Assert.Equal(0x3C,  Poe2.Quest.EntryState);
        Assert.Equal(0x3D,  Poe2.Quest.EntryObjective);
        Assert.Equal(0x00,  Poe2.Quest.RowId);
        Assert.Equal(0x0C,  Poe2.Quest.RowName);
    }

    [Fact]
    public void PassiveTree_alloc_vec_and_hop_chain_match_upstream()
    {
        Assert.Equal(0x8A8, Poe2.PassiveTree.AllocVecBegin);
        Assert.Equal(0x8B0, Poe2.PassiveTree.AllocVecEnd);
        Assert.Equal(4,     Poe2.PassiveTree.EntryStride);
        Assert.Equal(1024,  Poe2.PassiveTree.AllocMax);
        Assert.Equal(new[] { 0x60, 0x40, 0xCE0, 0x418 }, Poe2.PassiveTree.HopChain);
    }

    [Fact]
    public void Targetable_component_bytes_match_upstream()
    {
        Assert.Equal(0x69, Poe2.Targetable.IsTargetable);
        Assert.Equal(0x6A, Poe2.Targetable.IsHighlight);
        Assert.Equal(0x6B, Poe2.Targetable.IsTargeted);
        Assert.Equal(0x71, Poe2.Targetable.IsHidden);
    }

    [Fact]
    public void ChestComponent_label_visible_matches_upstream_and_OpenState_untouched()
    {
        Assert.Equal(0x21,  Poe2.ChestComponent.LabelVisible);
        Assert.Equal(0x168, Poe2.ChestComponent.OpenState); // regression guard
    }

    [Fact]
    public void Shrine_and_Transitionable_and_Blockage_match_upstream()
    {
        Assert.Equal(0x24,  Poe2.Shrine.IsUsed);
        Assert.Equal(0x120, Poe2.Transitionable.State);
        Assert.Equal(0x30,  Poe2.TriggerableBlockage.IsBlocked);
    }

    [Fact]
    public void StateMachineExt_state_and_timer_vectors_match_upstream()
    {
        Assert.Equal(0x160, Poe2.StateMachineExt.StatesBegin);
        Assert.Equal(0x168, Poe2.StateMachineExt.StatesEnd);
        Assert.Equal(0x178, Poe2.StateMachineExt.TimersBegin);
        Assert.Equal(0x180, Poe2.StateMachineExt.TimersEnd);
        Assert.Equal(8,     Poe2.StateMachineExt.EntryStride);
    }

    [Fact]
    public void HoverTracker_hovered_entity_direct_matches_shipped_offset()
    {
        Assert.Equal(0x18,  Poe2.HoverTracker.HoveredEntityDirect);
        Assert.Equal(0x7D8, Poe2.HoverTracker.FromUiRoot); // regression guard on shipped anchor
    }

    [Fact]
    public void Poe2Live_exposes_campaign_probe_accessors()
    {
        // Reflection-only surface check — production accessors must exist so PROBE-CORE can compile
        // without live memory. Argument-count guards against accidental signature drift.
        var t = typeof(Poe2Live);
        Assert.NotNull(t.GetMethod(nameof(Poe2Live.PlayerExperience)));
        Assert.NotNull(t.GetMethod(nameof(Poe2Live.AllocatedPassiveNodeIds)));
        Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadTargetable)));
        Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadChestState)));
        Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadShrineUsed)));
        Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadTransitionableState)));
        Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadTriggerableBlockage)));
        Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadQuestFlag)));
        Assert.NotNull(t.GetMethod(nameof(Poe2Live.HoveredEntityViaTracker)));
        Assert.NotNull(t.GetMethod(nameof(Poe2Live.WalkUiTree)));
    }
}
