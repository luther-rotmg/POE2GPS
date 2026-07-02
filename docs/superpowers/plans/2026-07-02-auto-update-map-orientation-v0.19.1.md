# v0.19.1 — Auto-Update & True-North Map — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the web minimap render in the same isometric orientation as the in-game overlay, and add an opt-out-able silent self-updater that downloads + verifies + swaps `Overlay.exe` from our GitHub releases (shipping `CHANGELOG.md` next to the exe).

**Architecture:** The map fix is a self-contained rewrite of the `MapPageHtml.cs` draw path applying the verified `MapProjection` isometric transform. Auto-update splits into a pure unit-tested Core policy (`AutoUpdatePolicy`) and a thin Overlay IO layer (`AutoUpdater`) that stages downloads for next-launch (Chrome-style), verifies by SHA-256, swaps `Overlay.exe` in-process (safe because the running process is a hardlink), and relaunches via `CreateProcess` (inherits admin elevation). A new `AutoUpdate.Mode` setting (default `silent`) gates it.

**Tech Stack:** .NET 10, C#, x64, Windows. `System.Net.Http`, `System.IO.Compression`, `System.Security.Cryptography`. Canvas 2D. xUnit.

## Global Constraints

- **Strictly READ-ONLY of the game.** No injection; no `WriteProcessMemory`/`VirtualProtectEx`/`CreateRemoteThread`; no input emission (`SendInput`/`PostMessage`/`keybd_event`). The updater touches **only POE2GPS's own files and its own process** — never PoE2.
- No pricing / poe.ninja / trade / reward-values anywhere.
- API writes stay loopback-gated on `RemoteEndPoint.Address` (`IsLoopback`), never the Host header. The new `autoUpdate` setting round-trips through the existing already-gated `/api/settings` POST.
- README **supports PoE2 0.5.4** badge unchanged.
- CI gates must pass: `scripts/compliance-gate.ps1` and `scripts/scrub-strings.ps1 -SelfTest`. (`Process.Start`, `File.Move`, `HttpClient`, `ZipFile` are not in the gate's symbol set.)
- Version → **0.19.1** in `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj`.
- All SDD implementer/reviewer subagents run on **Sonnet** (`model: 'sonnet'`) — Opus's cyber-classifier trips on memory-RE content.
- Branch: `feat/v0.19.1-autoupdate-map`.
- Release asset naming is authoritative: zip = `POE2GPS-{tag}-win-x64.zip`, checksum = `POE2GPS-{tag}-sha256.txt`, where `{tag}` is the **raw git tag with the `v`** (e.g. `v0.20.0`). Repo = `luther-rotmg/POE2GPS`.

---

### Task 1: True-North Map (isometric web minimap + view toggle)

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/MapPageHtml.cs` (the embedded `Page` HTML/JS string)

**Interfaces:**
- Consumes: `/api/map` (`{walkable, width, height, areaHash, ready}`), `/state` (`player {x,y}` in grid units), `/entities?limit=600` (`x,y` in grid units).
- Produces: nothing consumed by later tasks (self-contained client-side page).

Verified facts (do not re-derive): `/entities` and `/state.player` are both grid units (same space; direct subtraction valid). In-game projection is `sx = scale·(dx−dy)·COS`, `sy = scale·(−(dx+dy))·SIN`, `COS = 0.780430`, `SIN = 0.625243`. Map is fixed-north (no heading rotation). Terrain bitmap is row-major `[gridY*width+gridX]`, origin = grid (0,0).

- [ ] **Step 1: Add the projector + view state to the script**

In `MapPageHtml.cs`, inside the `<script>` block, replace the constants/state line (currently `const TAU=Math.PI*2;`) region so it reads:

```js
  const cv=document.getElementById('c'),ctx=cv.getContext('2d'),hud=document.getElementById('hud');
  const TAU=Math.PI*2;
  const COS=0.780430, SIN=0.625243;                         // cos/sin(38.7°) — mirrors MapProjection.cs
  let mapView=localStorage.getItem('poe2gps.mapView')||'iso'; // 'iso' (default, matches in-game) | 'top'
  let scale=4, terrain=null, tw=0, th=0, thash=null, player={x:0,y:0}, ents=[], areaLabel='—';
  const RC={Normal:'#b9b9c0',Magic:'#6a8bff',Rare:'#ffd52e',Unique:'#ff7a1a'}; // monster rarity palette
  // Grid delta (dx,dy) -> canvas delta. Isometric matches the in-game overlay; top-down keeps the old axis-aligned look.
  function proj(dx,dy){ return mapView==='iso'
      ? { sx: scale*(dx-dy)*COS, sy: scale*(-(dx+dy))*SIN }
      : { sx: dx*scale,          sy: dy*scale }; }
```

- [ ] **Step 2: Add the view-toggle button**

Change the zoom-button container markup from:

```html
<div id="z"><button id="zo">&minus;</button><button id="zi">+</button></div>
```
to:
```html
<div id="z"><button id="vt" title="Isometric / Top-down">◇</button><button id="zo">&minus;</button><button id="zi">+</button></div>
```

And after the existing `zi/zo` onclick handlers add:

```js
  document.getElementById('vt').onclick=()=>{ mapView=(mapView==='iso'?'top':'iso'); localStorage.setItem('poe2gps.mapView',mapView); draw(); };
```

- [ ] **Step 3: Rewrite `draw()` to project terrain and dots**

Replace the entire `function draw(){ ... }` body with:

```js
  function draw(){
    ctx.setTransform(1,0,0,1,0,0);                          // reset any prior transform
    ctx.clearRect(0,0,cv.width,cv.height);
    const cx=cv.width/2, cy=cv.height/2;
    if(terrain){
      ctx.imageSmoothingEnabled=false;
      if(mapView==='iso'){
        // Warp the grid-space bitmap into isometric screen-space (same affine as the in-game overlay).
        const p00=proj(-player.x,-player.y);
        ctx.save();
        ctx.setTransform(COS*scale, -SIN*scale, -COS*scale, -SIN*scale, cx+p00.sx, cy+p00.sy);
        ctx.drawImage(terrain, 0, 0, tw, th);
        ctx.restore();
      } else {
        ctx.drawImage(terrain, cx-player.x*scale, cy-player.y*scale, tw*scale, th*scale);
      }
    }
    for(const e of ents){
      if(e.hpMax>0 && e.hpCur<=0) continue;                 // skip corpses
      const d=proj(e.x-player.x, e.y-player.y);
      const x=cx+d.sx, y=cy+d.sy;
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
    hud.textContent=areaLabel+'  ·  '+ents.length+'  dots  ·  '+(mapView==='iso'?'iso':'top')+'  ·  z'+scale;
  }
```

- [ ] **Step 4: Build the overlay**

Run: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Debug`
Expected: `Build succeeded`, `0 Error(s)`. (MSB3026/MSB3027 copy-lock warnings are not errors — the running overlay locks the DLLs — ignore them.)

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Web/MapPageHtml.cs
git commit -m "feat(map): isometric web minimap matching in-game orientation + iso/top-down toggle"
```

Manual verification (in-game, not a build gate): open `/map`, confirm terrain + dots align with the in-game overlay orientation; the ◇ button flips iso↔top-down and the choice persists across reload.

---

### Task 2: Core `AutoUpdatePolicy` (pure decisions + tests)

**Files:**
- Create: `src/POE2Radar.Core/Update/AutoUpdatePolicy.cs`
- Test: `tests/POE2Radar.Tests/AutoUpdatePolicyTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (used by Task 3 `AutoUpdater`):
  - `record UpdateState(string TargetVersion, int Failures)`
  - `static bool IsNewer(string current, string latest)`
  - `static string AssetName(string tag)` → `POE2GPS-{tag}-win-x64.zip`
  - `static string ChecksumAssetName(string tag)` → `POE2GPS-{tag}-sha256.txt`
  - `static string ZipUrl(string repo, string tag)` / `static string ChecksumUrl(string repo, string tag)`
  - `static string? SelectAsset(IEnumerable<(string Name,string Url)> assets, string tag)`
  - `static bool ShouldAttempt(UpdateState? state, string latest, int maxFailures = 2)`
  - `static string? ExpectedSha(string checksumFileText, string assetName)`

- [ ] **Step 1: Write the failing tests**

Create `tests/POE2Radar.Tests/AutoUpdatePolicyTests.cs`:

```csharp
using POE2Radar.Core.Update;

public class AutoUpdatePolicyTests
{
    [Theory]
    [InlineData("0.19.0", "0.19.1", true)]
    [InlineData("0.19.1", "0.19.1", false)]
    [InlineData("0.20.0", "0.19.9", false)]
    [InlineData("v0.19.0", "v0.20.0", true)]     // tolerant of a leading v on either side
    [InlineData("0.19.0", "garbage", false)]     // unparseable latest -> not newer
    public void IsNewer_compares_semver(string cur, string latest, bool expected)
        => Assert.Equal(expected, AutoUpdatePolicy.IsNewer(cur, latest));

    [Fact]
    public void AssetName_and_urls_keep_the_v_prefix()
    {
        Assert.Equal("POE2GPS-v0.20.0-win-x64.zip", AutoUpdatePolicy.AssetName("v0.20.0"));
        Assert.Equal("POE2GPS-v0.20.0-sha256.txt", AutoUpdatePolicy.ChecksumAssetName("v0.20.0"));
        Assert.Equal("https://github.com/luther-rotmg/POE2GPS/releases/download/v0.20.0/POE2GPS-v0.20.0-win-x64.zip",
                     AutoUpdatePolicy.ZipUrl("luther-rotmg/POE2GPS", "v0.20.0"));
        Assert.Equal("https://github.com/luther-rotmg/POE2GPS/releases/download/v0.20.0/POE2GPS-v0.20.0-sha256.txt",
                     AutoUpdatePolicy.ChecksumUrl("luther-rotmg/POE2GPS", "v0.20.0"));
    }

    [Fact]
    public void SelectAsset_matches_by_name_case_insensitive()
    {
        var assets = new[] { ("readme.md","u0"), ("POE2GPS-V0.20.0-WIN-X64.ZIP","u1"), ("other.zip","u2") };
        Assert.Equal("u1", AutoUpdatePolicy.SelectAsset(assets, "v0.20.0"));
        Assert.Null(AutoUpdatePolicy.SelectAsset(assets, "v0.99.0"));
    }

    [Fact]
    public void ShouldAttempt_blocks_only_after_maxFailures_on_same_target()
    {
        Assert.True(AutoUpdatePolicy.ShouldAttempt(null, "v0.20.0"));
        Assert.True(AutoUpdatePolicy.ShouldAttempt(new("v0.20.0", 1), "v0.20.0"));
        Assert.False(AutoUpdatePolicy.ShouldAttempt(new("v0.20.0", 2), "v0.20.0"));
        Assert.True(AutoUpdatePolicy.ShouldAttempt(new("v0.20.0", 2), "v0.21.0")); // new target resets
    }

    [Fact]
    public void ExpectedSha_parses_sha256sum_lines()
    {
        var txt = "abc123  POE2GPS-v0.20.0-win-x64.zip\ndef456  other.txt\n";
        Assert.Equal("abc123", AutoUpdatePolicy.ExpectedSha(txt, "POE2GPS-v0.20.0-win-x64.zip"));
        Assert.Null(AutoUpdatePolicy.ExpectedSha(txt, "missing.zip"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~AutoUpdatePolicyTests`
Expected: FAIL to compile — `AutoUpdatePolicy` does not exist.

- [ ] **Step 3: Implement `AutoUpdatePolicy`**

Create `src/POE2Radar.Core/Update/AutoUpdatePolicy.cs`:

```csharp
namespace POE2Radar.Core.Update;

/// <summary>
/// Pure decision logic for the self-updater — no IO, no game reads. All the drift-prone / spoof-prone
/// choices (is a release newer? which asset URL? which checksum line? should we retry a failing target?)
/// live here so they can be unit-tested. The Overlay <c>AutoUpdater</c> does the actual download/swap.
/// </summary>
public static class AutoUpdatePolicy
{
    /// <summary>Per-target retry state persisted next to the exe (JSON). A NEW target resets the gate.</summary>
    public record UpdateState(string TargetVersion, int Failures);

    /// <summary>True iff <paramref name="latest"/> is a strictly higher semver than <paramref name="current"/>.</summary>
    public static bool IsNewer(string current, string latest)
    {
        var c = Parse(current); var l = Parse(latest);
        if (c is null || l is null) return false;
        for (var i = 0; i < 3; i++) if (l[i] != c[i]) return l[i] > c[i];
        return false;
    }

    /// <summary>Release zip asset name — uses the RAW tag (with its leading v), matching release.yml.</summary>
    public static string AssetName(string tag) => $"POE2GPS-{tag}-win-x64.zip";

    /// <summary>SHA-256 checksum asset name for a release.</summary>
    public static string ChecksumAssetName(string tag) => $"POE2GPS-{tag}-sha256.txt";

    public static string ZipUrl(string repo, string tag)
        => $"https://github.com/{repo}/releases/download/{tag}/{AssetName(tag)}";

    public static string ChecksumUrl(string repo, string tag)
        => $"https://github.com/{repo}/releases/download/{tag}/{ChecksumAssetName(tag)}";

    /// <summary>Find the download URL of the release zip asset for <paramref name="tag"/> (case-insensitive).</summary>
    public static string? SelectAsset(IEnumerable<(string Name, string Url)> assets, string tag)
    {
        var want = AssetName(tag);
        foreach (var a in assets)
            if (string.Equals(a.Name, want, StringComparison.OrdinalIgnoreCase)) return a.Url;
        return null;
    }

    /// <summary>Don't keep retrying a target that has already failed <paramref name="maxFailures"/> times.</summary>
    public static bool ShouldAttempt(UpdateState? state, string latest, int maxFailures = 2)
    {
        if (state is null) return true;
        if (!string.Equals(state.TargetVersion, latest, StringComparison.OrdinalIgnoreCase)) return true;
        return state.Failures < maxFailures;
    }

    /// <summary>Parse a sha256sum-format file for the hash of <paramref name="assetName"/> ("&lt;hash&gt;␠␠&lt;file&gt;").</summary>
    public static string? ExpectedSha(string checksumFileText, string assetName)
    {
        foreach (var raw in checksumFileText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var hash = line[..sp];
            var file = line[(sp + 1)..].TrimStart(' ', '*'); // sha256sum uses two spaces / '*' for binary mode
            if (string.Equals(file, assetName, StringComparison.OrdinalIgnoreCase)) return hash;
        }
        return null;
    }

    private static int[]? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        var parts = s.Split('.', '-');
        var v = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length || !int.TryParse(parts[i], out v[i])) { if (i == 0) return null; v[i] = 0; }
        }
        return v;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~AutoUpdatePolicyTests`
Expected: PASS (all AutoUpdatePolicyTests green).

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Update/AutoUpdatePolicy.cs tests/POE2Radar.Tests/AutoUpdatePolicyTests.cs
git commit -m "feat(update): pure AutoUpdatePolicy (semver gate, asset/url/checksum selection, retry state) + tests"
```

---

### Task 3: Overlay `AutoUpdater` (download / verify / stage / apply / rollback)

**Files:**
- Create: `src/POE2Radar.Overlay/Update/AutoUpdater.cs`

**Interfaces:**
- Consumes: `AutoUpdatePolicy` (Task 2); `UpdateChecker.CheckAsync()`, `UpdateChecker.Current` (existing, `POE2Radar.Overlay.UpdateChecker`).
- Produces (used by Task 5 `Program.cs`/`RadarApp`):
  - `static Task CheckAndStageAsync(string mode, string current, string installDir, CancellationToken ct)`
  - `static bool ApplyStagedIfPresent(string current, string installDir)` — returns true if it relaunched (caller returns 0)
  - `static bool RollbackIfCrashLooping(string installDir)` — returns true if it relaunched
  - `static void ConfirmHealthy(string installDir)`
  - `static string? PendingVersion(string installDir)` — the staged/target version, or null

Key mechanics (verified, do not second-guess): the running process is a **hardlink** to `Overlay.exe`, so `File.Move` over `Overlay.exe` succeeds while running. Relaunch the canonical `Overlay.exe` with **NO `--launched`** and `UseShellExecute=false` (inherits admin token, re-randomizes). Install dir passed in = `Path.GetDirectoryName(Environment.ProcessPath)`. Never touch `config/` or `icons/`. Staging lives in `installDir/.update/`.

- [ ] **Step 1: Implement `AutoUpdater`**

Create `src/POE2Radar.Overlay/Update/AutoUpdater.cs`:

```csharp
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using POE2Radar.Core.Update;

namespace POE2Radar.Overlay.Update;

/// <summary>
/// Silent self-updater. Chrome-style: during a session it downloads + verifies the newest release and
/// STAGES it; on the NEXT launch it atomically swaps Overlay.exe (safe because the running process is a
/// hardlink, not the file itself) and relaunches into it — inheriting the admin token, no .cmd, no UAC,
/// no SmartScreen. Retains Overlay.old.exe for one generation for crash rollback. Touches ONLY our own
/// files (Overlay.exe + CHANGELOG.md) — never config/ or icons/, never the game.
/// </summary>
internal static class AutoUpdater
{
    private const string Repo = "luther-rotmg/POE2GPS";
    private const int RollbackThreshold = 3;                 // healthy boot clears by attempt 2 (hardlink adds one pass)
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(120);
    private const long MaxBytes = 250L * 1024 * 1024;        // sanity cap on the download

    private static string Dir(string i)      => Path.Combine(i, ".update");
    private static string StagedExe(string i)=> Path.Combine(Dir(i), "Overlay.new.exe");
    private static string StagedLog(string i)=> Path.Combine(Dir(i), "CHANGELOG.md");
    private static string StatePath(string i)=> Path.Combine(Dir(i), "state.json");
    private static string BootPath(string i) => Path.Combine(Dir(i), "boot.json");
    private static string CanonExe(string i) => Path.Combine(i, "Overlay.exe");
    private static string BackupExe(string i)=> Path.Combine(i, "Overlay.old.exe");

    private record Boot(string Target, int Attempts);

    // ─────────────────────────── background check + stage (never blocks startup) ───────────────────────────

    public static async Task CheckAndStageAsync(string mode, string current, string installDir, CancellationToken ct)
    {
        if (mode != "silent") return;
        try
        {
            var res = await UpdateChecker.CheckAsync();
            if (res.Latest is not { Length: > 0 } tag || !AutoUpdatePolicy.IsNewer(current, tag)) return;

            var state = ReadState(installDir);
            if (!AutoUpdatePolicy.ShouldAttempt(state, tag)) return;

            var ok = await TryStageAsync(tag, installDir, ct);
            if (ok) { WriteState(installDir, new AutoUpdatePolicy.UpdateState(tag, 0)); }
            else    { BumpFailure(installDir, tag); }
        }
        catch { /* offline / rate-limited / IO — try again next launch */ }
    }

    private static async Task<bool> TryStageAsync(string tag, string installDir, CancellationToken ct)
    {
        var dir = Dir(installDir);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "download.zip");
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("POE2GPS-AutoUpdate");

            // 1) Fetch the checksum manifest (REQUIRED — no checksum, no update).
            var sums = await http.GetStringAsync(AutoUpdatePolicy.ChecksumUrl(Repo, tag), ct);
            var expected = AutoUpdatePolicy.ExpectedSha(sums, AutoUpdatePolicy.AssetName(tag));
            if (expected is null) return false;

            // 2) Download the zip to a temp file (bounded).
            using (var resp = await http.GetAsync(AutoUpdatePolicy.ZipUrl(Repo, tag), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength is > MaxBytes) return false;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                await src.CopyToAsync(dst, ct);
            }
            if (new FileInfo(zipPath).Length > MaxBytes) return false;

            // 3) Verify SHA-256 BEFORE extraction.
            if (!string.Equals(Sha256Hex(zipPath), expected, StringComparison.OrdinalIgnoreCase)) return false;

            // 4) Extract Overlay.exe + CHANGELOG.md only.
            var tmpExe = Path.Combine(dir, "Overlay.extract.exe");
            var tmpLog = Path.Combine(dir, "CHANGELOG.extract.md");
            File.Delete(tmpExe); File.Delete(tmpLog);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var exeEntry = zip.GetEntry("Overlay.exe");
                if (exeEntry is null) return false;
                exeEntry.ExtractToFile(tmpExe, overwrite: true);
                zip.GetEntry("CHANGELOG.md")?.ExtractToFile(tmpLog, overwrite: true);
            }

            // 5) Verify the extracted exe's FileVersion is actually the target (or newer).
            var fv = FileVersionInfo.GetVersionInfo(tmpExe).FileVersion;
            if (fv is null || !(AutoUpdatePolicy.IsNewer(UpdateChecker.Current, fv) ||
                                string.Equals(NormV(fv), NormV(tag), StringComparison.OrdinalIgnoreCase)))
                return false;

            StripMotw(tmpExe);

            // 6) Atomically promote to the staged names.
            File.Move(tmpExe, StagedExe(installDir), overwrite: true);
            if (File.Exists(tmpLog)) File.Move(tmpLog, StagedLog(installDir), overwrite: true);
            return true;
        }
        finally { try { File.Delete(zipPath); } catch { } }
    }

    // ─────────────────────────── apply a staged update at startup (then relaunch) ───────────────────────────

    /// <returns>true if it swapped + relaunched (caller must return 0 immediately).</returns>
    public static bool ApplyStagedIfPresent(string current, string installDir)
    {
        var staged = StagedExe(installDir);
        if (!File.Exists(staged)) return false;
        try
        {
            var fv = FileVersionInfo.GetVersionInfo(staged).FileVersion;
            if (fv is null || !AutoUpdatePolicy.IsNewer(current, fv)) { SafeDelete(staged); return false; }

            var canon = CanonExe(installDir);
            var backup = BackupExe(installDir);
            SafeDelete(backup);
            if (File.Exists(canon)) File.Move(canon, backup, overwrite: true);  // keep old for rollback
            File.Move(staged, canon);                                            // atomic swap (we hold the hardlink inode)
            if (File.Exists(StagedLog(installDir)))
                try { File.Copy(StagedLog(installDir), Path.Combine(installDir, "CHANGELOG.md"), overwrite: true); } catch { }

            WriteBoot(installDir, new Boot(NormV(fv), 1));
            SafeDelete(StagedLog(installDir));
            Relaunch(canon, installDir);
            return true;
        }
        catch { return false; }   // any failure -> continue booting the current version unchanged
    }

    // ─────────────────────────── crash-loop rollback ───────────────────────────

    /// <returns>true if it rolled back + relaunched (caller must return 0).</returns>
    public static bool RollbackIfCrashLooping(string installDir)
    {
        var boot = ReadBoot(installDir);
        if (boot is null) return false;
        var attempts = boot.Attempts + 1;
        WriteBoot(installDir, boot with { Attempts = attempts });
        if (attempts < RollbackThreshold) return false;

        var backup = BackupExe(installDir);
        if (!File.Exists(backup)) { SafeDelete(BootPath(installDir)); return false; } // no backup -> nothing to do
        try
        {
            var canon = CanonExe(installDir);
            File.Move(backup, canon, overwrite: true);                 // restore last-known-good
            WriteState(installDir, new AutoUpdatePolicy.UpdateState(boot.Target, 2)); // mark target bad
            SafeDelete(BootPath(installDir));
            Relaunch(canon, installDir);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Called once the app has run briefly without crashing: the applied update is good.</summary>
    public static void ConfirmHealthy(string installDir)
    {
        SafeDelete(BootPath(installDir));
        SafeDelete(BackupExe(installDir));
    }

    /// <summary>The version currently staged for next launch (for the /api/version "pending" line), or null.</summary>
    public static string? PendingVersion(string installDir)
    {
        try
        {
            var staged = StagedExe(installDir);
            if (File.Exists(staged)) return FileVersionInfo.GetVersionInfo(staged).FileVersion;
        }
        catch { }
        return null;
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static void Relaunch(string exe, string installDir)
        => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = installDir });

    private static string Sha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static void StripMotw(string path)
    { try { File.Delete(path + ":Zone.Identifier"); } catch { } }

    private static string NormV(string v) => v.Trim().TrimStart('v', 'V');
    private static void SafeDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    private static AutoUpdatePolicy.UpdateState? ReadState(string i)
    { try { return JsonSerializer.Deserialize<AutoUpdatePolicy.UpdateState>(File.ReadAllText(StatePath(i))); } catch { return null; } }
    private static void WriteState(string i, AutoUpdatePolicy.UpdateState s) => AtomicJson(StatePath(i), s);
    private static void BumpFailure(string i, string tag)
    {
        var s = ReadState(i);
        var n = (s is not null && string.Equals(s.TargetVersion, tag, StringComparison.OrdinalIgnoreCase)) ? s.Failures + 1 : 1;
        WriteState(i, new AutoUpdatePolicy.UpdateState(tag, n));
    }

    private static Boot? ReadBoot(string i)
    { try { return JsonSerializer.Deserialize<Boot>(File.ReadAllText(BootPath(i))); } catch { return null; } }
    private static void WriteBoot(string i, Boot b) => AtomicJson(BootPath(i), b);

    private static void AtomicJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }
}
```

- [ ] **Step 2: Build the overlay**

Run: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Debug`
Expected: `Build succeeded`, `0 Error(s)`. (Ignore MSB3026/MSB3027 copy-lock.)

