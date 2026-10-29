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

## State on 2026-10-29

- **Production** runs `main` through PR #202 (**1.0.1276, client 1.35.0**),
  verified after the deploy: `deploy.sh` saw the manifest answer 1.0.1276 and
  its zip serve 200, `/healthz` answers 200, and `smoke:screens` against
  production rendered 30/30 screens clean. #199 before it (1.0.1269) was
  verified the same way, plus `/api/v1/races` answering.
  - #202 (1.0.1276): the tool affixes `gather_yield_pct` and
    `gather_rare_find_pct` pay, each only for its own profession (axe
    woodcutting, pickaxe mining, rod fishing).
  - #197 (1.0.1263): inheritance Gathering yield pays, the Fortitude bough gives
    armour, and the weapon damage affixes are labelled "Damage". It also commits
    `docs/architecture/STATS_REFERENCE.md`, whose §16 is the defect list the
    tasks below come from.
  - #198: measurement tests only (`RaceUnitMeasurementTests`).
  - #199 (1.0.1269): Draugr mastery lifesteal 10% / 12% (it was a hundredth of
    that), Fairy mastery +15 and Draugr -15 attack speed, `RaceBonusRegistry`
    with `GET /api/v1/races`, race cards in the Wiki and mastery lines in
    Progression.
  - #201: CI runs the server suite and the client checks **on every pull
    request**. The image push, migrations and deploy stay push-to-main only.
    The stale-build hook now blocks only on a server running from the same
    checkout.
- **Open, CI green, waiting for the owner as a separate release:**
  - **#200** dodge curve + boss recalibration. Region 2 1.85 -> 1.87, region 3
    3.08 -> 3.17, seasonal tier 6 AttackPct 20 -> 35, with always-on margin
    tests. After it ships, check the region-2 and region-3 first clears and
    seasonal tier 6 in production.
  - It carries no release notes yet. Add them at deploy time.
  - Also open: #206 (task 123) and #207 (task 122), both cleanup with no
    behaviour change.

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

### 119. `check:safearea` - triage the existing findings

Measured 2026-10-14: findings on 48 of 60 screen/orientation pairs on
`main`, almost all "bottom ... (pinned)" - content under the gesture bar at
rest (Combat's Fight column, Gathering totals, World Boss, the Delve). It
was not caused by the design waves (47 on that branch). Decide per finding
whether it is real (pinned by scrolling and looking, as `client_web`'s
rules say) or the checker's own, fix the real ones, and make the check a
ratchet so the count cannot grow unnoticed.

### 120. `exercise.mjs` flakes - BUG, high priority (issue #204)

**Current state.** On 2026-10-29, four runs against working builds failed three
different single steps, none caused by the change under test:

| Step | Cause |
|---|---|
| "the gold actually left" (Delve) | The fixture had been away about a day. Its offline `CombatAway` payout (707,733 gold, read from `gold_income_daily`) landed mid-step. |
| "a mature account is told what to do next" | Spent fixture state. It passes after `--seed-dev`. |
| "a drop can be worn from the loot list" | A fixed 90 s wait on drop RNG. The fixture's slot-1 character is Human, so the race changes did not cause it. |

**Expected result.** A green run means the game works and a red run means it
does not. Nobody reruns to find out which.

**Depends on.** Nothing. Do it before CI (#201) is trusted as the gate, or CI
only produces false alarms faster.

**Acceptance.**
- `exercise` seeds the fixture itself at start (`--seed-dev` is idempotent),
  and the seed stamps the fixture's last-seen time to now, so no offline
  payout can land mid-run.
- The wearable-drop step uses a drop the fixture is granted, or polls against
  a time budget instead of one fixed wait.
- **5 consecutive full runs green** on an unchanged build, recorded with their
  counts.
- Optional, but it is the speed win: `EXERCISE_ONLY=<sections>`.

**Tests.** The five recorded runs; and each step, re-run after the fixture is
deliberately left spent, still passes.

### 121. `gather_speed_pct` units and scope - MEASURE FIRST, balance (issue #203)

**Current state.**
- Affix magnitudes are tenths of a percent, and every other affix and the
  tooltip read them that way.
- `GatheringToolEngine.TotalSpeedBonusPct` adds `ToolGatherSpeedPct` as WHOLE
  percent. A Legendary roll shows +5.5% and pays +55%.
- Speed is summed across all three tools, so an axe speeds up mining.
- Yield and rare find were made per profession in #202. Speed was left out on
  purpose and belongs here.

**Expected result.** The tooltip and the tick agree, and a tool speeds up only
its own profession, with the size of the change measured before it lands.

**Depends on.** #202 merged (the per-tool fields it adds are the pattern to
reuse).

**Acceptance.**
- A table of each profession's speed at reference builds (mastery, tool tier,
  village, affix), today and under each option ("engine /10" vs "tooltip x10"),
  from `ProgressionRateTests` / `GatheringShareTests`.
- A read-only production count of tools carrying `gather_speed_pct`, and how
  much speed they give today.
- The owner picks an option. No change ships without that decision.

**Tests.** A per-profession scope test, as #202 has for yield. A test that a
rolled magnitude M pays exactly what the tooltip prints for M.

### 122. Dead set-bonus fields - BUG or DESIGN, verify first

**Current state** (grepped 2026-10-29):
- `SetBonusEngine.SetBonusResult.FlatAttackPowerBonus` and
  `CooldownReductionActive` are declared and assigned by nothing.
- `StatsCalculator` still adds `FlatAttackPowerBonus` to both flat damage
  figures. That always adds 0.
- `CooldownReductionActive` is referenced only by comments in
  `StatsCalculator` (its consumer was already removed there).
