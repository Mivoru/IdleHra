using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The duel that replaced the shield wheel (2026-11-14,
    /// docs/design/WORLD_BOSS_REWORK_2026_11_14.md): the rules' economy and the
    /// REST service's contract.
    /// </summary>
    public class BossDuelTests
    {
        private readonly ITestOutputHelper _out;
        public BossDuelTests(ITestOutputHelper output) => _out = output;

        private static readonly DuelOption[] Answers = { DuelOption.Guard, DuelOption.Strike, DuelOption.Evade };

        // --- the economy (measured, the CLAUDE.md rule for balance numbers) -----

        /// <summary>
        /// Modul: the duel keeps the wheel's economy. Every call right = Cap 2.0,
        /// every call missed = Floor 1.0, and a player answering at random lands
        /// near the wheel's measured random-tap 1.35 (spec 3.1) - so guessing
        /// pays a little, reading pays most of the lever.
        /// </summary>
        [Fact]
        public void Ledger_RandomAnswersLandNearTheWheelsRandomTap_ReadingReachesTheCap()
        {
            var rng = new Random(20261114);
            foreach (var stance in Enum.GetValues<DuelStance>())
            {
                double randomSum = 0, bestSum = 0;
                const int runs = 20_000;
                for (int run = 0; run < runs; run++)
                {
                    var omen = BossDuelRules.Omens[rng.Next(7)];
                    bool enraged = rng.Next(4) == 0;
                    var moves = BossDuelRules.DrawMoves(enraged, omen, rng.Next);
                    var random = moves.Select(m => BossDuelRules.Judge(m, Answers[rng.Next(3)], stance, omen)).ToArray();
                    var best = moves.Select(m => Answers.Max(a => BossDuelRules.Judge(m, a, stance, omen))).ToArray();
                    randomSum += BossDuelRules.Multiplier(random);
                    bestSum += BossDuelRules.Multiplier(best);
                }
                double randomM = randomSum / runs, bestM = bestSum / runs;
                _out.WriteLine($"{stance,-10} random M {randomM:F3}   best M {bestM:F3}");
                Assert.InRange(randomM, 1.30, 1.50);
                Assert.Equal(2.0, bestM, 6);
            }
        }

        [Fact]
        public void EveryMove_HasExactlyOneRightAnswerForEveryone_BeforeStanceAndOmen()
        {
            var noOmen = new DuelOmen(-1, "", "", (DuelMove)0, DuelOption.None);
            foreach (var def in BossDuelRules.Moves)
            {
                int right = Answers.Count(a => BossDuelRules.Judge(def.Move, a, DuelStance.Balanced, noOmen) == DuelVerdict.Right);
                Assert.Equal(1, right);
            }
        }

        [Fact]
        public void Stance_ChangesWhichAnswerIsRight()
        {
            var noOmen = new DuelOmen(-1, "", "", (DuelMove)0, DuelOption.None);
            Assert.Equal(DuelVerdict.Fair, BossDuelRules.Judge(DuelMove.CrushingBlow, DuelOption.Guard, DuelStance.Balanced, noOmen));
            Assert.Equal(DuelVerdict.Right, BossDuelRules.Judge(DuelMove.CrushingBlow, DuelOption.Guard, DuelStance.Guard, noOmen));
            Assert.Equal(DuelVerdict.Right, BossDuelRules.Judge(DuelMove.BlackFlame, DuelOption.Evade, DuelStance.Precision, noOmen));
            Assert.Equal(DuelVerdict.Right, BossDuelRules.Judge(DuelMove.CurseOfNight, DuelOption.Strike, DuelStance.Might, noOmen));
        }

        [Fact]
        public void Omen_MakesItsAnswerRight_AndDemotesTheOldOne()
        {
            var thunder = BossDuelRules.Omens[0]; // Crushing Blow: Strike
            Assert.Equal(DuelVerdict.Right, BossDuelRules.Judge(DuelMove.CrushingBlow, DuelOption.Strike, DuelStance.Balanced, thunder));
            Assert.Equal(DuelVerdict.Fair, BossDuelRules.Judge(DuelMove.CrushingBlow, DuelOption.Evade, DuelStance.Balanced, thunder));
            // A stance that owns another answer keeps it.
            Assert.Equal(DuelVerdict.Right, BossDuelRules.Judge(DuelMove.CrushingBlow, DuelOption.Guard, DuelStance.Guard, thunder));
        }

        [Fact]
        public void TimedOutCall_IsWrong() =>
            Assert.Equal(DuelVerdict.Wrong, BossDuelRules.Judge(DuelMove.CrushingBlow, DuelOption.None, DuelStance.Guard, BossDuelRules.Omens[0]));

        [Fact]
        public void OmensMoveAlwaysComes_WhenTheFormCanMakeIt()
        {
            var rng = new Random(7);
            foreach (var omen in BossDuelRules.Omens)
            {
                foreach (bool enraged in new[] { false, true })
                {
                    var pool = enraged ? BossDuelRules.SerpentPool : BossDuelRules.NormalPool;
                    for (int i = 0; i < 200; i++)
                    {
                        var moves = BossDuelRules.DrawMoves(enraged, omen, rng.Next);
                        Assert.Equal(BossDuelRules.Calls, moves.Distinct().Count());
                        Assert.All(moves, m => Assert.Contains(m, pool));
                        if (pool.Contains(omen.Move)) Assert.Contains(omen.Move, moves);
                    }
                }
            }
        }

        [Fact]
        public void OmenOfTheDay_FollowsTheWeek()
        {
            Assert.Equal("Perun's Thunder", BossDuelRules.OmenFor(new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc)).Name); // Monday
            Assert.Equal("Simargl's Wing", BossDuelRules.OmenFor(new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc)).Name);  // Sunday
        }

        [Theory]
        [InlineData(1_000L)]
        [InlineData(12_345L)]
        [InlineData(987_654_321L)]
        public void Blows_SumToTheBaseHitExactly(long baseHit)
        {
            var rounds = BossDuelRules.SplitBlows(baseHit, new Random(3).Next);
            Assert.Equal(BossDuelRules.Rounds, rounds.Length);
            Assert.Equal(baseHit, rounds.Sum(r => r.Sum()));
            Assert.All(rounds.SelectMany(r => r), b => Assert.True(b > 0));
        }

        [Fact]
        public void FightOnScreen_AddsUpToTheCreditedMultiplier()
        {
            const long b = 10_000;
            var verdicts = new[] { DuelVerdict.Right, DuelVerdict.Right, DuelVerdict.Right };
            long shown = b + verdicts.Sum(v => BossDuelRules.CallHit(b, v)) + BossDuelRules.FinisherHit(b);
            Assert.Equal((long)(b * BossDuelRules.Multiplier(verdicts)), shown);
        }

        // --- the service --------------------------------------------------------

        private sealed class FakeBoard : IWorldBossStrikeBoard
        {
            public bool IsEventActive { get; set; } = true;
            public bool IsBossDead() => false;
            public long EventEndEpoch { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86_400;
            public long BossCurrentHp { get; set; } = 1_000_000;
            public long BossMaxHp { get; set; } = 1_000_000;
            public byte BrokenPlateMask => 0;
            public int Used { get; set; }
            public readonly ConcurrentQueue<WorldBossStrikeOrder> Orders = new();
            public Task<int> StrikesUsedTodayAsync(long playerId) => Task.FromResult(Used);
            public bool HasGameSession(long playerId) => true;
            public void Submit(WorldBossStrikeOrder order)
            {
                Orders.Enqueue(order);
                Used++;
                long a = Math.Max(WorldBossEngine.MinStrikeDamage, order.QuotedAttack ?? 0);
                order.Complete(new WorldBossStrikeOutcome(order.LandedAs, (long)(a * order.Multiplier), order.Multiplier));
            }
        }

        private static (BossDuelService service, FakeBoard board) Make(DuelStance stance = DuelStance.Balanced, long attack = 50_000, bool session = true)
        {
            var board = new FakeBoard();
            var service = new BossDuelService(new BossMinigameSettings { Mode = BossMinigameMode.Duel },
                board: board, quote: _ => Task.FromResult(session ? new DuelQuote(attack, stance) : null));
            return (service, board);
        }

        private static DuelOption BestAnswer(DuelCallDto call, DuelStance stance, string omenName)
        {
            var omen = BossDuelRules.Omens.First(o => o.Name == omenName);
            return Answers.OrderByDescending(a => BossDuelRules.Judge(call.Move, a, stance, omen)).First();
        }

        [Fact]
        public async Task ThreeRightCalls_LandTheStrikeAtTheCap_WithTheQuotedAttack()
        {
            var (service, board) = Make(DuelStance.Precision);
            var issued = await service.IssueAsync(1, practice: false);
            Assert.Equal(WorldBossStrikeResult.Issued, issued.Result);
            var duel = issued.Challenge!;
            Assert.Equal(50_000, duel.Rounds.Sum(r => r.Sum()));

            DuelCallResponse last = null!;
            foreach (var call in duel.Calls)
            {
                last = await service.CallAsync(1, new DuelCallRequest { ChallengeId = duel.ChallengeId, Index = call.Index, Option = BestAnswer(call, duel.Stance, duel.Omen.Name) });
                Assert.Equal(DuelVerdict.Right, last.Call!.Verdict);
            }

            Assert.Equal(WorldBossStrikeResult.Landed, last.Result);
            Assert.Equal(100_000, last.Strike!.Damage);
            Assert.True(board.Orders.TryDequeue(out var order));
            Assert.Equal(50_000, order.QuotedAttack);
            Assert.Equal(2.0, order.Multiplier, 6);
            Assert.Equal(1.0, order.Skill);
            Assert.Empty(order.Landings);
        }

        [Fact]
        public async Task Practice_DealsNothing_AndSpendsNothing()
        {
            var (service, board) = Make();
            var duel = (await service.IssueAsync(1, practice: true)).Challenge!;
            DuelCallResponse last = null!;
            foreach (var call in duel.Calls)
                last = await service.CallAsync(1, new DuelCallRequest { ChallengeId = duel.ChallengeId, Index = call.Index, Option = DuelOption.Guard });
            Assert.Equal(WorldBossStrikeResult.PracticeScored, last.Result);
            Assert.True(last.Strike!.NoDamageDealt);
            Assert.Empty(board.Orders);
        }

        [Fact]
        public async Task Calls_AreInOrder_AndIdempotent()
        {
            var (service, _) = Make();
            var duel = (await service.IssueAsync(1, practice: false)).Challenge!;
            var early = await service.CallAsync(1, new DuelCallRequest { ChallengeId = duel.ChallengeId, Index = 1, Option = DuelOption.Guard });
            Assert.Equal(WorldBossStrikeResult.TooEarly, early.Result);

            var first = await service.CallAsync(1, new DuelCallRequest { ChallengeId = duel.ChallengeId, Index = 0, Option = DuelOption.Guard });
            var again = await service.CallAsync(1, new DuelCallRequest { ChallengeId = duel.ChallengeId, Index = 0, Option = DuelOption.Strike });
            Assert.Equal(first.Call!.Verdict, again.Call!.Verdict);
            Assert.Equal(DuelOption.Guard, again.Call.Answer);
        }

        [Fact]
        public async Task SomeoneElsesChallenge_IsNotFound()
        {
            var (service, _) = Make();
            var duel = (await service.IssueAsync(1, practice: false)).Challenge!;
            var stolen = await service.CallAsync(2, new DuelCallRequest { ChallengeId = duel.ChallengeId, Index = 0, Option = DuelOption.Guard });
            Assert.Equal(WorldBossStrikeResult.NoChallenge, stolen.Result);
        }

        [Fact]
        public async Task Refusals_SpentToday_NoSession_WheelMode()
        {
            var (service, board) = Make();
            board.Used = 1;
            Assert.Equal(WorldBossStrikeResult.NoAttemptsLeft, (await service.IssueAsync(1, practice: false)).Result);

            var (noSession, _) = Make(session: false);
            Assert.Equal(WorldBossStrikeResult.NoGameSession, (await noSession.IssueAsync(1, practice: false)).Result);

            var wheel = new BossDuelService(new BossMinigameSettings { Mode = BossMinigameMode.Wheel }, board: new FakeBoard(), quote: _ => Task.FromResult<DuelQuote?>(new DuelQuote(1, DuelStance.Balanced)));
            Assert.Equal(WorldBossStrikeResult.Disabled, (await wheel.IssueAsync(1, practice: false)).Result);
        }

        [Fact]
        public async Task AbandonedDuel_LandsWithTheCallsItHad()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var board = new FakeBoard();
            var service = new BossDuelService(new BossMinigameSettings { Mode = BossMinigameMode.Duel }, nowMs: () => now,
                board: board, quote: _ => Task.FromResult<DuelQuote?>(new DuelQuote(20_000, DuelStance.Balanced)));
            var duel = (await service.IssueAsync(1, practice: false)).Challenge!;
            await service.CallAsync(1, new DuelCallRequest { ChallengeId = duel.ChallengeId, Index = 0, Option = BestAnswer(duel.Calls[0], duel.Stance, duel.Omen.Name) });

            now += BossDuelRules.ChallengeLifetimeMs + 1;
            var state = service.Get(1);
            Assert.True(board.Orders.TryDequeue(out var order));
            Assert.Equal(WorldBossStrikeResult.ResolvedAtFloor, order.LandedAs);
            Assert.Equal(1.0 + BossDuelRules.PerRightCall, order.Multiplier, 6);
            await Task.Delay(50);
            var later = service.Get(1);
            Assert.True(state.Resolved != null || later.Resolved != null);
        }

        [Fact]
        public void Stance_IsTheDeepestCombatColumn()
        {
            var ranks = new byte[SkillRankBuffer.Length];
            Assert.Equal(DuelStance.Balanced, BossDuelRules.StanceOf(ranks));
            foreach (var n in WarTreeRegistry.All().Where(n => n.Column == WarTreeRegistry.ColumnGuard && n.Row == 0)) ranks[n.Id] = (byte)n.MaxLevel;
            Assert.Equal(DuelStance.Guard, BossDuelRules.StanceOf(ranks));
        }
    }
}
