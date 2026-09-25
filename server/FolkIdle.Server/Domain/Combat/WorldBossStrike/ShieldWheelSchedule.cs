using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>Where the boss's blow will land. Drawn on the client by position and shape.</summary>
    public enum ParryTell
    {
        Left = 0,
        Right = 1,
        Overhead = 2,
    }

    /// <summary>The player's answer to a tell.</summary>
    public enum ParryChoice
    {
        DodgeLeft = 0,
        Block = 1,
        DodgeRight = 2,
    }

    /// <summary>
    /// One piece of the wheel's motion: constant speed from StartMs for
    /// DurationMs. DegPerSec 0 with an Interrupt index is a frozen interrupt.
    /// </summary>
    public sealed record ShieldWheelSegment(int StartMs, int DurationMs, double DegPerSec, int? Interrupt = null)
    {
        public int EndMs => StartMs + DurationMs;
        public bool Frozen => Interrupt.HasValue;
    }

    /// <summary>One boss wind-up: the wheel stops at TellAtMs for InterruptMs.</summary>
    public sealed record ShieldWheelInterrupt(int Index, int TellAtMs, ParryTell Tell, int ReactionFloorMs, int ResponseCloseMs, int InterruptMs)
    {
        public int EndMs => TellAtMs + InterruptMs;
    }

    /// <summary>
    /// A whole strike's wheel, sent to the client as explicit data - never as a
    /// seed. Times are milliseconds after the countdown ends.
    /// </summary>
    /// <remarks>
    /// Modul: THE ANGLE IS RING-LOCAL, UNDER THE IMPACT POINT. AngleAt(t) is
    /// the angle of the ring that sits at the impact point at time t, so
    /// PlateOf(AngleAt(t)) is the plate a spear landing at t strikes. The
    /// client draws the ring rotated by -AngleAt(t). That one convention is
    /// shared through Fixtures/shield_wheel_cases.json, which both the C# and
    /// the TypeScript tests read, so the drawn ring cannot disagree with the
    /// scored one.
    /// </remarks>
    public sealed record ShieldWheelSchedule(
        double StartAngleDeg,
        IReadOnlyList<ShieldWheelSegment> Segments,
        IReadOnlyList<ShieldWheelInterrupt> Interrupts,
        bool Practice,
        bool Enraged)
    {
        /// <summary>The ring-local angle at the impact point, t ms after the countdown.</summary>
        public double AngleAt(double tMs)
        {
            double angle = StartAngleDeg;
            if (tMs <= 0) return WorldBossStrikeRules.Normalize(angle);

            ShieldWheelSegment? last = null;
            foreach (var segment in Segments)
            {
                last = segment;
                if (tMs <= segment.StartMs) break;
                double inside = Math.Min(tMs, segment.EndMs) - segment.StartMs;
                angle += segment.DegPerSec * inside / 1000.0;
                if (tMs <= segment.EndMs) return WorldBossStrikeRules.Normalize(angle);
            }

            // Past the last segment (a landing just after MaxPlayMs): keep
            // turning at the last moving speed rather than stopping dead.
            if (last != null && tMs > last.EndMs)
            {
                var moving = Segments.LastOrDefault(s => !s.Frozen);
                if (moving != null) angle += moving.DegPerSec * (tMs - last.EndMs) / 1000.0;
            }
            return WorldBossStrikeRules.Normalize(angle);
        }

        public double SpeedAt(double tMs)
        {
            foreach (var segment in Segments)
            {
                if (tMs >= segment.StartMs && tMs < segment.EndMs) return segment.DegPerSec;
            }
            return Segments.LastOrDefault(s => !s.Frozen)?.DegPerSec ?? 0;
        }

        public int PlateAt(double tMs) => WorldBossStrikeRules.PlateOf(AngleAt(tMs));

        /// <summary>The interrupt whose frozen interval contains t, if any.</summary>
        public ShieldWheelInterrupt? InterruptAt(double tMs)
        {
            foreach (var interrupt in Interrupts)
            {
                if (tMs >= interrupt.TellAtMs && tMs < interrupt.EndMs) return interrupt;
            }
            return null;
        }

        public bool FrozenAt(double tMs) => InterruptAt(tMs) != null;

        public int TotalMs => Segments.Count == 0 ? 0 : Segments[^1].EndMs;

        public static ParryChoice CorrectChoice(ParryTell tell) => tell switch
        {
            ParryTell.Left => ParryChoice.DodgeRight,
            ParryTell.Right => ParryChoice.DodgeLeft,
            _ => ParryChoice.Block,
        };

        /// <summary>
        /// How many times the centre of each plate's seam passes the impact
        /// point while the wheel is moving. A plate that never comes round can
        /// only be hit by a counter, which makes a broken schedule.
        /// </summary>
        public int[] SeamPasses()
        {
            var passes = new int[WorldBossStrikeRules.PlateCount];
            double angle = StartAngleDeg;
            foreach (var segment in Segments)
            {
                double sweep = segment.DegPerSec * segment.DurationMs / 1000.0;
                if (!segment.Frozen && sweep != 0)
                {
                    double from = Math.Min(angle, angle + sweep);
                    double to = Math.Max(angle, angle + sweep);
                    for (int plate = 0; plate < passes.Length; plate++)
                    {
                        double centre = plate * WorldBossStrikeRules.PlateDegrees + WorldBossStrikeRules.PlateDegrees / 2.0;
                        // Every c + 360k inside (from, to].
                        double first = centre + 360.0 * Math.Ceiling((from - centre) / 360.0);
                        if (first <= from) first += 360.0;
                        for (double c = first; c <= to; c += 360.0) passes[plate]++;
                    }
                }
                angle += sweep;
            }
            return passes;
        }

        /// <summary>Seam passes each plate needs in a valid schedule.</summary>
        public const int MinSeamPassesPerPlate = 3;

        /// <summary>
        /// A fresh schedule from the cryptographic generator. Rejection-sampled
        /// until every plate's seam passes the impact point at least
        /// <see cref="MinSeamPassesPerPlate"/> times; the generator property
        /// test shows that takes a handful of draws, never a loop.
        /// </summary>
        public static ShieldWheelSchedule Generate(RandomNumberGenerator rng, bool practice, bool enraged = false)
        {
            for (int attempt = 0; attempt < 1_000; attempt++)
            {
                var schedule = Draw(rng, practice, enraged);
                if (schedule.SeamPasses().All(p => p >= MinSeamPassesPerPlate)) return schedule;
            }
            throw new InvalidOperationException("the shield wheel generator could not satisfy the seam-pass rule in 1,000 draws");
        }

        private static int Next(RandomNumberGenerator rng, int minInclusive, int maxInclusive)
        {
            if (maxInclusive <= minInclusive) return minInclusive;
            Span<byte> buffer = stackalloc byte[4];
            rng.GetBytes(buffer);
            uint value = BitConverter.ToUInt32(buffer);
            return minInclusive + (int)(value % (uint)(maxInclusive - minInclusive + 1));
        }

        private static ShieldWheelSchedule Draw(RandomNumberGenerator rng, bool practice, bool enraged)
        {
            int minSpeed = enraged ? WorldBossStrikeRules.EnragedMinSpeedDegPerSec : WorldBossStrikeRules.MinSpeedDegPerSec;
            int maxSpeed = enraged ? WorldBossStrikeRules.EnragedMaxSpeedDegPerSec : WorldBossStrikeRules.MaxSpeedDegPerSec;
            int minDur = enraged ? WorldBossStrikeRules.EnragedMinSegmentMs : WorldBossStrikeRules.MinSegmentMs;
            int maxDur = enraged ? WorldBossStrikeRules.EnragedMaxSegmentMs : WorldBossStrikeRules.MaxSegmentMs;
            int responseClose = enraged ? WorldBossStrikeRules.EnragedResponseCloseMs : WorldBossStrikeRules.ResponseCloseMs;

            int interruptCount = enraged ? 3 : Next(rng, 2, 3);

            // Tell times, in 10 ms steps: the first at or after 3,000, each at
            // least 4,000 after the previous, the last ending by MaxPlay - 2,000.
            int latestTell = WorldBossStrikeRules.LastInterruptEndsByMs - WorldBossStrikeRules.InterruptMs;
            var tells = new int[interruptCount];
            int earliest = WorldBossStrikeRules.FirstTellAtLeastMs;
            for (int i = 0; i < interruptCount; i++)
            {
                int remaining = interruptCount - 1 - i;
                int latest = latestTell - remaining * WorldBossStrikeRules.TellGapAtLeastMs;
                tells[i] = Next(rng, earliest / 10, latest / 10) * 10;
                earliest = tells[i] + WorldBossStrikeRules.TellGapAtLeastMs;
            }

            var interrupts = new List<ShieldWheelInterrupt>(interruptCount);
            var segments = new List<ShieldWheelSegment>();
            int cursor = 0;
            for (int i = 0; i < interruptCount; i++)
            {
                AddMoving(rng, segments, cursor, tells[i], minSpeed, maxSpeed, minDur, maxDur);
                var tell = (ParryTell)Next(rng, 0, 2);
                interrupts.Add(new ShieldWheelInterrupt(i, tells[i], tell, WorldBossStrikeRules.ReactionFloorMs, responseClose, WorldBossStrikeRules.InterruptMs));
                segments.Add(new ShieldWheelSegment(tells[i], WorldBossStrikeRules.InterruptMs, 0, i));
                cursor = tells[i] + WorldBossStrikeRules.InterruptMs;
            }
            AddMoving(rng, segments, cursor, WorldBossStrikeRules.MaxPlayMs, minSpeed, maxSpeed, minDur, maxDur);

            double startAngle = Next(rng, 0, 3599) / 10.0;
            return new ShieldWheelSchedule(startAngle, segments, interrupts, practice, enraged);
        }

        /// <summary>
        /// Fills [from, to) with moving segments whose durations all sit in
        /// [minDur, maxDur]: the smallest piece count that fits, each piece
        /// starting at minDur and taking a random share of the remainder.
        /// </summary>
        private static void AddMoving(RandomNumberGenerator rng, List<ShieldWheelSegment> segments, int from, int to,
            int minSpeed, int maxSpeed, int minDur, int maxDur)
        {
            int length = to - from;
            if (length <= 0) return;

            int minPieces = (int)Math.Ceiling(length / (double)maxDur);
            int maxPieces = Math.Max(minPieces, length / minDur);
            int pieces = Next(rng, minPieces, maxPieces);

            var durations = new int[pieces];
            int remainder = length;
            for (int i = 0; i < pieces; i++) { durations[i] = Math.Min(minDur, length / pieces); remainder -= durations[i]; }
            // Hand out the remainder in 10 ms steps to random pieces with room.
            while (remainder > 0)
            {
                int step = Math.Min(10, remainder);
                int i = Next(rng, 0, pieces - 1);
                for (int k = 0; k < pieces && durations[i] + step > maxDur; k++) i = (i + 1) % pieces;
                durations[i] += step;
                remainder -= step;
            }

            int start = from;
            foreach (int duration in durations)
            {
                int speed = Next(rng, minSpeed, maxSpeed) * (Next(rng, 0, 1) == 0 ? 1 : -1);
                segments.Add(new ShieldWheelSegment(start, duration, speed));
                start += duration;
            }
        }
    }
}
