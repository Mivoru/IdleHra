using System.Linq;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Draft mode's one rule: SkillTreeRegistry.PlanReason, which both the
    /// preview and the commit run. What these pin is that a draft is judged by
    /// replaying ordinary purchases, so it can never accept a tree one-by-one
    /// buying would refuse.
    /// </summary>
    public class SkillTreeDraftTests
    {
        private static byte[] Empty() => new byte[SkillTreeRegistry.NodeCount];

        [Fact]
        public void A_whole_limb_in_one_draft_costs_what_buying_it_level_by_level_costs()
        {
            var target = Empty();
            target[SkillTreeRegistry.BranchCritChance] = 10;
            target[SkillTreeRegistry.BoughGuile] = 8;
            target[SkillTreeRegistry.CrownDoubleStrike] = 1;

            string? reason = SkillTreeRegistry.PlanReason(Empty(), target, 1000, out int cost);

            Assert.Null(reason);
            Assert.Equal(SkillTreeRegistry.TotalCostForFullLimb(SkillTreeRegistry.BranchCritChance), cost);
        }

        [Fact]
        public void A_draft_takes_its_crown_before_the_bough_under_it_is_ready_only_if_the_bough_is_in_the_draft()
        {
            var target = Empty();
            target[SkillTreeRegistry.CrownDoubleStrike] = 1;

            Assert.NotNull(SkillTreeRegistry.PlanReason(Empty(), target, 1000, out _));
        }

        [Fact]
        public void Both_boughs_of_one_fork_are_refused()
        {
            var target = Empty();
            target[SkillTreeRegistry.BranchCritChance] = 5;
            target[SkillTreeRegistry.BoughGuile] = 1;
            target[SkillTreeRegistry.BoughRelentless] = 1;

            Assert.NotNull(SkillTreeRegistry.PlanReason(Empty(), target, 1000, out _));
        }

        [Fact]
        public void A_draft_that_lowers_a_node_is_refused_and_points_short_is_refused()
        {
            var current = Empty();
            current[SkillTreeRegistry.BranchLootRarity] = 3;

            Assert.NotNull(SkillTreeRegistry.PlanReason(current, Empty(), 1000, out _));

            var target = (byte[])current.Clone();
            target[SkillTreeRegistry.BranchLootRarity] = 10;
            Assert.NotNull(SkillTreeRegistry.PlanReason(current, target, 2, out _));
        }

        [Fact]
        public void An_unchanged_draft_is_legal_and_free()
        {
            Assert.Null(SkillTreeRegistry.PlanReason(Empty(), Empty(), 0, out int cost));
            Assert.Equal(0, cost);
        }

        [Fact]
        public void Every_node_has_effect_text_and_a_layout_cell_of_its_own()
        {
            var cells = Enumerable.Range(0, SkillTreeRegistry.NodeCount)
                .Select(SkillTreeRegistry.LayoutOf)
                .ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count());

            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
            {
                Assert.False(string.IsNullOrWhiteSpace(SkillTreeRegistry.EffectText(id, 1)), SkillTreeRegistry.GetName(id));
            }

            Assert.NotNull(NetworkBroadcastSystem.DescribeSkillTree());
            Assert.All(SkillTreeRegistry.Nodes, n => Assert.False(string.IsNullOrEmpty(n.Icon)));
        }

        /// <summary>
        /// The table reproduces the ring rules it replaced, cost for cost and
        /// reason for reason, so slice 2.1 changes no player-visible rule.
        /// </summary>
        [Fact]
        public void The_node_table_keeps_todays_costs_caps_and_reasons()
        {
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
            {
                var ring = SkillTreeRegistry.RingOf(id);
                int max = ring == SkillTreeRegistry.Ring.Root ? 10 : ring == SkillTreeRegistry.Ring.Bough ? 8 : 1;
                Assert.Equal(max, SkillTreeRegistry.MaxLevelOf(id));
                for (int l = 0; l < max; l++)
                {
                    int want = ring == SkillTreeRegistry.Ring.Root ? (l / 5) + 1 : ring == SkillTreeRegistry.Ring.Bough ? 2 : 12;
                    Assert.Equal(want, SkillTreeRegistry.GetUpgradeCost(id, l));
                }
            }

            var levels = new byte[SkillTreeRegistry.NodeCount];
            Assert.Equal("Needs Precision at 5.", SkillTreeRegistry.BlockedReason(SkillTreeRegistry.BoughGuile, levels, 99));
            levels[SkillTreeRegistry.BranchCritChance] = 5;
            levels[SkillTreeRegistry.BoughRelentless] = 1;
            Assert.Equal("Relentless was taken instead. One branch per fork.", SkillTreeRegistry.BlockedReason(SkillTreeRegistry.BoughGuile, levels, 99));
            Assert.Equal("Needs a branch of Precision at 5.", SkillTreeRegistry.BlockedReason(SkillTreeRegistry.CrownDoubleStrike, levels, 99));
            Assert.Equal(5 + 2, SkillTreeRegistry.PointsInColumn(levels, 2));
        }

        [Fact]
        public void A_payload_copy_does_not_share_its_ranks_with_the_original()
        {
            var live = new TickStatePayload();
            var copy = live;
            FolkIdle.Server.Domain.Combat.SimulationEngine.SetSkillTreeLevel(ref copy, SkillTreeRegistry.CrownDoubleStrike, 1);
            Assert.Equal(0, live.Skill_DoubleStrike);
            Assert.Equal(1, copy.Skill_DoubleStrike);
        }
    }
}
