using System.Text.Json;
using System.Text.Json.Serialization;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Overlay.Config;

/// <summary>
/// User-tweakable overlay settings, persisted as JSON next to the executable
/// (<c>config/radar_settings.json</c>). Defaults reproduce the original hardcoded behavior exactly,
/// so a missing/partial file changes nothing. Calibration is saved live as hotkeys adjust it.
/// </summary>
public sealed class RadarSettings
{
    // ── Feature flags (reserved for later phases; no behavior wired yet). ──
    public bool HideJunk { get; set; } = false;
    public bool ShowPath { get; set; } = false;
    public bool UseCuratedLandmarks { get; set; } = true;
    public bool DrawAllLandmarkPaths { get; set; } = false;

    // ── Landmark clustering. A reusable tile (e.g. a "stairs up" wall piece) recurs in several
    //    disjoint spots — a multi-level dungeon has several stair-up/stair-down sections — so the
    //    scanner groups a tile path's cells into spatial clusters and emits one marker per cluster.
    //    This is the MAX GAP (in TILES; 1 tile ≈ 23 grid units) between cells still considered the
    //    same cluster: larger = merges nearby spots (fewer markers, less map spam), smaller = splits
    //    them (more markers). 0 disables bridging (only directly-touching tiles group). ──
    public int LandmarkClusterGap { get; set; } = 2;

    // ── Radar display toggles. ──
    public bool ShowMonsters { get; set; } = true;
    public bool ShowTerrain { get; set; } = true;
    // The player position blip at map-center. Default on (prior behavior); some prefer it off.
    public bool ShowPlayerBlip { get; set; } = true;

    // Objective Director: when on, auto-select + route to the highest-priority in-zone objective
    // (catalog-ranked), advancing as objectives complete. Read-only — only changes the nav selection.
    public bool EnableDirector { get; set; } = false;
    // Campaign GPS (Quest-aware Director, Part B): when on, route cross-zone toward the next campaign
    // critical-path zone (current-zone + the embedded route table). Read-only — only changes nav selection.
    public bool EnableCampaignGps { get; set; } = false;
    // Quest-memory precision layer for Campaign GPS: only meaningful once the quest-completion offsets
    // are validated in-game; reads quest flags to refine the inferred step. Off until validated.
    public bool EnableQuestMemory { get; set; } = false;

    // ── Quick-Target Cycler (Task 2/3/4). When either input driver is enabled, a ranked target list
    //    is computed each world tick and the cycler can step through it (single-active selection). ──
    // Quick-Target Cycler keyboard hotkeys (Ctrl+Alt+ [ ] / 1-9 / 0) to switch the active radar target.
    // Reads keys to change the overlay's selection — never sends input to the game.
    public bool EnableTargetHotkeys { get; set; } = true;
    // Quick-Target Cycler controller support: L3 = prev target, R3 = next (both combat-dead in PoE2).
    // Read-only XInput poll. On by default; harmless when no controller is connected.
    public bool EnableControllerCycle { get; set; } = true;
    // Quick-Target Cycler ORDER: false (default) = cycle follows the radar-menu order (the nav dropdown:
    // landmarks/tiles, then nearest entities). true = priority-then-distance "intelligent" ranking.
    public bool IntelligentTargetCycling { get; set; } = false;
    // Hold-to-fast-cycle timing (controller L3/R3 + keyboard Ctrl+Alt+ [ / ]): tap = one step; hold past
    // CycleHoldDelayMs auto-repeats one step every CycleHoldIntervalMs.
    public int CycleHoldDelayMs { get; set; } = 400;
    public int CycleHoldIntervalMs { get; set; } = 150;

    // ── Overlay render/present rate (Hz). The overlay redraws + UpdateLayeredWindow-blits at this
    //    rate; lower = less CPU/GPU tax on the game (the blit cost is proportional to resolution).
    //    0 = AUTO: match the refresh rate of the monitor the game is on (re-detected ~1/s) — recommended,
    //    so fast screen-anchored elements (loot-value chips) track smoothly. Set a fixed number to cap it.
    //    The heavier entity/terrain walk stays fixed at ~30 Hz regardless. ──
    public int FpsCap { get; set; } = 0;

    // ── Navigation-menu widget: which screen corner it is pinned to.
    //    One of "TopLeft", "TopRight", "BottomLeft", "BottomRight". ──
    public string NavMenuCorner { get; set; } = "TopLeft";

    // Community Contribute: the project's Cloudflare Worker URL the dashboard uploads your non-identifying
    // pack to (one-click for everyone). LIVE — the project-hosted collector that auto-filters submissions
    // and files them as reviewable GitHub issues (the token lives only as a Worker secret). A user can
    // override this in Settings; empty would fall back to the GitHub issue-submission form.
    public const string DefaultContributeUrl = "https://poe2gps-contribute.luther-rotmg.workers.dev";
    public string ContributeUrl { get; set; } = DefaultContributeUrl;

