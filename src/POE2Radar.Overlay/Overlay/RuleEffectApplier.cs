using System;
using System.Collections.Generic;
using System.Globalization;
using POE2Radar.Core.Rules;
using Vortice.Mathematics;

namespace POE2Radar.Overlay.Overlay;

/// <summary>
/// Static helper for applying rules-engine effects (hide, tint, ring, label, pulse, etc.)
/// in the renderer entity loop.
/// Keeps the OverlayRenderer edit surface small — one call site instead of inline effect logic.
/// R3 ships hide + tint; R3.1 ships ring + label + pulse (sound deferred).
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
                // RingEffect, LabelEffect, SoundEffect, PulseEffect — handled by R3.1 helpers
            }
        }

        return hasHide;
    }

    /// <summary>
    /// Find the first effect of type <typeparamref name="T"/> in the effects list.
    /// Returns <c>true</c> and the effect via <paramref name="found"/> when found;
    /// <c>false</c> and <c>null</c> when not found.
    /// </summary>
    public static bool HasEffect<T>(IReadOnlyList<Effect> effects, out T? found) where T : Effect
    {
        if (effects is not null)
        {
            for (int i = 0; i < effects.Count; i++)
            {
                if (effects[i] is T match)
                {
                    found = match;
                    return true;
                }
            }
        }
        found = null;
        return false;
    }

    /// <summary>
    /// Expand known tokens in a label template using entity and snapshot values.
    /// Tokens: <c>{name}</c> → entity Token (last path segment), <c>{level}</c> → entity Level,
    /// <c>{metadata}</c> → entity Metadata, <c>{zone}</c> → snapshot ZoneCode.
    /// Missing/null values render as empty string. Unknown tokens (e.g. <c>{foo}</c>) are left as literal.
    /// Never throws.
    /// </summary>
    public static string ExpandLabelTokens(string template, EntityView e, WorldSnapshotView s)
    {
        if (string.IsNullOrEmpty(template))
            return template ?? "";

        return template
            .Replace("{name}", e.Token ?? "")
            .Replace("{level}", e.Level.ToString())
            .Replace("{metadata}", e.Metadata ?? "")
            .Replace("{zone}", s.ZoneCode ?? "");
    }

    /// <summary>
    /// Modulate the alpha channel of <paramref name="baseColor"/> using a sine wave.
    /// <paramref name="pulse"/>.<see cref="PulseEffect.Speed"/> determines the frequency:
    /// <c>"slow"</c> = 1 Hz, <c>"fast"</c> = 3 Hz. Unknown speed values return the base color unchanged.
    /// The sine output (range [-1, 1]) is linearly mapped to alpha range [0.4, 1.0].
    /// Formula: alpha = 0.7 + 0.3 × sin(2π × hz × elapsedMs / 1000).
    /// </summary>
    public static Color4 ApplyPulseAlpha(Color4 baseColor, PulseEffect pulse, double elapsedMs)
    {
        double hz = pulse.Speed switch
        {
            "slow" => 1.0,
            "fast" => 3.0,
            _ => 0.0, // unknown speed → no modulation (return baseColor unchanged below)
        };

        if (hz <= 0.0)
            return baseColor;

        double elapsedSec = elapsedMs / 1000.0;
        double sinVal = Math.Sin(2.0 * Math.PI * hz * elapsedSec);
        // Map sinVal [-1, 1] to alpha [0.4, 1.0]
        double alpha = 0.7 + 0.3 * sinVal;
        alpha = Math.Clamp(alpha, 0.4, 1.0);
        return new Color4(baseColor.R, baseColor.G, baseColor.B, (float)alpha);
    }
}