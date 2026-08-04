using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: the region rule itself, tested away from a database.
    //
    // The engines that use it are covered through Postgres fixtures, which is
    // right for the plumbing and wrong for the rule: those tests each pin one
    // point, and the interesting parts of this rule are its edges - the boss
    // killed out of order, the last region, the legacy tiers that have no boss.
    public class RegionUnlockGateTests
    {
        private static HashSet<int> BossesFor(params int[] regions)
        {
            var ids = new HashSet<int>();
            foreach (int region in regions)
            {
                ids.Add(RaceUnlockRegistry.GetRegionBossMonsterId(region));
            }
            return ids;
        }

        [Fact]
        public void A_new_account_holds_region_one_and_nothing_further()
        {
            var none = new HashSet<int>();

            Assert.Equal(1, RegionUnlockGate.HighestUnlockedRegion(none));
            Assert.True(RegionUnlockGate.CanEnterRegion(1, none));
            Assert.False(RegionUnlockGate.CanEnterRegion(2, none));

            // The whole reason this rework happened: a region-1 Epic was
            // refused to the only characters who could farm it.
            Assert.True(RegionUnlockGate.CanWearRegionTier(1, none));
        }

        [Fact]
        public void Each_boss_opens_exactly_one_more_region()
        {
            Assert.Equal(2, RegionUnlockGate.HighestUnlockedRegion(BossesFor(1)));
            Assert.Equal(3, RegionUnlockGate.HighestUnlockedRegion(BossesFor(1, 2)));
            Assert.Equal(4, RegionUnlockGate.HighestUnlockedRegion(BossesFor(1, 2, 3)));
            Assert.Equal(5, RegionUnlockGate.HighestUnlockedRegion(BossesFor(1, 2, 3, 4)));
        }

        [Fact]
        public void Clearing_the_last_boss_does_not_invent_a_sixth_region()
        {
            var all = BossesFor(1, 2, 3, 4, 5);

            Assert.Equal(5, RegionUnlockGate.HighestUnlockedRegion(all));
            Assert.False(RegionUnlockGate.CanEnterRegion(6, all));
        }

        [Fact]
        public void A_boss_beaten_out_of_order_unlocks_nothing_past_the_gap()
        {
            // Region 3's boss down but region 1's still standing. Counting
            // bosses would answer 2; the rule is consecutive, so it answers 1.
            var outOfOrder = BossesFor(3);

            Assert.Equal(1, RegionUnlockGate.HighestUnlockedRegion(outOfOrder));
            Assert.False(RegionUnlockGate.CanEnterRegion(2, outOfOrder));
        }

        [Fact]
        public void Legacy_gear_beyond_the_canonical_regions_needs_the_whole_game_cleared()
        {
            // Tiers 6-10 belong to the 90 legacy monsters and have no boss of
            // their own. Treating "no boss guards it" as "anyone may wear it"
            // made the strongest gear in the game the one thing a fresh account
            // could equip.
            var four = BossesFor(1, 2, 3, 4);
            Assert.Equal(5, RegionUnlockGate.HighestUnlockedRegion(four));
            Assert.False(RegionUnlockGate.CanWearRegionTier(9, four));

            var all = BossesFor(1, 2, 3, 4, 5);
            Assert.True(RegionUnlockGate.CanWearRegionTier(9, all));
        }

        [Fact]
        public void Rarity_is_not_part_of_the_rule()
        {
            // There is deliberately no quality parameter to pass. Region 1
            // gear is wearable in region 1 at every rarity the game rolls, and
            // this test exists so that reintroducing a quality term has to
            // break something visible.
            var none = new HashSet<int>();

            Assert.True(RegionUnlockGate.CanWearRegionTier(1, none));
            Assert.False(RegionUnlockGate.CanWearRegionTier(2, none));
        }

        [Fact]
        public void Boss_ids_round_trip_through_both_directions()
        {
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
                Assert.Equal(region, RaceUnlockRegistry.GetRegionForBossMonsterId(bossId));
            }

            // 111 is a real region-5 monster and not its boss. An arithmetic
            // inverse ((id - 90) / 5) would answer 4 here.
            Assert.Equal(0, RaceUnlockRegistry.GetRegionForBossMonsterId(111));
            Assert.Equal(0, RaceUnlockRegistry.GetRegionForBossMonsterId(94));
        }
    }
}
