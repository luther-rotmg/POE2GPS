namespace POE2Radar.Core.Game;

/// <summary>
/// Read-only reader for the PoE2 Atlas. Exposes (a) the map-archetype CATALOG + current-region map set
/// (for the dashboard's Atlas tab), and (b) the LIVE NODE GRAPH — atlas nodes are UiElements (one
/// vtable subclass) carrying per-node id/biome/content/flags/completion + a live screen-space position
/// (see <see cref="ReadNodes"/>). Node offsets validated live 2026-06-07 (resources/additional offsets.txt
/// + atlas-research-notes.md).
///
/// <para><b>Catalog</b> — an array of 0x18-byte entries <c>{int32 id; IntPtr parsedObj (stride 0x300);
/// IntPtr idStr → UTF-16 "MapXxx"}</c>. The display name follows the code inline in the dat row.
/// 139 entries seen live (all biomes, towers, uniques, Citadel/uber-boss maps).</para>
///
/// <para><b>Region set</b> — a 0x18-byte array <c>{IntPtr record; IntPtr archetype (a catalog parsedObj);
/// IntPtr sharedConst}</c>: one entry per map type present in the current atlas view.</para>
///
/// <para>Addresses are session-specific, so both structures are LOCATED by signature scan over the
/// game-data arena and cached (re-validated cheaply on each read; re-scanned only if the cache goes
/// stale). The catalog signature — a run of entries whose idStr reads "Map…" — is unambiguous.</para>
/// </summary>
public sealed class Poe2Atlas
{
    private readonly MemoryReader _reader;
    private readonly object _lock = new();

    private volatile int _catalogBaseLo, _catalogBaseHi; // first catalog entry split into two ints for volatile access (0 = not located)
    private int _catalogCount;
    private volatile bool _scanning;     // a background locate is in flight
    private nint _scanLo, _scanHi;       // game-heap slab to scan (derived from the live chain anchor)
    private readonly byte[] _scanChunk = new byte[1 << 20];   // background Task.Run scan thread only
    private readonly byte[] _regionBuffer = new byte[1 << 20]; // Reused 1 MiB scratch buffer for ReadRegion; single-caller world thread means no locking required

    private nint CatalogBase
    {
        get => (nint)(((long)(uint)_catalogBaseHi << 32) | (uint)_catalogBaseLo);
        set { _catalogBaseLo = (int)(long)value; _catalogBaseHi = (int)((long)value >> 32); }
    }

    private const int Stride = 0x18;
    private static readonly byte[] MapPrefix = { 0x4D, 0x00, 0x61, 0x00, 0x70, 0x00 }; // "Map" UTF-16LE

    public Poe2Atlas(MemoryReader reader) => _reader = reader;

    /// <summary>One map archetype: its internal code ("MapSteppe"), display name ("Steppe"), an id
    /// (tracks roughly with tier/level), and the address of its parsed runtime object.</summary>
    public readonly record struct MapType(int Id, string Code, string Name, string Kind, long ParsedObj, long IdStr);

    /// <summary>Archetype class derived from the code — drives "valuable map" filtering. (Per-node rolled
    /// content like a boss modifier isn't reachable; this is the map TYPE's inherent class.)</summary>
    public static string Classify(string code)
    {
        if (code.Contains("Citadel", StringComparison.Ordinal)) return "Citadel";
        if (code.Contains("UberBoss", StringComparison.Ordinal)) return "Boss";
        if (code.Contains("HildaCampsite", StringComparison.Ordinal) || code.Contains("Wildwood", StringComparison.Ordinal)) return "Unique";
        if (code.Contains("Merchant", StringComparison.Ordinal)) return "Merchant";
        if (code.Contains("Unique", StringComparison.Ordinal)) return "Unique";
        if (code.Contains("Tower", StringComparison.Ordinal)) return "Tower";
        return "Normal";
    }

    /// <summary>One map type present in the current atlas region (resolved to its archetype code/name).</summary>
    public readonly record struct RegionMap(string Code, string Name, string Kind, long Record);

    /// <summary>The full read result. <see cref="Located"/> is false when the catalog can't be found
    /// (not in/near the atlas, or the layout drifted) — the dashboard shows that state rather than guessing.</summary>
    public sealed record AtlasData(
        bool Located, long CatalogAddr, int CatalogCount,
        IReadOnlyList<MapType> Catalog, IReadOnlyList<RegionMap> Region, string Note);

