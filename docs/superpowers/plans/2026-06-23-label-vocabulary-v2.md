# POE2GPS — Label Vocabulary v2 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Replace the hardcoded 7-option classify picker with a curated rich, grouped label vocabulary (embedded `labels.json` → `GET /api/labels` → a dashboard datalist) plus free-text input.

**Architecture:** An embedded `Web/labels.json` (grouped) loaded once by a small `LabelVocabulary` loader + served via `GET /api/labels`. The dashboard builds a `<datalist id="labelVocab">` from it (mirroring the existing `modVocab` pattern) and swaps the two classify `<select>`s (`.ea-cat`, `.dir-cat`) for datalist-backed `<input>`s — pick a curated label or type any string. The `category` POST is unchanged.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`, x64), vanilla-JS dashboard.

## Global Constraints

- **.NET 10, x64.** `TreatWarningsAsErrors=true`, `Nullable=enable` → every task ends **0 warnings, 0 errors**.
- **Read-only / GGG-compliant.** Embedded vocabulary + a read route + a UI change. No input-emission/process-write/pricing. `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1` → PASS. No identifying data on any wire.
- **Syncability.** New `Web/labels.json` + `Web/LabelVocabulary.cs`; thin hooks into `ApiServer.cs`/`DashboardHtml.cs`/the csproj.
- Build: `dotnet build POE2Radar.slnx`. Test: `dotnet test POE2Radar.slnx`. Gate as above.

---

## File Structure

**New:** `src/POE2Radar.Overlay/Web/labels.json`, `src/POE2Radar.Overlay/Web/LabelVocabulary.cs`.
**Modified:** `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (embed), `src/POE2Radar.Overlay/Web/ApiServer.cs` (route), `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (datalist + pickers), `POE2Radar.Overlay.csproj` (version, T3).

---

## Task 1: `labels.json` + `LabelVocabulary` loader + `GET /api/labels`

**Files:** Create `src/POE2Radar.Overlay/Web/labels.json`, `src/POE2Radar.Overlay/Web/LabelVocabulary.cs`; modify `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj`, `src/POE2Radar.Overlay/Web/ApiServer.cs`.

**Interfaces — Produces:** `LabelVocabulary.Shared.Groups` (`IReadOnlyDictionary<string, List<string>>`); `GET /api/labels` returns the grouped vocabulary.

- [ ] **Step 1: Create `src/POE2Radar.Overlay/Web/labels.json`**

```json
{
  "Progression":        ["MainProgression", "Transition", "Checkpoint", "Waypoint", "SideZone", "Optional"],
  "Rewards & Upgrades": ["Reward", "PermanentUpgrade", "GemSource", "Vendor", "Merchant", "Currency"],
  "Bosses":             ["Boss", "SideBoss", "Pinnacle", "Citadel"],
  "League & Seasonal":  ["League", "Seasonal", "Event"],
  "Mechanics":          ["Breach", "Expedition", "Ritual", "Delirium", "Strongbox", "Essence", "Abyss", "Shrine", "Trial", "Sanctum"],
  "Atlas":              ["Tower", "Temple", "Vault", "Unique"],
  "Entities":           ["NPC", "Chest", "Door", "Other"]
}
```

- [ ] **Step 2: Embed it**

In `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj`, next to the other `<EmbeddedResource Include="Web\...json" />` lines, add:
```xml
    <EmbeddedResource Include="Web\labels.json" />
```

- [ ] **Step 3: Create the loader**

```csharp
// src/POE2Radar.Overlay/Web/LabelVocabulary.cs
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Overlay.Web;

/// <summary>The curated classification label vocabulary (group name → label list), loaded once from the
/// embedded <c>labels.json</c>. Served read-only via /api/labels for the dashboard's classify pickers.
/// Read-only w.r.t. the game.</summary>
public sealed class LabelVocabulary
{
    private readonly Dictionary<string, List<string>> _groups;
    private LabelVocabulary(Dictionary<string, List<string>> groups) => _groups = groups;

    public static LabelVocabulary Shared { get; } = Load();
    public IReadOnlyDictionary<string, List<string>> Groups => _groups;

    private static LabelVocabulary Load()
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("labels.json", StringComparison.Ordinal));
            if (resName != null)
                using (var s = asm.GetManifestResourceStream(resName))
                {
                    var parsed = s != null ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(s) : null;
                    if (parsed != null) groups = parsed;
                }
        }
        catch (Exception ex) { Console.Error.WriteLine($"LabelVocabulary load failed: {ex.Message}"); }
        return new LabelVocabulary(groups);
    }
}
```

- [ ] **Step 4: Add the `GET /api/labels` route**

In `ApiServer.cs`, next to the other read routes (e.g. `/api/dynasty-maps`), add:
```csharp
            case "/api/labels":
                // Read-only: the curated classification label vocabulary (grouped). No identifying data.
                Write(ctx, 200, JsonSerializer.Serialize(LabelVocabulary.Shared.Groups, Json));
                break;
