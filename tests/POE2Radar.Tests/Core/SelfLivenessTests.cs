using System;
using System.Collections.Generic;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Core;

public class SelfLivenessTests
{
    // We can't spin up a real MemoryReader in a unit test — this test exercises
    // the guard's contract at the algorithm level. If the guard is missing, a
    // recycled slot with a stale Self pointer produces a phantom entity in the
    // result list. With the guard, it's filtered.

    [Fact]
    public void RecycledSlot_SelfMismatch_isFiltered()
    {
        // Synthetic (slot address, Self pointer read from that slot) pairs.
        // Slot 0x1000 has been recycled — its Self field still contains 0xDEADBEEF
        // from the previous occupant, not 0x1000.
        var slots = new List<(ulong slot, ulong selfPtr)>
        {
            (0x1000UL, 0xDEADBEEFUL), // stale — should be filtered
            (0x2000UL, 0x2000UL),     // healthy — should pass
        };
        var kept = Poe2Live.FilterLiveSlotsForTest(slots);
        Assert.Single(kept);
        Assert.Equal(0x2000UL, kept[0].slot);
    }
}
