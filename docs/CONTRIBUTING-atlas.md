# Contributing to the Entity Atlas (community mapping)

POE2GPS names Path of Exile 2 entities from a built-in table
(`src/POE2Radar.Core/Game/entity_names.json`). It can't possibly know every entity in the game — so the
**Entity Atlas** turns naming into a community effort. The goal: **eventually map the entire game.**

## For players — submit the names you tag

1. Run the overlay, play, and open the dashboard → **Entity Atlas** tab (F12 / `localhost:7777`).
2. Under **Needs a name**, type a friendly name for entities the radar shows as a raw path. (It updates
   your own radar instantly — a local override in `config/entity_names_user.json`.)
3. When you've named a batch, click **Export pack** → you get an `atlas-pack.json`.
4. Submit it via the **Contribute names** button in the tab (or open an
   [Entity name submission](https://github.com/luther-rotmg/POE2GPS/issues/new?template=entity-name-submission.yml)
   issue) and attach the file.

That's it. Only the `names` are used — no character name, position, or account data is in the pack.

## For maintainers — fold submissions into the next release

Submissions arrive as issues labelled `atlas-submission`. Each release:

1. Download the attached `atlas-pack.json` files into a scratch folder.
2. Dry-run the merge to review counts (existing curated names are kept by default):

   ```bash
   python resources/poe2-data/merge_atlas_packs.py \
       src/POE2Radar.Core/Game/entity_names.json  packs/*.json  --dry-run
   ```

3. Merge for real (drop `--dry-run`; add `--overwrite` only to let submissions replace existing names):

   ```bash
   python resources/poe2-data/merge_atlas_packs.py \
       src/POE2Radar.Core/Game/entity_names.json  packs/*.json
   ```

   The tool lower-cases keys and strips `@<level>` suffixes to match `EntityNameResolver`, and writes the
   table back **sorted + compact** so diffs stay clean.

4. Spot-check the diff (`git diff` the JSON), build + run the gate, and ship it in the next release. The
   new names are now embedded for everyone.

## Notes

- **Curated names win by default.** The merge keeps an existing name unless you pass `--overwrite`, so a
  good built-in label isn't clobbered by a rough submission.
- **`objectives` in a pack are ignored by the merge tool** — they're personal Director catalog entries.
  Only the `names` map is community data.
- The pipeline is data-only and read-only with respect to the game; it never modifies PoE2.
