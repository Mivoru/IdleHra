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
                try
                {
                    int released = await ReleaseOrphanedClaimsAsync();
                    if (released > 0)
                    {
                        Console.WriteLine($"Mailbox: released {released} mail claim(s) stranded by a restart.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Mailbox: releasing stranded claims failed: {ex.Message}");
                }

                while (true)
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                        using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                        
                        long threshold = GameClock.UtcNowOffset.ToUnixTimeSeconds() - 604800;
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
                await transaction.SafeRollbackAsync();
            }
        }

        public async Task CommitMailClaimAsync(long playerId, long mailId, bool isSuccess)
        {
            try
            {
                // Modul: A LOST RACE NO LONGER LOSES THE MAIL (audit 2026-11-02).
                // This transaction locks the PlayerRecords and gold rows the
                // checkpoint flush writes too, so a Serializable conflict (40001)
                // is an ordinary outcome here. It used to be swallowed: the claim
                // rolled back with IsPending still true from ClaimMailItemAsync,
                // and nothing ever cleared it - the mailbox list hides pending
                // mail and the claim refuses it, so the gold, diamonds or item
                // were out of reach until the seven-day cleanup deleted them.
                // The success result was also sent BEFORE the commit, so the
                // player was told it worked. Now each attempt is a fresh
                // transaction (TransientDbConflict.RetryAsync), the result is
                // sent after the commit, and a claim that still fails is
                // released so the mail is claimable again.
                var outcome = await TransientDbConflict.RetryAsync(() => CommitMailClaimOnceAsync(mailId, isSuccess));
                if (!outcome.Claimed)
                {
                    return;
                }

                _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);

                // Modul: the live payload owns PremiumCurrency and the checkpoint
                // writes it back by plain assignment, so a DB-only credit would be
                // overwritten by the next flush (the 2026-08-02 achievement bug).
                // Hand the committed balance to the tick thread instead.
                if (outcome.DiamondBalance.HasValue)
                {
                    _playerRegistry.BillingSyncQueue.Enqueue(new BillingSyncNotification
                    {
                        PlayerId = outcome.OwnerId,
                        PremiumDiamondsBalance = outcome.DiamondBalance.Value
                    });
                }

                // Claiming mail can put an equipment instance in the backpack,
                // so the live count has to learn about it.
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await EnqueueInventoryCensusAsync(db, outcome.OwnerId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mail claim {mailId} for {playerId} failed: {ex.Message}");
                await ReleasePendingClaimAsync(mailId);
                _playerRegistry.EnqueueCommandResult(playerId, (byte)TransientDbConflict.RefusalFor(ex));
            }
            finally
            {
                // Modul: the in-memory pending flag started in ClaimMailItemAsync
                // is only ever cleared here, regardless of outcome (mail row
                // missing, success, rejection, or a thrown exception) - this
                // method is that claim's terminal step.
                EndPendingTransaction(playerId);
            }
        }

        private readonly record struct MailClaimOutcome(bool Claimed, long OwnerId, int? DiamondBalance);

        /// <summary>
        /// One attempt at the claim: its own scope and transaction, nothing done
        /// outside the database before the commit, so a rerun after a conflict
        /// starts from what is committed.
        /// </summary>
        private async Task<MailClaimOutcome> CommitMailClaimOnceAsync(long mailId, bool isSuccess)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            int? diamondBalance = null;

            var mailQuery = "SELECT * FROM \"MailboxInstances\" WHERE \"Id\" = {0} FOR UPDATE";
            var mail = await db.MailboxInstances.FromSqlRaw(mailQuery, mailId).SingleOrDefaultAsync();

            // Modul: IsClaimed is checked under the lock. ReleaseOrphanedClaimsAsync
            // and ReleasePendingClaimAsync can make a mail claimable again while
            // an earlier request for it is still queued, so two commits for one
            // mail are possible - the second must credit nothing.
            if (mail == null || mail.IsClaimed)
            {
                return new MailClaimOutcome(false, 0, null);
            }

            if (!isSuccess)
            {
                mail.IsPending = false;
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                return new MailClaimOutcome(false, mail.PlayerId, null);
            }

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
                // Modul: an upsert (CommodityLedger), task 44.
                await CommodityLedger.AddAsync(db, mail.PlayerId, "gold", mail.GoldAttachment);
                await GoldLedger.RecordIncomeAsync(db, mail.PlayerId, GoldIncomeSource.Mail, mail.GoldAttachment);
            }
            else if (mail.GoldAttachment < 0)
            {
                // Modul: A MAIL CAN CHARGE GOLD (owner, 2026-11-09 - the
                // "speed tax" sent to the two players who took twenty
                // rebirths at level 1). Only a hand-written row carries a
                // negative attachment; the admin route still refuses one.
                // Taken only when the player presses Claim, and never past
                // zero: the gold row is locked, then debited by at most what
                // it holds.
                long held = await db.Database.SqlQuery<long>(
                    $"SELECT \"Quantity\" AS \"Value\" FROM \"CommodityRecords\" WHERE \"PlayerId\" = {mail.PlayerId} AND \"ItemId\" = 'gold' FOR UPDATE")
                    .FirstOrDefaultAsync();
                long charge = Math.Min(Math.Max(0L, held), -mail.GoldAttachment);
                if (charge > 0)
                {
                    // GoldLedger: a debit, not income.
                    await CommodityLedger.AddAsync(db, mail.PlayerId, "gold", -charge);
                }
            }

            // Modul: diamonds and a title, sent only by the admin mail
            // (the 2026-10-09 betatester thank-you). The title is worn
            // at once: a gift nobody sees is no gift, and a level-1
            // tester cannot reach the Delve's title picker yet.
            if (mail.DiamondAttachment > 0 || !string.IsNullOrEmpty(mail.TitleAttachment))
            {
                var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == mail.PlayerId);
                if (player != null)
                {
                    if (mail.DiamondAttachment > 0)
                    {
                        player.PremiumDiamonds += mail.DiamondAttachment;
                    }
                    if (FolkIdle.Server.Domain.Progression.TitleRegistry.Find(mail.TitleAttachment) != null)
                    {
                        await FolkIdle.Server.Domain.Progression.TitleEngine.GrantAsync(db, mail.PlayerId, mail.TitleAttachment!, GameClock.UtcNow);
                        player.ActiveTitleSlug = mail.TitleAttachment;
                    }
                    diamondBalance = mail.DiamondAttachment > 0 ? player.PremiumDiamonds : null;
                }
            }

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new MailClaimOutcome(true, mail.PlayerId, diamondBalance);
        }

        /// <summary>
        /// Makes one unclaimed mail claimable again after its claim failed.
        /// Best-effort: if this fails too, ReleaseOrphanedClaimsAsync clears it
        /// at the next start.
        /// </summary>
        private async Task ReleasePendingClaimAsync(long mailId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"MailboxInstances\" SET \"IsPending\" = false WHERE \"Id\" = {mailId} AND \"IsClaimed\" = false");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Releasing mail claim {mailId} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears IsPending on every unclaimed mail. Run once at start-up.
        ///
        /// Modul: the queue between ClaimMailItemAsync and CommitMailClaimAsync
        /// (MailClaimRequestQueue) lives in memory, so a restart between the two
        /// strands the mail as pending for good - hidden from the list, refused
        /// by the claim, deleted unclaimed after seven days. Nothing can be in
        /// flight before this process has served a request, so every pending
        /// row at start-up is such an orphan. A commit that does still arrive
        /// checks IsClaimed under its lock, so nothing is credited twice.
        /// </summary>
        public async Task<int> ReleaseOrphanedClaimsAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            return await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"MailboxInstances\" SET \"IsPending\" = false WHERE \"IsPending\" = true AND \"IsClaimed\" = false");
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
