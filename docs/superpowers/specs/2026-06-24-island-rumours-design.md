# Island Rumours Reader — Design Spec

**Goal:** When the player is at the Expedition "Uncharted Waters" rumour-selection screen, the overlay reads which 2–3 rumour labels are currently offered (on-screen UI elements), looks each up in an embedded tier table, and shows a ranked info panel on the overlay and a mirrored live card in the dashboard. Strictly read-only. Tiers and qualitative notes only — no market prices.

**Why / scope:** The Expedition mechanic rewards knowing which rumour to pick before committing. This feature automates the lookup with zero interaction: the overlay sees the on-screen labels (the only memory read), performs a pure table lookup keyed by label string, and presents ranked results. No input, no injection, no pricing data. Compliance gate stays green.

---

## Part 1 — Tier Table (Core)

### 1.1 JSON schema — `Core/Game/island_rumours.json`

Add as a new `<EmbeddedResource>` in `POE2Radar.Core.csproj`, mirroring the existing entries for `dynasty_maps.json` (line 24) and `starter_stat_weights.json` (line 22). The declaration goes inside the same `<ItemGroup>` at lines 14–25:

```xml
<EmbeddedResource Include="Game\island_rumours.json" />
```

Schema: a JSON array of objects. Every field is required.

```jsonc
[
  {
    "rumor":  "<in-game display label, may end with '...' or '…'>",
    "type":   "<Rumour | UniqueMap | PowerfulBoss | BossEncounter | Saga>",
    "map":    "<destination area name, or 'NA'>",
    "mods":   "<short mechanic tags>",
    "tier":   "<S+ | S | A | B+ | B | C | F>",
    "note":   "<qualitative one-liner, may be empty string>"
  }
]
```

### 1.2 Full embedded data (transcribed from the Dracorath "Expedition Explained" sheet)

```json
[
  {
    "rumor": "Fallen Skies",
    "type": "UniqueMap",
    "map": "Moor of Fallen Skies",
    "mods": "Runestones",
    "tier": "S+",
    "note": "Unique Expedition Map, 9-10 Rune to unlock"
  },
  {
    "rumor": "All that glitters",
    "type": "UniqueMap",
    "map": "Castaway",
    "mods": "Gold",
    "tier": "A",
    "note": "All of the Gold"
  },
  {
    "rumor": "Reflective Waters",
    "type": "UniqueMap",
    "map": "Lake of Kalandra",
    "mods": "Ring Bases",
    "tier": "A",
    "note": "The best PoE1 league"
  },
  {
    "rumor": "Almost paradise",
    "type": "UniqueMap",
    "map": "Untainted paradise",
    "mods": "Exp",
    "tier": "C",
    "note": "Less XP than a City map"
  },
  {
    "rumor": "A good fellow",
    "type": "UniqueMap",
    "map": "Moment of Zen",
    "mods": "Seer",
    "tier": "C",
    "note": "Nameless seer hiding a mageblood"
  },
  {
    "rumor": "Endless Cliffs",
    "type": "Rumour",
    "map": "Craggy Peninsula",
    "mods": "Rarity+Rogue Exiles",
    "tier": "A",
    "note": "Good map - juice the rogue exiles"
  },
  {
    "rumor": "Sulphite!",
    "type": "Rumour",
    "map": "Scorched Cay",
    "mods": "Increased Rarity",
    "tier": "A",
    "note": "Juice the remnants for boss/Runes"
  },
  {
    "rumor": "Cold as ice",
    "type": "Rumour",
    "map": "Frigid Bluffs",
    "mods": "Old Expedition",
    "tier": "B",
    "note": "Juice hard, old-style expedition mobs"
  },
  {
    "rumor": "Unknown Ruins",
    "type": "Rumour",
    "map": "Exhumed Ruins",
    "mods": "Precursor Leylines",
    "tier": "B",
    "note": "Run only if adjacent logbook zone is empty"
  },
  {
    "rumor": "Warm but risky",
    "type": "Rumour",
    "map": "Grazed Prairie",
    "mods": "Exp+Beyond+Hoards",
    "tier": "B",
    "note": "Juice hard, old-style expedition mobs"
  },
  {
    "rumor": "Wild, Roaming Free",
    "type": "Rumour",
    "map": "Lush Island",
    "mods": "Azmeri Spirits",
    "tier": "B",
    "note": "Juice a strong boss with wisps"
  },
  {
    "rumor": "Something Fishy",
    "type": "Rumour",
    "map": "Bleached Shoals",
    "mods": "Amulets",
    "tier": "C",
    "note": "Source of All-Res Pearl Ammys"
  },
  {
    "rumor": "Nothing to drink",
    "type": "Rumour",
    "map": "Stagnant Basin",
    "mods": "Oil",
    "tier": "C",
    "note": "Check Runes else currency tiles"
  },
  {
    "rumor": "It's dry at least",
    "type": "Rumour",
    "map": "Sloughed Gully",
    "mods": "Monster effectiveness",
    "tier": "F",
    "note": "Can spawn untargetable enemies"
  },
  {
    "rumor": "Bleak and Awful",
    "type": "Rumour",
    "map": "Barren Atoll",
    "mods": "Strongbox",
    "tier": "F",
    "note": "Can spawn untargetable enemies"
  },
  {
    "rumor": "Crazed Chieftain",
    "type": "PowerfulBoss",
    "map": "Jade Isles",
    "mods": "Powerful Map Boss",
    "tier": "S+",
    "note": "Go get a Rakiatas Flow!"
  },
  {
    "rumor": "Stardrinker",
    "type": "BossEncounter",
    "map": "Secluded Temple",
    "mods": "Uhtred",
    "tier": "S",
    "note": "Drops the Drained Mana Rune"
  },
  {
    "rumor": "Origin of the Fall",
    "type": "BossEncounter",
    "map": "Obscure Island",
    "mods": "Olroth",
    "tier": "A",
    "note": "Drops Triskellion/Flask"
  },
  {
    "rumor": "The Last To Fall",
    "type": "BossEncounter",
    "map": "Mournful Cliffside",
    "mods": "Vorana",
    "tier": "B",
    "note": ""
  },
  {
    "rumor": "End of the Circle",
    "type": "BossEncounter",
    "map": "Sprawling Jungle",
    "mods": "Medved",
    "tier": "B",
    "note": ""
  },
  {
    "rumor": "Aldurs",
    "type": "Saga",
    "map": "NA",
    "mods": "Buffs expeditions",
    "tier": "S+",
    "note": "Gamble on the seed"
  },
  {
    "rumor": "Olroth",
    "type": "Saga",
    "map": "Obscure Island",
    "mods": "Boss Node",
    "tier": "A",
    "note": "Guarantees the boss encounter"
  },
  {
    "rumor": "Uhtred",
    "type": "Saga",
    "map": "Secluded Temple",
    "mods": "Boss Node",
    "tier": "B+",
    "note": "Guarantees the boss"
  },
  {
    "rumor": "Medved",
    "type": "Saga",
    "map": "Strange Jungle",
    "mods": "Boss Node",
    "tier": "B+",
    "note": "Guarantees the boss"
  },
  {
    "rumor": "Vorana",
    "type": "Saga",
    "map": "Mournful Cliffside",
    "mods": "Boss Node",
    "tier": "B+",
    "note": "Guarantees the boss"
  }
]
```

