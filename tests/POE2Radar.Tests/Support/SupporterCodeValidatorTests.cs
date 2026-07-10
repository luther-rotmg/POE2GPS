using POE2Radar.Core.Support;
using Xunit;

namespace POE2Radar.Tests.Support;

/// <summary>Support — v0.27: locks the honor-system supporter check.</summary>
public class SupporterCodeValidatorTests
{
    [Fact]
    public void Null_or_empty_or_whitespace_is_not_a_supporter()
    {
        Assert.False(SupporterCodeValidator.IsSupporter(null));
        Assert.False(SupporterCodeValidator.IsSupporter(""));
        Assert.False(SupporterCodeValidator.IsSupporter("   "));
    }

    [Fact]
    public void Random_code_is_not_a_supporter()
    {
        Assert.False(SupporterCodeValidator.IsSupporter("not-a-real-code"));
        Assert.False(SupporterCodeValidator.IsSupporter("POE2GPS-fake-code"));
    }

    [Fact]
    public void ComputeHash_is_deterministic_and_case_insensitive_and_trims_whitespace()
    {
        var a = SupporterCodeValidator.ComputeHash("test");
        var b = SupporterCodeValidator.ComputeHash("TEST");
        var c = SupporterCodeValidator.ComputeHash("  test  ");
        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(64, a.Length);   // SHA-256 hex is 64 chars
    }

    [Fact]
    public void Shipped_seed_code_validates()
    {
        // Support automation — v0.27.1: the shipped supporter_hashes.json seeds one working code
        // ("POE2GPS-FIRST-COFFEE-2026") so the feature is discoverable out-of-the-box. If someone
        // rotates the seed hash out, replace this test's raw code with a new seed — this test is the
        // "at least one code validates" invariant.
        Assert.True(SupporterCodeValidator.IsSupporter("POE2GPS-FIRST-COFFEE-2026"));
    }

    [Fact]
    public void Case_and_whitespace_tolerant_on_positive_path()
    {
        // Users pasting from Ko-fi email may pick up whitespace or the wrong casing — accept both.
        Assert.True(SupporterCodeValidator.IsSupporter("  poe2gps-first-coffee-2026  "));
        Assert.True(SupporterCodeValidator.IsSupporter("POE2GPS-FIRST-COFFEE-2026\n"));
        Assert.True(SupporterCodeValidator.IsSupporter("Poe2Gps-First-Coffee-2026"));
    }

    [Fact]
    public void HashCount_is_nonzero_after_load()
    {
        Assert.True(SupporterCodeValidator.HashCount >= 1,
            $"Expected shipped supporter_hashes.json to seed at least one hash; loaded {SupporterCodeValidator.HashCount}.");
    }
}
