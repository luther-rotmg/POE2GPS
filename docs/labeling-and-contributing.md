# Contributing Entity Names to POE2GPS

The radar sees every entity the game engine spawns. What it cannot do on its own is know what those entities *mean* — whether that raw path `Metadata/Monsters/LeagueBreach/BreachMonster` is something the Director should route you toward, or just ambient filler. That knowledge comes from players. Every name you assign gets reviewed and folded into the shared tables that ship with each release, so the radar gets progressively smarter for everyone as the game gets mapped.

---

## How it works in 30 seconds

1. **Scan** — the overlay quietly logs every distinct entity it sees while you play
2. **Label** — open the dashboard, find the entities with no friendly name yet, give them one
3. **Contribute** — one click, one confirmation, done
4. **Ship** — your names pass an auto-filter, land as a GitHub issue, get reviewed by a maintainer, and are folded into the built-in tables in the next release

No account required. Nothing identifying leaves your machine.

---

## Step-by-step walkthrough

### 1. Open the dashboard

While POE2GPS is running and PoE2 is focused, press **F12** — the overlay opens the dashboard in your default browser. (F12 is foreground-gated to PoE2 and debounced; it only launches your browser and sends nothing to the game.) You can also just open a browser yourself and go to:

```
http://localhost:7777/
```

The header will show a green "live" pulse when the overlay is connected. You do not need to log in — the dashboard only binds to your local machine.

### 2. Go to the Entity Atlas tab

The tab bar reads: **Rules | Landmarks | Atlas | Settings | Director | Entity Atlas | Gear ★ | Discord**

Click **Entity Atlas**. The page fetches everything the overlay has seen during your current and past sessions.

### 3. Name the unnamed ones

Look for the **"Needs a name"** card. The subtitle reads *"entities with no friendly name yet (shows the raw path)"*. This list is sorted by how many times each entity has appeared — the most frequently seen unknowns are at the top.

Each row shows:
- The raw metadata path (this is a game-internal file path, not your data)
- The category the overlay guessed
- The zone where it was first seen
- How many times it has appeared

To name one:
1. Type a short, clear friendly name in the **"friendly name"** text field (e.g. `Breach Tentacle`, `Expedition Logbook Chest`, `Alva Mission Entrance`)
2. Click **Save**

The name takes effect on the radar immediately — no restart required. The dot changes to your friendly name on the very next render tick.

### 4. Classify notable entities

Below the naming card is the **"Notable, uncatalogued"** card: *"named/notable entities no objective covers yet"*. These are entities the overlay flagged as potentially significant (bosses, mechanics, POIs) that have not yet been wired into the Director's routing catalog.

Each row has:
- A **"label..."** dropdown — start typing or click to pick from the curated vocabulary (see below), or type any custom label you prefer
- A **priority** number (default 50, range 0–1000 — higher means the Director routes to it sooner)
- A **Classify** button

Click **Classify** to register it as a Director objective. The entry disappears from the "Notable, uncatalogued" list once it's covered.

#### The label vocabulary

The dropdown is pre-populated with these groups (you can also type anything not on the list):

| Group | Labels |
|---|---|
| Progression | MainProgression, Transition, Checkpoint, Waypoint, SideZone, Optional |
| Rewards & Upgrades | Reward, PermanentUpgrade, GemSource, Vendor, Merchant, Currency |
| Bosses | Boss, SideBoss, Pinnacle, Citadel |
| League & Seasonal | League, Seasonal, Event |
| Mechanics | Breach, Expedition, Ritual, Delirium, Strongbox, Essence, Abyss, Shrine, Trial, Sanctum |
| Atlas | Tower, Temple, Vault, Unique |
| Entities | NPC, Chest, Door, Other |

The list is advisory — the field accepts any text you type. Novel labels you contribute get reviewed and, if approved, folded into the built-in vocabulary under a "Community" group so future players see them in the picker.

### 5. Contribute

Once you have named and/or classified some entities, click the **"Contribute names →"** button at the top of the Entity Atlas tab.

**First click only:** a browser confirmation dialog appears with the text:

> *"Share your discovered entity names + objectives publicly? This contains no character data."*

Click OK to proceed. On subsequent clicks in the same browser session the confirmation is skipped.

If the contribution succeeds, a toast reads **"✓ contributed — thank you!"** and disappears after a moment. That's it.

> **No Contribute URL configured?** If the button opens a GitHub issue form in a new tab instead of showing the confirmation, the Contribute URL has not been set in your Settings tab. Go to **Settings**, find the **"Contribute URL"** field, and enter `https://poe2gps-contribute.luther-rotmg.workers.dev`. Save it, then try again. (The fallback GitHub form path still works if you prefer to attach a file manually — use **Export pack** to download `atlas-pack.json` and attach it to the issue.)

---

## What actually gets sent

The contribution pack contains exactly two things:

- **names** — a mapping of game-internal metadata paths (e.g. `Metadata/Monsters/LeagueBreach/BreachMonster`) to the friendly names you typed
- **objectives** — the Director catalog entries you created via Classify, including label, category, priority, and the metadata matching terms

That's all. The following are **not** in the pack and are never sent:

- Your character name or account name
- Your character's position, level, or stats
- Zone names, area hashes, or session data
- HP/mana/ES values
- IP address or any network identifier
- Timestamps

Your browser does not make any outbound request. The "Contribute names →" button posts to the local endpoint `http://localhost:7777/api/contribute`. The overlay's own process then forwards the pack directly to the Cloudflare Worker — your browser is not involved in that request and never sees the Worker URL.

The Cloudflare Worker adds a further defense-in-depth check: it rejects any payload that contains fields named `charname`, `character`, `account`, `accountname`, `address`, or `ip`. This is belt-and-suspenders — those fields are not present in the assembled pack in the first place.

Contribution is strictly opt-in. Nothing leaves your machine until you click the button and confirm the dialog.

---

## What happens after you contribute

### Auto-filter (instant, Cloudflare Worker)

Before anything is logged, the Worker runs every entry through automated checks:

- **Size cap:** rejects payloads over 256 KB
- **Shape and field validation:** only whitelisted fields on objectives pass through
- **Identifying-field check:** rejects the entire payload if any flagged key is present
- **Name quality:** names must be 2–60 characters and contain at least one letter; names that appear gibberish (very long strings with no vowels, or near-random character distributions) are dropped
- **Profanity filter:** entries matching a word-boundary blocklist are dropped
- **Deduplication:** if the same metadata path appears more than once, only the first occurrence passes

If the filtered pack still has at least one valid entry, the Worker opens a GitHub issue in the POE2GPS repository. The issue title is `"Community pack: N names, M objectives"` and it carries the labels `community-pack` and `needs-review`. The issue body shows the categories used, a sample of up to 10 of your submitted names, and the full filtered pack in a collapsible block for maintainer review. The GitHub token that authorizes issue creation lives only as a server-side Worker secret — it is never in the app and is never transmitted to your machine.

### Human review gate

A maintainer reads the issue. If the names look correct and useful, they add the `approved` label. If something looks wrong — misspellings, test submissions, content that slipped past the auto-filter — they close the issue without approving. No automated system merges anything without a human sign-off.

### Merge into builds

Once approved, the maintainer runs:

```
python resources/poe2-data/merge_community.py
```

This script folds approved contributions into two files that ship with every build:

- `src/POE2Radar.Core/Game/entity_names.json` — the built-in friendly name table
- `src/POE2Radar.Overlay/Web/labels.json` — the built-in label vocabulary

Curated entries always win: if a name for a given metadata path already exists in the built-in table, the community submission for that path is skipped. Your name for a newly discovered entity adds it for the first time. Novel label categories from approved objectives are added to the vocabulary under a "Community" group.

Your contributions become part of the default install for every player who downloads the next release.

---

## Why it matters — what gets better as coverage grows

Every metadata path in `entity_names.json` is one more entity the radar can display with a readable name instead of a raw file path. Every classified objective is one more thing the Director knows to route you toward. Coverage compounds.

Specifically, as the community mapping builds up:

- **The radar becomes readable.** Dots go from cryptic internal paths to names like "Expedition Remnant" or "Ritual Altar" — useful at a glance during a map.
- **The Director routes more content.** The routing system only schedules objectives it knows about. More classified entities means denser routing coverage, fewer missed mechanics, and better zone plans.
- **The Atlas gets richer.** The label vocabulary grows to reflect mechanics and content that were not in the game at initial release. Seasonal leagues, new mechanic variants, and endgame content that drifts between patches all need community eyes to keep current.
- **Future features have a foundation to build on.** Planned improvements to the Director — deeper detection, richer objective tiers, quest-aware cross-zone guidance — depend on having a well-mapped entity catalog. The more thoroughly the game is mapped now, the more capable those features can be when they ship.

One name you add today ships to every player in the next release and stays in the tables indefinitely. If 20 players each name 10 entities after a major patch drops, the radar has 200 more friendly labels within days. That is the compounding effect.

---

## Tips for good labels

- **Be descriptive, not cryptic.** `Breach Splinter Chest` is more useful than `Chest1`. Someone reading the Director's route plan needs to know what to look for.
- **Match existing patterns.** Look at how similar entities are already named on the radar and in the **"Needs a name"** / **"Notable, uncatalogued"** lists. Consistent naming makes the tables easier to maintain.
- **Use the vocabulary first.** Pick from the curated label list before inventing a new one. The more consistently entries are categorized, the better the Director's objective grouping works. Invent a new label only when nothing in the list fits.
- **Set priority by value.** Boss = 80–100. Waypoints and checkpoints = 60–70. Incidental chests and ambient NPCs = 20–30. The defaults are fine if you are unsure.
- **Name before you classify.** The "Notable, uncatalogued" card lists named *or* notable entities that no objective covers yet — some may still show only a shortened raw path rather than a friendly name. Work through "Needs a name" first so you have good names to classify.
- **You do not need to do everything at once.** Names and classifications are saved locally as you go. Contribute whenever you have a batch that feels complete.

---

## Questions?

Join the Discord: **https://discord.gg/32qdzWRja3**

Report issues, share name suggestions for tricky entities, or ask what still needs coverage after a patch — the fastest way to coordinate with other contributors and the maintainer on anything that slipped through.