**Spelling note:** The Saga rumour for the fourth boss is "Medved" (matching the PoE2 boss name and the `mods` tag in the "End of the Circle" BossEncounter row). The draft previously used "Medhed" — corrected here to "Medved" for consistency.

### 1.3 Loader — `src/POE2Radar.Core/Game/IslandRumours.cs`

New file. Mirrors the `DynastyMaps` loader pattern exactly (confirmed at `DynastyMaps.cs` lines 35–46 and `StarterWeights.cs` lines 32–38).

**Public surface:**

```csharp
namespace POE2Radar.Core.Game;

public sealed class IslandRumours
{
    // Singleton — loaded once on first access, never throws out of Load().
    public static IslandRumours Shared { get; } = Load();

    // The full table, indexed by normalised label for O(1) lookup after load.
    private readonly Dictionary<string, RumourEntry> _byLabel;

    // Known font/map name prefixes that appear at Str1 (struct+0x20) in place
    // of the actual display label. When Str1 matches any of these, fall back
    // to Str2 (struct+0x50). Case-sensitive — these are exact known values.
    // Extend per patch if new font or map prefixes appear.
    internal static readonly HashSet<string> KnownMapNames = new(StringComparer.Ordinal)
    {
        "Fontin Smallcaps",
        "OptimusPrincepsSemiBold",
        // Add additional font/map-name prefixes here as discovered per patch.
    };

    // --- Pure helpers (no memory, fully unit-testable) ---

    /// <summary>
    /// Normalise a raw in-game display string and look it up in the table.
    /// Normalisation (single pass): trim surrounding whitespace; then strip
    /// exactly ONE trailing suffix — either the horizontal ellipsis U+2026 ('…')
    /// OR three ASCII dots ("..."), whichever is present (checked in that order);
    /// then trim again. The if/else-if is intentional: only one variant is
    /// removed per call. Returns null when no entry matches.
    /// </summary>
    public RumourEntry? MatchLabel(string raw);

    /// <summary>
    /// For each label, call MatchLabel, drop non-matches and duplicates
    /// (by <c>RumourEntry.Rumor</c>), sort best-first by tier, set
    /// <c>IsBestPick = true</c> on the first entry (index 0 after sort).
    /// Returns an empty list when no labels match. Deterministic.
    /// </summary>
    public IReadOnlyList<RankedRumour> RankOffered(IEnumerable<string> labels);

    // --- Tier rank (pure, static) ---
    public static int TierRank(string tier) => tier switch
    {
        "S+" => 6,
        "S"  => 5,
        "A"  => 4,
        "B+" => 3,
        "B"  => 2,
        "C"  => 1,
        "F"  => 0,
        _    => -1   // unknown / null / empty
    };
}

public sealed record RumourEntry(
    string Rumor,
    string Type,
    string Map,
    string Mods,
    string Tier,
    string Note);

public sealed record RankedRumour(
    RumourEntry Entry,
    bool IsBestPick);
```

**Loader implementation notes:**

- `Load()` is `private static IslandRumours Load()`. It calls `Assembly.GetExecutingAssembly()` (not `GetCallingAssembly`) because the resource is embedded in `POE2Radar.Core.dll` — exact same reason as `DynastyMaps.cs` line 35 and `StarterWeights.cs` line 32.
- Resource name scan: `GetManifestResourceNames().FirstOrDefault(n => n.Contains("island_rumours", StringComparison.Ordinal))`.
- Deserialise with `JsonSerializer.Deserialize<List<RumourEntry>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })`.
- Build `_byLabel` immediately from the loaded list: key = `NormaliseLabel(entry.Rumor).ToLowerInvariant()`, value = `entry`. Normalising and lowercasing at build time means `MatchLabel` can do a single `TryGetValue` with a lowercased key — O(1), no iteration required.
- Wrap the entire body in `try/catch (Exception ex) { Console.Error.WriteLine(...); }` and return a safe empty instance on failure — never throw.

**NormaliseLabel (private static):**

```csharp
private static string NormaliseLabel(string s)
{
    s = s.Trim();
    if (s.EndsWith('…'))          // horizontal ellipsis U+2026 — strip it
        s = s[..^1];
    else if (s.EndsWith("..."))        // three ASCII dots — strip them
        s = s[..^3];
    // Single pass: only one trailing suffix is removed. This is intentional.
    // A label ending with both ("...…") is not seen in practice; if encountered,
    // it will be stripped to "..."-suffix form and still fail to match — expected.
    return s.Trim();
}
```

**MatchLabel:**

```csharp
public RumourEntry? MatchLabel(string raw)
{
    if (string.IsNullOrEmpty(raw)) return null;
    var key = NormaliseLabel(raw).ToLowerInvariant();
    return _byLabel.TryGetValue(key, out var e) ? e : null;
}
```

