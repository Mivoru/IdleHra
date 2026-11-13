using System;

namespace FolkIdle.Server.Engine
{
    // Modul: pure, allocation-free resolver for PlayerRecord.LegacyPerks -
    // mirrors RaceMasteryResolver's shape (static methods over a packed
    // primitive field) so combat/reward math can read perk bonuses on the
    // 10Hz hot path without touching the database. Each perk occupies an
    // 8-bit rank slot; rank 0 means "never purchased" and every rank grants
    // a flat +1 percentage point of its respective bonus, so a rank of 12
    // is a flat +12%.
    //
    // Modul: THE ANCESTRAL BOONS (2026-11-13). These perks were applied live
    // and offline (LiveKillXpMultiplierPct, CombatGoldReward,
    // EffectiveMilliAttackFor) but no client could buy one, so Legacy Shards
    // piled up with nothing to spend them on - 40,000-55,000 on the three
    // accounts that had rebirthed. Players can buy them now (Rebirth screen),
    // and they were retuned first: 50 ranks of +1% at 20+8r shards would have
    // let those balances buy +50% XP, +50% gold AND +50% damage on day one.
    // Now XP and gold stop at +25% and the combat-speed boon at +10%, on
    // geometric prices; maxing all three costs about 75,000 shards, roughly
    // twenty level-90 rebirths (RebirthRules.ShardsForLevel). Bounded, so the
    // power gap a veteran can buy stays small, and an endless-feeling sink
    // for the shards an unlimited rebirth keeps paying.
    public static class LegacyPerkResolver
    {
        public const int XpMultiplierBitOffset = 0;
        public const int GoldDropRateBitOffset = 8;
        public const int CombatSpeedBitOffset = 16;
        public const long PerkRankMask = 0xFFL;

        /// <summary>The highest rank any boon has; MaxRankFor is the real cap per boon.</summary>
        public const int MaxPerkRank = 25;

        public const int XpMaxRank = 25;
        public const int GoldMaxRank = 25;
        public const int CombatSpeedMaxRank = 10;

        public const int XpBaseCost = 200;
        public const int GoldBaseCost = 200;
        public const int CombatSpeedBaseCost = 500;

        /// <summary>Price growth per rank, in percent (112 = x1.12).</summary>
        public const int XpGrowthPct = 112;
        public const int GoldGrowthPct = 112;
        public const int CombatSpeedGrowthPct = 130;

        public static int GetPerkRank(long legacyPerks, int bitOffset)
        {
            return (int)((legacyPerks >> bitOffset) & PerkRankMask);
        }

        public static long SetPerkRank(long legacyPerks, int bitOffset, int newRank)
        {
            long cleared = legacyPerks & ~(PerkRankMask << bitOffset);
            return cleared | (((long)newRank & PerkRankMask) << bitOffset);
        }

        // Clamped to the boon's cap on read, so a rank written under the old
        // 50-rank rules (none exist in production; every account read 0 on
        // 2026-11-13) can never pay past the new ceiling.
        public static int GetXpBonusPct(long legacyPerks) => Math.Min(GetPerkRank(legacyPerks, XpMultiplierBitOffset), XpMaxRank);

        public static int GetGoldBonusPct(long legacyPerks) => Math.Min(GetPerkRank(legacyPerks, GoldDropRateBitOffset), GoldMaxRank);

        public static int GetCombatSpeedBonusPct(long legacyPerks) => Math.Min(GetPerkRank(legacyPerks, CombatSpeedBitOffset), CombatSpeedMaxRank);

        /// <summary>The cap of the boon at this bit offset, or 0 for an unknown offset.</summary>
        public static int MaxRankFor(int bitOffset) => bitOffset switch
        {
            XpMultiplierBitOffset => XpMaxRank,
            GoldDropRateBitOffset => GoldMaxRank,
            CombatSpeedBitOffset => CombatSpeedMaxRank,
            _ => 0,
        };

        /// <summary>
        /// Shards for the next rank of a boon, or int.MaxValue at its cap (callers
        /// read that as "refuse", never as a price).
        /// </summary>
        public static int CalculatePerkRankCost(int bitOffset, int currentRank)
        {
            (int baseCost, int growthPct) = bitOffset switch
            {
                XpMultiplierBitOffset => (XpBaseCost, XpGrowthPct),
                GoldDropRateBitOffset => (GoldBaseCost, GoldGrowthPct),
                CombatSpeedBitOffset => (CombatSpeedBaseCost, CombatSpeedGrowthPct),
                _ => (0, 0),
            };
            if (baseCost <= 0 || currentRank < 0 || currentRank >= MaxRankFor(bitOffset)) return int.MaxValue;

            double cost = baseCost * Math.Pow(growthPct / 100.0, currentRank);
            // Rounded to tens, so a price reads as a price.
            return (int)Math.Min(int.MaxValue - 1, Math.Round(cost / 10.0) * 10);
        }

        /// <summary>Shards to take a boon from rank 0 to its cap.</summary>
        public static long TotalCostToMax(int bitOffset)
        {
            long total = 0;
            for (int r = 0; r < MaxRankFor(bitOffset); r++) total += CalculatePerkRankCost(bitOffset, r);
            return total;
        }
    }
}
