using System.Collections.Generic;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// Task 84: the tick's half of the Great Works - the DepositGreatWork command
    /// going out, and the committed stages coming back onto the payload.
    ///
    /// Modul: THE COMMAND CARRIES CHOICES, NEVER NUMBERS THE SERVER TRUSTS. The
    /// monument and the material are buttons, and the quantity is only ever
    /// clamped down to what the current stage still needs, so an out-of-range
    /// value is a state race or a stale screen and is ANSWERED with a result
    /// code (GenericValidationFailure), not answered with a disconnect.
    /// The materials are spent off the tick, in GreatWorksEngine's transaction.
    /// </summary>
    internal static class GreatWorksTickCoordinator
    {
        internal static void HandleDepositGreatWork(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long pId = currentPayload.PlayerId;
            long region = cmd.TargetId;
            long kind = cmd.SecondaryId;
            long quantity = cmd.DepositQuantity;

            if (region < GreatWorksRegistry.FirstRegion || region > GreatWorksRegistry.LastRegion
                || kind < 0 || kind > 1)
            {
                ctx.PlayerRegistry.EnqueueCommandResult(pId, (byte)CommandResultCode.GenericValidationFailure);
                return;
            }

            // Copied before the lambda: it cannot read ctx or the ref cmd.
            var factory = ctx.ContextFactory;
            var registry = ctx.PlayerRegistry;
            ctx.SafeDispatch("GreatWorks.Deposit", pId, async () =>
            {
                await GreatWorksEngine.DepositAsync(factory, registry, pId, (int)region, (int)kind, quantity);
            });
        }

        /// <summary>Applies every committed deposit to the live payload's cache.</summary>
        internal static void DrainUpdates(Dictionary<long, TickStatePayload> activePlayers)
        {
            while (GreatWorksEngine.Updates.TryDequeue(out var update))
            {
                ref var payload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, update.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref payload))
                {
                    payload.GreatWorksStagesPacked = update.StagesPacked;
                    payload.IsDirty = true;
                }
            }
        }
    }
}
