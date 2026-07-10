using System;
using System.IO;
using System.Linq;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

/// <summary>
/// Threshold — the WaygateDevice built-in Tile display rule must produce EXACTLY ONE marker
/// against the full seeded rule set (BuiltInTileRules unioned with BuildDefault). Rendering
/// downstream draws one marker per <see cref="DisplayRules.ResolveTile"/> hit; the risk is a
/// future R5 Waygate atlas-landmark family port that stamps a second marker through a different
/// code path. This test locks the invariant at the rule-set level: (a) the shipped resolve path
/// returns the Waygate rule for a WaygateDevice tile path; (b) enumerating every seeded rule
/// finds NO other rule whose Match term also fires on that path — the guard against silent
/// double-marker regressions.
/// </summary>
public class DisplayRulesWaygateTests
{
    // Realistic WaygateDevice tile path — the substring "WaygateDevice" is the stable seed-rule
    // matcher. Any tile path containing that substring should resolve to the one Waygate rule.
    private const string WaygateTilePath = "Metadata/Terrain/Leagues/EndGame/WaygateDevice_01.arm";

    [Fact]
    public void DisplayRules_WaygateDeviceMatches_ExactlyOneMarker()
    {
        var dir = Path.Combine(Path.GetTempPath(),
                               "poe2gps-waygate-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new RadarSettings();
            var rulesPath = Path.Combine(dir, "display_rules.json");
            var rules = new DisplayRules(rulesPath);

            // Simulate the RadarApp wire: BuildDefault + BuiltInTileRules unioned by Name.
            var seeded = DisplayRules.BuildDefault(
                st: settings.Styles,
                showMonsters: settings.ShowMonsters,
                watched: Enumerable.Empty<WatchedEntry>()).ToList();
            var byName = new System.Collections.Generic.HashSet<string>(
                seeded.Select(r => r.Name), StringComparer.Ordinal);
            foreach (var seed in DisplayRules.BuiltInTileRules())
                if (byName.Add(seed.Name)) seeded.Add(seed);
            rules.Replace(seeded);
            DisplayRules.SeedBuiltInTileRulesIfNeeded(settings);

            // (a) Exercise the shipped rule-resolution path: ResolveTile with requireMatch:true
            // is the SURFACING pass — only a Tile rule with explicit match terms qualifies.
            var first = rules.ResolveTile(WaygateTilePath, requireMatch: true);
            Assert.NotNull(first);
            Assert.Equal("Waygate", first!.Name);
            Assert.Contains("WaygateDevice", first.Match);

            // (b) Enumerate ALL seeded rules and count how many would fire against the tile path
            // via their own Match list. Any Tile rule with a Match term that is a substring of the
            // WaygateDevice tile path would produce a second marker. Exactly ONE is allowed today.
            var whichMatchByInspection = rules.All
                .Where(r => r.Enabled)
                .Where(r => r.Categories.Any(c => string.Equals(c, "Tile", StringComparison.OrdinalIgnoreCase)))
                .Where(r => r.Match.Any(m => !string.IsNullOrEmpty(m)
                    && WaygateTilePath.Contains(m, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.Single(whichMatchByInspection);
            Assert.Equal("Waygate", whichMatchByInspection[0].Name);

            // (c) Double-marker guard via strip-and-recheck: remove the matched rule, prove no
            // OTHER rule in the seeded set also fires. If a future port ships a second Waygate
            // rule alongside this one, ResolveTile will still return non-null here and the test
            // fails loudly — the port must fix at source, not weaken this assertion.
            var remaining = rules.All.Where(r => !ReferenceEquals(r, first)).ToList();
            rules.Replace(remaining);
            var second = rules.ResolveTile(WaygateTilePath, requireMatch: true);
            Assert.Null(second);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
