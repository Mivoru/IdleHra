using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FolkIdle.Server.Domain.Progression;

namespace FolkIdle.Server.Tests
{
    // Modul: THE PRICE A PLAYER READS IS THE PRICE THEY PAY.
    //
    // The Village screen priced upgrades from a hand-kept client copy of the
    // tier table, and it had drifted: rare ores where the server charges the
    // commons, the wrong tier for Town Hall and the Workshop, and no mention of
    // the Workshop's golden logs. The screen now shows
    // VillageManagementEngine.QuoteUpgrade through /api/v1/village/quote, and
    // these tests hold the upgrade handler to charging exactly those lines -
    // no more, no less - for every building across its level range.
    [Collection("Postgres collection")]
    public class VillageQuoteTests
    {
        private readonly PostgresTestFixture _fixture;

        public VillageQuoteTests(PostgresTestFixture fixture) => _fixture = fixture;

        private const long Plenty = 1_000_000L;

        private static readonly int[] BuildingIds =
        {
            VillageManagementEngine.ForgeBuildingId,
            VillageManagementEngine.InnBuildingId,
            VillageManagementEngine.BreedingGroundsBuildingId,
            VillageManagementEngine.LumberjackBuildingId,
            VillageManagementEngine.MineBuildingId,
            VillageManagementEngine.WarehouseBuildingId,
            VillageManagementEngine.TownHallBuildingId,
            VillageManagementEngine.CraftingWorkshopBuildingId,
        };

        [Fact]
        public void EveryValidBuildingIsCovered()
        {
            var valid = Enumerable.Range(1, 10)
                .Where(id => VillageManagementEngine.IsValidBuildingId((uint)id))
                .ToArray();
            Assert.Equal(valid.OrderBy(x => x), BuildingIds.OrderBy(x => x));
        }

        [Fact]
        public void StructuralBuildingsClimbOneTierPerTwoLevels()
        {
            // Town Hall 2 -> 3 is tier 1: willow and iron, not birch and copper.
            var quote = VillageManagementEngine.QuoteUpgrade(VillageManagementEngine.TownHallBuildingId, 2);
            Assert.Contains(quote, l => l.ItemId == "willow_log");
            Assert.Contains(quote, l => l.ItemId == "iron_ore");
            Assert.DoesNotContain(quote, l => l.ItemId == VillageManagementEngine.GoldItemId);
        }

        [Fact]
        public void TheWorkshopAlsoTakesGoldenLogs()
        {
            var quote = VillageManagementEngine.QuoteUpgrade(VillageManagementEngine.CraftingWorkshopBuildingId, 0);
            Assert.Contains(quote, l => l.ItemId == "golden_birch_log" && l.Quantity == 10L);
        }

        [Fact]
        public void ServiceBuildingsChargeCommonOreAndGold()
        {
            var quote = VillageManagementEngine.QuoteUpgrade(VillageManagementEngine.ForgeBuildingId, 0);
            Assert.Equal(new[] { "birch_log", "copper_ore", "gold" }, quote.Select(l => l.ItemId));
            Assert.DoesNotContain(quote, l => l.ItemId == "malachite_ore");
        }

        public static IEnumerable<object[]> Cases()
        {
            int index = 0;
            foreach (int building in BuildingIds)
            {
                bool structural = VillageManagementEngine.IsStructuralBuilding(building);
                int[] levels = structural ? new[] { 0, 1, 2, 3, 4 } : new[] { 0, 3, 4, 5, 9, 10, 11 };
                foreach (int level in levels)
                {
                    yield return new object[] { building, level, 951_700_000L + index };
                    index++;
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task TheHandlerChargesExactlyTheQuote(int buildingId, int currentLevel, long playerId)
        {
            var quote = VillageManagementEngine.QuoteUpgrade(buildingId, currentLevel);

            // Every material the village could ever charge, so a line the quote
            // does not name but the handler spends is caught too.
            var watched = new HashSet<string> { VillageManagementEngine.GoldItemId };
            for (int level = 0; level <= 12; level++)
            {
                var mats = VillageManagementEngine.GetTierMaterials(level);
                watched.Add(mats.Log);
                watched.Add(mats.Ore);
                watched.Add(mats.RareLog);
                watched.Add(mats.RareOre);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = playerId, BuildingId = buildingId, CurrentLevel = currentLevel });
                if (buildingId != VillageManagementEngine.TownHallBuildingId)
                {
                    // A maxed Town Hall, so the ceiling (12) never refuses.
                    db.VillageInfrastructures.Add(new VillageInfrastructure
                    {
                        PlayerId = playerId,
                        BuildingId = VillageManagementEngine.TownHallBuildingId,
                        CurrentLevel = VillageManagementEngine.MaxStructuralBuildingLevel,
                    });
                }
                foreach (string item in watched)
                {
                    db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = item, Quantity = Plenty });
                }
                await db.SaveChangesAsync();
            }

            var engine = new VillageManagementEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await engine.ExecuteUpgradeBuildingAsync(playerId, (uint)buildingId);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var infrastructure = await verify.VillageInfrastructures.AsNoTracking()
                .SingleAsync(v => v.PlayerId == playerId && v.BuildingId == buildingId);
            Assert.Equal(currentLevel + 1, infrastructure.UpgradeTargetLevel);

            var after = await verify.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .ToDictionaryAsync(c => c.ItemId, c => c.Quantity);

            foreach (string item in watched)
            {
                long quoted = quote.Where(l => l.ItemId == item).Sum(l => l.Quantity);
                Assert.True(
                    Plenty - quoted == after[item],
                    $"building {buildingId} level {currentLevel}: {item} spent {Plenty - after[item]}, quoted {quoted}");
            }
        }
    }
}
