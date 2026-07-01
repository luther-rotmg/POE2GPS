# v0.16.0 — Streaming & Presence — Design / Spec

**Date:** 2026-06-30
**Branch:** `feat/v0.16.0-streaming`
**Status:** Design approved (Ryan: "go"; Discord RP = neutral app identity + customizable text). Two features shipping together; zero new offsets.

## Goal

Two "share your session outward" features, both built on data the overlay already publishes:
1. **OBS overlay** — a transparent, stream-styled HTML page served on localhost that OBS captures as a Browser Source.
2. **Discord Rich Presence** — publish the current PoE2 session to Discord via its local IPC, under a neutral app identity with user-customizable text.

Both **read-only, zero new offsets** — they consume the existing `/state` / published `RadarState`.

## Compliance invariant (non-negotiable)

Strictly **read-only** of the game (no new memory reads/writes, no input, no pricing/reward-values). The two outward data flows are user-controlled and opt-in:
- **OBS overlay** stays on **localhost** (the same loopback HTTP server; the streamer chooses to capture it).
- **Discord RP** publishes session text to the **local Discord client** only (IPC pipe) — the user's explicit opt-in, off by default. Under a **neutral** Discord app identity (NOT "POE2GPS"), preserving the project's stealth ethos. No game data leaves the machine except what the user opts to broadcast.

`compliance-gate.ps1` + scrub stay green. README badge stays `0.5.4`. Version → `0.16.0`.

---

## Feature 1 — OBS overlay

### Behavior
A new HTTP route (`GET localhost:<ApiPort>/obs`) serves a self-contained, **transparent-background** HTML page styled for streaming. It polls the existing `/state` (and, where useful, `/api/preload` / session data) every ~1s and renders a **configurable set of widgets** in a clean corner-stack: session timer, zone timer, area name + level, zones, maps/hr, kills (N/M/R/U), XP-efficiency, current objective/GPS text. The streamer adds ONE **Browser Source** in OBS → `http://localhost:7777/obs` (transparent, sized to taste).

