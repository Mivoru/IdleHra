# Task 38 - Guild Wars: audit, design options and phased plan

> **For agentic workers:** this is an AUDIT + DESIGN BRIEF, not an executable
> plan yet. Part C ends with a phase breakdown in the house style
> (`2026-09-17-retire-unity-project.md`), but no phase may start until the
> owner has chosen a format (Part B) and a spec exists in
> `docs/superpowers/specs/`. When one does, use
> superpowers:subagent-driven-development or superpowers:executing-plans to run
> it phase by phase. Steps use checkbox (`- [ ]`) syntax.

**Source:** `docs/TASK_BOARD.md` §38 (line ~3586). Owner, 2026-09-23: "design
Guild Wars completely - format, rewards, everything; take inspiration from
popular games."

**Method:** the `wiring-auditor` chain (`.claude/agents/wiring-auditor.md`),
walked by hand across all five war sub-systems, plus read-only SELECTs on
production (Supabase) on 2026-09-23. Nothing was built, run or changed.

---

## TL;DR

1. **There is no Guild War. There are FIVE half-systems wearing the name**,
   two of which run as live cron loops on production right now: a three-front
   weekly points war (`GuildWarEngine`), a cross-shard "node HP" war
   (`GuildMatchmakingEngine` + `GlobalTournamentMeshService`), a turn-based
   guild duel (`GuildCombatSimulationEngine`), a defence roster
   (`RegisterGuildDefense`) and a guild raid (`GuildRaidEngine`). They share
   no data model, pair guilds independently, and none is reachable end to end
   by a player.
2. **The four svelte-check errors are not "hidden handlers".** Their markup was
   *deleted* (`GuildOps.svelte:760` is the tombstone comment). They are
   TS6133 "declared but never read" under `noUnusedLocals`
   (`client_web/tsconfig.json:14`). Deleting just those four would CREATE new
   errors (see §A.2), so they must go as one block.
3. **Production cannot host a guild-vs-guild war at all.** 50 accounts, **one**
   at level >= 10, **one** guild with **one** member; `GuildWarMatches`,
   `GuildMatchmakingSnapshots`, `GuildWarActiveMatches`, `GuildWarCombatHistory`
   and `GuildRaidStates` all have **0 rows** - no war has ever been paired.
