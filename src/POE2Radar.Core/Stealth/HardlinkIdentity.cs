namespace POE2Radar.Core.Stealth;

/// <summary>Pure file-identity predicate — no P/Invoke in its signature so tests need no Win32.</summary>
public static class HardlinkIdentity
{
    /// <summary>Returns true when both handles point to the same NTFS inode
    /// (dwVolumeSerialNumber + nFileIndexHigh + nFileIndexLow all match).</summary>
    public static bool SameFileId(
        uint volA, uint idxHiA, uint idxLoA,
        uint volB, uint idxHiB, uint idxLoB) =>
        volA == volB && idxHiA == idxHiB && idxLoA == idxLoB;
}
