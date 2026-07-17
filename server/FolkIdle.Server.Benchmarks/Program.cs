namespace FolkIdle.Server.Benchmarks
{
    // Modul: Phase 5, Part 2. Entry point for the multi-client WebSocket
    // load test harness - a standalone tool run manually against a live
    // FolkIdle server instance, not a unit test. See LoadTestHarness.cs for
    // the actual bot state machine and telemetry aggregation.
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            LoadTestOptions options = LoadTestOptions.Parse(args);
            return await LoadTestHarness.RunAsync(options).ConfigureAwait(false);
        }
    }
}