- [ ] **Step 3: Commit**

```bash
git add src/POE2Radar.Overlay/Update/AutoUpdater.cs
git commit -m "feat(update): AutoUpdater — background stage, SHA-256 verify, in-process atomic swap, .old rollback"
```

---

### Task 4: `AutoUpdateSettings` + migration + API round-trip

**Files:**
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs` (add nested class + property + migration in `Load`)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (`ReadSettings` + `ApplySettings` + `TryParseAutoUpdate`)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `RadarSettings.AutoUpdate` (`AutoUpdateSettings { string Mode }`, default `"silent"`), serialized as JSON key `autoUpdate`. Read by Task 5 (`Program.cs`) and Task 6 (dashboard).

- [ ] **Step 1: Add the settings class + property**

In `src/POE2Radar.Overlay/Config/RadarSettings.cs`, immediately after the existing `CheckForUpdates` property (near line 211), add:

```csharp
    // ── Auto-update (v0.19.1): "off" = no outbound update contact; "notify" = check only (banner);
    //    "silent" = check + download + apply on next launch. Default "silent" (owner-approved; disclosed
    //    in README + release notes). This supersedes the legacy CheckForUpdates bool (migrated in Load()).
    public AutoUpdateSettings AutoUpdate { get; set; } = new();
```

Then add the sealed class next to the other nested settings classes (e.g. directly below `BuffNameplateSettings`, ~line 492):

```csharp
public sealed class AutoUpdateSettings
{
    // one of: "off" | "notify" | "silent"
    public string Mode { get; set; } = "silent";
}
```

- [ ] **Step 2: Migrate the legacy `CheckForUpdates` flag in `Load`**

In `RadarSettings.Load()` (near line 297-324), after the settings object is deserialized and before it is returned, add a migration block. The intent: a config file written by an older build has no `autoUpdate` key, so `AutoUpdate` deserializes to its default (`Mode="silent"`) — but a user who had explicitly set `CheckForUpdates=false` must NOT be silently switched on. Detect the legacy opt-out by re-reading the raw JSON:

```csharp
        // v0.19.1 migration: honor a legacy explicit CheckForUpdates=false as AutoUpdate.Mode="off".
        // (A fresh install / any config without the key defaults to "silent".)
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);   // `json` = the raw file text read above
            var root = doc.RootElement;
            var hasAutoUpdate = root.TryGetProperty("autoUpdate", out _);
            if (!hasAutoUpdate && root.TryGetProperty("checkForUpdates", out var cfu)
                && cfu.ValueKind == System.Text.Json.JsonValueKind.False)
                s.AutoUpdate.Mode = "off";
        }
        catch { }