```

- [ ] **Step 5: Build + gate**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Gate → PASS. (Manual: `curl http://localhost:7777/api/labels` returns the grouped JSON.)

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Overlay/Web/labels.json src/POE2Radar.Overlay/Web/LabelVocabulary.cs src/POE2Radar.Overlay/POE2Radar.Overlay.csproj src/POE2Radar.Overlay/Web/ApiServer.cs
git commit -m "feat(labels): curated label vocabulary (labels.json) + LabelVocabulary loader + /api/labels"
```

---

## Task 2: Dashboard — `labelVocab` datalist + datalist-backed classify pickers

**Files:** Modify `src/POE2Radar.Overlay/Web/DashboardHtml.cs`.

**Interfaces — Consumes:** `GET /api/labels` (T1).

- [ ] **Step 1: Build the `labelVocab` datalist (mirror the existing `modVocab` pattern)**

Add a loader function (near the other tab loaders) that mirrors the `modVocab` datalist approach (DashboardHtml.cs ~972-977):
```javascript
async function loadLabelVocab(){
  let dl=document.getElementById('labelVocab');
  if(!dl){ dl=document.createElement('datalist'); dl.id='labelVocab'; document.body.appendChild(dl); }
  let groups={};
  try{ groups=await getJSON('/api/labels'); }catch(e){}
  const labels=[]; Object.values(groups).forEach(arr=>(arr||[]).forEach(l=>labels.push(l)));
  dl.innerHTML = labels.map(l=>'<option value="'+esc(l)+'"></option>').join('');
}
```
Call `loadLabelVocab();` once at startup init (find where the dashboard runs its initial `load*()` calls / `wireSettings()` on DOMready and add it; if there's a central init, put it there).

- [ ] **Step 2: Entity Atlas classify picker → datalist input**

In `eaClassRow` (DashboardHtml.cs ~1322-1330), replace:
```javascript
function eaClassRow(a){
  const opts=['League','PermanentUpgrade','GemSource','Boss','SideZone','SideBoss','Other']
    .map(c=>'<option value="'+c+'">'+c+'</option>').join('');
  return '<div class="row" data-m="'+esc(a.metadata)+'">'
    + '<div class="rl">'+esc(a.name)+'<small>'+esc(a.category)+' · '+esc(a.zone||'?')+' · ×'+a.count+'</small></div>'
    + '<select class="numin selin ea-cat">'+opts+'</select>'
    + '<input class="numin ea-prio" type="number" min="0" max="1000" value="50" style="width:64px">'
    + '<button class="delbtn ea-add">Classify</button></div>';
}
```
with:
```javascript
function eaClassRow(a){
  return '<div class="row" data-m="'+esc(a.metadata)+'">'
    + '<div class="rl">'+esc(a.name)+'<small>'+esc(a.category)+' · '+esc(a.zone||'?')+' · ×'+a.count+'</small></div>'
    + '<input class="numin ea-cat" list="labelVocab" placeholder="label…" style="width:130px">'
    + '<input class="numin ea-prio" type="number" min="0" max="1000" value="50" style="width:64px">'
    + '<button class="delbtn ea-add">Classify</button></div>';
}
```
(The `.ea-cat` `.value` read at ~line 1307 is unchanged — an `<input>` `.value` works identically.)

- [ ] **Step 3: Director classify picker → datalist input**

Find `candRow` (the Director-tab classify row that defines the `.dir-cat` picker — its `.dir-cat` `.value` is read at ~DashboardHtml.cs:1247). It uses the same hardcoded `<select class="...dir-cat">...</select>` pattern. Replace that `<select>` (and its `opts` const if it has its own) with:
```javascript
    '<input class="numin dir-cat" list="labelVocab" placeholder="label…" style="width:130px">'
```
Keep the `.dir-prio` input + the `.dir-add` button unchanged.

- [ ] **Step 4: Build + gate + manual sanity**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Gate → PASS. (Manual: the Entity Atlas + Director classify rows show a text box that autocompletes the curated labels and accepts a typed custom label; classifying with "Boss" or a custom "Waypoint" still POSTs and appears in the catalog.)

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(dashboard): classify pickers use the curated label vocabulary + free-text (datalist)"
```

---

## Task 3: Release v0.2.2

**Files:** Modify `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (version), `docs/release-checklist.md`.

- [ ] **Step 1: Bump version** `<Version>0.2.1</Version>` → `<Version>0.2.2</Version>`.

- [ ] **Step 2: Release-checklist item**

Append: `GET /api/labels` returns the grouped vocabulary; the Entity Atlas + Director classify pickers autocomplete the curated labels (Waypoint, Shrine, etc.) and accept a typed custom label; an existing classification (e.g. "Boss") still works.

- [ ] **Step 3: Full verification sweep**

```bash
dotnet build POE2Radar.slnx -c Release      # 0W/0E
dotnet test  POE2Radar.slnx                  # all pass (87/87)
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1          # PASS
powershell -ExecutionPolicy Bypass -File scripts/scrub-strings.ps1 -SelfTest  # PASSED
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "release: v0.2.2 — rich label vocabulary + free-text for classify"
```

---

## Self-Review (author checklist — completed)

- **Spec coverage:** vocabulary + loader + route (T1); datalist + both classify pickers (T2); release (T3). ✓
- **Type consistency:** `LabelVocabulary.Shared.Groups` (T1) consumed by the route (T1) + the dashboard datalist (T2). ✓
- **No placeholders:** complete code for labels.json, loader, route, datalist, eaClassRow; candRow is an anchor-match with the identical transform (its exact current markup mirrors eaClassRow). ✓
- **No existing breakage:** the 7 old options are a subset of the new vocabulary; `category` stays a free string; `.ea-cat`/`.dir-cat` `.value` reads are unchanged (input vs select — identical). ✓
- **Compliance:** read-only embedded data + a read route + a UI change; no input/write/pricing; gate run each code task. ✓
