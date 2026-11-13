# FolkIdle Task Board

**What is open, and only that.** Rewritten 2026-10-07. Tasks 1-110 and
every write-up behind them (investigations, measurements, the reasoning
for each decision) are kept verbatim in
[`archive/TASK_BOARD_1-110.md`](archive/TASK_BOARD_1-110.md). Code comments
and older docs that say "TASK_BOARD task N" mean a section of that file.

New work gets a new number from **111** up, in a section below. When a task
is done, move its section to the archive (or a new archive file) rather than
leaving it here marked DONE - a board that is mostly finished work is how
the old one reached 420 KB and three contradictory START HERE blocks.

## State on 2026-11-02

- **Production** is 1.0.1557 / client 1.53.0 (see the top of
  `architecture/NEXT_STEPS_BACKLOG.md` for what each release carried).
- **Closed since 2026-10-30** (still listed below until moved to the archive,
  each marked): 128 (CI runs vitest, #231), 132 (the Hearth half, #236,
  client 1.41.0), 119 (the findings were the checker's own - audit
  2026-11-02). **133 is largely built**: 3.1, 3.3 (utility half), 3.4 Refine,
  `crushing_blow_pct` and `thorns_pct` (3.6) are live; `double_hit_pct` was
  dropped by design (roadmap 6.2a). The 3.5 re-measure is what is left.
- Also shipped and not on this board: herbs and potions, the Ascension reset,
  the Book of Deeds, the diamond rework, loot chests, the dungeon gates, the
  playtest bots (backlog, 2026-10-31 to 2026-11-02).
- **What is next** is 134 (phase 2b, the per-swing simulator) and 135 (affix
  families and signatures), plus the measurement batch.
- **Update 2026-11-03:** the rebirth experiment ran (PLAYTEST_BOTS_REPORT.md
  0.6.1). The dungeon stall is the region gate and the stuck idle bot was a
  bot login defect (fixed). Still to run, overnight: casual + idle at x20,
  then the controlled rebirth experiment (0.6.2). No rebirth rule changes
  until then.

## State on 2026-10-30

