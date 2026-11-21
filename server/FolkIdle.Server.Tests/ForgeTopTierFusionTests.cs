using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The top of the fusion ladder (owner, 2026-11-21): four Demonic make a
    /// Godly, five Godly a Transcendent, and both fees double. A Transcendent
    /// from Demonic is twenty identical pieces and about nine billion gold.
    /// </summary>
    public class ForgeTopTierFusionTests
    {
        [Theory]
        [InlineData(RarityTier.Normal, 3)]
        [InlineData(RarityTier.Divine, 3)]
        [InlineData(RarityTier.Demonic, 4)]
        [InlineData(RarityTier.Godly, 5)]
        public void PiecesPerFusion(int fromTier, int pieces)
            => Assert.Equal(pieces, ForgeSplicingEngine.PiecesFor(fromTier));

        [Fact]
        public void TopFees_AreDoubled_TheRestUnchanged()
        {
            Assert.Equal(238_418_580L, ForgeSplicingEngine.FusionFee(RarityTier.Divine, 0));
            Assert.Equal(1_192_092_896L, ForgeSplicingEngine.FusionFee(RarityTier.Demonic, 0));
            Assert.Equal(2_980_232_240L, ForgeSplicingEngine.FusionFee(RarityTier.Godly, 0));
        }

        [Fact]
        public void TwentyDemonic_MakeOneTranscendent_ForAboutNineBillion()
        {
            var counts = new int[ForgeSplicingEngine.MaxQualityTier + 1];
            counts[RarityTier.Demonic] = 20;
            var plan = ForgeSplicingEngine.PlanStack(counts, RarityTier.Demonic, RarityTier.Transcendent,
                forgeLevel: 14, feeDiscount: 0, goldAvailable: long.MaxValue / 4);

            Assert.Equal(5, plan.FusionsByTier[RarityTier.Demonic]);
            Assert.Equal(1, plan.FusionsByTier[RarityTier.Godly]);
            Assert.Equal(1, plan.CountsAfter[RarityTier.Transcendent]);
            Assert.Equal(0, plan.CountsAfter[RarityTier.Demonic]);
            Assert.Equal(0, plan.CountsAfter[RarityTier.Godly]);
            Assert.InRange(plan.GoldCost, 8_900_000_000L, 9_000_000_000L);
        }

        [Fact]
        public void EightDemonicAndOneGodly_MakeNoTranscendent()
        {
            // The strongest account's best stack on 2026-11-21 (Doom Crown).
            var counts = new int[ForgeSplicingEngine.MaxQualityTier + 1];
            counts[RarityTier.Demonic] = 8;
            counts[RarityTier.Godly] = 1;
            var plan = ForgeSplicingEngine.PlanStack(counts, RarityTier.Demonic, RarityTier.Transcendent,
                forgeLevel: 14, feeDiscount: 0, goldAvailable: long.MaxValue / 4);

            Assert.Equal(2, plan.FusionsByTier[RarityTier.Demonic]);
            Assert.Equal(3, plan.CountsAfter[RarityTier.Godly]);
            Assert.Equal(0, plan.CountsAfter[RarityTier.Transcendent]);
        }

        [Fact]
        public void BelowDemonic_StaysThreeToOne()
        {
            var counts = new int[ForgeSplicingEngine.MaxQualityTier + 1];
            counts[RarityTier.Divine] = 9;
            var plan = ForgeSplicingEngine.PlanStack(counts, RarityTier.Divine, RarityTier.Demonic,
                forgeLevel: 14, feeDiscount: 0, goldAvailable: long.MaxValue / 4);
            Assert.Equal(3, plan.FusionsByTier[RarityTier.Divine]);
            Assert.Equal(3, plan.CountsAfter[RarityTier.Demonic]);
        }
    }
}
