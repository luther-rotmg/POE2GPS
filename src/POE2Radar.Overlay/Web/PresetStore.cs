using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using POE2Radar.Core.Config;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Web;

/// <summary>One entry in the preset list returned by <see cref="PresetStore.List"/>.</summary>
public sealed record PresetEntry(string Name, bool BuiltIn);

/// <summary>
/// Owns the built-in preset library (three embedded resources) and the user preset folder
/// (<c>config/presets/*.poe2preset</c>).  Threading: mutation is guarded by <c>_gate</c>;
/// <see cref="List"/> returns an immutable snapshot so the API thread never races with a
/// concurrent <see cref="Save"/> or <see cref="Delete"/>.  Mirrors the WatchedEntities /
/// DisplayRules lock+volatile-snapshot pattern.
/// </summary>
public sealed class PresetStore
{
    // Directory for user presets, next to config/ (same parent as the exe).
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "config", "presets");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    // ── Built-in registry (immutable after ctor) ──────────────────────────────────────────
    // Each entry: display name → JSON string loaded from the embedded .poe2preset resource.
    private readonly IReadOnlyList<(string Name, string Json)> _builtIns;

    // ── Mutable user state ─────────────────────────────────────────────────────────────────
    private readonly object _gate = new();
    private volatile IReadOnlyList<PresetEntry> _snapshot = Array.Empty<PresetEntry>();

    public PresetStore()
    {
        _builtIns = LoadBuiltIns();
        RebuildSnapshot();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Built-ins (BuiltIn=true) first, then user files sorted by name, excluding the
    /// automatic backup created before an import (<c>backup-before-import.poe2preset</c>).</summary>
    public IReadOnlyList<PresetEntry> List() => _snapshot;

    /// <summary>Return the raw JSON for a named preset (built-in resource or user file), or false
    /// if the name is unknown.</summary>
    public bool TryGet(string name, out string json)
    {
        // Built-in first (case-insensitive).
        foreach (var (n, j) in _builtIns)
        {
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                json = j;
                return true;
            }
        }

        // User file.
        var safe = PresetName.Sanitize(name);
        var path = Path.Combine(Dir, safe + ".poe2preset");
        if (File.Exists(path))
        {
            json = File.ReadAllText(path);
            return true;
        }

        json = "";
        return false;
    }

    /// <summary>Persist a preset JSON to <c>config/presets/&lt;sanitized&gt;.poe2preset</c> atomically,
    /// then rebuild the in-memory snapshot.</summary>
    public void Save(string name, string json)
    {
        var safe = PresetName.Sanitize(name);
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, safe + ".poe2preset");
        var tmp  = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
        lock (_gate) RebuildSnapshot();
    }

    /// <summary>Delete a user preset file.  Returns false if the name is a built-in or the file
    /// does not exist.</summary>
    public bool Delete(string name)
    {
        if (IsBuiltIn(name)) return false;
        var safe = PresetName.Sanitize(name);
        var path = Path.Combine(Dir, safe + ".poe2preset");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        lock (_gate) RebuildSnapshot();
        return true;
    }

    /// <summary>True if <paramref name="name"/> matches a built-in preset (case-insensitive).</summary>
    public bool IsBuiltIn(string name)
    {
        foreach (var (n, _) in _builtIns)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Load the three embedded preset resources and extract their <c>name</c> field.
    /// Called once in the constructor; result is immutable.</summary>
    private static IReadOnlyList<(string Name, string Json)> LoadBuiltIns()
    {
        var result = new List<(string, string)>(3);
        var asm = Assembly.GetExecutingAssembly();
        var resources = asm.GetManifestResourceNames();

        foreach (var stem in new[] { "high_contrast", "minimal", "boss_hunter" })
        {
            var resName = Array.Find(resources, n => n.Contains(stem, StringComparison.Ordinal));
            if (resName == null)
            {
                Console.Error.WriteLine($"PresetStore: embedded resource not found for '{stem}'");
                continue;
            }

            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null)
            {
                Console.Error.WriteLine($"PresetStore: could not open stream for '{resName}'");
                continue;
            }

            using var sr   = new StreamReader(stream);
            var json        = sr.ReadToEnd();

            // Extract the display name from the JSON so List() returns a human-readable label.
            var displayName = stem; // fallback
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("name", out var nameProp) &&
                    nameProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameProp.GetString()))
                {
                    displayName = nameProp.GetString()!;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"PresetStore: failed to parse name from '{stem}': {ex.Message}");
            }

            result.Add((displayName, json));
        }

        return result.AsReadOnly();
    }

    /// <summary>Rebuild the immutable <see cref="_snapshot"/> list from the current user-file state.
    /// Must be called under <c>_gate</c> for mutations, or in the constructor before the object
    /// is shared.</summary>
    private void RebuildSnapshot()
    {
        var entries = new List<PresetEntry>(_builtIns.Count + 8);

        // Built-ins first.
        foreach (var (name, _) in _builtIns)
            entries.Add(new PresetEntry(name, BuiltIn: true));

        // User files: enumerate config/presets/*.poe2preset, sorted by name, excluding backup.
        if (Directory.Exists(Dir))
        {
            var files = Directory.GetFiles(Dir, "*.poe2preset")
                                 .OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var stem = Path.GetFileNameWithoutExtension(file);

                // Exclude the auto-backup created before an import.
                if (stem.StartsWith("backup-before-import", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Try to read the display name from the JSON; fall back to the filename stem.
                var displayName = stem;
                try
                {
                    var text = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("name", out var np) &&
                        np.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(np.GetString()))
                    {
                        displayName = np.GetString()!;
                    }
                }
                catch { /* ignore corrupt files — still list them by stem */ }

                entries.Add(new PresetEntry(displayName, BuiltIn: false));
            }
        }

        _snapshot = entries.AsReadOnly();
    }
}
