# v0.19.1 — Auto-Update & True-North Map — Design

**Date:** 2026-07-02
**Status:** approved (design), pending spec review
**Release:** v0.19.1 (feedback fast-follow; keeps v0.20.0 = Sanctum on the roadmap)

## Goal

Ship two independent, user-requested features in one small release:

1. **True-North Map** — make the web minimap (`/map`) render terrain + entity dots in the **same
   isometric orientation** as the in-game overlay (today it draws the grid axis-aligned, ~45–90°
   off). Pure client-side.
2. **Silent Auto-Update** — the overlay updates itself: on a clean boundary it downloads the newest
   release from our GitHub, verifies it, atomically swaps `Overlay.exe`, and relaunches into the new
   version — with no manual zip handling. Plus **ship `CHANGELOG.md` next to the exe**.

## Architecture (2–3 sentences)

The map fix is a self-contained rewrite of the draw path in `MapPageHtml.cs` that applies the exact
`MapProjection` isometric transform (verified numerically) — no backend, no new reads. Auto-update
splits into a **pure, unit-tested Core policy** (`AutoUpdatePolicy`: semver gate, asset/URL/checksum
selection, fail-counter) and a **thin Overlay IO layer** (`AutoUpdater`: download, SHA-256 verify,
stage, atomic apply, `.old` rollback, elevation-preserving relaunch), wired into `Program.cs`
pre-attach and configured by a new `AutoUpdate.Mode` setting. The updater exploits the project's
existing hardlink self-relaunch so it can replace `Overlay.exe` **in-process** (no `.cmd` helper) and
relaunch via `CreateProcess` (inherits admin elevation, bypasses SmartScreen).

## Tech Stack

.NET 10, C#, x64, Windows. `System.Net.Http` (already used by `UpdateChecker`), `System.IO.Compression`
(zip extraction), `System.Security.Cryptography` (SHA-256). Canvas 2D (map). xUnit for Core policy.

## Global Constraints (copied verbatim — every task inherits these)

- **Strictly READ-ONLY of the game.** No injection, no `WriteProcessMemory`/`VirtualProtectEx`/
  `CreateRemoteThread`, no input emission (`SendInput`/`PostMessage`/`keybd_event`). Auto-update
  touches **only POE2GPS's own files** (its install dir) and its **own** process — never the PoE2
  process. It is orthogonal to the read-only invariant.
- No pricing / poe.ninja / trade / reward-values anywhere.
- Writes over the HTTP API stay **loopback-gated** on `RemoteEndPoint.Address` (`IsLoopback`), not the
  spoofable Host header (the v0.17 LAN write-gate invariant). The new `autoUpdate` setting round-trips
  through the same already-gated `/api/settings` POST.
- README **supports PoE2 0.5.4** badge unchanged.
- CI gates unchanged and must pass: `scripts/compliance-gate.ps1` (no input/process-write symbols; no
  pricing) and `scripts/scrub-strings.ps1 -SelfTest`. (`Process.Start`, `File.Move`, `HttpClient` are
  already present in shipped source and are **not** in the gate's symbol set.)
- Version → **0.19.1** in `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj`.

---

## Feature A — True-North Map (isometric web minimap)

### Root cause (verified)

`MapPageHtml.cs` draws the walkable bitmap axis-aligned (`drawImage(terrain, cx-player.x*scale, …)`)
and plots dots at raw `(e.x-player.x, e.y-player.y)*scale`. The in-game overlay
(`OverlayRenderer.cs:1267-1275, 1468-1473`) runs the identical grid through
`MapProjection.GridDeltaToMapDelta` — a 38.7° isometric warp. That difference is the mismatch.

Verified facts (all high-confidence):
- `/entities` `x,y` (`e.Grid.X/Y`) and `/state` `player {x,y}` are **both in grid units** (world ÷
  `WorldToGridRatio = 250/23`), same space → direct subtraction is correct.
- In-game projection (flat, `dz=0`): `sx = scale·(dx−dy)·COS`, `sy = scale·(−(dx+dy))·SIN`, with
  `COS = cos(38.7°) = 0.780430`, `SIN = sin(38.7°) = 0.625243`.
- The map is **fixed-north** — no player-heading rotation anywhere. Do **not** add heading rotation.
- The player blip is always at map center; the terrain bitmap is row-major `[gridY*width + gridX]`.
- The in-game `DefaultShift (0,-20)`, the read `Map.Shift`, and the live in-game `Map.Zoom` scale are
  **in-game-only** — the web map keeps its own `scale` and uses canvas center as the fixed center.

### The fix (client-side only, `MapPageHtml.cs`)

Add constants and a projector:

```js
const COS = 0.780430, SIN = 0.625243;   // cos/sin(38.7°) — mirrors MapProjection.cs
function project(dx, dy, scale){ return { sx: scale*(dx-dy)*COS, sy: scale*(-(dx+dy))*SIN }; }
```

**Dots:** `const {sx,sy}=project(e.x-player.x, e.y-player.y, scale); const x=cx+sx, y=cy+sy;`
(existing off-canvas cull `if(x<-4||…)` stays valid — same screen coords). Player blip stays at
`(cx,cy)`.

**Terrain:** project the three bitmap corners through the same `project()` (offset by `-player`),
derive the affine basis, and draw under a canvas transform:

```js
// setTransform(a,b,c,d,e,f): a=COS*scale, b=-SIN*scale, c=-COS*scale, d=-SIN*scale
const p00 = { x: cx+project(-player.x, -player.y, scale).sx, y: cy+project(-player.x, -player.y, scale).sy };
ctx.save();
ctx.setTransform(COS*scale, -SIN*scale, -COS*scale, -SIN*scale, p00.x, p00.y);
ctx.imageSmoothingEnabled = false;
ctx.drawImage(terrain, 0, 0, tw, th);
ctx.restore();
```

**View toggle:** add one button next to the zoom controls — **Isometric (default) ↔ Top-down** —
persisted in `localStorage` (`poe2gps.mapView`). Top-down keeps today's axis-aligned draw (arguably
clearer on a phone); isometric is the default and matches in-game. A `mapView` flag branches the draw.

