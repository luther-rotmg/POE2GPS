using System.Linq;
using System.Reflection;
using System.Text.Json;
using POE2Radar.Core.Campaign;
using Xunit;

namespace POE2Radar.Tests;

public class CampaignGuideDataEmbedTests
{
    // Grab the Core assembly by locating a known type in it (CampaignGps already ships).
    private static Assembly CoreAsm =>
        typeof(POE2Radar.Core.Campaign.CampaignGps).Assembly;

    [Theory]
    [InlineData("route.json",            500_000)]
    [InlineData("overrides.json",         60_000)]
    [InlineData("area-objectives.json",    5_000)]
    [InlineData("area-transitions.json",  10_000)]
    [InlineData("area-targets.json",       2_000)]
    [InlineData("xp_curve.json",             500)]
    public void Embedded_resource_discoverable_and_valid_json(string file, int minBytes)
    {
        var expected = $"POE2Radar.Core.Campaign.Guide.Data.poe2.{file}";
        var name = CoreAsm.GetManifestResourceNames()
            .SingleOrDefault(n => n == expected);
        Assert.NotNull(name);

        using var s = CoreAsm.GetManifestResourceStream(name)!;
        Assert.NotNull(s);
        Assert.True(s.Length >= minBytes,
            $"{file} is {s.Length} bytes — expected >= {minBytes}. Truncated download?");

        // Round-trip through JsonDocument to prove the payload is valid JSON.
        using var doc = JsonDocument.Parse(s);
        Assert.NotNull(doc.RootElement.ToString());
    }

    [Fact]
    public void All_six_embedded_resources_present()
    {
        var expected = new[]
        {
            "POE2Radar.Core.Campaign.Guide.Data.poe2.route.json",
            "POE2Radar.Core.Campaign.Guide.Data.poe2.overrides.json",
            "POE2Radar.Core.Campaign.Guide.Data.poe2.area-objectives.json",
            "POE2Radar.Core.Campaign.Guide.Data.poe2.area-transitions.json",
            "POE2Radar.Core.Campaign.Guide.Data.poe2.area-targets.json",
            "POE2Radar.Core.Campaign.Guide.Data.poe2.xp_curve.json",
        };
        var present = CoreAsm.GetManifestResourceNames().ToHashSet();
        foreach (var e in expected)
            Assert.Contains(e, present);
    }
}
