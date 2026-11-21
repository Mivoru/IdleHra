using System;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Village 2 (owner, 2026-11-21): Town Hall and Workshop to 10, the other
    /// buildings to 20, the Forge to 14. The levels past the old caps cost
    /// 5 million gold doubling and region 4-5 materials; the old levels keep
    /// their old prices.
    /// </summary>
    public class Village2Tests
    {
        private const int TownHall = VillageManagementEngine.TownHallBuildingId;
        private const int Workshop = VillageManagementEngine.CraftingWorkshopBuildingId;
        private const int Forge = VillageManagementEngine.ForgeBuildingId;
        private const int Lumberjack = VillageManagementEngine.LumberjackBuildingId;
        private const int Inn = VillageManagementEngine.InnBuildingId;

        [Fact]
        public void Caps_TownHall9OpensTwenty_ForgeStopsAtTheTopRarity()
        {
            Assert.Equal(12, VillageManagementEngine.GetMaxBuildingLevelCeiling(5));
            Assert.Equal(20, VillageManagementEngine.GetMaxBuildingLevelCeiling(9));
            Assert.Equal(20, VillageManagementEngine.GetMaxBuildingLevelCeiling(10));

            Assert.Equal(10, VillageManagementEngine.MaxLevelFor(TownHall, 0));
            Assert.Equal(10, VillageManagementEngine.MaxLevelFor(Workshop, 0));
            Assert.Equal(12, VillageManagementEngine.MaxLevelFor(Lumberjack, 5));
            Assert.Equal(20, VillageManagementEngine.MaxLevelFor(Lumberjack, 10));
            Assert.Equal(14, VillageManagementEngine.MaxLevelFor(Forge, 10));
            Assert.Equal(ForgeSplicingEngine.MaxQualityTier, VillageManagementEngine.MaxForgeLevel);
        }

        [Fact]
        public void OldLevels_KeepTheirOldPrice()
        {
            // Level 11 -> 12 of a service building: the pre-Village-2 quote.
            var quote = VillageManagementEngine.QuoteUpgrade(Forge, 11);
            Assert.Equal(VillageManagementEngine.CalculateUpgradeCost(11), quote.Single(l => l.ItemId == "gold").Quantity);
            Assert.Equal(VillageManagementEngine.CalculateProductionUpgradeCost(11), quote.First().Quantity);

            // Town Hall 4 -> 5 pays no gold.
            Assert.DoesNotContain(VillageManagementEngine.QuoteUpgrade(TownHall, 4), l => l.ItemId == "gold");
        }

        [Theory]
        [InlineData(Lumberjack, 12, 5_000_000L, 1_000L, "acacia_log")]
        [InlineData(Lumberjack, 14, 20_000_000L, 2_250L, "acacia_log")]
        [InlineData(Lumberjack, 19, 640_000_000L, 17_086L, "frostpine_log")]
        [InlineData(TownHall, 5, 5_000_000L, 1_000L, "acacia_log")]
        [InlineData(TownHall, 9, 80_000_000L, 5_063L, "ebon_log")]
        public void EndgameLevels_PayGoldDoublingFromFiveMillion(int building, int level, long gold, long materials, string log)
        {
            var quote = VillageManagementEngine.QuoteUpgrade(building, level);
            Assert.Equal(gold, quote.Single(l => l.ItemId == "gold").Quantity);
            Assert.Equal(log, quote[0].ItemId);
            Assert.Equal(materials, quote[0].Quantity);
            Assert.Equal(materials, quote[1].Quantity);
        }

        [Fact]
        public void TheWholeEndgameBuilding_CostsAboutOnePointThreeBillion()
        {
            long total = 0;
            for (int level = VillageManagementEngine.LegacyBuildingCap; level < VillageManagementEngine.MaxBuildingLevel; level++)
                total += VillageManagementEngine.QuoteUpgrade(Lumberjack, level).Single(l => l.ItemId == "gold").Quantity;
            Assert.Equal(1_275_000_000L, total);
        }

        [Fact]
        public void Workshop_PaysRareLogs_AtEveryLevel()
        {
            var quote = VillageManagementEngine.QuoteUpgrade(Workshop, 7);
            Assert.Contains(quote, l => l.ItemId.StartsWith("golden_", StringComparison.Ordinal));
        }

        [Fact]
        public void StructuralEndgameLevels_TakeHours()
        {
            // Town Hall 5 -> 6 times like a building at 12 -> 13, not like level 5.
            Assert.Equal(12, VillageManagementEngine.DurationLevelFor(TownHall, 5));
            Assert.True(VillageManagementEngine.CalculateUpgradeDurationSeconds(VillageManagementEngine.DurationLevelFor(TownHall, 5)) >= 3 * 3600);
            Assert.Equal(4, VillageManagementEngine.DurationLevelFor(TownHall, 4));
            Assert.Equal(15, VillageManagementEngine.DurationLevelFor(Lumberjack, 15));
        }

        [Fact]
        public void Inn_PastTwelve_RaisesTheLowestNewcomerRoll()
        {
            Assert.Equal(2, BreedingAptitudes.VillagerFloorFor(12));
            Assert.Equal(10, BreedingAptitudes.VillagerFloorFor(20));
            var rng = new Random(7);
            for (int i = 0; i < 200; i++)
                Assert.All(BreedingAptitudes.RollVillager(20, rng), v => Assert.InRange(v, 10, BreedingAptitudes.VillagerCeiling));
        }

        [Fact]
        public void Workshop_PastFive_CutsCommissionTime_ToHalfAtTen()
        {
            long epic = WorkshopCommissionRules.DurationSecondsFor(RarityTier.Epic);
            Assert.Equal(epic, WorkshopCommissionRules.DurationSecondsFor(RarityTier.Epic, 5));
            Assert.Equal(epic * 9 / 10, WorkshopCommissionRules.DurationSecondsFor(RarityTier.Epic, 6));
            Assert.Equal(epic / 2, WorkshopCommissionRules.DurationSecondsFor(RarityTier.Epic, 10));
            Assert.Equal(epic / 2, WorkshopCommissionRules.DurationSecondsFor(RarityTier.Epic, 30));
        }

        [Fact]
        public void TownHallGold_KeepsRising_PastFive()
        {
            Assert.Equal(3000L, VillageManagementEngine.GetTownHallGoldRatePerHour(5));
            Assert.Equal(30000L, VillageManagementEngine.GetTownHallGoldRatePerHour(10));
            for (int level = 1; level < 10; level++)
                Assert.True(VillageManagementEngine.GetTownHallGoldRatePerHour(level + 1) >= VillageManagementEngine.GetTownHallGoldRatePerHour(level));
        }
    }
}
