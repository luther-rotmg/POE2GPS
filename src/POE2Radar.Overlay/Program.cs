using System.Diagnostics;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using POE2Radar.Core;
using POE2Radar.Core.Stealth;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Config;

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

// Diagnostics: tee console → config/poe2gps.log + capture unhandled exceptions (so crash/offset reports
// are copyable from a file and carry a stack trace). Must run FIRST in the --launched instance.
POE2Radar.Overlay.DiagnosticsLog.Init();

// ── v0.19.1 auto-update (runs only in the --launched real instance, before attaching to the game) ──
var installDir = Path.GetDirectoryName(Environment.ProcessPath);
var startupSettings = RadarSettings.Load();
if (installDir != null)
{
    // A crash-looping update rolls back to Overlay.old.exe (safety — runs regardless of mode).
    if (POE2Radar.Overlay.Update.AutoUpdater.RollbackIfCrashLooping(installDir)) return 0;
    // Apply an update staged by a previous session (silent mode), then relaunch into it.
    // Non-silent: prune any stale staged file so PendingVersion() can't show a false banner.
    if (startupSettings.AutoUpdate.Mode == "silent")
    {
        if (POE2Radar.Overlay.Update.AutoUpdater.ApplyStagedIfPresent(UpdateChecker.Current, installDir)) return 0;
    }
    else
    {
        POE2Radar.Overlay.Update.AutoUpdater.DiscardStaged(installDir);
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

Console.WriteLine();
POE2Radar.Overlay.Overlay.ConsoleTheme.Accent("Running. The overlay connects automatically once you're in a zone. Ctrl+C to exit.");

// Update check (banner) + silent background staging for NEXT launch — never blocks startup.
// updateTask is non-null whenever Mode != "off"; CheckAndStageAsync reuses it (precheck) so
// silent mode makes exactly ONE GitHub request per launch instead of two.
System.Threading.Tasks.Task<UpdateChecker.Result>? updateTask = null;
if (startupSettings.AutoUpdate.Mode != "off")
    updateTask = System.Threading.Tasks.Task.Run(() => UpdateChecker.CheckAsync());
if (startupSettings.AutoUpdate.Mode == "silent" && installDir != null)
    _ = System.Threading.Tasks.Task.Run(() =>
        POE2Radar.Overlay.Update.AutoUpdater.CheckAndStageAsync("silent", UpdateChecker.Current, installDir, System.Threading.CancellationToken.None, precheck: updateTask, settings: startupSettings));

using var app = new RadarApp(process, reader, updateTask);
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
        // Primary guard: never delete the running base exe (Overlay.exe) itself.
        if (string.Equals(f, baseExe, StringComparison.OrdinalIgnoreCase)) continue;
        // Defensive backstop in case path normalization ever differs from Environment.ProcessPath.
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