```

(If the local variable holding the raw file text is not named `json`, use whatever `Load` reads the file into. If `Load` does not currently keep the raw text, capture it: `var json = File.ReadAllText(FilePath);` before deserializing, and deserialize from `json`.)

- [ ] **Step 3: Wire `autoUpdate` into the API read**

In `src/POE2Radar.Overlay/Web/ApiServer.cs`, in `ReadSettings()` (near line 1142 where `checkForUpdates` is emitted), add to the returned anonymous object:

```csharp
            autoUpdate = _settings.AutoUpdate,
```

- [ ] **Step 4: Wire `autoUpdate` into the API write + validator**

In `ApplySettings()` (the switch near line 1221-1363), add a case mirroring the `obsOverlay` whole-object case:

```csharp
                case "autoUpdate" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseAutoUpdate(p.Value, out var au)) { _settings.AutoUpdate = au; applied.Add(p.Name); }
                    break;
```

And add the validator method near the other `TryParse*` helpers in `ApiServer.cs`:

```csharp
    private static bool TryParseAutoUpdate(JsonElement e, out RadarSettings.AutoUpdateSettings v)
    {
        v = new RadarSettings.AutoUpdateSettings();
        var mode = e.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (mode is not ("off" or "notify" or "silent")) return false;
        v.Mode = mode;
        return true;
    }
