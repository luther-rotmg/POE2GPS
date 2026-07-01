# Remote Views v0.17.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the overlay's existing localhost data be viewed from other LAN devices (opt-in), and add a standalone top-down web minimap page — both read-only views of data already read, defaulting to zero cost.

**Architecture:** Two features on the existing loopback HTTP server (`ApiServer`). LAN access flips the `HttpListener` prefix from `localhost` to `http://+:port` behind an opt-in setting, with a loopback fallback if the wildcard bind fails; all write endpoints stay `IsLoopbackHost`-gated so LAN peers are view-only. The web minimap adds a `/api/map` terrain endpoint (base64 walkable grid, cached per area hash, sourced from the already-published `WorldSnapshot`) and a `/map` HTML canvas page that reuses the existing `/state` + `/entities` endpoints to draw terrain + dots + player. Pure, testable helpers (`ApiPrefix`, `TerrainMapPayload`) live in Core; Overlay wiring/pages are validated by build + live smoke.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`, x64), `System.Net.HttpListener`, `System.Text.Json`, vanilla-JS canvas page as a C# raw string, xUnit (Core-only test project).

## Global Constraints

- **Strictly read-only of the game.** No memory writes, no injection, no input (`SendInput`/`PostMessage`/`keybd_event`), no `WriteProcessMemory`/`VirtualProtectEx`/`CreateRemoteThread`. No new memory reads and **no new offsets** — both features are views of already-read data.
- **No pricing / poe.ninja / trade / reward-values** anywhere.
- **Everything perf-hitting defaults OFF.** `AllowLanAccess` default `false`. The `/map` page does work only while a client polls it (zero cost when unviewed) — no always-on toggle needed.
- **Writes stay loopback-locked.** Every POST/write endpoint already calls `IsLoopbackHost` and returns `403 {"error":"forbidden host"}` for non-loopback hosts. This MUST remain true after LAN access is added — LAN peers may read (`/state`, `/entities`, `/obs`, `/map`, `/api/*` GETs) but never write.
- **Platform:** `net10.0-windows`, x64, `Nullable` enable, `TreatWarningsAsErrors=true` — all code must be warning-clean.
- **Tests:** `tests/POE2Radar.Tests` references **Core only**. Put unit-testable logic in `POE2Radar.Core`; validate Overlay endpoints/pages/dashboard by successful build + live smoke.
- **README badge stays `supports PoE2 0.5.4`.** App `<Version>` → `0.17.0`.
- **CI gates stay green:** `scripts/compliance-gate.ps1` and `scripts/scrub-strings.ps1 -SelfTest`.
- **Build note:** if the user's overlay is running, a local `Release` build shows MSB3026/MSB3027 file-copy-lock errors on `Overlay.dll`/`POE2Radar.Core.dll` — those are lock errors, NOT code errors. Success criterion is **0 CS compile errors**; CI builds clean.

---

### Task 1: LAN access — setting + conditional prefix + bind fallback

**Files:**
- Create: `src/POE2Radar.Core/Remote/ApiPrefix.cs`
- Create: `tests/POE2Radar.Tests/ApiPrefixTests.cs`
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs` (after `ApiPort`, ~line 196)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (usings; fields ~45-86; constructor ~91-145; `Start()` ~147-153)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (`new ApiServer(...)` call, ~656-665)

**Interfaces:**
- Produces: `POE2Radar.Core.Remote.ApiPrefix.Build(bool lanAccess, int port) → string`; `RadarSettings.AllowLanAccess` (bool, default false); `ApiServer` constructor gains optional `bool allowLanAccess = false` (passed named); `ApiServer` private fields `_allowLanAccess` (bool), `_port` (int), `_lanBindFailed` (volatile bool) consumed by Task 2's `/api/lan-info`.

- [ ] **Step 1: Write the failing test**

Create `tests/POE2Radar.Tests/ApiPrefixTests.cs`:

```csharp
using POE2Radar.Core.Remote;

public class ApiPrefixTests
{
    [Fact] public void Build_localhost_when_lan_disabled()
        => Assert.Equal("http://localhost:7777/", ApiPrefix.Build(false, 7777));

    [Fact] public void Build_wildcard_when_lan_enabled()
        => Assert.Equal("http://+:7777/", ApiPrefix.Build(true, 7777));

    [Fact] public void Build_uses_given_port()
        => Assert.Equal("http://+:8080/", ApiPrefix.Build(true, 8080));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/POE2Radar.Tests --filter ApiPrefixTests`
Expected: FAIL — `ApiPrefix` does not exist (compile error).

- [ ] **Step 3: Create the Core helper**

Create `src/POE2Radar.Core/Remote/ApiPrefix.cs`:

```csharp
namespace POE2Radar.Core.Remote;

/// <summary>Builds the HttpListener URI prefix for the local API. Loopback by default; binds all
/// interfaces (http://+:port) only when the user opts into LAN access. Pure/testable.</summary>
public static class ApiPrefix
{
    public static string Build(bool lanAccess, int port)
        => lanAccess ? $"http://+:{port}/" : $"http://localhost:{port}/";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/POE2Radar.Tests --filter ApiPrefixTests`
Expected: PASS (3/3).

- [ ] **Step 5: Add the setting**

In `src/POE2Radar.Overlay/Config/RadarSettings.cs`, immediately after the `ApiPort` property (~line 196):

```csharp
    // ── HTTP API. ──
    public int ApiPort { get; set; } = 7777;

    // Allow other devices on your LAN to VIEW the overlay's pages (/obs, /map, /state) by binding the
    // HTTP server to all interfaces instead of loopback. OFF by default. Writes stay loopback-only
    // regardless — LAN peers get 403 on any settings change. Changing this needs an app restart.
    public bool AllowLanAccess { get; set; } = false;
```

(No serialization registration needed — `RadarSettings` JSON uses `DefaultIgnoreCondition = Never`, so the new property round-trips automatically.)

- [ ] **Step 6: Wire the prefix + fallback into ApiServer**

In `src/POE2Radar.Overlay/Web/ApiServer.cs`:

(a) Add the Core using near the top (after the existing `using POE2Radar.Core.*` lines, e.g. after line 8):

```csharp
using POE2Radar.Core.Remote;
```

(b) Add fields alongside the existing `_running` field (~line 86):

```csharp
    private volatile bool _running;
    private readonly bool _allowLanAccess;   // bound all-interfaces when true (opt-in LAN view)
    private readonly int _port;              // stored for /api/lan-info + loopback fallback
    private volatile bool _lanBindFailed;    // true if http://+:port bind threw and we fell back to loopback
```

(c) Add the constructor parameter `bool allowLanAccess = false` immediately before `int port = 7777` (~line 116-117):

```csharp
        PresetStore? presetStore = null,
        bool allowLanAccess = false,
        int port = 7777)
```

(d) In the constructor body, replace the current prefix line (line 144)
`_listener.Prefixes.Add($"http://localhost:{port}/");`
with:

```csharp
        _allowLanAccess = allowLanAccess;
        _port = port;
        _listener.Prefixes.Add(ApiPrefix.Build(allowLanAccess, port));
```

(e) Replace `Start()` (lines 147-153) with the bind-fallback version:

```csharp
    public void Start()
    {
        try { _listener.Start(); }
        catch (HttpListenerException) when (_allowLanAccess)
        {
            // Binding all interfaces (http://+:port) failed — usually missing admin rights / no urlacl
            // reservation. Fall back to loopback so the local dashboard still works; surfaced to the
            // user via /api/lan-info (bindFailed).
            _lanBindFailed = true;
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(ApiPrefix.Build(false, _port));
            _listener.Start();
        }
        _running = true;
        var t = new Thread(Loop) { IsBackground = true, Name = "POE2Radar.Api" };
        t.Start();
    }
```

- [ ] **Step 7: Pass the setting from RadarApp**

In `src/POE2Radar.Overlay/RadarApp.cs`, in the `new ApiServer(...)` call (~656-665), add `allowLanAccess:` before `port:`:

```csharp
                             rebuildAudio: () => RebuildAudioCues(),
                             presetStore: _presetStore,
                             allowLanAccess: _settings.AllowLanAccess,
                             port: _settings.ApiPort);
```

- [ ] **Step 8: Build**

Run: `dotnet build src/POE2Radar.Overlay -c Debug`
Expected: 0 CS errors. (If the overlay is running, ignore MSB3026/MSB3027 copy-lock errors.)

- [ ] **Step 9: Commit**

```bash
git add src/POE2Radar.Core/Remote/ApiPrefix.cs tests/POE2Radar.Tests/ApiPrefixTests.cs src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(remote): opt-in LAN access — conditional HttpListener prefix + loopback fallback"
```

---

### Task 2: LAN surface — settings round-trip + /api/lan-info + dashboard card

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (`ReadSettings` ~1077; `ApplySettings` switch ~1154+; `Handle` switch add `/api/lan-info`; add `LanAddresses()` helper)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (new "Remote Access (LAN)" card in the settings view; `renderLanInfo()` JS in `loadSettings()`)

**Interfaces:**
- Consumes: `ApiServer._allowLanAccess`, `_port`, `_lanBindFailed`, `_settings.AllowLanAccess`, `IsLoopbackHost`, `Write`, `Json`, `TryBool` (all existing).
- Produces: `GET /api/lan-info` → `{ port, bound:"lan"|"localhost", bindFailed:bool, addresses:string[] }`; settings key `allowLanAccess` (GET + POST).

- [ ] **Step 1: Expose the setting in ReadSettings**

In `ApiServer.ReadSettings` (~line 1077), after the `apiPort` line, add:

```csharp
        apiPort = _settings.ApiPort, // display only — changing it needs a restart
        allowLanAccess = _settings.AllowLanAccess, // opt-in LAN view binding; needs a restart to apply
```

- [ ] **Step 2: Honor the setting in ApplySettings**

In `ApiServer.ApplySettings`, inside the `switch (p.Name)` block (alongside the other bool cases, e.g. right after the `hideJunk` case ~line 1154), add:

```csharp
                case "allowLanAccess" when TryBool(p.Value, out var lan): _settings.AllowLanAccess = lan; applied.Add(p.Name); break;
```

- [ ] **Step 3: Add the /api/lan-info route + helper**

In `ApiServer.Handle`, add a new case in the switch (place it near the other `/api/*` GET cases). The `s` variable (RadarState) is already in scope but unused here:

```csharp
            case "/api/lan-info":
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    port = _port,
                    bound = (_allowLanAccess && !_lanBindFailed) ? "lan" : "localhost",
                    bindFailed = _lanBindFailed,
                    addresses = LanAddresses(),
                }, Json));
                break;
```

Add the helper method (place near the other private static helpers, e.g. beside `IsLoopbackHost` ~line 2060). Fully-qualified types avoid touching the `using` block:

```csharp
    /// <summary>Best-effort list of this machine's LAN IPv4 addresses (for the dashboard's "your LAN URL"
    /// display). Skips loopback, down interfaces, and APIPA link-local (169.254.*). Never throws.</summary>
    private static string[] LanAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254", StringComparison.Ordinal)) continue; // APIPA link-local
                    list.Add(ip);
                }
            }
        }
        catch { /* odd adapters can throw during enumeration — best-effort */ }
        return list.ToArray();
    }
