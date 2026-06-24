using Vector2 = System.Numerics.Vector2;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObjectiveTier
{
    SeasonalEvent = 4,
    SideBoss      = 3,
    Bonus         = 2,
    SideZone      = 1,
    Exit          = 0,
}

/// <summary>
/// One catalog objective: a MATCHER over the live entity/landmark data the radar already reads,
/// plus a <see cref="Priority"/> (higher = routed first) and a <see cref="Category"/> label.
/// Matcher fields are ANY-of; an empty/null field is "any". Entity objectives set
/// <see cref="Metadata"/>/<see cref="Categories"/>/<see cref="Poi"/>/<see cref="Rarity"/>; tile
/// objectives set <see cref="LandmarkPath"/>. JSON-serialized (camelCase) by the store.
/// </summary>
public sealed record CampaignObjective(
    string Id,
    string Label,
    string Category,
    int Priority,
    ObjectiveTier? Tier = null,
    bool Enabled = true,
    List<string>? Metadata = null,    // entity metadata terms (substring, or glob if it has * / ?)
    List<string>? Categories = null,  // EntityCategory names
    string? Poi = null,               // "Yes" | "No"
    string? Rarity = null,            // Normal | Magic | Rare | Unique
    List<string>? LandmarkPath = null // terrain-tile .tdt path terms (substring/glob)
)
{
    public static ObjectiveTier DefaultTierForCategory(string? category) =>
        category switch
        {
            "League"          => ObjectiveTier.SeasonalEvent,
            "SideBoss"        => ObjectiveTier.SideBoss,
            "SideZone"        => ObjectiveTier.SideZone,
            "MainProgression" => ObjectiveTier.Exit,
            _                 => ObjectiveTier.Exit,
        };
}

/// <summary>A matched, rankable objective in the current zone. <see cref="Id"/> is the stable
/// nav-selection id ("e:&lt;entityId&gt;" / "t:&lt;landmarkKey&gt;").</summary>
public readonly record struct RankedObjective(string Id, string Label, string Category, int Priority, ObjectiveTier Tier, float DistanceSq);

/// <summary>
/// Compiles a set of <see cref="CampaignObjective"/>s and ranks the ones present in the current
/// zone. Pure + allocation-free per-entity matching (mirrors <c>DisplayRules.Compiled</c>): the
/// per-tick allocation is only the small result list. Highest priority first, nearest as tiebreak.
/// </summary>
public sealed class ObjectiveCatalog
{
    private readonly Compiled[] _compiled;

    public ObjectiveCatalog(IEnumerable<CampaignObjective> objectives)
        => _compiled = objectives.Where(o => o.Enabled).Select(o => new Compiled(o)).ToArray();

    public IReadOnlyList<RankedObjective> Rank(
        IReadOnlyList<Poe2Live.EntityDot> entities,
        IReadOnlyList<Poe2Live.Landmark> landmarks,
        Vector2 player)
    {
        var best = new Dictionary<string, RankedObjective>(StringComparer.Ordinal);

        for (var i = 0; i < entities.Count; i++)
        {
            ref readonly var e = ref AsRef(entities, i);
            Compiled? top = null;
            foreach (var c in _compiled)
                if (c.MatchesEntity(in e) && (top is null || c.Obj.Priority > top.Obj.Priority))
                    top = c;
            if (top is null) continue;
            var id = "e:" + e.Id;
            Consider(best, id, top.Obj, Vector2.DistanceSquared(e.Grid, player));
        }

        for (var i = 0; i < landmarks.Count; i++)
        {
            var lm = landmarks[i];
            Compiled? top = null;
            foreach (var c in _compiled)
                if (c.MatchesLandmark(lm.Path) && (top is null || c.Obj.Priority > top.Obj.Priority))
                    top = c;
            if (top is null) continue;
            var id = "t:" + lm.Key;
            Consider(best, id, top.Obj, Vector2.DistanceSquared(lm.Center, player));
        }

        var list = new List<RankedObjective>(best.Values);
        list.Sort((a, b) =>
        {
            int tc = ((int)b.Tier).CompareTo((int)a.Tier);   // tier desc
            if (tc != 0) return tc;
            int pc = b.Priority.CompareTo(a.Priority);        // priority desc
            if (pc != 0) return pc;
            return a.DistanceSq.CompareTo(b.DistanceSq);      // nearest asc
        });
        return list;
    }

    /// <summary>True if any enabled objective matches this entity (reuses the compiled matcher).</summary>
    public bool Covers(in Poe2Live.EntityDot e)
    {
        foreach (var c in _compiled)
            if (c.MatchesEntity(in e)) return true;
        return false;
    }

    /// <summary>True if any enabled objective matches this terrain-tile landmark path.</summary>
    public bool Covers(string landmarkPath)
    {
        foreach (var c in _compiled)
            if (c.MatchesLandmark(landmarkPath)) return true;
        return false;
    }