```

- [ ] **Step 5: Build + run the full test suite**

Run: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Debug`
Expected: `Build succeeded`, `0 Error(s)`.
Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj`
Expected: PASS (existing suite + AutoUpdatePolicyTests green).

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Web/ApiServer.cs
git commit -m "feat(update): AutoUpdate.Mode setting (default silent) + legacy CheckForUpdates migration + API round-trip"
```

---

### Task 5: `Program.cs` startup wiring + `RadarApp` changes

**Files:**
- Modify: `src/POE2Radar.Overlay/Program.cs` (pre-attach apply/rollback + check/stage kickoff)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (accept `updateTask`, remove ctor fire-and-forget check, `ConfirmHealthy`, `VersionJson` extras)

**Interfaces:**
- Consumes: `AutoUpdater` (Task 3), `RadarSettings.AutoUpdate` (Task 4), `UpdateChecker` (existing).
- Produces: nothing for later code tasks (final wiring). Task 6 reads `/api/version`'s new `mode`/`pendingVersion` fields.

- [ ] **Step 1: Insert the pre-attach apply/rollback block in `Program.cs`**

In `src/POE2Radar.Overlay/Program.cs`, immediately **after** the hardlink self-relaunch block closes (after line 30, before `var myName = ...` on line 32), insert:

```csharp
// ── v0.19.1 auto-update (runs only in the --launched real instance, before attaching to the game) ──
var installDir = Path.GetDirectoryName(Environment.ProcessPath);
var startupSettings = RadarSettings.Load();
if (installDir != null)
{
    // A crash-looping update rolls back to Overlay.old.exe (safety — runs regardless of mode).
    if (POE2Radar.Overlay.Update.AutoUpdater.RollbackIfCrashLooping(installDir)) return 0;
    // Apply an update staged by a previous session, then relaunch into it.
    if (startupSettings.AutoUpdate.Mode == "silent"
        && POE2Radar.Overlay.Update.AutoUpdater.ApplyStagedIfPresent(UpdateChecker.Current, installDir)) return 0;
}
```

(Add `using POE2Radar.Overlay.Config;` at the top of `Program.cs` if `RadarSettings` is not already in scope — check the existing usings; the file currently `using POE2Radar.Overlay;`. Use the fully-qualified `POE2Radar.Overlay.Config.RadarSettings.Load()` if simpler.)

- [ ] **Step 2: Start the check + background stage, and pass the task to `RadarApp`**

In `Program.cs`, replace the app construction line (currently `using var app = new RadarApp(process, reader);` at line 49) with:

```csharp
// Update check (banner) + silent background staging for NEXT launch — never blocks startup.
System.Threading.Tasks.Task<UpdateChecker.Result>? updateTask = null;
if (startupSettings.AutoUpdate.Mode != "off")
    updateTask = System.Threading.Tasks.Task.Run(() => UpdateChecker.CheckAsync());
if (startupSettings.AutoUpdate.Mode == "silent" && installDir != null)
    _ = System.Threading.Tasks.Task.Run(() =>
        POE2Radar.Overlay.Update.AutoUpdater.CheckAndStageAsync("silent", UpdateChecker.Current, installDir, System.Threading.CancellationToken.None));

using var app = new RadarApp(process, reader, updateTask);
```