```

- [ ] **Step 4: Add the dashboard "Remote Access (LAN)" card**

In `src/POE2Radar.Overlay/Web/DashboardHtml.cs`, inside the settings view (`[data-view="settings"]`), near the OBS overlay card (~line 931), add a new card. It reuses the generic `data-set` toggle mechanism (so `wireSettings()`/`loadSettings()` handle the boolean automatically) and populates LAN URLs from `/api/lan-info`:

```html
          <div class="card collapsed" data-card="remote-lan">
            <h3>Remote Access (LAN) <small class="tag">&middot; view from other devices</small></h3>
            <div class="row"><div class="rl">Allow LAN access<small>let other devices on your network open /obs and /map (view-only — nobody on your LAN can change your settings). Needs an app restart to apply.</small></div>
              <label class="sw"><input type="checkbox" data-set="allowLanAccess"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl hint-row">First connection: allow POE2GPS through Windows Firewall (inbound TCP on your API port) when Windows prompts. Reads are unauthenticated over your LAN &mdash; only enable this on a network you trust.</div></div>
            <div class="row"><div class="rl">Your LAN URLs<small>open these from another device once LAN access is on + you've restarted</small></div>
              <span style="display:flex;flex-direction:column;gap:4px" id="lanUrls"><code style="font-size:12px;color:var(--ink-faint)">turn on LAN access + restart to see URLs</code></span></div>
          </div>
