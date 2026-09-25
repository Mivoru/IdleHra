using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>A wheel tap: the Seq of the spear and when it was thrown.</summary>
    public sealed record WheelTap(int Seq, double TapMs);

    /// <summary>The player's answer to interrupt <paramref name="Interrupt"/>.</summary>
    public sealed record ParryEntry(int Interrupt, ParryChoice Choice, double ChoiceMs);

    /// <summary>A spear thrown at a chosen plate during a counter window.</summary>
    public sealed record CounterEntry(int Interrupt, int Seq, double TapMs, int Plate);

    /// <summary>Everything the client reports about one strike: times and choices, never a number the server adopts.</summary>
    public sealed record StrikeLog(
        IReadOnlyList<WheelTap> Taps,
        IReadOnlyList<ParryEntry> Parries,
        IReadOnlyList<CounterEntry> Counters)
    {
        public static readonly StrikeLog Empty = new(Array.Empty<WheelTap>(), Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>());
    }

    public enum SubmissionVerdict
    {
        Accepted = 0,
        Refused = 1,
    }

    /// <summary>
    /// Telemetry detail codes (spec section 6). Written only by
    /// WorldBossStrikeTelemetry, as EventType 8.
    /// </summary>
    public enum StrikeSuspicion
    {
        None = 0,
        PreciseWheel = 1,
        InhumanReactionOrGuessHit = 2,
        DroppedTap = 3,
        ShapeRefusal = 4,
        WallClock = 5,
        ThrowFinishMismatch = 6,
    }

    /// <summary>The scorer's answer. Landings are in tap order, one per spear actually thrown.</summary>
    public sealed record ScoredAttempt(
        IReadOnlyList<SpearLanding> Landings,
        int SpearsLost,
        SubmissionVerdict Verdict,
        StrikeSuspicion RefusalDetail,
        IReadOnlyCollection<StrikeSuspicion> Suspicions)
    {
        public double Score => WorldBossStrikeRules.Score(Landings);
        public double Multiplier => Verdict == SubmissionVerdict.Accepted ? WorldBossStrikeRules.Multiplier(Score) : WorldBossStrikeRules.Floor;
    }

    /// <summary>
    /// Scores a strike log against the server's own schedule. Pure, and it
    /// never throws: every malformed input is a Refused verdict with a detail.
    /// </summary>
    /// <remarks>
    /// Modul: SUSPICION NEVER LOWERS A SCORE. A perfect human and a bot earn
    /// the same capped M (spec section 6), so the only thing an "improbable"
    /// flag may do is reach telemetry. Nothing here, or downstream, penalises
    /// a player for it - the macro detector's permanent bans are the precedent
    /// this refuses to repeat.
    /// </remarks>
    public static class ShieldWheelScorer
    {
        /// <summary>A wheel tap within this many ms of its seam centre-crossing counts as "precise".</summary>
        public const double PreciseWithinMs = 3.0;
        /// <summary>Three or more reads faster than this are flagged.</summary>
        public const int FastReadMs = 180;
        public const int FastReadsFlagged = 3;
        /// <summary>The wall-clock rule allows this much slack for clock resolution.</summary>
        public const int WallClockSlackMs = 50;

        public static ScoredAttempt Score(ShieldWheelSchedule schedule, StrikeLog log, double receivedAfterIssueMs)
        {
            try
            {
                return ScoreCore(schedule, log, receivedAfterIssueMs);
            }
            catch (Exception)
            {
                // Pure arithmetic over validated input should never land here;
                // if it does, the attempt is refused rather than the request 500ing.
                return Refused(StrikeSuspicion.ShapeRefusal);
            }
        }

        private static ScoredAttempt Refused(StrikeSuspicion detail) =>
            new(Array.Empty<SpearLanding>(), 0, SubmissionVerdict.Refused, detail, new[] { detail });

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static ScoredAttempt ScoreCore(ShieldWheelSchedule schedule, StrikeLog log, double receivedAfterIssueMs)
        {
            var taps = log.Taps ?? Array.Empty<WheelTap>();
            var parries = log.Parries ?? Array.Empty<ParryEntry>();
            var counters = log.Counters ?? Array.Empty<CounterEntry>();

            // --- shape --------------------------------------------------------
            if (taps.Count + counters.Count > WorldBossStrikeRules.Spears) return Refused(StrikeSuspicion.ShapeRefusal);

            var spears = new List<(int Seq, double TapMs, CounterEntry? Counter)>();
            foreach (var tap in taps) spears.Add((tap.Seq, tap.TapMs, null));
            foreach (var counter in counters) spears.Add((counter.Seq, counter.TapMs, counter));

            foreach (var spear in spears)
            {
                if (!Finite(spear.TapMs)) return Refused(StrikeSuspicion.ShapeRefusal);
                if (spear.Seq < 0 || spear.Seq >= WorldBossStrikeRules.Spears) return Refused(StrikeSuspicion.ShapeRefusal);
                if (spear.TapMs < 0 || spear.TapMs > WorldBossStrikeRules.MaxPlayMs) return Refused(StrikeSuspicion.ShapeRefusal);
            }
            if (spears.Select(s => s.Seq).Distinct().Count() != spears.Count) return Refused(StrikeSuspicion.ShapeRefusal);
            spears.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            for (int i = 1; i < spears.Count; i++)
            {
                if (spears[i].TapMs <= spears[i - 1].TapMs) return Refused(StrikeSuspicion.ShapeRefusal);
            }

            // Two WHEEL taps closer than the reload the client enforces.
            double lastWheel = double.NegativeInfinity;
            foreach (var spear in spears)
            {
                if (spear.Counter != null) continue;
                if (spear.TapMs - lastWheel < WorldBossStrikeRules.MinReloadMs - 50) return Refused(StrikeSuspicion.ShapeRefusal);
                lastWheel = spear.TapMs;
            }

            var interruptsByIndex = schedule.Interrupts.ToDictionary(i => i.Index);
            foreach (var parry in parries)
            {
                if (!interruptsByIndex.ContainsKey(parry.Interrupt)) return Refused(StrikeSuspicion.ShapeRefusal);
                if (!Enum.IsDefined(parry.Choice)) return Refused(StrikeSuspicion.ShapeRefusal);
                if (!Finite(parry.ChoiceMs) || parry.ChoiceMs < 0 || parry.ChoiceMs > WorldBossStrikeRules.MaxPlayMs) return Refused(StrikeSuspicion.ShapeRefusal);
            }
            if (parries.Select(p => p.Interrupt).Distinct().Count() != parries.Count) return Refused(StrikeSuspicion.ShapeRefusal);

            foreach (var counter in counters)
            {
                if (!interruptsByIndex.ContainsKey(counter.Interrupt)) return Refused(StrikeSuspicion.ShapeRefusal);
                if (counter.Plate < 0 || counter.Plate >= WorldBossStrikeRules.PlateCount) return Refused(StrikeSuspicion.ShapeRefusal);
            }
            if (counters.Select(c => c.Interrupt).Distinct().Count() != counters.Count) return Refused(StrikeSuspicion.ShapeRefusal);

            // --- wall clock ---------------------------------------------------
            double lastTap = spears.Count == 0 ? 0 : spears.Max(s => s.TapMs);
            if (!Finite(receivedAfterIssueMs)
                || receivedAfterIssueMs < WorldBossStrikeRules.CountdownMs + lastTap - WallClockSlackMs)
            {
                return Refused(StrikeSuspicion.WallClock);
            }

            var suspicions = new HashSet<StrikeSuspicion>();

            // --- parries ------------------------------------------------------
            // An interrupt is "reached" if its tell comes at or before the last
            // spear actually thrown. After the last spear there is nothing to lose.
            var reads = new Dictionary<int, (double From, double To)>();
            int spearsLost = 0;
            int fastReads = 0;
            foreach (var interrupt in schedule.Interrupts)
            {
                bool reached = spears.Count > 0 && interrupt.TellAtMs <= lastTap;
                var parry = parries.FirstOrDefault(p => p.Interrupt == interrupt.Index);
                var correct = ShieldWheelSchedule.CorrectChoice(interrupt.Tell);

                bool isRead = IsRead(interrupt, parry);

                if (parry != null && parry.ChoiceMs < interrupt.TellAtMs + interrupt.ReactionFloorMs && parry.Choice == correct)
                {
                    suspicions.Add(StrikeSuspicion.InhumanReactionOrGuessHit);
                }

                if (isRead)
                {
                    reads[interrupt.Index] = (parry!.ChoiceMs, interrupt.EndMs);
                    if (parry.ChoiceMs - interrupt.TellAtMs < FastReadMs) fastReads++;
                }
                else if (reached)
                {
                    spearsLost++;
                }
            }
            if (fastReads >= FastReadsFlagged) suspicions.Add(StrikeSuspicion.InhumanReactionOrGuessHit);

            foreach (var counter in counters)
            {
                if (!reads.ContainsKey(counter.Interrupt)) return Refused(StrikeSuspicion.ShapeRefusal);
            }

            if (spears.Count + spearsLost > WorldBossStrikeRules.Spears) return Refused(StrikeSuspicion.ShapeRefusal);

            // --- landings -----------------------------------------------------
            var landings = new List<SpearLanding>();
            int preciseWheelTaps = 0;
            int wheelTaps = 0;
            foreach (var spear in spears)
            {
                if (spear.Counter is { } counter)
                {
                    var window = reads[counter.Interrupt];
                    if (counter.TapMs >= window.From && counter.TapMs <= window.To)
                    {
                        landings.Add(new SpearLanding(spear.Seq, counter.Plate, SpearClass.Seam, true));
                    }
                    else
                    {
                        suspicions.Add(StrikeSuspicion.DroppedTap);
                    }
                    continue;
                }

                if (schedule.FrozenAt(spear.TapMs))
                {
                    // An honest client locks the zone, but boundary jitter must
                    // not cost a whole attempt: dropped, not refused.
                    suspicions.Add(StrikeSuspicion.DroppedTap);
                    continue;
                }

                var landing = LandWheelTap(schedule, spear.Seq, spear.TapMs);
                landings.Add(landing);
                wheelTaps++;
                if (IsPrecise(schedule, spear.TapMs)) preciseWheelTaps++;
            }
            if (wheelTaps >= 2 && preciseWheelTaps == wheelTaps) suspicions.Add(StrikeSuspicion.PreciseWheel);

            return new ScoredAttempt(landings, spearsLost, SubmissionVerdict.Accepted, StrikeSuspicion.None, suspicions);
        }

        /// <summary>
        /// A correct read: the right answer, no sooner than the reaction floor
        /// and no later than the response window. Shared by the finish and the
        /// per-throw answer so the two cannot disagree.
        /// </summary>
        public static bool IsRead(ShieldWheelInterrupt interrupt, ParryEntry? parry) =>
            parry != null
            && parry.ChoiceMs >= interrupt.TellAtMs + interrupt.ReactionFloorMs
            && parry.ChoiceMs <= interrupt.TellAtMs + interrupt.ResponseCloseMs
            && parry.Choice == ShieldWheelSchedule.CorrectChoice(interrupt.Tell);

        /// <summary>
        /// Where a wheel tap lands: the plate under the impact point FlightMs
        /// after the exact tap, and the best class on THAT plate anywhere in
        /// the tolerance window. The tolerance never moves a spear to another plate.
        /// </summary>
        public static SpearLanding LandWheelTap(ShieldWheelSchedule schedule, int seq, double tapMs)
        {
            int plate = schedule.PlateAt(tapMs + WorldBossStrikeRules.FlightMs);
            var best = SpearClass.None;
            for (int d = -WorldBossStrikeRules.ToleranceMs; d <= WorldBossStrikeRules.ToleranceMs; d += WorldBossStrikeRules.ToleranceStepMs)
            {
                double angle = schedule.AngleAt(tapMs + d + WorldBossStrikeRules.FlightMs);
                if (WorldBossStrikeRules.PlateOf(angle) != plate) continue;
                var cls = WorldBossStrikeRules.Classify(WorldBossStrikeRules.OffsetInPlate(angle));
                if (cls > best) best = cls;
            }
            if (best == SpearClass.None) best = SpearClass.Glance;
            return new SpearLanding(seq, plate, best, false);
        }

        private static bool IsPrecise(ShieldWheelSchedule schedule, double tapMs)
        {
            double landing = tapMs + WorldBossStrikeRules.FlightMs;
            double speed = Math.Abs(schedule.SpeedAt(landing));
            if (speed <= 0) return false;
            double offset = WorldBossStrikeRules.OffsetInPlate(schedule.AngleAt(landing));
            double degreesFromCentre = Math.Abs(offset - WorldBossStrikeRules.PlateDegrees / 2.0);
            return degreesFromCentre <= speed * PreciseWithinMs / 1000.0 + 1e-9;
        }
    }
}
