# Design audit 2026-09-28: implementation plan

> **For agentic workers:** one branch and one PR per phase, cut from `main`.
> Steps use checkbox (`- [ ]`) syntax. Every "Done when" is a check somebody can
> run. Read CLAUDE.md's load-bearing rules first; the ones each item leans on
> are named in it.

**Source:** the design audit of 2026-09-28 (a claude.ai artifact, not committed).
It was built from the code, 17 production screenshots of a fresh guest at
390px, and read-only production queries. The owner confirmed they are the only
real player, so none of this is justified by a funnel. It is justified by what a
first-time player sees, and by defects found while reading the code.

**A correction to the audit, recorded so nobody acts on it:** the audit said the
onboarding coach card "covers content mid-screen". It does not. It is
`position: fixed` at the bottom, and a full-page screenshot draws a fixed
element at its viewport offset, which lands mid-page on a long capture. Item
dropped.

## Owner decisions this plan does NOT take

These contradict an earlier owner decision or change the game's pacing. They
are listed for the owner, not built:

| # | Question | Why it is not built here |
|---|---|---|
| O1 | Starter weapon for a new account | 2026-09-23: "no starter weapon (the ~60s first kill is the design)". Stands. |
| O2 | Starter food in the larder (20-30 fish) | It removes onboarding step 1 ("Fill the larder") and changes the first-minute pacing. The audit recommends it; the owner decides. |
| O3 | Pause the 90-day season rollover until there is a population to race | Changes `SeasonalRotationEngine` and what a season means. Owner decision. |
| O4 | Bottom tab navigation + progressive screen unlocks (audit H-1/H-3) | Large client restructure that touches every geometry checker. Needs the owner to agree on the five tabs before anybody builds it. |
| O5 | What the Chronicle pass should become (see 1.3) | This plan only hides it and records the defect. |

## Owner answers, 2026-09-28 (second round)

- **O3, seasons: pause the rollover, and give the owner full control of it.**
  The owner wants to be able to reset or adjust a season whenever they like,
  for example to test from zero with friends. Built first, as Phase S, because
  the active season ends **2026-11-02 09:40 UTC** and would wipe the owner's
  level-96 account.
- **O2 + O1, start: food AND a weapon.** This REVERSES the 2026-09-23 "no
  starter weapon" decision, and the owner made that call knowingly. The
  tutorial must make the player do both actions by hand, in the guided style
  of mobile-game tutorials: the screen is dimmed, one control is lit, and
  nothing else can be pressed. Phase T.
- **O4, navigation: yes to a five-tab bottom bar**, Village possibly among the
  tabs. The owner asked for the design to be proposed to them, not built
  blind. Phase N.
- **O5, the Chronicle pass: stays hidden.** The owner's direction for later is
  cosmetics - rare profile avatars, profile frames and skins - as rewards,
  rather than a battle pass.

### Phase S - season control (branch `feat/season-control`)

- `SeasonalEraRecords.IsRolloverPaused`, an additive migration. The migration
  sets it to TRUE on the active era, because pausing is the owner's decision
  and a deploy must not depend on somebody remembering to press a button
  before Nov 2. A new era inherits the flag of the era it replaces.
- The cron skips a paused era. Nothing else reads the end date, and the
  client shows none.
- Admin only (`IsAdmin`), in Settings' admin panel:
  - `GET /api/v1/admin/season`: era id, end, paused.
  - `POST /api/v1/admin/season`: `pause` / `resume` / `setEnd` (epoch
    seconds).
  - `POST /api/v1/admin/season/end-now` with the typed phrase `END SEASON`:
    runs the normal rollover immediately, through the same code path as the
    cron. It wakes the cron instead of running in the request, because the
    rollover disconnects every client, the caller included.
- "Start from zero with friends" needs no wipe tool: a new account IS a start
  from zero. A full wipe that also takes villages, diamonds and ancestors is
  deliberately NOT built; that is a database restore, not a button.

### Phase T - starter kit and a guided first minute (branch after S)

- Registration grants a Normal region-1 weapon (unequipped, in the chest) and
  a small stack of fish (in the chest, not in the larder). Both registration
  paths, as with the tools.
