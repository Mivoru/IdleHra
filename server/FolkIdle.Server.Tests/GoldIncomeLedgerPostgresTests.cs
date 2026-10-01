using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 79, phase 2 against a real Postgres: each income writer, each
    /// material-flow writer, and the checkpoint carrying the tick's tally -
    /// once, through a failed flush, and across a relogin.
    /// </summary>
    /// <remarks>
    /// The fixture registers no RedisSessionCache, so these run the path where
    /// the checkpoint banks the gold too. With Redis up the gold is banked by
    /// write-behind instead, and the tally is untouched by that - it never
    /// enters a frame - so the counting here is the same on both.
    /// </remarks>
    [Collection("Postgres collection")]
    public class GoldIncomeLedgerPostgresTests
    {
        private readonly PostgresTestFixture _fixture;

        public GoldIncomeLedgerPostgresTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task<PlayerRecord> CreatePlayerAsync(bool withCharacter = false)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"income_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = 10,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = 1_000 });
            if (withCharacter)
            {
                db.CharacterRecords.Add(new CharacterRecord { Id = Guid.NewGuid(), PlayerId = player.Id, AgePhase = 1, SlotIndex = 0 });
            }
            await db.SaveChangesAsync();
            return player;
        }

        private async Task<Dictionary<GoldIncomeSource, long>> IncomeAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await db.GoldIncomeDaily.AsNoTracking().Where(g => g.PlayerId == playerId).ToListAsync();
            return rows.GroupBy(r => (GoldIncomeSource)r.Source).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        }

        private async Task<long> FlowAsync(long playerId, string itemId, MaterialFlowDirection direction)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.MaterialFlowDaily.AsNoTracking()
                .Where(m => m.PlayerId == playerId && m.ItemId == itemId && m.Direction == (short)direction)
                .SumAsync(m => m.Amount);
        }

        private async Task<long> GoldAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CommodityRecords.AsNoTracking().Where(c => c.PlayerId == playerId && c.ItemId == "gold").SumAsync(c => c.Quantity);
        }

        // ------------------------------------------------------------------
        // The ledger's own writes
        // ------------------------------------------------------------------

        [Fact]
        public async Task IncomeAddsUpBySource_AndARollbackLeavesNoTrace()
        {
            var player = await CreatePlayerAsync();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await GoldLedger.RecordIncomeAsync(db, player.Id, GoldIncomeSource.Market, 400);
                await GoldLedger.RecordIncomeAsync(db, player.Id, GoldIncomeSource.Market, 100);
                await GoldLedger.RecordIncomeAsync(db, player.Id, GoldIncomeSource.Mail, 0); // nothing
                await tx.CommitAsync();
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await GoldLedger.RecordIncomeAsync(db, player.Id, GoldIncomeSource.Delve, 999);
                await MaterialLedger.RecordAsync(db, player.Id, MaterialFlowDirection.Gathered, "birch_log", 5);
                await tx.RollbackAsync();
            }

            var income = await IncomeAsync(player.Id);
            Assert.Single(income);
            Assert.Equal(500, income[GoldIncomeSource.Market]);
            Assert.Equal(0, await FlowAsync(player.Id, "birch_log", MaterialFlowDirection.Gathered));
        }

        [Fact]
        public async Task MaterialFlow_AddsUpByItemAndDirection_AndNeverRecordsGold()
        {
            var player = await CreatePlayerAsync();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await MaterialLedger.RecordManyAsync(db, player.Id, MaterialFlowDirection.Gathered, new[]
                {
                    new KeyValuePair<string, long>("birch_log", 3),
                    new KeyValuePair<string, long>("birch_log", 4), // grouped, not refused by ON CONFLICT
                    new KeyValuePair<string, long>("copper_ore", 2),
                    new KeyValuePair<string, long>("gold", 50),     // gold has its own ledger
                    new KeyValuePair<string, long>("tin_ore", -1),  // never a negative flow
                });
                await MaterialLedger.RecordAsync(db, player.Id, MaterialFlowDirection.Spent, "birch_log", 6);
            }

            Assert.Equal(7, await FlowAsync(player.Id, "birch_log", MaterialFlowDirection.Gathered));
            Assert.Equal(6, await FlowAsync(player.Id, "birch_log", MaterialFlowDirection.Spent));
            Assert.Equal(2, await FlowAsync(player.Id, "copper_ore", MaterialFlowDirection.Gathered));
            Assert.Equal(0, await FlowAsync(player.Id, "gold", MaterialFlowDirection.Gathered));
            Assert.Equal(0, await FlowAsync(player.Id, "tin_ore", MaterialFlowDirection.Gathered));
        }

        // ------------------------------------------------------------------
        // The tick's income: tallied, then written by the checkpoint
        // ------------------------------------------------------------------

        private static TickStatePayload Live(long playerId, long epoch, long gold)
        {
            var state = new TickStatePayload
            {
                PlayerId = playerId,
                LogicEpochCounter = epoch,
                InventorySpaceRemaining = 20,
                STR = 50, DEX = 50, CON = 50, LCK = 25,
            };
            state.SetGold(gold);
            return state;
        }

        // What the kill, the Town Hall, auto-salvage and the offline catch-up
        // each do: the balance, the owed delta, and the tally, together.
        private static void Earn(ref TickStatePayload state, GoldIncomeSource source, long coins)
        {
            state.AddGold(coins);
            state.RedisPendingGoldDelta += coins;
            GoldLedger.TallyIncome(ref state, source, coins);
        }

        [Fact]
        public async Task TheCheckpointWritesTheTallyOnce_AndARelogin_CountsNothingAgain()
        {
            var player = await CreatePlayerAsync(withCharacter: true);
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            try
            {
                var holder = new[] { await manager.LoadPlayerState(player.Id) };
                Earn(ref holder[0], GoldIncomeSource.Combat, 70);
                Earn(ref holder[0], GoldIncomeSource.TownHall, 5);
                Earn(ref holder[0], GoldIncomeSource.AutoSalvage, 25);

                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                Assert.True(holder[0].PendingGoldIncome.IsEmpty); // it went with the job
                await manager.WhenWriterIdleAsync();
                manager.ApplyPendingAcks(ref holder[0]);

                // A second checkpoint with nothing new earned adds nothing.
                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                await manager.WhenWriterIdleAsync();
                manager.ApplyPendingAcks(ref holder[0]);

                var income = await IncomeAsync(player.Id);
                Assert.Equal(70, income[GoldIncomeSource.Combat]);
                Assert.Equal(5, income[GoldIncomeSource.TownHall]);
                Assert.Equal(25, income[GoldIncomeSource.AutoSalvage]);
                Assert.Equal(1_100, await GoldAsync(player.Id)); // the gold itself banked once, unchanged

                // A relogin: a new session loads a fresh payload and login
                // checkpoints it at once (FlushStateAndAdvance). Nothing it
                // loaded is income, and the ledger is still there.
                var relogin = new StateCheckpointManager(_fixture.ServiceProvider);
                var fresh = await relogin.LoadPlayerState(player.Id);
                Assert.True(fresh.PendingGoldIncome.IsEmpty);
                Earn(ref fresh, GoldIncomeSource.CombatAway, 40); // the offline catch-up's kills
                Assert.True(relogin.FlushStateAndAdvance(ref fresh));
                Assert.True(fresh.PendingGoldIncome.IsEmpty);

                income = await IncomeAsync(player.Id);
                Assert.Equal(70, income[GoldIncomeSource.Combat]);
                Assert.Equal(40, income[GoldIncomeSource.CombatAway]);
                Assert.Equal(1_140, await GoldAsync(player.Id));
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        [Fact]
        public async Task AFailedFlushHandsTheTallyBack_AndTheRetryCountsItOnce()
        {
            var player = await CreatePlayerAsync(withCharacter: true);
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            bool fail = true;
            manager.FlushOverrideForTests = snapshot => fail ? Task.FromResult(false) : manager.FlushState(snapshot);
            try
            {
                var holder = new[] { await manager.LoadPlayerState(player.Id) };
                Earn(ref holder[0], GoldIncomeSource.Combat, 60);

                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                await manager.WhenWriterIdleAsync();
                manager.ApplyPendingAcks(ref holder[0]);

                Assert.Equal(60, holder[0].PendingGoldIncome.Combat);
                Assert.Empty(await IncomeAsync(player.Id));

                fail = false;
                Earn(ref holder[0], GoldIncomeSource.Combat, 15); // earned meanwhile
                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                await manager.WhenWriterIdleAsync();
                manager.ApplyPendingAcks(ref holder[0]);

                Assert.True(holder[0].PendingGoldIncome.IsEmpty);
                Assert.Equal(75, (await IncomeAsync(player.Id))[GoldIncomeSource.Combat]);
                Assert.Equal(1_075, await GoldAsync(player.Id));
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        // ------------------------------------------------------------------
        // The engines that credit the row themselves
        // ------------------------------------------------------------------

        [Fact]
        public async Task AChestSale_IsIncome_AndItsMaterialIsSold_ABinIsDiscarded()
        {
            var player = await CreatePlayerAsync();
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                seed.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "birch_log", Quantity = 100 });
                await seed.SaveChangesAsync();
            }

            long gained;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var sale = await VillageChestEngine.RemoveMaterialAsync(db, player.Id, "birch_log", 30, sell: true);
                Assert.Equal(VillageChestEngine.ChestActionResult.Success, sale.Result);
                gained = sale.GoldGained;
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var bin = await VillageChestEngine.RemoveMaterialAsync(db, player.Id, "birch_log", 10, sell: false);
                Assert.Equal(VillageChestEngine.ChestActionResult.Success, bin.Result);
            }

            Assert.True(gained > 0, "birch_log should sell for something");
            Assert.Equal(gained, (await IncomeAsync(player.Id))[GoldIncomeSource.ChestSale]);
            Assert.Equal(30, await FlowAsync(player.Id, "birch_log", MaterialFlowDirection.Sold));
            Assert.Equal(10, await FlowAsync(player.Id, "birch_log", MaterialFlowDirection.Discarded));
        }

        [Fact]
        public async Task GoldItself_CannotBeSoldOrBinned()
        {
            var player = await CreatePlayerAsync();
            long before = await GoldAsync(player.Id);

            foreach (bool sell in new[] { true, false })
            {
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                var result = await VillageChestEngine.RemoveMaterialAsync(db, player.Id, "gold", 500, sell);
                Assert.Equal(VillageChestEngine.ChestActionResult.NotRemovable, result.Result);
                Assert.Equal(0, result.GoldGained);
            }

            Assert.Equal(before, await GoldAsync(player.Id));
        }

        [Fact]
        public async Task TheLoginReward_IsIncome_OncePerDay()
        {
            var player = await CreatePlayerAsync();
            var options = _fixture.ServiceProvider.GetRequiredService<RetryingDbContextOptions>();

            var first = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(options, player.PlayerGuid);
            var again = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(options, player.PlayerGuid);

            Assert.True(first.Granted);
            Assert.False(again.Granted);
            Assert.Equal(first.GoldGranted, (await IncomeAsync(player.Id))[GoldIncomeSource.LoginReward]);
        }

        [Fact]
        public async Task AClaimedMail_IsIncome()
        {
            var player = await CreatePlayerAsync();
            long mailId;
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var mail = new MailboxInstance { PlayerId = player.Id, GoldAttachment = 250, SenderName = "test", MessageText = "t", BaseItemId = string.Empty };
                seed.MailboxInstances.Add(mail);
                await seed.SaveChangesAsync();
                mailId = mail.Id;
            }

            var engine = new MailboxAndBankEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            await engine.CommitMailClaimAsync(player.Id, mailId, true);

            Assert.Equal(250, (await IncomeAsync(player.Id))[GoldIncomeSource.Mail]);
            Assert.Equal(1_250, await GoldAsync(player.Id));
        }

        // ------------------------------------------------------------------
        // Away: the Town Hall, the Warehouse cap, gathering
        // ------------------------------------------------------------------

        // Same arithmetic as OfflineVillageProductionOverflowTests: ten hours
        // at level 1 is 2,000 of each raw, the level-1 Warehouse (1,000) cuts
        // the window to 1,000, the 90/10 split makes that 900 common + 100
        // rare, and 990 already stored leaves room for 10 common.
        [Fact]
        public async Task OfflineProduction_RecordsTownHallGold_Gathered_AndWhatTheWarehouseCapThrewAway()
        {
            var player = await CreatePlayerAsync();
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                seed.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "birch_log", Quantity = 990 });
                await seed.SaveChangesAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                long overflow = await OfflineSimulationEngine.GrantVillagePassiveProductionAsync(
                    db, player.Id, lumberjackLevel: 1, mineLevel: 0, warehouseLevel: 1, townHallLevel: 1, elapsedSeconds: 36000L);
                Assert.Equal(1_790L, overflow);
            }

            long expectedGold = 36000L * VillageManagementEngine.GetTownHallGoldRatePerHour(1) / 3600L;
            Assert.True(expectedGold > 0);
            Assert.Equal(expectedGold, (await IncomeAsync(player.Id))[GoldIncomeSource.TownHall]);

            Assert.Equal(10, await FlowAsync(player.Id, "birch_log", MaterialFlowDirection.Gathered));
            Assert.Equal(200, await FlowAsync(player.Id, "golden_birch_log", MaterialFlowDirection.Gathered));
            // 2,000 produced, 200 of it rare: of the 1,800 common only 10 fit.
            // (Before 2026-09-30 a window ceiling also cut 1,000 first - a
            // second cap the live building never had, since removed.)
            Assert.Equal(1_790, await FlowAsync(player.Id, "birch_log", MaterialFlowDirection.LostToWarehouseCap));
            Assert.Equal(0, await FlowAsync(player.Id, "golden_birch_log", MaterialFlowDirection.LostToWarehouseCap));
        }

        [Fact]
        public async Task OfflineGathering_IsGathered_ButOfflineCombatLoot_IsNot()
        {
            var player = await CreatePlayerAsync();
            // The catalogue id space, which the analytic grant resolves through
            // (GetItemBaseId) since offline parity, 2026-09-30.
            Assert.True(ContentRegistry.TryGetItemDefinitionByBaseId("copper_ore", out var copperOre));
            var table = new[] { new LootTableEntry { ItemId = copperOre.Id, Weight = 100 } };

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await OfflineSimulationEngine.GrantAnalyticalLootAsync(db, player.Id, table, rollCount: 12, availableInventorySpace: 1000, lootLuckPct: 0f, recordAsGathered: true);
                await OfflineSimulationEngine.GrantAnalyticalLootAsync(db, player.Id, table, rollCount: 5, availableInventorySpace: 1000, lootLuckPct: 0f);
            }

            Assert.Equal(12, await FlowAsync(player.Id, "copper_ore", MaterialFlowDirection.Gathered));
        }

        // ------------------------------------------------------------------
        // Spent, and the retry outbox
        // ------------------------------------------------------------------

        [Fact]
        public async Task ConsumingAMaterial_IsSpent_AndARefusalRecordsNothing()
        {
            var player = await CreatePlayerAsync();
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                seed.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "copper_ore", Quantity = 50 });
                await seed.SaveChangesAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                Assert.True(await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, player.Id, "copper_ore", 20));
                Assert.False(await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, player.Id, "copper_ore", 500));
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            Assert.Equal(20, await FlowAsync(player.Id, "copper_ore", MaterialFlowDirection.Spent));
        }

        [Fact]
        public async Task ADelayedGrant_IsCountedWhenItLands_UnderItsOrigin()
        {
            var player = await CreatePlayerAsync();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // A rolled-back loot drop whose auto-salvage gold was re-queued,
                // and a rolled-back gathering batch.
                await PendingGrantOutbox.EnqueueCommodityDeltasAsync(db, player.Id, PendingGrantSourceType.CombatLoot,
                    new Dictionary<string, long> { ["gold"] = 33 });
                await PendingGrantOutbox.EnqueueCommodityDeltasAsync(db, player.Id, PendingGrantSourceType.Gathering,
                    new Dictionary<string, long> { ["birch_log"] = 8 });
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var rows = await db.PendingGrants.Where(r => r.PlayerId == player.Id).ToListAsync();
                Assert.Equal(2, rows.Count);
                foreach (var row in rows) Assert.True(await PendingGrantOutbox.TryApplyOneAsync(db, row));
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            Assert.Equal(33, (await IncomeAsync(player.Id))[GoldIncomeSource.AutoSalvage]);
            Assert.Equal(8, await FlowAsync(player.Id, "birch_log", MaterialFlowDirection.Gathered));
        }
    }
}
