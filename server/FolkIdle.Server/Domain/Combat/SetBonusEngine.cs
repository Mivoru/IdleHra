using System;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: Architecture Overhaul, Part 4. Authoritative equipment-set
    // bonus catalog and evaluator. Callers pass every currently-equipped
    // slot's EquipmentInstance.SetId (0 = no set) as a span; the evaluator
    // counts occurrences per distinct non-zero SetId and applies the
    // 2-piece / 4-piece threshold modifiers for each set that qualifies -
    // a player wearing pieces from two different sets simultaneously
    // (e.g. 2 Chiming Steel + 2 Eternal Dreadnought) gets both sets'
    // 2-piece bonuses stacked, matching how set-bonus systems conventionally
    // resolve mixed loadouts.
    public static class SetBonusEngine
    {
        public const int ChimingSteelSetId = 1;
        public const int EternalDreadnoughtSetId = 10;

        // Bounds the fixed-size local scratch buffers below so the scan
        // never allocates regardless of how many slots a caller passes -
        // matches CharacterSlotEngine's MaxCharacterSlots-style contract
        // of a small, known-at-compile-time upper bound. 8 comfortably
        // covers every equip slot named in the GDD (Weapon, Helper, Helm,
        // Chest, Leggings, Gloves, Boots) with headroom to spare.
        public const int MaxTrackedSlots = 8;

        public struct SetBonusResult
        {
            public int FlatAttackPowerBonus;
            public float TotalArmorMultiplierPct;
            public float FireDamageMultiplierPct;

            // Modul: 4-piece mechanics are cached here but not yet consumed
            // by the live combat tick (no thorns-reflect-damage, burn-DoT,
            // or cooldown-reduction application loop exists yet) - matching
            // this codebase's own established "computed here but not yet
            // consumed anywhere" precedent (see StatsCalculator's
            // CritMitigationPct doc comment).
            public bool ThornsReflectionActive;
            public bool CooldownReductionActive;
            public bool BurnApplicationActive;
            public bool CcImmunityActive;
        }

        // Zero-allocation occurrence count + threshold evaluation. Two
        // fixed-size stack scratch arrays (distinct set ids seen, their
        // counts) replace what would otherwise be a Dictionary<int,int> -
        // with at most MaxTrackedSlots equipped items the O(n^2) linear
        // scan is trivially cheap and never touches the managed heap.
        public static SetBonusResult Evaluate(ReadOnlySpan<int> equippedSetIds)
        {
            var result = new SetBonusResult();

            Span<int> distinctSetIds = stackalloc int[MaxTrackedSlots];
            Span<int> counts = stackalloc int[MaxTrackedSlots];
            int distinctCount = 0;

            for (int i = 0; i < equippedSetIds.Length; i++)
            {
                int setId = equippedSetIds[i];
                if (setId <= 0)
                {
                    continue;
                }

                int foundIndex = -1;
                for (int j = 0; j < distinctCount; j++)
                {
                    if (distinctSetIds[j] == setId)
                    {
                        foundIndex = j;
                        break;
                    }
                }

                if (foundIndex >= 0)
                {
                    counts[foundIndex]++;
                }
                else if (distinctCount < MaxTrackedSlots)
                {
                    distinctSetIds[distinctCount] = setId;
                    counts[distinctCount] = 1;
                    distinctCount++;
                }
            }

            for (int i = 0; i < distinctCount; i++)
            {
                ApplySetTiers(distinctSetIds[i], counts[i], ref result);
            }

            return result;
        }

        private static void ApplySetTiers(int setId, int matchCount, ref SetBonusResult result)
        {
            if (matchCount < 2)
            {
                return;
            }

            switch (setId)
            {
                case ChimingSteelSetId:
                    // 2-Piece: offensive core.
                    result.FlatAttackPowerBonus += 10;
                    if (matchCount >= 4)
                    {
                        // 4-Piece: fire-infused follow-through.
                        result.FireDamageMultiplierPct += 12f;
                        result.BurnApplicationActive = true;
                    }
                    break;

                case EternalDreadnoughtSetId:
                    // 2-Piece: defensive core.
                    result.TotalArmorMultiplierPct += 15f;
                    if (matchCount >= 4)
                    {
                        // 4-Piece: bulwark mechanics.
                        result.ThornsReflectionActive = true;
                        result.CcImmunityActive = true;
                        result.CooldownReductionActive = true;
                    }
                    break;
            }
        }
    }
}
