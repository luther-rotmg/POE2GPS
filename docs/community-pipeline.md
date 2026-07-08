# Community Pipeline — Maintainer Guide

This document describes the end-to-end ritual for collecting, reviewing, and merging
community-submitted entity packs into the embedded data tables.

---

## Overview

Players encounter unnamed entities, name them via the in-overlay Entity Atlas, then submit their
pack with one click. Submissions arrive as GitHub Issues. The maintainer reviews and approves
them; per release, a script folds approved packs into the shipped JSON tables so better coverage
reaches everyone automatically.

---

## Step 1 — Deploy the Cloudflare Worker (once)

The Worker (`cloudflare-worker/`) accepts POST requests from the overlay and opens a GitHub Issue
on your behalf.

1. `cd cloudflare-worker && npm install && npx wrangler deploy`
2. Copy the deployed URL (e.g. `https://poe2gps-submit.example.workers.dev`).
3. In `RadarSettings`, set `DefaultContributeUrl` to that URL and ship a release.

From that point on, end-users get a **Contribute** button in the overlay that submits their pack
in one click — no GitHub account required.

---

## Step 2 — Submissions arrive as Issues

Each submission creates a GitHub Issue with:

- Labels: `community-pack` + `needs-review` (applied automatically by the Worker).
- Title: `[Community Pack] <area> — <N> names, <M> objectives`.
- Body: a parsed summary table + the raw pack JSON in a fenced ` ```json ``` ` block.

Browse open issues filtered by `community-pack + needs-review` to see the queue.

---

## Step 3 — Review and approve (or reject)

For each issue:

- **Approve:** Add the `approved` label (remove `needs-review` if desired). The merge script will
  pick it up.
- **Reject:** Close the issue. Closed issues are ignored by the script.

Guidelines for approval:
- Names should match the visible in-game entity name (not a player-invented label).
- Metadata paths that look malformed or suspiciously long should be scrutinized.
- Duplicate keys: the script keeps the curated/existing value, so approving a duplicate is safe
  but redundant.

---

## Step 4 — Per-release merge (run locally)

After approving a batch of issues and before tagging a release:

```bash
# Dry run — shows what would change without writing files
python resources/poe2-data/merge_community.py --dry-run

# Live run — folds approved packs into entity_names.json + labels.json
python resources/poe2-data/merge_community.py
```

The script:
1. Calls `gh issue list -R luther-rotmg/POE2GPS --label community-pack --label approved --state all`
   to fetch all approved issue bodies.
2. Parses the ` ```json ``` ` pack from each body via `extract_pack`.
3. Folds `names` into `src/POE2Radar.Core/Game/entity_names.json` — **curated names win**
   (existing entries are never overwritten).
4. Collects `objectives[].category` values and appends any **novel** categories (not already in
   any group) to `src/POE2Radar.Overlay/Web/labels.json` under a `"Community"` group.
5. Prints a stable input-derived summary:
   `atlas fold: N approved pack(s), K unique names, C unique categories`. The delta
   against the shipped tables surfaces via `git diff` on the written data files.
6. Prints a `### Community contributors` markdown block listing every unique `@handle`
   (case-insensitive sort) with links to the merged issues (`[#42](...)`, `[#101](...)`).
   Copy the block verbatim above the themed body of `CHANGELOG.md` for this release.
   Deleted / bot accounts render as `@unknown`.

Requires `gh` CLI authenticated with repo access. The dry-run needs no write permissions.

After a successful live run:

1. Paste the printed `### Community contributors` block above the themed body of
   `CHANGELOG.md` for the release you're cutting.
2. Commit the updated data files together with the CHANGELOG entry:

```bash
git add src/POE2Radar.Core/Game/entity_names.json src/POE2Radar.Overlay/Web/labels.json CHANGELOG.md
git commit -m "data: fold community packs (vX.Y.Z)"
```

Thank each contributor by hand on their merged issue as a personal follow-up (habit
tracked in PMS-11). The merge script deliberately does **not** post `--comment` or
auto-close issues — the personal touch is the point for the first two release cycles.

---

## Relationship to `merge_atlas_packs.py`

`resources/poe2-data/merge_atlas_packs.py` remains for **direct file-attached packs** — e.g.,
packs attached to older issues or shared out-of-band. It takes explicit file paths as arguments
and has an `--overwrite` flag for maintainer-curated corrections.

`merge_community.py` is the automated path for the issue-based pipeline described here.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `gh: command not found` | `gh` CLI not installed | `winget install GitHub.cli` or https://cli.github.com |
| `Error: authentication required` | Not logged in | `gh auth login` |
| `0 approved pack(s)` but issues exist | Issues lack the `approved` label | Label them on GitHub |
| Pack not parsed | Issue body JSON block malformed | Check the raw issue body for a valid ` ```json {...} ``` ` block |
| Novel label already exists | Category name matches an existing label exactly | No action needed — idempotent |
