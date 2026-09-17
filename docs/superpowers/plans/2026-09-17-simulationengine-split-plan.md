# Splitting `SimulationEngine.cs` — executable plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs`
(6,667 lines, confirmed by `wc -l` on 2026-09-17) to a tick loop that
*sequences* work rather than one that *contains* it — without changing a single
observable behaviour of the running game. Every task in this plan is a
behaviour-preserving move. Nothing here fixes a bug, tunes a number, or
"cleans up while we're in there."

**Architecture:** `SimulationEngine` owns the 10Hz tick thread and is the only
writer of `TickStatePayload`. That does not change. What changes is *where the
code lives*: each `while (queue.TryDequeue) {...}` block becomes a named method
on a per-domain **static** coordinator class (Phase 1), each command
`else if` branch becomes an entry in a dispatch table behind one extracted
anti-cheat gate (Phase 2), and `ProcessSubTick`'s three activity-branch
*bodies* become three named methods while its activity-id dispatch stays put
(Phase 3). Coordinators are invoked BY the tick thread, synchronously, on the
tick thread. None of them owns a thread, a timer, or a scheduler.

**Tech Stack:** C# / .NET 8, xUnit + Testcontainers (a real Postgres 16 per
collection). No wire change, no client change, no migration anywhere in this
plan.

**Spec:** `docs/superpowers/plans/2026-09-17-simulationengine-split-scoping.md`
is the research this plan is built on — read it before starting, particularly
§3 (which `TickStatePayload` fields are domain-local vs. cross-cutting) and §6
(the landmines). `docs/TASK_BOARD.md` item 21 is the board entry. The owner's
decisions, taken after reading that scoping document, are recorded in
"Decisions already taken" below and are **not open for re-litigation by an
implementer**.

Where this plan's line numbers disagree with the scoping document's, this plan
wins — every reference below was re-confirmed against the live file on
2026-09-17. Drift found while confirming is noted inline (the drain plane has
**36** drains, not the scoping doc's "~33"; Legacy Store is still at 1497-1509
exactly; the shared gate starts at **1868**, not 1875, because the payload
resolution immediately above it is part of the same indivisible prologue).

---

## Decisions already taken

1. **Scope: Option A, then Option B, then the safer half of Option C.** Drains
   first (Phase 1), command dispatch second (Phase 2) with the shared
   anti-cheat/epoch gate extracted as its own always-first step before any
   command moves, and `ProcessSubTick`'s branch bodies last (Phase 3).
2. **Phase 3 extracts the three activity branches' BODIES only.** The
   activity-id `if / else if` dispatch stays in `ProcessSubTick`. This is the
   scope boundary, restated at the head of Phase 3.
3. **A coordinator is a `static class`.** No DI, no instance state, no
   constructor. Per-payload methods take `ref TickStatePayload`. This matches
   the file's existing zero-allocation style and keeps every move mechanical.

---

## Global Constraints

These apply to **every task in every phase**. A task that violates one is
wrong even if the suite is green.

- **Single-writer invariant.** A `TickStatePayload` field may be written from
  exactly one place: the tick thread, synchronously, inside a method the tick
  thread called. A coordinator is a *callee of the tick thread*, never a class
  with its own `Task.Run`, `Timer`, `Thread`, or independently-scheduled work.
  If an extraction makes it possible to write a payload field from anywhere
  else, the extraction is wrong regardless of what the tests say — the failure
  mode is a torn struct read at 10Hz, which no test in this repo can see.

- **`SafeDispatchAsync` is reused, never reinvented.** Every asynchronous call
  out of the tick thread goes through `SimulationEngine.SafeDispatchAsync`
  (`SimulationEngine.cs:622`) or something handed the *same* delegate. No
  coordinator may introduce a bare `Task.Run`, `Task.Factory.StartNew`, or
  `async void`. This is CLAUDE.md's cron-worker-silent-death trap: an exception
  in a bare fire-and-forget task is swallowed by the CLR and the feature simply
  stops existing, server-wide, with no log. The mechanism for handing it to a
  coordinator is fixed in Task 1.18 and used unchanged after that.

- **`ProcessTick` → `TrackState` stays atomic, per player, per tick.** They are
  called back-to-back inside one `try/catch` in `EngineLoop` (around line
  3546). Nothing in this plan moves the *caller* of `ProcessTick`. If a future
  task is tempted to, it must keep both calls on the same pass for the same
  player or it reopens CLAUDE.md's "a Redis frame is not a checkpoint" defect,
  where a live session's attributes, diamonds, larder and quests were durable
  nowhere.

- **A coordinator never mutates `_activePlayers` or `_guildMembersIndex`.** It
  may resolve a `ref` into `_activePlayers` and write through it; it may
  *read* `_guildMembersIndex`. Adding or removing a dictionary entry while any
  `ref` into that dictionary is alive can invalidate the ref on a resize — a
  silent memory-corruption class this codebase has no guard for. The two drains
  that do mutate (`StateReloadQueue`, which calls `AddActivePlayer`;
  `GuildMembershipChangeQueue`, which writes the guild index) are explicitly
  **excluded from Phase 1** for this reason, and stay in `EngineLoop`.

- **`_guildMembersIndex` ownership ruling (scoping doc §8, Decision 3):**
  **session lifecycle owns it; guild coordinators borrow read access.** Every
  writer is already a session-boundary event (`AddActivePlayer`,
  `RemoveActivePlayer`, `AddToGuildIndex`/`RemoveFromGuildIndex`, and the
  `GuildMembershipChangeQueue` drain, which is a membership *boundary*, not
  guild gameplay). The five guild-adjacent drains only ever read it. So the
  index stays a private field of `SimulationEngine`, and a guild coordinator
  receives it as `IReadOnlyDictionary<long, List<long>>` — a reference
  conversion with no boxing, and read-only-ness that the compiler enforces
  instead of a comment. Reasoning: giving one of five peer coordinators
  write access would make the other four depend on it for no reason, and
  moving it into a "guild coordinator" would be wrong on the facts, since
  nothing about guild gameplay writes it. (Coordinators must also not mutate
  the `List<long>` the lookup returns; none do, and none may start.)

- **Behaviour-preserving means verbatim.** Transcribe the block being moved.
  Do not rename a local, do not invert a condition, do not collapse
  `if (!IsNullRef) { ... }` into `if (IsNullRef) continue;`, do not "tidy" a
  comment. Every `// Modul:` comment travels with the code it explains — those
  comments are the record of bugs that were found the hard way, and a comment
  left behind in `EngineLoop` next to a call is a comment nobody will read
  again. Where a comment explains a *pair* of sites (the two gold paths), both
  copies travel together, in the same task.

- **Stop the running server before `dotnet build` / `dotnet test`.** A server
  holding the output directory makes the build succeed while producing a stale
  DLL. The `guard_stale_build.py` hook blocks this, and a hook-blocked Bash
  call runs *nothing else in the same call* — so never chain an edit and a
  build in one shell invocation.

- **`dotnet test` needs a working Docker daemon.** Without it the whole suite
  dies at once in `PostgresTestFixture` with NullReferenceExceptions, which
  looks nothing like a code error. Check Docker before debugging a mass
  failure; recovery is `docker desktop stop` then `start`.

- **Phase gate:** Phase 2 does not start until every Phase 1 task is committed
  and the full server suite is green. **Phase 3 does not start until Phase 1
  and Phase 2 are both complete and their final reviews are clean.** This is a
  hard gate, not an ordering preference: Phase 3 is the only phase where a
  mistake is a live gameplay regression rather than a compile error, and its
  whole justification is that the coordinator pattern will by then have been
  proven in this codebase ~30 times without incident.

- **Proof is the existing suite, not new tests.** Phases 1 and 2 are pure
  refactoring. The correct proof is: *the existing suite still passes, and no
  existing test needed new mocking, new arguments, or new setup to accommodate
  the extraction.* If a test had to change to keep passing, the extraction
  changed behaviour — stop and re-read the diff. Do **not** invent new unit
  tests for a moved block; a new test written against the moved code proves
  only that the moved code does what the moved code does. The two exceptions
  are stated explicitly in their own tasks: Phase 2's gate (which needs
  characterization tests written **first**, because nothing pins its ordering
  today) and Phase 3 (which needs a manual browser verification pass).

---

## Where coordinators live

One file per coordinator, in the domain folder that already owns that domain's
engine — matching the split the rest of `Domain/` already has:

