using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TelemetryEvent
    {
        public long PlayerId;
        public byte EventType;
        public int Value1;
        public int Value2;
        public long Timestamp;
    }

    public static class TelemetryStreamer
    {
        private static readonly Channel<TelemetryEvent> _channel = Channel.CreateBounded<TelemetryEvent>(
            new BoundedChannelOptions(10000)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        private static Task? _consumerTask;
        private static IConnectionMultiplexer? _redis;

        public static void ConfigureRedis(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public static void TryWrite(TelemetryEvent ev)
        {
            _channel.Writer.TryWrite(ev);
            var redis = _redis;
            if (redis != null && redis.IsConnected)
            {
                try
                {
                    var db = redis.GetDatabase();
                    _ = db.HashIncrementAsync("telemetry:hot_counts", $"{ev.PlayerId}:{ev.EventType}", 1L, CommandFlags.FireAndForget);
                    _ = db.HashSetAsync(
                        $"telemetry:last_event:{ev.PlayerId}",
                        new HashEntry[]
                        {
                            new("event_type", (int)ev.EventType),
                            new("value1", ev.Value1),
                            new("value2", ev.Value2),
                            new("timestamp", ev.Timestamp)
                        },
                        CommandFlags.FireAndForget);
                }
                catch
                {
                }
            }
        }

        private static void SerializeToBuffer(in TelemetryEvent ev, byte[] buffer)
        {
            var span = MemoryMarshal.CreateReadOnlySpan(ref System.Runtime.CompilerServices.Unsafe.AsRef(in ev), 1);
            var bytes = MemoryMarshal.AsBytes(span);
            bytes.CopyTo(buffer);
        }

        public static void StartConsumerAsync(CancellationToken cancellationToken)
        {
            _consumerTask = Task.Run(async () =>
            {
                using var fs = new FileStream("telemetry.bin", FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true);
                var buffer = new byte[Marshal.SizeOf<TelemetryEvent>()];
                try
                {
                    await foreach (var ev in _channel.Reader.ReadAllAsync(cancellationToken))
                    {
                        SerializeToBuffer(in ev, buffer);
                        await fs.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
                        await fs.FlushAsync(cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Graceful exit
                }
            }, CancellationToken.None);
        }

        public static void CompleteWriter()
        {
            _channel.Writer.TryComplete();
            if (_consumerTask != null)
            {
                _consumerTask.Wait();
            }
        }
    }
}
