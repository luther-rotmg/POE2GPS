using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace POE2Radar.Core.NavDestinations;

/// <summary>
/// Static store for user-authored navigation destinations persisted as a single
/// <c>nav-destinations.json</c> file under the config directory. Uses atomic writes
/// (.tmp + File.Move) matching the existing <see cref="Rules.RulesFileStore"/> pattern.
/// </summary>
public static class NavDestinationStore
{
    /// <summary>Maximum number of destinations allowed in the file.</summary>
    private const int MaxDestinations = 50;

    /// <summary>JSON serializer options: camelCase, indented.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Load the nav-destinations file from <c>configDir/nav-destinations.json</c>.
    /// Returns an empty <c>NavDestinationFile(1, [])</c> if the file does not exist.
    /// </summary>
    public static NavDestinationFile Load(string configDir)
    {
        var path = Path.Combine(configDir, "nav-destinations.json");
        if (!File.Exists(path))
            return new NavDestinationFile(1, Array.Empty<NavDestination>());

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<NavDestinationFile>(json, JsonOptions);
            if (file == null)
                return new NavDestinationFile(1, Array.Empty<NavDestination>());

            return file;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Failed to parse nav-destinations.json. The file is malformed.");
        }
    }

    /// <summary>
    /// Save the nav-destinations file to <c>configDir/nav-destinations.json</c> using an atomic
    /// write (.tmp + File.Move). Validates every destination before writing. Assigns a fresh
    /// <see cref="Guid.NewGuid"/> to any destination with <see cref="Guid.Empty"/> Id.
    /// Throws <see cref="ArgumentException"/> if <c>file.Destinations.Count &gt; 50</c>.
    /// </summary>
    public static void Save(string configDir, NavDestinationFile file)
    {
        if (file.Destinations.Count > MaxDestinations)
            throw new ArgumentException($"Destination count {file.Destinations.Count} exceeds maximum of {MaxDestinations}.");

        // Assign IDs for empty-guids and validate every destination.
        var destinations = file.Destinations.Select(d =>
        {
            var dest = d.Id == Guid.Empty ? d with { Id = Guid.NewGuid() } : d;
            ValidateDestination(dest);
            return dest;
        }).ToList();

        // Check for duplicate (zoneCode, name) combinations at create (ignore updates).
        var seen = new HashSet<(string ZoneCode, string Name)>(destinations.Count);
        foreach (var d in destinations)
        {
            if (!seen.Add((d.ZoneCode, d.Name)))
                throw new ArgumentException($"Duplicate destination (zoneCode: \"{d.ZoneCode}\", name: \"{d.Name}\"). Each (zoneCode, name) combination must be unique.");
        }

        var toSave = new NavDestinationFile(1, destinations.AsReadOnly());

        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        var path = Path.Combine(configDir, "nav-destinations.json");
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(toSave, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>
    /// Upsert (insert or replace) a destination. Loads the current file, replaces any destination
    /// with the same Id, or appends the new destination. If the destination's Id is <see cref="Guid.Empty"/>,
    /// a new Guid is assigned before the upsert. Throws <see cref="ArgumentException"/> if a duplicate
    /// (zoneCode, name) combination exists at create (new Id, not replacing an existing destination)
    /// or if the resulting count would exceed 50.
    /// </summary>
    public static void Upsert(string configDir, NavDestination destination)
    {
        if (destination.Id == Guid.Empty)
            destination = destination with { Id = Guid.NewGuid() };

        ValidateDestination(destination);

        var file = Load(configDir);
        var destinations = file.Destinations.ToList();

        var existingIndex = destinations.FindIndex(d => d.Id == destination.Id);
        if (existingIndex >= 0)
        {
            // Replace existing — skip duplicate check
            destinations[existingIndex] = destination;
        }
        else
        {
            // New destination — check for (zoneCode, name) duplicate
            foreach (var d in destinations)
            {
                if (string.Equals(d.ZoneCode, destination.ZoneCode, StringComparison.Ordinal) &&
                    string.Equals(d.Name, destination.Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Duplicate destination (zoneCode: \"{destination.ZoneCode}\", name: \"{destination.Name}\"). " +
                        "Each (zoneCode, name) combination must be unique.");
                }
            }

            destinations.Add(destination);
        }

        Save(configDir, new NavDestinationFile(1, destinations.AsReadOnly()));
    }

    /// <summary>
    /// Delete a destination by Id. Loads the current file, removes the destination if present,
    /// and saves. Returns true if the destination was removed, false if not found. Never throws.
    /// </summary>
    public static bool Delete(string configDir, Guid id)
    {
        try
        {
            var file = Load(configDir);
            var destinations = file.Destinations.ToList();
            var removed = destinations.RemoveAll(d => d.Id == id);
            if (removed == 0)
                return false;

            Save(configDir, new NavDestinationFile(1, destinations.AsReadOnly()));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validate a single destination. Throws <see cref="ArgumentException"/> on any failure.
    /// </summary>
    /// <param name="destination">The destination to validate.</param>
    /// <exception cref="ArgumentException">If the destination fails any validation check.</exception>
    public static void ValidateDestination(NavDestination destination)
    {
        if (string.IsNullOrWhiteSpace(destination.ZoneCode))
            throw new ArgumentException("ZoneCode must not be null or empty.");

        if (destination.ZoneCode.Length > 64)
            throw new ArgumentException($"ZoneCode must be at most 64 characters, got {destination.ZoneCode.Length}.");

        if (string.IsNullOrWhiteSpace(destination.Name))
            throw new ArgumentException("Name must not be null or empty.");

        if (destination.Name.Length > 40)
            throw new ArgumentException($"Name must be at most 40 characters, got {destination.Name.Length}.");
    }

    /// <summary>
    /// Load destinations for a specific zone, filtered by exact zoneCode equality (case-sensitive).
    /// </summary>
    /// <param name="configDir">The config directory.</param>
    /// <param name="zoneCode">The exact zone code to match (case-sensitive).</param>
    /// <returns>A read-only list of matching destinations.</returns>
    public static IReadOnlyList<NavDestination> LoadForZone(string configDir, string zoneCode)
    {
        var file = Load(configDir);
        return file.Destinations
            .Where(d => string.Equals(d.ZoneCode, zoneCode, StringComparison.Ordinal))
            .ToList()
            .AsReadOnly();
    }
}