    // ── Persistent auto-nav: substrings matched (case-insensitive Contains) against a navigation
    //    target's MatchKey (tile path / entity metadata). On every zone change, every target whose
    //    MatchKey matches ANY pattern is auto-selected (up to the 8-color cap), so entering a new
    //    zone auto-draws a path to e.g. the expedition encounter. Seeded with one example so the
    //    feature is visible out of the box; clear the list to disable. ──
    // Dir-qualified so it matches the real marker ("Expedition2/Expedition2Encounter") and not the
    // transient ".../Objects/Expedition2EncounterCrack" effects. (Plain "ExpeditionEncounter" matched
    // nothing — the live path is "Expedition2Encounter" with a digit.)
    public List<string> AutoNavPatterns { get; set; } = new() { "Expedition2/Expedition2Encounter" };

    // ── Monster HP bars (world-space nameplates) by rarity.
    //    Defaults preserve prior behavior: Magic/Rare/Unique shown, Normal hidden. ──
    public bool HpBarNormal { get; set; } = false;
    public bool HpBarMagic { get; set; } = true;
    public bool HpBarRare { get; set; } = true;
    public bool HpBarUnique { get; set; } = true;

    // ── Projection calibration (PageUp/Down = scale, arrows = offset, Home = reset). ──
    public float ScaleMul { get; set; } = 1.0f;
    public float OffX { get; set; } = 0f;
    public float OffY { get; set; } = 0f;

    // Draw the overlay even when PoE2 isn't the foreground window (e.g. while tweaking the dashboard).
    // Auto-flask stays foreground-gated regardless (safety). Default off (overlay hides when unfocused).
    public bool AlwaysShowOverlay { get; set; } = false;

    // NOTE: the atlas canvas→screen projection has NO stored settings — it's derived live from the game
    // window height (UIscale = winH/1600 × live zoom) in RadarApp.AtlasProjection, so it's resolution-
    // correct everywhere with no calibration. (The old F10/F11 homography calibration + its AtlasScale/
    // Off/Shear/Pers/CalibZoom settings were removed; F10 now inspects the tile under the cursor.)

