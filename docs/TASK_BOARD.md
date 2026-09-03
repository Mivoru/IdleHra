# FolkIdle Task Board

Seven tasks, restated against what the code actually does as of 2026-09-01.
Every "today" claim below was checked in the source or the live database rather
than remembered — where a task turned out to be different from its one-line
description, the difference is called out, because two of these are much smaller
than they sound and two are much larger.

Each task carries **Done when** criteria. A task with no way to tell it is
finished is a mood, not a task.

Ordering is a suggestion: 7 → 1 → 2 → 6 → 5 → 4 → 3, cheapest and most visible
first, riskiest last.

**Tasks 1-7 are all done (see the status table below). The OPEN items are 8, 9
and 10, added 2026-09-03/04 from player reports, and they are immediately after
this paragraph:**

| # | Open task | Shape |
|---|---|---|
| 8 | Combat is not readable as a fight | Diagnose first — the feedback layer is already wired |
| 9 | Rarity barely does anything | **Confirmed by arithmetic**, not a feel report. Balance risk. |
| 10 | World boss rework | Server half is sound; the interaction is the gap |

---

## OPEN — 8. Combat is not readable as a fight

**Reported 2026-09-03, by the player, unprompted:**

> "I can't properly see the fight against the monster. I see his picture, name
> and health bar, but I don't see the health bar moving or effects of my hits.
> I just see my health bar moving."

Not investigated yet — noted the same day and deliberately left. What follows
is the symptom and a map of what already exists, so whoever picks this up does
not rebuild machinery that is already there.

### The important half of the report

**"I just see MY health bar moving."** Both bars are built the same way, from
the same interpolated snapshot, in the same component — so one moving and the
other not is a real asymmetry and is the thread to pull first. It is much more
specific than "combat feels flat" and should not be folded into the polish half
below.

### What already exists — do not rewrite these

| Piece | Where |
|---|---|
| Monster HP smoothing | `net/interpolation.ts` — `CurrentMonsterHp` **is** in `INTERPOLATED_FIELD_NAMES` |
| The monster bar | `routes/Combat.svelte` ~line 303, `value={visual?.CurrentMonsterHp ?? snap.CurrentMonsterHp}` |
| Floating damage numbers | `ui/FloatingDamage.svelte`, fed by `stores/damage.ts` |
| Hit sparks, per weapon family, crit-aware | `ui/HitSpark.svelte`, fed by `hitSparks` in `stores/game.ts` |
| Crit + weapon on the wire | `StateUpdatePacket.LastHitWasCrit`, `EquippedWeaponKind` |
| Death / victory cards | `ui/DeathCard.svelte`, `ui/VictoryCard.svelte` |

So the feedback layer is **wired**, which makes this a "why is the wired thing
not visible" question rather than a "build it" question. That distinction is
the whole reason this task is worth reading before starting.

### Candidate causes, in the order worth checking

1. **The denominator, not the numerator.** The bar's max is
   `shownMaxHp(activeMonster)`. A boss carries 5x its authored HP until first
   clear (`BossFirstClearRules`). If the max is wrong the bar can look frozen
   near-full while the number under it changes.
2. **The fight may genuinely be one packet long.** `interpolation.ts` records
   a *measured* 1637 ms mean between monster-HP changes. A geared character on
   an early monster can take it from full to zero between two snapshots — there
   is nothing to animate, and that is a *pacing* finding, not a rendering one.
3. **Main-thread starvation.** The recorded trap "keying an effect on the
   damage array starves the main thread" is in this exact area. Until
   2026-09-03 the Combat screen also sat behind 3.2 MB inventory refetches;
   that is fixed, so **re-check the symptom on the current build before
   assuming it is still present**.
4. **`observedMaxPlayerHp` has no monster equivalent.** Max HP is not on the
   wire for either; the player bar derives a session high-water mark. Check
   whether the monster bar has an honest denominator at all.

### The half the player actually asked for

Death animations for monsters, and more motion during a fight. Scope it before
starting — "more entertaining" is unbounded. A concrete starting list:

- A death animation, since the monster currently just vanishes and is replaced.
- A hit reaction on the monster sprite (shake, flash) — the sparks exist but
  the sprite itself never acknowledges a hit.
- Attack telegraph or wind-up, so a 1.6 s swing reads as an action rather than
  a number changing.

### Done when

- The **asymmetry is explained** — a written cause for why the player's bar
  moved and the monster's did not, not just a change that makes it move.
