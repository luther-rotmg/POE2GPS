# ExileCampaigns2 Data Mirror

Upstream: https://github.com/syrairc/ExileCampaigns2
Upstream author: syrairc
Import date: 2026-07-07 (POE2GPS v0.21 "Guided Campaign")
Upstream commit: TODO(syrairc-hash)
License: TODO(syrairc-license)

## Files in this directory

| File          | Upstream path                              | Approx size |
|---------------|--------------------------------------------|-------------|
| `route.json`             | `Data/poe2/route/route.json`      | 602 KB |
| `overrides.json`         | `Data/poe2/route/overrides.json`  | 87 KB  |
| `area-objectives.json`   | `Data/poe2/area-objectives.json`  | 9.4 KB |
| `area-transitions.json`  | `Data/poe2/area-transitions.json` | 14.8 KB|
| `area-targets.json`      | `Data/poe2/area-targets.json`     | 4.2 KB |
| `xp_curve.json`          | `Data/poe2/xp_curve.json`         | 1.2 KB |

## Modification policy

Downstream modifications land in `overrides.json` only — **never** touch
`route.json` in place. `overrides.json` is the intended patching surface;
diffs against upstream `route.json` become impossible if it drifts locally.

Re-import protocol: replace all six files with fresh upstream copies in one
commit, keep `overrides.json` semantics stable (upstream owns the schema),
then bump the `Upstream commit` line above.

## Attribution

v0.21 ships the ExileCampaigns2 advance-engine and route data with syrairc's
verbal permission. Formal license + pinned commit hash land in
`EC2-ATTR-FORMALIZE` via grep-and-swap against the two `TODO(syrairc-*)`
sentinels above. See `README.md` and `CHANGELOG.md` for the full credit block.
