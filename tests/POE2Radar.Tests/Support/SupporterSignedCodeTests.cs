using POE2Radar.Core.Support;
using Xunit;

namespace POE2Radar.Tests.Support;

/// <summary>
/// Companion — v0.28: locks the Ed25519 signed-code verify path. The sample code below was minted
/// against the shipped public key using the matching private key held only by the Cloudflare Worker.
/// If the shipped public key rotates (i.e. LO regenerates the Worker's private key), regenerate the
/// sample code with `python -c "..."` following the private key rotation and update this test.
/// </summary>
public class SupporterSignedCodeTests
{
    // A signed code minted against the shipped public key (99392f...). Payload:
    //   {"email":"donor@example.com","issued":1783717239,"tier":"gold"}
    private const string ValidCode =
        "poe2gps.eyJlbWFpbCI6ImRvbm9yQGV4YW1wbGUuY29tIiwiaXNzdWVkIjoxNzgzNzE3MjM5LCJ0aWVyIjoiZ29sZCJ9.2hMuGwwmbqmdZRxGTjFLBa0gGfOG9pkaGKJM_rguWvBq0lfgVAZJ7jQfZU8wtFgM0nkiZm9uzXvaYnpheU95Cg";

    [Fact]
    public void Verifies_valid_signed_code_and_extracts_claims()
    {
        var claims = SupporterSignedCode.TryVerify(ValidCode);
        Assert.NotNull(claims);
        Assert.Equal("donor@example.com", claims!.Email);
        Assert.Equal("gold", claims.Tier);
        Assert.Equal(1783717239L, claims.Issued);
    }

    [Fact]
    public void Rejects_null_empty_or_wrong_prefix()
    {
        Assert.Null(SupporterSignedCode.TryVerify(null));
        Assert.Null(SupporterSignedCode.TryVerify(""));
        Assert.Null(SupporterSignedCode.TryVerify("   "));
        Assert.Null(SupporterSignedCode.TryVerify("not-a-code"));
        Assert.Null(SupporterSignedCode.TryVerify("prefix.eyJ0Ijoiei"));
    }

    [Fact]
    public void Rejects_tampered_payload()
    {
        // Flip a single character INSIDE the base64-encoded payload segment. That changes the
        // decoded payload bytes, so the shipped signature no longer matches — verify must fail.
        // (Note: the raw JSON strings like "gold" / "donor@example.com" are NOT visible in the code
        // string — they're base64-encoded — so string.Replace("gold", ...) is a no-op tamper.)
        var payloadStart = "poe2gps.".Length;
        var dotIdx = ValidCode.IndexOf('.', payloadStart);
        // Flip the 3rd character of the payload segment to a different but still-valid base64 char.
        var target = payloadStart + 2;
        var newChar = ValidCode[target] == 'A' ? 'B' : 'A';
        var tampered = ValidCode.Substring(0, target) + newChar + ValidCode.Substring(target + 1);
        Assert.NotEqual(ValidCode, tampered);   // guard the guard — make sure we actually changed something
        Assert.Null(SupporterSignedCode.TryVerify(tampered));
    }

    [Fact]
    public void Rejects_tampered_signature()
    {
        // Corrupt the signature suffix by chopping the last 4 chars.
        var tampered = ValidCode.Substring(0, ValidCode.Length - 4) + "AAAA";
        Assert.Null(SupporterSignedCode.TryVerify(tampered));
    }

    [Fact]
    public void Rejects_missing_signature_segment()
    {
        var noSig = ValidCode.Substring(0, ValidCode.IndexOf('.', "poe2gps.".Length));
        Assert.Null(SupporterSignedCode.TryVerify(noSig));
    }

    [Fact]
    public void IsValid_convenience_matches_TryVerify()
    {
        Assert.True(SupporterSignedCode.IsValid(ValidCode));
        Assert.False(SupporterSignedCode.IsValid("garbage"));
    }

    [Fact]
    public void SupporterCodeValidator_accepts_signed_code()
    {
        // The main validator now accepts both hash-based (v0.27) and signed (v0.28) codes.
        Assert.True(SupporterCodeValidator.IsSupporter(ValidCode));
    }

    [Fact]
    public void SupporterCodeValidator_still_accepts_legacy_hash_code()
    {
        // v0.27.1 seed code should keep working — backwards-compat is load-bearing here.
        Assert.True(SupporterCodeValidator.IsSupporter("POE2GPS-FIRST-COFFEE-2026"));
    }
}
