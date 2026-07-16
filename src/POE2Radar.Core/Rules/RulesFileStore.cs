using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace POE2Radar.Core.Rules;

/// <summary>
/// Static store for the rules engine config file. Reads and writes <c>rules.json</c>
/// from the given config directory. Uses atomic writes (tmp + File.Move) matching the
/// existing <see cref="Palettes.UserPaletteStore"/> pattern.
/// </summary>
public static class RulesFileStore
{
    /// <summary>Regex for valid hex colors: ^#[0-9a-fA-F]{6}$</summary>
    private static readonly Regex HexRegex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly HashSet<string> ValidRarities = new(StringComparer.OrdinalIgnoreCase)
    {
        "unique", "rare", "magic", "normal",
    };

    /// <summary>
    /// Load the rules file from <c>configDir/rules.json</c>. Returns an empty
    /// <c>RulesFile(1, [])</c> if the file does not exist. Throws <see cref="InvalidDataException"/>
    /// on malformed JSON, or <see cref="ArgumentException"/> if any rule fails validation.
    /// </summary>
    public static RulesFile Load(string configDir)
    {
        var path = Path.Combine(configDir, "rules.json");
        if (!File.Exists(path))
            return new RulesFile(1, Array.Empty<RuleRecord>());

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<RulesFile>(json, JsonOptions);
            if (file == null)
                return new RulesFile(1, Array.Empty<RuleRecord>());

            foreach (var rule in file.Rules)
                ValidateRule(rule);

            return file;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Failed to parse rules.json. The file is malformed.", ex);
        }
    }

    /// <summary>
    /// Save the rules file to <c>configDir/rules.json</c> using an atomic write
    /// (.tmp + File.Move). Validates every rule before writing. Assigns a fresh
    /// <see cref="Guid.NewGuid"/> to any rule with <see cref="Guid.Empty"/> Id.
    /// Throws <see cref="ArgumentException"/> if <c>file.Rules.Count &gt; 100</c>.
    /// </summary>
    public static void Save(string configDir, RulesFile file)
    {
        if (file.Rules.Count > 100)
            throw new ArgumentException($"Rule count {file.Rules.Count} exceeds maximum of 100.");

        // Assign IDs for empty-guids and validate every rule.
        var rules = file.Rules.Select(r =>
        {
            var rule = r.Id == Guid.Empty ? r with { Id = Guid.NewGuid() } : r;
            ValidateRule(rule);
            return rule;
        }).ToList();

        var toSave = file with { Rules = rules.AsReadOnly() };

        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        var path = Path.Combine(configDir, "rules.json");
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(toSave, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>
    /// Upsert (insert or replace) a rule. Loads the current file, replaces any rule
    /// with the same Id, or appends the new rule. If the rule's Id is <see cref="Guid.Empty"/>,
    /// a new Guid is assigned before the upsert and the returned <see cref="RuleRecord"/>
    /// reflects the assigned Id. Throws <see cref="ArgumentException"/> if the resulting
    /// rule count would exceed 100.
    /// </summary>
    public static RuleRecord Upsert(string configDir, RuleRecord rule)
    {
        if (rule.Id == Guid.Empty)
            rule = rule with { Id = Guid.NewGuid() };

        ValidateRule(rule);

        var file = Load(configDir);
        var rules = file.Rules.ToList();

        var existingIndex = rules.FindIndex(r => r.Id == rule.Id);
        if (existingIndex >= 0)
            rules[existingIndex] = rule;
        else
            rules.Add(rule);

        Save(configDir, file with { Rules = rules.AsReadOnly() });
        return rule;
    }

    /// <summary>
    /// Delete a rule by Id. Loads the current file, removes the rule if present,
    /// and saves. Returns true if the rule was removed, false if not found.
    /// Never throws.
    /// </summary>
    public static bool Delete(string configDir, Guid id)
    {
        try
        {
            var file = Load(configDir);
            var rules = file.Rules.ToList();
            var removed = rules.RemoveAll(r => r.Id == id);
            if (removed == 0)
                return false;

            Save(configDir, file with { Rules = rules.AsReadOnly() });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validate a single rule. Throws <see cref="ArgumentException"/> on any failure.
    /// </summary>
    /// <param name="rule">The rule to validate.</param>
    /// <exception cref="ArgumentException">If the rule fails any validation check.</exception>
    public static void ValidateRule(RuleRecord rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
            throw new ArgumentException("Rule name must not be empty.");

        if (rule.Name.Length > 80)
            throw new ArgumentException($"Rule name must be at most 80 characters, got {rule.Name.Length}.");

        if (rule.Then == null || rule.Then.Count == 0)
            throw new ArgumentException("Rule 'then' list must contain at least one effect.");

        foreach (var effect in rule.Then)
        {
            switch (effect)
            {
                case TintEffect t when !HexRegex.IsMatch(t.Color):
                    throw new ArgumentException($"Invalid tint color: \"{t.Color}\". Must be #RRGGBB.");
                case RingEffect r when !HexRegex.IsMatch(r.Color):
                    throw new ArgumentException($"Invalid ring color: \"{r.Color}\". Must be #RRGGBB.");
                case LabelEffect l when string.IsNullOrEmpty(l.Text) || l.Text.Length > 200:
                    throw new ArgumentException($"Label text must be 1-200 characters, got {l.Text?.Length ?? 0}.");
                case SoundEffect s:
                    if (string.IsNullOrEmpty(s.File) || s.File.Length > 64 ||
                        s.File.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.'))
                        throw new ArgumentException($"Invalid sound filename: \"{s.File}\". Must be 1-64 characters, alphanumeric with _ - . only.");
                    break;
                case PulseEffect p when p.Speed != "slow" && p.Speed != "fast":
                    throw new ArgumentException($"Invalid pulse speed: \"{p.Speed}\". Must be \"slow\" or \"fast\".");
            }
        }

        // Validate selector fields
        if (rule.When?.Rarity != null)
        {
            if (!ValidRarities.Contains(rule.When.Rarity))
                throw new ArgumentException($"Unknown rarity: \"{rule.When.Rarity}\". Must be one of: unique, rare, magic, normal.");
        }
    }
}