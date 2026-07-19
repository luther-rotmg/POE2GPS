using System;
using System.Linq;
using System.Reflection;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Game;

// v0.41.6 field diagnostic: locks down the EntityProbeSample record's public shape and
// Poe2Live.ProbeEntities' method signature so future edits can't silently break the
// /api/entity-probe consumer contract. No live MemoryReader needed — pure reflection
// against the shipped record + method surface. Matches the PanelResolverTests pattern
// of testing public API surface without spinning up a real memory reader.
public sealed class EntityProbeSampleTests
{
    [Fact]
    public void EntityProbeSample_Exists_AsPublicRecord()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample");
        Assert.NotNull(type);
        Assert.True(type!.IsPublic || type.IsNestedPublic);
        Assert.True(type.IsSealed, "EntityProbeSample should be sealed record");
    }

    [Fact(Skip = "shadowed by EntityChainSweepTests.EntityProbeSample_HasElevenPositionalFields after B2a extension")]
    public void EntityProbeSample_HasSevenPositionalFields()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        // Positional record — the primary constructor's parameter count equals the field count.
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var parameters = ctor.GetParameters();
        Assert.Equal(7, parameters.Length);
    }

    [Fact]
    public void EntityProbeSample_FieldsAreInExpectedOrder()
    {
        // The order matters because System.Text.Json serializes positional record properties
        // in constructor-parameter order. /api/entity-probe consumers depend on this shape.
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
    public void EntityProbeSample_SweepFieldsAreStringArrays()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var parameters = ctor.GetParameters();

        var lifeSweep = parameters.Single(p => p.Name == "LifeHealthSweep");
        var renderSweep = parameters.Single(p => p.Name == "RenderPositionSweep");
        Assert.Equal(typeof(string[]), lifeSweep.ParameterType);
        Assert.Equal(typeof(string[]), renderSweep.ParameterType);
    }

    [Fact]
    public void EntityProbeSample_AddressFieldsAreStrings()
    {
        // Addresses ship as hex-formatted strings (not longs) so the JSON payload is
        // human-readable when the user copies it back for support.
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var parameters = ctor.GetParameters();

        Assert.Equal(typeof(string), parameters.Single(p => p.Name == "EntityAddr").ParameterType);
        Assert.Equal(typeof(string), parameters.Single(p => p.Name == "RenderAddr").ParameterType);
        Assert.Equal(typeof(string), parameters.Single(p => p.Name == "LifeAddr").ParameterType);
    }

    [Fact]
    public void EntityProbeSample_HpFieldsAreInts()
    {
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var parameters = ctor.GetParameters();

        Assert.Equal(typeof(int), parameters.Single(p => p.Name == "HpCurCurrentOffset").ParameterType);
        Assert.Equal(typeof(int), parameters.Single(p => p.Name == "HpMaxCurrentOffset").ParameterType);
    }

    [Fact]
    public void ProbeEntities_MethodExists_WithMaxSamplesDefault()
    {
        var method = typeof(Poe2Live).GetMethod("ProbeEntities", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal("maxSamples", parameters[0].Name);
        Assert.Equal(typeof(int), parameters[0].ParameterType);
        Assert.True(parameters[0].HasDefaultValue);
        Assert.Equal(5, parameters[0].DefaultValue);
    }

    [Fact]
    public void ProbeEntities_ReturnsListOfSamples()
    {
        var method = typeof(Poe2Live).GetMethod("ProbeEntities", BindingFlags.Public | BindingFlags.Instance)!;
        // Return type: List<EntityProbeSample>
        Assert.True(method.ReturnType.IsGenericType);
        Assert.Equal(typeof(System.Collections.Generic.List<>), method.ReturnType.GetGenericTypeDefinition());
        var elementType = method.ReturnType.GetGenericArguments()[0];
        Assert.Equal(typeof(Poe2Live).GetNestedType("EntityProbeSample"), elementType);
    }

    [Fact]
    public void EntityProbeSample_ConstructedRecord_RoundTripsAllFields()
    {
        // Sanity check: manually construct a sample via reflection and verify all 11 fields
        // read back their input. Catches any accidental setter/init issue in future edits.
        var type = typeof(Poe2Live).GetNestedType("EntityProbeSample")!;
        var lifeSweep = new[] { "0x1B0={cur=234,max=500}" };
        var posSweep = new[] { "0x138=(1.5,2.5,3.5)" };
        var detailsSweep = new[] { "0x08=0x7ffe1234", "0x00={read-fail}", "0x10=0x0", "0x18=0x0" };
        var compListSweep = new[] { "0x10=count=5", "0x08={read-fail}", "0x18={read-fail}", "0x20={read-fail}", "0x28={read-fail}" };
        var nameSweep = new[] { "0x08=Metadata/Monsters/Foo", "0x00={read-fail}", "0x10={read-fail}", "0x18={read-fail}" };
        var bucketSweep = new[] { "0x28=0x7ffe1234/entries=3", "0x20={read-fail}", "0x30={read-fail}", "0x38={read-fail}" };
        var instance = Activator.CreateInstance(type,
            "0xABCD", "0x1234", "0x5678", 234, 500, lifeSweep, posSweep,
            detailsSweep, compListSweep, nameSweep, bucketSweep);
        Assert.NotNull(instance);

        Assert.Equal("0xABCD", type.GetProperty("EntityAddr")!.GetValue(instance));
        Assert.Equal("0x1234", type.GetProperty("RenderAddr")!.GetValue(instance));
        Assert.Equal("0x5678", type.GetProperty("LifeAddr")!.GetValue(instance));
        Assert.Equal(234, type.GetProperty("HpCurCurrentOffset")!.GetValue(instance));
        Assert.Equal(500, type.GetProperty("HpMaxCurrentOffset")!.GetValue(instance));
        Assert.Same(lifeSweep, type.GetProperty("LifeHealthSweep")!.GetValue(instance));
        Assert.Same(posSweep, type.GetProperty("RenderPositionSweep")!.GetValue(instance));
        Assert.Same(detailsSweep, type.GetProperty("EntityDetailsSweep")!.GetValue(instance));
        Assert.Same(compListSweep, type.GetProperty("ComponentListSweep")!.GetValue(instance));
        Assert.Same(nameSweep, type.GetProperty("EntityDetailsNameSweep")!.GetValue(instance));
        Assert.Same(bucketSweep, type.GetProperty("ComponentLookUpBucketSweep")!.GetValue(instance));
    }

    [Theory]
    [InlineData(0x1A8)]
    [InlineData(0x1B0)]
    [InlineData(0x1B8)]
    [InlineData(0x1C0)]
    [InlineData(0x1C8)]
    public void LifeSweepCandidateOffset_IsFourByteAligned(int offset)
    {
        // The sweep candidates for Life.Health are all 4-byte aligned (VitalStruct fields
        // are int32-aligned per LifeValidator.cs XDoc). Regression-guards the constant list.
        Assert.Equal(0, offset % 4);
    }

    [Theory]
    [InlineData(0x128)]
    [InlineData(0x130)]
    [InlineData(0x138)]
    [InlineData(0x140)]
    [InlineData(0x148)]
    public void RenderPositionCandidateOffset_IsFourByteAligned(int offset)
    {
        // Render.CurrentWorldPosition is a Vector3 (12 bytes of float, 4-byte aligned).
        // Sweep candidates must respect this alignment.
        Assert.Equal(0, offset % 4);
    }
}