| Domain | Folder | Namespace |
|---|---|---|
| Combat, equipment, character slots | `Domain/Combat/` | `FolkIdle.Server.Domain.Combat` |
| Crafting, forge, market, chest | `Domain/Economy/` | `FolkIdle.Server.Domain.Economy` |
| Village, quests, legacy, inheritance, skill tree, mastery | `Domain/Progression/` | `FolkIdle.Server.Domain.Progression` |
| Guild, guild war, mentorship, mail | `Domain/Social/` | `FolkIdle.Server.Domain.Social` |
| Cross-cutting adapters (the command gate) | `Domain/Shared/` | `FolkIdle.Server.Domain.Shared` |

`SimulationEngine.cs` already has `using` directives for all five (lines 8-12),
so no call site in this plan needs a new `using`.

---

# PHASE 1 — the notification-drain plane (Option A)

36 drain blocks live inline in `EngineLoop` between lines 748 and 1656. Each is
self-contained: one queue in, one or a few payload fields out. Phase 1 moves
them out, smallest and safest first.

**Explicitly NOT moved in Phase 1**, and why — an implementer must not "finish
the job" by moving these:

| Queue | Line | Why it stays |
|---|---|---|
| `StateReloadQueue` | 961 | Calls `AddActivePlayer`, which mutates `_activePlayers` while a `ref` into it is alive. Violates a Global Constraint. |
| `ActivityChangeQueue` | 992 | Drives `SwapSlotIntoActiveRegister`. The register-swap discipline is Phase 3's subject matter. |
| `EquipmentSlotUpdateQueue` | 865 | Same — swaps the active register twice around its writes. |
| `GuildMembershipChangeQueue` | 1336 | The only **writer** of `_guildMembersIndex`. Per the ownership ruling, no coordinator writes it. |
| `QuarantineNotificationQueue` | 1487 | Session lifecycle / anti-cheat, not a domain. Belongs with whatever eventually owns session lifecycle, which is out of scope here. |
| `ShardAttackResultQueue` | 1270 | Calls the instance method `TerminateSessionForSecurity`. Terminating a session is session-lifecycle work; routing it through a callback would give a domain coordinator the power to end sessions. |

---

### Task 1.1: Legacy Store — the proof of pattern

This task is deliberately over-specified. Every following Phase 1 task copies
the shape established here, so get this one exactly right and the rest are
mechanical.

**Scope note, deliberately narrower than the scoping document's §7:** that
document proposed moving the `LegacyStoreUpdateQueue` drain **and** the
`PurchaseLegacyUnlocks` command branch together. This plan moves the drain
only. The owner's decision puts the shared anti-cheat gate ahead of *any*
command move, and `PurchaseLegacyUnlocks` sits downstream of that gate. The
command branch moves in Task 2.2, which is its direct sequel.

**Files:**
- New: `server/FolkIdle.Server/Domain/Progression/LegacyStoreTickCoordinator.cs`
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs:1497-1509`
  (the `LegacyStoreUpdateQueue` drain block — confirmed at these exact lines
  on 2026-09-17)
- Test: none new. See "Proof is the existing suite" above.

**Interfaces:**
- Consumes: `PlayerSessionRegistry.LegacyStoreUpdateQueue`
  (`Engine/PlayerSessionRegistry.cs:491`) and `LegacyStoreUpdateNotification`
  (`Engine/PlayerSessionRegistry.cs:376-390`).
- Produces: **the coordinator shape every later Phase 1 task copies.** Two
  methods, always:
  - `DrainNotifications(PlayerSessionRegistry registry, Dictionary<long, TickStatePayload> activePlayers)` — owns the `while`/`TryDequeue`/ref-resolution loop. It needs the dictionary because a drain does not know which payload it is about until it has dequeued.
  - `Apply(ref TickStatePayload payload, in <Notification> notif)` — the per-payload mutation. This is the half that takes `ref TickStatePayload` per the owner's chosen shape, and it is the half that is independently callable without a dictionary, a database, or a socket.

- [ ] **Step 1: Read the live block first**

Read `SimulationEngine.cs:1497-1509`. It must read exactly:

```csharp
                while (_playerRegistry.LegacyStoreUpdateQueue.TryDequeue(out var legacyNotif))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, legacyNotif.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.SetLegacyShards(legacyNotif.LegacyShardBalance);
                        currentPayload.CitizenMultiSlotsUnlocked = legacyNotif.CitizenMultiSlotsUnlocked;
                        if (legacyNotif.HasLegacyPerksUpdate)
                        {
                            currentPayload.CachedLegacyPerks = legacyNotif.LegacyPerks;
                        }
                    }
                }
```

If it does not, the file has drifted since 2026-09-17 — transcribe what is
actually there, not what is printed here.

Note what this block does **not** do, because it is why Legacy Store was chosen
to go first: it does not touch `CurrentGold` or `RedisPendingGoldDelta` (no
gold-path ambiguity), does not read `_guildMembersIndex`, does not swap the
active register, does not dispatch anything asynchronously, and does not even
set `IsDirty` — the three fields it writes are read-only everywhere else
(`LegacyPerkResolver` consults `CachedLegacyPerks` from combat and never
writes it). It is the cleanest one-directional field dependency in the file.

- [ ] **Step 2: Create the coordinator**

`server/FolkIdle.Server/Domain/Progression/LegacyStoreTickCoordinator.cs`:

```csharp
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick thread's Legacy Store hand-off: folds a committed
    /// LegacyStoreEngine purchase back into the live TickStatePayload.
    ///
    /// Modul: this is a COORDINATOR, not an engine. It runs synchronously on
    /// the 10Hz tick thread, called by SimulationEngine.EngineLoop, and it is
    /// the only kind of class allowed to write TickStatePayload besides
    /// SimulationEngine itself. It owns no thread, no timer and no state - a
    /// static class with no fields, so there is nothing for a second thread
    /// to reach. See the plan's single-writer constraint: a coordinator that
    /// grew its own scheduling would silently reintroduce torn reads of a
    /// 200-field blittable struct at 10Hz, which nothing in this repo's test
    /// suite can observe.
    /// </summary>
    internal static class LegacyStoreTickCoordinator
    {
        /// <summary>
        /// Drains every pending Legacy Store update onto its player's live
        /// payload. Verbatim from SimulationEngine.EngineLoop, where this
        /// block sat inline between the quarantine drain and the inheritance
        /// drain.
        /// </summary>
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.LegacyStoreUpdateQueue.TryDequeue(out var legacyNotif))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, legacyNotif.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in legacyNotif);
                }
            }
        }

        /// <summary>
        /// Applies one notification to one live payload.
        ///
        /// Split out from the drain loop deliberately: this half needs no
        /// dictionary, no registry and no database, so it is the seam any
        /// future test of this domain's fold-back would drive. The drain loop
        /// above is pure plumbing.
        /// </summary>
        internal static void Apply(ref TickStatePayload payload, in LegacyStoreUpdateNotification legacyNotif)
        {
            payload.SetLegacyShards(legacyNotif.LegacyShardBalance);
            payload.CitizenMultiSlotsUnlocked = legacyNotif.CitizenMultiSlotsUnlocked;
            if (legacyNotif.HasLegacyPerksUpdate)
            {
                payload.CachedLegacyPerks = legacyNotif.LegacyPerks;
            }
        }
    }
}
```

Check the exact type of `activePlayers` against `SimulationEngine.cs:281`
(`private readonly System.Collections.Generic.Dictionary<long, TickStatePayload> _activePlayers = new();`)
and match it. Check that `LegacyStoreUpdateNotification` resolves under
`FolkIdle.Server.Engine` (it is declared in `PlayerSessionRegistry.cs`, which
is in that namespace) and that `TickStatePayload` does too.

- [ ] **Step 3: Replace the inline block with the call**

In `SimulationEngine.cs`, replace lines 1497-1509 entirely with:

```csharp
                LegacyStoreTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);
```

Leave its position in the sequence unchanged — it must still sit between the
`QuarantineNotificationQueue` drain and the `InheritanceSyncQueue` drain.
Drain order is not documented anywhere as load-bearing, but two of them
(`StateReloadQueue` before `ActivityChangeQueue`) carry a comment saying it
*is*, so treat the whole sequence as fixed until someone proves otherwise.

- [ ] **Step 4: Build**

Stop any running server first.

Run: `dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj`

Expected: clean. A `CS8347`/`CS8168` ref-safety error here means `Apply`'s
signature took the ref wrongly — fix the signature, do not add `unsafe` or
copy the struct by value (a by-value copy would make the whole drain a no-op,
which is exactly the "output side was never wired" defect class this codebase
keeps shipping).

- [ ] **Step 5: Run the full server suite**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj`

