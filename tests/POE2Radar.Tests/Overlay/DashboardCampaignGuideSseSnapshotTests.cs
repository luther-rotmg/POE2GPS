using System.IO;
using System.Text.Json;

namespace POE2Radar.Tests.Overlay;

// EC2-UI (v0.21 Guided Campaign, Task 6): verify-gate snapshot tests. The v0.21 backend adds a new
// additive `campaignGuide` key to the /state (and downstream /stream) payload; v0.20.x clients must
// continue to deserialize the JSON without error. This is enforced two ways:
//
//   1. A hand-crafted v0.20-shaped golden fixture (Overlay/Fixtures/v020-stream-golden.json) that
//      mirrors what a v0.20.1 desktop build read off /state. The fixture is deliberately synthetic
//      rather than a live capture — CI cannot boot the game, so we author a plausible shape and lock
//      the contract on it. The v0.20.x-client sniff transcript (spec section 12) remains the manual
//      verify gate LO runs before shipping.
//
//   2. A synthesized v0.21 payload with `campaignGuide` populated, parsed against a DTO shaped like
//      the v0.20.x client. Extra fields (campaignGuide.*) are ignored silently by System.Text.Json —
//      that's the additive-only guarantee.
public class DashboardCampaignGuideSseSnapshotTests
{
    private static readonly string GoldenPath = Path.Combine(
        AppContext.BaseDirectory, "Overlay", "Fixtures", "v020-stream-golden.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void CampaignGuide_IsAdditive_v020GoldenStillDeserializes()
    {
        // The v0.20 golden fixture must parse cleanly against the v0.20-shape DTO. If a future refactor
        // renamed CampaignGps → something else or removed the Director array shape, this test would
        // fail — that's the wire-format canary.
        Assert.True(File.Exists(GoldenPath),
            "Golden fixture missing at " + GoldenPath + " — csproj CopyToOutputDirectory misconfigured?");
        var goldenJson = File.ReadAllText(GoldenPath);
        var v020 = JsonSerializer.Deserialize<V020StreamGoldenDto>(goldenJson, Opts);

        Assert.NotNull(v020);
        Assert.NotNull(v020!.Director);
        // CampaignGps still present under original name (Parallel Rail per spec Q1). Renaming or
        // relocating it would break every v0.20.x client that reads the banner.
        Assert.NotNull(v020.CampaignGps);
        Assert.Equal("Head to the Riverside encampment exit", v020.CampaignGps);
    }

    [Fact]
    public void CampaignGuide_PopulatedPayload_DoesNotBreakV020GoldenParse()
    {
        // Build a v0.21 snapshot with campaignGuide populated (matches the /state projection ApiServer
        // ships in EC2-UI) and confirm the v0.20 golden DTO still parses cleanly. `campaignGuide` is
        // an unknown key to the v0.20-shape DTO — System.Text.Json ignores it silently. That's the
        // additive-only wire-format contract.
        var v021Payload = new
        {
            director = new object[]
            {
                new { id = "obj.wp", label = "Waypoint", category = "Waypoint", priority = 100, tier = "Exit" },
            },
            campaignGps = "Head to the Riverside encampment exit",
            campaignGuide = new
            {
                stepId            = "act1.riverside.enter",
                text              = "Kill Beira of the Moonlight",
                areaId            = "G1_1",
                act               = 1,
                ordinal           = 3,
                totalSteps        = 250,
                optional          = false,
                stalled           = false,
                available         = true,
                degradationReason = (string?)null,
            },
        };
        var json = JsonSerializer.Serialize(v021Payload);

        var parsed = JsonSerializer.Deserialize<V020StreamGoldenDto>(json, Opts);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Director);
        Assert.Equal("Head to the Riverside encampment exit", parsed.CampaignGps);
    }

    [Fact]
    public void CampaignGuide_StalledPayload_DoesNotBreakV020GoldenParse()
    {
        // Same as above but with Stalled=true + a DegradationReason string — the graceful-degradation
        // path from spec section 6. Still additive; v0.20 clients see nothing new.
        var v021Payload = new
        {
            director = System.Array.Empty<object>(),
            campaignGps = (string?)null,
            campaignGuide = new
            {
                stepId            = "act1.devourer",
                text              = "Slay the Devourer",
                areaId            = "G1_2",
                act               = 1,
                ordinal           = 7,
                totalSteps        = 250,
                optional          = false,
                stalled           = true,
                available         = false,
                degradationReason = "you're in G1_1, route expects G1_2",
            },
        };
        var json = JsonSerializer.Serialize(v021Payload);

        var parsed = JsonSerializer.Deserialize<V020StreamGoldenDto>(json, Opts);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Director);
        Assert.Null(parsed.CampaignGps);
    }

    // Mirrors the fields v0.20.1 desktop client reads off /state. Any unknown JSON key deserializes
    // silently, so populating `campaignGuide` on the wire cannot break v0.20.x parsing.
    private sealed record V020StreamGoldenDto(
        object[]? Director,
        string? CampaignGps);
}
