using System;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>What the player answers a signature move with. None = the call timed out.</summary>
    public enum DuelOption : byte
    {
        None = 0,
        Guard = 1,
        Strike = 2,
        Evade = 3,
    }

    /// <summary>Chernobog's signature moves. 1-5 in his own form, 6-8 only as the Black Serpent.</summary>
    public enum DuelMove : byte
    {
        CrushingBlow = 1,
        BlackFlame = 2,
        SerpentCoil = 3,
        CurseOfNight = 4,
        SummonShade = 5,
        VenomSpit = 6,
        TailSweep = 7,
        DeathCoil = 8,
    }

    public enum DuelVerdict : byte
    {
        Wrong = 0,
        Fair = 1,
        Right = 2,
    }

    /// <summary>The build a duel is fought with: the deepest combat column of the world-boss build.</summary>
    public enum DuelStance : byte
    {
        Balanced = 0,
        Might = 1,
        Precision = 2,
        Guard = 3,
    }

    public sealed record DuelMoveDef(DuelMove Move, string Name, string Tell, bool SerpentOnly);

    public sealed record DuelOmen(int Index, string Name, string Text, DuelMove Move, DuelOption Answer);

    /// <summary>
    /// The duel that replaces the shield wheel (owner 2026-11-14; four model
    /// evaluators chose "P4 + P6" unanimously - docs/design/WORLD_BOSS_REWORK_2026_11_14.md).
    /// Pure rules: no clock, no randomness of its own, no database.
    /// </summary>
    /// <remarks>
    /// Modul: THE CALL IS A DECISION, NOT A REFLEX. The wheel priced a 8-degree
    /// band at spinning speed and a 2-second parry; the owner could not read
    /// the parry in time on the phone (playtest 2026-09-25) and called the
    /// whole thing "nothing much". Here the boss shows a named move with a
    /// tell, and the player picks one of three answers. The right answer
    /// depends on the move, on the player's build (stance) and on the day's
    /// omen, so it is a reading of three things rather than one lookup.
    ///
    /// Modul: THE SAME ECONOMY AS THE WHEEL. M runs from 1.0 (auto-strike,
    /// timeouts) to 2.0 (every call right), exactly the wheel's Floor and Cap,
    /// so the boss's health sizing (WorldBossHpRules), the milestones and the
    /// sandbag detector keep their meaning across the switch. The plates and
    /// their 3x weak plate are gone: the duel has no hidden secret to deduce.
    /// </remarks>
    public static class BossDuelRules
    {
        public const int Calls = 3;

        /// <summary>Seconds a call stays open on the client before it answers None. The server does not time calls: they are decisions.</summary>
        public const int CallWindowMs = 6_000;

        /// <summary>A challenge left open this long is resolved with the calls already answered.</summary>
        public const long ChallengeLifetimeMs = 180_000;

        public const double Floor = WorldBossStrikeRules.Floor;
        public const double Cap = WorldBossStrikeRules.Cap;

        /// <summary>
        /// Modul: what one call adds to M. Three right calls add 0.9, and the
        /// finisher (all three right) the last 0.1 - the cap. A Fair call is
        /// worth 0.15 of a right one. Measured (BossDuelTests ledger, 20,000
        /// duels per stance): at 0.35 random answers landed at 1.43-1.50, above
        /// the wheel's random tapping (1.35, spec 3.1), so guessing paid too
        /// much; 0.15 brings it to the numbers the ledger prints.
        /// </summary>
        public const double PerRightCall = 0.30;
        public const double FairShare = 0.15;
        public const double FinisherBonus = 0.10;

        /// <summary>The base hit is struck as four rounds of blows around the three calls.</summary>
        public const int Rounds = Calls + 1;
        public const int BlowsPerRound = 4;

        /// <summary>A column must hold this many points to set the stance; below it the build is Balanced.</summary>
        public const int StanceMinPoints = 5;

        public static readonly IReadOnlyList<DuelMoveDef> Moves = new[]
        {
            new DuelMoveDef(DuelMove.CrushingBlow, "Crushing Blow", "Chernobog lifts his iron mace high. Slow - and it will split the ground where you stand.", false),
            new DuelMoveDef(DuelMove.BlackFlame, "Black Flame", "He draws the dark fire into his palm. The spell needs a breath to finish.", false),
            new DuelMoveDef(DuelMove.SerpentCoil, "Serpent Coil", "Scales slide over his armour and he hunches behind them, waiting.", false),
            new DuelMoveDef(DuelMove.CurseOfNight, "Curse of Night", "A ring of black runes opens on the ground around you.", false),
            new DuelMoveDef(DuelMove.SummonShade, "Summon Shade", "A shade crawls out of his shadow and comes for you.", false),
            new DuelMoveDef(DuelMove.VenomSpit, "Venom Spit", "The Serpent rears back, throat swelling with venom.", true),
            new DuelMoveDef(DuelMove.TailSweep, "Tail Sweep", "The great tail coils low behind it, ready to sweep the floor.", true),
            new DuelMoveDef(DuelMove.DeathCoil, "Death Coil", "The Serpent's body loops around you and begins to tighten.", true),
        };

        /// <summary>His own form draws from these.</summary>
        public static readonly DuelMove[] NormalPool =
            { DuelMove.CrushingBlow, DuelMove.BlackFlame, DuelMove.SerpentCoil, DuelMove.CurseOfNight, DuelMove.SummonShade };

        /// <summary>At or below 25% health (the Black Serpent form) - the serpent's three, plus two he keeps.</summary>
        public static readonly DuelMove[] SerpentPool =
            { DuelMove.VenomSpit, DuelMove.TailSweep, DuelMove.DeathCoil, DuelMove.CrushingBlow, DuelMove.BlackFlame };

        /// <summary>
        /// One omen a day, by the encounter's day (Monday = 0). Each makes a
        /// different answer right for one move, and that move always comes
        /// once in the day's duel, so reading the omen is always worth it.
        /// </summary>
        public static readonly IReadOnlyList<DuelOmen> Omens = new[]
        {
            new DuelOmen(0, "Perun's Thunder", "Thunder rides your arm today: meet the Crushing Blow head-on.", DuelMove.CrushingBlow, DuelOption.Strike),
            new DuelOmen(1, "Mokosh's Veil", "The Mother's veil covers you: stand your ground against the Curse of Night.", DuelMove.CurseOfNight, DuelOption.Guard),
            new DuelOmen(2, "Svarog's Fire", "The forge-fire guards its own: raise your shield into the Black Flame.", DuelMove.BlackFlame, DuelOption.Guard),
            new DuelOmen(3, "Veles in the Reeds", "Veles hides you in the reeds: slip past the Shade.", DuelMove.SummonShade, DuelOption.Evade),
            new DuelOmen(4, "Dazhbog's Dawn", "Dawn light shows the gaps: circle the Serpent Coil and find one.", DuelMove.SerpentCoil, DuelOption.Evade),
            new DuelOmen(5, "Stribog's Wind", "The wind blows the venom back: strike through the Venom Spit.", DuelMove.VenomSpit, DuelOption.Strike),
            new DuelOmen(6, "Simargl's Wing", "Simargl lifts you over the tail: strike as the Tail Sweep passes.", DuelMove.TailSweep, DuelOption.Strike),
        };

        public static DuelMoveDef Def(DuelMove move) => Moves[(int)move - 1];

        /// <summary>Monday = 0 .. Sunday = 6, from the UTC day key the calendar uses.</summary>
        public static DuelOmen OmenFor(long dayKey)
        {
            // Day key 0 is 1970-01-01, a Thursday (index 3).
            int index = (int)(((dayKey + 3) % 7 + 7) % 7);
            return Omens[index];
        }

        public static DuelOmen OmenFor(DateTime utc) => OmenFor(WorldBossCalendar.DayKey(new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds()));

        /// <summary>
        /// The stance of a build: the deepest of Might, Precision and Guard,
        /// when it holds at least StanceMinPoints and strictly more than the
        /// other two. Fortune is not a fighting stance.
        /// </summary>
        public static DuelStance StanceOf(ReadOnlySpan<byte> ranks)
        {
            int might = WarTreeRegistry.PointsInColumn(ranks, WarTreeRegistry.ColumnMight);
            int precision = WarTreeRegistry.PointsInColumn(ranks, WarTreeRegistry.ColumnPrecision);
            int guard = WarTreeRegistry.PointsInColumn(ranks, WarTreeRegistry.ColumnGuard);
            int top = Math.Max(might, Math.Max(precision, guard));
            if (top < StanceMinPoints) return DuelStance.Balanced;
            int ties = (might == top ? 1 : 0) + (precision == top ? 1 : 0) + (guard == top ? 1 : 0);
            if (ties > 1) return DuelStance.Balanced;
            return might == top ? DuelStance.Might : precision == top ? DuelStance.Precision : DuelStance.Guard;
        }

        /// <summary>The move's answers before stance and omen: what anyone may read from the tell.</summary>
        private static DuelVerdict BaseVerdict(DuelMove move, DuelOption option) => (move, option) switch
        {
            // A slow overhead blow: step aside and it misses; a shield takes some of it.
            (DuelMove.CrushingBlow, DuelOption.Evade) => DuelVerdict.Right,
            (DuelMove.CrushingBlow, DuelOption.Guard) => DuelVerdict.Fair,
            // A spell that needs a breath: hit him before it finishes.
            (DuelMove.BlackFlame, DuelOption.Strike) => DuelVerdict.Right,
            (DuelMove.BlackFlame, DuelOption.Evade) => DuelVerdict.Fair,
            // He turtles: hit the coil before it closes.
            (DuelMove.SerpentCoil, DuelOption.Strike) => DuelVerdict.Right,
            (DuelMove.SerpentCoil, DuelOption.Evade) => DuelVerdict.Fair,
            // A ring on the ground: get out of it.
            (DuelMove.CurseOfNight, DuelOption.Evade) => DuelVerdict.Right,
            (DuelMove.CurseOfNight, DuelOption.Guard) => DuelVerdict.Fair,
            // A small enemy: cut it down.
            (DuelMove.SummonShade, DuelOption.Strike) => DuelVerdict.Right,
            (DuelMove.SummonShade, DuelOption.Guard) => DuelVerdict.Fair,
            // Spit flies straight: a shield catches it.
            (DuelMove.VenomSpit, DuelOption.Guard) => DuelVerdict.Right,
            (DuelMove.VenomSpit, DuelOption.Evade) => DuelVerdict.Fair,
            // A low sweep: jump it.
            (DuelMove.TailSweep, DuelOption.Evade) => DuelVerdict.Right,
            (DuelMove.TailSweep, DuelOption.Guard) => DuelVerdict.Fair,
            // Being crushed: cut yourself free.
            (DuelMove.DeathCoil, DuelOption.Strike) => DuelVerdict.Right,
            (DuelMove.DeathCoil, DuelOption.Guard) => DuelVerdict.Fair,
            _ => DuelVerdict.Wrong,
        };

        /// <summary>
        /// Modul: WHAT A BUILD CHANGES. Each stance turns two or three answers
        /// that are wrong or fair for everyone into right (or fair) ones for
        /// itself: a Guard build can take the Crushing Blow on its shield, a
        /// Precision build slips the Black Flame and finds the gap in the Coil,
        /// a Might build breaks the Curse's ring before it closes. Same three
        /// buttons for everyone; different right answers per build.
        /// </summary>
        private static DuelVerdict? StanceVerdict(DuelStance stance, DuelMove move, DuelOption option) => (stance, move, option) switch
        {
            (DuelStance.Might, DuelMove.CurseOfNight, DuelOption.Strike) => DuelVerdict.Right,
            (DuelStance.Might, DuelMove.CrushingBlow, DuelOption.Strike) => DuelVerdict.Fair,
            (DuelStance.Might, DuelMove.TailSweep, DuelOption.Strike) => DuelVerdict.Fair,
            (DuelStance.Precision, DuelMove.BlackFlame, DuelOption.Evade) => DuelVerdict.Right,
            (DuelStance.Precision, DuelMove.SerpentCoil, DuelOption.Evade) => DuelVerdict.Right,
            (DuelStance.Precision, DuelMove.VenomSpit, DuelOption.Evade) => DuelVerdict.Right,
            (DuelStance.Guard, DuelMove.CrushingBlow, DuelOption.Guard) => DuelVerdict.Right,
            (DuelStance.Guard, DuelMove.SummonShade, DuelOption.Guard) => DuelVerdict.Right,
            (DuelStance.Guard, DuelMove.SerpentCoil, DuelOption.Guard) => DuelVerdict.Fair,
            _ => null,
        };

        /// <summary>
        /// How a call is judged: the omen first (its answer is right, and the
        /// answer it replaces drops to fair unless the stance holds it), then
        /// the stance, then the move itself. A timed-out call is always wrong.
        /// </summary>
        public static DuelVerdict Judge(DuelMove move, DuelOption option, DuelStance stance, DuelOmen omen)
        {
            if (option == DuelOption.None) return DuelVerdict.Wrong;
            if (omen.Move == move && omen.Answer == option) return DuelVerdict.Right;

            var verdict = StanceVerdict(stance, move, option) ?? BaseVerdict(move, option);
            bool stanceHeld = StanceVerdict(stance, move, option) == DuelVerdict.Right;
            if (omen.Move == move && verdict == DuelVerdict.Right && !stanceHeld) return DuelVerdict.Fair;
            return verdict;
        }

        public static double ValueOf(DuelVerdict verdict) => verdict switch
        {
            DuelVerdict.Right => 1.0,
            DuelVerdict.Fair => FairShare,
            _ => 0.0,
        };

        /// <summary>M for a set of judged calls; missing calls count as wrong.</summary>
        public static double Multiplier(IReadOnlyList<DuelVerdict> verdicts)
        {
            double sum = 0;
            int right = 0;
            foreach (var v in verdicts)
            {
                sum += ValueOf(v);
                if (v == DuelVerdict.Right) right++;
            }
            double m = Floor + PerRightCall * sum + (right == Calls ? FinisherBonus : 0.0);
            return Math.Clamp(m, Floor, Cap);
        }

        /// <summary>The sandbag detector's skill score for a duel: the share of right calls, 0..1.</summary>
        public static double Skill(IReadOnlyList<DuelVerdict> verdicts)
        {
            int right = 0;
            foreach (var v in verdicts) if (v == DuelVerdict.Right) right++;
            return right / (double)Calls;
        }

        /// <summary>
        /// The day's three moves: the omen's move always once, the rest drawn
        /// without repeats from the form's pool. The order is shuffled, so the
        /// omen's move is not always first.
        /// </summary>
        /// <remarks>
        /// Modul: ALWAYS, even outside the form's pool (found in the first
        /// screenshot run, 2026-11-15). Saturday's and Sunday's omens name
        /// serpent moves, and before the Black Serpent form those never came -
        /// so two days a week the omen said nothing. On those days the serpent
        /// stirs in him early: the omen's move comes anyway.
        /// </remarks>
        public static DuelMove[] DrawMoves(bool enraged, DuelOmen omen, Func<int, int> uniform)
        {
            var pool = new List<DuelMove>(enraged ? SerpentPool : NormalPool);
            var picked = new List<DuelMove>(Calls);
            if (Enum.IsDefined(omen.Move))
            {
                pool.Remove(omen.Move);
                picked.Add(omen.Move);
            }
            while (picked.Count < Calls && pool.Count > 0)
            {
                int i = uniform(pool.Count);
                picked.Add(pool[i]);
                pool.RemoveAt(i);
            }
            for (int i = picked.Count - 1; i > 0; i--)
            {
                int j = uniform(i + 1);
                (picked[i], picked[j]) = (picked[j], picked[i]);
            }
            return picked.ToArray();
        }

        /// <summary>
        /// The base hit B struck as Rounds rounds of BlowsPerRound blows. The
        /// blows of all rounds sum to exactly B, so the fight on screen adds up
        /// to the floor before any call - the numbers are the server's, never
        /// the client's.
        /// </summary>
        public static long[][] SplitBlows(long baseHit, Func<int, int> uniform)
        {
            int count = Rounds * BlowsPerRound;
            var weights = new int[count];
            long totalWeight = 0;
            for (int i = 0; i < count; i++)
            {
                weights[i] = 80 + uniform(41); // 0.8 .. 1.2
                totalWeight += weights[i];
            }
            var flat = new long[count];
            long given = 0;
            for (int i = 0; i < count; i++)
            {
                flat[i] = i == count - 1 ? baseHit - given : baseHit * weights[i] / totalWeight;
                given += flat[i];
            }
            var rounds = new long[Rounds][];
            for (int r = 0; r < Rounds; r++)
            {
                rounds[r] = new long[BlowsPerRound];
                Array.Copy(flat, r * BlowsPerRound, rounds[r], 0, BlowsPerRound);
            }
            return rounds;
        }

        /// <summary>The extra hit a judged call lands: its share of M, on the base hit.</summary>
        public static long CallHit(long baseHit, DuelVerdict verdict) => (long)Math.Round(baseHit * PerRightCall * ValueOf(verdict));

        /// <summary>The finisher, struck only when every call was right.</summary>
        public static long FinisherHit(long baseHit) => (long)Math.Round(baseHit * FinisherBonus);

        /// <summary>The right answer to a move for this stance and omen (the first, if two are right).</summary>
        public static DuelOption BestAnswer(DuelMove move, DuelStance stance, DuelOmen omen)
        {
            foreach (var o in new[] { DuelOption.Guard, DuelOption.Strike, DuelOption.Evade })
                if (Judge(move, o, stance, omen) == DuelVerdict.Right) return o;
            return DuelOption.Guard;
        }

        /// <summary>
        /// One line the result card shows for a call. A call that was not right
        /// names the answer that was - the duel is learnt by reading, and a card
        /// that only says "wrong" teaches nothing (first screenshot run).
        /// </summary>
        public static string Explain(DuelMove move, DuelOption option, DuelVerdict verdict, DuelStance stance, DuelOmen omen)
        {
            string name = Def(move).Name;
            string best = BestAnswer(move, stance, omen).ToString();
            if (option == DuelOption.None) return $"{name}: no answer in time - {best} was right.";
            if (omen.Move == move && omen.Answer == option) return $"{name}: {omen.Name} was with you.";
            bool stanceRight = StanceVerdict(stance, move, option) == DuelVerdict.Right;
            return verdict switch
            {
                DuelVerdict.Right when stanceRight => $"{name}: your {stance} build turned it.",
                DuelVerdict.Right => $"{name}: the right answer.",
                DuelVerdict.Fair => $"{name}: it held, but {best} was better.",
                _ => $"{name}: he punished it - {best} was right.",
            };
        }
    }
}
