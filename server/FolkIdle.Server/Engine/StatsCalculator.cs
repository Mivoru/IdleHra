using System.Runtime.InteropServices;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

        /// <summary>
        /// The chance, in percent, that a dropped piece of equipment comes out
        /// ONE rarity tier above what it rolled.
        ///
        /// Modul: FORTUNE'S SECOND EFFECT, 2026-09-06 - replacing forge success,
        /// which was neither.
        ///
        /// Fusion is guaranteed (three of a rarity make one of the next, with no
        /// roll), so `ForgeSuccessPct` had been quietly repurposed into a gold
        /// DISCOUNT on the fusion fee, capped at 25%, under a name that promises
        /// a success chance. A discount on the abundant currency is a weak
        /// reward and a misleading one.
        ///
        /// Elevation is the opposite on both counts: it acts on the scarce thing
        /// (rarity), and the player SEES it - the piece arrives a tier better
        /// than it should have. Loot luck reweights the roll silently; this
        /// bumps the result visibly, which is why the two read as different
        /// stats rather than one stat twice.
        /// </summary>
        public float RarityElevationPct { get; set; }
        public float LootLuckPct { get; set; }
        public float DodgeChancePct { get; set; }
        public float LifestealPct { get; set; }

        // Additive accuracy score (DEX-derived) - hit chance against a
        // monster is (100 + AccuracyRating) / (100 + monster.DodgeRating),
        // so 0 reproduces the old fixed-midpoint hit chance exactly.
        public int AccuracyRating { get; set; }

        // Percent (0-100 scale, matching CritChancePct's convention) chance
        // to reduce an incoming hit's damage multiplicatively (CON-derived
        // - bulk/endurance shrugging off a blow). Clamped at the point of
        // use, never here, so a single high-CON outlier cannot be assumed
        // safe by every caller.
        public float BlockStrengthPct { get; set; }

        // Modul 13.4.3: innate racial baseline passives.
        public float GoldAcquisitionMultiplierPct { get; set; }
        public float MiningOreDuplicationBonusPct { get; set; }
        public float WoodcuttingYieldBonusPct { get; set; }
        public float CritMitigationPct { get; set; }

        // Modul: Affix System Unification. The two GDD weapon affixes that had
        // no home in CombatStats at all: melee/range/magic_dmg_pct summed into
        // one multiplier (this combat model has a single damage number, not
        // per-type resistances) and crit_dmg_pct, which raises the crit
        // multiplier above its 1.5 baseline.
        public float EquipmentDamagePct { get; set; }
        public float EquipmentCritDamagePct { get; set; }

        // Modul: Architecture Overhaul, Part 4. Equipment set bonuses -
        // see SetBonusEngine.
        //
        // Modul: set bonuses made real. Four of these are now consumed by the
        // live combat tick: FireDamageMultiplierPct and BurnApplicationActive
        // in the outgoing damage step, ThornsReflectionActive in the incoming
        // one, and CooldownReductionActive at the skill-cast site.
        //
        // Modul: set effect rework. The fifth was SetCcImmunityActive, which
        // could never do anything - this game has no player-facing crowd
        // control, so there was nothing to be immune to. It is now
        // SetDamageCapActive, consumed in the same incoming-damage step, and
        // all five 4-piece effects do something.
        public float SetFireDamageMultiplierPct { get; set; }
        public bool SetThornsReflectionActive { get; set; }
        // Modul: cleanup. SetCooldownReductionActive was removed from here.
        // The cooldown reduction is applied at the skill-cast site, which is a
        // command handler with no CombatStats in scope - it reads the flag
        // straight off SetBonusEngine.Evaluate(payload.CachedSetIds) instead.
        // Mirroring it onto CombatStats as well left a property with no
        // consumer, which invites a second, divergent code path.
        public bool SetBurnApplicationActive { get; set; }
        public bool SetDamageCapActive { get; set; }
    }

    public static class StatsCalculator
    {
        public static CombatStats Calculate(int str, int dex, int con, int lck, int activeOffensivePotionId = 0, int activeDefensivePotionId = 0, int activeAgePhase = 1, int completedAreaFlags = 0, int activeRaceId = 0, int humanMastery = 0, int vilaMastery = 0, int draugrMastery = 0, EquippedAffixTotals equippedAffixTotals = default, bool isEpicMutation = false, int locusSpeed = 0, int locusCrit = 0, EquippedSetIds equippedSetIds = default)
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

            // Modul: THE FOUR ATTRIBUTES, REWORKED 2026-09-06 - see
            // AttributeRegistry for the identities, the curves and the milestone
            // tracks, and for what was dead before it.
            //
            // MIGHT: the blow, and getting it through armour. Both linear -
            // they race content that grows geometrically, so a curve here would
            // make them stop mattering rather than stop running away. Armour
            // penetration is a REAL stat now: CombatDamageModel.Mitigate takes
            // it, where before nothing did and every point of it was wasted.
            stats.FlatMeleeDamage = str * 2;
            stats.FlatArmorPenetration = str * 1;

            // FINESSE: landing the blow, and landing it well. No damage term at
            // all any more - that was `FlatRangedDamage`, which nothing in
            // combat has ever read, and which made Finesse a strictly better
            // Might with three bonuses attached. Accuracy stays linear because
            // it is priced against monster dodge, which rises per region;
            // crit chance and attack speed are curved, because at 0.1% and
            // 0.05% a point a long-played character reached +59% and +29% from
            // one attribute.
            stats.AttackSpeedPct = AttributeRegistry.DiminishedPercent(
                AttributeRegistry.AttackSpeedPerRootPoint, dex);
            stats.CritChancePct = AttributeRegistry.DiminishedPercent(
                AttributeRegistry.CritChancePerRootPoint, dex);
            stats.AccuracyRating = dex * 1;

            // VIGOUR: the bar and what reaches it. Health and armour linear for
            // the same reason as Might; block is a percentage and curved.
            stats.MaxHp = con * 15;
            stats.FlatPhysicalArmor = con * 1;
            stats.OutOfCombatHpRegen = con * 0.1f;
            stats.BlockStrengthPct = AttributeRegistry.DiminishedPercent(
                AttributeRegistry.BlockStrengthPerRootPoint, con);

            // FORTUNE: what the world gives back. Both percentages, both curved.
            // Its milestone track is the half that reaches outside a fight -
            // gathering yield and gold - which is what stops it being the dump
            // stat it has always been.
            stats.LootLuckPct = AttributeRegistry.DiminishedPercent(
                AttributeRegistry.LootLuckPerRootPoint, lck);
            stats.RarityElevationPct = AttributeRegistry.DiminishedPercent(
                AttributeRegistry.RarityElevationPerRootPoint, lck);
            // Modul: Fortune no longer grants forge success PER POINT. Fusion
            // cannot fail, so that stat was only ever a fee discount - it
            // survives as the capstone milestone, where a discrete perk is an
            // honest shape for it, instead of a trickle under a wrong name.
            stats.ForgeSuccessPct = 0f;
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
            // Modul: Affix System Unification. All twelve GDD affixes now land
            // on a real stat. Percentage totals arrive in tenths of a percent
            // (see EquippedAffixTotals) so each is divided by 10 here, which is
            // the single place that conversion happens.
            stats.FlatMeleeDamage += equippedAffixTotals.FlatAttack;
            stats.FlatRangedDamage += equippedAffixTotals.FlatAttack;
            stats.FlatPhysicalArmor += equippedAffixTotals.FlatDefense;
            stats.FlatArmorPenetration += equippedAffixTotals.FlatArmorPenetration;
            stats.MaxHp += equippedAffixTotals.FlatHp;
            stats.CritChancePct += equippedAffixTotals.CritChanceTenthsPct / 10f;
            stats.LootLuckPct += equippedAffixTotals.LootLuckTenthsPct / 10f;
            stats.AttackSpeedPct += equippedAffixTotals.AttackSpeedTenthsPct / 10f;
            stats.LifestealPct += equippedAffixTotals.LifestealTenthsPct / 10f;
            stats.DodgeChancePct += equippedAffixTotals.DodgeTenthsPct / 10f;
            stats.BlockStrengthPct += equippedAffixTotals.BlockTenthsPct / 10f;
            stats.EquipmentDamagePct += equippedAffixTotals.DamageTenthsPct / 10f;
            stats.EquipmentCritDamagePct += equippedAffixTotals.CritDamageTenthsPct / 10f;

            // Modul: THE MILESTONE TRACKS, applied AFTER gear on purpose - the
            // percentage rungs are meant to act on what the character actually
            // has. A +5% health rung landing before the affixes would be worth a
            // twentieth of what it reads on the card.
            //
            // Every effect routes into a field the live tick already reads. That
            // is the constraint AttributeRegistry's table was written under,
            // because a milestone list inventing new mechanics would be twenty
            // fresh chances at this codebase's most expensive recurring defect.
            ApplyAttributeMilestones(ref stats, str, dex, con, lck);

            // Modul 13.4.3: inherited genetic loci (see GeneticSplicingEngine/
            // BreedingEngine). LocusCrit scales Crit Chance directly; LocusSpeed
            // reduces the effective attack interval by adding to AttackSpeedPct
            // (a higher AttackSpeedPct shortens the interval between attacks in
            // the combat tick loop). Same additive block as equipped gear, before
            // the age-phase falloff below.
            stats.CritChancePct += locusCrit * 0.05f;
            stats.AttackSpeedPct += locusSpeed * 0.05f;

            // Modul: Architecture Overhaul, Part 4. Equipment set bonuses -
            // applied after individual-item affix totals (equippedFlatAttack
            // etc. above) but before the age-phase falloff below, so set
            // bonuses are subject to the same age scaling as every other
            // external stat source, matching equipped gear's own placement.
            // Modul: seven-slot set bonuses. Was a 3-element span built from
            // weapon/armour/leggings, which meant SetBonusEngine - whose whole
            // job is counting how many worn pieces share a set - could never
            // count past 3 and no 4-piece tier was reachable. Still stackalloc,
            // still zero allocation on the 10Hz path.
            Span<int> setIdSpan = stackalloc int[EquippedSetIds.SlotCount];
            equippedSetIds.CopyTo(setIdSpan);
            SetBonusEngine.SetBonusResult setBonus = SetBonusEngine.Evaluate(setIdSpan);
            stats.FlatMeleeDamage += setBonus.FlatAttackPowerBonus;
            stats.FlatRangedDamage += setBonus.FlatAttackPowerBonus;
            if (setBonus.TotalArmorMultiplierPct > 0f)
            {
                stats.FlatPhysicalArmor = (int)(stats.FlatPhysicalArmor * (1f + setBonus.TotalArmorMultiplierPct / 100f));
            }
            stats.SetFireDamageMultiplierPct = setBonus.FireDamageMultiplierPct;
            stats.SetThornsReflectionActive = setBonus.ThornsReflectionActive;
            stats.SetBurnApplicationActive = setBonus.BurnApplicationActive;
            stats.SetDamageCapActive = setBonus.DamageCapActive;

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
        /// <param name="inheritDamagePct">
        /// The player's permanent inheritance bonus, in whole percent. Optional
        /// and defaulted so the guild-war snapshot paths, which model a
        /// defender rather than a live player, keep their existing calls - a
        /// defender's inheritance is not part of what a raid reads.
        /// </param>
        public static long ComputeEffectiveMilliAttack(in CombatStats stats, int damageScalePerLevelPct, int level, int inheritDamagePct = 0)
        {
            long flatMilliAttack = BaseMilliAttack + (BaseMilliAttack * damageScalePerLevelPct * level / 100) + (stats.FlatMeleeDamage * 1000L);

            // Modul: Affix System Unification. The GDD's melee/range/magic
            // damage percentage affixes multiply total attack, applied here
            // rather than at each of the five combat call sites so weapon
            // affixes cannot be silently skipped by one of them.
            long withAffixes = stats.EquipmentDamagePct <= 0f
                ? flatMilliAttack
                : flatMilliAttack + (long)(flatMilliAttack * (stats.EquipmentDamagePct / 100f));

            // Modul: inheritance. Applied last and multiplicatively, so it
            // scales everything the player has built this season rather than
            // adding a flat amount that stops mattering by region 3.
            if (inheritDamagePct <= 0) return withAffixes;
            return withAffixes + (withAffixes * inheritDamagePct / 100L);
        }

        // Modul: Affix System Unification. The player's crit multiplier, 1.5
        // baseline plus whatever crit_dmg_pct the equipped weapon rolled. A
        // single accessor so the live combat tick and the offline/warp
        // projections cannot drift apart on crit maths.
        /// <summary>Adds every milestone the four attribute values have reached.</summary>
        private static void ApplyAttributeMilestones(ref CombatStats stats, int str, int dex, int con, int lck)
        {
            var milestones = AttributeRegistry.Milestones;
            for (int i = 0; i < milestones.Length; i++)
            {
                var milestone = milestones[i];

                int value = milestone.Attribute switch
                {
                    AttributeRegistry.Might => str,
                    AttributeRegistry.Finesse => dex,
                    AttributeRegistry.Vigour => con,
                    _ => lck,
                };

                if (value < milestone.Threshold) continue;

                switch (milestone.Effect)
                {
                    case AttributeRegistry.MilestoneEffect.AttackPowerPct:
                        stats.EquipmentDamagePct += milestone.Magnitude; break;
                    case AttributeRegistry.MilestoneEffect.ArmourPenetrationFlat:
                        stats.FlatArmorPenetration += (int)milestone.Magnitude; break;
                    case AttributeRegistry.MilestoneEffect.AttackSpeedPct:
                        stats.AttackSpeedPct += milestone.Magnitude; break;
                    case AttributeRegistry.MilestoneEffect.AccuracyFlat:
                        stats.AccuracyRating += (int)milestone.Magnitude; break;
                    case AttributeRegistry.MilestoneEffect.CritDamagePct:
                        stats.EquipmentCritDamagePct += milestone.Magnitude; break;
                    case AttributeRegistry.MilestoneEffect.CritChancePct:
                        stats.CritChancePct += milestone.Magnitude; break;
                    case AttributeRegistry.MilestoneEffect.MaxHpPct:
                        stats.MaxHp = (int)(stats.MaxHp * (1f + milestone.Magnitude / 100f)); break;
                    case AttributeRegistry.MilestoneEffect.ArmourPct:
                        stats.FlatPhysicalArmor = (int)(stats.FlatPhysicalArmor * (1f + milestone.Magnitude / 100f)); break;
                    case AttributeRegistry.MilestoneEffect.RegenPerSecond:
                        stats.OutOfCombatHpRegen += milestone.Magnitude; break;
                    case AttributeRegistry.MilestoneEffect.CritMitigationPct:
                        stats.CritMitigationPct += milestone.Magnitude; break;
                    case AttributeRegistry.MilestoneEffect.LootLuckPct:
                        stats.LootLuckPct += milestone.Magnitude; break;
                    case AttributeRegistry.MilestoneEffect.GatheringYieldPct:
                        // Both branches, so a Prospector is not secretly a miner -
                        // SimulationEngine picks one of these two by profession.
                        stats.WoodcuttingYieldBonusPct += milestone.Magnitude;
                        stats.MiningOreDuplicationBonusPct += milestone.Magnitude;
                        break;
                    case AttributeRegistry.MilestoneEffect.GoldPct:
                        stats.GoldAcquisitionMultiplierPct += milestone.Magnitude; break;
                    default:
                        stats.ForgeSuccessPct += milestone.Magnitude; break;
                }
            }
        }

        public static float ComputeCritMultiplier(in CombatStats stats)
        {
            return 1.5f + (stats.EquipmentCritDamagePct / 100f);
        }
    }
}