- Monster HP is observably animating in a real fight, verified the way
  `interpolation.ts` was: a MutationObserver on the bar against the live
  cadence, not by eye.
- A monster death is visually distinct from a monster being swapped out.
- `npm run exercise` still green; any new effect is checked at 390 px with
  `check:clipping` and `check:overlap`.

### Risk

Low to change, **medium to scope**. The trap is spending the effort on new
animation and never answering (1) — the player would still be looking at a
static monster bar, now with more going on around it.

---

## OPEN — 9. Rarity barely does anything, and the numbers agree

**Reported 2026-09-04, by the player:**

> "There isn't much difference between them, and the biggest difference is in
> tiers and not the 14 rarities. I think we should boost that."

**Checked in the source, and the instinct is right — more right than reported.**
This is not a feel problem, it is arithmetic.

### What an item's quality tier actually controls

**One thing: how many affixes it rolls.** And `CombatLootEngine.GetAffixCount`
buckets the fourteen tiers into **five** values:

| Quality tiers | Affixes |
|---|---|
| 1-3 — Normal, Common, Uncommon | 1 |
| 4-6 — Rare, Ultra Rare, Epic | 2 |
| 7-9 — Legendary, Mythic, Relic | 3 |
| 10-12 — Ancient, Divine, Demonic | 4 |
| 13-14 — Godly, Transcendent | 5 |

So **9 of the 14 tiers are mechanically identical to a neighbour.** A Normal
and an Uncommon are the same item with different coloured text. So are Godly
and Transcendent — the top of the ladder, where it should matter most.

### What it does NOT control — the part worth reading

- **Base power.** `FlatAttackPower` / `FlatDefenseRating` come from
  `items.json` per BASE ITEM and are read straight into
  `EquipmentSlotEngine.ComputeEquippedTotalsAsync`. Quality tier contributes
  **zero**. Weapon base attack by region: **12 / 36 / 108 / 324 / 972** — an
  **81x** spread that quality tier plays no part in. That is precisely the
  "the biggest difference is in tiers" the player described.
- **Affix magnitude.** `AffixRegistry.CalculateMagnitude` takes `regionTier`
  and the per-affix `AffixRarity`. Not the item's tier.
- **Affix rarity odds.** `RollAffixRarity()` takes **no arguments** and rolls a
  fixed weight table. A Transcendent's affixes roll from the same table as a
  Normal's.

**`AffixRegistry.RollAffixes` accepts an `itemRarityTier` parameter and never
references it in the body.** It is a dead parameter — checked, it appears only
in the signature. That is a strong hint the influence was intended and never
wired, which is this codebase's most-repeated defect shape.

### Do not "just multiply the numbers"

`AffixRegistry.CalculateMagnitude` carries a long comment about a previous pass
here: affixes grew *linearly* against gear that grew *geometrically*, so rarity
stopped mattering exactly at depth. Whatever is done must keep affix growth on
the same curve as the gear it sits on, or it re-creates that bug in the other
direction. Read that comment first.

### Candidate directions, cheapest first

1. **Give each of the 14 tiers its own affix count**, or a fractional
   equivalent (e.g. guaranteed count plus a probabilistic extra), so no two
   tiers are identical.
2. **Wire the dead `itemRarityTier`** into `RollAffixRarity` so a higher-tier
   item biases toward better affix *rarities* — magnitude, not just count.
   This is likely the largest felt change per line altered.
3. **Let quality tier scale base power**, e.g. a multiplier on
   `FlatAttackPower`. Biggest impact, biggest balance risk: it interacts with
   the monster ladder and the XP curve, both of which are pinned by tests.

### Done when

- No two adjacent quality tiers are mechanically identical.
- A table is printed showing expected item power at each of the 14 tiers, at
  region 1 and region 5, before and after — the same way `ProgressionRateTests`
  and `GatheringShareTests` print theirs.
- `MonsterLadderTests`, `ProgressionRateTests` and `GatheringShareTests` still
  pass, or their movement is explained deliberately.

### Risk

**Medium-high.** This is the balance curve, and CLAUDE.md says not to touch it
casually. Option 1 is contained; option 3 is a progression change and needs the
measured tables above before it ships.

---

## OPEN — 10. World boss rework: make the fight a fight

**Requested 2026-09-04:** rework the world boss, possibly with minigames, to
make it more engaging.

### What it is today

A button. `WorldBoss.svelte` renders the boss, a shared HP bar and three
attempt pips; `WorldBossEngine.MaxAttemptsPerEncounter` is **3**. Each attempt
posts one `clientPredictedDamage` figure and the server applies it. There is no
fight — there are three presses of **Attack**, and the only skill expressed is
having stocked the larder beforehand.

