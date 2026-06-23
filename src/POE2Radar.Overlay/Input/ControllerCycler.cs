using POE2Radar.Core.Input;

namespace POE2Radar.Overlay.Input;

/// <summary>Polls XInput controller 0 and resolves L3/R3 into a <see cref="ControllerInput"/> via the pure
/// <see cref="ControllerChord"/> seam: L3=prev, R3=next, L3+R3=menu toggle. Read-only — never emits input.</summary>
internal sealed class ControllerCycler
{
    private ushort _prev;

    /// <summary>Poll once. Returns the resolved cycle direction + menu-toggle edge. Always call each frame
    /// so the edge state stays correct.</summary>
    public ControllerInput Poll()
    {
        var read = XInputNative.TryGetButtons();
        if (read is not { } cur) { _prev = 0; return default; }
        var result = ControllerChord.Resolve(_prev, cur);
        _prev = cur;
        return result;
    }
}
