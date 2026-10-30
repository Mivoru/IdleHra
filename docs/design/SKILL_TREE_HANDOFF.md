# Skill tree rework - handoff

Written 2026-10-30, at the end of the session that started the rework.
Production is **1.0.1344 / client 1.38.2**. Read this first, then
`SKILL_TREE_REWORK.md` (the design and every measurement) and
`SKILL_TREE_ART.md` (icons, callouts, sounds).

## 1. Where it stands

| Slice | What | PR | Live |
|---|---|---|---|
| design | `SKILL_TREE_REWORK.md`, owner decisions D1, D3-D6 | #215 | - |
| 1 | Draft-mode tree screen on today's 20 nodes: REST definition, preview, atomic commit, server stat preview, square rune nodes, hold/hover tooltip | #216 | 1.0.1322 |
| art and sound | 42 icons (owner, Gemini) as 256 px WebP; 14 ElevenLabs clips, including the first `combat_hit_crit` | #216 | 1.0.1322 |
| 2.0 | `TreeBuildHarnessTests`: the baseline of today's tree | #217 | 1.0.1329 |
| 2.1 | Rank InlineArray on the payload, one node table, node levels off the snapshot (`GET /api/v1/skilltree/state`, packet 865 -> 845 bytes) | #217 | 1.0.1329 |
| 2.2 | `WarTreeRegistry`: 35 nodes (ids 20-54), stat nodes wired | #217 | 1.0.1329 |
| UI fixes | Steady backdrop, readable buttons in both schemes, dock clear of the tree and the coach | #218 | 1.0.1333 |
| 2.3a | Crushing Blow, First Strike, `ResponseCombatEventPacket.Proc` (26 -> 27 bytes), callouts | #218 | 1.0.1333 |
| 2.3b | Bossbreaker, Executioner, Double Strike, Echo, Frenzy | #219 | 1.0.1335 |
| 2.3c | Bloodthirst, Riposte, Last Stand | #220 | 1.0.1337 |
| 2.3d | Thunderer, Ruin, Perfect Sequence, Living Fortress, Golden Fleece | #221 | 1.0.1339 |
| stalled screens | 8 s stall message with Reload, idle prefetch of every screen | #222 | 1.0.1342 |
| 2.3e | Fortune column | #223 | 1.0.1344 |

**Players still play today's tree.** Every War-half effect applies only
when `TickStatePayload.SkillTreeVersion == 1`, and only tests set it.
`WarTreeRegistry.Resolve` answers all zeros for version 0, and
`TreeBuildHarnessTests.War_ranks_do_nothing_on_todays_tree` pins that.

## 2. How the code is laid out

- **`Engine/WarTreeRegistry.cs`**: the War half's table. It holds one
  `Add(...)` row per node, with column, row, lane, kind, ranks, cost per
  rank, effect kind, per-rank tenths (or a `ranks:` list when the values are
  not linear), and fork or bridge. It also has its own `BlockedReason` /
  `PlanReason` over column points (row gates 0/4/10/16/26, bridges 10 in
  both columns), `IsPending` (an effect kind outside `Wired` cannot be
  bought), and `Resolve(in payload) -> WarEffects`, which sums every effect
  in tenths.
- **Today's tree** is still `SkillTreeRegistry` (ids 0-19), now one
  `SkillNodeDef` table built from the old ring rules. Slice 2.5 deletes it.
- **The rank storage** is `TickStatePayload.SkillRanks`, a 64-byte
  `InlineArray`. It is not a `byte[]`, because the payload is copied by value
  (the preview and the advisor) and blitted, and an array would be shared by
  every copy. The twenty `Skill_*` names are properties over slots 0-19.
- **Counters**: `WarSwingCount` (Perfect Sequence, every 6th swing) and
  `WarHitCount` (Ruin, every 8th connecting hit) run across fights, so the
  live rate is exactly 1 in N.
