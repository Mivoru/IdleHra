using System.Collections.Concurrent;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// LowRegionXp (owner decision 2026-11-22): a monster two or more regions
    /// below the highest open region pays half its base XP - on the live kill
    /// and in the projection the advisor and the offline window price with.
    /// </summary>
    [Collection("Static loot queues")]
    public class LowRegionXpTests
    {
        public LowRegionXpTests() => ContentRegistry.Initialize();

        [Theory]
        [InlineData(1, 95, false)]  // a new player: nothing is below it
        [InlineData(2, 95, false)]  // one region below is still full pay
        [InlineData(3, 95, true)]   // region 3 open: region 1 (the boss too) is cut
        [InlineData(3, 100, false)] // ... region 2 is not
        [InlineData(3, 101, false)]
        [InlineData(5, 105, true)]  // Malakor's region open: region 3 is cut
        [InlineData(5, 115, false)] // Malakor himself is not
        public void Applies_TwoOrMoreRegionsBelowTheHighestOpen(int highestRegion, int monsterId, bool cut)
        {
            var payload = new TickStatePayload { HighestUnlockedRegion = highestRegion };
            Assert.Equal(cut, LowRegionXp.Applies(in payload, monsterId));

            var monster = ContentRegistry.Monsters[monsterId - 1];
            int expected = cut ? monster.BaseXpReward * LowRegionXp.PaidPct / 100 : monster.BaseXpReward;
            Assert.Equal(expected, LowRegionXp.BaseXp(in payload, in monster));
        }

        [Fact]
        public void TheCut_PutsTheRegionOneBossUnderRegionThree_PerHealthPoint()
        {
            // The trap the rule exists for: with region 3 open, Alpha Wolf must
            // pay less XP per health point than any region-3 monster.
            var payload = new TickStatePayload { HighestUnlockedRegion = 3 };
            var wolf = ContentRegistry.Monsters[94];
            double wolfPerHp = (double)LowRegionXp.BaseXp(in payload, in wolf) / wolf.MaxHp;
            for (int id = 101; id <= 105; id++)
            {
                var m = ContentRegistry.Monsters[id - 1];
                Assert.True(wolfPerHp < (double)LowRegionXp.BaseXp(in payload, in m) / m.MaxHp, $"monster {id}");
            }
        }

        [Fact]
        public void Projection_PaysHalfTheXp_ForTheSameFight()
        {
            var open1 = Character(highestRegion: 1);
            var open3 = Character(highestRegion: 3);
            var full = HuntingProjection.Project(in open1, 91, 100, 0);
            var cut = HuntingProjection.Project(in open3, 91, 100, 0);

            Assert.Equal(full.SecondsPerKill, cut.SecondsPerKill);
            Assert.InRange(cut.XpPerHour, full.XpPerHour * 0.49, full.XpPerHour * 0.51);
        }

        [Fact]
        public void LiveKill_PaysWhatTheProjectionPerKillSays()
        {
            foreach (int highest in new[] { 1, 3 })
            {
                var payload = Character(highestRegion: highest);
                var monster = ContentRegistry.Monsters[90];
                long expected = HuntingProjection.XpPerKill(in payload, in monster, 100, 0);
                Assert.Equal(expected, XpOfFirstLiveKill(payload));
            }
        }

        private static TickStatePayload Character(int highestRegion)
        {
            // Level 60 on the Field Mouse: one kill is a sliver of the level, so
            // no level-up can reset the XP bar between the two readings.
            var payload = new TickStatePayload
            {
                PlayerId = 979_001L,
                CurrentLevel = 60,
                STR = 200,
                DEX = 120,
                CON = 150,
                LCK = 25,
                ActiveActivityId = 91,
                AutoEatThreshold = 50,
                HighestUnlockedRegion = highestRegion,
                CachedCodexDamageMultiplier = 1f,
            };
            var stats = SimulationEngine.LiveCombatStats(in payload);
            payload.PlayerHp = (int)SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);
            return payload;
        }

        private static long XpOfFirstLiveKill(TickStatePayload payload)
        {
            var guildWar = new ConcurrentQueue<GuildWarPointEvent>();
            var sessions = new ConcurrentDictionary<long, LiveSessionContext>();
            try
            {
                for (int tick = 0; tick < 6000; tick++)
                {
                    // A one-swing kill respawns the monster at the same full
                    // health, so the kill shows as the XP bar moving, not the
                    // monster's bar.
                    long xpBefore = payload.CurrentXp;
                    int levelBefore = payload.CurrentLevel;
                    SimulationEngine.RunCombatTick(ref payload, 100, 100, guildWar, sessions);
                    if (payload.CurrentXp != xpBefore)
                    {
                        Assert.Equal(levelBefore, payload.CurrentLevel);
                        return payload.CurrentXp - xpBefore;
                    }
                }
                Assert.Fail("no kill in ten minutes");
                return 0;
            }
            finally
            {
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            }
        }
    }
}
