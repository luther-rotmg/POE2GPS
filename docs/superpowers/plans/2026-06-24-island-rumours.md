# Island Rumours Reader Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the player is at the Expedition "Uncharted Waters" rumour-selection screen, read the 2–3 on-screen rumour labels from game memory, look each up in an embedded tier table, and display a ranked info panel on the overlay and a mirrored live card in the dashboard.

**Architecture:** A new `IslandRumours` Core class holds the embedded 25-entry tier table and pure lookup logic (MatchLabel / RankOffered); `Poe2Live.ReadOfferedRumours` does a bounded BFS of the UI tree using the validated text-struct recipe with magic-guard fast-reject; `RadarApp.UpdateIslandRumours` runs on the world thread with an adaptive throttle, publishes a volatile `IslandRumoursRender` bundle, and wires it into `RadarState`; `OverlayRenderer.DrawIslandRumours` draws a screen-space panel mirroring `DrawMonolithPanel`.

**Tech Stack:** .NET 10 (net10.0-windows, x64), C#, xUnit, Vortice.Direct2D1, vanilla-JS dashboard embedded as a C# string.

## Global Constraints

- Platform: .NET 10, net10.0-windows, x64 only.
- Strictly READ-ONLY: introduce NO SendInput/PostMessage/keybd_event/mouse_event/SendMessage; NO WriteProcessMemory/VirtualProtectEx/CreateRemoteThread/injection; OpenProcess never write. The compliance gate (scripts/compliance-gate.ps1) MUST stay green.
- The embedded tier table is desirability TIERS + qualitative notes, NEVER market prices (the gate forbids pricing).
- Tier order is EXACTLY: S+ > S > A > B+ > B > C > F (TierRank: S+=6, S=5, A=4, B+=3, B=2, C=1, F=0, unknown=-1).
- Read recipe (validated from dumps; re-verify per patch): UiElement body +0x138 -> textStruct; magic-guard {0x91,0x9C,0x9F,0xFF,0x01,0x01,0x00,0x00} at textStruct+0x10 (fast-reject); textBuf = textStruct+0x18; display string = null-terminated UTF-16 at textBuf+0x08 UNLESS it begins with "Fontin" (font override) -> the next UTF-16 run after the font + zero padding (~textStruct+0x50).
- ReadOfferedRumours enqueues ALL children unconditionally regardless of visibility. Pool elements for the rumour catalog sit inside invisible wrapper elements; pruning on the visibility flag (+0x180 bit 0x0B) would discard the target data and return empty results at the selection screen. Text-match against the rumour table is the SOLE discriminator between target labels and the rest of the UI tree. Returns EMPTY when not at the rumour screen (zero text-matches from the full walk).
- Adaptive throttle: when the last poll found NO rumours, walk every ~2-3s; when rumours detected, ~1-2 Hz. Bounded walk (cap ~30000 elements, depth ~40). Runs on its OWN world-thread reader stack; never the render/frame path.
- RadarSettings.ShowIslandRumours default TRUE; toggle in dashboard Settings + flat /api/settings round-trip.
- Pure logic (MatchLabel, RankOffered, tier comparator) lives in POE2Radar.Core (test project references Core only).
- The display panel mirrors DrawMonolithPanel (FillRectangle via Vortice.RawRectF + per-row DrawText), drawn only when ShowIslandRumours && offered-rumours-present && in-game/focused.
- Commands: build = `dotnet build POE2Radar.slnx -c Release`; tests = `dotnet test POE2Radar.slnx`; gate = `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1`; scrub = `powershell -ExecutionPolicy Bypass -File scripts/scrub-strings.ps1 -SelfTest`.

---

### Task 1: Tier table + pure logic (Core, TDD)

**Files:**
- Create: `src/POE2Radar.Core/Game/island_rumours.json`
- Create: `src/POE2Radar.Core/Game/IslandRumours.cs`
- Modify: `src/POE2Radar.Core/POE2Radar.Core.csproj` (line 24 — add EmbeddedResource after `dynasty_maps.json`)
- Create: `tests/POE2Radar.Tests/IslandRumoursTests.cs`

**Interfaces:**
- Produces: `IslandRumours.Shared` (singleton), `IslandRumours.MatchLabel(string) -> RumourEntry?`, `IslandRumours.RankOffered(IEnumerable<string>) -> IReadOnlyList<RankedRumour>`, `IslandRumours.TierRank(string) -> int`, `IslandRumours.KnownMapNames` (HashSet), `RumourEntry` record, `RankedRumour` record — all consumed by Tasks 2, 3, 4.

---

- [ ] **Write the failing tests.** Create `tests/POE2Radar.Tests/IslandRumoursTests.cs` with the full content below. The class does NOT yet exist so every `[Fact]` will fail at compile time (CS0246) — that is the expected TDD red state.

