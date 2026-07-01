using POE2Radar.Core.Game;

public class EdgeArrowTests
{
    // W=1000,H=800 → centre (500,400); margin 46 → inset half-extents (454, 354)
    [Fact] public void Point_far_right_lands_on_right_inset_edge()
    {
        var (ex, ey, ux, uy) = EdgeArrow.BorderPoint(5000, 400, 500, 400, 1000, 800, 46);
        Assert.True(System.Math.Abs(ex - (500 + 454)) < 0.5);   // right inset edge x
        Assert.True(System.Math.Abs(ey - 400) < 0.5);           // same height
        Assert.True(ux > 0.99 && System.Math.Abs(uy) < 0.01);   // unit dir points right
    }
    [Fact] public void Point_top_lands_on_top_inset_edge()
    {
        var (ex, ey, _, uy) = EdgeArrow.BorderPoint(500, -5000, 500, 400, 1000, 800, 46);
        Assert.True(System.Math.Abs(ey - (400 - 354)) < 0.5);   // top inset edge y
        Assert.True(uy < -0.99);                                 // points up
    }
    [Fact] public void Degenerate_same_point_returns_centre_zero_dir()
    {
        var (ex, ey, ux, uy) = EdgeArrow.BorderPoint(500, 400, 500, 400, 1000, 800, 46);
        Assert.Equal(500, ex, 3); Assert.Equal(400, ey, 3);
        Assert.Equal(0, ux, 3);  Assert.Equal(0, uy, 3);
    }
}
