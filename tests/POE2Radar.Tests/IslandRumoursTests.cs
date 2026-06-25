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
