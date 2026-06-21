using System.Diagnostics;
using System.Runtime.InteropServices;
using POE2Radar.Core;
using POE2Radar.Core.Stealth;
using POE2Radar.Overlay;

// ── Optional process-name randomization (opt-in via --stealth) ──
// When --stealth is passed, the overlay relaunches once under a random-named hardlink so the
// on-disk/process name carries no identifying tokens, then cleans up stray hardlinks from prior runs.
// Without --stealth, behavior is unchanged.
Stealth.Enabled = args.Contains("--stealth");
if (Stealth.Enabled && !args.Contains("--launched"))
{
    var currentExe = Environment.ProcessPath;
    if (currentExe != null)
    {
        var dir = Path.GetDirectoryName(currentExe)!;
        var baseName = Path.GetFileNameWithoutExtension(currentExe);
        var target = Path.Combine(dir, RandomName.Generate() + ".exe");
        try
        {
            if (!File.Exists(target))
                CreateHardLink(target, currentExe, IntPtr.Zero);
            Process.Start(new ProcessStartInfo(target, $"--launched --stealth --base {baseName}")
            {
                UseShellExecute = false,
                WorkingDirectory = dir,
            });
            return 0;
        }
        catch { /* hardlink failed — fall through and run directly */ }
    }
}

if (Stealth.Enabled)
{
    var runtimeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Overlay");
    Console.Title = runtimeName;
    Console.WriteLine(runtimeName);
    Console.WriteLine(new string('=', runtimeName.Length));
}
else
{
    Console.WriteLine("POE2Radar — map/radar overlay");
    Console.WriteLine("=============================");
}

using var process = ProcessHandle.AttachToPoE();
if (process is null)
{
    Console.Error.WriteLine("PoE2 not running (no matching process found).");
    return 1;
}
Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

var reader = new MemoryReader(process);

var slot = Bootstrap.ResolveGameStateSlot(process, reader);
if (slot == 0)
    return 2;

Console.WriteLine();
Console.WriteLine("Radar running. Open the in-game map to see terrain + entities.");
Console.WriteLine("Atlas: open it in-game; rings are auto-positioned. F10 over a tile = inspect its map/content/biome.");
Console.WriteLine("Ctrl+C to exit.");

using var app = new RadarApp(process, reader, slot);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; app.RequestShutdown(); };
app.Run();

// In --stealth mode, clean up stray random hardlinks left by prior runs (never the base exe or self).
if (Stealth.Enabled)
{
    var baseIdx = Array.IndexOf(args, "--base");
    var baseName = baseIdx >= 0 && baseIdx + 1 < args.Length ? args[baseIdx + 1] : null;
    var self = Environment.ProcessPath;
    var dir = self != null ? Path.GetDirectoryName(self) : null;
    if (baseName != null && self != null && dir != null)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.exe"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name != baseName && !string.Equals(f, self, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}

Console.WriteLine("Done.");
return 0;

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