4. **The live three-front war is a latent diamond exploit.** It pays **100
   diamonds per member per win** (1.7x the Delve's whole weekly ceiling of 60),
   can be forced to resolve in minutes rather than a week by creating two alt
   guilds, and matches a real guild against an alt's empty guild. It has not
   fired only because nobody has ever had a second guild. **Phase 0 below turns
   it off regardless of what the owner picks.**
5. **Recommendation (the owner decides):** a **guild-vs-environment "Siege"
   season** (Tap Titans clan raids / AFK Arena guild hunts) that works with
   ONE guild, built so a matched **async guild-vs-guild** layer (Clash of Clans
   wars against defence snapshots) switches on behind a population floor
   later, the same way `LeaderboardTierRegistry` gates its tiers.

---

## OWNER DECISION, 2026-09-23: Guild Wars unlock behind a population floor

**The owner said:** Guild Wars should exist, but they unlock only once the game
has at least **50 players** (the owner confirmed 50 later the same day). Keep it
one named constant. Until then the feature is **locked**, not half-on.

What this changes in the plan below:

1. **Phase 0 becomes "lock behind the floor", not only a kill switch.** Pairing,
   settlement, payouts and opcodes 23/27/49/50 all go through one gate,
   `GuildWarUnlock.IsUnlocked(population)`. Below the floor, commands answer a
   readable "Guild Wars unlock at N active players (now M)" result instead of
   acting or disconnecting, and the Guild screen shows that same line with
   progress. Production has one qualifying player, so this also closes the
   diamond exploit (TL;DR item 4) without deleting anything.
2. **Count qualifying players, not registrations.** CLAUDE.md's leaderboard
   rule applies: the board carried `exercise######` throwaways, and 50
   registrations today means one real player. Suggested definition, reusing the
   leaderboard's existing numbers:
   - level >= `LeaderboardTierRegistry.MinimumRankedLevel` (10),
   - not quarantined or banned,
   - active in the last 14 days.
   Put this in ONE query next to the leaderboard's ranked-population query,
   rather than a second definition of "a real player".
3. **Also require enough guilds, not just players.** 100 players in one guild
   still cannot hold a war. Suggested: at least 4 guilds, each with at least 3
   qualifying members. Put this to the owner as a follow-up question.
4. **Unlock once, and stay unlocked.** The floor should be one-way: once it is
   crossed, record the unlock (a row or config flag). A population that dips
   back under it for a week must not switch off a running season. An active
   count on a small game swings with holidays.
5. **The format question reopens.** Siege-first (Option 1) was recommended
   because the game has one guild. With a floor, wars never run on a tiny
   population, so **head-to-head (Option 2) is viable as the launch format**.
   The owner should choose again with that in mind. Siege stays the fallback,
   and it can still be offered below the floor as the "practice" mode, if
   wanted.
6. **Order of work:** Phase 0 (the lock) ships now. The full design and build
   (Phases 1-6) is **not urgent** until the population approaches the floor.
   Tasks 36 and 37 come first.

Open questions this adds for the owner (the floor itself is settled at 50): the definition of a
qualifying player (item 2); the minimum number of guilds (item 3); whether the
locked screen shows progress ("37 / 50 players").

---

# Part A - The audit

## A.1 What production says (2026-09-23, SELECT only)

| Table | Rows | Note |
|---|---|---|
| `PlayerRecords` | 50 | 1 at `CurrentLevel >= 10` (and that one is >= 40) |
| `GuildRecords` | 1 | Id 1, `GuildMMR` 1000, `ActiveMembers` 1 |
| `GuildMembers` | 1 | |
| `GuildWarMatches` | **0** | three-front war - never paired |
| `GuildMatchmakingSnapshots` | **0** | cross-shard war - never paired |
| `GuildWarActiveMatches` | **0** | turn-based duel - nothing in `src` can write one |
| `GuildWarCombatHistory` | **0** | |
| `GuildRaidStates` | **0** | raid never launched |
| `GuildWarDefensiveSnapshots` | 1 | guild 1, MaxHp 1889 - `GuildWarSnapshotEngine` works |
| `GuildDefenseRosters` | 1 | guild 1 - write-only table, nothing reads it |

All war tables are PascalCase EF defaults (no `[Table]` override on
`Models/GuildWar*.cs`, `GuildMatchmakingSnapshot.cs`, `GuildDefenseRoster.cs`).

Guild creation needs level 10 (`GuildManagementEngine.MinGuildInteractionLevel`,
`Domain/Social/GuildManagementEngine.cs:53`) and is free; a guild whose last
member leaves is deleted (`:377-379`).

## A.2 The four svelte-check errors, exactly

`client_web/src/routes/GuildOps.svelte`:

| Symbol | Declared | Why it errors |
|---|---|---|
| `defend` | `:254` | function, no caller - the button markup was removed |
| `attackShard` | `:280` | function, no caller |
| `damageDelta` | `:301` | `$derived` const, never read |
| `takeTurn` | `:303` | function, no caller |

The error is TS6133 (declared but never read) because `tsconfig.json:14-15` sets
`noUnusedLocals`/`noUnusedParameters`. The markup that used them is gone: line
760 is the comment `<!-- Cross-shard war hidden -->` with nothing under it.
The only *hidden* (`display:none`) panel is the **Raid + Logistics** one at
`:484`, and it contains none of the four.

**The trap for whoever removes them:** each of the four is the only reader of
something else. Delete the four alone and these become unread in turn -
`quarantined` (`:252`), `shardMatch`/`matchUuid`/`EMPTY_UUID` (`:270-278`),
`matchId`/`turnCounter` (`:299-300`), and the imports `registerGuildDefense`,
`submitShardAttack`, `executeCombatTurn` (`:24-26`), `fetchGuildShardMatch`
(`:13`) and possibly `typicalHit` (`:3`). The count would go UP. The whole
"cross-shard war" and "guild battle turns" script blocks (`:251-306`) have to go
together, with `BASELINE` in `client_web/scripts/typecheck-ratchet.mjs:43`
lowered in the same commit.

Also note: the **visible** "Guild war" panel (`:428-470`) is live for every
player. It shows "No war is active" forever in production, and when a war IS
active it offers a raw numeric "Commodity id" box (`:461`) whose own help text
admits there is no picker.

## A.3 Sub-system by sub-system

### (1) Three-front weekly war - `GuildWarEngine` (LIVE cron)

Started at `Program.cs:551`. Four loops (`Engine/GuildWarEngine.cs:84-87`).

| Chain step | Status | Evidence |
|---|---|---|
| Pairing | runs | `RunMatchmakingLoopAsync` `:364`; pairs whenever >= 2 guilds are unmatched (`:419-465`), plus Sunday 23:30 and overdue catch-up |
| Point source: combat | wired, **online only** | `SimulationEngine.cs:4673-4683`, 10 WP a kill, 500 a regional boss. Offline catch-up (`OfflineSimulationEngine`) enqueues none - an idle player's main mode scores nothing |
| Point source: crafting | wired, near-unreachable | `CraftingTickCoordinator.cs:58-73`, only items with `RegionTier >= 5` |
| Point source: supply burn | **broken** | opcode 23 -> `GuildWarTickCoordinator.cs:21-37` -> `GuildWarEngine.cs:278-362`. `GetMaterialString` (`ContentRegistry.cs:196-207`) maps 1-6 to `copper_ore, raw_log, iron_ore, oak_log, gold_ore, magic_log`; the last three exist in neither `items.json` nor `gathering_nodes.json`. Points are `(qty/1000)*100` (`:324`) but the burn happens anyway (`:318`), so the client's default quantity of 10 (`GuildOps.svelte:72`) **destroys materials for zero points** |
| Aggregation | wired | `:215-276`, coalesced drain (no await inside - fine) |
| Persistence | yes | `GuildWarMatches` row |
| Wire | partial | scoreboard via `GuildWarScoreboardQueue` -> `GuildFanoutTickCoordinator.DrainWarScoreboard` (`SimulationEngine.cs:1051`). **But `ActiveGuildWarId` is hydrated only at login** (`StateCheckpointManager.cs:~590`); the notification (`CombatLootEngine.cs:196`) carries no match id, so members online when a war starts score **nothing** and see "No war is active" until they relog |
| Resolution | runs, wrong | each qualifying pass resolves **every** active match, not just overdue ones (`:473-487`). `ResolveCombatPhaseAsync` (`:561-624`) fixes hit chance at 0.95 and appears to subtract milli-attack (`ComputeEffectiveMilliAttack`) from plain `MaxHp` - a coin-flip on attack, 1000 WP to the winner |
| Reward | **dangerous** | `DistributeVictoryTokensAsync` `:631-666`: +100 `PremiumDiamonds` to every member (`VictoryDiamondReward`, `:47`) and +25 legacy shards, via raw SQL. For an **online** member it is then overwritten by the checkpoint's absolute write `player.PremiumDiamonds = state.PremiumCurrency` (`StateCheckpointManager.cs:377`, `:1550`) - the silent-lost-grant shape. Nobody hears about a win either way |
| MMR | never moves | nothing assigns `GuildMMR` (only readers at `NetworkBroadcastSystem.cs:2515`, `:6739`) |
| Cron guard | weak | `CronWorkerGuardTests.cs:61` passes it because a `catch (Exception` exists somewhere; but the aggregation loop opens its try *after* `CreateScope`/`BeginTransactionAsync` (`:235-239`), and the matchmaking probes (`:390-463`) are outside any try - the exact "guard that starts too late" CLAUDE.md warns about |
| Tests | display only | `Test_GuildWarScoreboardQueue_AppliesMatchTotalsToEveryGuildMember` (`HardenedEngineIntegrationTests.cs:12000`). Nothing tests pairing, resolution or payout |
| exercise.mjs | none | only checks the Guild screen *loads* ("Guild war" text, `exercise.mjs:777`) |

**The exploit, spelled out.** Pairing fires whenever two guilds are unmatched,
and firing resolves every active match. A player with a real guild and two
level-10 alts: alt guilds A and B are created -> the real guild is paired with
one of them -> the real guild scores kill WP against an empty guild -> alts
disband (guild deleted) and re-create -> two unmatched guilds -> every match
resolves -> +100 diamonds to each real member (if offline at that moment) ->
repeat. Minutes per cycle, no cap. Not observed (0 rows ever) - but it is one
second guild away.

### (2) Cross-shard "node HP" war - `GuildMatchmakingEngine` + `GlobalTournamentMeshService` (LIVE cron)

| Chain step | Status | Evidence |
|---|---|---|
| Pairing | runs, fragile | `GuildMatchmakingEngine.cs:65` fires only on the exact minute Sunday 23:30 - the bug `GuildWarEngine` fixed on 2026-08-01 and this one never got. Pairs independently of (1), so a guild can have two different opponents |
| Entry | opcode 50 `SubmitShardAttack` | `ClientCommandPacket.cs:77`, dispatch `SimulationEngine.cs:771` |
| Validation | disconnects | `ClientCommandValidator.cs:1379-1420` - any mismatch is `TerminateSessionForSecurity` |
| Effect | **trusts the client** | damage = `ClientPredictedDamage` (`ClientCommandPacket.cs:266-275`, the comment admits it), up to 100,000,000; `IsBuy != 0` becomes `IsFinalBlow` (`GuildWarTickCoordinator.cs:105`) which zeroes the node outright (`GlobalTournamentMeshService.cs:95`) |
| Who may attack | attacker guild only | defender's attack is status 1 (`snapshot.AttackerGuildId != request.AttackerAccountId`) |
| Reward | **none** | `IsComplete` is set (`:98`) and read only as "still running" filters. Winning pays nothing, ends nothing |
| Wire | REST + packet | `/api/v1/guild/shard-match` (`NetworkBroadcastSystem.cs:1077`, handler ~`:3110-3150`), `GlobalNodeRemainingHp` on `StateUpdatePacket.cs:605`, hydrated at login (`StateCheckpointManager.cs:546-556`) |
| Client | sender exists, no UI | `commands.ts:862` `submitShardAttack` sends `Math.max(typicalHit, 1000)` as damage (`GuildOps.svelte:284`) |

Verdict: a client-authoritative damage path that violates the rule task 36 and
task 10 set for the world boss ("the client sends a CHOICE or a measured
outcome, never a damage figure"). Replace, do not revive.

### (3) Turn-based guild duel - `GuildCombatSimulationEngine`

| Step | Status | Evidence |
|---|---|---|
| Entry | opcode 27 `ExecuteCombatTurn` | `ClientCommandPacket.cs:58`, dispatch `SimulationEngine.cs:773`, handler `GuildWarTickCoordinator.cs:157-184` |
| **Match creation** | **BREAK - no writer** | `new GuildWarActiveMatch` appears only in `HardenedEngineIntegrationTests.cs:1506-1507`. Nothing in `server/FolkIdle.Server` ever creates a match, so every turn is `NotFound` -> force-disconnect (`:178-182`) |
| Effect | computes | `ExecuteCombatTurnAsync` (`GuildCombatSimulationEngine.cs:59-127`) writes a turn counter and `GuildWarCombatHistory`; nothing ever ends a match or pays |
| Wire | fields exist | `CombatSimulationMatchId/TurnCounter/DamageDelta`, `StateUpdatePacket.cs:580-582` |
| Tests | yes, for the maths | `Test_GuildCombat_SimulationTick` (`:756`), `..._DamageScalesWithGearedVsNakedAttackerSnapshot` (`:1488`) |

A turn-per-tap duel with a disconnect on a stale counter is the wrong shape for
an idle game anyway. Keep the damage maths idea (snapshot-vs-snapshot), drop
the protocol.

### (4) Defence roster - `RegisterGuildDefense` (opcode 49)

Handler `SimulationEngine.cs:2559-2613` writes `GuildDefenseRosters`
(`GuildMMR, ActiveMembers, CurrentTier`, monolith levels). **Nothing reads that
table** (grep: only the two writers, `SimulationEngine.cs:2579` and
`GuildMatchmakingEngine.cs:181`). Write-only; delete.

### (5) Guild raid - `GuildRaidEngine` (LIVE cron, hidden panel)

Opcode 53 -> `GuildRaidEngine.TryStartRaidAsync` (leader-only, gold cost,
silent rollback for non-leaders - `GuildWarTickCoordinator.cs:139-148` says so).
Tick `ProcessGuildRaidTickAsync` (`GuildRaidEngine.cs:191-250`) damages the
boss by `CurrentLevel * DpsPerLevel` of **online** members only, and a kill
pays guild `ContributionPoints` only. The panel is `display:none`
(`GuildOps.svelte:484`). Zero rows in prod. It is the closest thing the repo
has to the recommended format, and its damage model (level, online-only) is
the part to replace.

### Shared pieces that DO work

- `GuildWarSnapshotEngine` (`Program.cs:552`, 15-min refresh, top 20 members by
  level through `StatsCalculator`) - wired, guarded, tested
  (`Test_GuildWarSnapshot_RefreshActuallyWritesTheDefensiveRoster`,
  `HardenedEngineIntegrationTests.cs:9670`), and producing a real row in
  production. **Reusable as-is.**
- The scoreboard fan-out pattern (`GuildFanoutTickCoordinator`) - reusable once
  it also carries the war id.
- `StateUpdatePacketFieldCoverageTests.cs:123-129` lists the seven scoreboard
  fields as `RuntimeOnlyByDesign` "Guild Wars, on the roadmap" - they move out
  of that list (or off the wire) in the phase that lands the new UI.

## A.4 Summary table

| Component | Exists? | Wired? | Persisted? | Reaches the client? | First broken link |
|---|---|---|---|---|---|
| Visible "Guild war" panel (`GuildOps.svelte:428`) | yes | reads packet | - | yes | never shows a war: no guild pair exists in prod; mid-session war never sets `ActiveGuildWarId` |
| `defend` / `attackShard` / `takeTurn` / `damageDelta` | yes | **no** - markup deleted | - | - | `GuildOps.svelte:760` tombstone; TS6133 |
| Opcode 23 `ContributeToWarSupply` | yes | yes | yes (burns) | scoreboard only | stale 1-6 material map; <1000 burns for 0 WP (`GuildWarEngine.cs:308,318,324`) |
| Opcode 27 `ExecuteCombatTurn` | yes | dispatched | history only | fields exist | **no writer for `GuildWarActiveMatches`** |
| Opcode 49 `RegisterGuildDefense` | yes | yes | yes | no | table has no reader |
| Opcode 50 `SubmitShardAttack` | yes | yes | yes | REST + packet | client-supplied damage + one-shot final blow; no reward |
| Opcode 53 `LaunchGuildRaid` | yes | yes | yes | packet | panel `display:none`; online-only level-based damage |
| `GuildWarEngine` (4 loops) | yes | **running live** | yes | partly | exploitable pairing/resolution; online diamond grant overwritten by checkpoint |
| `GuildMatchmakingEngine` | yes | **running live** | yes | via (2) | exact-minute window; duplicate pairer |
| `GlobalTournamentMeshService` | yes | via opcode 50 | yes | yes | trusts client damage |
| `GuildCombatSimulationEngine` | yes | via opcode 27 | yes | fields | match never created |
| `GuildWarSnapshotEngine` | yes | running live | yes (1 row) | no (input only) | none - works |
| War point queue (combat/craft) | yes | online only | via aggregation | scoreboard | offline play earns 0 |
| War rewards | yes | on resolution | raw SQL | **no notice at all** | 100 diamonds > Delve cap; lost for online members |
| REST `/api/v1/guild/shard-match` | yes | yes | read | `rest.ts:666` | only consumer is an orphaned handler |
| Tests | partial | - | - | - | nothing tests pairing, resolution, payout or anti-abuse |
| `exercise.mjs` | no | - | - | - | only asserts the screen loads |

**Verdict: BROKEN everywhere a player would touch it, and dangerous where it
runs unattended.** Reuse the snapshot engine and the fan-out pattern; retire the
rest behind a kill switch first.

---

# Part B - Design options (for the owner to brainstorm; NOT decided)

## B.1 Constraints any format must meet

- **Population:** one guild today. A format that needs two matched guilds does
  nothing for months. Whatever ships first must be meaningful for ONE guild,
  including a one-person guild.
- **Idle:** a few taps a day, and offline progress should count for something.
  The current war scores only live kills.
- **Server-authoritative:** the client sends a choice (target, stance, which
  charge to spend) or, with task 36, a server-bounded measured outcome - never
  a damage figure.
- **Diamond economy:** `DelveRegistry.MaxDiamondsPerWeek = 60`
  (`Engine/DelveRegistry.cs:69`) is the calibrated diamond tap; leaderboard
  first place is capped to the same 60. A war that pays 100 a win is a second,
  larger tap. Any diamond payout needs a weekly cap well under 60 and a
  population floor like `LeaderboardTierRegistry.MinimumRankedPopulation = 20`
  / `MinimumRankedLevel = 10` (`Engine/LeaderboardTierRegistry.cs:53,66`).
- **Task 37 (gold sink):** the owner says 100M gold is easy. Guild war is a
  natural place for a sink that scales with the guild's wealth (siege engines,
  war chest), rather than one more fixed fee.
- **Task 36 (world boss minigame)** and `docs/FUTURE_PLANS.md` (Czech, lines 7
  and 14) both ask for guild wars to be *partly interactive* with the same
  minigames. Build attack resolution so a minigame score can plug in later as a
  bounded multiplier.

## B.2 How other games do it

| Game | Mode | What a member does | Why it matters here |
|---|---|---|---|
| **Clash of Clans** - Clan War / Clan War League | 2-day async war: prep then battle; each member gets 2 attacks against the enemy's saved bases; stars decide it. CWL: 8-clan groups, 7 rounds, monthly | 2 taps a war, plus choosing a target | Attacks against **saved defences** = our `GuildWarDefensiveSnapshots`. Opt-in roster locked at war start defeats hopping. Needs real opponents |
| **Clash Royale** - River Race (Clan Wars 2) | Weekly race of 5 clans; 4 war decks a day each; points ("fame") move your boat; PvE-ish boat battles | 4 battles a day, optional | Clans race a **track** more than each other - still works when opponents are weak or absent. The current three-front war is a crude version |
| **Tap Titans 2** - Clan Raids | Clan vs a titan with body parts and armour; attacks refill on a timer; cards modify damage | spend attacks when they are full | An **idle** game's guild content, PvE, one clan is enough. Closest to our world boss plates |
| **AFK Arena** - Guild Hunting (Wrizz/Soren) | daily guild boss, each member attacks, rewards by total damage; Soren is opened by the guild | 1-2 taps a day, auto-battle | Low-effort, works for any guild size, pays guild coins not premium currency |
| **Idle Heroes** - Guild Boss / Guild War | boss daily; war is ranked guild vs guild on saved teams | 1-3 taps a day | Shows the usual idle order: PvE boss first, PvP war once there are guilds |
| **Lords Mobile** - Guild Fest / Showdown | Fest: collective timed quests from normal play; Showdown: matched rounds | play normally, pick quests | **Normal play counts** - the idea behind our combat/craft/supply fronts |
| **Summoners War** - Guild Siege | 3 guilds on a map of bases; attack enemy defences, hold bases | a few attacks a day | Territory with NPC-free map; needs 3 guilds |
| **Legends of IdleOn** - Guild tasks | members complete tasks from normal play for Guild Points; points buy guild-wide bonuses | nothing extra, just play | Offline progress feeding a guild meter, with no opponent. Melvor has no guilds; not relevant |

## B.3 The four formats worth putting to the owner

### Option 1 - "Siege": guild vs environment, weekly season (recommended for MVP)

A weekly **fortress** per guild with walls/gates/keep stages (reuse the world
boss armour-plate idea: plates are gates and towers). Every member gets
**attack charges** (e.g. 1 every 8 h, cap 3 - spend once a day and lose
nothing). An attack is resolved on the server from the member's own
`CombatStats` against the stage's defence (the same `StatsCalculator` path the
snapshot uses). The guild's **idle activity** during the week - kills
(including offline), crafts, gathering - fills a "war supply" meter that buffs
attacks, which is what the three fronts were for. The guild **treasury** can
buy siege engines with gold (task 37 sink: price scales with the guild's
total treasury or weekly income). All guilds are ranked by stage reached and
time; the board pays only behind a population floor.