**RankOffered:**

```csharp
public IReadOnlyList<RankedRumour> RankOffered(IEnumerable<string> labels)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var matched = new List<RumourEntry>();
    foreach (var lbl in labels)
    {
        var e = MatchLabel(lbl);
        if (e == null) continue;
        if (!seen.Add(e.Rumor)) continue;   // dedupe by canonical rumor name
        matched.Add(e);
    }
    matched.Sort((a, b) => TierRank(b.Tier).CompareTo(TierRank(a.Tier)));
    var result = new List<RankedRumour>(matched.Count);
    for (int i = 0; i < matched.Count; i++)
        result.Add(new RankedRumour(matched[i], IsBestPick: i == 0));
    return result;
}
```

**Files and anchors:**

| File | Action |
|---|---|
| `src/POE2Radar.Core/POE2Radar.Core.csproj` | Add `<EmbeddedResource Include="Game\island_rumours.json" />` inside the existing ItemGroup (after line 24, matching the dynasty_maps.json style) |
| `src/POE2Radar.Core/Game/island_rumours.json` | New file — the full 25-entry array above |
| `src/POE2Radar.Core/Game/IslandRumours.cs` | New file — `IslandRumours`, `RumourEntry`, `RankedRumour` |

Mirror of loader anchors:
- `DynastyMaps.cs:35` — `Assembly.GetExecutingAssembly()` + `Contains("dynasty_maps", Ordinal)` + `GetManifestResourceStream`
- `DynastyMaps.cs:41` — `JsonSerializer.Deserialize<Dictionary<string,Model>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })`
- `StarterWeights.cs:35` — null-safe fallback `?? new Model()`

### 1.4 Unit tests

New test class in `POE2Radar.Tests` (Core-only; no memory, no Overlay). All tests are pure function calls on `IslandRumours` instances constructed directly (or via `Shared`).

**NormaliseLabel single-pass contract:** `NormaliseLabel` removes exactly one trailing suffix per call (U+2026 or "...", checked in that order, via if/else-if). A string ending with `"...…"` strips the U+2026 only, leaving the `"..."` prefix in place. The test table below asserts actual single-pass behavior for this case.

**MatchLabel cases:**

| Input | Expected |
|---|---|
| `"Endless Cliffs"` | Entry with `Tier="A"`, `Map="Craggy Peninsula"` |
| `"Endless Cliffs..."` | Same entry (trailing ASCII ellipsis stripped) |
| `"Endless Cliffs…"` | Same entry (Unicode horizontal ellipsis stripped) |
| `"  Endless Cliffs  "` | Same entry (surrounding whitespace trimmed) |
| `"  Endless Cliffs...…"` | `null` — NormaliseLabel strips the U+2026 only (single pass), leaving `"Endless Cliffs..."` after normalisation; that trailing-dot form does not appear in the table (the table stores `"Endless Cliffs"` without any ellipsis). The test asserts `result == null` for this input. |
| `"endless cliffs"` | Same entry (case-insensitive) |
| `"ENDLESS CLIFFS"` | Same entry |
| `"Nonexistent Label"` | `null` |
| `""` | `null` |
| `"Aldurs"` | Entry with `Type="Saga"`, `Tier="S+"` |
| `"Aldurs..."` | Same Saga entry |
| `"Uhtred"` | Entry with `Type="Saga"`, `Tier="B+"` (not the BossEncounter "Stardrinker") |
| `"Medved"` | Entry with `Type="Saga"`, `Tier="B+"`, `Map="Strange Jungle"` |

**TierRank cases:**

| Input | Expected |
|---|---|
| `"S+"` | `6` |
| `"S"` | `5` |
| `"A"` | `4` |
| `"B+"` | `3` |
| `"B"` | `2` |
| `"C"` | `1` |
| `"F"` | `0` |
| `"X"` | `-1` |
| `null` / `""` | `-1` |
| B+ > B | `TierRank("B+") > TierRank("B")` |
| A > B+ | `TierRank("A") > TierRank("B+")` |

**RankOffered cases:**

| Labels | Expected result |
|---|---|
| `["Endless Cliffs", "Cold as ice", "Stardrinker"]` | Length 3; order: Stardrinker (S), Endless Cliffs (A), Cold as ice (B); `result[0].IsBestPick == true`; `result[1].IsBestPick == false` |
| `["Aldurs", "Uhtred", "Medved"]` | Length 3; order: Aldurs (S+), then Uhtred and Medved both B+ (stable sort within same tier is acceptable); `result[0].IsBestPick == true`, `result[0].Entry.Rumor == "Aldurs"` |
| `["Endless Cliffs", "endless cliffs...", "Endless Cliffs…"]` | Deduped to length 1 (all three normalise to same key) |
| `["Nonexistent", "Also Nonexistent"]` | Empty list |
| `[]` | Empty list |
| `["Bleak and Awful"]` | Length 1; `result[0].IsBestPick == true`; `Entry.Tier == "F"` |
| `["Endless Cliffs...", "Stardrinker"]` | Length 2; Stardrinker (S) is index 0 and `IsBestPick`; Endless Cliffs (A) is index 1 |
| `["The Last To Fall", "End of the Circle"]` | Both B; either order acceptable; first in sorted list is `IsBestPick` |

---

## Part 2 — Read Layer (Core)

### 2.1 New offsets — `Poe2Offsets.cs`

Add a new `public static class IslandRumour` block inside `public static class Poe2`, after the existing `HoverTracker` class (after line 616). All values are validated from live memory dumps (June 2026 GOLD hex dump, confirmed via BFS scan of 83 pool slots with zero false positives); re-verify per patch using a `--island-rumour` Research probe.

