using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace POE2Radar.Core.SessionWidget;

/// <summary>
/// Static store for the session stat widget configuration, persisted as a single
/// <c>session-widget.json</c> file under the config directory. Uses atomic writes
/// (.tmp + File.Move) matching the existing <see cref="Rules.RulesFileStore"/> pattern.
/// </summary>
public static class SessionWidgetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// The set of six allowed chip identifiers for the session stat widget.
    /// </summary>
    public static IReadOnlySet<string> AllowedChips { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "drops",
        "xp-gained",
        "bosses-killed",
        "deaths",
        "time-in-zone",
        "avg-map-clear-time",
    };

    /// <summary>
    /// Load the session-widget config from <c>configDir/session-widget.json</c>.
    /// Returns a default <c>SessionWidgetConfig(1, new WidgetPosition(20, 20), Array.Empty&lt;string&gt;())</c>
    /// if the file does not exist. Throws <see cref="InvalidDataException"/> on malformed JSON,
    /// or <see cref="ArgumentException"/> if validation fails.
    /// </summary>
    public static SessionWidgetConfig Load(string configDir)
    {
        var path = Path.Combine(configDir, "session-widget.json");
        if (!File.Exists(path))
            return new SessionWidgetConfig(1, new WidgetPosition(20, 20), Array.Empty<string>());

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<SessionWidgetConfig>(json, JsonOptions);
            if (config == null)
                return new SessionWidgetConfig(1, new WidgetPosition(20, 20), Array.Empty<string>());

            ValidateConfig(config);
            return config;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Failed to parse session-widget.json. The file is malformed.", ex);
        }
    }

    /// <summary>
    /// Save the session-widget config to <c>configDir/session-widget.json</c> using an atomic
    /// write (.tmp + File.Move). Validates the config before writing. Throws <see cref="ArgumentException"/>
    /// if validation fails.
    /// </summary>
    public static void Save(string configDir, SessionWidgetConfig config)
    {
        ValidateConfig(config);

        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        var path = Path.Combine(configDir, "session-widget.json");
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>
    /// Validate a <see cref="SessionWidgetConfig"/>. Throws <see cref="ArgumentException"/>
    /// on any failure.
    /// </summary>
    /// <param name="config">The config to validate.</param>
    /// <exception cref="ArgumentException">If the config fails any validation check.</exception>
    public static void ValidateConfig(SessionWidgetConfig config)
    {
        if (config == null)
            throw new ArgumentException("Config must not be null.");

        if (config.Position == null)
            throw new ArgumentException("Position must not be null.");

        if (config.Chips == null)
            throw new ArgumentException("Chips must not be null.");

        foreach (var chip in config.Chips)
        {
            if (!AllowedChips.Contains(chip))
                throw new ArgumentException($"Unknown chip identifier: \"{chip}\". Must be one of: {string.Join(", ", AllowedChips)}.");
        }

        if (config.Chips.Count != config.Chips.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("Duplicate chip identifiers are not allowed.");
    }
}