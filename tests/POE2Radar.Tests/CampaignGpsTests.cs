using System.Collections.Generic;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;
using Xunit;
using V2 = System.Numerics.Vector2;

namespace POE2Radar.Tests;

public class CampaignGpsTests
{
    private const string Json = """
    [
      { "zone": "Z1", "act": 1, "name": "Zone One", "next": "Z2", "exitHint": "Zone Two" },
      { "zone": "Z2", "act": 1, "name": "Zone Two", "next": "Z3", "exitHint": "Zone Three" },
      { "zone": "Z3", "act": 2, "name": "Zone Three", "next": null, "exitHint": null }
    ]
    """;
    private static CampaignRoute R() => CampaignRoute.FromJson(Json);
    private static readonly IReadOnlyList<Poe2Live.EntityDot> NoEntities = new List<Poe2Live.EntityDot>();

    private static Poe2Live.Landmark Lm(string path, string? curated, float x, float y) =>
        new("derived", path, new V2(x, y), 1, curated);

    [Fact] public void In_target_zone_routes_to_the_exit_toward_next_by_exitHint()
    {
        var lms = new List<Poe2Live.Landmark> {
            Lm("exit_a.tdt", "Zone Three", 10, 10),   // the onward exit (matches Z2.exitHint)
            Lm("exit_b.tdt", "Somewhere Else", 5, 5),
        };
        var ins = CampaignGps.Decide("Z2", new ZoneOrderProgress(R()), R(), lms, NoEntities, new V2(0, 0));
        Assert.Equal("t:exit_a.tdt@10,10", ins.ExitObjectiveId);
        Assert.Contains("Zone Three", ins.Text);
    }

    [Fact] public void Off_target_zone_routes_back_toward_the_target_by_name()
    {
        var p = new ZoneOrderProgress(R());
        p.CurrentStep("Z2");   // latch at Z2
        var lms = new List<Poe2Live.Landmark> { Lm("back.tdt", "Zone Two", 3, 4) };
        var ins = CampaignGps.Decide("SideZone", p, R(), lms, NoEntities, new V2(0, 0));
        Assert.Equal("t:back.tdt@3,4", ins.ExitObjectiveId);
        Assert.Contains("Zone Two", ins.Text);
    }

    [Fact] public void In_target_zone_uses_CodeForName_when_exitHint_does_not_match_a_label()
    {
        // Z2.exitHint "Detour" matches no landmark label, so the engine must fall to precedence 2:
        // a landmark whose CuratedName resolves via CodeForName to the NEXT zone's code (Z3 = "Zone Three").
        const string json = """
        [
          { "zone": "Z2", "act": 1, "name": "Zone Two",   "next": "Z3", "exitHint": "Detour" },
          { "zone": "Z3", "act": 2, "name": "Zone Three", "next": null, "exitHint": null }
        ]
        """;
        var route = CampaignRoute.FromJson(json);
        var lms = new List<Poe2Live.Landmark> { Lm("z3exit.tdt", "Zone Three", 7, 8) };
        var ins = CampaignGps.Decide("Z2", new ZoneOrderProgress(route), route, lms, NoEntities, new V2(0, 0));
        Assert.Equal("t:z3exit.tdt@7,8", ins.ExitObjectiveId);
    }

    [Fact] public void No_matching_label_falls_back_to_nearest_transition_entity()
    {
        var entities = new List<Poe2Live.EntityDot> {
            new(42u, default, new V2(2, 0), default, Poe2Live.EntityCategory.Transition, "Metadata/.../AreaTransition", 0, 0, false, 0, Poe2Live.Rarity.NonMonster, false),
        };
        var ins = CampaignGps.Decide("Z2", new ZoneOrderProgress(R()), R(), new List<Poe2Live.Landmark>(), entities, new V2(0, 0));
        Assert.Equal("e:42", ins.ExitObjectiveId);
    }

    [Fact] public void No_exit_anywhere_yields_null_objective_but_still_an_instruction()
    {
        var ins = CampaignGps.Decide("Z2", new ZoneOrderProgress(R()), R(), new List<Poe2Live.Landmark>(), NoEntities, new V2(0, 0));
        Assert.Null(ins.ExitObjectiveId);
        Assert.False(string.IsNullOrEmpty(ins.Text));
    }
}