- The tutorial's first two steps become GUIDED: "put the fish into Auto-Eat"
  and "wear your weapon". While a guided step runs, a full-screen dim layer
  covers the app. The one control the step needs is lifted above it with a
  pulsing ring and a short caption, every other click is swallowed, and the
  step advances only when the server state proves the action happened (larder
  count > 0, `EquippedWeaponId` > 0). There is always a small "Skip tutorial"
  so a returning player is never trapped. Then "Pick a fight" stays a normal
  coach step.
- `tutorial.ts` already exports `isInteractionAllowed()`; check what it gates
  before building a second mechanism.
- `exercise.mjs`'s new-account section has to follow the guided path.

### Phase N - five-tab navigation (proposal first)

Build only after the owner approves a rendered proposal. The five tabs
(proposal, see the chat for the reasoning): **Home - Fight - Gather -
Character - Village**, and "More" through the header menu. The phone gets a
bottom bar; the desktop keeps its header.

---

## Phase 1 - Truth and first-hour polish (branch `feat/audit-phase-1`)

Small, independent items. Everything here is either a defect or text a player
should never have seen. No balance change.

### 1.1 Village upgrade costs come from the server (DEFECT)

**Problem.** `villageCostLabel` in `client_web/src/lib/net/commands.ts` is a
hand-kept copy of `VillageManagementEngine.TierMaterials` and it is wrong:

| Tier | Client says | Server charges |
|---|---|---|
| 0 | Birch Log + **Malachite Ore** | `birch_log` + `copper_ore` |
| 1 | Willow Log + **Hematite Ore** | `willow_log` + `iron_ore` |
| 2 | Acacia Log + Sulfur Ore | `acacia_log` + `sulfur_ore` |
| 3 | Frostpine Log + **Cobalt Ore** | `frostpine_log` + `silver_ore` |
| 4 | Ebon Log + Darksteel Ore | `ebon_log` + `darksteel_ore` |

It also prices Town Hall and the Crafting Workshop on `level / 5`, while the
server uses `(level / 2) * 5` for structural buildings. The Workshop's extra
rare-log charge (`cost / 10` golden logs) is not shown at all. The header row
(Wood / Stone / Iron ore) shows legacy stocks that no upgrade spends. A player
reads a price they cannot check against what they hold. This is the codebase's
dominant bug class: two copies of one truth.

**Fix.** One server quote, used by both the handler and a new endpoint.

- [ ] `VillageManagementEngine.QuoteUpgrade(int buildingId, int currentLevel)`
  returns the cost lines `(ItemId, Quantity)`, gold included. The upgrade
  handler charges exactly the quote's lines (refactor, no behaviour change;
  read the whole handler first, CLAUDE.md "multi-line initialisers").
- [ ] `GET /api/v1/village/quote`: for every building, the current level, the
  next level's lines, and how much of each line the player holds (gold from
  `CommodityRecords["gold"]`, materials through the same unified inventory
  lookup `TryConsumeUnifiedAsync` spends from). A GET, read-only, no lock.
- [ ] `Village.svelte` renders the quote: "120 / 100 Birch Log" per line, a
  missing line in the danger colour, and the Upgrade button's title says what
  is missing. Header stock row replaced by nothing (the lines carry it).
- [ ] Delete `villageCostLabel`. Keep `villageGoldCost`/`villageMaterialCost`
  only for the Wiki table, which states quantities, not names.
- [ ] Invalidate the quote after an upgrade and when the infrastructure
  notification arrives.
- [ ] Test: `VillageQuoteTests` - for every building and levels 0..12, the
  handler charges exactly what `QuoteUpgrade` returned (materials and gold
  drop by the quoted amounts). This is the mechanical guard against the next
  drift.

**Done when:** a fresh account's Village shows `copper_ore` (by its catalogue
name) as the ore cost, and `VillageQuoteTests` passes.

### 1.2 Achievements have names

**Problem.** Progress shows "Achievement #1", "#2"... with no description. The
names exist only as constant names in `AchievementMilestones`.

- [ ] `AchievementMilestones.TitleFor(id)` / `DescriptionFor(id)` (Monster
  Slayer, Treasury, Forging, Logistics).
- [ ] `/api/v1/achievements/snapshot` carries `Title` and `Description`;
  `AchievementEntry` in `rest.ts` gains them.
- [ ] `Progression.svelte` shows them.

### 1.3 The Chronicle pass is hidden, and its defect is written down