### Non-goals (A)

No heading rotation, no syncing the web `scale` to the in-game zoom, no elevation (`dz=0` like the
overlay).

---

## Feature B — Silent Auto-Update

### Substrate (verified)

- Shipped artifact is a **single self-contained `Overlay.exe`** (`PublishSingleFile`,
  `EnableCompressionInSingleFile=false` — load-bearing for the string scrub; the updater must not
  re-bundle/compress). One file to replace.
- On launch the app **hardlinks itself to a random name in the same dir** and relaunches that with
  `--launched` (`Program.cs:9-30`); the running process is the **hardlink**, not the `Overlay.exe`
  directory entry. → We can `File.Move` a new `Overlay.exe` into place **while running** (the process
  holds the old inode). No wait-for-exit, no `.cmd`.
- Relaunching via `Process.Start(UseShellExecute=false)` from the already-elevated process **inherits
  the admin token** (the app already does this for the hardlink relaunch — no UAC prompt). Relaunch
  the **canonical `Overlay.exe` with NO `--launched`** so Program.cs re-randomizes normally.
- Install dir = `Path.GetDirectoryName(Environment.ProcessPath)` (reliable even as the random
  hardlink). Canonical exe = `Path.Combine(installDir, "Overlay.exe")`. **Never** use
  `Assembly.Location` (empty in single-file). `config/` and `icons/` live next to the exe and hold all
  user data — the updater replaces **only** `Overlay.exe` + `CHANGELOG.md` and never touches them.
- Files written by `HttpClient` carry **no Mark-of-the-Web**, and `CreateProcess` relaunch **does not
  invoke SmartScreen** → the update is genuinely silent. (Defensively strip any `Zone.Identifier` ADS
  after download.)
- There is **no `requireAdministrator` manifest** (deliberately omitted for identity hygiene). Silent
  works *because* we swap+relaunch in-process from the already-elevated overlay — never from a fresh,
  unelevated process.

### B.1 — Core: `AutoUpdatePolicy` (pure, unit-tested)

`src/POE2Radar.Core/Update/AutoUpdatePolicy.cs` — no IO, no `Poe2Live` dependency:

- `static bool IsNewer(string current, string latest)` — strict semver `>` (reuse the same
  major.minor.patch parse convention as `UpdateChecker`).
- `static string AssetName(string tag)` → `$"POE2GPS-{tag}-win-x64.zip"` using the **raw tag**
  (with `v`, e.g. `v0.20.0`) — matches `release.yml`. **Do not** `TrimStart('v')` for the URL.
- `static string ChecksumAssetName(string tag)` → `$"POE2GPS-{tag}-sha256.txt"`.
- `static string ZipUrl(string repo, string tag)` /
  `static string ChecksumUrl(string repo, string tag)` →
  `https://github.com/{repo}/releases/download/{tag}/{AssetName(tag)}`.
