using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;
using System;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Shared;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick thread's billing-sync hand-off. See LegacyStoreTickCoordinator
    /// for the coordinator shape this repeats.
    /// </summary>
    internal static class BillingTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.BillingSyncQueue.TryDequeue(out var billingSyncNotif))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, billingSyncNotif.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in billingSyncNotif);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in BillingSyncNotification billingSyncNotif)
        {
            payload.SetPremiumCurrency(billingSyncNotif.PremiumDiamondsBalance);
            payload.IsDirty = true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ClaimBattlePassReward)
        internal static void HandleClaimBattlePassReward(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateBattlePassClaimRequest(ref currentPayload, ref cmd))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            uint milestoneIndex = cmd.TargetMilestoneIndex;
            uint seasonalXp = currentPayload.AccumulatedSeasonalXp;
            uint passLevel = currentPayload.ActiveChroniclePassLevel;

            if (ctx.LiveSessionContexts.TryGetValue(pId, out var context))
            {
                var req = new BattlePassClaimRequest
                {
                    TargetMilestoneIndex = milestoneIndex,
                    AccumulatedSeasonalXp = seasonalXp,
                    ActiveChroniclePassLevel = passLevel
                };
                context.TryEnqueueBattlePassClaim(in req);
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.PurchaseBattlePass)
        internal static void HandlePurchaseBattlePass(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: Comprehensive Game System Audit, Part 4.3.
            // Premium track unlock via in-game PremiumDiamonds -
            // dispatched off the tick thread like every other
            // DB-transactional command; balance check, deduction,
            // and PremiumUnlocked flag all resolve inside one
            // Serializable FOR UPDATE transaction server-side.
            long pId = currentPayload.PlayerId;
            var executePassPurchase = ctx.ExecutePassPurchase;
            ctx.SafeDispatch("BattlePass.Purchase", pId, async () => {
                await executePassPurchase(pId);
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.SubmitPurchaseReceipt)
        internal static void HandleSubmitPurchaseReceipt(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long pId = currentPayload.PlayerId;
            string transactionId = "";
            // Modul: `fixed` is the only change the move required here. Inline
            // in EngineLoop, cmd was a LOCAL, so its fixed-size buffer was
            // already unmovable and could be read through a bare pointer; as a
            // `ref` parameter it may point into movable memory, and the
            // compiler (CS1666) insists on pinning it. Same 64 bytes, same
            // decode.
            unsafe {
                fixed (byte* ptr = cmd.RawTransactionReceipt)
                {
                    transactionId = System.Text.Encoding.UTF8.GetString(ptr, 64).TrimEnd('\0');
                }
            }

            // Modul: Production Release Hardening, Part 1. This
            // used to build productId as the literal string
            // "Product_{hash}", which could never match a real
            // GameBalanceConfig.json key
            // (ResolvePremiumDiamondsForProduct's dictionary
            // lookup would always miss, silently resolving to 0
            // diamonds) - previously broken for every purchase
            // submitted through this command, a real financial
            // blocker. TryResolveProductIdFromHash resolves the
            // client-computed FNV-1a hash back to the real
            // product id via ContentRegistry's own reverse
            // lookup table (built once at boot, see
            // ContentRegistry.Initialize), never throwing on an
            // unresolved hash.
            //
            // Bulletproof fallback: if the hash does not
            // resolve (a stale client build, a corrupted
            // packet, or simply hash 0 from an
            // uninitialized/never-set client field), fall back
            // to treating transactionId itself as a cleartext
            // product id - this WebSocket command's only other
            // string payload - and accept it if it is a real,
            // known catalog entry. Genuine cryptographic
            // signed-receipt verification (where a cleartext
            // product id is extracted from a verified Apple/
            // Google payload) already exists as a separate,
            // correct path - VerifyReceiptAsync, reached only
            // through the REST /api/v1/billing/verify endpoint,
            // which is the only place a real signed receipt can
            // actually be carried (this 64-byte WebSocket
            // packet never could). Neither branch here ever
            // throws - an unresolved product id simply falls
            // through to VerifyPurchaseAsync's own existing
            // premiumAmount <= 0 rejection.
            if (!ContentRegistry.TryResolveProductIdFromHash(cmd.TargetProductIdHash, out string productId))
            {
                productId = transactionId;
            }

            var billingVerificationEngine = ctx.BillingVerificationEngine;
            var networkSystem = ctx.NetworkSystem;
            ctx.SafeDispatch("Billing.VerifyPurchase", pId, async () => {
                bool success = await billingVerificationEngine.VerifyPurchaseAsync(pId, transactionId, productId);
                if (success) {
                    networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                }
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.SyncBillingStatus)
        internal static void HandleSyncBillingStatus(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: reconciles the live in-memory
            // TickStatePayload.PremiumCurrency against the
            // database-authoritative PlayerRecords.
            // PremiumDiamonds - the client calls this after
            // returning from a store purchase flow that was
            // verified through the REST
            // /api/v1/billing/verify endpoint (see
            // BillingVerificationEngine.VerifyReceiptAsync),
            // which writes directly to the database and never
            // touches this session's in-memory payload. Reads
            // the balance rather than re-running verification
            // on a stored receipt, since no such "pending
            // unapplied record" is ever persisted here - every
            // receipt is verified synchronously at submission
            // time by BillingVerificationEngine, either via
            // that REST endpoint or via
            // CommandType.SubmitPurchaseReceipt above.
            long syncPlayerId = currentPayload.PlayerId;
            var contextFactory = ctx.ContextFactory;
            var playerRegistry = ctx.PlayerRegistry;
            ctx.SafeDispatch("Billing.SyncStatus", syncPlayerId, async () =>
            {
                await using var syncDb = await contextFactory.CreateDbContextAsync();
                int? balance = await syncDb.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == syncPlayerId)
                    .Select(p => (int?)p.PremiumDiamonds)
                    .SingleOrDefaultAsync();

                if (balance.HasValue)
                {
                    playerRegistry.BillingSyncQueue.Enqueue(new BillingSyncNotification
                    {
                        PlayerId = syncPlayerId,
                        PremiumDiamondsBalance = balance.Value
                    });
                }
            });
        }
    }
}