```

- [ ] **Step 5: Populate the LAN URLs from /api/lan-info**

In `DashboardHtml.cs`, add a `renderLanInfo()` function near the other render helpers, and call it from `loadSettings()` (add the call next to the existing `renderObsOverlay();` line ~1107):

```javascript
async function renderLanInfo(){
  try{
    const li=await getJSON('/api/lan-info');
    const box=document.getElementById('lanUrls'); if(!box) return;
    if(!li.addresses||!li.addresses.length){ box.innerHTML='<code style="font-size:12px;color:var(--ink-faint)">no LAN address detected</code>'; return; }
    const note = li.bindFailed
      ? '<small style="color:#f66">LAN bind failed &mdash; running loopback-only. Restart POE2GPS as administrator.</small>'
      : (li.bound==='lan' ? '' : '<small style="color:var(--ink-faint)">LAN access is off &mdash; toggle it on above, then restart.</small>');
    box.innerHTML = li.addresses.map(a=>
      `<code style="font-size:12px;color:var(--gold-bright)">http://${a}:${li.port}/map</code>`+
      `<code style="font-size:12px;color:var(--gold-bright)">http://${a}:${li.port}/obs</code>`).join('') + note;
  }catch(e){}
}
```

In `loadSettings()` (~line 1107), add the call:

```javascript
    renderEntityArrows(); renderObsOverlay(); renderDiscordPresence(); renderLanInfo();
