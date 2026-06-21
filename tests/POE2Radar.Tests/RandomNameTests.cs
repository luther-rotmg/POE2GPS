using POE2Radar.Core.Stealth;

public class RandomNameTests
{
    [Fact]
    public void Generate_IsLettersOnly_AndReasonableLength()
    {
        var n = RandomName.Generate(new Random(1));
        Assert.InRange(n.Length, 5, 16);
        Assert.All(n, c => Assert.True(char.IsLetter(c)));
    }

    [Fact]
    public void Generate_VariesAcrossCalls()
    {
        var rng = new Random(2);
        var a = RandomName.Generate(rng);
        var b = RandomName.Generate(rng);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Generate_ContainsNoIdentifyingTokens()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var n = RandomName.Generate(new Random(seed));
            foreach (var bad in new[] { "poe", "radar", "sikaka", "nattkh" })
                Assert.DoesNotContain(bad, n, StringComparison.OrdinalIgnoreCase);
        }
    }
}