    // Atlas highlight rules: only nodes whose content tags include one of these are drawn in-game (the
    // point is to surface content the game hides by default). Set live from the dashboard Atlas tab.
    // Matched case-insensitively against each node's resolved content tags (e.g. "Breach", "Powerful Map Boss").
    public List<string> AtlasHighlightTags { get; set; } = new();
    // Tags with the off-screen ARROW enabled: when a matching map is outside render distance, an edge
    // arrow points toward it (for hunting high-value maps you can't zoom out to). Independent of tracking.
    public List<string> AtlasArrowTags { get; set; } = new();
    // Tags with NAV-TO enabled: a shortest-hop route line is drawn from the accessible frontier to each
    // matching map. Independent of Highlight (ring) and Arrow — you can route to a map without ringing it,
    // or ring without routing. This is the set the auto-router targets.
    public List<string> AtlasNavTags { get; set; } = new();
    // Per-rule ring colour (tag → "#RRGGBB"), so each highlighted map draws in its filter's category
    // colour in-game (Citadel gold, Boss red, …). Set from the dashboard alongside AtlasHighlightTags.
    public Dictionary<string, string> AtlasHighlightColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Seeded-defaults guard: false until the atlas rules have been initialized once (either by seeding
    // the Citadel defaults when nodes are first read, or by any dashboard edit). Stops re-seeding.
    public bool AtlasRulesInitialized { get; set; }
    // Guard for the built-in "Map Targets" preset (#6): set true after SeedAtlasDefaults runs once
    // (gated on AllTagsResolved). Never re-seeds — user edits from the dashboard persist independently.
    public bool AtlasTargetsSeeded { get; set; }
    // Map colour groups (#7): named sets of map display names → one ring/label colour, so a whole category
    // (Citadels, Halls, Uniques, Expedition) recolours together. Seeded with sensible defaults once
    // (AtlasGroupsSeeded). A node in a group draws in the group colour when it has no per-rule colour.
    public List<AtlasMapGroup> AtlasGroups { get; set; } = new();
    public bool AtlasGroupsSeeded { get; set; }
    // DEBUG: draw EVERY atlas node (overriding the highlight-only rule) — for offset/coverage diagnostics.
    // Off by default: normally only nodes matching AtlasHighlightTags (or manually selected) are drawn.
    public bool AtlasDrawAll { get; set; } = false;
    // Highlight maps whose Anomaly bosses drop Lineage/Dynasty Support Gems (Sealed Vault, Sacred
    // Reservoir, Derelict Mansion, …) with the full Citadel-style ring+arrow+track. Off by default.
    public bool HighlightDynastyMaps { get; set; } = false;
    // Atlas routing: F10 over a tile sets it as the route destination; the overlay draws the shortest path
    // (through the node connection graph) from the player's current node to it. On by default.
    public bool AtlasShowRoute { get; set; } = true;
    // Auto-routing: draw a shortest-hop route from the player's CURRENT atlas node (or the accessible-now
    // frontier when the current node isn't known) to every tracked tile, with a hop-count chip per target.
    // This is the "auto-navigate to the key tiles I'm tracking" feature. On by default.
    public bool AtlasAutoRoute { get; set; } = true;
    // Suppress auto-routes longer than this many map hops (0 = no limit). Keeps the view readable when a
    // common content type (e.g. Breach) is tracked across the whole atlas.
    public int AtlasAutoRouteMaxHops { get; set; } = 0;
    // Draw a biome-coloured border around tracked map labels on the open Atlas (richer in-game info). On by default.
    public bool AtlasShowBiomeBorder { get; set; } = true;
    // Filter: hide completed (run) maps from the atlas overlay — declutters heavily-run atlases. On by default.
    public bool AtlasHideCompleted { get; set; } = true;
    // Filter: hide accessible-only (adjacent but not tracked) maps. Off by default (too aggressive for first-time users).
    public bool AtlasHideAccessible { get; set; } = false;
    // Directional chevron spacing along atlas route lines (#5): distance (in chevron-heights) between arrowheads.
    // Smaller = denser arrows; larger = more spaced out. Clamped 2–60. Default 8 matches upstream spacing.
    public float AtlasRouteArrowSpacing { get; set; } = 8f;
    // #5 on-node content icons: draw content-type PNG glyphs (Breach/Boss/Essence/…) on FOGGED atlas nodes.
    // The game draws its own icons on REVEALED nodes — this surfaces what's hidden on un-revealed maps.
    // Size is the icon cell height in pixels (clamped 8–64). On by default.
    public bool AtlasShowContentIcons { get; set; } = true;
    public float AtlasContentIconSize { get; set; } = 26f;

    // One-time guard: false until the default "Abyss Lightless (Void)" monster display rule has been
    // seeded into display_rules.json. Set true after seeding so a user who deletes the rule keeps it gone.
    public bool AbyssRuleSeeded { get; set; }

    // One-time guard: false until the curated icon glyphs have been applied to the stock display rules
    // (Skull/Crown/Chest/MapPin/…). The migration only retouches rules still on their OLD default shape,
    // so user customizations are preserved; set true afterward so it runs at most once.
    public bool IconDefaultsApplied { get; set; }
    // v2 guard: re-runs the icon migration with separator-insensitive name matching (the v1 pass missed the
    // rules whose names contain "·" due to a code-point mismatch) and the monster Magic/Rare glyphs.
    public bool IconDefaultsApplied2 { get; set; }
    // One-time guard: removes the stale legacy "watched" Diamond rules that duplicated (and shadowed) the
    // mechanic rules, gates Ritual/Breach/Essence to Object/Other so they can't tag league monsters, and
    // reskins the remaining navigation-POI diamonds. Set true afterward so it runs at most once.
    public bool RuleCleanupV1 { get; set; }
    // One-time guard: gives the non-monster mechanic/special rules a default in-game LABEL where they had
    // none (Strongbox/Essence/Shrine/Transition/chest rarities), so the marker shows text, not just an icon.
    public bool MechanicLabelsV1 { get; set; }
    // One-time guard: broadens the ground-item category set from the old {Uniques,Runes,Essences,Currency}
    // to the full high-value set, now that non-uniques actually price + draw.
    public bool GroundDefaultsV2 { get; set; }
    // One-time guard: bumps the monster Magic/Rare/Unique rule sizes — the detailed Fang/Claw/Skull glyphs
    // need ~1.5× the size the old flat shapes used to be legible at radar scale.
    public bool IconSizesV1 { get; set; }

    // Onboarding: false until the user has dismissed or applied the first-run quick-start card.
    // Existing users see it once on upgrade (it's dismissible + informative); no migration guard needed
    // because false is the correct default for everyone.
    public bool FirstRunSeen { get; set; } = false;

    // ── HTTP API. ──
    public int ApiPort { get; set; } = 7777;

    // ── Stealth / footprint. ──
    // Hide the overlay from screen capture / screenshots / OBS (SetWindowDisplayAffinity). On by default
    // for the lowest footprint; turn OFF if you want to screenshot/stream the overlay itself. The overlay
    // always renders on your own monitor regardless — this only affects what capture software sees.
    public bool ExcludeFromCapture { get; set; } = true;

    // Check GitHub for a newer release once at startup (the only outbound request the overlay makes
    // beyond loopback). On by default so you hear about updates; turn OFF for zero network egress.
    public bool CheckForUpdates { get; set; } = true;

    // ── God-Roll Detector (experimental). ──
    // Read inventory on a slow cadence and score each item 0–100 against your stat weights. OFF by
    // default; when off, no inventory is read at all. See the dashboard "Gear ⭐" tab.
    public bool EnableGearScorer { get; set; } = false;

    // ── Audio alerts. Master gate defaults OFF — nothing plays out of the box. Individual sub-toggles
    //    are pre-enabled so turning on EnableAudioAlerts immediately activates all three cue types. ──
    public bool EnableAudioAlerts { get; set; } = false;     // master gate — default OFF
    public bool AudioAlertRareUnique { get; set; } = true;
    public bool AudioAlertUniqueDrop { get; set; } = true;
    public bool AudioAlertObjective { get; set; } = true;
    public int  AudioAlertRadiusCells { get; set; } = 60;
    public int    AudioAlertVolume    { get; set; } = 70;     // 0-100 -> PureToneWav volume /100
    public string AudioToneMonster    { get; set; } = "Chime";
    public string AudioToneItem       { get; set; } = "Bell";
    public string AudioToneObjective  { get; set; } = "Ding";
    public bool   AudioAlertMechanic  { get; set; } = true;
    public string AudioToneMechanic   { get; set; } = "Alert";

    // ── Per-item icon styling (shape / color / opacity / size) + metadata-matched "mechanic"
    //    overrides. Defaults reproduce the original hardcoded look exactly. ──
    public RadarStyles Styles { get; set; } = new();

    // ── Monster HP-bar geometry (the per-rarity ENABLE flags above stay the source of truth;
    //    this adds per-rarity sizing, border thickness, and border color). ──
    public HpBarSettings HpBars { get; set; } = new();

    // ── Affix nameplates: opt-in per-mob mod labels drawn above the HP bar. Off by default. ──
    public AffixNameplateSettings AffixNameplates { get; set; } = new();

    // ── Walkable-terrain bitmap colors/transparency. Defaults reproduce the old hardcoded wash. ──
    public TerrainSettings Terrain { get; set; } = new();

    // ── Ground-item value overlay (unique drops): name + price over the loot icon, border if above value. ──
    public GroundItemSettings GroundItems { get; set; } = new();

    // ── Runeshape-monolith reward overlay: value-coloured map icon + N badge + nearby reward panel. ──
    public MonolithSettings Monoliths { get; set; } = new();

    // ── Session HUD: elapsed time, zone pace, death counter overlay. Off by default. ──
    public SessionHudSettings SessionHud { get; set; } = new();

    // ── Zone summary panel: live counts (rares, chests, exits, landmarks) drawn as a corner HUD. Off by default. ──
    public ZoneSummarySettings ZoneSummary { get; set; } = new();

    // ── Preload Alert: surfaces pinnacle/mechanic content by scanning the loaded-asset list on zone entry. Off by default. ──
    public PreloadAlertSettings PreloadAlert { get; set; } = new();

    // ── Off-screen entity arrows: edge arrows pointing toward rule-flagged entities outside the visible radar area. ──
    public EntityArrowSettings EntityArrows { get; set; } = new();
    // One-time guard: false until OffScreenArrow=true has been seeded onto Unique+Boss/Citadel rules.
    public bool EntityArrowsSeeded { get; set; }

    // ── Configurable hotkey VK codes. Defaults are the original literals — no behavior change for
    //    existing users. The Ctrl+Alt modifier pair, slot-digit 1-9/0 keys, and controller buttons
    //    are fixed and not in this table (rebinding them would conflict with PoE2's own controls). ──
    public KeybindsSettings Keybinds { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Config file path: a "config" directory next to the executable.</summary>
    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "radar_settings.json");

    /// <summary>
    /// Load settings from disk. Returns defaults if the file is missing (and writes a default file),
    /// and is tolerant of partial/missing keys. Never throws on IO/parse errors — logs and falls back.
    /// </summary>
    public static RadarSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var fresh = new RadarSettings();
                fresh.Save();
                return fresh;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<RadarSettings>(json, Json) ?? new RadarSettings();
            // Existing configs are loaded verbatim (never re-seeded from defaults), so repair stale
            // patterns shipped by older builds in place, then persist the upgrade.
            if (loaded.Migrate())
            {
                loaded.Save();
                Console.WriteLine("Settings: migrated stale mechanic rules (Expedition/Strongbox category gating).");
            }
            return loaded;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Settings load failed ({ex.Message}); using defaults.");
            return new RadarSettings();
        }
    }

    /// <summary>
    /// One-time, idempotent repair of mechanic rules from older builds (loaded verbatim, so they'd
    /// otherwise keep the bug forever). Both fixes address ungated rules that tagged a mechanic's
    /// spawned monsters, not just the object:
    /// <list type="bullet">
    /// <item>Expedition: bare "Expedition" / dead "ExpeditionEncounter" → precise, Other-gated
    ///   "Expedition2/Expedition2Encounter".</item>
    /// <item>Strongbox: add a Chest category gate (the box's Vaal guards carry "...Strongbox").</item>
    /// </list>
    /// Returns true if anything changed.
    /// </summary>
    public bool Migrate()
    {
        const string precise = "Expedition2/Expedition2Encounter";
        static bool IsStaleExp(string p) =>
            string.Equals(p, "Expedition", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p, "ExpeditionEncounter", StringComparison.OrdinalIgnoreCase);

        var changed = false;

        static bool IsBroadStrongbox(string p) => string.Equals(p, "Strongbox", StringComparison.OrdinalIgnoreCase);

        if (Styles?.Mechanics is { } mechanics)
            foreach (var m in mechanics)
            {
                if (m.Match is null) continue;
                // Expedition: drop the stale/over-broad keys → the precise key + an Other category gate
                // (so it can't hijack the Monster-category expedition mobs).
                if (m.Match.RemoveAll(IsStaleExp) > 0)
                {
                    if (!m.Match.Exists(p => string.Equals(p, precise, StringComparison.OrdinalIgnoreCase)))
                        m.Match.Add(precise);
                    m.Categories ??= new List<string>();
                    if (m.Categories.Count == 0) m.Categories.Add("Other");
                    changed = true;
                }
                // Strongbox: the default's bare "Strongbox" term over-matched twice — the box's spawned
                // Vaal guards (…Strongbox monsters) and ordinary area chests named "...Strongbox". Drop
                // it down to the "StrongBoxes" directory term and gate to Chest (the box is a /Chests/
                // entity). Triggers whenever the broad term is still present, regardless of category.
                else if (m.Match.Exists(IsBroadStrongbox))
                {
                    m.Match.RemoveAll(IsBroadStrongbox);
                    if (!m.Match.Exists(p => string.Equals(p, "StrongBoxes", StringComparison.OrdinalIgnoreCase)))
                        m.Match.Add("StrongBoxes");
                    m.Categories ??= new List<string>();
                    if (m.Categories.Count == 0) m.Categories.Add("Chest");
                    changed = true;
                }
            }

        // Auto-nav: the seeded "ExpeditionEncounter" matched nothing (digit in the real path).
        if (AutoNavPatterns is not null)
            for (var i = 0; i < AutoNavPatterns.Count; i++)
                if (IsStaleExp(AutoNavPatterns[i])) { AutoNavPatterns[i] = precise; changed = true; }

        return changed;
    }

    /// <summary>Persist current settings to disk atomically (write-to-tmp then replace — crash-safe).
    /// Never throws on IO error — logs and continues.</summary>
    public void Save()
    {
        try
        {
            JsonStore.AtomicWrite(FilePath, this, Json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Settings save failed: {ex.Message}");
        }
    }
}

