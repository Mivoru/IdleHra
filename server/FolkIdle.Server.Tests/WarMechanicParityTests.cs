using System;
using System.Collections.Generic;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The War half's mechanics (rework slice 2.3), each held to the owner's
    /// rule that an hour away pays what an hour watched pays. Every row runs the
    /// REAL RunCombatTick and the offline projection from one payload
    /// (OfflineCombatParityTests' harness), and also proves the mechanic is
    /// LIVE at all: the same character without it kills measurably less. A
    /// mechanic wired into the projection but not the tick (or the reverse)
    /// fails one of the two.
    /// </summary>
    public class WarMechanicParityTests
    {
        private readonly ITestOutputHelper _output;

        public WarMechanicParityTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static int RegularOf(int region) => ContentRegistry.FirstCanonicalMonsterId + ((region - 1) * 5) + 2;

        private static TickStatePayload War(int region, int monsterId, params (int Id, byte Rank)[] ranks)
        {
            var payload = OfflineCombatParityTests.Character(region, geared: true, monsterId, food: 20_000);
            payload.SkillTreeVersion = 1;
            foreach (var (id, rank) in ranks) payload.SkillRanks[id] = rank;
            return payload;
        }

        public static IEnumerable<object[]> Rows()
        {
            int r3 = RegularOf(3), r5 = RegularOf(5);
            yield return new object[] { "crushing r3", 3, r3, new[] { WarTreeRegistry.CrushingBlow }, new byte[] { 3 } };
            yield return new object[] { "first strike r3", 3, r3, new[] { WarTreeRegistry.FirstStrike }, new byte[] { 5 } };
            // Not a one-shot fast killer: that row kills once per respawn whatever
            // the hit, so no damage mechanic can move it (measured 3,122 kills
            // with First Strike and 3,123 without).
            yield return new object[] { "crushing + first strike r5", 5, r5, new[] { WarTreeRegistry.CrushingBlow, WarTreeRegistry.FirstStrike }, new byte[] { 3, 5 } };
            yield return new object[] { "executioner r3", 3, r3, new[] { WarTreeRegistry.Executioner }, new byte[] { 5 } };
            // Region 4 gear on region 2's boss: a boss fight the character wins.
            yield return new object[] { "echo r3", 3, r3, new[] { WarTreeRegistry.Echo }, new byte[] { 5 } };
            yield return new object[] { "frenzy r3", 3, r3, new[] { WarTreeRegistry.Frenzy }, new byte[] { 5 } };
            yield return new object[] { "double strike r3", 3, r3, new[] { WarTreeRegistry.DoubleStrike }, new byte[] { 3 } };
            // Riposte answers the block's share of each connecting swing.
            yield return new object[] { "riposte + shield wall r3", 3, r3, new[] { WarTreeRegistry.ShieldWall, WarTreeRegistry.Riposte }, new byte[] { 5, 3 } };
            yield return new object[] { "ruin r3", 3, r3, new[] { WarTreeRegistry.Ruin }, new byte[] { 1 } };
            yield return new object[] { "perfect sequence r3", 3, r3, new[] { WarTreeRegistry.PerfectSequence }, new byte[] { 1 } };
            yield return new object[] { "bossbreaker on r2 boss", 4, RaceUnlockRegistry.GetRegionBossMonsterId(2), new[] { WarTreeRegistry.Bossbreaker }, new byte[] { 5 } };
        }

        [Theory]
        [MemberData(nameof(Rows))]
        public void A_war_mechanic_pays_the_same_away_as_watched(string row, int region, int monsterId, int[] ids, byte[] ranks)
        {
            var set = new (int, byte)[ids.Length];
            for (int i = 0; i < ids.Length; i++) set[i] = (ids[i], ranks[i]);

            var with = War(region, monsterId, set);
            var without = War(region, monsterId);
            var live = OfflineCombatParityTests.RunLive(with);
            var offline = OfflineCombatParityTests.RunOffline(with);
            var liveWithout = OfflineCombatParityTests.RunLive(without);

            _output.WriteLine($"{row,-22} kills offline {offline.Kills:F0} / live {live.Kills:F0}, live without it {liveWithout.Kills:F0}");

            Assert.Equal(0.0, live.DeathShare);
            Assert.True(live.Kills > liveWithout.Kills * 1.01, $"{row}: the live tick does not use it ({live.Kills:F0} vs {liveWithout.Kills:F0} without)");
            double slack = Math.Max(live.Kills * 0.05, 1.0);
            Assert.True(Math.Abs(offline.Kills - live.Kills) <= slack, $"{row}: offline {offline.Kills:F1}, live {live.Kills:F1}");
        }

        /// <summary>
        /// Bloodthirst heals rather than kills, so it shows in the larder: the
        /// same hour eats fewer fish with it, live and away alike.
        /// </summary>
        [Fact]
        public void Bloodthirst_eats_fewer_fish_away_as_watched()
        {
            int r4 = RegularOf(4);
            var with = War(4, r4, (WarTreeRegistry.Bloodthirst, 3));
            var without = War(4, r4);
            var live = OfflineCombatParityTests.RunLive(with);
            var offline = OfflineCombatParityTests.RunOffline(with);
            var liveWithout = OfflineCombatParityTests.RunLive(without);
            _output.WriteLine($"bloodthirst r4 food offline {offline.Food:F0} / live {live.Food:F0}, live without it {liveWithout.Food:F0}");

            Assert.True(live.Food < liveWithout.Food, $"the live tick does not heal with it ({live.Food:F0} fish vs {liveWithout.Food:F0} without)");
            Assert.True(Math.Abs(offline.Food - live.Food) <= Math.Max(live.Food * 0.05, 3.0), $"food: offline {offline.Food:F1}, live {live.Food:F1}");
        }

        /// <summary>
        /// Living Fortress trades every crit for block: the same hour kills
        /// fewer and eats fewer fish, and away agrees with watched on both.
        /// </summary>
        [Fact]
        public void Living_fortress_trades_crits_for_block_away_as_watched()
        {
            int r4 = RegularOf(4);
            var with = War(4, r4, (WarTreeRegistry.LivingFortress, 1));
            var without = War(4, r4);
            var live = OfflineCombatParityTests.RunLive(with);
            var offline = OfflineCombatParityTests.RunOffline(with);
            var liveWithout = OfflineCombatParityTests.RunLive(without);
            _output.WriteLine($"living fortress r4 kills {offline.Kills:F0}/{live.Kills:F0} (without {liveWithout.Kills:F0}), food {offline.Food:F0}/{live.Food:F0} (without {liveWithout.Food:F0})");

            Assert.True(live.Kills < liveWithout.Kills, "no crits should mean fewer kills");
            Assert.True(live.Food < liveWithout.Food, "more block should mean fewer fish");
            Assert.True(Math.Abs(offline.Kills - live.Kills) <= Math.Max(live.Kills * 0.05, 1.0), $"kills: offline {offline.Kills:F1}, live {live.Kills:F1}");
            Assert.True(Math.Abs(offline.Food - live.Food) <= Math.Max(live.Food * 0.05, 3.0), $"food: offline {offline.Food:F1}, live {live.Food:F1}");
        }

        [Fact]
        public void War_thunderer_and_golden_fleece_arm_the_same_rules()
        {
            var payload = War(3, RegularOf(3));
            Assert.False(FolkIdle.Server.Domain.Combat.SimulationEngine.HasGoldenFleece(in payload));
            payload.SkillRanks[WarTreeRegistry.GoldenFleece] = 1;
            payload.SkillRanks[WarTreeRegistry.Thunderer] = 3;
            Assert.True(FolkIdle.Server.Domain.Combat.SimulationEngine.HasGoldenFleece(in payload));
            Assert.Equal(5.0f, WarTreeRegistry.Resolve(in payload).ThundererTenthsPct / 1000f);
        }

        /// <summary>Last Stand on the War half arms the same rule today's crown does.</summary>
        [Fact]
        public void War_last_stand_is_the_same_last_stand()
        {
            var payload = War(3, RegularOf(3));
            Assert.False(FolkIdle.Server.Domain.Combat.SimulationEngine.HasLastStand(in payload));
            payload.SkillRanks[WarTreeRegistry.LastStand] = 1;
            Assert.True(FolkIdle.Server.Domain.Combat.SimulationEngine.HasLastStand(in payload));
        }
    }
}
