using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>Expeditions (2026-11-13): the rules, then a start-and-claim round trip on a real database.</summary>
    [Collection("Postgres collection")]
    public class ExpeditionTests
    {
        private readonly PostgresTestFixture _fixture;

        public ExpeditionTests(PostgresTestFixture fixture) => _fixture = fixture;

        [Fact]
        public void Rules_SlotsDurationsAndChests()
        {
            Assert.Equal(0, ExpeditionRules.SlotsFor(0));
            Assert.Equal(2, ExpeditionRules.SlotsFor(1));
            Assert.Equal(3, ExpeditionRules.SlotsFor(ExpeditionRules.ThirdSlotGroundsLevel));
            Assert.True(ExpeditionRules.IsDuration(12));
            Assert.False(ExpeditionRules.IsDuration(24));

            Assert.Equal(0, ExpeditionRules.AptitudeUpgradePct(16));
            Assert.Equal(30, ExpeditionRules.AptitudeUpgradePct(200));

            Assert.Equal(LootChestTier.Bronze, ExpeditionRules.ChestFor(4, 200, 0));
            Assert.Equal(LootChestTier.Silver, ExpeditionRules.ChestFor(8, 16, 24));
            Assert.Equal(LootChestTier.Bronze, ExpeditionRules.ChestFor(8, 16, 25));
            Assert.Equal(LootChestTier.Silver, ExpeditionRules.ChestFor(8, 200, 54));
            Assert.Equal(LootChestTier.Gold, ExpeditionRules.ChestFor(12, 200, 44));
            Assert.Equal(LootChestTier.Silver, ExpeditionRules.ChestFor(12, 200, 45));
        }

        [Fact]
        public async Task AnAncestorGoesAway_IsLocked_AndBringsBackOneChest()
        {
            const long playerId = 960_117_001L;
            var hero = Guid.NewGuid();
            var fielded = Guid.NewGuid();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = playerId, BuildingId = VillageManagementEngine.BreedingGroundsBuildingId, CurrentLevel = 3 });
                db.CharacterRecords.Add(new CharacterRecord { Id = hero, PlayerId = playerId, Name = "Away", SlotIndex = 5, AgePhase = AgePhaseCurve.Adult, AgeTicks = AgePhaseCurve.ChildEndTicks });
                db.CharacterRecords.Add(new CharacterRecord { Id = fielded, PlayerId = playerId, Name = "Playing", SlotIndex = 0, AgePhase = AgePhaseCurve.Adult, AgeTicks = AgePhaseCurve.ChildEndTicks });
                db.CharacterLineages.Add(new CharacterLineageRegistry { CharacterId = hero, AptitudeStrength = 30, AptitudeSkill = 30, AptitudeEndurance = 30, AptitudeFortune = 30 });
                await db.SaveChangesAsync();
            }

            long now = 1_900_000_000L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var board = await ExpeditionEngine.BoardAsync(db, playerId, now);
                Assert.Equal(2, board.Slots);
                Assert.Contains(board.Candidates, c => c.CharacterId == hero && c.AptitudeTotal == 120);
                Assert.DoesNotContain(board.Candidates, c => c.CharacterId == fielded);

                Assert.Equal(ExpeditionStartResult.NotEligible, await ExpeditionEngine.StartAsync(db, playerId, fielded, 4, now));
                Assert.Equal(ExpeditionStartResult.BadDuration, await ExpeditionEngine.StartAsync(db, playerId, hero, 5, now));
                Assert.Equal(ExpeditionStartResult.Started, await ExpeditionEngine.StartAsync(db, playerId, hero, 8, now));
                Assert.Equal(ExpeditionStartResult.NotEligible, await ExpeditionEngine.StartAsync(db, playerId, hero, 8, now));
            }

            long expeditionId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.True(await ExpeditionEngine.IsAwayAsync(db, hero));
                var character = await db.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == hero);
                Assert.Equal(now + 8 * 3600L, character.BreedingCooldownEndEpoch);
                expeditionId = (await db.Expeditions.AsNoTracking().SingleAsync(e => e.CharacterId == hero)).Id;

                var early = await ExpeditionEngine.ClaimAsync(db, playerId, expeditionId, now + 3600, DateTime.UtcNow, new Random(1));
                Assert.False(early.Ok);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                int chestsBefore = await db.LootChests.CountAsync(c => c.PlayerId == playerId);
                var claim = await ExpeditionEngine.ClaimAsync(db, playerId, expeditionId, now + 8 * 3600, DateTime.UtcNow, new Random(1));
                Assert.True(claim.Ok);
                Assert.Contains(claim.Tier, new[] { "Bronze", "Silver" });
                Assert.Equal(1, claim.Claimed);
                Assert.Equal(string.Empty, claim.Title);
                Assert.Equal(chestsBefore + 1, await db.LootChests.CountAsync(c => c.PlayerId == playerId && c.Source == (byte)LootChestSource.Expedition));
                Assert.False(await ExpeditionEngine.IsAwayAsync(db, hero));

                var twice = await ExpeditionEngine.ClaimAsync(db, playerId, expeditionId, now + 9 * 3600, DateTime.UtcNow, new Random(1));
                Assert.False(twice.Ok);
            }
        }

        [Fact]
        public async Task TheTenthClaimPaysTheFirstTitle()
        {
            const long playerId = 960_117_002L;
            var hero = Guid.NewGuid();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), ExpeditionsClaimed = 9 });
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = playerId, BuildingId = VillageManagementEngine.BreedingGroundsBuildingId, CurrentLevel = 1 });
                db.CharacterRecords.Add(new CharacterRecord { Id = hero, PlayerId = playerId, Name = "Tenth", SlotIndex = 4, AgePhase = AgePhaseCurve.Adult, AgeTicks = AgePhaseCurve.ChildEndTicks });
                db.CharacterLineages.Add(new CharacterLineageRegistry { CharacterId = hero });
                await db.SaveChangesAsync();
            }
            long now = 1_900_100_000L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(ExpeditionStartResult.Started, await ExpeditionEngine.StartAsync(db, playerId, hero, 4, now));
                var board = await ExpeditionEngine.BoardAsync(db, playerId, now);
                Assert.Equal("Pathfinder", board.NextTitle);
                long id = board.Active.Single().Id;
                var claim = await ExpeditionEngine.ClaimAsync(db, playerId, id, now + 4 * 3600, DateTime.UtcNow, new Random(2));
                Assert.Equal(10, claim.Claimed);
                Assert.Equal("Pathfinder", claim.Title);
                Assert.True(await db.PlayerTitles.AnyAsync(t => t.PlayerId == playerId && t.TitleSlug == "expedition_10"));
            }
        }

        [Fact]
        public void BoughtSlotsArePricedInHoursOfOwnIncome()
        {
            Assert.Equal(0, ExpeditionRules.SlotsFor(0, 2));
            Assert.Equal(5, ExpeditionRules.SlotsFor(ExpeditionRules.ThirdSlotGroundsLevel, 2));
            Assert.Equal(5, ExpeditionRules.SlotsFor(ExpeditionRules.ThirdSlotGroundsLevel, 9));
            // 10,000 gold a minute: the 4th slot is a day of it, the 5th three days.
            Assert.Equal(14_400_000L, ExpeditionRules.NextSlotPrice(0, 10_000));
            Assert.Equal(43_200_000L, ExpeditionRules.NextSlotPrice(1, 10_000));
            Assert.Equal(0L, ExpeditionRules.NextSlotPrice(2, 10_000));
        }

        [Fact]
        public async Task BuyingASlotTakesItsGoldAndAddsTheSlot()
        {
            const long playerId = 960_117_003L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = playerId, BuildingId = VillageManagementEngine.BreedingGroundsBuildingId, CurrentLevel = 2 });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 1_000_000_000L });
                await db.SaveChangesAsync();
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var before = await ExpeditionEngine.BoardAsync(db, playerId, 1_900_200_000L);
                Assert.Equal(2, before.Slots);
                Assert.True(before.NextSlotCost > 0);

                var (result, price) = await ExpeditionEngine.BuySlotAsync(db, playerId);
                Assert.Equal(ExpeditionSlotPurchase.Bought, result);
                Assert.Equal(before.NextSlotCost, price);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var after = await ExpeditionEngine.BoardAsync(db, playerId, 1_900_200_000L);
                Assert.Equal(3, after.Slots);
                Assert.Equal(1, after.SlotsBought);
                long gold = await db.CommodityRecords.AsNoTracking().Where(c => c.PlayerId == playerId && c.ItemId == "gold").Select(c => c.Quantity).SingleAsync();
                Assert.True(gold < 1_000_000_000L);
            }
        }
    }
}