Everything around it is real and working: a server-authoritative HP pool shared
across players, per-player damage attribution (`_playerDamageMap`), an event
window, and a defeat path. **The content is fine; the interaction is the gap.**

### Constraints any design must respect

- **The client cannot be trusted with damage.** `clientPredictedDamage` is
  already capped at `MaxClientPredictedDamage` (100,000,000) because it comes
  from the client. Any minigame that turns player *input* into *damage* is an
  exploit surface — the score must be validated or bounded server-side, or the
  minigame decides its own reward.
- **This is an idle game.** The anti-cheat has already banned players for
  clicking too regularly, and the four active skills were removed after being
  measured at "+90% damage for clicking every three seconds" — see
  `SkillTreeRegistry`. A minigame that rewards *reflexes* fights the genre and
  the existing balance philosophy. Prefer decisions over dexterity: a
  weak-point choice, a timing/ordering puzzle, a resource commitment.
- **Three attempts per encounter is a small budget** for anything with a
  learning curve. Either the minigame is short enough to fit three tries, or
  the attempt budget is part of the redesign.

### Done when

- A player can describe what they *did* in the fight, not just that they
  pressed Attack three times.
- No client-supplied score converts to damage without a server-side bound.
- Damage attribution and the shared HP pool still work with more than one
  player attacking (that is what `_playerDamageMap` is for).
- `exercise.mjs` drives the new interaction, not just the Attack button.

### Risk

**Medium.** The server half is sound and should mostly survive. The risk is
scope and genre fit — write the specific design down before building, and check
it against the "this is an idle game" constraint above.

---

## Status at the end of the 2026-09-02 pass

Everything below was worked in one session. Read this table before the task
bodies — several of them still describe the world as it was on 2026-09-01.

| # | Task | State |
|---|---|---|
| 7 | Missing icons | **Done.** Tools reach `ITEM_ICONS` by base id; `sprites.missing.txt` + a budget ratchet; all ten "missing" ores/logs turned out to have real art. |
| 1 | Verify the daily login | **Done.** No defect. Date key is UTC `floor(unix/86400)`; 7 new tests, mutation-checked. |
| 2 | Audio + panel clipping | **Done.** Audio: LFS was shipping 130-byte stubs; production now serves 35 KB of real WAV. Clipping: automated as `npm run check:clipping`, 0 findings across 25 screens × 3 widths. |
| 6 | Wiki | **Done.** Opened in a browser: 15 pages, no console errors. Its core loop taught the old, wrong order and said the *fourth* monster kills an unfed character - fixed. |
| 5 | Tutorial | **Done.** Discovery moments, seen-state, re-openable list, 64 tests, and `exercise.mjs` now drives a real new account. Doing that found the entrance defect below. |
| 4 | Breeding | **Done.** `docs/breeding_model.md`, explaining preview, interlocks, terminology canon. Found and fixed a real server defect. |
| 3 | Delete the chrono bank | **Done, not deployed.** See §8b of `CURRENT_IMPLEMENTATION_STATE.md`. |

**Verification at hand-off:** 537/537 server tests, 294/294 client tests,
**99/99 `npm run exercise`**, 25/25 `smoke:screens` (local and production),
0 clipping findings, 0 overlaps, `svelte-check` at 4 errors — the four pre-existing
`GuildOps.svelte` ones — and 16 warnings. Server builds clean. Nothing
committed, nothing deployed.

---

## The 2026-09-02 evening pass: running the exercise

The item at the top of the list below was "run `npm run exercise`". It had
never been run against this session's work. Running it found four things, and
only the last is a game defect — but it is the worst kind.

### The game's entrance was closed

**A brand-new player who followed onboarding step 1 died and the tutorial never
moved again.** Measured against the live server:

| start | outcome |
|---|---|
| naked, empty larder | dead at **29 s**, Field Mouse still on **264 of its 465 HP** |
| after 60 s of fishing | dead at **65 s**, mouse down to **73** — closer, still a loss |

The character has 100 HP; the mouse deals 8 every 2 s and has more than twice
the health the player can chew through. Tier one blocks in order, so with step 1
unreachable the food advice — step 3 — was never shown to the only people who
needed it.

