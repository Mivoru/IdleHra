using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 85: an automation rule does offline what it does live. Each row
    /// runs the REAL live tick (ProcessSubTick, so a character the rule sends
    /// fishing really fishes) for an hour, several times, beside the offline
    /// window (OfflineSimulationEngine.ProjectCombatLegs) from the same
    /// payload, and holds where the character ends up, when it got there and
    /// what it earned on the way to the live result.
    /// </summary>
    public class AutomationRuleParityTests
    {
        private const int LiveHours = 6;
        private const long Hour = 3600;
        private const int FishingSpot = 3001;

        private readonly ITestOutputHelper _output;

        public AutomationRuleParityTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static TickStatePayload WithRules(TickStatePayload payload, params AutomationRules.Rule[] rules)
        {
            var slots = new AutomationRules.Rule[AutomationRules.SlotCount];
            for (int i = 0; i < rules.Length; i++) slots[i] = rules[i];
            // The slots the rules use are open, so the row tests the rule
            // rather than the gate - and no higher, so the fight stays hard.
            int needed = rules.Length == 0 ? 0 : AutomationRules.UnlockLevels[rules.Length - 1];
            if (payload.CurrentLevel < needed) payload.CurrentLevel = needed;
            payload.AutomationRules = AutomationRules.Pack(slots);
            payload.HighestLocationReached = 5;
            var stats = SimulationEngine.LiveCombatStats(in payload);
            payload.PlayerHp = (int)SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);
            return payload;
        }

        private readonly record struct Run(
            long FinalActivity, byte FinalReason, double Kills, double Deaths, double SecondsToSwitch, double FishingXp, double Food);

        private static Run RunLiveOnce(TickStatePayload start)
        {
            var guildWar = new ConcurrentQueue<GuildWarPointEvent>();
            var sessions = new ConcurrentDictionary<long, LiveSessionContext>();
            var payload = start;
            double kills = 0, deaths = 0, switchAt = -1;
            for (int tick = 0; tick < HuntingProjection.HorizonTicks; tick++)
            {
                int fleeceBefore = payload.KillsSinceFleece;
                long deathsBefore = payload.LifetimeDeaths;
                long activityBefore = payload.ActiveActivityId;
                SimulationEngine.ProcessSubTick(ref payload, 100, 100, guildWar, sessions);
                if (payload.KillsSinceFleece != fleeceBefore) kills++;
                deaths += payload.LifetimeDeaths - deathsBefore;
                if (payload.ActiveActivityId != activityBefore && ActivityIdBands.IsGatheringActivity(payload.ActiveActivityId))
                {
                    switchAt = (tick + 1) / 10.0;
                }
                if (payload.ActiveActivityId <= 0) break;
            }
            return new Run(payload.ActiveActivityId, payload.ActivityHaltReason, kills, deaths, switchAt,
                TotalFishingXp(payload) - TotalFishingXp(start),
                Food(start) - Food(payload));
        }

        private static Run RunOffline(TickStatePayload payload)
        {
            var start = payload;
            var legs = OfflineSimulationEngine.ProjectCombatLegs(ref payload, Hour, new Random(85));
            double kills = legs.Where(l => !l.IsGathering).Sum(l => (double)l.Projection.Actions);
            double switchAt = -1;
            double elapsed = 0;
            foreach (var leg in legs)
            {
                if (leg.IsGathering) { switchAt = elapsed; break; }
                elapsed += leg.Seconds;
            }
            return new Run(payload.ActiveActivityId, payload.ActivityHaltReason, kills,
                payload.LifetimeDeaths - start.LifetimeDeaths, switchAt,
                TotalFishingXp(payload) - TotalFishingXp(start),
                Food(start) - Food(payload));
        }

        private static int Food(in TickStatePayload p) => p.Food1_Count + p.Food2_Count + p.Food3_Count;

        /// <summary>Every fishing XP point a payload holds, levels included (50 * (level + 1)^2 a level).</summary>
        private static double TotalFishingXp(in TickStatePayload p)
        {
            double total = p.FishingMasteryXp;
            for (int level = 0; level < p.FishingMasteryLevel; level++) total += 50.0 * (level + 1) * (level + 1);
            return total;
        }

        private static List<Run> RunLive(TickStatePayload start)
        {
            var runs = new List<Run>();
            try
            {
                for (int i = 0; i < LiveHours; i++) runs.Add(RunLiveOnce(start));
            }
            finally
            {
                Drain();
            }
            return runs;
        }

        private static void Drain()
        {
            // Static queues (server/CLAUDE.md): leave nothing for another test's worker.
            while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            while (CombatLootEngine.GatheringGrantQueue.TryDequeue(out _)) { }
            while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
        }

        private void Report(string row, List<Run> live, Run offline)
        {
            foreach (var run in live)
            {
                _output.WriteLine($"{row} live    : ends {run.FinalActivity} (reason {run.FinalReason}), kills {run.Kills}, deaths {run.Deaths}, switch {run.SecondsToSwitch:F1}s, fishing xp {run.FishingXp}, food {run.Food}");
            }
            _output.WriteLine($"{row} offline : ends {offline.FinalActivity} (reason {offline.FinalReason}), kills {offline.Kills}, deaths {offline.Deaths}, switch {offline.SecondsToSwitch:F1}s, fishing xp {offline.FishingXp}, food {offline.Food}");
        }

        private static void AssertClose(string what, double offline, double live, double relative, double absolute)
        {
            double slack = Math.Max(Math.Abs(live) * relative, absolute);
            Assert.True(Math.Abs(offline - live) <= slack, $"{what}: offline {offline:F1}, live {live:F1}");
        }

        /// <summary>
        /// A bare region-1 character on region 1's strongest regular, eating at
        /// 90% of its bar from a 15-bite larder - it eats through the larder
        /// well inside the hour.
        /// </summary>
        private static TickStatePayload Hungry()
        {
            var payload = OfflineCombatParityTests.Character(1, false, ContentRegistry.FirstCanonicalMonsterId + 3, 15);
            payload.AutoEatThreshold = 90;
            return payload;
        }

        [Fact]
        public void TheLarderRunsDry_AndBothPathsGoFishingAtTheSameMoment()
        {
            // A bare region-1 character on a regular with a small larder: it
            // eats through the larder inside the hour and has to stop.
            var payload = Hungry();
            payload = WithRules(payload, new AutomationRules.Rule { Type = AutomationRules.FishWhenLarderDry, Param = FishingSpot - (int)ActivityIdBands.FishingBand });

            var live = RunLive(payload);
            Run offline;
            try { offline = RunOffline(payload); } finally { Drain(); }
            Report("larder dry", live, offline);

            // Every live hour ends fishing, and so does the offline window.
            Assert.All(live, run => Assert.Equal(FishingSpot, run.FinalActivity));
            Assert.All(live, run => Assert.Equal(Network.ActivityHaltReason.AutomationFishing, run.FinalReason));
            Assert.Equal(FishingSpot, offline.FinalActivity);
            Assert.Equal(Network.ActivityHaltReason.AutomationFishing, offline.FinalReason);
            Assert.All(live, run => Assert.Equal(0, run.Deaths));
            Assert.Equal(0, offline.Deaths);

            // The whole larder is eaten on both paths before the switch.
            Assert.All(live, run => Assert.Equal(15, run.Food));
            Assert.Equal(15, offline.Food);

            // When: the switch lands within 10% (a short life, so also within
            // ten seconds - a few swings either way).
            double liveSwitch = live.Average(r => r.SecondsToSwitch);
            AssertClose("seconds to the switch", offline.SecondsToSwitch, liveSwitch, 0.10, 10.0);

            // What it earned: the kills before it, and the fishing after it.
            AssertClose("kills before the switch", offline.Kills, live.Average(r => r.Kills), 0.10, 2.0);
            AssertClose("fishing mastery xp", offline.FishingXp, live.Average(r => r.FishingXp), 0.05, 50.0);
        }

        [Fact]
        public void WithoutTheRule_TheLarderRunningDryChangesNothing()
        {
            var payload = WithRules(Hungry()); // no rules
            var live = RunLive(payload);
            Run offline;
            try { offline = RunOffline(payload); } finally { Drain(); }
            Report("no rule", live, offline);

            Assert.All(live, run => Assert.NotEqual(FishingSpot, run.FinalActivity));
            Assert.NotEqual(FishingSpot, offline.FinalActivity);
            // It fights on unhealed, as before the rules existed - on both paths.
            long liveEnd = live.GroupBy(r => r.FinalActivity).OrderByDescending(g => g.Count()).First().Key;
            Assert.Equal(liveEnd, offline.FinalActivity);
        }

        [Fact]
        public void ADeath_StepsBothPathsDownTheSameLadder()
        {
            // A bare region-1 character with no food against the region's
            // first-clear boss: the wall holds and it dies. The rule sends it
            // down the ladder until it can stand.
            // Modul: 2026-10-07, the larder is 300, was 0. With monsters hitting 1.5x as hard
            // (owner balance pass) a bare level-20 character with NO food dies on every rung
            // of region 1 - the ladder bottoms out and both paths end on activity 0 (halted,
            // OutOfFood), which says nothing about the step-down rule. A stocked larder
            // lets it stand on the Horned Rabbit after three deaths, on both paths.
            var payload = OfflineCombatParityTests.Character(1, false, RaceUnlockRegistry.GetRegionBossMonsterId(1), 300);
            payload.DefeatedRegionBossMask = 0;
            payload = WithRules(payload, new AutomationRules.Rule { Type = AutomationRules.StepDownOnDeath });

            var live = RunLive(payload);
            Run offline;
            try { offline = RunOffline(payload); } finally { Drain(); }
            Report("step down", live, offline);

            Assert.True(offline.Deaths >= 1, "the row must die at least once or it tests nothing");

            // Where the character ends: the rung the live hours settle on, below
            // the boss it died to - and the same note on the card. (With an
            // empty larder the step-down note is replaced by OutOfFood at the
            // next bite it cannot take, on both paths.)
            long liveRung = live.GroupBy(r => r.FinalActivity).OrderByDescending(g => g.Count()).First().Key;
            Assert.True(liveRung > 0 && liveRung < RaceUnlockRegistry.GetRegionBossMonsterId(1), $"the live hours ended on {liveRung}");
            Assert.Equal(liveRung, offline.FinalActivity);
            byte liveReason = live.GroupBy(r => r.FinalReason).OrderByDescending(g => g.Count()).First().Key;
            Assert.Equal(liveReason, offline.FinalReason);
            AssertClose("deaths", offline.Deaths, live.Average(r => r.Deaths), 0.0, 0.5);
            AssertClose("kills", offline.Kills, live.Average(r => r.Kills), 0.05, 2.0);
        }
    }
}
