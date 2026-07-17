using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace POE2Radar.Core.OverlayLayouts;

/// <summary>
/// Static store for the overlay layouts config file. Reads and writes
/// <c>overlay-layouts.json</c> from the given config directory. Uses atomic writes
/// (tmp + File.Move) matching the existing <see cref="Palettes.UserPaletteStore"/> pattern.
/// </summary>
public static class OverlayLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Load the overlay layouts file from <c>configDir/overlay-layouts.json</c>.
    /// Returns an empty <c>OverlayLayoutFile(1, [])</c> if the file does not exist.
    /// Throws <see cref="InvalidDataException"/> on malformed JSON.
    /// </summary>
    public static OverlayLayoutFile Load(string configDir)
    {
        var path = Path.Combine(configDir, "overlay-layouts.json");
        if (!File.Exists(path))
            return new OverlayLayoutFile(1, Array.Empty<OverlayLayoutPreset>());

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<OverlayLayoutFile>(json, JsonOptions);
            return file ?? new OverlayLayoutFile(1, Array.Empty<OverlayLayoutPreset>());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Failed to parse overlay-layouts.json. The file is malformed.", ex);
        }
    }

    /// <summary>
    /// Save the overlay layouts file to <c>configDir/overlay-layouts.json</c> using an
    /// atomic write (.tmp + File.Move). Validates every preset before writing.
    /// Throws <see cref="ArgumentException"/> on any validation failure.
    /// </summary>
    public static void Save(string configDir, OverlayLayoutFile file)
    {
        if (file.Presets.Count > 10)
            throw new ArgumentException($"Preset count {file.Presets.Count} exceeds maximum of 10.");

        // Validate each preset and check for duplicate names (case-insensitive).
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in file.Presets)
        {
            ValidatePreset(preset);

            if (!seenNames.Add(preset.Name))
                throw new ArgumentException($"Duplicate preset name \"{preset.Name}\" (case-insensitive). All preset names must be unique.");
        }

        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        var path = Path.Combine(configDir, "overlay-layouts.json");
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>
    /// Validate a single preset. Throws <see cref="ArgumentException"/> on any failure.
    /// </summary>
    /// <param name="preset">The preset to validate.</param>
    /// <exception cref="ArgumentException">If the preset fails any validation check.</exception>
    public static void ValidatePreset(OverlayLayoutPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Name))
            throw new ArgumentException("Preset name must not be null or empty.");

        if (preset.Name.Length > 40)
            throw new ArgumentException($"Preset name must be at most 40 characters, got {preset.Name.Length}.");

        if (string.IsNullOrWhiteSpace(preset.Match))
            throw new ArgumentException("Preset match pattern must not be null or empty.");

        if (preset.Match.Length > 64)
            throw new ArgumentException($"Preset match pattern must be at most 64 characters, got {preset.Match.Length}.");

        if (preset.Panels == null)
            throw new ArgumentException("Panels dictionary must not be null.");

        if (preset.Panels.Count > 32)
            throw new ArgumentException($"Panels dictionary has {preset.Panels.Count} entries, exceeding maximum of 32.");

        foreach (var key in preset.Panels.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Panel key must not be null or empty.");

            if (key.Length > 64)
                throw new ArgumentException($"Panel key must be at most 64 characters, got {key.Length}.");
        }
    }
}