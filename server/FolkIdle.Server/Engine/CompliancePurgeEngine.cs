using System;
using System.Data;
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

        public CompliancePurgeEngine(IServiceProvider serviceProvider, IConnectionMultiplexer? redis)
        {
            _serviceProvider = serviceProvider;
            _redis = redis;
        }

        public void QueueGdprPurge(long playerId)
        {
            if (playerId <= 0)
            {
                return;
            }

            _ = Task.Run(async () => await ExecuteGdprPurgeAsync(playerId));
        }

        private async Task ExecuteGdprPurgeAsync(long playerId)
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
                    await transaction.RollbackAsync(timeout.Token);
                    return;
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
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PasswordResetTokens\" WHERE \"PlayerId\" = {0}", new object[] { playerId }, timeout.Token);

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
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                Console.WriteLine($"GDPR purge failed for player {playerId}: {ex.Message}");
            }
        }
    }
}
