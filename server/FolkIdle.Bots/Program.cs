using System.Globalization;
using FolkIdle.Server.Engine;

namespace FolkIdle.Bots
{
    /// <summary>
    /// Playtest bots. Start the dev server with a tick multiplier, then:
    ///
    ///   dotnet run --project server/FolkIdle.Bots/FolkIdle.Bots.csproj -- --bots 3 --hours 24 --speed 10
    ///
    /// Each bot is a brand-new guest that plays the "optimal" persona (BotBrain)
    /// until it has played --hours of GAME time. Results land in --out
    /// (default bot-runs/&lt;timestamp&gt;): milestones.csv and events.log.
    ///
    /// Modul: LOCAL ONLY, BY DEFAULT. A bot run against production would add
    /// accounts that look like players (the smoke-guest pollution of 2026-11-01
    /// all over again) and feed the macro detector. A non-local --server is
    /// refused unless --allow-remote is given.
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var o = Options.Parse(args);
            if (o == null) return 2;

            var uri = new Uri(o.Server);
            if (!uri.IsLoopback && !o.AllowRemote)
            {
                Console.Error.WriteLine($"Refusing to run bots against {uri.Host}: bots are a dev-box tool (pass --allow-remote to override).");
                return 2;
            }

            ContentRegistry.Initialize(FindGameData());

