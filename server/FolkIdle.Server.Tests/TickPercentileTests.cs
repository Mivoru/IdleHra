using System.Linq;
using FolkIdle.Server.Domain.Combat;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 43: /metrics reports tick p50/p95/p99/max over the last 600 ticks,
    /// because the cumulative whole-millisecond buckets cannot say what p99
    /// was after a deploy.
    /// </summary>
    public class TickPercentileTests
    {
        [Fact]
        public void PercentilesAreReadFromTheSortedWindowInMilliseconds()
        {
            // 1..600 ms in microseconds, shuffled.
            var micros = Enumerable.Range(1, 600).Select(i => i * 1000).Reverse().ToArray();

            var p = SimulationEngine.ComputeTickPercentiles(micros, micros.Length);

            Assert.Equal(600, p.Samples);
            Assert.Equal(300.0, p.P50Ms);
            Assert.Equal(570.0, p.P95Ms);
            Assert.Equal(594.0, p.P99Ms);
            Assert.Equal(600.0, p.MaxMs);
        }

        [Fact]
        public void APartlyFilledWindowUsesOnlyTheSamplesTaken()
        {
            var micros = new int[SimulationEngine.RecentTickWindow];
            micros[0] = 4_000; micros[1] = 30_000; micros[2] = 5_000;

            var p = SimulationEngine.ComputeTickPercentiles(micros, 3);

            Assert.Equal(3, p.Samples);
            Assert.Equal(5.0, p.P50Ms);
            Assert.Equal(30.0, p.P99Ms);
            Assert.Equal(0, SimulationEngine.ComputeTickPercentiles(micros, 0).Samples);
        }
    }
}
