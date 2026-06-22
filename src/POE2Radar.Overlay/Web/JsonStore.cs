using System.Text.Json;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Shared helper for the local JSON config stores (<see cref="ModCatalog"/>, <see cref="SeenPoiLog"/>,
/// <see cref="EntityAtlasLog"/>, <see cref="EntityNameStore"/>). Read-only w.r.t. the game — only the
/// overlay's own config files.
/// </summary>
internal static class JsonStore
{
    /// <summary>
    /// Serialize + write atomically: write to a sibling <c>.tmp</c> file, then replace the target in one
    /// move. A crash or kill mid-write can never leave a half-written (corrupt) config file — the old
    /// file stays intact until the complete new one is swapped in. Creates the directory if needed.
    /// </summary>
    public static void AtomicWrite<T>(string path, T value, JsonSerializerOptions options)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Load + deserialize a config file. Returns false (with a logged warning) on a missing or corrupt
    /// file so the caller simply keeps its in-memory defaults — the single-sourced "load or keep
    /// defaults" policy. The caller does its own post-load population from <paramref name="value"/>.
    /// </summary>
    public static bool TryLoad<T>(string path, JsonSerializerOptions options, string label, out T? value)
    {
        value = default;
        try
        {
            if (!File.Exists(path)) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
            return value is not null;
        }
        catch (Exception ex) { Console.Error.WriteLine($"{label} load failed: {ex.Message}"); return false; }
    }
}
