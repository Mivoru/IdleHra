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

        /// <summary>
        /// The away-time cap a player really has: the universal 12 h, Vodnik's
        /// extension, and the minutes their built Great Works add (task 84).
        /// Modul: THE ONE COMPOSITION. The offline window below, the wire's
        /// OfflineCapSeconds and OfflineCapNotifier's email all call this, so the
        /// number the client shows, the number the mail names and the number the
        /// projection enforces cannot come apart. Great Works stack ON TOP of
        /// Vodnik (a bonus, not a competing cap) and are bounded by
        /// GreatWorksRegistry.MaxOfflineMinutes, which PowerCeilingTests holds.
        /// </summary>
        public static long EffectiveOfflineCapSeconds(int vodnikMasteryLevel, int greatWorksStagesPacked)
            => RaceMasteryResolver.GetVodnikExtendedOfflineSeconds(vodnikMasteryLevel, MaxOfflineSeconds)
               + Domain.Progression.GreatWorksRegistry.OfflineMinutes(greatWorksStagesPacked) * 60L;

        // Modul: what a projection resolved, as MATERIALS BY BASEID rather
        // than as a roll count against a loot table id (offline parity,
        // 2026-09-30). The roll count was granted through GetMaterialString,
        // which names six legacy slugs, so every catalogued id in a real loot
        // table came back "unknown" and was skipped: an offline window's
        // gathering and combat materials were silently thrown away, and the
        // welcome-back card still counted them as drops.
        internal readonly struct LootProjection
        {
            public readonly bool IsValid;
            /// <summary>What the window granted, by chest BaseId.</summary>
            public readonly Dictionary<string, long> MaterialDeltas;
            /// <summary>How many drops that is, for the welcome-back card (rolls won, not units).</summary>
            public readonly int Drops;
            /// <summary>Gathering: harvests completed. Combat: kills paid for.</summary>
            public readonly long Actions;
            /// <summary>Gathering: harvests PAID for, Scholar's extra ones included.</summary>
            public readonly long RewardActions;

            public LootProjection(bool isValid, Dictionary<string, long>? materialDeltas = null, int drops = 0, long actions = 0, long rewardActions = 0)
            {
                IsValid = isValid;
                MaterialDeltas = materialDeltas ?? new Dictionary<string, long>();
                Drops = drops;
                Actions = actions;
                RewardActions = rewardActions;
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

        // Task 85: how many fights one window may chain through automation
        // rules - one per rung of the 25-monster ladder, plus the fishing leg.
        private const int MaxAutomationLegs = 32;

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
            long effectiveMaxOfflineSeconds = EffectiveOfflineCapSeconds(payload.VodnikMasteryLevel, payload.GreatWorksStagesPacked);

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

            // Modul: Scholar, the Insight crown, is a RATE on the rewards now
            // (ScholarRate, owner decision 2026-09-30), applied by each
            // projection exactly as the live tick applies it - never to time.
            // It used to inflate this window's seconds instead, which paid it
            // only while away, fed the fight a quarter more time than the
            // player was gone, and ate a quarter more food doing so.
            // Everything below runs on the honest elapsedSeconds.
            long earningSeconds = elapsedSeconds;

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

            payload.OfflineMaterialsLostToFullWarehouse = await GrantVillagePassiveProductionAsync(db, payload.PlayerId, payload.LumberjackLevel, payload.MineLevel, payload.WarehouseLevel, payload.TownHallLevel, earningSeconds, ScholarRate.BonusPermille(payload.Skill_Scholar));

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

            // Modul: TWO PASSES - every other job first, crafting last
            // (offline parity, 2026-09-30). Live, a crafter and a gatherer run
            // side by side and a craft refused for want of materials is simply
            // retried at the next completion, so over a window the crafter
            // spends what it started with PLUS what the others brought in.
            // Crafting after them is that total; crafting first would starve
            // the crafter of everything its own village gathered overnight.
            for (int pass = 0; pass < 2; pass++)
            for (int slotIndex = 0; slotIndex < unlockedSlots; slotIndex++)
            {
                if (slotIndex > 0 && !SlotHoldsCharacter(ref payload, slotIndex))
                {
                    continue;
                }

                SimulationEngine.SwapSlotIntoActiveRegister(ref payload, slotIndex);
                try
                {
                    // Modul: the live tick's dispatch ORDER - crafting, then
                    // gathering, then combat (SimulationEngine.ProcessSubTick).
                    // This asked for a gathering node and otherwise ran the
                    // combat projection, so a crafter (activity 5000+, past the
                    // monster table) fought monster 1 all night and crafted
                    // nothing - PR #7's defect, still alive in this path.
                    bool isCraftingJob = ContentRegistry.TryGetRecipeByActivityId(payload.ActiveActivityId, out ContentRegistry.RecipeDefinition craftingRecipe);
                    if (isCraftingJob != (pass == 1))
                    {
                        continue;
                    }

                    long slotGoldBefore = payload.CurrentGold;
                    long slotXpBefore = payload.CurrentXp;
                    int slotDrops = 0;

                    if (isCraftingJob)
                    {
                        // Scholar: each completion pays one more with its
                        // chance, as the live job's tick does.
                        long attempts = ScholarRate.RewardUnits(Random.Shared,
                            earningSeconds * 10L / SimulationEngine.CraftTicksFor(in craftingRecipe), payload.Skill_Scholar);
                        var crafted = await CraftingEngine.ExecuteOfflineCraftsAsync(db, payload.PlayerId, craftingRecipe, attempts, Random.Shared);
                        // Mirrors CraftingTickCoordinator.DrainCraftingCompletions:
                        // the wire counter tracks what the engine just added to
                        // PlayerRecords.TotalItemsCrafted.
                        payload.LifetimeItemsCrafted += crafted.UnitsProduced;
                        slotDrops += ClampToInt(crafted.UnitsGranted);
                    }
                    else if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out GatheringNodeDefinition gatheringNode))
                    {
                        LootProjection projection = CalculateGatheringProjection(ref payload, gatheringNode, earningSeconds, Random.Shared);
                        slotDrops += projection.Drops;
                        await GrantMaterialDeltasAsync(db, payload.PlayerId, projection.MaterialDeltas, recordAsGathered: true);
                    }
                    else if (payload.ActiveActivityId > 0)
                    {
                        // Task 85: the fight, and wherever an automation rule
                        // sends the character after it - see ProjectCombatLegs.
                        foreach (OfflineLeg leg in ProjectCombatLegs(ref payload, earningSeconds, Random.Shared))
                        {
                            slotDrops += leg.Projection.Drops;
                            await GrantMaterialDeltasAsync(db, payload.PlayerId, leg.Projection.MaterialDeltas, recordAsGathered: leg.IsGathering);
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

        // Modul: one multi-row upsert (CommodityLedger.AddManyAsync), task
        // 44 - a single statement, so it is all-or-nothing even when a caller
        // holds no transaction. Gathering while away is gathering, so it is
        // also written to the material flow (task 79); a combat window's
        // materials are loot, exactly as CombatLootEngine writes a live kill's.
        internal static async Task GrantMaterialDeltasAsync(FolkIdleDbContext db, long playerId, Dictionary<string, long> deltas, bool recordAsGathered)
        {
            var materialDeltas = new List<KeyValuePair<string, long>>(deltas.Count);
            foreach (KeyValuePair<string, long> kvp in deltas)
            {
                if (kvp.Value > 0 && !string.IsNullOrEmpty(kvp.Key)) materialDeltas.Add(kvp);
            }
            if (materialDeltas.Count == 0)
            {
                return;
            }

            if (!recordAsGathered)
            {
                await CommodityLedger.AddManyAsync(db, playerId, materialDeltas);
                await db.SaveChangesAsync();
                return;
            }

            // Modul: task 79. The ledger row is a SECOND statement, so the pair
            // runs in a transaction when the caller has none - otherwise a
            // ledger failure would throw out of the login AFTER the grant
            // landed, and the offline window (not yet stamped) would grant it
            // again.
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
        internal static async Task<long> GrantVillagePassiveProductionAsync(FolkIdleDbContext db, long playerId, int lumberjackLevel, int mineLevel, int warehouseLevel, int townHallLevel, long elapsedSeconds, int scholarBonusPermille = 0)
        {
            if (elapsedSeconds <= 0)
            {
                return 0L;
            }

            // Modul: THE RULE THE LIVE TICK CALLS (owner decision 2026-09-30).
            // Rates, the tick-exact accrual, the rare share and the Warehouse
            // cap are all VillageManagementEngine's, and
            // SimulationEngine.ProcessPassiveVillageTick asks the same four
            // functions a tenth of a second at a time - so an hour away pays
            // what an hour watched pays, to the unit.
            //
            // Scholar is a RATE here as it is live (ScholarRate): 1000 + its
            // permille of each hourly rate, over the honest elapsed time.
            long ticks = elapsedSeconds * 10L;
            int ratePermille = 1000 + scholarBonusPermille;
            long goldAccumulator = 0L, woodAccumulator = 0L, oreAccumulator = 0L;
            long goldEarned = VillageManagementEngine.AccrueProduction(ref goldAccumulator,
                VillageManagementEngine.GetTownHallGoldRatePerHour(townHallLevel), ticks, ratePermille);
            long woodEarned = VillageManagementEngine.AccrueProduction(ref woodAccumulator,
                VillageManagementEngine.ProductionRatePerHour(lumberjackLevel), ticks, ratePermille);
            long oreEarned = VillageManagementEngine.AccrueProduction(ref oreAccumulator,
                VillageManagementEngine.ProductionRatePerHour(mineLevel), ticks, ratePermille);

            // Modul: THE WINDOW CEILING IS GONE, 2026-09-30. This clamped the
            // whole window's production to one Warehouse's worth BEFORE the
            // rare split and before the per-material cap below - a second,
            // stricter cap the live building never had. Live, the common
            // material fills to the cap and the rare one keeps arriving; with
            // the ceiling, offline stopped both at 90% / 10% of one cap. The
            // Warehouse cap below, against what is actually stored, is the rule.
            //
            // A SHARE OF THE YIELD IS THE TIER'S RARE MATERIAL, in the same
            // 90/10 the gathering loot tables use for the same pairs. Split
            // rather than added: the throughput is unchanged and a tenth of it
            // arrives as the better material.
            long rareWood = VillageManagementEngine.RareShareOf(0L, woodEarned);
            long rareOre = VillageManagementEngine.RareShareOf(0L, oreEarned);
            woodEarned -= rareWood;
            oreEarned -= rareOre;

            bool anyMaterialProduction = woodEarned > 0 || oreEarned > 0 || rareWood > 0 || rareOre > 0;
            if (!anyMaterialProduction && goldEarned <= 0)
            {
                return 0L;
            }

            var lumberjackMats = VillageManagementEngine.GetTierMaterials(lumberjackLevel);
            var mineMats = VillageManagementEngine.GetTierMaterials(mineLevel);

            // Modul: summed inside the transaction, before commit - if the
            // transaction rolls back, nothing was actually granted OR clamped,
            // so reporting a nonzero figure in that case would misattribute a
            // transient DB failure as "your warehouse was full".
            long materialsLostToFullWarehouse = 0L;
            bool committed = false;

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                materialsLostToFullWarehouse = await VillageManagementEngine.GrantProductionAsync(
                    db, playerId, lumberjackLevel, mineLevel, warehouseLevel, woodEarned, rareWood, oreEarned, rareOre);

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

        // Modul: THE LIVE HARVEST, IN EXPECTATION AND IN BULK (offline parity,
        // 2026-09-30). Every term is asked of the function the live tick
        // calls - SimulationEngine.RequiredGatherTicks for the speed,
        // GatheringYieldFor for the roll count and luck, RollGatherQuantity
        // for each winner's stack - so there is no second copy here to drift.
        //
        // What this used to do instead: roll count = actions x codex yield x
        // yield trait, which dropped the monolith, both race bonuses, the
        // Golden Harvest event and the global drop multiplier; one unit per
        // roll whatever the entry's authored range; and a grant through
        // GetMaterialString, under which every real gathering item was
        // "unknown" and skipped. A night of gathering paid mastery XP and no
        // materials at all.
        //
        // Internal, and taking its Random, so OfflineLootParityTests can hold
        // it against the live tick directly.
        internal static LootProjection CalculateGatheringProjection(ref TickStatePayload payload, GatheringNodeDefinition node, long elapsedSeconds, Random rng)
        {
            // Modul: MASTERY RISES DURING THE WINDOW, and it speeds the node up.
            // The live tick re-reads the speed on every harvest, so a level
            // gained at 02:00 makes the rest of the night faster; projecting
            // the whole window at the level the player logged out with paid
            // the slow rate for all of it. The window is walked one mastery
            // level at a time instead - a handful of steps, not a tick loop.
            long remainingTicks = elapsedSeconds * 10L;
            long allowedActions = 0;
            // Scholar: harvests that PAY - each completed harvest pays one more
            // with the crown's chance (ScholarRate), mastery XP and rolls alike,
            // exactly as RunGatheringTick pays it.
            long rewardActions = 0;
            int scholarPermille = ScholarRate.BonusPermille(payload.Skill_Scholar);
            while (remainingTicks > 0 && allowedActions < MaxOfflineGatherActions)
            {
                int requiredTicks = Domain.Combat.SimulationEngine.RequiredGatherTicks(ref payload, in node);
                if (requiredTicks <= 0) break;

                long affordable = Math.Min(remainingTicks / requiredTicks, MaxOfflineGatherActions - allowedActions);
                if (affordable <= 0) break;

                long step = affordable;
                if (node.BaseMasteryXpReward > 0)
                {
                    long xpToNext = Domain.Combat.SimulationEngine.MasteryXpToNextLevel(ref payload, node.ProfessionType);
                    long xpPerActionPermille = (long)node.BaseMasteryXpReward * (1000 + scholarPermille);
                    long actionsToNext = (xpToNext * 1000L + xpPerActionPermille - 1) / xpPerActionPermille;
                    step = Math.Min(affordable, Math.Max(1L, actionsToNext));
                }

                long rewardStep = ScholarRate.RewardUnits(rng, step, payload.Skill_Scholar);
                ApplyGatheringMasteryXp(ref payload, node.ProfessionType, rewardStep * node.BaseMasteryXpReward);
                allowedActions += step;
                rewardActions += rewardStep;
                remainingTicks -= step * requiredTicks;
            }

            LootTableEntry[] lootTable = ContentRegistry.GetLootTable(node.ActivityId).ToArray();
            if (allowedActions <= 0 || lootTable.Length == 0)
            {
                return new LootProjection(true, actions: allowedActions, rewardActions: rewardActions);
            }

            Domain.Combat.SimulationEngine.GatheringYield yield =
                Domain.Combat.SimulationEngine.GatheringYieldFor(ref payload, in node, GlobalEngineState.GlobalDropMultiplier);

            // The live harvest makes MultiplierPct / 100 rolls, plus one more
            // with probability (MultiplierPct % 100) / 100. Over n harvests
            // that is n * whole + Binomial(n, fraction): the same distribution,
            // drawn once.
            long whole = yield.MultiplierPct / 100;
            double fraction = (yield.MultiplierPct % 100) / 100.0;
            int cappedActions = (int)Math.Min(rewardActions, int.MaxValue);
            long rolls = rewardActions * whole + SampleBinomial(rng, cappedActions, fraction);
            int rollCount = (int)Math.Min(rolls, MaxOfflineLootRolls);

            Dictionary<string, long> deltas = DrawGatheringMaterials(lootTable, rollCount, yield.LuckWeightBonus, rng);
            return new LootProjection(true, deltas, rollCount, allowedActions, rewardActions);
        }

        /// <summary>
        /// <paramref name="rollCount"/> live gathering rolls against one table:
        /// which entry each roll picks (weight + the flat luck bonus, drawn as
        /// a multinomial by <see cref="DrawLootCountsByEntry"/>) and the stack
        /// each winner grants (<c>SimulationEngine.RollGatherQuantity</c>),
        /// summed by chest BaseId - the key the live grant writes
        /// (CombatLootEngine.GrantGatheredMaterialsAsync resolves through
        /// GetItemBaseId).
        /// </summary>
        internal static Dictionary<string, long> DrawGatheringMaterials(LootTableEntry[] lootTable, int rollCount, int luckWeightBonus, Random rng)
        {
            var deltas = new Dictionary<string, long>();
            long[] perEntry = DrawLootCountsByEntry(lootTable, rollCount, luckWeightBonus, rng);
            for (int i = 0; i < perEntry.Length; i++)
            {
                if (perEntry[i] <= 0) continue;
                string baseId = ContentRegistry.GetItemBaseId(lootTable[i].ItemId);
                if (string.IsNullOrEmpty(baseId)) continue;

                long quantity;
                if (lootTable[i].MaxQuantity > lootTable[i].MinQuantity)
                {
                    quantity = 0;
                    for (long k = 0; k < perEntry[i]; k++)
                    {
                        quantity += Domain.Combat.SimulationEngine.RollGatherQuantity(rng, in lootTable[i]);
                    }
                }
                else
                {
                    quantity = perEntry[i];
                }

                deltas.TryGetValue(baseId, out long existing);
                deltas[baseId] = existing + quantity;
            }
            return deltas;
        }

        /// <summary>One stretch of an offline window spent on one activity.</summary>
        internal readonly struct OfflineLeg
        {
            public LootProjection Projection { get; init; }
            /// <summary>A gathering leg (the fishing an automation rule sent the character to).</summary>
            public bool IsGathering { get; init; }
            public long ActivityId { get; init; }
            public long Seconds { get; init; }
        }

        /// <summary>
        /// A combat slot's offline window: the fight, and - when an automation
        /// rule moved the character - the rest of the window where it went.
        /// Mutates the payload as the live tick would; grants nothing (the
        /// caller writes each leg's materials). Internal and taking its Random
        /// so AutomationRuleParityTests can hold it against the live tick.
        /// </summary>
        /// <remarks>
        /// Modul: task 85 - A WINDOW IS A SEQUENCE OF LEGS. A fight ends at the
        /// window's end, a death, or (with the fishing rule) a dry larder. The
        /// rules themselves act INSIDE ProjectCombat, through the functions the
        /// live tick calls (ApplyCombatDeath's step-down, TryGoFishing), so this
        /// loop only asks where the character is now and spends the rest of the
        /// window there: another fight one monster down, or the fishing spot.
        /// Without a rule the first leg is the whole window, exactly as before.
        /// Bounded by MaxAutomationLegs - the ladder has 25 rungs.
        /// </remarks>
        internal static List<OfflineLeg> ProjectCombatLegs(ref TickStatePayload payload, long elapsedSeconds, Random rng)
        {
            var legs = new List<OfflineLeg>(1);
            long remainingSeconds = elapsedSeconds;
            for (int leg = 0; leg < MaxAutomationLegs && remainingSeconds > 0; leg++)
            {
                long activityBefore = payload.ActiveActivityId;
                LootProjection projection = CalculateCombatProjection(ref payload, remainingSeconds, out long secondsFought);
                if (!projection.IsValid)
                {
                    break;
                }

                legs.Add(new OfflineLeg { Projection = projection, ActivityId = activityBefore, Seconds = Math.Min(remainingSeconds, secondsFought) });
                remainingSeconds -= Math.Max(1L, secondsFought);
                if (payload.ActiveActivityId <= 0 || payload.ActiveActivityId == activityBefore)
                {
                    break;
                }

                if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out GatheringNodeDefinition fishingNode))
                {
                    if (remainingSeconds > 0)
                    {
                        legs.Add(new OfflineLeg
                        {
                            Projection = CalculateGatheringProjection(ref payload, fishingNode, remainingSeconds, rng),
                            IsGathering = true,
                            ActivityId = fishingNode.ActivityId,
                            Seconds = remainingSeconds,
                        });
                    }
                    break;
                }
            }
            return legs;
        }

        private static LootProjection CalculateCombatProjection(ref TickStatePayload payload, long elapsedSeconds, out long secondsFought)
        {
            secondsFought = elapsedSeconds;
            int fallbackId = payload.ActiveActivityId > ContentRegistry.Monsters.Length ? 1 : (int)payload.ActiveActivityId;
            if (fallbackId <= 0 || fallbackId > ContentRegistry.Monsters.Length)
            {
                return new LootProjection(false);
            }

            CombatStats combatStats = SimulationEngine.LiveCombatStats(in payload);

            OfflineCombatOutcome outcome = ProjectCombat(ref payload, fallbackId, elapsedSeconds);
            if (!outcome.CanDamage)
            {
                return new LootProjection(false);
            }

            // Whole seconds the fight used, rounded up so a leg that follows
            // never gets time this one already spent.
            secondsFought = (long)Math.Ceiling(outcome.SecondsFought);

            long totalKills = outcome.Kills;

            // Funnel step 2, the offline half: a first kill made while away is
            // still a first kill. See FunnelRecorder.
            if (totalKills > 0) FunnelRecorder.Record(payload.PlayerId, FunnelStep.FirstKill);

            // Everything a kill drops, for this window's kills - see
            // ProjectCombatLoot. The kill COUNT is this method's; what each
            // kill pays is the live kill's.
            return ProjectCombatLoot(ref payload, in combatStats, fallbackId, outcome.PaidKills, Random.Shared);
        }

        /// <summary>
        /// What <paramref name="totalKills"/> kills of one monster drop, by the
        /// live kill's own rules - every stream a live kill produces besides
        /// its gold and XP.
        /// </summary>
        /// <remarks>
        /// Modul: OFFLINE PARITY, owner rule 2026-09-30: offline and online give
        /// the same per hour, drops included. Per stream:
        ///
        /// EQUIPMENT, the boss guarantee and COSMETIC CHESTS - one
        /// CombatLootDropRequest carrying the window's kills, built by the same
        /// CombatLootDropRequest.Build the live tick uses, rolled by the same
        /// kill loop in CombatLootEngine. It used to enqueue one request per
        /// kill capped at 500, which is 25 pieces however long you were away;
        /// the loot engine now rolls the whole window in one transaction.
        ///
        /// MATERIALS - CombatLootEngine.RollMaterialsForKills, the live kill
        /// loop's own gate and pick. This path used to grant
        /// kills x codex yield x global drop multiplier rolls, one unit each,
        /// through GetMaterialString - three differences from a live kill, and
        /// the last one discarded every monster material as "unknown". Rolled
        /// here at login rather than on the loot engine's thread (the requests
        /// skip their material roll) so the welcome-back card can count them.
        ///
        /// DIAMONDS - Binomial(kills, OrdinaryKillDiamondChance), bosses none;
        /// the offline catch-up paid none at all before.
        ///
        /// CODEX - one KillEvent carrying the window's kills, so codex levels,
        /// region completion, the race unlock and the first-clear trophy all
        /// see a kill made while away. They saw none.
        ///
        /// Internal and taking its Random so OfflineLootParityTests can drive it.
        /// </remarks>
        internal static LootProjection ProjectCombatLoot(ref TickStatePayload payload, in CombatStats combatStats, int monsterId, long totalKills, Random rng)
        {
            if (totalKills <= 0 || monsterId < 1 || monsterId > ContentRegistry.Monsters.Length)
            {
                return new LootProjection(true);
            }

            MonsterDefinition monster = ContentRegistry.Monsters[monsterId - 1];

            // Modul: a RUNAWAY GUARD on how many kills one login may hand the
            // loot engine - see MaxOfflineKillsPerSlot. Every stream below uses
            // this same count, so none of them can disagree about the window.
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

            CombatLootDropRequest plainRequest = CombatLootDropRequest.Build(
                in payload, in combatStats, monsterId,
                kills: (int)plainKills, bonusRarityTiers: 0, skipMaterialRoll: true, source: DropSource.Offline);
            if (plainKills > 0)
            {
                CombatLootEngine.DropRequestQueue.Enqueue(plainRequest);
            }
            if (fleeceKills > 0)
            {
                CombatLootEngine.DropRequestQueue.Enqueue(CombatLootDropRequest.Build(
                    in payload, in combatStats, monsterId,
                    kills: (int)fleeceKills,
                    bonusRarityTiers: Domain.Combat.SimulationEngine.GoldenFleeceBonusTiers,
                    skipMaterialRoll: true,
                    source: DropSource.Offline));
            }

            // Materials: the live roll, per kill. Plenty comes off the request
            // Build composed, so it is the figure a live kill's request carries.
            var materialDeltas = new Dictionary<string, long>();
            int materialDrops = CombatLootEngine.RollMaterialsForKills(
                rng, monsterId, killsToRoll, plainRequest.MaterialQuantityPct, materialDeltas);

            // Diamonds: the same chance a live ordinary kill rolls, drawn once
            // for the window.
            if (Domain.Combat.SimulationEngine.KillCanPayDiamond(monsterId))
            {
                long diamonds = SampleBinomial(rng, (int)killsToRoll, Domain.Combat.SimulationEngine.OrdinaryKillDiamondChance);
                if (diamonds > 0)
                {
                    payload.SetPremiumCurrency((int)Math.Min(int.MaxValue, (long)payload.PremiumCurrency + diamonds));
                    payload.IsDirty = true;
                }
            }

            // Codex: the live kill's KillEvent, carrying the window. GainedXp is
            // what the live kill sends per kill (the seasonal figure,
            // BaseXpReward at the live XP multiplier), times the kills.
            int codexRaceId = payload.Slot1_CharacterId != Guid.Empty ? (int)(payload.Slot1_GeneticVector & 0xFF) : 0;
            int finalXpMultiplier = Domain.Combat.SimulationEngine.LiveKillXpMultiplierPct(in payload, GlobalEngineState.GlobalXpMultiplier);
            long seasonalXpPerKill = (long)monster.BaseXpReward * finalXpMultiplier / 100L;
            CodexEngine.KillEventQueue.Enqueue(new KillEvent
            {
                PlayerId = payload.PlayerId,
                MonsterId = monsterId,
                RaceId = codexRaceId,
                GainedXp = seasonalXpPerKill * killsToRoll,
                Kills = (int)killsToRoll,
            });

            // Modul: the equipment component of the drop count is 0, not a
            // guess. Equipment is rolled later, on CombatLootEngine's own
            // thread, so nothing here knows how many pieces fell. It used to
            // report the REQUEST count, which overstated the truth twentyfold.
            return new LootProjection(true, materialDeltas, materialDrops, killsToRoll);
        }

        /// <summary>What an offline window of fighting came to, for one slot.</summary>
        internal readonly struct OfflineCombatOutcome
        {
            public bool CanDamage { get; init; }
            public long Kills { get; init; }
            /// <summary>
            /// The kills PAID for - Kills with Scholar's per-kill draw applied,
            /// as the live kill pays it. XP and gold here, and every drop in
            /// ProjectCombatLoot, count these; the fight counts Kills.
            /// </summary>
            public long PaidKills { get; init; }
            /// <summary>The kill count as the loot roll count scales from it.</summary>
            public double KillsExact { get; init; }
            public double SecondsFought { get; init; }
            public bool Died { get; init; }
            /// <summary>The larder ran dry mid-fight (the live OutOfFood).</summary>
            public bool Starved { get; init; }
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

            // Task 85: a character that will leave for the fishing spot when
            // the larder runs dry stops fighting at that tick, as live.
            state.StopWhenStarved = AutomationRules.CanGoFishing(in payload, out _);

            // The server-wide XP terms the live kill reads when it lands.
            int globalXpMultiplier = GlobalEngineState.GlobalXpMultiplier;
            int globalEventId = SimulationEngine.ActiveGlobalEventId;

            long remainingTicks = Math.Max(0L, elapsedSeconds) * 10L;
            long totalGold = 0;
            long totalKills = 0;
            long totalPaidKills = 0;
            while (remainingTicks > 0 && !state.Died && !(state.Starved && state.StopWhenStarved))
            {
                int stretch = (int)Math.Min(remainingTicks, CombatStretchTicks);
                long killsBefore = state.Kills;
                long hungryBefore = state.HungryKills;
                long ticksBefore = state.Ticks;
                HuntingProjection.Advance(ref state, in setup, stretch);
                remainingTicks -= state.Ticks - ticksBefore;

                long kills = state.Kills - killsBefore;
                if (kills <= 0) continue;
                totalKills += kills;

                // Modul: SCHOLAR PAYS PER KILL, as the live kill does
                // (ScholarRate): XP, gold and every drop count paid kills; the
                // fight itself (time, food, a death) ran on the real ones.
                long paidKills = ScholarRate.RewardUnits(Random.Shared, kills, payload.Skill_Scholar);
                totalPaidKills += paidKills;

                // Rations: the kills made hungry pay HungryRewardPct, as the
                // live kill does. Scaled as a share of the stretch's pay so the
                // Scholar draw above stays one draw.
                long hungryKills = state.HungryKills - hungryBefore;
                long payNumerator = (kills - hungryKills) * 100 + hungryKills * FoodRegistry.HungryRewardPct;
                long payDenominator = kills * 100;

                // Each kill at the level it was made at - the stretch is short
                // enough that the mentorship term (level < 50) is the only
                // per-level input, and it moves once.
                long xpPerKill = HuntingProjection.XpPerKill(in payload, in monster, globalXpMultiplier, globalEventId);
                long seasonalPerKill = (long)monster.BaseXpReward * SimulationEngine.LiveKillXpMultiplierPct(in payload, globalXpMultiplier) / 100;
                SimulationEngine.AddSeasonalXp(ref payload, (int)Math.Min(int.MaxValue, seasonalPerKill * paidKills * payNumerator / payDenominator));
                QuestEngine.IncrementProgress(ref payload, QuestEngine.QuestTypeKillMonsters, (int)Math.Min(int.MaxValue, kills));
                totalGold += paidKills * CombatGoldReward.PerKill(in payload, in monster, setup.Stats.GoldAcquisitionMultiplierPct) * payNumerator / payDenominator;

                int levelBefore = payload.CurrentLevel;
                ApplyCombatXp(ref payload, xpPerKill * paidKills * payNumerator / payDenominator);
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
            payload.RationTicksSinceMeal = state.RationTicks;
            payload.Hungry = state.Hungry;

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

                if (state.Starved)
                {
                    // The live auto-eat's warning, raised where it would have
                    // been - and then, with the rule, the live combat tick's
                    // answer to it (RunCombatTick calls the same function).
                    payload.ActivityHaltReason = Network.ActivityHaltReason.OutOfFood;
                    if (state.StopWhenStarved)
                    {
                        AutomationRules.TryGoFishing(ref payload);
                    }
                }
            }

            return new OfflineCombatOutcome
            {
                CanDamage = true,
                Kills = totalKills,
                PaidKills = totalPaidKills,
                KillsExact = totalKills,
                SecondsFought = state.Ticks / 10.0,
                Died = state.Died,
                Starved = state.Starved,
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

        // Isolated so it can be tested directly against a hand-built loot table.
        //
        // Modul: LootLuckPct does not scale rollCount (that inflated the
        // absolute volume of every entry, common trash and rare drops alike,
        // in fixed proportion). It adds a flat weight bonus to every entry's
        // selection weight - GatheringYield.LuckWeightBonusFor, the live
        // harvest's own conversion - so higher luck shifts the selection
        // toward rare drops without changing the total number of rolls.
        //
        // Modul: THE CATALOGUE, NOT THE SIX SLUGS (offline parity,
        // 2026-09-30). Ids resolve through GetItemBaseId, the key the live
        // gathering grant writes. They went through GetMaterialString, which
        // knows ids 1-6 as legacy slugs and everything else as "unknown", so
        // every item in every real gathering table was skipped - and ids 1-6
        // landed in the wrong namespace. The tests that pinned that id space
        // had pinned the defect.
        internal static async Task<int> GrantAnalyticalLootAsync(FolkIdleDbContext db, long playerId, LootTableEntry[] lootTable, int rollCount, int availableInventorySpace, float lootLuckPct = 0f, bool recordAsGathered = false)
        {
            if (lootTable.Length == 0 || rollCount <= 0 || availableInventorySpace <= 0)
            {
                return 0;
            }

            int rollsToExecute = Math.Min(rollCount, availableInventorySpace);
            int luckWeightBonus = Domain.Combat.SimulationEngine.GatheringYield.LuckWeightBonusFor(lootLuckPct);

            Dictionary<string, long> deltas = DrawGatheringMaterials(lootTable, rollsToExecute, luckWeightBonus, Random.Shared);
            if (deltas.Count == 0)
            {
                return 0;
            }

            await GrantMaterialDeltasAsync(db, playerId, deltas, recordAsGathered);
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
            long[] perEntry = DrawLootCountsByEntry(lootTable, rollCount, luckWeightBonus, rng);
            for (int i = 0; i < perEntry.Length; i++)
            {
                if (perEntry[i] <= 0) continue;
                counts.TryGetValue(lootTable[i].ItemId, out long existing);
                counts[lootTable[i].ItemId] = existing + perEntry[i];
            }
            return counts;
        }

        // The same draw, kept per ENTRY rather than per ItemId, because two
        // entries for one item can carry different quantity ranges and the
        // stack each winner grants belongs to its entry.
        internal static long[] DrawLootCountsByEntry(LootTableEntry[] lootTable, int rollCount, int luckWeightBonus, Random rng)
        {
            var counts = new long[lootTable.Length];
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
                counts[i] += drawn;
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