```

- [ ] **Step 6: Build**

Run: `dotnet build src/POE2Radar.Overlay -c Debug`
Expected: 0 CS errors.

- [ ] **Step 7: Commit**

```bash
git add src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(remote): /api/lan-info + Remote Access (LAN) dashboard card (view-only over LAN)"
```

**Live smoke (record in report; not a blocking step):** flip "Allow LAN access" on, restart the overlay, and from another device open `http://<lan-ip>:7777/state` (should return JSON) and POST to `/api/settings` (should 403 `forbidden host`).

---

### Task 3: Web minimap backend — terrain provider + /api/map endpoint

**Files:**
- Create: `src/POE2Radar.Core/Remote/TerrainMapPayload.cs`
- Create: `tests/POE2Radar.Tests/TerrainMapPayloadTests.cs`
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (add `CurrentTerrain()`; pass `terrainProvider:` into `new ApiServer(...)`)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (constructor param + field; per-hash cache fields; `/api/map` route)

**Interfaces:**
- Consumes: `RadarApp._world` (published `WorldSnapshot` with `.Terrain` : `Poe2Live.TerrainData?` and `.AreaHash` : `uint` — confirmed by the snapshot construction at `RadarApp.cs:1772`); `TerrainData(byte[] Walkable, int Width, int Height)` (`Poe2Live.cs:170`).
- Produces: `POE2Radar.Core.Remote.TerrainMapPayload.ToJson(byte[] walkable, int width, int height, uint areaHash) → string`; `ApiServer` gains optional `Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)>? terrainProvider = null`; `GET /api/map` → `{ ready:true, areaHash, width, height, walkable:<base64> }` or `{ ready:false }`.

