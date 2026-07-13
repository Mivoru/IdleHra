using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FolkIdle.Server.Engine
{
    public sealed class RedisWriteBehindEngine
    {
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);

        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionMultiplexer _redis;
        private CancellationTokenSource _cts = new();
        private Task? _workerTask;

        public RedisWriteBehindEngine(IServiceProvider serviceProvider, IConnectionMultiplexer redis)
        {
            _serviceProvider = serviceProvider;
            _redis = redis;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => ExecuteAsync(_cts.Token));
        }

        public async Task StopAndFlushAsync()
        {
            _cts.Cancel();
            if (_workerTask != null)
            {
                try
                {
                    await _workerTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            await FlushNowAsync(CancellationToken.None);
        }

        public async Task FlushNowAsync(CancellationToken cancellationToken)
        {
            if (!_redis.IsConnected)
            {
                return;
            }

            var redisDb = _redis.GetDatabase();
            RedisValue[] dirtyPlayers;
            try
            {
                dirtyPlayers = await redisDb.SetMembersAsync(RedisSessionCache.DirtyPlayersSetKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis write-behind dirty set read failed: {ex.Message}");
                return;
            }

            if (dirtyPlayers.Length == 0)
            {
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var syncedPlayers = new List<long>(dirtyPlayers.Length);
            var appliedGoldDeltas = new List<(long PlayerId, long Delta)>(dirtyPlayers.Length);

            try
            {
                for (int i = 0; i < dirtyPlayers.Length; i++)
                {
                    if (!TryReadLong(dirtyPlayers[i], out long playerId) || playerId <= 0)
                    {
                        continue;
                    }

                    HashEntry[] sessionEntries = await redisDb.HashGetAllAsync(RedisSessionCache.SessionStateKey(playerId));
                    if (sessionEntries.Length == 0)
                    {
                        syncedPlayers.Add(playerId);
                        continue;
                    }

                    await UpsertPlayerRecordAsync(db, playerId, sessionEntries, cancellationToken);
                    long appliedDelta = await ApplyGoldDeltaAsync(db, redisDb, playerId, cancellationToken);
                    if (appliedDelta != 0L)
                    {
                        appliedGoldDeltas.Add((playerId, appliedDelta));
                    }
                    syncedPlayers.Add(playerId);
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                for (int i = 0; i < appliedGoldDeltas.Count; i++)
                {
                    await redisDb.HashIncrementAsync(RedisSessionCache.GoldBufferKey(appliedGoldDeltas[i].PlayerId), "delta", -appliedGoldDeltas[i].Delta);
                }

                for (int i = 0; i < syncedPlayers.Count; i++)
                {
                    await redisDb.SetRemoveAsync(RedisSessionCache.DirtyPlayersSetKey, syncedPlayers[i]);
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                Console.WriteLine($"Redis write-behind flush failed: {ex.Message}");
            }
        }

        private async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(FlushInterval, cancellationToken);
                    await FlushNowAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Redis write-behind loop failed: {ex.Message}");
                }
            }
        }

        private static async Task UpsertPlayerRecordAsync(FolkIdleDbContext db, long playerId, HashEntry[] entries, CancellationToken cancellationToken)
        {
            var player = await db.PlayerRecords
                .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                .FirstOrDefaultAsync(cancellationToken);

            if (player == null)
            {
                player = new PlayerRecord { Id = playerId };
                db.PlayerRecords.Add(player);
            }

            player.CurrentLevel = ReadInt(entries, "current_level", player.CurrentLevel);
            player.CurrentXp = ReadLong(entries, "current_xp", player.CurrentXp);
            player.SelectedLineageId = ReadInt(entries, "selected_lineage_id", player.SelectedLineageId);
            player.LastLogoutTimestamp = ReadLong(entries, "last_logout_ts", player.LastLogoutTimestamp);
            player.AccumulatedTimeBankSeconds = ReadInt(entries, "accumulated_time_bank_seconds", player.AccumulatedTimeBankSeconds);
            player.LogicEpochCounter = Math.Max(player.LogicEpochCounter, ReadLong(entries, "logic_epoch_counter", player.LogicEpochCounter));
            player.BankedChronoSeconds = ReadDouble(entries, "banked_chrono_seconds", player.BankedChronoSeconds);
            player.IsChronoAccelerating = ReadInt(entries, "is_chrono_accelerating", player.IsChronoAccelerating ? 1 : 0) != 0;
            bool isQuarantined = ReadInt(entries, "is_quarantined", player.IsQuarantined ? 1 : 0) != 0;
            player.IsQuarantined = isQuarantined;
            if (isQuarantined)
            {
                player.Quarantine_Active = true;
            }
        }

        private static async Task<long> ApplyGoldDeltaAsync(FolkIdleDbContext db, IDatabase redisDb, long playerId, CancellationToken cancellationToken)
        {
            RedisValue deltaValue = await redisDb.HashGetAsync(RedisSessionCache.GoldBufferKey(playerId), "delta");
            if (!TryReadLong(deltaValue, out long delta) || delta == 0L)
            {
                return 0L;
            }

            var gold = await db.CommodityRecords
                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                .FirstOrDefaultAsync(cancellationToken);

            if (gold == null)
            {
                gold = new CommodityRecord
                {
                    PlayerId = playerId,
                    ItemId = "gold",
                    Quantity = 0L
                };
                db.CommodityRecords.Add(gold);
            }

            gold.Quantity += delta;
            return delta;
        }

        private static int ReadInt(HashEntry[] entries, string field, int fallback)
        {
            long value = ReadLong(entries, field, fallback);
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }

        private static long ReadLong(HashEntry[] entries, string field, long fallback)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name == field && TryReadLong(entries[i].Value, out long value))
                {
                    return value;
                }
            }

            return fallback;
        }

        private static double ReadDouble(HashEntry[] entries, string field, double fallback)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name == field && double.TryParse(entries[i].Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    return value;
                }
            }

            return fallback;
        }

        private static bool TryReadLong(RedisValue value, out long result)
        {
            if (value.IsNull)
            {
                result = 0L;
                return false;
            }

            return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
    }
}
