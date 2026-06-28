namespace POE2Radar.Core.Input;

/// <summary>Formats a Windows virtual-key code to a short display label (F6, R, ], …) for the keybind
/// editor. Pure + unit-tested. The overlay only READS these keys — it never sends input.</summary>
public static class KeyNames
{
    public static string Format(int vk) => vk switch
    {
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),            // F1..F12
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),         // 0..9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),         // A..Z
        0xDD => "]", 0xDB => "[", 0xBA => ";", 0xBF => "/",
        0xBD => "-", 0xBB => "=", 0xC0 => "`", 0xDE => "'", 0xDC => "\\",
        0xBC => ",", 0xBE => ".", 0x20 => "Space",
        _ => "Key" + vk.ToString("X2"),
    };
}
