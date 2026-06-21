using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Overlay;

/// <summary>
/// One-shot "am I out of date?" check against GitHub. Best-effort and non-blocking: queries the latest
/// published Release (falling back to the tag list if there are no formal Releases), parses the semver,
/// and compares it to this build's <see cref="Current"/>. Never throws into the caller — on any failure
/// (offline, rate-limited, parse error) it just reports "no update known". The result is surfaced in the
/// console banner + the dashboard so the user knows to grab a newer build.
/// </summary>
internal static class UpdateChecker
{
    private const string Repo = "luther-rotmg/POE2GPS";
    public static readonly string ReleasesPage = $"https://github.com/{Repo}/releases";

    /// <summary>This build's version ("0.7.0"), from the assembly version baked in by the csproj.</summary>
    public static string Current
    {
        get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}"; }
    }

    public sealed record Result(string Current, string? Latest, bool UpdateAvailable, string Url);

    /// <summary>Check GitHub for a newer version. Always returns a Result (never throws).</summary>
    public static async Task<Result> CheckAsync()
    {
        var current = Current;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("POE2GPS-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string? latest = null; var url = ReleasesPage;

            // Preferred: the latest published Release (has a download page + assets).
            var rel = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (rel.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await rel.Content.ReadAsStringAsync());
                latest = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (doc.RootElement.TryGetProperty("html_url", out var h) && h.GetString() is { Length: > 0 } hu) url = hu;
            }
            else
            {
                // No formal Releases — fall back to the tag list and pick the highest semver.
                using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/tags"));
                int[] best = { -1, 0, 0 };
                foreach (var tag in doc.RootElement.EnumerateArray())
                {
                    var name = tag.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var v = Parse(name);
                    if (v != null && Cmp(v, best) > 0) { best = v; latest = name; }
                }
            }

            var lv = Parse(latest);
            var available = lv != null && Cmp(lv, Parse(current)!) > 0;
            return new Result(current, latest, available, url);
        }
        catch
        {
            return new Result(current, null, false, ReleasesPage);
        }
    }

    /// <summary>Parse "vX.Y.Z" / "X.Y.Z" → [major,minor,patch]; null if not a version string.</summary>
    private static int[]? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        var parts = s.Split('.', '-');
        var v = new int[3];
        for (var i = 0; i < 3; i++) { if (i >= parts.Length || !int.TryParse(parts[i], out v[i])) { if (i == 0) return null; v[i] = 0; } }
        return v;
    }

    private static int Cmp(int[] a, int[] b)
    {
        for (var i = 0; i < 3; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }
}
