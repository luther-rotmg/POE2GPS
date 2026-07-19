namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// Represents a single memory read attempt for a probe — the result of reading an offset from a
/// target address, with a domain-specific plausibility gate. Consumed by offset health channels
/// (see <see cref="Health.OffsetHealthChannels"/>) and surfaced via the probe HTTP endpoints.
/// </summary>
/// <typeparam name="T">The decoded value type (e.g. <c>int</c>, <c>long</c>, <c>string</c>).</typeparam>
/// <param name="OffsetHex">The offset that was read, formatted as uppercase hex with <c>"0x"</c> prefix
/// and no leading zeros in the numeric part, e.g. <c>"0x1B0"</c> not <c>"0x001B0"</c>.</param>
/// <param name="TargetAddr">The full read address (base + offset), as a hex string, e.g. <c>"0x7ffe1234"</c>.</param>
/// <param name="Value">The decoded value, or <c>default(T)</c> when the read failed or was skipped.</param>
/// <param name="ReadFailReason">Non-null when the read could not be performed, with a short explanation
/// (e.g. <c>"target not user-mode"</c>, <c>"signature-fail"</c>). <c>null</c> on success.</param>
/// <param name="PassesSignature"><c>true</c> iff the decoded value passes the domain-specific plausibility
/// gate for this family (e.g. EntityHP ∈ [0, 1e7], GridPos ∈ [-1e5, 1e5], etc.). Must be <c>false</c>
/// when <c>ReadFailReason</c> is non-null.</param>
public readonly record struct ProbeSample<T>(
    string OffsetHex,
    string TargetAddr,
    T? Value,
    string? ReadFailReason,
    bool PassesSignature);