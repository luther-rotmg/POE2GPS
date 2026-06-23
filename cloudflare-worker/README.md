# POE2GPS Contribute Worker

The project's single community-pack collector. The **maintainer** deploys this once; end-users never
touch it. It receives non-identifying `{names, objectives}` packs from the POE2GPS dashboard, auto-filters
junk (profanity / gibberish / over-length / identifying fields), and files clean submissions as reviewable
GitHub Issues labelled `community-pack` + `needs-review` with a parsed summary. The GitHub token lives
only as a Worker secret — never in the shipped overlay.

## Deploy (maintainer only)

1. Install Wrangler: `npm i -g wrangler` and `wrangler login`.
2. Create a **fine-grained** GitHub PAT scoped to **Issues: Read and write** on `luther-rotmg/POE2GPS` only.
3. From this directory:
   ```
   wrangler deploy
   wrangler secret put GITHUB_TOKEN   # paste the PAT
   ```
4. Copy the deployed `https://poe2gps-contribute.<you>.workers.dev` URL.
5. In the POE2GPS dashboard → Settings → **Contribute URL**, paste that URL.

Now the Entity Atlas **Contribute** button uploads the pack in one click.

## Auto-filter

Before filing any issue, the Worker rejects:

- **Profanity / slurs** — word-boundary matched against a conservative list.
- **Gibberish** — long consonant runs or near-random character distributions.
- **Identifying fields** — any key named `charname`, `character`, `account`, etc.
- **Oversized payloads** — hard cap at 256 KB.
- **Duplicates** — within a single submission, repeated meta-keys are deduplicated.

Submissions with nothing valid after filtering are rejected with `400 nothing valid after filtering`.

## Moderation workflow

1. Submissions arrive as GitHub Issues tagged `community-pack` + `needs-review`.
2. Each issue shows a **parsed summary** (name count, objective count, categories used, sample names)
   plus the full pack JSON in a collapsible `<details>` block.
3. **Review** the issue. If it looks good, add the `approved` label. To reject, simply close the issue.
4. At each release, fold approved submissions into the catalog:
   ```
   python resources/poe2-data/merge_community.py
   ```
   This pulls all issues labelled `approved`, merges them into the catalog, and closes the issues.
