using System.Text.Json;
using POE2Radar.Core.Session;
using Xunit;

namespace POE2Radar.Tests.Core;

public class CodexEventTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void LevelUpEvent_HasCorrectKind()
    {
        var e = new LevelUpEvent(1000, 0xdeadbeef, "MapMesa", 84);
        Assert.Equal("level", e.Kind);
        Assert.Equal(84, e.Level);
    }

    [Fact]
    public void BossKillEvent_HasCorrectKind()
    {
        var e = new BossKillEvent(2000, 0x12345678, "MapUberBoss_Kitava", "kitava", "Kitava");
        Assert.Equal("boss", e.Kind);
        Assert.Equal("kitava", e.BossKey);
    }

    [Fact]
    public void DeathEvent_HasCorrectKind()
    {
        var e = new DeathEvent(3000, 0xabcdef01, "T16 Colosseum", 84, 96);
        Assert.Equal("death", e.Kind);
        Assert.Equal(96, e.PlayerLevel);
    }

    [Fact]
    public void NotableDropEvent_HasCorrectKind()
    {
        var e = new NotableDropEvent(4000, 0xfeed, "MapCanal", "Headhunter", "Unique", "hh-belt");
        Assert.Equal("drop", e.Kind);
        Assert.Equal("Headhunter", e.Name);
    }

    [Fact]
    public void Roundtrip_LevelUp_PolymorphicSerialization()
    {
        CodexEvent src = new LevelUpEvent(1000, 0xdeadbeef, "MapMesa", 84);
        var json = JsonSerializer.Serialize(src, Json);
        Assert.Contains("\"kind\":\"level\"", json);
        var back = JsonSerializer.Deserialize<CodexEvent>(json, Json);
        var lvl = Assert.IsType<LevelUpEvent>(back);
        Assert.Equal(84, lvl.Level);
        Assert.Equal(1000, lvl.Ts);
        Assert.Equal("MapMesa", lvl.Zone);
    }

    [Fact]
    public void Roundtrip_BossKill_PolymorphicSerialization()
    {
        CodexEvent src = new BossKillEvent(2000, 0x12345678, "MapUberBoss_Kitava", "kitava", "Kitava");
        var json = JsonSerializer.Serialize(src, Json);
        Assert.Contains("\"kind\":\"boss\"", json);
        var back = JsonSerializer.Deserialize<CodexEvent>(json, Json);
        var boss = Assert.IsType<BossKillEvent>(back);
        Assert.Equal("kitava", boss.BossKey);
        Assert.Equal("Kitava", boss.BossLabel);
    }

    [Fact]
    public void Roundtrip_Death_PolymorphicSerialization()
    {
        CodexEvent src = new DeathEvent(3000, 0xabcdef01, "T16", 84, 96);
        var json = JsonSerializer.Serialize(src, Json);
        Assert.Contains("\"kind\":\"death\"", json);
        var back = JsonSerializer.Deserialize<CodexEvent>(json, Json);
        var death = Assert.IsType<DeathEvent>(back);
        Assert.Equal(96, death.PlayerLevel);
        Assert.Equal(84, death.AreaLevel);
    }

    [Fact]
    public void Roundtrip_NotableDrop_PolymorphicSerialization()
    {
        CodexEvent src = new NotableDropEvent(4000, 0xfeed, "MapCanal", "Headhunter", "Unique", "hh-belt");
        var json = JsonSerializer.Serialize(src, Json);
        Assert.Contains("\"kind\":\"drop\"", json);
        var back = JsonSerializer.Deserialize<CodexEvent>(json, Json);
        var drop = Assert.IsType<NotableDropEvent>(back);
        Assert.Equal("Headhunter", drop.Name);
        Assert.Equal("hh-belt", drop.Art);
    }

    [Fact]
    public void NotableDrop_NullArt_Roundtrips()
    {
        CodexEvent src = new NotableDropEvent(5000, 0x1, "Zone", "Old-item", "Rare", null);
        var json = JsonSerializer.Serialize(src, Json);
        var back = Assert.IsType<NotableDropEvent>(JsonSerializer.Deserialize<CodexEvent>(json, Json));
        Assert.Null(back.Art);
    }

    [Fact]
    public void SerializedShape_IsSingleLine_ForJsonlAppendability()
    {
        // JSONL contract: one event = one line, no embedded newlines.
        CodexEvent src = new BossKillEvent(1, 2, "Zone", "key", "Label");
        var json = JsonSerializer.Serialize(src, Json);
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
    }
}
