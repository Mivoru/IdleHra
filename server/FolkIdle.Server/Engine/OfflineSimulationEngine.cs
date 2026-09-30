using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
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

            // Modul: active (Slot1) character aging for the offline period, at
            // the live tick's 10-AgeTicks-per-real-second rate (the tick adds 1
            // on every 10 Hz pass). Gated on ActiveActivityId > 0, matching
            // ProcessSubTick's own early-return when no activity was active.
            // Computed as O(1) math rather than a per-tick loop since aging is
            // a pure threshold check on accumulated ticks.
            //
            // The thresholds themselves used to be repeated here as literals,
            // under a comment claiming they mirrored ProcessAgeSlot's exactly.
            // They are AgePhaseCurve's now, so the claim is structural instead
            // of aspirational.
            if (payload.ActiveActivityId > 0 && payload.Slot1_CharacterId != Guid.Empty)
            {
                payload.Slot1_AgeTicks += elapsedSeconds * 10L;
                payload.Slot1_AgePhase = AgePhaseCurve.PhaseFor(payload.Slot1_AgeTicks);
            }

            payload.OfflineMaterialsLostToFullWarehouse = await GrantVillagePassiveProductionAsync(db, payload.PlayerId, payload.LumberjackLevel, payload.MineLevel, payload.WarehouseLevel, payload.TownHallLevel, earningSeconds);

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
                        slotDrops += await GrantProjectedLootAsync(db, payload.PlayerId, projection, MaxOfflineLootRolls, recordAsGathered: true);
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

        private static async Task<int> GrantProjectedLootAsync(FolkIdleDbContext db, long playerId, LootProjection projection, int availableInventorySpace, bool recordAsGathered = false)
        {
            // ReadOnlySpan<T> cannot be a parameter of an async method, so the span is
            // materialized into a plain array before the first await.
            LootTableEntry[] lootTable = ContentRegistry.GetLootTable(projection.LootTableId).ToArray();
            return await GrantAnalyticalLootAsync(db, playerId, lootTable, projection.LootRolls, availableInventorySpace, projection.LootLuckPct, recordAsGathered);
        }

        // Modul: THIS PATH HAD NO OBSERVABILITY AT ALL - the exact shape
        // CombatLootEngine's loot path had before its 2026-09-06 fix. A
        // transient database error here (a dropped Supabase pooler
        // connection, a serialization failure) rolled back silently, and a
        // player's earned wood/ore/gold simply vanished: not in the log, not
        // in the offline summary, not in a counter a dashboard could alert
        // on. Mirrors CombatLootEngine's _requestsFailed exactly (audit #17,
        // phase 1 - durable retry is a separate effort, audit #18).
        private static long _villageProductionFailures;

        /// <summary>
        /// How many offline village production grants have rolled back since
        /// the process started. Phase 1 (audit #17) stops at "visible and
        /// countable" - a dashboard/heartbeat integration the way
        /// <c>CombatLootEngine.ReportLootThroughput</c> reports its own
        /// counters is future work, not required by this task's Done-when.
        /// </summary>
        public static long VillageProductionFailures => Interlocked.Read(ref _villageProductionFailures);

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        // Grants offline wood/stone/iron_ore analytically, independent of
        // whatever gathering/combat activity was active while offline.
        // Modul: returns the total materials (across all four Log/Ore/RareLog/
        // RareOre grants) that a full warehouse discarded this call - see
        // GrantSingleCommodityProductionAsync's own comment. 0L on every early
        // return, since nothing was clamped when nothing was attempted.
        // internal rather than private: OfflineVillageProductionOverflowTests
        // drives this directly rather than through the full
        // ExtrapolateOfflineProgressAsync (which needs a whole populated
        // slot/character payload just to reach it) - the same seam
        // GrantAnalyticalLootAsync already uses for the same reason.
        internal static async Task<long> GrantVillagePassiveProductionAsync(FolkIdleDbContext db, long playerId, int lumberjackLevel, int mineLevel, int warehouseLevel, int townHallLevel, long elapsedSeconds)
        {
            if (elapsedSeconds <= 0)
            {
                return 0L;
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
            // Task 79: what that window ceiling discarded is lost to the
            // Warehouse cap too - it is the same storage figure. Recorded
            // against the common material, inside the transaction below.
            long woodClampedByWindow = elapsedSeconds * woodRatePerHour / 3600L - woodEarned;
            long oreClampedByWindow = elapsedSeconds * ironRatePerHour / 3600L - oreEarned;

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
                return 0L;
            }

            // Modul: summed inside the transaction, before commit - if the
            // transaction rolls back (see the catch below, which this task
            // does not touch - that failure path is task #17's own
            // counter/log), nothing was actually granted OR clamped, so
            // reporting a nonzero figure in that case would misattribute a
            // transient DB failure as "your warehouse was full". `committed`
            // gates the return on the same success path the catch's absence
            // of a rethrow already implies, without needing to read anything
            // out of the catch block itself.
            long materialsLostToFullWarehouse = 0L;
            bool committed = false;

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                if (woodEarned > 0)
                {
                    materialsLostToFullWarehouse += await GrantSingleCommodityProductionAsync(db, playerId, lumberjackMats.Log, woodEarned, maxStoragePerItem);
                }
                if (oreEarned > 0)
                {
                    materialsLostToFullWarehouse += await GrantSingleCommodityProductionAsync(db, playerId, mineMats.Ore, oreEarned, maxStoragePerItem);
                }
                if (rareWood > 0)
                {
                    materialsLostToFullWarehouse += await GrantSingleCommodityProductionAsync(db, playerId, lumberjackMats.RareLog, rareWood, maxStoragePerItem);
                }
                if (rareOre > 0)
                {
                    materialsLostToFullWarehouse += await GrantSingleCommodityProductionAsync(db, playerId, mineMats.RareOre, rareOre, maxStoragePerItem);
                }

                if (woodClampedByWindow > 0)
                {
                    await MaterialLedger.RecordAsync(db, playerId, MaterialFlowDirection.LostToWarehouseCap, lumberjackMats.Log, woodClampedByWindow);
                }
                if (oreClampedByWindow > 0)
                {
                    await MaterialLedger.RecordAsync(db, playerId, MaterialFlowDirection.LostToWarehouseCap, mineMats.Ore, oreClampedByWindow);
                }

                if (goldEarned > 0)
                {
                    // Modul: an upsert (CommodityLedger), task 44. What banks
                    // this gold is unchanged; only how the row is created.
                    await CommodityLedger.AddAsync(db, playerId, "gold", goldEarned);
                    await GoldLedger.RecordIncomeAsync(db, playerId, GoldIncomeSource.TownHall, goldEarned);
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                committed = true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Interlocked.Increment(ref _villageProductionFailures);
                Console.WriteLine(
                    $"Village: offline production for player {playerId} failed and was rolled back - "
                    + $"lost {goldEarned} gold, {woodEarned}+{rareWood} wood, {oreEarned}+{rareOre} ore: {ex.Message} - queued for retry.");

                // Modul: THE DURABLE RETRY OUTBOX (audit #18). Every delta
                // below is already a plain, fully-computed long by this
                // point - nothing here re-rolls anything, it only persists
                // the exact outcome the rolled-back transaction was trying
                // to write, so a retry replays it rather than recomputing it.
                var deltas = new Dictionary<string, long>();
                if (woodEarned > 0) deltas[lumberjackMats.Log] = woodEarned;
                if (oreEarned > 0) deltas[mineMats.Ore] = oreEarned;
                if (rareWood > 0) deltas[lumberjackMats.RareLog] = rareWood;
                if (rareOre > 0) deltas[mineMats.RareOre] = rareOre;
                // GoldLedger: counted when the outbox applies it (PendingGrantOutbox).
                if (goldEarned > 0) deltas["gold"] = goldEarned;

                await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                    db, playerId, PendingGrantSourceType.OfflineVillageProduction, deltas);
            }

            return committed ? materialsLostToFullWarehouse : 0L;
        }

        // Modul: returns what this call could NOT grant. This is the clamp
        // that matters to the player - the caller's own window-ceiling clamp
        // (elapsedSeconds * rate / 3600, capped at maxStoragePerItem) is a
        // theoretical bound that rarely binds; THIS one reflects what the
        // warehouse actually had room for, against live storage, at grant
        // time. The caller sums this across all four materials into
        // TickStatePayload.OfflineMaterialsLostToFullWarehouse, so a full
        // warehouse stops silently discarding production with no record.
        private static async Task<long> GrantSingleCommodityProductionAsync(FolkIdleDbContext db, long playerId, string itemId, long amountToGrant, long maxStorage)
        {
            if (amountToGrant <= 0) return 0L;

            var commodity = await db.CommodityRecords
                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, itemId)
                .SingleOrDefaultAsync();

            long currentStorage = commodity?.Quantity ?? 0L;
            long grantedAmount = Math.Min(amountToGrant, Math.Max(0L, maxStorage - currentStorage));
            long overflow = amountToGrant - grantedAmount;

            // Task 79: the material flow, in the caller's transaction.
            await MaterialLedger.RecordAsync(db, playerId, MaterialFlowDirection.LostToWarehouseCap, itemId, overflow);
            if (grantedAmount <= 0)
            {
                return overflow;
            }

            // Modul: the FOR UPDATE read above stays, because the storage cap
            // needs the current stack; the write is an upsert (task 44), since
            // FOR UPDATE on a row that does not exist yet locks nothing.
            await CommodityLedger.AddAsync(db, playerId, itemId, grantedAmount);
            await MaterialLedger.RecordAsync(db, playerId, MaterialFlowDirection.Gathered, itemId, grantedAmount);

            return overflow;
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
                + BloodlineBonuses.GatherSpeedBonusPct(payload.Aptitude_Skill, TraitTotals.From(payload.TraitMask)));

            double actionIntervalSeconds = requiredTicks / 10.0;
            double totalActionsDouble = elapsedSeconds / actionIntervalSeconds;

            long allowedActions = (long)Math.Min(totalActionsDouble, MaxOfflineGatherActions);
            double usedSeconds = allowedActions * actionIntervalSeconds;

            long masteryXpGained = allowedActions * node.BaseMasteryXpReward;
            ApplyGatheringMasteryXp(ref payload, node.ProfessionType, masteryXpGained);

            // Modul: yield traits (BloodlineBonuses) still scale roll
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
            CombatStats gatherProjectionStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, gatherProjectionAgePhase, payload.CompletedAreaFlags, gatherProjectionRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation, TraitTotals.From(payload.TraitMask), payload.CachedSetIds);
            double traitYieldFactor = 1.0 + BloodlineBonuses.GatherYieldBonusPct(TraitTotals.From(payload.TraitMask)) / 100.0;

            int lootRolls = (int)(allowedActions * payload.CachedCodexYieldMultiplier * traitYieldFactor);
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
            CombatStats combatStats = SimulationEngine.LiveCombatStats(in payload);

            OfflineCombatOutcome outcome = ProjectCombat(ref payload, fallbackId, elapsedSeconds);
            if (!outcome.CanDamage)
            {
                return new LootProjection(false, 0, 0);
            }

            double totalKillsDouble = outcome.KillsExact;
            long totalKills = outcome.Kills;

            // Funnel step 2, the offline half: a first kill made while away is
            // still a first kill. See FunnelRecorder.
            if (totalKills > 0) FunnelRecorder.Record(payload.PlayerId, FunnelStep.FirstKill);

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
                    kills: (int)plainKills, bonusRarityTiers: 0, skipMaterialRoll: true, source: DropSource.Offline));
            }
            if (fleeceKills > 0)
            {
                CombatLootEngine.DropRequestQueue.Enqueue(CombatLootDropRequest.Build(
                    in payload, in combatStats, fallbackId,
                    kills: (int)fleeceKills,
                    bonusRarityTiers: Domain.Combat.SimulationEngine.GoldenFleeceBonusTiers,
                    skipMaterialRoll: true,
                    source: DropSource.Offline));
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

        /// <summary>What an offline window of fighting came to, for one slot.</summary>
        internal readonly struct OfflineCombatOutcome
        {
            public bool CanDamage { get; init; }
            public long Kills { get; init; }
            /// <summary>The kill count as the loot roll count scales from it.</summary>
            public double KillsExact { get; init; }
            public double SecondsFought { get; init; }
            public bool Died { get; init; }
            public long FoodEaten { get; init; }
            public long XpGained { get; init; }
            public long GoldGained { get; init; }
        }

        /// <summary>
        /// Game time between re-derivations of the character. A level gained
        /// grows the health bar (and, with a lineage, the swing), so the fight
        /// is re-set-up at the next boundary, as the live tick would have fought
        /// on at the new level.
        /// </summary>
        private const int CombatStretchTicks = 3000;

        /// <summary>
        /// The fighting half of an offline window - kills, XP (levels included),
        /// gold, the larder, and a death - applied to the payload as the live
        /// tick would have applied them. Loot is the caller's.
        /// </summary>
        /// <remarks>
        /// Modul: OFFLINE IS THE LIVE FIGHT, IN EXPECTATION (owner, 2026-09-30:
        /// "offline and online must give the SAME results per hour").
        ///
        /// This was its own model, and every piece of it had drifted from
        /// RunCombatTick. Kill time came from CombatDamageModel's mean swing
        /// divided into the health, on the stat interval - no Relentless, no
        /// skill-tree crit, no Double Strike, no burn or set fire, and its own
        /// copy of the attack figure without the guild Damage buff or the legacy
        /// speed perk. A monster was charged a swing every interval, where the
        /// live tick restarts the monster's swing clock at every kill. A
        /// first-clear boss was priced at first-clear ATTACK for the whole
        /// window but farm HEALTH (ExpectedSecondsPerKill read the registry by
        /// id), and never marked beaten. Lifesteal and Bloodthirst healed
        /// nothing. The larder was a pool of health rather than bites at a
        /// threshold with a cooldown, and running out ended the window's
        /// earning without the death the live tick records. And XP took only
        /// the inheritance bonus - no global multiplier, event, mentors, Human
        /// mastery, legacy perk, skill tree or guild buff.
        ///
        /// Now the window is HuntingProjection's fight - the tick's own helpers,
        /// order and swing clock, each roll replaced by its expectation - run
        /// tick by tick for the whole window, and each kill pays through the
        /// same per-kill figures the live kill does (HuntingProjection.XpPerKill,
        /// CombatGoldReward.PerKill, LiveKillXpMultiplierPct for the seasonal
        /// pass). OfflineCombatParityTests runs the REAL RunCombatTick for an
        /// hour beside it and holds kills, XP, gold, food and survival to 5%.
        /// </remarks>
        internal static OfflineCombatOutcome ProjectCombat(ref TickStatePayload payload, int monsterId, long elapsedSeconds)
        {
            var setup = HuntingProjection.FightSetup.For(in payload, monsterId, timedEffects: true);
            if (!setup.CanDamage)
            {
                return default;
            }

            MonsterDefinition monster = setup.Monster;
            int startLevel = payload.CurrentLevel;
            long startXp = payload.CurrentXp;
            long foodBefore = (long)payload.Food1_Count + payload.Food2_Count + payload.Food3_Count;

            var state = HuntingProjection.FightState.Begin(in payload, in setup, payload.PlayerHp, withFood: true);

            // The server-wide XP terms the live kill reads when it lands.
            int globalXpMultiplier = GlobalEngineState.GlobalXpMultiplier;
            int globalEventId = SimulationEngine.ActiveGlobalEventId;

            long remainingTicks = Math.Max(0L, elapsedSeconds) * 10L;
            long totalGold = 0;
            long totalKills = 0;
            while (remainingTicks > 0 && !state.Died)
            {
                int stretch = (int)Math.Min(remainingTicks, CombatStretchTicks);
                long killsBefore = state.Kills;
                long ticksBefore = state.Ticks;
                HuntingProjection.Advance(ref state, in setup, stretch);
                remainingTicks -= state.Ticks - ticksBefore;

                long kills = state.Kills - killsBefore;
                if (kills <= 0) continue;
                totalKills += kills;

                // Each kill at the level it was made at - the stretch is short
                // enough that the mentorship term (level < 50) is the only
                // per-level input, and it moves once.
                long xpPerKill = HuntingProjection.XpPerKill(in payload, in monster, globalXpMultiplier, globalEventId);
                long seasonalPerKill = (long)monster.BaseXpReward * SimulationEngine.LiveKillXpMultiplierPct(in payload, globalXpMultiplier) / 100;
                SimulationEngine.AddSeasonalXp(ref payload, (int)Math.Min(int.MaxValue, seasonalPerKill * kills));
                QuestEngine.IncrementProgress(ref payload, QuestEngine.QuestTypeKillMonsters, (int)Math.Min(int.MaxValue, kills));
                totalGold += kills * CombatGoldReward.PerKill(in payload, in monster, setup.Stats.GoldAcquisitionMultiplierPct);

                int levelBefore = payload.CurrentLevel;
                ApplyCombatXp(ref payload, xpPerKill * kills);
                if (payload.CurrentLevel != levelBefore && remainingTicks > 0)
                {
                    // A bigger bar (and, for a lineage, a harder swing) from here on.
                    // The fight in progress carries over: health stays where it was.
                    var grown = HuntingProjection.FightSetup.For(in payload, monsterId, timedEffects: true);
                    if (grown.CanDamage) setup = grown;
                }
            }

            if (totalKills > 0)
            {
                // The location, the next region's door and a boss's first-clear
                // mark - what the live kill opens. Once is enough: every later
                // kill of the same monster opens nothing new.
                SimulationEngine.ApplyKillProgression(ref payload, monsterId);
            }

            if (totalGold > 0)
            {
                payload.AddGold(totalGold);
                payload.RedisPendingGoldDelta += totalGold;
                GoldLedger.TallyIncome(ref payload, GoldIncomeSource.CombatAway, totalGold);
                payload.RequiresRedisFlush = true;
            }

            payload.Food1_Count = state.Food1;
            payload.Food2_Count = state.Food2;
            payload.Food3_Count = state.Food3;

            int effectiveMaxHp = (int)setup.MaxMilliHp;
            if (state.DeathWardUsed)
            {
                // The ward is spent - through the live tick's own interception.
                ConsumableEngine.TryInterceptLethalDamage(ref payload, effectiveMaxHp);
            }

            if (state.Died)
            {
                // Modul: A DEATH AWAY IS A DEATH. The live tick ends the activity
                // and records it; the old projection simply stopped counting and
                // left the character deployed, to die again seconds after login.
                SimulationEngine.ApplyCombatDeath(ref payload, monsterId, effectiveMaxHp);
            }
            else
            {
                // The fight in progress is not carried across the login: the
                // character is back on the bar it had, against a fresh monster.
                payload.PlayerHp = (int)Math.Clamp(state.PlayerHp, 1.0, effectiveMaxHp);
                payload.CurrentMonsterId = 0;
                payload.CurrentMonsterHp = 0;
                payload.CombatTargetTickAccumulator = 0;
            }

            return new OfflineCombatOutcome
            {
                CanDamage = true,
                Kills = totalKills,
                KillsExact = totalKills,
                SecondsFought = state.Ticks / 10.0,
                Died = state.Died,
                FoodEaten = foodBefore - ((long)payload.Food1_Count + payload.Food2_Count + payload.Food3_Count),
                XpGained = XpBetween(startLevel, startXp, payload.CurrentLevel, payload.CurrentXp),
                GoldGained = totalGold,
            };
        }

        /// <summary>Total XP a character moved through, levels included.</summary>
        private static long XpBetween(int levelBefore, long xpBefore, int levelAfter, long xpAfter)
        {
            long total = -xpBefore;
            for (int level = levelBefore; level < levelAfter; level++) total += ProgressionEngine.GetRequiredXpForLevel(level);
            return total + xpAfter;
        }

        private static void ApplyCombatXp(ref TickStatePayload payload, long xpGained)
        {
            if (xpGained <= 0) return;

            // Modul 13.4.3: the mentorship penalty's -20% is already in the
            // figure - HuntingProjection.XpPerKill applies it per kill, with the
            // live kill's own truncation, as ProcessMonsterDeath does.
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
                CosmeticGrantEngine.NoteLevel(payload.PlayerId, payload.CurrentLevel);
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
        internal static async Task<int> GrantAnalyticalLootAsync(FolkIdleDbContext db, long playerId, LootTableEntry[] lootTable, int rollCount, int availableInventorySpace, float lootLuckPct = 0f, bool recordAsGathered = false)
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

            Dictionary<int, long> grantedQuantities = DrawLootCounts(lootTable, rollsToExecute, luckWeightBonus, Random.Shared);

            // Modul: one multi-row upsert (CommodityLedger.AddManyAsync), task
            // 44 - a single statement, so it is all-or-nothing even when a
            // caller holds no transaction.
            var materialDeltas = new List<KeyValuePair<string, long>>(grantedQuantities.Count);
            foreach (KeyValuePair<int, long> kvp in grantedQuantities)
            {
                string materialName = ContentRegistry.GetMaterialString(kvp.Key);
                if (materialName == "unknown")
                {
                    continue;
                }
                materialDeltas.Add(new KeyValuePair<string, long>(materialName, kvp.Value));
            }
            if (!recordAsGathered)
            {
                await CommodityLedger.AddManyAsync(db, playerId, materialDeltas);
                await db.SaveChangesAsync();
                return rollsToExecute;
            }

            // Modul: task 79. Gathering while away is gathering (a combat
            // projection's material drops are loot, not the material flow).
            // The ledger row is a SECOND statement, so the pair runs in a
            // transaction when the caller has none - otherwise a ledger
            // failure would throw out of the login AFTER the grant landed, and
            // the offline window (not yet stamped) would grant it again.
            var ownTransaction = db.Database.CurrentTransaction == null
                ? await db.Database.BeginTransactionAsync()
                : null;
            try
            {
                await CommodityLedger.AddManyAsync(db, playerId, materialDeltas);
                await MaterialLedger.RecordManyAsync(db, playerId, MaterialFlowDirection.Gathered, materialDeltas);
                await db.SaveChangesAsync();
                if (ownTransaction != null) await ownTransaction.CommitAsync();
            }
            finally
            {
                if (ownTransaction != null) await ownTransaction.DisposeAsync();
            }

            return rollsToExecute;
        }

        // Modul: ONE DRAW PER TABLE ENTRY, NOT ONE PER ROLL.
        //
        // Offline catch-up used to make up to MaxOfflineLootRolls (200,000)
        // weighted rolls one at a time, each a walk of the table - inside the
        // login path, before the player sees anything. What those rolls
        // produce is a multinomial: n rolls, entry i chosen with probability
        // w_i / W. The same distribution is drawn as a chain of binomials,
        // entry i taking Binomial(rolls still unassigned, w_i / weight still
        // unassigned), and the last entry with weight taking whatever is left.
        // So the counts still sum to exactly `rollCount` (the caps upstream
        // mean what they meant), each entry's expected count is still
        // n * w_i / W, and the work is one draw per entry whatever n is.
        //
        // Luck enters exactly as before - a flat bonus on every entry's weight
        // - and duplicates of one ItemId across entries still add up.
        internal static Dictionary<int, long> DrawLootCounts(LootTableEntry[] lootTable, int rollCount, int luckWeightBonus, Random rng)
        {
            var counts = new Dictionary<int, long>();
            if (lootTable.Length == 0 || rollCount <= 0)
            {
                return counts;
            }

            long remainingWeight = 0;
            int lastWeighted = -1;
            for (int i = 0; i < lootTable.Length; i++)
            {
                long w = (long)lootTable[i].Weight + luckWeightBonus;
                if (w > 0)
                {
                    remainingWeight += w;
                    lastWeighted = i;
                }
            }

            if (remainingWeight <= 0)
            {
                return counts;
            }

            int remainingRolls = rollCount;
            for (int i = 0; i <= lastWeighted && remainingRolls > 0; i++)
            {
                long w = (long)lootTable[i].Weight + luckWeightBonus;
                if (w <= 0)
                {
                    continue;
                }

                int drawn = i == lastWeighted
                    ? remainingRolls
                    : SampleBinomial(rng, remainingRolls, (double)w / remainingWeight);

                remainingWeight -= w;
                remainingRolls -= drawn;

                if (drawn > 0)
                {
                    counts.TryGetValue(lootTable[i].ItemId, out long existing);
                    counts[lootTable[i].ItemId] = existing + drawn;
                }
            }

            return counts;
        }

        // Below this many expected successes (on the rarer side) the draw is
        // exact inversion; above it a rounded normal, whose error at a mean of
        // 30+ is far under a single unit of a stack in the hundreds.
        private const double ExactBinomialMeanLimit = 30.0;

        // Modul: exact where it matters, cheap where it does not. The rare
        // entries - the ones a player actually reads on the welcome-back card -
        // have a small n*p and are drawn by exact CDF inversion, which takes
        // about n*p + 1 steps. Only a count expected in the dozens or more goes
        // through the normal approximation, and there a rounding difference of
        // one ore in a stack of hundreds is not observable. Mirrored around
        // p = 0.5 so the inversion always walks the short tail.
        internal static int SampleBinomial(Random rng, int n, double p)
        {
            if (n <= 0 || p <= 0.0) return 0;
            if (p >= 1.0) return n;

            if (p > 0.5)
            {
                return n - SampleBinomial(rng, n, 1.0 - p);
            }

            double mean = n * p;
            if (mean < ExactBinomialMeanLimit)
            {
                // P(0) = q^n, then P(k+1) = P(k) * (n-k)/(k+1) * p/q. With
                // mean < 30, q^n >= e^-(~31), so nothing underflows.
                double q = 1.0 - p;
                double ratio = p / q;
                double pk = Math.Exp(n * Math.Log(q));
                double u = rng.NextDouble();
                double cumulative = pk;
                int k = 0;
                while (u > cumulative && k < n)
                {
                    pk *= (double)(n - k) / (k + 1) * ratio;
                    k++;
                    cumulative += pk;
                    // Floating-point tail: the cdf can stall a hair under 1.
                    if (pk <= 0.0) break;
                }
                return k;
            }

            // Box-Muller; 1 - NextDouble() keeps the log argument off zero.
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double sample = Math.Round(mean + z * Math.Sqrt(mean * (1.0 - p)));
            if (sample < 0) return 0;
            if (sample > n) return n;
            return (int)sample;
        }
    }
}