```csharp
// tests/POE2Radar.Tests/IslandRumoursTests.cs
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class IslandRumoursTests
{
    // -- MatchLabel ---------------------------------------------------------

    [Fact]
    public void MatchLabel_ExactMatch_ReturnsEntry()
    {
        var r = IslandRumours.Shared.MatchLabel("Endless Cliffs");
        Assert.NotNull(r);
        Assert.Equal("A", r!.Tier);
        Assert.Equal("Craggy Peninsula", r.Map);
    }

    [Fact]
    public void MatchLabel_AsciiEllipsis_Stripped()
    {
        var r = IslandRumours.Shared.MatchLabel("Endless Cliffs...");
        Assert.NotNull(r);
        Assert.Equal("Craggy Peninsula", r!.Map);
    }

    [Fact]
    public void MatchLabel_UnicodeEllipsis_Stripped()
    {
        var r = IslandRumours.Shared.MatchLabel("Endless Cliffs…");
        Assert.NotNull(r);
        Assert.Equal("Craggy Peninsula", r!.Map);
    }

    [Fact]
    public void MatchLabel_LeadingAndTrailingWhitespace_Trimmed()
    {
        var r = IslandRumours.Shared.MatchLabel("  Endless Cliffs  ");
        Assert.NotNull(r);
        Assert.Equal("Endless Cliffs", r!.Rumor);
    }

    [Fact]
    public void MatchLabel_BothEllipsisVariants_OnlyOneStripped()
    {
        // NormaliseLabel is single-pass: input "Endless Cliffs...…"
        // after Trim() = "Endless Cliffs...…"
        // EndsWith(U+2026) is TRUE -> strips one char -> "Endless Cliffs..."
        // else-if not reached.
        // After second Trim() = "Endless Cliffs..."
        // ToLowerInvariant() = "endless cliffs..." which is NOT in the table.
        // Therefore the result is null -- intentional single-pass behaviour.
        var r = IslandRumours.Shared.MatchLabel("Endless Cliffs...…");
        Assert.Null(r);
    }

    [Fact]
    public void MatchLabel_CaseInsensitive_Upper()
    {
        var r = IslandRumours.Shared.MatchLabel("ENDLESS CLIFFS");
        Assert.NotNull(r);
        Assert.Equal("Endless Cliffs", r!.Rumor);
    }

    [Fact]
    public void MatchLabel_CaseInsensitive_Lower()
    {
        var r = IslandRumours.Shared.MatchLabel("endless cliffs");
        Assert.NotNull(r);
    }

    [Fact]
    public void MatchLabel_NoMatch_ReturnsNull()
        => Assert.Null(IslandRumours.Shared.MatchLabel("Nonexistent Rumour"));

    [Fact]
    public void MatchLabel_EmptyString_ReturnsNull()
        => Assert.Null(IslandRumours.Shared.MatchLabel(""));

    [Fact]
    public void MatchLabel_Saga_Aldurs_WithAsciiEllipsis()
    {
        var r = IslandRumours.Shared.MatchLabel("Aldurs...");
        Assert.NotNull(r);
        Assert.Equal("Saga", r!.Type);
        Assert.Equal("S+", r.Tier);
    }

    [Fact]
    public void MatchLabel_BossEncounter_Stardrinker()
    {
        var r = IslandRumours.Shared.MatchLabel("Stardrinker");
        Assert.NotNull(r);
        Assert.Equal("BossEncounter", r!.Type);
        Assert.Equal("S", r.Tier);
    }

    [Fact]
    public void MatchLabel_Saga_Medved()
    {
        var r = IslandRumours.Shared.MatchLabel("Medved");
        Assert.NotNull(r);
        Assert.Equal("Saga", r!.Type);
        Assert.Equal("B+", r.Tier);
        Assert.Equal("Strange Jungle", r.Map);
    }

    // -- TierRank -----------------------------------------------------------

    [Fact]
    public void TierRank_AllKnownTiers_CorrectValues()
    {
        Assert.Equal(6, IslandRumours.TierRank("S+"));
        Assert.Equal(5, IslandRumours.TierRank("S"));
        Assert.Equal(4, IslandRumours.TierRank("A"));
        Assert.Equal(3, IslandRumours.TierRank("B+"));
        Assert.Equal(2, IslandRumours.TierRank("B"));
        Assert.Equal(1, IslandRumours.TierRank("C"));
        Assert.Equal(0, IslandRumours.TierRank("F"));
        Assert.Equal(-1, IslandRumours.TierRank("X"));
        Assert.Equal(-1, IslandRumours.TierRank(""));
    }

    [Fact]
    public void TierRank_Ordering_BPlusGreaterThanB()
        => Assert.True(IslandRumours.TierRank("B+") > IslandRumours.TierRank("B"));

    [Fact]
    public void TierRank_Ordering_AGreaterThanBPlus()
        => Assert.True(IslandRumours.TierRank("A") > IslandRumours.TierRank("B+"));

    // -- RankOffered --------------------------------------------------------

    [Fact]
    public void RankOffered_ThreeTiers_SortedBestFirst()
    {
        var result = IslandRumours.Shared.RankOffered(
            ["Endless Cliffs", "Cold as ice", "Stardrinker"]);
        Assert.Equal(3, result.Count);
        Assert.Equal("Stardrinker", result[0].Entry.Rumor);    // S
        Assert.Equal("Endless Cliffs", result[1].Entry.Rumor); // A
        Assert.Equal("Cold as ice", result[2].Entry.Rumor);    // B
        Assert.True(result[0].IsBestPick);
        Assert.False(result[1].IsBestPick);
        Assert.False(result[2].IsBestPick);
    }

    [Fact]
    public void RankOffered_TopTierSaga_AldursIsFirst()
    {
        var result = IslandRumours.Shared.RankOffered(["Uhtred", "Aldurs", "Medved"]);
        Assert.Equal(3, result.Count);
        Assert.Equal("Aldurs", result[0].Entry.Rumor);  // S+
        Assert.True(result[0].IsBestPick);
        Assert.Equal("B+", result[1].Entry.Tier);       // Uhtred or Medved (both B+)
    }

    [Fact]
    public void RankOffered_BothBossesAreB_EitherOrderFirstIsBest()
    {
        // "The Last To Fall" (BossEncounter, B) and "End of the Circle" (BossEncounter, B)
        // are both tier B. Either may appear first after a stable sort of equal-rank entries.
        // The invariant: Count==2, result[0].IsBestPick==true, result[1].IsBestPick==false,
        // and both entries have Tier=="B".
        var result = IslandRumours.Shared.RankOffered(
            ["The Last To Fall", "End of the Circle"]);
        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsBestPick);
        Assert.False(result[1].IsBestPick);
        Assert.Equal("B", result[0].Entry.Tier);
        Assert.Equal("B", result[1].Entry.Tier);
    }

    [Fact]
    public void RankOffered_Dedupe_SameNormalisedLabel()
    {
        var result = IslandRumours.Shared.RankOffered(
            ["Endless Cliffs", "Endless Cliffs...", "Endless Cliffs…"]);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void RankOffered_AllNonMatching_EmptyList()
    {
        var result = IslandRumours.Shared.RankOffered(["Fake", "Also Fake"]);
        Assert.Empty(result);
    }

    [Fact]
    public void RankOffered_EmptyInput_EmptyList()
    {
        var result = IslandRumours.Shared.RankOffered([]);
        Assert.Empty(result);
    }

    [Fact]
    public void RankOffered_SingleEntry_IsBestPick()
    {
        var result = IslandRumours.Shared.RankOffered(["Bleak and Awful"]);
        Assert.Equal(1, result.Count);
        Assert.True(result[0].IsBestPick);
        Assert.Equal("F", result[0].Entry.Tier);
    }

    [Fact]
    public void RankOffered_EllipsisVariant_CountsOnce()
    {
        var result = IslandRumours.Shared.RankOffered(["Endless Cliffs...", "Stardrinker"]);
        Assert.Equal(2, result.Count);
        Assert.Equal("Stardrinker", result[0].Entry.Rumor);  // S beats A
        Assert.True(result[0].IsBestPick);
    }
}
```

- [ ] **Run tests — expect compile failure (red).**

```
dotnet test POE2Radar.slnx
```

Expected output contains:
```
error CS0246: The type or namespace name 'IslandRumours' could not be found
Build FAILED.
```

- [ ] **Add the EmbeddedResource to the csproj.** In `src/POE2Radar.Core/POE2Radar.Core.csproj`, add after line 24 (after the `dynasty_maps.json` entry):

```xml
    <EmbeddedResource Include="Game\island_rumours.json" />
```

The `<ItemGroup>` block (lines 14–25) then ends:
```xml
    <EmbeddedResource Include="Game\dynasty_maps.json" />
    <EmbeddedResource Include="Game\island_rumours.json" />
  </ItemGroup>
```

- [ ] **Create `src/POE2Radar.Core/Game/island_rumours.json`** with the full 25-entry array:

