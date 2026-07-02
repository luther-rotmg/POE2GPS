using POE2Radar.Core.Update;

public class AutoUpdatePolicyTests
{
    [Theory]
    [InlineData("0.19.0", "0.19.1", true)]
    [InlineData("0.19.1", "0.19.1", false)]
    [InlineData("0.20.0", "0.19.9", false)]
    [InlineData("v0.19.0", "v0.20.0", true)]     // tolerant of a leading v on either side
    [InlineData("0.19.0", "garbage", false)]     // unparseable latest -> not newer
    public void IsNewer_compares_semver(string cur, string latest, bool expected)
        => Assert.Equal(expected, AutoUpdatePolicy.IsNewer(cur, latest));

    [Fact]
    public void AssetName_and_urls_keep_the_v_prefix()
    {
        Assert.Equal("POE2GPS-v0.20.0-win-x64.zip", AutoUpdatePolicy.AssetName("v0.20.0"));
        Assert.Equal("POE2GPS-v0.20.0-sha256.txt", AutoUpdatePolicy.ChecksumAssetName("v0.20.0"));
        Assert.Equal("https://github.com/luther-rotmg/POE2GPS/releases/download/v0.20.0/POE2GPS-v0.20.0-win-x64.zip",
                     AutoUpdatePolicy.ZipUrl("luther-rotmg/POE2GPS", "v0.20.0"));
        Assert.Equal("https://github.com/luther-rotmg/POE2GPS/releases/download/v0.20.0/POE2GPS-v0.20.0-sha256.txt",
                     AutoUpdatePolicy.ChecksumUrl("luther-rotmg/POE2GPS", "v0.20.0"));
    }

    [Fact]
    public void SelectAsset_matches_by_name_case_insensitive()
    {
        var assets = new[] { ("readme.md","u0"), ("POE2GPS-V0.20.0-WIN-X64.ZIP","u1"), ("other.zip","u2") };
        Assert.Equal("u1", AutoUpdatePolicy.SelectAsset(assets, "v0.20.0"));
        Assert.Null(AutoUpdatePolicy.SelectAsset(assets, "v0.99.0"));
    }

    [Fact]
    public void ShouldAttempt_blocks_only_after_maxFailures_on_same_target()
    {
        Assert.True(AutoUpdatePolicy.ShouldAttempt(null, "v0.20.0"));
        Assert.True(AutoUpdatePolicy.ShouldAttempt(new("v0.20.0", 1), "v0.20.0"));
        Assert.False(AutoUpdatePolicy.ShouldAttempt(new("v0.20.0", 2), "v0.20.0"));
        Assert.True(AutoUpdatePolicy.ShouldAttempt(new("v0.20.0", 2), "v0.21.0")); // new target resets
    }

    [Fact]
    public void ExpectedSha_parses_sha256sum_lines()
    {
        var txt = "abc123  POE2GPS-v0.20.0-win-x64.zip\ndef456  other.txt\n";
        Assert.Equal("abc123", AutoUpdatePolicy.ExpectedSha(txt, "POE2GPS-v0.20.0-win-x64.zip"));
        Assert.Null(AutoUpdatePolicy.ExpectedSha(txt, "missing.zip"));
    }
}