```csharp
public static class IslandRumour
{
    // Offset from a UiElement body (the element pointer itself) to the
    // text struct that holds the label strings.
    // body+0x138 is the text struct pointer for label widgets across multiple
    // mechanic panels (Ritual, Runeforge, and now Island Rumours confirmed).
    // ✓ validated live (Island Rumours offer screen, June 2026 GOLD dump)
    public const int TextStructPtr = 0x138;

    // Magic guard bytes at text_struct+0x10. Read 8 bytes; must match exactly.
    // Fast-reject: if these bytes do not match, the element is not a label
    // widget — skip without reading the string fields.
    // ✓ validated live (confirmed in 83-slot pool scan, June 2026)
    public static ReadOnlySpan<byte> TextStructMagic =>
        [0x91, 0x9C, 0x9F, 0xFF, 0x01, 0x01, 0x00, 0x00];
    // Note: the collection-expression form [ ... ] on a ReadOnlySpan<byte>-returning
    // property is optimised by the compiler to a static data blob (no heap allocation
    // per call), unlike `new byte[]{ ... }` in an expression body.

    // Offset from text_struct base to the first string slot (Str1).
    // Str1 contains the display label OR a font/map-name prefix
    // (e.g. "Fontin Smallcaps", "OptimusPrincepsSemiBold").
    // ✓ validated live
    public const int Str1 = 0x20;

    // Offset from text_struct base to the second string slot (Str2).
    // Str2 is the actual display label when Str1 is a font/map-name prefix.
    // ✓ validated live
    public const int Str2 = 0x50;
}
```

**Rationale:** All other mechanic-specific offset groups follow this pattern: `Runeforge` (lines 497–510), `Ritual` (lines 511–520), `AtlasPanel` (lines 601–607), `HoverTracker` (lines 610–616). A new group is always added after the last existing group inside `public static class Poe2`.

### 2.2 Text-read recipe (validated from June 2026 GOLD dump)

The pool contains all 83 rumour slots in the UI tree. Discrimination of the 3 offered rumours from the remaining pool slots is achieved by text-matching the extracted string against `IslandRumours.Shared` (the `KnownLabels` table) — this produces zero false positives because font names and other UI text strings never match a rumour label. There is no additional "discriminator" byte at any struct offset; the text-match IS the discriminator.

For each candidate UI element `el` encountered in the walk:

1. Read the text-struct pointer: `nint ts = Ptr(el + Poe2.IslandRumour.TextStructPtr)`. If zero/invalid, skip.
2. Read 8 bytes at `ts + 0x10` into a `Span<byte> magic`. If they do not match `Poe2.IslandRumour.TextStructMagic` byte-for-byte, skip (fast-reject — not a label widget).
3. Read Str1: `string s1 = ReadStringUtf16(ts + Poe2.IslandRumour.Str1)`. This is a direct read from the struct body; no additional pointer dereference is needed.
4. If `s1` is a known font/map-name prefix (check `IslandRumours.KnownMapNames.Contains(s1)`): read Str2 as the actual display label: `string label = ReadStringUtf16(ts + Poe2.IslandRumour.Str2)`.
5. Otherwise, `label = s1`.
6. Pass `label` to `IslandRumours.Shared.MatchLabel`. If non-null, the element is an offered rumour; add the raw `label` string to results.

### 2.3 `ReadOfferedRumours` — `Poe2Live.cs`

New `public` method added to the `Poe2Live` class. Signature:

```csharp
public IReadOnlyList<string> ReadOfferedRumours(nint inGameState)
```

Returns the raw display label strings for all elements that pass the magic-guard and produce a table match. Returns an empty list when not at the rumour screen. The caller (`RadarApp`) passes these to `IslandRumours.Shared.RankOffered`.

**Walk algorithm:**

BFS from `uiRoot`, enqueuing ALL children regardless of visibility. The pool elements sit inside invisible wrapper elements in the UI tree; pruning invisible nodes would discard the target data. Text-match against the rumour table is the sole discriminator between offered labels and non-matching labels (font names, "Uncharted Waters" variants, etc.).

```
uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot)   // 0x2F0, ✓

queue = new Queue<nint> { uiRoot }
visited = 0
maxNodes = 30000     // production cap — matches ReadIslandRumours validated value
                     // (diagnostic UiDump cap was 6000; do not use that here)
maxDepth = 40

results = new List<string>()

while queue non-empty AND visited < maxNodes:
    el = queue.Dequeue()
    visited++

    // Attempt text-read recipe (steps 1–6 above).
    label = TryReadRumourLabel(el)     // private helper wrapping steps 1-6
    if label != "":
        results.Add(label)

    // Enqueue children unconditionally — do NOT check visibility before enqueue.
    // Pool elements sit inside invisible wrappers; pruning invisible nodes
    // would discard the target data.
    // (Children+0x10, ChildrenEnd+0x18 — two separate reads,
    //  same pattern as Poe2Live.cs:1201)
    if ChildSpan(el, out nint first, out long n):
        for k in 0..n-1:
            child = Ptr(first + k*8)
            if child != 0: queue.Enqueue(child)

return results.Distinct(StringComparer.Ordinal).ToList()
```

**TryReadRumourLabel** (private helper, encapsulates steps 1–6):

```csharp
private string TryReadRumourLabel(nint el)
{
    nint ts = Ptr(el + Poe2.IslandRumour.TextStructPtr);
    if (ts == 0) return "";
    Span<byte> magic = stackalloc byte[8];
    if (TryReadBytes(ts + 0x10, magic) != 8) return "";
    if (!magic.SequenceEqual(Poe2.IslandRumour.TextStructMagic)) return "";
    string s1 = ReadStringUtf16(ts + Poe2.IslandRumour.Str1);
    if (IslandRumours.KnownMapNames.Contains(s1))
        return ReadStringUtf16(ts + Poe2.IslandRumour.Str2);
    return s1;
}
```

**Returns empty when off-screen:** The magic-guard fast-rejects all non-label elements. The KnownLabels table match in the caller (`MatchLabel`) rejects all non-rumour strings. Together they produce zero false positives (confirmed across three off-screen dumps in the June 2026 GOLD analysis session).

**Files and anchors:**

| File | Change |
|---|---|
| `src/POE2Radar.Core/Game/Poe2Offsets.cs` | Add `public static class IslandRumour { ... }` after line 616 inside `public static class Poe2` |
| `src/POE2Radar.Core/Game/Poe2Live.cs` | Add `ReadOfferedRumours` and `TryReadRumourLabel` methods |

