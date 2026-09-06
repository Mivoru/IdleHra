using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public static class OfflineSimulationEngine
    {
        // Hard cap on analytically-projected offline time: Modul 11 formula
        // T_elapsed = Math.Min(43200, T_current - T_last_checkpoint).
        // Modul: public because OfflineCapNotifier mails players when they
        // reach it, and a second copy of "twelve hours" living in the notifier
        // is exactly the drift that made offline loot worse than online loot.
        public const long MaxOfflineSeconds = 43200L;

        private readonly struct LootProjection
        {
            public readonly bool IsValid;
            public readonly int LootTableId;
            public readonly int LootRolls;
            public readonly int EquipmentDropsGranted;
            public readonly float LootLuckPct;

            public LootProjection(bool isValid, int lootTableId, int lootRolls, int equipmentDropsGranted = 0, float lootLuckPct = 0f)
            {
                IsValid = isValid;
                LootTableId = lootTableId;
                LootRolls = lootRolls;
                EquipmentDropsGranted = equipmentDropsGranted;
                LootLuckPct = lootLuckPct;
            }
        }

        // Modul: THE LIVE TICK STOPPED HEALING A FLAT 50 HP AND THIS DID NOT.
        //
        // This constant was written when SimulationEngine's Auto-Eat block also
        // healed 50000 milli-HP per unit regardless of the food. That block now
        // asks FoodRegistry, which pays 40 HP for a tier-1 minnow and 82,000 for
        // a tier-10 Astral Ambrosia Roast - a factor of two thousand.
        //
        // The constant stayed, so the offline projection sized a night's food
        // demand as though every fish in the larder were worth 50 HP. At the
        // bottom of the game that OVERPAYS by 25%, and at the top it underpays
        // by 1,640x: a player who logged off with high-tier food banked was
        // told their larder ran dry in minutes and lost the rest of the window.
        //
        // The heal now comes from the same registry the live tick reads, per
        // stocked slot. Same class of defect as the three damage models, in the
        // same file, found the same way - by measuring rather than by reading.
        private static double AverageHealPerFoodUnitMilliHp(in TickStatePayload payload, long effectiveMaxMilliHp)
        {
            long units = payload.Food1_Count + payload.Food2_Count + payload.Food3_Count;
            if (units <= 0)
            {
                return 0.0;
            }

            // Weighted by how much of each food is actually stocked, because
            // auto-eat drains the highest-healing slot first but ends up
            // consuming all of it - over a full offline window the average is
            // what decides how long the larder lasts.
            double total =
                (double)payload.Food1_Count * FoodRegistry.GetHealMilliHp(payload.Food1_ItemId, effectiveMaxMilliHp) +
                (double)payload.Food2_Count * FoodRegistry.GetHealMilliHp(payload.Food2_ItemId, effectiveMaxMilliHp) +
                (double)payload.Food3_Count * FoodRegistry.GetHealMilliHp(payload.Food3_ItemId, effectiveMaxMilliHp);

            return total / units;
        }

        // Modul: THE BACKPACK CAPPED OFFLINE PROGRESS AT TWENTY.
        //
        // Every bound in this file was `payload.InventorySpaceRemaining`. With
        // a 20 slot backpack that meant a night away produced at most twenty
        // gathers and twenty kills' worth of drops - the rest of the elapsed
        // time was pushed into the chrono bank as "overflow", which is why the
        // Time Warp screen always had hours banked and the welcome-back card
        // always showed almost nothing. Storage is now one unlimited village
        // chest, so the real bound is what a single login may safely enqueue
        // and write, not what a character could carry.
        //
        // Gathering loot is granted analytically (one aggregated row per item)
        // so its ceiling only needs to stop absurd arithmetic. Equipment drops
        // enqueue one CombatLootEngine request each, so theirs is much tighter
        // and is per slot.
        private const int MaxOfflineGatherActions = 200_000;
        private const int MaxOfflineLootRolls = 200_000;
        // Modul: a RUNAWAY GUARD, not a balance cap. It replaces
        // MaxOfflineEquipmentDropsPerSlot (500), which WAS a balance cap by
        // accident and cost players most of their offline gear.
        //
        // The offline window is capped at twelve hours, so even a one-second
        // kill cannot reach this number; it exists so that a corrupt kill-time
        // estimate cannot ask the loot engine for an unbounded loop.
        private const long MaxOfflineKillsPerSlot = 200_000L;

        private static bool SlotHoldsCharacter(ref TickStatePayload payload, int slotIndex)
        {
            return slotIndex switch
            {
                0 => payload.Slot1_CharacterId != Guid.Empty,
                1 => payload.Slot2_CharacterId != Guid.Empty,
                2 => payload.Slot3_CharacterId != Guid.Empty,
                _ => false
            };
        }

        // The per-slot summary fields are ints on the wire, so a delta that
        // somehow exceeded int range saturates rather than wrapping negative -
        // a negative "you earned" line is worse than a clamped one.
        private static int ClampToInt(long value)
        {
            if (value <= 0L) return 0;
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        public static async Task<TickStatePayload> ExtrapolateOfflineProgressAsync(FolkIdleDbContext db, TickStatePayload payload, long currentUnixTimestamp)
        {
            if (payload.LastLogoutTimestamp == 0)
            {
                payload.LastLogoutTimestamp = currentUnixTimestamp;
                return payload;
            }

            long rawDeltaSeconds = currentUnixTimestamp - payload.LastLogoutTimestamp;
            if (rawDeltaSeconds <= 0)
            {
                return payload;
            }

            // Modul 13: Vodnik Mastery extends the universal offline cap.
            long effectiveMaxOfflineSeconds = RaceMasteryResolver.GetVodnikExtendedOfflineSeconds(payload.VodnikMasteryLevel, MaxOfflineSeconds);

            // Modul: TIME BEYOND THE CAP IS DISCARDED, DELIBERATELY. This Min is
            // where it goes, and nothing downstream ever sees rawDeltaSeconds
            // again.
            //
            // It reads like a loss and is not. Offline catch-up runs in FULL for
            // every character up to the cap - the player already has the gold,
            // the XP and the drops for those hours. The overflow used to be
            // pushed into the chrono bank, which paid for the same hours twice;
            // that was made a no-op long before the bank was deleted, so
            // removing the bank changed nothing here. If a reward for being away
            // longer than the cap is ever wanted, this is the line to change,
            // and it is a balance decision rather than a cleanup.
            long elapsedSeconds = Math.Min(effectiveMaxOfflineSeconds, rawDeltaSeconds);

            // Modul: Scholar, the Insight crown - everything earned while away
            // comes in a quarter faster.
            //
            // A SEPARATE NUMBER from elapsedSeconds, deliberately. Inflating
            // the elapsed time itself would also age the character faster and
            // would make the morning card report a night longer than the one
            // the player actually slept. What Scholar buys is the RATE, so
            // only the projections read this; aging, the overflow bank and
            // OfflineElapsedSeconds all keep the honest number.
            long earningSeconds = elapsedSeconds;
            if (payload.Skill_Scholar > 0)
            {
                float bonus = SkillTreeRegistry.GetBonusPercent(
                    SkillTreeRegistry.CrownScholar, payload.Skill_Scholar) / 100f;
                earningSeconds = (long)(elapsedSeconds * (1f + bonus));
            }

            // Modul: active (Slot1) character aging for the offline period,
            // mirroring SimulationEngine.ProcessAgeSlot's exact thresholds
            // (36000/72000/108000 AgeTicks) and its 10-AgeTicks-per-real-second
            // rate (the live tick increments AgeTicks by 1 on every 10 Hz tick).
            // Gated on ActiveActivityId > 0, matching ProcessSubTick's own
            // early-return when no activity was active. Computed as O(1) math
            // rather than a per-tick loop since aging is a pure threshold check
            // on accumulated ticks.
            if (payload.ActiveActivityId > 0 && payload.Slot1_CharacterId != Guid.Empty)
            {
                payload.Slot1_AgeTicks += elapsedSeconds * 10L;
                if (payload.Slot1_AgeTicks >= 108000L) payload.Slot1_AgePhase = 3;
                else if (payload.Slot1_AgeTicks >= 72000L) payload.Slot1_AgePhase = 2;
                else if (payload.Slot1_AgeTicks >= 36000L) payload.Slot1_AgePhase = 1;
                else payload.Slot1_AgePhase = 0;
            }

            await GrantVillagePassiveProductionAsync(db, payload.PlayerId, payload.LumberjackLevel, payload.MineLevel, payload.WarehouseLevel, payload.TownHallLevel, earningSeconds);

            // Modul: Phase - Full-Stack Production Polish, Part 1.1 (Offline
            // "Welcome Back" flow). Captured before the projection branches
            // below mutate payload, so the deltas set at the bottom of this
            // method are exactly what THIS catch-up granted - never a
            // running lifetime total - matching OfflineElapsedSeconds/
            // OfflineGoldEarned/OfflineXpEarned/OfflineMaterialDropsGranted's
            // own doc comments on TickStatePayload.
            long goldBeforeOfflineCatchUp = payload.CurrentGold;
            long xpBeforeOfflineCatchUp = payload.CurrentXp;
            int materialDropsGrantedThisCatchUp = 0;

            // Modul: EVERY CHARACTER CATCHES UP, not just slot 1.
            //
            // This method only ever read payload.ActiveActivityId, which is the
            // ACTIVE REGISTER - always slot 1. Characters 2 and 3 could be
            // bred, housed, aged and given a job, and then earned exactly
            // nothing for every hour the player was away. The live tick has
            // walked all three slots since the multi-slot overhaul; this did
            // not, so the two disagreed about what a character does.
            //
            // Same swap the live tick uses, so "what a slot earns offline" and
            // "what it earns online" read the identical register rather than
            // two descriptions of it.
            int unlockedSlots = CharacterSlotEngine.GetUnlockedSlotCount(payload.TownHallLevel);

            for (int slotIndex = 0; slotIndex < unlockedSlots; slotIndex++)
            {
                if (slotIndex > 0 && !SlotHoldsCharacter(ref payload, slotIndex))
                {
                    continue;
                }

                SimulationEngine.SwapSlotIntoActiveRegister(ref payload, slotIndex);
                try
                {
                    long slotGoldBefore = payload.CurrentGold;
                    long slotXpBefore = payload.CurrentXp;
                    int slotDrops = 0;

                    if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out GatheringNodeDefinition gatheringNode))
                    {
                        LootProjection projection = CalculateGatheringProjection(ref payload, gatheringNode, earningSeconds);
                        slotDrops += await GrantProjectedLootAsync(db, payload.PlayerId, projection, MaxOfflineLootRolls);
                    }
                    else if (payload.ActiveActivityId > 0)
                    {
                        LootProjection projection = CalculateCombatProjection(ref payload, earningSeconds);
                        if (projection.IsValid)
                        {
                            slotDrops += projection.EquipmentDropsGranted;
                            slotDrops += await GrantProjectedLootAsync(db, payload.PlayerId, projection, MaxOfflineLootRolls);
                        }
                        else if (slotIndex == 0)
                        {
                        }
                    }

                    int slotGold = ClampToInt(payload.CurrentGold - slotGoldBefore);
                    int slotXp = ClampToInt(payload.CurrentXp - slotXpBefore);
                    materialDropsGrantedThisCatchUp += slotDrops;

                    switch (slotIndex)
                    {
                        case 0:
                            payload.OfflineSlot1Gold = slotGold;
                            payload.OfflineSlot1Xp = slotXp;
                            payload.OfflineSlot1Drops = slotDrops;
                            break;
                        case 1:
                            payload.OfflineSlot2Gold = slotGold;
                            payload.OfflineSlot2Xp = slotXp;
                            payload.OfflineSlot2Drops = slotDrops;
                            break;
                        default:
                            payload.OfflineSlot3Gold = slotGold;
                            payload.OfflineSlot3Xp = slotXp;
                            payload.OfflineSlot3Drops = slotDrops;
                            break;
                    }
                }
                finally
                {
                    SimulationEngine.SwapSlotIntoActiveRegister(ref payload, slotIndex);
                }
            }

            payload.OfflineElapsedSeconds = elapsedSeconds;
            payload.OfflineGoldEarned = Math.Max(0L, payload.CurrentGold - goldBeforeOfflineCatchUp);
            payload.OfflineXpEarned = Math.Max(0L, payload.CurrentXp - xpBeforeOfflineCatchUp);
            payload.OfflineMaterialDropsGranted = materialDropsGrantedThisCatchUp;
            payload.OfflineSummaryTick = unchecked((byte)(payload.OfflineSummaryTick + 1));

            payload.LastLogoutTimestamp = currentUnixTimestamp;
            payload.IsDirty = true;
            return payload;
        }

        private static async Task<int> GrantProjectedLootAsync(FolkIdleDbContext db, long playerId, LootProjection projection, int availableInventorySpace)
        {
            // ReadOnlySpan<T> cannot be a parameter of an async method, so the span is
            // materialized into a plain array before the first await.
            LootTableEntry[] lootTable = ContentRegistry.GetLootTable(projection.LootTableId).ToArray();
            return await GrantAnalyticalLootAsync(db, playerId, lootTable, projection.LootRolls, availableInventorySpace, projection.LootLuckPct);
        }

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        // Grants offline wood/stone/iron_ore analytically, independent of
        // whatever gathering/combat activity was active while offline.
        private static async Task GrantVillagePassiveProductionAsync(FolkIdleDbContext db, long playerId, int lumberjackLevel, int mineLevel, int warehouseLevel, int townHallLevel, long elapsedSeconds)
        {
            if (elapsedSeconds <= 0)
            {
                return;
            }

            long goldRatePerHour = VillageManagementEngine.GetTownHallGoldRatePerHour(townHallLevel);
            long goldEarned = elapsedSeconds * goldRatePerHour / 3600L;

            // Modul: OUTPUT ONLY EVER GOES UP, 2026-09-01.
            //
            // These read `level % 5`, so every fifth upgrade RESET the building
            // to its weakest band: a Mine went from 500 ore an hour at level 4
            // to 100 at level 5, and a Warehouse from 2,500 storage to 500.
            // Upgrading made the building worse, and the cost reset alongside
            // it - so it read as a bargain right up until the output halved.
            //
            // The tier idea was sound and is kept, but it belongs to the COST
            // and the MATERIALS, which still band by five (see
            // CalculateProductionUpgradeCost and GetTierMaterials). What a
            // building produces is not a thing an upgrade may reduce.
            long woodRatePerHour = lumberjackLevel > 0 ? (lumberjackLevel + 1) * 100L : 0;
            long ironRatePerHour = mineLevel > 0 ? (mineLevel + 1) * 100L : 0;

            var lumberjackMats = VillageManagementEngine.GetTierMaterials(lumberjackLevel);
            var mineMats = VillageManagementEngine.GetTierMaterials(mineLevel);

            // One formula for storage, asked of the authority that owns it -
            // this used to compute its own (warehouseLevel % 5 + 1) * 500 while
            // CalculateWarehouseMaxStorage said level * 1000, so the offline
            // path and the live path disagreed about how much a warehouse holds.
            long maxStoragePerItem = VillageManagementEngine.CalculateWarehouseMaxStorage(warehouseLevel);

            long woodEarned = Math.Min(elapsedSeconds * woodRatePerHour / 3600L, maxStoragePerItem);
            long oreEarned = Math.Min(elapsedSeconds * ironRatePerHour / 3600L, maxStoragePerItem);

            // Modul: A SHARE OF THE YIELD IS THE TIER'S RARE MATERIAL,
            // 2026-09-01, in the same 90/10 the gathering loot tables use for
            // the same pairs. A Mine automates mining and should pay out what
            // mining pays out.
            //
            // Split rather than added: the building's throughput is unchanged
            // and a tenth of it simply arrives as the better material. Adding
            // it on top would make a Mine strictly better than the activity it
            // represents, which is a balance decision and not this fix.
            //
            // Computed as a share of the WHOLE window rather than rolled per
            // unit - this path is analytic by design and a per-unit loop over
            // twelve hours of production is exactly what it exists to avoid.
            long rareWood = woodEarned * VillageManagementEngine.RareYieldPercent / 100L;
            long rareOre = oreEarned * VillageManagementEngine.RareYieldPercent / 100L;
            woodEarned -= rareWood;
            oreEarned -= rareOre;

            bool anyMaterialProduction = woodEarned > 0 || oreEarned > 0 || rareWood > 0 || rareOre > 0;
            if (!anyMaterialProduction && goldEarned <= 0)
            {
                return;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                if (woodEarned > 0)
                {
                    await GrantSingleCommodityProductionAsync(db, playerId, lumberjackMats.Log, woodEarned, maxStoragePerItem);
                }
                if (oreEarned > 0)
                {
                    await GrantSingleCommodityProductionAsync(db, playerId, mineMats.Ore, oreEarned, maxStoragePerItem);
                }
                if (rareWood > 0)
                {
                    await GrantSingleCommodityProductionAsync(db, playerId, lumberjackMats.RareLog, rareWood, maxStoragePerItem);
                }
                if (rareOre > 0)
                {
                    await GrantSingleCommodityProductionAsync(db, playerId, mineMats.RareOre, rareOre, maxStoragePerItem);
                }

                if (goldEarned > 0)
                {
                    var goldRecord = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();
                    if (goldRecord == null)
                    {
                        goldRecord = new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 0L };
                        db.CommodityRecords.Add(goldRecord);
                    }
                    goldRecord.Quantity += goldEarned;
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
            }
        }

        private static async Task GrantSingleCommodityProductionAsync(FolkIdleDbContext db, long playerId, string itemId, long amountToGrant, long maxStorage)
        {
            if (amountToGrant <= 0) return;

            var commodity = await db.CommodityRecords
                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, itemId)
                .SingleOrDefaultAsync();

            long currentStorage = commodity?.Quantity ?? 0L;
            long grantedAmount = Math.Min(amountToGrant, Math.Max(0L, maxStorage - currentStorage));
            if (grantedAmount <= 0)
            {
                return;
            }

            if (commodity == null)
            {
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = itemId, Quantity = grantedAmount });
            }
            else
            {
                commodity.Quantity += grantedAmount;
            }
        }

        private static LootProjection CalculateGatheringProjection(ref TickStatePayload payload, GatheringNodeDefinition node, long elapsedSeconds)
        {
            // Modul: the same two-branch bug, one line up from the XP one -
            // this decides the SPEED a fishing node gathers at, and it read the
            // player's mining level to do it. Asked of SimulationEngine, which
            // owns the mapping.
            int masteryLevel = Domain.Combat.SimulationEngine.GetMasteryLevel(ref payload, node.ProfessionType);

            // Modul: THE SAME FUNCTION THE LIVE TICK CALLS, at last.
            //
            // This kept its own private copy of the formula, and the copy was
            // the version from before the live one was fixed: it read
            // CachedCurrentToolTier, which is the FORGE BUILDING'S level rather
            // than any tool, so an hour offline gathered at a speed set by a
            // building - no matching tool, no percentage curve, no village
            // production bonus, no affixes. A player logging out mid-fishing
            // came back to a different game than the one they left.
            int toolTier = node.ProfessionType switch
            {
                0 => payload.AxeToolTier,
                1 => payload.PickaxeToolTier,
                _ => payload.RodToolTier
            };
            int villageProductionLevel = node.ProfessionType switch
            {
                0 => payload.LumberjackLevel,
                1 => payload.MineLevel,
                _ => 0
            };
            int requiredTicks = Domain.Shared.GatheringToolEngine.ComputeRequiredTicks(
                node.BaseTickThreshold, masteryLevel, toolTier, villageProductionLevel,
                payload.ToolGatherSpeedPct
                + SkillTreeRegistry.GetBonusTenthsOfPercent(
                    SkillTreeRegistry.BoughHarvest, payload.Skill_Harvest) / 10
                + (int)BreedingAptitudes.BonusPercentFor(payload.Aptitude_Skill));

            double actionIntervalSeconds = requiredTicks / 10.0;
            double totalActionsDouble = elapsedSeconds / actionIntervalSeconds;

            long allowedActions = (long)Math.Min(totalActionsDouble, MaxOfflineGatherActions);
            double usedSeconds = allowedActions * actionIntervalSeconds;

            long masteryXpGained = allowedActions * node.BaseMasteryXpReward;
            ApplyGatheringMasteryXp(ref payload, node.ProfessionType, masteryXpGained);

            // Modul: LocusYield (+4% harvest rolls per point) still scales roll
            // COUNT. LootLuckPct no longer does - it now shifts per-item weight
            // distribution toward rare entries inside GrantAnalyticalLootAsync,
            // instead of inflating the absolute volume of every entry
            // (including common trash) in fixed proportion.
            int gatherProjectionAgePhase = 1;
            int gatherProjectionRaceId = 0;
            if (payload.Slot1_CharacterId != Guid.Empty)
            {
                gatherProjectionAgePhase = payload.Slot1_AgePhase;
                gatherProjectionRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);
            }
            CombatStats gatherProjectionStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, gatherProjectionAgePhase, payload.CompletedAreaFlags, gatherProjectionRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds);
            double locusYieldFactor = 1.0 + (payload.LocusYield * 0.04);

            int lootRolls = (int)(allowedActions * payload.CachedCodexYieldMultiplier * locusYieldFactor);
            return new LootProjection(true, node.ActivityId, lootRolls, 0, gatherProjectionStats.LootLuckPct);
        }

        private static LootProjection CalculateCombatProjection(ref TickStatePayload payload, long elapsedSeconds)
        {
            int fallbackId = payload.ActiveActivityId > ContentRegistry.Monsters.Length ? 1 : (int)payload.ActiveActivityId;
            if (fallbackId <= 0 || fallbackId > ContentRegistry.Monsters.Length)
            {
                return new LootProjection(false, 0, 0);
            }

            MonsterDefinition activeMonster = ContentRegistry.Monsters[fallbackId - 1];

            // Modul: a first-clear boss is bigger here too.
            //
            // This path reads the authored definition straight out of the
            // registry, so without this an offline stretch would fight the
            // farmable version of a boss the live tick treats as a first clear
            // - and credit kills the player has not earned. Offline diverging
            // from live in exactly this way is a mistake this codebase has
            // already made three times (food healing, warp tool tier, mastery
            // routing).
            if (BossFirstClearRules.IsFirstClearPending(payload.DefeatedRegionBossMask, fallbackId))
            {
                activeMonster.MaxHp = (int)Math.Min(
                    int.MaxValue, (long)activeMonster.MaxHp * BossFirstClearRules.FirstClearHpMultiplier);
                activeMonster.AttackPower = (int)Math.Min(
                    int.MaxValue, (long)activeMonster.AttackPower * BossFirstClearRules.FirstClearAttackMultiplier);
            }

            int lineageId = payload.SelectedLineageId;
            if (lineageId < 0 || lineageId >= ProgressionEngine.Lineages.Length) lineageId = 0;
            LineageDefinition lineage = ProgressionEngine.Lineages[lineageId];

            int activeAgePhase = 1;
            int activeRaceId = 0;
            if (payload.Slot1_CharacterId != Guid.Empty)
            {
                activeAgePhase = payload.Slot1_AgePhase;
                activeRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);
            }

            CombatStats combatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, activeAgePhase, payload.CompletedAreaFlags, activeRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds);

            // Analytical projection intentionally uses expected (average) damage
            // per hit rather than replaying per-swing hit/crit RNG - but the
            // EXPECTATION is now CombatDamageModel's, the same one the live tick
            // rolls against.
            //
            // This line used to read `Math.Max(1000, (int)effectiveMilliAttack)`:
            // the monster's armour was never subtracted and the hit roll never
            // applied, so an hour offline was credited with roughly three hours
            // of live combat on region 1 and worse further in, where armour is
            // five times higher. See CombatDamageModel for the other two models
            // this replaces.
            long effectiveMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(in combatStats, lineage.DamageScalePerLevelPct, payload.CurrentLevel, InheritanceRegistry.GetBonusPct(payload.Inherit_Damage));
            double secondsPerKillEstimate = CombatDamageModel.ExpectedSecondsPerKill(in combatStats, in activeMonster, effectiveMilliAttack, payload.CachedCodexDamageMultiplier);

            if (double.IsInfinity(secondsPerKillEstimate) || secondsPerKillEstimate <= 0.0 || activeMonster.MaxHp <= 0)
            {
                return new LootProjection(false, 0, 0);
            }

            // Modul: expected incoming damage, mirroring the live tick's
            // "Monster attacks player" block and monster crit formula (5% base
            // + 0.5% per region tier, 1.5x crit multiplier, Vodnik's
            // CritMitigationPct subtracted from that multiplier). Uses an
            // expected-value blend of crit/non-crit hits rather than replaying
            // per-swing RNG, consistent with the rest of this analytical path.
            int monsterRegionTier = ContentRegistry.GetMonsterRegionTier(fallbackId);
            float monsterCritChance = 0.05f + (monsterRegionTier * 0.005f);
            float mitigatedCritMult = Math.Max(1.0f, 1.5f - (combatStats.CritMitigationPct / 100f));
            float expectedCritMultiplier = 1.0f + monsterCritChance * (mitigatedCritMult - 1.0f);

            long rawIncomingMilliDamage = (long)(activeMonster.AttackPower * 1000 * expectedCritMultiplier);
            long netIncomingMilliDamage = CombatDamageModel.Mitigate(
                rawIncomingMilliDamage,
                combatStats.FlatPhysicalArmor,
                CombatDamageModel.PlayerArmourHalvingConstant(monsterRegionTier));

            double monsterAttacksPerSecond = activeMonster.AttackIntervalMs > 0 ? 1000.0 / activeMonster.AttackIntervalMs : 0.0;
            double expectedIncomingMilliDps = (netIncomingMilliDamage) * monsterAttacksPerSecond;

            // Modul: the player's own max-HP pool is a "free" absorption buffer
            // before any food is ever needed (mirrors the live tick, where
            // Auto-Eat only triggers once HP drops below AutoEatThreshold, not
            // at the very first point of damage) - without this, a character
            // with simply no food stocked (Food1-3 all zero, the common case
            // for most players) would be treated as unable to survive any
            // combat time at all, which is wrong.
            // Modul: the base pool is a CURVE now, not a constant - see
            // ProgressionEngine.BaseMilliHpForLevel. A flat 100 against monster
            // attack that goes up 4.2x a region is why region 5 one-shot
            // everybody.
            long baseMilliHp = ProgressionEngine.BaseMilliHpForLevel(payload.CurrentLevel);
            long effectiveMilliHp = baseMilliHp + (baseMilliHp * lineage.HpScalePerLevelPct * payload.CurrentLevel / 100) + (combatStats.MaxHp * 1000L);
            effectiveMilliHp += effectiveMilliHp * InheritanceRegistry.GetBonusPct(payload.Inherit_MaxHp) / 100L;
            // Modul: Fortitude, the Cruelty bough - more health, layered the
            // same additive-percent way inheritance is just above.
            effectiveMilliHp += effectiveMilliHp * (long)SkillTreeRegistry.GetBonusTenthsOfPercent(
                SkillTreeRegistry.BoughFortitude, payload.Skill_Fortitude) / 1000L;

            // Modul: Endurance offline as well as live. The two health formulas
            // diverging is a bug this codebase has already shipped once, in
            // gathering - the offline path used a stale private formula for
            // months while the live one moved on.
            effectiveMilliHp += (long)(effectiveMilliHp
                * BreedingAptitudes.BonusPercentFor(payload.Aptitude_Endurance) / 100f);

            double effectiveElapsedSeconds = elapsedSeconds;
            if (expectedIncomingMilliDps > 0.0)
            {
                double totalIncomingMilliDamage = expectedIncomingMilliDps * elapsedSeconds;
                long totalFoodUnits = payload.Food1_Count + payload.Food2_Count + payload.Food3_Count;
                double healPerUnitMilliHp = AverageHealPerFoodUnitMilliHp(in payload, effectiveMilliHp);
                double totalHealCapacityMilliHp = effectiveMilliHp + ((double)totalFoodUnits * healPerUnitMilliHp);

                if (totalIncomingMilliDamage > totalHealCapacityMilliHp)
                {
                    // Modul: food stock depletes before the full offline
                    // window is survived - sustain only as much combat time as
                    // available food allows, bank the remainder as overflow
                    // seconds (same mechanic already used when inventory space
                    // caps gathering actions), and consume all available food.
                    effectiveElapsedSeconds = totalHealCapacityMilliHp / expectedIncomingMilliDps;
                    if (effectiveElapsedSeconds < 0.0) effectiveElapsedSeconds = 0.0;


                    ConsumeFoodStock(ref payload, totalFoodUnits);
                }
                else if (healPerUnitMilliHp > 0.0)
                {
                    long foodUnitsConsumed = (long)Math.Ceiling(totalIncomingMilliDamage / healPerUnitMilliHp);
                    ConsumeFoodStock(ref payload, foodUnitsConsumed);
                }
            }

            double totalKillsDouble = effectiveElapsedSeconds / secondsPerKillEstimate;
            long totalKills = (long)totalKillsDouble;

            long xpGained = totalKills * activeMonster.BaseXpReward;
            xpGained += xpGained * InheritanceRegistry.GetBonusPct(payload.Inherit_XpGain) / 100L;
            ApplyCombatXp(ref payload, xpGained);

            // Modul 13.4.3: Gold reward, matching the live tick's exact
            // formula (GlobalEngineState.GlobalGoldDropMultiplier scaling plus
            // Human's innate +5% Gold acquisition passive) so offline combat
            // grants the same gold value per kill as live/warp combat.
            long goldPerKill = (activeMonster.BaseGoldReward * (long)GlobalEngineState.GlobalGoldDropMultiplier) / 100L;
            goldPerKill += goldPerKill * InheritanceRegistry.GetBonusPct(payload.Inherit_GoldGain) / 100L;
            goldPerKill = (long)(goldPerKill * (1.0f + combatStats.GoldAcquisitionMultiplierPct / 100f));
            long totalGoldGained = totalKills * goldPerKill;
            if (totalGoldGained > 0)
            {
                payload.AddGold(totalGoldGained);
                payload.RedisPendingGoldDelta += totalGoldGained;
                payload.RequiresRedisFlush = true;
            }

            // Modul: OFFLINE EQUIPMENT NOW ROLLS EXACTLY AS ONLINE DOES.
            //
            // This used to enqueue ONE REQUEST PER KILL, capped at 500, each
            // costing its own scope, SERIALIZABLE transaction and commit. The
            // cap was there for that cost - but equipment drops at 5% a kill,
            // so 500 requests is 25 pieces however long you were away. A twelve
            // hour window at fifteen seconds a kill earns 144 pieces online and
            // paid 25, and the materials beside them were uncapped, which is
            // precisely the reported "offline drops me nothing good".
            //
            // One request now carries the whole window and the loot engine
            // rolls it inside a single transaction, so the rate is the online
            // rate and the cost is one transaction rather than thousands.
            long killsToRoll = Math.Min(totalKills, MaxOfflineKillsPerSlot);

            // Golden Fleece across the window. The counter advances on every
            // kill whether or not the crown is taken - matching the live tick,
            // whose comment explains that taking it must not hand over a
            // hundred-kill head start - and only pays tiers if it is.
            long fleeceCounter = payload.KillsSinceFleece + killsToRoll;
            long fleeceProcs = fleeceCounter / Domain.Combat.SimulationEngine.GoldenFleeceKillInterval;
            payload.KillsSinceFleece = (int)(fleeceCounter % Domain.Combat.SimulationEngine.GoldenFleeceKillInterval);

            long fleeceKills = payload.Skill_GoldenFleece > 0 ? fleeceProcs : 0L;
            long plainKills = killsToRoll - fleeceKills;

            // Materials are skipped on these requests because this method's own
            // projection below already grants the window's materials in bulk.
            // Rolling them here as well was a double grant, hidden by the cap.
            if (plainKills > 0)
            {
                CombatLootEngine.DropRequestQueue.Enqueue(CombatLootDropRequest.Build(
                    in payload, in combatStats, fallbackId,
                    kills: (int)plainKills, bonusRarityTiers: 0, skipMaterialRoll: true));
            }
            if (fleeceKills > 0)
            {
                CombatLootEngine.DropRequestQueue.Enqueue(CombatLootDropRequest.Build(
                    in payload, in combatStats, fallbackId,
                    kills: (int)fleeceKills,
                    bonusRarityTiers: Domain.Combat.SimulationEngine.GoldenFleeceBonusTiers,
                    skipMaterialRoll: true));
            }

            // Modul: the global drop multiplier reaches offline play too. The
            // live tick scales its loot rolls by GlobalEngineState
            // .GlobalDropMultiplier (100 = normal, raised by an admin for an
            // event); this path ignored it, so a double-drop weekend paid
            // double only to players who sat and watched.
            int lootRolls = (int)(totalKillsDouble
                * payload.CachedCodexYieldMultiplier
                * (GlobalEngineState.GlobalDropMultiplier / 100.0));

            // Modul: the equipment component of this count is 0, not a guess.
            // Equipment is rolled later, on CombatLootEngine's own thread, so
            // nothing here knows how many pieces fell. It used to report the
            // REQUEST count, which overstated the truth twentyfold - a 5% roll
            // reported as a drop. The summary counts what this method actually
            // granted; the gear arrives in the chest either way.
            return new LootProjection(true, activeMonster.LootTableId, lootRolls, 0, combatStats.LootLuckPct);
        }

        // Modul: drains Food1-3 in a fixed order (mirrors the live tick's
        // Auto-Eat consumption, which always prefers the first populated
        // slot). Used to simulate offline food consumption without per-swing
        // RNG or per-heal-event iteration.
        private static void ConsumeFoodStock(ref TickStatePayload payload, long unitsToConsume)
        {
            if (unitsToConsume <= 0) return;

            long fromSlot1 = Math.Min(unitsToConsume, payload.Food1_Count);
            payload.Food1_Count -= (int)fromSlot1;
            unitsToConsume -= fromSlot1;
            if (unitsToConsume <= 0) return;

            long fromSlot2 = Math.Min(unitsToConsume, payload.Food2_Count);
            payload.Food2_Count -= (int)fromSlot2;
            unitsToConsume -= fromSlot2;
            if (unitsToConsume <= 0) return;

            long fromSlot3 = Math.Min(unitsToConsume, payload.Food3_Count);
            payload.Food3_Count -= (int)fromSlot3;
        }

        private static void ApplyCombatXp(ref TickStatePayload payload, long xpGained)
        {
            if (xpGained <= 0) return;

            // Modul 13.4.3: -20% character XP generation while an early
            // mentorship termination penalty is active (see MentorshipEngine).
            if (payload.XpPenaltyExpiresEpoch > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                xpGained = (long)(xpGained * 0.8);
            }

            payload.CurrentXp += xpGained;
            int levelsGained = 0;
            while (true)
            {
                // Modul: must stay identical to the live-tick formula, or a
                // player's level-up pace would silently diverge depending on
                // whether the XP was earned online or projected while offline.
                // Calls the one authority rather than mirroring it - see
                // ProgressionEngine.GetRequiredXpForLevel.
                long requiredXp = ProgressionEngine.GetRequiredXpForLevel(payload.CurrentLevel);
                if (payload.CurrentXp >= requiredXp)
                {
                    payload.CurrentXp -= requiredXp;
                    payload.CurrentLevel++;
                    levelsGained++;
                    // Modul: and the skill point that comes with the level.
                    // Identical to the live tick was already the stated rule
                    // here, and it held for the XP formula while quietly
                    // failing for the reward the level pays out.
                    payload.AvailableSkillPoints++;
                }
                else
                {
                    break;
                }
            }

            // Modul: AND THE ATTRIBUTES, WHICH WERE THE THIRD THING THIS PATH
            // FORGOT, 2026-09-06.
            //
            // Both live level-up paths call RaceAttributeGrowth here. This one
            // never has, so every level gained while the player was away raised
            // the level and paid the skill point and granted NO STR, DEX, CON or
            // LCK. In an idle game most levels are gained exactly this way.
            //
            // Measured on the only account past level 1: level 86, and its four
            // attributes read 50 / 50 / 50 / 25 - the values a fresh
            // registration gets. A Human at level 86 should hold 220 of the
            // first three.
            //
            // It matters more than it did. DEX is AccuracyRating, and accuracy
            // bought nothing at all while every canonical monster had
            // DodgeRating 0. Monsters evade now, and MonsterDefenceCurve prices
            // their dodge against the accuracy levelling is supposed to provide
            // - so a character stuck at its starting DEX misses swings the
            // curve assumes it lands. CON is 15 max HP a point on top of that.
            //
            // The same comment three fixes ago said this path "must stay
            // identical to the live tick"; that is now true of the XP formula,
            // the skill point AND the attributes.
            if (levelsGained > 0)
            {
                int activeRaceId = payload.Slot1_CharacterId != System.Guid.Empty
                    ? (int)(payload.Slot1_GeneticVector & 0xFF)
                    : 0;
                RaceAttributeGrowth.ApplyLevelUpGrowth(ref payload, activeRaceId, levelsGained);
            }
        }

        // Modul: THIS WAS THE COPY THAT WAS MISSED.
        //
        // `professionType == 0 ? Woodcutting : Mining` - two branches for four
        // professions, so Fishing (2) and Herbalism (3) both levelled MINING.
        // The realtime and warp paths were fixed and given a comment saying the
        // mapping "now exists exactly once"; it did not. It existed twice, and
        // the second one is the one that runs while the player is away.
        //
        // Reported from the live game: fishing, connection lost during a
        // deployment, and the time away came back as mining experience. That is
        // this method.
        //
        // It delegates to SimulationEngine's own switch now, rather than
        // restating it, because a copy that agrees today is exactly what this
        // was.
        private static void ApplyGatheringMasteryXp(ref TickStatePayload payload, int professionType, long xpGained)
        {
            if (xpGained <= 0) return;
            Domain.Combat.SimulationEngine.ApplyBulkMasteryXp(ref payload, professionType, xpGained);
        }

        // Isolated so it can be tested directly against a hand-built loot table,
        // since ContentRegistry's real loot tables currently carry no entries.
        //
        // Modul: LootLuckPct no longer scales rollCount (that inflated the
        // absolute volume of every entry, common trash and rare drops alike,
        // in fixed proportion). It now adds a flat weight bonus to every
        // entry's selection weight, mirroring the live-tick gathering roll's
        // identical fix - a fixed addition is a far larger relative increase
        // for a low-weight (rare) entry than a high-weight (common) one, so
        // higher luck shifts the selection distribution toward rare drops
        // without changing the total number of rolls.
        internal static async Task<int> GrantAnalyticalLootAsync(FolkIdleDbContext db, long playerId, LootTableEntry[] lootTable, int rollCount, int availableInventorySpace, float lootLuckPct = 0f)
        {
            if (lootTable.Length == 0 || rollCount <= 0 || availableInventorySpace <= 0)
            {
                return 0;
            }

            int luckWeightBonus = (int)(lootLuckPct * 0.1f);
            if (luckWeightBonus < 0) luckWeightBonus = 0;

            int totalWeight = 0;
            for (int i = 0; i < lootTable.Length; i++)
            {
                totalWeight += lootTable[i].Weight + luckWeightBonus;
            }

            if (totalWeight <= 0)
            {
                return 0;
            }

            int rollsToExecute = Math.Min(rollCount, availableInventorySpace);

            var grantedQuantities = new Dictionary<int, long>();
            for (int r = 0; r < rollsToExecute; r++)
            {
                int roll = Random.Shared.Next(totalWeight);
                int currentWeight = 0;
                for (int i = 0; i < lootTable.Length; i++)
                {
                    currentWeight += lootTable[i].Weight + luckWeightBonus;
                    if (roll < currentWeight)
                    {
                        grantedQuantities.TryGetValue(lootTable[i].ItemId, out long existing);
                        grantedQuantities[lootTable[i].ItemId] = existing + 1;
                        break;
                    }
                }
            }

            foreach (KeyValuePair<int, long> kvp in grantedQuantities)
            {
                string materialName = ContentRegistry.GetMaterialString(kvp.Key);
                if (materialName == "unknown")
                {
                    continue;
                }

                var commodity = await db.CommodityRecords
                    .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, materialName)
                    .SingleOrDefaultAsync();

                if (commodity == null)
                {
                    db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = materialName, Quantity = kvp.Value });
                }
                else
                {
                    commodity.Quantity += kvp.Value;
                }
            }

            await db.SaveChangesAsync();

            return rollsToExecute;
        }
    }
}
