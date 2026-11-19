using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>One depth of a Nav run, as the screen lists it.</summary>
    public readonly record struct NavDepthResult(int Depth, string Guardian, string Elite, double Seconds, bool Cleared, string Outcome, long Souls);

    /// <summary>A whole run: how deep, what it paid, and why it stopped.</summary>
    public sealed record NavRunResult(int Circle, int StartDepth, int DeepestCleared, long Souls, string EndedBy, double Seconds, int RevivesUsed, IReadOnlyList<NavDepthResult> Depths)
    {
        /// <summary>The guardian that ended the run, empty if the run hit its limit.</summary>
        public string StoppedBy { get; init; } = string.Empty;

        /// <summary>What would move the record past it (NavRules.Hint).</summary>
        public string Hint { get; init; } = string.Empty;
    }

    /// <summary>What the Workshop and the research add inside Nav, already summed.</summary>
    public readonly record struct NavBonuses(double DamagePct, double HealthPct, double AttackSpeedPct, double CritDamagePct, double SoulsPct, int StartDepth, int Revives);

    /// <summary>
    /// Plays one Descent from the live payload: depth after depth until a
    /// guardian outlasts the clock or the character falls.
    /// </summary>
    /// <remarks>
    /// Modul: THE PROJECTION'S SWING, NOT A NEW DAMAGE MODEL. Each guardian is
    /// fought with HuntingProjection.PlayerSwing - the swing the offline
    /// catch-up and the dungeon use, parity-tested against the live tick
    /// (crit, double hit, the War tree, the codex, burn) - and hits back with
    /// BossGearBenchmark's expected blow through the character's armour and
    /// block. A third damage model is how this codebase has disagreed with
    /// itself before.
    ///
    /// Modul: EXPECTATIONS, NOT DICE. A depth is decided by its expected time to
    /// kill against its expected time to die, so the same character reaches the
    /// same depth - what varies a run is the elite modifiers the seed lays down.
    /// A record should move because the character got stronger, not because a
    /// crit roll went well.
    /// </remarks>
    public static class NavSimulator
    {
        public static NavRunResult Run(TickStatePayload payload, int slot, int circle, NavBonuses bonuses, int runSeed, int recordDepth)
        {
            var p = payload;
            SimulationEngine.SwapSlotIntoActiveRegister(ref p, slot);
            p.KillStreak = 0;
            CombatStats stats = SimulationEngine.LiveCombatStats(in p);
            long maxMilliHp = Math.Max(1000L, SimulationEngine.EffectiveMaxMilliHpFor(in p, in stats));
            double navMaxHp = maxMilliHp * (1.0 + bonuses.HealthPct / 100.0);
            long rawMilliAttack = SimulationEngine.EffectiveMilliAttackFor(ref p, in stats, SimulationEngine.LineageOf(in p).DamageScalePerLevelPct);
            int intervalMs = Math.Max(200, SimulationEngine.LiveAttackIntervalMs(in p, in stats));
            double navIntervalMs = Math.Max(200.0, intervalMs / (1.0 + bonuses.AttackSpeedPct / 100.0));

            int start = Math.Clamp(1 + bonuses.StartDepth, 1, Math.Max(1, recordDepth / 2));
            int revives = Math.Max(0, bonuses.Revives);
            int revivesUsed = 0;
            var depths = new List<NavDepthResult>();
            long souls = 0;
            double seconds = 0;
            int deepest = start - 1;
            string endedBy = "limit";
            string stoppedBy = string.Empty;
            string hint = string.Empty;

            for (int depth = start; depth < start + NavRules.MaxDepthPerRun; depth++)
            {
                var elite = NavRules.EliteFor(runSeed, depth);
                var (hpF, atkF, speedF) = NavRules.EliteFactors(elite);
                var who = NavRules.GuardianFor(depth);
                var ability = who.Ability;
                if (ability == NavRules.Ability.Warden) { hpF *= NavRules.WardenHp; atkF *= NavRules.WardenAttack; }
                if (ability == NavRules.Ability.SecondHeart) hpF *= 1.0 + NavRules.SecondHeartPct / 100.0;

                // Malakor's armour and dodge: the depth decides the numbers,
                // the ability decides the fight.
                MonsterDefinition guardian = ContentRegistry.Monsters[GuardianStatTemplateId - 1];
                double guardianMilliHp = NavRules.GuardianHp(circle, depth) * hpF * 1000.0;
                long guardianAttack = (long)Math.Min(long.MaxValue / 4, NavRules.GuardianAttack(circle, depth) * atkF);

                var swing = HuntingProjection.PlayerSwing.For(in p, in stats, in guardian, rawMilliAttack, guardianMilliHp);
                double grave = 1.0 + bonuses.CritDamagePct / 100.0;
                double normalHit = swing.NormalDamage * (ability == NavRules.Ability.IronHide ? NavRules.IronHidePlainFactor : 1.0);
                double critHit = ability == NavRules.Ability.Hex ? swing.NormalDamage : swing.CritDamage * grave;
                double doubleHit = ability == NavRules.Ability.Hex ? swing.NormalDamage * 2.0 : swing.DoubleDamage * grave;
                double meanPerSwing = (swing.PNormal * normalHit) + (swing.PCrit * critHit) + (swing.PDouble * doubleHit);
                meanPerSwing *= 1.0 + bonuses.DamagePct / 100.0;
                if (ability == NavRules.Ability.Elusive) meanPerSwing *= 1.0 - NavRules.ElusiveMissShare;

                double swingMs = navIntervalMs / (ability == NavRules.Ability.GraveChill ? NavRules.GraveChillSpeedFactor : 1.0);
                double dps = meanPerSwing * 1000.0 / swingMs;
                // Lifedrinker: the guardian's own blows mend it, so damage per
                // second is what the regeneration leaves over.
                if (ability == NavRules.Ability.Lifedrinker)
                    dps -= guardianMilliHp * (NavRules.LifedrinkerPct / 100.0) * speedF * 1000.0 / NavRules.GuardianIntervalMs;
                double timeToKill = dps <= 0 ? double.PositiveInfinity : guardianMilliHp / dps;

                double fightMaxHp = navMaxHp * (ability == NavRules.Ability.Nightmare ? NavRules.NightmareHealthFactor : 1.0);
                double incomingPerSwing = BossGearBenchmark.ExpectedBossMilliDamagePerSwing(in stats, in guardian, guardianAttack);
                double incomingPerSecond = incomingPerSwing * speedF * 1000.0 / NavRules.GuardianIntervalMs;
                double healPerSwing = swing.ExpectedHeal((long)(navMaxHp / 100.0));
                if (ability == NavRules.Ability.Elusive) healPerSwing *= 1.0 - NavRules.ElusiveMissShare;
                if (ability == NavRules.Ability.Drowning) healPerSwing *= NavRules.DrowningHealFactor;
                double healPerSecond = healPerSwing * 1000.0 / swingMs;
                if (ability == NavRules.Ability.Mirror)
                {
                    double pLand = swing.PNormal + swing.PCrit + swing.PDouble;
                    incomingPerSecond += pLand * navMaxHp * (NavRules.MirrorPctOfHealth / 100.0) * 1000.0 / swingMs;
                }
                double timeToDie = ability == NavRules.Ability.NoonHeat
                    ? TimeToDieRamping(fightMaxHp, incomingPerSecond, healPerSecond, NavRules.NoonHeatPerBlow * speedF * 1000.0 / NavRules.GuardianIntervalMs)
                    : (incomingPerSecond - healPerSecond <= 0 ? double.PositiveInfinity : fightMaxHp / (incomingPerSecond - healPerSecond));

                bool outOfTime = timeToKill > NavRules.SecondsPerGuardian;
                bool falls = timeToDie < Math.Min(timeToKill, NavRules.SecondsPerGuardian);

                if (falls && revives > 0 && !outOfTime)
                {
                    // Second breath: the character stands back up once, on a full bar.
                    revives--;
                    revivesUsed++;
                    falls = timeToDie * 2 < timeToKill;
                }

                if (outOfTime || falls)
                {
                    double spent = Math.Min(NavRules.SecondsPerGuardian, falls ? timeToDie : NavRules.SecondsPerGuardian);
                    seconds += spent;
                    endedBy = falls ? "fell" : "time";
                    stoppedBy = who.Key;
                    hint = NavRules.Hint(ability, falls);
                    depths.Add(new NavDepthResult(depth, who.Key, NavRules.EliteName(elite), Math.Round(spent, 1), false,
                        falls ? $"{who.Name} struck you down." : $"{who.Name} outlasted the clock.", 0));
                    break;
                }

                long paid = NavRules.SoulsForDepth(circle, depth, bonuses.SoulsPct);
                souls += paid;
                seconds += timeToKill;
                deepest = depth;
                depths.Add(new NavDepthResult(depth, who.Key, NavRules.EliteName(elite), Math.Round(timeToKill, 1), true, string.Empty, paid));
            }

            return new NavRunResult(circle, start, deepest, souls, endedBy, Math.Round(seconds, 1), revivesUsed, depths)
            {
                StoppedBy = stoppedBy,
                Hint = hint,
            };
        }

        /// <summary>
        /// The depth a character should reach, before a charge is spent: the
        /// median of five seeds, so one lucky elite roll does not set it.
        /// </summary>
        public static int ExpectedDepth(TickStatePayload payload, int slot, int circle, NavBonuses bonuses, int recordDepth)
        {
            var depths = new int[5];
            for (int i = 0; i < depths.Length; i++) depths[i] = Run(payload, slot, circle, bonuses, 1000 + i, recordDepth).DeepestCleared;
            Array.Sort(depths);
            return depths[depths.Length / 2];
        }

        /// <summary>Malakor (115): the armour and dodge every guardian fights with.</summary>
        public const int GuardianStatTemplateId = 115;

        /// <summary>
        /// Seconds until the bar is empty when the blows grow linearly: damage
        /// taken by t is in*t + in*ramp*t^2/2, minus heal*t. Solved for t.
        /// </summary>
        internal static double TimeToDieRamping(double maxHp, double incomingPerSecond, double healPerSecond, double rampPerSecond)
        {
            double a = incomingPerSecond * rampPerSecond / 2.0;
            double b = incomingPerSecond - healPerSecond;
            if (a <= 0) return b <= 0 ? double.PositiveInfinity : maxHp / b;
            double disc = (b * b) + (4.0 * a * maxHp);
            return (-b + Math.Sqrt(disc)) / (2.0 * a);
        }
    }
}
