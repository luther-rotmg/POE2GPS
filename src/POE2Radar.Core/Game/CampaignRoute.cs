using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

/// <summary>One critical-path campaign step: a zone, the next zone to head to, and the friendly name
/// of the exit that leads there (matched against the curated CustomLandmarks exit labels). Pure data.</summary>
public readonly record struct CampaignStep(string Zone, int Act, string Name, string? Next, string? ExitHint);

/// <summary>
/// Static campaign critical-path route, loaded once from the embedded <c>campaign_route.json</c>
/// (mirrors <see cref="ZoneGuide"/>). Maps the player's current zone code to where they should go next.
/// Read-only; no memory access. The quest-completion read (if added later) refines the inferred step
/// but this table is the always-available baseline.
/// </summary>
public sealed class CampaignRoute
{
    private readonly List<CampaignStep> _steps = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);   // zone code → ordinal
    private readonly Dictionary<string, string> _nameToCode = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CampaignStep> Steps => _steps;

    /// <summary>The shared route, loaded once from the embedded table.</summary>
    public static CampaignRoute Shared { get; } = LoadEmbedded();

    public CampaignStep? StepFor(string zoneCode) =>
        _index.TryGetValue(zoneCode, out var i) ? _steps[i] : null;

    public CampaignStep? NextStep(CampaignStep step) =>
        step.Next is { } n ? StepFor(n) : null;

    public int IndexOf(string zoneCode) => _index.TryGetValue(zoneCode, out var i) ? i : -1;

    /// <summary>Reverse map: a route zone's friendly name → its code (for matching curated exit labels
    /// back to a destination code). Null on miss. Case-insensitive.</summary>
    public string? CodeForName(string name) => _nameToCode.TryGetValue(name, out var c) ? c : null;

    /// <summary>Parse a route from a JSON array of steps. Used by <see cref="Shared"/> and by tests.</summary>
    public static CampaignRoute FromJson(string json)
    {
        var route = new CampaignRoute();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return route;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var zone = Str(e, "zone");
            if (zone.Length == 0) continue;
            var step = new CampaignStep(
                Zone: zone,
                Act: e.TryGetProperty("act", out var a) && a.TryGetInt32(out var ai) ? ai : 0,
                Name: Str(e, "name"),
                Next: NullableStr(e, "next"),
                ExitHint: NullableStr(e, "exitHint"));
            route._index[zone] = route._steps.Count;
            route._steps.Add(step);
            if (step.Name.Length > 0) route._nameToCode.TryAdd(step.Name, zone);   // first wins on dup names
        }
        return route;
    }

    private static CampaignRoute LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("campaign_route"));
            if (name == null) return new CampaignRoute();
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return new CampaignRoute();
            using var r = new StreamReader(s);
            return FromJson(r.ReadToEnd());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CampaignRoute load failed: {ex.Message}");
            return new CampaignRoute();
        }
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? NullableStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
