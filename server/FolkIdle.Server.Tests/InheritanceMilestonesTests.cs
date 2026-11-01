using System;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The inheritance milestones at levels 5 and 10 (InheritanceMilestones).
    /// Each effect is pinned at level 4 (absent), 5 and 10. The combat half's
    /// offline parity is two OfflineCombatParityTests rows ("inheritance
    /// milestones r3/r5 fed").
    /// </summary>
    public class InheritanceMilestonesTests
    {
        public InheritanceMilestonesTests() => ContentRegistry.Initialize();

        private static TickStatePayload Character() => new()
        {
            PlayerId = 1,
            CurrentLevel = 60,
            STR = 80, DEX = 80, CON = 80, LCK = 40,
            AutoEatThreshold = 50,
        };

        [Fact]
        public void EveryStatHasTwoMilestoneLines()
        {
            for (int stat = 0; stat < InheritanceRegistry.StatCount; stat++)
            {
                Assert.False(string.IsNullOrWhiteSpace(InheritanceMilestones.Describe(stat, false)));
                Assert.False(string.IsNullOrWhiteSpace(InheritanceMilestones.Describe(stat, true)));
            }
        }

        [Fact]
        public void DamageMilestones_AddCritChanceAtFiveAndCritDamageAtTen()
        {
            var p = Character();
            p.Inherit_Damage = 4;
            var four = SimulationEngine.LiveCombatStats(in p);
            p.Inherit_Damage = 5;
            var five = SimulationEngine.LiveCombatStats(in p);
            p.Inherit_Damage = 10;
            var ten = SimulationEngine.LiveCombatStats(in p);

            Assert.Equal(four.CritChancePct + InheritanceMilestones.CritChancePct, five.CritChancePct, 3);
            Assert.Equal(four.EquipmentCritDamagePct, five.EquipmentCritDamagePct, 3);
            Assert.Equal(four.EquipmentCritDamagePct + InheritanceMilestones.CritDamagePct, ten.EquipmentCritDamagePct, 3);
        }

        [Fact]
        public void MaxHealthMilestones_BiggerBiteAtFiveAndArmourAtTen()
        {
            int food = Enumerable.Range(1, ContentRegistry.ItemDefinitions.Length)
                .First(id => FoodRegistry.GetHealMilliHp(id, 1_000_000) > 0);
            var p = Character();
            p.Inherit_MaxHp = 4;
            int plain = InheritanceMilestones.FoodHealMilliHp(in p, food, 1_000_000);
            Assert.Equal(FoodRegistry.GetHealMilliHp(food, 1_000_000), plain);
            p.Inherit_MaxHp = 5;
            Assert.Equal(plain * 115L / 100, InheritanceMilestones.FoodHealMilliHp(in p, food, 1_000_000));

            var armoured = Character();
            armoured.CachedAffixTotals.FlatDefense = 200;
            armoured.Inherit_MaxHp = 9;
            int nine = SimulationEngine.LiveCombatStats(in armoured).FlatPhysicalArmor;
            armoured.Inherit_MaxHp = 10;
            int ten = SimulationEngine.LiveCombatStats(in armoured).FlatPhysicalArmor;
            Assert.True(nine > 0);
            Assert.Equal(nine + nine * InheritanceMilestones.ArmourPct / 100, ten);
        }

        [Fact]
        public void ExperienceMilestones_AwayXpAtFiveAndGateXpAtTen()
        {
            var p = Character();
            p.Inherit_XpGain = 4;
            Assert.Equal(1000L, InheritanceMilestones.AwayXp(in p, 1000));
            Assert.Equal((1000L, 1000L), InheritanceMilestones.DungeonIncome(in p, 1000, 1000));
            p.Inherit_XpGain = 5;
            Assert.Equal(1050L, InheritanceMilestones.AwayXp(in p, 1000));
            Assert.Equal((1000L, 1000L), InheritanceMilestones.DungeonIncome(in p, 1000, 1000));
            p.Inherit_XpGain = 10;
            Assert.Equal((1250L, 1000L), InheritanceMilestones.DungeonIncome(in p, 1000, 1000));
        }

        [Fact]
        public void GoldMilestones_GateGoldAtFiveAndDelveCeilingAtTen()
        {
            var p = Character();
            p.Inherit_GoldGain = 5;
            Assert.Equal((1000L, 1250L), InheritanceMilestones.DungeonIncome(in p, 1000, 1000));
            Assert.Equal(DelveRegistry.MaxDiamondsPerWeek, InheritanceMilestones.DelveDiamondCeiling(9));
            Assert.Equal(DelveRegistry.MaxDiamondsPerWeek + InheritanceMilestones.DelveDiamonds, InheritanceMilestones.DelveDiamondCeiling(10));
        }

        [Fact]
        public void GatheringMilestones_SpeedAtFiveAndRareWeightDoubledAtTen()
        {
            var p = Character();
            p.Inherit_GatheringYield = 4;
            int four = SimulationEngine.GatherSpeedTermsFor(ref p, 0).ExtraPct;
            p.Inherit_GatheringYield = 5;
            Assert.Equal(four + InheritanceMilestones.GatherSpeedPct, SimulationEngine.GatherSpeedTermsFor(ref p, 0).ExtraPct);

            Assert.Equal(7, InheritanceMilestones.RareFindWeight(in p, 7));
            p.Inherit_GatheringYield = 10;
            Assert.Equal(14, InheritanceMilestones.RareFindWeight(in p, 7));
        }

        [Fact]
        public void LootLuckMilestones_ElevationAtFiveAndChestUpgradeAtTen()
        {
            var p = Character();
            var stats = SimulationEngine.LiveCombatStats(in p);
            p.Inherit_LootLuck = 4;
            var four = LootLuckBreakdown.From(in p, in stats);
            p.Inherit_LootLuck = 5;
            var five = LootLuckBreakdown.From(in p, in stats);
            Assert.Equal(four.ElevationTotal + InheritanceMilestones.ElevationPct, five.ElevationTotal, 3);

            var rng = new Random(1234);
            Assert.DoesNotContain(true, Enumerable.Range(0, 1000).Select(_ => InheritanceMilestones.UpgradesChest(9, rng)));
            int upgrades = Enumerable.Range(0, 20_000).Count(_ => InheritanceMilestones.UpgradesChest(10, rng));
            Assert.InRange(upgrades, 1_800, 2_200);
        }
    }
}
