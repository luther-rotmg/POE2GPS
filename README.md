# POE2GPS

A strictly **read-only GPS / navigation overlay for Path of Exile 2**.

It attaches to the PoE2 client, reads game state directly out of process memory (no injection, no
hooks), and draws a terrain + entity radar and atlas route overlay on top of the game's map. It is a
focused, community-safe fork of [Sikaka/POE2Radar](https://github.com/Sikaka/POE2Radar): the
auto-flask keystroke feature and all economy/pricing network calls have been removed, and a
process-randomization layer (from [NattKh/POE2Radar](https://github.com/NattKh/POE2Radar)) has been
added.

## What it does — and never does

POE2GPS does three things **never**, and this is enforced by an automated compliance gate that fails
the build if any of them reappear (see [Compliance](#compliance--how-this-stays-safe)):

- **Never sends input to the game.** No `SendInput`, no simulated keystrokes or clicks — there is no
  auto-flask and no automation of any kind.
- **Never writes to or injects into the game process.** No `WriteProcessMemory`, no byte-patching,
  no DLL injection. The game is opened **read-only** (`PROCESS_VM_READ`).
- **Never makes economy/automation network calls.** No poe.ninja pricing, no MCP server.

What it *does*: read game memory and draw a navigation overlay + a localhost dashboard.

> **Honest note on risk.** Reading another process's memory is a gray area: GGG has historically
> been agnostic toward passive read-only overlays, but it is *tolerated, not officially blessed*.
> This tool removes the categories GGG explicitly prohibits (input automation, process modification)
> to stay in the lowest-risk category. It is a personal/educational tool; you are responsible for how
> you use it. SmartScreen/antivirus may warn on an unsigned memory-reading exe — that is expected.

## Features

- **Map overlay** — when the in-game map is open, draws the walkable-terrain mask + entity dots,
  projected player-centered onto the game's map.
- **Entity radar** — alive enemies (red), NPCs, chests, area transitions, other players, and **POIs**
  shown with a ring; optional world-space **HP bars** over monsters; dangerous rare/magic monster
  mods flagged.
- **Tile landmarks** — static features pulled from terrain tile data (boss arenas, transitions, …),
  shown the moment you enter an area, with community-curated friendly names.
- **Atlas overlay + route planning** — on the open Atlas, highlights/labels nodes by content, draws
  off-screen arrows to tracked maps, and auto-routes shortest-hop guidance lines to every tracked
  tile. Route planning is **draw-only** — it never drives input.
- **Ground / reward labels** — names of named ground drops, and the **names** of Ritual / Runeforge /
  Runeshape-monolith rewards drawn on the map. (No economy values — pricing was removed.)
- **Navigation** — pick any landmark, POI, or entity as a destination; the overlay draws a smoothed
  A* route to it (on the in-game map, or as world waypoints when it's closed). Multi-select, each
  route its own color.
- **Customizable icons & display rules** — per-rule icon shape/color/size/opacity, editable live in
  the dashboard; drop your own `*.svg` into the `icons/` folder to add or override any icon.
- **Process randomization** — the overlay relaunches under a random-named hardlink, uses a random
  window class/title, ships under a neutral assembly name, never exposes your character name on the
  dashboard, and the release build is string-scrubbed of identifying credit/URL tokens.
- **Web dashboard** (`http://localhost:7777`, or **F12** in-game) — a local control panel: a
  searchable list of every entity/landmark you can click to navigate to, plus radar/icon/atlas
  settings tabs. Served same-origin only; setting/navigation writes are loopback-gated. Read
  endpoints: `GET /state`, `/entities`, `/landmarks`, `/api/icons`.

## Download (no build required)

Grab the latest **`POE2GPS-vX.Y.Z-win-x64.zip`** from this repository's Releases page, unzip, and run
`Overlay.exe` **as Administrator** (reading another process's memory requires it) with PoE2 already
running. The build is self-contained — no .NET install needed. (The exe relaunches itself once under
a random name; that is the process-randomization feature, not malware.)

## Build from source

Requires the **.NET 10 SDK**, Windows x64.

```
dotnet build POE2Radar.slnx
# launch with PoE2 already running and you in a zone (run as Administrator):
src\POE2Radar.Overlay\bin\Debug\net10.0-windows\Overlay.exe
```

To **exit**: right-click the system-tray icon → **Exit**, or press **F9** (or close the console).

Hotkeys: **F9** quits; **F12** opens the web dashboard; **F6** routes to the nearest landmark/POI and
**F7** clears routes; **F10** (with the Atlas open) inspects the hovered tile and sets a route
start/end. All other settings live in the dashboard. (There is no F8 — auto-flask was removed.)

## Compliance — how this stays safe

The three invariants above are not just a promise; `scripts/compliance-gate.ps1` scans the shipped
source and **fails the build** if any input-emission or process-write API (e.g. `SendInput`,
`WriteProcessMemory`, `VirtualProtectEx`, `CreateRemoteThread`) appears, or if `OpenProcess` ever
requests write access. It runs in CI on every push/PR and can be run locally:

```
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1
```

This also protects against accidentally re-introducing removed code when merging upstream updates
from Sikaka — see [docs/upstream-merge.md](docs/upstream-merge.md).

## Architecture

Three projects:

- `src/POE2Radar.Core` — memory plumbing (`OpenProcess` read-only + `NtReadVirtualMemory`), the PoE2
  offset table (`Game/Poe2Offsets.cs`), the live read layer (`Game/Poe2Live.cs`), and the
  identity-neutral `Stealth/RandomName` generator.
- `src/POE2Radar.Overlay` — the overlay `.exe` (published as `Overlay.exe`): attaches, AOB-resolves
  the game roots, runs the tick loop, renders the Direct2D overlay, and serves the dashboard. Reads
  only — it emits no input and writes nothing to the game.
- `src/POE2Radar.Research` — dev-time offset discovery/validation tools. Never linked into the
  overlay binary; excluded from the compliance gate.

## Offsets & patches

PoE2 memory offsets drift with game patches. Validated offsets live in `Game/Poe2Offsets.cs`. After a
patch that breaks reads, use the `POE2Radar.Research` probes to re-discover them, or pull updates from
Sikaka upstream (see [docs/upstream-merge.md](docs/upstream-merge.md)).

## Credits

Forked from **[Sikaka/POE2Radar](https://github.com/Sikaka/POE2Radar)** (MIT); the
process-randomization layer is adapted from **[NattKh/POE2Radar](https://github.com/NattKh/POE2Radar)**
(MIT). Memory-layout research draws on the open-source **GameHelper2** project (not redistributed
here; only independently re-validated offsets are recorded). See [NOTICE](NOTICE).

## License

MIT — see [LICENSE](LICENSE).
