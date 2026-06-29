using POE2Radar.Core.Game;

namespace POE2Radar.Tests;

public class ComponentBucketMatchTests
{
    // Builds a fake bucket: each 16-byte entry = [int8 nameIdx slot used as a stand-in id][...]
    // We test the index-selection logic: given entry (nameKey,index) pairs and target keys,
    // results[t] must be the index of the matching entry, or -1 if absent.
    [Fact]
    public void Matches_targets_and_marks_absent()
    {
        // entries: key->index : ("Render",5),("Life",2),("Positioned",7)
        var entries = new (string Key, int Index)[] { ("Render", 5), ("Life", 2), ("Positioned", 7) };
        var targets = new[] { "Life", "MinimapIcon", "Render" };
        var results = Poe2Live.MatchComponentIndices(entries, targets);
        Assert.Equal(2, results[0]);   // Life
        Assert.Equal(-1, results[1]);  // MinimapIcon absent
        Assert.Equal(5, results[2]);   // Render
    }
}
