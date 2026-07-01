# Streaming & Presence (v0.16.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Two "share your session outward" features on already-published data — an OBS browser-source overlay (`/obs`) + opt-in Discord Rich Presence (neutral app, customizable text).

**Architecture:** Two pure Core units (`PresenceTemplate` formatter, `DiscordIpc` named-pipe frame encoder/client) + a transparent HTML page served on localhost + a ~15 s RP cadence in RadarApp reading the published `RadarState`. Zero new offsets, read-only.

**Tech Stack:** C#/.NET 10 (`net10.0-windows`, x64), `System.IO.Pipes` + `System.Text.Json` (both built-in — no new NuGet), Vortice (unaffected), xUnit (Core tests), vanilla-JS pages.

## Global Constraints

- **Strictly read-only of the game** — no new memory reads/offsets, no input, no pricing. The two outward flows are opt-in + user-controlled: OBS stays on **localhost**; Discord RP publishes to the **local Discord IPC** only, under a **neutral** app identity (NOT "POE2GPS"), off by default.
- `TreatWarningsAsErrors=true` → 0/0. README badge stays `0.5.4`. Version → `0.16.0`.
- `Poe2Offsets.cs` unchanged. Compliance gate + scrub stay green.
- **Discord Client ID:** default **empty** → RP is inert until a Client ID is configured. Ryan registers a neutral app + provides the ID (embedded as default in a tiny follow-up); users can override in settings. This UN-gates the build — build everything now.
- **Validated seams (grounding):** route dispatch = `switch (path)` in `ApiServer.Handle()` (ApiServer.cs:175); `case "/": WriteHtml(ctx, DashboardHtml.Page);` (181). `DashboardHtml` = `internal static class { public const string Page = """…"""; }`. `/state` serializes a `session` object (ApiServer.cs:189-234) that currently OMITS kills/maps-hr/xp-eff. `SessionStats` has `KillsNormal/KillsMagic/KillsRare/KillsUnique/MapsPerHour/XpEfficiency/CurrentZoneName/CurrentAreaLevel`. `RadarState` has `CharLevel, AreaLevel, Session`. Settings whole-object pattern = `TryParseGroundItems` (ApiServer.cs:1350: deserialize→clamp→`catch(JsonException) return false`). Low-rate throttle idiom = `_nextSessionResetAt` (`DateTime.UtcNow >= …`) in `WorldTick`. `_state` reachable in RadarApp. Dispose at RadarApp.cs:3258. Core new folder `src/POE2Radar.Core/Presence/`, namespace `POE2Radar.Core.Presence`.

### Build & test
- Core: `dotnet build src/POE2Radar.Core/POE2Radar.Core.csproj -c Release` → 0/0.
- Overlay: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release` → 0/0. *(A file-copy lock on `Overlay.dll` = the running overlay, NOT a code error; 0 CS errors is what matters.)*
- Tests: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Release -p:Platform=x64` → all pass (baseline 256).

---

## File structure

| File | Action | Task |
|---|---|---|
| `src/POE2Radar.Core/Presence/PresenceTemplate.cs` | new (pure) | 1 |
| `tests/POE2Radar.Tests/PresenceTemplateTests.cs` | new | 1 |
| `src/POE2Radar.Core/Presence/DiscordIpc.cs` | new (frame encode + pipe) | 2 |
| `tests/POE2Radar.Tests/DiscordIpcTests.cs` | new | 2 |
| `src/POE2Radar.Overlay/Config/RadarSettings.cs` | `ObsOverlaySettings` + `DiscordPresenceSettings` | 3 |
| `src/POE2Radar.Overlay/Web/ApiServer.cs` | `/state` session ext + both settings round-trip + `/obs` route | 3,4 |
| `src/POE2Radar.Overlay/Web/ObsOverlayHtml.cs` | new (transparent page) | 4 |
| `src/POE2Radar.Overlay/Web/DashboardHtml.cs` | OBS card + Discord RP card | 4,5 |
| `src/POE2Radar.Overlay/RadarApp.cs` | RP cadence + DiscordIpc field + Dispose | 5 |
| `CHANGELOG.md`, `README.md`, `*.csproj` | release | 6 |

