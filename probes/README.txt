POE2GPS — one-click probe launchers
===================================

These run the read-only POE2Radar.Research memory probes for you and save the
output where Claude can read it. No typing, no terminals.

HOW TO USE
----------
1. Make sure Path of Exile 2 is running and you are loaded into a zone.
2. Double-click the .bat file that matches what you're checking (list below).
3. Click "Yes" on the Windows admin prompt (the probes need Admin to read PoE2's
   memory — this is read-only; nothing is ever written to the game or sent to it).
4. The first launch builds the probes once (~15 sec). After that they're instant.
5. The output shows in the window AND is saved to:  probes\output\<name>.txt
6. Just tell Claude  "<name> done"  (e.g. "core done"). Claude reads the file.

WHICH FILE TO USE
-----------------
1 - core ............ chain + info + vitals + rarity.  Be in any normal zone.
                      (The big one — confirms the tool reads the live patch.)
2 - items ........... inventory + item mods.  Have identified, modded gear in bags.
3 - atlas ........... atlas-probe + atlas-graph.  OPEN THE ATLAS MAP first.
4 - camera .......... only if HP bars / item labels look mis-placed in-game.
5 - tiles ........... landmark/tile list.  Be in a non-town zone.
6 - xp .............. finds the XP offset.  Run it while killing monsters.
7 - quest ........... quest-state dump.  Run right AFTER advancing a quest step.
8 - watch ........... zone-change logger.  Leave running, walk through 2-3 zones,
                      then CLOSE the window.
9 - metadata ........ dumps nearby entity metadata. Use if a mechanic AUDIO CUE
                      didn't fire — stand near that mechanic and run it.

NOTES
-----
- If a window says "PoE2 not running", load into a zone and run it again.
- "8 - watch" runs until you close it; everything is saved as it goes.
- Output files in probes\output\ are local-only (git-ignored). Re-running a
  launcher overwrites its own file.
