using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Network;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: moved out of SimulationEngine.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md).
    public partial class SimulationEngine
    {
        internal static void ProcessPassiveVillageTick(ref TickStatePayload payload, double deltaTimeSeconds, long nowEpoch)
        {
            // Modul 16: live completion of a matured upgrade for players who
            // are already online, so the progress bar/Upgrade button react at
            // the exact moment the timer elapses instead of only refreshing
            // on the player's next explicit action - VillageManagementEngine.
            // ResolveMaturedUpgradesAsync is still the DB-level source of
            // truth (reconciled before any new upgrade is granted), this is
            // purely a same-tick in-memory mirror of that same completion.
            if (payload.PendingUpgradeBuildingId != 0 && nowEpoch >= payload.PendingUpgradeCompletesAtEpoch)
            {
                ApplyMaturedUpgradeInMemory(ref payload);
            }

            // Modul: ONE PRODUCTION RULE, LIVE AND AWAY (owner decision
            // 2026-09-30). This tick produced the legacy "wood" and "iron_ore"
            // rows at 0.1 and 0.05 a second per level, checked against a
            // cached "wood" stock, while the offline window produced the
            // region's catalogued log and ore at (level + 1) x 100 an hour with
            // a tenth of it rare. The offline rule is the game: this tick now
            // asks VillageManagementEngine for the rate, the exact integer
            // accrual and the rare share - the functions the offline window
            // calls - and hands the units to VillageProductionQueue, whose
            // drain writes them with the material ledger, as the offline grant
            // does. Live production is not capped: the Warehouse stores only
            // what is made away (2026-11-07).
            //
            // Scholar is a rate here too (ScholarRate): every stream below
            // accrues at 1000 + its permille of the hourly rate.
            long ticks = (long)Math.Round(deltaTimeSeconds * 10.0);
            int ratePermille = 1000 + ScholarRate.BonusPermille(in payload);
            // The Warehouse's bonus is on the buildings' material, not on gold.
            int materialPermille = ratePermille + VillageManagementEngine.WarehouseBonusPermille(payload.WarehouseLevel);

            long logs = VillageManagementEngine.AccrueProduction(ref payload.LumberjackProductionAccumulator,
                VillageManagementEngine.ProductionRatePerHour(payload.LumberjackLevel), ticks, materialPermille);
            if (logs > 0)
            {
                long rare = VillageManagementEngine.RareShareOf(payload.VillageLogUnitsProduced, logs);
                payload.VillageLogUnitsProduced += logs;
                payload.PendingVillageRareLog += rare;
                payload.PendingVillageLog += logs - rare;
            }

            long ores = VillageManagementEngine.AccrueProduction(ref payload.MineProductionAccumulator,
                VillageManagementEngine.ProductionRatePerHour(payload.MineLevel), ticks, materialPermille);
            if (ores > 0)
            {
                long rare = VillageManagementEngine.RareShareOf(payload.VillageOreUnitsProduced, ores);
                payload.VillageOreUnitsProduced += ores;
                payload.PendingVillageRareOre += rare;
                payload.PendingVillageOre += ores - rare;
            }

            payload.VillageProductionTicksSinceGrant++;
            if (payload.VillageProductionTicksSinceGrant >= VillageProductionGrantIntervalTicks)
            {
                EnqueuePendingVillageProduction(ref payload);
            }

            // Modul: Economy Polish, Part 2. Town Hall passive gold on the
            // live tick, through the same accrual as the production above
            // (and as the offline window): an hour of ticks pays exactly one
            // hour's rate - integer arithmetic, no drift. Gold flows through
            // the same AddGold/RedisPendingGoldDelta channel combat gold uses,
            // landing in the CommodityRecords gold row at flush.
            long wholeGold = VillageManagementEngine.AccrueProduction(ref payload.TownHallGoldAccumulator,
                VillageManagementEngine.GetTownHallGoldRatePerHour(payload.TownHallLevel), ticks, ratePermille);
            if (wholeGold > 0L)
            {
                payload.AddGold(wholeGold);
                payload.RedisPendingGoldDelta += wholeGold;
                GoldLedger.TallyIncome(ref payload, GoldIncomeSource.TownHall, wholeGold);
                payload.RequiresRedisFlush = true;
                payload.IsDirty = true;
            }

            while (payload.AccumulatedStone >= 1.0f - ProductionAccumulatorEpsilon)
            {
                payload.AccumulatedStone -= 1.0f;
                payload.CachedStoneStock++;
                payload.PendingStoneDelta++;
                payload.IsDirty = true;
            }
        }

        /// <summary>
        /// Hands the payload's pending production to VillageProductionQueue.
        /// Called once a minute by the tick and once more when the session
        /// ends (RemoveActivePlayer), so nothing produced is left on a payload
        /// that is about to be discarded.
        /// </summary>
        internal static void EnqueuePendingVillageProduction(ref TickStatePayload payload)
        {
            payload.VillageProductionTicksSinceGrant = 0;
            if (payload.PendingVillageLog <= 0 && payload.PendingVillageRareLog <= 0
                && payload.PendingVillageOre <= 0 && payload.PendingVillageRareOre <= 0)
            {
                return;
            }

            VillageProductionQueue.Enqueue(new VillageProductionGrant
            {
                PlayerId = payload.PlayerId,
                LumberjackLevel = payload.LumberjackLevel,
                MineLevel = payload.MineLevel,
                WarehouseLevel = payload.WarehouseLevel,
                Log = payload.PendingVillageLog,
                RareLog = payload.PendingVillageRareLog,
                Ore = payload.PendingVillageOre,
                RareOre = payload.PendingVillageRareOre,
            });
            payload.PendingVillageLog = 0;
            payload.PendingVillageRareLog = 0;
            payload.PendingVillageOre = 0;
            payload.PendingVillageRareOre = 0;
        }

        // Modul 16: mirrors VillageManagementEngine's BuildingId -> cached
        // level field mapping (BuildInfrastructureNotificationAsync). Pure
        // struct field arithmetic, no allocations.
        private static void ApplyMaturedUpgradeInMemory(ref TickStatePayload payload)
        {
            switch (payload.PendingUpgradeBuildingId)
            {
                case VillageManagementEngine.ForgeBuildingId:
                    payload.ForgeLevel++;
                    payload.CachedCurrentToolTier = payload.ForgeLevel;
                    break;
                case VillageManagementEngine.InnBuildingId:
                    payload.InnLevel++;
                    payload.CachedMaxPopulationCapacity = VillageManagementEngine.CalculatePopulationCapacity(payload.InnLevel);
                    payload.CachedInnMaturationBonus = payload.InnLevel;
                    break;
                case VillageManagementEngine.BreedingGroundsBuildingId:
                    payload.BreedingLevel++;
                    break;
                case VillageManagementEngine.MentorshipAcademyBuildingId:
                    payload.AcademyLevel++;
                    break;
                case VillageManagementEngine.LumberjackBuildingId:
                    payload.LumberjackLevel++;
                    break;
                case VillageManagementEngine.MineBuildingId:
                    payload.MineLevel++;
                    break;
                case VillageManagementEngine.WarehouseBuildingId:
                    payload.WarehouseLevel++;
                    break;
                // Village 2: the Shrine's power counts from the moment the
                // timer ends, not from the next village refresh.
                case VillageManagementEngine.ShrineBuildingId:
                    if (payload.ShrineLevel < byte.MaxValue) payload.ShrineLevel++;
                    break;
            }

            payload.PendingUpgradeBuildingId = 0;
            payload.PendingUpgradeCompletesAtEpoch = 0;
            payload.IsDirty = true;
        }

        // Modul: A SLOT AGES ON ITS OWN WORK, NOT ON THE ACCOUNT'S (owner
        // decision 2026-10-21). This used to age all three slots whenever ANY
        // slot was working, so a child parked idle in slot 3 grew up on slot
        // 1's fight - and offline only slot 1 aged at all, so the same child
        // stayed a child for every hour the player was away. Now a character
        // ages exactly while its own slot has an activity, online and offline
        // alike (OfflineSimulationEngine applies the same rule per slot).
        private static bool ProcessAgeSlot(ref System.Guid characterId, ref long ageTicks, ref int agePhase, bool slotIsWorking)
        {
            if (characterId == System.Guid.Empty || !Engine.ChildGrowth.AgesThisTick(agePhase, slotIsWorking)) return false;
            
            ageTicks++;
            // Modul: the thresholds live in AgePhaseCurve now. They used to be
            // four literals here and four more in OfflineSimulationEngine,
            // under a comment promising the two would stay identical.
            int newPhase = AgePhaseCurve.PhaseFor(ageTicks);

            if (newPhase != agePhase)
            {
                agePhase = newPhase;
                return true;
            }
            return false;
        }

        // Modul: multi-slot simulation. Everything in the old ProcessSubTick
        // prologue that belongs to the ACCOUNT rather than to one character:
        // character aging, mana regeneration, potion/food buff countdowns, and
        // child maturation. It has to run exactly once per tick.
        //
        // With three characters now driving ProcessSubTick up to three times a
        // tick, leaving these here would have aged every character three times
        // as fast, tripled mana regen, and expired potions three times quicker
        // the moment a second slot was assigned - a silent, invisible speedup
        // of unrelated systems as a side effect of a UI action.
        //
        // The activity guard is preserved as "any slot is working": before
        // multi-slot, an idle player's potions did not tick down and their
        // characters did not age, and that behaviour is deliberately unchanged
        // rather than quietly fixed as part of this refactor.
        private static void ProcessAccountTick(ref TickStatePayload payload)
        {
            payload.TicksSinceLastFlush++;
            payload.IsDirty = true;

            bool stateFlashed = false;
            // Runs before ProcessAllSlotSubTicks swaps any register, so
            // ActiveActivityId is slot 1's own and Slot2/3Activity are theirs.
            int unlockedSlots = CharacterSlotEngine.GetUnlockedSlotCount(payload.TownHallLevel);
            stateFlashed |= ProcessAgeSlot(ref payload.Slot1_CharacterId, ref payload.Slot1_AgeTicks, ref payload.Slot1_AgePhase,
                payload.ActiveActivityId > 0);
            stateFlashed |= ProcessAgeSlot(ref payload.Slot2_CharacterId, ref payload.Slot2_AgeTicks, ref payload.Slot2_AgePhase,
                unlockedSlots > 1 && payload.Slot2Activity.ActiveActivityId > 0);
            stateFlashed |= ProcessAgeSlot(ref payload.Slot3_CharacterId, ref payload.Slot3_AgeTicks, ref payload.Slot3_AgePhase,
                unlockedSlots > 2 && payload.Slot3Activity.ActiveActivityId > 0);
            if (stateFlashed)
            {
                payload.IsDirty = true; // Flashes state to client implicitly via network loop
            }

            // Active Skill Tree: passive mana regen, unconditional like potion
            // duration below - runs regardless of gathering/combat activity
            // type so mana is topped up between casts.
            // Modul: mana regen removed with the skills that spent it. It was
            // never a constraint anyway - 10 a second against a rotation that
            // wanted 12 - which is part of why those skills were as strong as
            // they were.

            // Modul: Constitution made real. StatsCalculator has always
            // documented CON as granting "+0.1 Out-of-Combat HP Regen/sec" and
            // has always computed CombatStats.OutOfCombatHpRegen - and nothing
            // anywhere read it, so the stat was pure decoration.
            //
            // OUT of combat means exactly that: only while no activity is
            // running. Regenerating during a fight would silently undercut the
            // auto-eat larder, which is the intended sustain mechanic and the
            // thing every halt reason is built around.
            //
            // PlayerHp is milli-HP, and the stat is HP per SECOND at 10Hz, so
            // one tick is stat * 1000 / 10 = stat * 100.
            // Modul: OUT OF COMBAT MEANS OUT OF COMBAT.
            //
            // This used to require ActiveActivityId == 0 - completely idle. A
            // character chopping wood or standing at a bench is not fighting
            // anything, and healed at exactly zero. Combined with the second
            // half of this fix it meant a character who came out of a fight
            // hurt stayed hurt until the player noticed and stopped them doing
            // anything at all.
            if (!ActivityIdBands.IsCombatActivity(payload.ActiveActivityId))
            {
                // Resolved the same way the combat path does it - slot 1 is the
                // active character, and its race is the low byte of the
                // genetic vector.
                int regenAgePhase = payload.Slot1_AgePhase;
                int regenRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);

                CombatStats regenStats = StatsCalculator.Calculate(
                    payload.STR, payload.DEX, payload.CON, payload.LCK,
                    payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId,
                    regenAgePhase, payload.CompletedAreaFlags,
                    regenRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel,
                    payload.DraugrMasteryLevel, AffixFamilies.Totals(in payload), payload.IsEpicMutation,
                    TraitTotals.From(payload.TraitMask), payload.CachedSetIds);

                long regenMaxHp = 100000L + (regenStats.MaxHp * 1000L);
                if (payload.PlayerHp < regenMaxHp)
                {
                    // Modul: and there is always a baseline. The stat is zero
                    // for a character with no gear and no perks, so the whole
                    // branch used to be a no-op for exactly the players who
                    // most need it. Everyone recovers a share of their own
                    // maximum per second; the stat adds to that rather than
                    // being the entire supply of it.
                    long baselinePerTick = regenMaxHp / (BaselineOutOfCombatRegenSeconds * 10);
                    if (baselinePerTick < 1) baselinePerTick = 1;

                    payload.PlayerHp = HealClamped(payload.PlayerHp, baselinePerTick + (long)(regenStats.OutOfCombatHpRegen * 100f), regenMaxHp);
                    payload.IsDirty = true;
                }
            }

            // Modul: Deferred Part 5 Implementation, Part 2. The inline
            // offensive/defensive countdown moved into
            // ConsumableEngine.TickBuffCountdowns (unchanged semantics)
            // and gained the food-buff slot - one unit-testable,
            // zero-allocation method for all three expirations.
            ConsumableEngine.TickBuffCountdowns(ref payload);

            // Child Maturation Sub-tick (Breeding Loop)
            if (payload.ActiveChildMaturationMs > 0)
            {
                int decrementValue = (int)Math.Floor(100 * (1 + payload.CachedInnMaturationBonus * 0.20f));
                payload.ActiveChildMaturationMs -= decrementValue;
                if (payload.ActiveChildMaturationMs <= 0)
                {
                    payload.ActiveChildMaturationMs = 0;
                }
            }
        }

        /// <summary>
        /// How hard this character hits, in milli, before any per-swing roll.
        ///
        /// ONE AUTHORITY, deliberately. This used to live inline inside the
        /// player-attack branch, which was fine while the live tick was the
        /// only thing that swung. The world boss now needs the same figure -
        /// it used to take a damage number the CLIENT computed about itself -
        /// and two copies of "how hard does this player hit" is exactly the
        /// shape of defect this codebase keeps paying for.
        ///
        /// The crit multiplier is NOT applied here: it is rolled per swing and
        /// belongs to the swing, not to the character.
        /// </summary>
        internal static LineageDefinition LineageOf(in TickStatePayload payload)
        {
            int lineageId = payload.SelectedLineageId;
            if (lineageId < 0 || lineageId >= ProgressionEngine.Lineages.Length) lineageId = 0;
            return ProgressionEngine.Lineages[lineageId];
        }

        /// <summary>The active character's race: slot 1's genetic vector, low byte.</summary>
        internal static int ActiveRaceId(in TickStatePayload payload)
            => payload.Slot1_CharacterId != System.Guid.Empty ? (int)(payload.Slot1_GeneticVector & 0xFF) : 0;
    }
}
