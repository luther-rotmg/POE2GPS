using System.Runtime.InteropServices;

namespace POE2Radar.Overlay.Input;

[StructLayout(LayoutKind.Sequential)]
internal struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger, RightTrigger;
    public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

/// <summary>Read-only XInput access for controller 0. Reads the gamepad button bitmask to drive the
/// overlay's target cycler — never emits input to the game. (Win8+; a missing xinput1_4.dll is tolerated.)</summary>
internal static partial class XInputNative
{
    public const ushort GamepadLeftThumb = 0x0040;   // L3
    public const ushort GamepadRightThumb = 0x0080;  // R3
    private const uint ErrorSuccess = 0;

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static partial uint XInputGetState(uint dwUserIndex, out XInputState pState);

    /// <summary>The button bitmask of controller <paramref name="userIndex"/>, or null when no pad is
    /// connected / the read fails / xinput is unavailable.</summary>
    public static ushort? TryGetButtons(uint userIndex = 0)
    {
        try { return XInputGetState(userIndex, out var s) == ErrorSuccess ? s.Gamepad.Buttons : null; }
        catch { return null; }
    }
}
