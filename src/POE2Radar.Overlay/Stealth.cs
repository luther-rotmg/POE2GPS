namespace POE2Radar.Overlay;

/// <summary>
/// Opt-in process/identity randomization, enabled by the <c>--stealth</c> command-line flag.
/// When off (the default), behavior is identical to a normal launch. When on: the overlay relaunches
/// under a random-named hardlink, uses a random window class/title, and does not expose the character
/// name on the dashboard API.
/// </summary>
internal static class Stealth
{
    public static bool Enabled;
}