- **Where effects land.** Combat reads them in one place per rule:
  - `SimulationEngine.LiveCombatStats` (armour, dodge, block, accuracy,
    Living Fortress)
  - `EffectiveMaxMilliHpFor`
  - `LiveAttackIntervalMs` (Relentless, Frenzy, Ruin's price)
  - `LiveCritChancePct` / `LiveCritMultiplier`
  - `EffectiveMilliAttackFor` (damage %)
  - the live hit in `RunCombatTick` (Crushing, First Strike, Bossbreaker,
    Executioner, Echo, Ruin, Perfect)
  - the monster's swing (Riposte)

  Helpers shared by the live and offline paths: `DoubleStrikeChance`,
  `BloodthirstTenths`, `HasLastStand`, `HasGoldenFleece`, `RiposteChance`,
  `RiposteMilliDamage` and `LiveCodexDamageMultiplier`.
- **Offline catch-up and the advisor** price each mechanic in
  `HuntingProjection` (`PlayerSwing.For`, `Landed`, `FightSetup.For`), as an
  expectation:
  - First Strike and Executioner are priced as monster health taken off the
    swing count.
  - Riposte is spread over the swing outcomes; the same factor goes into
    `burnFactor`, so it does not heal.
  - Perfect Sequence is a weighted crit outcome.
- **Economy nodes** read through:
  - `LootLuckBreakdown.From` (Scavenger, Rarity)
  - the drop request's `MaterialQuantityPct` (Plenty)
  - `CombatGoldReward` (`GoldFactors.Tree` for Golden Touch, Trophy Hunter)
  - the XP multiplier (Quick Study)
- **Callouts**: `ResponseCombatEventPacket.Proc` names the skill (`SkillProc`:
  1 Crush, 2 Echo, 3 Riposte, 4 Ruin, 5 Perfect). The client's
  `ui/skillProcs.ts` maps each id to a label, a CSS tone and a clip;
  `FloatingDamage.svelte` styles the tone, and `serverMirrors.test.ts`
  compares both lists.
- **REST**: `/api/v1/skilltree` (definition, icon slugs), `/state`,
  `/preview` and `/commit` live in `Network/NetworkBroadcastSystem.SkillTree.cs`.
  All of them serve today's tree; the War half is not on the wire yet.

## 3. The proof every mechanic must pass

`WarMechanicParityTests` runs the REAL `RunCombatTick` for an hour against
`OfflineSimulationEngine.ProjectCombat` from one payload
(`OfflineCombatParityTests.Character`, geared, fed), and asserts two things:

1. offline agrees with live (5% or one kill), and
2. the live tick really uses the mechanic: the same character without it
   kills measurably less.

Effects that are not kills get their own fact: food for Bloodthirst and
Living Fortress, gold and XP for the Fortune nodes, the loot sums for the
loot nodes. **Copy this pattern for every new mechanic.** It caught both
directions of the "wired on one side only" defect.

The measured worth of every mechanic is in `SKILL_TREE_REWORK.md` section
8.

## 4. The three bridges (built 2026-10-30)

**Built** in `claude/skill-tree-bridges`: Piercing Crit is a `CritDamagePct`
node, Counterstroke is `CounterstrokeOfDefence` (added in
`LiveCritChancePct` after the Living Fortress check, so block never feeds
itself), and Know Your Prey is `KnowYourPreyPct` through
`SimulationEngine.KnowYourPreyTenthsPct` (live hit, `Landed`,
`CombatGoldReward.PerKill`). The highest unlocked region comes from the new
`RegionUnlockGate.HighestUnlockedRegion(byte mask)`. Parity rows and the
measurements are in `WarMechanicParityTests` and `SKILL_TREE_REWORK.md`
4.3. The rest of this section is the plan as approved.

The original bridges cannot be built. Each fails on a rule that this
session measured or hit. The owner approved these replacements on
2026-10-30:

| Bridge | Original | Why it cannot stay | Build instead |
|---|---|---|---|
| Piercing Crit (Might-Precision, id 53) | A crit ignores more monster armour | Armour lets 94-99% of a geared hit through, so it is worth nothing | **+6 / 12 / 20% crit damage** |
| Counterstroke (Precision-Guard, id 54) | After a dodge or block, the next swing is likelier to crit | Needs state between swings; the player and the monster share one swing accumulator | **Crit chance +10 / 20 / 33% of your dodge + block points** (a conversion, as Frenzy is) |
| Know Your Prey (Fortune-Might, id 52) | More damage against monsters with codex level 10+ | Per-monster codex levels are not on the payload | **+3 / 6 / 10% damage and gold against monsters of your highest unlocked region** |

How to build each one:
1. Give it an effect kind and add it to `Wired`.
2. Add it to `Resolve` and wire it at the one consumer:
   - Piercing Crit: `LiveCritMultiplier`.
   - Counterstroke: `LiveCritChancePct`. Read dodge and block off the stats.
     Watch the Living Fortress and Perfect Sequence clamps, which already
     live there.
   - Know Your Prey: the live hit and `Landed` for damage, `CombatGoldReward`
     for gold. "Highest unlocked region" is derivable from
     `DefeatedRegionBossMask` (the region after the highest beaten boss),
     and the monster's region is `monster.RegionTier`.
3. Add a parity row in `WarMechanicParityTests`.

Bridges count their points toward both columns (`PointsInColumn` reads
`BridgeColumn`). `WarTreeRegistry.PlanReason` already handles their gate.

## 5. Slice 2.4: tuning (done 2026-10-30)

**Done** in `claude/skill-tree-2-4`. The measured table is in
`SKILL_TREE_REWORK.md` 8.1: every band lands at 0.98-1.04 of today's best
build. The owner was away and asked for these to be decided alone, by the
best recommendation:

- **Combat values cut to about a quarter.** The overshoot had the same
  log-ratio in every band (0.23-0.25), so a uniform cut fixes the early
  overshoot too. Row-0 costs stay at 1 point; the curve already matches.
- **Keystones to about five points' worth.** Ruin x1.6 every 8th hit
  (was x2.5), and its price is now 75% of attack speed (was 50%), as
  planned. Perfect Sequence: every 12th swing, +20% crit damage (was 6th
  and +50%).
- **Bossbreaker became the War half's First Blood:** -6.4% of a
  first-clear boss's extra health a rank, 32% at 5. That is exactly
  today's First Blood maximum, which `BossGearBenchmark` assumes. It fixes
  two problems at once: the War half had no First Blood, and Bossbreaker's
  damage made boss farms 19% faster. `BossFirstClearRules.ReliefFor` takes
  whichever tree the payload runs. It no longer adds world boss damage.
  Considered and rejected: a new node (it changes the approved layout), and
  re-solving the walls without the relief (it is risky and makes every
  wall harder).
- **Guard stays a survival column; rations are not touched.**
  `FoodRegistry` says rations are a deliberate fish sink that sustain must
  not reach, so a Guard node that saved rations would undo the fishing
  economy. Living Fortress keeps "you never crit": -11% kills for +32%
  swings survived is a real trade for wall pushers. Bloodthirst is
  unchanged.
- **Codex Scholar cut to 1 / 2 / 3%** of the codex bonus. The harness runs
  at codex x1, so this node sits outside the measured ceiling. At the late
  x5.76 it is now worth about 2.5%, where it was about 40%.
- **Golden Touch cut to 2% a rank** (+10%). Today's tree has no flat gold
  bonus, and gold already rose in the stats fixes.
- **The bridges were cut with the rest.** The owner approved 6/12/20% crit
  damage, 10/20/33% of defence and 3/6/10% damage at the starting scale.
  They are now 1.5/3/4.5%, 2/4/6% and 0.5/1/2%.
- **The parity tests run at x4** (`WarTreeRegistry.TestValueScale`, an
  `AsyncLocal`). At the tuned sizes, several mechanics are worth less than
  one live hour's noise. A mechanic wired on only one side would then hide
  inside the 5% slack. The harness and the exact-value facts use the real
  numbers.

Not done (noted for later): the design's secondary targets in section 8
item 2 (keystone builds within 15% of each other, Fortune keeping 70% of
the Duelist's damage). These are not measured yet.

The plan as written before the tuning follows.

**Target** (`SKILL_TREE_REWORK.md` section 8). The best War build must land
within ±5% of today's best build at every band, measured the same way:

| Region (level) | Today's best, DPS from the tree |
|---|---|
| 1 (20) | +8.6% |
| 2 (40) | +18.5% |
| 3 (60) | +26.0% |
| 4 (80) | +28.6% |
| 5 (100) | +30.1% (today's tree is out of combat power at 58 points) |

The War half should keep that ceiling but spread it over the whole budget,
so the last points of a level-100 player still buy a choice. The walls do
not include the tree (`BossGearBenchmark` assumes only First Blood at its
maximum), so the tree's slack is exactly this ratio.

**Known problems, measured:**
- The War half is too strong early: +27.7% at region 1 against 8.6%,
  because row 0 costs 1 point a rank. Lower the row-0 values, or cost the
  ranks 1/1/2/2/2.
- The War half is above the ceiling on 27 points (+37.3%).
- **Bossbreaker** (+19% kills on a boss farm) softens the walls directly,
  which today's tree never did. Either measure it in `WallWindowScanTests`
  or cap it.
- **First Blood** is not in the War half. `BossFirstClearRules.MaxHpFor`
  assumes its relief, so the switch either keeps an equivalent node or
  drops the relief and re-solves the walls (`FOLKIDLE_WALL_SCAN=1`).
- **Living Fortress** costs 14% of kills for 2% of fish, and **Bloodthirst**
  saves 4 fish in 554. The larder at region 4 is mostly rations, not
  healing, so Guard needs a payoff that touches rations, or bigger numbers.
- **Ruin**'s price (attack speed at half) eats its gain (+5%). Try 75%.
- **Codex Scholar** is +26% kills at codex x2.0, and the codex reaches
  x5.76 late. Cut it hard, or cap the boost.

**Harness work needed first:**
- The harness optimises damage per second, so it cannot see First Strike
  or Executioner, which shorten a kill without raising the mean swing.
  Add a seconds-per-kill objective from `FightSetup` (its `FarmKill`
  `SwingsToKill`).
- Its greedy search takes one rank at a time, so it stops where a gate
  needs points in a weak node. Let a move be "the cheapest legal path to
  the next row".
- Run it: `$env:FOLKIDLE_TREE_HARNESS='1'; dotnet test
  server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter
  TreeBuildHarnessTests --logger "console;verbosity=detailed"`.

The owner's rule for balance is that numbers come from a measurement.
Show the harness table before and after a change. A change that moves a
wall goes through `WallWindowScanTests`.

## 6. Slice 2.5: the switch

1. **Wire the War half onto the screen.** The definition endpoint serves
   `WarTreeRegistry` (columns Fortune/Might/Precision/Guard, rows 0-4,
   lanes, bridges). `preview` and `commit` run `WarTreeRegistry.PlanReason`
   over the 64-slot ranks. The client grid already reads Column, Row, Lane,
   `BridgeColumn` and `ColumnPointsGate` from the definition. It still draws
   today's three rows per limb, so give it the five-tier layout with
   bridges between columns.
2. **Write a migration:**
   - refund every point spent on ids 0-19 (recompute from the old costs,
     as `RespecAsync` does),
   - delete those rows,
   - set every player's `SkillTreeVersion = 1`, which needs a column and
     hydration in `StateCheckpointManager`,
   - grant one free respec.

   Production had 4 accounts with tree points on 2026-10-29. Take a
   backup first (deploy skill).
3. **Gold respec (D3).** Price it by points spent; keep the free one and
   the paid grants.
4. **Delete today's tree:** `SkillTreeRegistry`'s old nodes, the `Skill_*`
   readers, the `today` halves of the shared helpers, and the client's
   `SKILL_TREE_NODES` mirror, which the Wiki still reads.
5. **Verify with a fresh account and the fixture** (`exercise.mjs`). First
   see a callout in a real fight; none has been seen yet, because no player
   ran the War tree.
6. **Bump the version with release notes.** This is the first change
   players will notice.

## 7. Working rules from this session

- **The owner's standing rule** for this rework: after green CI, merge,
  deploy (deploy skill, `deploy.sh` over SSH), smoke production, then
  continue.
- **The design rule** behind every redesign this session: a mechanic must
  resolve inside one swing and have a closed-form expectation until slice
  2b (a per-swing offline simulator, D6). That is why Frenzy is a
  conversion, Riposte fires on dodge or the block share, Crushing Blow
  doubles instead of ignoring armour, and Codex Scholar boosts the codex
  bonus instead of counting kills.
- **Measure before designing on armour.** Monster armour is nearly
  irrelevant to a geared character.
- **The stale-build hook kills the whole command.** A chained "edit and
  build" while the local server runs from the same worktree does nothing at
  all, the edit included. Stop the server first.
- **Edit C# through a script file, not a bash heredoc with Python.** Python
  string escapes turned `\b` into a backspace once. The scratchpad scripts
  that check every replacement before writing any were the safe pattern.
- **Local Docker broke twice** (WSL mkfs, then an overlay2 I/O error).
  Restarting Docker or the PC fixed it. Never repair it destructively; the
  dev database lives there.
- **Production smoke:** before #222, a random screen hung on "Loading..."
  about once per deploy. The cause is unknown (Caddy is clean, HTTP/2, the
  box is idle). #222 handles it, and smoke has been clean since.
- **`PendingGrantCombatLootTests.AutoSalvageGoldSurvivesAFailure`** fails
  when one name filter runs it together with the parity suites, on main
  too. It is a static loot-queue collision, not a defect.

## 8. Open items outside the rework

- `AutomationAutoFuseTests.TheWorker_FusesTheStacksARequestsDropsLandedIn`
  fails in a full local run on this PC (twice on 2026-10-30, on the bridges
  branch and on 2.4). It passes alone and in CI. The likely cause is load or
  timing: it waits on a background worker. Nobody has looked further.

- Wardrobe fails `check:touch` with one undersized control. It is old;
  nobody looked at it this session.
- The ElevenLabs key the owner pasted into the chat should be rotated.
- `last_stand.webp` has a light snowy background that stands out among the
  dark icons. The owner may regenerate it.
