using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    // Modul: WHAT A WHOLE SEASON OF POINTS IS WORTH.
    //
    // The four active skills this replaced were never put in numbers until the
    // day they were removed, and the number was +90% damage - +136% with the
    // status synergy - for a player willing to click every three seconds. It
    // had been in the game for months.
    //
    // So the tree gets its arithmetic written down on the way in rather than on
    // the way out. The brief was tens of percent for a season's investment, and
    // this is where that claim is checked rather than asserted in a commit
    // message.
    public class SkillTreeTests
    {
        private readonly ITestOutputHelper _output;

        public SkillTreeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Test_SkillTree_ASeasonBuysTwoBranchesDeepOrFiveShallow()
        {
            // A LIMB, not a bare branch. The tree grew two rings above the
            // roots, and measuring only the root would be measuring the
            // cheapest third of what a player actually buys.
            int fullBranch = SkillTreeRegistry.TotalCostForFullLimb(SkillTreeRegistry.BranchLootRarity);
            int everything = 0;
            for (int root = 0; root < SkillTreeRegistry.RootCount; root++)
            {
                everything += SkillTreeRegistry.TotalCostForFullLimb(root);
            }

            // A season is a hundred levels, so a hundred points.
            const int pointsPerSeason = 100;

            _output.WriteLine($"one limb to cap:  {fullBranch} points");
            _output.WriteLine($"all five limbs:   {everything} points");
            _output.WriteLine($"a season pays:    {pointsPerSeason} points");

            // THE CHOICE HAS TO BE REAL. If a season paid for everything the
            // tree would be a formality with a delay on it, and if it paid for
            // barely one branch the other four would be decoration.
            Assert.True(everything > pointsPerSeason * 2,
                "a season must not buy the whole tree, or there is no choice in it");
            Assert.True(fullBranch <= pointsPerSeason,
                "a season must be able to take at least one branch all the way");
        }

        [Fact]
        public void Test_SkillTree_APriceThatRises()
        {
            // Flat pricing makes the last level as cheap as the first, which
            // makes "how deep" a question with no cost attached to it.
            Assert.Equal(1, SkillTreeRegistry.GetUpgradeCost(0));
            Assert.Equal(1, SkillTreeRegistry.GetUpgradeCost(4));
            Assert.Equal(2, SkillTreeRegistry.GetUpgradeCost(5));
            Assert.Equal(2, SkillTreeRegistry.GetUpgradeCost(9));

            // Nothing costs anything past the cap, and nothing is free below it.
            Assert.Equal(0, SkillTreeRegistry.GetUpgradeCost(SkillTreeRegistry.RootMaxLevel));
            for (int level = 0; level < SkillTreeRegistry.RootMaxLevel; level++)
            {
                Assert.True(SkillTreeRegistry.GetUpgradeCost(level) > 0);
            }
        }

        [Fact]
        public void Test_SkillTree_EveryBranchIsTensOfPercentAndNoneIsAMultiple()
        {
            _output.WriteLine("branch                  at cap");
            for (int branch = 0; branch < SkillTreeRegistry.BranchCount; branch++)
            {
                float atCap = SkillTreeRegistry.GetBonusPercent(branch, SkillTreeRegistry.RootMaxLevel);
                _output.WriteLine($"{SkillTreeRegistry.GetName(branch),-20} {atCap,7:F1}%");

                // The brief, checked: tens of percent for a whole season in one
                // branch. Nothing here may double anything.
                // Halved with the cap: the ceiling moved up into the boughs
                // and crowns, which SkillTreeRingTests covers.
                Assert.InRange(atCap, 2.0f, 30.0f);
            }
        }

        [Fact]
        public void Test_SkillTree_TheCritBranchesTogetherAreWorthAboutAFifth()
        {
            // The two branches that touch damage are the ones worth pricing
            // together, because a player who wants damage takes both and a
            // season pays for exactly that.
            //
            // A levelled character runs about 10% crit from DEX and the base
            // multiplier is 1.5x. Expected damage is
            // 1 + chance * (multiplier - 1), so this compares the before and
            // after of a hundred points spent on nothing else.
            const float baseCritChance = 10f;
            const float baseCritMultiplier = 1.5f;

            float treeChance = baseCritChance
                + SkillTreeRegistry.GetBonusPercent(SkillTreeRegistry.BranchCritChance, SkillTreeRegistry.RootMaxLevel);
            float treeMultiplier = baseCritMultiplier
                + (SkillTreeRegistry.GetBonusPercent(SkillTreeRegistry.BranchCritDamage, SkillTreeRegistry.RootMaxLevel) / 100f);

            double before = 1.0 + (baseCritChance / 100.0) * (baseCritMultiplier - 1.0);
            double after = 1.0 + (treeChance / 100.0) * (treeMultiplier - 1.0);
            double gain = (after / before) - 1.0;

            _output.WriteLine($"crit {baseCritChance}% x{baseCritMultiplier} -> {treeChance}% x{treeMultiplier}");
            _output.WriteLine($"damage over time: {gain:P1}");

            // Tens of percent, not a multiple. The mechanic this replaced was
            // +90% and it was reached by clicking rather than by spending a
            // season, which is the whole distinction.
            Assert.InRange(gain, 0.02, 0.40);
        }

        [Fact]
        public void Test_SkillTree_TheXpBranchCannotQuietlyRewriteTheSeason()
        {
            // Insight is the only branch that shortens the season directly, so
            // it is the one where a generous number would undo the curve
            // without appearing to touch it.
            float atCap = SkillTreeRegistry.GetBonusPercent(SkillTreeRegistry.BranchXpGain, SkillTreeRegistry.RootMaxLevel);

            _output.WriteLine($"a season of Insight shortens the next one by {atCap:F1}%");
            Assert.InRange(atCap, 1.0f, 12.0f);
        }

        [Fact]
        public void Test_SkillTree_LevelsAreBoundedAndBranchesAreNamed()
        {
            Assert.False(SkillTreeRegistry.IsValidBranch(-1));
            Assert.False(SkillTreeRegistry.IsValidBranch(SkillTreeRegistry.BranchCount));

            for (int branch = 0; branch < SkillTreeRegistry.BranchCount; branch++)
            {
                Assert.True(SkillTreeRegistry.IsValidBranch(branch));
                Assert.NotEqual("Unknown", SkillTreeRegistry.GetName(branch));
                Assert.NotEqual("Unknown", SkillTreeRegistry.GetBlurb(branch));

                // Past the cap pays no more than at it - a level 40 row written
                // by hand into the database must not pay double.
                Assert.Equal(
                    SkillTreeRegistry.GetBonusTenthsOfPercent(branch, SkillTreeRegistry.RootMaxLevel),
                    SkillTreeRegistry.GetBonusTenthsOfPercent(branch, SkillTreeRegistry.RootMaxLevel * 2));

                Assert.Equal(0, SkillTreeRegistry.GetBonusTenthsOfPercent(branch, 0));
            }
        }
    }
}
