using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The Book of Deeds: five chapters, and the two rules they were written
    /// under.
    ///
    /// These matter more than a checklist usually would, because completing a
    /// chapter awards a Seal and **a Seal grants +2 permanent skill points
    /// every season, forever**. A deed with an unreachable threshold is not a
    /// cosmetic problem - it is a permanent reward nobody can collect, which is
    /// exactly what the old tiered achievements were (Treasury tier IV wanted
    /// 2.5 BILLION gold against a measured 53M per region-4 season).
    /// </summary>
    public class DeedRegistryTests
    {
        private readonly ITestOutputHelper _output;

        public DeedRegistryTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void ThereAreFiveChaptersAndNoneIsEmpty()
        {
            Assert.Equal(DeedRegistry.ChapterCount, DeedRegistry.Chapters.Count);

            for (int i = 0; i < DeedRegistry.Chapters.Count; i++)
            {
                var chapter = DeedRegistry.Chapters[i];
                Assert.Equal(i + 1, chapter.Index);
                Assert.NotEmpty(chapter.Deeds);
                Assert.False(string.IsNullOrWhiteSpace(chapter.Title));
                Assert.False(string.IsNullOrWhiteSpace(chapter.Reward));
            }
        }

        /// <summary>
        /// THE FIRST NON-NEGOTIABLE: every deed shows a live x / y. The old
        /// achievements returned 0 from GetNextTierTarget for most ids and the
        /// client rendered "0 / MAX" - a deed without a number does not exist
        /// to the player.
        ///
        /// A zero target would divide by zero in the progress meter; a negative
        /// one would never complete.
        /// </summary>
        [Fact]
        public void EveryDeedHasAPositiveTargetAndAUniqueId()
        {
            var ids = new System.Collections.Generic.HashSet<string>();

            foreach (var chapter in DeedRegistry.Chapters)
            {
                foreach (var deed in chapter.Deeds)
                {
                    Assert.True(deed.Target > 0, $"{deed.Id} has target {deed.Target}");
                    Assert.False(string.IsNullOrWhiteSpace(deed.Title), $"{deed.Id} has no title");
                    Assert.False(string.IsNullOrWhiteSpace(deed.Body), $"{deed.Id} has no instruction");
                    Assert.False(string.IsNullOrWhiteSpace(deed.Screen), $"{deed.Id} names no screen");
                    Assert.True(ids.Add(deed.Id), $"{deed.Id} appears twice");
                }
            }
        }

        /// <summary>
        /// Progress must be MONOTONIC in the account's own numbers and never
        /// exceed its target - the client draws `current / target` as a bar
        /// width, so an over-count is a bar that runs off the card.
        /// </summary>
        [Fact]
        public void NoDeedReportsMoreProgressThanItsTarget()
        {
            // An account that has done absurdly much of everything.
            var maxed = new DeedContext(
                Level: 9999, HasWeaponEquipped: true, LarderStocked: 3, WoodStock: 9_999_999,
                ItemsCrafted: 999_999, TotalKills: 9_999_999, BossesSlain: 999, RegionsCompleted: 5,
                HighestUnlockedRegion: 5, DefeatedRegionBossMask: 0b11111, ForgeFusions: 999_999,
                AffixRerolls: 999_999, HighestRarityOwned: 14, LargestActiveSetBonus: 8,
                ForgeLevel: 99, InnLevel: 99, VillageBuildingLevelTotal: 999, WarehouseLevel: 99,
                GatheringMasteryTotal: 9999, LowestRegionOneKillCount: 999_999,
                BestCodexRegionCompletion: 1, BestSeasonRank: 1, ChildrenBred: 99,
                EpicChildrenBred: 9, BestAptitudeTotal: 200);

            foreach (var chapter in DeedRegistry.Chapters)
            {
                foreach (var deed in chapter.Deeds)
                {
                    long progress = deed.Progress(maxed);
                    Assert.True(progress <= deed.Target, $"{deed.Id} reports {progress} against a target of {deed.Target}");
                }
            }

            // And with everything maxed, every chapter is finished - a deed
            // that cannot complete even here is unreachable, which is the
            // failure the old achievement thresholds actually shipped.
            foreach (var chapter in DeedRegistry.Chapters)
            {
                Assert.True(DeedRegistry.IsComplete(chapter, maxed), $"chapter {chapter.Index} cannot be completed at all");
            }
        }

        /// <summary>
        /// A brand new account has finished nothing. Sounds trivial; it is the
        /// assertion that catches a deed whose progress function reads a field
        /// that happens to be non-zero at creation, which would hand out a Seal
        /// on the first login.
        /// </summary>
        [Fact]
        public void AFreshAccountHasCompletedNoChapter()
        {
            var fresh = default(DeedContext);

            foreach (var chapter in DeedRegistry.Chapters)
            {
                Assert.False(DeedRegistry.IsComplete(chapter, fresh), $"chapter {chapter.Index} completes itself on a new account");
            }
        }

        /// <summary>
        /// THE SECOND NON-NEGOTIABLE: thresholds calibrated against measured
        /// pacing. ProgressionRateTests measures one hour of play as 53 kills;
        /// the biggest kill target here is 5,000, which is about a hundred
        /// hours - a season-long chase, not two seasons of nothing else.
        /// </summary>
        [Fact]
        public void TheLongestDeedIsAboutAHundredHoursOfPlay()
        {
            const int killsPerHour = 53;

            var killDeeds = DeedRegistry.Chapters
                .SelectMany(c => c.Deeds)
                .Where(d => d.Id == "five-thousand" || d.Id == "region-one-hundred")
                .ToList();

            Assert.NotEmpty(killDeeds);

            long longest = killDeeds.Max(d => d.Target);
            double hours = (double)longest / killsPerHour;
            _output.WriteLine($"the longest kill deed is {longest} kills, about {hours:0} hours at the measured rate");

            Assert.True(hours < 200, $"{longest} kills is {hours:0} hours - past a season of doing nothing else");
        }

        // --- Seals ------------------------------------------------------------

        /// <summary>
        /// The coupling the whole document turns on: five Seals is +10 skill
        /// points against a base of about 100 a season. Felt, and nowhere near
        /// decisive - a Seal worth ten points would make the tree a function of
        /// the checklist rather than of the season.
        /// </summary>
        [Fact]
        public void FiveSealsAreWorthTenPointsASeason()
        {
            int all = 0;
            for (int chapter = 1; chapter <= DeedRegistry.ChapterCount; chapter++)
            {
                Assert.False(DeedRegistry.HasSeal(all, chapter));
                all = DeedRegistry.WithSeal(all, chapter);
                Assert.True(DeedRegistry.HasSeal(all, chapter));
            }

            Assert.Equal(DeedRegistry.ChapterCount, DeedRegistry.SealCount(all));
            Assert.Equal(10, DeedRegistry.SkillPointsFrom(all));
            Assert.Equal(0, DeedRegistry.SkillPointsFrom(0));
        }

        /// <summary>
        /// Awarding one Seal must not award another. A mask written with the
        /// wrong shift would light two bits and pay twice, and nothing else
        /// would ever notice.
        /// </summary>
        [Fact]
        public void EachSealOccupiesItsOwnBit()
        {
            for (int chapter = 1; chapter <= DeedRegistry.ChapterCount; chapter++)
            {
                int mask = DeedRegistry.WithSeal(0, chapter);
                Assert.Equal(1, DeedRegistry.SealCount(mask));

                for (int other = 1; other <= DeedRegistry.ChapterCount; other++)
                {
                    if (other == chapter) continue;
                    Assert.False(DeedRegistry.HasSeal(mask, other), $"sealing {chapter} also sealed {other}");
                }
            }
        }

        /// <summary>
        /// Chapter I is the interactive tutorial, so its deeds have to be
        /// reachable in the first hour - the whole point is that a new player
        /// finishes it. Nothing in it may want a number an hour cannot produce.
        /// </summary>
        [Fact]
        public void ChapterOneIsFinishableInAnEveningsPlay()
        {
            // One hour, measured: level 11, 53 kills, and enough wood to build
            // with. Deliberately NOT a maxed context - this asserts the chapter
            // is reachable, not that it is completable in principle.
            var firstHour = new DeedContext(
                Level: 11, HasWeaponEquipped: true, LarderStocked: 1, WoodStock: 120,
                ItemsCrafted: 1, TotalKills: 53, BossesSlain: 0, RegionsCompleted: 0,
                HighestUnlockedRegion: 1, DefeatedRegionBossMask: 0, ForgeFusions: 0,
                AffixRerolls: 0, HighestRarityOwned: 2, LargestActiveSetBonus: 0,
                ForgeLevel: 0, InnLevel: 0, VillageBuildingLevelTotal: 0, WarehouseLevel: 0,
                GatheringMasteryTotal: 2, LowestRegionOneKillCount: 5,
                BestCodexRegionCompletion: 0, BestSeasonRank: 0, ChildrenBred: 0,
                EpicChildrenBred: 0, BestAptitudeTotal: 16);

            Assert.True(DeedRegistry.IsComplete(DeedRegistry.Chapters[0], firstHour));

            // And the chapters after it are NOT, or the tutorial would hand out
            // four Seals for an hour's play.
            for (int i = 1; i < DeedRegistry.Chapters.Count; i++)
            {
                Assert.False(DeedRegistry.IsComplete(DeedRegistry.Chapters[i], firstHour));
            }
        }
    }
}
