using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Domain.Progression
{
    public sealed class LifeRecordBoardRow
    {
        public int Rank { get; set; }
        public long PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        /// <summary>Fastest life to a level-50 rebirth, seconds.</summary>
        public long BestTo50 { get; set; }
        /// <summary>Fastest life to a level-100 rebirth, seconds, or 0.</summary>
        public long BestTo100 { get; set; }
        public int RebirthCount { get; set; }
        public string? Title { get; set; }
    }

    /// <summary>
    /// The Fastest lives board (2026-11-13): every account with a timed life
    /// that ended in a level-50 rebirth, fastest first (RebirthRules.LifeRecord).
    /// </summary>
    /// <remarks>
    /// Modul: GLORY ONLY, like the Deepest board - a plain query, not a Redis
    /// board, so no payout can pick it up. It exists because the game review
    /// (docs/design/GAME_REVIEW_2026_11_13.md, D) found that with a handful of
    /// players the social features that work are asynchronous ones: a record
    /// someone else can try to beat on their own time.
    /// </remarks>
    public static class LifeRecordBoard
    {
        public const int Size = 50;

        public static async Task<List<LifeRecordBoardRow>> TopAsync(FolkIdleDbContext db)
        {
            var rows = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.BestLifeSecondsTo50 > 0)
                .OrderBy(p => p.BestLifeSecondsTo50)
                .ThenBy(p => p.Id)
                .Take(Size)
                .Select(p => new { p.Id, p.Username, p.BestLifeSecondsTo50, p.BestLifeSecondsTo100, p.RebirthCount, p.ActiveTitleSlug })
                .ToListAsync();

            return rows.Select((p, i) => new LifeRecordBoardRow
            {
                Rank = i + 1,
                PlayerId = p.Id,
                Name = string.IsNullOrWhiteSpace(p.Username) ? $"Player {p.Id}" : p.Username!,
                BestTo50 = p.BestLifeSecondsTo50,
                BestTo100 = p.BestLifeSecondsTo100,
                RebirthCount = p.RebirthCount,
                Title = TitleRegistry.DisplayNameFor(p.ActiveTitleSlug),
            }).ToList();
        }
    }
}