/// <summary>
/// A single drawable radar icon: shape, RGB color, opacity, pixel size, and an enable toggle.
/// <see cref="Shape"/> is one of Circle/Triangle/Star/Diamond/Plus/Square (anything else falls back
/// to Circle when rendered); <see cref="Color"/> is <c>#RRGGBB</c>; <see cref="Opacity"/> is 0..1.
/// </summary>
public sealed class IconStyle
{
    public bool Enabled { get; set; } = true;
    public string Shape { get; set; } = "Circle";
    public string Color { get; set; } = "#FFFFFF";
    public float Opacity { get; set; } = 1.0f;
    public float Size { get; set; } = 3.0f;

    public IconStyle() { }
    public IconStyle(string shape, string color, float opacity, float size)
    {
        Shape = shape; Color = color; Opacity = opacity; Size = size;
    }
}

/// <summary>
/// A user-defined "mechanic" highlight: when an entity's metadata contains ANY of <see cref="Match"/>
/// (case-insensitive) AND its category is in <see cref="Categories"/> (if any are listed), it draws
/// this icon instead of its generic category dot — so e.g. an Expedition marker or a Strongbox stands
/// out. The first enabled matching rule wins.
/// </summary>
public sealed class MechanicStyle
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public List<string> Match { get; set; } = new();
    /// <summary>Entity-category gate by <c>Poe2Live.EntityCategory</c> name (e.g. "Monster", "Chest",
    /// "Other"). A rule applies only to these categories; empty = all categories. This stops a broad
    /// match term (e.g. "Expedition") from hijacking the wrong entities — the league POI marker
    /// (category Other) vs. the monsters that spawn during the event (category Monster).</summary>
    public List<string> Categories { get; set; } = new();
    public string Shape { get; set; } = "Star";
    public string Color { get; set; } = "#FFFFFF";
    public float Opacity { get; set; } = 1.0f;
    public float Size { get; set; } = 6.0f;
}

