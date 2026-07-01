# Preload Alert (v0.14.0) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Opt-in, default-off "Preload Alert" — on zone entry, read the game's loaded-files list, match it against a curated path→content catalog, and surface this zone's notable content (bosses / league mechanics / valuable chests) as an overlay panel + optional audio, robust to always-loaded noise via a zone-frequency filter.

**Architecture:** Core gains a validated loaded-files reader (`Poe2LoadedFiles`), a content catalog (`PreloadCatalog` + embedded JSON), and a pure zone-frequency filter (`PreloadTracker`). The Overlay world loop scans once per zone, filters, and publishes a `PreloadFrame`; the render thread draws a panel. All read-only.

**Tech Stack:** C#/.NET 10 (x64), Vortice.Direct2D1, xUnit (Core-only tests), embedded JSON, vanilla-JS dashboard in a C# raw-string.

## Global Constraints

- **Strictly read-only:** `ReadProcessMemory` + AOB scan of exec sections only. NO `WriteProcessMemory`/`VirtualProtectEx`/`CreateRemoteThread`, NO `SendInput`/`PostMessage`/`keybd_event`, NO pricing/poe.ninja/reward-VALUES. Content *types* only.
- **Default OFF, opt-in, experimental.** Loopback-gated config writes.
- `TreatWarningsAsErrors=true` → 0/0. README badge stays `0.5.4`. Version → `0.14.0`.
- Compliance gate + scrub stay green. The `POE2Radar.Research` `--preload` probe already on-branch is dev-only (not shipped) — leave it.
- **Validated reader constants (from `--preload`, 2026-06-30):** FileRoot AOB already in `AobPatterns.FileRootRefs`. Buckets=16, bucketStride=0x38, nodeStride=0x18, FilesPointer=+0x08, Name(StdWString)=+0x08, AreaChangeCount=+0x40. Paths split on `'@'`, compared lowercase.

### Build & test
- Core build: `dotnet build src/POE2Radar.Core/POE2Radar.Core.csproj -c Release` → 0/0.
- Overlay build: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release` → 0/0.
- Tests: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Release -p:Platform=x64` → all pass.

---

## File structure

| File | Action | Task |
|---|---|---|
| `src/POE2Radar.Core/Game/Poe2Offsets.cs` | add `LoadedFiles` offset block | 1 |
| `src/POE2Radar.Core/Game/Poe2LoadedFiles.cs` | new — reader | 1 |
| `src/POE2Radar.Core/Game/PreloadCatalog.cs` | new — catalog loader + Match | 2 |
| `src/POE2Radar.Core/Game/preload_catalog.json` | new — embedded catalog | 2 |
| `src/POE2Radar.Core/POE2Radar.Core.csproj` | embed catalog json | 2 |
| `src/POE2Radar.Core/Game/PreloadTracker.cs` | new — frequency filter (pure) | 3 |
| `tests/POE2Radar.Tests/PreloadCatalogTests.cs` | new | 2 |
| `tests/POE2Radar.Tests/PreloadTrackerTests.cs` | new | 3 |
| `src/POE2Radar.Overlay/Config/RadarSettings.cs` | `PreloadAlertSettings` | 4 |
| `src/POE2Radar.Overlay/RadarApp.cs` | world-loop scan + publish `PreloadFrame` | 4 |
| `src/POE2Radar.Overlay/Overlay/RenderContext.cs` | `PreloadHit` + ctx field | 4 |
| `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` | `DrawPreloadPanel` + audio | 5 |
| `src/POE2Radar.Overlay/Web/ApiServer.cs` | `/state` preload + patch + diagnostic | 6 |
| `src/POE2Radar.Overlay/Web/DashboardHtml.cs` | settings card + diagnostic | 6 |
| `CHANGELOG.md`, `README.md`, `*.csproj` | release | 7 |

---

## Task 1: Core loaded-files reader

**Files:** `Poe2Offsets.cs` (modify), `Poe2LoadedFiles.cs` (create).

