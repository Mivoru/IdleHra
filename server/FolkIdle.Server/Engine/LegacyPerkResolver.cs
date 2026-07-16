namespace FolkIdle.Server.Engine
{
    // Modul: pure, allocation-free resolver for PlayerRecord.LegacyPerks -
    // mirrors RaceMasteryResolver's shape (static methods over a packed
    // primitive field) so combat/reward math can read perk bonuses on the
    // 10Hz hot path without touching the database. Each perk occupies an
    // 8-bit rank slot; rank 0 means "never purchased" and every rank grants
    // a flat +1 percentage point of its respective bonus, so a rank of 12
    // is a flat +12%.
    public static class LegacyPerkResolver
    {
        public const int XpMultiplierBitOffset = 0;
        public const int GoldDropRateBitOffset = 8;
        public const int CombatSpeedBitOffset = 16;
        public const long PerkRankMask = 0xFFL;

        public const int MaxPerkRank = 50;

        public static int GetPerkRank(long legacyPerks, int bitOffset)
        {
            return (int)((legacyPerks >> bitOffset) & PerkRankMask);
        }

        public static long SetPerkRank(long legacyPerks, int bitOffset, int newRank)
        {
            long cleared = legacyPerks & ~(PerkRankMask << bitOffset);
            return cleared | (((long)newRank & PerkRankMask) << bitOffset);
        }

        public static int GetXpBonusPct(long legacyPerks) => GetPerkRank(legacyPerks, XpMultiplierBitOffset);

        public static int GetGoldBonusPct(long legacyPerks) => GetPerkRank(legacyPerks, GoldDropRateBitOffset);

        public static int GetCombatSpeedBonusPct(long legacyPerks) => GetPerkRank(legacyPerks, CombatSpeedBitOffset);

        // Modul: linear cost curve, matching LegacyStoreEngine.
        // CalculateCitizenSlotCost's own style (a flat base plus a
        // per-rank increment) rather than a compounding curve, since perk
        // ranks are meant to be earned steadily across many raid/gathering
        // sessions, not gated behind an exponential wall.
        public static int CalculatePerkRankCost(int currentRank)
        {
            if (currentRank >= MaxPerkRank)
            {
                return int.MaxValue;
            }

            return 20 + (currentRank * 8);
        }
    }
}
