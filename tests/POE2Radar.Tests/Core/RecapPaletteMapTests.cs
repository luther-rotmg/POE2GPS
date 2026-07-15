using System.Linq;
using System.Text.RegularExpressions;
using POE2Radar.Core.Themes;
using Xunit;

namespace POE2Radar.Tests.Core;

public class RecapPaletteMapTests
{
    private static readonly Regex HexPattern = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    [Fact]
    public void All_contains_exactly_eight_palettes()
    {
        Assert.Equal(8, RecapPaletteMap.All.Count);
    }

    [Fact]
    public void Every_palette_has_four_valid_hex_colors()
    {
        foreach (var (name, p) in RecapPaletteMap.All)
        {
            Assert.True(HexPattern.IsMatch(p.Accent), $"{name}.Accent invalid: {p.Accent}");
            Assert.True(HexPattern.IsMatch(p.Panel),  $"{name}.Panel invalid: {p.Panel}");
            Assert.True(HexPattern.IsMatch(p.Text),   $"{name}.Text invalid: {p.Text}");
            Assert.True(HexPattern.IsMatch(p.Border), $"{name}.Border invalid: {p.Border}");
        }
    }

    [Fact]
    public void Every_palette_has_a_distinct_accent_from_every_other_palette()
    {
        // "Distinct-enough recap" — the accent bar is the most visible themed pixel;
        // if two palettes share an accent, the recap PNGs collide visually.
        var accents = RecapPaletteMap.All.Select(kvp => kvp.Value.Accent).ToList();
        Assert.Equal(accents.Count, accents.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Resolve_returns_matching_palette_case_insensitive()
    {
        var firstName = RecapPaletteMap.All.Keys.First();
        var expected = RecapPaletteMap.All[firstName];
        Assert.Equal(expected, RecapPaletteMap.Resolve(firstName));
        Assert.Equal(expected, RecapPaletteMap.Resolve(firstName.ToUpperInvariant()));
    }

    [Fact]
    public void Resolve_returns_null_for_null_blank_or_unknown()
    {
        // Null preserves existing recap look for kalguuran/terminal/Default users.
        Assert.Null(RecapPaletteMap.Resolve(null));
        Assert.Null(RecapPaletteMap.Resolve(""));
        Assert.Null(RecapPaletteMap.Resolve("   "));
        Assert.Null(RecapPaletteMap.Resolve("kalguuran"));
        Assert.Null(RecapPaletteMap.Resolve("terminal"));
        Assert.Null(RecapPaletteMap.Resolve("not-a-real-palette-name"));
    }
}