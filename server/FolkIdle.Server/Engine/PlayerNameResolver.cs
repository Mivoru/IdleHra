using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// A player's name, for the places that talk ABOUT a player rather than to
    /// one.
    ///
    /// Every announcement in the game said "Player #123". The id is the one
    /// thing about a player that means nothing to anybody else reading global
    /// chat - it is a database key - and the game has had usernames since
    /// registration was added.
    ///
    /// Cached because an announcement is a broadcast: one rare drop produces
    /// one line read by everyone, and the name behind it does not change. The
    /// cache is unbounded by design and bounded in practice - it holds one
    /// short string per player who has ever done something worth announcing,
    /// which is a far smaller set than "players".
    /// </summary>
    public static class PlayerNameResolver
    {
        /// <summary>
        /// Set once by Program at startup. Static for the same reason
        /// ChatEngine's announcement queue is: the callers reach this from
        /// inside a simulation tick or a detached drain task, neither of which
        /// has a service provider in hand.
        /// </summary>
        public static IDbContextFactory<FolkIdleDbContext>? ContextFactory;

        private static readonly ConcurrentDictionary<long, string> _cache = new();

        /// <summary>
        /// Falls back to "Player #id" rather than to an empty string: a line
        /// reading "found a Mythic Iron Helm" with nobody in it is worse than
        /// one naming a number.
        /// </summary>
        public static string Fallback(long playerId) => $"Player #{playerId}";

        /// <summary>
        /// The name if it is already known, without touching the database.
        /// For callers on the tick, which must not wait on IO.
        /// </summary>
        public static string GetCachedOrFallback(long playerId)
            => _cache.TryGetValue(playerId, out string? cached) ? cached : Fallback(playerId);

        public static async Task<string> GetAsync(long playerId)
        {
            if (_cache.TryGetValue(playerId, out string? cached)) return cached;

            var factory = ContextFactory;
            if (factory is null) return Fallback(playerId);

            try
            {
                await using var db = await factory.CreateDbContextAsync();
                string? username = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.Username)
                    .SingleOrDefaultAsync();

                string resolved = string.IsNullOrWhiteSpace(username) ? Fallback(playerId) : username!;

                // Only cache a REAL name. Caching the fallback would pin an
                // account created before usernames existed to "Player #12"
                // forever, even after it picks one up.
                if (!string.IsNullOrWhiteSpace(username)) _cache[playerId] = resolved;
                return resolved;
            }
            catch (Exception ex)
            {
                // A name lookup must never be able to break the thing it is
                // decorating - an announcement is a nice-to-have.
                Console.WriteLine($"PlayerNameResolver failed for {playerId}: {ex.Message}");
                return Fallback(playerId);
            }
        }

        /// <summary>
        /// Populated on hydration, so the tick-side callers hit the cache
        /// rather than the fallback.
        /// </summary>
        public static void Remember(long playerId, string? username)
        {
            if (playerId > 0 && !string.IsNullOrWhiteSpace(username)) _cache[playerId] = username!;
        }
    }
}