**The balance was not at fault and was not touched.**
`ProgressionRateTests.TheFirstMonsterTakesAboutSeventyFiveSeconds` passes, and
passes because it hands its simulated character a million bites of food; its own
comment says that without them "the character dies in about thirty seconds",
which is within four seconds of what a real account does. The model was always
right *given food*. **Fixed by reordering tier one to larder → fight → gear**,
which is the true dependency; see `docs/onboarding_steps.md` §2. Guarded by
`tutorial.test.ts` and by the exercise driving a real new account through
fishing and stocking.

### The exercise had been quietly rotting

Three checks failed on a working game, because the script consumed the state its
own later steps needed and so could only ever pass on a fresh fixture:

- **Ancestors "Keep"** — marking is a flag nothing clears, so each run marked one
  more until all 23 read "Kept" and the check failed permanently. Now a round
  trip that asserts both directions and puts the flag back.
- **Doll "Wear"** — the picker lists the piece already worn and sorts it first,
  so the script re-equipped what was on and read no change. Now picks a
  different item.
- **Village / breeding** — "Send on" ate the last unmarried villager that the
  marriage step needed. It now holds the last one back, and an exhausted pool
  is reported honestly: the assertion is that a greyed-out option *states a
  reason*, not that the reason is one I enumerated (the first attempt failed on
  "(both women)").

`DevFixtureSeeder`'s standing villager pool went 2 → 6 per sex: the top-up only
runs on an explicit `--seed-dev`, so two-per-sex lasted about two runs.

### One thing the fixture was hiding

`/api/v1/admin/status` 403s for an ordinary account — correct, but the dev
fixture is an admin so the console error never appeared until a new account
drove the client. Tolerated in the exercise alongside the deliberate 404.

### Task 2b, finished by automating it

Eyeballing 26 screens does not scale and does not run again next month, so the
sweep became a script: **`npm run check:clipping`** walks all 25 screens at
1500 / 900 / 390px and reports content wider than its box in a container that
cannot scroll. It reads **0 findings**.

Getting there needed three refinements, each of which is the difference between
a signal and 700 lines of noise:

- a box with `overflow-x: auto` is a **deliberate scroller**, not a clip — CLAUDE.md
  actually asks for those on wide content;
- `text-overflow: ellipsis` **says** it truncated, with a visible "…". The Chest's
  item list does it 724 times on a phone and is right every time. What is hunted
  is the silent slice;
- SVG reports `clientWidth` in a different coordinate system, so the Skill Tree's
  labels read as overflowing by 91px in a 29px box. Arithmetic, not a defect.

It found one real bug: **Gathering's node rows** hung 9px past their panel at
900px, slicing the Gather button, because `minmax(7rem, …)` + `minmax(5rem, …)`
+ three gaps demand more than a 245px panel has. The floors are `minmax(0, …)`
now, so a `fr` track still takes its proportional share without demanding a
width the panel cannot give.

`overlap-check.mjs` also reads 0 now. Its four findings were all the fixed
ChatDock covering whatever sits in the bottom-right corner at the current
scroll offset — measured reachable by scrolling, so a floating overlay is no
longer counted. `app.css` reserves `padding-bottom` so content at the very END
of a screen, where there is nothing left to scroll, can still clear it.

### The three checkers shared one rotting list

`SCREENS` lived in three files and each copy rotted separately. It is
`scripts/screens.mjs` once now, with the sign-in, the hamburger-aware `go()` and
`assertMatchesNav`, which makes the nav the authority rather than the file.

### What is genuinely left

1. **The 40 legacy `*_crafting_material` entries** — see
   `docs/crafting_material_audit.md`. Classified, deliberately not deleted;
   deleting them is a product decision, not a cleanup.

---

## 7. Missing icons — and a list of everything without art

**DONE, 2026-09-02.** What follows is what was actually true and what changed;
the remaining art backlog is now a generated file rather than a paragraph.

### What was wrong

**The tool art existed.** `client/Assets/Images/SpritesWeb/Tools&Equipment/`
holds `axes/`, `pickaxes/`, `fishing rods/` with per-wood art. All 33 tools
rendered as two-letter initials on the paper doll, the Chest and the Forge
because `generate-sprites.mjs` routed those three directories into
`toolIcons[kind][tier]` only — a matrix reachable through `toolIcon(kind, tier)`
and nowhere else — while `ItemIcon.svelte` asks `itemIcon(baseItemId)`, which
reads `ITEM_ICONS`. Fixed in the generator: the same path is now emitted under
the item's own BaseId as well, and `toolIcon()` is untouched. The BaseId shape
is `<wood>_<axe|pickaxe|fishing_rod>_tool`, and the wood token is **`acacia`
where the art file says "Acatia"** — the one place a slug() of the filename
would silently produce a non-item.

