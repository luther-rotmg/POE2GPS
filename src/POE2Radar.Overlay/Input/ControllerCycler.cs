namespace POE2Radar.Overlay.Input;

/// <summary>Edge-detects L3 (prev) / R3 (next) on XInput controller 0 → a cycle direction. Read-only.</summary>
internal sealed class ControllerCycler
{
    private ushort _prev;

    /// <summary>Poll once. Returns -1 (L3 pressed = prev), +1 (R3 pressed = next), or 0. Edge-triggered:
    /// fires once per physical press. Always call it each frame so the edge state stays correct.</summary>
    public int Poll()
    {
        var read = XInputNative.TryGetButtons();
        if (read is not { } cur) { _prev = 0; return 0; }
        var pressed = (ushort)(cur & ~_prev);   // rising edges since last poll
        _prev = cur;
        if ((pressed & XInputNative.GamepadLeftThumb) != 0) return -1;
        if ((pressed & XInputNative.GamepadRightThumb) != 0) return +1;
        return 0;
    }
}
