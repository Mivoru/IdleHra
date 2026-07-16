using System;
using System.Diagnostics.Tracing;

namespace FolkIdle.Server.Engine
{
    // Modul: diagnostic middleware that subscribes to FolkIdleEventSource's
    // BroadcastSnapshotEnd events and maintains a rolling P99 over the last
    // WindowSize broadcast cycles, logging a PerformanceAlert (and emitting
    // the EventSource event of the same name) whenever that P99 exceeds
    // AlertThresholdMicroseconds. Both the sample window and the sort
    // scratch space are fixed-size arrays allocated once in the
    // constructor, never resized or reallocated per event - the only
    // per-event costs are a bounds-checked array write, an Array.Copy into
    // the scratch buffer, and an in-place Array.Sort, none of which touch
    // the managed heap. This runs once per broadcast cycle (once per
    // second, see SimulationEngine's _ticksSinceLastBroadcast gate), not
    // once per 10 Hz tick, so it is deliberately not held to the same
    // zero-allocation bar as the tick loop itself - only the underlying
    // buffers are required to be pre-allocated, which they are.
    public sealed class BroadcastLatencyProfiler : EventListener
    {
        private const int WindowSize = 128;
        private const long AlertThresholdMicroseconds = 50_000L;

        private readonly long[] _latencyWindowMicroseconds = new long[WindowSize];
        private readonly long[] _sortScratch = new long[WindowSize];
        private readonly object _lock = new object();
        private int _writeIndex;
        private int _sampleCount;

        // EventListener.OnEventSourceCreated fires for every EventSource in
        // the process, including ones created before this listener existed
        // (the runtime replays them) - filtering by name here is what
        // scopes this listener to FolkIdle-Server only, rather than every
        // EventSource loaded into the process (including framework-internal
        // ones this class has no interest in).
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (string.Equals(eventSource.Name, "FolkIdle-Server", StringComparison.Ordinal))
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId != FolkIdleEventSource.EventIds.BroadcastSnapshotEnd)
            {
                return;
            }

            if (eventData.Payload == null || eventData.Payload.Count < 2)
            {
                return;
            }

            if (eventData.Payload[0] is not long elapsedMicroseconds || eventData.Payload[1] is not long activePlayerCount)
            {
                return;
            }

            long p99Microseconds;
            lock (_lock)
            {
                _latencyWindowMicroseconds[_writeIndex] = elapsedMicroseconds;
                _writeIndex = (_writeIndex + 1) % WindowSize;
                if (_sampleCount < WindowSize)
                {
                    _sampleCount++;
                }

                p99Microseconds = ComputeP99Locked();
            }

            if (p99Microseconds > AlertThresholdMicroseconds)
            {
                FolkIdleEventSource.Log.PerformanceAlert(p99Microseconds, activePlayerCount);
                Console.WriteLine($"PerformanceAlert: BroadcastSnapshot P99 latency {p99Microseconds / 1000.0:F2}ms exceeds the 50ms threshold (active players: {activePlayerCount}).");
            }
        }

        // Must be called while holding _lock - copies the live ring buffer
        // into the pre-allocated scratch array before sorting so the
        // in-flight window itself is never reordered out from under a
        // concurrent write.
        private long ComputeP99Locked()
        {
            if (_sampleCount == 0)
            {
                return 0L;
            }

            Array.Copy(_latencyWindowMicroseconds, _sortScratch, _sampleCount);
            Array.Sort(_sortScratch, 0, _sampleCount);

            int index = (int)Math.Ceiling(_sampleCount * 0.99) - 1;
            if (index < 0) index = 0;
            if (index >= _sampleCount) index = _sampleCount - 1;

            return _sortScratch[index];
        }
    }
}
