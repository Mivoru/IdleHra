# Village Gold Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Village building upgrades and villager recruitment ("the feast") update the live session's `CurrentGold` the moment they debit `CommodityRecords["gold"]`, the same way breeding already does — so the header stops showing a stale balance until the next relogin.

**Architecture:** Both paths already build and enqueue a `PlayerSessionRegistry` notification struct that `SimulationEngine`'s tick drains to mutate the live `TickStatePayload` — building upgrades via `InfrastructureUpdateNotification`/`InfrastructureUpdateQueue`, already wired for every other field the upgrade changes. Villager recruitment currently enqueues nothing at all. This plan (1) adds a `GoldSpent` field to the existing infrastructure notification and consumes it exactly where `BirthNotification.GoldSpent` already is, and (2) gives recruitment its own minimal notification + queue, modeled on the existing `MentorshipUpdateNotification` (just a `PlayerId`) plus a `GoldSpent` field, consumed the same way.

**Tech Stack:** C# / .NET 8, xUnit + Testcontainers (real Postgres 16 per collection).

**Spec:** None — this is a direct mirror of an already-shipped, already-approved fix (breeding's `BirthNotification.GoldSpent`, `PlayerSessionRegistry.cs:228-242`, consumed at `SimulationEngine.cs:816`), confirmed against current code during a 2026-09-17 audit-verification pass (see `docs/TASK_BOARD.md`, task 16).

## Global Constraints

- **Never bank the pending delta for a gold row a DB write already credited/debited.** `CurrentGold` moves by the exact amount already applied to `CommodityRecords["gold"]`; `RedisPendingGoldDelta` is not touched by either path in this plan (CLAUDE.md, "Two gold paths, and mixing them pays the player twice").
- Mirror the breeding pattern exactly: the notification struct carries the gold amount, `SimulationEngine`'s drain loop applies `Math.Max(0L, currentPayload.CurrentGold - notif.GoldSpent)` and sets `currentPayload.IsDirty = true`. Do not invent a different mechanism.
- Every new/changed test seeds its own `PlayerRecord` and passes a **fresh** `new PlayerSessionRegistry()` to the engine under test (not the xUnit collection's shared `_fixture.PlayerRegistry`) — the shared registry's queues can carry stray items from other tests in the same collection, and `BreedingRoundOneTests.cs:116` already establishes this as the pattern for exactly this kind of gold-notification assertion.

---

### Task 1: Village building upgrades update the live session's gold

**Files:**
- Modify: `server/FolkIdle.Server/Engine/PlayerSessionRegistry.cs:284-313` (the `InfrastructureUpdateNotification` struct)
- Modify: `server/FolkIdle.Server/Domain/Progression/VillageManagementEngine.cs:447-470` (`ExecuteUpgradeBuildingAsync`, the gold-debit block and the notification build/enqueue that follows it)
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs:1436-1459` (the `InfrastructureUpdateQueue` drain loop)
- Test: `server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs` (new test, placed after the existing `Test_VillageUpgrade_QueuesUpgradeAndDeductsWoodAndStone` at line 990)

**Interfaces:**
- Consumes: nothing new.
- Produces: `InfrastructureUpdateNotification.GoldSpent` — Task 20 (the state-ownership-manifest work on the task board) and any future notification struct added to this file should follow the same "caller sets the derived field after the builder returns, before enqueueing" shape this task establishes.

- [ ] **Step 1: Write the failing test**

In `HardenedEngineIntegrationTests.cs`, directly after `Test_VillageUpgrade_QueuesUpgradeAndDeductsWoodAndStone` (ends at line 990), add:

```csharp
        [Fact]
        public async Task Test_VillageUpgrade_TellsTheLiveSessionWhatItCost()
        {
            const long testPlayerId = 950000108L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = testPlayerId,
                    BuildingId = VillageManagementEngine.LumberjackBuildingId,
                    CurrentLevel = 0
                });
                var tier0 = VillageManagementEngine.GetTierMaterials(0);
                db.CommodityRecords.AddRange(
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = tier0.Log, Quantity = 10000L },
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = tier0.Ore, Quantity = 10000L },
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 1_000_000L });
                await db.SaveChangesAsync();
            }

            var registry = new PlayerSessionRegistry();
            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, registry);
            await villageManagementEngine.ExecuteUpgradeBuildingAsync(testPlayerId, VillageManagementEngine.LumberjackBuildingId);

            long expectedCost = VillageManagementEngine.CalculateProductionUpgradeCost(0);

            Assert.True(registry.InfrastructureUpdateQueue.TryDequeue(out var notif));
            Assert.Equal(testPlayerId, notif.PlayerId);
            Assert.Equal(expectedCost, notif.GoldSpent);

            // And the row the notification describes really was debited by the
            // same amount - the tick moves the live balance, never the row again.
            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var gold = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "gold");
            Assert.Equal(1_000_000L - expectedCost, gold.Quantity);
        }

        [Fact]
        public async Task Test_VillageUpgrade_StructuralBuildingReportsZeroGoldSpent()
        {
            // Modul: structural buildings (Town Hall etc.) charge no gold at
            // all - GoldSpent must read 0 for them, not the non-structural
            // cost formula, or a structural upgrade would falsely debit the
            // live session's displayed balance for a row that was never
            // touched.
            const long testPlayerId = 950000109L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = testPlayerId,
                    BuildingId = VillageManagementEngine.TownHallBuildingId,
                    CurrentLevel = 0
                });
                var tier0 = VillageManagementEngine.GetTierMaterials(0);
                db.CommodityRecords.AddRange(
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = tier0.Log, Quantity = 10000L },
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = tier0.Ore, Quantity = 10000L },
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = tier0.RareLog, Quantity = 10000L });
                await db.SaveChangesAsync();
            }

            var registry = new PlayerSessionRegistry();
            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, registry);
            await villageManagementEngine.ExecuteUpgradeBuildingAsync(testPlayerId, VillageManagementEngine.TownHallBuildingId);

            Assert.True(registry.InfrastructureUpdateQueue.TryDequeue(out var notif));
            Assert.Equal(0L, notif.GoldSpent);
        }
