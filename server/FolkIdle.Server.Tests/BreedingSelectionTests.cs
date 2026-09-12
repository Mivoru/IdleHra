using System;
using System.Numerics;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// THE BREEDING GROUNDS, WHICH USED TO DO NOTHING.
    ///
    /// `BreedingLevel` was read in four places and every one of them tested
    /// `<= 0` or `== 0`. Upgrading the building past level 1 changed no number
    /// anywhere in the game; the reporting player had it at 4.
    ///
    /// It buys SELECTION now: the player names an aptitude to breed for, and a
    /// selected aptitude takes the better parent's value outright instead of
    /// InheritOne's weighted coin. That is the difference between husbandry and
    /// a slot machine - with the coin, a 4 against a villager's 6 takes the 6
    /// only 60% of the time, so a bloodline regularly loses ground on the exact
    /// stat the player was trying to raise.
    /// </summary>
    public class BreedingSelectionTests
    {
        private readonly ITestOutputHelper _output;

        public BreedingSelectionTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(3, 0)]
        [InlineData(4, 1)]
        [InlineData(6, 1)]
        [InlineData(7, 2)]
        [InlineData(9, 2)]
        [InlineData(10, 3)]
        [InlineData(12, 3)]
        public void SelectableCountByGroundsLevel(int level, int expected)
        {
            Assert.Equal(expected, BreedingAptitudes.SelectableCount(level));
        }

        /// <summary>
        /// Never all four. A player who could select every aptitude would have
        /// removed inheritance from the game and replaced it with "take the max
        /// of both parents", which makes the choice of partner irrelevant.
        /// </summary>
        [Fact]
        public void TheGroundsCanNeverSelectEveryAptitude()
        {
            for (int level = 0; level <= 100; level++)
            {
                Assert.InRange(BreedingAptitudes.SelectableCount(level), 0, BreedingAptitudes.Count - 1);
            }
        }

        [Fact]
        public void ASelectedAptitudeNeverTakesTheWorseParent()
        {
            var father = new[] { 12, 4, 4, 4 };
            var mother = new[] { 4, 4, 4, 4 };
            var rng = new Random(7);

            for (int i = 0; i < 2_000; i++)
            {
                var child = BreedingAptitudes.Breed(
                    father, mother, isInbred: false, isEpic: false,
                    selectionMask: 0b0001, groundsLevel: 4, rng: rng);

                // 12 is taken outright, then the ordinary mutation moves it by
                // at most one - so the floor is 11, never the mother's 4.
                Assert.True(child[0] >= 11, $"selected Strength fell to {child[0]}");
            }
        }

        /// <summary>
        /// And an UNSELECTED aptitude still uses the weighted coin, so the rest
        /// of the vector behaves exactly as it always did.
        /// </summary>
        [Fact]
        public void AnUnselectedAptitudeStillRollsTheWeightedCoin()
        {
            var father = new[] { 4, 12, 4, 4 };
            var mother = new[] { 4, 4, 4, 4 };
            var rng = new Random(8);

            bool sawTheWorseParent = false;
            for (int i = 0; i < 2_000; i++)
            {
                var child = BreedingAptitudes.Breed(
                    father, mother, isInbred: false, isEpic: false,
                    selectionMask: 0b0001, groundsLevel: 4, rng: rng);

                if (child[1] <= 5) sawTheWorseParent = true;
            }

            Assert.True(sawTheWorseParent, "Skill was not selected, so it must still be able to inherit the 4");
        }

        [Fact]
        public void TheMaskIsClampedToWhatTheGroundsPermit()
        {
            Assert.Equal(0, BitOperations.PopCount((uint)BreedingAptitudes.ClampSelection(0b1111, 3)));
            Assert.Equal(1, BitOperations.PopCount((uint)BreedingAptitudes.ClampSelection(0b1111, 4)));
            Assert.Equal(2, BitOperations.PopCount((uint)BreedingAptitudes.ClampSelection(0b1111, 7)));
            Assert.Equal(3, BitOperations.PopCount((uint)BreedingAptitudes.ClampSelection(0b1111, 12)));
        }

        /// <summary>
        /// A CLIENT THAT ASKS FOR TOO MUCH GETS WHAT IT BOUGHT, not a refusal.
        /// The count is a server truth; the client's copy of the table is a
        /// hint for drawing checkboxes, and a hint that has drifted must not
        /// cost a player their gold.
        /// </summary>
        [Fact]
        public void AnOverreachingMaskIsTrimmedRatherThanRefused()
        {
            int clamped = BreedingAptitudes.ClampSelection(0b1111, groundsLevel: 4);
            Assert.NotEqual(0, clamped);
            Assert.Equal(1, BitOperations.PopCount((uint)clamped));
        }

        [Fact]
        public void BitsAboveTheFourAptitudesAreIgnored()
        {
            // The mask arrives over the wire as a uint. Anything above bit 3 is
            // not an aptitude and must not survive into the loop.
            int clamped = BreedingAptitudes.ClampSelection(unchecked((int)0xFFFF_FFF0), groundsLevel: 12);
            Assert.Equal(0, clamped);
        }

        [Fact]
        public void NoSelectionIsTheOldBehaviourExactly()
        {
            var father = new[] { 12, 4, 4, 4 };
            var mother = new[] { 4, 4, 4, 4 };

            bool sawTheWorseParent = false;
            var rng = new Random(9);
            for (int i = 0; i < 2_000; i++)
            {
                var child = BreedingAptitudes.Breed(
                    father, mother, isInbred: false, isEpic: false,
                    selectionMask: 0, groundsLevel: 1, rng: rng);

                if (child[0] <= 5) sawTheWorseParent = true;
            }

            Assert.True(sawTheWorseParent, "with nothing selected the weighted coin must still be able to take the 4");
        }

        [Theory]
        [InlineData(0, 25)]
        [InlineData(1, 26)]
        [InlineData(12, 37)]
        public void TheGroundsAlsoRaisesTheUpMutationChance(int groundsLevel, int expected)
        {
            Assert.Equal(expected, BreedingAptitudes.UpMutationPercentFor(groundsLevel));
        }

        /// <summary>
        /// Inbreeding still inverts the mutation, and the Grounds does not
        /// rescue it - otherwise a player could build their way out of needing
        /// the village at all, which is the one strategy the inversion exists
        /// to prevent.
        /// </summary>
        [Fact]
        public void ARelatedPairingStillDriftsDownwardsAtAMaxedGrounds()
        {
            const int Start = 25;
            var rng = new Random(10);
            long total = 0;
            const int Generations = 20_000;

            for (int i = 0; i < Generations; i++)
            {
                var a = new[] { Start, Start, Start, Start };
                var b = new[] { Start, Start, Start, Start };
                int[] child = BreedingAptitudes.Breed(
                    a, b, isInbred: true, isEpic: false,
                    selectionMask: 0b0010, groundsLevel: 12, rng: rng);

                total += child[1] - Start;
            }

            double drift = (double)total / Generations;
            _output.WriteLine($"inbred drift at Grounds 12: {drift:+0.000;-0.000}");
            Assert.True(drift < 0.0, "a related pairing climbs, so nothing stops a closed bloodline");
        }

        [Fact]
        public void TheCapIsStillTheCap()
        {
            var father = new[] { BreedingAptitudes.MaxValue, 4, 4, 4 };
            var mother = new[] { BreedingAptitudes.MaxValue, 4, 4, 4 };
            var rng = new Random(12);

            for (int i = 0; i < 1_000; i++)
            {
                var child = BreedingAptitudes.Breed(
                    father, mother, isInbred: false, isEpic: true,
                    selectionMask: 0b0001, groundsLevel: 12, rng: rng);

                Assert.True(child[0] <= BreedingAptitudes.MaxValue);
            }
        }
    }
}
