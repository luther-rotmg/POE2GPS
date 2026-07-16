using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace POE2Radar.Core.Palettes;

/// <summary>
/// Static store for user-authored palettes persisted as JSON files under
/// the config/palettes/ directory. All public methods operate on the default
/// path (sibling to the app config directory); internal overloads accept a
/// custom root directory for test isolation.
/// </summary>
public static class UserPaletteStore
{
    /// <summary>Regex for valid slugs: ^[a-z0-9-]{1,32}$</summary>
    private static readonly Regex SlugRegex = new("^[a-z0-9-]{1,32}$", RegexOptions.Compiled);

    /// <summary>Regex for valid hex colors: ^#[0-9a-fA-F]{6}$</summary>
    private static readonly Regex HexRegex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>The 13 required CSS custom property names.</summary>
    private static readonly HashSet<string> RequiredVarKeys = new(StringComparer.Ordinal)
    {
        "--gold", "--gold-bright", "--gold-deep",
        "--ink", "--ink-dim", "--ink-faint",
        "--panel", "--panel2",
        "--bg", "--bg-alt",
        "--line", "--line-soft",
        "--good",
    };

    /// <summary>Built-in palette slugs that cannot be used by user palettes.</summary>
    private static readonly HashSet<string> BuiltInSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "kalguuran", "terminal",
        "ultimatum-red", "sanctum-cream", "necropolis-amethyst",
        "delirium-static", "legion-bronze", "ritual-blood",
        "trial-ordeal", "blight-bloom",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Default palettes directory path.</summary>
    private static string DefaultRoot() =>
        Path.Combine(AppContext.BaseDirectory, "config", "palettes");

    /// <summary>List all saved palettes.</summary>
    public static IReadOnlyList<UserPalette> List() => List(DefaultRoot());

    /// <summary>Get a single palette by slug, or null if not found.</summary>
    public static UserPalette? Get(string slug) => Get(DefaultRoot(), slug);

    /// <summary>Save (create or overwrite) a palette. Returns the saved palette.</summary>
    public static UserPalette Save(UserPalette p) => Save(DefaultRoot(), p);

    /// <summary>Delete a palette by slug. Returns true if the file was removed.</summary>
    public static bool Delete(string slug) => Delete(DefaultRoot(), slug);

    // --- Internal overloads with explicit root directory (for test isolation) ---

    /// <summary>List all saved palettes under the given root directory.</summary>
    internal static IReadOnlyList<UserPalette> List(string rootDir)
    {
        EnsureDir(rootDir);
        var files = Directory.EnumerateFiles(rootDir, "*.json");
        var results = new List<UserPalette>();
        foreach (var f in files)
        {
            try
            {
                var json = File.ReadAllText(f);
                var dto = JsonSerializer.Deserialize<PaletteDto>(json, JsonOptions);
                if (dto != null && dto.Slug != null && dto.Vars != null)
                {
                    var preview = DerivePreview(dto.Vars);
                    results.Add(dto.ToRecord(preview));
                }
            }
            catch
            {
                // Skip corrupt files silently.
            }
        }
        return results;
    }

