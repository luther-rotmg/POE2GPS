namespace POE2Radar.Core.Support;

/// <summary>
/// Support — v0.41 (LO ask): cached supporter-gate wrapper around
/// <see cref="SupporterCodeValidator.IsSupporter"/>. Avoids recomputing the SHA-256 hash or Ed25519
/// signature verify on every per-frame read — the overlay's render loop may check
/// <see cref="IsSupporter"/> dozens of times a second without blocking on hash work. The cache
/// is a simple volatile bool; call <see cref="RefreshFromSettings"/> after the user saves a new
/// code or on app start. Defaults to <c>false</c> until the first explicit refresh.
///
/// Fail-closed: an invalid / null / empty code string sets the cache to <c>false</c>.
/// Thread-safe for concurrent read + infrequent write (single-bool semantics).
/// </summary>
public static class SupporterGate
{
    // Volatile bool provides single-read atomicity — the read side never locks.
    private static volatile bool _cached;

    /// <summary>
    /// Returns the cached supporter status. <c>false</c> until
    /// <see cref="RefreshFromSettings"/> is called at least once.
    /// </summary>
    public static bool IsSupporter => _cached;

    /// <summary>
    /// Recompute the supporter status from a supporter code string and update the cache.
    /// Idempotent and safe to call from any thread. Null or empty string → cache set to
    /// <c>false</c> (fail-closed).
    /// </summary>
    public static void RefreshFromSettings(string? supporterCode)
    {
        _cached = !string.IsNullOrWhiteSpace(supporterCode)
                   && SupporterCodeValidator.IsSupporter(supporterCode);
    }

    /// <summary>Test-only reset — returns the gate to its default (false) state.</summary>
    internal static void ResetForTesting() => _cached = false;
}