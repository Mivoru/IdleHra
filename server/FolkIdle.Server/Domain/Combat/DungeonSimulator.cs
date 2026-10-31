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
        IReadOnlyList<int>? WaveMonsterIds = null);

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
        private const int TickMs = 100;
        private const int MaxTicks = 30 * 60 * 1000 / TickMs; // half an hour of fight time
        private const int MaxEvents = 80;

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
        /// </summary>
        public static DungeonRunResult Run(TickStatePayload payload, int stage, int foodItemId, int foodInLarder)
            => Run(payload, DungeonKind.Equipment, stage, foodItemId, foodInLarder);

        public static DungeonRunResult Run(TickStatePayload payload, DungeonKind kind, int stage, int foodItemId, int foodInLarder)
        {
            stage = Math.Clamp(stage, 1, StageCount);
            DungeonBoss rules = DungeonBoss.For(kind);
            CombatStats stats = SimulationEngine.LiveCombatStats(in payload);
            long maxMilliHp = Math.Max(1000L, SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats));
            // The tick's own attack (aptitudes, inheritance, the lineage's scale) -
            // computed here, not read from the cache, so a payload loaded from the
            // database (the calibration harness) gets the same number as a live one.
            long rawMilliAttack = SimulationEngine.EffectiveMilliAttackFor(ref payload, in stats, SimulationEngine.LineageOf(in payload).DamageScalePerLevelPct);

            MonsterDefinition boss = ContentRegistry.Monsters[BossMonsterIdFor(stage) - 1];
            var (bossMilliHp, bossAttack, enrage) = BossStrength(stage);

            // Modul: THE OFFLINE PROJECTION'S SWING, which is parity-tested against
            // the live tick: crit and double hit, Crushing Blow, Know Your Prey,
            // the codex, set fire and burn, Riposte. The first cut used the plain
            // CombatDamageModel swing the boss-wall benchmark uses for reference
            // gear, and measured on the owner's character it dealt about a
            // twentieth of the live damage - the reference model has no skill
            // tree and no codex, which a real character's damage mostly is.
            var swing = HuntingProjection.PlayerSwing.For(in payload, in stats, in boss, rawMilliAttack);
            double bossHitFactor = rules.PlayerHitFactor(stage, in swing);
            int playerIntervalMs = Math.Max(TickMs, SimulationEngine.LiveAttackIntervalMs(in payload, in stats));
            int bossIntervalMs = boss.AttackIntervalMs > 0 ? boss.AttackIntervalMs : 2000;

            int healPerBite = foodItemId > 0 && foodInLarder > 0 ? FoodRegistry.GetHealMilliHp(foodItemId, maxMilliHp) : 0;
            int mealsAvailable = Math.Min(MealCap, Math.Max(0, foodInLarder));
            int thresholdPct = payload.AutoEatThreshold > 0 ? payload.AutoEatThreshold : Shared.AutoEatDefaults.ThresholdPct;
            double eatBelow = maxMilliHp * (thresholdPct / 100.0);

            var events = new List<DungeonEvent>();
            var f = new DungeonFightState { Stage = stage, PlayerMax = maxMilliHp, PlayerHp = maxMilliHp };
            int tick = 0, meals = 0, eatCooldown = 0, wavesCleared = 0, bossHpLeftPct = 100;
            void Note(string kindOfEvent, string text, int value = 0)
            {
                if (events.Count >= MaxEvents) return;
                events.Add(new DungeonEvent(tick * TickMs / 1000.0, kindOfEvent, text, value,
                    Pct(f.PlayerHp, f.PlayerMax), Pct(f.Enemy, f.EnemyMax), Pct(f.Shield, f.EnemyMax)));
            }
            f.Note = (_, text, v) => Note("ability", text, v);

            // Six fights: five waves at WaveStrength, then the boss.
            for (int fight = 0; fight <= Waves; fight++)
            {
                bool isBoss = fight == Waves;
                f.EnemyMax = isBoss ? bossMilliHp : bossMilliHp * WaveStrength;
                f.Enemy = f.EnemyMax;
                f.Shield = 0;
                f.EnemySwings = 0;
                f.BurnPerTick = 0;
                f.BurnUntil = 0;
                f.BossState = null;
                f.Now = 0;
                double enemyHit = BossGearBenchmark.ExpectedBossMilliDamagePerSwing(in stats, in boss, isBoss ? bossAttack : (long)(bossAttack * WaveStrength));
                double playerHit = swing.Mean * (isBoss ? bossHitFactor : 1.0);
                double healPerSwing = swing.ExpectedHeal(maxMilliHp / 100) * (isBoss ? bossHitFactor : 1.0);
                if (isBoss) rules.Begin(f);
                Note(isBoss ? "boss" : "wave", isBoss ? rules.Intro : $"Wave {fight + 1} of {Waves}.", fight + 1);

                int playerAcc = 0;
                double enemyAcc = 0;
                int fightStart = tick;

                while (true)
                {
                    tick++;
                    if (tick > MaxTicks)
                    {
                        Note("timeout", "The fight drags on past half an hour - the run is lost.");
                        return Finish(false);
                    }
                    f.Now = (tick - fightStart) * TickMs / 1000.0;
                    if (isBoss) rules.OnTime(f);

                    playerAcc += TickMs;
                    if (playerAcc >= playerIntervalMs)
                    {
                        playerAcc -= playerIntervalMs;
                        double dealt = isBoss ? rules.ModifyDealt(f, playerHit) : playerHit;
                        if (f.Shield > 0)
                        {
                            f.Shield -= dealt;
                            if (f.Shield <= 0)
                            {
                                f.Shield = 0;
                                Note("ability", "The shield shatters.");
                            }
                        }
                        else
                        {
                            f.Enemy -= dealt;
                        }
                        if (healPerSwing > 0) f.PlayerHp = Math.Min(maxMilliHp, f.PlayerHp + healPerSwing * (dealt / Math.Max(1.0, playerHit)));
                        if (isBoss) rules.AfterPlayerSwing(f);
                    }

                    if (f.Enemy <= 0 && !(isBoss && rules.Survives(f))) break;

                    enemyAcc += TickMs;
                    double interval = bossIntervalMs * (isBoss ? rules.EnemyIntervalFactor(f) : 1.0);
                    if (enemyAcc >= interval)
                    {
                        enemyAcc -= interval;
                        f.EnemySwings++;
                        bool enraged = isBoss && enrage && f.Enemy * 100.0 < f.EnemyMax * AscensionModifiers.EnrageBelowPct;
                        double hit = (enraged ? enemyHit * AscensionModifiers.EnrageAttackPct / 100.0 : enemyHit) * (isBoss ? rules.EnemyHitFactor(f) : 1.0);
                        f.PlayerHp -= hit;
                        if (isBoss) rules.AfterEnemySwing(f, hit);
                    }
                    if (f.BurnPerTick > 0 && f.Now < f.BurnUntil) f.PlayerHp -= f.BurnPerTick;

                    if (f.PlayerHp <= 0)
                    {
                        f.PlayerHp = 0;
                        bossHpLeftPct = isBoss ? Math.Max(1, (int)Math.Ceiling(f.Enemy * 100 / f.EnemyMax)) : 100;
                        Note("defeat", isBoss
                            ? $"Defeated by {rules.BossName} at {bossHpLeftPct}% of the boss's health."
                            : $"Defeated in wave {fight + 1}.");
                        return Finish(false);
                    }

                    if (eatCooldown > 0) eatCooldown--;
                    else if (f.PlayerHp <= eatBelow && healPerBite > 0 && meals < mealsAvailable && (!isBoss || rules.CanEat(f)))
                    {
                        f.PlayerHp = Math.Min(maxMilliHp, f.PlayerHp + healPerBite);
                        meals++;
                        eatCooldown = SimulationEngine.AutoEatCooldownTicks;
                        Note("meal", meals == mealsAvailable ? "The last meal of the run." : $"A meal ({meals} of {mealsAvailable}).", meals);
                    }
                }

                f.Enemy = 0;
                if (!isBoss)
                {
                    wavesCleared++;
                    int left = (int)Math.Ceiling(f.PlayerHp * 100 / maxMilliHp);
                    Note("cleared", $"Wave {fight + 1} cleared, {left}% health left.", left);
                }
            }

            Note("victory", rules.Fall);
            bossHpLeftPct = 0;
            return Finish(true);

            DungeonRunResult Finish(bool won) => new(won, stage, wavesCleared, wavesCleared >= Waves, won ? 0 : bossHpLeftPct,
                tick * TickMs / 1000.0, meals, events, kind, WaveMonsterIdsFor(stage));
        }

        private static int Pct(double part, double whole) => whole <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(part * 100 / whole), 0, 100);
    }
}