```json
[
  {
    "rumor": "Fallen Skies",
    "type": "UniqueMap",
    "map": "Moor of Fallen Skies",
    "mods": "Runestones",
    "tier": "S+",
    "note": "Unique Expedition Map, 9-10 Rune to unlock"
  },
  {
    "rumor": "All that glitters",
    "type": "UniqueMap",
    "map": "Castaway",
    "mods": "Gold",
    "tier": "A",
    "note": "All of the Gold"
  },
  {
    "rumor": "Reflective Waters",
    "type": "UniqueMap",
    "map": "Lake of Kalandra",
    "mods": "Ring Bases",
    "tier": "A",
    "note": "The best PoE1 league"
  },
  {
    "rumor": "Almost paradise",
    "type": "UniqueMap",
    "map": "Untainted paradise",
    "mods": "Exp",
    "tier": "C",
    "note": "Less XP than a City map"
  },
  {
    "rumor": "A good fellow",
    "type": "UniqueMap",
    "map": "Moment of Zen",
    "mods": "Seer",
    "tier": "C",
    "note": "Nameless seer hiding a mageblood"
  },
  {
    "rumor": "Endless Cliffs",
    "type": "Rumour",
    "map": "Craggy Peninsula",
    "mods": "Rarity+Rogue Exiles",
    "tier": "A",
    "note": "Good map - juice the rogue exiles"
  },
  {
    "rumor": "Sulphite!",
    "type": "Rumour",
    "map": "Scorched Cay",
    "mods": "Increased Rarity",
    "tier": "A",
    "note": "Juice the remnants for boss/Runes"
  },
  {
    "rumor": "Cold as ice",
    "type": "Rumour",
    "map": "Frigid Bluffs",
    "mods": "Old Expedition",
    "tier": "B",
    "note": "Juice hard, old-style expedition mobs"
  },
  {
    "rumor": "Unknown Ruins",
    "type": "Rumour",
    "map": "Exhumed Ruins",
    "mods": "Precursor Leylines",
    "tier": "B",
    "note": "Run only if adjacent logbook zone is empty"
  },
  {
    "rumor": "Warm but risky",
    "type": "Rumour",
    "map": "Grazed Prairie",
    "mods": "Exp+Beyond+Hoards",
    "tier": "B",
    "note": "Juice hard, old-style expedition mobs"
  },
  {
    "rumor": "Wild, Roaming Free",
    "type": "Rumour",
    "map": "Lush Island",
    "mods": "Azmeri Spirits",
    "tier": "B",
    "note": "Juice a strong boss with wisps"
  },
  {
    "rumor": "Something Fishy",
    "type": "Rumour",
    "map": "Bleached Shoals",
    "mods": "Amulets",
    "tier": "C",
    "note": "Source of All-Res Pearl Ammys"
  },
  {
    "rumor": "Nothing to drink",
    "type": "Rumour",
    "map": "Stagnant Basin",
    "mods": "Oil",
    "tier": "C",
    "note": "Check Runes else currency tiles"
  },
  {
    "rumor": "It's dry at least",
    "type": "Rumour",
    "map": "Sloughed Gully",
    "mods": "Monster effectiveness",
    "tier": "F",
    "note": "Can spawn untargetable enemies"
  },
  {
    "rumor": "Bleak and Awful",
    "type": "Rumour",
    "map": "Barren Atoll",
    "mods": "Strongbox",
    "tier": "F",
    "note": "Can spawn untargetable enemies"
  },
  {
    "rumor": "Crazed Chieftain",
    "type": "PowerfulBoss",
    "map": "Jade Isles",
    "mods": "Powerful Map Boss",
    "tier": "S+",
    "note": "Go get a Rakiatas Flow!"
  },
  {
    "rumor": "Stardrinker",
    "type": "BossEncounter",
    "map": "Secluded Temple",
    "mods": "Uhtred",
    "tier": "S",
    "note": "Drops the Drained Mana Rune"
  },
  {
    "rumor": "Origin of the Fall",
    "type": "BossEncounter",
    "map": "Obscure Island",
    "mods": "Olroth",
    "tier": "A",
    "note": "Drops Triskellion/Flask"
  },
  {
    "rumor": "The Last To Fall",
    "type": "BossEncounter",
    "map": "Mournful Cliffside",
    "mods": "Vorana",
    "tier": "B",
    "note": ""
  },
  {
    "rumor": "End of the Circle",
    "type": "BossEncounter",
    "map": "Sprawling Jungle",
    "mods": "Medved",
    "tier": "B",
    "note": ""
  },
  {
    "rumor": "Aldurs",
    "type": "Saga",
    "map": "NA",
    "mods": "Buffs expeditions",
    "tier": "S+",
    "note": "Gamble on the seed"
  },
  {
    "rumor": "Olroth",
    "type": "Saga",
    "map": "Obscure Island",
    "mods": "Boss Node",
    "tier": "A",
    "note": "Guarantees the boss encounter"
  },
  {
    "rumor": "Uhtred",
    "type": "Saga",
    "map": "Secluded Temple",
    "mods": "Boss Node",
    "tier": "B+",
    "note": "Guarantees the boss"
  },
  {
    "rumor": "Medved",
    "type": "Saga",
    "map": "Strange Jungle",
    "mods": "Boss Node",
    "tier": "B+",
    "note": "Guarantees the boss"
  },
  {
    "rumor": "Vorana",
    "type": "Saga",
    "map": "Mournful Cliffside",
    "mods": "Boss Node",
    "tier": "B+",
    "note": "Guarantees the boss"
  }
]
```

- [ ] **Create `src/POE2Radar.Core/Game/IslandRumours.cs`** with the full implementation:

