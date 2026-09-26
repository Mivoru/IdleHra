using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    public sealed class WorldBossBoardRow
    {
        public int Rank { get; set; }
        public long PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public long Damage { get; set; }
        /// <summary>The display name of the title the player wears, or null.</summary>
        public string? Title { get; set; }
    }

    public sealed class WorldBossBoardView
    {
        /// <summary>Everything every player has dealt this encounter, together.</summary>
        public long TotalDamage { get; set; }
        public long BossMaxHp { get; set; }
        public long BossCurrentHp { get; set; }
        public int Participants { get; set; }
        public IReadOnlyList<WorldBossBoardRow> Top { get; set; } = Array.Empty<WorldBossBoardRow>();
        /// <summary>The asking player's own row, ranked among everyone - or null before their first strike.</summary>
        public WorldBossBoardRow? Me { get; set; }
        /// <summary>The reward bracket the asking player sits in right now, by the same rule the payout uses.</summary>
        public string? MyBracket { get; set; }
    }

    /// <summary>
    /// The encounter's damage board, and the one ranking the payout uses
    /// (owner, 2026-09-26: the boss does not have to fall - everyone is paid by
    /// what they dealt, and the board shows what the server dealt together).
    /// </summary>
    /// <remarks>
    /// Modul: THE BOARD AND THE PAYOUT RANK FROM THE SAME ROWS.
    /// player_world_boss_attempts.TotalInflictedDamage is written in the same
    /// transaction as the damage itself, so it survives a restart - unlike the
    /// in-memory damage map and the Redis hash the payout used to read, which
    /// a restart mid-week emptied. A board that ranked one way and a payout
    /// that paid another would be two sources for one truth.
    /// </remarks>
    public static class WorldBossBoard
    {
        public const int Size = 50;

        /// <summary>Everyone who dealt damage this encounter, best first; ties go to the lower id.</summary>
        public static async Task<List<(long PlayerId, long Damage)>> RankedAsync(FolkIdleDbContext db)
        {
            var rows = await db.PlayerWorldBossAttempts.AsNoTracking()
                .Where(a => a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId && a.TotalInflictedDamage > 0)
                .OrderByDescending(a => a.TotalInflictedDamage)
                .ThenBy(a => a.PlayerId)
                .Select(a => new { a.PlayerId, a.TotalInflictedDamage })
                .ToListAsync();
            return rows.Select(r => (r.PlayerId, r.TotalInflictedDamage)).ToList();
        }

        /// <summary>
        /// The reward bracket for rank <paramref name="rank"/> (1-based) of
        /// <paramref name="participants"/>: top 1%, top 10%, top 50%, or
        /// participation. Tokens and gold as the payout mails them.
        /// </summary>
        public static (string Bracket, int Tokens, long Gold) BracketFor(int rank, int participants)
        {
            double percentile = (double)rank / Math.Max(1, participants);
            if (percentile <= 0.01) return ("Top 1%", 10, 250_000L);
            if (percentile <= 0.10) return ("Top 10%", 6, 100_000L);
            if (percentile <= 0.50) return ("Top 50%", 3, 50_000L);
            return ("Participation", 1, 10_000L);
        }

        public static async Task<WorldBossBoardView> ViewAsync(FolkIdleDbContext db, long playerId)
        {
            var ranked = await RankedAsync(db);
            var snapshot = await db.WorldBossSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(s => s.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);

            var topIds = ranked.Take(Size).Select(r => r.PlayerId).ToList();
            if (playerId > 0 && !topIds.Contains(playerId)) topIds.Add(playerId);
            var people = await db.PlayerRecords.AsNoTracking()
                .Where(p => topIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Username, p.ActiveTitleSlug })
                .ToDictionaryAsync(p => p.Id);

            WorldBossBoardRow Row(int index)
            {
                var (id, damage) = ranked[index];
                people.TryGetValue(id, out var person);
                return new WorldBossBoardRow
                {
                    Rank = index + 1,
                    PlayerId = id,
                    Name = string.IsNullOrWhiteSpace(person?.Username) ? $"Player {id}" : person!.Username!,
                    Damage = damage,
                    Title = TitleRegistry.DisplayNameFor(person?.ActiveTitleSlug),
                };
            }

            int mine = ranked.FindIndex(r => r.PlayerId == playerId);
            return new WorldBossBoardView
            {
                TotalDamage = ranked.Sum(r => r.Damage),
                BossMaxHp = snapshot?.MaxHp ?? 0,
                BossCurrentHp = snapshot?.CurrentHp ?? 0,
                Participants = ranked.Count,
                Top = Enumerable.Range(0, Math.Min(Size, ranked.Count)).Select(Row).ToList(),
                Me = mine >= 0 ? Row(mine) : null,
                MyBracket = mine >= 0 ? BracketFor(mine + 1, ranked.Count).Bracket : null,
            };
        }
    }
}
