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
        private const long MaxOfflineSeconds = 43200L;

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
        private const int MaxOfflineEquipmentDropsPerSlot = 500;

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
            long elapsedSeconds = Math.Min(effectiveMaxOfflineSeconds, rawDeltaSeconds);

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

            await GrantVillagePassiveProductionAsync(db, payload.PlayerId, payload.LumberjackLevel, payload.QuarryLevel, payload.MineLevel, payload.WarehouseLevel, payload.TownHallLevel, elapsedSeconds);

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
                        LootProjection projection = CalculateGatheringProjection(ref payload, gatheringNode, elapsedSeconds);
                        slotDrops += await GrantProjectedLootAsync(db, payload.PlayerId, projection, MaxOfflineLootRolls);
                    }
                    else if (payload.ActiveActivityId > 0)
                    {
                        LootProjection projection = CalculateCombatProjection(ref payload, elapsedSeconds);
                        if (projection.IsValid)
                        {
                            slotDrops += projection.EquipmentDropsGranted;
                            slotDrops += await GrantProjectedLootAsync(db, payload.PlayerId, projection, MaxOfflineLootRolls);
                        }
                        else if (slotIndex == 0)
                        {
                            // Only slot 1 banks unusable time. Banking once per
                            // idle slot would triple a player's chrono reserve
                            // for the crime of owning characters.
                            BankOverflowSeconds(ref payload, elapsedSeconds);
                        }
                    }
                    else if (slotIndex == 0)
                    {
                        BankOverflowSeconds(ref payload, elapsedSeconds);
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
        private static async Task GrantVillagePassiveProductionAsync(FolkIdleDbContext db, long playerId, int lumberjackLevel, int quarryLevel, int mineLevel, int warehouseLevel, int townHallLevel, long elapsedSeconds)
        {
            if (elapsedSeconds <= 0)
            {
                return;
            }

            // Modul: Economy Polish, Part 2. Town Hall passive gold is
            // granted regardless of warehouse storage (gold is liquidity,
            // not a warehoused material - the storage cap below only
            // governs wood/stone/iron). Pure integer math:
            // goldEarned = elapsedSeconds * ratePerHour / 3600.
            long goldRatePerHour = VillageManagementEngine.GetTownHallGoldRatePerHour(townHallLevel);
            long goldEarned = elapsedSeconds * goldRatePerHour / 3600L;

            long maxStorage = VillageManagementEngine.CalculateWarehouseMaxStorage(warehouseLevel);

            float woodRate = lumberjackLevel * VillageManagementEngine.LumberjackWoodRatePerLevel;
            float stoneRate = quarryLevel * VillageManagementEngine.QuarryStoneRatePerLevel;
            float ironRate = mineLevel * VillageManagementEngine.MineIronRatePerLevel;

            bool anyMaterialProduction = maxStorage > 0 && (woodRate > 0f || stoneRate > 0f || ironRate > 0f);
            if (!anyMaterialProduction && goldEarned <= 0)
            {
                return;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                if (anyMaterialProduction)
                {
                    await GrantSingleCommodityProductionAsync(db, playerId, VillageManagementEngine.WoodCommodityId, woodRate, elapsedSeconds, maxStorage);
                    await GrantSingleCommodityProductionAsync(db, playerId, VillageManagementEngine.StoneCommodityId, stoneRate, elapsedSeconds, maxStorage);
                    await GrantSingleCommodityProductionAsync(db, playerId, VillageManagementEngine.IronOreCommodityId, ironRate, elapsedSeconds, maxStorage);
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

        private static async Task GrantSingleCommodityProductionAsync(FolkIdleDbContext db, long playerId, string itemId, float productionRatePerSecond, long elapsedSeconds, long maxStorage)
        {
            if (productionRatePerSecond <= 0f)
            {
                return;
            }

            long potential = (long)(elapsedSeconds * productionRatePerSecond);
            if (potential <= 0)
            {
                return;
            }

            var commodity = await db.CommodityRecords
                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, itemId)
                .SingleOrDefaultAsync();

            long currentStorage = commodity?.Quantity ?? 0L;
            long grantedAmount = Math.Min(potential, Math.Max(0L, maxStorage - currentStorage));
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
            int masteryLevel = node.ProfessionType == 0 ? payload.WoodcuttingMasteryLevel : payload.MiningMasteryLevel;
            int requiredTicks = node.BaseTickThreshold - (masteryLevel * 2) - payload.CachedCurrentToolTier;
            if (requiredTicks < 2) requiredTicks = 2;

            double actionIntervalSeconds = requiredTicks / 10.0;
            double totalActionsDouble = elapsedSeconds / actionIntervalSeconds;

            long allowedActions = (long)Math.Min(totalActionsDouble, MaxOfflineGatherActions);
            double usedSeconds = allowedActions * actionIntervalSeconds;
            double overflowSeconds = elapsedSeconds - usedSeconds;
            BankOverflowSeconds(ref payload, (long)overflowSeconds);

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
            long netIncomingMilliDamage = Math.Max(1000L, rawIncomingMilliDamage - (combatStats.FlatPhysicalArmor * 1000L));

            double monsterAttacksPerSecond = activeMonster.AttackIntervalMs > 0 ? 1000.0 / activeMonster.AttackIntervalMs : 0.0;
            double expectedIncomingMilliDps = (netIncomingMilliDamage) * monsterAttacksPerSecond;

            // Modul: the player's own max-HP pool is a "free" absorption buffer
            // before any food is ever needed (mirrors the live tick, where
            // Auto-Eat only triggers once HP drops below AutoEatThreshold, not
            // at the very first point of damage) - without this, a character
            // with simply no food stocked (Food1-3 all zero, the common case
            // for most players) would be treated as unable to survive any
            // combat time at all, which is wrong.
            long baseMilliHp = 100000L;
            long effectiveMilliHp = baseMilliHp + (baseMilliHp * lineage.HpScalePerLevelPct * payload.CurrentLevel / 100) + (combatStats.MaxHp * 1000L);
            effectiveMilliHp += effectiveMilliHp * InheritanceRegistry.GetBonusPct(payload.Inherit_MaxHp) / 100L;

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

                    double overflowSeconds = elapsedSeconds - effectiveElapsedSeconds;
                    BankOverflowSeconds(ref payload, (long)overflowSeconds);

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

            // Modul: equipment drop requests, safely bounded by kill count and
            // available inventory space (reserved by the caller in
            // ExtrapolateOfflineProgressAsync) so a long-offline player cannot
            // flood CombatLootEngine's queue/transactions in a single login.
            int equipmentDropsToGrant = (int)Math.Min(totalKills, MaxOfflineEquipmentDropsPerSlot);
            for (int i = 0; i < equipmentDropsToGrant; i++)
            {
                CombatLootEngine.DropRequestQueue.Enqueue(new CombatLootDropRequest
                {
                    PlayerId = payload.PlayerId,
                    MonsterId = fallbackId,
                    LootLuckPct = combatStats.LootLuckPct + InheritanceRegistry.GetBonusPct(payload.Inherit_LootLuck)
                });
            }

            int lootRolls = (int)(totalKillsDouble * payload.CachedCodexYieldMultiplier);
            return new LootProjection(true, activeMonster.LootTableId, lootRolls, equipmentDropsToGrant, combatStats.LootLuckPct);
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
                }
                else
                {
                    break;
                }
            }
        }

        private static void ApplyGatheringMasteryXp(ref TickStatePayload payload, int professionType, long xpGained)
        {
            if (xpGained <= 0) return;

            int gainedXp = (int)Math.Min(xpGained, int.MaxValue);

            if (professionType == 0)
            {
                payload.WoodcuttingMasteryXp += gainedXp;
                while (true)
                {
                    int requiredMasteryXp = 50 * (payload.WoodcuttingMasteryLevel + 1) * (payload.WoodcuttingMasteryLevel + 1);
                    if (payload.WoodcuttingMasteryXp >= requiredMasteryXp)
                    {
                        payload.WoodcuttingMasteryXp -= requiredMasteryXp;
                        payload.WoodcuttingMasteryLevel++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else
            {
                payload.MiningMasteryXp += gainedXp;
                while (true)
                {
                    int requiredMasteryXp = 50 * (payload.MiningMasteryLevel + 1) * (payload.MiningMasteryLevel + 1);
                    if (payload.MiningMasteryXp >= requiredMasteryXp)
                    {
                        payload.MiningMasteryXp -= requiredMasteryXp;
                        payload.MiningMasteryLevel++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        // Modul: OFFLINE TIME IS NO LONGER BANKED.
        //
        // The chrono bank existed because offline catch-up was capped at twenty
        // actions - everything past that was pushed in here as "overflow", and
        // Time Warp was how a player got it back. Catch-up now runs in full for
        // every character, so banking on top of that pays for the same hours
        // twice: the player already has the gold, the XP and the drops.
        //
        // The bank itself stays - it is a real mechanic with a real screen, and
        // it is the right shape for a reward. What fills it is login streaks
        // and season pass tiers, not simply having been away.
        //
        // Kept as a no-op with its callers intact rather than deleted: the two
        // call sites are the "this character could not use its time" branches,
        // and they are exactly where a future reward hook belongs.
        private static void BankOverflowSeconds(ref TickStatePayload payload, long seconds)
        {
            _ = payload;
            _ = seconds;
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