```

Check `VillageManagementEngine.TownHallBuildingId` and the exact structural-building material seeding (`tierMats.RareLog`) against the current file before running — if Town Hall needs a different tier-material shape than this test assumes, adjust to match, not to a guess.

- [ ] **Step 2: Run the tests to verify they fail**

Stop the running server first (CLAUDE.md's stale-build rule), then:

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_VillageUpgrade_TellsTheLiveSessionWhatItCost|FullyQualifiedName~Test_VillageUpgrade_StructuralBuildingReportsZeroGoldSpent"`

Expected: `Test_VillageUpgrade_TellsTheLiveSessionWhatItCost` FAILS — `notif.GoldSpent` does not exist yet (compile error) or, once Step 3 adds the field with a default, fails on `Assert.Equal(expectedCost, notif.GoldSpent)` since it's never set to anything but 0. `Test_VillageUpgrade_StructuralBuildingReportsZeroGoldSpent` compiles the same way and passes vacuously once the field exists with its 0 default (structural buildings never touch it) — that one is a **characterization test**, not expected to go red once the field exists; it exists to pin the zero case once Task 1 lands, not to prove a defect.

- [ ] **Step 3: Implement — add the field**

In `PlayerSessionRegistry.cs`, add to `InfrastructureUpdateNotification` (after `PendingUpgradeCompletesAtEpoch`, before the closing brace at line 313):

