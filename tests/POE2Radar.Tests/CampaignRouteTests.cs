using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class CampaignRouteTests
{
    // A tiny fixture so the loader logic is tested independently of the shipped (draft) data.
    private const string Json = """
    [
      { "zone": "Z1", "act": 1, "name": "Zone One", "next": "Z2", "exitHint": "Zone Two" },
      { "zone": "Z2", "act": 1, "name": "Zone Two", "next": "Z3", "exitHint": null },
      { "zone": "Z3", "act": 2, "name": "Zone Three", "next": null, "exitHint": null }
    ]
    """;
    private static CampaignRoute R() => CampaignRoute.FromJson(Json);

    [Fact] public void StepFor_returns_the_matching_step()
    {
        var s = R().StepFor("Z2");
        Assert.NotNull(s);
        Assert.Equal("Zone Two", s!.Value.Name);
        Assert.Equal("Z3", s.Value.Next);
    }

    [Fact] public void StepFor_is_case_insensitive_and_null_on_miss()
    {
        Assert.NotNull(R().StepFor("z1"));
        Assert.Null(R().StepFor("nope"));
    }

    [Fact] public void NextStep_resolves_the_next_code()
    {
        var r = R();
        var s1 = r.StepFor("Z1")!.Value;
        Assert.Equal("Zone Two", r.NextStep(s1)!.Value.Name);
        var s3 = r.StepFor("Z3")!.Value;
        Assert.Null(r.NextStep(s3));   // next == null → campaign end
    }

    [Fact] public void IndexOf_returns_ordinal_or_minus_one()
    {
        var r = R();
        Assert.Equal(0, r.IndexOf("Z1"));
        Assert.Equal(2, r.IndexOf("Z3"));
        Assert.Equal(-1, r.IndexOf("nope"));
    }

    [Fact] public void CodeForName_reverse_maps_name_to_code_case_insensitively()
    {
        var r = R();
        Assert.Equal("Z2", r.CodeForName("Zone Two"));
        Assert.Equal("Z2", r.CodeForName("zone two"));
        Assert.Null(r.CodeForName("Unknown"));
    }

    [Fact] public void Shared_loads_the_embedded_table_nonempty()
    {
        Assert.True(CampaignRoute.Shared.Steps.Count > 0);
    }
}
