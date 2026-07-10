namespace POE2Radar.Core.Game;

/// <summary>
/// PoE2 memory offsets — the going-forward source of truth, sourced from the upstream reference
/// <c>GameOffsets/</c> dump and validated against the live client where marked ✓.
///
/// <para>This is separate from the legacy PoE1-shaped <see cref="KnownOffsets"/> (which the
/// overlay still references and which is being migrated). As each PoE2 structure is validated
/// here, the corresponding overlay reader is rechained to use it.</para>
///
/// Markers: ✓ = confirmed against live PoE2; (prior-art) = from upstream reference, not yet live-checked;
/// ✗ = transcribed from a third-party IDA dump (a private fork), NOT yet validated against our
/// live client and NOT yet wired into any read path. Validate via the Research probes before using
/// any ✗ offset — patch drift means these may be wrong for the current build.
/// </summary>
public static class Poe2
{
    /// <summary>Tile→world = 250, tile→grid = 23 ⇒ world/grid ratio ≈ 10.8696. ✓</summary>
    public const float WorldToGridRatio = 250f / 23f;

    /// <summary>Conservative network-bubble radius in grid units (prior-art uses 150). </summary>
    public const int NetworkBubbleGrid = 150;

    /// <summary>
    /// GameState root — found via the "Game States" AOB pattern (<see cref="AobPatterns"/>).
    /// Holds the array of game-state slots; one of them is InGameState.
    /// </summary>
    public static class GameState
    {
        public const int CurrentStatePtr = 0x08;  // (prior-art) StdVector — current state
        public const int States          = 0x48;  // (prior-art) inline array of 12 × StdTuple2D<IntPtr> (16 bytes each)
        public const int StateSlotStride = 0x10;   // each slot is StdTuple2D<IntPtr> (ptr + extra)
        public const int StateSlotCount  = 12;
    }

    /// <summary>
    /// InGameState. Resolve it from <c>GameState.CurrentStatePtr</c> (StdVector @ +0x08): the
    /// vector's first element is the active state pointer when in-game. ✓ (matches States[] slot).
    /// </summary>
    public static class InGameState
    {
        public const int AreaInstanceData = 0x290; // ✓ → AreaInstance (validated: target holds the local player)
        public const int UiRoot           = 0x2F0; // ✓ → root UiElement (self-ref; children are UI elements)
        public const int Camera           = 0x368; // ✓ → Camera object (Zoom @ +0x528 == 1.0 confirmed)
    }

    public static class UiRootStruct
    {
        public const int UiRootPtr = 0x5A8; // (prior-art)
        public const int GameUiPtr = 0xBF0; // (prior-art)
    }

    /// <summary>
    /// The big per-area container: area metadata, player, entity maps, terrain.
    /// <para>⚠ These internal offsets drift per patch. <b>PoE2 0.5.4 inserted +0x18 (24 bytes)</b>
    /// into this struct: every field at offset ≥ 0x580 shifted by +0x18 (ServerDataPtr 0x580→0x598,
    /// LocalPlayer 0x5A0→0x5B8, AwakeEntities 0x6C0→0x6D8, SleepingEntities 0x6D0→0x6E8,
    /// TerrainMetadata 0x8A0→0x8B8). The low fields (AreaInfo/Level/Hash) sit below the insertion and
    /// were unchanged. Re-validate per patch via the Research probes (<c>--chain</c>/<c>--info</c>/
    /// <c>--rarity</c>/<c>--find-terrain</c>).</para>
    /// </summary>
    public static class AreaInstance
    {
        public const int AreaInfoPtr      = 0x0A0;  // ✓ → AreaInfo; +0x00 → UTF-16 "Code\0Name\0" (Code validated 'G1_town'). Below the 0.5.4 insertion — unchanged.
        public const int LocalPlayer      = 0x5B8;  // ✓ → player Entity. Was 0x5A0; +0x18 for 0.5.4 (= ServerDataPtr+0x20).
        public const int ServerDataPtr    = 0x598;  // ✓ → ServerData (gateway to player inventories; +0x20 here = LocalPlayer @ 0x5B8). Was 0x580; +0x18 for 0.5.4. PlayerServerDataVec @ +0x48 unchanged.
        public const int AwakeEntities    = 0x6D8;  // ✓ StdMap of live entities (id→EntityPtr). Was 0x6C0; +0x18 for 0.5.4.
        public const int SleepingEntities = 0x6E8;  // ✓ StdMap. Was 0x6D0; +0x18 for 0.5.4 (inferred from the uniform AreaInstance shift; confirm via --rarity).
        public const int TerrainMetadata  = 0x8B8;  // ✓ TerrainStruct base. Was 0x8A0; +0x18 for 0.5.4 (prior-art's 0xD20 drifted).
        public const int CurrentAreaLevel = 0x0C4;  // ✓ int — per-area, validated 27/32 (prior-art's 0xBC drifted). Below the 0.5.4 insertion — unchanged.
        public const int CurrentAreaHash  = 0x11C;  // ✓ uint — per-area random hash (prior-art's 0xFC drifted; +0x120 paired seed). Below the 0.5.4 insertion — unchanged.
    }

    /// <summary>Entity StdMap conventions. Maps live at AreaInstance+0x6D8 (Awake) / +0x6E8 (Sleeping) on 0.5.4.</summary>
    public static class EntityList
    {
        public const int StdMapSize = 0x10; // each StdMap is {Head ptr, int Size, pad} = 16 bytes
        /// <summary>Entity ids below this are real entities; above are visuals/decorations (prior-art filter). ✓ confirmed live.</summary>
        public const uint VisualIdThreshold = 0x40000000;
    }

    /// <summary>std::map node: Left/Parent/Right ptrs, Color, IsNil byte, then Data{Key,Value} @ +0x20.</summary>
    public static class StdMapNode
    {
        public const int Left   = 0x00;
        public const int Parent = 0x08;
        public const int Right  = 0x10;
        public const int IsNil  = 0x19; // bool
        public const int Data   = 0x20; // Key (EntityNodeKey: uint id + pad = 8 bytes), then Value (IntPtr EntityPtr)
        public const int KeyId  = 0x20; // uint entity id
        public const int ValueEntityPtr = 0x28; // IntPtr
    }