Expected: all PASS, with **no test file modified**. `E2EGameLoopTest.cs:178`,
`HardenedEngineIntegrationTests.cs:1916/2996/5076/9449` all construct a
`LegacyStoreEngine` against a real registry and drive this path end to end;
they are the regression proof for this task. If any of them needed a change to
compile or pass, the extraction changed behaviour — revert and re-read.

- [ ] **Step 6: Commit**

```bash
git add server/FolkIdle.Server/Domain/Progression/LegacyStoreTickCoordinator.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs
git commit -m "refactor(tick): the Legacy Store drain becomes a coordinator"
```

---

### Tasks 1.2 - 1.17: the remaining safe drains

Every one of these follows Task 1.1's shape exactly: create the coordinator
file, transcribe the block verbatim into `DrainNotifications` + `Apply`, replace
the inline block with a one-line call in the same position, build, run the full
suite, commit. **Each is its own task and its own commit.** Do not batch them.

The per-task steps are therefore always these six — an implementer should not
need them restated:

1. Read the live block at the stated lines; if it has drifted, transcribe what
   is there.
2. Create the coordinator file at the stated path.
3. Replace the inline block with the one-line call, position unchanged.
4. `dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj` (server stopped).
5. `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj` — all
   pass, **no test file modified**.
6. Commit with the stated message, `git add`ing exactly the new coordinator file
   and `SimulationEngine.cs`.

| Task | Domain | Queue(s) and line(s) | New file | Commit message |
|---|---|---|---|---|
| **1.2** | Inheritance | `InheritanceSyncQueue` 1511-1519 | `Domain/Progression/InheritanceTickCoordinator.cs` | `refactor(tick): the inheritance drain becomes a coordinator` |
| **1.3** | Skill tree | `SkillTreeSyncQueue` 1521-1545 | `Domain/Progression/SkillTreeTickCoordinator.cs` | `refactor(tick): the skill-tree drain becomes a coordinator` |
| **1.4** | Billing | `BillingSyncQueue` 1547-1555 | `Domain/Progression/BillingTickCoordinator.cs` | `refactor(tick): the billing drain becomes a coordinator` |
| **1.5** | World boss | `WorldBossAttemptUpdateQueue` 821-830 | `Domain/Combat/WorldBossTickCoordinator.cs` | `refactor(tick): the world-boss attempt drain becomes a coordinator` |
| **1.6** | Forge | `ForgeUpgradeQueue` 851-863 | `Domain/Economy/ForgeTickCoordinator.cs` | `refactor(tick): the forge-upgrade drain becomes a coordinator` |
| **1.7** | Codex | `CodexMultiplierUpdateQueue` 909-917 | `Domain/Progression/CodexTickCoordinator.cs` | `refactor(tick): the codex-multiplier drain becomes a coordinator` |
| **1.8** | Race mastery / unlock | `MasteryUpdateQueue` 832-849 **and** `RaceUnlockQueue` 934-945 | `Domain/Progression/RaceProgressionTickCoordinator.cs` | `refactor(tick): the race mastery and unlock drains become a coordinator` |
| **1.9** | Region progression | `RegionCompletionUpdateQueue` 919-927 | `Domain/Progression/RegionProgressionTickCoordinator.cs` | `refactor(tick): the region-completion drain becomes a coordinator` |
| **1.10** | Larder | `LarderSlotUpdateQueue` 1037-1072 | `Domain/Shared/LarderTickCoordinator.cs` | `refactor(tick): the larder drain becomes a coordinator` |
| **1.11** | Mentorship | `MentorshipUpdateQueue` 1478-1485 **and** `MentorshipContractUpdateQueue` 1629-1643 | `Domain/Social/MentorshipTickCoordinator.cs` | `refactor(tick): the two mentorship drains become a coordinator` |
| **1.12** | Inventory census | `InventoryCensusQueue` 1138-1173 **and** `CombatLootDropQueue` 1175-1201 | `Domain/Combat/InventoryCensusTickCoordinator.cs` | `refactor(tick): the inventory-census drains become a coordinator` |
| **1.13** | Command-result ring | `CommandResultQueue` 1392-1409 | `Domain/Shared/CommandResultTickCoordinator.cs` | `refactor(tick): the command-result ring drain becomes a coordinator` |
| **1.14** | Village | `InfrastructureUpdateQueue` 1436-1463 **and** `VillagerRecruitmentUpdateQueue` 1465-1476 | `Domain/Progression/VillageTickCoordinator.cs` | `refactor(tick): the two village drains become a coordinator` |
| **1.15** | Breeding | `BirthNotificationQueue` 804-819 | `Domain/Progression/BreedingTickCoordinator.cs` | `refactor(tick): the birth drain becomes a coordinator` |
| **1.16** | Village chest (**the gold pair**) | `AutoSalvageQueue` 1203-1230, `ChestSaleGoldQueue` 1232-1252, `ChestSettingsQueue` 1254-1268 | `Domain/Economy/VillageChestTickCoordinator.cs` | `refactor(tick): the chest drains, both gold paths together, become a coordinator` |
| **1.17** | Guild fan-out (**the five index readers**) | `GuildWarScoreboardQueue` 1074-1121, `GuildUpdateQueue` 1411-1434, `GuildLogisticsDepotUpdateQueue` 1557-1572, `GuildCombatSimulationUpdateQueue` 1574-1608, `GuildRaidBossUpdateQueue` 1610-1625 | `Domain/Social/GuildFanoutTickCoordinator.cs` | `refactor(tick): the five guild fan-out drains become a coordinator` |

Where a task lists more than one queue, they share one coordinator class with
one `DrainNotifications` method per queue (`DrainMasteryUpdates`,
`DrainRaceUnlocks`, and so on) — not one merged loop. They are grouped because
they are one domain, never to save a task.

Five of these carry an extra constraint that an implementer must read before
starting them:

- [ ] **Task 1.14 (Village)** — both drains write `CurrentGold` and *neither*
  touches `RedisPendingGoldDelta`. The `// Modul:` comment above each
  `CurrentGold` line (lines 1457-1459 and 1470-1472) says why, and both
  comments must travel into the coordinator with their code. This is the
  "already-debited" half of CLAUDE.md's two gold paths: the row is correct and
  only the displayed total is behind, so banking a pending delta as well would
  pay the player twice one checkpoint later.

- [ ] **Task 1.15 (Breeding)** — same shape, same reason, at line 816. The
  comment block at 809-815 explains both why a birth no longer bumps
  `VillagePopulation` and why the gold follows the row; move it whole.

- [ ] **Task 1.16 (the gold pair) — MUST be a single task, and is.**
  `AutoSalvageQueue` (1218) sets `AddGold` **and** `RedisPendingGoldDelta`
  **and** `RequiresRedisFlush`, because no database row was ever written for
  that gold. `ChestSaleGoldQueue` (1242), fifteen lines below it, sets
  `AddGold` **only**, because `VillageChestEngine` already credited
  `CommodityRecords["gold"]` in its own transaction and the checkpoint applies
  the pending delta as an *increment* to that same row. The two sixteen-line
  `// Modul:` comments above them (1203-1217 and 1232-1241) are a matched pair
  — the second one literally opens "READ ChestSaleGoldNotification BEFORE
  EDITING THIS - it is deliberately not the drain above." Splitting these into
  two tasks or two coordinators would separate the two halves of one
  explanation, which is precisely the condition under which this rule has been
  broken before. They go into one file, adjacent, with both comments intact.
  `ChestSettingsQueue` rides along because it is the same domain and the same
  coordinator, not because it shares state.

- [ ] **Task 1.17 (guild fan-out)** — this is the task the `_guildMembersIndex`
  ownership ruling exists for. All five drains *read* the index and write
  through refs into `_activePlayers` for every online member of a guild. The
  coordinator's methods therefore take a third parameter:

  ```csharp
  internal static void DrainGuildUpdates(
      PlayerSessionRegistry registry,
      Dictionary<long, TickStatePayload> activePlayers,
      IReadOnlyDictionary<long, List<long>> guildMembersIndex)
  ```

  `SimulationEngine` passes `_guildMembersIndex` directly — a
  `Dictionary<long, List<long>>` converts to `IReadOnlyDictionary<long, List<long>>`
  by reference conversion, no allocation, no boxing. The interface is not
  decoration: it is what makes "a guild coordinator cannot write the index" a
  compile error instead of a convention. Note that it does not stop a
  coordinator mutating a returned `List<long>`; none do, and none may start.
  The **writer** (`GuildMembershipChangeQueue`, 1336) stays in `EngineLoop`
  and is not part of this task.

