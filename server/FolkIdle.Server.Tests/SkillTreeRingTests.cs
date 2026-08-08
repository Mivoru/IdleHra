using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The three rings, and the doors that close.
    ///
    /// The flat five-branch tree it replaced looked like a choice without being
    /// one: every branch was a pure bonus on a nearly flat cost curve, so the
    /// best play was always "pour into the strongest, then the next". These
    /// tests exist to pin the parts that make it a real decision - the fork
    /// exclusion, the prerequisites, and the budget gap that keeps all five
    /// crowns out of one season's reach.
    /// </summary>
    public class SkillTreeRingTests
    {
        private readonly ITestOutputHelper _output;

        public SkillTreeRingTests(ITestOutputHelper output) => _output = output;

        private static byte[] Levels(params (int NodeId, int Level)[] set)
        {
            var levels = new byte[SkillTreeRegistry.NodeCount];
            foreach (var (id, level) in set) levels[id] = (byte)level;
            return levels;
        }

        [Fact]
        public void TheTreeIsFiveRootsTenBoughsAndFiveCrowns()
        {
            int roots = 0, boughs = 0, crowns = 0;
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
            {
                switch (SkillTreeRegistry.RingOf(id))
                {
                    case SkillTreeRegistry.Ring.Root: roots++; break;
                    case SkillTreeRegistry.Ring.Bough: boughs++; break;
                    default: crowns++; break;
                }
            }

            Assert.Equal(5, roots);
            Assert.Equal(10, boughs);
            Assert.Equal(5, crowns);
        }

        [Fact]
        public void EveryNodeHangsFromExactlyOneRoot()
        {
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
            {
                int root = SkillTreeRegistry.RootOf(id);
                Assert.InRange(root, 0, SkillTreeRegistry.RootCount - 1);
            }

            for (int root = 0; root < SkillTreeRegistry.RootCount; root++)
            {
                var (a, b) = SkillTreeRegistry.BoughsOfRoot(root);
                Assert.Equal(root, SkillTreeRegistry.RootOf(a));
                Assert.Equal(root, SkillTreeRegistry.RootOf(b));
                Assert.Equal(root, SkillTreeRegistry.RootOf(SkillTreeRegistry.CrownOfRoot(root)));
            }
        }

        [Fact]
        public void SiblingLookupIsSymmetricAndNeverItself()
        {
            for (int id = SkillTreeRegistry.FirstBoughId; id < SkillTreeRegistry.FirstCrownId; id++)
            {
                int sibling = SkillTreeRegistry.SiblingBoughOf(id);
                Assert.NotEqual(id, sibling);
                Assert.Equal(id, SkillTreeRegistry.SiblingBoughOf(sibling));
            }

            Assert.Equal(-1, SkillTreeRegistry.SiblingBoughOf(0));
            Assert.Equal(-1, SkillTreeRegistry.SiblingBoughOf(SkillTreeRegistry.FirstCrownId));
        }

        /// <summary>
        /// THE RULE THE WHOLE REDESIGN EXISTS FOR. Taking one side of a fork
        /// forecloses the other for the season - without this the tree is a
        /// shopping list again.
        /// </summary>
        [Fact]
        public void TakingOneBoughLocksItsSibling()
        {
            // Cruelty, whose two boughs are both wired - so what is refused
            // here is the fork rule rather than the not-yet-implemented guard.
            var levels = Levels((SkillTreeRegistry.BranchCritDamage, 5), (SkillTreeRegistry.BoughBloodthirst, 1));

            Assert.Null(SkillTreeRegistry.BlockedReason(SkillTreeRegistry.BoughBloodthirst, levels, 99));

            string? blocked = SkillTreeRegistry.BlockedReason(SkillTreeRegistry.BoughFortitude, levels, 99);
            Assert.NotNull(blocked);
            Assert.Contains("Bloodthirst", blocked);
        }

        [Fact]
        public void ABoughNeedsItsRootAtFive()
        {
            Assert.NotNull(SkillTreeRegistry.BlockedReason(
                SkillTreeRegistry.BoughRarity, Levels((SkillTreeRegistry.BranchLootRarity, 4)), 99));

            Assert.Null(SkillTreeRegistry.BlockedReason(
                SkillTreeRegistry.BoughRarity, Levels((SkillTreeRegistry.BranchLootRarity, 5)), 99));
        }

        [Fact]
        public void ACrownNeedsABoughAtFiveAndEitherSideCounts()
        {
            // Scholar over Insight - a crown whose effect is wired.
            Assert.NotNull(SkillTreeRegistry.BlockedReason(
                SkillTreeRegistry.CrownScholar,
                Levels((SkillTreeRegistry.BranchXpGain, 5), (SkillTreeRegistry.BoughHarvest, 4)), 99));

            foreach (int bough in new[] { SkillTreeRegistry.BoughCraft, SkillTreeRegistry.BoughHarvest })
            {
                Assert.Null(SkillTreeRegistry.BlockedReason(
                    SkillTreeRegistry.CrownScholar,
                    Levels((SkillTreeRegistry.BranchXpGain, 5), (bough, 5)), 99));
            }
        }

        [Fact]
        public void NothingGatesARootExceptPointsAndItsCap()
        {
            for (int root = 0; root < SkillTreeRegistry.RootCount; root++)
            {
                Assert.Null(SkillTreeRegistry.BlockedReason(root, Levels(), 99));
                Assert.Equal("Already at its limit.",
                    SkillTreeRegistry.BlockedReason(root, Levels((root, SkillTreeRegistry.RootMaxLevel)), 99));
            }
        }

        [Fact]
        public void ShortOfPointsSaysBothNumbers()
        {
            string? reason = SkillTreeRegistry.BlockedReason(
                SkillTreeRegistry.CrownScholar,
                Levels((SkillTreeRegistry.BranchXpGain, 5), (SkillTreeRegistry.BoughHarvest, 5)), 3);

            Assert.Equal("Costs 12 points; you have 3.", reason);
        }

        /// <summary>
        /// The budget the design rests on. A season pays about a hundred
        /// points; if five full limbs ever came within reach of that, the tree
        /// would have stopped being a choice.
        /// </summary>
        [Fact]
        public void OneLimbCostsFortyThreeAndAllFiveAreOutOfSeasonReach()
        {
            int limb = SkillTreeRegistry.TotalCostForFullLimb(SkillTreeRegistry.BranchLootRarity);
            _output.WriteLine($"root {SkillTreeRegistry.TotalCostForFullNode(0)}"
                + $" + bough {SkillTreeRegistry.TotalCostForFullNode(SkillTreeRegistry.BoughPlenty)}"
                + $" + crown {SkillTreeRegistry.CrownCost} = {limb}");

            Assert.Equal(43, limb);

            int allFive = 0;
            for (int root = 0; root < SkillTreeRegistry.RootCount; root++)
            {
                allFive += SkillTreeRegistry.TotalCostForFullLimb(root);
            }

            _output.WriteLine($"all five limbs: {allFive} points against ~100 a season");
            Assert.Equal(215, allFive);
            Assert.True(allFive > 200, "five full limbs must stay far beyond one season's points");
        }

        // Modul: the "some nodes await an effect" test lived here and asked to
        // be deleted when the last one landed. It has. What replaced it is
        // stronger and lives in SkillNodeEffectTests: the pending list must now
        // be EMPTY, so a node cannot be quietly disabled instead of fixed.

        [Fact]
        public void EveryNodeIsNamedAndDescribed()
        {
            var names = new HashSet<string>();
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
            {
                string name = SkillTreeRegistry.GetName(id);
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.True(names.Add(name), $"duplicate node name: {name}");
                Assert.True(SkillTreeRegistry.GetBlurb(id).Length > 20, $"node {id} has no real blurb");
            }
        }

        /// <summary>
        /// A crown's magnitude is flat, not per-level. Multiplying by the level
        /// is a no-op today and a bug the day a crown gains a second one.
        /// </summary>
        [Fact]
        public void ACrownsMagnitudeDoesNotScaleWithLevel()
        {
            Assert.Equal(
                SkillTreeRegistry.GetBonusTenthsOfPercent(SkillTreeRegistry.CrownScholar, 1),
                SkillTreeRegistry.GetBonusTenthsOfPercent(SkillTreeRegistry.CrownScholar, 5));
        }

        /// <summary>
        /// First Blood softens the first-clear penalty and can never delete it.
        /// The wall is the mechanic; making it survivable is the reward.
        /// </summary>
        [Fact]
        public void FirstBloodSoftensTheBossWallWithoutRemovingIt()
        {
            float atCap = SkillTreeRegistry.GetBonusPercent(
                SkillTreeRegistry.BoughFirstBlood, SkillTreeRegistry.BoughMaxLevel);

            _output.WriteLine($"First Blood at cap relieves {atCap}% of the penalty");
            Assert.InRange(atCap, 30f, 33f);
            Assert.True(atCap < 100f, "First Blood must never erase the first-clear penalty");
        }
    }
}
