using System.Collections.Generic;
using System.Text.Json;
using POE2Radar.Core.Campaign.Probe;

namespace POE2Radar.Overlay.Config;

/// <summary>
/// v0.20.1 T12: consolidates the 11 legacy one-shot bool fields that used to live on
/// <see cref="RadarSettings"/> (AtlasRulesInitialized, AtlasTargetsSeeded, AtlasGroupsSeeded,
/// AbyssRuleSeeded, IconDefaultsApplied, IconDefaultsApplied2, RuleCleanupV1, MechanicLabelsV1,
/// GroundDefaultsV2, IconSizesV1, EntityArrowsSeeded) into a single
/// <see cref="RadarSettings.AppliedMigrations"/> string list — stopping the settings-model bloat
/// that would have grown linearly with every future one-shot seed.
/// </summary>
/// <remarks>
/// Called ONCE from <see cref="RadarSettings.Load"/>. Reads legacy bool keys from the JSON doc,
/// translates any <c>true</c> value into its migration-key string, and appends it to
/// <c>AppliedMigrations</c>. Missing legacy keys are skipped (no migration key added). Once the
/// migration has run, subsequent serializations omit the legacy fields entirely (they were removed
/// from <see cref="RadarSettings"/>), so the migration is inherently one-shot: on the second load
/// the legacy keys are gone and nothing changes.
/// <para/>
/// Backward-compat is load-bearing: a v0.20.0 <c>config/radar_settings.json</c> with any of the 11
/// legacy bools present (some <c>true</c>) MUST deserialize cleanly and carry each <c>true</c>
/// forward as the matching migration-key entry, so the guarded seed action doesn't re-fire on
/// the first v0.20.1 launch. See <c>tests/POE2Radar.Tests/fixtures/settings-v0.20.0-{seeded,unseeded}.json</c>
/// for the pinned fixtures.
/// </remarks>
public static class SettingsMigrator
{
    // Enumerate every legacy one-shot bool and its migration key. Add a row whenever a new one-shot
    // bool ships and gets consolidated — same shape (guard→string), zero new fields.
    static readonly (string LegacyKey, string MigrationKey)[] Map = new[]
    {
        ("AtlasRulesInitialized",  "seed:atlas-rules"),
        ("AtlasTargetsSeeded",     "seed:atlas-targets"),
        ("AtlasGroupsSeeded",      "seed:atlas-groups"),
        ("AbyssRuleSeeded",        "seed:abyss-rule"),
        ("IconDefaultsApplied",    "seed:icon-defaults-v1"),
        ("IconDefaultsApplied2",   "seed:icon-defaults-v2"),
        ("RuleCleanupV1",          "seed:rule-cleanup-v1"),
        ("MechanicLabelsV1",       "seed:mechanic-labels-v1"),
        ("GroundDefaultsV2",       "seed:ground-defaults-v2"),
        ("IconSizesV1",            "seed:icon-sizes-v1"),
        ("EntityArrowsSeeded",     "seed:entity-arrows"),
    };

    // The production settings serializer writes camelCase; a user-hand-edited file might use the
    // C#-side PascalCase. Deserialize case-insensitively so both round-trip cleanly (matches the
    // v0.19.1 CheckForUpdates→AutoUpdate migration's tolerance).
    static readonly JsonSerializerOptions LenientOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Deserialize <paramref name="doc"/> as <see cref="RadarSettings"/> and fold any legacy
    /// one-shot bool set to <c>true</c> into the returned settings' <see cref="RadarSettings.AppliedMigrations"/>
    /// list. Any keys already present in <c>AppliedMigrations</c> are preserved; migration-key entries
    /// are de-duplicated (idempotent second-load).
    /// </summary>
    public static RadarSettings Migrate(JsonDocument doc)
    {
        var settings = JsonSerializer.Deserialize<RadarSettings>(doc.RootElement.GetRawText(), LenientOptions)
            ?? new RadarSettings();

        settings.AppliedMigrations ??= new List<string>();
        var applied = new HashSet<string>(settings.AppliedMigrations, System.StringComparer.Ordinal);
        foreach (var (legacyKey, migrationKey) in Map)
        {
            if (TryGetPropertyIgnoreCase(doc.RootElement, legacyKey, out var el)
                && el.ValueKind == JsonValueKind.True)
            {
                applied.Add(migrationKey);
            }
        }

        // v0.22 campaign-probe: on first load with an empty/missing ProbeInstallId, mint a fresh
        // v4 UUID and stamp "probe_install_id_v1" into AppliedMigrations. Idempotent — a second
        // load sees a populated ProbeInstallId and no-ops (the key already lives in the set).
        if (string.IsNullOrEmpty(settings.ProbeInstallId))
        {
            settings.ProbeInstallId = AnonymizationHelpers.NewInstallUuid();
            applied.Add("probe_install_id_v1");
        }

        settings.AppliedMigrations = new List<string>(applied);
        return settings;
    }

    // JsonElement.TryGetProperty is case-sensitive; the on-disk file uses camelCase (from the
    // serializer's naming policy) but hand-edits or older tooling may leave PascalCase. Try the
    // canonical camelCase form first, then the source PascalCase — one of the two always hits.
    static bool TryGetPropertyIgnoreCase(JsonElement root, string pascalName, out JsonElement value)
    {
        if (root.ValueKind != JsonValueKind.Object) { value = default; return false; }
        var camel = char.ToLowerInvariant(pascalName[0]) + pascalName.Substring(1);
        if (root.TryGetProperty(camel, out value)) return true;
        if (root.TryGetProperty(pascalName, out value)) return true;
        value = default;
        return false;
    }
}
