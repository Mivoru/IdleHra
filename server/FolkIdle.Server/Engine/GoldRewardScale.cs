using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// A gold reward sized to the player's own income: "an hour of what you earn".
    /// </summary>
    /// <remarks>
    /// Modul: A FLAT REWARD IS WORTH NOTHING AT THE TOP (owner, 2026-10-13). The
    /// world boss paid 10k-250k gold, and the owner's account earns 8M an hour -
    /// a milestone was not worth aiming at. So a reward is now a number of
    /// minutes of the player's own income, read from the ledger task 79 already
    /// keeps (GoldIncomeDaily), with a floor so a brand-new account with no
    /// income still gets something.
    ///
    /// ONLY EARNED GOLD COUNTS. Trades (Market) and every reward source - the
    /// login reward, mail (which is where these rewards themselves land), the
    /// guild payout, the Delve, the quest line, the starter purse - are left
    /// out, so a reward can never raise its own base week after week.
    ///
    /// The window is the last seven whole UTC days plus today, divided by 168
    /// hours: an idle game earns while the player is away, so a calendar hour
    /// is the honest unit, not an hour of play.
    /// </remarks>
    public static class GoldRewardScale
    {
        /// <summary>The least any scaled reward pays, in gold.</summary>
        public const long FloorGold = 20_000;

        public const int WindowDays = 7;

        public static readonly IReadOnlyList<GoldIncomeSource> EarnedSources = new[]
        {
            GoldIncomeSource.Combat,
            GoldIncomeSource.CombatAway,
            GoldIncomeSource.TownHall,
            GoldIncomeSource.AutoSalvage,
            GoldIncomeSource.ChestSale,
        };

        /// <summary>Gold for <paramref name="minutes"/> of a <paramref name="hourlyIncome"/>, never under the floor.</summary>
        public static long ForMinutes(long hourlyIncome, int minutes)
        {
            if (minutes <= 0) return 0;
            long scaled = (long)Math.Min(long.MaxValue / 2, (double)Math.Max(0, hourlyIncome) * minutes / 60.0);
            return Math.Max(FloorGold, scaled);
        }

        /// <summary>Average earned gold per hour over the window, per player. A player with no rows reads 0.</summary>
        public static async Task<Dictionary<long, long>> HourlyIncomeAsync(FolkIdleDbContext db, IReadOnlyCollection<long> playerIds, DateTime utcNow)
        {
            var result = playerIds.Distinct().ToDictionary(id => id, _ => 0L);
            if (result.Count == 0) return result;

            var since = DateOnly.FromDateTime(utcNow).AddDays(-WindowDays);
            var sources = EarnedSources.Select(s => (short)s).ToArray();
            var ids = result.Keys.ToArray();
            var sums = await db.GoldIncomeDaily.AsNoTracking()
                .Where(r => ids.Contains(r.PlayerId) && r.Day >= since && sources.Contains(r.Source))
                .GroupBy(r => r.PlayerId)
                .Select(g => new { PlayerId = g.Key, Total = g.Sum(r => r.Amount) })
                .ToListAsync();

            foreach (var row in sums)
            {
                result[row.PlayerId] = Math.Max(0, row.Total) / (WindowDays * 24L);
            }
            return result;
        }

        public static async Task<long> HourlyIncomeAsync(FolkIdleDbContext db, long playerId, DateTime utcNow)
            => (await HourlyIncomeAsync(db, new[] { playerId }, utcNow))[playerId];
    }
}
