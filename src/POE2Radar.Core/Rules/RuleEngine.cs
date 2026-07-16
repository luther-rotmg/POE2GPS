using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace POE2Radar.Core.Rules;

/// <summary>
/// Pre-compiled regex slots for a single rule. Regex predicates are compiled once at
/// <see cref="RuleEngine.Compile"/> time; <see cref="CompiledRuleSet.TryMatch"/> only ever
/// calls <see cref="Regex.IsMatch"/> on these pre-built instances (guaranteed throw-free).
/// </summary>
internal sealed record CompiledRule(
    RuleRecord Source,
    Regex? MetadataRegex,
    Regex? TokenRegex,
    Regex? ZoneCodeRegex,
    Regex? HasBuffRegex);

/// <summary>
/// Immutable, thread-safe set of pre-compiled rules. <see cref="TryMatch"/> is guaranteed
/// never to throw — all regex validation happens at <see cref="RuleEngine.Compile"/> time.
/// </summary>
public sealed class CompiledRuleSet
{
    // Rules pre-sorted by descending Priority at Compile time. Disabled rules are retained
    // so a runtime toggle can flip them without forcing a recompile.
    private readonly IReadOnlyList<CompiledRule> _rules;

    internal CompiledRuleSet(IReadOnlyList<CompiledRule> rules)
    {
        _rules = rules;
    }

    /// <summary>Total number of compiled rules (including disabled ones).</summary>
    public int RuleCount => _rules.Count;

    /// <summary>
    /// Returns the concatenated <see cref="Effect"/> lists from ALL matching enabled rules,
    /// in priority-desc order (higher-priority rules' effects appear earlier in the output).
    /// Never throws. An empty ruleset or no matches returns <see cref="Array.Empty{T}"/>.
    /// </summary>
    public IReadOnlyList<Effect> TryMatch(EntityView entity, WorldSnapshotView snap)
    {
        if (_rules.Count == 0)
            return Array.Empty<Effect>();

        List<Effect>? result = null;

        // Iterate in pre-sorted priority-desc order. Any exception is swallowed to honor the
        // throw-free contract (regexes are pre-validated, so this is defensive only).
        for (int i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (!rule.Source.Enabled)
                continue;

            bool matches;
            try
            {
                matches = Matches(rule, entity, snap);
            }
            catch
            {
                matches = false;
            }

            if (!matches)
                continue;

            var then = rule.Source.Then;
            if (then == null || then.Count == 0)
                continue;

            result ??= new List<Effect>();
            result.AddRange(then);
        }

        return result is null ? Array.Empty<Effect>() : result;
    }

    private static bool Matches(CompiledRule rule, EntityView entity, WorldSnapshotView snap)
    {
        var sel = rule.Source.When;

        // Metadata regex
        if (rule.MetadataRegex is not null && !rule.MetadataRegex.IsMatch(entity.Metadata))
            return false;

        // Token regex
        if (rule.TokenRegex is not null && !rule.TokenRegex.IsMatch(entity.Token))
            return false;

        // Rarity — case-insensitive equality
        if (sel.Rarity is not null &&
            !string.Equals(sel.Rarity, entity.Rarity, StringComparison.OrdinalIgnoreCase))
            return false;

        // ZoneCode regex
        if (rule.ZoneCodeRegex is not null && !rule.ZoneCodeRegex.IsMatch(snap.ZoneCode))
            return false;

        // InHideout
        if (sel.InHideout.HasValue && snap.InHideout != sel.InHideout.Value)
            return false;

        // MinLevel (inclusive)
        if (sel.MinLevel.HasValue && entity.Level < sel.MinLevel.Value)
            return false;

        // MaxLevel (inclusive)
        if (sel.MaxLevel.HasValue && entity.Level > sel.MaxLevel.Value)
            return false;

        // HasBuff — regex against any buff name, short-circuit on first match
        if (rule.HasBuffRegex is not null)
        {
            var buffs = entity.Buffs;
            if (buffs is null)
                return false;
            bool found = false;
            foreach (var buff in buffs)
            {
                if (rule.HasBuffRegex.IsMatch(buff))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }

        return true;
    }
}

/// <summary>
/// Compiles a <see cref="RulesFile"/> into a throw-free <see cref="CompiledRuleSet"/>.
/// Regex predicates are pre-compiled with <see cref="RegexOptions.Compiled"/> |
/// <see cref="RegexOptions.IgnoreCase"/> | <see cref="RegexOptions.CultureInvariant"/>.
/// Invalid regex throws <see cref="ArgumentException"/> naming the rule and predicate.
/// </summary>
public static class RuleEngine
{
    private const RegexOptions RegexOpts =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// A ruleset with no rules. <see cref="CompiledRuleSet.TryMatch"/> always returns an
    /// empty list. Cached singleton.
    /// </summary>
    public static CompiledRuleSet Empty { get; } = new CompiledRuleSet(Array.Empty<CompiledRule>());

    /// <summary>
    /// Compile <paramref name="file"/> into a <see cref="CompiledRuleSet"/>. Rules are
    /// sorted by descending <see cref="RuleRecord.Priority"/>. Disabled rules are retained
    /// (skipped at match time). Throws <see cref="ArgumentException"/> on any regex parse
    /// failure, with a message naming the rule and the offending predicate field.
    /// </summary>
    public static CompiledRuleSet Compile(RulesFile file)
    {
        if (file.Rules == null || file.Rules.Count == 0)
            return Empty;

        var compiled = new List<CompiledRule>(file.Rules.Count);
        foreach (var rule in file.Rules)
        {
            var when = rule.When;
            Regex? metadataRegex = CompileRegex(rule, when?.Metadata, nameof(Selector.Metadata));
            Regex? tokenRegex = CompileRegex(rule, when?.Token, nameof(Selector.Token));
            Regex? zoneCodeRegex = CompileRegex(rule, when?.ZoneCode, nameof(Selector.ZoneCode));
            Regex? hasBuffRegex = CompileRegex(rule, when?.HasBuff, nameof(Selector.HasBuff));

            compiled.Add(new CompiledRule(rule, metadataRegex, tokenRegex, zoneCodeRegex, hasBuffRegex));
        }

        // Sort by priority descending (stable enough — ties keep insertion order via List sort).
        compiled.Sort((a, b) => b.Source.Priority.CompareTo(a.Source.Priority));

        return new CompiledRuleSet(compiled);
    }

    private static Regex? CompileRegex(RuleRecord rule, string? pattern, string predicateField)
    {
        if (string.IsNullOrEmpty(pattern))
            return null;

        try
        {
            return new Regex(pattern, RegexOpts);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"Invalid regex in rule '{rule.Name}' predicate '{predicateField}': {ex.Message}",
                ex);
        }
    }
}