**Produces:** `Poe2LoadedFiles` with `bool TryResolveRoot()` and `IReadOnlySet<string> ScanLoadedPaths()` (lowercased, '@'-split). Consumed by Task 4.

- [ ] **Step 1: Offsets.** In `Poe2Offsets.cs`, add near the other structs:
```csharp
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
```
- [ ] **Step 2: Reader.** Create `src/POE2Radar.Core/Game/Poe2LoadedFiles.cs` (namespace `POE2Radar.Core.Game`). Model it on the Research `RunPreload`/`TryWalkFileRootBuckets` logic (that's the validated reference) but as a reusable class taking a `ProcessHandle` + `MemoryReader`:
```csharp
public sealed class Poe2LoadedFiles
{
    private readonly ProcessHandle _proc;
    private readonly MemoryReader _reader;
    private nint _rootSlot;   // cached resolved global slot (0 = unresolved)

    public Poe2LoadedFiles(ProcessHandle proc, MemoryReader reader) { _proc = proc; _reader = reader; }

    /// <summary>Resolve + cache the FileRoot global slot via the AOB (once). Returns false if not found.</summary>
    public bool TryResolveRoot()
    {
        if (_rootSlot != 0) return true;
        foreach (var pat in AobPatterns.FileRootRefs)
        {
            var slots = AobScanner.ScanForResolvedAddresses(_proc, _reader, pat);
            if (slots.Count > 0) { _rootSlot = slots[0]; return true; }
        }
        return false;
    }

    /// <summary>Walk all 16 buckets → the set of currently-loaded asset paths (lowercased, '@'-split).
    /// HEAVY (~20k reads) — call ONCE per zone, off the render thread.</summary>
    public IReadOnlySet<string> ScanLoadedPaths()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!TryResolveRoot()) return set;
        if (!_reader.TryReadStruct<nint>(_rootSlot, out var fileRoot) || fileRoot == 0) return set;
        for (var bi = 0; bi < Poe2.LoadedFiles.BucketCount; bi++)
        {
            var bucket = fileRoot + (nint)(bi * Poe2.LoadedFiles.BucketStride);
            if (!_reader.TryReadStruct<nint>(bucket, out var first)) continue;
            if (!_reader.TryReadStruct<nint>(bucket + 0x08, out var last)) continue;
            var fu = (ulong)first; var lu = (ulong)last;
            if (fu < 0x10000 || fu > 0x7FFFFFFFFFFF || lu < fu) continue;
            var range = (long)(lu - fu);
            if (range <= 0 || range > 16L * 1024 * 1024) continue;
            var count = range / Poe2.LoadedFiles.NodeStride;
            for (long i = 0; i < count; i++)
            {
                var node = first + (nint)(i * Poe2.LoadedFiles.NodeStride);
                if (!_reader.TryReadStruct<nint>(node + Poe2.LoadedFiles.FilesPointer, out var fp) || fp == 0) continue;
                var name = ReadStdWStringLocal(fp + Poe2.LoadedFiles.NameStr);
                if (name.Length < 4 || !(name.Contains('/') || name.Contains('.'))) continue;
                var p = name.Split('@')[0].ToLowerInvariant();
                set.Add(p);
            }
        }
        return set;
    }

    private string ReadStdWStringLocal(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return string.Empty;
        if (len < 8) return _reader.ReadStringUtf16(addr, len);
        if (!_reader.TryReadStruct<nint>(addr, out var ptr) || ptr == 0) return string.Empty;
        return _reader.ReadStringUtf16(ptr, len);
    }
}
```
Use the `Poe2Offsets` alias the file already uses (`using Poe2 = ...` or the project's convention — match `Poe2Live.cs`). Confirm `AobScanner.ScanForResolvedAddresses`, `MemoryReader.TryReadStruct`/`ReadStringUtf16`, `ProcessHandle` signatures against the real files.
- [ ] **Step 3: Build Core** → 0/0. (No unit test here — the walk needs a live process; the pure catalog/tracker logic is tested in Tasks 2–3. The reader mirrors the already-live-validated probe.)
- [ ] **Step 4: Commit** — `git add` both files; `git commit -m "feat(core): Poe2LoadedFiles reader + LoadedFiles offsets (validated FileRoot walk)"`.

---

## Task 2: Content catalog

**Files:** `PreloadCatalog.cs` (create), `preload_catalog.json` (create), `POE2Radar.Core.csproj` (embed), `tests/POE2Radar.Tests/PreloadCatalogTests.cs` (create).

**Produces:** `PreloadCatalog.Shared` with `CatalogHit? Match(string lowerPath)` and `record CatalogHit(string Label, string Tier, string Category, string Color, string Match)`. Consumed by Tasks 3/4.

- [ ] **Step 1: Catalog JSON.** Create `src/POE2Radar.Core/Game/preload_catalog.json` from the draft (`scratchpad/preload_catalog_draft.json`, 69 rules) PLUS these probe-confirmed real paths (add as rules): `metadata/chests/blightchests/` → "Blight Chest" (interactable, cyan); `metadata/chests/incursionatzirileadupchests` → "Atziri Lead-up (Incursion)" (high, orange); `metadata/chests/leagueheist/` → "Heist Chest" (interactable); `metadata/chests/delvechests/` → "Delve Fossil Chest" (interactable); `metadata/monsters/leagueexpeditionnew/expedition2/` → "Expedition Monster" (mechanic, yellow). Shape:
```json
{ "version": 1, "rules": [ { "match": "metadata/monsters/breach/breachoverseerboss", "label": "Xesht — Breachlord", "tier": "pinnacle", "category": "Breach", "color": "#c060ff" } ], "gateRoots": ["metadata/monsters/", "metadata/chests/", "metadata/terrain/leagues/", "metadata/miscellaneousobjects/league"] }
```
Include a `gateRoots` array = the entity-metadata prefixes that pass the noise gate.
- [ ] **Step 2: Embed** — in `POE2Radar.Core.csproj`, add `<EmbeddedResource Include="Game\preload_catalog.json" />` to the existing Game\*.json ItemGroup.
- [ ] **Step 3: Failing test.** `tests/POE2Radar.Tests/PreloadCatalogTests.cs`:
```csharp
using POE2Radar.Core.Game;
public class PreloadCatalogTests
{
    [Fact] public void Loads_rules() => Assert.True(PreloadCatalog.Shared.RuleCount > 20);
    [Fact] public void Matches_known_boss()
    {
        var hit = PreloadCatalog.Shared.Match("metadata/monsters/breach/breachoverseerboss/itthatreturned");
        Assert.NotNull(hit); Assert.Equal("pinnacle", hit!.Tier);
    }
    [Fact] public void Noise_path_is_gated_out()
        => Assert.Null(PreloadCatalog.Shared.Match("art/models/monsters/honeyant/rig.amd")); // art/ not a gateRoot
    [Fact] public void Unknown_content_path_returns_null()
        => Assert.Null(PreloadCatalog.Shared.Match("metadata/monsters/nothingspecial/whatever"));
}
```
- [ ] **Step 4: Run red** (RuleCount/Match not defined) → build fails / test red.
- [ ] **Step 5: Implement `PreloadCatalog`.** `src/POE2Radar.Core/Game/PreloadCatalog.cs`: `Shared` singleton loads the embedded JSON (mirror `AtlasMapData.OpenResource` substring-marker pattern). `int RuleCount`. `CatalogHit? Match(string lowerPath)`: return null unless `lowerPath` startswith one of `gateRoots`; else return the first rule whose `match` is a substring of `lowerPath` (rules ordered pinnacle→high→mechanic→interactable so the strongest wins — sort on load by tier rank). Null-safe, never throws.
- [ ] **Step 6: Green** — build + `dotnet test` pass.
- [ ] **Step 7: Commit** — `feat(core): PreloadCatalog + embedded preload_catalog.json (entity-metadata gated)`.

---

## Task 3: Zone-frequency filter (pure)

**Files:** `PreloadTracker.cs` (create), `tests/POE2Radar.Tests/PreloadTrackerTests.cs` (create).

**Produces:** `PreloadTracker` — pure, testable, persistable. Consumed by Task 4.

- [ ] **Step 1: Failing tests.** `tests/POE2Radar.Tests/PreloadTrackerTests.cs`:
```csharp
using POE2Radar.Core.Game;
public class PreloadTrackerTests
{
    [Fact] public void Rare_path_alerts_after_warmup()
    {
        var t = new PreloadTracker(warmupZones: 4, commonThreshold: 0.6);
        for (int z = 0; z < 4; z++) t.ObserveZone(new[] { "common/a" });         // saturate a common path
        var res = t.ObserveZone(new[] { "common/a", "rare/boss" });               // zone 5: rare appears once
        Assert.Contains("rare/boss", res.Alerts);
        Assert.DoesNotContain("common/a", res.Alerts);   // common/a in 5/5 zones → suppressed
    }
    [Fact] public void During_warmup_everything_alerts()
    {
        var t = new PreloadTracker(warmupZones: 4, commonThreshold: 0.6);
        var res = t.ObserveZone(new[] { "x/y" });
        Assert.Contains("x/y", res.Alerts);              // no data yet → don't suppress
    }
    [Fact] public void Frequencies_exposed_for_diagnostic()
    {
        var t = new PreloadTracker(4, 0.6);
        t.ObserveZone(new[] { "p" }); t.ObserveZone(new[] { "p" });
        Assert.Equal(2, t.Snapshot()["p"].hits);
        Assert.Equal(2, t.ZonesObserved);
    }
}
```
- [ ] **Step 2: Run red.**
- [ ] **Step 3: Implement.** `src/POE2Radar.Core/Game/PreloadTracker.cs`:
```csharp
public sealed class PreloadTracker
{
    private readonly int _warmup; private readonly double _threshold;
    private readonly Dictionary<string,int> _hits = new(StringComparer.Ordinal);
    public int ZonesObserved { get; private set; }
    public PreloadTracker(int warmupZones, double commonThreshold) { _warmup = warmupZones; _threshold = commonThreshold; }

    public readonly record struct ZoneResult(IReadOnlyList<string> Alerts);

    /// <summary>Record a zone's catalog-matched paths; returns which are NOT "common noise".</summary>
    public ZoneResult ObserveZone(IEnumerable<string> matchedPaths)
    {
        var paths = matchedPaths.Distinct(StringComparer.Ordinal).ToList();
        ZonesObserved++;
        foreach (var p in paths) _hits[p] = _hits.GetValueOrDefault(p) + 1;
        var alerts = new List<string>();
        foreach (var p in paths)
        {
            var common = ZonesObserved >= _warmup && (double)_hits[p] / ZonesObserved >= _threshold;
            if (!common) alerts.Add(p);
        }
        return new ZoneResult(alerts);
    }

    public IReadOnlyDictionary<string,(int hits,double freq)> Snapshot()
        => _hits.ToDictionary(k => k.Key, k => (k.Value, ZonesObserved == 0 ? 0 : (double)k.Value / ZonesObserved));

    // Persistence: expose (ZonesObserved, _hits) for save; add a ctor/Load to restore.
    public void Load(int zonesObserved, IReadOnlyDictionary<string,int> hits)
    { ZonesObserved = zonesObserved; foreach (var kv in hits) _hits[kv.Key] = kv.Value; }
    public (int zones, IReadOnlyDictionary<string,int> hits) Export() => (ZonesObserved, _hits);
}
```
- [ ] **Step 4: Green.** build + test.
- [ ] **Step 5: Commit** — `feat(core): PreloadTracker zone-frequency common-noise filter`.

---

## Task 4: Settings + world-loop scan + publish

**Files:** `RadarSettings.cs`, `RadarApp.cs`, `RenderContext.cs`.

- [ ] **Step 1: Settings.** In `RadarSettings.cs` add a `PreloadAlertSettings PreloadAlert { get; set; } = new();` and the class:
```csharp
public sealed class PreloadAlertSettings
{
    public bool Enabled { get; set; }               // opt-in, default OFF
    public string MinTier { get; set; } = "mechanic"; // pinnacle|high|mechanic|interactable — show >= this
    public string AudioTier { get; set; } = "pinnacle"; // cue when >= this (or "off")
    public bool Diagnostic { get; set; }            // expose full match+frequency
    public double CommonThreshold { get; set; } = 0.6;
    public int WarmupZones { get; set; } = 4;
    public string Anchor { get; set; } = "top-right";
    public int OffsetX { get; set; } = 0;
    public int OffsetY { get; set; } = 0;
}
```
- [ ] **Step 2: RenderContext.** In `RenderContext.cs` add `public readonly record struct PreloadHit(string Label, string Tier, string Category, string Color);` and a ctx field `IReadOnlyList<PreloadHit>? PreloadHits = null` + `bool PreloadEnabled = false` + anchor/offset params (mirror the Zone Summary panel's ctx params).
- [ ] **Step 3: Wire the world loop.** In `RadarApp.cs`: construct a `Poe2LoadedFiles` on the world reader stack (`_live`) and a `PreloadTracker` (from settings, restored from a sidecar `config/preload_freq.json` at startup). Detect zone change (existing AreaHash change signal the world loop already has). On zone change, if `_settings.PreloadAlert.Enabled`: (a) `var loaded = _loadedFiles.ScanLoadedPaths();` (b) `var matched = loaded.Select(PreloadCatalog.Shared.Match).Where(h => h != null)...` keep `(path, hit)`; (c) feed matched *paths* to `_preloadTracker.ObserveZone(...)`; (d) alerts = tracker result ∩ the matched hits, filtered to `Tier >= MinTier`; (e) build `_preloadFrame = List<PreloadHit>` and publish into the `WorldSnapshot` (same volatile-swap idiom as HP-bar/affix frames); (f) persist tracker via `Export()` → sidecar json (throttled). Pass ctx fields in `Tick`. Keep the scan on the WORLD thread only (heavy).
- [ ] **Step 4: Build Overlay** → 0/0. Tests still green.
- [ ] **Step 5: Commit** — `feat(overlay): preload world-loop scan + tracker + PreloadFrame publish + settings`.

---

## Task 5: Draw panel + audio cue

**Files:** `OverlayRenderer.cs`.

- [ ] **Step 1: DrawPreloadPanel.** Add `DrawPreloadPanel(ID2D1RenderTarget rt, RenderContext ctx)` mirroring `DrawZoneSummary`/`DrawSessionHud` (anchor + offset resolution, panel bg, tier-coloured lines grouped by tier). Draw only when `ctx.PreloadEnabled && ctx.PreloadHits is { Count: >0 }`. Call it from the main draw path where the other HUD panels are drawn. Tier color from the hit's `Color`; header e.g. "PRELOAD".
- [ ] **Step 2: Audio cue.** Reuse the existing audio-cue mechanism: when a new zone's hits include `>= AudioTier`, fire the configured cue. (The world loop can raise the cue when it publishes the frame, mirroring the mechanic-nearby cue — put the trigger where the frame is built in Task 4 if cleaner; if so, note it. Either location is fine; pick the one matching the existing audio-cue call site.)
- [ ] **Step 3: Build** → 0/0; tests green.
- [ ] **Step 4: Commit** — `feat(overlay): DrawPreloadPanel + tiered audio cue`.

**Live check (Task 7):** in a real map, the panel lists the zone's actual content; noise (always-loaded) is suppressed after warmup.

---

## Task 6: API + dashboard

**Files:** `ApiServer.cs`, `DashboardHtml.cs`.

- [ ] **Step 1: API state.** In `ApiServer` `ReadSettings()` add the `preloadAlert` object (all `PreloadAlertSettings` fields, camelCase). In the `/state` (or a `/preload`) response add the current zone's hits + (when `Diagnostic`) the tracker `Snapshot()` (path → hits/freq) so the dashboard can show signal-vs-noise.
- [ ] **Step 2: Patch cases.** In `ApplySettings`: `preloadEnabled` (TryBool), `preloadMinTier`/`preloadAudioTier`/`preloadAnchor` (TryString, validated against the allowed sets), `preloadDiagnostic` (TryBool), `preloadCommonThreshold` (TryFloat clamp 0..1), `preloadWarmupZones` (TryInt clamp 1..50), `preloadOffsetX/Y` (TryInt). Follow the sessionHud* idiom exactly.
- [ ] **Step 3: Dashboard.** Add a **collapsible "Preload Alert (experimental)"** settings card: enable toggle, MinTier + AudioTier selects, threshold + warmup inputs, anchor + offsets, and a **Diagnostic** toggle that reveals a live table of current matches + their zone-frequency (from `/state`). Match the sibling card idioms (`data-set` for scalars; a small JS render for the diagnostic table).
- [ ] **Step 4: Build** → 0/0; tests green.
- [ ] **Step 5: Commit** — `feat(overlay): preload API + dashboard card + diagnostic view`.

---

## Task 7: Integration sweep + release

**Files:** `*.csproj` (version), `CHANGELOG.md`, `README.md`.

- [ ] **Step 1:** Full build + tests + `compliance-gate.ps1` + `scrub -SelfTest` (all green). Confirm the branch diff has NO input/process-write/pricing symbols and `Poe2Offsets.cs` only gained the read-only `LoadedFiles` block.
- [ ] **Step 2:** Version → `0.14.0`.
- [ ] **Step 3:** CHANGELOG `## [0.14.0]` (ALL-OUT themed — bold/emoji/symmetry) describing the opt-in Preload Alert + experimental/diagnostic framing; README feature bullet. Badge stays `0.5.4`.
- [ ] **Step 4: Live tuning (user).** In real maps: enable Preload Alert + Diagnostic; confirm the panel reflects real content and the frequency filter suppresses noise after warmup; tune default `CommonThreshold` if needed; grow the catalog from unmatched content-root paths surfaced in diagnostic.
- [ ] **Step 5:** Final whole-branch review (Sonnet). Then commit `chore(release): v0.14.0 - Preload Alert (opt-in, experimental)`, merge to `main` (handle any stale tag), tag `v0.14.0`, push (CI release), themed Discord post, memory update.

---

## Self-review

**Spec coverage:** validated reader → Task 1; catalog + entity-gate → Task 2; frequency filter → Task 3; world-loop scan + settings + publish → Task 4; panel + audio → Task 5; API + dashboard + diagnostic → Task 6; release + live tuning → Task 7. All spec sections covered.

**Placeholder scan:** No TBD/TODO. Novel logic (reader, catalog match, tracker) has full signatures + concrete test cases. The reader mirrors the already-live-validated `--preload` probe. Where a call site is codebase-specific (audio-cue location, AreaHash-change signal, snapshot-publish idiom), the plan names the existing sibling to mirror (mechanic-nearby cue, Zone Summary panel, HP-bar/affix frame publish) rather than inventing.

**Type consistency:** `Poe2LoadedFiles.ScanLoadedPaths():IReadOnlySet<string>` (T1) → consumed T4. `PreloadCatalog.Match(string):CatalogHit?` + `CatalogHit(Label,Tier,Category,Color,Match)` (T2) → T4. `PreloadTracker.ObserveZone(IEnumerable<string>):ZoneResult(Alerts)` + `Snapshot()` + `Export/Load` (T3) → T4/T6. `PreloadHit(Label,Tier,Category,Color)` (T4 RenderContext) → T5 render. `PreloadAlertSettings` fields (T4) consistent across T4/T6 (camelCase mirror). Tier strings `pinnacle|high|mechanic|interactable` consistent catalog↔settings↔render.