- [ ] **Step 1: Write the failing test**

Create `tests/POE2Radar.Tests/TerrainMapPayloadTests.cs`:

```csharp
using POE2Radar.Core.Remote;

public class TerrainMapPayloadTests
{
    [Fact] public void ToJson_includes_ready_dims_hash_and_base64_walkable()
    {
        var walk = new byte[] { 0, 1, 1, 0 };                 // 2x2 grid
        var json = TerrainMapPayload.ToJson(walk, 2, 2, 0xABCDu);
        Assert.Contains("\"ready\":true", json);
        Assert.Contains("\"width\":2", json);
        Assert.Contains("\"height\":2", json);
        Assert.Contains("\"areaHash\":43981", json);          // 0xABCD == 43981
        Assert.Contains("\"walkable\":\"" + System.Convert.ToBase64String(walk) + "\"", json);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/POE2Radar.Tests --filter TerrainMapPayloadTests`
Expected: FAIL — `TerrainMapPayload` does not exist.

- [ ] **Step 3: Create the Core payload helper**

Create `src/POE2Radar.Core/Remote/TerrainMapPayload.cs`:

```csharp
using System.Text.Json;

namespace POE2Radar.Core.Remote;

/// <summary>Serializes a walkable terrain grid into the JSON the /api/map endpoint returns: dimensions,
/// the area hash it belongs to, and the grid itself base64-encoded (one byte per cell, 0/1). Pure.</summary>
public static class TerrainMapPayload
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ToJson(byte[] walkable, int width, int height, uint areaHash)
        => JsonSerializer.Serialize(new
        {
            ready = true,
            areaHash,
            width,
            height,
            walkable = System.Convert.ToBase64String(walkable),
        }, Json);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/POE2Radar.Tests --filter TerrainMapPayloadTests`
Expected: PASS.

- [ ] **Step 5: Add CurrentTerrain() to RadarApp**

In `src/POE2Radar.Overlay/RadarApp.cs`, add a private method (near the other API provider methods; it reads the published world snapshot once so terrain + hash are consistent). First confirm the published field is `_world` and that `WorldSnapshot` exposes `Terrain` (a `Poe2Live.TerrainData?`) and `AreaHash` (`uint`) — per the construction `_world = new WorldSnapshot(true, areaHash, ..., _terrain, ...)` at ~line 1772:

```csharp
    /// <summary>Snapshot the current zone's walkable terrain + its area hash for the /api/map endpoint.
    /// Reads the published _world once (a single volatile ref) so terrain and hash are always a matched
    /// pair. Returns nulls until terrain is available (loading / no zone).</summary>
    private (byte[]? Walkable, int Width, int Height, uint AreaHash) CurrentTerrain()
    {
        var w = _world;                       // one volatile read → consistent terrain + hash
        var t = w?.Terrain;
        return t == null ? (null, 0, 0, 0u) : (t.Walkable, t.Width, t.Height, w!.AreaHash);
    }
```

- [ ] **Step 6: Pass the provider into ApiServer**

In the `new ApiServer(...)` call (~656-665), add `terrainProvider:` before `allowLanAccess:`:

```csharp
                             presetStore: _presetStore,
                             terrainProvider: CurrentTerrain,
                             allowLanAccess: _settings.AllowLanAccess,
                             port: _settings.ApiPort);
```

- [ ] **Step 7: Add the constructor param + field + cache to ApiServer**

In `src/POE2Radar.Overlay/Web/ApiServer.cs`:

(a) Add fields near `_lanBindFailed` (from Task 1):

```csharp
    private readonly Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)>? _terrainProvider;
    private uint _mapCacheHash;      // /api/map: 1-entry payload cache keyed by area hash (API loop is single-threaded)
    private string? _mapCacheJson;
```

(b) Add the constructor parameter immediately before `bool allowLanAccess = false`:

```csharp
        PresetStore? presetStore = null,
        Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)>? terrainProvider = null,
        bool allowLanAccess = false,
        int port = 7777)
```

