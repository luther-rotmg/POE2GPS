# POE2GPS — Dynasty-Support Map Highlighting — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Surface the maps whose Anomaly bosses drop Lineage/Dynasty Support Gems on the atlas (purple ring + label + auto-track + arrows), behind a toggle — the first community-requested feature.

**Architecture:** A curated `MapCode → {name, boss, gems}` table (embedded JSON + a `Core` loader), matched in the existing atlas mark-building loop. When `HighlightDynastyMaps` is on, matched nodes get the full Citadel-style treatment by reusing the `AtlasMark` fields (`Color`/`Label`/`Arrow`/`Nav`) and the `isTracked`/`isNav`/`isArrow` flags — no renderer change.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`, x64), xUnit (`tests/POE2Radar.Tests`, Core only), vanilla-JS dashboard.

## Global Constraints

- **.NET 10, x64.** `TreatWarningsAsErrors=true`, `Nullable=enable` → every task ends **0 warnings, 0 errors**.
- **Read-only / GGG-compliant.** Embedded lookup + the existing atlas draw path. No input-emission/process-write/pricing. `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1` → PASS. No identifying data on any wire. Anti-detection unchanged.
- **Syncability.** New logic in new files; thin hooks into `RadarApp.cs`/`ApiServer.cs`/`DashboardHtml.cs`/`RadarSettings.cs`.
- Tests reference `POE2Radar.Core` only.
- Build: `dotnet build POE2Radar.slnx`. Test: `dotnet test POE2Radar.slnx`. Gate as above.
- **Curated data (verified, code-keyed — codes ≠ display names):**
  `MapVaalVault`→Sealed Vault (Maztli/Ytzara), `MapDerelictMansion`→Derelict Mansion (Varloch/Avelyne),
  `MapCavernCity`→Sacred Reservoir (Zahmir), `MapUberBoss_JadeCitadel`→The Jade Isles (Manoki).

---

## File Structure

**New:**
- `src/POE2Radar.Core/Game/dynasty_maps.json` — the curated table (committed directly; hand-maintained, no generator). Embedded.
- `src/POE2Radar.Core/Game/DynastyMaps.cs` — load-once loader.
- `tests/POE2Radar.Tests/DynastyMapsTests.cs`.

**Modified (thin hooks):**
- `src/POE2Radar.Core/Game/POE2Radar.Core.csproj` — embed the JSON.
- `src/POE2Radar.Overlay/Config/RadarSettings.cs` — `HighlightDynastyMaps`.
- `src/POE2Radar.Overlay/RadarApp.cs` — the dynasty branch in the atlas mark loop.
- `src/POE2Radar.Overlay/Web/ApiServer.cs` — `highlightDynastyMaps` round-trip + `GET /api/dynasty-maps`.
- `src/POE2Radar.Overlay/Web/DashboardHtml.cs` — Settings toggle + Dynasty Maps reference card.
- `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` — version bump (T5).

---

## Task 1: Dynasty data + `DynastyMaps` loader (Core) + test

**Files:** Create `src/POE2Radar.Core/Game/dynasty_maps.json`, `src/POE2Radar.Core/Game/DynastyMaps.cs`, `tests/POE2Radar.Tests/DynastyMapsTests.cs`; modify `src/POE2Radar.Core/Game/POE2Radar.Core.csproj`.

**Interfaces — Produces:** `DynastyMaps.Shared`, `int Count`, `bool TryGet(string mapCode, out DynastyInfo info)`, `IReadOnlyDictionary<string,DynastyInfo> All`, `DynastyInfo(string Name, string Boss, IReadOnlyList<string> Gems)`.

- [ ] **Step 1: Create the curated data file**

`src/POE2Radar.Core/Game/dynasty_maps.json`:
```json
{
  "MapVaalVault":            { "name": "Sealed Vault",     "boss": "Maztli / Ytzara",   "gems": ["Atalui's Bloodletting", "Paquate's Pact", "Xibaqua's Rending"] },
  "MapDerelictMansion":      { "name": "Derelict Mansion", "boss": "Varloch / Avelyne", "gems": ["Ailith's Chimes", "Rigwald's Ferocity", "Einhar's Beastrite"] },
  "MapCavernCity":           { "name": "Sacred Reservoir", "boss": "Zahmir",            "gems": ["Varashta's Blessing", "Zarokh's Refrain", "Garukhan's Resolve", "Khatal's Rejuvenation"] },
  "MapUberBoss_JadeCitadel": { "name": "The Jade Isles",   "boss": "Manoki",            "gems": ["Rakiata's Flow", "Tawhoa's Tending", "Kaom's Madness", "Tasalio's Rhythm"] }
}
```

- [ ] **Step 2: Embed it**

In `src/POE2Radar.Core/Game/POE2Radar.Core.csproj`, in the `<EmbeddedResource>` ItemGroup, add:
```xml
    <EmbeddedResource Include="Game\dynasty_maps.json" />
