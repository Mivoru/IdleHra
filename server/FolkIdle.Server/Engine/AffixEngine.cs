using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public static class AffixEngine
    {
        public static int CalculateFlatHp(int regionTier, int rarityTier)
        {
            // Flat HP Law: Value = floor(15 * RegionTier * 1.22^(RarityTier - 1))
            return (int)Math.Floor(15.0 * regionTier * Math.Pow(1.22, rarityTier - 1));
        }

        public static int CalculateFlatArmor(int regionTier, int rarityTier)
        {
            // Flat Armor/Stat Law: Value = floor(2 * Region Tier * 1.18^(RarityTier - 1))
            return (int)Math.Floor(2.0 * regionTier * Math.Pow(1.18, rarityTier - 1));
        }

        public static int CalculatePercentagePool(int baseValue, int growthIncrement, int rarityTier)
        {
            // Percentage Pools: Value = BaseValue + (GrowthIncrement * (RarityTier - 1))
            return baseValue + (growthIncrement * (rarityTier - 1));
        }

        public static string GetRandomAffixKey()
        {
            int idx = Random.Shared.Next(0, 12);
            switch (idx)
            {
                case 0: return "flat_hp";
                case 1: return "flat_armor";
                case 2: return "melee_dmg_pct";
                case 3: return "range_dmg_pct";
                case 4: return "magic_dmg_pct";
                case 5: return "attack_speed_pct";
                case 6: return "crit_chance_pct";
                case 7: return "crit_dmg_pct";
                case 8: return "lifesteal_pct";
                case 9: return "armor_pen_flat";
                case 10: return "dodge_chance_pct";
                default: return "block_chance_pct";
            }
        }
    }
}