            string outDir = o.Out ?? Path.Combine("bot-runs", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            using var log = new MilestoneLog(outDir, o.Speed);
            var roster = o.Roster();
            Console.WriteLine($"FolkIdle bots: {string.Join(", ", roster.GroupBy(p => p.Name).Select(g => $"{g.Count()} x {g.Key}"))}, {o.Hours} game hours at speed x{o.Speed} (~{o.Hours * 60.0 / o.Speed:0} wall minutes) -> {Path.GetFullPath(outDir)}");
            Console.WriteLine(o.Lockstep
                ? "LOCKSTEP: the server must run with FOLKIDLE_DEV_TOOLS=1 and FOLKIDLE_SIM_LOCKSTEP=1; --speed is ignored."
                : "The server must run with FOLKIDLE_DEV_TOOLS=1 and FOLKIDLE_TICK_SPEED matching --speed.");
            if (Persona.SleepDisabled) Console.WriteLine("--no-sleep: optimal/patient/patientbare/eagerkit stay online 24/7.");

            if (o.Lockstep) return await RunLockstepAsync(o, log, roster);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            DateTime stopAt = DateTime.UtcNow.AddHours(o.Hours / o.Speed);
            var metricsAtStart = await TickMetrics.ReadAsync(o.Server);
            DateTime wallStart = DateTime.UtcNow;
            string runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

            int stopped = 0;
            var tasks = roster.Select(async (persona, index) =>
            {
                int i = index + 1;
                string name = $"{persona.Name}{i}";
                string deviceId = $"{o.Prefix}-{runId}-{i}";
                var brain = new BotBrain(async t =>
                {
                    var client = new BotClient(o.Server, deviceId);
                    await client.ConnectAsync(t);
                    return client;
                }, log, seed: i * 7919 + o.Seed * 104729, name, persona);
                try
                {
                    await brain.RunAsync(stopAt, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[{name}] stopped: {ex.GetType().Name}: {ex.Message}");
                    log.Event(name, $"stopped: {ex.Message}");
                    Interlocked.Increment(ref stopped);
                }
                if (brain.LastState is { } last) log.Summary(name, last, brain.StateFrames);
            }).ToArray();

            await Task.WhenAll(tasks);
            var metricsAtEnd = await TickMetrics.ReadAsync(o.Server);
            log.WriteSummary(TickMetrics.Describe(metricsAtStart, metricsAtEnd, DateTime.UtcNow - wallStart, o.Speed));
            log.WriteRunInfo(o.RunInfo("paced", WallBotTime.GameStartUtc ?? wallStart, roster.Count, log.StartedBots, stopped, log.GameHours));
            Console.WriteLine($"Done. {Path.GetFullPath(Path.Combine(outDir, "milestones.csv"))}");
            return 0;
        }

        /// <summary>
        /// The lockstep run (phase 2): one control socket for every bot, each bot
        /// on its own LockstepBotTime, the run measured in GAME hours.
        /// </summary>
        private static async Task<int> RunLockstepAsync(Options o, MilestoneLog log, List<Persona> roster)
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            await using var driver = new SimDriver();
            await driver.ConnectAsync(o.Server, cts.Token);
            log.Clock = () => driver.GameHours;
            var metricsAtStart = await TickMetrics.ReadAsync(o.Server);
            DateTime wallStart = DateTime.UtcNow;
            string runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            Console.WriteLine($"LOCKSTEP: connected at tick {driver.Tick}, game time {driver.GameUtc:yyyy-MM-dd HH:mm} UTC, quantum {driver.Quantum} ticks.");
            DateTime gameStart = driver.GameUtc;
            int stopped = 0;

            var tasks = roster.Select(async (persona, index) =>
            {
                int i = index + 1;
                string name = $"{persona.Name}{i}";
                string deviceId = $"{o.Prefix}-{runId}-{i}";
                var time = driver.Join(name, reactionMinTicks: 4, reactionMaxTicks: 12);
                var brain = new BotBrain(async t =>
                {
                    var client = new BotClient(o.Server, deviceId);
                    await client.ConnectAsync(t, waitForState: false);
                    return client;
                }, log, seed: i * 7919 + o.Seed * 104729, name, persona, time);
                try
                {
                    await brain.RunLockstepAsync(o.Hours, time, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[{name}] stopped: {ex.GetType().Name}: {ex.Message}");
                    log.Event(name, $"stopped: {ex.Message}");
                    Interlocked.Increment(ref stopped);
                    time.Leave();
                }
                if (brain.LastState is { } last) log.Summary(name, last, brain.StateFrames);
            }).ToArray();

            await Task.WhenAll(tasks);
            var metricsAtEnd = await TickMetrics.ReadAsync(o.Server);
            var wall = DateTime.UtcNow - wallStart;
            double gameHours = driver.GameHours;
            string ticksRun = metricsAtEnd is { } e && metricsAtStart is { } s0 ? (e.Ticks - s0.Ticks).ToString("N0", CultureInfo.InvariantCulture) : "?";
            string offline = TickMetrics.Describe(metricsAtStart, metricsAtEnd, wall, 1).Split("\n\nTick metrics")[0];
            log.WriteSummary($"Lockstep: {gameHours:0.0} game hours in {wall.TotalMinutes:0.0} wall minutes = x{gameHours * 60 / Math.Max(wall.TotalMinutes, 1e-6):0} effective; {driver.Wakes:N0} wake frames; {ticksRun} ticks actually run (the rest were jumped while nobody was online).\n\n{offline}");
            log.WriteRunInfo(o.RunInfo("lockstep", gameStart, roster.Count, log.StartedBots, stopped, gameHours));
            Console.WriteLine($"Done. {gameHours:0.0} game hours in {wall.TotalMinutes:0.0} wall minutes.");
            return 0;
        }

        /// <summary>
        /// Modul: GAME TIME IS CHECKED, NOT ASSUMED (report 4.3, 2026-11-01).
        /// MilestoneLog counts game hours as wall time x --speed. If the server
        /// cannot keep up it drops ticks and the real game time is shorter, so
        /// every milestone reads early. The tick counter and the dropped-tick
        /// counter from /metrics, read at both ends of the run, give the speed
        /// the server actually ran at; summary.md reports it.
        /// </summary>
        private static class TickMetrics
        {
            public static async Task<(long Ticks, long Dropped, long OfflineSim)?> ReadAsync(string server)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    string text = await http.GetStringAsync(server.TrimEnd('/') + "/metrics");
                    long Read(string name, long missing = 0) => text.Split('\n').Where(l => l.StartsWith(name + " ", StringComparison.Ordinal))
                        .Select(l => long.TryParse(l.Split(' ')[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : missing).DefaultIfEmpty(missing).First();
                    return (Read("folkidle_tick_duration_milliseconds_count"), Read("folkidle_ticks_dropped_total"), Read("folkidle_offline_sim", -1));
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public static string Describe((long Ticks, long Dropped, long OfflineSim)? start, (long Ticks, long Dropped, long OfflineSim)? end, TimeSpan wall, int speed)
            {
                if (start is not { } a || end is not { } b || wall.TotalSeconds < 1) return "Tick metrics: /metrics was not readable.";
                double effective = (b.Ticks - a.Ticks) / (wall.TotalSeconds * 10.0);
                // Modul: THE OFFLINE MODEL IS PART OF A RUN'S CONFIGURATION
                // (2026-11-03). The first rebirth experiment ran on the old model
                // and nothing in its output said so; the server now reports it.
                string offline = b.OfflineSim switch
                {
                    1 => "per-swing simulator (FOLKIDLE_OFFLINE_SIM=1)",
                    0 => "OLD expected-value model (FOLKIDLE_OFFLINE_SIM not 1)",
                    _ => "unknown (the server predates the folkidle_offline_sim metric)",
                };
                return $"Offline combat: {offline}.\n\nTick metrics: {b.Ticks - a.Ticks:N0} ticks in {wall.TotalMinutes:0.0} wall minutes = x{effective:0.0} effective (asked x{speed}); {b.Dropped - a.Dropped:N0} ticks dropped. Game hours above assume x{speed}; at x{effective:0.0} they are {effective / speed * 100:0.0}% of what they say.";
            }
        }

        private static string FindGameData()
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "GameData");
            if (Directory.Exists(beside)) return beside;
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "server", "GameData");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("server/GameData not found");
        }

