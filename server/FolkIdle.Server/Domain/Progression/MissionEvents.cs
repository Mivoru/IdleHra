using System.Collections.Concurrent;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The one hook every engine calls when a mission act has really happened.
    /// </summary>
    /// <remarks>
    /// Modul: CALL IT FROM THE SUCCESS BRANCH, AFTER THE COMMIT. A refused or
    /// rolled-back act must not count, so each writer sits beside the line that
    /// proves the act landed (a CommitAsync, a Success result, an Ok outcome).
    ///
    /// It only enqueues, so it is safe from the 10 Hz tick and from a REST
    /// handler alike; MissionProgressWorker adds the counts up and writes them.
    /// The day is stamped HERE, at the act, so an act just before midnight
    /// counts for the day it happened even if the worker drains it after.
    ///
    /// MissionWriterCoverageTests fails if any MissionAction has no caller of
    /// Record outside this folder. A read side with no writer is the
    /// "computed but never consumed" defect inverted, and this codebase has
    /// shipped it more than once (IsAffixLocked, CLAUDE.md).
    /// </remarks>
    public static class MissionEvents
    {
        public readonly record struct MissionEvent(long PlayerId, int DayKey, MissionAction Action, short Detail, int Amount);

        internal static readonly ConcurrentQueue<MissionEvent> Queue = new();

        public static void Record(long playerId, MissionAction action, int amount = 1, int detail = 0)
        {
            if (playerId <= 0 || amount <= 0) return;
            int dayKey = (int)DailyLoginRewardEngine.ToDateKey(GameClock.UtcNow);
            Queue.Enqueue(new MissionEvent(playerId, dayKey, action, (short)detail, amount));
        }
    }
}
