using POE2Radar.Overlay.Overlay;
using System.Numerics;
using Vortice.Mathematics;
using Xunit;

namespace POE2Radar.Tests.Overlay;

public class EntityIconDestRectTests
{
    [Theory]
    [InlineData(100f, 100f, 8f,  92f,  92f, 108f, 108f)]
    [InlineData(  0f,   0f, 4f,  -4f,  -4f,   4f,   4f)]
    [InlineData(500f, 300f, 12f, 488f, 288f, 512f, 312f)]
    public void ComputeEntityIconDestRect_CentersOnPoint_WithDiameter2R(
        float cx, float cy, float r, float x0, float y0, float x1, float y1)
    {
        var rect = EntityIconCache.ComputeEntityIconDestRect(new Vector2(cx, cy), r);
        Assert.Equal(x0, rect.Left);
        Assert.Equal(y0, rect.Top);
        Assert.Equal(x1, rect.Right);
        Assert.Equal(y1, rect.Bottom);
    }

    [Fact]
    public void ComputeEntityIconDestRect_ClampsToMinRadiusOne()
    {
        var rect = EntityIconCache.ComputeEntityIconDestRect(new Vector2(50, 50), 0f);
        Assert.True(rect.Right - rect.Left >= 2f);
        Assert.True(rect.Bottom - rect.Top >= 2f);
    }
}