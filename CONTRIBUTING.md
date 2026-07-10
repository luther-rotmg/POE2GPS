# Contributing to POE2GPS

[Discord](https://discord.gg/32qdzWRja3) · [Discussions](https://github.com/luther-rotmg/POE2GPS/discussions) · [Roadmap](README.md#-roadmap) · [Community pipeline](docs/community-pipeline.md)

Two ways to contribute — pick whichever fits.

## 1. Data contributions (no code required)

The overlay collects four data streams that feed the built-in catalogs.
Every stream shares the same one-click Contribute rails; the maintainer folds
accumulated submissions into the built-in catalogs each release via
`resources/poe2-data/merge_community.py`.

| What you tag in-app | Where to Contribute | What it feeds |
|---|---|---|
| Entity names in **Entity Atlas** | **Contribute** button on the Entity Atlas tab | `metadata/monsters/**` name table |
| Observed buffs in **Buffs** | **Contribute** button on the **Buff icons** card in ⚙️ Settings (enable the card first to reveal the button) | `Core/Game/BuffCatalog.cs` seed |
| Preload freq table in **Preload** | **Contribute** button on the **Preload Alert** card in ⚙️ Settings (enable the card first to reveal the button) | `Core/Game/PreloadCatalog.cs` seed |
| Campaign traces (zone traversals, boss encounters, level-ups, checkpoints) | **Auto-piggybacks on every other Contribute click** — no separate button needed. Manual send via **Contribute trace** in the Director card. Toggle in ⚙️ Settings → **Enable Campaign Probe** to opt out. | `Core/Campaign/Campaign*Model.cs` — feeds the Campaign Director |

Each Contribute click POSTs anonymized metadata to the community pipeline
Worker (`cloudflare-worker/`). No account data, character name, or map
coordinates leave your machine. See
[`docs/labeling-and-contributing.md`](docs/labeling-and-contributing.md)
for the end-user walkthrough of how to tag entities in the app before
contributing, and [`docs/community-pipeline.md`](docs/community-pipeline.md)
for the full pipeline architecture.

Every contributor's GitHub handle lands in that release's `CHANGELOG.md`
credit block.

## 2. Reporting bugs

Use the [issue templates](.github/ISSUE_TEMPLATE/) on the New Issue page.

- **[Bug report](.github/ISSUE_TEMPLATE/bug-report.yml)** — something behaves wrong.
- **[Patch drift](.github/ISSUE_TEMPLATE/patch-drift.yml)** — a game patch shifted memory
  offsets and something no longer reads right. POE2GPS auto-heals vitals
  offsets on startup and logs `auto-relocated 0x{old}→0x{new}` when it does
  — if you see that line, no report needed; the app already fixed itself.
- **[Feature request](.github/ISSUE_TEMPLATE/feature-request.yml)** — check the roadmap
  first; the template asks you to.
- **[Entity-name submission](.github/ISSUE_TEMPLATE/entity-name-submission.yml)** — the
  in-app Contribute button is faster, but this template exists for one-offs.

Support questions belong in
[Discussions](https://github.com/luther-rotmg/POE2GPS/discussions) or the
[Discord](https://discord.gg/32qdzWRja3).

## 3. Contributing code

1. Fork + branch off `main`.
2. `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release`
3. `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj`
4. Open a PR against `main`. CI runs the full suite + the internal-tooling
   grep gate + attribution-sentinel gate.

Small drive-by fixes welcome without a prior issue. For anything that
reshapes an existing subsystem, open a Discussion first so we can align
on approach.

## Code of conduct

This project follows the standard GitHub community guidelines
([contributor covenant summary](https://docs.github.com/en/site-policy/github-terms/github-community-guidelines)).
Be kind, assume good faith, no harassment. Maintainers reserve the right
to moderate.
