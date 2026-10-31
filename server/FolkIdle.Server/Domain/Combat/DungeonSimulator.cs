using System;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>One thing that happened in a dungeon run, for the playback and the feed.</summary>
    public readonly record struct DungeonEvent(double AtSeconds, string Kind, string Text, int Value = 0);

    public sealed record DungeonRunResult(
        bool Won,
        int Stage,
        int WavesCleared,
        bool ReachedBoss,
        int BossHpLeftPct,
        double Seconds,
        int MealsEaten,
        IReadOnlyList<DungeonEvent> Events);

    /// <summary>One of the boss's abilities as it stands at a stage, for the lobby.</summary>
    public readonly record struct DungeonAbilityView(string Id, string Name, int FromStage, string Line, bool Active);

    /// <summary>
    /// The equipment dungeon's run, simulated on the server (owner D1-D6,
    /// CHESTS_DUNGEONS_DIAMONDS.md 2.6a): five waves and Baba Yaga, health
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

            int step = stage <= 10 ? (stage - 5) * 2 : BossAscensionRegistry.MaxStep;
            var modifiers = BossAscensionRegistry.ModifiersFor(5, step);
            hp = BossAscensionRules.ScaleBossHp(hp, in modifiers);
            attack = BossAscensionRules.ScaleBossAttack(attack, in modifiers);
            if (stage > 10)
            {
                double factor = 1.5 * Math.Pow(1.4, stage - 11);
                attack = (long)(attack * factor);
                hp = (long)(hp * Math.Pow(factor, 0.68));
            }
            return (hp, attack, modifiers.Enrage);
        }

        // ---------------------------------------------------------------
        // Baba Yaga's four abilities - each a rule in the loop below, with
        // its numbers growing linearly from its first stage to stage 15.
        // ---------------------------------------------------------------

        private static double Grow(int stage, int from, double start, double end)
            => stage < from ? 0 : start + (end - start) * (stage - from) / Math.Max(1, StageCount - from);

        public static double MortarDodgePct(int stage) => Grow(stage, 1, 10, 25);
        public static double HexEverySeconds(int stage) => stage < 4 ? 0 : 15 - 5.0 * (stage - 4) / (StageCount - 4);
        public static int HexHits(int stage) => stage < 4 ? 0 : (int)Math.Round(3 + 2.0 * (stage - 4) / (StageCount - 4));
        public const double HexDamageCut = 0.4;
        public static double FencePct(int stage) => Grow(stage, 8, 15, 30);
        public static double SweepEverySeconds(int stage) => stage < 11 ? 0 : 30 - 10.0 * (stage - 11) / (StageCount - 11);
        public static double SweepSeconds(int stage) => stage < 11 ? 0 : 6 + 4.0 * (stage - 11) / (StageCount - 11);

        public static IReadOnlyList<DungeonAbilityView> AbilitiesAt(int stage) => new[]
        {
            new DungeonAbilityView("mortar", "Mortar Flight", 1, $"Dodges {MortarDodgePct(Math.Max(stage, 1)):0}% of your attacks.", stage >= 1),
            new DungeonAbilityView("hex", "Hex", 4,
                stage >= 4 ? $"Every {HexEverySeconds(stage):0} s your next {HexHits(stage)} hits deal {HexDamageCut * 100:0}% less." : "Your next hits deal 40% less.", stage >= 4),
            new DungeonAbilityView("fence", "Bone Fence", 8,
                stage >= 8 ? $"At half health a skull-lantern with {FencePct(stage):0}% of her health must fall before she can be hit." : "At half health a skull-lantern shields her.", stage >= 8),
            new DungeonAbilityView("sweep", "Broom Sweep", 11,
                stage >= 11 ? $"Every {SweepEverySeconds(stage):0} s no meal can be eaten for {SweepSeconds(stage):0} s." : "Sweeps the larder away for a while.", stage >= 11),
        };

        /// <summary>
        /// Runs the stage for the character in <paramref name="payload"/> (a
        /// snapshot of the live payload, the main character in the active
        /// register). <paramref name="foodItemId"/> and
        /// <paramref name="foodInLarder"/> are the larder's best food and how
        /// many of it there are; the run eats at most <see cref="MealCap"/>.
        /// </summary>
        public static DungeonRunResult Run(TickStatePayload payload, int stage, int foodItemId, int foodInLarder)
        {
            stage = Math.Clamp(stage, 1, StageCount);
            CombatStats stats = SimulationEngine.LiveCombatStats(in payload);
            long maxMilliHp = Math.Max(1000L, SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats));
            long rawMilliAttack = payload.CachedEffectiveMilliAttack > 0
                ? payload.CachedEffectiveMilliAttack
                : StatsCalculator.ComputeEffectiveMilliAttack(in stats, damageScalePerLevelPct: 0, level: Math.Max(1, payload.CurrentLevel));

            MonsterDefinition boss = ContentRegistry.Monsters[BossMonsterIdFor(stage) - 1];
            var (bossMilliHp, bossAttack, enrage) = BossStrength(stage);

            double playerHit = CombatDamageModel.ExpectedMilliDamagePerSwing(in stats, in boss, rawMilliAttack, codexDamageMultiplier: 1f)
                * (1.0 - MortarDodgePct(stage) / 100.0);
            int playerIntervalMs = Math.Max(TickMs, CombatDamageModel.AttackIntervalMs(in stats));
            int bossIntervalMs = boss.AttackIntervalMs > 0 ? boss.AttackIntervalMs : 2000;

            int healPerBite = foodItemId > 0 && foodInLarder > 0 ? FoodRegistry.GetHealMilliHp(foodItemId, maxMilliHp) : 0;
            int mealsAvailable = Math.Min(MealCap, Math.Max(0, foodInLarder));
            int thresholdPct = payload.AutoEatThreshold > 0 ? payload.AutoEatThreshold : Shared.AutoEatDefaults.ThresholdPct;
            double eatBelow = maxMilliHp * (thresholdPct / 100.0);

            var events = new List<DungeonEvent>();
            void Note(int tick, string kind, string text, int value = 0)
            {
                if (events.Count < MaxEvents) events.Add(new DungeonEvent(tick * TickMs / 1000.0, kind, text, value));
            }

            double hp = maxMilliHp;
            int tick = 0, meals = 0, eatCooldown = 0, wavesCleared = 0, bossHpLeftPct = 100;

            // Six fights: five waves at WaveStrength, then the boss.
            for (int fight = 0; fight <= Waves; fight++)
            {
                bool isBoss = fight == Waves;
                double enemyMax = isBoss ? bossMilliHp : bossMilliHp * WaveStrength;
                double enemy = enemyMax;
                double enemyHit = BossGearBenchmark.ExpectedBossMilliDamagePerSwing(in stats, in boss, isBoss ? bossAttack : (long)(bossAttack * WaveStrength));
                Note(tick, isBoss ? "boss" : "wave", isBoss ? "Baba Yaga flies in on her mortar." : $"Wave {fight + 1} of {Waves}.", fight + 1);

                int playerAcc = 0, enemyAcc = 0;
                double fence = 0;
                bool fenceRaised = false;
                int hexLeft = 0;
                double nextHex = isBoss && HexEverySeconds(stage) > 0 ? HexEverySeconds(stage) : double.MaxValue;
                double nextSweep = isBoss && SweepEverySeconds(stage) > 0 ? SweepEverySeconds(stage) : double.MaxValue;
                double sweptUntil = 0;
                int fightStart = tick;

                while (true)
                {
                    tick++;
                    if (tick > MaxTicks)
                    {
                        Note(tick, "timeout", "The fight drags on past half an hour - the run is lost.");
                        return Finish(false);
                    }
                    double now = (tick - fightStart) * TickMs / 1000.0;

                    if (isBoss && now >= nextHex)
                    {
                        hexLeft = HexHits(stage);
                        nextHex += HexEverySeconds(stage);
                        Note(tick, "ability", $"Hex - your next {hexLeft} hits are cursed.", hexLeft);
                    }
                    if (isBoss && now >= nextSweep)
                    {
                        sweptUntil = now + SweepSeconds(stage);
                        nextSweep += SweepEverySeconds(stage);
                        Note(tick, "ability", $"Broom Sweep - no meals for {SweepSeconds(stage):0} s.");
                    }

                    playerAcc += TickMs;
                    if (playerAcc >= playerIntervalMs)
                    {
                        playerAcc -= playerIntervalMs;
                        double dealt = playerHit;
                        if (hexLeft > 0) { dealt *= 1.0 - HexDamageCut; hexLeft--; }
                        if (fence > 0)
                        {
                            fence -= dealt;
                            if (fence <= 0) Note(tick, "ability", "The skull-lantern shatters.");
                        }
                        else
                        {
                            enemy -= dealt;
                        }
                        if (stats.LifestealPct > 0f) hp = Math.Min(maxMilliHp, hp + dealt * (stats.LifestealPct / 100f));

                        if (isBoss && !fenceRaised && FencePct(stage) > 0 && enemy > 0 && enemy <= enemyMax * 0.5)
                        {
                            fenceRaised = true;
                            fence = enemyMax * FencePct(stage) / 100.0;
                            Note(tick, "ability", $"Bone Fence - a skull-lantern with {FencePct(stage):0}% of her health.");
                        }
                    }

                    if (enemy <= 0) break;

                    enemyAcc += TickMs;
                    if (enemyAcc >= bossIntervalMs)
                    {
                        enemyAcc -= bossIntervalMs;
                        bool enraged = isBoss && enrage && enemy * 100.0 < enemyMax * AscensionModifiers.EnrageBelowPct;
                        hp -= enraged ? enemyHit * AscensionModifiers.EnrageAttackPct / 100.0 : enemyHit;
                    }

                    if (hp <= 0)
                    {
                        Note(tick, "defeat", isBoss
                            ? $"Defeated by Baba Yaga at {Math.Max(0, (int)Math.Ceiling(enemy * 100 / enemyMax))}% of her health."
                            : $"Defeated in wave {fight + 1}.");
                        bossHpLeftPct = isBoss ? Math.Max(1, (int)Math.Ceiling(enemy * 100 / enemyMax)) : 100;
                        return Finish(false);
                    }

                    if (eatCooldown > 0) eatCooldown--;
                    else if (hp <= eatBelow && healPerBite > 0 && meals < mealsAvailable && now >= sweptUntil)
                    {
                        hp = Math.Min(maxMilliHp, hp + healPerBite);
                        meals++;
                        eatCooldown = SimulationEngine.AutoEatCooldownTicks;
                        Note(tick, "meal", meals == mealsAvailable ? "The last meal of the run." : $"A meal ({meals} of {mealsAvailable}).", meals);
                    }
                }

                if (!isBoss)
                {
                    wavesCleared++;
                    Note(tick, "cleared", $"Wave {fight + 1} cleared, {(int)Math.Ceiling(hp * 100 / maxMilliHp)}% health left.", (int)Math.Ceiling(hp * 100 / maxMilliHp));
                }
            }

            Note(tick, "victory", "Baba Yaga falls.");
            bossHpLeftPct = 0;
            return Finish(true);

            DungeonRunResult Finish(bool won) => new(won, stage, wavesCleared, wavesCleared >= Waves, won ? 0 : bossHpLeftPct,
                tick * TickMs / 1000.0, meals, events);
        }

    }
}
