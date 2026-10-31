using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>
    /// A dungeon run in progress: the paused simulation, the telegraph the
    /// player has been shown, and the bonuses on offer. One per player.
    /// </summary>
    public sealed class DungeonLiveRun
    {
        public required Guid Id { get; init; }
        public required long PlayerId { get; init; }
        public required DungeonKind Kind { get; init; }
        public required int Stage { get; init; }
        public required DungeonRunner Runner { get; init; }
        public required DungeonEngine.Income Income { get; init; }
        public required bool FreeRun { get; init; }
        public required int FoodSlot { get; init; }
        public required int FoodItemId { get; init; }
        public Random Rng { get; } = new();
        public object Gate { get; } = new();

        /// <summary>The swipes asked for, once the client has asked to see them; null before.</summary>
        public string[]? Telegraph { get; set; }
        public int WindowMs { get; set; }
        public DateTime IssuedUtc { get; set; }
        /// <summary>The bonuses offered after a clean dodge, until one is chosen.</summary>
        public IReadOnlyList<DungeonPerkView>? Offer { get; set; }
        public DateTime TouchedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Set once the reward has been paid, so it is paid once.</summary>
        public bool Paid { get; set; }
    }

    /// <summary>
    /// The live runs, in memory (slice 3). The server holds the fight between
    /// the player's answers, so the client never says "I dodged" - it sends
    /// swipes and the server judges them against the telegraph it chose.
    /// </summary>
    /// <remarks>
    /// Modul: IN MEMORY ON PURPOSE. A run lasts a minute or two, and the server
    /// runs one replica (ops/oracle/README.md). A restart mid-run loses the run
    /// and its ticket; a run left alone is played out with every telegraph
    /// missed and paid when the player starts another or opens the lobby after
    /// <see cref="AbandonAfter"/>, so walking away never pays more than playing.
    /// </remarks>
    public static class DungeonLiveRuns
    {
        public static readonly TimeSpan AbandonAfter = TimeSpan.FromMinutes(10);
        private static readonly ConcurrentDictionary<long, DungeonLiveRun> ByPlayer = new();

        public static DungeonLiveRun? Get(long playerId, Guid runId)
            => ByPlayer.TryGetValue(playerId, out var run) && run.Id == runId ? run : null;

        public static DungeonLiveRun? Current(long playerId) => ByPlayer.TryGetValue(playerId, out var run) ? run : null;

        public static void Put(DungeonLiveRun run) => ByPlayer[run.PlayerId] = run;

        public static void Remove(DungeonLiveRun run) => ByPlayer.TryRemove(new KeyValuePair<long, DungeonLiveRun>(run.PlayerId, run));
    }
}
