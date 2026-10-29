using System;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
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
            tooled.ToolGatherYieldPct = 97; // a Legendary roll's centre: "+9.7%"
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
            tooled.ToolRareFindPct = 1180; // ten Legendary rolls' worth, so the weight moves visibly
            var node = FirstNode(2);

            var bareYield = SimulationEngine.GatheringYieldFor(ref bare, in node, 100);
            var tooledYield = SimulationEngine.GatheringYieldFor(ref tooled, in node, 100);

            Assert.True(tooledYield.LuckWeightBonus > bareYield.LuckWeightBonus,
                $"luck weight {bareYield.LuckWeightBonus} -> {tooledYield.LuckWeightBonus}");
            Assert.Equal(bareYield.MultiplierPct, tooledYield.MultiplierPct);
        }

        [Fact]
        public void The_tool_yield_affix_pays_offline_too()
        {
            var node = FirstNode(0);
            var bare = Character();
            bare.ActiveActivityId = node.ActivityId;
            var tooled = bare;
            tooled.ToolGatherYieldPct = 400; // +40 points, so the offline draw moves clearly

            var bareProjection = OfflineSimulationEngine.CalculateGatheringProjection(ref bare, node, 8 * 3600, new Random(5));
            var tooledProjection = OfflineSimulationEngine.CalculateGatheringProjection(ref tooled, node, 8 * 3600, new Random(5));

            Assert.True(tooledProjection.Drops > bareProjection.Drops * 1.25,
                $"offline drops {bareProjection.Drops} -> {tooledProjection.Drops}");
        }
    }
}
