# POE2GPS — Pending Manual Steps

**Purpose:** Every roadmap item that requires LO's real-world hands-on work (playing PoE2, harvesting assets, coordinating with community testers, acquiring the CN client, etc.) lives here. Everything on this list blocks a specific downstream roadmap item until LO does it at his desk.

**Source of truth:** [`docs/superpowers/specs/2026-07-07-v1.0-roadmap.md`](superpowers/specs/2026-07-07-v1.0-roadmap.md) — Short List / Long List item numbers below refer to that doc.

**Update pattern:** When LO completes an item, move it to the "Done" section at the bottom with the date + the roadmap item it unblocked, then delete the row from the active table.

---

## Active — ordered by earliest-blocking

| # | Item | What it unlocks | What LO does | Rough time | Blocks roadmap items |
|---|---|---|---|---|---|
| 2 | **Atlas icon PNG harvest** | Fills the empty `atlas-icons.json` slot values (currently all `""` with placeholder `_note`) so fogged-node content icons stop rendering blank | Enter each atlas mechanic zone once (Expedition, Ritual, Breach, Delirium, Harvest, Essence, plus pinnacle/citadel/waypoint variants); screenshot each map icon, crop to 32×32 PNG, name per the `_note` scheme; drop into `src/POE2Radar.Overlay/Web/Assets/atlas-icons/` and update the JSON | 2-3 hrs | Downstream cleanliness of Short List #7 (dashboard polish) + browser views Long List #37, #39 |
| 3 | **v0.20.1 "Roadclearing" RC smoke tester coordination** | Real gameplay QA before tagging v0.20.1 | Hand `v0.20.1-rc.1` to two Discord regulars (natural fit: Diamondsr + torx per credits); collect 30-min-of-mapping report; sign off the smoke checklist | ~1 day round-trip | v0.20.1 public tag |
| 4 | **Real player-heading Research probe** | Lets us drop the velocity-fallback and ship a real facing angle for the arrow + path polyline correctness | Run `POE2Radar.Research --heading` variants in-game; walk cardinal directions and record the field that tracks it independently of movement; hand LO the offset for `Poe2Offsets.cs` | 30-45 min | Long List #30 (real player heading) → gates cleaner Long List #19 (path polylines) look-ahead |
| 5 | **Quest-memory Research probe** | Wires `EnableQuestMemory` for real; ships Campaign GPS Part B | Run `Research --quest` variants across campaign checkpoints; capture the deep-pointer chain (`ServerDataStructure+0x3030` lead, `0xB4000000` sentinel per Roadmap D24) | 1-2 hrs | Long List #31 (quest-memory precision) + downstream Director maturation (#32) |
| 6 | **XP field Research probe** | Ships the deferred XP/hour Session HUD chip | Run `Research --xp`; verify the `Experience` int64 tracks across kills without character reload | 20-30 min | Long List #34 (XP/hour) |
| 7 | **Island Rumours live-validation** | Confirms the plan's BFS + tier table against the actual rumour selection screen | Enter an Expedition Uncharted Waters area with at least 3 unspent charges → open rumour-select screen → verify overlay panel labels each option with the correct tier from the embedded table | 45 min | Long List #26 (Island Rumours) |
| 8 | **Tencent CN client — recon-to-shipping** | Actual CN offset validation + module-scan variant + `UpdateUrl` split rollout to CN users | Acquire the WeGame/Tencent PoE2 client; run the CN variant of Research offset discovery; publish per-patch CN offsets alongside the international table | multi-session | Long List #33 (CN compatibility) |
| 9 | **Data-catalog harvest** (skill_gems / uniques / essences / ascendancy / base_items / sanctum_rooms) | Fills the ten `GAP:` items from the data-catalog audit; unlocks build-planner, unique naming, essence preview, gear-weight normalization | Run existing GGG `.dat` extraction (RePoE-adjacent tools) against the current patch's client, transform to the `Core/Game/*.json` schemas, PR each catalog as its own commit | 4-6 hrs initial + 30 min per patch after | Long List #45 (data-catalog gap fill) |
| 10 | **Positional inventory grid live-validation** | Ships the deferred inventory positional layout for Gear tab | Run `Research --inventory` on a live rare drop; capture the cell-index + box-dims deep-pointer path | 30-45 min | Long List #48 (positional inventory grid) |
| 11 | **Community pipeline flywheel spin-up habit** | Turns the receipt loop + credit block into a *visible* flywheel — no code, just a habit block for the first two releases after Short List #13 lands | Personally comment on each merged Contribute issue thanking the contributor by GH handle; include their name in the CHANGELOG credit block | 15 min per release | Short List #13 (contributor receipt) — no code, habit block after v0.21 lands |

---

## Done

- **2026-07-07 — Vitals offset re-validation** for PoE2 0.5.4 (unblocked v0.20.1 tag; Short List #1). LO confirmed shipped Life/Mana/ES constants are current with no drift since the 2026-07-02 ES slide (0x248→0x264 already captured). No Research run needed. TODO comment at `Poe2Offsets.cs:122` rewritten as a re-validation event note; README badge held at 0.5.4.