/// <summary>
/// A named Atlas colour group (#7): a set of map display names that all draw in one ring/label colour,
/// so a whole category (Citadels, Halls, Uniques, Expedition) recolours together. Adopted from the
/// GameHelper2 Atlas plugin's Map Styles. <see cref="Color"/> is <c>#RRGGBB</c>.
/// </summary>
public sealed class AtlasMapGroup
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#e0b341";
    public List<string> Maps { get; set; } = new();
}

/// <summary>
/// Affix nameplates: opt-in overlay that draws the most-dangerous explicit mods above each mob's HP
/// bar. Disabled by default (Enabled = false). <see cref="Tier"/> sets the minimum tier shown
/// (Deadly | NotableAndAbove | All); <see cref="AlwaysShow"/> / <see cref="Hide"/> are mod-id
/// override lists; <see cref="DisplayAll"/> bypasses threshold/overrides entirely.
/// </summary>
public sealed class AffixNameplateSettings
{
    public bool Enabled { get; set; } = false;            // opt-in
    public string Tier { get; set; } = "Deadly";          // threshold: Deadly | NotableAndAbove | All
    public List<string> AlwaysShow { get; set; } = new(); // mod ids always shown
    public List<string> Hide { get; set; } = new();       // mod ids never shown
    public bool DisplayAll { get; set; } = false;         // ignore threshold/overrides, show every affix
    public bool ShowOnRare { get; set; } = true;
    public bool ShowOnUnique { get; set; } = true;
    public bool ShowOnMagic { get; set; } = false;
    public int MaxLines { get; set; } = 4;
    public float OffsetY { get; set; } = -46f;            // px above the mob (clears the -30 HP bar)
    public string DeadlyColor { get; set; } = "#FF3333";
    public string NotableColor { get; set; } = "#FF9900";
    public string MinorColor { get; set; } = "#AAAAAA";
}

