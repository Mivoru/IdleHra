using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>
    /// One scored strike on its way to the boss row (spec 5.7): the REST handler
    /// builds it, the tick adds A x G from the payload, and the engine applies it
    /// inside the Serializable transaction and completes <see cref="Completion"/>.
    /// </summary>
    public sealed class WorldBossStrikeOrder
    {
        public required long PlayerId { get; init; }

        /// <summary>
        /// The encounter the challenge was issued in. A strike that arrives after
        /// the week rolled over answers NotActive and spends nothing (spec 5.6).
        /// 0 for an auto-strike, which is issued and applied in one go.
        /// </summary>
        public long EncounterEndEpoch { get; init; }

        /// <summary>In tap order - the break rule takes the first qualifying spear.</summary>
        public required IReadOnlyList<SpearLanding> Landings { get; init; }

        /// <summary>
        /// This attempt's weak plate, drawn at issue and held on the challenge
        /// (spec 3.3.1). Null for an auto-strike: the engine draws it under the
        /// row lock, from the plates unbroken at that moment.
        /// </summary>
        public int? WeakPlate { get; init; }

        /// <summary>M. The scorer's for an accepted wheel log, Floor for everything else.</summary>
        public required double Multiplier { get; init; }

        /// <summary>What the player is told if it lands: Landed, ResolvedAtFloor or Refused.</summary>
        public required WorldBossStrikeResult LandedAs { get; init; }

        public int SpearsLost { get; init; }

        public TaskCompletionSource<WorldBossStrikeOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once; a second answer is ignored rather than thrown.</summary>
        public void Complete(WorldBossStrikeOutcome outcome) => Completion.TrySetResult(outcome);
    }

    /// <summary>How one strike ended, as the result card needs it.</summary>
    public sealed record WorldBossStrikeOutcome(
        WorldBossStrikeResult Result,
        long Damage = 0,
        double Multiplier = WorldBossStrikeRules.Floor,
        double PlateMultiplier = 1.0,
        double Played = 1.0,
        int BrokePlate = -1,
        IReadOnlyList<LandingDto>? Landings = null)
    {
        public static WorldBossStrikeOutcome Refusal(WorldBossStrikeResult result) => new(result);
    }

    /// <summary>
    /// What the strike service may know about the boss: public board state and
    /// a way to count today's strikes. Deliberately NO weak plate - in wheel
    /// mode the secret lives on the challenge and nowhere else.
    /// </summary>
    public interface IWorldBossStrikeBoard
    {
        bool IsEventActive { get; }
        bool IsBossDead();
        long EventEndEpoch { get; }
        long BossCurrentHp { get; }
        long BossMaxHp { get; }
        byte BrokenPlateMask { get; }

        /// <summary>Strikes this player has spent today (UTC). A plain read, no lock.</summary>
        Task<int> StrikesUsedTodayAsync(long playerId);

        /// <summary>Hands the order to the tick, which prices and applies it.</summary>
        void Submit(WorldBossStrikeOrder order);
    }

    /// <summary>
    /// The per-attempt weak plate (spec 3.3.1): uniform over the plates that are
    /// NOT broken, so every break raises the odds of the rest and four broken
    /// make the fifth certain.
    /// </summary>
    public static class WeakPlateDraw
    {
        public const int AllPlatesMask = (1 << WorldBossStrikeRules.PlateCount) - 1;

        /// <remarks>
        /// Modul: A FULL MASK FALLS BACK TO ALL FIVE. It cannot happen while the
        /// engine refuses to break the last plate, but a draw with nothing to
        /// pick from must still answer rather than throw.
        /// </remarks>
        public static int From(int brokenMask, Func<int, int> uniform)
        {
            Span<int> open = stackalloc int[WorldBossStrikeRules.PlateCount];
            int count = 0;
            for (int plate = 0; plate < WorldBossStrikeRules.PlateCount; plate++)
            {
                if ((brokenMask & (1 << plate)) == 0) open[count++] = plate;
            }
            if (count == 0) return uniform(WorldBossStrikeRules.PlateCount);
            return open[uniform(count)];
        }

        public static int From(int brokenMask) => From(brokenMask, RandomNumberGenerator.GetInt32);

        /// <summary>
        /// Whether breaking <paramref name="plate"/> would leave no plate
        /// standing. The last plate is always that attempt's weak one, so it
        /// never breaks (spec 3.3.1, rule 3) - enforced here, under the lock,
        /// because a challenge's draw may be a moment stale.
        /// </summary>
        public static bool WouldBreakTheLast(int brokenMask, int plate) =>
            ((brokenMask | (1 << plate)) & AllPlatesMask) == AllPlatesMask;
    }
}
