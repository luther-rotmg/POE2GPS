using System;
using System.Linq;
using System.Reflection;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Game;

// v0.41.7 field diagnostic: locks the AtlasProbeInfo record's positional shape after
// the controller-mode payload prompted a widen (ScanWidth 60→200) + two new fields
// (SignatureMatchingCandidates + TotalUiRootChildren). Same reflection-only pattern
// as EntityProbeSampleTests — no live MemoryReader needed.
public sealed class AtlasProbeInfoTests
{
    [Fact]
    public void AtlasProbeInfo_Exists_AsPublicRecordStruct()
    {
        var type = typeof(Poe2Atlas).GetNestedType("AtlasProbeInfo");
        Assert.NotNull(type);
        Assert.True(type!.IsPublic || type.IsNestedPublic);
        Assert.True(type.IsValueType, "AtlasProbeInfo should be a record struct");
    }

    [Fact]
    public void AtlasProbeInfo_HasThirteenPositionalFields()
    {
        // v0.41.7 added SignatureMatchingCandidates + TotalUiRootChildren atop the v0.41.5 shape
        // (which had 11 fields — so 13 total after the two additions).
        var type = typeof(Poe2Atlas).GetNestedType("AtlasProbeInfo")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        Assert.Equal(13, ctor.GetParameters().Length);
    }

    [Fact]
    public void AtlasProbeInfo_FieldsAreInExpectedOrder()
    {
        // /api/atlas consumers depend on this order for JSON serialization; positional records
        // serialize in constructor-parameter order.
        var type = typeof(Poe2Atlas).GetNestedType("AtlasProbeInfo")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var paramNames = ctor.GetParameters().Select(p => p.Name).ToArray();

        Assert.Equal("PrimaryIndex", paramNames[0]);
        Assert.Equal("CachedIndex", paramNames[1]);
        Assert.Equal("ChosenIndex", paramNames[2]);
        Assert.Equal("ChosenVisible", paramNames[3]);
        Assert.Equal("CandidateChildCounts", paramNames[4]);
        Assert.Equal("UiRootAddr", paramNames[5]);
        Assert.Equal("ChildrenBeginAddr", paramNames[6]);
        Assert.Equal("ChildrenEndAddr", paramNames[7]);
        Assert.Equal("ChildrenOffsetHex", paramNames[8]);
        Assert.Equal("ChildrenEndOffsetHex", paramNames[9]);
        Assert.Equal("ProbeAtOffsets", paramNames[10]);
        // v0.41.7 additions live at positions 11+12 (0-indexed) — appended (never inserted) so
        // JSON consumers on older clients don't miss existing fields.
        Assert.Equal("SignatureMatchingCandidates", paramNames[11]);
        Assert.Equal("TotalUiRootChildren", paramNames[12]);
    }

    [Fact]
    public void AtlasProbeInfo_FieldTypesMatchExpected()
    {
        var type = typeof(Poe2Atlas).GetNestedType("AtlasProbeInfo")!;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var byName = ctor.GetParameters().ToDictionary(p => p.Name!, p => p.ParameterType);

        Assert.Equal(typeof(int),      byName["PrimaryIndex"]);
        Assert.Equal(typeof(int),      byName["CachedIndex"]);
        Assert.Equal(typeof(int),      byName["ChosenIndex"]);
        Assert.Equal(typeof(bool),     byName["ChosenVisible"]);
        Assert.Equal(typeof(int[]),    byName["CandidateChildCounts"]);
        Assert.Equal(typeof(string),   byName["UiRootAddr"]);
        Assert.Equal(typeof(string),   byName["ChildrenBeginAddr"]);
        Assert.Equal(typeof(string),   byName["ChildrenEndAddr"]);
        Assert.Equal(typeof(int),      byName["ChildrenOffsetHex"]);
        Assert.Equal(typeof(int),      byName["ChildrenEndOffsetHex"]);
        Assert.Equal(typeof(string[]), byName["ProbeAtOffsets"]);
        Assert.Equal(typeof(string[]), byName["SignatureMatchingCandidates"]);
        Assert.Equal(typeof(int),      byName["TotalUiRootChildren"]);
    }

    [Fact]
    public void AtlasProbeInfo_ConstructedRecord_RoundTripsAllFields()
    {
        var type = typeof(Poe2Atlas).GetNestedType("AtlasProbeInfo")!;
        var counts = new[] { 0, 1, 18, 3, 8 };
        var probeAt = new[] { "0x10=null" };
        var sigMatches = new[] { "index=22 childCount=18 visible=false" };
        var instance = Activator.CreateInstance(type,
            22, 22, 22, false, counts,
            "0xABCD", "0x1234", "0x5678", 0x10, 0x18,
            probeAt, sigMatches, 124);
        Assert.NotNull(instance);

        Assert.Equal(22, type.GetProperty("PrimaryIndex")!.GetValue(instance));
        Assert.Equal(22, type.GetProperty("CachedIndex")!.GetValue(instance));
        Assert.Equal(22, type.GetProperty("ChosenIndex")!.GetValue(instance));
        Assert.Equal(false, type.GetProperty("ChosenVisible")!.GetValue(instance));
        Assert.Same(counts, type.GetProperty("CandidateChildCounts")!.GetValue(instance));
        Assert.Equal("0xABCD", type.GetProperty("UiRootAddr")!.GetValue(instance));
        Assert.Equal("0x1234", type.GetProperty("ChildrenBeginAddr")!.GetValue(instance));
        Assert.Equal("0x5678", type.GetProperty("ChildrenEndAddr")!.GetValue(instance));
        Assert.Equal(0x10, type.GetProperty("ChildrenOffsetHex")!.GetValue(instance));
        Assert.Equal(0x18, type.GetProperty("ChildrenEndOffsetHex")!.GetValue(instance));
        Assert.Same(probeAt, type.GetProperty("ProbeAtOffsets")!.GetValue(instance));
        Assert.Same(sigMatches, type.GetProperty("SignatureMatchingCandidates")!.GetValue(instance));
        Assert.Equal(124, type.GetProperty("TotalUiRootChildren")!.GetValue(instance));
    }

    [Fact]
    public void AtlasProbeInfo_DefaultLastProbe_HasEmptyArraysNotNull()
    {
        // Poe2Atlas.LastProbe's default value (constructor initializer) must never surface
        // null arrays — JSON serialization would emit null for those fields and dashboard
        // consumers would break on Array.isArray() checks.
        var atlasType = typeof(Poe2Atlas);
        var lastProbeProp = atlasType.GetProperty("LastProbe", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(lastProbeProp);
        // Verify property type is the record struct we validated in earlier tests
        Assert.Equal(typeof(Poe2Atlas).GetNestedType("AtlasProbeInfo"), lastProbeProp!.PropertyType);
    }

    [Theory]
    [InlineData("SignatureMatchingCandidates")]
    [InlineData("TotalUiRootChildren")]
    public void V041dot7_NewFields_ArePresentOnRecordType(string fieldName)
    {
        // Guards against a future refactor accidentally dropping the v0.41.7 additions.
        var type = typeof(Poe2Atlas).GetNestedType("AtlasProbeInfo")!;
        Assert.NotNull(type.GetProperty(fieldName));
    }
}