/// <summary>
/// Monster HP-bar geometry. Width, border thickness, and border color are per-rarity; height + X/Y
/// offset are shared. The per-rarity enable flags live on <see cref="RadarSettings"/>
/// (HpBarNormal/Magic/Rare/Unique). The bar *fill* color is taken from the matching monster icon
/// color (so "rare = gold" stays one setting); the border is configured independently below. Border
/// defaults reproduce the old weight-by-rarity cue (Normal undecorated, Magic 1px, Rare/Unique 2px)
/// with borders tinted to match each rarity's icon color.
/// </summary>
public sealed class HpBarSettings
{
    public float Height { get; set; } = 5f;
    public float OffsetX { get; set; } = 0f;
    public float OffsetY { get; set; } = -30f; // px relative to the mob's screen position (neg = up)
    public float WidthNormal { get; set; } = 30f;
    public float WidthMagic { get; set; } = 38f;
    public float WidthRare { get; set; } = 50f;
    public float WidthUnique { get; set; } = 64f;
    // Border thickness in px (0 = no border).
    public float BorderNormal { get; set; } = 0f;
    public float BorderMagic { get; set; } = 1f;
    public float BorderRare { get; set; } = 2f;
    public float BorderUnique { get; set; } = 2f;
    // Border color (#RRGGBB); defaults mirror the per-rarity monster icon colors.
    public string BorderColorNormal { get; set; } = "#FF3333";
    public string BorderColorMagic { get; set; } = "#73A6FF";
    public string BorderColorRare { get; set; } = "#FFD926";
    public string BorderColorUnique { get; set; } = "#FF7300";
}

/// <summary>
/// Walkable-terrain bitmap styling: the interior "wash" over walkable cells and the brighter
/// outline drawn on walkable cells bordering a wall/edge. Color is <c>#RRGGBB</c>; opacity is 0..1
/// (baked into the per-pixel alpha). Defaults reproduce the formerly hardcoded look exactly:
/// interior <c>#506482</c> @ ~30/255, edge <c>#3CDCFF</c> @ ~180/255. The per-area terrain bitmap
/// is rebuilt when any of these change.
/// </summary>
public sealed class TerrainSettings
{
    public string InteriorColor { get; set; } = "#506482";
    public float InteriorOpacity { get; set; } = 0.118f; // → 30/255
    public string EdgeColor { get; set; } = "#3CDCFF";
    public float EdgeOpacity { get; set; } = 0.706f;      // → 180/255
}

public sealed class MonolithSettings
{
    public bool Enabled { get; set; } = true;
    // Value tiers (best offered reward, Exalted): green ≥ HighlightMinEx, yellow from 0.6×, neutral below.
    public double HighlightMinEx { get; set; } = 30.0;
    public double MinRewardEx { get; set; } = 1.0;       // hide reward rows below this (panel + dashboard)
    public bool HideCollected { get; set; } = true;      // hide monoliths whose reward was already claimed
    public bool ShowPanel { get; set; } = false;         // the in-overlay nearby-monolith reward panel (off by default; toggle in Settings)
    public bool ShowMapLabel { get; set; } = true;       // draw the value + top-reward label at the icon
    public float PanelMaxDistance { get; set; } = 0f;    // 0 = every monolith in the area; else only within N grid
}

