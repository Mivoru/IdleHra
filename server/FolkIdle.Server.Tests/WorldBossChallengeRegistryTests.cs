using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 36 Phase 1: practice challenges over REST, without HTTP. The
    /// service is driven directly with a fake clock; nothing here touches a
    /// database, an attempt or the real weak plate.
    /// </summary>
    public class WorldBossChallengeRegistryTests
    {
        private long _now = 1_000_000;
        private const long Player = 950_060_001L;

        private WorldBossStrikeService Service(BossMinigameMode mode, WorldBossChallengeRegistry? registry = null) =>
            new(registry ?? new WorldBossChallengeRegistry(new SeededRandomNumberGenerator(36)),
                new BossMinigameSettings { Mode = mode }, () => _now);

        /// <summary>A wheel tap time that is on a moving wheel and lands on a plate.</summary>
        private static double MovingTap(ChallengeDto c, double from)
        {
            for (double t = from; t < WorldBossStrikeRules.MaxPlayMs; t += 10)
            {
                bool frozen = c.Interrupts.Any(i => t >= i.TellAtMs && t < i.TellAtMs + i.InterruptMs);
                if (!frozen) return t;
            }
            throw new InvalidOperationException("no moving time left");
        }

        [Fact]
        public void IssuingTwiceReturnsTheSameChallenge()
        {
            var service = Service(BossMinigameMode.Practice);
            var first = service.IssueChallenge(Player, practice: true);
            var second = service.IssueChallenge(Player, practice: true);

            Assert.Equal(WorldBossStrikeResult.Issued, first.Result);
            Assert.Equal(WorldBossStrikeResult.Outstanding, second.Result);
            Assert.Equal(first.Challenge!.ChallengeId, second.Challenge!.ChallengeId);
            Assert.Equal(32, first.Challenge.ChallengeId.Length);
            Assert.True(first.Challenge.Practice);
            Assert.Equal(0, first.Challenge.BrokenPlateMask);
            Assert.Equal(255, first.Challenge.RevealedWeakPlate);
            Assert.Equal(WorldBossStrikeResult.Outstanding, service.GetChallenge(Player).Result);
        }

        [Fact]
        public void EveryRouteIsDisabledWithTheFlagOff()
        {
            var service = Service(BossMinigameMode.Off);
            Assert.Equal(WorldBossStrikeResult.Disabled, service.GetChallenge(Player).Result);
            Assert.Equal("off", service.GetChallenge(Player).Mode);
            Assert.Equal(WorldBossStrikeResult.Disabled, service.IssueChallenge(Player, practice: true).Result);
            Assert.Equal(WorldBossStrikeResult.Disabled, service.IssueChallenge(Player, practice: false).Result);
            Assert.Equal(WorldBossStrikeResult.Disabled, service.Throw(Player, new ThrowRequest { ChallengeId = "x" }).Result);
            Assert.Equal(WorldBossStrikeResult.Disabled, service.ScorePractice(Player, new StrikeRequest { ChallengeId = "x" }).Result);
            Assert.Equal(WorldBossStrikeResult.Disabled, service.Strike(Player, new StrikeRequest()).Result);
        }

        [Theory]
        [InlineData(BossMinigameMode.Practice)]
        [InlineData(BossMinigameMode.Wheel)]
        public void PhaseOneRefusesEveryRealPathWhateverTheFlag(BossMinigameMode mode)
        {
            var service = Service(mode);
            Assert.Equal(WorldBossStrikeResult.Disabled, service.IssueChallenge(Player, practice: false).Result);
            Assert.Equal(WorldBossStrikeResult.Disabled, service.Strike(Player, new StrikeRequest { Mode = "Auto", Plate = 1 }).Result);
        }

        [Fact]
        public void PracticeAnswersFromItsOwnDecoyAndNeverFromTheRealBoss()
        {
            // The service cannot consult the real weak plate: it is never given
            // anything that knows it. Pinned in the source, since a later
            // edit that injected WorldBossEngine here would compile silently.
            string source = File.ReadAllText(ServerFile("Domain", "Combat", "WorldBossStrike", "WorldBossStrikeService.cs"));
            Assert.DoesNotContain("WorldBossEngine", source);

            // And the decoy is drawn per challenge: across many practice
            // challenges it takes more than one value.
            var registry = new WorldBossChallengeRegistry(new SeededRandomNumberGenerator(7));
            var decoys = new HashSet<int>();
            for (int i = 0; i < 60; i++)
            {
                var (challenge, _) = registry.IssueOrGet(Player + i, practice: true, enraged: false, _now);
                Assert.InRange(challenge.DecoyWeakPlate, 0, WorldBossStrikeRules.PlateCount - 1);
                decoys.Add(challenge.DecoyWeakPlate);
            }
            Assert.True(decoys.Count > 1, "every practice challenge had the same decoy");

            // A throw whose landing is the decoy says WeakHit; any other does not.
            var service = Service(BossMinigameMode.Practice, registry);
            var issued = service.IssueChallenge(Player, practice: true).Challenge!;
            var stored = registry.Get(Player, practice: true)!;
            double tap = MovingTap(issued, 0);
            _now += WorldBossStrikeRules.CountdownMs + (long)tap + 100;
            var answer = service.Throw(Player, new ThrowRequest { ChallengeId = issued.ChallengeId, Seq = 0, TapMs = tap });
            Assert.Equal(WorldBossStrikeResult.Landed, answer.Result);
            Assert.Equal(answer.Class >= SpearClass.Plate && answer.Plate == stored.DecoyWeakPlate, answer.WeakHit);
        }

        [Fact]
        public void ARepeatedSeqReturnsTheStoredAnswer()
        {
            var service = Service(BossMinigameMode.Practice);
            var c = service.IssueChallenge(Player, practice: true).Challenge!;
            double tap = MovingTap(c, 100);
            _now += WorldBossStrikeRules.CountdownMs + 5_000;

            var first = service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 0, TapMs = tap });
            // The repeat carries a different time; it still gets the first answer.
            var repeat = service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 0, TapMs = MovingTap(c, tap + 900) });

            Assert.Equal(WorldBossStrikeResult.Landed, first.Result);
            Assert.Equal(first.Plate, repeat.Plate);
            Assert.Equal(first.Class, repeat.Class);
            Assert.Equal(first.WeakHit, repeat.WeakHit);
        }

        [Fact]
        public void ASeqBeyondTheSpearsLeftIsOutOfSpears()
        {
            var service = Service(BossMinigameMode.Practice);
            var c = service.IssueChallenge(Player, practice: true).Challenge!;
            var firstTell = c.Interrupts[0];
            _now += WorldBossStrikeRules.CountdownMs + WorldBossStrikeRules.MaxPlayMs;

            // Past the first tell with no read: one spear is gone, so Seq 4 is the sixth.
            double after = MovingTap(c, firstTell.TellAtMs + firstTell.InterruptMs);
            Assert.Equal(WorldBossStrikeResult.OutOfSpears,
                service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 4, TapMs = after }).Result);

            // With that interrupt read correctly, the same spear is fine.
            var read = new ParryEntry(firstTell.Index, ShieldWheelSchedule.CorrectChoice(firstTell.Tell), firstTell.TellAtMs + 300);
            Assert.Equal(WorldBossStrikeResult.Landed,
                service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 4, TapMs = after, Parries = new() { read } }).Result);

            Assert.Equal(WorldBossStrikeResult.Refused,
                service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 5, TapMs = after }).Result);
        }

        // Code review, 2026-09-25: a counter whose interrupt has no parry in the
        // same request used to throw inside the service and answer HTTP 500.
        [Fact]
        public void ACounterWithNoParryIsDroppedNotAnException()
        {
            var service = Service(BossMinigameMode.Practice);
            var c = service.IssueChallenge(Player, practice: true).Challenge!;
            var tell = c.Interrupts[0];
            _now += WorldBossStrikeRules.CountdownMs + WorldBossStrikeRules.MaxPlayMs;

            var answer = service.Throw(Player, new ThrowRequest
            {
                ChallengeId = c.ChallengeId,
                Seq = 0,
                TapMs = tell.TellAtMs + 500,
                Counter = new CounterEntry(tell.Index, 0, tell.TellAtMs + 500, 1),
                Parries = new(),
            });

            Assert.Equal(WorldBossStrikeResult.Landed, answer.Result);
            Assert.Equal(-1, answer.Plate);
            Assert.Equal(SpearClass.None, answer.Class);
        }

        // Code review, 2026-09-25: a reopened screen restarted at Seq 0 with
        // five spears, so its throws reused Seqs the server had answered and
        // got the OLD answers back. The challenge now carries what it has.
        [Fact]
        public void AReopenedChallengeCarriesItsThrowsAndParries()
        {
            var service = Service(BossMinigameMode.Practice);
            var c = service.IssueChallenge(Player, practice: true).Challenge!;
            var tell = c.Interrupts[0];
            var read = new ParryEntry(tell.Index, ShieldWheelSchedule.CorrectChoice(tell.Tell), tell.TellAtMs + 300);
            double tap = MovingTap(c, 100);
            _now += WorldBossStrikeRules.CountdownMs + (long)tell.TellAtMs + 1_000;

            var first = service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 0, TapMs = tap, Parries = new() { read } });
            var counter = new CounterEntry(tell.Index, 1, tell.TellAtMs + 600, 3);
            var second = service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 1, TapMs = counter.TapMs, Counter = counter, Parries = new() { read } });
            Assert.Equal(SpearClass.Seam, second.Class);

            var reopened = service.IssueChallenge(Player, practice: true);
            Assert.Equal(WorldBossStrikeResult.Outstanding, reopened.Result);
            var throws = reopened.Challenge!.Throws;
            Assert.Equal(new[] { 0, 1 }, throws.Select(t => t.Seq));
            Assert.Equal(first.Plate, throws[0].Plate);
            Assert.Equal(first.Class, throws[0].Class);
            Assert.Equal(3, throws[1].Plate);
            Assert.NotNull(throws[1].Counter);
            Assert.Equal(new[] { read }, reopened.Challenge.Parries);
        }

        [Fact]
        public void ATooEarlyThrowIsNotStored()
        {
            var registry = new WorldBossChallengeRegistry(new SeededRandomNumberGenerator(9));
            var service = Service(BossMinigameMode.Practice, registry);
            var c = service.IssueChallenge(Player, practice: true).Challenge!;
            double tap = MovingTap(c, 2_000);

            // Only half the countdown has passed: the spear cannot have been thrown yet.
            _now += WorldBossStrikeRules.CountdownMs / 2;
            Assert.Equal(WorldBossStrikeResult.TooEarly,
                service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 0, TapMs = tap }).Result);
            Assert.Empty(registry.Get(Player, practice: true)!.ThrowsSnapshot());

            // Later, the same Seq is answered as a fresh throw, not from a stored refusal.
            _now += WorldBossStrikeRules.CountdownMs + (long)tap;
            Assert.Equal(WorldBossStrikeResult.Landed,
                service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 0, TapMs = tap }).Result);
        }

        [Fact]
        public void AThrowForAForeignOrMissingChallengeIsNoChallenge()
        {
            var service = Service(BossMinigameMode.Practice);
            service.IssueChallenge(Player, practice: true);
            Assert.Equal(WorldBossStrikeResult.NoChallenge,
                service.Throw(Player, new ThrowRequest { ChallengeId = new string('0', 32), Seq = 0, TapMs = 100 }).Result);
            Assert.Equal(WorldBossStrikeResult.NoChallenge,
                service.Throw(Player + 1, new ThrowRequest { ChallengeId = "anything", Seq = 0, TapMs = 100 }).Result);
        }

        [Fact]
        public void PracticeExpiresSilently()
        {
            var registry = new WorldBossChallengeRegistry(new SeededRandomNumberGenerator(3));
            var service = Service(BossMinigameMode.Practice, registry);
            var c = service.IssueChallenge(Player, practice: true).Challenge!;

            _now += WorldBossStrikeRules.CountdownMs + WorldBossStrikeRules.MaxPlayMs + WorldBossChallengeRegistry.ExpiryGraceMs;
            Assert.Empty(registry.ExpireDue(_now));
            Assert.Null(registry.Get(Player, practice: true));
            Assert.Equal(WorldBossStrikeResult.NoChallenge, service.GetChallenge(Player).Result);
            Assert.Equal(WorldBossStrikeResult.NoChallenge, service.ScorePractice(Player, new StrikeRequest { ChallengeId = c.ChallengeId }).Result);
        }

        [Fact]
        public void APracticeScoreDealsNoDamageAndClosesTheChallenge()
        {
            var service = Service(BossMinigameMode.Practice);
            var c = service.IssueChallenge(Player, practice: true).Challenge!;
            double t1 = MovingTap(c, 200);
            double t2 = MovingTap(c, t1 + 400);
            _now += WorldBossStrikeRules.CountdownMs + WorldBossStrikeRules.MaxPlayMs;

            var scored = service.ScorePractice(Player, new StrikeRequest
            {
                ChallengeId = c.ChallengeId,
                Taps = new() { new WheelTap(0, t1), new WheelTap(1, t2) },
                // Every interrupt read correctly, so no spear is lost.
                Parries = c.Interrupts.Select(i => new ParryEntry(i.Index, ShieldWheelSchedule.CorrectChoice(i.Tell), i.TellAtMs + 300)).ToList(),
            });

            Assert.Equal(WorldBossStrikeResult.PracticeScored, scored.Result);
            Assert.Equal(SubmissionVerdict.Accepted, scored.Verdict);
            Assert.True(scored.NoDamageDealt);
            Assert.Equal(2, scored.Landings.Count);
            Assert.InRange(scored.Multiplier, WorldBossStrikeRules.Floor, WorldBossStrikeRules.Cap);
            Assert.True(scored.Played >= scored.Multiplier * scored.PlateMultiplier - 1e-9);

            // The challenge is spent: scoring it again finds nothing.
            Assert.Equal(WorldBossStrikeResult.NoChallenge, service.ScorePractice(Player, new StrikeRequest { ChallengeId = c.ChallengeId }).Result);
        }

        [Fact]
        public void TheFlagParsesAndAnythingUnknownIsOff()
        {
            Assert.Equal(BossMinigameMode.Off, BossMinigameSettings.FromEnvironment(null).Mode);
            Assert.Equal(BossMinigameMode.Off, BossMinigameSettings.FromEnvironment("").Mode);
            Assert.Equal(BossMinigameMode.Off, BossMinigameSettings.FromEnvironment("on").Mode);
            Assert.Equal(BossMinigameMode.Practice, BossMinigameSettings.FromEnvironment(" Practice ").Mode);
            Assert.Equal(BossMinigameMode.Wheel, BossMinigameSettings.FromEnvironment("wheel").Mode);
        }

        // --- telemetry and the client's result mirror --------------------------

        internal static string ServerFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "FolkIdle.Server"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName, "FolkIdle.Server" }.Concat(parts).ToArray());
        }

        [Fact]
        public void OnlyWorldBossStrikeTelemetryWritesEventTypeEight()
        {
            string root = Path.GetDirectoryName(ServerFile("Program.cs"))!;
            var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(p => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(p), @"EventType\s*=\s*8\b"))
                .Select(Path.GetFileName)
                .ToList();
            Assert.Equal(new[] { "WorldBossStrikeTelemetry.cs" }, offenders);
        }

        [Fact]
        public void TheClientsResultListIsExportedFromTheEnum()
        {
            // Regenerate-and-compare: the fixture is what client_web's
            // worldBossResults.test.ts reads. A new result without a client
            // sentence fails there; a stale fixture fails here.
            string path = Path.Combine(Path.GetDirectoryName(ServerFile("Program.cs"))!, "..", "FolkIdle.Server.Tests", "Fixtures", "world_boss_results.json");
            var expected = WorldBossStrikeResults.AllPlayerFacing.Select(r => r.ToString()).ToList();
            var actual = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            Assert.Equal(expected, actual);
        }
    }
}