**Problem.** The panel claims milestones by typing an index 0-49 beside text
saying which ones are claimed "is not exposed by any endpoint". Reading the
claim handler found worse: the free track grants
`BaseItemId = "chronicle_free_{n}"` and **no such item exists in
`items.json`** - every free milestone mints an equipment row the catalogue does
not know. The premium track costs 950 diamonds.

- [ ] Remove the panel from `Progression.svelte`. Leave server code alone.
- [ ] Record the phantom item under "Known dead code" in
  `CURRENT_IMPLEMENTATION_STATE.md`, with O5 as the open question.
- [ ] Production check (read-only): count `EquipmentInstances` with
  `BaseItemId LIKE 'chronicle_free_%'` and who owns premium. Record the numbers.

### 1.4 Developer voice out of the player's UI

Text meant for a code comment, shown to players:
- [ ] Gathering "Hauled this session" paragraph ("a working node and a broken
  one looked identical"). Replace with one sentence for the player.
- [ ] Progression "Live from the state feed: ..." line. Remove.
- [ ] Settings support: "no ticketing system behind this endpoint". Replace
  with "Thanks - we read every message." only if that is true; otherwise
  "Sent." The owner reads these, so "Sent. The developer reads every message."
- [ ] GuildOps war contribution "numeric commodity id - no picker endpoint"
  (hidden UI, but fix the words anyway).
- [ ] Grep `endpoint|server-side|packet|state feed` in visible markup once
  more after the edits.

### 1.5 One name per attribute

**Problem.** Character shows STR/DEX/CON/LCK at the top and
Might/Finesse/Vigour/Fortune in the panel below, under a second "Attributes"
heading.

- [ ] Top summary uses Might / Finesse / Vigour / Fortune.
- [ ] Rename the lower heading so the screen has one "Attributes".
- [ ] Wiki: keep the STR/DEX/CON/LCK abbreviations only where it explains the
  mapping.

### 1.6 Combat reads as "the next step", not as a catalogue

- [ ] Hide the Drops panel until a monster is selected (today it renders an
  empty heading).
- [ ] Render the unlocked regions and the FIRST locked region (banner and
  rows, dimmed, as today). Collapse every further locked region into one line:
  "3 more regions beyond - each opens when the one before it falls."
- [ ] When the larder is empty (the same three `Food*_Count` fields the tutorial
  reads), show a warning above the monster list: "Your larder is empty - a
  fight without food is usually lost." with a button to Auto-Eat
  (`requestScreen('larder')`). Fight stays enabled: it is advice, not a gate.

### 1.7 Empty states offer the action

- [ ] Auto-Eat "No food in the chest": a button "Go fishing" that navigates to
  Gathering (`requestScreen('gathering')`).

### 1.8 The phone header gives the screen back

At 390px the header is ~230px of an 844px screen.
- [ ] At `max-width: 40rem`: the event chip shows its name only (effect stays in
  `title` and in a tap-to-expand), the connection phase is hidden while `live`
  (shown when reconnecting/failed), Sign out moves out of the header.
- [ ] Settings gets a Sign out button (the only place on a phone).
- [ ] Desktop header unchanged; `screens.mjs` `NON_DESTINATIONS` still lists
  "Sign out".

### 1.9 Book of Deeds says what it means

- [ ] `first-blood` counts kills (`TotalKills >= 1`, target 1), not level. It
  read "1/2" before any fight.
- [ ] `stock-larder` body agrees with the tutorial: without food the FIRST
  monster wins, not "the fourth monster of a region".

### 1.10 Starter tools are worn, not in the chest

**Problem.** Registration grants the three tools but leaves every slot empty,
so a new Character screen shows eleven empty slots and Gathering reads
"axe 0 - pickaxe 0 - rod 0".

- [ ] `StarterEquipmentGrant.Seed` returns the instances; both registration
  paths save, then set `EquippedAxeId`/`EquippedPickaxeId`/`EquippedRodId` on
  the account's first character and save again inside the same transaction.
- [ ] Test: a registered account's first character wears all three tools.
  Tool slots are 8/9/10 - CLAUDE.md "ELEVEN equipment slots".

**Verification for the whole phase:** `dotnet test` (Docker up), `npm test`,
`npm run check:ratchet`, then `run-dev.ps1` and `npm run exercise`, and the
geometry checkers at 390px (`check:clipping`, `check:overlap`, `check:touch`).

---

## Phase 2 - Less busywork, clearer goals (branch `feat/audit-phase-2`)

Order: 2.2, then 2.3, then 2.1 only if it still looks worth it.

### 2.1 The larder refills itself from the chest - DEMOTED, build last

**Re-measured while Phase 1 was built, and the audit overrated it.** A slot
holds `LarderLimits.SlotCapacity` = **9,999** bites, and the Auto-Eat screen
loads a whole stack in two taps (amount "all", then `+`). Three full slots are
roughly 30,000 bites, which is days of fighting. So a refill only helps a
player who loaded a small amount on purpose, or one who fishes and fights in
turns. That is real but small. 2.2 goes first. If this is built, it is an
off-tick refill request: the tick enqueues "slot N ran dry on food X",
`LarderEngine` moves up to capacity from the chest, and the existing
`LarderSlotUpdateQueue` hands the count back. It does not touch the offline
path, because 30,000 bites already outlast the 12-hour cap.

The original sketch follows.

**Problem.** Food is the difficulty ceiling (by design), but keeping it
stocked is manual: the player moves food chest -> slot with `+` presses. An
empty larder is the most common halt.

**Design.** A per-account toggle "Refill from the chest", default ON. When a
slot is eaten down to zero, the server moves more of the SAME food from the
chest into that slot, up to the slot's last loaded amount. It never picks a
different food and never touches a slot the player emptied by hand.

- [ ] Find the auto-eat consumption site(s) in the tick AND the offline path
  (`OfflineSimulationEngine` models food too - CLAUDE.md "three paths grow a
  level" is the same lesson: every path has to be told).
- [ ] The chest is `CommodityRecords`; the tick must not do DB work
  (`CheckpointOffTickGuardTests`). Refill therefore happens where larder
  counts are already reconciled with the database (the checkpoint, or
  `LarderEngine`), not per bite. Measure how far a slot can run dry between
  two checkpoints before choosing; if that gap matters, preload a buffer.
- [ ] Wire: one flag on the settings path the client already uses.
- [ ] Tests: tick and offline paths both refill; a hand-emptied slot stays
  empty; food never appears that the chest did not hold.

### 2.2 The Map becomes a home screen

**BUILT 2026-09-28 (PR on `feat/audit-phase-2`).** Two cards, not three:
"Right now" and "Closest goal" (`HomeCards.svelte`, `homeGoal.ts`). "Next" was
dropped because the onboarding coach already ranks exactly that, and a second
list would have been a second source for one truth. `exercise.mjs` checks the
cards and that Go leaves the map. 2.3 (ETA) is not built yet.

**Design.** Keep the painted map, smaller, and add three cards under it:
- **Now** - each character: what it is doing, rate, and the halt reason with
  one fixing action.
- **Next** - the first due item from `tutorialObjectives.ts` (it already ranks
  them), or the tutorial step while onboarding runs.
- **Goal** - the unfinished deed with the highest completion, with a Go button.

Client only. `exercise.mjs` clicks the Map's plates; keep them.

### 2.3 Time to goal

**BUILT 2026-09-28, differently from the sketch below.** The Goal card shows
"about 3 h at this session's pace" (`lib/ui/pace.ts`, tested). It
extrapolates the deed's OWN counter as it moves while the map is open,
instead of modelling XP, kill or gathering rates, because that would copy
three server formulas to the client. It says nothing until it has watched
for 2 minutes and seen progress, and nothing past 30 days. The Village line
ETA is not built, because it would need the gathering rate formula on the
client.

- [ ] Deeds and the Goal card show an ETA where a rate exists (kills/h from the
  session, gathering from the node's seconds per unit and mastery).
- [ ] Village shows "at your current gathering rate: ~40 min" for a missing
  material line when that material's node is the character's activity.

---

## Later phases (outline only; plan each before building)

- **Phase 3 - core loop:** regional contracts (audit E4), Boosts merged into
  Auto-Eat, genetics under one screen, chest sale visible on the Market
  (mind the two gold paths). Needs O4 first for the navigation shape.
- **Phase 4 - progression:** Deeds absorbs achievements, collection log,
  statistics with rates/records/timeline (`player_funnel_events` already holds
  per-player milestones), season chronicle (O5), race roles in the roster.
- **Phase 5 - polish:** rare+ drop reveal, unlock cards, region silhouettes for
  missing monster portraits, Delve theme, folklore lines.
- **Phase 6 - endgame:** boss challenges, weekly Deep seed, cosmetic rewards.
