using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    public readonly struct AccountAnalyticsWriteBuffer
    {
        public readonly Guid AccountId;
        public readonly uint EventTypeHash;
        public readonly long TimestampEpoch;
        public readonly long PayloadMetric;

        public AccountAnalyticsWriteBuffer(Guid accountId, uint eventTypeHash, long timestampEpoch, long payloadMetric)
        {
            AccountId = accountId;
            EventTypeHash = eventTypeHash;
            TimestampEpoch = timestampEpoch;
            PayloadMetric = payloadMetric;
        }
    }

    public sealed class TelemetryStreamingEngine
    {
        public const uint KpiAutoEatDepletedHaltHash = 0xA0EAD001u;
        public const uint ClientTelemetryBurstHash = 0xC1E17001u;
        private const int DrainBatchLimit = 512;
        private const int ClientPacketPackedEventCapacity = 16;

        private readonly IDbContextFactory<FolkIdleDbContext> _contextFactory;
        private readonly ConcurrentDictionary<long, LiveSessionContext> _liveSessionContexts;
        private readonly List<AccountAnalyticsWriteBuffer> _drainBuffer = new(DrainBatchLimit);
        private CancellationTokenSource _cts = new();
        private Task? _workerTask;

        public TelemetryStreamingEngine(
            IDbContextFactory<FolkIdleDbContext> contextFactory,
            ConcurrentDictionary<long, LiveSessionContext> liveSessionContexts)
        {
            _contextFactory = contextFactory;
            _liveSessionContexts = liveSessionContexts;
        }

        public void Start()
        {
            if (_workerTask != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => ExecuteAsync(_cts.Token));
        }

        public void StopAndDrain()
        {
            _cts.Cancel();
            try
            {
                _workerTask?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
            }

            DrainOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            _workerTask = null;
        }

        public static long PackTelemetryMetric(uint eventTypeHash, long payloadMetric)
        {
            long metricBits = payloadMetric & 0xFFFFFFFFL;
            return ((long)eventTypeHash << 32) | metricBits;
        }

        public static uint UnpackEventTypeHash(long packedMetric)
        {
            return unchecked((uint)(packedMetric >> 32));
        }

        public static long UnpackPayloadMetric(long packedMetric)
        {
            int signedMetric = unchecked((int)(packedMetric & 0xFFFFFFFFL));
            return signedMetric;
        }

        public void WriteServerKpi(long playerId, Guid accountId, uint eventTypeHash, long payloadMetric)
        {
            if (_liveSessionContexts.TryGetValue(playerId, out var sessionContext))
            {
                sessionContext.UpdateAccountId(accountId);
                sessionContext.WriteTelemetryEvent(PackTelemetryMetric(eventTypeHash, payloadMetric));
            }
        }

        public void EnqueueClientTelemetryBurst(Guid accountId, long playerId, ClientCommandPacket packet)
        {
            Task.Run(() => ProcessClientTelemetryBurst(accountId, playerId, packet));
        }

        private unsafe void ProcessClientTelemetryBurst(Guid accountId, long playerId, ClientCommandPacket packet)
        {
            if (!_liveSessionContexts.TryGetValue(playerId, out var sessionContext))
            {
                return;
            }

            sessionContext.UpdateAccountId(accountId);
            int eventCount = (int)Math.Min(packet.TelemetryEventCount, 32U);
            uint fallbackEventTypeHash = ClientTelemetryBurstHash;
            for (int i = 0; i < eventCount; i++)
            {
                long packedMetric = i < ClientPacketPackedEventCapacity ? ReadPackedClientEvent(ref packet, i) : 0L;
                if (packedMetric == 0L)
                {
                    packedMetric = PackTelemetryMetric(fallbackEventTypeHash, i + 1L);
                }

                sessionContext.WriteTelemetryEvent(packedMetric);
            }
        }

        private static unsafe long ReadPackedClientEvent(ref ClientCommandPacket packet, int index)
        {
            if (index < 8)
            {
                fixed (byte* source = packet.DeviceTokenBytes)
                {
                    return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<long>(source + index * sizeof(long));
                }
            }

            fixed (byte* source = packet.RawTransactionReceipt)
            {
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<long>(source + (index - 8) * sizeof(long));
            }
        }

        private async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HasHighWatermarkSession() ? 50 : 1000, cancellationToken);
                    await DrainOnceAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private bool HasHighWatermarkSession()
        {
            foreach (var kvp in _liveSessionContexts)
            {
                if (kvp.Value.ShouldPrioritizeTelemetryFlush())
                {
                    return true;
                }
            }

            return false;
        }

        public async Task DrainOnceAsync(CancellationToken cancellationToken)
        {
            _drainBuffer.Clear();
            long timestampEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var kvp in _liveSessionContexts)
            {
                var sessionContext = kvp.Value;
                Guid accountId = sessionContext.AccountId == Guid.Empty
                    ? ResolveAccountId(sessionContext.PlayerId)
                    : sessionContext.AccountId;

                for (int i = 0; i < 64; i++)
                {
                    if (!sessionContext.TryDrainTelemetryEvent(i, out long packedMetric))
                    {
                        continue;
                    }

                    _drainBuffer.Add(new AccountAnalyticsWriteBuffer(
                        accountId,
                        UnpackEventTypeHash(packedMetric),
                        timestampEpoch,
                        UnpackPayloadMetric(packedMetric)));

                    if (_drainBuffer.Count >= DrainBatchLimit)
                    {
                        await InsertBatchAsync(_drainBuffer, cancellationToken);
                        _drainBuffer.Clear();
                    }
                }
            }

            if (_drainBuffer.Count > 0)
            {
                await InsertBatchAsync(_drainBuffer, cancellationToken);
                _drainBuffer.Clear();
            }
        }

        private async Task InsertBatchAsync(List<AccountAnalyticsWriteBuffer> rows, CancellationToken cancellationToken)
        {
            if (rows.Count == 0)
            {
                return;
            }

            int fixedBatchSize = 32;
            var sql = new StringBuilder(fixedBatchSize * 48 + 100);
            sql.Append("INSERT INTO \"AccountAnalyticsLogs\" (\"AccountId\", \"EventTypeHash\", \"TimestampEpoch\", \"PayloadMetric\") SELECT * FROM (VALUES ");

            for (int i = 0; i < fixedBatchSize; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                int p = i * 4;
                sql.Append('(')
                    .Append('{').Append(p).Append("},")
                    .Append('{').Append(p + 1).Append("},")
                    .Append('{').Append(p + 2).Append("},")
                    .Append('{').Append(p + 3).Append("})");
            }
            sql.Append(") AS t(\"AccountId\", \"EventTypeHash\", \"TimestampEpoch\", \"PayloadMetric\") WHERE t.\"AccountId\" IS NOT NULL;");

            for (int chunkStart = 0; chunkStart < rows.Count; chunkStart += fixedBatchSize)
            {
                var parameters = new object[fixedBatchSize * 4];
                int chunkLimit = Math.Min(fixedBatchSize, rows.Count - chunkStart);

                for (int i = 0; i < fixedBatchSize; i++)
                {
                    int p = i * 4;
                    if (i < chunkLimit)
                    {
                        var row = rows[chunkStart + i];
                        parameters[p] = row.AccountId;
                        parameters[p + 1] = (long)row.EventTypeHash;
                        parameters[p + 2] = row.TimestampEpoch;
                        parameters[p + 3] = row.PayloadMetric;
                    }
                    else
                    {
                        parameters[p] = DBNull.Value;
                        parameters[p + 1] = DBNull.Value;
                        parameters[p + 2] = DBNull.Value;
                        parameters[p + 3] = DBNull.Value;
                    }
                }

                await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                await context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, cancellationToken);
            }
            
            GlobalEngineState.AddAnalyticsEventsLogged(rows.Count);
        }

        private static Guid ResolveAccountId(long playerId)
        {
            Span<byte> bytes = stackalloc byte[16];
            BitConverter.TryWriteBytes(bytes[..8], playerId);
            BitConverter.TryWriteBytes(bytes[8..], playerId ^ 0x71A7E11D5F3759DFL);
            return new Guid(bytes);
        }
    }
}