### Config (dashboard-driven)
- `ObsOverlay.Widgets` — which widgets to show (bool per widget: sessionTimer, zoneTimer, area, kills, mapsHr, xpEff, objective, …).
- `ObsOverlay.Theme` — a small style set: text colour, background panel opacity (0 = fully transparent chip-less), scale, corner/stack direction.
- Served regardless of enabled (it's just a page); a dashboard **"Open OBS overlay"** link + a copy-URL button + a live preview.

### Architecture
- `Web/ObsOverlayHtml.cs` (new) — the embedded HTML/CSS/JS raw-string (mirrors `DashboardHtml`), transparent body, polls `/state`, renders enabled widgets from a `?w=` query or from settings fetched via `/api/settings`.
- `Web/ApiServer.cs` — add the `case "/obs":` route returning the HTML; add `obsOverlay` to `ReadSettings`/`ApplySettings` (widget toggles + theme, whole-object `TryParseObsOverlay`).
- `Web/DashboardHtml.cs` — an "OBS Overlay" settings card (widget checkboxes + theme + the browser-source URL + a note on adding it in OBS).
- No new memory reads — reuses the published state.

### Testing
- The widget-selection + theme are config round-trip (API test-ish); the page itself is validated by live smoke (add as OBS Browser Source, confirm transparent + widgets update). A pure Core/JS-agnostic unit isn't required beyond the settings parse (`TryParseObsOverlay` exception-safety).

---

## Feature 2 — Discord Rich Presence

### Behavior
When enabled, POE2GPS connects to the **local Discord client's IPC pipe** and publishes a Rich Presence activity: **details** + **state** lines (user templates), a large image (neutral), and an **elapsed timer** (session start). Updated on Discord's rate limit (~1 update / 15 s). Off by default; no-ops cleanly if Discord isn't running; auto-reconnects if Discord restarts.

### Neutral identity + customizable text (the approved decision)
- Presence is published under a **neutral Discord Application** (registered on the Discord dev portal under a discreet, non-impersonating name — **NOT "POE2GPS"**). Its **Client ID** is embedded as the default.
- **One-time setup dependency:** registering a Discord app is an external account action; **Ryan registers the neutral app once and provides the Client ID** at the RP build step (I'll ask for it then). The Client ID is ALSO a settings field so any user can override with their own app.
- **User-customizable text:** `DiscordPresence.DetailsTemplate` (default `"{area}"`) + `.StateTemplate` (default `"Level {level} · {mapshr} maps/hr"`), with tokens `{area} {level} {zones} {mapshr} {kills} {xpeff} {time}` filled from the session/state each update. A pure formatter fills + clamps them (Discord caps each line at 128 chars).

### IPC protocol (Windows named pipe)
- Connect to `\\.\pipe\discord-ipc-0` (try `-0` … `-9`).
- Frame = `int32 opcode (LE)` + `int32 length (LE)` + `UTF-8 JSON`.
- Handshake: opcode **0**, `{ "v": 1, "client_id": "<clientId>" }`. Read the READY response.
- Update: opcode **1**, `{ "cmd":"SET_ACTIVITY", "args":{ "pid":<pid>, "activity":{ "details":"…", "state":"…", "timestamps":{"start":<unixSec>}, "assets":{"large_image":"logo","large_text":"…"} } }, "nonce":"<n>" }`.
- Throttle to ≥15 s between updates; only send when the presence text actually changed. Close the pipe on disable/shutdown.

### Architecture
- `Core/Presence/PresenceTemplate.cs` (new, pure) — `Format(string template, PresenceData data) → string` (token fill + 128-char clamp). Unit-tested.
- `Core/Presence/DiscordIpc.cs` (new) — the frame encoder (`EncodeFrame(int op, string json) → byte[]`) + pipe connect/handshake/send/reconnect. Frame-encode is unit-tested; the pipe I/O is thin + smoke-tested.
- `Overlay/RadarApp.cs` — a ~15 s cadence that builds `PresenceData` from the published `RadarState`/`SessionStats`, formats via `PresenceTemplate`, and pushes through `DiscordIpc` when enabled + changed. Runs off the hot path (its own timer or piggybacked on a low-rate tick). Gracefully handles "no Discord / no client id".
- `Config/RadarSettings.DiscordPresence` (new) — `Enabled` (default false), `ClientId` (default = the neutral app id, overridable), `DetailsTemplate`, `StateTemplate`, `ShowTimer` (bool).
- `Web/ApiServer` + `Web/DashboardHtml` — settings round-trip + a "Discord Rich Presence (opt-in)" card (enable, template fields with a token legend, client-id override, a live "current presence" preview).

### Testing
- Core unit tests: `PresenceTemplate.Format` (token fill, unknown-token passthrough, 128-char clamp, empty template); `DiscordIpc.EncodeFrame` (opcode + LE length + payload bytes correct). The live pipe connection + actual Discord display validated by smoke test (Discord running, RP on).

---

## Architecture / boundaries

- **Core (new):** `Presence/PresenceTemplate.cs` (pure), `Presence/DiscordIpc.cs` (frame encode + pipe). No new offsets, no game reads.
- **Overlay:** `Web/ObsOverlayHtml.cs` (new), `Web/ApiServer.cs` (`/obs` route + both settings), `Web/DashboardHtml.cs` (two cards), `RadarApp.cs` (RP cadence + feed), `Config/RadarSettings.cs` (`ObsOverlay` + `DiscordPresence`).
- Both reuse the existing loopback HTTP server + published `RadarState`.

## Out of scope (YAGNI)
- Discord "buttons"/join/spectate (party features) — presence display only.
- Streaming to services other than an OBS Browser Source (no direct RTMP etc.).
- Any new memory reads/offsets — both are views of existing data.
- The other scouted features (buff icons, Sanctum) — separate releases.

## Version
Ships as **v0.16.0 — Streaming & Presence**. README badge stays `0.5.4`. SDD flow. Discord RP's build task is gated on Ryan providing the neutral Client ID (one-time); OBS overlay has no gate.
