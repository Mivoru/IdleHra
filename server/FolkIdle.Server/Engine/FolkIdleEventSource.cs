using System.Diagnostics.Tracing;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    // Modul: custom EventSource for the 1Hz broadcast snapshot path -
    // SimulationEngine.cs's per-second state-broadcast loop, the source of
    // the micro-hitches this class exists to diagnose. Every Write* method
    // below is restricted to WriteEvent overloads with a genuine built-in
    // fast path (int/long argument combinations only) - EventSource falls
    // back to a params object[] overload for argument shapes it does not
    // have a dedicated overload for, which boxes every argument on every
    // call. Timings are carried as integer microseconds rather than double
    // milliseconds specifically so every event fits the WriteEvent(int,
    // long, long) or WriteEvent(int, long) shape, not because microsecond
    // precision itself matters here.
    //
    // IsEnabled() guards every Verbose-level Write* call so the hot path
    // pays only a single boolean check per broadcast/per-packet when no
    // listener is attached (the normal case outside of active
    // diagnostics) - PerformanceAlert is Warning level and intentionally
    // unconditional, since it is already rate-limited to at most once per
    // broadcast cycle by BroadcastLatencyProfiler, not once per tick.
    [EventSource(Name = "FolkIdle-Server")]
    public sealed class FolkIdleEventSource : EventSource
    {
        public static readonly FolkIdleEventSource Log = new FolkIdleEventSource();

        private FolkIdleEventSource()
        {
        }

        public static class EventIds
        {
            public const int BroadcastSnapshotStart = 1;
            public const int BroadcastSnapshotEnd = 2;
            public const int PacketSerializationLatency = 3;
            public const int PerformanceAlert = 4;
        }

        [Event(EventIds.BroadcastSnapshotStart, Level = EventLevel.Verbose)]
        public void BroadcastSnapshotStart(long activePlayerCount)
        {
            if (IsEnabled())
            {
                WriteEvent(EventIds.BroadcastSnapshotStart, activePlayerCount);
            }
        }

        [Event(EventIds.BroadcastSnapshotEnd, Level = EventLevel.Verbose)]
        public void BroadcastSnapshotEnd(long elapsedMicroseconds, long activePlayerCount)
        {
            if (IsEnabled())
            {
                WriteEvent(EventIds.BroadcastSnapshotEnd, elapsedMicroseconds, activePlayerCount);
            }
        }

        [Event(EventIds.PacketSerializationLatency, Level = EventLevel.Verbose)]
        public void PacketSerializationLatency(long playerId, long elapsedMicroseconds)
        {
            if (IsEnabled())
            {
                WriteEvent(EventIds.PacketSerializationLatency, playerId, elapsedMicroseconds);
            }
        }

        // Modul: not IsEnabled()-guarded - BroadcastLatencyProfiler only
        // calls this at most once per broadcast cycle (once per second),
        // already well below any rate that would justify skipping it when
        // no ETW/EventPipe listener is attached, and a threshold breach is
        // exactly the kind of event that should still reach Console.WriteLine
        // (see BroadcastLatencyProfiler.OnEventWritten) even when nothing is
        // subscribed to this EventSource specifically at the tracing layer.
        [Event(EventIds.PerformanceAlert, Level = EventLevel.Warning)]
        public void PerformanceAlert(long p99LatencyMicroseconds, long activePlayerCount)
        {
            WriteEvent(EventIds.PerformanceAlert, p99LatencyMicroseconds, activePlayerCount);
        }
    }
}