(c) In the constructor body, assign it (near `_presetStore = ...`):

```csharp
        _terrainProvider = terrainProvider;
```

- [ ] **Step 8: Add the /api/map route**

In `ApiServer.Handle`, add the case (near `/entities`). Uses the Core `TerrainMapPayload` (already imported transitively via `using POE2Radar.Core.Remote;` added in Task 1):

```csharp
            case "/api/map":
            {
                var (walk, w, h, hash) = _terrainProvider?.Invoke() ?? (null, 0, 0, 0u);
                if (walk == null || w <= 0 || h <= 0) { Write(ctx, 200, "{\"ready\":false}"); break; }
                if (_mapCacheJson == null || _mapCacheHash != hash)
                {
                    _mapCacheJson = TerrainMapPayload.ToJson(walk, w, h, hash);
                    _mapCacheHash = hash;
                }
                Write(ctx, 200, _mapCacheJson);
                break;
            }
```

- [ ] **Step 9: Build + full test run**

Run: `dotnet build src/POE2Radar.Overlay -c Debug` → 0 CS errors.
Run: `dotnet test tests/POE2Radar.Tests` → all green (existing + the two new suites).

- [ ] **Step 10: Commit**

```bash
git add src/POE2Radar.Core/Remote/TerrainMapPayload.cs tests/POE2Radar.Tests/TerrainMapPayloadTests.cs src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Web/ApiServer.cs
git commit -m "feat(remote): /api/map terrain endpoint (base64 walkable, per-hash cache) from published world snapshot"
```

---

### Task 4: Web minimap page + route + dashboard card

**Files:**
- Create: `src/POE2Radar.Overlay/Web/MapPageHtml.cs`
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (`Handle` switch: add `case "/map"`)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (new "Web Minimap" card + copy handler)

**Interfaces:**
- Consumes: `GET /api/map` (Task 3), existing `GET /state` (`player{x,y}`, `areaHash`, `areaName`/`areaCode`), existing `GET /entities?limit=` (`x`,`y`,`rarity`,`poi`,`friendly`,`hpCur`,`hpMax`); `WriteHtml` helper.
- Produces: `GET /map` HTML page.

- [ ] **Step 1: Create the minimap page**

Create `src/POE2Radar.Overlay/Web/MapPageHtml.cs` (a self-contained top-down canvas page; mirrors `ObsOverlayHtml` structure):

