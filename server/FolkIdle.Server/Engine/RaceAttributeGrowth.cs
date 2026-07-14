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

            GetGrowthPerLevel(activeRaceId, out int str, out int dex, out int con, out int lck);

            payload.STR += str * levelsGained;
            payload.DEX += dex * levelsGained;
            payload.CON += con * levelsGained;
            payload.LCK += lck * levelsGained;
        }
    }
}