- *Day to day:* open Guild, press "Attack" (3 charges), optionally pick which
  gate. 10 seconds. Idle play adds supply without taps.
- *With one guild:* fully playable; the "opponent" is the fortress curve.
- *Matchmaking:* none. Fortress level = the guild's last cleared stage (a
  ladder), so a weak guild climbs, a strong one starts high.
- *Scoring:* stages cleared, then damage; ties by time.
- *Rewards:* guild buff charges and treasury gold (existing guild buffs,
  `GuildActiveBuffs`), materials of the fortress's region, legacy shards,
  cosmetic banner. Diamonds only from a capped ranked board.
- *Anti-abuse:* charges belong to members with tenure (joined before the
  season started), so hopping into a strong guild on Saturday earns nothing;
  there is no opponent to sandbag against; alts add charges but a charge from
  a level-10 alt deals level-10 damage.
- *Reuse:* `GuildRaidEngine`/`GuildRaidStates` (retune damage source),
  `WorldBossEngine` attempt/plate pattern, `GuildWarSnapshotEngine`, guild
  buffs/treasury, `LeaderboardPayoutEngine`'s idempotent weekly payout.
- *Risk:* feels like "a second world boss" unless the stages and the supply
  meter make it distinct. Mitigation: guild-scoped, weekly, collective choices.

