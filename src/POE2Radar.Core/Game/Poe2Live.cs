using System.Runtime.CompilerServices;
using System.Buffers;
using POE2Radar.Core.Health;

[assembly: InternalsVisibleTo("POE2Radar.Tests")]

namespace POE2Radar.Core.Game;

/// <summary>
/// Live PoE2 game-state reader for the radar overlay. Resolves the top-level chain each tick
/// (GameState → InGameState → AreaInstance) and exposes the player position, the entity list,
/// the walkable terrain grid, and the large-map UI state — all via offsets validated live and
/// recorded in <see cref="Poe2"/> / resources/community-offsets.md.
///
/// <para>Construct once with the AOB-resolved GameState pointer slot (see Bootstrap). Call
/// <see cref="TryResolve"/> at the start of each tick; everything else takes the resolved
/// AreaInstance / InGameState.</para>
/// </summary>
public sealed class Poe2Live
{
    private readonly MemoryReader _reader;
    private nint _gameStateSlot;   // (was readonly) — rebindable for lazy/late resolution + re-attach

    // Per-entity frozen data, keyed by entity object address (stable within an area).
    private readonly Dictionary<nint, nint> _renderAddr = new();   // entity → Render component
    private readonly Dictionary<nint, nint> _lifeAddr = new();     // entity → Life component (0 = none)
    private readonly Dictionary<nint, nint> _posAddr = new();      // entity → Positioned component (0 = none)
    private readonly Dictionary<nint, nint> _ompAddr = new();      // entity → ObjectMagicProperties (0 = none)
    private readonly Dictionary<nint, nint> _chestAddr = new();    // entity → Chest component (0 = none)
    private readonly HashSet<nint> _openedChests = new();          // entity → chest confirmed opened (one-way; cleared on zone change/rebind)
    private readonly HashSet<nint> _completedPois = new();         // POI confirmed complete (one-way; cleared per zone)
    private readonly Dictionary<nint, (int tick, bool poi, bool complete)> _iconState = new();  // last ReadIcon result + tick
    private int _iconTick;                                          // incremented once per Entities() pass
    private readonly Dictionary<nint, MonolithState> _monolithCache = new();  // device → static station data (Collected re-read live)
    private readonly Dictionary<nint, EntityCategory> _category = new();
    private readonly Dictionary<nint, string> _meta = new();
    private readonly Dictionary<nint, nint> _iconAddr = new();     // entity → MinimapIcon component (0 = none); game POI
    private readonly Dictionary<nint, Rarity> _rarity = new();     // entity → rarity (static per spawn; cached)
    private readonly Dictionary<nint, byte> _reaction = new();    // entity → Reaction byte (static per spawn; slow-refresh cache)
    private int _reactionTick;
    private const int ReactionRefreshTicks = 30;   // ~1s at 30Hz world cadence; re-reads catch enemy<->friendly conversions

    /// <summary>Pure: true on ticks where the reaction cache should be flushed and re-read.</summary>
    public static bool ShouldRefreshReaction(int tick, int interval) => interval > 0 && tick % interval == 0;
    /// <summary>When false, monster affix-mod reads are skipped entirely (no consumer needs them). Set by
    /// RadarApp each world tick from the affix-nameplate + mod-filter feature state. Default true = fail-safe.</summary>
    public bool EnableModReads { get; set; } = true;
    /// <summary>When false, dropped-item identity reads (art/name/rarity) are skipped — the ground-item
    /// label overlay is off. Set by RadarApp per world tick. Default true = fail-safe.</summary>
    public bool EnableItemIdentityReads { get; set; } = true;

    /// <summary>One active buff on an entity. Id = internal name (e.g. "igniting_presence_aura");
    /// Timer = seconds remaining (0 when Permanent); Permanent = the timer read as Inf/≤0.</summary>
    public readonly record struct BuffState(string Id, float Timer, bool Permanent);

    /// <summary>When false, the Buffs component is never read (no consumer). Set by RadarApp each world tick
    /// from the Buff-nameplate feature state. Default true = fail-safe. Mirrors <see cref="EnableModReads"/>.</summary>
    public bool EnableBuffReads { get; set; } = true;

    private readonly Dictionary<nint, nint> _buffsAddr = new();    // entity → Buffs component (0 = none)
    private readonly Dictionary<nint, string> _buffId = new();     // StatusEffect addr → id (static per instance)

    private readonly Dictionary<nint, string[]> _mods = new();     // entity → affix mod ids (static per spawn; cached; empty = no mods)
    private readonly Dictionary<nint, (Rarity rarity, string? art, bool identified, string? name, IReadOnlyList<RawAffix>? affixes)> _itemIdent = new(); // WorldItem entity → dropped-item identity (static; cached)
    private readonly Dictionary<nint, uint> _idAt = new();         // entity address → last-seen std::map key id (recycle guard)
    // Bounds the number of NEW (uncached) monster mod reads per Entities() pass so walking into a large
    // pack can't stall the world tick. Cached monsters cost nothing; new ones fill over a few ticks.
    private int _modReadBudget;
    private const int ModReadBudgetPerPass = 16;
    private readonly byte[] _modVecBuf = new byte[24]; // one StdVector (First/Last/End)
    // Same budgeting for the (cheap, read-once) dropped-item identity reads.
    private int _itemReadBudget;
    private const int ItemReadBudgetPerPass = 12;
    private nint _entCacheKey;   // AreaInstance address the entity caches were built for
    // Triple-buffer: world thread fills one, publishes it (volatile swap), then fills the next.
    // 3 buffers ensure the just-published list is not refilled for 2 more world ticks (~66 ms),
    // far exceeding one render frame (~7 ms), so the render thread can never iterate a buffer
    // being refilled. See task-9-brief.md for the full concurrency argument.
    private readonly List<EntityDot>[] _entityBufs =
        { new List<EntityDot>(256), new List<EntityDot>(256), new List<EntityDot>(256) };
    private int _entityBufIdx;

    // Reused across Entities() calls (tick thread only) to avoid per-tick allocations. The std::map
    // walk reads each 48-byte node in ONE ReadProcessMemory (fields are contiguous), not 5 syscalls.
    private readonly Queue<nint> _entQueue = new();
    private readonly HashSet<nint> _entVisited = new();
    // E6: reused BFS buffers for ScanLootLabels (mirrors _entQueue/_entVisited pattern).
    private readonly Queue<nint> _lootQueue = new();
    private readonly HashSet<nint> _lootVisited = new();
    private readonly byte[] _nodeBuf = new byte[0x30];
    private readonly byte[] _compBucketBuf = new byte[256 * 16];   // fixed upper-bound scratch; reused
    private static readonly string[] _hotComponents =
        { "Render", "Positioned", "Life", "ObjectMagicProperties", "MinimapIcon", "Chest" };
    // Reused camera-matrix buffers (read every render frame).
    private readonly byte[] _camBytes = new byte[64];
    private readonly float[] _camMatrix = new float[16];
    // Reused BFS buffers for DiscoverMapElements (render-thread-owned, used via _liveRender.ReadMap).
    private readonly Queue<nint> _mapQueue = new();
    private readonly HashSet<nint> _mapVisited = new();
    private readonly byte[] _mapBody = new byte[Poe2.MapUiElement.Zoom + 8];
    private readonly List<nint> _probeCandidates = new(13);   // reused per Probe() call; owned by this instance's thread
    private int _vitalBadReadCount;   // owned by this instance's thread
    private const int VitalBadReadThreshold = 3;

    public Poe2Live(MemoryReader reader, nint gameStateSlot)
    {
        _reader = reader;
        _gameStateSlot = gameStateSlot;
    }

    /// <summary>The GameState slot this reader is currently bound to (0 = not yet resolved).</summary>
    public nint Slot => _gameStateSlot;

    /// <summary>Late-bind (or re-bind, on re-attach) the GameState slot. Clears every per-entity/per-area
    /// cache, whose keys (entity / AreaInstance addresses) are meaningless under a new slot or process.
    /// Call only from the thread that owns this Poe2Live instance.</summary>
    public void Rebind(nint gameStateSlot)
    {
        _gameStateSlot = gameStateSlot;
        _renderAddr.Clear(); _lifeAddr.Clear(); _posAddr.Clear(); _ompAddr.Clear(); _chestAddr.Clear(); _openedChests.Clear(); _completedPois.Clear(); _iconState.Clear();
        _category.Clear(); _meta.Clear(); _iconAddr.Clear(); _rarity.Clear(); _reaction.Clear(); _mods.Clear();
        _itemIdent.Clear(); _idAt.Clear(); _monolithCache.Clear(); _buffsAddr.Clear(); _buffId.Clear();
        _entCacheKey = 0;
        _league = ""; _leagueFor = -1;
        _areaCode = ""; _areaCodeFor = -1;
        _plPlayer = 0; _plPlayerFor = 0;
        _cachedPlayerName = null; _cachedPlayerNameFor = 0;
        // Additional per-entity/per-area caches not in the brief's explicit list:
        _plLife = 0; _plLifeFor = 0;
        _vitalOffsetsResolved = false;
        _vitalBadReadCount = 0;
        _healthOff = Poe2.Life.Health; _manaOff = Poe2.Life.Mana; _esOff = Poe2.Life.EnergyShield;
        _esOffKnown = true;
        _landmarksKey = -1; _landmarks = null;
        _tilePathsKey = -1; _tilePaths = null;
        _mapCacheKey = -1; _mapEls.Clear(); _everHidden.Clear(); _everVisible.Clear();
    }

    public enum EntityCategory { Player, Monster, Npc, Chest, Transition, Object, Other }

    /// <summary>Monster rarity from ObjectMagicProperties.Rarity. NonMonster = not applicable.</summary>
    public enum Rarity { Normal = 0, Magic = 1, Rare = 2, Unique = 3, NonMonster = -1 }

    public readonly record struct EntityDot(
        uint Id, nint Address, System.Numerics.Vector2 Grid, Vector3 World, EntityCategory Category, string Metadata,
        int HpCur, int HpMax, bool Poi, byte Reaction, Rarity Rarity, bool Opened, bool IconComplete = false,
        IReadOnlyList<string>? Mods = null, string? ItemArt = null, bool ItemIdentified = true, string? ItemName = null,
        IReadOnlyList<RawAffix>? ItemAffixes = null)
    {
        // ItemName (positional): a dropped item's rendered BASE-TYPE display name (Base +0x10 → +0x30),
        // e.g. "Greater Orb of Augmentation". The price key for NON-uniques (currency/runes/essences),
        // where one .dds art is shared across tiers so art can't disambiguate. Null for non-items / unread.
        // ItemIdentified (positional): for a dropped unique, whether the game has identified it (Mods+0x90).
        // Drives the loot overlay's unique rule — unID → reveal the resolved name; ID → value only. Defaults
        // true so non-item / non-unique entities are never treated as "unidentified".
        /// <summary>The monster's affix mod ids (auras/buffs), never null. Empty for non-monsters,
        /// unrolled monsters, or before the budgeted mod read has filled this entity in.</summary>
        public IReadOnlyList<string> ModList => Mods ?? Array.Empty<string>();
        // ItemArt (positional): for a dropped-item (WorldItem) entity, the basename of its 2D art (.dds),
        // e.g. "Earthbound" — the price-lookup key (matches poe2scout IconUrl basename). Null for non-items
        // / not-yet-read. When set, Rarity carries the dropped item's rarity (Unique=3) for gating.

        /// <summary>Monsters are "alive" only with positive HP; non-life entities are always shown.</summary>
        public bool IsAlive => HpMax <= 0 || HpCur > 0;
        public bool HasLife => HpMax > 0;
        /// <summary>PoE2 friendly rule: (Reaction &amp; 0x7F) == 1.</summary>
        public bool IsFriendly => (Reaction & 0x7F) == 1;
        public float HpFraction => HpMax > 0 ? Math.Clamp((float)HpCur / HpMax, 0f, 1f) : 1f;
    }

    public readonly record struct MapUi(bool IsVisible, float ShiftX, float ShiftY, float Zoom);

    /// <summary>One rolled affix on an inventory item: the internal GGG mod id and its raw integer values
    /// (one per stat the mod grants, in stat order). Values are raw (not rendered) — the Overlay layer
    /// renders them to English stat lines via <see cref="ItemModTranslator"/>.</summary>
    public readonly record struct RawAffix(string ModId, IReadOnlyList<int> Values);

    /// <summary>One item from the player's inventory (any bag/slot). Implicit + explicit affixes as raw
    /// (modId + rolled int values). Rarity is the Mods-component rarity string (Normal/Magic/Rare/Unique).
    /// Name is the rendered base-type display name (e.g. "Greater Orb of Augmentation"), empty if unavailable.
    /// InventoryId is the Inventories.dat index (1=Main, 2=BodyArmour, 3=Weapon1, etc.).</summary>
    public sealed record InventoryItem(string Name, string Rarity, bool Identified, int InventoryId,
        IReadOnlyList<RawAffix> Affixes);

    /// <summary>A static tile-based landmark: a notable terrain feature and its grid centroid.
    /// <paramref name="CuratedName"/> is an optional curated friendly label (null when none matches);
    /// <paramref name="Name"/> is the derived-from-path fallback.</summary>
    public readonly record struct Landmark(string Name, string Path, System.Numerics.Vector2 Center, int TileCount, string? CuratedName = null)
    {
        /// <summary>Stable per-CLUSTER identity for nav selection. A tile path can now yield several
        /// landmarks (one per spatial cluster — e.g. each stair-up section of a multi-level dungeon),
        /// so the path alone is ambiguous; qualify it with the integer centroid, which is stable per
        /// area (tiles are static terrain).</summary>
        public string Key { get; } = $"{Path}@{(int)Center.X},{(int)Center.Y}";
    }

    public sealed record TerrainData(byte[] Walkable, int Width, int Height);

    /// <summary>Graduated resolve: report how far the GameState → InGameState → AreaInstance → LocalPlayer
    /// chain got this tick, plus the patch-stable low fields, so the health monitor can tell "in a zone but
    /// can't read the player" (offsets broke) from "at a menu / loading". Returns the resolved handles when
    /// it reaches <see cref="ResolveStage.Full"/>; <paramref name="areaHash"/>/<paramref name="areaLevel"/>
    /// are valid whenever the stage is <see cref="ResolveStage.InZone"/> or <see cref="ResolveStage.Full"/>.</summary>
    public ResolveStage Probe(out nint inGameState, out nint areaInstance, out nint localPlayer,
                              out uint areaHash, out int areaLevel)
    {
        inGameState = areaInstance = localPlayer = 0; areaHash = 0; areaLevel = 0;
        var gameState = Ptr(_gameStateSlot);
        if (gameState == 0) return ResolveStage.None;

        var best = ResolveStage.GameState;
        _probeCandidates.Clear();
        var vecFirst = Ptr(gameState + Poe2.GameState.CurrentStatePtr);
        if (vecFirst != 0) _probeCandidates.Add(Ptr(vecFirst));
        for (var i = 0; i < Poe2.GameState.StateSlotCount; i++)
            _probeCandidates.Add(Ptr(gameState + Poe2.GameState.States + (nint)(i * Poe2.GameState.StateSlotStride)));

        foreach (var igs in _probeCandidates)
        {
            if (igs == 0) continue;
            if (best < ResolveStage.InGameState) best = ResolveStage.InGameState;
            var ai = Ptr(igs + Poe2.InGameState.AreaInstanceData);
            if (ai == 0) continue;

            // S4 low fields: direct scalar reads (no sub-pointer) — patch-stable, valid early in zone load.
            _reader.TryReadStruct<uint>(ai + Poe2.AreaInstance.CurrentAreaHash, out var h);
            _reader.TryReadStruct<int>(ai + Poe2.AreaInstance.CurrentAreaLevel, out var lvl);
            var thisInZone = h != 0 && lvl >= 0 && lvl <= 100;

            var lp = Ptr(ai + Poe2.AreaInstance.LocalPlayer);
            if (lp != 0 && ReadMetadata(lp).StartsWith("Metadata/", StringComparison.Ordinal))
            {
                inGameState = igs; areaInstance = ai; localPlayer = lp; areaHash = h; areaLevel = lvl;
                return ResolveStage.Full;   // best possible — take the first fully-valid candidate
            }
            if (thisInZone)
            {
                if (best < ResolveStage.InZone)
                {
                    best = ResolveStage.InZone;
                    inGameState = igs; areaInstance = ai; areaHash = h; areaLevel = lvl;
                }
            }
            else if (best < ResolveStage.AreaInstance)
            {
                best = ResolveStage.AreaInstance;
                inGameState = igs; areaInstance = ai;
            }
        }
        return best;
    }

