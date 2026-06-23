// tests/POE2Radar.Tests/RenderedLineIndexTests.cs
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class RenderedLineIndexTests
{
    [Theory]
    [InlineData("+# to maximum Life", "base_maximum_life")]
    [InlineData("+#% to Cold Resistance", "base_cold_damage_resistance_%")]
    public void resolves_common_meta_lines(string line, string expectedStatId)
    {
        var ids = ItemModTranslator.Shared.StatIdsForRenderedLine(line);
        Assert.NotNull(ids);
        Assert.Contains(expectedStatId, ids!);
    }

    [Fact]
    public void unknown_line_returns_null()
        => Assert.Null(ItemModTranslator.Shared.StatIdsForRenderedLine("this is not a real stat line at all"));
}
