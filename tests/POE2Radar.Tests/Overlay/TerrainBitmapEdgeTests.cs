using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace POE2Radar.Tests.Overlay;

public class TerrainBitmapEdgeTests
{
    const int W = 24;
    const int H = 24;

    // Deterministic fixture:
    //   • A rectangular walkable room from (2..21, 2..21).
    //   • A diagonal wall stripe at cells where (x + y) % 7 == 0 inside the room.
    //   • Two isolated single-cell walls at (10,10) and (12,13) to force diagonal edge cases.
    //   • Outside the room and on the border: unwalkable.
    // Chosen to exercise cardinal + diagonal Moore neighbors AND out-of-bounds counting.
    static byte[] BuildWalkableFixture()
    {
        var grid = new byte[W * H];
        for (var y = 2; y <= 21; y++)
        for (var x = 2; x <= 21; x++)
        {
            if ((x + y) % 7 == 0) continue;
            if (x == 10 && y == 10) continue;
            if (x == 12 && y == 13) continue;
            grid[y * W + x] = 1;
        }
        return grid;
    }

    [Fact]
    public void Fixture_bin_matches_deterministic_generator()
    {
        var expected = BuildWalkableFixture();
        var actual = File.ReadAllBytes(FixturePath("terrain-small.bin"));
        Assert.Equal(expected, actual);
    }

    [Fact(Skip = "generator-only: unskip and run once to regenerate the fixture .bin files")]
    public void Generate_Fixture_Bins()
    {
        var walkable = BuildWalkableFixture();

        // Save walkable
        Directory.CreateDirectory(Path.GetDirectoryName(FixturePath("terrain-small.bin"))!);
        File.WriteAllBytes(FixturePath("terrain-small.bin"), walkable);

        // Compute expected edges using the same 8-neighbor Moore rule as TerrainBitmap.BuildFrom:
        //   cell is 'edge' if walkable[i] == 1 AND any of its 8 neighbors (OOB counted as 0/wall) is 0.
        var edges = new byte[W * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            if (walkable[y * W + x] == 0) continue;
            var isEdge = false;
            for (var dy = -1; dy <= 1 && !isEdge; dy++)
            for (var dx = -1; dx <= 1 && !isEdge; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var nx = x + dx; var ny = y + dy;
                if (nx < 0 || nx >= W || ny < 0 || ny >= H) { isEdge = true; break; }
                if (walkable[ny * W + nx] == 0) { isEdge = true; break; }
            }
            edges[y * W + x] = (byte)(isEdge ? 1 : 0);
        }
        File.WriteAllBytes(FixturePath("terrain-small-expected-edges.bin"), edges);
    }

    [Fact]
    public void BuildFrom_edge_detection_matches_expected_bin()
    {
        var walkable = File.ReadAllBytes(FixturePath("terrain-small.bin"));
        var expected = File.ReadAllBytes(FixturePath("terrain-small-expected-edges.bin"));

        var actual = POE2Radar.Overlay.TerrainBitmap.ComputeEdgesForTest(walkable, W, H);

        Assert.Equal(expected, actual);
    }

    static string FixturePath(string name)
        => Path.Combine(Path.GetDirectoryName(typeof(TerrainBitmapEdgeTests).Assembly.Location)!,
                        "fixtures", name);
}
