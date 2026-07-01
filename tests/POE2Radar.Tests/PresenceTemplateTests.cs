using POE2Radar.Core.Presence;

public class PresenceTemplateTests
{
    static readonly Dictionary<string,string> Toks = new()
        { ["area"]="The Twilight Strand", ["level"]="92", ["mapshr"]="4.2", ["kills"]="137" };

    [Fact] public void Fills_known_tokens()
        => Assert.Equal("The Twilight Strand · Lvl 92",
             PresenceTemplate.Format("{area} · Lvl {level}", Toks));
    [Fact] public void Unknown_token_becomes_empty()
        => Assert.Equal("x  y", PresenceTemplate.Format("x {nope} y", Toks));
    [Fact] public void Clamps_to_128_chars()
    {
        var t = PresenceTemplate.Format(new string('a', 200), Toks);
        Assert.Equal(128, t.Length);
    }
    [Fact] public void Null_or_empty_template_is_empty()
    {
        Assert.Equal("", PresenceTemplate.Format("", Toks));
        Assert.Equal("", PresenceTemplate.Format(null!, Toks));
    }
}
