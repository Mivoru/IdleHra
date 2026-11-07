using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Lockstep validation 2026-11-06 (20 bots, 72 game hours, x20 / scale 1 /
    /// scale 4): 7 / 1 / 5 gathering grants, 3 / 6 / 5 skill tree commits and
    /// 2 / 1 / 10 inheritance purchases failed on a lost race. None of these may
    /// lose an operation or apply one twice.
    ///
    /// Every conflict here is real, not mocked: a second transaction holds a
    /// row the engine needs, and Postgres itself fails the engine's attempt -
    /// 40001 for the Serializable commands, 40P01 (a deadlock) for the
    /// ReadCommitted gathering grant, which cannot fail any other way.
    /// </summary>
    [Collection("Postgres collection")]
    public class NoLostOperationsTests
    {
        private const int BirchLogItemId = 267; // ContentRegistry.GetItemBaseId(267) == "birch_log"
        private const string BirchLog = "birch_log";

        private readonly PostgresTestFixture _fixture;

        public NoLostOperationsTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private async Task<long> CreatePlayerAsync(string prefix, Action<PlayerRecord>? setup = null)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"{prefix}_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = 1,
            };
            setup?.Invoke(player);
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        private async Task<int> BlockingWaitersAsync(int blockerPid)
        {
            await using var probe = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await probe.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE {0} = ANY(pg_blocking_pids(pid))",
                blockerPid).SingleAsync();
        }

        private async Task WaitUntilBlockedAsync(int blockerPid, string what)
        {
            for (int i = 0; i < 400; i++)
            {
                if (await BlockingWaitersAsync(blockerPid) > 0) return;
                await Task.Delay(25);
            }
            Assert.Fail($"the engine never queued behind the blocker ({what}) - the race was not set up");
        }

        /// <summary>
        /// Holds an update on <paramref name="playerId"/>'s PlayerRecords row,
        /// runs <paramref name="engineCall"/>, and commits once the engine's
        /// SELECT ... FOR UPDATE is waiting - the engine's Serializable attempt
        /// then fails with 40001 "could not serialize access due to concurrent
        /// update", as the checkpoint flush makes it fail in a run.
        /// </summary>
        private async Task<T> RaceOnThePlayerRowAsync<T>(long playerId, Func<Task<T>> engineCall)
        {
            await using var blocker = await _fixture.DbContextFactory.CreateDbContextAsync();
            await using var tx = await blocker.Database.BeginTransactionAsync();
            await blocker.Database.ExecuteSqlRawAsync(
                "UPDATE \"PlayerRecords\" SET \"CurrentLevel\" = \"CurrentLevel\" WHERE \"Id\" = {0}", playerId);
            int pid = await blocker.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

            Task<T> call = engineCall();
            await WaitUntilBlockedAsync(pid, "PlayerRecords");
            await tx.CommitAsync();
            return await call;
        }

        /// <summary>
        /// Deadlocks one gathering grant attempt against the shape of the
        /// checkpoint flush. The blocker holds the player's birch_log row; the
        /// grant deletes its outbox reservation (taking that row's lock) and
        /// then waits on birch_log; the blocker then asks for the reservation
        /// row. Neither can move, and Postgres aborts the grant with 40P01 -
        /// the blocker's deadlock_timeout is set high so the grant is the one
        /// that checks first and becomes the victim. The blocker's FOR UPDATE
        /// can only return once the grant's DELETE was rolled back, so reaching
        /// the commit below proves the conflict happened.
        /// </summary>
        private async Task<CombatLootEngine.GatheringGrantOutcome> DeadlockAGatheringGrantAsync(
            long playerId, Func<Task<CombatLootEngine.GatheringGrantOutcome>> engineCall)
        {
            await using var blocker = await _fixture.DbContextFactory.CreateDbContextAsync();
            await using var tx = await blocker.Database.BeginTransactionAsync();
            await blocker.Database.ExecuteSqlRawAsync("SET LOCAL deadlock_timeout = '10s'");
            await blocker.Database.ExecuteSqlRawAsync(
                "UPDATE \"CommodityRecords\" SET \"Quantity\" = \"Quantity\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1}",
                playerId, BirchLog);
            int pid = await blocker.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

            var call = engineCall();
            await WaitUntilBlockedAsync(pid, "CommodityRecords");

            long reservation = await blocker.Database.SqlQueryRaw<long>(
                "SELECT \"Id\" AS \"Value\" FROM pending_grants WHERE \"PlayerId\" = {0}", playerId).SingleAsync();
            await blocker.Database.SqlQueryRaw<long>(
                "SELECT \"Id\" AS \"Value\" FROM pending_grants WHERE \"Id\" = {0} FOR UPDATE", reservation).ToListAsync();

            await tx.CommitAsync();
            return await call;
        }

        private static List<KeyValuePair<(long PlayerId, int ItemId), CombatLootEngine.GatheringGrantTotal>> Logs(long playerId, int quantity)
            => new() { new((playerId, BirchLogItemId), new CombatLootEngine.GatheringGrantTotal { ActivityId = 7, Quantity = quantity }) };

        private async Task<long> CreateGathererAsync(string prefix, long logsHeld)
        {
            long playerId = await CreatePlayerAsync(prefix);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = BirchLog, Quantity = logsHeld });
            await db.SaveChangesAsync();
            return playerId;
        }

        private async Task<long> LogsOfAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == BirchLog)
                .SumAsync(c => c.Quantity);
        }

        private async Task<List<PendingGrant>> OutboxOfAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PendingGrants.AsNoTracking().Where(g => g.PlayerId == playerId).ToListAsync();
        }

        /// <summary>Drains the outbox until this player's rows are gone (other tests' rows may be ahead of them).</summary>
        private async Task DrainOutboxAsync(long playerId)
        {
            var drain = new PendingGrantDrainEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            for (int i = 0; i < 20 && (await OutboxOfAsync(playerId)).Any(); i++)
            {
                await drain.DrainOneCycleAsync();
            }
        }

        // ---- Gathering ----

        [Fact]
        public async Task AGatheringGrantThatLosesARace_IsRetried_AndCreditsOnce()
        {
            long playerId = await CreateGathererAsync("gathrace", 10);
            var registry = new PlayerSessionRegistry();
            var engine = new CombatLootEngine(_fixture.ServiceProvider, registry);

            var outcome = await DeadlockAGatheringGrantAsync(playerId,
                () => engine.GrantGatheredMaterialsAsync(playerId, Logs(playerId, 50)));

            Assert.Equal(CombatLootEngine.GatheringGrantOutcome.Applied, outcome);
            Assert.Equal(60L, await LogsOfAsync(playerId));
            Assert.Empty(await OutboxOfAsync(playerId));
            Assert.True(registry.OutboundLootDropQueue.TryDequeue(out var feed));
            Assert.Equal(50, feed.Quantity);
            Assert.False(registry.OutboundLootDropQueue.TryDequeue(out _));

            // Nothing is left for the drain to grant a second time.
            await new PendingGrantDrainEngine(_fixture.ServiceProvider, registry).DrainOneCycleAsync();
            Assert.Equal(60L, await LogsOfAsync(playerId));
        }

        [Fact]
        public async Task AGatheringGrantThatExhaustsItsRetries_StaysInTheOutbox_AndTheDrainCreditsItOnce()
        {
            long playerId = await CreateGathererAsync("gathexh", 10);
            var registry = new PlayerSessionRegistry();
            var engine = new CombatLootEngine(_fixture.ServiceProvider, registry) { GatheringGrantAttempts = 1 };

            var outcome = await DeadlockAGatheringGrantAsync(playerId,
                () => engine.GrantGatheredMaterialsAsync(playerId, Logs(playerId, 50)));

            Assert.Equal(CombatLootEngine.GatheringGrantOutcome.KeptInOutbox, outcome);
            Assert.Equal(10L, await LogsOfAsync(playerId));
            Assert.False(registry.OutboundLootDropQueue.TryDequeue(out _));
            var row = Assert.Single(await OutboxOfAsync(playerId));
            Assert.Equal(50L, JsonSerializer.Deserialize<Dictionary<string, long>>(row.PayloadJson)![BirchLog]);
            Assert.True(row.NextAttemptAtEpochMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "the failed grant was not handed to the drain");
            Assert.Contains("40P01", row.LastError);

            await DrainOutboxAsync(playerId);
            Assert.Equal(60L, await LogsOfAsync(playerId));
            Assert.Empty(await OutboxOfAsync(playerId));

            await new PendingGrantDrainEngine(_fixture.ServiceProvider, registry).DrainOneCycleAsync();
            Assert.Equal(60L, await LogsOfAsync(playerId));
        }

        [Fact]
        public async Task AReservationTheDrainAppliedFirst_IsNotGrantedAgainInline()
        {
            // The shape of a commit whose answer was lost: the grant is in the
            // database, and someone tries to apply it again.
            long playerId = await CreateGathererAsync("gathdup", 10);
            var deltas = new Dictionary<string, long> { [BirchLog] = 50L };
            long reservation;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                reservation = await PendingGrantOutbox.ReserveCommodityDeltasAsync(db, playerId, PendingGrantSourceType.Gathering, deltas, holdMs: 0);
            }
            await DrainOutboxAsync(playerId);
            Assert.Equal(60L, await LogsOfAsync(playerId));

            var engine = new CombatLootEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            Assert.False(await engine.ApplyReservedGatheringAsync(playerId, reservation, deltas));
            Assert.Equal(60L, await LogsOfAsync(playerId));
        }

        [Fact]
        public async Task AReservationGrantedInline_IsNotGrantedAgainByTheDrain()
        {
            long playerId = await CreateGathererAsync("gathonce", 10);
            var deltas = new Dictionary<string, long> { [BirchLog] = 50L };
            long reservation;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                reservation = await PendingGrantOutbox.ReserveCommodityDeltasAsync(db, playerId, PendingGrantSourceType.Gathering, deltas, holdMs: 0);
            }
            var engine = new CombatLootEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            Assert.True(await engine.ApplyReservedGatheringAsync(playerId, reservation, deltas));
            Assert.False(await engine.ApplyReservedGatheringAsync(playerId, reservation, deltas));

            await new PendingGrantDrainEngine(_fixture.ServiceProvider, new PlayerSessionRegistry()).DrainOneCycleAsync();
            Assert.Equal(60L, await LogsOfAsync(playerId));
            Assert.Empty(await OutboxOfAsync(playerId));
        }

        // ---- Skill tree ----

        private static byte[] OneWarRank(out int cost)
        {
            foreach (var node in WarTreeRegistry.All())
            {
                var target = new byte[WarTreeRegistry.Slots];
                target[node.Id] = 1;
                if (WarTreeRegistry.PlanReason(new byte[WarTreeRegistry.Slots], target, 1000, out cost) == null) return target;
            }
            throw new InvalidOperationException("no War node can be bought from an empty tree");
        }

        private static byte[] OneHearthRank(int available, out int cost)
        {
            foreach (var node in HearthTreeRegistry.All())
            {
                var target = new byte[WarTreeRegistry.Slots];
                target[node.Id] = 1;
                if (HearthTreeRegistry.PlanReason(new byte[WarTreeRegistry.Slots], target, available, out cost) == null) return target;
            }
            throw new InvalidOperationException("no Hearth node can be bought from an empty half");
        }

        private async Task<(int Points, List<PlayerSkillTreeNode> Rows)> TreeOfAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            int points = await db.PlayerRecords.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.AvailableSkillPoints).SingleAsync();
            var rows = await db.PlayerSkillTreeNodes.AsNoTracking().Where(r => r.PlayerId == playerId).ToListAsync();
            return (points, rows);
        }

        [Fact]
        public async Task ASkillTreeCommitThatLosesARace_IsRetried_AndSpendsOnce()
        {
            var target = OneWarRank(out int cost);
            long playerId = await CreatePlayerAsync("treerace", p => p.AvailableSkillPoints = 100);
            var registry = new PlayerSessionRegistry();
            var engine = new SkillTreeEngine(_fixture.ServiceProvider, registry);

            var result = await RaceOnThePlayerRowAsync(playerId, () => engine.CommitAsync(playerId, target));

            Assert.Null(result.Reason);
            Assert.Equal(cost, result.Spent);
            var (points, rows) = await TreeOfAsync(playerId);
            Assert.Equal(100 - cost, points);
            var row = Assert.Single(rows);
            Assert.Equal(1, row.Level);
            Assert.True(registry.SkillTreeSyncQueue.TryDequeue(out var sync));
            Assert.Equal(100 - cost, sync.RemainingSkillPoints);
            Assert.False(registry.SkillTreeSyncQueue.TryDequeue(out _));
        }

        [Fact]
        public async Task ASkillTreeCommitThatExhaustsItsRetries_SaysBusy_AndSpendsNothing()
        {
            var target = OneWarRank(out _);
            long playerId = await CreatePlayerAsync("treeexh", p => p.AvailableSkillPoints = 100);
            var registry = new PlayerSessionRegistry();
            var engine = new SkillTreeEngine(_fixture.ServiceProvider, registry) { ConflictAttempts = 1 };

            var result = await RaceOnThePlayerRowAsync(playerId, () => engine.CommitAsync(playerId, target));

            Assert.Equal(SkillTreeEngine.BusyReason, result.Reason);
            var (points, rows) = await TreeOfAsync(playerId);
            Assert.Equal(100, points);
            Assert.Empty(rows);
            Assert.False(registry.SkillTreeSyncQueue.TryDequeue(out _));
        }

        [Fact]
        public async Task ASkillTreeCommitWithoutThePoints_IsRefusedAtOnce()
        {
            var target = OneWarRank(out _);
            long playerId = await CreatePlayerAsync("treeno", p => p.AvailableSkillPoints = 0);
            var engine = new SkillTreeEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());

            var result = await engine.CommitAsync(playerId, target);

            Assert.NotNull(result.Reason);
            Assert.NotEqual(SkillTreeEngine.BusyReason, result.Reason);
            Assert.Empty((await TreeOfAsync(playerId)).Rows);
        }

        [Fact]
        public async Task AHearthCommitThatLosesARace_IsRetried_AndBuysOnce()
        {
            long playerId = await CreatePlayerAsync("hearthrace", p =>
            {
                p.WoodcuttingMasteryLevel = 50; p.MiningMasteryLevel = 50;
                p.FishingMasteryLevel = 50; p.HerbalismMasteryLevel = 50;
            });
            int available = HearthPoints.Earned(50, 50, 50, 50);
            var target = OneHearthRank(available, out int cost);
            var registry = new PlayerSessionRegistry();
            var engine = new SkillTreeEngine(_fixture.ServiceProvider, registry);

            var result = await RaceOnThePlayerRowAsync(playerId, () => engine.CommitHearthAsync(playerId, target));

            Assert.Null(result.Reason);
            Assert.Equal(available - cost, result.AvailableSkillPoints);
            var row = Assert.Single((await TreeOfAsync(playerId)).Rows);
            Assert.Equal(1, row.Level);
            Assert.True(registry.SkillTreeSyncQueue.TryDequeue(out _));
            Assert.False(registry.SkillTreeSyncQueue.TryDequeue(out _));
        }

        // ---- Inheritance ----

        private async Task<(int Diamonds, int Level)> InheritanceOfAsync(long playerId, int statId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            int diamonds = await db.PlayerRecords.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.PremiumDiamonds).SingleAsync();
            int level = await db.PlayerInheritanceStats.AsNoTracking()
                .Where(r => r.PlayerId == playerId && r.StatId == statId).Select(r => r.Level).SingleOrDefaultAsync();
            return (diamonds, level);
        }

        private static byte[] Results(PlayerSessionRegistry registry) => registry.CommandResultQueue.Select(r => r.ResultCode).ToArray();

        [Fact]
        public async Task AnInheritancePurchaseThatLosesARace_IsRetried_AndChargesOnce()
        {
            long cost = InheritanceRegistry.GetUpgradeCost(0);
            long playerId = await CreatePlayerAsync("inhrace", p => p.PremiumDiamonds = 10_000);
            var registry = new PlayerSessionRegistry();
            var engine = new InheritanceEngine(_fixture.ServiceProvider, registry);

            await RaceOnThePlayerRowAsync(playerId, async () => { await engine.PurchaseLevelAsync(playerId, 0); return true; });

            Assert.Equal(new[] { (byte)CommandResultCode.Success }, Results(registry));
            var (diamonds, level) = await InheritanceOfAsync(playerId, 0);
            Assert.Equal(10_000 - cost, diamonds);
            Assert.Equal(1, level);
            Assert.True(registry.BillingSyncQueue.TryDequeue(out var billing));
            Assert.Equal(10_000 - cost, billing.PremiumDiamondsBalance);
            Assert.False(registry.BillingSyncQueue.TryDequeue(out _));
            Assert.True(registry.InheritanceSyncQueue.TryDequeue(out _));
            Assert.False(registry.InheritanceSyncQueue.TryDequeue(out _));
        }

        [Fact]
        public async Task AnInheritancePurchaseThatExhaustsItsRetries_SaysTryAgain_AndChargesNothing()
        {
            long playerId = await CreatePlayerAsync("inhexh", p => p.PremiumDiamonds = 10_000);
            var registry = new PlayerSessionRegistry();
            var engine = new InheritanceEngine(_fixture.ServiceProvider, registry) { ConflictAttempts = 1 };

            await RaceOnThePlayerRowAsync(playerId, async () => { await engine.PurchaseLevelAsync(playerId, 0); return true; });

            Assert.Equal(new[] { (byte)CommandResultCode.TransactionPending }, Results(registry));
            Assert.Equal((10_000, 0), await InheritanceOfAsync(playerId, 0));
            Assert.False(registry.BillingSyncQueue.TryDequeue(out _));
            Assert.False(registry.InheritanceSyncQueue.TryDequeue(out _));
        }

        [Fact]
        public async Task AnInheritancePurchaseWithoutTheDiamonds_IsRefusedAtOnce()
        {
            long playerId = await CreatePlayerAsync("inhno", p => p.PremiumDiamonds = 0);
            var registry = new PlayerSessionRegistry();
            var engine = new InheritanceEngine(_fixture.ServiceProvider, registry);

            await engine.PurchaseLevelAsync(playerId, 0);

            Assert.Equal(new[] { (byte)CommandResultCode.InsufficientMaterials }, Results(registry));
            Assert.Equal((0, 0), await InheritanceOfAsync(playerId, 0));
        }
    }
}
