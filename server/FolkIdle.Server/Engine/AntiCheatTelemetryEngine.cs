using System;
using System.Collections.Concurrent;
using System.Data;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FolkIdle.Server.Engine
{
    public sealed class AntiCheatTelemetryEngine
    {
        private const int RingSize = 100;
        private const double MacroVarianceThreshold = 0.002;
        private const int MinimumSampleCount = 20;

        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionMultiplexer _redis;
        private readonly PlayerSessionRegistry _playerRegistry;
        private readonly ConcurrentDictionary<long, CommandTimingProfile> _profiles = new();
        private readonly ConcurrentDictionary<long, byte> _shadowBanRequests = new();

        public AntiCheatTelemetryEngine(IServiceProvider serviceProvider, IConnectionMultiplexer redis, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _redis = redis;
            _playerRegistry = playerRegistry;
        }

        public void RecordCommand(long playerId, byte commandType)
        {
            if (playerId <= 0 || commandType == 31)
            {
                return;
            }

            long now = Environment.TickCount64;
            var profile = _profiles.GetOrAdd(playerId, _ => new CommandTimingProfile());
            if (profile.RecordAndCheck(now))
            {
                RequestShadowBan(playerId, 54, 1);
            }
        }

        public void RequestShadowBan(long playerId, int reasonCode, int detailCode)
        {
            if (playerId <= 0 || !_shadowBanRequests.TryAdd(playerId, 1))
            {
                return;
            }

            TelemetryStreamer.TryWrite(new TelemetryEvent
            {
                PlayerId = playerId,
                EventType = 3,
                Value1 = reasonCode,
                Value2 = detailCode,
                Timestamp = Environment.TickCount64
            });

            _playerRegistry.QuarantineNotificationQueue.Enqueue(new QuarantineNotification { PlayerId = playerId });

            _ = Task.Run(async () =>
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                try
                {
                    var player = await db.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (player != null)
                    {
                        player.IsQuarantined = true;
                        player.Quarantine_Active = true;
                        await db.SaveChangesAsync();
                    }

                    await transaction.CommitAsync();

                    if (_redis.IsConnected)
                    {
                        var redisDb = _redis.GetDatabase();
                        await redisDb.HashSetAsync(RedisSessionCache.SessionStateKey(playerId), new HashEntry[]
                        {
                            new("is_quarantined", 1),
                            new("shadow_reason", reasonCode),
                            new("shadow_detail", detailCode)
                        });
                        await redisDb.SetAddAsync(RedisSessionCache.DirtyPlayersSetKey, playerId);
                    }
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine($"Shadow quarantine failed for player {playerId}: {ex.Message}");
                    _shadowBanRequests.TryRemove(playerId, out _);
                }
            });
        }

        public static uint GenerateChallengeSeed(long playerId, long logicEpochCounter, long tickCounter)
        {
            uint seed = unchecked((uint)playerId);
            seed ^= unchecked((uint)(playerId >> 32));
            seed ^= unchecked((uint)logicEpochCounter * 0x9E3779B9u);
            seed ^= unchecked((uint)tickCounter * 0x85EBCA6Bu);
            return XorShift32(seed == 0u ? 0xA341316Cu : seed);
        }

        public static uint ComputeChallengeHash(uint challengeSeed, long playerId, long logicEpochCounter)
        {
            uint value = challengeSeed;
            value ^= unchecked((uint)playerId);
            value = XorShift32(value);
            value ^= unchecked((uint)(playerId >> 32));
            value = XorShift32(value + unchecked((uint)logicEpochCounter));
            value ^= 0xC2B2AE35u;
            return XorShift32(value);
        }

        private static uint XorShift32(uint value)
        {
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            return value == 0u ? 0x6D2B79F5u : value;
        }

        private sealed class CommandTimingProfile
        {
            private readonly long[] _timestamps = new long[RingSize];
            private int _cursor;
            private int _count;

            public bool RecordAndCheck(long timestampMs)
            {
                lock (_timestamps)
                {
                    _timestamps[_cursor] = timestampMs;
                    _cursor = (_cursor + 1) % RingSize;
                    if (_count < RingSize) _count++;
                    if (_count < MinimumSampleCount) return false;

                    double sum = 0.0;
                    double sumSquares = 0.0;
                    int intervalCount = _count - 1;
                    int start = (_cursor - _count + RingSize) % RingSize;
                    long previous = _timestamps[start];

                    for (int i = 1; i < _count; i++)
                    {
                        int index = (start + i) % RingSize;
                        long current = _timestamps[index];
                        double intervalSeconds = (current - previous) / 1000.0;
                        previous = current;
                        sum += intervalSeconds;
                        sumSquares += intervalSeconds * intervalSeconds;
                    }

                    double mean = sum / intervalCount;
                    double variance = (sumSquares / intervalCount) - (mean * mean);
                    return variance >= 0.0 && variance < MacroVarianceThreshold;
                }
            }
        }
    }
}
