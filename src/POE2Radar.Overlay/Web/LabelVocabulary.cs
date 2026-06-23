// src/POE2Radar.Overlay/Web/LabelVocabulary.cs
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Overlay.Web;

/// <summary>The curated classification label vocabulary (group name → label list), loaded once from the
/// embedded <c>labels.json</c>. Served read-only via /api/labels for the dashboard's classify pickers.
/// Read-only w.r.t. the game.</summary>
public sealed class LabelVocabulary
{
    private readonly Dictionary<string, List<string>> _groups;
    private LabelVocabulary(Dictionary<string, List<string>> groups) => _groups = groups;

    public static LabelVocabulary Shared { get; } = Load();
    public IReadOnlyDictionary<string, List<string>> Groups => _groups;

    private static LabelVocabulary Load()
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("labels.json", StringComparison.Ordinal));
            if (resName != null)
                using (var s = asm.GetManifestResourceStream(resName))
                {
                    var parsed = s != null ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(s) : null;
                    if (parsed != null) groups = parsed;
                }
        }
        catch (Exception ex) { Console.Error.WriteLine($"LabelVocabulary load failed: {ex.Message}"); }
        return new LabelVocabulary(groups);
    }
}
