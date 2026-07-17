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
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MentorshipContracts\" WHERE \"MentorPlayerId\" = {0} OR \"MenteePlayerId\" = {0}", new object[] { playerId }, timeout.Token);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MarketOrderRecords\" WHERE \"SellerId\" = {0}", new object[] { playerId }, timeout.Token);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PlayerDeviceRegistrations\" WHERE \"PlayerId\" = {0}", new object[] { playerId }, timeout.Token);
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
