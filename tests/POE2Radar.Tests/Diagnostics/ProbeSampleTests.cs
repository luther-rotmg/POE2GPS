using POE2Radar.Core.Diagnostics;
using Xunit;

namespace POE2Radar.Tests.Diagnostics;

public sealed class ProbeSampleTests
{
    [Fact]
    public void ProbeSample_Equality_TwoIdenticalSamplesAreEqual()
    {
        var a = new ProbeSample<int>("0x1B0", "0x7ffe1234", 42, null, true);
        var b = new ProbeSample<int>("0x1B0", "0x7ffe1234", 42, null, true);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ProbeSample_Equality_DifferentValuesAreNotEqual()
    {
        var a = new ProbeSample<int>("0x1B0", "0x7ffe1234", 42, null, true);
        var b = new ProbeSample<int>("0x1B0", "0x7ffe1234", 43, null, true);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ProbeSample_OffsetHex_FormatIsUppercaseWithoutLeadingZeros()
    {
        var sample = new ProbeSample<int>("0x1B0", "0x7ffe1234", 100, null, true);

        Assert.Equal("0x1B0", sample.OffsetHex);
        // Verify it does NOT have leading zeros
        Assert.DoesNotContain("0x0", sample.OffsetHex.Substring(2));
    }

    [Fact]
    public void ProbeSample_PassesSignature_FalseWhenReadFailReasonNonNull()
    {
        var sample = new ProbeSample<int>("0x1B0", "0x7ffe1234", default, "target not user-mode", false);

        Assert.NotNull(sample.ReadFailReason);
        Assert.False(sample.PassesSignature);
        Assert.Equal(default(int), sample.Value);
    }

    [Fact]
    public void ProbeSample_WithNullableString_Works()
    {
        var sample = new ProbeSample<string>("0x208", "0x7ffe5678", "mana_value", null, true);

        Assert.Equal("mana_value", sample.Value);
        Assert.Null(sample.ReadFailReason);
        Assert.True(sample.PassesSignature);
    }

    [Fact]
    public void ProbeSample_Deconstruct_MatchesPositionalArgs()
    {
        var sample = new ProbeSample<int>("0x1B0", "0x7ffe1234", 42, null, true);

        var (hex, addr, val, reason, pass) = sample;

        Assert.Equal("0x1B0", hex);
        Assert.Equal("0x7ffe1234", addr);
        Assert.Equal(42, val);
        Assert.Null(reason);
        Assert.True(pass);
    }
}