Reused helpers (no changes needed):

| Helper | Location | Confirmed at |
|---|---|---|
| `ChildSpan(nint, out nint, out long)` | `Poe2Live.cs:1201` | Reads `Children+0x10` / `ChildrenEnd+0x18` as two separate `nint` reads |
| `Ptr(nint)` | `Poe2Live.cs:1423` | Canonical-range pointer deref |
| `ReadStringUtf16(nint, int)` | `MemoryReader.cs:121` | Safe, returns `""` on failure; default `maxChars=256` is sufficient |
| `TryReadBytes(nint, Span<byte>)` | `MemoryReader.cs:88` | Returns bytes-read count, 0 on failure |

Note: `ReadOfferedRumours` does not have unit tests. It reads live game memory and its correctness is verified by building successfully and confirming in-game that the panel shows exactly the 2–3 offered rumours when at the selection screen.

---

## Part 3 — Wiring (RadarApp)

### 3.1 New volatile field

In `RadarApp.cs`, add alongside the existing volatile render bundles (confirmed at lines 87–115):

```csharp
// Line ~116, after _monoRender
private volatile IslandRumoursRender _islandRumoursRender = IslandRumoursRender.Empty;
```

```csharp
// New sealed record (define near the other render records in RadarApp.cs):
private sealed record IslandRumoursRender(
    bool HasOffers,
    IReadOnlyList<RankedRumour> Ranked)
{
    public static readonly IslandRumoursRender Empty =
        new(false, Array.Empty<RankedRumour>());
}
```

### 3.2 Own world-thread reader stack

`ReadOfferedRumours` runs on the world thread via the existing `_live` reader (`Poe2Live` instance). It does NOT use `_liveRender` (render thread) or `_liveApi` (HTTP thread). This matches how `UpdateRitualRewards` (confirmed at `RadarApp.cs:722`) and `UpdateMonoliths` use `_live`.

### 3.3 Adaptive throttle

Add two fields to `RadarApp`:

```csharp
private DateTime _nextRumourPoll = DateTime.UtcNow;
private bool _rumourActive = false;   // true when last poll found offers
```

The poll interval is:
- When `_rumourActive == false` (common case — not at the rumour screen): poll every ~2500 ms.
- When `_rumourActive == true` (at the rumour screen): poll every ~750 ms for responsiveness.

### 3.4 UpdateIslandRumours (new private method, world thread)

```csharp
private void UpdateIslandRumours(nint inGameState)
{
    if (!_settings.ShowIslandRumours) { _islandRumoursRender = IslandRumoursRender.Empty; return; }
    if (DateTime.UtcNow < _nextRumourPoll) return;

    var labels = _live.ReadOfferedRumours(inGameState);
    var ranked = IslandRumours.Shared.RankOffered(labels);
    bool hasOffers = ranked.Count > 0;

    _islandRumoursRender = hasOffers
        ? new IslandRumoursRender(true, ranked)
        : IslandRumoursRender.Empty;

    _rumourActive = hasOffers;
    _nextRumourPoll = DateTime.UtcNow.AddMilliseconds(_rumourActive ? 750 : 2500);
}
```

### 3.5 Call site in WorldTick

Add immediately after the `UpdateRitualRewards(inGameState)` call (confirmed at `RadarApp.cs:1140`):

```csharp
UpdateIslandRumours(inGameState);
```

### 3.6 Render-thread snapshot capture

In `Tick()`, extend the existing local-snapshot block (confirmed at `RadarApp.cs:807-811`):

```csharp
var snap = _world;
var ar   = _atlasRender;
var rr   = _runeRender;
var rit  = _ritualRender;
var mr   = _monoRender;
var ir   = _islandRumoursRender;   // NEW
```

### 3.7 RadarState — add the IslandRumours field

`RadarState` is a sealed record defined at `ApiServer.cs:1252`. Add a new optional parameter (mirrors `Monoliths` and `Director` which are also nullable `IReadOnlyList<...>`):

```csharp
IReadOnlyList<RankedRumour>? IslandRumours = null
```

The full updated constructor tail becomes:

```
..., IReadOnlyList<MonolithMarker>? Monoliths = null,
     IReadOnlyList<RankedObjective>? Director = null,
     float Fps = 0,
     SessionStats? Session = null,
     IReadOnlyList<RankedRumour>? IslandRumours = null
```

Update `RadarState.Empty` sentinel at `ApiServer.cs:1281` — no change needed (default for new optional parameters is `null`).

### 3.8 _state publish — render thread

The `_state` is published by the render thread in `Tick()` at line 921. Update the `RadarState` constructor call to include the new field:

```csharp
_state = new RadarState(..., IslandRumours: ir.HasOffers ? ir.Ranked : null);
```

**Threading model recap (critical):**
- `_islandRumoursRender` is written by the world thread in `UpdateIslandRumours`.
- The render thread reads it into local `ir` at the snapshot block.
- The render thread then passes `ir.Ranked` into `RadarState` and publishes `_state`.
- The API thread reads `_state` via the `Func<RadarState>` delegate.
- This is identical to the Monolith path (`UpdateMonoliths` → `_monoRender` → `mr` → `_state` → `/state`), confirmed at `RadarApp.cs:722`, `RadarApp.cs:811`, `RadarApp.cs:921`, `ApiServer.cs:155`.

---

## Part 4 — Display

### 4.1 Overlay panel — `DrawIslandRumours`

New private method in `OverlayRenderer.cs`, added alongside `DrawMonolithPanel` (confirmed at `OverlayRenderer.cs:520`), `DrawSessionHud` (line 554), and `DrawRitualRewards` (line 476).

**Call site:** Inside the second `if (ctx.Active && ctx.InGame)` block at `OverlayRenderer.cs:131-136` (confirmed — this block already contains `DrawRuneforge`, `DrawRitualRewards`, `DrawMonolithPanel`, `DrawSessionHud`; it is NOT inside the `if (ctx.Map.IsVisible)` branch, so the panel is visible with the map open or closed):

