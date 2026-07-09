using System.Text.RegularExpressions;
using POE2Radar.Core.Campaign.Probe;

namespace POE2Radar.Tests.Campaign.Probe;

// v0.22 campaign-probe — spec §2 install identity + §3 *_hash envelope fields.
// Anonymization primitives are purely computational; no I/O, no PII on the wire.
public class AnonymizationHelpersTests
{
    // ── HashText16 ──────────────────────────────────────────────────────

    [Fact]
    public void HashText16_IsDeterministic()
    {
        Assert.Equal(
            AnonymizationHelpers.HashText16("Alira"),
            AnonymizationHelpers.HashText16("Alira"));
    }

    [Fact]
    public void HashText16_ReturnsSixteenLowercaseHex()
    {
        var h = AnonymizationHelpers.HashText16("The Karui Way");
        Assert.Equal(16, h.Length);
        Assert.Matches(new Regex("^[0-9a-f]{16}$"), h);
    }

    [Fact]
    public void HashText16_DifferentInputsDiffer()
    {
        Assert.NotEqual(
            AnonymizationHelpers.HashText16("Alira"),
            AnonymizationHelpers.HashText16("Kraityn"));
    }

    [Fact]
    public void HashText16_NullNormalizesToEmpty()
    {
        Assert.Equal(
            AnonymizationHelpers.HashText16(""),
            AnonymizationHelpers.HashText16(null!));
    }

    [Fact]
    public void HashText16_EmptySentinelIsStable()
    {
        // First 8 bytes of SHA-256("") in lowercase hex. Grep-able in trace JSONL
        // if this ever leaks through to a debug post-mortem.
        Assert.Equal("e3b0c44298fc1c14", AnonymizationHelpers.HashText16(""));
    }

    // ── NewInstallUuid ─────────────────────────────────────────────────

    [Fact]
    public void NewInstallUuid_IsValidGuidD()
    {
        var s = AnonymizationHelpers.NewInstallUuid();
        Assert.True(Guid.TryParseExact(s, "D", out _));
    }

    [Fact]
    public void NewInstallUuid_IsLowercase()
    {
        var s = AnonymizationHelpers.NewInstallUuid();
        Assert.Equal(s.ToLowerInvariant(), s);
    }

    [Fact]
    public void NewInstallUuid_HasCanonicalDashLayout()
    {
        // "D" format is 36 chars, dashes at positions 8/13/18/23.
        var s = AnonymizationHelpers.NewInstallUuid();
        Assert.Equal(36, s.Length);
        Assert.Equal('-', s[8]);
        Assert.Equal('-', s[13]);
        Assert.Equal('-', s[18]);
        Assert.Equal('-', s[23]);
    }

    [Fact]
    public void NewInstallUuid_HasEntropy()
    {
        var set = new HashSet<string>();
        for (int i = 0; i < 100; i++)
            set.Add(AnonymizationHelpers.NewInstallUuid());
        Assert.Equal(100, set.Count);
    }

    // ── GetOrInitInstallUuid ───────────────────────────────────────────

    [Fact]
    public void GetOrInitInstallUuid_ReturnsExistingWithoutCallingPersist()
    {
        const string existing = "abcd1234-abcd-1234-abcd-1234abcd1234";
        var persistCalls = 0;
        var got = AnonymizationHelpers.GetOrInitInstallUuid(
            read: () => existing,
            persist: _ => persistCalls++);

        Assert.Equal(existing, got);
        Assert.Equal(0, persistCalls);
    }

    [Fact]
    public void GetOrInitInstallUuid_EmptyStringMintsAndPersistsExactlyOnce()
    {
        string? persisted = null;
        var persistCalls = 0;
        var got = AnonymizationHelpers.GetOrInitInstallUuid(
            read: () => "",
            persist: v => { persisted = v; persistCalls++; });

        Assert.True(Guid.TryParseExact(got, "D", out _));
        Assert.Equal(got, persisted);
        Assert.Equal(1, persistCalls);
    }

    [Fact]
    public void GetOrInitInstallUuid_NullMintsAndPersistsExactlyOnce()
    {
        string? persisted = null;
        var persistCalls = 0;
        var got = AnonymizationHelpers.GetOrInitInstallUuid(
            read: () => null,
            persist: v => { persisted = v; persistCalls++; });

        Assert.True(Guid.TryParseExact(got, "D", out _));
        Assert.Equal(got, persisted);
        Assert.Equal(1, persistCalls);
    }

    [Fact]
    public void GetOrInitInstallUuid_WhitespaceReadTriggersMintPath()
    {
        string? persisted = null;
        var persistCalls = 0;
        var got = AnonymizationHelpers.GetOrInitInstallUuid(
            read: () => "   \t\n",
            persist: v => { persisted = v; persistCalls++; });

        Assert.True(Guid.TryParseExact(got, "D", out _));
        Assert.Equal(got, persisted);
        Assert.Equal(1, persistCalls);
    }
}
