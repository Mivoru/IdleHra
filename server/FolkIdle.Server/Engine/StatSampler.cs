using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// "Statistics that say how you play" (task 56): a ten-minute sample of
    /// every ONLINE account's lifetime counters, and the read that turns those
    /// samples into rates, a style and a timeline.
    ///
    /// Modul: WHY SAMPLES AND NOT A COUNTER PER EVENT. Every number here
    /// already has a durable, monotone home - kills in the codex, crafts on
    /// PlayerRecords, harvests on the Logistics achievement row, XP as level
    /// plus progress - so a rate is just two readings and a clock. Hooking the
    /// kill, harvest and craft paths to count per hour would add writes to the
    /// hottest code in the game for a screen opened a few times a day. This
    /// costs one small batch every ten minutes for the players who are online.
    ///
    /// OFFLINE PLAYERS ARE NOT SAMPLED, and that is correct rather than a gap:
    /// what they earn away arrives at the next login, and the first sample after
    /// it records it as earnings in that window - which is what it was.
    ///
    /// GOLD IS A BALANCE, not an income. "Earned" is the sum of the rises
    /// between consecutive readings and "spent" the sum of the falls, so a
    /// purchase inside one ten-minute gap hides that much income. Named as
    /// such on the screen; a per-category gold ledger is a separate job.
    /// </summary>
    public class StatSampler
    {
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan Retention = TimeSpan.FromDays(8);

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _registry;
        private CancellationTokenSource _cts = new();

        public StatSampler(IServiceProvider serviceProvider, PlayerSessionRegistry? registry)
        {
            _serviceProvider = serviceProvider;
            _registry = registry;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
            Console.WriteLine("Stat sampler started.");
        }

        public void StopCron() => _cts.Cancel();

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Modul: the try opens BEFORE the delay and before CreateScope,
                // where a refused connection throws (CronWorkerGuardTests).
                try
                {
                    await Task.Delay(Interval, stoppingToken);
                    long[] online = _registry?.GetOnlinePlayerIds() ?? Array.Empty<long>();
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    await SampleAsync(db, online, DateTime.UtcNow);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Stat sampler cycle failed: {ex.Message}");
                }
            }
        }

        /// <summary>Writes one sample per player and prunes past the retention window.</summary>
        public static async Task<int> SampleAsync(FolkIdleDbContext db, IReadOnlyCollection<long> playerIds, DateTime utcNow)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM player_stat_samples WHERE \"AtUtc\" < {utcNow - Retention}");
            if (playerIds.Count == 0) return 0;

            var samples = await ReadAsync(db, playerIds, utcNow);
            db.PlayerStatSamples.AddRange(samples);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return samples.Count;
        }

        /// <summary>The counters as they stand now, for these players - not saved.</summary>
        public static async Task<List<PlayerStatSample>> ReadAsync(FolkIdleDbContext db, IReadOnlyCollection<long> playerIds, DateTime utcNow)
        {
            var ids = playerIds.ToArray();

            var players = await db.PlayerRecords.AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.CurrentLevel, p.CurrentXp, p.TotalItemsCrafted })
                .ToListAsync();

            var kills = await db.MonsterCodexEntries.AsNoTracking()
                .Where(c => ids.Contains(c.PlayerId))
                .GroupBy(c => c.PlayerId)
                .Select(g => new { PlayerId = g.Key, Kills = g.Sum(c => (long)c.KillCount) })
                .ToDictionaryAsync(x => x.PlayerId, x => x.Kills);

            var gold = await db.CommodityRecords.AsNoTracking()
                .Where(c => ids.Contains(c.PlayerId) && c.ItemId == "gold")
                .ToDictionaryAsync(c => c.PlayerId, c => c.Quantity);

            var harvests = await db.PlayerLifetimeAchievements.AsNoTracking()
                .Where(a => ids.Contains(a.PlayerId) && a.AchievementId == AchievementMilestones.LogisticsAchievementId)
                .ToDictionaryAsync(a => a.PlayerId, a => a.CurrentProgress);

            var activities = await db.CharacterRecords.AsNoTracking()
                .Where(c => ids.Contains(c.PlayerId) && c.SlotIndex >= 0 && c.SlotIndex <= 2)
                .Select(c => new { c.PlayerId, c.ActiveActivityId })
                .ToListAsync();

            var result = new List<PlayerStatSample>(players.Count);
            foreach (var p in players)
            {
                var sample = new PlayerStatSample
                {
                    PlayerId = p.Id,
                    AtUtc = utcNow,
                    Kills = kills.GetValueOrDefault(p.Id),
                    Xp = CumulativeXp(p.CurrentLevel, p.CurrentXp),
                    Gold = gold.GetValueOrDefault(p.Id),
                    Harvests = harvests.GetValueOrDefault(p.Id),
                    Crafted = p.TotalItemsCrafted,
                };
                foreach (var a in activities.Where(a => a.PlayerId == p.Id))
                {
                    if (ActivityIdBands.IsCombatActivity(a.ActiveActivityId) || a.ActiveActivityId == ActivityIdBands.WorldBossActivityId) sample.Fighting++;
                    else if (ActivityIdBands.IsGatheringActivity(a.ActiveActivityId)) sample.Gathering++;
                    else if (ActivityIdBands.IsCraftingActivity(a.ActiveActivityId)) sample.Crafting++;
                    else sample.Idle++;
                }
                result.Add(sample);
            }
            return result;
        }

        /// <summary>Every XP point ever earned: the levels climbed plus progress into this one.</summary>
        public static long CumulativeXp(int level, long currentXp)
        {
            long total = 0;
            for (int l = 1; l < level; l++) total += ProgressionEngine.GetRequiredXpForLevel(l);
            return total + currentXp;
        }

        // ------------------------------------------------------------------
        // The read side.
        // ------------------------------------------------------------------

        public sealed class RateView
        {
            public string Window { get; set; } = string.Empty;
            public int WindowSeconds { get; set; }
            /// <summary>How much of the window the samples actually cover - short when the player has just arrived.</summary>
            public int CoveredSeconds { get; set; }
            public double KillsPerHour { get; set; }
            public double XpPerHour { get; set; }
            public double HarvestsPerHour { get; set; }
            public double CraftsPerHour { get; set; }
            public double GoldEarnedPerHour { get; set; }
            public long GoldEarned { get; set; }
            public long GoldSpent { get; set; }
        }

        public sealed class StyleView
        {
            public string Window { get; set; } = string.Empty;
            public int Samples { get; set; }
            public int FightingPct { get; set; }
            public int GatheringPct { get; set; }
            public int CraftingPct { get; set; }
            public int IdlePct { get; set; }
        }

        public sealed class TimelineEntry
        {
            public DateTime AtUtc { get; set; }
            public string Kind { get; set; } = string.Empty;
            public string Text { get; set; } = string.Empty;
        }

        public sealed class InsightsView
        {
            public int SampleIntervalMinutes { get; set; }
            public List<RateView> Rates { get; set; } = new();
            public List<StyleView> Style { get; set; } = new();
            public List<TimelineEntry> Timeline { get; set; } = new();
        }

        private static readonly (string Name, int Seconds)[] Windows =
        {
            ("10 min", 600), ("1 hour", 3600), ("24 hours", 86400),
        };

        public static async Task<InsightsView> BuildInsightsAsync(FolkIdleDbContext db, long playerId, DateTime utcNow)
        {
            var since = utcNow - Retention;
            var samples = await db.PlayerStatSamples.AsNoTracking()
                .Where(s => s.PlayerId == playerId && s.AtUtc >= since)
                .OrderBy(s => s.AtUtc)
                .ToListAsync();

            // The reading at this moment closes every window, so a player who
            // arrived five minutes ago still sees a rate.
            var now = (await ReadAsync(db, new[] { playerId }, utcNow)).FirstOrDefault();
            var points = new List<PlayerStatSample>(samples);
            if (now != null) points.Add(now);

            var view = new InsightsView { SampleIntervalMinutes = (int)Interval.TotalMinutes };
            foreach (var (name, seconds) in Windows)
            {
                view.Rates.Add(RateOver(points, utcNow, name, seconds));
            }

            view.Style.Add(StyleOver(samples, utcNow, "24 hours", TimeSpan.FromHours(24)));
            view.Style.Add(StyleOver(samples, utcNow, "7 days", TimeSpan.FromDays(7)));

            view.Timeline = await TimelineAsync(db, playerId);
            return view;
        }

        /// <summary>
        /// The rate over a window: from the last reading at or before its start
        /// (or the first inside it) to the last reading. Pure, for tests.
        /// </summary>
        public static RateView RateOver(IReadOnlyList<PlayerStatSample> points, DateTime utcNow, string name, int seconds)
        {
            var rate = new RateView { Window = name, WindowSeconds = seconds };
            if (points.Count < 2) return rate;

            DateTime start = utcNow.AddSeconds(-seconds);
            int from = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].AtUtc <= start) from = i;
            }
            var first = points[from];
            var last = points[^1];
            double covered = (last.AtUtc - first.AtUtc).TotalSeconds;
            if (covered < 60) return rate;

            rate.CoveredSeconds = (int)Math.Min(covered, seconds);
            double perHour = 3600.0 / covered;
            rate.KillsPerHour = Math.Round((last.Kills - first.Kills) * perHour, 1);
            rate.XpPerHour = Math.Round((last.Xp - first.Xp) * perHour, 1);
            rate.HarvestsPerHour = Math.Round((last.Harvests - first.Harvests) * perHour, 1);
            rate.CraftsPerHour = Math.Round((last.Crafted - first.Crafted) * perHour, 1);

            for (int i = from + 1; i < points.Count; i++)
            {
                long step = points[i].Gold - points[i - 1].Gold;
                if (step > 0) rate.GoldEarned += step; else rate.GoldSpent -= step;
            }
            rate.GoldEarnedPerHour = Math.Round(rate.GoldEarned * perHour, 1);
            return rate;
        }

        public static StyleView StyleOver(IReadOnlyList<PlayerStatSample> samples, DateTime utcNow, string name, TimeSpan window)
        {
            var style = new StyleView { Window = name };
            long fighting = 0, gathering = 0, crafting = 0, idle = 0;
            foreach (var s in samples)
            {
                if (s.AtUtc < utcNow - window) continue;
                style.Samples++;
                fighting += s.Fighting; gathering += s.Gathering; crafting += s.Crafting; idle += s.Idle;
            }
            long total = fighting + gathering + crafting + idle;
            if (total == 0) return style;
            style.FightingPct = (int)Math.Round(fighting * 100.0 / total);
            style.GatheringPct = (int)Math.Round(gathering * 100.0 / total);
            style.CraftingPct = (int)Math.Round(crafting * 100.0 / total);
            style.IdlePct = Math.Max(0, 100 - style.FightingPct - style.GatheringPct - style.CraftingPct);
            return style;
        }

        /// <summary>What the funnel recorded for this account, and the best drop, oldest first.</summary>
        private static async Task<List<TimelineEntry>> TimelineAsync(FolkIdleDbContext db, long playerId)
        {
            var steps = await db.PlayerFunnelEvents.AsNoTracking()
                .Where(f => f.PlayerId == playerId)
                .ToListAsync();

            var timeline = steps
                .Select(f => new TimelineEntry { AtUtc = f.At, Kind = "milestone", Text = FunnelRecorder.DisplayText((FunnelStep)f.Step) })
                .Where(t => t.Text.Length > 0)
                .ToList();

            var drop = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == playerId)
                .Select(p => new { p.BestDropTier, p.BestDropBaseId, p.BestDropAtUtc })
                .FirstOrDefaultAsync();
            if (drop?.BestDropAtUtc is DateTime at && drop.BestDropTier > 0)
            {
                timeline.Add(new TimelineEntry
                {
                    AtUtc = at,
                    Kind = "drop",
                    Text = $"Best drop so far: a {RarityTier.GetName(drop.BestDropTier)} {Prettify(drop.BestDropBaseId)}",
                });
            }

            return timeline.OrderBy(t => t.AtUtc).ToList();
        }


        private static string Prettify(string? baseId)
        {
            if (string.IsNullOrEmpty(baseId)) return "piece";
            var words = baseId.Split('_')
                .Where(w => w.Length > 0 && w != "eq" && w != "base" && w != "slot" && w != "armor" && w != "weapon")
                .Take(2);
            return string.Join(' ', words);
        }
    }
}
