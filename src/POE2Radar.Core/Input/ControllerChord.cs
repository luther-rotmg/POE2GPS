// src/POE2Radar.Core/Input/ControllerChord.cs
namespace POE2Radar.Core.Input;

/// <summary>One controller poll's resolved intent: a cycle direction (-1 prev / +1 next / 0 none) and
/// whether the L3+R3 menu-toggle chord fired this poll. Pure + deterministic so it is unit-testable.</summary>
public readonly record struct ControllerInput(int Cycle, bool MenuToggle);

/// <summary>Pure edge resolver shared by the Quick-Target cycler and the nav-menu chord. Given the
/// previous and current XInput button masks: L3+R3 held together fires <see cref="ControllerInput.MenuToggle"/>
/// once (on the rising edge of "both down") and suppresses the single-stick cycle for that press; otherwise a
/// rising edge of L3 = -1 (prev) or R3 = +1 (next). Read-only — never emits input.</summary>
public static class ControllerChord
{
    public const ushort LeftThumb = 0x0040;   // XInput GAMEPAD_LEFT_THUMB  (L3)
    public const ushort RightThumb = 0x0080;  // XInput GAMEPAD_RIGHT_THUMB (R3)

    public static ControllerInput Resolve(ushort prev, ushort cur)
    {
        var bothNow = (cur & LeftThumb) != 0 && (cur & RightThumb) != 0;
        if (bothNow)
        {
            var bothPrev = (prev & LeftThumb) != 0 && (prev & RightThumb) != 0;
            return new ControllerInput(0, !bothPrev);   // toggle once on rising edge; suppress cycle while both held
        }

        var pressed = (ushort)(cur & ~prev);            // rising edges since last poll
        if ((pressed & LeftThumb) != 0) return new ControllerInput(-1, false);
        if ((pressed & RightThumb) != 0) return new ControllerInput(+1, false);
        return default;
    }
}