```csharp
        /// <summary>
        /// The gold BuildingId's upgrade already debited from
        /// CommodityRecords["gold"], or 0 for a structural building (which
        /// pays in materials only). The tick moves the session's CurrentGold
        /// by this and NOT the pending delta - the row is already right, and
        /// banking it again would charge twice (CLAUDE.md, "two gold paths").
        /// Mirrors BirthNotification.GoldSpent exactly.
        /// </summary>
        public long GoldSpent;
```

- [ ] **Step 4: Implement — populate it at the debit site**

In `VillageManagementEngine.cs`, the block around lines 447-470 currently reads (confirm against the live file before editing — line numbers may have drifted):

```csharp
                if (!isStructuralBuilding)
                {
                    long goldCost = CalculateUpgradeCost(infrastructure.CurrentLevel);
                    var goldRecord = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (goldRecord == null || goldRecord.Quantity < goldCost)
                    {
                        await transaction.RollbackAsync();
                        Reject(playerId, FolkIdle.Server.Network.CommandResultCode.InsufficientGold);
                        return;
                    }
                    goldRecord.Quantity -= goldCost;
                }

                infrastructure.UpgradeTargetLevel = infrastructure.CurrentLevel + 1;
                infrastructure.UpgradeCompletesAtEpoch = nowEpoch + CalculateUpgradeDurationSeconds(infrastructure.CurrentLevel);

                await db.SaveChangesAsync();
                var notification = await BuildInfrastructureNotificationAsync(db, playerId);
                await transaction.CommitAsync();

                _playerRegistry.InfrastructureUpdateQueue.Enqueue(notification);
```

Change to (hoist a `goldSpent` local before the `if`, set it inside, apply it to the notification before enqueueing):

```csharp
                long goldSpent = 0L;
                if (!isStructuralBuilding)
                {
                    long goldCost = CalculateUpgradeCost(infrastructure.CurrentLevel);
                    var goldRecord = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (goldRecord == null || goldRecord.Quantity < goldCost)
                    {
                        await transaction.RollbackAsync();
                        Reject(playerId, FolkIdle.Server.Network.CommandResultCode.InsufficientGold);
                        return;
                    }
                    goldRecord.Quantity -= goldCost;
                    goldSpent = goldCost;
                }

                infrastructure.UpgradeTargetLevel = infrastructure.CurrentLevel + 1;
                infrastructure.UpgradeCompletesAtEpoch = nowEpoch + CalculateUpgradeDurationSeconds(infrastructure.CurrentLevel);

                await db.SaveChangesAsync();
                var notification = await BuildInfrastructureNotificationAsync(db, playerId);
                notification.GoldSpent = goldSpent;
                await transaction.CommitAsync();

                _playerRegistry.InfrastructureUpdateQueue.Enqueue(notification);
```

**Do not touch** `ExecuteEvictVillagerAsync` (the other call site of `BuildInfrastructureNotificationAsync`, around line 563-602) — it never charges gold, and the struct's `GoldSpent` default of `0L` is already correct there with no change needed.

- [ ] **Step 5: Implement — consume it in the tick**

In `SimulationEngine.cs`, inside the `InfrastructureUpdateQueue` drain loop (lines 1436-1459), add the gold line alongside the other field assignments — immediately after `currentPayload.PendingUpgradeCompletesAtEpoch = updateNotif.PendingUpgradeCompletesAtEpoch;` and before `currentPayload.IsDirty = true;`:

```csharp
                        currentPayload.PendingUpgradeCompletesAtEpoch = updateNotif.PendingUpgradeCompletesAtEpoch;
                        // Modul: the row is already debited (VillageManagementEngine),
                        // so the live balance follows it and the pending delta is left
                        // alone - the same reasoning as BirthNotification.GoldSpent.
                        currentPayload.CurrentGold = Math.Max(0L, currentPayload.CurrentGold - updateNotif.GoldSpent);
                        currentPayload.IsDirty = true;
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_VillageUpgrade"`

Expected: all PASS, including the two new tests and the three pre-existing `Test_VillageUpgrade_*` tests (unaffected — they never asserted on `GoldSpent`).