- [ ] **Task 1.12 (inventory census)** — the two blocks carry four stacked
  `// Modul:` comments recording that the backpack was removed, that the
  counter is now a deliberately-pinned fossil, and that a full backpack was
  once a permanent dead end that stopped combat, loot and gathering for the
  rest of a session. All of it moves; none of it gets summarised.

---

### Task 1.18: Market settlement — and the `SafeDispatchAsync` convention

**Files:**
- New: `server/FolkIdle.Server/Domain/Economy/MarketTickCoordinator.cs`
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs:748-802`
  (the `MarketMatchQueue` drain) and the field block around line 113 (a new
  cached delegate field)

**Interfaces:**
- Produces: **the convention for handing `SafeDispatchAsync` to a coordinator.**
  Tasks 1.19 and 1.20, and every Phase 2 command coordinator, use it unchanged.

This is the first drain that dispatches asynchronous work. Its offline
"settlement rescue" path (lines 756-801) calls `SafeDispatchAsync` with a
closure that credits `CommodityRecords["gold"]` directly for a seller who
logged out between the escrow's online-check and this drain. That path exists
because dropping the notification silently lost a completed sale's proceeds
with no error and no telemetry; it must survive the move exactly.

- [ ] **Step 1: Cache the dispatch delegate once**

A coordinator cannot call the private instance method `SafeDispatchAsync`, and
it must not reimplement it. Hand it over as a delegate — but cache the delegate
in a field rather than constructing one per tick, since this loop runs at 10Hz
forever. Beside the other `private readonly` fields (around
`SimulationEngine.cs:113`):

```csharp
        // Modul: SafeDispatchAsync, handed to the tick coordinators as a value.
        //
        // A coordinator must never own its own async dispatch - CLAUDE.md's
        // cron-worker trap is that a bare Task.Run swallows its exception and
        // the feature simply stops existing, server-wide, with no log. So the
        // ONE implementation is passed down rather than reimplemented per
        // coordinator, and it is cached here rather than built per call: this
        // is a 10Hz loop, and a fresh delegate per drain per tick is an
        // allocation on the hot path for nothing.
        private readonly Action<string, long, Func<Task>> _safeDispatch;
```

Assign it in the constructor, after `_networkSystem` is set (it is the field
`SafeDispatchAsyncCore` force-disconnects through):

```csharp
            _safeDispatch = SafeDispatchAsync;
