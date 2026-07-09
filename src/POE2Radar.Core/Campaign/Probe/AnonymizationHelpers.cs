// v0.22 campaign-probe — spec §2 (install identity) + §3 (event schema *_hash fields).
// Purely computational anonymization primitives. Zero PII: text values are collapsed to a
// stable 16-char sha256 prefix before they hit the JSONL sink; install identity is a
// CSPRNG-drawn UUID minted once and persisted by the caller. The only side effect on the
// whole surface is the settings-persistence callback that GetOrInitInstallUuid fires
// exactly once when it mints a fresh UUID. No file I/O, no logging, no exceptions
// carrying original text.
using System.Security.Cryptography;
using System.Text;

namespace POE2Radar.Core.Campaign.Probe;

/// <summary>
/// Anonymization primitives for the Campaign Probe (spec §2, §3). Consumed by
/// PROBE-RECORD (envelope <c>*_hash</c> fields), PROBE-WRITER (per-boot <c>boot_id</c>),
/// PROBE-CORE (hashes at record construction), and PROBE-SETTINGS (initial
/// <c>ProbeInstallId</c> + reset-session button).
/// </summary>
public static class AnonymizationHelpers
{
    /// <summary>
    /// Deterministic 16-char lowercase-hex prefix of <c>SHA-256(UTF-8(input))</c>.
    /// Null and empty inputs collapse to the same sentinel hash so "field was missing"
    /// and "field was empty" are indistinguishable on the wire — no side-channel on
    /// absence. Used for every <c>*_hash</c> field in the event schema (spec §3):
    /// npc_name_hash, dialogue_text_hash, option_text_hash, reward_display_name_hash,
    /// node_display_name_hash. 64 bits of prefix is ample for anonymization; no
    /// collision handling required.
    /// </summary>
    public static string HashText16(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
        var digest = SHA256.HashData(bytes);
        // First 8 bytes → 16 hex chars. StringBuilder(16) sizes the backing array
        // exactly, so no growth-realloc on the hot path (PROBE-CORE hashes several
        // strings per emitted event on npc_dialogue_started / quest_reward_selected).
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++) sb.Append(digest[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    /// Fresh install UUID (<see cref="Guid"/> v4-shape, "D" format, lowercase — 36 chars
    /// with dashes at positions 8/13/18/23). <c>Guid.NewGuid</c> is a CSPRNG draw on
    /// .NET 10 — no correlation to machine identity. Called once per install by
    /// <see cref="GetOrInitInstallUuid"/> and once per boot by PROBE-WRITER for the
    /// per-boot <c>boot_id</c>.
    /// </summary>
    public static string NewInstallUuid()
        => Guid.NewGuid().ToString("D").ToLowerInvariant();

    /// <summary>
    /// Returns the persisted install UUID if <paramref name="read"/> yields a value that
    /// is not null, empty, or whitespace-only; otherwise mints one via
    /// <see cref="NewInstallUuid"/>, hands it to <paramref name="persist"/> so the caller
    /// can save it, then returns the new value. Delegate-injected because
    /// POE2Radar.Core does not reference POE2Radar.Overlay (where <c>RadarSettings</c>
    /// lives) — the PROBE-SETTINGS task wires the actual accessors at the call site:
    /// <code>
    /// AnonymizationHelpers.GetOrInitInstallUuid(
    ///     read:    () =&gt; settings.ProbeInstallId,
    ///     persist: id =&gt; { settings.ProbeInstallId = id; RadarSettings.Save(path, settings); });
    /// </code>
    /// The <paramref name="persist"/> callback fires at most once per call — never on
    /// the passthrough branch, exactly once on the mint branch.
    /// </summary>
    public static string GetOrInitInstallUuid(Func<string?> read, Action<string> persist)
    {
        var existing = read();
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        var fresh = NewInstallUuid();
        persist(fresh);
        return fresh;
    }
}