    /// <summary>An Entity object.</summary>
    public static class Entity
    {
        public const int EntityDetailsPtr = 0x08; // ✓ → EntityDetails
        public const int ComponentList    = 0x10; // ✓ StdVector of component pointers (8-byte elems)
        public const int Id               = 0x80; // (prior-art) uint  (read 0 for local player — revisit)
        public const int IsValid          = 0x84; // (prior-art) byte; valid when bit0 clear
    }

    public static class EntityDetails
    {
        public const int Name              = 0x08; // ✓ StdWString — metadata path (e.g. Metadata/Characters/<Class>/<Variant>)
        public const int ComponentLookUpPtr = 0x28; // ✓ → ComponentLookUp
    }

    /// <summary>ComponentLookUp: a StdBucket of (NamePtr, Index) at +0x28; index → ComponentList[index].</summary>
    public static class ComponentLookUp
    {
        public const int NameAndIndexBucket = 0x28; // ✓ StdBucket; its Data StdVector starts here
        public const int EntryStride        = 0x10; // ✓ {IntPtr NamePtr; int Index; int pad}
    }

    // ── Components (offsets from the component object base) ───────────────────

    /// <summary>Life — vital blocks. History: pre-0.5.4 Health/Mana/ES = 0x1A8/0x1F8/0x230; 0.5.4 (re-validated
    /// 2026-06-04, 980/980 HP, 427 mana, 274 ES) slid to 0x1B0/0x208/0x248. 2026-07-02 patch: EnergyShield
    /// drifted 0x248→0x264. 2026-07-10 patch: **EnergyShield drifted again 0x264→0x24C** (confirmed live by
    /// LO + auto-heal log lines from multiple users after that day's game update; Health/Mana unchanged). The
    /// VitalStruct internal layout (Max@+0x2C, Current@+0x30) is UNCHANGED throughout every one of these
    /// slides. Baking 0x24C in as the shipped default (Support v0.27) so users on the current patch stop
    /// paying the auto-heal cost on every launch; the auto-heal (Poe2Live.EnsureVitalOffsets) remains as
    /// the belt-and-suspenders backstop for any future drift.</summary>
    public static class Life
    {
        public const int Owner        = 0x008; // ComponentHeader.EntityPtr (back-pointer to entity)
        public const int Health       = 0x1B0; // ✓ VitalStruct (was 0x1A8 pre-0.5.4)
        public const int Mana         = 0x208; // ✓ VitalStruct (was 0x1F8 pre-0.5.4)
        public const int EnergyShield = 0x24C; // ✓ VitalStruct — drifted 0x264→0x24C on the 2026-07-10 patch (chain: 0x230 → 0x248 → 0x264 → 0x24C)
    }

    /// <summary>VitalStruct — ✓ (Max/Current confirmed). Reuse <see cref="VitalStruct"/> for reads.</summary>
    public static class Vital
    {
        public const int ReservedFlat = 0x10;
        public const int Regen        = 0x28;
        public const int Max          = 0x2C; // ✓
        public const int Current      = 0x30; // ✓
    }

    /// <summary>Render component.</summary>
    public static class Render
    {
        public const int CurrentWorldPosition = 0x138; // ✓ Vector3 (X,Y,Z); grid = XY / WorldToGridRatio
        public const int ModelBounds          = 0x144; // candidate (3 floats right after world pos)
    }

    /// <summary>Player component — character name + level + experience. ✓ Name/Level validated live
    /// (StdWString @ +0x1B0, level byte @ +0x204, 27 confirmed). CurrentExperience (+0x1D8) is a uint32
    /// per the imkk000/poe2-offsets extraction (2026-07-08); PoE2's 100-cap experience ~4.25B fits.
    /// Widened to long by callers for JSON serialisation ergonomics.</summary>
    public static class PlayerComponent
    {
        public const int Name              = 0x1B0; // ✓ StdWString
        public const int CurrentExperience = 0x1D8; // uint32 — upstream (imkk000/poe2-offsets 2026-07-08)
        public const int Level             = 0x204; // ✓ byte (low byte of a u32 slot)
    }

    /// <summary>Camera object (at InGameState+0x368). Holds the WorldToScreen matrix.</summary>
    public static class Camera
    {
        // The matrix is stored duplicated (two identical 0x40-byte copies back-to-back); the first
        // copy is at +0x1A0. Row-major Matrix4x4; screen = project(world * M). Validated visually.
        public const int WorldToScreenMatrix = 0x1A0;
        public const int Zoom = 0x528; // float, == 1.0 confirmed
    }

    /// <summary>MinimapIcon component — present on entities the game marks as map POIs (waypoints,
    /// checkpoints, league encounters…). <see cref="CompletedState"/> is an int the game flips when a
    /// repeatable encounter is finished: it then FADES the icon rather than removing it. ✓ validated
    /// live on an Expedition2Encounter — 0 while not-started/ready/active/looting, 1 after the reward
    /// was claimed. Read it live (don't cache the value): the component stays put; only the flag flips.</summary>
    public static class MinimapIcon
    {
        public const int CompletedState = 0x10; // ✓ int — 0 = active/shown, non-zero = completed/faded
    }

    /// <summary>StateMachine component — drives stateful devices. Its listener vector at
    /// <see cref="ListenerVec"/> registers the device's RuneStation (see <see cref="RuneStation"/>).</summary>
    public static class StateMachine
    {
        public const int ListenerVec = 0x20; // ✓ StdVector {first,last} of listener-node ptrs
    }

