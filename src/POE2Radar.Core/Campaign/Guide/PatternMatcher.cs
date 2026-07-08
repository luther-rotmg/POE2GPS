// Ported from ExileCampaigns2 by syrairc under TODO(syrairc-license) — upstream commit TODO(syrairc-hash)
using System;
using System.Text.RegularExpressions;

namespace POE2Radar.Core.Campaign.Guide;

// compiles a Pattern once and matches candidates. literal = case-insensitive substring; regex = compiled
// IgnoreCase regex. invalid regex falls back to literal and exposes Error for a diagnostic.
public sealed class PatternMatcher
{
    private readonly Pattern _pattern;
    private readonly Regex? _regex;
    public string? Error { get; }

    public PatternMatcher(Pattern pattern)
    {
        _pattern = pattern ?? new Pattern("");
        if (_pattern.Regex && !string.IsNullOrEmpty(_pattern.Value))
        {
            try { _regex = new Regex(_pattern.Value, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch (ArgumentException ex) { _regex = null; Error = ex.Message; }
        }
    }

    public bool IsMatch(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (_regex != null) return _regex.IsMatch(candidate);
        if (string.IsNullOrEmpty(_pattern.Value)) return false;
        return candidate.IndexOf(_pattern.Value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