---

## Task 1: Core `PresenceTemplate` (pure)

**Files:** create `src/POE2Radar.Core/Presence/PresenceTemplate.cs`, `tests/POE2Radar.Tests/PresenceTemplateTests.cs`.

**Produces:** `static string PresenceTemplate.Format(string template, IReadOnlyDictionary<string,string> tokens)`. Consumed by Task 5.

- [ ] **Step 1: Failing tests.** `tests/POE2Radar.Tests/PresenceTemplateTests.cs`:
```csharp
using POE2Radar.Core.Presence;

public class PresenceTemplateTests
{
    static readonly Dictionary<string,string> Toks = new()
        { ["area"]="The Twilight Strand", ["level"]="92", ["mapshr"]="4.2", ["kills"]="137" };

    [Fact] public void Fills_known_tokens()
        => Assert.Equal("The Twilight Strand · Lvl 92",
             PresenceTemplate.Format("{area} · Lvl {level}", Toks));
    [Fact] public void Unknown_token_becomes_empty()
        => Assert.Equal("x  y", PresenceTemplate.Format("x {nope} y", Toks));
    [Fact] public void Clamps_to_128_chars()
    {
        var t = PresenceTemplate.Format(new string('a', 200), Toks);
        Assert.Equal(128, t.Length);
    }
    [Fact] public void Null_or_empty_template_is_empty()
    {
        Assert.Equal("", PresenceTemplate.Format("", Toks));
        Assert.Equal("", PresenceTemplate.Format(null!, Toks));
    }
}
```
- [ ] **Step 2: Run red** — `dotnet test … -p:Platform=x64` → fail (type missing).
- [ ] **Step 3: Implement.** `src/POE2Radar.Core/Presence/PresenceTemplate.cs`:
```csharp
using System.Text;
namespace POE2Radar.Core.Presence;

/// <summary>Fills <c>{token}</c> placeholders in a user presence template from a token map, and clamps
/// the result to Discord's 128-char per-line limit. Pure; unknown tokens resolve to empty. No I/O.</summary>
public static class PresenceTemplate
{
    public static string Format(string template, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var sb = new StringBuilder(template.Length);
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] == '{')
            {
                int close = template.IndexOf('}', i + 1);
                if (close > i)
                {
                    var key = template.Substring(i + 1, close - i - 1);
                    sb.Append(tokens.TryGetValue(key, out var v) ? v : "");
                    i = close;
                    continue;
                }
            }
            sb.Append(template[i]);
        }
        var s = sb.ToString();
        return s.Length > 128 ? s.Substring(0, 128) : s;
    }
}
```
- [ ] **Step 4: Run green.** build Core 0/0; tests pass (4 new).
- [ ] **Step 5: Commit** — `feat(core): PresenceTemplate token formatter (Discord presence)`.

---

## Task 2: Core `DiscordIpc` (frame encoder + pipe client)

**Files:** create `src/POE2Radar.Core/Presence/DiscordIpc.cs`, `tests/POE2Radar.Tests/DiscordIpcTests.cs`.

**Produces:** `DiscordIpc` (IDisposable): `static byte[] EncodeFrame(int opcode, string json)`; `bool TryConnect(string clientId)`; `void SetActivity(string details, string state, long? startUnixSec, string? largeImage, string? largeText)`; `void Clear()`; `Dispose()`. Consumed by Task 5.