    /// <summary>RuneStation — the heap object behind a runeshape-monolith device (the persistent
    /// <c>Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter</c> entity, the one carrying the
    /// MinimapIcon POI). NOT an entity/component: it's reached from the device via
    /// device→StateMachine→listener-vec → <c>station = *(node) − <see cref="ListenerSub"/></c>, verified by
    /// <c>*(station + <see cref="Owner"/>) == device</c>. Exposes the monolith's hole count + anchor rune
    /// WITHOUT opening the panel (and persists out of the network bubble → readable area-wide).
    /// ✓ validated live 2026-06-20 (Research <c>--monolith</c>): N=3, anchor rune index 12 ("Cyclonic").</summary>
    public static class RuneStation
    {
        public const int Owner       = 0x10; // ✓ → device entity (verification)
        public const int AnchorRef   = 0x28; // ✓ → Expedition2Runes row ptr (0 = no anchor → "unique" monolith)
        public const int AnchorHolder= 0x30; // ✓ → holder; (+0x28 → rune-table ptr; *ptr = per-area table base)
        public const int HoleCount   = 0x38; // ✓ int N — the authoritative recipe hole count ("slots")
        public const int AnchorPos   = 0x3c; // ✓ int — anchor hole index (0-based)
        public const int ListenerSub = 0xA0; // listener node ptr; 0x98→0xA0 on the 2026-06-25 patch (upstream Sikaka 058db5d); re-validate via Research --monolith
        public const int RuneStride  = 0x68; // Expedition2Runes row stride (anchorIdx=(rowPtr-base)/stride); 0x6c→0x68 on the 2026-06-25 patch (upstream Sikaka 058db5d)
        public const int RuneCount   = 34;   // ✓ Expedition2Runes rows 0..33
    }

    /// <summary>ObjectMagicProperties component — monster/chest rarity.</summary>
    public static class ObjectMagicProperties
    {
        // ✓ validated live across 21 monsters (values 0 and 2 seen). Enum: 0=Normal,1=Magic,2=Rare,3=Unique.
        public const int Rarity = 0x144;

        // ⚠ affix-mod vector (the rolled monster modifiers — auras/buffs like MonsterPhysicalDamageAura1).
        // std::vector at +0x168; element stride 0x20, record pointer at element+0x8, mod-id UTF-16 string
        // at record+0x0. Validated live 2026-06-11 across Magic/Rare/Unique (Research --mods); the seed
        // matched what the brute-force discovery found on every monster. NOT yet ✓-tier — one patch's
        // evidence — and patch-volatile, so the overlay reads it but Research --mods re-discovers on drift.
        // (+0x150 is the rarity/tier PLACEHOLDER vector — MonsterRare/Magic/Unique{N} filler — not affixes.)
        public const int Mods = 0x168;
        public const int ModElemStride = 0x20;
        public const int ModRecordPtr = 0x8;   // element + this → mod record pointer
        public const int ModIdString = 0x0;    // record + this → POINTER to the UTF-16 mod id (always deref, even when 0)
    }

    /// <summary>WorldItem component — wraps a dropped item on the ground. ⚠ validated live 2026-06-12
    /// (Research --item) on a dropped unique staff: the container entity is "Metadata/MiscellaneousObjects/
    /// WorldItem"; its WorldItem component +0x28 points to the actual item entity (its own
    /// EntityDetails/ComponentList, metadata "Metadata/Items/...").</summary>
    public static class WorldItemComponent
    {
        public const int ItemEntity = 0x28; // ⚠ → inner item entity
    }

    /// <summary>RenderItem component (on the inner item entity) — the item's 2D art. ⚠ validated live
    /// 2026-06-12: +0x28 is a pointer to the UTF-16 .dds resource path (e.g.
    /// "Art/2DItems/Weapons/.../Uniques/Earthbound.dds"). The basename ("Earthbound") is the price-lookup
    /// key — it matches poe2scout's IconUrl basename. NB: RenderItem also lists socketed-gem art at later
    /// offsets, so take the FIRST entry (the item's own art).</summary>
    public static class RenderItemComponent
    {
        public const int ResourcePath = 0x28; // ⚠ → UTF-16 .dds art path
    }

    /// <summary>Base component (on the inner item entity) — the item's BASE TYPE, including the rendered
    /// display name. ✓ validated live 2026-06-20 (Research --itemdump on a dropped Greater Orb of
    /// Augmentation): <c>Base +0x10</c> → a row whose <c>+0x30</c> is a pointer to the UTF-16 display name
    /// ("Greater Orb of Augmentation"); <c>Base +0x18</c> → the BaseItemTypes row (+0x00 internal id
    /// "CurrencyAddModToMagic2", +0x08 .dds art, +0x10 .ao). The display name is the price-lookup key for
    /// NON-uniques (currency/runes/essences/…), which the shared .dds art can't disambiguate across tiers.</summary>
    public static class BaseComponent
    {
        public const int NameRow        = 0x10; // → row carrying the rendered display name
        public const int RowDisplayName = 0x30; // row + this → UTF-16 display base-type name
    }

    /// <summary>Mods component (on items) — rarity lives at a DIFFERENT offset than ObjectMagicProperties.
    /// ⚠ validated live 2026-06-12 on a dropped unique (read 3 = Unique). Matches upstream reference's
    /// ModsAndObjectMagicProperties (Rarity at the sub-struct's +0x94; for the item Mods component the
    /// sub-struct is at +0x00, so rarity = +0x94). Enum 0=Normal,1=Magic,2=Rare,3=Unique.</summary>
    public static class ModsComponent
    {
        public const int Rarity = 0x94;     // ✓ int (0=Normal,1=Magic,2=Rare,3=Unique)
        public const int Identified = 0x90; // ✓ int — 1 = identified, 0 = unidentified. Validated live
                                            // 2026-06-12 by diffing an identified unique (Earthbound=1) vs
                                            // an unidentified one (Keelhaul=0) on the ground.
        // Affix mod vectors — AllModsType (prior-art) lives at the sub-struct's +0xA0, each a StdVector of
        // ModArrayStruct (stride 0x40). A record's ModsPtr (+0x28) → Mods.dat row whose first qword →
        // UTF-16 internal mod id ("UniqueGiantsBlood1"). ✓ validated live 2026-06-16 against the
        // identified unique gloves "Treefingers Riveted Mitts" (read UniqueGiantsBlood1 + 5 more) and
        // equipped rares/uniques (explicit + implicit ids matched the worn gear).
        public const int ImplicitMods = 0xA0; // ✓ StdVector<ModArrayStruct>
        public const int ExplicitMods = 0xB8; // ✓ StdVector<ModArrayStruct>
        public const int EnchantMods  = 0xD0; // ✓ StdVector<ModArrayStruct>
        public const int ModArrayStride = 0x40; // ✓ sizeof(ModArrayStruct)
        public const int ModRecordPtr   = 0x28; // ✓ element + this → Mods.dat row
        public const int ModRecordIdPtr = 0x00; // ✓ row's first qword → UTF-16 internal mod id
    }

