using System.Text;
namespace POE2Radar.Core.Presence;

/// <summary>Fills <c>{token}</c> placeholders in a user presence template from a token map, and clamps
/// the result to Discord's 128-char per-line limit. Pure; unknown tokens resolve to empty. No I/O.</summary>
public static class PresenceTemplate
{
    public static string Format(string template, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var sb = new StringBuilder(template.Length);
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] == '{')
            {
                int close = template.IndexOf('}', i + 1);
                if (close > i)
                {
                    var key = template.Substring(i + 1, close - i - 1);
                    sb.Append(tokens.TryGetValue(key, out var v) ? v : "");
                    i = close;
                    continue;
                }
            }
            sb.Append(template[i]);
        }
        var s = sb.ToString();
        return s.Length > 128 ? s.Substring(0, 128) : s;
    }
}
