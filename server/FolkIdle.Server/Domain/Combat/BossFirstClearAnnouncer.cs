using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using StackExchange.Redis;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// Announces a boss falling, and says truthfully whether it was a first in
    /// the world.
    ///
    /// Two different claims live here and only one of them is cheap. "This
    /// player has never beaten this boss" is a bit on the payload, known
    /// instantly inside the tick. "Nobody anywhere has ever beaten this boss"
    /// is a contended, durable fact - and a 10Hz simulation loop is the wrong
    /// place to go and find out, because the answer needs a round trip and
    /// getting it wrong in the fast direction means two players are both told
    /// they were first.
    ///
    /// So the tick calls Announce and returns immediately. The claim is settled
    /// off the loop, against Redis SET NX: exactly one caller in the whole
    /// cluster can create the key, and that one is the world first. If Redis is
    /// not there the line still goes out, without the claim - a missing
    /// superlative is a much smaller failure than a false one, and than no
    /// announcement at all.
    /// </summary>
    public static class BossFirstClearAnnouncer
    {
        /// <summary>
        /// Set once by Program at startup. Static for the same reason
        /// ChatEngine's announcement queue is: this is reached from inside the
        /// simulation tick, which has no service provider in hand, and
        /// threading a multiplexer through every engine that might announce
        /// something would couple half the server to Redis.
        /// </summary>
        public static IConnectionMultiplexer? Redis;

        /// <summary>
        /// Bounded, like the chat announcement queue it feeds. An announcement
        /// is a nice-to-have, so under a flood it is correct to drop rather
        /// than to grow without limit.
        /// </summary>
        private const int MaxPending = 128;

        private static readonly ConcurrentQueue<(long PlayerId, int MonsterId)> _pending = new();
        private static int _drainRunning;

        public static void Announce(long playerId, int monsterId)
        {
            if (_pending.Count >= MaxPending) return;
            _pending.Enqueue((playerId, monsterId));

            // One drain task at a time, started on demand. The tick never waits
            // for it and never blocks on Redis.
            if (System.Threading.Interlocked.Exchange(ref _drainRunning, 1) == 0)
            {
                _ = Task.Run(DrainAsync);
            }
        }

        private static async Task DrainAsync()
        {
            try
            {
                while (_pending.TryDequeue(out var item))
                {
                    string bossName = ContentRegistry.GetMonsterName(item.MonsterId);
                    bool worldFirst = await TryClaimWorldFirstAsync(item.MonsterId);

                    Social.ChatEngine.EnqueueSystemAnnouncement(
                        worldFirst
                            ? $"Player #{item.PlayerId} is the FIRST in the world to defeat {bossName}. Congratulations!"
                            : $"Player #{item.PlayerId} defeated {bossName} for the first time. Congratulations!");
                }
            }
            catch (Exception ex)
            {
                // An announcement that throws must not take anything with it.
                Console.WriteLine($"BossFirstClearAnnouncer failed: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _drainRunning, 0);

                // A producer that enqueued between the last dequeue and the
                // flag being cleared would otherwise wait for the next kill.
                if (!_pending.IsEmpty
                    && System.Threading.Interlocked.Exchange(ref _drainRunning, 1) == 0)
                {
                    _ = Task.Run(DrainAsync);
                }
            }
        }

        private static async Task<bool> TryClaimWorldFirstAsync(int monsterId)
        {
            var redis = Redis;
            if (redis is null || !redis.IsConnected) return false;

            try
            {
                // SET NX with no expiry: a world first happens once, ever, and
                // an expiring key would hand the title out again next week.
                return await redis.GetDatabase().StringSetAsync(
                    $"worldfirst:boss:{monsterId}",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    expiry: null,
                    when: When.NotExists);
            }
            catch (RedisException)
            {
                // Modul: fails CLOSED on the CLAIM, not on the announcement.
                // Unreachable Redis means "cannot prove it was a first", which
                // is not the same as "it was" - this codebase has already
                // shipped one fail-open Redis defect and it cost a live hole.
                return false;
            }
        }
    }
}
