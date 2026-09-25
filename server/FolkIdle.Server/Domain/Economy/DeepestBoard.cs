using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Domain.Economy
{
    public sealed class DeepestBoardRow
    {
        public int Rank { get; set; }
        public long PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Floor { get; set; }
        public DateTime? ReachedAtUtc { get; set; }
        /// <summary>The display name of the title the player wears, or null.</summary>
        public string? Title { get; set; }
    }

    /// <summary>
    /// "Deepest this week": the top 100 by DelveDeepestThisWeek.
    ///
    /// Modul: A BOARD THAT PAYS NOTHING, and a plain query rather than a Redis
    /// ZSET on purpose. LeaderboardPayoutEngine pays diamonds off
    /// `leaderboard:mastery` and reads no other key; a Deep board kept out of
    /// Redis cannot be picked up by that payout, or by any later one, without
    /// somebody writing the code on purpose - DeepestBoardTests pins both.
    /// There is no cron, so no StartCron and no CronWorkerGuardTests entry.
    ///
    /// The same population floor as the mastery board (MinimumRankedLevel)
    /// keeps exercise.mjs's throwaway accounts off it, and the week filter is
    /// DelveWeekKey = this week, so a stale week never shows. Ties go to whoever
    /// got there first.
    /// </summary>
    public static class DeepestBoard
    {
        public const int Size = 100;

        public static async Task<List<DeepestBoardRow>> TopAsync(FolkIdleDbContext db, DateTime utcNow)
        {
            int week = DelveEngine.CurrentWeekKey(utcNow);
            var rows = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.DelveWeekKey == week
                            && p.DelveDeepestThisWeek > 0
                            && p.CurrentLevel >= LeaderboardTierRegistry.MinimumRankedLevel)
                .OrderByDescending(p => p.DelveDeepestThisWeek)
                .ThenBy(p => p.DelveDeepestThisWeekAtUtc)
                .ThenBy(p => p.Id)
                .Take(Size)
                .Select(p => new { p.Id, p.Username, p.DelveDeepestThisWeek, p.DelveDeepestThisWeekAtUtc, p.ActiveTitleSlug })
                .ToListAsync();

            return rows.Select((p, i) => new DeepestBoardRow
            {
                Rank = i + 1,
                PlayerId = p.Id,
                Name = string.IsNullOrWhiteSpace(p.Username) ? $"Player {p.Id}" : p.Username!,
                Floor = p.DelveDeepestThisWeek,
                ReachedAtUtc = p.DelveDeepestThisWeekAtUtc,
                Title = TitleRegistry.DisplayNameFor(p.ActiveTitleSlug),
            }).ToList();
        }
    }
}
