using System;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE REBIRTH IS THE CLOCK (owner 2026-10-14: an aptitude of 50
    /// should take about twenty rebirths). Before the ceiling the climb was
    /// limited only by hours played - BreedingClimbTests measures 77-119
    /// generations from 25 to 50, and nothing stopped a run holding all of
    /// them. These pin the ceiling's two ends, that breeding obeys it, and that
    /// a line already above it keeps what it has.
    /// </summary>
    public class BloodlineCeilingTests
    {
        private readonly ITestOutputHelper _output;

        public BloodlineCeilingTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void TheCeilingRunsFromTheVillageToTheCapOverEveryRebirth()
        {
            Assert.Equal(BreedingAptitudes.VillagerCeiling, BreedingAptitudes.CeilingFor(0));
            Assert.Equal(BreedingAptitudes.MaxValue, BreedingAptitudes.CeilingFor(RebirthRules.MaxRebirths));
            Assert.Equal(BreedingAptitudes.MaxValue, BreedingAptitudes.CeilingFor(RebirthRules.MaxRebirths + 5));
            Assert.Equal(BreedingAptitudes.VillagerCeiling, BreedingAptitudes.CeilingFor(-1));

            for (int n = 1; n <= RebirthRules.MaxRebirths; n++)
            {
                int step = BreedingAptitudes.CeilingFor(n) - BreedingAptitudes.CeilingFor(n - 1);
                // Every rebirth opens something - a rebirth that raised nothing
                // would be a step on the roadmap with no reward on it.
                Assert.InRange(step, 1, 2);
            }
            _output.WriteLine(string.Join(" ", System.Linq.Enumerable.Range(0, RebirthRules.MaxRebirths + 1)
                .Select(n => $"{n}:{BreedingAptitudes.CeilingFor(n)}")));
        }

        [Fact]
        public void NoChildIsBredAboveTheCeiling_EvenAnEpicOne()
        {
            var rng = new Random(7);
            int ceiling = BreedingAptitudes.CeilingFor(3);
            var father = new[] { ceiling, ceiling, ceiling, ceiling };
            var mother = new[] { ceiling, ceiling - 1, ceiling, 4 };

            for (int i = 0; i < 20_000; i++)
            {
                int[] child = BreedingAptitudes.Breed(father, mother, false, isEpic: i % 3 == 0, 0b0011, groundsLevel: 12, rng, ceiling);
                Assert.All(child, v => Assert.InRange(v, 0, ceiling));
            }
        }

        [Fact]
        public void ALineAboveTheCeilingKeepsItsValue_ButDoesNotClimb()
        {
            var rng = new Random(9);
            const int grandfathered = 30;
            int ceiling = BreedingAptitudes.CeilingFor(0);
            var father = new[] { grandfathered, 4, 4, 4 };
            var mother = new[] { grandfathered, 4, 4, 4 };

            bool sawThirty = false;
            for (int i = 0; i < 20_000; i++)
            {
                int[] child = BreedingAptitudes.Breed(father, mother, false, isEpic: true, 0b0001, groundsLevel: 12, rng, ceiling);
                Assert.True(child[0] <= grandfathered, $"a line at {grandfathered} climbed past it to {child[0]} under a ceiling of {ceiling}");
                sawThirty |= child[0] == grandfathered;
            }
            Assert.True(sawThirty, "a child could not even inherit the value its parents already had");
        }

        [Fact]
        public void ThePreviewBandStopsWhereTheEngineStops()
        {
            int ceiling = BreedingAptitudes.CeilingFor(2);
            BreedingAptitudes.PreviewOne(ceiling, ceiling - 3, out int min, out int max, ceiling);
            Assert.Equal(ceiling, max);
            Assert.Equal(ceiling - 4, min);

            // Below the ceiling the band is the parents' plus one, as before.
            BreedingAptitudes.PreviewOne(10, 8, out min, out max, ceiling);
            Assert.Equal(11, max);
            Assert.Equal(7, min);
        }
    }
}
