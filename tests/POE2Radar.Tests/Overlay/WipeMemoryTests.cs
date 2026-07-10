using System;
using System.Collections.Generic;
using Xunit;
using POE2Radar.Overlay.Overlay;

namespace POE2Radar.Tests.Overlay;

public class WipeMemoryTests
{
    [Fact]
    public void Count_returns_zero_for_unknown_zone()
    {
        var memory = new WipeMemory(new Dictionary<string, int>(), () => { });
        
        Assert.Equal(0, memory.Count("MapAny"));
        Assert.Equal(0, memory.Count(null));
        Assert.Equal(0, memory.Count(""));
    }

    [Fact]
    public void RecordDeath_increments_count()
    {
        var memory = new WipeMemory(new Dictionary<string, int>(), () => { });
        
        Assert.Equal(1, memory.RecordDeath("MapUberBoss_Breach"));
        Assert.Equal(2, memory.RecordDeath("MapUberBoss_Breach"));
        Assert.Equal(2, memory.Count("MapUberBoss_Breach"));
    }

    [Fact]
    public void RecordDeath_null_or_empty_is_noop()
    {
        var memory = new WipeMemory(new Dictionary<string, int>(), () => { });
        
        Assert.Equal(0, memory.RecordDeath(null));
        Assert.Equal(0, memory.RecordDeath(""));
        Assert.Equal(0, memory.Snapshot().Count);
    }

    [Fact]
    public void RecordDeath_fires_onChanged()
    {
        var callCount = 0;
        var memory = new WipeMemory(new Dictionary<string, int>(), () => callCount++);
        
        memory.RecordDeath("X");
        Assert.Equal(1, callCount);
        
        memory.RecordDeath("X");
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void ClearZone_removes_entry_and_fires_onChanged()
    {
        var callCount = 0;
        var dict = new Dictionary<string, int> { { "MapA", 3 } };
        var memory = new WipeMemory(dict, () => callCount++);
        
        Assert.True(memory.ClearZone("MapA"));
        Assert.Equal(0, memory.Count("MapA"));
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void ClearZone_missing_zone_returns_false()
    {
        var callCount = 0;
        var memory = new WipeMemory(new Dictionary<string, int>(), () => callCount++);
        
        Assert.False(memory.ClearZone("NotThere"));
        Assert.Equal(0, callCount);
    }

    [Fact]
    public void ClearAll_wipes_dict_and_fires_onChanged()
    {
        var callCount = 0;
        var dict = new Dictionary<string, int> { { "A", 1 }, { "B", 2 } };
        var memory = new WipeMemory(dict, () => callCount++);
        
        memory.ClearAll();
        Assert.Equal(0, memory.Snapshot().Count);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Snapshot_is_independent_of_internal_state()
    {
        var memory = new WipeMemory(new Dictionary<string, int>(), () => { });
        var snapshot1 = memory.Snapshot();
        
        memory.RecordDeath("TestZone");
        var snapshot2 = memory.Snapshot();
        
        Assert.Equal(0, snapshot1.Count);
        Assert.Equal(1, snapshot2.Count);
    }

    [Fact]
    public void Ctor_null_dict_is_tolerated()
    {
        var memory = new WipeMemory(null, () => { });
        Assert.Equal(1, memory.RecordDeath("X"));
    }

    [Fact]
    public void Ctor_null_onChanged_is_tolerated()
    {
        var memory = new WipeMemory(new Dictionary<string, int>(), null!);
        Assert.Equal(1, memory.RecordDeath("X"));
    }
}