```csharp
if (ctx.Active && ctx.InGame)
{
    DrawRuneforge(rt, ctx);
    DrawRitualRewards(rt, ctx);
    DrawMonolithPanel(rt, ctx);
    DrawSessionHud(rt, ctx);
    DrawIslandRumours(rt, ctx);   // NEW — after DrawSessionHud
}
```

**Early-exit guard (mirroring `DrawMonolithPanel:522`):**

```csharp
private void DrawIslandRumours(ID2D1RenderTarget rt, RenderContext ctx)
{
    if (!ctx.ShowIslandRumours || ctx.IslandRumours is not { Count: > 0 } rumours) return;
    // ... draw body
}
```

**Panel geometry (mirroring `DrawMonolithPanel` constants at `OverlayRenderer.cs:524`):**

```csharp
const float w = 260f, pad = 6f, lineH = 15f, titleH = 18f;
// Anchor: top-left corner, offset inward from the left + top edges.
// DrawSessionHud uses the same corner+offset anchor helper from DrawSessionHud
// (the helper exists — DrawSessionHud is confirmed at OverlayRenderer.cs:554).
// Reuse the same anchor-from-corner approach, placing this panel below the
// session HUD. Starting values (tunable via config in a follow-up):
float x = 10f, y = 90f;
```

The `DrawSessionHud` corner+offset anchoring pattern (confirmed present at `OverlayRenderer.cs:554`) is reused here. The Island Rumours panel is placed at the same left edge, below the session HUD's reserved space, so both panels coexist without overlap during normal play (session HUD is visible in-game; Island Rumours panel only appears at the rumour screen, so simultaneous display is rare).

**Height calculation:**

```csharp
float h = titleH + rumours.Count * lineH + pad * 2;
```

Note: h is expanded dynamically in the draw loop when note sub-rows are present (see below).

**Draw sequence (exact idiom from `DrawMonolithPanel:533-548`):**

```
// 1. Panel background — RawRectF for FillRectangle
rt.FillRectangle(new Vortice.RawRectF(x, y, x + w, y + h), _bPanel!);

// 2. Title row — white text
float cy = y + pad;
rt.DrawText("Island Rumours", _tf!, new Rect(x + pad, cy, x + w - pad, cy + titleH),
            _bText!, DrawTextOptions.Clip);
cy += titleH;

// 3. Per-rumour rows
foreach (var rr in rumours)
{
    // Tier badge color
    _bStyle!.Color = TierColor(rr.Entry.Tier);

    // Best-pick marker: a leading ">" character; other rows indent with a space.
    string marker = rr.IsBestPick ? ">" : " ";

    // Row text: "[TIER] MAP"
    // Map may be "NA" for Sagas — show as-is.
    string row = $"{marker}[{rr.Entry.Tier,-2}] {rr.Entry.Map}";
    rt.DrawText(row, _tf!, new Rect(x + pad, cy, x + w - pad, cy + lineH),
                _bStyle, DrawTextOptions.Clip);
    cy += lineH;

    // Note sub-row (if non-empty): smaller indent, white text
    if (!string.IsNullOrEmpty(rr.Entry.Note))
    {
        string noteLine = $"     {rr.Entry.Note}";
        rt.DrawText(noteLine, _tf!, new Rect(x + pad, cy, x + w - pad, cy + lineH),
                    _bText!, DrawTextOptions.Clip);
        cy += lineH;
        h  += lineH;   // dynamic height expansion for note rows
    }
}
```

**TierColor (private static, returns `Color4`):**

```csharp
private static Color4 TierColor(string tier) => tier switch
{
    "S+" => new Color4(1.0f, 0.85f, 0.2f, 1f),   // gold
    "S"  => new Color4(0.8f, 1.0f, 0.2f, 1f),   // gold-green lighter
    "A"  => new Color4(0.4f, 1.0f, 0.4f, 1f),   // green
    "B+" => new Color4(1.0f, 1.0f, 0.3f, 1f),   // yellow
    "B"  => new Color4(0.9f, 0.9f, 0.2f, 1f),   // yellow dimmer
    "C"  => new Color4(1.0f, 0.5f, 0.1f, 1f),   // orange
    "F"  => new Color4(1.0f, 0.2f, 0.2f, 1f),   // red
    _    => new Color4(0.7f, 0.7f, 0.7f, 1f),   // grey (unknown)
};
```

**RenderContext additions:**

`RenderContext` (the struct/record passed to all Draw* methods) must gain two new fields, populated from the render-thread snapshot in `Tick()` before calling `OverlayRenderer.Draw`:

```csharp
bool ShowIslandRumours;
IReadOnlyList<RankedRumour>? IslandRumours;
```

Set in the `Tick()` context-build block:

```csharp
ShowIslandRumours = _settings.ShowIslandRumours,
IslandRumours     = ir.HasOffers ? ir.Ranked : null,
```

### 4.2 /state projection

In `ApiServer.Handle()`, the `/state` case (confirmed at `ApiServer.cs:167`) projects `s.Monoliths` at lines 188–193. Add an `islandRumours` field immediately after:

```csharp
islandRumours = s.IslandRumours == null ? null : s.IslandRumours.Select(r => new
{
    rumor   = r.Entry.Rumor,
    type    = r.Entry.Type,
    map     = r.Entry.Map,
    mods    = r.Entry.Mods,
    tier    = r.Entry.Tier,
    note    = r.Entry.Note,
    isBest  = r.IsBestPick
}).ToArray(),
```

This uses the shared `Json` options (`PropertyNamingPolicy = JsonNamingPolicy.CamelCase`, confirmed at `ApiServer.cs:76`), so all keys are already camelCase in the output.

### 4.3 Dashboard live panel

Add a new sidebar card in `DashboardHtml.cs`, modelled on the `#session-panel` (confirmed at `DashboardHtml.cs:409-417`). Place it after the session-panel `<div>` inside `<aside>`:

```html
<div id="rumours-panel" class="card" style="display:none">
  <div class="card-title">Island Rumours</div>
  <div id="rumours-list"></div>
</div>
```

In the dashboard's `tick()` polling function (which already reads `/state` and updates `#session-panel`), add:

