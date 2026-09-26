using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 36 Phase 2 without a database: the real challenge, the per-attempt
    /// weak plate (spec 3.3.1), the refusals, the consistency rule, lazy
    /// expiry and the secret. The board is a fake whose Submit prices the
    /// order with the same pure rules the engine uses, so what is asserted is
    /// the service's half of the pipeline; the engine's half is
    /// WorldBossStrikeIntegrationTests.
    /// </summary>
    public class WorldBossStrikeServiceTests
    {
        private long _nowMs = 1_800_000_000_000;
        private const long Player = 950_070_001L;
        private const long A = 10_000;

        private sealed class FakeBoard : IWorldBossStrikeBoard
        {
            public bool IsEventActive { get; set; } = true;
            public bool Dead { get; set; }
            public bool IsBossDead() => Dead;
            public long EventEndEpoch { get; set; }
            public long BossCurrentHp { get; set; } = 1_000_000;
            public long BossMaxHp { get; set; } = 1_000_000;
            public byte BrokenPlateMask { get; set; }
            public int Used { get; set; }
            /// <summary>When false, orders are held and never answered (the Queued path).</summary>
            public bool Answer { get; set; } = true;
            public readonly ConcurrentQueue<WorldBossStrikeOrder> Orders = new();

            public Task<int> StrikesUsedTodayAsync(long playerId) => Task.FromResult(Used);

            public void Submit(WorldBossStrikeOrder order)
            {
                Orders.Enqueue(order);
                if (!Answer) return;
                int weak = order.WeakPlate ?? WeakPlateDraw.From(BrokenPlateMask);
                var price = WorldBossStrikeRules.Price(order.Landings, weak, order.Multiplier, BrokenPlateMask);
                if (price.BreakPlate is int plate) BrokenPlateMask |= (byte)(1 << plate);
                Used++;
                order.Complete(new WorldBossStrikeOutcome(order.LandedAs, (long)(A * price.Played), price.Multiplier,
                    price.PlateMultiplier, price.Played, price.BreakPlate ?? -1,
                    order.Landings.Select(l => new LandingDto
                    {
                        Seq = l.Seq, Plate = l.Plate, Class = l.Class, IsCounter = l.IsCounter,
                        WeakHit = l.Class >= SpearClass.Plate && l.Plate == weak,
                    }).ToList()));
            }
        }

        private (WorldBossStrikeService Service, FakeBoard Board, WorldBossChallengeRegistry Registry) Wheel(int seed = 36)
        {
            var board = new FakeBoard { EventEndEpoch = _nowMs / 1000 + 86_400 };
            var registry = new WorldBossChallengeRegistry(new SeededRandomNumberGenerator(seed));
            var service = new WorldBossStrikeService(registry, new BossMinigameSettings { Mode = BossMinigameMode.Wheel },
                () => _nowMs, board, TimeSpan.FromMilliseconds(200));
            return (service, board, registry);
        }

        private static double MovingTap(ChallengeDto c, double from)
        {
            for (double t = from; t < WorldBossStrikeRules.MaxPlayMs; t += 10)
            {
                bool frozen = c.Interrupts.Any(i => t >= i.TellAtMs && t < i.TellAtMs + i.InterruptMs);
                if (!frozen) return t;
            }
            throw new InvalidOperationException("no moving time left");
        }

        private static List<ParryEntry> AllRead(ChallengeDto c) =>
            c.Interrupts.Select(i => new ParryEntry(i.Index, ShieldWheelSchedule.CorrectChoice(i.Tell), i.TellAtMs + 300)).ToList();

        // --- the draw (spec 3.3.1, rules 1 and 3) ---------------------------------

        [Fact]
        public void TheDrawOnlyEverPicksAnUnbrokenPlate()
        {
            for (int mask = 0; mask < WeakPlateDraw.AllPlatesMask; mask++)
            {
                for (int pick = 0; pick < WorldBossStrikeRules.PlateCount; pick++)
                {
                    // Every index the uniform source could return.
                    int fixedPick = pick;
                    int weak = WeakPlateDraw.From(mask, n => Math.Min(fixedPick, n - 1));
                    Assert.True((mask & (1 << weak)) == 0, $"mask {mask:b5} drew broken plate {weak}");
                }
            }
        }

        [Fact]
        public void WithFourBrokenTheDrawAlwaysPicksTheFifth()
        {
            for (int standing = 0; standing < WorldBossStrikeRules.PlateCount; standing++)
            {
                int mask = WeakPlateDraw.AllPlatesMask & ~(1 << standing);
                for (int i = 0; i < 50; i++) Assert.Equal(standing, WeakPlateDraw.From(mask));
            }
        }

        [Fact]
        public void TheLastPlateStandingNeverBreaks()
        {
            // Four broken, and a stale challenge that thinks plate 0 is weak: its
            // spear on plate 4 would complete the mask. Refused under the lock.
            int mask = 0b01111;
            var landings = new[] { new SpearLanding(0, 4, SpearClass.Plate, false) };
            Assert.Null(WorldBossStrikeRules.Price(landings, weakPlate: 0, 1.0, mask).BreakPlate);
            Assert.True(WeakPlateDraw.WouldBreakTheLast(mask, 4));
            Assert.False(WeakPlateDraw.WouldBreakTheLast(0b00111, 4));
        }

        [Fact]
        public void ARealChallengeDrawsItsWeakPlateFromTheBoardAtIssue()
        {
            var (_, board, registry) = Wheel();
            var seen = new HashSet<int>();
            for (int i = 0; i < 200; i++)
            {
                var service = new WorldBossStrikeService(registry, new BossMinigameSettings { Mode = BossMinigameMode.Wheel }, () => _nowMs, board);
                board.BrokenPlateMask = 0b01011; // plates 0, 1, 3 broken
                var issued = service.IssueChallengeAsync(Player + i, practice: false).Result;
                Assert.Equal(WorldBossStrikeResult.Issued, issued.Result);
                var weak = registry.Get(Player + i, practice: false)!.WeakPlate;
                Assert.Contains(weak, new[] { 2, 4 });
                seen.Add(weak);
            }
            Assert.Equal(new HashSet<int> { 2, 4 }, seen);
        }

        // --- refusals: every one answered, nothing issued or spent -----------------

        [Fact]
        public void EveryIneligibleChallengeIsAnsweredAndIssuesNothing()
        {
            var (service, board, registry) = Wheel();

            board.IsEventActive = false;
            Assert.Equal(WorldBossStrikeResult.NotActive, service.IssueChallengeAsync(Player, false).Result.Result);
            board.IsEventActive = true;

            board.Dead = true;
            Assert.Equal(WorldBossStrikeResult.AlreadyDefeated, service.IssueChallengeAsync(Player, false).Result.Result);
            board.Dead = false;

            board.EventEndEpoch = _nowMs / 1000 + 30; // less than countdown + play + margin
            Assert.Equal(WorldBossStrikeResult.TooLateInWindow, service.IssueChallengeAsync(Player, false).Result.Result);
            board.EventEndEpoch = _nowMs / 1000 + 86_400;

            board.Used = 1;
            Assert.Equal(WorldBossStrikeResult.NoAttemptsLeft, service.IssueChallengeAsync(Player, false).Result.Result);
            Assert.Equal(WorldBossStrikeResult.NoAttemptsLeft,
                service.StrikeAsync(Player, new StrikeRequest { Mode = "Auto", Plate = 0 }).Result!.Result);

            Assert.Null(registry.Get(Player, practice: false));
            Assert.Empty(board.Orders);
        }

        [Fact]
        public void AnAutoStrikeWhileAChallengeIsOpenIsRefusedAndChangesNothing()
        {
            var (service, board, _) = Wheel();
            Assert.Equal(WorldBossStrikeResult.Issued, service.IssueChallengeAsync(Player, false).Result.Result);
            var answer = service.StrikeAsync(Player, new StrikeRequest { Mode = "Auto", Plate = 2 }).Result!;
            Assert.Equal(WorldBossStrikeResult.ChallengeOutstanding, answer.Result);
            Assert.Empty(board.Orders);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(-1)]
        [InlineData(5)]
        public void AnAutoStrikeWithNoPlateIsAMalformedRequest(int? plate)
        {
            var (service, board, _) = Wheel();
            Assert.Null(service.StrikeAsync(Player, new StrikeRequest { Mode = "Auto", Plate = plate }).Result);
            Assert.Empty(board.Orders);
        }

        [Fact]
        public void AnAutoStrikeIsTodaysStrikeExactly()
        {
            var (service, board, _) = Wheel();
            var answer = service.StrikeAsync(Player, new StrikeRequest { Mode = "Auto", Plate = 3 }).Result!;
            Assert.Equal(WorldBossStrikeResult.Landed, answer.Result);
            var order = Assert.Single(board.Orders);
            Assert.Null(order.WeakPlate); // drawn under the lock
            Assert.Equal(WorldBossStrikeRules.Floor, order.Multiplier);
            Assert.Equal(new SpearLanding(0, 3, SpearClass.Plate, false), Assert.Single(order.Landings));
            // Either the weak plate (3.0, private WeakHit) or a full blow that breaks it.
            bool weak = answer.Landings.Single().WeakHit;
            Assert.Equal(weak ? 3.0 : 1.0, answer.Played);
            Assert.Equal(weak ? -1 : 3, answer.BrokePlate);
        }

        // --- the wheel strike ---------------------------------------------------

        [Fact]
        public void AnAcceptedWheelLogIsPricedWithTheChallengesWeakPlate()
        {
            var (service, board, registry) = Wheel();
            var c = service.IssueChallengeAsync(Player, false).Result.Challenge!;
            int weak = registry.Get(Player, false)!.WeakPlate;
            var parries = AllRead(c);
            var tell = c.Interrupts[0];
            var counter = new CounterEntry(tell.Index, 0, tell.TellAtMs + 600, weak);
            _nowMs += WorldBossStrikeRules.CountdownMs + WorldBossStrikeRules.MaxPlayMs;

            var answer = service.StrikeAsync(Player, new StrikeRequest
            {
                ChallengeId = c.ChallengeId,
                Parries = parries,
                Counters = new() { counter },
            }).Result!;

            Assert.Equal(WorldBossStrikeResult.Landed, answer.Result);
            var order = Assert.Single(board.Orders);
            Assert.Equal(weak, order.WeakPlate);
            Assert.Equal(0, order.SpearsLost);
            // One guaranteed Seam on the weak plate: P = 3.0, and the auto floor
            // alone guarantees the 3.0 an auto-strike on it pays.
            Assert.True(answer.Played >= 3.0);
            Assert.True(Assert.Single(answer.Landings).WeakHit);
            Assert.Null(registry.Get(Player, false));
        }

        [Fact]
        public void AFinishThatDisagreesWithAnAnsweredThrowIsRefusedAtTheFloor()
        {
            var (service, board, _) = Wheel();
            var c = service.IssueChallengeAsync(Player, false).Result.Challenge!;
            double tap = MovingTap(c, 200);
            _nowMs += WorldBossStrikeRules.CountdownMs + (long)tap + 100;
            var thrown = service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 0, TapMs = tap });
            Assert.Equal(WorldBossStrikeResult.Landed, thrown.Result);

            _nowMs += WorldBossStrikeRules.MaxPlayMs;
            // The log moves the answered spear 40 ms - onto a better angle, say.
            var answer = service.StrikeAsync(Player, new StrikeRequest
            {
                ChallengeId = c.ChallengeId,
                Taps = new() { new WheelTap(0, tap + 40) },
                Parries = AllRead(c),
            }).Result!;

            Assert.Equal(WorldBossStrikeResult.Refused, answer.Result);
            var order = Assert.Single(board.Orders);
            Assert.Equal(WorldBossStrikeRules.Floor, order.Multiplier);
            // Resolved from what the SERVER answered, not from the log.
            var landing = Assert.Single(order.Landings);
            Assert.Equal(thrown.Plate, landing.Plate);
            Assert.Equal(thrown.Class, landing.Class);
        }

        [Fact]
        public void AMalformedLogIsRefusedAtTheFloorAndSpendsTheAttempt()
        {
            var (service, board, _) = Wheel();
            var c = service.IssueChallengeAsync(Player, false).Result.Challenge!;
            _nowMs += WorldBossStrikeRules.CountdownMs + WorldBossStrikeRules.MaxPlayMs;

            var answer = service.StrikeAsync(Player, new StrikeRequest
            {
                ChallengeId = c.ChallengeId,
                Taps = Enumerable.Range(0, 6).Select(i => new WheelTap(i, 1000 + i * 500)).ToList(),
            }).Result!;

            Assert.Equal(WorldBossStrikeResult.Refused, answer.Result);
            Assert.Equal(1.0, answer.Played);
            Assert.Equal(-1, answer.BrokePlate);
            Assert.Equal(1, board.Used);
        }

        [Fact]
        public void AStrikeOnAnUnknownChallengeIsNoChallenge()
        {
            var (service, board, _) = Wheel();
            service.IssueChallengeAsync(Player, false).Wait();
            Assert.Equal(WorldBossStrikeResult.NoChallenge,
                service.StrikeAsync(Player, new StrikeRequest { ChallengeId = new string('f', 32) }).Result!.Result);
            Assert.Equal(WorldBossStrikeResult.NoChallenge,
                service.StrikeAsync(Player + 1, new StrikeRequest { ChallengeId = "anything" }).Result!.Result);
            Assert.Empty(board.Orders);
        }

        [Fact]
        public void AStrikeTheTickDoesNotAnswerInTimeIsQueued()
        {
            var (service, board, _) = Wheel();
            board.Answer = false;
            var answer = service.StrikeAsync(Player, new StrikeRequest { Mode = "Auto", Plate = 1 }).Result!;
            Assert.Equal(WorldBossStrikeResult.Queued, answer.Result);
            Assert.Single(board.Orders);
        }

        // --- abandoned challenges (spec 5.6) ---------------------------------------

        [Fact]
        public void AnAbandonedChallengeResolvesAtTheFloorFromItsAnsweredThrows()
        {
            var (service, board, _) = Wheel();
            var c = service.IssueChallengeAsync(Player, false).Result.Challenge!;
            double t1 = MovingTap(c, 200);
            double t2 = MovingTap(c, t1 + 400);
            _nowMs += WorldBossStrikeRules.CountdownMs + (long)t2 + 100;
            service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 0, TapMs = t1 });
            service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = 1, TapMs = t2 });

            // The player walks away; somebody else's request sweeps it.
            _nowMs += WorldBossStrikeRules.MaxPlayMs + WorldBossChallengeRegistry.ExpiryGraceMs;
            service.GetChallengeAsync(Player + 99).Wait();

            var order = Assert.Single(board.Orders);
            Assert.Equal(WorldBossStrikeResult.ResolvedAtFloor, order.LandedAs);
            Assert.Equal(WorldBossStrikeRules.Floor, order.Multiplier);
            Assert.Equal(2, order.Landings.Count);
            Assert.Equal(1, board.Used);

            // The owner is told once, on their next look.
            var seen = service.GetChallengeAsync(Player).Result;
            Assert.Equal(WorldBossStrikeResult.ResolvedAtFloor, seen.Result);
            Assert.NotNull(seen.Resolved);
            Assert.True(seen.Resolved!.Damage > 0);
            Assert.Null(service.GetChallengeAsync(Player).Result.Resolved);
        }

        [Fact]
        public void AnAbandonedChallengeWithNoThrowsIsAPlainBlowThatBreaksNothing()
        {
            var (service, board, _) = Wheel();
            service.IssueChallengeAsync(Player, false).Wait();
            _nowMs += WorldBossStrikeRules.CountdownMs + WorldBossStrikeRules.MaxPlayMs + WorldBossChallengeRegistry.ExpiryGraceMs;
            var seen = service.GetChallengeAsync(Player).Result;
            Assert.Equal(1.0, seen.Resolved!.Played);
            Assert.Equal(-1, seen.Resolved.BrokePlate);
            Assert.Equal(0, board.BrokenPlateMask);
        }

        // --- the secret -----------------------------------------------------------

        [Fact]
        public void TheWeakPlateIsNeverOnTheWireExceptAsTheThrowersOwnWeakHit()
        {
            var (service, _, registry) = Wheel(seed: 11);
            var responses = new List<object>();

            var issued = service.IssueChallengeAsync(Player, false).Result;
            responses.Add(issued);
            var c = issued.Challenge!;
            responses.Add(service.GetChallengeAsync(Player).Result);

            double t = 200;
            _nowMs += WorldBossStrikeRules.CountdownMs + WorldBossStrikeRules.MaxPlayMs;
            var taps = new List<WheelTap>();
            for (int seq = 0; seq < 3; seq++)
            {
                t = MovingTap(c, t + 400);
                taps.Add(new WheelTap(seq, t));
                responses.Add(service.Throw(Player, new ThrowRequest { ChallengeId = c.ChallengeId, Seq = seq, TapMs = t, Parries = AllRead(c) }));
            }
            responses.Add(service.StrikeAsync(Player, new StrikeRequest { ChallengeId = c.ChallengeId, Taps = taps, Parries = AllRead(c) }).Result!);

            var options = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            foreach (var response in responses)
            {
                string json = JsonSerializer.Serialize(response, response.GetType(), options);
                // No field that names a weak plate - only the per-spear boolean.
                Assert.DoesNotContain("\"WeakPlate\"", json);
                Assert.DoesNotContain("Decoy", json);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Challenge", out var challenge) && challenge.ValueKind == JsonValueKind.Object)
                {
                    Assert.Equal(255, challenge.GetProperty("RevealedWeakPlate").GetInt32());
                }
            }
        }
    }
}
