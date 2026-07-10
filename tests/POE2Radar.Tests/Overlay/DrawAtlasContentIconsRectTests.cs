using Vortice.Mathematics;
using Xunit;

namespace POE2Radar.Tests.Overlay;

/// <summary>
/// Locks the destination-rect math for the atlas content-icon row on fogged nodes.
/// The rect MUST be square (width == height == iconH) and origin-aligned
/// (left == ix, top == iy, right == ix + iconH, bottom == iy + iconH).
/// Vortice.Mathematics.Rect's four-arg constructor is (X, Y, Width, Height),
/// so the correct call is <c>new Rect(ix, iy, iconH, iconH)</c>. Any drift to
/// <c>new Rect(ix, iy, ix + iconH, iy + iconH)</c> (treating the constructor
/// as LTRB) inflates width/height with the icon's screen-space coordinates
/// and silently mis-places icons at high atlas zoom.
/// </summary>
public class DrawAtlasContentIconsRectTests
{
    [Fact]
    public void ComputeAtlasContentIconDestRect_IsSquareAndOriginAligned()
    {
        // Synthetic icon at a known coord, iconH = 24 px.
        const float ix = 137.5f;
        const float iy = 42.0f;
        const float iconH = 24f;

        var rect = POE2Radar.Overlay.OverlayRenderer
            .ComputeAtlasContentIconDestRect(ix, iy, iconH);

        Assert.Equal(ix,          rect.Left,   3);
        Assert.Equal(iy,          rect.Top,    3);
        Assert.Equal(ix + iconH,  rect.Right,  3);
        Assert.Equal(iy + iconH,  rect.Bottom, 3);
        // Square: width == height == iconH.
        Assert.Equal(iconH, rect.Right  - rect.Left, 3);
        Assert.Equal(iconH, rect.Bottom - rect.Top,  3);
    }

    [Theory]
    [InlineData(0f,      0f,      6f)]
    [InlineData(-50f,    100f,    12f)]
    [InlineData(1024.9f, 2048.1f, 32.5f)]
    public void ComputeAtlasContentIconDestRect_SquareForVariedInputs(float ix, float iy, float iconH)
    {
        var rect = POE2Radar.Overlay.OverlayRenderer
            .ComputeAtlasContentIconDestRect(ix, iy, iconH);

        Assert.Equal(iconH, rect.Right  - rect.Left, 3);
        Assert.Equal(iconH, rect.Bottom - rect.Top,  3);
        Assert.Equal(ix, rect.Left, 3);
        Assert.Equal(iy, rect.Top,  3);
    }
}
