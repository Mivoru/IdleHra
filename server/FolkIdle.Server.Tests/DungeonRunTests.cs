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
                var first = await DungeonEngine.RunAsync(db, playerId, 1, Strong(playerId), fish, 100, now, new Random(1));
                Assert.Equal(DungeonEngine.RunOutcome.Ok, first.Outcome);
                Assert.True(first.Result!.Won, "a 2000-attribute character should beat stage 1");
                Assert.Equal(DungeonEngine.TicketCap - 1, first.TicketsLeft);
                Assert.True(first.Reward!.PieceId > 0);
                Assert.True(first.Reward.ChestId > 0);
                Assert.Equal(DungeonEngine.FirstClearDiamonds(1), first.Reward.Diamonds);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var second = await DungeonEngine.RunAsync(db, playerId, 1, Strong(playerId), fish, 100, now, new Random(2));
                Assert.Equal(0, second.Reward!.Diamonds);
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
            Assert.Equal(DungeonEngine.TicketCap - 2, player.DungeonTickets);
            Assert.Equal(1, player.DungeonBestStage);
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
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(),
                    DungeonTickets = 0, DungeonTicketDay = DungeonEngine.UtcDay(now) });
                await db.SaveChangesAsync();
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var locked = await DungeonEngine.RunAsync(db, playerId, 3, Strong(playerId), 0, 0, now, new Random(1));
                Assert.Equal(DungeonEngine.RunOutcome.Locked, locked.Outcome);
                var empty = await DungeonEngine.RunAsync(db, playerId, 1, Strong(playerId), 0, 0, now, new Random(1));
                Assert.Equal(DungeonEngine.RunOutcome.NoTickets, empty.Outcome);
            }
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(0, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == playerId));
            Assert.Equal(0, (await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId)).DungeonTickets);
        }
    }
}
