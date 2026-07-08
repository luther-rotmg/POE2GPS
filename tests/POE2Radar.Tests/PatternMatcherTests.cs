using POE2Radar.Core.Campaign.Guide;
using Xunit;

namespace POE2Radar.Tests;

public class PatternMatcherTests
{
    [Fact]
    public void Literal_IsMatch_IsCaseInsensitiveSubstring()
    {
        var m = new PatternMatcher(new Pattern("Doryani"));
        Assert.True(m.IsMatch("doryani the majestic"));
        Assert.True(m.IsMatch("DORYANI"));
        Assert.False(m.IsMatch("someone else"));
    }

    [Fact]
    public void Literal_IsMatch_ReturnsFalseOnNullOrEmpty()
    {
        var m = new PatternMatcher(new Pattern("x"));
        Assert.False(m.IsMatch(null));
        Assert.False(m.IsMatch(""));
    }

    [Fact]
    public void EmptyLiteral_IsMatch_ReturnsFalse()
    {
        var m = new PatternMatcher(new Pattern(""));
        Assert.False(m.IsMatch("anything"));
    }

    [Fact]
    public void Regex_IsMatch_CompilesIgnoreCase()
    {
        var m = new PatternMatcher(new Pattern("^doryani.*majestic$", Regex: true));
        Assert.Null(m.Error);
        Assert.True(m.IsMatch("Doryani the Majestic"));
        Assert.False(m.IsMatch("Not Doryani"));
    }

    [Fact]
    public void InvalidRegex_FallsBackToLiteralAndExposesError()
    {
        var m = new PatternMatcher(new Pattern("[unterminated", Regex: true));
        Assert.NotNull(m.Error);
        Assert.True(m.IsMatch("this contains [unterminated somewhere"));
        Assert.False(m.IsMatch("no match here"));
    }
}