- `static string? SelectAsset(IEnumerable<(string Name,string Url)> assets, string tag)` — picks the
  asset whose name equals `AssetName(tag)` (case-insensitive); null if absent.
- `record UpdateState(string TargetVersion, int Failures)` +
  `static bool ShouldAttempt(UpdateState? state, string latest, int maxFailures = 2)` — true unless
  `state.TargetVersion == latest && state.Failures >= maxFailures`. A **new** `latest` resets the gate
  (different target).
- `static string ExpectedSha(string checksumFileText, string assetName)` — parse a
  `<sha256>␠␠<filename>` line (standard `sha256sum` format) for `assetName`.

### B.2 — Overlay: `AutoUpdater` (thin IO)

`src/POE2Radar.Overlay/Update/AutoUpdater.cs`. Uses `AutoUpdatePolicy` for every decision. Paths:

- `installDir/Overlay.exe` — canonical.
- `installDir/Overlay.old.exe` — one-generation rollback backup.
- `installDir/.update/` — staging: `Overlay.new.exe`, `CHANGELOG.md`, `state.json`
  (`AutoUpdatePolicy.UpdateState`), `boot.json` (`{target, attempts}`), `update.lock` (PID).

**`CheckAndStageAsync(mode, current, installDir, ct)`** — background, never blocks startup:
1. If `mode != silent` → return (notify/off handled elsewhere).
2. `UpdateChecker.CheckAsync()` (existing) → if not newer, return.
3. Load `state.json`; if `!ShouldAttempt(state, latest)` → return (this version already failed twice).
4. Acquire `update.lock` (write PID); if a live PID already holds it, return.
5. Download `ChecksumUrl` (text) → `ExpectedSha`. Download `ZipUrl` to `.update/download.zip`
   (HttpClient, hard total timeout, size-capped). Verify `SHA256(download.zip) == ExpectedSha`
   (case-insensitive) — **before extraction**. On mismatch: bump `state.Failures`, delete partial,
   return.
6. Extract `Overlay.exe` + `CHANGELOG.md` from the zip to `.update/`. Verify the extracted exe's
   `FileVersionInfo.FileVersion` `>= latest`. On mismatch: bump failures, clean, return.
7. Strip `Zone.Identifier` ADS on `.update/Overlay.new.exe` (defensive). Atomically rename extracted
   exe → `.update/Overlay.new.exe`. Reset `state.Failures = 0` for this target. Release lock.
   → Update is now **staged**; nothing swapped yet.

**`ApplyStagedIfPresent(current, installDir)`** — called at startup (see flow), returns `true` if it
relaunched (caller must `return 0`):
1. If no verified `.update/Overlay.new.exe` → false.
2. Re-verify staged exe `FileVersion` is newer than `current` (guard against a stale/older stage).
3. `File.Move(Overlay.exe → Overlay.old.exe, overwrite:true)` (backup — old inode; the running
   hardlink keeps it alive, harmless).
4. `File.Move(.update/Overlay.new.exe → Overlay.exe)` (atomic swap; works while running — we hold the
   hardlink inode, not the directory entry).
5. Copy `.update/CHANGELOG.md → installDir/CHANGELOG.md` (best-effort).
6. Write `.update/boot.json {target, attempts:1}`; delete the rest of `.update/` staging (download.zip,
   Overlay.new.exe).
7. `Process.Start(ProcessStartInfo(Overlay.exe){ UseShellExecute=false, WorkingDirectory=installDir })`
   — **no `--launched`** → inherits elevation, re-randomizes. Return true.

**`ConfirmHealthy(installDir)`** — called once the app reaches a known-good point
(`RadarApp.Run` steady state): delete `boot.json`, delete `Overlay.old.exe`. This marks the swapped
version as good.

**Crash-loop rollback** — at startup, if `boot.json` exists: increment `attempts` and persist; if
`attempts >= ROLLBACK_THRESHOLD` (3 — accounts for the one extra Program.cs pass the hardlink relaunch
adds; a healthy boot clears by attempt 2) **and** `Overlay.old.exe` exists →
`File.Move(Overlay.old.exe → Overlay.exe, overwrite:true)`, delete `boot.json`, mark the target as bad
in `state.json` (`Failures = maxFailures`), relaunch `Overlay.exe` (no `--launched`), return true.
**Guaranteed fallback:** even if auto-rollback never fires, `Overlay.old.exe` remains on disk for
manual restore (documented in README).

### B.3 — Startup flow (`Program.cs`)

