# FolkIdle Next Steps Backlog

Status: living document. Numbered items are independent units of work;
number order is priority order within a category, not a strict dependency
chain unless stated. Remove an item when it ships; do not renumber the
remaining items (a gap is fine and preserves historical references in
commit messages/PRs).

Most sections below predate the web client and describe Unity work. The
dated handoff immediately following is the live one.

**For the NEXT block of work, see `docs/TASK_BOARD.md`** - seven scoped tasks
with acceptance criteria, written 2026-09-01 against what the code actually
does. This file remains the record of what happened and why; the board is what
to do next.

---

# HANDOFF 2026-09-05 - Tasks 8, 9 and 10 closed, and what closing them found

`docs/TASK_BOARD.md` now reads 1-10 done. The detail lives there, phase by
phase; this is the short version and the loose ends.

## The three tasks

**8. "I can't see the fight."** The health bar was never broken - the wire was
empty. Measured before changing anything: across 27 consecutive snapshots
`CurrentMonsterHp` took **exactly one value**, because snapshots arrive every
~1090 ms and a geared character kills an early monster every ~1400 ms. Spawn and
death both happened between two samples. A control run against a 71,000 HP boss
produced 20 distinct values, which proves the bar animates fine when there is
something to animate.

So the wire carries combat events now (`ResponseCombatEventPacket`, 26 bytes,
one per resolved blow) and the screen has the fight log the player asked for.
The **miss** is the load-bearing event: it moves no health, so no inference from
a health difference could ever have produced it.

Two defects fell out: both health bars had **fictional maximums** - the
monster's from a hand-copy of `BossFirstClearRules` that predates First Blood,
the player's from a session high-water mark caught reading "2320 / 2320" while
`PlayerHp` was 3701. Both are on the wire now.

## CLOSED 2026-09-06 - the second loot fault, and the gathering economy

**Live kills paid no loot because GATHERING was starving the loot worker.**
One background loop drains two queues, and the gathering half was
`while (queue.TryDequeue(...))` with an `await` inside - which terminates only
when the producer pauses. The producer does not pause: a harvest enqueues one
grant PER ROLL, the roll count is scaled by the codex yield multiplier, and that
multiplier was **71.9x** on the reporting account. ~290 grants a second against
a worker that could write thirty, each grant paying for its own transaction and
three round trips to Supabase. The loop entered the gathering drain and never
came out.

Every symptom of the previous handoff falls out of that: `tick saw 0` was the
last honest line printed *before* the wedge (the report sits above the drain),
the log going silent was the same loop, and "loot only arrives at a relogin" is
the queue finally emptying when gathering stops. Confirmed live before changing
anything - codex +54 kills a minute, `EquipmentInstances.Id` unmoved,
`CommodityRecords` taking 52 writes a second, last log line eleven minutes old.

Fixed by budgeting both drains, coalescing grants per (player, material) into
one transaction, and reporting the gathering queue in the heartbeat. Full
evidence: `docs/drop_rates_investigation_2026_09_05.md`, section "THE SECOND
FAULT".

**The gathering economy was rebalanced against its own sinks.** The codex yield
multiplier is capped at 2.0x and mastery speed is `40 * sqrt(level)` where it
was `10 * level` uncapped; the tool curve is untouched. Supply at the top falls
36x, a new player is unaffected, and the most expensive recipe in the game moves
from eight minutes to 3.2 hours. `GatheringEconomyTests` prints supply and every
sink in the same units and fails when they drift apart. Details and the tables:
`docs/gathering_balance_2026_09_06.md`.

**STILL OPEN, and deliberately not taken: the codex DAMAGE multiplier is
`1 + 0.01 * levelSum`, uncapped, and stands at 142x on that same account.** It
is one line above the one that was capped, and capping it would divide a live
character's output by a hundred - a re-pacing of the whole game, not a bug fix.
The arithmetic and what to read first are at the bottom of the gathering doc.

**A 24-hour JWT with no refresh token** logs every player out mid-session; the
client now signs them out cleanly instead of retrying a dead token forever, but
the product decision is unmade.

**Reported after those landed: "it looks like no items are dropping."** The
pipeline was intact - the roll matches its authored table across 2,000,000
samples, nothing caps the ladder, every monster's table is its own region's,
`EquipmentDropChance` has only ever gone up, and the player's last 91 drops
averaged tier 2.26 against a theoretical 1.918. **The panel was throwing them
away**: one shared 100-entry ring held materials and equipment, and two
characters gathering turned the whole buffer over in about four minutes. Split
into two stores and two lists; the odds (Legendary+ is 1 kill in ~780) are now
stated on screen, because they never had been. Full evidence in
`docs/drop_rates_investigation_2026_09_05.md` - read it before "fixing" drop
rates in response to a similar report.

**9. "Rarity barely does anything."** True, and worse than reported. The whole
fourteen-tier ladder was worth **1.48x** against a region step's **3.00x** -
walking from the worst item in the game to the best one bought less than half a
region. Nine of thirteen adjacent tiers were mechanically identical, Godly and
Transcendent among them.

Decided with the player: rarity is worth one region step, 1:1. It is **3.00x**
now, the region curve is untouched, and monster health rose per region so kill
time and XP/sec land within **0.2%** of where they were. Region 1 is deliberately
unbuffed - a new player is only 1.07x stronger and this game has shipped a
closed entrance once already.

**10. World boss.** Five armour plates, one soft, re-seeded every encounter.
The design went in `docs/world_boss_design.md` before any code, and the
arithmetic overturned its first answer: three plates matched the attempt budget
exactly, which meant a blind player could not fail, so knowing was worth 1.2x
and nobody would read the board. Five puts it at 1.67x.

The half worth reading is smaller than the mechanic: **the client stopped
sending a damage figure.** It posts a plate index and the server takes the
damage from the player's own cached attack power. There is no longer a quantity
to inflate.

## What building them turned up

Four silent-failure defects, none of them in the new code:

1. **World boss attempts did not survive a logout** - written by one
   notification, loaded by nothing, so the screen offered three spent attempts
   to a server that refused them in silence.
2. **The battle session cap was invisible from every angle.** Five minutes from
   the first strike, inside a seven-day encounter, and nothing carried the
   deadline. An idle player who strikes once and comes back later is the NORMAL
   case in this genre; it cost them two thirds of their participation and never
   said why.
3. **The affix lock was inert** - read in ten places, set to true by nothing.
   The inverse of this codebase's usual defect. Built.
4. **The lock meant less than its name** - honoured by reroll and fusion,
   ignored by both removal paths, so a "locked" item could still be swept.

## Two new guards, because both of those found their own defect

- `StateUpdatePacketFieldCoverageTests` now checks the wire BOTH ways: every
  field is copied into the packet, and either loaded at login or on
  `RuntimeOnlyByDesign` with a reason. That is what would have caught (1).
- `serverMirrors.test.ts` gained the world boss and combat-log constants. The
  log ones matter most: a mismatch there does not throw, it relabels every line.

## Loose ends, all in `docs/audit_2026_09_05.md`

- **`stores/damage.ts` still infers the floating numbers** from snapshot
  differences. The feed makes that redundant and it should end as one source of
  truth - deliberately not done in the commit that introduced the replacement.
- **The 300-second battle session cap should probably go.** Now that it is
  visible, it punishes the player who strikes once and closes the game and
  rewards the one who sits on the screen. Same shape as the clicker ban and the
  active skills, both reverted.
- **Four orphan logs** in `items.json` that nothing can obtain.
- **`RegisterWorldBossDamage` (opcode 19)** is retired but still declared.

---

# HANDOFF 2026-09-03 (c) - The chest had no drain, and that is what made it lag

Reported as "it's starting to lag because of the amount of items I have".
Correct, and measurable. The live database:

| Player | EquipmentInstances rows |
|---|---|
| **8** | **17,836** |
| 7 | 49 |
| 6 | 26 |
| everyone else | 3 |

**The database was never the problem.** `select * from "EquipmentInstances"
where "PlayerId"=8` runs in **5.3 ms**. The cost was on both sides of it.

## Why it grew: a 15% drop rate against no removal at all

`CombatLootEngine.EquipmentDropChance` is 0.150 and every drop writes a row.
The only removal anywhere in the game was the per-item Sell button on the chest
screen. `CombatLootEngine`'s own comment said so plainly - *"The chest grows;
the player sells or bins what they do not want."*

The trap in that: **the only cleanup tool was on the screen the volume had made
unusable.** A player could not dig out one click at a time, and nothing warned
them they would ever need to. ~50 hours of play reaches 17,836 rows.

## What it cost

- **Payload.** `/api/v1/player/inventory` returned every row: **3.2 MB of
  JSON**, built by calling `JsonNode.Parse` once per row. `/api/v1/forge/inventory`
  did the identical thing again.
- **Render.** `Chest.svelte` was `{#each equipment}` with no window, inside a
  `max-height: 26rem` box - ~180,000 DOM nodes to show about twenty.
  `ItemBrowser.svelte` (Forge, Market) was the same.
- **Four multipliers.** `ItemIcon` called `loadContent()` from its own
  `onMount`, *per icon*, and `content.ts` cached the resolved value rather than
  the in-flight promise - so a cold cache meant tens of thousands of concurrent
  fetches for three files. `prettifyBaseId` (regex + split + join, uncached) ran
  ~4x per item per render and inside a sort comparator. `Character.svelte`'s
  `wornBy` linear-scanned all rows 33 times per render. Larder, Boosts and the
  guild depot each downloaded the whole 3.2 MB to read `Stacks`, which is **63
  rows**.
- **Per kill, for nothing.** `CountOccupiedBackpackSlotsAsync` ran two COUNTs
  and a roster read inside a Serializable transaction on **every kill**, to
  produce a number `SimulationEngine`'s drain overwrote with `InventoryCapacity`
  unconditionally, because the backpack was retired.

## What changed

Rendering and payload:

- `lib/ui/VirtualList.svelte` - a uniform-row windowed list. Used by the chest
  and the item browser. **Rows must match the `rowHeight` passed in**; it
  positions by arithmetic, not measurement, and a taller row overlaps rather
  than pushes.
- `/api/v1/player/materials` - stacks only, two queries, no JSON parsing.
  Larder, Boosts and the guild depot use it. `invalidateOwnedItems`
  (`net/queryClient`) invalidates both keys, because they answer from the same
  tables and a call site that remembers one is a stale screen.
- Memoised `prettifyBaseId` and `ItemBrowser`'s `slotLabel`; `loadContent`
  caches the promise; `net/registry.svelte.ts` is one shared reactive registry
  so `ItemIcon` no longer loads content per instance; `wornBy` is a Map.

The drain (this is the part that stops it coming back):

- **Bulk sweep.** `/api/v1/chest/bulk-sell` and `/bulk-discard` clear a whole
  rarity band in ONE transaction - `VillageChestEngine.RemoveEquipmentUpToTierAsync`.
  Measured: **1,472 items in 131 ms**, gold exact, worn items skipped.
- **Auto-salvage.** `PlayerRecord.AutoSalvageBelowTier`, off by default, set in
  Settings. A drop at or below it is sold on the way in and never becomes a row,
  for the same gold a chest sale pays (one `ValueEquipment`, so the two cannot
  become two economies). Crafted and market items are deliberately untouched -
  those were asked for.
- Both are capped at **Epic (6)** by `VillageChestEngine.MaxSweepableQualityTier`.
  Legendary and above is never sweepable and never auto-salvaged; there is no
  undo. A request above the cap is **refused, not clamped** - clamping hands the
  player a destructive setting they did not choose.

## Standing traps this added

- **Two gold paths that must not be confused.** `AutoSalvageQueue` wrote no row,
  so its drain adds to `CurrentGold` *and* `RedisPendingGoldDelta` (the combat
  idiom). `ChestSaleGoldQueue` follows an engine that ALREADY credited
  `CommodityRecords["gold"]`, so its drain moves `CurrentGold` only and must
  leave the pending delta alone - the checkpoint applies that as an increment
  and would pay the sale twice, one checkpoint later, where nothing connects the
  two. Both are commented at the struct.
- `AutoSalvageBelowTier` is hydrated onto `TickStatePayload` at login, so the
  settings route posts to `ChestSettingsQueue` as well as writing the row -
  otherwise the toggle does nothing until the next sign-in.

## A real bug found on the way

`VillageChestEngine.RemoveEquipmentAsync`'s worn-item guard listed the **eight
combat slots and stopped at `EquippedRingId`** - so a worn **axe, pickaxe or
rod could be sold or binned out from under the character wearing it**, leaving
exactly the dangling pointer `ClearDanglingEquipReferencesAsync` exists to heal.
The eleven-slot truncation again; see CLAUDE.md. Both the single and bulk paths
now go through `LoadWornEquipmentIdsAsync`, which is the one place the slot list
is written down.

---

# HANDOFF 2026-09-03 (b) - Offline loot was a worse game than online loot

Reported as "when I am offline almost nothing good drops for equipment". True,
for two independent reasons, and the second is the larger one.

## Offline rolled rarity with up to 73 points less luck

`CombatLootEngine.RollTier(lootLuckPct)` decides rarity and nothing else does -
tiers 2-14 scale by `1 + luck/100` while Normal keeps a flat weight - so that
one figure IS the drop table. The live tick summed six terms into it. The
offline path summed **two**, and passed no `BonusRarityTiers` at all:

| source | live | offline |
|---|---|---|
| gear/affix luck, inheritance | yes | yes |
| Fortune root (loot rarity skill), cap +10 | yes | **no** |
| Rarity bough, cap +8 | yes | **no** |
| guild DropRate buff (tier x2), cap +10 | yes | **no** |
| Fortune breeding aptitude, cap +45 | yes | **no** |
| Golden Fleece (+2 tiers every 100th kill) | yes | **no** |

Measured against the real weight table, at a zero-gear baseline that is Normal
**50.8% offline against 37.4% online**, and Rare-or-better 11.0% against 14.0%.
Every point a player had spent on rarity stopped existing when they closed the
tab.

The live copy of that sum already carried a comment begging for it to be kept
contiguous, because an edit had once slid a term out of it. That is the same
failure twice, and a comment cannot fix it. **The sum now exists once**, in
`CombatLootDropRequest.Build`, which both paths call.

## Offline equipment was capped at 500 rolls - worth 25 pieces, ever

`equipmentDropsToGrant = Math.Min(totalKills, 500)`. Equipment drops at **5% a
kill**, so that cap is 25 pieces however long the player was away, while the
materials beside it were capped at 200,000 and were effectively uncapped. Hence
"materials fine, gear nothing".

| kill time | kills in 12h | pieces online | offline | share |
|---|---|---|---|---|
| 15s | 2,880 | 144 | 25 | **17%** |
| 30s | 1,440 | 72 | 25 | **35%** |
| 60s | 720 | 36 | 25 | 69% |

The cap was not arbitrary: every request cost its own scope, SERIALIZABLE
transaction and commit, and thousands of those on one login is real load. So the
fix is not to raise it - `CombatLootDropRequest.Kills` lets ONE request carry the
whole window, and the loot engine rolls it inside a single transaction. The rate
is now the online rate at one transaction per catch-up instead of thousands.
`MaxOfflineKillsPerSlot` (200,000) replaces it as a runaway guard only; the
twelve-hour window cannot reach it.

Two struct fields were chosen so that `default` preserves live behaviour, which
is load-bearing: `CombatLootDropRequest` is a **struct with no field
initialisers** and the live tick does not set them. `Kills` reads 0 as 1 (0 as
"no kills" would switch off every drop in the game), and the material flag is
named `SkipMaterialRoll` rather than `RollMaterials` for the same reason.

Also fixed here: offline material rolls ignored `GlobalEngineState
.GlobalDropMultiplier`, so an admin's double-drop event paid double only to
players who sat and watched it.

Also found: offline was granting materials TWICE - once from its own bulk
projection and again from each of the (up to 500) equipment requests. The cap
was hiding it. Equipment requests from offline now skip the material roll.

## Two notes for anyone testing this

- **The canonical monster ladder is ids 91-115.** Ids 1-90 are legacy rows that
  resolve to no fight at all: a payload with `ActiveActivityId = 1` runs a full
  offline window and earns zero XP, zero gold and zero kills, silently.
- A test that asserts "offline is not capped at 500" has to produce **more than
  500 kills**, or it passes against the bug. A bare level-40 statline kills a
  Field Mouse every ~143 seconds - 301 kills in twelve hours - and never reaches
  the cap. Both new tests were mutation-checked against the un-fixed engine.

## And the cap that is left is now something players are told about

With the 500-drop cap gone, the only cap remaining is the one that was always
meant to be there: the **twelve-hour offline earning window**
(`OfflineSimulationEngine.MaxOfflineSeconds`, eighteen hours with Vodnik mastery
25). Time past it is discarded, deliberately - so there is a real moment, known
server-side, after which being away earns nothing.

`OfflineCapNotifier` mails a player once when they reach it. Three rules it does
not bend, because this is the only thing the server sends to a person rather
than to a client:

- **Opt-in.** `PlayerRecord.EmailNotificationsConsented` starts false for every
  account, the migration does not backfill it true, and the only thing that
  writes it is the player's own toggle in Settings.
- **Once per absence.** `OfflineCapEmailSentEpoch` is compared against
  `LastLogoutTimestamp`, not against now, so a player away for a week is told
  once rather than nightly. Opting out clears the marker so opting back in later
  is not silently blocked.
- **A refused send is not recorded.** With no provider configured the registered
  `IEmailSender` is `DisabledEmailSender`, which returns false; the player then
  stays eligible, so an outage delays the mail rather than cancelling it.

Consent lives on the account rather than in `localStorage` on purpose: a
background job decides to mail somebody hours after the tab closed, and a
consent flag in a browser is one nobody can ask. `/api/v1/player/email-consent`
GETs and POSTs it, and refuses consent (409) on an account with no address, so
a guest is told why instead of being shown a toggle that does nothing.

## MIGRATIONS DO NOT RUN ON APP START LOCALLY

`CLAUDE.md` says they do. They run on the **container entrypoint** in
production (`--migrate && exec ...`, see `ops/oracle/docker-compose.yml`), which
is why deploys are fine - but `run-dev.ps1` starts the server with no arguments
and `Program.cs` only migrates under an explicit `--migrate`.

The failure this produces looks nothing like a migration problem: the server
starts, `/gamedata` answers **200**, and then signing in hangs forever with no
shell, because the first query touching the changed table fails on a column that
does not exist. After adding a migration, run it by hand:

```powershell
$env:FOLKIDLE_DB_CONN='Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres'
dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj --migrate
```

## Still open here

- The offline "drops" summary counts materials only. Equipment is rolled later on
  the loot engine's own thread, so the number is not knowable at projection time;
  it used to report the REQUEST count, which overstated the truth twentyfold.
- The notifier's copy is written for the twelve-hour window and names eighteen
  hours correctly for Vodnik, but it is **English only** - the game ships four
  languages and this mail ignores `LocalizationMatrix`.
- Nothing tells a player the mail exists other than the Settings toggle itself.

---

# HANDOFF 2026-09-03 - Two bugs that made gear and the village look decorative

Both were reported from play, not found by a checker, and both are the same
shape as this codebase's usual worst defect: the screen renders perfectly while
the number on it describes something else.

## The paper doll showed slot 1's combat rating under every tab

Reported as "I click between slots 1-3 and their stats are identical except the
set bonuses". Exactly right, and the set bonuses were the tell: they are the one
part of that panel already computed per character, from the inventory snapshot.

`Character.svelte` read `snap.PlayerAccuracyRating` / `PlayerArmorRating` /
`PlayerBlockStrengthPct` straight off `StateUpdate`, and **that packet carries
the ACTIVE character's combat rating only** - deliberately, for the same reason
the three tool slots have no wire field: gear changes on a button press, not at
10Hz. So switching tabs changed the gear on screen and left the numbers beside
it describing whoever was actually fighting.

`/api/v1/player/inventory` now also carries `RosterCombatStats`, one entry per
playable slot, computed by `EquipmentSlotEngine.ComputeCharacterCombatStatsAsync`
from that character's own gear. That method is the reusable half of
`GuildWarSnapshotEngine.BuildMemberCombatStatsAsync`, which had been doing this
correctly for guild war all along - for the account's first character only.

**STR/DEX/CON/LCK and skill points are genuinely account-wide** and still read
identical across tabs. They live on `PlayerRecord.BaseStrength` and friends;
`CharacterRecord` has no attributes at all. That half of the report was not a
bug, and the new test says so out loud so nobody "fixes" it later.

### The trap inside the fix

The first version narrowed the roster query to three characters and used it for
BOTH questions the handler asks. That was wrong, and the live database said so:

```
PlayerId | SlotIndex | weap | axe
       1 |         0 |    1 |   1
       1 |         4 |    1 |   1     <- benched, and still holding gear
```

