using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>One depth of a Nav run, as the screen lists it.</summary>
    public readonly record struct NavDepthResult(int Depth, int GuardianId, string Elite, double Seconds, bool Cleared, string Outcome, long Souls);

    /// <summary>A whole run: how deep, what it paid, and why it stopped.</summary>
    public sealed record NavRunResult(int Circle, int StartDepth, int DeepestCleared, long Souls, string EndedBy, double Seconds, int RevivesUsed, IReadOnlyList<NavDepthResult> Depths);

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

            for (int depth = start; depth < start + NavRules.MaxDepthPerRun; depth++)
            {
                var elite = NavRules.EliteFor(runSeed, depth);
                var (hpF, atkF, speedF) = NavRules.EliteFactors(elite);
                int templateId = NavRules.GuardianTemplateId(depth);
                MonsterDefinition guardian = ContentRegistry.Monsters[templateId - 1];
                double guardianMilliHp = NavRules.GuardianHp(circle, depth) * hpF * 1000.0;
                long guardianAttack = (long)Math.Min(long.MaxValue / 4, NavRules.GuardianAttack(circle, depth) * atkF);

                var swing = HuntingProjection.PlayerSwing.For(in p, in stats, in guardian, rawMilliAttack, guardianMilliHp);
                double grave = 1.0 + bonuses.CritDamagePct / 100.0;
                double meanPerSwing = (swing.PNormal * swing.NormalDamage) + (swing.PCrit * swing.CritDamage * grave) + (swing.PDouble * swing.DoubleDamage * grave);
                meanPerSwing *= 1.0 + bonuses.DamagePct / 100.0;

                double timeToKill = meanPerSwing <= 0 ? double.PositiveInfinity : guardianMilliHp / meanPerSwing * navIntervalMs / 1000.0;

                double incomingPerSwing = BossGearBenchmark.ExpectedBossMilliDamagePerSwing(in stats, in guardian, guardianAttack);
                double incomingPerSecond = incomingPerSwing * speedF * 1000.0 / NavRules.GuardianIntervalMs;
                double healPerSecond = swing.ExpectedHeal((long)(navMaxHp / 100.0)) * 1000.0 / navIntervalMs;
                double net = incomingPerSecond - healPerSecond;
                double timeToDie = net <= 0 ? double.PositiveInfinity : navMaxHp / net;

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
                    depths.Add(new NavDepthResult(depth, templateId, NavRules.EliteName(elite), Math.Round(spent, 1), false,
                        falls ? "The guardian struck you down." : "The guardian outlasted the clock.", 0));
                    break;
                }

                long paid = NavRules.SoulsForDepth(circle, depth, bonuses.SoulsPct);
                souls += paid;
                seconds += timeToKill;
                deepest = depth;
                depths.Add(new NavDepthResult(depth, templateId, NavRules.EliteName(elite), Math.Round(timeToKill, 1), true, string.Empty, paid));
            }

            return new NavRunResult(circle, start, deepest, souls, endedBy, Math.Round(seconds, 1), revivesUsed, depths);
        }
    }
}
