using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace FolkIdle.Server.Network
{
    /// <summary>
    /// Wall time spent per REST route, dev tools only (docs/design/LOCKSTEP_SIM.md,
    /// phase 1d).
    ///
    /// Modul: IN LOCKSTEP A BOT'S REST CALLS ARE THE CRITICAL PATH. P0 saw the
    /// server use two cores while the tick thread used 5% of one, and the bots
    /// had made 1,191 what-if projections in four minutes. Under lockstep the
    /// world waits while a bot thinks, so whichever route eats that time IS the
    /// simulation's speed. This says which, on /metrics, without a profiler.
    /// Off (one bool read) without FOLKIDLE_DEV_TOOLS; capped at 256 paths so a
    /// path with an id in it cannot grow it without bound.
    /// </summary>
    internal static class DevRouteTiming
    {
        private const int MaxRoutes = 256;
        private static readonly ConcurrentDictionary<string, long[]> Routes = new(StringComparer.Ordinal);

        internal static readonly bool Enabled = NetworkBroadcastSystem.DevToolsEnabled();

        internal static void Record(string? path, long stopwatchTicks)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!Routes.TryGetValue(path, out var cell))
            {
                if (Routes.Count >= MaxRoutes) path = "(other)";
                cell = Routes.GetOrAdd(path, _ => new long[2]);
            }
            Interlocked.Increment(ref cell[0]);
            Interlocked.Add(ref cell[1], stopwatchTicks);
        }

        internal static void AppendMetrics(StringBuilder body)
        {
            if (!Enabled || Routes.IsEmpty) return;
            var inv = CultureInfo.InvariantCulture;
            body.Append("# HELP folkidle_dev_route_seconds_total Wall seconds spent in each REST route (dev tools only).\n");
            body.Append("# TYPE folkidle_dev_route_seconds_total counter\n");
            foreach (var (route, cell) in Routes.OrderByDescending(kv => Interlocked.Read(ref kv.Value[1])).Select(kv => (kv.Key, kv.Value)))
            {
                double seconds = (double)Interlocked.Read(ref cell[1]) / Stopwatch.Frequency;
                body.Append("folkidle_dev_route_seconds_total{route=\"").Append(route.Replace("\"", "")).Append("\"} ").Append(seconds.ToString("0.###", inv)).Append('\n');
                body.Append("folkidle_dev_route_requests_total{route=\"").Append(route.Replace("\"", "")).Append("\"} ").Append(Interlocked.Read(ref cell[0])).Append('\n');
            }
        }
    }
}