    /// <summary>Buffs component — the entity's active status-effect list. ✓ validated live 2026-07-01
    /// (Research --buffs): +0x160 is a StdVector&lt;StatusEffect*&gt; (First/Last/End, stride 8).</summary>
    public static class BuffsComponent
    {
        public const int BuffVector = 0x160; // ✓ StdVector<StatusEffect*> (First @ +0x160, Last @ +0x168)
    }

    /// <summary>One active buff/debuff. ✓ validated live 2026-07-01. +0x08 → Definition; +0x18 timer float
    /// (Inf/∞ = permanent aura, finite = temporary — the popped Life flask read 3.2).</summary>
    public static class StatusEffect
    {
        public const int Definition = 0x08; // ✓ ptr → BuffDefinition
        public const int Timer      = 0x18; // ✓ float — remaining time; Inf = permanent
        public const int MaxTimer   = 0x1C; // float — total/base (semantics unconfirmed; not shipped)
        public const int Charges    = 0x40; // int — stack/charge count (not shipped)
    }

    /// <summary>Buff definition row. ✓ validated live 2026-07-01: +0x00 = ptr to the UTF-16 internal id.</summary>
    public static class BuffDefinition
    {
        public const int IdPtr = 0x00; // ✓ ptr → UTF-16 buff id string (e.g. "igniting_presence_aura")
    }

    /// <summary>Stack component (on stackable items) — current stack count. ✓ validated live 2026-06-16
    /// (currency/gem stacks in the player inventory read their true counts; matches prior-art StackOffsets).</summary>
    public static class StackComponent
    {
        public const int Count = 0x18; // ✓ int — current stack size
    }

    /// <summary>Player inventory chain. ✓ validated live 2026-06-16 (--inventory): every inventory
    /// (equipment + backpack + flasks + stash-style) resolved with correct box dimensions and items.
    /// Chain: AreaInstance +0x598 → ServerData; ServerData +0x48 → StdVector PlayerServerData, [0] →
    /// ServerDataStructure; ServerDataStructure +0x320 → StdVector PlayerInventories (InventoryArrayStruct,
    /// stride 0x18). Each InventoryArrayStruct: +0x00 int InventoryId (Inventories.dat index: 1=Main,
    /// 2=BodyArmour, 3=Weapon1, 5=Helm, 6=Amulet, 7/8=Rings, 9=Gloves, 10=Boots, 11=Belt, 12=Flask…),
    /// +0x08 ptr InventoryStruct, +0x10 ptr (= +0x08 − 0x10, the fingerprint invariant).</summary>
    public static class ServerData
    {
        public const int League = 0x21E0;  // ✓ live 2026-06-22 (Sikaka v0.15.0, read-only) — std::wstring current league name as the game stores it (e.g. "HC Runes of Aldur", "Standard", "Hardcore"); the "HC " prefix identifies hardcore vs softcore.
        public const int PlayerServerDataVec = 0x48;  // ✓ StdVector<IntPtr>; [0] → ServerDataStructure
        public const int PlayerInventoriesVec = 0x320; // ✓ (on ServerDataStructure) StdVector<InventoryArrayStruct>
        public const int InvArrayStride = 0x18;        // ✓ sizeof(InventoryArrayStruct)
        public const int InvArrayId     = 0x00;        // ✓ int InventoryName index
        public const int InvArrayPtr    = 0x08;        // ✓ → InventoryStruct
    }

    /// <summary>InventoryStruct — one grid inventory. ✓ validated live 2026-06-16. TotalBoxes (X,Y) at
    /// +0x150; ItemList (StdVector of InventoryItemStruct pointers, length = X·Y) at +0x170.</summary>
    public static class Inventory
    {
        public const int TotalBoxesX = 0x150; // ✓ int columns
        public const int TotalBoxesY = 0x154; // ✓ int rows
        public const int ItemListVec = 0x170; // ✓ StdVector<IntPtr→InventoryItemStruct>
    }

    /// <summary>InventoryItemStruct — links a grid slot to an item entity. ✓ validated live 2026-06-16.
    /// Duplicate Item pointers across cells = a multi-cell item (de-dup by item address).</summary>
    public static class InventoryItem
    {
        public const int Item      = 0x00; // ✓ → item Entity (ItemBase/ComponentList; meta "Metadata/Items/…")
        public const int SlotStartX = 0x08; // ✓ int
        public const int SlotStartY = 0x0C; // ✓ int
        public const int SlotEndX   = 0x10; // ✓ int
        public const int SlotEndY   = 0x14; // ✓ int
    }

    /// <summary>Sockets component (on socketable items) — socketed runes/soul-cores/gems as item-entity
    /// pointers. ⚠ one observation 2026-06-16 (--itemdump on a rare body armour with 2 Lesser Life Runes):
    /// owner back-ptr at +0x08 (ComponentHeader.EntityPtr); the two socketed RuneLifeLesser entities read
    /// as consecutive inline pointers at +0x30 / +0x38. Whether that's a fixed inline array or a small-buffer
    /// StdVector — and the empty-socket representation — needs cross-validation on items with other socket
    /// counts (the lone +0x98 hit was likely an unrelated neighbour pointer).</summary>
    public static class SocketsComponent
    {
        public const int Owner          = 0x08; // ComponentHeader.EntityPtr
        public const int SocketedItems  = 0x30; // ⚠ first socketed item entity ptr (then +0x38, …)
    }

