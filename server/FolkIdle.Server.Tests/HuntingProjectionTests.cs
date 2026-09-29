using System;
using System.Collections.Concurrent;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 78: the hunting advisor against the REAL fight. Each test runs
    /// SimulationEngine.RunCombatTick - the live tick, rolls and all - for an
    /// hour of game time and holds HuntingProjection to what it measured. The
    /// projection replaces every roll with its expectation, so this is the only
    /// thing that notices when the two drift. It is the guard the task asked
    /// for, and the reason the projection calls the tick's own helpers.
    /// </summary>
    public class HuntingProjectionTests
    {
        private readonly ITestOutputHelper _output;

        public HuntingProjectionTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static TickStatePayload Character(int level, int str, int dex, int con, int monsterId, int food)
        {
            var payload = new TickStatePayload
            {
                PlayerId = 978_001L,
                CurrentLevel = level,
                STR = str,
                DEX = dex,
                CON = con,
                LCK = 25,
                ActiveActivityId = monsterId,
                AutoEatThreshold = 50,
                Food1_ItemId = FoodRegistry.FirstRawFishOfTier(ContentRegistry.GetMonsterRegionTier(monsterId)),
                Food1_Count = food,
                CachedCodexDamageMultiplier = 1f,
            };
            var stats = SimulationEngine.LiveCombatStats(in payload);
            payload.PlayerHp = (int)SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);
            return payload;
        }

        /// <summary>The live tick for up to an hour: kills, and whether it died.</summary>
        private static (int Kills, bool Died, int FoodEaten) RunLive(TickStatePayload payload)
        {
            var guildWar = new ConcurrentQueue<GuildWarPointEvent>();
            var sessions = new ConcurrentDictionary<long, LiveSessionContext>();
            int kills = 0;
            int foodBefore = payload.Food1_Count;
            try
            {
                for (int tick = 0; tick < HuntingProjection.HorizonTicks; tick++)
                {
                    int monsterBefore = payload.CurrentMonsterId;
                    long hpBefore = payload.CurrentMonsterHp;
                    SimulationEngine.RunCombatTick(ref payload, 100, 100, guildWar, sessions);
                    if (payload.ActiveActivityId == 0)
                    {
                        return (kills, true, foodBefore - payload.Food1_Count);
                    }
                    // A kill respawns the monster at full health in the same tick.
                    if (monsterBefore != 0 && payload.CurrentMonsterHp > hpBefore) kills++;
                }
                return (kills, false, foodBefore - payload.Food1_Count);
            }
            finally
            {
                // The live kill enqueues loot on a STATIC queue - leave nothing
                // behind for another test's worker (server/CLAUDE.md).
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            }
        }

        [Theory]
        [InlineData(20, 40, 40, 40, 91)]    // an early character on the first monster
        [InlineData(20, 40, 40, 40, 94)]    // the same character a rung up
        [InlineData(45, 120, 90, 90, 97)]   // mid-game, region 2
        public void KillsPerHour_MatchTheLiveTick_WithinTenPercent(int level, int str, int dex, int con, int monsterId)
        {
            var payload = Character(level, str, dex, con, monsterId, food: 5000);
            var estimate = HuntingProjection.Project(in payload, monsterId, globalXpMultiplier: 100, activeGlobalEventId: 0);
            var live = RunLive(payload);

            double projectedKills = 3600.0 / estimate.SecondsPerKill;
            _output.WriteLine(
                $"monster {monsterId}: projected {projectedKills:F0} kills/h ({estimate.SecondsPerKillLow}-{estimate.SecondsPerKillHigh}s, mean {estimate.SecondsPerKill:F2}s), "
                + $"live {live.Kills} (died {live.Died}); food projected {estimate.FoodPerHour:F0}, live {live.FoodEaten}");

            Assert.True(estimate.CanDamage);
            Assert.Equal(!live.Died, estimate.SurvivesWithFood);
            if (!live.Died)
            {
                // Measured 2026-09-29: within 3-5% on all three rows. Ten leaves
                // room for the rolls without letting a real drift through.
                Assert.InRange(projectedKills, live.Kills * 0.90, live.Kills * 1.10);
                // Food is the larder's cost - measured within 10%.
                Assert.InRange(estimate.FoodPerHour, live.FoodEaten * 0.75 - 5, live.FoodEaten * 1.25 + 5);
            }
        }

        [Fact]
        public void AnUnderpoweredCharacter_IsToldItDies()
        {
            // Level 5 in region 3: nothing about this fight is close, so the
            // live tick's rolls cannot flip the verdict.
            var payload = Character(5, 10, 10, 10, 101, food: 0);
            var estimate = HuntingProjection.Project(in payload, 101, 100, 0);
            var live = RunLive(payload);

            Assert.True(live.Died);
            Assert.False(estimate.SurvivesWithoutFood);
            Assert.False(estimate.SurvivesWithFood);
        }

        [Fact]
        public void XpAndGold_AreTheKillRatesTimesTheLivePerKillFigures()
        {
            var payload = Character(20, 40, 40, 40, 91, food: 5000);
            var estimate = HuntingProjection.Project(in payload, 91, 100, 0);
            var monster = ContentRegistry.Monsters[90];
            double killsPerHour = 3600.0 / estimate.SecondsPerKill;

            long xpPerKill = (long)monster.BaseXpReward * SimulationEngine.LiveKillXpMultiplierPct(in payload, 100) / 100;
            Assert.Equal((long)(killsPerHour * xpPerKill), estimate.XpPerHour);
            Assert.True(estimate.GoldPerHour > 0);
            Assert.True(estimate.SecondsPerKillLow <= estimate.SecondsPerKill);
            Assert.True(estimate.SecondsPerKillHigh >= estimate.SecondsPerKill);
        }
    }
}