- [ ] **Step 3: Update the `RadarApp` constructor signature + wire the update result**

In `src/POE2Radar.Overlay/RadarApp.cs`, change the constructor signature to accept the optional task. Find the ctor declaration (`public RadarApp(ProcessHandle process, MemoryReader reader)`) and change it to:

```csharp
    public RadarApp(ProcessHandle process, MemoryReader reader, Task<UpdateChecker.Result>? updateTask = null)
```

Then **replace** the fire-and-forget block at the end of the ctor (lines 686-697, the `if (_settings.CheckForUpdates) { _ = Task.Run(async () => { ... }); }`) with a consumer of the passed task:

```csharp
        // Update result comes from Program.cs (checked pre-attach so Mode gating lives in one place).
        if (updateTask != null)
        {
            _ = updateTask.ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                var u = t.Result;
                _update = u;
                if (u.UpdateAvailable)
                    ConsoleTheme.WarnLine($"\n*** UPDATE AVAILABLE: {u.Latest} — you have v{u.Current}." +
                        (_settings.AutoUpdate.Mode == "silent" ? " Installing automatically on next launch." : $" Download: {u.Url}") + " ***\n");
                else
                    ConsoleTheme.Accent($"POE2GPS v{u.Current}" + (u.Latest != null ? " (up to date)." : " (update check unavailable)."));
            });
        }
```

- [ ] **Step 4: Mark a fresh update healthy once running, in `Run()`**

In `RadarApp.Run()`, at the very start (after the method opens, before the main loop), add:

```csharp
        // v0.19.1: if this launch applied a staged update, mark it good after running briefly without crashing.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                var d = Path.GetDirectoryName(Environment.ProcessPath);
                if (d != null) POE2Radar.Overlay.Update.AutoUpdater.ConfirmHealthy(d);
            }
            catch { }
        });
```

- [ ] **Step 5: Add `mode` + `pendingVersion` to `VersionJson()`**

In `RadarApp.cs`, find `VersionJson()` (near line 902-911). It currently returns an object like `new { current, latest, updateAvailable, url }`. Add two fields:

```csharp
            mode = _settings.AutoUpdate.Mode,
            pendingVersion = POE2Radar.Overlay.Update.AutoUpdater.PendingVersion(Path.GetDirectoryName(Environment.ProcessPath) ?? "."),
```

(Preserve the existing fields exactly; only append these two to the returned anonymous object.)

- [ ] **Step 6: Build + run tests**

Run: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Debug`
Expected: `Build succeeded`, `0 Error(s)`.
Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/POE2Radar.Overlay/Program.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(update): pre-attach apply/rollback + background stage wiring; RadarApp consumes update task, ConfirmHealthy, VersionJson mode/pending"
```

