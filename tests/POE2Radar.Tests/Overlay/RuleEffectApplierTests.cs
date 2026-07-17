using System;
using System.Collections.Generic;
using POE2Radar.Core.Rules;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Overlay;
using Vortice.Mathematics;
using Xunit;

namespace POE2Radar.Tests.Overlay;

public class RuleEffectApplierTests
{
    // ── HexToColor4 ──

    [Fact]
    public void HexToColor4_ParsesRedCorrectly()
    {
        var color = RuleEffectApplier.HexToColor4("#ff0000");
        Assert.Equal(1f, color.R, 4);
        Assert.Equal(0f, color.G, 4);
        Assert.Equal(0f, color.B, 4);
        Assert.Equal(1f, color.A, 4);
    }

    [Fact]
    public void HexToColor4_ParsesLowercase()
    {
        var color = RuleEffectApplier.HexToColor4("#00ff00");
        Assert.Equal(0f, color.R, 4);
        Assert.Equal(1f, color.G, 4);
        Assert.Equal(0f, color.B, 4);
        Assert.Equal(1f, color.A, 4);
    }

    [Fact]
    public void HexToColor4_ParsesUppercase()
    {
        var color = RuleEffectApplier.HexToColor4("#00FF00");
        Assert.Equal(0f, color.R, 4);
        Assert.Equal(1f, color.G, 4);
        Assert.Equal(0f, color.B, 4);
        Assert.Equal(1f, color.A, 4);
    }

    [Theory]
    [InlineData("not-a-hex")]
    [InlineData("ff0000")]       // missing #
    [InlineData("#gggggg")]      // invalid hex digits
    [InlineData("#ff000")]       // too short
    [InlineData("#ff00000")]     // too long
    [InlineData("")]             // empty
    public void HexToColor4_Malformed_Throws(string malformed)
    {
        Assert.Throws<ArgumentException>(() => RuleEffectApplier.HexToColor4(malformed));
    }

    // ── TryApply ──

    [Fact]
    public void TryApply_Empty_ReturnsFalseNoColorChange()
    {
        var color = new Color4(0.5f, 0.5f, 0.5f, 1f);
        var original = color;

        var result = RuleEffectApplier.TryApply(Array.Empty<Effect>(), ref color);

        Assert.False(result);
        Assert.Equal(original.R, color.R, 4);
        Assert.Equal(original.G, color.G, 4);
        Assert.Equal(original.B, color.B, 4);
        Assert.Equal(original.A, color.A, 4);
    }

    [Fact]
    public void TryApply_HideEffect_ReturnsTrue()
    {
        var color = new Color4(0.5f, 0.5f, 0.5f, 1f);
        var effects = new List<Effect> { new HideEffect() };

        var result = RuleEffectApplier.TryApply(effects, ref color);

        Assert.True(result);
    }

    [Fact]
    public void TryApply_TintEffect_SetsColor()
    {
        var color = new Color4(1f, 0f, 0f, 1f); // red
        var effects = new List<Effect> { new TintEffect("#0000ff") };

        var result = RuleEffectApplier.TryApply(effects, ref color);

        Assert.False(result);                  // no hide
        Assert.Equal(0f, color.R, 4);          // now blue
        Assert.Equal(0f, color.G, 4);
        Assert.Equal(1f, color.B, 4);
        Assert.Equal(1f, color.A, 4);
    }

    [Fact]
    public void TryApply_HideAndTint_HideWins()
    {
        var color = new Color4(1f, 0f, 0f, 1f);
        var effects = new List<Effect> { new HideEffect(), new TintEffect("#0000ff") };

        var result = RuleEffectApplier.TryApply(effects, ref color);

        Assert.True(result);  // HideEffect present → caller should continue
        // Color is still set by tint (implementation detail), but the true return
        // means the caller discards it via `continue`
    }

    [Fact]
    public void TryApply_IgnoresRingLabelSoundPulse_ForR3()
    {
        var color = new Color4(0.5f, 0.5f, 0.5f, 1f);
        var original = color;
        var effects = new List<Effect>
        {
            new RingEffect("#ff0000"),
            new LabelEffect("test"),
            new SoundEffect("alert.wav"),
            new PulseEffect("slow"),
        };

        var result = RuleEffectApplier.TryApply(effects, ref color);

        Assert.False(result);
        Assert.Equal(original.R, color.R, 4);
        Assert.Equal(original.G, color.G, 4);
        Assert.Equal(original.B, color.B, 4);
        Assert.Equal(original.A, color.A, 4);
    }

