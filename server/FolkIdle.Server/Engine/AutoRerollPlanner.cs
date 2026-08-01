using System;

namespace FolkIdle.Server.Engine
{
    // Modul: auto-reroll stop conditions, 2026-08-01.
    //
    // Deliberately a PURE evaluator with no database, no async and no engine
    // reference. The decision "is this roll good enough to stop on" is the only
    // part of auto-reroll with interesting logic, and keeping it pure means it
    // is directly unit-testable without a Postgres fixture - every other reroll
    // path needs Testcontainers, which is why bugs in them have historically
    // been found in production rather than in CI.
    //
    // The conditions combine with AND, which is what makes "stop at Legendary
    // STR" expressible: rarity floor AND exact affix id. Either half may be
    // left unset to mean "any".
    public readonly struct AutoRerollStopCondition
    {
        // AffixRarity.Common (1) means "any rarity", since every roll is at
        // least Common - it is a floor, not an equality test. Stopping only on
        // an exact rarity would make "stop at Epic" fail on a Legendary, which
        // is never what a player means.
        public readonly AffixRarity MinimumRarity;

        // Null or empty means "any stat". Compared against the bare affix id,
        // so callers must not pass a payload key with a stack or rarity suffix.
        public readonly string? RequiredAffixId;

        public AutoRerollStopCondition(AffixRarity minimumRarity, string? requiredAffixId = null)
        {
            MinimumRarity = minimumRarity < AffixRarity.Common ? AffixRarity.Common : minimumRarity;
            RequiredAffixId = requiredAffixId;
        }

        public bool HasAffixConstraint => !string.IsNullOrEmpty(RequiredAffixId);

        // True when this condition would stop on literally the first roll.
        // Callers should reject such a request rather than spend a player's
        // gold on a reroll whose result was guaranteed to be accepted.
        public bool IsTriviallySatisfied => MinimumRarity <= AffixRarity.Common && !HasAffixConstraint;
    }

    public enum AutoRerollStopReason
    {
        ConditionMet = 0,
        AttemptLimitReached = 1,
        BudgetExhausted = 2,
        RejectedTrivialCondition = 3,
        RejectedUnreachableCondition = 4
    }

    public static class AutoRerollPlanner
    {
        // Hard ceiling on a single auto-reroll request regardless of what the
        // client asks for. A run is a loop of Serializable transactions, so an
        // unbounded request is a self-inflicted denial of service; and the
        // escalating gold curve means attempt 200 would cost more than any
        // player could hold anyway.
        public const int MaxAttemptsPerRequest = 100;

        public static bool IsSatisfied(in AutoRerollStopCondition condition, AffixRarity rolledRarity, string rolledAffixId)
        {
            if (rolledRarity < condition.MinimumRarity)
            {
                return false;
            }

            if (condition.HasAffixConstraint
                && !string.Equals(condition.RequiredAffixId, rolledAffixId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        // A stat-type constraint is only reachable if that affix is legal for
        // the item's slot. Without this check, "stop at block_chance_pct" on a
        // sword would spend the player's entire budget on a target that can
        // never be rolled - block_chance_pct is shield-only.
        //
        // A Value reroll never changes the stat, so an affix constraint that
        // does not already match is unreachable under that operation too.
        public static bool IsConditionReachable(
            in AutoRerollStopCondition condition,
            string baseItemId,
            RerollOperation operation,
            string currentAffixId)
        {
            if (!condition.HasAffixConstraint)
            {
                return true;
            }

            if (operation == RerollOperation.Value || operation == RerollOperation.UpgradeRarity)
            {
                // Neither operation can change which stat the affix is.
                return string.Equals(condition.RequiredAffixId, currentAffixId, StringComparison.Ordinal);
            }

            if (!AffixRegistry.TryGetDefinition(condition.RequiredAffixId!, out var required))
            {
                return false;
            }

            EquipmentSlotKind slot = AffixRegistry.ResolveSlot(baseItemId);
            EquipmentSlotMask mask = AffixRegistry.ToMask(slot);
            return (required.AllowedSlots & mask) != 0;
        }

        // An UpgradeRarity run can only ever climb, and stops dead at
        // Legendary. Asking it to reach a rarity below the affix's current one
        // is already satisfied; asking it to exceed Legendary never completes.
        public static bool IsRarityTargetReachable(in AutoRerollStopCondition condition, RerollOperation operation, AffixRarity currentRarity)
        {
            if (condition.MinimumRarity > AffixRarity.Legendary)
            {
                return false;
            }

            if (operation == RerollOperation.UpgradeRarity)
            {
                return true;
            }

            // Value rerolls never move rarity, so a target above the current
            // rarity can never be met by them.
            if (operation == RerollOperation.Value)
            {
                return currentRarity >= condition.MinimumRarity;
            }

            // StatType preserves rarity as well - see AffixRerollEngine.
            return currentRarity >= condition.MinimumRarity;
        }

        public static int ClampAttempts(int requestedAttempts)
        {
            if (requestedAttempts < 1) return 1;
            if (requestedAttempts > MaxAttemptsPerRequest) return MaxAttemptsPerRequest;
            return requestedAttempts;
        }

        // Total gold an auto-reroll run would cost if it went the full distance,
        // so the UI can quote a worst case before the player commits. Uses the
        // same escalating curve the engine charges, summed over the run.
        public static long EstimateWorstCaseGoldCost(int itemRarityTier, int attempts, bool rerollStatType)
        {
            attempts = ClampAttempts(attempts);

            long total = 0L;
            for (int i = 0; i < attempts; i++)
            {
                long attemptCost = AffixRegistry.CalculateRerollGoldCost(itemRarityTier, i, rerollStatType);
                if (attemptCost >= AffixRegistry.RerollGoldMaxCost)
                {
                    // Saturated - every further attempt costs the same, so the
                    // remainder is a multiply rather than a loop.
                    total += attemptCost * (attempts - i);
                    break;
                }
                total += attemptCost;
            }

            return total;
        }
    }
}
