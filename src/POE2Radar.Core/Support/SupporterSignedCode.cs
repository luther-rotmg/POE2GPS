using System.Reflection;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace POE2Radar.Core.Support;

/// <summary>
/// Companion — v0.28 (LO ask): Ed25519 signed supporter codes. The Cloudflare Worker mints a
/// compact <c>poe2gps.&lt;payload&gt;.&lt;signature&gt;</c> token on every Ko-fi donation and emails
/// it to the donor; POE2GPS ships only the Ed25519 PUBLIC KEY and verifies offline. The private key
/// never leaves the Worker, so anyone extracting the exe cannot mint new codes — cosmetic gating with
/// actual crypto behind it.
///
/// Code format: <c>poe2gps.&lt;base64url-payload&gt;.&lt;base64url-signature&gt;</c>
///
/// Payload is JSON with stable key ordering (Worker uses sorted keys so the signed bytes match exactly):
/// <code>{"email":"donor@example.com","issued":1783717239,"tier":"gold"}</code>
///
/// Verify: base64url-decode signature + payload, then Ed25519.Verify(publicKey, payloadBytes, sig).
/// Backwards-compat: the legacy hash-based codes (v0.27.0) still validate through <see cref="SupporterCodeValidator"/>.
/// </summary>
public static class SupporterSignedCode
{
    /// <summary>All parsed fields from a valid signed code. Null when the input doesn't parse or the
    /// signature doesn't verify. Callers can inspect <see cref="Email"/> / <see cref="Tier"/> for
    /// tier-gated UI (e.g. gold tier reveals extra dashboard palettes).</summary>
    public sealed record Claims(string Email, string Tier, long Issued);

    private const string CodePrefix = "poe2gps.";

    private static readonly Lazy<byte[]> _publicKey = new(LoadPublicKey,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Verify a signed code offline against the shipped public key. Returns non-null
    /// <see cref="Claims"/> on valid signature; null on any failure (malformed, wrong prefix,
    /// bad base64, bad JSON, bad signature). Never throws.</summary>
    public static Claims? TryVerify(string? code)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            code = code.Trim();
            if (!code.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase)) return null;
            var rest = code.Substring(CodePrefix.Length);
            var dotIndex = rest.IndexOf('.');
            if (dotIndex <= 0 || dotIndex >= rest.Length - 1) return null;
            var payloadB64 = rest.Substring(0, dotIndex);
            var signatureB64 = rest.Substring(dotIndex + 1);

            var payloadBytes = Base64UrlDecode(payloadB64);
            var signatureBytes = Base64UrlDecode(signatureB64);
            if (payloadBytes == null || signatureBytes == null) return null;
            if (signatureBytes.Length != 64) return null;   // Ed25519 signatures are always 64 bytes

            // Verify with the shipped public key.
            var pub = _publicKey.Value;
            if (pub.Length != 32) return null;
            var signer = new Ed25519Signer();
            signer.Init(forSigning: false, new Ed25519PublicKeyParameters(pub, 0));
            signer.BlockUpdate(payloadBytes, 0, payloadBytes.Length);
            if (!signer.VerifySignature(signatureBytes)) return null;

            // Decode the payload — Worker uses sorted-key JSON so the payload bytes match verbatim.
            using var doc = JsonDocument.Parse(payloadBytes);
            var root = doc.RootElement;
            var email = root.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() ?? "" : "";
            var tier  = root.TryGetProperty("tier",  out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "" : "";
            var issued = root.TryGetProperty("issued", out var i) && i.ValueKind == JsonValueKind.Number
                ? i.GetInt64() : 0L;
            if (email.Length == 0 || tier.Length == 0) return null;
            return new Claims(email, tier, issued);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the code parses + verifies. Convenience for the settings gate.</summary>
    public static bool IsValid(string? code) => TryVerify(code) != null;

    // ── loading ─────────────────────────────────────────────────────────────────────────────────────

    private static byte[] LoadPublicKey()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("supporter_public_key"));
            if (name == null) return Array.Empty<byte>();
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return Array.Empty<byte>();
            using var reader = new StreamReader(stream);
            var hex = reader.ReadToEnd().Trim();
            return HexToBytes(hex);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0) return Array.Empty<byte>();
        var buf = new byte[hex.Length / 2];
        for (int i = 0; i < buf.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var b))
                return Array.Empty<byte>();
            buf[i] = b;
        }
        return buf;
    }

    private static byte[]? Base64UrlDecode(string input)
    {
        try
        {
            var s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "=";  break;
                case 1: return null;    // invalid length
            }
            return Convert.FromBase64String(s);
        }
        catch
        {
            return null;
        }
    }
}