**The generator was reading a stale catalogue.** It parsed
`client/Assets/StreamingAssets/GameData/items.json`, a retired-Unity copy that
is 111 items adrift from `server/GameData/items.json`: it still carries the
whole legacy equipment line and the five `_helper_offhand_base` pieces the
catalogue cut removed, and it lacks `copper_ore` / `iron_ore` / `obsidian_ore` /
`silver_ore` entirely. Now reads the server's. This changed no mapping — the
generated file diffed clean — but every coverage number before this was
measured against a catalogue no player sees.

**Four logs and one ore had art pointed at their dead twin.** `golden_willow_log`,
`golden_acacia_log`, `golden_frostpine_log`, `golden_ebon_log` are the live rare
woodcutting drops (loot table indices 80/82/84/86, `TierMaterials`,
`GuildContributionEngine`). The art named exactly after them —
`Golden Willow log.webp` and friends — was aliased to `whispering_willow_log` /
`ironwood_log` / `glacier_pine_log` / `void_bark_log`, an older duplicate family
that appears in **no** C# file. The alias table now takes a list, and each of
those files maps to both ids: the live one so it renders, the legacy one because
players can still be holding it. `Absidian.webp` likewise now serves the live
`obsidian_ore` as well as `obsidian_ore_crafting_material`.

**One equipment piece was drawn and never shown.** `brawler_pelt.webp` is a
wolf's head worn as a hood; the noun table filed `pelt` under *chest*, where it
collided with `brawler_harness.webp` for the Frost Brawler set's single chest
slot. One overwrote the other and `eq_brawler_pelt_helmet_armor_slot_base` — a
BaseId that names its slot outright — got nothing. `pelt` is a helmet now, and
every one of the 75 equipment pieces has art.

### What shipped

- `ITEM_ICONS` went from **121 to 165** of 330 items; missing art from **209 to
  165**. All 75 equipment pieces now have art.
- `client_web/src/lib/ui/sprites.missing.txt` — generated, sorted, grouped,
  committed. `MISSING_ART_BUDGET` in the generator asserts the count and carries
  a comment saying it may only fall.
- `node scripts/generate-sprites.mjs --check` (npm: `check:sprites`) fails on a
  stale generated file OR on the count rising, and runs in CI beside the
  protocol check.
- `client_web/scripts/draw-placeholder-ores.mjs` draws the five ores that had no
  art at all — `copper_ore`, `iron_ore`, `silver_ore`, `cobalt_ore`,
  `darksteel_ore` — as faceted nuggets into
  `client/Assets/Images/SpritesWeb/Generated/`. Deterministic; Chromium's canvas
  is the WebP encoder because the repo has none and Playwright was already here.
  Delete a placeholder when real art lands: the walk visits `Generated/` before
  `Locations/`, so a hand-drawn file of the same name wins on its own.
- The auto-matcher now prefers an **exact** BaseId filename match over a prefix
  one, which is what lets a file called `copper_ore.webp` land on `copper_ore`
  rather than being thrown out as ambiguous with `copper_ore_crafting_material`.

### The `*_crafting_material` question — answered

**All 50 of them are unreachable.** Not "several are legacy": zero of the 50 can
be obtained or spent. Traced through `_lootSegments`, which is the only thing
that makes a `_lootEntries` index reachable — the nine-ore Mining table at
indices 61-76 and the coal entry at 21 are orphaned, keyed to activity ids
201-205 that moved to 2001-2005 — and through `_recipes`, which was cut to the
30 tool recipes and consumes none of them.

Deleting them is still a product decision and was **not** taken. Two things
argue for care: live inventories hold them (one account was measured with 5,017
`copper_ore_crafting_material`), and `copper_ore_crafting_material`,
`iron_bar_crafting_material` and `silver_bar_crafting_material` currently carry
the only bar/ingot artwork in the game. They stay listed in
`sprites.missing.txt` like anything else; nobody should commission art for them.

### Still open

- 165 items have no art: 46 crafting materials (all legacy, per above), 46
  gathering and profession materials, 3 consumables and 70 uncategorised (boss
  drops, `premium_diamond`, alchemy reagents). Work it by frequency of
  appearance, not alphabetically. The full list is in `sprites.missing.txt`.
- The five generated ores are placeholders, not painted art.

---

## 1. Verify the daily login

**Verification, not construction — it is fully built.**

### What is actually true