    /// <summary>Locate (cached) + read the catalog and current-region map set. Thread-safe; safe to call
    /// from the API thread concurrently with the tick loop (independent reads on the same handle).
    /// <para><paramref name="anchor"/> is any live in-arena address (e.g. the current AreaInstance): the
    /// catalog lives in the same heap slab, so we scan the 1 TB-aligned slab containing the anchor —
    /// robust to ASLR across sessions. Pass 0 only as a last resort (scans every readable region).</para></summary>
    public AtlasData Read(nint anchor = 0)
    {
        var baseAddr = CatalogBase;
        // Cache hit: re-validate cheaply, then walk (fast). No scan.
        if (baseAddr != 0 && IsCatalogEntry(baseAddr) && IsCatalogEntry(baseAddr + Stride))
        {
            var catalog = WalkCatalog(baseAddr, _catalogCount);
            if (catalog.Count == 0) { CatalogBase = 0; return NotLocated("Catalog cache stale; refresh to re-scan."); }
            var byParsed = new Dictionary<nint, MapType>(catalog.Count);
            foreach (var m in catalog) byParsed[(nint)m.ParsedObj] = m;
            var region = ReadRegion(byParsed, baseAddr);
            var note = region.Count == 0 ? "Current-region map set not located (catalog still valid)." : "";
            return new AtlasData(true, (long)baseAddr, catalog.Count, catalog, region, note);
        }

        // Not located yet — kick off (or report) a one-time BACKGROUND scan. The slab scan is seconds-
        // long, so it must not block the API thread; the dashboard polls/refreshes until it's ready.
        if (_scanning) return NotLocated("Scanning game memory for the atlas catalog… (one-time, ~1–2 min) — refresh shortly.");
        if (anchor == 0) return NotLocated("Not in game / no anchor yet — open the Atlas, then refresh.");

        var lo = (nint)((long)anchor & ~0xFF_FFFF_FFFFL);          // 1 TB-aligned slab containing the anchor
        var hi = (nint)((long)lo + 0x100_0000_0000L);
        lock (_lock)
        {
            if (_scanning || CatalogBase != 0) return Read(anchor); // another thread won the race
            _scanning = true;
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                _scanLo = lo; _scanHi = hi;
                // The catalog (loaded at startup) is typically BELOW the live AreaInstance — scan there
                // first (roughly halves the work), then above only if needed.
                if (!ScanForCatalog(lo, anchor)) ScanForCatalog(anchor, hi);
            }
            finally { _scanning = false; }
        });
        return NotLocated("Scanning game memory for the atlas catalog… (one-time, ~1–2 min) — refresh shortly.");
    }

    private static AtlasData NotLocated(string note)
        => new(false, 0, 0, Array.Empty<MapType>(), Array.Empty<RegionMap>(), note);

    /// <summary>An address is a catalog entry iff +0x08 and +0x10 are canonical heap pointers and the
    /// +0x10 target begins "Map" in UTF-16.</summary>
    private bool IsCatalogEntry(nint e)
    {
        var obj = Ptr(e + 0x08);
        var idStr = Ptr(e + 0x10);
        if (obj == 0 || idStr == 0) return false;
        Span<byte> b = stackalloc byte[6];
        return _reader.TryReadBytes(idStr, b) == 6 && b.SequenceEqual(MapPrefix);
    }

    /// <summary>Scan [lo,hi) for a run of ≥8 consecutive catalog entries; on a hit, walk to the run's
    /// bounds and cache base + count. Cheap-prunes candidates on the in-buffer pointer shape before
    /// the (syscall) idStr deref.</summary>
    private bool ScanForCatalog(nint lo, nint hi)
    {
        var overlap = Stride * 9;
        foreach (var (regionBase, regionSize) in _reader.Process.EnumerateReadableRegions(privateOnly: false))
        {
            if ((long)regionBase + regionSize <= (long)lo || (long)regionBase >= (long)hi) continue;
            long off = 0;
            while (off < regionSize)
            {
                var toRead = (int)Math.Min(_scanChunk.Length, regionSize - off);
                var read = _reader.TryReadBytes(regionBase + (nint)off, _scanChunk.AsSpan(0, toRead));
                if (read <= 0) break;
                for (var i = 0; i + Stride * 8 <= read; i += 8)
                {
                    // Cheap prune (no syscall — all from the buffer): a catalog entry starts with a small
                    // int32 id, then two canonical heap pointers. Random data rarely matches all three.
                    var id = BitConverter.ToInt32(_scanChunk, i);
                    if (id is < 0 or > 4096) continue;
                    if (!IsCanon((nint)BitConverter.ToInt64(_scanChunk, i + 0x08))) continue;
                    if (!IsCanon((nint)BitConverter.ToInt64(_scanChunk, i + 0x10))) continue;
                    var baseAddr = regionBase + (nint)(off + i);
                    if (!IsCatalogEntry(baseAddr)) continue;
                    // Confirm a run (≥6 more) before committing.
                    var ok = true;
                    for (var k = 1; k <= 6 && ok; k++) ok = IsCatalogEntry(baseAddr + (nint)(k * Stride));
                    if (!ok) continue;
                    // Walk to the true start + count.
                    var start = baseAddr;
                    while (IsCatalogEntry(start - Stride)) start -= Stride;
                    var count = 0;
                    for (var e = start; count < 20000 && IsCatalogEntry(e); e += Stride) count++;
                    _catalogCount = count; CatalogBase = start;
                    return true;
                }
                if (read != toRead) break;
                if (toRead < _scanChunk.Length) break;
                off += _scanChunk.Length - overlap;
            }
        }
        return false;
    }

    private List<MapType> WalkCatalog(nint start, int count)
    {
        var list = new List<MapType>(count);
        for (var i = 0; i < count; i++)
        {
            var e = start + (nint)(i * Stride);
            if (!IsCatalogEntry(e)) break;
            _reader.TryReadStruct<int>(e, out var id);
            var obj = Ptr(e + 0x08);
            var idStr = Ptr(e + 0x10);
            var code = _reader.ReadStringUtf16(idStr, 64);
            list.Add(new MapType(id, code, Prettify(code), Classify(code), (long)obj, (long)idStr));
        }
        return list;
    }

    // ── current region ────────────────────────────────────────────────────────

    /// <summary>Read the current-region map array: a 0x18-stride run of {record, archetype∈catalog,
    /// sharedConst}. Returns the longest such run. Bounded to a window around the catalog (the array +
    /// records live near it), so this stays fast enough to run on every read.</summary>
    private List<RegionMap> ReadRegion(Dictionary<nint, MapType> byParsed, nint catalogBase)
    {
        var winLo = (long)catalogBase - 0x1000_0000L; // ±256 MB around the catalog
        var winHi = (long)catalogBase + 0x1000_0000L;
        var best = new List<RegionMap>();
        var chunk = _regionBuffer;
        var overlap = Stride * 32;
        foreach (var (regionBase, regionSize) in _reader.Process.EnumerateReadableRegions(privateOnly: false))
        {
            if ((long)regionBase + regionSize <= winLo || (long)regionBase >= winHi) continue;
            long off = 0;
            while (off < regionSize)
            {
                var toRead = (int)Math.Min(chunk.Length, regionSize - off);
                var read = _reader.TryReadBytes(regionBase + (nint)off, chunk.AsSpan(0, toRead));
                if (read <= 0) break;
                for (var i = 0; i + Stride <= read; i += 8)
                {
                    var rec = (nint)BitConverter.ToInt64(chunk, i);
                    var arch = (nint)BitConverter.ToInt64(chunk, i + 0x08);
                    var shared = (nint)BitConverter.ToInt64(chunk, i + 0x10);
                    if (!IsCanon(rec) || !IsCanon(shared) || !byParsed.ContainsKey(arch)) continue;
                    // Found a candidate entry — measure the run from here (entries share `shared`).
                    var run = new List<RegionMap>();
                    for (var e = regionBase + (nint)(off + i); run.Count < 20000; e += Stride)
                    {
                        var r = Ptr(e); var a = Ptr(e + 0x08); var s = Ptr(e + 0x10);
                        if (!IsCanon(r) || s != shared || !byParsed.TryGetValue(a, out var mt)) break;
                        run.Add(new RegionMap(mt.Code, mt.Name, mt.Kind, (long)r));
                    }
                    if (run.Count > best.Count) best = run;
                    // Skip past this run to avoid re-measuring its interior.
                    if (run.Count > 1) i += (run.Count - 1) * Stride;
                }
                if (read != toRead) break;
                if (toRead < chunk.Length) break;
                off += chunk.Length - overlap;
            }
        }
        return best.Count >= 8 ? best : new List<RegionMap>(); // require a real run, not a coincidence
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Derive a readable display name from the internal code: strip the "Map" prefix and common
    /// qualifiers, then space out CamelCase / underscores. "MapRustbowl"→"Rustbowl";
    /// "MapUberBoss_StoneCitadel"→"Stone Citadel"; "MapUniqueMerchant01_Oasis"→"Merchant Oasis". This is
    /// clearly DERIVED (the real localized display name isn't reliably adjacent in memory across builds).</summary>
    public static string Prettify(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        var s = code;
        if (s.StartsWith("Map", StringComparison.Ordinal)) s = s[3..];
        s = s.Replace("UberBoss_", "").Replace("PrecursorTower", "Tower ").Replace("Unique", "");
        // Drop a leading "MerchantNN_" style numeric qualifier inside merchant codes.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\D)\d{1,2}(?=_|$)", "");
        s = s.Replace("_", " ");
        // Space out CamelCase boundaries.
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))) && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
            sb.Append(c);
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static bool IsCanon(nint p) => (ulong)p >= 0x10000 && (ulong)p <= 0x7FFFFFFFFFFF;

    private nint Ptr(nint addr)
    {
        if (!_reader.TryReadStruct<nint>(addr, out var p)) return 0;
        return IsCanon(p) ? p : 0;
    }

    // ── Live node graph (atlas nodes are UiElements) ─────────────────────────────────────────────

    /// <summary>One live atlas node. <see cref="X"/>/<see cref="Y"/> are the element's RelativePos
    /// (canvas/screen-space units that the game updates live as you pan; project ×scale + origin to draw).</summary>
    public readonly record struct AtlasNodeLive(
        nint Element, uint Id, uint Content, byte State, byte Biome, byte Flags, byte Completion,
        float X, float Y, float W, float H, float Scale, bool Visible, int IconType,
        int GridX, int GridY, string MapName, string MapCode, IReadOnlyList<string> Tags,
        bool Accessible, bool Completed, string Kind, string MapType, string MapGroup, IReadOnlyList<string> MapDataTags)
    {
        /// <summary>The node's atlas grid coordinate (<see cref="Poe2Offsets.AtlasNode.GridPos"/>) — the
        /// key into the connection graph for routing (unique per node; stable while the atlas is open).</summary>
        public (int X, int Y) Grid => (GridX, GridY);
        public bool Unlocked => (Flags & 0x01) != 0;
        public bool Visited => (Flags & 0x02) != 0;
        public bool HasContent => Content != 0;   // +0x310 (atlas-row ptr) non-null ⇒ has rolled content
        // Accessible/Completed are decoded from the deeper node-data status byte (the upstream reference-validated
        // source — *(node+0x10)+0x20 +0x2CF, bit0 accessible / bit1 completed). Accessible ("you can run
        // this now") is the route SOURCE frontier; the element-flag Unlocked/Visited bits are kept separate.
    }

    private readonly object _nodeLock = new(); // ReadNodes is called from both the tick + API threads
    private nint _nodeVtable;    // cached atlas-node element class vtable
    private nint _nodeCanvas;    // cached parent container holding the node elements
    private int _nodeRetry;      // throttle re-detection when not located
    private int _hiddenTicks;    // counts ticks the cached canvas read as hidden (self-heal a stale cache)
    // Per-element resolved content tags (display names). Content is read via the +0x310 EndgameMapAtlas
    // row (validated live 2026-06-07): the headline content @ row+0x38 → contentRow+0x30 name, plus the
    // league mechanics from the stats list @ row+0x50 (stat ids "map_atlas_node_has_<mechanic>"). Cached
    // (content is stable while the atlas is open) and resolved at a bounded rate to avoid a tick hitch.
    private readonly Dictionary<nint, (string code, string map, string[] content, AtlasMapData.MapMeta? meta)> _tagCache = new();
    private readonly Dictionary<nint, int> _iconTypeCache = new();  // element → content-icon type (static per node)
    private static readonly string[] NoTags = Array.Empty<string>();

    // Atlas CONNECTION GRAPH (grid coord → neighbour grid coords), read from the canvas's edge vector
    // (Poe2.AtlasGraph.ConnectionsVec). Static while the atlas is open, so it's read once per canvas and
    // cached (rebuilt on Invalidate / canvas change). This is what enables node-to-node routing.
    private readonly Dictionary<(int, int), List<(int, int)>> _graph = new();
    private nint _graphCanvas;   // canvas the cached _graph was built from (0 = not built)
    // The current-location ("player icon") marker: the lone non-node element whose +0x300 → a node. Located
    // during DetectNodeClass (vtable-independent — see Poe2.AtlasGraph.CurrentMarkerNodePtr). *(marker+0x300)
    // is the node the player is currently in (the route's true start).
    private nint _currentMarker;
    // The most-recently resolved atlas panel UiElement address (0 = unresolved).
    // Updated inside AtlasPanelOpen whenever a candidate is chosen. Exposed for
    // the /api/probe/uielement diagnostic endpoint (B5a).
    private nint _atlasPanelAddr;

    /// <summary>Cheap "is the Atlas screen open?" check (the persistent panel's visible bit, ~4 reads) —
    /// the same gate <see cref="ReadNodes"/> uses internally, exposed so callers can tell a TRANSIENT empty
    /// read (atlas open, a node read just hiccupped) from the atlas genuinely being closed. Lets the overlay
    /// hold its last marks through a read miss instead of blanking (the off-screen-arrow flicker).</summary>
    public bool IsAtlasOpen(nint inGameState)
    {
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        return uiRoot != 0 && AtlasPanelOpen(uiRoot);
    }

    /// <summary>Read the live atlas node list. Atlas nodes are all children of one canvas container; we
    /// detect the node element-class + canvas once (BFS, vtable-grouped) and cache them, then each call
    /// just reads the canvas's children (cheap). Re-detects (throttled) if the cache goes stale or the
    /// atlas hasn't been opened yet. Returns empty when not in/near the Atlas.
    /// <para><paramref name="showContentIcons"/> gates the per-node sigil-icon child-walk;
    /// <paramref name="needNodeStatus"/> gates the accessible/completed DataStorage derefs. Pass
    /// <c>true, true</c> when full node data is required (API / F10); pass the overlay settings bools
    /// for the world-tick path so stealth-read savings are preserved.</para></summary>
    public List<AtlasNodeLive> ReadNodes(nint inGameState, bool showContentIcons, bool needNodeStatus)
    {
        var nodes = new List<AtlasNodeLive>();
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return nodes;

        lock (_nodeLock) // called from both the tick thread (BuildAtlasMarks) and the API thread (AtlasJson)
        {
            // Fast path: cached canvas. Cheap gate first — when the Atlas is CLOSED the canvas isn't
            // hierarchically visible, so we skip reading ~1100 nodes (this runs on the tick loop).
            if (_nodeCanvas != 0 && _nodeVtable != 0)
            {
                if (HierarchicallyVisible(_nodeCanvas))
                {
                    _hiddenTicks = 0;
                    if (ReadCanvasNodes(_nodeCanvas, nodes, showContentIcons, needNodeStatus)) return nodes;
                    // read failed → ReadCanvasNodes already Invalidated; fall through to re-detect.
                }
                else
                {
                    // Canvas hidden = atlas closed → normally a cheap return (no node read). BUT the game
                    // RECREATES the atlas panel on close/reopen, so the cached pointer can go stale and
                    // never recover (the old "restart the overlay to fix detection" bug). Guard against
                    // that: drop a cache that's no longer a live element immediately, and force a periodic
                    // re-detect as a self-heal. Otherwise cheap-return.
                    var liveSelf = Ptr(_nodeCanvas + Poe2.UiElement.Self) == _nodeCanvas;
                    if (liveSelf && ++_hiddenTicks % 150 != 0) return nodes;
                    Invalidate();
                }
            }

            // Cheap open-gate (the key cost saver). We only reach here with NO cached canvas — i.e. the
            // atlas has never been opened this session, or the cache self-healed. DetectNodeClass below
            // BFS-walks the entire (~50k-element) UI tree, and while the atlas is CLOSED it can never
            // succeed (the node elements aren't instantiated until first open), so without this gate it
            // would burn that whole-tree BFS on every retry — the entire time you're mapping. Instead,
            // gate on the atlas panel's visible bit (a persistent UiRoot child; ~4 reads). Closed → bail
            // cheaply. Fail-safe: any read failure reads as closed, so a drifted index degrades to
            // feature-off, never back to a per-tick BFS.
            if (!AtlasPanelOpen(uiRoot)) return nodes;

            // (Re)detect — throttled so even with the gate open we don't BFS 50k elements every tick.
            if (_nodeRetry++ % 30 != 0) return nodes;
            if (!DetectNodeClass(uiRoot)) return nodes;
            if (HierarchicallyVisible(_nodeCanvas)) ReadCanvasNodes(_nodeCanvas, nodes, showContentIcons, needNodeStatus);
            return nodes;
        }
    }

    /// <summary>Read the cached canvas's children, keeping those of the node class. Returns false (and
    /// invalidates the cache) if the canvas no longer looks right, forcing a re-detect.</summary>
    private bool ReadCanvasNodes(nint canvas, List<AtlasNodeLive> outNodes, bool showContentIcons, bool needNodeStatus)
    {
        var first = Ptr(canvas + Poe2.UiElement.Children);
        if (first == 0 || !_reader.TryReadStruct<nint>(canvas + Poe2.UiElement.ChildrenEnd, out var last)) { Invalidate(); return false; }
        var count = ((long)last - (long)first) / 8;
        if (count is <= 0 or > 20000) { Invalidate(); return false; }

        var matched = 0;
        var resolveBudget = 80;  // cap new content resolves per call → spread the first-read cost
        var allCached = true;    // false if any node was left unresolved this pass (budget spent)
        for (long i = 0; i < count; i++)
        {
            var el = Ptr(first + (nint)(i * 8));
            if (el == 0 || Ptr(el) != _nodeVtable) continue;     // vtable == node class
            matched++;
            _reader.TryReadStruct<uint>(el + Poe2.AtlasNode.MapNodeId, out var id);
            _reader.TryReadStruct<uint>(el + Poe2.AtlasNode.Content, out var content);
            _reader.TryReadStruct<byte>(el + Poe2.AtlasNode.State, out var state);
            _reader.TryReadStruct<byte>(el + Poe2.AtlasNode.Biome, out var biome);
            _reader.TryReadStruct<byte>(el + Poe2.AtlasNode.Flags, out var flags);
            _reader.TryReadStruct<byte>(el + Poe2.AtlasNode.Completion, out var compl);
            _reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos, out var x);
            _reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos + 4, out var y);
            _reader.TryReadStruct<float>(el + Poe2.UiElement.SizeW, out var w);
            _reader.TryReadStruct<float>(el + Poe2.UiElement.SizeH, out var h);
            _reader.TryReadStruct<float>(el + 0x130, out var scale);
            _reader.TryReadStruct<int>(el + Poe2.AtlasNode.GridPos, out var gridX);     // StdTuple2D<int> atlas grid coord
            _reader.TryReadStruct<int>(el + Poe2.AtlasNode.GridPos + 4, out var gridY); // → the routing graph key
            _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var uiFlags);
            var visible = ((uiFlags >> Poe2.UiElement.FlagVisibleBit) & 1) != 0;
            // The node's content/icon TYPE lives on a nested sigil-icon child (content int 1..~50);
            // walk first-children a few levels to find it. Lets us classify + match nodes to in-game icons.
            var iconType = 0;
            if (showContentIcons)
            {
                if (!_iconTypeCache.TryGetValue(el, out iconType))
                {
                    var d = el;
                    for (var lvl = 0; lvl < 5 && d != 0; lvl++)
                    {
                        if (_reader.TryReadStruct<uint>(d + Poe2.AtlasNode.Content, out var c) && c is > 0 and < 256) { iconType = (int)c; break; }
                        d = Ptr(Ptr(d + Poe2.UiElement.Children)); // first child = *(*(el+Children))
                    }
                    _iconTypeCache[el] = iconType;
                }
            }
            // Accessible/completed status: the upstream reference-validated deeper model
            // *(node+DataStorage)+DataModel → status byte +0x2CF (bit0 accessible, bit1 completed). This is
            // the route SOURCE frontier ("maps you can run right now"). Cheap (2 derefs + 1 byte).
            bool accessible = false, completed = false;
            if (needNodeStatus)
            {
                var storage = Ptr(el + Poe2.AtlasNode.DataStorage);
                if (storage != 0)
                {
                    var model = Ptr(storage + Poe2.AtlasNode.DataModel);
                    if (model != 0 && _reader.TryReadStruct<byte>(model + Poe2.AtlasNode.DataStatus, out var stb))
                    { accessible = (stb & 1) != 0; completed = (stb & 2) != 0; }
                }
            }
            // Resolved (cached): map name (all nodes) + rolled content tags (nodes with a +0x310 row).
            // Budget-limited per call so the first read doesn't hitch the tick; fills in over a few calls.
            if (!_tagCache.TryGetValue(el, out var resolved))
            {
                if (resolveBudget > 0) { resolved = ResolveTags(el); _tagCache[el] = resolved; resolveBudget--; }
                else { resolved = ("", "", NoTags, null); allCached = false; } // budget spent — retried next call (not cached)
            }
            var kind = Classify(resolved.code);   // map-archetype class (Citadel/Boss/Tower/Unique/Merchant/Normal) — first-class track target
            var meta = resolved.meta;
            outNodes.Add(new AtlasNodeLive(el, id, content, state, biome, flags, compl, x, y, w, h, scale, visible, iconType, gridX, gridY, resolved.map, resolved.code, resolved.content, accessible, completed, kind,
                meta is { } mm ? mm.Type : "normal",
                meta is { } mm2 ? mm2.Group : "",
                meta is { } mm3 ? mm3.Tags : System.Array.Empty<string>()));
        }
        if (matched < 8) { Invalidate(); return false; }          // canvas no longer the node container
        AllTagsResolved = allCached;   // true once every node's tags are cached (seed defaults only then)
        EnsureGraph();                 // (re)read the connection-edge vector once per canvas (cached)
        return true;
    }

    /// <summary>True once every visible node's tags have been resolved + cached (tag resolution is
    /// budget-limited per read, so it takes a few reads after opening the atlas). Lets callers seed
    /// defaults only when the full map/content set is available.</summary>
    public bool AllTagsResolved { get; private set; }

    private void Invalidate() { _nodeCanvas = 0; _nodeVtable = 0; _hiddenTicks = 0; _tagCache.Clear(); _iconTypeCache.Clear(); _graph.Clear(); _graphCanvas = 0; _currentMarker = 0; }

    /// <summary>The player's CURRENT atlas node grid coord (the "player icon" tile), via the marker element
    /// (<see cref="Poe2Offsets.AtlasGraph.CurrentMarkerNodePtr"/>): <c>*(marker+0x300)</c> → current node →
    /// its <see cref="Poe2Offsets.AtlasNode.GridPos"/>. Returns null when the marker isn't located or has
    /// gone stale (caller keeps its last-known start). Thread-safe; read each tick — it tracks the player as
    /// they run maps. The marker is found during <see cref="DetectNodeClass"/>.</summary>
    public (int X, int Y)? CurrentNodeGrid()
    {
        lock (_nodeLock)
        {
            var m = _currentMarker;
            if (m == 0 || Ptr(m + Poe2.UiElement.Self) != m) return null;   // not located / stale
            var node = Ptr(m + Poe2.AtlasGraph.CurrentMarkerNodePtr);
            if (node == 0) return null;
            if (!_reader.TryReadStruct<int>(node + Poe2.AtlasNode.GridPos, out var gx)) return null;
            if (!_reader.TryReadStruct<int>(node + Poe2.AtlasNode.GridPos + 4, out var gy)) return null;
            return (gx, gy);
        }
    }

    /// <summary>Read the canvas's connection-edge <see cref="StdVector"/> (<see cref="Poe2Offsets.AtlasGraph"/>)
    /// once per canvas and build the bidirectional adjacency by grid coord. Each 20-byte edge is
    /// <c>{ int unknown; StdTuple2D&lt;int&gt; source@+0x04; StdTuple2D&lt;int&gt; target@+0x0C }</c>. Bulk-reads
    /// the whole vector in one pass (cheap, ~300 edges). No-op when already built for this canvas. Caller
    /// holds <see cref="_nodeLock"/>.</summary>
    private void EnsureGraph()
    {
        if (_nodeCanvas == 0 || _graphCanvas == _nodeCanvas) return;
        _graph.Clear(); _graphCanvas = _nodeCanvas;
        var begin = Ptr(_nodeCanvas + Poe2.AtlasGraph.ConnectionsVec);
        if (begin == 0 || !_reader.TryReadStruct<nint>(_nodeCanvas + Poe2.AtlasGraph.ConnectionsVec + 8, out var end)) return;
        var bytes = (long)end - (long)begin;
        if (bytes <= 0 || bytes % Poe2.AtlasGraph.EdgeStride != 0) return;
        var count = (int)(bytes / Poe2.AtlasGraph.EdgeStride);
        if (count is <= 0 or > 200000) return;
        var buf = new byte[count * Poe2.AtlasGraph.EdgeStride];
        if (_reader.TryReadBytes(begin, buf) < buf.Length) return;
        for (var i = 0; i < count; i++)
        {
            var o = i * Poe2.AtlasGraph.EdgeStride;
            var sx = BitConverter.ToInt32(buf, o + Poe2.AtlasGraph.EdgeSourceOff);
            var sy = BitConverter.ToInt32(buf, o + Poe2.AtlasGraph.EdgeSourceOff + 4);
            var dx = BitConverter.ToInt32(buf, o + Poe2.AtlasGraph.EdgeTargetOff);
            var dy = BitConverter.ToInt32(buf, o + Poe2.AtlasGraph.EdgeTargetOff + 4);
            if (sx == dx && sy == dy) continue;
            AddEdge((sx, sy), (dx, dy));
            AddEdge((dx, dy), (sx, sy));
        }
    }

    private void AddEdge((int, int) a, (int, int) b)
    {
        if (!_graph.TryGetValue(a, out var list)) { list = new List<(int, int)>(4); _graph[a] = list; }
        if (!list.Contains(b)) list.Add(b);
    }

    /// <summary>A* over the atlas connection graph from <paramref name="start"/> to <paramref name="goal"/>
    /// (both grid coords). Returns the ordered grid-coord path (start … goal inclusive), or null when either
    /// endpoint is absent or the two aren't connected. Cost + heuristic are Euclidean grid distance, so the
    /// result is the fewest-hops / shortest route through the unlocked node mesh. Thread-safe (snapshots the
    /// graph under <see cref="_nodeLock"/>); safe to call from the tick thread alongside ReadNodes.</summary>
    /// <summary>Number of nodes in the cached connection graph (0 ⇒ not built / atlas closed). Diagnostic.</summary>
    public int GraphNodeCount { get { lock (_nodeLock) return _graph.Count; } }

    /// <summary>True if the given grid coord is a vertex in the connection graph (has ≥1 edge). Diagnostic —
    /// a node with no edges can't be a route endpoint.</summary>
    public bool GraphHas((int, int) grid) { lock (_nodeLock) return _graph.ContainsKey(grid); }

    public List<(int X, int Y)>? FindPath((int X, int Y) start, (int X, int Y) goal)
    {
        Dictionary<(int, int), List<(int, int)>> g;
        lock (_nodeLock)
        {
            if (!_graph.ContainsKey(start) || !_graph.ContainsKey(goal)) return null;
            // Snapshot so the search doesn't race a concurrent EnsureGraph rebuild.
            g = new Dictionary<(int, int), List<(int, int)>>(_graph);
        }
        if (start == goal) return new List<(int X, int Y)> { start };

        static float Dist((int X, int Y) a, (int X, int Y) b)
        { float dx = a.X - b.X, dy = a.Y - b.Y; return MathF.Sqrt(dx * dx + dy * dy); }

        var cameFrom = new Dictionary<(int, int), (int, int)>();
        var gScore = new Dictionary<(int, int), float> { [start] = 0f };
        var open = new PriorityQueue<(int, int), float>();   // lazy PQ: stale entries are filtered via gScore
        open.Enqueue(start, Dist(start, goal));

        while (open.Count > 0)
        {
            var cur = open.Dequeue();
            if (cur == goal)
            {
                var path = new List<(int X, int Y)> { cur };
                while (cameFrom.TryGetValue(cur, out var prev)) { cur = prev; path.Add(cur); }
                path.Reverse();
                return path;
            }
            if (!g.TryGetValue(cur, out var neighbours)) continue;
            var baseG = gScore[cur];
            foreach (var nb in neighbours)
            {
                var tentative = baseG + Dist(cur, nb);
                if (gScore.TryGetValue(nb, out var old) && tentative >= old) continue;
                cameFrom[nb] = cur;
                gScore[nb] = tentative;
                open.Enqueue(nb, tentative + Dist(nb, goal));
            }
        }
        return null;
    }

    // Per-node content BADGES: node[0][0] children, each child's +0x300 → "[Code|Display]" UTF-16 string.
    // Prior-art reads this at child+0x290; on OUR build it's child+0x300 (validated
    // live 2026-06-20 via the F10 discovery dump: n00[0]+0x300='[DeadlyMapBoss|Deadly Map Boss]'). This badge
    // list carries the boss TIER ("Deadly Map Boss") that the +0x310 headline row collapses to the generic
    // "Powerful Map Boss" — so it's what makes Deadly/Twinned/etc. trackable + navigable.
    private const int BadgeContentStr = 0x300;

    /// <summary>Read + parse a node's content-badge display names (no lock — caller holds <see cref="_nodeLock"/>,
    /// e.g. ResolveTags via ReadCanvasNodes). Returns the display portion of each "[Code|Display]" badge.</summary>
    private List<string> BadgeContentsNoLock(nint el)
    {
        var result = new List<string>();
        var n0 = Ptr(Ptr(el + Poe2.UiElement.Children));           // node[0]
        if (n0 == 0) return result;
        var n00 = Ptr(Ptr(n0 + Poe2.UiElement.Children));          // node[0][0] = content-badge container
        if (n00 == 0) return result;
        var begin = Ptr(n00 + Poe2.UiElement.Children);
        if (begin == 0 || !_reader.TryReadStruct<nint>(n00 + Poe2.UiElement.ChildrenEnd, out var end)) return result;
        var count = ((long)end - (long)begin) / 8;
        if (count is <= 0 or > 64) return result;
        for (long i = 0; i < count; i++)
        {
            var child = Ptr(begin + (nint)(i * 8));
            if (child == 0) continue;
            var sp = Ptr(child + BadgeContentStr);                  // badge child → content-name ptr
            if (sp == 0) continue;
            var name = ParseBadgeName(_reader.ReadStringUtf16(sp, 96));
            if (name.Length > 0 && !result.Contains(name)) result.Add(name);
        }
        return result;
    }

    /// <summary>Public, lock-guarded variant of <see cref="BadgeContentsNoLock"/> for external callers (F10).</summary>
    public List<string> ReadContentBadges(nint el)
    {
        if (el == 0) return new List<string>();
        lock (_nodeLock) return BadgeContentsNoLock(el);
    }

    /// <summary>"[DeadlyMapBoss|Deadly Map Boss]" → "Deadly Map Boss" (display part after '|'); a bare
    /// "[Code]" → "Code". Returns "" for anything that doesn't look like a real name.</summary>
    private static string ParseBadgeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        int lb = s.IndexOf('['), rb = s.LastIndexOf(']');
        if (lb >= 0 && rb > lb) s = s.Substring(lb + 1, rb - lb - 1);
        int pipe = s.IndexOf('|');
        if (pipe >= 0) s = s[(pipe + 1)..];
        s = s.Trim();
        return LooksLikeName(s) ? s : "";
    }

    /// <summary>DIAGNOSTIC: describe a node's child-tree + scan each content-badge child's offset window
    /// (0x280..0x300) for UTF-16 strings, so we can LOCATE where per-node content (incl. the boss-tier
    /// "Deadly Map Boss" badge) lives on our build — prior-art path (node[0][0] children,
    /// +0x290) returned nothing here, so this finds the real layout instead of guessing. Printed by F10.</summary>
    public string DescribeNodeContent(nint el)
    {
        if (el == 0) return "(null element)";
        var sb = new System.Text.StringBuilder();
        lock (_nodeLock)
        {
            int Count(nint e)
            {
                var b = Ptr(e + Poe2.UiElement.Children);
                if (b == 0 || !_reader.TryReadStruct<nint>(e + Poe2.UiElement.ChildrenEnd, out var en)) return -1;
                var c = ((long)en - (long)b) / 8;
                return c is >= 0 and <= 100000 ? (int)c : -1;
            }
            nint Child(nint e, int i)
            {
                var b = Ptr(e + Poe2.UiElement.Children);
                return b == 0 ? 0 : Ptr(b + (nint)(i * 8));
            }
            void ScanStrings(nint e, string lbl)
            {
                for (var off = 0x280; off <= 0x310; off += 8)
                {
                    var p = Ptr(e + off);
                    if (p == 0) continue;
                    var s = _reader.ReadStringUtf16(p, 48);
                    if (LooksLikeName(s)) sb.Append($" {lbl}+0x{off:X}='{s}'");
                }
            }

            var n0 = Child(el, 0);
            var n00 = n0 != 0 ? Child(n0, 0) : 0;
            sb.Append($"tree: node.ch={Count(el)} n0.ch={(n0 != 0 ? Count(n0) : -1)} n00.ch={(n00 != 0 ? Count(n00) : -1)}");
            // Scan the node itself + the two child levels we navigate.
            ScanStrings(el, "node");
            if (n0 != 0) ScanStrings(n0, "n0");
            if (n00 != 0)
            {
                var cnt = Count(n00);
                for (var i = 0; i < cnt && i < 8; i++)
                {
                    var ch = Child(n00, i);
                    if (ch == 0) continue;
                    sb.Append($"\n   n00[{i}]:");
                    ScanStrings(ch, "");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Multi-source shortest-hop routing over the connection graph: one BFS seeded from every
    /// <paramref name="sources"/> node, then the fewest-hops path from the nearest source reconstructed for
    /// each goal. Returns goal→path (source…goal inclusive) for the REACHABLE goals only. This is the
    /// "route from where I am (or the accessible frontier) to each tracked tile" primitive — far cheaper
    /// than one A* per goal, and it naturally picks the closest entry point. Thread-safe (snapshots the
    /// graph under <see cref="_nodeLock"/>); safe to call from the world thread alongside ReadNodes.</summary>
    public Dictionary<(int X, int Y), List<(int X, int Y)>> RoutesFromSources(
        IReadOnlyCollection<(int, int)> sources, IReadOnlyCollection<(int, int)> goals)
    {
        var result = new Dictionary<(int X, int Y), List<(int X, int Y)>>();
        if (sources.Count == 0 || goals.Count == 0) return result;

        Dictionary<(int, int), List<(int, int)>> g;
        lock (_nodeLock)
        {
            if (_graph.Count == 0) return result;
            g = new Dictionary<(int, int), List<(int, int)>>(_graph);   // snapshot — don't race EnsureGraph
        }

        var srcSet = new HashSet<(int, int)>();
        var cameFrom = new Dictionary<(int, int), (int, int)>();
        var visited = new HashSet<(int, int)>();
        var queue = new Queue<(int, int)>();
        foreach (var s in sources)
            if (g.ContainsKey(s) && srcSet.Add(s) && visited.Add(s)) queue.Enqueue(s);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!g.TryGetValue(cur, out var nbrs)) continue;
            foreach (var nb in nbrs)
            {
                if (!visited.Add(nb)) continue;
                cameFrom[nb] = cur;
                queue.Enqueue(nb);
            }
        }

        foreach (var goal in goals)
        {
            if (!g.ContainsKey(goal)) continue;
            if (srcSet.Contains(goal)) { result[goal] = new List<(int X, int Y)> { goal }; continue; }
            if (!cameFrom.ContainsKey(goal)) continue;
            var path = new List<(int X, int Y)> { goal };
            var cur = goal;
            while (cameFrom.TryGetValue(cur, out var prev)) { cur = prev; path.Add(cur); }
            path.Reverse();
            result[goal] = path;
        }
        return result;
    }

    /// <summary>Resolve a node's content TAGS (display names) via its EndgameMapAtlas row (+0x310):
    /// the headline content (row+0x38 → content row +0x30 name, e.g. "Powerful Map Boss") plus the league
    /// mechanics harvested from the stats sub-struct (row+0x50): stat ids "map_atlas_node_has_&lt;x&gt;"
    /// → "X" (Breach, Delirium, …). Validated live 2026-06-07; re-confirm offsets via Research --atlas-resolve.</summary>
    private (string code, string map, string[] content, AtlasMapData.MapMeta? meta) ResolveTags(nint el)
    {
        // Map NAME: node +0x300 → EndgameMaps row; its +0x00 → the WorldAreas row, which holds
        // {+0x00 → Id "MapXxx",  +0x08 → the LOCALIZED display name}. We now read +0x08 (the game's real
        // name, e.g. "Savannah"/"Digsite"/"Precursor Tower") instead of Prettify()-guessing the code —
        // Prettify mismatched the in-game name for some maps, which broke web-UI filters. Validated live
        // 2026-06-16 (Research --atlas-mapname). Prettify(code) stays only as a fallback. The raw code is
        // returned too (stable, never localized) so the F10 inspector / dashboard can show it.
        string code = "", name = "";
        var mapRow = Ptr(el + 0x300);
        if (mapRow != 0)
        {
            var w = Ptr(mapRow);
            var direct = w != 0 ? _reader.ReadStringUtf16(w, 64) : "";
            if (direct.StartsWith("Map", StringComparison.Ordinal))
            {
                code = direct;                                  // legacy layout: +0x300 row → code string directly
            }
            else if (w != 0)
            {
                var idP = Ptr(w);                               // WorldAreas +0x00 → Id "MapXxx"
                code = idP != 0 ? _reader.ReadStringUtf16(idP, 64) : "";
                var nmP = Ptr(w + Poe2.AtlasMapRow.WorldAreaName); // WorldAreas +0x08 → localized name
                name = nmP != 0 ? _reader.ReadStringUtf16(nmP, 64) : "";
            }
        }
        var map = (LooksLikeName(name) && name.Length <= 48) ? name.Trim()
                : (code.StartsWith("Map", StringComparison.Ordinal) ? Prettify(code) : "");

        var tags = new List<string>(4);

        // Rolled content lives on the EndgameMapAtlas row at +0x310 (null ⇒ no headline/mechanic content,
        // but the node may still carry CONTENT BADGES — read below regardless).
        var row = Ptr(el + 0x310);
        if (row != 0)
        {
            // Headline content: row+0x38 → content row; +0x30 is a pointer to the (NUL-terminated UTF-16)
            // display name, e.g. "Powerful Map Boss" / "Trialmaster's Trainee".
            var contentRow = Ptr(row + 0x38);
            if (contentRow != 0)
            {
                var np = Ptr(contentRow + 0x30);
                var nm = np != 0 ? _reader.ReadStringUtf16(np, 64) : "";
                if (LooksLikeName(nm)) tags.Add(nm.Trim());
            }

            // League mechanics: row+0x50 → stats sub-struct; harvest "map_atlas_node_has_<mechanic>" stat ids.
            var stats = Ptr(row + 0x50);
            if (stats != 0)
            {
                Span<byte> sb = stackalloc byte[0x100];
                var n = _reader.TryReadBytes(stats, sb);
                for (var o = 0; o + 8 <= n; o += 8)
                {
                    var p = (nint)System.BitConverter.ToInt64(sb[o..]);
                    if (!IsCanon(p)) continue;
                    const string pre = "map_atlas_node_has_";
                    // The stat id (ASCII) lives at the pointer, or one deref in.
                    var s = ReadAscii(p, 64);
                    if (!s.StartsWith(pre, StringComparison.Ordinal)) { var pp = Ptr(p); s = pp == 0 ? "" : ReadAscii(pp, 64); }
                    if (s.StartsWith(pre, StringComparison.Ordinal))
                    {
                        var mech = TitleCase(s[pre.Length..].Replace('_', ' '));
                        if (mech.Length > 0 && !tags.Contains(mech)) tags.Add(mech);
                    }
                }
            }
        }

        // CONTENT BADGES (node[0][0] children, +0x300 → "[Code|Display]"): the boss-TIER badge "Deadly Map
        // Boss" and any other per-node content the headline collapses. Always read (a node can have a badge
        // with a null +0x310 row), de-duped against the headline/mechanic tags above.
        foreach (var bc in BadgeContentsNoLock(el))
            if (!tags.Contains(bc)) tags.Add(bc);

        AtlasMapData.MapMeta? meta = AtlasMapData.Shared.TryGet(code, out var mm) ? mm : (AtlasMapData.MapMeta?)null;
        return (code, map, tags.Count == 0 ? NoTags : tags.ToArray(), meta);
    }

    /// <summary>Read a NUL/garbage-terminated ASCII run at <paramref name="addr"/> (stat ids are ASCII).</summary>
    private string ReadAscii(nint addr, int max)
    {
        Span<byte> b = stackalloc byte[max];
        var n = _reader.TryReadBytes(addr, b);
        var sb = new System.Text.StringBuilder(n);
        for (var i = 0; i < n; i++) { var c = b[i]; if (c is >= 0x20 and < 0x7f) sb.Append((char)c); else break; }
        return sb.ToString();
    }

    private static bool LooksLikeName(string s) => s.Length is >= 3 and <= 64 && s[0] is >= ' ' and < (char)0x7f;

    /// <summary>Title-case each space-separated word ("breach" → "Breach", "boss unique" → "Boss Unique").</summary>
    private static string TitleCase(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return string.Join(' ', parts);
    }

    /// <summary>Cheap "is the Atlas screen open?" gate used to avoid the whole-tree node-class BFS while
    /// the atlas is closed. The atlas panel is a persistent UiRoot child at a fixed index
    /// (<see cref="Poe2.AtlasPanel.UiRootChildIndex"/>) whose visible bit toggles with the panel — so this
    /// is ~4 reads. Validates the indexed element is a real UiElement (Self==self) first; returns false on
    /// any read failure (fail-safe: a drifted index degrades to feature-off, not a per-tick BFS).
    /// <para>v0.41.4 defensive fallback (widened from v0.41.3's ±6/exact-18 attempt): scans first 60
    /// UiRoot children and accepts child count in [8, 30] as the atlas panel signature. Cached
    /// index is preferred as fast path. Populates <see cref="LastProbe"/> so operators can see
    /// what was tried via <c>/api/atlas</c>.</para></summary>
    private int _lastFoundAtlasChildIndex = -1;
    // v0.41.7: widened from 60 to 200 to cover UiRoot's actual child count (~124 observed in controller
    // mode field reports where the atlas panel may sit past index 60). Bounded by actual child count
    // at runtime so we never over-scan a smaller list.
    private const int ScanWidthMax = 200;
    private const int SigMinChildren = 8;
    private const int SigMaxChildren = 30;

    /// <summary>Debug info exposed for <c>/api/atlas</c> to diagnose child-index drift in the field.</summary>
    public record struct AtlasProbeInfo(
        int PrimaryIndex,
        int CachedIndex,
        int ChosenIndex,
        bool ChosenVisible,
        int[] CandidateChildCounts,
        // v0.41.5: raw pointer diagnostic for cases where the scan never runs (uiRoot == 0 or
        // UiRoot's Children offset itself has drifted so the vector begin/end reads back 0).
        // Values are hex strings so the payload is readable when pasted.
        string UiRootAddr,
        string ChildrenBeginAddr,
        string ChildrenEndAddr,
        int    ChildrenOffsetHex,
        int    ChildrenEndOffsetHex,
        // ProbeAtOffsets sweeps common candidate offsets for Children begin — if the raw offset
        // 0x10 reads back 0, one of these might be the new location post-patch. Format:
        // "0x10=0x7ffe1234 (18 slots)" etc.
        string[] ProbeAtOffsets,
        // v0.41.7: every UiRoot child whose child count falls in the [8, 30] signature window
        // AND whose visible bit reads TRUE right now. Users hitting /api/atlas twice (atlas open +
        // atlas closed) can diff which index's visible flag flips — that's the actual atlas panel
        // for their UI mode. Format: "index=N childCount=CC visible=BOOL". Controller mode field
        // reports revealed the primary index 22 always reads visible=false; the true atlas panel
        // may sit at a different index only reachable via this diff.
        string[] SignatureMatchingCandidates,
        // v0.41.7: total number of UiRoot direct children (was implicit — surfaced so payload
        // readers see "we only scanned 60 of 124" or similar and can request wider scan).
        int TotalUiRootChildren);

    public AtlasProbeInfo LastProbe { get; private set; } =
        new(Poe2.AtlasPanel.UiRootChildIndex, -1, -1, false, Array.Empty<int>(),
            "0x0", "0x0", "0x0", Poe2.UiElement.Children, Poe2.UiElement.ChildrenEnd, Array.Empty<string>(),
            Array.Empty<string>(), 0);

    private bool AtlasPanelOpen(nint uiRoot)
    {
        // v0.41.5 diagnostic: always populate LastProbe with the raw pointer info before any early
        // return, so field reports show whether we short-circuited on uiRoot==0 vs first==0.
        var first = uiRoot == 0 ? 0 : Ptr(uiRoot + Poe2.UiElement.Children);
        var last  = uiRoot == 0 ? 0 : Ptr(uiRoot + Poe2.UiElement.ChildrenEnd);
        var probeSweep = uiRoot == 0 ? Array.Empty<string>() : SweepChildrenCandidateOffsets(uiRoot);

        if (uiRoot == 0 || first == 0)
        {
            LastProbe = new AtlasProbeInfo(
                Poe2.AtlasPanel.UiRootChildIndex, _lastFoundAtlasChildIndex, -1, false,
                Array.Empty<int>(),
                $"0x{uiRoot:X}", $"0x{first:X}", $"0x{last:X}",
                Poe2.UiElement.Children, Poe2.UiElement.ChildrenEnd,
                probeSweep, Array.Empty<string>(), 0);
            return false;
        }

        // v0.41.7: bound scan by ACTUAL child count, not a static cap. UiRoot has ~124 children
        // in normal cases; controller mode field report confirmed the atlas panel may sit past
        // index 60 where our old scan gave up.
        var totalChildren = last > first ? (int)((last - first) / 8) : 0;
        var scanWidth = Math.Min(ScanWidthMax, Math.Max(60, totalChildren));

        var primary = Poe2.AtlasPanel.UiRootChildIndex;
        var cached  = _lastFoundAtlasChildIndex;
        int chosen  = -1;
        bool chosenVis = false;

        // v0.41.9 fix: v0.41.8's "prefer visible=true among [8, 30]-child signature matches" wasn't
        // specific enough — controller-mode payload showed indices 17 (9 children, visible), 19 (9,
        // visible), 22 (18, invisible), and 97 (18, visible). Sorted by distance-from-primary=22,
        // the v0.41.8 code hit index 19 (9 children, visible=true) before index 97, cached the wrong
        // index, kept reporting "atlas closed."
        //
        // v0.41.9 uses a tier-preference: EXACT 18-child count (the true atlas panel signature —
        // both historical index 22 AND controller index 97 have exactly 18) beats the looser [8, 30]
        // range. Within each tier, prefer visible=true, then closer-to-primary. Loose range remains
        // as final fallback for future patches that might shift the child count by 1-2.
        //
        //   Tier 1: childCount == 18 AND visible=true → strongest match
        //   Tier 2: childCount == 18 (any visibility) → historical signature match
        //   Tier 3: childCount in [8, 30] AND visible=true → loose fallback with visibility
        //   Tier 4: childCount in [8, 30] (any visibility) → last resort

        if (cached >= 0 && TryReadPanelVisibleBit(first, cached, out var visC, out var sigC) && sigC)
        {
            chosen = cached; chosenVis = visC;
        }
        else
        {
            (int idx, bool vis)? tier1 = null; // exact 18 + visible
            (int idx, bool vis)? tier2 = null; // exact 18
            (int idx, bool vis)? tier3 = null; // [8,30] + visible
            (int idx, bool vis)? tier4 = null; // [8,30]

            var ordered = new List<int>();
            for (int i = 0; i < scanWidth; i++) ordered.Add(i);
            ordered.Sort((a, b) => Math.Abs(a - primary) - Math.Abs(b - primary));

            foreach (var candidate in ordered)
            {
                if (!TryReadPanelVisibleBit(first, candidate, out var vis, out var sig) || !sig) continue;
                var childCount = ReadElementChildCount(first, candidate);
                if (childCount == Poe2.AtlasPanel.ExpectedChildCount)
                {
                    if (vis) tier1 ??= (candidate, vis);
                    tier2 ??= (candidate, vis);
                    if (tier1 is not null) break; // best possible tier found, stop
                }
                else
                {
                    if (vis) tier3 ??= (candidate, vis);
                    tier4 ??= (candidate, vis);
                }
            }

            var pick = tier1 ?? tier2 ?? tier3 ?? tier4;
            if (pick is { } p)
            {
                _lastFoundAtlasChildIndex = p.idx;
                chosen = p.idx;
                chosenVis = p.vis;
                _atlasPanelAddr = Ptr(first + (nint)(p.idx * 8));
            }
        }

        // Build diagnostic snapshot (child counts of all scanned indices) for /api/atlas.
        var counts = new int[scanWidth];
        var sigMatches = new List<string>();
        for (int i = 0; i < scanWidth; i++)
        {
            var p = Ptr(first + (nint)(i * 8));
            if (p == 0 || Ptr(p + Poe2.UiElement.Self) != p) { counts[i] = -1; continue; }
            var b = Ptr(p + Poe2.UiElement.Children);
            var e = Ptr(p + Poe2.UiElement.ChildrenEnd);
            var count = (b == 0 || e == 0) ? -1 : (int)((e - b) / 8);
            counts[i] = count;
            // v0.41.7: record every signature-window match + its current visible bit so field
            // reports can identify which index's visibility flips between atlas-open and atlas-closed
            // hits — that's the true atlas panel for the user's UI mode.
            if (count >= SigMinChildren && count <= SigMaxChildren)
            {
                var visBit = _reader.TryReadStruct<uint>(p + Poe2.UiElement.Flags, out var fl)
                    ? ((fl >> Poe2.UiElement.FlagVisibleBit) & 1) != 0
                    : false;
                sigMatches.Add($"index={i} childCount={count} visible={(visBit ? "true" : "false")}");
            }
        }
        LastProbe = new AtlasProbeInfo(
            primary, _lastFoundAtlasChildIndex, chosen, chosenVis, counts,
            $"0x{uiRoot:X}", $"0x{first:X}", $"0x{last:X}",
            Poe2.UiElement.Children, Poe2.UiElement.ChildrenEnd,
            probeSweep, sigMatches.ToArray(), totalChildren);

        return chosen >= 0 && chosenVis;
    }

    /// <summary>v0.41.5 field-diagnostic sweep: read <c>*(uiRoot + off)</c> for a range of plausible
    /// StdVector-begin offsets and report which ones look non-null + how many 8-byte pointer slots
    /// exist between begin and the presumed end (off+8). If the game patch shifted UiElement.Children
    /// away from the current 0x10, the shifted offset will show a non-null result with a reasonable
    /// child count — LO can then update Poe2Offsets to match.</summary>
    private string[] SweepChildrenCandidateOffsets(nint uiRoot)
    {
        // Common shift amounts from patch drift: +/-0x08, +/-0x10, and offsets nearby the current 0x10.
        int[] candidateOffsets = { 0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48 };
        var results = new List<string>(candidateOffsets.Length);
        foreach (var off in candidateOffsets)
        {
            var b = Ptr(uiRoot + off);
            var e = Ptr(uiRoot + off + 8);
            if (b == 0)
            {
                results.Add($"0x{off:X2}=null");
                continue;
            }
            var slots = (e == 0 || e < b) ? -1 : (int)((e - b) / 8);
            results.Add($"0x{off:X2}=0x{b:X} ({slots} slots)");
        }
        return results.ToArray();
    }

    /// <summary>v0.41.9: read the child count of a candidate panel without touching visibility/signature
    /// state. Used by the tier selector to distinguish exact-18 matches from looser [8, 30] matches.</summary>
    private int ReadElementChildCount(nint firstChildPtr, int index)
    {
        var panel = Ptr(firstChildPtr + (nint)(index * 8));
        if (panel == 0 || Ptr(panel + Poe2.UiElement.Self) != panel) return -1;
        var childBegin = Ptr(panel + Poe2.UiElement.Children);
        var childEnd   = Ptr(panel + Poe2.UiElement.ChildrenEnd);
        if (childBegin == 0 || childEnd == 0) return -1;
        return (int)((childEnd - childBegin) / 8);
    }

    private bool TryReadPanelVisibleBit(nint firstChildPtr, int index, out bool visible, out bool matchesSignature)
    {
        visible = false; matchesSignature = false;
        var panel = Ptr(firstChildPtr + (nint)(index * 8));
        if (panel == 0 || Ptr(panel + Poe2.UiElement.Self) != panel) return false;

        // Loosened signature (v0.41.4): child count in [SigMinChildren, SigMaxChildren] instead of
        // an exact match against ExpectedChildCount. The 2026-07-16 patch appears to have changed the
        // atlas panel's child structure too (not just its parent index), so the exact-18 gate from
        // v0.41.3 failed even when neighbor scanning found the panel.
        var childBegin = Ptr(panel + Poe2.UiElement.Children);
        var childEnd   = Ptr(panel + Poe2.UiElement.ChildrenEnd);
        if (childBegin == 0 || childEnd == 0) return false;
        var childCount = (int)((childEnd - childBegin) / 8);
        matchesSignature = childCount >= SigMinChildren && childCount <= SigMaxChildren;

        if (!_reader.TryReadStruct<uint>(panel + Poe2.UiElement.Flags, out var fl)) return false;
        visible = ((fl >> Poe2.UiElement.FlagVisibleBit) & 1) != 0;
        return true;
    }

    /// <summary>True iff the element and all ancestors (via Parent +0xB8) have the local visible bit set
    /// — i.e. actually shown. Cheap (~6 reads); used to detect "the Atlas screen is open".</summary>
    private bool HierarchicallyVisible(nint el)
    {
        var cur = el; var guard = 0;
        while (cur != 0 && guard++ < 16)
        {
            if (!_reader.TryReadStruct<uint>(cur + Poe2.UiElement.Flags, out var fl)) return false;
            if (((fl >> Poe2.UiElement.FlagVisibleBit) & 1) == 0) return false;
            var par = Ptr(cur + Poe2.UiElement.Parent);
            if (par == cur) break;
            cur = par;
        }
        return true;
    }

    /// <summary>BFS the UI tree; the atlas-node class is the vtable whose instances spread across many
    /// distinct biome values (0..12) — generic elements are all biome 0. Cache that vtable + the nodes'
    /// common parent (the canvas container).</summary>
    private bool DetectNodeClass(nint uiRoot)
    {
        var root = Ptr(uiRoot + Poe2.UiElement.Parent) is var tr && tr != 0 ? tr : uiRoot;
        var queue = new Queue<nint>(); queue.Enqueue(root);
        var visited = new HashSet<nint>();
        var byVtable = new Dictionary<nint, List<nint>>();
        while (queue.Count > 0 && visited.Count < 200000)
        {
            var el = queue.Dequeue();
            if (el == 0 || !visited.Add(el) || Ptr(el + Poe2.UiElement.Self) != el) continue;
            var vt = Ptr(el);
            if (vt != 0) (byVtable.TryGetValue(vt, out var l) ? l : byVtable[vt] = new()).Add(el);
            var first = Ptr(el + Poe2.UiElement.Children);
            if (first != 0 && _reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last))
            {
                var n = ((long)last - (long)first) / 8;
                if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) queue.Enqueue(Ptr(first + (nint)(k * 8)));
            }
        }
        // Score each candidate vtable on THREE signals so a stray biome-ish UI class can't win (a
        // biome-spread-only pick mis-detected a 18×18 list element → the overlay read 0 nodes): the real
        // atlas-node class is ~40×40, has biome spread ≥3, AND the most instances.
        nint bestVt = 0; var bestCount = 0; var bestBiomes = 0;
        nint fbVt = 0; var fbBiomes = 0;   // fallback: max biome-spread, in case sizes drift
        foreach (var (vt, list) in byVtable)
        {
            if (list.Count < 50) continue;
            var biomes = new HashSet<int>(); var widths = new Dictionary<int, int>();
            foreach (var el in list.Take(400))
            {
                if (_reader.TryReadStruct<byte>(el + Poe2.AtlasNode.Biome, out var b) && b is >= 1 and <= 12) biomes.Add(b);
                if (_reader.TryReadStruct<float>(el + Poe2.UiElement.SizeW, out var w)) { var iw = (int)w; widths[iw] = widths.GetValueOrDefault(iw) + 1; }
            }
            if (biomes.Count > fbBiomes) { fbBiomes = biomes.Count; fbVt = vt; }
            var modalW = widths.Count == 0 ? 0 : widths.OrderByDescending(k => k.Value).First().Key;
            if (modalW is >= 28 and <= 56 && biomes.Count >= 3 && list.Count > bestCount) { bestCount = list.Count; bestVt = vt; bestBiomes = biomes.Count; }
        }
        if (bestVt == 0) { bestVt = fbVt; bestBiomes = fbBiomes; }   // no ~40×40 class — fall back
        if (bestVt == 0 || bestBiomes < 3) return false;
        _nodeVtable = bestVt;
        // The node-class elements also appear OUTSIDE the atlas (terrain props / minimap), so the
        // first one's parent isn't necessarily the node canvas. The real atlas canvas is the parent
        // that holds the MOST node-class children — pick that (443 nodes vs a few terrain props).
        var parentCount = new Dictionary<nint, int>();
        foreach (var el in byVtable[bestVt])
        {
            var p = Ptr(el + Poe2.UiElement.Parent);
            if (p != 0) parentCount[p] = parentCount.GetValueOrDefault(p) + 1;
        }
        if (parentCount.Count == 0) return false;
        _nodeCanvas = parentCount.OrderByDescending(k => k.Value).First().Key;

        // Current-location marker: the lone NON-node element whose +0x300 points at a node-class element
        // (*(marker+0x300) = the node the player is currently in). Structural, so no vtable to drift. The BFS
        // above already visited every element (grouped in byVtable); scan them once.
        _currentMarker = 0;
        var nodeSet = new HashSet<nint>(byVtable[bestVt]);
        foreach (var el in byVtable.Values.SelectMany(v => v))
        {
            if (nodeSet.Contains(el)) continue;
            var p = Ptr(el + Poe2.AtlasGraph.CurrentMarkerNodePtr);
            if (p != 0 && nodeSet.Contains(p)) { _currentMarker = el; break; }
        }

        return _nodeCanvas != 0;
    }

    /// <summary>
    /// Returns the address of the most-recently resolved atlas panel UiElement,
    /// or 0 when the panel has never been found this session / the atlas is closed.
    /// Updated inside <see cref="AtlasPanelOpen"/> whenever a candidate is chosen.
    /// Thread-safe (only written under <see cref="_lock"/>; reads are lock-free but
    /// a stale read is harmless — address is either valid or 0).
    /// </summary>
    public nint AtlasPanelAddr => _atlasPanelAddr;

    /// <summary>
    /// Returns the address of the first atlas node UiElement under the cached node canvas,
    /// or 0 when the canvas is not yet detected / the atlas is closed. Used by the B3a
    /// /api/probe/atlas-graph diagnostic endpoint to feed AtlasGraphProber sweeps.
    /// Thread-safe (uses <see cref="_nodeLock"/>).
    /// </summary>
    public nint FirstNodeAddr
    {
        get
        {
            lock (_nodeLock)
            {
                if (_nodeCanvas == 0) return 0;
                var first = Ptr(_nodeCanvas + Poe2.UiElement.Children);
                if (first == 0) return 0;
                if (!_reader.TryReadStruct<nint>(_nodeCanvas + Poe2.UiElement.ChildrenEnd, out var last))
                    return 0;
                var count = (long)(last - first) / 8;
                if (count <= 0 || count > 20000) return 0;
                return Ptr(first);
            }
        }
    }
}
