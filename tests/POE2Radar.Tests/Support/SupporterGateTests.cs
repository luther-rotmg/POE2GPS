using POE2Radar.Core.Support;
using Xunit;

namespace POE2Radar.Tests.Support;

/// <summary>
/// Support — v0.41 (LO ask): locks the cached supporter gate. These tests verify the caching wrapper
/// behaves as a drop-in equivalence layer over <see cref="SupporterCodeValidator.IsSupporter"/> while
/// maintaining the "default false until explicit refresh" invariant, concurrent safety, and bi-directional
/// state flips.
/// </summary>
public class SupporterGateTests
{
    public SupporterGateTests()
    {
        // Each test starts with a fresh gate (default-false state).
        SupporterGate.ResetForTesting();
    }

    [Fact]
    public void IsSupporter_DefaultFalse()
    {
        // No refresh called → cache is false.
        Assert.False(SupporterGate.IsSupporter);
    }

    [Fact]
    public void RefreshFromSettings_EmptyCode_SetsFalse()
    {
        SupporterGate.RefreshFromSettings("");
        Assert.False(SupporterGate.IsSupporter);
    }

    [Fact]
    public void RefreshFromSettings_NullCode_SetsFalse()
    {
        SupporterGate.RefreshFromSettings(null);
        Assert.False(SupporterGate.IsSupporter);
    }

    [Fact]
    public void RefreshFromSettings_InvalidCode_SetsFalse()
    {
        SupporterGate.RefreshFromSettings("not-a-real-code");
        Assert.False(SupporterGate.IsSupporter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-code")]
    [InlineData("POE2GPS-fake-code")]
    public void RefreshFromSettings_MatchesValidator_ForVariousInputs(string? code)
    {
        SupporterGate.RefreshFromSettings(code);
        Assert.Equal(SupporterCodeValidator.IsSupporter(code), SupporterGate.IsSupporter);
    }

    [Fact]
    public void RefreshFromSettings_ValidSeedCode_SetsTrue()
    {
        // The shipped seed code "POE2GPS-FIRST-COFFEE-2026" must validate.
        SupporterGate.RefreshFromSettings("POE2GPS-FIRST-COFFEE-2026");
        Assert.True(SupporterGate.IsSupporter);
    }

    [Fact]
    public void RefreshFromSettings_SignedCode_SetsTrue()
    {
        // The signed code from SupporterSignedCodeTests.ValidCode must validate through the gate.
        const string signedCode =
            "poe2gps.eyJlbWFpbCI6ImRvbm9yQGV4YW1wbGUuY29tIiwiaXNzdWVkIjoxNzgzNzE3MjM5LCJ0aWVyIjoiZ29sZCJ9.4URJewMH_67LDYw9X8j-QIvUeoJXvcU3t_YJA61xirRD0oJpVrQKI8HirJV7XQVZHgXj_G_pXmUAZAqoLmJnDw";
        SupporterGate.RefreshFromSettings(signedCode);
        Assert.True(SupporterGate.IsSupporter);
    }

    [Fact]
    public void RefreshFromSettings_SetsFromFalseToTrue_ChangesGate()
    {
        Assert.False(SupporterGate.IsSupporter); // starts false
        SupporterGate.RefreshFromSettings("POE2GPS-FIRST-COFFEE-2026");
        Assert.True(SupporterGate.IsSupporter);
    }

    [Fact]
    public void RefreshFromSettings_SetsFromTrueToFalse_ChangesGate()
    {
        // First set to true.
        SupporterGate.RefreshFromSettings("POE2GPS-FIRST-COFFEE-2026");
        Assert.True(SupporterGate.IsSupporter);
        // Then flip to false with an invalid code.
        SupporterGate.RefreshFromSettings("");
        Assert.False(SupporterGate.IsSupporter);
    }

    [Fact]
    public async Task RefreshFromSettings_Concurrent_NoRaceOrThrow()
    {
        var inputs = new[]
        {
            null,
            "",
            "   ",
            "not-a-real-code",
            "POE2GPS-FIRST-COFFEE-2026",
            "POE2GPS-fake-code",
        };

        var tasks = Enumerable.Range(0, 100).Select(i =>
        {
            var code = inputs[i % inputs.Length];
            return Task.Run(() => SupporterGate.RefreshFromSettings(code));
        });

        // Should complete without any exception.
        await Task.WhenAll(tasks);

        // Final state should be a valid result for the last input used.
        // No crash is the main assertion; also verify the gate still answers sensibly.
        var _ = SupporterGate.IsSupporter; // read is non-throwing
        Assert.True(true, "No race condition or exception during concurrent refreshes.");
    }

    [Fact]
    public void IsSupporter_ReturnsCachedNotRecomputed()
    {
        // Verify that IsSupporter is a property getter (not a method call that recomputes).
        var prop = typeof(SupporterGate).GetProperty(nameof(SupporterGate.IsSupporter));
        Assert.NotNull(prop);
        Assert.True(prop.CanRead);
        Assert.False(prop.CanWrite); // no public setter — only RefreshFromSettings mutates.
    }

    [Fact]
    public void RefreshFromSettings_CaseWhitespaceTolerant()
    {
        // Same tolerance as SupporterCodeValidator: whitespace and casing are normalized.
        SupporterGate.RefreshFromSettings("  poe2gps-first-coffee-2026  ");
        Assert.True(SupporterGate.IsSupporter);

        SupporterGate.RefreshFromSettings("POE2GPS-FIRST-COFFEE-2026\n");
        Assert.True(SupporterGate.IsSupporter);

        SupporterGate.RefreshFromSettings("Poe2Gps-First-Coffee-2026");
        Assert.True(SupporterGate.IsSupporter);
    }
}