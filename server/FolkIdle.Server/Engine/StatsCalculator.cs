using System.Runtime.InteropServices;

namespace FolkIdle.Server.Engine
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CombatStats
    {
        public int FlatMeleeDamage;
        public int FlatRangedDamage;
        public int FlatArmorPenetration;
        public int FlatPhysicalArmor;
        public int MaxHp;
        public float AttackSpeedPct;
        public float CritChancePct;
        public float OutOfCombatHpRegen;
        public float ForgeSuccessPct;
        public float LootLuckPct;
        public float DodgeChancePct;
        public float LifestealPct;
    }

    public static class StatsCalculator
    {
        public static CombatStats Calculate(int str, int dex, int con, int lck, int activeOffensivePotionId = 0, int activeDefensivePotionId = 0, int activeAgePhase = 1, int completedAreaFlags = 0, int activeRaceId = 0, int humanMastery = 0, int vilaMastery = 0, int draugrMastery = 0)
        {
            var stats = new CombatStats();
            
            // Strength (STR): +2 Melee Damage, +1 Armor Penetration.
            stats.FlatMeleeDamage = str * 2;
            stats.FlatArmorPenetration = str * 1;
            
            // Dexterity (DEX): +2 Ranged Damage, +0.05% Attack Speed, +0.1% Critical Hit Chance.
            stats.FlatRangedDamage = dex * 2;
            stats.AttackSpeedPct = dex * 0.05f;
            stats.CritChancePct = dex * 0.1f;
            
            // Constitution (CON): +15 Max HP, +1 Physical Armor, +0.1 Out-of-Combat HP Regen/sec.
            stats.MaxHp = con * 15;
            stats.FlatPhysicalArmor = con * 1;
            stats.OutOfCombatHpRegen = con * 0.1f;
            
            // Luck (LCK): +0.05% Forge Success, +0.1% Loot Luck.
            stats.ForgeSuccessPct = lck * 0.05f;
            stats.LootLuckPct = lck * 0.1f;
            stats.DodgeChancePct = 0f;
            stats.LifestealPct = 0f;

            // Sprint 38: Area Completion Loot Luck
            int areaLuckBonus = 0;
            for (int i = 1; i <= 10; i++)
            {
                if ((completedAreaFlags & (1 << i)) != 0)
                {
                    areaLuckBonus += 1; // +1.0% per area
                }
            }
            stats.LootLuckPct += areaLuckBonus;

            // Sprint 38: Race Mastery Milestones
            // Modul 13 fix: these previously checked raw literals (3, 4) that predate
            // RaceIds and never matched it (RaceIds.Vila=2, RaceIds.Draugr=3), so the
            // "Vila" bonus below fired for Draugr's active race and the "Draugr" bonus
            // fired for Kobold's - see RaceMasteryResolver for the milestone table.
            if (activeRaceId == RaceIds.Vila)
            {
                if (vilaMastery >= 10)
                {
                    stats.AttackSpeedPct += 0.15f; // Nullify armor agility penalty
                }
                stats.CritChancePct += RaceMasteryResolver.GetVilaCritBonusPct(vilaMastery);
            }

            if (activeRaceId == RaceIds.Draugr)
            {
                stats.LifestealPct += RaceMasteryResolver.GetDraugrLifestealBonusPct(draugrMastery) / 100f;
            }

            if (activeOffensivePotionId > 0 && activeOffensivePotionId <= ContentRegistry.ItemDefinitions.Length)
            {
                var offDef = ContentRegistry.ItemDefinitions[activeOffensivePotionId - 1];
                int tier = offDef.RegionTier;
                stats.FlatMeleeDamage += tier * 10;
                stats.FlatRangedDamage += tier * 10;
                stats.FlatArmorPenetration += tier * 5;
            }

            if (activeDefensivePotionId > 0 && activeDefensivePotionId <= ContentRegistry.ItemDefinitions.Length)
            {
                var defDef = ContentRegistry.ItemDefinitions[activeDefensivePotionId - 1];
                int tier = defDef.RegionTier;
                stats.MaxHp += tier * 100;
                stats.FlatPhysicalArmor += tier * 5;
                stats.DodgeChancePct += tier * 0.01f;
            }

            // Age penalties: 0=Child, 1=Adult, 2=Senior, 3=Old
            if (activeAgePhase == 2)
            {
                stats.FlatMeleeDamage = (int)(stats.FlatMeleeDamage * 0.9f);
                stats.FlatRangedDamage = (int)(stats.FlatRangedDamage * 0.9f);
                stats.MaxHp = (int)(stats.MaxHp * 0.9f);
                stats.AttackSpeedPct *= 0.9f;
            }
            else if (activeAgePhase == 3)
            {
                stats.FlatMeleeDamage = (int)(stats.FlatMeleeDamage * 0.8f);
                stats.FlatRangedDamage = (int)(stats.FlatRangedDamage * 0.8f);
                stats.MaxHp = (int)(stats.MaxHp * 0.8f);
                stats.AttackSpeedPct *= 0.8f;
            }

            return stats;
        }
    }
}
