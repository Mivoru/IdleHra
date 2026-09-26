using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: task 47 - offline catch-up used to make up to 200,000 weighted loot
    // rolls one at a time. It now draws one binomial per table entry
    // (OfflineSimulationEngine.DrawLootCounts). These pin the three things the
    // swap must not change: the counts sum to exactly the roll count (so the
    // caps upstream still mean what they meant), each entry's mean is still
    // n * w_i / W, and luck still shifts share toward the rare entry. And the
    // one thing it must change: the work no longer grows with the roll count.
    public class OfflineLootDrawTests
    {
        private static readonly LootTableEntry[] Table =
        {
            new LootTableEntry { ItemId = 1, Weight = 700 },
            new LootTableEntry { ItemId = 2, Weight = 250 },
            new LootTableEntry { ItemId = 3, Weight = 45 },
            new LootTableEntry { ItemId = 4, Weight = 5 },
        };

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(400)]
        [InlineData(200_000)]
        public void Counts_sum_to_exactly_the_roll_count(int rolls)
        {
            var rng = new Random(rolls);
            for (int trial = 0; trial < 200; trial++)
            {
                Dictionary<int, long> counts = OfflineSimulationEngine.DrawLootCounts(Table, rolls, 0, rng);
                Assert.Equal(rolls, counts.Values.Sum());
            }
        }

        [Theory]
        [InlineData(20, 0)]
        [InlineData(5_000, 0)]
        [InlineData(200_000, 0)]
        [InlineData(5_000, 500)]
        public void Each_entry_mean_matches_the_per_roll_expectation(int rolls, int luckBonus)
        {
            const int trials = 2_000;
            var rng = new Random(12345 + rolls + luckBonus);
            double totalWeight = Table.Sum(e => (double)e.Weight + luckBonus);
            var sums = new double[Table.Length];

            for (int trial = 0; trial < trials; trial++)
            {
                Dictionary<int, long> counts = OfflineSimulationEngine.DrawLootCounts(Table, rolls, luckBonus, rng);
                for (int i = 0; i < Table.Length; i++)
                {
                    counts.TryGetValue(Table[i].ItemId, out long c);
                    sums[i] += c;
                }
            }

            for (int i = 0; i < Table.Length; i++)
            {
                double p = (Table[i].Weight + luckBonus) / totalWeight;
                double expected = rolls * p;
                double observed = sums[i] / trials;
                // Five standard errors of the mean of `trials` binomial draws,
                // plus a quarter unit for the normal branch's rounding.
                double tolerance = 5.0 * Math.Sqrt(rolls * p * (1 - p) / trials) + 0.25;
                Assert.True(Math.Abs(observed - expected) <= tolerance,
                    $"entry {Table[i].ItemId}: expected {expected:F3}, observed {observed:F3}, tolerance {tolerance:F3}");
            }
        }

        [Fact]
        public void Luck_still_shifts_share_toward_the_rare_entry()
        {
            var rng = new Random(7);
            long plain = 0, lucky = 0;
            for (int trial = 0; trial < 200; trial++)
            {
                plain += OfflineSimulationEngine.DrawLootCounts(Table, 1_000, 0, rng).GetValueOrDefault(4);
                lucky += OfflineSimulationEngine.DrawLootCounts(Table, 1_000, 500, rng).GetValueOrDefault(4);
            }
            Assert.True(lucky > plain * 5, $"luck moved the rare entry from {plain} to only {lucky}");
        }

        [Fact]
        public void Duplicate_item_ids_add_up_and_zero_weight_entries_never_drop()
        {
            var table = new[]
            {
                new LootTableEntry { ItemId = 9, Weight = 50 },
                new LootTableEntry { ItemId = 8, Weight = 0 },
                new LootTableEntry { ItemId = 9, Weight = 50 },
            };
            Dictionary<int, long> counts = OfflineSimulationEngine.DrawLootCounts(table, 10_000, 0, new Random(1));
            Assert.Equal(10_000, counts[9]);
            Assert.False(counts.ContainsKey(8));
        }

        [Fact]
        public void Binomial_edges_are_exact()
        {
            var rng = new Random(3);
            Assert.Equal(0, OfflineSimulationEngine.SampleBinomial(rng, 0, 0.5));
            Assert.Equal(0, OfflineSimulationEngine.SampleBinomial(rng, 100, 0.0));
            Assert.Equal(100, OfflineSimulationEngine.SampleBinomial(rng, 100, 1.0));
            for (int i = 0; i < 1_000; i++)
            {
                int k = OfflineSimulationEngine.SampleBinomial(rng, 50, 0.97);
                Assert.InRange(k, 0, 50);
            }
        }

        // The old loop was 200,000 table walks per slot per login. The draw is
        // one per entry, so a full-cap catch-up costs the same as a tiny one.
        // Bounded loosely enough not to flake on a loaded CI runner, and far
        // below what 1,000 x 200,000 per-roll draws could ever reach.
        [Fact]
        public void Runtime_does_not_scale_with_the_roll_count()
        {
            var rng = new Random(11);
            OfflineSimulationEngine.DrawLootCounts(Table, 200_000, 0, rng); // warm up

            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 1_000; i++)
            {
                OfflineSimulationEngine.DrawLootCounts(Table, 200_000, 0, rng);
            }
            watch.Stop();

            Assert.True(watch.ElapsedMilliseconds < 1_000,
                $"1,000 full-cap draws took {watch.ElapsedMilliseconds} ms");
        }
    }
}
