using System;
using System.Text.Json.Nodes;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// A reference character's gear, described the way the boss ladder talks
    /// about it: all eight combat slots of one RegionTier, one QualityTier, and
    /// one affix rarity.
    ///
    /// Tools (slots 8 Axe, 9 Pickaxe, 10 Rod) are deliberately absent. There are
    /// eleven equipment slots and only the first eight carry combat stats.
    /// </summary>
    public readonly struct ReferenceLoadout
    {
        public readonly int Level;
        public readonly int GearRegionTier;
        public readonly int QualityTier;
        public readonly AffixRarity AffixRarity;

        public ReferenceLoadout(int level, int gearRegionTier, int qualityTier, AffixRarity affixRarity)
        {
            Level = level < 1 ? 1 : level;
            GearRegionTier = gearRegionTier < 1 ? 1 : gearRegionTier;
            QualityTier = Math.Clamp(qualityTier, 1, RarityTier.Transcendent);
            AffixRarity = affixRarity < AffixRarity.Common ? AffixRarity.Common : affixRarity;
        }
    }

    public readonly struct BossFightProjection
    {
        public readonly double SecondsToKillBoss;
        public readonly double SecondsToPlayerDeath;
        public readonly long BossMilliHp;
        public readonly long PlayerMilliHp;
        public readonly double PlayerMilliDps;
        public readonly double BossMilliDps;

        public BossFightProjection(
            double secondsToKillBoss,
            double secondsToPlayerDeath,
            long bossMilliHp,
            long playerMilliHp,
            double playerMilliDps,
            double bossMilliDps)
        {
            SecondsToKillBoss = secondsToKillBoss;
            SecondsToPlayerDeath = secondsToPlayerDeath;
            BossMilliHp = bossMilliHp;
            PlayerMilliHp = playerMilliHp;
            PlayerMilliDps = playerMilliDps;
            BossMilliDps = bossMilliDps;
        }

        /// <summary>
        /// The boss dies before the character does. A character who dies has
        /// lost outright, not merely slowly: the tick zeroes CurrentMonsterHp
        /// and ActiveActivityId on death, so the boss resets to full and the
        /// run is over.
        /// </summary>
        public bool PlayerWins => SecondsToKillBoss < SecondsToPlayerDeath;
    }

    /// <summary>
    /// What a given set of gear does against a given boss, projected rather than
    /// guessed.
    ///
    /// Modul: WHY THIS IS A TICK SIMULATION AND NOT A DPS COMPARISON.
    ///
    /// The auto-eat larder heals a SHARE of the health bar (12% per food tier,
    /// FoodRegistry.HealPercentOfMaxHpPerTier) on a cooldown of
    /// AutoEatCooldownTicks, and only once health has fallen to
    /// AutoEatThreshold. So sustain is not a flat healing rate that can be
    /// subtracted from incoming damage: what kills a character here is one large
    /// hit landing between two bites, which is exactly why region bosses sit at
    /// about 2.5x their region's regular attack power. Averaging that away would
    /// model a fight the game does not have.
    ///
    /// Everything it computes, it computes with the LIVE code:
    /// StatsCalculator.Calculate, CombatDamageModel.ExpectedMilliDamagePerSwing
    /// and .Mitigate, RarityTier.PowerMultiplier and .GetAffixCount,
    /// AffixRegistry.CalculateMagnitudeRange, ProgressionEngine
    /// .BaseMilliHpForLevel, FoodRegistry.GetHealMilliHp. A benchmark that
    /// disagrees with the tick is worse than no benchmark, and two derivations
    /// of one truth is this codebase's dominant bug class.
    ///
    /// The rolls are replaced by their MIDPOINTS rather than sampled, so the
    /// projection is deterministic and a test built on it cannot flake.
    /// </summary>
    public static class BossGearBenchmark
    {
        /// <summary>
        /// TWENTY LEVELS A REGION - the design's own pacing, and the same
        /// mapping ProgressionRateTests computes its bands from
        /// (region = (level - 1) / 20 + 1). A player standing at region N's boss
        /// has farmed that region to gear up for it, so they are at the TOP of
        /// its band.
        ///
        /// Region 5's top is level 100, which is where Malakor is actually
        /// fought - and it is why the wall has to hold at the level cap rather
        /// than at some comfortable mid-game figure. The live account that beat
        /// the region-4 boss and then went after Malakor in region-4 gear was
        /// level 88: inside region 5's band, one region behind on gear, which is
        /// exactly the case the wall must refuse.
        ///
        /// Calibrating every region at the cap instead would be far worse than
        /// wrong: the region-1 boss would need about four thousand times its
        /// authored attack to threaten a level-100 character, and would then
        /// one-shot every real new player who meets it at level 20.
        /// </summary>
        public const int LevelsPerRegion = 20;

        public static int ReferenceLevelForRegion(int region)
            => Math.Clamp(region, RaceUnlockRegistry.FirstRegion, RaceUnlockRegistry.LastRegion) * LevelsPerRegion;

        private const int TickMs = 100;

        /// <summary>
        /// Four hours. Long past the point where an idle player would conclude
        /// the fight is not happening, and long enough that a projection which
        /// reaches it is reporting a real stalemate rather than an impatient
        /// cap.
        /// </summary>
        private const int MaxTicks = 4 * 60 * 60 * 1000 / TickMs;

        public static BossFightProjection ProjectFirstClear(int bossMonsterId, in ReferenceLoadout gear)
            => Project(bossMonsterId, in gear, defeatedMask: 0, attackMultiplierOverride: 0.0);

        public static BossFightProjection ProjectCleared(int bossMonsterId, in ReferenceLoadout gear)
            => Project(bossMonsterId, in gear, BossFirstClearRules.MarkDefeated(0, bossMonsterId), attackMultiplierOverride: 0.0);

        /// <summary>
        /// The same projection with the wall's attack multiplier replaced, so the
        /// calibration can SEARCH for the multiplier that puts the break-even on
        /// the ladder instead of somebody picking a number and hoping. Nothing in
        /// production calls this.
        /// </summary>
        public static BossFightProjection ProjectFirstClearWithAttackMultiplier(
            int bossMonsterId, in ReferenceLoadout gear, double attackMultiplier)
            => Project(bossMonsterId, in gear, defeatedMask: 0, attackMultiplier);

        private static BossFightProjection Project(
            int bossMonsterId, in ReferenceLoadout gear, byte defeatedMask, double attackMultiplierOverride)
        {
            MonsterDefinition boss = ContentRegistry.Monsters[bossMonsterId - 1];

            CombatStats stats = BuildStats(in gear);
            long rawMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(
                in stats, damageScalePerLevelPct: 0, level: gear.Level);

            long playerMaxMilliHp = PlayerMaxMilliHp(in stats, gear.Level);

            // The wall, softened by First Blood at its MAXIMUM level: the bough
            // exists to reduce this penalty, so a wall that only holds against a
            // character who has not invested in it is not a wall.
            long bossMilliHp = BossFirstClearRules.MaxHpFor(
                defeatedMask, bossMonsterId,
                SkillTreeRegistry.MaxLevelOf(SkillTreeRegistry.BoughFirstBlood)) * 1000L;
            long bossAttackPower = attackMultiplierOverride > 0.0
                ? (long)(ContentRegistry.GetScaledMonsterAttackPower(bossMonsterId) * attackMultiplierOverride)
                : BossFirstClearRules.AttackPowerFor(defeatedMask, bossMonsterId);

            double playerMilliDamagePerSwing = CombatDamageModel.ExpectedMilliDamagePerSwing(
                in stats, in boss, rawMilliAttack, codexDamageMultiplier: 1f);
            int playerIntervalMs = CombatDamageModel.AttackIntervalMs(in stats);

            double bossMilliDamagePerSwing = ExpectedBossMilliDamagePerSwing(in stats, in boss, bossAttackPower);
            int bossIntervalMs = boss.AttackIntervalMs > 0 ? boss.AttackIntervalMs : 2000;

            // The best food the boss's own region offers, on the reasoning that
            // a player standing at a boss has the larder of the region they are
            // standing in. Sustain is the term that decides these fights, so it
            // is modelled generously: a wall that only holds against an unfed
            // character is the larder bug this game has already had once, where
            // "nothing could kill a player who owned fish" made a boss a check
            // on inventory rather than on equipment.
            //
            // Cooked food tiers run 1-10 over a contiguous id block and heal a
            // share of the bar per tier, so the region's tier is its food tier.
            int foodItemId = FoodRegistry.FirstCookedFoodItemId
                + Math.Clamp(boss.RegionTier, 1, FoodRegistry.TierCount) - 1;
            int healPerBite = FoodRegistry.GetHealMilliHp(foodItemId, playerMaxMilliHp);

            double playerMilliHp = playerMaxMilliHp;
            double remainingBossMilliHp = bossMilliHp;
            double eatThreshold = playerMaxMilliHp * (Shared.AutoEatDefaults.ThresholdPct / 100.0);

            int playerSwingAccumulator = 0;
            int bossSwingAccumulator = 0;
            int eatCooldownTicks = 0;

            double secondsToKill = double.PositiveInfinity;
            double secondsToDeath = double.PositiveInfinity;

            for (int tick = 1; tick <= MaxTicks; tick++)
            {
                playerSwingAccumulator += TickMs;
                if (playerSwingAccumulator >= playerIntervalMs)
                {
                    playerSwingAccumulator -= playerIntervalMs;
                    remainingBossMilliHp -= playerMilliDamagePerSwing;

                    // Lifesteal heals on the damage DEALT, as the tick does.
                    if (stats.LifestealPct > 0f)
                    {
                        playerMilliHp = Math.Min(
                            playerMaxMilliHp,
                            playerMilliHp + (playerMilliDamagePerSwing * (stats.LifestealPct / 100f)));
                    }
                }

                bossSwingAccumulator += TickMs;
                if (bossSwingAccumulator >= bossIntervalMs)
                {
                    bossSwingAccumulator -= bossIntervalMs;
                    playerMilliHp -= bossMilliDamagePerSwing;
                }

                if (remainingBossMilliHp <= 0)
                {
                    secondsToKill = tick * TickMs / 1000.0;
                    break;
                }

                if (playerMilliHp <= 0)
                {
                    secondsToDeath = tick * TickMs / 1000.0;
                    break;
                }

                // Auto-eat, in the tick's own order: the cooldown first, then
                // the threshold test.
                if (eatCooldownTicks > 0)
                {
                    eatCooldownTicks--;
                }
                else if (playerMilliHp <= eatThreshold && healPerBite > 0)
                {
                    playerMilliHp = Math.Min(playerMaxMilliHp, playerMilliHp + healPerBite);
                    eatCooldownTicks = SimulationEngine.AutoEatCooldownTicks;
                }
            }

            double playerDps = playerMilliDamagePerSwing * (1000.0 / playerIntervalMs);
            double bossDps = bossMilliDamagePerSwing * (1000.0 / bossIntervalMs);

            return new BossFightProjection(
                secondsToKill, secondsToDeath, bossMilliHp, playerMaxMilliHp, playerDps, bossDps);
        }

        /// <summary>
        /// One boss swing against this character, averaged over its hit roll and
        /// its crit roll, in the live tick's order: dodge, then the monster crit,
        /// then armour, then block, then the 1000 milli-HP floor.
        /// </summary>
        private static double ExpectedBossMilliDamagePerSwing(in CombatStats stats, in MonsterDefinition boss, long bossAttackPower)
        {
            float hitChance = Math.Clamp(100f / (100f + stats.DodgeChancePct), 0.05f, 0.95f);

            float critChance = 0.05f + (boss.RegionTier * 0.005f);
            float critMultiplier = Math.Max(1.0f, 1.5f - (stats.CritMitigationPct / 100f));

            double normal = AfterPlayerDefences(bossAttackPower, 1.0f, in stats, in boss);
            double crit = AfterPlayerDefences(bossAttackPower, critMultiplier, in stats, in boss);

            return hitChance * (((1.0 - critChance) * normal) + (critChance * crit));
        }

        private static double AfterPlayerDefences(long bossAttackPower, float critMultiplier, in CombatStats stats, in MonsterDefinition boss)
        {
            long raw = (long)(bossAttackPower * 1000L * critMultiplier);

            long afterArmour = CombatDamageModel.Mitigate(
                raw, stats.FlatPhysicalArmor, CombatDamageModel.PlayerArmourHalvingConstant(boss.RegionTier));

            float blockFraction = Math.Clamp(stats.BlockStrengthPct / 100f, 0f, 0.75f);
            double afterBlock = afterArmour * (1f - blockFraction);

            return Math.Max(1000.0, afterBlock);
        }

        /// <summary>
        /// The reference character's stats: attribute points placed the way the
        /// game's own growth table deals them, and eight pieces of gear.
        ///
        /// No potions, no set bonus, no inheritance, no breeding aptitude and no
        /// racial mastery. Those are all additive on top, so a real character who
        /// has them finds the boss EASIER than this projection - the floor errs
        /// toward letting an invested player through, never toward walling one
        /// out.
        /// </summary>
        private static CombatStats BuildStats(in ReferenceLoadout gear)
        {
            RaceAttributeGrowth.GetGrowthPerLevel(RaceIds.Human, out int str, out int dex, out int con, out int lck);
            int levels = gear.Level - 1;

            EquippedAffixTotals totals = BuildEquippedTotals(in gear);

            return StatsCalculator.Calculate(
                str * levels, dex * levels, con * levels, lck * levels,
                activeOffensivePotionId: 0, activeDefensivePotionId: 0,
                activeAgePhase: 1, completedAreaFlags: 0, activeRaceId: RaceIds.Human,
                humanMastery: 0, vilaMastery: 0, draugrMastery: 0,
                equippedAffixTotals: totals);
        }

        /// <summary>
        /// Eight slots, each with the base stats of a real item of that region
        /// tier scaled by its quality tier, and the affixes that quality tier
        /// grants - folded in by the SAME function the equip path uses, so a
        /// change to how an affix maps onto a stat cannot leave this behind.
        /// </summary>
        private static EquippedAffixTotals BuildEquippedTotals(in ReferenceLoadout gear)
        {
            EquippedAffixTotals totals = default;
            double qualityMultiplier = RarityTier.PowerMultiplier(gear.QualityTier);
            int affixCount = RarityTier.GetAffixCount(gear.QualityTier);

            foreach (EquipmentSlotKind slot in ReferenceSlots)
            {
                if (!TryFindItem(gear.GearRegionTier, slot, out ItemDefinition definition))
                {
                    continue;
                }

                totals.FlatAttack += (int)Math.Round(definition.FlatAttackPower * qualityMultiplier);
                totals.FlatDefense += (int)Math.Round(definition.FlatDefenseRating * qualityMultiplier);

                EquipmentSlotEngine.AddAffixTotals(
                    BuildAffixPayload(slot, gear.GearRegionTier, affixCount, gear.AffixRarity),
                    ref totals);
            }

            return totals;
        }

        /// <summary>
        /// A real catalogue item of this region tier for this slot - the
        /// strongest one, so the projection is not decided by which of a
        /// region's several chestpieces happens to come first in the file.
        ///
        /// The slot is asked of AffixRegistry.ResolveSlot, which reads the
        /// BaseItemId's own suffix, rather than inferred from the item's name or
        /// position: item ids in this catalogue are positional and have been
        /// mis-read that way before.
        /// </summary>
        private static bool TryFindItem(int regionTier, EquipmentSlotKind slot, out ItemDefinition found)
        {
            found = default;
            bool any = false;
            int bestPower = -1;

            ReadOnlySpan<ItemDefinition> items = ContentRegistry.ItemDefinitions;
            for (int i = 0; i < items.Length; i++)
            {
                ItemDefinition candidate = items[i];
                if (candidate.RegionTier != regionTier) continue;

                string baseId = ContentRegistry.GetItemBaseId(candidate.Id);
                if (AffixRegistry.ResolveSlot(baseId) != slot) continue;

                int power = candidate.FlatAttackPower + candidate.FlatDefenseRating;
                if (power > bestPower)
                {
                    bestPower = power;
                    found = candidate;
                    any = true;
                }
            }

            return any;
        }

        private static readonly EquipmentSlotKind[] ReferenceSlots =
        {
            EquipmentSlotKind.Weapon,
            EquipmentSlotKind.Helmet,
            EquipmentSlotKind.Chest,
            EquipmentSlotKind.Leggings,
            EquipmentSlotKind.Boots,
            EquipmentSlotKind.Gloves,
            EquipmentSlotKind.Amulet,
            EquipmentSlotKind.Ring
        };

        /// <summary>
        /// The affixes a representative - not optimal - piece carries: the
        /// slot's own legal pool, taken round-robin, each at the MIDPOINT of its
        /// magnitude range for this region tier and affix rarity.
        ///
        /// Round-robin rather than "the best ones" deliberately. An optimised
        /// build is not what a ladder should be calibrated against: tune the wall
        /// to beat a perfect build and every ordinary one is locked out, which is
        /// the failure mode this whole exercise is correcting in the other
        /// direction.
        /// </summary>
        private static string BuildAffixPayload(EquipmentSlotKind slot, int regionTier, int affixCount, AffixRarity rarity)
        {
            Span<int> legal = stackalloc int[16];
            int legalCount = AffixRegistry.GetLegalAffixIndices(slot, legal);
            if (legalCount == 0) return "{}";

            var payload = new JsonObject();
            for (int i = 0; i < affixCount; i++)
            {
                AffixDefinition definition = AffixRegistry.Definitions[legal[i % legalCount]];
                (int min, int max) = AffixRegistry.CalculateMagnitudeRange(definition, regionTier, rarity);
                int magnitude = min + ((max - min) / 2);

                int stackIndex = (i / legalCount) + 1;
                payload[AffixRegistry.BuildPayloadKey(definition.Id, stackIndex, rarity)] = magnitude;
            }

            return payload.ToJsonString();
        }

        /// <summary>
        /// The health bar, built from the same terms the tick builds it from,
        /// minus every optional layer (inheritance, Fortitude, Endurance) for the
        /// reason BuildStats leaves out potions.
        /// </summary>
        private static long PlayerMaxMilliHp(in CombatStats stats, int level)
        {
            long baseMilliHp = ProgressionEngine.BaseMilliHpForLevel(level);
            return baseMilliHp + (stats.MaxHp * 1000L);
        }
    }
}
