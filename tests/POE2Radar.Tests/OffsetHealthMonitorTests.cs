using System;
using POE2Radar.Core.Health;
using Xunit;

namespace POE2Radar.Tests;

public class OffsetHealthMonitorTests
{
    // Shipping thresholds, with a fake clock (seconds).
    private static OffsetHealthMonitor New() =>
        new(TimeSpan.FromSeconds(25), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(90), 3, 10);
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime At(int sec) => T0.AddSeconds(sec);

    private static ChainProbe P(bool attached, bool slot, ResolveStage stage,
        int aob = 1, bool aobScanned = true, bool terrain = true,
        bool updAvail = false, bool updChecked = true, string? url = "https://rel") =>
        new(attached, slot, aob, aobScanned, stage, terrain, updAvail, updChecked, url);

    [Fact] public void Not_attached_is_Waiting()
    {
        var v = New().Evaluate(P(false, false, ResolveStage.None), At(0));
        Assert.Equal(HealthState.Waiting, v.State);
        Assert.NotNull(v.Message);  // Waiting intentionally carries a dashboard message ("…is not running.")
    }

    [Fact] public void Attached_no_slot_is_Searching_connecting()
    {
        var v = New().Evaluate(P(true, false, ResolveStage.None), At(0));
        Assert.Equal(HealthState.Searching, v.State);
        Assert.Contains("Connecting", v.Message);
    }

    [Fact] public void Searching_sustained_shows_soft_update_hint()
    {
        var m = New();
        m.Evaluate(P(true, false, ResolveStage.None), At(0));
        var v = m.Evaluate(P(true, false, ResolveStage.None), At(91));   // > 90 s
        Assert.Equal(HealthState.Searching, v.State);
        Assert.Contains("may need an update", v.Message);
    }

    [Fact] public void Aob_zero_after_15s_is_pattern_broke_wording()
    {
        var m = New();
        m.Evaluate(P(true, false, ResolveStage.None, aob: 0), At(0));
        var v = m.Evaluate(P(true, false, ResolveStage.None, aob: 0), At(16));
        Assert.Contains("can't find", v.Message);
    }

    [Fact] public void Full_is_Ok_with_no_message()
    {
        var v = New().Evaluate(P(true, true, ResolveStage.Full), At(0));
        Assert.Equal(HealthState.Ok, v.State);
        Assert.Null(v.Message);
    }

    [Fact] public void Ok_but_no_terrain_for_ten_ticks_soft_warns()
    {
        var m = New(); HealthVerdict v = default;
        for (var i = 0; i < 10; i++) v = m.Evaluate(P(true, true, ResolveStage.Full, terrain: false), At(i));
        Assert.Equal(HealthState.Ok, v.State);
        Assert.Contains("no map data", v.Message);
    }

    [Fact] public void InZone_below_holdoff_is_Loading_no_alarm()
    {
        var m = New(); HealthVerdict v = default;
        for (var i = 0; i < 3; i++) v = m.Evaluate(P(true, true, ResolveStage.InZone), At(i));
        Assert.Equal(HealthState.Loading, v.State);
        Assert.Null(v.Message);
    }

    [Fact] public void Single_InZone_tick_not_trusted_stays_Searching()
    {
        // Relies on _everResolved == false: no Full tick precedes this, so a single InZone tick
        // falls through to Searching (not NotInGame). A future edit adding a Full tick first would
        // silently change the intent.
        var v = New().Evaluate(P(true, true, ResolveStage.InZone), At(0));
        Assert.Equal(HealthState.Searching, v.State);
    }

    [Fact] public void InZone_sustained_past_holdoff_is_Broken()
    {
        var m = New(); HealthVerdict v = default;
        for (var i = 0; i < 3; i++) v = m.Evaluate(P(true, true, ResolveStage.InZone), At(i)); // loadingSince=At(2)
        v = m.Evaluate(P(true, true, ResolveStage.InZone), At(27));   // 25 s later
        Assert.Equal(HealthState.Broken, v.State);
        Assert.Contains("can't read", v.Message);
    }

    [Fact] public void Holdoff_resets_after_returning_to_Ok()
    {
        var m = New();
        for (var i = 0; i < 3; i++) m.Evaluate(P(true, true, ResolveStage.InZone), At(i)); // loadingSince=At(2)
        m.Evaluate(P(true, true, ResolveStage.Full), At(20));                               // Ok clears loadingSince
        var v = m.Evaluate(P(true, true, ResolveStage.InZone), At(21));                     // loadingSince=At(21)
        v = m.Evaluate(P(true, true, ResolveStage.InZone), At(40));                         // 19 s < 25 → not Broken
        Assert.Equal(HealthState.Loading, v.State);
    }

    [Fact] public void Post_ok_offline_under_five_min_is_benign()
    {
        var m = New();
        m.Evaluate(P(true, true, ResolveStage.Full), At(0));                       // Ok latch
        var v = m.Evaluate(P(true, true, ResolveStage.InGameState), At(60));       // < 5 min
        Assert.Equal(HealthState.NotInGame, v.State);
        Assert.Null(v.Message);
    }

    [Fact] public void Post_ok_offline_over_five_min_soft_warns()
    {
        var m = New();
        m.Evaluate(P(true, true, ResolveStage.Full), At(0));                       // Ok latch
        m.Evaluate(P(true, true, ResolveStage.InGameState), At(1));                // NotInGame since At(1)
        var v = m.Evaluate(P(true, true, ResolveStage.InGameState), At(302));      // > 5 min
        Assert.Equal(HealthState.NotInGame, v.State);
        Assert.NotNull(v.Message);
        Assert.Contains("offline", v.Message);  // pin the specific offline-warning branch
    }

    [Fact] public void Broken_message_is_update_aware_when_update_available()
    {
        var m = New();
        for (var i = 0; i < 3; i++) m.Evaluate(P(true, true, ResolveStage.InZone, updAvail: true, url: "http://dl"), At(i));
        var v = m.Evaluate(P(true, true, ResolveStage.InZone, updAvail: true, url: "http://dl"), At(30));
        Assert.Equal(HealthState.Broken, v.State);
        Assert.Contains("Update available", v.Message);
        Assert.Contains("http://dl", v.Message);
    }

    [Fact] public void Broken_message_when_check_not_done_says_likely_updated()
    {
        var m = New();
        for (var i = 0; i < 3; i++) m.Evaluate(P(true, true, ResolveStage.InZone, updChecked: false), At(i));
        var v = m.Evaluate(P(true, true, ResolveStage.InZone, updChecked: false), At(30));
        Assert.Contains("likely just updated", v.Message);
    }
}
