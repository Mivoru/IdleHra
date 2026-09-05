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

            SetStatic("_killsEnqueued", 12);
            SetStatic("_codexKills", 12);
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
            Assert.Contains("worker drained 12 requests / 12 kills", output);
            Assert.Contains("tick saw", output);
            Assert.Contains("3 equipment", output);
        }

        [Fact]
        public void ItBeatsEvenWithNothingToReport()
        {
            var engine = new CombatLootEngine(null!, new PlayerSessionRegistry());
            var report = typeof(CombatLootEngine)
                .GetMethod("ReportLootThroughput", BindingFlags.NonPublic | BindingFlags.Instance)!;

            SetStatic("_requestsDrained", 0);
            SetStatic("_killsRolled", 0);
            SetStatic("_killsEnqueued", 0);
            SetStatic("_codexKills", 0);

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

            // Now a HEARTBEAT: it must speak even with nothing to report, because
            // silence was indistinguishable from a stopped loop.
            Assert.Contains("Loot:", captured.ToString());
        }

        [Fact]
        public void CountersSurviveASkippedBeat()
        {
            // Modul: THE BUG THIS FILE EXISTS FOR, in its current form.
            //
            // The gate used to stamp its clock before deciding it had nothing
            // to say, so an empty call three seconds after start-up ate the
            // window; with the loop calling this every three seconds and the
            // counters resetting only on a print, it settled into never
            // printing while loot poured through. It is a heartbeat now, so the
            // remaining guarantee is the other half: work counted between two
            // beats must still be there at the next one, never dropped by the
            // beat that was skipped.
            var engine = new CombatLootEngine(null!, new PlayerSessionRegistry());
            var report = typeof(CombatLootEngine)
                .GetMethod("ReportLootThroughput", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var lastReport = typeof(CombatLootEngine)
                .GetField("_lastLootReportMs", BindingFlags.NonPublic | BindingFlags.Instance)!;

            SetStatic("_requestsDrained", 0);
            SetStatic("_killsRolled", 0);
            SetStatic("_killsEnqueued", 0);
            SetStatic("_codexKills", 0);

            var captured = new StringWriter();
            var previous = Console.Out;
            Console.SetOut(captured);
            try
            {
                report.Invoke(engine, null);            // the beat, empty
                SetStatic("_requestsDrained", 40);
                SetStatic("_killsRolled", 40);
                report.Invoke(engine, null);            // too soon: skipped

                // A minute later, the same work must still be reported.
                lastReport.SetValue(engine, (long)lastReport.GetValue(engine)! - 61_000);
                report.Invoke(engine, null);
            }
            finally
            {
                Console.SetOut(previous);
            }

            Assert.Contains("worker drained 40 requests / 40 kills", captured.ToString());
        }
    }
}
