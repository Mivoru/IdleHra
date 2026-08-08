using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Husbandry: what a child inherits, and whether the bloodline can climb.
    ///
    /// These are statistical rules, so most of these tests run many trials and
    /// assert a shape rather than a value. A seeded Random keeps them
    /// deterministic - a flaky balance test gets disabled, and a disabled test
    /// is worse than none.
    /// </summary>
    public class BreedingAptitudeTests
    {
        private readonly ITestOutputHelper _output;

        public BreedingAptitudeTests(ITestOutputHelper output) => _output = output;

        private static Random Seeded() => new Random(20260808);

        // --- what a point is worth ------------------------------------------

        [Theory]
        [InlineData(0, 0f)]
        [InlineData(1, 1.5f)]
        [InlineData(20, 30f)]
        [InlineData(35, 40.5f)]
        [InlineData(50, 45f)]
        public void TheThreeBandsPayWhatTheySay(int points, float expected)
        {
            Assert.Equal(expected, BreedingAptitudes.BonusPercentFor(points), 3);
        }

        /// <summary>
        /// The property that matters more than any single number: another point
        /// is never worth less than none. An inversion here would punish a
        /// lineage for improving, which is the one thing this system must never
        /// do.
        /// </summary>
        [Fact]
        public void MoreIsNeverWorseAndTheCapHolds()
        {
            float previous = -1f;
            for (int points = 0; points <= BreedingAptitudes.MaxValue + 10; points++)
            {
                float now = BreedingAptitudes.BonusPercentFor(points);
                Assert.True(now >= previous, $"{points} points is worth less than {points - 1}");
                previous = now;
            }

            Assert.Equal(
                BreedingAptitudes.BonusPercentFor(BreedingAptitudes.MaxValue),
                BreedingAptitudes.BonusPercentFor(BreedingAptitudes.MaxValue + 100), 3);
        }

        /// <summary>
        /// A veteran at the absolute cap must stay visible but not decisive
        /// against a settled player at twenty - the season, not the account
        /// age, has to decide the board.
        /// </summary>
        [Fact]
        public void TheVeteranEdgeIsFeltAndNotOverwhelming()
        {
            float settled = BreedingAptitudes.BonusPercentFor(20);
            float veteran = BreedingAptitudes.BonusPercentFor(BreedingAptitudes.MaxValue);

            _output.WriteLine($"settled {settled}% vs veteran {veteran}%");
            Assert.InRange(veteran - settled, 10f, 20f);
            Assert.True(veteran < 50f, "a maxed lineage must not approach doubling a newcomer");
        }

        // --- inheritance -------------------------------------------------------

        [Fact]
        public void AnAptitudeAlwaysComesFromOneParentOrTheOther()
        {
            var rng = Seeded();
            for (int i = 0; i < 2000; i++)
            {
                int got = BreedingAptitudes.InheritOne(12, 4, rng);
                Assert.True(got == 12 || got == 4, $"inherited {got}, which is neither parent");
            }
        }

        [Fact]
        public void TheStrongerParentIsFavouredInProportion()
        {
            var rng = Seeded();
            int fromFather = 0;
            const int trials = 20000;
            for (int i = 0; i < trials; i++)
            {
                if (BreedingAptitudes.InheritOne(12, 4, rng) == 12) fromFather++;
            }

            double share = (double)fromFather / trials;
            _output.WriteLine($"12 vs 4 came from the 12 in {share:P1} of {trials} (expected 75%)");
            Assert.InRange(share, 0.73, 0.77);
        }

        /// <summary>
        /// THE PROPERTY THE WHOLE DESIGN RESTS ON. Crossing two specialists
        /// produces a child good at both, so the strategy - marry difference,
        /// not similarity - discovers itself.
        /// </summary>
        [Fact]
        public void CrossingTwoSpecialistsProducesOneChildGoodAtBoth()
        {
            var rng = Seeded();
            int[] fighter = { 12, 4, 4, 4 };
            int[] gatherer = { 4, 12, 4, 4 };

            int strongBoth = 0;
            const int trials = 5000;
            for (int i = 0; i < trials; i++)
            {
                int[] child = BreedingAptitudes.Breed(fighter, gatherer, isInbred: false, isEpic: false, rng);
                if (child[BreedingAptitudes.Strength] >= 11 && child[BreedingAptitudes.Skill] >= 11) strongBoth++;
            }

            double share = (double)strongBoth / trials;
            _output.WriteLine($"{share:P1} of children were strong in BOTH parents' specialities");
            // 0.75 x 0.75 before mutation nudges either way.
            Assert.InRange(share, 0.50, 0.70);
        }

        /// <summary>
        /// Without mutation a bloodline could never exceed the best value it
        /// already had, and would freeze after two generations. This is the
        /// test that would fail if someone "simplified" mutation away.
        /// </summary>
        [Fact]
        public void ABloodlineCanClimbPastBothParents()
        {
            var rng = Seeded();
            int[] parent = { 10, 10, 10, 10 };

            bool everExceeded = false;
            for (int i = 0; i < 500 && !everExceeded; i++)
            {
                int[] child = BreedingAptitudes.Breed(parent, parent, isInbred: false, isEpic: false, rng);
                for (int a = 0; a < BreedingAptitudes.Count; a++)
                {
                    if (child[a] > 10) everExceeded = true;
                }
            }

            Assert.True(everExceeded, "no child ever beat its parents - the bloodline cannot climb");
        }

        /// <summary>
        /// Ten generations with no selection at all should move a lineage by
        /// about one and a half points - slow enough that reaching the cap of
        /// fifty is years of deliberate breeding rather than a weekend.
        ///
        /// AVERAGED over many lineages, not asserted on one. The first version
        /// of this test ran a single seeded lineage and demanded every value
        /// land under 16; the model was right and the test was not - a single
        /// run clears 16 about 0.3% of the time, and across four aptitudes that
        /// is roughly one seed in a hundred. A test that fails on the tail of
        /// the distribution it is measuring gets disabled, and a disabled test
        /// is worse than none.
        /// </summary>
        [Fact]
        public void TheClimbIsSlowRatherThanExplosive()
        {
            var rng = Seeded();
            const int lineages = 4000;
            const int generations = 10;
            const int start = 10;

            long total = 0;
            int highest = start;

            for (int i = 0; i < lineages; i++)
            {
                int[] line = { start, start, start, start };
                for (int gen = 0; gen < generations; gen++)
                {
                    line = BreedingAptitudes.Breed(line, line, isInbred: false, isEpic: false, rng);
                }
                foreach (int value in line)
                {
                    total += value;
                    if (value > highest) highest = value;
                }
            }

            double mean = (double)total / (lineages * BreedingAptitudes.Count);
            _output.WriteLine(
                $"{generations} unselected generations from {start}: mean {mean:0.00}, highest seen {highest}");

            // +0.15 a generation, so about +1.5 over ten.
            Assert.InRange(mean, start + 1.0, start + 2.0);

            // And nothing runs away: even the luckiest lineage in four thousand
            // stays nowhere near the cap without selection behind it.
            Assert.True(highest < 25, $"an unselected lineage reached {highest} - the drift is too steep");
        }

        // --- inbreeding ---------------------------------------------------------

        /// <summary>
        /// Degraded, not forbidden. It has to stay possible - and it has to
        /// stay a bad idea, or the village gene pool is decoration.
        /// </summary>
        [Fact]
        public void RelatedPairingsDriftDownwardInsteadOfUp()
        {
            var rng = Seeded();
            int healthy = 0, inbred = 0;
            const int trials = 20000;

            for (int i = 0; i < trials; i++)
            {
                healthy += BreedingAptitudes.Mutate(25, isInbred: false, rng) - 25;
                inbred += BreedingAptitudes.Mutate(25, isInbred: true, rng) - 25;
            }

            double healthyDrift = (double)healthy / trials;
            double inbredDrift = (double)inbred / trials;
            _output.WriteLine($"drift per child: healthy {healthyDrift:+0.000;-0.000}, inbred {inbredDrift:+0.000;-0.000}");

            Assert.True(healthyDrift > 0, "an ordinary pairing must trend upward");
            Assert.True(inbredDrift < 0, "a related pairing must trend downward");
        }

        [Fact]
        public void EpicIsRareAndRarerStillBetweenRelatives()
        {
            var rng = Seeded();
            int healthy = 0, inbred = 0;
            const int trials = 40000;

            for (int i = 0; i < trials; i++)
            {
                if (BreedingAptitudes.RollEpic(false, rng)) healthy++;
                if (BreedingAptitudes.RollEpic(true, rng)) inbred++;
            }

            _output.WriteLine($"epic: healthy {(double)healthy / trials:P2}, inbred {(double)inbred / trials:P2}");
            Assert.InRange((double)healthy / trials, 0.04, 0.06);
            Assert.InRange((double)inbred / trials, 0.005, 0.015);
        }

        [Fact]
        public void AnEpicChildGainsInEveryAptitude()
        {
            var rng = Seeded();
            int[] parent = { 20, 20, 20, 20 };

            int[] plain = BreedingAptitudes.Breed(parent, parent, false, isEpic: false, new Random(7));
            int[] epic = BreedingAptitudes.Breed(parent, parent, false, isEpic: true, new Random(7));

            for (int a = 0; a < BreedingAptitudes.Count; a++)
            {
                Assert.True(epic[a] >= plain[a], $"epic was not better in {BreedingAptitudes.NameOf(a)}");
            }
        }

        // --- caps and villagers ---------------------------------------------------

        [Fact]
        public void NothingEverExceedsTheAbsoluteCap()
        {
            var rng = Seeded();
            int[] maxed = { 50, 50, 50, 50 };

            for (int i = 0; i < 2000; i++)
            {
                int[] child = BreedingAptitudes.Breed(maxed, maxed, false, isEpic: true, rng);
                foreach (int value in child) Assert.InRange(value, 0, BreedingAptitudes.MaxValue);
            }
        }

        /// <summary>
        /// The two-phase climb: the village gets a bloodline to twenty and no
        /// further, so everything above that is generations of selection - the
        /// veteran axis, and the reason the cap can sit at fifty.
        /// </summary>
        [Fact]
        public void TheVillageCannotCarryALineagePastTwenty()
        {
            var rng = Seeded();
            for (int innLevel = 0; innLevel <= 100; innLevel += 5)
            {
                for (int i = 0; i < 200; i++)
                {
                    foreach (int value in BreedingAptitudes.RollVillager(innLevel, rng))
                    {
                        Assert.InRange(value, 2, BreedingAptitudes.VillagerCeiling);
                    }
                }
            }
        }

        [Fact]
        public void ABetterInnAttractsBetterPeople()
        {
            var rng = Seeded();
            double Average(int innLevel)
            {
                long total = 0;
                const int trials = 4000;
                for (int i = 0; i < trials; i++)
                {
                    foreach (int v in BreedingAptitudes.RollVillager(innLevel, rng)) total += v;
                }
                return (double)total / (trials * BreedingAptitudes.Count);
            }

            double poor = Average(1);
            double rich = Average(15);
            _output.WriteLine($"Inn 1 averages {poor:0.00}, Inn 15 averages {rich:0.00}");
            Assert.True(rich > poor + 3, "upgrading the Inn must visibly improve the gene pool");
        }

        // --- the band the player is shown --------------------------------------------

        /// <summary>
        /// The preview is a PROMISE, so it has to be exact. Brute-forces the
        /// real Breed() many times and asserts every outcome lands inside the
        /// band PreviewOne quotes - and that the band is tight, not a shrug.
        /// </summary>
        [Theory]
        [InlineData(4, 4)]
        [InlineData(12, 4)]
        [InlineData(0, 0)]
        [InlineData(20, 19)]
        [InlineData(50, 50)]
        public void ThePreviewedBandContainsEveryOutcomeAndNothingSpare(int a, int b)
        {
            BreedingAptitudes.PreviewOne(a, b, out int min, out int max);

            var rng = Seeded();
            var father = new[] { a, a, a, a };
            var mother = new[] { b, b, b, b };

            int seenLow = int.MaxValue;
            int seenHigh = int.MinValue;
            for (int trial = 0; trial < 4000; trial++)
            {
                foreach (int value in BreedingAptitudes.Breed(father, mother, false, false, rng))
                {
                    Assert.InRange(value, min, max);
                    if (value < seenLow) seenLow = value;
                    if (value > seenHigh) seenHigh = value;
                }
            }

            _output.WriteLine($"{a} x {b} -> previewed {min}-{max}, observed {seenLow}-{seenHigh}");

            // Tight: over four thousand trials both ends are reached, so the
            // band is the real range rather than a safe over-estimate.
            Assert.Equal(min, seenLow);
            Assert.Equal(max, seenHigh);
        }

        /// <summary>
        /// The epic roll's +1 is deliberately outside the quoted band. Stated
        /// as a test so a later "the preview was wrong once in twenty" report
        /// finds the decision rather than re-litigating it.
        /// </summary>
        [Fact]
        public void TheEpicBonusIsNotFoldedIntoTheBand()
        {
            BreedingAptitudes.PreviewOne(10, 10, out _, out int max);
            Assert.Equal(11, max);

            var rng = Seeded();
            bool exceeded = false;
            for (int trial = 0; trial < 200 && !exceeded; trial++)
            {
                var epicChild = BreedingAptitudes.Breed(
                    new[] { 10, 10, 10, 10 }, new[] { 10, 10, 10, 10 }, false, true, rng);
                exceeded = Array.Exists(epicChild, v => v > max);
            }

            Assert.True(exceeded, "an epic child must be able to land above the quoted band");
        }

        // --- relatedness ------------------------------------------------------------

        [Fact]
        public void SiblingsAndParentsCountAsRelated()
        {
            var father = Guid.NewGuid();
            var mother = Guid.NewGuid();
            var childA = Guid.NewGuid();
            var childB = Guid.NewGuid();

            // Full siblings.
            Assert.True(BreedingAptitudes.AreRelated(childA, father, mother, childB, father, mother));

            // Parent and child, in both directions.
            Assert.True(BreedingAptitudes.AreRelated(childA, father, mother, father, null, null));
            Assert.True(BreedingAptitudes.AreRelated(father, null, null, childA, father, mother));

            // Half siblings - one shared parent is enough.
            var otherMother = Guid.NewGuid();
            Assert.True(BreedingAptitudes.AreRelated(childA, father, mother, childB, father, otherMother));
        }

        [Fact]
        public void StrangersAreNotRelated()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            Assert.False(BreedingAptitudes.AreRelated(a, Guid.NewGuid(), Guid.NewGuid(), b, Guid.NewGuid(), Guid.NewGuid()));

            // Two orphans with no recorded parents are not relatives, and must
            // not be treated as sharing a "null parent".
            Assert.False(BreedingAptitudes.AreRelated(a, null, null, b, null, null));
        }

        [Fact]
        public void CousinsCountWhenGrandparentsAreKnown()
        {
            var shared = Guid.NewGuid();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();

            Assert.True(BreedingAptitudes.AreRelated(
                a, Guid.NewGuid(), Guid.NewGuid(),
                b, Guid.NewGuid(), Guid.NewGuid(),
                new[] { shared, Guid.NewGuid() },
                new[] { Guid.NewGuid(), shared }));

            // An empty slot in the pedigree is not a shared ancestor.
            Assert.False(BreedingAptitudes.AreRelated(
                a, Guid.NewGuid(), Guid.NewGuid(),
                b, Guid.NewGuid(), Guid.NewGuid(),
                new[] { Guid.Empty, Guid.Empty },
                new[] { Guid.Empty, Guid.Empty }));
        }
    }
}
