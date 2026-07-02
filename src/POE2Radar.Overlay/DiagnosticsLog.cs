using System.Text;

namespace POE2Radar.Overlay;

/// <summary>
/// Best-effort local diagnostics log. Two jobs, both aimed at making crash/offset reports actionable:
///  1) Tees ALL console output to <c>config/poe2gps.log</c> so users can COPY diagnostics (e.g. the
///     vital-offset auto-heal notice) from a file instead of selecting text in the console window — the
///     latter triggers Windows "Quick Edit" mode which freezes the process (see ConsoleTheme quick-edit
///     disable). Purely local; no network, no game reads. The log holds the same text already printed.
///  2) Registers process-wide handlers so an UNHANDLED exception (on any thread or an unobserved Task)
///     writes its stack trace to the log before the process dies — turning "and then it crashes" into a
///     stack trace we can act on.
/// Never throws into the caller; every path is guarded.
/// </summary>
internal static class DiagnosticsLog
{
    private static readonly object _lock = new();
    private static StreamWriter? _fw;
    private static bool _inited;

    /// <summary>Path of the log file (next to the exe, under config/). Shown to users so they can find it.</summary>
    public static string Path { get; private set; } = "";

    public static void Init()
    {
        if (_inited) return;
        _inited = true;
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "config");
            Directory.CreateDirectory(dir);
            Path = System.IO.Path.Combine(dir, "poe2gps.log");
            // Cap growth: start fresh if the previous log got large (~1 MB).
            try { if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000) File.Delete(Path); } catch { }
            _fw = new StreamWriter(Path, append: true) { AutoFlush = true };
            // Set the console encoding BEFORE installing the tee: assigning Console.OutputEncoding
            // recreates Console.Out, which would discard our tee if done afterwards. (ConsoleTheme no
            // longer sets it — this is the single place the console encoding is configured.)
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected/legacy — leave default */ }
            // Tee the console through us so every line printed is also captured to the file.
            Console.SetOut(new TeeTextWriter(Console.Out));
            WriteRaw($"\n=== POE2GPS v{UpdateChecker.Current} session start ===\n");
        }
        catch { _fw = null; }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteRaw($"[UNHANDLED] {(e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString()}\n");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteRaw($"[UNOBSERVED-TASK] {e.Exception}\n");
            e.SetObserved();   // don't escalate an unobserved task fault into a process kill
        };
    }

    /// <summary>Append raw text to the log file only (not the console). Thread-safe; swallows all errors.</summary>
    internal static void WriteRaw(string s)
    {
        lock (_lock)
        {
            try { _fw?.Write(s); } catch { }
        }
    }

    /// <summary>Console <see cref="TextWriter"/> that mirrors every write to the diagnostics file.
    /// Console.SetOut already wraps this in a synchronized writer, so the console path is serialized;
    /// the file path is additionally guarded by <see cref="WriteRaw"/>'s lock (shared with crash writes).</summary>
    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _console;
        public TeeTextWriter(TextWriter console) => _console = console;
        public override Encoding Encoding => _console.Encoding;
        public override void Write(char value) { _console.Write(value); WriteRaw(value.ToString()); }
        public override void Write(string? value) { _console.Write(value); if (value != null) WriteRaw(value); }
        public override void WriteLine(string? value) { _console.WriteLine(value); WriteRaw((value ?? "") + "\n"); }
        public override void Flush() { _console.Flush(); }
    }
}
