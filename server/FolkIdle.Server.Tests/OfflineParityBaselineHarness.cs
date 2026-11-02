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

        /// <summary>
        /// Phase 2b.2: the per-swing simulator over the same rows, many seeded
        /// hours against many live ones. Both sides roll dice, so each cell
        /// carries both means' error. The bar the roadmap set: every interval
        /// contains 1.000 (the expected-value model above misses it on a third of
        /// the rows).
        /// </summary>
        [Fact]
        public void Print_TheSimulatorAgainstTheLiveTick()
        {
            if (!Enabled) return;
            int samples = Samples;
            var table = new StringBuilder();
            table.AppendLine($"Per-swing simulator against the live tick, {samples} seeded simulator hours and {samples} live hours a row.");
            table.AppendLine("Each cell: simulator mean / live mean [95% interval from both errors].");
            table.AppendLine("row | kills | xp | gold | food | seconds alive");
            int misses = 0, cells = 0;
            foreach (object[] r in OfflineCombatParityTests.Rows())
            {
                var payload = OfflineCombatParityTests.Build((int)r[1], (bool)r[2], (int)r[3], (int)r[4], (int)r[5]);
                var live = SampleLive(payload, samples);
                var sim = SampleSimulator(payload, samples);
                var line = new StringBuilder($"{r[0]}");
                foreach (string key in new[] { "kills", "xp", "gold", "food", "alive" })
                {
                    var a = Of(sim[key]);
                    var l = Of(live[key]);
                    if (l.Mean == 0 && a.Mean == 0) { line.Append(" |  = "); continue; }
                    double ratio = a.Mean / l.Mean;
                    double rel = Math.Sqrt(Math.Pow(a.StdErr / Math.Max(1e-9, a.Mean), 2) + Math.Pow(l.StdErr / Math.Max(1e-9, l.Mean), 2));
                    double lo = ratio * (1 - (1.96 * rel)), hi = ratio * (1 + (1.96 * rel));
                    bool contains = lo <= 1.0 && hi >= 1.0;
                    cells++;
                    if (!contains) misses++;
                    line.Append(FormattableString.Invariant($" | {ratio:F3} [{lo:F3}-{hi:F3}]{(contains ? "" : " MISS")}"));
                }
                table.AppendLine(line.ToString());
            }
            table.AppendLine($"{misses} of {cells} intervals miss 1.000 (at 95%, about {cells / 20} would by chance alone).");
            _out.WriteLine(table.ToString());
        }

        private static Dictionary<string, List<double>> SampleSimulator(TickStatePayload start, int samples)
        {
            var figures = new Dictionary<string, List<double>>
            {
                ["kills"] = new(), ["xp"] = new(), ["gold"] = new(), ["food"] = new(), ["alive"] = new(),
            };
            for (int seed = 0; seed < samples; seed++)
            {
                var payload = start;
                var outcome = CombatSimulator.Run(ref payload, HuntingProjection.HorizonTicks, new Random(1_000_003 * (seed + 1)));
                figures["kills"].Add(outcome.Tally.Kills);
                figures["xp"].Add(OfflineCombatParityTests.XpBetween(start.CurrentLevel, start.CurrentXp, payload.CurrentLevel, payload.CurrentXp));
                figures["gold"].Add(payload.CurrentGold - start.CurrentGold);
                figures["food"].Add(OfflineCombatParityTests.FoodCount(in start) - OfflineCombatParityTests.FoodCount(in payload));
                figures["alive"].Add(outcome.SecondsFought);
            }
            return figures;
        }

        /// <summary>
        /// Phase 2b.3's number: what a 12-hour window costs, ticked and simulated,
        /// against the expected-value model. The roadmap's budget is 50 ms.
        /// </summary>
        [Fact]
        public void Print_WhatATwelveHourWindowCosts()
        {
            if (!Enabled) return;
            var table = new StringBuilder("row | ticked every tick ms | simulated ms (ticks run) | expected-value model ms" + Environment.NewLine);
            foreach (var (name, region, geared, monster) in new[]
            {
                ("r5 geared fed", 5, true, ContentRegistry.FirstCanonicalMonsterId + 22),
                ("r3 geared fed", 3, true, ContentRegistry.FirstCanonicalMonsterId + 12),
                ("r1 bare fed", 1, false, ContentRegistry.FirstCanonicalMonsterId + 2),
            })
            {
                for (int rep = 0; rep < 3; rep++)
                {
                    var a = OfflineCombatParityTests.Build(region, geared, monster, 20_000, 0);
                    var b = a; var c = a;
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    CombatSimulator.Run(ref a, 12L * 36_000, new Random(rep), tickEveryTick: true);
                    double tickedMs = w.Elapsed.TotalMilliseconds;
                    w.Restart();
                    var o = CombatSimulator.Run(ref b, 12L * 36_000, new Random(rep));
                    double simMs = w.Elapsed.TotalMilliseconds;
                    w.Restart();
                    OfflineSimulationEngine.ProjectCombat(ref c, monster, 12 * 3600);
                    double evMs = w.Elapsed.TotalMilliseconds;
                    while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
                    table.AppendLine(FormattableString.Invariant($"{name} | {tickedMs:F0} | {simMs:F0} ({o.TicksTicked} of {o.TicksRun}) | {evMs:F1}"));
                }
            }
            _out.WriteLine(table.ToString());
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
