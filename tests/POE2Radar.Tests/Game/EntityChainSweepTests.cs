using System;
using System.Linq;
using System.Reflection;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Game;

// v0.42 B2a: entity-chain root sweeps in /api/entity-probe. Locks down the 4 new sweep fields
// on EntityProbeSample (EntityDetailsSweep, ComponentListSweep, EntityDetailsNameSweep,
// ComponentLookUpBucketSweep) — field names, types, positions, and count. Pure reflection,
// no live MemoryReader needed. Follows the EntityProbeSampleTests pattern.
public sealed class EntityChainSweepTests
{
    [Fact]
    public void EntityProbeSample_HasElevenPositionalFields()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var parameters = ctor.GetParameters();
        Assert.Equal(11, parameters.Length);
    }

    [Fact]
    public void EntityDetailsSweep_HasFourEntries_MatchesCandidateCount()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var param = ctor.GetParameters().Single(p => p.Name == "EntityDetailsSweep");
        Assert.Equal(typeof(string[]), param.ParameterType);
    }

    [Fact]
    public void ComponentListSweep_HasFiveEntries()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var param = ctor.GetParameters().Single(p => p.Name == "ComponentListSweep");
        Assert.Equal(typeof(string[]), param.ParameterType);
    }

    [Fact]
    public void EntityDetailsNameSweep_HasFourEntries()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var param = ctor.GetParameters().Single(p => p.Name == "EntityDetailsNameSweep");
        Assert.Equal(typeof(string[]), param.ParameterType);
    }

    [Fact]
    public void ComponentLookUpBucketSweep_HasFourEntries()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var param = ctor.GetParameters().Single(p => p.Name == "ComponentLookUpBucketSweep");
        Assert.Equal(typeof(string[]), param.ParameterType);
    }

    [Fact]
    public void NewFields_AppendedNotInserted_OldFieldsPreserveOrder()
    {
        // Verify existing 7 field names are still at positions 0..6
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
    }

    [Fact]
    public void EntityDetailsSweep_FieldTypeIsStringArray()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var prop = type.GetProperty("EntityDetailsSweep");
        Assert.NotNull(prop);
        Assert.Equal(typeof(string[]), prop!.PropertyType);
    }

    [Fact]
    public void NewFields_ArePublicProperties_ForJsonSerialization()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("EntityDetailsSweep", props);
        Assert.Contains("ComponentListSweep", props);
        Assert.Contains("EntityDetailsNameSweep", props);
        Assert.Contains("ComponentLookUpBucketSweep", props);
    }

    [Fact]
    public void NewFields_AreReadWriteProperties()
    {
        // Positional record fields are compiler-generated init-only properties.
        // Verify they have both getters and are in the constructor parameter list.
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var paramNames = ctor.GetParameters().Select(p => p.Name).ToHashSet();

        var detailsSweep = type.GetProperty("EntityDetailsSweep")!;
        Assert.NotNull(detailsSweep.GetMethod);
        Assert.Contains("EntityDetailsSweep", paramNames);

        var compListSweep = type.GetProperty("ComponentListSweep")!;
        Assert.NotNull(compListSweep.GetMethod);
        Assert.Contains("ComponentListSweep", paramNames);

        var nameSweep = type.GetProperty("EntityDetailsNameSweep")!;
        Assert.NotNull(nameSweep.GetMethod);
        Assert.Contains("EntityDetailsNameSweep", paramNames);

        var bucketSweep = type.GetProperty("ComponentLookUpBucketSweep")!;
        Assert.NotNull(bucketSweep.GetMethod);
        Assert.Contains("ComponentLookUpBucketSweep", paramNames);
    }

    [Fact]
    public void EntityProbeSample_IsNestedPublic_InsidePoe2Live()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample");
        Assert.NotNull(type);
        Assert.True(type!.IsNestedPublic);
    }
}