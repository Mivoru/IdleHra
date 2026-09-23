using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// The tick thread's two inventory-census hand-offs. Grouped in one
    /// coordinator because they are one domain, not to save a task - see
    /// LegacyStoreTickCoordinator for the coordinator shape this repeats.
    /// </summary>
    internal static class InventoryCensusTickCoordinator
    {
        // Modul: THE FULL-BACKPACK DEAD END.
        //
        // InventorySpaceRemaining was refreshed from exactly two
        // places: the session load, and a loot drop carrying a census.
        // ProcessSubTick's first line returns when it is 0, so a full
        // backpack stopped combat, which stopped loot, which stopped
        // the only thing that could recount - while depositing to the
        // bank, claiming mail or selling on the market all changed the
        // database without touching the live payload. The player freed
        // slots, watched the number stay at 0, and had no way back
        // short of reconnecting.
        //
        // Found by driving the real UI: the dev fixture at 20/20
        // accepted ChangeActivity, set ActiveActivityId to 91, and
        // CurrentMonsterId never left 0.
        internal static void DrainCensus(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.InventoryCensusQueue.TryDequeue(out var census))
            {
                ref var censusPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, census.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref censusPayload))
                {
                    ApplyCensus(ref censusPayload, in census);
                }
            }
        }

        internal static void ApplyCensus(ref TickStatePayload censusPayload, in InventoryCensusNotification census)
        {
            // Modul: VESTIGIAL SINCE THE BACKPACK WAS REMOVED.
            //
            // Materials go to the unbounded village chest and
            // equipment to the bank or to scrap, so there is no
            // per-character carrying capacity left to run out of.
            // The field survives because a dozen gathering and
            // loot loops still decrement it defensively and the
            // wire still carries it; reporting full capacity keeps
            // every one of those a no-op without touching them, and
            // keeps the packet layout unchanged.
            //
            // It is deliberately NOT deleted in the same pass that
            // changed the loot routing - one behaviour change at a
            // time, and the layout guard pins this packet's size.
            int capacity = censusPayload.InventoryCapacity > 0 ? censusPayload.InventoryCapacity : SimulationEngine.DefaultBackpackCapacity;
            censusPayload.InventorySpaceRemaining = capacity;

            // Clearing the halt here as well as recomputing the
            // number: the tick that follows only clears it when an
            // activity is running, and a player who just made room
            // should not keep reading "everything is stopped"
            // until the next kill lands.
            if (censusPayload.InventorySpaceRemaining > 0 &&
                censusPayload.ActivityHaltReason == Network.ActivityHaltReason.InventoryFull)
            {
                censusPayload.ActivityHaltReason = Network.ActivityHaltReason.None;
            }

            censusPayload.IsDirty = true;
        }

        internal static void DrainLootDrops(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.CombatLootDropQueue.TryDequeue(out var combatLootDrop))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, combatLootDrop.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    ApplyLootDrop(ref currentPayload, in combatLootDrop);
                }
            }
        }

        internal static void ApplyLootDrop(ref TickStatePayload currentPayload, in CombatLootDropNotification combatLootDrop)
        {
            // Modul: inventory census. Assign from the truth when
            // the loot engine sent one. The decrement below is only
            // the fallback for a notification with no census (the
            // no-loot-table early-out path), and even then the next
            // real kill corrects it - the old behaviour was decrement
            // only, forever, which made every backpack read as full
            // after 20 kills and silently discarded all loot for the
            // rest of the session.
            // Modul: the backpack is gone. Storage is one
            // unlimited village chest, so this counter is pinned
            // at capacity and gates nothing. It used to be
            // `capacity - OccupiedSlots`, and OccupiedSlots now
            // counts the CHEST - an unbounded number measured
            // against a 20 slot ceiling. Twenty stacks in and
            // every player was permanently "full": gathering
            // dropped nothing, and the halt banner said
            // EVERYTHING IS STOPPED.
            currentPayload.InventorySpaceRemaining =
                currentPayload.InventoryCapacity > 0 ? currentPayload.InventoryCapacity : SimulationEngine.DefaultBackpackCapacity;
            currentPayload.IsDirty = true;
        }
    }
}
