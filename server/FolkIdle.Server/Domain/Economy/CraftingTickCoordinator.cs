using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Shared;
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

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.InitializeCrafting)
        internal static void HandleInitializeCrafting(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long pId = currentPayload.PlayerId;
            int resultItemId = (int)cmd.TargetId;

            // Modul: the batch rides DepositQuantity, an existing
            // uint no other branch of this opcode reads. Adding a
            // BatchSize field would have meant a wire-struct change
            // - the packet is demultiplexed by exact byte size, so
            // the layout guard and the generated client protocol
            // both move - for one small integer that an unused
            // field already carries. 0 means a client that predates
            // this and gets the old behaviour of one.
            //
            // The value is CLAMPED IN THE ENGINE, not here.
            // batchSize multiplies both cost and output, so it is
            // exactly the kind of number a client must not be
            // trusted with.
            int batchSize = cmd.DepositQuantity > 0 ? (int)Math.Min(cmd.DepositQuantity, (uint)CraftingEngine.MaxCraftBatchSize) : 1;

            var craftingEngine = ctx.CraftingEngine;
            ctx.SafeDispatch("Crafting.Initialize", pId, async () => {
                await craftingEngine.ExecuteCraftingAsync(pId, resultItemId, batchSize);
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.CraftItem)
        internal static void HandleCraftItem(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: CommandType.CraftItem is RETIRED, along with the
            // equipment recipes it carried. Equipment is monster loot
            // and tools are crafted - see CraftingEngine. A client
            // still sending it is an old bundle rather than an attack,
            // so it is ignored rather than treated as a protocol
            // violation: disconnecting a stale tab teaches nobody
            // anything and looks like the game is broken.
            // Deliberately empty.
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.UpgradeTool)
        internal static void HandleUpgradeTool(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: UPGRADETOOL DOES NOTHING AND NEVER DID.
            //
            // VillageBuildingEngine.ExecuteUpgradeToolAsync was
            // `return Task.CompletedTask;` - a twenty-four line
            // engine holding one empty method, constructed in
            // Program, threaded through this constructor and
            // dispatched to on every request. The command validated,
            // routed, awaited and accomplished nothing.
            //
            // Worse, a request that failed validation DISCONNECTED
            // the player - the same defect fusion had, over a
            // command with no effect to protect.
            //
            // Tools are ordinary equipment now: crafted, carried,
            // rerolled and raised at the Forge like anything else.
            // A second upgrade path for them was removed from the
            // village screen; this is the other half of it. Ignored
            // rather than rejected, so a client built before the
            // removal is simply not answered.
        }
    }
}
