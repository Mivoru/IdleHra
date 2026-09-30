using System;
using System.Collections.Concurrent;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The offline projection against the REAL monster swing. Task 78 found
    /// that OfflineSimulationEngine priced incoming damage without the player's
    /// dodge or block (nor the 1,000 floor, nor the Dreadnought cap), which
    /// RunCombatTick applies - so the same hour cost more health away than
    /// watched, and a food-limited window ended early. The offline path now
    /// calls the tick's own helper; this runs RunCombatTick and holds it to
    /// what the tick measures, so the two cannot drift apart again unseen.
    /// </summary>
    public class OfflineDefenceParityTests
    {
        private readonly ITestOutputHelper _output;

        public OfflineDefenceParityTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        /// <summary>A regular from the middle of each region: 93, 98, 103, 108, 113.</summary>
        private static int RegularOf(int region) => ContentRegistry.FirstCanonicalMonsterId + ((region - 1) * 5) + 2;

        /// <summary>
        /// A human at the region's reference level, bare or in the
        /// region's wall-required gear (the loadout BossGearBenchmark prices).
        /// </summary>
        private static TickStatePayload Character(int region, bool geared, int monsterId, int food)
        {
            int level = BossGearBenchmark.ReferenceLevelForRegion(region);
            var payload = new TickStatePayload
            {
                PlayerId = 978_101L,
                CurrentLevel = level,
                ActiveActivityId = monsterId,
                AutoEatThreshold = 50,
                Food1_ItemId = FoodRegistry.FirstRawFishOfTier(region),
                Food1_Count = food,
                CachedCodexDamageMultiplier = 1f,
                CachedCodexYieldMultiplier = 1f,
            };
            // A registration's 50/50/50/25, and each level's points placed the
            // way the human growth table deals them (as BossGearBenchmark does) -
            // ApplyLevelUpGrowth only grants an unspent pool.
            RaceAttributeGrowth.GetGrowthPerLevel(RaceIds.Human, out int str, out int dex, out int con, out int lck);
            payload.STR = 50 + (str * (level - 1));
            payload.DEX = 50 + (dex * (level - 1));
            payload.CON = 50 + (con * (level - 1));
            payload.LCK = 25 + (lck * (level - 1));
            if (geared)
            {
                payload.CachedAffixTotals = BossGearBenchmark.BuildEquippedTotals(new ReferenceLoadout(
                    level, region, BossFirstClearRules.RequiredQualityTierFor(region), BossFirstClearRules.RequiredAffixRarityFor(region)));
            }
            var stats = SimulationEngine.LiveCombatStats(in payload);
            payload.PlayerHp = (int)SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);
            return payload;
        }

        /// <summary>
        /// The live tick's mean milli-damage per monster swing, measured over an
        /// hour. The player's damage is zeroed (codex multiplier 0) so the
        /// monster never dies and never resets its swing clock, and the bar is
        /// refilled far above any hit each tick, so every swing is counted and
        /// nothing clamps it. Only the monster's side of the fight is exercised.
        /// </summary>
        private static double LiveMilliDamagePerSwing(TickStatePayload payload)
        {
            payload.CachedCodexDamageMultiplier = 0f;
            payload.AutoEatThreshold = 0;
            payload.Food1_Count = 0;
            const int Topped = int.MaxValue / 2;
            var monster = ContentRegistry.Monsters[(int)payload.ActiveActivityId - 1];
            var guildWar = new ConcurrentQueue<GuildWarPointEvent>();
            var sessions = new ConcurrentDictionary<long, LiveSessionContext>();
            long taken = 0;
            int swings = 0;
            try
            {
                for (int tick = 0; tick < HuntingProjection.HorizonTicks; tick++)
                {
                    payload.PlayerHp = Topped;
                    SimulationEngine.RunCombatTick(ref payload, 100, 100, guildWar, sessions);
                    if (SimulationEngine.HasCrossedInterval(payload.CombatTargetTickAccumulator, monster.AttackIntervalMs))
                    {
                        swings++;
                        taken += Topped - (long)payload.PlayerHp;
                    }
                }
            }
            finally
            {
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            }
            Assert.True(swings > 100, $"only {swings} monster swings in an hour");
            return (double)taken / swings;
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(2, false)]
        [InlineData(2, true)]
        [InlineData(3, false)]
        [InlineData(3, true)]
        [InlineData(4, false)]
        [InlineData(4, true)]
        [InlineData(5, false)]
        [InlineData(5, true)]
        public void OfflineIncomingSwing_MatchesTheLiveTick(int region, bool geared)
        {
            int monsterId = RegularOf(region);
            var payload = Character(region, geared, monsterId, food: 0);
            var stats = SimulationEngine.LiveCombatStats(in payload);
            long maxMilliHp = SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);

            // The swing the offline fight charges (HuntingProjection.Advance).
            double offline = SimulationEngine.ExpectedMonsterMilliDamagePerSwing(in stats, monsterId, payload.DefeatedRegionBossMask, maxMilliHp);
            double live = LiveMilliDamagePerSwing(payload);

            _output.WriteLine(
                $"region {region} {(geared ? "geared" : "bare  ")} monster {monsterId}: dodge {stats.DodgeChancePct:F1}% block {stats.BlockStrengthPct:F1}% "
                + $"armour {stats.FlatPhysicalArmor} | offline {offline / 1000.0:F1} HP/swing, live {live / 1000.0:F1} ({offline / live:F3}x)");

            // Measured 2026-09-30: every row within 2% after the fix. Before
            // it, offline ran 1.11-1.43x the live figure on the same rows (the
            // 5% built-in miss and the CON block, never taken off). Five per
            // cent leaves room for an hour of hit and crit rolls and none for
            // either term going missing again.
            Assert.InRange(offline, live * 0.95, live * 1.05);
        }

        // Survival and food over a real window moved to OfflineCombatParityTests,
        // which runs the whole offline fight (not only its incoming half) beside
        // the live tick and holds kills, XP, gold, food and survival to 5%.
    }
}