```csharp
namespace POE2Radar.Overlay.Web;

internal static class MapPageHtml
{
    public const string Page = """
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>POE2GPS Minimap</title>
<style>
  html,body{margin:0;height:100%;background:#0a0a0c;overflow:hidden}
  #c{display:block;width:100vw;height:100vh}
  #hud{position:fixed;left:8px;top:6px;color:#8a8a90;font:12px Consolas,monospace;text-shadow:0 1px 2px #000;pointer-events:none}
  #z{position:fixed;right:8px;top:6px;display:flex;gap:6px}
  #z button{font:14px monospace;background:rgba(0,0,0,.5);color:#ccb96a;border:1px solid #554;border-radius:4px;width:28px;height:26px;cursor:pointer}
</style></head><body>
<canvas id="c"></canvas>
<div id="hud">connecting…</div>
<div id="z"><button id="zo">&minus;</button><button id="zi">+</button></div>
<script>
  const cv=document.getElementById('c'),ctx=cv.getContext('2d'),hud=document.getElementById('hud');
  const TAU=Math.PI*2;
  let scale=4, terrain=null, tw=0, th=0, thash=null, player={x:0,y:0}, ents=[];
  const RC={Normal:'#b9b9c0',Magic:'#6a8bff',Rare:'#ffd52e',Unique:'#ff7a1a'}; // monster rarity palette
  function fit(){ cv.width=innerWidth; cv.height=innerHeight; }
  addEventListener('resize',fit); fit();
  document.getElementById('zi').onclick=()=>scale=Math.min(16,scale+1);
  document.getElementById('zo').onclick=()=>scale=Math.max(1,scale-1);
  async function j(u){const r=await fetch(u,{cache:'no-store'});if(!r.ok)throw 0;return r.json();}
  function buildTerrain(b64,w,h){
    const bin=atob(b64), off=document.createElement('canvas'); off.width=w; off.height=h;
    const octx=off.getContext('2d'), img=octx.createImageData(w,h), d=img.data;
    for(let i=0;i<w*h;i++){ const o=i*4;
      if(bin.charCodeAt(i)!==0){ d[o]=34; d[o+1]=38; d[o+2]=52; d[o+3]=255; } else { d[o+3]=0; } }
    octx.putImageData(img,0,0); return off;
  }
  async function loadTerrain(){
    try{ const m=await j('/api/map'); if(!m.ready){ terrain=null; thash=null; return; }
      if(m.areaHash!==thash){ terrain=buildTerrain(m.walkable,m.width,m.height); tw=m.width; th=m.height; thash=m.areaHash; }
    }catch(e){}
  }
  async function tick(){
    try{
      const s=await j('/state'); if(s.player) player=s.player;
      if(s.areaHash!==thash) await loadTerrain();
      ents=await j('/entities?limit=600').catch(()=>[]);
      hud.textContent=(s.areaName||s.areaCode||'—')+'  ·  '+ents.length+' dots  ·  z'+scale;
    }catch(e){ hud.textContent='waiting for game…'; }
    draw();
  }
  function draw(){
    ctx.clearRect(0,0,cv.width,cv.height);
    const cx=cv.width/2, cy=cv.height/2;
    if(terrain){ ctx.imageSmoothingEnabled=false;
      ctx.drawImage(terrain, cx-player.x*scale, cy-player.y*scale, tw*scale, th*scale); }
    for(const e of ents){
      if(e.hpMax>0 && e.hpCur<=0) continue;                 // skip corpses
      const x=cx+(e.x-player.x)*scale, y=cy+(e.y-player.y)*scale;
      if(x<-4||y<-4||x>cv.width+4||y>cv.height+4) continue;  // off-canvas cull
      let col='#8a8a90';
      if(e.poi) col='#e0b341';
      else if(e.hpMax>0) col=RC[e.rarity]||'#cc5555';         // has health = monster
      else if(e.friendly) col='#55aadd';
      ctx.fillStyle=col; ctx.beginPath();
      ctx.arc(x,y, e.rarity==='Unique'?4:e.rarity==='Rare'?3:2.4, 0, TAU); ctx.fill();
    }
    ctx.fillStyle='#39d353'; ctx.beginPath(); ctx.arc(cx,cy,4,0,TAU); ctx.fill();
    ctx.strokeStyle='#0a0'; ctx.lineWidth=1.5; ctx.stroke();
  }
  loadTerrain(); tick(); setInterval(tick,1000);
</script></body></html>
""";
}
```

- [ ] **Step 2: Add the /map route**

In `ApiServer.Handle`, add next to the `/obs` case (~line 185):

```csharp
            case "/map":
                WriteHtml(ctx, MapPageHtml.Page);
                break;
```

- [ ] **Step 3: Add the "Web Minimap" dashboard card**

In `DashboardHtml.cs`, inside the settings view near the LAN card, add:

```html
          <div class="card collapsed" data-card="web-minimap">
            <h3>Web Minimap <small class="tag">&middot; second-screen map</small></h3>
            <div class="row"><div class="rl">Standalone minimap page<small>walkable terrain + live dots + your position &mdash; drop it fullscreen on a second monitor, phone, or Raspberry Pi</small></div>
              <span style="display:flex;gap:6px;align-items:center">
                <a class="addbtn" href="/map" target="_blank" style="width:auto;text-decoration:none">Open</a>
                <code id="mapUrl" style="font-size:12px;color:var(--gold-bright)">/map</code>
                <button class="addbtn" id="mapCopyUrl" style="width:auto">Copy</button>
              </span></div>
            <div class="row"><div class="rl hint-row">Only does work while a browser has it open &mdash; it costs nothing when nobody's viewing. Turn on Remote Access (LAN) above to open it from another device.</div></div>
          </div>
```

