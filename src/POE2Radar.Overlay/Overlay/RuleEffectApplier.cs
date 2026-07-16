using System;
using System.Collections.Generic;
using System.Globalization;
using POE2Radar.Core.Rules;
using Vortice.Mathematics;

namespace POE2Radar.Overlay.Overlay;

/// <summary>
/// Static helper for applying rules-engine effects (hide, tint, etc.) in the renderer entity loop.
/// Keeps the OverlayRenderer edit surface small — one call site instead of inline effect logic.
/// R3 ships hide + tint; ring/label/sound/pulse are reserved for R3.1.
/// </summary>
public static class RuleEffectApplier
{
    /// <summary>
    /// Parse a "#rrggbb" hex color string (case-insensitive) into a <see cref="Color4"/>
    /// with alpha = 1. Throws <see cref="ArgumentException"/> on malformed input.
    /// </summary>
    public static Color4 HexToColor4(string hex)
    {
        if (string.IsNullOrEmpty(hex))
            throw new ArgumentException("Hex color must not be null or empty.", nameof(hex));
        if (hex.Length != 7 || hex[0] != '#')
            throw new ArgumentException("Hex color must be in #rrggbb format.", nameof(hex));

        try
        {
            var r = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var g = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var b = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Color4(r / 255f, g / 255f, b / 255f, 1f);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"Invalid hex color '{hex}'. Must be #rrggbb with valid hex digits.", nameof(hex), ex);
        }
        catch (OverflowException ex)
        {
            throw new ArgumentException($"Invalid hex color '{hex}'. Hex digits out of range.", nameof(hex), ex);
        }
    }

    /// <summary>
    /// Apply a list of effects to a brush color. Returns <c>true</c> if a <see cref="HideEffect"/>
    /// is present (caller should skip the entity). When a <see cref="TintEffect"/> is present,
    /// sets <paramref name="color"/> to the parsed tint (first Tint wins if multiple).
    /// All other effect kinds (RingEffect, LabelEffect, SoundEffect, PulseEffect) are silently
    /// ignored — reserved for R3.1.
    /// </summary>
    public static bool TryApply(IReadOnlyList<Effect> effects, ref Color4 color)
    {
        if (effects is null || effects.Count == 0)
            return false;

        bool hasHide = false;
        bool tintApplied = false;

        for (int i = 0; i < effects.Count; i++)
        {
            var fx = effects[i];
            switch (fx)
            {
                case HideEffect:
                    hasHide = true;
                    break;
                case TintEffect tint when !tintApplied:
                    color = HexToColor4(tint.Color);
                    tintApplied = true;
                    break;
                // RingEffect, LabelEffect, SoundEffect, PulseEffect — reserved for R3.1
            }
        }

        return hasHide;
    }
}