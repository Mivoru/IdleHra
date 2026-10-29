using System;
using System.Text;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The gathering speed extras MULTIPLY the core (tool tier + mastery +
    /// village) since TASK_BOARD 121's follow-up (owner, 2026-10-29); they used
    /// to be added to it, which made every small bonus shrink as the core grew.
    /// This pins the new shape and PRINTS the old-vs-new table, which is the
    /// measurement the change was judged on.
    /// </summary>
    public class GatherSpeedCompositionTests
    {
        private readonly ITestOutputHelper _out;
        public GatherSpeedCompositionTests(ITestOutputHelper output) => _out = output;

        // The formula before: everything summed, and a negative extra dropped.
        private static int OldTicks(int node, int mastery, int tier, int village, int extra)
        {
            int total = GatheringToolEngine.CoreSpeedBonusPct(mastery, tier, village) + Math.Max(0, extra);
            return Math.Max(GatheringToolEngine.MinRequiredTicks, node * 100 / (100 + total));
        }

        private static int NewTicks(int node, int mastery, int tier, int village, int extra)
            => GatheringToolEngine.ComputeRequiredTicks(node, mastery, tier, village, extra);

        private static double Gain(Func<int, int, int, int, int, int> f, int node, int m, int tier, int v, int extra)
            => (double)f(node, m, tier, v, 0) / f(node, m, tier, v, extra) - 1;

        private static readonly (string Name, int Mastery, int Tier, int Village)[] Stages =
        {
            ("early: tool tier 1, mastery 10", 10, 1, 0),
            ("middle: tier 5, mastery 50, village 5", 50, 5, 5),
            ("late: tier 8, mastery 100, village 10", 100, 8, 10),
        };

        [Fact]
        public void Old_versus_new_speed_for_each_extra()
        {
            int aptitudeMax = (int)BreedingAptitudes.BonusPercentFor(BreedingAptitudes.MaxValue);
            int harvestMax = SkillTreeRegistry.GetBonusTenthsOfPercent(SkillTreeRegistry.BoughHarvest, SkillTreeRegistry.BoughMaxLevel) / 10;
            var extras = new (string Name, int Pct)[]
            {
                ("Quick Hands trait +5", 5),
                ("Clumsy Hands flaw -6", -6),
                ("tool speed affix, production average 19", 19),
                ("tool speed affix, Legendary centre 55", 55),
                ($"Harvest bough at max +{harvestMax}", harvestMax),
                ($"Skill aptitude at max +{aptitudeMax}", aptitudeMax),
                ($"loaded: Harvest + affix 19 + Quick Hands + pet 8", harvestMax + 19 + 5 + 8),
            };

            var t = new StringBuilder();
            t.AppendLine("How much quicker than the same character with no extras. 'rate' = the formula on a huge node (no rounding);");
            t.AppendLine("'100-tick node' = what a real node of 100 base ticks does, where one tick is 0.1 s and small gains can round away.");
            t.AppendLine("stage | extra | old rate | NEW rate | old 100-tick node | NEW 100-tick node | ticks bare -> new");
            foreach (var (stage, m, tier, v) in Stages)
            {
                Assert.Equal(OldTicks(100, m, tier, v, 0), NewTicks(100, m, tier, v, 0)); // no extras: nothing changes
                foreach (var (name, pct) in extras)
                {
                    t.AppendLine(FormattableString.Invariant(
                        $"{stage} | {name} | {Gain(OldTicks, 1_000_000, m, tier, v, pct):+0.0%;-0.0%;0%} | {Gain(NewTicks, 1_000_000, m, tier, v, pct):+0.0%;-0.0%;0%} | {Gain(OldTicks, 100, m, tier, v, pct):+0.0%;-0.0%;0%} | {Gain(NewTicks, 100, m, tier, v, pct):+0.0%;-0.0%;0%} | {NewTicks(100, m, tier, v, 0)} -> {NewTicks(100, m, tier, v, pct)}"));
                }
            }
            _out.WriteLine(t.ToString());
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(50, 5, 5)]
        [InlineData(100, 8, 10)]
        public void A_five_percent_extra_is_five_percent_at_every_stage(int mastery, int tier, int village)
        {
            // On a big node, so the integer rounding is well under a percent.
            const int bigNode = 100_000;
            int bare = GatheringToolEngine.ComputeRequiredTicks(bigNode, mastery, tier, village, 0);
            int quick = GatheringToolEngine.ComputeRequiredTicks(bigNode, mastery, tier, village, 5);
            Assert.InRange((double)bare / quick, 1.045, 1.055);
        }

        [Fact]
        public void A_flaw_slows_and_the_extras_never_take_more_than_half()
        {
            int bare = GatheringToolEngine.ComputeRequiredTicks(100_000, 50, 5, 5, 0);
            int clumsy = GatheringToolEngine.ComputeRequiredTicks(100_000, 50, 5, 5, -6);
            Assert.True(clumsy > bare);
            int floor = GatheringToolEngine.ComputeRequiredTicks(100_000, 50, 5, 5, -500);
            Assert.Equal(GatheringToolEngine.ComputeRequiredTicks(100_000, 50, 5, 5, GatheringToolEngine.MinExtraSpeedPct), floor);
        }
    }
}