### Option 2 - Async matched guild vs guild (Clash of Clans-style), with ghost fallback

Weekly: day 1 prep (roster lock, register defence = snapshot), days 2-3 battle.
Each rostered member gets 2 attacks against the enemy's **defence snapshots**
(one per member, from `GuildWarSnapshotEngine`'s per-member stats); stars from
damage thresholds. **If no opponent exists, the guild fights a "ghost guild"**
generated from its own snapshot scaled to its ladder rating - playable alone,
but less meaningful.

- *Day to day:* 2 attacks, pick targets. Very CoC.
- *Matchmaking:* rating from snapshot power, not headcount; population floor
  (e.g. >= 4 eligible guilds) before real pairing replaces ghosts.
- *Anti-abuse:* roster lock at prep; per-account one war per week; alts in the
  roster are weak defenders the enemy farms (self-punishing); sandbagging by
  benching strong members is limited because rating uses the top-N snapshot.
- *Reuse:* snapshots, the snapshot-vs-snapshot damage maths of
  `GuildCombatSimulationEngine` (not its protocol).
- *Risk:* with today's population it is Option 1 with a worse opponent.

### Option 3 - Weekly race (River Race / Guild Fest), fix the existing three fronts

Keep `GuildWarMatch`'s three fronts (combat / production / supply), make them
count offline progress, and race **all** guilds against a published weekly
target track rather than one paired opponent. Finishing the track pays;
placing pays a little more behind a floor.

- *Day to day:* nothing extra; optionally burn materials to the supply front.
- *Pros:* cheapest - most of the plumbing exists. A good material sink.
- *Cons:* zero decisions - it measures who played most. Alt-farming the
  activity fronts is easy. It is closer to guild quests than a war.

### Option 4 - Territory map (Summoners War Siege / Lords Mobile)

The five regions as a map of holdings; guilds attack NPC garrisons or other
guilds' holdings; holding a node gives a regional buff (yield, drop).
Most fun at scale, the most content and balance work, and it touches the
multiplier ledger (`PowerCeilingTests`) for every buff. Right for later, wrong
for now.

## B.4 Reuse vs replace

| Piece | Keep / adapt / remove | Why |
|---|---|---|
| `GuildWarSnapshotEngine`, `GuildWarDefensiveSnapshots` | **keep** | works, tested, guarded; per-member snapshots would be a small extension for Option 2 |
| `StatsCalculator` path | keep | one damage model - do not add a fourth |
| `GuildFanoutTickCoordinator` scoreboard drain | adapt | must carry the season/war id so mid-session starts reach the player |
| War point queue (`GuildWarPointEvent`) | adapt | add offline sources; budgeted drain; only if Option 1's supply meter or Option 3 is chosen |
| `GuildRaidEngine`, `GuildRaidStates`, opcode 53 | adapt (Option 1) | replace level x online-only damage with charges resolved from stats |
| World boss attempt/plate pattern | copy the pattern | `WorldBossEngine.MaxAttemptsPerEncounter` and plate choice |
| `LeaderboardTierRegistry` floor + `LeaderboardPayoutEngine` | reuse the pattern | population floor and idempotent weekly payout already solved |
| Guild buffs / treasury gold | reuse as reward and sink | existing, visible, working |
| `GuildWarEngine` pairing / resolution / payout | **remove** | exploit, lost grants, wrong resolution |
| `GuildMatchmakingEngine`, `GlobalTournamentMeshService`, `GuildMatchmakingSnapshots`, opcode 50, `ClientPredictedDamage` | **remove** | client-authoritative damage, no reward, duplicate pairer |
| `GuildCombatSimulationEngine` protocol, `GuildWarActiveMatches`, opcode 27, `CombatSimulation*` wire fields | **remove** (keep damage maths if Option 2) | no match writer; tap-per-turn + disconnect is not idle |
| `RegisterGuildDefense`, `GuildDefenseRosters`, opcode 49 | **remove** | write-only |
| `ContributeToWarSupply` opcode 23 | remove or rebuild | stale material map; replace with a picker keyed on real catalogue ids if a supply sink survives |

Removing opcodes: follow the `add-command` skill (the wire is generated -
`npm run generate:protocol`, and `NetworkPacketLayoutGuard` sizes the packet);
a retired opcode number should stay reserved, not reused.

---

# Part C - Recommendation, owner questions, and how to approach it

## C.1 Recommendation

**Option 1 ("Siege") as the MVP, designed so Option 2 bolts on later.**
Concretely: one weekly season object per guild, attack charges per member,
server-resolved attacks from real stats, the guild's idle week feeding a
supply buff, a gold-scaled siege-engine sink, rewards that are mostly guild
buffs/materials/shards, and a ranked board whose diamond payout sits behind a
population floor and a weekly cap below the Delve's 60. The attack resolver
takes an optional bounded "skill multiplier" input so task 36's minigame can
drive both the world boss and the siege. When the floor of eligible guilds is
met, the same season can pair guilds so each attacks the OTHER's snapshot
fortress (Option 2) - same charges, same resolver, a different defence.

Why not the others first: Option 2 is Option 1 with a ghost until there are
guilds; Option 3 is cheap but has no decisions and rewards alts; Option 4 is a
season of work for a population of one guild.

**Independently of the choice: Phase 0 (kill switch) should ship now.** It is
a live latent diamond exploit plus two unattended loops with late guards.

## C.2 Questions for the owner

1. Format: Siege (PvE) first, or do you want head-to-head from day one even if
   it is mostly against ghosts?
2. Cadence: weekly season (Mon-Sun) or 3-day wars? What reset time (the Delve
   and leaderboard already use a weekly boundary - same one)?
3. Taps: how many attacks a day feels right (1, 3, "charges that refill")? Is an
   "auto-spend at reset" option acceptable for pure idlers?
4. Interactivity: should an attack be a plain button now and gain the task-36
   minigame later, or wait for task 36?
5. Rewards: are diamonds on the table at all? If yes, what weekly cap per
   member (suggest <= 20, well under the Delve's 60)? Or keep diamonds to the
   ranked board only?
6. Sink: may the guild treasury spend gold on siege engines, priced as a share
   of the treasury or of weekly income (task 37)? Should members' personal gold
   be spendable too?
7. Solo players: does a one-person guild get a full season (it is the only
   guild today)? Should guildless players see anything?
8. Offline: should offline kills/gathering feed the guild's supply meter?
9. Anti-abuse: minimum tenure before a member's charges count (1 day? season
   start?); minimum level (10, matching guild creation and the leaderboard)?
10. Old code: agree that the cross-shard, turn-based and defence-roster systems
    are removed rather than kept "for later"?
11. Naming/theme: "Siege", "Guild War", something folk-themed?

## C.3 How to approach this (for the implementing agent)

1. **Audit** - done (this file). Re-verify the line numbers; the tree moves.
2. **Brainstorm with the owner** - use Part B and §C.2; answer in Czech (see
   memory). Do not start a spec before questions 1, 2, 5 and 10 are answered.
3. **Spec** - `docs/superpowers/specs/2026-09-XX-guild-wars-design.md`: format,
   data model (tables, snake_case or not - decide and note it for
   `CURRENT_IMPLEMENTATION_STATE.md` §3), charge and damage formulas, reward
   table with its diamond budget, anti-abuse rules, wire fields (each one
   hydrated at login or on `RuntimeOnlyByDesign` with a reason), what each
   phase deletes.
4. **Phased plans** - one plan file per phase group, below. Each phase is its
   own PR and deploys on its own.

## C.4 Phase breakdown (for Option 1, "Siege"; adjust once the spec exists)

### Global constraints

- **The four orphaned handlers are removed ONLY in the phase that lands the new
  UI (Phase 3), as the whole `GuildOps.svelte:251-306` block plus its imports,
  and `BASELINE` in `client_web/scripts/typecheck-ratchet.mjs:43` is lowered in
  the SAME commit** (to 0 if nothing else is outstanding - then the ratchet
  comment and CLAUDE.md's Conventions paragraph about "four pre-existing
  errors" are updated too). Until then they stay: they are the baseline.
- Never trust a client damage number. The only client inputs are a target
  index, a charge count, and (later) a server-issued minigame token.
- Every new `StartCron` loop goes into `CronWorkerGuardTests`' inventory with its
  try opening *before* `CreateScope`, and every drain takes a budget.
- Every new wire field is hydrated at login or listed in
  `StateUpdatePacketFieldCoverageTests.RuntimeOnlyByDesign`; run
  `npm run generate:protocol`.
- A diamond grant goes through the payload (or a pending-grant row the
  checkpoint cannot overwrite), never a raw `UPDATE "PlayerRecords"` behind a
  live session - see A.3(1).
- Stop the server before `dotnet build`; `dotnet test` needs Docker.
- A rejected command tells the player why (`EnqueueCommandResult`), never a
  silent rollback and never a disconnect for an ordinary stale screen.

### Phase 0 - Make the current code safe (S, can ship before the brainstorm)

**Goal:** no path can pay a diamond or burn a material for a war that does not
exist as a feature.

**Files:** `Engine/GuildWarEngine.cs`, `Engine/GuildMatchmakingEngine.cs`,
`Program.cs:551-554`, `Domain/Social/GuildWarTickCoordinator.cs`,
`client_web/src/routes/GuildOps.svelte` (visible war panel only).

- [ ] Stop starting `GuildWarEngine` and `GuildMatchmakingEngine` (or gate both
      behind a config flag defaulting OFF); keep `GuildWarSnapshotEngine`.
      Update `CronWorkerGuardTests`' inventory in the same commit.
- [ ] Make opcodes 23/27/49/50 answer a "not available" command result instead
      of acting or disconnecting.
- [ ] Hide the visible "Guild war" panel (`GuildOps.svelte:428-470`) with
      `{#if}` - not `display:none` - or replace it with a "coming soon" line.
      Do NOT touch the four handlers.
- [ ] Test: a unit test that pairing cannot run and that opcode 50 no longer
      reaches `GlobalTournamentMeshService`.
- [ ] Verify: `dotnet test`; `npm run check:ratchet` stays at 4; `npm run
      exercise` (the Guild screen still loads).

### Phase 1 - Season model and resolver, server only (M)

**Goal:** a guild has a weekly siege season with stages; a member can spend a
charge and the server resolves damage from real stats. No UI yet.

**Files:** new `Domain/Social/GuildSiegeEngine.cs` (engine) and
`GuildSiegeRegistry.cs` (stage curve, charge rules - one place for numbers),
`Models/` + one migration (season row per guild per week; attempt/charge row
per member per season), a new opcode via the `add-command` skill,
`GuildWarTickCoordinator.cs` (or a renamed coordinator), `StateUpdatePacket`
fields (stage, stage HP, charges) hydrated in `StateCheckpointManager`.

- [ ] Stage curve pinned by a shape test (never descends, like
      `MonsterLadderTests`) and measured against the geared player HP/attack
      from `ProgressionRateTests` - assert, don't print.
- [ ] Charges: refill on a server clock, cap, tenure and level gates; a
      rejected attack returns a command result.
- [ ] Mid-session: a season starting while a member is online reaches that
      member's payload (the `ActiveGuildWarId` defect must not recur) - test it.
- [ ] Tests: resolver uses `StatsCalculator`; a level-10 alt's charge is weak;
      a member who joined after season start has no charges; field coverage.
- [ ] Apply the migration locally by hand (`--migrate`) before sign-in.

### Phase 2 - Supply meter and gold sink (M)

**Goal:** the guild's idle week matters and gold has somewhere to go.

**Files:** point sources in `SimulationEngine` (kill), `OfflineSimulationEngine`
(**the offline path too** - three paths grow a level, and the same lesson
applies), crafting and gathering coordinators; a budgeted aggregation drain;
treasury purchase of siege engines (price as a share of treasury/income).

- [ ] Every multiplier the meter or engines add has a cap or a curve and
      appears in `PowerCeilingTests`' ledger.
- [ ] `GatheringEconomyTests`/`GoldSinkAffordabilityTests` extended with the new
      sink in the same units.
- [ ] Heartbeat reports the new queue's depth.

### Phase 3 - The UI, and removing the old handlers (M)

**Goal:** a player can see the season, spend charges and see the result.

**Files:** `client_web/src/routes/GuildOps.svelte` (or a new route registered
in `client_web/scripts/screens.mjs`), `client_web/src/lib/net/commands.ts`,
`rest.ts`, `client_web/scripts/typecheck-ratchet.mjs`, CLAUDE.md Conventions,
`StateUpdatePacketFieldCoverageTests` (remove the seven "on the roadmap"
entries or the fields themselves).

- [ ] Build the Siege panel; 44px targets; no ticking values inside a control
      (Android `<select>` lesson); `{#if}` rather than `<details>`.
- [ ] **In the same commit:** delete `GuildOps.svelte:251-306` (the whole
      cross-shard and battle-turn blocks) and the now-unused imports
      (`registerGuildDefense`, `submitShardAttack`, `executeCombatTurn`,
      `fetchGuildShardMatch`, any `typicalHit` left unread), delete the
      matching senders in `commands.ts:842-920` and `rest.ts:643-667`, and
      lower `BASELINE` to the new count. Run `npm run check` raw and confirm
      the number, don't assume it.
- [ ] `exercise.mjs`: spend a charge and assert the stage HP moved and the
      charge count dropped - and restore/round-trip what it spends (a check
      that spends fixture state passes once). Seed the dev fixture into a
      guild with a season (`DevFixtureInvariantTests`).
- [ ] `check:clipping`, `check:overlap`, `check:touch`, `check:safearea` on the
      new panel; `smoke:screens` against production after deploy.

### Phase 4 - Rewards and the ranked board (M)

**Goal:** the season pays, once, visibly, inside the diamond budget.

- [ ] Idempotent weekly settlement (copy `LeaderboardPayoutEngine`'s tracking
      pattern); mail or notice to every member saying what they got.
- [ ] Diamonds only behind a `MinimumRankedPopulation`-style floor of eligible
      guilds and a per-member weekly cap asserted against
      `DelveRegistry.MaxDiamondsPerWeek` in a test (as
      `LeaderboardRewardTests` does).
- [ ] Grants through the payload / pending-grant path, never raw SQL behind a
      live session.

### Phase 5 - Remove the dead war code (M, server)

**Goal:** one war system in the tree.

- [ ] Delete `GuildWarEngine` (its useful fragments already moved),
      `GuildMatchmakingEngine`, `GlobalTournamentMeshService`,
      `GuildCombatSimulationEngine`, `GuildDefenseRoster`, their DbSets,
      opcodes 23/27/49/50 (numbers stay reserved), `ClientPredictedDamage`
      (and the `ClientCommandPacket.cs:266-275` comment), the
      `CombatSimulation*`/`GlobalNodeRemainingHp`/`ActiveMatchMmr` wire fields;
      a migration dropping the empty tables (confirm 0 rows in production first -
      it is not additive).
- [ ] Update `CronWorkerGuardTests`, `CommandGateOrderingTests`,
      `E2ETestHarness`, `HardenedEngineIntegrationTests` references;
      `generate:protocol`; update `CURRENT_IMPLEMENTATION_STATE.md` and
      `NEXT_STEPS_BACKLOG.md`.

### Phase 6 (later, behind the population floor) - Guild vs guild

Pair eligible guilds weekly; each attacks the other's snapshot fortress with the
same charges and resolver; ghost fortress below the floor. Per-member
snapshots, roster lock at season start, one war per account per week.
