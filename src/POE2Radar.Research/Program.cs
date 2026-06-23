using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Research;

// POE2Radar.Research — dev-time offset discovery / validation harness.
//
// There is no POEMCP-style oracle for PoE2, so validation here is manual + value-scan based:
//   --hp <N> [--mana <N>]   value-scan for the Life component, then back-walk to IngameData
//                           and dump the resolved chain so offsets can be checked by hand.
//   --dump <hexAddr> [len]  hex-dump a memory region (default 256 bytes) for manual inspection.
//   --aob                   scan for IngameState via the committed AOB patterns (if any).
//
// As PoE2 offsets get discovered, build this out into a per-patch sweep (see CLAUDE.md).

Console.WriteLine("POE2Radar.Research");
Console.WriteLine("==================");

// Offline generator (no live game): build the meta-derived starter weight table from a vendored Tincture
// meta-detail.json snapshot. Dispatched here, ABOVE AttachToPoE, so it runs without PoE2 running.
if (HasFlag(args, "--gen-weights"))
    return RunGenWeights(TryGetStrArg(args, "--meta"), TryGetStrArg(args, "--out"));

using var process = ProcessHandle.AttachToPoE();
if (process is null)
{
    Console.Error.WriteLine("PoE2 not running (no matching process found).");
    return 1;
}
Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");
Console.WriteLine($"Main module base: 0x{process.MainModuleBase:X16}  size: 0x{process.MainModuleSize:X}");
var reader = new MemoryReader(process);

if (HasFlag(args, "--aob"))
    return RunAobScan(process, reader);

if (HasFlag(args, "--chain"))
    return RunChainProbe(process, reader);

if (HasFlag(args, "--vitals"))
    return RunVitals(process, reader);

if (HasFlag(args, "--find-entities"))
    return RunFindEntities(process, reader, TryGetIntArg(args, "--window") ?? 0x4000);

if (HasFlag(args, "--find-terrain"))
    return RunFindTerrain(process, reader, TryGetIntArg(args, "--window") ?? 0x2000);

if (HasFlag(args, "--find-map"))
    return RunFindMap(process, reader);

if (HasFlag(args, "--watch-expedition"))
    return RunWatchExpedition(process, reader);

if (HasFlag(args, "--ritual"))
    return RunRitual(process, reader, HasFlag(args, "--watch"));

if (HasFlag(args, "--tribute"))
    return RunTribute(process, reader, TryGetStrArg(args, "--find"));

if (HasFlag(args, "--tribute-scan"))
    return RunTributeScan(process, reader, TryGetStrArg(args, "--costs") ?? "1590", TryGetIntArg(args, "--cost") ?? 1590);

if (HasFlag(args, "--tribute-tiles"))
    return RunTributeTiles(process, reader, TryGetStrArg(args, "--costs") ?? "1755,1590,1395,1230,174");

if (HasFlag(args, "--tribute-hover"))
    return RunTributeHover(process, reader);

if (HasFlag(args, "--ritual-rewards"))
    return RunRitualRewards(process, reader, TryGetStrArg(args, "--reward") ?? "Venopuncture");

if (HasFlag(args, "--tooltip-capture"))
    return RunTooltipCapture(process, reader);

if (HasFlag(args, "--ritual-shop"))
    return RunRitualShop(process, reader);

if (TryGetHexArg(args, "--eldump") is { } elAddr)
    return RunElDump(reader, elAddr, TryGetIntArg(args, "--span") ?? 0x600);

if (TryGetStrArg(args, "--findwstr") is { } needleW)
    return RunFindWStr(process, reader, needleW);

if (TryGetHexArg(args, "--subtree") is { } subRoot)
    return RunSubtree(process, reader, subRoot, TryGetIntArg(args, "--up") ?? 8, TryGetIntArg(args, "--down") ?? 6);

if (HasFlag(args, "--rune-dump"))
    return RunRuneDump(process, reader, TryGetIntArg(args, "--radius") ?? 120);

if (HasFlag(args, "--monolith"))
    return RunMonolith(process, reader);

if (HasFlag(args, "--runeforge"))
    return RunRuneforge(process, reader);

if (HasFlag(args, "--lootvec"))
    return RunLootVec(process, reader);

if (HasFlag(args, "--lootcursor"))
    return RunLootCursor(process, reader);

if (TryGetStrArg(args, "--lootstruct") is { } lootName)
    return RunLootStruct(process, reader, lootName);

if (HasFlag(args, "--lootmap"))
    return RunLootMap(process, reader);

if (TryGetStrArg(args, "--lootwatch") is { } lootWatchName)
    return RunLootWatch(process, reader, lootWatchName);

if (HasFlag(args, "--watch"))
    return RunWatch(process, reader);

if (TryGetStrArg(args, "--tile-find") is { } tileNeedle)
    return RunTileFind(process, reader, tileNeedle);

if (HasFlag(args, "--tiles"))
    return RunTiles(process, reader);

if (HasFlag(args, "--rarity"))
    return RunRarity(process, reader);

if (HasFlag(args, "--mods"))
    return RunMods(process, reader, TryGetIntArg(args, "--min") ?? 1, TryGetIntArg(args, "--max") ?? 12);

if (HasFlag(args, "--item"))
    return RunItem(process, reader, TryGetIntArg(args, "--max") ?? 6);

if (HasFlag(args, "--inventory"))
    return RunInventory(process, reader, TryGetIntArg(args, "--inv") ?? -1, HasFlag(args, "--itemmods"));

if (TryGetHexArg(args, "--itemdump") is { } itemAddr)
    return RunItemDump(reader, itemAddr);

if (HasFlag(args, "--groundlabels"))
    return RunGroundLabels(process, reader, TryGetIntArg(args, "--delay") ?? 0);

if (HasFlag(args, "--labelmove"))
    return RunLabelMove(process, reader, TryGetIntArg(args, "--secs") ?? 6);

if (HasFlag(args, "--validate"))
    return RunValidate(process, reader, TryGetIntArg(args, "--n") ?? 6);

if (HasFlag(args, "--info"))
    return RunInfo(process, reader);

if (HasFlag(args, "--presence"))
    return RunPresence(process, reader, HasFlag(args, "--diff"));

if (HasFlag(args, "--devtree"))
    return RunDevTree(process, reader, TryGetIntArg(args, "--port") ?? 7778);

if (HasFlag(args, "--camera"))
    return RunCamera(process, reader);

if (TryGetHexArg(args, "--serverdata-vec") is { } vecOff)
    return RunServerDataVec(process, reader, (int)vecOff);

if (HasFlag(args, "--serverdata-diff"))
    return RunServerDataDiff(process, reader);

if (HasFlag(args, "--serverdata"))
    return RunServerData(process, reader);

if (HasFlag(args, "--pagesnap"))
    return RunPageSnap(reader, TryGetStrArg(args, "--tag") ?? "atlas",
        TryGetHexArg(args, "--lo") ?? unchecked((nint)0x040100000000L), TryGetHexArg(args, "--hi") ?? unchecked((nint)0x040400000000L));

if (HasFlag(args, "--pagediff"))
    return RunPageDiff(reader, TryGetStrArg(args, "--tag") ?? "atlas",
        TryGetHexArg(args, "--lo") ?? unchecked((nint)0x040180000000L), TryGetHexArg(args, "--hi") ?? unchecked((nint)0x040190000000L),
        TryGetStrArg(args, "--save"), TryGetStrArg(args, "--exclude"), TryGetStrArg(args, "--only"));

if (HasFlag(args, "--atlas-live"))
{
    // Exercise the real Core reader (dynamic locator, no hardcoded addresses) — what the overlay/API use.
    var (_, _, aiAnchor, _) = ResolveChain(process, reader);
    Console.WriteLine($"anchor (AreaInstance) = 0x{aiAnchor:X}");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var atlas = new Poe2Atlas(reader);
    var data = atlas.Read(aiAnchor);                 // kicks off the background scan
    while (!data.Located && data.Note.Contains("Scanning") && sw.Elapsed.TotalSeconds < 180)
    { Thread.Sleep(1000); Console.Write($"\r  {data.Note}  ({sw.Elapsed.TotalSeconds:F0}s)   "); data = atlas.Read(aiAnchor); }
    Console.WriteLine();
    sw.Stop();
    Console.WriteLine($"Poe2Atlas.Read() in {sw.ElapsedMilliseconds} ms: located={data.Located} catalog@0x{data.CatalogAddr:X} count={data.CatalogCount} region={data.Region.Count}  note='{data.Note}'");
    foreach (var m in data.Catalog.Take(8)) Console.WriteLine($"  [{m.Id,3}] {m.Code,-28} name='{m.Name}'  parsed=0x{m.ParsedObj:X}");
    if (data.Catalog.Count > 8) Console.WriteLine($"  … (+{data.Catalog.Count - 8} more)");
    Console.WriteLine("  region maps: " + string.Join(", ", data.Region.Take(20).Select(r => r.Name.Length > 0 ? r.Name : r.Code)) + (data.Region.Count > 20 ? " …" : ""));
    return 0;
}

if (HasFlag(args, "--atlas-catalog"))
    return RunAtlasCatalog(reader, TryGetHexArg(args, "--seed") ?? unchecked((nint)0x00000401883378C0L));

if (HasFlag(args, "--atlas-nodes"))
    return RunAtlasNodes(reader, TryGetHexArg(args, "--seed") ?? unchecked((nint)0x0000040180282200L),
        TryGetHexArg(args, "--catalog") ?? unchecked((nint)0x00000401883378C0L));

if (HasFlag(args, "--atlas-fields"))
    return RunAtlasFields(process, reader, TryGetStrArg(args, "--code") ?? "Marrow");

if (HasFlag(args, "--atlas-nodes2"))
    return RunAtlasNodes2(process, reader);

if (HasFlag(args, "--atlas-canvas"))
    return RunAtlasCanvas(process, reader, TryGetHexArg(args, "--vt") ?? 0);

if (HasFlag(args, "--atlas-hoverflag"))
    return RunAtlasHoverFlag(process, reader, TryGetHexArg(args, "--vt") ?? 0);

if (HasFlag(args, "--atlas-anyhover"))
    return RunAtlasAnyHover(process, reader);

if (HasFlag(args, "--atlas-findpos"))
    return RunAtlasFindPos(process, reader);

if (HasFlag(args, "--atlas-corr"))
    return RunAtlasCorr(process, reader, HasFlag(args, "--solve"), HasFlag(args, "--reset"));

if (HasFlag(args, "--atlas-nodefilter"))
{
    var (_, igsf, _, _) = ResolveChain(process, reader);
    var uiRootf = SafePtr(reader, igsf + 0x2F0);
    var rootf = SafePtr(reader, uiRootf + 0xB8) is var trf && trf != 0 ? trf : uiRootf;
    // BFS, group by vtable, pick 0CECA0-like (biome) class, find the canvas = parent with most such children.
    var q = new Queue<nint>(); q.Enqueue(rootf); var seen = new HashSet<nint>(); var byVt = new Dictionary<nint, List<nint>>();
    while (q.Count > 0 && seen.Count < 200000) { var e = q.Dequeue(); if (e == 0 || !seen.Add(e) || SafePtr(reader, e + 8) != e) continue;
        var vt = SafePtr(reader, e); if (vt != 0) (byVt.TryGetValue(vt, out var l) ? l : byVt[vt] = new()).Add(e);
        var f0 = SafePtr(reader, e + 0x10); if (f0 != 0 && reader.TryReadStruct<nint>(e + 0x18, out var l0)) { var n = ((long)l0 - (long)f0) / 8; if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) q.Enqueue(SafePtr(reader, f0 + (nint)(k * 8))); } }
    nint nv = 0; var bb = 0; foreach (var (vt, l) in byVt) { if (l.Count < 50) continue; var bs = new HashSet<int>(); foreach (var e in l.Take(400)) if (reader.TryReadStruct<byte>(e + 0x32E, out var b) && b is >= 1 and <= 12) bs.Add(b); if (bs.Count > bb && l.Count > 200) { bb = bs.Count; nv = vt; } }
    // canvas = the parent holding the most nv-class children.
    var parentCount = new Dictionary<nint, int>();
    foreach (var e in byVt[nv]) { var p = SafePtr(reader, e + 0xB8); if (p != 0) parentCount[p] = parentCount.GetValueOrDefault(p) + 1; }
    var canvas = parentCount.OrderByDescending(k => k.Value).First();
    Console.WriteLine($"node class 0x{nv:X}; canvas 0x{canvas.Key:X} holds {canvas.Value} of them.");
    // Bucket canvas's nv children by hasChildren; report counts + samples.
    int withChild = 0, without = 0; var exWith = new List<string>(); var exWithout = new List<string>();
    var cf = SafePtr(reader, canvas.Key + 0x10); reader.TryReadStruct<nint>(canvas.Key + 0x18, out var cl);
    var cn = cf == 0 ? 0 : ((long)cl - (long)cf) / 8;
    for (long i = 0; i < cn; i++) { var ch = SafePtr(reader, cf + (nint)(i * 8)); if (ch == 0 || SafePtr(reader, ch) != nv) continue;
        var chf = SafePtr(reader, ch + 0x10); long ccc = 0; if (chf != 0 && reader.TryReadStruct<nint>(ch + 0x18, out var chl)) ccc = ((long)chl - (long)chf) / 8;
        reader.TryReadStruct<uint>(ch + 0x300, out var id); reader.TryReadStruct<byte>(ch + 0x32E, out var bm); reader.TryReadStruct<float>(ch + 0x118, out var px); reader.TryReadStruct<float>(ch + 0x11C, out var py);
        // peek a content-icon descendant's content (child→child→0D0680.content).
        uint deepContent = 0; var d = chf;
        for (var lvl = 0; lvl < 3 && d != 0; lvl++) { reader.TryReadStruct<uint>(d + 0x310, out deepContent); if (deepContent is > 0 and < 256) break; d = SafePtr(reader, d + 0x10) is var dd && dd != 0 ? SafePtr(reader, dd) : 0; }
        var line = $"id={id} biome={bm} children={ccc} deepContent={deepContent} pos=({px:F0},{py:F0})";
        if (ccc > 0) { withChild++; if (exWith.Count < 6) exWith.Add(line); } else { without++; if (exWithout.Count < 6) exWithout.Add(line); }
    }
    Console.WriteLine($"canvas {nv:X} children: {withChild} WITH children (nodes?), {without} WITHOUT (terrain?)");
    Console.WriteLine("-- WITH children --"); exWith.ForEach(s => Console.WriteLine("   " + s));
    Console.WriteLine("-- WITHOUT children --"); exWithout.ForEach(s => Console.WriteLine("   " + s));
    return 0;
}

if (TryGetHexArg(args, "--atlas-up") is { } upEl)
{
    Console.WriteLine($"ancestor chain from 0x{upEl:X} (via Parent +0xB8):");
    var cur = upEl; var guard = 0;
    while (cur != 0 && guard++ < 20)
    {
        var vt = SafePtr(reader, cur);
        reader.TryReadStruct<uint>(cur + 0x300, out var id);
        reader.TryReadStruct<uint>(cur + 0x310, out var content);
        reader.TryReadStruct<byte>(cur + 0x32E, out var biome);
        reader.TryReadStruct<float>(cur + 0x118, out var px); reader.TryReadStruct<float>(cur + 0x11C, out var py);
        reader.TryReadStruct<float>(cur + 0x288, out var sw); reader.TryReadStruct<float>(cur + 0x28C, out var sh);
        var first = SafePtr(reader, cur + 0x10); long cc = 0;
        if (first != 0 && reader.TryReadStruct<nint>(cur + 0x18, out var last)) cc = ((long)last - (long)first) / 8;
        Console.WriteLine($"  0x{cur:X} vt=0x{vt:X} children={cc} id={id} content={content} biome={biome} pos=({px:F0},{py:F0}) size=({sw:F0}x{sh:F0})");
        var par = SafePtr(reader, cur + 0xB8); if (par == cur) break; cur = par;
    }
    return 0;
}

if (HasFlag(args, "--atlas-watch"))
    return RunAtlasWatch(process, reader);

if (HasFlag(args, "--atlas-xform"))
    return RunAtlasXform(process, reader);

if (HasFlag(args, "--atlas-probe"))
    return RunAtlasProbe(process, reader);

if (HasFlag(args, "--atlas-content"))
    return RunAtlasContent(process, reader);

if (HasFlag(args, "--atlas-resolve"))
    return RunAtlasResolve(process, reader);

if (HasFlag(args, "--atlas-graph"))
    return RunAtlasGraph(process, reader);

if (HasFlag(args, "--atlas-mapname"))
    return RunAtlasMapName(process, reader, TryGetIntArg(args, "--max") ?? 12);

if (HasFlag(args, "--atlas-current"))
    return RunAtlasCurrent(process, reader);

if (HasFlag(args, "--atlas-findcur"))
    return RunAtlasFindCur(process, reader);

if (HasFlag(args, "--atlas-marker"))
    return RunAtlasMarker(process, reader);

if (HasFlag(args, "--atlas-readnodes"))
{
    var (_, igs2, _, _) = ResolveChain(process, reader);
    if (igs2 == 0) { Console.Error.WriteLine("no chain."); return 1; }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var atlas = new POE2Radar.Core.Game.Poe2Atlas(reader);
    var nodes = atlas.ReadNodes(igs2);   // first call may BFS-detect
    if (nodes.Count == 0) { Thread.Sleep(200); nodes = atlas.ReadNodes(igs2); }
    var t1 = sw.ElapsedMilliseconds;
    var n2 = atlas.ReadNodes(igs2);      // cached fast path
    Console.WriteLine($"ReadNodes: {nodes.Count} nodes (first {t1}ms, cached {sw.ElapsedMilliseconds - t1}ms). " +
        $"visible={n2.Count(n => n.Visible)} hasContent={n2.Count(n => n.HasContent)} unvisited={n2.Count(n => !n.Visited)} unlocked={n2.Count(n => n.Unlocked)}");
    Console.WriteLine("biome histogram: " + string.Join(" ", n2.GroupBy(n => n.Biome).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
    Console.WriteLine("sample (visible, hasContent or unvisited):");
    foreach (var n in n2.Where(n => n.Visible && (n.HasContent || !n.Visited)).Take(16))
        Console.WriteLine($"  id={n.Id,-9} biome={n.Biome,-2} content={n.Content,-6} flags=0x{n.Flags:X2}(unlk={(n.Unlocked ? 1 : 0)} vis={(n.Visited ? 1 : 0)}) compl={n.Completion} pos=({n.X:F0},{n.Y:F0}) size=({n.W:F0}x{n.H:F0}) scale={n.Scale:G4}");
    return 0;
}

if (HasFlag(args, "--hover"))
    return RunHover(process, reader);

if (HasFlag(args, "--atlas-ui"))
    return RunAtlasUi(process, reader, TryGetStrArg(args, "--text") ?? "Steppe");

if (TryGetStrArg(args, "--scan-string") is { } scanNeedle)
    return RunScanString(reader, scanNeedle, HasFlag(args, "--utf8"), HasFlag(args, "--all-regions"),
        HasFlag(args, "--refs"), TryGetIntArg(args, "--max") ?? 40);

if (TryGetHexArg(args, "--find-range") is { } rangeLo)
    return RunFindRange(reader, rangeLo, TryGetIntArg(args, "--range-len") ?? 0x40, TryGetIntArg(args, "--max") ?? 200);

if (TryGetHexArg(args, "--find") is { } needle)
    return RunFindPointer(reader, needle, TryGetHexArg(args, "--near"), TryGetIntArg(args, "--window") ?? 0x2000,
        HasFlag(args, "--all-regions"), TryGetIntArg(args, "--align") ?? 8);

if (TryGetHexArg(args, "--dump") is { } dumpAddr)
    return RunDump(reader, dumpAddr, TryGetIntArg(args, "--dump-len") ?? 256);

if (TryGetHexArg(args, "--entity") is { } entAddr)
    return RunEntityProbe(reader, entAddr);

if (TryGetIntArg(args, "--hp") is { } hp)
    return RunValueScan(reader, hp, TryGetIntArg(args, "--mana"));

Console.WriteLine();
Console.WriteLine("No mode specified. Options:");
Console.WriteLine("  --hp <N> [--mana <N>]      value-scan for the player Life component");
Console.WriteLine("  --dump <hexAddr> [--dump-len <N>]   hex-dump a region for inspection");
Console.WriteLine("  --dump <hexAddr> [--dump-len <N>]   hex-dump a region for inspection");
Console.WriteLine("  --entity <hexAddr>         walk a PoE2 entity: id, metadata path, component map, Render→grid, Life");
Console.WriteLine("  --rune-dump [--radius N]   dump nearby entities (path/components/MinimapIcon/small-ints) + tile paths near you");
Console.WriteLine("  --presence [--diff]        baseline (then --diff) player components to find the presence-radius float");
Console.WriteLine("  --devtree [--port N]       browser-based live memory/UI/entity explorer (default port 7778)");
Console.WriteLine("  --serverdata               dump ServerData (AreaInstance+0x580): strings + StdVector quest-list candidates");
Console.WriteLine("  --aob                      scan for IngameState via AOB patterns");
return 0;

// ── ServerData probe — locate the quest-state container ────────────────────
// AreaInstance+0x580 is the PlayerInfo/LocalPlayerStruct base (+0x00 ServerDataPtr,
// +0x20 LocalPlayerPtr). LocalPlayer @ AreaInstance+0x5A0 (= base+0x20) is validated, so the
// +0x580 deref is the ServerData object. In PoE1 GameHelper, ServerData holds the quest-states
// list (among league/guild/passives). This surfaces ServerData's strings (to confirm identity)
// and its StdVector-shaped fields (quest-list candidates), and writes the raw region to a temp
// file so two runs (before/after advancing a quest) can be byte-diffed to pinpoint quest flags.
//
// FINDINGS (2026-05-31, PAUSED mid-decode — resume here):
//   • ServerData = *(AreaInstance+0x580) CONFIRMED — its +0x20 equals the validated LocalPlayer
//     (AreaInstance+0x5A0), so the PlayerInfo shape holds.
//   • Clean before/after-quest diffs (delta 0, no zone change) RULED OUT two volatile candidates:
//     +0x22D0 (an int that drifts up AND down between reads) and the +0x23C8 StdVector (reallocates
//     constantly; grew 27→204 across a zone — content/area-dependent, NOT a stable quest list).
//   • LEAD: a block-structured region (vectors repeat every ~0x238 from ~+0x3030). Completing
//     "Trail of Corruption" flipped 16 dwords in +0x3434..+0x3B48 from 0 → 0xB4000000
//     (stable→sentinel = quest-state-like). "Lost Lute" only churned the volatile fields, so the
//     per-quest field mapping is NOT pinned yet, and 0xB4000000's meaning is unknown.
//   • NEXT: (1) control diff with NO quest action to confirm +0x34xx is quest-only; (2) decode
//     several quests to map field→quest + the sentinel semantics; (3) curate quest→objective-area
//     to drive auto-nav. Tools: --serverdata (baseline), --serverdata-diff, --serverdata-vec <off>.
static int RunServerData(ProcessHandle process, MemoryReader reader)
{
    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }

    var playerInfo = ai + 0x580;
    var serverData = SafePtr(reader, playerInfo);
    var localPlayer = SafePtr(reader, playerInfo + 0x20);
    var validatedPlayer = SafePtr(reader, ai + 0x5A0);
    Console.WriteLine($"AreaInstance 0x{ai:X}  PlayerInfo(+0x580) 0x{playerInfo:X}");
    Console.WriteLine($"  ServerDataPtr (+0x00) -> 0x{serverData:X}");
    Console.WriteLine($"  LocalPlayerPtr(+0x20) -> 0x{localPlayer:X}   (AreaInstance+0x5A0 = 0x{validatedPlayer:X}, {(localPlayer == validatedPlayer && localPlayer != 0 ? "MATCH" : "mismatch")})");
    if (serverData == 0) { Console.Error.WriteLine("ServerData null — wrong offset or not in game."); return 1; }

    const int scan = 0x4000;
    var buf = new byte[scan];
    var got = reader.TryReadBytes(serverData, buf);
    Console.WriteLine($"  read {got} bytes of ServerData @ 0x{serverData:X}");

    Console.WriteLine("\n--- ASCII-ish StdWString fields (+0x000..+0x1000) — expect league / character / guild ---");
    for (var off = 0; off <= 0x1000; off += 8)
    {
        var s = ReadStdWString(reader, serverData + off);
        if (!string.IsNullOrEmpty(s) && s.Length is >= 2 and <= 48 && s.All(c => c is >= (char)0x20 and < (char)0x7f))
            Console.WriteLine($"  +0x{off:X3}: \"{s}\"");
    }

    Console.WriteLine("\n--- StdVector-shaped fields (First<=Last<=Cap, plausible heap) — quest-list candidates ---");
    for (var off = 0; off + 24 <= got; off += 8)
    {
        var first = BitConverter.ToInt64(buf, off);
        var last  = BitConverter.ToInt64(buf, off + 8);
        var cap   = BitConverter.ToInt64(buf, off + 16);
        if (first <= 0x10000 || last < first || cap < last) continue;
        if ((ulong)first > 0x7FFFFFFFFFFF || (ulong)cap > 0x7FFFFFFFFFFF) continue;
        var span = last - first;
        if (span <= 0 || span > 0x80000) continue;
        Console.WriteLine($"  +0x{off:X3}: n8={span / 8} n16={span / 16} n24={span / 24} n40={span / 40}  (span 0x{span:X}) first=0x{first:X}");
    }

    var snap = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "poe2_serverdata.bin");
    try
    {
        // Prepend the 8-byte base address so the diff can be relocation-aware: ServerData moves
        // across zones, after which its internal pointers shift by a constant base delta we filter.
        using var fs = System.IO.File.Create(snap);
        fs.Write(BitConverter.GetBytes((long)serverData));
        fs.Write(buf, 0, Math.Min(got, scan));
        Console.WriteLine($"\nSnapshot written: {snap} (base 0x{serverData:X} + {Math.Min(got, scan)} bytes)");
    }
    catch (Exception ex) { Console.WriteLine($"\n(snapshot write failed: {ex.Message})"); }

    Console.WriteLine("Quest-flag hunt: run --serverdata (baseline), advance ONE quest step in-game,");
    Console.WriteLine("then run --serverdata-diff to print exactly which offsets changed.");
    return 0;
}

// ── Inspect a StdVector inside ServerData (e.g. the quest-states vector at +0x23C8) ──
// Walks the vector as 8-byte elements; for each element that's a heap pointer, dumps the target's
// first qwords and tries to read a string at the target + a few inner offsets, to surface quest
// id/name and the per-entry layout (so we can key auto-nav on quest state).
static int RunServerDataVec(ProcessHandle process, MemoryReader reader, int off)
{
    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    var sd = SafePtr(reader, ai + 0x580);
    if (sd == 0) { Console.Error.WriteLine("ServerData null."); return 1; }

    var vec = reader.ReadStruct<POE2Radar.Core.Game.StdVector>(sd + off);
    var span = (long)vec.Last - (long)vec.First;
    Console.WriteLine($"ServerData 0x{sd:X}  +0x{off:X}: First=0x{vec.First:X} Last=0x{vec.Last:X} span=0x{span:X}  (n8={span/8} n16={span/16} n24={span/24})");
    if (vec.First == 0 || span <= 0 || span > 0x80000) { Console.Error.WriteLine("implausible vector"); return 1; }

    var count = span / 8;
    for (long i = 0; i < Math.Min(count, 60); i++)
    {
        var p = reader.ReadPointer(vec.First + (nint)(i * 8));
        if (p <= 0x10000 || (ulong)p >= 0x7FFF_FFFFFFFF) { Console.WriteLine($"  [{i,2}] 0x{p:X}"); continue; }
        reader.TryReadStruct<int>(p + 0x18, out var s18);
        reader.TryReadStruct<int>(p + 0x20, out var s20);
        var def = reader.ReadPointer(p + 0x08); // quest definition (dat row) — should carry id/name
        Console.Write($"  [{i,2}] obj=0x{p:X} def=0x{def:X} s[+18]={s18} s[+20]={s20}");
        if (def > 0x10000 && (ulong)def < 0x7FFF_FFFFFFFF)
            foreach (var so in new[] { 0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30 })
            {
                var w = ReadStdWString(reader, def + so);
                if (Printable(w)) { Console.Write($"  +{so:X}=\"{w}\""); continue; }
                var pp = reader.ReadPointer(def + so);
                if (pp > 0x10000 && (ulong)pp < 0x7FFF_FFFFFFFF) { var u = reader.ReadStringUtf8(pp, 64); if (Printable(u)) Console.Write($"  +{so:X}->\"{u}\""); }
            }
        Console.WriteLine();
    }
    return 0;
}

static bool Printable(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length >= 3 && s.All(c => c >= ' ' && c < (char)0x7f);

// ── ServerData diff — compare current ServerData to the last --serverdata baseline ──
// ServerData is mostly static character/account data, so between two runs with NO quest change
// the diff should be ~empty. Advance one quest step and the changed dword(s) are the quest state.
static int RunServerDataDiff(ProcessHandle process, MemoryReader reader)
{
    var snap = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "poe2_serverdata.bin");
    if (!System.IO.File.Exists(snap)) { Console.Error.WriteLine("No baseline — run --serverdata first."); return 1; }
    var raw = System.IO.File.ReadAllBytes(snap);
    if (raw.Length < 16) { Console.Error.WriteLine("Baseline too small / stale — re-run --serverdata."); return 1; }
    var oldBase = BitConverter.ToInt64(raw, 0);
    var baseline = raw[8..];

    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    var serverData = SafePtr(reader, ai + 0x580);
    if (serverData == 0) { Console.Error.WriteLine("ServerData null."); return 1; }

    var cur = new byte[baseline.Length];
    var got = reader.TryReadBytes(serverData, cur);
    var n = Math.Min(got, baseline.Length);
    var delta = unchecked((uint)((long)serverData - oldBase)); // base relocation, low 32 bits
    Console.WriteLine($"ServerData base 0x{oldBase:X} -> 0x{serverData:X} (delta 0x{delta:X}); diffing {n} bytes");
    Console.WriteLine("(filtered: pointers shifted by the base delta, and pointer/float churn — small-int quest flags remain)");

    var changes = 0; var filtered = 0;
    for (var off = 0; off + 4 <= n; off += 4)
    {
        var b = BitConverter.ToUInt32(baseline, off);
        var c = BitConverter.ToUInt32(cur, off);
        if (b == c) continue;
        if (unchecked(c - b) == delta) { filtered++; continue; }                 // relocated internal pointer
        if (b >= 0x0010_0000u && c >= 0x0010_0000u) { filtered++; continue; }     // pointer/float churn, not a small flag
        Console.WriteLine($"  +0x{off:X4}: 0x{b:X8} -> 0x{c:X8}   ({(int)b} -> {(int)c})");
        changes++;
    }
    Console.WriteLine($"{changes} candidate changed dwords ({filtered} pointer/relocation changes filtered).");
    Console.WriteLine("For a clean read: flip ONE quest with minimal zoning between baseline and diff.");
    return 0;
}

// ── Player inventory + item-structure probe ────────────────────────────────
// Walks the GameHelper2 inventory chain (re-derived for our drifted build) and dumps every
// inventory + every item's identity (metadata, rarity, identified, art, stack) and mod ids.
//   AreaInstance +0x580 -> ServerData
//   ServerData   +0x48  -> StdVector PlayerServerData ; [0] -> ServerDataStructure
//   ServerDataStructure +0x320 -> StdVector PlayerInventories (InventoryArrayStruct, stride 0x18)
//     InventoryArrayStruct: +0x00 int Id, +0x08 ptr Inventory, +0x10 ptr(=+0x08 - 0x10)
//   InventoryStruct: +0x150 TotalBoxes(int x,int y), +0x170 StdVector ItemList(ptr InventoryItem), +0x1E8 int ReqCounter
//     InventoryItemStruct: +0x00 ptr Item(entity), +0x08 SlotStart(x,y), +0x10 SlotEnd(x,y)
// Self-validates the most drift-prone hops (PlayerInventories vec, ItemList vec) with brute fallbacks.
static int RunInventory(ProcessHandle process, MemoryReader reader, int onlyInv, bool dumpMods)
{
    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }

    var serverData = SafePtr(reader, ai + Poe2.AreaInstance.ServerDataPtr);
    var localPlayer = SafePtr(reader, ai + Poe2.AreaInstance.LocalPlayer);
    Console.WriteLine($"AreaInstance 0x{ai:X}  ServerData(+0x580) 0x{serverData:X}  LocalPlayer(+0x5A0) 0x{localPlayer:X}");
    if (serverData == 0) { Console.Error.WriteLine("ServerData null."); return 1; }

    // Step 1 — PlayerServerData vector @ ServerData+0x48; element [0] = the player's ServerDataStructure.
    var sdStruct = ResolveServerDataStruct(reader, serverData);
    if (sdStruct == 0) { Console.Error.WriteLine("Could not resolve ServerDataStructure (PlayerServerData vec)."); return 1; }
    Console.WriteLine($"ServerDataStructure 0x{sdStruct:X}");

    // Step 2 — PlayerInventories vector (InventoryArrayStruct, stride 0x18). Try +0x320, else brute.
    var (invVecOff, invVec, invCount) = FindPlayerInventoriesVec(reader, sdStruct, 0x320);
    if (invVecOff < 0) { Console.Error.WriteLine("Could not locate PlayerInventories vector in ServerDataStructure."); return 1; }
    Console.WriteLine($"PlayerInventories vec @ ServerDataStructure+0x{invVecOff:X}  ({invCount} entries)  First=0x{invVec.First:X}\n");

    // Step 3 — list every inventory, then dump items for the requested one(s).
    for (long i = 0; i < invCount; i++)
    {
        var rec = invVec.First + (nint)(i * 0x18);
        reader.TryReadStruct<int>(rec + 0x00, out var invId);
        var invPtr = SafePtr(reader, rec + 0x08);
        var invPtr1 = SafePtr(reader, rec + 0x10);
        if (invPtr == 0) continue;
        var name = InvName(invId);
        // Peek box count + item-list length without committing to the dump.
        var (boxX, boxY, itemListOff, itemVec, itemCount) = ProbeInventoryStruct(reader, invPtr);
        var flag = (invPtr1 == invPtr - 0x10) ? "" : "  (ptr1 invariant FAIL)";
        Console.WriteLine($"[{invId,3}] {name,-26} inv=0x{invPtr:X}  boxes={boxX}x{boxY}  items~{itemCount}{flag}");

        if (onlyInv >= 0 && invId != onlyInv) continue;
        if (onlyInv < 0 && itemCount <= 0) continue; // default: only dump non-empty inventories

        if (itemListOff < 0) { Console.WriteLine("      (could not locate ItemList vector)"); continue; }
        DumpInventoryItems(reader, invPtr, itemVec, itemCount, dumpMods);
    }
    Console.WriteLine("\nTip: --inventory --inv 1 dumps MainInventory only; add --mods for explicit/implicit mod ids.");
    return 0;
}

// ServerData+0x48 is a StdVector<IntPtr>; element [0] is the player's ServerDataStructure. If the
// offset drifted, brute-scan ServerData for a vector whose [0] target carries a valid inventory vec.
static nint ResolveServerDataStruct(MemoryReader reader, nint serverData)
{
    nint Try(int off)
    {
        if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(serverData + off, out var v)) return 0;
        var span = (long)v.Last - (long)v.First;
        if (v.First == 0 || span < 8 || span > 0x4000) return 0;
        var first = SafePtr(reader, v.First);
        if (first == 0) return 0;
        return FindPlayerInventoriesVec(reader, first, 0x320).off >= 0 ? first : 0;
    }
    var direct = Try(0x48);
    if (direct != 0) return direct;
    for (var off = 0x10; off <= 0x200; off += 8)
    {
        var hit = Try(off);
        if (hit != 0) { Console.WriteLine($"(PlayerServerData vec found at ServerData+0x{off:X}, not +0x48)"); return hit; }
    }
    return 0;
}

// Locate the PlayerInventories StdVector inside a candidate ServerDataStructure. Strong fingerprint:
// stride-0x18 records where InventoryId in 1..145, InventoryPtr0 is heap, InventoryPtr1 == Ptr0-0x10.
static (int off, POE2Radar.Core.Game.StdVector vec, int count) FindPlayerInventoriesVec(
    MemoryReader reader, nint sdStruct, int preferred)
{
    (int, POE2Radar.Core.Game.StdVector, int) Score(int off)
    {
        if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(sdStruct + off, out var v))
            return (-1, default, 0);
        var span = (long)v.Last - (long)v.First;
        if (v.First == 0 || span <= 0 || span % 0x18 != 0) return (-1, default, 0);
        var count = (int)(span / 0x18);
        if (count is <= 0 or > 400) return (-1, default, 0);
        var good = 0; var check = Math.Min(count, 32);
        for (var i = 0; i < check; i++)
        {
            var rec = v.First + (nint)(i * 0x18);
            if (!reader.TryReadStruct<int>(rec, out var id) || id < 1 || id > 200) continue;
            var p0 = SafePtr(reader, rec + 0x08);
            var p1 = SafePtr(reader, rec + 0x10);
            if (p0 != 0 && p1 == p0 - 0x10) good++;
        }
        return (good, v, count);
    }
    // Prefer the GH2 offset if it scores at all.
    var (pg, pv, pc) = Score(preferred);
    if (pg >= 1) return (preferred, pv, pc);
    var best = (-1, default(POE2Radar.Core.Game.StdVector), 0); var bestOff = -1;
    for (var off = 0x100; off <= 0x800; off += 8)
    {
        var (g, v, c) = Score(off);
        if (g > best.Item1) { best = (g, v, c); bestOff = off; }
    }
    return best.Item1 >= 2 ? (bestOff, best.Item2, best.Item3) : (-1, default, 0);
}

// Read TotalBoxes (+0x150) and ItemList vector (+0x170) from an InventoryStruct, with brute fallback
// for ItemList (a StdVector of 8-byte ptrs whose non-zero elements resolve to Metadata/Items entities).
static (int boxX, int boxY, int itemListOff, POE2Radar.Core.Game.StdVector vec, int count) ProbeInventoryStruct(
    MemoryReader reader, nint inv)
{
    reader.TryReadStruct<int>(inv + 0x150, out var boxX);
    reader.TryReadStruct<int>(inv + 0x154, out var boxY);
    if (boxX is < 0 or > 200) boxX = 0;
    if (boxY is < 0 or > 200) boxY = 0;

    int CountItemsLike(POE2Radar.Core.Game.StdVector v)
    {
        var span = (long)v.Last - (long)v.First;
        if (v.First == 0 || span <= 0 || span % 8 != 0 || span > 0x8000) return -1;
        var n = (int)(span / 8);
        var hits = 0; var seen = 0;
        for (var i = 0; i < Math.Min(n, 64) && seen < 12; i++)
        {
            var ii = SafePtr(reader, v.First + (nint)(i * 8));
            if (ii == 0) continue;
            seen++;
            var item = SafePtr(reader, ii + 0x00);
            if (item != 0 && ReadEntityMetadata(reader, item).StartsWith("Metadata/Items", StringComparison.Ordinal)) hits++;
        }
        return hits;
    }

    // Direct: ItemList @ +0x170.
    if (reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(inv + 0x170, out var direct))
    {
        var span = (long)direct.Last - (long)direct.First;
        if (direct.First != 0 && span > 0 && span % 8 == 0 && span <= 0x8000)
        {
            var n = (int)(span / 8);
            // Accept if box-count matches OR elements look like items.
            if ((boxX > 0 && boxY > 0 && n == boxX * boxY) || CountItemsLike(direct) >= 1)
                return (boxX, boxY, 0x170, direct, n);
        }
    }
    // Brute fallback.
    var bestOff = -1; var bestHits = 0; POE2Radar.Core.Game.StdVector bestVec = default; var bestN = 0;
    for (var off = 0x100; off <= 0x300; off += 8)
    {
        if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(inv + off, out var v)) continue;
        var h = CountItemsLike(v);
        if (h > bestHits) { bestHits = h; bestOff = off; bestVec = v; bestN = (int)(((long)v.Last - (long)v.First) / 8); }
    }
    return bestHits >= 1 ? (boxX, boxY, bestOff, bestVec, bestN) : (boxX, boxY, -1, default, 0);
}

static void DumpInventoryItems(MemoryReader reader, nint inv, POE2Radar.Core.Game.StdVector itemVec, int count, bool dumpMods)
{
    var seen = new HashSet<nint>();
    var slot = 0;
    for (var i = 0; i < count; i++)
    {
        var iiPtr = SafePtr(reader, itemVec.First + (nint)(i * 8));
        if (iiPtr == 0) continue;
        var item = SafePtr(reader, iiPtr + 0x00);
        if (item == 0 || !seen.Add(item)) continue; // de-dup multi-slot items
        reader.TryReadStruct<int>(iiPtr + 0x08, out var sx);
        reader.TryReadStruct<int>(iiPtr + 0x0C, out var sy);

        var meta = ReadEntityMetadata(reader, item);
        if (!meta.StartsWith("Metadata/Items", StringComparison.Ordinal)) continue;
        slot++;

        // Identity: rarity + identified (Mods component), art basename (RenderItem), stack count (Stack).
        var mods = ResolveComponentAddr(reader, item, "Mods");
        var rarity = -1; var identified = -1;
        if (mods != 0)
        {
            reader.TryReadStruct<int>(mods + Poe2.ModsComponent.Rarity, out rarity);
            reader.TryReadStruct<int>(mods + Poe2.ModsComponent.Identified, out identified);
        }
        var renderItem = ResolveComponentAddr(reader, item, "RenderItem");
        var art = renderItem == 0 ? "" : ItemArtBasename(reader.ReadStringUtf16(SafePtr(reader, renderItem + Poe2.RenderItemComponent.ResourcePath), 128)) ?? "";
        var stack = ResolveComponentAddr(reader, item, "Stack");
        var stackN = -1; if (stack != 0) reader.TryReadStruct<int>(stack + 0x18, out stackN);

        var rarStr = rarity switch { 0 => "Normal", 1 => "Magic", 2 => "Rare", 3 => "Unique", _ => $"?{rarity}" };
        var idStr = identified == 1 ? "ID" : identified == 0 ? "unID" : "id?";
        Console.WriteLine($"  #{slot,2} slot({sx},{sy})  {rarStr,-6} {idStr,-4} {(stackN > 0 ? $"x{stackN} " : "")}{art,-22} item=0x{item:X}");
        Console.WriteLine($"        meta: {meta}");
        Console.WriteLine($"        components: {string.Join(", ", ComponentNames(reader, item).OrderBy(s => s, StringComparer.Ordinal))}");

        if (dumpMods && mods != 0)
            foreach (var (kind, off) in new[] { ("implicit", 0xA0), ("explicit", 0xB8), ("enchant", 0xD0) })
            {
                var ids = ReadItemModIds(reader, mods + off);
                if (ids.Count > 0) Console.WriteLine($"        {kind}: {string.Join(" | ", ids)}");
            }
    }
    if (slot == 0) Console.WriteLine("      (no items resolved)");
}

// Read mod ids + rolled values from an AllModsType sub-vector, and render each to its English stat
// line(s) via ItemModTranslator. ModArrayStruct (stride 0x40): +0x00 Values StdVector<int>,
// +0x18 int Value0, +0x28 ModsPtr -> Mods.dat row (first qword -> UTF-16 internal mod id).
static List<string> ReadItemModIds(MemoryReader reader, nint vecAddr)
{
    var ids = new List<string>();
    if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(vecAddr, out var v)) return ids;
    var span = (long)v.Last - (long)v.First;
    if (v.First == 0 || span <= 0 || span % 0x40 != 0 || span > 0x800) return ids;
    var n = (int)(span / 0x40);
    for (var i = 0; i < n; i++)
    {
        var rec = v.First + (nint)(i * 0x40);
        var modsPtr = SafePtr(reader, rec + Poe2.ModsComponent.ModRecordPtr);
        if (modsPtr == 0) continue;
        var id = ReadModName(reader, modsPtr);
        if (string.IsNullOrEmpty(id)) continue;
        var vals = ReadModValueArray(reader, rec);
        var text = string.Join("; ", POE2Radar.Core.Game.ItemModTranslator.Shared.RenderMod(id, vals));
        ids.Add($"{id} [{string.Join(",", vals)}] → {text}");
    }
    return ids;
}

// The full ordered rolled-value array of one ModArrayStruct (Values StdVector<int> @ +0x00; falls back
// to Value0 @ +0x18 when the vector is empty). One value per stat the mod grants, in stat order.
static List<int> ReadModValueArray(MemoryReader reader, nint modArray)
{
    var outv = new List<int>();
    if (reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(modArray + 0x00, out var vv))
    {
        var span = (long)vv.Last - (long)vv.First;
        var count = vv.First == 0 || span <= 0 ? 0 : span / 4;
        for (long i = 0; i < Math.Min(count, 8); i++)
            if (reader.TryReadStruct<int>(vv.First + (nint)(i * 4), out var x)) outv.Add(x);
    }
    if (outv.Count == 0 && reader.TryReadStruct<int>(modArray + 0x18, out var v0)) outv.Add(v0);
    return outv;
}

// Internal mod id: Mods.dat row's first qword -> UTF-16 string.
static string ReadModName(MemoryReader reader, nint modsDatRow)
{
    var strPtr = SafePtr(reader, modsDatRow + Poe2.ModsComponent.ModRecordIdPtr);
    return strPtr == 0 ? "" : reader.ReadStringUtf16(strPtr, 80);
}

// ── Deep single-item dump: --itemdump <hexItemEntityAddr> ───────────────────────────────────
// For one item entity (address from --inventory output): metadata, every component, the Mods
// rarity/identified + each affix's id/values + a Mods.dat ROW dump (string-pointer scan for the
// affix display name + stat-key strings), and the Sockets component contents (socketed runes/gems).
// True for a plausible human-readable base-type name (≥3 printable ASCII chars, starts alpha).
static bool LooksPrintable(string? s)
{
    if (string.IsNullOrWhiteSpace(s) || s.Length < 3 || !char.IsLetter(s[0])) return false;
    foreach (var ch in s) if (ch is < ' ' or > '~') return false;
    return true;
}

static int RunItemDump(MemoryReader reader, nint item)
{
    var meta = ReadEntityMetadata(reader, item);
    Console.WriteLine($"Item 0x{item:X}  {meta}");
    if (!meta.StartsWith("Metadata/Items", StringComparison.Ordinal))
        Console.WriteLine("  (warning: metadata is not Metadata/Items — wrong address?)");

    var comps = ComponentNamesAndAddrs(reader, item);
    Console.WriteLine($"  components ({comps.Count}): {string.Join(", ", comps.Select(c => c.name).OrderBy(s => s, StringComparer.Ordinal))}");

    // ── Base component probe: locate the rendered BASE-TYPE NAME ("Greater Orb of Augmentation"). Walk the
    //    component's first qwords; for each canonical pointer, try (a) a direct UTF-16 string, and (b) one
    //    deref then UTF-16 (BaseItemTypes row → name ptr). Prints offset + the string so we can pin it.
    var basec = comps.FirstOrDefault(c => c.name == "Base").addr;
    if (basec != 0)
    {
        Console.WriteLine($"\n  Base @ 0x{basec:X} — Base+off → BaseItemTypes ROW; scan ROW fields for the name:");
        for (var off = 0; off <= 0x40; off += 8)
        {
            var row = SafePtr(reader, basec + off);
            if (row == 0) continue;
            // Treat `row` as a dat row: scan its qword fields as string pointers (one deref → UTF-16).
            for (var k = 0; k <= 0x40; k += 8)
            {
                var sp = SafePtr(reader, row + k);
                if (sp == 0) continue;
                var s = reader.ReadStringUtf16(sp, 64);
                if (LooksPrintable(s)) Console.WriteLine($"    Base+0x{off:X2} → row+0x{k:X2} → \"{s}\"");
            }
        }
    }

    // ── Mods component ──
    var mods = comps.FirstOrDefault(c => c.name == "Mods").addr;
    if (mods != 0)
    {
        reader.TryReadStruct<int>(mods + Poe2.ModsComponent.Rarity, out var rarity);
        reader.TryReadStruct<int>(mods + Poe2.ModsComponent.Identified, out var ident);
        Console.WriteLine($"\n  Mods @ 0x{mods:X}  rarity={rarity}  identified={ident}");
        foreach (var (kind, off) in new[] { ("implicit", Poe2.ModsComponent.ImplicitMods),
                                            ("explicit", Poe2.ModsComponent.ExplicitMods),
                                            ("enchant",  Poe2.ModsComponent.EnchantMods) })
        {
            if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(mods + off, out var v)) continue;
            var span = (long)v.Last - (long)v.First;
            if (v.First == 0 || span <= 0 || span % 0x40 != 0 || span > 0x800) continue;
            var n = (int)(span / 0x40);
            if (n == 0) continue;
            Console.WriteLine($"    {kind} ({n}):");
            for (var i = 0; i < n; i++)
            {
                var rec = v.First + (nint)(i * 0x40);
                var row = SafePtr(reader, rec + Poe2.ModsComponent.ModRecordPtr);
                var id = ReadModName(reader, row);
                var vals = ReadModValueArray(reader, rec);
                var text = string.Join("; ", POE2Radar.Core.Game.ItemModTranslator.Shared.RenderMod(id, vals));
                var sids = POE2Radar.Core.Game.ItemModTranslator.Shared.StatIdsFor(id);
                Console.WriteLine($"      [{i}] {id} [{string.Join(",", vals)}] → {text}");
                if (sids != null) Console.WriteLine($"            stats: {string.Join(", ", sids)}");
                if (i == 0) DumpDatRow(reader, row, "            ");  // confirms affix Name + StatsKey storage
            }
        }
    }

    // ── LocalStats / Stats component (aggregated stat key->value the item grants) ──
    foreach (var sc in new[] { "LocalStats", "Stats" })
    {
        var statc = comps.FirstOrDefault(c => c.name == sc).addr;
        if (statc == 0) continue;
        Console.WriteLine($"\n  {sc} @ 0x{statc:X} — StdVector<{{int key,int value}}> candidates:");
        for (var off = 0; off + 24 <= 0x200; off += 8)
        {
            if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(statc + off, out var v)) continue;
            var span = (long)v.Last - (long)v.First;
            if (v.First == 0 || span <= 0 || span % 8 != 0 || span > 0x400) continue;
            var n = (int)(span / 8); var pairs = new List<string>(); var ok = true;
            for (var i = 0; i < n && ok; i++)
            {
                if (!reader.TryReadStruct<int>(v.First + (nint)(i * 8), out var k) ||
                    !reader.TryReadStruct<int>(v.First + (nint)(i * 8) + 4, out var val)) { ok = false; break; }
                if (k is < 0 or > 0x20000) { ok = false; break; } // key = stat index, small
                pairs.Add($"{k}={val}");
            }
            if (ok && pairs.Count > 0) Console.WriteLine($"    +0x{off:X3}: [{string.Join(", ", pairs)}]");
        }
    }

    // ── Sockets component ──
    var sockets = comps.FirstOrDefault(c => c.name == "Sockets").addr;
    if (sockets != 0)
    {
        Console.WriteLine($"\n  Sockets @ 0x{sockets:X} — scanning for socketed-item ptrs + vectors:");
        ScanComponentForItems(reader, sockets, 0x120);
    }
    return 0;
}

// Hunt a Mods.dat row for string/foreign-key columns. PoE .dat rows are PACKED (mixed-width, NOT
// aligned), so we scan at 1-BYTE stride for any qword that is a valid heap pointer, and classify it:
//   (a) direct UTF-16/UTF-8 string  -> the mod Id or affix Name column;
//   (b) pointer whose target's first qword -> a snake_case string -> a Stats.dat row (StatsKey column):
//       this resolves mod -> stat ids ENTIRELY in-memory, leaving only stat-id->template as external data.
// Dedupes by resolved string so the 1-byte overlap doesn't spam.
static void DumpDatRow(MemoryReader reader, nint row, string indent)
{
    if (row == 0) return;
    Console.WriteLine($"{indent}Mods.dat row 0x{row:X} — 1-byte-stride pointer/string scan (0x180):");
    var seenStr = new HashSet<string>();
    var seenStat = new HashSet<string>();
    for (var off = 0; off < 0x180; off += 1)
    {
        var q = SafePtr(reader, row + off);
        if (q == 0) continue;
        // (a) direct string column
        var w = reader.ReadStringUtf16(q, 64);
        if (Printable(w) && seenStr.Add(w)) { Console.WriteLine($"{indent}  +0x{off:X3} -> str \"{w}\""); continue; }
        var a = reader.ReadStringUtf8(q, 64);
        if (Printable(a) && IsStatId(a) && seenStr.Add(a)) { Console.WriteLine($"{indent}  +0x{off:X3} -> str \"{a}\""); continue; }
        // (b) foreign-row pointer -> target's first qword -> snake_case stat id (Stats.dat)
        var inner = SafePtr(reader, q);
        if (inner != 0)
        {
            var sid = reader.ReadStringUtf8(inner, 64);
            if (IsStatId(sid) && seenStat.Add(sid))
                Console.WriteLine($"{indent}  +0x{off:X3} -> statRow 0x{q:X} -> id \"{sid}\"");
        }
    }
    if (seenStat.Count == 0) Console.WriteLine($"{indent}  (no Stats.dat-row pointers found — StatsKeys likely stored as row indices, not pointers)");
}

// A PoE stat id: lowercase snake_case ASCII, length >= 4, e.g. "local_energy_shield".
static bool IsStatId(string? s) =>
    !string.IsNullOrEmpty(s) && s.Length is >= 4 and <= 96 &&
    s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') && s.Contains('_');

// Scan a component's first `len` bytes for (a) pointers to item entities (socketed gems/runes) and
// (b) StdVectors of such pointers — to map the Sockets layout.
static void ScanComponentForItems(MemoryReader reader, nint comp, int len)
{
    for (var off = 0; off + 8 <= len; off += 8)
    {
        var p = SafePtr(reader, comp + off);
        if (p == 0) continue;
        var m = ReadEntityMetadata(reader, p);
        if (m.StartsWith("Metadata/", StringComparison.Ordinal))
            Console.WriteLine($"    +0x{off:X2}: entity 0x{p:X}  {m}");
        // StdVector of entity ptrs?
        if (reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(comp + off, out var v))
        {
            var span = (long)v.Last - (long)v.First;
            if (v.First != 0 && span > 0 && span % 8 == 0 && span <= 0x200)
            {
                var n = (int)(span / 8); var hit = new List<string>();
                for (var i = 0; i < n; i++)
                {
                    var e = SafePtr(reader, v.First + (nint)(i * 8));
                    var em = e == 0 ? "" : ReadEntityMetadata(reader, e);
                    if (em.StartsWith("Metadata/", StringComparison.Ordinal)) hit.Add($"{em}");
                }
                if (hit.Count > 0) Console.WriteLine($"    +0x{off:X2}: vec[{n}] -> {string.Join(", ", hit)}");
            }
        }
    }
}

// Like ComponentNames but returns (name, componentAddr) pairs.
static List<(string name, nint addr)> ComponentNamesAndAddrs(MemoryReader reader, nint entity)
{
    var result = new List<(string, nint)>();
    var details = SafePtr(reader, entity + Poe2.Entity.EntityDetailsPtr);
    var lookup = details == 0 ? 0 : SafePtr(reader, details + Poe2.EntityDetails.ComponentLookUpPtr);
    if (lookup == 0) return result;
    if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(entity + Poe2.Entity.ComponentList, out var cl)) return result;
    var compCount = ((long)cl.Last - (long)cl.First) / 8;
    if (compCount is <= 0 or > 256) return result;
    var bFirst = SafePtr(reader, lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
    if (!reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return result;
    var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
    if (bFirst == 0 || entries is <= 0 or > 256) return result;
    for (long i = 0; i < entries; i++)
    {
        var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
        if (!reader.TryReadStruct<int>(e + 8, out var index) || index < 0 || index >= compCount) continue;
        var nm = reader.ReadStringUtf8(SafePtr(reader, e), 40);
        if (!string.IsNullOrEmpty(nm)) result.Add((nm, SafePtr(reader, cl.First + (nint)(index * 8))));
    }
    return result;
}

// All component names on an entity (mirrors the ResolveComponentAddr bucket walk).
static List<string> ComponentNames(MemoryReader reader, nint entity)
{
    var names = new List<string>();
    var details = SafePtr(reader, entity + Poe2.Entity.EntityDetailsPtr);
    var lookup = details == 0 ? 0 : SafePtr(reader, details + Poe2.EntityDetails.ComponentLookUpPtr);
    if (lookup == 0) return names;
    var bFirst = SafePtr(reader, lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
    if (!reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return names;
    var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
    if (bFirst == 0 || entries is <= 0 or > 256) return names;
    for (long i = 0; i < entries; i++)
    {
        var nm = reader.ReadStringUtf8(SafePtr(reader, bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride)), 40);
        if (!string.IsNullOrEmpty(nm)) names.Add(nm);
    }
    return names;
}

static string InvName(int id) => id switch
{
    1 => "MainInventory", 2 => "BodyArmour", 3 => "Weapon1", 4 => "Offhand1", 5 => "Helm",
    6 => "Amulet", 7 => "Ring1", 8 => "Ring2", 9 => "Gloves", 10 => "Boots", 11 => "Belt",
    12 => "Flask", 13 => "Cursor", 14 => "Map", 15 => "Weapon2", 16 => "Offhand2",
    64 => "Currency", 128 => "Ring3", _ => $"Inv{id}"
};

// ── PoE2 entity / component-map probe ──────────────────────────────────────
// Validates the GameHelper2 PoE2 layout: Entity{Id@0x80, IsValid@0x84, ItemBase{
//   EntityDetailsPtr@0x08, ComponentList StdVector@0x10}}, EntityDetails{name@0x08,
//   ComponentLookUpPtr@0x28}, ComponentLookUp.StdBucket@0x28 of (NamePtr, Index) →
//   ComponentList[Index]. Render.CurrentWorldPosition@0xB8; grid = world / (250/23).
static int RunEntityProbe(MemoryReader reader, nint entity)
{
    const float WorldToGridRatio = 250f / 23f; // ≈ 10.8696 (GameHelper2 TileStructure)

    Console.WriteLine($"Entity @ 0x{entity:X16}");
    if (!reader.TryReadStruct<uint>(entity + 0x80, out var id) ||
        !reader.TryReadStruct<byte>(entity + 0x84, out var isValid))
    {
        Console.Error.WriteLine("  could not read Entity.Id / IsValid");
        return 1;
    }
    Console.WriteLine($"  Id        : {id} (0x{id:X8})   IsValid byte: 0x{isValid:X2} (valid={(isValid & 1) == 0})");

    var detailsPtr   = reader.ReadPointer(entity + 0x08);
    var componentList = reader.ReadStruct<POE2Radar.Core.Game.StdVector>(entity + 0x10);
    var compCount = ((long)componentList.Last - (long)componentList.First) / 8;
    Console.WriteLine($"  Details   : 0x{detailsPtr:X16}   ComponentList: {compCount} entries");

    if (detailsPtr == 0) { Console.Error.WriteLine("  null details"); return 1; }
    Console.WriteLine($"  Metadata  : {ReadStdWString(reader, detailsPtr + 0x08)}");

    var lookupPtr = reader.ReadPointer(detailsPtr + 0x28);
    if (lookupPtr == 0) { Console.Error.WriteLine("  null component lookup"); return 1; }

    // StdBucket.Data (StdVector) lives at ComponentLookUp + 0x28; element = {IntPtr Name, int Index, int pad} = 16 bytes.
    var bucket = reader.ReadStruct<POE2Radar.Core.Game.StdVector>(lookupPtr + 0x28);
    var entryCount = ((long)bucket.Last - (long)bucket.First) / 16;
    Console.WriteLine($"  Components : {entryCount} named");
    if (entryCount <= 0 || entryCount > 256) { Console.Error.WriteLine("  implausible component count — chain offset likely wrong"); return 1; }

    var byName = new Dictionary<string, nint>(StringComparer.Ordinal);
    for (long i = 0; i < entryCount; i++)
    {
        var entryAddr = bucket.First + (nint)(i * 16);
        var namePtr = reader.ReadPointer(entryAddr);
        if (!reader.TryReadStruct<int>(entryAddr + 8, out var index)) continue;
        var name = reader.ReadStringUtf8(namePtr, 64);
        if (string.IsNullOrEmpty(name) || index < 0 || index >= compCount) continue;
        var compAddr = reader.ReadPointer(componentList.First + (nint)(index * 8));
        byName[name] = compAddr;
        Console.WriteLine($"    [{index,2}] {name,-22} @ 0x{compAddr:X16}");
    }

    // Render.CurrentWorldPosition validated @ +0x138 on live PoE2 (GameHelper2's 0xB8 is stale here).
    if (byName.TryGetValue("Render", out var render) && render != 0 &&
        reader.TryReadStruct<POE2Radar.Core.Game.Vector3>(render + 0x138, out var world))
    {
        Console.WriteLine($"  Render.World : ({world.X:F1}, {world.Y:F1}, {world.Z:F1})");
        Console.WriteLine($"  → Grid       : ({world.X / WorldToGridRatio:F1}, {world.Y / WorldToGridRatio:F1})");
    }
    if (byName.TryGetValue("Life", out var life) && life != 0 &&
        reader.TryReadStruct<POE2Radar.Core.Game.VitalStruct>(life + 0x1A8, out var hp))
    {
        Console.WriteLine($"  Life.Health  : {hp.Current} / {hp.Max}");
    }
    if (byName.TryGetValue("Player", out var pc) && pc != 0)
    {
        // PoE2 Player component char-name offset unknown yet; dump a window to find the character name.
        Console.WriteLine($"  Player comp  @ 0x{pc:X16} (char-name offset TBD — dump to locate)");
    }
    return 0;
}

// ── String scan: anchor a new subsystem by the text it shows ─────────────────
// Scans readable memory for a literal string (UTF-16LE by default; UTF-8 with --utf8), reports
// each hit address + a little context, and (with --refs) back-scans private memory for 8-byte-
// aligned pointers to each hit — i.e. the struct fields that reference the string. This is how we
// pin the Atlas: the hovered map's name/biome/description are live UTF-16 right now, and whatever
// points at them is the node (or its dat row). --all-regions widens past private heap to image/
// mapped pages (dat string tables sometimes live there); default private-only is much faster.
static int RunScanString(MemoryReader reader, string text, bool utf8, bool allRegions, bool refs, int max)
{
    var needle = utf8 ? System.Text.Encoding.UTF8.GetBytes(text) : System.Text.Encoding.Unicode.GetBytes(text);
    Console.WriteLine($"Scanning {(allRegions ? "ALL readable" : "private")} regions for {(utf8 ? "UTF-8" : "UTF-16")} \"{text}\" ({needle.Length} bytes)...");

    var hits = ScanBytes(reader, needle, allRegions, max);
    Console.WriteLine($"{hits.Count} hit(s){(hits.Count >= max ? " (capped — raise --max)" : "")}.");

    foreach (var h in hits)
    {
        // Context window: 0x20 before, the match, and a bit after — to eyeball whether the string is
        // inline in a struct (SSO StdWString) or a standalone heap/dat allocation.
        var ctx = new byte[0x60];
        var ctxBase = h - 0x20;
        var n = reader.TryReadBytes(ctxBase, ctx);
        Console.WriteLine($"\n  hit @ 0x{h:X16}");
        if (n > 0)
            for (var i = 0; i < n; i += 16)
                Console.WriteLine($"    +0x{i - 0x20,3:+0;-0;0}  {string.Join(' ', Enumerable.Range(0, Math.Min(16, n - i)).Select(j => ctx[i + j].ToString("X2")))}");
    }

    if (refs && hits.Count > 0)
    {
        Console.WriteLine("\n=== back-references (8-byte-aligned pointers into private memory) ===");
        foreach (var h in hits)
        {
            Console.WriteLine($"\n  -> pointers to 0x{h:X16}:");
            var ptrHits = ScanBytes(reader, BitConverter.GetBytes((long)h), allRegions: false, max: 20, aligned: 8);
            if (ptrHits.Count == 0) { Console.WriteLine("     (none — string may be referenced via an offset/handle, not a raw pointer)"); continue; }
            foreach (var p in ptrHits) Console.WriteLine($"     0x{p:X16}");
        }
        Console.WriteLine("\nNext: --dump <referrer> to inspect the struct, or --find <referrer> to climb to its container.");
    }
    return 0;
}

// Byte-pattern scan over committed readable regions. Reads in 1 MiB chunks overlapped by
// (pattern-1) bytes so a match straddling a chunk boundary isn't missed. `aligned` (default 1)
// restricts matches to that byte alignment within a region (8 for pointer scans). Capped at `max`.
static List<nint> ScanBytes(MemoryReader reader, byte[] pattern, bool allRegions, int max, int aligned = 1)
{
    var hits = new List<nint>();
    if (pattern.Length == 0) return hits;
    var regions = reader.Process.EnumerateReadableRegions(privateOnly: !allRegions).ToArray();
    var chunk = new byte[1 << 20];
    var overlap = pattern.Length - 1;
    foreach (var (regionBase, regionSize) in regions)
    {
        long off = 0;
        while (off < regionSize && hits.Count < max)
        {
            var toRead = (int)Math.Min(chunk.Length, regionSize - off);
            var read = reader.TryReadBytes(regionBase + (nint)off, chunk.AsSpan(0, toRead));
            if (read <= 0) break;
            var span = chunk.AsSpan(0, read);
            var search = 0;
            while (search <= read - pattern.Length)
            {
                var idx = span.Slice(search).IndexOf(pattern);
                if (idx < 0) break;
                var at = search + idx;
                var abs = regionBase + (nint)(off + at);
                if (aligned <= 1 || ((long)abs % aligned) == 0)
                {
                    hits.Add(abs);
                    if (hits.Count >= max) break;
                }
                search = at + 1;
            }
            if (read != toRead) break;          // region torn down / partial — move on
            if (toRead < chunk.Length) break;   // that was this region's final (tail) chunk — done
            off += chunk.Length - overlap;       // next window overlaps so a boundary-straddling match is caught
        }
    }
    return hits;
}

// ── Range pointer back-search: find 8-byte-aligned locations holding a pointer INTO [lo, lo+len) ──
// Used to locate everything that references a packed dat row (whose exact field-0 base is unknown):
// any pointer landing inside the row's byte range counts. Reports each referrer, the exact target it
// points to, and groups referrers by their containing 4 KiB page + by stride, so an ARRAY of
// references (the maps catalog, or a live node table) shows up as a regular run.
static int RunFindRange(MemoryReader reader, nint lo, int len, int max)
{
    var hi = lo + len;
    Console.WriteLine($"Scanning ALL readable regions for pointers into [0x{lo:X}, 0x{hi:X}) ({len} bytes)...");
    var regions = reader.Process.EnumerateReadableRegions(privateOnly: false).ToArray();
    var chunk = new byte[1 << 20];
    var hits = new List<(nint at, nint target)>();
    foreach (var (regionBase, regionSize) in regions)
    {
        long off = 0;
        while (off < regionSize && hits.Count < max)
        {
            var toRead = (int)Math.Min(chunk.Length, regionSize - off);
            var read = reader.TryReadBytes(regionBase + (nint)off, chunk.AsSpan(0, toRead));
            if (read <= 0) break;
            for (var i = 0; i + 8 <= read; i += 8)
            {
                var v = (nint)BitConverter.ToInt64(chunk, i);
                if (v >= lo && v < hi) { hits.Add((regionBase + (nint)(off + i), v)); if (hits.Count >= max) break; }
            }
            if (read != toRead) break;
            if (toRead < chunk.Length) break;
            off += chunk.Length - 8;
        }
    }
    Console.WriteLine($"{hits.Count} referrer(s){(hits.Count >= max ? " (capped — raise --max)" : "")}.");
    foreach (var (at, target) in hits)
        Console.WriteLine($"  @ 0x{at:X16}  -> 0x{target:X}  (+0x{(long)target - (long)lo:X} into range)");

    // Stride analysis: sort referrer addresses, report common gaps (an array of refs has a fixed stride).
    var addrs = hits.Select(h => (long)h.at).OrderBy(a => a).ToArray();
    if (addrs.Length >= 2)
    {
        var gaps = new Dictionary<long, int>();
        for (var i = 1; i < addrs.Length; i++) { var g = addrs[i] - addrs[i - 1]; if (g is > 0 and <= 0x4000) gaps[g] = gaps.GetValueOrDefault(g) + 1; }
        Console.WriteLine("\ncommon referrer strides (gap: count) — a regular stride ⇒ an array of references:");
        foreach (var kv in gaps.OrderByDescending(k => k.Value).Take(8))
            Console.WriteLine($"  stride 0x{kv.Key:X} ({kv.Key}): {kv.Value}");
    }
    return 0;
}

// Pointer back-search: find 8-byte-aligned locations holding `needle`. With --near <addr>,
// only scans [addr, addr+window) (fast, for locating a field offset within one object);
// otherwise scans all readable private regions (slow). Prints each hit and, when --near is
// given, its offset from the near base.
static int RunFindPointer(MemoryReader reader, nint needle, nint? near, int window, bool allRegions = false, int align = 8)
{
    if (align < 1) align = 1;
    var target = (long)needle;
    var hits = 0;
    if (near is { } baseAddr)
    {
        Console.WriteLine($"Searching [0x{baseAddr:X}, +0x{window:X}) for 0x{needle:X16} (align {align})...");
        var buf = new byte[window];
        var n = reader.TryReadBytes(baseAddr, buf);
        for (var i = 0; i + 8 <= n; i += align)
            if (BitConverter.ToInt64(buf, i) == target)
                { Console.WriteLine($"  hit @ 0x{baseAddr + i:X16}  (base +0x{i:X})"); hits++; }
        Console.WriteLine($"{hits} hit(s).");
        return 0;
    }

    Console.WriteLine($"Scanning {(allRegions ? "ALL readable" : "private")} regions for 0x{needle:X16} (align {align})...");
    var regions = reader.Process.EnumerateReadableRegions(privateOnly: !allRegions).ToArray();
    var chunk = new byte[1 << 20];
    for (var ri = 0; ri < regions.Length && hits < 60; ri++)
    {
        var (regionBase, regionSize) = regions[ri];
        long off = 0;
        while (off < regionSize && hits < 60)
        {
            var toRead = (int)Math.Min(chunk.Length, regionSize - off);
            var read = reader.TryReadBytes(regionBase + (nint)off, chunk.AsSpan(0, toRead));
            if (read == 0) break;
            for (var i = 0; i + 8 <= read; i += align)
                if (BitConverter.ToInt64(chunk, i) == target)
                    { Console.WriteLine($"  hit @ 0x{regionBase + (nint)(off + i):X16}"); if (++hits >= 60) break; }
            if (read != toRead) break;          // partial/torn — done with region
            if (toRead < chunk.Length) break;   // final (tail) chunk of region scanned — done
            off += chunk.Length - 8;             // overlap 8 so an 8-byte value isn't split at the seam
        }
    }
    Console.WriteLine($"{hits} hit(s){(hits >= 60 ? " (capped)" : "")}.");
    return 0;
}

// ── Camera: find the WorldToScreen 4x4 matrix. Scans pointers reachable from InGameState; for
// each pointed object, treats every 16-float window as a row-major matrix, projects the player's
// world position, and reports any that land the player near screen-center (the camera follows
// the player). Run standing still.
static int RunCamera(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, ai, lp) = ResolveChain(process, reader);   // 2nd element = InGameState
    if (igs == 0) { Console.Error.WriteLine("no chain"); return 1; }
    var render = ResolveComponentAddr(reader, lp, "Render");
    if (render == 0 || !reader.TryReadStruct<POE2Radar.Core.Game.Vector3>(render + 0x138, out var w))
    { Console.Error.WriteLine("no player world pos"); return 1; }
    Win.GetClientRect(Win.GetForegroundWindow(), out var rc);
    int W = rc.right - rc.left, H = rc.bottom - rc.top;
    if (W <= 0) { W = 1920; H = 1080; }
    var cam368 = SafePtr(reader, igs + 0x368);
    Console.WriteLine($"InGameState 0x{igs:X}  Camera(*+0x368) 0x{cam368:X}  player world=({w.X:F1},{w.Y:F1},{w.Z:F1})  window={W}x{H}");
    var monsters = new List<POE2Radar.Core.Game.Vector3>();
    var head = SafePtr(reader, ai + Poe2.AreaInstance.AwakeEntities);
    if (head != 0)
    {
        var q = new Queue<nint>(); q.Enqueue(SafePtr(reader, head + Poe2.StdMapNode.Parent));
        var seen = new HashSet<nint>();
        while (q.Count > 0 && seen.Count < 100000 && monsters.Count < 10)
        {
            var node = q.Dequeue();
            if (node == 0 || node == head || !seen.Add(node)) continue;
            if (!reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
            var ent = SafePtr(reader, node + Poe2.StdMapNode.ValueEntityPtr);
            q.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Left));
            q.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Right));
            if (ent == 0 || !ReadEntityMetadata(reader, ent).Contains("/Monsters/", StringComparison.Ordinal)) continue;
            var r = ResolveComponentAddr(reader, ent, "Render");
            if (r != 0 && reader.TryReadStruct<POE2Radar.Core.Game.Vector3>(r + 0x138, out var mw)) monsters.Add(mw);
        }
    }
    Console.WriteLine($"validating against {monsters.Count} monster world positions.");

    static (float sx, float sy, float cw) Project(float[] m, POE2Radar.Core.Game.Vector3 v, int W, int H)
    {
        float cx = v.X*m[0]+v.Y*m[4]+v.Z*m[8]+m[12];
        float cy = v.X*m[1]+v.Y*m[5]+v.Z*m[9]+m[13];
        float cw = v.X*m[3]+v.Y*m[7]+v.Z*m[11]+m[15];
        return ((cx/cw/2f + 0.5f) * W, (0.5f - cy/cw/2f) * H, cw);
    }

    // Zoom per the community note (Camera+0x528) — a sanity readout.
    if (cam368 != 0 && reader.TryReadStruct<float>(cam368 + 0x528, out var zoom)) Console.WriteLine($"  Camera.Zoom(*+0x528) = {zoom}");

    // Scan candidate camera objects: the +0x368 camera first, then any pointer in InGameState.
    var objs = new List<(string label, nint addr)>();
    if (cam368 != 0) objs.Add(("Camera+0x368", cam368));
    for (var o = 0; o < 0x600; o += 8) { var p = SafePtr(reader, igs + o); if (p != 0 && p != cam368) objs.Add(($"IGS+0x{o:X3}", p)); }

    var buf = new byte[0x600];
    foreach (var (label, cam) in objs)
    {
        if (reader.TryReadBytes(cam, buf) < buf.Length) continue;
        for (var mo = 0; mo + 64 <= buf.Length; mo += 4)
        {
            var m = new float[16];
            for (var i = 0; i < 16; i++) m[i] = BitConverter.ToSingle(buf, mo + i * 4);
            var (sx, sy, cw) = Project(m, w, W, H);
            if (cw < 1f || cw > 1_000_000f) continue;
            if (sx < W*0.25f || sx > W*0.75f || sy < H*0.25f || sy > H*0.75f) continue; // player ~ center
            int on = 0; float minx = 9e9f, maxx = -9e9f;
            foreach (var mw in monsters)
            {
                var (msx, msy, mcw) = Project(m, mw, W, H);
                if (mcw > 0 && msx >= 0 && msx <= W && msy >= 0 && msy <= H) { on++; minx = Math.Min(minx, msx); maxx = Math.Max(maxx, msx); }
            }
            var need = monsters.Count == 0 ? 0 : Math.Max(1, (int)(monsters.Count * 0.6));
            if (on < need) continue;
            // spreadX = how far apart monsters land horizontally — a real projection spreads them; a
            // degenerate one stacks them near center.
            var spread = on > 1 ? (int)(maxx - minx) : 0;
            Console.WriteLine($"  {label} (0x{cam:X}) matrix@+0x{mo:X3} -> player=({sx:F0},{sy:F0}) w={cw:F1}  onScreen={on}/{monsters.Count} spreadX={spread}");
        }
    }
    Console.WriteLine("Real W2S: from the Camera+0x368 object, player≈center, all monsters on-screen, and a healthy spreadX.");
    return 0;
}

// ── Info: validate the community-note fields reachable from town — area name, character
// name/level, camera/zoom — and dump the camera object so the WorldToScreen matrix can be found.
static int RunInfo(ProcessHandle process, MemoryReader reader)
{
    var (igs, _, ai, lp) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    Console.WriteLine($"InGameState 0x{igs:X}  AreaInstance 0x{ai:X}  LocalPlayer 0x{lp:X}");

    // Area name: AreaInstance+0xA0 -> AreaInfo -> +0x00 -> UTF-16 "Code\0Name\0".
    var areaInfo = SafePtr(reader, ai + 0xA0);
    var strPtr = SafePtr(reader, areaInfo);
    var code = reader.ReadStringUtf16(strPtr, 64);
    var name = code.Length > 0 ? reader.ReadStringUtf16(strPtr + (nint)((code.Length + 1) * 2), 64) : "";
    Console.WriteLine($"AreaInfo 0x{areaInfo:X}  Code='{code}'  Name='{name}'");

    // Character: try the Player component, then a 'Character' component if present.
    foreach (var compName in new[] { "Player", "Character", "PlayerClass" })
    {
        var c = ResolveComponentAddr(reader, lp, compName);
        if (c == 0) continue;
        var nm0x1B0 = reader.ReadStringUtf16(c + 0x1B0, 32);
        var nmStd = ReadStdWString(reader, c + 0x1B0);
        reader.TryReadStruct<int>(c + 0x204, out var lvl204);
        reader.TryReadStruct<byte>(c + 0x204, out var lvlByte);
        Console.WriteLine($"  [{compName}] @0x{c:X}  name@0x1B0(raw)='{nm0x1B0}' (std)='{nmStd}'  lvl@0x204 int={lvl204} byte={lvlByte}");
    }

    // Camera: InGameState+0x368 -> Camera; Zoom @ +0x528. Dump +0x000..+0x160 to spot the 4x4 matrix.
    var cam = SafePtr(reader, igs + 0x368);
    Console.WriteLine($"Camera 0x{cam:X}");
    if (cam != 0)
    {
        reader.TryReadStruct<float>(cam + 0x528, out var zoom);
        Console.WriteLine($"  Zoom@0x528 = {zoom}");
        var buf = new byte[0x160];
        if (reader.TryReadBytes(cam, buf) == buf.Length)
            for (var i = 0; i < buf.Length; i += 16)
            {
                var f = string.Join(" ", Enumerable.Range(0, 4).Select(j => BitConverter.ToSingle(buf, i + j * 4).ToString("0.###")));
                Console.WriteLine($"  +0x{i:X3}  {f}");
            }
    }
    return 0;
}

// ── Presence: find the player's "presence radius" field by a walk-stable before/after diff ──
// Presence is a per-entity aura radius (default ~4). We don't know which component/offset holds it,
// nor the unit (metres vs grid), so we diff a byte window of EVERY component on the local player.
//
// THE NOISE PROBLEM: a single before/after read is swamped by world position + animation churn,
// and the player must MOVE between samples (buffs are claimed at different map spots). SOLUTION:
// poll many times WHILE WALKING and keep only floats that stay bitwise-identical across all samples
// — i.e. true constants (max HP, model bounds, base speed, presence radius…). Position/animation
// vary as you move, so they self-eliminate. A float that is walk-stable in BOTH phases yet DIFFERS
// between them — with only the presence buff applied — is the presence field.
//
// UNIT-AGNOSTIC: the buff is a multiplier, so the change shows as a ratio regardless of unit:
//   "+20% Presence radius" → ×1.20    |    "20% increased AoE" → radius ×√1.2 ≈ 1.095 (area = πr²)
//
//   --presence          baseline: poll ~5s while you walk; save the walk-stable constants.
//   --presence --diff   poll ~5s while you walk (buff active); report constants that changed,
//                       ranked by closeness to a presence multiplier. Re-runnable per buff.
static int RunPresence(ProcessHandle process, MemoryReader reader, bool diff)
{
    const int Window = 0x800;       // bytes snapshotted per component
    const int Samples = 9;          // reads per run
    const int IntervalMs = 600;     // ~5s total — walk around the whole time
    var snapPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "poe2_presence.bin");

    var (_, _, lpArea, lp) = ResolveChain(process, reader);
    if (lp == 0) { Console.Error.WriteLine("Could not resolve LocalPlayer (in game?)."); return 1; }
    Console.WriteLine($"LocalPlayer 0x{lp:X}  ({ReadEntityMetadata(reader, lp)})");

    // Resolve component addresses ONCE (stable within an area — do NOT zone during sampling).
    var targets = new List<(string name, nint addr)> { ("<entity>", lp) };
    targets.AddRange(WalkComponents(reader, lp).OrderBy(c => c.name, StringComparer.Ordinal));
    Console.WriteLine($"{targets.Count} components (incl. <entity>). Polling {Samples}× over ~{Samples * IntervalMs / 1000.0:F0}s — WALK AROUND now.");

    byte[] ReadOne(nint addr)
    {
        var b = new byte[Window];
        var n = reader.TryReadBytes(addr, b);
        return n == Window ? b : n > 0 ? b[..n] : Array.Empty<byte>();
    }

    // Sample 0 establishes the candidate values; later samples knock out any slot that moves.
    var value = new Dictionary<string, byte[]>(StringComparer.Ordinal);          // name -> first-sample bytes
    var stable = new Dictionary<string, bool[]>(StringComparer.Ordinal);          // name -> per-4-byte-slot "never changed"
    foreach (var (name, addr) in targets)
    {
        var d = ReadOne(addr);
        value[name] = d;
        stable[name] = Enumerable.Repeat(true, d.Length / 4).ToArray();
    }
    for (var s = 1; s < Samples; s++)
    {
        Thread.Sleep(IntervalMs);
        foreach (var (name, addr) in targets)
        {
            var d = ReadOne(addr);
            var f = value[name]; var st = stable[name];
            for (var slot = 0; slot < st.Length; slot++)
            {
                if (!st[slot]) continue;
                var o = slot * 4;
                if (o + 4 > d.Length || BitConverter.ToInt32(d, o) != BitConverter.ToInt32(f, o)) st[slot] = false;
            }
        }
        Console.Write($"\r  sample {s + 1}/{Samples}   ");
    }
    Console.WriteLine();

    static bool Plausible(float f) => float.IsFinite(f) && f != 0f && MathF.Abs(f) is >= 0.01f and <= 100000f;
    var stableFloats = targets.Sum(t => Enumerable.Range(0, stable[t.name].Length)
        .Count(slot => stable[t.name][slot] && Plausible(BitConverter.ToSingle(value[t.name], slot * 4))));
    Console.WriteLine($"walk-stable plausible floats: {stableFloats}");

    if (!diff)
    {
        using (var fs = System.IO.File.Create(snapPath))
        using (var w = new System.IO.BinaryWriter(fs))
        {
            w.Write(targets.Count);
            foreach (var (name, addr) in targets)
            {
                var d = value[name]; var st = stable[name];
                w.Write(name); w.Write((long)addr); w.Write(d.Length); w.Write(d);
                w.Write(st.Length); foreach (var bit in st) w.Write(bit);
            }
        }
        Console.WriteLine($"baseline written: {snapPath}");
        Console.WriteLine("\n--- walk-stable floats in [3.5, 4.5] (presence-default ≈ 4 candidates) ---");
        foreach (var (name, _) in targets)
            for (var slot = 0; slot < stable[name].Length; slot++)
            {
                if (!stable[name][slot]) continue;
                var f = BitConverter.ToSingle(value[name], slot * 4);
                if (f is >= 3.5f and <= 4.5f) Console.WriteLine($"  {name,-26} +0x{slot * 4:X3} = {f:F4}");
            }
        Console.WriteLine("\nNow claim the presence buff, then run (walking again):  --presence --diff");
        return 0;
    }

    // Diff: load baseline (value + stable mask), keyed by component name.
    if (!System.IO.File.Exists(snapPath)) { Console.Error.WriteLine("No baseline — run --presence first."); return 1; }
    var baseVal = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var baseStable = new Dictionary<string, bool[]>(StringComparer.Ordinal);
    using (var fs = System.IO.File.OpenRead(snapPath))
    using (var r = new System.IO.BinaryReader(fs))
    {
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var name = r.ReadString(); _ = r.ReadInt64();
            var len = r.ReadInt32(); baseVal[name] = r.ReadBytes(len);
            var slots = r.ReadInt32(); var st = new bool[slots];
            for (var k = 0; k < slots; k++) st[k] = r.ReadBoolean();
            baseStable[name] = st;
        }
    }
    Console.WriteLine($"baseline: {baseVal.Count} components loaded from {snapPath}");

    // Closeness to a plausible presence multiplier (1.20 = +radius%, 1.095 = √1.2 area%); 0 = exact.
    // Direction-agnostic: a buff can be GAINED (ratio≈1.20) or LOST (ratio≈0.833=1/1.20), depending
    // on which run is the baseline — normalize to ≥1 before measuring so both read as a hit.
    static float MultiDist(float ratio)
    {
        var r = ratio < 1f ? 1f / ratio : ratio;
        return MathF.Min(MathF.Abs(r - 1.20f), MathF.Abs(r - 1.0954f));
    }

    // A real candidate is walk-stable in BOTH runs (eliminates position/animation) yet differs.
    var changes = new List<(string name, int off, float oldF, float newF, float ratio)>();
    foreach (var (name, _) in targets)
    {
        if (!baseVal.TryGetValue(name, out var ob) || !baseStable.TryGetValue(name, out var obs)) continue;
        var cur = value[name]; var cs = stable[name];
        var slots = Math.Min(obs.Length, cs.Length);
        for (var slot = 0; slot < slots; slot++)
        {
            if (!obs[slot] || !cs[slot]) continue;          // must be constant in BOTH phases
            var o = slot * 4;
            var a = BitConverter.ToSingle(ob, o);
            var b = BitConverter.ToSingle(cur, o);
            if (a == b || !Plausible(a) || !Plausible(b) || MathF.Sign(a) != MathF.Sign(b)) continue;
            changes.Add((name, o, a, b, b / a));
        }
    }

    Console.WriteLine($"\n{changes.Count} walk-stable float(s) changed between baseline and buffed.");
    Console.WriteLine("\n--- ranked by closeness to a presence multiplier (1.20 or √1.2≈1.095) ---");
    foreach (var c in changes.OrderBy(c => MultiDist(c.ratio)))
    {
        var flag = MultiDist(c.ratio) < 0.02f ? "  <== presence candidate" : "";
        Console.WriteLine($"  {c.name,-26} +0x{c.off:X3}  {c.oldF,12:F4} -> {c.newF,-12:F4}  x{c.ratio:F4}{flag}");
    }
    if (changes.Count == 0)
        Console.WriteLine("  (nothing changed among walk-stable constants — did the buff apply? same area? walking both runs?)");
    Console.WriteLine("\nConfirm: --dump <componentAddr> at that offset, then toggle the buff and re-diff to verify it tracks.");
    return 0;
}

// Walk an entity's component lookup, returning every (componentName, componentAddr). Mirrors
// ResolveComponentAddr but yields the whole set (for snapshotting all of a player's components).
static List<(string name, nint addr)> WalkComponents(MemoryReader reader, nint entity)
{
    var result = new List<(string, nint)>();
    var details = SafePtr(reader, entity + Poe2.Entity.EntityDetailsPtr);
    if (details == 0) return result;
    var lookup = SafePtr(reader, details + Poe2.EntityDetails.ComponentLookUpPtr);
    if (lookup == 0) return result;
    if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(entity + Poe2.Entity.ComponentList, out var cl)) return result;
    var compCount = ((long)cl.Last - (long)cl.First) / 8;
    if (compCount is <= 0 or > 256) return result;
    var bFirst = SafePtr(reader, lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
    if (!reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return result;
    var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
    if (bFirst == 0 || entries is <= 0 or > 256) return result;
    for (long i = 0; i < entries; i++)
    {
        var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
        if (!reader.TryReadStruct<int>(e + 8, out var index) || index < 0 || index >= compCount) continue;
        var name = reader.ReadStringUtf8(SafePtr(reader, e), 40);
        if (string.IsNullOrEmpty(name)) continue;
        result.Add((name, SafePtr(reader, cl.First + (nint)(index * 8))));
    }
    return result;
}

// ── Rarity: find the ObjectMagicProperties rarity offset. Walks all alive monsters, resolves
// each one's ObjectMagicProperties component, and for every 4-byte offset records the set of
// values seen. The rarity field is the offset whose values are all small (0..3) AND vary across
// the sample (white/magic/rare/unique). Run while standing in a mixed pack.
static int RunRarity(ProcessHandle process, MemoryReader reader)
{
    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }

    var head = SafePtr(reader, ai + Poe2.AreaInstance.AwakeEntities);
    reader.TryReadStruct<int>(ai + Poe2.AreaInstance.AwakeEntities + 8, out var size);
    if (head == 0 || size <= 0) { Console.Error.WriteLine("no awake entities"); return 1; }

    const int span = 0x180;
    var perOffset = new Dictionary<int, HashSet<int>>();
    var sampled = 0;
    var queue = new Queue<nint>(); queue.Enqueue(SafePtr(reader, head + Poe2.StdMapNode.Parent));
    var visited = new HashSet<nint>();
    var buf = new byte[span];
    while (queue.Count > 0 && visited.Count < 200000 && sampled < 200)
    {
        var node = queue.Dequeue();
        if (node == 0 || node == head || !visited.Add(node)) continue;
        if (!reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
        reader.TryReadStruct<uint>(node + Poe2.StdMapNode.KeyId, out var id);
        var entity = SafePtr(reader, node + Poe2.StdMapNode.ValueEntityPtr);
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Left));
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Right));
        if (entity == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;
        if (!ReadEntityMetadata(reader, entity).Contains("/Monsters/", StringComparison.Ordinal)) continue;

        var omp = ResolveComponentAddr(reader, entity, "ObjectMagicProperties");
        if (omp == 0 || reader.TryReadBytes(omp, buf) != span) continue;
        sampled++;
        for (var o = 0; o + 4 <= span; o += 4)
        {
            var v = BitConverter.ToInt32(buf, o);
            (perOffset.TryGetValue(o, out var s) ? s : perOffset[o] = new HashSet<int>()).Add(v);
        }
    }
    Console.WriteLine($"sampled {sampled} monsters' ObjectMagicProperties.");
    Console.WriteLine("offsets whose values are all in 0..3 and vary (rarity candidates):");
    foreach (var (o, set) in perOffset.OrderBy(k => k.Key))
        if (set.Count > 1 && set.All(v => v is >= 0 and <= 3))
            Console.WriteLine($"  +0x{o:X3}: values {{{string.Join(",", set.OrderBy(x => x))}}}");
    Console.WriteLine("\n(also showing offsets all in 0..4 with >=3 distinct, in case Unique/special tiers present:)");
    foreach (var (o, set) in perOffset.OrderBy(k => k.Key))
        if (set.Count >= 3 && set.All(v => v is >= 0 and <= 6))
            Console.WriteLine($"  +0x{o:X3}: values {{{string.Join(",", set.OrderBy(x => x))}}}");
    return 0;
}

// ── Item: discover dropped-item identity (art path + unique name + rarity) ──────────────────
// Walks for WorldItem entities on the ground, unwraps each to its inner item entity, lists the
// item's components, and string-scans each component to surface the .dds ART PATH and the UNIQUE
// NAME (the keys for poe.ninja/art-map price lookup). Nothing here is in the validated table yet —
// this is the discovery pass. Run it with a dropped item nearby (ideally an identified unique).
static int RunItem(ProcessHandle process, MemoryReader reader, int maxItems)
{
    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }

    var head = SafePtr(reader, ai + Poe2.AreaInstance.AwakeEntities);
    if (head == 0) { Console.Error.WriteLine("no awake entities"); return 1; }

    // Collect ground items: WorldItem containers (preferred) and any direct Metadata/Items entity.
    var worldItems = new List<(nint addr, string meta)>();
    var queue = new Queue<nint>(); queue.Enqueue(SafePtr(reader, head + Poe2.StdMapNode.Parent));
    var visited = new HashSet<nint>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var node = queue.Dequeue();
        if (node == 0 || node == head || !visited.Add(node)) continue;
        if (!reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
        var entity = SafePtr(reader, node + Poe2.StdMapNode.ValueEntityPtr);
        reader.TryReadStruct<uint>(node + Poe2.StdMapNode.KeyId, out var id);
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Left));
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Right));
        if (entity == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;
        var meta = ReadEntityMetadata(reader, entity);
        if (meta.IndexOf("WorldItem", StringComparison.OrdinalIgnoreCase) >= 0 ||
            meta.StartsWith("Metadata/Items", StringComparison.Ordinal))
            worldItems.Add((entity, meta));
    }

    Console.WriteLine($"found {worldItems.Count} ground-item entity(ies); inspecting up to {maxItems}.\n");
    var shown = 0;
    foreach (var (wiAddr, wiMeta) in worldItems)
    {
        if (shown >= maxItems) break;
        shown++;
        Console.WriteLine($"════════ WorldItem #{shown}  0x{wiAddr:X}  {wiMeta}");
        DumpComponentList(reader, wiAddr, "  ");

        // Unwrap to the inner item entity. The WorldItem component holds a pointer to it; brute-scan
        // the container entity + its WorldItem component for a pointer to a Metadata/Items entity.
        var (item, foundWhere) = FindInnerItem(reader, wiAddr);
        if (item == 0)
        {
            Console.WriteLine("  (no inner Metadata/Items entity found — container may BE the item)");
            item = wiMeta.StartsWith("Metadata/Items", StringComparison.Ordinal) ? wiAddr : 0;
        }
        else Console.WriteLine($"  → inner item entity 0x{item:X}  (ptr found at {foundWhere})");
        if (item == 0) { Console.WriteLine(); continue; }

        var itemMeta = ReadEntityMetadata(reader, item);
        Console.WriteLine($"  ITEM  0x{item:X}  {itemMeta}");
        var comps = ListComponents(reader, item);
        Console.WriteLine($"  components ({comps.Count}): {string.Join(", ", comps.Select(c => c.name))}");

        // Direct rarity read (validated layout: ObjectMagicProperties+0x144; Mods component +0x94).
        foreach (var (cn, ca) in comps)
            if (cn is "ObjectMagicProperties" or "Mods")
            {
                reader.TryReadStruct<int>(ca + 0x144, out var r144);
                reader.TryReadStruct<int>(ca + 0x94, out var r94);
                Console.WriteLine($"    {cn} 0x{ca:X}  rarity@+0x144={r144}  rarity@+0x94={r94}");
            }

        // String-scan every component for the art path + unique name (pointer-to-wide-string).
        Console.WriteLine("  --- string scan (component +offset → wide string) ---");
        foreach (var (cn, ca) in comps)
            ScanComponentStrings(reader, cn, ca, 0x300);
        Console.WriteLine();
    }
    return 0;
}

// Brute-scan a container entity (and its WorldItem component if present) for a pointer to an entity
// whose metadata starts with "Metadata/Items". Returns (itemAddr, "where") or (0, "").
static (nint, string) FindInnerItem(MemoryReader reader, nint container)
{
    bool IsItemEntity(nint p)
    {
        if (!IsUserPtr(p)) return false;
        if (SafePtr(reader, p + Poe2.Entity.EntityDetailsPtr) == 0) return false;
        return ReadEntityMetadata(reader, p).StartsWith("Metadata/Items", StringComparison.Ordinal);
    }

    // 1) the WorldItem component, if any.
    var wi = ResolveComponentAddr(reader, container, "WorldItem");
    if (wi != 0)
        for (var off = 0; off <= 0x80; off += 8)
        {
            var p = SafePtr(reader, wi + off);
            if (IsItemEntity(p)) return (p, $"WorldItem+0x{off:X}");
        }
    // 2) anywhere in the container's first 0x100 bytes.
    for (var off = 0; off <= 0x100; off += 8)
    {
        var p = SafePtr(reader, container + off);
        if (IsItemEntity(p)) return (p, $"entity+0x{off:X}");
    }
    return (0, "");
}

static bool IsUserPtr(nint p) => (ulong)p > 0x10000 && (ulong)p < 0x7FFF_FFFF_0000;

// All (name, address) components of an entity (mirrors ResolveComponentAddr's bucket walk).
static List<(string name, nint addr)> ListComponents(MemoryReader reader, nint entity)
{
    var res = new List<(string, nint)>();
    var details = SafePtr(reader, entity + Poe2.Entity.EntityDetailsPtr);
    if (details == 0) return res;
    var lookup = SafePtr(reader, details + Poe2.EntityDetails.ComponentLookUpPtr);
    if (lookup == 0) return res;
    if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(entity + Poe2.Entity.ComponentList, out var cl)) return res;
    var compCount = ((long)cl.Last - (long)cl.First) / 8;
    if (compCount is <= 0 or > 256) return res;
    var bFirst = SafePtr(reader, lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
    if (!reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return res;
    var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
    if (bFirst == 0 || entries is <= 0 or > 256) return res;
    for (long i = 0; i < entries; i++)
    {
        var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
        if (!reader.TryReadStruct<int>(e + 8, out var index) || index < 0 || index >= compCount) continue;
        var name = reader.ReadStringUtf8(SafePtr(reader, e), 40);
        if (string.IsNullOrEmpty(name)) continue;
        res.Add((name, SafePtr(reader, cl.First + (nint)(index * 8))));
    }
    return res;
}

static void DumpComponentList(MemoryReader reader, nint entity, string indent)
{
    var comps = ListComponents(reader, entity);
    Console.WriteLine($"{indent}components ({comps.Count}): {string.Join(", ", comps.Select(c => c.name))}");
}

// Scan a component's first `span` bytes for 8-byte pointers leading to a readable wide string, and
// print any that look like an art path / metadata path / human name — the price-lookup keys.
static void ScanComponentStrings(MemoryReader reader, string compName, nint comp, int span)
{
    if (comp == 0) return;
    for (var off = 0; off + 8 <= span; off += 8)
    {
        var p = SafePtr(reader, comp + off);
        if (!IsUserPtr(p)) continue;
        string s;
        try { s = reader.ReadStringUtf16(p, 96); } catch { continue; }
        if (!LooksInteresting(s)) continue;
        Console.WriteLine($"    {compName}+0x{off:X3} → \"{s}\"");
    }
}

// Accept art paths, metadata paths, .dds resources, and Title-Case display names (unique/base).
static bool LooksInteresting(string s)
{
    if (s.Length is < 4 or > 96) return false;
    var letters = 0; var printable = 0;
    foreach (var c in s) { if (c is >= ' ' and <= '~') printable++; if (char.IsLetter(c)) letters++; }
    if (printable != s.Length || letters < 3) return false;
    if (s.Contains("Art/", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("Metadata/", StringComparison.Ordinal)) return true;
    // Title-Case-ish display name: has a space and starts uppercase (e.g. "Pillar of the Caged God").
    return s[0] is >= 'A' and <= 'Z' && s.Contains(' ');
}

// ── Label move-diff: find WORLD-ANCHORED label elements by the fact they MOVE on screen when the
// player moves (the user's key insight). Snapshot every UiElement's relPos, wait while the user walks,
// snapshot again; elements whose relPos changed are world-anchored. Text-sized movers that also sit
// next to a known item pointer are the loot labels — the clean joint filter the noisy heap scan lacked.
static int RunLabelMove(ProcessHandle process, MemoryReader reader, int secs)
{
    var (_, igs, ai, _) = ResolveChain(process, reader);
    if (ai == 0 || igs == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    var uiRoot = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    if (uiRoot == 0) { Console.Error.WriteLine("no UiRoot"); return 1; }

    // Known dropped items (container + inner) for the proximity cross-check.
    var known = new Dictionary<nint, string>();
    var head = SafePtr(reader, ai + Poe2.AreaInstance.AwakeEntities);
    var q0 = new Queue<nint>(); q0.Enqueue(SafePtr(reader, head + Poe2.StdMapNode.Parent));
    var v0 = new HashSet<nint>();
    while (q0.Count > 0 && v0.Count < 200000)
    {
        var node = q0.Dequeue();
        if (node == 0 || node == head || !v0.Add(node)) continue;
        if (!reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
        var e = SafePtr(reader, node + Poe2.StdMapNode.ValueEntityPtr);
        reader.TryReadStruct<uint>(node + Poe2.StdMapNode.KeyId, out var id);
        q0.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Left)); q0.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Right));
        if (e == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;
        if (!ReadEntityMetadata(reader, e).Contains("WorldItem", StringComparison.Ordinal)) continue;
        var wi = ResolveComponentAddr(reader, e, "WorldItem");
        var inner = wi == 0 ? 0 : SafePtr(reader, wi + Poe2.WorldItemComponent.ItemEntity);
        var art = "?";
        if (inner != 0) { var ri = ResolveComponentAddr(reader, inner, "RenderItem"); if (ri != 0) art = ItemArtBasename(reader.ReadStringUtf16(SafePtr(reader, ri + Poe2.RenderItemComponent.ResourcePath), 128)) ?? "?"; }
        known[e] = $"{art}(container)"; if (inner != 0) known[inner] = $"{art}(item)";
    }

    Dictionary<nint, (float x, float y, float w, float h)> Snapshot()
    {
        var map = new Dictionary<nint, (float, float, float, float)>();
        var uq = new Queue<(nint, int)>(); uq.Enqueue((uiRoot, 0));
        var uv = new HashSet<nint>();
        while (uq.Count > 0 && uv.Count < 40000)
        {
            var (el, depth) = uq.Dequeue();
            if (el == 0 || depth > 14 || !uv.Add(el)) continue;
            reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos, out var x);
            reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos + 4, out var y);
            reader.TryReadStruct<float>(el + Poe2.UiElement.SizeW, out var w);
            reader.TryReadStruct<float>(el + Poe2.UiElement.SizeH, out var h);
            map[el] = (x, y, w, h);
            var first = SafePtr(reader, el + Poe2.UiElement.Children);
            if (!reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last) || first == 0) continue;
            var n = ((long)last - first) / 8;
            if (n is <= 0 or > 1024) continue;
            for (long i = 0; i < n; i++) { var c = SafePtr(reader, first + (nint)(i * 8)); if (c != 0) uq.Enqueue((c, depth + 1)); }
        }
        return map;
    }

    Console.WriteLine($"snapshot A… (then WALK your character around for {secs}s)");
    var a = Snapshot();
    for (var t = secs; t > 0; t--) { Console.Write($"{t} "); System.Threading.Thread.Sleep(1000); }
    Console.WriteLine("\nsnapshot B…");
    var b = Snapshot();

    // Elements whose relPos changed = world-anchored. Report text-label-sized ones; ★ if near a known item.
    var moved = 0;
    Console.WriteLine($"\n=== world-anchored movers (relPos changed; common in A∩B={a.Keys.Intersect(b.Keys).Count()}) ===");
    foreach (var kv in a)
    {
        if (!b.TryGetValue(kv.Key, out var nb)) continue;
        var (ax, ay, aw, ah) = kv.Value;
        var (bx, by, bw, bh) = nb;
        var d = MathF.Abs(ax - bx) + MathF.Abs(ay - by);
        if (d < 3f) continue;                                  // didn't move → screen-fixed HUD
        if (bw is < 20 or > 1000 || bh is < 6 or > 48) continue; // not a text-label shape
        moved++;
        // is a known item pointer in this element's struct?
        var link = "";
        for (var off = -0x48; off <= 0x80; off += 8)
        {
            var p = SafePtr(reader, kv.Key + off);
            if (p != 0 && known.TryGetValue(p, out var l)) { link = $"  ★ITEM {l} @+0x{off & 0xFFFF:X}"; break; }
        }
        if (moved <= 40)
            Console.WriteLine($"  el=0x{kv.Key:X}  posΔ={d:F0}  ({ax:F0},{ay:F0})→({bx:F0},{by:F0})  size=({bw:F0}x{bh:F0}) parent=0x{SafePtr(reader, kv.Key + 0xB8):X}{link}");
    }
    Console.WriteLine($"\n{moved} text-sized world-anchored element(s). The loot labels are the ones marked ★ITEM (or whose size differs per item / matches the name lengths).");
    return 0;
}

// ── Ground labels: find the in-world loot-LABEL UI elements + the item "identified" flag ────
// The loot label is a UiElement with its own screen rect (NOT the item's world position). It's
// undiscovered for PoE2. Strategy: we know the dropped-item entity addresses, and each label element
// references its item — so walk the UI tree from UiRoot and report any element whose struct contains a
// pointer to a known item (→ that's the label, and the offset is the ItemOnGround link). Also dumps the
// two items' Mods component bytes side-by-side so the IDENTIFIED flag (the byte that differs between an
// identified and an unidentified unique) can be spotted. Run with ≥1 dropped item (ideally one of each).
static int RunGroundLabels(ProcessHandle process, MemoryReader reader, int delaySec)
{
    if (delaySec > 0)
    {
        Console.WriteLine($"*** Switch to PoE2 NOW and keep it FOCUSED (loot labels only lay out while the game renders). Scanning in {delaySec}s... ***");
        for (var t = delaySec; t > 0; t--) { Console.Write($"{t} "); System.Threading.Thread.Sleep(1000); }
        Console.WriteLine("\nscanning…");
    }
    var (_, igs, ai, _) = ResolveChain(process, reader);
    if (ai == 0 || igs == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }

    // 1) Known dropped items: container (WorldItem) + inner item entity, labeled by art basename + rarity.
    var known = new Dictionary<nint, string>();   // addr (container OR inner) → label
    var items = new List<(nint container, nint inner, string art, int rarity)>();
    var head = SafePtr(reader, ai + Poe2.AreaInstance.AwakeEntities);
    var queue = new Queue<nint>(); queue.Enqueue(SafePtr(reader, head + Poe2.StdMapNode.Parent));
    var visited = new HashSet<nint>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var node = queue.Dequeue();
        if (node == 0 || node == head || !visited.Add(node)) continue;
        if (!reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
        var entity = SafePtr(reader, node + Poe2.StdMapNode.ValueEntityPtr);
        reader.TryReadStruct<uint>(node + Poe2.StdMapNode.KeyId, out var id);
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Left));
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Right));
        if (entity == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;
        if (!ReadEntityMetadata(reader, entity).Contains("WorldItem", StringComparison.Ordinal)) continue;

        var wi = ResolveComponentAddr(reader, entity, "WorldItem");
        var inner = wi == 0 ? 0 : SafePtr(reader, wi + Poe2.WorldItemComponent.ItemEntity);
        string art = "?"; int rarity = -1;
        if (inner != 0)
        {
            var ri = ResolveComponentAddr(reader, inner, "RenderItem");
            if (ri != 0) art = ItemArtBasename(reader.ReadStringUtf16(SafePtr(reader, ri + Poe2.RenderItemComponent.ResourcePath), 128)) ?? "?";
            var mc = ResolveComponentAddr(reader, inner, "Mods");
            if (mc != 0 && reader.TryReadStruct<int>(mc + Poe2.ModsComponent.Rarity, out var r)) rarity = r;
        }
        items.Add((entity, inner, art, rarity));
        known[entity] = $"{art}(container)";
        if (inner != 0) known[inner] = $"{art}(item)";
    }

    Console.WriteLine($"dropped items: {items.Count}");
    foreach (var (c, inner, art, rarity) in items)
        Console.WriteLine($"  {art,-20} rarity={rarity}  container=0x{c:X}  item=0x{inner:X}");

    // 2) Mods component byte dump (find the identified flag by diffing ID vs unID).
    Console.WriteLine("\n=== Mods component bytes +0x80..+0x120 (diff ID vs unID for the 'identified' flag) ===");
    foreach (var (_, inner, art, _) in items)
    {
        if (inner == 0) continue;
        var mc = ResolveComponentAddr(reader, inner, "Mods");
        if (mc == 0) { Console.WriteLine($"  {art}: no Mods component"); continue; }
        Console.Write($"  {art,-20} ");
        for (var off = 0x80; off < 0x120; off += 4)
            if (reader.TryReadStruct<int>(mc + off, out var v)) Console.Write($"{off:X3}={v} ");
        Console.WriteLine();
    }

    // 3) Walk the UI tree from UiRoot; report elements whose struct points at a known item.
    var uiRoot = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    Console.WriteLine($"\n=== UI tree scan from UiRoot 0x{uiRoot:X} for pointers to known items ===");
    var uq = new Queue<(nint el, int depth)>(); uq.Enqueue((uiRoot, 0));
    var uvis = new HashSet<nint>();
    var scanned = 0; var hits = 0;
    while (uq.Count > 0 && uvis.Count < 40000)
    {
        var (el, depth) = uq.Dequeue();
        if (el == 0 || depth > 14 || !uvis.Add(el)) continue;
        scanned++;

        // Scan this element's struct for a pointer to a known item entity.
        for (var off = 0; off <= 0x600; off += 8)
        {
            var q = SafePtr(reader, el + off);
            if (q != 0 && known.TryGetValue(q, out var lbl))
            {
                hits++;
                reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos, out var px);
                reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos + 4, out var py);
                reader.TryReadStruct<float>(el + Poe2.UiElement.SizeW, out var sw);
                reader.TryReadStruct<float>(el + Poe2.UiElement.SizeH, out var sh);
                reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags);
                var parent = SafePtr(reader, el + 0xB8);
                Console.WriteLine($"  HIT el=0x{el:X} +0x{off:X3}→{lbl}  relPos=({px:F0},{py:F0}) size=({sw:F0}x{sh:F0}) flags=0x{flags:X} parent=0x{parent:X} depth={depth}");
            }
        }

        // enqueue children (Children StdVector: First @ +0x10, Last @ +0x18; 8-byte child ptrs)
        var first = SafePtr(reader, el + Poe2.UiElement.Children);
        if (!reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last)) continue;
        if (first == 0) continue;
        var n = ((long)last - first) / 8;
        if (n is <= 0 or > 1024) continue;
        for (long i = 0; i < n; i++)
        {
            var child = SafePtr(reader, first + (nint)(i * 8));
            if (child != 0) uq.Enqueue((child, depth + 1));
        }
    }
    Console.WriteLine($"\nscanned {scanned} UI elements, {hits} pointer hit(s).");

    // 4) Heap scan: find every committed-memory location holding a pointer to a known item. The
    // LabelOnGround struct's ItemOnGround field is one of these; its neighbors reveal the struct layout
    // (incl. the Label UiElement pointer → screen rect).
    var targets = new HashSet<long>();
    foreach (var kv in known) targets.Add((long)kv.Key);
    Console.WriteLine($"\n=== heap scan for {targets.Count} item pointer(s) ===");
    var buf = new byte[1 << 20];
    var occ = new List<(nint at, long val)>();
    long scannedBytes = 0;
    foreach (var (regBase, regSize) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
    {
        for (long o = 0; o < regSize; o += buf.Length)
        {
            if (occ.Count >= 400) break;
            var want = (int)Math.Min(buf.Length, regSize - o);
            var got = reader.TryReadBytes(regBase + (nint)o, buf.AsSpan(0, want));
            if (got <= 0) continue;
            scannedBytes += got;
            for (var i = 0; i + 8 <= got; i += 8)
            {
                var v = BitConverter.ToInt64(buf, i);
                if (targets.Contains(v)) occ.Add((regBase + (nint)(o + i), v));
            }
        }
        if (occ.Count >= 400) break;
    }
    Console.WriteLine($"scanned {scannedBytes / (1024 * 1024)} MB, {occ.Count} occurrence(s).");

    // 5) The LabelOnGround struct holds the ItemOnGround ptr next to a Label UiElement ptr. Print ONLY
    //    occurrences that have a UiElement (self-ref *(p+0x08)==p) within a wide neighborhood — those are
    //    the labels. Most other hits are plain entity-pointer arrays (no UiElement nearby).
    bool IsUi(nint p) => p != 0 && SafePtr(reader, p + Poe2.UiElement.Self) == p;
    Console.WriteLine("\n=== LabelOnGround candidates (item ptr with a UiElement neighbor) ===");
    var labelHits = 0;
    nint bestEl = 0, bestStruct = 0; float bestW = 0;
    nint fallbackEl = 0, fallbackStruct = 0;
    foreach (var (at, val) in occ)
    {
        // wide window: the Label element ptr may sit a few qwords from ItemOnGround.
        var uiNeighbor = nint.Zero; var uiOff = 0;
        for (var d = -0x40; d <= 0x80; d += 8)
        {
            var p = SafePtr(reader, at + d);
            if (IsUi(p)) { uiNeighbor = p; uiOff = d; break; }
        }
        if (uiNeighbor == 0) continue;
        labelHits++;
        reader.TryReadStruct<float>(uiNeighbor + Poe2.UiElement.SizeW, out var sw);
        reader.TryReadStruct<float>(uiNeighbor + Poe2.UiElement.SizeH, out var sh);
        if (labelHits <= 16)
            Console.WriteLine($"  LABEL struct @0x{at:X} ({known[(nint)val]}): Label UiElement @{(uiOff < 0 ? "-" : "+")}0x{Math.Abs(uiOff):X} = 0x{uiNeighbor:X}  size=({sw:F0}x{sh:F0})");
        // Pick the widest text-line element (the item-name label) as the anchor for the vector search.
        if (sw > bestW && sw is > 80 and < 1200 && sh is > 6 and < 40) { bestW = sw; bestEl = uiNeighbor; bestStruct = at + uiOff; }
        if (bestEl == 0 && fallbackEl == 0) { fallbackEl = uiNeighbor; fallbackStruct = at + uiOff; } // any candidate, in case nothing is laid out (unfocused)
    }
    if (bestEl == 0) { bestEl = fallbackEl; bestStruct = fallbackStruct; }
    Console.WriteLine($"{labelHits} label candidate(s). best name label = 0x{bestEl:X} (w={bestW:F0}), struct base = 0x{bestStruct:X}");
    if (bestW == 0) Console.WriteLine("  (no laid-out text label found — PoE2 likely wasn't focused; re-run with --delay 8 and switch to the game.)");

    // 6) Walk parents of the best label element; in each ancestor, look for a StdVector whose memory
    //    contains pointers to our known items → that's LabelsOnGround inside ItemsOnGroundLabelElement.
    if (bestEl != 0)
    {
        Console.WriteLine("\n=== parent walk → find LabelsOnGround vector + container ===");
        var el = bestEl;
        for (var lvl = 0; lvl < 10 && el != 0; lvl++)
        {
            var parent = SafePtr(reader, el + 0xB8);
            reader.TryReadStruct<nint>(parent == 0 ? el : parent + Poe2.UiElement.Children, out var pcFirst);
            reader.TryReadStruct<nint>(parent + Poe2.UiElement.ChildrenEnd, out var pcLast);
            var childCount = (parent != 0 && pcLast > pcFirst) ? ((long)pcLast - pcFirst) / 8 : 0;
            Console.WriteLine($"  lvl{lvl}: el=0x{el:X} parent=0x{parent:X} (children={childCount})");
            if (parent == 0) break;

            // Scan the parent struct for a StdVector. LabelsOnGround is a vector of POINTERS to wrapper
            // structs, so for each slot we follow the pointer and check wrapper+0x40 (ItemOnGround) ∈ known.
            for (var off = 0; off <= 0xC00; off += 8)
            {
                var first = SafePtr(reader, parent + off);
                if (!ModIsPtr(first)) continue;
                if (!reader.TryReadStruct<nint>(parent + off + 8, out var last)) continue;
                var slots = ((long)last - first) / 8;
                if (slots is <= 0 or > 4096) continue;
                var matches = 0; long firstMatchSlot = -1;
                for (long k = 0; k < slots && k < 512; k++)
                {
                    var wptr = SafePtr(reader, first + (nint)(k * 8));
                    if (!ModIsPtr(wptr)) continue;
                    var item = SafePtr(reader, wptr + 0x40);
                    if (item != 0 && known.ContainsKey(item)) { matches++; if (firstMatchSlot < 0) firstMatchSlot = k; }
                }
                if (matches > 0)
                    Console.WriteLine($"    ★ LabelsOnGround(ptr-vec) container=0x{parent:X} vec@+0x{off:X} First=0x{first:X} slots={slots} matches={matches} (1st@slot {firstMatchSlot})");
            }
            el = parent;
        }
    }

    // 7) Definitive locator: heap-scan for a pointer EQUAL to the best wrapper struct base. That hit IS a
    //    LabelsOnGround vector slot — dump around it to reveal the vector (First/Last) + the owner.
    if (bestStruct != 0)
    {
        Console.WriteLine($"\n=== heap scan for pointer == wrapper base 0x{bestStruct:X} ===");
        var wrapHits = new List<nint>();
        foreach (var (regBase, regSize) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
        {
            for (long o = 0; o < regSize && wrapHits.Count < 40; o += buf.Length)
            {
                var want = (int)Math.Min(buf.Length, regSize - o);
                if (reader.TryReadBytes(regBase + (nint)o, buf.AsSpan(0, want)) <= 0) continue;
                for (var i = 0; i + 8 <= want; i += 8)
                    if (BitConverter.ToInt64(buf, i) == (long)bestStruct) wrapHits.Add(regBase + (nint)(o + i));
            }
            if (wrapHits.Count >= 40) break;
        }
        Console.WriteLine($"{wrapHits.Count} reference(s) to the wrapper:");
        foreach (var hit in wrapHits.Take(20))
        {
            // Is `hit` a vector slot? read the surrounding qwords; show neighbors + whether nearby is a StdVector header.
            reader.TryReadStruct<nint>(hit - 0x0, out var v0);
            Console.Write($"  @0x{hit:X}");
            // Treat hit as possibly inside a slot array: look 0x18 back for a plausible vector header (First ≤ hit ≤ Last).
            for (var b = 0; b <= 0x40; b += 8)
            {
                var f = SafePtr(reader, hit - b);          // candidate First
                if (!reader.TryReadStruct<nint>(hit - b + 8, out var l)) continue;
                if (ModIsPtr(f) && (long)l > (long)f && (long)hit >= (long)f && (long)hit < (long)l && ((long)l - (long)f) <= 0x8000)
                { Console.Write($"  [vec First=0x{f:X} Last=0x{l:X} headerAt=hit-0x{b:X}]"); break; }
            }
            Console.WriteLine();
        }
    }
    return 0;
}

static string? ItemArtBasename(string path)
{
    if (string.IsNullOrEmpty(path)) return null;
    var slash = path.LastIndexOf('/'); var start = slash >= 0 ? slash + 1 : 0;
    var dot = path.LastIndexOf('.'); var end = dot > start ? dot : path.Length;
    return end > start ? path[start..end] : null;
}

// ── Mods: read the monster modifier id strings out of ObjectMagicProperties ────────────────
// The affix-vector offset inside OMP is not in the validated table and drifts with patches, so
// this probe (a) tests the seed layouts ported from the Auras plugin (rarity mods @+0x150,
// affixes @+0x168; elem 0x20, ptr@+0x8, id at record+0x0) and (b) brute-force discovers any
// vector in the first 0x300 bytes whose elements lead to mod-id-looking UTF-16 strings. Run it
// with a few Magic/Rare/Unique monsters on screen; the goal is to lock the affix offset so the
// overlay can read it from Poe2Offsets instead of brute-forcing at runtime.
static bool ModIsPtr(nint p) => (ulong)p > 0x10000 && (ulong)p < 0x7FFF_FFFF_0000;

static string? ModTryName(MemoryReader reader, nint strPtr)
{
    if (!ModIsPtr(strPtr)) return null;
    string s;
    try { s = reader.ReadStringUtf16(strPtr, 64); } catch { return null; }
    if (s.Length is < 3 or > 64) return null;
    var hasLetter = false;
    foreach (var c in s)
    {
        if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) { hasLetter = true; continue; }
        if (c is (>= '0' and <= '9') or '_') continue;
        return null;
    }
    return hasLetter ? s : null;
}

static List<string> ModReadNames(MemoryReader reader, nint comp, VecLayout l)
{
    var res = new List<string>();
    if (comp == 0 || !reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(comp + l.VecOff, out var v)) return res;
    var len = (long)v.Last - (long)v.First;
    if (!ModIsPtr(v.First) || len <= 0 || len > 0x4000 || len % l.ElemSize != 0) return res;
    var n = (int)(len / l.ElemSize);
    if (n > 100) return res;
    for (var i = 0; i < n; i++)
    {
        var p = SafePtr(reader, v.First + (nint)(i * l.ElemSize + l.SlotA));
        if (!ModIsPtr(p)) continue;
        var q = l.SlotB < 0 ? p : SafePtr(reader, p + l.SlotB);
        var s = ModTryName(reader, q);
        if (s != null && !res.Contains(s)) res.Add(s);
    }
    return res;
}

static List<(VecLayout Layout, List<string> Names)> ModDiscover(MemoryReader reader, nint comp)
{
    var found = new List<(VecLayout, List<string>)>();
    if (comp == 0) return found;
    int[] elemSizes = [8, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40];
    int[] slotBs = [-1, 0x0, 0x8, 0x10, 0x18];
    for (var off = 0x10; off <= 0x2F8; off += 8)
    {
        if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(comp + off, out var v)) continue;
        var len = (long)v.Last - (long)v.First;
        if (!ModIsPtr(v.First) || len < 8 || len > 0x4000) continue;
        if ((long)v.End < (long)v.Last) continue;

        (VecLayout, List<string>)? bestHere = null;
        foreach (var es in elemSizes)
        {
            if (len % es != 0) continue;
            var n = (int)(len / es);
            if (n is < 1 or > 100) continue;
            for (var slotA = 0; slotA + 8 <= es; slotA += 8)
                foreach (var slotB in slotBs)
                {
                    var l = new VecLayout(off, es, slotA, slotB);
                    var names = ModReadNames(reader, comp, l);
                    if (names.Count < 1 || names.Count * 2 < n) continue;
                    if (!names.Any(s => s[0] is >= 'A' and <= 'Z' && s.Any(c => c is >= 'a' and <= 'z'))) continue;
                    if (bestHere == null || names.Count > bestHere.Value.Item2.Count) bestHere = (l, names);
                }
        }
        if (bestHere != null) found.Add(bestHere.Value);
    }
    return found;
}

static int RunMods(ProcessHandle process, MemoryReader reader, int minRarity, int maxMonsters)
{
    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }

    var head = SafePtr(reader, ai + Poe2.AreaInstance.AwakeEntities);
    if (head == 0) { Console.Error.WriteLine("no awake entities"); return 1; }

    VecLayout[] seeds = [new(0x150, 0x20, 0x8, 0x0), new(0x168, 0x20, 0x8, 0x0)];
    Console.WriteLine($"--mods: minRarity={minRarity} (0 Normal/1 Magic/2 Rare/3 Unique), up to {maxMonsters} monsters.");
    Console.WriteLine($"seed layouts: {string.Join("  ", seeds.Select(s => $"+0x{s.VecOff:X}(es 0x{s.ElemSize:X},a 0x{s.SlotA:X},b 0x{s.SlotB:X})"))}\n");

    var queue = new Queue<nint>(); queue.Enqueue(SafePtr(reader, head + Poe2.StdMapNode.Parent));
    var visited = new HashSet<nint>();
    var seedHits = new Dictionary<int, int>();   // VecOff → monsters where seed layout yielded names
    var discHits = new Dictionary<int, int>();    // VecOff → monsters where discovery yielded names
    var shown = 0;

    while (queue.Count > 0 && visited.Count < 200000 && shown < maxMonsters)
    {
        var node = queue.Dequeue();
        if (node == 0 || node == head || !visited.Add(node)) continue;
        if (!reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
        var entity = SafePtr(reader, node + Poe2.StdMapNode.ValueEntityPtr);
        reader.TryReadStruct<uint>(node + Poe2.StdMapNode.KeyId, out var id);
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Left));
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Right));
        if (entity == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;

        var meta = ReadEntityMetadata(reader, entity);
        if (!meta.Contains("/Monsters/", StringComparison.Ordinal)) continue;

        var omp = ResolveComponentAddr(reader, entity, "ObjectMagicProperties");
        if (omp == 0) continue;
        if (!reader.TryReadStruct<int>(omp + Poe2.ObjectMagicProperties.Rarity, out var rarity)) continue;
        if (rarity < minRarity) continue;

        shown++;
        var shortMeta = meta.Contains("/Monsters/") ? meta[(meta.IndexOf("/Monsters/", StringComparison.Ordinal) + 1)..] : meta;
        Console.WriteLine($"#{shown}  rarity={rarity}  OMP=0x{omp:X}  {shortMeta}");

        foreach (var s in seeds)
        {
            var names = ModReadNames(reader, omp, s);
            if (names.Count > 0) { seedHits[s.VecOff] = seedHits.GetValueOrDefault(s.VecOff) + 1;
                Console.WriteLine($"    seed +0x{s.VecOff:X}: {string.Join(", ", names)}"); }
        }
        foreach (var (layout, found) in ModDiscover(reader, omp))
        {
            discHits[layout.VecOff] = discHits.GetValueOrDefault(layout.VecOff) + 1;
            Console.WriteLine($"    disc +0x{layout.VecOff:X} (es 0x{layout.ElemSize:X}, a 0x{layout.SlotA:X}, b {(layout.SlotB < 0 ? "direct" : $"0x{layout.SlotB:X}")}): {string.Join(", ", found.Take(12))}");
        }
        Console.WriteLine();
    }

    Console.WriteLine($"scanned {shown} monster(s) (rarity >= {minRarity}).");
    Console.WriteLine("seed-layout hit counts:  " + (seedHits.Count == 0 ? "(none)" : string.Join("  ", seedHits.OrderBy(k => k.Key).Select(k => $"+0x{k.Key:X}×{k.Value}"))));
    Console.WriteLine("discovered vec hit counts: " + (discHits.Count == 0 ? "(none)" : string.Join("  ", discHits.OrderBy(k => k.Key).Select(k => $"+0x{k.Key:X}×{k.Value}"))));
    return 0;
}

// ── Validate the read-only fork ports against the live client in one pass ──────────────────
// Confirms (1) ZoneGuide area-name resolution, (2) EntityNameResolver entity names, and (3) the
// transcribed ✗ component offsets (Chest.Locked/Large, Monster.IsBoss, Targetable, Pathfinding.
// BaseSpeed, AreaTransition timers). Read-only: walks entities, resolves components, dumps the
// candidate offsets + a hex window so the values can be eyeballed against known ground truth.
static int RunValidate(ProcessHandle process, MemoryReader reader, int perBucket)
{
    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }

    // (1) ZoneGuide: raw area code → friendly name / act / level.
    var areaInfo = SafePtr(reader, ai + Poe2.AreaInstance.AreaInfoPtr);
    var code = reader.ReadStringUtf16(SafePtr(reader, areaInfo), 64);
    var za = ZoneGuide.Shared.Area(code);
    Console.WriteLine("=== ZoneGuide ===");
    Console.WriteLine($"  areaCode = '{code}'  →  name='{ZoneGuide.Shared.FriendlyName(code)}'"
        + (za is { } z ? $"  act={z.Act} level={z.Level} waypoint={z.Waypoint} town={z.Town}" : "  (NOT in world_areas)"));

    // Walk awake entities into category buckets.
    var head = SafePtr(reader, ai + Poe2.AreaInstance.AwakeEntities);
    reader.TryReadStruct<int>(ai + Poe2.AreaInstance.AwakeEntities + 8, out var size);
    if (head == 0 || size <= 0) { Console.Error.WriteLine("no awake entities"); return 1; }

    var monsters = new List<nint>(); var chests = new List<nint>(); var transitions = new List<nint>();
    var queue = new Queue<nint>(); queue.Enqueue(SafePtr(reader, head + Poe2.StdMapNode.Parent));
    var visited = new HashSet<nint>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var node = queue.Dequeue();
        if (node == 0 || node == head || !visited.Add(node)) continue;
        if (!reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
        reader.TryReadStruct<uint>(node + Poe2.StdMapNode.KeyId, out var id);
        var ent = SafePtr(reader, node + Poe2.StdMapNode.ValueEntityPtr);
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Left));
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Right));
        if (ent == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;
        var meta = ReadEntityMetadata(reader, ent);
        if (meta.Contains("/Monsters/", StringComparison.Ordinal)) { if (monsters.Count < perBucket) monsters.Add(ent); }
        else if (meta.Contains("/Chests", StringComparison.Ordinal)) { if (chests.Count < perBucket) chests.Add(ent); }
        else if (meta.Contains("Transition", StringComparison.Ordinal)) { if (transitions.Count < perBucket) transitions.Add(ent); }
    }

    // (2) EntityNameResolver sample across all buckets.
    Console.WriteLine("\n=== EntityNameResolver (metadata → friendly) ===");
    foreach (var ent in monsters.Concat(chests).Concat(transitions).Take(12))
    {
        var meta = ReadEntityMetadata(reader, ent);
        Console.WriteLine($"  {EntityNameResolver.Shared.ResolveOrShorten(meta),-32} ← {meta}");
    }

    // Dump the full component-name list for one chest + one monster so the real component names
    // backing the ✗ offsets are visible (guessed names below may need correcting).
    if (chests.Count > 0) DumpComponentNames(reader, chests[0], "CHEST");
    if (monsters.Count > 0) DumpComponentNames(reader, monsters[0], "MONSTER");

    // (3a) Chest offsets — the magic unopened chest is ground truth (OpenState should be 1=closed).
    Console.WriteLine("\n=== Chest offsets (OpenState ✓0x168 | ✗ Locked 0x25 / Large 0x21 / OpeningDestroys 0x20) ===");
    foreach (var ent in chests)
    {
        var meta = ReadEntityMetadata(reader, ent);
        var c = ResolveComponentAddr(reader, ent, "Chest");
        var omp = ResolveComponentAddr(reader, ent, "ObjectMagicProperties");
        var rarity = omp != 0 && reader.TryReadStruct<int>(omp + Poe2.ObjectMagicProperties.Rarity, out var r) ? r : -1;
        Console.WriteLine($"  {EntityNameResolver.Shared.ResolveOrShorten(meta)}  rarity={rarity}  chestComp=0x{c:X}");
        if (c == 0) { Console.WriteLine("    (no Chest component)"); continue; }
        Console.WriteLine($"    OpenState(+0x168)={B(reader, c + 0x168)}  Locked(+0x25)={B(reader, c + 0x25)}"
            + $"  Large(+0x21)={B(reader, c + 0x21)}  OpeningDestroys(+0x20)={B(reader, c + 0x20)}");
        DumpWindow(reader, c, 0x40, "    ");
    }

    // (3b) Monster offsets.
    Console.WriteLine("\n=== Monster offsets (✗ IsBoss 0x27 | Targetable IsTargetable 0x18 / Attackable 0x17 | Pathfinding BaseSpeed 0xEC int / Flying 0xE5) ===");
    foreach (var ent in monsters)
    {
        var meta = ReadEntityMetadata(reader, ent);
        var mon = ResolveComponentAddr(reader, ent, "Monster");
        var tgt = ResolveComponentAddr(reader, ent, "Targetable");
        var pf  = ResolveComponentAddr(reader, ent, "Pathfinding");
        var omp = ResolveComponentAddr(reader, ent, "ObjectMagicProperties");
        var rarity = omp != 0 && reader.TryReadStruct<int>(omp + Poe2.ObjectMagicProperties.Rarity, out var r) ? r : -1;
        var rname = rarity switch { 0 => "Normal", 1 => "Magic", 2 => "Rare", 3 => "UNIQUE", _ => "?" };
        Console.WriteLine($"  [{rname}] {EntityNameResolver.Shared.ResolveOrShorten(meta)}  ({meta})");
        // For bosses/uniques, also dump the Monster component head so the real boss flag can be spotted
        // if 0x27 isn't it.
        if (mon != 0 && rarity == 3) DumpWindow(reader, mon, 0x40, "      mon ");
        Console.WriteLine($"    Monster=0x{mon:X} IsBoss(+0x27)={B(reader, mon + 0x27)}   "
            + $"Targetable=0x{tgt:X} IsTargetable(+0x18)={B(reader, tgt + 0x18)} Attackable(+0x17)={B(reader, tgt + 0x17)}");
        Console.WriteLine($"    Pathfinding=0x{pf:X} BaseSpeed(+0xEC)={I(reader, pf + 0xEC)} Flying(+0xE5)={B(reader, pf + 0xE5)}");
    }

    // (3c) Area transitions.
    if (transitions.Count > 0)
    {
        Console.WriteLine("\n=== AreaTransition offsets (✗ GracePeriod 0x18 float / TeleportDelay 0x1C float) ===");
        foreach (var ent in transitions)
        {
            var meta = ReadEntityMetadata(reader, ent);
            var at = ResolveComponentAddr(reader, ent, "AreaTransition");
            Console.WriteLine($"  {EntityNameResolver.Shared.ResolveOrShorten(meta)}  AreaTransition=0x{at:X}"
                + (at != 0 ? $"  GracePeriod(+0x18)={F(reader, at + 0x18)} TeleportDelay(+0x1C)={F(reader, at + 0x1C)}" : ""));
        }
    }
    return 0;
}

static void DumpComponentNames(MemoryReader reader, nint entity, string label)
{
    Console.WriteLine($"\n=== {label} component names @ 0x{entity:X} ({ReadEntityMetadata(reader, entity)}) ===");
    var details = SafePtr(reader, entity + Poe2.Entity.EntityDetailsPtr);
    var lookup = details == 0 ? 0 : SafePtr(reader, details + Poe2.EntityDetails.ComponentLookUpPtr);
    if (lookup == 0) { Console.WriteLine("  (no component lookup)"); return; }
    if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(entity + Poe2.Entity.ComponentList, out var cl)) return;
    var bFirst = SafePtr(reader, lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
    if (!reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return;
    var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
    if (bFirst == 0 || entries is <= 0 or > 256) { Console.WriteLine("  (implausible entry count)"); return; }
    var names = new List<string>();
    for (long i = 0; i < entries; i++)
    {
        var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
        var nm = reader.ReadStringUtf8(SafePtr(reader, e), 40);
        if (!string.IsNullOrEmpty(nm)) names.Add(nm);
    }
    Console.WriteLine("  " + string.Join(", ", names.OrderBy(x => x, StringComparer.Ordinal)));
}

static void DumpWindow(MemoryReader reader, nint addr, int len, string indent)
{
    var buf = new byte[len];
    if (reader.TryReadBytes(addr, buf) != len) { Console.WriteLine($"{indent}(read failed)"); return; }
    for (var i = 0; i < len; i += 16)
        Console.WriteLine($"{indent}+0x{i:X2}  {string.Join(' ', Enumerable.Range(0, 16).Select(j => buf[i + j].ToString("X2")))}");
}

static int B(MemoryReader reader, nint addr) => reader.TryReadStruct<byte>(addr, out var b) ? b : -1;
static int I(MemoryReader reader, nint addr) => reader.TryReadStruct<int>(addr, out var v) ? v : -1;
static float F(MemoryReader reader, nint addr) => reader.TryReadStruct<float>(addr, out var v) ? v : float.NaN;

// Resolve a component address by name (same StdBucket walk as Poe2Live, inline for probes).
static nint ResolveComponentAddr(MemoryReader reader, nint entity, string name)
{
    var details = SafePtr(reader, entity + Poe2.Entity.EntityDetailsPtr);
    if (details == 0) return 0;
    var lookup = SafePtr(reader, details + Poe2.EntityDetails.ComponentLookUpPtr);
    if (lookup == 0) return 0;
    if (!reader.TryReadStruct<POE2Radar.Core.Game.StdVector>(entity + Poe2.Entity.ComponentList, out var cl)) return 0;
    var compCount = ((long)cl.Last - (long)cl.First) / 8;
    if (compCount is <= 0 or > 256) return 0;
    var bFirst = SafePtr(reader, lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
    if (!reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return 0;
    var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
    if (bFirst == 0 || entries is <= 0 or > 256) return 0;
    for (long i = 0; i < entries; i++)
    {
        var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
        if (!reader.TryReadStruct<int>(e + 8, out var index) || index < 0 || index >= compCount) continue;
        if (reader.ReadStringUtf8(SafePtr(reader, e), 40) != name) continue;
        return SafePtr(reader, cl.First + (nint)(index * 8));
    }
    return 0;
}

// ── Tiles: read the terrain tile grid (GameHelper2 GetTgtFileData) — each tile's TgtPath →
// grid positions. Shows what static tile-based landmarks exist (boss arenas, special rooms,
// waypoints) and whether a per-tile semantic "detail name" is reachable. TerrainStruct @
// AreaInstance+0x8A0: TotalTiles@+0x18, TileDetailsPtr StdVector@+0x28 (TileStructure=0x38);
// TileStructure.TgtFilePtr@+0x8 → TgtFileStruct.TgtPath (StdWString)@+0x8.
static int RunTiles(ProcessHandle process, MemoryReader reader)
{
    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    var terrain = ai + 0x8A0;
    reader.TryReadStruct<long>(terrain + 0x18, out var tilesX);
    reader.TryReadStruct<nint>(terrain + 0x28, out var first);
    reader.TryReadStruct<nint>(terrain + 0x30, out var last);
    var count = first == 0 ? 0 : ((long)last - (long)first) / 0x38;
    Console.WriteLine($"AreaInstance 0x{ai:X}  terrain 0x{terrain:X}  tilesX={tilesX}  tileCount={count}");
    if (count is <= 0 or > 200000) { Console.Error.WriteLine("implausible tile count"); return 1; }

    // Dump the first non-empty tile's TgtFileStruct so we can look for a semantic detail-name ptr.
    var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
    nint sampleTgt = 0;
    for (long i = 0; i < count; i++)
    {
        var tile = first + (nint)(i * 0x38);
        var tgtFile = SafePtr(reader, tile + 0x8);
        if (tgtFile == 0) continue;
        var path = ReadStdWString(reader, tgtFile + 0x8);
        if (path.Length == 0) continue;
        if (sampleTgt == 0) sampleTgt = tgtFile;
        byPath[path] = byPath.GetValueOrDefault(path) + 1;
    }
    Console.WriteLine($"distinct tile paths: {byPath.Count}");
    Console.WriteLine("\n--- paths matching boss/arena/unique/waypoint/mechanic/encounter ---");
    foreach (var kv in byPath.Where(k => k.Key.Contains("oss", StringComparison.OrdinalIgnoreCase)
            || k.Key.Contains("rena", StringComparison.OrdinalIgnoreCase)
            || k.Key.Contains("nique", StringComparison.OrdinalIgnoreCase)
            || k.Key.Contains("aypoint", StringComparison.OrdinalIgnoreCase)
            || k.Key.Contains("ncounter", StringComparison.OrdinalIgnoreCase)
            || k.Key.Contains("itual", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(k => k.Value))
        Console.WriteLine($"  {kv.Value,4}  {kv.Key}");

    Console.WriteLine("\n--- ALL distinct tile paths (alphabetical) ---");
    foreach (var kv in byPath.OrderBy(k => k.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {kv.Value,5}  {kv.Key}");

    if (sampleTgt != 0)
    {
        Console.WriteLine($"\n--- sample TgtFileStruct @ 0x{sampleTgt:X} (+0x00..+0x60; look for a detail-name ptr) ---");
        var buf = new byte[0x60];
        if (reader.TryReadBytes(sampleTgt, buf) == buf.Length)
            for (var i = 0; i < buf.Length; i += 16)
                Console.WriteLine($"  +0x{i:X2}  {string.Join(' ', Enumerable.Range(0, 16).Select(j => buf[i + j].ToString("X2")))}");
    }
    return 0;
}

// ── Tile-find: list the GRID positions of every terrain tile whose TgtPath contains <needle>.
// Answers "is this feature actually in the static tile grid, and WHERE?" — and exposes whether a
// tile type is a single landmark or a reusable piece scattered across the map (whose averaged
// centroid would be meaningless). Prints each instance's grid pos + the cluster count, bounding
// box, and centroid so a scattered-vs-clustered tile is obvious at a glance.
static int RunTileFind(ProcessHandle process, MemoryReader reader, string needle)
{
    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    var terrain = ai + Poe2.AreaInstance.TerrainMetadata;
    reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX);
    var first = SafePtr(reader, terrain + Poe2.Terrain.TileDetailsPtr);
    reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var last);
    var count = first == 0 ? 0 : ((long)last - (long)first) / Poe2.TileStructureSize;
    if (tilesX <= 0 || count is <= 0 or > 1_000_000) { Console.Error.WriteLine("implausible tile grid"); return 1; }
    Console.WriteLine($"AreaInstance 0x{ai:X}  tilesX={tilesX}  tileCount={count}  needle='{needle}'");

    var cell = Poe2.Terrain.TileGridCells;
    var pathCache = new Dictionary<nint, string?>();
    var hits = new List<(int gx, int gy, string path)>();
    for (long i = 0; i < count; i++)
    {
        var tile = first + (nint)(i * Poe2.TileStructureSize);
        var tgt = SafePtr(reader, tile + Poe2.TileStructure.TgtFilePtr);
        if (tgt == 0) continue;
        if (!pathCache.TryGetValue(tgt, out var path))
        {
            var p = ReadStdWString(reader, tgt + Poe2.TgtFileStruct.TgtPath);
            path = p.Contains(needle, StringComparison.OrdinalIgnoreCase) ? p : null;
            pathCache[tgt] = path;
        }
        if (path is null) continue;
        hits.Add(((int)((i % tilesX) * cell), (int)((i / tilesX) * cell), path));
    }

    if (hits.Count == 0) { Console.WriteLine("no matching tiles."); return 0; }
    Console.WriteLine($"\n{hits.Count} matching tile(s) — grid positions:");
    foreach (var h in hits.OrderBy(h => h.gy).ThenBy(h => h.gx))
        Console.WriteLine($"  ({h.gx,5},{h.gy,5})  {h.path}");
    int minx = hits.Min(h => h.gx), maxx = hits.Max(h => h.gx);
    int miny = hits.Min(h => h.gy), maxy = hits.Max(h => h.gy);
    Console.WriteLine($"\ncentroid ({(int)hits.Average(h => h.gx)},{(int)hits.Average(h => h.gy)})  " +
        $"bbox ({minx},{miny})-({maxx},{maxy})  span {maxx - minx}x{maxy - miny}");
    Console.WriteLine("(a large span = a reusable tile scattered across the map: its averaged centroid is meaningless.)");
    return 0;
}

// ── DevTree: launch the browser-based live memory/UI/entity explorer. Locks the GameState slot
// once (AOB, validated in-game), then serves DevTreeServer until Ctrl+C. The slot stays valid
// across zoning, so you only need to be in an area at launch; the server re-resolves the chain live
// per request. See DevTree/DevTreeServer.cs.
static int RunDevTree(ProcessHandle process, MemoryReader reader, int port)
{
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
    {
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
        if (slot != 0) break;
    }
    if (slot == 0) { Console.Error.WriteLine("Could not lock GameState slot — load into an area, then relaunch."); return 1; }

    using var server = new DevTreeServer(reader, slot, port);
    try { server.Start(); }
    catch (Exception ex) { Console.Error.WriteLine($"Could not start server on port {port}: {ex.Message}"); return 1; }
    Console.WriteLine($"DevTree (GameState slot 0x{slot:X}) serving at http://localhost:{port}/");
    Console.WriteLine("Open it in a browser. Ctrl+C to stop.");
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

// ── Watch: poll as the player plays, logging an AreaInstance snapshot on every area
// change so the area-hash/level offsets can be diffed out across zones. Resolves the
// GameState slot once (AOB), then cheap chain derefs each poll. Run in the background and
// inspect the log. Each area block also reads a few candidate fields so drift is obvious.
static int RunWatch(ProcessHandle process, MemoryReader reader)
{
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
        {
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
            if (slot != 0) break;
        }
    if (slot == 0) { Console.Error.WriteLine("Could not lock GameState slot (in game?)."); return 1; }
    var live = new Poe2Live(reader, slot);
    Console.WriteLine($"WATCH started, GameState slot 0x{slot:X16}. Logging on area change. Ctrl+C to stop.");

    nint prevArea = 0; var idx = 0;
    while (true)
    {
        if (live.TryResolve(out var igs, out var ai, out var lp) && ai != prevArea)
        {
            prevArea = ai;
            idx++;
            var meta = "";
            { var d = reader.TryReadStruct<nint>(lp + Poe2.Entity.EntityDetailsPtr, out var dp) ? dp : 0;
              if (d != 0) meta = ReadStdWString(reader, d + Poe2.EntityDetails.Name); }
            Console.WriteLine($"\n##### AREA #{idx}  AreaInstance=0x{ai:X16}  player={meta}  (t={Environment.TickCount64}) #####");
            // Candidate fields (GH2): level byte @0xBC, hash uint @0xFC — likely drifted.
            reader.TryReadStruct<byte>(ai + 0xBC, out var ghLvl);
            reader.TryReadStruct<uint>(ai + 0xFC, out var ghHash);
            Console.WriteLine($"  GH2 guesses: level@0xBC={ghLvl}  hash@0xFC=0x{ghHash:X8}");
            // Dump 0x00..0x200 so the changing uint (hash) + a 1..100 byte (level) can be found.
            var buf = new byte[0x200];
            if (reader.TryReadBytes(ai, buf) == buf.Length)
                for (var i = 0; i < buf.Length; i += 16)
                {
                    var hex = string.Join(' ', Enumerable.Range(0, 16).Select(j => buf[i + j].ToString("X2")));
                    Console.WriteLine($"  +0x{i:X3}  {hex}");
                }
        }
        Thread.Sleep(1500);
    }
}

// ── Watch-expedition: poll the live entity list, re-resolving expedition-related entities each
// tick (robust to address recycling), and log whenever the set OR any per-entity signal changes:
//   poi  = MinimapIcon component present (what we draw as a map icon)
//   sm   = StateMachine state int @ +0x10 (the candidate "event phase" field)
//   hp   = Life current/max
// Run it across an expedition (ready → place charges → detonate → loot → done) to see which signal
// flips when the icon should hide, and which extra entities get tagged while it's active.
static int RunWatchExpedition(ProcessHandle process, MemoryReader reader)
{
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
    if (slot == 0) { Console.Error.WriteLine("Could not lock GameState slot (in game?)."); return 1; }
    var live = new Poe2Live(reader, slot);
    Console.WriteLine($"WATCH-EXPEDITION started (slot 0x{slot:X}). Logging on change. Ctrl+C to stop.");

    var prev = new Dictionary<uint, string>();
    while (true)
    {
        if (live.TryResolve(out _, out var ai, out _))
        {
            var cur = new Dictionary<uint, string>();
            foreach (var (id, ent, meta) in WalkEntities(reader, ai))
            {
                if (!meta.Contains("xpedition", StringComparison.OrdinalIgnoreCase)) continue;
                var sm = ResolveComponentAddr(reader, ent, "StateMachine");
                var smState = sm != 0 && reader.TryReadStruct<int>(sm + 0x10, out var v) ? v.ToString() : "-";
                var poi = ResolveComponentAddr(reader, ent, "MinimapIcon") != 0;
                var life = ResolveComponentAddr(reader, ent, "Life");
                var hp = life != 0 && reader.TryReadStruct<POE2Radar.Core.Game.VitalStruct>(life + 0x1A8, out var h) ? $"{h.Current}/{h.Max}" : "-";
                cur[id] = $"poi={poi} sm={smState} hp={hp} {meta}";
            }

            // Log additions / removals / changed signals.
            foreach (var (id, line) in cur)
                if (!prev.TryGetValue(id, out var old) || old != line)
                    Console.WriteLine($"[t={Environment.TickCount64}] id={id,-5} {line}");
            foreach (var id in prev.Keys)
                if (!cur.ContainsKey(id))
                    Console.WriteLine($"[t={Environment.TickCount64}] id={id,-5} REMOVED ({prev[id]})");
            prev = cur;
        }
        Thread.Sleep(750);
    }
}

// ── Ritual: find the signal that flips when a ritual altar is COMPLETED ───────────────────────────
// Ritual altars don't fade their MinimapIcon (MinimapIcon.CompletedState stays 0), so the overlay's
// generic "Hide completed encounters" rule never catches them. This probe dumps every non-monster
// ritual entity in the area side-by-side so the discriminating field jumps out: stand in a map with a
// MIX of completed and not-yet-started ritual altars and run `--ritual`. It prints, per altar (sorted
// nearest-first — the active one is usually the nearest), the component list plus the obvious
// completion candidates (MinimapIcon.CompletedState, StateMachine state, Targetable byte) and a raw
// hex dump of the StateMachine + MinimapIcon components. Compare the completed altars vs the active one
// to spot the field that differs. Add `--watch` to re-poll on change (to catch the moment one flips).
static int RunRitual(ProcessHandle process, MemoryReader reader, bool watch)
{
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
    if (slot == 0) { Console.Error.WriteLine("Could not lock GameState slot (in game?)."); return 1; }
    var live = new Poe2Live(reader, slot);

    void DumpOnce()
    {
        if (!live.TryResolve(out _, out var ai, out var lp)) { Console.Error.WriteLine("Could not resolve area."); return; }
        var pg = EntityGrid(reader, lp);

        var altars = new List<(uint id, nint ent, string meta, float dist, System.Numerics.Vector2 g)>();
        var lights = new List<(uint id, System.Numerics.Vector2 g)>();
        foreach (var (id, ent, meta) in WalkEntities(reader, ai))
        {
            if (meta.IndexOf("itual", StringComparison.OrdinalIgnoreCase) < 0) continue; // [Rr]itual
            if (meta.Contains("/Monsters/", StringComparison.Ordinal)) continue;          // skip league mobs
            var g = EntityGrid(reader, ent);
            var d = g == default ? 9999f : (float)Math.Sqrt((g.X - pg.X) * (g.X - pg.X) + (g.Y - pg.Y) * (g.Y - pg.Y));
            altars.Add((id, ent, meta, d, g));
            if (meta.IndexOf("RitualRuneLight", StringComparison.OrdinalIgnoreCase) >= 0) lights.Add((id, g));
        }
        altars.Sort((a, b) => a.dist.CompareTo(b.dist));

        Console.WriteLine($"\n=== {altars.Count} non-monster ritual entities ({lights.Count} RitualRuneLight) ===");
        Console.WriteLine($"player grid ({pg.X:F0},{pg.Y:F0})  lights: {string.Join(" | ", lights.Select(l => $"id{l.id}@({l.g.X:F0},{l.g.Y:F0})"))}");
        foreach (var (id, ent, meta, dist, g) in altars)
        {
            // For each RitualRuneObject, distance to the NEAREST light (the "is this the active one" test).
            var nearLight = lights.Count == 0 ? -1f
                : lights.Min(l => (float)Math.Sqrt((l.g.X - g.X) * (l.g.X - g.X) + (l.g.Y - g.Y) * (l.g.Y - g.Y)));
            Console.WriteLine($"\nid={id} ent=0x{ent:X} grid=({g.X:F0},{g.Y:F0}) dist={dist:F0} nearestLight={nearLight:F0}  {meta}");
            DumpComponentList(reader, ent, "  ");

            var icon = ResolveComponentAddr(reader, ent, "MinimapIcon");
            if (icon != 0)
            {
                reader.TryReadStruct<int>(icon + Poe2.MinimapIcon.CompletedState, out var cs);
                Console.WriteLine($"  MinimapIcon 0x{icon:X}  CompletedState(+0x10)={cs}");
                DumpInts(reader, icon, 0x40, "    MinimapIcon");
            }
            var sm = ResolveComponentAddr(reader, ent, "StateMachine");
            if (sm != 0)
            {
                reader.TryReadStruct<int>(sm + 0x10, out var st);
                Console.WriteLine($"  StateMachine 0x{sm:X}  state(+0x10)={st}");
                DumpInts(reader, sm, 0x60, "    StateMachine");
            }
            var tgt = ResolveComponentAddr(reader, ent, "Targetable");
            if (tgt != 0)
            {
                Console.WriteLine($"  Targetable 0x{tgt:X}");
                DumpInts(reader, tgt, 0x20, "    Targetable");
            }
        }
    }

    if (!watch) { DumpOnce(); return 0; }

    // Watch mode: snapshot a wide byte window of each ritual altar's components and report only the
    // 4-byte words that CHANGE. Pointers (vtables etc.) are stable per fixed entity address over a short
    // window, so a completion event shows up as a clean flip of one or two scalar fields. Components
    // watched cover the likely homes of a "completed" flag. Run, then complete the nearest ritual.
    string[] watchComps = { "StateMachine", "Stats", "Life", "Animated", "Buffs", "BaseEvents", "InteractionAction" };
    const int span = 0x100;
    var prev = new Dictionary<(uint id, string comp), int[]>();
    Console.WriteLine("RITUAL watch — logging changed component words. Complete the nearest ritual now. Ctrl+C to stop.\n");
    if (!live.TryResolve(out _, out var ai0, out _)) { Console.Error.WriteLine("no area"); return 1; }
    while (true)
    {
        if (live.TryResolve(out _, out var ai, out _))
        {
            foreach (var (id, ent, meta) in WalkEntities(reader, ai))
            {
                if (meta.IndexOf("Ritual/RitualRune", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var comp in watchComps)
                {
                    var addr = ResolveComponentAddr(reader, ent, comp);
                    if (addr == 0) continue;
                    var buf = new byte[span];
                    if (reader.TryReadBytes(addr, buf) < span) continue;
                    var words = new int[span / 4];
                    for (var i = 0; i < words.Length; i++) words[i] = BitConverter.ToInt32(buf, i * 4);
                    var key = (id, comp);
                    if (prev.TryGetValue(key, out var old))
                        for (var i = 0; i < words.Length; i++)
                            if (words[i] != old[i])
                                Console.WriteLine($"[t={Environment.TickCount64}] id={id} ({meta.Split('/')[^1]}) {comp}+0x{i * 4:X2}: {old[i]} -> {words[i]}");
                    prev[key] = words;
                }
            }
        }
        Thread.Sleep(500);
    }
}

// ── Ritual TRIBUTE shop panel discovery ──────────────────────────────────────────────────────────
// The post-ritual "buy with tribute" shop is a UI panel (NOT a ServerData PlayerInventory — the 5x1
// reward inventories there read empty). So we attack it from the UiElement tree like --runeforge:
//   1) GameUi = Ptr(InGameState + UiRoot 0x2F0); full-tree DFS over Children (+0x10/+0x18 StdVector).
//   2) TEXT pass: every element's inline std::wstring @ +0x390 (the runeforge text offset). Report each
//      element whose text matches an anchor needle (the known cost "1,590"/"1590" + "tribute"/"ritual"/
//      "briar" + any --find <needle>), with its geometry and full ancestor chain (Parent +0xB8) — that
//      chain is the raw material for a flag-fingerprint resolve walk later.
//   3) ITEM pass: every element whose body holds a pointer to a Metadata/Items entity (the reward icon
//      slots) — print art basename + rarity + the ancestor chain, so each cost label can be tied to its
//      reward item (priced via art/name like the inventory path). Run with the tribute shop OPEN.
static int RunTribute(ProcessHandle process, MemoryReader reader, string? extraNeedle)
{
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
    if (slot == 0) { Console.Error.WriteLine("Could not lock GameState slot (in game?)."); return 1; }
    var live = new Poe2Live(reader, slot);
    if (!live.TryResolve(out var igs, out _, out _)) { Console.Error.WriteLine("Could not resolve InGameState."); return 1; }
    var gameUi = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    if (gameUi == 0) { Console.Error.WriteLine("GameUi (UiRoot) null."); return 1; }
    Console.WriteLine($"GameUi 0x{gameUi:X}");

    var needles = new List<string> { "1,590", "1590", "tribute", "ritual", "briar", "reroll", "defer", "favour", "sacrifice" };
    if (!string.IsNullOrWhiteSpace(extraNeedle)) needles.Add(extraNeedle.Trim());

    const int TextWStr = 0x390;
    const uint VisMask = 1u << Poe2.UiElement.FlagVisibleBit;

    // ── DFS the whole UiElement tree once, recording text-bearing + item-bearing elements. ──
    var textHits = new List<(nint el, int depth, uint flags, string text, bool needle)>();
    var itemHits = new List<(nint el, int depth, uint flags, nint item, string art, int rarity, string meta)>();
    var visited = new HashSet<nint>();
    var stack = new Stack<(nint el, int depth)>();
    stack.Push((gameUi, 0));
    var nodes = 0;
    while (stack.Count > 0 && nodes < 300_000)
    {
        var (el, depth) = stack.Pop();
        if (el == 0 || !visited.Add(el) || depth > 80) continue;
        nodes++;

        reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags);

        // Text: inline wstring @ +0x390.
        var text = ReadStdWString(reader, el + TextWStr);
        if (!string.IsNullOrEmpty(text) && text.Length <= 120 && IsMostlyPrintable(text))
        {
            var isNeedle = needles.Any(nd => text.Contains(nd, StringComparison.OrdinalIgnoreCase));
            textHits.Add((el, depth, flags, text, isNeedle));
        }

        // Item icon: a qword in the element body resolving to a Metadata/Items entity.
        var body = new byte[0x300];
        if (reader.TryReadBytes(el, body) >= body.Length)
            for (var o = 0x20; o + 8 <= body.Length; o += 8)
            {
                var cand = (nint)BitConverter.ToInt64(body, o);
                if ((ulong)cand is < 0x10000 or > 0x7FFFFFFFFFFF) continue;
                var meta = ReadEntityMetadata(reader, cand);
                if (!meta.StartsWith("Metadata/Items", StringComparison.Ordinal)) continue;
                var ri = ResolveComponentAddr(reader, cand, "RenderItem");
                var art = ri == 0 ? "" : ItemArtBasename(reader.ReadStringUtf16(SafePtr(reader, ri + Poe2.RenderItemComponent.ResourcePath), 128)) ?? "";
                var mods = ResolveComponentAddr(reader, cand, "Mods");
                var rarity = -1; if (mods != 0) reader.TryReadStruct<int>(mods + Poe2.ModsComponent.Rarity, out rarity);
                itemHits.Add((el, depth, flags, cand, art, rarity, meta));
                break; // one item ptr per element is enough to flag it
            }

        if (RfChildren(reader, el, out var first, out var n))
            for (long i = 0; i < n; i++)
                stack.Push((SafePtr(reader, first + (nint)(i * 8)), depth + 1));
    }
    Console.WriteLine($"walked {nodes} UI elements — {textHits.Count} text, {itemHits.Count} item-bearing\n");

    // ── Anchor needle matches (the cost labels / titles), with full ancestor chain. ──
    Console.WriteLine("=== NEEDLE matches (cost labels / panel text) ===");
    foreach (var h in textHits.Where(t => t.needle))
    {
        DumpUiEl(reader, h.el, h.depth, h.flags, $"\"{h.text}\"");
        DumpAncestors(reader, h.el);
    }
    if (!textHits.Any(t => t.needle)) Console.WriteLine("(no needle matches — is the tribute shop open? text may live at a different offset)");

    // ── Reward item icons, with ancestor chain. ──
    Console.WriteLine("\n=== ITEM-bearing elements (reward icons) ===");
    foreach (var h in itemHits)
    {
        var rar = h.rarity switch { 0 => "Normal", 1 => "Magic", 2 => "Rare", 3 => "Unique", _ => $"r{h.rarity}" };
        DumpUiEl(reader, h.el, h.depth, h.flags, $"item=0x{h.item:X} {rar} {h.art}  {h.meta}");
        DumpAncestors(reader, h.el);
    }
    if (itemHits.Count == 0) Console.WriteLine("(no item-bearing UI elements found)");

    // ── Full text dump (so we can eyeball the whole panel layout). ──
    Console.WriteLine("\n=== ALL visible text elements (depth-ordered) ===");
    foreach (var h in textHits.Where(t => (t.flags & VisMask) != 0).OrderBy(t => t.depth).ThenBy(t => (long)t.el))
        Console.WriteLine($"  d{h.depth,2} 0x{h.el:X} flags=0x{h.flags & ~VisMask:X}  \"{h.text}\"");
    return 0;
}

// ── Tribute reward struct finder — value-scan for a known item cost (e.g. 1590) ───────────────────
// The reward NAMES + COSTS are NOT in the UI text tree (names render on hover; costs aren't text), so
// the data lives in a server/data struct the UI renders from. We find it monolith-style: scan private
// heap for the int32 cost, and for each hit check a ±window for (a) a pointer to a Metadata/Items entity
// (the reward item) and (b) other plausible costs — that combination uniquely fingerprints the reward
// array. For each promising hit, dump the surrounding qwords + any item entity's art/rarity/name. Run
// with the tribute shop open; pass --cost N to scan a different known cost.
static int RunTributeScan(ProcessHandle process, MemoryReader reader, string costsCsv, int primary)
{
    var costs = costsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n > 0).Distinct().ToArray();
    if (costs.Length == 0) costs = new[] { primary };
    if (!costs.Contains(primary)) primary = costs[0];
    var others = costs.Where(c => c != primary).ToArray();
    Console.WriteLine($"Co-location scan: primary={primary}, others=[{string.Join(",", others)}].");
    Console.WriteLine($"Scanning private heap for int32=={primary}; for each hit, counting other costs within ±0x800…\n");

    // Match either the int or the float bit-pattern of a cost (we don't yet know the field type).
    var costBits = costs.ToDictionary(c => c, c => new[] { c, BitConverter.SingleToInt32Bits(c) });
    bool IsCost(int v, int c) => v == c || v == BitConverter.SingleToInt32Bits(c);
    var primBits = new HashSet<int>(costBits[primary]);

    var buf = new byte[1 << 20];
    var hits = new List<nint>();
    foreach (var (regBase, regSize) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
    {
        for (long o = 0; o < regSize && hits.Count < 400000; o += buf.Length - 8)
        {
            var want = (int)Math.Min(buf.Length, regSize - o);
            var got = reader.TryReadBytes(regBase + (nint)o, buf.AsSpan(0, want));
            if (got <= 0) continue;
            for (var i = 0; i + 4 <= got; i += 4)
                if (primBits.Contains(BitConverter.ToInt32(buf, i))) hits.Add(regBase + (nint)(o + i));
        }
    }
    Console.WriteLine($"raw primary hits (int OR float): {hits.Count}.\n");

    // All-five-in-a-window detector (no order/stride assumption): for each primary hit, require EVERY
    // other cost to appear within ±Win. The monotonic-counter false positives contain 1230/1395/1755 but
    // NOT 174, so demanding all four kills them; we also reject runs where an adjacent dword is primary±1.
    const int Win = 0x800;
    var arrays = new List<(nint hit, List<(int cost, int off)> where)>();
    foreach (var hit in hits)
    {
        reader.TryReadStruct<int>(hit - 4, out var pm); reader.TryReadStruct<int>(hit + 4, out var nx);
        if (pm == primary - 1 || nx == primary + 1 || pm == primary + 1 || nx == primary - 1) continue; // counter run

        var win = new byte[Win * 2];
        if (reader.TryReadBytes(hit - Win, win) < win.Length) continue;
        var where = new List<(int cost, int off)> { (primary, 0) };
        var all = true;
        foreach (var c in others)
        {
            var at = int.MinValue;
            for (var o = 0; o + 4 <= win.Length; o += 4)
                if (IsCost(BitConverter.ToInt32(win, o), c)) { at = o - Win; break; }
            if (at == int.MinValue) { all = false; break; }
            where.Add((c, at));
        }
        if (all) arrays.Add((hit, where));
    }
    Console.WriteLine($"{arrays.Count} hit(s) have ALL {costs.Length} costs within ±0x{Win:X}:\n");
    foreach (var f in arrays.Take(8))
    {
        var ordered = f.where.OrderBy(w => w.off).ToList();
        var lo = ordered.First().off - 0x10; var hi = ordered.Last().off + 0x18;
        Console.WriteLine($"★ COST CLUSTER near 0x{f.hit:X}  span 0x{hi - lo:X}");
        foreach (var (c, off) in ordered)
            Console.WriteLine($"     {c,6} @ 0x{f.hit + off:X}  (primary{(off >= 0 ? "+" : "")}0x{off:X})");
        Console.WriteLine("   ── qword layout across the cluster ──");
        DumpQwords(reader, f.hit + (lo & ~7), ((hi - (lo & ~7) + 15) & ~15), "     ");
        for (var o = (lo & ~7); o + 8 <= hi; o += 8)   // item-entity check across the cluster
        {
            if (!reader.TryReadStruct<nint>(f.hit + o, out var q)) continue;
            var r = AsItem(q);
            if (r is { } it)
            {
                var ri = ResolveComponentAddr(reader, it.item, "RenderItem");
                var art = ri == 0 ? "" : ItemArtBasename(reader.ReadStringUtf16(SafePtr(reader, ri + Poe2.RenderItemComponent.ResourcePath), 128)) ?? "";
                Console.WriteLine($"        → +0x{o:X} item=0x{it.item:X} {art}  {it.meta}");
            }
        }
        Console.WriteLine();
    }
    if (arrays.Count == 0)
        Console.WriteLine("No all-five cluster — costs may be far apart or stored as floats. See item-pointer pass below.\n");

    // Secondary: also report any primary hit with an item entity nearby (direct or one indirection).
    Console.WriteLine("── primary hits with an item entity within ±0x300 ──");

    // Resolve a candidate qword to a Metadata/Items entity, directly or via one dereference (slot struct
    // whose +0x00 is the Item entity, like InventoryItemStruct).
    (nint item, string meta)? AsItem(nint cand)
    {
        if ((ulong)cand is < 0x10000 or > 0x7FFFFFFFFFFF) return null;
        var m = ReadEntityMetadata(reader, cand);
        if (m.StartsWith("Metadata/Items", StringComparison.Ordinal)) return (cand, m);
        var inner = SafePtr(reader, cand);
        if ((ulong)inner is >= 0x10000 and <= 0x7FFFFFFFFFFF)
        {
            var mi = ReadEntityMetadata(reader, inner);
            if (mi.StartsWith("Metadata/Items", StringComparison.Ordinal)) return (inner, mi);
        }
        return null;
    }

    // A reward descriptor must reference the item to render its tile — via either an item Entity OR a
    // descriptive STRING (metadata path "Metadata/Items/…", icon art "Art/2DItems/…", or the base-type
    // name). For each primary cost hit, scan ±0x400 for a pointer to such a string (UTF-8 or UTF-16) or
    // an item entity. That string is the bridge to identifying & pricing the reward.
    bool Interesting(string? s) => !string.IsNullOrEmpty(s) && s.Length >= 6 &&
        (s.Contains("Metadata/Items", StringComparison.Ordinal) || s.Contains("Art/", StringComparison.Ordinal) ||
         s.Contains("2DItems", StringComparison.Ordinal) || s.Contains(".dds", StringComparison.OrdinalIgnoreCase));

    var promising = 0;
    foreach (var hit in hits)
    {
        const int half = 0x400;
        var win = new byte[half * 2];
        var baseAddr = hit - half;
        if (reader.TryReadBytes(baseAddr, win) < win.Length) continue;

        var notes = new List<string>();
        for (var o = 0; o + 8 <= win.Length; o += 8)
        {
            var q = (nint)BitConverter.ToInt64(win, o);
            if ((ulong)q is < 0x10000 or > 0x7FFFFFFFFFFF) continue;
            var it = AsItem(q);
            if (it is { } i) { notes.Add($"+0x{o - half:X} itemEntity 0x{i.item:X} {i.meta}"); continue; }
            var s8 = reader.ReadStringUtf8(q, 96);
            if (Interesting(s8)) { notes.Add($"+0x{o - half:X} str8→\"{s8}\""); continue; }
            var s16 = reader.ReadStringUtf16(q, 96);
            if (Interesting(s16)) { notes.Add($"+0x{o - half:X} str16→\"{s16}\""); }
        }
        if (notes.Count == 0) continue;
        promising++;
        if (promising > 30) { Console.WriteLine("(>30 promising — stopping)"); break; }
        Console.WriteLine($"── cost@0x{hit:X} has {notes.Count} item/art reference(s) within ±0x{half:X}:");
        foreach (var nt in notes.Take(8)) Console.WriteLine($"     {nt}");
    }
    Console.WriteLine($"\n{promising} cost hit(s) had an item/art/metadata reference nearby.");
    if (promising == 0) Console.WriteLine("None — reward descriptor doesn't keep an art/metadata string within ±0x400 of the cost.");
    return 0;
}

// Dump qwords with offsets; annotate ones that look like heap pointers.
static void DumpQwords(MemoryReader reader, nint addr, int span, string label)
{
    var buf = new byte[span];
    if (reader.TryReadBytes(addr, buf) < span) return;
    for (var o = 0; o + 8 <= span; o += 8)
    {
        var q = BitConverter.ToInt64(buf, o);
        var lo = (int)(q & 0xFFFFFFFF); var hi = (int)(q >> 32);
        var ptr = (ulong)q is >= 0x10000 and <= 0x7FFFFFFFFFFF ? " <ptr>" : "";
        Console.WriteLine($"{label}+0x{o - 0x40:+0x0;-0x0;0} 0x{addr + o:X}: q=0x{q:X16} (i32 {lo,11} {hi,11}){ptr}");
    }
}

// ── Heap UTF-16 substring finder + back-references: --findwstr "text". Finds every occurrence of the
// wide string, then scans the heap for pointers AT each occurrence (back-refs) so we can climb from the
// reward NAME string to the record that owns it (the bridge from item identity → reward data).
static int RunFindWStr(ProcessHandle process, MemoryReader reader, string needle)
{
    var pat = System.Text.Encoding.Unicode.GetBytes(needle);
    Console.WriteLine($"Scanning heap for UTF-16 \"{needle}\" ({pat.Length} bytes)…\n");
    var buf = new byte[1 << 20];
    var hits = new List<nint>();
    foreach (var (regBase, regSize) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
    {
        for (long o = 0; o < regSize && hits.Count < 5000; o += buf.Length - pat.Length)
        {
            var want = (int)Math.Min(buf.Length, regSize - o);
            var got = reader.TryReadBytes(regBase + (nint)o, buf.AsSpan(0, want));
            if (got <= 0) continue;
            for (var i = 0; i + pat.Length <= got; i += 2)
            {
                var m = true;
                for (var j = 0; j < pat.Length; j++) if (buf[i + j] != pat[j]) { m = false; break; }
                if (m) hits.Add(regBase + (nint)(o + i));
            }
        }
    }
    Console.WriteLine($"{hits.Count} occurrence(s):");
    foreach (var h in hits.Take(40))
    {
        var full = reader.ReadStringUtf16(h, 80);
        Console.WriteLine($"  @0x{h:X}  \"{full}\"");
    }
    if (hits.Count == 0) { Console.WriteLine("(none — name not present as UTF-16 in private heap)"); return 0; }

    // Back-references: who points at (near) the first few occurrences?
    Console.WriteLine("\nBack-references (pointers into the string / its container) for the first 3 hits:");
    var targets = hits.Take(3).ToHashSet();
    var lows = targets.Select(t => (long)t - 0x40).ToArray();
    var found = 0;
    foreach (var (regBase, regSize) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
    {
        for (long o = 0; o < regSize && found < 60; o += buf.Length - 8)
        {
            var want = (int)Math.Min(buf.Length, regSize - o);
            var got = reader.TryReadBytes(regBase + (nint)o, buf.AsSpan(0, want));
            if (got <= 0) continue;
            for (var i = 0; i + 8 <= got; i += 8)
            {
                var q = BitConverter.ToInt64(buf, i);
                foreach (var t in targets)
                    if (q >= (long)t - 0x40 && q <= (long)t + 8)
                    { Console.WriteLine($"  ptr@0x{regBase + (nint)(o + i):X} → 0x{q:X} (near \"{needle}\" @0x{t:X})"); found++; break; }
            }
        }
    }
    if (found == 0) Console.WriteLine("  (no back-references — string may be inline in a struct, not pointed to)");
    return 0;
}

// ── End-to-end test of the shipped Core read path: --ritual-shop. Calls Poe2Live.ReadRitualRewards (the
// exact method the overlay uses) and prints each resolved reward's rarity/art/name + screen rect. Validates
// the signature-gated grid walk without needing the overlay. Run with the tribute shop open.
static int RunRitualShop(ProcessHandle process, MemoryReader reader)
{
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
    if (slot == 0) { Console.Error.WriteLine("no GameState (in game?)"); return 1; }
    var live = new Poe2Live(reader, slot);
    if (!live.TryResolve(out var igs, out _, out _)) { Console.Error.WriteLine("no InGameState"); return 1; }
    float winW = 2560, winH = 1600; var hwnd = Win.FindMainWindowForPid((uint)process.ProcessId);
    if (hwnd != 0 && Win.GetClientRect(hwnd, out var rc) && rc.right > 0) { winW = rc.right; winH = rc.bottom; }

    var rewards = live.ReadRitualRewards(igs, winW, winH);
    Console.WriteLine($"window {winW}x{winH} — ReadRitualRewards returned {rewards.Count} reward(s):\n");
    foreach (var r in rewards)
        Console.WriteLine($"  {r.Rarity,-7} {(r.Identified ? "ID  " : "unID")} art={r.Art,-22} name=\"{r.Name}\"  rect=({r.X:0},{r.Y:0} {r.W:0}x{r.H:0})");
    if (rewards.Count == 0) Console.WriteLine("(none — is the tribute shop open? signature text not found, or grid walk failed)");
    return 0;
}

// ── Raw element/struct qword dumper: --eldump 0xADDR [--span N]. Every qword annotated with item-entity /
// string / small-int (cost candidate). Used to find the tribute-cost field offset within a reward tile.
static int RunElDump(MemoryReader reader, nint addr, int span)
{
    var win = new byte[span];
    if (reader.TryReadBytes(addr, win) < span) { Console.WriteLine("(unreadable)"); return 1; }
    for (var o = 0; o + 8 <= span; o += 8)
    {
        var q = (nint)BitConverter.ToInt64(win, o);
        string note = "";
        if ((ulong)q is >= 0x10000 and <= 0x7FFFFFFFFFFF)
        {
            var meta = ReadEntityMetadata(reader, q);
            if (meta.StartsWith("Metadata/Items", StringComparison.Ordinal)) note = $"  →itemEntity {meta}";
            else { var s = reader.ReadStringUtf16(q, 48); if (!string.IsNullOrEmpty(s) && s.Length >= 4 && s.All(c => c >= ' ' || c == '\n' || c == '\r')) note = $"  →str \"{s.Replace("\n", "/")}\""; else note = " <ptr>"; }
        }
        else
        {
            var i0 = (int)((long)q & 0xFFFFFFFF); var i1 = (int)((long)q >> 32);
            if (i0 is > 0 and < 1_000_000) note += $"  i0={i0}";
            if (i1 is > 0 and < 1_000_000) note += $"  i1={i1}";
        }
        Console.WriteLine($"  +0x{o:X3}  0x{addr + o:X}  q=0x{q:X16}{note}");
    }
    return 0;
}

// ── Hovered-tooltip item capture: --tooltip-capture. Fast UI-tree-ONLY walk (no heap scan, so it catches
// a transient tooltip). For every UiElement, scan its body for a pointer (direct or one deref) to a
// Metadata/Items entity and print art/rarity/identified + rendered mods + the element's text. Hover a
// ritual reward and run this: the tooltip's real item entity (the bridge to pricing) falls out, and its
// address feeds a follow-up reference-scan for the full 5-offer list.
static int RunTooltipCapture(ProcessHandle process, MemoryReader reader)
{
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
    if (slot == 0) { Console.Error.WriteLine("no GameState (in game?)"); return 1; }
    var live = new Poe2Live(reader, slot);
    if (!live.TryResolve(out var igs, out _, out _)) { Console.Error.WriteLine("no InGameState"); return 1; }
    var gameUi = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    if (gameUi == 0) { Console.Error.WriteLine("no GameUi"); return 1; }
    Console.WriteLine($"GameUi 0x{gameUi:X} — walking UI tree for item entities…\n");

    const uint VisMask = 1u << Poe2.UiElement.FlagVisibleBit;
    var visited = new HashSet<nint>();
    var seenItems = new HashSet<nint>();
    var stack = new Stack<(nint el, int depth)>();
    stack.Push((gameUi, 0));
    var nodes = 0; var found = 0;

    // Resolve a qword to a Metadata/Items entity, directly or via one dereference.
    (nint item, string meta)? AsItem(nint cand)
    {
        if ((ulong)cand is < 0x10000 or > 0x7FFFFFFFFFFF) return null;
        var m = ReadEntityMetadata(reader, cand);
        if (m.StartsWith("Metadata/Items", StringComparison.Ordinal)) return (cand, m);
        var inner = SafePtr(reader, cand);
        if ((ulong)inner is >= 0x10000 and <= 0x7FFFFFFFFFFF)
        {
            var mi = ReadEntityMetadata(reader, inner);
            if (mi.StartsWith("Metadata/Items", StringComparison.Ordinal)) return (inner, mi);
        }
        return null;
    }

    while (stack.Count > 0 && nodes < 400_000)
    {
        var (el, depth) = stack.Pop();
        if (el == 0 || !visited.Add(el) || depth > 80) continue;
        nodes++;
        reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags);
        var txt = ReadStdWString(reader, el + 0x390);

        var body = new byte[0x600];
        if (reader.TryReadBytes(el, body) >= body.Length)
            for (var o = 0x10; o + 8 <= body.Length; o += 8)
            {
                var cand = (nint)BitConverter.ToInt64(body, o);
                var it = AsItem(cand);
                if (it is not { } item || !seenItems.Add(item.item)) continue;
                var ri = ResolveComponentAddr(reader, item.item, "RenderItem");
                var art = ri == 0 ? "" : ItemArtBasename(reader.ReadStringUtf16(SafePtr(reader, ri + Poe2.RenderItemComponent.ResourcePath), 128)) ?? "";
                var mods = ResolveComponentAddr(reader, item.item, "Mods");
                var rarity = -1; var ident = -1;
                if (mods != 0) { reader.TryReadStruct<int>(mods + Poe2.ModsComponent.Rarity, out rarity); reader.TryReadStruct<int>(mods + Poe2.ModsComponent.Identified, out ident); }
                var rar = rarity switch { 0 => "Normal", 1 => "Magic", 2 => "Rare", 3 => "Unique", _ => $"r{rarity}" };
                found++;
                Console.WriteLine($"ITEM 0x{item.item:X}  {rar} {(ident == 1 ? "ID" : ident == 0 ? "unID" : "id?")}  art={art}");
                Console.WriteLine($"   meta: {item.meta}");
                Console.WriteLine($"   via element 0x{el:X} (+0x{o:X}) d{depth} vis={(flags & VisMask) != 0}{(string.IsNullOrEmpty(txt) ? "" : $" text=\"{txt.Replace("\n", " / ")}\"")}");
                Console.WriteLine($"   components: {string.Join(", ", ComponentNames(reader, item.item).OrderBy(s => s, StringComparer.Ordinal))}");
                if (mods != 0)
                    foreach (var (kind, moff) in new[] { ("implicit", 0xA0), ("explicit", 0xB8), ("enchant", 0xD0) })
                    {
                        var ids = ReadItemModIds(reader, mods + moff);
                        if (ids.Count > 0) Console.WriteLine($"   {kind}: {string.Join(" | ", ids)}");
                    }
                Console.WriteLine();
            }

        if (RfChildren(reader, el, out var first, out var n))
            for (long i = 0; i < n; i++)
                stack.Push((SafePtr(reader, first + (nint)(i * 8)), depth + 1));
    }
    Console.WriteLine($"walked {nodes} elements; {found} distinct item entit{(found == 1 ? "y" : "ies")} reachable from the UI tree.");
    if (found == 0) Console.WriteLine("(none — hover a reward so its tooltip is up, then re-run)");
    return 0;
}

// ── Ritual reward offer-list RE: --ritual-rewards [--reward NAME]. The reward TILES carry no item ptr
// (confirmed: not in the UI tree, not in any ServerData PlayerInventory). The offers live in a server/data
// list of records, each pointing to a display label "Unique\nBase" (e.g. "Venopuncture\nIron Ring"). We
// anchor on ONE known reward's display label, find every pointer to it (= a record field), and dump each
// record + its neighbours so the array (5 rewards in slot order) + the tribute-cost field fall out. Run
// with the ritual shop OPEN; pass --reward <UniqueName> for the current ritual's set (default Venopuncture).
static int RunRitualRewards(ProcessHandle process, MemoryReader reader, string needle)
{
    var pat = System.Text.Encoding.Unicode.GetBytes(needle);
    var buf = new byte[1 << 20];

    // 1) Find the display-label string(s): UTF-16 occurrences of `needle` whose full string spans a newline.
    var labelAddrs = new List<nint>();
    var occ = 0;
    foreach (var (regBase, regSize) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
        for (long o = 0; o < regSize && occ < 20000; o += buf.Length - pat.Length)
        {
            var want = (int)Math.Min(buf.Length, regSize - o);
            var got = reader.TryReadBytes(regBase + (nint)o, buf.AsSpan(0, want));
            if (got <= 0) continue;
            for (var i = 0; i + pat.Length <= got; i += 2)
            {
                var m = true;
                for (var j = 0; j < pat.Length; j++) if (buf[i + j] != pat[j]) { m = false; break; }
                if (!m) continue;
                occ++;
                var a = regBase + (nint)(o + i);
                if (reader.ReadStringUtf16(a, 80).Contains('\n')) labelAddrs.Add(a);
            }
        }
    Console.WriteLine($"\"{needle}\": {occ} occurrence(s), {labelAddrs.Count} display-label (newline) form(s):");
    foreach (var a in labelAddrs) Console.WriteLine($"  label @0x{a:X}  \"{reader.ReadStringUtf16(a, 80).Replace("\n", " / ")}\"");
    // Fallback: when the needle isn't a "Unique\nBase" label (e.g. a ".dds" icon path or a bare unique
    // name), anchor on ALL occurrences so we can still back-ref the record that owns the icon/name.
    if (labelAddrs.Count == 0)
    {
        Console.WriteLine("(no newline form — anchoring on ALL occurrences instead)");
        foreach (var (regBase, regSize) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
            for (long o = 0; o < regSize && labelAddrs.Count < 64; o += buf.Length - pat.Length)
            {
                var want = (int)Math.Min(buf.Length, regSize - o);
                var got = reader.TryReadBytes(regBase + (nint)o, buf.AsSpan(0, want));
                if (got <= 0) continue;
                for (var i = 0; i + pat.Length <= got && labelAddrs.Count < 64; i += 2)
                {
                    var m = true;
                    for (var j = 0; j < pat.Length; j++) if (buf[i + j] != pat[j]) { m = false; break; }
                    if (m) labelAddrs.Add(regBase + (nint)(o + i));
                }
            }
        foreach (var a in labelAddrs) Console.WriteLine($"  occ @0x{a:X}  \"{reader.ReadStringUtf16(a, 48)}\"");
    }
    if (labelAddrs.Count == 0) { Console.WriteLine("(nothing found — is the ritual shop open?)"); return 0; }

    // Annotate a qword that points to ANY display label (newline + printable) — reveals sibling rewards.
    string? LabelAt(nint q)
    {
        if ((ulong)q is < 0x10000 or > 0x7FFFFFFFFFFF) return null;
        var s = reader.ReadStringUtf16(q, 80);
        if (s.Length >= 6 && s.Contains('\n') && s.All(c => c >= ' ' || c == '\n' || c == '\r')) return s.Replace("\n", " / ").Replace("\r", "");
        return null;
    }

    // 2) Back-reference scan: heap pointers whose value is exactly a label addr (= a record's name field).
    var targets = labelAddrs.ToHashSet();
    var backrefs = new List<(nint at, nint to)>();
    foreach (var (regBase, regSize) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
        for (long o = 0; o < regSize && backrefs.Count < 4000; o += buf.Length - 8)
        {
            var want = (int)Math.Min(buf.Length, regSize - o);
            var got = reader.TryReadBytes(regBase + (nint)o, buf.AsSpan(0, want));
            if (got <= 0) continue;
            for (var i = 0; i + 8 <= got; i += 8)
                if (targets.Contains((nint)BitConverter.ToInt64(buf, i))) backrefs.Add((regBase + (nint)(o + i), (nint)BitConverter.ToInt64(buf, i)));
        }
    Console.WriteLine($"\n{backrefs.Count} pointer(s) to a label. Dumping each record + neighbours (±0x80/0x180):");

    // 3) For each back-ref, dump the surrounding record; annotate label/dds/item-entity/int-cost candidates.
    foreach (var (at, to) in backrefs.Take(10))
    {
        Console.WriteLine($"\n── field @0x{at:X} → label 0x{to:X} (\"{reader.ReadStringUtf16(to, 80).Replace("\n", " / ")}\") ──");
        const int span = 0x200; var lo = at - 0x80;
        var win = new byte[span];
        if (reader.TryReadBytes(lo, win) < span) { Console.WriteLine("   (unreadable)"); continue; }
        var labelsHere = 0;
        for (var o = 0; o + 8 <= span; o += 8)
        {
            var q = (nint)BitConverter.ToInt64(win, o);
            var rel = (long)(lo + o) - at;
            var relS = (rel >= 0 ? "+" : "-") + "0x" + Math.Abs(rel).ToString("X");
            var lab = LabelAt(q);
            if (lab != null) { Console.WriteLine($"   @0x{lo + o:X} ({relS,7})  →LABEL \"{lab}\""); labelsHere++; continue; }
            if ((ulong)q is >= 0x10000 and <= 0x7FFFFFFFFFFF)
            {
                var s16 = reader.ReadStringUtf16(q, 64);
                if (!string.IsNullOrEmpty(s16) && (s16.Contains(".dds") || s16.Contains("Art/") || s16.Contains("Metadata")))
                { Console.WriteLine($"   @0x{lo + o:X} ({relS,7})  →str \"{s16}\""); continue; }
                var meta = ReadEntityMetadata(reader, q);
                if (meta.StartsWith("Metadata/Items", StringComparison.Ordinal))
                { Console.WriteLine($"   @0x{lo + o:X} ({relS,7})  →itemEntity {meta}"); continue; }
            }
            var i0 = (int)((long)q & 0xFFFFFFFF); var i1 = (int)((long)q >> 32);
            var note = (i0 is > 0 and < 1_000_000 ? $" int0={i0}" : "") + (i1 is > 0 and < 1_000_000 ? $" int1={i1}" : "");
            var pp = (ulong)q is >= 0x10000 and <= 0x7FFFFFFFFFFF ? " <ptr>" : "";
            Console.WriteLine($"   @0x{lo + o:X} ({relS,7})  q=0x{q:X16}{pp}{note}");
        }
        if (labelsHere >= 2) Console.WriteLine($"   ★ {labelsHere} reward labels in this window — likely the offer array.");
    }
    return 0;
}

// ── Generic UI subtree + ancestor dumper: --subtree 0xADDR [--up N] [--down N]. Prints the ancestor
// chain (child counts + indices = the fingerprint trail) and the full subtree with each element's rect,
// flags, text, and any item-identity reference (item Entity ptr / "Art/2DItems" icon DDS / metadata).
static int RunSubtree(ProcessHandle process, MemoryReader reader, nint root, int up, int down)
{
    float winW = 2560, winH = 1600; var hwnd = Win.FindMainWindowForPid((uint)process.ProcessId);
    if (hwnd != 0 && Win.GetClientRect(hwnd, out var rc) && rc.right > 0) { winW = rc.right; winH = rc.bottom; }
    float v1 = winW / 2560f, v2 = winH / 1600f;
    const uint VisMask = 1u << Poe2.UiElement.FlagVisibleBit;

    (float x, float y, float w, float h) Rect(nint el)
    {
        var (ux, uy) = RfUnscaledPos(reader, el, 0);
        reader.TryReadStruct<float>(el + 0x288, out var w); reader.TryReadStruct<float>(el + 0x28C, out var h);
        reader.TryReadStruct<byte>(el + 0x18A, out var sidx);
        var (sx, sy) = sidx switch { 1 => (v1, v1), 3 => (v1, v2), _ => (v2, v2) };
        return (ux * sx, uy * sy, w * sx, h * sy);
    }
    bool InterestingStr(string? s) => !string.IsNullOrEmpty(s) && s.Length >= 5 &&
        (s.Contains("2DItems", StringComparison.OrdinalIgnoreCase) || s.Contains("Metadata/Items", StringComparison.Ordinal)
         || s.Contains("Art/", StringComparison.Ordinal) || s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
    void Probe(nint q, string pad, int hop, HashSet<nint> seen)
    {
        if (hop > 2 || (ulong)q is < 0x10000 or > 0x7FFFFFFFFFFF || !seen.Add(q)) return;
        var meta = ReadEntityMetadata(reader, q);
        if (meta.StartsWith("Metadata/Items", StringComparison.Ordinal)) { Console.WriteLine($"{pad}  hop{hop} 0x{q:X} itemEntity {meta}"); return; }
        var s16 = reader.ReadStringUtf16(q, 120); if (InterestingStr(s16)) { Console.WriteLine($"{pad}  hop{hop} 0x{q:X} str16→\"{s16}\""); return; }
        var s8 = reader.ReadStringUtf8(q, 120); if (InterestingStr(s8)) { Console.WriteLine($"{pad}  hop{hop} 0x{q:X} str8→\"{s8}\""); return; }
    }
    void Identity(nint el, string pad)
    {
        var body = new byte[0x300];
        if (reader.TryReadBytes(el, body) < body.Length) return;
        var seen = new HashSet<nint>();
        for (var o = 0x10; o + 8 <= body.Length; o += 8)
        {
            var q = (nint)BitConverter.ToInt64(body, o);
            if ((ulong)q is < 0x10000 or > 0x7FFFFFFFFFFF) continue;
            Probe(q, pad, 1, seen);
            // one more hop: follow the first few qwords of the target
            var inner = new byte[0x40];
            if (reader.TryReadBytes(q, inner) >= inner.Length)
                for (var k = 0; k + 8 <= inner.Length; k += 8)
                    Probe((nint)BitConverter.ToInt64(inner, k), pad, 2, seen);
        }
    }

    // Ancestors (root → up).
    Console.WriteLine($"ANCESTORS of 0x{root:X}:");
    var cur = root; var chain = new List<string>();
    for (var i = 0; i < up; i++)
    {
        var parent = SafePtr(reader, cur + Poe2.UiElement.Parent);
        if (parent == 0) break;
        reader.TryReadStruct<uint>(parent + Poe2.UiElement.Flags, out var pf);
        var idx = -1; var cc = 0L;
        if (RfChildren(reader, parent, out var first, out var n)) { cc = n; for (long k = 0; k < n; k++) if (SafePtr(reader, first + (nint)(k * 8)) == cur) { idx = (int)k; break; } }
        chain.Add($"  ↑ 0x{parent:X} flags=0x{pf & ~VisMask:X} children={cc} (this=child[{idx}])");
        cur = parent;
    }
    foreach (var c in chain) Console.WriteLine(c);

    // Subtree (root → down).
    Console.WriteLine($"\nSUBTREE of 0x{root:X} (depth {down}):");
    void Walk(nint el, int depth)
    {
        if (el == 0 || depth > down) return;
        reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var fl);
        var (x, y, w, h) = Rect(el);
        var txt = ReadStdWString(reader, el + 0x390);
        var pad = new string(' ', depth * 2);
        Console.WriteLine($"{pad}• 0x{el:X} d{depth} flags=0x{fl & ~VisMask:X} vis={(fl & VisMask) != 0} rect=({x:0},{y:0} {w:0}x{h:0}) {(string.IsNullOrEmpty(txt) ? "" : $"text=\"{txt}\"")}");
        Identity(el, pad);
        if (RfChildren(reader, el, out var first, out var n))
            for (long i = 0; i < n && i < 40; i++) Walk(SafePtr(reader, first + (nint)(i * 8)), depth + 1);
    }
    Walk(root, 0);
    return 0;
}

// ── Hovered-tile finder: project every visible UiElement to a screen rect (runeforge projection) and
// report every element whose rect contains the cursor, innermost (smallest) first. Hover a reward tile
// and run: the hovered tile + its parent grid + sibling tiles fall out of the nesting, and for each we
// show how the item identity is stored (item Entity ptr / "Art/2DItems" icon DDS / inline text).
static int RunTributeHover(ProcessHandle process, MemoryReader reader)
{
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
    if (slot == 0) { Console.Error.WriteLine("no GameState"); return 1; }
    var live = new Poe2Live(reader, slot);
    if (!live.TryResolve(out var igs, out _, out _)) { Console.Error.WriteLine("no InGameState"); return 1; }
    var gameUi = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    if (gameUi == 0) { Console.Error.WriteLine("no GameUi"); return 1; }

    // Use PoE2's own window (NOT the foreground — that's this console), so the rect math is in game space.
    float winW = 2560, winH = 1600; var hwnd = Win.FindMainWindowForPid((uint)process.ProcessId);
    if (hwnd != 0 && Win.GetClientRect(hwnd, out var rc) && rc.right > 0) { winW = rc.right; winH = rc.bottom; }
    Win.POINT cur = default; Win.GetCursorPos(out cur); if (hwnd != 0) Win.ScreenToClient(hwnd, ref cur);
    float v1 = winW / 2560f, v2 = winH / 1600f;
    Console.WriteLine($"GameUi 0x{gameUi:X}  poe2Hwnd=0x{hwnd:X}  window {winW}x{winH}  cursor(client)=({cur.X},{cur.Y})\n");

    const uint VisMask = 1u << Poe2.UiElement.FlagVisibleBit;
    var hitsUnderCursor = new List<(nint el, int depth, float x, float y, float w, float h, float area)>();
    var visited = new HashSet<nint>();
    var stack = new Stack<(nint el, int depth)>();
    stack.Push((gameUi, 0));
    var nodes = 0;
    while (stack.Count > 0 && nodes < 300_000)
    {
        var (el, depth) = stack.Pop();
        if (el == 0 || !visited.Add(el) || depth > 80) continue;
        nodes++;
        reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags);
        var visible = (flags & VisMask) != 0;
        if (visible)
        {
            var (ux, uy) = RfUnscaledPos(reader, el, 0);
            reader.TryReadStruct<float>(el + 0x288, out var usw);
            reader.TryReadStruct<float>(el + 0x28C, out var ush);
            reader.TryReadStruct<byte>(el + 0x18A, out var sidx);
            var (sx, sy) = sidx switch { 1 => (v1, v1), 3 => (v1, v2), _ => (v2, v2) };
            float x = ux * sx, y = uy * sy, w = usw * sx, h = ush * sy;
            if (w > 0 && h > 0 && cur.X >= x && cur.X <= x + w && cur.Y >= y && cur.Y <= y + h)
                hitsUnderCursor.Add((el, depth, x, y, w, h, w * h));
        }
        if (RfChildren(reader, el, out var first, out var n))
            for (long i = 0; i < n; i++)
                stack.Push((SafePtr(reader, first + (nint)(i * 8)), depth + 1));
    }
    hitsUnderCursor.Sort((a, b) => a.area.CompareTo(b.area));
    Console.WriteLine($"walked {nodes}; {hitsUnderCursor.Count} visible element(s) under cursor (innermost first):\n");
    foreach (var h in hitsUnderCursor.Take(18))
    {
        reader.TryReadStruct<uint>(h.el + Poe2.UiElement.Flags, out var fl);
        var txt = ReadStdWString(reader, h.el + 0x390);
        Console.WriteLine($"• 0x{h.el:X} d{h.depth} flags=0x{fl & ~VisMask:X} rect=({h.x:0},{h.y:0} {h.w:0}x{h.h:0})  text=\"{txt}\"");
        // Identity hunt: item Entity ptr or 2DItems icon DDS reachable from the body.
        var body = new byte[0x380];
        if (reader.TryReadBytes(h.el, body) >= body.Length)
            for (var o = 0x18; o + 8 <= body.Length; o += 8)
            {
                var q = (nint)BitConverter.ToInt64(body, o);
                if ((ulong)q is < 0x10000 or > 0x7FFFFFFFFFFF) continue;
                var meta = ReadEntityMetadata(reader, q);
                if (meta.StartsWith("Metadata/Items", StringComparison.Ordinal))
                { Console.WriteLine($"      +0x{o:X} itemEntity 0x{q:X} {meta}"); continue; }
                var s16 = reader.ReadStringUtf16(q, 110);
                if (!string.IsNullOrEmpty(s16) && (s16.Contains("2DItems", StringComparison.OrdinalIgnoreCase) || s16.Contains("Metadata/Items", StringComparison.Ordinal)))
                    Console.WriteLine($"      +0x{o:X} str→\"{s16}\"");
            }
        var pAddr = SafePtr(reader, h.el + Poe2.UiElement.Parent);
        if (pAddr != 0 && RfChildren(reader, pAddr, out _, out var pn))
            Console.WriteLine($"      parent 0x{pAddr:X} (children={pn})");
    }
    return 0;
}

// ── Reward TILE finder: walk the UI tree, follow each element's body pointers one hop, and report any
// element that reaches a struct containing a known tribute cost (or an item-icon art string). This links
// the reward tile UiElement → its server reward record (cost + item ref), since blind cost-scanning
// showed the cost is neither an array nor adjacent to the item. Run with the tribute shop open.
static int RunTributeTiles(ProcessHandle process, MemoryReader reader, string costsCsv)
{
    var costs = costsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n > 0).ToHashSet();
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
    if (slot == 0) { Console.Error.WriteLine("no GameState"); return 1; }
    var live = new Poe2Live(reader, slot);
    if (!live.TryResolve(out var igs, out _, out _)) { Console.Error.WriteLine("no InGameState"); return 1; }
    var gameUi = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    if (gameUi == 0) { Console.Error.WriteLine("no GameUi"); return 1; }
    Console.WriteLine($"GameUi 0x{gameUi:X}  costs=[{string.Join(",", costs)}]\n");

    bool HasCost(ReadOnlySpan<byte> b)
    {
        for (var o = 0; o + 4 <= b.Length; o += 4) if (costs.Contains(BitConverter.ToInt32(b[o..]))) return true;
        return false;
    }
    int WhichCost(ReadOnlySpan<byte> b, out int off)
    {
        for (var o = 0; o + 4 <= b.Length; o += 4) { var v = BitConverter.ToInt32(b[o..]); if (costs.Contains(v)) { off = o; return v; } }
        off = -1; return -1;
    }

    var visited = new HashSet<nint>();
    var stack = new Stack<(nint el, int depth)>();
    stack.Push((gameUi, 0));
    var nodes = 0; var found = 0;
    var elBody = new byte[0x380];
    var recBody = new byte[0x100];
    while (stack.Count > 0 && nodes < 300_000)
    {
        var (el, depth) = stack.Pop();
        if (el == 0 || !visited.Add(el) || depth > 80) continue;
        nodes++;

        if (reader.TryReadBytes(el, elBody) >= elBody.Length)
        {
            // (a) cost directly in the element body?
            if (HasCost(elBody))
            {
                WhichCost(elBody, out var off);
                ReportTile(reader, el, depth, $"cost in body @+0x{off:X}");
                found++;
            }
            else
            {
                // (b) follow each body pointer one hop; does the target struct hold a cost?
                for (var o = 0x18; o + 8 <= elBody.Length; o += 8)
                {
                    var q = (nint)BitConverter.ToInt64(elBody, o);
                    if ((ulong)q is < 0x10000 or > 0x7FFFFFFFFFFF) continue;
                    if (reader.TryReadBytes(q, recBody) < recBody.Length) continue;
                    if (!HasCost(recBody)) continue;
                    WhichCost(recBody, out var roff);
                    ReportTile(reader, el, depth, $"body+0x{o:X} → record 0x{q:X} (cost @rec+0x{roff:X})");
                    DumpQwords(reader, q, 0x80, "        ");
                    found++;
                    break;
                }
            }
        }

        if (RfChildren(reader, el, out var first, out var n))
            for (long i = 0; i < n; i++)
                stack.Push((SafePtr(reader, first + (nint)(i * 8)), depth + 1));
    }
    Console.WriteLine($"\nwalked {nodes} elements; {found} tile/record hit(s).");
    if (found == 0) Console.WriteLine("No UI element reaches a cost within one hop. The cost may be ≥2 hops from the tile, " +
                                      "or rendered from a server record not pointed to directly by the UiElement.");
    return 0;
}

static void ReportTile(MemoryReader reader, nint el, int depth, string note)
{
    reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags);
    var txt = ReadStdWString(reader, el + 0x390);
    DumpUiEl(reader, el, depth, flags, $"{note}  text=\"{txt}\"");
    DumpAncestors(reader, el);
}

static bool IsMostlyPrintable(string s)
{
    if (string.IsNullOrEmpty(s)) return false;
    var ok = 0; foreach (var c in s) if (c is >= ' ' and <= '~' || c > 127) ok++;
    return ok >= s.Length - 1;
}

static void DumpUiEl(MemoryReader reader, nint el, int depth, uint flags, string note)
{
    const uint VisMask = 1u << Poe2.UiElement.FlagVisibleBit;
    reader.TryReadStruct<float>(el + 0x118, out var rx);
    reader.TryReadStruct<float>(el + 0x11C, out var ry);
    reader.TryReadStruct<float>(el + 0x288, out var sw);
    reader.TryReadStruct<float>(el + 0x28C, out var sh);
    Console.WriteLine($"\n• 0x{el:X} d{depth} flags=0x{flags:X8} (masked 0x{flags & ~VisMask:X}) vis={(flags & VisMask) != 0} "
                    + $"relPos=({rx:0.#},{ry:0.#}) size=({sw:0.#}x{sh:0.#})  {note}");
}

// Walk Parent (+0xB8) up to the root, printing each ancestor's addr / masked-flags / child count and
// this node's index within it — the fingerprint trail for a resolve walk.
static void DumpAncestors(MemoryReader reader, nint el)
{
    const uint VisMask = 1u << Poe2.UiElement.FlagVisibleBit;
    var cur = el; var guard = 0;
    while (guard++ < 40)
    {
        var parent = SafePtr(reader, cur + Poe2.UiElement.Parent);
        if (parent == 0) break;
        reader.TryReadStruct<uint>(parent + Poe2.UiElement.Flags, out var pf);
        var idx = -1; var cc = 0L;
        if (RfChildren(reader, parent, out var first, out var n))
        {
            cc = n;
            for (long i = 0; i < n; i++) if (SafePtr(reader, first + (nint)(i * 8)) == cur) { idx = (int)i; break; }
        }
        Console.WriteLine($"      ↑ parent 0x{parent:X} flags=0x{pf & ~VisMask:X8} children={cc} (this is child[{idx}])");
        cur = parent;
    }
}

// Dump a component's first `span` bytes as a grid of little-endian int32s with byte offsets — the
// quickest way to eyeball which scalar differs between two snapshots of the "same" component.
static void DumpInts(MemoryReader reader, nint addr, int span, string label)
{
    var buf = new byte[span];
    if (reader.TryReadBytes(addr, buf) < span) return;
    for (var i = 0; i < span; i += 16)
    {
        var ints = string.Join(" ", Enumerable.Range(0, 4).Select(j => BitConverter.ToInt32(buf, i + j * 4).ToString().PadLeft(11)));
        Console.WriteLine($"{label}+0x{i:X2}  {ints}");
    }
}

// ── Monolith: validate the device→station chain that exposes hole count N + anchor rune ───────────
// Ports GameHelper RunecraftHelper's MonolithRewards resolution to confirm the offsets live on OUR
// patch. For each Expedition2Encounter device: device → StateMachine → listener vec (SM+0x20) →
// station = *(node) − 0x98 (verified *(station+0x10)==device). Then station+0x38 = N (hole count),
// station+0x28 = anchor rune row ptr, station+0x3c = anchor hole index, and the anchor rune index =
// (rowPtr − tableBase)/0x6c with tableBase = *(*(station+0x30 + 0x28)). Stand near monoliths and run.
static int RunMonolith(ProcessHandle process, MemoryReader reader)
{
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
    if (slot == 0) { Console.Error.WriteLine("Could not lock GameState slot (in game?)."); return 1; }
    var live = new Poe2Live(reader, slot);
    if (!live.TryResolve(out _, out var ai, out var lp)) { Console.Error.WriteLine("Could not resolve area."); return 1; }

    var catalog = POE2Radar.Core.Game.RuneMonolithCatalog.Instance;
    var areaLevel = live.AreaLevel(ai);
    var pg = EntityGrid(reader, lp);
    Console.WriteLine($"AreaInstance 0x{ai:X}  areaLevel={areaLevel}  catalogLoaded={catalog.IsLoaded}  player ({pg.X:F0},{pg.Y:F0})\n");

    var devices = new List<(uint id, nint ent, float dist)>();
    foreach (var (id, ent, meta) in WalkEntities(reader, ai))
    {
        if (meta.IndexOf("Expedition2Encounter", StringComparison.OrdinalIgnoreCase) < 0) continue;
        var g = EntityGrid(reader, ent);
        var d = g == default ? 9999f : (float)Math.Sqrt((g.X - pg.X) * (g.X - pg.X) + (g.Y - pg.Y) * (g.Y - pg.Y));
        devices.Add((id, ent, d));
    }
    devices.Sort((a, b) => a.dist.CompareTo(b.dist));
    Console.WriteLine($"{devices.Count} monolith device(s)\n");

    foreach (var (id, device, dist) in devices)
    {
        var m = live.ReadMonolith(device);
        Console.WriteLine($"── device id={id} @0x{device:X}  dist={dist:F0} ──");
        if (!m.Resolved) { Console.WriteLine("   ✗ station not resolved (collected=" + m.Collected + ")"); continue; }

        var kind = m.IsUnique ? "UNIQUE (anchor-less)"
            : m.AnchorIdx >= 0 ? $"anchor={catalog.RuneName(m.AnchorIdx)} (idx {m.AnchorIdx}) @ hole {m.AnchorPos + 1}"
            : "anchor decode FAILED";
        Console.WriteLine($"   ✓ N={m.HoleCount} holes   {kind}   collected={m.Collected}");

        var offers = catalog.Offers(m.AnchorIdx, m.AnchorPos, m.HoleCount, m.IsUnique, areaLevel);
        Console.WriteLine($"   offers {offers.Count} recipe(s):");
        foreach (var o in offers.Take(40))
            Console.WriteLine($"     size{o.Size} x{o.Count,-2} {(string.IsNullOrEmpty(o.Name) ? "(" + o.Description + ")" : o.Name),-34} [{o.Runes}]");
    }
    return 0;
}

// ── Rune-dump: identify the in-world event object near the player ─────────────────────────────────
// Answers "what tile / entity am I standing next to, and does the rune COUNT appear anywhere static?"
// Stand at the event and run it; then go to an event with a DIFFERENT rune count and run it again. If
// the tile path AND the object metadata path are identical across counts, the count is NOT encoded in
// any static path (it's a server-side roll → only readable from the open panel / a live component), so
// the tile/POI locator is count-blind by construction. The per-component small-int scan flags any field
// on the object holding a value in 1..12 — a candidate "number of recipes/slots" that would let us
// color the icon the moment it's in range (before opening the panel).
static int RunRuneDump(ProcessHandle process, MemoryReader reader, int radiusGrid)
{
    var (_, _, ai, lp) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }

    var pg = EntityGrid(reader, lp);
    Console.WriteLine($"AreaInstance 0x{ai:X}  player grid ({pg.X:F0},{pg.Y:F0})  radius {radiusGrid} tiles\n");

    // ── 1) ENTITIES near the player, nearest first ───────────────────────────────────────────────
    var near = new List<(double dist, uint id, nint ent, string meta, System.Numerics.Vector2 g)>();
    foreach (var (id, ent, meta) in WalkEntities(reader, ai))
    {
        var g = EntityGrid(reader, ent);
        var d = (g == default) ? double.MaxValue : Math.Sqrt((g.X - pg.X) * (g.X - pg.X) + (g.Y - pg.Y) * (g.Y - pg.Y));
        if (d > radiusGrid) continue;
        near.Add((d, id, ent, meta, g));
    }
    near.Sort((a, b) => a.dist.CompareTo(b.dist));
    Console.WriteLine($"=== {near.Count} entities within {radiusGrid} tiles (nearest first) ===");
    foreach (var (dist, id, ent, meta, g) in near)
    {
        var icon = ResolveComponentAddr(reader, ent, "MinimapIcon");
        var poi = icon != 0;
        var done = poi && reader.TryReadStruct<int>(icon + Poe2.MinimapIcon.CompletedState, out var s) && s != 0;
        var keyword = meta.Contains("rune", StringComparison.OrdinalIgnoreCase)
                   || meta.Contains("forge", StringComparison.OrdinalIgnoreCase)
                   || meta.Contains("combin", StringComparison.OrdinalIgnoreCase)
                   || meta.Contains("xpedition", StringComparison.OrdinalIgnoreCase);
        var flag = keyword ? " <<< RUNE/FORGE/EXPEDITION" : poi ? " <POI>" : "";
        Console.WriteLine($"\nid={id,-6} dist={dist,5:F1}  grid=({g.X:F0},{g.Y:F0})  @0x{ent:X}");
        Console.WriteLine($"   meta: {meta}{flag}");
        var comps = WalkComponents(reader, ent);
        Console.WriteLine($"   components ({comps.Count}): {string.Join(", ", comps.Select(c => c.name))}");
        if (poi) Console.WriteLine($"   MinimapIcon @0x{icon:X}  completed={done}");

        // Small-int candidate-count scan: only for the interesting objects (a POI or a keyword match),
        // to keep noise down. Reports each component offset (+0x00..+0x100, 4-byte stride) holding a
        // value in 1..12 — a plausible "number of recipes/slots/runes". Compare these across instances
        // with different counts: the field that TRACKS the count is the one to read.
        if (poi || keyword)
            foreach (var (cname, caddr) in comps)
            {
                if (caddr == 0) continue;
                var buf = new byte[0x180];
                if (reader.TryReadBytes(caddr, buf) < buf.Length) continue;
                var hits = new List<string>();
                for (var o = 0; o + 4 <= buf.Length; o += 4)
                {
                    var v = BitConverter.ToInt32(buf, o);
                    if (v is >= 1 and <= 12) hits.Add($"+0x{o:X2}={v}");
                }
                if (hits.Count > 0) Console.WriteLine($"     {cname,-20} small-ints: {string.Join("  ", hits)}");

                // Vector-length scan: the rune COUNT is likely a vector length (reward items / sub-
                // inventories), not a scalar. Scan +0x00..+0x180 for StdVector-shaped {First,Last,End}
                // triples and report element counts in 1..64 for plausible strides.
                var vhits = new List<string>();
                for (var o = 0; o + 24 <= 0x180; o += 8)
                {
                    var first = (nint)BitConverter.ToInt64(buf, o);
                    var last = (nint)BitConverter.ToInt64(buf, o + 8);
                    var end = (nint)BitConverter.ToInt64(buf, o + 16);
                    if ((ulong)first is < 0x10000 or > 0x7FFFFFFFFFFF) continue;
                    var span = (long)last - first;
                    if (span <= 0 || last > end || (long)end - last > 0x400) continue;
                    foreach (var stride in new[] { 8, 0x10, 0x18, 0x28, 0x40 })
                        if (span % stride == 0 && span / stride is >= 1 and <= 64)
                        { vhits.Add($"+0x{o:X2}[s{stride:X}]={span / stride}"); break; }
                }
                if (vhits.Count > 0) Console.WriteLine($"     {cname,-20} vec-lens:   {string.Join("  ", vhits)}");
            }
    }

    // ── 2) TILE paths near the player ────────────────────────────────────────────────────────────
    var terrain = ai + Poe2.AreaInstance.TerrainMetadata;
    reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX);
    var tFirst = SafePtr(reader, terrain + Poe2.Terrain.TileDetailsPtr);
    reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var tLast);
    var tCount = tFirst == 0 ? 0 : ((long)tLast - (long)tFirst) / Poe2.TileStructureSize;
    Console.WriteLine($"\n\n=== distinct tile paths within {radiusGrid} tiles of player (min distance shown) ===");
    if (tilesX > 0 && tCount is > 0 and <= 1_000_000)
    {
        var cell = Poe2.Terrain.TileGridCells;
        var pathCache = new Dictionary<nint, string?>();
        var nearest = new Dictionary<string, double>(StringComparer.Ordinal);
        for (long i = 0; i < tCount; i++)
        {
            var tgt = SafePtr(reader, tFirst + (nint)(i * Poe2.TileStructureSize) + Poe2.TileStructure.TgtFilePtr);
            if (tgt == 0) continue;
            if (!pathCache.TryGetValue(tgt, out var path))
                pathCache[tgt] = path = ReadStdWString(reader, tgt + Poe2.TgtFileStruct.TgtPath);
            if (string.IsNullOrEmpty(path)) continue;
            double gx = (i % tilesX) * cell, gy = (i / tilesX) * cell;
            var d = Math.Sqrt((gx - pg.X) * (gx - pg.X) + (gy - pg.Y) * (gy - pg.Y));
            if (d > radiusGrid) continue;
            if (!nearest.TryGetValue(path, out var prev) || d < prev) nearest[path] = d;
        }
        foreach (var kv in nearest.OrderBy(k => k.Value))
            Console.WriteLine($"  {kv.Value,6:F1}  {kv.Key}");
        if (nearest.Count == 0) Console.WriteLine("  (none — increase --radius)");
    }
    return 0;
}

// Read an entity's grid position via its Render component (CurrentWorldPosition +0x138 / world-to-grid).
static System.Numerics.Vector2 EntityGrid(MemoryReader reader, nint entity)
{
    var render = ResolveComponentAddr(reader, entity, "Render");
    if (render != 0 && reader.TryReadStruct<POE2Radar.Core.Game.Vector3>(render + 0x138, out var w))
        return new System.Numerics.Vector2((float)(w.X / Poe2.WorldToGridRatio), (float)(w.Y / Poe2.WorldToGridRatio));
    return default;
}

// ── Runeforge / "Runeshape Combinations" reward panel probe ─────────────────────────────────────
// Validates (against the LIVE patch, with the panel OPEN) the port of GameHelper's RuneforgeHelper:
//   1) resolve the recipes panel by a UI-FLAGS-FINGERPRINT walk with backtracking from GameUi
//      (= Ptr(InGameState + UiRoot 0x2F0), the UiRootStruct the game treats as a UiElement). Child
//      indices drift across restarts/patches but each element's Flags "role" bits are stable — so we
//      match (flags & ~visibleBit) == fingerprint, trying visible siblings first, and backtrack to
//      whichever branch bottoms out at a real recipes-container (rows whose kid[0] has a name wstring).
//   2) read each VISIBLE row: kid[0] inline std::wstring @ +0x390 = "<count>x <name>".
//   3) dump each row's raw geometry fields (relPos/size/scale) so the overlay-side screen projection
//      can be ported with confidence. Run with the Runeshape Combinations panel OPEN.
static int RunRuneforge(ProcessHandle process, MemoryReader reader)
{
    // Flag-fingerprint chain (GameHelper RuneforgeHelper, PoE2 0.5.x): window-container (gate, step 0)
    // → … → recipes-container. Match (flags & ~visibleBit); see RfWalk.
    uint[] fps = { 0x00462EF1, 0x00502EF3, 0x00502EF7, 0x00542EF1, 0x00502EF1 };
    const uint UiVisibleMask = 1u << Poe2.UiElement.FlagVisibleBit; // 0x800
    const int RuneforgeNameWString = 0x390;     // kid[0] inline "<count>x <name>" wstring
    nint slot = 0;
    foreach (var pat in AobPatterns.GameStateRefs)
        foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
            if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
    if (slot == 0) { Console.Error.WriteLine("Could not lock GameState slot (in game?)."); return 1; }
    var live = new Poe2Live(reader, slot);

    Console.WriteLine("RUNEFORGE probe — open the Runeshape Combinations panel. Polling ~5s for the panel…");
    nint panel = 0, gameUi = 0;
    for (var attempt = 0; attempt < 12 && panel == 0; attempt++)
    {
        if (live.TryResolve(out var igs, out _, out _))
        {
            gameUi = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
            if (gameUi != 0) panel = RfWalk(reader, gameUi, 0, fps);
        }
        if (panel == 0) Thread.Sleep(450);
    }
    if (panel == 0)
    {
        Console.Error.WriteLine($"Panel NOT resolved (gameUi=0x{gameUi:X}). Is the Runeshape Combinations panel open? "
                                + "If so, the flag fingerprints likely drifted this patch — dumping GameUi children flags for re-fingerprinting:");
        RfDumpChildFlags(reader, gameUi);
        return 1;
    }
    Console.WriteLine($"Panel RESOLVED: 0x{panel:X}");

    // Visible rows.
    if (!RfChildren(reader, panel, out var first, out var n)) { Console.WriteLine("(no row vector)"); return 0; }
    Console.WriteLine($"recipes-container rows: {n} total (visible ones below)");
    var shown = 0;
    for (long i = 0; i < n; i++)
    {
        var row = SafePtr(reader, first + (nint)(i * 8));
        if (row == 0) continue;
        if (!reader.TryReadStruct<uint>(row + Poe2.UiElement.Flags, out var rflags) || (rflags & UiVisibleMask) == 0) continue;
        var label = RfChild(reader, row, 0);
        if (label == 0) continue;
        var raw = ReadStdWString(reader, label + RuneforgeNameWString);
        if (string.IsNullOrEmpty(raw)) continue;
        RfParseNameCount(raw, out var count, out var name);

        // Raw geometry fields (validate the screen-projection port).
        reader.TryReadStruct<float>(row + 0x118, out var rx);       // RelativePosition.X
        reader.TryReadStruct<float>(row + 0x11C, out var ry);       // RelativePosition.Y
        reader.TryReadStruct<float>(row + 0x288, out var sw);       // UnscaledSize.X
        reader.TryReadStruct<float>(row + 0x28C, out var sh);       // UnscaledSize.Y
        reader.TryReadStruct<float>(row + 0x130, out var smul);     // LocalScaleMultiplier
        reader.TryReadStruct<byte>(row + 0x18A, out var sidx);      // ScaleIndex
        var (ux, uy) = RfUnscaledPos(reader, row, 0);
        shown++;
        Console.WriteLine($"  [{shown}] '{raw}'  -> count={count} name='{name}'");
        Console.WriteLine($"        relPos=({rx:0.#},{ry:0.#}) size=({sw:0.#}x{sh:0.#}) scaleIdx={sidx} mul={smul:0.##} unscaledAbs=({ux:0.#},{uy:0.#})");
    }
    Console.WriteLine($"Visible reward rows: {shown}");
    return 0;
}

// Flag-fingerprint walk with backtracking. step indexes fps; at the end, require a recipes-container.
static nint RfWalk(MemoryReader reader, nint parent, int step, uint[] fps)
{
    const uint UiVisibleMask = 1u << Poe2.UiElement.FlagVisibleBit;
    const int gateStep = 0; // window-container: only accept it visible (panel-open gate)
    if (step == fps.Length)
        return RfIsRecipesContainer(reader, parent) ? parent : 0;
    if (!RfChildren(reader, parent, out var first, out var n)) return 0;
    var target = fps[step] & ~UiVisibleMask;
    for (var pass = 0; pass < 2; pass++)            // visible candidates first, then invisible
    {
        var wantVisible = pass == 0;
        for (long i = 0; i < n; i++)
        {
            var child = SafePtr(reader, first + (nint)(i * 8));
            if (child == 0) continue;
            if (!reader.TryReadStruct<uint>(child + Poe2.UiElement.Flags, out var flags)) continue;
            if ((flags & ~UiVisibleMask) != target) continue;
            var visible = (flags & UiVisibleMask) != 0;
            if (visible != wantVisible) continue;
            if (step == gateStep && !visible) continue;   // panel-open gate
            var deeper = RfWalk(reader, child, step + 1, fps);
            if (deeper != 0) return deeper;
        }
    }
    return 0;
}

static bool RfIsRecipesContainer(MemoryReader reader, nint addr)
{
    const int RuneforgeNameWString = 0x390;
    if (!RfChildren(reader, addr, out var first, out var n)) return false;
    for (long i = 0; i < n; i++)
    {
        var row = SafePtr(reader, first + (nint)(i * 8));
        if (row == 0) continue;
        var label = RfChild(reader, row, 0);
        if (label != 0 && !string.IsNullOrEmpty(ReadStdWString(reader, label + RuneforgeNameWString))) return true;
    }
    return false;
}

static bool RfChildren(MemoryReader reader, nint el, out nint first, out long n)
{
    first = 0; n = 0;
    first = SafePtr(reader, el + Poe2.UiElement.Children);
    if (first == 0) return false;
    if (!reader.TryReadStruct<nint>(el + Poe2.UiElement.Children + 8, out var last)) return false;
    n = ((long)last - (long)first) / 8;
    return n is > 0 and <= 4000;
}

static nint RfChild(MemoryReader reader, nint el, int index)
{
    if (!RfChildren(reader, el, out var first, out var n) || index < 0 || index >= n) return 0;
    return SafePtr(reader, first + (nint)(index * 8));
}

// Parent-accumulated UNSCALED position (relPos + parent chain; PositionModifier when flag 0x0A set).
// The final ×(winSize/2560,1600) scaling is done overlay-side where the window size is known.
static (float x, float y) RfUnscaledPos(MemoryReader reader, nint el, int depth)
{
    reader.TryReadStruct<float>(el + 0x118, out var lx);
    reader.TryReadStruct<float>(el + 0x11C, out var ly);
    var parent = SafePtr(reader, el + Poe2.UiElement.Parent);
    if (parent == 0 || depth >= 64) return (lx, ly);
    var (px, py) = RfUnscaledPos(reader, parent, depth + 1);
    if (reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var f) && (f & (1u << 0x0A)) != 0)
    {
        reader.TryReadStruct<float>(el + 0xF0, out var mx);
        reader.TryReadStruct<float>(el + 0xF4, out var my);
        px += mx; py += my;
    }
    return (px + lx, py + ly);
}

static void RfParseNameCount(string raw, out int count, out string name)
{
    count = 1; name = raw?.Trim() ?? "";
    if (name.Length == 0) return;
    var i = 0;
    while (i < name.Length && char.IsDigit(name[i])) i++;
    if (i > 0 && i < name.Length && (name[i] == 'x' || name[i] == 'X') && int.TryParse(name.AsSpan(0, i), out var c) && c > 0)
    { count = c; name = name[(i + 1)..].TrimStart(); }
}

static void RfDumpChildFlags(MemoryReader reader, nint gameUi)
{
    const uint UiVisibleMask = 1u << Poe2.UiElement.FlagVisibleBit;
    if (gameUi == 0 || !RfChildren(reader, gameUi, out var first, out var n)) { Console.WriteLine("(no GameUi children)"); return; }
    for (long i = 0; i < n && i < 40; i++)
    {
        var child = SafePtr(reader, first + (nint)(i * 8));
        if (child == 0) continue;
        reader.TryReadStruct<uint>(child + Poe2.UiElement.Flags, out var flags);
        Console.WriteLine($"  child[{i}] 0x{child:X} flags=0x{flags:X8} (masked 0x{flags & ~UiVisibleMask:X8}) visible={(flags & UiVisibleMask) != 0}");
    }
}

// ── Loot-label VECTOR finder ────────────────────────────────────────────────────────────────────
// The game's on-ground item name tags are NOT in the UiElement children tree (an item ptr never sits
// in an element body — confirmed by --groundlabels). They live in an ExileCore-style LabelsOnGround
// vector of LabelOnGround structs, each linking an on-ground ITEM entity ↔ its label UiElement. We
// find that vector by its repeating signature: an ARRAY where every entry holds a known on-ground-item
// pointer at a fixed offset AND a UiElement pointer at another fixed offset. That two-field-pattern
// across many contiguous entries is unambiguous (plain entity arrays have no UiElement neighbor).
//
// Run with items on the ground + PoE2 FOCUSED (labels only lay out while rendering). If you HOVER one,
// its label rect should contain the cursor — printed as a cross-check so we can confirm the mapping.
static int RunLootVec(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, ai, _) = ResolveChain(process, reader);
    if (ai == 0 || igs == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }

    float winW = 2560, winH = 1600; var hwnd = Win.GetForegroundWindow();
    if (hwnd != 0 && Win.GetClientRect(hwnd, out var rc) && rc.right > 0) { winW = rc.right; winH = rc.bottom; }
    Win.POINT curPt = default; var haveCur = Win.GetCursorPos(out curPt) && hwnd != 0 && Win.ScreenToClient(hwnd, ref curPt);
    Console.WriteLine($"window {winW}x{winH}  cursor(client)=({curPt.X},{curPt.Y})  (hover the Warding Rune for the cross-check)");

    // On-ground items: container + inner entity → label. The LabelOnGround struct referenced the CONTAINER
    // in --groundlabels, so containers are the primary keys (inner included for completeness).
    var known = new Dictionary<long, string>();
    foreach (var (id, ent, meta) in WalkEntities(reader, ai))
    {
        if (!meta.Contains("WorldItem", StringComparison.Ordinal)) continue;
        var wi = ResolveComponentAddr(reader, ent, "WorldItem");
        var inner = wi == 0 ? 0 : SafePtr(reader, wi + Poe2.WorldItemComponent.ItemEntity);
        var art = "?";
        if (inner != 0) { var ri = ResolveComponentAddr(reader, inner, "RenderItem"); if (ri != 0) art = ItemArtBasename(reader.ReadStringUtf16(SafePtr(reader, ri + Poe2.RenderItemComponent.ResourcePath), 128)) ?? "?"; }
        known[(long)ent] = art + "(container)";
        if (inner != 0) known[(long)inner] = art + "(item)";
    }
    if (known.Count == 0) { Console.WriteLine("no on-ground items."); return 0; }
    Console.WriteLine($"on-ground item pointers: {known.Count} ({known.Values.Where(v => v.EndsWith("(container)")).Count()} containers)");

    // 1) Heap scan: every location holding a known item pointer.
    var occ = new List<nint>();
    var buf = new byte[1 << 20];
    foreach (var (regBase, regSize) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
    {
        for (long o = 0; o < regSize && occ.Count < 20000; o += buf.Length)
        {
            var want = (int)Math.Min(buf.Length, regSize - o);
            var got = reader.TryReadBytes(regBase + (nint)o, buf.AsSpan(0, want));
            if (got <= 0) continue;
            for (var i = 0; i + 8 <= got; i += 8)
                if (known.ContainsKey(BitConverter.ToInt64(buf, i))) occ.Add(regBase + (nint)(o + i));
        }
        if (occ.Count >= 20000) break;
    }
    bool IsUi(nint p) => p != 0 && SafePtr(reader, p + Poe2.UiElement.Self) == p;
    Console.WriteLine($"item-pointer occurrences: {occ.Count}");

    // 2) Candidate wrappers: every item-ptr occurrence that has a UiElement somewhere in a small window
    //    (broad — persistence below removes the junk). Record (at, labelOffset, labelElement, itemPtr).
    var cands = new List<(nint at, int off, nint lbl, long item)>();
    foreach (var at in occ)
    {
        if (!reader.TryReadStruct<long>(at, out var itemv)) continue;
        for (var d = -0x60; d <= 0x80; d += 8)
        {
            if (d == 0) continue;
            var le = SafePtr(reader, at + d);
            if (IsUi(le)) { cands.Add((at, d, le, itemv)); break; }
        }
    }
    Console.WriteLine($"raw UiElement-neighbor candidates: {cands.Count}");

    // 3) PERSISTENCE FILTER — the killer step. One-shot scans keep catching TRANSIENT render/stack layout
    //    (last run's "wrapper" was on the stack, item ptr already overwritten). Real LabelsOnGround entries
    //    are stable, so re-read each candidate several times over ~2s and keep only those whose
    //    (item ptr, label element) stay put. KEEP PoE2 FOCUSED + the items in view during this.
    for (var s = 0; s < 6 && cands.Count > 0; s++)
    {
        System.Threading.Thread.Sleep(350);
        cands = cands.Where(c =>
            reader.TryReadStruct<long>(c.at, out var iv) && iv == c.item && known.ContainsKey(iv)
            && SafePtr(reader, c.at + c.off) == c.lbl && IsUi(c.lbl)).ToList();
    }
    Console.WriteLine($"PERSISTENT wrappers after filter: {cands.Count}");
    if (cands.Count == 0)
    {
        Console.WriteLine("none persisted → the loot-label layout here is transient (no stable heap vector this scan caught).");
        Console.WriteLine("Next: resolve continuously IN THE OVERLAY (observe across frames), per the prior research note.");
        return 0;
    }
    Console.WriteLine("survivor label-offset histogram: " + string.Join("  ", cands.GroupBy(c => c.off).OrderByDescending(g => g.Count()).Select(g => $"{(g.Key < 0 ? "-" : "+")}0x{Math.Abs(g.Key):X}={g.Count()}")));

    // 4) Dump EVERY survivor offset group so the real item↔label link is visible (sizes/vis/cursor).
    foreach (var grp in cands.GroupBy(c => c.off).OrderByDescending(g => g.Count()).Take(4))
    {
        Console.WriteLine($"\n-- label offset {(grp.Key < 0 ? "-" : "+")}0x{Math.Abs(grp.Key):X} ({grp.Count()}) --");
        foreach (var c in grp.OrderBy(x => (long)x.at)) DumpEntryAt(c.at, c.off);
    }

    // 5) THE KEY STRUCTURE — the contiguous on-ground item-pointer array (the labels collection's item
    //    list): from each known-item-ptr location, walk ±8 while neighbors are DISTINCT known items.
    var occSet = new HashSet<long>(occ.Select(a => (long)a));
    (long baseAddr, int count) arr = (0, 0);
    foreach (var at in occ)
    {
        long lo = (long)at; while (occSet.Contains(lo - 8) && DistinctItem(lo - 8, lo)) lo -= 8;
        var cnt = 0; var p = lo; var seen = new HashSet<long>();
        while (occSet.Contains(p) && reader.TryReadStruct<long>((nint)p, out var iv) && known.ContainsKey(iv) && seen.Add(iv)) { cnt++; p += 8; }
        if (cnt > arr.count) arr = (lo, cnt);
    }
    bool DistinctItem(long a, long b)
        => reader.TryReadStruct<long>((nint)a, out var va) && reader.TryReadStruct<long>((nint)b, out var vb) && va != vb && known.ContainsKey(va);
    Console.WriteLine($"\n*** on-ground item array: base=0x{arr.baseAddr:X} count={arr.count} (stride 8) ***");
    for (var k = 0; k < arr.count; k++) { reader.TryReadStruct<long>((nint)(arr.baseAddr + k * 8), out var iv); Console.WriteLine($"   [{k}] 0x{iv:X}  {known.GetValueOrDefault(iv, "?")}"); }

    // 6) Find what references the item-array base → the std::vector {first,last,cap} → the owning
    //    ItemsOnGroundLabelElement. From there the overlay can resolve it stably + find the label elements.
    List<nint> FindRefs(long target, int cap)
    {
        var hits = new List<nint>(); var b = new byte[1 << 20];
        foreach (var (rb, rs) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
        {
            for (long o = 0; o < rs && hits.Count < cap; o += b.Length)
            {
                var want = (int)Math.Min(b.Length, rs - o);
                var got = reader.TryReadBytes(rb + (nint)o, b.AsSpan(0, want));
                if (got <= 0) continue;
                for (var i = 0; i + 8 <= got; i += 8) if (BitConverter.ToInt64(b, i) == target) { hits.Add(rb + (nint)(o + i)); if (hits.Count >= cap) break; }
            }
            if (hits.Count >= cap) break;
        }
        return hits;
    }
    // 7) Find the LABELS COLLECTION: pick the survivor offset group that looks like real loot tags
    //    (visible + on-screen-sized label elements), then for several wrappers find what POINTS at the
    //    wrapper (= a vector<LabelOnGround*> element slot). Cluster those slots → the vector array →
    //    refs to the array base → the owning ItemsOnGroundLabelElement (a stable anchor).
    var realGrp = cands.GroupBy(c => c.off).Where(g => g.Key != 0)
        .OrderByDescending(g => g.Count(c =>
        {
            reader.TryReadStruct<uint>(c.lbl + Poe2.UiElement.Flags, out var fl);
            reader.TryReadStruct<float>(c.lbl + Poe2.UiElement.SizeW, out var w);
            return (fl & (1u << Poe2.UiElement.FlagVisibleBit)) != 0 && w is > 30 and < 1000;
        })).First();
    var realOff = realGrp.Key;
    Console.WriteLine($"\n=== labels-vector hunt @ wrapper offset {(realOff < 0 ? "-" : "+")}0x{Math.Abs(realOff):X} ({realGrp.Count()} wrappers) ===");
    var slots = new List<long>();
    foreach (var c in realGrp.OrderBy(x => (long)x.at).Take(4))
    {
        var wbase = (long)c.at + realOff;   // wrapper start = the label-ptr field
        foreach (var r in FindRefs(wbase, 4))
        {
            slots.Add((long)r);
            Console.WriteLine($"  wrapper 0x{wbase:X} ({known.GetValueOrDefault(c.item, "?")}) <- ref @0x{r:X}");
        }
    }
    // Cluster the slot addresses into a contiguous 8-stride run = the vector's element array.
    slots.Sort();
    long arrLo = 0; var arrLen = 0;
    foreach (var s in slots)
    {
        var len = 1; var p = s; var set = new HashSet<long>(slots);
        while (set.Contains(p + 8)) { p += 8; len++; }
        if (len > arrLen) { arrLen = len; arrLo = s; }
    }
    if (arrLen >= 2)
    {
        Console.WriteLine($"\n*** labels vector element array: base=0x{arrLo:X} (>= {arrLen} contiguous wrapper-ptr slots) ***");
        foreach (var r in FindRefs(arrLo, 6))   // who holds vector.begin == arrLo → the container
        {
            reader.TryReadStruct<nint>((nint)((long)r + 8), out var last);
            var span = (long)last - arrLo;
            Console.WriteLine($"   container/vec @0x{r:X}  begin=0x{arrLo:X} last@+8=0x{last:X}  span=0x{span:X}{(span > 0 && span < 0x8000 && span % 8 == 0 ? $"  (count≈{span / 8})  <== VECTOR" : "")}");
        }
    }
    else Console.WriteLine("\n(no contiguous wrapper-ptr array among the refs — wrappers may be held individually; inspect the refs above)");
    return 0;

    void DumpEntryAt(nint at, int off)
    {
        var item = SafePtr(reader, at);
        var lbl = SafePtr(reader, at + off);
        var name = known.GetValueOrDefault((long)item, "?");
        reader.TryReadStruct<float>(lbl + Poe2.UiElement.SizeW, out var sw);
        reader.TryReadStruct<float>(lbl + Poe2.UiElement.SizeH, out var sh);
        reader.TryReadStruct<byte>(lbl + 0x18A, out var sidx);
        reader.TryReadStruct<float>(lbl + 0x130, out var smul);
        reader.TryReadStruct<uint>(lbl + Poe2.UiElement.Flags, out var flags);
        var vis = (flags & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;
        var (ux, uy) = RfUnscaledPos(reader, lbl, 0);
        float v2 = winH / 1600f, v1 = winW / 2560f;
        float scl = sidx == 2 ? v2 : sidx == 1 ? v1 : (sidx == 3 ? v2 : (smul == 0 ? 1 : smul));
        float scx = ux * (sidx == 3 ? v1 : scl), scy = uy * scl, scw = sw * (sidx == 3 ? v1 : scl), sch = sh * scl;
        var isHot = haveCur && curPt.X >= scx && curPt.X <= scx + scw && curPt.Y >= scy && curPt.Y <= scy + sch;
        Console.WriteLine($"  entry@0x{at:X} {name,-22} label=0x{lbl:X} vis={(vis ? "Y" : "n")} size=({sw:0}x{sh:0}) idx={sidx} screen=({scx:0},{scy:0} {scw:0}x{sch:0}){(isHot ? "  <== CURSOR" : "")}");
    }
}

// ── Loot labels via CURSOR + screen rect (top-down, robust) ───────────────────────────────────────
// Instead of heap-scanning (which keeps catching transient stack layout), find the loot label the user
// is HOVERING: BFS the UiElement tree, compute each visible element's screen rect, and pick the smallest
// text-sized one whose rect contains the cursor — that's the hovered item's label element, persistent and
// reachable from UiRoot. Then (a) print its ancestor chain (→ the container = stable anchor) and (b)
// heap-scan for the lone struct that references THAT element (a clean target) → the LabelOnGround wrapper
// (item ptr at +0x48) → the labels vector. Run with PoE2 focused + the cursor ON an item's name tag.
static int RunLootCursor(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, ai, _) = ResolveChain(process, reader);
    if (ai == 0 || igs == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    var uiRoot = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    if (uiRoot == 0) { Console.Error.WriteLine("no UiRoot."); return 1; }

    float winW = 2560, winH = 1600; var hwnd = Win.GetForegroundWindow();
    if (hwnd != 0 && Win.GetClientRect(hwnd, out var rc) && rc.right > 0) { winW = rc.right; winH = rc.bottom; }
    if (!(Win.GetCursorPos(out var cp) && hwnd != 0 && Win.ScreenToClient(hwnd, ref cp))) { Console.Error.WriteLine("no cursor / not focused."); return 1; }
    float curX = cp.X, curY = cp.Y;
    Console.WriteLine($"window {winW}x{winH}  cursor(client)=({curX},{curY}) — hover an item's NAME TAG.");

    (float x, float y, float w, float h, bool ok) Rect(nint el)
    {
        reader.TryReadStruct<byte>(el + 0x18A, out var sidx);
        reader.TryReadStruct<float>(el + 0x130, out var smul);
        reader.TryReadStruct<float>(el + Poe2.UiElement.SizeW, out var sw);
        reader.TryReadStruct<float>(el + Poe2.UiElement.SizeH, out var sh);
        var (ux, uy) = RfUnscaledPos(reader, el, 0);
        float v2 = winH / 1600f, v1 = winW / 2560f;
        float scl = sidx == 2 ? v2 : sidx == 1 ? v1 : (sidx == 3 ? v2 : (smul == 0 ? 1 : smul));
        float sx = ux * (sidx == 3 ? v1 : scl), sy = uy * scl, w = sw * (sidx == 3 ? v1 : scl), h = sh * scl;
        return (sx, sy, w, h, w > 0 && h > 0 && float.IsFinite(sx) && float.IsFinite(sy));
    }

    // BFS the tree; collect VISIBLE elements whose screen rect contains the cursor.
    var q = new Queue<(nint el, int depth)>(); q.Enqueue((uiRoot, 0));
    var vis = new HashSet<nint>();
    var hits = new List<(nint el, int depth, float x, float y, float w, float h)>();
    while (q.Count > 0 && vis.Count < 120000)
    {
        var (el, depth) = q.Dequeue();
        if (el == 0 || depth > 24 || !vis.Add(el)) continue;
        if (reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var fl) && (fl & (1u << Poe2.UiElement.FlagVisibleBit)) != 0)
        {
            var r = Rect(el);
            if (r.ok && curX >= r.x && curX <= r.x + r.w && curY >= r.y && curY <= r.y + r.h && r.w < 1400 && r.h < 500)
                hits.Add((el, depth, r.x, r.y, r.w, r.h));
        }
        var first = SafePtr(reader, el + Poe2.UiElement.Children);
        if (first != 0 && reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last))
        {
            var n = ((long)last - (long)first) / 8;
            if (n is > 0 and <= 4096) for (long i = 0; i < n; i++) { var ch = SafePtr(reader, first + (nint)(i * 8)); if (ch != 0) q.Enqueue((ch, depth + 1)); }
        }
    }
    Console.WriteLine($"visited {vis.Count} elements; {hits.Count} visible under the cursor.");
    hits.Sort((a, b) => (a.w * a.h).CompareTo(b.w * b.h));   // smallest (innermost) first
    Console.WriteLine("\n=== elements under cursor (smallest first) ===");
    foreach (var h in hits.Take(14))
        Console.WriteLine($"  0x{h.el:X} depth={h.depth,2} rect=({h.x:0},{h.y:0} {h.w:0}x{h.h:0})");

    if (hits.Count == 0) { Console.WriteLine("nothing under cursor — is the tag laid out? hover the NAME text, keep PoE2 focused."); return 0; }

    // The hovered label = the smallest text-tag-sized element under the cursor (height ~14..45, width > 30).
    var label = hits.FirstOrDefault(h => h.h is > 12 and < 50 && h.w > 30);
    if (label.el == 0) label = hits[0];
    Console.WriteLine($"\nHOVERED LABEL = 0x{label.el:X}  rect=({label.x:0},{label.y:0} {label.w:0}x{label.h:0}) depth={label.depth}");

    // TEXT scan — the tag's text is the item NAME (our price key). The cursor may land on the icon, so
    // walk UP to the label-group container, then BFS its subtree and try every qword offset on each
    // element for a std::wstring. Surfaces the name text + the exact (element,offset) to read at runtime.
    void TextScan(nint el, string who)
    {
        for (var off = 0; off <= 0x600; off += 8)
        {
            var s = ReadStdWString(reader, el + off);
            if (s.Length >= 2 && s.Length <= 80 && s.All(ch => ch >= ' ' && ch < (char)0x7f))
                Console.WriteLine($"   {who} 0x{el:X} +0x{off:X3} = \"{s}\"");
        }
    }
    // climb to a group container (a parent with a text-bar-ish width)
    var grp = label.el;
    for (var i = 0; i < 4; i++) { var p = SafePtr(reader, grp + Poe2.UiElement.Parent); if (p == 0 || p == uiRoot) break; grp = p; }
    Console.WriteLine($"=== text scan of label group 0x{grp:X} subtree (item name = price key) ===");
    var tq = new Queue<(nint el, int d)>(); tq.Enqueue((grp, 0));
    var tv = new HashSet<nint>(); var scanned = 0;
    while (tq.Count > 0 && scanned < 200)
    {
        var (el, d) = tq.Dequeue();
        if (el == 0 || d > 4 || !tv.Add(el)) continue;
        scanned++;
        TextScan(el, $"d{d}");
        var f = SafePtr(reader, el + Poe2.UiElement.Children);
        if (f != 0 && reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var l))
        { var n = ((long)l - (long)f) / 8; if (n is > 0 and <= 64) for (long k = 0; k < n; k++) tq.Enqueue((SafePtr(reader, f + (nint)(k * 8)), d + 1)); }
    }

    // Ancestor chain → the container that holds all loot labels (a stable anchor under UiRoot).
    Console.WriteLine("\n=== ancestor chain (→ ItemsOnGroundLabelElement container) ===");
    var chain = new List<nint>();
    var cur = label.el;
    for (var lvl = 0; lvl < 18 && cur != 0; lvl++)
    {
        chain.Add(cur);
        var parent = SafePtr(reader, cur + Poe2.UiElement.Parent);
        long childN = 0;
        var cf = SafePtr(reader, cur + Poe2.UiElement.Children);
        if (cf != 0 && reader.TryReadStruct<nint>(cur + Poe2.UiElement.ChildrenEnd, out var cl)) childN = ((long)cl - (long)cf) / 8;
        reader.TryReadStruct<float>(cur + Poe2.UiElement.SizeW, out var sw);
        reader.TryReadStruct<float>(cur + Poe2.UiElement.SizeH, out var sh);
        Console.WriteLine($"  lvl{lvl,2}: 0x{cur:X} children={childN,-4} size=({sw:0}x{sh:0}) {(cur == uiRoot ? "<= UiRoot" : "")}");
        if (cur == uiRoot) break;
        cur = parent;
    }

    // Heap-scan for the struct that references THIS label element (clean target). Its neighbor that is an
    // on-ground item = the LabelOnGround wrapper → confirms item↔label offset + leads to the vector.
    var known = new Dictionary<long, string>();
    var idArt = new Dictionary<uint, string>();   // entity std::map KeyId → art (for the ID-link test)
    foreach (var (id, ent, meta) in WalkEntities(reader, ai))
    {
        if (!meta.Contains("WorldItem", StringComparison.Ordinal)) continue;
        var wi = ResolveComponentAddr(reader, ent, "WorldItem");
        var inner = wi == 0 ? 0 : SafePtr(reader, wi + Poe2.WorldItemComponent.ItemEntity);
        var art = "?";
        if (inner != 0) { var ri = ResolveComponentAddr(reader, inner, "RenderItem"); if (ri != 0) art = ItemArtBasename(reader.ReadStringUtf16(SafePtr(reader, ri + Poe2.RenderItemComponent.ResourcePath), 128)) ?? "?"; }
        known[(long)ent] = art + "(container)"; if (inner != 0) known[(long)inner] = art + "(item)";
        if (id != 0) idArt[id] = art;
    }
    Console.WriteLine($"on-ground item ids: {string.Join(", ", idArt.Select(kv => $"{kv.Value}=#{kv.Key}"))}");
    Console.WriteLine($"\n=== heap refs to label element 0x{label.el:X} (→ LabelOnGround wrapper) ===");
    var b = new byte[1 << 20]; var found = 0;
    foreach (var (rb, rs) in process.EnumerateReadableRegions(privateOnly: true, excludeImage: true))
    {
        for (long o = 0; o < rs && found < 10; o += b.Length)
        {
            var want = (int)Math.Min(b.Length, rs - o);
            var got = reader.TryReadBytes(rb + (nint)o, b.AsSpan(0, want));
            if (got <= 0) continue;
            for (var i = 0; i + 8 <= got; i += 8)
            {
                if (BitConverter.ToInt64(b, i) != (long)label.el) continue;
                var at = rb + (nint)(o + i);   // a field holding the label-element ptr (the wrapper)
                found++;
                Console.WriteLine($"  ref @0x{at:X}:");
                for (var d = -0x20; d <= 0x60; d += 8)
                {
                    var v = SafePtr(reader, at + d);
                    var tag = known.TryGetValue((long)v, out var kn) ? $"  ITEM:{kn}" : v == label.el ? "  <-LABEL" : "";
                    if (tag != "") Console.WriteLine($"     {(d < 0 ? "-" : "+")}0x{Math.Abs(d):X2} = 0x{v:X}{tag}");
                }
                if (found >= 10) break;
            }
        }
        if (found >= 10) break;
    }
    if (found == 0) Console.WriteLine("  (no heap ref — the label ptr may live only on the stack; rely on the tree container above)");

    // ID-LINK TEST: games often link UI→entity by a stable uint ID, not a pointer (a pointer scan can't
    // see it). Scan the hovered label + its ancestor panels for a uint32 == an on-ground item's entity
    // id. A hit = the exact, robust link (label panel +offset holds the item id → match to our walked id).
    Console.WriteLine($"\n=== ID-link test: scanning hovered label + ancestors for an item entity id (uint32) ===");
    var idHits = 0;
    foreach (var el in chain)
    {
        var bb = new byte[0x600];
        var n = reader.TryReadBytes(el, bb);
        for (var o = 0; o + 4 <= n; o += 4)
        {
            var v = BitConverter.ToUInt32(bb, o);
            if (idArt.TryGetValue(v, out var art)) { Console.WriteLine($"   el 0x{el:X} +0x{o:X3} = #{v}  ITEM:{art}"); idHits++; }
        }
    }
    if (idHits == 0) Console.WriteLine("   (no item-id field in the label subtree — link is neither pointer nor entity-id here)");
    return 0;
}

// Fully MAP the ItemsOnGroundLabelElement: find the container (the UI element holding pointers to the
// on-ground item entities), characterize exactly HOW it stores them (inline entity-ptr array, std::vector
// of Entity*, or a struct array {Entity*,Element*,…}), find the element stride + the entity/label offsets,
// and pair the entries to the label elements. Goal: a documented, reliable structure (no guessing).
static int RunLootMap(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, ai, _) = ResolveChain(process, reader);
    if (ai == 0 || igs == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    var uiRoot = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    if (uiRoot == 0) { Console.Error.WriteLine("no UiRoot."); return 1; }

    // On-ground item entities (container + inner item) → label. The link points at one of these.
    var item = new Dictionary<long, string>();
    var nItems = 0;
    foreach (var (id, ent, meta) in WalkEntities(reader, ai))
    {
        if (!meta.Contains("WorldItem", StringComparison.Ordinal)) continue;
        var wi = ResolveComponentAddr(reader, ent, "WorldItem");
        var inner = wi == 0 ? 0 : SafePtr(reader, wi + Poe2.WorldItemComponent.ItemEntity);
        var art = "?";
        if (inner != 0) { var ri = ResolveComponentAddr(reader, inner, "RenderItem"); if (ri != 0) art = ItemArtBasename(reader.ReadStringUtf16(SafePtr(reader, ri + Poe2.RenderItemComponent.ResourcePath), 128)) ?? "?"; }
        item[(long)ent] = art; if (inner != 0) item[(long)inner] = art + ":inner";
        nItems++;
    }
    Console.WriteLine($"on-ground WorldItems: {nItems} (tracked ptrs {item.Count})");
    bool IsUi(nint p) => p != 0 && SafePtr(reader, p + Poe2.UiElement.Self) == p;
    bool HasText(nint p) => p != 0 && ReadStdWString(reader, p + Poe2.UiElement.Text).Length >= 2;

    // Find the CONTAINER: scan the whole UI tree for the element with the MOST inline item-entity pointers
    // in its struct (the ItemsOnGroundLabelElement holds them all). This is robust (no cursor / text needed).
    var q = new Queue<nint>(); q.Enqueue(uiRoot); var vis = new HashSet<nint>();
    nint bestEl = 0; var bestCount = 0; int bestFirstOff = 0;
    while (q.Count > 0 && vis.Count < 120000)
    {
        var el = q.Dequeue();
        if (el == 0 || !vis.Add(el)) continue;
        var hitCount = 0; var firstOff = -1;
        for (var off = 0; off <= 0x1200; off += 8)
        {
            var v = SafePtr(reader, el + off);
            if (v != 0 && item.ContainsKey((long)v)) { hitCount++; if (firstOff < 0) firstOff = off; }
        }
        if (hitCount > bestCount) { bestCount = hitCount; bestEl = el; bestFirstOff = firstOff; }
        var f = SafePtr(reader, el + Poe2.UiElement.Children);
        if (f != 0 && reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var l))
        { var n = ((long)l - (long)f) / 8; if (n is > 0 and <= 4096) for (long i = 0; i < n; i++) q.Enqueue(SafePtr(reader, f + (nint)(i * 8))); }
    }
    if (bestEl == 0 || bestCount == 0) { Console.WriteLine("no UI element holds on-ground item ptrs (focus game + items on ground)."); return 0; }
    Console.WriteLine($"\n*** CONTAINER = 0x{bestEl:X}  (holds {bestCount} item-entity ptrs; first @ +0x{bestFirstOff:X}) ***");

    // Dump the region around the item-ptr block so the layout is unambiguous: annotate each qword as an
    // ITEM, a UiElement (the paired label!), another canonical ptr, or a raw int/float.
    Console.WriteLine("=== container qword dump around the item block ===");
    var lo = Math.Max(0, bestFirstOff - 0x40);
    for (var off = lo; off <= bestFirstOff + 0x140; off += 8)
    {
        var v = SafePtr(reader, bestEl + off);
        string tag;
        if (v != 0 && item.TryGetValue((long)v, out var art)) tag = $"ITEM {art}";
        else if (IsUi(v)) tag = HasText(v) ? $"UiElement(text=\"{ReadStdWString(reader, v + Poe2.UiElement.Text).Split('\n')[0]}\")" : "UiElement";
        else if ((ulong)v >= 0x10000 && (ulong)v <= 0x7FFFFFFFFFFF) tag = "ptr";
        else { reader.TryReadStruct<long>(bestEl + off, out var raw); tag = $"raw={raw}"; }
        Console.WriteLine($"   +0x{off:X3} = 0x{v:X}   {tag}");
    }

    // Try to read it as a std::vector at the most likely base: many vectors are {begin,end,cap}. Look for
    // an offset O (near the block) where *(O)=begin, *(O+8)=end bound a clean array; report stride+count by
    // testing candidate strides (8 = Entity*/ptr vector; 0x10/0x18/0x20 = struct array with item @ +0).
    Console.WriteLine("\n=== std::vector interpretation attempts ===");
    for (var vo = lo; vo <= bestFirstOff; vo += 8)
    {
        var begin = SafePtr(reader, bestEl + vo);
        if (begin == 0 || !reader.TryReadStruct<nint>(bestEl + vo + 8, out var end)) continue;
        var bytes = (long)end - (long)begin;
        if (bytes <= 0 || bytes > 0x4000) continue;
        foreach (var stride in new[] { 8, 0x10, 0x18, 0x20, 0x28, 0x30 })
        {
            if (bytes % stride != 0) continue;
            var cnt = (int)(bytes / stride);
            if (cnt is < 1 or > 200) continue;
            // validate: element +0 (and +8) are item/UiElement across entries
            var itemHits = 0; var uiHits = 0; int uiOff = -1;
            for (var k = 0; k < cnt; k++)
            {
                var e0 = SafePtr(reader, begin + (nint)(k * stride));
                if (e0 != 0 && item.ContainsKey((long)e0)) itemHits++;
                for (var so = 8; so < stride; so += 8) { var ev = SafePtr(reader, begin + (nint)(k * stride + so)); if (IsUi(ev)) { uiHits++; if (uiOff < 0) uiOff = so; break; } }
            }
            if (itemHits >= Math.Max(2, cnt / 2))
            {
                Console.WriteLine($"  VECTOR @container+0x{vo:X}: begin=0x{begin:X} end=0x{end:X} stride=0x{stride:X} count={cnt}  itemHits={itemHits} uiHits={uiHits}{(uiOff >= 0 ? $" labelOff=+0x{uiOff:X}" : "")}");
                for (var k = 0; k < cnt && k < 16; k++)
                {
                    var e0 = SafePtr(reader, begin + (nint)(k * stride));
                    var lbl = uiOff >= 0 ? SafePtr(reader, begin + (nint)(k * stride + uiOff)) : 0;
                    var lt = lbl != 0 ? ReadStdWString(reader, lbl + Poe2.UiElement.Text).Split('\n')[0] : "";
                    Console.WriteLine($"     [{k,2}] item=0x{e0:X} {item.GetValueOrDefault((long)e0, "?")}  label=0x{lbl:X} \"{lt}\"");
                }
                Console.WriteLine($"  --- documenting this as the LabelsOnGround vector ---");
                break;
            }
        }
    }
    return 0;
}

// Poll a named loot tag rapidly (~20 Hz, ~4 s) to characterize WHY the overlay chip looks low-refresh.
// Run it WHILE MOVING the character with the item on the ground + PoE2 focused. Each poll re-finds the
// visible text element whose first line == <name> and records its UiElement address + RelativePos.
// Distinguishes the three causes:
//   (a) MANY distinct addresses  → the game recreates the label element (transient) → the overlay's cached
//       address goes stale/frozen between scans → must re-resolve the element per RENDER frame (e.g. cache
//       the loot-label container and enumerate its children each frame), not off a 200 ms world scan.
//   (b) ONE stable address + relPos changing nearly every sample → the value updates fine; the chop is the
//       overlay FpsCap vs a high-refresh monitor → raise FpsCap.
//   (c) ONE stable address + relPos changing only every ~200 ms → the source value itself updates slowly
//       (or our scan gates it) → interpolate the chip toward the latest target.
static int RunLootWatch(ProcessHandle process, MemoryReader reader, string name)
{
    name = name.Trim();
    Console.WriteLine($"Locating loot tag \"{name}\" (one BFS)...");
    var (_, igs0, _, _) = ResolveChain(process, reader);
    var uiRoot = igs0 == 0 ? 0 : SafePtr(reader, igs0 + Poe2.InGameState.UiRoot);
    nint el = 0;
    if (uiRoot != 0)
    {
        var q = new Queue<nint>(); q.Enqueue(uiRoot); var vis = new HashSet<nint>();
        while (q.Count > 0 && vis.Count < 60000 && el == 0)
        {
            var e = q.Dequeue(); if (e == 0 || !vis.Add(e)) continue;
            if (!(reader.TryReadStruct<uint>(e + Poe2.UiElement.Flags, out var fl) && (fl & (1u << Poe2.UiElement.FlagVisibleBit)) != 0)) continue;
            var t = ReadStdWString(reader, e + Poe2.UiElement.Text);
            if (t.Length >= 2) { var nl = t.IndexOf('\n'); if (string.Equals((nl >= 0 ? t[..nl] : t).Trim(), name, StringComparison.OrdinalIgnoreCase)) el = e; }
            var f = SafePtr(reader, e + Poe2.UiElement.Children);
            if (f != 0 && reader.TryReadStruct<nint>(e + Poe2.UiElement.ChildrenEnd, out var l))
            { var n = ((long)l - (long)f) / 8; if (n is > 0 and <= 4096) for (long i = 0; i < n; i++) q.Enqueue(SafePtr(reader, f + (nint)(i * 8))); }
        }
    }
    if (el == 0) { Console.WriteLine("not found — is the item on the ground + PoE2 focused?"); return 0; }
    Console.WriteLine($"locked element 0x{el:X}. MOVE your character continuously for ~10s now (keep PoE2 focused)...");

    // Fast-poll the LOCKED address (no BFS) to measure the TRUE update rate of its RelativePos. Records the
    // interval between distinct values: that interval's inverse is the rate the game refreshes the label pos.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int polls = 0, textOk = 0, changes = 0;
    (float x, float y) last = (float.NaN, float.NaN);
    long lastChangeMs = -1; var intervals = new List<long>();
    var firstSamples = new List<(long t, float x, float y)>();
    while (sw.ElapsedMilliseconds < 10000)
    {
        polls++;
        var t = ReadStdWString(reader, el + Poe2.UiElement.Text);
        var nl = t.IndexOf('\n');
        if (string.Equals((nl >= 0 ? t[..nl] : t).Trim(), name, StringComparison.OrdinalIgnoreCase)) textOk++;
        reader.TryReadStruct<System.Numerics.Vector2>(el + Poe2.UiElement.RelativePos, out var rel);
        if (!(Math.Abs(rel.X - last.x) < 0.01f && Math.Abs(rel.Y - last.y) < 0.01f))
        {
            var now = sw.ElapsedMilliseconds;
            if (lastChangeMs >= 0) intervals.Add(now - lastChangeMs);
            lastChangeMs = now; changes++; last = (rel.X, rel.Y);
            if (firstSamples.Count < 30) firstSamples.Add((now, rel.X, rel.Y));
        }
        System.Threading.Thread.Sleep(2);
    }
    var pollHz = polls / 10.0;
    Console.WriteLine($"\npolls={polls} (~{pollHz:0} Hz poll)  text-still-matches={textOk}/{polls}");
    if (intervals.Count > 0)
    {
        intervals.Sort();
        var avg = intervals.Average();
        var med = intervals[intervals.Count / 2];
        Console.WriteLine($"relPos distinct changes={changes}  interval ms: min={intervals[0]} median={med} avg={avg:0.0} max={intervals[^1]}");
        Console.WriteLine($"=> source update rate ~ {(med > 0 ? 1000.0 / med : 0):0} Hz (median)");
    }
    else Console.WriteLine($"relPos NEVER changed over 4s (you weren't moving, or the value is static). Re-run while moving.");
    Console.WriteLine("\nfirst distinct-value samples (t ms, relPos):");
    foreach (var s in firstSamples) Console.WriteLine($"  t={s.t,4} ({s.x:0.0},{s.y:0.0})");
    Console.WriteLine("\nINTERPRETATION (while moving):");
    Console.WriteLine($"  update rate ~ monitor Hz   => source is smooth; the chop is overlay FpsCap (raise it to match).");
    Console.WriteLine($"  update rate low (10-30 Hz) => the GAME refreshes the label pos slowly; interpolate the chip toward target.");
    return 0;
}

// Show the STRUCTURE behind a loot tag: find the visible text element whose first line == <name>, dump
// its ancestor chain (what the tag falls under), and scan the label + each ancestor for a pointer to an
// on-ground ITEM entity (does the UI reference the item? = the proper link we'd use for unID uniques).
static int RunLootStruct(ProcessHandle process, MemoryReader reader, string name)
{
    var (_, igs, ai, _) = ResolveChain(process, reader);
    if (ai == 0 || igs == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    var uiRoot = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    if (uiRoot == 0) { Console.Error.WriteLine("no UiRoot."); return 1; }
    name = name.Trim();
    Console.WriteLine($"Looking for the loot tag whose first line == \"{name}\"…");

    // On-ground item entities (container + inner) → label, for the link test.
    var itemPtrs = new Dictionary<long, string>();
    foreach (var (id, ent, meta) in WalkEntities(reader, ai))
    {
        if (!meta.Contains("WorldItem", StringComparison.Ordinal)) continue;
        var wi = ResolveComponentAddr(reader, ent, "WorldItem");
        var inner = wi == 0 ? 0 : SafePtr(reader, wi + Poe2.WorldItemComponent.ItemEntity);
        var art = "?";
        if (inner != 0) { var ri = ResolveComponentAddr(reader, inner, "RenderItem"); if (ri != 0) art = ItemArtBasename(reader.ReadStringUtf16(SafePtr(reader, ri + Poe2.RenderItemComponent.ResourcePath), 128)) ?? "?"; }
        itemPtrs[(long)ent] = art + "(container)"; if (inner != 0) itemPtrs[(long)inner] = art + "(item)";
        // include WorldItem component + a couple of its inner components, since the link might be on a component
        if (wi != 0) itemPtrs[(long)wi] = art + "(WorldItem-comp)";
    }
    Console.WriteLine($"on-ground item/related pointers tracked: {itemPtrs.Count}");

    // BFS the tree for the named tag.
    var q = new Queue<(nint el, int d)>(); q.Enqueue((uiRoot, 0));
    var vis = new HashSet<nint>(); nint found = 0;
    while (q.Count > 0 && vis.Count < 120000 && found == 0)
    {
        var (el, d) = q.Dequeue();
        if (el == 0 || !vis.Add(el)) continue;
        var t = ReadStdWString(reader, el + Poe2.UiElement.Text);
        if (t.Length >= 2)
        {
            var nl = t.IndexOf('\n');
            if (string.Equals((nl >= 0 ? t[..nl] : t).Trim(), name, StringComparison.OrdinalIgnoreCase)) { found = el; break; }
        }
        var f = SafePtr(reader, el + Poe2.UiElement.Children);
        if (f != 0 && reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var l))
        { var n = ((long)l - (long)f) / 8; if (n is > 0 and <= 4096) for (long i = 0; i < n; i++) q.Enqueue((SafePtr(reader, f + (nint)(i * 8)), d + 1)); }
    }
    if (found == 0) { Console.WriteLine("tag not found (focus the game; the item must be on the ground with its label rendered)."); return 0; }
    Console.WriteLine($"FOUND tag element 0x{found:X}");

    // Ancestor chain — what the tag falls under, up to UiRoot.
    Console.WriteLine("\n=== ancestor chain (tag → container → UiRoot) ===");
    var chain = new List<nint>();
    var cur = found;
    for (var lvl = 0; lvl < 20 && cur != 0; lvl++)
    {
        chain.Add(cur);
        long cn = 0; var cf = SafePtr(reader, cur + Poe2.UiElement.Children);
        if (cf != 0 && reader.TryReadStruct<nint>(cur + Poe2.UiElement.ChildrenEnd, out var cl)) cn = ((long)cl - (long)cf) / 8;
        reader.TryReadStruct<float>(cur + Poe2.UiElement.SizeW, out var sw);
        reader.TryReadStruct<float>(cur + Poe2.UiElement.SizeH, out var sh);
        var tt = ReadStdWString(reader, cur + Poe2.UiElement.Text); tt = tt.Length > 0 ? $" text=\"{tt.Split('\n')[0]}\"" : "";
        Console.WriteLine($"  lvl{lvl,2}: 0x{cur:X} children={cn,-4} size=({sw:0}x{sh:0}){tt}{(cur == uiRoot ? "  <= UiRoot" : "")}");
        if (cur == uiRoot) break;
        cur = SafePtr(reader, cur + Poe2.UiElement.Parent);
    }

    // LINK TEST: scan the tag + each ancestor's struct for a pointer to an on-ground item entity.
    Console.WriteLine("\n=== link test: does the tag/panel/container reference an on-ground item entity? ===");
    var any = false;
    foreach (var el in chain)
        for (var off = 0; off <= 0x800; off += 8)
        {
            var v = SafePtr(reader, el + off);
            if (itemPtrs.TryGetValue((long)v, out var who)) { Console.WriteLine($"   0x{el:X} +0x{off:X3} -> 0x{v:X}  {who}"); any = true; }
        }
    if (!any) Console.WriteLine("   (no direct item-entity pointer in the tag's UI subtree — the link lives elsewhere, e.g. the ItemsOnGroundLabelElement's parallel data vector)");
    return 0;
}

// Walk the AwakeEntities std::map, yielding (id, entityPtr, metadata) for real entities.
static IEnumerable<(uint id, nint ent, string meta)> WalkEntities(MemoryReader reader, nint ai)
{
    var head = SafePtr(reader, ai + Poe2.AreaInstance.AwakeEntities);
    if (head == 0) yield break;
    var queue = new Queue<nint>(); queue.Enqueue(SafePtr(reader, head + Poe2.StdMapNode.Parent));
    var visited = new HashSet<nint>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var node = queue.Dequeue();
        if (node == 0 || node == head || !visited.Add(node)) continue;
        if (!reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
        reader.TryReadStruct<uint>(node + Poe2.StdMapNode.KeyId, out var id);
        var ent = SafePtr(reader, node + Poe2.StdMapNode.ValueEntityPtr);
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Left));
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Right));
        if (ent == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;
        yield return (id, ent, ReadEntityMetadata(reader, ent));
    }
}

// ── Changed-page detector: find WHERE memory changes across a state change ──────────────────
// --pagesnap hashes every committed 4 KiB page (mapped+private) and writes {addr,hash} to a temp
// file. --pagediff re-hashes and reports which pages changed. Procedure to pin the live atlas state:
//   1) --pagesnap --tag base        (in the atlas, idle)
//   2) --pagediff --tag base        (still idle — this is the CONTROL: pages that churn on their own)
//   3) <do the action: complete/enter a map, or move/select a node>
//   4) --pagediff --tag base        (the ACTION diff)
// Pages in the action diff but NOT the control are the state change → drill with --dump / --find-range.
static ulong HashPage(ReadOnlySpan<byte> p)
{
    // Mix qwords (8-byte stride) rather than bytes — ~8x faster, still detects virtually any change.
    var q = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(p);
    ulong h = 1469598103934665603UL;
    for (var i = 0; i < q.Length; i++) { h ^= q[i]; h *= 1099511628211UL; }
    return h;
}

static string PageSnapPath(string tag) => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"poe2_pages_{tag}.bin");

static int RunPageSnap(MemoryReader reader, string tag, nint lo, nint hi)
{
    const int Page = 0x1000;
    var regions = reader.Process.EnumerateReadableRegions(privateOnly: false)
        .Where(r => r.Address >= lo && r.Address < hi).ToArray();
    var chunk = new byte[1 << 20];
    var path = PageSnapPath(tag);
    using var fs = System.IO.File.Create(path);
    using var w = new System.IO.BinaryWriter(fs);
    long pages = 0;
    foreach (var (regionBase, regionSize) in regions)
    {
        long off = 0;
        while (off < regionSize)
        {
            var toRead = (int)Math.Min(chunk.Length, regionSize - off);
            toRead -= toRead % Page;
            if (toRead == 0) break;
            var read = reader.TryReadBytes(regionBase + (nint)off, chunk.AsSpan(0, toRead));
            if (read >= Page)
                for (var p = 0; p + Page <= read; p += Page)
                { w.Write((long)(regionBase + (nint)(off + p))); w.Write(HashPage(chunk.AsSpan(p, Page))); pages++; }
            if (read != toRead) break;
            off += toRead;
        }
    }
    Console.WriteLine($"pagesnap '{tag}': {pages} pages hashed in [0x{lo:X}, 0x{hi:X}) -> {path}");
    return 0;
}

static int RunPageDiff(MemoryReader reader, string tag, nint lo, nint hi, string? save, string? exclude, string? only)
{
    const int Page = 0x1000;
    string ChangedPath(string n) => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"poe2_changed_{n}.bin");
    HashSet<long> LoadSet(string n) { var p = ChangedPath(n); var s = new HashSet<long>(); if (System.IO.File.Exists(p)) foreach (var ln in System.IO.File.ReadAllLines(p)) if (long.TryParse(ln, System.Globalization.NumberStyles.HexNumber, null, out var v)) s.Add(v); return s; }

    var path = PageSnapPath(tag);
    if (!System.IO.File.Exists(path)) { Console.Error.WriteLine($"No snapshot '{tag}' — run --pagesnap --tag {tag} first."); return 1; }
    var prev = new Dictionary<long, ulong>(1 << 20);
    using (var fs = System.IO.File.OpenRead(path))
    using (var r = new System.IO.BinaryReader(fs))
        while (fs.Position + 16 <= fs.Length) { var a = r.ReadInt64(); var h = r.ReadUInt64(); prev[a] = h; }

    var regions = reader.Process.EnumerateReadableRegions(privateOnly: false)
        .Where(r => r.Address >= lo && r.Address < hi).ToArray();
    var chunk = new byte[1 << 20];
    var changed = new List<long>();
    long checkd = 0;
    foreach (var (regionBase, regionSize) in regions)
    {
        long off = 0;
        while (off < regionSize)
        {
            var toRead = (int)Math.Min(chunk.Length, regionSize - off);
            toRead -= toRead % Page;
            if (toRead == 0) break;
            var read = reader.TryReadBytes(regionBase + (nint)off, chunk.AsSpan(0, toRead));
            if (read >= Page)
                for (var p = 0; p + Page <= read; p += Page)
                {
                    var addr = (long)(regionBase + (nint)(off + p));
                    if (!prev.TryGetValue(addr, out var oldH)) continue; // only compare pages present in baseline
                    checkd++;
                    if (HashPage(chunk.AsSpan(p, Page)) != oldH) changed.Add(addr);
                }
            if (read != toRead) break;
            off += toRead;
        }
    }
    var rawCount = changed.Count;
    // Save the full changed set (pre-filter) for later --exclude/--only set algebra.
    if (save != null) System.IO.File.WriteAllLines(ChangedPath(save), changed.Select(a => a.ToString("X")));
    // Filter: drop pages that changed in an --exclude set (noise control); keep only pages also in --only.
    if (exclude != null) { var ex = exclude.Split(',').SelectMany(LoadSet).ToHashSet(); changed = changed.Where(a => !ex.Contains(a)).ToList(); }
    if (only != null) { var keep = LoadSet(only); changed = changed.Where(a => keep.Contains(a)).ToList(); }

    Console.WriteLine($"pagediff '{tag}': {rawCount} of {checkd} baseline pages changed" +
        $"{(exclude != null || only != null ? $"  ->  {changed.Count} after filter (exclude={exclude} only={only})" : "")}" +
        $"{(save != null ? $"  [saved set '{save}']" : "")}.");
    // Group contiguous changed pages into runs for readability.
    changed.Sort();
    long runStart = -1, prevA = -1;
    void Flush(long endA) { if (runStart >= 0) Console.WriteLine($"  0x{runStart:X12} .. 0x{endA + Page - 1:X12}  ({(endA - runStart) / Page + 1} pages)"); }
    foreach (var a in changed)
    {
        if (runStart < 0) { runStart = a; prevA = a; continue; }
        if (a == prevA + Page) { prevA = a; continue; }
        Flush(prevA); runStart = a; prevA = a;
    }
    Flush(prevA);
    if (changed.Count > 0)
        Console.WriteLine("Re-snapshot to update the baseline (--pagesnap), or --dump a changed page to inspect.");
    return 0;
}

// ── Atlas catalog walker: enumerate the map-type catalog ────────────────────
// The catalog is an array of 0x18-byte entries {int32 id (8 bytes incl. pad); IntPtr parsedObject
// (stride 0x300); IntPtr idString -> UTF-16 "MapXxx"}. Seeded from a known entry (found via
// --find-range on a map's dat row), it snaps to entry alignment, walks backward then forward while
// entries validate (idString points to "Map..." text), and prints the count + each map's code/name.
// This is the set of map TYPES the client has parsed — the "what data is present" inventory. (Per-
// node Atlas STATE — tier/content/completion/position — is a separate model, still TBD.)
// Load the map-type catalog: array of 0x18-byte {int id; IntPtr parsedObj; IntPtr idStr->"MapXxx"}.
static List<(nint e, int id, nint obj, string code)> LoadCatalog(MemoryReader reader, nint seed)
{
    bool Valid(nint e, out string code, out nint obj, out int id)
    {
        code = ""; obj = 0; id = 0;
        if (!reader.TryReadStruct<int>(e, out id)) return false;
        obj = SafePtr(reader, e + 0x08);
        var idStr = SafePtr(reader, e + 0x10);
        if (obj == 0 || idStr == 0) return false;
        code = reader.ReadStringUtf16(idStr, 64);
        return code.StartsWith("Map", StringComparison.Ordinal) && code.All(c => c is >= ' ' and < (char)0x7f);
    }

    var result = new List<(nint, int, nint, string)>();
    nint baseEntry = 0;
    for (var d = -3; d <= 3 && baseEntry == 0; d++)
        if (Valid(seed + (nint)(d * 0x18), out _, out _, out _)) baseEntry = seed + (nint)(d * 0x18);
    if (baseEntry == 0) return result;
    var start = baseEntry;
    while (Valid(start - 0x18, out _, out _, out _)) start -= 0x18;
    for (var e = start; result.Count < 20000; e += 0x18)
    {
        if (!Valid(e, out var code, out var obj, out var id)) break;
        result.Add((e, id, obj, code));
    }
    return result;
}

static int RunAtlasCatalog(MemoryReader reader, nint seed)
{
    var entries = LoadCatalog(reader, seed);
    if (entries.Count == 0) { Console.Error.WriteLine($"No valid catalog near seed 0x{seed:X}. Re-find via --find-range on a map dat row."); return 1; }
    Console.WriteLine($"Atlas/map catalog @ 0x{entries[0].e:X16}  —  {entries.Count} map-type entries (stride 0x18).\n");
    Console.WriteLine($"{"idx",-4} {"id",-5} {"code",-28} parsedObj");
    foreach (var (i, en) in entries.Select((x, i) => (i, x)))
        Console.WriteLine($"{i,-4} {en.id,-5} {en.code,-28} 0x{en.obj:X}");
    return 0;
}

// ── Atlas node-list walker: count placed nodes + histogram by archetype ─────────────────────
// The node array (@ ~0x40180282200) is 0x18-byte entries {IntPtr recordPtr (the per-node static
// record, stride ~0xEF); IntPtr archetype (a catalog parsedObj); IntPtr sharedConst}. We validate by
// the shared constant (read from the seed entry), walk back/forward, map each archetype ptr back to
// its catalog code, and report total node count + a per-archetype histogram (⇒ "how many Citadels /
// uniques / each map"). For the first few, we also dump the record so position/edge fields can be
// spotted next.
static int RunAtlasNodes(MemoryReader reader, nint seed, nint catalogSeed)
{
    var catalog = LoadCatalog(reader, catalogSeed);
    var byObj = catalog.ToDictionary(c => c.obj, c => c.code);
    Console.WriteLine($"catalog: {catalog.Count} map types loaded ({byObj.Count} distinct parsedObj).");

    // The shared constant identifies array entries; read it from the seed (+0x10).
    var shared = SafePtr(reader, seed + 0x10);
    bool Valid(nint e, out nint rec, out nint obj)
    {
        rec = SafePtr(reader, e); obj = SafePtr(reader, e + 0x08);
        var sh = SafePtr(reader, e + 0x10);
        return rec != 0 && obj != 0 && sh == shared && byObj.ContainsKey(obj);
    }
    if (shared == 0 || !Valid(seed, out _, out _))
    {
        // Snap to a nearby valid entry.
        nint snapped = 0;
        for (var d = -4; d <= 4 && snapped == 0; d++) { var e = seed + (nint)(d * 0x18); if (SafePtr(reader, e + 0x10) is var s && s != 0) { shared = s; if (Valid(e, out _, out _)) snapped = e; } }
        if (snapped == 0) { Console.Error.WriteLine($"No valid node-array entry near seed 0x{seed:X} (shared=0x{shared:X})."); return 1; }
        seed = snapped;
    }
    Console.WriteLine($"node array seed 0x{seed:X}  sharedConst 0x{shared:X}");

    var start = seed;
    while (Valid(start - 0x18, out _, out _)) start -= 0x18;
    var nodes = new List<(nint e, nint rec, nint obj, string code)>();
    for (var e = start; nodes.Count < 100000; e += 0x18)
    {
        if (!Valid(e, out var rec, out var obj)) break;
        nodes.Add((e, rec, obj, byObj.GetValueOrDefault(obj, $"?0x{obj:X}")));
    }

    Console.WriteLine($"\nNODE ARRAY @ 0x{start:X16}  —  {nodes.Count} entries (stride 0x18).");
    var hist = nodes.GroupBy(n => n.code).OrderByDescending(g => g.Count()).ToList();
    Console.WriteLine($"{hist.Count} distinct archetypes among the nodes. Histogram:");
    foreach (var g in hist) Console.WriteLine($"  {g.Count(),4}  {g.Key}");

    const int RecScan = 0xE8; // stay within the ~0xEF record stride (don't bleed into the next record)
    var recd = nodes.Select(n => { var b = new byte[RecScan]; var got = reader.TryReadBytes(n.rec, b); return got == RecScan ? (n.code, b) : (n.code, (byte[]?)null); })
                    .Where(x => x.Item2 != null).Select(x => (x.code, bytes: x.Item2!)).ToList();

    // Per-offset float variance — coordinates VARY per node (vs. constant sizes like 40.0).
    Console.WriteLine($"\n--- per-offset float variance across {recd.Count} node records (candidate position fields) ---");
    var cands = new List<(int off, float min, float max, int distinct)>();
    for (var o = 0; o + 4 <= RecScan; o += 2)
    {
        float mn = float.MaxValue, mx = float.MinValue; var ok = true; var vals = new HashSet<int>();
        foreach (var (_, b) in recd)
        {
            var f = BitConverter.ToSingle(b, o);
            if (!float.IsFinite(f) || MathF.Abs(f) > 1e6f) { ok = false; break; }
            mn = MathF.Min(mn, f); mx = MathF.Max(mx, f); vals.Add(BitConverter.ToInt32(b, o));
        }
        if (ok && vals.Count >= recd.Count / 2 && (mx - mn) > 1f) cands.Add((o, mn, mx, vals.Count));
    }
    foreach (var c in cands.OrderByDescending(c => c.distinct).Take(16))
        Console.WriteLine($"  +0x{c.off:X3}  range [{c.min:F1} .. {c.max:F1}]  distinct={c.distinct}/{recd.Count}");

    // Per-offset DEVIATION hunt: find offsets where only a FEW nodes differ from the modal byte. A
    // field that's unique to the node(s) the player just changed (e.g. completed Steppe) shows up here.
    Console.WriteLine("\n--- rare per-byte deviations (offsets where 1–6 nodes differ from the norm) — completion/state candidates ---");
    for (var o = 0; o < RecScan; o++)
    {
        var counts = new Dictionary<byte, int>();
        foreach (var (_, b) in recd) counts[b[o]] = counts.GetValueOrDefault(b[o]) + 1;
        if (counts.Count < 2) continue;
        var mode = counts.OrderByDescending(k => k.Value).First().Key;
        var deviants = recd.Where(r => r.bytes[o] != mode).ToList();
        if (deviants.Count is >= 1 and <= 6)
            Console.WriteLine($"  +0x{o:X3} (mode 0x{mode:X2}): " +
                string.Join(", ", deviants.Select(d => $"{d.code}=0x{d.bytes[o]:X2}")));
    }

    Console.WriteLine("\n--- first 3 node records (raw) ---");
    foreach (var n in nodes.Take(3))
    {
        Console.WriteLine($"\n  node @0x{n.e:X} record 0x{n.rec:X}  archetype={n.code}");
        DumpWindow(reader, n.rec, 0x80, "    ");
    }
    return 0;
}

// ── Atlas fields: dump a map's parsed object with each field annotated, to find tier/biome/boss ──
// Uses the live Core reader (dynamic locator) to get the catalog, then for every map whose code
// contains <code> dumps its 0x300 parsed object: per 4-byte offset shows the int + float; per 8-byte
// offset, if it's a canonical pointer, peeks the target as a UTF-16 string. Compare a boss map
// (e.g. Marrow) vs a plain one, and a high- vs low-tier, to pin the flag/tier/biome offsets.
static int RunAtlasFields(ProcessHandle process, MemoryReader reader, string code)
{
    var (_, _, ai, _) = ResolveChain(process, reader);
    if (ai == 0) { Console.Error.WriteLine("no chain (in game?)."); return 1; }
    var atlas = new POE2Radar.Core.Game.Poe2Atlas(reader);
    var data = atlas.Read(ai);
    for (var w = 0; !data.Located && data.Note.Contains("Scanning") && w < 180; w++) { Thread.Sleep(1000); data = atlas.Read(ai); }
    if (!data.Located) { Console.Error.WriteLine($"atlas not located: {data.Note}"); return 1; }

    var matches = data.Catalog.Where(m => m.Code.Contains(code, StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
    if (matches.Count == 0) { Console.WriteLine($"no catalog code contains '{code}'. Sample: " + string.Join(", ", data.Catalog.Take(10).Select(m => m.Code))); return 0; }

    // The dat row is a packed (2-byte-misaligned) record; the Id string "MapXxx" lives INSIDE it. Dump
    // a window starting a bit before the Id and resolve each 8-byte-misaligned pointer (these rows store
    // pointers at addr%8==6) to a string, and show small ints/floats — biome/description/boss-mod refs
    // should appear as string pointers, tier/level as small ints.
    foreach (var m in matches)
    {
        var idStr = (nint)m.IdStr;
        Console.WriteLine($"\n===== {m.Code}  (id={m.Id})  idStr=0x{idStr:X}  parsedObj=0x{m.ParsedObj:X} =====");
        var rowStart = idStr - 0x60;
        var buf = new byte[0x140];
        var n = reader.TryReadBytes(rowStart, buf);
        for (var o = 0; o + 8 <= n; o += 2) // 2-byte step: rows are misaligned
        {
            var p = (nint)BitConverter.ToInt64(buf, o);
            if ((ulong)p < 0x10000 || (ulong)p > 0x7FFFFFFFFFFF) continue;
            var s = reader.ReadStringUtf16(p, 48);
            var disp = Printable(s) ? $"\"{s}\"" : "";
            if (disp.Length == 0) { var u = reader.ReadStringUtf8(p, 48); if (Printable(u)) disp = $"utf8 \"{u}\""; }
            if (disp.Length > 0) Console.WriteLine($"  row+0x{o - 0x60:+0;-0;0} (0x{rowStart + o:X}) -> {disp}");
        }
        // Also dump the raw row bytes so int columns (tier/flags) are visible.
        Console.WriteLine("  raw row bytes (Id at +0x00):");
        var rb = new byte[0x80];
        if (reader.TryReadBytes(idStr - 0x40, rb) == rb.Length)
            for (var i = 0; i < rb.Length; i += 16)
                Console.WriteLine($"    {i - 0x40,4}  {string.Join(' ', Enumerable.Range(0, 16).Select(j => rb[i + j].ToString("X2")))}");
    }
    Console.WriteLine("\nCompare a boss map vs a plain one (and high- vs low-tier) to spot the flag/tier/biome offsets.");
    return 0;
}

// ── Atlas nodes v2: validate the community-sourced Atlas-node ELEMENT layout (2026-06-07 notes) ──
// Atlas nodes ARE UiElements. Walk the UI tree (Self@+0x08, children begin/end @ +0x10/+0x18), group
// by vtable (+0x00); the atlas-node class shows up as a vtable with many instances whose biome byte
// (+0x32E) is in 0..12. Dump that class's fields to confirm the offsets against the live atlas:
//   +0x300 u32 map-node id | +0x310 content | +0x32E u8 biome(0..12) | +0x32F flags(bit0 unlocked,
//   bit1 visited) | +0x339 completion id | Element +0x118 pos(x,y floats) | +0x288/+0x28C size W/H.
static int RunAtlasNodes2(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain (in game?)."); return 1; }
    var uiRoot = SafePtr(reader, igs + 0x2F0);
    var trueRoot = SafePtr(reader, uiRoot + 0xB8);            // notes: true UI root = *(UiRoot+0xB8)
    var root = trueRoot != 0 ? trueRoot : uiRoot;
    Console.WriteLine($"InGameState 0x{igs:X}  UiRoot 0x{uiRoot:X}  trueRoot 0x{root:X}");

    // BFS the element tree.
    var queue = new Queue<nint>(); queue.Enqueue(root);
    var visited = new HashSet<nint>();
    var elements = new List<nint>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el)) continue;
        if (SafePtr(reader, el + 0x08) != el) continue;       // Element self-ref validation
        elements.Add(el);
        var first = SafePtr(reader, el + 0x10);
        if (first != 0 && reader.TryReadStruct<nint>(el + 0x18, out var last))
        {
            var n = ((long)last - (long)first) / 8;
            if (n is > 0 and <= 16384)
                for (long k = 0; k < n; k++) queue.Enqueue(SafePtr(reader, first + (nint)(k * 8)));
        }
    }
    Console.WriteLine($"{elements.Count} elements.");

    // Group by vtable; for each populous vtable, count instances whose +0x32E (biome) is in 0..12.
    var byVtable = new Dictionary<nint, List<nint>>();
    foreach (var el in elements)
    {
        var vt = SafePtr(reader, el);
        if (vt == 0) continue;
        (byVtable.TryGetValue(vt, out var l) ? l : byVtable[vt] = new()).Add(el);
    }
    // Rank by DISTINCT NONZERO biome values (1..12) seen across a vtable's instances AND distinct
    // +0x300 ids — the atlas-node subclass spreads biomes across the map types; generic elements are
    // all biome 0 / id constant.
    Console.WriteLine("\nvtables with >=20 instances (distinctBiomes 1..12 / distinctIds among first 400):");
    var ranked = new List<(nint vt, int count, int distinctBiomes, int distinctIds)>();
    foreach (var (vt, list) in byVtable)
    {
        if (list.Count < 20) continue;
        var biomes = new HashSet<int>(); var ids = new HashSet<uint>();
        foreach (var el in list.Take(400))
        {
            if (reader.TryReadStruct<byte>(el + 0x32E, out var b) && b is >= 1 and <= 12) biomes.Add(b);
            if (reader.TryReadStruct<uint>(el + 0x300, out var id)) ids.Add(id);
        }
        ranked.Add((vt, list.Count, biomes.Count, ids.Count));
    }
    foreach (var r in ranked.OrderByDescending(r => r.distinctIds).Take(12))
        Console.WriteLine($"  vtable 0x{r.vt:X}  instances={r.count}  distinctBiomes={r.distinctBiomes}  distinctIds={r.distinctIds}  uniqRatio={(double)r.distinctIds / r.count:F2}");

    // Dump classes that have BOTH biome variation (>=3) AND unique ids — the real map-node signature.
    // (Decorations have biomes but reuse ids; lists/toolbars have unique ids but no biome variation.)
    foreach (var r in ranked.Where(r => r.distinctBiomes >= 3).OrderByDescending(r => (double)r.distinctIds / r.count).Take(4))
    {
        var nodes = byVtable[r.vt];
        Console.WriteLine($"\n=== vtable 0x{r.vt:X} ({r.count} instances, {r.distinctIds} distinct ids, ratio {(double)r.distinctIds / r.count:F2}) ===");
        foreach (var el in nodes.Take(12))
        {
            reader.TryReadStruct<uint>(el + 0x300, out var id);
            reader.TryReadStruct<uint>(el + 0x310, out var content);
            reader.TryReadStruct<byte>(el + 0x32C, out var state);
            reader.TryReadStruct<byte>(el + 0x32E, out var biome);
            reader.TryReadStruct<byte>(el + 0x32F, out var flags);
            reader.TryReadStruct<uint>(el + 0x339, out var compl);
            reader.TryReadStruct<float>(el + 0x118, out var px);
            reader.TryReadStruct<float>(el + 0x11C, out var py);
            reader.TryReadStruct<float>(el + 0x288, out var sw);
            reader.TryReadStruct<float>(el + 0x28C, out var sh);
            reader.TryReadStruct<uint>(el + 0x180, out var fl);
            Console.WriteLine($"  0x{el:X}  id={id,-10} content={content,-10} state={state} biome={biome,-2} " +
                $"flags=0x{flags:X2} compl={compl,-4} pos=({px:F0},{py:F0}) size=({sw:F0}x{sh:F0}) shown={((fl >> 0x0B) & 1)}");
        }
    }
    Console.WriteLine("\nThe REAL map-node class: unique id per instance (ratio≈1), sensible size, flags vary with progress.");
    return 0;
}

// ── Atlas correspondence collector + homography solver ──────────────────────────────────────
// Each call: capture the cursor, find the visible node nearest the cursor (via the CURRENT projection
// from /api/settings), and record (relPos → cursor). After ≥4 spread nodes, solve the canvas→screen
// HOMOGRAPHY (least-squares DLT) and POST h0..h7 to the overlay. --reset clears; --solve forces a solve.
static int RunAtlasCorr(ProcessHandle process, MemoryReader reader, bool solve, bool reset)
{
    var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "poe2_atlas_corr.txt");
    if (reset) { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); Console.WriteLine("correspondences reset."); return 0; }
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain."); return 1; }

    // FIXED rough reference projection for nearest-node identification (NOT the live/updating one —
    // using the live homography created a feedback loop where one bad pick skewed all subsequent picks).
    double[] h = { 0.631, 0, -94, 0, 0.539, 53, 0, 0 };

    Win.GetCursorPos(out var cur); double cx = cur.X, cy = cur.Y;
    var atlas = new POE2Radar.Core.Game.Poe2Atlas(reader);
    var nodes = atlas.ReadNodes(igs); for (var w = 0; nodes.Count == 0 && w < 30; w++) { Thread.Sleep(100); nodes = atlas.ReadNodes(igs); }
    var vis = nodes.Where(n => n.Visible).ToList();
    nint bestEl = 0; double bestD = 1e18, brx = 0, bry = 0;
    foreach (var n in vis)
    {
        if (n.IconType != 0) continue; // only the actual clickable TILES (type>0 = content tags offset above)
        double x = n.X, y = n.Y, w = h[6] * x + h[7] * y + 1; if (Math.Abs(w) < 1e-6) continue;
        double sx = (h[0] * x + h[1] * y + h[2]) / w, sy = (h[3] * x + h[4] * y + h[5]) / w;
        double d = (sx - cx) * (sx - cx) + (sy - cy) * (sy - cy);
        if (d < bestD) { bestD = d; bestEl = (nint)n.Element; brx = x; bry = y; }
    }
    if (bestEl == 0) { Console.Error.WriteLine("no visible nodes."); return 1; }
    System.IO.File.AppendAllText(path, $"{brx} {bry} {cx} {cy}\n");
    Console.WriteLine($"cursor=({cx},{cy})  nearest node 0x{bestEl:X} relPos=({brx:F0},{bry:F0})  projDist={Math.Sqrt(bestD):F0}px");

    var lines = System.IO.File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
    Console.WriteLine($"{lines.Count} correspondence(s) collected. (run with --solve to fit + apply; needs 4+, 8+ recommended)");
    if (!solve) return 0;
    if (lines.Count < 4) { Console.Error.WriteLine("need 4+ points to solve."); return 1; }

    // Build + solve the DLT homography with Hartley normalization (centroid→origin, mean dist→√2 on
    // both source + dest, then denormalize). WITHOUT this the unnormalized normal equations are ill-
    // conditioned (~10¹³ spread) and the perspective terms come out as roundoff noise — the core bug.
    static (double scale, double cx, double cy) NormP(List<double[]> ps, int col)
    {
        double cx = 0, cy = 0; foreach (var p in ps) { cx += p[col]; cy += p[col + 1]; }
        cx /= ps.Count; cy /= ps.Count;
        double md = 0; foreach (var p in ps) md += Math.Sqrt((p[col] - cx) * (p[col] - cx) + (p[col + 1] - cy) * (p[col + 1] - cy));
        md /= ps.Count; return md < 1e-9 ? (0, cx, cy) : (Math.Sqrt(2) / md, cx, cy);
    }
    static double[] Mul3(double[] a, double[] b2)
    { var m = new double[9]; for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) { double s = 0; for (var k = 0; k < 3; k++) s += a[r * 3 + k] * b2[k * 3 + c]; m[r * 3 + c] = s; } return m; }
    static double[]? Fit(List<double[]> ps)
    {
        if (ps.Count < 4) return null;
        var (sS, cxS, cyS) = NormP(ps, 0); var (sD, cxD, cyD) = NormP(ps, 2);
        if (sS == 0 || sD == 0) return null;
        int n2 = ps.Count * 2; var A = new double[n2, 8]; var b = new double[n2];
        for (var i = 0; i < ps.Count; i++)
        {
            double x = sS * (ps[i][0] - cxS), y = sS * (ps[i][1] - cyS), u = sD * (ps[i][2] - cxD), v = sD * (ps[i][3] - cyD); int r0 = i * 2;
            A[r0, 0] = x; A[r0, 1] = y; A[r0, 2] = 1; A[r0, 6] = -u * x; A[r0, 7] = -u * y; b[r0] = u;
            A[r0 + 1, 3] = x; A[r0 + 1, 4] = y; A[r0 + 1, 5] = 1; A[r0 + 1, 6] = -v * x; A[r0 + 1, 7] = -v * y; b[r0 + 1] = v;
        }
        var N = new double[8, 8]; var rhs = new double[8];
        for (var r = 0; r < 8; r++) { for (var c = 0; c < 8; c++) { double s = 0; for (var k = 0; k < n2; k++) s += A[k, r] * A[k, c]; N[r, c] = s; } double sb = 0; for (var k = 0; k < n2; k++) sb += A[k, r] * b[k]; rhs[r] = sb; }
        var hn = SolveLinear(N, rhs, 8); if (hn == null) return null;
        var Hn = new[] { hn[0], hn[1], hn[2], hn[3], hn[4], hn[5], hn[6], hn[7], 1.0 };
        var Tsrc = new[] { sS, 0, -sS * cxS, 0, sS, -sS * cyS, 0, 0, 1.0 };
        var TdstInv = new[] { 1 / sD, 0, cxD, 0, 1 / sD, cyD, 0, 0, 1.0 };
        var H = Mul3(TdstInv, Mul3(Hn, Tsrc));
        if (Math.Abs(H[8]) < 1e-12) return null;
        for (var i = 0; i < 9; i++) H[i] /= H[8];
        return new[] { H[0], H[1], H[2], H[3], H[4], H[5], H[6], H[7] };
    }
    static double Resid(double[] s, double[] p)
    { double x = p[0], y = p[1], w = s[6] * x + s[7] * y + 1; double su = (s[0] * x + s[1] * y + s[2]) / w, sv = (s[3] * x + s[4] * y + s[5]) / w; return Math.Sqrt((su - p[2]) * (su - p[2]) + (sv - p[3]) * (sv - p[3])); }

    var pts = lines.Select(l => l.Split(' ').Select(double.Parse).ToArray()).ToList();
    var sol = Fit(pts);
    if (sol == null) { Console.Error.WriteLine("singular system — pick well-spread nodes."); return 1; }
    // Outlier rejection: while >4 points and the worst residual is a clear outlier (>25px AND >3× median), drop it + refit.
    while (pts.Count > 4)
    {
        var res = pts.Select(p => Resid(sol!, p)).ToList();
        var worst = res.IndexOf(res.Max());
        var sorted = res.OrderBy(x => x).ToList(); var med = sorted[sorted.Count / 2];
        if (res[worst] > 25 && res[worst] > 3 * Math.Max(med, 1)) { Console.WriteLine($"  dropped outlier point {worst} (residual {res[worst]:F0}px, median {med:F0})"); pts.RemoveAt(worst); sol = Fit(pts); if (sol == null) break; }
        else break;
    }
    if (sol == null) { Console.Error.WriteLine("fit failed after outlier removal."); return 1; }
    Console.WriteLine($"\nsolved homography from {pts.Count} pts: h0={sol[0]:F5} h1={sol[1]:F5} h2={sol[2]:F2} h3={sol[3]:F5} h4={sol[4]:F5} h5={sol[5]:F2} h6={sol[6]:G4} h7={sol[7]:G4}");
    var maxErr = pts.Max(p => Resid(sol, p));
    var rmsErr = Math.Sqrt(pts.Average(p => { var r = Resid(sol, p); return r * r; }));
    Console.WriteLine($"max reprojection error = {maxErr:F1}px  rms = {rmsErr:F1}px (over {pts.Count} kept pts)");
    Console.WriteLine($"persp terms h6={sol[6]:E2} h7={sol[7]:E2} (≈0 ⇒ effectively affine / no tilt captured)");
    // Affine-only baseline (persp forced 0) — if the homography isn't clearly better, the picks are too
    // clustered/collinear to constrain perspective (or the mapping really is affine). Solve a·x+b·y+c.
    static double[]? FitAffine(List<double[]> ps)
    {
        var Na = new double[3, 3]; var ru = new double[3]; var rv = new double[3];
        foreach (var p in ps) { double[] row = { p[0], p[1], 1 }; for (var r = 0; r < 3; r++) { for (var c = 0; c < 3; c++) Na[r, c] += row[r] * row[c]; ru[r] += row[r] * p[2]; rv[r] += row[r] * p[3]; } }
        var au = SolveLinear(Na, ru, 3); var av = SolveLinear((double[,])Na.Clone(), rv, 3);
        return au == null || av == null ? null : new[] { au[0], au[1], au[2], av[0], av[1], av[2], 0.0, 0.0 };
    }
    var affBase = FitAffine(pts);
    if (affBase != null) Console.WriteLine($"affine-only baseline: max {pts.Max(p => Resid(affBase, p)):F1}px  →  homography {(maxErr < pts.Max(p => Resid(affBase, p)) * 0.7 ? "HELPS (perspective real)" : "no better (mapping ~affine / picks clustered)")}");
    // Push to the overlay.
    try
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { atlasScale = sol[0], atlasShearX = sol[1], atlasOffX = sol[2], atlasShearY = sol[3], atlasScaleY = sol[4], atlasOffY = sol[5], atlasPersX = sol[6], atlasPersY = sol[7] });
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var resp = http.PostAsync("http://localhost:7777/api/settings", new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")).Result;
        Console.WriteLine($"POST /api/settings -> {(int)resp.StatusCode}. Homography applied; verify in-game.");
    }
    catch (Exception e) { Console.WriteLine($"(POST failed: {e.Message})"); }
    return 0;
}

// Solve M·x = b (n×n) via Gaussian elimination with partial pivoting. Returns null if singular.
static double[]? SolveLinear(double[,] M, double[] b, int n)
{
    var a = new double[n, n + 1];
    for (var i = 0; i < n; i++) { for (var j = 0; j < n; j++) a[i, j] = M[i, j]; a[i, n] = b[i]; }
    for (var col = 0; col < n; col++)
    {
        var piv = col; for (var r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
        if (Math.Abs(a[piv, col]) < 1e-12) return null;
        if (piv != col) for (var j = 0; j <= n; j++) (a[col, j], a[piv, j]) = (a[piv, j], a[col, j]);
        for (var r = 0; r < n; r++) { if (r == col) continue; var f = a[r, col] / a[col, col]; for (var j = col; j <= n; j++) a[r, j] -= f * a[col, j]; }
    }
    var x = new double[n]; for (var i = 0; i < n; i++) x[i] = a[i, n] / a[i, i]; return x;
}

// ── Atlas find-pos: locate a cached ABSOLUTE screen-position field on the node elements ─────────
// You hover a node (cursor on it). We read the cursor, then scan EVERY visible node element's bytes
// for a float pair ≈ the cursor (in window pixels AND design-res 2560×1600). The hovered node will
// match at the element's absolute-rect offset — giving us exact positions with no projection math.
// Reports (offset → which node matched), so a consistent offset across runs is the abs-pos field.
static int RunAtlasFindPos(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain."); return 1; }
    int winW = Win.GetSystemMetrics(0), winH = Win.GetSystemMetrics(1); if (winW <= 0) { winW = 1920; winH = 1080; }
    Win.GetCursorPos(out var cur); // screen-absolute (independent of focus)
    var ui = winH / 1600f; // PoE UI scale (design height 1600)
    float cx = cur.X, cy = cur.Y, dx = cx / ui, dy = cy / ui; // window + design-space cursor
    Console.WriteLine($"cursor=({cx},{cy})  screen={winW}x{winH}  uiscale={ui:F3}  design-cursor=({dx:F0},{dy:F0})");

    var atlas = new POE2Radar.Core.Game.Poe2Atlas(reader);
    var nodes = atlas.ReadNodes(igs);
    for (var w = 0; nodes.Count == 0 && w < 30; w++) { Thread.Sleep(100); nodes = atlas.ReadNodes(igs); }
    var vis = nodes.Where(n => n.Visible).ToList();
    Console.WriteLine($"{vis.Count} visible nodes; scanning each element + its child chain [0..0x800] for a float pair ≈ cursor (±12px, window/design, also as top-left)...");

    const int Span = 0x800;
    var hits = new Dictionary<string, int>();
    var buf = new byte[Span];
    bool Near(float a, float b) => MathF.Abs(a - b) <= 12f;
    nint FirstChild(nint e) => SafePtr(reader, SafePtr(reader, e + 0x10));
    foreach (var n in vis)
    {
        // Scan the node element and up to 3 nested first-children (the sigil icon may hold the rect).
        var el = (nint)n.Element;
        for (var lvl = 0; lvl < 4 && el != 0; lvl++, el = FirstChild(el))
        {
            if (reader.TryReadBytes(el, buf) != Span) continue;
            for (var o = 0; o + 8 <= Span; o += 4)
            {
                var fx = BitConverter.ToSingle(buf, o); var fy = BitConverter.ToSingle(buf, o + 4);
                if (!float.IsFinite(fx) || !float.IsFinite(fy)) continue;
                string space = (Near(fx, cx) && Near(fy, cy)) ? "WIN" : (Near(fx, dx) && Near(fy, dy)) ? "DESIGN" : null!;
                if (space == null) continue;
                var key = $"lvl{lvl}+0x{o:X3}-{space}";
                hits[key] = hits.GetValueOrDefault(key) + 1;
                Console.WriteLine($"  node 0x{n.Element:X} {key}: ({fx:F1},{fy:F1})  relPos=({n.X:F0},{n.Y:F0})");
            }
        }
    }
    Console.WriteLine("\nmatch keys (an abs-pos field matches for ~1 node = the hovered one):");
    foreach (var kv in hits.OrderBy(k => k.Value)) Console.WriteLine($"  {kv.Key}: {kv.Value} node(s)");
    if (hits.Count == 0) Console.WriteLine("  (no cached abs-pos near the cursor — fall back to transform calibration / homography.)");
    return 0;
}

// ── Atlas any-hover: watch EVERY UI element's flags; the one that flips on hover is the node ─────
// Class-agnostic: snapshot +0x180 (UI flags) for all elements, poll, report any element whose flags
// change as the user hovers atlas map nodes. Whatever class the node is, hovering highlights it → its
// flags flip → we catch it (with its vtable + fields), pinning the real map-node class definitively.
static int RunAtlasAnyHover(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain."); return 1; }
    var uiRoot = SafePtr(reader, igs + 0x2F0);
    var root = SafePtr(reader, uiRoot + 0xB8) is var tr && tr != 0 ? tr : uiRoot;

    var queue = new Queue<nint>(); queue.Enqueue(root);
    var visited = new HashSet<nint>();
    var els = new List<nint>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el) || SafePtr(reader, el + 0x08) != el) continue;
        els.Add(el);
        var first = SafePtr(reader, el + 0x10);
        if (first != 0 && reader.TryReadStruct<nint>(el + 0x18, out var last))
        { var n = ((long)last - (long)first) / 8; if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) queue.Enqueue(SafePtr(reader, first + (nint)(k * 8))); }
    }
    Console.WriteLine($"watching {els.Count} elements' +0x180 flags. Hover atlas MAP NODES slowly. Ctrl+C to stop.\n");
    var arr = els.ToArray();
    var prev = new uint[arr.Length];
    for (var i = 0; i < arr.Length; i++) { reader.TryReadStruct<uint>(arr[i] + 0x180, out var f); prev[i] = f; }

    while (true)
    {
        for (var i = 0; i < arr.Length; i++)
        {
            if (!reader.TryReadStruct<uint>(arr[i] + 0x180, out var f) || f == prev[i]) continue;
            var el = arr[i];
            var vt = SafePtr(reader, el);
            reader.TryReadStruct<uint>(el + 0x300, out var id);
            reader.TryReadStruct<uint>(el + 0x310, out var content);
            reader.TryReadStruct<byte>(el + 0x32E, out var biome);
            reader.TryReadStruct<float>(el + 0x118, out var px); reader.TryReadStruct<float>(el + 0x11C, out var py);
            reader.TryReadStruct<float>(el + 0x288, out var sw); reader.TryReadStruct<float>(el + 0x28C, out var sh);
            Console.WriteLine($"[flip] 0x{el:X} vt=0x{vt:X} flags 0x{prev[i]:X8}->0x{f:X8} id={id} content={content} biome={biome} pos=({px:F0},{py:F0}) size=({sw:F0}x{sh:F0})");
            prev[i] = f;
        }
        Thread.Sleep(200);
    }
}

// ── Atlas map-NAME discovery: find the localized display name in the EndgameMaps row ───────────
// The overlay currently Prettify()s the internal "MapXxx" code (node+0x300 row, +0x00) into a display
// name, which mismatches what the player sees for some maps. This walks visible atlas nodes and, for a
// few DISTINCT maps, scans their EndgameMaps row (1-byte stride) for string columns — directly AND one
// pointer-level deep (the localized name may live in a referenced WorldAreas row) — so we can spot the
// real display-name column offset to read instead of Prettify. Run with the Atlas MAP open.
static int RunAtlasMapName(ProcessHandle process, MemoryReader reader, int maxDistinct)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain."); return 1; }
    var uiRoot = SafePtr(reader, igs + 0x2F0);
    var root = SafePtr(reader, uiRoot + 0xB8) is var tr && tr != 0 ? tr : uiRoot;

    var queue = new Queue<nint>(); queue.Enqueue(root);
    var visited = new HashSet<nint>();
    var seenCodes = new HashSet<string>(StringComparer.Ordinal);
    var shown = 0;
    Console.WriteLine("Walking UI tree for atlas nodes (Atlas MAP must be open)…\n");
    while (queue.Count > 0 && visited.Count < 200000 && shown < maxDistinct)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el) || SafePtr(reader, el + 0x08) != el) continue;
        var first = SafePtr(reader, el + 0x10);
        if (first != 0 && reader.TryReadStruct<nint>(el + 0x18, out var last))
        { var n = ((long)last - (long)first) / 8; if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) queue.Enqueue(SafePtr(reader, first + (nint)(k * 8))); }

        // Atlas node: el+0x300 → EndgameMaps row; row+0x00 → "MapXxx" code wstring (sometimes one ptr deeper).
        var row = SafePtr(reader, el + 0x300);
        if (row == 0) continue;
        var w = SafePtr(reader, row);
        var code = w != 0 ? reader.ReadStringUtf16(w, 64) : "";
        if (!code.StartsWith("Map", StringComparison.Ordinal))
        {
            var w2 = SafePtr(reader, w);
            code = w2 != 0 ? reader.ReadStringUtf16(w2, 64) : code;
        }
        if (!code.StartsWith("Map", StringComparison.Ordinal) || !seenCodes.Add(code)) continue;

        shown++;
        Console.WriteLine($"════════ code=\"{code}\"  Prettify=\"{Poe2Atlas.Prettify(code)}\"  row=0x{row:X}");
        ScanRowForNames(reader, row, "    ");
        Console.WriteLine();
    }
    Console.WriteLine($"Dumped {shown} distinct maps. Identify the column whose UTF-16 string = the in-game");
    Console.WriteLine("display name; note its +offset (direct, or 'row->ptr+off') to wire into Poe2Atlas.");
    return 0;
}

// Scan an EndgameMaps row (the code+0xEF stride keeps it to one row) for printable string columns at
// 1-byte stride. For each direct string-pointer column it prints the string; for each pointer-to-a-row
// column it follows ONE level and scans THAT row's first 0x40 bytes for its own string columns — so a
// referenced WorldAreas / BaseItemType display name surfaces as "+off -> row -> +inner \"Name\"".
static void ScanRowForNames(MemoryReader reader, nint row, string indent)
{
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (var off = 0; off < 0xE0; off += 1)        // < 0xEF: stay within this row (next row's code starts there)
    {
        var q = SafePtr(reader, row + off);
        if (q == 0) continue;
        var w = reader.ReadStringUtf16(q, 64);
        if (Printable(w)) { if (seen.Add(w)) Console.WriteLine($"{indent}+0x{off:X3} -> \"{w}\""); continue; }
        // q is a pointer to another .dat row — scan its first columns for strings (the display name may be here).
        for (var io = 0; io < 0x40; io += 1)
        {
            var ip = SafePtr(reader, q + io);
            if (ip == 0) continue;
            var iw = reader.ReadStringUtf16(ip, 64);
            if (Printable(iw) && seen.Add($"{off:X3}/{io:X2}:{iw}"))
                Console.WriteLine($"{indent}+0x{off:X3} -> row 0x{q:X} +0x{io:X2} -> \"{iw}\"");
        }
    }
}

// ── Atlas hover-flag diff: confirm WHICH element class/node the game highlights on hover ────────
// Snapshot every element of the candidate class's UI flags (+0x180) + state bytes; poll while the user
// hovers atlas map nodes. The element whose flags change on hover IS the hovered map node — proving
// the class and revealing the hover-highlight bit. If NOTHING changes, that class isn't hover-reactive
// (decoration), and we try another. Seed class via --vt; default = the biome-bearing scattered class.
static int RunAtlasHoverFlag(ProcessHandle process, MemoryReader reader, nint seedVt)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain."); return 1; }
    var uiRoot = SafePtr(reader, igs + 0x2F0);
    var root = SafePtr(reader, uiRoot + 0xB8) is var tr && tr != 0 ? tr : uiRoot;

    var queue = new Queue<nint>(); queue.Enqueue(root);
    var visited = new HashSet<nint>();
    var byVtable = new Dictionary<nint, List<nint>>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el) || SafePtr(reader, el + 0x08) != el) continue;
        var vt = SafePtr(reader, el);
        if (vt != 0) (byVtable.TryGetValue(vt, out var l) ? l : byVtable[vt] = new()).Add(el);
        var first = SafePtr(reader, el + 0x10);
        if (first != 0 && reader.TryReadStruct<nint>(el + 0x18, out var last))
        { var n = ((long)last - (long)first) / 8; if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) queue.Enqueue(SafePtr(reader, first + (nint)(k * 8))); }
    }
    if (seedVt == 0)
    {
        var bestB = 0;
        foreach (var (vt, list) in byVtable)
        { if (list.Count < 50) continue; var b = new HashSet<int>(); foreach (var el in list.Take(400)) if (reader.TryReadStruct<byte>(el + 0x32E, out var bb) && bb is >= 1 and <= 12) b.Add(bb); if (b.Count > bestB && list.Count > 200) { bestB = b.Count; seedVt = vt; } }
    }
    if (!byVtable.TryGetValue(seedVt, out var els)) { Console.Error.WriteLine($"class 0x{seedVt:X} not found."); return 1; }
    Console.WriteLine($"watching {els.Count} elements of class 0x{seedVt:X} for hover-flag changes.");
    Console.WriteLine("Hover atlas MAP NODES slowly. The element that changes = the hovered node. Ctrl+C to stop.\n");

    // Baseline a few state words per element: UI flags +0x180, +0x32C/+0x32F bytes packed.
    var prevFlags = new Dictionary<nint, uint>();
    var prevState = new Dictionary<nint, uint>();
    foreach (var el in els)
    { reader.TryReadStruct<uint>(el + 0x180, out var f); prevFlags[el] = f; reader.TryReadStruct<uint>(el + 0x32C, out var s); prevState[el] = s; }

    while (true)
    {
        foreach (var el in els)
        {
            reader.TryReadStruct<uint>(el + 0x180, out var f);
            reader.TryReadStruct<uint>(el + 0x32C, out var s);
            if (f != prevFlags[el] || s != prevState[el])
            {
                reader.TryReadStruct<uint>(el + 0x300, out var id);
                reader.TryReadStruct<byte>(el + 0x32E, out var biome);
                reader.TryReadStruct<float>(el + 0x118, out var px); reader.TryReadStruct<float>(el + 0x11C, out var py);
                Console.WriteLine($"[hover] 0x{el:X} id={id} biome={biome} pos=({px:F0},{py:F0})  flags 0x{prevFlags[el]:X8}->0x{f:X8}  state 0x{prevState[el]:X8}->0x{s:X8}");
                prevFlags[el] = f; prevState[el] = s;
            }
        }
        Thread.Sleep(250);
    }
}

// ── Atlas canvas inventory: enumerate the atlas canvas's children grouped by element class ──────
// The biome-decoration tiles are direct children of the atlas canvas; the real clickable map nodes
// are likely SIBLINGS on the same canvas in a different element class. Find the canvas (parent of a
// biome-bearing 40×40 scattered element), then list every child class with a sample, so we can tell
// the node class (likely with a content/data pointer + completion that varies) from decoration art.
static int RunAtlasCanvas(ProcessHandle process, MemoryReader reader, nint seedVt)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain."); return 1; }
    var uiRoot = SafePtr(reader, igs + 0x2F0);
    var root = SafePtr(reader, uiRoot + 0xB8) is var tr && tr != 0 ? tr : uiRoot;

    // BFS to find the biome-decoration class (biome 0..12 varies, 40×40), then its parent = canvas.
    var queue = new Queue<nint>(); queue.Enqueue(root);
    var visited = new HashSet<nint>();
    var byVtable = new Dictionary<nint, List<nint>>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el) || SafePtr(reader, el + 0x08) != el) continue;
        var vt = SafePtr(reader, el);
        if (vt != 0) (byVtable.TryGetValue(vt, out var l) ? l : byVtable[vt] = new()).Add(el);
        var first = SafePtr(reader, el + 0x10);
        if (first != 0 && reader.TryReadStruct<nint>(el + 0x18, out var last))
        { var n = ((long)last - (long)first) / 8; if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) queue.Enqueue(SafePtr(reader, first + (nint)(k * 8))); }
    }
    nint decoVt = seedVt;
    if (decoVt == 0)
    {
        var bestB = 0;
        foreach (var (vt, list) in byVtable)
        {
            if (list.Count < 50) continue;
            var b = new HashSet<int>();
            foreach (var el in list.Take(400)) if (reader.TryReadStruct<byte>(el + 0x32E, out var bb) && bb is >= 1 and <= 12) b.Add(bb);
            if (b.Count > bestB && list.Count is > 200) { bestB = b.Count; decoVt = vt; }
        }
    }
    if (decoVt == 0 || !byVtable.ContainsKey(decoVt)) { Console.Error.WriteLine($"class 0x{decoVt:X} not found (in the Atlas?)."); return 1; }
    var canvas = SafePtr(reader, byVtable[decoVt][0] + 0xB8);
    Console.WriteLine($"seed class 0x{decoVt:X} ({byVtable[decoVt].Count}); parent canvas 0x{canvas:X}");

    // Enumerate canvas children by class.
    var cFirst = SafePtr(reader, canvas + 0x10);
    reader.TryReadStruct<nint>(canvas + 0x18, out var cLast);
    var cCount = cFirst == 0 ? 0 : ((long)cLast - (long)cFirst) / 8;
    Console.WriteLine($"canvas has {cCount} direct children. Classes:");
    var childByVt = new Dictionary<nint, List<nint>>();
    for (long i = 0; i < cCount && i < 50000; i++)
    {
        var ch = SafePtr(reader, cFirst + (nint)(i * 8));
        if (ch == 0 || SafePtr(reader, ch + 0x08) != ch) continue;
        var vt = SafePtr(reader, ch);
        (childByVt.TryGetValue(vt, out var l) ? l : childByVt[vt] = new()).Add(ch);
    }
    foreach (var (vt, list) in childByVt.OrderByDescending(k => k.Value.Count))
    {
        Console.WriteLine($"\n  class 0x{vt:X}: {list.Count} children");
        foreach (var el in list.Take(5))
        {
            reader.TryReadStruct<uint>(el + 0x300, out var id);
            reader.TryReadStruct<uint>(el + 0x310, out var content);
            reader.TryReadStruct<byte>(el + 0x32E, out var biome);
            reader.TryReadStruct<byte>(el + 0x32F, out var flags);
            reader.TryReadStruct<float>(el + 0x118, out var px);
            reader.TryReadStruct<float>(el + 0x11C, out var py);
            reader.TryReadStruct<float>(el + 0x288, out var sw);
            reader.TryReadStruct<float>(el + 0x28C, out var sh);
            Console.WriteLine($"      0x{el:X} id={id} content={content} biome={biome} flags=0x{flags:X2} pos=({px:F0},{py:F0}) size=({sw:F0}x{sh:F0})");
        }
    }
    return 0;
}

// ── Atlas transform: find the canvas pan/zoom by watching a node's ancestor chain while panning ──
// Node positions (+0x118) are large CANVAS coords; the atlas pans a viewport over that canvas. So a
// node's SCREEN pos = (sum of RelativePos up the parent chain) with the canvas container carrying the
// pan offset + zoom. This enumerates the node class, picks one node, prints its ancestor chain
// (addr/vtable/RelativePos/Size/scale), then polls each ancestor's RelativePos — PAN the atlas and the
// ancestor whose pos changes is the pan transform; its scale (+0x130) is the zoom.
static int RunAtlasXform(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain."); return 1; }
    var uiRoot = SafePtr(reader, igs + 0x2F0);
    var root = SafePtr(reader, uiRoot + 0xB8) is var tr && tr != 0 ? tr : uiRoot;

    // BFS, group by vtable, pick the atlas-node class (most distinct nonzero biomes among instances).
    var queue = new Queue<nint>(); queue.Enqueue(root);
    var visited = new HashSet<nint>();
    var byVtable = new Dictionary<nint, List<nint>>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el) || SafePtr(reader, el + 0x08) != el) continue;
        var vt = SafePtr(reader, el);
        if (vt != 0) (byVtable.TryGetValue(vt, out var l) ? l : byVtable[vt] = new()).Add(el);
        var first = SafePtr(reader, el + 0x10);
        if (first != 0 && reader.TryReadStruct<nint>(el + 0x18, out var last))
        {
            var n = ((long)last - (long)first) / 8;
            if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) queue.Enqueue(SafePtr(reader, first + (nint)(k * 8)));
        }
    }
    nint nodeVt = 0; var bestBiomes = 0;
    foreach (var (vt, list) in byVtable)
    {
        if (list.Count < 50) continue;
        var biomes = new HashSet<int>();
        foreach (var el in list.Take(400)) if (reader.TryReadStruct<byte>(el + 0x32E, out var b) && b is >= 1 and <= 12) biomes.Add(b);
        if (biomes.Count > bestBiomes) { bestBiomes = biomes.Count; nodeVt = vt; }
    }
    if (nodeVt == 0) { Console.Error.WriteLine("no atlas-node class found (in the Atlas?)."); return 1; }
    var node = byVtable[nodeVt][0];
    Console.WriteLine($"node class vtable 0x{nodeVt:X} ({byVtable[nodeVt].Count} nodes). Sample node 0x{node:X}.");

    // Ancestor chain via Parent (+0xB8).
    var chain = new List<nint>(); var cur = node; var guard = 0;
    while (cur != 0 && guard++ < 16) { chain.Add(cur); var par = SafePtr(reader, cur + 0xB8); if (par == cur || par == 0) break; cur = par; }
    Console.WriteLine($"ancestor chain (node → root), {chain.Count} levels:");
    foreach (var (a, i) in chain.Select((a, i) => (a, i)))
    {
        reader.TryReadStruct<float>(a + 0x118, out var rx); reader.TryReadStruct<float>(a + 0x11C, out var ry);
        reader.TryReadStruct<float>(a + 0x130, out var scale);
        reader.TryReadStruct<float>(a + 0x288, out var sw); reader.TryReadStruct<float>(a + 0x28C, out var sh);
        Console.WriteLine($"  [{i,2}] 0x{a:X} vt=0x{SafePtr(reader, a):X} relPos=({rx:F1},{ry:F1}) scale={scale:G5} size=({sw:F0}x{sh:F0})");
    }
    Console.WriteLine("\nNow ZOOM the atlas in/out. Watching relPos(+0x118) AND scale(+0x130) per ancestor.");
    Console.WriteLine("  • If a node's relPos CHANGES on zoom → relPos already includes zoom.");
    Console.WriteLine("  • If only some ancestor's scale changes → that's the zoom scalar; relPos is zoom-independent.\n");

    var prev = new Dictionary<nint, (float rx, float ry, float sc)>();
    while (true)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var a = chain[i];
            reader.TryReadStruct<float>(a + 0x118, out var rx); reader.TryReadStruct<float>(a + 0x11C, out var ry);
            reader.TryReadStruct<float>(a + 0x130, out var sc);
            if (prev.TryGetValue(a, out var old) &&
                (MathF.Abs(old.rx - rx) > 0.5f || MathF.Abs(old.ry - ry) > 0.5f || MathF.Abs(old.sc - sc) > 0.001f))
                Console.WriteLine($"  [{i,2}] 0x{a:X} relPos ({old.rx:F1},{old.ry:F1})->({rx:F1},{ry:F1})  scale {old.sc:F4}->{sc:F4}");
            prev[a] = (rx, ry, sc);
        }
        Thread.Sleep(300);
    }
}

// ── Atlas PROBE: one-shot recovery + validation of the WHOLE atlas projection chain ─────────────
// This is THE command to run after a patch breaks the atlas overlay. It re-locates the node class +
// canvas, validates every field offset with sanity checks (flagging drift), prints the ancestor chain
// with its scales (the zoom propagation), DERIVES the full projection from memory + window metrics,
// and prints a paste-ready offset block for Poe2Offsets.cs. The projection model (validated live
// 2026-06-07): screen = (UIscale × zoom) × relPos + offset, where
//   • relPos  = node UiElement +0x118 (read live; already includes PAN)
//   • zoom    = node/canvas scale +0x130 (read live; 0.85 at max zoom-out, larger zoomed in)
//   • UIscale = winH / 1600 (design height); offset = canvas-origin screen pos ≈ factor × halfIcon.
static int RunAtlasProbe(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain (in game?)."); return 1; }
    var uiRoot = SafePtr(reader, igs + 0x2F0);
    var root = SafePtr(reader, uiRoot + 0xB8) is var tr && tr != 0 ? tr : uiRoot;
    Console.WriteLine("ATLAS PROJECTION PROBE — recovery + validation\n==============================================");

    // 1) BFS the UI tree, group by vtable, pick the atlas-node class = the vtable whose instances spread
    //    across the most distinct biome values (generic elements are all biome 0).
    var queue = new Queue<nint>(); queue.Enqueue(root);
    var visited = new HashSet<nint>();
    var byVtable = new Dictionary<nint, List<nint>>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el) || SafePtr(reader, el + 0x08) != el) continue;
        var vt = SafePtr(reader, el);
        if (vt != 0) (byVtable.TryGetValue(vt, out var l) ? l : byVtable[vt] = new()).Add(el);
        var first = SafePtr(reader, el + 0x10);
        if (first != 0 && reader.TryReadStruct<nint>(el + 0x18, out var last))
        { var n = ((long)last - (long)first) / 8; if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) queue.Enqueue(SafePtr(reader, first + (nint)(k * 8))); }
    }
    // Score every candidate vtable on THREE independent signals so a stray biome-ish list can't win:
    //   • biome spread (distinct 1..12)   • modal size ≈ 40×40   • instance count (atlas has ~1000+).
    // The real atlas-node class is the one that's ~40×40 AND biome-spread ≥3 AND has the most instances.
    nint nodeVt = 0; var bestBiomes = 0; (float w, float h) bestSize = (0, 0); var ranked = new List<(nint vt, int count, int biomes, float w, float h)>();
    foreach (var (vt, list) in byVtable)
    {
        if (list.Count < 50) continue;
        var biomes = new HashSet<int>(); var szs = new List<(float, float)>();
        foreach (var el in list.Take(400))
        {
            if (reader.TryReadStruct<byte>(el + 0x32E, out var b) && b is >= 1 and <= 12) biomes.Add(b);
            if (reader.TryReadStruct<float>(el + 0x288, out var sw) && reader.TryReadStruct<float>(el + 0x28C, out var sh)) szs.Add((sw, sh));
        }
        var modal = szs.GroupBy(s => ((int)s.Item1, (int)s.Item2)).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? (0, 0);
        ranked.Add((vt, list.Count, biomes.Count, modal.Item1, modal.Item2));
    }
    // Prefer ~40×40 AND biome-spread≥3, then max instance count. Fall back to max biome-spread if none qualify.
    var qualified = ranked.Where(r => r.w is >= 28 and <= 56 && r.biomes >= 3).OrderByDescending(r => r.count).ToList();
    var pick = qualified.FirstOrDefault();
    if (pick.vt == 0) { pick = ranked.OrderByDescending(r => r.biomes).ThenByDescending(r => r.count).FirstOrDefault(); if (pick.vt != 0) Console.WriteLine("    (no ~40×40 biome class — falling back to max biome-spread; verify the Atlas map view is open)"); }
    if (pick.vt == 0) { Console.Error.WriteLine("FAIL: no atlas-node class found. Open the Atlas MAP view (Endgame tab), then re-run."); return 1; }
    nodeVt = pick.vt; bestBiomes = pick.biomes; bestSize = (pick.w, pick.h);
    var nodes = byVtable[nodeVt];
    Console.WriteLine($"[1] node class vtable = 0x{nodeVt:X}  ({nodes.Count} instances, {bestBiomes} distinct biomes, size {bestSize.w:F0}x{bestSize.h:F0}) — module +0x{(long)nodeVt - (long)process.MainModuleBase:X}");
    if (nodes.Count < 200) Console.WriteLine($"    ⚠ only {nodes.Count} instances — expected ~1000+. The Atlas map view may not be open; results below may be wrong.");

    // 2) Canvas = the parent holding the MOST node-class children.
    var parentCount = new Dictionary<nint, int>();
    foreach (var el in nodes) { var p = SafePtr(reader, el + 0xB8); if (p != 0) parentCount[p] = parentCount.GetValueOrDefault(p) + 1; }
    var canvas = parentCount.OrderByDescending(k => k.Value).First();
    Console.WriteLine($"[2] canvas = 0x{canvas.Key:X}  (holds {canvas.Value} node-class children)");
    // Replicate the overlay's HierarchicallyVisible gate on the canvas (walk Parent +0xB8, check Flags
    // +0x180 bit 0x0B) — this is what decides whether the overlay reads nodes. If it reports HIDDEN while
    // the atlas is clearly open, the gate is the bug.
    {
        var vcur = canvas.Key; var vguard = 0; var visible = true; var sbv = new System.Text.StringBuilder();
        while (vcur != 0 && vguard++ < 16)
        {
            reader.TryReadStruct<uint>(vcur + 0x180, out var fl);
            var bit = ((fl >> 0x0B) & 1) != 0;
            sbv.Append($"0x{vcur:X}[fl=0x{fl:X} vis={(bit ? 1 : 0)}] → ");
            if (!bit) { visible = false; }
            var par = SafePtr(reader, vcur + 0xB8); if (par == vcur || par == 0) break; vcur = par;
        }
        Console.WriteLine($"    gate HierarchicallyVisible = {visible}");
        Console.WriteLine($"    chain: {sbv}root");
    }

    // 3) Validate the per-node field offsets with sanity checks; flag drift.
    Console.WriteLine("\n[3] FIELD VALIDATION (sanity checks — PASS / ⚠ DRIFT):");
    var sample = nodes.Take(400).ToList();
    int finiteRel = 0, distinctRel; var relSet = new HashSet<(int, int)>();
    var scales = new List<float>(); var sizes = new List<(float, float)>(); var ids = new HashSet<uint>(); var biomeHist = new Dictionary<int, int>();
    foreach (var el in sample)
    {
        if (reader.TryReadStruct<float>(el + 0x118, out var rx) && reader.TryReadStruct<float>(el + 0x11C, out var ry) && float.IsFinite(rx) && float.IsFinite(ry))
        { finiteRel++; relSet.Add(((int)rx, (int)ry)); }
        if (reader.TryReadStruct<float>(el + 0x130, out var sc) && sc > 0.01f && sc < 4f) scales.Add(sc);
        if (reader.TryReadStruct<float>(el + 0x288, out var sw) && reader.TryReadStruct<float>(el + 0x28C, out var sh)) sizes.Add((sw, sh));
        if (reader.TryReadStruct<uint>(el + 0x300, out var id)) ids.Add(id);
        if (reader.TryReadStruct<byte>(el + 0x32E, out var bm)) biomeHist[bm] = biomeHist.GetValueOrDefault(bm) + 1;
    }
    distinctRel = relSet.Count;
    scales.Sort(); var zoom = scales.Count > 0 ? scales[scales.Count / 2] : 0f;
    var modeSize = sizes.GroupBy(s => s).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? (0, 0);
    void Check(string name, int off, bool ok, string detail) => Console.WriteLine($"    {(ok ? "PASS" : "⚠ DRIFT")}  {name,-22} +0x{off:X3}  {detail}");
    Check("RelativePos", 0x118, finiteRel > sample.Count * 0.8 && distinctRel > sample.Count / 2, $"{finiteRel}/{sample.Count} finite, {distinctRel} distinct positions");
    Check("scale (zoom)", 0x130, zoom is > 0.05f and < 4f, $"median zoom = {zoom:F4} (expect ~0.85 at max zoom-out)");
    Check("Size W/H", 0x288, modeSize.Item1 is >= 16 and <= 128, $"mode size = {modeSize.Item1:F0}x{modeSize.Item2:F0} (expect ~40x40)");
    Check("MapNodeId", 0x300, ids.Count >= 20, $"{ids.Count} distinct ids over {sample.Count} nodes (a map-TYPE id, shared by same-type nodes — element addr is the unique key)");
    Check("Biome", 0x32E, biomeHist.Keys.Count(k => k is >= 1 and <= 12) >= 3, $"biomes present: {string.Join(",", biomeHist.Keys.OrderBy(k => k))}");

    // 4) Ancestor chain + scales (the zoom propagation — for re-deriving the chain if it moves).
    Console.WriteLine("\n[4] ANCESTOR CHAIN (node → root) with relPos + scale:");
    var chain = new List<nint>(); var cur = nodes[0]; var guard = 0;
    while (cur != 0 && guard++ < 16) { chain.Add(cur); var par = SafePtr(reader, cur + 0xB8); if (par == cur || par == 0) break; cur = par; }
    foreach (var (a, i) in chain.Select((a, i) => (a, i)))
    {
        reader.TryReadStruct<float>(a + 0x118, out var rx); reader.TryReadStruct<float>(a + 0x11C, out var ry);
        reader.TryReadStruct<float>(a + 0x130, out var sc); reader.TryReadStruct<float>(a + 0x288, out var sw); reader.TryReadStruct<float>(a + 0x28C, out var sh);
        Console.WriteLine($"    [{i,2}] 0x{a:X} relPos=({rx:F1},{ry:F1}) scale={sc:G5} size=({sw:F0}x{sh:F0})");
    }

    // 5) Derive the projection from memory + window metrics, and print the live transform.
    int winW = Win.GetSystemMetrics(0), winH = Win.GetSystemMetrics(1); if (winH <= 0) { winW = 1920; winH = 1080; }
    const float DesignH = 1600f;
    float uiscale = winH / DesignH;
    float factor = uiscale * zoom;
    float half = modeSize.Item1 / 2f;
    float offX = factor * half, offY = factor * half; // canvas origin ≈ 0; the offset is ~½-icon × factor
    Console.WriteLine("\n[5] DERIVED PROJECTION (no calibration):");
    Console.WriteLine($"    window={winW}x{winH}  designH={DesignH:F0}  UIscale={uiscale:F4}  zoom={zoom:F4}");
    Console.WriteLine($"    factor = UIscale×zoom = {factor:F4}   offset ≈ factor×½icon = ({offX:F1},{offY:F1})");
    Console.WriteLine($"    ⇒ screen = {factor:F4}·relPos + ({offX:F1},{offY:F1})    [relPos & zoom read live each frame]");
    Console.WriteLine("    NOTE: a one-time F10/F11 calibration in the overlay refines the offset to ~2px (this");
    Console.WriteLine("          auto-derivation lands ~5-7px). Calibration anchors at the zoom you solved at.");

    // 6) Paste-ready offsets for Poe2Offsets.cs.
    Console.WriteLine("\n[6] PASTE-READY (Poe2Offsets.cs — re-confirm markers if any changed):");
    Console.WriteLine("    UiElement:  Self +0x08  Children +0x10/+0x18  Parent +0xB8  RelativePos +0x118  scale +0x130  Flags +0x180 (vis bit 0x0B)  Size +0x288/+0x28C");
    Console.WriteLine("    AtlasNode:  MapNodeId +0x300  Content +0x310  State +0x32C  Biome +0x32E  Flags +0x32F  Completion +0x339");
    Console.WriteLine($"    node-class vtable (drifts every patch): module +0x{(long)nodeVt - (long)process.MainModuleBase:X}");
    Console.WriteLine("\nDONE. If any line above is ⚠ DRIFT, that offset moved — re-discover with --atlas-canvas / --atlas-nodes2.");
    return 0;
}

// ── Atlas GRAPH probe: validate the GameHelper2-sourced node GRID COORDINATES + CONNECTION GRAPH ──
// (resources/GameHelper2-main .../ImportantUiElements.cs). These are the two structures POE2Radar
// currently LACKS — they're what enables node-to-node atlas pathfinding ("route from here to that map
// in the fewest hops"). GH2 reads: grid pos @ nodeElem+0x320 (StdTuple2D<int>); connection edges @
// atlasPanel+0x5A8 (StdVector of {int unknown; Tuple2D<int> src; Tuple2D<int> dst}); and a node-DATA
// model two derefs in (*(*(node+0x10)+0x20)) carrying biome+0x2CE / status+0x2CF / mapId+0x2A0 chain.
// We BRUTE-SCAN for each offset (don't trust GH2 blindly — it may be a different build) and report the
// discovered offset + PASS/DRIFT vs GH2's value. Run with the Atlas MAP view open.
static int RunAtlasGraph(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain (in game?)."); return 1; }
    Console.WriteLine("ATLAS GRAPH PROBE — grid coords + connection graph (GameHelper2 structures)\n=========================================================================");

    var (vt, canvas, nodes) = FindAtlasNodeClass(reader, igs);
    if (vt == 0 || nodes.Count < 50) { Console.Error.WriteLine($"FAIL: atlas-node class not found ({nodes.Count} instances). Open the Atlas MAP view, then re-run."); return 1; }
    Console.WriteLine($"[0] node class 0x{vt:X}  canvas 0x{canvas:X}  ({nodes.Count} node instances)\n");
    var sample = nodes.Take(800).ToList();

    // ── [1] GRID COORDINATES — scan node element +0x300..+0x360 for an (int32,int32) pair that is small
    //        (|v|≤512), finite, and highly distinct per node (a real grid coord), then report best off. ──
    Console.WriteLine("[1] GRID COORDINATE field (expect GH2 +0x320):");
    (int off, int score, int distinct, int inRange, int minX, int maxX, int minY, int maxY) bestGrid = (-1, 0, 0, 0, 0, 0, 0, 0);
    for (var o = 0x300; o <= 0x35C; o += 4)
    {
        var pairs = new HashSet<(int, int)>(); int inRange = 0, total = 0;
        int mnx = int.MaxValue, mxx = int.MinValue, mny = int.MaxValue, mxy = int.MinValue;
        foreach (var el in sample)
        {
            if (!reader.TryReadStruct<int>(el + o, out var x) || !reader.TryReadStruct<int>(el + o + 4, out var y)) continue;
            total++;
            if (x is >= -512 and <= 512 && y is >= -512 and <= 512) { inRange++; pairs.Add((x, y)); if (x < mnx) mnx = x; if (x > mxx) mxx = x; if (y < mny) mny = y; if (y > mxy) mxy = y; }
        }
        if (total == 0) continue;
        // A grid field: nearly all in range AND many distinct pairs (not a constant/type id).
        var score = (inRange * 100 / Math.Max(1, total)) + pairs.Count;
        if (inRange > total * 0.9 && pairs.Count > sample.Count / 2 && score > bestGrid.score)
            bestGrid = (o, score, pairs.Count, inRange, mnx, mxx, mny, mxy);
    }
    var gridOff = bestGrid.off;
    if (gridOff < 0) Console.WriteLine("    ⚠ no grid-coord-like (int,int) field found in +0x300..+0x360.");
    else Console.WriteLine($"    {(gridOff == 0x320 ? "PASS" : "⚠ DRIFT")}  grid coords @ +0x{gridOff:X3}  (GH2=+0x320)  {bestGrid.distinct} distinct, {bestGrid.inRange}/{sample.Count} in-range, X[{bestGrid.minX}..{bestGrid.maxX}] Y[{bestGrid.minY}..{bestGrid.maxY}]");

    // Build the grid-position set (used to validate connection edges below).
    var gridSet = new HashSet<(int, int)>();
    var gridByEl = new Dictionary<nint, (int, int)>();
    if (gridOff >= 0)
        foreach (var el in nodes)
            if (reader.TryReadStruct<int>(el + gridOff, out var gx) && reader.TryReadStruct<int>(el + gridOff + 4, out var gy)
                && gx is >= -512 and <= 512 && gy is >= -512 and <= 512)
            { gridSet.Add((gx, gy)); gridByEl[el] = (gx, gy); }
    Console.WriteLine($"    → {gridSet.Count} distinct grid positions across all {nodes.Count} nodes\n");

    // ── [2] CONNECTION GRAPH — scan the canvas (and the atlas panel ancestors) for a StdVector whose
    //        elements are edges {int; Tuple2D<int> src; Tuple2D<int> dst} with src+dst in the grid set. ──
    Console.WriteLine("[2] CONNECTION edge vector (expect GH2 panel +0x5A8, edge stride 20 = {int,src,dst}):");
    // Candidate containers: the canvas + a few ancestors (GH2's "atlas" element may be an ancestor of
    // the canvas that actually parents the node children).
    var containers = new List<(string label, nint addr)> { ("canvas", canvas) };
    { var cur = canvas; for (var i = 0; i < 3; i++) { var p = SafePtr(reader, cur + 0xB8); if (p == 0 || p == cur) break; containers.Add(($"canvas.parent[{i + 1}]", p)); cur = p; } }

    (string who, int off, int stride, int edges, int valid) bestConn = ("", -1, 0, 0, 0);
    foreach (var (label, addr) in containers)
    {
        if (addr == 0) continue;
        for (var o = 0x400; o <= 0x800; o += 8)
        {
            var begin = SafePtr(reader, addr + o);
            if (!reader.TryReadStruct<nint>(addr + o + 8, out var end)) continue;
            if (begin == 0 || end <= begin) continue;
            var bytes = (long)end - (long)begin;
            if (bytes is < 20 or > 20_000_000) continue;
            foreach (var stride in new[] { 20, 24, 16 })
            {
                if (bytes % stride != 0) continue;
                var count = (int)(bytes / stride);
                if (count is < 8 or > 200000) continue;
                // Sample edges: read {int @+0; src @+4; dst @+12} and count how many have BOTH endpoints
                // in the grid set (the decisive signal that this is the connection graph).
                int valid = 0, tested = 0;
                for (var i = 0; i < count && tested < 200; i++)
                {
                    var e = begin + (nint)(i * stride); tested++;
                    if (!reader.TryReadStruct<int>(e + 4, out var sx) || !reader.TryReadStruct<int>(e + 8, out var sy)) continue;
                    if (!reader.TryReadStruct<int>(e + 12, out var dx) || !reader.TryReadStruct<int>(e + 16, out var dy)) continue;
                    if (gridSet.Contains((sx, sy)) && gridSet.Contains((dx, dy))) valid++;
                }
                if (valid > bestConn.valid && valid >= tested / 2)
                    bestConn = (label, o, stride, count, valid * 100 / Math.Max(1, tested));
            }
        }
    }
    if (bestConn.off < 0) Console.WriteLine("    ⚠ no edge vector found whose endpoints land on grid positions. (Grid offset wrong, or connections live elsewhere.)");
    else
    {
        var on5A8 = bestConn.who == "canvas" && bestConn.off == 0x5A8;
        Console.WriteLine($"    {(on5A8 ? "PASS" : "⚠ DRIFT")}  edges @ {bestConn.who}+0x{bestConn.off:X3}  stride {bestConn.stride}  (GH2=canvas+0x5A8 stride 20)");
        Console.WriteLine($"    {bestConn.edges} edges, {bestConn.valid}% of sampled endpoints land on real grid positions");
        // Build adjacency from the discovered vector and report degree stats — a real atlas graph is
        // sparse (most nodes 2-6 neighbours).
        var who = containers.First(c => c.label == bestConn.who).addr;
        var begin = SafePtr(reader, who + bestConn.off);
        var adj = new Dictionary<(int, int), HashSet<(int, int)>>();
        for (var i = 0; i < bestConn.edges; i++)
        {
            var e = begin + (nint)(i * bestConn.stride);
            if (!reader.TryReadStruct<int>(e + 4, out var sx) || !reader.TryReadStruct<int>(e + 8, out var sy)) continue;
            if (!reader.TryReadStruct<int>(e + 12, out var dx) || !reader.TryReadStruct<int>(e + 16, out var dy)) continue;
            if (!gridSet.Contains((sx, sy)) || !gridSet.Contains((dx, dy))) continue;
            (adj.TryGetValue((sx, sy), out var a) ? a : adj[(sx, sy)] = new()).Add((dx, dy));
            (adj.TryGetValue((dx, dy), out var b) ? b : adj[(dx, dy)] = new()).Add((sx, sy));
        }
        var degrees = adj.Values.Select(s => s.Count).ToList();
        if (degrees.Count > 0) Console.WriteLine($"    graph: {adj.Count} connected nodes, avg degree {degrees.Average():F1}, max {degrees.Max()} (atlas graphs are sparse — expect 2-6)");
    }
    Console.WriteLine();

    // ── [3] node-DATA model chain (GH2: *(*(node+0x10)+0x20) → biome+0x2CE / status+0x2CF / mapId+0x2A0).
    //        Cross-check its biome against the element's own +0x32E biome we already trust. ──
    Console.WriteLine("[3] node-DATA model chain (GH2: *(*(node+0x10)+0x20) → +0x2CE biome / +0x2CF status / +0x2A0 mapId):");
    int dataOk = 0, biomeMatch = 0, mapIdOk = 0, tested3 = 0; string exMap = "";
    foreach (var el in sample.Take(200))
    {
        tested3++;
        var storage = SafePtr(reader, el + 0x10);
        if (storage == 0) continue;
        var data = SafePtr(reader, storage + 0x20);
        if (data == 0) continue;
        dataOk++;
        reader.TryReadStruct<byte>(el + 0x32E, out var elemBiome);          // trusted element biome
        reader.TryReadStruct<byte>(data + 0x2CE, out var dataBiome);
        if (elemBiome == dataBiome) biomeMatch++;
        var wrap = SafePtr(reader, data + 0x2A0);
        if (wrap != 0) { var hdr = SafePtr(reader, wrap); if (hdr != 0) { var buf = SafePtr(reader, hdr); var s = buf != 0 ? reader.ReadStringUtf16(buf, 64) : ""; if (s.StartsWith("Map", StringComparison.Ordinal)) { mapIdOk++; if (exMap == "") exMap = s; } } }
    }
    Console.WriteLine($"    nodeData resolved: {dataOk}/{tested3}   biome matches element +0x32E: {biomeMatch}/{dataOk}   mapId 'Map…' read: {mapIdOk}/{dataOk}  e.g. \"{exMap}\"");
    if (dataOk < tested3 / 2) Console.WriteLine("    ⚠ nodeData chain (*(*(node+0x10)+0x20)) mostly null — POE2Radar already reads biome/mapId DIRECTLY off the element (+0x32E / +0x300 row), so this model may be unneeded.");

    Console.WriteLine("\nSUMMARY");
    Console.WriteLine($"  grid coords : {(gridOff >= 0 ? $"+0x{gridOff:X3} ({gridSet.Count} positions)" : "NOT FOUND")}");
    Console.WriteLine($"  connections : {(bestConn.off >= 0 ? $"{bestConn.who}+0x{bestConn.off:X3} stride {bestConn.stride} ({bestConn.edges} edges)" : "NOT FOUND")}");
    Console.WriteLine("  → if both found, atlas node-graph pathfinding (player→target in fewest hops) is portable from GH2's A*.");
    return 0;
}

// ── Atlas CURRENT-NODE discovery: find what marks the tile the player is standing in (the "player icon"
//    tile). Hover that tile in-game, then run this — it identifies the hovered node and hunts for the
//    distinguishing signal three ways: (A) a per-node FLAG that's unique to it, (B) an extra CHILD element
//    (the player-icon sprite), (C) an external POINTER to it (InGameState / canvas / AreaInstance). ──
static int RunAtlasCurrent(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, ai, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain (in game?)."); return 1; }
    // AreaCode is TWO derefs: ai+AreaInfoPtr → AreaInfo; *AreaInfo → string ptr (matches Poe2Live.AreaCode).
    var areaInfo = SafePtr(reader, ai + Poe2.AreaInstance.AreaInfoPtr);
    var areaCode = reader.ReadStringUtf16(SafePtr(reader, areaInfo), 64);
    Console.WriteLine($"ATLAS CURRENT-NODE DISCOVERY\n============================\ncurrent area code = \"{areaCode}\"\n");

    var (vt, canvas, nodes) = FindAtlasNodeClass(reader, igs);
    if (vt == 0 || nodes.Count < 50) { Console.Error.WriteLine($"FAIL: node class not found ({nodes.Count}). Open the Atlas MAP view."); return 1; }
    Console.WriteLine($"node class 0x{vt:X}  canvas 0x{canvas:X}  ({nodes.Count} nodes)");

    // Per-node: grid (+0x320) + raw map code via the data-model chain *(*(el+0x10)+0x20)+0x2A0.
    string CodeOf(nint el)
    {
        var storage = SafePtr(reader, el + 0x10); if (storage == 0) return "";
        var data = SafePtr(reader, storage + 0x20); if (data == 0) return "";
        var wrap = SafePtr(reader, data + 0x2A0); if (wrap == 0) return "";
        var hdr = SafePtr(reader, wrap); if (hdr == 0) return "";
        var buf = SafePtr(reader, hdr); return buf == 0 ? "" : reader.ReadStringUtf16(buf, 64);
    }
    (int x, int y) GridOf(nint el)
    { reader.TryReadStruct<int>(el + 0x320, out var gx); reader.TryReadStruct<int>(el + 0x324, out var gy); return (gx, gy); }

    // Identify the HOVERED node (cursor inverse-projected into canvas/relPos units), like the F10 inspector.
    var scales = new List<float>();
    foreach (var el in nodes) if (reader.TryReadStruct<float>(el + 0x130, out var sc) && sc is > 0.01f and < 4f) scales.Add(sc);
    scales.Sort(); float zoom = scales.Count > 0 ? scales[scales.Count / 2] : 0.85f;
    int winH = Win.GetSystemMetrics(1); if (winH <= 0) winH = 1080;
    float pscale = winH / 1600f * zoom; if (pscale < 1e-4f) pscale = 1f;
    Win.GetCursorPos(out var cur);
    double curX = cur.X / pscale, curY = cur.Y / pscale;
    nint hovered = 0; double bIn = 1e18, bAny = 1e18; nint hoverAny = 0;
    foreach (var el in nodes)
    {
        if (!reader.TryReadStruct<float>(el + 0x118, out var rx) || !reader.TryReadStruct<float>(el + 0x11C, out var ry)) continue;
        reader.TryReadStruct<float>(el + 0x288, out var w); reader.TryReadStruct<float>(el + 0x28C, out var h);
        double dx = curX - rx, dy = curY - ry, d = dx * dx + dy * dy;
        if (d < bAny) { bAny = d; hoverAny = el; }
        double hw = (w > 1 ? w : 40) * 0.5, hh = (h > 1 ? h : 40) * 0.5;
        if (Math.Abs(dx) <= hw && Math.Abs(dy) <= hh && d < bIn) { bIn = d; hovered = el; }
    }
    hovered = hovered != 0 ? hovered : hoverAny;
    if (hovered == 0) { Console.Error.WriteLine("no node under cursor."); return 1; }
    Console.WriteLine($"hovered node 0x{hovered:X}  grid {GridOf(hovered)}  code \"{CodeOf(hovered)}\"  (zoom {zoom:F3}, win H {winH})");

    // Nodes whose code matches the current area (there may be several — the player icon picks the real one).
    var sameCode = nodes.Where(el => !string.IsNullOrEmpty(areaCode) && CodeOf(el).Equals(areaCode, StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"nodes matching area code \"{areaCode}\": {sameCode.Count}  [{string.Join(" ", sameCode.Take(12).Select(GridOf))}]\n");

    // ── (A) UNIQUE PER-NODE FLAG: scan +0x100..+0x400 for a uint field where the hovered node's value is
    //        rare (≤2 nodes) while a single modal value dominates (>70%) — a "you are here" flag pattern. ──
    Console.WriteLine("[A] per-node fields where the HOVERED node stands out (candidate 'current' flag):");
    int hitsA = 0;
    for (var o = 0x100; o <= 0x3FC; o += 4)
    {
        if (!reader.TryReadStruct<uint>(hovered + o, out var hv)) continue;
        var hist = new Dictionary<uint, int>();
        foreach (var el in nodes) if (reader.TryReadStruct<uint>(el + o, out var v)) hist[v] = hist.GetValueOrDefault(v) + 1;
        if (!hist.TryGetValue(hv, out var hvCount)) continue;
        var modal = hist.OrderByDescending(k => k.Value).First();
        if (hvCount <= 2 && hv != modal.Key && modal.Value > nodes.Count * 0.7)
        { Console.WriteLine($"    +0x{o:X3}  hovered=0x{hv:X8} (shared by {hvCount}); modal=0x{modal.Key:X8} ({modal.Value}/{nodes.Count})"); hitsA++; }
    }
    if (hitsA == 0) Console.WriteLine("    (none — the marker isn't a simple unique uint on the node element)");

    // ── (B) EXTRA CHILD: the player icon may be an extra child UiElement. Compare child counts. ──
    int ChildCount(nint el)
    { var f = SafePtr(reader, el + 0x10); if (f == 0 || !reader.TryReadStruct<nint>(el + 0x18, out var l)) return -1; var n = ((long)l - (long)f) / 8; return n is >= 0 and < 100000 ? (int)n : -1; }
    var hChildren = ChildCount(hovered);
    var childHist = new Dictionary<int, int>();
    foreach (var el in nodes) { var c = ChildCount(el); if (c >= 0) childHist[c] = childHist.GetValueOrDefault(c) + 1; }
    Console.WriteLine($"\n[B] child count: hovered={hChildren}; distribution {string.Join(" ", childHist.OrderBy(k => k.Key).Select(k => $"{k.Key}×{k.Value}"))}");
    if (hChildren > 0)
    {
        var first = SafePtr(reader, hovered + 0x10);
        for (var i = 0; i < hChildren && i < 8; i++)
        { var ch = SafePtr(reader, first + (nint)(i * 8)); var cvt = SafePtr(reader, ch); Console.WriteLine($"      child[{i}] 0x{ch:X} vtable 0x{cvt:X} (mod +0x{(long)cvt - (long)process.MainModuleBase:X})"); }
    }

    // ── (C) ANY pointer-to-a-NODE in InGameState / canvas / AreaInstance — print the node it resolves to
    //        (code + grid), independent of the cursor. A 'current location' field points to the SAME node
    //        (your map) no matter where you hover; a 'hovered' field follows the cursor. Re-run hovering a
    //        DIFFERENT tile: the offset whose target DOESN'T move is the current-location marker. ──
    var nodeSet = new HashSet<nint>(nodes);
    Console.WriteLine("\n[C] container fields that point AT a node (code/grid of the target):");
    int hitsC = 0;
    void ScanPtr(string who, nint baseAddr, int span)
    {
        var buf = new byte[span];
        if (reader.TryReadBytes(baseAddr, buf) < buf.Length) return;
        for (var o = 0; o + 8 <= span; o += 8)
        {
            var p = (nint)BitConverter.ToInt64(buf, o);
            if (p != 0 && nodeSet.Contains(p))
            { Console.WriteLine($"    {who}+0x{o:X3} → 0x{p:X} grid {GridOf(p)} code \"{CodeOf(p)}\"{(p == hovered ? "   <= HOVERED" : "")}"); hitsC++; }
        }
    }
    ScanPtr("InGameState", igs, 0x1500);
    ScanPtr("canvas", canvas, 0x1500);
    ScanPtr("AreaInstance", ai, 0x1000);
    if (hitsC == 0) Console.WriteLine("    (no field points at a node in the scanned ranges)");

    // ── WATCH: sample canvas+0x420 vs the live hovered node for ~6s. MOVE THE MOUSE over different tiles.
    //    If +0x420's target FOLLOWS the cursor → it's just 'hovered node'. If it STAYS on your map while the
    //    hovered tile changes → it's the current-location pointer we want. ──
    Console.WriteLine("\n[WATCH] move the mouse over DIFFERENT tiles for ~15s (Ctrl+C to stop early):");
    nint lastA = 0, lastHov = 0;
    for (var i = 0; i < 60; i++)
    {
        Win.GetCursorPos(out var c2); double hx = c2.X / pscale, hy = c2.Y / pscale;
        nint hv2 = 0; double best = 1e18;
        foreach (var el in nodes)
        {
            if (!reader.TryReadStruct<float>(el + 0x118, out var rx) || !reader.TryReadStruct<float>(el + 0x11C, out var ry)) continue;
            double dx = hx - rx, dy = hy - ry, d = dx * dx + dy * dy;
            if (d < best) { best = d; hv2 = el; }
        }
        var a420 = SafePtr(reader, canvas + 0x420);
        if (a420 != lastA || hv2 != lastHov)
        {
            Console.WriteLine($"    hovered {GridOf(hv2)} \"{CodeOf(hv2)}\"    |    canvas+0x420 → {GridOf(a420)} \"{CodeOf(a420)}\"{(a420 == hv2 ? "  (==hovered)" : "  (FIXED ≠ hovered)")}");
            lastA = a420; lastHov = hv2;
        }
        System.Threading.Thread.Sleep(250);
    }
    Console.WriteLine("\nVerdict: if canvas+0x420 stayed FIXED while 'hovered' changed → that's the current-location node.");
    return 0;
}

// ── Characterise the CURRENT-LOCATION marker element: the (hypothesised unique) non-node UiElement whose
//    +0x300 points at a node-class element. Confirms uniqueness + a STRUCTURAL accessor (no hardcoded
//    vtable, which drifts per patch): "the element E with Ptr(E+0x300) ∈ node set". Dumps its node, vtable,
//    position, and ancestry so we can wire the overlay to read currentNode = *(marker+0x300) each frame. ──
static int RunAtlasMarker(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain (in game?)."); return 1; }
    var (vt, canvas, nodes) = FindAtlasNodeClass(reader, igs);
    if (vt == 0 || nodes.Count < 50) { Console.Error.WriteLine($"FAIL: node class not found ({nodes.Count})."); return 1; }
    var nodeSet = new HashSet<nint>(nodes);
    (int x, int y) GridOf(nint el) { reader.TryReadStruct<int>(el + 0x320, out var gx); reader.TryReadStruct<int>(el + 0x324, out var gy); return (gx, gy); }
    string CodeOf(nint el)
    { var st = SafePtr(reader, el + 0x10); if (st == 0) return ""; var d = SafePtr(reader, st + 0x20); if (d == 0) return ""; var w = SafePtr(reader, d + 0x2A0); if (w == 0) return ""; var h = SafePtr(reader, w); if (h == 0) return ""; var b = SafePtr(reader, h); return b == 0 ? "" : reader.ReadStringUtf16(b, 64); }

    // BFS the whole UI tree; collect every element E (Self==self) whose +0x300 points at a node-class element.
    var uiRoot = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    var root = SafePtr(reader, uiRoot + 0xB8) is var tr && tr != 0 ? tr : uiRoot;
    var queue = new Queue<nint>(); queue.Enqueue(root);
    var visited = new HashSet<nint>();
    var markers = new List<nint>();
    while (queue.Count > 0 && visited.Count < 300000)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el) || SafePtr(reader, el + 0x08) != el) continue;
        if (!nodeSet.Contains(el)) { var p300 = SafePtr(reader, el + 0x300); if (p300 != 0 && nodeSet.Contains(p300)) markers.Add(el); }
        var first = SafePtr(reader, el + 0x10);
        if (first != 0 && reader.TryReadStruct<nint>(el + 0x18, out var last))
        { var n = ((long)last - (long)first) / 8; if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) queue.Enqueue(SafePtr(reader, first + (nint)(k * 8))); }
    }

    Console.WriteLine($"ATLAS CURRENT-LOCATION MARKER\n=============================\nnodes {nodes.Count}  canvas 0x{canvas:X}");
    Console.WriteLine($"non-node elements whose +0x300 → a node: {markers.Count}\n");
    foreach (var m in markers)
    {
        var node = SafePtr(reader, m + 0x300);
        reader.TryReadStruct<float>(m + 0x118, out var mrx); reader.TryReadStruct<float>(m + 0x11C, out var mry);
        reader.TryReadStruct<uint>(m + 0x180, out var fl);
        Console.WriteLine($"  marker 0x{m:X}  vt 0x{SafePtr(reader, m):X} (mod +0x{(long)SafePtr(reader, m) - (long)process.MainModuleBase:X})  relPos=({mrx:F0},{mry:F0})  visBit={((fl >> 0x0B) & 1)}");
        Console.WriteLine($"      → current node 0x{node:X}  grid {GridOf(node)}  code \"{CodeOf(node)}\"");
        // Ancestry: walk Parent (+0xB8) and show child indices, to find a stable path (canvas relation).
        var chain = new List<nint>(); var cur = m; var g = 0;
        while (cur != 0 && g++ < 12) { chain.Add(cur); var par = SafePtr(reader, cur + 0xB8); if (par == cur || par == 0) break; cur = par; }
        Console.WriteLine($"      ancestry: {string.Join(" → ", chain.Select(a => $"0x{a:X}"))}");
        Console.WriteLine($"      (canvas in ancestry: {chain.Contains(canvas)}; marker.parent==canvas.parent: {SafePtr(reader, m + 0xB8) == SafePtr(reader, canvas + 0xB8)})");
    }
    if (markers.Count == 0) Console.WriteLine("  (none right now — is the Atlas map view open with your player icon visible?)");
    Console.WriteLine("\nIf exactly ONE marker and it points at your current map, currentNode = *(marker+0x300).");
    Console.WriteLine("Accessor is structural (find the lone non-node element whose +0x300 ∈ node set) — vtable-independent.");
    return 0;
}

// ── Back-scan for whatever points at the CURRENT node. Hover your current (player-icon) tile; this grabs
//    that node via the hover pointer (canvas+0x420), then scans ALL memory for 8-byte-aligned pointers to
//    it and classifies each: the hover field, the canvas children array (structural), or a CANDIDATE
//    'current-location' field elsewhere. The candidate that's stable across maps is the marker we want. ──
static int RunAtlasFindCur(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, ai, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain (in game?)."); return 1; }
    var (vt, canvas, nodes) = FindAtlasNodeClass(reader, igs);
    if (vt == 0 || nodes.Count < 50) { Console.Error.WriteLine($"FAIL: node class not found ({nodes.Count}). Open the Atlas MAP view."); return 1; }
    var nodeSet = new HashSet<nint>(nodes);

    string CodeOf(nint el)
    { var st = SafePtr(reader, el + 0x10); if (st == 0) return ""; var d = SafePtr(reader, st + 0x20); if (d == 0) return ""; var w = SafePtr(reader, d + 0x2A0); if (w == 0) return ""; var h = SafePtr(reader, w); if (h == 0) return ""; var b = SafePtr(reader, h); return b == 0 ? "" : reader.ReadStringUtf16(b, 64); }
    (int x, int y) GridOf(nint el)
    { reader.TryReadStruct<int>(el + 0x320, out var gx); reader.TryReadStruct<int>(el + 0x324, out var gy); return (gx, gy); }

    // Capture the current node from the hover pointer (hover your player-icon tile while this starts).
    var target = SafePtr(reader, canvas + 0x420);
    if (target == 0 || !nodeSet.Contains(target)) { Console.Error.WriteLine("Hover your CURRENT (player-icon) tile so canvas+0x420 captures it, then re-run."); return 1; }
    Console.WriteLine($"ATLAS CURRENT-NODE BACK-SCAN\n============================\ntarget (current) node 0x{target:X}  grid {GridOf(target)}  code \"{CodeOf(target)}\"");

    var childBegin = SafePtr(reader, canvas + 0x10);
    reader.TryReadStruct<nint>(canvas + 0x18, out var childEnd);
    Console.WriteLine($"canvas 0x{canvas:X}  children array [0x{childBegin:X}..0x{childEnd:X})  ({((long)childEnd - (long)childBegin) / 8} entries)");

    // Baseline node B: an ordinary node (neither current nor hovered). Its only referrers are STRUCTURAL
    // (self / child→parent / children-array), the same ones A has. Diffing A's referrers against B's cancels
    // structure out; what's left for A (minus the hover pointer) is the current-location marker.
    var baseline = nodes.First(n => n != target);
    Console.WriteLine($"baseline node B 0x{baseline:X} grid {GridOf(baseline)} code \"{CodeOf(baseline)}\"\n");

    // Normalised "signature" of a referrer, so A's and B's structural refs compare equal.
    string Sig(nint h, nint tgt)
    {
        if (h == tgt + 0x08) return "self+0x08";
        if (h >= childBegin && h < childEnd) return "children-array";
        if (h == canvas + 0x420) return "HOVER canvas+0x420";
        if (h >= igs && h < igs + 0x4000) return $"InGameState+0x{(long)h - (long)igs:X}";
        if (h >= ai && h < ai + 0x4000) return $"AreaInstance+0x{(long)h - (long)ai:X}";
        if (h >= canvas && h < canvas + 0x4000) return $"canvas+0x{(long)h - (long)canvas:X}";
        nint owner = 0; for (var b = 0; b <= 0x600; b += 8) { var a = h - b; if (SafePtr(reader, a + 0x08) == a) { owner = a; break; } }
        if (owner != 0) return $"UiElement(vt 0x{SafePtr(reader, owner):X})+0x{(long)h - (long)owner:X}";
        return "raw-arena-ptr";
    }

    var aHits = ScanBytes(reader, BitConverter.GetBytes((long)target), allRegions: true, max: 4000, aligned: 8);
    var bHits = ScanBytes(reader, BitConverter.GetBytes((long)baseline), allRegions: true, max: 4000, aligned: 8);
    var bSigs = new Dictionary<string, int>();
    foreach (var h in bHits) { var s = Sig(h, baseline); bSigs[s] = bSigs.GetValueOrDefault(s) + 1; }
    var aBySig = new Dictionary<string, List<nint>>();
    foreach (var h in aHits) { var s = Sig(h, target); (aBySig.TryGetValue(s, out var l) ? l : aBySig[s] = new()).Add(h); }

    Console.WriteLine($"refs → A(current)={aHits.Count}  B(baseline)={bHits.Count}\n");
    Console.WriteLine("A's referrers NOT shared with B (current-location candidates; HOVER excluded):");
    var found = 0;
    foreach (var (sig, list) in aBySig)
    {
        if (sig.StartsWith("HOVER")) { Console.WriteLine($"    [{sig}] ×{list.Count}  (hovered — ignore)"); continue; }
        var extra = list.Count - bSigs.GetValueOrDefault(sig);
        if (extra <= 0) continue;   // structural — B has the same, cancels
        found++;
        Console.WriteLine($"    ★ {sig}  ×{extra}   e.g. 0x{list[^1]:X}");
    }
    if (found == 0) Console.WriteLine("    (none — current node isn't referenced by a unique pointer; the marker likely stores its GRID/map-id)");
    Console.WriteLine("\nThe ★ signature stable across maps is the current-location pointer.");
    return 0;
}

// ── Shared: locate the atlas-node class + canvas (scored on size≈40×40 + biome-spread + count). ──
static (nint vt, nint canvas, List<nint> nodes) FindAtlasNodeClass(MemoryReader reader, nint igs)
{
    var uiRoot = SafePtr(reader, igs + 0x2F0);
    var root = SafePtr(reader, uiRoot + 0xB8) is var tr && tr != 0 ? tr : uiRoot;
    var queue = new Queue<nint>(); queue.Enqueue(root);
    var visited = new HashSet<nint>();
    var byVtable = new Dictionary<nint, List<nint>>();
    while (queue.Count > 0 && visited.Count < 200000)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el) || SafePtr(reader, el + 0x08) != el) continue;
        var vt = SafePtr(reader, el);
        if (vt != 0) (byVtable.TryGetValue(vt, out var l) ? l : byVtable[vt] = new()).Add(el);
        var first = SafePtr(reader, el + 0x10);
        if (first != 0 && reader.TryReadStruct<nint>(el + 0x18, out var last))
        { var n = ((long)last - (long)first) / 8; if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) queue.Enqueue(SafePtr(reader, first + (nint)(k * 8))); }
    }
    var ranked = new List<(nint vt, int count, int biomes, float w)>();
    foreach (var (vt, list) in byVtable)
    {
        if (list.Count < 50) continue;
        var biomes = new HashSet<int>(); var szs = new List<float>();
        foreach (var el in list.Take(400))
        { if (reader.TryReadStruct<byte>(el + 0x32E, out var b) && b is >= 1 and <= 12) biomes.Add(b); if (reader.TryReadStruct<float>(el + 0x288, out var sw)) szs.Add(sw); }
        var modalW = szs.GroupBy(s => (int)s).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? 0;
        ranked.Add((vt, list.Count, biomes.Count, modalW));
    }
    var pick = ranked.Where(r => r.w is >= 28 and <= 56 && r.biomes >= 3).OrderByDescending(r => r.count).FirstOrDefault();
    if (pick.vt == 0) pick = ranked.OrderByDescending(r => r.biomes).ThenByDescending(r => r.count).FirstOrDefault();
    if (pick.vt == 0) return (0, 0, new List<nint>());
    var nodes = byVtable[pick.vt];
    var parentCount = new Dictionary<nint, int>();
    foreach (var el in nodes) { var p = SafePtr(reader, el + 0xB8); if (p != 0) parentCount[p] = parentCount.GetValueOrDefault(p) + 1; }
    var canvas = parentCount.Count == 0 ? 0 : parentCount.OrderByDescending(k => k.Value).First().Key;
    return (pick.vt, canvas, nodes);
}

// Sniff name/path strings reachable from a UI element: scan its first 0xC0 bytes for canonical pointers,
// deref each, and try reading a string there (UTF-16 and ASCII) + at common 1-hop offsets (*(p), *(p+0x20)).
// Content/icon defs carry a path or name string near their start → names the modifier directly.
static List<string> SniffPointerStrings(MemoryReader reader, nint baseAddr, int span = 0xC0, int cap = 6)
{
    var outp = new List<string>(); var seen = new HashSet<long>();
    var buf = new byte[span];
    if (reader.TryReadBytes(baseAddr, buf) < buf.Length) return outp;
    string AsciiAt(nint a)
    {
        var b = new byte[64]; if (reader.TryReadBytes(a, b) <= 0) return "";
        var sb = new System.Text.StringBuilder(); foreach (var ch in b) { if (ch >= 0x20 && ch < 0x7f) sb.Append((char)ch); else break; }
        return sb.ToString();
    }
    for (var off = 0; off + 8 <= buf.Length && outp.Count < cap; off += 8)
    {
        var p = (nint)BitConverter.ToInt64(buf, off);
        if ((ulong)p < 0x10000 || (ulong)p > 0x7FFFFFFFFFFF || !seen.Add((long)p)) continue;
        foreach (var cand in new[] { p, SafePtr(reader, p), SafePtr(reader, p + 0x20), SafePtr(reader, p + 0x8) })
        {
            if (cand == 0) continue;
            var w = reader.ReadStringUtf16(cand, 64); var a = AsciiAt(cand);
            var s = Printable(w) && w.Length >= 4 ? w : (Printable(a) && a.Length >= 4 ? a : null);
            if (s != null) { outp.Add($"+0x{off:X2}→\"{s}\""); break; }
        }
    }
    return outp;
}

// ── Atlas RESOLVE: exhaustively try to turn a node's ContentVec ids → modifier NAMES ─────────────
// For the node under the cursor, tries every route: ContentVec ids as arena offsets (several base
// candidates derived from the node's dat pointers + the known 0x40180000000 arena), pointer
// reconstruction, and deep-sniffing the tag-icon children for icon-path/name strings. Hover a node
// whose mechanic you KNOW, run this, and see which route prints that name → that's our mapping route.
static int RunAtlasResolve(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain."); return 1; }
    var (vt, canvas, all) = FindAtlasNodeClass(reader, igs);
    if (vt == 0) { Console.Error.WriteLine("FAIL: no node class (open the Atlas MAP view)."); return 1; }
    // Canvas children = the drawn set (carries name + ContentVec).
    var nodes = new List<nint>();
    var cf = SafePtr(reader, canvas + 0x10); reader.TryReadStruct<nint>(canvas + 0x18, out var cl);
    var cc = (cf != 0 && (long)cl > (long)cf) ? ((long)cl - (long)cf) / 8 : 0;
    for (long i = 0; i < cc && i < 20000; i++) { var ch = SafePtr(reader, cf + (nint)(i * 8)); if (ch != 0 && SafePtr(reader, ch) == vt) nodes.Add(ch); }
    if (nodes.Count < 8) nodes = all;
    // LAYER SURVEY: group ALL node instances by parent canvas; report which canvas holds the
    // content-bearing nodes (+0x310 atlas-row populated, non-empty ContentVec). Resolves the
    // "display layer vs data layer" ambiguity definitively.
    Console.WriteLine("LAYER SURVEY (parent canvas → children / with +0x310 / with content):");
    var byParent = new Dictionary<nint, List<nint>>();
    foreach (var n in all) { var p = SafePtr(reader, n + 0xB8); if (p != 0) (byParent.TryGetValue(p, out var l) ? l : byParent[p] = new()).Add(n); }
    foreach (var (p, list) in byParent.OrderByDescending(k => k.Value.Count).Take(6))
    {
        int withRow = 0, withContent = 0;
        foreach (var n in list)
        {
            if (SafePtr(reader, n + 0x310) != 0) withRow++;
            var b = SafePtr(reader, n + 0x350); reader.TryReadStruct<nint>(n + 0x358, out var e);
            if (b != 0 && (long)e > (long)b) withContent++;
        }
        var lo = list.Min(x => (long)x); var hi = list.Max(x => (long)x);
        Console.WriteLine($"  canvas 0x{p:X}: {list.Count} kids, {withRow} with +0x310 row, {withContent} with ContentVec  (addr 0x{lo:X}..0x{hi:X})");
    }
    Console.WriteLine();
    if (!Win.GetCursorPos(out var cur)) { Console.Error.WriteLine("no cursor."); return 1; }
    int winH = Win.GetSystemMetrics(1); if (winH <= 0) winH = 1080;
    float uiscale = winH / 1600f;
    var scales = nodes.Select(n => { reader.TryReadStruct<float>(n + 0x130, out var s); return s; }).Where(s => s > 0.01f).OrderBy(s => s).ToList();
    float zoom = scales.Count > 0 ? scales[scales.Count / 2] : 0.85f;
    float factor = uiscale * zoom, off = factor * 20f;
    // Pick the nearest DATA node (+0x310 populated) — the empty display twins are skipped. Fall back to
    // nearest-overall only if no data node is within reach.
    nint el = 0, elAny = 0; double bd = 1e18, bdAny = 1e18;
    foreach (var n in nodes)
    {
        reader.TryReadStruct<float>(n + 0x118, out var rx); reader.TryReadStruct<float>(n + 0x11C, out var ry);
        double d = (factor * rx + off - cur.X) * (factor * rx + off - cur.X) + (factor * ry + off - cur.Y) * (factor * ry + off - cur.Y);
        if (d < bdAny) { bdAny = d; elAny = n; }
        if (SafePtr(reader, n + 0x310) != 0 && d < bd) { bd = d; el = n; }
    }
    if (el == 0 || bd > 400.0 * 400.0) { el = elAny; bd = bdAny; }
    if (el == 0) { Console.Error.WriteLine("no node near cursor."); return 1; }
    Console.WriteLine($"cursor ({cur.X},{cur.Y})  zoom {zoom:F3}  pickDist(data node) {Math.Sqrt(bd):F0}px");

    string? TryStr(nint a) { if ((ulong)a < 0x10000 || (ulong)a > 0x7FFFFFFFFFFF) return null; var w = reader.ReadStringUtf16(a, 64); if (Printable(w) && w.Length >= 3) return w; var b = new byte[80]; if (reader.TryReadBytes(a, b) > 0) { var sb = new System.Text.StringBuilder(); foreach (var ch in b) { if (ch >= 0x20 && ch < 0x7f) sb.Append((char)ch); else break; } if (sb.Length >= 3) return sb.ToString(); } return null; }
    // Resolve a candidate address through a few hops, returning the first readable string.
    string? Deref(nint a) { foreach (var c in new[] { a, SafePtr(reader, a), SafePtr(reader, a + 0x20), SafePtr(reader, a + 0x8), SafePtr(reader, a + 0x10) }) { var s = TryStr(c); if (s != null) return s; } return null; }

    var p300 = SafePtr(reader, el + 0x300); var p308 = SafePtr(reader, el + 0x308); var p318 = SafePtr(reader, el + 0x318);
    Console.WriteLine($"RESOLVE node 0x{el:X}  dat ptrs: +0x300=0x{p300:X} +0x308=0x{p308:X} +0x318=0x{p318:X}");
    Console.WriteLine($"  +0x300 → \"{Deref(p300)}\"   +0x318 → \"{Deref(p318)}\"");
    var vb = SafePtr(reader, el + 0x350); reader.TryReadStruct<nint>(el + 0x358, out var ve);
    var vlen = (vb != 0 && (long)ve > (long)vb) ? (int)((long)ve - (long)vb) : 0;
    var ids = new List<uint>(); for (var i = 0; i < vlen / 4 && i < 16; i++) { reader.TryReadStruct<uint>(vb + (nint)(i * 4), out var x); ids.Add(x); }
    Console.WriteLine($"  ContentVec ({vlen}B): {string.Join(", ", ids.Select(x => $"{x} (0x{x:X})"))}\n");

    // Base candidates for the "id is an offset into a data arena" hypothesis.
    long[] bases =
    {
        (long)p300 & ~0xFFFFFFFFL, (long)p308 & ~0xFFFFFFFFL, (long)p318 & ~0xFFFFFFFFL,
        (long)p300 & ~0xFFFFFFFFFFL, 0x40180000000L,
        (long)el & ~0xFFFFFFFFL,
    };
    var p310 = SafePtr(reader, el + 0x310);
    long[] dumpBases = { (long)p300 & ~0xFFFFFFFFL, (long)p310 & ~0xFFFFFFFFL, (long)p318 & ~0xFFFFFFFFL, 0x40180000000L };
    Console.WriteLine("[Route A] id as offset into an arena — RAW DUMP + sniff at base+id (non-base ids):");
    foreach (var id in ids.Where(x => x != 4220400))
        foreach (var b in dumpBases.Distinct())
        {
            var cand = (nint)(b + id);
            var raw = new byte[0x40];
            if (reader.TryReadBytes(cand, raw) < 0x40) continue; // unmapped → wrong base
            var q = new List<string>(); for (var k = 0; k < 4; k++) q.Add($"0x{BitConverter.ToInt64(raw, k * 8):X}");
            var strs = SniffPointerStrings(reader, cand, 0x80, 8);
            Console.WriteLine($"  id 0x{id:X} @ base 0x{b:X} (=0x{cand:X}): [{string.Join(" ", q)}]");
            if (strs.Count > 0) Console.WriteLine($"        strs: {string.Join("  ", strs)}");
        }

    // [Route B] tag-icon children deep sniff (icon path / def name).
    Console.WriteLine("\n[Route B] tag-icon children — chase +0x300/+0x310 ptrs + deep sniff (incl. grandchildren):");
    void DumpKid(nint ch, string lbl)
    {
        var k300 = SafePtr(reader, ch + 0x300); var k310 = SafePtr(reader, ch + 0x310);
        var s300 = Deref(k300); var s310 = Deref(k310);
        if (s300 != null || s310 != null) Console.WriteLine($"  {lbl} 0x{ch:X}: +0x300→\"{s300}\"  +0x310→\"{s310}\"");
        foreach (var s in SniffPointerStrings(reader, ch, 0x360, 16)) Console.WriteLine($"  {lbl} 0x{ch:X} sniff: {s}");
    }
    var kf = SafePtr(reader, el + 0x10); reader.TryReadStruct<nint>(el + 0x18, out var kl);
    var kn = (kf != 0 && (long)kl > (long)kf) ? ((long)kl - (long)kf) / 8 : 0;
    for (long i = 0; i < kn && i < 12; i++)
    {
        var ch = SafePtr(reader, kf + (nint)(i * 8)); if (ch == 0) continue;
        DumpKid(ch, $"child[{i}]");
        var gf = SafePtr(reader, ch + 0x10); reader.TryReadStruct<nint>(ch + 0x18, out var gl);
        var gn = (gf != 0 && (long)gl > (long)gf) ? ((long)gl - (long)gf) / 8 : 0;
        for (long j = 0; j < gn && j < 6; j++) { var gc = SafePtr(reader, gf + (nint)(j * 8)); if (gc != 0) DumpKid(gc, $"  gchild[{i}.{j}]"); }
    }

    // [Route C] deep-sniff the node itself (the NAME may sit near the description).
    Console.WriteLine("\n[Route C] node deep-sniff (0..0x400) — name should be near the description:");
    foreach (var s in SniffPointerStrings(reader, el, 0x400, 30)) Console.WriteLine($"  {s}");

    // [Route D] The per-node EndgameMapAtlas.dat ROW (+0x310 target). Dump it and chase every pointer it
    // holds 2 levels deep for strings — the rolled content may be pointers to content rows (codes/names).
    Console.WriteLine($"\n[Route D] per-node EndgameMapAtlas row @ +0x310 = 0x{p310:X} — raw + pointer chase:");
    if (p310 != 0)
    {
        var row = new byte[0x100];
        if (reader.TryReadBytes(p310, row) >= 0x100)
        {
            for (var o = 0; o + 8 <= 0x100; o += 8)
            {
                var pp = (nint)BitConverter.ToInt64(row, o);
                if ((ulong)pp < 0x10000 || (ulong)pp > 0x7FFFFFFFFFFF) continue;
                var s = Deref(pp);
                var strs2 = SniffPointerStrings(reader, pp, 0xA0, 6);
                if (s != null || strs2.Count > 0) Console.WriteLine($"  +0x{o:X2}→0x{pp:X}  direct:\"{s}\"  sniff:{string.Join(",", strs2)}");
            }
        }
        else Console.WriteLine("  (row not fully readable)");
    }
    // [Route E] The EndgameMaps (map-TYPE) row at node +0x300 — dump it WIDE to locate the map NAME
    // ("Vaal Ruins", "Precursor Tower", …) which sits near the description. For map-type/name tags.
    Console.WriteLine($"\n[Route E] EndgameMaps row @ node+0x300 = 0x{p300:X} — wide string scan for the map NAME:");
    for (var eoff = -0x40; eoff < 0x140; eoff += 8)
    {
        var pp = SafePtr(reader, (nint)((long)p300 + eoff));
        if (pp == 0) continue;
        var s = TryStr(pp); if (s == null) { var d2 = SafePtr(reader, pp); s = d2 == 0 ? null : TryStr(d2); }
        if (s != null) Console.WriteLine($"  +0x{eoff:X} → \"{s}\"");
    }
    Console.WriteLine("\nDONE. Whichever route printed your hovered node's known mechanic name is the mapping route.");
    return 0;
}

// ── Atlas CONTENT dump: find WHERE per-node modifiers live (Breach/Ritual/boss/etc.) ─────────────
// IconType (a content int found by walking child icon elements) shows ONE tag; maps carry many. This
// surveys every candidate carrier per node — the +0x310 content u32, the +0x350/+0x358 ContentVec, and
// the child TAG elements (each child's +0x300/+0x310 + size) — and histograms the content-type ids on
// the current atlas. Run it, then hover a node whose modifier you KNOW (read the tooltip) and re-run:
// the "NODE UNDER CURSOR" dump correlates that id → name. Build the id→name table from those examples.
static int RunAtlasContent(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain."); return 1; }
    var (vt, canvas, nodes) = FindAtlasNodeClass(reader, igs);
    if (vt == 0) { Console.Error.WriteLine("FAIL: no node class (open the Atlas MAP view)."); return 1; }
    Console.WriteLine($"ATLAS CONTENT DUMP — node class 0x{vt:X}, {nodes.Count} instances, canvas 0x{canvas:X}");
    // Use the CANVAS CHILDREN (the set the overlay actually draws) — not every vtable instance, which
    // also includes non-displayed twins (e.g. a hover-highlight layer) that carry no content.
    if (canvas != 0)
    {
        var cf0 = SafePtr(reader, canvas + 0x10); reader.TryReadStruct<nint>(canvas + 0x18, out var cl0);
        var cc0 = (cf0 != 0 && (long)cl0 > (long)cf0) ? ((long)cl0 - (long)cf0) / 8 : 0;
        var kids = new List<nint>();
        for (long i = 0; i < cc0 && i < 20000; i++) { var ch = SafePtr(reader, cf0 + (nint)(i * 8)); if (ch != 0 && SafePtr(reader, ch) == vt) kids.Add(ch); }
        if (kids.Count >= 8) { nodes = kids; Console.WriteLine($"using {kids.Count} CANVAS CHILDREN (the drawn set)"); }
    }
    Console.WriteLine();

    // [A] +0x310 content u32 histogram across ALL nodes (the per-node 'content' field).
    var c310 = new Dictionary<uint, int>();
    var vecLens = new Dictionary<long, int>();
    foreach (var n in nodes)
    {
        if (reader.TryReadStruct<uint>(n + 0x310, out var c)) c310[c] = c310.GetValueOrDefault(c) + 1;
        var b = SafePtr(reader, n + 0x350); reader.TryReadStruct<nint>(n + 0x358, out var e);
        var len = (b != 0 && e != 0 && (long)e > (long)b) ? ((long)e - (long)b) : 0;
        vecLens[len] = vecLens.GetValueOrDefault(len) + 1;
    }
    Console.WriteLine("[A] +0x310 content u32 histogram (value: #nodes):");
    foreach (var kv in c310.OrderByDescending(k => k.Value).Take(20)) Console.WriteLine($"      {kv.Key,6} : {kv.Value}");
    Console.WriteLine("\n[B] +0x350 ContentVec byte-length histogram (len: #nodes) — len/4 and len/8 hint element stride:");
    foreach (var kv in vecLens.OrderBy(k => k.Key).Take(20)) Console.WriteLine($"      {kv.Key,6} bytes : {kv.Value} nodes  (/4={kv.Key / 4}, /8={kv.Key / 8})");

    // [C] Detailed dump of nodes that carry content (cap 20): base fields + ContentVec raw + tag children.
    Console.WriteLine("\n[C] CONTENT-BEARING NODES (base + ContentVec + tag children), up to 20:");
    var shown = 0;
    foreach (var n in nodes)
    {
        reader.TryReadStruct<uint>(n + 0x310, out var content);
        var vb = SafePtr(reader, n + 0x350); reader.TryReadStruct<nint>(n + 0x358, out var ve);
        var vlen = (vb != 0 && ve != 0 && (long)ve > (long)vb) ? ((long)ve - (long)vb) : 0;
        if (content == 0 && vlen == 0) continue;
        if (shown++ >= 20) break;
        reader.TryReadStruct<uint>(n + 0x300, out var id); reader.TryReadStruct<byte>(n + 0x32E, out var biome);
        reader.TryReadStruct<byte>(n + 0x32F, out var fl); reader.TryReadStruct<byte>(n + 0x339, out var comp);
        Console.WriteLine($"  node 0x{n:X} id={id} biome={biome} flags=0x{fl:X2} compl={comp} content(+0x310)={content} vec={vlen}B");
        // ContentVec raw: dump up to 8 entries as both u32 and u64 so we can tell ids from pointers.
        if (vlen is > 0 and <= 4096)
        {
            var cnt = (int)Math.Min(8, vlen / 4);
            var u32 = new List<string>(); for (var i = 0; i < cnt; i++) { reader.TryReadStruct<uint>(vb + (nint)(i * 4), out var u); u32.Add(u.ToString()); }
            Console.WriteLine($"      vec u32[]: {string.Join(", ", u32)}");
        }
        // Tag children: each child element's content ids + size (the drawn tag icons live here).
        var cf = SafePtr(reader, n + 0x10); reader.TryReadStruct<nint>(n + 0x18, out var cl);
        var ccount = (cf != 0 && (long)cl > (long)cf) ? ((long)cl - (long)cf) / 8 : 0;
        for (long i = 0; i < ccount && i < 12; i++)
        {
            var ch = SafePtr(reader, cf + (nint)(i * 8)); if (ch == 0) continue;
            reader.TryReadStruct<uint>(ch + 0x300, out var ci300); reader.TryReadStruct<uint>(ch + 0x310, out var ci310);
            reader.TryReadStruct<float>(ch + 0x288, out var cw); reader.TryReadStruct<float>(ch + 0x28C, out var chh);
            reader.TryReadStruct<float>(ch + 0x118, out var crx); reader.TryReadStruct<float>(ch + 0x11C, out var cry);
            if (ci300 != 0 || ci310 != 0)
                Console.WriteLine($"      child[{i}] 0x{ch:X} +0x300={ci300} +0x310={ci310} size={cw:F0}x{chh:F0} relPos=({crx:F0},{cry:F0})");
        }
    }

    // [D] ALL node-class elements near the cursor (overlapping layers: the tile + its tag icons may be
    //     separate elements). For each: base fields + ContentVec + sniffed name strings (deref pointers),
    //     and the same for its children. The tag elements' icon/content def usually carries a NAME/path.
    if (Win.GetCursorPos(out var cur))
    {
        int winH = Win.GetSystemMetrics(1); if (winH <= 0) winH = 1080;
        float uiscale = winH / 1600f;
        reader.TryReadStruct<float>(nodes[0] + 0x130, out var zoom); if (zoom < 0.01f) zoom = 0.85f;
        float factor = uiscale * zoom, off = factor * 20f;
        var near = new List<(double d, nint el, float rx, float ry)>();
        foreach (var n in nodes)
        {
            reader.TryReadStruct<float>(n + 0x118, out var rx); reader.TryReadStruct<float>(n + 0x11C, out var ry);
            double sx = factor * rx + off, sy = factor * ry + off, d = Math.Sqrt((sx - cur.X) * (sx - cur.X) + (sy - cur.Y) * (sy - cur.Y));
            if (d <= 80) near.Add((d, n, rx, ry));
        }
        near.Sort((a, b) => a.d.CompareTo(b.d));
        Console.WriteLine($"\n[D] {near.Count} node(s) within 80px of cursor ({cur.X},{cur.Y}) — closest first:");
        foreach (var (d, el, rx, ry) in near.Take(8))
        {
            reader.TryReadStruct<uint>(el + 0x300, out var id32); reader.TryReadStruct<byte>(el + 0x32E, out var biome);
            reader.TryReadStruct<byte>(el + 0x32F, out var fl); reader.TryReadStruct<byte>(el + 0x339, out var comp);
            reader.TryReadStruct<float>(el + 0x288, out var sw); reader.TryReadStruct<float>(el + 0x28C, out var sh);
            Console.WriteLine($"  ── el 0x{el:X} dist {d:F0}px id={id32} biome={biome} flags=0x{fl:X2} compl={comp} size={sw:F0}x{sh:F0} relPos=({rx:F0},{ry:F0})");
            var vb = SafePtr(reader, el + 0x350); reader.TryReadStruct<nint>(el + 0x358, out var ve);
            var vlen = (vb != 0 && (long)ve > (long)vb) ? ((long)ve - (long)vb) : 0;
            if (vlen is > 0 and <= 4096) { var cnt = (int)Math.Min(16, vlen / 4); var u = new List<string>(); for (var i = 0; i < cnt; i++) { reader.TryReadStruct<uint>(vb + (nint)(i * 4), out var x); u.Add(x.ToString()); } Console.WriteLine($"       ContentVec({vlen}B): {string.Join(", ", u)}"); }
            // DEEP sniff of the closest element only (full struct 0..0x360) — find the "Deserted" name +
            // the link to the content layer. Shallow sniff for the rest.
            foreach (var s in SniffPointerStrings(reader, el, d < 6 ? 0x360 : 0xC0, d < 6 ? 24 : 6)) Console.WriteLine($"       self {s}");
            var cf = SafePtr(reader, el + 0x10); reader.TryReadStruct<nint>(el + 0x18, out var cl);
            var ccount = (cf != 0 && (long)cl > (long)cf) ? ((long)cl - (long)cf) / 8 : 0;
            for (long i = 0; i < ccount && i < 10; i++)
            {
                var ch = SafePtr(reader, cf + (nint)(i * 8)); if (ch == 0) continue;
                reader.TryReadStruct<float>(ch + 0x288, out var cw); reader.TryReadStruct<float>(ch + 0x118, out var crx); reader.TryReadStruct<float>(ch + 0x11C, out var cry);
                var strs = SniffPointerStrings(reader, ch);
                if (strs.Count > 0) Console.WriteLine($"       child[{i}] 0x{ch:X} w={cw:F0} relPos=({crx:F0},{cry:F0}): {string.Join("  ", strs)}");
            }
        }
    }
    Console.WriteLine("\nDONE. Re-run while hovering distinct known-modifier tiles to map content ids/strings → names.");
    return 0;
}

// ── Atlas hover watcher: validate the community hover-tracker chain + atlas-node fields ─────
// Hover chain (2026-06-07 notes): worldTracker = *(UiRoot+0x7D8) + 0x630; hovered = *(worldTracker+0x18).
// Polls it; on each change, dumps the hovered object — atlas-node fields if it looks like one, else its
// metadata path. Hover KNOWN maps (Marrow, a boss map, a visited vs unvisited node) to confirm id↔name,
// biome, the content/flags/completion semantics, and that it tracks atlas nodes at all.
static int RunAtlasWatch(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain."); return 1; }
    var uiRoot = SafePtr(reader, igs + 0x2F0);
    var htC = SafePtr(reader, uiRoot + 0x7D8);
    var hoverVtable = process.MainModuleBase + 0x2D707D8;
    Console.WriteLine($"UiRoot 0x{uiRoot:X}  htContainer 0x{htC:X}  hoverVtable 0x{hoverVtable:X}");

    // Find every hover-tracker instance embedded in the container (its first qword == the hover vtable).
    var trackers = new List<int>();
    var cbuf = new byte[0x2000];
    var cn = reader.TryReadBytes(htC, cbuf);
    for (var o = 0; o + 8 <= cn; o += 8)
        if ((nint)BitConverter.ToInt64(cbuf, o) == hoverVtable) trackers.Add(o);
    Console.WriteLine($"hover-tracker instances at htContainer offsets: {string.Join(", ", trackers.Select(t => $"+0x{t:X}"))}");
    Console.WriteLine("Hover atlas nodes / maps. Logs the tracker whose +0x18 changes. Ctrl+C to stop.\n");

    var prev = new Dictionary<int, nint>();
    while (true)
    {
        foreach (var t in trackers)
        {
            var hov = SafePtr(reader, htC + t + 0x18);
            if (prev.TryGetValue(t, out var old) && old == hov) continue;
            prev[t] = hov;
            if (hov == 0) continue;
            var isEl = SafePtr(reader, hov + 0x08) == hov;
            var meta = ReadEntityMetadata(reader, hov);
            reader.TryReadStruct<uint>(hov + 0x300, out var id);
            reader.TryReadStruct<uint>(hov + 0x310, out var content);
            reader.TryReadStruct<byte>(hov + 0x32C, out var state);
            reader.TryReadStruct<byte>(hov + 0x32E, out var biome);
            reader.TryReadStruct<byte>(hov + 0x32F, out var flags);
            reader.TryReadStruct<byte>(hov + 0x339, out var compl);
            reader.TryReadStruct<float>(hov + 0x118, out var px);
            reader.TryReadStruct<float>(hov + 0x11C, out var py);
            Console.WriteLine($"[tracker +0x{t:X}] hov=0x{hov:X} el={isEl} vtable=0x{SafePtr(reader, hov):X}");
            if (isEl) Console.WriteLine($"    node? id={id} content={content} state={state} biome={biome} flags=0x{flags:X2}(unlk={flags & 1} vis={(flags >> 1) & 1}) compl={compl} pos=({px:F0},{py:F0})");
            if (Printable(meta)) Console.WriteLine($"    meta='{meta}'");
        }
        Thread.Sleep(300);
    }
}

// ── Hover probe: find the game's "currently-hovered UiElement" pointer ──────────────────────
// The capture anchor for the UI explorer. We poll InGameState's fields for any slot holding a
// pointer to a self-referential UiElement (Self@+0x08 == self), and report which slot's target
// CHANGES as the user moves the cursor over different UI elements. The slot that tracks the hover in
// lockstep is the hovered-element pointer. Run it, then slowly move the cursor between distinct UI
// elements (atlas nodes, panel buttons, inventory slots) — watch which offset keeps changing.
static int RunHover(ProcessHandle process, MemoryReader reader)
{
    var (_, igs, _, _) = ResolveChain(process, reader);
    if (igs == 0) { Console.Error.WriteLine("no chain (in game?)."); return 1; }
    var uiRoot = SafePtr(reader, igs + Poe2.InGameState.UiRoot);
    Console.WriteLine($"InGameState 0x{igs:X}  UiRoot 0x{uiRoot:X}");
    Console.WriteLine("Watching pointer slots that change as you hover. Move the cursor SLOWLY over a few");
    Console.WriteLine("distinct UI elements / atlas nodes (pause ~2s each). The HOVERED pointer changes once");
    Console.WriteLine("per switch (low count); per-frame churn changes every poll (filtered out). Ctrl+C to stop.\n");

    bool IsUiEl(nint p) => p != 0 && SafePtr(reader, p + Poe2.UiElement.Self) == p;

    // Two scan windows: InGameState (wide) and the UiRoot object. Each slot keyed by (region,offset).
    var regions = new (string tag, nint baseAddr, int span)[] { ("IGS", igs, 0x8000), ("UiRoot", uiRoot, 0x1000) };
    var prev = new Dictionary<(int, int), nint>();
    var changeCount = new Dictionary<(int, int), int>();
    var polls = 0;
    var bufs = regions.Select(r => new byte[r.span]).ToArray();

    while (true)
    {
        polls++;
        for (var ri = 0; ri < regions.Length; ri++)
        {
            var (tag, baseAddr, span) = regions[ri];
            if (baseAddr == 0 || reader.TryReadBytes(baseAddr, bufs[ri]) != span) continue;
            for (var o = 0; o + 8 <= span; o += 8)
            {
                var p = (nint)BitConverter.ToInt64(bufs[ri], o);
                if ((ulong)p < 0x10000 || (ulong)p > 0x7FFFFFFFFFFF) continue;
                var key = (ri, o);
                if (prev.TryGetValue(key, out var old) && old != p)
                    changeCount[key] = changeCount.GetValueOrDefault(key) + 1;
                prev[key] = p;
            }
        }

        // Every few polls, report CANDIDATE slots: those that changed a few times (matching deliberate
        // hovers), NOT every poll (churn). Rank by "changed but not churning".
        if (polls % 5 == 0)
        {
            var cands = changeCount
                .Where(kv => kv.Value >= 1 && kv.Value <= polls / 2 + 1 && kv.Value <= 12)
                .OrderBy(kv => kv.Value).ToList();
            Console.WriteLine($"--- poll {polls}: {cands.Count} candidate slot(s) (changed 1..{polls / 2 + 1}x) ---");
            foreach (var kv in cands.Take(25))
            {
                var (ri, o) = kv.Key;
                var p = prev[kv.Key];
                Console.WriteLine($"  {regions[ri].tag}+0x{o:X4}  ={kv.Value}x  -> 0x{p:X}  ui={IsUiEl(p)}  meta='{ShortMeta(reader, p)}'  first8=0x{(reader.TryReadStruct<nint>(p, out var f) ? f : 0):X}");
            }
            Console.WriteLine();
        }
        Thread.Sleep(500);
    }
}

// Best-effort: an element's StringId text (if present at the GH2-analogous offset) or empty.
static string ShortMeta(MemoryReader reader, nint el)
{
    foreach (var off in new[] { 0x140, 0x148, 0x158, 0x160 })
    {
        var s = ReadStdWString(reader, el + off);
        if (Printable(s)) return s.Length > 24 ? s[..24] : s;
    }
    return "";
}

// ── Atlas UI walker: find the Atlas node subtree and decode the node UiElement ──
// The PoE2 Atlas is a tree/graph UI (cf. GH2 SkillTreeNodeUiElement = UiElementBase + a pointer to
// the node's dat row). Our PoE2 UiElement is validated only at Self@+0x08, Children@+0x10,
// Flags@+0x180; the StringId / Parent / Position / node-data-ptr offsets drifted from GH2 and are
// rediscovered here. BFS the UI tree from UiRoot (InGameState+0x2F0); find the element(s) carrying
// the hovered map name "<token>" inline (a short name is SSO-inline in a StdWString, so one cheap
// IndexOf over each element's body catches it without extra syscalls). For each match, print its
// ancestry + a byte dump + an interpretation pass (plausible screen-position floats, and pointer
// fields whose targets look like the map-type dat row) so the node-data offset can be pinned.
static int RunAtlasUi(ProcessHandle process, MemoryReader reader, string token)
{
    var (_, inGameState, _, _) = ResolveChain(process, reader);
    if (inGameState == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    var uiRoot = SafePtr(reader, inGameState + Poe2.InGameState.UiRoot);
    if (uiRoot == 0) { Console.Error.WriteLine($"UiRoot null (InGameState+0x{Poe2.InGameState.UiRoot:X})."); return 1; }
    Console.WriteLine($"InGameState 0x{inGameState:X}  UiRoot 0x{uiRoot:X}  token=\"{token}\"");

    var needle = System.Text.Encoding.Unicode.GetBytes(token);
    const int Window = 0x300;
    var queue = new Queue<nint>(); queue.Enqueue(uiRoot);
    var depthOf = new Dictionary<nint, int> { [uiRoot] = 0 };
    var parent = new Dictionary<nint, nint>();
    var visited = new HashSet<nint>();
    var matches = new List<(nint el, int depth, long children, int off)>();
    var containers = new List<(nint el, int depth, long children)>();
    var body = new byte[Window];
    int total = 0, visibleCount = 0, maxDepth = 0;

    while (queue.Count > 0 && visited.Count < 120000)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el)) continue;
        // Validate element-shape via the self-pointer (Self@+0x08 == el) to avoid chasing junk ptrs.
        if (SafePtr(reader, el + Poe2.UiElement.Self) != el) continue;
        total++;
        var depth = depthOf.GetValueOrDefault(el);
        if (depth > maxDepth) maxDepth = depth;

        var first = SafePtr(reader, el + Poe2.UiElement.Children);
        long childCount = 0;
        if (first != 0 && reader.TryReadStruct<nint>(el + Poe2.UiElement.Children + 8, out var last))
        {
            childCount = ((long)last - (long)first) / 8;
            if (childCount is > 0 and <= 16384)
                for (long k = 0; k < childCount; k++)
                {
                    var c = SafePtr(reader, first + (nint)(k * 8));
                    if (c != 0 && !depthOf.ContainsKey(c)) { depthOf[c] = depth + 1; parent[c] = el; }
                    queue.Enqueue(c);
                }
            if (childCount >= 30) containers.Add((el, depth, childCount));
        }

        var n = reader.TryReadBytes(el, body);
        if (n <= 0) continue;
        if (reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var fl) && ((fl >> Poe2.UiElement.FlagVisibleBit) & 1) != 0) visibleCount++;
        var idx = body.AsSpan(0, n).IndexOf(needle);
        if (idx >= 0) matches.Add((el, depth, childCount, idx));
    }

    Console.WriteLine($"\nUI tree: {total} elements, maxDepth {maxDepth}, {visibleCount} visible (own bit). {matches.Count} carry \"{token}\" inline.");

    // Largest-child-count containers — the Atlas node grid should stand out (hundreds of sibling
    // node elements). For each top container, sample a couple children and peek their pointer fields
    // for a target that reads as map-type text (the node-data ptr, cf. GH2 SkillInfo→dat row).
    Console.WriteLine("\n=== top containers by child count (candidate node grids) ===");
    foreach (var (el, depth, children) in containers.OrderByDescending(c => c.children).Take(20))
    {
        var vis = reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var fl) && ((fl >> Poe2.UiElement.FlagVisibleBit) & 1) != 0;
        Console.WriteLine($"  0x{el:X16}  depth={depth,2} children={children,5} visible={vis}");
    }

    Console.WriteLine("\n=== sampling children of the largest containers for map-type pointers ===");
    foreach (var (cont, cdepth, cchildren) in containers.OrderByDescending(c => c.children).Take(6))
    {
        var first = SafePtr(reader, cont + Poe2.UiElement.Children);
        if (first == 0) continue;
        // Sample children spread across the container (not just the first), and 2-hop deref each
        // pointer (element -> structPtr -> datRow) to catch the GH2 SkillInfo-style node-data chain.
        var sampleIdx = cchildren <= 4 ? Enumerable.Range(0, (int)cchildren).ToArray()
            : new[] { 0, (int)cchildren / 3, (int)(2 * cchildren / 3), (int)cchildren - 1 };
        Console.WriteLine($"\n  container 0x{cont:X} (children={cchildren}) — sampling children {string.Join(",", sampleIdx)}:");
        foreach (var k in sampleIdx)
        {
            var child = SafePtr(reader, first + (nint)((long)k * 8));
            if (child == 0) continue;
            var cbuf = new byte[0x300];
            var cn = reader.TryReadBytes(child, cbuf);
            var found = new List<string>();
            for (var o = 0; o + 8 <= cn; o += 8)
            {
                var p = (nint)BitConverter.ToInt64(cbuf, o);
                if ((ulong)p < 0x10000 || (ulong)p > 0x7FFFFFFFFFFF) continue;
                // hop 1: text directly off this pointer.
                var t = TryReadAnyText(reader, p);
                if (t != null) { found.Add($"+0x{o:X3}->0x{p:X} {t}"); continue; }
                // hop 2: through a small intermediate struct (cf. SkillInfo+0x08 -> dat row).
                foreach (var mid in new[] { 0x00, 0x08, 0x10, 0x18, 0x20, 0x288 })
                {
                    if (!reader.TryReadStruct<nint>(p + mid, out var p2)) continue;
                    if ((ulong)p2 < 0x10000 || (ulong)p2 > 0x7FFFFFFFFFFF) continue;
                    var t2 = TryReadAnyText(reader, p2);
                    if (t2 != null && (t2.Contains("Map", StringComparison.Ordinal) || t2.Contains("Steppe", StringComparison.Ordinal)))
                    { found.Add($"+0x{o:X3}->0x{p:X}+0x{mid:X}->0x{p2:X} {t2}"); break; }
                }
            }
            Console.WriteLine($"    child[{k}] 0x{child:X} (n={cn}): {(found.Count == 0 ? "(no map-type text)" : "")}");
            foreach (var f in found.Take(8)) Console.WriteLine($"      {f}");
        }
    }

    if (matches.Count == 0)
        Console.WriteLine("\n(No element holds the token inline — node names are pointer-stored / tooltip-composed.)");

    foreach (var (el, depth, children, off) in matches.Take(8))
    {
        Console.WriteLine($"\n===== element 0x{el:X16}  depth={depth} children={children}  token@+0x{off:X} =====");
        // Ancestry (climb the parent map to UiRoot).
        var chain = new List<nint>(); var cur = el; var guard = 0;
        while (cur != 0 && guard++ < 24) { chain.Add(cur); if (!parent.TryGetValue(cur, out var par) || par == cur) break; cur = par; }
        Console.WriteLine("  ancestry: " + string.Join(" -> ", chain.Select(a => $"0x{a:X}")));

        var buf = new byte[0x300];
        var n = reader.TryReadBytes(el, buf);
        // Interpretation pass over the element body.
        Console.WriteLine("  interpret (offset : value):");
        for (var o = 0; o + 4 <= n; o += 4)
        {
            // Plausible screen-position / size floats (UI coords).
            var f = BitConverter.ToSingle(buf, o);
            if (float.IsFinite(f) && f >= 1f && f <= 4000f && MathF.Abs(f - MathF.Round(f)) > 0f && (o % 4 == 0))
            {
                // Only surface paired (x,y) float runs to cut noise: this float and the next look like coords.
                if (o + 8 <= n)
                {
                    var f2 = BitConverter.ToSingle(buf, o + 4);
                    if (float.IsFinite(f2) && f2 >= 1f && f2 <= 4000f)
                        Console.WriteLine($"    +0x{o:X3}  floatpair ({f:F1}, {f2:F1})");
                }
            }
        }
        // Pointer fields: surface those whose target looks like a dat row (inline StdWString that reads
        // as text — e.g. the map-type "Steppe"/"MapSteppe" row) — that's the node-data pointer.
        Console.WriteLine("  pointer fields -> target peek:");
        for (var o = 0; o + 8 <= n; o += 8)
        {
            var p = (nint)BitConverter.ToInt64(buf, o);
            if ((ulong)p < 0x10000 || (ulong)p > 0x7FFFFFFFFFFF) continue;
            // Peek the target: a short UTF-16 string at the target, or at target+a few dat-row offsets.
            var s0 = reader.ReadStringUtf16(p, 32);
            var sName = TryReadAnyText(reader, p);
            if (Printable(s0) || sName != null)
                Console.WriteLine($"    +0x{o:X3} -> 0x{p:X}   {(Printable(s0) ? $"\"{s0}\"" : "")}{(sName != null ? $"   row-text=\"{sName}\"" : "")}");
        }
        DumpWindow(reader, el, 0x120, "    raw ");
    }
    Console.WriteLine("\nRead the matched element's ancestry to find the Atlas container (a parent with ~node-count children).");
    Console.WriteLine("The pointer field whose target carries the map-type text is the node-data ptr (cf. GH2 SkillInfo).");
    return 0;
}

// Probe a candidate dat-row pointer for human text: try a direct inline StdWString and a few common
// row offsets (the parsed map-type rows we saw store inline names at small offsets like +0x24/+0x3C).
static string? TryReadAnyText(MemoryReader reader, nint p)
{
    foreach (var off in new[] { 0x00, 0x08, 0x10, 0x18, 0x20, 0x24, 0x28, 0x30, 0x3C })
    {
        var s = reader.ReadStringUtf16(p + off, 32);
        if (Printable(s)) return $"+{off:X}:{s}";
    }
    return null;
}

// ── Discovery: large-map UI element + its visibility flag ───────────────────
// 1) Auto-detect UiRoot from InGameState (a pointer to a self-referential UiElement, which also
//    confirms the Self offset). 2) Auto-detect the children StdVector offset (a vector of
//    self-referential UiElements). 3) BFS the tree; identify the LargeMap by its DefaultShift
//    signature (0.0, -20.0). 4) Report its address, the visible-flag region, Zoom/Shift.
static int RunFindMap(ProcessHandle process, MemoryReader reader)
{
    var (_, inGameState, _, _) = ResolveChain(process, reader);
    if (inGameState == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }

    // 1+2) Find UiRoot: a self-referential UiElement whose children StdVector holds elements
    //      that are ALSO self-referential at the same offset. Try every self-ref candidate in
    //      InGameState and accept the first whose children validate (auto-detects Self + Children).
    int[] selfCandidates = { 0x30, 0x28, 0x38, 0x20, 0x18, 0x10, 0x08 };
    nint uiRoot = 0; var selfOff = -1; var childOff = -1; var rootField = -1;
    for (var o = 0; o < 0x1000 && uiRoot == 0; o += 8)
    {
        var p = SafePtr(reader, inGameState + o);
        if (p == 0) continue;
        foreach (var so in selfCandidates)
        {
            if (SafePtr(reader, p + so) != p) continue;
            // try to find a children vector under p whose first element self-refs at the same so
            for (var co = so + 8; co <= so + 0x60; co += 8)
            {
                var first = SafePtr(reader, p + co);
                if (first == 0) continue;
                if (!reader.TryReadStruct<nint>(p + co + 8, out var last)) continue;
                var n = ((long)last - (long)first) / 8;
                if (n < 1 || n > 8192) continue;
                var c0 = SafePtr(reader, first);
                if (c0 != 0 && SafePtr(reader, c0 + so) == c0)
                { uiRoot = p; selfOff = so; childOff = co; rootField = o; break; }
            }
            if (uiRoot != 0) break;
        }
    }
    if (uiRoot == 0) { Console.Error.WriteLine("No UiRoot (self-ref element with self-ref children) found in InGameState[0..0x1000]."); return 1; }
    Console.WriteLine($"UiRoot 0x{uiRoot:X16}  (InGameState+0x{rootField:X}, Self@+0x{selfOff:X}, Children@+0x{childOff:X})\n");

    // 3) BFS; collect elements carrying the DefaultShift (0,-20) signature, recording the
    //    offset it was found at and the element's child count. The large/mini map are outliers:
    //    a rare DefaultShift offset, with children (the map icons), and a real Zoom at +0x38.
    Console.WriteLine("Walking UI tree for map-element candidates (DefaultShift = (0,-20))...");
    var queue = new Queue<nint>(); queue.Enqueue(uiRoot);
    var visited = new HashSet<nint>();
    var parent = new Dictionary<nint, nint>();
    var hits = new List<(nint el, int dsOff, long children, float zoom)>();
    var body = new byte[0x400];
    while (queue.Count > 0 && visited.Count < 30000)
    {
        var el = queue.Dequeue();
        if (el == 0 || !visited.Add(el)) continue;

        var first0 = SafePtr(reader, el + childOff);
        long childCount = 0;
        if (first0 != 0 && reader.TryReadStruct<nint>(el + childOff + 8, out var last0))
        {
            childCount = ((long)last0 - (long)first0) / 8;
            if (childCount is > 0 and <= 8192)
                for (long k = 0; k < childCount; k++)
                {
                    var c = SafePtr(reader, first0 + (nint)(k * 8));
                    if (c != 0 && !parent.ContainsKey(c)) parent[c] = el;
                    queue.Enqueue(c);
                }
        }

        var n = reader.TryReadBytes(el, body);
        for (var i = 0x100; i + 8 <= n; i += 4)   // map fields are deep in the struct
        {
            if (BitConverter.ToSingle(body, i) != 0f || BitConverter.ToSingle(body, i + 4) != -20f) continue;
            var zoom = i + 0x3C <= n ? BitConverter.ToSingle(body, i + 0x38) : 0f; // GH2: Zoom = DefaultShift+0x38
            hits.Add((el, i, childCount, zoom));
            break;
        }
    }

    // The real map elements have a non-default zoom (0.5 live). Print their ancestry + a flag
    // fingerprint per ancestor, so the parent that toggles visibility can be diffed open/closed.
    foreach (var h in hits.Where(h => h.zoom is > 0.05f and < 4f && MathF.Abs(h.zoom - 1f) > 0.01f))
    {
        Console.WriteLine($"\nMAP element 0x{h.el:X16} (DefaultShift@+0x{h.dsOff:X}, Zoom={h.zoom:F3}) ancestry:");
        var cur = h.el; var depth = 0;
        while (cur != 0 && depth++ < 14)
        {
            reader.TryReadStruct<uint>(cur + 0x88, out var f88);
            reader.TryReadStruct<uint>(cur + 0xA8, out var fA8);
            reader.TryReadStruct<uint>(cur + 0x180, out var f180); // ← current validated Flags offset
            reader.TryReadStruct<uint>(cur + 0x190, out var f190);
            reader.TryReadStruct<uint>(cur + 0x1B8, out var f1B8);
            Console.WriteLine($"  0x{cur:X16}  [+0x180]={f180:X8} (bit0x0B={((f180 >> 0x0B) & 1)})  [+0x88]={f88:X8} [+0xA8]={fA8:X8} [+0x190]={f190:X8} [+0x1B8]={f1B8:X8}");
            if (!parent.TryGetValue(cur, out var par) || par == cur) break;
            cur = par;
        }
    }

    // Group by DefaultShift offset; rare offsets with children + plausible zoom are the map.
    Console.WriteLine($"\n{hits.Count} (0,-20) elements. Grouped by DefaultShift offset:");
    foreach (var g in hits.GroupBy(h => h.dsOff).OrderBy(g => g.Count()))
    {
        Console.WriteLine($"  DefaultShift@+0x{g.Key:X}: {g.Count()} element(s)");
        if (g.Count() <= 4) // likely the map (large+mini) — show details
            foreach (var h in g)
                Console.WriteLine($"      0x{h.el:X16}  children={h.children}  Zoom@+0x{h.dsOff + 0x38:X}={h.zoom:F3}");
    }
    Console.WriteLine("\nThe large map = a rare-offset element with children and a sensible Zoom.");
    Console.WriteLine("Confirm by toggling the map and re-running: the count/visibility of that group changes.");
    return 0;
}

// ── PoE2 top-level chain resolver ───────────────────────────────────────────
// AOB "Game States" → GameState → CurrentStatePtr StdVector @+0x08; its first element is the
// active InGameState. InGameState+0x290 → AreaInstance. AreaInstance+0x5A0 → LocalPlayer.
// Validated live (resolved LocalPlayer == the value-scanned player entity). Falls back to
// scanning the 12 States[] slots if the current-state vector doesn't validate.
static (nint gameState, nint inGameState, nint areaInstance, nint localPlayer) ResolveChain(
    ProcessHandle process, MemoryReader reader)
{
    foreach (var pattern in AobPatterns.GameStateRefs)
    foreach (var slot in AobScanner.ScanForResolvedAddresses(process, reader, pattern).Distinct())
    {
        var gameState = SafePtr(reader, slot);
        if (gameState == 0) continue;

        var candidates = new List<nint>();
        var vecFirst = SafePtr(reader, gameState + Poe2.GameState.CurrentStatePtr);
        if (vecFirst != 0) candidates.Add(SafePtr(reader, vecFirst));
        for (var i = 0; i < Poe2.GameState.StateSlotCount; i++)
            candidates.Add(SafePtr(reader, gameState + Poe2.GameState.States + (nint)(i * Poe2.GameState.StateSlotStride)));

        foreach (var inGameState in candidates)
        {
            if (inGameState == 0) continue;
            var areaInstance = SafePtr(reader, inGameState + Poe2.InGameState.AreaInstanceData);
            if (areaInstance == 0) continue;
            var localPlayer = SafePtr(reader, areaInstance + Poe2.AreaInstance.LocalPlayer);
            if (localPlayer == 0) continue;
            if (!ReadEntityMetadata(reader, localPlayer).StartsWith("Metadata/", StringComparison.Ordinal)) continue;
            return (gameState, inGameState, areaInstance, localPlayer);
        }
    }
    return (0, 0, 0, 0);
}

static int RunChainProbe(ProcessHandle process, MemoryReader reader)
{
    var (gameState, inGameState, areaInstance, localPlayer) = ResolveChain(process, reader);
    if (areaInstance == 0) { Console.Error.WriteLine("Could not resolve in-game chain (are you in game?)."); return 1; }
    Console.WriteLine($"GameState    : 0x{gameState:X16}");
    Console.WriteLine($"InGameState  : 0x{inGameState:X16}");
    Console.WriteLine($"AreaInstance : 0x{areaInstance:X16}");
    Console.WriteLine($"LocalPlayer  : 0x{localPlayer:X16}  ({ReadEntityMetadata(reader, localPlayer)})");
    return 0;
}

// ── Vitals: dump the local player's Life component for per-patch re-validation ──
// Resolves the Life component, prints what the CONFIGURED Health/Mana/EnergyShield offsets read,
// then scans the whole component for EVERY valid-looking VitalStruct (offset-ascending). Run it in
// game with known HP/Mana/ES to (a) confirm the table offsets still land on the right pools after a
// patch, and (b) see the "decoy" structs between the real pools — the reason ordinal "Nth pool"
// guessing is unsafe and self-heal must anchor near each known offset.
static int RunVitals(ProcessHandle process, MemoryReader reader)
{
    var (_, _, _, lp) = ResolveChain(process, reader);
    if (lp == 0) { Console.Error.WriteLine("Could not resolve LocalPlayer (in game?)."); return 1; }
    var life = ResolveComponentAddr(reader, lp, "Life");
    if (life == 0) { Console.Error.WriteLine("Could not resolve Life component."); return 1; }
    Console.WriteLine($"LocalPlayer 0x{lp:X}  Life 0x{life:X}");

    void Show(string label, int off)
    {
        var ok = reader.TryReadStruct<VitalStruct>(life + off, out var v);
        var valid = ok && v.LooksValid();
        Console.WriteLine($"  configured {label,-12} @0x{off:X3} -> {(ok ? $"{v.Current}/{v.Max} reservedFlat={v.ReservedFlat} reservedFrac={v.ReservedFraction} regen={v.Regen:F2}" : "<unreadable>")}  {(valid ? "VALID" : "invalid")}");
    }
    Console.WriteLine("Configured table offsets (Poe2.Life):");
    Show("Health", Poe2.Life.Health);
    Show("Mana", Poe2.Life.Mana);
    Show("EnergyShield", Poe2.Life.EnergyShield);

    Console.WriteLine("All valid VitalStructs in the component (offset-ascending — 1st=Health, then decoys/Mana/ES):");
    for (var off = 0x80; off <= 0x400;)
    {
        if (reader.TryReadStruct<VitalStruct>(life + off, out var v) && v.LooksValid())
        {
            var tag = off == Poe2.Life.Health ? " <- Health" : off == Poe2.Life.Mana ? " <- Mana"
                : off == Poe2.Life.EnergyShield ? " <- EnergyShield" : "";
            Console.WriteLine($"  @0x{off:X3}  {v.Current,7}/{v.Max,-7} reservedFlat={v.ReservedFlat,-5} reservedFrac={v.ReservedFraction,-5} regen={v.Regen,8:F2}{tag}");
            off += 0x34; // skip past this struct's extent so the overlapping +4 alias isn't double-counted
        }
        else off += 4;
    }
    return 0;
}

// ── Discovery: entity-list StdMap offset within AreaInstance ────────────────
// Scans [AreaInstance, +scan) for {ptr Head, int Size} pairs that validate as a std::map of
// entities: Head is a heap ptr whose Parent (root) leads to a node whose value is an Entity
// (metadata starts with "Metadata/"). Reports the offset(s) — these are AwakeEntities/Sleeping.
static int RunFindEntities(ProcessHandle process, MemoryReader reader, int scan)
{
    var (_, _, areaInstance, _) = ResolveChain(process, reader);
    if (areaInstance == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    Console.WriteLine($"AreaInstance 0x{areaInstance:X16} — scanning +0x0..+0x{scan:X} for entity std::maps...");

    var found = 0;
    for (var o = 0; o + 0x10 <= scan; o += 8)
    {
        var head = SafePtr(reader, areaInstance + o);
        if (head == 0) continue;
        if (!reader.TryReadStruct<int>(areaInstance + o + 8, out var size)) continue;
        if (size <= 0 || size > 100000) continue;

        var root = SafePtr(reader, head + Poe2.StdMapNode.Parent);
        if (root == 0) continue;
        // root node should be non-nil; its value should be an entity.
        if (!reader.TryReadStruct<byte>(root + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
        var entityPtr = SafePtr(reader, root + Poe2.StdMapNode.ValueEntityPtr);
        var meta = ReadEntityMetadata(reader, entityPtr);
        if (!meta.StartsWith("Metadata/", StringComparison.Ordinal)) continue;

        found++;
        Console.WriteLine($"\n  +0x{o:X}: std::map size={size} head=0x{head:X16}  (root entity: {meta})");
        WalkEntityMap(reader, head, size);
    }
    if (found == 0) Console.WriteLine("  no entity std::map found in range — widen --window.");
    return 0;
}

// ── Discovery: terrain StdVectors within AreaInstance ───────────────────────
// Lists StdVector-looking triples {First,Last,End} with First≤Last≤End (heap), reporting byte
// count + a guess. The walkable grid is a big byte vector (≈ rows × bytesPerRow); an int right
// after a big vector is a BytesPerRow candidate. Helps locate the TerrainStruct.
static int RunFindTerrain(ProcessHandle process, MemoryReader reader, int scan)
{
    var (_, _, areaInstance, _) = ResolveChain(process, reader);
    if (areaInstance == 0) { Console.Error.WriteLine("Could not resolve chain (in game?)."); return 1; }
    Console.WriteLine($"AreaInstance 0x{areaInstance:X16} — scanning +0x0..+0x{scan:X} for StdVectors...");

    for (var o = 0; o + 24 <= scan; o += 8)
    {
        var first = SafePtr(reader, areaInstance + o);
        if (first == 0) continue;
        if (!reader.TryReadStruct<nint>(areaInstance + o + 8, out var last)) continue;
        if (!reader.TryReadStruct<nint>(areaInstance + o + 16, out var end)) continue;
        var u = (ulong)last;
        if (u < 0x10000 || u > 0x7FFFFFFFFFFF) continue;
        if ((long)last < (long)first || (long)end < (long)last) continue;
        var bytes = (long)last - (long)first;
        if (bytes < 0x200 || bytes > 0x4000000) continue;       // big-ish allocations only
        reader.TryReadStruct<int>(areaInstance + o + 24, out var trailingInt); // BytesPerRow candidate
        Console.WriteLine($"  +0x{o:X4}: vec first=0x{first:X12} bytes={bytes} (0x{bytes:X})  nextInt={trailingInt}");
    }
    Console.WriteLine("Look for a large byte vector whose size ≈ gridRows × bytesPerRow (nextInt≈row stride).");
    return 0;
}

// BFS over the MSVC std::map red-black tree. Node: Left@0, Parent@8, Right@0x10, IsNil@0x19;
// Data@0x20 = key{uint id}, value{IntPtr EntityPtr}@0x28. Leaf children point at the nil sentinel.
static void WalkEntityMap(MemoryReader reader, nint head, int size)
{
    if (head == 0 || size <= 0 || size > 200000) return;
    var root = SafePtr(reader, head + Poe2.StdMapNode.Parent);
    var queue = new Queue<nint>();
    queue.Enqueue(root);
    var seen = 0; var printed = 0; var visited = new HashSet<nint>();
    while (queue.Count > 0 && seen < size + 8 && visited.Count < 300000)
    {
        var node = queue.Dequeue();
        if (node == 0 || node == head || !visited.Add(node)) continue;
        if (!reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var isNil) || isNil != 0) continue;
        seen++;

        reader.TryReadStruct<uint>(node + Poe2.StdMapNode.KeyId, out var id);
        var entityPtr = SafePtr(reader, node + Poe2.StdMapNode.ValueEntityPtr);
        if (printed < 14 && entityPtr != 0 && id < Poe2.EntityList.VisualIdThreshold)
        {
            var meta = ReadEntityMetadata(reader, entityPtr);
            if (meta.Length > 0)
            {
                Console.WriteLine($"      id {id,-10} 0x{entityPtr:X16}  {meta}");
                printed++;
            }
        }
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Left));
        queue.Enqueue(SafePtr(reader, node + Poe2.StdMapNode.Right));
    }
    Console.WriteLine($"      … walked {seen} non-nil nodes (printed first {printed} real entities).");
}

// Safe pointer read — returns 0 on any failure (never throws). Also rejects obviously-bad
// pointers (non-canonical / low addresses) so garbage from a wrong chain branch can't propagate.
static nint SafePtr(MemoryReader reader, nint addr)
{
    if (!reader.TryReadStruct<nint>(addr, out var p)) return 0;
    var u = (ulong)p;
    if (u < 0x10000 || u > 0x7FFFFFFFFFFF) return 0; // user-mode heap range sanity
    return p;
}

// Resolve an entity's metadata path via EntityDetails (ptr @ +0x08) → name StdWString @ +0x08.
static string ReadEntityMetadata(MemoryReader reader, nint entity)
{
    if (entity == 0) return "";
    var detailsPtr = SafePtr(reader, entity + Poe2.Entity.EntityDetailsPtr);
    if (detailsPtr == 0) return "";
    return ReadStdWString(reader, detailsPtr + Poe2.EntityDetails.Name);
}

// Read a PoE/MSVC std::wstring (SSO): Length (chars) at +0x10; inline UTF-16 at base when
// Length < 8, otherwise Buffer (at +0x00) is a pointer to the chars.
static string ReadStdWString(MemoryReader reader, nint addr)
{
    if (!reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return "";
    if (len < 8) return reader.ReadStringUtf16(addr, len);
    var ptr = SafePtr(reader, addr);
    return ptr == 0 ? "" : reader.ReadStringUtf16(ptr, len);
}

static int RunValueScan(MemoryReader reader, int hp, int? mana)
{
    Console.WriteLine($"Value-scanning for LifeComponent (hp={hp}{(mana.HasValue ? $", mana={mana}" : "")})...");
    var matches = LifeValidator.FindCandidates(reader, hp, mana,
        onProgress: p =>
        {
            if (p.RegionsScanned % 20 == 0 || p.RegionsScanned == p.TotalRegions)
                Console.Write($"\r  {p.RegionsScanned}/{p.TotalRegions} regions  {p.BytesScanned / 1024 / 1024} MB  {p.CandidatesFound} hit(s)   ");
        });
    Console.WriteLine();

    if (matches.Count == 0)
    {
        Console.Error.WriteLine("No match. HP must equal the current value at scan time; stand still in town.");
        return 1;
    }

    Console.WriteLine($"{matches.Count} candidate Life component(s):");
    foreach (var m in matches)
        Console.WriteLine($"  Life @ 0x{m.LifeComponentAddress:X16}  owner(entity) @ 0x{m.OwnerAddress:X16}");
    Console.WriteLine("Use --entity <owner> to walk the entity, or --chain to resolve roots via AOB.");
    return 0;
}

static int RunDump(MemoryReader reader, nint addr, int len)
{
    Console.WriteLine($"Dumping 0x{len:X} bytes @ 0x{addr:X16}:");
    var buf = new byte[len];
    if (reader.TryReadBytes(addr, buf) != len)
    {
        Console.Error.WriteLine("Read failed (or partial).");
        return 1;
    }
    for (var i = 0; i < len; i += 16)
    {
        var n = Math.Min(16, len - i);
        var hex = string.Join(' ', Enumerable.Range(0, n).Select(j => buf[i + j].ToString("X2")));
        Console.WriteLine($"  +0x{i:X3}  {hex}");
    }
    return 0;
}

static int RunAobScan(ProcessHandle process, MemoryReader reader)
{
    if (AobPatterns.IngameStateRefs.Length == 0)
    {
        Console.Error.WriteLine("No AOB patterns committed yet (AobPatterns.IngameStateRefs is empty).");
        Console.Error.WriteLine("Discover a PoE2 IngameState pattern first, then add it to AobPatterns.cs.");
        return 1;
    }
    foreach (var pattern in AobPatterns.IngameStateRefs)
    {
        Console.WriteLine($"Scanning pattern: {pattern}");
        var slots = AobScanner.ScanForResolvedAddresses(process, reader, pattern);
        foreach (var slot in slots)
            Console.WriteLine($"  slot @ 0x{slot:X16}  -> 0x{(reader.TryReadStruct<nint>(slot, out var v) ? v : 0):X16}");
    }
    return 0;
}

static int RunGenWeights(string? metaPath, string? outPath)
{
    metaPath ??= "resources/poe2-data/tincture-meta-detail.json";
    outPath ??= "src/POE2Radar.Core/Game/starter_stat_weights.json";
    if (!System.IO.File.Exists(metaPath)) { Console.Error.WriteLine($"meta snapshot not found: {metaPath}"); return 1; }

    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(metaPath));
    if (!doc.RootElement.TryGetProperty("global", out var global) || !global.TryGetProperty("gear", out var gear))
    { Console.Error.WriteLine("meta-detail.json has no global.gear array."); return 1; }

    var byStatId = new Dictionary<string, double>(StringComparer.Ordinal);
    var normById = new Dictionary<string, double>(StringComparer.Ordinal);
    int matched = 0, total = 0;
    var unmatched = new List<string>();

    foreach (var g in gear.EnumerateArray())
    {
        total++;
        var name = g.GetProperty("name").GetString() ?? "";
        var pct = g.TryGetProperty("pct", out var p) && p.TryGetDouble(out var pv) ? pv : 0;
        double lo = g.TryGetProperty("lo", out var le) && le.TryGetDouble(out var lov) ? lov : 0;
        double hi = g.TryGetProperty("hi", out var he) && he.TryGetDouble(out var hiv) ? hiv : 0;
        var ids = POE2Radar.Core.Game.ItemModTranslator.Shared.StatIdsForRenderedLine(name);
        if (ids == null || ids.Length == 0) { unmatched.Add(name); continue; }
        matched++;
        var norm = (lo > 0 || hi > 0) ? (lo + hi) / 2.0 : 1.0;
        foreach (var id in ids)
        {
            if (pct > byStatId.GetValueOrDefault(id)) byStatId[id] = Math.Round(pct, 2);   // strongest meta signal wins
            if (norm > 0) normById[id] = Math.Round(norm, 2);
        }
    }

    var model = new { byStatId, normById, target = 100.0, godRollThreshold = 85.0 };
    var json = System.Text.Json.JsonSerializer.Serialize(model,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    System.IO.File.WriteAllText(outPath, json);

    Console.WriteLine($"Generated {outPath}: {byStatId.Count} stat ids from {matched}/{total} meta gear lines.");
    if (unmatched.Count > 0) Console.WriteLine("Unmatched (left to hand-weight): " + string.Join(" | ", unmatched));
    return 0;
}

static bool HasFlag(string[] args, string flag) => Array.IndexOf(args, flag) >= 0;

static string? TryGetStrArg(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static int? TryGetIntArg(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    if (idx < 0 || idx + 1 >= args.Length) return null;
    return int.TryParse(args[idx + 1], out var v) ? v : null;
}

static nint? TryGetHexArg(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    if (idx < 0 || idx + 1 >= args.Length) return null;
    var s = args[idx + 1];
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
    return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? (nint)v : null;
}

static class Win
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetClientRect(nint h, out RECT r);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ScreenToClient(nint h, ref POINT p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int n);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, nint lparam);
    public delegate bool EnumWindowsProc(nint h, nint lparam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint h, out uint pid);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint h);

    // The largest visible top-level window owned by `pid` (the game's main window, even when not focused).
    public static nint FindMainWindowForPid(uint pid)
    {
        nint best = 0; long bestArea = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var wp);
            if (wp != pid) return true;
            if (GetClientRect(h, out var r))
            {
                long area = (long)r.right * r.bottom;
                if (area > bestArea) { bestArea = area; best = h; }
            }
            return true;
        }, 0);
        return best;
    }
}

readonly record struct VecLayout(int VecOff, int ElemSize, int SlotA, int SlotB);
