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
        private static void WriteParkedSlotBars(ref StateUpdatePacket packet, in TickStatePayload payload)
        {
            WriteSlotBars(in payload.Slot2Activity, in payload, out packet.Slot2PlayerHp, out packet.Slot2PlayerMaxHp,
                out packet.Slot2MonsterId, out packet.Slot2MonsterHp, out packet.Slot2MonsterMaxHp,
                out packet.Slot2WorkProgressTicks, out packet.Slot2WorkRequiredTicks);
            WriteSlotBars(in payload.Slot3Activity, in payload, out packet.Slot3PlayerHp, out packet.Slot3PlayerMaxHp,
                out packet.Slot3MonsterId, out packet.Slot3MonsterHp, out packet.Slot3MonsterMaxHp,
                out packet.Slot3WorkProgressTicks, out packet.Slot3WorkRequiredTicks);
        }

        private static void WriteSlotBars(
            in CharacterActivityState slot, in TickStatePayload payload,
            out int playerHp, out int playerMaxHp, out ushort monsterId, out int monsterHp, out int monsterMaxHp,
            out ushort progressTicks, out ushort requiredTicks)
        {
            playerHp = (int)Math.Min(int.MaxValue, slot.PlayerHp / 1000L);
            playerMaxHp = (int)(slot.CachedEffectiveMaxHp / 1000L);
            monsterId = (ushort)Math.Clamp(slot.CurrentMonsterId, 0, ushort.MaxValue);
            monsterHp = (int)(slot.CurrentMonsterHp / 1000L);
            monsterMaxHp = slot.CurrentMonsterId > 0
                ? (int)BossFirstClearRules.MaxHpFor(payload.DefeatedRegionBossMask, slot.CurrentMonsterId, BossFirstClearRules.ReliefFor(in payload))
                : 0;
            progressTicks = (ushort)Math.Clamp(slot.GatheringProgressTicks, 0, ushort.MaxValue);
            requiredTicks = (ushort)Math.Clamp(slot.RequiredProgressTicks, 0, ushort.MaxValue);
        }

        // Each fielded slot's activity, slot order, read without swapping.
        internal static long[] LiveSlotActivities(ref TickStatePayload payload)
        {
            return new long[]
            {
                payload.ActiveActivityId,
                payload.Slot2_CharacterId != System.Guid.Empty ? payload.Slot2Activity.ActiveActivityId : 0L,
                payload.Slot3_CharacterId != System.Guid.Empty ? payload.Slot3Activity.ActiveActivityId : 0L
            };
        }

        private static int ResolveSlotIndexForCharacter(ref TickStatePayload payload, System.Guid characterId)
        {
            if (characterId == System.Guid.Empty) return -1;
            if (payload.Slot1_CharacterId == characterId) return 0;
            if (payload.Slot2_CharacterId == characterId) return 1;
            if (payload.Slot3_CharacterId == characterId) return 2;
            return -1;
        }

        internal static void ApplyActivityChangeToPayload(ref TickStatePayload payload, long targetActivityId)
        {
            // Task 87: any change of activity ends an Ascension attempt; the start
            // command changes the activity FIRST and arms after.
            DisarmAscension(ref payload);
            // Coming into a fight from anything else (idle, a job) is coming in
            // rested. Fight to fight is not: that would make switching monsters
            // a free heal.
            if (ActivityIdBands.IsCombatActivity(targetActivityId) && !ActivityIdBands.IsCombatActivity(payload.ActiveActivityId))
            {
                payload.RestedHpPending = true;
            }
            payload.ActiveActivityId = targetActivityId;
            payload.CurrentProgressTicks = 0;
            payload.CurrentMonsterId = 0;
            payload.CurrentMonsterHp = 0;
            payload.CombatTargetTickAccumulator = 0;
            payload.GatheringProgressTicks = 0;

            // Modul: halt reasons. Deploying is the player's answer to
            // whatever stopped them, so the reason must not survive it - an
            // "out of food" banner that persists after a restock-and-redeploy
            // is worse than none at all. Clearing here rather than in the tick
            // loop also covers the multi-character queue drain, which routes
            // through this same method.
            payload.ActivityHaltReason = Network.ActivityHaltReason.None;

            payload.IsDirty = true;
        }

        // liveActivitiesBySlot: the session's own view of slots 0-2, when there
        // is a session. The rows lag it by up to a checkpoint (a death idles a
        // character on the payload first), so a stale row must not refuse a
        // deploy the live state allows. Null reads the rows (no session).
        // Modul: retried on a Postgres conflict (TransientDbConflict). The core
        // is one Serializable transaction on its own context that returns a code
        // and touches nothing else, so a rerun after a rollback is safe.
        internal Task<Network.CommandResultCode> ChangeCharacterActivityAsync(long playerId, Guid characterId, long targetActivityId, long[]? liveActivitiesBySlot = null, int liveRequestingSlot = -1)
            => TransientDbConflict.RetryAsync(() => ChangeCharacterActivityCoreAsync(playerId, characterId, targetActivityId, liveActivitiesBySlot, liveRequestingSlot));

        private async Task<Network.CommandResultCode> ChangeCharacterActivityCoreAsync(long playerId, Guid characterId, long targetActivityId, long[]? liveActivitiesBySlot, int liveRequestingSlot)
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                var characters = await db.CharacterRecords
                    .FromSqlInterpolated($"SELECT * FROM \"characters\" WHERE \"PlayerId\" = {playerId} FOR UPDATE")
                    .ToListAsync();

                var requesting = characters.Find(c => c.Id == characterId);
                if (requesting == null)
                {
                    await transaction.SafeRollbackAsync();
                    return Network.CommandResultCode.TargetNotFound;
                }

                // Modul: Town Hall slot gating. Was the main character's
                // CurrentLevel; the second and third slots now hang off the
                // Town Hall's level instead - see CharacterSlotEngine for why
                // level was the wrong axis.
                int townHallLevel = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.TownHallBuildingId)
                    .Select(v => (int?)v.CurrentLevel)
                    .SingleOrDefaultAsync() ?? 0;

                if (!CharacterSlotEngine.IsSlotUnlocked(requesting.SlotIndex, townHallLevel))
                {
                    await transaction.SafeRollbackAsync();
                    return Network.CommandResultCode.LevelTooLow;
                }

                long[] activeActivityIds = new long[CharacterSlotEngine.MaxCharacterSlots];
                if (liveActivitiesBySlot != null && liveActivitiesBySlot.Length == CharacterSlotEngine.MaxCharacterSlots)
                {
                    Array.Copy(liveActivitiesBySlot, activeActivityIds, activeActivityIds.Length);
                }
                else
                {
                    for (int i = 0; i < characters.Count; i++)
                    {
                        int slot = characters[i].SlotIndex;
                        if (slot >= 0 && slot < CharacterSlotEngine.MaxCharacterSlots)
                        {
                            activeActivityIds[slot] = characters[i].ActiveActivityId;
                        }
                    }
                }

                // The live array is indexed by fielded rank, which is not always
                // the row's SlotIndex (an escrowed character leaves a gap).
                int requestingIndex = liveActivitiesBySlot != null && liveRequestingSlot >= 0 ? liveRequestingSlot : requesting.SlotIndex;
                if (CharacterSlotEngine.IsActivityOccupiedByAnotherSlot(activeActivityIds, requestingIndex, targetActivityId))
                {
                    await transaction.SafeRollbackAsync();
                    return Network.CommandResultCode.NodeOccupied;
                }

                requesting.ActiveActivityId = targetActivityId;
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                return Network.CommandResultCode.Success;
            }
            catch
            {
                await transaction.SafeRollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// The XP multiplier a live kill pays at, in percent, before the
        /// global-event and mentorship-penalty terms ProcessMonsterDeath adds.
        /// </summary>
        /// <summary>Whether any slot holds a character still in childhood (Mentor's condition).</summary>
        internal static bool AChildWorksInASlot(in TickStatePayload payload)
            => (payload.Slot1_CharacterId != Guid.Empty && payload.Slot1_AgePhase == AgePhaseCurve.Child)
               || (payload.Slot2_CharacterId != Guid.Empty && payload.Slot2_AgePhase == AgePhaseCurve.Child)
               || (payload.Slot3_CharacterId != Guid.Empty && payload.Slot3_AgePhase == AgePhaseCurve.Child);

        // internal: OfflineSimulationEngine needs the same swap to catch up
        // characters 2 and 3. Before that it only ever simulated whatever was
        // in the active register, which is always slot 1 - so an assigned
        // second character earned nothing at all while the player was away.
        internal static void SwapSlotIntoActiveRegister(ref TickStatePayload payload, int slotIndex)
        {
            if (slotIndex == 0)
            {
                return;
            }

            if (slotIndex == 1)
            {
                SwapRegisterWith(ref payload, ref payload.Slot2Activity, ref payload.Slot2_CharacterId, ref payload.Slot2_AgeTicks, ref payload.Slot2_AgePhase, ref payload.Slot2_GeneticVector);
            }
            else
            {
                SwapRegisterWith(ref payload, ref payload.Slot3Activity, ref payload.Slot3_CharacterId, ref payload.Slot3_AgeTicks, ref payload.Slot3_AgePhase, ref payload.Slot3_GeneticVector);
            }
        }

        private static void SwapRegisterWith(
            ref TickStatePayload payload,
            ref CharacterActivityState parked,
            ref System.Guid parkedCharacterId,
            ref long parkedAgeTicks,
            ref int parkedAgePhase,
            ref long parkedGeneticVector)
        {
            Swap(ref payload.ActiveActivityId, ref parked.ActiveActivityId);
            Swap(ref payload.CurrentProgressTicks, ref parked.CurrentProgressTicks);
            Swap(ref payload.RequiredProgressTicks, ref parked.RequiredProgressTicks);
            Swap(ref payload.CurrentMonsterId, ref parked.CurrentMonsterId);
            Swap(ref payload.CurrentMonsterHp, ref parked.CurrentMonsterHp);
            Swap(ref payload.PlayerHp, ref parked.PlayerHp);
            Swap(ref payload.CachedEffectiveMaxHp, ref parked.CachedEffectiveMaxHp);
            Swap(ref payload.RestedHpPending, ref parked.RestedHpPending);
            Swap(ref payload.CombatTargetTickAccumulator, ref parked.CombatTargetTickAccumulator);
            Swap(ref payload.TargetStatusEffectBitmask, ref parked.TargetStatusEffectBitmask);
            Swap(ref payload.SunderStacks, ref parked.SunderStacks);
            Swap(ref payload.KillStreak, ref parked.KillStreak);
            Swap(ref payload.BleedSecondsLeft, ref parked.BleedSecondsLeft);
            Swap(ref payload.BleedMilliPool, ref parked.BleedMilliPool);
            Swap(ref payload.GatheringProgressTicks, ref parked.GatheringProgressTicks);
            Swap(ref payload.HarvestLoopCount, ref parked.HarvestLoopCount);
            Swap(ref payload.ActivityHaltReason, ref parked.ActivityHaltReason);
            // Task 85: a character's rules go where the character goes.
            Swap(ref payload.AutomationRules, ref parked.AutomationRules);

            // Modul: per-character equipment. Gear and its derived totals travel
            // with the character, or every slot would fight in slot 1's armour.
            Swap(ref payload.EquippedWeaponId, ref parked.EquippedWeaponId);
            Swap(ref payload.EquippedHelmetId, ref parked.EquippedHelmetId);
            Swap(ref payload.EquippedArmorId, ref parked.EquippedChestId);
            Swap(ref payload.EquippedGlovesId, ref parked.EquippedGlovesId);
            Swap(ref payload.EquippedLeggingsId, ref parked.EquippedLeggingsId);
            Swap(ref payload.EquippedBootsId, ref parked.EquippedBootsId);
            Swap(ref payload.EquippedAmuletId, ref parked.EquippedAmuletId);
            Swap(ref payload.EquippedRingId, ref parked.EquippedRingId);
            Swap(ref payload.EquippedWeaponAffixLocked, ref parked.EquippedWeaponAffixLocked);
            Swap(ref payload.EquippedArmorAffixLocked, ref parked.EquippedArmorAffixLocked);
            Swap(ref payload.EquippedLeggingsAffixLocked, ref parked.EquippedLeggingsAffixLocked);
            Swap(ref payload.CachedAffixTotals, ref parked.CachedAffixTotals);
            Swap(ref payload.CachedSetIds, ref parked.CachedSetIds);
            // The character's own tools (CharacterActivityState).
            Swap(ref payload.AxeToolTier, ref parked.AxeToolTier);
            Swap(ref payload.PickaxeToolTier, ref parked.PickaxeToolTier);
            Swap(ref payload.RodToolTier, ref parked.RodToolTier);
            Swap(ref payload.ToolAxeSpeedPct, ref parked.ToolAxeSpeedPct);
            Swap(ref payload.ToolPickaxeSpeedPct, ref parked.ToolPickaxeSpeedPct);
            Swap(ref payload.ToolRodSpeedPct, ref parked.ToolRodSpeedPct);
            Swap(ref payload.ToolAxeYieldPct, ref parked.ToolAxeYieldPct);
            Swap(ref payload.ToolPickaxeYieldPct, ref parked.ToolPickaxeYieldPct);
            Swap(ref payload.ToolRodYieldPct, ref parked.ToolRodYieldPct);
            Swap(ref payload.ToolAxeRareFindPct, ref parked.ToolAxeRareFindPct);
            Swap(ref payload.ToolPickaxeRareFindPct, ref parked.ToolPickaxeRareFindPct);
            Swap(ref payload.ToolRodRareFindPct, ref parked.ToolRodRareFindPct);

            // Identity travels with the activity: combat stats are derived from
            // the active character's race, age phase and genetic loci, so a
            // slot has to fight as itself rather than as slot 1. The Slot1_*
            // fields ARE the register's identity, so this exchanges them with
            // the parked slot's own Slot2_*/Slot3_* fields.
            Swap(ref payload.Slot1_CharacterId, ref parkedCharacterId);
            Swap(ref payload.Slot1_AgeTicks, ref parkedAgeTicks);
            Swap(ref payload.Slot1_AgePhase, ref parkedAgePhase);
            Swap(ref payload.Slot1_GeneticVector, ref parkedGeneticVector);
            // The bloodline travels too (CharacterActivityState's note): aptitudes,
            // traits and the epic mark are the character's, not slot 1's.
            Swap(ref payload.Aptitude_Strength, ref parked.AptitudeStrength);
            Swap(ref payload.Aptitude_Skill, ref parked.AptitudeSkill);
            Swap(ref payload.Aptitude_Endurance, ref parked.AptitudeEndurance);
            Swap(ref payload.Aptitude_Fortune, ref parked.AptitudeFortune);
            Swap(ref payload.TraitMask, ref parked.TraitMask);
            Swap(ref payload.IsEpicMutation, ref parked.IsEpicMutation);
        }

        private static void Swap<T>(ref T left, ref T right) where T : struct
        {
            T temporary = left;
            left = right;
            right = temporary;
        }

        // Modul: multi-slot simulation. Runs one 10Hz activity step for every
        // character the player has unlocked AND assigned.
        //
        // Before this, only slot 1 was ever simulated - slots 2 and 3 held a
        // persisted ActiveActivityId that nothing acted on, so assigning a
        // second character produced no gold, no drops and no progress of any
        // kind. The occupancy mutex in CharacterSlotEngine guarantees no two
        // slots share an activity id, so the three passes can never
        // double-count the same node or monster.
        private static void ProcessAllSlotSubTicks(ref TickStatePayload payload, int localXpMultiplier, int localDropMultiplier, System.Collections.Concurrent.ConcurrentQueue<GuildWarPointEvent> guildWarPointQueue, System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext> liveSessionContexts)
        {
            int unlockedSlots = CharacterSlotEngine.GetUnlockedSlotCount(payload.TownHallLevel);

            // The old ProcessSubTick bailed out before its prologue whenever the
            // player was idle or their backpack was full, so aging, mana regen
            // and potion countdowns did not advance in those states. That is
            // preserved verbatim rather than quietly corrected here - the
            // generalisation is only from "slot 1 is working" to "any unlocked
            // slot is working".
            if (!HasAnyWorkingSlot(ref payload, unlockedSlots))
            {
                return;
            }

            ProcessAccountTick(ref payload);

            for (int slotIndex = 0; slotIndex < unlockedSlots; slotIndex++)
            {
                // Slots 2 and 3 only run when a real character occupies them.
                // Slot 0 is exempt: it is the pre-existing single-character path
                // and injected virtual players legitimately have no
                // CharacterRecord, so requiring one there would stop them.
                if (slotIndex > 0 && !SlotHoldsCharacter(ref payload, slotIndex))
                {
                    continue;
                }

                // Modul: slot register hardening, 2026-08-01.
                //
                // The swap-back is in a finally so it cannot be skipped. The
                // pair is what keeps the active register consistent with what
                // the payload believes it holds; if ProcessSubTick threw, the
                // second swap was lost and the register kept the WRONG slot's
                // character, gear and combat state while every later reader
                // assumed slot 0. That corruption would outlive the exception
                // and be attributed to something else entirely.
                SwapSlotIntoActiveRegister(ref payload, slotIndex);
                try
                {
                    ProcessSubTick(ref payload, localXpMultiplier, localDropMultiplier, guildWarPointQueue, liveSessionContexts);
                }
                finally
                {
                    SwapSlotIntoActiveRegister(ref payload, slotIndex);
                }
            }
        }

        private static bool SlotHoldsCharacter(ref TickStatePayload payload, int slotIndex)
        {
            return slotIndex switch
            {
                0 => payload.Slot1_CharacterId != System.Guid.Empty,
                1 => payload.Slot2_CharacterId != System.Guid.Empty,
                2 => payload.Slot3_CharacterId != System.Guid.Empty,
                _ => false
            };
        }

        // True when at least one unlocked slot has a character on an activity
        // and there is backpack room to put its yield. Reads the parked slots
        // directly rather than swapping, since a swap per probe would be pure
        // overhead on the common single-character path.
        private static bool HasAnyWorkingSlot(ref TickStatePayload payload, int unlockedSlots)
        {
            // Modul: the backpack no longer gates work. This used to return
            // false with no room left, which stopped every slot on the account
            // at once - the account-wide version of the same wall
            // ProcessSubTick had.

            // Slot 0: exactly the original condition, no character requirement.
            if (payload.ActiveActivityId > 0)
            {
                return true;
            }

            if (unlockedSlots > 1 && payload.Slot2Activity.ActiveActivityId > 0 && payload.Slot2_CharacterId != System.Guid.Empty)
            {
                return true;
            }

            if (unlockedSlots > 2 && payload.Slot3Activity.ActiveActivityId > 0 && payload.Slot3_CharacterId != System.Guid.Empty)
            {
                return true;
            }

            return false;
        }

        // internal, not private: the tick's whole reward path lives in here and
        // there was no way to observe an hour of it without a Postgres
        // container, a socket and twenty-six constructor dependencies. It takes
        // its two collaborators as parameters already, so a test can drive it
        // headlessly with empty ones - see ProgressionRateTests, which is how
        // "levelling is too fast" stopped being a report and became a number.
        internal static void ProcessSubTick(ref TickStatePayload payload, int localXpMultiplier, int localDropMultiplier, System.Collections.Concurrent.ConcurrentQueue<GuildWarPointEvent> guildWarPointQueue, System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext> liveSessionContexts)
        {
            // Modul: multi-slot simulation. Guard kept byte-for-byte identical
            // to the pre-multi-slot original. It deliberately does NOT also
            // require a character id: injected virtual players (the benchmark
            // stress tester, several integration tests) run an activity with no
            // CharacterRecord behind them, and adding that condition here
            // silently stopped them ticking. Whether a SLOT holds a character is
            // the slot loop's business, not this function's - see
            // ProcessAllSlotSubTicks.
            //
            // The account-level prologue that used to live here now runs once
            // per tick in ProcessAccountTick.
            // Modul: THE BACKPACK NO LONGER GATES THE SIMULATION.
            //
            // This line used to read `|| payload.InventorySpaceRemaining <= 0`,
            // which stopped combat, gathering, XP - everything - the moment a
            // character had twenty things. It also made CombatLootEngine's own
            // graceful-degradation path unreachable dead code: that engine
            // already knew how to scrap an overflowing drop into stackable
            // material, but no tick ever ran to ask it.
            //
            // Loot now routes by WHAT IT IS. Materials go to the unbounded
            // village chest; equipment goes to the bank if it clears the keep
            // threshold and scraps into material if it does not. There is no
            // capacity left to run out of, so there is nothing here to gate on.
            if (payload.ActiveActivityId <= 0)
            {
                return;
            }

            payload.IsDirty = true;


            if (ContentRegistry.TryGetRecipeByActivityId(payload.ActiveActivityId, out var craftingRecipe))
            {
                RefreshRestingHealth(ref payload);
                RunCraftingProgressTick(ref payload, in craftingRecipe);

                // Modul: dispatch exclusivity, 2026-09-17. This branch had no
                // return, unlike the gathering branch immediately below it -
                // so a crafting character fell through into full combat
                // resolution every tick, against fallbackId 1 (every crafting
                // ActivityId lives in ActivityIdBands.CraftingBand, 5000+,
                // always above ContentRegistry.Monsters.Length). The craft
                // itself completed correctly; a whole silent second combat
                // session ran alongside it - real damage, XP, gold, loot,
                // kills - for every character ever assigned to a crafting
                // job. See ProcessSubTickDispatchTests for the regression
                // guard.
                return;
            }
            else if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out var gatheringNode))
            {
                RefreshRestingHealth(ref payload);
                RunGatheringTick(ref payload, in gatheringNode, localDropMultiplier);
                return;
            }

            RunCombatTick(ref payload, localXpMultiplier, localDropMultiplier, guildWarPointQueue, liveSessionContexts);
        }

        /// <summary>
        /// A character at work away from a fight: its health maximum is kept
        /// current (the wire draws its bar against it, and gear or a level can
        /// move it mid-job) and the bar counts as rested, so the next fight
        /// opens full. Same helpers the combat tick uses - one definition of
        /// the pool.
        /// </summary>
        private static void RefreshRestingHealth(ref TickStatePayload payload)
        {
            var combatStats = LiveCombatStats(in payload);
            payload.CachedEffectiveMaxHp = EffectiveMaxMilliHpFor(in payload, in combatStats);
            payload.PlayerHp = Bar(payload.CachedEffectiveMaxHp);
            payload.RestedHpPending = true;
        }

        internal static CombatSimulator.Tally? BeginSimulation(CombatSimulator.Tally tally)
        {
            var previous = t_simulating;
            t_simulating = tally;
            return previous;
        }

        internal static void EndSimulation(CombatSimulator.Tally? previous)
        {
            t_simulating = previous;
            t_hasCarriedStats = false;
        }

        internal static void CarryStatsIntoNextTick(in CombatStats stats)
        {
            // Only inside a simulation, which clears it on the way out
            // (EndSimulation) - a copy left behind would be read by whatever
            // live tick ran next on this thread.
            if (t_simulating == null) return;
            t_carriedStats = stats;
            t_hasCarriedStats = true;
        }
    }
}
