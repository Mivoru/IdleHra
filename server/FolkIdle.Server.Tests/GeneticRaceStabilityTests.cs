using System.Collections.Generic;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A BRED CHILD IS ALWAYS ONE OF THE GAME'S SIX RACES.
    ///
    /// Modul: THIS SHIPPED, AND IT PRESENTED AS A FLAKY TEST.
    ///
    /// SpliceLocus mutates by flipping the low five bits (^ 0x1F) and was
    /// applying that to all four loci. On Speed, Crit or Yield that is the
    /// mechanic. On RACE it produces a species id that does not exist - a
    /// Human is 1, and 1 ^ 0x1F is 30 - so the child had no mastery table, no
    /// innate passives and no artwork, and nothing logged a thing.
    ///
    /// At generation 0 the mutation chance is 1.5%, so it corrupted about one
    /// pairing in seventy. That is exactly the rate that reads as noise:
    /// Test_HeroVillager_MarriesAndTheVillagerBecomesAnElder failed once in a
    /// full run with "expected 1, actual 30" and passed alone on the re-run,
    /// which is the worst failure rate there is - often enough to be real,
    /// rare enough to be dismissed.
    ///
    /// The intent was already written down: ApplyInbreedingDegradation's own
    /// comment says a genetic defect changes the child's potential, never its
    /// species. This is that rule, enforced.
    /// </summary>
    public class GeneticRaceStabilityTests
    {
        private readonly ITestOutputHelper _output;
        public GeneticRaceStabilityTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// Enough pairings that a 1.5% mutation would appear hundreds of times.
        /// The defect this guards was found at one in seventy; a hundred
        /// iterations would have been a coin flip.
        /// </summary>
        private const int Pairings = 20_000;

        [Fact]
        public void BreedingNeverInventsARaceThatDoesNotExist()
        {
            var seen = new HashSet<byte>();

            // Two parents of the same race, which is the only pairing
            // BreedingEngine permits (it refuses a mixed pair outright).
            var parent = new GeneticVector(0);
            parent.LocusRace = new Locus { Dominant = RaceIds.Human, Recessive = RaceIds.Human };
            parent.LocusSpeed = new Locus { Dominant = 10, Recessive = 8 };
            parent.LocusCrit = new Locus { Dominant = 12, Recessive = 6 };
            parent.LocusYield = new Locus { Dominant = 9, Recessive = 9 };

            for (int i = 0; i < Pairings; i++)
            {
                long childGenome = GeneticSplicingEngine.Breed(parent.RawValue, parent.RawValue, maxGeneration: 0);
                seen.Add(new GeneticVector(childGenome).LocusRace.Dominant);
            }

            _output.WriteLine($"{Pairings:N0} pairings of two Humans produced race id(s): {string.Join(", ", seen)}");

            Assert.Equal(new HashSet<byte> { RaceIds.Human }, seen);
        }

        [Fact]
        public void TheQualityLociStillMutate()
        {
            // Modul: the other half of the fix. Disabling the mutation
            // wholesale would have been the easy version and would have quietly
            // removed the only thing that lets a line exceed its parents -
            // which is a balance change wearing a bug fix's clothes, and
            // exactly the "computed but never consumed" shape this codebase
            // keeps finding. Race stops mutating; Speed, Crit and Yield must
            // not.
            var parent = new GeneticVector(0);
            parent.LocusRace = new Locus { Dominant = RaceIds.Human, Recessive = RaceIds.Human };
            parent.LocusSpeed = new Locus { Dominant = 10, Recessive = 10 };
            parent.LocusCrit = new Locus { Dominant = 10, Recessive = 10 };
            parent.LocusYield = new Locus { Dominant = 10, Recessive = 10 };

            bool sawAMutation = false;
            for (int i = 0; i < Pairings && !sawAMutation; i++)
            {
                var child = new GeneticVector(
                    GeneticSplicingEngine.Breed(parent.RawValue, parent.RawValue, maxGeneration: 0));

                // Both alleles are 10, so any value other than 10 can only have
                // come from the mutation.
                if (child.LocusSpeed.Dominant != 10
                    || child.LocusCrit.Dominant != 10
                    || child.LocusYield.Dominant != 10)
                {
                    sawAMutation = true;
                    _output.WriteLine(
                        $"mutation after {i + 1} pairings: speed {child.LocusSpeed.Dominant}, " +
                        $"crit {child.LocusCrit.Dominant}, yield {child.LocusYield.Dominant}");
                }
            }

            Assert.True(sawAMutation, "the quality loci must still be able to mutate - that is the whole breeding upside");
        }
    }
}
