using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Phase 2b.4 (roadmap 5): with FOLKIDLE_OFFLINE_SIM=1 the away window is
    /// fought by CombatSimulator. These hold what the switch must keep from
    /// the expected-value path - gold booked as CombatAway, the loot as a few
    /// Offline requests with their materials counted, one codex event a
    /// monster - and that the two paths pay the same over a long window.
    /// They call CalculateSimulatedCombatProjection directly rather than
    /// flipping the static flag, which other test classes read in parallel.
    /// </summary>
    [Collection("Static loot queues")]
    public class OfflineSimulatorSwitchTests
    {
        public OfflineSimulatorSwitchTests() => ContentRegistry.Initialize();

        private static void Drain()
        {
            while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
        }

        [Fact]
        public void A_simulated_window_books_gold_away_and_sends_merged_offline_loot()
        {
            Drain();
            try
            {
                var payload = OfflineCombatParityTests.Build(3, true, ContentRegistry.FirstCanonicalMonsterId + 12, 20_000, 0);
                long goldBefore = payload.CurrentGold;
                long combatBefore = payload.PendingGoldIncome.Combat;
                long awayBefore = payload.PendingGoldIncome.CombatAway;

                var projection = OfflineSimulationEngine.CalculateSimulatedCombatProjection(ref payload, 3600, new Random(7), out long secondsFought);

                Assert.True(projection.IsValid);
                Assert.Equal(3600, secondsFought);
                long earned = payload.CurrentGold - goldBefore;
                Assert.True(earned > 0);
                Assert.Equal(combatBefore, payload.PendingGoldIncome.Combat);
                Assert.Equal(earned, payload.PendingGoldIncome.CombatAway - awayBefore);

                var requests = new List<CombatLootDropRequest>();
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out var r)) requests.Add(r);
                Assert.InRange(requests.Count, 1, 2); // plain, and Golden Fleece if it procced
                Assert.All(requests, r => Assert.True(r.SkipMaterialRoll && r.Source == DropSource.Offline));
                Assert.Equal(projection.Actions, requests.Sum(r => (long)r.Kills));
                Assert.True(projection.Drops > 0, "the materials are rolled here, so the welcome-back card can count them");

                var codex = new List<KillEvent>();
                while (CodexEngine.KillEventQueue.TryDequeue(out var k)) codex.Add(k);
                Assert.Single(codex);
                Assert.Equal(projection.Actions, codex[0].Kills);

                // The fight in progress is not carried across the login.
                Assert.Equal(0, payload.CurrentMonsterId);
            }
            finally
            {
                Drain();
            }
        }

        /// <summary>
        /// Twelve hours on both paths, geared and fed: the simulator is the live
        /// fight, the model its expectation, so the gold and XP must agree to a
        /// few percent (2b.0 measured the model's own bias at up to 2.5% on
        /// surviving rows). Twelve hours is ~70k swings, far below the noise.
        /// </summary>
        [Theory]
        [InlineData(1, false, 2)]
        [InlineData(3, true, 12)]
        [InlineData(5, true, 22)]
        public void Twelve_hours_pay_the_same_on_both_paths(int region, bool geared, int monsterOffset)
        {
            Drain();
            try
            {
                int monsterId = ContentRegistry.FirstCanonicalMonsterId + monsterOffset;
                var simulated = OfflineCombatParityTests.Build(region, geared, monsterId, 20_000, 0);
                var modelled = simulated;
                long goldStart = simulated.CurrentGold;

                OfflineSimulationEngine.CalculateSimulatedCombatProjection(ref simulated, 12 * 3600, new Random(11), out _);
                var outcome = OfflineSimulationEngine.ProjectCombat(ref modelled, monsterId, 12 * 3600);

                double simGold = simulated.CurrentGold - goldStart;
                double modelGold = modelled.CurrentGold - goldStart;
                Assert.True(modelGold > 0);
                Assert.True(Math.Abs(simGold / modelGold - 1.0) <= 0.05,
                    $"r{region}: simulated gold {simGold:F0}, model {modelGold:F0}");
                Assert.Equal(outcome.Died, simulated.ActiveActivityId == 0);
            }
            finally
            {
                Drain();
            }
        }

        [Fact]
        public void Xp_gained_counts_the_levels_crossed()
        {
            long need = ProgressionEngine.GetRequiredXpForLevel(10);
            Assert.Equal(50, OfflineSimulationEngine.XpGainedBetween(10, 100, 10, 150));
            Assert.Equal(need - 100 + 30, OfflineSimulationEngine.XpGainedBetween(10, 100, 11, 30));
        }
    }
}