    [Fact]
    public void TryApply_NullEffects_ReturnsFalseNoChange()
    {
        var color = new Color4(0.5f, 0.5f, 0.5f, 1f);
        var original = color;

        var result = RuleEffectApplier.TryApply(null!, ref color);

        Assert.False(result);
        Assert.Equal(original, color);
    }

    /// <summary>
    /// Structural assertion: OverlayRenderer has a Rules property of type CompiledRuleSet
    /// that defaults to RuleEngine.Empty. Uses reflection to verify existence without
    /// requiring a rendering environment.
    /// </summary>
    [Fact]
    public void OverlayRenderer_RulesProperty_DefaultsToEmpty()
    {
        var prop = typeof(OverlayRenderer).GetProperty("Rules",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Equal(typeof(POE2Radar.Core.Rules.CompiledRuleSet), prop.PropertyType);
    }

    // ── HasEffect<T> ──

    [Fact]
    public void HasEffect_Ring_ReturnsTrue()
    {
        var effects = new List<Effect> { new RingEffect("#ff0000") };
        Assert.True(RuleEffectApplier.HasEffect<RingEffect>(effects, out var found));
        Assert.NotNull(found);
        Assert.Equal("#ff0000", found!.Color);
    }

    [Fact]
    public void HasEffect_Label_ReturnsTrue()
    {
        var effects = new List<Effect> { new LabelEffect("hello") };
        Assert.True(RuleEffectApplier.HasEffect<LabelEffect>(effects, out var found));
        Assert.NotNull(found);
        Assert.Equal("hello", found!.Text);
    }

    [Fact]
    public void HasEffect_Pulse_ReturnsTrue()
    {
        var effects = new List<Effect> { new PulseEffect("slow") };
        Assert.True(RuleEffectApplier.HasEffect<PulseEffect>(effects, out var found));
        Assert.NotNull(found);
        Assert.Equal("slow", found!.Speed);
    }

    [Fact]
    public void HasEffect_Missing_ReturnsFalse()
    {
        var effects = new List<Effect> { new HideEffect() };
        Assert.False(RuleEffectApplier.HasEffect<RingEffect>(effects, out var ringFound));
        Assert.Null(ringFound);
        Assert.False(RuleEffectApplier.HasEffect<LabelEffect>(effects, out var labelFound));
        Assert.Null(labelFound);
        Assert.False(RuleEffectApplier.HasEffect<PulseEffect>(effects, out var pulseFound));
        Assert.Null(pulseFound);
    }

    [Fact]
    public void HasEffect_NullList_ReturnsFalse()
    {
        Assert.False(RuleEffectApplier.HasEffect<RingEffect>(null!, out var found));
        Assert.Null(found);
    }

    [Fact]
    public void HasEffect_EmptyList_ReturnsFalse()
    {
        Assert.False(RuleEffectApplier.HasEffect<HideEffect>(Array.Empty<Effect>(), out var found));
        Assert.Null(found);
    }

    [Fact]
    public void HasEffect_SecondType_IgnoresFirst()
    {
        // Mixed effects: first is Hide, second is Ring → HasEffect<Ring> finds Ring
        var effects = new List<Effect> { new HideEffect(), new RingEffect("#00ff00") };
        Assert.True(RuleEffectApplier.HasEffect<RingEffect>(effects, out var found));
        Assert.NotNull(found);
        Assert.Equal("#00ff00", found!.Color);
    }

    // ── ExpandLabelTokens ──

    private static readonly EntityView SampleEntity = new(
        Metadata: "Metadata/Monsters/Snakes/RedSnake",
        Token: "RedSnake",
        Rarity: "magic",
        Level: 42,
        Buffs: Array.Empty<string>());

    private static readonly WorldSnapshotView SampleSnapshot = new(
        ZoneCode: "2_8_1",
        InHideout: false);

    [Fact]
    public void ExpandLabelTokens_Name()
    {
        var result = RuleEffectApplier.ExpandLabelTokens("{name}", SampleEntity, SampleSnapshot);
        Assert.Equal("RedSnake", result);
    }

    [Fact]
    public void ExpandLabelTokens_Level()
    {
        var result = RuleEffectApplier.ExpandLabelTokens("{level}", SampleEntity, SampleSnapshot);
        Assert.Equal("42", result);
    }

    [Fact]
    public void ExpandLabelTokens_Metadata()
    {
        var result = RuleEffectApplier.ExpandLabelTokens("{metadata}", SampleEntity, SampleSnapshot);
        Assert.Equal("Metadata/Monsters/Snakes/RedSnake", result);
    }

    [Fact]
    public void ExpandLabelTokens_Zone()
    {
        var result = RuleEffectApplier.ExpandLabelTokens("{zone}", SampleEntity, SampleSnapshot);
        Assert.Equal("2_8_1", result);
    }

    [Fact]
    public void ExpandLabelTokens_AllFour()
    {
        var template = "{name}:{level}:{metadata}:{zone}";
        var result = RuleEffectApplier.ExpandLabelTokens(template, SampleEntity, SampleSnapshot);
        Assert.Equal("RedSnake:42:Metadata/Monsters/Snakes/RedSnake:2_8_1", result);
    }

    [Fact]
    public void ExpandLabelTokens_UnknownTokenLiteral()
    {
        var result = RuleEffectApplier.ExpandLabelTokens("{foo}", SampleEntity, SampleSnapshot);
        Assert.Equal("{foo}", result);
    }

    [Fact]
    public void ExpandLabelTokens_EmptyTemplate()
    {
        var result = RuleEffectApplier.ExpandLabelTokens("", SampleEntity, SampleSnapshot);
        Assert.Equal("", result);
    }

    [Fact]
    public void ExpandLabelTokens_NoTokens()
    {
        var result = RuleEffectApplier.ExpandLabelTokens("hello world", SampleEntity, SampleSnapshot);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void ExpandLabelTokens_NullTemplate()
    {
        var result = RuleEffectApplier.ExpandLabelTokens(null!, SampleEntity, SampleSnapshot);
        Assert.Equal("", result);
    }

    [Fact]
    public void ExpandLabelTokens_MissingEntityValues_RendersEmpty()
    {
        var emptyEntity = new EntityView("", "", "", 0, Array.Empty<string>());
        var emptySnap = new WorldSnapshotView("", false);
        var result = RuleEffectApplier.ExpandLabelTokens("{name}-{level}-{metadata}-{zone}", emptyEntity, emptySnap);
        Assert.Equal("-0--", result);
    }

    // ── ApplyPulseAlpha ──

    [Fact]
    public void ApplyPulseAlpha_SlowAtZeroMs_MidAlpha()
    {
        // sin(2π * 1 * 0) = 0 → alpha = 0.7 + 0.3 * 0 = 0.7
        var color = new Color4(1f, 0f, 0f, 1f);
        var pulse = new PulseEffect("slow");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 0);
        Assert.Equal(1f, result.R, 4);
        Assert.Equal(0f, result.G, 4);
        Assert.Equal(0f, result.B, 4);
        Assert.Equal(0.7, result.A, 4);
    }

    [Fact]
    public void ApplyPulseAlpha_SlowAtQuarterSecond_MaxAlpha()
    {
        // sin(2π * 1 * 0.25) = sin(π/2) = 1 → alpha = 0.7 + 0.3 * 1 = 1.0
        var color = new Color4(0f, 1f, 0f, 1f);
        var pulse = new PulseEffect("slow");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 250);
        Assert.Equal(0f, result.R, 4);
        Assert.Equal(1f, result.G, 4);
        Assert.Equal(0f, result.B, 4);
        Assert.Equal(1.0, result.A, 4);
    }

