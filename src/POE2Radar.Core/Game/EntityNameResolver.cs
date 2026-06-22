using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

/// <summary>
/// Resolves opaque entity metadata paths (e.g. <c>Metadata/Monsters/Wraith/WraithSpookyLightning</c>)
/// to human-friendly names (<c>"Lightning Wraith"</c>) via a lookup table. The table is reference
/// data baked into the assembly as the embedded <c>entity_names.json</c> (a flat path→name map with
/// lower-cased keys), so the overlay ships self-contained — no config seeding needed. Read-only:
/// this never touches game memory; it only maps strings the live reader already extracted.
///
/// <para>Ported from the privately-shared fork's name DB, but adapted to our embedded-resource
/// convention (cf. <see cref="CustomLandmarkData"/>) and given a bounded prefix-fallback instead of
/// the fork's full-table scan.</para>
/// </summary>
public sealed class EntityNameResolver
{
    private readonly Dictionary<string, string> _names;
    private volatile IReadOnlyDictionary<string, string>? _overrides;

    private EntityNameResolver(Dictionary<string, string> names) => _names = names;

    /// <summary>The shared resolver, loaded once from the embedded table.</summary>
    public static EntityNameResolver Shared { get; } = LoadEmbedded();

    private static EntityNameResolver LoadEmbedded()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("entity_names"));
            if (resName != null)
            {
                using var stream = asm.GetManifestResourceStream(resName)!;
                var doc = JsonDocument.Parse(stream);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    names[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"EntityNames load failed: {ex.Message}");
        }
        return new EntityNameResolver(names);
    }

    /// <summary>Number of loaded name mappings (0 means the table failed to load).</summary>
    public int Count => _names.Count;

    /// <summary>
    /// Resolve a metadata path to a friendly name, or null if unknown. Consults the live user
    /// override layer first (exact, then progressively dropping trailing segments), then the embedded
    /// table the same way. Strips a trailing runtime area-level annotation ("...MonkeyJungle@34") since
    /// table keys never carry it.
    /// </summary>
    public string? Resolve(string metadataPath)
    {
        if (string.IsNullOrEmpty(metadataPath)) return null;
        var at = metadataPath.IndexOf('@');
        var path = at >= 0 ? metadataPath[..at] : metadataPath;

        var ov = _overrides; // volatile read once
        if (ov != null && LookupWithFallback(ov, path) is { } o) return o;
        return LookupWithFallback(_names, path);
    }

    // Exact match, then drop trailing "/segment"s and retry. Bounded by path depth.
    private static string? LookupWithFallback(IReadOnlyDictionary<string, string> table, string path)
    {
        if (table.TryGetValue(path, out var name)) return name;
        var probe = path;
        int slash;
        while ((slash = probe.LastIndexOf('/')) > 0)
        {
            probe = probe[..slash];
            if (table.TryGetValue(probe, out name)) return name;
        }
        return null;
    }

    /// <summary>
    /// Install a user override table (metadata→name), consulted BEFORE the embedded table by
    /// <see cref="Resolve"/>. Atomic swap (volatile) — lock-free for the many reader threads
    /// (world/render/API); call from the rare writer (startup load / a name edit). Null clears all
    /// overrides. Blank names are dropped so an empty override never shadows a real embedded name.
    /// </summary>
    public void SetUserOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0) { _overrides = null; return; }
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in overrides)
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v)) copy[k] = v;
        _overrides = copy.Count > 0 ? copy : null;
    }

    /// <summary>
    /// Friendly name if known, otherwise a best-effort short label derived from the path's last
    /// segment. A leading <c>[DNT-UNUSED]</c> marker (do-not-translate placeholders in the source
    /// table) is stripped.
    /// </summary>
    public string ResolveOrShorten(string metadataPath)
    {
        var resolved = Resolve(metadataPath);
        if (resolved != null)
        {
            const string dnt = "[DNT-UNUSED] ";
            if (resolved.StartsWith(dnt, StringComparison.Ordinal)) resolved = resolved[dnt.Length..];
            return resolved;
        }
        var at = metadataPath.IndexOf('@');
        var path = at >= 0 ? metadataPath[..at] : metadataPath;
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
