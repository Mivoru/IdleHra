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

        /// <summary>The live tick for an hour of real fighting: food eaten, and whether it died.</summary>
        private static (int FoodEaten, bool Died, int SecondsAlive) RunLive(TickStatePayload payload)
        {
            var guildWar = new ConcurrentQueue<GuildWarPointEvent>();
            var sessions = new ConcurrentDictionary<long, LiveSessionContext>();
            int foodBefore = payload.Food1_Count;
            try
            {
                for (int tick = 0; tick < HuntingProjection.HorizonTicks; tick++)
                {
                    SimulationEngine.RunCombatTick(ref payload, 100, 100, guildWar, sessions);
                    if (payload.ActiveActivityId == 0)
                    {
                        return (foodBefore - payload.Food1_Count, true, tick / 10);
                    }
                }
                return (foodBefore - payload.Food1_Count, false, 3600);
            }
            finally
            {
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            }
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

            double offline = OfflineSimulationEngine.ExpectedIncomingMilliDamagePerSwing(in payload, in stats, monsterId, maxMilliHp);
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

        /// <summary>
        /// What the fix does to a real offline window: how long an empty larder
        /// lasts, and what an hour eats with a full one, against the live tick.
        /// </summary>
        [Theory]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(2, false)]
        [InlineData(2, true)]
        [InlineData(3, false)]
        [InlineData(3, true)]
        [InlineData(4, true)]
        [InlineData(5, true)]
        public void OfflineSurvivalAndFood_TrackTheLiveTick(int region, bool geared)
        {
            int monsterId = RegularOf(region);

            var hungry = Character(region, geared, monsterId, food: 0);
            var stats = SimulationEngine.LiveCombatStats(in hungry);
            var offlineHungry = OfflineSimulationEngine.ProjectCombatSustain(in hungry, in stats, monsterId, 3600);
            var liveHungry = RunLive(hungry);

            var fed = Character(region, geared, monsterId, food: 20_000);
            var offlineFed = OfflineSimulationEngine.ProjectCombatSustain(in fed, in stats, monsterId, 3600);
            var liveFed = RunLive(fed);

            _output.WriteLine(
                $"region {region} {(geared ? "geared" : "bare  ")}: no food - offline {offlineHungry.SustainedSeconds:F0}s, live {liveHungry.SecondsAlive}s (died {liveHungry.Died}); "
                + $"food/h - offline {offlineFed.FoodUnitsConsumed}, live {liveFed.FoodEaten} (died {liveFed.Died})");

            Assert.False(liveFed.Died);
            Assert.Equal(3600.0, offlineFed.SustainedSeconds);

            // An empty larder: how long the bar lasts. Measured 2026-09-30 at
            // 0.83-1.00x the live tick after the fix and 0.66-0.86x before it.
            // The residue below 1.0 is not defence - the offline model charges a
            // swing every monster interval, while the live tick restarts the
            // monster's swing clock at every kill, so a fast killer is hit less
            // often live. It is largest on region 1, where kills take seconds.
            Assert.InRange(offlineHungry.SustainedSeconds, liveHungry.SecondsAlive * 0.78, liveHungry.SecondsAlive * 1.10);

            // A full larder: bites an hour. Offline may overstate (same swing
            // clock residue - measured 1.0-1.65x after the fix, 1.19-1.88x
            // before) but must never promise a cheaper hour than the live tick.
            Assert.InRange((double)offlineFed.FoodUnitsConsumed, (liveFed.FoodEaten * 0.90) - 2, (liveFed.FoodEaten * 1.80) + 5);
        }
    }
}
