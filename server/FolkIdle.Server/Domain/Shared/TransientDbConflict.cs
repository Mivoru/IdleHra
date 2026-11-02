using System;
using System.Threading.Tasks;
using Npgsql;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// Postgres serialization conflicts (40001) and deadlocks (40P01): what they
    /// are, and a retry for a command that can safely run again.
    ///
    /// Modul: A CONFLICT IS NOT A FAULT (found by the playtest bots, 2026-11-02).
    /// A Serializable transaction that loses a race with another write - the
    /// tick's checkpoint flush, most often - is rolled back with 40001, and
    /// Postgres expects the caller to try again. Commands opened on the plain
    /// context factory had no retry, so the exception reached SafeDispatchAsync,
    /// which force-disconnected the player. Seen for ChangeActivity and
    /// Potions.Stock: 15-24 times per bot in 1,526 game hours of play.
    /// RetryingDbContextOptions is the answer for engines built around an EF
    /// execution strategy; this is the same answer for a delegate that owns its
    /// whole transaction.
    /// </summary>
    public static class TransientDbConflict
    {
        public const int DefaultAttempts = 3;

        /// <summary>True when <paramref name="ex"/> or an inner exception is a serialization failure or a deadlock.</summary>
        public static bool IsConflict(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is PostgresException pg
                    && (pg.SqlState == PostgresErrorCodes.SerializationFailure || pg.SqlState == PostgresErrorCodes.DeadlockDetected))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Runs <paramref name="attempt"/>, and again after a short random pause
        /// when it fails with a conflict, up to <paramref name="attempts"/> times.
        ///
        /// ONLY for a delegate that is safe to run twice: it opens its own
        /// context and transaction, and does nothing outside the database before
        /// its commit. A conflict rolls the whole attempt back, so a rerun starts
        /// from what is committed.
        /// </summary>
        public static async Task<T> RetryAsync<T>(Func<Task<T>> attempt, int attempts = DefaultAttempts)
        {
            for (int i = 1; ; i++)
            {
                try
                {
                    return await attempt().ConfigureAwait(false);
                }
                catch (Exception ex) when (i < attempts && IsConflict(ex))
                {
                    await Task.Delay(Random.Shared.Next(20, 80) * i).ConfigureAwait(false);
                }
            }
        }

        public static Task RetryAsync(Func<Task> attempt, int attempts = DefaultAttempts)
            => RetryAsync(async () => { await attempt().ConfigureAwait(false); return true; }, attempts);
    }
}