- [ ] **Step 1: Failing tests** (only the pure `EncodeFrame` is unit-tested; pipe I/O needs Discord). `tests/POE2Radar.Tests/DiscordIpcTests.cs`:
```csharp
using System.Text;
using POE2Radar.Core.Presence;

public class DiscordIpcTests
{
    [Fact] public void EncodeFrame_has_le_opcode_and_length_header()
    {
        var frame = DiscordIpc.EncodeFrame(1, "{}");
        Assert.Equal(8 + 2, frame.Length);
        Assert.Equal(1, BitConverter.ToInt32(frame, 0));    // opcode LE
        Assert.Equal(2, BitConverter.ToInt32(frame, 4));    // payload length LE
        Assert.Equal("{}", Encoding.UTF8.GetString(frame, 8, 2));
    }
    [Fact] public void EncodeFrame_utf8_payload_length_is_byte_count_not_char_count()
    {
        var json = "{\"s\":\"éé\"}";              // 2 two-byte chars
        var frame = DiscordIpc.EncodeFrame(0, json);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), BitConverter.ToInt32(frame, 4));
    }
    [Fact] public void TryConnect_returns_false_when_discord_absent_and_never_throws()
    {
        using var ipc = new DiscordIpc();
        // In CI/dev there is no Discord pipe; must return false, not throw.
        var ok = ipc.TryConnect("000000000000000000");
        Assert.False(ok);
    }
}
```
- [ ] **Step 2: Run red.**
- [ ] **Step 3: Implement.** `src/POE2Radar.Core/Presence/DiscordIpc.cs`:
```csharp
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace POE2Radar.Core.Presence;

/// <summary>Minimal Discord Rich Presence over the local IPC named pipe (\\.\pipe\discord-ipc-0..9).
/// Frame = int32 opcode (LE) + int32 length (LE) + UTF-8 JSON. Handshake op 0 {v:1,client_id}; update
/// op 1 SET_ACTIVITY. Read-only w.r.t. the game; publishes ONLY to the local Discord client, opt-in.
/// Never throws out of the public surface — a missing/closed Discord degrades to "not connected".</summary>
public sealed class DiscordIpc : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private int _nonce;
    public bool Connected => _pipe is { IsConnected: true };

    public static byte[] EncodeFrame(int opcode, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), opcode);         // little-endian on x64/Windows
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    /// <summary>Connect to the first available discord-ipc pipe + handshake. Returns false (no throw) if
    /// Discord isn't running or the client id is empty.</summary>
    public bool TryConnect(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        Dispose();
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(200);   // 200ms per candidate
                var hello = JsonSerializer.Serialize(new { v = 1, client_id = clientId });
                var frame = EncodeFrame(0, hello);
                pipe.Write(frame, 0, frame.Length);
                DrainOne(pipe);      // read the READY response (best-effort)
                _pipe = pipe;
                return true;
            }
            catch { /* try next pipe index */ }
        }
        return false;
    }

    /// <summary>Push a SET_ACTIVITY. No-op if not connected; on write failure, drops the connection so the
    /// caller can reconnect next cycle.</summary>
    public void SetActivity(string details, string state, long? startUnixSec, string? largeImage, string? largeText)
    {
        if (_pipe is not { IsConnected: true } pipe) return;
        object? timestamps = startUnixSec is { } t ? new { start = t } : null;
        object? assets = largeImage != null ? new { large_image = largeImage, large_text = largeText ?? "" } : null;
        var activity = new { details = Trim(details), state = Trim(state), timestamps, assets };
        var msg = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = (++_nonce).ToString(),
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        try { var f = EncodeFrame(1, msg); pipe.Write(f, 0, f.Length); DrainOne(pipe); }
        catch { Dispose(); }
    }

    public void Clear()
    {
        if (_pipe is not { IsConnected: true } pipe) return;
        var msg = JsonSerializer.Serialize(new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity = (object?)null }, nonce = (++_nonce).ToString() });
        try { var f = EncodeFrame(1, msg); pipe.Write(f, 0, f.Length); } catch { }
    }

    private static string? Trim(string? s) => string.IsNullOrEmpty(s) ? null : (s.Length > 128 ? s.Substring(0, 128) : s);

    private static void DrainOne(NamedPipeClientStream pipe)
    {
        // Best-effort read of one response frame (8-byte header + payload) so the pipe buffer doesn't stall.
        try { var hdr = new byte[8]; if (pipe.Read(hdr, 0, 8) == 8) { var len = BitConverter.ToInt32(hdr, 4); if (len is > 0 and < 65536) { var buf = new byte[len]; pipe.Read(buf, 0, len); } } }
        catch { }
    }

    public void Dispose()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }
}
```
- [ ] **Step 4: Run green.** build Core 0/0; tests pass (3 new — the `TryConnect` test asserts graceful false without Discord).
- [ ] **Step 5: Commit** — `feat(core): DiscordIpc — named-pipe RP frame encoder + client (graceful, opt-in)`.