**A benched character past slot 2 really does keep its `EquippedWeaponId`.**
Narrowing the worn-item map would have reported those items as free, offered
them in the picker, and let one `EquipmentInstance` be worn by two characters
the moment that ancestor was fielded. The handler now runs two queries on
purpose: every character for "who wears this", the three playable slots for
"what is this character's rating".

Also worth writing down, because it was assumed and was false: **`CharacterRecord
.IsLockedInEscrow` is false for every character row** (167/167 locally). It is an
equipment-escrow flag. What actually bounds the canonical roster query in
`StateCheckpointManager` is `OrderBy(SlotIndex).Take(3)`, not that filter.

## Town Hall and the Crafting Workshop never left material tier 0

Reported as "upgrades are too easy, I am on Town Hall 5 and have never once used
a tier-2 material". Measured and true, for a reason that is pure arithmetic.

`GetTierMaterials` bands every five levels (`currentLevel / 5`), which is right
for a building that keeps climbing - production and service buildings reach 12
under a maxed Town Hall, so they walk tiers 0/1/2. But the two STRUCTURAL
buildings hard-cap at `MaxStructuralBuildingLevel` (5), so their `CurrentLevel`
is only ever 0-4 when an upgrade is priced, and `currentLevel / 5` is therefore
**always 0**. The two buildings that gate the whole village - Town Hall raises
every other building's ceiling and unlocks character slots 2 and 3, the Workshop
feeds crafted-item rarity - could be taken to their cap on region 1's
`copper_ore` and `birch_log` alone.

Structural buildings now walk **one tier per two levels** (tiers 0,0,1,1,2), so a
maxed Town Hall costs region-3 materials.

**One tier per LEVEL was the other candidate and was deliberately not taken.** It
prices the last Town Hall level in `ebon_log`/`darksteel_ore`, and Town Hall 5 is
what unlocks the third character slot - so the third character would become an
endgame reward gated behind region 5. That is a re-pacing of the game, not a fix
to this bug, and `BaseUpgradeCost`'s own comment records a pass that deliberately
CUT village costs because a wall had formed in exactly this place.

Note that the two formulas are now a matched pair only for non-structural
buildings: `CalculateProductionUpgradeCost` still resets on `currentLevel % 5`,
and the comment on `Test_VillageManagementEngine_ProductionUpgradeCost_
ScalesExponentially` describing that pairing is about those buildings.

**Nobody already at Town Hall 5 pays anything back** - this only bites a village
still being built, or a fresh season.

## What is still open here

- **Quantity, not just tier, is the other "too easy" lever.** A structural
  upgrade costs 100/150/225/338/506 units, and an established account measured
  152,968 `birch_log`. Raising the tier makes the ladder real; it does not by
  itself make the numbers demanding. Untouched deliberately - that is a balance
  decision, and `ProgressionRateTests`/`GatheringShareTests` are where it would
  have to be argued.
- The Character panel falls back to the wire packet when `RosterCombatStats` has
  not loaded yet. Correct for slot 1, briefly wrong for slots 2 and 3.
- **`exercise.mjs`'s "the market pages rather than dumping the book" is flaky.**
  It failed once in three runs here and passed either side of that, unchanged.
  Both it and the check above it read ONE `innerText` snapshot taken right after
  navigation; the earlier check accepts `"Loading the market"` as a pass, this
  one requires `Page N of M`, so a slow market load fails only the second. The
  snapshot wants a `waitForFunction` on the listing having rendered, not a
  wider regex - a wider regex would make it assert nothing.
- Two `exercise.mjs` runs without an intervening `--seed-dev` do not compare:
  the second run reported 93 checks against the first's 99, because steps that
  spend fixture state stop being reachable. Re-seed between runs before reading
  anything into a changed total.

---

# HANDOFF 2026-09-02 - The game had no entrance (READ THIS FIRST)

Two sessions landed in one commit: the seven task-board tasks, and then the
verification pass that was the top open item on that board. Read
`docs/TASK_BOARD.md` for the per-task state; this is the one finding that
changes how the game plays.

## A new player could not get in, and had not been able to for some time

Onboarding's first instruction was "open Combat and press Fight on Field
Mouse". Driven against the live server on a **brand-new registration**:

| start | outcome |
|---|---|
| naked, empty larder | dead at **29 s**, Field Mouse still on **264 of its 465 HP** |
| after 60 s of fishing (16 perch) | dead at **65 s**, mouse down to **73** - closer, still a loss |
| a properly stocked larder | the fight is won, at about the 75 s the model predicts |

The character has 100 HP and the mouse deals 8 every 2 s; it out-damages the
player better than two to one, and the monster resets on death, so no number of
retries wins. Tier one **blocks in order**, so step 1 could never be completed
and the food advice - step 3, behind two steps that a kill has to finish - was
never shown to the only people who needed it. The tutorial's own first
instruction killed the player and then said nothing more, forever.