- [ ] **Step 7: Commit**

```bash
git add server/FolkIdle.Server/Engine/PlayerSessionRegistry.cs server/FolkIdle.Server/Domain/Progression/VillageManagementEngine.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs
git commit -m "fix(village): a building upgrade tells the live session what it cost"
```

---

### Task 2: Villager recruitment ("the feast") updates the live session's gold

**Files:**
- Modify: `server/FolkIdle.Server/Engine/PlayerSessionRegistry.cs` (new struct + queue, placed after `MentorshipUpdateNotification` at line 315-318)
- Modify: `server/FolkIdle.Server/Engine/VillageArrivalEngine.cs:103-128` (`RecruitAsync`'s return shape)
- Modify: `server/FolkIdle.Server/Domain/Progression/VillageManagementEngine.cs:195-236` (`ExecuteRecruitVillagerAsync`, its one caller)
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs` (a new drain loop, placed beside the `InfrastructureUpdateQueue` one from Task 1)
- Test: `server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs` (new tests; check first whether a `Test_RecruitVillager_*` test already exists nearby to place these next to — none was found during this plan's research, so add a new `[Fact]` pair near the Village upgrade tests from Task 1)

**Interfaces:**
- Consumes: nothing from Task 1 — independent notification, independent queue. Order between Task 1 and Task 2 does not matter; either can land first.
- Produces: `RecruitAsync` returns `Task<(string? Refusal, long GoldSpent)>` instead of `Task<string?>` — this is a breaking signature change with exactly one caller (`ExecuteRecruitVillagerAsync`), confirmed by grep during this plan's research. If a second caller has appeared since, update it the same way rather than adding an overload.

- [ ] **Step 1: Write the failing test**

In `HardenedEngineIntegrationTests.cs`, near the Village upgrade tests from Task 1, add:

```csharp
        [Fact]
        public async Task Test_RecruitVillager_TellsTheLiveSessionWhatItCost()
        {
            const long testPlayerId = 950000110L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = testPlayerId,
                    BuildingId = VillageManagementEngine.InnBuildingId,
                    CurrentLevel = 5
                });
                db.CommodityRecords.Add(
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 1_000_000L });
                await db.SaveChangesAsync();
            }

            var registry = new PlayerSessionRegistry();
            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, registry);
            await villageManagementEngine.ExecuteRecruitVillagerAsync(testPlayerId);

            long expectedCost = FolkIdle.Server.Engine.VillagerArrivalRules.RecruitCostGold(0);

            Assert.True(registry.VillagerRecruitmentUpdateQueue.TryDequeue(out var notif));
            Assert.Equal(testPlayerId, notif.PlayerId);
            Assert.Equal(expectedCost, notif.GoldSpent);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var gold = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "gold");
            Assert.Equal(1_000_000L - expectedCost, gold.Quantity);
        }

        [Fact]
        public async Task Test_RecruitVillager_RefusalEnqueuesNoNotification()
        {
            // Modul: a refused recruit (too poor, village full) must not tell
            // the live session it spent anything - RecruitAsync's tuple return
            // makes "did it commit" and "what did it cost" one fact instead of
            // two, so a refusal cannot accidentally produce a GoldSpent notif.
            const long testPlayerId = 950000111L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = testPlayerId,
                    BuildingId = VillageManagementEngine.InnBuildingId,
                    CurrentLevel = 5
                });
                db.CommodityRecords.Add(
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 0L });
                await db.SaveChangesAsync();
            }

            var registry = new PlayerSessionRegistry();
            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, registry);
            await villageManagementEngine.ExecuteRecruitVillagerAsync(testPlayerId);

            Assert.False(registry.VillagerRecruitmentUpdateQueue.TryDequeue(out _));
        }
