using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

/// <summary>
/// Reach — CHOR-42 (v0.26): reactive boss encounter cheat sheet. Loaded once from the embedded
/// <c>boss_encounters.json</c>; never throws (missing/malformed resource → empty catalog). Lookup
/// surfaces mirror the shipping pattern: by boss key, by zone code (MapUberBoss_*), or by an
/// entity-metadata substring (piggybacks the preload-catalog match).
/// </summary>
public sealed class BossEncounterCatalog
{
    /// <summary>One boss's full cheat-sheet entry — hand-authored, catalog-of-record for the sheet UI.</summary>
    public sealed record EncounterEntry(
        string Key,
        string Label,
        string MatchMetadata,
        IReadOnlyList<string> ZoneCodes,
        string Tier,
        string Category,
        DamageMix DamageTypes,
        IReadOnlyList<string> OneShots,
        IReadOnlyDictionary<string, int> Overcap,
        string FlaskNotes,
        IReadOnlyList<PhaseEntry> Phases);

    /// <summary>Approximate damage-type share (0..1 per element). Sum ≈ 1 by convention but the loader
    /// does not enforce it — some encounters split more than five ways or omit an element entirely.</summary>
    public readonly record struct DamageMix(float Phys, float Fire, float Cold, float Lightning, float Chaos);

    public readonly record struct PhaseEntry(string Cue, string Note);

    private static readonly Lazy<BossEncounterCatalog> _shared =
        new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);
    public static BossEncounterCatalog Shared => _shared.Value;

    private readonly EncounterEntry[] _entries;
    private readonly Dictionary<string, EncounterEntry> _byKey;
    private readonly Dictionary<string, EncounterEntry> _byZone;

    private BossEncounterCatalog(EncounterEntry[] entries)
    {
        _entries = entries;
        _byKey = new Dictionary<string, EncounterEntry>(StringComparer.OrdinalIgnoreCase);
        _byZone = new Dictionary<string, EncounterEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            _byKey[e.Key] = e;
            foreach (var z in e.ZoneCodes) _byZone[z] = e;
        }
    }

    /// <summary>Every entry, in JSON authoring order — the browsable list surface.</summary>
    public IReadOnlyList<EncounterEntry> Entries => _entries;

    public int Count => _entries.Length;

    public EncounterEntry? ByBossKey(string key)
        => _byKey.TryGetValue(key ?? "", out var e) ? e : null;

    public EncounterEntry? ByZoneCode(string code)
        => _byZone.TryGetValue(code ?? "", out var e) ? e : null;

    /// <summary>Substring match — looks for the entry whose <see cref="EncounterEntry.MatchMetadata"/>
    /// appears (case-insensitive) inside the given entity metadata path. First match wins.</summary>
    public EncounterEntry? ByMetadata(string metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return null;
        foreach (var e in _entries)
            if (metadata.Contains(e.MatchMetadata, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }

    // ── loading ─────────────────────────────────────────────────────────────────────────────────────

    private static BossEncounterCatalog LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = OpenResource(asm, "boss_encounters");
            if (stream == null) return Empty();

            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Empty();

            var entries = new List<EncounterEntry>();
            foreach (var el in arr.EnumerateArray())
            {
                var key = Str(el, "key");
                if (key.Length == 0) continue;

                var zoneCodes = StrList(el, "zoneCodes");
                var oneShots  = StrList(el, "oneShots");
                var overcap   = IntDict(el, "overcap");
                var phases    = PhaseList(el, "phases");
                var dmg       = ParseDamage(el);

                entries.Add(new EncounterEntry(
                    Key:           key,
                    Label:         Str(el, "label"),
                    MatchMetadata: Str(el, "matchMetadata"),
                    ZoneCodes:     zoneCodes,
                    Tier:          Str(el, "tier"),
                    Category:      Str(el, "category"),
                    DamageTypes:   dmg,
                    OneShots:      oneShots,
                    Overcap:       overcap,
                    FlaskNotes:    Str(el, "flaskNotes"),
                    Phases:        phases));
            }
            return new BossEncounterCatalog(entries.ToArray());
        }
        catch
        {
            return Empty();
        }
    }

    private static BossEncounterCatalog Empty() => new(Array.Empty<EncounterEntry>());

    private static Stream? OpenResource(Assembly asm, string contains)
    {
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains(contains));
        return name == null ? null : asm.GetManifestResourceStream(name);
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static IReadOnlyList<string> StrList(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }

    private static IReadOnlyDictionary<string, int> IntDict(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, int>();
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n)) d[p.Name] = n;
        return d;
    }

    private static IReadOnlyList<PhaseEntry> PhaseList(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return Array.Empty<PhaseEntry>();
        var list = new List<PhaseEntry>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            list.Add(new PhaseEntry(Str(el, "cue"), Str(el, "note")));
        }
        return list;
    }

    private static DamageMix ParseDamage(JsonElement e)
    {
        if (!e.TryGetProperty("damageTypes", out var obj) || obj.ValueKind != JsonValueKind.Object) return default;
        float phys = F(obj, "phys"), fire = F(obj, "fire"), cold = F(obj, "cold"),
              lightning = F(obj, "lightning"), chaos = F(obj, "chaos");
        return new DamageMix(phys, fire, cold, lightning, chaos);

        static float F(JsonElement o, string k)
            => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;
    }
}
