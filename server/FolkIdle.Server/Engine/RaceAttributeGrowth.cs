namespace FolkIdle.Server.Engine
{
    // Modul 16/21: per-level STR/DEX/CON/LCK growth by race. Keyed by the same
    // activeRaceId derivation already used for combat stats (Slot1_GeneticVector
    // low byte, see StatsCalculator call sites) - if no character occupies
    // Slot1, activeRaceId is 0 and growth is a no-op, matching how race-gated
    // combat bonuses already behave in that same situation.
    public static class RaceAttributeGrowth
    {
        public static void GetGrowthPerLevel(int raceId, out int str, out int dex, out int con, out int lck)
        {
            switch (raceId)
            {
                case RaceIds.Human:
                    str = 2; dex = 2; con = 2; lck = 1;
                    break;
                case RaceIds.Vila:
                    str = 1; dex = 4; con = 1; lck = 2;
                    break;
                case RaceIds.Draugr:
                    str = 3; dex = 1; con = 4; lck = 0;
                    break;
                case RaceIds.Kobold:
                    str = 1; dex = 2; con = 1; lck = 4;
                    break;
                case RaceIds.Moosleute:
                    str = 2; dex = 2; con = 2; lck = 2;
                    break;
                case RaceIds.Vodnik:
                    str = 2; dex = 1; con = 3; lck = 2;
                    break;
                default:
                    str = 0; dex = 0; con = 0; lck = 0;
                    break;
            }
        }

        public static void ApplyLevelUpGrowth(ref TickStatePayload payload, int activeRaceId, int levelsGained)
        {
            if (levelsGained <= 0) return;

            // Race no longer decides the split - see the grant below. The table
            // above is still the authority for anything that wants to know what
            // a race's per-level budget WAS (the dev fixture allocates by it, so
            // a seeded account looks like a played one).

            // Modul 13.4.3: an Epic-mutated lineage grants +5% growth per level,
            // matching StatsCalculator's flat attribute bonus. Positive genetic
            // loci (bred via GeneticSplicingEngine) add a small further bonus
            // scaled by their combined magnitude, so a well-bred lineage grows
            // faster in addition to starting with a higher base line.
            float geneticMultiplier = 1.0f;
            if (payload.IsEpicMutation) geneticMultiplier += 0.05f;

            int lociSum = payload.LocusSpeed + payload.LocusCrit + payload.LocusYield;
            if (lociSum > 0) geneticMultiplier += lociSum * 0.001f;

            // Modul 13.4.3: an inbred lineage (see BreedingEngine's ancestor
            // check) locks growth down by a heavy -25%, composed multiplicatively
            // with the epic/loci bonus above so a character can never fully
            // offset the defect through good breeding luck alone.
            if (payload.IsInbred) geneticMultiplier *= 0.75f;

            // Modul: THE POINTS ARE GRANTED, NOT PLACED, 2026-09-06.
            //
            // This used to allocate all four attributes itself, by race, with no
            // say from the player - and the offline levelling path forgot to
            // call it at all, so the only account past level 1 reached level 86
            // holding a fresh registration's 50/50/50/25 and nobody noticed for
            // months. A system that can go entirely missing without being felt
            // is not carrying its weight.
            //
            // So a level pays a POOL and the player places it (see
            // CommandType.SpendAttributePoint). Race no longer decides the
            // split: every race grants AttributePointsPerLevel, and races keep
            // their identity through the innate passives and mastery bonuses
            // they already have, which are visible and chosen rather than a
            // one-point-a-level difference nothing ever surfaced.
            //
            // The genetic multiplier survives intact, because breeding for a
            // better lineage should still be worth something - it now buys more
            // POINTS rather than a faster automatic allocation.
            payload.UnspentAttributePoints += (int)(AttributePointsPerLevel * levelsGained * geneticMultiplier);
        }

        /// <summary>
        /// What one level is worth in attribute points.
        ///
        /// Seven, which is exactly what a Human used to be dealt automatically
        /// (2 STR + 2 DEX + 2 CON + 1 LCK) - so the pacing model, which has
        /// always been built on a Human, is unchanged by this becoming a choice.
        /// The five other races were on eight and lose a point a level; that gap
        /// was never visible anywhere in the game and is not worth preserving as
        /// a permanent racial advantage nothing explained.
        /// </summary>
        public const int AttributePointsPerLevel = 7;
    }
}
