using System.Runtime.InteropServices;

namespace FolkIdle.Server.Engine
{
    // Modul: fields (not auto-properties) would silently break
    // System.Text.Json round-tripping - JsonSerializer only serializes
    // properties by default, so a plain-field struct serializes to "{}" and
    // deserializes back to all-zero with no error at either end. This struct
    // is round-tripped through GuildWarDefensiveSnapshots.RosterPayloadJson
    // (GuildWarSnapshotEngine writes it, GuildWarEngine/
    // GuildCombatSimulationEngine read it), so it must stay properties.
    // StructLayout is not load-bearing here - CombatStats never crosses the
    // network boundary directly (only StateUpdatePacket/ClientCommandPacket/
    // AuthHandshakePacket do, see NetworkPacketLayoutGuard), it is only ever
    // JSON-serialized into a DB text column.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CombatStats
    {
        public int FlatMeleeDamage { get; set; }
        public int FlatRangedDamage { get; set; }
        public int FlatArmorPenetration { get; set; }
        public int FlatPhysicalArmor { get; set; }
        public int MaxHp { get; set; }
        public float AttackSpeedPct { get; set; }
        public float CritChancePct { get; set; }
        public float OutOfCombatHpRegen { get; set; }
        public float ForgeSuccessPct { get; set; }
        public float LootLuckPct { get; set; }
        public float DodgeChancePct { get; set; }
        public float LifestealPct { get; set; }

        // Modul 13.4.3: innate racial baseline passives.
        public float GoldAcquisitionMultiplierPct { get; set; }
        public float MiningOreDuplicationBonusPct { get; set; }
        public float WoodcuttingYieldBonusPct { get; set; }
        public float CritMitigationPct { get; set; }
    }

