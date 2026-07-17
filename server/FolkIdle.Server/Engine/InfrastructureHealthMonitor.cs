using System;
using System.Net;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public struct InfrastructureHealthStatusBlock
    {
        public long ManagedHeapBytes;
        public int ThreadPoolBusyWorkers;
        public long DroppedTelemetryEvents;
        public uint MemoryLoadMetric;
    }

    public static class InfrastructureHealthMonitor
    {
        private static readonly byte[] OkBytes = { 79, 75 };
        private static readonly byte[] UnavailableBytes = { 85, 78, 65, 86, 65, 73, 76, 65, 66, 76, 69 };
        private const long ManagedHeapReadinessLimitBytes = 768L * 1024L * 1024L;
        private static InfrastructureHealthStatusBlock _lastStatus;
        public static InfrastructureHealthStatusBlock LastStatus => _lastStatus;

        public static InfrastructureHealthStatusBlock CaptureStatus()
        {
            ThreadPool.GetAvailableThreads(out int availableWorkers, out _);
            ThreadPool.GetMaxThreads(out int maxWorkers, out _);
            long heapBytes = GC.GetTotalMemory(false);
            int busyWorkers = Math.Max(0, maxWorkers - availableWorkers);
            long droppedTelemetry = GlobalEngineState.TotalTelemetryDroppedEventsCount;
            uint memoryMetric = heapBytes <= 0L
                ? 0U
                : (uint)Math.Min(uint.MaxValue, heapBytes / 1024L);

            var status = new InfrastructureHealthStatusBlock
            {
                ManagedHeapBytes = heapBytes,
                ThreadPoolBusyWorkers = busyWorkers,
                DroppedTelemetryEvents = droppedTelemetry,
                MemoryLoadMetric = memoryMetric
            };
            _lastStatus = status;
            return status;
        }

        public static uint GetCurrentNodeMemoryLoadMetric()
        {
            return CaptureStatus().MemoryLoadMetric;
        }

        public static bool IsLive()
        {
            return !GlobalEngineState.IsShuttingDown;
        }

        public static bool IsReady()
        {
            var status = CaptureStatus();
            return IsLive() &&
                GlobalEngineState.IsColdBootRecoveryComplete &&
                status.ManagedHeapBytes < ManagedHeapReadinessLimitBytes;
        }

        public static void WritePlainHealth(HttpListenerResponse response, bool ready)
        {
            response.StatusCode = ready ? 200 : 503;
            byte[] payload = ready ? OkBytes : UnavailableBytes;
            response.ContentType = "text/plain";
            response.ContentLength64 = payload.Length;
            response.OutputStream.Write(payload, 0, payload.Length);
            response.Close();
        }
    }
}
