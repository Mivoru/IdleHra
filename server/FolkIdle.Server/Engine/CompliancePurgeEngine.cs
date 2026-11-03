using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public sealed class CompliancePurgeEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionMultiplexer? _redis;
        private readonly StateCheckpointManager? _checkpointManager;

        internal const int PurgeAttempts = 3;

        public CompliancePurgeEngine(IServiceProvider serviceProvider, IConnectionMultiplexer? redis, StateCheckpointManager? checkpointManager = null)
        {
            _serviceProvider = serviceProvider;
            _redis = redis;
            _checkpointManager = checkpointManager;
        }

        public void QueueGdprPurge(long playerId)
        {
            if (playerId <= 0)
            {
                return;
            }

            _ = Task.Run(() => PurgeWithRetryAsync(playerId));
        }

        /// <summary>
        /// Runs the purge until the account is gone, a bounded number of times.
        ///
        /// Modul: AN ERASURE REQUEST IS NOT BEST-EFFORT (audit 2026-11-02). The
        /// purge locks the PlayerRecords row FOR UPDATE inside a Serializable
        /// transaction, and the request arrives from a live session - whose
        /// last checkpoint may still be on the CheckpointWriter, writing that
        /// same row. A lost race (40001) rolled the purge back with one console
        /// line and nothing ever asked again, while the session had already
        /// been closed as if the account were gone. Now: the session's queued
        /// flushes finish first, a failed attempt is retried, and an account
        /// that survives every attempt is logged as needing erasure by hand.
        /// </summary>
        internal async Task<bool> PurgeWithRetryAsync(long playerId)
        {
            try
            {
                if (_checkpointManager != null)
                {
                    await _checkpointManager.WaitForPendingFlushesAsync(playerId);
                }

                for (int attempt = 1; attempt <= PurgeAttempts; attempt++)
                {
                    if (await ExecuteGdprPurgeAsync(playerId)) return true;
                    if (!await PlayerExistsAsync(playerId)) return true;
                    if (attempt < PurgeAttempts) await Task.Delay(200 * attempt);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GDPR purge for player {playerId} threw: {ex.Message}");
            }

            Console.WriteLine($"GDPR PURGE FAILED for player {playerId} after {PurgeAttempts} attempts - the account still exists and must be erased by hand.");
            return false;
        }

        private async Task<bool> PlayerExistsAsync(long playerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            return await db.PlayerRecords.AsNoTracking().AnyAsync(p => p.Id == playerId);
        }

        /// <summary>Runs one purge now. True when the account is gone.</summary>
        internal async Task<bool> ExecuteGdprPurgeAsync(long playerId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, timeout.Token);

            try
            {
                var player = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .SingleOrDefaultAsync(timeout.Token);

                if (player == null)
                {
                    await transaction.SafeRollbackAsync(timeout.Token);
                    return false;
                }

                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"VillageResidents\" WHERE \"PlayerId\" = {0}", new object[] { playerId }, timeout.Token);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"VillageInfrastructures\" WHERE \"PlayerId\" = {0}", new object[] { playerId }, timeout.Token);
                // Modul: the mentorship tables are dropped, so a purge no
                // longer has rows to delete there. Kept as raw SQL against a
                // table that may still exist in an old database would fail the
                // purge outright, and a purge that throws is worse than one
                // that has nothing to do.
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MarketOrderRecords\" WHERE \"SellerId\" = {0}", new object[] { playerId }, timeout.Token);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PlayerDeviceRegistrations\" WHERE \"PlayerId\" = {0}", new object[] { playerId }, timeout.Token);
                // Task 83: a Workshop commission in progress.
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PlayerCraftingSlots\" WHERE \"PlayerId\" = {0}", new object[] { playerId }, timeout.Token);
                // Task 79: the gold ledger is per-player history.
                await db.Database.ExecuteSqlRawAsync("DELETE FROM gold_spend_daily WHERE \"PlayerId\" = {0}", new object[] { playerId }, timeout.Token);
                // The quest line's latches and claims.
                await db.Database.ExecuteSqlRawAsync("DELETE FROM quest_line_claims WHERE \"PlayerId\" = {0}", new object[] { playerId }, timeout.Token);

                // Modul: A PURGED ACCOUNT MUST NOT LEAVE A WORKING CREDENTIAL
                // BEHIND, and a refresh token is one.
                //
                // Keyed on AccountId, not PlayerId - these are issued by the
                // auth routes, which run before anything has resolved a
                // PlayerRecord. RedeemRefreshTokenAsync does not check that the
                // account still exists (it has no reason to; the row IS the
                // authority), so a surviving row would go on minting valid JWTs
                // for an account that had been erased. That is both a live
                // session after a deletion request and, under GDPR, personal
                // data that was supposed to be gone.
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PlayerRefreshTokens\" WHERE \"AccountId\" = {0}", new object[] { player.PlayerGuid }, timeout.Token);

                // Short-lived and useless once PlayerRecords is gone -
                // CompleteResetAsync cannot find the player and refuses - but
                // there is no reason to leave an hour of somebody's email
                // address's shadow lying in a table after they asked for it to
                // be erased.
                //
                // Modul: THROUGH THE MODEL, NOT A HAND-WRITTEN TABLE NAME
                // (2026-11-01). This was raw SQL against "PasswordResetTokens",
                // but the table is password_reset_tokens ([Table] on the
                // entity). Every purge threw on that line, the catch below
                // rolled the whole transaction back and printed one console
                // line - so no erasure request had ever completed. Found when
                // the same sequence was run by hand to delete the smoke-test
                // guests. ExecuteDeleteAsync asks EF for the table name.
                await db.PasswordResetTokens.Where(t => t.PlayerId == playerId).ExecuteDeleteAsync(timeout.Token);

                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PlayerRecords\" WHERE \"Id\" = {0}", new object[] { playerId }, timeout.Token);

                await transaction.CommitAsync(timeout.Token);

                if (_redis?.IsConnected == true)
                {
                    var redisDb = _redis.GetDatabase();
                    await redisDb.KeyDeleteAsync(new RedisKey[]
                    {
                        RedisSessionCache.SessionStateKey(playerId),
                        RedisSessionCache.GoldBufferKey(playerId),
                        PushNotificationTriggerEngine.PushTokenCacheKey(playerId)
                    });
                    await redisDb.SetRemoveAsync(RedisSessionCache.DirtyPlayersSetKey, playerId);
                }

                TelemetryStreamer.TryWrite(new TelemetryEvent
                {
                    PlayerId = playerId,
                    EventType = 7,
                    Value1 = 57,
                    Value2 = 1,
                    Timestamp = Environment.TickCount64
                });
                return true;
            }
            catch (Exception ex)
            {
                await transaction.SafeRollbackAsync(CancellationToken.None);
                Console.WriteLine($"GDPR purge failed for player {playerId}: {ex.Message}");
                return false;
            }
        }
    }
}
