using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Sets have three tiers, reached by COUNT - and each tier is worth what
    /// the pieces are worth.
    ///
    /// Two rules that answer different questions, and the history here is that
    /// each was tried alone and each lost something:
    ///
    ///   - Counting alone (the original) made a Transcendent helmet worth what
    ///     a Normal one was, and the third piece of a four-piece set worth
    ///     nothing at all.
    ///   - Scaling alone (the replacement) fixed that and removed the tiers, so
    ///     five matching pieces of ordinary gear stopped being a set.
    ///
    /// Which tier you have is what you PUT ON. How much it gives is what those
    /// pieces ARE.
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

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(3, 2)]
        // Modul: FOUR IS NOT A TIER. Eight slots exist, and a 2/3/4/5 ladder
        // would make every step a formality - so the fourth piece holds the
        // third tier's bonus and the fifth is what a player reaches for.
        [InlineData(4, 2)]
        [InlineData(5, 3)]
        [InlineData(8, 3)]
        public void TheTiersAreTwoThreeAndFive(int pieceCount, int expectedTier)
        {
            Assert.Equal(expectedTier, SetBonusEngine.TierOf(pieceCount));
        }

        /// <summary>
        /// The player's own framing: a Linen chest and Linen leggings are two
        /// pieces of Linen whether they are Rare or Uncommon. Rarity decides
        /// how strong the tier is; it never decides whether you have one.
        /// </summary>
        [Fact]
        public void RarityDoesNotDecideWhetherASetIsWorn()
        {
            var mismatched = SetBonusEngine.Evaluate(Worn((Chiming, 4), (Chiming, 3)));
            var poor = SetBonusEngine.Evaluate(Worn((Chiming, 1), (Chiming, 1)));

            Assert.True(mismatched.FireDamageMultiplierPct > 0f);
            Assert.True(poor.FireDamageMultiplierPct > 0f, "even two junk pieces are a set");
            _output.WriteLine(
                $"Rare+Uncommon {mismatched.FireDamageMultiplierPct:F2}%, " +
                $"two Normal {poor.FireDamageMultiplierPct:F2}%");
        }

        [Fact]
        public void EachTierIsWorthMoreThanTheOneBelow()
        {
            float two = SetBonusEngine.Evaluate(Worn((Chiming, 4), (Chiming, 4))).FireDamageMultiplierPct;
            float three = SetBonusEngine.Evaluate(
                Worn((Chiming, 4), (Chiming, 4), (Chiming, 4))).FireDamageMultiplierPct;
            float five = SetBonusEngine.Evaluate(
                Worn((Chiming, 4), (Chiming, 4), (Chiming, 4), (Chiming, 4), (Chiming, 4)))
                .FireDamageMultiplierPct;

            _output.WriteLine($"2 pieces {two:F1}%, 3 pieces {three:F1}%, 5 pieces {five:F1}%");
            Assert.True(three > two);
            Assert.True(five > three);
        }

        [Fact]
        public void BetterPiecesMakeTheSameTierStronger()
        {
            float ordinary = SetBonusEngine.Evaluate(
                Worn((Dread, 4), (Dread, 4), (Dread, 4))).TotalArmorMultiplierPct;
            float excellent = SetBonusEngine.Evaluate(
                Worn((Dread, 8), (Dread, 8), (Dread, 8))).TotalArmorMultiplierPct;

            Assert.True(excellent > ordinary, "three excellent pieces must beat three ordinary ones");

            // And it is the AVERAGE, so one upgraded piece already counts.
            float oneUpgraded = SetBonusEngine.Evaluate(
                Worn((Dread, 4), (Dread, 4), (Dread, 8))).TotalArmorMultiplierPct;
            Assert.True(oneUpgraded > ordinary);
        }

        /// <summary>
        /// The request this whole rework came from: wear the best things you
        /// own from two sets and get a real share of each, instead of being
        /// forced to hoard one.
        /// </summary>
        [Fact]
        public void MixingTwoSetsPaysTheTierEachHasReached()
        {
            var mixed = SetBonusEngine.Evaluate(
                Worn((Chiming, 8), (Chiming, 8), (Chiming, 8), (Dread, 8), (Dread, 8)));

            var chimingThreeAlone = SetBonusEngine.Evaluate(
                Worn((Chiming, 8), (Chiming, 8), (Chiming, 8)));
            var dreadTwoAlone = SetBonusEngine.Evaluate(Worn((Dread, 8), (Dread, 8)));

            // Three of one and two of the other: the 3-piece tier and the
            // 2-piece tier, each exactly what it would be on its own.
            Assert.Equal(chimingThreeAlone.FireDamageMultiplierPct, mixed.FireDamageMultiplierPct, 3);
            Assert.Equal(dreadTwoAlone.TotalArmorMultiplierPct, mixed.TotalArmorMultiplierPct, 3);

            _output.WriteLine(
                $"3 Chiming + 2 Dreadnought: fire {mixed.FireDamageMultiplierPct:F1}%, " +
                $"armour {mixed.TotalArmorMultiplierPct:F1}%");
        }

        [Fact]
        public void OneStrayPieceIsNotASet()
        {
            var stray = SetBonusEngine.Evaluate(Worn((Chiming, 14)));
            Assert.Equal(0f, stray.FireDamageMultiplierPct);
            Assert.False(stray.BurnApplicationActive);
        }

        [Fact]
        public void TheTopEffectsNeedTheTopTier()
        {
            // Quality cannot buy the boolean effects - only the fifth piece can.
            var fourExcellent = SetBonusEngine.Evaluate(
                Worn((Dread, 14), (Dread, 14), (Dread, 14), (Dread, 14)));
            Assert.False(fourExcellent.ThornsReflectionActive);

            var fiveOrdinary = SetBonusEngine.Evaluate(
                Worn((Dread, 2), (Dread, 2), (Dread, 2), (Dread, 2), (Dread, 2)));
            Assert.True(fiveOrdinary.ThornsReflectionActive);
            Assert.True(fiveOrdinary.DamageCapActive);
        }

        [Fact]
        public void QualityIsClampedAtBothEnds()
        {
            // A full set of junk still does something...
            Assert.Equal(SetBonusEngine.MinQualityScale, SetBonusEngine.QualityScaleOf(5, 5), 3);
            // ...and a full set of the very best does not drown everything else.
            Assert.Equal(SetBonusEngine.MaxQualityScale, SetBonusEngine.QualityScaleOf(70, 5), 3);
        }

        /// <summary>
        /// Rows written before quality was recorded carry a zero. They are
        /// still WORN, so they must still count - reading zero as "not part of
        /// the set" would make old gear silently stop working.
        /// </summary>
        [Fact]
        public void APieceWithNoRecordedQualityStillCounts()
        {
            var ids = default(EquippedSetIds);
            for (int slot = 0; slot < 3; slot++) ids.SetBySlotIndex(slot, Chiming, 0);

            var result = SetBonusEngine.Evaluate(ids);
            Assert.True(result.FireDamageMultiplierPct > 0f);
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