```javascript
// Island Rumours live panel
const rp = document.getElementById('rumours-panel');
const rl = document.getElementById('rumours-list');
if (state.islandRumours && state.islandRumours.length > 0) {
    rp.style.display = '';
    rl.innerHTML = state.islandRumours.map(r => {
        const best = r.isBest ? ' ★' : '';
        const note = r.note ? `<div class="rumour-note">${r.note}</div>` : '';
        return `<div class="rumour-row tier-${r.tier.replace('+','p')}">` +
               `<span class="badge">${r.tier}</span> ` +
               `<b>${r.map}</b>${best}` +
               note +
               `</div>`;
    }).join('');
} else {
    rp.style.display = 'none';
    rl.innerHTML = '';
}
```

Add minimal CSS (inline in the dashboard `<style>` block):

```css
.rumour-row { padding: 2px 0; font-size: 12px; }
.rumour-note { color: #aaa; font-size: 11px; padding-left: 8px; }
.badge { font-weight: bold; min-width: 24px; display: inline-block; }
.tier-Sp { color: #d4af37; }   /* S+ gold */
.tier-S  { color: #b0e040; }
.tier-A  { color: #40e040; }
.tier-Bp { color: #e0e040; }   /* B+ yellow */
.tier-B  { color: #c0c020; }
.tier-C  { color: #e08020; }
.tier-F  { color: #e02020; }
```

(`B+` becomes CSS class `tier-Bp` because `+` is not valid in a CSS class name; `S+` becomes `tier-Sp`.)

### 4.4 Dashboard Settings toggle

In `DashboardHtml.cs`, in the Settings tab section (near the Monolith panel toggle confirmed at line 544), add:

```html
<div class="row">
  <div class="rl">Island Rumours panel<small>Show ranked rumour picks at the Uncharted Waters screen</small></div>
  <label class="sw">
    <input type="checkbox" data-set="showIslandRumours">
    <span class="track"></span><span class="knob"></span>
  </label>
</div>
```

`wireSettings()` (confirmed at `DashboardHtml.cs:845`) auto-wires this via the `data-set` attribute — no additional JavaScript is needed. `saveSetting('showIslandRumours', el.checked)` POSTs `{ showIslandRumours: true/false }` to `/api/settings`.

---

## Part 5 — Config

### 5.1 RadarSettings

Add directly on `RadarSettings` (not nested in a sub-object, because this is a single boolean with no associated sub-settings in v1):

```csharp
// src/POE2Radar.Overlay/Config/RadarSettings.cs
public bool ShowIslandRumours { get; set; } = true;
```

Default is `true`. Rationale: the panel only ever appears at the rumour-selection screen — it is intrinsically contextual and non-intrusive. The adaptive throttle makes idle cost negligible. This contrasts with `MonolithSettings.ShowPanel` which defaults `false` because it appears in general gameplay. An overlay that is on by default only when meaningful earns its keep without cluttering normal play.

### 5.2 /api/settings round-trip

**In `ReadSettings()`** (`ApiServer.cs:~641`), add to the anonymous object:

```csharp
showIslandRumours = _settings.ShowIslandRumours,
```

**In `ApplySettings()`** (`ApiServer.cs:~695`), add to the switch:

```csharp
case "showIslandRumours" when TryBool(p.Value, out var b):
    _settings.ShowIslandRumours = b;
    applied.Add(p.Name);
    break;
```

The flat key `"showIslandRumours"` maps directly to `RadarSettings.ShowIslandRumours` (no nesting). This follows the same pattern as boolean flags like `alwaysShowOverlay` (top-level on `RadarSettings`), not the nested `showMonolithPanel` → `Monoliths.ShowPanel` pattern, because there is no sub-object here.

`_settings.Save()` is already called at the end of `ApplySettings()` when `applied.Count > 0` — no change needed there.

---

## Performance

**Idle cost (not at the rumour screen):** `UpdateIslandRumours` fires every ~2500 ms. One bounded BFS over the UI tree capped at 30000 nodes and depth 40. The magic-guard fast-reject (`TryReadBytes` of 8 bytes at `ts+0x10`) rejects non-label elements in ~2 reads each. The total walk takes a small fraction of the time of the existing 30 Hz entity+terrain walk (`WorldTick`), which reads thousands of entity components and a large terrain byte array. On a low-spec machine, 2500 ms idle polling is imperceptible.

**Active cost (at the rumour screen, ~750 ms poll):** Only the ~18 label-widget elements in the pool pass the magic-guard (confirmed in the June 2026 83-slot scan). Of those, 2–3 match the rumour table. The pure `RankOffered` call on 3 items is O(1). The volatile reference swap for `_islandRumoursRender` is a single atomic pointer write.

**Render cost:** `DrawIslandRumours` draws 3–5 rows of text (2–3 rumour rows plus optional note sub-rows) plus one `FillRectangle`. Identical in cost to `DrawMonolithPanel` which draws a similar number of rows. Not on the critical path of the radar frame.

**Own reader stack:** `ReadOfferedRumours` uses the world thread's `_live` reader (`Poe2Live` instance), which is dedicated to the world thread. The per-instance buffers and caches on `_live` are not touched by the render or API threads — no locking required, consistent with the existing architecture (three independent reader stacks: `_live`, `_liveRender`, `_liveApi`).

---

## Testing

Unit tests live in `POE2Radar.Tests`, testing `POE2Radar.Core.Game.IslandRumours` only. No Overlay types, no memory, no `ProcessHandle`. Every test is a pure function call.

**Test class: `IslandRumoursTests`**