```

Check `VillageManagementEngine.InnBuildingId` and `VillagerArrivalRules.RecruitCostGold`/`PopulationCapFor` against the live file — Inn level 5 must be enough population cap and a low enough recruitment count (0 this-season) that the first test's recruit is actually accepted, and the second test's zero gold must actually be below `RecruitCostGold(0)` (it will be — the base cost is never zero, confirmed by `VillagerArrivalRules.cs:144` returning `RecruitBaseGold` for the zeroth recruit).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_RecruitVillager"`

Expected: compile failure — `PlayerSessionRegistry.VillagerRecruitmentUpdateQueue` does not exist yet. That compile failure is this step's RED; once Steps 3-5 exist the tests must be re-run to confirm they pass for the right reason.

- [ ] **Step 3: Implement — the new notification and queue**

In `PlayerSessionRegistry.cs`, add directly after `MentorshipUpdateNotification` (line 315-318):

```csharp
    /// <summary>
    /// The gold ExecuteRecruitVillagerAsync already debited from
    /// CommodityRecords["gold"] for "the feast" - paying gold for an
    /// immediate villager rather than waiting for the Inn's free arrival
    /// clock. Mirrors BirthNotification.GoldSpent; this path previously
    /// enqueued nothing at all, so the live session never learned it had
    /// spent anything until relogin.
    /// </summary>
    public struct VillagerRecruitmentNotification
    {
        public long PlayerId;
        public long GoldSpent;
    }
```