    /// <summary>Get a single palette by slug under the given root directory.</summary>
    internal static UserPalette? Get(string rootDir, string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var path = Path.Combine(rootDir, slug + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<PaletteDto>(json, JsonOptions);
            if (dto == null || dto.Slug == null || dto.Vars == null) return null;
            var preview = DerivePreview(dto.Vars);
            return dto.ToRecord(preview);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Save a palette under the given root directory. Validates slug and vars.</summary>
    internal static UserPalette Save(string rootDir, UserPalette p)
    {
        ValidateSlug(p.Slug);
        ValidateVars(p.Vars);

        var preview = DerivePreview(p.Vars);
        var dto = new PaletteDto
        {
            Slug = p.Slug,
            DisplayName = p.DisplayName,
            Vars = p.Vars.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Preview = preview.ToList(),
            CreatedUtc = p.CreatedUtc,
        };

        EnsureDir(rootDir);
        var targetPath = Path.Combine(rootDir, p.Slug + ".json");
        var tmpPath = targetPath + ".tmp";
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, targetPath, overwrite: true);

        return dto.ToRecord(preview);
    }

    /// <summary>Delete a palette by slug under the given root directory.</summary>
    internal static bool Delete(string rootDir, string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return false;
        var path = Path.Combine(rootDir, slug + ".json");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // --- Validation ---

    /// <summary>Validate a slug: length 1-32, lowercase alphanumeric + hyphens only, not a built-in slug.</summary>
    /// <exception cref="ArgumentException">If the slug is invalid or reserved.</exception>
    private static void ValidateSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || !SlugRegex.IsMatch(slug))
            throw new ArgumentException($"Invalid palette slug: \"{slug}\". Must be 1-32 lowercase alphanumeric characters or hyphens.");
        if (BuiltInSlugs.Contains(slug))
            throw new ArgumentException($"Slug \"{slug}\" is reserved for a built-in palette.");
    }

    /// <summary>Validate that the vars dictionary contains exactly the 13 required keys with valid hex values.</summary>
    /// <exception cref="ArgumentException">If vars are missing keys, have extra keys, or contain invalid hex values.</exception>
    private static void ValidateVars(IReadOnlyDictionary<string, string> vars)
    {
        if (vars == null)
            throw new ArgumentException("Vars dictionary must not be null.");

        if (vars.Count != RequiredVarKeys.Count)
        {
            var missing = RequiredVarKeys.Except(vars.Keys).ToList();
            var extra = vars.Keys.Except(RequiredVarKeys).ToList();
            var msg = $"Vars must contain exactly {RequiredVarKeys.Count} keys.";
            if (missing.Count > 0) msg += $" Missing: {string.Join(", ", missing)}.";
            if (extra.Count > 0) msg += $" Extra: {string.Join(", ", extra)}.";
            throw new ArgumentException(msg);
        }

        foreach (var (key, value) in vars)
        {
            if (!RequiredVarKeys.Contains(key))
                throw new ArgumentException($"Unexpected var key: \"{key}\".");
            if (string.IsNullOrWhiteSpace(value) || !HexRegex.IsMatch(value))
                throw new ArgumentException($"Var \"{key}\" has invalid hex value: \"{value}\". Must be #RRGGBB.");
        }
    }

    // --- Helpers ---

    /// <summary>Auto-derive the preview array from vars: [--bg, --panel, --gold, --ink].</summary>
    private static List<string> DerivePreview(IReadOnlyDictionary<string, string> vars)
    {
        var list = new List<string>(4);
        list.Add(vars.TryGetValue("--bg", out var bg) ? bg : "#000000");
        list.Add(vars.TryGetValue("--panel", out var panel) ? panel : "#000000");
        list.Add(vars.TryGetValue("--gold", out var gold) ? gold : "#000000");
        list.Add(vars.TryGetValue("--ink", out var ink) ? ink : "#000000");
        return list;
    }

    private static void EnsureDir(string rootDir)
    {
        if (!Directory.Exists(rootDir))
            Directory.CreateDirectory(rootDir);
    }

    // --- DTO for JSON serialization (camelCase, round-trip stable) ---

    private sealed class PaletteDto
    {
        public string? Slug { get; set; }
        public string? DisplayName { get; set; }
        public Dictionary<string, string>? Vars { get; set; }
        public List<string>? Preview { get; set; }
        public DateTime CreatedUtc { get; set; }

        public UserPalette ToRecord(IReadOnlyList<string> preview)
        {
            return new UserPalette(
                Slug ?? string.Empty,
                DisplayName ?? string.Empty,
                Vars!,
                preview,
                CreatedUtc);
        }
    }
}