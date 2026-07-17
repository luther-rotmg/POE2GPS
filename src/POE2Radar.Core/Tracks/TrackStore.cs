using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace POE2Radar.Core.Tracks;

/// <summary>
/// v0.40 Cartographer: append-only per-character per-zone position log at
/// <c>config/tracks/&lt;sanitized-character&gt;/&lt;zone-code&gt;.jsonl</c>.
/// Called at ~1 Hz from the render thread's world-tick observer.
///
/// Format: one JSON object per line, camelCase, keys t/x/y where t is
/// milliseconds since zone entry (relative, not wall clock — avoids timezone
/// leakage and makes route replay deterministic).
///
/// Ring cap: 10000 samples per file. At cap, drops oldest 1000 samples in a
/// single read-rewrite pass.
///
/// File contention (multiple ticks racing on the same file, or a reader
/// concurrent with a writer) is handled with a 3-attempt retry loop with a
/// short backoff — Append returns false on ultimate failure rather than
/// throwing so the render thread never observes an exception from this path.
/// </summary>
public static class TrackStore
{
    private const int RingCap = 10000;
    private const int RingKeep = 9000;

    internal static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        var result = sb.ToString();
        return string.IsNullOrEmpty(result.Replace("_", "").Replace("-", "")) ? string.Empty : result;
    }

    private static string TracksRoot(string configDir) => Path.Combine(configDir, "tracks");

    private static string CharacterDir(string configDir, string character)
    {
        var slug = Sanitize(character);
        return string.IsNullOrEmpty(slug) ? string.Empty : Path.Combine(TracksRoot(configDir), slug);
    }

    private static string ZoneFile(string configDir, string character, string zoneCode)
    {
        var charDir = CharacterDir(configDir, character);
        if (string.IsNullOrEmpty(charDir)) return string.Empty;
        var zone = Sanitize(zoneCode);
        return string.IsNullOrEmpty(zone) ? string.Empty : Path.Combine(charDir, zone + ".jsonl");
    }

    /// <summary>
    /// Appends a single sample to the character+zone track file. Returns false
    /// on invalid character/zone or if the write ultimately failed after retry.
    /// Never throws.
    /// </summary>
    public static bool Append(string configDir, string character, string zoneCode, TrackSample sample)
    {
        var path = ZoneFile(configDir, character, zoneCode);
        if (string.IsNullOrEmpty(path)) return false;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch { return false; }
        }

        var line = JsonSerializer.Serialize(sample) + "\n";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.AppendAllText(path, line);
                EnsureUnderRingCap(path);
                return true;
            }
            catch (IOException)
            {
                if (attempt == 2) return false;
                Thread.Sleep(50);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static void EnsureUnderRingCap(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length <= RingCap) return;
            var kept = lines.Skip(lines.Length - RingKeep).ToArray();
            File.WriteAllLines(path, kept);
        }
        catch { /* best-effort — skip on any error */ }
    }

    /// <summary>Loads all samples for a (character, zone). Empty list on missing file. Skips malformed lines silently.</summary>
    public static IReadOnlyList<TrackSample> Load(string configDir, string character, string zoneCode)
    {
        var path = ZoneFile(configDir, character, zoneCode);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<TrackSample>();

        var samples = new List<TrackSample>();
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return Array.Empty<TrackSample>(); }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<TrackSample>(line);
                if (s is not null) samples.Add(s);
            }
            catch { /* skip malformed line */ }
        }
        return samples;
    }

    /// <summary>Zone codes present for a character (bare filename minus .jsonl).</summary>
    public static IReadOnlyList<string> ListZones(string configDir, string character)
    {
        var dir = CharacterDir(configDir, character);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return Array.Empty<string>();

        try
        {
            return Directory.GetFiles(dir, "*.jsonl")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Character slugs with at least one track file.</summary>
    public static IReadOnlyList<string> ListCharacters(string configDir)
    {
        var root = TracksRoot(configDir);
        if (!Directory.Exists(root)) return Array.Empty<string>();

        try
        {
            return Directory.GetDirectories(root)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
}