And wherever `ConcurrentQueue<MentorshipUpdateNotification> MentorshipUpdateQueue` is declared (search for that exact line — it is near the other `ConcurrentQueue<...>` properties this file already has, e.g. beside `InfrastructureUpdateQueue`'s declaration at line 462), add immediately after it:

```csharp
        public ConcurrentQueue<VillagerRecruitmentNotification> VillagerRecruitmentUpdateQueue { get; } = new();
```

- [ ] **Step 4: Implement — `RecruitAsync` returns the cost, not just the refusal**

In `VillageArrivalEngine.cs`, change the signature and body (lines 103-128) from:

```csharp
        public static async Task<string?> RecruitAsync(
            FolkIdleDbContext db, PlayerRecord player, int innLevel, long nowEpoch)
        {
            int population = await db.VillageNewcomers.CountAsync(v => v.PlayerId == player.Id);

            var gold = await db.CommodityRecords
                .FirstOrDefaultAsync(c => c.PlayerId == player.Id && c.ItemId == "gold");
            long held = gold?.Quantity ?? 0L;

            string? refusal = VillagerArrivalRules.RecruitBlockedReason(
                innLevel, population, held, player.VillagerRecruitmentsThisSeason);

            if (refusal != null) return refusal;

            // Read the price back from the same function that just approved it
            // rather than recomputing it from a different argument list.
            long cost = VillagerArrivalRules.RecruitCostGold(player.VillagerRecruitmentsThisSeason);
            gold!.Quantity -= cost;
            player.VillagerRecruitmentsThisSeason++;

            byte[] races = await UnlockedRacesAsync(db, player.Id);
            db.VillageNewcomers.Add(Roll(player.Id, innLevel, nowEpoch, races));

            await db.SaveChangesAsync();
            return null;
        }
```

to:

```csharp
        public static async Task<(string? Refusal, long GoldSpent)> RecruitAsync(
            FolkIdleDbContext db, PlayerRecord player, int innLevel, long nowEpoch)
        {
            int population = await db.VillageNewcomers.CountAsync(v => v.PlayerId == player.Id);

            var gold = await db.CommodityRecords
                .FirstOrDefaultAsync(c => c.PlayerId == player.Id && c.ItemId == "gold");
            long held = gold?.Quantity ?? 0L;

            string? refusal = VillagerArrivalRules.RecruitBlockedReason(
                innLevel, population, held, player.VillagerRecruitmentsThisSeason);

            if (refusal != null) return (refusal, 0L);

            // Read the price back from the same function that just approved it
            // rather than recomputing it from a different argument list.
            long cost = VillagerArrivalRules.RecruitCostGold(player.VillagerRecruitmentsThisSeason);
            gold!.Quantity -= cost;
            player.VillagerRecruitmentsThisSeason++;

            byte[] races = await UnlockedRacesAsync(db, player.Id);
            db.VillageNewcomers.Add(Roll(player.Id, innLevel, nowEpoch, races));

            await db.SaveChangesAsync();
            return (null, cost);
        }
```

- [ ] **Step 5: Implement — the caller enqueues the new notification**

In `VillageManagementEngine.cs`, `ExecuteRecruitVillagerAsync` (lines 195-236), change:

```csharp
                string? refusal = await Engine.VillageArrivalEngine.RecruitAsync(
                    db, player, innLevel, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                if (refusal != null)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 70, Value2 = 1, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                await transaction.CommitAsync();
```

to:

```csharp
                (string? refusal, long goldSpent) = await Engine.VillageArrivalEngine.RecruitAsync(
                    db, player, innLevel, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                if (refusal != null)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 70, Value2 = 1, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                await transaction.CommitAsync();

                _playerRegistry.VillagerRecruitmentUpdateQueue.Enqueue(new Engine.VillagerRecruitmentNotification
                {
                    PlayerId = playerId,
                    GoldSpent = goldSpent
                });
```

(Match the actual namespace `VillagerRecruitmentNotification` resolves under — it's declared in `PlayerSessionRegistry.cs`'s `FolkIdle.Server.Engine` namespace per Step 3; drop the `Engine.` prefix if `VillageManagementEngine.cs` already has a `using FolkIdle.Server.Engine;` that makes it unnecessary, matching however `BirthNotification`/`InfrastructureUpdateNotification` are referenced elsewhere in this same file.)

- [ ] **Step 6: Implement — consume it in the tick**

In `SimulationEngine.cs`, add a new drain loop beside the `InfrastructureUpdateQueue` one from Task 1 (after its closing brace, before `MentorshipUpdateQueue`'s loop):

```csharp
                while (_playerRegistry.VillagerRecruitmentUpdateQueue.TryDequeue(out var recruitmentNotif))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, recruitmentNotif.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        // Modul: the row is already debited (VillageArrivalEngine.RecruitAsync),
                        // so the live balance follows it and the pending delta is left
                        // alone - the same reasoning as BirthNotification.GoldSpent.
                        currentPayload.CurrentGold = Math.Max(0L, currentPayload.CurrentGold - recruitmentNotif.GoldSpent);
                        currentPayload.IsDirty = true;
                    }
                }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_RecruitVillager"`

Expected: both PASS.

- [ ] **Step 8: Run the full server suite once**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj`

Expected: all PASS — `RecruitAsync`'s signature change is a breaking change to a `public static` method; this confirms no other caller was missed by the Step 4/5 grep-and-fix.

- [ ] **Step 9: Commit**

```bash
git add server/FolkIdle.Server/Engine/PlayerSessionRegistry.cs server/FolkIdle.Server/Engine/VillageArrivalEngine.cs server/FolkIdle.Server/Domain/Progression/VillageManagementEngine.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs
git commit -m "fix(village): the feast tells the live session what it cost"
```

---

## Not in this plan

`RedisPendingGoldDelta` is untouched by design — both paths in this plan already write directly to `CommodityRecords["gold"]`, so per CLAUDE.md's "two gold paths" rule they must move `CurrentGold` only, exactly like breeding. A future audit of `AutoRerollRunner`/Forge and any other gold-debiting command not yet confirmed to sync live session state is out of scope here — this plan closes exactly the two paths named in `docs/TASK_BOARD.md` task 16 and `docs/architecture/NEXT_STEPS_BACKLOG.md`'s "STILL OPEN elsewhere" note.
