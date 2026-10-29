using System;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The tool affixes gather_yield_pct and gather_rare_find_pct were summed by
    /// ToolLoadoutResolver, stored on the payload and shipped to the client, and
    /// read by no simulation code (STATS_REFERENCE.md §16, 2026-10-29). These
    /// hold the EFFECT: harvest rolls and the rare-favouring luck weight.
    /// </summary>
    public class ToolAffixWiringTests
    {
        public ToolAffixWiringTests()
        {
            ContentRegistry.Initialize();
        }

        private static TickStatePayload Character()
            => new TickStatePayload { CurrentLevel = 40, CON = 50, STR = 50, DEX = 50, LCK = 25 };

        private static GatheringNodeDefinition FirstNode(int profession)
            => ContentRegistry.GatheringNodes.ToArray().First(n => n.ProfessionType == profession);

        [Fact]
        public void A_tool_yield_affix_adds_the_percent_its_tooltip_shows()
        {
            var bare = Character();
            var tooled = bare;
            tooled.ToolAxeYieldPct = 97; // a Legendary roll's centre: "+9.7%" (node is woodcutting)
            var node = FirstNode(0);

            var bareYield = SimulationEngine.GatheringYieldFor(ref bare, in node, 100);
            var tooledYield = SimulationEngine.GatheringYieldFor(ref tooled, in node, 100);

            Assert.Equal(bareYield.MultiplierPct + 9, tooledYield.MultiplierPct);
        }

        [Fact]
        public void A_rare_find_affix_raises_the_rare_favouring_luck_weight()
        {
            var bare = Character();
            var tooled = bare;
            tooled.ToolRodRareFindPct = 1180; // ten Legendary rolls' worth, so the weight moves visibly (node is fishing)
            var node = FirstNode(2);

            var bareYield = SimulationEngine.GatheringYieldFor(ref bare, in node, 100);
            var tooledYield = SimulationEngine.GatheringYieldFor(ref tooled, in node, 100);

            Assert.True(tooledYield.LuckWeightBonus > bareYield.LuckWeightBonus,
                $"luck weight {bareYield.LuckWeightBonus} -> {tooledYield.LuckWeightBonus}");
            Assert.Equal(bareYield.MultiplierPct, tooledYield.MultiplierPct);
        }

        [Fact]
        public void A_tools_affixes_pay_only_its_own_profession()
        {
            // An axe's yield and rare find used to be summed with the pickaxe's
            // and the rod's, so they paid on every harvest of every profession.
            var axeOnly = Character();
            axeOnly.ToolAxeYieldPct = 400;
            axeOnly.ToolAxeRareFindPct = 1180;
            var bare = Character();

            var woodcutting = FirstNode(0);
            var mining = FirstNode(1);
            var fishing = FirstNode(2);

            Assert.True(SimulationEngine.GatheringYieldFor(ref axeOnly, in woodcutting, 100).MultiplierPct
                > SimulationEngine.GatheringYieldFor(ref bare, in woodcutting, 100).MultiplierPct);

            foreach (var other in new[] { mining, fishing })
            {
                var node = other;
                var withAxe = SimulationEngine.GatheringYieldFor(ref axeOnly, in node, 100);
                var without = SimulationEngine.GatheringYieldFor(ref bare, in node, 100);
                Assert.Equal(without.MultiplierPct, withAxe.MultiplierPct);
                Assert.Equal(without.LuckWeightBonus, withAxe.LuckWeightBonus);
            }
        }

        [Fact]
        public void The_resolver_keeps_each_tools_affixes_apart_and_the_wire_sum_whole()
        {
            var axe = new EquipmentInstance { Id = 1, BaseItemId = "voidbark_axe_tool", QualityTier = 13, AffixPayload = "{\"gather_yield_pct@5\":45}" };
            var rod = new EquipmentInstance { Id = 2, BaseItemId = "birch_fishing_rod_tool", QualityTier = 1, AffixPayload = "{\"gather_yield_pct@2\":20,\"gather_rare_find_pct@3\":25}" };
            var character = new FolkIdle.Server.Models.CharacterRecord { EquippedAxeId = 1, EquippedRodId = 2 };
            var loadout = ToolLoadoutResolver.Resolve(character, new System.Collections.Generic.Dictionary<long, FolkIdle.Server.Models.EquipmentInstance> { { 1, axe }, { 2, rod } });

            Assert.Equal(45, loadout.AxeYieldPct);
            Assert.Equal(0, loadout.PickaxeYieldPct);
            Assert.Equal(20, loadout.RodYieldPct);
            Assert.Equal(25, loadout.RodRareFindPct);
            Assert.Equal(65, loadout.GatherYieldPct);
            Assert.Equal(25, loadout.RareFindPct);
        }

        [Fact]
        public void The_tool_yield_affix_pays_offline_too()
        {
            var node = FirstNode(0);
            var bare = Character();
            bare.ActiveActivityId = node.ActivityId;
            var tooled = bare;
            tooled.ToolAxeYieldPct = 400; // +40 points, so the offline draw moves clearly

            var bareProjection = OfflineSimulationEngine.CalculateGatheringProjection(ref bare, node, 8 * 3600, new Random(5));
            var tooledProjection = OfflineSimulationEngine.CalculateGatheringProjection(ref tooled, node, 8 * 3600, new Random(5));

            Assert.True(tooledProjection.Drops > bareProjection.Drops * 1.25,
                $"offline drops {bareProjection.Drops} -> {tooledProjection.Drops}");
        }
    }
}
