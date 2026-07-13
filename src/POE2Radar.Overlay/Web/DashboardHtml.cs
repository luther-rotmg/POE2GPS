using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Dashboard shell served at <c>GET /</c> by <see cref="ApiServer"/>. HTML/CSS/JS are stored as
/// embedded assets in <c>Web/Assets/dashboard.{html,css,js}</c> and assembled on first access via
/// <see cref="AssemblePage"/>. The Console tab reads/writes radar/visual settings via
/// <c>/api/settings</c> (the only writes it makes — flags + calibration, never flask/automation);
/// the Filters tab manages watched/hidden lists via <c>/api/watched</c> / <c>/api/hidden</c>;
/// the Dashboard tab polls the same-origin read endpoints (<c>/state</c>, <c>/entities</c>,
/// <c>/landmarks</c>, <c>/api/nav</c>).
/// </summary>
internal static class DashboardHtml
{
    // ── EC2 attribution surface (DRAFT phase) ───────────────────────
    // Route data ported from https://github.com/syrairc/ExileCampaigns2 with syrairc's verbal go-ahead.
    // The four `TODO(syrairc-*)` sentinels below are LOAD-BEARING for CI attribution gate.
    public const string CampaignGuideAttribution =
        "Campaign step guide by syrairc (ExileCampaigns2 — click to view)";
    public const string CampaignGuideUpstreamUrl =
        "https://github.com/syrairc/ExileCampaigns2";
    public const string CampaignGuideLicense = "TODO(syrairc-license)";
    public const string CampaignGuideCommit  = "TODO(syrairc-hash)";

    // v0.33 #29a-c: byte-exact page assembly via embedded assets.
    private static readonly Lazy<string> _pageCache =
        new(AssemblePage, LazyThreadSafetyMode.PublicationOnly);

    public static string Page => _pageCache.Value;

    private static string AssemblePage()
    {
        var html = LoadEmbeddedText("POE2Radar.Overlay.Web.Assets.dashboard.html", "dashboard.html");
        var css  = LoadEmbeddedText("POE2Radar.Overlay.Web.Assets.dashboard.css",  "dashboard.css");
        var js   = LoadEmbeddedText("POE2Radar.Overlay.Web.Assets.dashboard.js",   "dashboard.js");
        return html
            .Replace("/*DASHBOARD_STYLE_INLINE_v1*/",  css)
            .Replace("/*DASHBOARD_SCRIPT_INLINE_v1*/", js);
    }

    private static string LoadEmbeddedText(string resourceName, string friendlyName)
    {
        using var stream = typeof(DashboardHtml).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource missing: {friendlyName} ({resourceName}). " +
                "Check POE2Radar.Overlay.csproj EmbeddedResource include path.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }
}
