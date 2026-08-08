using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;
using System.Data;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public class MailboxAndBankEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        // Modul: Phase - Full-Stack Production Polish Phase 2, Part 1.
        // Claim/Deposit/Withdraw are all two-phase for the queued paths
        // (an initial synchronous validation+enqueue, then an async
        // Commit*Async that actually mutates rows once SimulationEngine's
        // tick loop drains the notification queue) - previously nothing
        // stopped a player from firing several requests for the SAME or
        // DIFFERENT items before the first one's Commit* step ever ran,
        // queuing up multiple in-flight bank mutations with no ordering
        // guarantee against each other (see WithdrawFromBankAsync's own
        // former comment openly describing this as an unresolved risk).
        // This dictionary tracks, per player, the UTC tick timestamp a
        // bank transaction started - TryBeginPendingTransaction/
        // EndPendingTransaction below are the only two places that touch
        // it, both lock-free (ConcurrentDictionary's own atomic TryAdd/
        // TryUpdate/TryRemove, no explicit lock needed). A stale entry
        // (older than PendingTransactionTimeoutTicks) is treated as
        // resolved even if EndPendingTransaction was never called for it -
        // the essential safety valve for a player who disconnects between
        // the initial enqueue and the tick loop's drain (see
        // SimulationEngine's MailClaimRequestQueue
        // drains, which silently skip an offline player and would
        // otherwise never call Commit*Async to clear the flag).
        private readonly ConcurrentDictionary<long, long> _pendingBankTransactions = new();
        private static readonly long PendingTransactionTimeoutTicks = TimeSpan.FromSeconds(10).Ticks;

        public MailboxAndBankEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        private bool TryBeginPendingTransaction(long playerId)
        {
            long now = DateTime.UtcNow.Ticks;
            long expiredBefore = now - PendingTransactionTimeoutTicks;

            while (true)
            {
                if (_pendingBankTransactions.TryGetValue(playerId, out long existingStartedAt))
                {
                    if (existingStartedAt > expiredBefore)
                    {
                        return false;
                    }

                    // Stale - a prior transaction never cleared its flag
                    // (most likely the player disconnected before the tick
                    // loop's drain could call Commit*Async). Attempt to
                    // atomically replace it; if another thread changed it
                    // first, retry with the fresh value.
                    if (_pendingBankTransactions.TryUpdate(playerId, now, existingStartedAt))
                    {
                        return true;
                    }
                    continue;
                }

                if (_pendingBankTransactions.TryAdd(playerId, now))
                {
                    return true;
                }
            }
        }

        private void EndPendingTransaction(long playerId)
        {
            _pendingBankTransactions.TryRemove(playerId, out _);
        }

        public void StartCleanupCron()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                        using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                        
                        long threshold = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 604800;
                        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"MailboxInstances\" WHERE \"ReceivedTimestamp\" < {threshold}");
                        
                        await transaction.CommitAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Mailbox cleanup failed: {ex.Message}");
                    }
                    await Task.Delay(60000);
                }
            });
        }

        public async Task ClaimMailItemAsync(long playerId, long mailId)
        {
            if (!TryBeginPendingTransaction(playerId))
            {
                _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TransactionPending);
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var mailQuery = "SELECT * FROM \"MailboxInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                var mail = await db.MailboxInstances.FromSqlRaw(mailQuery, mailId).SingleOrDefaultAsync();

                if (mail == null || mail.PlayerId != playerId || mail.IsClaimed || mail.IsPending)
                {
                    EndPendingTransaction(playerId);
                    return;
                }

                mail.IsPending = true;
                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                // Modul: the pending flag is deliberately NOT cleared here -
                // this claim is still unresolved until SimulationEngine's
                // tick loop drains MailClaimRequestQueue and calls
                // CommitMailClaimAsync, which is what actually clears it.
                _playerRegistry.MailClaimRequestQueue.Enqueue(new MailClaimRequest
                {
                    PlayerId = playerId,
                    MailId = mailId,
                    GoldAttachment = mail.GoldAttachment,
                    HasItem = !string.IsNullOrEmpty(mail.BaseItemId) || mail.AttachedEquipmentId.HasValue
                });
            }
            catch (Exception)
            {
                EndPendingTransaction(playerId);
                await transaction.RollbackAsync();
            }
        }

        public async Task CommitMailClaimAsync(long playerId, long mailId, bool isSuccess)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var mailQuery = "SELECT * FROM \"MailboxInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                var mail = await db.MailboxInstances.FromSqlRaw(mailQuery, mailId).SingleOrDefaultAsync();

                if (mail == null) return;

                if (isSuccess)
                {
                    mail.IsClaimed = true;
                    mail.IsPending = false;

                    if (mail.AttachedEquipmentId.HasValue)
                    {
                        var eqQuery = "SELECT * FROM \"EquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                        var eq = await db.EquipmentInstances.FromSqlRaw(eqQuery, mail.AttachedEquipmentId.Value).SingleOrDefaultAsync();
                        if (eq != null)
                        {
                            eq.PlayerId = mail.PlayerId;
                        }
                    }
                    else if (!string.IsNullOrEmpty(mail.BaseItemId))
                    {
                        db.EquipmentInstances.Add(new EquipmentInstance
                        {
                            PlayerId = mail.PlayerId,
                            BaseItemId = mail.BaseItemId,
                            QualityTier = mail.QualityTier,
                            AffixPayload = "{}"
                        });
                    }

                    if (mail.GoldAttachment > 0)
                    {
                        var goldQuery = "SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE";
                        var gold = await db.CommodityRecords.FromSqlRaw(goldQuery, mail.PlayerId).SingleOrDefaultAsync();
                        if (gold == null)
                        {
                            gold = new CommodityRecord { PlayerId = mail.PlayerId, ItemId = "gold", Quantity = 0 };
                            db.CommodityRecords.Add(gold);
                        }
                        gold.Quantity += mail.GoldAttachment;
                    }
                }
                else
                {
                    mail.IsPending = false;
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                // Claiming mail can put an equipment instance in the backpack,
                // so the live count has to learn about it.
                await EnqueueInventoryCensusAsync(db, mail.PlayerId);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
            }
            finally
            {
                // Modul: the pending flag started in ClaimMailItemAsync is
                // only ever cleared here, regardless of outcome (mail row
                // missing, success, rejection, or a thrown exception) -
                // this method is that claim's terminal step.
                EndPendingTransaction(playerId);
            }
        }

        /// <summary>
        /// Recounts occupied backpack slots and hands the number to the tick
        /// thread, which owns the live payload and must never be written from
        /// a background dispatch task.
        ///
        /// Deliberately best-effort: a failure here costs a stale count that
        /// the next loot drop or reconnect corrects, and must never roll back
        /// the transaction that already committed.
        /// </summary>
        private async Task EnqueueInventoryCensusAsync(FolkIdleDbContext db, long playerId)
        {
            try
            {
                int occupied = await CombatLootEngine.CountOccupiedBackpackSlotsAsync(db, playerId);
                _playerRegistry.InventoryCensusQueue.Enqueue(new InventoryCensusNotification
                {
                    PlayerId = playerId,
                    OccupiedSlots = occupied
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Inventory census enqueue failed for {playerId}: {ex.Message}");
            }
        }
    }
}
