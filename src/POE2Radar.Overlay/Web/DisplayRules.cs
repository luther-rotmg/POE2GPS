using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// One row of the unified display ruleset. A rule is a MATCHER (every set condition must hold;
/// an empty list / null field means "any") plus an ACTION (hide, or draw with a style/label/HP-bar).
/// The ruleset is an ORDERED list evaluated top-down per entity — the first enabled rule that matches
/// decides the entity's fate, so precedence is explicit (list order) and the old watched-vs-mechanic-
/// vs-category conflicts are impossible by construction. Mutable + JSON-serialized (object-initializer
/// shape, like <see cref="MechanicStyle"/>); treated as immutable once in a snapshot.
/// </summary>
public sealed class DisplayRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";

    // ── Matcher (unset = "any") ──
    public List<string> Categories { get; set; } = new();   // EntityCategory names; empty = any
    public List<string> Match { get; set; } = new();        // metadata terms (substring, or glob if it has * / ?); ANY-of; empty = any
    public List<string> Mods { get; set; } = new();         // monster affix-mod terms (e.g. "Aura"); ANY-of vs the entity's mod ids; empty = any
    public string? Rarity { get; set; }                     // Normal | Magic | Rare | Unique
    public string? Reaction { get; set; }                   // Hostile | Friendly
    public string? Life { get; set; }                       // Alive | Dead
    public string? Chest { get; set; }                      // Opened | Unopened
    public string? Poi { get; set; }                        // Yes | No   (game MinimapIcon present)
    public string? Encounter { get; set; }                  // Active | Complete   (IconComplete faded state)

    // ── Action ──
    public bool Hide { get; set; }                          // match → stop, don't draw
    public string Shape { get; set; } = "Circle";
    public string Color { get; set; } = "#FFFFFF";
    public float Opacity { get; set; } = 1f;
    public float Size { get; set; } = 3f;
    public string? Label { get; set; }                      // optional text label drawn next to the dot
    public bool OffScreenArrow { get; set; }                // draw an edge arrow when this entity is off-screen
    public bool Navigable { get; set; }                     // reserved (Phase 2): qualifies as a nav target
}

