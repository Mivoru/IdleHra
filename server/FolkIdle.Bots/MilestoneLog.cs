using System.Globalization;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Bots
{
    /// <summary>
    /// Writes when each bot reached each milestone, in GAME hours.
    ///
    /// Game time is wall time multiplied by the tick speed the server was
    /// started with (FOLKIDLE_TICK_SPEED, passed here as --speed): each tick is
    /// 100 ms of game time however fast it runs. If the server cannot keep up
    /// (it drops ticks it owes beyond TickPacer.MaxCatchUpTicks), game time is
    /// overstated - watch the server's TicksDropped metric on long runs.
    /// The snapshot stream stays at one frame a wall second at any speed (see
    /// SimulationEngine._ticksPerBroadcast), so the summary's frame rate only
    /// says whether the socket stayed healthy.
    ///
    /// Two files: milestones.csv (one row per milestone per bot) and
    /// events.log (every decision, for reading a run after the fact).
    /// </summary>
    public sealed class MilestoneLog : IDisposable
    {
        private static readonly int[] LevelMarks = { 2, 5, 10, 15, 20, 25, 30, 40, 50, 60, 75, 100 };

        private readonly object _lock = new();
        private readonly StreamWriter _csv;
        private readonly StreamWriter _events;
        private readonly StreamWriter _timeline;
        private readonly DateTime _startUtc = DateTime.UtcNow;
        private readonly int _speed;
        private readonly Dictionary<string, BotProgress> _progress = new();
        private readonly string _directory;
        private readonly List<(string Bot, string Milestone, double Hours, int Level)> _rows = new();

        private sealed class BotProgress
        {
            public int Level;
            public int Region;
            public byte BossMask;
            public byte LastDeathTick;
            public bool DeathSeen;
            public int Deaths;
            public long Gold;
            public int Rebirths;
            public readonly Dictionary<string, int> Counters = new();
        }

        public MilestoneLog(string directory, int speed)
        {
            Directory.CreateDirectory(directory);
            _directory = directory;
            _speed = Math.Max(1, speed);
            _csv = new StreamWriter(Path.Combine(directory, "milestones.csv")) { AutoFlush = true };
            _events = new StreamWriter(Path.Combine(directory, "events.log")) { AutoFlush = true };
            _csv.WriteLine("bot,milestone,game_hours,wall_minutes,level,region,gold,deaths");
            _timeline = new StreamWriter(Path.Combine(directory, "timeline.csv")) { AutoFlush = true };
            _timeline.WriteLine("bot,game_hours,level,xp,region,location,gold,diamonds,activity,larder,deaths,forge,town_hall,rebirths");
        }

        public double GameHours => (DateTime.UtcNow - _startUtc).TotalHours * _speed;
        private double WallMinutes => (DateTime.UtcNow - _startUtc).TotalMinutes;

        public void Observe(string bot, StateUpdatePacket s)
        {
            lock (_lock)
            {
                if (!_progress.TryGetValue(bot, out var p))
                {
                    p = new BotProgress { Level = s.CurrentLevel, Region = s.HighestUnlockedRegion, BossMask = s.DefeatedRegionBossMask, LastDeathTick = s.LastDeathTick };
                    _progress[bot] = p;
                    Row(bot, "start", s, p);
                    return;
                }

                p.Gold = s.Gold;
                if (s.LastDeathTick != p.LastDeathTick)
                {
                    p.LastDeathTick = s.LastDeathTick;
                    p.Deaths++;
                    if (!p.DeathSeen)
                    {
                        p.DeathSeen = true;
                        Row(bot, "first_death", s, p);
                    }
                    EventLocked(bot, $"died to monster {s.LastDeathMonsterId} (death {p.Deaths})");
                }

                // Modul: after a rebirth the level climbs from 1 again, so its
                // marks are named per life (r1_level_10) rather than logged twice.
                string life = p.Rebirths == 0 ? "" : $"r{p.Rebirths}_";
                foreach (int mark in LevelMarks)
                {
                    if (p.Level < mark && s.CurrentLevel >= mark) Row(bot, $"{life}level_{mark}", s, p);
                }
                p.Level = s.CurrentLevel;

                if (s.HighestUnlockedRegion > p.Region)
                {
                    for (int r = p.Region + 1; r <= s.HighestUnlockedRegion; r++) Row(bot, $"region_{r}_open", s, p);
                    p.Region = s.HighestUnlockedRegion;
                }

                for (int r = 1; r <= 5; r++)
                {
                    int boss = RaceUnlockRegistry.GetRegionBossMonsterId(r);
                    if (BossFirstClearRules.IsDefeated(s.DefeatedRegionBossMask, boss) && !BossFirstClearRules.IsDefeated(p.BossMask, boss))
                        Row(bot, $"boss_r{r}_defeated", s, p);
                }
                p.BossMask = s.DefeatedRegionBossMask;
            }
        }

        /// <summary>A milestone the bot itself knows about (a rebirth), logged like the observed ones.</summary>
        public void Milestone(string bot, string milestone, StateUpdatePacket s)
        {
            lock (_lock)
            {
                if (!_progress.TryGetValue(bot, out var p)) return;
                if (milestone.StartsWith("rebirth_", StringComparison.Ordinal)) { p.Rebirths++; p.Level = 0; }
                Row(bot, milestone, s, p);
            }
        }

        /// <summary>One row per bot per game hour: where it stands, for curves rather than milestones (report 4.3).</summary>
        public void Timeline(string bot, StateUpdatePacket s)
        {
            lock (_lock)
            {
                _progress.TryGetValue(bot, out var p);
                _timeline.WriteLine(string.Join(",", bot, GameHours.ToString("0.00", CultureInfo.InvariantCulture), s.CurrentLevel, s.CurrentXp,
                    s.HighestUnlockedRegion, s.HighestLocationReached, s.Gold, s.PremiumCurrencyBalance, s.ActiveActivityId,
                    s.Food1_Count + s.Food2_Count + s.Food3_Count, p?.Deaths ?? 0, s.ForgeLevel, s.TownHallLevel, p?.Rebirths ?? 0));
            }
        }

        /// <summary>Counts something per bot (fishing trips, ...) for the summary row.</summary>
        public void Count(string bot, string counter)
        {
            lock (_lock)
            {
                if (!_progress.TryGetValue(bot, out var p)) return;
                p.Counters[counter] = p.Counters.GetValueOrDefault(counter) + 1;
            }
        }

        public void Event(string bot, string text)
        {
            lock (_lock) EventLocked(bot, text);
        }

        private void EventLocked(string bot, string text)
            => _events.WriteLine($"{GameHours,8:0.00}h  {bot,-10} {text}");

        private void Row(string bot, string milestone, StateUpdatePacket s, BotProgress p)
        {
            string line = string.Join(",",
                bot, milestone,
                GameHours.ToString("0.000", CultureInfo.InvariantCulture),
                WallMinutes.ToString("0.0", CultureInfo.InvariantCulture),
                s.CurrentLevel, s.HighestUnlockedRegion, s.Gold, p.Deaths);
            _csv.WriteLine(line);
            _rows.Add((bot, milestone, GameHours, s.CurrentLevel));
            Console.WriteLine($"{GameHours,8:0.00}h  {bot,-10} {milestone} (level {s.CurrentLevel}, region {s.HighestUnlockedRegion}, deaths {p.Deaths})");
            EventLocked(bot, $"MILESTONE {milestone}");
        }

        public void Summary(string bot, StateUpdatePacket s, long frames)
        {
            lock (_lock)
            {
                _progress.TryGetValue(bot, out var p);
                double fps = frames / Math.Max(1.0, (DateTime.UtcNow - _startUtc).TotalSeconds);
                Row(bot, "end", s, p ?? new BotProgress());
                EventLocked(bot, $"state frames/s {fps:0.0} (healthy: ~1)");
                if (p != null)
                {
                    foreach (var (name, value) in p.Counters) EventLocked(bot, $"total {name}: {value}");
                    EventLocked(bot, $"total deaths: {p.Deaths}");
                }
                Console.WriteLine($"          {bot,-10} state frames/s {fps:0.0} (healthy: ~1)");
            }
        }

        /// <summary>The persona a bot name belongs to: "casual2" -> "casual".</summary>
        private static string PersonaOf(string bot) => bot.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        /// <summary>
        /// summary.md: for each persona, the MEDIAN game hours to each milestone
        /// and how many of its bots reached it. A median rather than a mean
        /// because one bot that stalls (a refused fight, a bad drop streak)
        /// should show as "2 of 3 reached it", not drag the number.
        /// </summary>
        public void WriteSummary(string? footer = null)
        {
            lock (_lock)
            {
                var personas = _rows.Select(r => PersonaOf(r.Bot)).Distinct().OrderBy(x => x).ToList();
                var botsPer = personas.ToDictionary(p => p, p => _rows.Where(r => PersonaOf(r.Bot) == p).Select(r => r.Bot).Distinct().Count());
                var milestones = _rows.Where(r => r.Milestone is not ("start" or "end"))
                    .GroupBy(r => r.Milestone).OrderBy(g => g.Min(r => r.Hours)).Select(g => g.Key).ToList();

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# Bot run summary ({GameHours:0.#} game hours, speed x{_speed})");
                sb.AppendLine();
                sb.AppendLine("Median game hours to each milestone; (n/m) = bots of that persona that reached it.");
                sb.AppendLine();
                sb.AppendLine("| milestone | " + string.Join(" | ", personas) + " |");
                sb.AppendLine("|---|" + string.Concat(personas.Select(_ => "---|")));
                foreach (string m in milestones)
                {
                    var cells = personas.Select(p =>
                    {
                        var hours = _rows.Where(r => r.Milestone == m && PersonaOf(r.Bot) == p).Select(r => r.Hours).OrderBy(h => h).ToList();
                        if (hours.Count == 0) return "-";
                        double median = hours.Count % 2 == 1 ? hours[hours.Count / 2] : (hours[hours.Count / 2 - 1] + hours[hours.Count / 2]) / 2;
                        return $"{median.ToString("0.00", CultureInfo.InvariantCulture)} h ({hours.Count}/{botsPer[p]})";
                    });
                    sb.AppendLine($"| {m} | " + string.Join(" | ", cells) + " |");
                }
                var endLevels = personas.Select(p =>
                {
                    var levels = _rows.Where(r => r.Milestone == "end" && PersonaOf(r.Bot) == p).Select(r => r.Level).OrderBy(l => l).ToList();
                    return levels.Count == 0 ? "-" : string.Join(", ", levels);
                });
                sb.AppendLine("| level at end | " + string.Join(" | ", endLevels) + " |");
                // What each bot did, by counter - which systems it actually used.
                var bots = _progress.Keys.OrderBy(b => b).ToList();
                var counters = _progress.Values.SelectMany(p => p.Counters.Keys).Distinct().OrderBy(c => c).ToList();
                if (counters.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("| counter | " + string.Join(" | ", bots) + " |");
                    sb.AppendLine("|---|" + string.Concat(bots.Select(_ => "---|")));
                    foreach (string c in counters)
                        sb.AppendLine($"| {c} | " + string.Join(" | ", bots.Select(b => _progress[b].Counters.GetValueOrDefault(c).ToString(CultureInfo.InvariantCulture))) + " |");
                    sb.AppendLine("| deaths | " + string.Join(" | ", bots.Select(b => _progress[b].Deaths.ToString(CultureInfo.InvariantCulture))) + " |");
                }
                if (footer != null)
                {
                    sb.AppendLine();
                    sb.AppendLine(footer);
                }
                File.WriteAllText(Path.Combine(_directory, "summary.md"), sb.ToString());
                Console.WriteLine(sb.ToString());
            }
        }

        public void Dispose()
        {
            _csv.Dispose();
            _events.Dispose();
            _timeline.Dispose();
        }
    }
}