    /// <summary>Resolve the in-game chain. Returns false during loading / character select / a broken patch.
    /// (Now a thin wrapper over <see cref="Probe"/> — true iff the chain fully resolved.)</summary>
    public bool TryResolve(out nint inGameState, out nint areaInstance, out nint localPlayer)
    {
        var stage = Probe(out inGameState, out areaInstance, out localPlayer, out _, out _);
        if (stage == ResolveStage.Full) return true;
        inGameState = areaInstance = localPlayer = 0;
        return false;
    }

    /// <summary>Per-area instance hash. (Caches key on the AreaInstance address; this is for display/ID.)</summary>
    public uint AreaHash(nint areaInstance)
    {
        _reader.TryReadStruct<uint>(areaInstance + Poe2.AreaInstance.CurrentAreaHash, out var h);
        return h;
    }

    /// <summary>Monster/area level (validated live: 27, 32).</summary>
    public int AreaLevel(nint areaInstance)
    {
        _reader.TryReadStruct<int>(areaInstance + Poe2.AreaInstance.CurrentAreaLevel, out var l);
        return l;
    }

    private string _league = ""; private nint _leagueFor = -1;

    /// <summary>Current league name as the game stores it (ServerData @ AreaInstance+0x598 → std::wstring
    /// +0x21E0), e.g. "HC Runes of Aldur" / "Standard". The "HC " prefix distinguishes hardcore from
    /// softcore. Cached per area. (Pulled from Sikaka v0.15.0; read-only.)</summary>
    public string LeagueName(nint areaInstance)
    {
        if (areaInstance == _leagueFor) return _league;
        _leagueFor = areaInstance;
        var serverData = Ptr(areaInstance + Poe2.AreaInstance.ServerDataPtr);
        _league = serverData == 0 ? "" : ReadStdWString(serverData + Poe2.ServerData.League);
        return _league;
    }

    private string _areaCode = ""; private nint _areaCodeFor = -1;

    /// <summary>Area code identifier (e.g. "G1_town"). Cached per area.</summary>
    public string AreaCode(nint areaInstance)
    {
        if (areaInstance == _areaCodeFor) return _areaCode;
        _areaCodeFor = areaInstance;
        var info = Ptr(areaInstance + Poe2.AreaInstance.AreaInfoPtr);
        var s = Ptr(info);
        _areaCode = s == 0 ? "" : _reader.ReadStringUtf16(s, 64);
        return _areaCode;
    }

    private nint _plPlayer, _plPlayerFor;
    private nint PlayerComp(nint localPlayer)
    {
        if (localPlayer != _plPlayerFor) { _plPlayerFor = localPlayer; _plPlayer = ResolveComponent(localPlayer, "Player"); }
        return _plPlayer;
    }

    private string? _cachedPlayerName;
    private nint _cachedPlayerNameFor;
    /// <summary>Local character name (validated via StdWString @ Player+0x1B0). Cached per localPlayer address — re-read on character switch.</summary>
    public string PlayerName(nint localPlayer)
    {
        if (_cachedPlayerName != null && _cachedPlayerNameFor == localPlayer) return _cachedPlayerName;
        var c = PlayerComp(localPlayer);
        var name = c == 0 ? "" : ReadStdWString(c + Poe2.PlayerComponent.Name);
        if (!string.IsNullOrEmpty(name)) { _cachedPlayerName = name; _cachedPlayerNameFor = localPlayer; }
        return name;
    }

    /// <summary>Local character level (byte @ Player+0x204).</summary>
    public int PlayerLevel(nint localPlayer)
    {
        var c = PlayerComp(localPlayer);
        return c != 0 && _reader.TryReadStruct<byte>(c + Poe2.PlayerComponent.Level, out var b) ? b : 0;
    }

    /// <summary>Player grid position (from the Render component's world position ÷ grid ratio).</summary>
    public System.Numerics.Vector2? PlayerGrid(nint localPlayer) => EntityGrid(localPlayer);

    /// <summary>Player world position (the Render component's CurrentWorldPosition, incl. Z). RENDER-RATE
    /// safe — resolves + caches the player's Render component on demand (same path as <see cref="PlayerGrid"/>),
    /// so the render thread can anchor the guidance line at the player's live feet without the local player
    /// needing to be in the (world-rate) entity list.</summary>
    public Vector3? PlayerWorld(nint localPlayer) => EntityWorld(localPlayer);

    public readonly record struct Vitals(int HpCur, int HpUnreserved, int ManaCur, int ManaUnreserved,
        int EsCur, int EsUnreserved)
    {
        public float HpPct   => HpUnreserved   > 0 ? 100f * HpCur   / HpUnreserved   : 100f;
        public float ManaPct => ManaUnreserved > 0 ? 100f * ManaCur / ManaUnreserved : 100f;
        // ES% is 100 when there is no ES pool (Max 0, or the offset couldn't be confirmed) so an
        // "ES" / "Either" flask trigger never fires on a build that has no shield to restore.
        public float EsPct   => EsUnreserved   > 0 ? 100f * EsCur   / EsUnreserved   : 100f;
        public bool  HasEs   => EsUnreserved > 0;
    }

    private nint _plLife, _plLifeFor;

    // Self-healing vital offsets. Components are resolved by NAME (robust across patches), but the
    // VitalStruct offsets WITHIN the Life component slide between patches (e.g. 2026-06-04: Health
    // 0x1A8→0x1B0, Mana 0x1F8→0x208, ES 0x230→0x248 — each by a different small amount). We validate
    // each configured offset against a live Life component once; if it doesn't read a valid pool we
    // re-anchor it (see ResolveVitalOffset) so a minor layout shift degrades gracefully (auto-flask +
    // HP bars keep working) instead of silently reading 0. The same offsets back the monster HP reads
    // (identical component layout). Logged loudly so the table still gets updated.
    //
    // Health and ES BOTH self-heal; Mana is best-effort (kept for the mana flask but never gated on).
    // _esOffKnown gates the ES read: if ES can't be confirmed near its offset we suppress the read
    // entirely (→ ES% reads 100 → the ES/Either trigger never fires) rather than risk reading a decoy
    // and misfiring the flask.
    private int _healthOff = Poe2.Life.Health, _manaOff = Poe2.Life.Mana, _esOff = Poe2.Life.EnergyShield;
    private bool _esOffKnown = true;
    private bool _vitalOffsetsResolved;

    // Stricter than VitalStruct.LooksValid: ReservedFraction is reservation in basis-points, so a real
    // pool keeps it in [0, 10000]. The Life component is littered with decoy structs that pass the
    // loose check but carry out-of-range/garbage ReservedFraction — this filters most of them out.
    private static bool LooksLikeRealPool(in VitalStruct v)
        => v.LooksValid() && v.ReservedFraction >= 0 && v.ReservedFraction <= 10000;

    // Resolve one pool's offset within the Life component, healing small drift. Returns the configured
    // offset if it still reads a valid pool (the normal case); otherwise searches a TIGHT window
    // anchored on the configured offset and returns the valid pool nearest to it, or -1 if none. The
    // window is deliberately narrow so the distant decoy VitalStructs (verified live to sit well away
    // from each real pool) stay out of reach — we heal a slide, we don't hunt blindly.
    private int ResolveVitalOffset(nint lifeComp, int configured)
    {
        if (_reader.TryReadStruct<VitalStruct>(lifeComp + configured, out var v) && v.LooksValid())
            return configured;
        int best = -1, bestDist = int.MaxValue;
        for (var off = Math.Max(0x80, configured - 0x18); off <= configured + 0x30; off += 4)
        {
            if (_reader.TryReadStruct<VitalStruct>(lifeComp + off, out var c) && LooksLikeRealPool(c))
            {
                var d = Math.Abs(off - configured);
                if (d < bestDist) { bestDist = d; best = off; }
            }
        }
        return best;
    }

    private void EnsureVitalOffsets(nint lifeComp)
    {
        if (_vitalOffsetsResolved || lifeComp == 0) return;

        // Health is safety-critical and reliably the FIRST valid pool, so it gets an extra fallback:
        // if it won't anchor near its configured offset, take the first valid pool in the component.
        var health = ResolveVitalOffset(lifeComp, Poe2.Life.Health);
        if (health < 0)
        {
            for (var off = 0x80; off <= 0x400; off += 4)
                if (_reader.TryReadStruct<VitalStruct>(lifeComp + off, out var v) && LooksLikeRealPool(v)) { health = off; break; }
            if (health < 0) return; // not in-game yet / unreadable — retry next call (don't latch)
        }

        _vitalOffsetsResolved = true;
        _healthOff = health;
        if (_healthOff != Poe2.Life.Health)
            Console.WriteLine($"Poe2Live: Life Health offset appears to have drifted — auto-relocated " +
                $"0x{Poe2.Life.Health:X}->0x{_healthOff:X} (life flask + HP bars keep working). Update " +
                $"Poe2.Life + re-validate (Research --vitals).");

        // ES self-heals the same way; if it can't be confirmed we suppress the read (safe: ES% → 100).
        var es = ResolveVitalOffset(lifeComp, Poe2.Life.EnergyShield);
        _esOffKnown = es >= 0;
        if (es >= 0)
        {
            _esOff = es;
            if (_esOff != Poe2.Life.EnergyShield)
                Console.WriteLine($"Poe2Live: Life EnergyShield offset appears to have drifted — auto-relocated " +
                    $"0x{Poe2.Life.EnergyShield:X}->0x{_esOff:X} (ES flask keeps working). Update Poe2.Life + re-validate (Research --vitals).");
        }
        else
        {
            Console.WriteLine($"Poe2Live: Life EnergyShield offset (0x{Poe2.Life.EnergyShield:X}) couldn't be confirmed — " +
                "ES flask trigger suppressed (reads as full) until the table is updated (Research --vitals).");
        }

        // Mana: best-effort relocation only. The mana flask is never gated on a confident read — if it
        // drifts past the window it keeps the configured offset (reads 0 → mana% 100 → no misfire).
        var mana = ResolveVitalOffset(lifeComp, Poe2.Life.Mana);
        if (mana >= 0) _manaOff = mana;
    }

    /// <summary>World-thread entry point: resolve the Life component (cached) and run ONLY the
    /// vital-offset latch (the side-effect that self-heals the Health offset backing monster HP reads).
    /// Reads no VitalStruct values — no HP/Mana/ES syscalls. Call only from the instance's owning thread.</summary>
    public void EnsurePlayerVitalOffsets(nint localPlayer)
    {
        if (localPlayer != _plLifeFor) { _plLifeFor = localPlayer; _plLife = ResolveComponent(localPlayer, "Life"); }
        EnsureVitalOffsets(_plLife);
    }

    /// <summary>
    /// Local player HP/mana as current vs. *unreserved* max (auras reserve part of the pool, so
    /// raw Max would understate the real % full). Drives the auto-flask thresholds. Returns null
    /// when the Life component / vitals can't be read plausibly (Max &lt;= 0) — the caller MUST treat
    /// that as "unknown" and NOT fire flasks, rather than assuming full/empty.
    /// </summary>
    public Vitals? PlayerVitals(nint localPlayer)
    {
        if (localPlayer != _plLifeFor) { _plLifeFor = localPlayer; _plLife = ResolveComponent(localPlayer, "Life"); }
        if (_plLife == 0) return null;
        EnsureVitalOffsets(_plLife);
        if (!_reader.TryReadStruct<VitalStruct>(_plLife + _healthOff, out var hp) ||
            hp.Max <= 0 || hp.Current > hp.Max)
        {
            if (++_vitalBadReadCount >= VitalBadReadThreshold) _vitalOffsetsResolved = false;
            return null;
        }
        _vitalBadReadCount = 0;
        _reader.TryReadStruct<VitalStruct>(_plLife + _manaOff, out var mana);
        VitalStruct es = default; // suppressed (stays 0 → ES% 100) when the offset isn't confirmed
        if (_esOffKnown) _reader.TryReadStruct<VitalStruct>(_plLife + _esOff, out es);
        return new Vitals(hp.Current, Unreserved(hp), mana.Current, Unreserved(mana), es.Current, Unreserved(es));
    }

    private static int Unreserved(VitalStruct v)
    {
        var reserved = (int)Math.Ceiling(v.ReservedFraction / 10000f * v.Max) + v.ReservedFlat;
        return Math.Max(0, v.Max - reserved);
    }

