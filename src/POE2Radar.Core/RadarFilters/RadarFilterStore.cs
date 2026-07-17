using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace POE2Radar.Core.RadarFilters;

/// <summary>
/// Static store for the radar filters configuration file. Reads and writes
/// <c>radar-filters.json</c> from the given config directory. Uses atomic
/// writes (.tmp + File.Move) matching the existing store pattern.
/// </summary>
public static class RadarFilterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private const string FileName = "radar-filters.json";
    private const int MaxPresets = 20;
    private const int MaxPatternsPerList = 50;
    private const int MinMatchLength = 1;
    private const int MaxMatchLength = 64;
    private const int MinEntryLength = 1;
    private const int MaxEntryLength = 128;

    /// <summary>
    /// Load the radar filters file from <c>configDir/radar-filters.json</c>.
    /// Returns an empty <c>RadarFilterFile(1, [])</c> if the file does not exist.
    /// Throws <see cref="InvalidDataException"/> on malformed JSON, or
    /// <see cref="ArgumentException"/> if any preset fails validation.
    /// </summary>
    public static RadarFilterFile Load(string configDir)
    {
        var path = Path.Combine(configDir, FileName);
        if (!File.Exists(path))
            return new RadarFilterFile(1, Array.Empty<RadarFilterPreset>());

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<RadarFilterFile>(json, JsonOptions);
            if (file == null)
                return new RadarFilterFile(1, Array.Empty<RadarFilterPreset>());

            foreach (var preset in file.Presets)
                ValidatePreset(preset);

            return file;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Failed to parse radar-filters.json. The file is malformed.", ex);
        }
    }

    /// <summary>
    /// Save the radar filters file to <c>configDir/radar-filters.json</c>
    /// using an atomic write (.tmp + File.Move). Validates every preset before
    /// writing. Throws <see cref="ArgumentException"/> if any validation fails,
    /// if <c>file.Presets.Count &gt; 20</c>, or if any list exceeds 50 entries.
    /// </summary>
    public static void Save(string configDir, RadarFilterFile file)
    {
        if (file.Presets.Count > MaxPresets)
            throw new ArgumentException($"Preset count {file.Presets.Count} exceeds maximum of {MaxPresets}.");

        foreach (var preset in file.Presets)
        {
            if (preset.Whitelist.Count > MaxPatternsPerList)
                throw new ArgumentException($"Whitelist has {preset.Whitelist.Count} entries, exceeds maximum of {MaxPatternsPerList}.");
            if (preset.Blacklist.Count > MaxPatternsPerList)
                throw new ArgumentException($"Blacklist has {preset.Blacklist.Count} entries, exceeds maximum of {MaxPatternsPerList}.");
            ValidatePreset(preset);
        }

        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        var path = Path.Combine(configDir, FileName);
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>
    /// Validate a single preset. Throws <see cref="ArgumentException"/> on any
    /// validation failure per decision #3.
    /// </summary>
    /// <param name="preset">The preset to validate.</param>
    /// <exception cref="ArgumentException">If the preset fails any validation check.</exception>
    public static void ValidatePreset(RadarFilterPreset preset)
    {
        // Validate match pattern
        if (preset.Match == null)
            throw new ArgumentException("Match pattern must not be null.");
        if (preset.Match.Length < MinMatchLength || preset.Match.Length > MaxMatchLength)
            throw new ArgumentException($"Match pattern must be {MinMatchLength}-{MaxMatchLength} characters, got {preset.Match.Length}.");

        // Validate whitelist entries
        ValidatePatternList(preset.Whitelist, "Whitelist");

        // Validate blacklist entries
        ValidatePatternList(preset.Blacklist, "Blacklist");
    }

    private static void ValidatePatternList(IReadOnlyList<string> entries, string listName)
    {
        if (entries == null)
            throw new ArgumentException($"{listName} must not be null.");

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry == null)
                throw new ArgumentException($"{listName} entry at index {i} must not be null.");
            if (entry.Length < MinEntryLength || entry.Length > MaxEntryLength)
                throw new ArgumentException($"{listName} entry at index {i} must be {MinEntryLength}-{MaxEntryLength} characters, got {entry.Length}.");
            if (string.IsNullOrWhiteSpace(entry))
                throw new ArgumentException($"{listName} entry at index {i} must not be whitespace-only.");
        }
    }
}