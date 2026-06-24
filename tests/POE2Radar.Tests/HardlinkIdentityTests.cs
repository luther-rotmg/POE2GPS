using POE2Radar.Core.Stealth;

public class HardlinkIdentityTests
{
    [Fact] public void SameFileId_IdenticalValues_ReturnsTrue()      => Assert.True( HardlinkIdentity.SameFileId(1u, 2u, 3u, 1u, 2u, 3u));
    [Fact] public void SameFileId_DifferentVolume_ReturnsFalse()     => Assert.False(HardlinkIdentity.SameFileId(1u, 2u, 3u, 2u, 2u, 3u));
    [Fact] public void SameFileId_DifferentIndexHigh_ReturnsFalse()  => Assert.False(HardlinkIdentity.SameFileId(1u, 2u, 3u, 1u, 9u, 3u));
    [Fact] public void SameFileId_DifferentIndexLow_ReturnsFalse()   => Assert.False(HardlinkIdentity.SameFileId(1u, 2u, 3u, 1u, 2u, 9u));
    [Fact] public void SameFileId_AllZeros_ReturnsTrue()             => Assert.True( HardlinkIdentity.SameFileId(0u, 0u, 0u, 0u, 0u, 0u));
}
