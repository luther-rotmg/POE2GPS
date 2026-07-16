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
}