    /// <summary>Stats / LocalStats component — aggregated stat (key,value) pairs. ⚠ observed 2026-06-16.
    /// A StatArrayStruct is {int statIndex; int value}; the vector of them was found at +0x20 on an item's
    /// LocalStats component (read [131 = 18] = +18 local Energy Shield on the body armour). statIndex maps
    /// 1:1 to upstream reference's GameStats enum (value = Stats.dat row index + 1, e.g. 131 = local_energy_shield),
    /// and that enum's ordering MATCHES our live build — so statIndex → stat-id string is solved via a ported
    /// GameStats table. NB: only LOCAL stats live on an item; global mods (life/resist) only aggregate onto
    /// the character's Stats component once equipped. prior-art chain for the character Stats component:
    /// +0x160 → StatsStructInternal, Stats StdVector @ +0xF8 (StatArrayStruct stride 0x08).</summary>
    public static class StatsComponent
    {
        public const int StatArrayStride = 0x08; // ✓ {int statIndex; int value}
        // ⚠ item LocalStats: a {key,value} StdVector observed at component +0x20 (one entry). Character
        // Stats: StatsChangedByItemsPtr @ +0x160 → StatsStructInternal; its Stats vec @ +0xF8 (prior-art).
        public const int ItemLocalStatsVec = 0x20;  // ⚠ (one observation)
        public const int StatsChangedByItemsPtr = 0x160; // (prior-art) → StatsStructInternal
        public const int StatsStructStatsVec     = 0xF8;  // (prior-art) StdVector<StatArrayStruct>
    }

    /// <summary>Chest component. ✓ OpenState @ +0x168 — the offset is stable, but the 2026-06-06 patch
    /// INVERTED its polarity: now 0 = closed/openable, non-zero = opened/used (was 1=closed/0=opened,
    /// per the 2026-06-03 read). Re-validated live by diffing a rare chest closed-vs-opened (+0x168
    /// flipped 0→1). The fork's extra sub-offsets did NOT survive validation on our build.</summary>
    public static class ChestComponent
    {
        public const int OpenState    = 0x168; // ✓ 0 = closed/openable, non-zero = opened/used (polarity flipped 2026-06-06)
        public const int LabelVisible = 0x021; // byte — upstream (imkk000/poe2-offsets 2026-07-08)
    }

    /// <summary>Positioned component.</summary>
    public static class Positioned
    {
        // ✓ validated live: player (friendly) = 0x01, hostile MastodonBoss = 0x00.

        public const int Reaction = 0x1E0;

        // ✓ validated live (presence buff on/off sweep, Research --presence): the presence
        // area-of-effect scalar. Float, defaults to 1.0; a "+20% Presence AoE" buff drove it to
        // 1.0 from a ~0.92 base (≈ √1.2 radius scaling), and it tracked the buff on→off→on with
        // nothing else moving. Effective presence radius = base radius × this scalar.
        public const int PresenceAoeScale = 0x2A0;
    }

    /// <summary>
    /// TerrainStruct (base at AreaInstance+0x8B8). Validated live: TotalTiles (54,48) → 2592 tiles
    /// (matches TileDetails count); walkable grid 685584 bytes; BytesPerRow 621 → cellsPerRow 1242;
    /// grid 1242×1104 = (54×23)×(48×23). PoE2 has FOUR grid layers (0xD0/0xE8/0x100/0x118), so
    /// BytesPerRow sits at 0x130 — not GH2's 0x100.
    /// </summary>
    public static class Terrain
    {
        public const int TotalTiles        = 0x18;  // ✓ StdTuple2D<long> (tilesX, tilesY)
        public const int TileDetailsPtr    = 0x28;  // ✓ StdVector of TileStructure (0x38 bytes)
        public const int GridWalkableData  = 0xD0;  // ✓ StdVector — packed walkable grid bytes
        public const int GridLandscapeData = 0xE8;  // ✓ StdVector
        public const int GridLayer3        = 0x100; // ✓ StdVector (extra PoE2 layer)
        public const int GridLayer4        = 0x118; // ✓ StdVector (extra PoE2 layer)
        public const int BytesPerRow       = 0x130; // ✓ int (621 live) — cellsPerRow = ×2
        public const int TileGridCells     = 23;    // tile = 23×23 grid cells
    }

    /// <summary>One entry in Terrain.TileDetailsPtr (0x38 bytes). ✓ validated (TgtPath gives tile names).</summary>
    public const int TileStructureSize = 0x38;
    public static class TileStructure
    {
        public const int SubTileDetailsPtr = 0x00; // pointer
        public const int TgtFilePtr        = 0x08; // ✓ → TgtFileStruct
        public const int TileHeight        = 0x30; // short
        public const int RotationSelector  = 0x36; // byte
    }

    public static class TgtFileStruct
    {
        public const int TgtPath = 0x08; // ✓ StdWString — full tile .tdt path (e.g. .../Feature/arena_01.tdt)
    }

    /// <summary>
    /// MapUiElement (large map + minimap share this class/vtable). ✓ validated live: exactly two
    /// elements carry DefaultShift=(0,-20) with Zoom=0.5. Struct shape matches prior-art (shifted +0x70):
    /// Shift→DefaultShift = 8, DefaultShift→Zoom = 0x38.
    /// </summary>
    public static class MapUiElement
    {
        public const int Shift        = 0x368; // ✓ StdTuple2D<float>
        public const int DefaultShift = 0x370; // ✓ StdTuple2D<float> (0,-20)
        public const int Zoom         = 0x3A8; // ✓ float (0.5 live)
    }