```csharp
// src/POE2Radar.Core/Game/IslandRumours.cs
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

public sealed record RumourEntry(
    string Rumor,
    string Type,
    string Map,
    string Mods,
    string Tier,
    string Note);

public sealed record RankedRumour(
    RumourEntry Entry,
    bool IsBestPick);

/// <summary>Embedded island-rumour tier table for the Expedition "Uncharted Waters" selection screen.
/// Loaded once from the embedded <c>island_rumours.json</c>. Read-only; pure-function lookup helpers
/// are fully unit-testable (no memory, no Overlay types).</summary>
public sealed class IslandRumours
{
    // Singleton -- loaded once on first access, never throws out of Load().
    public static IslandRumours Shared { get; } = Load();

    // Full table keyed by normalised+lowercased label for O(1) lookup.
    private readonly Dictionary<string, RumourEntry> _byLabel;
    private IslandRumours(Dictionary<string, RumourEntry> byLabel) => _byLabel = byLabel;

    /// <summary>Known font/map-name prefixes that appear at Str1 (textStruct+0x20) in place of the
    /// actual display label. When Str1 starts with any of these, the read recipe falls back to
    /// Str2 (textStruct+0x50). Case-sensitive -- these are exact known prefixes.</summary>
    internal static readonly HashSet<string> KnownMapNames = new(StringComparer.Ordinal)
    {
        "Fontin Smallcaps",
        "OptimusPrincepsSemiBold",
    };

    // -- Tier rank (pure, static) -------------------------------------------

    /// <summary>Maps a tier string to an integer rank for sorting (higher = better).
    /// S+=6, S=5, A=4, B+=3, B=2, C=1, F=0, unknown/null=-1.</summary>
    public static int TierRank(string tier) => tier switch
    {
        "S+" => 6,
        "S"  => 5,
        "A"  => 4,
        "B+" => 3,
        "B"  => 2,
        "C"  => 1,
        "F"  => 0,
        _    => -1,
    };

    // -- Normalisation (private, pure) --------------------------------------

    /// <summary>Single-pass normalisation: trim surrounding whitespace; then strip exactly ONE
    /// trailing suffix -- either the horizontal ellipsis U+2026 ('...') OR three ASCII dots ("..."),
    /// checked in that order via if/else-if (only one suffix removed per call); then trim again.</summary>
    private static string NormaliseLabel(string s)
    {
        s = s.Trim();
        if (s.EndsWith('…'))       // horizontal ellipsis U+2026 -- checked first
            s = s[..^1];
        else if (s.EndsWith("..."))     // three ASCII dots -- only if U+2026 was NOT present
            s = s[..^3];
        return s.Trim();
    }

    // -- Public pure helpers ------------------------------------------------

    /// <summary>Normalise <paramref name="raw"/> and look it up in the table.
    /// Returns null when the string is empty or no entry matches.</summary>
    public RumourEntry? MatchLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var key = NormaliseLabel(raw).ToLowerInvariant();
        return _byLabel.TryGetValue(key, out var e) ? e : null;
    }

    /// <summary>For each label in <paramref name="labels"/>, call MatchLabel, drop non-matches and
    /// duplicates (by <c>RumourEntry.Rumor</c>), sort best-first by tier rank, set
    /// <c>IsBestPick = true</c> on index 0. Returns an empty list when nothing matches.</summary>
    public IReadOnlyList<RankedRumour> RankOffered(IEnumerable<string> labels)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matched = new List<RumourEntry>();
        foreach (var lbl in labels)
        {
            var e = MatchLabel(lbl);
            if (e == null) continue;
            if (!seen.Add(e.Rumor)) continue;  // dedupe by canonical rumor name
            matched.Add(e);
        }
        matched.Sort((a, b) => TierRank(b.Tier).CompareTo(TierRank(a.Tier)));
        var result = new List<RankedRumour>(matched.Count);
        for (int i = 0; i < matched.Count; i++)
            result.Add(new RankedRumour(matched[i], IsBestPick: i == 0));
        return result;
    }

    // -- Loader -------------------------------------------------------------

    private static IslandRumours Load()
    {
        var byLabel = new Dictionary<string, RumourEntry>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.Contains("island_rumours", StringComparison.Ordinal));
            if (name != null)
                using (var s = asm.GetManifestResourceStream(name))
                {
                    var list = s != null
                        ? JsonSerializer.Deserialize<List<RumourEntry>>(s,
                              new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        : null;
                    if (list != null)
                        foreach (var e in list)
                        {
                            var key = NormaliseLabel(e.Rumor).ToLowerInvariant();
                            byLabel[key] = e;
                        }
                }
        }
        catch (Exception ex) { Console.Error.WriteLine($"IslandRumours load failed: {ex.Message}"); }
        return new IslandRumours(byLabel);
    }
}
```

- [ ] **Run tests — expect green.**

```
dotnet test POE2Radar.slnx
```

Expected output (all 23 new facts pass, existing suite unchanged):
```
Passed!  - Failed: 0, Passed: <N+23>, Skipped: 0, Total: <N+23>
```

- [ ] **Commit.**

```
git add src/POE2Radar.Core/POE2Radar.Core.csproj src/POE2Radar.Core/Game/island_rumours.json src/POE2Radar.Core/Game/IslandRumours.cs tests/POE2Radar.Tests/IslandRumoursTests.cs
git commit -m "$(cat <<'EOF'
feat(core): island rumours tier table + pure lookup logic (TDD)

25-entry embedded JSON (S+ through F, qualitative notes, no prices);
IslandRumours singleton with MatchLabel / RankOffered / TierRank;
23 xUnit facts covering ellipsis-strip single-pass, dedup, sort, B+
ordering, both-B equal-tier test. Compliance gate unaffected (no
memory/input symbols added).
EOF
)"
```

---

### Task 2: Read layer (Core, build + gate verified)

**Files:**
- Modify: `src/POE2Radar.Core/Game/Poe2Offsets.cs` (add `public static class IslandRumour` after the closing brace of `HoverTracker`, inside `public static class Poe2`)
- Modify: `src/POE2Radar.Core/Game/Poe2Live.cs` (add `ReadOfferedRumours` + `TryReadRumourLabel`)

**Interfaces:**
- Consumes: `IslandRumours.Shared.MatchLabel` (Task 1), `IslandRumours.KnownMapNames` (Task 1), `Poe2.InGameState.UiRoot` (existing offset 0x2F0), `ChildSpan` at `Poe2Live.cs:1201`, `Ptr` at `Poe2Live.cs:1423`, `_reader.ReadStringUtf16` at `MemoryReader.cs:121`, `_reader.TryReadBytes` at `MemoryReader.cs:88`
- Produces: `Poe2Live.ReadOfferedRumours(nint inGameState) -> IReadOnlyList<string>` — consumed by Task 3.
- Note on `TryReadRumourLabel`: this private helper returns the raw display label string ONLY when it both passes the magic-guard AND matches a table entry (via MatchLabel); it returns "" in all other cases. The contract is: callers receive only matched, non-empty labels. The public `ReadOfferedRumours` accumulates these matched strings; `MatchLabel`/`RankOffered` are then applied again in Task 3 via `RankOffered(labels)`.

---

- [ ] **Add the `IslandRumour` offset class to `Poe2Offsets.cs`.** Open `src/POE2Radar.Core/Game/Poe2Offsets.cs`. After the closing brace of `public static class HoverTracker` and before the closing brace of `public static class Poe2`, insert:

