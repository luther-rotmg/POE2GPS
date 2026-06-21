using System.Diagnostics;
using System.Runtime.InteropServices;
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
Console.Title = myName;
Console.WriteLine(myName);
Console.WriteLine(new string('=', myName.Length));

using var process = ProcessHandle.AttachToPoE();
if (process is null)
{
    Console.Error.WriteLine("Game not running (no matching process found).");
    return 1;
}
Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

var reader = new MemoryReader(process);
var slot = Bootstrap.ResolveGameStateSlot(process, reader);
if (slot == 0)
    return 2;

Console.WriteLine();
Console.WriteLine("Running. Open the in-game map to see terrain + entities. Ctrl+C to exit.");

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