Current order: `if(!--launched){ hardlink+relaunch; return 0; }` → `Banner()` → `AttachToPoE()` →
`new RadarApp(...)` (which today fires the update check in its ctor at `RadarApp.cs:686-697`).

New order (all update logic runs **only in the `--launched` instance**, after the hardlink block, so
the non-launched original stays a pure launcher):

```
// (after the hardlink self-relaunch block — we are the --launched real instance)
var installDir = Path.GetDirectoryName(Environment.ProcessPath)!;
var settings   = RadarSettings.Load();                       // load once, reuse for RadarApp
if (AutoUpdater.RollbackIfCrashLooping(installDir)) return 0; // step: boot.json attempts++ / rollback
if (settings.AutoUpdate.Mode == "silent" &&
    AutoUpdater.ApplyStagedIfPresent(UpdateChecker.Current, installDir)) return 0;  // swap + relaunch
// ↓ normal startup continues
ConsoleTheme.Banner();
var updateTask = Task.Run(() => UpdateChecker.CheckAsync());  // moved OUT of RadarApp ctor
if (settings.AutoUpdate.Mode == "silent")
    _ = Task.Run(() => AutoUpdater.CheckAndStageAsync(settings.AutoUpdate.Mode, UpdateChecker.Current, installDir, CancellationToken.None));
using var process = ProcessHandle.AttachToPoE();
var app = new RadarApp(process, reader, settings, updateTask);   // pass settings + update task in
app.Run();
```

- **Remove** the fire-and-forget check from the `RadarApp` ctor (`RadarApp.cs:686-697`) to avoid a
  double check; feed `_update`/`VersionJson()` from the passed `updateTask` result instead (await it
  where the ctor currently sets `_update`, or set `_update` when the task completes).
- `RadarApp.Run`, once steady, calls `AutoUpdater.ConfirmHealthy(installDir)` (deletes `boot.json` +
  `Overlay.old.exe`).
- **Boot-loop guardrails:** `RollbackIfCrashLooping` increments `attempts` exactly once per
  `--launched` startup. `ApplyStagedIfPresent` cleans `.update/` staging after applying, so it never
  re-applies the same stage. The download step never runs on the current session's exe — only stages
  for the *next* launch (Chrome-style), so a slow download never delays this session.

### B.4 — Settings (`RadarSettings`)

Add a nested sub-object mirroring the existing pattern (e.g. `ObsOverlay`/`BuffNameplates`):

```csharp
public AutoUpdateSettings AutoUpdate { get; set; } = new();

public sealed class AutoUpdateSettings
{
    // "off" = no outbound update contact; "notify" = check-only (banner); "silent" = check+download+apply
    public string Mode { get; set; } = "silent";   // product default (owner-approved); disclosed in README + release notes
}
```

