namespace POE2Radar.Core.Update;

/// <summary>
/// Pure decision logic for the self-updater — no IO, no game reads. All the drift-prone / spoof-prone
/// choices (is a release newer? which asset URL? which checksum line? should we retry a failing target?)
/// live here so they can be unit-tested. The Overlay <c>AutoUpdater</c> does the actual download/swap.
/// </summary>
public static class AutoUpdatePolicy
{
    /// <summary>Per-target retry state persisted next to the exe (JSON). A NEW target resets the gate.</summary>
    public record UpdateState(string TargetVersion, int Failures);

    /// <summary>True iff <paramref name="latest"/> is a strictly higher semver than <paramref name="current"/>.</summary>
    public static bool IsNewer(string current, string latest)
    {
        var c = Parse(current); var l = Parse(latest);
        if (c is null || l is null) return false;
        for (var i = 0; i < 3; i++) if (l[i] != c[i]) return l[i] > c[i];
        return false;
    }

    /// <summary>Release zip asset name — uses the RAW tag (with its leading v), matching release.yml.</summary>
    public static string AssetName(string tag) => $"POE2GPS-{tag}-win-x64.zip";

    /// <summary>SHA-256 checksum asset name for a release.</summary>
    public static string ChecksumAssetName(string tag) => $"POE2GPS-{tag}-sha256.txt";

    public static string ZipUrl(string repo, string tag)
        => $"https://github.com/{repo}/releases/download/{tag}/{AssetName(tag)}";

    public static string ChecksumUrl(string repo, string tag)
        => $"https://github.com/{repo}/releases/download/{tag}/{ChecksumAssetName(tag)}";

    /// <summary>Find the download URL of the release zip asset for <paramref name="tag"/> (case-insensitive).</summary>
    public static string? SelectAsset(IEnumerable<(string Name, string Url)> assets, string tag)
    {
        var want = AssetName(tag);
        foreach (var a in assets)
            if (string.Equals(a.Name, want, StringComparison.OrdinalIgnoreCase)) return a.Url;
        return null;
    }

    /// <summary>Don't keep retrying a target that has already failed <paramref name="maxFailures"/> times.</summary>
    public static bool ShouldAttempt(UpdateState? state, string latest, int maxFailures = 2)
    {
        if (state is null) return true;
        if (!string.Equals(state.TargetVersion, latest, StringComparison.OrdinalIgnoreCase)) return true;
        return state.Failures < maxFailures;
    }

    /// <summary>Parse a sha256sum-format file for the hash of <paramref name="assetName"/> ("&lt;hash&gt;␠␠&lt;file&gt;").</summary>
    public static string? ExpectedSha(string checksumFileText, string assetName)
    {
        foreach (var raw in checksumFileText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var hash = line[..sp];
            var file = line[(sp + 1)..].TrimStart(' ', '*'); // sha256sum uses two spaces / '*' for binary mode
            if (string.Equals(file, assetName, StringComparison.OrdinalIgnoreCase)) return hash;
        }
        return null;
    }

    private static int[]? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        var parts = s.Split('.', '-');
        var v = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length || !int.TryParse(parts[i], out v[i])) { if (i == 0) return null; v[i] = 0; }
        }
        return v;
    }
}
