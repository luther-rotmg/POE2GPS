# POE2GPS release checklist

The automated layer (CI) covers the gate, the build, the tests, and the scrub self-test. The items
below require a **live PoE2 client** and cannot be automated — run them before tagging a release.

## Automated (also run by CI — confirm green locally)

```
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1 -SelfTest
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1          # PASS
powershell -ExecutionPolicy Bypass -File scripts/scrub-strings.ps1 -SelfTest  # PASSED
dotnet build POE2Radar.slnx -c Release                                        # 0 warnings, 0 errors
dotnet test  POE2Radar.slnx -c Release                                        # all pass
```

## Publish smoke

```
powershell -ExecutionPolicy Bypass -File publish.ps1 v0.0.0-smoke
```

- [ ] `publish/Overlay.exe` is produced (self-contained, ~75 MB).
- [ ] `Select-String -Path publish/Overlay.exe -Pattern 'Sikaka','NattKh' -SimpleMatch` finds nothing.
- [ ] Delete the smoke zip + `publish/` afterward.

## Manual — live game (run the published `Overlay.exe` as Administrator, PoE2 running)

- [ ] **Process randomization:** the process relaunches once under a random name; on exit, no stray
      `<Random>.exe` is left in the folder. The window title / tray label are not "POE2Radar".
- [ ] **Radar:** entities, terrain mask, POIs, and landmarks render on the in-game map.
- [ ] **Atlas + route planning:** with the Atlas open, tracked tiles get labels and guidance route
      lines; off-screen arrows point to tracked maps.
- [ ] **Navigation:** F6 draws a smoothed route to the nearest landmark/POI; F7 clears it.
- [ ] **Dashboard:** `http://localhost:7777` serves; there is **no pricing card**; `GET /state`
      carries **no character name** (`charName` is empty).
- [ ] **No input emission:** confirm the overlay never causes a flask/skill to fire — there is no F8,
      and the game receives nothing from the overlay.
- [ ] **No crash** without a game attached: launching with PoE2 closed prints "Game not running" and
      exits cleanly.
- [ ] **Objective Director:** enable it in the dashboard; enter a zone containing a catalog objective
      (seasonal event / side boss / transition) and confirm the overlay auto-routes to the
      highest-priority one, advances when it's completed, and falls back to the zone exit once optional
      content is cleared. Confirm a manual target pick (F6) overrides it until the next zone, and that
      `/state` carries no character name.
- [ ] **Catalog Builder:** in a zone, open the dashboard → Director tab; confirm uncatalogued POIs/
      landmarks appear under "Needs cataloguing" with friendly names; "Add" (pick category + priority)
      makes it disappear from the list and show under "Catalog" + drive the Director; "Remove" deletes it.
      Confirm `/api/seen-pois` carries no character name.
- [ ] **Entity Atlas:** in a zone, open the dashboard → Entity Atlas tab; confirm entities populate
      "Needs a name" (raw paths) and "Notable, uncatalogued"; typing a name + Save removes it from
      "Needs a name" AND shows that name on the radar/legend immediately; "Classify" adds a Director
      objective (it leaves the list). "Export pack" downloads `atlas-pack.json`; "Import pack" merges one
      back. Confirm `/api/entity-atlas` carries no character name, and that the endgame Atlas-map tab
      (which uses `/api/atlas`) still loads — i.e. the two were not conflated.
- [ ] **Quick-Target Cycler (keyboard):** in a zone, Ctrl+Alt+] / [ cycles the active radar target
      next/prev (priority then distance); Ctrl+Alt+1-9 jumps to a slot; Ctrl+Alt+0 clears. The on-screen
      "▸ N/M name" indicator shows + fades; the route follows the active target. Only fires while PoE2 is
      focused.
- [ ] **Quick-Target Cycler (controller — needs an XInput pad):** L3 = prev, R3 = next cycle the same way.
      Confirm normal gameplay is unaffected (R3 still toggles PoE2's life/mana number display — expected).
