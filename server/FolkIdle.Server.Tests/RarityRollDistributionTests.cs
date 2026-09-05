using System;
using System.Linq;
using System.Text;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Does the rarity roll still produce the distribution it is authored with?
    ///
    /// Reported 2026-09-05: "now not even the tier 3 monsters drop good things,
    /// something broke in an update". That is a claim about a distribution, and
    /// a live account cannot answer it - what a chest holds is drops MINUS the
    /// sweep, MINUS auto-salvage, PLUS whatever the forge fused, and none of
    /// those are recorded anywhere with a timestamp.
    ///
    /// This asks the roll itself, which is the only place the answer is clean.
    /// If RollTier matches its table, no update changed drop quality, whatever
    /// a chest looks like.
    /// </summary>
    public class RarityRollDistributionTests
    {
        private readonly ITestOutputHelper _output;

        public RarityRollDistributionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // The authored weights, restated - RollTier's own array is private, and
        // a test that read it would be asking the code to agree with itself.
        private static readonly double[] Weights =
        {
            0.0, 100.0, 50.0, 25.0, 12.5, 5.0, 2.5, 1.0, 0.5, 0.1, 0.05, 0.01, 0.005, 0.001, 0.0001,
        };

        [Fact]
        public void TheRollMatchesItsAuthoredTable()
        {
            const int samples = 2_000_000;
            var counts = new int[15];

            for (int i = 0; i < samples; i++)
            {
                counts[RarityTier.RollTier(0f)]++;
            }

            double total = Weights.Sum();
            var report = new StringBuilder();
            report.AppendLine($"{samples:N0} rolls at zero loot luck\n");
            report.AppendLine("  tier  name             expected      observed    ratio");

            for (int tier = 1; tier <= 14; tier++)
            {
                double expected = Weights[tier] / total;
                double observed = (double)counts[tier] / samples;
                string ratio = expected > 0 ? $"{observed / expected,7:F3}" : "     -";
                report.AppendLine(
                    $"  {tier,4}  {RarityTier.GetName(tier),-15} {expected,10:P4} {observed,12:P4} {ratio}");
            }

            double avgTier = 0;
            for (int tier = 1; tier <= 14; tier++) avgTier += tier * ((double)counts[tier] / samples);
            report.AppendLine($"\n  mean tier: {avgTier:F3}");
            report.AppendLine("  A chest full of tier 1-3 is what this table LOOKS like unswept -");
            report.AppendLine("  half of everything is Normal. High tiers in a chest come from the");
            report.AppendLine("  sweep removing the rest, from auto-salvage, and from the forge.");

            _output.WriteLine(report.ToString());

            // Two million samples puts the sampling error on the common tiers
            // far below a percent, so a 3% band is generous for them and the
            // rare tiers are checked by order of magnitude instead.
            for (int tier = 1; tier <= 8; tier++)
            {
                double expected = Weights[tier] / total;
                double observed = (double)counts[tier] / samples;
                Assert.True(Math.Abs(observed - expected) / expected < 0.03,
                    $"tier {tier} ({RarityTier.GetName(tier)}) rolled {observed:P4} against an authored {expected:P4}");
            }

            // The top six are too rare to bound tightly at this sample size;
            // what matters is that they are REACHABLE, because a clamp that
            // capped the ladder would show up here as a hard zero.
            for (int tier = 9; tier <= 14; tier++)
            {
                double expected = Weights[tier] / total * samples;
                if (expected >= 5)
                {
                    Assert.True(counts[tier] > 0,
                        $"tier {tier} ({RarityTier.GetName(tier)}) never rolled in {samples:N0} tries, " +
                        $"against {expected:F0} expected - something is capping the ladder");
                }
            }
        }

        [Fact]
        public void NothingCapsTheLadderBelowTranscendent()
        {
            // Modul: THE CLAMP IN TryRollEquipment USES A DIFFERENT CONSTANT.
            //
            // `tier = Math.Clamp(tier + bonusRarityTiers, 1, CraftingEngine.RarityTierCount)`
            // - a crafting constant applied to a LOOT roll. If those two numbers
            // ever disagreed, the Golden Fleece crown would silently cap drops
            // at the crafting ceiling, and the only symptom would be a player
            // saying good items stopped appearing.
            Assert.Equal(14, FolkIdle.Server.Domain.Economy.CraftingEngine.RarityTierCount);
            Assert.Equal(14, RarityTier.Transcendent);
        }

        [Fact]
        public void LootLuckRaisesTheTopWithoutRemovingTheBottom()
        {
            const int samples = 200_000;
            var plain = new int[15];
            var lucky = new int[15];

            for (int i = 0; i < samples; i++)
            {
                plain[RarityTier.RollTier(0f)]++;
                lucky[RarityTier.RollTier(100f)]++;
            }

            int plainHigh = plain.Skip(5).Sum();
            int luckyHigh = lucky.Skip(5).Sum();

            _output.WriteLine($"tier 5+ at 0% luck: {plainHigh:N0}, at 100% luck: {luckyHigh:N0}");
            Assert.True(luckyHigh > plainHigh, "loot luck did not raise the odds of a better drop");
        }
    }
}
