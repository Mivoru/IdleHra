using System;
using StackExchange.Redis;

namespace FolkIdle.Server.Engine
{
    public sealed class RedisSessionCache
    {
        public const string DirtyPlayersSetKey = "players:dirty_session_state";

        private readonly IConnectionMultiplexer _redis;

        public RedisSessionCache(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public static RedisKey SessionStateKey(long playerId) => $"player:{playerId}:session_state";

        public static RedisKey GoldBufferKey(long playerId) => $"player:{playerId}:gold_buffer";

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        public static RedisKey WoodBufferKey(long playerId) => $"player:{playerId}:wood_buffer";
        public static RedisKey StoneBufferKey(long playerId) => $"player:{playerId}:stone_buffer";
        public static RedisKey IronOreBufferKey(long playerId) => $"player:{playerId}:iron_ore_buffer";

        public async System.Threading.Tasks.Task SetQuarantineFlagAsync(long playerId, bool isQuarantined)
        {
            if (_redis.IsConnected)
            {
                var db = _redis.GetDatabase();
                await db.HashSetAsync(SessionStateKey(playerId), "is_quarantined", isQuarantined ? 1 : 0);
            }
        }

        public bool TryStoreFrame(ref TickStatePayload state)
        {
            if (!_redis.IsConnected)
            {
                return false;
            }

            try
            {
                var db = _redis.GetDatabase();
                var batch = db.CreateBatch();
                RedisKey sessionKey = SessionStateKey(state.PlayerId);

                var entries = new HashEntry[]
                {
                    new("player_id", state.PlayerId),
                    new("current_level", state.CurrentLevel),
                    new("current_xp", state.CurrentXp),
                    new("selected_lineage_id", state.SelectedLineageId),
                    new("last_logout_ts", state.LastLogoutTimestamp),
                    new("accumulated_time_bank_seconds", state.AccumulatedTimeBankMs / 1000L),
                    new("logic_epoch_counter", state.LogicEpochCounter),
                    new("banked_chrono_seconds", state.BankedChronoSeconds),
                    new("is_chrono_accelerating", state.IsChronoAccelerating ? 1 : 0),
                    new("is_quarantined", state.IsQuarantined || state.Quarantine_Active ? 1 : 0),
                    new("current_gold_frame", state.CurrentGold),
                    new("inventory_space_remaining", state.InventorySpaceRemaining),
                    new("ticks_since_last_flush", state.TicksSinceLastFlush),
                    new("updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                };

                _ = batch.HashSetAsync(sessionKey, entries, CommandFlags.FireAndForget);

                if (state.RedisPendingGoldDelta != 0L)
                {
                    _ = batch.HashIncrementAsync(GoldBufferKey(state.PlayerId), "delta", state.RedisPendingGoldDelta, CommandFlags.FireAndForget);
                    _ = batch.HashSetAsync(GoldBufferKey(state.PlayerId), "updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), When.Always, CommandFlags.FireAndForget);
                    state.RedisPendingGoldDelta = 0L;
                }

                if (state.PendingWoodDelta != 0L)
                {
                    _ = batch.HashIncrementAsync(WoodBufferKey(state.PlayerId), "delta", state.PendingWoodDelta, CommandFlags.FireAndForget);
                    _ = batch.HashSetAsync(WoodBufferKey(state.PlayerId), "updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), When.Always, CommandFlags.FireAndForget);
                    state.PendingWoodDelta = 0L;
                }

                if (state.PendingStoneDelta != 0L)
                {
                    _ = batch.HashIncrementAsync(StoneBufferKey(state.PlayerId), "delta", state.PendingStoneDelta, CommandFlags.FireAndForget);
                    _ = batch.HashSetAsync(StoneBufferKey(state.PlayerId), "updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), When.Always, CommandFlags.FireAndForget);
                    state.PendingStoneDelta = 0L;
                }

                if (state.PendingIronDelta != 0L)
                {
                    _ = batch.HashIncrementAsync(IronOreBufferKey(state.PlayerId), "delta", state.PendingIronDelta, CommandFlags.FireAndForget);
                    _ = batch.HashSetAsync(IronOreBufferKey(state.PlayerId), "updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), When.Always, CommandFlags.FireAndForget);
                    state.PendingIronDelta = 0L;
                }

                _ = batch.SetAddAsync(DirtyPlayersSetKey, state.PlayerId, CommandFlags.FireAndForget);
                batch.Execute();

                state.RequiresRedisFlush = false;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis session cache write failed for player {state.PlayerId}: {ex.Message}");
                return false;
            }
        }
    }
}
