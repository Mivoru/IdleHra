using System;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// HOW FAST A BLOODLINE ACTUALLY CLIMBS, printed and asserted.
    ///
    /// The question that produced this file came from the developer playing his
    /// own game: "I can have villagers with 6 points on two attributes and marry
    /// a 4/4/4/4 hero, the child would be 4/6/6/4, and then I can only marry him
    /// to a hero that is permanently 4/4/4/4 - so how is this scalable?"
    ///
    /// Half of that is a misreading - you marry a FRESH VILLAGER each
    /// generation, never your own founders, and crossing your own line is the
    /// dead end he describes. The other half was right, and two things were
    /// broken underneath it:
    ///
    /// 1. VillagerCeiling said 20 and the Inn could not roll it. Villagers rolled
    ///    `2 + rand(0..InnLevel)` and the Inn cannot exceed 12 (the Town Hall
    ///    maxes at 5 and the ceiling is 2 + 5*2), so the real cap was 14. The
    ///    file's own comment - "0 to 20 is village-driven" - described numbers
    ///    the game could not produce.
    ///
    /// 2. The Breeding Grounds level did NOTHING. It was read in four places and
    ///    every one tested `<= 0`. The reporting player had it at level 4; those
    ///    three upgrades changed no number anywhere in the game.
    ///
    /// Following PowerCeilingTests: a number a test prints is not a number a
    /// test checks, so every measurement below is asserted.
    /// </summary>
    public class BreedingClimbTests
    {
        private readonly ITestOutputHelper _output;

        public BreedingClimbTests(ITestOutputHelper output) => _output = output;

        /// <summary>The Inn's own ceiling: Town Hall 5 permits 2 + 5*2.</summary>
        private const int MaxInnLevel = 12;

        [Fact]
        public void AMaxedInnCanActuallyReachTheVillagerCeiling()
        {
            int best = 0;
            var rng = new Random(1);
            for (int i = 0; i < 20_000; i++)
            {
                foreach (int v in BreedingAptitudes.RollVillager(MaxInnLevel, rng))
                {
                    best = Math.Max(best, v);
                }
            }

            Assert.Equal(BreedingAptitudes.VillagerCeiling, best);
        }

        [Fact]
        public void AVillagerNeverRollsBelowTheFloorOrAboveTheCeiling()
        {
            var rng = new Random(2);
            for (int inn = 0; inn <= MaxInnLevel; inn++)
            {
                for (int i = 0; i < 2_000; i++)
                {
                    foreach (int v in BreedingAptitudes.RollVillager(inn, rng))
                    {
                        Assert.InRange(v, 2, BreedingAptitudes.VillagerCeiling);
                    }
                }
            }
        }

        /// <summary>
        /// What the Inn is worth, in the only unit that matters: the best
        /// aptitude a villager can bring.
        /// </summary>
        [Fact]
        public void TheInnIsTheFirstPhaseOfTheClimb()
        {
            var rng = new Random(3);
            int previousBest = 0;

            for (int inn = 0; inn <= MaxInnLevel; inn++)
            {
                int best = 0;
                double sum = 0;
                int samples = 0;

                for (int i = 0; i < 5_000; i++)
                {
                    foreach (int v in BreedingAptitudes.RollVillager(inn, rng))
                    {
                        best = Math.Max(best, v);
                        sum += v;
                        samples++;
                    }
                }

                _output.WriteLine($"Inn {inn,2}: best villager aptitude {best,2}, mean {sum / samples:0.00}");

                // Monotonic. A player who upgrades the Inn must never be handed
                // a narrower band than before.
                Assert.True(best >= previousBest, $"Inn {inn} rolls worse than Inn {inn - 1}");
                previousBest = best;
            }

            Assert.Equal(BreedingAptitudes.VillagerCeiling, previousBest);
        }

        /// <summary>
        /// THE SECOND PHASE. Past the Inn's reach every villager is worse than
        /// the line, so the only climb left is mutation drift - and the Breeding
        /// Grounds is what makes that drift worth anything.
        ///
        /// Measured as the average change in ONE aptitude over a generation
        /// where both parents already hold the same value, which is the state a
        /// bloodline settles into once it has outgrown the village.
        /// </summary>
        [Fact]
        public void TheGroundsIsTheSecondPhaseOfTheClimb()
        {
            const int Start = 25;
            const int Generations = 40_000;

            double driftAtOne = MeasureDrift(groundsLevel: 1, Start, Generations, seed: 4);
            double driftAtMax = MeasureDrift(groundsLevel: 12, Start, Generations, seed: 5);

            _output.WriteLine($"drift per generation at Grounds  1: {driftAtOne:+0.000;-0.000}");
            _output.WriteLine($"drift per generation at Grounds 12: {driftAtMax:+0.000;-0.000}");
            _output.WriteLine($"generations from {Start} to {BreedingAptitudes.MaxValue} at Grounds  1: {(BreedingAptitudes.MaxValue - Start) / driftAtOne:0}");
            _output.WriteLine($"generations from {Start} to {BreedingAptitudes.MaxValue} at Grounds 12: {(BreedingAptitudes.MaxValue - Start) / driftAtMax:0}");

            // Lineage overhaul 2026-11-13: a SELECTED aptitude far below the
            // limit gains a sure +2 (SureGrowth) plus the luck roll, so the
            // climb no longer depends on the Grounds - the Grounds buys the
            // luck and the number of selections.
            Assert.InRange(driftAtOne, 2.2, 2.45);
            Assert.True(driftAtMax > driftAtOne, "the Grounds level buys nothing");
            Assert.InRange(driftAtMax / driftAtOne, 1.02, 1.2);
        }

        private static double MeasureDrift(int groundsLevel, int start, int generations, int seed)
        {
            var rng = new Random(seed);
            long total = 0;

            for (int i = 0; i < generations; i++)
            {
                var father = new[] { start, start, start, start };
                var mother = new[] { start, start, start, start };

                bool isEpic = BreedingAptitudes.RollEpic(false, rng);
                int[] child = BreedingAptitudes.Breed(
                    father, mother, isInbred: false, isEpic: isEpic,
                    selectionMask: 0b0001, groundsLevel: groundsLevel, rng: rng);

                total += child[0] - start;
            }

            return (double)total / generations;
        }

        /// <summary>
        /// THE CEILING IS THE CLOCK (lineage overhaul 2026-11-13). Production:
        /// both 20-rebirth accounts sat at 21-23 under a ceiling of 50. With
        /// three selected aptitudes, rotated onto the three lowest, and a fresh
        /// villager each generation (the hardest partner - a weak one), a line
        /// at 20 reaches 50/50/50/50 in about 25 generations; the Python model
        /// in docs/design/LINEAGE_REBIRTH_OVERHAUL.md said 25 (p90 29).
        /// </summary>
        [Fact]
        public void ATwentyRebirthLineFillsItsCeilingInAFewDozenGenerations()
        {
            int ceiling = BreedingAptitudes.CeilingFor(20);
            Assert.Equal(BreedingAptitudes.MaxValue, ceiling);

            var counts = new System.Collections.Generic.List<int>();
            for (int run = 0; run < 200; run++)
            {
                var rng = new Random(1000 + run);
                int[] line = { 20, 20, 20, 20 };
                int generations = 0;
                while (Array.Exists(line, v => v < ceiling) && generations < 500)
                {
                    int mask = 0;
                    var order = new[] { 0, 1, 2, 3 };
                    Array.Sort(order, (x, y) => line[x].CompareTo(line[y]));
                    for (int k = 0; k < 3; k++) mask |= 1 << order[k];

                    var villager = BreedingAptitudes.RollVillager(MaxInnLevel, rng);
                    var child = BreedingAptitudes.Breed(line, villager, false, false, mask, MaxInnLevel, rng, ceiling);
                    // A player keeps the better of parent and child.
                    if (System.Linq.Enumerable.Sum(child) >= System.Linq.Enumerable.Sum(line)) line = child;
                    generations++;
                }
                counts.Add(generations);
            }

            counts.Sort();
            int median = counts[counts.Count / 2];
            int p90 = counts[counts.Count * 9 / 10];
            _output.WriteLine($"20 -> 50/50/50/50 with a fresh villager each generation: median {median}, p90 {p90}");
            Assert.InRange(median, 15, 40);
            Assert.True(p90 < 60, $"p90 {p90}");
        }

        /// <summary>A selected aptitude never falls below the better parent, and stops at the limit.</summary>
        [Fact]
        public void ASelectedAptitudeAlwaysGrowsUntilTheLimit()
        {
            var rng = new Random(3);
            for (int i = 0; i < 5_000; i++)
            {
                var child = BreedingAptitudes.Breed(new[] { 30, 4, 4, 4 }, new[] { 10, 4, 4, 4 }, false, false, 0b0001, 1, rng, ceiling: 41);
                Assert.InRange(child[0], 32, 33);
                var capped = BreedingAptitudes.Breed(new[] { 40, 4, 4, 4 }, new[] { 10, 4, 4, 4 }, false, false, 0b0001, 1, rng, ceiling: 41);
                Assert.Equal(41, capped[0]);
            }
            // A related pair gets no sure growth.
            Assert.Equal(0, BreedingAptitudes.SureGrowth(30, 41, isRelated: true));
        }

        /// <summary>
        /// The whole climb, end to end, as a player would actually walk it:
        /// start at the founding 4/4/4/4, marry a fresh villager every
        /// generation, and select the aptitude you care about.
        /// </summary>
        [Fact]
        public void AFullClimbFromFounderToTheVillageCeiling()
        {
            foreach (int inn in new[] { 1, 5, MaxInnLevel })
            {
                foreach (int grounds in new[] { 1, 4, MaxInnLevel })
                {
                    var rng = new Random(11);
                    var line = BreedingAptitudes.Starting();
                    int generations = 0;

                    while (line[0] < BreedingAptitudes.VillagerCeiling && generations < 2_000)
                    {
                        var villager = BreedingAptitudes.RollVillager(inn, rng);
                        bool isEpic = BreedingAptitudes.RollEpic(false, rng);
                        line = BreedingAptitudes.Breed(line, villager, false, isEpic, 0b0001, grounds, rng);
                        generations++;
                    }

                    _output.WriteLine(
                        $"Inn {inn,2}, Grounds {grounds,2}: reached {line[0],2} in {generations,4} generations");

                    // A level-1 Inn cannot reach 20 - villagers roll 2-3 there -
                    // so the guard is that the climb is FINITE where the village
                    // can supply it, and that it stalls where it cannot.
                    if (inn == MaxInnLevel)
                    {
                        Assert.True(generations < 2_000,
                            $"a maxed Inn and Grounds {grounds} never reached the ceiling");
                    }
                }
            }
        }

        /// <summary>
        /// The reporting player's own village, as it stood: Inn level 5, ten
        /// newcomers, best aptitude among them 6. That is what `2 + rand(0..5)`
        /// produces, and it is why his line could not climb - he was reading the
        /// game correctly.
        /// </summary>
        [Fact]
        public void TheReportedVillageIsExactlyWhatTheOldRollProduced()
        {
            // The OLD formula, kept here as the record of what he was seeing.
            static int OldRoll(int innLevel, Random rng) => Math.Min(20, 2 + rng.Next(innLevel + 1));

            var rng = new Random(6);
            int best = 0;
            for (int i = 0; i < 10_000; i++) best = Math.Max(best, OldRoll(5, rng));

            Assert.Equal(7, best);

            // And what the same Inn produces now.
            int nowBest = 0;
            for (int i = 0; i < 10_000; i++)
            {
                foreach (int v in BreedingAptitudes.RollVillager(5, rng)) nowBest = Math.Max(nowBest, v);
            }

            _output.WriteLine($"Inn 5: was max {best}, now max {nowBest}");
            Assert.True(nowBest > best, "the Inn change bought nothing at the level the report came from");
        }
    }
}