```

- [ ] **Step 3: Write the failing test**

```csharp
// tests/POE2Radar.Tests/DynastyMapsTests.cs
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class DynastyMapsTests
{
    [Fact]
    public void embedded_table_loads_and_maps_code_to_name_and_gems()
    {
        Assert.True(DynastyMaps.Shared.Count >= 4);
        Assert.True(DynastyMaps.Shared.TryGet("MapVaalVault", out var info), "expected MapVaalVault in the dynasty table");
        Assert.Equal("Sealed Vault", info.Name);
        Assert.NotEmpty(info.Gems);
        Assert.Contains("Atalui's Bloodletting", info.Gems);
    }

    [Fact]
    public void unknown_code_returns_false()
        => Assert.False(DynastyMaps.Shared.TryGet("MapNotADynastyMap", out _));
}
```

- [ ] **Step 4: Run → fail.** `dotnet test POE2Radar.slnx --filter DynastyMapsTests` → FAIL (type missing).

- [ ] **Step 5: Implement the loader**

```csharp
// src/POE2Radar.Core/Game/DynastyMaps.cs
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Game;

/// <summary>Curated table of endgame maps whose Anomaly bosses drop Lineage/Dynasty Support Gems, keyed by
/// the in-game MapCode (e.g. "MapVaalVault" → "Sealed Vault" — codes differ wildly from display names).
/// Loaded once from the embedded <c>dynasty_maps.json</c>. Read-only.</summary>
public sealed class DynastyMaps
{
    public sealed record DynastyInfo(string Name, string Boss, IReadOnlyList<string> Gems);

    private readonly Dictionary<string, DynastyInfo> _byCode;
    private DynastyMaps(Dictionary<string, DynastyInfo> byCode) => _byCode = byCode;

    public static DynastyMaps Shared { get; } = Load();
    public int Count => _byCode.Count;
    public bool TryGet(string mapCode, out DynastyInfo info) => _byCode.TryGetValue(mapCode, out info!);
    public IReadOnlyDictionary<string, DynastyInfo> All => _byCode;

    private sealed class Model
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("boss")] public string Boss { get; set; } = "";
        [JsonPropertyName("gems")] public List<string> Gems { get; set; } = new();
    }

    private static DynastyMaps Load()
    {
        var byCode = new Dictionary<string, DynastyInfo>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("dynasty_maps", StringComparison.Ordinal));
            if (name != null)
                using (var s = asm.GetManifestResourceStream(name))
                {
                    var parsed = s != null
                        ? JsonSerializer.Deserialize<Dictionary<string, Model>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        : null;
                    if (parsed != null)
                        foreach (var (code, m) in parsed)
                            byCode[code] = new DynastyInfo(m.Name, m.Boss, m.Gems);
                }
        }
        catch (Exception ex) { Console.Error.WriteLine($"DynastyMaps load failed: {ex.Message}"); }
        return new DynastyMaps(byCode);
    }
}
```

- [ ] **Step 6: Run → pass.** `dotnet test POE2Radar.slnx --filter DynastyMapsTests` → PASS. Build 0W/0E. Gate PASS.

- [ ] **Step 7: Commit**

```bash
git add src/POE2Radar.Core/Game/dynasty_maps.json src/POE2Radar.Core/Game/DynastyMaps.cs src/POE2Radar.Core/Game/POE2Radar.Core.csproj tests/POE2Radar.Tests/DynastyMapsTests.cs
git commit -m "feat(core): DynastyMaps — curated dynasty-support map table (code -> name/boss/gems)"
```

---

## Task 2: `HighlightDynastyMaps` setting + round-trip + `GET /api/dynasty-maps`

**Files:** Modify `src/POE2Radar.Overlay/Config/RadarSettings.cs`, `src/POE2Radar.Overlay/Web/ApiServer.cs`.

- [ ] **Step 1: Add the setting**

In `RadarSettings.cs`, near the atlas settings (`AtlasHighlightTags`/`AtlasDrawAll`), add:
```csharp
    // Highlight maps whose Anomaly bosses drop Lineage/Dynasty Support Gems (Sealed Vault, Sacred
    // Reservoir, Derelict Mansion, …) with the full Citadel-style ring+arrow+track. Off by default.
    public bool HighlightDynastyMaps { get; set; } = false;
```

- [ ] **Step 2: Settings round-trip**

In `ApiServer.cs` `ReadSettings()` add `highlightDynastyMaps = _settings.HighlightDynastyMaps,`.
In `ApplySettings`, among the flat `TryBool` cases add:
```csharp
                case "highlightDynastyMaps" when TryBool(p.Value, out var b): _settings.HighlightDynastyMaps = b; applied.Add(p.Name); break;
