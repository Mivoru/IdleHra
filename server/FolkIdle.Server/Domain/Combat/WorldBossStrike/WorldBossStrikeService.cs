using System;
using System.Collections.Generic;
using System.Linq;

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

        public static ChallengeDto From(WorldBossChallenge challenge, long nowMs) => new()
        {
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

    public sealed class ChallengeResponse
    {
        public WorldBossStrikeResult Result { get; init; }
        public string Mode { get; init; } = "off";
        public ChallengeDto? Challenge { get; init; }
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
    }

    /// <summary>
    /// The shield wheel's REST surface, without HTTP: NetworkBroadcastSystem's
    /// handlers parse and serialise, this decides. Phase 1 serves PRACTICE
    /// only - a real challenge and /strike answer Disabled whatever the flag,
    /// until Phase 2 turns scoring into damage behind its security review.
    /// </summary>
    public sealed class WorldBossStrikeService
    {
        private readonly WorldBossChallengeRegistry _registry;
        private readonly BossMinigameSettings _settings;
        private readonly Func<long> _nowMs;

        public WorldBossStrikeService(WorldBossChallengeRegistry registry, BossMinigameSettings settings, Func<long>? nowMs = null)
        {
            _registry = registry;
            _settings = settings;
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        private bool PracticeOpen => _settings.Mode != BossMinigameMode.Off;

        public ChallengeResponse GetChallenge(long playerId)
        {
            long now = _nowMs();
            _registry.ExpireDue(now);
            if (!PracticeOpen) return new ChallengeResponse { Result = WorldBossStrikeResult.Disabled, Mode = _settings.ModeName };

            var practice = _registry.Get(playerId, practice: true);
            return practice == null
                ? new ChallengeResponse { Result = WorldBossStrikeResult.NoChallenge, Mode = _settings.ModeName }
                : new ChallengeResponse { Result = WorldBossStrikeResult.Outstanding, Mode = _settings.ModeName, Challenge = ChallengeDto.From(practice, now) };
        }

        public ChallengeResponse IssueChallenge(long playerId, bool practice)
        {
            long now = _nowMs();
            _registry.ExpireDue(now);

            // Modul: PHASE 1 ISSUES PRACTICE ONLY. A real challenge is the
            // path that spends an attempt and deals damage; it arrives with
            // Phase 2 and its security review, so until then it is Disabled
            // even with the flag at "wheel".
            if (!PracticeOpen || !practice) return new ChallengeResponse { Result = WorldBossStrikeResult.Disabled, Mode = _settings.ModeName };

            var (challenge, issued) = _registry.IssueOrGet(playerId, practice: true, enraged: false, now);
            return new ChallengeResponse
            {
                Result = issued ? WorldBossStrikeResult.Issued : WorldBossStrikeResult.Outstanding,
                Mode = _settings.ModeName,
                Challenge = ChallengeDto.From(challenge, now),
            };
        }

        private WorldBossChallenge? Find(long playerId, string challengeId)
        {
            var practice = _registry.Get(playerId, practice: true);
            if (practice != null && string.Equals(practice.ChallengeId, challengeId, StringComparison.Ordinal)) return practice;
            return null;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        public ThrowResponse Throw(long playerId, ThrowRequest request)
        {
            long now = _nowMs();
            _registry.ExpireDue(now);
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
                && parries.All(p => interrupts.ContainsKey(p.Interrupt) && Enum.IsDefined(p.Choice) && Finite(p.ChoiceMs))
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
                var parry = parries.First(p => p.Interrupt == counter.Interrupt);
                bool inWindow = reads.TryGetValue(counter.Interrupt, out var read)
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

            // Practice answers from ITS decoy, never the real secret.
            bool weakHit = landing.Class >= SpearClass.Plate && landing.Plate == challenge.DecoyWeakPlate;
            var recorded = _registry.RecordThrow(challenge, new RecordedThrow(request.Seq, request.TapMs, request.Counter, landing, weakHit));
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

        public PracticeScoreResponse ScorePractice(long playerId, StrikeRequest request)
        {
            long now = _nowMs();
            _registry.ExpireDue(now);
            if (!PracticeOpen) return new PracticeScoreResponse { Result = WorldBossStrikeResult.Disabled };

            var challenge = _registry.TryTake(playerId, practice: true, request.ChallengeId ?? string.Empty);
            if (challenge == null) return new PracticeScoreResponse { Result = WorldBossStrikeResult.NoChallenge };

            var log = new StrikeLog(
                (IReadOnlyList<WheelTap>?)request.Taps ?? Array.Empty<WheelTap>(),
                (IReadOnlyList<ParryEntry>?)request.Parries ?? Array.Empty<ParryEntry>(),
                (IReadOnlyList<CounterEntry>?)request.Counters ?? Array.Empty<CounterEntry>());
            var scored = ShieldWheelScorer.Score(challenge.Schedule, log, now - challenge.IssuedAtMs);

            if (scored.Verdict == SubmissionVerdict.Refused) WorldBossStrikeTelemetry.Record(playerId, scored.RefusalDetail);
            else foreach (var suspicion in scored.Suspicions) WorldBossStrikeTelemetry.Record(playerId, suspicion);

            int weak = challenge.DecoyWeakPlate;
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

        /// <summary>Phase 1: the real strike is Disabled whatever the flag (see IssueChallenge).</summary>
        public StrikeResponse Strike(long playerId, StrikeRequest request) => new() { Result = WorldBossStrikeResult.Disabled };
    }
}
