using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>
    /// The tick thread's two crafting hand-offs. Grouped in one coordinator
    /// because they are one domain, not to save a task - see
    /// LegacyStoreTickCoordinator for the coordinator shape this repeats.
    /// </summary>
    internal static class CraftingTickCoordinator
    {
        // Modul: crafting as an assignable job. Drained next to every
        // other cross-engine queue, so a finished craft costs the tick
        // one dequeue and CraftingEngine does the rest off the hot
        // path.
        //
        // CraftingTickQueue is a public static field on SimulationEngine
        // itself, not on PlayerSessionRegistry - ProcessSubTick enqueues to
        // it from a static context. Read directly off SimulationEngine
        // rather than moving the field; moving it is a separate decision
        // with its own blast radius (CombatLootEngine.cs comments on
        // exactly this static-ness) and is out of scope here.
        internal static void DrainCraftingTicks(Action<string, long, Func<Task>> safeDispatch, CraftingEngine craftingEngine)
        {
            while (SimulationEngine.CraftingTickQueue.TryDequeue(out var craftCompletion))
            {
                long craftPlayerId = craftCompletion.PlayerId;
                int craftResultItemId = craftCompletion.ResultItemId;
                safeDispatch("Crafting.Job", craftPlayerId, async () => {
                    await craftingEngine.ExecuteCraftingAsync(craftPlayerId, craftResultItemId);
                });
            }
        }

        internal static void DrainCraftingCompletions(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            ConcurrentQueue<GuildWarPointEvent> guildWarPointQueue)
        {
            while (registry.CraftingCompletionQueue.TryDequeue(out var craftCompletion))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, craftCompletion.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    QuestEngine.IncrementProgress(ref currentPayload, QuestEngine.QuestTypeCraftItems, 1);

                    // Mirrors the increment CraftingEngine already committed
                    // to PlayerRecords, so the wire counter tracks the craft
                    // instead of standing at its login value all session.
                    currentPayload.LifetimeItemsCrafted += craftCompletion.Quantity;

                    if (currentPayload.ActiveGuildWarId > 0 && ContentRegistry.ItemDefinitions.Length >= craftCompletion.CraftedItemId)
                    {
                        var def = ContentRegistry.ItemDefinitions[craftCompletion.CraftedItemId - 1];
                        if (def.RegionTier >= 5)
                        {
                            int wp = 50 * def.RegionTier;
                            guildWarPointQueue.Enqueue(new GuildWarPointEvent
                            {
                                MatchId = currentPayload.ActiveGuildWarId,
                                GuildId = currentPayload.GuildId,
                                Front = 1,
                                Points = wp
                            });
                        }
                    }
                    currentPayload.IsDirty = true;
                }
            }
        }
    }
}