    /// <summary>UiElement base — ✓ validated live (prior-art's offsets drifted: Self 0x30→0x8, Flags 0x1B8→0x180).
    /// Parent/Position/Size from the 2026-06-07 community offset dump (resources/additional offsets.txt);
    /// Position + Size confirmed live on the atlas-node class (size = 40×40 icons, positions vary per node).</summary>
    public static class UiElement
    {
        public const int Self           = 0x08;  // ✓ self pointer
        public const int Children       = 0x10;  // ✓ StdVector begin (child UiElement ptrs); End @ +0x18
        public const int ChildrenEnd    = 0x18;  // ✓ StdVector end
        public const int PositionModifier = 0xF0; // StdTuple2D<float>; added to parent pos when Flags bit 0x0A set (prior-art UiElementBase)
        public const int Parent         = 0xB8;  // (community) parent UiElement; true UI root = *(UiRoot+0xB8)
        public const int RelativePos    = 0x118; // ✓ StdTuple2D<float> position relative to parent (varies per atlas node)
        public const int LocalScaleMul  = 0x130; // float local scale multiplier (also the atlas zoom on node elements)
        public const int Flags          = 0x180; // ✓ uint; IsVisibleLocal = bit 0x0B (toggle-diff: 0x2EF1↔0x26F1)
        public const int FlagVisibleBit = 0x0B;  // ✓ visible bit (set when shown)
        public const int FlagModifyPosBit = 0x0A; // when set, PositionModifier (+0xF0) is added to the parent pos
        public const int ScaleIndex     = 0x18A; // byte; selects which axis scale(s) apply (1=v1,2=v2,3=v1×v2). root=3
        public const int Text           = 0x390; // std::wstring of the element's displayed text (font name @ +0xC8).
                                                  // Validated live 2026-06-14: every text element (loot tags, skill
                                                  // rows, runeforge rows) holds its UTF-16 string here.
        public const int SizeW          = 0x288; // ✓ float unscaled width  (atlas node = 40)
        public const int SizeH          = 0x28C; // ✓ float unscaled height (atlas node = 40)
        // Full visibility is hierarchical: an element is shown iff its own bit 0x0B AND every
        // ancestor's bit are set. Walk Parent (+0xB8) up to the root.
        // Screen geometry (prior-art UiElementBaseFuncs): v1 = winW/2560, v2 = winH/1600 (BaseResolution
        // 2560×1600). ScaleValue(ScaleIndex, LocalScaleMul): idx1→(v1,v1) idx2→(v2,v2) idx3→(v1,v2),
        // else (mul,mul). screenPos = unscaledParentChainPos × ScaleValue; screenSize = UnscaledSize × ScaleValue.
        public const double BaseResW = 2560.0;
        public const double BaseResH = 1600.0;
    }

    /// <summary>"Runeshape Combinations" reward panel (rune-crafting league mechanic). The panel is found
    /// by a UI-FLAGS-FINGERPRINT walk with backtracking from GameUi (= <see cref="InGameState.UiRoot"/>,
    /// the UiRootStruct the game treats as a UiElement) — child indices drift per patch/restart, the Flags
    /// "role" bits don't. Each fingerprint is matched with the visible bit (0x800) masked out; step 0
    /// (window-container) must be VISIBLE = panel open. Validated live 2026-06-14 (Research --runeforge);
    /// re-validate per patch (the probe prints GameUi child flags on resolve-fail for re-fingerprinting).</summary>
    public static class Runeforge
    {
        // window-container (gate) → … → recipes-container. (visible bit masked out before compare.)
        public static readonly uint[] PanelFlagFingerprints =
            { 0x00462EF1, 0x00502EF3, 0x00502EF7, 0x00542EF1, 0x00502EF1 };
        public const int GateStep = 0;       // the window-container; its visible bit gates panel-open
        public const int ViewportStep = 2;   // this hop's element holds the scroll offset (+0x120)
        public const int ScrollOffset = 0x120; // StdTuple2D<float> viewport scroll offset
        public const int NameWString = 0x390;  // visible row's kid[0]: inline std::wstring "<count>x <name>"
    }

    /// <summary>Ritual tribute-shop reward grid. The reward TILES are item-slot UiElements (same "ItemFrame"
    /// element type as the flask bar): each holds its reward item Entity at <see cref="TileSlotItem"/>. The
    /// grid is found by walking up from a shop-signature text element to the ancestor whose child is a
    /// container of these tiles (see <c>Poe2Live.ReadRitualRewards</c>). Validated live 2026-06-20 (Research
    /// <c>--tooltip-capture</c>): all 5 offered rewards read as full item entities with no hover needed.</summary>
    public static class Ritual
    {
        public const int TileSlotItem = 0x4F8; // ✓ item-slot UiElement → reward item Entity (also the flask-bar slot field)
    }

    /// <summary>Atlas map-node UiElement (a subclass with its own vtable; ~1200+ instances live in the
    /// open Atlas). Fields from the 2026-06-07 community dump; structurally confirmed live: biome
    /// (+0x32E) spread 0..12, per-node positions (UiElement.RelativePos), 40×40 size, scale (+0x130) =
    /// the atlas zoom. (+0x300 is a map-TYPE id shared by same-type nodes — NOT unique per node.)
    ///
    /// <para><b>PROJECTION (✓ live, pan + zoom):</b> a node's on-screen position is
    /// <c>screen = (UIscale × zoom) × relPos + offset</c>, where relPos = +0x118 (read live; the game
    /// rewrites it on PAN so pan is free), zoom = +0x130 (read live; ~0.85 max zoom-out → larger zoomed
    /// in), UIscale = winH/1600, offset ≈ factor×½icon ≈ (15,13) @ 1080p/zoom-0.85. NOT a perspective
    /// homography. The overlay derives the WHOLE projection live from the window height + live zoom
    /// (RadarApp.AtlasProjection) — resolution-correct with no calibration. <b>Recovery after a patch:</b> run
    /// <c>POE2Radar.Research --atlas-probe</c> (Atlas map open) — it re-locates the class + canvas,
    /// validates every offset, and prints the derived projection. Only the node-class vtable drifts.
    /// See resources/atlas-research-notes.md "FULLY SOLVED".</para></summary>
    /// <summary>The EndgameMaps row a node points at (node <see cref="AtlasNode.MapNodeId"/> +0x300 → row).
    /// Its +0x00 → the WorldAreas row, whose +0x00 is the Id ("MapXxx") and +0x08 is the LOCALIZED display
    /// name ("Savannah"/"Digsite"/"Precursor Tower"). ✓ validated live 2026-06-16 (Research --atlas-mapname);
    /// reading +0x08 fixed web-UI filters where Prettify(code) mismatched the in-game name.</summary>
    public static class AtlasMapRow
    {
        public const int WorldAreaName = 0x08; // ✓ WorldAreas row +0x08 → UTF-16 localized map name
    }

