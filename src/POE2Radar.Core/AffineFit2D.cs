namespace POE2Radar.Core;

/// <summary>
/// Least-squares fit of a 2D affine map <c>(sx,sy) = (A·gx + B·gy + C, D·gx + E·gy + F)</c> from a set of
/// (grid, screen/canvas) sample pairs, plus <see cref="Apply"/> to evaluate it. Used to place OFF-screen
/// atlas nodes: their live position is unreliable once the game culls them, but their integer grid
/// coordinate is stable, so we fit the grid→canvas map from the ON-screen nodes and extrapolate. Pure math;
/// no IO. Both output axes share one 3×3 normal matrix, solved by symmetric inverse.
/// </summary>
public static class AffineFit2D
{
    public readonly record struct Affine(double A, double B, double C, double D, double E, double F);

    /// <summary>Fit from ≥3 non-degenerate anchors. Returns false (and default fit) for &lt;3 anchors, a
    /// singular/collinear set, or a non-finite result. Never throws.</summary>
    public static bool TryFit(IReadOnlyList<(float Gx, float Gy, float Sx, float Sy)> anchors, out Affine fit)
    {
        fit = default;
        if (anchors is null || anchors.Count < 3) return false;

        // Normal matrix N = Σ [gx,gy,1]ᵀ[gx,gy,1] (symmetric 3×3); RHS rx/ry = Σ [gx,gy,1]ᵀ·s.
        double n11 = 0, n12 = 0, n13 = 0, n22 = 0, n23 = 0, n33 = 0;
        double rx1 = 0, rx2 = 0, rx3 = 0, ry1 = 0, ry2 = 0, ry3 = 0;
        foreach (var (gx, gy, sx, sy) in anchors)
        {
            double x = gx, y = gy;
            n11 += x * x; n12 += x * y; n13 += x; n22 += y * y; n23 += y; n33 += 1;
            rx1 += x * sx; rx2 += y * sx; rx3 += sx;
            ry1 += x * sy; ry2 += y * sy; ry3 += sy;
        }

        double det = n11 * (n22 * n33 - n23 * n23)
                   - n12 * (n12 * n33 - n23 * n13)
                   + n13 * (n12 * n23 - n22 * n13);
        if (Math.Abs(det) < 1e-6) return false;   // singular / collinear grids

        // Symmetric inverse (cofactors / det).
        double i11 = (n22 * n33 - n23 * n23) / det, i12 = (n13 * n23 - n12 * n33) / det, i13 = (n12 * n23 - n13 * n22) / det;
        double i22 = (n11 * n33 - n13 * n13) / det, i23 = (n12 * n13 - n11 * n23) / det, i33 = (n11 * n22 - n12 * n12) / det;

        double A = i11 * rx1 + i12 * rx2 + i13 * rx3;
        double B = i12 * rx1 + i22 * rx2 + i23 * rx3;
        double C = i13 * rx1 + i23 * rx2 + i33 * rx3;
        double D = i11 * ry1 + i12 * ry2 + i13 * ry3;
        double E = i12 * ry1 + i22 * ry2 + i23 * ry3;
        double F = i13 * ry1 + i23 * ry2 + i33 * ry3;

        if (!(double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C)
           && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F))) return false;

        fit = new Affine(A, B, C, D, E, F);
        return true;
    }

    /// <summary>Evaluate the fitted affine at a grid coordinate.</summary>
    public static (float Sx, float Sy) Apply(in Affine fit, float gx, float gy)
        => ((float)(fit.A * gx + fit.B * gy + fit.C), (float)(fit.D * gx + fit.E * gy + fit.F));
}
