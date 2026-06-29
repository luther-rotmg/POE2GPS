using POE2Radar.Core;

namespace POE2Radar.Tests;

public class RpmCounterTests
{
    [Fact] public void Zero_elapsed_returns_zero()
        => Assert.Equal(0f, MemoryReader.ComputeRpmPerSec(1000, 0.0));

    [Fact] public void One_second_window()
        => Assert.Equal(500f, MemoryReader.ComputeRpmPerSec(500, 1.0));

    [Fact] public void Half_second_window_doubles_rate()
        => Assert.Equal(1000f, MemoryReader.ComputeRpmPerSec(500, 0.5));

    [Fact] public void Negative_elapsed_returns_zero()
        => Assert.Equal(0f, MemoryReader.ComputeRpmPerSec(500, -1.0));
}