    public static class AtlasNode
    {
        public const int MapNodeId   = 0x300; // ✓ u32 — distinct per node
        public const int Content     = 0x310; // (community) u32 content (0 = none)
        public const int State       = 0x32C; // (community) u8 state (seen =1 on loaded nodes)
        public const int Biome       = 0x32E; // ✓ u8 biome index (0..12)
        public const int Flags       = 0x32F; // (community) u8: bit0 unlocked, bit1 visited
        public const int GridPos     = 0x320; // ✓ live 2026-06-08 — StdTuple2D<int> atlas grid coord (X,Y); 1:1 with node, range small (e.g. X[-16..31] Y[0..47]). The key for node-graph pathfinding. (prior-art-sourced)
        public const int Completion  = 0x339; // (community) u8 per-node completion id
        public const int ContentVec  = 0x350; // (community) StdVector begin (content list); End @ +0x358

        /// <summary>Alternate node-DATA model (prior-art): <c>*(*(node+0x10)+0x20)</c> → a struct with
        /// biome <c>+0x2CE</c> / status byte <c>+0x2CF</c> (bit0 accessible, bit1 completed) / mapId at
        /// <c>+0x2A0</c> (ptr→ptr→ptr→UTF-16 "MapXxx"). Validated live 2026-06-08 (biome matches the
        /// element's own <see cref="Biome"/> 200/200). POE2Radar reads biome/mapId DIRECTLY off the
        /// element (<see cref="Biome"/>, <see cref="MapNodeId"/> + the +0x300 EndgameMaps row), so this
        /// deeper model is an alternate source, not required.</summary>
        public const int DataStorage = 0x10;   // *(node+0x10) → storage
        public const int DataModel   = 0x20;   // *(storage+0x20) → nodeData
        public const int DataBiome   = 0x2CE;  // u8 within nodeData
        public const int DataStatus  = 0x2CF;  // u8 within nodeData: bit0 accessible, bit1 completed
        public const int DataMapId   = 0x2A0;  // ptr chain → UTF-16 "MapXxx"
    }

    /// <summary>Atlas CONNECTION GRAPH (✓ live 2026-06-08, prior-art-sourced). The node canvas (the
    /// parent holding the most node-class children — POE2Radar's detected <c>_nodeCanvas</c>) carries a
    /// <c>StdVector</c> of edges at <c>+0x5A8</c>. Each edge is 20 bytes: <c>{ int unknown; StdTuple2D&lt;int&gt;
    /// source; StdTuple2D&lt;int&gt; target }</c> — source @ +0x04, target @ +0x0C, both in node grid
    /// coords (<see cref="AtlasNode.GridPos"/>). Live: 291 edges, 100% endpoints on real grid positions,
    /// avg degree 2.9 / max 5 (a real sparse atlas graph). This is what enables "route from the player's
    /// current node to a target node in the fewest hops" (A* over the graph, per prior-art's FindShortestPathAStar).
    /// Re-discover after a patch with <c>POE2Radar.Research --atlas-graph</c>.</summary>
    public static class AtlasGraph
    {
        public const int ConnectionsVec = 0x5A8; // on the node canvas: StdVector<edge> begin; End @ +0x5B0
        public const int EdgeStride     = 20;
        public const int EdgeSourceOff  = 0x04;  // StdTuple2D<int>
        public const int EdgeTargetOff  = 0x0C;  // StdTuple2D<int>

        /// <summary>Current-location ("player icon") marker: the SINGLE non-node UiElement in the atlas
        /// UI subtree whose <c>+0x300</c> field points at a node-class element. That target node is the map
        /// the player is currently in (✓ live 2026-06-08 — held even while standing in a hideout). The
        /// accessor is structural, not vtable-keyed (the marker's class drifts per patch), so it's found by
        /// "the lone non-node element whose +0x300 ∈ node set". <c>currentNode = *(marker + 0x300)</c>, then
        /// read the node's <see cref="AtlasNode.GridPos"/>. Re-discover with <c>--atlas-marker</c>.</summary>
        public const int CurrentMarkerNodePtr = 0x300;
    }

    /// <summary>Atlas screen panel — a PERSISTENT direct child of UiRoot (the element at
    /// <c>InGameState+0x2F0</c>, walked via its Children StdVector <c>+0x10</c>) at <see cref="UiRootChildIndex"/>.
    /// Present from a cold launch even when the atlas has NEVER been opened (✓ live 2026-06-08); its
    /// UiElement visible bit (Flags <c>+0x180</c> bit <c>0x0B</c>) is the only thing that toggles when the
    /// atlas opens/closes (closed flags 0x5626F5 → open 0x562EF5). This is the cheap atlas open-gate:
    /// reading this one element's visible bit is ~4 reads, versus BFS-walking the ~50k-element UI tree to
    /// (re)detect the node class — which while the atlas is closed can never succeed and so would burn that
    /// BFS every retry. <b>If a patch shifts UiRoot's children this index drifts</b> — re-discover by
    /// diffing the DevTree <c>/api/ui-flat</c> tree closed-vs-open (the element whose visible bit flips at
    /// the shallowest stable path). <see cref="ExpectedChildCount"/> is a secondary signature (18 children).</summary>
    public static class AtlasPanel
    {
        public const int UiRootChildIndex  = 22; // ✓ live 2026-06-08 — stable across a cold restart
        public const int ExpectedChildCount = 18; // ✓ signature (panel had 18 children closed + open)
    }

    /// <summary>World hover tracker (community, 2026-06-07): <c>*(UiRoot+0x7D8)+0x630</c>; hovered entity
    /// at +0x18. Singletons share vtable (image+0x2D707D8). The capture anchor for "what am I pointing at".</summary>
    public static class HoverTracker
    {
        public const int FromUiRoot          = 0x7D8; // *(UiRoot + 0x7D8) → tracker container
        public const int WorldTracker        = 0x630; // + 0x630 → world hover tracker
        public const int HoveredEntity       = 0x18;  // + 0x18 → hovered entity/element (existing)
        public const int HoveredEntityDirect = 0x18;  // canonical anchor for PROBE-CORE (alias of HoveredEntity)
    }

