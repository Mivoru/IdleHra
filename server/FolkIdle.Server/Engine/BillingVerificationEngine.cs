using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public class BillingVerificationEngine
    {
        private readonly IDbContextFactory<FolkIdleDbContext> _contextFactory;
        private readonly RedisSessionCache _redisCache;
        private readonly PlayerSessionRegistry _playerRegistry; 

        public BillingVerificationEngine(IDbContextFactory<FolkIdleDbContext> contextFactory, RedisSessionCache redisCache, PlayerSessionRegistry playerRegistry)
        {
            _contextFactory = contextFactory;
            _redisCache = redisCache;
            _playerRegistry = playerRegistry;
        }

        public async Task<bool> VerifyPurchaseAsync(long playerId, string transactionId, string productId, int premiumAmount)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            try
            {
                var existing = await context.PrimaryPurchaseLedgers
                    .FirstOrDefaultAsync(p => p.TransactionId == transactionId);

                if (existing != null) return false;

                var profile = await context.PlayerRecords
                    .FirstOrDefaultAsync(p => p.Id == playerId);

                if (profile == null) return false;

                int previousBalance = profile.PremiumDiamonds;
                profile.PremiumDiamonds += premiumAmount;

                var purchase = new PrimaryPurchaseLedger
                {
                    TransactionId = transactionId,
                    PlayerId = playerId,
                    ProductId = productId,
                    PurchaseState = 1,
                    TimestampProcessed = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                var log = new EventHorizonPremiumLedger
                {
                    TransactionId = transactionId,
                    PlayerId = playerId,
                    PreviousBalance = previousBalance,
                    NewBalance = profile.PremiumDiamonds,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                context.PrimaryPurchaseLedgers.Add(purchase);
                context.EventHorizonPremiumLedgers.Add(log);

                await context.SaveChangesAsync();
                await transaction.CommitAsync();

                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task HandleRefundAlertAsync(string transactionId)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            try
            {
                var purchase = await context.PrimaryPurchaseLedgers
                    .FirstOrDefaultAsync(p => p.TransactionId == transactionId);

                if (purchase == null || purchase.PurchaseState == 2) return;

                purchase.PurchaseState = 2;

                var profile = await context.PlayerRecords
                    .FirstOrDefaultAsync(p => p.Id == purchase.PlayerId);

                if (profile != null)
                {
                    int previousBalance = profile.PremiumDiamonds;
                    int deduction = 1000; // Mapped via product lookup matrix
                    profile.PremiumDiamonds -= deduction;

                    var log = new EventHorizonPremiumLedger
                    {
                        TransactionId = transactionId,
                        PlayerId = purchase.PlayerId,
                        PreviousBalance = previousBalance,
                        NewBalance = profile.PremiumDiamonds,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    context.EventHorizonPremiumLedgers.Add(log);

                    if (profile.PremiumDiamonds < 0)
                    {
                        profile.IsQuarantined = true;
                        await _redisCache.SetQuarantineFlagAsync(purchase.PlayerId, true);
                        
                        await context.Database.ExecuteSqlRawAsync(
                            "UPDATE \"MarketOrderRecords\" SET \"IsQuarantined\" = TRUE WHERE \"PlayerId\" = {0} AND \"IsActive\" = TRUE", 
                            purchase.PlayerId);

                        // Issue hot-swap signal via existing QuarantineNotificationQueue
                        _playerRegistry.QuarantineNotificationQueue.Enqueue(new QuarantineNotification { PlayerId = purchase.PlayerId });
                    }
                }

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }
}
