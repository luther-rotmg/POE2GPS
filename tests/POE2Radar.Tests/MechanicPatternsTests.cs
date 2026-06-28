using POE2Radar.Core.Game;
using Xunit;

public class MechanicPatternsTests
{
    [Theory]
    [InlineData("Metadata/Terrain/Leagues/Expedition2/Expedition2Encounter", "Expedition")]
    [InlineData("Metadata/MiscellaneousObjects/Breach/BreachObject", "Breach")]
    [InlineData("Metadata/Chests/StrongBoxes/ArcanistStrongbox", "Strongbox")]
    [InlineData("Metadata/Shrines/LesserShrine", "Shrine")]
    [InlineData("Metadata/MiscellaneousObjects/Ritual/RitualRuntime", "Ritual")]
    [InlineData("Metadata/Chests/Essence/EssenceCage", "Essence")]
    public void classifies_known_mechanics(string meta, string expected) => Assert.Equal(expected, MechanicPatterns.Classify(meta));

    [Theory]
    [InlineData("Metadata/Monsters/Goblin/Goblin")]
    [InlineData("")]
    [InlineData(null)]
    public void unrelated_or_empty_returns_null(string? meta) => Assert.Null(MechanicPatterns.Classify(meta));

    [Fact] public void match_is_case_insensitive() => Assert.Equal("Breach", MechanicPatterns.Classify("metadata/breach/x"));
    [Fact] public void names_lists_all_six() => Assert.Equal(6, MechanicPatterns.Names.Count);
}