```

- [ ] **Step 3: Add the `GET /api/dynasty-maps` route**

In the route switch (near the other `/api/*` read routes), add:
```csharp
            case "/api/dynasty-maps":
                // Read-only reference: the curated dynasty-support map table (no identifying data).
                Write(ctx, 200, JsonSerializer.Serialize(
                    POE2Radar.Core.Game.DynastyMaps.Shared.All.Select(kv => new
                    {
                        code = kv.Key, name = kv.Value.Name, boss = kv.Value.Boss, gems = kv.Value.Gems
                    }), Json));
                break;
```

- [ ] **Step 4: Build + gate**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Gate → PASS. (Manual: `GET /api/dynasty-maps` returns the 4-entry table; `/api/settings` carries `highlightDynastyMaps`.)

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Web/ApiServer.cs
git commit -m "feat(api): HighlightDynastyMaps setting + /api/dynasty-maps reference route"
```

---

## Task 3: Atlas mark hook — dynasty nodes get the full treatment when the toggle is on

**Files:** Modify `src/POE2Radar.Overlay/RadarApp.cs` (the atlas mark-building loop, ~2157-2174).

**Interfaces — Consumes:** `DynastyMaps.Shared.TryGet` (T1), `_settings.HighlightDynastyMaps` (T2).

- [ ] **Step 1: Replace the per-node mark computation with the dynasty-augmented version**

Find (RadarApp.cs ~2159-2171, inside `foreach (var n in nodes)`):
```csharp
            var selected = sel.Contains(n.Element);
            var mTrack = Match(hlTrack, n);
            var mNav = Match(hlNav, n);
            var mArrow = Match(hlArrow, n);
            var isTracked = selected || mTrack != null;   // Highlight (ring)
            var isNav = mNav != null;                     // Nav-to (route line)
            var isArrow = mArrow != null;                 // Arrow (off-screen pointer)
            // ONLY highlighted/nav/arrow maps are drawn (the point: surface content the game hides).
            // AtlasDrawAll debug overrides this to draw every node.
            if (!_settings.AtlasDrawAll && !isTracked && !isNav && !isArrow) continue;
            var matched = mTrack ?? mNav ?? mArrow;
            var label = matched ?? (n.Tags is { Count: > 0 } ? n.Tags[0] : (string.IsNullOrEmpty(n.MapName) ? null : n.MapName));
            string? color = matched != null && _settings.AtlasHighlightColors.TryGetValue(matched, out var c) ? c : null;
```

Replace with:
```csharp
            var selected = sel.Contains(n.Element);
            var mTrack = Match(hlTrack, n);
            var mNav = Match(hlNav, n);
            var mArrow = Match(hlArrow, n);
            // Dynasty-support maps (curated by MapCode) get the full Citadel-style treatment when the
            // toggle is on — ring + route + off-screen arrow — plus a gem-count label in dynasty purple.
            POE2Radar.Core.Game.DynastyMaps.DynastyInfo? dyn =
                _settings.HighlightDynastyMaps && POE2Radar.Core.Game.DynastyMaps.Shared.TryGet(n.MapCode, out var di) ? di : null;
            var isTracked = selected || mTrack != null || dyn != null;   // Highlight (ring)
            var isNav = mNav != null || dyn != null;                     // Nav-to (route line)
            var isArrow = mArrow != null || dyn != null;                 // Arrow (off-screen pointer)
            // ONLY highlighted/nav/arrow maps are drawn (the point: surface content the game hides).
            // AtlasDrawAll debug overrides this to draw every node.
            if (!_settings.AtlasDrawAll && !isTracked && !isNav && !isArrow) continue;
            var matched = mTrack ?? mNav ?? mArrow;
            var label = matched ?? (n.Tags is { Count: > 0 } ? n.Tags[0] : (string.IsNullOrEmpty(n.MapName) ? null : n.MapName));
            string? color = matched != null && _settings.AtlasHighlightColors.TryGetValue(matched, out var c) ? c : null;
            if (dyn != null)
            {
                label = $"{dyn.Name} · {dyn.Gems.Count} dynasty gem{(dyn.Gems.Count == 1 ? "" : "s")}";
                if (n.Kind != "Citadel") color = "#A55CFF";   // dynasty purple — but keep gold if it's also a Citadel
            }
```

(`n.MapCode` and `n.Kind` are existing `AtlasNodeLive` fields. `·` is the `·` middot. The nav/route + color flow downstream is unchanged — `gridColor[n.Grid] = color` at the existing line picks up purple.)

- [ ] **Step 2: Build + gate**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Gate → PASS.

- [ ] **Step 3: Commit**

```bash
git add src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(atlas): highlight dynasty-support maps (purple ring + gem label + track/arrow) when enabled"
```

---

## Task 4: Dashboard — toggle + "Dynasty Maps" reference card

**Files:** Modify `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (Settings tab toggle + a card in the Atlas tab + a small loader).

- [ ] **Step 1: Settings toggle**

In the `data-view="settings"` Radar Display card (near the other toggles), add:
```html
            <div class="row"><div class="rl">Dynasty-support maps<small>highlight maps whose Anomaly bosses drop Lineage/Dynasty support gems (off by default)</small></div>
              <label class="sw"><input type="checkbox" data-set="highlightDynastyMaps"><span class="track"></span><span class="knob"></span></label></div>
```
(The generic `[data-set]` binder wires it — no JS change for the toggle.)

- [ ] **Step 2: Reference card in the Atlas tab**

In the `data-view="atlas"` section, add a card (place it near the top of that section):
```html
          <div class="card">
            <h3>Dynasty-support maps <small>Anomaly-boss maps that drop Lineage/Dynasty support gems &mdash; enable the highlight in Settings</small></h3>
            <div id="dynastyList" class="znotes" style="display:block"><div class="rl hint-row">Loading&hellip;</div></div>
          </div>
```

- [ ] **Step 3: Loader JS**

Add (near the other tab loaders) a `loadDynasty()` and call it when the atlas tab opens (find where the atlas tab's `loadAtlas()` is invoked on tab-switch and add `loadDynasty()` beside it; if the atlas tab has no on-open hook, call `loadDynasty()` once at startup):
```javascript
async function loadDynasty(){
  const el=$('#dynastyList'); if(!el) return;
  let rows=[];
  try{ rows=await getJSON('/api/dynasty-maps'); }catch(e){}
  el.innerHTML = (rows&&rows.length) ? rows.map(r=>
    '<div class="row" style="flex-wrap:wrap"><div class="rl">'+esc(r.name||'')+'<small>'+esc(r.boss||'')+'</small></div>'
    +'<div style="flex-basis:100%;font-size:11px;color:var(--ink-faint)">'+(r.gems||[]).map(g=>esc(g)).join(' &middot; ')+'</div></div>'
  ).join('') : '<div class="rl hint-row">No dynasty maps loaded.</div>';
}
```

- [ ] **Step 4: Build + gate + manual sanity**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Gate → PASS. (Manual: the Atlas tab shows the 4 maps · boss · gems; the Settings toggle flips `highlightDynastyMaps`.)

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(dashboard): dynasty-support maps toggle + reference card"
```

---

## Task 5: Release v0.2.1

**Files:** Modify `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (version), `docs/release-checklist.md`.

- [ ] **Step 1: Bump version** `<Version>0.2.0</Version>` → `<Version>0.2.1</Version>`.

- [ ] **Step 2: Release-checklist item**

Append a manual live check: with **Highlight dynasty-support maps** on and the Atlas open, Sealed Vault / Sacred Reservoir / Derelict Mansion ring purple with a `<name> · N dynasty gems` label + arrow + auto-route; the Jade Isles stays Citadel-gold; toggle off → they disappear. The dashboard Atlas tab lists the 4 maps · boss · gems.

- [ ] **Step 3: Full verification sweep**

```bash
dotnet build POE2Radar.slnx -c Release      # 0W/0E
dotnet test  POE2Radar.slnx                  # all pass
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1          # PASS
powershell -ExecutionPolicy Bypass -File scripts/scrub-strings.ps1 -SelfTest  # PASSED
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "release: v0.2.1 — dynasty-support map highlighting (first community request)"
```

---

## Self-Review (author checklist — completed)

- **Spec coverage:** data + loader (T1); toggle + round-trip + reference route (T2); atlas highlight hook (T3); dashboard toggle + card (T4); release (T5). The Jade-Isles-also-a-Citadel edge handled in T3 (keep gold). ✓
- **Type consistency:** `DynastyMaps.TryGet`/`DynastyInfo`/`All` defined T1, consumed in T2 (route) + T3 (mark hook). `HighlightDynastyMaps` defined T2, consumed T3. ✓
- **No placeholders:** complete code for the loader, data, mark hook, settings, route; dashboard card + loader concrete (the one anchor-match is the atlas-tab on-open hook — fall back to a startup call if absent). ✓
- **No renderer change:** confirmed — `AtlasMark` already carries `Label`/`Color`/`Arrow`/`Nav`; the loop sets them. ✓
- **Compliance:** read-only embedded lookup + existing atlas draw; no input/write/pricing; toggle default off; gate run each code task. ✓
