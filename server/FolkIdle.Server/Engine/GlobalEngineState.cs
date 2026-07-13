namespace FolkIdle.Server.Engine
{
    public static class GlobalEngineState
    {
        private static long _totalAnalyticsEventsLoggedCount;
        private static long _totalTelemetryDroppedEventsCount;
        private static long _activeConnectionThroughput;
        public static volatile bool IsShuttingDown = false;
        public static volatile int GlobalXpMultiplier = 100;
        public static volatile int GlobalDropMultiplier = 100;
        public static volatile int GlobalGoldDropMultiplier = 100;
        public static volatile int ActiveEventType = 0;
        public static volatile int NotificationQueueStateLength = 0;
        // Cold boot gate: set to true only after ColdRecoveryCoordinator completes reconstruction.
        public static volatile bool IsColdBootRecoveryComplete = false;
        public static volatile bool IsEraTransitionActive = false;
        public static readonly object EraTransitionLock = new object();

        public static long TotalAnalyticsEventsLoggedCount => System.Threading.Interlocked.Read(ref _totalAnalyticsEventsLoggedCount);
        public static long TotalTelemetryDroppedEventsCount => System.Threading.Interlocked.Read(ref _totalTelemetryDroppedEventsCount);
        public static long ActiveConnectionThroughput => System.Threading.Interlocked.Read(ref _activeConnectionThroughput);

        public static void AddAnalyticsEventsLogged(long count)
        {
            if (count > 0)
            {
                System.Threading.Interlocked.Add(ref _totalAnalyticsEventsLoggedCount, count);
            }
        }

        public static void AddTelemetryDroppedEvents(long count)
        {
            if (count > 0)
            {
                System.Threading.Interlocked.Add(ref _totalTelemetryDroppedEventsCount, count);
            }
        }

        public static void SetActiveConnectionThroughput(long throughput)
        {
            System.Threading.Interlocked.Exchange(ref _activeConnectionThroughput, throughput < 0L ? 0L : throughput);
        }
    }
}
