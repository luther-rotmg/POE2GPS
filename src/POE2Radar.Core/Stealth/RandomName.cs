namespace POE2Radar.Core.Stealth;

/// <summary>
/// Generates a pronounceable, identity-neutral random name (consonant/vowel alternation, 1-2 words).
/// Used to launch the overlay under a random hardlink name and to name the overlay window.
/// </summary>
public static class RandomName
{
    private const string Vowels = "eioau";
    private const string Consonants = "bcdfghjklmnpqrstvwxyz";

    public static string Generate(Random? rng = null)
    {
        rng ??= Random.Shared;
        var parts = new List<string>();
        for (var w = 0; w < rng.Next(1, 3); w++)
        {
            var len = rng.Next(5, 9);
            var ch = new char[len];
            for (var i = 0; i < len; i++)
                ch[i] = (i % 2 == 0) ? Consonants[rng.Next(Consonants.Length)] : Vowels[rng.Next(Vowels.Length)];
            ch[0] = char.ToUpper(ch[0]);
            parts.Add(new string(ch));
        }
        return string.Join("", parts);
    }
}
