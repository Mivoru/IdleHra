using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    // --- the REST contract (spec 5.2-5.4). Hand-written DTOs by design; the
    // --- client mirrors them in rest.ts and the result list is pinned by a test.

    public sealed class ChallengeSegmentDto
    {
        public int StartMs { get; init; }
        public int DurationMs { get; init; }
        public double DegPerSec { get; init; }
        public int? Interrupt { get; init; }
    }

    public sealed class ChallengeInterruptDto
    {
        public int Index { get; init; }
        public int TellAtMs { get; init; }
        public ParryTell Tell { get; init; }
        public int ReactionFloorMs { get; init; }
        public int ResponseCloseMs { get; init; }
        public int InterruptMs { get; init; }
    }

    public sealed class ChallengeDto
    {
        public string ChallengeId { get; init; } = string.Empty;
        public bool Practice { get; init; }
        public bool Enraged { get; init; }
        public int CountdownMs { get; init; } = WorldBossStrikeRules.CountdownMs;
        public int MaxPlayMs { get; init; } = WorldBossStrikeRules.MaxPlayMs;
        public int Spears { get; init; } = WorldBossStrikeRules.Spears;
        public int FlightMs { get; init; } = WorldBossStrikeRules.FlightMs;
        public int MinReloadMs { get; init; } = WorldBossStrikeRules.MinReloadMs;
        public int ToleranceMs { get; init; } = WorldBossStrikeRules.ToleranceMs;
        public double PlateDegrees { get; init; } = WorldBossStrikeRules.PlateDegrees;
        public double SeamDegrees { get; init; } = WorldBossStrikeRules.SeamDegrees;
        public double RivetDegrees { get; init; } = WorldBossStrikeRules.RivetDegrees;
        public double StartAngleDeg { get; init; }
        public IReadOnlyList<ChallengeSegmentDto> Segments { get; init; } = Array.Empty<ChallengeSegmentDto>();
        public IReadOnlyList<ChallengeInterruptDto> Interrupts { get; init; } = Array.Empty<ChallengeInterruptDto>();
        public int BrokenPlateMask { get; init; }
        /// <summary>255 while the weak plate is hidden - always, in practice.</summary>
        public int RevealedWeakPlate { get; init; } = 255;
        /// <summary>How long this challenge has been open, so a reopened screen resumes the same clock.</summary>
        public long ElapsedMs { get; init; }

        /// <summary>
        /// The spears this player has already thrown on this challenge, as the
        /// server answered them. A reopened screen restores from these rather
        /// than starting again at Seq 0 - which would reuse Seqs the server has
        /// already answered and replay the old answers against new taps.
        /// </summary>
        public IReadOnlyList<RecordedThrowDto> Throws { get; init; } = Array.Empty<RecordedThrowDto>();

        /// <summary>The parries those throws reported, so a resumed run knows which reads it has made.</summary>
        public IReadOnlyList<ParryEntry> Parries { get; init; } = Array.Empty<ParryEntry>();

        public static ChallengeDto From(WorldBossChallenge challenge, long nowMs) => new()
        {
            Throws = challenge.ThrowsSnapshot().Select(t => new RecordedThrowDto
            {
                Seq = t.Seq, TapMs = t.TapMs, Plate = t.Landing.Plate, Class = t.Landing.Class,
                WeakHit = t.WeakHit, Counter = t.Counter,
            }).ToList(),
            Parries = challenge.ParriesSnapshot(),
            ChallengeId = challenge.ChallengeId,
            Practice = challenge.Practice,
            Enraged = challenge.Schedule.Enraged,
            StartAngleDeg = challenge.Schedule.StartAngleDeg,
            Segments = challenge.Schedule.Segments.Select(s => new ChallengeSegmentDto
            {
                StartMs = s.StartMs, DurationMs = s.DurationMs, DegPerSec = s.DegPerSec, Interrupt = s.Interrupt,
            }).ToList(),
            Interrupts = challenge.Schedule.Interrupts.Select(i => new ChallengeInterruptDto
            {
                Index = i.Index, TellAtMs = i.TellAtMs, Tell = i.Tell, ReactionFloorMs = i.ReactionFloorMs,
                ResponseCloseMs = i.ResponseCloseMs, InterruptMs = i.InterruptMs,
            }).ToList(),
            ElapsedMs = Math.Max(0, nowMs - challenge.IssuedAtMs),
        };
    }

    public sealed class RecordedThrowDto
    {
        public int Seq { get; init; }
        public double TapMs { get; init; }
        public int Plate { get; init; }
        public SpearClass Class { get; init; }
        public bool WeakHit { get; init; }
        public CounterEntry? Counter { get; init; }
    }

    public sealed class ChallengeResponse
    {
        public WorldBossStrikeResult Result { get; init; }
        public string Mode { get; init; } = "off";
        public ChallengeDto? Challenge { get; init; }

        /// <summary>
        /// A strike this player left unfinished, resolved at the floor since
        /// they last looked (spec 5.6). Shown once, then forgotten.
        /// </summary>
        public StrikeResponse? Resolved { get; init; }
    }

    public sealed class ThrowRequest
    {
        public string ChallengeId { get; set; } = string.Empty;
        public int Seq { get; set; }
        public double TapMs { get; set; }
        public CounterEntry? Counter { get; set; }
        /// <summary>
        /// Every parry the player has made so far. The server needs them to
        /// know how many spears a missed read has cost before it answers
        /// OutOfSpears; the finish's consistency rule (Phase 2) pins them to
        /// the submitted log.
        /// </summary>
        public List<ParryEntry>? Parries { get; set; }
    }

    public sealed class ThrowResponse
    {
        public WorldBossStrikeResult Result { get; init; }
        public int Seq { get; init; }
        /// <summary>-1 when the spear was dropped (a wheel tap inside a freeze, or a counter outside its window).</summary>
        public int Plate { get; init; } = -1;
        public SpearClass Class { get; init; }
        public bool WeakHit { get; init; }
    }

    public sealed class StrikeRequest
    {
        public string Mode { get; set; } = "Wheel";
        public string ChallengeId { get; set; } = string.Empty;
        public List<WheelTap>? Taps { get; set; }
        public List<ParryEntry>? Parries { get; set; }
        public List<CounterEntry>? Counters { get; set; }
        public int? Plate { get; set; }
    }

    public sealed class LandingDto
    {
        public int Seq { get; init; }
        public int Plate { get; init; }
        public SpearClass Class { get; init; }
        public bool IsCounter { get; init; }
        public bool WeakHit { get; init; }
    }

    public sealed class PracticeScoreResponse
    {
        public WorldBossStrikeResult Result { get; init; }
        public SubmissionVerdict Verdict { get; init; }
        public IReadOnlyList<LandingDto> Landings { get; init; } = Array.Empty<LandingDto>();
        public int SpearsLost { get; init; }
        public double Score { get; init; }
        public double Multiplier { get; init; }
        public double PlateMultiplier { get; init; }
        public double Played { get; init; }
        /// <summary>Always true: practice deals no damage and spends no attempt.</summary>
        public bool NoDamageDealt { get; init; } = true;
    }

    public sealed class StrikeResponse
    {
        public WorldBossStrikeResult Result { get; init; }
        /// <summary>Hit points taken off the boss. 0 unless the strike landed.</summary>
        public long Damage { get; init; }
        public double Multiplier { get; init; } = WorldBossStrikeRules.Floor;
        public double PlateMultiplier { get; init; } = 1.0;
        public double Played { get; init; } = 1.0;
        /// <summary>The plate this strike broke for everyone, or -1.</summary>
        public int BrokePlate { get; init; } = -1;
        public int SpearsLost { get; init; }
        /// <summary>Each spear, with WeakHit - to the striker only, never broadcast.</summary>
        public IReadOnlyList<LandingDto> Landings { get; init; } = Array.Empty<LandingDto>();

        public static StrikeResponse From(WorldBossStrikeOutcome outcome, int spearsLost) => new()
        {
            Result = outcome.Result,
            Damage = outcome.Damage,
            Multiplier = outcome.Multiplier,
            PlateMultiplier = outcome.PlateMultiplier,
            Played = outcome.Played,
            BrokePlate = outcome.BrokePlate,
            SpearsLost = spearsLost,
            Landings = outcome.Landings ?? Array.Empty<LandingDto>(),
        };
    }

    /// <summary>
    /// The shield wheel's REST surface, without HTTP: NetworkBroadcastSystem's
    /// handlers parse and serialise, this decides. Practice needs the flag at
    /// practice or wheel; a real challenge, a throw on one, and /strike need
    /// wheel (spec 5.1).
    /// </summary>
    /// <remarks>
    /// Modul: THIS CLASS NEVER KNOWS A BOSS-WIDE SECRET, because in wheel mode
    /// there is none any more (spec 3.3.1). Each real challenge draws its own
    /// weak plate at issue, from the plates unbroken at that moment, and holds
    /// it on the challenge; the board it reads through IWorldBossStrikeBoard
    /// exposes the mask and the health, nothing else.
    /// </remarks>
    public sealed class WorldBossStrikeService
    {
        /// <summary>How long /strike waits for the tick and the transaction before answering Queued.</summary>
        public static readonly TimeSpan DefaultStrikeWait = TimeSpan.FromSeconds(5);

        /// <summary>A challenge must be able to finish, with this margin, before the encounter ends (spec 5.2).</summary>
        public const long ChallengeEndMarginMs = 15_000;

        private readonly WorldBossChallengeRegistry _registry;
        private readonly BossMinigameSettings _settings;
        private readonly Func<long> _nowMs;
        private readonly TimeSpan _strikeWait;
        private IWorldBossStrikeBoard? _board;

        public WorldBossStrikeService(WorldBossChallengeRegistry registry, BossMinigameSettings settings,
            Func<long>? nowMs = null, IWorldBossStrikeBoard? board = null, TimeSpan? strikeWait = null)
        {
            _registry = registry;
            _settings = settings;
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _board = board;
            _strikeWait = strikeWait ?? DefaultStrikeWait;
        }

        /// <summary>
        /// The engine is built after the container, so Program hands it over
        /// here. Until it does, every real path answers Disabled.
        /// </summary>
        public void AttachBoard(IWorldBossStrikeBoard board) => _board = board;

        private bool PracticeOpen => _settings.Mode != BossMinigameMode.Off;
        private bool WheelOpen => _settings.Mode == BossMinigameMode.Wheel && _board != null;

        // --- expiry -----------------------------------------------------------

        /// <summary>
        /// Sweeps what is due. An expired REAL challenge is a commitment: it is
        /// resolved at the floor from the throws the server already answered,
        /// and spends the attempt (spec 5.6). Whoever's request swept it, the
        /// owner collects the result on their next look.
        /// </summary>
        private void Sweep(long nowMs)
        {
            foreach (var expired in _registry.ExpireDue(nowMs))
            {
                var board = _board;
                if (board == null) continue;
                var order = FloorOrder(expired, WorldBossStrikeResult.ResolvedAtFloor);
                long owner = expired.PlayerId;
                board.Submit(order);

                // Noted at once if the board already answered, so the owner's
                // own sweeping request sees it; otherwise when the tick does.
                var answered = order.Completion.Task;
                if (answered.IsCompletedSuccessfully) NoteIfLanded(owner, answered.Result);
                else _ = answered.ContinueWith(t => NoteIfLanded(owner, t.Result), TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }

        private void NoteIfLanded(long owner, WorldBossStrikeOutcome outcome)
        {
            if (outcome.Result == WorldBossStrikeResult.ResolvedAtFloor) _registry.NoteResolved(owner, outcome);
        }

        /// <summary>
        /// A strike built from the throws the server answered, at M = Floor:
        /// what an abandoned, expired or refused challenge is worth. With no
        /// answered throws it is A x G x 1.0 and breaks nothing.
        /// </summary>
        private static WorldBossStrikeOrder FloorOrder(WorldBossChallenge challenge, WorldBossStrikeResult landedAs) => new()
        {
            PlayerId = challenge.PlayerId,
            EncounterEndEpoch = challenge.EncounterEndEpoch,
            Landings = challenge.ThrowsSnapshot().Select(t => t.Landing).ToList(),
            WeakPlate = challenge.WeakPlate,
            Multiplier = WorldBossStrikeRules.Floor,
            LandedAs = landedAs,
        };

        // --- eligibility ------------------------------------------------------

        /// <summary>
        /// The early answer for a real strike, from the board's in-memory
        /// mirror and one plain read of today's count. Null when eligible.
        /// </summary>
        /// <remarks>
        /// Modul: AN EARLY ANSWER, NOT THE AUTHORITY - the same position as the
        /// tick's in-memory check on opcode 32. The engine re-checks every one
        /// of these inside its Serializable transaction and answers with the
        /// same results, so a race past this function is refused there, never
        /// double-spent.
        /// </remarks>
        private async Task<WorldBossStrikeResult?> RefusalAsync(long playerId, long nowMs, bool forChallenge)
        {
            var board = _board;
            if (board == null) return WorldBossStrikeResult.Disabled;
            if (!board.IsEventActive) return WorldBossStrikeResult.NotActive;
            if (board.IsBossDead()) return WorldBossStrikeResult.AlreadyDefeated;
            if (forChallenge)
            {
                long needMs = WorldBossStrikeRules.CountdownMs + WorldBossStrikeRules.MaxPlayMs + ChallengeEndMarginMs;
                if (board.EventEndEpoch * 1000L - nowMs < needMs) return WorldBossStrikeResult.TooLateInWindow;
            }
            if (await board.StrikesUsedTodayAsync(playerId) >= FolkIdle.Server.Engine.WorldBossCalendar.StrikesPerDay)
            {
                return WorldBossStrikeResult.NoAttemptsLeft;
            }
            return null;
        }

        // --- challenge --------------------------------------------------------

        public Task<ChallengeResponse> GetChallengeAsync(long playerId)
        {
            long now = _nowMs();
            Sweep(now);
            if (!PracticeOpen) return Task.FromResult(new ChallengeResponse { Result = WorldBossStrikeResult.Disabled, Mode = _settings.ModeName });

            var resolved = TakeResolved(playerId);
            var open = (WheelOpen ? _registry.Get(playerId, practice: false) : null) ?? _registry.Get(playerId, practice: true);
            return Task.FromResult(new ChallengeResponse
            {
                Result = open != null ? WorldBossStrikeResult.Outstanding
                    : resolved != null ? WorldBossStrikeResult.ResolvedAtFloor
                    : WorldBossStrikeResult.NoChallenge,
                Mode = _settings.ModeName,
                Challenge = open == null ? null : ChallengeDto.From(open, now),
                Resolved = resolved,
            });
        }

        private StrikeResponse? TakeResolved(long playerId)
        {
            var note = _registry.TakeResolvedNote(playerId);
            return note == null ? null : StrikeResponse.From(note, 0);
        }

        public async Task<ChallengeResponse> IssueChallengeAsync(long playerId, bool practice)
        {
            long now = _nowMs();
            Sweep(now);
            if (!PracticeOpen) return new ChallengeResponse { Result = WorldBossStrikeResult.Disabled, Mode = _settings.ModeName };

            if (practice)
            {
                var (drill, drillIssued) = _registry.IssueOrGet(playerId, practice: true, enraged: false, now);
                return new ChallengeResponse
                {
                    Result = drillIssued ? WorldBossStrikeResult.Issued : WorldBossStrikeResult.Outstanding,
                    Mode = _settings.ModeName,
                    Challenge = ChallengeDto.From(drill, now),
                };
            }

            if (!WheelOpen) return new ChallengeResponse { Result = WorldBossStrikeResult.Disabled, Mode = _settings.ModeName };
            var resolved = TakeResolved(playerId);

            // Idempotent: an open challenge is handed back before any gate, so a
            // reopened screen resumes its run.
            var outstanding = _registry.Get(playerId, practice: false);
            if (outstanding != null && outstanding.ExpiresAtMs > now)
            {
                return new ChallengeResponse
                {
                    Result = WorldBossStrikeResult.Outstanding, Mode = _settings.ModeName,
                    Challenge = ChallengeDto.From(outstanding, now), Resolved = resolved,
                };
            }

            var refusal = await RefusalAsync(playerId, now, forChallenge: true);
            if (refusal.HasValue) return new ChallengeResponse { Result = refusal.Value, Mode = _settings.ModeName, Resolved = resolved };

            // Modul: A REAL CHALLENGE NEEDS A LIVE CHARACTER (security review,
            // 2026-09-26). Defence in depth: the strike is priced from the
            // tick's payload, and one issued with no session open is what the
            // free re-roll exploited. The authority is still the drain, which
            // prices a session-less strike at the floor and spends it.
            if (!_board!.HasGameSession(playerId))
            {
                return new ChallengeResponse { Result = WorldBossStrikeResult.NoGameSession, Mode = _settings.ModeName, Resolved = resolved };
            }

            var board = _board!;
            bool enraged = WorldBossStrikeRules.IsEnraged(board.BossCurrentHp, board.BossMaxHp);
            var (challenge, issued) = _registry.IssueOrGet(playerId, practice: false, enraged, now, board.BrokenPlateMask, board.EventEndEpoch);
            return new ChallengeResponse
            {
                Result = issued ? WorldBossStrikeResult.Issued : WorldBossStrikeResult.Outstanding,
                Mode = _settings.ModeName,
                Challenge = ChallengeDto.From(challenge, now),
                Resolved = resolved,
            };
        }

        // --- throw ------------------------------------------------------------

        private WorldBossChallenge? Find(long playerId, string challengeId)
        {
            var practice = _registry.Get(playerId, practice: true);
            if (practice != null && string.Equals(practice.ChallengeId, challengeId, StringComparison.Ordinal)) return practice;
            if (!WheelOpen) return null;
            var real = _registry.Get(playerId, practice: false);
            if (real != null && string.Equals(real.ChallengeId, challengeId, StringComparison.Ordinal)) return real;
            return null;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        public ThrowResponse Throw(long playerId, ThrowRequest request)
        {
            long now = _nowMs();
            Sweep(now);
            if (!PracticeOpen) return new ThrowResponse { Result = WorldBossStrikeResult.Disabled, Seq = request.Seq };

            var challenge = Find(playerId, request.ChallengeId ?? string.Empty);
            if (challenge == null) return new ThrowResponse { Result = WorldBossStrikeResult.NoChallenge, Seq = request.Seq };

            // Idempotent by Seq: a repeat gets the answer already given.
            if (_registry.TryGetThrow(challenge, request.Seq, out var already) && already != null) return Answer(already);

            var schedule = challenge.Schedule;
            var parries = request.Parries ?? new List<ParryEntry>();
            var interrupts = schedule.Interrupts.ToDictionary(i => i.Index);

            bool shapeOk = request.Seq >= 0 && request.Seq < WorldBossStrikeRules.Spears
                && Finite(request.TapMs) && request.TapMs >= 0 && request.TapMs <= WorldBossStrikeRules.MaxPlayMs
                && parries.All(p => p != null && interrupts.ContainsKey(p.Interrupt) && Enum.IsDefined(p.Choice) && Finite(p.ChoiceMs))
                && parries.Select(p => p.Interrupt).Distinct().Count() == parries.Count
                && (request.Counter == null
                    || (interrupts.ContainsKey(request.Counter.Interrupt)
                        && request.Counter.Plate >= 0 && request.Counter.Plate < WorldBossStrikeRules.PlateCount));
            if (!shapeOk)
            {
                WorldBossStrikeTelemetry.Record(playerId, StrikeSuspicion.ShapeRefusal);
                return new ThrowResponse { Result = WorldBossStrikeResult.Refused, Seq = request.Seq };
            }

            // The wall clock: a spear cannot arrive before it could have been
            // thrown. An honest client always passes - the network only adds time.
            if (now - challenge.IssuedAtMs < WorldBossStrikeRules.CountdownMs + request.TapMs - ShieldWheelScorer.WallClockSlackMs)
            {
                WorldBossStrikeTelemetry.Record(playerId, StrikeSuspicion.WallClock);
                return new ThrowResponse { Result = WorldBossStrikeResult.TooEarly, Seq = request.Seq };
            }

            // Spears a missed read has cost by the time of this throw.
            int lost = 0;
            var reads = new Dictionary<int, ShieldWheelInterrupt>();
            foreach (var interrupt in schedule.Interrupts)
            {
                var parry = parries.FirstOrDefault(p => p.Interrupt == interrupt.Index);
                if (ShieldWheelScorer.IsRead(interrupt, parry)) reads[interrupt.Index] = interrupt;
                else if (interrupt.TellAtMs <= request.TapMs) lost++;
            }
            if (request.Seq > WorldBossStrikeRules.Spears - 1 - lost)
            {
                return new ThrowResponse { Result = WorldBossStrikeResult.OutOfSpears, Seq = request.Seq };
            }

            SpearLanding landing;
            if (request.Counter is { } counter)
            {
                // A counter with no parry for its interrupt is no read at all:
                // dropped, like one outside its window - never a 500.
                var parry = parries.FirstOrDefault(p => p.Interrupt == counter.Interrupt);
                bool inWindow = parry != null
                    && reads.TryGetValue(counter.Interrupt, out var read)
                    && request.TapMs >= parry.ChoiceMs && request.TapMs <= read.EndMs;
                landing = inWindow
                    ? new SpearLanding(request.Seq, counter.Plate, SpearClass.Seam, true)
                    : new SpearLanding(request.Seq, -1, SpearClass.None, true);
            }
            else if (schedule.FrozenAt(request.TapMs))
            {
                landing = new SpearLanding(request.Seq, -1, SpearClass.None, false);
            }
            else
            {
                landing = ShieldWheelScorer.LandWheelTap(schedule, request.Seq, request.TapMs);
            }

            // The challenge's OWN weak plate: a decoy in practice, this
            // attempt's draw for a real strike. Told to the thrower only.
            bool weakHit = landing.Class >= SpearClass.Plate && landing.Plate == challenge.WeakPlate;
            var recorded = _registry.RecordThrow(challenge, new RecordedThrow(request.Seq, request.TapMs, request.Counter, landing, weakHit));
            _registry.RecordParries(challenge, parries);
            return Answer(recorded);
        }

        private static ThrowResponse Answer(RecordedThrow recorded) => new()
        {
            Result = WorldBossStrikeResult.Landed,
            Seq = recorded.Seq,
            Plate = recorded.Landing.Plate,
            Class = recorded.Landing.Class,
            WeakHit = recorded.WeakHit,
        };

        // --- practice ---------------------------------------------------------

        private static StrikeLog LogOf(StrikeRequest request) => new(
            (IReadOnlyList<WheelTap>?)request.Taps?.Where(t => t != null).ToList() ?? Array.Empty<WheelTap>(),
            (IReadOnlyList<ParryEntry>?)request.Parries?.Where(p => p != null).ToList() ?? Array.Empty<ParryEntry>(),
            (IReadOnlyList<CounterEntry>?)request.Counters?.Where(c => c != null).ToList() ?? Array.Empty<CounterEntry>());

        public PracticeScoreResponse ScorePractice(long playerId, StrikeRequest request)
        {
            long now = _nowMs();
            Sweep(now);
            if (!PracticeOpen) return new PracticeScoreResponse { Result = WorldBossStrikeResult.Disabled };

            var challenge = _registry.TryTake(playerId, practice: true, request.ChallengeId ?? string.Empty);
            if (challenge == null) return new PracticeScoreResponse { Result = WorldBossStrikeResult.NoChallenge };

            var scored = ShieldWheelScorer.Score(challenge.Schedule, LogOf(request), now - challenge.IssuedAtMs);

            if (scored.Verdict == SubmissionVerdict.Refused) WorldBossStrikeTelemetry.Record(playerId, scored.RefusalDetail);
            else foreach (var suspicion in scored.Suspicions) WorldBossStrikeTelemetry.Record(playerId, suspicion);

            int weak = challenge.WeakPlate;
            var landings = scored.Landings;
            return new PracticeScoreResponse
            {
                Result = WorldBossStrikeResult.PracticeScored,
                Verdict = scored.Verdict,
                Landings = landings.Select(l => new LandingDto
                {
                    Seq = l.Seq, Plate = l.Plate, Class = l.Class, IsCounter = l.IsCounter,
                    WeakHit = l.Class >= SpearClass.Plate && l.Plate == weak,
                }).ToList(),
                SpearsLost = scored.SpearsLost,
                Score = scored.Score,
                Multiplier = scored.Multiplier,
                PlateMultiplier = WorldBossStrikeRules.PlateMultiplier(landings, weak),
                Played = scored.Verdict == SubmissionVerdict.Accepted
                    ? WorldBossStrikeRules.Played(landings, weak)
                    : Math.Max(WorldBossStrikeRules.PlateMultiplier(landings, weak), WorldBossStrikeRules.AutoFloor(landings, weak)),
            };
        }

        // --- strike -----------------------------------------------------------

        /// <summary>
        /// Every throw the server answered must appear in the finished log
        /// exactly as it was thrown - same Seq, same time, same counter - and
        /// every parry it reported must be the log's parry for that interrupt
        /// (spec 5.4). Taps the server never saw are accepted from the log.
        /// </summary>
        internal static bool MatchesAnsweredThrows(WorldBossChallenge challenge, StrikeLog log)
        {
            foreach (var answered in challenge.ThrowsSnapshot())
            {
                bool present = answered.Counter == null
                    ? log.Taps.Any(t => t.Seq == answered.Seq && t.TapMs.Equals(answered.TapMs))
                        && !log.Counters.Any(c => c.Seq == answered.Seq)
                    : log.Counters.Any(c => c.Equals(answered.Counter))
                        && !log.Taps.Any(t => t.Seq == answered.Seq);
                if (!present) return false;
            }
            foreach (var parry in challenge.ParriesSnapshot())
            {
                if (!log.Parries.Any(p => p.Equals(parry))) return false;
            }
            return true;
        }

        /// <summary>
        /// The real strike (spec 5.4). Null means the request itself is
        /// malformed (an auto-strike with no plate 0-4): the handler answers 400.
        /// </summary>
        public async Task<StrikeResponse?> StrikeAsync(long playerId, StrikeRequest request)
        {
            long now = _nowMs();
            Sweep(now);
            if (!WheelOpen) return new StrikeResponse { Result = WorldBossStrikeResult.Disabled };

            WorldBossStrikeOrder order;
            if (string.Equals(request.Mode, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Plate is not int plate || plate < 0 || plate >= WorldBossStrikeRules.PlateCount) return null;

                var open = _registry.Get(playerId, practice: false);
                if (open != null && open.ExpiresAtMs > now) return new StrikeResponse { Result = WorldBossStrikeResult.ChallengeOutstanding };

                var refusal = await RefusalAsync(playerId, now, forChallenge: false);
                if (refusal.HasValue) return new StrikeResponse { Result = refusal.Value };

                // Auto-strike is today's strike exactly: M = Floor, one Plate
                // hit on the chosen plate, its weak plate drawn under the lock.
                order = new WorldBossStrikeOrder
                {
                    PlayerId = playerId,
                    Landings = new[] { new SpearLanding(0, plate, SpearClass.Plate, false) },
                    WeakPlate = null,
                    Multiplier = WorldBossStrikeRules.Floor,
                    LandedAs = WorldBossStrikeResult.Landed,
                };
            }
            else
            {
                var challenge = _registry.TryTake(playerId, practice: false, request.ChallengeId ?? string.Empty);
                if (challenge == null) return new StrikeResponse { Result = WorldBossStrikeResult.NoChallenge };

                var log = LogOf(request);
                var scored = ShieldWheelScorer.Score(challenge.Schedule, log, now - challenge.IssuedAtMs);
                if (!MatchesAnsweredThrows(challenge, log))
                {
                    WorldBossStrikeTelemetry.Record(playerId, StrikeSuspicion.ThrowFinishMismatch);
                    order = FloorOrder(challenge, WorldBossStrikeResult.Refused);
                }
                else if (scored.Verdict == SubmissionVerdict.Refused)
                {
                    WorldBossStrikeTelemetry.Record(playerId, scored.RefusalDetail);
                    order = FloorOrder(challenge, WorldBossStrikeResult.Refused);
                }
                else
                {
                    foreach (var suspicion in scored.Suspicions) WorldBossStrikeTelemetry.Record(playerId, suspicion);
                    order = new WorldBossStrikeOrder
                    {
                        PlayerId = playerId,
                        EncounterEndEpoch = challenge.EncounterEndEpoch,
                        Landings = scored.Landings,
                        WeakPlate = challenge.WeakPlate,
                        Multiplier = scored.Multiplier,
                        LandedAs = WorldBossStrikeResult.Landed,
                        SpearsLost = scored.SpearsLost,
                    };
                }
            }

            _board!.Submit(order);
            var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(_strikeWait));
            if (finished != order.Completion.Task) return new StrikeResponse { Result = WorldBossStrikeResult.Queued };
            return StrikeResponse.From(order.Completion.Task.Result, order.SpearsLost);
        }
    }
}
