using System.Runtime.InteropServices;

namespace POE2Radar.Overlay.Overlay.Native;

/// <summary>
/// Read the Windows clipboard as UTF-16 text. Returns null on any failure — including another app
/// briefly holding the clipboard (retries a few times), a non-text payload, or a locked GlobalLock.
/// Purely read-only (no clipboard writes, no side effects on paste-target focus).
/// v0.29 Panels: WaystoneRisk hotkey reads via this to feed <see cref="POE2Radar.Core.Game.WaystoneModRisk"/>.
/// </summary>
public static class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint uFormat);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint hMem);

    /// <summary>Read the current clipboard as unicode text, or null on any failure.</summary>
    public static string? Read()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            if (!OpenClipboard(0)) { System.Threading.Thread.Sleep(15); continue; }
            try
            {
                var hMem = GetClipboardData(CF_UNICODETEXT);
                if (hMem == 0) return null;
                var pMem = GlobalLock(hMem);
                if (pMem == 0) return null;
                try { return Marshal.PtrToStringUni(pMem); }
                finally { GlobalUnlock(hMem); }
            }
            finally { CloseClipboard(); }
        }
        return null;
    }
}
