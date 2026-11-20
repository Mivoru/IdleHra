using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{
    // Modul: moved out of NetworkBroadcastSystem.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md). Routing stays in RouteCoreAsync.
    public partial class NetworkBroadcastSystem
    {
        // Modul: static content, not per-player data - no database access
        // needed, just an authenticated read of ContentRegistry.Balance
        // (already loaded once at boot from GameBalanceConfig.json).
        private async Task HandleStoreCatalog(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var entries = new System.Collections.Generic.List<StoreCatalogEntryResponse>();
                foreach (var kvp in ContentRegistry.Balance.IapProductPrices)
                {
                    entries.Add(new StoreCatalogEntryResponse { ProductId = kvp.Key, DiamondAmount = kvp.Value });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Store catalog error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleStorefrontListings(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                string query = context.Request.Url?.Query ?? string.Empty;
                if (!ClientCommandValidator.ValidateStorefrontQuery(playerId, query))
                {
                    ForceDisconnect(playerId);
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // Modul: Phase - Full-Stack Production Polish Phase 2, Part
                // 4.1. Resolves StorefrontSegmentationEngine.ResolveCohort's
                // three real inputs from this player's own transaction
                // history/account record, replacing the previous pure
                // playerId-hash cohort assignment. lifetimeValue is the
                // cumulative granted premium-diamond total (see
                // ProcessedTransactions - the authoritative anti-replay IAP
                // ledger); a player with no rows here has never purchased
                // anything, so lastTransactionEpoch stays null and
                // daysSinceLastTransaction resolves to int.MaxValue,
                // correctly excluding them from both the "active high-value"
                // and "recently active veteran" branches.
                long lifetimeValue = await db.ProcessedTransactions
                    .AsNoTracking()
                    .Where(t => t.PlayerId == playerId)
                    .Select(t => (long)t.PremiumDiamondsGranted)
                    .SumAsync();

                long? lastTransactionEpoch = await db.ProcessedTransactions
                    .AsNoTracking()
                    .Where(t => t.PlayerId == playerId)
                    .OrderByDescending(t => t.ProcessedAtEpoch)
                    .Select(t => (long?)t.ProcessedAtEpoch)
                    .FirstOrDefaultAsync();

                long ageInTicks = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.LogicEpochCounter)
                    .SingleOrDefaultAsync();

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int daysSinceLastTransaction = lastTransactionEpoch.HasValue
                    ? (int)Math.Min(int.MaxValue, (nowEpoch - lastTransactionEpoch.Value) / 86400L)
                    : int.MaxValue;

                int cohort = StorefrontSegmentationEngine.ResolveCohort(lifetimeValue, ageInTicks, daysSinceLastTransaction);

                await using (var profileTransaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"PlayerSegmentationProfiles\" (\"PlayerId\", \"CohortTag\", \"LifetimeValueCents\", \"ChurnRiskScore\") VALUES ({0}, {1}, {2}, {3}) ON CONFLICT (\"PlayerId\") DO UPDATE SET \"CohortTag\" = EXCLUDED.\"CohortTag\", \"LifetimeValueCents\" = EXCLUDED.\"LifetimeValueCents\", \"ChurnRiskScore\" = EXCLUDED.\"ChurnRiskScore\";",
                        playerId,
                        cohort,
                        (int)Math.Min(int.MaxValue, lifetimeValue),
                        Math.Min(1.0, daysSinceLastTransaction / 90.0));
                    await profileTransaction.CommitAsync();
                }

                await using var listingsTransaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                var products = await db.SegmentedStorefrontListings
                    .AsNoTracking()
                    .Where(l => l.TargetCohort == cohort)
                    .OrderBy(l => l.ListingId)
                    .Select(l => new StorefrontListingResponse
                    {
                        ListingId = l.ListingId,
                        ProductIdentifier = l.ProductIdentifier,
                        DiamondPackageYield = l.DiamondPackageYield,
                        PriceInCents = l.PriceInCents
                    })
                    .ToListAsync();
                await listingsTransaction.CommitAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, products);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Storefront listings error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: HandleVerifyReceipt REMOVED 2026-09-18 - it was the REST
        // wrapper around VerifyPurchaseAsync (an unauthenticated
        // AccountId/TransactionId/ProductId out of the request body, no
        // signature check) exposed at what the client believed was the
        // hardened URL. See the routing comment above where
        // /api/v1/billing/verify-receipt used to be registered.

        // Modul: the real, hardened IAP verification endpoint. Identifies
        // the player from the caller's own session Bearer JWT (see
        // TryResolveAuthenticatedPlayerAsync) rather than trusting a
        // client-supplied AccountId, and passes the raw base64 receipt
        // straight through to BillingVerificationEngine.VerifyReceiptAsync,
        // which is the only place TransactionId/ProductId/reward amount are
        // ever derived from - none of them come from this request body
        // directly.
        private async Task HandleBillingVerify(HttpListenerContext context)
        {
            try
            {
                var (playerId, authMethod) = await TryResolveAuthenticatedPlayerWithMethodAsync(context.Request);
                if (playerId == 0L)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                if (_billingVerificationEngine == null)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                // Modul: step-up gate, Task 10. A device-bearer session on an
                // account that already has a password proves only "held the
                // DeviceId", not "is the account owner" - a real-money
                // purchase requires re-proving the password in this same
                // request body before VerifyReceiptAsync runs.
                if (await RequiresPasswordStepUpAsync(playerId, authMethod))
                {
                    string suppliedPassword = payload.TryGetProperty("password", out var pwElement) ? (pwElement.GetString() ?? string.Empty) : string.Empty;
                    await using var stepUpDb = await _contextFactory.CreateDbContextAsync();
                    if (suppliedPassword.Length == 0 || !await VerifyStepUpPasswordAsync(stepUpDb, playerId, suppliedPassword))
                    {
                        // Modul: this endpoint is now in the AuthThrottle
                        // budget (see the allowlist above) precisely because
                        // it verifies a password - the request-count throttle
                        // bounds the guess rate, and this line leaves a trace
                        // in the server's own log of which player a
                        // brute-force attempt targeted, which the throttle
                        // alone would not record anywhere.
                        Console.WriteLine($"Step-up rejected: player {playerId} presented a device-bearer session with a missing or incorrect password on billing/verify.");
                        WriteStepUpRequired(context);
                        context.Response.Close();
                        return;
                    }
                }

                if (!payload.TryGetProperty("receipt", out var receiptElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string base64Receipt = receiptElement.GetString() ?? string.Empty;
                bool success = await _billingVerificationEngine.VerifyReceiptAsync(playerId, base64Receipt);
                context.Response.StatusCode = success ? 200 : 409;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Billing verify error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleRefundWebhook(HttpListenerContext context)
        {
            try
            {
                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                var accountId = payload.GetProperty("AccountId").GetGuid();
                var refundedDiamonds = payload.GetProperty("RefundedDiamonds").GetInt32();

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                var player = await db.PlayerRecords.FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"PlayerGuid\" = {0} FOR UPDATE", accountId).FirstOrDefaultAsync();
                if (player != null)
                {
                    await Engine.DiamondLedger.LabelAsync(db, Engine.DiamondLedger.Refund);
                    player.PremiumDiamonds -= refundedDiamonds;
                    if (player.PremiumDiamonds < 0)
                    {
                        player.Quarantine_Active = true;
                        player.IsQuarantined = true;
                        
                        _playerSessionRegistry?.QuarantineNotificationQueue.Enqueue(new QuarantineNotification { PlayerId = player.Id });
                    }
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Refund webhook error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleSupportTicket(HttpListenerContext context)
        {
            try
            {
                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                var traceLog = payload.GetProperty("TraceLog").GetString();
                
                // Server-side scrubbing logic is not requested here, the client runs the regex on its side.
                // Or maybe we should scrub here too? The task says "collection boundary", meaning before sending, 
                // but we also have to execute sanitization exclusively upon explicit ticket dispatch. So it runs on the client.

                // Modul: THE MESSAGE USED TO BE THROWN AWAY. This line printed
                // "Received Support Ticket" and nothing else, so whatever the
                // player wrote reached no one - the Settings screen was honest
                // that nobody would reply, but not that nobody could read it.
                // It is logged now, on one grep-able line (`[support]`), with
                // the account when the request carries a token. Newlines are
                // escaped so one ticket stays one log line; the body cap is
                // ReadBodyAsync's, the print cap is here.
                long supportPlayerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                string text = string.Join(" | ", (traceLog ?? string.Empty).Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries));
                if (text.Length > 4000) text = text.Substring(0, 4000) + " [truncated]";
                Console.WriteLine($"[support] player={supportPlayerId} {text}");
                
                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Support ticket error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }
    }
}