public sealed class SessionHudSettings
{
    public bool   Enabled               { get; set; } = false;
    public bool   ShowPace              { get; set; } = false;
    public bool   ShowZoneContext       { get; set; } = false;
    public bool   ShowDeaths            { get; set; } = false;
    public bool   ShowKills             { get; set; } = false;
    public string Anchor                { get; set; } = "TopLeft";
    // Legal values: "TopLeft", "TopRight", "BottomLeft", "BottomRight"
    // Mirrors NavMenuCorner (RadarSettings.cs line 55) — plain string, no C# enum.
    public int    OffsetX               { get; set; } = 0; // pixels inward from the anchored corner (positive = toward screen center)
    public int    OffsetY               { get; set; } = 0; // pixels inward from the anchored corner (positive = toward screen center)
    // Behavior-tuning flag (NOT a visibility toggle): defaults TRUE so towns are excluded from pace.
    public bool   ExcludeTownsFromPace  { get; set; } = true;
}

public sealed class ZoneSummarySettings
{
    public bool   Enabled { get; set; } = false;
    public string Anchor  { get; set; } = "TopRight";  // TopLeft|TopRight|BottomLeft|BottomRight
    public int    OffsetX { get; set; } = 0;
    public int    OffsetY { get; set; } = 0;
}

/// <summary>
/// Preload Alert: surfaces high-value in-zone content (pinnacle bosses, Breach, etc.) by scanning
/// the game's loaded-asset list on zone entry. Off by default — opt in from the dashboard.
/// </summary>
public sealed class PreloadAlertSettings
{
    public bool   Enabled           { get; set; } = false;          // opt-in, default OFF
    public string MinTier           { get; set; } = "mechanic";     // pinnacle|high|mechanic|interactable — show >=
    public string AudioTier         { get; set; } = "pinnacle";     // play cue when >= this tier (or "off")
    public bool   Diagnostic        { get; set; } = false;          // expose full match+frequency in /state
    public double CommonThreshold   { get; set; } = 0.6;            // paths seen in >= this fraction are noise
    public int    WarmupZones       { get; set; } = 4;              // zones before noise suppression activates
    public string Anchor            { get; set; } = "top-right";    // corner: top-right|top-left|bottom-right|bottom-left
    public int    OffsetX           { get; set; } = 0;              // px inward from corner
    public int    OffsetY           { get; set; } = 0;
}

/// <summary>
/// Virtual key codes for overlay hotkeys. All defaults reproduce the original hardcoded literals
/// exactly — existing users see no behavior change. The Ctrl+Alt modifier pair, the slot-jump
/// digit keys (1-9/0), and controller buttons (L3/R3) are fixed and cannot be changed here.
/// </summary>
public sealed class KeybindsSettings
{
    public int Quit          { get; set; } = 0x78; // F9  — quits the overlay (no foreground gate)
    public int OpenDashboard { get; set; } = 0x7B; // F12 — open web dashboard (foreground-gated)
    public int AtlasInspect  { get; set; } = 0x79; // F10 — inspect atlas tile under cursor
    public int AddNearest    { get; set; } = 0x75; // F6  — add nearest nav target (debounced)
    public int ClearRoutes   { get; set; } = 0x76; // F7  — clear all nav routes (debounced)
    public int CycleNext     { get; set; } = 0xDD; // ]   — cycle next target  (Ctrl+Alt+], hold-to-fast)
    public int CyclePrev     { get; set; } = 0xDB; // [   — cycle prev target  (Ctrl+Alt+[, hold-to-fast)
    public int NavMenuToggle { get; set; } = 0x4D; // M   — toggle nav menu    (Ctrl+Alt+M, foreground-gated)
    public int SessionReset  { get; set; } = 0x52; // R   — reset session HUD  (Ctrl+Alt+R, foreground-gated)
}

/// <summary>
/// Off-screen entity arrow settings. Arrows are drawn at the screen edge pointing toward rule-flagged
/// entities that are outside the visible radar area. Enabled = master gate (arrows fire only for entities
/// whose DisplayRule has <c>OffScreenArrow = true</c>). Size, label, cap, and edge-margin are tunable.
/// </summary>
public sealed class EntityArrowSettings
{
    public bool Enabled { get; set; } = true;          // master gate (arrows only fire for rule-flagged, off-screen entities)
    public float Size { get; set; } = 11f;             // arrowhead size in px
    public bool ShowLabel { get; set; } = true;        // draw the rule name by the arrow
    public int MaxArrows { get; set; } = 12;           // cap (nearest-first) to avoid edge clutter
    public int MinEdgeDistancePx { get; set; } = 24;   // skip targets whose projection is within this of the edge
}

public sealed class GroundItemSettings
{
    // Off by default: the per-drop name labels overlay every non-grey ground item, which clutters the
    // screen and obscures the game's own loot filter. Opt in from the dashboard ("Ground Item Labels").
    public bool Enabled { get; set; } = false;
    // Which item categories get a ground name label. Empty list ⇒ nothing shows.
    public List<string> Categories { get; set; } = new()
    {
        "Uniques", "Currency", "Runes", "SoulCores", "Essences", "Fragments",
        "UncutGems", "Delirium", "Tablets", "Idols", "Abyss", "Ritual",
    };
}