    public static class StatsCalculator
    {
        public static CombatStats Calculate(int str, int dex, int con, int lck, int activeOffensivePotionId = 0, int activeDefensivePotionId = 0, int activeAgePhase = 1, int completedAreaFlags = 0, int activeRaceId = 0, int humanMastery = 0, int vilaMastery = 0, int draugrMastery = 0, int equippedFlatAttack = 0, int equippedFlatDefense = 0, int equippedCritBonus = 0, int equippedLuckBonus = 0, bool isEpicMutation = false, int locusSpeed = 0, int locusCrit = 0)
        {
            var stats = new CombatStats();

            // Modul 13.4.3: an Epic-mutated lineage (see BreedingEngine's grand
            // mutation roll) grants a flat +5% to all base attributes, applied to
            // the raw inputs before any derived stat below is computed so every
            // downstream formula benefits proportionally.
            if (isEpicMutation)
            {
                str = (int)(str * 1.05f);
                dex = (int)(dex * 1.05f);
                con = (int)(con * 1.05f);
                lck = (int)(lck * 1.05f);
            }

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

            // Modul 13.4.3: innate, always-on racial baseline passives - distinct
            // from the mastery-level-scaled RaceMasteryResolver bonuses above,
            // which only unlock/scale as a player kills that race's monsters.
            // These apply unconditionally to every character of that race
            // regardless of mastery progress. Placed before equipped gear/age
            // falloff below, so only the base+potion stats are scaled - gear and
            // age apply on top afterward, not multiplied again by race.
            switch (activeRaceId)
            {
                case RaceIds.Human:
                    // Jack-of-all-trades: no combat penalty, +5% Gold acquisition.
                    stats.GoldAcquisitionMultiplierPct += 5f;
                    break;
                case RaceIds.Vila:
                    // Agility master: +20% Flat Ranged Damage, +10% Dodge Chance
                    // (absolute), -30% Base Armor (multiplicative).
                    stats.FlatRangedDamage = (int)(stats.FlatRangedDamage * 1.2f);
                    stats.DodgeChancePct += 10f;
                    stats.FlatPhysicalArmor = (int)(stats.FlatPhysicalArmor * 0.7f);
                    break;
                case RaceIds.Draugr:
                    // Undead juggernaut: +25% Max HP, +15% Base Armor, -15%
                    // Attack Speed (absolute).
                    stats.MaxHp = (int)(stats.MaxHp * 1.25f);
                    stats.FlatPhysicalArmor = (int)(stats.FlatPhysicalArmor * 1.15f);
                    stats.AttackSpeedPct -= 0.15f;
                    break;
                case RaceIds.Kobold:
                    // Subterranean miner: +30% Mining Ore duplication chance.
                    // The GDD's paired -20% non-ore inventory cap penalty is not
                    // applied here - there is no per-item-category inventory
                    // tracking anywhere in this codebase; InventorySpaceRemaining
                    // is a single flat counter with no ore/non-ore distinction to
                    // lock down, and building one is out of scope for this pass.
                    stats.MiningOreDuplicationBonusPct += 30f;
                    break;
                case RaceIds.Moosleute:
                    // Nature warden: +20% Woodcutting harvest yield. No dedicated
                    // Herbalism profession exists in this codebase - see the
                    // existing Moosleute-double-harvest-mastery-bonus precedent in
                    // SimulationEngine's gathering block, which applies to
                    // Woodcutting for the same reason.
                    stats.WoodcuttingYieldBonusPct += 20f;
                    break;
                case RaceIds.Vodnik:
                    // River guardian: +15% Health Regen efficiency, +10%
                    // Critical Strike mitigation (absolute). CritMitigationPct is
                    // computed here but not yet consumed anywhere - monsters
                    // currently deal fixed, non-crit damage (no incoming-crit
                    // roll exists in the combat tick to mitigate against),
                    // matching the existing "cached but not yet consumed"
                    // precedent (e.g. LocusYield before this task).
                    stats.OutOfCombatHpRegen *= 1.15f;
                    stats.CritMitigationPct += 10f;
                    break;
            }

            // Modul 16/21: equipped gear (weapon + armor combined, pre-summed by
            // EquipmentSlotEngine and cached in TickStatePayload - no JSON/DB
            // access here). Applied additively alongside potions, before the age
            // penalty scaling below, so equipped bonuses are subject to the same
            // age-phase falloff as every other external stat source.
            stats.FlatMeleeDamage += equippedFlatAttack;
            stats.FlatRangedDamage += equippedFlatAttack;
            stats.FlatPhysicalArmor += equippedFlatDefense;
            stats.CritChancePct += equippedCritBonus;
            stats.LootLuckPct += equippedLuckBonus;

            // Modul 13.4.3: inherited genetic loci (see GeneticSplicingEngine/
            // BreedingEngine). LocusCrit scales Crit Chance directly; LocusSpeed
            // reduces the effective attack interval by adding to AttackSpeedPct
            // (a higher AttackSpeedPct shortens the interval between attacks in
            // the combat tick loop). Same additive block as equipped gear, before
            // the age-phase falloff below.
            stats.CritChancePct += locusCrit * 0.05f;
            stats.AttackSpeedPct += locusSpeed * 0.05f;

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

        public const long BaseMilliAttack = 15000L;

        // Modul: single shared definition of "effective milli-attack" -
        // previously duplicated identically in SimulationEngine.ProcessSubTick
        // and OfflineSimulationEngine.CalculateCombatProjection (live/offline
        // PvE), and as a simplified copy missing the level-scaling term
        // entirely in GuildWarEngine.ResolveCombatPhaseAsync (PvP) - the exact
        // PVP/PVE math desync this collapses into one formula. For
        // guild-vs-guild aggregate combat, pass damageScalePerLevelPct=0 and
        // level=0: GuildWarSnapshotEngine already bakes each contributing
        // member's own level-scaled attack into the aggregated
        // FlatMeleeDamage at snapshot-build time, so applying level scaling a
        // second time here would double-count it.
        public static long ComputeEffectiveMilliAttack(in CombatStats stats, int damageScalePerLevelPct, int level)
        {
            return BaseMilliAttack + (BaseMilliAttack * damageScalePerLevelPct * level / 100) + (stats.FlatMeleeDamage * 1000L);
        }
    }
}
