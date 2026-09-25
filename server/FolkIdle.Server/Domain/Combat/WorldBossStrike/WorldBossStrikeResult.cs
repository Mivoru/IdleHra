using System.Collections.Generic;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>
    /// The REST contract of the shield wheel (spec 5.7). Every value the client
    /// can receive has a sentence in client_web; worldBossResults.test.ts
    /// compares its list to Fixtures/world_boss_results.json, which
    /// WorldBossStrikeResultTests regenerates from this enum.
    /// </summary>
    public enum WorldBossStrikeResult
    {
        Issued,
        Outstanding,
        Disabled,
        NotActive,
        AlreadyDefeated,
        NoAttemptsLeft,
        TooLateInWindow,
        ChallengeOutstanding,
        NoChallenge,
        TooEarly,
        OutOfSpears,
        Landed,
        ResolvedAtFloor,
        Refused,
        Queued,
        Failed,
        PracticeScored,
    }

    public static class WorldBossStrikeResults
    {
        /// <summary>Every value, in declaration order, for the client mirror test.</summary>
        public static IReadOnlyList<WorldBossStrikeResult> AllPlayerFacing { get; } =
            (WorldBossStrikeResult[])System.Enum.GetValues(typeof(WorldBossStrikeResult));
    }
}