---

## Task 3: Settings + `/state` session extension + API round-trip

**Files:** `RadarSettings.cs`, `ApiServer.cs`.

- [ ] **Step 1: Settings classes.** In `RadarSettings.cs` add `public ObsOverlaySettings ObsOverlay { get; set; } = new();` + `public DiscordPresenceSettings DiscordPresence { get; set; } = new();` and the classes:
```csharp
public sealed class ObsOverlaySettings
{
    public bool ShowSessionTimer { get; set; } = true;
    public bool ShowZoneTimer { get; set; } = true;
    public bool ShowArea { get; set; } = true;
    public bool ShowKills { get; set; } = true;
    public bool ShowMapsHr { get; set; } = true;
    public bool ShowXpEff { get; set; } = false;
    public bool ShowObjective { get; set; } = false;
    public string TextColor { get; set; } = "#FFFFFF";
    public int PanelOpacity { get; set; } = 40;   // 0-100 (0 = no chip background)
    public float Scale { get; set; } = 1.0f;       // 0.5-3.0
    public string Corner { get; set; } = "top-left";
}
public sealed class DiscordPresenceSettings
{
    public bool Enabled { get; set; }                        // opt-in, default OFF
    public string ClientId { get; set; } = "";               // EMPTY → RP inert until set (Ryan's neutral app id)
    public string DetailsTemplate { get; set; } = "{area}";
    public string StateTemplate { get; set; } = "Level {level} · {mapshr} maps/hr";
    public bool ShowTimer { get; set; } = true;
}
```
- [ ] **Step 2: Extend `/state` session projection.** In `ApiServer.cs` (the `case "/state"` `session = … new { … }` block, ~line 224-233) ADD the v2 fields so the OBS page can render them:
```csharp
            killsNormal = s.Session.KillsNormal,
            killsMagic  = s.Session.KillsMagic,
            killsRare   = s.Session.KillsRare,
            killsUnique = s.Session.KillsUnique,
            mapsPerHour = s.Session.MapsPerHour,
            xpEfficiency = s.Session.XpEfficiency,
```
  (append inside the existing `session = new { … }` object.)
- [ ] **Step 3: API round-trip.** `ReadSettings()` add `obsOverlay = _settings.ObsOverlay, discordPresence = _settings.DiscordPresence`. `ApplySettings` add two whole-object cases mirroring `TryParseGroundItems`:
```csharp
case "obsOverlay" when p.Value.ValueKind == JsonValueKind.Object:
    if (TryParseObsOverlay(p.Value, out var obs)) { _settings.ObsOverlay = obs; applied.Add(p.Name); }
    break;
case "discordPresence" when p.Value.ValueKind == JsonValueKind.Object:
    if (TryParseDiscordPresence(p.Value, out var dp)) { _settings.DiscordPresence = dp; applied.Add(p.Name); }
    break;
```
  and the two parsers (mirror `TryParseGroundItems` — deserialize, clamp `PanelOpacity` 0-100 / `Scale` 0.5-3.0, cap template lengths to 128, `ClientId` to `[0-9]{0,32}` trimmed, `catch(JsonException) return false`).
