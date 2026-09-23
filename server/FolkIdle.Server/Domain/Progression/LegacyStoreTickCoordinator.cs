using System.Collections.Generic;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick thread's Legacy Store hand-off: folds a committed
    /// LegacyStoreEngine purchase back into the live TickStatePayload.
    ///
    /// Modul: this is a COORDINATOR, not an engine. It runs synchronously on
    /// the 10Hz tick thread, called by SimulationEngine.EngineLoop, and it is
    /// the only kind of class allowed to write TickStatePayload besides
    /// SimulationEngine itself. It owns no thread, no timer and no state - a
    /// static class with no fields, so there is nothing for a second thread
    /// to reach. See the plan's single-writer constraint: a coordinator that
    /// grew its own scheduling would silently reintroduce torn reads of a
    /// 200-field blittable struct at 10Hz, which nothing in this repo's test
    /// suite can observe.
    /// </summary>
    internal static class LegacyStoreTickCoordinator
    {
        /// <summary>
        /// Drains every pending Legacy Store update onto its player's live
        /// payload. Verbatim from SimulationEngine.EngineLoop, where this
        /// block sat inline between the quarantine drain and the inheritance
        /// drain.
        /// </summary>
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.LegacyStoreUpdateQueue.TryDequeue(out var legacyNotif))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, legacyNotif.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in legacyNotif);
                }
            }
        }

        /// <summary>
        /// Applies one notification to one live payload.
        ///
        /// Split out from the drain loop deliberately: this half needs no
        /// dictionary, no registry and no database, so it is the seam any
        /// future test of this domain's fold-back would drive. The drain loop
        /// above is pure plumbing.
        /// </summary>
        internal static void Apply(ref TickStatePayload payload, in LegacyStoreUpdateNotification legacyNotif)
        {
            payload.SetLegacyShards(legacyNotif.LegacyShardBalance);
            payload.CitizenMultiSlotsUnlocked = legacyNotif.CitizenMultiSlotsUnlocked;
            if (legacyNotif.HasLegacyPerksUpdate)
            {
                payload.CachedLegacyPerks = legacyNotif.LegacyPerks;
            }
        }

        /// <summary>
        /// CommandType.PurchaseLegacyUnlocks. Verbatim from
        /// SimulationEngine.EngineLoop's dispatch chain.
        ///
        /// The rejection path is a DISCONNECT, not a CommandResult - the
        /// branch chose that deliberately, and the per-branch choice between
        /// ignore, report and disconnect is exactly what a dispatch table must
        /// not flatten.
        /// </summary>
        internal static void HandlePurchaseLegacyUnlocks(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateLegacyStoreRequest(ref currentPayload, ref cmd))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            uint unlockId = cmd.TargetUnlockId;
            uint slotIndex = cmd.RequestedSlotIndex;

            var legacyStoreEngine = ctx.LegacyStoreEngine;
            ctx.SafeDispatch("Legacy.PurchaseUnlock", pId, async () => {
                await legacyStoreEngine.PurchaseLegacyUnlockAsync(pId, unlockId, slotIndex);
            });
        }
    }
}