- **Production** runs `main` through PR #229 (**server 1.0.1356, client
  1.39.1**): the War skill tree is live for every player (#225-#229), on top
  of the stats fixes #197-#212 and the dodge curve #200.
- **The plan is [`design/ROADMAP_2026_10_30.md`](design/ROADMAP_2026_10_30.md)**
  (#230). Each task below names the roadmap section that holds its full plan.
  The roadmap's section 9 collects the owner decisions, each with a
  recommendation.
- Tasks 121-127 are closed and moved to
  [`archive/TASK_BOARD_111-plus.md`](archive/TASK_BOARD_111-plus.md), with
  the PR each shipped in. 120 stays open for the steps #209 did not cover.

## State on 2026-10-14

- **Production** runs `main` through PR #181 (1.0.1228, client 1.27.0):
  design waves 1 and 2 (task 118), on top of the bloodline ceiling (#179)
  and Chernobog (#178). The 2026-10-07 state below is kept for its history.

## State on 2026-10-07

- **Production** runs `main` through PR #139 (1.0.1120): the balance pass
  (slower pace, harder bosses, ascension two regions ahead, Demonic trophy,
  fusion as a gold sink, cheaper Deep start), the owner's playtest fixes, and
  the quest line tutorial. Details in `docs/architecture/NEXT_STEPS_BACKLOG.md`.
- **Tasks 1-110 are closed** except the entries below. Two were dropped by
  the owner: **58** and **86** (the deterministic affix step, 2026-10-07).
- **Built 2026-10-07 on `claude/larder-46-deep-board`:** rations (task 111
  below, "fishing must matter again"), task 46 (compressed state frames), the
  Deep's week-1 numbers, and this rewrite.

## How to work

The rules that held across the old board, kept because each one cost
something to learn:

- **Check git before calling board work open.** Task 21 was reported open
  on 2026-10-07 from a stale line; it had been merged as PR #16 two weeks
  earlier. `gh pr list --state all` first.
- **Verify gameplay with `npm run exercise`**, not with `smoke:screens`.
  Re-seed (`--seed-dev`) between runs; a spent villager pool skips steps.
- **Balance and economy numbers are measured, then proposed to the owner.**
  The council plugin was removed on 2026-10-07 (it was down every time).
  For Ascension, `AscensionCalibrationHarness` fights a real character from
  a restored production copy (set `FOLKIDLE_CALIBRATION_DB`).
- **Once the owner approves a batch, carry it to production** without
  re-asking at every step.
- **One PR per batch, not a stack.** Stacked PRs merged into their base
  branches instead of `main` (#121/#122).
- **Never `git reset --hard` in the main tree** - the owner keeps
  uncommitted work there. Test merges go in a worktree on `D:`.

## Open

In the roadmap's order (section 1). Do them top to bottom unless the owner
says otherwise.

| # | Task | Roadmap | Size |
|---|---|---|---|
| 1 | 128. CI runs the client unit tests | 4.1 | S, done (#231) |
| 2 | Board tidy (this rewrite) | 4.2 | S, done |
| 3 | 120. `exercise` flakes | 2.1 | M, done |
| 4 | 129. Flaky server tests | 2.2 | S-M |
| 5 | 37, 111, 130, 131. Measurement batch | 3.1-3.4 | S each |
| 6 | 132. Phase 4 - Hearth | 7 | L-XL, done (#236) |
| 7 | 133. Phase 3 - affixes | 6 | L, largely done - see 133 |
| 8 | 112. Malakor re-measure | 3.5 | M |
| 9 | 134. Phase 2b - the per-swing simulator | 5 | XL |
| 10 | 135. Phase 5 - affix families and signatures | 8 | L |
| - | 118. UI wave 3, when the event art exists | 4.4 | M |
| - | 119. Safe-area triage, between phases | 4.3 | M, done (checker bug) |

### 128. CI runs the client unit tests - TOOLING (roadmap 4.1) - DONE

**Done in #231**: `deploy.yml`'s client job runs `npm test`. Move to the
archive at the next tidy.

The `client` CI job never ran vitest, so four guard tests stayed red on
`main` from #216 until 2026-10-30. **In PR #231**: `npm test` after
`check:ratchet`. Acceptance: a deliberately broken guard turns the PR red,
and the revert turns it green.

### 129. Flaky server tests - BUG (roadmap 2.2) - DONE (2026-11-14)

**Acceptance met:** five consecutive full local runs on main (6c248a31) were
green, 2241/2241 each, 8.5-8.8 min (2026-11-13/14 night). Every class that
runs a live tick or touches the static loot queues sits in the Postgres or
the "Static loot queues" collection (checked by grep; no free-standing one
left). If a parity row fails again, start from that collection rule.

`OfflineCombatParityTests` and
`AutomationAutoFuseTests.TheWorker_FusesTheStacksARequestsDropsLandedIn`
fail only under full-suite load. 2026-11-02: a different parity row ("lifesteal
r2 hungry") failed once in a full run and passed 4 of 4 runs of the class on
its own - the same shape. Suspect the process-wide static queues
(`CombatLootEngine.DropRequestQueue`, `GatheringGrantQueue`) shared by test
classes that xUnit runs in parallel. Acceptance: 5 full local runs green.

### 130. After the switch: what players build - MEASURE (roadmap 3.3)

Due 1-2 weeks after 2026-10-30. Read-only query over `player_skill_tree`
and `gold_spend_daily` (category 12, Respec): nodes bought, fork sides,
keystones, gold respecs. Compare with the harness's predicted best builds.

**First read 2026-11-13** (prod, `player_skill_tree`, the three real accounts):
- **Keystones:** Ruin - Mivoru and (pro)boss; Perfect Sequence - Mivoru (with
  Ruin, so two keystones); Golden Fleece - Tomda; **Living Fortress - nobody**.
- **Mivoru:** War only, no Fortune column at all; Might/Precision/Guard deep
  (Edge, First Strike, Executioner, Heavy Hand, Keen Eye, Cruel Edge, Echo,
  Steady Hand, Vitality, Evasion at 5), affinities Power/Precision/Guard 3.
  No Hearth.
- **Tomda:** the widest build - every column of War partly, Fortune to Golden
  Fleece, and nearly every Hearth node at 1-5 (Strong Blood 5, Nursery 3);
  all four affinities at 1. Points spread thin rather than to the gates.
- **(pro)boss:** Ruin build (Might column full) + Scavenger/Plenty, and a deep
  gathering Hearth (Sharp Axe, Steady Swing, Prospector, Patient Line, Still
  Water, Quick Hands, Batch Work, Salvager, Studious, Homebody at 5).
- **Gold respecs (category 12): none ever.**
Read: Ruin is the consensus damage keystone; Living Fortress is unpicked
(its block-cap rise was also mis-shown on the stat sheet until #387). Tomda's
spread suggests the gates and fork costs are not explained enough to steer a
build - a "recommended path" hint is a candidate if the owner wants it.
Still to do: compare with the harness's predicted best builds.

### 131. The gathering economy after #211 - MEASURE (roadmap 3.4)

**First read 2026-10-30:** gathered per day 2.0-2.7M before #211, 2.69M on
10-29 (one day after). Too early; re-run in a week with 111.

**Second read 2026-11-13** (`material_flow_daily`, Gathered, per player per
day): Mivoru 0.40M (10-20..28) -> 0.85M (10-29..11-04) -> 0.88M (11-05..12);
Tomda 1.14M -> 1.32M -> 2.18M; (pro)boss 0.85M -> 0.63M -> 0.88M. Server
total 2.4M -> 2.8M -> 3.9M a day. #211 roughly doubled Mivoru at once; Tomda's
later jump lines up with the Hearth half (phase 4, his tree is Hearth-heavy -
see 130). Warehouse-cap losses fell from ~180k to ~80k a day after 11-08.
Spending is spiky (3.9M and 6.5M on 11-03/04 - the Forge and Hearth week)
but stays under what is gathered. No sign of runaway: the extras multiply,
the floors hold. Re-read once the Skill aptitude milestones (gather yield
+5/10/15%, live 2026-11-13) have a week.

Material flow per active player before and after #211 (the extras now
multiply). Run it with 111's queries. It feeds Hearth's D2.

### 132. Phase 4 - the Hearth half of the skill tree - BUILD (roadmap 7) - DONE

**Shipped in #236 (client 1.41.0)**; `HearthTreeRegistry` wires 24 effects,
with Green Thumb pending (no herbalism node, #245). Move to the archive at the
next tidy. The text below is the plan as written.

Gives back the craft time, gathering speed and offline bonus the switch
took. Measure first (step 4.0, D2). The rank buffer decision (7.2) is the
owner's. Masterwork waits for phase 3.

### 133. Phase 3 - the affix rework - BUILD (roadmap 6) - LARGELY DONE

**Built (2026-10-30, roadmap 6.2/6.2a):** 3.1 the damage family (#240), 3.3
the ring/amulet utility half, 3.4 Refine (#244), 3.6 `crushing_blow_pct` on
gloves and `thorns_pct` on the chest (thorns back in rolls after 3fa32b5b
priced it per swing offline). `double_hit_pct` was **dropped** (roadmap
6.2a: a full repeat at chance p has the same expectation as Crushing Blow, so
it would be a second name for one stat). **Left:** 3.5 - re-measure the tree
harness, the walls and Malakor (task 112) now the gear has moved.

Includes the ring's pool (owner decision D4, from task 125). Keep the
`KNOWN_AFFIX_IDS` order: it is a wire index.

### 134. Phase 2b - one combat model, the per-swing simulator - BUILD (roadmap 5)

The biggest engineering risk: a rewrite of the offline combat path. It
unlocks Bleed, Momentum and similar mechanics.

### 135. Phase 5 - affix families and signature affixes - BUILD (roadmap 8)

Needs phase 3, and is best after 2b. **2026-11-03: 5.1 families built (roadmap
8.2 status); the owner picked signatures S1-S8 + S10 (5.2), built after 2b.5
with the four stateful mechanics.**

### 37. The Deep - measure the retune (PAST DUE since 2026-10-14, roadmap 3.1)

**Re-measured 2026-11-13 (read-only, gold_income_daily / gold_spend_daily,
ISO weeks):**

| Week of | Gold minted | Deep spend | Deep share | Deep payers |
|---|---|---|---|---|
| 10-12 | 3.34B | 1.37B | 40.9% | 2 |
| 10-19 | 3.79B | 0.02B | 0.5% | 1 |
| 10-26 | 7.55B | 1.83B | 24.2% | 1 |
| 11-02 | 14.75B | 0.92B | 6.3% | 1 |
| 11-09 (part) | 10.65B | 0.47B | 4.4% | 2 |

The Deep's spend recovered after the retune (0.5% to 24%), then its SHARE
fell again, mostly because minting quadrupled: offline kills (source 2) went
2.2B -> 8.6B a week, auto-salvage 1.4B -> 3.5B, and the new loot chests (14)
and Gold dungeon (15) add 1-2B each. The biggest sinks the week of 11-09
were the guild (5.1B) and the Deep (0.5B); villager recruits ran ~1.4B a
week through October. Balances: 2.8B / 0.78B / 0.25B gold on the three
accounts. **Recommendation: leave the Deep alone; the open question is the
whole gold economy (a 6x rise in weekly minting in five weeks against sinks
that did not grow) - an owner decision, recorded in
`docs/design/GAME_REVIEW_2026_11_13.md`.**

**Measured 2026-10-30, acted on (PR `claude/deep-reward`).** Week 1 file:
deepest floor 23 (yes), one payer. Fresh read-only query: a second payer
(Tomda, 3.65M, floor 10); the diamond ceiling held (60/60). But Deep spend
fell from 41.2% of the gold minted (week of 10-12) to 0.8% in the weeks of
10-19 and 10-26: depth paid nothing the shared 60-diamond ceiling had not
already paid. Owner decision: toll x1.35 -> x1.25 and the Deep's own 20
diamonds a week (floors 10-19, 2 each). **Re-measure the Deep's share of
minted gold two weeks after that deploys.**

The retune (stake 0.2%, toll x1.35 a floor, 1 diamond per new deepest floor
of the week) went live 2026-10-07. Week 1 before it: one payer, 683.8M spent,
115.5% of the gold minted that week. The scheduled task **"FolkIdle Deep
retune capture"** on the owner's PC writes
`D:\FolkIdleBackups\deep-retune-week1.txt` on 2026-10-14. Read it for: a
deepest floor past 13, a second payer, diamonds paid inside the cap. Full
history: archive section 37.

### 111. Rations - three weeks of data (live since 2026-10-07, roadmap 3.2)

**Measured 2026-10-30 (read-only, the 3 players at level 95+).** Rations
cost nothing yet: every larder holds about 3 x 10,000 fish, and fish
gathered outruns fish spent 5-15x a week (week of 10-19: 4.65M gathered,
0.25M spent). Every player runs 1 fighter and 2 gatherers all the time.
Hunger time is not recorded anywhere, so it cannot be read directly.
**Recommendation: no change yet.** #211 changed gathering yields on
10-29; re-measure with 130/131 in a week, then decide `RationIntervalTicks`
with the owner.

From region 2 a fighting character eats one ration every 10/8/7/6 s
(regions 2-5): a fish of the monster's region or later, older fish at double
per region behind, no heal. Unpaid = hungry, kills pay half XP and gold.
`FoodRegistry` ("RATIONS") has the reasoning.

- After deploy, query the larders of every active player (level 20+) daily
  for a week: does anyone go hungry, and does anyone fish? The level-78
  account held 3 x 9,999 tier 1-2 fish on 2026-10-07; in region 4 those cost
  4 (tier 2) or 8 (tier 1) fish a ration, about 2,100 or 4,100 an hour, so
  that whole stock is roughly 12 hours of fighting. Expect that player to
  meet hunger within a day of the deploy - that is the point, but watch it.
- `GatheringShareTests` still models food as damage taken only. Add the
  rations to its food column so the instrument reports what the game does;
  expect the share to rise, and re-check the 10-40% band.
- If hunger hits too hard, the lever is `RationIntervalTicks`, not
  `HungryRewardPct` - halving pay is the signal, the interval is the cost.

### 112. Re-measure Malakor's Ascension after power changes (after phase 3, roadmap 3.5)

**Re-measured 2026-11-02** (roadmap 6.2a item 4): the ladder had drifted
(owner x47.6, maxed x68-77) and is re-solved to A3 = x40, x1.21 a step. Run
it again after the next power change.

Malakor's ladder (`BossAscensionRegistry.MalakorAttackMultiplier`) is solved
against measured headroom: about x19 attack for the owner's character on
2026-10-07, about x34 for a maxed profile. Anything that makes characters
stronger (new affixes, inheritance caps, skills, rebirth bonus) moves those
numbers. Restore a production dump into a local database, set
`FOLKIDLE_CALIBRATION_DB`, run the harness, and move the constants if the
targets drift (owner clears ~A2-A3, maxed ~A5-A7, A10 about 2x maxed).
Regions 1-4 still use "A10 = the boss two regions ahead".

### 115. Breeding a new player can follow

The owner, 2026-10-10: breeding is hard to get into, and how aptitudes climb
(villagers arrive with at most 20, the cap is 50) is not explained. Today:
villagers roll `2 + rand(0..Inn x 1.5)` capped at 20; a child takes each
aptitude from one parent (weighted by value), then +1 at 25% + Grounds
level, -1 at 10% (swapped when inbred), 5% epic +1 to all; Grounds 4/7/10
lets 1/2/3 aptitudes take the better parent outright (`BreedingAptitudes`).
Past 20 it climbs ~+0.2-0.3 a generation, so 20 -> 50 is 100+ generations.
**Done 2026-10-14** (owner decisions the same day):
- seasons removed outright - no calendar, no admin "END SEASON", no placement
  rewards; every reset is one player's rebirth;
- 20 rebirths; skill points, Renown, avatars, title and Phoenix stop at 10
  (`RebirthRules.RewardedRebirths`), rebirths 11-20 pay only the ceiling;
- the **bloodline ceiling** `20 + 30 x rebirths / 20` caps what breeding can
  produce (`BreedingAptitudes.CeilingFor`), so 50 takes twenty rebirths rather
  than however many hours a run holds (measured: 77-119 generations 25 -> 50);
- breeding is surfaced near the first rebirth (quest step and NEW card at
  level 40), but works as soon as the Grounds stand;
- the Breeding screen redrawn: Marry/Raise/Carry path, the child drawn on one
  track per aptitude with a verdict, price and button under it, rules behind a
  toggle, a "born" banner that sends you to the Hall; the Hall tags benched
  children "Field to raise"; the Rebirth screen lists who carries;
- `RebirthTests.ABredChildMarkedKeepSurvivesARebirthUnchanged` covers the chain.

Still open, proposed and not approved:
- a "suggest a partner" button (server-ranked, never a client copy);
- the first selectable aptitude from Grounds level 1, not 4;
- no -1 mutation on a selected aptitude - measure with `BreedingClimbTests`
  first, since the ceiling now bounds the climb anyway.

Tasks 113, 114, 116 and 117 are closed: see
[`archive/TASK_BOARD_111-plus.md`](archive/TASK_BOARD_111-plus.md).

### 118. Design waves - "less static" (waves 1 and 2 DONE, wave 3 open)

Agreed with the owner 2026-10-14: three waves, one PR each, each measured
with `check:perf` and the geometry checks. Write-up of what shipped:
`docs/architecture/NEXT_STEPS_BACKLOG.md`, 2026-10-14.

- **Wave 1 - motion. DONE, PR #180.** Screen cross-fade, the wallet counts
  up, a birth catches light.
- **Wave 2 - hierarchy. DONE, PR #181.** Combat's best pick and yield-first
  rows; global `.dim/.tiny/.small` with a higher reading floor.
- **Wave 3 - seasonal events as a token layer. OPEN, next.** The mechanism
  already exists: `eventTheme.ts` sets `data-event` on `<html>` from the
  wire's `SeasonalEventId`, and `app.css` re-points tokens under
  `:root[data-event='samhain']` (boot.js repeats it before the bundle
  loads). So the work is content, not plumbing:
  - read the samhain block and list every token it touches; make that list
    the contract every event block must fill (accent, glow, header, page
    background, loading art);
  - add blocks for the winter, spring and summer events, with art from the
    owner (backgrounds and loading screens; the Halloween set in
    `client/Assets/Images/WithWhiteBackground/` shows what one needs);
  - a test that every event key the server can send has a complete block;
  - acceptance: switching `data-event` by hand recolours the whole game, and
    `check:clipping`, `check:touch` and `check:perf` stay green under each.
  Which events exist and when they run is server content - ask the owner
  before inventing dates.

### 119. `check:safearea` - triage the existing findings - DONE (2026-11-02)

**The findings were the checker's.** `markPinned` tagged `display: contents`
boxes (Game.svelte's `main.screen-main` reports an all-zero rect, so it never
"moved"), and every element below it inherited the tag through
`closest('[data-sa-pinned]')`. Fixed in the checker: 49 of 62 pairs -> 0. A
negative control (body's bottom padding removed) still finds 3. The check
already fails on any finding, which is the ratchet at zero.

Measured 2026-10-14: findings on 48 of 60 screen/orientation pairs on
`main`, almost all "bottom ... (pinned)" - content under the gesture bar at
rest (Combat's Fight column, Gathering totals, World Boss, the Delve). It
was not caused by the design waves (47 on that branch). Decide per finding
whether it is real (pinned by scrolling and looking, as `client_web`'s
rules say) or the checker's own, fix the real ones, and make the check a
ratchet so the count cannot grow unnoticed.

### 46 follow-up. Confirm the compression ratio live

`/metrics`: `folkidle_ws_deflate_output_bytes_total` over
`folkidle_ws_deflate_input_bytes_total` after a day of real sessions. The
test measures ~39x on a synthetic frame stream; anything under ~5x means
real frames differ more between seconds than the test assumes. Archive
section 46 has the numbers and the design.

### 45. Haptics and local notifications - needs the owner's phone

On a NEW APK: a first clear vibrates; backgrounding schedules the reminder.
Archive section 45.

### 47 leftover. Larder run-out notification

Needs a server-computed `ProjectedLarderSeconds` on the wire from
`OfflineSimulationEngine`'s food model; the client cannot compute the drain
honestly. More useful now that rations drain the larder on a schedule.

### 13. Mobile app - store submission

The app is built and signed (release key in `%USERPROFILE%\.folkidle`,
backup in `D:\FolkIdleBackups\signing`) and the web login offers the APK
download. Left: store listings and developer accounts (owner), push needs
`google-services.json` (Firebase) before `register()` may be called,
the IAP vendor adapter. Archive section 13, phases C and D.

## Parked by the owner

- **38. Guild Wars** - until the population nears the floor (50 players at
  level 10+). Design: `docs/superpowers/specs/2026-09-24-guild-wars-design.md`.
  The four `svelte-check` errors in `GuildOps.svelte` are its hidden
  handlers; do not delete them.
- **62.** Social features that need about 30 active players a week.
- **Long Game L5 (Stripe)** - blocked on the owner's payment account.
- **Legacy perks (XP, Gold, "Combat speed")** - parked with Guild Wars
  (owner, 2026-10-29, task 125). They can be neither bought (no client screen
  calls `purchaseLegacyUnlock`) nor earned (only `GuildWarEngine` credits
  legacy shards). Production: 0 of 26 accounts hold a perk. Revisit when
  Guild Wars return; the damage perk then needs an honest name, since
  "Combat speed" multiplies damage.
