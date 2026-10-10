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

### 37. The Deep - measure the retune (due 2026-10-14)

The retune (stake 0.2%, toll x1.35 a floor, 1 diamond per new deepest floor
of the week) went live 2026-10-07. Week 1 before it: one payer, 683.8M spent,
115.5% of the gold minted that week. The scheduled task **"FolkIdle Deep
retune capture"** on the owner's PC writes
`D:\FolkIdleBackups\deep-retune-week1.txt` on 2026-10-14. Read it for: a
deepest floor past 13, a second payer, diamonds paid inside the cap. Full
history: archive section 37.

### 111. Rations - watch the first week (built 2026-10-07, not yet deployed)

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

### 112. Re-measure Malakor's Ascension after power changes

Malakor's ladder (`BossAscensionRegistry.MalakorAttackMultiplier`) is solved
against measured headroom: about x19 attack for the owner's character on
2026-10-07, about x34 for a maxed profile. Anything that makes characters
stronger (new affixes, inheritance caps, skills, rebirth bonus) moves those
numbers. Restore a production dump into a local database, set
`FOLKIDLE_CALIBRATION_DB`, run the harness, and move the constants if the
targets drift (owner clears ~A2-A3, maxed ~A5-A7, A10 about 2x maxed).
Regions 1-4 still use "A10 = the boss two regions ahead".

### 113. Re-measure pumpkins and tune the Witch (due 2026-10-11 ~22:40 UTC)

Pumpkins went x5 (kill 0.5%, harvest 0.1%) and the Witch to 1 in 20,000 per
pumpkin on 2026-10-10 (PRs #163, #165). Both were sized on the OLD rate x5,
because the new one had run for 1.5 h. Baseline at 2026-10-10 22:39 UTC:
Mivoru (8) 5,496, Tomda (107) 1,542, (pro)boss (108) 1,522. There is no
earnings ledger: from each balance subtract Cailleach first clears since
then (`seasonal_boss_clears`, 250-1,500 per tier) and add back shop spending.
Then divide by the hours. Acceptance:
- an ordinary active account (Tomda) still reaches 2-3 pets + 2-3 avatars
  (~10,000 pumpkins incl. boss) by 2026-11-02;
- the Witch's chance over the rest of the event, per account, is reported
  to the owner (target was ~73% for the owner, ~38% for Tomda) and the
  constant (`PetRegistry.RareDropPerCurrency`) moved if they ask.
Details: `NEXT_STEPS_BACKLOG.md`, 2026-10-10 evening entry.

### 114. Rebirth that is worth taking - needs the owner's decision

The owner, 2026-10-10: rebirth does not pay - staying on the last region
is better; it must be visible and worth it, but capped so a new player can
still catch up, after which players build and farm the endgame. Today it
pays only Renown, `floor(15 x (1 - 0.8^n))`% damage for rebirths at level
50+ (`RebirthRules`). Proposal on the table, not approved:
- a cap of about 10 rebirths;
- each pays something that makes the next run FASTER (XP %, cheaper levels
  1-50), so a rebirth is not a punishment and a newcomer catches up through
  a fast start;
- one point per rebirth in a small permanent rebirth tree (loot, gather,
  crit... - the start of builds);
- milestone unlocks (e.g. R3 a character slot, R5 a second pet slot, R10 a
  title or frame) and a visible mark beside the name;
- every rebirth bonus together capped around +20-25%.
Measure before proposing numbers: how long the second run takes with the
bonuses (simulation), and what it does to `PowerCeilingTests` and Malakor's
Ascension (task 112). Also check that a bought high-level item on the
market cannot be worn by a reborn level-1 character (level requirement).

### 115. Breeding a new player can follow

The owner, 2026-10-10: breeding is hard to get into, and how aptitudes climb
(villagers arrive with at most 20, the cap is 50) is not explained. Today:
villagers roll `2 + rand(0..Inn x 1.5)` capped at 20; a child takes each
aptitude from one parent (weighted by value), then +1 at 25% + Grounds
level, -1 at 10% (swapped when inbred), 5% epic +1 to all; Grounds 4/7/10
lets 1/2/3 aptitudes take the better parent outright (`BreedingAptitudes`).
Past 20 it climbs ~+0.2-0.3 a generation, so 20 -> 50 is 100+ generations.
Proposed, not approved:
- the screen states the child's range per aptitude in plain words;
- a "suggest a partner" button and a "first child" quest step;
- the first selectable aptitude from Grounds level 1, not 4;
- no -1 mutation on a selected aptitude;
- measure generations 20 -> 50 before and after (`BreedingClimbTests`).

### 116. Insights harvest rate looks dead

`player_stat_samples.Harvests` (read off the Logistics achievement row,
`StatSampler`) stayed flat all of 2026-10-10 for the owner's account while
two characters gathered, so Progress -> Insights probably shows 0 harvests
an hour. Find where the Logistics counter stops (a maxed achievement?) and
give the sampler a counter that always moves. Grep for the WRITER.

### 117. exercise.mjs leaves a 50-diamond mail per run

The monster-pet duplicate check (`/api/v1/dev/pet-drop`) mails the fixture
50 diamonds every run and nothing claims it. Harmless, but the fixture's
mailbox grows. Claim it in the same check (round-trip), or have
`--seed-dev` clear the pet-duplicate mail.

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

### Flaky: `OfflineCombatParityTests` row "r5 bare fed"

Fails only under full-suite load, passes alone 3 of 3. Find what it shares
with other tests (static queues, the shared Random) before widening any
tolerance.

## Parked by the owner

- **38. Guild Wars** - until the population nears the floor (50 players at
  level 10+). Design: `docs/superpowers/specs/2026-09-24-guild-wars-design.md`.
  The four `svelte-check` errors in `GuildOps.svelte` are its hidden
  handlers; do not delete them.
- **62.** Social features that need about 30 active players a week.
- **Long Game L5 (Stripe)** - blocked on the owner's payment account.
