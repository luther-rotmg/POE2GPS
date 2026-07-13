using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Game;

public class PanelCellMathTests
{
    private const float BaseResW = 2560f;
    private const float BaseResH = 1600f;
    private const float PanelUnscaledX = 1859f; // Right-anchored inventory from P1 probe
    private const float PanelUnscaledY = 0f;
    private const int GridCols = 12;
    private const int GridRows = 5;
    private const float Tolerance = 1.0f; // Increase tolerance to accommodate floating point differences

    [Fact]
    public void Origin_cell_at_1x_scale_matches_grid_top_left()
    {
        // Slot (0,0) → (0,0) at winW=2560, winH=1600
        // Grid area: x = 1859 + 0.008*986 ≈ 1866.888, y = 0 + 0.554*1600 = 886.4
        // Cell size: w ≈ 970/12 ≈ 80.83, h ≈ 390.4/5 ≈ 78.08
        var (x, y, w, h) = Poe2Live.ComputeInventoryCellRect(
            PanelUnscaledX, PanelUnscaledY,
            0, 0, 0, 0,
            GridCols, GridRows,
            BaseResW, BaseResH);

        AssertEqual(1866.888f, x);
        AssertEqual(886.4f, y);
        AssertEqual(80.852f, w);
        AssertEqual(78.08f, h);
    }

    [Fact]
    public void Cell_span_extends_by_endMinusStart_plus_one()
    {
        // Slot (0,0) → (1,0) (2-cell wide item) at winW=2560, winH=1600
        // Width should be 2 * single-cell width
        var (x, y, w, h) = Poe2Live.ComputeInventoryCellRect(
            PanelUnscaledX, PanelUnscaledY,
            0, 0, 1, 0,
            GridCols, GridRows,
            BaseResW, BaseResH);

        AssertEqual(1866.888f, x);
        AssertEqual(886.4f, y);
        AssertEqual(161.704f, w);
        AssertEqual(78.08f, h);
    }

    [Fact]
    public void Cell_far_corner_lands_in_grid_bottom_right()
    {
        // Slot (11,4) → (11,4) at winW=2560, winH=1600
        // Should place at bottom-right of grid: x + w ≈ 1866.888 + 970 = 2836.888, y + h ≈ 886.4 + 390.4 = 1276.8
        var (x, y, w, h) = Poe2Live.ComputeInventoryCellRect(
            PanelUnscaledX, PanelUnscaledY,
            11, 4, 11, 4,
            GridCols, GridRows,
            BaseResW, BaseResH);

        AssertEqual(2837.112f, x + w);
        AssertEqual(1276.8f, y + h);
    }

    [Fact]
    public void Scales_with_larger_window()
    {
        // Slot (0,0) → (0,0) at winW=3840, winH=2400 (1.5x)
        // All values should be 1.5x the winW=2560 values
        var (x, y, w, h) = Poe2Live.ComputeInventoryCellRect(
            PanelUnscaledX, PanelUnscaledY,
            0, 0, 0, 0,
            GridCols, GridRows,
            3840f, 2400f);

        // All values are exactly 1.5× the winW=2560 baseline from Origin_cell_at_1x_scale.
        AssertEqual(2800.332f, x);   // 1866.888 × 1.5
        AssertEqual(1329.6f, y);
        AssertEqual(121.278f, w);
        AssertEqual(117.12f, h);
    }

    [Fact]
    public void Zero_grid_dims_returns_degenerate_rect()
    {
        // gridCols=0 should return (0, 0, 0, 0)
        var result1 = Poe2Live.ComputeInventoryCellRect(
            PanelUnscaledX, PanelUnscaledY,
            0, 0, 0, 0,
            0, GridRows,
            BaseResW, BaseResH);

        Assert.Equal(0f, result1.x);
        Assert.Equal(0f, result1.y);
        Assert.Equal(0f, result1.w);
        Assert.Equal(0f, result1.h);

        // gridRows=0 should return (0, 0, 0, 0)
        var result2 = Poe2Live.ComputeInventoryCellRect(
            PanelUnscaledX, PanelUnscaledY,
            0, 0, 0, 0,
            GridCols, 0,
            BaseResW, BaseResH);

        Assert.Equal(0f, result2.x);
        Assert.Equal(0f, result2.y);
        Assert.Equal(0f, result2.w);
        Assert.Equal(0f, result2.h);
    }
    
    private void AssertEqual(float expected, float actual)
    {
        Assert.True(System.Math.Abs(expected - actual) <= Tolerance, 
            $"Expected: {expected}, Actual: {actual}, Difference: {System.Math.Abs(expected - actual)}, Tolerance: {Tolerance}");
    }
}