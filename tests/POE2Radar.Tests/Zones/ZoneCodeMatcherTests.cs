using POE2Radar.Core.Zones;

namespace POE2Radar.Tests.Zones;

public class ZoneCodeMatcherTests
{
    [Fact]
    public void Match_ExactString_Matches()
    {
        Assert.True(ZoneCodeMatcher.Match("G1_town", "G1_town"));
    }

    [Fact]
    public void Match_ExactString_MismatchDoesNotMatch()
    {
        Assert.False(ZoneCodeMatcher.Match("G1_town", "G2_town"));
    }

    [Fact]
    public void Match_SuffixWildcard_Matches()
    {
        Assert.True(ZoneCodeMatcher.Match("T17_*", "T17_Necropolis"));
    }

    [Fact]
    public void Match_SuffixWildcard_MismatchOnDifferentPrefix()
    {
        Assert.False(ZoneCodeMatcher.Match("T17_*", "T18_Foo"));
    }

    [Fact]
    public void Match_PrefixWildcard_Matches()
    {
        Assert.True(ZoneCodeMatcher.Match("*_town", "G1_town"));
    }

    [Fact]
    public void Match_PrefixWildcard_MismatchOnDifferentSuffix()
    {
        Assert.False(ZoneCodeMatcher.Match("*_town", "G1_hideout"));
    }

    [Fact]
    public void Match_SuffixWildcard_ExactPrefixOnly()
    {
        Assert.False(ZoneCodeMatcher.Match("*_town", "G1_town_a"));
    }

    [Fact]
    public void Match_JustWildcard_MatchesAnything()
    {
        Assert.True(ZoneCodeMatcher.Match("*", "anything"));
    }

    [Fact]
    public void Match_JustWildcard_MatchesEmpty()
    {
        Assert.True(ZoneCodeMatcher.Match("*", ""));
    }

    [Fact]
    public void Match_EmptyPattern_MatchesEmpty()
    {
        Assert.True(ZoneCodeMatcher.Match("", ""));
    }

    [Fact]
    public void Match_EmptyPattern_DoesNotMatchNonEmpty()
    {
        Assert.False(ZoneCodeMatcher.Match("", "G1_town"));
    }

    [Fact]
    public void Match_MultipleWildcards()
    {
        Assert.True(ZoneCodeMatcher.Match("*town*", "G1_town_a"));
    }

    [Fact]
    public void Match_NullPattern_ReturnsFalse()
    {
        Assert.False(ZoneCodeMatcher.Match(null!, "anything"));
    }

    [Fact]
    public void Match_NullZoneCode_ReturnsFalse()
    {
        Assert.False(ZoneCodeMatcher.Match("*", null!));
    }

    [Fact]
    public void Match_CaseSensitive()
    {
        Assert.False(ZoneCodeMatcher.Match("*_town", "G1_TOWN"));
    }

    [Fact]
    public void Match_LiteralDotInPattern_TreatedAsLiteral()
    {
        Assert.True(ZoneCodeMatcher.Match("g.town", "g.town"));
    }
}