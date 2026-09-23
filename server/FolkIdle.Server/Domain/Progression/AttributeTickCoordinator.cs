using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick thread's attribute commands: placing and refunding STR/DEX/CON/LCK points, entirely on the payload.
    ///
    /// Modul: a COORDINATOR, not an engine - a static class with no fields,
    /// called synchronously on the 10Hz tick thread by SimulationEngine's
    /// command dispatch table after CommandGate has said Proceed. It owns no
    /// thread, timer or state; anything asynchronous goes through the
    /// SafeDispatch delegate it is handed, never a task it starts itself.
    /// </summary>
    internal static class AttributeTickCoordinator
    {
        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.SpendAttributePoint)
        internal static void HandleSpendAttributePoint(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: ENTIRELY ON THE TICK THREAD, no database at all.
            //
            // Both the balance and the four attributes live on the
            // payload, and the checkpoint already persists all five
            // (PlayerRecords.UnspentAttributePoints and the Base*
            // columns). So this is pure struct arithmetic - no scope,
            // no transaction, no queue - which is the cheapest thing
            // a command can be and is available precisely because
            // the state was already tick-owned.
            //
            // A menu choice out of range is REFUSED, not treated as
            // a protocol violation: the attribute id comes from a
            // dropdown and the amount from a button, and neither is
            // evidence of a tampered client. Same call the skill tree
            // makes just below.
            int attributeId = (int)cmd.TargetId;
            int requested = cmd.LimitPrice;

            if (attributeId < 0 || attributeId > 3 || requested <= 0)
            {
                ctx.PlayerRegistry.EnqueueCommandResult(currentPayload.PlayerId,
                    (byte)Network.CommandResultCode.GenericValidationFailure);
            }
            else if (currentPayload.UnspentAttributePoints < requested)
            {
                // Says so, rather than silently doing nothing - this
                // server's favourite way to lie.
                ctx.PlayerRegistry.EnqueueCommandResult(currentPayload.PlayerId,
                    (byte)Network.CommandResultCode.InsufficientMaterials);
            }
            else
            {
                currentPayload.UnspentAttributePoints -= requested;
                switch (attributeId)
                {
                    case 0: currentPayload.STR += requested; break;
                    case 1: currentPayload.DEX += requested; break;
                    case 2: currentPayload.CON += requested; break;
                    default: currentPayload.LCK += requested; break;
                }

                currentPayload.IsDirty = true;

                // Modul: a placed point is a DECISION, not an
                // accumulating counter, so it does not wait out the
                // five-minute checkpoint window. Pulling the
                // boundary forward instead of flushing inline keeps
                // the synchronous database write off the click path
                // and coalesces a burst of clicks into one write on
                // the next tick.
                currentPayload.TicksSinceLastFlush = StateCheckpointManager.CheckpointBoundaryTicks;

                ctx.PlayerRegistry.EnqueueCommandResult(currentPayload.PlayerId,
                    (byte)Network.CommandResultCode.Success);
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.RespecAttributes)
        internal static void HandleRespecAttributes(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: FREE, AND THAT IS A DECISION.
            //
            // Every other purchase in this game charges through a
            // database transaction off the tick. Gold spent on the
            // TICK would need a new path - decrement CurrentGold and
            // RedisPendingGoldDelta together - and "two gold paths,
            // and mixing them pays the player twice" is a rule this
            // codebase learned the hard way. Inventing a third one
            // for a respec button is not worth it.
            //
            // Doing it off the tick instead would mean an engine
            // writing the four attribute columns while a live
            // session holds its own copy, which is the exact
            // split-brain the checkpoint's own comment warns about:
            // these are absolutes, so there can only be one writer,
            // and on the tick that writer is the payload.
            //
            // So the cost is the placing, not the paying. The points
            // come back and have to be spent again, which is enough
            // friction for a season-long choice and cannot corrupt a
            // balance. If it should cost gold later, the honest way
            // is a safe tick-side spend path first.
            int refunded =
                (currentPayload.STR - AttributeRegistry.StartingValue(AttributeRegistry.Might))
                + (currentPayload.DEX - AttributeRegistry.StartingValue(AttributeRegistry.Finesse))
                + (currentPayload.CON - AttributeRegistry.StartingValue(AttributeRegistry.Vigour))
                + (currentPayload.LCK - AttributeRegistry.StartingValue(AttributeRegistry.Fortune));

            if (refunded <= 0)
            {
                // Nothing placed. Says so rather than appearing to
                // work - a button that silently does nothing is this
                // server's favourite way to lie.
                ctx.PlayerRegistry.EnqueueCommandResult(currentPayload.PlayerId,
                    (byte)Network.CommandResultCode.GenericValidationFailure);
            }
            else
            {
                currentPayload.STR = AttributeRegistry.StartingValue(AttributeRegistry.Might);
                currentPayload.DEX = AttributeRegistry.StartingValue(AttributeRegistry.Finesse);
                currentPayload.CON = AttributeRegistry.StartingValue(AttributeRegistry.Vigour);
                currentPayload.LCK = AttributeRegistry.StartingValue(AttributeRegistry.Fortune);
                currentPayload.UnspentAttributePoints += refunded;
                currentPayload.IsDirty = true;

                // Same reason as the spend above: a respec that only
                // exists in memory is a respec the next reload undoes.
                currentPayload.TicksSinceLastFlush = StateCheckpointManager.CheckpointBoundaryTicks;

                ctx.PlayerRegistry.EnqueueCommandResult(currentPayload.PlayerId,
                    (byte)Network.CommandResultCode.Success);
            }
        }
    }
}