```
MatchLabel_ExactMatch_ReturnsEntry
    Input: "Endless Cliffs"
    Assert: result != null, result.Tier == "A", result.Map == "Craggy Peninsula"

MatchLabel_AsciiEllipsis_Stripped
    Input: "Endless Cliffs..."
    Assert: same entry as exact match

MatchLabel_UnicodeEllipsis_Stripped
    Input: "Endless Cliffs…"
    Assert: same entry as exact match

MatchLabel_LeadingAndTrailingWhitespace_Trimmed
    Input: "  Endless Cliffs  "
    Assert: same entry as exact match

MatchLabel_BothEllipsisVariants_OnlyOneStripped
    Input: "Endless Cliffs...…"
    Assert: result == null
    Rationale: NormaliseLabel is single-pass; strips U+2026 (checked first), leaving
    "Endless Cliffs..." which does NOT appear in the table (table stores "Endless Cliffs"
    without any ellipsis). Single-pass behavior is intentional and this assert validates it.

MatchLabel_CaseInsensitive_Upper
    Input: "ENDLESS CLIFFS"
    Assert: result != null, result.Rumor == "Endless Cliffs"

MatchLabel_CaseInsensitive_Lower
    Input: "endless cliffs"
    Assert: result != null

MatchLabel_NoMatch_ReturnsNull
    Input: "Nonexistent Rumour"
    Assert: result == null

MatchLabel_EmptyString_ReturnsNull
    Input: ""
    Assert: result == null

MatchLabel_Saga_Aldurs
    Input: "Aldurs..."
    Assert: result != null, result.Type == "Saga", result.Tier == "S+"

MatchLabel_BossEncounter_Stardrinker
    Input: "Stardrinker"
    Assert: result != null, result.Type == "BossEncounter", result.Tier == "S"

MatchLabel_Saga_Medved
    Input: "Medved"
    Assert: result != null, result.Type == "Saga", result.Tier == "B+", result.Map == "Strange Jungle"

TierRank_AllKnownTiers_CorrectValues
    Assert: TierRank("S+") == 6
    Assert: TierRank("S")  == 5
    Assert: TierRank("A")  == 4
    Assert: TierRank("B+") == 3
    Assert: TierRank("B")  == 2
    Assert: TierRank("C")  == 1
    Assert: TierRank("F")  == 0
    Assert: TierRank("X")  == -1
    Assert: TierRank("")   == -1

TierRank_Ordering_BPlusGreaterThanB
    Assert: TierRank("B+") > TierRank("B")

TierRank_Ordering_AGreaterThanBPlus
    Assert: TierRank("A") > TierRank("B+")

RankOffered_ThreeTiers_SortedBestFirst
    Input: ["Endless Cliffs", "Cold as ice", "Stardrinker"]
    Assert: result.Count == 3
    Assert: result[0].Entry.Rumor == "Stardrinker"    // S
    Assert: result[1].Entry.Rumor == "Endless Cliffs" // A
    Assert: result[2].Entry.Rumor == "Cold as ice"    // B
    Assert: result[0].IsBestPick == true
    Assert: result[1].IsBestPick == false
    Assert: result[2].IsBestPick == false

RankOffered_TopTierSaga_AldursIsFirst
    Input: ["Uhtred", "Aldurs", "Medved"]
    Assert: result[0].Entry.Rumor == "Aldurs"   // S+
    Assert: result[0].IsBestPick == true
    Assert: result[1].Entry.Tier == "B+" // Uhtred or Medved

RankOffered_Dedupe_SameNormalisedLabel
    Input: ["Endless Cliffs", "Endless Cliffs...", "Endless Cliffs…"]
    Assert: result.Count == 1

RankOffered_AllNonMatching_EmptyList
    Input: ["Fake", "Also Fake"]
    Assert: result.Count == 0

RankOffered_EmptyInput_EmptyList
    Input: []
    Assert: result.Count == 0

RankOffered_SingleEntry_IsBestPick
    Input: ["Bleak and Awful"]
    Assert: result.Count == 1
    Assert: result[0].IsBestPick == true
    Assert: result[0].Entry.Tier == "F"

RankOffered_EllipsisVariant_CountsOnce
    Input: ["Endless Cliffs...", "Stardrinker"]
    Assert: result.Count == 2
    Assert: result[0].Entry.Rumor == "Stardrinker"   // S beats A
    Assert: result[0].IsBestPick == true
```

---

## Compliance

- **Read-only:** `ReadOfferedRumours` uses `TryReadBytes` and `ReadStringUtf16` via `ReadProcessMemory` (`ProcessHandle`). No `WriteProcessMemory`, no `SendInput`, no `PostMessage`, no DLL injection, no function hooking.
- **No market prices:** `island_rumours.json` contains `tier` (S+ through F) and `note` (qualitative text). No currency values, no chaos/divine prices, no trade-site references.
- **No automation:** The feature never sends keystrokes or simulates input. The only output is visual (overlay panel + dashboard card).
- **Compliance gate: GREEN.** All three axes (memory access, automation, pricing) are clean.

---

## Out of Scope (v1)

**Aldurs reroll advice:** The "use Aldurs if you have 3 rumours and none is a boss/unique" heuristic is a clean follow-up that builds on the RankOffered output. Deferred to v2 — the tier table and read layer for v1 provide everything needed to implement it without additional memory reads.

**Area-ID precise locator:** The June 2026 dump analysis identified area-ID strings at `container_base + 0x190` encoding the 3 offered destination areas. Using these directly would require a per-patch reverse-engineered area_id → rumour_label map. The current text-match approach achieves the same result without that map. The area-ID signal is deferred to v2 as an optional exact-offered confirmation path.

---

## Per-Patch Upkeep

`island_rumours.json` must be regenerated each patch when the Expedition rumour pool changes. Procedure:

1. Open the community Dracorath "Expedition Explained" sheet and download the current rumour list.
2. Regenerate `src/POE2Radar.Core/Game/island_rumours.json` from the sheet, preserving the schema (`rumor`, `type`, `map`, `mods`, `tier`, `note`).
3. Re-run the unit tests — `MatchLabel` tests for specific entries will catch any label string changes between patches.
4. Re-validate the `Poe2.IslandRumour` offsets via a `--island-rumour` Research probe if a patch also changes the UI element layout. The magic-guard bytes at `ts+0x10` are the most likely value to drift; if the probe shows a new magic value, update `TextStructMagic` in `Poe2Offsets.cs` and commit with a `✓ re-validated <date>` annotation.
5. The `v1` snapshot embeds the June 2026 table above.
