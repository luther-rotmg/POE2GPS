using System.Runtime.InteropServices;
using System.Text;

namespace POE2Radar.Overlay.Overlay;

/// <summary>Themed console output for the POE2GPS startup banner + status. BODY text only — the console
/// window TITLE stays neutral (anti-detection). Enables ANSI/VT color once with a graceful plain-text
/// fallback. Console-only; touches no game state.</summary>
internal static class ConsoleTheme
{
    private static bool _noColor;
    private static bool _inited;

    private const string Reset = "\x1b[0m";
    private static string Gold  => _noColor ? "" : "\x1b[38;5;179m";
    private static string Cyan  => _noColor ? "" : "\x1b[38;5;80m";
    private static string Dim   => _noColor ? "" : "\x1b[38;5;245m";
    private static string Green => _noColor ? "" : "\x1b[38;5;114m";
    private static string Amber => _noColor ? "" : "\x1b[38;5;215m";
    private static string R      => _noColor ? "" : Reset;

    // "POE2GPS" — ANSI Shadow figlet (body only; never the window title).
    private static readonly string[] Art =
    {
        "██████╗  ██████╗ ███████╗██████╗  ██████╗ ██████╗ ███████╗",
        "██╔══██╗██╔═══██╗██╔════╝╚════██╗██╔════╝ ██╔══██╗██╔════╝",
        "██████╔╝██║   ██║█████╗   █████╔╝██║  ███╗██████╔╝███████╗",
        "██╔═══╝ ██║   ██║██╔══╝  ██╔═══╝ ██║   ██║██╔═══╝ ╚════██║",
        "██║     ╚██████╔╝███████╗███████╗╚██████╔╝██║     ███████║",
        "╚═╝      ╚═════╝ ╚══════╝╚══════╝ ╚═════╝ ╚═╝     ╚══════╝",
    };

    private static void Init()
    {
        if (_inited) return;
        _inited = true;
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* leave default */ }
        if (!EnableVt()) _noColor = true;
    }

    public static void Banner()
    {
        Init();
        Console.WriteLine();
        foreach (var line in Art) Console.WriteLine(Gold + line + R);
        Console.WriteLine($"  {Cyan}read-only PoE2 GPS overlay{R}   {Dim}v{UpdateChecker.Current}{R}");
        Console.WriteLine();
    }

    public static void Section(string label) => Console.WriteLine($"{Dim}── {label} ──{R}");
    public static void Kv(string key, string value) => Console.WriteLine($"  {Dim}{key,-16}{R}{value}");
    public static void Ok(string text) => Console.WriteLine($"{Green}{text}{R}");
    public static void Accent(string text) => Console.WriteLine($"{Cyan}{text}{R}");
    public static void WarnLine(string text) => Console.WriteLine($"{Amber}{text}{R}");

    public static void Hotkeys()
    {
        Init();
        Console.WriteLine();
        Console.WriteLine($"{Gold}  HOTKEYS{R}  {Dim}(read-only · only while PoE2 is focused){R}");
        foreach (var (k, d) in HotkeyList)
            Console.WriteLine($"   {Cyan}{k,-20}{R}{Dim}{d}{R}");
        Console.WriteLine();
    }

    private static readonly (string Key, string Desc)[] HotkeyList =
    {
        ("F12", "open the web dashboard"),
        ("F6 / F7", "route to nearest / clear routes"),
        ("F10", "(Atlas) inspect tile · set route"),
        ("Ctrl+Alt+ ] / [", "cycle active nav target"),
        ("Ctrl+Alt+ 1-9 / 0", "jump to target slot / clear"),
        ("Ctrl+Alt+M / L3+R3", "toggle the nav-menu list"),
        ("F9", "quit (or tray → Exit)"),
    };

    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    private static bool EnableVt()
    {
        try
        {
            var h = GetStdHandle(STD_OUTPUT_HANDLE);
            if (h == 0 || h == -1) return false;
            if (!GetConsoleMode(h, out var mode)) return false;
            return SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch { return false; }
    }
}
