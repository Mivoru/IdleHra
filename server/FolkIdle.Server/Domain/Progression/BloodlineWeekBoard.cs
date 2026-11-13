using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Progression
{
    public sealed class BloodlineWeekRow
    {
        public int Rank { get; set; }
        public long PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Title { get; set; }
        /// <summary>The best child's name (may be empty - the client names it by race).</summary>
        public string ChildName { get; set; } = string.Empty;
        public int ChildRaceId { get; set; }
        /// <summary>The best child's four aptitudes summed.</summary>
        public int AptitudeTotal { get; set; }
        /// <summary>Its weakest aptitude - the tiebreak, and what the bloodline climb measures.</summary>
        public int LowestAptitude { get; set; }
        /// <summary>Children this account bred this week, the best included.</summary>
        public int ChildrenThisWeek { get; set; }
    }

    /// <summary>
    /// The Bloodline week board (2026-11-13): each account's best child born
    /// since Monday 00:00 UTC, by aptitude total, then by weakest aptitude.
    /// </summary>
    /// <remarks>
    /// Modul: GLORY ONLY, like Fastest lives and the Deepest board - a plain
    /// query that pays nothing, so there is nothing to farm. The game review
    /// (docs/design/GAME_REVIEW_2026_11_13.md, C and D) asked for breeding to
    /// have a social side that works with three players: a weekly record the
    /// others can try to beat on their own time. The week is derived from the
    /// clock (WorldBossCalendar.WeekStart), never reset by a job.
    ///
    /// Modul: A CHILD IS A LINEAGE MEMBER WITH A PARENT, the same rule as the
    /// Book of Deeds (DeedProgressSource): a founder or a granted character
    /// born this week is not a bred child. Only living rows count - a child
    /// released from the Hall leaves the board with it.
    /// </remarks>
    public static class BloodlineWeekBoard
    {
        public const int Size = 50;

        public static async Task<List<BloodlineWeekRow>> TopAsync(FolkIdleDbContext db, DateTimeOffset now)
        {
            long weekStart = WorldBossCalendar.WeekStart(now).ToUnixTimeSeconds();

            var children = await (
                from c in db.CharacterRecords.AsNoTracking()
                join l in db.CharacterLineages.AsNoTracking() on c.Id equals l.CharacterId
                where c.BornAtEpoch >= weekStart && (l.ParentPaternalId != null || l.ParentMaternalId != null)
                select new { c.PlayerId, c.Name, c.BornAtEpoch, Lineage = l })
                .ToListAsync();

            if (children.Count == 0) return new List<BloodlineWeekRow>();

            var best = children
                .GroupBy(c => c.PlayerId)
                .Select(g =>
                {
                    var top = g
                        .Select(c => new { c.Name, c.BornAtEpoch, c.Lineage, Apt = c.Lineage.AptitudeVector() })
                        .OrderByDescending(c => c.Apt.Sum())
                        .ThenByDescending(c => c.Apt.Min())
                        .ThenBy(c => c.BornAtEpoch)
                        .First();
                    return new { PlayerId = g.Key, top.Name, top.BornAtEpoch, Total = top.Apt.Sum(), Lowest = top.Apt.Min(),
                        RaceId = (int)(top.Lineage.GeneticVector & 0xFF), Count = g.Count() };
                })
                .OrderByDescending(r => r.Total)
                .ThenByDescending(r => r.Lowest)
                .ThenBy(r => r.BornAtEpoch)
                .Take(Size)
                .ToList();

            var ids = best.Select(b => b.PlayerId).ToList();
            var players = await db.PlayerRecords.AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.Username, p.ActiveTitleSlug })
                .ToDictionaryAsync(p => p.Id);

            return best.Select((b, i) =>
            {
                players.TryGetValue(b.PlayerId, out var p);
                return new BloodlineWeekRow
                {
                    Rank = i + 1,
                    PlayerId = b.PlayerId,
                    Name = string.IsNullOrWhiteSpace(p?.Username) ? $"Player {b.PlayerId}" : p!.Username!,
                    Title = TitleRegistry.DisplayNameFor(p?.ActiveTitleSlug),
                    ChildName = b.Name ?? string.Empty,
                    ChildRaceId = b.RaceId,
                    AptitudeTotal = b.Total,
                    LowestAptitude = b.Lowest,
                    ChildrenThisWeek = b.Count,
                };
            }).ToList();
        }
    }
}
