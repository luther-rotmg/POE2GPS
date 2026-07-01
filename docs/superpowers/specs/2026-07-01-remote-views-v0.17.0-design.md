# v0.17.0 — Remote Views — Design / Spec

**Date:** 2026-07-01
**Branch:** `feat/v0.17.0-remote-views`
**Status:** Design approved (Ryan: "go"). Two features shipping together; zero new offsets, read-only. Everything perf-hitting defaults **OFF**.

## Goal

Let the overlay's existing localhost data be **viewed from other devices** (a stream-capture PC, a Raspberry Pi second screen), and add a **standalone web minimap** so the user never has to tab open the in-game map. Both are views of data the overlay already reads — no new memory reads, no new offsets.

1. **LAN access** — an opt-in setting that binds the HTTP server to all interfaces so `http://<lan-ip>:7777/obs` (and `/map`, `/state`) work from another machine on the same network. **Writes stay loopback-locked.**
2. **Web minimap `/map`** — a self-contained HTML canvas page that draws walkable terrain + entity dots + player blip, updating ~1 Hz, suitable for a second screen / Pi.

## Compliance invariant (non-negotiable)

Strictly **read-only** of the game: no new memory reads/writes, no input, no pricing/reward-values. Both features consume data the overlay already publishes (`/state`, `/entities`, `/landmarks`) plus the already-read terrain grid. `compliance-gate.ps1` + `scrub-strings.ps1 -SelfTest` stay green. README badge stays `0.5.4`. Version → `0.17.0`.

**Security posture (the load-bearing part):**
- LAN access is **opt-in, default off**. When off, behavior is byte-identical to today (`http://localhost:7777/`).
- When on, the listener binds `http://+:{port}/` (all interfaces). **All 23 POST/write endpoints already gate on `IsLoopbackHost`** (`ApiServer.cs` ~2053) — a LAN peer can **read** (`/state`, `/entities`, `/obs`, `/map`, `/api/*` GETs) but **cannot change any setting**. This is preserved and verified, not newly built.
- Reads are unauthenticated over LAN **by design** — the user's explicit opt-in on a trusted network. The UI says so plainly.

---

## Feature 1 — LAN access

