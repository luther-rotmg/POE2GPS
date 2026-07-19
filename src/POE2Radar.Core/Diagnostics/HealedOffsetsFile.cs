using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// Healed-offset persistence file format. Each entry records a symbol name, the configured offset
/// (from <c>Poe2Offsets</c>), the healed (runtime-discovered) offset, and the UTC timestamp of
/// the heal event. Offsets are stored as decimal integers (not hex strings) — smaller, unambiguous,
/// and <c>System.Text.Json</c> native.
/// </summary>
/// <param name="SchemaVersion">Schema version for forward-compatibility. Current: 1.</param>
/// <param name="Healed">The list of healed offset entries.</param>
public sealed record HealedOffsetsFileContent(int SchemaVersion, IReadOnlyList<HealedEntry> Healed);

/// <summary>One healed offset entry, stored in <c>healed-offsets.json</c>.</summary>
public sealed record HealedEntry(string Symbol, int Configured, int Healed, DateTime HealedUtc);

/// <summary>
/// Atomic-write JSON file store for healed offsets. Writes to <c>healed-offsets.json</c> under a
/// config directory. Uses .tmp + File.Move for atomicity, matching the existing
/// <see cref="Rules.RulesFileStore"/> / <see cref="Palettes.UserPaletteStore"/> pattern.
/// </summary>
public static class HealedOffsetsFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Load healed offsets from <c>configDir/healed-offsets.json</c>.
    /// Returns an empty <c>HealedOffsetsFileContent(1, [])</c> if the file does not exist.
    /// Throws <see cref="InvalidDataException"/> on malformed JSON.
    /// </summary>
    public static HealedOffsetsFileContent Load(string configDir)
    {
        var path = Path.Combine(configDir, "healed-offsets.json");
        if (!File.Exists(path))
            return new HealedOffsetsFileContent(1, Array.Empty<HealedEntry>());

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<HealedOffsetsFileContent>(json, JsonOptions);
            return file ?? new HealedOffsetsFileContent(1, Array.Empty<HealedEntry>());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Failed to parse healed-offsets.json. The file is malformed.", ex);
        }
    }

    /// <summary>
    /// Save healed offsets to <c>configDir/healed-offsets.json</c> using an atomic write
    /// (.tmp + File.Move). Creates the config directory if it does not exist.
    /// </summary>
    public static void Save(string configDir, HealedOffsetsFileContent content)
    {
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        var path = Path.Combine(configDir, "healed-offsets.json");
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(content, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }
}