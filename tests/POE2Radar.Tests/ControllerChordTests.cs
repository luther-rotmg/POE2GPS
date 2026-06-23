// tests/POE2Radar.Tests/ControllerChordTests.cs
using POE2Radar.Core.Input;
using Xunit;

namespace POE2Radar.Tests;

public class ControllerChordTests
{
    private const ushort L = ControllerChord.LeftThumb;   // 0x0040
    private const ushort R = ControllerChord.RightThumb;  // 0x0080

    [Fact] public void L3_rising_edge_is_prev()  => Assert.Equal(new ControllerInput(-1, false), ControllerChord.Resolve(0, L));
    [Fact] public void R3_rising_edge_is_next()  => Assert.Equal(new ControllerInput(+1, false), ControllerChord.Resolve(0, R));
    [Fact] public void L3_held_does_not_repeat() => Assert.Equal(default, ControllerChord.Resolve(L, L));

    [Fact] public void both_down_rising_edge_toggles_menu_and_suppresses_cycle()
        => Assert.Equal(new ControllerInput(0, true), ControllerChord.Resolve(L, (ushort)(L | R)));

    [Fact] public void both_down_held_does_not_repeat_toggle()
        => Assert.Equal(new ControllerInput(0, false), ControllerChord.Resolve((ushort)(L | R), (ushort)(L | R)));

    [Fact] public void releasing_one_of_two_held_emits_nothing()
        => Assert.Equal(default, ControllerChord.Resolve((ushort)(L | R), R));

    [Fact] public void no_change_emits_default()
        => Assert.Equal(default, ControllerChord.Resolve(0, 0));
}
