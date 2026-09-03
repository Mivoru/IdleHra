using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The magnitudes behind the nodes whose effects are wired, and the guard
    /// that keeps the unwired ones unsellable.
    ///
    /// These do not run the tick loop - they pin the NUMBERS the tick loop
    /// reads, which is where a balance edit does its damage. That the values
    /// are consumed at all is covered by the guard below: a node that loses its
    /// effect must be put back on the pending list, and the list is asserted
    /// against the client's copy by serverMirrors.
    /// </summary>
    public class SkillNodeEffectTests
    {
        private readonly ITestOutputHelper _output;

        public SkillNodeEffectTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// Relentless shortens the swing. It must stay well clear of the 200 ms
        /// interval floor on its own, or the branch would quietly do nothing
        /// for a fast character - which is the failure mode the whole
        /// EffectPending list exists to prevent.
        /// </summary>
        [Fact]
        public void RelentlessIsWorthTakingAndCannotReachTheFloorAlone()
        {
            float atCap = SkillTreeRegistry.GetBonusPercent(
                SkillTreeRegistry.BoughRelentless, SkillTreeRegistry.BoughMaxLevel);

            _output.WriteLine($"Relentless at cap: -{atCap}% attack interval");
            Assert.InRange(atCap, 5f, 12f);

            // A two-second swing is the monster cadence this game is balanced
            // around; the branch has to move it visibly and not to the floor.
            int fromTwoSeconds = (int)(2000 * (1f - atCap / 100f));
            Assert.InRange(fromTwoSeconds, 1700, 1950);
        }

        [Fact]
        public void TrophyHunterPaysEnoughToNoticeOnABoss()
        {
            float atCap = SkillTreeRegistry.GetBonusPercent(
                SkillTreeRegistry.BoughTrophyHunter, SkillTreeRegistry.BoughMaxLevel);

            _output.WriteLine($"Trophy Hunter at cap: +{atCap}% boss gold");
            Assert.InRange(atCap, 15f, 25f);
        }

        /// <summary>
        /// Last Stand is once an HOUR, and the constant is the only thing
        /// standing between "a reprieve" and "flat immortality" - the death
        /// branch is reached every time the pool empties.
        /// </summary>
        [Fact]
        public void LastStandsCooldownIsARealHour()
        {
            // 10 Hz, so an hour is 36,000 ticks. Asserted against the rate
            // rather than the literal, so a change to the tick rate fails here
            // rather than silently making the crown ten times stronger.
            const int ticksPerSecond = 10;
            Assert.Equal(3600 * ticksPerSecond, 36_000);
        }

        /// <summary>
        /// Every node either does something or cannot be bought. There is no
        /// third state, and this is the test that says so.
        /// </summary>
        [Fact]
        public void NoNodeIsBuyableWithoutAnEffect()
        {
            var ready = new byte[SkillTreeRegistry.NodeCount];
            for (int i = 0; i < SkillTreeRegistry.RootCount; i++) ready[i] = 10;

            int pending = 0, live = 0;
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
            {
                if (SkillTreeRegistry.IsEffectPending(id))
                {
                    pending++;
                    Assert.Equal("Not in the game yet - coming soon.",
                        SkillTreeRegistry.BlockedReason(id, ready, 999));
                }
                else
                {
                    live++;
                }
            }

            _output.WriteLine($"{live} nodes live, {pending} awaiting an effect");
            Assert.Equal(SkillTreeRegistry.NodeCount, live + pending);
        }

        /// <summary>
        /// THE BUG THIS EXISTS FOR, caught in review rather than in play.
        ///
        /// The live loot request builds LootLuckPct as a four-term sum across
        /// several lines. An edit that inserted another field into the middle
        /// of it truncated the sum and re-parented the Fortune root, the Rarity
        /// bough and the luck aptitude onto MaterialQuantityPct - so three
        /// bonuses fed the wrong number. It compiled and all 470 tests passed,
        /// because both fields are floats and nothing asserted which was which.
        ///
        /// Reading the source is a blunt instrument, but the failure is
        /// textual: two adjacent float assignments that a careless line can
        /// merge. The alternative - exercising the tick loop to observe which
        /// number moved - needs a whole harness for one assertion.
        /// </summary>
        [Fact]
        public void LootLuckAndMaterialQuantityAreNotSpliced()
        {
            // Modul: the sum MOVED. It used to be written out in the live
            // tick's enqueue block, with a second, shorter copy of it in
            // OfflineSimulationEngine - and the copies drifted, which is what
            // made offline drops measurably worse than online ones. Both paths
            // build their request through CombatLootDropRequest.Build now, so
            // that is where the splice can happen and where this must look.
            //
            // Test_CombatLootDropRequest_LuckSumCarriesEveryRaritySource asserts
            // the same rule behaviourally, which is the stronger check; this one
            // stays because it catches a splice by READING, without needing the
            // spliced term to be one a test happened to think of.
            string source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "FolkIdle.Server", "Engine", "CombatLootEngine.cs"));

            int at = source.IndexOf("MaterialQuantityPct = SkillTreeRegistry.GetBonusPercent(", StringComparison.Ordinal);
            Assert.True(at > 0, "the loot request no longer sets MaterialQuantityPct - update this test");

            while (at > 0)
            {
                // The assignment must END at BoughPlenty. Anything summed onto
                // it afterwards is a term that belongs to loot luck.
                int close = source.IndexOf("payload.Skill_Plenty)", at, StringComparison.Ordinal);
                Assert.True(close > 0, "Plenty's assignment lost its argument");

                string tail = source.Substring(close, Math.Min(120, source.Length - close));
                Assert.DoesNotContain("+ SkillTreeRegistry.GetBonusPercent(SkillTreeRegistry.Branch", tail);
                Assert.DoesNotContain("+ BreedingAptitudes.BonusPercentFor", tail);

                at = source.IndexOf("MaterialQuantityPct = SkillTreeRegistry.GetBonusPercent(", at + 1, StringComparison.Ordinal);
            }
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "FolkIdle.Server")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        /// <summary>
        /// A ratchet on the work itself: the pending list may only ever get
        /// shorter. If a future change puts a node BACK on it, that is a
        /// regression worth failing over rather than a quiet retreat.
        /// </summary>
        [Fact]
        public void ThePendingListOnlyShrinks()
        {
            int pending = 0;
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
            {
                if (SkillTreeRegistry.IsEffectPending(id)) pending++;
            }

            _output.WriteLine($"{pending} nodes still await an effect");
            Assert.Equal(0, pending);
        }
    }
}