```

- [ ] **Step 2: The coordinator**

`MarketTickCoordinator.DrainMatchNotifications` takes the registry, the
dictionary, `Action<string, long, Func<Task>> safeDispatch`, and
`IDbContextFactory<FolkIdleDbContext> contextFactory` (the rescue path creates
its own context). Transcribe 748-802 verbatim, replacing `SafeDispatchAsync(` with
`safeDispatch(` and `_contextFactory` with `contextFactory`. Every line of the
44-line `// Modul: market settlement rescue, 2026-08-01.` comment moves with it.

- [ ] **Step 3-6:** call site, build, full suite, commit.

```bash
git add server/FolkIdle.Server/Domain/Economy/MarketTickCoordinator.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs
git commit -m "refactor(tick): the market settlement drain becomes a coordinator"
```

---

### Task 1.19: Mail claim

**Files:** New `server/FolkIdle.Server/Domain/Social/MailTickCoordinator.cs`;
modify `SimulationEngine.cs:1645-1656`.

The `MailClaimRequestQueue` drain credits `AddGold` on the live payload and
then dispatches `_mailboxEngine.CommitMailClaimAsync` through
`SafeDispatchAsync`. It needs the cached delegate from Task 1.18 plus a
`MailboxAndBankEngine` parameter. Note the redundant nested braces at 1650-1654
in the live file — transcribe them as they are; removing them is a
readability change, and this plan does not make readability changes in the same
commit as a move.

```bash
git add server/FolkIdle.Server/Domain/Social/MailTickCoordinator.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs
git commit -m "refactor(tick): the mail-claim drain becomes a coordinator"
```

---

### Task 1.20: Crafting

**Files:** New `server/FolkIdle.Server/Domain/Economy/CraftingTickCoordinator.cs`;
modify `SimulationEngine.cs:1028-1035` (`CraftingTickQueue`) and `1305-1334`
(`CraftingCompletionQueue`).

Two peculiarities to preserve:

- `CraftingTickQueue` is a `public static` field on `SimulationEngine` itself
  (line 137), not on `PlayerSessionRegistry` — `ProcessSubTick` enqueues to it
  from a static context. Leave the field where it is; the coordinator reads
  `SimulationEngine.CraftingTickQueue`. Moving the field is a separate decision
  with its own blast radius (`CombatLootEngine.cs:233` comments on exactly this
  static-ness) and is out of scope.
- The `CraftingCompletionQueue` drain enqueues onto
  `_guildWarEngine.GuildWarPointQueue` when a high-tier item is crafted during a
  guild war. Pass that queue as a parameter — `ProcessSubTick` already takes
  `ConcurrentQueue<GuildWarPointEvent> guildWarPointQueue` the same way
  (line 5393), so this is an established convention in this file, not a new one.

```bash
git add server/FolkIdle.Server/Domain/Economy/CraftingTickCoordinator.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs
git commit -m "refactor(tick): the two crafting drains become a coordinator"
```

---

### Task 1.21: Phase 1 review gate

- [ ] `dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj` clean.
- [ ] `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj` fully green.
- [ ] `git diff main --stat -- server/FolkIdle.Server.Tests/` is **empty**. If any
      test changed across all of Phase 1, say which and why in the report — that
      is a finding, not a formality.
- [ ] `npm run exercise` from `client_web/` against a locally-running stack
      (`.\run-dev.ps1`). Phase 1 touched no gameplay rule, but it moved the
      fold-back path for village upgrades, chest sales, guild buffs, the larder
      and the command-result ring — all of which `exercise.mjs` asserts
      *changed*, which is the only check in this repo that catches "the output
      side was never wired."
- [ ] Use `superpowers:requesting-code-review` on the accumulated Phase 1 diff
      before opening Phase 2.

---

# PHASE 2 — command dispatch (Option B)

The command-dispatch plane is a single `if / else if` chain over ~57
`CommandType` values, running from line 1663 to roughly 3411. Three branches
run **before** the shared gate and stay exactly where they are:
`InitiateNodeMigration` (1663), `ConsumeConsumableAsset` (1732) and `Login`
(1826). They precede the gate because none of them has a live payload to gate
against yet, and moving them would change what is validated.

### Task 2.1: Extract the shared anti-cheat / epoch gate — **always first**

This is the highest-reach change in the entire plan. Every command in the game
passes through these lines. Nothing moves to a command coordinator until this
has landed and is green.

**Confirmed against the live file:** the gate is lines **1868-1938**, not the
scoping doc's 1875-1938. Line 1868 resolves `currentPayload` and 1870-1873
skips a command for a player who is not active; those four lines are the gate's
prologue and are part of the same indivisible decision.

**Files:**
- New: `server/FolkIdle.Server/Domain/Shared/CommandGate.cs`
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs:1875-1937`
- Test: `server/FolkIdle.Server.Tests/CommandGateOrderingTests.cs` (new)

**Interfaces:**
- Produces: `CommandGateVerdict` (`Proceed` / `Terminate` / `ShadowBan`) and
  `CommandGate.Evaluate(ref TickStatePayload, ref ClientCommandPacket)`. Every
  Phase 2 coordinator task depends on this having landed.

- [ ] **Step 1: Verify what pins the gate today — the scoping doc says nobody checked**

Scoping doc §6, finding 1 and §8, Decision 4 both flag this as unverified.
**It has now been verified, and the answer is no.** Grepping
`server/FolkIdle.Server.Tests/` for `ValidateEpochSynchronization`,
`ValidateNoAntiCheatPayload`, `ValidateNoPushCompliancePayload`,
`LastClientCommandAtMs` and `IsServerInternalCommand` returns only:

- `BackgroundedClientTests.cs` — tests `LastClientCommandAtMs` semantics and
  `IsServerInternalCommand`'s membership, against a locally-reimplemented
  predicate. It never runs the gate.
- `HardenedEngineIntegrationTests.cs:2906-2918`
  (`Test_EpochGate_AcceptsTheServersOwnLogoutPacket`) — asserts each validator
  in isolation and that `Logout` is internal. It never runs the gate either.

**Nothing asserts the four checks run in that order, or that the stamp happens
before them.** So this task writes characterization tests *first*. Do not skip
this step; re-run the greps to confirm nothing has been added since.

- [ ] **Step 2: Write the characterization tests BEFORE touching the gate**

New file `server/FolkIdle.Server.Tests/CommandGateOrderingTests.cs`. These must
be written and **passing against the unmodified codebase** before Step 3.

There are two levels, and both are needed:

*(a) Behavioural, through the real loop.* Use
`HardenedEngineIntegrationTests.cs:2975`'s `CreateLiveSimulationEngine(uriPrefix)`
helper and `SendCommandAsync` (line 3012) — the established pattern in this repo
for driving `EngineLoop`'s command dispatch on the real background thread
(its own comment at 2967-2974 says that is exactly what it exists for). Assert
the four outcomes as a client sees them:

1. A well-formed command from a synchronized client is accepted (whatever that
   command's own observable effect is).
2. A command carrying a stale `LogicEpochCounter` severs the session.
3. A server-internal command (`Logout`, `ReloadState`) with a zeroed epoch is
   **not** severed — this is the live defect CLAUDE.md records, where a closed
   tab was terminated instead of flushed.
4. A command from a player with no `_activePlayers` entry is silently skipped.

*(b) Ordering, which is the part nothing pins.* The order only becomes
observable when two checks fail at once. Construct one packet that fails **both**
`ValidateEpochSynchronization` (→ terminate) **and**
`ValidateNoAntiCheatPayload` (→ shadow-ban, session survives). Under the
current order the session is **terminated**; under any reordering that puts the
anti-cheat-payload check first, the session **survives and is shadow-banned**.
That single assertion is the order guard. Add the mirror for the
push-compliance check if a packet can be made to fail both it and the epoch
check.

Before the extraction these tests drive `EngineLoop` end to end. After Step 3
they are kept as-is (they still drive `EngineLoop`), and Step 5 adds the
cheaper direct-`Evaluate` versions beside them.

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~CommandGateOrdering"`

Expected: all PASS **on the unmodified code**. A characterization test that is
red before the refactor is describing behaviour the system does not have.

- [ ] **Step 3: Commit the characterization tests on their own**

```bash
git add server/FolkIdle.Server.Tests/CommandGateOrderingTests.cs
git commit -m "test(anti-cheat): pin the command gate's ordering before extracting it"
```

A separate commit on purpose: it is the evidence that the guard existed before
the change it guards, and it can be cherry-picked or bisected independently.

- [ ] **Step 4: Extract**

`server/FolkIdle.Server/Domain/Shared/CommandGate.cs`:

```csharp
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>What the gate decided about one command.</summary>
    internal enum CommandGateVerdict
    {
        /// <summary>Hand the command to its coordinator.</summary>
        Proceed,

        /// <summary>Caller must TerminateSessionForSecurity and skip the command.</summary>
        Terminate,

        /// <summary>Caller must RequestShadowBan(playerId, 54, 2) and skip the command.</summary>
        ShadowBan
    }

    /// <summary>
    /// The anti-cheat and epoch gate every non-internal client command passes
    /// through, extracted verbatim from SimulationEngine.EngineLoop.
    ///
    /// Modul: THE ORDER OF THESE FOUR CHECKS IS THE CONTRACT, and until
    /// CommandGateOrderingTests was written nothing in the repository pinned
    /// it - it was enforced only by the physical order of four `if` statements
    /// inside a 700-line loop body. Three of the four validators take the
    /// payload by ref and may mutate it, so reordering them is not merely a
    /// different error message: it is a different resulting payload.
    ///
    /// The verdict is returned rather than acted on because terminating a
    /// session and requesting a shadow ban are both SimulationEngine's own
    /// instance work (they touch _networkSystem and the anti-cheat engine).
    /// A gate that could end sessions itself would be a second place in the
    /// server with that power.
    /// </summary>
    internal static class CommandGate
    {
        internal static CommandGateVerdict Evaluate(ref TickStatePayload payload, ref ClientCommandPacket cmd)
        {
            // Server-generated packets carry no client epoch - see
            // IsServerInternalCommand for why Logout is one of them.
            bool isInternalCommand = SimulationEngine.IsServerInternalCommand(cmd.Command);

            //  <-- the full "THE ANTI-CHEAT'S 'WAS THIS CLIENT TALKING' STAMP"
            //      comment block from SimulationEngine.cs:1879-1902 moves here
            //      verbatim. It is the record of a guard that read a flag
            //      almost nothing wrote, and it belongs beside the stamp.
            if (!isInternalCommand)
            {
                payload.LastClientCommandAtMs = Environment.TickCount64;
            }

            //  <-- the "Epoch interception gate" comment from 1908-1914,
            //      including the note that commands 47 and 48 are gone and
            //      with them the only place LogicEpochCounter meant two
            //      different things.
            if (!isInternalCommand && !ClientCommandValidator.ValidateEpochSynchronization(ref payload, ref cmd))
            {
                return CommandGateVerdict.Terminate;
            }

            if (!isInternalCommand && !ClientCommandValidator.ValidateCommand(ref payload, (byte)cmd.Command))
            {
                return CommandGateVerdict.Terminate;
            }

            if (!isInternalCommand && !ClientCommandValidator.ValidateNoAntiCheatPayload(ref payload, ref cmd))
            {
                return CommandGateVerdict.ShadowBan;
            }

            if (!isInternalCommand && !ClientCommandValidator.ValidateNoPushCompliancePayload(ref payload, ref cmd))
            {
                return CommandGateVerdict.Terminate;
            }

            return CommandGateVerdict.Proceed;
        }
    }
}
```

`SimulationEngine.IsServerInternalCommand` is already `public static`
(line 586), so no accessibility change is needed.

The call site — replacing exactly lines 1875-1937, leaving 1868-1873's payload
resolution and null-ref skip where they are:

```csharp
                    switch (CommandGate.Evaluate(ref currentPayload, ref cmd))
                    {
                        case CommandGateVerdict.Terminate:
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        case CommandGateVerdict.ShadowBan:
                            _antiCheatTelemetryEngine?.RequestShadowBan(routingPlayerId, 54, 2);
                            continue;
                    }
```

Note the `54, 2` argument pair is the anti-cheat-payload case's own code. It
must not be shared with the `54, 3` used by the challenge-response branch at
line 1976, which is a different branch and stays where it is.

- [ ] **Step 5: Add the direct ordering tests beside the behavioural ones**

Now that `Evaluate` exists, add the same order assertion as a direct call —
cheap, no container, no socket — and keep the behavioural ones. Two levels is
deliberate: the direct test pins the gate's logic, the behavioural test pins
that `EngineLoop` still *calls* it, which a direct test cannot see.

- [ ] **Step 6: Full suite and commit**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj`

Expected: all PASS, including the Step 2 characterization tests **unmodified**.
If one of them had to change to accommodate the extraction, the extraction
changed the gate — that is the exact failure this task exists to prevent.

```bash
git add server/FolkIdle.Server/Domain/Shared/CommandGate.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs server/FolkIdle.Server.Tests/CommandGateOrderingTests.cs
git commit -m "refactor(anti-cheat): the shared command gate becomes its own adapter"
```

---

### Task 2.2: Legacy Store command — the command-coordinator proof of pattern

**Files:**
- Modify: `server/FolkIdle.Server/Domain/Progression/LegacyStoreTickCoordinator.cs` (from Task 1.1)
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs:3002-3018` and the dispatch-table declaration

**Interfaces:**
- Produces: `CommandCoordinatorContext` and the `Dictionary<CommandType, CommandHandler>`
  dispatch table. Tasks 2.3 onward add one entry each.

The live branch is 17 lines (3002-3018): validate, capture three locals off the
payload, dispatch. That shape — validate, capture, dispatch — is roughly 40 of
the ~57 branches, which is why this one is the template.

- [ ] **Step 1: The handler signature**

A command handler needs more than `ref TickStatePayload`: the routing player
id, the packet, the dispatch delegate, the domain engine, and — for branches
that reject — the ability to disconnect. Bundle the non-payload half into one
context struct so handlers stay a single consistent shape and the table stays
one type:

```csharp
namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// Everything a command handler needs that is not the payload.
    ///
    /// A readonly ref struct: it never escapes the tick thread's stack, it
    /// cannot be captured into a lambda or stored on a field, and it therefore
    /// cannot become a back door through which a coordinator reaches the
    /// engine's state from somewhere other than the tick thread.
    /// </summary>
    internal readonly ref struct CommandCoordinatorContext
    {
        internal long RoutingPlayerId { get; init; }
        internal Action<string, long, Func<Task>> SafeDispatch { get; init; }
        internal Action<long> TerminateSessionForSecurity { get; init; }
        internal Action<long> RemoveActivePlayerAndDisconnect { get; init; }
        // ... plus the domain engine handles the moved branches need, added
        //     one at a time by the task that needs each.
    }

    internal delegate void CommandHandler(
        ref TickStatePayload payload,
        ref ClientCommandPacket cmd,
        in CommandCoordinatorContext context);
}
```

Cache every delegate in a `SimulationEngine` field exactly the way Task 1.18
cached `_safeDispatch` — built once in the constructor, never per tick. A
`ref struct` cannot be cached, so build the context once per dequeued command
(a stack-only construction, no allocation), not per handler.

Verify this compiles before going further: a `ref struct` with `Action<>`
properties is legal, but a `ref TickStatePayload` parameter in a delegate is
the part most likely to need adjusting. If `CommandHandler` cannot be expressed
as a delegate with a `ref` first parameter plus a `ref struct` third, fall back
to a plain `internal static void Handle(...)` per coordinator and a
`switch (cmd.Command)` that calls them — the table is a means, not the goal,
and the goal is that the branch bodies stop living in `EngineLoop`.

- [ ] **Step 2: Move the branch**

Add to `LegacyStoreTickCoordinator`:

```csharp
        /// <summary>
        /// CommandType.PurchaseLegacyUnlocks. Verbatim from
        /// SimulationEngine.EngineLoop's dispatch chain.
        ///
        /// The rejection path is a DISCONNECT, not a CommandResult - the
        /// branch chose that deliberately (a malformed legacy-store request is
        /// not something an honest client sends), and CLAUDE.md's silent-
        /// rollback rule is that per-branch choice between ignore, report and
        /// disconnect is exactly what a dispatch table must not flatten.
        /// </summary>
        internal static void HandlePurchaseLegacyUnlocks(
            ref TickStatePayload payload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext context)
        {
            if (!ClientCommandValidator.ValidateLegacyStoreRequest(ref payload, ref cmd))
            {
                context.RemoveActivePlayerAndDisconnect(context.RoutingPlayerId);
                return;
            }

            long pId = payload.PlayerId;
            uint unlockId = cmd.TargetUnlockId;
            uint slotIndex = cmd.RequestedSlotIndex;

            context.SafeDispatch("Legacy.PurchaseUnlock", pId, async () =>
            {
                await context.LegacyStoreEngine.PurchaseLegacyUnlockAsync(pId, unlockId, slotIndex);
            });
        }
```

The `continue` in the original becomes `return` — the handler returns to the
dispatch site, which then continues the loop. Confirm by reading that the
original `continue` skips only the rest of the command's own handling and not
anything after the `if/else if` chain.

- [ ] **Step 3: The table**

Declare it as a `private readonly Dictionary<CommandType, CommandHandler>`
built once in the constructor. In the dispatch chain, try the table **first**;
fall through to the remaining `else if` chain when the command is not in it
yet. That is what makes Tasks 2.3+ independently landable — each moves a
cluster into the table and deletes its `else if` branches, and the chain
shrinks to nothing rather than all at once.

- [ ] **Steps 4-6:** build, full suite (the same five Legacy Store tests from
  Task 1.1 are the regression proof, plus `CommandGateOrderingTests`), commit.

```bash
git add server/FolkIdle.Server/Domain/Shared/ server/FolkIdle.Server/Domain/Progression/LegacyStoreTickCoordinator.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs
git commit -m "refactor(commands): dispatch PurchaseLegacyUnlocks through a table"
```

---

### Tasks 2.3 - 2.19: the remaining command clusters

One task per cluster, each following Task 2.2's six steps: add handlers to that
domain's coordinator (creating the coordinator file if Phase 1 did not),
register them in the table, delete the `else if` branches, build, full suite,
commit. **Each cluster is its own commit.** Line numbers are the live file's on
2026-09-17 and will drift as earlier tasks delete branches above them — locate
each branch by its `CommandType`, not by its line.

| Task | Coordinator | `CommandType`s (live line) |
|---|---|---|
| **2.3** | `Domain/Economy/MarketTickCoordinator` | `MarketListItem`/`MarketBuyItem` (1990), `PlaceLimitOrder` (2474) |
| **2.4** | `Domain/Progression/VillageTickCoordinator` | `UpgradeBuilding` (2356), `EvictVillager` (2373), `RecruitVillager`/`DismissNewcomer` (2395) |
| **2.5** | `Domain/Social/GuildTickCoordinator` (new) | `ContributeToGuild` (2114), `ContributeGuildTreasury` (2903), `DepositGuildMaterial` (3019) |
| **2.6** | `Domain/Social/GuildWarTickCoordinator` (new) | `ContributeToWarSupply` (2460), `RegisterGuildDefense` (2804), `SubmitShardAttack` (2832), `LaunchGuildRaid` (3038) |
| **2.7** | `Domain/Social/RelationshipTickCoordinator` (new) | `AddFriend` (2135), `RemoveFriend` (2146), `BlockPlayer` (2157), `UnblockPlayer` (2168) |
| **2.8** | `Domain/Economy/ForgeTickCoordinator` | `ExecuteForgeFusion` (2179), `RerollItemAffix` (2222) |
| **2.9** | `Domain/Progression/BreedingTickCoordinator` | `ExecuteBreeding` (2280), `ExecuteVillagerBreeding` (2302) |
| **2.10** | `Domain/Economy/CraftingTickCoordinator` | `InitializeCrafting` (2321), `CraftItem` (2352), `UpgradeTool` (2420) |
| **2.11** | `Domain/Combat/EquipmentTickCoordinator` (new) | `EquipItem` (3059), `UnequipItem` (3075) |
| **2.12** | `Domain/Shared/LarderTickCoordinator` | `StockFoodSlot` (3099), `UpdateAutoEatThreshold` (3163) |
| **2.13** | `Domain/Progression/AttributeTickCoordinator` (new) | `SpendAttributePoint` (2633), `RespecAttributes` (2691) |
| **2.14** | `Domain/Progression/SkillTreeTickCoordinator` | `PurchaseSkillTreeLevel` (2746), `RespecSkillTree` (2760), `RequestUnlockSkill` (3406) |
| **2.15** | `Domain/Progression/InheritanceTickCoordinator` | `PurchaseInheritanceLevel` (2567), `PurchaseAncestorSlot` (2589) |
| **2.16** | `Domain/Progression/BillingTickCoordinator` | `ClaimBattlePassReward` (2543), `PurchaseBattlePass` (2776), `SubmitPurchaseReceipt` (3296), `SyncBillingStatus` (3351) |
| **2.17** | `Domain/Social/MailTickCoordinator` | `ClaimMailItem` (2508), `DepositToBank`/`WithdrawFromBank` (2800), `ClaimAchievementReward` (2522), `AssignMentor…` (2442) |
| **2.18** | `Domain/Combat/WorldBossTickCoordinator` | `AttackWorldBoss` (3188), `ExecuteCombatTurn` (3123) |
| **2.19** | `Domain/Shared/ClientSessionTickCoordinator` (new) | `ReportTelemetryBurst` (2881), `PingNetworkDiagnostics` (2891), `RegisterPushToken` (3244), `TriggerGdprPurge` (3255), `SwitchLanguage` (3266), `ReportUiContextSwitch` (3390), `SetSimulationSpeed` (3146) |

**Not moved, deliberately** — these stay in `EngineLoop` and an implementer
must not move them:
`InitiateNodeMigration` (1663, runs before the gate and is cross-shard session
infrastructure), `ConsumeConsumableAsset` (1732, same), `Login` (1826, same),
`AntiCheatChallengeResponse` (1939, part of the anti-cheat machinery the gate
belongs to), `ChangeActivity` (2021, drives the active register — Phase 3
territory), `ReloadState` (2939) and `Logout` (3285, both server-internal
session lifecycle, and `Logout` is the flush path CLAUDE.md records as having
been broken by the epoch gate once already).

**Two constraints that apply to every task in this range:**

- [ ] **No new `Task.Run`.** Every moved branch already routes async work
  through `SafeDispatchAsync`; it must keep doing so via
  `context.SafeDispatch`. A handler that awaits directly, or that spawns its own
  task, blocks or loses the 10Hz loop respectively.

- [ ] **Preserve each branch's own failure shape.** Some branches ignore a bad
  request (`CraftItem`, `UpgradeTool`, `RequestUnlockSkill`), some report via
  the `CommandResult` ring (`ExecuteForgeFusion`, `ConsumeConsumableAsset`),
  and most disconnect. These are three different deliberate answers to "what
  does the player see", and several CLAUDE.md paragraphs exist to defend
  specific ones. A dispatch table that routes them all to one generic error
  path would be the refactor's single worst outcome — it would turn visible
  refusals into silent rollbacks, which is the defect class this server is
  named for in its own documentation.

---

### Task 2.20: Phase 2 review gate

Same as Task 1.21, plus:

- [ ] Confirm the `else if` chain now contains only the seven deliberately-kept
      branches listed above, and that each still carries the comment explaining
      why it stayed.
- [ ] `grep -n "Task.Run" server/FolkIdle.Server/Domain/` returns nothing new.
- [ ] `CommandGateOrderingTests` still passes **unmodified** after every cluster
      moved.

---

# PHASE 3 — `ProcessSubTick`'s branch bodies (the safer half of Option C)

**Do not start this phase until Phase 1 and Phase 2 are both complete and their
review gates are clean.** This is a hard gate.

### Scope boundary — read this first

`ProcessSubTick` (`SimulationEngine.cs:5393-6665`, 1,273 lines) branches on what
`ActiveActivityId` currently is. **This phase extracts the three branch bodies
into three methods. The activity-id `if / else if` dispatch itself does not
move.** After Phase 3, `ProcessSubTick` still contains the same `if`, the same
`else if`, and the same fall-through — it just calls out instead of inlining.
`ProcessSubTick`'s own signature is unchanged, which is what makes every
existing caller a regression harness (see Task 3.0).

### Phase 3 Global Constraints

These are additional to the plan-wide constraints and specific to this region.

- **The dispatch is not symmetric, and making it symmetric is a behaviour
  change.** Confirmed by reading: `ProcessSubTick` contains exactly three
  `return` statements — 5421 (the idle guard), **5629 (the last statement of
  the gathering branch)**, and 6328 (inside the death branch). The **crafting
  branch does not return**. A character on a crafting recipe runs the crafting
  progress block and then **falls through into the entire combat block**, where
  `fallbackId` resolves to 1 because a crafting activity id is in
  `ActivityIdBands.CraftingBand`, far above `ContentRegistry.Monsters.Length`.
  So a crafting character also fights monster 1 every tick, today, in
  production. **This plan does not change that.** An extraction that gives all
  three branches a symmetric `RunX(); return;` shape would silently stop that
  happening, which is a live gameplay change disguised as a refactor. Whether
  the fall-through is intended is a separate question for the owner — record it
  in the final report, do not answer it here.

- **The death branch's early `return` at 6328 is load-bearing control flow.**
  It is what keeps "died this tick" and "killed something this tick" mutually
  exclusive within one call: the kill-reward block at 6332 is only reached
  because a death returned before it. It sits inside the
  `!ConsumableEngine.TryInterceptLethalDamage(...)` block, so a Death Ward
  intercept deliberately does *not* return and the character can still land a
  kill in the same tick. Both facts must survive. Do not split the combat body
  into always-both-called `ResolveDeath()` / `ResolveKillReward()` methods.

- **The register-swap discipline lives one level up and must not be bypassed.**
  `ProcessAllSlotSubTicks` (5205-5252) wraps every `ProcessSubTick` call in
  `SwapSlotIntoActiveRegister` / `try` / `finally SwapSlotIntoActiveRegister`.
  Its own comment records why: if `ProcessSubTick` throws, a missed swap-back
  leaves the *wrong* character's gear, HP and activity in the active register
  for every subsequent read — the packet broadcast, the next tick, the
  checkpoint — and that corruption outlives the exception and gets attributed to
  something else. The three extracted methods must be called **only** from
  inside `ProcessSubTick`, never directly from a new caller. If a later change
  wants a new caller, it reproduces the swap/try/finally pair exactly.

- **Existing test coverage is not proof of correctness in this region.**
  CLAUDE.md records six defects that lived in these ~1,270 lines and were found
  by a *player*, not by a test: the armour-mitigation formula that cancelled
  against its own halving constant, the interval-crossing attack-cadence bug
  that made attack-speed affixes slow the player down, milli-HP long-vs-int
  overflow, crit-chance clamping, lifesteal read as a percent instead of a
  fraction, and the death-branch early return. A green suite here means "no
  known regression", not "correct". That is why Task 3.4 requires a manual
  browser pass.

- **No behaviour change, including no bug fixes.** If reading these 1,270 lines
  surfaces something that looks wrong — and it will — write it down in the
  report and leave it exactly as it is. A fix and a move in the same commit
  makes a bisect useless.

---

### Task 3.0: Establish the regression harness before touching anything

- [ ] Confirm `ProcessSubTick`'s callers. Live on 2026-09-17: one production
      caller (`ProcessAllSlotSubTicks`, line 5245) and four test files —
      `ProgressionRateTests.cs`, `CombatEventFeedTests.cs`,
      `DevFixtureInvariantTests.cs`, `HardenedEngineIntegrationTests.cs`
      (4 call sites). Because this phase leaves the signature untouched, all
      five callers are an unmodified regression harness. Re-run the grep;
      if a caller has been added, note it.
- [ ] Run the full suite and record which tests exercise `ProcessSubTick`, so
      that "still green" after each task means something specific.
      `ProgressionRateTests` in particular prints and asserts the monster-ladder
      pacing band — it is the closest thing to a combat oracle this repo has.

---

### Task 3.1: Extract the crafting-progress body

The smallest and safest of the three: 26 lines (5427-5452), reading only
`payload`, the `craftingRecipe` out-var, the constant `MinCraftTicks`
(line 150) and the static `CraftingTickQueue` (line 137). No shared locals with
either other branch.

**Files:** `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs:5427-5452`

- [ ] **Step 1:** Add a private static method beside `ProcessSubTick`:

```csharp
        /// <summary>
        /// Crafting-as-a-job progress for one tick. Extracted verbatim from
        /// ProcessSubTick's first activity branch.
        ///
        /// Modul: THIS METHOD DOES NOT RETURN OUT OF ProcessSubTick, AND THAT
        /// IS DELIBERATE. The crafting branch has never had a `return` - after
        /// counting a craft tick, control falls through into the combat block
        /// below, where a crafting activity id resolves to fallbackId 1. That
        /// is what the game does today; this extraction preserves it rather
        /// than tidying it into a symmetric three-way dispatch, which would be
        /// a live behaviour change wearing a refactor's clothes.
        /// </summary>
        private static void RunCraftingProgressTick(ref TickStatePayload payload, in RecipeDefinition craftingRecipe)
        {
            // ... lines 5429-5451 verbatim, comments included ...
        }
```

Confirm `RecipeDefinition` is the actual out-var type of
`ContentRegistry.TryGetRecipeByActivityId` (`ContentRegistry.cs:697`) and that
it can be passed by `in` — if it is a class, drop the `in`.

- [ ] **Step 2:** Replace the branch body with the call, keeping the `if` and
      keeping the absence of a `return`:

```csharp
            if (ContentRegistry.TryGetRecipeByActivityId(payload.ActiveActivityId, out var craftingRecipe))
            {
                RunCraftingProgressTick(ref payload, in craftingRecipe);
            }
            else if (...)
```

- [ ] **Step 3:** Build, full suite, commit.

```bash
git add server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs
git commit -m "refactor(tick): the crafting-progress branch body becomes a method"
```

---

### Task 3.2: Extract the gathering body

178 lines (5453-5630), reading `payload`, the `gatheringNode` out-var,
`localDropMultiplier`, and the static property `ActiveGlobalEventId`
(line 157 — static, so callable from a static method unchanged).

The load-bearing detail: **line 5629 is a `return`, and it is the last statement
of the branch.** It must be hoisted to the call site, not swallowed inside the
extracted method — a method that "returns" cannot make its caller return, and a
gathering character that fell through into the combat block would start fighting
monster 1 while gathering.

- [ ] **Step 1:** Add `RunGatheringTick(ref TickStatePayload payload, in GatheringNodeDefinition gatheringNode, int localDropMultiplier)`
      containing lines 5455-5628 verbatim (everything except the trailing
      `return;`). Confirm the out-var's real type name from
      `ContentRegistry.TryGetGatheringNode`.

- [ ] **Step 2:** The call site keeps the `return`:

```csharp
            else if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out var gatheringNode))
            {
                RunGatheringTick(ref payload, in gatheringNode, localDropMultiplier);
                return;
            }
```

- [ ] **Step 3:** Build, full suite, commit.

```bash
git add server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs
git commit -m "refactor(tick): the gathering branch body becomes a method"
```

Note for the reviewer's attention, not for fixing here: the gathering body
computes its own `gatherCombatStats` via `StatsCalculator.Calculate` (5511) and
the combat body computes `combatStats` the same way (5646). That duplication
exists today and is what makes these two branches cleanly separable; leave it.

---

### Task 3.3: Extract the combat-resolution body

The large one: ~1,030 lines (5632-6664). It lands alone, in its own commit, so
it can be reverted without losing Tasks 3.1 and 3.2.

- [ ] **Step 1: Read all 1,030 lines before editing.** Not skimmed, not
      grepped. CLAUDE.md's rule against editing multi-line C# initialisers by
      blind string replacement applies with full force here: one such edit
      inserted a field into the middle of a four-term sum, silently re-parented
      three bonuses onto the wrong field, compiled cleanly and passed every
      test.

- [ ] **Step 2:** Add the method with the same four non-payload parameters
      `ProcessSubTick` itself takes:

```csharp
        /// <summary>
        /// Monster combat resolution for one tick - spawn, the player's swing,
        /// the monster's reply, auto-eat, death, and kill rewards. Extracted
        /// verbatim from ProcessSubTick's fall-through branch.
        ///
        /// Modul: THE EARLY `return` IN THE DEATH BRANCH IS LOAD-BEARING. It
        /// is the only thing that keeps "died this tick" and "killed something
        /// this tick" mutually exclusive within one call - the kill-reward
        /// block below is reached only because a death returned before it. It
        /// sits inside the `!TryInterceptLethalDamage` block on purpose, so a
        /// Death Ward intercept is NOT a death and can still land a kill in
        /// the same tick. Do not split this into always-both-called
        /// ResolveDeath/ResolveKillReward methods; the exclusivity would have
        /// to be reproduced explicitly and there is nothing today that would
        /// catch it if it were reproduced wrongly.
        ///
        /// Callable ONLY from ProcessSubTick. The slot register's
        /// swap/try/finally discipline lives one level up in
        /// ProcessAllSlotSubTicks; a caller that bypasses it leaves the wrong
        /// character's gear, HP and activity "active" for every subsequent
        /// read after any throw in here.
        /// </summary>
        private static void RunCombatTick(
            ref TickStatePayload payload,
            int localXpMultiplier,
            int localDropMultiplier,
            System.Collections.Concurrent.ConcurrentQueue<GuildWarPointEvent> guildWarPointQueue,
            System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext> liveSessionContexts)
        {
            // ... lines 5632-6664 verbatim ...
        }
```

The `return;` at 6328 stays exactly where it is, now returning from
`RunCombatTick` instead of from `ProcessSubTick`. Since the combat block is the
*last* thing in `ProcessSubTick` and nothing follows it, returning from the
extracted method is behaviourally identical. **Verify this by reading the end of
`ProcessSubTick`**: line 6664 closes the kill-reward `if`, 6665 closes the
method. Nothing runs after the combat block today.

- [ ] **Step 3:** The call site — an unconditional call after the `if/else if`,
      matching today's fall-through:

```csharp
            RunCombatTick(ref payload, localXpMultiplier, localDropMultiplier, guildWarPointQueue, liveSessionContexts);
        }
```

- [ ] **Step 4:** Build, then diff-review before testing. `git diff` should show
      the 1,030 lines moved with **zero content changes** — indentation is the
      only thing allowed to differ, and even that only by the one level the move
      does not change (the body was already at method-body depth inside
      `ProcessSubTick`, so ideally nothing shifts at all). Any line that differs
      by more than whitespace is a bug you have just introduced.

- [ ] **Step 5:** Full suite. `ProgressionRateTests`, `CombatEventFeedTests` and
      `MonsterLadderTests` are the meaningful ones here.

- [ ] **Step 6:** Commit.

```bash
git add server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs
git commit -m "refactor(tick): the combat-resolution branch body becomes a method"
```

---

### Task 3.4: Manual verification in a real browser — required, not optional

The automated suite is necessary and not sufficient here, for the reason stated
in this phase's constraints: the known defects in this region were found by a
player. Per CLAUDE.md's verify rule and the `verify` skill, this phase ends at a
browser.

- [ ] **Step 1:** `.\run-dev.ps1`, sign in as the dev fixture
      (`dev@folkidle.local` / `FolkIdleDev123!`).

- [ ] **Step 2:** `npm run exercise` from `client_web/`. It must be fully green.
      If a step reports a spent villager or breeding pool, re-seed with
      `--seed-dev` (idempotent) and re-run — that is a fixture-state failure,
      not a regression.

- [ ] **Step 3: Fight a monster by hand, and make the character both die once
      and get a kill once.** This is the specific thing no automated test
      covers: the early `return` at 6328 is what makes those two outcomes
      mutually exclusive within a tick, and the only way to exercise the
      exclusivity is to produce both outcomes in a real session.
      - **A kill:** deploy against an early-region monster the fixture
        out-levels. Confirm XP moved, gold moved, the combat event feed showed a
        `Kill` event, and a loot drop or codex increment landed.
      - **A death:** empty the larder (or set the auto-eat threshold so nothing
        triggers) and deploy against a monster well above the character's
        level. Confirm the death card names the killer (`LastDeathMonsterId`
        is captured *before* the respawn wipes it, at 6295/6320), that HP
        resets to full, that the activity stops with a `Died` halt reason, and
        that **no kill reward landed on the same tick**.
      - Record both observations in the report. "I ran exercise" is not this
        step.

- [ ] **Step 4:** Confirm a gathering job and a crafting job each still produce
      output, since Tasks 3.1 and 3.2 moved their bodies. For crafting,
      specifically note whether the fall-through into combat is still
      observable (the character should still be engaging monster 1 while
      crafting, exactly as before this plan) — that is the confirmation that
      the asymmetric dispatch was preserved, and it belongs in the report as a
      question for the owner, not as a fix.

- [ ] **Step 5:** `superpowers:requesting-code-review` on the Phase 3 diff.

---

## Not in this plan

- **The seven command branches and six drains listed as deliberately kept.**
  Session lifecycle, the active-register swap, the guild index's writer, node
  migration and the anti-cheat challenge machinery all stay in
  `SimulationEngine`. Extracting them needs a session-lifecycle owner, which is
  a design decision nobody has taken.
- **`BattlePassWorkerLoop`** — `SimulationEngine`'s second background thread. It
  respects the single-writer invariant only by convention (it routes through
  `CommandQueue` rather than touching `TickStatePayload`). Scoping doc §6
  finding 2 proposes giving that convention real enforcement; this plan does not,
  because doing so changes behaviour rather than location.
- **The broadcast plane** (3558-3995) — the ~300-field `StateUpdatePacket`
  initializer and its dirty-check cache. It reads every domain and is guarded by
  `StateUpdatePacketFieldCoverageTests`, which is a *source scan* of that exact
  initializer block. Moving it would need that test rewritten in the same
  commit, which is the one situation this plan's "no test may change" rule
  cannot absorb.
- **`SimulationEngine`'s 26-parameter constructor.** Static coordinators do not
  reduce it — they take what they need as method parameters. Shrinking the
  constructor is the instantiated-coordinator design the owner explicitly did
  not choose.
- **Whether the crafting branch's fall-through into combat is a bug.** Found
  while confirming line numbers for Phase 3; recorded, deliberately not acted
  on. It is a gameplay question for the owner.
