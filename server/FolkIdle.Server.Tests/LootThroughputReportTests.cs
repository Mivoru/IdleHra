using System;
using System.IO;
using System.Reflection;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The throughput line is the only thing that can tell "the tick never
    /// enqueued" from "the worker wrote nothing" when loot stops. It went to
    /// production and printed nothing while drops were demonstrably flowing,
    /// which makes it worse than no telemetry at all - it reads as silence
    /// meaning "no requests".
    /// </summary>
    public class LootThroughputReportTests
    {
        private readonly ITestOutputHelper _o;

        public LootThroughputReportTests(ITestOutputHelper o) => _o = o;

        private static void SetStatic(string name, long value) =>
            typeof(CombatLootEngine)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, value);

        [Fact]
        public void ItPrintsWhenKillsWereRolled()
        {
            var engine = new CombatLootEngine(null!, new PlayerSessionRegistry());
            var report = typeof(CombatLootEngine)
                .GetMethod("ReportLootThroughput", BindingFlags.NonPublic | BindingFlags.Instance)!;

            SetStatic("_requestsDrained", 12);
            SetStatic("_killsRolled", 12);
            SetStatic("_equipmentWritten", 3);
            SetStatic("_materialsGranted", 5);

            var captured = new StringWriter();
            var previous = Console.Out;
            Console.SetOut(captured);
            try
            {
                // First call: the gate has never fired, so it should report.
                report.Invoke(engine, null);
            }
            finally
            {
                Console.SetOut(previous);
            }

            string output = captured.ToString();
            _o.WriteLine($"captured: [{output.Trim()}]");
            Assert.Contains("Loot:", output);
            Assert.Contains("12 kills rolled", output);
            Assert.Contains("3 equipment", output);
        }

        [Fact]
        public void ItStaysQuietWhenNothingWasRolled()
        {
            var engine = new CombatLootEngine(null!, new PlayerSessionRegistry());
            var report = typeof(CombatLootEngine)
                .GetMethod("ReportLootThroughput", BindingFlags.NonPublic | BindingFlags.Instance)!;

            SetStatic("_requestsDrained", 0);
            SetStatic("_killsRolled", 0);

            var captured = new StringWriter();
            var previous = Console.Out;
            Console.SetOut(captured);
            try
            {
                report.Invoke(engine, null);
            }
            finally
            {
                Console.SetOut(previous);
            }

            Assert.DoesNotContain("Loot:", captured.ToString());
        }

        [Fact]
        public void AQuietFirstMinuteDoesNotSILENCETheNextOne()
        {
            // Modul: THE BUG THIS FILE EXISTS FOR.
            //
            // The gate stamped its clock BEFORE checking whether there was
            // anything to say, so an empty first call - the overwhelmingly
            // likely one, three seconds after start-up - consumed the window
            // and every subsequent call inside the next minute returned early.
            // With a busy queue the loop calls this every three seconds, and
            // the counters were reset only on a successful print, so it settled
            // into never printing at all while loot poured through.
            var engine = new CombatLootEngine(null!, new PlayerSessionRegistry());
            var report = typeof(CombatLootEngine)
                .GetMethod("ReportLootThroughput", BindingFlags.NonPublic | BindingFlags.Instance)!;

            SetStatic("_requestsDrained", 0);
            SetStatic("_killsRolled", 0);

            var captured = new StringWriter();
            var previous = Console.Out;
            Console.SetOut(captured);
            try
            {
                report.Invoke(engine, null);   // nothing to say
                SetStatic("_requestsDrained", 40);
                SetStatic("_killsRolled", 40);
                report.Invoke(engine, null);   // now there is, immediately after
            }
            finally
            {
                Console.SetOut(previous);
            }

            Assert.Contains("40 kills rolled", captured.ToString());
        }
    }
}
