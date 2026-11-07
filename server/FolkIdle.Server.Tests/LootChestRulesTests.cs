using System;
using System.Linq;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Loot chests (CHESTS_DUNGEONS_DIAMONDS.md section 1): what one opening
    /// holds, as time rather than flat numbers, and the weekly diamond ceiling
    /// turning a refused diamond into gold instead of nothing.
    /// </summary>
    public class LootChestRulesTests
    {
        public LootChestRulesTests() => ContentRegistry.Initialize();

        [Fact]
        public void EveryTierPaysMoreThanTheOneBelow()
        {
            for (int t = 2; t <= 4; t++)
            {
                var lower = LootChestRules.TableFor((LootChestTier)(t - 1));
                var upper = LootChestRules.TableFor((LootChestTier)t);
                Assert.True(upper.MaterialMinutes > lower.MaterialMinutes);
                Assert.True(upper.GoldMinutes > lower.GoldMinutes);
                // Silver and up always hold a piece (2026-11-07), so the better
                // chest pays through the rarity floor instead.
                Assert.True(upper.EquipmentPermille >= lower.EquipmentPermille);
                Assert.True(upper.EquipmentRarityFloor > lower.EquipmentRarityFloor);
                Assert.True(upper.DiamondPermille >= lower.DiamondPermille);
            }
            Assert.Equal(1000, LootChestRules.TableFor(LootChestTier.Ancient).EquipmentPermille);
        }

        [Fact]
        public void GoldIsMinutesOfTheHoldersOwnIncome()
        {
            var poor = LootChestRules.Roll(LootChestTier.Gold, 2, 2, goldPerMinute: 100, diamondRoomThisWeek: 0, new Random(1));
            var rich = LootChestRules.Roll(LootChestTier.Gold, 2, 2, goldPerMinute: 10_000, diamondRoomThisWeek: 0, new Random(1));
            Assert.Equal(100L * 60, poor.Gold);
            Assert.Equal(10_000L * 60, rich.Gold);
        }

        [Fact]
        public void MaterialsComeFromTheChestsRegion_AndSilverUpAddsTheHighestRegion()
        {
            var bronze = LootChestRules.Roll(LootChestTier.Bronze, 1, 4, 100, 0, new Random(2));
            // Log, ore, their rare pair, and the region's herb pair (step 5).
            Assert.Equal(6, bronze.Materials.Count);
            Assert.Contains(bronze.Materials, m => m.ItemId == "yarrow" && m.Quantity == 135);
            Assert.Contains(bronze.Materials, m => m.ItemId == "golden_chamomile" && m.Quantity == 15);

            var silver = LootChestRules.Roll(LootChestTier.Silver, 1, 4, 100, 0, new Random(2));
            Assert.Equal(10, silver.Materials.Count);
            Assert.All(silver.Materials, m => Assert.True(m.Quantity > 0, m.ItemId));

            // Same region twice: the lines merge, nothing is listed double.
            var same = LootChestRules.Roll(LootChestTier.Silver, 3, 3, 100, 0, new Random(2));
            Assert.Equal(6, same.Materials.Count);
            Assert.Equal(same.Materials.Count, same.Materials.Select(m => m.ItemId).Distinct().Count());
        }

        [Fact]
        public void ARefusedDiamondPaysGoldInstead()
        {
            // Ancient always rolls 5-10 diamonds.
            var full = LootChestRules.Roll(LootChestTier.Ancient, 5, 5, 1000, diamondRoomThisWeek: 0, new Random(3));
            Assert.Equal(0, full.Diamonds);
            Assert.True(full.GoldInsteadOfDiamonds >= 5L * LootChestRules.GoldMinutesPerRefusedDiamond * 1000);

            var room = LootChestRules.Roll(LootChestTier.Ancient, 5, 5, 1000, diamondRoomThisWeek: 100, new Random(3));
            Assert.InRange(room.Diamonds, 5, 10);
            Assert.Equal(0, room.GoldInsteadOfDiamonds);

            var partial = LootChestRules.Roll(LootChestTier.Ancient, 5, 5, 1000, diamondRoomThisWeek: 2, new Random(3));
            Assert.Equal(2, partial.Diamonds);
            Assert.True(partial.GoldInsteadOfDiamonds > 0);
        }

        [Fact]
        public void TheOddsMatchTheRolls_OverManyOpenings()
        {
            var rng = new Random(4);
            int equipment = 0, diamonds = 0;
            const int n = 20_000;
            for (int i = 0; i < n; i++)
            {
                var r = LootChestRules.Roll(LootChestTier.Silver, 2, 2, 100, 1000, rng);
                if (r.Equipment) equipment++;
                if (r.Diamonds > 0) diamonds++;
            }
            var t = LootChestRules.TableFor(LootChestTier.Silver);
            Assert.InRange(equipment * 1000.0 / n, t.EquipmentPermille - 15, t.EquipmentPermille + 15);
            Assert.InRange(diamonds * 1000.0 / n, t.DiamondPermille - 6, t.DiamondPermille + 6);

            Assert.Contains(LootChestRules.OddsLines(LootChestTier.Silver), l => l.StartsWith("Equipment: 100%"));
            Assert.DoesNotContain(LootChestRules.OddsLines(LootChestTier.Bronze), l => l.StartsWith("Diamonds"));
        }
    
        [Theory]
        [InlineData(LootChestTier.Bronze)]
        [InlineData(LootChestTier.Silver)]
        [InlineData(LootChestTier.Gold)]
        [InlineData(LootChestTier.Ancient)]
        public void EquipmentRarityOdds_SumToOne_NeverFallBelowTheFloor_AndAreRolledAsWritten(LootChestTier tier)
        {
            var t = LootChestRules.TableFor(tier);
            long sum = 0;
            for (int q = 1; q <= RarityTier.Transcendent; q++)
            {
                int odds = LootChestRules.RarityOddsFor(tier, q);
                if (q < t.EquipmentRarityFloor) Assert.Equal(0, odds);
                sum += odds;
            }
            Assert.Equal(LootChestRules.RarityOddsScale, sum);
            // Godly and Transcendent are possible from every chest.
            Assert.True(LootChestRules.RarityOddsFor(tier, RarityTier.Godly) > 0);
            Assert.True(LootChestRules.RarityOddsFor(tier, RarityTier.Transcendent) > 0);

            var rng = new Random(11);
            const int n = 200_000;
            var counts = new int[RarityTier.Transcendent + 1];
            for (int i = 0; i < n; i++) counts[LootChestRules.RollEquipmentTier(tier, rng)]++;
            int floorShare = (int)(counts[t.EquipmentRarityFloor] * (long)LootChestRules.RarityOddsScale / n);
            Assert.InRange(floorShare, LootChestRules.RarityOddsFor(tier, t.EquipmentRarityFloor) - 50_000,
                LootChestRules.RarityOddsFor(tier, t.EquipmentRarityFloor) + 50_000);
            for (int q = 1; q < t.EquipmentRarityFloor; q++) Assert.Equal(0, counts[q]);

            var line = Assert.Single(LootChestRules.OddsLines(tier), l => l.StartsWith("Equipment rarity: "));
            Assert.Contains("Transcendent", line);
        }

        [Fact]
        public void AnAncientChest_IsARelicOrBetter_AndCanBeTranscendent()
        {
            Assert.Equal(6_100_000, LootChestRules.RarityOddsFor(LootChestTier.Ancient, RarityTier.Relic));
            Assert.Equal(75_000, LootChestRules.RarityOddsFor(LootChestTier.Ancient, RarityTier.Godly));        // 0.75%
            Assert.Equal(15_000, LootChestRules.RarityOddsFor(LootChestTier.Ancient, RarityTier.Transcendent)); // 0.15%
            var roll = LootChestRules.Roll(LootChestTier.Ancient, 3, 3, 100, 100, new Random(5));
            Assert.True(roll.Equipment);
            Assert.True(roll.EquipmentTier >= RarityTier.Relic);
        }

        [Fact]
        public void TheSourcesPayTheChestsTheBuildPlanNames()
        {
            Assert.Null(LootChestEngine.DeepFloorChest(9));
            Assert.Equal(LootChestTier.Bronze, LootChestEngine.DeepFloorChest(10));
            Assert.Equal(LootChestTier.Bronze, LootChestEngine.DeepFloorChest(12));
            Assert.Equal(LootChestTier.Silver, LootChestEngine.DeepFloorChest(13));
            Assert.Equal(LootChestTier.Silver, LootChestEngine.DeepFloorChest(16));
            Assert.Equal(LootChestTier.Gold, LootChestEngine.DeepFloorChest(17));
            Assert.Equal(LootChestTier.Gold, LootChestEngine.DeepFloorChest(40));
            Assert.Equal(LootChestTier.Silver, LootChestEngine.BossFirstClearChest(1));
            Assert.Equal(LootChestTier.Gold, LootChestEngine.BossFirstClearChest(5));
        }

        [Fact]
        public void AnAscensionChestIsMostOftenTheLowestTier()
        {
            var rng = new Random(9);
            var counts = new int[5];
            for (int i = 0; i < 20_000; i++) counts[(int)LootChestEngine.RollAscensionChest(rng, 1)]++;
            Assert.True(counts[1] > counts[2] && counts[2] > counts[3] && counts[3] > counts[4]);
            Assert.InRange(counts[1] / 20_000.0, 0.47, 0.53);
            Assert.InRange(counts[4] / 20_000.0, 0.035, 0.065);

            // The bands get better; step 10 is Gold-or-better every time.
            double GoldPlus(int step) { int g = 0; for (int i = 0; i < 20_000; i++) if (LootChestEngine.RollAscensionChest(rng, step) >= LootChestTier.Gold) g++; return g / 20_000.0; }
            Assert.True(GoldPlus(8) > GoldPlus(5) + 0.07);
            Assert.Equal(1.0, GoldPlus(10));
        }
}
}