- [ ] **Step 4: Wire the copy button (idempotent)**

In `DashboardHtml.cs`, in `loadSettings()` (right after the `renderLanInfo();` call from Task 2), add an idempotent `onclick` binding (using `onclick=` so repeated `loadSettings()` calls don't stack handlers):

```javascript
    const mc=document.getElementById('mapCopyUrl'); if(mc) mc.onclick=()=>{ navigator.clipboard.writeText(location.origin+'/map').catch(()=>{}); const t=mc.textContent; mc.textContent='Copied!'; setTimeout(()=>mc.textContent=t,1200); };
```

- [ ] **Step 5: Build**

Run: `dotnet build src/POE2Radar.Overlay -c Debug`
Expected: 0 CS errors.

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Overlay/Web/MapPageHtml.cs src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(remote): /map web minimap page (top-down canvas) + dashboard card"
```

**Live smoke (record in report):** open `http://localhost:7777/map` while in a zone — terrain shape + monster/POI dots + green player blip appear and track movement; +/- zoom works; on zone change the terrain refreshes.

---

### Task 5: Integration sweep + version bump + compliance

**Files:**
- Modify: `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (`<Version>` → `0.17.0`)

**Interfaces:** none (release-prep task).

- [ ] **Step 1: Bump the version**

In `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj`, change:

```xml
    <Version>0.16.0</Version>
```
to
```xml
    <Version>0.17.0</Version>
```

- [ ] **Step 2: Full solution build**

Run: `dotnet build -c Debug`
Expected: 0 CS errors across Core / Overlay / Research / Tests.

- [ ] **Step 3: Full test run**

Run: `dotnet test tests/POE2Radar.Tests`
Expected: all green (prior suites + `ApiPrefixTests` + `TerrainMapPayloadTests`).

- [ ] **Step 4: Compliance gates**

Run: `pwsh scripts/compliance-gate.ps1` → PASS (no memory writes / input / pricing introduced).
Run: `pwsh scripts/scrub-strings.ps1 -SelfTest` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/POE2Radar.Overlay.csproj
git commit -m "chore(release): bump version to 0.17.0 (Remote Views)"
```

---

## Self-Review

**Spec coverage:**
- LAN access opt-in + conditional prefix + writes-stay-loopback + restart-to-apply + firewall note + bind-fallback → Tasks 1 & 2. ✓
- `/api/lan-info` + detected LAN URLs → Task 2. ✓
- Web minimap `/map` page (top-down, terrain + dots + player, reuses /entities + /state) → Tasks 3 & 4. ✓
- `/api/map` terrain endpoint (base64 walkable, per-hash cache, from already-read terrain) → Task 3. ✓
- Everything defaults off / zero-cost-when-unviewed → `AllowLanAccess=false` (Task 1); `/map` only works while polled (inherent). ✓
- Dashboard cards for both → Tasks 2 & 4. ✓
- Version 0.17.0, README badge unchanged, compliance green → Task 5. ✓
- Landmarks on the minimap: the spec lists them as optional/"if already serialized"; deliberately **deferred from v1** (the minimap ships terrain + entities + player; entity POI dots already cover the key markers). Not a gap — an explicit YAGNI scope call for the first cut.

**Placeholder scan:** No TBD/TODO; every code step contains complete code. ✓

**Type consistency:** `ApiPrefix.Build(bool,int)`, `TerrainMapPayload.ToJson(byte[],int,int,uint)`, `terrainProvider` tuple `(byte[]? Walkable,int Width,int Height,uint AreaHash)`, `CurrentTerrain()` returning the same tuple, `_mapCacheHash`/`_mapCacheJson`, `_allowLanAccess`/`_port`/`_lanBindFailed` — consistent across Tasks 1-4. The `new ApiServer(...)` named args (`terrainProvider:`, `allowLanAccess:`, `port:`) match the constructor param names. ✓