### Behavior
A new setting **`AllowLanAccess`** (default `false`). On app start, `ApiServer` chooses its listener prefix:
- `false` → `http://localhost:{port}/` (today's behavior, unchanged).
- `true` → `http://+:{port}/` (binds every interface; reachable as `http://<lan-ip>:{port}/…`).

The prefix is fixed at `HttpListener.Start()`, so **changing this setting requires an app restart** — the dashboard states this and the setting card shows a "restart to apply" note. Writes remain `IsLoopbackHost`-gated regardless, so LAN peers stay read-only.

### Windows specifics (documented in the UI, handled gracefully in code)
- Binding `http://+:{port}/` normally needs either admin rights or a `netsh http add urlacl` reservation. The overlay already runs elevated for memory reads, so the admin bind path works; **if the bind throws** (`HttpListenerException`), `Start()` catches it, **falls back to `localhost`**, and surfaces a clear one-line reason in the console + a dashboard status flag (`lanBindFailed`) so the user isn't left wondering.
- First LAN connection will prompt **Windows Firewall** (allow inbound TCP 7777). The dashboard card notes this once.

### Config (dashboard-driven)
- `RadarSettings.AllowLanAccess` (bool, default false).
- Dashboard "Remote Access (LAN)" card: the toggle, a **restart-required** note, the **firewall** note, a plain-English "reads only over LAN; nobody on your network can change your settings" reassurance, and a **detected LAN URL** line (`http://<lan-ip>:7777/obs` and `…/map`) computed client-side or from a tiny `/api/lan-info` GET returning the machine's LAN IPv4(s) + port (read-only, loopback-or-LAN reachable — it exposes only the local IP, which the requester already knows).

### Architecture
- `Config/RadarSettings.cs` — add `AllowLanAccess` (default false).
- `Web/ApiServer.cs` — constructor takes `bool allowLanAccess`; prefix line (`:144`) becomes conditional. `Start()` wraps `_listener.Start()` in try/catch → localhost fallback + `LanBindFailed` flag exposed via `/state` (or `/health`). New `GET /api/lan-info` → `{ port, addresses: [ "192.168.x.y", … ], bound: "lan"|"localhost", bindFailed: bool }` (enumerates `NetworkInterface` IPv4 non-loopback; pure local info).
- `RadarApp.cs` — pass `_settings.AllowLanAccess` into the `ApiServer` constructor (instantiation ~656).
- `Web/DashboardHtml.cs` — the "Remote Access (LAN)" card.

### Testing
- Prefix selection is a pure branch — a small unit/round-trip test on the setting parse (`AllowLanAccess` default false, clamps/parses). The bind-fallback + firewall behavior is validated by **live smoke** (flip on, restart, hit `http://<lan-ip>:7777/obs` from the phone/stream PC). LAN write-gating is already covered by the existing `IsLoopbackHost` tests; a doc note reaffirms LAN peers get 403 on writes.

---

## Feature 2 — Web minimap `/map`

### Behavior
A new route `GET /map` serves a self-contained HTML page: a dark, full-viewport **canvas** that draws the current zone's **walkable terrain** (faint), **entity dots** (coloured by category/rarity, same palette spirit as the overlay), **landmarks**, and the **player blip** centred, updating ~1 Hz. Designed to sit fullscreen on a second monitor / Raspberry Pi. Reuses the existing `/entities`, `/landmarks`, and `/state` endpoints for the live layer; adds one **terrain endpoint** for the map shape.

**Top-down radar projection** (the approved style) — clean and glanceable on a small screen, not the game's isometric skew. The page centres on the player, applies its own zoom (fit-to-canvas + a zoom control), and plots everything in grid space.

### Data path (all already-read data)
- **Terrain:** `Poe2Live.Terrain()` returns `TerrainData(byte[] Walkable, int Width, int Height)`; `RadarApp` already holds the current zone's terrain in `_terrain` (populated per zone, invalidated on zone change). It is **not** currently exposed to the API. Add `GET /api/map?hash=<areaHash>` returning `{ areaHash, width, height, walkable: <base64 of the byte[]> }`. Cached per `areaHash`; the page re-fetches only when `areaHash` changes (terrain is static per zone). No new memory read — it serves the byte[] the world loop already produced.
- **Entities:** existing `GET /entities` (id, category, metadata, name, poi, rarity, `x`=Grid.X, `y`=Grid.Y, hpCur, hpMax, dist; filters `?category=&alive=&radius=&limit=`).
- **Landmarks:** existing landmark data (grid coords + name). If live landmark positions aren't already on a GET endpoint, expose them via `/state` (`landmarks: [{x,y,name,kind}]`) or a `/api/map-landmarks` GET — grounding confirms the store exists (`_landmarkStore`); pick whichever is already serialized, else add the minimal GET.
- **Player + zone:** existing `/state` (`player.x`, `player.y`, `areaHash`, `areaName`, `areaLevel`). Add lightweight `mapData` to `/state` if useful (`terrainWidth`, `terrainHeight`) so the page can size before fetching terrain — optional; the `/api/map` response already carries dims.

### Rendering (client-side, no server image encoding)
The page builds terrain from the raw walkable bytes directly in the browser (an offscreen `ImageData` pass: walkable cells → faint colour, non-walkable → transparent), then draws entity dots + landmarks + the player blip on top each 1 Hz tick. **No server-side PNG encoder** (avoids a new dependency; the walkable grid is ~small and gzips well, sent once per zone). A JS port of the top-down grid→screen transform (centre-on-player + zoom + flip-Y) lives in the page.

### Config (dashboard-driven)
- The `/map` page is always servable (it's just a page), but its **polling/terrain reads only happen while a client has it open** — so it contributes **zero cost by default** (nobody's viewing it). No always-on toggle needed for the read side; the "perf hit" is strictly per active viewer.
- Optional `MinimapSettings` (dot categories to show, zoom default, dot size) — small, dashboard-editable, defaults mirror the overlay. Not required for v1; a query-string (`?dots=monsters,poi`) can cover it initially. **Default view = the essentials** (monsters, POI, player, landmarks).
- Dashboard "Web Minimap" card: an **Open** link + copy-URL (localhost and, if LAN on, the LAN URL) + a one-line "put this fullscreen on a second screen / Pi" note.

### Architecture
- `Web/ApiServer.cs` — `case "/map":` → `WriteHtml(MapPageHtml.Page)`; `case "/api/map":` → terrain JSON (base64 walkable) cached per `areaHash` (an `AreaHash→(w,h,base64)` one-entry cache on the API side, invalidated when the incoming/publisher hash changes). Terrain is provided to `ApiServer` via a new `Func<TerrainData?>` (or `Func<(byte[],int,int,uint)?>`) publisher, wired like the existing `_state`/atlas providers — `RadarApp` supplies the current `_terrain` + its `areaHash`.
- `Web/MapPageHtml.cs` (new) — the embedded HTML/CSS/JS raw-string (mirrors `ObsOverlayHtml`/`DashboardHtml`): canvas, terrain builder from base64 walkable, 1 Hz poll of `/state` + `/entities`, top-down projection, zoom/pan, player-centred.
- `RadarApp.cs` — expose the current terrain (+areaHash) to the `ApiServer` constructor as a provider delegate (published like other snapshots; the world loop already owns `_terrain`).
- `Web/DashboardHtml.cs` — the "Web Minimap" card.
- `Config/RadarSettings.cs` — optional `MinimapSettings` (YAGNI-minimal; may ship as query-string only in v1).

### Testing
- Terrain endpoint: a Core-adjacent round-trip on the base64 encode/decode of a small walkable grid (dims preserved, bytes preserved) — pure, unit-testable. The page render is **live smoke** (open `/map` in a browser + on the Pi, confirm terrain shape + dots + player track the game).
- The `/api/map` per-hash cache invalidation is unit-checkable at the parse/cache-key level.

---

## Architecture / boundaries

- **Core:** none required (terrain already read). Optional: a tiny pure base64/grid helper if it makes the endpoint testable in Core.
- **Overlay:**
  - `Config/RadarSettings.cs` — `AllowLanAccess` (+ optional `MinimapSettings`).
  - `Web/ApiServer.cs` — conditional prefix + bind-fallback; `/api/lan-info`; `/map` + `/api/map` routes + per-hash terrain cache; terrain provider param.
  - `Web/MapPageHtml.cs` (new).
  - `Web/DashboardHtml.cs` — "Remote Access (LAN)" + "Web Minimap" cards.
  - `RadarApp.cs` — pass `AllowLanAccess` + terrain provider into `ApiServer`.

## Data flow
1. World loop already reads terrain → `_terrain` (per zone). RadarApp publishes it (delegate) to `ApiServer`.
2. `/map` page loads → fetches `/api/map?hash=<h>` once per zone (terrain) → polls `/state` + `/entities` at 1 Hz → draws.
3. If `AllowLanAccess` on, the same pages/endpoints are reachable at `http://<lan-ip>:7777/…`; writes 403 unless loopback.

## Error handling
- LAN bind failure → catch → localhost fallback → `lanBindFailed` surfaced; never crashes startup.
- `/api/map` before terrain is ready (loading/no zone) → `204`/empty; page shows "waiting for zone".
- Zone change mid-view → page detects `areaHash` change on next `/state` poll → re-fetches terrain.
- All API exceptions already return a generic 500 body (no memory internals on the wire) — unchanged.

## Out of scope (YAGNI)
- Isometric minimap (top-down only for v1; iso is a possible later dial).
- Authentication / TLS for LAN (opt-in, trusted-network, read-only-over-LAN is the stated model; no accounts/passwords — that would violate the credential rules anyway).
- Serving the minimap as a server-rendered image (client-side canvas only).
- Any new memory reads/offsets.
- Buff icons / Sanctum (later releases).

## Version
Ships as **v0.17.0 — Remote Views**. README badge stays `0.5.4`. SDD flow. Both features default off / zero-cost-when-unviewed. Discord announcement drafted at release (themed, per standing directive).