`DailyLoginRewardEngine` has rotating weekly gold matrices
(`GoldRewardMatrices`), a day-7 bonus of 100 diamonds
(`PremiumDiamondsOnDay7Completion`), and streak state on
`PlayerRecord.LastLoginTimestamp` / `LoginStreakDays`. `Progression.svelte:142`
renders the seven days with collected / today / upcoming states and explains
that rewards arrive on sign-in rather than being claimed.

**The thing worth checking:** every account in the live database has
`LoginStreakDays = 1`, including a level-74 account played across several weeks.
That is *consistent with* legitimate resets — a missed day sets it back to 1,
and this account was quarantined for most of August — so it is **evidence, not
proof**. It is also exactly what a streak that never advances would look like.

### Scope

1. Settle the ambiguity with a test rather than by staring: drive
   `DailyLoginRewardEngine` across a simulated day boundary and assert the
   streak goes 1 → 2 → 3, resets to 1 after a skipped day, and pays diamonds
   exactly once on day 7.
2. Pin the **date key**. A streak turns on "what day is it", and a UTC-vs-local
   disagreement is the classic way one becomes unwinnable for players in some
   timezones. Whatever the rule is, assert it.
3. Check the reward is idempotent within a day — two sign-ins must not pay
   twice.
4. Only if the test shows a real defect, fix it.

### Done when

- Tests cover advance, reset-after-gap, day-7 diamonds, and same-day
  idempotence.
- The date-key rule is written down where the engine is.
- A real account observed advancing past streak 1, or a defect found and fixed.

### Risk

Low, and it pays real currency, so it is worth being certain.

---

## 2. UI polish and sound

**Split this in two — one half is a live bug, the other is taste.**

### The bug half: production has no audio at all

Eleven real clips exist locally (`client/Assets/Resources/Audio/`, 4 KB–132 KB:
`level_up.wav`, `loot_rare_dropped.wav`, `ui_button_click.wav`, …). They are
tracked in **Git LFS** — and **git-lfs is not installed on the deploy box**, so
what ships is 130-byte pointer stubs. The game is silent in production and
always has been, while sounding fine on a developer machine.

`exercise.mjs` already tolerates missing audio as expected 404s, which is why
nothing has ever complained.

Fix options, in order of preference:
1. Stop shipping audio through LFS — the runtime needs 11 small files, and the
   1 GB of LFS in this repo is source PNGs nothing serves (see the LFS item in
   `NEXT_STEPS_BACKLOG.md`).
2. Install git-lfs on the box and fetch only `client/Assets/Resources/Audio/`.

### The taste half: modernisation

Scope this deliberately rather than as "make it nicer". Candidates observed:
- Panels are visually uniform; nothing signals which is the primary action on a
  screen.
- The buff tier rows were cropped until today — same class of bug likely exists
  in other dense panels. Audit every panel at a **narrow** container width, not
  just a narrow viewport; the panel grid means those are different things.
- Sound design is a separate question from sound *delivery*: decide which
  events deserve audio before adding more clips.

### Done when

- A `level_up.wav` actually plays on the live site.
- Every panel is screenshotted at two container widths with no clipped content.
- Any visual rework is described concretely enough that "done" is checkable.

### Risk

Low for the audio delivery. The polish half is unbounded unless scoped — write
the specific list before starting.

---

## 6. Make the Wiki complete and readable

### What is actually true

`Wiki.svelte` is 435 lines with **eight sections**: Basics & Progression,
Combat & Stats, Skill Tree, Items & Tiers, Map & Regions, Gathering & Crafting,
Genetics & Breeding, Guilds & Social. Three sub-components add real data:
`WikiItemDatabase`, `WikiDropChances` (a luck calculator), `WikiMonsterDrops`
(live drop tables from `/api/v1/monsters/loot`).

The game has **26 screens**. Systems with no Wiki section at all:

- **The Village** — buildings, the Town Hall ceiling, what each upgrade does,
  the tier materials. Nothing. This is the system players most recently could
  not use.
- **Tools** — that they are equipment, that they have eleven slots, what they
  accelerate.
- **The Long Game** — Book of Deeds, Seals, Hall of Ancestors, Inheritance,
  what a season resets and what survives. Four systems, no page.
- **The Market**, **Forge/fusion and affix rerolls**, **World Boss**,
  **Mailbox**, **Chrono/Boosts**, **Daily login**, **Achievements**.

### Scope

1. Write the missing sections, weighted by what a confused player actually
   opens the Wiki for. Village and the Long Game first.
