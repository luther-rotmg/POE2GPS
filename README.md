<div align="center">

# 🧭 POE2GPS

### Your turn-by-turn GPS for the wilds of Wraeclast.

*A strictly **read-only** navigation overlay for Path of Exile 2 — it shows you where to go, and never touches your game.*

[![CI](https://github.com/luther-rotmg/POE2GPS/actions/workflows/ci.yml/badge.svg)](https://github.com/luther-rotmg/POE2GPS/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/luther-rotmg/POE2GPS?sort=semver&label=release)](https://github.com/luther-rotmg/POE2GPS/releases)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Windows x64](https://img.shields.io/badge/Windows-x64-0078D6)
![License: MIT](https://img.shields.io/badge/license-MIT-blue)
<br>
![Input: read-only](https://img.shields.io/badge/input-read--only-2ea043)
![No process writes](https://img.shields.io/badge/process-never%20written-2ea043)
![No automation](https://img.shields.io/badge/automation-none-2ea043)

<img src="docs/img/poe2gps.png" alt="POE2GPS drawing a green GPS route across a PoE2 zone, with a named-landmark legend and radar dots" width="800">

<sub>The green line is your route to the next objective. The legend names what matters — bosses, transitions, even a *Support Gem* memorial. You still drive.</sub>

</div>

---

## ✨ Why POE2GPS

You attach it, it reads the game's map out of memory, and it **draws a radar + a route line** to wherever you're headed. No more squinting at the minimap or alt-tabbing to a wiki — the path is on your screen. And because it's a focused, community-safe fork of [Sikaka/POE2Radar](https://github.com/Sikaka/POE2Radar), it deliberately strips everything that could get you flagged.

## 🛡️ What it does — and never does

POE2GPS does three things **never** — and an automated compliance gate *fails the build* if any of them sneak back in:

| 🚫 Never | ✅ Instead |
|---|---|
| Sends input to the game (no `SendInput`, no auto-flask, no automation) | Just **draws** — you press every key yourself |
| Writes to / injects into the game (no `WriteProcessMemory`, no byte-patching) | Opens the game **read-only** (`PROCESS_VM_READ`) |
| Phones home (no poe.ninja pricing, no telemetry) | Everything is **local** |

> **Honest note on risk.** Reading another process's memory is a gray area — GGG has long been agnostic toward passive read-only overlays, but it's *tolerated, not blessed*. POE2GPS removes the categories GGG explicitly prohibits (input automation, process modification) to sit in the lowest-risk bucket. It's a personal/educational tool; you're responsible for how you use it. SmartScreen/AV may warn on an unsigned memory-reading exe — expected.

## 🗺️ Features

- 🛰️ **Entity radar** — enemies, NPCs, chests, transitions, players, and POIs; optional world-space HP bars; dangerous rare/magic mods flagged.
- 🧱 **Terrain + map overlay** — the walkable-terrain mask and entity dots, projected onto the in-game map.
- 📍 **Tile landmarks** — boss arenas, transitions, reward rooms, surfaced the moment you enter a zone, with community-curated names.
- 🧭 **Navigation** — pick any landmark/POI/entity and get a smoothed A* route drawn to it (on the map, or as world waypoints). Multi-select, each its own color. **Draw-only — never drives input.**
- 🌌 **Atlas overlay + route planning** — labels nodes by content, off-screen arrows to tracked maps, shortest-hop auto-routes.
- 🏷️ **Reward/name labels** — names of ground drops and Ritual / Runeforge / monolith rewards (no economy values).
- 🧪 **Objective Director** *(experimental, off by default)* — auto-routes you through a zone's objectives in priority order: **seasonal event → side bosses → side zones → exit**. Still maturing — [roadmap below](#-roadmap).
- 🎨 **Customizable icons & display rules** — per-rule shape/color/size, editable live; drop your own `*.svg` into `icons/`.
- 🕵️ **Process randomization** — relaunches under a random-named hardlink, randomized window class/title, neutral assembly name, character name never exposed, release binary string-scrubbed.
- 🖥️ **Web dashboard** (`http://localhost:7777`, or **F12**) — click any entity/landmark to navigate to it; tune radar/icons/atlas. Local-only, loopback-gated.

## 🚀 Download (no build required)

Grab the latest **`POE2GPS-vX.Y.Z-win-x64.zip`** from the [**Releases**](https://github.com/luther-rotmg/POE2GPS/releases) page, unzip, and run **`Overlay.exe` as Administrator** (memory reads require it) with PoE2 already running. Self-contained — no .NET install needed. *(It relaunches itself once under a random name — that's the process-randomization feature, not malware.)*

## ⌨️ Hotkeys

| Key | Action |
|---|---|
| **F12** | open the web dashboard |
| **F6 / F7** | route to nearest landmark/POI / clear routes |
| **F10** | (Atlas open) inspect hovered tile, set route start/end |
| **F9** | quit (or right-click tray → Exit) |

*(No F8 — auto-flask was removed on purpose.)*

## 🔧 Build from source

Requires the **.NET 10 SDK**, Windows x64.

```bash
dotnet build POE2Radar.slnx
# then, with PoE2 running and you in a zone (as Administrator):
src\POE2Radar.Overlay\bin\Debug\net10.0-windows\Overlay.exe
```

## ✅ Compliance — how this stays safe

The three invariants above aren't just a promise — `scripts/compliance-gate.ps1` scans the shipped source and **fails the build** if any input-emission or process-write API (`SendInput`, `WriteProcessMemory`, `VirtualProtectEx`, `CreateRemoteThread`, …) appears, or if `OpenProcess` ever asks for write access. It runs in CI on every push/PR, and locally:

```bash
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1
```

It also catches accidentally re-introducing removed code when merging upstream from Sikaka — see [docs/upstream-merge.md](docs/upstream-merge.md).

## 🏗️ Architecture

- **`src/POE2Radar.Core`** — read-only memory plumbing (`OpenProcess` read-only + `NtReadVirtualMemory`), the PoE2 offset table, the live read layer, the `Stealth/RandomName` generator, and the `Campaign/` objective catalog + director.
- **`src/POE2Radar.Overlay`** — the overlay `.exe` (`Overlay.exe`): attaches, AOB-resolves the game roots, runs the tick loop, renders the Direct2D overlay, serves the dashboard. Reads only.
- **`src/POE2Radar.Research`** — dev-time offset discovery tools. Never shipped; excluded from the gate.

PoE2 offsets drift with patches — validated values live in `Game/Poe2Offsets.cs`; re-discover via the Research probes or pull updates from Sikaka.

## 🧪 Roadmap

The **Objective Director** is the headline work-in-progress. Next up: deep detection/cataloging of every relevant POI (seasonal events, passive-point upgrades, free skill/support gems), richer priority tiers, and — the dream — quest-aware cross-zone guidance that reads your quest log and points you to the right zone.

## 🙏 Credits

Forked from **[Sikaka/POE2Radar](https://github.com/Sikaka/POE2Radar)** (MIT); process-randomization adapted from **[NattKh/POE2Radar](https://github.com/NattKh/POE2Radar)** (MIT). Memory-layout research draws on **GameHelper2** (not redistributed; only re-validated offsets recorded). See [NOTICE](NOTICE).

## 📜 License

MIT — see [LICENSE](LICENSE).
