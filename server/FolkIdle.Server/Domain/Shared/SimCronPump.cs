using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// The gameplay background workers, driven by the TICK under lockstep
    /// (docs/design/LOCKSTEP_SIM.md, phase 1b).
    ///
    /// Modul: A CRON ON A WALL DELAY HAS NO MEANING IN LOCKSTEP (2026-11-03).
    /// The codex, loot, grant, achievement, chest, live-ops, leaderboard and
    /// season workers each loop on Task.Delay of WALL time. At x200 that made a
    /// region-boss kill reach the codex (which the boss mask and region opens
    /// derive from) up to 17 game minutes late, and loot up to 10; in lockstep,
    /// where the engine runs as fast as the CPU allows, the lag would be
    /// whatever the thread pool felt like. So under lockstep their loops never
    /// start: each registers its ONE cycle here with its production period in
    /// ticks, and the engine runs the due ones on the tick thread and WAITS for
    /// them before the tick (owner review: the result must land on a
    /// deterministic tick, not "somewhere in T+50..T+500"). The lag a player
    /// sees is then exactly production's.
    ///
    /// A clock jump (nobody online) runs a due cron once on landing, not once
    /// per skipped period - each of these cycles drains a queue or compares
    /// "now" against a boundary, so one run sees everything the skipped ones
    /// would have.
    /// </summary>
    public sealed class SimCronPump
    {
        private sealed class Job
        {
            public required string Name;
            public required long IntervalTicks;
            public required Func<Task> RunOnce;
            public long NextDue;
            public long Runs;
            public long Failures;
        }

        private readonly List<Job> _jobs = new();

        /// <summary>
        /// Registers a cycle. <paramref name="periodMs"/> is the worker's production
        /// wall period, turned into ticks of <paramref name="tickMs"/>. The first
        /// run is one period in, as the production loops delay before their first
        /// cycle; <paramref name="runAtStart"/> is for the ones that run first.
        /// </summary>
        public void Register(string name, int periodMs, Func<Task> runOnce, bool runAtStart = false, int tickMs = 100)
        {
            long interval = Math.Max(1, (periodMs + tickMs - 1) / tickMs);
            _jobs.Add(new Job { Name = name, IntervalTicks = interval, RunOnce = runOnce, NextDue = runAtStart ? 0 : interval });
        }

        public int Count => _jobs.Count;

        /// <summary>Runs every cycle due by <paramref name="tick"/>, in registration order, and waits for each.</summary>
        public int RunDue(long tick)
        {
            int ran = 0;
            foreach (var job in _jobs)
            {
                if (tick < job.NextDue) continue;
                try
                {
                    job.RunOnce().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // Same rule as every production worker: one failed cycle
                    // never stops the worker.
                    job.Failures++;
                    Console.WriteLine($"[SimCronPump] {job.Name} cycle failed at tick {tick}: {ex.Message}");
                }
                job.Runs++;
                job.NextDue = tick + job.IntervalTicks;
                ran++;
            }
            return ran;
        }

        public IEnumerable<(string Name, long IntervalTicks, long Runs, long Failures)> Describe()
        {
            foreach (var j in _jobs) yield return (j.Name, j.IntervalTicks, j.Runs, j.Failures);
        }
    }
}
