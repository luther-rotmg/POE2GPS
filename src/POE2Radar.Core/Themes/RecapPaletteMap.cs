using System.Collections.Generic;

namespace POE2Radar.Core.Themes;

public static class RecapPaletteMap
{
    // MIRRORS src/POE2Radar.Overlay/Web/Assets/dashboard.css [data-palette="..."] blocks
    // from P1 verbatim: Accent = --gold, Panel = --panel, Text = --ink, Border = --line.
    // Contains ONLY the 8 v0.35 signature palettes. kalguuran/terminal and empty-string
    // Default are intentionally absent — Resolve returns null for those so R2's recap
    // renderer keeps the existing hardcoded #1a1e28/#e6d99c/#f0e8d0/#6a7080 consts and
    // no existing supporter's recap look regresses (additive constraint from spec).
    // If a hex changes in dashboard.css, change it here in the same commit.
    private static readonly Dictionary<string, PaletteColorSet> _byName = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["ultimatum-red"]       = new PaletteColorSet(Accent: "#d94a4a", Panel: "#240a0a", Text: "#f2d6d6", Border: "#4a1919"),
        ["sanctum-cream"]       = new PaletteColorSet(Accent: "#d4b26a", Panel: "#2a2418", Text: "#f5ecd6", Border: "#4a3f24"),
        ["necropolis-amethyst"] = new PaletteColorSet(Accent: "#b56ad9", Panel: "#1a0a24", Text: "#ecd6f5", Border: "#3a1e4a"),
        ["delirium-static"]     = new PaletteColorSet(Accent: "#7fd8e6", Panel: "#0e1a24", Text: "#dceff5", Border: "#1e3a4a"),
        ["legion-bronze"]       = new PaletteColorSet(Accent: "#c78e4a", Panel: "#241a10", Text: "#f0ddc0", Border: "#4a3620"),
        ["ritual-blood"]        = new PaletteColorSet(Accent: "#b83060", Panel: "#180814", Text: "#f0d0d8", Border: "#3a1428"),
        ["trial-ordeal"]        = new PaletteColorSet(Accent: "#f5d84a", Panel: "#14120a", Text: "#f5edc4", Border: "#38301a"),
        ["blight-bloom"]        = new PaletteColorSet(Accent: "#a8c748", Panel: "#14180a", Text: "#e0e8b8", Border: "#384418"),
    };

    public static IReadOnlyDictionary<string, PaletteColorSet> All => _byName;

    /// <summary>Returns the hex-color record for the given palette slug, or null when the
    /// slug is missing/blank/unknown. Caller uses null to mean "use existing hardcoded
    /// recap consts" so kalguuran/terminal/Default users retain byte-identical output.</summary>
    public static PaletteColorSet? Resolve(string? paletteName)
    {
        if (string.IsNullOrWhiteSpace(paletteName)) return null;
        return _byName.TryGetValue(paletteName, out var found) ? found : null;
    }
}