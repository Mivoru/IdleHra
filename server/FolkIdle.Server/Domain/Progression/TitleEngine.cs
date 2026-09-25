using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Domain.Progression
{
    public enum TitleResult
    {
        Ok = 0,
        /// <summary>The slug names a real title the player has not earned. Refused visibly, nothing changes.</summary>
        NotEarned,
        /// <summary>The slug names no title at all.</summary>
        UnknownTitle,
        PlayerNotFound
    }

    public sealed record EarnedTitle(string Slug, string Name, DateTime EarnedAtUtc);

    /// <summary>
    /// Granting and wearing titles. See TitleRegistry for what a title is.
    ///
    /// Modul: GRANTS ARE INSERT-ONLY AND IDEMPOTENT. player_titles is keyed
    /// (PlayerId, TitleSlug) and a grant is ON CONFLICT DO NOTHING, so the same
    /// milestone crossed twice, a replayed request or a retried transaction all
    /// leave exactly one row. GrantAsync runs on the CALLER's context so it
    /// joins the caller's transaction - a title is granted in the same
    /// transaction as the record that earned it, or not at all.
    ///
    /// Raw SQL on a snake_case table with PascalCase quoted columns (see
    /// CURRENT_IMPLEMENTATION_STATE.md §3).
    /// </summary>
    public static class TitleEngine
    {
        /// <summary>Grants <paramref name="slug"/>; true when this call created the row.</summary>
        public static async Task<bool> GrantAsync(FolkIdleDbContext db, long playerId, string slug, DateTime utcNow)
        {
            if (TitleRegistry.Find(slug) == null) return false;
            int inserted = await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_titles (\"PlayerId\", \"TitleSlug\", \"EarnedAtUtc\") VALUES ({0}, {1}, {2}) " +
                "ON CONFLICT (\"PlayerId\", \"TitleSlug\") DO NOTHING",
                playerId, slug, DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
            return inserted > 0;
        }

        /// <summary>The player's titles, known slugs only, oldest first.</summary>
        public static async Task<List<EarnedTitle>> ListAsync(FolkIdleDbContext db, long playerId)
        {
            var rows = await db.PlayerTitles.AsNoTracking()
                .Where(t => t.PlayerId == playerId)
                .OrderBy(t => t.EarnedAtUtc)
                .ToListAsync();

            var titles = new List<EarnedTitle>();
            foreach (var row in rows)
            {
                // A row whose slug the registry no longer knows is kept (a later
                // rename or restore may want it) and simply not shown.
                var def = TitleRegistry.Find(row.TitleSlug);
                if (def != null) titles.Add(new EarnedTitle(def.Slug, def.DisplayName, row.EarnedAtUtc));
            }
            return titles;
        }

        /// <summary>
        /// Wears <paramref name="slug"/>, or clears the title when it is null.
        /// Only an earned title can be worn: an unearned one answers NotEarned
        /// and changes nothing, rather than a silent no-op.
        /// </summary>
        public static async Task<TitleResult> SetActiveAsync(FolkIdleDbContext db, long playerId, string? slug)
        {
            var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
            if (player == null) return TitleResult.PlayerNotFound;

            if (slug != null)
            {
                if (TitleRegistry.Find(slug) == null) return TitleResult.UnknownTitle;
                bool earned = await db.PlayerTitles.AsNoTracking().AnyAsync(t => t.PlayerId == playerId && t.TitleSlug == slug);
                if (!earned) return TitleResult.NotEarned;
            }

            player.ActiveTitleSlug = slug;
            await db.SaveChangesAsync();
            return TitleResult.Ok;
        }
    }
}