    /// <summary>True if any enabled objective would route to this logged candidate. Tile entries
    /// match by path; entity entries match via a synthetic <see cref="Poe2Live.EntityDot"/> carrying
    /// the catalog-relevant fields (category/metadata/poi/rarity).</summary>
    public bool Covers(SeenPoi p)
    {
        if (p.LandmarkPath is { Length: > 0 } path) return Covers(path);
        var cat = Enum.TryParse<Poe2Live.EntityCategory>(p.Category, ignoreCase: true, out var c)
            ? c : Poe2Live.EntityCategory.Other;
        var rar = Enum.TryParse<Poe2Live.Rarity>(p.Rarity, ignoreCase: true, out var r)
            ? r : Poe2Live.Rarity.NonMonster;
        var e = new Poe2Live.EntityDot(0, 0, default, default, cat, p.Metadata ?? "", 0, 0, p.Poi, 0, rar, false);
        return Covers(in e);
    }

    private static void Consider(Dictionary<string, RankedObjective> best, string id, CampaignObjective o, float distSq)
    {
        if (best.TryGetValue(id, out var cur) && cur.Priority >= o.Priority) return;
        best[id] = new RankedObjective(
            id, o.Label, o.Category, o.Priority,
            o.Tier ?? CampaignObjective.DefaultTierForCategory(o.Category),
            distSq);
    }

    // Index a List/IReadOnlyList by ref without copying the (largish) EntityDot struct per access.
    private static ref readonly Poe2Live.EntityDot AsRef(IReadOnlyList<Poe2Live.EntityDot> list, int i)
    {
        if (list is List<Poe2Live.EntityDot> l)
            return ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(l)[i];
        _scratch = list[i];
        return ref _scratch;
    }

    [ThreadStatic] private static Poe2Live.EntityDot _scratch;

    private sealed class Compiled
    {
        public readonly CampaignObjective Obj;
        private readonly bool _anyCat;
        private readonly bool[] _catMask = new bool[7]; // EntityCategory has 7 members
        private readonly (string sub, Regex? glob)[]? _meta;
        private readonly bool _anyRarity;
        private readonly Poe2Live.Rarity _rarity;
        private readonly int _poi; // 0 any / 1 Yes / 2 No
        private readonly (string sub, Regex? glob)[]? _landmark;
        private readonly bool _hasEntityMatcher;

        public Compiled(CampaignObjective o)
        {
            Obj = o;
            _anyCat = o.Categories is not { Count: > 0 };
            if (!_anyCat)
                foreach (var c in o.Categories!)
                    if (Enum.TryParse<Poe2Live.EntityCategory>(c, ignoreCase: true, out var ec)) _catMask[(int)ec] = true;
            _meta = Compile(o.Metadata);
            _anyRarity = string.IsNullOrEmpty(o.Rarity);
            _rarity = _anyRarity ? default
                : Enum.TryParse<Poe2Live.Rarity>(o.Rarity, ignoreCase: true, out var rr) ? rr : (Poe2Live.Rarity)int.MaxValue;
            _poi = string.IsNullOrEmpty(o.Poi) ? 0
                 : string.Equals(o.Poi, "Yes", StringComparison.OrdinalIgnoreCase) ? 1
                 : string.Equals(o.Poi, "No", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            _landmark = Compile(o.LandmarkPath);
            _hasEntityMatcher = _meta != null || !_anyCat || _poi != 0 || !_anyRarity;
        }

        public bool MatchesEntity(in Poe2Live.EntityDot e)
        {
            if (!_hasEntityMatcher) return false; // landmark-only objective never matches an entity
            if (!_anyCat) { var ci = (int)e.Category; if ((uint)ci >= (uint)_catMask.Length || !_catMask[ci]) return false; }
            if (_meta != null && !Any(_meta, e.Metadata)) return false;
            if (!_anyRarity && e.Rarity != _rarity) return false;
            if (_poi == 1 && !e.Poi) return false;
            if (_poi == 2 && e.Poi) return false;
            return true;
        }

        public bool MatchesLandmark(string path) => _landmark != null && Any(_landmark, path);

        private static (string, Regex?)[]? Compile(List<string>? terms)
        {
            if (terms is not { Count: > 0 }) return null;
            var arr = terms.Where(t => !string.IsNullOrEmpty(t)).Select(CompileTerm).ToArray();
            return arr.Length == 0 ? null : arr;
        }

        private static (string, Regex?) CompileTerm(string term)
        {
            if (term.IndexOf('*') < 0 && term.IndexOf('?') < 0) return (term, null);
            var rx = "^" + Regex.Escape(term).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return (term, new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        private static bool Any((string sub, Regex? glob)[] terms, string value)
        {
            foreach (var (sub, glob) in terms)
            {
                if (glob != null) { if (glob.IsMatch(value)) return true; }
                else if (value.Contains(sub, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