```csharp
    /// <summary>Offsets for reading the Expedition "Uncharted Waters" label-widget text structs.
    /// Used by <see cref="Poe2Live.ReadOfferedRumours"/>.
    /// All validated live (Island Rumours offer screen, June 2026 GOLD dump).</summary>
    public static class IslandRumour
    {
        // Offset from a UiElement body to the text struct pointer.
        // body+0x138 is the text struct pointer for label widgets (confirmed across Ritual,
        // Runeforge, and Island Rumours panels in the June 2026 GOLD dump).
        // Validated live.
        public const int TextStructPtr = 0x138;

        // Magic guard bytes at text_struct+0x10. Read 8 bytes; must match exactly.
        // Fast-reject: if these bytes do not match, the element is not a label widget -- skip.
        // Validated live (confirmed in 83-slot pool scan, June 2026).
        public static ReadOnlySpan<byte> TextStructMagic =>
            [0x91, 0x9C, 0x9F, 0xFF, 0x01, 0x01, 0x00, 0x00];

        // Offset from text_struct base to Str1 (first string slot).
        // Contains the display label OR a font/map-name prefix (e.g. "Fontin Smallcaps").
        // Validated live.
        public const int Str1 = 0x20;

        // Offset from text_struct base to Str2 (second string slot).
        // Contains the actual display label when Str1 is a known font/map-name prefix.
        // Validated live.
        public const int Str2 = 0x50;
    }
```

- [ ] **Add `ReadOfferedRumours` and `TryReadRumourLabel` to `Poe2Live.cs`.** Locate the end of the `Poe2Live` class (after the last method, before the final closing brace). Add the following two methods. They use only the existing `ChildSpan`, `Ptr`, `_reader.TryReadBytes`, and `_reader.ReadStringUtf16` helpers — no new dependencies:

```csharp
    /// <summary>BFS the in-game UI tree from UiRoot, visiting ALL children unconditionally.
    /// Pool elements for the rumour catalog sit inside invisible wrapper elements; pruning on
    /// the visibility flag would discard the target data and produce empty results at the
    /// selection screen. Text-match against the rumour table (via MatchLabel inside
    /// TryReadRumourLabel) is the SOLE discriminator between target labels and the rest of
    /// the UI tree. Returns an EMPTY list when not at the rumour screen (zero text-matches).
    /// Bounded to <paramref name="maxNodes"/> elements. Runs on the world thread;
    /// allocates fresh collections per call -- call throttled (see RadarApp.UpdateIslandRumours).</summary>
    public IReadOnlyList<string> ReadOfferedRumours(nint inGameState, int maxNodes = 30000)
    {
        var results = new List<string>();
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return results;

        var queue = new Queue<nint>();
        queue.Enqueue(uiRoot);
        int visited = 0;

        while (queue.Count > 0 && visited < maxNodes)
        {
            var el = queue.Dequeue();
            if (el == 0) continue;
            visited++;

            // Attempt the text-read recipe (magic-guard + string extraction + table match).
            var label = TryReadRumourLabel(el);
            if (label.Length > 0)
                results.Add(label);

            // Enqueue children UNCONDITIONALLY -- do NOT gate on visibility.
            // The offered rumours sit inside invisible wrapper elements in the pool;
            // pruning invisible nodes would discard the target data.
            if (ChildSpan(el, out nint first, out long n))
                for (long k = 0; k < n; k++)
                {
                    var child = Ptr(first + (nint)(k * 8));
                    if (child != 0) queue.Enqueue(child);
                }
        }

        // Deduplicate by exact string (same label may appear in multiple pool slots).
        return results.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Attempt the Island Rumours text-struct read recipe on a single UI element.
    /// Returns the raw display label string when it passes the magic-guard AND matches an entry
    /// in the rumour table. Returns "" on any failure, fast-reject, or no table match.
    /// The table-match test is what distinguishes the 2-3 offered labels from the 80+ pool
    /// elements that also have text structs.</summary>
    private string TryReadRumourLabel(nint el)
    {
        // Step 1: read the text-struct pointer at body+0x138.
        nint ts = Ptr(el + Poe2.IslandRumour.TextStructPtr);
        if (ts == 0) return "";

        // Step 2: magic-guard -- read 8 bytes at ts+0x10 and reject if they don't match.
        Span<byte> magic = stackalloc byte[8];
        if (_reader.TryReadBytes(ts + 0x10, magic) != 8) return "";
        if (!magic.SequenceEqual(Poe2.IslandRumour.TextStructMagic)) return "";

        // Step 3: read Str1 (ts+0x20) -- the display label or a font/map-name prefix.
        string s1 = _reader.ReadStringUtf16(ts + Poe2.IslandRumour.Str1);

        // Step 4: if Str1 is a known font/map-name prefix, fall back to Str2 (ts+0x50).
        string label = IslandRumours.KnownMapNames.Contains(s1)
            ? _reader.ReadStringUtf16(ts + Poe2.IslandRumour.Str2)
            : s1;

        // Step 5: only return strings that match a table entry (text-match is the discriminator).
        // Returns "" when not matched, so ReadOfferedRumours accumulates only true rumour labels.
        return IslandRumours.Shared.MatchLabel(label) != null ? label : "";
    }
```

- [ ] **Build -- expect 0 errors.**

```
dotnet build POE2Radar.slnx -c Release
```

Expected output:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

- [ ] **Run compliance gate -- expect PASS.**

```
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1
```

Expected output:
```
PASS -- no forbidden symbols found.
```

- [ ] **Inspection check.** Verify the two methods contain ONLY `_reader.TryReadBytes`, `_reader.ReadStringUtf16`, `Ptr`, `ChildSpan`, and `IslandRumours` calls. Grep confirms no forbidden symbol appears in the new code:

```
grep -n "WriteProcessMemory\|SendInput\|PostMessage\|CreateRemoteThread" src/POE2Radar.Core/Game/Poe2Live.cs
```

Expected output: (empty -- no matches).

- [ ] **Commit.**

```
git add src/POE2Radar.Core/Game/Poe2Offsets.cs src/POE2Radar.Core/Game/Poe2Live.cs
git commit -m "$(cat <<'EOF'
feat(core): IslandRumour offsets + ReadOfferedRumours UI-tree walk

Poe2.IslandRumour offset group (TextStructPtr 0x138, TextStructMagic,
Str1 0x20, Str2 0x50; all validated June 2026 GOLD dump). Unconditional
BFS walk with magic-guard fast-reject; text-match is the sole
discriminator (zero false positives confirmed in 83-slot pool scan).
Read-only; gate green.
EOF
)"
```

---

### Task 3: Wiring + config (Overlay, build + gate verified)

**Files:**
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs` (add `ShowIslandRumours` property)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (add to `RadarState`, `/state` projection, `ReadSettings`, `ApplySettings`)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (add `IslandRumoursRender` record + volatile field, `_nextRumourPoll`, `_rumourActive`, `UpdateIslandRumours`, call site in `WorldTick`, render-thread snapshot, `RenderContext` build)
- Modify: `src/POE2Radar.Overlay/Overlay/RenderContext.cs` (add `ShowIslandRumours` + `IslandRumours` fields to `RenderContext`)

**Interfaces:**
- Consumes: `Poe2Live.ReadOfferedRumours` (Task 2), `IslandRumours.Shared.RankOffered` (Task 1), `RankedRumour` (Task 1)
- Produces: `_islandRumoursRender` volatile bundle, `RadarState.IslandRumours`, `RenderContext.ShowIslandRumours`, `RenderContext.IslandRumours` -- all consumed by Task 4.

---

- [ ] **Add `ShowIslandRumours` to `RadarSettings`.** In `src/POE2Radar.Overlay/Config/RadarSettings.cs`, after the `HighlightDynastyMaps` property, add:

```csharp
    // Island Rumours panel: shown at the Expedition "Uncharted Waters" screen only.
    // Default true -- the panel is contextual (only appears when rumours are offered)
    // so it is non-intrusive during normal play. The adaptive throttle makes idle cost negligible.
    public bool ShowIslandRumours { get; set; } = true;
