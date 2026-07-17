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
        // SimulationEngine's MailClaimRequestQueue/BankWithdrawRequestQueue
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

        public async Task DepositToBankAsync(long playerId, long instanceId)
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
                var eqQuery = "SELECT * FROM \"EquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                var eq = await db.EquipmentInstances.FromSqlRaw(eqQuery, instanceId).SingleOrDefaultAsync();

                if (eq == null || eq.PlayerId != playerId)
                {
                    return;
                }

                // Modul: Comprehensive Game System Audit, Part 5.3.
                // Equipped-item guard - previously MISSING here while
                // MarketEscrowEngine and ForgeSplicingEngine both had it.
                // Depositing an equipped item deleted the EquipmentInstances
                // row outright while PlayerRecord.EquippedWeaponId/
                // EquippedArmorId still referenced the dead id, leaving a
                // dangling equip pointer plus stale cached equip stats
                // (EquipmentSlotEngine only recomputes on equip/unequip
                // events), while the item's copy sat in the bank and could
                // be withdrawn as a brand new row and re-equipped - a
                // stat-duplication vector. Same FOR UPDATE lock + rejection
                // MarketEscrowEngine.ListItemAsync already uses.
                var playerRow = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();
                if (playerRow != null && (
                    (playerRow.EquippedWeaponId.HasValue && playerRow.EquippedWeaponId.Value == eq.Id) ||
                    (playerRow.EquippedArmorId.HasValue && playerRow.EquippedArmorId.Value == eq.Id) ||
                    (playerRow.EquippedLeggingsId.HasValue && playerRow.EquippedLeggingsId.Value == eq.Id)))
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("BankDeposit failed: Item is currently equipped.");
                    _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.ItemEquipped);
                    return;
                }

                var bankCountQuery = "SELECT * FROM \"BankEquipmentInstances\" WHERE \"PlayerId\" = {0} FOR UPDATE";
                var bankItems = await db.BankEquipmentInstances.FromSqlRaw(bankCountQuery, playerId).ToListAsync();

                int maxBankSlots = 100;
                var humanMastery = await db.PlayerRaceMasteries.FirstOrDefaultAsync(m => m.PlayerId == playerId && m.RaceId == 1);
                if (humanMastery != null)
                {
                    if (humanMastery.MasteryLevel >= 50) maxBankSlots += 15;
                    else if (humanMastery.MasteryLevel >= 25) maxBankSlots += 10;
                    else if (humanMastery.MasteryLevel >= 10) maxBankSlots += 5;
                }

                if (bankItems.Count >= maxBankSlots)
                {
                    return;
                }

                db.EquipmentInstances.Remove(eq);

                var bankItem = new BankEquipmentInstance
                {
                    PlayerId = playerId,
                    BaseItemId = eq.BaseItemId,
                    QualityTier = eq.QualityTier,
                    AffixPayload = eq.AffixPayload,
                    IsAffixLocked = eq.IsAffixLocked
                };

                db.BankEquipmentInstances.Add(bankItem);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
            }
            finally
            {
                // Modul: Deposit is single-phase (no queued Commit* second
                // step) - the pending flag is always cleared right here,
                // on every exit path.
                EndPendingTransaction(playerId);
            }
        }

        public async Task WithdrawFromBankAsync(long playerId, long bankId)
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
                var bankQuery = "SELECT * FROM \"BankEquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                var bankItem = await db.BankEquipmentInstances.FromSqlRaw(bankQuery, bankId).SingleOrDefaultAsync();

                if (bankItem == null || bankItem.PlayerId != playerId)
                {
                    EndPendingTransaction(playerId);
                    return;
                }

                // Modul: previously an unresolved double-enqueue race (see
                // this method's own former inline commentary, now
                // resolved) - TryBeginPendingTransaction above already
                // rejects any further deposit/withdraw/claim request for
                // this player while this one is in flight, so at most one
                // BankWithdrawRequest can ever be queued per player at a
                // time. The pending flag is deliberately NOT cleared on
                // this success path - it stays set until
                // CommitBankWithdrawAsync (this withdrawal's terminal
                // step) clears it.
                _playerRegistry.BankWithdrawRequestQueue.Enqueue(new BankWithdrawRequest
                {
                    PlayerId = playerId,
                    BankId = bankId
                });

                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                EndPendingTransaction(playerId);
                await transaction.RollbackAsync();
            }
        }

        public async Task CommitBankWithdrawAsync(long playerId, long bankId, bool isSuccess)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var bankQuery = "SELECT * FROM \"BankEquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                var bankItem = await db.BankEquipmentInstances.FromSqlRaw(bankQuery, bankId).SingleOrDefaultAsync();

                if (bankItem == null) return;

                if (isSuccess)
                {
                    db.BankEquipmentInstances.Remove(bankItem);

                    var eq = new EquipmentInstance
                    {
                        PlayerId = bankItem.PlayerId,
                        BaseItemId = bankItem.BaseItemId,
                        QualityTier = bankItem.QualityTier,
                        AffixPayload = bankItem.AffixPayload,
                        IsAffixLocked = bankItem.IsAffixLocked
                    };
                    db.EquipmentInstances.Add(eq);
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
            }
            finally
            {
                // Modul: the pending flag started in WithdrawFromBankAsync
                // is only ever cleared here, regardless of outcome - this
                // method is that withdrawal's terminal step.
                EndPendingTransaction(playerId);
            }
        }
    }
}
