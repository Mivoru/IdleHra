using System;
using System.Collections.Generic;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>The three dungeons. The number is stored (DungeonProgress.Kind) and sent - never reorder.</summary>
    public enum DungeonKind
    {
        Equipment = 0,
        Xp = 1,
        Gold = 2,
    }

    /// <summary>
    /// The mutable state of one boss fight that the boss's rules read and
    /// change. Health is in milli-HP, time in seconds from the fight's start.
    /// </summary>
    public sealed class DungeonFightState
    {
        public int Stage;
        public double Now;
        public double Enemy;
        public double EnemyMax;
        /// <summary>Absorbs the player's blows before Enemy does (Bone Fence, Bone Ward).</summary>
        public double Shield;
        public double PlayerHp;
        public double PlayerMax;
        public int EnemySwings;
        /// <summary>Armour-ignoring damage the player takes each 100 ms tick until BurnUntil (Fire Breath).</summary>
        public double BurnPerTick;
        public double BurnUntil;
        /// <summary>The boss's own per-fight state; each boss class owns its shape.</summary>
        public object? BossState;
        public Action<string, string, int> Note = (_, _, _) => { };
    }

    /// <summary>
    /// One dungeon boss: its name, its line, and its four abilities as rules
    /// inside DungeonSimulator's loop. Waves never use these - only the boss.
    /// </summary>
    /// <remarks>
    /// Modul: EACH ABILITY IS ASKED FOR BY A BUILD (CHESTS_DUNGEONS_DIAMONDS.md
    /// 2.6a). Koschei rewards crit, Zmey speed and big hits, Baba Yaga accuracy
    /// and sustain - so the three dungeons ask different things of a character
    /// rather than three bigger health bars. Every number grows linearly from
    /// the ability's first stage to stage 15, unless its line says otherwise.
    /// A new rule goes here as a hook DungeonSimulator calls, never as a
    /// special case inside the loop.
    /// </remarks>
    public abstract class DungeonBoss
    {
        public abstract DungeonKind Kind { get; }
        public abstract string DungeonName { get; }
        public abstract string BossName { get; }
        public abstract string Intro { get; }
        public abstract string Fall { get; }
        public abstract IReadOnlyList<DungeonAbilityView> AbilitiesAt(int stage);

        /// <summary>Share of an average swing that lands on the boss: dodge, hide, a weak spot.</summary>
        internal virtual double PlayerHitFactor(int stage, in HuntingProjection.PlayerSwing swing) => 1.0;
        public virtual void Begin(DungeonFightState f) { }
        /// <summary>Timed abilities, once per 100 ms tick, before either side swings.</summary>
        public virtual void OnTime(DungeonFightState f) { }
        /// <summary>One landed swing, before the shield and the bar take it.</summary>
        public virtual double ModifyDealt(DungeonFightState f, double dealt) => dealt;
        public virtual void AfterPlayerSwing(DungeonFightState f) { }
        /// <summary>The bar reached zero. True when the boss gets up again.</summary>
        public virtual bool Survives(DungeonFightState f) => false;
        public virtual double EnemyIntervalFactor(DungeonFightState f) => 1.0;
        public virtual double EnemyHitFactor(DungeonFightState f) => 1.0;
        public virtual void AfterEnemySwing(DungeonFightState f, double dealtToPlayer) { }
        public virtual bool CanEat(DungeonFightState f) => true;

        internal static double Grow(int stage, int from, double start, double end)
            => stage < from ? 0 : start + (end - start) * (stage - from) / Math.Max(1, DungeonSimulator.StageCount - from);

        public static DungeonBoss For(DungeonKind kind) => kind switch
        {
            DungeonKind.Xp => Koschei.Instance,
            DungeonKind.Gold => Zmey.Instance,
            _ => BabaYaga.Instance,
        };

        public static readonly IReadOnlyList<DungeonKind> All = new[] { DungeonKind.Xp, DungeonKind.Gold, DungeonKind.Equipment };
    }

    /// <summary>Baba Yaga's Hut, the equipment dungeon - "she does not fight fair".</summary>
    public sealed class BabaYaga : DungeonBoss
    {
        public static readonly BabaYaga Instance = new();
        public override DungeonKind Kind => DungeonKind.Equipment;
        public override string DungeonName => "Baba Yaga's Hut";
        public override string BossName => "Baba Yaga";
        public override string Intro => "Baba Yaga flies in on her mortar.";
        public override string Fall => "Baba Yaga falls.";

        public static double MortarDodgePct(int stage) => Grow(stage, 1, 10, 25);
        public static double HexEverySeconds(int stage) => stage < 4 ? 0 : 15 - 5.0 * (stage - 4) / (DungeonSimulator.StageCount - 4);
        public static int HexHits(int stage) => stage < 4 ? 0 : (int)Math.Round(3 + 2.0 * (stage - 4) / (DungeonSimulator.StageCount - 4));
        public const double HexDamageCut = 0.4;
        public static double FencePct(int stage) => Grow(stage, 8, 15, 30);
        public static double SweepEverySeconds(int stage) => stage < 11 ? 0 : 30 - 10.0 * (stage - 11) / (DungeonSimulator.StageCount - 11);
        public static double SweepSeconds(int stage) => stage < 11 ? 0 : 6 + 4.0 * (stage - 11) / (DungeonSimulator.StageCount - 11);

        public override IReadOnlyList<DungeonAbilityView> AbilitiesAt(int stage) => new[]
        {
            new DungeonAbilityView("mortar", "Mortar Flight", 1, $"Dodges {MortarDodgePct(Math.Max(stage, 1)):0}% of your attacks.", stage >= 1),
            new DungeonAbilityView("hex", "Hex", 4,
                stage >= 4 ? $"Every {HexEverySeconds(stage):0} s your next {HexHits(stage)} hits deal {HexDamageCut * 100:0}% less." : "Your next hits deal 40% less.", stage >= 4),
            new DungeonAbilityView("fence", "Bone Fence", 8,
                stage >= 8 ? $"At half health a skull-lantern with {FencePct(stage):0}% of her health must fall before she can be hit." : "At half health a skull-lantern shields her.", stage >= 8),
            new DungeonAbilityView("sweep", "Broom Sweep", 11,
                stage >= 11 ? $"Every {SweepEverySeconds(stage):0} s no meal can be eaten for {SweepSeconds(stage):0} s." : "Sweeps the larder away for a while.", stage >= 11),
        };

        // Per-fight state lives on the fight (BossState), not here: the instance is shared.
        private sealed class State { public int HexLeft; public double NextHex, NextSweep, SweptUntil; public bool FenceRaised; }
        private static State S(DungeonFightState f) => (State)(f.BossState ??= new State());

        internal override double PlayerHitFactor(int stage, in HuntingProjection.PlayerSwing swing) => 1.0 - MortarDodgePct(stage) / 100.0;

        public override void Begin(DungeonFightState f)
        {
            var s = S(f);
            s.NextHex = HexEverySeconds(f.Stage) > 0 ? HexEverySeconds(f.Stage) : double.MaxValue;
            s.NextSweep = SweepEverySeconds(f.Stage) > 0 ? SweepEverySeconds(f.Stage) : double.MaxValue;
        }

        public override void OnTime(DungeonFightState f)
        {
            var s = S(f);
            if (f.Now >= s.NextHex)
            {
                s.HexLeft = HexHits(f.Stage);
                s.NextHex += HexEverySeconds(f.Stage);
                f.Note("hex", $"Hex - your next {s.HexLeft} hits are cursed.", s.HexLeft);
            }
            if (f.Now >= s.NextSweep)
            {
                s.SweptUntil = f.Now + SweepSeconds(f.Stage);
                s.NextSweep += SweepEverySeconds(f.Stage);
                f.Note("sweep", $"Broom Sweep - no meals for {SweepSeconds(f.Stage):0} s.", (int)SweepSeconds(f.Stage));
            }
        }

        public override double ModifyDealt(DungeonFightState f, double dealt)
        {
            var s = S(f);
            if (s.HexLeft <= 0) return dealt;
            s.HexLeft--;
            return dealt * (1.0 - HexDamageCut);
        }

        public override void AfterPlayerSwing(DungeonFightState f)
        {
            // Modul: the lantern rises ONCE, at half health, at every stage - the
            // second and third lanterns at the top wait for the tuning pass.
            var s = S(f);
            if (!s.FenceRaised && FencePct(f.Stage) > 0 && f.Enemy > 0 && f.Enemy <= f.EnemyMax * 0.5)
            {
                s.FenceRaised = true;
                f.Shield += f.EnemyMax * FencePct(f.Stage) / 100.0;
                f.Note("fence", $"Bone Fence - a skull-lantern with {FencePct(f.Stage):0}% of her health.", (int)FencePct(f.Stage));
            }
        }

        public override bool CanEat(DungeonFightState f) => f.Now >= S(f).SweptUntil;
    }

    /// <summary>Koschei's Tower, the XP dungeon - "his death is not here".</summary>
    public sealed class Koschei : DungeonBoss
    {
        public static readonly Koschei Instance = new();
        public override DungeonKind Kind => DungeonKind.Xp;
        public override string DungeonName => "Koschei's Tower";
        public override string BossName => "Koschei the Deathless";
        public override string Intro => "Koschei the Deathless rises from his throne of bone.";
        public override string Fall => "Koschei's needle snaps. He falls for good.";

        /// <summary>The health he rises with: 15% at stage 1, 40% from stage 10.</summary>
        public static double RisePct(int stage) => Math.Min(40, 15 + 25.0 * (Math.Max(1, stage) - 1) / 9);
        public static int Rises(int stage) => stage >= 13 ? 2 : 1;
        public static double WardEverySeconds(int stage) => stage < 4 ? 0 : 20 - 6.0 * (stage - 4) / (DungeonSimulator.StageCount - 4);
        public static double WardPct(int stage) => Grow(stage, 4, 5, 12);
        public static double DrainPct(int stage) => Grow(stage, 8, 10, 30);
        public static double NeedleCritBonusPct(int stage) => Grow(stage, 11, 25, 60);
        public static double NeedleNormalCutPct(int stage) => Grow(stage, 11, 20, 35);

        public override IReadOnlyList<DungeonAbilityView> AbilitiesAt(int stage) => new[]
        {
            new DungeonAbilityView("deathless", "Deathless", 1,
                $"The killing blow does not kill him: he rises with {RisePct(stage):0}% health{(Rises(stage) > 1 ? ", twice" : "")}.", stage >= 1),
            new DungeonAbilityView("ward", "Bone Ward", 4,
                stage >= 4 ? $"Every {WardEverySeconds(stage):0} s a bone shield absorbs {WardPct(stage):0.#}% of his health." : "A bone shield absorbs part of your blows.", stage >= 4),
            new DungeonAbilityView("drain", "Soul Drain", 8,
                stage >= 8 ? $"Heals for {DrainPct(stage):0}% of the damage he deals." : "Heals from the damage he deals.", stage >= 8),
            new DungeonAbilityView("needle", "The Needle", 11,
                stage >= 11 ? $"Crits deal {NeedleCritBonusPct(stage):0}% more to him, other hits {NeedleNormalCutPct(stage):0}% less." : "His weakness: crits hurt him more, other hits less.", stage >= 11),
        };

        private sealed class State { public int RisesLeft; public double NextWard; }
        private static State S(DungeonFightState f) => (State)(f.BossState ??= new State());

        internal override double PlayerHitFactor(int stage, in HuntingProjection.PlayerSwing swing)
        {
            if (stage < 11 || swing.Mean <= 0) return 1.0;
            double normal = swing.PNormal * swing.NormalDamage;
            double crits = swing.PCrit * swing.CritDamage + swing.PDouble * swing.DoubleDamage;
            double total = normal + crits;
            if (total <= 0) return 1.0;
            return (normal * (1 - NeedleNormalCutPct(stage) / 100.0) + crits * (1 + NeedleCritBonusPct(stage) / 100.0)) / total;
        }

        public override void Begin(DungeonFightState f)
        {
            var s = S(f);
            s.RisesLeft = Rises(f.Stage);
            s.NextWard = WardEverySeconds(f.Stage) > 0 ? WardEverySeconds(f.Stage) : double.MaxValue;
        }

        public override void OnTime(DungeonFightState f)
        {
            var s = S(f);
            if (f.Now < s.NextWard) return;
            s.NextWard += WardEverySeconds(f.Stage);
            f.Shield += f.EnemyMax * WardPct(f.Stage) / 100.0;
            f.Note("ward", $"Bone Ward - a shield of {WardPct(f.Stage):0.#}% of his health.", (int)Math.Round(WardPct(f.Stage)));
        }

        public override bool Survives(DungeonFightState f)
        {
            var s = S(f);
            if (s.RisesLeft <= 0) return false;
            s.RisesLeft--;
            f.Enemy = f.EnemyMax * RisePct(f.Stage) / 100.0;
            f.Note("deathless", $"Deathless - his death is not here. He rises with {RisePct(f.Stage):0}% health.", (int)RisePct(f.Stage));
            return true;
        }

        public override void AfterEnemySwing(DungeonFightState f, double dealtToPlayer)
        {
            if (DrainPct(f.Stage) <= 0 || dealtToPlayer <= 0) return;
            f.Enemy = Math.Min(f.EnemyMax, f.Enemy + dealtToPlayer * DrainPct(f.Stage) / 100.0);
        }
    }

    /// <summary>Zmey's Hoard, the gold dungeon - three heads, three tempers.</summary>
    public sealed class Zmey : DungeonBoss
    {
        public static readonly Zmey Instance = new();
        public override DungeonKind Kind => DungeonKind.Gold;
        public override string DungeonName => "Zmey's Hoard";
        public override string BossName => "Zmey Gorynych";
        public override string Intro => "Zmey Gorynych uncoils from the hoard, three heads hissing.";
        public override string Fall => "The last head falls. The hoard is yours.";

        public const int Heads = 3;
        /// <summary>How much faster the heads left swing, per head lost.</summary>
        public static double HeadsFasterPct(int stage) => Grow(stage, 1, 15, 30);
        public static int BreathEverySwings(int stage) => stage < 4 ? 0 : stage < 10 ? 6 : 5;
        public static double BreathPctPerSecond(int stage) => Grow(stage, 4, 3, 6);
        public const double BreathSeconds = 3;
        public static double RegrowSeconds(int stage) => stage < 8 ? 0 : 20 - 10.0 * (stage - 8) / (DungeonSimulator.StageCount - 8);
        public static double HidePct(int stage) => Grow(stage, 11, 20, 40);

        public override IReadOnlyList<DungeonAbilityView> AbilitiesAt(int stage) => new[]
        {
            new DungeonAbilityView("heads", "Three Heads", 1,
                $"Three bars, one per head. Each head lost cuts his damage by a third; the heads left swing {HeadsFasterPct(Math.Max(stage, 1)):0}% faster.", stage >= 1),
            new DungeonAbilityView("breath", "Fire Breath", 4,
                stage >= 4 ? $"Every {BreathEverySwings(stage)}th swing burns for {BreathPctPerSecond(stage):0.#}% of your health a second for {BreathSeconds:0} s, armour ignored." : "Every few swings a breath burns through armour.", stage >= 4),
            new DungeonAbilityView("regrowth", "Regrowth", 8,
                stage >= 8 ? $"A severed head grows back after {RegrowSeconds(stage):0} s unless the next head falls first." : "A severed head grows back unless the next falls quickly.", stage >= 8),
            new DungeonAbilityView("hide", "Gold Hide", 11,
                stage >= 11 ? $"Takes {HidePct(stage):0}% less from every hit." : "His golden hide turns part of every blow.", stage >= 11),
        };

        private sealed class State { public int HeadsAlive; public double RegrowAt; }
        private static State S(DungeonFightState f) => (State)(f.BossState ??= new State());

        private static int HeadsFor(DungeonFightState f)
            => Math.Clamp((int)Math.Ceiling(f.Enemy / (f.EnemyMax / Heads) - 1e-9), 0, Heads);

        internal override double PlayerHitFactor(int stage, in HuntingProjection.PlayerSwing swing) => 1.0 - HidePct(stage) / 100.0;

        public override void Begin(DungeonFightState f)
        {
            var s = S(f);
            s.HeadsAlive = Heads;
            s.RegrowAt = double.MaxValue;
        }

        public override void OnTime(DungeonFightState f)
        {
            var s = S(f);
            if (f.Now < s.RegrowAt || s.HeadsAlive >= Heads || f.Enemy <= 0) return;
            // Modul: the stump grows back a WHOLE head, so the bar can rise past
            // where the last blow left it - the speed check of this dungeon.
            f.Enemy = Math.Min(f.EnemyMax, f.Enemy + f.EnemyMax / Heads);
            s.HeadsAlive = HeadsFor(f);
            s.RegrowAt = double.MaxValue;
            f.Note("regrowth", $"Regrowth - a head grows back. {s.HeadsAlive} heads.", s.HeadsAlive);
        }

        public override void AfterPlayerSwing(DungeonFightState f)
        {
            var s = S(f);
            int now = f.Enemy <= 0 ? 0 : HeadsFor(f);
            if (now >= s.HeadsAlive) return;
            s.HeadsAlive = now;
            if (now > 0)
            {
                f.Note("head", $"A head falls - {now} left, and they swing faster.", now);
                s.RegrowAt = RegrowSeconds(f.Stage) > 0 ? f.Now + RegrowSeconds(f.Stage) : double.MaxValue;
            }
        }

        public override double EnemyIntervalFactor(DungeonFightState f)
            => 1.0 / (1.0 + HeadsFasterPct(f.Stage) / 100.0 * (Heads - Math.Max(1, S(f).HeadsAlive)));

        public override double EnemyHitFactor(DungeonFightState f) => Math.Max(1, S(f).HeadsAlive) / (double)Heads;

        public override void AfterEnemySwing(DungeonFightState f, double dealtToPlayer)
        {
            int every = BreathEverySwings(f.Stage);
            if (every <= 0 || f.EnemySwings % every != 0) return;
            f.BurnPerTick = f.PlayerMax * BreathPctPerSecond(f.Stage) / 100.0 / 10.0;
            f.BurnUntil = f.Now + BreathSeconds;
            f.Note("breath", $"Fire Breath - burning for {BreathSeconds:0} s.", (int)Math.Round(BreathPctPerSecond(f.Stage)));
        }
    }
}