```

- [ ] **Add `IslandRumours` to `RadarState`.** In `src/POE2Radar.Overlay/Web/ApiServer.cs`, in the `RadarState` record, add a new optional parameter after `SessionStats? Session = null`:

```csharp
    // Offered island rumours (ranked best-first) at the Expedition selection screen.
    // Null/empty when not at the rumour screen or feature disabled.
    IReadOnlyList<POE2Radar.Core.Game.RankedRumour>? IslandRumours = null)
```

The `RadarState.Empty` sentinel does NOT need to change -- the new parameter has a default value of `null` and the existing positional `new(...)` call in `Empty` omits it, which is valid for optional record parameters. Do not modify the `Empty` line.

- [ ] **Add `islandRumours` to the `/state` JSON projection.** In `ApiServer.Handle`, the `/state` case anonymous object, add the `islandRumours` field immediately after the `session` field:

```csharp
                    // Island Rumours: offered ranked picks at the Expedition screen (null when off-screen).
                    islandRumours = s.IslandRumours == null ? null : s.IslandRumours.Select(r => new
                    {
                        rumor  = r.Entry.Rumor,
                        type   = r.Entry.Type,
                        map    = r.Entry.Map,
                        mods   = r.Entry.Mods,
                        tier   = r.Entry.Tier,
                        note   = r.Entry.Note,
                        isBest = r.IsBestPick,
                    }).ToArray(),
```

- [ ] **Add `showIslandRumours` to `ReadSettings`.** In `ApiServer.ReadSettings()`, add after the `sessionHudExcludeTowns` line:

```csharp
        showIslandRumours = _settings.ShowIslandRumours,
```

- [ ] **Add `showIslandRumours` to `ApplySettings`.** In `ApiServer.ApplySettings()`, in the `switch (p.Name)` block, add before the "Anything else" comment:

```csharp
                case "showIslandRumours" when TryBool(p.Value, out var b):
                    _settings.ShowIslandRumours = b; applied.Add(p.Name); break;
```

- [ ] **Add `IslandRumoursRender` record + volatile field + throttle fields to `RadarApp`.** In `src/POE2Radar.Overlay/RadarApp.cs`, after the `_monoRender` field, add:

```csharp
    // -- Island Rumours (Expedition "Uncharted Waters" selection screen) ------
    // World thread reads the offered rumour labels + ranks them; render thread draws the panel.
    // Lock-free: the world thread writes _islandRumoursRender (volatile reference swap);
    // the render thread reads it once at the snapshot block into a local `ir`.
    private sealed record IslandRumoursRender(
        bool HasOffers,
        IReadOnlyList<POE2Radar.Core.Game.RankedRumour> Ranked)
    {
        public static readonly IslandRumoursRender Empty =
            new(false, Array.Empty<POE2Radar.Core.Game.RankedRumour>());
    }
    private volatile IslandRumoursRender _islandRumoursRender = IslandRumoursRender.Empty;

    // Adaptive throttle: ~2500 ms when no rumours found (not at the screen),
    // ~750 ms when active (at the selection screen, for responsiveness).
    private DateTime _nextRumourPoll = DateTime.UtcNow;
    private bool _rumourActive = false;
```

- [ ] **Add `UpdateIslandRumours` private method to `RadarApp`.** Add immediately after `UpdateMonoliths`:

```csharp
    /// <summary>Poll the offered island rumours at the Expedition "Uncharted Waters" screen.
    /// Runs on the world thread via <c>_live</c>. Adaptive throttle: ~2500 ms idle,
    /// ~750 ms when rumours are offered. Publishes a volatile <see cref="IslandRumoursRender"/>
    /// bundle; the render thread reads it lock-free at the snapshot block.</summary>
    private void UpdateIslandRumours(nint inGameState)
    {
        if (!_settings.ShowIslandRumours)
        {
            if (_islandRumoursRender.HasOffers) _islandRumoursRender = IslandRumoursRender.Empty;
            return;
        }
        if (DateTime.UtcNow < _nextRumourPoll) return;

        var labels = _live.ReadOfferedRumours(inGameState);
        var ranked = POE2Radar.Core.Game.IslandRumours.Shared.RankOffered(labels);
        bool hasOffers = ranked.Count > 0;

        _islandRumoursRender = hasOffers
            ? new IslandRumoursRender(true, ranked)
            : IslandRumoursRender.Empty;

        _rumourActive = hasOffers;
        _nextRumourPoll = DateTime.UtcNow.AddMilliseconds(_rumourActive ? 750 : 2500);
    }
```

- [ ] **Add the `UpdateIslandRumours` call site in `WorldTick`.** In `RadarApp.WorldTick`, add the call immediately after `UpdateMonoliths(...)`:

```csharp
        UpdateIslandRumours(inGameState);
```

- [ ] **Capture the snapshot in the render thread `Tick()`.** In the snapshot block where `_world`, `_atlasRender`, `_runeRender`, `_ritualRender`, and `_monoRender` are read, add:

```csharp
        var ir = _islandRumoursRender;   // island rumours lock-free snapshot
```

- [ ] **Add `IslandRumours` to the `RadarState` publish in `Tick()`.** In the `_state =` assignment, add the new field after `Session: _sessionSnapshot`:

```csharp
            Session: _sessionSnapshot,
            IslandRumours: ir.HasOffers ? ir.Ranked : null);
```

- [ ] **Add `ShowIslandRumours` and `IslandRumours` to `RenderContext`.** In `src/POE2Radar.Overlay/Overlay/RenderContext.cs`, after the last existing parameter in the `RenderContext` record (e.g. `SessionHudSettings`), add two new optional parameters:

```csharp
    bool ShowIslandRumours = true,
    IReadOnlyList<POE2Radar.Core.Game.RankedRumour>? IslandRumours = null);
```

- [ ] **Set the new `RenderContext` fields in `Tick()`.** In `RadarApp.Tick()`, in the `RenderContext` constructor call, add the two new fields after `SessionHudSettings: _settings.SessionHud`:

```csharp
            SessionHudSettings: _settings.SessionHud,
            ShowIslandRumours: _settings.ShowIslandRumours,
            IslandRumours: ir.HasOffers ? ir.Ranked : null);