- [ ] **Step 4: Build + test** (Overlay 0/0; 256 pass). **Commit** — `feat(overlay): ObsOverlay + DiscordPresence settings + /state v2-session fields`.

---

## Task 4: OBS overlay page + `/obs` route + dashboard card

**Files:** create `Web/ObsOverlayHtml.cs`; `ApiServer.cs`; `DashboardHtml.cs`.

- [ ] **Step 1: The page.** Create `src/POE2Radar.Overlay/Web/ObsOverlayHtml.cs` mirroring `DashboardHtml`'s shape:
```csharp
namespace POE2Radar.Overlay.Web;
internal static class ObsOverlayHtml
{
    public const string Page = """
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>POE2GPS OBS</title>
<style>
  html,body{margin:0;background:transparent;color:#fff;font:600 20px/1.35 Consolas,monospace;
    text-shadow:0 1px 2px #000,0 0 4px #000}
  #wrap{position:fixed;padding:10px 14px;display:flex;flex-direction:column;gap:2px}
  .chip{background:rgba(0,0,0,var(--op,.4));border-radius:6px;padding:2px 8px}
  .k{opacity:.7;margin-right:6px}
</style></head><body><div id="wrap"></div>
<script>
  async function j(u){const r=await fetch(u,{cache:'no-store'});if(!r.ok)throw 0;return r.json();}
  let cfg={showSessionTimer:true,showArea:true,showKills:true,showMapsHr:true};
  async function loadCfg(){try{const s=await j('/api/settings');cfg=s.obsOverlay||cfg;applyStyle(cfg);}catch(e){}}
  function applyStyle(c){const w=document.getElementById('wrap');
    w.style.setProperty('--op',(c.panelOpacity??40)/100);
    w.style.color=c.textColor||'#fff'; w.style.transform='scale('+(c.scale||1)+')';
    w.style.transformOrigin=(c.corner||'top-left').includes('right')?'top right':'top left';
    const right=(c.corner||'').includes('right'), bottom=(c.corner||'').includes('bottom');
    w.style.left=right?'auto':'0'; w.style.right=right?'0':'auto';
    w.style.top=bottom?'auto':'0'; w.style.bottom=bottom?'0':'auto';
    w.style.alignItems=right?'flex-end':'flex-start';}
  function row(label,val){return '<div class="chip"><span class="k">'+label+'</span>'+val+'</div>';}
  async function tick(){
    try{const s=await j('/state');const se=s.session||{};const out=[];
      if(cfg.showSessionTimer&&se.sessionElapsed)out.push(row('SESS',se.sessionElapsed));
      if(cfg.showZoneTimer&&se.zoneElapsed)out.push(row('ZONE',se.zoneElapsed));
      if(cfg.showArea)out.push(row('AREA',(s.areaName||s.areaCode||'—')+' · '+(s.areaLevel||0)));
      if(cfg.showKills)out.push(row('KILLS','N'+(se.killsNormal||0)+' M'+(se.killsMagic||0)+' R'+(se.killsRare||0)+' U'+(se.killsUnique||0)));
      if(cfg.showMapsHr)out.push(row('MAPS/HR',(se.mapsPerHour||0).toFixed(1)));
      if(cfg.showXpEff&&se.xpEfficiency!==undefined)out.push(row('XP',(se.xpEfficiency>0?'+':'')+se.xpEfficiency));
      if(cfg.showObjective&&s.campaignGps)out.push(row('NEXT',s.campaignGps));
      document.getElementById('wrap').innerHTML=out.join('');
    }catch(e){}
  }
  loadCfg(); setInterval(loadCfg,5000); tick(); setInterval(tick,1000);
</script></body></html>
""";
}
```
- [ ] **Step 2: Route.** In `ApiServer.Handle()` `switch (path)`, add beside `case "/"`:
```csharp
case "/obs":
    WriteHtml(ctx, ObsOverlayHtml.Page);
    break;
```
- [ ] **Step 3: Dashboard card.** In `DashboardHtml.cs` add an "OBS Overlay" settings card: the widget checkboxes (`showSessionTimer/showZoneTimer/showArea/showKills/showMapsHr/showXpEff/showObjective`), text-colour + opacity + scale + corner controls, and a **copy-able `http://localhost:7777/obs` URL** + a one-line "Add a Browser Source in OBS pointing here (transparent)". POST them as the whole `obsOverlay` object (mirror how a whole-object settings card like GroundItems/HpBars saves).
- [ ] **Step 4: Build + test** (0/0; 256). **Commit** — `feat(overlay): OBS browser-source overlay (/obs page + route + dashboard card)`.