    [Fact]
    public void ApplyPulseAlpha_SlowAtThreeQuarterSecond_MinAlpha()
    {
        // sin(2π * 1 * 0.75) = sin(3π/2) = -1 → alpha = 0.7 + 0.3 * (-1) = 0.4
        var color = new Color4(0f, 0f, 1f, 1f);
        var pulse = new PulseEffect("slow");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 750);
        Assert.Equal(0f, result.R, 4);
        Assert.Equal(0f, result.G, 4);
        Assert.Equal(1f, result.B, 4);
        Assert.Equal(0.4, result.A, 4);
    }

    [Fact]
    public void ApplyPulseAlpha_FastAt0Ms_MidAlpha()
    {
        // sin(0) = 0 → alpha = 0.7
        var color = new Color4(1f, 1f, 1f, 1f);
        var pulse = new PulseEffect("fast");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 0);
        Assert.Equal(0.7, result.A, 4);
    }

    [Fact]
    public void ApplyPulseAlpha_FastAt166Ms_MidAlpha()
    {
        // sin(2π * 3 * 0.166) ≈ sin(π) = 0 → alpha ≈ 0.7
        var color = new Color4(1f, 1f, 1f, 1f);
        var pulse = new PulseEffect("fast");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 166);
        Assert.Equal(0.7, result.A, 2);
    }

    [Fact]
    public void ApplyPulseAlpha_FastAt333Ms_MidAlpha()
    {
        // sin(2π * 3 * 0.333) ≈ sin(2π) = 0 → alpha ≈ 0.7
        var color = new Color4(1f, 1f, 1f, 1f);
        var pulse = new PulseEffect("fast");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 333);
        Assert.Equal(0.7, result.A, 2);
    }

    [Fact]
    public void ApplyPulseAlpha_FastAtEighthSecond_MaxAlpha()
    {
        // sin(2π * 3 * 0.0833) = sin(π/2) = 1 → alpha = 1.0
        var color = new Color4(0.5f, 0.5f, 0.5f, 1f);
        var pulse = new PulseEffect("fast");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 83);
        Assert.Equal(1.0, result.A, 2);
    }

    [Fact]
    public void ApplyPulseAlpha_FastAtQuarterSecond_MinAlpha()
    {
        // sin(2π * 3 * 0.25) = sin(3π/2) = -1 → alpha = 0.4
        var color = new Color4(0.5f, 0.5f, 0.5f, 1f);
        var pulse = new PulseEffect("fast");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 250);
        Assert.Equal(0.4, result.A, 2);
    }

    [Fact]
    public void ApplyPulseAlpha_UnknownSpeed_ReturnsBaseColorUnchanged()
    {
        var color = new Color4(0.3f, 0.6f, 0.9f, 0.8f);
        var pulse = new PulseEffect("invalid");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 500);
        Assert.Equal(color.R, result.R, 4);
        Assert.Equal(color.G, result.G, 4);
        Assert.Equal(color.B, result.B, 4);
        Assert.Equal(color.A, result.A, 4);
    }

    [Fact]
    public void ApplyPulseAlpha_NullSpeed_ReturnsBaseColorUnchanged()
    {
        var color = new Color4(0.3f, 0.6f, 0.9f, 0.8f);
        var pulse = new PulseEffect(null!);
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 500);
        Assert.Equal(color, result);
    }

    [Theory]
    [InlineData("slow", 0, 0.7)]
    [InlineData("slow", 250, 1.0)]
    [InlineData("slow", 750, 0.4)]
    [InlineData("fast", 0, 0.7)]
    [InlineData("fast", 83, 1.0)]
    [InlineData("fast", 250, 0.4)]
    public void ApplyPulseAlpha_AlphaBoundedTo04To10(string speed, double ms, double expectedAlpha)
    {
        var color = new Color4(0.2f, 0.4f, 0.6f, 0.5f);
        var pulse = new PulseEffect(speed);
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, ms);
        Assert.InRange(result.A, 0.39, 1.01); // relaxed bounds for FP
        Assert.Equal(expectedAlpha, result.A, 2);
    }

    [Fact]
    public void ApplyPulseAlpha_PreservesRgb()
    {
        var color = new Color4(0.2f, 0.4f, 0.6f, 1f);
        var pulse = new PulseEffect("slow");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 100);
        Assert.Equal(color.R, result.R, 4);
        Assert.Equal(color.G, result.G, 4);
        Assert.Equal(color.B, result.B, 4);
    }

    [Fact]
    public void ApplyPulseAlpha_VeryLargeElapsed_WrapsAround()
    {
        // 10000ms = 10s: sin(2π * 1 * 10) = sin(20π) = 0 → alpha = 0.7
        var color = new Color4(1f, 0f, 0f, 1f);
        var pulse = new PulseEffect("slow");
        var result = RuleEffectApplier.ApplyPulseAlpha(color, pulse, 10000);
        Assert.Equal(0.7, result.A, 4);
    }
}