---

### Task 6: Dashboard Auto-Update card

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (settings card HTML + `renderAutoUpdate`/`saveAutoUpdate`/`wireAutoUpdate` JS + init call + pending line)

**Interfaces:**
- Consumes: `/api/settings` (`autoUpdate.mode`), `/api/version` (`mode`, `pendingVersion`), the existing `saveSetting(key,val)` helper (DashboardHtml.cs:1207-1211).
- Produces: nothing for later tasks.

- [ ] **Step 1: Add the Auto-Update settings card**

In `src/POE2Radar.Overlay/Web/DashboardHtml.cs`, add a card in the settings section (place it near the update banner / a "Startup" group; mirror an existing simple card's markup). Insert this HTML block:

```html
<div class="card">
  <h3>Auto-Update</h3>
  <label>Mode
    <select id="au-mode">
      <option value="silent">Silent (download &amp; install automatically)</option>
      <option value="notify">Notify only (tell me, don't install)</option>
      <option value="off">Off (no update check)</option>
    </select>
  </label>
  <div id="au-pending" class="muted" style="margin-top:6px"></div>
  <p class="muted" style="margin-top:6px">Updates come only from github.com/luther-rotmg/POE2GPS over HTTPS (SHA-256 verified). No telemetry, no pricing.</p>
</div>
```

- [ ] **Step 2: Add the JS render/save/wire functions**

In the `<script>` section of `DashboardHtml.cs`, add:

```js
let autoUpd = { mode: 'silent' };
function renderAutoUpdate(s){
  if(s && s.autoUpdate) autoUpd = s.autoUpdate;
  const sel=document.getElementById('au-mode'); if(sel) sel.value = autoUpd.mode || 'silent';
}
function saveAutoUpdate(){
  const sel=document.getElementById('au-mode'); if(!sel) return;
  autoUpd = { mode: sel.value };
  saveSetting('autoUpdate', autoUpd);
}
function wireAutoUpdate(){
  const sel=document.getElementById('au-mode'); if(sel) sel.onchange = saveAutoUpdate;
}
function renderAuPending(v){
  const el=document.getElementById('au-pending'); if(!el) return;
  el.textContent = (v && v.pendingVersion) ? ('Update '+v.pendingVersion+' downloaded — installs on next launch.') : '';
}
```

- [ ] **Step 3: Call render/wire from init and feed the pending line from the version check**

Find the init sequence (the block ending with `checkVersion();` near line 2401-2406). Add `renderAutoUpdate` where settings are first applied (wherever the settings object `s` is rendered into cards) and `wireAutoUpdate()` alongside the other `wire*()` calls. In `checkVersion()` (near line 2210-2219, where `/api/version` JSON is handled), after it uses the response, call `renderAuPending(v)` (where `v` is the parsed `/api/version` object).

Concretely, in `checkVersion()`'s `.then(v => { ... })` handler, add at the end:

```js
    renderAuPending(v);
```

And in the settings-render function (where `renderObsOverlay(s)` / similar are called with the fetched settings `s`), add:

```js
    renderAutoUpdate(s);
```

And in the init wire block (where `wireSettings()` etc. run), add:

```js
    wireAutoUpdate();
```

- [ ] **Step 4: Build**

Run: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Debug`
Expected: `Build succeeded`, `0 Error(s)`. (Watch for raw-string-literal escaping if the card uses `"""` — keep braces balanced.)

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(update): dashboard Auto-Update card (mode select + pending-update line)"
```

Manual verification (not a build gate): dashboard shows the card; changing Mode persists via `/api/settings`; a staged update shows the pending line.

---

### Task 7: Release pipeline (CHANGELOG + SHA-256), README disclosure, version bump, CHANGELOG entry

**Files:**
- Modify: `.github/workflows/release.yml` (bundle `CHANGELOG.md`; publish SHA-256 asset)
- Modify: `publish.ps1` (bundle `CHANGELOG.md` for local parity)
- Modify: `README.md` (disclose opt-out-able silent auto-update)
- Modify: `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (`<Version>0.19.1</Version>`)
- Modify: `CHANGELOG.md` (add the v0.19.1 entry)

**Interfaces:**
- Produces: `POE2GPS-{tag}-sha256.txt` release asset + `CHANGELOG.md` inside the zip — consumed at runtime by Task 3's `AutoUpdater`.

- [ ] **Step 1: Bundle CHANGELOG.md into the release zip**

In `.github/workflows/release.yml`, change the Package step's copy line (line 50) from:

```yaml
          Copy-Item README.md, LICENSE publish/
```
to:
```yaml
          Copy-Item README.md, LICENSE, CHANGELOG.md publish/
```

- [ ] **Step 2: Publish the SHA-256 checksum asset**

In `.github/workflows/release.yml`, after the Package step (after the `Compress-Archive` at line 51) add a new step that writes the sha256sum-format manifest:

```yaml
      - name: Write SHA-256 checksum
        shell: pwsh
        run: |
          $tag = "${{ github.ref_name }}"
          $zip = "POE2GPS-$tag-win-x64.zip"
          $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
          # sha256sum format: "<hash><space><space><filename>"
          "$hash  $zip" | Set-Content -Path "POE2GPS-$tag-sha256.txt" -Encoding ascii -NoNewline
```

And update the release `files:` glob (line 76) so the checksum ships too:

```yaml
        with:
          files: |
            POE2GPS-*-win-x64.zip
            POE2GPS-*-sha256.txt
          body_path: RELEASE_NOTES.md
          generate_release_notes: true
```

- [ ] **Step 3: Local parity in publish.ps1**

In `publish.ps1`, change the copy line (line 26) from:

```powershell
Copy-Item "$root/README.md", "$root/LICENSE" "$root/publish/" -Force
```
to:
```powershell
Copy-Item "$root/README.md", "$root/LICENSE", "$root/CHANGELOG.md" "$root/publish/" -Force
```

- [ ] **Step 4: README disclosure**

In `README.md`, update the "What it does — and never does" section: the outbound-contact line should now read that POE2GPS makes an **update check + opt-out-able silent auto-update, from github.com/luther-rotmg/POE2GPS only (SHA-256 verified); no telemetry, no pricing**, and note the `Auto-Update` setting (Silent / Notify only / Off) and that `Overlay.old.exe` is kept for one generation as a manual rollback. Add one sentence to the install/first-run notes: *"On launch POE2GPS may update itself from our GitHub Releases; set Auto-Update to 'Notify only' or 'Off' in the dashboard to disable."*

- [ ] **Step 5: Version bump**

In `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj`, change `<Version>0.19.0</Version>` to:

```xml
    <Version>0.19.1</Version>
```

- [ ] **Step 6: CHANGELOG entry**

In `CHANGELOG.md`, add a new top entry:

```markdown
## [0.19.1] — 2026-07-02
### Added — 🔄 **Silent Auto-Update** & 🧭 **True-North Map**
- 🔄 **POE2GPS updates itself.** When a newer release exists on our GitHub, POE2GPS downloads it in the background, verifies it (**SHA-256**), and installs it on your next launch — no more hunting for a zip. Fully **opt-out-able**: **⚙️ Settings → Auto-Update → Silent / Notify only / Off**.
- 🛡️ **Safe by design.** Downloads only from `github.com/luther-rotmg/POE2GPS` over HTTPS, verifies the checksum before installing, swaps only `Overlay.exe`, and **never touches your `config/` or `icons/`**. Keeps `Overlay.old.exe` for one generation and auto-rolls-back if a new build fails to start. No telemetry, no pricing — still 100% read-only of the game.
- 🧭 **Web minimap now matches the game.** The `/map` view renders **isometrically**, aligned with the in-game overlay instead of the old top-down/rotated look — with a one-tap **iso ↔ top-down** toggle.
- 📄 **`CHANGELOG.md` now ships next to the exe** (and lands beside it on auto-update).
```

- [ ] **Step 7: Build + tests + compliance**

Run: `dotnet build -c Debug`
Expected: `Build succeeded`, `0 Error(s)`.
Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj`
Expected: PASS.
Run: `powershell -File scripts/compliance-gate.ps1`
Expected: `COMPLIANCE GATE: PASS`.
Run: `powershell -File scripts/scrub-strings.ps1 -SelfTest`
Expected: `scrub self-test PASSED`.

- [ ] **Step 8: Commit**

```bash
git add .github/workflows/release.yml publish.ps1 README.md src/POE2Radar.Overlay/POE2Radar.Overlay.csproj CHANGELOG.md
git commit -m "chore(release): v0.19.1 — CHANGELOG-next-to-exe + SHA-256 asset, README disclosure, version bump"
```

---

### Task 8: Integration sweep + final review

**Files:** none new — verification + fixes only.

- [ ] **Step 1: Full clean build**

Run: `dotnet build -c Debug`
Expected: `Build succeeded`, `0 Error(s)`.

- [ ] **Step 2: Full test suite**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj`
Expected: PASS (all green, including AutoUpdatePolicyTests).

- [ ] **Step 3: Compliance + scrub gates**

Run: `powershell -File scripts/compliance-gate.ps1` → `PASS`.
Run: `powershell -File scripts/scrub-strings.ps1 -SelfTest` → `PASSED`.

- [ ] **Step 4: Cross-task consistency check**

Verify by reading:
- `AutoUpdate.Mode` values are exactly `off`/`notify`/`silent` everywhere (settings default, `TryParseAutoUpdate`, `Program.cs` gates, dashboard `<option>`s).
- `Program.cs` runs apply/rollback only in the `--launched` path (after the hardlink block), never with `--launched` passed to the relaunch.
- `AutoUpdater.Relaunch` passes **no** `--launched` and `UseShellExecute=false`.
- The ctor's old `if (_settings.CheckForUpdates)` block is fully removed (no double update check).
- `installDir` is `Path.GetDirectoryName(Environment.ProcessPath)` everywhere (never `Assembly.Location`).

- [ ] **Step 5: Whole-branch final review**

Dispatch the final multi-lens review (see subagent-driven-development). Focus lenses: (a) compliance invariants intact (read-only, no input/pricing, loopback write-gate); (b) the updater never deletes `config/`/`icons/` and never bricks on a partial download (verify-before-apply + `.old` retained); (c) crash-loop counting cannot false-rollback a healthy boot nor infinite-loop; (d) threading — `CheckAndStageAsync` never blocks startup or `AttachToPoE`. Dispatch ONE fix subagent with the full findings list; re-review until clean.

- [ ] **Step 6: Ledger**

Append to `.superpowers/sdd/progress.md`: `v0.19.1 complete (map iso + silent auto-update); N tests; final review clean.`

---

## Self-Review (completed by plan author)

**Spec coverage:** Map iso transform + toggle → T1. `AutoUpdatePolicy` + tests → T2. `AutoUpdater` download/verify/stage/apply/rollback → T3. `AutoUpdateSettings` + migration + API → T4. Program.cs pre-attach flow + RadarApp changes + VersionJson extras → T5. Dashboard card → T6. CHANGELOG-in-zip + SHA-256 asset + README disclosure + version + CHANGELOG entry → T7. Compliance/tests/final review → T8. All spec sections covered.

**Placeholder scan:** No TBD/TODO; every code step has complete code.

**Type consistency:** `AutoUpdatePolicy.UpdateState(TargetVersion, Failures)`, `AutoUpdater` method signatures, and `AutoUpdateSettings.Mode` names match across T2–T6. `Relaunch`/`installDir`/`Mode` used consistently. `PendingVersion`/`ConfirmHealthy`/`ApplyStagedIfPresent`/`RollbackIfCrashLooping`/`CheckAndStageAsync` names identical in T3 (definition) and T5 (call sites).