    /// <summary>
    /// Walk the awake-entity std::map and project each to a grid dot with a category. Visuals /
    /// decorations (id ≥ 0x40000000) are skipped. Render addresses + categories are cached per
    /// entity for the area's lifetime; the per-tick cost is then ~1 pointer read per entity.
    /// </summary>
    public List<EntityDot> Entities(nint areaInstance)
    {
        if (areaInstance != _entCacheKey)
        {
            _renderAddr.Clear(); _lifeAddr.Clear(); _posAddr.Clear(); _ompAddr.Clear(); _chestAddr.Clear(); _openedChests.Clear(); _completedPois.Clear(); _iconState.Clear();
            _category.Clear(); _meta.Clear(); _iconAddr.Clear(); _rarity.Clear(); _reaction.Clear(); _mods.Clear(); _itemIdent.Clear(); _idAt.Clear(); _monolithCache.Clear(); _buffsAddr.Clear(); _buffId.Clear();
            _entCacheKey = areaInstance;
        }

        _reactionTick++;
        if (ShouldRefreshReaction(_reactionTick, ReactionRefreshTicks)) _reaction.Clear();

        _entityBufIdx = (_entityBufIdx + 1) % 3;
        var dots = _entityBufs[_entityBufIdx];
        dots.Clear();
        var head = Ptr(areaInstance + Poe2.AreaInstance.AwakeEntities);
        _reader.TryReadStruct<int>(areaInstance + Poe2.AreaInstance.AwakeEntities + 8, out var size);
        if (head == 0 || size <= 0 || size > 100000) return dots;

        var root = Ptr(head + Poe2.StdMapNode.Parent);
        _entQueue.Clear(); _entQueue.Enqueue(root);
        _entVisited.Clear();
        _modReadBudget = ModReadBudgetPerPass;
        _itemReadBudget = ItemReadBudgetPerPass;
        _iconTick++;
        var walkCap = size * 4 + 1024;   // E7: scale cap to entity count (balanced BST of N has N+1 nodes; 4× is generous)
        Span<nint> batchR = stackalloc nint[6];   // reused per new entity; declared outside the loop (CA2014)
        while (_entQueue.Count > 0 && _entVisited.Count < walkCap)
        {
            var node = _entQueue.Dequeue();
            if (node == 0 || node == head || !_entVisited.Add(node)) continue;

            // One read for the whole node — Left/Right/IsNil/KeyId/ValueEntityPtr are contiguous in
            // 48 bytes, so this replaces 5 separate ReadProcessMemory syscalls per node with one.
            if (_reader.TryReadBytes(node, _nodeBuf) < _nodeBuf.Length) continue;
            if (_nodeBuf[Poe2.StdMapNode.IsNil] != 0) continue; // sentinel/nil — don't traverse its children

            var id = BitConverter.ToUInt32(_nodeBuf, Poe2.StdMapNode.KeyId);
            var entity = (nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.ValueEntityPtr);
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Left));
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Right));

            if (entity == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;

            // Recycle guard: entity object addresses are reused within an area as things die/spawn.
            // The std::map key id is the stable per-entity identity (monotonic, never reused in an
            // area), so if THIS address now carries a different id than we cached it under, the prior
            // occupant is gone — evict its frozen component addresses/category/rarity/icon so we don't
            // read a freed/reused Life or Render (stale HP bars over corpses, POIs flickering at stale
            // positions). Re-resolves fresh below.
            if (_idAt.TryGetValue(entity, out var prevId) && prevId != id) EvictEntity(entity);
            _idAt[entity] = id;

            // Batch-resolve the 6 hot-path components in ONE bucket read for newly-seen entities.
            // Per-component methods keep their TryGetValue→resolve fallback (now always a cache hit).
            if (!_renderAddr.ContainsKey(entity))
            {
                ResolveComponents(entity, _hotComponents, batchR);
                _renderAddr[entity] = batchR[0];
                _posAddr[entity]    = batchR[1];
                _lifeAddr[entity]   = batchR[2];
                _ompAddr[entity]    = batchR[3];
                _iconAddr[entity]   = batchR[4];
                _chestAddr[entity]  = batchR[5];
            }

            var world = EntityWorld(entity);
            if (world is not { } wv) continue;
            var g = new System.Numerics.Vector2(wv.X / Poe2.WorldToGridRatio, wv.Y / Poe2.WorldToGridRatio);

            var cat = Categorize(entity);
            int hpCur = 0, hpMax = 0;
            var rarity = Rarity.NonMonster;
            var opened = false;
            if (cat == EntityCategory.Monster) (hpCur, hpMax) = ReadHp(entity);   // SR-5d: player HP comes from render-thread PlayerVitals
            if (cat is EntityCategory.Monster or EntityCategory.Chest) rarity = ReadRarity(entity);
            if (cat == EntityCategory.Chest) opened = ReadChestOpened(entity);
            var mods = (EnableModReads && cat == EntityCategory.Monster) ? ReadMods(entity) : null;
            var meta = _meta.GetValueOrDefault(entity, "");
            // Dropped items (WorldItem containers, categorized Other) carry a price-lookup identity: art
            // basename + rarity, read once off the inner item entity. Rarity then reflects the item.
            string? itemArt = null, itemName = null;
            var itemIdentified = true;
            IReadOnlyList<RawAffix>? itemAffixes = null;
            if (EnableItemIdentityReads && cat == EntityCategory.Other && meta.Contains("WorldItem", StringComparison.Ordinal))
            {
                var itemIdentity = ReadItemIdentity(entity);
                var (rarityTemp, itemArtTemp, itemIdentifiedTemp, itemNameTemp) = itemIdentity;
                rarity = rarityTemp;
                itemArt = itemArtTemp;
                itemIdentified = itemIdentifiedTemp;
                itemName = itemNameTemp;
                // Get affixes from the cache
                if (_itemIdent.TryGetValue(entity, out var cachedIdentity))
                {
                    itemAffixes = cachedIdentity.affixes;
                }
            }

            var (poi, iconComplete) = ReadIcon(entity);
            dots.Add(new EntityDot(id, entity, g, wv, cat, meta, hpCur, hpMax,
                poi, ReadReaction(entity), rarity, opened, iconComplete, mods, itemArt, itemIdentified, itemName, itemAffixes));
        }
        return dots;
    }

    /// <summary>Drop every frozen per-entity cache entry for an address whose occupant has changed
    /// (the std::map key id no longer matches). Forces a fresh component re-resolve next read.</summary>
    private void EvictEntity(nint entity)
    {
        _renderAddr.Remove(entity); _lifeAddr.Remove(entity); _posAddr.Remove(entity);
        _ompAddr.Remove(entity); _chestAddr.Remove(entity); _openedChests.Remove(entity); _completedPois.Remove(entity); _iconState.Remove(entity); _category.Remove(entity);
        _meta.Remove(entity); _iconAddr.Remove(entity); _rarity.Remove(entity); _reaction.Remove(entity); _mods.Remove(entity); _itemIdent.Remove(entity); _monolithCache.Remove(entity); _buffsAddr.Remove(entity);
    }

    /// <summary>
    /// The entity's POI state from its MinimapIcon component:
    /// <list type="bullet">
    /// <item><c>poi</c> — the game marks it as a map POI (component present).</item>
    /// <item><c>complete</c> — the game has FADED the icon because its encounter is finished
    ///   (CompletedState != 0). The component stays put once resolved, so we cache only its ADDRESS
    ///   and read the flag live every tick (it flips, e.g. on claiming an expedition reward).</item>
    /// </list>
    /// </summary>
    private (bool poi, bool complete) ReadIcon(nint entity)
    {
        if (_completedPois.Contains(entity)) return (true, true);          // one-way: never re-read a completed POI
        if (_iconState.TryGetValue(entity, out var last) && (_iconTick - last.tick) < 10)
            return (last.poi, last.complete);                              // slow-refresh: reuse recent result
        if (!_iconAddr.TryGetValue(entity, out var icon))
        {
            icon = ResolveComponent(entity, "MinimapIcon");
            _iconAddr[entity] = icon; // cache even if 0, to avoid re-walking non-POI entities
        }
        if (icon == 0) { return (false, false); }  // non-POI: no _iconState write (would mask a dynamic MinimapIcon acquisition)
        var complete = _reader.TryReadStruct<int>(icon + Poe2.MinimapIcon.CompletedState, out var s) && s != 0;
        if (complete) _completedPois.Add(entity);
        _iconState[entity] = (_iconTick, true, complete);
        return (true, complete);
    }

    /// <summary>The live state of a runeshape-monolith device (the persistent Expedition2Encounter POI
    /// entity), read off the device → StateMachine → RuneStation chain (see <see cref="Poe2.RuneStation"/>).
    /// <paramref name="Resolved"/> false means the station chain didn't resolve (e.g. transient read, or the
    /// device isn't a monolith). Feeds <see cref="RuneMonolithCatalog.Offers"/> to compute the rewards the
    /// monolith will offer WITHOUT opening its panel. Persists out of the network bubble → readable
    /// area-wide. <paramref name="Collected"/> = the MinimapIcon completed flag (reward already claimed).</summary>
    public readonly record struct MonolithState(
        bool Resolved, int HoleCount, int AnchorIdx, int AnchorPos, bool IsUnique, bool Collected);

    /// <summary>Resolve a monolith device's hole count + anchor rune. Caches the static station data
    /// (HoleCount/AnchorIdx/AnchorPos/IsUnique) per device; only <c>Collected</c> is re-read live each tick.
    /// <c>Resolved == true</c> results are cached; transient failures (Resolved false) are never cached so
    /// they retry next tick.</summary>
    public MonolithState ReadMonolith(nint device)
    {
        var (_, collected) = ReadIcon(device);
        if (_monolithCache.TryGetValue(device, out var cachedMono))
            return cachedMono with { Collected = collected };

        var fail = new MonolithState(false, 0, -1, -1, false, collected);

        var sm = ResolveComponent(device, "StateMachine");
        if (sm == 0) return fail;
        var first = Ptr(sm + Poe2.StateMachine.ListenerVec);
        if (first == 0 || !_reader.TryReadStruct<nint>(sm + Poe2.StateMachine.ListenerVec + 8, out var last)) return fail;
        var n = ((long)last - first) / 8;
        if (n is <= 0 or > 256) return fail;

        nint station = 0;
        for (long i = 0; i < n; i++)
        {
            var node = Ptr(first + (nint)(i * 8));
            if (node == 0) continue;
            var sub = Ptr(node);                                   // = station + ListenerSub
            if (sub == 0) continue;
            var cand = sub - Poe2.RuneStation.ListenerSub;
            if (Ptr(cand + Poe2.RuneStation.Owner) == device) { station = cand; break; }
        }
        if (station == 0) return fail;

        if (!_reader.TryReadStruct<int>(station + Poe2.RuneStation.HoleCount, out var holes) || holes is <= 0 or > 16)
            return fail;
        _reader.TryReadStruct<int>(station + Poe2.RuneStation.AnchorPos, out var pos);

        var rowPtr = Ptr(station + Poe2.RuneStation.AnchorRef);
        if (rowPtr == 0)
        {
            var uniqueResult = new MonolithState(true, holes, -1, -1, true, collected); // anchor-less → "unique"
            _monolithCache[device] = uniqueResult;
            return uniqueResult;
        }

        // anchor rune index = (rowPtr − tableBase)/RuneStride; tableBase is per-area (deref holder+0x28).
        var holder = Ptr(station + Poe2.RuneStation.AnchorHolder);
        var p1 = Ptr(holder + 0x28);
        if (p1 == 0 || !_reader.TryReadStruct<long>(p1, out var tableBase) || tableBase == 0)
        {
            var partialResult = new MonolithState(true, holes, -1, pos, false, collected); // station ok, anchor decode failed
            _monolithCache[device] = partialResult;
            return partialResult;
        }
        var delta = (long)rowPtr - tableBase;
        var idx = (delta >= 0 && delta % Poe2.RuneStation.RuneStride == 0)
            ? (int)(delta / Poe2.RuneStation.RuneStride) : -1;
        if (idx < 0 || idx >= Poe2.RuneStation.RuneCount) idx = -1;
        var result = new MonolithState(true, holes, idx, pos, false, collected);
        _monolithCache[device] = result;
        return result;
    }

    private Rarity ReadRarity(nint entity)
    {
        // Rarity is fixed at spawn — read it once per entity and cache the value (not just the addr).
        if (_rarity.TryGetValue(entity, out var cached)) return cached;
        if (!_ompAddr.TryGetValue(entity, out var omp))
        {
            omp = ResolveComponent(entity, "ObjectMagicProperties");
            _ompAddr[entity] = omp;
        }
        if (omp == 0) { _rarity[entity] = Rarity.Normal; return Rarity.Normal; }
        if (!_reader.TryReadStruct<int>(omp + Poe2.ObjectMagicProperties.Rarity, out var r))
            return Rarity.Normal; // transient read failure — don't poison the cache
        var rarity = r is >= 0 and <= 3 ? (Rarity)r : Rarity.Normal;
        _rarity[entity] = rarity;
        return rarity;
    }

    /// <summary>
    /// The monster's affix mod ids (auras/buffs) from ObjectMagicProperties+Mods. Like rarity, mods are
    /// fixed at spawn, so the result is cached per entity (even when empty) and read at most once. New
    /// (uncached) reads are bounded by <see cref="_modReadBudget"/> per pass so a fresh pack fills over a
    /// few world ticks rather than stalling one. Reads ONLY the rolled-affix vector (+0x168); the +0x150
    /// rarity-placeholder filler (MonsterRare/Magic/Unique{N}) is intentionally excluded.
    /// </summary>
    private string[]? ReadMods(nint entity)
    {
        if (_mods.TryGetValue(entity, out var cached)) return cached.Length == 0 ? null : cached;
        if (_modReadBudget <= 0) return null;                  // out of budget this pass — retry next tick (don't cache)

        if (!_ompAddr.TryGetValue(entity, out var omp))
        {
            omp = ResolveComponent(entity, "ObjectMagicProperties");
            _ompAddr[entity] = omp;
        }
        if (omp == 0) { _mods[entity] = Array.Empty<string>(); return null; }
        _modReadBudget--;

        // StdVector at omp+Mods: [First, Last, End]. Element stride ModElemStride; each element holds a
        // record pointer at +ModRecordPtr; the record's +ModIdString is a UTF-16 mod-id string.
        if (_reader.TryReadBytes(omp + Poe2.ObjectMagicProperties.Mods, _modVecBuf) < _modVecBuf.Length)
            return null; // transient read failure — leave uncached, retry next tick
        var first = (nint)BitConverter.ToInt64(_modVecBuf, 0);
        var last = (nint)BitConverter.ToInt64(_modVecBuf, 8);
        var len = (long)last - first;
        const int stride = Poe2.ObjectMagicProperties.ModElemStride;
        if (first == 0 || len <= 0 || len > 0x4000 || len % stride != 0)
        {
            _mods[entity] = Array.Empty<string>(); return null; // no/garbage affix vector — cache as empty
        }
        var n = (int)(len / stride);
        if (n > 100) { _mods[entity] = Array.Empty<string>(); return null; }

        Span<byte> arrBuf = stackalloc byte[n * stride];   // n<=100, stride=0x40 -> <=6400 bytes
        if (_reader.TryReadBytes(first, arrBuf) < arrBuf.Length) { _mods[entity] = Array.Empty<string>(); return null; }

        var list = new List<string>(n);
        var seen = new HashSet<string>();
        for (var i = 0; i < n; i++)
        {
            var rec = (nint)BitConverter.ToInt64(arrBuf.Slice(i * stride + Poe2.ObjectMagicProperties.ModRecordPtr, 8));
            if (rec == 0) continue;
            // record's +ModIdString qword is a POINTER to the UTF-16 mod id (not the string inline) — must
            // deref even when the offset is 0 (Ptr(rec+0) = *rec). Skipping this deref read the record
            // pointer's own bytes as text → garbage → every monster cached as "no mods".
            var idPtr = Ptr(rec + Poe2.ObjectMagicProperties.ModIdString);
            if (idPtr == 0) continue;
            var s = _reader.ReadStringUtf16(idPtr, 64);
            if (LooksLikeModId(s) && seen.Add(s)) list.Add(s);
        }
        var arr = list.Count == 0 ? Array.Empty<string>() : list.ToArray();
        _mods[entity] = arr;
        return arr.Length == 0 ? null : arr;
    }

    /// <summary>Active buffs on an entity: walk the Buffs component's StatusEffect vector, decode each buff's
    /// internal id (cached per StatusEffect — static per instance) + timer. Empty when the feature is off
    /// (EnableBuffReads) or the entity has no Buffs component. Read-only; the only new memory read this release.</summary>
    public IReadOnlyList<BuffState> Buffs(nint entity)
    {
        var result = new List<BuffState>();
        if (!EnableBuffReads) return result;
        if (!_buffsAddr.TryGetValue(entity, out var comp)) { comp = ResolveComponent(entity, "Buffs"); _buffsAddr[entity] = comp; }
        if (comp == 0) return result;

        var first = Ptr(comp + Poe2.BuffsComponent.BuffVector);
        if (first == 0 || !_reader.TryReadStruct<nint>(comp + Poe2.BuffsComponent.BuffVector + 8, out var last) || last == 0) return result;
        var count = (int)(((long)last - (long)first) / 8);
        if (count <= 0 || count > 128) return result;   // sanity bound

        for (var i = 0; i < count; i++)
        {
            var se = Ptr(first + (nint)(i * 8));
            if (se == 0) continue;
            if (!_buffId.TryGetValue(se, out var id))
            {
                var def = Ptr(se + Poe2.StatusEffect.Definition);
                var idPtr = def == 0 ? 0 : Ptr(def + Poe2.BuffDefinition.IdPtr);
                id = idPtr == 0 ? "" : _reader.ReadStringUtf16(idPtr, 128);
                _buffId[se] = id;
            }
            if (string.IsNullOrEmpty(id)) continue;
            // Keep the buff (its id read cleanly); a failed/Inf/≤0 timer read → treat as permanent (no
            // countdown), never dropping a known buff. Folding the read result in keeps Permanent authoritative.
            var perm = !_reader.TryReadStruct<float>(se + Poe2.StatusEffect.Timer, out var t)
                || float.IsInfinity(t) || float.IsNaN(t) || t <= 0f;
            result.Add(new BuffState(id, perm ? 0f : t, perm));
        }
        return result;
    }

    /// <summary>
    /// Resolve a dropped item's identity for price lookup: unwrap the WorldItem container → inner item
    /// entity, read its rarity (Mods+0x94) and 2D-art basename (RenderItem+0x28 → UTF-16 .dds path). Like
    /// other item facts these are fixed once dropped, so the result is cached per entity and read at most
    /// once; new reads are bounded per pass by <see cref="_itemReadBudget"/>. Returns (rarity, artBasename);
    /// artBasename is null when the item can't be resolved.
    /// </summary>
    private (Rarity, string?, bool, string?) ReadItemIdentity(nint entity)
    {
        if (_itemIdent.TryGetValue(entity, out var cached)) return (cached.rarity, cached.art, cached.identified, cached.name);
        if (_itemReadBudget <= 0) return (Rarity.NonMonster, null, true, null);   // out of budget — retry next tick (don't cache)

        var wi = ResolveComponent(entity, "WorldItem");
        var item = wi == 0 ? 0 : Ptr(wi + Poe2.WorldItemComponent.ItemEntity);
        if (item == 0) { var v = (Rarity.NonMonster, (string?)null, true, (string?)null, (IReadOnlyList<RawAffix>?)null); _itemIdent[entity] = v; return (v.Item1, v.Item2, v.Item3, v.Item4); }
        _itemReadBudget--;

        var result0 = ReadIdentityFromItem(item);
        _itemIdent[entity] = result0;
        return (result0.rarity, result0.art, result0.identified, result0.name);
    }

    /// <summary>Read an item ENTITY's identity directly (rarity/art/identified/base-type name) — the shared
    /// core of <see cref="ReadItemIdentity"/> without the WorldItem unwrap or per-entity cache, for callers
    /// (ritual shop, inventory) that already hold the item entity. See the field notes inline.</summary>
    private (Rarity rarity, string? art, bool identified, string? name, IReadOnlyList<RawAffix>? affixes) ReadIdentityFromItem(nint item)
    {
        // Rarity (+0x94) + Identified (+0x90) from the item's Mods component (distinct from monster
        // ObjectMagicProperties+0x144). Identified defaults true (non-uniques / no Mods comp aren't "unID").
        var rarity = Rarity.NonMonster;
        var identified = true;
        var modsComp = ResolveComponent(item, "Mods");
        if (modsComp != 0)
        {
            if (_reader.TryReadStruct<int>(modsComp + Poe2.ModsComponent.Rarity, out var r) && r is >= 0 and <= 3)
                rarity = (Rarity)r;
            if (_reader.TryReadStruct<int>(modsComp + Poe2.ModsComponent.Identified, out var idf))
                identified = idf != 0;
        }

        // 2D-art .dds path → basename (the price key). RenderItem+0x28 is a pointer to the UTF-16 path.
        string? art = null;
        var renderItem = ResolveComponent(item, "RenderItem");
        if (renderItem != 0)
        {
            var pathPtr = Ptr(renderItem + Poe2.RenderItemComponent.ResourcePath);
            if (pathPtr != 0)
            {
                var full = _reader.ReadStringUtf16(pathPtr, 128);
                art = ArtBasename(full);
            }
        }

        // Rendered base-type display NAME (Base +0x10 → row +0x30 → UTF-16). The price key for NON-uniques:
        // currency/runes/essences TIERS share one .dds art (Orb/Greater/Perfect → "CurrencyAddModToMagic"),
        // so only the exact name (e.g. "Greater Orb of Augmentation") disambiguates them.
        string? name = null;
        var baseComp = ResolveComponent(item, "Base");
        if (baseComp != 0)
        {
            var nameRow = Ptr(baseComp + Poe2.BaseComponent.NameRow);
            var namePtr = nameRow == 0 ? 0 : Ptr(nameRow + Poe2.BaseComponent.RowDisplayName);
            if (namePtr != 0) { var s = _reader.ReadStringUtf16(namePtr, 64); if (!string.IsNullOrWhiteSpace(s)) name = s.Trim(); }
        }

        // Read implicit + explicit affixes from the Mods component
        IReadOnlyList<RawAffix>? affixes = null;
        if (modsComp != 0)
        {
            var affixList = new List<RawAffix>();
            ReadRawAffixesInto(modsComp + Poe2.ModsComponent.ImplicitMods, affixList);
            ReadRawAffixesInto(modsComp + Poe2.ModsComponent.ExplicitMods, affixList);
            affixes = affixList.Count > 0 ? affixList : null;
        }

        return (rarity, art, identified, name, affixes);
    }

    /// <summary>"Art/2DItems/Weapons/.../Uniques/Earthbound.dds" → "Earthbound" (last path segment, no
    /// extension). Returns null for empty/garbage so callers can ignore it.</summary>
    private static string? ArtBasename(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var slash = path.LastIndexOf('/');
        var start = slash >= 0 ? slash + 1 : 0;
        var dot = path.LastIndexOf('.');
        var end = dot > start ? dot : path.Length;
        if (end <= start) return null;
        var name = path[start..end];
        return name.Length >= 2 ? name : null;
    }

    /// <summary>A GGG mod id is a non-trivial identifier: letters/digits/underscore only, has a letter.</summary>
    private static bool LooksLikeModId(string s)
    {
        if (s.Length is < 3 or > 64) return false;
        var hasLetter = false;
        foreach (var c in s)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) { hasLetter = true; continue; }
            if (c is (>= '0' and <= '9') or '_') continue;
            return false;
        }
        return hasLetter;
    }

    private byte ReadReaction(nint entity)
    {
        if (_reaction.TryGetValue(entity, out var cached)) return cached;
        if (!_posAddr.TryGetValue(entity, out var pos))
        {
            pos = ResolveComponent(entity, "Positioned");
            _posAddr[entity] = pos;
        }
        if (pos == 0) { _reaction[entity] = 0; return 0; }
        var b = _reader.TryReadStruct<byte>(pos + Poe2.Positioned.Reaction, out var v) ? v : (byte)0;
        _reaction[entity] = b;
        return b;
    }

    private (int cur, int max) ReadHp(nint entity)
    {
        if (!_lifeAddr.TryGetValue(entity, out var life))
        {
            life = ResolveComponent(entity, "Life");
            _lifeAddr[entity] = life;
        }
        if (life == 0) return (0, 0);
        // Use the (possibly auto-relocated) Health offset so monster HP bars survive the same vital-
        // block drift the player vitals do. _healthOff == Poe2.Life.Health unless drift was detected.
        if (!_reader.TryReadStruct<VitalStruct>(life + _healthOff, out var v)) return (0, 0);
        return (v.Current, v.Max);
    }

    private List<Landmark>? _landmarks;
    private nint _landmarksKey = -1;

    /// <summary>Optional Overlay-supplied matcher: given a tile path, returns a friendly label (possibly
    /// empty) when the user wants that tile surfaced as a landmark, or null to ignore it. Lets users add
    /// their own landmark/tile patterns at runtime on top of the built-in keyword filter + curated list.
    /// Set by RadarApp; call <see cref="InvalidateLandmarks"/> after the pattern set changes so the
    /// per-area scan cache rebuilds.</summary>
    public Func<string, string?>? CustomLandmarkMatch { get; set; }

    /// <summary>Optional Overlay-supplied curated-label lookup: (areaCode, tilePath) → friendly label,
    /// or null. Lets a user-editable overlay sit on top of the baked-in <see cref="CustomLandmarkData"/>
    /// (the "Landmarks" tab). When unset, the baked data is used directly. Call <see cref="InvalidateLandmarks"/>
    /// after edits so the per-area scan rebuilds.</summary>
    public Func<string, string, string?>? CuratedLookup { get; set; }

    /// <summary>Resolve a tile's curated label: the injected user overlay if wired, else the baked list.</summary>
    private string? Curated(string areaCode, string tilePath)
        => CuratedLookup is { } f ? f(areaCode, tilePath) : CustomLandmarkData.TryMatch(areaCode, tilePath);

    /// <summary>Max gap (in TILES, Chebyshev) between cells still treated as one landmark cluster.
    /// Larger merges nearby copies of a reusable tile into fewer markers; smaller splits them. Set by
    /// the Overlay from <c>RadarSettings.LandmarkClusterGap</c>; call <see cref="InvalidateLandmarks"/>
    /// after changing it so the per-area scan rebuilds. Clamped to a sane range when used.</summary>
    public int LandmarkClusterGap { get; set; } = 2;

    /// <summary>Drop the cached per-area landmark scan so the next <see cref="Landmarks"/> call rebuilds
    /// it (e.g. after the user edits the custom landmark patterns from the dashboard).</summary>
    public void InvalidateLandmarks() => _landmarksKey = -1;

    private List<string>? _tilePaths;
    private nint _tilePathsKey = -1;

    /// <summary>
    /// All DISTINCT terrain-tile paths in the area (sorted), scanned once per area and cached. This is
    /// the full vocabulary of tile names — what the dashboard's add-rule picker browses so a tile rule
    /// can target any tile, not just the ones already surfaced as landmarks.
    /// </summary>
    public IReadOnlyList<string> TilePaths(nint areaInstance)
    {
        if (areaInstance == _tilePathsKey && _tilePaths is not null) return _tilePaths;
        _tilePathsKey = areaInstance;
        _tilePaths = ScanTilePaths(areaInstance);
        return _tilePaths;
    }

    private List<string> ScanTilePaths(nint areaInstance)
    {
        var result = new List<string>();
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        if (!_reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX) || tilesX <= 0) return result;
        var first = Ptr(terrain + Poe2.Terrain.TileDetailsPtr);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var last) || first == 0) return result;
        var count = ((long)last - (long)first) / Poe2.TileStructureSize;
        if (count is <= 0 or > 1_000_000) return result;

        // Distinct by TgtFilePtr (one read per tile type — dozens, not per tile), collect the paths.
        var seenPtr = new HashSet<nint>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (long i = 0; i < count; i++)
        {
            var tgt = Ptr(first + (nint)(i * Poe2.TileStructureSize) + Poe2.TileStructure.TgtFilePtr);
            if (tgt == 0 || !seenPtr.Add(tgt)) continue;
            var p = ReadStdWString(tgt + Poe2.TgtFileStruct.TgtPath);
            if (!string.IsNullOrEmpty(p)) paths.Add(p);
        }
        result.AddRange(paths);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Static tile-based landmarks for the area (boss arenas, treasure, waypoints, mechanics…).
    /// Scans the terrain tile grid once per area (cached): each tile's TgtPath, grouped by path
    /// for "interesting" features, with the grid centroid of each group. This is the pre-explored
    /// "X is over here" layer — terrain-feature granularity, not a per-monster spawn table.
    /// </summary>
    public IReadOnlyList<Landmark> Landmarks(nint areaInstance)
    {
        if (areaInstance == _landmarksKey && _landmarks is not null) return _landmarks;
        _landmarksKey = areaInstance;
        _landmarks = ScanLandmarks(areaInstance);
        return _landmarks;
    }

    private List<Landmark> ScanLandmarks(nint areaInstance)
    {
        var result = new List<Landmark>();
        var areaCode = AreaCode(areaInstance);
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        if (!_reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX) || tilesX <= 0) return result;
        var first = Ptr(terrain + Poe2.Terrain.TileDetailsPtr);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var last) || first == 0) return result;
        var count = ((long)last - (long)first) / Poe2.TileStructureSize;
        if (count is <= 0 or > 1_000_000) return result;

        // Collect each kept path's tile cells (in tile-index space) so we can CLUSTER them spatially
        // rather than average every instance into one centroid. A reusable tile (e.g. a "stairs up"
        // wall piece) recurs in several disjoint spots — multi-level dungeons have multiple stair-up /
        // stair-down sections connecting layers — and averaging them lands a marker in the dead space
        // between, pointing at nothing. Clustering yields one landmark per actual spot. Cache path by
        // TgtFilePtr so we read each distinct tile type's StdWString once (dozens), not per tile.
        var pathCache = new Dictionary<nint, string?>();
        var cellsByPath = new Dictionary<string, List<(int tx, int ty)>>();

        for (long i = 0; i < count; i++)
        {
            var tile = first + (nint)(i * Poe2.TileStructureSize);
            var tgtFile = Ptr(tile + Poe2.TileStructure.TgtFilePtr);
            if (tgtFile == 0) continue;
            if (!pathCache.TryGetValue(tgtFile, out var path))
            {
                var p = ReadStdWString(tgtFile + Poe2.TgtFileStruct.TgtPath);
                // Surface a tile as a landmark ONLY if the curated community list names it for this area
                // OR a user "Tile" display rule matches it (CustomLandmarkMatch). The old generic keyword
                // sweep was removed — it surfaced decorative terrain (e.g. every "...Vault_Door..." tile)
                // as noise; users now opt into any tile via Tile rules + the dashboard picker.
                var keep = Curated(areaCode, p) != null
                           || CustomLandmarkMatch?.Invoke(p) != null;
                path = keep ? p : null;
                pathCache[tgtFile] = path;
            }
            if (path is null) continue;
            (cellsByPath.TryGetValue(path, out var cells) ? cells : cellsByPath[path] = new())
                .Add(((int)(i % tilesX), (int)(i / tilesX)));
        }

        var cell = Poe2.Terrain.TileGridCells;
        foreach (var (path, cells) in cellsByPath)
        {
            var name = LandmarkName(path);
            // Curated label wins; else a non-empty user label; else null (derived name shows). Same
            // for every cluster of this path (they're the same feature type in different spots).
            var curated = Curated(areaCode, path) ?? NonEmpty(CustomLandmarkMatch?.Invoke(path));
            foreach (var cluster in ClusterTiles(cells, Math.Clamp(LandmarkClusterGap, 0, 64)))
            {
                double sx = 0, sy = 0;
                foreach (var (tx, ty) in cluster) { sx += tx; sy += ty; }
                var center = new System.Numerics.Vector2(
                    (float)(sx / cluster.Count * cell), (float)(sy / cluster.Count * cell));
                result.Add(new Landmark(name, path, center, cluster.Count, curated));
            }
        }
        return result;
    }

    /// <summary>
    /// Group same-path tile cells into spatially-disjoint clusters: two cells join when within a
    /// Chebyshev gap of <c>≤ gap</c> tiles (gap=2 bridges a one-tile hole inside a feature while
    /// keeping well-separated copies apart; larger merges more, 0 = only directly-touching cells).
    /// Plain BFS over a cell set — O(tiles) for the small kept-path counts, so a tile type that recurs
    /// across the map yields one cluster per location instead of a single meaningless average.
    /// </summary>
    private static List<List<(int tx, int ty)>> ClusterTiles(List<(int tx, int ty)> cells, int gap)
    {
        var set = new HashSet<(int, int)>(cells);
        var visited = new HashSet<(int, int)>();
        var clusters = new List<List<(int tx, int ty)>>();
        var queue = new Queue<(int, int)>();
        foreach (var start in cells)
        {
            if (!visited.Add(start)) continue;
            var cluster = new List<(int tx, int ty)>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                cluster.Add((cx, cy));
                for (var dx = -gap; dx <= gap; dx++)
                    for (var dy = -gap; dy <= gap; dy++)
                    {
                        var nb = (cx + dx, cy + dy);
                        if (set.Contains(nb) && visited.Add(nb)) queue.Enqueue(nb);
                    }
            }
            clusters.Add(cluster);
        }
        return clusters;
    }

    /// <summary>Null for null/empty, else the string — so an empty user label means "surface but use the
    /// path-derived name" rather than showing a blank curated label.</summary>
    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string LandmarkName(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Read the packed walkable grid (one nibble per cell, 2 cells/byte) into a flat 0/1 array.</summary>
    public TerrainData? Terrain(nint areaInstance)
    {
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        var first = Ptr(terrain + Poe2.Terrain.GridWalkableData);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.GridWalkableData + 8, out var last) || last == 0) return null;
        if (!_reader.TryReadStruct<int>(terrain + Poe2.Terrain.BytesPerRow, out var bytesPerRow) || bytesPerRow <= 0 || bytesPerRow > 65536) return null;
        var totalBytes = (long)last - (long)first;
        if (first == 0 || totalBytes <= 0 || totalBytes > 64 * 1024 * 1024) return null;

        var rows = (int)(totalBytes / bytesPerRow);
        var width = bytesPerRow * 2;
        if (rows <= 0 || rows > 65536) return null;

        byte[] raw = ArrayPool<byte>.Shared.Rent((int)totalBytes);
        try
        {
            if (_reader.TryReadBytes(first, raw.AsSpan(0, (int)totalBytes)) != (int)totalBytes) return null;

            var walk = new byte[width * rows];   // retained by TerrainData — NOT pooled
            for (var y = 0; y < rows; y++)
            {
                var rowBase = (long)y * bytesPerRow;
                for (var x = 0; x < width; x++)
                {
                    var b = raw[rowBase + (x >> 1)];
                    var nibble = (x & 1) == 0 ? (b & 0x0F) : (b >> 4);
                    walk[y * width + x] = (byte)(nibble != 0 ? 1 : 0);
                }
            }
            return new TerrainData(walk, width, rows);
        }
        finally { ArrayPool<byte>.Shared.Return(raw); }
    }

    private readonly List<nint> _mapEls = new();
    private readonly HashSet<nint> _everHidden = new();  // elements observed with visible-bit clear
    private readonly HashSet<nint> _everVisible = new(); // elements observed with visible-bit set
    private nint _mapCacheKey = -1;

    /// <summary>
    /// Map UI state. The MapUiElements (DefaultShift=(0,-20), Zoom=0.5) are discovered once per area
    /// and cached — per frame we only read their flags/shift/zoom (cheap). The game exposes several:
    /// some are always-on, some always-off, and one is the minimap viewport whose visible bit Tab
    /// toggles. We gate "map open" on a *genuine toggler* — an element observed BOTH visible and
    /// hidden — so a permanently-hidden element can't masquerade as the toggle signal (the bug that
    /// pinned this to "closed" once the UI began exposing 4 elements instead of 2). Projection
    /// params (shift/zoom) come from a currently-visible toggler. Until the first toggle is observed
    /// this area, fall back to "more than the always-on baseline visible" (&gt;=2).
    /// </summary>
    public MapUi ReadMap(nint inGameState, nint areaInstance)
    {
        if (areaInstance != _mapCacheKey || _mapEls.Count == 0)
        {
            _mapCacheKey = areaInstance;
            _mapEls.Clear();
            _everHidden.Clear();
            _everVisible.Clear();
            DiscoverMapElements(inGameState);
        }

        var visibleCount = 0;
        var any = false; MapUi anyUi = default;
        var sawToggler = false; var togglerVisible = false; var haveTogglerUi = false; MapUi togglerUi = default;
        foreach (var el in _mapEls)
        {
            // Self==el liveness guard: recycled UI element slots may have stale Self pointers from the previous occupant
            if (!_reader.TryReadStruct<nint>(el + Poe2.UiElement.Self, out var self) || self != el) continue;
            if (!TryReadMapElement(el, out var vis, out var sx, out var sy, out var zoom)) continue;
            if (vis) { _everVisible.Add(el); visibleCount++; } else _everHidden.Add(el);
            if (!any) { any = true; anyUi = new MapUi(vis, sx, sy, zoom); }

            // A genuine toggler has been seen in BOTH states; permanently-on/off elements never qualify.
            if (_everVisible.Contains(el) && _everHidden.Contains(el))
            {
                sawToggler = true;
                if (vis) togglerVisible = true;
                if (vis || !haveTogglerUi) { togglerUi = new MapUi(vis, sx, sy, zoom); haveTogglerUi = true; }
            }
        }
        if (!any) return default;

        if (sawToggler)
            return new MapUi(togglerVisible, togglerUi.ShiftX, togglerUi.ShiftY, togglerUi.Zoom);

        // No toggle observed yet this area: the open minimap lights up one element beyond the
        // always-on baseline, so >=2 visible ≈ open. Superseded as soon as a real toggle is seen.
        return new MapUi(visibleCount >= 2, anyUi.ShiftX, anyUi.ShiftY, anyUi.Zoom);
    }

    private void DiscoverMapElements(nint inGameState)
    {
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return;
        _mapQueue.Clear(); _mapQueue.Enqueue(uiRoot);
        _mapVisited.Clear();
        while (_mapQueue.Count > 0 && _mapVisited.Count < 30000)
        {
            var el = _mapQueue.Dequeue();
            if (el == 0 || !_mapVisited.Add(el)) continue;

            var first = Ptr(el + Poe2.UiElement.Children);
            if (first != 0 && _reader.TryReadStruct<nint>(el + Poe2.UiElement.Children + 8, out var lastC))
            {
                var n = ((long)lastC - (long)first) / 8;
                if (n is > 0 and <= 8192)
                    for (long k = 0; k < n; k++) _mapQueue.Enqueue(Ptr(first + (nint)(k * 8)));
            }

            if (_reader.TryReadBytes(el, _mapBody) < _mapBody.Length) continue;
            if (BitConverter.ToSingle(_mapBody, Poe2.MapUiElement.DefaultShift) != 0f) continue;
            if (BitConverter.ToSingle(_mapBody, Poe2.MapUiElement.DefaultShift + 4) != -20f) continue;
            var zoom = BitConverter.ToSingle(_mapBody, Poe2.MapUiElement.Zoom);
            if (zoom is <= 0.05f or >= 8f) continue;
            _mapEls.Add(el);
        }
    }

    private bool TryReadMapElement(nint el, out bool visible, out float shiftX, out float shiftY, out float zoom)
    {
        visible = false; shiftX = shiftY = zoom = 0;
        if (_reader.TryReadBytes(el, _mapBody) < _mapBody.Length) return false;
        // DefaultShift.y guard (must be -20) — same gate as before, now from the buffer
        if (BitConverter.ToSingle(_mapBody, Poe2.MapUiElement.DefaultShift + 4) != -20f) return false;
        shiftX = BitConverter.ToSingle(_mapBody, Poe2.MapUiElement.Shift);
        shiftY = BitConverter.ToSingle(_mapBody, Poe2.MapUiElement.Shift + 4);
        zoom   = BitConverter.ToSingle(_mapBody, Poe2.MapUiElement.Zoom);
        var flags = BitConverter.ToUInt32(_mapBody, Poe2.UiElement.Flags);
        visible = (flags & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;   // mirror IsVisible's exact bit test
        return true;
    }

    /// <summary>Element's own visibility bit (0x0B of Flags). Note: full visibility is hierarchical.</summary>
    public bool IsVisible(nint element)
    {
        if (!_reader.TryReadStruct<uint>(element + Poe2.UiElement.Flags, out var flags)) return false;
        return (flags & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;
    }

    /// <summary>v0.32 Panorama: locate the CharacterPanel UiElement, if open.
    /// Returns 0 if not found or not visible. Uses idx hint first, then shape fallback.</summary>
    public nint TryFindCharacterPanel()
    {
        if (!TryResolve(out var igs, out _, out _)) return 0;
        var uiRoot = Ptr(igs + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return 0;
        return TryFindPanelByShape(uiRoot, PanelKind.Character);
    }

    /// <summary>v0.32 Panorama: locate the InventoryPanel UiElement, if open.
    /// Returns 0 if not found or not visible.</summary>
    public nint TryFindInventoryPanel()
    {
        if (!TryResolve(out var igs, out _, out _)) return 0;
        var uiRoot = Ptr(igs + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return 0;
        return TryFindPanelByShape(uiRoot, PanelKind.Inventory);
    }

    /// <summary>v0.32 Panorama: locate the StashPanel UiElement, if open. Returns 0 if not
    /// found or not visible. Note: opening a stash also opens InventoryPanel — both can be
    /// visible simultaneously.</summary>
    public nint TryFindStashPanel()
    {
        if (!TryResolve(out var igs, out _, out _)) return 0;
        var uiRoot = Ptr(igs + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return 0;
        return TryFindPanelByShape(uiRoot, PanelKind.Stash);
    }

    internal enum PanelKind { Character, Inventory, Stash }

    private nint TryFindPanelByShape(nint uiRoot, PanelKind kind)
    {
        if (!ChildSpan(uiRoot, out var first, out var n)) return 0;

        // Try idx hint first (fast path).
        int hint = kind switch
        {
            PanelKind.Character => Poe2.Panels.CharacterPanel_IdxHint,
            PanelKind.Inventory => Poe2.Panels.InventoryPanel_IdxHint,
            PanelKind.Stash     => Poe2.Panels.StashPanel_IdxHint,
            _ => -1
        };
        if (hint >= 0 && hint < n)
        {
            var candidate = Ptr(first + (nint)(hint * 8));
            if (candidate != 0 && MatchesPanelShape(candidate, kind)) return candidate;
        }

        // Fallback: scan all UiRoot children.
        for (long i = 0; i < n; i++)
        {
            if (i == hint) continue; // already tried
            var child = Ptr(first + (nint)(i * 8));
            if (child == 0) continue;
            if (MatchesPanelShape(child, kind)) return child;
        }
        return 0;
    }

    private bool MatchesPanelShape(nint el, PanelKind kind)
    {
        const uint visBit = 1u << Poe2.UiElement.FlagVisibleBit;
        if (!_reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags)) return false;
        if ((flags & visBit) == 0) return false;

        if (!_reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos, out var x)) return false;
        if (!_reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos + 4, out var y)) return false;
        if (!_reader.TryReadStruct<float>(el + Poe2.UiElement.SizeW, out var w)) return false;
        if (!_reader.TryReadStruct<float>(el + Poe2.UiElement.SizeH, out var h)) return false;

        // Check if it has stash bottom bar for stash panel detection
        bool hasStashBottomBar = HasStashBottomBar(el);

        return MatchesPanelShape(kind, flags, x, y, w, h, hasStashBottomBar);
    }

    internal static bool MatchesPanelShape(PanelKind kind, uint flags, float x, float y, float w, float h, bool hasStashBottomBar)
    {
        const uint visBit = 1u << Poe2.UiElement.FlagVisibleBit;
        if ((flags & visBit) == 0) return false;

        // All three panels share the 986x1600 rect. Reject anything else immediately.
        if (System.Math.Abs(w - Poe2.Panels.PanelWidthUnscaled)  > 4f) return false;
        if (System.Math.Abs(h - Poe2.Panels.PanelHeightUnscaled) > 4f) return false;

        return kind switch
        {
            PanelKind.Inventory => x > 100f && System.Math.Abs(y) < 4f, // right-anchored
            PanelKind.Character => System.Math.Abs(x) < 4f && System.Math.Abs(y) < 4f
                                   && !hasStashBottomBar,           // left-anchored, no stash bar
            PanelKind.Stash     => System.Math.Abs(x) < 4f && System.Math.Abs(y) < 4f
                                   && hasStashBottomBar,            // left-anchored, has stash bar
            _ => false
        };
    }

    private bool HasStashBottomBar(nint panel)
    {
        if (!ChildSpan(panel, out var first, out var n)) return false;
        var children = new List<(uint flags, float ry, float rw)>();
        for (long i = 0; i < n && i < 60; i++)
        {
            var child = Ptr(first + (nint)(i * 8));
            if (child == 0) continue;
            if (!_reader.TryReadStruct<uint>(child + Poe2.UiElement.Flags, out var f)) continue;
            if (!_reader.TryReadStruct<float>(child + Poe2.UiElement.RelativePos + 4, out var cy)) continue;
            if (!_reader.TryReadStruct<float>(child + Poe2.UiElement.SizeW,        out var cw)) continue;
            children.Add((f, cy / Poe2.Panels.PanelHeightUnscaled, cw / Poe2.Panels.PanelWidthUnscaled));
        }
        return HasStashBottomBar(children);
    }

    internal static bool HasStashBottomBar(IReadOnlyList<(uint flags, float ry, float rw)> children)
    {
        const uint visBit = 1u << Poe2.UiElement.FlagVisibleBit;
        foreach (var (f, ry, rw) in children)
        {
            if ((f & visBit) == 0) continue;
            if (ry >= Poe2.Panels.StashBottomBarRyMin && ry <= Poe2.Panels.StashBottomBarRyMax
                && rw >= Poe2.Panels.StashBottomBarRwMin && rw <= Poe2.Panels.StashBottomBarRwMax)
                return true;
        }
        return false;
    }

    // ── UiElement screen geometry (shared with Poe2Runeforge) ──────────

    /// <summary>Screen-space rect (pixels) of ANY UiElement, via the upstream reference UiElementBase math:
    /// parent-chain unscaled position × resolution scale. Same geometry as <see cref="Poe2Runeforge"/>
    /// (sans its scroll viewport). <paramref name="winW"/>/<paramref name="winH"/> are the current game
    /// window size. Returns false on a read failure, a degenerate (≤1 px) rect, or when the element's own
    /// visibility bit is clear (so a render-thread caller can read live tag rects and a stale/closed tag
    /// just drops out). Touches no per-entity cache → safe to call from a separate reader stack per frame.</summary>
    public bool TryUiElementRect(nint el, float winW, float winH, out float x, out float y, out float w, out float h,
        string? requireFirstLine = null)
    {
        x = y = w = h = 0f;
        if (el == 0) return false;
        if (!_reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags)) return false;
        if ((flags & (1u << Poe2.UiElement.FlagVisibleBit)) == 0) return false;   // not (locally) visible
        // Stale-address guard: a loot-tag element captured at world rate can be freed/recycled before this
        // frame. Require the element to STILL show the matched first-line text; a recycled element holding
        // unrelated UI fails this and drops out (no garbage rect → no jitter). Caller passes the matched text.
        if (requireFirstLine is { Length: > 0 })
        {
            var t = ReadStdWString(el + Poe2.UiElement.Text);
            var nl = t.IndexOf('\n');
            if (!string.Equals((nl >= 0 ? t[..nl] : t).Trim(), requireFirstLine, StringComparison.Ordinal)) return false;
        }
        if (!_reader.TryReadStruct<byte>(el + Poe2.UiElement.ScaleIndex, out var idx)) return false;
        _reader.TryReadStruct<float>(el + Poe2.UiElement.LocalScaleMul, out var mul);
        // Size as ONE atomic 8-byte read (W,H contiguous at 0x288/0x28C) — never split into two reads, or a
        // mid-update read tears W from one frame and H from another.
        _reader.TryReadStruct<System.Numerics.Vector2>(el + Poe2.UiElement.SizeW, out var sz);
        var (sw, sh) = UiScaleValue(idx, mul, winW, winH);
        if (sw <= 0f || sh <= 0f) return false;
        var (px, py) = UiUnscaledPos(el, 0, winW, winH);
        if (!float.IsFinite(px) || !float.IsFinite(py)) return false;
        x = px * sw; y = py * sh; w = sz.X * sw; h = sz.Y * sh;
        return w > 1f && h > 1f;
    }

    /// <summary>A UiElement's RelativePos (canvas/parent-relative position) as ONE atomic 8-byte read,
    /// VALIDATED. Used by the render thread to re-read atlas node positions per frame so the overlay tracks
    /// pan smoothly. Returns false — so the caller falls back to the last baked position — when the element
    /// is stale/freed/recycled (its Self pointer no longer matches: atlas nodes scrolled far off-screen can
    /// be virtualized, and reading the freed slot yields garbage that projected to streaking lines) or when
    /// the value is non-finite / implausibly large.</summary>
    public bool TryRelPos(nint el, out float x, out float y)
    {
        x = y = 0f;
        if (el == 0) return false;
        // Liveness guard: a real UiElement's Self (+0x08) points back at itself; a recycled/freed slot won't.
        if (!_reader.TryReadStruct<nint>(el + Poe2.UiElement.Self, out var self) || self != el) return false;
        if (!_reader.TryReadStruct<System.Numerics.Vector2>(el + Poe2.UiElement.RelativePos, out var v)) return false;
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || MathF.Abs(v.X) > 200000f || MathF.Abs(v.Y) > 200000f) return false;
        x = v.X; y = v.Y; return true;
    }

    /// <summary>v1 = winW/2560, v2 = winH/1600; ScaleIndex picks which axis scale(s) apply (1→(v1,v1),
    /// 2→(v2,v2), 3→(v1,v2), else uniform mul). Mirrors upstream reference ScaleValue / Poe2Runeforge.</summary>
    private static (float w, float h) UiScaleValue(byte idx, float mul, float winW, float winH)
    {
        if (mul == 0f) mul = 1f;
        var v1 = winW / (float)Poe2.UiElement.BaseResW;
        var v2 = winH / (float)Poe2.UiElement.BaseResH;
        float w = mul, h = mul;
        switch (idx)
        {
            case 1: w *= v1; h *= v1; break;
            case 2: w *= v2; h *= v2; break;
            case 3: w *= v1; h *= v2; break;
        }
        return (w, h);
    }

    /// <summary>Parent-chain accumulated UNSCALED position: relPos + parent position, plus this element's
    /// PositionModifier when its flag <c>0x0A</c> is set. SIMPLE add (no cross-scale rescale) — this is the
    /// form validated for loot tags by Research <c>--lootcursor</c>. (The runeforge panel's
    /// <see cref="Poe2Runeforge"/> keeps a rescale branch, but its rows share their parents' scale so it
    /// never fires; loot tags DO cross scale indices, where the rescale mis-positioned them.)</summary>
    private (float x, float y) UiUnscaledPos(nint el, int depth, float winW, float winH)
    {
        // RelativePos as ONE atomic 8-byte read (X,Y contiguous). Splitting it into two float reads tears
        // X from one frame and Y from the next while the game is repositioning a world-anchored loot label
        // as the player moves — which is exactly what made the chips jitter in motion. Same idiom as the
        // HP-bar Vector3 world read.
        _reader.TryReadStruct<System.Numerics.Vector2>(el + Poe2.UiElement.RelativePos, out var rel);
        var parent = Ptr(el + Poe2.UiElement.Parent);
        if (parent == 0 || depth >= 64) return (rel.X, rel.Y);

        var (ppx, ppy) = UiUnscaledPos(parent, depth + 1, winW, winH);

        if (_reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags)
            && (flags & (1u << Poe2.UiElement.FlagModifyPosBit)) != 0)
        {
            _reader.TryReadStruct<System.Numerics.Vector2>(el + Poe2.UiElement.PositionModifier, out var mod);
            ppx += mod.X; ppy += mod.Y;
        }
        return (ppx + rel.X, ppy + rel.Y);
    }

    /// <summary>One offered reward in the post-ritual TRIBUTE SHOP: the reward item's identity (for pricing —
    /// uniques key off <see cref="Art"/>, everything else off the base-type <see cref="Name"/>) and its tile's
    /// SCREEN rect (already scaled). Value lookup + drawing happen overlay-side.</summary>
    public readonly record struct RitualReward(Rarity Rarity, string? Art, string? Name, bool Identified, float X, float Y, float W, float H);

    /// <summary>Read the post-ritual tribute shop's offered rewards (full item entities) so the overlay can
    /// price each. The reward tiles are item-slot UiElements carrying their item Entity at
    /// <see cref="Poe2Offsets.Ritual.TileSlotItem"/> (+0x4F8); ALL are present with no hover. Returns empty
    /// when the shop is closed — gated on a shop-signature text element, so it's cheap when not shopping.
    /// World thread (resolves item components via the per-area cache). See the ritual-rewards RE notes.</summary>
    public List<RitualReward> ReadRitualRewards(nint inGameState, float winW, float winH)
    {
        var result = new List<RitualReward>();
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return result;

        // 1) Confirm the shop is open + find a signature text element. BFS the VISIBLE tree (invisible
        //    subtrees pruned → cheap); the signature only renders while the tribute shop is up.
        //    BFS shared with PROBE-CORE via WalkUiTree — same visit set, same order.
        nint sigEl = 0;
        foreach (var el in WalkUiTree(uiRoot))
        {
            if (sigEl != 0) continue;                            // preserve original: enumerate full tree
            var t = ReadStdWString(el + Poe2.UiElement.Text);
            if (t.Length >= 6 && (t.Contains("Rituals Remaining", StringComparison.OrdinalIgnoreCase)
                                  || t.Contains("tribute to the king", StringComparison.OrdinalIgnoreCase)))
                sigEl = el;
        }
        if (sigEl == 0) return result;   // shop closed

        // 2) Walk UP from the signature element; at each ancestor look among its DIRECT children for the
        //    reward grid (a container of item-slot tiles). Grid + signature share the shop-window ancestor;
        //    the flask bar — the only other multi-slot item grid — is NOT under that window, so it's excluded.
        var cur = sigEl;
        nint grid = 0;
        for (var up = 0; up < 8 && grid == 0; up++)
        {
            grid = FindRewardGrid(cur);
            var parent = Ptr(cur + Poe2.UiElement.Parent);
            if (parent == 0) break;
            cur = parent;
        }
        if (grid == 0 || !ChildSpan(grid, out var gf, out var gn)) return result;

        // 3) Read each tile's item entity (+0x4F8) → identity, and its live screen rect.
        for (long i = 0; i < gn; i++)
        {
            var tile = Ptr(gf + (nint)(i * 8));
            var item = TileItem(tile);
            if (item == 0) continue;
            var itemIdentity = ReadIdentityFromItem(item);
            var rarity = itemIdentity.rarity;
            var art = itemIdentity.art;
            var identified = itemIdentity.identified;
            var name = itemIdentity.name;
            if (!TryUiElementRect(tile, winW, winH, out var x, out var y, out var w, out var h)) continue;
            result.Add(new RitualReward(rarity, art, name, identified, x, y, w, h));
        }
        return result;
    }

    /// <summary>A reward-grid container among <paramref name="parent"/>'s DIRECT children: the child whose
    /// own children are mostly item-slot tiles (item entity at +0x4F8). Returns the best (≥2 tiles) or 0.</summary>
    private nint FindRewardGrid(nint parent)
    {
        if (!ChildSpan(parent, out var first, out var n)) return 0;
        nint best = 0; var bestItems = 0;
        for (long i = 0; i < n; i++)
        {
            var c = Ptr(first + (nint)(i * 8));
            if (!ChildSpan(c, out var cf, out var cn) || cn is < 1 or > 16) continue;
            var items = 0;
            for (long k = 0; k < cn; k++) if (TileItem(Ptr(cf + (nint)(k * 8))) != 0) items++;
            if (items >= 2 && items > bestItems && items * 2 >= cn) { best = c; bestItems = items; }
        }
        return best;
    }

    /// <summary>The item Entity held by an item-slot tile (+0x4F8), or 0 when empty / not an item element
    /// (validated by requiring a RenderItem component).</summary>
    private nint TileItem(nint tile)
    {
        if (tile == 0) return 0;
        var item = Ptr(tile + Poe2.Ritual.TileSlotItem);
        return item != 0 && ResolveComponent(item, "RenderItem") != 0 ? item : 0;
    }

    private bool ChildSpan(nint el, out nint first, out long n)
    {
        first = Ptr(el + Poe2.UiElement.Children); n = 0;
        if (first == 0) return false;
        if (!_reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last)) return false;
        n = ((long)last - (long)first) / 8;
        return n is > 0 and <= 4000;
    }

    /// <summary>BFS the in-game UI tree (from <see cref="Poe2Offsets.InGameState.UiRoot"/>) for VISIBLE,
    /// text-bearing elements and return each one's address + the FIRST LINE of its text. Invisible subtrees
    /// are PRUNED (the game won't render their children either), so the walk stays cheap — typically a few
    /// hundred visible nodes rather than the whole tree. The caller matches each first line to a priced item
    /// by NAME — a loot tag's text IS the item name, so no item-entity link is needed — and reads the live
    /// rect via <see cref="TryUiElementRect"/>. Empty when not in game. Bounded by <paramref name="maxNodes"/>.
    /// Allocates a fresh list/queue/set per call → meant to run THROTTLED on the world thread, not per frame.</summary>
    public List<(nint El, string Text)> ScanLootLabels(nint inGameState, int maxNodes = 20000)
    {
        var result = new List<(nint, string)>();
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return result;
        const uint visBit = 1u << Poe2.UiElement.FlagVisibleBit;

        // E6: reuse _lootQueue/_lootVisited to avoid per-call Queue/HashSet allocation.
        _lootQueue.Clear(); _lootQueue.Enqueue(uiRoot); _lootVisited.Clear();
        while (_lootQueue.Count > 0 && _lootVisited.Count < maxNodes)
        {
            var el = _lootQueue.Dequeue();
            if (el == 0 || !_lootVisited.Add(el)) continue;
            var visible = _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags) && (flags & visBit) != 0;
            if (!visible && el != uiRoot) continue;   // prune the invisible subtree (root always descended)

            var first = Ptr(el + Poe2.UiElement.Children);
            if (first != 0 && _reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last))
            {
                var n = ((long)last - (long)first) / 8;
                if (n is > 0 and <= 8192)
                    for (long k = 0; k < n; k++) _lootQueue.Enqueue(Ptr(first + (nint)(k * 8)));
            }

            var text = ReadStdWString(el + Poe2.UiElement.Text);
            if (text.Length < 2) continue;
            var nl = text.IndexOf('\n');
            var firstLine = (nl >= 0 ? text[..nl] : text).Trim();
            if (firstLine.Length >= 2) result.Add((el, firstLine));
        }
        return result;
    }

    // ── internals ───────────────────────────────────────────────────────────

    /// <summary>RENDER-RATE live read of one already-known monster's world position + HP, reusing the
    /// component addresses cached by the last <see cref="Entities"/> walk (no component re-resolve, no map
    /// re-enumeration). This is what lets HP bars track a moving monster smoothly at the full frame rate
    /// while the expensive entity enumeration stays at world rate. Two tiny reads (12-byte position, 8-byte
    /// vital). Returns false if the entity isn't in the current area's cache or the position read fails.</summary>
    public bool TryLiveBar(nint entity, out Vector3 world, out int hpCur, out int hpMax)
    {
        world = default; hpCur = 0; hpMax = 0;
        if (!_renderAddr.TryGetValue(entity, out var render) || render == 0) return false;
        if (!_reader.TryReadStruct<Vector3>(render + Poe2.Render.CurrentWorldPosition, out world)) return false;
        if (_lifeAddr.TryGetValue(entity, out var life) && life != 0
            && _reader.TryReadStruct<VitalStruct>(life + _healthOff, out var v)) { hpCur = v.Current; hpMax = v.Max; }
        return true;
    }

    /// <summary>The Render + Life component addresses cached for <paramref name="entity"/> by the most
    /// recent <see cref="Entities"/> walk (0 when not resolved). Lets the world thread CAPTURE these into
    /// an HP-bar spec so the RENDER thread can read the bar's live pos/HP via <see cref="TryLiveBarAt"/>
    /// on its OWN reader stack — no shared per-entity cache between threads. Returns false if no Render.</summary>
    public bool TryBarComponents(nint entity, out nint render, out nint life)
    {
        render = _renderAddr.GetValueOrDefault(entity);
        life = _lifeAddr.GetValueOrDefault(entity);
        return render != 0;
    }

    /// <summary>RENDER-RATE bar read from EXPLICIT component addresses (captured off the world thread's
    /// <see cref="Entities"/> walk via <see cref="TryBarComponents"/>), using THIS instance's reader +
    /// resolved Health offset. Touches no per-entity cache, so a render-thread <see cref="Poe2Live"/> can
    /// drive HP bars without sharing state with the world-thread instance. <paramref name="render"/> must
    /// be non-zero. Returns false on a failed position read.</summary>
    public bool TryLiveBarAt(nint render, nint life, out Vector3 world, out int hpCur, out int hpMax)
    {
        world = default; hpCur = 0; hpMax = 0;
        if (render == 0 || !_reader.TryReadStruct<Vector3>(render + Poe2.Render.CurrentWorldPosition, out world)) return false;
        if (life != 0 && _reader.TryReadStruct<VitalStruct>(life + _healthOff, out var v)) { hpCur = v.Current; hpMax = v.Max; }
        return true;
    }

    private Vector3? EntityWorld(nint entity)
    {
        if (!_renderAddr.TryGetValue(entity, out var render))
        {
            render = ResolveComponent(entity, "Render");
            _renderAddr[entity] = render; // cache even if 0, to avoid re-walking
        }
        if (render == 0) return null;
        if (!_reader.TryReadStruct<Vector3>(render + Poe2.Render.CurrentWorldPosition, out var w)) return null;
        return w;
    }

    private System.Numerics.Vector2? EntityGrid(nint entity)
        => EntityWorld(entity) is { } w ? new System.Numerics.Vector2(w.X / Poe2.WorldToGridRatio, w.Y / Poe2.WorldToGridRatio) : null;

    /// <summary>Chest opened state. The 2026-06-06 patch INVERTED this flag: Chest +0x168 is now 0
    /// while closed/openable and non-zero once opened/used (was the reverse). Validated live by diffing
    /// one rare chest closed-vs-opened — only +0x168 flipped (0→1; loot/interaction pointers nulled).
    /// A read failure returns not-opened (i.e. shows the chest): for chests, over-showing is far safer
    /// than silently hiding a real one — which is exactly the bug this flip caused.</summary>
    private bool ReadChestOpened(nint entity)
    {
        if (_openedChests.Contains(entity)) return true;
        if (!_chestAddr.TryGetValue(entity, out var c)) { c = ResolveComponent(entity, "Chest"); _chestAddr[entity] = c; }
        if (c == 0) return false;
        if (!_reader.TryReadStruct<byte>(c + Poe2.ChestComponent.OpenState, out var b) || b == 0) return false;
        _openedChests.Add(entity);
        return true;
    }

    /// <summary>WorldToScreen matrix (16 floats, row-major) from Camera@InGameState+0x368. Null if unavailable.</summary>
    public float[]? CameraMatrix(nint inGameState)
    {
        var cam = Ptr(inGameState + Poe2.InGameState.Camera);
        if (cam == 0) return null;
        // Reuse the buffers — this runs every render frame; the result is consumed synchronously.
        if (_reader.TryReadBytes(cam + Poe2.Camera.WorldToScreenMatrix, _camBytes) != 64) return null;
        System.Buffer.BlockCopy(_camBytes, 0, _camMatrix, 0, 64);
        return _camMatrix;
    }

    private EntityCategory Categorize(nint entity)
    {
        if (_category.TryGetValue(entity, out var c)) return c;
        var meta = ReadMetadata(entity);
        _meta[entity] = meta;
        c = meta switch
        {
            // NPCs FIRST: friendly NPCs (Alva, vendors…) live under "Metadata/Monsters/NPC/…", so the
            // "/NPC/" check must precede "/Monsters/" or they'd be miscategorized as combat monsters
            // (and a Unique-rarity NPC would draw the enemy unique star). "/NPC/" is the NPC marker.
            _ when meta.Contains("/NPC/", StringComparison.Ordinal)         => EntityCategory.Npc,
            // Real combat monsters only — exclude on-death/aura effect carriers (MonsterMods),
            // player/ally summons, and invisible effect daemons. Those clutter the map and aren't
            // fight targets. (Friendly/hostile is applied at draw time via Positioned.Reaction.)
            _ when meta.Contains("/Monsters/", StringComparison.Ordinal) && IsNonCombat(meta) => EntityCategory.Other,
            _ when meta.Contains("/Monsters/", StringComparison.Ordinal)   => EntityCategory.Monster,
            _ when meta.Contains("/Characters/", StringComparison.Ordinal)  => EntityCategory.Player,
            // Real chests only — exclude breakable props (urns/vases/pots/etc.) under /Chests/.
            _ when meta.Contains("/Chests", StringComparison.Ordinal) && IsBreakableProp(meta) => EntityCategory.Other,
            _ when meta.Contains("/Chests", StringComparison.Ordinal)       => EntityCategory.Chest,
            _ when meta.Contains("Transition", StringComparison.Ordinal)    => EntityCategory.Transition,
            _ when meta.Contains("/Terrain/", StringComparison.Ordinal)     => EntityCategory.Object,
            _                                                              => EntityCategory.Other,
        };
        _category[entity] = c;
        return c;
    }

    /// <summary>True for "/Chests/" entities that are destructible scenery (urns, vases, pots…) not loot chests.</summary>
    private static bool IsBreakableProp(string meta) =>
        meta.Contains("Urn", StringComparison.Ordinal) ||
        meta.Contains("Vase", StringComparison.Ordinal) ||
        meta.Contains("Pot", StringComparison.Ordinal) ||
        meta.Contains("Jar", StringComparison.Ordinal) ||
        meta.Contains("Sack", StringComparison.Ordinal) ||
        meta.Contains("Barrel", StringComparison.Ordinal) ||
        meta.Contains("Crate", StringComparison.Ordinal) ||
        meta.Contains("Debris", StringComparison.Ordinal) ||
        meta.Contains("Rubble", StringComparison.Ordinal) ||
        meta.Contains("Basket", StringComparison.Ordinal) ||
        meta.Contains("Coffin", StringComparison.Ordinal);

    /// <summary>True for "/Monsters/" entities that aren't real fight targets (effects / summons).</summary>
    private static bool IsNonCombat(string meta) =>
        meta.Contains("MonsterMods", StringComparison.Ordinal) ||
        meta.Contains("Summoned", StringComparison.Ordinal) ||
        meta.Contains("/Daemon/", StringComparison.Ordinal) ||
        meta.Contains("Invisible", StringComparison.Ordinal);

    /// <summary>Resolve several component addresses for one entity in a single bucket read.
    /// results[i] is the component address for names[i], or 0 if absent. names.Length == results.Length.</summary>
    private void ResolveComponents(nint entity, ReadOnlySpan<string> names, Span<nint> results)
    {
        for (var i = 0; i < results.Length; i++) results[i] = 0;

        var details = Ptr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return;
        var lookup = Ptr(details + Poe2.EntityDetails.ComponentLookUpPtr);
        if (lookup == 0) return;
        if (!_reader.TryReadStruct<StdVector>(entity + Poe2.Entity.ComponentList, out var compList)) return;
        var compCount = ((long)compList.Last - (long)compList.First) / 8;
        if (compCount is <= 0 or > 256) return;

        var bFirst = Ptr(lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
        if (!_reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return;
        var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
        if (bFirst == 0 || entries is <= 0 or > 256) return;

        var span = (int)(entries * Poe2.ComponentLookUp.EntryStride);
        if (_reader.TryReadBytes(bFirst, _compBucketBuf.AsSpan(0, span)) < span) return;   // ONE bulk read of the whole bucket

        var remaining = names.Length;
        for (long i = 0; i < entries && remaining > 0; i++)
        {
            var off = (int)(i * Poe2.ComponentLookUp.EntryStride);
            var namePtr = (nint)BitConverter.ToInt64(_compBucketBuf, off);
            var index = BitConverter.ToInt32(_compBucketBuf, off + 8);
            if (index < 0 || index >= compCount) continue;
            var name = _reader.ReadStringUtf8(namePtr, 32);   // one read per entry, shared across all targets
            for (var t = 0; t < names.Length; t++)
            {
                if (results[t] != 0 || names[t] != name) continue;
                results[t] = Ptr(compList.First + (nint)(index * 8));
                remaining--;
            }
        }
    }

    /// <summary>Resolve a component address by name via EntityDetails → ComponentLookUp (StdBucket) → ComponentList.
    /// Thin wrapper over <see cref="ResolveComponents"/> — all ~14 existing single-name call sites unchanged.</summary>
    private nint ResolveComponent(nint entity, string name)
    {
        Span<nint> r = stackalloc nint[1];
        ResolveComponents(entity, new[] { name }, r);
        return r[0];
    }

    /// <summary>Pure: given decoded (name,index) bucket entries and target names, return each target's
    /// component index (or -1 if absent), preserving target order. No memory access — unit-tested.</summary>
    public static int[] MatchComponentIndices(ReadOnlySpan<(string Key, int Index)> entries, ReadOnlySpan<string> targets)
    {
        var results = new int[targets.Length];
        for (var t = 0; t < targets.Length; t++)
        {
            results[t] = -1;
            for (var i = 0; i < entries.Length; i++)
                if (entries[i].Key == targets[t]) { results[t] = entries[i].Index; break; }
        }
        return results;
    }

    /// <summary>Read an entity's metadata path: EntityDetails(+0x08) → name StdWString(+0x08).</summary>
    private string ReadMetadata(nint entity)
    {
        var details = Ptr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return string.Empty;
        return ReadStdWString(details + Poe2.EntityDetails.Name);
    }

    private string ReadStdWString(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return string.Empty;
        if (len < 8) return _reader.ReadStringUtf16(addr, len);
        var ptr = Ptr(addr);
        return ptr == 0 ? string.Empty : _reader.ReadStringUtf16(ptr, len);
    }

    /// <summary>Safe pointer read: 0 on failure or implausible (non-user-mode) value.</summary>
    private nint Ptr(nint addr)
    {
        if (!_reader.TryReadStruct<nint>(addr, out var p)) return 0;
        var u = (ulong)p;
        return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? 0 : p;
    }

    // ── Inventory read (experimental; default-OFF) ─────────────────────────────────────────────────
    // Ported faithfully from Research.RunInventory / DumpInventoryItems / ReadItemModIds /
    // ReadModValueArray / ReadModName (Research/Program.cs ~line 527-771).
    // Same chain: AreaInstance+0x598 → ServerData → +0x48 StdVector [0] → ServerDataStructure
    //             → +0x320 StdVector<InventoryArrayStruct> (stride 0x18).
    // Self-validates the two drift-prone hops (PlayerServerData vec, PlayerInventories vec) with
    // brute-scan fallbacks, exactly as the Research probe does.

    /// <summary>
    /// Read the player's inventory items and their raw rolled affixes (experimental; default-OFF).
    /// Ported from the validated Research <c>RunInventory</c> + <c>--itemmods</c> walk.
    /// Runs on the world thread; any failed hop returns an empty list — never throws.
    /// </summary>
    public IReadOnlyList<InventoryItem> ReadInventory(nint areaInstance)
    {
        var result = new List<InventoryItem>();
        try
        {
            // Step 1 — ServerData @ AreaInstance+0x598.
            var serverData = Ptr(areaInstance + Poe2.AreaInstance.ServerDataPtr);
            if (serverData == 0) return result;

            // Step 2 — PlayerServerData StdVector @ ServerData+0x48; [0] = ServerDataStructure.
            var sdStruct = ResolveServerDataStructForInventory(serverData);
            if (sdStruct == 0) return result;

            // Step 3 — PlayerInventories StdVector @ ServerDataStructure+0x320 (stride 0x18).
            var (invVecOff, invVec, invCount) = FindPlayerInventoriesVecForInventory(sdStruct, Poe2.ServerData.PlayerInventoriesVec);
            if (invVecOff < 0 || invCount <= 0) return result;

            var seen = new HashSet<nint>();

            for (long i = 0; i < invCount; i++)
            {
                var rec = invVec.First + (nint)(i * Poe2.ServerData.InvArrayStride);
                if (!_reader.TryReadStruct<int>(rec + Poe2.ServerData.InvArrayId, out var invId)) continue;
                var invPtr = Ptr(rec + Poe2.ServerData.InvArrayPtr);
                if (invPtr == 0) continue;

                // Step 4 — ItemList StdVector @ InventoryStruct+0x170 (with brute fallback).
                var (itemVec, itemCount) = ProbeItemListVecForInventory(invPtr);
                if (itemCount <= 0) continue;

                // Step 5 — walk each InventoryItemStruct slot (de-dup multi-cell items by address).
                for (var j = 0; j < itemCount; j++)
                {
                    var iiPtr = Ptr(itemVec.First + (nint)(j * 8));
                    if (iiPtr == 0) continue;
                    var item = Ptr(iiPtr + Poe2.InventoryItem.Item);
                    if (item == 0 || !seen.Add(item)) continue; // de-dup multi-slot items

                    // Validate: must be a real item entity.
                    if (!ReadMetadata(item).StartsWith("Metadata/Items", StringComparison.Ordinal)) continue;

                    // Identity: name, rarity, identified — reuse ReadIdentityFromItem.
                    var itemIdentity = ReadIdentityFromItem(item);
                    var rarityEnum = itemIdentity.rarity;
                    var identified = itemIdentity.identified;
                    var name = itemIdentity.name;
                    var rarStr = rarityEnum switch
                    {
                        Rarity.Normal => "Normal",
                        Rarity.Magic  => "Magic",
                        Rarity.Rare   => "Rare",
                        Rarity.Unique => "Unique",
                        _             => "Normal",
                    };

                    // Affixes: implicit + explicit mods from Mods component.
                    var modsComp = ResolveComponent(item, "Mods");
                    var affixes = new List<RawAffix>();
                    if (modsComp != 0)
                    {
                        ReadRawAffixesInto(modsComp + Poe2.ModsComponent.ImplicitMods, affixes);
                        ReadRawAffixesInto(modsComp + Poe2.ModsComponent.ExplicitMods, affixes);
                    }

                    result.Add(new InventoryItem(name ?? "", rarStr, identified, invId, affixes));
                }
            }
        }
        catch
        {
            // Never propagate — live memory reads can access freed/invalid memory during zone transitions.
        }
        return result;
    }

    // Ported from Research.ResolveServerDataStruct: try ServerData+0x48; brute-scan +0x10..+0x200 on miss.
    private nint ResolveServerDataStructForInventory(nint serverData)
    {
        nint TryOff(int off)
        {
            if (!_reader.TryReadStruct<StdVector>(serverData + off, out var v)) return 0;
            var span = (long)v.Last - (long)v.First;
            if (v.First == 0 || span < 8 || span > 0x4000) return 0;
            var first = Ptr(v.First);
            if (first == 0) return 0;
            // Validate by checking that the candidate carries a plausible PlayerInventories vec.
            return FindPlayerInventoriesVecForInventory(first, Poe2.ServerData.PlayerInventoriesVec).off >= 0 ? first : 0;
        }

        var direct = TryOff(Poe2.ServerData.PlayerServerDataVec);
        if (direct != 0) return direct;
        for (var off = 0x10; off <= 0x200; off += 8)
        {
            var hit = TryOff(off);
            if (hit != 0) return hit;
        }
        return 0;
    }

    // Ported from Research.FindPlayerInventoriesVec: prefer preferred offset; brute-scan +0x100..+0x800
    // if it doesn't score. Fingerprint: stride-0x18 records, id 1..200, ptr1 == ptr0-0x10.
    private (int off, StdVector vec, int count) FindPlayerInventoriesVecForInventory(nint sdStruct, int preferred)
    {
        (int score, StdVector vec, int count) Score(int off)
        {
            if (!_reader.TryReadStruct<StdVector>(sdStruct + off, out var v))
                return (-1, default, 0);
            var span = (long)v.Last - (long)v.First;
            if (v.First == 0 || span <= 0 || span % Poe2.ServerData.InvArrayStride != 0) return (-1, default, 0);
            var count = (int)(span / Poe2.ServerData.InvArrayStride);
            if (count is <= 0 or > 400) return (-1, default, 0);
            var good = 0;
            var check = Math.Min(count, 32);
            for (var i = 0; i < check; i++)
            {
                var r = v.First + (nint)(i * Poe2.ServerData.InvArrayStride);
                if (!_reader.TryReadStruct<int>(r, out var id) || id < 1 || id > 200) continue;
                var p0 = Ptr(r + 0x08);
                var p1 = Ptr(r + 0x10);
                if (p0 != 0 && p1 == p0 - 0x10) good++;
            }
            return (good, v, count);
        }

        var (pg, pv, pc) = Score(preferred);
        if (pg >= 1) return (preferred, pv, pc);

        var bestOff = -1; var best = (-1, default(StdVector), 0);
        for (var off = 0x100; off <= 0x800; off += 8)
        {
            var (g, v, c) = Score(off);
            if (g > best.Item1) { best = (g, v, c); bestOff = off; }
        }
        return best.Item1 >= 2 ? (bestOff, best.Item2, best.Item3) : (-1, default, 0);
    }

    // Ported from Research.ProbeInventoryStruct: try ItemList @ +0x170; brute-scan +0x100..+0x300 on miss.
    // Returns (vec, count); count==0 means nothing usable found.
    private (StdVector vec, int count) ProbeItemListVecForInventory(nint inv)
    {
        _reader.TryReadStruct<int>(inv + Poe2.Inventory.TotalBoxesX, out var boxX);
        _reader.TryReadStruct<int>(inv + Poe2.Inventory.TotalBoxesY, out var boxY);
        if (boxX is < 0 or > 200) boxX = 0;
        if (boxY is < 0 or > 200) boxY = 0;

        int CountItemsLike(StdVector v)
        {
            var span = (long)v.Last - (long)v.First;
            if (v.First == 0 || span <= 0 || span % 8 != 0 || span > 0x8000) return -1;
            var n = (int)(span / 8);
            var hits = 0; var seen = 0;
            for (var i = 0; i < Math.Min(n, 64) && seen < 12; i++)
            {
                var ii = Ptr(v.First + (nint)(i * 8));
                if (ii == 0) continue;
                seen++;
                var item = Ptr(ii + Poe2.InventoryItem.Item);
                if (item != 0 && ReadMetadata(item).StartsWith("Metadata/Items", StringComparison.Ordinal)) hits++;
            }
            return hits;
        }

        // Direct path: +0x170.
        if (_reader.TryReadStruct<StdVector>(inv + Poe2.Inventory.ItemListVec, out var direct))
        {
            var span = (long)direct.Last - (long)direct.First;
            if (direct.First != 0 && span > 0 && span % 8 == 0 && span <= 0x8000)
            {
                var n = (int)(span / 8);
                if ((boxX > 0 && boxY > 0 && n == boxX * boxY) || CountItemsLike(direct) >= 1)
                    return (direct, n);
            }
        }

        // Brute fallback: scan +0x100..+0x300 for the best matching StdVector.
        StdVector bestVec = default; var bestHits = 0; var bestN = 0;
        for (var off = 0x100; off <= 0x300; off += 8)
        {
            if (!_reader.TryReadStruct<StdVector>(inv + off, out var v)) continue;
            var h = CountItemsLike(v);
            if (h > bestHits)
            {
                bestHits = h;
                bestVec = v;
                bestN = (int)(((long)v.Last - (long)v.First) / 8);
            }
        }
        return bestHits >= 1 ? (bestVec, bestN) : (default, 0);
    }

    // Ported from Research.ReadItemModIds: walk one AllModsType sub-vector (Implicit or Explicit).
    // ModArrayStruct stride 0x40: +0x00 Values StdVector<int>, +0x18 Value0 fallback int,
    // +0x28 ModsPtr → Mods.dat row, row's first qword → UTF-16 internal mod id.
    private void ReadRawAffixesInto(nint vecAddr, List<RawAffix> affixes)
    {
        if (!_reader.TryReadStruct<StdVector>(vecAddr, out var v)) return;
        var span = (long)v.Last - (long)v.First;
        if (v.First == 0 || span <= 0 || span % Poe2.ModsComponent.ModArrayStride != 0 || span > 0x800) return;
        var n = (int)(span / Poe2.ModsComponent.ModArrayStride);
        for (var i = 0; i < n; i++)
        {
            var rec = v.First + (nint)(i * Poe2.ModsComponent.ModArrayStride);

            // ModsPtr (+0x28) → Mods.dat row; row's first qword (+0x00) → UTF-16 internal mod id.
            var modsPtr = Ptr(rec + Poe2.ModsComponent.ModRecordPtr);
            if (modsPtr == 0) continue;
            var idStrPtr = Ptr(modsPtr + Poe2.ModsComponent.ModRecordIdPtr);
            if (idStrPtr == 0) continue;
            var modId = _reader.ReadStringUtf16(idStrPtr, 80);
            if (!LooksLikeModId(modId)) continue;

            // Rolled values: Values StdVector<int> at +0x00; fallback to Value0 at +0x18 when empty.
            var values = ReadModRolledValues(rec);
            affixes.Add(new RawAffix(modId, values));
        }
    }

    // Ported from Research.ReadModValueArray: Values StdVector<int> at rec+0x00 (up to 8 values);
    // falls back to the inline Value0 int at rec+0x18 when the vector is empty.
    private IReadOnlyList<int> ReadModRolledValues(nint modArray)
    {
        var outv = new List<int>(4);
        if (_reader.TryReadStruct<StdVector>(modArray + 0x00, out var vv))
        {
            var span = (long)vv.Last - (long)vv.First;
            var count = vv.First == 0 || span <= 0 ? 0L : span / 4;
            for (long k = 0; k < Math.Min(count, 8); k++)
                if (_reader.TryReadStruct<int>(vv.First + (nint)(k * 4), out var x)) outv.Add(x);
        }
        if (outv.Count == 0 && _reader.TryReadStruct<int>(modArray + 0x18, out var v0)) outv.Add(v0);
        return outv;
    }

    /// <summary>
    /// Filter slot/self-pointer pairs, keeping only slots whose Self field points back at
    /// themselves (drops recycled slots whose Self field is stale from the previous occupant).
    /// Extracted for testability — the production path applies the same check inline in the
    /// entity-scan loop.
    /// </summary>
    internal static List<(ulong slot, ulong selfPtr)> FilterLiveSlotsForTest(
        IEnumerable<(ulong slot, ulong selfPtr)> input)
    {
        var kept = new List<(ulong slot, ulong selfPtr)>();
        foreach (var (slot, selfPtr) in input)
        {
            if (slot == selfPtr) kept.Add((slot, selfPtr));
        }
        return kept;
    }

    // ── UI-tree BFS (extracted from ReadRitualRewards for PROBE-CORE) ──────────
    /// <summary>Breadth-first walk of the UI element tree rooted at <paramref name="uiRoot"/>,
    /// yielding each visited element address. Invisible subtrees are PRUNED
    /// (<see cref="Poe2.UiElement.FlagVisibleBit"/>) — the root is descended unconditionally so
    /// callers pass <c>*(InGameState + <see cref="Poe2.InGameState.UiRoot"/>)</c> without
    /// pre-checking its visible bit. Bounded by <paramref name="maxVisit"/> (default 20 000).
    /// Same visit set + order as the original <see cref="ReadRitualRewards"/> BFS — a single
    /// reusable primitive so PROBE-CORE's NpcDialog / QuestReward signature walks can share it.
    /// Allocates a fresh <see cref="Queue{T}"/> + <see cref="HashSet{T}"/> per call → callers on the
    /// world thread should treat this as a throttled operation, not per-frame.</summary>
    public IEnumerable<nint> WalkUiTree(nint uiRoot, uint maxVisit = 20000)
    {
        if (uiRoot == 0) yield break;
        const uint visBit = 1u << Poe2.UiElement.FlagVisibleBit;
        var queue = new Queue<nint>();
        queue.Enqueue(uiRoot);
        var visited = new HashSet<nint>();
        while (queue.Count > 0 && visited.Count < maxVisit)
        {
            var el = queue.Dequeue();
            if (el == 0 || !visited.Add(el)) continue;
            var visible = _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags) && (flags & visBit) != 0;
            if (!visible && el != uiRoot) continue;             // prune invisible subtree
            if (ChildSpan(el, out var f, out var nn))
                for (long k = 0; k < nn; k++) queue.Enqueue(Ptr(f + (nint)(k * 8)));
            yield return el;
        }
    }

    // ── Campaign-Probe accessors (Task PROBE-OFFSETS, spec §4) ─────────────────
    // All read-only over ServerData / component memory. No writes. No allocations
    // on the disabled path (PROBE-CORE is gated on _settings.EnableCampaignProbe
    // upstream; these methods are simply the read primitives it composes).

    /// <summary>Current character experience (uint32 @ Player+0x1D8). Widened to long for
    /// serialisation ergonomics; PoE2 caps XP at ~4.25B which fits in uint32. Also feeds the
    /// PMS-6 XP/hour Session HUD chip. Returns 0 when the Player component is unresolved.</summary>
    public long PlayerExperience(nint localPlayer)
    {
        var c = PlayerComp(localPlayer);
        return c != 0 && _reader.TryReadStruct<uint>(c + Poe2.PlayerComponent.CurrentExperience, out var xp) ? xp : 0L;
    }

    /// <summary>Walks the passive-tree hop chain from <paramref name="areaInstance"/> to
    /// ServerPlayerData, then reads the allocated-node <c>StdVector&lt;uint16&gt;</c> at
    /// <see cref="Poe2.PassiveTree.AllocVecBegin"/>..<see cref="Poe2.PassiveTree.AllocVecEnd"/>.
    /// Returns an empty list on any failed hop or an implausible entry count (&gt;<see cref="Poe2.PassiveTree.AllocMax"/>).
    /// Never throws.</summary>
    public IReadOnlyList<ushort> AllocatedPassiveNodeIds(nint areaInstance)
    {
        var empty = System.Array.Empty<ushort>();
        try
        {
            var node = areaInstance;
            foreach (var hop in Poe2.PassiveTree.HopChain)
            {
                node = Ptr(node + hop);
                if (node == 0) return empty;
            }
            if (!_reader.TryReadStruct<nint>(node + Poe2.PassiveTree.AllocVecBegin, out var begin)) return empty;
            if (!_reader.TryReadStruct<nint>(node + Poe2.PassiveTree.AllocVecEnd,   out var end))   return empty;
            if (begin == 0 || end == 0 || (long)end < (long)begin) return empty;

            var byteSpan = (long)end - (long)begin;
            if (byteSpan <= 0 || byteSpan % Poe2.PassiveTree.EntryStride != 0) return empty;

            var count = (int)(byteSpan / Poe2.PassiveTree.EntryStride);
            if (count > Poe2.PassiveTree.AllocMax) return empty;

            var result = new List<ushort>(count);
            for (var i = 0; i < count; i++)
            {
                if (!_reader.TryReadStruct<ushort>(begin + (nint)(i * Poe2.PassiveTree.EntryStride), out var id)) return result;
                result.Add(id);
            }
            return result;
        }
        catch
        {
            return empty;
        }
    }

    /// <summary>Read the four Targetable component bytes (IsTargetable/IsHighlight/IsTargeted/IsHidden).
    /// Caller passes the entity address; this resolves the "Targetable" component and reads the bytes.
    /// Returns false when the component is missing or any byte fails to read; out parameters are 0 on false.</summary>
    public bool TryReadTargetable(nint entity, out byte isTargetable, out byte isHighlight, out byte isTargeted, out byte isHidden)
    {
        isTargetable = isHighlight = isTargeted = isHidden = 0;
        var comp = ResolveComponent(entity, "Targetable");
        if (comp == 0) return false;
        if (!_reader.TryReadStruct<byte>(comp + Poe2.Targetable.IsTargetable, out isTargetable)) return false;
        if (!_reader.TryReadStruct<byte>(comp + Poe2.Targetable.IsHighlight,  out isHighlight))  return false;
        if (!_reader.TryReadStruct<byte>(comp + Poe2.Targetable.IsTargeted,   out isTargeted))   return false;
        if (!_reader.TryReadStruct<byte>(comp + Poe2.Targetable.IsHidden,     out isHidden))     return false;
        return true;
    }

    /// <summary>Read Chest.OpenState + Chest.LabelVisible in one hop. <paramref name="isOpened"/> follows
    /// the 2026-06-06 polarity flip (0 = closed, non-zero = opened).</summary>
    public bool TryReadChestState(nint entity, out bool isOpened, out bool labelVisible)
    {
        isOpened = labelVisible = false;
        var comp = ResolveComponent(entity, "Chest");
        if (comp == 0) return false;
        if (!_reader.TryReadStruct<byte>(comp + Poe2.ChestComponent.OpenState,    out var open))  return false;
        if (!_reader.TryReadStruct<byte>(comp + Poe2.ChestComponent.LabelVisible, out var label)) return false;
        isOpened = open != 0;
        labelVisible = label != 0;
        return true;
    }

    /// <summary>Read Shrine.IsUsed byte. Non-zero = used (already activated).</summary>
    public bool TryReadShrineUsed(nint entity, out bool isUsed)
    {
        isUsed = false;
        var comp = ResolveComponent(entity, "Shrine");
        if (comp == 0) return false;
        if (!_reader.TryReadStruct<byte>(comp + Poe2.Shrine.IsUsed, out var b)) return false;
        isUsed = b != 0;
        return true;
    }

    /// <summary>Read Transitionable.State (int16). Non-zero values encode traversal states.</summary>
    public bool TryReadTransitionableState(nint entity, out short state)
    {
        state = 0;
        var comp = ResolveComponent(entity, "Transitionable");
        if (comp == 0) return false;
        return _reader.TryReadStruct<short>(comp + Poe2.Transitionable.State, out state);
    }

    /// <summary>Read TriggerableBlockage.IsBlocked byte.</summary>
    public bool TryReadTriggerableBlockage(nint entity, out bool isBlocked)
    {
        isBlocked = false;
        var comp = ResolveComponent(entity, "TriggerableBlockage");
        if (comp == 0) return false;
        if (!_reader.TryReadStruct<byte>(comp + Poe2.TriggerableBlockage.IsBlocked, out var b)) return false;
        isBlocked = b != 0;
        return true;
    }

    /// <summary>Read one bool entry out of the per-player quest-flags dictionary
    /// (PlayerServerData+0x230). Walks: AreaInstance → ServerData (+0x598) → PlayerServerData
    /// StdVector (+0x48) [0] → PlayerServerData → QuestFlags dictionary → probe key.
    /// Dictionary internals are traversed via the shipped StdMap conventions
    /// (<see cref="Poe2.StdMapNode"/>). Returns false when any hop fails or the key is missing.</summary>
    public bool TryReadQuestFlag(nint areaInstance, uint questFlagKey, out bool value)
    {
        value = false;
        try
        {
            var serverData = Ptr(areaInstance + Poe2.AreaInstance.ServerDataPtr);
            if (serverData == 0) return false;
            if (!_reader.TryReadStruct<StdVector>(serverData + Poe2.ServerData.PlayerServerDataVec, out var v)) return false;
            if (v.First == 0) return false;
            var psd = Ptr(v.First);
            if (psd == 0) return false;

            // Dictionary<QuestFlag,bool> at psd + 0x230. Read the std::map root node ptr and BST-walk
            // by uint key; StdMapNode layout: {Left,Parent,Right, IsNil(byte @+0x19), Data{Key,Value}@+0x20}.
            if (!_reader.TryReadStruct<nint>(psd + Poe2.PlayerServerData.QuestFlags, out var rootHolder)) return false;
            if (rootHolder == 0) return false;

            // std::map layout: rootHolder is the header node; header.Parent is the true root.
            if (!_reader.TryReadStruct<nint>(rootHolder + Poe2.StdMapNode.Parent, out var cur)) return false;
            for (var guard = 0; guard < 4096 && cur != 0; guard++)
            {
                if (!_reader.TryReadStruct<byte>(cur + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) return false;
                if (!_reader.TryReadStruct<uint>(cur + Poe2.StdMapNode.KeyId, out var key)) return false;
                if (key == questFlagKey)
                {
                    if (!_reader.TryReadStruct<byte>(cur + Poe2.StdMapNode.ValueEntityPtr, out var b)) return false;
                    value = b != 0;
                    return true;
                }
                var branch = questFlagKey < key ? Poe2.StdMapNode.Left : Poe2.StdMapNode.Right;
                if (!_reader.TryReadStruct<nint>(cur + branch, out cur)) return false;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Resolve the current hovered entity via the UI-root hover-tracker chain
    /// (<c>*(UiRoot+0x7D8) → tracker → +0x18</c>). This is the anchor PROBE-CORE uses for
    /// <c>npc_dialogue_started</c> — when the NpcDialog panel is visible (UI-tree signature walk),
    /// the hovered entity at dialog-open time identifies the NPC. Returns 0 on any failed hop.</summary>
    public nint HoveredEntityViaTracker(nint inGameState)
    {
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return 0;
        var tracker = Ptr(uiRoot + Poe2.HoverTracker.FromUiRoot);
        if (tracker == 0) return 0;
        return Ptr(tracker + Poe2.HoverTracker.HoveredEntityDirect);
    }
}
