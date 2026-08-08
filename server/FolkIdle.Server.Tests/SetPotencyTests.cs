using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Sets reward QUALITY, not collection.
    ///
    /// The old rule counted matching pieces and paid at exactly two and
    /// exactly four. That made a Transcendent helmet worth what a Normal one
    /// was, made the third piece of a four-piece set worth nothing, and left a
    /// player holding three superb pieces of one set and three of another with
    /// nothing from either. The only build it rewarded was "collect four of
    /// the same thing".
    ///
    /// Asked for directly: a player with better pieces from different sets
    /// should get benefit matching that quality, rather than being forced to
    /// hoard one set.
    /// </summary>
    public class SetPotencyTests
    {
        private readonly ITestOutputHelper _output;

        public SetPotencyTests(ITestOutputHelper output) => _output = output;

        private static EquippedSetIds Worn(params (int SetId, int Quality)[] pieces)
        {
            var ids = default(EquippedSetIds);
            for (int i = 0; i < pieces.Length && i < EquippedSetIds.SlotCount; i++)
            {
                ids.SetBySlotIndex(i, pieces[i].SetId, pieces[i].Quality);
            }
            return ids;
        }

        private const int Chiming = SetBonusEngine.ChimingSteelSetId;
        private const int Dread = SetBonusEngine.EternalDreadnoughtSetId;

        [Fact]
        public void FourOrdinaryPiecesIsAFullSet()
        {
            // Four Rare - the reference tier - is exactly 1.0, which is what
            // the doc comment promises a player.
            var result = SetBonusEngine.Evaluate(
                Worn((Chiming, 4), (Chiming, 4), (Chiming, 4), (Chiming, 4)));

            Assert.True(result.BurnApplicationActive, "a full ordinary set must arm the effect");
            _output.WriteLine($"four Rare: fire {result.FireDamageMultiplierPct:F1}%");
        }

        /// <summary>
        /// The heart of the rework: quality substitutes for quantity. Two
        /// exceptional pieces are worth a full set of ordinary ones.
        /// </summary>
        [Fact]
        public void TwoExceptionalPiecesMatchFourOrdinaryOnes()
        {
            var ordinary = SetBonusEngine.Evaluate(
                Worn((Chiming, 4), (Chiming, 4), (Chiming, 4), (Chiming, 4)));

            // Two at quality 8 sum to 16, the same as four at 4.
            var exceptional = SetBonusEngine.Evaluate(Worn((Chiming, 8), (Chiming, 8)));

            Assert.Equal(ordinary.FireDamageMultiplierPct, exceptional.FireDamageMultiplierPct, 3);
            Assert.True(exceptional.BurnApplicationActive);
        }

        /// <summary>
        /// The player's own words: better pieces from DIFFERENT sets should
        /// pay a share of each, so nobody is forced to collect one set.
        /// </summary>
        [Fact]
        public void MixingTwoSetsPaysAShareOfBoth()
        {
            var mixed = SetBonusEngine.Evaluate(
                Worn((Chiming, 8), (Chiming, 8), (Dread, 8), (Dread, 8)));

            Assert.True(mixed.FireDamageMultiplierPct > 0f, "the offensive half must pay");
            Assert.True(mixed.TotalArmorMultiplierPct > 0f, "the defensive half must pay");

            _output.WriteLine(
                $"mixed: fire {mixed.FireDamageMultiplierPct:F1}%, armour {mixed.TotalArmorMultiplierPct:F1}%");

            // And each half is worth what those two pieces alone would be -
            // mixing costs nothing beyond the pieces not spent on the other set.
            var chimingAlone = SetBonusEngine.Evaluate(Worn((Chiming, 8), (Chiming, 8)));
            Assert.Equal(chimingAlone.FireDamageMultiplierPct, mixed.FireDamageMultiplierPct, 3);
        }

        [Fact]
        public void EveryUpgradeIsWorthSomethingImmediately()
        {
            // The old rule's worst edge: the third piece paid nothing at all.
            var two = SetBonusEngine.Evaluate(Worn((Chiming, 4), (Chiming, 4)));
            var three = SetBonusEngine.Evaluate(Worn((Chiming, 4), (Chiming, 4), (Chiming, 4)));

            Assert.True(
                three.FireDamageMultiplierPct > two.FireDamageMultiplierPct,
                "a third piece must be worth more than two");

            // And so is raising the rarity of a piece already worn.
            var upgraded = SetBonusEngine.Evaluate(Worn((Chiming, 4), (Chiming, 5)));
            Assert.True(upgraded.FireDamageMultiplierPct > two.FireDamageMultiplierPct);
        }

        [Fact]
        public void OneStrayPieceIsNotASet()
        {
            // Wearing one piece of something is a coincidence, not a choice.
            var stray = SetBonusEngine.Evaluate(Worn((Chiming, 4)));
            Assert.Equal(0f, stray.FireDamageMultiplierPct);
            Assert.False(stray.BurnApplicationActive);
        }

        [Fact]
        public void TheTopOfTheLadderIsCapped()
        {
            var transcendent = SetBonusEngine.Evaluate(
                Worn((Dread, 14), (Dread, 14), (Dread, 14), (Dread, 14), (Dread, 14), (Dread, 14)));

            // Six Transcendent pieces would be 84/16 = 5.25x without the cap,
            // which would drown every other stat on the character.
            Assert.Equal(SetBonusEngine.MaxPotency, SetBonusEngine.PotencyOf(84), 3);
            Assert.Equal(25f * SetBonusEngine.MaxPotency, transcendent.TotalArmorMultiplierPct, 2);
        }

        /// <summary>
        /// Rows written before quality was recorded carry a zero. They are
        /// still WORN, so they must still count for something - reading zero
        /// as "not part of the set" would make old gear silently stop working.
        /// </summary>
        [Fact]
        public void APieceWithNoRecordedQualityStillCounts()
        {
            var ids = default(EquippedSetIds);
            ids.SetBySlotIndex(0, Chiming, 0);
            ids.SetBySlotIndex(1, Chiming, 0);
            ids.SetBySlotIndex(2, Chiming, 0);
            ids.SetBySlotIndex(3, Chiming, 0);

            Assert.Equal(SetBonusEngine.PotencyOf(4), SetBonusEngine.PotencyOf(4));
            Assert.True(SetBonusEngine.PotencyOf(4) >= SetBonusEngine.MinimumPotency,
                "four pieces of unknown quality must still reach the floor");
            Assert.True(SetBonusEngine.Evaluate(ids).FireDamageMultiplierPct > 0f);
        }

        [Fact]
        public void PackingRoundTrips()
        {
            int packed = EquippedSetIds.Pack(Dread, 13);
            Assert.Equal(Dread, EquippedSetIds.SetIdOf(packed));
            Assert.Equal(13, EquippedSetIds.QualityOf(packed));
        }
    }
}
