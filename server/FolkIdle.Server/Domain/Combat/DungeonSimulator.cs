using System;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>One thing that happened in a dungeon run, for the playback and the feed.</summary>
    /// <summary>
    /// One thing that happened in a dungeon run, for the playback: when, what,
    /// and both bars right after it (percent), so the client can animate the
    /// fight between events without inferring health from text.
    /// </summary>
    public readonly record struct DungeonEvent(double AtSeconds, string Kind, string Text, int Value = 0,
        int PlayerHpPct = 0, int EnemyHpPct = 0, int ShieldPct = 0);

    public sealed record DungeonRunResult(
        bool Won,
        int Stage,
        int WavesCleared,
        bool ReachedBoss,
        int BossHpLeftPct,
        double Seconds,
        int MealsEaten,
        IReadOnlyList<DungeonEvent> Events,
        DungeonKind Kind = DungeonKind.Equipment,
        IReadOnlyList<int>? WaveMonsterIds = null,
        int PartySize = 1,
        int Dodged = 0,
        int Missed = 0);

    /// <summary>One of the boss's abilities as it stands at a stage, for the lobby.</summary>
    public readonly record struct DungeonAbilityView(string Id, string Name, int FromStage, string Line, bool Active);

    /// <summary>
    /// A dungeon run, simulated on the server (owner D1-D6,
    /// CHESTS_DUNGEONS_DIAMONDS.md 2.6a): five waves and the dungeon's boss
    /// (DungeonBoss: Baba Yaga, Koschei, Zmey), health
    /// carried from fight to fight, at most five meals from the larder.
    /// </summary>
    /// <remarks>
    /// Modul: THE LIVE COMBAT MODEL, NOT A COPY. The character's numbers are
    /// LiveCombatStats and EffectiveMaxMilliHpFor over a snapshot of the live
    /// payload, the blows are CombatDamageModel's expected swing and
    /// BossGearBenchmark's expected monster swing (the tick's own curve), the
    /// meal is FoodRegistry's heal on the tick's cooldown. Rolls are replaced by
    /// their expected values, as the boss-wall benchmark does, so a run is
    /// deterministic: the same character at the same stage always gets the
    /// same answer, the playback is honest, and a test cannot flake.
    ///
    /// Modul: THE MEAL CAP IS THE DIFFICULTY. Auto-eat heals a share of the
    /// bar, so a larder without limit beats any monster table - the lesson the
    /// Malakor calibration taught. Five meals across six fights makes healing a
    /// resource and the boss's hit size matter again.
    ///
    /// Modul: STAGES IN THREE BANDS. 1-5 are region N's boss (as farmed after
    /// its first clear) x1.1; 6-10 are Malakor's Ascension ladder A2-A10;
    /// 11-15 are A10 x1.5 and then x1.4 a stage on attack, health rising as
    /// attack^0.68 (the Ascension split). The absolute top is set against the
    /// maxed profile once task 112 re-measures it - the shape stays.
    /// </remarks>
    public static class DungeonSimulator
    {
        public const int StageCount = 15;
        public const int Waves = 5;
        public const int MealCap = 5;
        public const double WaveStrength = 0.4;
        internal const int TickMs = 100;
        internal const int MaxTicks = 30 * 60 * 1000 / TickMs; // half an hour of fight time
        internal const int MaxEvents = 400;

        /// <summary>
        /// Stage 10 is Malakor at Ascension 10 with attack times this and health
        /// times this^0.68; stages 6-9 step geometrically from stage 5 to it.
        /// MEASURED (2026-10-31, production copy, AscensionCalibrationHarness):
        /// the maxed profile wins stage 10 up to x0.256 using all five meals -
        /// the waves and the meal cap make a run far harder than the Ascension
        /// fight alone, where the same profile clears A7. 0.24 is "clears it
        /// with effort", the owner's target.
        /// </summary>
        public const double AscensionBandScale = 0.24;

        /// <summary>Calibration only (the harness's search); 0 = use AscensionBandScale.</summary>
        internal static double CalibrationScale;

        public static int RegionOfStage(int stage) => Math.Clamp(stage, 1, 5);

        /// <summary>The boss monster whose body the stage borrows (its region's boss, Malakor from 6).</summary>
        public static int BossMonsterIdFor(int stage) => RaceUnlockRegistry.GetRegionBossMonsterId(RegionOfStage(stage));

        /// <summary>The stage's boss health and attack power, before abilities.</summary>
        public static (long MilliHp, long AttackPower, bool Enrage) BossStrength(int stage)
        {
            stage = Math.Clamp(stage, 1, StageCount);
            int bossId = BossMonsterIdFor(stage);
            byte cleared = BossFirstClearRules.MarkDefeated(0, bossId);
            long hp = BossFirstClearRules.MaxHpFor(cleared, bossId) * 1000L;
            long attack = BossFirstClearRules.AttackPowerFor(cleared, bossId);
            if (stage <= 5)
            {
                return ((long)(hp * 1.1), (long)(attack * 1.1), false);
            }

            // Stage 10's target: Malakor at A10, scaled by the measured band scale.
            var top = BossAscensionRegistry.ModifiersFor(5, BossAscensionRegistry.MaxStep);
            double scale = CalibrationScale > 0 ? CalibrationScale : AscensionBandScale;
            double topAttack = BossAscensionRules.ScaleBossAttack(attack, in top) * scale;
            double topHp = BossAscensionRules.ScaleBossHp(hp, in top) * Math.Pow(scale, 0.68);
            double fiveAttack = attack * 1.1, fiveHp = hp * 1.1;
            // The Ascension step a stage stands for, for Enrage (from step 4).
            var modifiers = BossAscensionRegistry.ModifiersFor(5, stage <= 10 ? (stage - 5) * 2 : BossAscensionRegistry.MaxStep);
            if (stage <= 10)
            {
                // Geometric steps from stage 5 to stage 10, so every stage is
                // harder than the one before whatever the scale.
                double t = (stage - 5) / 5.0;
                attack = (long)(fiveAttack * Math.Pow(Math.Max(1.0, topAttack / fiveAttack), t));
                hp = (long)(fiveHp * Math.Pow(Math.Max(1.0, topHp / fiveHp), t));
            }
            else
            {
                double factor = 1.5 * Math.Pow(1.4, stage - 11);
                attack = (long)(Math.Max(topAttack, fiveAttack) * factor);
                hp = (long)(Math.Max(topHp, fiveHp) * Math.Pow(factor, 0.68));
            }
            return (hp, attack, modifiers.Enrage);
        }

        /// <summary>
        /// The monsters the five waves wear, for the playback: the ordinary
        /// monsters of the stage's region, in turn. Their numbers are the
        /// wave's (40% of the boss), not theirs - this is only whose face it is.
        /// </summary>
        public static IReadOnlyList<int> WaveMonsterIdsFor(int stage)
        {
            int region = RegionOfStage(stage);
            int boss = RaceUnlockRegistry.GetRegionBossMonsterId(region);
            var pool = new List<int>();
            for (int id = ContentRegistry.FirstCanonicalMonsterId; id <= ContentRegistry.Monsters.Length; id++)
                if (id != boss && ContentRegistry.GetMonsterRegionTier(id) == region) pool.Add(id);
            var waves = new int[Waves];
            for (int i = 0; i < Waves; i++) waves[i] = pool.Count > 0 ? pool[i % pool.Count] : boss;
            return waves;
        }

        /// <summary>The boss's abilities at a stage, for the lobby.</summary>
        public static IReadOnlyList<DungeonAbilityView> AbilitiesAt(DungeonKind kind, int stage) => DungeonBoss.For(kind).AbilitiesAt(stage);

        /// <summary>
        /// Runs the stage for the character in <paramref name="payload"/> (a
        /// snapshot of the live payload, the main character in the active
        /// register). <paramref name="foodItemId"/> and
        /// <paramref name="foodInLarder"/> are the larder's best food and how
        /// many of it there are; the run eats at most <see cref="MealCap"/>.
        /// No dodges: the whole run at once, as the calibration and the sweep use it.
        /// </summary>
        public static DungeonRunResult Run(TickStatePayload payload, int stage, int foodItemId, int foodInLarder)
            => Run(payload, DungeonKind.Equipment, stage, foodItemId, foodInLarder);

        public static DungeonRunResult Run(TickStatePayload payload, DungeonKind kind, int stage, int foodItemId, int foodInLarder)
        {
            var runner = new DungeonRunner(payload, kind, stage, foodItemId, foodInLarder, new[] { 0 }, challenges: false);
            runner.Advance();
            return runner.Result!;
        }

        internal static int Pct(double part, double whole) => whole <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(part * 100 / whole), 0, 100);
    }
}
