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
            Console.WriteLine("The server must run with FOLKIDLE_DEV_TOOLS=1 and FOLKIDLE_TICK_SPEED matching --speed.");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            DateTime stopAt = DateTime.UtcNow.AddHours(o.Hours / o.Speed);
            string runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

            var tasks = roster.Select(async (persona, index) =>
            {
                int i = index + 1;
                string name = $"{persona.Name}{i}";
                await using var client = new BotClient(o.Server, $"{o.Prefix}-{runId}-{i}");
                try
                {
                    await client.ConnectAsync(cts.Token);
                    var brain = new BotBrain(client, log, seed: i * 7919, name, persona);
                    await brain.RunAsync(stopAt, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[{name}] stopped: {ex.GetType().Name}: {ex.Message}");
                    log.Event(name, $"stopped: {ex.Message}");
                }
                if (client.TryGetState(out var last)) log.Summary(name, last, client.StateFrames);
            }).ToArray();

            await Task.WhenAll(tasks);
            log.WriteSummary();
            Console.WriteLine($"Done. {Path.GetFullPath(Path.Combine(outDir, "milestones.csv"))}");
            return 0;
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

            public List<Persona> Roster()
            {
                if (string.IsNullOrWhiteSpace(Mix)) return Enumerable.Repeat(Persona.Optimal, Bots).ToList();
                var roster = new List<Persona>();
                foreach (string part in Mix.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kv = part.Split(':');
                    var persona = Persona.Parse(kv[0]) ?? throw new ArgumentException($"unknown persona '{kv[0]}' (optimal, casual, idle)");
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
                        case "--speed": o.Speed = Math.Clamp(int.Parse(next(), CultureInfo.InvariantCulture), 1, 20); break;
                        case "--out": o.Out = next(); break;
                        case "--prefix": o.Prefix = next(); break;
                        case "--allow-remote": o.AllowRemote = true; break;
                        case "--mix": o.Mix = next(); break;
                        default:
                            Console.Error.WriteLine($"unknown argument {args[i]}");
                            Console.Error.WriteLine("usage: (--bots N | --mix optimal:1,casual:1,idle:1) --hours GAME_HOURS --speed N [--server URL] [--out DIR] [--prefix ID]");
                            return null;
                    }
                }
                return o;
            }
        }
    }
}
