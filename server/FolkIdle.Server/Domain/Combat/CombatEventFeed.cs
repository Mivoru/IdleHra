using System.Collections.Concurrent;
using System.Threading;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// The queue between the simulation tick and the socket, for combat events.
    ///
    /// Static for the same reason <see cref="BossFirstClearAnnouncer"/> is:
    /// this is reached from inside <c>SimulationEngine.ProcessSubTick</c>,
    /// which is a static method with no service provider and no registry in
    /// hand. Threading a queue through ProcessAllSlotSubTicks and its callers
    /// to reach one publish site would be a much larger edit than the feature,
    /// in the file this project is most careful about editing.
    ///
    /// PUBLISHING NEVER TOUCHES A SOCKET. Enqueue only - the drain lives in
    /// NetworkBroadcastSystem's own loop, exactly as loot drops do, so a slow
    /// or dead client can never stall the 10Hz simulation.
    /// </summary>
    public static class CombatEventFeed
    {
        /// <summary>
        /// Bounded, and dropping is the correct behaviour when it is full.
        ///
        /// An idle game means every connected player is in combat essentially
        /// forever, so this queue has a permanent producer. A combat line is a
        /// nice-to-have that is worthless a few seconds after the blow it
        /// describes, so under pressure the right thing is to drop new events
        /// rather than to grow without limit or to make the tick wait.
        ///
        /// Sized for roughly two seconds of a full server at about one event
        /// per second per player.
        /// </summary>
        public const int MaxPending = 2048;

        private static readonly ConcurrentQueue<ResponseCombatEventPacket> _pending = new();

        // Signed because Interlocked works on int; it goes over the wire as
        // uint, and wrapping past int.MaxValue is fine - the client only ever
        // compares adjacent values for ordering, never their magnitude.
        private static int _sequence;

        /// <summary>How many events were dropped because the queue was full.</summary>
        public static long DroppedCount;

        public static int PendingCount => _pending.Count;

        /// <summary>
        /// Called from the tick. Allocation-free, lock-free, and returns
        /// immediately whether or not anyone is listening.
        /// </summary>
        public static void Publish(
            long playerId,
            int monsterId,
            byte eventKind,
            int amount,
            int monsterHpAfter,
            byte flags = 0)
        {
            if (_pending.Count >= MaxPending)
            {
                Interlocked.Increment(ref DroppedCount);
                return;
            }

            _pending.Enqueue(new ResponseCombatEventPacket
            {
                PlayerId = playerId,
                MonsterId = monsterId,
                Amount = amount,
                MonsterHpAfter = monsterHpAfter,
                Sequence = unchecked((uint)Interlocked.Increment(ref _sequence)),
                EventKind = eventKind,
                Flags = flags,
            });
        }

        public static bool TryDequeue(out ResponseCombatEventPacket packet) => _pending.TryDequeue(out packet);

        /// <summary>
        /// Drops everything pending. For tests, which must not inherit a
        /// previous test's events - the queue is process-wide.
        /// </summary>
        public static void Clear()
        {
            while (_pending.TryDequeue(out _)) { }
            Interlocked.Exchange(ref DroppedCount, 0L);
        }
    }
}
