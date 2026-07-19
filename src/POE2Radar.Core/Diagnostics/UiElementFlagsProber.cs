namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// A single snapshot of 32-bit words read at candidate Flags offsets on an atlas-panel
/// UiElement. Used by the /api/probe/uielement diagnostic endpoint to record raw flag
/// values for later XOR-diff analysis (B5b adds the auto-diff + heal logic).
/// </summary>
/// <param name="AtlasPanelAddr">The atlas-panel UiElement address, or 0 if unresolved.</param>
/// <param name="TakenUtc">The UTC timestamp when the snapshot was taken.</param>
/// <param name="WordsPerOffset">A dictionary mapping each candidate offset (in bytes)
/// to the 32-bit word read at that offset. Always contains exactly 13 entries for
/// offsets [0x170..0x1A0] step 4.</param>
public sealed record FlagsSnapshot(
    nint AtlasPanelAddr,
    System.DateTime TakenUtc,
    IReadOnlyDictionary<int, uint> WordsPerOffset);

/// <summary>
/// Probe that reads 13 candidate 32-bit words from a UiElement's Flags region
/// (<c>[0x170..0x1A0]</c> step 4). Purely diagnostic — no auto-heal, no
/// <c>HealedOffsetCache</c> writes. Read failures at a specific offset are caught
/// and recorded as <c>0u</c> (no exception escapes).
/// </summary>
public static class UiElementFlagsProber
{
    /// <summary>
    /// The 13 candidate offsets: 0x170, 0x174, 0x178, 0x17C, 0x180, 0x184, 0x188,
    /// 0x18C, 0x190, 0x194, 0x198, 0x19C, 0x1A0.
    /// </summary>
    private static readonly int[] CandidateOffsets = BuildCandidateOffsets();

    private static int[] BuildCandidateOffsets()
    {
        var offsets = new int[13];
        for (int i = 0; i < 13; i++)
            offsets[i] = 0x170 + i * 4;
        return offsets;
    }

    /// <summary>
    /// Read 32-bit words at each candidate offset on the given atlas-panel address.
    /// Returns a <see cref="FlagsSnapshot"/> with all 13 offsets populated.
    /// When <paramref name="atlasPanel"/> is 0, all words are recorded as 0u.
    /// Read failures at a specific offset are caught and recorded as 0u — no
    /// exception escapes.
    /// </summary>
    public static FlagsSnapshot TakeSnapshot(nint atlasPanel, MemoryReader r)
    {
        var words = new Dictionary<int, uint>(13);
        var now = System.DateTime.UtcNow;

        if (atlasPanel == 0)
        {
            foreach (var off in CandidateOffsets)
                words[off] = 0u;
            return new FlagsSnapshot(0, now, words);
        }

        foreach (var off in CandidateOffsets)
        {
            uint word = 0u;
            try
            {
                if (r.TryReadStruct<uint>(atlasPanel + off, out var w))
                    word = w;
                // else: record 0u
            }
            catch
            {
                // Read failure — record as 0u, no exception escapes
            }
            words[off] = word;
        }

        return new FlagsSnapshot(atlasPanel, now, words);
    }
}