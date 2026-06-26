using System;
using POE2Radar.Core.Input;
using Xunit;

namespace POE2Radar.Tests;

public class HoldRepeatTests
{
    private static HoldRepeat New() => new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(150));
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime At(int ms) => T0.AddMilliseconds(ms);

    [Fact] public void Tap_fires_one_step_on_press()
    {
        var h = New();
        Assert.Equal(1, h.Update(+1, At(0)));   // press → one step
        Assert.Equal(0, h.Update(0, At(10)));   // release → nothing
    }

    [Fact] public void Hold_within_initial_delay_does_not_repeat()
    {
        var h = New();
        Assert.Equal(1, h.Update(+1, At(0)));
        Assert.Equal(0, h.Update(+1, At(100)));  // 100ms < 400ms delay
        Assert.Equal(0, h.Update(+1, At(399)));
    }

    [Fact] public void Hold_past_delay_repeats_at_interval()
    {
        var h = New();
        Assert.Equal(1, h.Update(+1, At(0)));    // tap
        Assert.Equal(1, h.Update(+1, At(400)));  // first repeat (>= delay, >= interval since press)
        Assert.Equal(0, h.Update(+1, At(500)));  // 100ms since last fire < 150
        Assert.Equal(1, h.Update(+1, At(550)));  // 150ms since last fire → repeat
    }

    [Fact] public void Direction_flip_retaps_immediately()
    {
        var h = New();
        Assert.Equal(1, h.Update(+1, At(0)));
        Assert.Equal(1, h.Update(-1, At(50)));   // changed direction → immediate tap
    }

    [Fact] public void Release_resets_so_next_press_taps()
    {
        var h = New();
        Assert.Equal(1, h.Update(+1, At(0)));
        Assert.Equal(0, h.Update(0, At(10)));
        Assert.Equal(1, h.Update(+1, At(20)));   // fresh press taps again
    }
}
