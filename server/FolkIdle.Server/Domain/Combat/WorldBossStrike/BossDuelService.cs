using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>What the tick answers a duel quote with: the base hit and the build's stance.</summary>
    public sealed record DuelQuote(long Attack, DuelStance Stance);

    /// <summary>A duel waiting for the tick to read the striker's payload.</summary>
    public sealed class DuelQuoteRequest
    {
        public required long PlayerId { get; init; }
        public TaskCompletionSource<DuelQuote?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // --- the REST contract. Hand-written DTOs, mirrored in client_web/src/lib/net/rest.ts.

    public sealed class DuelOmenDto
    {
        public string Name { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public DuelMove Move { get; init; }
        public static DuelOmenDto From(DuelOmen omen) => new() { Name = omen.Name, Text = omen.Text, Move = omen.Move };
    }

    public sealed class DuelCallDto
    {
        public int Index { get; init; }
        public DuelMove Move { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Tell { get; init; } = string.Empty;
        /// <summary>Null until answered.</summary>
        public DuelOption? Answer { get; init; }
        public DuelVerdict? Verdict { get; init; }
        /// <summary>The extra hit the answer landed (0 for a wrong one); null until answered.</summary>
        public long? Hit { get; init; }
        public string? Explain { get; init; }
    }

    public sealed class DuelChallengeDto
    {
        public string ChallengeId { get; init; } = string.Empty;
        public bool Practice { get; init; }
        public bool Enraged { get; init; }
        public DuelStance Stance { get; init; }
        public DuelOmenDto Omen { get; init; } = new();
        /// <summary>The base hit B: the fight before any call adds up to exactly this.</summary>
        public long BaseHit { get; init; }
        /// <summary>Rounds of blows, struck before call 1, between the calls, and after call 3.</summary>
        public IReadOnlyList<long[]> Rounds { get; init; } = Array.Empty<long[]>();
        public IReadOnlyList<DuelCallDto> Calls { get; init; } = Array.Empty<DuelCallDto>();
        public long FinisherHit { get; init; }
        public int CallWindowMs { get; init; } = BossDuelRules.CallWindowMs;
    }

    public sealed class DuelStrikeDto
    {
        public WorldBossStrikeResult Result { get; init; }
        public long Damage { get; init; }
        public double Multiplier { get; init; } = BossDuelRules.Floor;
        public int RightCalls { get; init; }
        /// <summary>True for practice: no damage, no attempt.</summary>
        public bool NoDamageDealt { get; init; }
    }

    public sealed class DuelStateResponse
    {
        public WorldBossStrikeResult Result { get; init; }
        public string Mode { get; init; } = "off";
        public DuelOmenDto Omen { get; init; } = new();
        public DuelChallengeDto? Challenge { get; init; }
        /// <summary>A duel left open, resolved with the calls it had since the player last looked. Shown once.</summary>
        public DuelStrikeDto? Resolved { get; init; }
    }

    public sealed class DuelCallRequest
    {
        public string ChallengeId { get; set; } = string.Empty;
        public int Index { get; set; }
        public DuelOption Option { get; set; }
    }

    public sealed class DuelCallResponse
    {
        public WorldBossStrikeResult Result { get; init; }
        public DuelCallDto? Call { get; init; }
        /// <summary>Set on the last call: the strike, landed (or practice scored).</summary>
        public DuelStrikeDto? Strike { get; init; }
    }

    internal sealed class DuelChallenge
    {
        public required string ChallengeId { get; init; }
        public required long PlayerId { get; init; }
        public required bool Practice { get; init; }
        public required bool Enraged { get; init; }
        public required DuelStance Stance { get; init; }
        public required DuelOmen Omen { get; init; }
        public required DuelMove[] Moves { get; init; }
        public required long BaseHit { get; init; }
        public required long Attack { get; init; }
        public required long[][] Rounds { get; init; }
        public required long ExpiresAtMs { get; init; }
        public long EncounterEndEpoch { get; init; }
        public DuelOption?[] Answers { get; } = new DuelOption?[BossDuelRules.Calls];

        public DuelVerdict[] Verdicts() => Enumerable.Range(0, BossDuelRules.Calls)
            .Select(i => Answers[i] is DuelOption o ? BossDuelRules.Judge(Moves[i], o, Stance, Omen) : DuelVerdict.Wrong)
            .ToArray();

        public DuelChallengeDto ToDto() => new()
        {
            ChallengeId = ChallengeId,
            Practice = Practice,
            Enraged = Enraged,
            Stance = Stance,
            Omen = DuelOmenDto.From(Omen),
            BaseHit = BaseHit,
            Rounds = Rounds,
            Calls = Enumerable.Range(0, BossDuelRules.Calls).Select(CallDto).ToList(),
            FinisherHit = BossDuelRules.FinisherHit(BaseHit),
        };

        public DuelCallDto CallDto(int i)
        {
            var def = BossDuelRules.Def(Moves[i]);
            DuelVerdict? verdict = Answers[i] is DuelOption o ? BossDuelRules.Judge(Moves[i], o, Stance, Omen) : null;
            return new DuelCallDto
            {
                Index = i,
                Move = def.Move,
                Name = def.Name,
                Tell = def.Tell,
                Answer = Answers[i],
                Verdict = verdict,
                Hit = verdict is DuelVerdict v ? BossDuelRules.CallHit(BaseHit, v) : null,
                Explain = Answers[i] is DuelOption a && verdict is DuelVerdict w ? BossDuelRules.Explain(Moves[i], a, w, Stance, Omen) : null,
            };
        }
    }

    /// <summary>
    /// The duel's REST surface, without HTTP (NetworkBroadcastSystem parses and
    /// serialises). Issue a duel, answer its three calls, and the last answer
    /// lands the strike through the same order the wheel used.
    /// </summary>
    /// <remarks>
    /// Modul: THE CLIENT NEVER SIMULATES AND NEVER SENDS A NUMBER. All four
    /// evaluators named the same risk - a fight replayed on two machines that
    /// drifts apart. So the server deals every blow at issue (they sum to the
    /// base hit exactly), answers each call with the hit it landed, and the
    /// client only animates what it was told. A request carries a challenge
    /// id, a call index 0-2 and one of three answers; nothing else.
    ///
    /// Modul: THE BASE HIT IS QUOTED BY THE TICK AT ISSUE (DuelQuoteRequest),
    /// from the same ServerStrikeDamage a strike is priced from, and the order
    /// carries it (QuotedAttack) - so the blows on screen and the damage
    /// credited cannot disagree if the player changes gear mid-fight.
    /// </remarks>
    public sealed class BossDuelService
    {
        public static readonly TimeSpan QuoteWait = TimeSpan.FromSeconds(5);

        private readonly BossMinigameSettings _settings;
        private readonly Func<long> _nowMs;
        private readonly Func<int, int> _uniform;
        private readonly TimeSpan _strikeWait;
        private IWorldBossStrikeBoard? _board;
        private Func<long, Task<DuelQuote?>>? _quote;

        private readonly object _gate = new();
        private readonly Dictionary<(long, bool), DuelChallenge> _open = new();
        private readonly Dictionary<long, DuelStrikeDto> _resolvedNotes = new();

        public BossDuelService(BossMinigameSettings settings, Func<long>? nowMs = null, Func<int, int>? uniform = null,
            IWorldBossStrikeBoard? board = null, Func<long, Task<DuelQuote?>>? quote = null, TimeSpan? strikeWait = null)
        {
            _settings = settings;
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _uniform = uniform ?? RandomNumberGenerator.GetInt32;
            _board = board;
            _quote = quote;
            _strikeWait = strikeWait ?? WorldBossStrikeService.DefaultStrikeWait;
        }

        /// <summary>Program hands over the engine and the tick's quote queue once both exist.</summary>
        public void Attach(IWorldBossStrikeBoard board, Func<long, Task<DuelQuote?>> quote)
        {
            _board = board;
            _quote = quote;
        }

        private bool Open => _settings.Mode == BossMinigameMode.Duel && _board != null && _quote != null;

        private DuelOmen OmenNow() => BossDuelRules.OmenFor(FolkIdle.Server.Engine.WorldBossCalendar.DayKey(_nowMs() / 1000));

        // --- expiry -------------------------------------------------------------

        private void Sweep(long nowMs)
        {
            List<DuelChallenge> expired;
            lock (_gate)
            {
                expired = _open.Values.Where(c => c.ExpiresAtMs <= nowMs).ToList();
                foreach (var c in expired) _open.Remove((c.PlayerId, c.Practice));
            }
            foreach (var c in expired)
            {
                if (c.Practice || _board == null) continue;
                // An abandoned duel is a commitment, like an abandoned wheel run
                // (spec 5.6): it lands with the calls already answered.
                var order = OrderFor(c, WorldBossStrikeResult.ResolvedAtFloor);
                long owner = c.PlayerId;
                _board.Submit(order);
                _ = order.Completion.Task.ContinueWith(t =>
                {
                    if (t.Result.Result != WorldBossStrikeResult.ResolvedAtFloor) return;
                    lock (_gate) _resolvedNotes[owner] = StrikeDto(t.Result, c);
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }

        private static WorldBossStrikeOrder OrderFor(DuelChallenge c, WorldBossStrikeResult landedAs)
        {
            var verdicts = c.Verdicts();
            return new WorldBossStrikeOrder
            {
                PlayerId = c.PlayerId,
                EncounterEndEpoch = c.EncounterEndEpoch,
                Landings = Array.Empty<SpearLanding>(),
                WeakPlate = null,
                Multiplier = BossDuelRules.Multiplier(verdicts),
                LandedAs = landedAs,
                IsPlayed = true,
                QuotedAttack = c.Attack,
                Skill = BossDuelRules.Skill(verdicts),
            };
        }

        private static DuelStrikeDto StrikeDto(WorldBossStrikeOutcome outcome, DuelChallenge c) => new()
        {
            Result = outcome.Result,
            Damage = outcome.Damage,
            Multiplier = outcome.Multiplier,
            RightCalls = c.Verdicts().Count(v => v == DuelVerdict.Right),
        };

        // --- state ----------------------------------------------------------------

        public DuelStateResponse Get(long playerId)
        {
            long now = _nowMs();
            Sweep(now);
            if (_settings.Mode != BossMinigameMode.Duel)
                return new DuelStateResponse { Result = WorldBossStrikeResult.Disabled, Mode = _settings.ModeName, Omen = DuelOmenDto.From(OmenNow()) };
            DuelChallenge? open;
            DuelStrikeDto? resolved;
            lock (_gate)
            {
                _open.TryGetValue((playerId, false), out open);
                if (open == null) _open.TryGetValue((playerId, true), out open);
                if (_resolvedNotes.Remove(playerId, out var note)) resolved = note; else resolved = null;
            }
            return new DuelStateResponse
            {
                Result = open != null ? WorldBossStrikeResult.Outstanding
                    : resolved != null ? WorldBossStrikeResult.ResolvedAtFloor
                    : WorldBossStrikeResult.NoChallenge,
                Mode = _settings.ModeName,
                Omen = DuelOmenDto.From(OmenNow()),
                Challenge = open?.ToDto(),
                Resolved = resolved,
            };
        }

        // --- issue ------------------------------------------------------------------

        public async Task<DuelStateResponse> IssueAsync(long playerId, bool practice)
        {
            long now = _nowMs();
            Sweep(now);
            var omen = OmenNow();
            if (!Open) return new DuelStateResponse { Result = WorldBossStrikeResult.Disabled, Mode = _settings.ModeName, Omen = DuelOmenDto.From(omen) };

            lock (_gate)
            {
                // Idempotent: an open duel is handed back, so a reopened screen resumes it.
                if (_open.TryGetValue((playerId, practice), out var existing))
                    return new DuelStateResponse { Result = WorldBossStrikeResult.Outstanding, Mode = _settings.ModeName, Omen = DuelOmenDto.From(omen), Challenge = existing.ToDto() };
            }

            var board = _board!;
            if (!practice)
            {
                if (!board.IsEventActive) return Refuse(WorldBossStrikeResult.NotActive, omen);
                long needMs = BossDuelRules.ChallengeLifetimeMs + WorldBossStrikeService.ChallengeEndMarginMs;
                if (board.EventEndEpoch * 1000L - now < needMs) return Refuse(WorldBossStrikeResult.TooLateInWindow, omen);
                if (await board.StrikesUsedTodayAsync(playerId) >= FolkIdle.Server.Engine.WorldBossCalendar.StrikesPerDay)
                    return Refuse(WorldBossStrikeResult.NoAttemptsLeft, omen);
            }

            var quote = await _quote!(playerId);
            if (quote == null) return Refuse(WorldBossStrikeResult.NoGameSession, omen);

            bool enraged = WorldBossStrikeRules.IsEnraged(board.BossCurrentHp, board.BossMaxHp);
            long baseHit = Math.Max(FolkIdle.Server.Engine.WorldBossEngine.MinStrikeDamage, quote.Attack);
            var challenge = new DuelChallenge
            {
                ChallengeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)),
                PlayerId = playerId,
                Practice = practice,
                Enraged = enraged,
                Stance = quote.Stance,
                Omen = omen,
                Moves = BossDuelRules.DrawMoves(enraged, omen, _uniform),
                BaseHit = baseHit,
                Attack = quote.Attack,
                Rounds = BossDuelRules.SplitBlows(baseHit, _uniform),
                ExpiresAtMs = now + BossDuelRules.ChallengeLifetimeMs,
                EncounterEndEpoch = practice ? 0 : board.EventEndEpoch,
            };
            lock (_gate)
            {
                if (_open.TryGetValue((playerId, practice), out var raced))
                    return new DuelStateResponse { Result = WorldBossStrikeResult.Outstanding, Mode = _settings.ModeName, Omen = DuelOmenDto.From(omen), Challenge = raced.ToDto() };
                _open[(playerId, practice)] = challenge;
            }
            return new DuelStateResponse { Result = WorldBossStrikeResult.Issued, Mode = _settings.ModeName, Omen = DuelOmenDto.From(omen), Challenge = challenge.ToDto() };
        }

        private DuelStateResponse Refuse(WorldBossStrikeResult result, DuelOmen omen) =>
            new() { Result = result, Mode = _settings.ModeName, Omen = DuelOmenDto.From(omen) };

        // --- call ---------------------------------------------------------------------

        public async Task<DuelCallResponse> CallAsync(long playerId, DuelCallRequest request)
        {
            long now = _nowMs();
            Sweep(now);
            if (_settings.Mode != BossMinigameMode.Duel) return new DuelCallResponse { Result = WorldBossStrikeResult.Disabled };
            if (request.Index < 0 || request.Index >= BossDuelRules.Calls || !Enum.IsDefined(request.Option))
                return new DuelCallResponse { Result = WorldBossStrikeResult.Refused };

            DuelChallenge? challenge;
            bool last;
            lock (_gate)
            {
                challenge = _open.Values.FirstOrDefault(c => c.PlayerId == playerId
                    && string.Equals(c.ChallengeId, request.ChallengeId, StringComparison.Ordinal));
                if (challenge == null) return new DuelCallResponse { Result = WorldBossStrikeResult.NoChallenge };

                // Idempotent by index: a repeat gets the answer already given.
                if (challenge.Answers[request.Index] != null)
                    return new DuelCallResponse { Result = WorldBossStrikeResult.Outstanding, Call = challenge.CallDto(request.Index) };
                // In order: a call cannot be answered before the one before it.
                if (request.Index > 0 && challenge.Answers[request.Index - 1] == null)
                    return new DuelCallResponse { Result = WorldBossStrikeResult.TooEarly };

                challenge.Answers[request.Index] = request.Option;
                last = request.Index == BossDuelRules.Calls - 1;
                if (last) _open.Remove((challenge.PlayerId, challenge.Practice));
            }

            var call = challenge.CallDto(request.Index);
            if (!last) return new DuelCallResponse { Result = WorldBossStrikeResult.Issued, Call = call };

            var verdicts = challenge.Verdicts();
            if (challenge.Practice)
            {
                double m = BossDuelRules.Multiplier(verdicts);
                return new DuelCallResponse
                {
                    Result = WorldBossStrikeResult.PracticeScored,
                    Call = call,
                    Strike = new DuelStrikeDto
                    {
                        Result = WorldBossStrikeResult.PracticeScored,
                        Damage = 0,
                        Multiplier = m,
                        RightCalls = verdicts.Count(v => v == DuelVerdict.Right),
                        NoDamageDealt = true,
                    },
                };
            }

            var order = OrderFor(challenge, WorldBossStrikeResult.Landed);
            _board!.Submit(order);
            var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(_strikeWait));
            if (finished != order.Completion.Task)
                return new DuelCallResponse { Result = WorldBossStrikeResult.Queued, Call = call, Strike = new DuelStrikeDto { Result = WorldBossStrikeResult.Queued } };
            var outcome = order.Completion.Task.Result;
            return new DuelCallResponse { Result = outcome.Result, Call = call, Strike = StrikeDto(outcome, challenge) };
        }
    }
}
