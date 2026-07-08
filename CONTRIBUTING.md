# Contributing to POE2GPS

Two ways to contribute — pick whichever fits.

## 1. Data contributions (no code required)

The overlay collects the three data streams the built-in catalogs cover:

| What you tag in-app | Where to Contribute | What it feeds |
|---|---|---|
| Entity names in **Entity Atlas** | **Contribute** button on the Atlas tab | `metadata/monsters/**` name table |
| Observed buffs in **Buffs** | **Contribute** button on the Buffs tab | `Core/Game/BuffCatalog.cs` seed |
| Preload freq table in **Preload** | **Contribute** button on the Preload tab | `Core/Game/PreloadCatalog.cs` seed |

Each button POSTs anonymized metadata to the community pipeline Worker
(`cloudflare-worker/`). A maintainer folds accumulated submissions into
the built-in catalogs each release via
`resources/poe2-data/merge_community.py`, and every contributor's GitHub
handle lands in that release's `CHANGELOG.md` credit block.

Full pipeline architecture: [`docs/community-pipeline.md`](docs/community-pipeline.md).

## 2. Code contributions

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

## Reporting bugs / requesting features

Use the [issue templates](.github/ISSUE_TEMPLATE/) on the New Issue page.
Support questions belong in
[Discussions](https://github.com/luther-rotmg/POE2GPS/discussions).
