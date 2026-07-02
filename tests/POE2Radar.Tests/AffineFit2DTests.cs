using POE2Radar.Core;

public class AffineFit2DTests
{
    // A known affine: sx = 2·gx + 0.5·gy + 100 ; sy = -0.5·gx + 3·gy + 50
    static (float, float, float, float) Anchor(float gx, float gy)
        => (gx, gy, 2f * gx + 0.5f * gy + 100f, -0.5f * gx + 3f * gy + 50f);

    [Fact]
    public void TryFit_recovers_a_known_affine_and_extrapolates_offscreen()
    {
        var anchors = new[] { Anchor(0, 0), Anchor(10, 0), Anchor(0, 10), Anchor(10, 10), Anchor(-5, 7) };
        Assert.True(AffineFit2D.TryFit(anchors, out var fit));
        // Coefficients recovered.
        Assert.Equal(2.0, fit.A, 3); Assert.Equal(0.5, fit.B, 3); Assert.Equal(100.0, fit.C, 3);
        Assert.Equal(-0.5, fit.D, 3); Assert.Equal(3.0, fit.E, 3); Assert.Equal(50.0, fit.F, 3);
        // Apply to a FAR held-out grid coord (the off-screen case) matches the true affine.
        var (sx, sy) = AffineFit2D.Apply(fit, 200f, -150f);
        Assert.Equal(2f * 200f + 0.5f * -150f + 100f, sx, 1);
        Assert.Equal(-0.5f * 200f + 3f * -150f + 50f, sy, 1);
    }

    [Fact]
    public void TryFit_fails_with_fewer_than_three_anchors()
    {
        var two = new[] { Anchor(0, 0), Anchor(1, 1) };
        Assert.False(AffineFit2D.TryFit(two, out _));
        Assert.False(AffineFit2D.TryFit(System.Array.Empty<(float, float, float, float)>(), out _));
    }

    [Fact]
    public void TryFit_fails_on_collinear_grid_points()
    {
        // All grids on the line gy = gx → singular normal matrix → no unique affine.
        var collinear = new[] { Anchor(0, 0), Anchor(1, 1), Anchor(2, 2), Anchor(3, 3), Anchor(4, 4) };
        Assert.False(AffineFit2D.TryFit(collinear, out _));
    }

    [Fact]
    public void Apply_computes_the_affine()
    {
        var fit = new AffineFit2D.Affine(2, 0.5, 100, -0.5, 3, 50);
        var (sx, sy) = AffineFit2D.Apply(fit, 10f, 4f);
        Assert.Equal(122f, sx, 3);   // 2*10 + 0.5*4 + 100
        Assert.Equal(57f, sy, 3);    // -0.5*10 + 3*4 + 50
    }
}
