using System.Diagnostics;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using POE2Radar.Core;
using POE2Radar.Core.Stealth;
using POE2Radar.Overlay;

// ── Self-relaunch under a random hardlink name (identity hygiene) ──
if (!args.Contains("--launched"))
{
    var currentExe = Environment.ProcessPath;
    if (currentExe != null)
    {
        var currentDir = Path.GetDirectoryName(currentExe)!;
        var targetExe = Path.Combine(currentDir, RandomName.Generate() + ".exe");
        try
        {
            SweepOldHardlinks(currentDir, currentExe);
            if (!File.Exists(targetExe))
                CreateHardLink(targetExe, currentExe, IntPtr.Zero);
            Process.Start(new ProcessStartInfo(targetExe, "--launched")
            {
                UseShellExecute = false,
                WorkingDirectory = currentDir,
            });
            return 0;
        }
        catch { /* hardlink failed — fall through and run directly */ }
    }
}

var myName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Overlay");
Console.Title = myName;                 // neutral/randomized — anti-detection (do NOT brand the title)
POE2Radar.Overlay.Overlay.ConsoleTheme.Banner();

using var process = ProcessHandle.AttachToPoE();
if (process is null)
{
    Console.Error.WriteLine("Game not running (no matching process found).");
    return 1;
}
POE2Radar.Overlay.Overlay.ConsoleTheme.Ok($"Attached to {process.ProcessName} (PID {process.ProcessId})");

var reader = new MemoryReader(process);
var slot = Bootstrap.ResolveGameStateSlot(process, reader);
if (slot == 0)
    return 2;

Console.WriteLine();
POE2Radar.Overlay.Overlay.ConsoleTheme.Accent("Running. Open the in-game map to see terrain + entities. Ctrl+C to exit.");

using var app = new RadarApp(process, reader, slot);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; app.RequestShutdown(); };
app.Run();

// Clean up hardlinks (any *.exe in the dir that isn't the published base 'Overlay' or this process).
try
{
    var self = Environment.ProcessPath;
    var dir = Path.GetDirectoryName(self);
    if (self != null && dir != null)
        foreach (var f in Directory.GetFiles(dir, "*.exe"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name != "Overlay" && f != self)
                try { File.Delete(f); } catch { }
        }
}
catch { }

return 0;

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetFileInformationByHandle(
    IntPtr hFile,
    out BY_HANDLE_FILE_INFORMATION lpFileInformation);

static void SweepOldHardlinks(string dir, string baseExe)
{
    if (!GetByHandle(baseExe, out var baseInfo)) return;
    foreach (var f in Directory.GetFiles(dir, "*.exe"))
    {
        if (string.Equals(f, baseExe, StringComparison.OrdinalIgnoreCase)) continue;
        if (string.Equals(Path.GetFileNameWithoutExtension(f), "Overlay",
                          StringComparison.OrdinalIgnoreCase)) continue;
        if (!GetByHandle(f, out var fi)) continue;
        if (!HardlinkIdentity.SameFileId(
                baseInfo.dwVolumeSerialNumber, baseInfo.nFileIndexHigh, baseInfo.nFileIndexLow,
                fi.dwVolumeSerialNumber,       fi.nFileIndexHigh,       fi.nFileIndexLow)) continue;
        try { File.Delete(f); } catch { }
    }
}

static bool GetByHandle(string path, out BY_HANDLE_FILE_INFORMATION info)
{
    info = default;
    try
    {
        using var fs = File.OpenRead(path);
        return GetFileInformationByHandle(fs.SafeFileHandle.DangerousGetHandle(), out info);
    }
    catch { return false; }
}

[StructLayout(LayoutKind.Sequential)]
struct BY_HANDLE_FILE_INFORMATION
{
    public uint  dwFileAttributes;
    public ComTypes.FILETIME ftCreationTime;
    public ComTypes.FILETIME ftLastAccessTime;
    public ComTypes.FILETIME ftLastWriteTime;
    public uint  dwVolumeSerialNumber;
    public uint  nFileSizeHigh;
    public uint  nFileSizeLow;
    public uint  nNumberOfLinks;
    public uint  nFileIndexHigh;
    public uint  nFileIndexLow;
}
