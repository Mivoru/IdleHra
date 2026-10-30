using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
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

        /// <summary>The x4 test scale (WarTreeRegistry.TestValueScale) every row here runs at.</summary>
        private const int Scale = 4;

        private static TickStatePayload War(int region, int monsterId, params (int Id, byte Rank)[] ranks)
        {
            WarTreeRegistry.TestValueScale.Value = Scale;
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
            // Ruin is a TRADE: x1.7 every 8th hit against attack speed counting
            // 85%, worth little on this fast region-5 character (+1.8% by the
            // projection, inside one live hour's noise), so the row proves the
            // tick reads it either way rather than that it pays.
            yield return new object[] { "ruin r5", 5, r5, new[] { WarTreeRegistry.Ruin }, new byte[] { 1 }, true };
            yield return new object[] { "perfect sequence r3", 3, r3, new[] { WarTreeRegistry.PerfectSequence }, new byte[] { 1 } };
            // The bridges (owner, 2026-10-30). Character() beats every boss
            // behind the monster, so region 3 is the highest unlocked one.
            yield return new object[] { "piercing crit r3", 3, r3, new[] { WarTreeRegistry.PiercingCrit }, new byte[] { 3 } };
            // Counterstroke converts defence, so it rides Shield Wall's block, as
            // Riposte's row does; block alone moves no kills.
            yield return new object[] { "counterstroke + shield wall r3", 3, r3, new[] { WarTreeRegistry.ShieldWall, WarTreeRegistry.Counterstroke }, new byte[] { 5, 3 } };
            yield return new object[] { "know your prey r3", 3, r3, new[] { WarTreeRegistry.KnowYourPrey }, new byte[] { 3 } };
        }

        /// <summary>
        /// Bossbreaker is the War half's First Blood: at 5 ranks a first-clear
        /// boss has exactly the health today's First Blood 8 leaves it, the
        /// relief BossGearBenchmark calibrates every wall against.
        /// </summary>
        [Fact]
        public void Bossbreaker_softens_a_wall_exactly_as_first_blood_does()
        {
            int boss = RaceUnlockRegistry.GetRegionBossMonsterId(2);
            var war = War(4, boss, (WarTreeRegistry.Bossbreaker, 5));
            // Today's First Blood at its cap, the relief the walls were solved against.
            float firstBlood = SkillTreeRegistry.GetBonusPercent(
                SkillTreeRegistry.BoughFirstBlood, SkillTreeRegistry.MaxLevelOf(SkillTreeRegistry.BoughFirstBlood)) / 100f;
            var bare = War(4, boss);
            WarTreeRegistry.TestValueScale.Value = 1; // exact values: the real numbers

            Assert.True(BossFirstClearRules.IsFirstClearPending(war.DefeatedRegionBossMask, boss));
            long warHp = BossFirstClearRules.MaxHpFor(war.DefeatedRegionBossMask, boss, BossFirstClearRules.ReliefFor(in war));
            long todayHp = BossFirstClearRules.MaxHpFor(war.DefeatedRegionBossMask, boss, firstBlood);
            long bareHp = BossFirstClearRules.MaxHpFor(bare.DefeatedRegionBossMask, boss, BossFirstClearRules.ReliefFor(in bare));
            Assert.Equal(todayHp, warHp);
            Assert.True(warHp < bareHp);
            Assert.Equal(firstBlood, BossFirstClearRules.MaxRelief, 4);
        }

        /// <summary>
        /// Know Your Prey pays gold as well as damage, the same away as
        /// watched, and only on the frontier: a region already behind the
        /// player gets nothing.
        /// </summary>
        [Fact]
        public void Know_your_prey_pays_gold_on_the_frontier_only()
        {
            int r3 = RegularOf(3);
            var with = War(3, r3, (WarTreeRegistry.KnowYourPrey, 3));
            var without = War(3, r3);
            var live = OfflineCombatParityTests.RunLive(with);
            var offline = OfflineCombatParityTests.RunOffline(with);
            var liveWithout = OfflineCombatParityTests.RunLive(without);
            _output.WriteLine($"know your prey r3 gold {offline.Gold:F0}/{live.Gold:F0} (without {liveWithout.Gold:F0})");

            Assert.True(live.Gold > liveWithout.Gold * 1.05, $"the live kill does not pay it ({live.Gold:F0} vs {liveWithout.Gold:F0} without)");
            Assert.True(Math.Abs(offline.Gold - live.Gold) <= live.Gold * 0.05, $"gold: offline {offline.Gold:F0}, live {live.Gold:F0}");

            var behind = with;
            behind.DefeatedRegionBossMask = BossFirstClearRules.MarkDefeated(behind.DefeatedRegionBossMask, RaceUnlockRegistry.GetRegionBossMonsterId(3));
            var monster = ContentRegistry.Monsters[r3 - 1];
            Assert.Equal(r3, monster.Id);
            Assert.Equal(WarTreeRegistry.Get(WarTreeRegistry.KnowYourPrey)!.ValueAt(3) * Scale, FolkIdle.Server.Domain.Combat.SimulationEngine.KnowYourPreyTenthsPct(in with, in monster));
            Assert.Equal(0, FolkIdle.Server.Domain.Combat.SimulationEngine.KnowYourPreyTenthsPct(in behind, in monster));
        }

        /// <summary>Counterstroke reads the Guard column's dodge and block: more defence, more crit.</summary>
        [Fact]
        public void Counterstroke_turns_defence_into_crit_chance()
        {
            var payload = War(3, RegularOf(3), (WarTreeRegistry.Counterstroke, 3));
            var stats = FolkIdle.Server.Domain.Combat.SimulationEngine.LiveCombatStats(in payload);
            float before = FolkIdle.Server.Domain.Combat.SimulationEngine.LiveCritChancePct(in payload, in stats);
            payload.SkillRanks[WarTreeRegistry.ShieldWall] = 5;
            stats = FolkIdle.Server.Domain.Combat.SimulationEngine.LiveCombatStats(in payload);
            float after = FolkIdle.Server.Domain.Combat.SimulationEngine.LiveCritChancePct(in payload, in stats);
            // Shield Wall's added block, times Counterstroke's share of it.
            float block = WarTreeRegistry.Get(WarTreeRegistry.ShieldWall)!.ValueAt(5) * Scale / 10f;
            float share = WarTreeRegistry.Get(WarTreeRegistry.Counterstroke)!.ValueAt(3) * Scale / 1000f;
            Assert.Equal(before + block * share, after, 2);
        }

        [Theory]
        [MemberData(nameof(Rows))]
        public void A_war_mechanic_pays_the_same_away_as_watched(string row, int region, int monsterId, int[] ids, byte[] ranks, bool eitherWay = false)
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
            bool moved = eitherWay
                ? Math.Abs(live.Kills - liveWithout.Kills) > liveWithout.Kills * 0.01
                : live.Kills > liveWithout.Kills * 1.01;
            Assert.True(moved, $"{row}: the live tick does not use it ({live.Kills:F0} vs {liveWithout.Kills:F0} without)");
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
            WarTreeRegistry.TestValueScale.Value = 1; // exact values: the real numbers
            payload.SkillRanks[WarTreeRegistry.GoldenFleece] = 1;
            payload.SkillRanks[WarTreeRegistry.Thunderer] = 3;
            Assert.True(FolkIdle.Server.Domain.Combat.SimulationEngine.HasGoldenFleece(in payload));
            Assert.Equal(5.0f, WarTreeRegistry.Resolve(in payload).ThundererTenthsPct / 1000f);
        }

        /// <summary>Golden Touch and Quick Study pay the same gold and XP away as watched, and more than without.</summary>
        [Fact]
        public void Fortune_gold_and_xp_pay_away_as_watched()
        {
            int r3 = RegularOf(3);
            var with = War(3, r3, (WarTreeRegistry.GoldenTouch, 5), (WarTreeRegistry.QuickStudy, 5));
            var without = War(3, r3);
            var live = OfflineCombatParityTests.RunLive(with);
            var offline = OfflineCombatParityTests.RunOffline(with);
            var liveWithout = OfflineCombatParityTests.RunLive(without);
            _output.WriteLine($"fortune r3 gold {offline.Gold:F0}/{live.Gold:F0} (without {liveWithout.Gold:F0}), xp {offline.Xp:F0}/{live.Xp:F0} (without {liveWithout.Xp:F0})");

            Assert.True(live.Gold > liveWithout.Gold * 1.10 && live.Xp > liveWithout.Xp * 1.03);
            Assert.True(Math.Abs(offline.Gold - live.Gold) <= live.Gold * 0.05, $"gold: offline {offline.Gold:F0}, live {live.Gold:F0}");
            Assert.True(Math.Abs(offline.Xp - live.Xp) <= live.Xp * 0.05, $"xp: offline {offline.Xp:F0}, live {live.Xp:F0}");
        }

        /// <summary>Codex Scholar raises a real codex bonus, live and away alike.</summary>
        [Fact]
        public void Codex_scholar_raises_the_codex_bonus_away_as_watched()
        {
            int r3 = RegularOf(3);
            var with = War(3, r3, (WarTreeRegistry.CodexScholar, 3));
            var without = War(3, r3);
            with.CachedCodexDamageMultiplier = 2f;
            without.CachedCodexDamageMultiplier = 2f;
            var live = OfflineCombatParityTests.RunLive(with);
            var offline = OfflineCombatParityTests.RunOffline(with);
            var liveWithout = OfflineCombatParityTests.RunLive(without);
            _output.WriteLine($"codex scholar r3 kills {offline.Kills:F0}/{live.Kills:F0} (without {liveWithout.Kills:F0})");

            // Codex x2.0 is a +1.0 bonus, made larger by Codex Scholar's share.
            float expected = 1f + 1f * (1f + WarTreeRegistry.Get(WarTreeRegistry.CodexScholar)!.ValueAt(3) * Scale / 1000f);
            Assert.Equal(expected, FolkIdle.Server.Domain.Combat.SimulationEngine.LiveCodexDamageMultiplier(in with), 3);
            Assert.True(live.Kills > liveWithout.Kills * 1.01);
            Assert.True(Math.Abs(offline.Kills - live.Kills) <= Math.Max(live.Kills * 0.05, 1.0), $"kills: offline {offline.Kills:F1}, live {live.Kills:F1}");
        }

        /// <summary>Scavenger, Rarity and Plenty feed the same loot sums today's Fortune nodes do.</summary>
        [Fact]
        public void Fortune_loot_nodes_reach_the_drop_request()
        {
            var payload = War(3, RegularOf(3), (WarTreeRegistry.Scavenger, 5), (WarTreeRegistry.Rarity, 5));
            WarTreeRegistry.TestValueScale.Value = 1; // exact values: the real numbers
            var stats = FolkIdle.Server.Domain.Combat.SimulationEngine.LiveCombatStats(in payload);
            var odds = LootLuckBreakdown.From(in payload, in stats);
            Assert.Equal(7.5f, odds.FortuneRoot, 3);
            Assert.Equal(7.5f, odds.RarityBough, 3);
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
