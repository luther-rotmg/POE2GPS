using POE2Radar.Core.Game;

namespace POE2Radar.Tests;

public class ReactionRefreshTests
{
    [Theory]
    [InlineData(0, 30, true)]
    [InlineData(30, 30, true)]
    [InlineData(60, 30, true)]
    [InlineData(1, 30, false)]
    [InlineData(29, 30, false)]
    public void Refreshes_every_interval(int tick, int interval, bool expected)
        => Assert.Equal(expected, Poe2Live.ShouldRefreshReaction(tick, interval));
}