        private sealed class Options
        {
            public string Server = "http://localhost:8080";
            public int Bots = 1;
            public double Hours = 2;
            public int Speed = 10;
            public string? Out;
            public string Prefix = "bot";
            public bool AllowRemote;
            /// <summary>--mix optimal:2,casual:1,idle:1; without it, --bots N optimal bots.</summary>
            public string? Mix;
            /// <summary>--seed N: shifts every bot's decision dice, so a repeat of the same mix is an independent sample.</summary>
            public int Seed;
            /// <summary>--lockstep: drive a FOLKIDLE_SIM_LOCKSTEP server through /api/v1/dev/sim (docs/design/LOCKSTEP_SIM.md).</summary>
            public bool Lockstep;

            /// <summary>What run.json records: everything a validation compares before it compares a milestone.</summary>
            public object RunInfo(string mode, DateTime gameStartUtc, int botsExpected, int botsStarted, int botsStopped, double gameHoursRun) => new
            {
                Mode = mode,
                Speed = mode == "lockstep" ? 0 : Speed,
                CronScale = Environment.GetEnvironmentVariable("FOLKIDLE_SIM_CRON_SCALE") ?? "1",
                Mix,
                Seed,
                Hours,
                NoSleep = Persona.SleepDisabled,
                GameStartUtc = gameStartUtc.ToString("o", CultureInfo.InvariantCulture),
                BotsExpected = botsExpected,
                BotsStarted = botsStarted,
                BotsStopped = botsStopped,
                GameHoursRun = gameHoursRun,
            };

            public List<Persona> Roster()
            {
                if (string.IsNullOrWhiteSpace(Mix)) return Enumerable.Repeat(Persona.Optimal, Bots).ToList();
                var roster = new List<Persona>();
                foreach (string part in Mix.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kv = part.Split(':');
                    var persona = Persona.Parse(kv[0]) ?? throw new ArgumentException($"unknown persona '{kv[0]}' (optimal, patient, patientbare, eagerkit, casual, idle)");
                    int count = kv.Length > 1 ? int.Parse(kv[1], CultureInfo.InvariantCulture) : 1;
                    roster.AddRange(Enumerable.Repeat(persona, Math.Clamp(count, 1, 50)));
                }
                return roster;
            }

            public static Options? Parse(string[] args)
            {
                var o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                    switch (args[i])
                    {
                        case "--server": o.Server = next().TrimEnd('/'); break;
                        case "--bots": o.Bots = Math.Clamp(int.Parse(next(), CultureInfo.InvariantCulture), 1, 50); break;
                        case "--hours": o.Hours = double.Parse(next(), CultureInfo.InvariantCulture); break;
                        case "--speed": o.Speed = Math.Clamp(int.Parse(next(), CultureInfo.InvariantCulture), 1, FolkIdle.Server.Domain.Shared.TickPacer.MaxTickSpeed); break;
                        case "--out": o.Out = next(); break;
                        case "--prefix": o.Prefix = next(); break;
                        case "--allow-remote": o.AllowRemote = true; break;
                        case "--mix": o.Mix = next(); break;
                        case "--seed": o.Seed = int.Parse(next(), CultureInfo.InvariantCulture); break;
                        case "--lockstep": o.Lockstep = true; break;
                        case "--no-sleep": Persona.SleepDisabled = true; break;
                        case "--start-utc": WallBotTime.GameStartUtc = DateTime.Parse(next(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal); break;
                        default:
                            Console.Error.WriteLine($"unknown argument {args[i]}");
                            Console.Error.WriteLine("usage: (--bots N | --mix optimal:1,casual:1,idle:1) --hours GAME_HOURS --speed N [--server URL] [--out DIR] [--prefix ID] [--seed N] [--lockstep] [--no-sleep] [--start-utc ISO_UTC]");
                            return null;
                    }
                }
                return o;
            }
        }
    }
}
