# Contributing to the Entity Atlas -- Deprecated page

> **This page has moved.** The Entity Atlas contributor flow is now folded into the
> unified community-pack pipeline. See **[CONTRIBUTING.md](../CONTRIBUTING.md)** at the
> repo root for the current path (atlas names + buff metadata + preload metadata all
> funnel through the same rail as of v0.21).

## For players -- one-click Contribute (unchanged)

1. Run the overlay, open the dashboard (F12 / `localhost:7777`) -> **Entity Atlas** tab.
2. Name entities under **Needs a name** -- your radar updates instantly (local override
   in `config/entity_names_user.json`).
3. Click **Contribute names**. The dashboard POSTs your pack to the community Worker
   (`/submit-atlas`), which opens a labelled issue on your behalf.

Only the `names` map is sent. No character name, position, or account data leaves your
machine.

## For maintainers -- use `merge_community.py`

As of v0.21 the single merge rail is `merge_community.py`. It handles atlas / buff /
preload submissions from one command:

```bash
# Fold approved community-pack submissions into the entity_names.json seed.
python resources/poe2-data/merge_community.py --catalog names --state open
```

`merge_community.py` reads issues by label. Pre-v0.21 submissions were labelled
`atlas-submission`; the v0.21 relabel script
(`resources/poe2-data/relabel-atlas-issues.sh`) added `community-pack` to every open
atlas-submission issue so nothing was orphaned.

The old script `merge_atlas_packs.py` is retained in-tree but prints a deprecation
banner on every invocation. Please do not use it for new releases.

## Why the change?

Buff and preload contributions ship through the same funnel as of v0.21, so one merge
script + one label (`community-pack`) covers all three catalogs. See root
[CONTRIBUTING.md](../CONTRIBUTING.md) for the full contributor flow.
