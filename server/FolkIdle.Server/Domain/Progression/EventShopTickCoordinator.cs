using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick's half of the seasonal event shop: the BuyEventShopItem
    /// command takes the price off the live balance and hands the save to
    /// EventShopEngine; refunds come back through <see cref="DrainRefunds"/>.
    ///
    /// Modul: EVERY REFUSAL IS ANSWERED, NONE DISCONNECTS. A stale screen (the
    /// event rolled over, the item moved) is a state race, not a tampered
    /// client - server/CLAUDE.md.
    /// </summary>
    internal static class EventShopTickCoordinator
    {
        internal static void HandleBuy(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long pId = currentPayload.PlayerId;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var (current, phase) = SeasonalEventRegistry.Current(now);

            if (current == null || phase == SeasonalEventPhase.None)
            {
                ctx.PlayerRegistry.EnqueueCommandResult(pId, (byte)CommandResultCode.EventShopClosed);
                return;
            }
            if (cmd.SecondaryId != current.Id || cmd.TargetId < 0 || cmd.TargetId >= current.Shop.Count)
            {
                ctx.PlayerRegistry.EnqueueCommandResult(pId, (byte)CommandResultCode.GenericValidationFailure);
                return;
            }

            var item = current.Shop[(int)cmd.TargetId];
            if (!SeasonalEventEarning.TrySpend(ref currentPayload, item.Price, now))
            {
                ctx.PlayerRegistry.EnqueueCommandResult(pId, (byte)CommandResultCode.EventShopInsufficient);
                return;
            }

            int eventId = current.Id;
            var factory = ctx.ContextFactory;
            var registry = ctx.PlayerRegistry;
            ctx.SafeDispatch("EventShop.Buy", pId, async () =>
            {
                await EventShopEngine.BuyAsync(factory, registry, pId, eventId, item);
            });
        }

        /// <summary>Hands rare pets found this tick to PetEngine, off the tick.</summary>
        internal static void DrainPetDrops(
            PlayerSessionRegistry registry,
            Action<string, long, Func<System.Threading.Tasks.Task>> safeDispatch,
            Microsoft.EntityFrameworkCore.IDbContextFactory<FolkIdle.Server.Models.FolkIdleDbContext> contextFactory)
        {
            while (PetEngine.Drops.TryDequeue(out var note))
            {
                var captured = note;
                safeDispatch("Pets.Drop", captured.PlayerId, () => PetEngine.SaveDropAsync(contextFactory, registry, captured));
            }
        }

        internal static void DrainRefunds(Dictionary<long, TickStatePayload> activePlayers)
        {
            while (EventShopEngine.Refunds.TryDequeue(out var refund))
            {
                ref var payload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, refund.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref payload))
                {
                    // Normalised first so a balance that has never been stamped
                    // (a session older than a dev-forced event) takes the grant.
                    SeasonalEventEarning.Normalise(ref payload, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    SeasonalEventEarning.Refund(ref payload, refund.EventId, refund.Amount);
                }
            }
        }
    }
}