**The balance was not at fault and was not touched.** `ProgressionRateTests`
`.TheFirstMonsterTakesAboutSeventyFiveSeconds` passes, and passes because
`FreshPayload` hands its simulated character 1,000,000 bites of food; its own
comment says that without them "the character dies in about thirty seconds",
which is within four seconds of what a real account does. Region 1's attack was
deliberately left un-tripled for the same reason ("The whole ladder was raised
threefold", below). The model was always right *given food*. Onboarding was what
failed to deliver the food.

**Fixed by reordering tier one to larder -> fight -> gear**, which is the true
dependency: you cannot win the first fight without food, and you cannot wear a
drop before the kill that drops it. Client-only; no content or balance number
moved. See `docs/onboarding_steps.md` section 2.

Guarded two ways, because a test alone would not have caught it: `tutorial.test.ts`
("starts with the larder, because the first fight cannot be won without it"), and
`exercise.mjs`, which now registers a real account, fishes with the granted rod,
stocks the larder and asserts the step advances.

## Why nothing caught it: the fixture is the wrong witness

`dev@folkidle.local` is level 40, geared, stocked and an admin. It has already
done everything onboarding asks, so the coach panel is empty for it and every
tier-one predicate reads true - signing in as the fixture proves nothing about
onboarding at all. It also masked `/api/v1/admin/status` answering **403** to
ordinary accounts, which is correct behaviour whose console error simply never
appeared until a non-admin drove the client.

This is now a load-bearing rule in `CLAUDE.md`.

## `exercise.mjs` had been rotting, quietly

Three checks were failing on a working game because the script **consumed the
fixture state its own later steps needed**, so it could only ever pass on a
fresh fixture and had been degrading run by run:

- **Ancestors "Keep"** - marking is a flag nothing clears. Each run marked one
  more member until all 23 non-main ancestors read "Kept" and the check failed
  permanently with "no Keep button rendered". Now a **round trip** that asserts
  both directions of the same button and puts the flag back, so the hundredth
  run reads like the first.
- **Doll "Wear"** - the picker lists the piece already worn and sorts it first,
  so `wear.first()` re-equipped what was already on, the slot text was identical
  before and after, and a working game read as a failure. Now picks an item
  whose row does not name the worn one.
- **Village / breeding** - "Send on" ran first and ate the last villager who had
  not married in, which is exactly the villager the breeding step then needed.
  It holds the last one back now. An exhausted pool is reported honestly, and
  the assertion became "a greyed-out option **states a reason**" rather than a
  list of reasons I enumerated - the first attempt at that failed on
  "(both women)".

`DevFixtureSeeder`'s standing villager pool went **2 -> 6 per sex**. The comment
there already said "a fixture that survives one pass is not re-runnable", but
the top-up only runs on an explicit `--seed-dev` and nothing calls it between
runs, so two-per-sex lasted about two passes and the pool had measured empty
with twenty elders standing in it.

## Deploying found two more things, both in the verification itself

**`git pull` on the Oracle box does not work and had not for some time.** The
repository is private and the box carries no GitHub credentials at all - no
helper, no `~/.git-credentials`, no deploy key - so it fails with `could not
read Username for 'https://github.com'`, which reads like a dead network. The
route that works needs no credentials on the box: set
`receive.denyCurrentBranch=updateInstead` there once, then
`git push ssh://folkidle-server/home/ubuntu/folkidle main` from a machine that
has the commit. Written up in `ops/oracle/README.md` and the deploy skill.

**`smoke:screens` hardcoded localhost and ignored `FOLKIDLE_E2E_BASE`** - the
exact variable the deploy skill tells you to set to aim it at production. The
post-deploy check had therefore been smoke-testing the developer's own dev
server and reporting a pass for a box it had never opened. It honours the
variable now, and its screen list - which had gone stale in both directions -
is checked against the nav rather than trusted: it was still asking for
`Larder` (now `Auto-Eat`), `Social` (now `Friends`), `Chat` (a dock, not a
screen) and `Bank` (the chrono bank, deleted in this very release), while never
visiting Map, Leaderboards, Ancestors, Inheritance, Skill Tree or Wiki at all.
Six screens had been going unchecked, silently, which is the half a click
timeout never reports.

Production now reads **25/25 screens clean**.

## The clipping audit became a script, and found one

Task 2b was "look at every panel for cropped content", which does not scale to
25 screens and does not run again next month. It is `npm run check:clipping`
now: every screen at 1500 / 900 / 390px, reporting content wider than its box
in a container that cannot scroll. **0 findings.**

Three exclusions carry the whole signal-to-noise ratio, and each was learned by
getting it wrong first: `overflow-x: auto` is a deliberate scroller (CLAUDE.md
asks for those); `text-overflow: ellipsis` announces its own truncation with a
visible "..." and the Chest does it 724 times on a phone, correctly; and SVG
reports `clientWidth` in another coordinate system, which made the Skill Tree's
labels look like 91px overflows in 29px boxes.

The real one: **Gathering's node rows** hung 9px past their panel at the 900px
breakpoint, slicing the Gather button, because `minmax(7rem, 1.4fr)` plus
`minmax(5rem, auto)` plus three gaps demand more than a 245px panel has. The
floors are `minmax(0, ...)` now - a `fr` track still takes its proportional
share, so the name column stays widest and keeps wrapping, without demanding a
width the panel cannot give.

`overlap-check.mjs` reads 0 as well. Its four findings were all the fixed
ChatDock sitting on whatever occupies the bottom-right corner at the current
scroll offset; each was measured freely reachable by scrolling, so a fixed
overlay no longer counts as an overlap. Four permanent false positives on a
check with four findings is the entire signal. `app.css` now reserves
`padding-bottom` so a control at the very END of a screen - where there is
nothing left to scroll - can still be brought clear.

## Three checkers, one rotting list

`SCREENS` was copied into smoke-screens, overlap-check and the new clipping
check, and every copy rotted its own way. It lives once now in
`scripts/screens.mjs`, along with the sign-in, a hamburger-aware `go()` and
`assertMatchesNav`, which makes the nav itself the authority instead of the
file. The shared sign-in also stopped waiting on a visible `text=Combat`: below
the mobile breakpoint that button exists but never becomes visible, so every
narrow-viewport run died on a 25-second timeout that named a locator and said
nothing about the breakpoint.

## The Wiki was finally opened, and taught the old order

Fifteen pages, no console errors, and the layout holds. But its **core loop**
still opened with "Fight", put the larder third, and said it was the *fourth*
monster of a region that kills an unfed character. That is the closed entrance
again, preserved in the one place a player goes to look things up. Corrected to
match: larder first, and it is the FIRST monster.

## State at hand-off

537/537 server, 294/294 client, **99/99 `npm run exercise`**, 25/25
`smoke:screens` against both localhost and production, 0 clipping findings,
0 overlaps, and `svelte-check` at the documented 4 errors (the pre-existing
`GuildOps.svelte` ones) and 16 warnings.

One thing is still open, and it is a decision rather than a defect: the 40
legacy `*_crafting_material` entries are classified and deliberately undeleted
(`docs/crafting_material_audit.md`).

---

# HANDOFF 2026-09-01 - Tooling, and the guild donate panel that never worked

Three weeks passed with no commits. Nothing was half-finished when work stopped
at `4397a7f`; the tree was clean and deployed.

## The repository now carries its own instructions

There was no `CLAUDE.md`, no skills, no hooks and no agents, so every session
re-derived the project by reading this 134 KB file. That is fixed:

- **`CLAUDE.md`** holds the rules that actually bite - the stale-DLL build,
  Docker before `dotnet test`, generated wire types, the snake_case table
  overrides, why `npm run exercise` is the only verification that proves
  gameplay. **`AGENTS.md` points at it rather than copying it**, so Antigravity
  and any other tool read the same file. A copy would be the largest instance
  of the drift this codebase keeps losing to.
- **Four skills** in `.claude/skills/`: `run-stack`, `verify`, `add-command`,
  `deploy`.
- **Three hooks** in `.claude/hooks/` make rules mechanical instead of
  remembered. `guard_stale_build` BLOCKS `dotnet build`/`dotnet test` while
  `FolkIdle.Server.exe` holds the output directory - it caught a real one
  within the hour. `packet_struct_guard` fires on wire-struct edits.
  `validate_content` BLOCKS on bad `GameData` JSON. Both blocking hooks fail
  OPEN: a guard with no workaround is worse than the trap it guards.
- **A `client` CI job**, parallel to `test`, gating `build-and-push`. Its real
  purpose is `generate-protocol.mjs --check` - the packet-drift check that
  script's own comment says CI should run, and that had never been wired up.
- **`.gitignore` was ignoring all of `**/.claude/`**, which silently made
  project skills untrackable. Narrowed to the machine-local files.

**A `wiring-auditor` subagent** exists in `.claude/agents/`. It walks command →
handler → effect → persistence → wire → client call → screen → exercised and
reports the first break. It was written for this project's dominant defect
class and the bug below is exactly what it looks for.

## The guild Donate Materials panel was dead in production

Found while clearing lint, not while hunting bugs. **Every button in that panel
was permanently disabled** - To depot, To chain and Donate alike.

`depotMaterial` was `$state(0)`, a numeric item id, but the `<select>` binds
`<option value={baseId}>` - strings. `depotMax` looked up
`row.definition.Id === depotMaterial`, compared a number against a string,
never matched, and stayed 0 forever. All three buttons carry `depotMax === 0`
in their `disabled` expression.

**The live database confirmed it before the fix was written.**
`GuildDepotBalances`, `GuildContributionLedgers` and `GuildActiveBuffs` all held
zero rows. The guild buff system shipped 2026-08-11 has never been activated by
anyone, because the only way to fuel it could not be clicked. That reading came
from the read-only Supabase MCP, which answers the question this document has
asked since 2026-08-06 - "nothing is measured against real players" - and should
be used that way again.

Two more layers surfaced only by driving it in a browser, which is the point of
driving it in a browser:

- **The materials were filtered out of the panel entirely.** `depositable`
  dropped any stack with no `ItemDefinition`. See the namespace trap below.
- **Donating needs a catalogued item after all.** `GuildDepotBalances` is keyed
  on `ItemDefinitionId` and the engine bails on
  `TryGetItemDefinitionByBaseId`, so an uncatalogued commodity is a 400 however
  much the player holds. The buttons now match the server's rule.

## THE MATERIAL NAMESPACES - the single most expensive idea in this codebase

Six separate defects traced to this in one day. It belongs here rather than
only in `DevFixtureSeeder`'s comments, where it was already written twice.

Materials all live in one `CommodityRecords` table, keyed by a STRING, and
several different string spaces were in play at once:

| Space | Examples | In `items.json`? | Resolved by |
|---|---|---|---|
| gathering slugs | `raw_log`, `oak_log`, the village's `wood`/`stone` | No | `ContentRegistry.GetMaterialString` (ids 1-6) |
| catalogued items | the region logs and ores, all equipment | Yes | `GetItemBaseId` / `TryGetItemDefinitionByBaseId` |
| `*_crafting_material` | `copper_ore_crafting_material` | Yes, separate ids | recipe ingredients |

Anything keyed on `ItemDefinitionId` silently rejects the first space, and the
client's `registry.itemsByBaseId.get()` returns undefined for it - which is how
a player holding 5,000 copper ore saw it listed as "x0".

**ORE IS NOW ONE SPACE, as of 2026-09-01.** There is exactly one ore per region
and everything uses it - mining pays it, the village spends it, tool recipes
consume it, guild buffs take it:

| Region | Common (90%) | Rare (10%) |
|---|---|---|
| 1 Sunlit Plains | `copper_ore` | `malachite_ore` |
| 2 Whispering Woods | `iron_ore` | `hematite_ore` |
| 3 Scorched Wasteland | `sulfur_ore` | `obsidian_ore` |
| 4 Frozen Peaks | `silver_ore` | `cobalt_ore` |
| 5 Shadow Citadel | `darksteel_ore` | `astralite_ore` |

That pairing was always in the gathering loot tables and in the guild's
`BuffTierMaterials`. It did not work because **four of the commons had no
`items.json` entry when those tables were written**, so their loot rows were
pointed at the `*_crafting_material` variants as stand-ins (ids 165, 111, 57,
147). Sulfur and darksteel looked correct only because those two ores did
already exist. So mining paid out `copper_ore_crafting_material` while the
village charged `copper_ore` - the ore a player earned could never buy the
thing it was for. Measured live: seven players holding 175 `copper_ore`
between them, one holding 5,017 of the crafting-material name, which WAS their
mining under a name nothing spends.

The four ores are catalogued (438-441) and every table names them.

**KNOWN, NOT MIGRATED:** existing balances of `copper_ore_crafting_material`,
`iron_ore_crafting_material`, `obsidian_ore_crafting_material` and
`silver_ore_crafting_material` are now unspendable - nothing consumes them any
more. Sulfur and darksteel players are unaffected, having always earned the
right name. Converting those balances is a one-off `UPDATE` and a decision, not
a bug fix.

Before touching a material feature, check which space it needs and that the id
exists there. `EveryRecipeIngredientIsObtainableFromSomeSource` is the guard
that catches half of this and it earned its keep on the day it was needed.

**The fixture could not drive the guild depot at all.** It stocked the four
gathering slugs and the village's three, none catalogued, so every material the
account held answered 400 on the screen that exists for driving things by hand.
It now also carries `birch_log`, `golden_birch_log`, `malachite_ore`,
`willow_log` and `hematite_ore`.

## Tools could be equipped but never SHOWN

Reported as "I equip a tool and nothing appears in the slot". Equipping worked
the whole time - `EquipItemAsync` resolved the slot and `WriteSlot` has handled
all eleven since tools were added. What never happened was the snapshot saying
so: the inventory endpoint recorded the eight combat slots and not the three
tool ones, so an axe written to `EquippedAxeId` came back with
`EquippedByCharacterSlot: -1`. The doll drew the slot empty AND the axe stayed
in its own picker as available - worn and offered at once.

**FolkIdle has ELEVEN equipment slots.** 0 Weapon, 1 Helmet, 2 Chest, 3 Gloves,
4 Leggings, 5 Boots, 6 Amulet, 7 Ring, **8 Axe, 9 Pickaxe, 10 Rod**. Every list
that stopped at eight has been a bug, and three more were found in the same
sitting: `IsEquippedAnywhereAsync` (so one axe counted across the whole roster),
`ReadSlot` (answering "empty" for slots its own writer can fill), and the
fixture's equip switch. **Grep for `EquippedRingId`** - it is the last of the
eight, so every truncated list ends there.

A re-seed also renumbers `EquipmentInstances`, and a character slot still naming
a deleted row is indistinguishable from equipping being broken: the item cannot
appear in the snapshot, so the slot draws empty for ever. The fixture now drops
slots pointing at nothing, across all eleven, before deciding whether the
character is dressed.

## The village could not be upgraded at all, for four separate reasons

Reported as "I cannot even upgrade stuff", diagnosed against the live account.

1. **No player could build anything.** `ExecuteUpgradeBuildingAsync` rolled back
   when the player had no `VillageInfrastructures` row, and the ONLY place such
   a row was ever created is `DevFixtureSeeder`. Real accounts are born with
   none, so every building was permanently unupgradable - including the Town
   Hall, which caps every other building, so the village was frozen behind a
   building that could not be started. The seeded fixture had rows, so every
   village test passed while no real player could upgrade anything. **The
   fixture-shaped blind spot in its purest form.** A missing row now means "not
   built yet" and is created at level 0, still paid for.
2. **The cost was in a currency nobody earned** - see the ore section above.
3. **Upgrading made buildings worse.** Production and storage were computed from
   `level % 5`, so every fifth upgrade reset the building to its weakest band: a
   Mine paid 500 ore an hour at level 4 and 100 at level 5; a Warehouse held
   2,500 then 500. The cost reset too, so it read as a bargain until the output
   collapsed. **Tiers govern COST and MATERIALS; output may only rise.** Pinned
   by `Test_Village_UpgradingNeverReducesOutput`, which was verified by
   reintroducing the `% 5` and watching it fail.
4. **Every refusal was silent.** Each rejection rolled back and returned, and the
   client's button is only disabled while another upgrade is in flight - so an
   unaffordable or ceiling-blocked press did nothing at all. All five paths now
   report a result code, and the Town Hall ceiling has its own
   (`TownHallCeilingReached = 21`) rather than borrowing the one that talks
   about item rarity.

Gold now funds every non-structural upgrade, so the abundant currency shares the
load with the scarce one. The offline path also stopped computing its own
warehouse capacity, which disagreed with `CalculateWarehouseMaxStorage`.

## Crafting was a job and only a job

Assigning a character crafted one unit per interval FOREVER while materials
lasted - right for idling, wrong for "I need a pickaxe", so making one tool
meant assigning a worker and remembering to stop them.

A one-shot path already existed and reached nothing: `startTreeCraft` ->
`CommandType.InitializeCrafting` had zero callers on the client while the server
handler was live. It is a Craft button now, beside Put to work rather than
replacing it. `ExecuteCraftingAsync` takes a `batchSize` (clamped at 10 IN THE
ENGINE - it multiplies cost and output, so it is not a number to trust a client
with), and the batch rides `DepositQuantity`, an existing field this opcode does
not read, so there was no wire change.

## Private messages were never written down

Chat was Redis Pub/Sub fan-out to whoever was connected, with the client holding
200 lines in a store a reload wiped. So there was no history - and **a whisper
to an OFFLINE player was silently DROPPED**, a bare `return` after the
connected-client lookup missed.

`conversation_messages` is the durable record; the packet stays the live
delivery. The pair is stored SORTED so one thread has one key whoever spoke. The
write sits inside `PublishWhisperMessageAsync` BEFORE the Redis branch, so it
lands whether Redis is up, down or absent - and downstream of
`DispatchInboundChatRequest`, the single choke point where `ChatProfanityFilter`
runs. **Anything that ever writes that table from elsewhere must filter first**:
the filter is applied at neither the engine layer nor the database.

History is REST (`/api/v1/conversations/list|history|read`), deliberately not on
the wire, and pages by timestamp rather than offset because messages arrive
while a player scrolls. The Whispers tab is a list of people, then a thread.

## `exercise.mjs` had been dead for three weeks and nobody noticed

It died at `go('Social')`. That nav item became **'Friends'** when the menu was
reorganised on 2026-08-10 and this script was not updated with it, so every run
since then failed at navigation rather than at a check. The one verification
that proves gameplay works had not run since.

It is now 79/79, and the guild section asserts the donation rather than
asserting the screen renders. **If a `go()` target times out, check
`App.svelte`'s labels before suspecting the screen.**

## The fixture stopped being repeatable, twice

`exercise.mjs` buys an inheritance level every run, and those levels persist and
escalate - so after enough runs the next purchase cost more than the fixture's
fixed diamond grant could ever cover, and the script did not fail a check, it
CRASHED on a disabled button with every later check unrun. The seeder resets the
ladder now, on the same principle it already recomputes attributes: **a re-seed
has to return the account to a known state or it is not a fixture, it is a save
file.**

Still true and NOT fixed: the village pool exhausts the same way. Every run
marries a villager and the roster grows, so two checks fail on a well-used
fixture. Re-seed before trusting a run.

## Open, and each is a decision rather than a defect

1. **Orphaned `*_crafting_material` ore balances.** Now that one ore per region
   is canon, existing stocks of `copper_ore_crafting_material`,
   `iron_ore_crafting_material`, `obsidian_ore_crafting_material` and
   `silver_ore_crafting_material` are unspendable - nothing consumes them.
   Sulfur and darksteel players are unaffected. Converting the balances is a
   one-off `UPDATE`; leaving them is defensible too, since the amounts are
   small. A decision, not a fix.
2. **Guild War is ON HOLD**, decided 2026-09-01. `defend`, `attackShard`,
   `takeTurn` and `damageDelta` in `GuildOps.svelte` stay, and they are the four
   remaining `svelte-check` errors - the CI baseline exists to hold them.
   Independently of the hold, **`shardMatch` still polls every 30 seconds for a
   panel behind `display:none`** - a live cost on every guild member's client,
   worth killing either way. Resolving the hold lets CI gate at zero.
3. **The Logistics section is inside the hidden Raid panel**, so the "To chain"
   button in the visible Depot feeds a production bar players cannot see.
4. **Receiving conversations is untested.** Both directions share one code path
   and the schema records sender and recipient separately, but every message
   driven so far was outgoing. A two-browser check would confirm the unread
   badge and incoming rendering.
5. **"Craft instantly" was deliberately not done.** The x10 button was the fix
   for what felt broken; cutting `CraftingTimeMs` is a pacing decision worth
   feeling first. Tool craft times are 5s/10s/15s/20s/25s by tier.
6. **The village ceiling is now a real lever for the first time.**
   `GetMaxBuildingLevelCeiling` is `2 + townHallLevel * 2` with the Town Hall
   capped at 5, so 12 is the maximum for anything else. Nobody has ever felt
   that curve, because nobody could build. Worth watching before tuning.
7. **Git LFS**: 1,025 MB of source PNGs nothing at runtime uses, on a PUBLIC
   repo, so every clone by anyone bills the owner. The sprites the game serves
   are `.webp` and are NOT in LFS. Deploys do not fetch LFS at all - git-lfs is
   not installed on the box, so the live `.wav` files are pointer stubs.

## Security

**The Supabase Data API has been turned OFF.** The advisor flagged RLS disabled
on all 61 tables, which exposed every row - `PlayerRecords`,
`password_reset_tokens`, every gold and item table - to anyone with the anon
key, a value that is public by design. That would have bypassed the entire
`Serializable` + `FOR UPDATE` double-spend model by writing to the tables
directly. This server talks to Postgres through Npgsql and never uses PostgREST,
so switching the door off was the right fix and nothing in the game changed.
RLS itself is still disabled; if the Data API is ever switched back on, enabling
RLS is a prerequisite, not an option.

**`svelte-check`'s baseline is 4, and the count is a weak guard.** It sat at 9
for a long time and silently turned over into an entirely different 9. Read the
error list, not the total.

---

# HANDOFF 2026-08-08 - The Long Game

The design for all of it is `docs/architecture/LONG_GAME_SPEC.md`. It is
agreed with the player and should not be re-litigated; read it before touching
anything below. Section 6 of that spec tracks status and is kept current.

**Shipped 2026-08-08:** the achievement toast, Book of Deeds chapter I, all
three rings of the skill tree with **all 15 node effects wired**, the respec
gate, the season reset that was missing, breeding aptitudes end to end, and the
village gene pool (table, arrival, endpoint, panel).

**Shipped 2026-08-09: L3, the Book of Deeds.** All five chapters, every deed
with a live x / y, and the Seals - **+2 permanent skill points per Seal, every
season, forever**, which is the coupling the whole document turns on. The
chapters moved to the SERVER (`DeedRegistry`, `DeedProgressSource`,
`SealEngine`, `/api/v1/deeds/snapshot`): a Seal pays permanent skill points, so
a client that decided when it had earned one could award itself the tree.
`chapterOne.ts` is deleted rather than kept - a second copy of the content is a
second copy that drifts.

Three deeds substitute for spec entries this game does not count (which
materials a craft consumed, a narrow escape below 10% HP, cooking totals), and
each substitution is named in `DeedRegistry`'s own comment. Two new lifetime
counters (fusions, rerolls) and `BestSeasonRank`, written where the rollover
already ranks the roster.

**Shipped 2026-08-09: L2, the Hall of Ancestors.** The 10-to-14 cap, the
diamond slot purchase, the keep marks, the rollover cull, and an Ancestors
screen showing the pedigree and who the rollover would let go. **And the door
that never existed: nothing in this server had ever written a
`CharacterRecord.SlotIndex` after creation**, so every child bred past the
third slot was permanently unplayable and "begin the next season with your best
child" could not be performed. `AssignCharacterSlot` (75) swaps.

Three defects found on the way, all fixed: the aptitudes migration backfilled
**0** into every lineage row that predated it while a fresh character starts at
4 (every founder on every account older than 2026-08-08 was four points down on
the one axis that never resets); the dev fixture's repair path assumed slot 0
always held the account's own character, which stopped being true the moment
slots could move, and re-seeding threw on the primary key; and a breeding test
ignored the 5% epic bonus, so it failed one run in twenty.

**Shipped 2026-08-09: L4, the village roster.** `RecruitVillager` (70) and
`DismissNewcomer` (71), a `VillagerRecruitmentsThisSeason` counter that prices
the escalation, and both buttons on the Village screen. **And the rollover wipe
that `VillageNewcomer` documented in capitals and nobody had written** - the
roster, the arrival clock and the recruitment price now reset with the season,
so this season's Inn decides this season's gene pool. `character_lineage_registry`
is still untouched by the rollover, and is now asserted rather than left to
chance.

**Shipped 2026-08-09: L1, hero x villager pairing - the gene pool is live.**
`ExecuteHeroVillagerBreedingAsync`, `CommandType.ExecuteVillagerBreeding` (69),
`/api/v1/breeding/village-preview`, and a two-tab Breeding screen. Driven end
to end in a browser by `client_web/scripts/exercise.mjs` (58/58), which marries
a villager, reads the new child back off the roster and checks the villager is
spent. Detail and the two defects it uncovered are in `LONG_GAME_SPEC.md`
section 6 item 5.

Everything below is what remains, in the order I would do it.

---

## L5. The respec purchase flow - DEFERRED TO THE MOBILE LAUNCH

The gate works: one free respec a season, then a `PaidRespecGrants` grant.
**Nothing increments that counter** - the purchase flow does not exist, and as
of 2026-08-09 it is deliberately not being built.

**Decided with the player: no payments on web. Nothing ships until the Google
Play launch.** That is the right call for a reason worth writing down: on Play
and the App Store, **Google and Apple are the merchant of record** for digital
goods in the EU - they collect and remit the buyer's-country VAT themselves.
Stripe on web makes the seller the merchant, which is where OSS registration and
quarterly filing come from. Waiting does not just postpone the work, it removes
most of the tax burden from it.

The client already has Capacitor purchase plumbing and `BillingVerificationEngine`
already validates store receipts with a duplicate-transaction guard, so the
remaining work at that point is small.

**Selling GOLD for money was considered and should not be done.** Measured, from
`ProgressionRateTests`: region 1's entire economy is **7,124 gold**, while
region 4 yields **53 million** and region 5 about **24 gold a second**. So a
fixed pack is worth roughly a hundred times more to a beginner than to a
veteran - the smallest pack discussed (10k) is more gold than the whole first
region contains, which deletes the early game for anyone who buys one. Gold is
also **wiped at every rollover**, and it buys market gear and affix rerolls,
which is more direct power than the inheritance percentages diamonds buy.

**Diamonds are already the earn-or-buy currency and that is the F2P-friendly
shape.** They are earned from achievements, the daily login, season placement
and the world boss, and four packs are already priced in
`GameBalanceConfig.json` (`IapProductPrices`). Nothing needs a second paid
currency.

---

## Standing traps for whoever picks this up

- **Nine pre-existing `svelte-check` errors** are unrelated to this work
  (Combat/Forge "used before declaration", Market/Forge index-signature
  variance, one unused variable). Do not treat a count of 9 as a regression.
- **Running the local stack**: `--seed-dev` seeds and EXITS, it needs
  `FOLKIDLE_ALLOW_DEV_SEED=1`, and without
  `FOLKIDLE_WEB_ORIGINS="http://localhost:5173"` every request is blocked by
  CORS and the UI says "Could not reach the server" - which reads as a dead
  server when the server is fine.
- **Stop the running server before `dotnet build`/`dotnet test`** or the build
  fails on a locked `FolkIdle.Server.exe`.
- **Do not edit multi-line C# initialisers with blind string replacement.** A
  Plenty edit this session inserted a field into the middle of a four-term
  `LootLuckPct` sum, silently re-parenting three bonuses onto the wrong field.
  It compiled and all 470 tests passed. There is now a guard test, but the
  lesson generalises.
- **`SkillTreeRegistry.EffectPending` is empty and must stay empty.**
  `SkillNodeEffectTests` asserts zero. If a node loses its effect, restore the
  effect rather than re-adding the id.
- **The dev fixture is what makes a feature drivable by hand, and it silently
  was not.** Breeding needed three things the fixture lacked, each fatal on its
  own and none of which logs anything: the building, a
  `character_lineage_registry` row per character, and sexes. If a screen on the
  fixture looks empty, suspect the fixture before the screen -
  `DevFixtureInvariantTests` is where that suspicion gets written down.
- **`isDisabled()` does not work on an `<option>`.** Playwright's editability
  check is defined for inputs and selects and answers "not disabled" whatever
  an option's attribute says, so an exercise step happily picked a choice the
  screen had greyed out and then reported the feature broken. Read `o.disabled`
  through `evaluate` instead.

---

# HANDOFF - 2026-08-06

Everything below `## Client UI Hook Points` predates the web client.

**Where things stand.** The game is live at https://folkidle.duckdns.org, open
to anyone with the link, and the server suite is green at 341/341 - which it
was NOT at the start of this session, and nobody had noticed. Combat, gathering
and the larder were rebalanced against each other in one pass and every number
in that pass is measured by a test that prints it, not authored by hand.

## A region boss is an EVENT the first time and a chore afterwards

Reported from play: "I killed the first boss almost without fish, wearing only
Field Mouse and Horned Rabbit drops at about rarity 2."

Simply enlarging the boss does not answer that. A boss sized to demand a full
set of high-rarity gear is then a wall every time a player wants the thing it
drops, and farming a wall is a tax, not a fight. So the two cases were split:

**Until a player puts a boss down once it carries 5x the health and 2x the
attack.** After that it reverts to its authored stats and can be farmed.
`BossFirstClearRules` owns the rule; the four fight-start sites and the two
incoming-damage sites all route through it, live AND offline.

| region | boss | farmed | first clear |
|---|---|---|---|
| 1 | Alpha Wolf | 5,850 HP / 130 atk | 29,250 / 260 |
| 2 | Shadow Lynx | 14,200 / 1,625 | 71,000 / 3,250 |
| 3 | Magma Wyrm | 34,600 / 6,800 | 173,000 / 13,600 |
| 4 | Frost Titan | 84,300 / 28,400 | 421,500 / 56,800 |
| 5 | Malakor | 205,100 / 118,400 | 1,025,500 / 236,800 |

**The state is the state that already unlocks regions** - a boss is beaten or
it is not, per the monster codex - cached onto the payload as a five-bit mask.
`TickStatePayload` is NOT the wire packet, so this cost nothing on the network
and needed no protocol regeneration.

**`HighestUnlockedRegion` cannot stand in for the mask.** Clearing region 5's
boss opens no sixth region, so that number stays at 5 before and after: the
last boss in the game would read as never beaten and stay at first-clear stats
forever, unfarmable by the only players who can reach it. The mask is also set
OUTSIDE the `< LastRegion` guard that raises the unlock, for the same reason.

## The whole ladder was raised threefold, and region 1's ATTACK was not

Asked for directly: "3x HP and damage on everything, bosses especially". HP is
tripled everywhere. Attack is tripled from region 2 onward.

**Region 1's attack exemption is a hard constraint, not a preference.** The
first fight of a new account happens with nothing equipped. At 3x, Field Mouse
takes 25 off a 100-point bar every 1.5 seconds while one bite of food every 2.5
seconds returns 12 - so a new player dies before landing a single kill.
`TheFirstMonsterTakesAboutSeventyFiveSeconds` measured it as 300 seconds a
kill, which was the simulation reporting that the game had no entrance.

The step out of region 1 therefore reads 5.75x on attack against 1.9x at the
later borders. That is the right direction - by then the player is wearing what
region 1 dropped.

**Boss base multipliers went BACK to 5x/3.25x** from the 8x/5x tried first: the
boss-specific steepening now lives in the first clear, and stacking both made a
capstone a fifteen-minute slog rather than a hard fight.

**`EquipmentDropChance` 5% -> 15%, and this is a change nobody asked for on its
own.** A drop is rolled PER KILL and kills now take three times as long, so
leaving it would have quietly undone the drop-rate decision made two changes
earlier. Drops per HOUR are held where they were. If a slower drip is wanted,
that is the one line to change - the difficulty raise does not depend on it.

Opening kill: ~20s -> ~75s. Worst regular on arrival: 166s (Wild Boar, fought
naked - those four monsters ARE the gearing-up). Gathering share rose again
with the damage, to 67-78%; see the open item below.

## The monster ladder used to go DOWNHILL at every region border

Reported from play as "monsters at the start of a tier have too little HP and
attack - the player does not start from zero there", and it was worse than it
looked. Measured, the old ladder read:

```
104  Sandstone Golem   1325 HP    520 atk
105  Magma Wyrm        6625 HP   1690 atk  (BOSS)
106  Ice Bat           1690 HP    152 atk  <-- 0.09x the attack before it
...
114  Death Knight      8760 HP   1000 atk
111  Grave Ghoul       3440 HP    200 atk  <-- a fifth of it, one region LATER
```

**The cause was structural, not a typo.** Monsters were sized as a percentage
of the health pool expected in THEIR region - 8% of it for the first, 40% for
the fourth. Inside a region that is a clean fivefold ramp. Across a border the
percentage resets to 8% while the pool grows only about twice, so the product
is 2 x 0.2 = 0.4: the first monster of a region hit for less than half of what
the last one did. Every border in the game was a step down, and no retuning of
the percentages fixes a shape that multiplies a reset by a smaller growth.

**Difficulty is now one continuous curve across all twenty regulars.** Region 1
is authored by hand and unchanged - a starting character's own power multiplies
several times inside it, which no later region repeats, and that is why its
8-to-40 interior is fair and why nothing else copies it. Regions 2-5 step +15%
HP and +30% attack per monster, so every monster is a gear check rather than
the fourth one alone, and each border steps 1.6x HP and 1.9x attack. Bosses
stay 5x HP and 3.25x attack over their own region's fourth monster.

`MonsterLadderTests` pins the SHAPE, not the numbers: every regular beats the
one before it, every border out-steps any step inside a region, every boss tops
its own region and the boss before it, and rewards still track HP exactly
(XP = HP/5, gold = HP/20). Retune the steps freely; the ladder may not descend.

**A boss is deliberately above the next region's first monster.** The first
version of the check compared them and reported the design as a break. Bosses
are a separate ladder, checked against their own region and the previous boss.

## The player model in two tests wore armour affixes and no weapon affixes

Both balance models dressed the player inconsistently: armour was modelled with
affixes (correctly - they carry the spread between loadouts now), damage was
modelled as `15 + weapon` with none. So a player who rolls their armour was
compared against one who never touches their weapon, kills came out slow, and
the slow kills were then read as time spent fishing.

Two further errors in the same model, both fixed:

- **Affix rarity was pinned at Rare for all five regions.** Rising rarity is
  not optimism, it is the design - every monster is a gear check and clearing
  one pays for the next. A frozen-gear model reports the late game as
  impossible and blames the larder. `RarityForRegion` runs Common to Legendary.
- **The armour rating was used as the size of the health bar.** Two different
  stats off two different affix curves, and the chosen one does not feed the
  bar at all. It matters because a bite heals a PERCENTAGE of max HP.

## CLOSED: gathering was most of the playtime, and the tool curve was why

Gathering reached 78% of region 4 against an intent of a fifth. It is back to
36%, 44%, 35%, 27%, 17% across regions 1-5 - and it FALLS as the game goes on,
which is the right shape, because that is where the gathering is heaviest.

**Nothing about the monsters was softened.** The fix was the tool curve, which
ran +10% to +200% speed across the ENTIRE game: the best tool in existence was
three times a bare hand and only 2.7 times the first tool a player ever crafts.
Fishing therefore barely improved across five regions while the reason to fish
grew steeply, and the gap became the game.

**The curve is geometric now, 1.35x a tier** - Void Bark is +1912%, twenty
times a bare hand. Every gear band is two tiers, so the within-region upgrade
is worth about 1.35x whichever band a player is in: a steady reason to go back
to the forge rather than a payoff that only exists at the end of the game.

### Two defects found while wiring it

- **`gather_speed_pct` affixes did nothing at all.** `StateCheckpointManager`
  computed the figure off the equipped tools, stored it on the payload as
  `ToolGatherSpeedPct` and shipped it to the client - and no code read it back.
  Same shape as the five "output side never wired" defects found before it: the
  input half complete, convincing, and connected to nothing.
- **Offline gathering kept a private copy of the speed formula**, and the copy
  predated the fix to the live one: it read `CachedCurrentToolTier`, which is
  the FORGE BUILDING'S level rather than any tool. No matching tool, no
  percentage curve, no village bonus, no affixes. A player who logged out
  mid-fishing came back to a different game than the one they left. Both paths
  call `GatheringToolEngine.ComputeRequiredTicks` now.

### The flip side, recorded

Tools are cheap in TIME now - 12% of region 1 down to 2.6% of region 5 - because
a tool makes gathering its own materials fast. The `toolShare` floor moved from
8% to 2% to record that. What a tool costs in MATERIALS did not change, and
whether it is a real investment is what `Test_Gathering_EveryToolTierPaysBackIts
OwnCost` checks. **If tools should feel like a bigger commitment the dial is the
recipe material cost, not the speed curve** - roughly threefold puts region 3
back near 17% and leaves the overall share inside its band.

## Where it runs

**https://folkidle.duckdns.org** — the address to give people, with
**https://92-5-0-94.sslip.io** still answering beside it so older links keep
working. Both come out of one Caddy block and share one certificate. The built
bundle points at the duckdns name, because `VITE_FOLKIDLE_SERVER` is a build
ARG and pointing it at a name rather than at an address means the day this box
changes IP, only DNS moves. `FOLKIDLE_WEB_ORIGINS` in `.env` must list BOTH,
comma separated - getting it wrong presents as a login screen that simply
refuses, with nothing in the console naming the origin list.

Both names reach the same machine: the whole game, client and API, on the
Oracle Ampere box. **Render is no longer used for anything** and both its
services can be suspended. The database is still Supabase.

    ssh folkidle-server
    cd ~/folkidle/ops/oracle && docker compose up -d --build

See `ops/oracle/README.md`. Secrets live in `.env` there, mode 600, gitignored.

Traps, in the order they will bite:

- **Caddy orders directives by its own rules, not by the file.** `try_files`
  sorts before `reverse_proxy`, so bare matchers made the SPA fallback run
  first and every API call returned 200 with the client's own HTML - a game
  that loads, looks alive, and knows about no items or monsters, with a green
  health check. Use `handle` blocks. `caddy validate` called the broken version
  valid; only a dry run against a throwaway database caught it.
- **`VITE_FOLKIDLE_SERVER` is a build ARG.** Vite inlines it, so changing the
  hostname is a rebuild, not a restart.
- **Supabase MCP is read-only.** Run writes with psql from the box.
- **Supabase pooler port 5432**, never 6543 - the latter hangs EF migrations.
- **The item catalogue has HOLES.** Ids are positional (`_itemBaseIds[id - 1]`)
  and 111 entries were removed, so never renumber and never assume 1..N.

## What shipped

- **Self-hosting**, one origin for client and API. The WebSocket connects to
  `/` where `index.html` also lives, so the upgrade is matched by its headers
  before any path routing.
- **Per-monster drop tables.** Each location's gear is dealt across its five
  monsters; nothing is orphaned and no monster drops only weapons.
- **There is no offhand slot.** It was invented, along with five items to fill
  it. Amulet and Ring were the genuinely missing pair - one of each per tier
  had been in the catalogue all along resolving to no slot.
- **Catalogue cut 437 -> 326**, leaving exactly the 75 canonical pieces.
- **One gold reroll** rolling type, rarity and magnitude together. The diamond
  path was broken anyway and auto-reroll could not start.
- **Market**: type checkboxes, tier filter, price history, payout breakdown.
- **Combat freeze fixed** (the send lock), **gold made durable without Redis**,
  **three damage models unified**.

## The balance, as of the season curve

**The 13-hour figure below is history.** The level curve became
`250 * 1.13^level` (was `400 * 1.06^level`), which is a season-length game
rather than a weekend one. Each region now costs roughly four times the one
before it - 12.1x XP growth against 3x gear growth - and the base was lowered
so the opening stays brisk:

    region 1  2.5 h | 2  10.9 | 3  47.6 | 4  197 | 5  784   (1042 h, no affixes)
    with gear 0.8 h | 2   3.6 | 3  15.9 | 4   66 | 5  261   ( 347 h, ~3x gear)

At 200 active hours a season, region 4 falls in the first season and region 5
in the second. Monsters and their rewards were not touched, so XP = HP/5 still
holds and `ProgressionRateTests` still prints the real curve.

**These are intent, not measurement of players.** Once a few people have played
a month, compare where they actually are against what that test prints - the
model is worth checking against reality rather than re-estimating.

## What a season leaves behind

Three things now survive a rollover: **the village you built, the race mastery
you learned, and the inheritance levels you bought**. Everything else still
goes - levels, gear, gold, materials, the market, the chronicle pass - because
the season is the ladder and the ladder is the game.

Inheritance is six permanent bonuses (damage, health, XP, gold, gathering
yield, loot luck), 20 levels of 2%, 40 diamonds on a x1.28 curve. It exists as
much to give diamonds a sink - they had nine producers and one real use - as to
make a rollover a step rather than a loss.
`Test_SeasonalRotation_KeepsTheVillageTheMasteryAndTheInheritance` pins both
halves of the rule, which is otherwise observable only once every ninety days.

## One store, one number

`VillageStashInstances` folded into `CommodityRecords`. The split was never a
feature - every spend already drew from the sum - and it produced the same bug
three times, each found separately: the larder, the boosts panel and the guild
deposit each filtered on one half. The API returns a single `Quantity`.

`BankEquipmentInstances` **is now merged too, and then dropped** - see the
`RetireTheBank` migration. It was a hundred-slot store that existed to relieve a
backpack cap the game no longer has, and it did active harm: `EquipmentSlotEngine`,
forge fusion, affix reroll and every market listing read `EquipmentInstances`, so
an item in the Bank could not be worn, upgraded, rerolled or sold. Depositing
was a way to make your own gear inert and nothing said so. Its rows were moved
across rather than dropped with the table, and commands 12/13 are ignored rather
than routed.

## The suite was red, and the red was not being read

Ten of 337 server tests were failing when this session started, none of them
noticed. The "155/155" in the last few commit messages is a FILTERED SUBSET,
not the suite - run `dotnet test` on the test project with no filter.

One of the ten was a live hole rather than a stale expectation:

**The market's price corridor failed OPEN for an unpriceable item.** It ran
only when a rolling average or a catalogue baseline could be computed, and
skipped silently otherwise - which was harmless until the catalogue was cut
from 437 entries to its 75 canonical pieces. Every instance of a removed item
still sits in `EquipmentInstances`, has no baseline, has never traded, and
could therefore be listed at ANY price: the exact gold-laundering route the
corridor exists to close, opened by a content change. Both the direct SELL path
(`MarketEscrowEngine`) and the BUY order path (`MarketOrderBookEngine`) now
reject an item they cannot price.

`RegionUnlockGate.CanWearItem` fails open on the same input and deliberately
stays that way - wearing an obsolete item you already own is a small power
gain, pricing one freely moves gold between accounts.

The other nine, briefly: the level curve moved to 1.13 and two tests still
asserted 1.06 and a 45-260 minute band per region; the packet ceiling was
crossed twice without either pass noticing this assertion; the stash fold moved
where deposits land; the catalogue cut invalidated three hard-coded item ids;
the offline projection kept a private copy of a damage model that has since
been unified; and `python3` on Windows is an App Execution Alias that exits
zero while printing "Python was not found".

Three of those are one lesson: **a test that re-derives what the engine
computes will drift, and it drifts silently.** Every one of them is now asked
of the authority - `ContentRegistry.TryGetRecipe`, `CombatDamageModel`,
`ContentRegistry.GetMonsterRegionTier` - instead of spelling the answer out.
(One of them pointed at `CraftingReceptuary` for a few hours; that whole system
was removed later the same session, which rather makes the point.)

**And behind the `python3` alias, a second real defect.** Once the test could
actually run `ops/validate_content.py`, the script rejected the live catalogue
on every one of the 111 ids above the entry count: it still required item ids
to be a contiguous 1..N, a rule `ContentRegistry` dropped when it began sizing
its arrays by the HIGHEST id so that removing an item leaves an inert hole
rather than repointing every loot table and owned row. The pre-build validator
CI depends on would have failed the build on correct content - invisible for as
long as the interpreter probe was wrong. Item ids are positive and unique now;
gaps are legal and documented as such in the script's own header.

## Monsters, rebalanced twice in one pass - 2026-08-06

**Attack is derived from armour now.** The strongest regular of each region
hits for roughly what a fully geared player of that region is wearing, the
other three scale below it, and a boss is 1.5x its region's strongest. The
cliff described below is closed: net damage is a trickle in every region
instead of nothing through region 3 and a full health bar afterwards.

**AND HP IS SIZED BY HOW LONG A FIGHT SHOULD LAST.** This is the change that
matters more, and it came from a question worth repeating: equipment only drops
from monsters, so a kill IS a loot roll. At the old table a region-5 regular
took thirteen minutes and Malakor seventy-six - at a 2% equipment chance that
is one piece of gear every three to eleven HOURS, at the point in the game
where a player most needs gear, and a fight nobody would watch. The drop rate
was never the problem; the kill rate was.

    region        1      2      3      4      5
    weakest      80    110    335   1985   4575   .. hit points
    strongest   190    265    800   4760  10975
    boss        950   1325   4000  23800  54875

Measured by `ProgressionRateTests` driving the real tick with tier-appropriate
gear: **11.7 / 6.2 / 7.0 / 15.0 / 11.9 seconds a kill**. On arrival in a region,
carrying the previous region's weapon, the same monsters run 25-60 seconds and
get faster as the region equips you. Equipment now drops about every eight
minutes rather than every several hours.

THREE RULES THAT ARE EASY TO BREAK BY ACCIDENT, all of them load-bearing:

1. **Sizing monsters is not balancing them.** XP is MaxHp/5 and gold MaxHp/20,
   so hit points are the size of the bite and nothing else. The season stays
   the length it is - region 4 in the first season, region 5 in the second or
   third - no matter what this table says. `ProgressionRateTests` prints both
   halves; check the region hours, not the kill times, when asking whether
   pacing moved.
2. **Do not buy the gathering share by making gathering slower.** A node is
   3-10 seconds a unit and with a tier-appropriate tool 2.6-3.6 - under the
   ten-second bar a kill is held to. The share comes from how MUCH a tool
   costs, never from how long a swing takes.
3. **Region 1's attack cannot be derived from armour.** Every other region's
   arriving player wears the previous region's gear; region 1's wears nothing.
   Priced against region 1's authored armour (40) a hit takes two fifths of a
   bare 100 HP bar, and a normal hit followed by a crit kills before auto-eat's
   50% threshold fires twice. `ProjectedKillRateMatchesTheLiveOne` caught it by
   simulating a real character and getting zero kills in seventeen minutes.

**This cost nothing in pacing, and that is not a coincidence.** Rewards are a
flat function of health across the whole file - XP is MaxHp/5, gold MaxHp/20 -
so cutting a monster's health cuts what it pays and multiplies how many are
killed per hour by the same factor. XP per hour is identical. Health is purely
the size of the bite, and it had been authored as though it were difficulty.

`Test_Content_EveryMonsterDiesInsideTheAttentionSpan` pins the band.
`Test_Content_RegionBossesAreContinuousWithTheirRegionCurve` now compares
regions in SECONDS rather than in hit points, because a region-5 monster has
fewer hit points than a region-1 one used to and is far harder - the player's
weapon grew 80x in between.

## The health pool, finally measured - and the pair it decided

Every attempt to size the larder had stood in a guess for one number: what a
player's health bar actually is. `HowLongARegionTakes` prints it now, because
it already builds the tier-appropriate character the question is about.

**The bar is 100 HP at level 1 and about 2,500 by region 5.** It grows through
CON, which `RaceAttributeGrowth` adds at roughly two points a level and
`StatsCalculator` pays at 15 HP each. Nothing else moves it except `flat_hp`
affixes. Note the first reading of that print said 100 HP in EVERY region -
that was the fixture setting `CurrentLevel` directly instead of levelling up,
not the game. A number that surprising is worth re-deriving before acting on.

With the pool known, the arithmetic stops being a matter of taste:

    one fish restores (tier x 12%) of the bar
    for gathering to stay near a fifth of playtime,
    one fish must cover ~35 seconds of combat
    => a normal hit may take about 1.6% of the bar

So **attack and the heal move together or not at all.** Raising attack above
armour - which is what stops damage being entirely crit-driven, and it was,
because a hit equal to the armour it faces lands on the 1 HP floor unless it
crits - spends food one for one. Both moved:

- monster attack is `region armour + 1.6% of that region's health pool`, so an
  ordinary swing lands for a real amount in every region rather than nothing
  followed by everything
- `FoodRegistry.HealPercentOfMaxHpPerTier` is 12, up from 5. Not 20, which
  would let a tier-5 fish restore the whole bar and make every deeper tier
  worthless - the wrong shape for a profession the player should keep
  investing in.

Measured result: gathering is **33-39% of playtime, flat across regions 2-5**.
The cliff is gone; the share is at the top of the one-fifth-to-one-third band
rather than the middle, and the honest reason to leave it there is that the
health pool model still ignores `flat_hp` affixes, which can only make the bar
bigger and the share smaller.

**Armour subtracted rather than reduced - SINCE FIXED, see "Armour reduces
now" above.** Kept because the numbers in this section were all tuned under
subtraction and only make sense against it: monster attack had to out-scale the
armour table, so region 5's strongest hit for 3,290 where it now hits for 80.
If a figure here disagrees with one above, the one above is current.

**Known flake, do not chase it as a regression.**
`Test_BreedingPair_GrantedRacePairCanBreedAndSameSexIsRefused` failed once in a
full-suite run (child race 27 instead of Kobold), passed in isolation, and
passed on an immediate re-run of the whole suite. Same shape as the market
escrow concurrency test in item 16: order-dependent contention on the shared
Postgres fixture, not a defect in breeding. If it starts failing REPEATEDLY,
that is a different finding and worth the dig.

## The cliff this replaced, kept for the reasoning

Found while sizing the gathering economy, and much more important than what it
was found looking for. The strongest REGULAR monster of each region, against
the best armour authored for that region:

    region        1      2      3       4       5
    attack       32     96    330    1200    4800
    armour       40    120    360    1080    3240
    net hit    floor  floor  floor     120    1560

Through region 3 armour exceeds attack, every hit lands on the 1 HP floor, and
the larder is almost decorative. From region 4 it does not, and incoming damage
goes from nothing to more than a player's whole health pool in a handful of
swings. This is not a curve with a steep end; it is a cliff with nothing before
it.

It surfaces as a food problem - fishing was 756% of region 5's playtime, and is
89% after food was made a share of max HP - but food is the symptom. No larder
can answer a hit that takes a large fraction of the bar, and a fish that could
would make food decorative again from the other side.

Closing it means changing one of three things, and it is a design decision:
monster attack power, the authored armour curve, or how max HP scales.
`Test_Gathering_ShareOfPlaytimeStaysInBand` prints the measured share every run
and holds regions 4-5 at "no worse than today" - a ratchet, not an
endorsement. It fails the moment someone improves it, which is when the comment
in it should go.

**Gathering itself is now sized.** Every tool recipe cost a flat 8 + 4 units
regardless of tier, so the entire ten-tier ladder was 360 units for the whole
game. Costs now ramp with the tier, sized from the pacing model, and wood and
ore land at 11-15% of each region. Equipment is monster loot and tools are
crafted: `CraftingReceptuary` and `ExecuteEquipmentCraftingAsync` - a second
crafting system that turned ore into armour - are gone, and
`Test_Crafting_ProducesToolsAndNothingWearable` keeps them gone.

Two fidelity defects fell out of the same measurement, both the familiar shape
of "the live tick learned something and the projection beside it did not":
offline healed a flat 50 HP per food unit while the live tick had moved to
FoodRegistry's 40-to-82,000 scale, and the warp path fed gathering the FORGE
BUILDING'S LEVEL instead of the equipped tool - so every hour spent offline
threw away the entire reason to craft tools. A larder stocked with
gold_ore_crafting_material healed for years in three test fixtures, because a
flat constant never asks what is in the slot.

## The older measurement, kept for the reasoning

**Gathering is now a rounding error.** Measured, per region:

    region        1      2       3        4         5
    gathering   7.6   11.4    15.2     20.3      25.3   minutes
    combat       94    572    2036    11417     45186   minutes
    share      7.5%   2.0%   0.74%    0.18%     0.06%

Gathering does scale - 3.3x across the five regions - but combat scales 480x.
Nothing about gathering changed; the thing beside it got much larger, because
the season curve multiplies XP per region ~12x while a full loadout stays a
flat 38 bars and the node threshold only moves 30 -> 100 ticks.

In region 1 that is one minute in thirteen. In region 5 it is one minute in
eighteen hundred: gather for twenty minutes, craft the tier, then fight for a
month. The 104-recipe tree stops being a system a player meets and becomes a
formality at the start of a region.

The band test's old "gathering is at least 2% of a region" floor could not
survive that and is a ceiling only now - a test can say "gathering must not
become the bottleneck", but it cannot decide whether the right share is 7% or
0.06%. Three ways out, in the order they are worth considering:

1. Leave it. Gear also drops per-monster and the market exists, so crafting is
   an onboarding system and the late game runs on drops and trade.
2. Scale recipe costs with the region (~4x each, to hold the share). Holds the
   ratio and turns region 5 into ~27 hours of gathering - one grind traded for
   two.
3. Point gathering somewhere else late: materials into rerolls, the forge and
   consumables rather than into base gear, so the tree stays alive without
   gating progression.

Not yet, though. The curve itself is a model rather than a measurement (see
above), and tuning recipe costs to fit an unvalidated model stacks one guess
on another.

## Traps found the hard way, 2026-08-05 (late)

- **A migration without a `.Designer.cs` never runs.** EF discovers migrations
  by scanning for `[Migration]` and then filtering on `[DbContext]`; both are
  generated into that file. `FoldStashIntoCommodities` was hand-written without
  it, so it shipped, deployed, and moved nothing, while the server logged
  "Database migrations applied successfully". Caught by reading the live
  `__EFMigrationsHistory` and noticing the row was absent.
  `Test_Migrations_EveryMigrationTypeIsDiscoverableByEf` now catches it.
- **Two enum names on one value is silent in C#.** `PurchaseInheritanceLevel`
  was given 65, which `StockFoodSlot` already held, so the server would have
  read every larder stocking as a diamond purchase. It surfaced only because
  the protocol generator emits one entry per NAME and produced a duplicate key
  in the client's opcode map. Check the list before adding to it.
- **A crashed check hides every check below it.** `exercise.mjs` called
  `selectOption` on a market filter that had become a checkbox; it threw rather
  than failed, so everything after the market stopped running - and nothing
  said so, because the summary line never printed.

## Armour reduces now - SHIPPED, and it moved everything

`raw * K / (K + armour)` in `CombatDamageModel.Mitigate`, with K the armour
that halves damage, taken from the monster's REGION rather than from the
defender - an armour term derived from the defender's own armour cancels out
and stops being a stat. Best-in-slot takes half, half-geared takes two thirds,
over-geared takes a third.

**Monster attack became a statement about the player.** It had to out-scale the
armour table before, because a hit below the armour it faced did nothing:
region 5's strongest regular hit for 3,290. It is 80 now - 3.2% of the region's
health pool, landing as 1.6% after mitigation - and one rule writes every
region. Incoming damage measures 1.0-1.3% of the bar per second everywhere.

**There were FIVE copies of the subtraction, not four.** The fifth sat in the
live tick's own outgoing damage - the copy that decides what actually happens -
and it kept subtracting after the model stopped. Every projection then claimed
a kill took half as long as it did. `ProjectedKillRateMatchesTheLiveOne` caught
it inside one run, which is the whole argument for that test existing.

**The first swing at a new monster costs a full interval.** The tick zeroes the
swing accumulator on respawn, so the timer restarts and the player waits before
landing anything. The projection modelled continuous swinging and ran fast by
one interval per kill - invisible at minute-long fights, a sixth of a
ten-second one. Now in `ExpectedSecondsPerKill`.

Current shape: 22-55 seconds a kill on arrival, 13-22 with the region's own
gear, gathering 17-32%, regions 2.3 / 11.2 / 57.3 / 258 / 1120 hours. Longer
than the subtraction model gave, because 25% mitigation on the monster side is
more than the 5% flat subtraction was worth at region 5.

**Method note worth more than the numbers:** the first hit-point pass scaled
from a table measured BEFORE the live tick was corrected, and overshot
fourfold. Measure, change, measure again - never measure, change, then apply
the first measurement.

## Security, audited 2026-08-06

Triggered by Chrome warning on the registration page. **That warning was about
password REUSE** - Chrome recognised a password saved for another site being
typed here - not a Safe Browsing flag on the domain. A deceptive-site flag
shows a full red interstitial before the page loads, not a dialog after typing.
`duckdns.org` is a shared suffix popular with phishers, so the reputation is
inherited and there is nothing to fix in the code for it.

Measured, not assumed. What was actually wrong:

- **No rate limit on authentication.** Eight wrong passwords in a row returned
  eight plain 401s. Unlimited guessing, and - since every attempt runs PBKDF2
  at 210,000 iterations - about a tenth of a second of server CPU per guess,
  which is a denial of service from a laptop. `AuthThrottle` now gives each
  address fifteen requests a minute across the four auth endpoints. It counts
  REQUESTS not failures (failures leave a valid-looking flood unbounded) and
  reads X-Forwarded-For, because behind Caddy every request arrives from the
  Docker gateway and keying on that would let one attacker lock out everyone.
- **A default admin password in a public repository.** `ADMIN_SECRET_KEY ??
  "supersecretadmin123"`, with the variable unset on the box. Unreachable by
  luck: the Caddyfile's api matcher does not list `/admin/*`, so the static
  file server answers it. That is an accident of a path list, not a decision.
  Unset means closed now, and the compare is constant-time.
- **No security headers at all.** HSTS, nosniff, frame-ancestors,
  Referrer-Policy, Permissions-Policy, and the `Server` banner removed.

What is sound, checked rather than assumed: PBKDF2-HMAC-SHA256 at 210k with a
random salt and a constant-time compare; a hand-rolled JWT that verifies the
signature BEFORE parsing the payload and always computes HMAC with the server
key, so the `alg` header is never trusted; parameterised SQL everywhere with no
string-built queries; no `@html` anywhere in the client; `.env` gitignored and
never committed; CORS restricted to the two real origins.

**No Content-Security-Policy beyond frame-ancestors, deliberately.** A CSP
written without the built bundle in front of you breaks the page in ways that
look like random bugs. It wants its own pass.

## Open

**THE MOCK OAUTH VALIDATOR WAS REGISTERED IN PRODUCTION - fixed 2026-08-09.**
`MockOAuthTokenValidator` accepts `mock:Google:{id}` with no cryptography, its
own comment says never to register it outside local development, and
`Program.cs` registered it unconditionally. `/api/v1/auth/login` is
unauthenticated by design and accepts an `oauthProviderToken`. Not yet
exploitable, and only by luck - nothing in the client can link an OAuth
identity, so the lookup finds no row - but that luck ends the day OAuth ships,
and a Google `sub` is not a secret. Production now gets
`DisabledOAuthTokenValidator`, which refuses everything. **Writing a real
validator (Google tokeninfo / Apple JWKS) is a prerequisite for the mobile
launch**, not an optional extra.

**~~NO PASSWORD RESET~~ - BUILT 2026-08-09.** `/api/v1/auth/request-password-reset`
and `/api/v1/auth/reset-password`, a `password_reset_tokens` table, and an
`IEmailSender` with Resend as the concrete provider (chosen for having no SDK -
one POST with a bearer key). **Configure `FOLKIDLE_RESEND_API_KEY` and
`FOLKIDLE_MAIL_FROM` before the next deploy**: without them production gets
`DisabledEmailSender` and the flow refuses rather than pretending, while
development gets `ConsoleEmailSender`, which prints the link and is what makes
the whole thing drivable with no provider account.

The properties, all tested: a request answers 200 for every outcome (an unknown
address must not be distinguishable - that is the oracle `check-email` was
deleted for), the token is 32 CSPRNG bytes stored only as a SHA-256 hash,
single-use, one hour, a new request supersedes the old link, and a successful
reset also clears `DeviceId` because that field signs somebody in with NO
password at all.

**KNOWN LIMIT:** a reset does not end sessions already signed in. This server
issues self-contained 24-hour JWTs and has no revocation list -
`PlayerRecord.AuthenticatorToken` exists and is read by nothing - so an attacker
who had the old password keeps their session until it expires. Closing that
means a revocation check on every authenticated request, which is its own piece
of work.

**EMAIL VERIFICATION IS STILL NOT BUILT**, and is now the cheaper half: the
sender, the token table and the throttle all exist, so it is one more token
kind and one more endpoint.

**~~Two auth decisions left~~ - BOTH DONE 2026-08-09.** The password minimum is
**eight**, length only and no composition rule (see `PasswordPolicy` for why
NIST argues against required symbols), enforced at registration ONLY so nobody
holding an old six-character password is locked out. And
`/api/v1/auth/check-email` is **deleted**, along with `IsEmailAvailableAsync`
and the client's `isEmailAvailable` - it answered "does this address have an
account here" unauthenticated and unthrottled, and no screen ever called it.
Registration still refuses a duplicate, which is where a real player finds out.

**~~The health pool ignores `flat_hp` affixes~~ - DONE, and the reading moved
a long way.** Both models carry gear HP now, and `GatheringShareTests` was also
sizing every fish against a hardcoded **level-1** bar while modelling region 5.

    region 1  100 hp bare, 175 geared     gathering 37% of playtime
    region 2  700 hp bare, 960 geared               37%
    region 3  1,300 bare, 2,225 geared              29%
    region 4  1,900 bare, 5,170 geared              23%
    region 5  2,500 bare, 14,010 geared             15%

Two things fall out and neither was visible before. Gear **quintuples** the
region-5 bar, which turns the strongest regular monster there from 583% of the
bar per second (lethal in a fifth of a second, i.e. region 5 unplayable) into
104% - dangerous, which is the intent. And the gathering share is no longer
flat: it runs 37% early and 15% late, so the previously recorded "33-39% flat
across regions 2-5" was an artefact of the level-1 bar.

**The share now leaves the one-fifth-to-one-third band at BOTH ends** and that
is a balance decision, deliberately not taken here: the guard in
`GatheringShareTests` is 15-80% and region 5 sits on its floor. If it is worth
flattening, the levers are `FoodRegistry.HealPercentOfMaxHpPerTier` and the
monster attack formula, and they move together - see the section below.

**Nothing is measured against real players.** Every figure here comes from a
model driving the real tick, which is a much better thing than an estimate and
still not the same as a person playing for a month. `ProgressionRateTests` and
`GatheringShareTests` both print their tables; compare them to reality once
there is reality to compare to.

**H3. Sprite coverage.** Now measurable against a clean catalogue: 326 items,
and the asset list names what should exist. `scripts/generate-sprites.mjs`
holds the alias table where a wrong mapping would live.

**THE TWO LOUDEST MOMENTS WENT UNMARKED - fixed 2026-08-09.** A first boss
clear carries 5x health and 2x attack, the hardest fight in the game, and
produced no acknowledgement of any kind: a player learned they had unlocked a
race by noticing a new option on another screen. A death showed a small badge
reading "Died" and could not even name the killer, because the respawn clears
`CurrentMonsterId` before any broadcast runs.

Both are now cards, carried on the packet like the offline summary (769 -> 799
bytes) and edge-triggered off a tick byte so they show exactly once. The victory
card names the fight length, gold and xp, and derives the UNLOCKS client-side
from the boss id - the race, the region that opened and the five monsters in it.
The death card names the killer and diagnoses the cause, pointing at the larder
when it is empty.

**And the dev fixture had every attribute at zero.** Setting `CurrentLevel`
directly leaves STR/DEX/CON/LCK at 0 - the trap `ProgressionRateTests` already
documents - so the level-40 fixture had a **148 HP** health bar against the
~3,100 a real one carries, and died to a first-clear boss a genuine player of
that level beats. It now grows its attributes the way the game does.

**THE CHRONO BANK HAD NO SOURCE AT ALL - fixed 2026-08-09.** Its only producer
was `ChronoCoreEngine`, which spends a "chrono core" item that **is not in
items.json** - the engine's own comment already admitted it could never fire. So
`BankedChronoSeconds` was zero for every player who has ever played, both of its
buttons sat permanently disabled, and the Boosts screen claimed it "fills on its
own" and that "login rewards and the season pass" grant it. Three false
statements on one panel.

Now fed by three rewards (`ChronoGrantRules`): a seventh login-streak day (1h),
each Book of Deeds chapter (4h), and each first boss clear (2h). A full season
pays **43 hours against the 168-hour cap**, so late rewards still land.

**And acceleration could not be turned off where it was turned on.**
`ActivateChronoAcceleration` ignored any multiplier that was not 2 or 4, and the
validator treated one as tampering - so asking to stop **disconnected the
player**. The only working off-switch was on the Store screen. 1x now means stop,
is allowed even with an empty bank (which is when it is most needed), and the
Boosts screen has the button.

**`BankOverflowSeconds` is an empty method** - offline time past the 12-hour cap
is discarded, not banked, which is the third thing that panel was wrong about.
Left as-is deliberately: offline pays in full up to the cap, and banking the
overflow would make the bank a second, hidden copy of offline progress.

**MONSTERS DROPPED "ALMOST THE WHOLE SET PLUS BOOTS" - fixed 2026-08-09.**
Reported from reading the tables and it was real: the deal alternated between a
slot's two candidates by INDEX, and the index came from items.json's own order,
which is not consistent per set. So region 1 gave one monster four linen pieces
and a steel boot, and the next the mirror image - two sets with a swapped shoe.
`ArmourSetRegistry` now orders each slot's candidates by SET, which makes the
same rotation deal **three pieces of one set and two of the other, flipping
between monsters**. No monster is "the linen one" and completing a set means
killing more than one thing.

**AND THE SET BONUS HAS NEVER FIRED FOR ANYBODY.** `EquipmentInstance.SetId`
exists, `SetBonusEngine` reads it, and **nothing in this server has ever written
it** - nine places construct an EquipmentInstance and not one assigns a set, so
every item every player owns is set 0. Two consequences: set bonuses are dead
content, and the Book of Deeds entries "wear two pieces of one set" (chapter II)
and "wear five pieces" (chapter V) are **unachievable**, which blocks those two
Seals permanently. `ArmourSetRegistry` is what a fix would be built from -
stamping a set id on drops is a few lines - but it turns a dead mechanic on and
is therefore a BALANCE change, so it is left as a decision rather than slipped
in.

**~~Gloves, amulets and rings are thin in the drop tables~~ - DONE, and it was
code rather than content.** One authored amulet per location dealt to exactly
one of five monsters meant four of them dropped none, so a player with a
favourite could farm it forever and never see one. Each slot is now dealt round
robin with wrap-around: every monster carries exactly one piece of all eight
slots, so an equipment roll is an even eight-way choice. `EquipmentDropTable`
needs no change when a second amulet is authored - it starts alternating them.

**~~`BankEquipmentInstances`~~ - DONE.** Merged into `EquipmentInstances` and
the table dropped; see "One store, one number" above.

**~~`ExecuteUpgradeToolAsync` is an empty stub~~ - DONE.** The button, the
helper, the command handler and the whole twenty-four-line
`VillageBuildingEngine` are gone. The dead door was wider than this entry
knew: the engine was constructed in `Program`, threaded through the simulation
constructor and dispatched to on every request, and a request that failed
validation DISCONNECTED the player - over a command with no effect to protect.

## How to verify a gameplay change

`client_web/scripts/exercise.mjs` drives every interactive feature against a
real server, database and browser and asserts the world CHANGED. **58/58 as of
2026-08-09**, including the inheritance purchase and marrying a villager. Needs
Postgres, the server with `--seed-dev`, and vite on 5173.

Server suite: **480/480**. Client: **238**. Run the server suite with NO
filter - see the section on the red suite for why that sentence is here.

Read the last line of its output, not the last check: a Playwright call that
THROWS ends the process without a summary, and every check below the throw
simply never runs. That is how the market's filter rework silently disabled a
third of the suite.

Two of its steps pick their subject rather than naming it, and both learned it
the hard way: combat fights the strongest unlocked NON-boss monster (the
weakest dies in one tick and shows a frozen bar; a boss can kill the fixture
and turn the check into a coin toss), and the inheritance step reads the card
that owns the Buy button it clicked (levels are permanent, so a well-exercised
fixture caps its first stat).

## Client UI Hook Points`
predates the web client and describes Unity work.

## Where it runs

| | |
|---|---|
| Game (players) | https://folkidle.onrender.com - Render static site `srv-d9or28m7bikc73ft5250` |
| API | https://idlehra.onrender.com - Render web service `srv-d9opsd5bedkc73dfi8h0` |
| Database | Supabase project `copqoxrngbvnvnebybzc` |
| Cache | Render Key Value `red-d9orkqm7bikc73fu43o0` (free, 25 MB, no persistence) |

`idlehra.onrender.com` serves NO page - a plain GET returns 400 by design,
because that path only accepts a WebSocket upgrade. Health is `/healthz`.

Traps that cost time and will cost it again:

- **Supabase pooler port.** Use **5432** (session mode). Port 6543 passes
  ordinary queries but hangs EF migrations on the command timeout.
- **The client build cannot run `npm run build` on Render** - that chains
  `generate:protocol`, which shells out to `dotnet`. The deploy build command
  is `cd client_web && npm ci && npx vite build`.
- **Five minutes of CDN cache on `index.html`.** Verify a client change by
  fetching the bundle and grepping it, not by the deploy status.
- **A player's browser holds the old bundle** across a deploy. Ask for a hard
  reload before investigating anything the code says is already fixed.

## M1. Migration to the Oracle box - BUILT, BLOCKED ON ONE CONSOLE CHANGE

`ops/oracle/` holds the compose file, the Caddyfile, `.env.example` and a
runbook. The image builds cleanly on the box's aarch64. The hostname is
settled: **92-5-0-94.sslip.io**, which already resolves to 92.5.0.94 with no
account and no cost - and it is not cosmetic, because Let's Encrypt will not
issue for a bare IP and an https page cannot open a `ws://` socket.

Instance iptables for 80/443 is open and persisted. **Oracle's cloud Security
List is not**, and it cannot be changed from the box - there is no OCI CLI and
no instance principal. Console steps are in `ops/oracle/README.md`. Until that
is done there is no certificate, so no `wss://`, so no cutover. Verified
closed: a listener on port 80 is unreachable from the internet with the
instance firewall open.

Still needed at cutover, and NOT obtainable through the Render API (there is
no read endpoint for environment variables): `JWT_SECRET_KEY` and the Supabase
password, both from the Render dashboard. **Copy the JWT secret, do not
regenerate it** - tokens are signed with it and a new one bounces every
logged-in player to the login screen with no explanation.

## What shipped 2026-08-05

**Loot: each monster drops its own gear.** Equipment was picked by scanning
the whole region for a BaseId containing a category substring, so every
monster in a location dropped the same pool, ordinary monsters dropped only
weapons and offhands (armour was on a boss-only roll), and the canonical bows
could not drop anywhere because the code grepped `_ranged_weapon_slot_`
against ids authored `_range_weapon_slot_`. `EquipmentDropTable` deals each
location's equipment across its five monsters; the RegionTier 6-10 endgame
ladder, which no reachable monster carried, goes to Malakor.

**H1 was misdiagnosed and is now measured.** A fresh character on region 1,
driven through the real tick for a simulated hour, reaches **level 7** - the
87x is not reachable on the live path, and the reward arithmetic was never
wrong (XP is exactly 4x gold for every monster, and the report's own figures
are consistent with that). Two real defects behind it: three disagreeing
damage models, two of which ignored monster armour entirely, so offline and
warp paid for combat that could not have happened; and gold having no durable
path that did not go through Redis, which is the "two gold figures on one
screen". Both fixed, both pinned by tests.

**H2 was the send lock.** Sends took a semaphore with no timeout from a
fire-and-forget 10 Hz broadcast, so a peer that stopped reading left one send
pending forever and every later frame queued behind it. Nothing threw, nothing
closed - the socket stayed open and silent, which is why the client never
reconnected and F5 fixed it. Frames are dropped rather than queued now.

**H4** did not need a new table: `historical_market_archives` has recorded
every completed trade with a timestamp since the market shipped. Price
history, day/week/month change, and the fee-plus-guild-cut breakdown are in.

**H5/H6** done. Both stale tests asserted deliberately removed content; the
reroll now has a door in the Chest, where the items are.

## Open

**H3. Sprite coverage.** `sprites.generated.ts` holds 234 entries against 437
items, so more than half fall back to initials. Needs an audit of what exists,
what is missing, and what is mapped to the wrong item. The alias table in
`scripts/generate-sprites.mjs` is where a wrong mapping would live.

**Balance is now measurable and probably wants attention.** An hour of region
1 reaching level 7 is the other end of the complaint that started H1 -
`ProgressionRateTests.OneHourOfCombatIsMeasured` prints the number, and
`AnHourDoesNotFinishTheGame` bounds it loosely at level 40 so it catches
another 87x without freezing the balance. The design's own intent, in
`ProgressionEngine`'s comment, is roughly 72 minutes for region 1.

**Gloves and offhands are thin.** Each location authors only two gloves and
one offhand against five monsters, so some monsters offer neither. That is a
content gap, not a code one - `EquipmentDropTableTests` deliberately does not
require every location to cover those two slots.

## How to verify a gameplay change

`client_web/scripts/exercise.mjs` drives every interactive feature against a
real server, database and browser and asserts the world CHANGED - unlike
smoke-screens.mjs, which only proves a screen renders. 44/44 as of this
handoff. It needs Postgres, the server with `--seed-dev`, and vite on 5173.

## Client UI Hook Points

### 1. Region-Completion Codex UI - ALREADY SHIPPED, item was stale

Resolved, and had been for some time before anyone noticed this entry was
out of date. `client/Assets/Scripts/Engine/CodexRegionsCache.cs` and
`client/Assets/Scripts/UI/UiCodexRegionsWindow.cs` both exist, and
`UiCombatLocationPanel` consumes the same cache. The description below,
which asserts "there is no client-side reference to `RegionCompletion`
anywhere under `client/Assets/Scripts/`", is simply no longer true.
Original description follows.



The server fully implements per-region completion tracking
(`PlayerRegionCompletions` table, `TickStatePayload.CompletedAreaFlags`,
`RegionCompletionNotification` queue drained every tick, and
`CachedCodexDamageMultiplier`/yield multiplier bonuses that already affect
live combat math - see `GAME_DESIGN_SPEC.md` Section 1.3). There is no
client-side reference to `RegionCompletion` anywhere under
`client/Assets/Scripts/` - not in `UiCommandDispatcher.cs`, not in any UI
binder. The existing Monster Codex UI stack
(`UI/UiCodexListBinder.cs`, `UI/UiCodex3DViewer.cs`, `UI/UiCodexBonusBinder.cs`,
`UI/UiCodexListRow.cs`, `UI/MonsterCodexEntryView.cs`,
`Engine/CodexInventoryCache.cs`) is the concrete pattern to follow for a
new region-completion view: a cache component that mirrors
`CompletedAreaFlags` from the inbound `StateUpdatePacket`, a list/grid
binder, and a bonus-summary binder analogous to `UiCodexBonusBinder`.

### 2. Market Order-Book Browser UI - SHIPPED, item retained for reference

Resolved. `UiMarketBrowserWindow`, `UiMarketDataBinder`, `UiMarketListingRow`,
`UiMarketBuyOrderPanel` and `UiMarketSellPanel` all exist and are constructed
by `MainSceneBuilder.BuildMarketBankWindow`. Original description follows.

`UI/UiCommandDispatcher.cs` already exposes `DispatchMarketListItem()` and
`DispatchMarketBuyItem()`, which send real `MarketListItem`/`MarketBuyItem`
packets - but both read their arguments from bare public fields
(`MarketTargetInstanceId`, `MarketListingPrice`) that nothing in the client
currently populates from user interaction. `UI/UiMarketDataBinder.cs` is a
read-only HUD (current gold, tax bracket, net-payout preview for a price the
player has already decided on) - it is not a listings browser. Needed: a
view that requests/displays active `MarketOrderRecords` for a chosen
`(BaseItemId, QualityTier)`, lets the player select a target order or a bag
item + price, and wires the selection into the dispatcher fields before
calling the existing `Dispatch*` methods. The server-side corridor and tax
logic (`GAME_DESIGN_SPEC.md` Section 3) does not need any changes to
support this - it is purely a client presentation gap.

## Architecture

### 3. Real horizontal-scaling design for SimulationEngine (do not do the literal "stateless PlayerSessionRegistry" ask)

A prior task asked to make `PlayerSessionRegistry` stateless via Redis to
unblock Kubernetes HPA. That specific class is not the blocker (see
`CURRENT_IMPLEMENTATION_STATE.md` Section 10) - the actual constraint is
that `SimulationEngine._activePlayers` holds every online player's full
live tick state in one process's memory, so a given player's session is
pinned to whichever pod accepted their WebSocket connection. Two real paths
forward, in order of implementation cost:

1. **Sticky routing / pod affinity**: keep the current in-memory
   architecture unchanged, add a Redis-backed `playerId -> podId` (or
   `playerId -> podAddress`) mapping written on connect, and route/proxy a
   reconnecting client back to the pod already holding their session (or
   reject and force a clean reconnect if that pod is gone). This does not
   allow arbitrary pod interception of an in-progress session but does
   allow HPA to add pods for new connections and drain old pods gracefully
   before termination.
2. **Full state externalization**: move `TickStatePayload` itself into
   Redis (or another shared store) with per-player distributed locking for
   the duration of a tick's mutation. This is a full rewrite of the tick
   loop's core execution model, touches every engine that currently takes
   a `ref TickStatePayload`, and should not be attempted without a
   dedicated design pass and load testing plan.

### 4. Market lock contention - partition before abandoning transactional integrity

A prior task asked to replace the order book's `Serializable` + `FOR UPDATE`
matching with Redis ZSETs and an async write-behind pipeline. Do not do
this as literally specified (see `CURRENT_IMPLEMENTATION_STATE.md`
Section 10 for why - it reverses this codebase's anti-double-spend
hardening for a real-money-adjacent subsystem). If matching throughput
becomes a measured, real bottleneck (not a hypothetical one), the lower-risk
next step is partitioning contention by `(BaseItemId, QualityTier)` - e.g.
per-partition advisory locks or per-partition worker affinity - so unrelated
items no longer serialize against each other, while keeping every
individual match inside a real ACID transaction. Only reach for
eventual-consistency/write-behind designs after partitioning is proven
insufficient, and only with an explicit reconciliation/crash-recovery plan.

### 5. Domain namespace reorg for Engine/ and Models/ (deferred, do incrementally)

`Engine/` (71 files) and `Models/` (57 files) are flat. A full mass
relocation into domain namespaces (`FolkIdle.Server.Core`,
`.Combat`, `.Economy`, `.Social`, `.Infrastructure`,
`.Utils.Cryptography`, plus a Models split between EF entities and
DTOs/seed routines) was requested and deferred this pass as too large a
diff for the value delivered right now. If picked back up, do it file group
by file group (e.g. move the market trio first: `MarketOrderBookEngine.cs`,
`MarketEscrowEngine.cs`, `CraftingEngine.cs` into `FolkIdle.Server.Economy`),
verifying `dotnet build` and the full test suite after each group, rather
than as one mass move.

## Cleanup

### 6. Dead engine duplicates - ENTIRELY STALE, do not act on this

All three claims below were re-checked and every one is wrong. Kept only so
nobody rediscovers the entry and "fixes" something that is either gone or
load-bearing.

- `Engine/SeasonEraEngine.cs` **does not exist**. It was deleted in commit
  `39d204c`; there is no file matching `*SeasonEra*` anywhere in `server/`.
- `PlayerChronoRegistry` is **not dead code and is not an engine**. It is
  `Models.PlayerChronoRegistry`, a live EF entity: all 22 references are
  migration snapshots of the DbContext model. Removing it would need a real
  migration, not a file deletion.
- `ChronoBufferEngine.ProcessLoginHandshake` has **exactly one overload and
  exactly one caller** (`StateCheckpointManager` line ~1022). There is no
  unused second overload.

The general lesson, which is worth more than the entry: a "delete this dead
code" item is only as good as the day it was written. Re-verify before acting.

### 6b. UpgradeTool - SHIPPED, item retained for reference

Resolved. The Village screen has a TOOLS section with an Upgrade Tools
button and a line naming the current tier and its gathering speed bonus,
which was previously invisible - the bonus is applied inside
`GatheringToolEngine`'s tick threshold with nothing on screen attributing
it. Note the server's `ExecuteUpgradeToolAsync` takes only a player id:
there is a single account-wide tool tier, not a tier per tool type.
Original description follows.



`CommandType.UpgradeTool = 21` is validated and implemented server-side, and
tool tier is a substantial gathering multiplier - `GatheringToolEngine`
grants +10% through +200% speed across its ten tool tiers, and the measured
pacing model assumes tier 0. The only sender,
`WebSocketClient.SendUpgradeCommandZeroAlloc`, is reachable exclusively from
`UiCommandDispatcher.DispatchUpgradeTool`, which nothing wires up. So no
player can ever upgrade a tool.

Do NOT delete the sender - it is the wiring for a real feature, not dead
code. It needs a button, most naturally on the Village or Workshop screen
next to the other infrastructure upgrades.

`SendPingCommandZeroAlloc` is the other unreferenced sender; that one is
network diagnostics and is plausibly meant to be manual-only.

### 7. ForgeSplicingEngine BaseItemId parse - SHIPPED, item retained for reference

Resolved. The method already resolved the same definition a few lines above
for the tier cap, so the affix roll now reuses that value instead of doing a
second lookup that could never succeed. Note the file has moved to
`Domain/Economy/ForgeSplicingEngine.cs`. Original description follows.



`Engine/ForgeSplicingEngine.cs` line ~165 does
`int.TryParse(targetItem.BaseItemId, out int baseId)`, but `BaseItemId` is
always a slug string (e.g. `gilded_sabatons_boots_armor_slot_base`), never
numeric, so this always fails and `regionTier` silently defaults to 1 for
every forge-fusion affix roll regardless of the item's actual region tier.
Fix: use `ContentRegistry.TryGetItemDefinitionByBaseId(targetItem.BaseItemId, out var definition)`
(added this pass for the market fallback-price feature, see
`GAME_DESIGN_SPEC.md` Section 3.1) to get the real `RegionTier` instead.

## Content and Balance

### 8. Region 3-5 balance - SHIPPED, item retained for reference

Resolved. The curve is now measured rather than merely reachable, and
`Test_Progression_EveryRegionClearsInsideThePlayableTimeBand` fails if any
region leaves the playable band. Three compounding defects were found:

1. Item base `FlatAttackPower` reached `StatsCalculator` from nowhere, so all
   five gear tiers were identical in combat.
2. The level curve grew `1.15^level` (16.4x per region) against 3x more player
   power per region. Level 100 was ~59 days of uninterrupted combat.
3. Region bosses sat at 17-29x their own region's strongest regular.

Modelled clear time per region, using weapon base power alone and ignoring
affixes/STR/set bonuses (a floor, not an estimate): 76 / 127 / 169 / 199 / 222
minutes, ~13.2 hours total. Gathering is a steady 9-11% of each region, so the
31 node thresholds and the 103 recipe costs were measured and deliberately left
unchanged. Original description follows.

Every recipe ingredient is now obtainable and every gathering node drops
something (`Test_ContentRegistry_EveryRecipeIngredientIsObtainableFromSomeSource`
and `Test_ActivityIdBands_EveryRekeyedNodeKeptItsLootTable` both pin this).
What has NOT been done is any balance pass over the numbers: node tick
thresholds, drop weights, and the 103 recipes' material costs were authored to
be reachable, not to be paced. Nobody has played a full progression curve end
to end, so the shape of the mid-game is unmeasured.

### 9. Set bonuses collapsing four armour slots - SHIPPED, item retained for reference

Resolved, and it was worse than this entry described. `SetBonusEngine` awards
its tiers by counting how many worn pieces share a SetId, and it was always
sized for seven slots (`MaxTrackedSlots` is 8). Its caller handed it three.
So a player in a full matching set produced a count of at most 3 and **no
4-piece bonus in the game was reachable by anyone, ever** - not a fidelity
loss, a whole tier of content that could not fire. Fixed by replacing the
weapon/armour/leggings triple with `EquippedSetIds` (all seven slots, one
value type, same bundling rationale as `EquippedAffixTotals`).
`Test_SetBonusEngine_FourMatchingArmourPiecesReachTheFourPieceTier` pins it.
This also folded in item 17. Original description follows.



`EquipmentSlotEngine.ComputeEquippedTotalsAsync` returns a weapon/armour/
leggings SetId triple because that is what `SetBonusEngine.Evaluate` consumes.
With six equip slots, the four armour pieces all fold onto the single armour
set id, taking the first one found. Widening set bonuses to six slots is a
balance change rather than a refactor and was deliberately left out of the
equipment pass.

### 10. Helper/offhand slot - SHIPPED, item retained for reference

Resolved. `EquipmentSlotEngine.SlotOffhand` (index 6, `SlotCount` 7),
`CharacterRecord.EquippedOffhandId` plus migration
`20260731182136_AddCharacterOffhandSlot`, the `StateUpdatePacket` field, and the
client slot row. Note the estimate below understated it: the change touched 13
mirror sites, including `SeasonalRotationEngine`'s era wipe - missing that one
would have re-opened the cross-player equipped-id leak for offhand items.
Original description follows.

`AffixRegistry.EquipmentSlotMask` includes `Shield`, and
`AffixRegistry.ResolveSlot` matches the `_helper_offhand_` BaseId marker, so
helper items already roll slot-correct affixes. There is no seventh equip
slot, so they cannot be worn - the same shape as the helmet/gloves/boots gap
that the six-slot pass closed. Adding it is one entry in
`EquipmentSlotEngine`'s slot constants plus one column.

## Client UI Hook Points (continued)

### 11. Per-character loadouts for slots 2 and 3 - SHIPPED, item retained for reference

Resolved. The blocker was not the UI: `/api/v1/player/inventory` reported an
account-wide `IsEquipped` flag, which can say an item is worn but never by
WHICH character, so the Roster had no way to attribute gear even though it
had the data in front of it. The snapshot now carries
`EquippedByCharacterSlot` (-1 when carried) and `UiRosterPanel` renders a
"Gear (n/7): ..." line per slot. Original description follows.



The wire carries the ACTIVE character's six equipment slots only. Gear changes
on a button press rather than at 10Hz, so the other characters' loadouts are
deliberately left to `/api/v1/player/inventory` rather than costing 96 bytes a
frame. The Roster screen currently shows each character's activity and status
but not what they are wearing; wiring the REST snapshot into a per-character
equipment view is the remaining piece.

### 12. Race unlock feedback - SHIPPED, item retained for reference

Resolved. `UiRaceUnlockToast`, fed by `StateUpdatePacket.UnlockedRaceBitmask`.
Carried as a monotonic ownership mask rather than a one-shot event so the
announcement survives a reconnect and cannot fire twice; the first mask seen in
a session is a baseline, never an announcement. Original description follows.

`PlayerRaceUnlocks` is written and a male/female pair is granted on a region
boss's first kill, but nothing tells the player it happened - no toast, no
entry on the Roster or Race Mastery screens. The unlock is currently only
visible as two new characters appearing in the roster.

## Tooling

### 13. Unity CI - licence RESOLVED, release build needs one variable

`UNITY_LICENSE` is configured and the Unity jobs run. As of 2026-08-01 the
licence check passes and **both the EditMode and PlayMode suites are green** -
client-side verification is no longer manual-only.

The jobs now run under a GitHub Environment named `Unity`, so secrets and
variables must be defined there rather than at repository level.

**Still outstanding: the Android release build.** It fails because no
`FOLKIDLE_CDN_BASE_URL` variable is set. `BuildPipelineController` creates the
`Production` Addressables profile from it on first run and fails loudly when
it is absent, deliberately refusing to default it - Production content built
against a placeholder URL would ship and then fail to load, which is the exact
failure the surrounding code exists to prevent. Add
`FOLKIDLE_CDN_BASE_URL` to the `Unity` environment, set to the CDN root that
will serve the remote catalog and bundles.

Note this is the FIRST time the release build has ever been exercised - it
previously required an Addressables profile that only existed in a
developer's local settings asset, so it could never have passed on a clean
checkout. Expect further genuine failures on the first successful run past
the profile stage; nothing downstream of it has been proven yet.

### 15. No audio clips exist

The audio trigger layer is built and wired (`GameAudioDirector`,
`GameAudioEventRelay`, `UiButtonClickSfx` on every button, plus combat, loot,
crafting, level-up, race-unlock and error triggers), but
`client/Assets/Resources/Audio/` is empty, so the game is silent. This is
deliberate and safe - a missing clip resolves to null, `Play` returns
immediately, and nothing logs - and it is verified: all ten effects fire with
zero clips present and no exception. Dropping correctly named files into that
folder starts them playing with no code change and no scene rebuild. See that
folder's README for the names and their trigger sites. Nothing registers a
music track either, so `AmbientAudioEngine`'s crossfade has no bed to fade.

### 16. Test_MarketEscrow_ConcurrentListings - SHIPPED, item retained for reference

Resolved by fixing the engine rather than the test, exactly as this entry
proposed: `MarketEscrowEngine.ListItemAsync` now runs under a retrying
execution strategy built on `RetryingDbContextOptions`, with the command
result and log line hoisted out of the retried delegate into a
`ListAttemptOutcome` so a retry cannot enqueue a duplicate result. Original
description follows.



`Test_MarketEscrow_ConcurrentListings_ExactReplicaNoSerializationDrift` fires
six concurrent `Serializable` listings for one player and asserts all six
commit. Under full-suite load against the shared Postgres container, five
routinely lose the serialization race and `ListItemAsync` returns false after
catching the transient failure rather than retrying; in isolation it passes
every time. Confirmed pre-existing by stashing an unrelated working tree and
reproducing the identical failure on the untouched baseline - do not chase it
as a regression. The real fix is to wrap `MarketEscrowEngine.ListItemAsync` in
a retrying execution strategy the way the equip path already is (commit
`7a95764`), rather than weakening the test.

### 17. Set bonuses and the offhand slot - SHIPPED, folded into item 9

Resolved together with item 9: `EquippedSetIds` carries all seven slots, so
the offhand now contributes its set id alongside its base stats and affixes.

### 18. Client server address is now configurable - SHIPPED, new item for the record

Twenty-five classes each declared their own
`ServerBaseUrl = "http://localhost:8080"` and **nothing anywhere ever assigned
a different value to any of them**. Only `UiLoginWindow`'s copy affected
authentication and the WebSocket handshake, so a build could authenticate
against a real server and then have all twenty-two HTTP caches - inventory,
market, guild roster, mailbox, leaderboard, codex - silently query localhost
and come back empty. In practice the client only ever worked on the machine
running the server, which made every non-shipped item above untestable
anywhere else.

Now one `ClientServerConfig.BaseUrl`, resolved from `FOLKIDLE_SERVER_URL`,
then a saved preference, then the localhost default, with `UiLoginWindow` as
the sole writer.

### 19. 4-piece set tier - FULLY SHIPPED

All five 4-piece effects are now consumed by the live combat tick.

The fifth, `CcImmunityActive`, was replaced rather than implemented. It could
never fire: this game models no player-facing crowd control - Vulnerable,
Chilled and Burning are all applied BY the player TO the monster - so there
was nothing to be immune to, and building a CC system to justify one set
bonus would have been the tail wagging the dog.

It is now `DamageCapActive`: any single incoming hit is capped at 20 percent
of effective max HP. Same tank/mitigation archetype, and it answers the
failure mode this game actually has. Region bosses sit at roughly 2.5x the
attack power of their region's regular monsters, and the auto-eat larder can
only respond BETWEEN hits, never during one - so a single large hit is
unsurvivable in a way that the same total damage spread over several hits is
not. At 20 percent a wearer always survives at least five consecutive
maximum hits from full, which is exactly the window auto-eat needs.

Applied after armour and block so it is a true ceiling rather than another
mitigation term, and before the HP subtraction so the set's own thorns
reflects the CAPPED figure - the set cannot convert its defence into extra
offence. `Test_SetBonus_DamageCapLimitsASingleHitToAShareOfMaxHp` pins the
arithmetic, not merely the flag. Verified the progression pacing band is
unchanged. Original description follows.



Resolved for four of the five effects, all now consumed by the live combat
tick: `FireDamageMultiplierPct` and `BurnApplicationActive` in the outgoing
damage step, `ThornsReflectionActive` in the incoming one, and
`CooldownReductionActive` at the skill-cast site. Burn is a deterministic
fraction of the hit that applied it rather than a timed DoT - this combat
loop has no per-target effect timers, and adding a scheduler for one effect
would be a far larger change than the effect is worth.

**`SetCcImmunityActive` remains deliberately unconsumed.** This game models
no player-facing crowd control at all: the only status effects that exist
(Vulnerable, Chilled, and the new Burning) are applied BY the player TO the
monster, so there is nothing to be immune to. Implementing it would mean
inventing a CC system, which is a design decision rather than a wiring fix.
Either add player-facing CC and connect it, or give that slot in the Eternal
Dreadnought 4-piece an effect that does something. Original description
follows.



`SetBonusEngine` produces five 4-piece effects and **not one is consumed by
anything**: `ThornsReflectionActive`, `CooldownReductionActive`,
`BurnApplicationActive`, `CcImmunityActive` and `FireDamageMultiplierPct`
are copied onto `CombatStats` by `StatsCalculator` (lines ~274-277) and
read by zero call sites in the entire server.

This was harmless while the 4-piece tier was unreachable. **Item 9 made it
reachable**, so a player can now assemble four matching pieces, be told
they have a set bonus, and receive only the 2-piece flat stat. Both
authored sets are affected: Chiming Steel's 4-piece is Fire damage + Burn
(both inert) and Eternal Dreadnought's is Thorns + CC immunity + cooldown
reduction (all three inert).

Either implement them in the combat tick or stop advertising them. Do not
leave a tier that visibly qualifies and silently pays nothing - that is
worse than not having it.

### 20. Luck and Constitution bonuses - SHIPPED, item retained for reference

Resolved. `ForgeSuccessPct` is now added to the fusion roll in
`ForgeSplicingEngine`, clamped at 95 percent so enough Luck can improve the
odds without turning the forge into a guaranteed upgrade and removing the
tier sink. `OutOfCombatHpRegen` is applied by an idle-only regen tick, gated
on `ActiveActivityId == 0` on purpose: regenerating mid-fight would undercut
the auto-eat larder, which is the intended sustain mechanic and the thing
every halt reason is built around. Original description follows.



`StatsCalculator` documents Luck as granting "+0.05% Forge Success" and
Constitution as granting "+0.1 Out-of-Combat HP Regen/sec", computes both
into `ForgeSuccessPct` and `OutOfCombatHpRegen`, and **nothing anywhere
reads either field**. The forge's success roll does not consult
`ForgeSuccessPct`, and no regen tick exists.

So a player investing in Luck for forge safety, or Constitution for
regeneration, gets nothing for it. Same class of defect as item 19 and as
the item-base-power bug: the value is computed correctly and thrown away.

### 21. Broadcast dirty-checking - SHIPPED, but NOT as this entry proposed

Resolved - and the approach suggested below would have been a real bug.

This entry proposed gating on `TickStatePayload.IsDirty` and clearing it
after dispatch. `IsDirty` is owned by `StateCheckpointManager`, which uses
it to decide whether to persist to Postgres/Redis and resets it when it
does. Consuming it in the broadcast would have silently skipped saves -
trading data loss for bandwidth.

Instead each packet is compared against the last one actually sent to that
player, excluding `TicksSinceLastFlush` (which increments every tick, so
including it would make every packet differ and the check would save
nothing). Cache entries are dropped through `RemoveActivePlayer`, the
existing single choke point for session cleanup.

Verified live: an idle session receives no packets at all, while a session
in combat receives one per tick.
`Test_Broadcast_SuppressesIdenticalPacketsButStillKeepalives` pins both
halves, including the 10-tick keepalive - which is the half that cannot be
observed from the client and would starve interpolation if it silently
stopped firing. Original description follows.



`SimulationEngine`'s broadcast loop iterates all of `_activePlayers` and
calls `SendToPlayer` unconditionally - there is no check against
`TickStatePayload.IsDirty`, even though that flag exists, is maintained
throughout the tick, and is exactly the signal needed.

Cost: 695 bytes x 10 Hz = **~7 KB/s per connected player, whether or not
anything changed**. About 55 Mbps sustained at 1,000 concurrent players and
556 Mbps at 10,000 - for a game where an idle player's state is identical
frame to frame.

This is the single largest optimisation available. The obvious shape is to
send on dirty, plus a forced keepalive every N ticks so the client's
interpolation and save-trust indicator never starve. Note the client
interpolates between two snapshots (`VisualSyncProxy`), so the keepalive
interval has to stay short enough not to make motion stutter - measure
before picking it.

### 22. Hot-table indexes - SHIPPED, item retained for reference

Resolved by migration `AddHotTableCompositeIndexes`. Verified against a real
database that the planner now uses `IX_CommodityRecords_PlayerId_ItemId`
with BOTH columns as the index condition for the gold lookup, rather than
scanning the whole `ItemId` index. The pre-existing single-column indexes
were kept: they still serve the market's cross-player searches, which
genuinely do lead with the item. Original description follows.



`FolkIdleDbContext` adds exactly three indexes for this family, and each is
on the low-selectivity column rather than the one every query filters by:

| Table | Indexed on | Actually queried by |
|---|---|---|
| `CommodityRecords` | `ItemId` | `PlayerId` + `ItemId` |
| `EquipmentInstances` | `BaseItemId` | `PlayerId` |
| `MarketOrderRecords` | `BaseItemId` | `BaseItemId` + `QualityTier` + `Status` |
| `CharacterRecords` | (nothing) | `PlayerId` |

`CommodityRecords` is the worst case: the index is on `ItemId`, and
`ItemId = "gold"` matches **one row per player in the game**. Reading a
single player's gold balance - which happens on every login, every kill
reward and every purchase - scans that entire index. `CharacterRecords` has
no secondary index at all and is read on every login, equip and inventory
snapshot.

Fix: composite `(PlayerId, ItemId)` on `CommodityRecords`, `(PlayerId)` on
`EquipmentInstances` and `CharacterRecords`, and
`(BaseItemId, QualityTier, Status)` on `MarketOrderRecords`. Cheap, one
migration, no behaviour change.

Note this only affects tables using a conventional `Id` primary key. The
many tables with a composite `(PlayerId, X)` key - codex entries, race
masteries, region completions, village infrastructure, quests - are already
covered by their primary key index.

### 23. EvictVillager - SHIPPED, item retained for reference

Resolved, and the real blocker was not the missing button. The client was
never told WHICH village slots are occupied - the wire carries a population
count and nothing else - so there was no way to name a target even with a
button present. The player statistics snapshot now carries the villager
slots, and the Village screen renders a roster with a per-villager Evict
that sends the resident's real `SlotIndex` rather than its row position
(slots go sparse after an eviction, so sending the row would evict the wrong
resident). Original description follows.



`CommandType.EvictVillager` is validated and implemented server-side and
has no client reference outside the dead `UiCommandDispatcher`. Same shape
as item 6b (`UpgradeTool`), smaller stakes. Do not delete the sender.

### 24. OfflineStateEngine - SHIPPED (deleted), with one correction

Deleted. But the entry below was not quite right, and the correction is the
useful part: it had zero PRODUCTION references, not zero references. One
integration test instantiated it directly. The first sweep missed that
because it was scoped to `server/FolkIdle.Server/` and did not include the
test project.

Deleting the test along with the engine would have silently dropped the only
guard on a rule that is still live: backpack capacity is
`SimulationEngine.DefaultBackpackCapacity` plus the Human vault mastery
bonus, which `StateCheckpointManager` uses for real and which had no direct
test of its own. The test was therefore retargeted at the live formula
rather than deleted -
`Test_RaceMastery_BackpackCapacityUsesHumanVaultBonusNotAHardcodedValue`.

Reinforces item 6's lesson from the other direction: verify the scope of a
"no references" claim, not just its result. Original description follows.



Zero references anywhere, including `Program.cs` - unlike the phantom
entries in item 6, this one was verified to exist and to be unreferenced.
`OfflineSimulationEngine` is the live offline path. Safe to delete after a
final check.

### 14. Play Mode harness needs a seeded fixture account - SHIPPED, item retained for reference

Resolved. `--seed-dev` provisions a repeatable account (three characters, all
seven equip slots filled, Town Hall 5, materials, gold) and is double-guarded:
the flag alone is not enough, `FOLKIDLE_ALLOW_DEV_SEED=1` must also be set,
because unlike the other operator flags this one writes a known password. See
`DevFixtureSeeder`. Original description follows.



Verifying multi-character, equipment and progression behaviour in Play Mode
currently requires hand-seeding the database (Town Hall level, roster,
equipment) with a throwaway console app. A committed, idempotent dev-seed
entry point - alongside `--migrate` and `--lift-quarantine` - would make the
audit repeatable instead of improvised. Note it must stay clearly
non-production, guarded the way `--lift-quarantine` is.

## Audit Findings, 2026-08-01

Logged from a full sweep across server, tests and client. Grouped because
they share a cause: the wire and the command surface both grew faster than
the UI that was meant to consume them.

### 25. Thirty-two VisualSyncProxy properties have no reader

The client mirrors 32 wire values into `VisualSyncProxy` properties that
nothing in `client/Assets/Scripts/` reads. Most are harmless mirroring, but
several are live server features with no player-facing surface at all. The
two worth doing first:

- **`VisualInventorySpaceRemaining` / `VisualInventoryCapacity`.** The
  backpack "13/20" readout. `InventoryCapacity` was added to the wire
  specifically so this could exist - see the inventory census work - and the
  display was never built. A player has no way to see how full their
  backpack is until loot starts being silently discarded.
- **`VisualGatheringProgress` / `VisualProgressTicks`.** There is no
  gathering progress indicator anywhere in the client. In an idle game whose
  gathering loop is 9-11 percent of total playtime, the player watches
  nothing happen.

Also unread, lower priority: `VisualMentorCount` (the Academy XP bonus,
previously fixed for being "invisible client-side" and still not shown),
`VisualSlot1/2/3AgePhase` and `VisualChildMaturationMs` (character aging and
breeding maturation), `VisualMaxMana`, `VisualGlobalEventId`, and the three
village population fields.

Not every one of the 32 needs UI - some are genuinely internal. The point of
the entry is that nobody has ever gone through the list and decided.

### 26. TotalItemsCraftedCount is never assigned - SHIPPED, and the audit was wrong about it

This entry proposed "assign it or delete it and reclaim four bytes." Deleting
it would have been a silent regression: `UiTutorialController` reads
`VisualTotalItemsCraftedCount` and detects a completed craft purely from that
value rising. The field was not unused - it had a consumer that could never
fire. The removal compiled cleanly on the first attempt and was only caught
by grepping for readers before trusting the audit.

Shipped as the assign branch. `TickStatePayload.LifetimeItemsCrafted`
hydrates at login from `PlayerRecords."TotalItemsCrafted"`, the tick thread
increments it as `CraftingCompletionQueue` drains, and the packet clamps it
into its uint.

Deliberately NOT written back by `StateCheckpointManager`, unlike
`LifetimeDeaths` directly above it in the payload. `CraftingEngine` persists
the column inside the same transaction as the item grant, making it the
single author; a checkpoint flushing an absolute snapshot on top would
clobber any craft committed between hydration and flush.

Lesson worth keeping: "no server code writes it" and "nothing reads it" are
different claims. This audit checked the first and assumed the second.

### 27. Five commands remain unreachable from any UI

Implemented and validated server-side with no client path:
`ConsumeChronoCore`, `SubmitShardAttack`, `RegisterWorldBossDamage`,
`InitiateNodeMigration`, `PingNetworkDiagnostics`.

Two are real player features. `ConsumeChronoCore` is **not** covered by
`UiChronoBankPanel`, which sends `ActivateChronoBoost` and
`ConsumeTimeWarpCore` - different commands. `SubmitShardAttack` is mentioned
only in a comment in `UiGuildWarPanel`. The last two are plausibly
ops/diagnostics and may be fine to retire formally rather than wire.

When checking this yourself: `SendWorldBossAttackCommandZeroAlloc` looks
unreachable to grep and is NOT - it is wired through
`UnityEventTools.AddPersistentListener` in `MainSceneBuilder`, which no text
search can see. Always check the builder before believing a sender is dead.

### 28. CombatStats.SetCooldownReductionActive is redundant - SHIPPED (removed)

The cooldown-reduction effect reads its flag straight off
`SetBonusEngine.Evaluate(...)` at the skill-cast site, so the mirrored
`CombatStats` property has no consumer. The effect works; the property is
dead weight that invites someone to "fix" it by wiring a second path.
Delete the property or switch the cast site to read it - one or the other,
not both.

### 29. AssignMentor carries a slot index in the LimitPrice field

`CommandType.AssignMentor` reads its mentor slot index out of
`cmd.LimitPrice`, a market price field. It works, and it is not urgent - but
it is the same shape as the numeric-id-as-identity bugs that have bitten
this codebase repeatedly. Give it a named field if that packet is touched
for any other reason.

### 30. Stale TODO in the AssignMentor command branch - SHIPPED (deleted)

`SimulationEngine.cs` around line 1872 carries a TODO asking whether a
validator check is needed. `ClientCommandValidator.ValidateMentorshipAssignment`
is called on the very next line. One line to delete. Noted only because it
is the single TODO marker in the entire codebase and reads as a gap when it
is not.

### 31. Character stat rows render bare numbers with no labels - SHIPPED

`UiCharacterStatsPanel` writes only the integer into each row's char buffer -
`WriteIntToBuffer(_strBuffer, 0, str)` with no "STR: " prefix - so the
top-left HUD shows eight unlabelled values reading `0 / 0 / 0 / 0 / 0 / 0 /
0.0% / 0`. The placeholder text passed by the scene builder ("STR: 0") is
overwritten on the first refresh.

Confirmed in a Play Mode screenshot. Pre-existing, unrelated to the activity
status work that sits directly below it in the same panel - and invisible to
any structural check, since every row exists and is correctly wired.

Fix is the same shape as `UiActivityStatusPanel.RefreshBackpack`: write the
label into the buffer before the number.

### 32. Art history is still 472 MB of plain git blobs

`.gitattributes` now routes `*.png` and the other art formats through LFS,
but forward-only. The 127 PNGs already committed - 472 MB across three
commits (`a859802`, `885d87f`, `5d7ee3b`) - remain ordinary blobs, so every
clone still pays for them.

Migrating them is `git lfs migrate import --include="*.png" --everything`,
which rewrites 76 commits and requires a force-push. That part is mechanical.
The reason it is deferred is quota, not difficulty:

- GitHub Free allows 1 GB LFS storage and 1 GB/month LFS bandwidth.
- Migrating puts roughly 472 MB into storage, plus 124 MB of art currently
  untracked on disk, for about 596 MB - already 60 percent of the storage
  allowance with no room for future revisions of the same files.
- `unity_client.yml` checks out with `lfs: true` in TWO jobs. One CI run
  would therefore pull about 1.19 GB and exhaust the entire monthly
  bandwidth allowance on its own.
- LFS overage blocks pushes, not just fetches. The failure mode is the CI
  that was unblocked in `6af382f` breaking again, plus an inability to push
  until the next billing cycle or a paid data pack.

So this is a billing decision before it is an engineering one. Three viable
routes:

1. Buy a data pack (50 GB storage + 50 GB bandwidth). Makes the migration
   safe as specified and is the least invasive to the workflow.
2. Migrate, but drop `lfs: true` from CI and have the Unity jobs build
   against placeholder art. Free, but the build stops covering the real
   asset pipeline, which is exactly what the Android build failure was about.
3. Move art out of git entirely and serve it from the CDN already configured
   via `FOLKIDLE_CDN_BASE_URL`, keeping only import settings in the repo.
   Best long-term, largest change, and it interacts with how the client
   loads sprites today.

Until one is chosen, the forward-only `.gitattributes` is the correct state:
it stops the growth without spending quota.

## Audit Findings, 2026-08-01 (second pass)

### 33. Monster milli-HP overflowed int - SHIPPED, and it was a self-inflicted regression

`CurrentMonsterHp` held milli-HP in an `int`, capping monster HP at
2,147,483. The pacing rebalance set region bosses to
3500/14000/82000/440000/**3000000** without checking that ceiling, so
Malakor wrapped to -1,294,967,296, satisfied the `CurrentMonsterHp <= 0`
death check on spawn, and paid a full kill reward every tick: 6,000,000 XP
and 1,500,000 gold per second from ordinary progression content.

41 of 115 monsters were affected. The mirror defect on the damage side made
the four strongest monsters deal exactly 1 HP per hit.

Fixed by widening to `long` and making the endgame scaling cast saturate.
The scaling fix matters independently of the data: the multiplier compounds
at 1.25 per tier without bound, so wrapping was guaranteed eventually.

The lesson is not "check for overflow." It is that a balance change and a
representation limit lived in two files that nobody cross-checked, and
neither the test suite nor a playtest could reach the region-5 boss to
notice. The guard that now prevents recurrence is the type system - the
`int` revert fails compilation in 10 places - not a test.

### 34. Gathering mastery was never persisted - SHIPPED

Woodcutting/mining mastery was earned by three code paths, consumed for
gathering yield, and carried on the wire, but had no database column, no
hydration, and no UI. Every logout reset both professions to zero, and
nothing on screen could reveal it. Now has columns, hydration, write-back,
proxy mirrors for the levels, and `UiGatheringMasteryPanel`.

### 35. Corrections to the first audit pass

Three findings from the earlier report did not survive verification. Logged
because the wrong methods are worth remembering:

- **"155 dead buttons"** - false. Buttons are wired by serialized field
  reference and runtime `AddListener`, not `AddPersistentListener` (6 vs 56
  files). Cross-checking every button against BOTH mechanisms gives **0
  unwired of 190**. A persistent-listener scan alone is meaningless here.
- **"Command rejections are never surfaced"** - false. `VisualLastCommandResultCode`
  has no reader by design; `VisualSyncProxy` documents that UI must subscribe
  to `OnCommandResultReceived`, which `UiCommandResultToast` does correctly.
  An unread property is not automatically an unwired feature.
- **"UiChatWindow leaks an event subscription"** - false.
  `HandleRowPrefabLoaded` fires exactly once and pools rows for the window's
  lifetime, so the subscription never accumulates.

Also corrected: `AccumulatedWood/Stone/Iron` are sub-1.0 fractional carries,
so their absence from login hydration is by design, not lost state.

Standing methodological note: for this codebase, "X has no reference" is only
a finding once it has been checked against every wiring mechanism the
codebase actually uses. Three of the four false positives above came from
checking exactly one.

### 36. Remaining known gaps

- 27 `VisualSyncProxy` members still have no reader. `VisualMiningXp`/
  `VisualWoodcuttingXp` are now consumed; the rest need triage into "wire a
  UI" or "delete", individually rather than as a batch.
- 8 scene texts still overflow their rect. All come from creators other than
  `CreateHelpText`, which now auto-sizes.
- The art history migration to LFS remains open - see item 32.

### 27b. Unreachable-command triage - RESOLVED, and it surfaced a live server stall

Worked through the five commands in item 27 plus `ConsumeConsumableAsset`.
The triage mattered less than what it uncovered.

**Live defect found: `RegisterGuildDefense` blocked the tick thread.**
`SimulationEngine` ran `RegisterGuildDefenseAsync(...).GetAwaiter().GetResult()`
inline in the 10 Hz loop - a Serializable transaction taking two `FOR UPDATE`
row locks, executed synchronously, for every player. `UiGuildWarPanel` sends
it from a button, so any player could stall the entire simulation for as long
as those locks took, and blocking the tick thread while EF holds locks is a
deadlock shape as well as a latency one. Converted to `SafeDispatchAsync`.

**`SubmitShardAttack` (50) has the same shape and is NOT fixed.** It writes
its result back into `currentPayload`, so it needs the notification-queue
pattern rather than a straight `SafeDispatchAsync`. It is unreachable, which
is the only reason it has never stalled production. The call site now carries
a DO-NOT-WIRE warning. Restructuring it belongs with item 3.

**`RegisterWorldBossDamage` (19) - RETIRED.** A second entry point into the
same `WorldBossEngine.QueueAttack` that `AttackWorldBoss` already reaches,
but with weaker validation: it took the damage figure straight from
`cmd.TargetId` and merely clamped it, where `AttackWorldBoss` validates the
boss instance id, that the event is live, and that the boss is not dead. No
client path sent it. Removed the handler, `WorldBossEngine.RegisterDamage`,
`ValidateWorldBossRegistration`, and the client sender.

**`ConsumeChronoCore` (24) - cannot be wired; it has no content.** The
handler consumes a `CommodityRecords` row and grants 4 hours of banked chrono
time, but no Chrono Core item exists in the 379-entry catalogue, so every
send would fail the `core == null` check. This is a content gap, not a wiring
gap. The dispatcher method is retained with that explanation.

**`InitiateNodeMigration` (44) and `PingNetworkDiagnostics` (52) - client
halves removed.** Migration is server-orchestrated (item 3); the ping handler
echoes a token into `StateUpdatePacket.NetworkDiagnosticsToken` that no client
code reads, so the round trip measured nothing. Server handlers retained for
ops use. Fully retiring 52 would additionally reclaim 4 wire bytes and remove
an unconsumed field - worth doing next time the packet is touched.

**`ConsumeConsumableAsset` (45) was never unreachable.** The earlier audit
called it "a landmine, not a live outage" on the strength of
`DispatchConsumeConsumableAsset` having no builder binding. That was wrong:
`UiCombatLocationPanel` sends opcode 45 directly from `UseFoodButton` and
`UsePotionButton`. Combined with the broken SQL in that handler, **every food
or potion use force-disconnected the player**. Both are now fixed, but the
severity call was wrong for the same reason item 35 documents - one wiring
mechanism checked out of several.

## Affix Rarity, Reroll and Social Layer, 2026-08-01

### 37. Affix rarity system - SHIPPED

A second rarity axis, deliberately smaller than the GDD's 14 item tiers.
Items keep those tiers and keep deciding affix COUNT (GDD 5.2's 1/2/3/4/5,
cap 5, unchanged); affixes gained their own Common..Legendary scale deciding
MAGNITUDE at `floor(base * region * 1.6^(rarity-1))`.

Region keeps the growth term it always had, so progression through the five
regions still drives raw power on its own. Legendary is 6.55x Common. Rolled
values vary +/-20% around that centre, deliberately narrower than one rarity
step so a lucky Common can never beat an unlucky Uncommon - rarity stays
strictly dominant over luck, which is what keeps the Diamond upgrade
worth buying. A test asserts that ordering directly.

Affix count is NOT redefined in AffixRegistry. `RarityTier.GetAffixCount`
already implements GDD 5.2 and every drop path calls it.

Payload keys became `id`, `id@rarity`, `id#stack@rarity`. Both
`AffixRegistry.StripStackSuffix` and `ClientAffixRegistry.StripStackSuffix`
strip the marker; a key either side failed to strip would resolve to no
definition and contribute silently nothing.

### 38. Reroll economy and auto-reroll - SHIPPED

Three operations, two currencies. Value and stat rerolls cost GOLD, escalating
1.35x per consecutive attempt and saturating at a documented ceiling; only a
rarity upgrade costs Diamonds. Auto-reroll burns attempts in bulk, so pricing
it in premium currency would have made the headline convenience feature a
pay-to-win treadmill - and gold needed an endgame sink.

Auto-reroll checks reachability BEFORE spending. Targeting a shield-only
affix on a sword, or asking a value reroll to raise rarity, are rejected up
front rather than discovered by burning the budget. Rarity is a floor, not an
equality, so "stop at Epic" is satisfied by a Legendary. The logic is a pure
evaluator in `AutoRerollPlanner` - no database, no async - so it is testable
without Testcontainers.

Operation and stop condition travel as NAMED packet fields (352 -> 359),
deliberately not smuggled through `LimitPrice` - see item 29.

### 39. Announcements, congratulate button, generated audio - SHIPPED

Epic and above announce to global chat on channel type 3, at the same
threshold `UiRarityPalette` uses for glow so the two cannot disagree.
Enqueued only AFTER the transaction commits and cleared on rollback: the
queue drains on another thread, and nothing can retract a chat line.

The congratulate button sends through the ordinary chat path, inheriting the
server's rate limiting, mute and profanity handling. A dedicated command
would have bypassed all three.

All ten SFX are synthesised from code by `ProceduralSfxGenerator` -
oscillators, filtered noise and ADSR into 16-bit PCM. **Item 15 is closed.**
216 KB total, deterministically seeded so regeneration is byte-identical.
These are placeholders, not authored audio.

### 40. Still open after this work

- **Legendary voice line.** Not possible from here - the synthesiser produces
  tones and noise, not speech. Needs a recording or an external TTS.
- **Font restyle.** TMP needs a font asset built from a TTF/OTF. Sizes,
  weights and colour can be restyled against a supplied face; choosing or
  authoring the typeface cannot be done from code.
- **`ConsumeChronoCore` still has no item** - see item 27b. Unchanged.
- **LFS history migration** - item 32. The audio is the first content actually
  routed through LFS (206 KB), which validates the `.gitattributes` but does
  not change the quota maths for the 472 MB of art history.

### 41. Targeted sweep: dropped deltas and split currency stores, 2026-08-01

Run after three bugs of the same family turned up in a row. Two invariants
were swept exhaustively rather than subsystem by subsystem.

**Invariant 1 - a notification dropped on the tick thread must not destroy
value.** All 33 queue drains in `SimulationEngine` were classified. The
distinguishing property is SNAPSHOT versus DELTA:

- A notification carrying a snapshot (`LegacyStoreUpdate` new balance,
  `BillingSync` balance, `InfrastructureUpdate` building levels) is SAFE to
  drop. The database already holds the value and login re-hydrates it; only a
  live display refresh is lost.
- A notification carrying a delta is NOT safe. The producer has already
  committed the cost, so dropping it destroys what the player paid for.

Only three delta-carrying fields exist in the entire registry:
`MarketMatchNotification.GoldDelta` (fixed - see the market commit),
`ChronoAccelerationNotification.SecondsToAdd` (fixed here), and
`DamageDelta` (guild raid, idempotent - the raid boss row is authoritative).

`MailClaimRequestQueue` deserves note as the correct pattern already: if the
payload is gone the drain does nothing, so `CommitMailClaimAsync` never runs
and the mail simply stays unclaimed. Do-nothing equals no-loss by
construction, rather than by a rescue path.

`CraftingCompletionQueue` drops one quest-progress increment if the player
logs out mid-craft. The item itself is committed by `CraftingEngine`, so this
is a lost counter tick, not lost value. Left alone deliberately - a rescue
path would cost more complexity than the defect.

**Invariant 2 - every currency has exactly one authoritative store.**

- Gold: `CommodityRecords["gold"]` in all ten engines that touch it, with
  `TickStatePayload.CurrentGold` as an in-memory mirror flushed as a DELTA.
  The checkpoint never writes it back as a snapshot, which is what makes a
  direct database credit safe. No split.
- Diamonds: `PlayerRecords."PremiumDiamonds"` only. Was split; fixed.
- Legacy shards: `PlayerLegacyLedger.LegacyShardBalance` only, and the
  checkpoint only READS it (summing ledgers), so there is no snapshot
  write-back to clobber. No split.

**The generalisation worth keeping:** gold is delta-persisted and diamonds
are snapshot-persisted, and that difference decides whether an off-thread
credit is safe or gets silently refunded by the next checkpoint. Anyone
adding a currency should decide which of the two it is before writing the
first spend path.

## Live Play Mode session, 2026-08-01

Ran against the dev fixture (`--seed-dev`, player 1) with Postgres and Redis
in Docker and a real WebSocket session. Everything below was measured against
the database, not inferred.

### 42. Verified working end to end

- **Diamond rarity upgrade.** Uncommon -> Rare -> Epic -> Legendary, costing
  17, 57 and 196 Diamonds - matching `5 * 3.4^(rarity-1)` exactly. This is the
  path that was impossible before the store fix, and the balance survived a
  relog (client read 5186 after reconnect, matching the row).
- **Gold value reroll.** Cost 902 on a tier-3 item, matching
  `250 * 1.9^(tier-1)`. Diamonds unchanged, proving the currency split holds.
  Rarity preserved, magnitude moved 177 -> 151, inside the +/-20% band.
- **Legendary announcement.** Reached the client as
  `1|5|crit_dmg_pct|177` with `IsAnnouncement = true` on channel 3.

### 43. ChatEngine subscribes to Redis once at boot and never retries

Found by accident: the server was started before Redis, and chat was
completely dead - zero messages delivered, no error anywhere. Starting Redis
afterwards did not help. Only restarting the server fixed it, after which the
identical test delivered immediately.

`InitializeAsync` checks `redis == null || !redis.IsConnected` and returns
early, skipping all three channel subscriptions. There is no reconnect
handler and no retry, so a server that boots while Redis is unavailable has
permanently dead global, guild and whisper chat for the lifetime of the
process.

This is a realistic production failure, not a lab artefact: container start
order is not guaranteed, and Redis restarting under a running server produces
the same silent outcome. Same shape as the guild war bug - a condition
observed once and assumed to hold forever.

Fix direction: subscribe on the multiplexer's `ConnectionRestored` event as
well as at boot, and make the subscribe path idempotent (it already is for
the dispatch worker).

### 44. Chat rows are Addressables-only, so chat renders nothing in the Editor

`UiChatWindow.RowPrefabAddressableKey = "UiChatMessageRow"` resolves through
`AssetManager.LoadAsync`. Without built Addressables content the load fails
silently, `_rows` stays entirely null, and the window shows nothing even
while messages arrive correctly (verified: `_totalMessagesAccepted` reached 2
with zero rows instantiated).

So chat is untestable in Play Mode without an Addressables build, and if a
player build ever ships without that content, chat is invisible with no error.
Worth either bundling the row prefab as a direct reference like every other
pooled row in the project, or failing loudly when the key does not resolve.

### 45. The dev fixture seeds gear with empty affix payloads

All four seeded `EquipmentInstances` carry `AffixPayload = '{}'`, so the
reroll and affix UI cannot be exercised from the fixture at all - this
session had to write a payload in by hand. `DevFixtureSeeder` should roll a
real affix set through `AffixRegistry.RollAffixes`, exactly as a drop would,
so the fixture exercises the same path players do.

### 46. World Boss audit, 2026-08-01

Audited because it combines the two categories this codebase repeatedly gets
wrong: currency (rewards) and timers (respawn). Most of it holds up.

**Verified correct:**

- Reward delivery goes through the mailbox, whose claim path is already
  safe-by-construction: if the payload is gone the claim never commits and
  the mail is simply still there next login.
- No duplicate reward distribution. `ProcessDefeatedBossAsync` has an
  interlocked re-entry guard, takes the snapshot row `FOR UPDATE`, re-checks
  `CurrentHp > 0`, and on completion sets `EventState = 2` so the scheduler's
  `IsEventActive` check stops re-firing it. HP is reset to `BaseHp` in the
  same transaction, so the next window starts a fresh boss.
- The lifecycle is a recurring date-window poll (1st-7th, 15th-22nd UTC), not
  a one-shot equality check, so downtime spanning a boundary self-heals on the
  next tick. This is NOT the guild war bug shape.
- The 3-attempt cap reads `PlayerWorldBossAttempts` inside the transaction, so
  it cannot be bypassed by relogging - unlike the payload mirror, which is
  display only.

**Fixed: a full mailbox silently destroyed the whole reward.**
`existingMail.Count >= 50` did a bare `continue`, so a player who fought the
boss and placed in any bracket received nothing - no tokens, no gold, no log,
no telemetry, nothing visible to them or to ops. Now logged and streamed as a
telemetry event.

**Open design question, deliberately not decided here:** should an earned,
non-repeatable reward bypass the 50-item mailbox cap, or be held and
delivered when space frees up? Force-inserting would quietly break an
invariant that exists for a reason, and holding needs a retry store. Both are
design calls rather than bug fixes, which is why this pass only made the loss
visible.

**Not covered:** live end-to-end verification of a full kill. The event window
is date-gated (day 1-7 or 15-22 UTC) and today falls outside it, so exercising
a real defeat requires either clock manipulation or forcing the snapshot's
EventState - neither of which tests the scheduler that would run in
production. Static audit plus the reward-path reasoning above is what this
pass could honestly establish.

### 47. Leaderboard audit, 2026-08-01

**Fixed: every entry was named "Player".** The global leaderboard hardcoded
`DisplayName = "Player"` for all fifty rows, making it impossible to tell
anyone apart - while `PlayerRecords."Username"` sat on the very record already
loaded into the lookup dictionary two lines above. Now uses the real username,
falling back to `Player #id` because the column is nullable for accounts
created before it existed. The guild leaderboard was already correct
(`g.Name`), and the `"LocalRank"` string is a deliberate offline-fallback
placeholder, not a defect.

**Paging is correct.** `skip`/`take` are validated, the Redis call is
`SortedSetRangeByRankWithScoresAsync(key, skip, skip + take - 1, Descending)`,
and rank is `skip + i + 1` derived from the Redis index rather than the
filtered list - so a player id present in Redis but missing from Postgres
(deleted account, drift) shortens the page without corrupting the ranks below
it.

**Not a cold-start gap.** `SyncLeaderboardsAsync` runs BEFORE the 5-minute
delay, so a freshly booted server populates immediately. I initially misread
the loop order and recorded the opposite; corrected here.

### 48. Anti-cheat quarantined the dev account during a normal headless session

The live leaderboard test returned nothing, and the cause was that player 1
had `IsQuarantined` and `Quarantine_Active` both true - the leaderboard query
excludes quarantined accounts. `DevFixtureSeeder` explicitly sets both false,
so this happened AT RUNTIME during the session.

Source is the integrity-challenge path: `ConsecutiveChallengeMisses >=
AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit` sets both flags and
calls `RequestShadowBan`. The client DOES answer challenges
(`WebSocketClient.cs:269` responds on packet receipt), so this is not simply
an unimplemented responder.

That leaves two possibilities, and this pass could not distinguish them:

1. The verification hash disagrees between client and server, in which case
   every real player accumulates misses and is eventually shadow-banned.
2. The responses are correct but too slow under some conditions, in which case
   a laggy or backgrounded client is punished for its connection.

Either would be severe: quarantine silently removes a player from
leaderboards, and `RequestShadowBan` is not something a player can see or
appeal. This echoes the earlier anti-cheat finding about irreversible
penalties needing a reversal path - `--lift-quarantine <playerId>` exists and
was used to restore the fixture, but nothing surfaces that a player needs it.

Highest-priority item to investigate next: compare the client's hash
computation against the server's verifier directly, with a single challenge
round-trip logged on both sides.

### 48b. Anti-cheat challenge race - ROOT-CAUSED AND FIXED

Item 48 left two possibilities open. It was neither a hash mismatch nor slow
responses: `ComputeChallengeHash` and `XorShift32` are byte-identical on both
sides. The inputs disagreed.

The client hashes the epoch it saw in the broadcast. The server validated
against `payload.LogicEpochCounter` as it stood when the ANSWER arrived. That
counter advances on every successful checkpoint flush - and ordinary play
flushes constantly, including an explicit flush on every reroll command. So
any flush landing between broadcast and reply turned a correct answer into a
recorded miss, and enough misses meant quarantine plus `RequestShadowBan`,
invisible to the player and with no appeal.

Latency made it strictly worse, so the players most likely to be banned were
the ones with the worst connections - and the most active, since activity is
what drives flushes.

Fixed by pinning `ActiveChallengeIssuedEpoch` when the challenge is issued and
validating against that, so the answer is judged against the state the client
was actually shown.

Verified live: a session driving repeated rerolls advanced the epoch 18 times
- the exact condition that previously banned the account - and the account
stayed clean with zero challenge rejections. The same activity pattern
quarantined it before the fix.

A regression test asserts the hash genuinely depends on the epoch, which is
why pinning is required rather than optional.

### 49. Remaining subsystems: multitasking, friendlist, guild discovery, 2026-08-01

**Multitasking - audited clean.** The concern was resource duplication or
state lockout across concurrent character slots. `ProcessMultiSlotTick` swaps
a slot into the active register, runs `ProcessSubTick`, and swaps back - a
symmetric pair, so the register is always restored. Account-wide work
(`ProcessAccountTick`) runs once per tick OUTSIDE the slot loop, so it cannot
be applied three times. Slots 2 and 3 are skipped unless a real character
occupies them, with slot 0 deliberately exempt so injected virtual players
still run. Per-slot gold and loot accrue into account-wide fields, which is
the intended behaviour of multitasking rather than duplication.

One latent fragility worth noting rather than fixing: if `ProcessSubTick`
ever threw, the second swap would not run and the register would keep the
wrong slot's data while the payload believed it held slot 0. A try/finally
around the pair would make the restore unconditional. Not urgent - an
exception on the tick thread is already catastrophic - but it is a cheap
guarantee.

**Guild discovery - does not exist.** This is a feature gap, not a bug, and
the spec's "search functionality for players/guilds" has no implementation on
either side. The server exposes create, join, roster and the three
application endpoints; there is no guild list, browse or search endpoint
anywhere. Joining is BY EXACT NAME, so a player who has not been told a
guild's precise name has no way to find one at all. The application/approval
half is complete and works; only discovery is missing.

Smallest useful addition would be a paged `/api/v1/guilds/list` with an
optional name filter, mirroring the leaderboard's skip/take shape which is
already proven correct.

**Friendlist - only partially audited.** `/api/v1/friends/list` exists and
name resolution goes through the shared `HandlePlayerNames` path rather than
a second implementation, which is the right shape. Add/remove/invite flows
and online-status accuracy were NOT verified in this pass - recorded as
uncovered rather than claimed, since context ran out before they could be
exercised live.


### 49b. Item 49 follow-ups - SHIPPED

- **Slot register hardened.** The swap-back around `ProcessSubTick` is now in
  a `finally`, so an exception cannot leave the active register holding one
  slot's character while every later reader assumes slot 0. The corruption
  would have outlived the exception and been blamed on something else.
- **Dev fixture rolls real affixes.** Was a literal `{}` on every seeded item,
  so the reroll, affix and rarity UI could not be exercised without
  hand-writing a payload into the database first - which is exactly what the
  2026-08-01 live session had to do. Now goes through
  `AffixRegistry.RollAffixes` like a real drop. Verified: the fixture produced
  `attack_speed_pct@1` on a weapon, `crit_chance_pct@2` on a helmet and
  `flat_hp@3` on a chest - slot-legal, with per-affix rarities.
- **Guild discovery now exists.** `GET /api/v1/guilds/list` with `skip`/`take`
  and an optional case-insensitive `name` filter, ordered by active members.
  Paging deliberately mirrors the leaderboard's shape rather than inventing a
  second convention. Returns enough per guild - members, tier, MMR, tax, join
  type, minimum level - to decide whether to apply without a second
  round-trip. Verified live: 401 unauthenticated, and a genuinely unmatched
  path still 400s, so the route is real rather than a catch-all.

**Still open from item 49:** the client has no UI for the new endpoint yet -
this pass added the capability, not the screen. And the friendlist
add/remove/invite and online-status flows remain unaudited.

**Noted while testing, not a product bug:** deleting `EquipmentInstances` rows
leaves `characters.Equipped*Id` pointing at them, and the fixture's
"already equipped" guard then refuses to re-seed. Only reachable by editing
the database by hand, but worth knowing before the next fixture reset.

### 50. Friendlist audit, 2026-08-02

**Verified correct.** `RelationshipEngine.AddFriendAsync` validates properly
before touching anything: rejects self-friending and non-positive ids, checks
the target actually exists, and takes the existing relationship row `FOR
UPDATE` inside a Serializable transaction. Its raw SQL quotes identifiers
correctly. The command handlers dispatch through `SafeDispatchAsync` with no
tick-thread blocking. Validation living in the engine rather than the handler
is the right split - the handler cannot reach the database.

**Fixed: the friend list carried no online status at all.**
`FriendEntryResponse` returned PlayerId, Username, Level and IsBlocked, so the
list could not answer the one question a friend list exists to answer - who is
around right now. Neither the server DTO nor the client cache had the field.
Added on both sides, sourced from the live connection table rather than a
persisted column, since a stored flag goes stale the instant a process dies
without a clean logout and would then claim someone is online forever.

**Known limitation, deliberately not papered over:** the online answer is
POD-LOCAL. It reads this pod's own WebSocket table, so a friend connected to a
different pod reads as offline. That is correct for the single-pod deployment
this runs as today. A multi-pod answer needs a Redis presence key, and
inventing one inline would have created a second source of truth about who is
online - the exact class of split this codebase has already been bitten by
twice (diamonds, gold).

**Still not exercised live.** Add, remove, block and the online flag itself
were audited by reading, not by running two accounts against each other. The
mechanics are simple and the validation is visibly correct, but "looks right"
and "works" have diverged three times in this project within two days, so this
is recorded as unverified rather than passed.

### 51. Achievement rewards were silently refunded for online players, 2026-08-02

Same shape as the reroll diamond bug from 2026-08-01, found by looking for it
deliberately rather than by accident.

`AchievementEngine` credits `PlayerRecords."PremiumDiamonds"` and stops there.
For an ONLINE player the live payload owns `PremiumCurrency`, and
`StateCheckpointManager` writes it back with plain assignment
(`player.PremiumDiamonds = state.PremiumCurrency`), so the next flush
overwrote the reward with the payload's stale balance. The diamonds simply
disappeared.

Offline players were unaffected, since no payload existed to overwrite them -
which is precisely what made this hard to notice, and why the earlier session
saw an achievement grant survive while a different one would not have.

Fixed by enqueuing `BillingSyncNotification`, the same hand-off the reroll fix
uses: the tick thread is the only thread allowed to touch the payload.

**Checked and NOT affected:** `DailyLoginRewardEngine` grants at
`/api/v1/auth/login` and `/api/v1/auth/register`, both of which run before the
session payload is hydrated - so hydration reads the already-updated column.
Verified rather than assumed.

**Unresolved:** `StateCheckpointManager.EvaluateAndAwardTierAsync` also does
`player.PremiumDiamonds += diamondsAwarded`. It runs inside the checkpoint
itself and is a static method with no payload reference, so whether its award
survives the next flush depends on ordering this pass could not trace safely.
Recorded as open rather than guessed at. It is the last known instance of this
pattern.

### 52. Registration needs the backend running - not a bug

The user could not register from a self-launched Unity session. Cause: no
backend. The client alone cannot register; it needs Postgres, and the server
process listening on 8080. Verified working once started - POST
/api/v1/auth/register returned 200 with a JWT.

To play locally:
1. `docker start folk-idle-db folk-idle-redis` (Redis is optional but chat and
   leaderboards need it - and the server must start AFTER Redis, or see item
   43, now fixed by the retry loop).
2. `FOLKIDLE_DB_CONN="Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres" dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj`
3. Then enter Play Mode.

Worth considering a startup check in the client that says "cannot reach
server" plainly, since the current failure gives no indication that the
backend is simply absent.

### 53. Combat audit, 2026-08-02

**CRITICAL, FIXED: attack speed bonuses made the player attack SLOWER.**

Both attack timers used `(tickAccumulator * 100) % intervalMs == 0`, which
only fires when elapsed time lands EXACTLY on the interval. Any interval that
is not a divisor of a multiple of the 100ms tick therefore fired at the least
common multiple instead:

| bonus | intended | actual  | effect      |
|-------|----------|---------|-------------|
| 0%    | 1500ms   | 1500ms  | correct     |
| 5%    | 1425ms   | 5700ms  | 4x slower   |
| 10%   | 1350ms   | 2700ms  | 2x slower   |
| 20%   | 1200ms   | 1200ms  | correct     |
| 33%   | 1004ms   | 25100ms | 25x slower  |

Only bonuses landing on a multiple of 100 worked. `attack_speed_pct` is one of
the twelve rerollable affixes, so players were spending gold and diamonds to
make their character worse - and the same fault applied to any monster whose
authored `AttackIntervalMs` was not a multiple of 100.

Replaced with a boundary-crossing test (`HasCrossedInterval`): an attack is due
when elapsed/interval increases across the tick. Exact for any interval and
needs no extra payload state.

**Combat is NOT turn-based alternating.** Player and monster run independent
cadences off the same tick clock, which is the right model for an idle game -
attack speed only means something if the two sides are not locked in
lockstep - but it is worth stating plainly since the request assumed
alternation.

**Already working, verified:** session loot appears under the monster with
rarity in square brackets exactly as requested (`Iron Ore x3`,
`Doomblade [Legendary]`), skipping tier-0 materials. Inventory
(`UiInventoryPanel`), the village bank vault (`UiBankVaultWindow`,
`UiBankDepositCandidateRow`) and `WithdrawFromBank` all exist.

**Missing: per-monster drop preview.** Selecting a monster does not show what
it drops or the chance. The data cannot reach the client today: loot tables
are a hardcoded `_lootEntries` array inside the server's `ContentRegistry`,
and while monsters carry a `LootTableId` client-side, no loot table file ships
in StreamingAssets. Needs either a `/api/v1/monsters/{id}/loot` endpoint or a
generated `loot_tables.json` alongside the other content files. The endpoint is
probably better - drop rates are balance data and should not be a client asset
that can drift from the server's table.

**Not verified this pass:** that the bank vault round-trip actually works end
to end - deposit from inventory, then market or equip straight from the vault.
The screens and the command exist; the flow was not exercised.

### 54. Monster drop preview endpoint - SHIPPED (server half)

`GET /api/v1/monsters/loot?monsterId=N` returns what a monster can drop and
the REAL per-kill probability of each entry.

An endpoint rather than a shipped content file, deliberately: drop rates are
balance data, and as a client asset they would drift from the server table and
show players odds the server does not honour. For the same reason the rates
are read from `CombatLootEngine`'s own constants, which were made public
rather than copied - a second set of numbers in the API layer is exactly the
split that has produced three currency bugs this week.

ChancePct is the true probability, combining the 35% material roll with the
entry's share of its table's weight. Raw weights would be meaningless to a
player without the total. Equipment is reported separately because it does not
come from the weighted table at all - it rolls flat per-slot chances (melee
0.5%, ranged 0.4%, magic 0.4%, helper 0.33%), so omitting it would have
claimed monsters drop no gear.

Verified live against Malakor: `mat_demon_heart` at 35% (single-entry table,
so the full material share) plus the four equipment lines. Invalid ids 400.

**Client UI still to build.** The data is reachable; nothing consumes it yet.
The natural home is the monster selection list in `UiCombatLocationPanel`,
shown on select.

**Flaky test noted:** `Test_BreedingPair_GrantedRacePairCanBreedAndSameSexIsRefused`
failed once in a full run and passed both in isolation and on the next full
run. Unrelated to this change. Worth watching - an intermittently failing test
erodes trust in the suite faster than a consistently failing one.
