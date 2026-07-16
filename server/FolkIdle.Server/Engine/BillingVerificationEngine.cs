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
        private readonly FolkIdle.Server.Network.NetworkBroadcastSystem? _networkSystem;
        private readonly RetryingDbContextOptions _retryingDbOptions;
        private readonly IIapReceiptValidator _receiptValidator;

        public BillingVerificationEngine(
            IDbContextFactory<FolkIdleDbContext> contextFactory,
            RedisSessionCache redisCache,
            PlayerSessionRegistry playerRegistry,
            RetryingDbContextOptions retryingDbOptions,
            IIapReceiptValidator receiptValidator,
            FolkIdle.Server.Network.NetworkBroadcastSystem? networkSystem = null)
        {
            _contextFactory = contextFactory;
            _redisCache = redisCache;
            _playerRegistry = playerRegistry;
            _retryingDbOptions = retryingDbOptions;
            _receiptValidator = receiptValidator;
            _networkSystem = networkSystem;
        }

        // Modul: server-side product catalog - the ONLY source of truth for
        // how many diamonds a given ProductId is worth. Neither the
        // WebSocket notification path nor the REST receipt-verification
        // path ever trusts a client- or receipt-supplied amount; both
        // resolve it here. Previously a hardcoded switch statement; prices
        // now live in GameBalanceConfig.json (ContentRegistry.Balance.
        // IapProductPrices) so a price change is a content-data deploy, not
        // a code deploy. An unknown productId - one with no entry in the
        // config - resolves to 0, matching the old switch's default arm.
        internal static int ResolvePremiumDiamondsForProduct(string productId)
        {
            return ContentRegistry.Balance.IapProductPrices.TryGetValue(productId, out int amount) ? amount : 0;
        }

        // Modul: the legacy in-session notification path (SimulationEngine's
        // CommandType.SubmitPurchaseReceipt WebSocket command). transactionId
        // here is a short, client-supplied opaque string, not a real store
        // receipt - the WebSocket packet's fixed 64-byte buffer could not
        // carry a real receipt anyway. The reward amount was previously also
        // client-supplied (a genuine spoofing vector: any player could claim
        // an arbitrary premiumAmount) - it is now always resolved from
        // productId via ResolvePremiumDiamondsForProduct instead. Real
        // purchase verification is VerifyReceiptAsync below, reached only
        // through the REST /api/v1/billing/verify endpoint, which can carry
        // an arbitrarily large base64 receipt body.
        public async Task<bool> VerifyPurchaseAsync(long playerId, string transactionId, string productId)
        {
            int premiumAmount = ResolvePremiumDiamondsForProduct(productId);
            if (premiumAmount <= 0)
            {
                return false;
            }

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

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
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Purchase verification failed - PlayerId {playerId}, TransactionId {transactionId}: {ex.Message}");
                return false;
            }
        }

        // Modul: the real, hardened verification path - reached only from
        // the REST /api/v1/billing/verify endpoint. base64Receipt is decoded
        // and validated by _receiptValidator; TransactionId/ProductId used
        // below always come from that validated result, never from any
        // other caller-supplied value, so a client cannot influence which
        // transaction gets recorded or which product's price is applied.
        // ProcessedTransactions.TransactionId (the primary key) is what
        // atomically rejects a replay - the INSERT itself fails on a
        // duplicate rather than relying on a separate check-then-insert
        // window, and that failure propagates out as the Serializable/
        // unique-violation path below, which reports rejection rather than
        // retrying (retrying an insert that violates a primary key fails
        // identically every time).
        public async Task<bool> VerifyReceiptAsync(long playerId, string base64Receipt)
        {
            IapReceiptValidationResult receipt = _receiptValidator.Validate(base64Receipt);
            if (!receipt.IsValid)
            {
                return false;
            }

            // Modul: mandatory signature-verification gate - checked
            // explicitly and separately from IsValid so this requirement is
            // visible at the call site that grants currency, rather than
            // trusting whichever IIapReceiptValidator happens to be
            // registered to have enforced it internally. No premium
            // currency is granted, and no ledger row is written, unless the
            // receipt's signature verified against a configured store
            // public key.
            if (!receipt.SignatureVerified)
            {
                return false;
            }

            int premiumAmount = ResolvePremiumDiamondsForProduct(receipt.ProductId);
            if (premiumAmount <= 0)
            {
                return false;
            }

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    try
                    {
                        var profile = await context.PlayerRecords
                            .FirstOrDefaultAsync(p => p.Id == playerId);

                        if (profile == null)
                        {
                            await transaction.RollbackAsync();
                            return false;
                        }

                        bool markedProcessed = await TransactionDedupEngine.TryMarkProcessedAsync(context, new ProcessedTransaction
                        {
                            TransactionId = receipt.TransactionId,
                            PlayerId = playerId,
                            ProductId = receipt.ProductId,
                            PremiumDiamondsGranted = premiumAmount,
                            ProcessedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });

                        if (!markedProcessed)
                        {
                            // Already processed - reject before granting any
                            // currency or writing any other ledger row, even
                            // though the store API call that produced this
                            // receipt reported success.
                            await transaction.RollbackAsync();
                            return false;
                        }

                        int previousBalance = profile.PremiumDiamonds;
                        profile.PremiumDiamonds += premiumAmount;

                        context.PrimaryPurchaseLedgers.Add(new PrimaryPurchaseLedger
                        {
                            TransactionId = receipt.TransactionId,
                            PlayerId = playerId,
                            ProductId = receipt.ProductId,
                            PurchaseState = 1,
                            TimestampProcessed = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });

                        context.EventHorizonPremiumLedgers.Add(new EventHorizonPremiumLedger
                        {
                            TransactionId = receipt.TransactionId,
                            PlayerId = playerId,
                            PreviousBalance = previousBalance,
                            NewBalance = profile.PremiumDiamonds,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });

                        await context.SaveChangesAsync();
                        await transaction.CommitAsync();
                        return true;
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
                    {
                        // TransactionId already exists in ProcessedTransactions
                        // (or PrimaryPurchaseLedger) - a replay, not a
                        // retryable failure. Roll back and reject.
                        await transaction.RollbackAsync();
                        return false;
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Receipt verification failed - PlayerId {playerId}: {ex.Message}");
                return false;
            }
        }

        public async Task HandleRefundAlertAsync(string transactionId)
        {
            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var purchase = await context.PrimaryPurchaseLedgers
                        .FirstOrDefaultAsync(p => p.TransactionId == transactionId);

                    // A refund alert for a transaction this server never
                    // recorded means the purchase and refund ledgers have
                    // diverged - fail loudly (the catch below logs and
                    // rethrows) rather than silently swallowing a refund
                    // that will never be clawed back.
                    if (purchase == null)
                    {
                        throw new InvalidOperationException($"Refund alert for unknown TransactionId '{transactionId}' - no PrimaryPurchaseLedger row exists.");
                    }

                    // Already refunded - an idempotent repeat delivery of the
                    // same alert, not an error.
                    if (purchase.PurchaseState == 2) return;

                    purchase.PurchaseState = 2;

                    var profile = await context.PlayerRecords
                        .FirstOrDefaultAsync(p => p.Id == purchase.PlayerId);

                    if (profile != null)
                    {
                        int previousBalance = profile.PremiumDiamonds;

                        // Claw back exactly what the original purchase
                        // granted. ProcessedTransactions records the granted
                        // amount for every receipt-verified purchase; the
                        // legacy WebSocket notification path predates that
                        // ledger, so fall back to re-resolving the amount
                        // from the purchase's ProductId via the same fixed
                        // price table that granted it. If neither source can
                        // produce a positive amount, the ledgers are
                        // inconsistent - throw (rolling back the
                        // PurchaseState change above) instead of guessing.
                        var processedTransaction = await context.ProcessedTransactions
                            .FirstOrDefaultAsync(t => t.TransactionId == transactionId);

                        int deduction = processedTransaction?.PremiumDiamondsGranted
                            ?? ResolvePremiumDiamondsForProduct(purchase.ProductId);

                        if (deduction <= 0)
                        {
                            throw new InvalidOperationException($"Refund alert for TransactionId '{transactionId}' cannot resolve the granted diamond amount - no ProcessedTransactions row and ProductId '{purchase.ProductId}' is not in the price table.");
                        }

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
                                "UPDATE \"MarketOrderRecords\" SET \"IsQuarantined\" = TRUE WHERE \"SellerId\" = {0} AND \"Status\" = 0",
                                purchase.PlayerId);

                            // Issue hot-swap signal via existing QuarantineNotificationQueue
                            _playerRegistry.QuarantineNotificationQueue.Enqueue(new QuarantineNotification { PlayerId = purchase.PlayerId });

                            // Modul 32/39: force-terminate the active WebSocket
                            // session immediately on refund-triggered quarantine,
                            // rather than only freezing tick processing on the
                            // next live update - the account should not remain
                            // connected once its premium balance has gone
                            // negative from a platform refund.
                            _networkSystem?.ForceDisconnect(purchase.PlayerId);
                        }
                    }

                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Refund alert handling failed - TransactionId {transactionId}: {ex.Message}");
                throw;
            }
        }
    }
}
