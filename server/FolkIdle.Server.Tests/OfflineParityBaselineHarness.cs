using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// MEASUREMENT HARNESS, off by default (roadmap 5, phase 2b step 0). How
    /// close is today's expected-value offline model to the live tick, measured
    /// precisely enough to compare a per-swing simulator against later?
    ///
    /// OfflineCombatParityTests answers pass/fail with six live hours a row and
    /// a 5% allowance. That is a guard, not a measurement: six samples cannot
    /// tell a 1% bias from noise. This runs every parity row for many live
    /// hours (lives, for a row that dies) and prints, per figure, the offline
    /// value over the live mean with the live mean's 95% interval, so a bias is
    /// visible as an interval that does not contain 1.00.
    ///   $env:FOLKIDLE_PARITY_BASELINE='1'; dotnet test --filter OfflineParityBaselineHarness --logger "console;verbosity=detailed"
    /// FOLKIDLE_PARITY_BASELINE_HOURS sets the sample count (default 60).
    /// </summary>
    [Collection("Static loot queues")]
    public class OfflineParityBaselineHarness
    {
        private readonly ITestOutputHelper _out;

        public OfflineParityBaselineHarness(ITestOutputHelper output)
        {
            _out = output;
            ContentRegistry.Initialize();
        }

        private static bool Enabled => Environment.GetEnvironmentVariable("FOLKIDLE_PARITY_BASELINE") == "1";

        private static int Samples => int.TryParse(Environment.GetEnvironmentVariable("FOLKIDLE_PARITY_BASELINE_HOURS"), out int n) && n > 1 ? n : 60;

        /// <summary>Mean and standard error of one figure over the live samples.</summary>
        private readonly record struct Stat(double Mean, double StdErr);

        private static Stat Of(List<double> xs)
        {
            double mean = 0;
            foreach (double x in xs) mean += x;
            mean /= xs.Count;
            double var = 0;
            foreach (double x in xs) var += (x - mean) * (x - mean);
            var /= xs.Count - 1;
            return new Stat(mean, Math.Sqrt(var / xs.Count));
        }

        /// <summary>
        /// The live tick for <paramref name="samples"/> independent hours, each
        /// ending at the first death (as OfflineCombatParityTests.RunLive does),
        /// keeping every sample instead of only the average.
        /// </summary>
        private static Dictionary<string, List<double>> SampleLive(TickStatePayload start, int samples)
        {
            var guildWar = new ConcurrentQueue<GuildWarPointEvent>();
            var sessions = new ConcurrentDictionary<long, LiveSessionContext>();
            var figures = new Dictionary<string, List<double>>
            {
                ["kills"] = new(), ["xp"] = new(), ["gold"] = new(), ["food"] = new(), ["alive"] = new(), ["died"] = new(),
            };
            try
            {
                for (int run = 0; run < samples; run++)
                {
                    var payload = start;
                    int ticks = 0, kills = 0;
                    bool died = false;
                    for (; ticks < HuntingProjection.HorizonTicks; ticks++)
                    {
                        int fleeceBefore = payload.KillsSinceFleece;
                        SimulationEngine.RunCombatTick(ref payload, 100, 100, guildWar, sessions);
                        if (payload.KillsSinceFleece != fleeceBefore) kills++;
                        if (payload.ActiveActivityId == 0) { died = true; ticks++; break; }
                    }
                    figures["kills"].Add(kills);
                    figures["xp"].Add(OfflineCombatParityTests.XpBetween(start.CurrentLevel, start.CurrentXp, payload.CurrentLevel, payload.CurrentXp));
                    figures["gold"].Add(payload.CurrentGold - start.CurrentGold);
                    figures["food"].Add(OfflineCombatParityTests.FoodCount(in start) - OfflineCombatParityTests.FoodCount(in payload));
                    figures["alive"].Add(ticks / 10.0);
                    figures["died"].Add(died ? 1 : 0);
                    // Static queues (server/CLAUDE.md): drain every hour, not
                    // once at the end - 60 hours of drops is a lot of memory.
                    while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
                    while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
                }
            }
            finally
            {
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
                while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
            }
            return figures;
        }

        /// <summary>offline / live, with the live mean's 95% interval carried into the ratio.</summary>
        private static string Cell(double offline, Stat live)
        {
            if (live.Mean == 0) return offline == 0 ? "  = " : " inf";
            double lo = live.Mean + (1.96 * live.StdErr), hi = live.Mean - (1.96 * live.StdErr);
            string range = hi > 0 ? FormattableString.Invariant($" [{offline / lo:F3}-{offline / hi:F3}]") : "";
            return FormattableString.Invariant($"{offline / live.Mean:F3}{range}");
        }

        [Fact]
        public void Print_TheOfflineModelAgainstTheLiveTick()
        {
            if (!Enabled) return;
            int samples = Samples;
            var table = new StringBuilder();
            table.AppendLine($"Offline expected-value model against the live tick, {samples} live hours a row (one life each for a dying row).");
            table.AppendLine("Each cell: offline / live mean [95% interval of that ratio]. An interval that misses 1.000 is a measured bias.");
            table.AppendLine("row | died off/live | kills | xp | gold | food | seconds alive");
            foreach (object[] r in OfflineCombatParityTests.Rows())
            {
                var payload = OfflineCombatParityTests.Build((int)r[1], (bool)r[2], (int)r[3], (int)r[4], (int)r[5]);
                var live = SampleLive(payload, samples);
                var offline = OfflineCombatParityTests.RunOffline(payload);
                var died = Of(live["died"]);
                table.AppendLine(FormattableString.Invariant(
                    $"{r[0]} | {offline.DeathShare:F0}/{died.Mean:F2} | {Cell(offline.Kills, Of(live["kills"]))} | {Cell(offline.Xp, Of(live["xp"]))} | {Cell(offline.Gold, Of(live["gold"]))} | {Cell(offline.Food, Of(live["food"]))} | {Cell(offline.SecondsAlive, Of(live["alive"]))}"));
            }
            _out.WriteLine(table.ToString());
        }
    }
}
