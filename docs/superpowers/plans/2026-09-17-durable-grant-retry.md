# Durable Grant Retry (Outbox) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close `docs/TASK_BOARD.md` task 18 — loot, gathering, and offline
village production each catch a failed database write and simply log it; the
reward is gone. Build one durable outbox mechanism (a `pending_grants`
Postgres table, no new infra) and wire all three grant paths onto it, so a
transient failure (the exact Supabase-pooler shape that caused the original
loot-starvation incident) results in the reward landing on retry instead of
being silently dropped.

**Tech stack:** C# / .NET 8, EF Core + Npgsql, xUnit + Testcontainers (real
Postgres 16 per collection).

**Spec:** `docs/TASK_BOARD.md` §18 ("No durable retry for loot, gathering, or
offline production grants"), and CLAUDE.md's "A background worker that can
throw is a feature that can vanish", "An unbounded drain in a worker loop is
a starvation bug", and the `ConnectionStringDefaults.WithBoundedPool` note —
all read in full before this plan was written. Researched against the live
source on 2026-09-17: `server/FolkIdle.Server/Engine/CombatLootEngine.cs`
(the 2026-09-06 per-item try/catch, the fault-*isolation* fix this task
builds fault-*recovery* on top of), `server/FolkIdle.Server.Tests/
CronWorkerGuardTests.cs` (the guarded-worker inventory a new `StartCron` loop
must join), `server/FolkIdle.Server/Engine/OfflineSimulationEngine.cs`
(`GrantVillagePassiveProductionAsync`, `GrantSingleCommodityProductionAsync`),
`server/FolkIdle.Server/Models/FolkIdleDbContext.cs` and
`server/FolkIdle.Server/Models/AccountPenalty.cs` (the append-only,
snake_case-table shape this plan's model follows), and
`server/FolkIdle.Server/Models/ConnectionStringDefaults.cs` (the bounded
Npgsql pool this plan's worker must not bypass).

**Relationship to the sibling plan:** `docs/superpowers/plans/
2026-09-17-audit-fixes-14-17-19-20.md` Task 2 (#17) adds a failure counter
and a log line to `GrantVillagePassiveProductionAsync`'s existing `catch`
block — pure visibility, no persistence. This plan's Task 1 extends that
*same* catch block one layer further: after counting and logging, it also
persists the already-computed grant so it can be replayed. **#17 and #19
have both landed** (PR #9, 2026-09-19) — the catch block now also carries
#19's `committed`-flag/`return` shape. See Task 1 Step 5 below for the
now-current version of that block; the version originally sketched here is
stale.

**Re-validated 2026-09-19, before implementation, per the owner's brainstorm
gate.** Every method this plan targets was re-read against current
`main` (`GrantGatheredMaterialsAsync`, `DrainGatheringGrantsAsync`,
`ProcessMonsterLootDropAsync`, `GrantMaterialDropAsync`, `TryRollEquipment`)
— all match this plan's description structurally, with only line-number
drift. The pool size (12) and cron-worker count (14, confirmed via
`CronWorkerGuardTests.KnownCronEngines` — this task's worker is genuinely
the 15th) are both still accurate. **One gap found and closed by adding a
step to Task 4 below:** the drain worker as originally planned writes
straight to the database with no way to tell an online player their retry
just landed — they would not see it until their next relogin. Fixed by
reusing two mechanisms that already exist rather than inventing a new one
— see Task 4 Step 1b.

## Global Constraints

- **The outbox stores the RESOLVED grant, never the recipe to reroll it.**
  This is the load-bearing design decision in this whole plan. Combat loot
  rolls a random equipment tier, a random item and random affixes *before*
  the database write that can fail; gathering and offline production compute
  deterministic commodity deltas before their write. In every case, by the
  time the code reaches its `catch` block the outcome is already decided —
  only the write failed. A retry must apply that *exact* outcome. Re-running
  the original method from scratch would re-roll the dice and either hand
  the player a different item than the one they were told about (loot event
  feed already announced it — see `CombatLootEngine.PublishLootDrop`) or, in
  the worst case, apply two different outcomes for one failure if the first
  roll's write actually landed but its acknowledgement didn't. Every payload
  this plan writes to the outbox is therefore plain already-resolved data
  (item ids, quantities, an affix payload string), never "reroll with these
  inputs."
- **This is a fallback path, not a replacement for the in-memory queues.**
  `CombatLootEngine.DropRequestQueue` / `GatheringGrantQueue` stay exactly as
  they are — the 10Hz tick still can't touch the database directly, and
  those `ConcurrentQueue`s are still how work gets from the tick to a
  worker under normal operation. `pending_grants` only ever receives a row
  from inside a `catch` block, after the normal write has already thrown.
  Expected steady-state volume is near zero; a `PendingGrants` row that
  can't be applied is a symptom of the same class of DB outage that already
  page the on-call, not routine traffic. This is why it does not need
  `EquipmentInstances`' windowing treatment (CLAUDE.md, "a list of owned
  items must be windowed") — that table grows with *playtime*, this one
  should be empty almost all the time by construction (successful retries
  delete their row; see Task 4).
- **Idempotency key: `(PlayerId, SourceType, SourceSequence)`, unique.**
  `SourceType` says which subsystem produced the row (combat loot, gathering,
  offline production) for logs and reporting. `SourceSequence` is a
  monotonically increasing `long`, **minted once, in memory, per process, per
  `SourceType`**, via `Interlocked.Increment` — not a value recomputed from
  the grant's contents. This is scoped deliberately narrow: the enqueue call
  happens synchronously inside the same `catch` block that just observed the
  failure, in the same call stack, so "the same failure enqueued twice" can
  only happen from a future bug that calls the enqueue helper more than once
  for one failure — the unique constraint (`ON CONFLICT DO NOTHING`) is a
  belt-and-suspenders against *that*, not a distributed at-least-once
  producer guarantee. It does **not** protect against a process crash
  between the failed write and the enqueue call (a few CPU instructions
  apart) — that residual gap is accepted and named here rather than solved,
  because closing it would mean persisting *every* grant attempt before it
  is even tried, which is a fundamentally larger, slower design the board's
  Done-when does not ask for (it asks about a **forced database failure**,
  not a process kill). The counter is seeded at worker start from
  `SELECT COALESCE(MAX("SourceSequence"), 0) FROM pending_grants WHERE
  "SourceType" = @type`, so a restart never reissues a number a live,
  not-yet-resolved row already holds.
- **Give-up policy: bounded retries, permanent dead-letter, never silently
  dropped.** A reward is real value the player was already told (or will be
  told) they earned; silently deleting it after N failures would be the
  exact defect this task exists to close, just delayed. Capped at 10
  attempts with exponential backoff (15s base, x3 per attempt, capped at 30
  minutes) — total retry window a little over two hours, long enough to
  outlast a pooler hiccup or a rolling deploy, short enough that a
  genuinely broken write (bad constraint, corrupt payload) surfaces as a
  dead-letter row within a shift rather than retrying forever against a
  database that is never coming back for it. A dead-lettered row is never
  deleted — matches `AccountPenalty`'s "append-only, lift by stamping"
  convention — and is excluded from the drain query so it costs nothing
  going forward. Reviving one (clear `DeadLetteredAtEpochMs`, reset
  `AttemptCount`) is a manual DB operation for now; an admin replay tool is
  future work, out of scope here.
- **The drain worker takes a budget, never `while (TryDequeue)` /
  `while (more rows)`.** CLAUDE.md's own recorded trap
  (`CombatLootEngine`'s gathering starvation). Each cycle reads at most
  `MaxGrantsPerDrainCycle` (100) eligible rows via `... LIMIT 100`, processes
  each in its own transaction and its own `catch`, and stops — the next
  cycle picks up wherever this one left off. A backlog therefore drains
  across several cycles rather than blocking everything else the process
  does in one.
- **Concurrent drain safety: `FOR UPDATE SKIP LOCKED`, not a status column
  and a race.** Two containers briefly overlap during every rolling deploy
  (`docker compose up -d --build` starts the new one before stopping the
  old). `SELECT ... FOR UPDATE SKIP LOCKED LIMIT 1` makes it structurally
  impossible for two workers to claim the same row, so the apply-then-delete
  step below is exactly-once even with two processes polling at once — no
  extra locking table, no leader election.
- **Bounded connections: no new pool.** The drain worker uses the exact same
  `_serviceProvider.CreateScope()` → `FolkIdleDbContext` pattern every other
  engine uses, which is already capped by
  `ConnectionStringDefaults.WithBoundedPool` (`DefaultMaxPoolSize = 12`,
  Supabase's session pooler allows 15). It acquires a connection per row,
  releases it immediately after that row's commit/rollback — never holds one
  open across the cycle. This is one more short-lived consumer of the
  existing bounded pool, not a new one; note in Task 4 that this is the
  *fifteenth* `StartCron` loop sharing that pool and worth a glance at
  whether 12 is still comfortable headroom, but re-tuning the pool size is
  out of scope here.
- **Every new/changed test seeds its own rows and, where a task touches
  `PlayerSessionRegistry`, passes a fresh instance** — matching
  `BreedingRoundOneTests.cs:116` and both prior 2026-09-17 audit plans.
- **Forcing "a database failure during the grant" reuses this codebase's own
  established technique, not a literal severed connection.** The Testcontainers
  fixture (`PostgresTestFixture` in `HardenedEngineIntegrationTests.cs`) shares
  one Postgres container across every test class in its xUnit collection;
  stopping that container inside one test to simulate a connection loss would
  corrupt every other test scheduled against the same collection.
  `LootWorkerResilienceTests.AFailingRequestDoesNotStopTheQueue` already
  solved this the honest way: seed a row that makes the real write throw a
  real Postgres error (there, a foreign-key violation — a player id with no
  `PlayerRecords` row). From the calling code's point of view this is
  indistinguishable from a connection drop: both surface as an `Exception`
  out of `SaveChangesAsync`/`CommitAsync`. Every "forced failure" test in this
  plan uses that same poison-row technique. This is a deliberate,
  recorded choice, not a shortcut — a literal container kill was considered
  and rejected for the reason above.

---

## Task 1: The `pending_grants` table, the outbox helper, and offline village
production as the proof of the mechanism

Offline village production is the simplest of the three grant paths: every
delta (`woodEarned`, `oreEarned`, `rareWood`, `rareOre`, `goldEarned`) is
already a plain `long` local variable, fully computed, **before**
`GrantVillagePassiveProductionAsync` even opens its transaction (lines
306–358 all run before `BeginTransactionAsync` at line 360). There is no
randomness and no batching to reason about — it is the cleanest place to
prove the mechanism before the harder cases in Tasks 2 and 3.

**Files:**
- New migration: `server/FolkIdle.Server/Migrations/` (`pending_grants`
  table + `pending_grant_source_seq` sequence)
- New: `server/FolkIdle.Server/Models/PendingGrant.cs`
- New: `server/FolkIdle.Server/Engine/PendingGrantOutbox.cs` (the shared
  enqueue/apply helper both this task and Tasks 2–3 call)
- Modify: `server/FolkIdle.Server/Models/FolkIdleDbContext.cs` (add
  `DbSet<PendingGrant> PendingGrants`)
- Modify: `server/FolkIdle.Server/Engine/OfflineSimulationEngine.cs`
  (`GrantVillagePassiveProductionAsync`'s catch block, ~line 396–399 —
  confirm against the sibling plan's Task 2 (#17) landing first per Global
  Constraints)
- Test: new `server/FolkIdle.Server.Tests/PendingGrantOutboxTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `PendingGrantOutbox.EnqueueCommodityDeltasAsync(FolkIdleDbContext
  db, long playerId, int sourceType, IReadOnlyDictionary<string, long>
  deltas)` — Tasks 2 and 3 call this directly (Task 3 also introduces
  `EnqueueEquipmentGrantAsync`, see its own section).
- Produces: `PendingGrantOutbox.TryApplyOneAsync(FolkIdleDbContext db,
  PendingGrant row) -> Task<bool>` — applies one row's payload inside the
  caller's transaction and returns whether it succeeded; Task 4's drain loop
  is the only other caller, but this task's own tests call it directly to
  prove application works before a polling worker exists.

- [ ] **Step 1: Migration**

Add the table and its sequence. `Id` is the surrogate PK (bigserial);
`SourceSequence` comes from a dedicated Postgres sequence, minted by the app
(not a DB default) per the idempotency-key reasoning in Global Constraints —
so the migration only needs to create the sequence, not attach it as a
column default.

```csharp
migrationBuilder.Sql("CREATE SEQUENCE pending_grant_source_seq;");

migrationBuilder.CreateTable(
    name: "pending_grants",
    columns: table => new
    {
        Id = table.Column<long>(type: "bigint", nullable: false)
            .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
        PlayerId = table.Column<long>(type: "bigint", nullable: false),
        SourceType = table.Column<int>(type: "integer", nullable: false),
        SourceSequence = table.Column<long>(type: "bigint", nullable: false),
        PayloadKind = table.Column<string>(type: "text", nullable: false),
        PayloadJson = table.Column<string>(type: "text", nullable: false),
        CreatedAtEpochMs = table.Column<long>(type: "bigint", nullable: false),
        NextAttemptAtEpochMs = table.Column<long>(type: "bigint", nullable: false),
        AttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
        LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
        DeadLetteredAtEpochMs = table.Column<long>(type: "bigint", nullable: true),
    },
    constraints: table => table.PrimaryKey("PK_pending_grants", x => x.Id));

migrationBuilder.CreateIndex(
    name: "IX_pending_grants_idempotency",
    table: "pending_grants",
    columns: new[] { "PlayerId", "SourceType", "SourceSequence" },
    unique: true);

// Partial index: only rows the drain worker will ever query for are indexed,
// so a table full of permanent dead-letters (rare, but the whole point of
// keeping them) never slows the query that matters.
migrationBuilder.Sql(
    "CREATE INDEX \"IX_pending_grants_eligible\" ON pending_grants (\"NextAttemptAtEpochMs\") " +
    "WHERE \"DeadLetteredAtEpochMs\" IS NULL;");
```

`Down` drops the index, the table, then the sequence, in that order.

Generate this with the project's normal EF tooling against the raw SQL
additions above rather than hand-writing the whole `Designer.cs` snapshot;
follow the same "additive, no backfill" shape as the sibling
`AddCurrentSessionNonce` migration already in this worktree.

- [ ] **Step 2: `PendingGrant.cs`**

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: THE OUTBOX. A grant that already succeeded at deciding WHAT to
    // give a player (a rolled item, a coalesced set of material deltas) but
    // failed to WRITE it - the exact gap CombatLootEngine's per-item
    // try/catch left open: fault isolation, not fault recovery. A row here
    // is resolved data, never "redo this random roll" - see
    // PendingGrantOutbox's own doc comment for why that distinction is load-
    // bearing.
    //
    // Expected to be near-empty in steady state: a successful drain DELETES
    // its row (Task 4), so this table's size tracks "grants currently
    // failing to apply", not playtime. Unlike EquipmentInstances (which
    // reached 17,836 rows and needed windowing), a permanently full
    // pending_grants table is itself the incident, not a slow screen.
    [Table("pending_grants")]
    public class PendingGrant
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        /// <summary>Which subsystem produced this row. See PendingGrantSourceType.</summary>
        public int SourceType { get; set; }

        /// <summary>
        /// Idempotency key half 2 of 2, with (PlayerId, SourceType). A
        /// per-process, per-SourceType monotonic counter minted once at
        /// enqueue time - not derived from the payload. See
        /// PendingGrantOutbox.NextSourceSequence.
        /// </summary>
        public long SourceSequence { get; set; }

        /// <summary>"commodity_deltas" or "equipment_grant". See PendingGrantPayloadKind.</summary>
        [MaxLength(32)]
        public string PayloadKind { get; set; } = string.Empty;

        public string PayloadJson { get; set; } = string.Empty;

        public long CreatedAtEpochMs { get; set; }

        /// <summary>Eligible for the next drain cycle once this passes. Never null - a fresh row is eligible immediately.</summary>
        public long NextAttemptAtEpochMs { get; set; }

        public int AttemptCount { get; set; }

        [MaxLength(512)]
        public string? LastError { get; set; }

        /// <summary>Null while retries continue. Stamped, never deleted - see AccountPenalty for the same convention.</summary>
        public long? DeadLetteredAtEpochMs { get; set; }
    }

    public static class PendingGrantSourceType
    {
        public const int CombatLoot = 1;
        public const int Gathering = 2;
        public const int OfflineVillageProduction = 3;
    }

    public static class PendingGrantPayloadKind
    {
        /// <summary>PayloadJson is a Dictionary&lt;string, long&gt; of ItemId -> delta (gold included as "gold").</summary>
        public const string CommodityDeltas = "commodity_deltas";

        /// <summary>PayloadJson is one EquipmentGrantPayload (BaseItemId, QualityTier, AffixPayload).</summary>
        public const string EquipmentGrant = "equipment_grant";
    }
}
```

- [ ] **Step 3: `FolkIdleDbContext.cs`**

Add `public DbSet<PendingGrant> PendingGrants { get; set; }` beside the other
`DbSet<>` declarations (no `OnModelCreating` entry needed — the unique index
and partial index came from raw SQL in the migration, and `[Table]` already
sets the table name).

- [ ] **Step 4: `PendingGrantOutbox.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The shared read/write surface for pending_grants. Enqueue is called
    /// from inside an existing catch block, after the normal write already
    /// failed and its transaction already rolled back. Apply is called by
    /// Task 4's drain worker (and directly by this task's own tests, before
    /// that worker exists) to replay a row's already-resolved outcome.
    /// </summary>
    public static class PendingGrantOutbox
    {
        // Modul: one counter per SourceType, not one global counter - see
        // Global Constraints for why this is in-memory and not a DB-derived
        // value recomputed from the payload. Seeded from the table's own
        // high-water mark at first use so a process restart never reissues
        // a number a still-pending row already holds.
        private static readonly long[] _nextSequence = new long[4]; // index 0 unused, 1..3 = PendingGrantSourceType values
        private static readonly bool[] _seeded = new bool[4];
        private static readonly object _seedLock = new();

        private static async Task EnsureSeededAsync(FolkIdleDbContext db, int sourceType)
        {
            if (Volatile.Read(ref _seeded[sourceType])) return;
            lock (_seedLock)
            {
                if (_seeded[sourceType]) return;
            }

            long max = await db.PendingGrants
                .Where(g => g.SourceType == sourceType)
                .Select(g => (long?)g.SourceSequence)
                .MaxAsync() ?? 0L;

            lock (_seedLock)
            {
                if (!_seeded[sourceType])
                {
                    _nextSequence[sourceType] = max;
                    _seeded[sourceType] = true;
                }
            }
        }

        private static long NextSourceSequence(int sourceType) => Interlocked.Increment(ref _nextSequence[sourceType]);

        /// <summary>
        /// Persists an already-resolved set of commodity deltas (materials
        /// and/or gold, by ItemId) for retry. Called from a catch block AFTER
        /// the normal transaction has rolled back - this opens its OWN short
        /// transaction on the same DbContext, since the caller's is already
        /// gone.
        /// </summary>
        public static async Task EnqueueCommodityDeltasAsync(
            FolkIdleDbContext db, long playerId, int sourceType, IReadOnlyDictionary<string, long> deltas)
        {
            if (deltas.Count == 0) return;
            await EnsureSeededAsync(db, sourceType);

            var row = new PendingGrant
            {
                PlayerId = playerId,
                SourceType = sourceType,
                SourceSequence = NextSourceSequence(sourceType),
                PayloadKind = PendingGrantPayloadKind.CommodityDeltas,
                PayloadJson = JsonSerializer.Serialize(deltas),
                CreatedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                NextAttemptAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            // ON CONFLICT DO NOTHING: the unique (PlayerId, SourceType,
            // SourceSequence) index makes a duplicate enqueue for the same
            // logical failure a no-op rather than a second row. See Global
            // Constraints on what this does and does not protect against.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO pending_grants
                    (""PlayerId"", ""SourceType"", ""SourceSequence"", ""PayloadKind"", ""PayloadJson"",
                     ""CreatedAtEpochMs"", ""NextAttemptAtEpochMs"", ""AttemptCount"")
                VALUES
                    ({row.PlayerId}, {row.SourceType}, {row.SourceSequence}, {row.PayloadKind}, {row.PayloadJson},
                     {row.CreatedAtEpochMs}, {row.NextAttemptAtEpochMs}, 0)
                ON CONFLICT (""PlayerId"", ""SourceType"", ""SourceSequence"") DO NOTHING");
        }

        /// <summary>
        /// Applies one row's resolved payload inside the CALLER's transaction
        /// and stages the row for deletion (via the caller's SaveChanges) -
        /// this method does not commit anything itself, so success and the
        /// row's removal are always one atomic unit. Returns false only for
        /// an unrecognized PayloadKind (a version-skew bug, not a transient
        /// failure) so the caller can decide how to record that separately
        /// from an ordinary retryable exception.
        /// </summary>
        public static async Task<bool> TryApplyOneAsync(FolkIdleDbContext db, PendingGrant row)
        {
            switch (row.PayloadKind)
            {
                case PendingGrantPayloadKind.CommodityDeltas:
                    var deltas = JsonSerializer.Deserialize<Dictionary<string, long>>(row.PayloadJson)!;
                    foreach (var (itemId, amount) in deltas)
                    {
                        if (amount == 0) continue;
                        var commodity = await db.CommodityRecords
                            .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {row.PlayerId} AND \"ItemId\" = {itemId} FOR UPDATE")
                            .SingleOrDefaultAsync();
                        if (commodity == null)
                        {
                            db.CommodityRecords.Add(new CommodityRecord { PlayerId = row.PlayerId, ItemId = itemId, Quantity = Math.Max(0L, amount) });
                        }
                        else
                        {
                            commodity.Quantity = Math.Max(0L, commodity.Quantity + amount);
                        }
                    }
                    return true;

                case PendingGrantPayloadKind.EquipmentGrant:
                    var grant = JsonSerializer.Deserialize<EquipmentGrantPayload>(row.PayloadJson)!;
                    db.EquipmentInstances.Add(new EquipmentInstance
                    {
                        BaseItemId = grant.BaseItemId,
                        PlayerId = row.PlayerId,
                        QualityTier = grant.QualityTier,
                        AffixPayload = grant.AffixPayload,
                        IsAffixLocked = false
                    });
                    return true;

                default:
                    return false;
            }
        }
    }

    /// <summary>PayloadJson shape for PendingGrantPayloadKind.EquipmentGrant. See CombatLootEngine.TryRollEquipment for where these three values are already decided before the write that can fail.</summary>
    public struct EquipmentGrantPayload
    {
        public string BaseItemId;
        public int QualityTier;
        public string AffixPayload;
    }
}
```

- [ ] **Step 5: wire `GrantVillagePassiveProductionAsync`**

Current shape, now that #17 and #19 have both landed (`OfflineSimulationEngine.cs`,
inside `GrantVillagePassiveProductionAsync` — note the method now returns
`Task<long>` and closes over a `committed` flag; do not disturb either):

```csharp
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Interlocked.Increment(ref _villageProductionFailures);
                Console.WriteLine(
                    $"Village: offline production for player {playerId} failed and was rolled back - "
                    + $"lost {goldEarned} gold, {woodEarned}+{rareWood} wood, {oreEarned}+{rareOre} ore: {ex.Message}");
            }

            return committed ? materialsLostToFullWarehouse : 0L;
```

Add the enqueue call inside that same `catch`, after the existing log line
(nothing else in the block changes — `committed` stays `false`, so the
`return` after the block still correctly reports `0L`, since #19's overflow
accounting has nothing to report on a rolled-back grant either):

```csharp
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Interlocked.Increment(ref _villageProductionFailures);
                Console.WriteLine(
                    $"Village: offline production for player {playerId} failed and was rolled back - "
                    + $"lost {goldEarned} gold, {woodEarned}+{rareWood} wood, {oreEarned}+{rareOre} ore: {ex.Message} - queued for retry.");

                var deltas = new Dictionary<string, long>();
                if (woodEarned > 0) deltas[lumberjackMats.Log] = woodEarned;
                if (oreEarned > 0) deltas[mineMats.Ore] = oreEarned;
                if (rareWood > 0) deltas[lumberjackMats.RareLog] = rareWood;
                if (rareOre > 0) deltas[mineMats.RareOre] = rareOre;
                if (goldEarned > 0) deltas["gold"] = goldEarned;

                await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                    db, playerId, PendingGrantSourceType.OfflineVillageProduction, deltas);
            }

            return committed ? materialsLostToFullWarehouse : 0L;
```

(All five locals — `woodEarned`, `oreEarned`, `rareWood`, `rareOre`,
`goldEarned` — are already in scope at this point; nothing new needs to be
computed. This is exactly the "resolved before the write" property Global
Constraints describes.) The enqueue call opens its own short transaction on
`db` after the caller's has already rolled back — confirm `db` is still a
usable, non-disposed `DbContext` at this point (it is: the caller only
disposes `transaction`, not `db` itself) before relying on this.

- [ ] **Step 6: tests, `PendingGrantOutboxTests.cs`**

1. **The row lands correctly.** Force a failure in
   `GrantVillagePassiveProductionAsync` via the poison-row technique
   (Global Constraints) — the simplest lever here is a `CommodityRecord`
   already present with a value that makes the `FOR UPDATE` read or the
   later write violate a constraint, or (simpler, matching
   `LootWorkerResilienceTests`) a `playerId` with no `PlayerRecords` row so
   the eventual insert throws a foreign-key violation. Run the grant, assert
   it throws/rolls back as today, then query `PendingGrants` directly and
   assert: exactly one row, `SourceType == OfflineVillageProduction`,
   `PayloadKind == CommodityDeltas`, and the deserialized deltas match the
   exact wood/ore/rare/gold amounts the (deterministic) formula would have
   produced for the seeded levels and elapsed time.
2. **Applying the row delivers the reward.** Take the row from test 1 (or a
   freshly seeded one against a *valid* player this time), call
   `PendingGrantOutbox.TryApplyOneAsync` directly inside a test-owned
   transaction, commit, and assert the player's `CommodityRecords` actually
   increased by the payload's amounts. This is Task 1's proof that
   *application* works, ahead of Task 4's polling wrapper existing.
3. **Duplicate enqueue is a no-op.** Call `EnqueueCommodityDeltasAsync` twice
   with the same `playerId`/`sourceType` and a `SourceSequence` forced to
   collide (construct the row manually via the same SQL rather than through
   the counter, to make the test deterministic) — assert only one row exists
   afterward. This pins the `ON CONFLICT DO NOTHING` behavior described in
   Global Constraints.
4. **A dropped/full warehouse still enqueues correctly** — reuse whatever
   near-full-warehouse seeding Task 3 of the sibling plan (#19) established,
   if it has landed, to confirm this task's enqueue and that task's overflow
   accounting don't interfere with each other (they touch adjacent but
   distinct code in the same method).

- [ ] **Step 7: run the full server suite once**

- [ ] **Step 8: Commit**

```bash
git add server/FolkIdle.Server/Models/PendingGrant.cs server/FolkIdle.Server/Models/FolkIdleDbContext.cs server/FolkIdle.Server/Engine/PendingGrantOutbox.cs server/FolkIdle.Server/Engine/OfflineSimulationEngine.cs server/FolkIdle.Server/Migrations/ server/FolkIdle.Server.Tests/PendingGrantOutboxTests.cs
git commit -m "feat(reliability): a failed offline production grant is now queued for retry, not just logged"
```

---

## Task 2: Gathering grants join the outbox

**Files:**
- Modify: `server/FolkIdle.Server/Engine/CombatLootEngine.cs`
  (`DrainGatheringGrantsAsync`'s catch, ~line 680–685, and
  `GrantGatheredMaterialsAsync`, which needs its already-coalesced
  `byMaterial` dictionary available at the point of failure)
- Test: extend `PendingGrantOutboxTests.cs` or a nearby gathering-specific
  file

**Interfaces:**
- Consumes: `PendingGrantOutbox.EnqueueCommodityDeltasAsync` from Task 1,
  unchanged.
- Produces: nothing new — this task is purely "wire an existing helper onto
  a second call site."

`GrantGatheredMaterialsAsync` already builds `byMaterial` (a
`Dictionary<string, (int ItemId, long ActivityId, int Quantity)>`) *before*
`strategy.ExecuteAsync` opens its transaction (lines 1290–1305, transaction
at 1316). Exactly the same "resolved before the write" shape as Task 1's
target — no restructuring needed, only a catch block.

- [ ] **Step 1: Write the failing test**

Mirror Task 1's test 1: enqueue a `GatheredMaterialGrant` for a `playerId`
with no `PlayerRecords` row (poison technique), call
`GrantGatheredMaterialsAsync` (it is `private` — call through
`DrainGatheringGrantsAsync` via the public queue, the same seam
`LootWorkerResilienceTests` already uses for `CombatLootEngine`'s private
methods, or reflection if that is cleaner — match whichever this file's
existing tests already do). Assert the write throws today (RED), and that no
`PendingGrants` row exists yet.

- [ ] **Step 2: Implement**

`GrantGatheredMaterialsAsync`'s `strategy.ExecuteAsync(...)` call has no
surrounding `try`/`catch` today — `DrainGatheringGrantsAsync` is what catches
(line 675–685):

```csharp
                try
                {
                    await GrantGatheredMaterialsAsync(player.Key, player);
                    _gatheringWrites++;
                }
                catch (Exception ex)
                {
                    _requestsFailed++;
                    Console.WriteLine(
                        $"Loot: gathering grant for player {player.Key} failed: {ex.Message}");
                }
```

The coalesced `byMaterial` dictionary lives inside
`GrantGatheredMaterialsAsync`, one level down from this catch, so it is not
visible here. Two options: (a) have `GrantGatheredMaterialsAsync` itself
catch around its `strategy.ExecuteAsync` call and enqueue there, where
`byMaterial` is in scope, re-throwing afterward so `DrainGatheringGrantsAsync`'s
existing counters/logging still fire unchanged; or (b) change
`GrantGatheredMaterialsAsync`'s signature to return `byMaterial` on failure.
**Prefer (a)** — it keeps every existing counter and log line exactly as-is
and adds exactly one new step, matching how Task 1 extended
`GrantVillagePassiveProductionAsync`'s existing catch rather than replacing
it:

```csharp
            try
            {
                await strategy.ExecuteAsync(async () => { /* unchanged body */ });
            }
            catch (Exception)
            {
                var deltas = byMaterial.ToDictionary(m => m.Key, m => (long)m.Value.Quantity);
                await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                    dbContext, playerId, PendingGrantSourceType.Gathering, deltas);
                throw; // DrainGatheringGrantsAsync's existing catch still counts and logs this.
            }
```

- [ ] **Step 3: Run the tests, verify pass**

- [ ] **Step 4: run the full server suite once**

- [ ] **Step 5: Commit**

```bash
git add server/FolkIdle.Server/Engine/CombatLootEngine.cs server/FolkIdle.Server.Tests/
git commit -m "feat(reliability): a failed gathering grant is now queued for retry, not just logged"
```

---

## Task 3: Combat loot (equipment + materials) joins the outbox

**The hard one.** `ProcessMonsterLootDropAsync` processes a whole
*batch* — up to `Kills` iterations (200 in a live tick request, up to
200,000 in an offline catch-up request; see `CombatLootDropRequest.Kills`),
each of which can independently roll a material grant and/or one or two
equipment pieces, all inside ONE transaction. Unlike Tasks 1 and 2, the
resolved outcome is not one pre-existing local variable — it accumulates
across the loop, alongside EF's own change tracker (which cannot be
serialized to JSON directly). This task adds a parallel plain-data
accumulator so that whatever the transaction was about to commit is also
available, as data, the moment it fails.

**Files:**
- Modify: `server/FolkIdle.Server/Engine/CombatLootEngine.cs`
  (`ProcessMonsterLootDropAsync`, `GrantMaterialDropAsync`,
  `TryRollEquipment` — see the accumulator shape below)
- Test: extend `PendingGrantOutboxTests.cs` or a nearby loot-specific file

**Interfaces:**
- Consumes: `PendingGrantOutbox.EnqueueCommodityDeltasAsync` (Task 1) and,
  new here, `PendingGrantOutbox.EnqueueEquipmentGrantAsync` for the
  equipment side (a thin sibling of the commodity helper, added in this
  task since Task 1 had nothing to prove it against yet).
- Produces: `PendingGrantOutbox.EnqueueEquipmentGrantAsync(FolkIdleDbContext
  db, long playerId, int sourceType, string baseItemId, int qualityTier,
  string affixPayload)` — same shape as
  `EnqueueCommodityDeltasAsync`, one `PendingGrant` row with
  `PayloadKind = EquipmentGrant`.

- [ ] **Step 1: the accumulator**

Add two small local collections at the top of
`ProcessMonsterLootDropAsync`, alongside the existing `SalvageTally salvage`
local:

```csharp
                var resolvedCommodityDeltas = new Dictionary<string, long>();
                var resolvedEquipmentGrants = new List<EquipmentGrantPayload>();
```

`GrantMaterialDropAsync` and `TryRollEquipment` both already decide their
outcome and hand it to EF's change tracker (`dbContext.CommodityRecords.Add`
/ `existing.Quantity +=`, `dbContext.EquipmentInstances.Add`) — add one line
at each site that *also* records the same outcome into these plain
dictionaries/lists, passed in as parameters (both methods are already
instance/static methods on this class; thread the accumulators through their
parameter lists rather than making them fields, since this method is
re-entered per request and fields would leak state across calls). For
`GrantMaterialDropAsync`, immediately after the `Quantity`
increment/`Add`:

```csharp
                resolvedCommodityDeltas.TryGetValue(materialItemId, out long existingDelta);
                resolvedCommodityDeltas[materialItemId] = existingDelta + quantity;
```

For `TryRollEquipment`, immediately after `dbContext.EquipmentInstances.Add(...)`:

```csharp
                resolvedEquipmentGrants.Add(new EquipmentGrantPayload
                {
                    BaseItemId = baseItemId, QualityTier = tier, AffixPayload = affixPayload
                });
```

(Auto-salvaged pieces — the `autoSalvageBelowTier` branch that returns early
with a gold value instead of adding an `EquipmentInstance` — do not add to
`resolvedEquipmentGrants`; they never touch `EquipmentInstances` at all, so
there is nothing to retry as equipment. Their gold is `salvage.Gold`, which
is a payload channel — see the note below.)

- [ ] **Step 2: the catch block**

```csharp
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _pendingDrops.Clear();
                Console.WriteLine($"Combat loot drop failed: {ex.Message} - queued for retry.");

                if (resolvedCommodityDeltas.Count > 0)
                {
                    await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                        dbContext, playerId, PendingGrantSourceType.CombatLoot, resolvedCommodityDeltas);
                }
                foreach (var grant in resolvedEquipmentGrants)
                {
                    await PendingGrantOutbox.EnqueueEquipmentGrantAsync(
                        dbContext, playerId, PendingGrantSourceType.CombatLoot,
                        grant.BaseItemId, grant.QualityTier, grant.AffixPayload);
                }
            }
```

**Deliberately excluded from this task: `salvage.Gold` (auto-salvage gold)
and the loot event feed (`_pendingDrops`).** Auto-salvage gold is paid
through `PlayerSessionRegistry.AutoSalvageQueue` — a live-session-only
channel (CLAUDE.md, "two gold paths") that has no meaning for a player who
is not currently connected, which is exactly the situation a retried grant,
some seconds to hours later, may be in. Re-crediting the salvaged gold as a
`CommodityRecords["gold"]` *outbox* delta (rather than through
`AutoSalvageQueue`) is the correct fix and shares this task's `deltas`
dictionary — add `if (salvage.Gold > 0L) resolvedCommodityDeltas["gold"] =
resolvedCommodityDeltas.GetValueOrDefault("gold") + salvage.Gold;` right
before the catch's enqueue calls, in the same place `salvage.Gold` is
already read on the success path (~line 951). The loot event feed
(`_pendingDrops`) is cosmetic (an in-session announcement) and is correctly
left unsent on a failure that gets retried later — there is no live session
to announce to, and the retried grant still lands in the player's
chest/bank.

- [ ] **Step 3: `PendingGrantOutbox.EnqueueEquipmentGrantAsync`**

```csharp
        public static async Task EnqueueEquipmentGrantAsync(
            FolkIdleDbContext db, long playerId, int sourceType, string baseItemId, int qualityTier, string affixPayload)
        {
            await EnsureSeededAsync(db, sourceType);

            var payload = new EquipmentGrantPayload { BaseItemId = baseItemId, QualityTier = qualityTier, AffixPayload = affixPayload };
            long sequence = NextSourceSequence(sourceType);
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO pending_grants
                    (""PlayerId"", ""SourceType"", ""SourceSequence"", ""PayloadKind"", ""PayloadJson"",
                     ""CreatedAtEpochMs"", ""NextAttemptAtEpochMs"", ""AttemptCount"")
                VALUES
                    ({playerId}, {sourceType}, {sequence}, {PendingGrantPayloadKind.EquipmentGrant},
                     {JsonSerializer.Serialize(payload)}, {nowMs}, {nowMs}, 0)
                ON CONFLICT (""PlayerId"", ""SourceType"", ""SourceSequence"") DO NOTHING");
        }
```

- [ ] **Step 4: tests**

1. **Multi-kill batch, forced failure, exact replay.** Seed a request with
   `Kills = 50` against a poisoned `playerId` (no `PlayerRecords` row) so the
   whole batch's commit fails. Assert: the resulting `PendingGrants` rows'
   summed commodity deltas and count of equipment grants are internally
   consistent (every `EquipmentGrantPayload`'s `BaseItemId` is one this
   monster's table can actually produce; the commodity deltas' keys are
   valid item ids) — this cannot assert an exact expected value the way
   Task 1 could (the rolls are random), but it can assert the *shape* is
   right and that applying every resulting row (via `TryApplyOneAsync` in a
   loop, this task's own test, ahead of Task 4's worker) produces exactly
   the equipment and material rows the batch was trying to write, with no
   duplicates.
2. **Auto-salvage gold survives a failure.** Force `autoSalvageBelowTier` to
   guarantee at least one salvage in the batch (seed a low threshold),
   force the failure, and assert the resulting commodity-deltas row
   includes `"gold"` for the salvaged amount.
3. **A single-kill live-tick request (Kills defaults to 1 via the zero-means-
   one rule) behaves the same as the batch case** — a regression guard, since
   most real traffic is Kills=1 and Task 3's accumulator change must not
   alter that path's existing (already-tested)
   `EveryCanonicalMonsterGrantsWhatItsTablesPromise` behavior. Re-run that
   existing test unmodified as part of this step to confirm no regression.

- [ ] **Step 5: run the full server suite once**

- [ ] **Step 6: Commit**

```bash
git add server/FolkIdle.Server/Engine/CombatLootEngine.cs server/FolkIdle.Server.Tests/
git commit -m "feat(reliability): a failed combat loot grant (equipment, materials, and salvage gold) is now queued for retry"
```

---

## Task 4: The drain worker

Everything above writes to `pending_grants`. Nothing yet reads it back on a
schedule — Tasks 1–3's own tests call `PendingGrantOutbox.TryApplyOneAsync`
directly. This task adds the actual background worker, guarded per
`CronWorkerGuardTests`' convention, and is where the board's literal
Done-when ("kills the DB connection mid-grant and asserts the reward
eventually lands", via the poison-row technique) gets its end-to-end proof
for all three paths at once, through the real polling loop.

**Files:**
- New: `server/FolkIdle.Server/Engine/PendingGrantDrainEngine.cs`
- Modify: `server/FolkIdle.Server/Program.cs` (construct + `StartCron()`,
  beside `combatLootEngine.StartCron()`)
- Modify: `server/FolkIdle.Server.Tests/CronWorkerGuardTests.cs`
  (`KnownCronEngines`, add `PendingGrantDrainEngine`)
- Test: new `server/FolkIdle.Server.Tests/PendingGrantDrainEngineTests.cs`

**Interfaces:**
- Consumes: `PendingGrantOutbox.TryApplyOneAsync` (Task 1).
- Produces: nothing another task depends on.

- [ ] **Step 1: `PendingGrantDrainEngine.cs`**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    // Modul: THE RETRY HALF of the outbox CombatLootEngine and
    // OfflineSimulationEngine now write to on a failed grant. Guarded per
    // CronWorkerGuardTests' convention - the catch wraps the connection
    // acquisition, not just the loop around it, for the same reason
    // CombatLootEngine's does: that IS where Supabase's pooler throws.
    public class PendingGrantDrainEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerSessionRegistry;
        private CancellationTokenSource _cts = new();

        // Modul: A BUDGET, NOT "EVERY ELIGIBLE ROW" - see CombatLootEngine's
        // own recorded gathering-starvation trap. One cycle reads at most
        // this many rows; a bigger backlog drains across several cycles
        // instead of holding this worker (and the connection it is using)
        // for however long an unbounded backlog takes.
        public const int MaxGrantsPerDrainCycle = 100;

        public const int MaxAttempts = 10;
        private const long BaseBackoffMs = 15_000L;
        private const long MaxBackoffMs = 1_800_000L; // 30 minutes

        private static long _applied;
        private static long _failed;
        private static long _deadLettered;
        private long _lastReportMs;

        public PendingGrantDrainEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerSessionRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerSessionRegistry = playerSessionRegistry;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
            Console.WriteLine("Pending grant drain worker started.");
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, stoppingToken);
                    await DrainOneCycleAsync();
                    ReportThroughput();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Same shape as CombatLootEngine's outer guard: nothing
                    // above should reach here, but the one thing this worker
                    // must never do is stop.
                    Console.WriteLine($"Pending grant drain cycle failed: {ex.Message}");
                }
            }

            Console.WriteLine("Pending grant drain worker STOPPED. Queued grants will not be retried until restart.");
        }

        private async Task DrainOneCycleAsync()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            for (int i = 0; i < MaxGrantsPerDrainCycle; i++)
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // FOR UPDATE SKIP LOCKED: two overlapping containers during a
                // rolling deploy can poll at the same time without ever
                // claiming the same row - see Global Constraints.
                var row = await db.PendingGrants
                    .FromSqlInterpolated($@"
                        SELECT * FROM pending_grants
                        WHERE ""DeadLetteredAtEpochMs"" IS NULL AND ""NextAttemptAtEpochMs"" <= {nowMs}
                        ORDER BY ""Id"" LIMIT 1 FOR UPDATE SKIP LOCKED")
                    .SingleOrDefaultAsync();

                if (row == null) return; // nothing eligible - stop early, don't spin the budget for nothing.

                using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                try
                {
                    bool applied = await PendingGrantOutbox.TryApplyOneAsync(db, row);
                    if (!applied)
                    {
                        // Unrecognized PayloadKind - a version-skew bug, not
                        // transient. Dead-letter immediately rather than
                        // retrying something that can never succeed.
                        row.DeadLetteredAtEpochMs = nowMs;
                        row.LastError = $"unrecognized PayloadKind '{row.PayloadKind}'";
                        await db.SaveChangesAsync();
                        await transaction.CommitAsync();
                        Interlocked.Increment(ref _deadLettered);
                        continue;
                    }

                    db.PendingGrants.Remove(row);
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    Interlocked.Increment(ref _applied);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    Interlocked.Increment(ref _failed);

                    // A fresh scope: the one above may be in a bad state
                    // after the exception above (especially if it came from
                    // the connection itself). Recording the failed attempt
                    // must not depend on the connection that just failed.
                    using var retryScope = _serviceProvider.CreateScope();
                    var retryDb = retryScope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    int attempt = row.AttemptCount + 1;
                    long backoffMs = Math.Min(MaxBackoffMs, BaseBackoffMs * (long)Math.Pow(3, attempt));
                    long nextAttemptAt = nowMs + backoffMs;
                    bool giveUp = attempt >= MaxAttempts;

                    await retryDb.Database.ExecuteSqlInterpolatedAsync($@"
                        UPDATE pending_grants SET
                            ""AttemptCount"" = {attempt},
                            ""LastError"" = {Truncate(ex.Message, 512)},
                            ""NextAttemptAtEpochMs"" = {nextAttemptAt},
                            ""DeadLetteredAtEpochMs"" = {(giveUp ? (long?)nowMs : null)}
                        WHERE ""Id"" = {row.Id}");

                    if (giveUp) Interlocked.Increment(ref _deadLettered);

                    Console.WriteLine(
                        $"Pending grant {row.Id} (player {row.PlayerId}, {row.PayloadKind}) attempt {attempt} failed: {ex.Message}"
                        + (giveUp ? " - DEAD-LETTERED." : $" - retrying in {backoffMs / 1000}s."));
                }
            }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        private void ReportThroughput()
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastReportMs < 60_000) return;
            _lastReportMs = nowMs;

            long applied = Interlocked.Exchange(ref _applied, 0);
            long failed = Interlocked.Exchange(ref _failed, 0);
            long deadLettered = Interlocked.Exchange(ref _deadLettered, 0);
            if (applied == 0 && failed == 0 && deadLettered == 0) return;

            Console.WriteLine($"Pending grants: {applied} applied, {failed} retried, {deadLettered} dead-lettered this cycle.");
        }
    }
}
```

- [ ] **Step 1b: tell a live session its retry landed (added 2026-09-19,
owner's brainstorm gate)**

Without this, a player who is online when a delayed grant applies sees
nothing until their next relogin — the drain worker writes straight to the
database with no signal to a live `TickStatePayload`. Fixed by reusing two
mechanisms that already exist, rather than adding a new notification type
or a new wire field:

- **Gold:** `ChestSaleGoldQueue` / `ChestSaleGoldNotification { PlayerId,
  GoldGained }` already exist for exactly this shape — "the database row is
  already correct (a chest sale wrote it directly), the live session's
  *displayed* total is what's behind." Its consumer in `SimulationEngine`
  calls `AddGold` on the payload WITHOUT touching `RedisPendingGoldDelta`,
  which is precisely correct here too: `PendingGrantOutbox` already wrote
  `CommodityRecords["gold"]` directly, so only the display needs to catch up,
  never a second bank of the same amount.
- **Materials and equipment:** `PlayerSessionRegistry.EnqueueCommandResult`
  already exists and is exactly what PR #10 (task 23) wired every screen's
  cache invalidation through — enqueuing a `Success` result for the player
  causes `processCommandResults` client-side to invalidate every REST cache
  globally, the same as any other successful command. No new queue, no new
  wire field: the client refetches its owned-items/materials view on its
  own.

`PendingGrantDrainEngine` needs a `PlayerSessionRegistry` reference (new
constructor parameter, same pattern `CombatLootEngine` already takes) to
call `IsPlayerOnline` and both queues above. Add this in
`DrainOneCycleAsync`, right after a row is successfully applied and removed
(inside the `try`, after `Interlocked.Increment(ref _applied)`):

```csharp
                    if (_playerSessionRegistry.IsPlayerOnline(row.PlayerId))
                    {
                        if (row.PayloadKind == PendingGrantPayloadKind.CommodityDeltas)
                        {
                            var deltas = JsonSerializer.Deserialize<Dictionary<string, long>>(row.PayloadJson)!;
                            if (deltas.TryGetValue("gold", out long goldGained) && goldGained > 0L)
                            {
                                _playerSessionRegistry.ChestSaleGoldQueue.Enqueue(
                                    new ChestSaleGoldNotification { PlayerId = row.PlayerId, GoldGained = goldGained });
                            }
                        }

                        _playerSessionRegistry.EnqueueCommandResult(
                            row.PlayerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
                    }
```

(Deserializing `PayloadJson` a second time here, after `TryApplyOneAsync`
already did once internally, is a small duplicated cost accepted for
keeping `TryApplyOneAsync`'s signature simple — it returns only `bool`, on
purpose, since Task 1's own tests call it directly without a
`PlayerSessionRegistry` in hand. Revisit only if this becomes a measured
hot path, which a near-zero-steady-state outbox should never be.)

**Test:** extend Task 4 Step 4's test list with **7. A live session sees a
retried gold grant without relogging in** — register the player as online
(`PlayerSessionRegistry.RegisterPlayer`) before running the drain cycle in
test 1 (offline production, end-to-end), and additionally assert
`ChestSaleGoldQueue` and `CommandResultQueue` each received exactly one
entry for that player. A parallel assertion for materials-only (no gold)
should confirm `CommandResultQueue` still gets an entry with no
`ChestSaleGoldQueue` entry.

- [ ] **Step 2: `Program.cs`**

Beside `var combatLootEngine = new CombatLootEngine(serviceProvider,
playerRegistry);` (line 473):

```csharp
var pendingGrantDrainEngine = new PendingGrantDrainEngine(serviceProvider, playerRegistry);
```

Beside `combatLootEngine.StartCron();` (line 566):

```csharp
pendingGrantDrainEngine.StartCron();
```

- [ ] **Step 3: `CronWorkerGuardTests.cs`**

Add to `KnownCronEngines`:

```csharp
                ["PendingGrantDrainEngine"] = "drains pending_grants - the retry half of the loot/gathering/offline-production outbox",
```

Run `EveryCronEngineIsAccountedFor` and `EveryCronEngineCatchesSomething`
(no code change needed in the test file beyond the dictionary entry — these
are the mechanical scans) to confirm this worker is picked up correctly.

- [ ] **Step 4: tests, `PendingGrantDrainEngineTests.cs`**

The board's literal Done-when, run against the real polling worker for all
three source types:

1. **Offline production, end-to-end.** Poison a player (per Global
   Constraints) so `GrantVillagePassiveProductionAsync` fails, producing a
   `PendingGrants` row (Task 1's wiring). Fix the poison (add the missing
   `PlayerRecords` row — the *cause* of the DB failure is gone, matching
   "database is healthy again" rather than "this row can never succeed").
   Start `PendingGrantDrainEngine`. Poll `CommodityRecords` for up to 30
   seconds the same way `LootWorkerResilienceTests` polls
   `EquipmentInstances`. Assert the reward lands and the `PendingGrants` row
   is gone afterward.
2. **Gathering, end-to-end.** Same shape, via `DrainGatheringGrantsAsync`.
3. **Combat loot, end-to-end.** Same shape, via `CombatLootEngine`'s
   `DropRequestQueue`; assert both an equipment row and a commodity delta
   land when the seeded batch guarantees at least one of each.
4. **Backoff actually backs off.** Seed a row with `AttemptCount = 3` and a
   `NextAttemptAtEpochMs` in the future; run one drain cycle; assert it is
   NOT picked up (still there, `AttemptCount` unchanged). Then set
   `NextAttemptAtEpochMs` to the past and re-run; assert it IS picked up.
5. **Give-up is permanent and correct.** Seed a row whose payload can never
   succeed (e.g. `PayloadKind` = a garbage string, or a commodity delta for
   a `playerId` that will never exist) with `AttemptCount = MaxAttempts - 1`
   and an eligible `NextAttemptAtEpochMs`. Run one drain cycle. Assert
   `DeadLetteredAtEpochMs` is now set, `AttemptCount == MaxAttempts`, and a
   second drain cycle does NOT touch it again (it is excluded by the
   `WHERE "DeadLetteredAtEpochMs" IS NULL` clause).
6. **A budgeted cycle does not starve on a large backlog.** Seed
   `MaxGrantsPerDrainCycle + 20` eligible, immediately-applicable rows (valid
   players, simple commodity deltas). Run exactly ONE call to
   `DrainOneCycleAsync` (expose it `internal` + `InternalsVisibleTo`, or call
   through one iteration of `ExecuteAsync` bounded by a very short-lived
   `CancellationTokenSource`, matching however
   `GatheringGrantStarvationTests` — if it exists — already isolates one
   cycle of `CombatLootEngine`'s loop). Assert exactly
   `MaxGrantsPerDrainCycle` rows were removed and the rest remain, proving
   the budget is real rather than "processes everything, just slowly."

- [ ] **Step 5: run the full server suite once, including `CronWorkerGuardTests`**

- [ ] **Step 6: Commit**

```bash
git add server/FolkIdle.Server/Engine/PendingGrantDrainEngine.cs server/FolkIdle.Server/Program.cs server/FolkIdle.Server.Tests/CronWorkerGuardTests.cs server/FolkIdle.Server.Tests/PendingGrantDrainEngineTests.cs
git commit -m "feat(reliability): a background worker retries pending_grants until it lands or gives up"
```

---

## Not in this plan

- **An admin tool to inspect or manually replay dead-lettered rows.** The
  schema supports it (`DeadLetteredAtEpochMs`, `LastError`, `AttemptCount`
  are all there to read and reset by hand today), but building a UI/endpoint
  for it is separate work.
- **Closing the process-crash gap named in Global Constraints** (a crash
  between the original failed write and the enqueue call). The board's
  Done-when is specifically about a forced *database* failure, which this
  plan closes completely; a full at-least-once producer guarantee against a
  process kill is a materially larger design (persisting the grant's intent
  before every attempt, not just after a caught failure) and was not asked
  for here.
- **Re-tuning `ConnectionStringDefaults.DefaultMaxPoolSize`.** This plan adds
  a fifteenth `StartCron` consumer of the existing bounded pool; Task 4 notes
  this is worth a glance but does not change the constant.
- **Task 17/19 from the sibling plan** (the failure counter and the
  full-warehouse overflow report) are assumed already landed or landing
  first — this plan only extends their same catch block, it does not
  duplicate or revert their work.