    /// <summary>Loaded-files list (Preload Alert). ✓ validated live 2026-06-30 via --preload.
    /// FileRoot(AOB) → 16 buckets @0x38 (each a StdVector) → node @0x18 → FilesPointer+0x08 →
    /// FileInfo{ Name StdWString @+0x08, AreaChangeCount int @+0x40 }.</summary>
    public static class LoadedFiles
    {
        public const int BucketCount   = 16;
        public const int BucketStride  = 0x38;
        public const int NodeStride    = 0x18;
        public const int FilesPointer  = 0x08;
        public const int NameStr       = 0x08;
        public const int AreaChangeCnt = 0x40;
    }

    // ── Campaign-Probe offset floor (PROBE-OFFSETS, imkk000/poe2-offsets 2026-07-08) ────────────

    /// <summary>PlayerServerData record (element [0] of ServerData's PlayerServerData StdVector at
    /// ServerData+0x48). Hosts per-player mutable state. QuestFlags at +0x230 is a
    /// <c>Dictionary&lt;QuestFlag, bool&gt;</c> the game uses to gate quest branches (drives
    /// <c>npc_dialogue_option_selected</c> inference and <c>waypoint_travel</c> context in the campaign
    /// probe). Source: imkk000/poe2-offsets element.go (2026-07-08 extraction).</summary>
    public static class PlayerServerData
    {
        public const int QuestFlags = 0x230; // Dictionary<QuestFlag,bool> (drives quest-progression events)
    }

    /// <summary>Quest definition + entry layout. Source: imkk000/poe2-offsets quest.go (2026-07-08).
    /// <c>DefinitionPtr</c> / <c>EntryPtr</c> live off the quest root; <c>EntryState</c> (byte) and
    /// <c>EntryObjective</c> track per-quest progress. <c>RowId</c> / <c>RowName</c> live inside a
    /// quest-row struct (definition table entry).</summary>
    public static class Quest
    {
        public const int DefinitionPtr   = 0x2E0; // ptr to Quest definition
        public const int EntryPtr        = 0x2F0; // ptr to Quest entry
        public const int EntryState      = 0x3C;  // byte — entry state (drives dialogue_option/waypoint context)
        public const int EntryObjective  = 0x3D;  // objective sub-state byte
        public const int RowId           = 0x00;  // quest id within row
        public const int RowName         = 0x0C;  // quest name within row
    }

    /// <summary>Passive tree allocation. Reached from AreaInstance via the four-hop pointer chain
    /// (HopChain) that lands on ServerPlayerData; the allocated-node vector runs from AllocVecBegin
    /// (+0x8A8) to AllocVecEnd (+0x8B0) with 4-byte stride — each entry is a uint16 node id (top 2
    /// bytes pad). Source: imkk000/poe2-offsets passive_tree.go (2026-07-08). Drives the campaign
    /// probe's <c>passive_allocated</c> diff-observer.</summary>
    public static class PassiveTree
    {
        public const int AllocVecBegin = 0x8A8; // on ServerPlayerData
        public const int AllocVecEnd   = 0x8B0; // = begin + 8 (StdVector last)
        public const int EntryStride   = 4;     // uint16 node id + 2 bytes pad
        public const int AllocMax      = 1024;  // sanity cap on entry count
        /// <summary>Pointer chain from AreaInstance to ServerPlayerData: dereference each offset in order.</summary>
        public static readonly int[] HopChain = { 0x60, 0x40, 0xCE0, 0x418 };
    }

    /// <summary>Targetable component byte flags. Source: imkk000/poe2-offsets world_components.go
    /// (2026-07-08). Drives interaction detection — used by the campaign probe to refine
    /// <c>area_transition_used</c> (only fire when Transition entity was actually targeted).</summary>
    public static class Targetable
    {
        public const int IsTargetable = 0x69; // byte — 1 when entity accepts targeting
        public const int IsHighlight  = 0x6A; // byte — 1 when highlight ring shown
        public const int IsTargeted   = 0x6B; // byte — 1 when player has this entity targeted
        public const int IsHidden     = 0x71; // byte — 1 when entity is hidden from cursor
    }

    /// <summary>Shrine component. Source: imkk000/poe2-offsets world_components.go (2026-07-08).</summary>
    public static class Shrine
    {
        public const int IsUsed = 0x24; // byte — 1 after activation
    }

    /// <summary>Transitionable component (area-transition entities carry this alongside Targetable).
    /// State is int16 — non-zero values encode "opened" / "traversed". Source: imkk000/poe2-offsets
    /// world_components.go (2026-07-08).</summary>
    public static class Transitionable
    {
        public const int State = 0x120; // int16
    }

    /// <summary>TriggerableBlockage component (barriers, breakable walls). Source: imkk000/poe2-offsets
    /// world_components.go (2026-07-08).</summary>
    public static class TriggerableBlockage
    {
        public const int IsBlocked = 0x30; // byte
    }

    /// <summary>StateMachine extended layout for probe-side state reads. The existing
    /// <see cref="StateMachine.ListenerVec"/> (+0x20) drives the RuneStation chain; the additional
    /// state and timer vectors here (+0x160..+0x180) expose per-entity state slots used by
    /// Chest/Shrine/Transitionable interaction detection. Source: imkk000/poe2-offsets
    /// world_components.go (2026-07-08). Named "StateMachineExt" (not shadowing the shipped
    /// <see cref="StateMachine"/>) so the RuneStation code path is untouched.</summary>
    public static class StateMachineExt
    {
        public const int StatesBegin = 0x160;
        public const int StatesEnd   = 0x168;
        public const int TimersBegin = 0x178;
        public const int TimersEnd   = 0x180;
        public const int EntryStride = 8;
    }
}