```

- [ ] **Build -- expect 0 errors.**

```
dotnet build POE2Radar.slnx -c Release
```

Expected output:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

- [ ] **Run compliance gate -- expect PASS.**

```
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1
```

Expected output:
```
PASS -- no forbidden symbols found.
```

- [ ] **Inspection check -- adaptive throttle + own reader + gated.** Verify the following by reading `UpdateIslandRumours`:
  - Uses `_live` (not `_liveRender` or `_liveApi`). Grep: `grep -n "_live\." src/POE2Radar.Overlay/RadarApp.cs | grep -i "ReadOfferedRumours"` -- shows `_live.ReadOfferedRumours`.
  - Throttle guard `if (DateTime.UtcNow < _nextRumourPoll) return;` is present.
  - Early-exit when `!_settings.ShowIslandRumours` is present.

- [ ] **Commit.**

```
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Overlay/RenderContext.cs
git commit -m "$(cat <<'EOF'
feat(overlay): wire IslandRumours into world tick + RadarState + config

Adaptive throttle (750ms active / 2500ms idle), own _live reader stack,
ShowIslandRumours default true with /api/settings round-trip. Volatile
IslandRumoursRender lock-free published to render thread. /state exposes
islandRumours array. RenderContext carries ShowIslandRumours + Ranked.
Gate green; no pricing or input symbols introduced.
EOF
)"
```

---

### Task 4: Display (Overlay, build-verified)

**Files:**
- Modify: `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (add `TierColor`, `DrawIslandRumours`, call site)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (add `#rumours-panel` card in `<aside>`, JS in `tick()`, CSS, Settings toggle)

**Interfaces:**
- Consumes: `RenderContext.ShowIslandRumours` (Task 3), `RenderContext.IslandRumours` (Task 3), `RankedRumour.Entry`, `RumourEntry.Tier/Map/Note/Rumor`, `RankedRumour.IsBestPick` (Task 1), `RadarState.IslandRumours` (Task 3) via `/state`
- Produces: visible overlay panel + dashboard live card + Settings toggle.

---

- [ ] **Add `TierColor` static helper to `OverlayRenderer.cs`.** Add this private static method near `ColorFromU`:

```csharp
    /// <summary>Map an island-rumour tier string to a Direct2D Color4 for the tier badge text.</summary>
    private static Color4 TierColor(string tier) => tier switch
    {
        "S+" => new Color4(1.0f, 0.85f, 0.2f, 1f),   // gold
        "S"  => new Color4(0.8f, 1.0f, 0.2f, 1f),    // gold-green
        "A"  => new Color4(0.4f, 1.0f, 0.4f, 1f),    // green
        "B+" => new Color4(1.0f, 1.0f, 0.3f, 1f),    // yellow
        "B"  => new Color4(0.9f, 0.9f, 0.2f, 1f),    // yellow dimmer
        "C"  => new Color4(1.0f, 0.5f, 0.1f, 1f),    // orange
        "F"  => new Color4(1.0f, 0.2f, 0.2f, 1f),    // red
        _    => new Color4(0.7f, 0.7f, 0.7f, 1f),    // grey (unknown)
    };
```

- [ ] **Add `DrawIslandRumours` private method to `OverlayRenderer.cs`.** Add after `DrawSessionHud`, before the next method. The background rectangle height is pre-computed in a single pass before `FillRectangle` is called; the draw loop uses only `cy` (a separate cursor variable) and must NOT re-modify `h` after `FillRectangle`:

```csharp
    /// <summary>Screen-space ranked island-rumour panel, drawn at the Expedition "Uncharted Waters"
    /// screen. Mirrors <see cref="DrawMonolithPanel"/> geometry: FillRectangle + per-row DrawText.
    /// Drawn only when <c>ctx.ShowIslandRumours</c> is true AND offered rumours are present.
    ///
    /// Height calculation: a single pre-scan pass accumulates all row heights (title + per-rumour
    /// main row + optional note sub-row) before FillRectangle is issued. The draw loop uses a
    /// separate cursor variable <c>cy</c> and does NOT modify <c>h</c> after FillRectangle.</summary>
    private void DrawIslandRumours(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (!ctx.ShowIslandRumours || ctx.IslandRumours is not { Count: > 0 } rumours) return;

        const float w = 260f, pad = 6f, lineH = 15f, titleH = 18f;
        // Anchor: top-left corner, below the session HUD's reserved space.
        float x = 10f, y = 90f;

        // Pre-compute total panel height (single pass, before FillRectangle).
        float h = titleH + rumours.Count * lineH + pad * 2f;
        foreach (var rr in rumours)
            if (!string.IsNullOrEmpty(rr.Entry.Note)) h += lineH;

        // 1. Panel background -- issued once with the fully pre-computed height.
        rt.FillRectangle(new Vortice.RawRectF(x, y, x + w, y + h), _bPanel!);

        // 2. Title row -- cy is the only variable that advances through the draw loop.
        float cy = y + pad;
        rt.DrawText("Island Rumours", _tf!, new Rect(x + pad, cy, x + w - pad, cy + titleH),
            _bText!, DrawTextOptions.Clip);
        cy += titleH;

        // 3. Per-rumour rows -- h is NOT modified below this point.
        foreach (var rr in rumours)
        {
            // Tier badge color.
            _bStyle!.Color = TierColor(rr.Entry.Tier);

            // Best-pick marker: ">" for the top pick, " " for others.
            string marker = rr.IsBestPick ? ">" : " ";

            // Row text: "[TIER ] MAP" (Map may be "NA" for Sagas -- shown as-is).
            string row = $"{marker}[{rr.Entry.Tier,-2}] {rr.Entry.Map}";
            rt.DrawText(row, _tf!, new Rect(x + pad, cy, x + w - pad, cy + lineH),
                _bStyle, DrawTextOptions.Clip);
            cy += lineH;

            // Note sub-row (if non-empty): indented, white text.
            if (!string.IsNullOrEmpty(rr.Entry.Note))
            {
                rt.DrawText($"     {rr.Entry.Note}", _tf!,
                    new Rect(x + pad, cy, x + w - pad, cy + lineH),
                    _bText!, DrawTextOptions.Clip);
                cy += lineH;
            }
            // h is intentionally NOT touched here -- the pre-scan already owns all height accounting.
        }
    }
```

- [ ] **Add the `DrawIslandRumours` call site.** In `OverlayRenderer.Render`, inside the `if (ctx.Active && ctx.InGame)` block, add the call after `DrawSessionHud`:

```csharp
            if (ctx.Active && ctx.InGame)
            {
                DrawRuneforge(rt, ctx);
                DrawRitualRewards(rt, ctx);
                DrawMonolithPanel(rt, ctx);
                DrawSessionHud(rt, ctx);
                DrawIslandRumours(rt, ctx);    // NEW -- drawn at Expedition selection screen only
            }
```

- [ ] **Add the `#rumours-panel` sidebar card to `DashboardHtml.cs`.** In the `<aside>` section, after the `#session-panel` closing `</div>` and before the `<div style="height:24px">` spacer, insert:

