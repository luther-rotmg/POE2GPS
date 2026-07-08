# POE2GPS Contribute Worker

The project's community-pack collector. The **maintainer** deploys this once; end-users never
touch it. It receives non-identifying `{names, objectives}` / `{buffs}` / `{preloads}` packs from the
POE2GPS dashboard, auto-filters junk (profanity / identifying fields / bare asset paths / rate abuse),
and files clean submissions as reviewable GitHub Issues labelled `community-pack` + `needs-review`
(+ optional sub-label) with a parsed summary. The GitHub token lives only as a Worker secret —
never in the shipped overlay.

## Deploy (maintainer only)

1. Install Wrangler: `npm i -g wrangler` and `wrangler login`.
2. Create a **fine-grained** GitHub PAT scoped to **Issues: Read and write** on `luther-rotmg/POE2GPS` only.
3. Mint the KV namespace that backs the rate-limiter and paste its id into `wrangler.toml`
   (replacing the placeholder sentinel that ships in the repo — wrangler refuses to deploy
   until a real id is in place, which is the intended gate):
   ```
   wrangler kv:namespace create RATE_KV
   ```
4. From this directory:
   ```
   wrangler deploy
   wrangler secret put GITHUB_TOKEN   # paste the PAT
   ```
5. Copy the deployed `https://poe2gps-contribute.<you>.workers.dev` URL.
6. In the POE2GPS dashboard -> Settings -> **Contribute URL**, paste that URL.
7. Smoke-verify before opening the CF-DASH-BUTTONS PR:
   ```
   bash ../resources/poe2-data/smoke-worker.sh https://poe2gps-contribute.<you>.workers.dev
   ```

Now the Entity Atlas / Buff / Preload **Contribute** buttons upload their packs in one click.

## Routes

| Path | Body shape | Sub-label | Notes |
|---|---|---|---|
| `POST /submit-atlas`   | `{names:object, objectives:array}`               | (none)     | v0.20 backward-compat payload shape |
| `POST /submit-buffs`   | `{buffs:[{path, tier?, metadata?}]}`             | `buffs`    | targets `BuffCatalog.cs` seed |
| `POST /submit-preload` | `{preloads:[{path, freq?}]}`                     | `preload`  | targets `PreloadCatalog.cs` seed; bare `.dds`/`.ao` rejected |

All three share middleware: CORS, 256 KB cap, identifying-field reject, NFKD+leet profanity fold,
KV rate limit 5/60s per `CF-Connecting-IP`, and GitHub Issue dispatch under labels
`community-pack` + `needs-review` (+ sub-label above). Any other path returns `404 unknown route`
so stale desktop clients see an unambiguous "route gone" rather than a schema-mismatch 400.

## Auto-filter

Before filing any issue, the Worker rejects:

- **Profanity / slurs** — NFKD-normalized, combining marks stripped, leet-substituted
  (`n1gg3r`, `f@gg0t`, `$pic` all match), matched against a conservative 7-word list. The v2
  length-based gibberish gates (8/12-char) were dropped — they misfired on legitimate PoE
  metadata paths.
- **Identifying fields** — any key named `charname`, `character`, `account`, `accountname`,
  `address`, `ip`.
- **Oversized payloads** — hard cap at 256 KB, returned as `413`.
- **Bare asset filenames on `/submit-preload`** — e.g. `foo.dds`, `bar.ao` without a
  `Metadata/...` prefix are rejected (they carry no research signal).
- **Rate abuse** — more than 5 POSTs from the same IP inside a rolling 60-second window
  return `429` with `Retry-After: 60`.

Submissions with nothing valid after filtering return `400 nothing valid after filtering`.

## Moderation workflow

1. Submissions arrive as GitHub Issues tagged `community-pack` + `needs-review`
   (+ optional `buffs` / `preload`).
2. Each issue shows a parsed summary and the full pack JSON in a collapsible `<details>` block.
3. Review the issue. Add the `approved` label to accept, or close the issue to reject.
4. At each release, fold approved submissions into the catalog:
   ```
   python resources/poe2-data/merge_community.py
   ```
   This pulls issues labelled `approved`, merges them into the catalog, and prints a
   ready-to-paste `### Community contributors` credit block for the CHANGELOG. Thanking
   contributors and closing their issues stays a **manual maintainer step** — personal
   follow-up comment on each merged issue (PMS-11 habit block). No `--comment`, no
   auto-close.

## Local unit tests

```
npm test          # equivalent to: node --test worker.test.mjs
```

Covers: NFKD+leet profanity fold, path routing, preload bare-asset reject, and the
KV-counter rate limit. No network, no KV — the KV shim is in-memory.
