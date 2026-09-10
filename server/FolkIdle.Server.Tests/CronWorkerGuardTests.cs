using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A BACKGROUND WORKER THAT CAN THROW IS A FEATURE THAT CAN VANISH.
    ///
    /// Modul: every `StartCron` in this server dispatches its loop into a bare
    /// `Task.Run`. An exception anywhere in that loop ends the task - no log, no
    /// restart, no other symptom. `CombatLootEngine` lost its entire drain that
    /// way and equipment stopped dropping for every player on the live server
    /// while kills, XP, gold, the codex and gathering all kept working. The
    /// trigger was not a code fault: Supabase's session pooler refused the
    /// sixteenth client, thrown from `CreateDbContext`.
    ///
    /// CLAUDE.md carried the sentence "Checked 2026-09-09: the other eleven
    /// StartCron loops do wrap their bodies". That is a manual audit with a
    /// date on it, and a manual audit with a date on it is a guard that expires
    /// the moment somebody adds the twelfth. This file is that sentence made
    /// mechanical.
    ///
    /// WHAT IT CAN AND CANNOT SEE. It reads source text, so it proves a catch
    /// EXISTS, not that it is in the right place - and placement is most of the
    /// lesson, because a guard that begins after `CreateScope` or
    /// `BeginTransactionAsync` is not a guard: that is exactly where the
    /// connection is acquired and where the throw comes from. So it also
    /// insists that the engines it knows about stay a known list. A new
    /// StartCron fails this test until a person has looked at it, which is the
    /// whole point.
    /// </summary>
    public class CronWorkerGuardTests
    {
        private readonly ITestOutputHelper _output;
        public CronWorkerGuardTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// Every engine that starts a background loop, and a note on what its
        /// loop is for. Adding one here is a deliberate act; the test below
        /// fails on any StartCron that is not listed.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> KnownCronEngines =
            new Dictionary<string, string>
            {
                ["CombatLootEngine"] = "drains the loot and gathering grant queues",
                ["CodexEngine"] = "drains codex kill credits",
                ["GuildMatchmakingEngine"] = "weekly guild pairing - guarded 2026-09-10, the last one",
                ["PushNotificationTriggerEngine"] = "polls delayed push triggers and drains the outbound queue",
                ["LiveOpsTickEngine"] = "world boss windows and the daily reset boundary",
                ["LeaderboardCronEngine"] = "periodic leaderboard snapshots",
                ["RedisWriteBehindEngine"] = "flushes dirty players from Redis to Postgres",
                ["AchievementEngine"] = "periodic achievement evaluation",
                ["EcoTelemetryEngine"] = "ten-minute economy audit",
                ["GuildRaidEngine"] = "raid ticks",
                ["GuildWarEngine"] = "war ticks",
                ["GuildWarSnapshotEngine"] = "war standings refresh",
                ["OfflineCapNotifier"] = "mails players whose offline bank has filled",
                ["SeasonalRotationEngine"] = "season rollover",
            };

        // Modul: FOURTEEN, NOT TWELVE - and finding that out is why this file
        // exists. CLAUDE.md said "the other eleven StartCron loops do wrap
        // their bodies", checked by hand on 2026-09-09. There were thirteen
        // besides GuildMatchmakingEngine by the time anybody counted
        // mechanically, and seven of them had never appeared in any audit. All
        // seven turned out to be guarded, and each one's try does open
        // immediately inside its loop - which was luck rather than process
        // until now.

        private static string ServerSourceRoot()
        {
            // Walk up from the test binary to the repository, which is the only
            // fixed point available at runtime.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server", "Engine")))
            {
                dir = dir.Parent;
            }

            Assert.True(dir != null, "could not locate server/FolkIdle.Server/Engine from the test binary");
            return Path.Combine(dir!.FullName, "server", "FolkIdle.Server", "Engine");
        }

        private static IEnumerable<(string Engine, string Path, string Source)> CronSources()
        {
            foreach (string path in Directory.EnumerateFiles(ServerSourceRoot(), "*.cs", SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(path);
                if (!Regex.IsMatch(source, @"\bpublic\s+void\s+StartCron\s*\(")) continue;
                yield return (Path.GetFileNameWithoutExtension(path), path, source);
            }
        }

        [Fact]
        public void EveryCronEngineIsAccountedFor()
        {
            var found = CronSources().Select(entry => entry.Engine).OrderBy(name => name).ToList();

            _output.WriteLine($"{found.Count} engines start a background loop:");
            foreach (string engine in found)
            {
                _output.WriteLine($"  {engine,-32} {(KnownCronEngines.TryGetValue(engine, out var why) ? why : "** NOT LISTED **")}");
            }

            var unlisted = found.Where(engine => !KnownCronEngines.ContainsKey(engine)).ToList();
            Assert.True(
                unlisted.Count == 0,
                "A new StartCron loop exists and nobody has decided whether it is guarded: " +
                string.Join(", ", unlisted) +
                ". Read its loop body, confirm the catch wraps the DbContext acquisition, then add it to KnownCronEngines.");

            var vanished = KnownCronEngines.Keys.Where(engine => !found.Contains(engine)).ToList();
            Assert.True(
                vanished.Count == 0,
                "Listed as a cron engine but no longer starts one: " + string.Join(", ", vanished) +
                ". Remove it from KnownCronEngines rather than leaving a stale entry.");
        }

        [Fact]
        public void EveryCronEngineCatchesSomething()
        {
            var unguarded = new List<string>();

            foreach (var (engine, _, source) in CronSources())
            {
                // Modul: `catch (Exception` specifically, not any catch. A
                // handler for one narrow type is the shape that let
                // CombatLootEngine die - it caught what it expected and the
                // pooler threw something else.
                if (!Regex.IsMatch(source, @"catch\s*\(\s*Exception\b"))
                {
                    unguarded.Add(engine);
                }
            }

            Assert.True(
                unguarded.Count == 0,
                "These start a background loop with no `catch (Exception` anywhere in the file, " +
                "so one throw ends them for the life of the process, silently: " +
                string.Join(", ", unguarded));
        }

        [Fact]
        public void TheOneThatDiedInProductionStillCatchesInsideItsDrain()
        {
            // Modul: a named regression, because this is the file that took loot
            // down for every player on the live server. It is not enough for
            // CombatLootEngine to catch SOMEWHERE - the catch has to be inside
            // the per-item drain, or one bad grant still ends the queue.
            var loot = CronSources().SingleOrDefault(entry => entry.Engine == "CombatLootEngine");
            Assert.False(loot.Source is null, "CombatLootEngine no longer starts a cron loop");

            int catches = Regex.Matches(loot.Source, @"catch\s*\(\s*Exception\b").Count;
            _output.WriteLine($"CombatLootEngine has {catches} `catch (Exception` handlers");

            // One at the loop and at least one per drained queue. Two is the
            // floor; the number today is higher and is allowed to grow.
            Assert.True(catches >= 2, $"CombatLootEngine has only {catches} broad handlers; the drain needs its own");
        }
    }
}