2. Make the data-driven parts do more of the work. `WikiMonsterDrops` already
   reads live drop tables; the same trick suits recipes, village upgrade costs
   and buff tiers, and data that reads itself cannot drift from the game.
3. Readability pass: the sidebar-plus-page layout is sound; the pages are dense
   prose. Tables, per-region breakdowns, and worked examples ("a tier-3 reroll
   costs X, which is about N minutes of region-3 income").
4. Search across all sections, not just the item database.

### Done when

- Every one of the 26 screens is either documented or explicitly listed as not
  needing a page.
- Village, tools and the Long Game have pages.
- At least the village upgrade costs and buff tiers are generated from server
  data rather than retyped.

### Risk

Low, but it is a lot of writing. Content generated from live data is the part
that keeps paying.

---

## 5. Tutorial, hints, and teaching the game

**Larger than it sounds. A tutorial exists but covers almost nothing.**

### What is actually true

`lib/stores/tutorial.ts` (111 lines) and `tutorialSteps.ts` (105 lines)
implement a three-step first session, driven purely off the state packet:

1. **Win a fight** — done when `CurrentLevel >= 2`
2. **Equip a drop**
3. **Stock the larder**

Then `Completed`. The design is good — each step is "a fact on the wire that
means it is done", which is why it needed no bespoke tracking. It simply stops
after three steps.

**Nothing teaches:** gathering, crafting (including the new Craft ×10), tools
and their slots, the village and the Town Hall ceiling, region unlocking via
bosses, the forge, affix rerolls, the market, guilds and buffs, breeding, the
skill tree, deeds and Seals, the Hall of Ancestors, inheritance, the world boss,
or conversations.

### Scope

1. **Extend the existing pattern rather than replacing it.** Keep "a step is a
   predicate over the state packet"; it is testable without a browser and it is
   why the current three work.
2. Add a second tier: **discovery moments**. When a player first unlocks or
   reaches a system, explain that system once. Region-2 unlock, first tool
   crafted, first guild joined, Town Hall available, first child bred.
3. Decide the **teaching surface**: modal, coach-mark on the real control, or a
   dismissible panel. Coach-marks on the real control are the most effective and
   the most work; pick knowingly.
4. Persistence: which steps a player has seen must survive a reload, and a
   season reset must not re-teach everything.
5. Make it skippable and re-openable — an idle game is often replayed by people
   who already know it.

### Done when

- Every major system has a first-encounter explanation.
- Steps are predicates over the state packet, tested in a node runner.
- `exercise.mjs` drives a new account through the whole onboarding chain.
- Seen-state survives reload and behaves sanely across a season reset.

### Risk

Medium, and it is the task most likely to sprawl. Write the full step list
before building any of it.

---

## 4. Breeding: make it understandable

### What is actually true

Breeding is *complete* — see `LONG_GAME_SPEC.md` sections 3 and 5. Two pairing
modes (hero × hero, hero × villager), aptitudes, genetic loci, epic mutations,
a village gene pool with arrivals and recruitment, cooldowns, and a two-tab
Breeding screen. `exercise.mjs` drives it end to end.

The problem is not that it does not work. It is that it is the most
mechanically dense system in the game with the least explanation, and it
interlocks with four others: the Village (the Inn produces the gene pool),
Ancestors (the Hall culls at rollover), Inheritance, and the season reset.

### Scope

1. Write down the player-facing model first, in one page, before touching code:
   what a child inherits, what a villager contributes, what survives a season,
   what is lost. If that page is hard to write, the design is what needs work —
   not the UI.
2. Make the **preview** carry the explanation. The screen already quotes what a
   child would inherit and what it costs; that is the natural teaching moment,
   and expanding it beats a separate help page nobody opens.
3. Surface the interlocks where they bite: on the Village screen say the Inn
   feeds the gene pool; on Ancestors say what the rollover will cull.
4. Name things consistently. "Aptitudes", "loci", "genes", "inheritance" and
   "legacy" are currently distinct concepts with overlapping names.
5. Only then consider mechanical simplification — and if any is proposed,
   measure it against `LONG_GAME_SPEC.md` §7, which argues some of this
   complexity is deliberate.

### Done when

- A one-page model exists and matches the code.
- The Breeding screen explains a child's outcome without leaving the screen.
- Village and Ancestors mention their breeding interlocks.
- Terminology is consistent across screens, Wiki and tooltips.

### Risk

Medium. Mostly explanatory, but touching the mechanics risks the season-long
progression the Long Game is built on. Do the writing first.

---

## 3. Delete the chrono bank

**DONE, 2026-09-02 — but read this first, because four of the claims below
turned out to be wrong.** The full record is §8b of
`CURRENT_IMPLEMENTATION_STATE.md`. In short:

- **"Deleting it is a balance change" — no.** `BankOverflowSeconds` was already
  a no-op; over-cap offline time was already discarded.
- **"One of the few things diamonds and the Store are wired to" — no.** There
  was no diamond price, no gold price and no exchange rate anywhere.
- **"87 server files" / "~700-byte ceiling" / three wire fields** — really 22
  hand-editable source files, a ceiling of 832, and **four** state fields
  (`VisualBankedChronoSeconds` was a second copy of `BankedChronoSeconds`, and
  the two were read by two different screens).
- **`PlayerChronoRegistry` and `SeasonEraEngine` did not exist.** §9 of
  `CURRENT_IMPLEMENTATION_STATE.md` had been wrong for months; corrected.

Outcome: `StateUpdatePacket` 800 → **779**, `ClientCommandPacket` 359 → **339**,
opcode 8 kept but renamed `SetSimulationSpeed` (it was never chrono), opcodes
24/47/48 retired as gaps, and nine accounts compensated 1:1 into
`AccumulatedTimeBankSeconds` by `20260902180220_DeleteChronoBank`.

The original task text follows, for the reasoning it records.

**Much larger than it sounds. Do this last, and only if the answer to "should
this exist" is genuinely no.**

### What is actually true

The chrono bank converts offline overflow into banked seconds a player spends to
accelerate the game. Its surface:

- **87 server files** mention `Chrono`.
- Table `AccountChronoRegistry` / `account_chrono_registry` — 13 live rows —
  holding `BankedChronoSeconds`, `ActiveSpeedMultiplier`,
  `AccelerationTerminationEpoch`, `LastClockSyncEpoch`.
- **Wire fields on `StateUpdatePacket`**: `BankedChronoSeconds`,
  `IsChronoAccelerating`, `ActiveChronoLockExpirationTicks`.
- A `ChronoAccelerationQueue` on `PlayerSessionRegistry`, drained by the tick.
- Client: `Boosts.svelte` (the bank UI), `Store.svelte`, `VictoryCard.svelte`,
  and `activateChronoBoost` in `commands.ts`.
- `OfflineSimulationEngine` pushes overflow time into the bank — so deleting it
  changes what happens to time beyond the 12-hour offline cap.

### Decide before deleting

Deletion is not free and not obviously right:
- What happens to offline time past the cap once the bank is gone? Today it is
  banked rather than discarded. Discarding it is a **balance change**, not a
  cleanup.
- Players hold banked seconds now. Deleting the table destroys a currency they
  earned — the same class of problem as the stranded ores, and it needs the same
  decision.
- It is one of the few things diamonds and the Store are wired to.

### Scope, if the answer is still delete

1. Write down what replaces it for over-cap offline time.
2. Client first — remove the Boosts bank UI and the Store hook, ship, confirm
   nothing else calls it.
3. Then the engine and the queue.
4. Then the wire fields. Removing them **frees space on `StateUpdatePacket`**,
   which is near its ~700-byte ceiling — a real benefit. Both layout-guard
   constants and `npm run generate:protocol` move in the same commit.
5. Migration last: decide compensation for banked seconds, then drop the table.
6. `SeasonEraEngine` and `PlayerChronoRegistry` are already listed as dead code
   in `CURRENT_IMPLEMENTATION_STATE.md` §9 — fold them into the same pass.

### Done when

- No `Chrono` symbol remains outside migration history.
- `StateUpdatePacket` is smaller and the guard says so.
- Over-cap offline time has a documented, deliberate behaviour.
- Existing banked seconds were compensated or explicitly written off.

### Risk

**High.** Touches the wire, the tick, offline progression and the store, and
carries a destructive migration. The single largest task here.

---

## Cross-cutting notes

- **Two of these are already half-built** (tutorial, daily login) and one is
  half-broken in a way nobody could see (audio in production). Check what exists
  before estimating any of them.
- Anything touching the wire — task 3 — costs a layout-guard change and a
  protocol regeneration. Anything that can be REST instead should be.
- The village, ore and equipment-slot traps in the 2026-09-01 handoff are the
  same shape as several of these: a system that renders correctly while doing
  nothing. Prefer `exercise.mjs` assertions over screenshots when closing any of
  these out.