- Neither is in `StateUpdatePacket`, the roster's `ActiveSetResponse` or the
  client.

**Expected result.** Each field ends as exactly one of:
- implemented, if a set's design names that bonus;
- removed;
- kept with a comment saying why it is inert.

**Depends on.** The set design (`SetBonusEngine`, and the task-63 write-up in
the archive), to learn whether any set was meant to carry them.

**Acceptance.**
- Every reader and writer listed (code, packets, client, tests, docs).
- A removal compiles with no packet or protocol change (`generate:protocol
  --check` clean).
- Nothing persisted refers to either field. Verify, do not assume.

**Tests.** Removal: the existing set tests stay green. Implementation: a test
that the set pays the bonus, and that a mixed set does not.

### 123. Stale code - CLEANUP, no behaviour change

**Current state.**
- `StatsCalculator`'s Vodnik block still says `CritMitigationPct` is "computed
  here but not yet consumed anywhere". `SimulationEngine.MonsterCritMultiplier`
  and `BossGearBenchmark` consume it.
- `Domain/Combat/EquipmentLevelGate.cs` has no caller. It is mentioned only by
  comments in `RegionUnlockGate`, `CraftingEngine` and `StateUpdatePacket`.

**Expected result.** The comment tells the truth. A dead class is gone, or it
is documented as kept on purpose.

**Depends on.** Nothing.

**Acceptance.** `EquipmentLevelGate` has no code reference left (grep tests
too). The full server suite is green. The diff touches no logic.

**Tests.** None new. This is a deletion and a comment.

### 124. Fairy's dead "+20% ranged damage" - DESIGN

**Current state.**
- The Vila (Fairy) innate multiplies `FlatRangedDamage` by 1.2
  (`RaceBonusRegistry.VilaRangedDamageFactor`).
- Combat has one damage figure (`FlatMeleeDamage`). `FlatRangedDamage` is read
  only by `GuildWarSnapshotEngine`'s aggregate.
- `/api/v1/races` deliberately does not describe the bonus, and
  `RaceBonusRegistryTests` pins that it never will while it is dead.

**Expected result.** The Fairy's innate package is something the fight pays
and the page can state.

**Depends on.** The owner's choice. Find the original intent first: the GDD,
git history of the race block, and the archive.

**Options to prepare, each with its measured effect** on Fairy damage per
second and survival against the other five races (use the reference builds in
`RaceUnitMeasurementTests`):
- **(a)** A real ranged damage type. That is a combat-model change, not a race
  change, so price it as such.
- **(b)** Replace it with a different, working effect.
- **(c)** Remove it.

Do **not** silently redirect it into general damage. That is option (b) with
a number nobody chose.

**Acceptance.** The owner picks. The registry, the page and a test all say the
same thing.

### 125. Design decisions with numbers - DESIGN

Prepare options, not changes. Each option comes with its measured effect.

- **Tank lineage.** `ProgressionEngine.Lineages` defines Tank (+8% base health
  per level), and no command or screen selects it. Registration writes
  `SelectedLineageId = 1` (Warrior, +5% base damage per level). For
  reference, +8% a level makes base health x9 at level 100, against Warrior's
  base damage x6.
  - Options: expose the choice (where and when: registration, rebirth),
    rework it, or delete it.
  - Measure: boss walls (`WallWindowScanTests`) and `ProgressionRateTests` for
    a Tank.
- **Ring affixes.** The ring rolls only `block_chance_pct`, so a high-rarity
  ring stacks 4-5 block rolls.
  - Options: widen the pool (crit chance, crit damage, lifesteal, flat HP),
    with its effect on the best builds' DPS, block and sustain.
  - Must keep: the `KNOWN_AFFIX_IDS` order (the wire index) and the stored
    items.
- **Legacy perks.** XP, Gold and "Combat speed" (which actually multiplies
  damage) are purchasable on the server (`PurchaseLegacyUnlocks`), but no
  client screen calls `purchaseLegacyUnlock`, so they cannot be bought.
  - Options: a screen, which then needs an honest name for the damage perk;
    or retire them.
  - Measure: where legacy points come from and how many players hold them
    (read-only query).

**Acceptance.** For each item, a one-page option sheet with numbers. The owner
decides, and only the chosen option becomes a separate build task.

### 126. Automatic stat audit - TOOLING, prevents a repeat of 2026-10-29

**Why.** Nearly every defect found on 2026-10-29 was one shape: a value that
is bought, rolled, stored or shown, and that the simulation never reads, or
reads in the wrong unit. Examples: inheritance Gathering yield, the Fortitude
armour, the tool yield and rare find, Draugr's double divide, the 0.15 attack
speeds, the dodge dead zone, `gather_speed_pct` x10. Each was found by reading
code. Reading does not scale, and it is not repeated after the next change.

**Expected result.** A test that fails when a new stat is added without a
consumer, or with a unit that disagrees with how it is shown.

**Acceptance.**
- **Registry -> consumer:** every inheritance stat, skill-tree node, race
  effect, affix id, pet stat and trait has a test-visible effect. Changing it
  from zero to its maximum changes a figure the tick uses: attack, health,
  armour, hit or crit, yield, speed, gold or XP. A registry entry with no
  effect fails, unless it is listed in an explicit allow-list with a reason.
- **Units:** every percentage affix's tooltip value (magnitude / 10) equals
  the effect the tick applies at that magnitude.
- **Parity:** for a set of reference payloads, the live tick's figures, the
  offline projection, `CharacterStatSheet` and `HuntingProjection` agree. The
  task-78 parity tests are the pattern.
- It runs in the normal suite, under a minute.

**Depends on.** 120, so the CI it runs in is trustworthy. It will surface
whatever 122-124 have not closed yet, through its allow-list.

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