/// <summary>
/// The full radar icon style table. Every default mirrors the formerly hardcoded values in
/// <c>OverlayRenderer</c>, so a missing/partial config renders identically to before.
/// </summary>
public sealed class RadarStyles
{
    // Monster dots by rarity.
    public IconStyle MonsterNormal { get; set; } = new("Circle",   "#FF3333", 0.95f, 2.6f);
    public IconStyle MonsterMagic  { get; set; } = new("Fang",     "#73A6FF", 0.97f, 5.5f);
    public IconStyle MonsterRare   { get; set; } = new("Claw",     "#FFD926", 1.00f, 7.5f);
    public IconStyle MonsterUnique { get; set; } = new("Skull",    "#FF7300", 1.00f, 8.0f);

    // Other entity categories.
    public IconStyle Player        { get; set; } = new("Person",  "#4DF2FF", 1.00f, 3.4f);
    public IconStyle Npc           { get; set; } = new("Chat",    "#FFD933", 0.95f, 4.2f);
    public IconStyle ChestRare     { get; set; } = new("Chest",   "#FFD926", 0.95f, 5.0f);
    public IconStyle ChestUnique   { get; set; } = new("Crown",   "#FF7300", 0.95f, 5.5f);
    public IconStyle Transition    { get; set; } = new("Stairs",  "#66FF99", 0.95f, 5.0f);
    public IconStyle Poi           { get; set; } = new("MapPin",  "#8CBFFF", 0.80f, 3.6f);

    // Tile landmarks (shape marker + text label at the group centroid).
    public IconStyle Landmark      { get; set; } = new("Diamond", "#F259F2", 1.00f, 5.0f);

    // Metadata-matched overrides (first enabled match wins). Seeded with common PoE2 mechanics.
    public List<MechanicStyle> Mechanics { get; set; } = new()
    {
        // Match the actual league POI marker ONLY. The old bare "Expedition" substring (with an empty
        // category gate) hijacked EVERY entity carrying "Expedition" in its path — the combat mobs
        // (".../...CrabExpedition", category Monster) and the transient detonation effects
        // (".../Objects/Expedition2EncounterCrack", category Other) all got the marker icon. The
        // dir-qualified key hits only "Expedition2/Expedition2Encounter" (NOT the "/Objects/...Crack"
        // path), and the Other gate keeps it off the monsters. ("ExpeditionEncounter" was also dead —
        // the real path is "Expedition2Encounter" with a digit, so that key matched nothing.)
        new() { Name = "Expedition", Match = new() { "Expedition2/Expedition2Encounter" }, Categories = new() { "Other" }, Shape = "Flag", Color = "#26E6D9", Opacity = 1f, Size = 7f },
        // Ritual/Breach/Essence are gated to the mechanic MARKER (Object/Other) so the bare substring can't
        // hijack the league's combat monsters (e.g. "Metadata/Monsters/LeagueRitual/…", "…LeagueBreach/…").
        new() { Name = "Ritual",     Match = new() { "Ritual" }, Categories = new() { "Object", "Other" },  Shape = "Star",     Color = "#FF3355", Opacity = 1f, Size = 7f },
        new() { Name = "Breach",     Match = new() { "Breach" }, Categories = new() { "Object", "Other" },  Shape = "Portal",   Color = "#A64DFF", Opacity = 1f, Size = 7f },
        // Match the league-strongbox DIRECTORY only ("Metadata/Chests/StrongBoxes/…") and gate to
        // Chest. The bare "Strongbox" term was too broad twice over: it tagged the box's spawned Vaal
        // guards (…Strongbox monsters — now excluded by the Chest gate) AND ordinary area chests that
        // merely carry "Strongbox" in their name (e.g. Chests/KedgeBayChests/KedgeBayChestStrongbox).
        // "StrongBoxes" hits the real boxes (BasicStrongboxLow lives under it) but not those.
        new() { Name = "Strongbox",  Match = new() { "StrongBoxes" }, Categories = new() { "Chest" }, Shape = "Chest", Color = "#FFB300", Opacity = 1f, Size = 6f },
        new() { Name = "Essence",    Match = new() { "Essence" }, Categories = new() { "Object", "Other" }, Shape = "Flask",    Color = "#33E0FF", Opacity = 1f, Size = 7f },
        // Match the real shrine namespace ONLY (Metadata/Shrines/Shrine_Trigger). A bare "Shrine" substring
        // false-positives on terrain cosmetics/spawners (GoblinShrineCosmetic, GoblinShrineSpawnerLeap) and
        // the ShrineFireDaemon effect carrier — none of which are the clickable shrine mechanic.
        new() { Name = "Shrine",     Match = new() { "Metadata/Shrines/" },                  Shape = "Star",     Color = "#7DFF7D", Opacity = 1f, Size = 6f },
    };
}
