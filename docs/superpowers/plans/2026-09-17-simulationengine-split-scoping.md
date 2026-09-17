# Scoping: splitting `SimulationEngine.cs`

Status: **decision-support document, not an implementation plan.** TASK_BOARD.md
item 21 is explicit that this file needs its own planning session before any
task list gets written, because a decomposition picked without the owner's
input is presumptuous. This document is that planning session's input: what
the file actually contains (read in full, not grepped), where the real
coupling is, three candidate shapes with honest tradeoffs, and the specific
questions that block writing an executable plan.

**The one constraint every option below is scored against:** `SimulationEngine`
is the only thing that mutates `TickStatePayload`. Whatever shape this takes,
a `TickStatePayload` field may still be written from exactly one place: the
tick thread, inside a method that runs synchronously as part of the 10Hz loop
or its command-dispatch pass. A "coordinator" in every option below is a
method/class invoked BY the tick thread, never a class with its own thread or
its own write access resolved independently.

File: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs`, 6,667 lines
(the board's "6,650" is close; grep-counted at scoping time). It already lives
in `Domain/Combat` even though it orchestrates every other domain — the
satellite engines it calls (`CraftingEngine` in `Domain/Economy`,
`VillageManagementEngine` in `Domain/Progression`, `GuildManagementEngine` in
`Domain/Social`, etc.) are already split by domain. This file is the one place
that split stopped.

---

## 1. What the file actually contains

Read start to finish in ~500-1000 line windows (not reconstructed from grep).
Four structurally distinct regions:

| Lines | What | Character |
|---|---|---|
| 1–712 | Engine identity, lifecycle (`Start`/`Stop`/`ExecuteDataDrainage`/`ShutdownGracefully`), `_activePlayers`/`_liveSessionContexts`/`_guildMembersIndex` management, `AddActivePlayer`/`RemoveActivePlayer` (the single choke point for session bookkeeping), `SafeDispatchAsync` helper, `BattlePassWorkerLoop` | Infra + a **second background thread** |
| 713–4040 | `EngineLoop` — the 10Hz tick thread's actual body | The core; see §3 |
| 4042–4628 | Command-adjacent async helpers (`ChangeCharacterActivityAsync`, `RegisterGuildDefenseAsync`, `SubmitShardAttackAsync`, `ExecutePassPurchaseAsync`, `ExecuteBattlePassClaimAsync`, mastery/XP math) | Mixed domains, called from §713–4040 |
| 4629–6667 | `ProcessTick` → `ProcessPassiveVillageTick` / `ProcessAllSlotSubTicks` → `ProcessAccountTick` / **`ProcessSubTick`** (5393–6665, ~1,270 lines) | Per-player, per-character-slot simulation |

`EngineLoop` itself (713–4040) is not one blob — it has three distinct
sub-passes plus a broadcast tail, each with a different shape:

1. **Notification-queue drain plane** (748–1657, ~33 blocks). Every
   `while (_playerRegistry.XQueue.TryDequeue(out var n)) { ... }` block folds
   the result of an already-off-thread domain engine call back into
   `TickStatePayload`. This is where async work (a DB transaction run via
   `SafeDispatchAsync`) rejoins the single-writer tick. Each block is already
   self-contained: one queue in, one or a few payload fields out.
2. **Command-dispatch plane** (1658–3411). A single `if (cmd.Command == X) {...}
   else if (cmd.Command == Y) {...}` chain — not a `switch` — over ~40
   `CommandType` values. A shared gate runs once before the chain for every
   non-internal command: `LastClientCommandAtMs` stamp → epoch validation →
   `ValidateCommand` → `ValidateNoAntiCheatPayload` → `ValidateNoPushCompliancePayload`
   (lines 1875–1938). Each branch then either mutates the payload inline
   (attribute spend, respec, activity change) or calls `SafeDispatchAsync` into
   an already-existing domain engine and enqueues a notification/`ReloadState`
   for the drain plane above to pick up next tick.
3. **Simulation plane** (3424–3556 call site → `ProcessTick` at 4629).
   Per active, non-suspended player: anti-cheat challenge-timeout check,
   consumable-ingestion drain, then `ProcessTick`/`TrackState` inside a
   `try/catch` that exists specifically so one player's exception can't kill
   the thread for everyone (documented in CLAUDE.md; confirmed by reading —
   the `finally`-wrapped slot-register swap immediately below is what makes
   that isolation safe for multi-character accounts specifically).
4. **Broadcast plane** (3558–3995, gated to run every 10th tick = 1Hz). Reads
   fields from *every* domain to build a ~300-field `StateUpdatePacket`,
   dirty-checks it against a per-player last-sent cache, sends.

`ProcessSubTick` (5393–6665) is its own animal: one function that branches on
what `ActiveActivityId` currently is — a crafting recipe, a gathering node, or
(falling through) a monster — and the monster branch is the entire combat
resolution: spawn, player-attacks-monster (hit/crit/mitigation/lifesteal/burn/
thorns/combat-event-feed), monster-attacks-player (symmetric), auto-eat,
death/respawn, and kill rewards (XP, gold, codex enqueue, loot-drop enqueue,
region unlock, boss first-clear, premium-currency drop). It has **one early
`return` inside the death branch** (line 6328) that is load-bearing: it is what
keeps death and kill-reward mutually exclusive within one call. A naive split
into always-both-called "resolve death" / "resolve kill" methods would need to
reproduce that exclusivity explicitly or risk running both.

---

## 2. Domain-area inventory (refined against what was actually read)

The board's list — Guild, Breeding, WorldBoss, Market, Village, Gathering,
Crafting, plus persistence, anti-cheat telemetry, notification-queue draining —
is directionally right but undercounts at the granularity a coordinator split
needs, and understates how many of these are **not independent**:

| Area | Where it lives | Independent? |
|---|---|---|
| **Combat resolution** | Core of `ProcessSubTick`, ~900 of its 1,270 lines | No — shares the function and its locals (`combatStats`, `effectiveMaxHp`) with gathering/crafting below |
| **Gathering** | `ProcessSubTick`, progress + loot roll (~230 lines) | No — same function, same activity-id dispatch as combat |
| **Crafting-as-job** | `ProcessSubTick` (~25 lines, progress only) + `CraftingTickQueue`/`CraftingCompletionQueue` drains | Progress logic is entangled in `ProcessSubTick`; actual craft resolution is already delegated to `CraftingEngine` |
| **Village** | `ProcessPassiveVillageTick` + 4 queue drains + 4 command branches | Mostly self-contained |
| **Guild (core)** | 3 queue drains + `_guildMembersIndex` (shared infra, see below) + 3 command branches | Owns shared infra other guild sub-systems depend on |
| **Guild War** | Scoreboard drain, supply, defense registration, shard attack / cross-shard mesh, **plus `InitiateNodeMigration`** (live cross-shard player migration over Redis, command-dispatch's first branch) | No — reads `_guildMembersIndex`; node migration is arguably session-infra wearing a guild-war hat |
| **Guild Logistics** | 1 drain + 1 command | Reads `_guildMembersIndex` |
| **Guild Raid** | 1 drain + 1 command | Reads `_guildMembersIndex` |
| **Guild Combat Simulation** | 1 drain + 1 command (turn-based async guild-vs-guild) | Reads `_guildMembersIndex` |
| **World Boss** | 1 drain + 1 command | No — `AttackWorldBoss` reuses `EffectiveMilliAttackFor`, the same "how hard does this player hit" authority live combat uses |
| **Market** | 1 drain (incl. an offline "settlement rescue" fallback) + 2 command branches | Mostly self-contained |
| **Breeding** | 1 drain (birth gold debit) + 2 commands + child-maturation countdown embedded in `ProcessAccountTick` + aging in `ProcessAgeSlot` | No — aging/maturation run once-per-tick account-side, not in a Breeding-owned pass |
| **Equipment / per-character gear** | 1 drain + 4 commands + `SwapRegisterWith` (the slot-swap mechanism) | No — the swap mechanism is load-bearing shared infra, not Equipment's alone |
| **Larder / consumables** | 1 drain + 2 commands + auto-eat **inside** the combat branch of `ProcessSubTick` | No — auto-eat fires mid-fight, cannot be pulled out of combat's sequencing |
| **Skill tree / Inheritance / Attributes / Hall of Ancestors** | Several small commands; `SpendAttributePoint`/`RespecAttributes` resolve **entirely on the tick thread**, no DB dispatch at all | Read (not written) from almost everywhere in combat/gathering |
| **Billing / IAP / Battle Pass / Chronicle Pass** | 1 drain + 4 commands + 2 large async helpers + **`BattlePassWorkerLoop`, a second background thread** | Mostly self-contained, but owns its own thread |
| **Anti-cheat / session lifecycle** | Epoch gate, challenge-response, quarantine, command-result ring buffer, Login/Logout/ReloadState, node migration | Gates *every* other domain's commands — not a peer, a prerequisite |
| **Persistence orchestration** | `FlushStateAndAdvance` called inline from ~10 command branches; `TrackState` called once per player per tick | Cross-cutting call pattern, not a domain |
| **Broadcast/serialization** | The `StateUpdatePacket` literal + dirty-check cache | Reads every domain's fields; not a domain itself |
| **Quests / Achievements / Codex** | `QuestEngine.IncrementProgress`, `CodexEngine.KillEventQueue` calls scattered through combat/crafting/gathering | No — invoked from inside other domains' code paths |

Takeaway: the notification-drain plane (§1.1) is **already** close to
per-domain coordinators, just physically inlined in one loop body. The
command-dispatch plane (§1.2) is a router where most branches already
delegate to a real domain engine — the entanglement there is the *shared
gate*, not the branches. `ProcessSubTick` is where domains that should be
independent (gathering, crafting, combat, auto-eat, world-boss damage) are
actually fused into one function by activity-id branching and shared locals.

---

## 3. `TickStatePayload`: the shared state

`server/FolkIdle.Server/Engine/TickStatePayload.cs`, 835 lines, a single flat
blittable struct (no sub-structs beyond `Slot2Activity`/`Slot3Activity` and a
couple of small embedded buffers) — ~200+ fields, one per concern, no
namespacing. This is the "everything a coordinator split needs to know which
fields are genuinely domain-local vs. cross-cutting" the task asked for.

**Genuinely domain-local** (owned and consumed by one area, safe to hand a
coordinator exclusive write access to): `Food1..3_ItemId/Count`,
`ForgeUpgradeCount`/`HighestForgeSynthesisTier`, `LumberjackLevel`/`MineLevel`/
`WarehouseLevel`/`TownHallLevel` and the `Accumulated*`/`Pending*Delta`
production accumulators, `Skill_*` (19 fields, though *read* everywhere),
`Inherit_*` (6 fields, same caveat), `CachedGuildLogisticsLevel`/
`GuildLogisticsCurrentStock`/`TargetRequirement`, `WorldBossAttemptCount`/
`SessionEndsEpoch`.

**Genuinely cross-cutting** (multiple domains write or must agree on it):

- **`CurrentGold` / `RedisPendingGoldDelta` / `RequiresRedisFlush`.** Written by
  combat kills, village passive gold, world-boss regional-boss diamond path
  (no — that's `PremiumCurrency`), auto-salvage, chest sale, market rescue,
  mail claim, birth (debit), infrastructure upgrade (debit), villager
  recruitment (debit). This is CLAUDE.md's documented two-gold-paths trap —
  any split has to keep the "earned fresh" writers (combat, auto-salvage,
  village passive) and the "already-debited-or-credited-elsewhere" writers
  (birth, infra, recruitment, chest sale, mail) visibly distinct, ideally by
  which coordinator owns the call, not by convention alone.
- **`PremiumCurrency`.** Written by Billing's purchase/claim paths *and* by
  combat's kill-reward branch (regional-boss guaranteed drop, 0.05% random
  drop on regular kills). Not billing-exclusive.
- **The active-register fields** (`ActiveActivityId`, `PlayerHp`,
  `CurrentMonsterId`/`Hp`, `CombatTargetTickAccumulator`, all `Equipped*Id`,
  `Slot1_CharacterId`/`AgeTicks`/`AgePhase`/`GeneticVector`). These are what
  `SwapRegisterWith` exchanges with the parked `Slot2`/`Slot3` state. Any
  coordinator that runs per-character logic must go through the same
  swap-in/swap-out discipline or corrupt which character is "active."
- **`GuildId`** and `_guildMembersIndex` (not a payload field, but the
  in-memory index keyed by it) — written at session boundaries, read by five
  different guild-adjacent notification drains.
- **`IsDirty`** — set by nearly every mutation in the file; it is the
  checkpoint's own dirty flag (`StateCheckpointManager` owns consuming it),
  not owned by any one domain.
- **`CachedEffectiveMaxHp` / `CachedEffectiveMilliAttack` / `CachedCodexYieldMultiplier`
  / `CachedCodexDamageMultiplier` / `CachedAffixTotals` / `CachedSetIds`** —
  computed by combat, consumed by combat, but *sourced* from Equipment,
  Codex, and Skill Tree drains respectively. These are the concrete shape of
  "coordinator boundaries have to agree on a cache contract," not a place
  where one domain can simply own the field.

---

## 4. The tick loop's own structure, restated as "what runs where"

- **Every 10Hz pass, unconditionally:** era-transition guard, benchmark
  injection, the ~33 queue drains, the command-dispatch chain, the
  `_readyLogins` drain, `ProcessTick` per active player (→ village passive +
  all slot sub-ticks), metrics bookkeeping, sleep-to-cadence.
- **Every 10th pass (1Hz):** anti-cheat challenge issuance + the broadcast
  packet build/send.
- **Dispatched off the queue, not the clock:** every `CommandType` branch —
  runs whenever a client command is dequeued, same tick loop, same thread.
- **Off-thread entirely, rejoining next tick:** everything behind
  `SafeDispatchAsync` (an EF transaction, a Redis call) — writes to a
  notification queue, picked up by the matching drain block on a later pass.
- **A second, independent thread (`BattlePassWorkerLoop`):** polls
  `_liveSessionContexts` for queued battle-pass claims at ~50 ops/sec,
  resolves them via a direct-blocking `.GetAwaiter().GetResult()` call on its
  own thread, then enqueues a `ReloadState` **command** back onto the main
  tick thread's `CommandQueue` — it does not touch `TickStatePayload` directly.
  It already respects the single-writer invariant, but only by convention;
  nothing enforces that a future edit can't "simplify" it into direct payload
  access.

A "coordinator" in this codebase's terms is therefore not one shape — it's at
least three: a **notification-drain coordinator** (owns one or more queues,
runs once per tick, writes its own fields), a **command coordinator** (owns a
set of `CommandType` values, runs when dequeued, downstream of the shared
gate), and — for the hardest case — a **simulation-phase coordinator** (owns a
branch inside the per-character tick, reads/writes the active register). The
board's phrase "adapters for tick mutation, persistence, and notifications"
maps to: tick mutation = the simulation-phase shape, notifications = the
drain shape, persistence = already exists as `StateCheckpointManager`
(`TrackState`/`FlushStateAndAdvance`/`LoadPlayerState`/`FlushBatch`) and just
needs its ~10 inline call sites routed through whatever coordinator now owns
each command, not re-invented.

---

## 5. Candidate decomposition shapes

None of these is "the" answer — they're presented for the owner to weigh,
including combining or sequencing them.

### Option A — Extract the notification-drain plane only

Turn each `while (queue.TryDequeue...) { ref payload... }` block into a named
method on a small per-domain coordinator (`VillageTickCoordinator.DrainNotifications(ref TickStatePayload, ...)`,
etc.), called in sequence from `EngineLoop`. `EngineLoop` keeps owning
`_activePlayers`/`_guildMembersIndex` and the calling sequence. Command
dispatch and `ProcessSubTick` untouched.

- **Single-writer invariant:** trivially preserved — same thread, same call
  graph, just named methods instead of inline blocks.
- **Churn to drain loops / cron workers:** low. Each block is already
  self-contained (one queue, one or a few fields); this is close to
  mechanical extraction.
- **Incrementality:** high. One domain's drain can move per PR without
  touching any other domain's.
- **Blast radius if wrong:** low–medium. A misbehaving extraction affects
  only that domain's fields and is easy to isolate and revert.
- **Honest downside:** doesn't touch the two most tangled regions (command
  dispatch's shared gate, `ProcessSubTick`'s fused combat/gathering/crafting).
  Reduces the file's line count in one section without reducing its real
  coupling much.

### Option B — Extract command dispatch into per-domain command coordinators behind a dispatch table

Replace (or wrap) the `if/else if` chain with something like
`Dictionary<CommandType, ICommandCoordinator>`, where each coordinator handles
a cluster of related `CommandType`s (Guild*, Market*, Village*, etc.),
downstream of the shared anti-cheat/epoch gate, which is extracted **first**
as its own always-run adapter.

- **Single-writer invariant:** preserved *if and only if* every coordinator's
  handler still runs synchronously on the tick thread for inline mutations,
  and still routes exclusively through `SafeDispatchAsync` (not a new
  `Task.Run`) for anything async — the shape most likely to leak.
- **Churn:** medium. The gate currently runs once, in one fixed order, before
  the chain, for every command; extracting it is itself a single high-reach
  change (every command in the game passes through it) that has to land
  correctly before any per-domain split is safe to build on top of.
- **Incrementality:** medium–high *after* the gate is extracted — domains can
  then move one at a time. Before that, it's a single all-or-nothing
  prerequisite step.
- **Blast radius:** medium, concentrated in the gate-extraction step; low per
  subsequent domain move.

### Option C — Split `ProcessSubTick`'s three activity branches into coordinator methods sharing an explicit tick-context

Extract crafting-progress, gathering, and combat into three methods, each
taking `ref TickStatePayload` plus a small explicit context struct
(`combatStats`, `effectiveMaxHp`, `activeRaceId`, `activeAgePhase` — currently
free locals computed once and reused across all three branches).

- **Single-writer invariant:** preserved if the three methods stay
  static/synchronous, same thread, same struct.
- **Churn to drain loops:** none — this region doesn't touch them.
- **Incrementality:** low. The three branches are sequenced by one
  activity-id `if/else if` *inside* the function; a first cut can extract
  each branch's *body* while leaving the dispatch centralized (safer,
  smaller diffs), or move the dispatch itself (touches everything at once).
- **Blast radius:** **high**. This is the single most landmine-dense region
  in the file — CLAUDE.md documents a dozen "found the hard way" fixes
  living in exactly these ~1,270 lines (the armor-mitigation formula, the
  interval-crossing attack-cadence fix, milli-HP long-vs-int overflow,
  crit-chance clamping, the lifesteal percent-vs-fraction bug, the
  death-branch early return). A slip here is a live gameplay regression, not
  a compile error, and several of these bugs were found by a player, not a
  test.

**No option scores well on all four axes simultaneously** — that's the honest
finding, not a gap in the analysis. A before C is the order the risk profile
argues for, but A alone doesn't reduce the file's real complexity much, which
is itself a reason the owner might reject doing A in isolation.

---

## 6. Risks and landmines

### Already documented in CLAUDE.md — confirmed present, and exactly where a split would touch them

- **Two gold paths.** `AutoSalvageQueue` (lines ~1218–1230) and
  `ChestSaleGoldQueue` (~1242–1252) sit directly adjacent in the drain plane.
  If a split moves them into different coordinators (e.g., "Loot" vs.
  "Village/Chest"), the paired comment explaining why one sets
  `RedisPendingGoldDelta` and the other doesn't must travel with **both**
  sites, or a future editor won't see the sibling case that makes the rule
  legible.
- **A Redis frame is not a checkpoint.** `TrackState` is called once per
  player per tick (line 3546), immediately after `ProcessTick`, inside the
  same `try/catch`. Any split that moves the *caller* of `ProcessTick` out of
  `EngineLoop` must keep `ProcessTick` → `TrackState` atomic per player on the
  same pass, or reopen exactly the bug CLAUDE.md records.
- **Cron worker silent death / `SafeDispatchAsync`.** Every async engine call
  in command dispatch already goes through `SafeDispatchAsync`, which is the
  fix for the bare-`Task.Run` pattern. A coordinator split must not
  reintroduce raw `Task.Run` at new call sites — `SafeDispatchAsync` (or an
  equivalent with the same try/catch-and-disconnect contract) has to move
  with whatever now owns dispatching, not get reinvented per coordinator.
- **Silent rollback / "what does the player see."** Command branches
  deliberately choose between ignore (`CraftItem`, `UpgradeTool`,
  `RequestUnlockSkill`), report-via-CommandResult (`ExecuteForgeFusion`,
  `ConsumeConsumableAsset`), and disconnect (most validated branches). A
  dispatch-table refactor risks flattening these into one generic error path
  — that per-branch decision is exactly what several CLAUDE.md paragraphs
  exist to preserve.

### Found while reading, not already named in CLAUDE.md

1. **The anti-cheat/epoch gate's ordering is enforced only by its position in
   one function**, not by any structural guarantee. Lines 1875–1938 run, in
   this exact order, for every non-internal command. Nothing currently pins
   that order as a contract — the closest thing found is
   `HardenedEngineIntegrationTests.cs` (12,292 lines, exercises the running
   engine end-to-end), but whether it specifically asserts gate ordering
   under a coordinator refactor wasn't verified here and should be before
   Option B is attempted.
2. **`BattlePassWorkerLoop` is a second background thread that respects the
   single-writer invariant only by convention** (it routes through the
   `CommandQueue` rather than touching `TickStatePayload` directly). Nothing
   stops a future edit — inside or outside a coordinator split — from
   "simplifying" it into direct payload access "since it's just one field."
   Worth either documenting this convention explicitly as part of whatever
   adapter owns notifications, or giving it the same enforcement the rest of
   the invariant gets.
3. **`_guildMembersIndex` is multi-domain shared state with no single owner.**
   Written at session boundaries (owned by session lifecycle, not by
   "Guild"), read by five separate drains (Guild core, Guild War, Guild
   Logistics, Guild Combat Simulation, Guild Raid). If those five become five
   separate coordinators — which the domain inventory in §2 implies — the
   index's ownership is an open question, not a detail: one coordinator owns
   it and the rest borrow read access, or it moves up into whatever owns
   session lifecycle (`AddActivePlayer`/`RemoveActivePlayer`) and none of the
   five own it.
4. **`ProcessSubTick`'s death branch's early `return` (line 6328) is load-
   bearing control flow, not incidental.** It is what keeps "the character
   died this tick" and "the character killed something this tick" mutually
   exclusive within one call. A split into always-both-invoked
   `ResolveDeath()` / `ResolveKillReward()` methods needs to reproduce that
   exclusivity explicitly, or risks running kill-reward logic in the same
   tick as a death (or vice versa) in a case the current single early-return
   silently prevents.
5. **The register-swap discipline lives one level above `ProcessSubTick`, in
   `ProcessAllSlotSubTicks`'s `try/finally`.** Anything that calls
   `ProcessSubTick` (or an extracted successor) from a *new* caller — e.g., a
   coordinator invoked directly instead of through
   `ProcessAllSlotSubTicks` — has to reproduce the swap-in/swap-out-in-finally
   pattern exactly, or a thrown exception mid-tick on a multi-character
   account silently leaves the wrong character's gear/HP/activity "active"
   for every subsequent read (packet broadcast, next tick, checkpoint). This
   is a sharper, more specific instance of the general tick-thread-exception-
   isolation point CLAUDE.md already makes.

---

## 7. Proposed minimal first increment

**Legacy Store** (the `LegacyStoreUpdateQueue` drain at lines 1497–1509, plus
the `PurchaseLegacyUnlocks` command branch) is the strongest candidate to
extract first, as a proof that the notification-drain coordinator shape
(Option A) works end-to-end, before committing to migrating anything else.

Why this one specifically, against the other small candidates considered
(Inheritance, Mail Claim, Billing):

- **Smallest surface.** One ~13-line queue drain, one ~15-line command
  branch. Both are already near-pure delegation to `LegacyStoreEngine` — the
  business logic isn't in `SimulationEngine` to begin with, only the
  plumbing is.
- **One-directional field dependency.** `LegacyShardBalance`,
  `CitizenMultiSlotsUnlocked`, and `CachedLegacyPerks` are *written* only by
  this domain's drain and *read* (never written) elsewhere — combat consults
  `CachedLegacyPerks` via `LegacyPerkResolver` for combat-speed/gold/XP
  bonuses, but never mutates it. That's the cleanest possible shape for a
  first "domain owns write access, others get a read-only cached value"
  extraction.
- **No gold-path ambiguity.** Doesn't touch `CurrentGold`/
  `RedisPendingGoldDelta` at all, so it sidesteps the single most
  well-documented landmine in the file entirely.
- **No `_guildMembersIndex` dependency, no register-swap participation.**
  Doesn't need either of the two pieces of genuinely tricky shared
  infrastructure identified in §6.

Runner-ups, in case Legacy Store turns out to have a wrinkle not visible from
reading alone: **Inheritance** (same shape — one drain, one command, fields
read-only elsewhere) and **Mail Claim** (equally small, but does touch
`CurrentGold`, so it exercises the gold-path question sooner rather than
avoiding it — useful if the owner would rather prove the gold-path split
works early rather than defer it).

---

## 8. Open decisions for the owner

1. **Which decomposition shape(s), in what order.** The risk analysis in §5
   argues for A before C, with B's prerequisite (extracting the shared
   anti-cheat gate) treated as its own single high-reach step rather than
   folded into "the first domain move." Does the owner want A alone as a
   deliverable, or only as a step toward B/C?
2. **What a "coordinator" is, concretely, as a C# shape.** A static class
   with methods taking `ref TickStatePayload` (matches the file's existing
   zero-allocation style and needs no DI wiring), or an instantiated class
   holding its own engine references (matches how `_guildEngine`,
   `_villageManagementEngine`, etc. are already injected into
   `SimulationEngine`'s 26-parameter constructor, and would make coordinators
   independently testable/mockable)? This affects whether the split reduces
   `SimulationEngine`'s constructor sprawl or just relocates it.
3. **Who owns `_guildMembersIndex`.** One of the five guild-adjacent
   coordinators, a session-lifecycle coordinator, or does it stay on
   `SimulationEngine` itself while everything else moves around it?
4. **Whether the anti-cheat/epoch gate is extracted as a standalone,
   always-first adapter before any command-dispatch split (Option B's
   prerequisite), and whether existing coverage (`HardenedEngineIntegrationTests.cs`,
   12,292 lines) is confirmed to pin its ordering before that extraction is
   attempted — or whether new characterization tests need writing first.**
5. **Whether `ProcessSubTick`'s three activity branches are touched at all in
   a first phase.** Given the landmine density in §6, is this deferred
   indefinitely, attempted only as "extract each branch's body, keep the
   dispatch centralized" (Option C's safer sub-variant), or considered out of
   scope for this initiative entirely and left as a second, separately-scoped
   effort?
6. **In-place vs. parallel scaffold.** Small successive PRs against the live
   `SimulationEngine` class, or new coordinator classes built and
   unit-tested standalone first, then wired in behind a flag/feature toggle —
   given the task's own question about blast radius if a migration is
   interrupted partway through?