/// <summary>
/// Ordered display ruleset — the single source of truth for per-entity visibility/icon/label/HP-bar.
/// Modeled on <see cref="WatchedEntities"/> / <see cref="LandmarkPatterns"/>: JSON-persisted, mutated
/// under a lock, read lock-free on the render thread via a volatile precompiled snapshot, with a
/// <see cref="Generation"/> counter so the tick loop notices live edits. <see cref="Resolve"/> is the
/// hot path (called per entity per frame) and must stay allocation-free.
/// </summary>
public sealed class DisplayRules
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private List<DisplayRule> _rules = new();           // under _gate (authoritative, ordered)
    private volatile Compiled[] _snapshot = Array.Empty<Compiled>(); // immutable; lock-free reads
    private volatile int _generation;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // keep the file tidy (omit "any" fields)
    };

    public DisplayRules(string filePath)
    {
        _filePath = filePath;
        Load();
        Rebuild();
    }

    /// <summary>Bumped on every mutation so the tick loop can detect a live edit (same pattern as
    /// <see cref="LandmarkPatterns.Generation"/>). The snapshot itself is already live; this is a marker.</summary>
    public int Generation => _generation;

    /// <summary>Whether any rules are loaded (used to decide if a migration must seed defaults).</summary>
    public int Count { get { lock (_gate) return _rules.Count; } }

    /// <summary>True when at least one enabled rule uses a Mods filter, meaning monster affix-mod reads
    /// are required to evaluate that rule. Lock-free read of the precompiled snapshot.</summary>
    public bool AnyModFilter
    {
        get
        {
            var snap = _snapshot;
            foreach (var c in snap)
                if (c.HasModFilter) return true;
            return false;
        }
    }

    /// <summary>True when at least one enabled rule has a Rarity constraint AND could apply to a
    /// WorldItem (<see cref="Poe2Live.EntityCategory.Other"/>) — meaning item-identity reads are
    /// required to evaluate that rule even when GroundItems and AudioAlertUniqueDrop are both off.
    /// Lock-free read of the precompiled snapshot. O(rules), called once per world-tick.</summary>
    public bool AnyItemRarityFilter
    {
        get
        {
            var snap = _snapshot;
            foreach (var c in snap)
                if (c.HasItemRarityFilter) return true;
            return false;
        }
    }

    /// <summary>All rules in order (snapshot copy; safe to enumerate off-thread / serialize for the API).</summary>
    public IReadOnlyList<DisplayRule> All { get { lock (_gate) return _rules.ToList(); } }

    /// <summary>
    /// The first ENABLED rule that matches the entity, or null if none (→ not drawn). Lock-free hot path:
    /// reads the volatile precompiled snapshot. The returned rule's action fields tell the caller whether
    /// to hide or how to draw.
    /// </summary>
    public DisplayRule? Resolve(Poe2Live.EntityDot e)
    {
        var snap = _snapshot;
        foreach (var c in snap)
            if (c.Matches(in e)) return c.Rule;
        return null;
    }

    /// <summary>
    /// Resolve a terrain TILE path to its first matching enabled rule of the special "Tile" category,
    /// or null. Tiles aren't entities, so a Tile rule matches purely on its <see cref="DisplayRule.Match"/>
    /// terms (path substrings/globs); the other conditions are ignored. <paramref name="requireMatch"/>
    /// distinguishes the two passes: the SURFACING pass (true) only lets a Tile rule with explicit match
    /// terms pull a NEW tile onto the map; the STYLING pass (false) lets an empty-match Tile rule restyle
    /// any already-surfaced landmark. The matched rule's shape/color/size/label/hide then style it.
    /// </summary>
    public DisplayRule? ResolveTile(string path, bool requireMatch)
    {
        var snap = _snapshot;
        foreach (var c in snap)
            if (c.MatchesTile(path, requireMatch)) return c.Rule;
        return null;
    }

    /// <summary>Replace the entire ordered ruleset (used by the API "set whole list" path and by
    /// migration). Persists + recompiles + bumps the generation.</summary>
    public void Replace(IEnumerable<DisplayRule> rules)
    {
        lock (_gate)
        {
            _rules = rules.ToList();
            Rebuild(); Save();
        }
    }

    /// <summary>Append a rule (to the end = lowest precedence). Persists + recompiles.</summary>
    public void Add(DisplayRule rule)
    {
        lock (_gate) { _rules.Add(rule); Rebuild(); Save(); }
    }

    /// <summary>Remove the rule at <paramref name="index"/> (no-op if out of range).</summary>
    public void RemoveAt(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _rules.Count) return;
            _rules.RemoveAt(index); Rebuild(); Save();
        }
    }

    /// <summary>Move the rule at <paramref name="from"/> to <paramref name="to"/> (reorder = change
    /// precedence). Indices are clamped; no-op if equal/out of range.</summary>
    public void Move(int from, int to)
    {
        lock (_gate)
        {
            if (from < 0 || from >= _rules.Count) return;
            to = Math.Clamp(to, 0, _rules.Count - 1);
            if (from == to) return;
            var r = _rules[from];
            _rules.RemoveAt(from);
            _rules.Insert(to, r);
            Rebuild(); Save();
        }
    }

    /// <summary>Replace the rule at <paramref name="index"/> with <paramref name="rule"/>.</summary>
    public void Update(int index, DisplayRule rule)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _rules.Count) return;
            _rules[index] = rule; Rebuild(); Save();
        }
    }

    /// <summary>
    /// Build the default ordered ruleset that REPRODUCES the legacy three-system behavior, used to
    /// seed <c>display_rules.json</c> on first run. Order encodes the old precedence:
    /// <list type="number">
    /// <item>state hides (dead monster / opened chest / completed encounter) — the old IsDrawable gate;</item>
    /// <item>watched highlights (force-draw + label, any category) — wins over mechanics, as before;</item>
    /// <item>mechanic overrides (force-draw, category-gated);</item>
    /// <item>category defaults (hostile monsters by rarity; player; npc; rare/unique chests; transition;
    ///   Object/Other POIs).</item>
    /// </list>
    /// Disabled categories / ShowMonsters-off seed the corresponding rule as <c>Enabled=false</c>.
    /// (Phase 1 leaves the hidden/junk pre-filters and nav qualification external. HP bars are NOT a rule
    /// concern — they're monster-only and gated entirely by the per-rarity toggles in Settings.)
    /// </summary>
    public static List<DisplayRule> BuildDefault(
        RadarStyles st, bool showMonsters, IEnumerable<WatchedEntry> watched)
    {
        var rules = new List<DisplayRule>();

        // 1) State hides (precede everything — mirror the old IsDrawable corpse/opened/complete gate).
        rules.Add(new DisplayRule { Name = "Hide dead monsters",        Categories = new() { "Monster" }, Life = "Dead",     Hide = true });
        rules.Add(new DisplayRule { Name = "Hide opened chests",        Categories = new() { "Chest" },   Chest = "Opened",  Hide = true });
        rules.Add(new DisplayRule { Name = "Hide completed encounters", Encounter = "Complete",                              Hide = true });

        // 2) Watched highlights (force-draw + label; substring, any category) — before mechanics so
        //    watched still wins, matching the old DrawMap precedence.
        foreach (var w in watched)
            rules.Add(new DisplayRule
            {
                Name = string.IsNullOrWhiteSpace(w.Label) ? w.Pattern : w.Label,
                Enabled = w.Enabled, Match = new() { w.Pattern },
                Shape = w.Shape, Color = w.Color, Opacity = 1f, Size = w.Size, Label = w.Label,
            });

        // 3) Mechanic overrides (force-draw, category-gated).
        foreach (var m in st.Mechanics ?? new())
            rules.Add(new DisplayRule
            {
                Name = m.Name, Enabled = m.Enabled,
                Categories = new(m.Categories ?? new()), Match = new(m.Match ?? new()),
                Shape = m.Shape, Color = m.Color, Opacity = m.Opacity, Size = m.Size,
            });

        // 4) Category defaults.
        void Mon(string rarity, IconStyle s) => rules.Add(new DisplayRule
        {
            Name = $"Monster · {rarity}", Enabled = s.Enabled && showMonsters,
            Categories = new() { "Monster" }, Reaction = "Hostile", Rarity = rarity,
            Shape = s.Shape, Color = s.Color, Opacity = s.Opacity, Size = s.Size,
        });
        Mon("Unique", st.MonsterUnique);
        Mon("Rare",   st.MonsterRare);
        Mon("Magic",  st.MonsterMagic);
        Mon("Normal", st.MonsterNormal);

        void Cat(string name, string category, string? rarity, IconStyle s) => rules.Add(new DisplayRule
        {
            Name = name, Enabled = s.Enabled, Categories = new() { category }, Rarity = rarity,
            Shape = s.Shape, Color = s.Color, Opacity = s.Opacity, Size = s.Size,
        });
        Cat("Player",        "Player",     null,     st.Player);
        Cat("NPC",           "Npc",        null,     st.Npc);
        Cat("Chest · Unique", "Chest", "Unique", st.ChestUnique);
        Cat("Chest · Rare",   "Chest", "Rare",   st.ChestRare);
        Cat("Transition",    "Transition", null,     st.Transition);

        // Object/Other entities the game flags as POIs (waypoints, checkpoints, shrines…).
        rules.Add(new DisplayRule
        {
            Name = "Point of Interest", Enabled = st.Poi.Enabled,
            Categories = new() { "Object", "Other" }, Poi = "Yes",
            Shape = st.Poi.Shape, Color = st.Poi.Color, Opacity = st.Poi.Opacity, Size = st.Poi.Size,
        });

        return rules;
    }

    /// <summary>
    /// Built-in Tile display rules seeded exactly once via the <c>built_in_tile_rules_v1</c>
    /// migration key in <see cref="RadarSettings.AppliedMigrations"/>. Kept intentionally tiny:
    /// one rule per game-object entity that ships as a first-class navigation target regardless
    /// of the user's <see cref="BuildDefault"/> seed. Currently just <c>WaygateDevice</c> so
    /// end-game waygates render as a distinct <see cref="DisplayRule.Navigable"/> Eye marker.
    /// <para/>
    /// Additive-only: the caller must UNION this list into the existing ruleset (de-duping on
    /// <see cref="DisplayRule.Name"/>) and Replace — never wipe user rules. Idempotent by
    /// construction: the migration-key guard via <see cref="SeedBuiltInTileRulesIfNeeded"/>
    /// ensures the seed runs at most once per install. Match term is the exact substring
    /// <c>WaygateDevice</c> (no glob, no path prefix) so a future atlas-landmark port can
    /// short-circuit the same entity at source without ambiguity — preventing the double-marker
    /// regression documented in the Threshold spec.
    /// </summary>
    public static List<DisplayRule> BuiltInTileRules() => new()
    {
        new DisplayRule
        {
            Enabled = true,
            Name = "Waygate",
            Categories = new() { "Tile" },
            Match = new() { "WaygateDevice" },
            Shape = "Eye",
            Color = "#00E5FF",
            Opacity = 1f,
            Size = 5f,
            Navigable = true,
        },
    };

    /// <summary>
    /// Migration key stamped into <see cref="RadarSettings.AppliedMigrations"/> when
    /// <see cref="BuiltInTileRules"/> has been folded into the persisted display ruleset.
    /// </summary>
    public const string BuiltInTileRulesMigrationKey = "built_in_tile_rules_v1";

    /// <summary>
    /// Idempotent migration gate for the <see cref="BuiltInTileRules"/> seed. If
    /// <see cref="RadarSettings.AppliedMigrations"/> already contains
    /// <see cref="BuiltInTileRulesMigrationKey"/>, this is a NO-OP; otherwise the key is
    /// appended so the caller (typically the display-rules load path) knows to Replace the
    /// current ruleset with a de-duplicated union of the built-in seed. Re-runs never
    /// double-mark: the guard reads the current key set, not a bool field, so a second call
    /// on the same settings instance and a fresh load of a settings file that already carries
    /// the key both short-circuit at the same guard.
    /// </summary>
    public static void SeedBuiltInTileRulesIfNeeded(RadarSettings settings)
    {
        if (settings is null) return;
        settings.AppliedMigrations ??= new List<string>();
        if (settings.AppliedMigrations.Contains(BuiltInTileRulesMigrationKey)) return;
        settings.AppliedMigrations.Add(BuiltInTileRulesMigrationKey);
    }

    // ── internals ───────────────────────────────────────────────────────────

    /// <summary>Rebuild the immutable precompiled snapshot + bump generation. Call under <see cref="_gate"/>.</summary>
    private void Rebuild()
    {
        var compiled = new Compiled[_rules.Count];
        for (var i = 0; i < _rules.Count; i++) compiled[i] = new Compiled(_rules[i]);
        _snapshot = compiled;
        _generation++;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<DisplayRule>>(File.ReadAllText(_filePath), Json);
            if (list != null) _rules = list;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Display rules load failed: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_rules, Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Display rules save failed: {ex.Message}"); }
    }

    /// <summary>A rule precompiled for fast matching: category set + metadata matchers (substring/glob)
    /// + condition codes (0 = any). Built once per Rebuild; immutable thereafter.</summary>
    private sealed class Compiled
    {
        public readonly DisplayRule Rule;
        public readonly bool HasModFilter;                 // true = this enabled rule has a Mods filter; mod reads are required
        public readonly bool HasItemRarityFilter;          // true = enabled, has Rarity constraint, applies to EntityCategory.Other
        private readonly bool _enabled;
        private readonly bool _isTile;                     // Categories contains "Tile" → matches terrain tiles
        // Category/rarity are matched by ENUM, precompiled — NOT by e.Category.ToString()/e.Rarity.ToString()
        // per call, which allocated a string for every entity × rule × frame (the dominant GC pressure during
        // combat). _anyCat = no category filter; otherwise _catMask is indexed by (int)EntityCategory.
        private readonly bool _anyCat;
        private readonly bool[] _catMask = new bool[7];    // EntityCategory has 7 members (Player..Other)
        private readonly (string sub, Regex? glob)[]? _match; // null = any
        private readonly (string sub, Regex? glob)[]? _mods;  // null = any (matched vs the entity's mod-id list)
        private readonly bool _anyRarity;                  // true = no rarity filter
        private readonly Poe2Live.Rarity _rarity;          // matched rarity enum (sentinel if unparseable → never matches)
        private readonly int _reaction, _life, _chest, _poi, _enc; // 0 any / 1 / 2

        public Compiled(DisplayRule r)
        {
            Rule = r;
            _enabled = r.Enabled;
            _anyCat = r.Categories is not { Count: > 0 };
            if (!_anyCat)
                foreach (var c in r.Categories)
                {
                    if (string.Equals(c, "Tile", StringComparison.OrdinalIgnoreCase)) _isTile = true;
                    else if (Enum.TryParse<Poe2Live.EntityCategory>(c, ignoreCase: true, out var ec)) _catMask[(int)ec] = true;
                }
            _match = r.Match is { Count: > 0 }
                ? r.Match.Where(m => !string.IsNullOrEmpty(m)).Select(CompileTerm).ToArray() : null;
            if (_match is { Length: 0 }) _match = null;
            _mods = r.Mods is { Count: > 0 }
                ? r.Mods.Where(m => !string.IsNullOrEmpty(m)).Select(CompileTerm).ToArray() : null;
            if (_mods is { Length: 0 }) _mods = null;
            HasModFilter = r.Enabled && _mods != null;
            _anyRarity = string.IsNullOrEmpty(r.Rarity);
            // A rule requires item-identity reads if it is enabled, has a Rarity constraint, and
            // could apply to a WorldItem (EntityCategory.Other = index 6).  Mirrors the same
            // _anyCat / _catMask[(int)ec] logic that Matches() uses for category applicability;
            // if _anyCat the rule applies to every category including Other.  Fail-safe: ambiguous
            // / unparseable Rarity still sets !_anyRarity, so we include the rule (prefer a read
            // over a silent miss, per SR-4 follow-up spec).
            HasItemRarityFilter = r.Enabled && !_anyRarity
                && (_anyCat || ((int)Poe2Live.EntityCategory.Other < _catMask.Length
                                && _catMask[(int)Poe2Live.EntityCategory.Other]));
            _rarity = _anyRarity ? default
                : Enum.TryParse<Poe2Live.Rarity>(r.Rarity, ignoreCase: true, out var rr) ? rr : (Poe2Live.Rarity)int.MaxValue;
            _reaction = Code(r.Reaction, "Friendly", "Hostile");
            _life     = Code(r.Life, "Alive", "Dead");
            _chest    = Code(r.Chest, "Opened", "Unopened");
            _poi      = Code(r.Poi, "Yes", "No");
            _enc      = Code(r.Encounter, "Active", "Complete");
        }

        public bool Matches(in Poe2Live.EntityDot e)
        {
            if (!_enabled) return false;
            if (!_anyCat) { var ci = (int)e.Category; if ((uint)ci >= (uint)_catMask.Length || !_catMask[ci]) return false; }
            if (_match != null && !AnyMatch(e.Metadata)) return false;
            if (_mods != null && !AnyMatchMods(e.Mods)) return false;
            if (!_anyRarity && e.Rarity != _rarity) return false;
            if (_reaction == 1 && !e.IsFriendly) return false;
            if (_reaction == 2 && e.IsFriendly) return false;
            if (_life == 1 && !e.IsAlive) return false;
            if (_life == 2 && e.IsAlive) return false;
            if (_chest == 1 && !e.Opened) return false;
            if (_chest == 2 && e.Opened) return false;
            if (_poi == 1 && !e.Poi) return false;
            if (_poi == 2 && e.Poi) return false;
            if (_enc == 1 && e.IconComplete) return false;   // "Active" requires not-complete
            if (_enc == 2 && !e.IconComplete) return false;  // "Complete" requires complete
            return true;
        }

        public bool MatchesTile(string path, bool requireMatch)
        {
            if (!_enabled || !_isTile) return false;
            if (_match == null) return !requireMatch; // no terms: styles any tile, but never surfaces one
            return AnyMatch(path);
        }

        private bool AnyMatch(string metadata)
        {
            foreach (var (sub, glob) in _match!)
            {
                if (glob != null) { if (glob.IsMatch(metadata)) return true; }
                else if (metadata.Contains(sub, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>True if any of this rule's mod terms matches any of the entity's mod ids (substring or
        /// glob). Allocation-free hot path: no LINQ, null/empty list short-circuits.</summary>
        private bool AnyMatchMods(IReadOnlyList<string>? mods)
        {
            if (mods is not { Count: > 0 }) return false;
            foreach (var (sub, glob) in _mods!)
                for (var i = 0; i < mods.Count; i++)
                {
                    var m = mods[i];
                    if (glob != null) { if (glob.IsMatch(m)) return true; }
                    else if (m.Contains(sub, StringComparison.OrdinalIgnoreCase)) return true;
                }
            return false;
        }

        /// <summary>A term with <c>*</c>/<c>?</c> compiles to an anchored glob regex (mirrors
        /// <see cref="HiddenEntities"/>); otherwise it's a case-insensitive substring.</summary>
        private static (string, Regex?) CompileTerm(string term)
        {
            if (term.IndexOf('*') < 0 && term.IndexOf('?') < 0) return (term, null);
            var rx = "^" + Regex.Escape(term).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return (term, new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        private static int Code(string? v, string one, string two)
            => string.IsNullOrEmpty(v) ? 0
             : string.Equals(v, one, StringComparison.OrdinalIgnoreCase) ? 1
             : string.Equals(v, two, StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }
}