**Live check (Task 6):** add `localhost:7777/obs` as an OBS Browser Source (transparent); the selected widgets update live over gameplay.

---

## Task 5: Discord RP wiring (cadence + feed + dispose + dashboard card)

**Files:** `RadarApp.cs`, `DashboardHtml.cs`.

- [ ] **Step 1: Field + dispose.** In `RadarApp` add `private readonly POE2Radar.Core.Presence.DiscordIpc _discordIpc = new();`, `private DateTime _nextDiscordUpdateAt = DateTime.MinValue;`, `private string _lastPresenceKey = "";`. In `Dispose()` (RadarApp.cs:3258) add `try { _discordIpc.Clear(); } catch {}` then `_discordIpc.Dispose();`.
- [ ] **Step 2: Cadence in WorldTick.** Add a helper and call it once per world tick (it self-throttles):
```csharp
private void UpdateDiscordPresence()
{
    var cfg = _settings.DiscordPresence;
    if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.ClientId)) { return; }   // inert until opted-in + id set
    if (DateTime.UtcNow < _nextDiscordUpdateAt) return;
    _nextDiscordUpdateAt = DateTime.UtcNow.AddSeconds(15);   // Discord rate limit

    if (!_discordIpc.Connected && !_discordIpc.TryConnect(cfg.ClientId)) return;   // Discord not running → skip

    var st = _state;   // the published RadarState (read directly, in-process)
    var sess = st.Session;
    var toks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["area"]   = ZoneGuide.Shared.FriendlyName(st.AreaCode),
        ["level"]  = st.CharLevel.ToString(),
        ["zones"]  = (sess?.ZonesEntered ?? 0).ToString(),
        ["mapshr"] = (sess?.MapsPerHour ?? 0).ToString("F1"),
        ["kills"]  = ((sess?.KillsNormal ?? 0) + (sess?.KillsMagic ?? 0) + (sess?.KillsRare ?? 0) + (sess?.KillsUnique ?? 0)).ToString(),
        ["xpeff"]  = (sess?.XpEfficiency ?? 0).ToString("+#;-#;0"),
    };
    var details = POE2Radar.Core.Presence.PresenceTemplate.Format(cfg.DetailsTemplate, toks);
    var state   = POE2Radar.Core.Presence.PresenceTemplate.Format(cfg.StateTemplate, toks);
    long? start = cfg.ShowTimer ? _sessionStartUnix : null;   // see Step 3
    var key = details + "|" + state + "|" + (start?.ToString() ?? "");
    if (key == _lastPresenceKey) return;   // no change → don't spam
    _lastPresenceKey = key;
    _discordIpc.SetActivity(details, state, start, largeImage: null, largeText: null);
}
```
  Call `UpdateDiscordPresence();` inside `WorldTick` near the other per-tick maintenance (guarded — it early-outs cheaply when disabled). If `_state` isn't directly a field, read it via the same source the `() => _state` API lambda uses.
