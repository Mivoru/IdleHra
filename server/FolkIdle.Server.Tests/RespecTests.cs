using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The way back out of a fork.
    ///
    /// Ring 2 forks and taking one side locks the other for a NINETY-DAY
    /// season, so a respec is not a convenience here - without one a misclick
    /// costs three months. It is limited for the opposite reason: free and
    /// unlimited would delete the exclusivity that is the only real choice the
    /// tree has.
    /// </summary>
    public class RespecTests
    {
        [Fact]
        public void TheFirstRespecOfASeasonIsFree()
        {
            Assert.Null(SkillTreeEngine.RespecBlockedReason(freeRespecUsed: false, paidGrants: 0));
        }

        [Fact]
        public void TheSecondNeedsAGrant()
        {
            Assert.NotNull(SkillTreeEngine.RespecBlockedReason(freeRespecUsed: true, paidGrants: 0));
            Assert.Null(SkillTreeEngine.RespecBlockedReason(freeRespecUsed: true, paidGrants: 1));
        }

        /// <summary>
        /// The refusal has to name the reason. "Disabled" teaches a player
        /// nothing, and this one is recoverable - they can buy a grant.
        /// </summary>
        [Fact]
        public void TheRefusalSaysWhy()
        {
            string? reason = SkillTreeEngine.RespecBlockedReason(true, 0);
            Assert.Contains("free respec", reason);
        }

        /// <summary>
        /// What a full tree refunds, computed the way RespecAsync computes it.
        /// A stored "points spent" column would be a second source of truth
        /// for something derivable, and the first time the cost curve moved
        /// the two would disagree - in someone's favour.
        /// </summary>
        [Fact]
        public void ARespecReturnsExactlyWhatWasPaid()
        {
            int spent = 0;
            for (int level = 0; level < SkillTreeRegistry.RootMaxLevel; level++)
            {
                spent += SkillTreeRegistry.GetUpgradeCost(SkillTreeRegistry.BranchLootRarity, level);
            }
            for (int level = 0; level < SkillTreeRegistry.BoughMaxLevel; level++)
            {
                spent += SkillTreeRegistry.GetUpgradeCost(SkillTreeRegistry.BoughRarity, level);
            }
            spent += SkillTreeRegistry.CrownCost;

            Assert.Equal(SkillTreeRegistry.TotalCostForFullLimb(SkillTreeRegistry.BranchLootRarity), spent);
        }
    }

    /// <summary>
    /// What the season rollover takes back.
    ///
    /// PlayerSkillTreeNode has always DOCUMENTED that its levels reset with the
    /// season - "a tree that survived would be paid for twice" - and nothing
    /// implemented it. Neither the rows nor AvailableSkillPoints were cleared,
    /// so a player finished season one with ~100 points spent, re-levelled in
    /// season two and spent ~100 MORE on a tree still standing. By the third
    /// season the whole 215-point tree was bought and the choice was gone for
    /// good.
    ///
    /// These assert the arithmetic that makes the reset necessary, which is the
    /// part a future edit could quietly break; that the statements run is
    /// covered by the rollover's own integration test.
    /// </summary>
    public class SeasonResetsTheTreeTests
    {
        private const int PointsPerSeason = 100;

        [Fact]
        public void ThreeSeasonsOfPointsWouldBuyTheWholeTree()
        {
            int wholeTree = 0;
            for (int root = 0; root < SkillTreeRegistry.RootCount; root++)
            {
                wholeTree += SkillTreeRegistry.TotalCostForFullLimb(root);
            }

            Assert.True(PointsPerSeason * 3 > wholeTree,
                "if three seasons of points no longer buys everything, the reset may look optional - it is not");
        }

        [Fact]
        public void OneSeasonOfPointsDoesNot()
        {
            int wholeTree = 0;
            for (int root = 0; root < SkillTreeRegistry.RootCount; root++)
            {
                wholeTree += SkillTreeRegistry.TotalCostForFullLimb(root);
            }

            Assert.True(wholeTree > PointsPerSeason * 2,
                "a single season must never approach the whole tree, or there is no choice in it");
        }
    }
}
