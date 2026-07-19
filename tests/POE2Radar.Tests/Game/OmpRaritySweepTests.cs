using System;
using System.Linq;
using System.Reflection;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Game;

// v0.42 B7a: OMP rarity-candidate sweep in /api/entity-probe. Locks down the new
// RarityCandidateSweep field on EntityProbeSample — field name, type, position (12th),
// and public property surface. Pure reflection, no live MemoryReader needed.
// Follows the EntityChainSweepTests pattern.
public sealed class OmpRaritySweepTests
{
    [Fact]
    public void RarityCandidateSweep_FieldExists_OnEntityProbeSample()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var param = ctor.GetParameters().SingleOrDefault(p => p.Name == "RarityCandidateSweep");
        Assert.NotNull(param);
    }

    [Fact]
    public void RarityCandidateSweep_FieldTypeIsStringArray()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var param = ctor.GetParameters().Single(p => p.Name == "RarityCandidateSweep");
        Assert.Equal(typeof(string[]), param.ParameterType);
    }

    [Fact]
    public void RarityCandidateSweep_IsAppendedAtPosition11()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var paramNames = ctor.GetParameters().Select(p => p.Name).ToArray();

        // 12 total positional fields (0-indexed, so last is index 11)
        Assert.Equal(12, paramNames.Length);
        Assert.Equal("RarityCandidateSweep", paramNames[11]);
    }

    [Fact]
    public void RarityCandidateSweep_IsPublicProperty_ForJsonSerialization()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var prop = type.GetProperty("RarityCandidateSweep");
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetMethod); // has a public getter
    }

    [Fact]
    public void NewField_AppendedNotInserted_OldFieldsPreserveOrder()
    {
        // Verify all 11 existing field names are still at positions 0..10
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var paramNames = ctor.GetParameters().Select(p => p.Name).ToArray();

        Assert.Equal("EntityAddr", paramNames[0]);
        Assert.Equal("RenderAddr", paramNames[1]);
        Assert.Equal("LifeAddr", paramNames[2]);
        Assert.Equal("HpCurCurrentOffset", paramNames[3]);
        Assert.Equal("HpMaxCurrentOffset", paramNames[4]);
        Assert.Equal("LifeHealthSweep", paramNames[5]);
        Assert.Equal("RenderPositionSweep", paramNames[6]);
        Assert.Equal("EntityDetailsSweep", paramNames[7]);
        Assert.Equal("ComponentListSweep", paramNames[8]);
        Assert.Equal("EntityDetailsNameSweep", paramNames[9]);
        Assert.Equal("ComponentLookUpBucketSweep", paramNames[10]);
    }

    [Fact]
    public void RarityCandidateOffsets_HasSevenValues()
    {
        // Construct a sample with a 7-element RarityCandidateSweep (mirrors B2a
        // ComponentListSweep_HasFiveEntries pattern — shape assertion, not runtime behavior).
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var lifeSweep = Array.Empty<string>();
        var posSweep = Array.Empty<string>();
        var detailsSweep = Array.Empty<string>();
        var compListSweep = Array.Empty<string>();
        var nameSweep = Array.Empty<string>();
        var bucketSweep = Array.Empty<string>();
        var raritySweep = new string[7];

        var instance = Activator.CreateInstance(type,
            "0x0", "0x0", "0x0", 0, 0,
            lifeSweep, posSweep,
            detailsSweep, compListSweep, nameSweep, bucketSweep,
            raritySweep);
        Assert.NotNull(instance);

        var prop = type.GetProperty("RarityCandidateSweep")!;
        var result = (string[])prop.GetValue(instance)!;
        Assert.Equal(7, result.Length);
    }
}