using System;
using System.Threading.Tasks;

namespace FolkIdle.Server.Engine
{
    // Modul: auto-reroll's loop, 2026-09-12. Extracted from AffixRerollEngine
    // because the decision it makes was being made on state that had not
    // committed.
    //
    // The loop used to read two instance fields on the engine
    // (LastRerollResultRarity / LastRerollResultAffixId) which were assigned
    // BEFORE SaveChangesAsync and never cleared on a rollback. Three ways that
    // lies, and all three were reachable in production:
    //
    //   1. A Serializable conflict or any throw after the assignment rolls the
    //      attempt back and leaves the fields holding the roll that did not
    //      happen - so the run could STOP on a Legendary the player never
    //      received. Reported as "I think it sometimes skips the legendary".
    //   2. "Did this attempt commit" was tested by asking whether the affix id
    //      field was empty, so a rollback and a validation refusal were the same
    //      event as far as the loop could tell.
    //   3. Program.cs constructs ONE AffixRerollEngine for the whole server and
    //      SimulationEngine dispatches reroll commands as concurrent tasks, so
    //      those fields were shared between players. The engine's own comment
    //      claimed otherwise.
    //
    // An attempt now REPORTS what it committed, after its transaction is
    // durable, and the loop can see nothing else. Pure and
    // delegate-driven - like AutoRerollPlanner beside it - so the loop's
    // behaviour is unit-testable without a Postgres fixture, which is the other
    // half of why these bugs were only ever found in production.
    public readonly struct RerollAttemptOutcome
    {
        // False for every path that did not reach a durable commit: a rollback,
        // an unaffordable price, a locked item, a lost row. The loop must not
        // read the rarity or the affix id in that case - they describe a roll
        // the player does not own.
        public readonly bool DidCommit;
        public readonly AffixRarity Rarity;
        public readonly string AffixId;
        public readonly long GoldSpent;

        // The CommandResultCode the attempt would have reported on its own, so
        // the one end-of-run message can say why a run stopped early instead of
        // a bare green tick. 0 when the attempt committed.
        public readonly byte FailureCode;

        private RerollAttemptOutcome(bool didCommit, AffixRarity rarity, string affixId, long goldSpent, byte failureCode)
        {
            DidCommit = didCommit;
            Rarity = rarity;
            AffixId = affixId ?? string.Empty;
            GoldSpent = goldSpent;
            FailureCode = failureCode;
        }

        public static RerollAttemptOutcome Committed(AffixRarity rarity, string affixId, long goldSpent)
            => new RerollAttemptOutcome(true, rarity, affixId, goldSpent, 0);

        public static RerollAttemptOutcome Rejected(byte failureCode)
            => new RerollAttemptOutcome(false, AffixRarity.Common, string.Empty, 0L, failureCode);
    }

    // What the run did, reported once. Every field exists because the player
    // asked a question the old single green toast per attempt could not answer:
    // how many of my fifty did it use, what did it cost, and what did it stop
    // on.
    public readonly struct AutoRerollRunResult
    {
        public readonly AutoRerollStopReason Reason;
        public readonly int AttemptsCommitted;
        public readonly long GoldSpent;
        public readonly AffixRarity FinalRarity;
        public readonly string FinalAffixId;
        public readonly byte FailureCode;

        public AutoRerollRunResult(
            AutoRerollStopReason reason,
            int attemptsCommitted,
            long goldSpent,
            AffixRarity finalRarity,
            string finalAffixId,
            byte failureCode = 0)
        {
            Reason = reason;
            AttemptsCommitted = attemptsCommitted;
            GoldSpent = goldSpent;
            FinalRarity = finalRarity;
            FinalAffixId = finalAffixId ?? string.Empty;
            FailureCode = failureCode;
        }

        public static AutoRerollRunResult Refused(AutoRerollStopReason reason, byte failureCode = 0)
            => new AutoRerollRunResult(reason, 0, 0L, AffixRarity.Common, string.Empty, failureCode);
    }

    public static class AutoRerollRunner
    {
        /// <summary>
        /// Rolls until the stop condition is met, the attempt limit is reached,
        /// or an attempt fails to commit.
        ///
        /// `attemptAsync` receives the zero-based attempt index - the same
        /// number the gold curve is quoted against - and must report what it
        /// actually committed.
        /// </summary>
        public static async Task<AutoRerollRunResult> RunAsync(
            AutoRerollStopCondition condition,
            int maxAttempts,
            Func<int, Task<RerollAttemptOutcome>> attemptAsync)
        {
            if (attemptAsync == null) throw new ArgumentNullException(nameof(attemptAsync));

            maxAttempts = AutoRerollPlanner.ClampAttempts(maxAttempts);

            int committed = 0;
            long goldSpent = 0L;
            AffixRarity lastRarity = AffixRarity.Common;
            string lastAffixId = string.Empty;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                RerollAttemptOutcome outcome = await attemptAsync(attempt);

                if (!outcome.DidCommit)
                {
                    // Nothing was paid and nothing changed. Stop rather than
                    // hammering the same failing transaction to the limit, and
                    // carry the reason out so the player is told which refusal
                    // it was.
                    return new AutoRerollRunResult(
                        AutoRerollStopReason.BudgetExhausted,
                        committed,
                        goldSpent,
                        lastRarity,
                        lastAffixId,
                        outcome.FailureCode);
                }

                committed++;
                goldSpent += outcome.GoldSpent;
                lastRarity = outcome.Rarity;
                lastAffixId = outcome.AffixId;

                if (AutoRerollPlanner.IsSatisfied(condition, outcome.Rarity, outcome.AffixId))
                {
                    return new AutoRerollRunResult(
                        AutoRerollStopReason.ConditionMet,
                        committed,
                        goldSpent,
                        lastRarity,
                        lastAffixId);
                }
            }

            return new AutoRerollRunResult(
                AutoRerollStopReason.AttemptLimitReached,
                committed,
                goldSpent,
                lastRarity,
                lastAffixId);
        }
    }
}
