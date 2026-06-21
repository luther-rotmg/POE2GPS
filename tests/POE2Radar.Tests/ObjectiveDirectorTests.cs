using POE2Radar.Core.Campaign;

public class ObjectiveDirectorTests
{
    private static RankedObjective R(string id, int prio) => new(id, id, "c", prio, 0f);
    private static readonly string[] None = System.Array.Empty<string>();
    private static readonly RankedObjective[] NoObjectives = System.Array.Empty<RankedObjective>();

    [Fact]
    public void SelectsTop_WhenSelectionEmpty()
    {
        var d = new ObjectiveDirector();
        var dec = d.Decide(new[] { R("e:1", 100), R("e:2", 10) }, None);
        Assert.True(dec.ChangeSelection);
        Assert.Equal("e:1", dec.DesiredActiveId);
    }

    [Fact]
    public void NoChange_WhenAlreadyOnTop()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100) }, None);            // selects e:1
        var dec = d.Decide(new[] { R("e:1", 100) }, new[] { "e:1" });
        Assert.False(dec.ChangeSelection);
    }

    [Fact]
    public void Advances_WhenActiveCompletes()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100), R("e:2", 80) }, None); // active e:1
        // e:1 completed → no longer ranked; selection cleared by PruneCompletedTargets → empty
        var dec = d.Decide(new[] { R("e:2", 80) }, None);
        Assert.True(dec.ChangeSelection);
        Assert.Equal("e:2", dec.DesiredActiveId);
    }

    [Fact]
    public void StandsDown_OnManualOverride()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100) }, None);                 // active e:1
        var dec = d.Decide(new[] { R("e:1", 100) }, new[] { "e:9" }); // user picked e:9
        Assert.False(dec.ChangeSelection);
    }

    [Fact]
    public void StandsDown_WhenUserAddedSecondTarget()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100) }, None);
        var dec = d.Decide(new[] { R("e:1", 100) }, new[] { "e:1", "e:7" });
        Assert.False(dec.ChangeSelection);
    }

    [Fact]
    public void ResetZone_LetsDirectorReacquire()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100) }, new[] { "e:9" }); // stood down (manual)
        d.ResetZone();
        var dec = d.Decide(new[] { R("e:5", 100) }, None);  // new zone, empty selection
        Assert.True(dec.ChangeSelection);
        Assert.Equal("e:5", dec.DesiredActiveId);
    }

    [Fact]
    public void ClearsToEmpty_WhenNothingRankedAndDirectorOwns()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100) }, None);  // active e:1
        var dec = d.Decide(NoObjectives, new[] { "e:1" }); // nothing present now
        Assert.True(dec.ChangeSelection);
        Assert.Null(dec.DesiredActiveId);
    }

    [Fact]
    public void Queue_ExposesRanked()
    {
        var d = new ObjectiveDirector();
        d.Decide(new[] { R("e:1", 100), R("e:2", 80) }, None);
        Assert.Equal(new[] { "e:1", "e:2" }, d.Queue.Select(q => q.Id).ToArray());
    }
}