- **Migration:** on `Load`, if `autoUpdate` is absent but the legacy `checkForUpdates` bool is present:
  `false → Mode="off"`, `true → Mode="silent"` (existing users move to silent auto-update on upgrade —
  the owner's product decision; disclosed prominently in the v0.19.1 release notes + README). Fresh
  installs default `Mode="silent"`.
- `AutoUpdate.Mode` is the **single authoritative** flag. `checkForUpdates` is deprecated (kept for
  back-compat read/migration only). The `RadarApp` ctor's `if (_settings.CheckForUpdates)` guard is
  replaced by `Mode != "off"` semantics moved to `Program.cs`.
- `/api/settings` round-trip: add `autoUpdate = _settings.AutoUpdate` to `ReadSettings`, and a
  whole-object case in `ApplySettings` (`TryParseAutoUpdate` validating `Mode ∈ {off,notify,silent}`),
  mirroring the `obsOverlay` case. camelCase key = `autoUpdate`.

### B.5 — Surfacing / dashboard

- Existing console banner + `#updateBanner` + `/api/version` already fire on `UpdateAvailable` — keep.
- `VersionJson()` (`RadarApp.cs:902`) gains `mode` and `pendingVersion` (from `.update/state.json` /
  staged exe) so the dashboard can show *"Update vX.Y.Z downloaded — restarts into it on next launch."*
- **Dashboard Auto-Update card** (mirror a simple existing card): a `<select>` for
  Off / Notify only / Silent auto-update, wired via `saveSetting('autoUpdate', {Mode})`; plus a
  read-only "pending update" line. Under a "Startup / Updates" section.

### B.6 — Release pipeline

`.github/workflows/release.yml`:
- **Package step:** `Copy-Item README.md, LICENSE, CHANGELOG.md publish/` (add `CHANGELOG.md`).
- **New step (before Create Release):** compute `Get-FileHash POE2GPS-$tag-win-x64.zip -Algorithm
  SHA256` and write `POE2GPS-$tag-sha256.txt` in `sha256sum` format (`<hash>␠␠<filename>`); attach it
  as a release asset (`files:` glob → include `POE2GPS-*-sha256.txt` alongside the zip).

`publish.ps1` (local parity): add `CHANGELOG.md` to the `Copy-Item` and (optional) emit the local
`sha256.txt`.

### B.7 — Security posture (explicit)

- **Integrity:** HTTPS to `github.com/luther-rotmg/POE2GPS` + **SHA-256 verify of the zip before
  extraction** + extracted-exe `FileVersion` check. Protects against corrupted/partial downloads and
  wrong assets.
- **Residual (accepted, owner-approved):** binaries are **not code-signed**, so a compromised GitHub
  account pushing a valid-looking release would pass verification and silent-apply to all users.
  Mitigation path (future, out of scope): GPG-signed checksum manifest with the public key embedded in
  the binary. Documented as a known limitation.
- **No `.cmd`, no dropper pattern:** swap + relaunch happen in-process; staged files use `.zip`/`.exe`
  inside `.update/` (a subdir the hardlink sweep — which scans install-root `*.exe` by matching
  `Overlay.exe`'s inode — never touches, since the new exe has a different inode).
- **README disclosure (required):** update the "What it does / never does" section — the outbound
  contact is now *"update check + opt-out-able silent auto-update, from our GitHub release only; no
  telemetry, no pricing."* Add a one-line first-run console note the first time silent mode stages an
  update.

### Non-goals (B)

Code-signing / GPG (future), delta/partial updates, cross-repo or pre-release channels, a rollback UI
beyond automatic `.old` + manual restore, downloading during the current session's startup path.

---

## File structure / units

**New:**
- `src/POE2Radar.Core/Update/AutoUpdatePolicy.cs` — pure decisions (semver, asset/URL/checksum, state).
- `tests/POE2Radar.Tests/AutoUpdatePolicyTests.cs` — unit tests for the above.
- `src/POE2Radar.Overlay/Update/AutoUpdater.cs` — IO: download/verify/stage/apply/rollback/relaunch.

**Modified:**
- `src/POE2Radar.Overlay/Web/MapPageHtml.cs` — isometric draw + view toggle.
- `src/POE2Radar.Overlay/Program.cs` — pre-attach update flow.
- `src/POE2Radar.Overlay/RadarApp.cs` — remove ctor update-check, accept `settings`+`updateTask`,
  `ConfirmHealthy`, `VersionJson` extras.
- `src/POE2Radar.Overlay/Config/RadarSettings.cs` — `AutoUpdateSettings` + migration.
- `src/POE2Radar.Overlay/Web/ApiServer.cs` — `autoUpdate` read/apply.
- `src/POE2Radar.Overlay/Web/DashboardHtml.cs` — Auto-Update card + pending line.
- `.github/workflows/release.yml`, `publish.ps1` — CHANGELOG.md + SHA-256 asset.
- `README.md` — disclosure.
- `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` — `<Version>0.19.1</Version>`.
- `CHANGELOG.md` — v0.19.1 entry.

## Testing

- **Core (`AutoUpdatePolicyTests`):** `IsNewer` (>, ==, <, malformed), `AssetName`/`ZipUrl` keep the
  `v`, `SelectAsset` match/miss/case, `ShouldAttempt` (fresh target resets, ≥maxFailures blocks),
  `ExpectedSha` parse (present/absent/malformed).
- **Map:** manual — verify web `/map` orientation matches the in-game overlay side-by-side; toggle
  round-trips and persists.
- **Auto-update IO:** manual in-game/on-machine — stage a synthetic newer release, confirm silent swap
  + elevation preserved (game reads still work after relaunch) + `config/`/`icons/` untouched +
  `CHANGELOG.md` present next to exe + `.old` cleaned on healthy boot + rollback restores on a
  deliberately-broken staged exe.
- **CI:** `compliance-gate.ps1` PASS, `scrub-strings.ps1 -SelfTest` PASS, full suite green.

## Rollout / disclosure

v0.19.1 itself installs manually (it introduces the updater). From v0.19.1 → v0.20.0 onward, users on
`Mode=silent` (the default) auto-update. The v0.19.1 release notes + README must state this plainly and
show how to set `notify`/`off`.
