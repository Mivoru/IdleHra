using System;
using System.Collections.Concurrent;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Phase 2b.1 (roadmap 5): every roll in RunCombatTick goes through one
    /// generator the caller can seed, so a live hour can be replayed swing for
    /// swing. That is what lets the per-swing simulator be checked against the
    /// tick exactly instead of only in distribution, and what lets an offline
    /// payout be reproduced in a test.
    ///
    /// The second test is the control: two different seeds must give two
    /// different hours. Without it, "identical twice" would also pass on a tick
    /// that ignored the generator and rolled nothing.
    /// </summary>
    [Collection("Static loot queues")]
    public class SeededLiveHourTests
    {
        public SeededLiveHourTests() => ContentRegistry.Initialize();

        private readonly record struct HourFingerprint(
            int Kills, long Gold, int Level, long Xp, long PlayerHp, long MonsterHp, int Food, int Drops, int CodexEvents);

        private static HourFingerprint LiveHour(TickStatePayload payload, int seed)
        {
            var rng = new Random(seed);
            var guildWar = new ConcurrentQueue<GuildWarPointEvent>();
            var sessions = new ConcurrentDictionary<long, LiveSessionContext>();
            while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
            int kills = 0;
            try
            {
                for (int tick = 0; tick < HuntingProjection.HorizonTicks; tick++)
                {
                    int fleeceBefore = payload.KillsSinceFleece;
                    SimulationEngine.RunCombatTick(ref payload, 100, 100, guildWar, sessions, rng);
                    if (payload.KillsSinceFleece != fleeceBefore) kills++;
                    if (payload.ActiveActivityId == 0) break;
                }
                return new HourFingerprint(
                    kills, payload.CurrentGold, payload.CurrentLevel, payload.CurrentXp, payload.PlayerHp,
                    payload.CurrentMonsterHp, OfflineCombatParityTests.FoodCount(in payload),
                    CombatLootEngine.DropRequestQueue.Count, CodexEngine.KillEventQueue.Count);
            }
            finally
            {
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
                while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
            }
        }

        // A fed region-3 character in its region's gear with the skill tree's
        // killer build and a burning set: crits, Double Strike, Echo, misses,
        // the monster's crits and block all roll inside the hour.
        private static TickStatePayload Character() => OfflineCombatParityTests.Build(3, true, ContentRegistry.FirstCanonicalMonsterId + 7, 20_000, 3);

        [Fact]
        public void TheSameSeed_ReplaysTheSameHour()
        {
            var first = LiveHour(Character(), 1234);
            var second = LiveHour(Character(), 1234);
            Assert.True(first.Kills > 100, $"the row should fight a real hour, killed {first.Kills}");
            Assert.Equal(first, second);
        }

        [Fact]
        public void DifferentSeeds_GiveDifferentHours()
        {
            var a = LiveHour(Character(), 1);
            var b = LiveHour(Character(), 2);
            Assert.NotEqual(a, b);
        }
    }
}