```html
      <div id="rumours-panel" class="card" style="display:none">
        <div class="card-title">Island Rumours</div>
        <div id="rumours-list"></div>
      </div>
```

- [ ] **Add Island Rumours CSS to the dashboard `<style>` block.** Add the following rules at the end of the style block (before `</style>`):

```css
.rumour-row{padding:2px 0;font-size:12px;}
.rumour-note{color:#aaa;font-size:11px;padding-left:8px;}
.badge{font-weight:bold;min-width:24px;display:inline-block;}
.tier-Sp{color:#d4af37;}
.tier-S{color:#b0e040;}
.tier-A{color:#40e040;}
.tier-Bp{color:#e0e040;}
.tier-B{color:#c0c020;}
.tier-C{color:#e08020;}
.tier-F{color:#e02020;}
```

(`B+` maps to CSS class `tier-Bp`; `S+` maps to `tier-Sp` -- `+` is not valid in a CSS class name.)

- [ ] **Add Island Rumours update to the dashboard `tick()` function.** In `DashboardHtml.cs`, in the JavaScript `tick()` async polling loop, after the session-panel update block, add:

```javascript
// Island Rumours live panel
const rp=document.getElementById('rumours-panel');
const rl=document.getElementById('rumours-list');
if(state.islandRumours&&state.islandRumours.length>0){
  rp.style.display='';
  rl.innerHTML=state.islandRumours.map(r=>{
    const best=r.isBest?' &#9733;':'';
    const cls='tier-'+r.tier.replace('+','p');
    const note=r.note?`<div class="rumour-note">${esc(r.note)}</div>`:'';
    return `<div class="rumour-row ${cls}"><span class="badge">${esc(r.tier)}</span> `+
           `<b>${esc(r.map)}</b>${best}${note}</div>`;
  }).join('');
}else{
  rp.style.display='none';
  rl.innerHTML='';
}
```

- [ ] **Add the Settings toggle for Island Rumours to `DashboardHtml.cs`.** In the Settings tab section, after the `showMonolithPanel` toggle row, add:

```html
            <div class="row"><div class="rl">Island Rumours panel<small>Show ranked rumour picks at the Expedition Uncharted Waters screen</small></div>
              <label class="sw"><input type="checkbox" data-set="showIslandRumours"><span class="track"></span><span class="knob"></span></label></div>
```

`wireSettings()` auto-wires `data-set="showIslandRumours"` to `saveSetting('showIslandRumours', el.checked)` and POST to `/api/settings`. No additional JavaScript is needed.

- [ ] **Build -- expect 0 errors.**

```
dotnet build POE2Radar.slnx -c Release
```

Expected output:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

- [ ] **Inspection check.** Open `OverlayRenderer.cs` and confirm:
  - `DrawIslandRumours` is called ONLY from inside the `if (ctx.Active && ctx.InGame)` block (same block as `DrawMonolithPanel` and `DrawSessionHud`) -- NOT inside the `if (ctx.Map.IsVisible)` branch.
  - No `SendInput`, `PostMessage`, `WriteProcessMemory`, or pricing calls appear in the new methods.
  - `TierColor` covers all 7 tiers plus the wildcard `_` default.
  - The draw loop does NOT contain any `h += lineH` statements (the pre-scan owns all height accounting; the draw loop advances only `cy`).

- [ ] **Commit.**

```
git add src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "$(cat <<'EOF'
feat(overlay): DrawIslandRumours panel + dashboard Rumours card + Settings toggle

Screen-space panel mirroring DrawMonolithPanel geometry: pre-scan height
accumulation before FillRectangle (cy-only draw loop, h never touched
post-fill); tier-badge color per TierColor; best-pick ">"; map name;
note sub-row. Drawn only when ShowIslandRumours && rumours present &&
Active+InGame. Dashboard live card reads state.islandRumours; Settings
toggle data-set="showIslandRumours" auto-wired by wireSettings(). Build
0 errors.
EOF
)"
```

---

### Task 5: Integration verification & smoke checklist

**Files:** No code changes.

---

- [ ] **Full build (Release), 0 errors.**

```
dotnet build POE2Radar.slnx -c Release
```

Expected output:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

- [ ] **Full test suite -- Task 1 suite green, all existing tests pass.**

```
dotnet test POE2Radar.slnx
```

Expected output (all 23 new `IslandRumoursTests` facts pass; no regressions):
```
Passed!  - Failed: 0, Passed: <N+23>, Skipped: 0, Total: <N+23>
```

- [ ] **Compliance gate -- PASS.**

```
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1
```

Expected output:
```
PASS -- no forbidden symbols found.
```

- [ ] **Scrub self-test -- PASS.**

```
powershell -ExecutionPolicy Bypass -File scripts/scrub-strings.ps1 -SelfTest
```

Expected output:
```
PASS
```

- [ ] **Manual smoke checklist (requires live PoE2 client).** Launch the overlay (`POE2Radar.Overlay.exe`). Perform the following checks in order:

  1. **Off-screen baseline.** Be in any non-Expedition area. Confirm the Island Rumours panel does NOT appear on the overlay. Confirm `GET /state` returns `islandRumours: null`.

  2. **At the Uncharted Waters screen.** Open the Expedition "Uncharted Waters" rumour-selection screen. Confirm:
     - The overlay panel appears within ~1 second (first active poll at 750 ms).
     - The panel shows EXACTLY 2 or 3 rows -- no more, no fewer -- matching the rumours currently offered on-screen.
     - The top row is prefixed with `>` (best pick).
     - Each row shows the correct tier badge (e.g. `[S+]`, `[A ]`) and map name from `island_rumours.json`.
     - Note sub-rows appear for entries with non-empty `note` fields.
     - Note sub-rows are drawn INSIDE the background panel (not clipped outside it).
     - Tier badge color matches `TierColor`: S+ = gold, A = green, F = red, etc.

  3. **Tier ordering.** If the offered set includes rumours of different tiers, the overlay rows are sorted best-tier first (S+ before A before B, etc.).

  4. **Dashboard live card.** Open `http://localhost:7777` in a browser. The right-hand sidebar shows the "Island Rumours" card with the same ranked data as the overlay. Card is hidden when not at the selection screen.

  5. **Settings toggle.** In the dashboard Settings tab, uncheck "Island Rumours panel". Confirm the overlay panel disappears immediately (next render frame). Re-check -- panel reappears within ~1 second. Confirm the toggle is persisted: restart the overlay and verify `ShowIslandRumours` is saved in `config/radar_settings.json`.

  6. **Return to normal gameplay.** Navigate away from the Uncharted Waters screen. Confirm the overlay panel disappears and the dashboard card hides. Confirm there is no perceptible performance impact (the adaptive throttle resumes 2500 ms idle polling).

  7. **Compliance re-check.** Run `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1` one final time after the smoke test. Expected: `PASS`.
