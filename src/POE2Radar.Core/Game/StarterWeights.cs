// src/POE2Radar.Core/Game/StarterWeights.cs
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Game;

/// <summary>The meta-derived default gear-scoring weights, loaded once from the embedded
/// <c>starter_stat_weights.json</c> (generated offline from a Tincture ladder snapshot via
/// <c>POE2Radar.Research --gen-weights</c>). Read-only; the user can override per stat in the dashboard.</summary>
public static class StarterWeights
{
    private sealed class Model
    {
        [JsonPropertyName("byStatId")] public Dictionary<string, double> ByStatId { get; set; } = new();
        [JsonPropertyName("normById")] public Dictionary<string, double> NormById { get; set; } = new();
        [JsonPropertyName("target")] public double Target { get; set; } = 100;
        [JsonPropertyName("godRollThreshold")] public double GodRollThreshold { get; set; } = 85;
    }

    private static readonly Model M = Load();

    public static IReadOnlyDictionary<string, double> ByStatId => M.ByStatId;
    public static IReadOnlyDictionary<string, double> NormById => M.NormById;
    public static double Target => M.Target;
    public static double GodRollThreshold => M.GodRollThreshold;

    private static Model Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("starter_stat_weights", StringComparison.Ordinal));
            if (name == null) return new Model();
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return new Model();
            return JsonSerializer.Deserialize<Model>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Model();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"StarterWeights load failed: {ex.Message}");
            return new Model();
        }
    }
}
