using POE2Radar.Core.Input;

namespace POE2Radar.Overlay.Input;

/// <summary>Polls XInput controller 0 and resolves L3/R3 into a <see cref="ControllerInput"/> via the pure
/// <see cref="ControllerChord"/> seam: L3=prev, R3=next, L3+R3=menu toggle. Read-only — never emits input.</summary>
internal sealed class ControllerCycler
{
    private ushort _prev;

    /// <summary>Poll once. Returns the resolved menu-toggle/cycle edge AND the currently-HELD cycle
    /// direction (L3 down = -1, R3 down = +1, both down = 0 (menu mode), none = 0). Always call each
    /// frame so the edge state stays correct.</summary>
    public (ControllerInput input, int heldDir) Poll()
    {
        var read = XInputNative.TryGetButtons();
        if (read is not { } cur) { _prev = 0; return (default, 0); }
        var result = ControllerChord.Resolve(_prev, cur);
        _prev = cur;
        return (result, HeldDir(cur));
    }

    private static int HeldDir(ushort cur)
    {
        var l = (cur & ControllerChord.LeftThumb) != 0;
        var r = (cur & ControllerChord.RightThumb) != 0;
        if (l && r) return 0;          // both held = menu mode; suppress cycle
        return l ? -1 : r ? +1 : 0;
    }
}
