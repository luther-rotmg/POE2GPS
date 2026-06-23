# POE2GPS Contribute Worker

A tiny Cloudflare Worker that receives community packs from the POE2GPS dashboard and files them as
GitHub issues. The GitHub token lives only as a Worker secret — never in the shipped overlay.

## Deploy

1. Install Wrangler: `npm i -g wrangler` and `wrangler login`.
2. Create a **fine-grained** GitHub PAT scoped to **Issues: Read and write** on `luther-rotmg/POE2GPS` only.
3. From this directory:
   ```
   wrangler deploy
   wrangler secret put GITHUB_TOKEN   # paste the PAT
   ```
4. Copy the deployed `https://poe2gps-contribute.<you>.workers.dev` URL.
5. In the POE2GPS dashboard → Settings → **Contribute URL**, paste that URL.

Now the Entity Atlas **Contribute** button uploads the pack in one click. The payload is the
non-identifying `{names, objectives}` pack only; the Worker validates shape, caps size, and rejects
identifying fields.