- [ ] **Step 3: Session start timestamp.** Add `private readonly long _sessionStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();` (set in ctor). Used as the RP elapsed-timer start. *(Simplest correct behavior: presence timer = since app launch. If a `SessionTracker` reset should reset it too, that's a future nicety — YAGNI here.)*
- [ ] **Step 4: Dashboard card.** In `DashboardHtml.cs` add a "Discord Rich Presence *(opt-in)*" card: `enabled` toggle, `clientId` text field (with a note: "paste your neutral Discord app's Client ID; leave blank to disable"), `detailsTemplate` + `stateTemplate` text inputs with a **token legend** (`{area} {level} {zones} {mapshr} {kills} {xpeff}`), `showTimer` toggle, and a small live "current presence" preview computed client-side from `/state`. POST as the whole `discordPresence` object.
- [ ] **Step 5: Build + test** (0/0; 256). **Commit** — `feat(overlay): Discord Rich Presence cadence + feed + dashboard card`.

**Live check (Task 6):** with a Client ID set + RP enabled + Discord running, your Discord status shows the templated presence with an elapsed timer, updating ≤ every 15 s; disabling clears it.

---

## Task 6: Integration sweep + release

**Files:** `*.csproj` (version), `CHANGELOG.md`, `README.md`.

- [ ] **Step 1:** Full Core + Overlay build (0/0), `dotnet test` (all pass), `compliance-gate.ps1` + `scrub -SelfTest` (green). Confirm branch diff has NO input/process-write/pricing symbols and `Poe2Offsets.cs` unchanged; confirm no game reads were added (both features consume existing state).
- [ ] **Step 2:** Version → `0.16.0`.
- [ ] **Step 3:** CHANGELOG `## [0.16.0]` (ALL-OUT themed — bold/emoji/symmetry): OBS browser-source overlay + Discord Rich Presence (opt-in, neutral, customizable). README feature bullets. Note the Discord Client-ID one-time setup. Badge stays `0.5.4`.
- [ ] **Step 4:** Final whole-branch review (Sonnet). Then live smoke (OBS source + Discord RP), commit `chore(release): v0.16.0 - Streaming & Presence`, merge to `main` (handle stale tag), tag `v0.16.0`, push (CI release), themed Discord post, memory update. **Follow-up (non-blocking):** embed Ryan's neutral Client ID as the default once he provides it.

---

## Self-review

**Spec coverage:** OBS overlay (page T4 + route T4 + widgets/theme settings T3/T4 + /state v2 fields T3) ✓; Discord RP (PresenceTemplate T1 + DiscordIpc T2 + cadence/feed T5 + neutral client-id-empty-default T3/T5 + customizable templates T3/T5 + settings/dashboard T3/T5) ✓; compliance/read-only (all tasks) ✓; release T6. All spec sections covered.

**Placeholder scan:** No TBD/TODO. Pure Core units (PresenceTemplate, DiscordIpc) carry full code + test cases. Overlay wiring names exact anchors (route switch @181, /state session block @224, TryParseGroundItems @1350, _nextSessionResetAt throttle idiom, Dispose @3258) + mirrors the whole-object settings pattern. The `_state` read is flagged ("via the same source the API lambda uses") for the implementer to confirm the field.

**Type consistency:** `PresenceTemplate.Format(string, IReadOnlyDictionary<string,string>) → string` (T1) → used T5. `DiscordIpc.EncodeFrame(int,string)→byte[]`, `TryConnect(string)→bool`, `SetActivity(string,string,long?,string?,string?)`, `Clear()`, `Connected`, `Dispose()` (T2) → used T5. `ObsOverlaySettings`/`DiscordPresenceSettings` fields (T3) → consumed by ObsOverlayHtml JS + ApplySettings (T3/T4) + UpdateDiscordPresence (T5). `/state` session v2 fields `killsNormal/…/mapsPerHour/xpEfficiency` (T3) → read by ObsOverlayHtml (T4). Token names `{area}{level}{zones}{mapshr}{kills}{xpeff}` consistent between the RP feed (T5) and the dashboard legend (T5). Consistent throughout.
