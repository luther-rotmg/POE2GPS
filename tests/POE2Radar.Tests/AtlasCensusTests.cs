using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

public class AtlasCensusTests
{
    private static Poe2Live.EntityDot Dot(string metadata,
        Poe2Live.EntityCategory cat = Poe2Live.EntityCategory.Monster)
        => new(0, 0, default, default, cat, metadata, 0, 0, false, 0, Poe2Live.Rarity.Normal, false);

    [Fact] public void Keeps_Monster()
        => Assert.True(AtlasCensus.IsCensusEntity(Dot("Metadata/Monsters/Wraith/Wraith1")));

    [Fact] public void Keeps_Npc()
        => Assert.True(AtlasCensus.IsCensusEntity(Dot("Metadata/NPC/Act1/Una", Poe2Live.EntityCategory.Npc)));

    [Fact] public void Skips_Player()
        => Assert.False(AtlasCensus.IsCensusEntity(Dot("Metadata/Player", Poe2Live.EntityCategory.Player)));

    [Fact] public void Skips_Junk_Fx()
        => Assert.False(AtlasCensus.IsCensusEntity(Dot("Metadata/Effects/fx/Spell/Foo")));

    [Fact] public void Skips_Junk_Daemon()
        => Assert.False(AtlasCensus.IsCensusEntity(Dot("Metadata/Monsters/Daemon/SomeDaemon")));

    [Fact] public void Skips_EmptyMetadata()
        => Assert.False(AtlasCensus.IsCensusEntity(Dot("")));

    [Fact] public void Signature_StripsLevelSuffix()
        => Assert.Equal("Metadata/Monsters/Wraith/Wraith1",
                        AtlasCensus.Signature(Dot("Metadata/Monsters/Wraith/Wraith1@34")));

    [Fact] public void Signature_DedupsAcrossLevels()
        => Assert.Equal(AtlasCensus.Signature(Dot("Metadata/M/Foo@34")),
                        AtlasCensus.Signature(Dot("Metadata/M/Foo@45")));
}
