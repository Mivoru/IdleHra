using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The equipment dungeon's run as the REST route performs it: a ticket
    /// leaves in the same transaction as the reward lands, a locked stage or an
    /// empty ticket shelf spends nothing, and a first clear pays once.
    /// </summary>
    [Collection("Postgres collection")]
    public class DungeonRunTests
    {
        private readonly PostgresTestFixture _fixture;

        public DungeonRunTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private static TickStatePayload Strong(long playerId)
        {
            byte mask = 0;
            for (int region = 1; region <= 2; region++)
                mask = BossFirstClearRules.MarkDefeated(mask, RaceUnlockRegistry.GetRegionBossMonsterId(region));
            return new TickStatePayload
            {
                PlayerId = playerId,
                CurrentLevel = 60,
                STR = 2000, DEX = 2000, CON = 2000, LCK = 200,
                AutoEatThreshold = 50,
                DefeatedRegionBossMask = mask,
            };
        }

        [Fact]
        public async Task AWonRunTakesOneTicketAndPaysAPieceAChestAndTheFirstClearOnce()
        {
            const long playerId = 950_420_001L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            int fish = FoodRegistry.FirstRawFishOfTier(1);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var first = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Equipment, 1, Strong(playerId), fish, 100, default, now, new Random(1));
                Assert.Equal(DungeonEngine.RunOutcome.Ok, first.Outcome);
                Assert.True(first.Result!.Won, "a 2000-attribute character should beat stage 1");
                Assert.Equal(DungeonEngine.TicketCap - 1, first.TicketsLeft);
                Assert.True(first.Reward!.PieceId > 0);
                Assert.True(first.Reward.ChestId > 0);
                Assert.Equal(DungeonEngine.FirstClearDiamonds(1), first.Reward.Diamonds);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var second = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Equipment, 1, Strong(playerId), fish, 100, default, now, new Random(2));
                Assert.Equal(0, second.Reward!.Diamonds);
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
            var row = await verify.DungeonProgress.AsNoTracking().SingleAsync(d => d.PlayerId == playerId && d.Kind == (int)DungeonKind.Equipment);
            Assert.Equal(DungeonEngine.TicketCap - 2, row.Tickets);
            Assert.Equal(1, row.BestStage);
            // The other dungeons keep their own tickets.
            Assert.Equal(DungeonEngine.TicketCap, (await verify.DungeonProgress.AsNoTracking().SingleAsync(d => d.PlayerId == playerId && d.Kind == (int)DungeonKind.Gold)).Tickets);
            Assert.Equal(DungeonEngine.FirstClearDiamonds(1), player.PremiumDiamonds);
            Assert.Equal(2, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == playerId));
            Assert.Equal(2, await verify.LootChests.CountAsync(c => c.PlayerId == playerId));
        }

        [Fact]
        public async Task ALockedStageOrNoTicketSpendsNothing()
        {
            const long playerId = 950_420_002L;
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.DungeonProgress.Add(new DungeonProgress { PlayerId = playerId, Kind = (int)DungeonKind.Equipment, Tickets = 0, TicketDay = DungeonEngine.UtcDay(now) });
                await db.SaveChangesAsync();
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var locked = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Equipment, 3, Strong(playerId), 0, 0, default, now, new Random(1));
                Assert.Equal(DungeonEngine.RunOutcome.Locked, locked.Outcome);
                var empty = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Equipment, 1, Strong(playerId), 0, 0, default, now, new Random(1));
                Assert.Equal(DungeonEngine.RunOutcome.NoTickets, empty.Outcome);
            }
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(0, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == playerId));
            Assert.Equal(0, (await verify.DungeonProgress.AsNoTracking().SingleAsync(d => d.PlayerId == playerId && d.Kind == (int)DungeonKind.Equipment)).Tickets);
        }

        [Fact]
        public async Task TheXpDungeonReturnsItsXpAndTheGoldDungeonBanksItsGold()
        {
            const long playerId = 950_420_003L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            int fish = FoodRegistry.FirstRawFishOfTier(1);
            var income = new DungeonEngine.Income(HourlyXp: 120_000, HourlyGold: 3_000_000);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var xp = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Xp, 1, Strong(playerId), fish, 100, income, now, new Random(1));
                Assert.True(xp.Result!.Won);
                Assert.Equal(DungeonEngine.XpFor(1, 120_000, won: true), xp.Reward!.Xp);
                Assert.Equal(0, xp.Reward.Gold);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var gold = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Gold, 1, Strong(playerId), fish, 100, income, now, new Random(1));
                Assert.True(gold.Result!.Won);
                Assert.Equal(DungeonEngine.GoldFor(1, 3_000_000, won: true), gold.Reward!.Gold);
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var banked = await verify.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
            Assert.Equal(DungeonEngine.GoldFor(1, 3_000_000, won: true), banked.Quantity);
            // Two first clears, one in each dungeon.
            Assert.Equal(2 * DungeonEngine.FirstClearDiamonds(1), (await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId)).PremiumDiamonds);
        }

        [Fact]
        public async Task AnAdminTestRunSpendsNoTicketAndRecordsWhatItPaid()
        {
            const long playerId = 950_420_004L;
            var account = Guid.NewGuid();
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = account, AuthenticatorToken = Guid.NewGuid() });
                db.DungeonProgress.Add(new DungeonProgress { PlayerId = playerId, Kind = (int)DungeonKind.Xp, Tickets = 0, TicketDay = DungeonEngine.UtcDay(now) });
                await db.SaveChangesAsync();
            }
            int fish = FoodRegistry.FirstRawFishOfTier(1);
            var income = new DungeonEngine.Income(HourlyXp: 120_000, HourlyGold: 0);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var run = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Xp, 1, Strong(playerId), fish, 100, income, now, new Random(1), freeRun: true);
                Assert.Equal(DungeonEngine.RunOutcome.Ok, run.Outcome);
                Assert.Equal(0, run.TicketsLeft);
            }
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var logged = await verify.AccountAnalyticsLogs.AsNoTracking().Where(l => l.AccountId == account).ToListAsync();
            Assert.Equal(DungeonEngine.XpFor(1, 120_000, won: true), logged.Single(l => l.EventTypeHash == DungeonEngine.TestGrantXp).PayloadMetric);
            Assert.Equal(DungeonEngine.FirstClearDiamonds(1), logged.Single(l => l.EventTypeHash == DungeonEngine.TestGrantDiamonds).PayloadMetric);
        }

        [Fact]
        public async Task ALiveRunTakesItsTicketAtTheStartAndASweepNeedsABeatenStage()
        {
            const long playerId = 950_420_005L;
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }
            byte mask = Strong(playerId).DefeatedRegionBossMask;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var sweep = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Gold, 1, Strong(playerId), 0, 0, default, now, new Random(1), sweep: true);
                Assert.Equal(DungeonEngine.RunOutcome.Locked, sweep.Outcome);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var start = await DungeonEngine.StartLiveAsync(db, playerId, DungeonKind.Gold, 1, mask, now, freeRun: false);
                Assert.Equal(DungeonEngine.RunOutcome.Ok, start.Outcome);
                Assert.Equal(DungeonEngine.TicketCap - 1, start.TicketsLeft);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var finish = await DungeonEngine.FinishLiveAsync(db, playerId, DungeonKind.Gold, 1, won: true, new DungeonEngine.Income(0, 3_000_000), now, new Random(1), freeRun: false);
                Assert.Equal(DungeonEngine.GoldFor(1, 3_000_000, won: true), finish.Reward!.Gold);
                Assert.Equal(DungeonEngine.FirstClearDiamonds(1), finish.Reward.Diamonds);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var sweep = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Gold, 1, Strong(playerId), 0, 0, new DungeonEngine.Income(0, 3_000_000), now, new Random(1), sweep: true);
                Assert.Equal(DungeonEngine.RunOutcome.Ok, sweep.Outcome);
                Assert.True(sweep.Result!.Won);
                Assert.Equal(0, sweep.Reward!.Diamonds);
                Assert.Equal(DungeonEngine.TicketCap - 2, sweep.TicketsLeft);
            }
        }
    }
}
