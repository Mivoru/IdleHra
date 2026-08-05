using System;
using FolkIdle.Server.Domain.Combat;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// How hard the player hits a given monster, in one place.
    ///
    /// THERE WERE THREE OF THESE AND THEY DISAGREED.
    ///
    ///   1. The live 10 Hz tick rolled per swing: hit chance from accuracy
    ///      against the monster's dodge, a crit roll, then
    ///      `max(1000, raw - armor * 1000)`, then the codex and set-bonus
    ///      multipliers.
    ///   2. The offline projection used `max(1000, effectiveMilliAttack)` -
    ///      the same attack power with NO ARMOUR SUBTRACTED AND NO HIT CHANCE.
    ///   3. The instant-warp estimate used none of the above: a hand-rolled
    ///      `15000 + ln(STR + 1) * 1000 + level * 750` that reads no equipment
    ///      at all, so affixes, set bonuses and the weapon itself were worth
    ///      nothing during a warp, and armour was again ignored.
    ///
    /// Armour is the expensive omission, because it is subtracted from every
    /// swing and scales with region: region 1 monsters carry 10 and region 5
    /// carry 50, against a bare-handed 15 damage. A level-1 character really
    /// deals 5.75 to a Field Mouse and the projections credited 15.75, so an
    /// hour banked offline paid what nearly three hours played would - and the
    /// gap widens with every region, in favour of never playing live.
    ///
    /// This is the single expected-value model. The live tick keeps rolling per
    /// swing (it should - variance is part of the moment), but rolls against the
    /// same armour rule; offline and warp ask for the expectation. A test pins
    /// the two together, because three models agreeing today is worth nothing if
    /// nothing keeps them agreeing.
    /// </summary>
    public static class CombatDamageModel
    {
        /// <summary>
        /// The chance a swing connects. Mirrors the live tick exactly: dodge
        /// and accuracy are both offsets on 100, and the result is clamped so
        /// neither perfect accuracy nor perfect evasion is reachable.
        /// </summary>
        public static float HitChance(in CombatStats stats, in MonsterDefinition monster)
        {
            float attackerAccuracy = 100f + stats.AccuracyRating;
            float defenderDodge = 100f + monster.DodgeRating;
            return Math.Clamp(attackerAccuracy / defenderDodge, 0.05f, 0.95f);
        }

        /// <summary>
        /// Net milli-damage of one CONNECTING swing at a given crit multiplier -
        /// armour subtraction, codex multiplier and the set fire bonus, in the
        /// live tick's order. The floor of 1000 is the live tick's: a hit always
        /// removes at least one whole hit point however heavy the armour.
        /// </summary>
        public static long NetMilliDamage(long rawMilliAttack, float critMultiplier, in CombatStats stats, in MonsterDefinition monster, float codexDamageMultiplier)
        {
            long raw = (long)(rawMilliAttack * critMultiplier);
            long net = Math.Max(1000L, raw - (monster.Armor * 1000L));

            if (codexDamageMultiplier > 0f)
            {
                net = (long)(net * codexDamageMultiplier);
            }

            if (stats.SetFireDamageMultiplierPct > 0f)
            {
                net = (long)(net * (1f + (stats.SetFireDamageMultiplierPct / 100f)));
            }

            return net;
        }

        /// <summary>
        /// Expected milli-damage per swing, averaged over the hit roll and the
        /// crit roll.
        ///
        /// The crit term is a two-point blend rather than "raw times an average
        /// multiplier" because armour is subtracted AFTER the crit multiplier -
        /// with heavy armour a normal hit can be on the 1000 floor while a crit
        /// is not, and averaging the multiplier first would quietly credit
        /// damage that no individual swing ever deals.
        /// </summary>
        public static double ExpectedMilliDamagePerSwing(in CombatStats stats, in MonsterDefinition monster, long rawMilliAttack, float codexDamageMultiplier)
        {
            float critChance = Math.Clamp(stats.CritChancePct / 100f, 0f, 1f);
            float critMultiplier = StatsCalculator.ComputeCritMultiplier(in stats);

            double normal = NetMilliDamage(rawMilliAttack, 1.0f, in stats, in monster, codexDamageMultiplier);
            double crit = NetMilliDamage(rawMilliAttack, critMultiplier, in stats, in monster, codexDamageMultiplier);

            double perConnectingSwing = ((1.0 - critChance) * normal) + (critChance * crit);
            return perConnectingSwing * HitChance(in stats, in monster);
        }

        /// <summary>The live tick's attack cadence, so nothing re-derives it.</summary>
        public static int AttackIntervalMs(in CombatStats stats)
        {
            int intervalMs = (int)(1500 * (1.0f - stats.AttackSpeedPct));
            return intervalMs < 200 ? 200 : intervalMs;
        }

        /// <summary>
        /// Expected seconds to kill one of these, at this character's stats.
        /// Returns <see cref="double.PositiveInfinity"/> when the character
        /// cannot damage the monster at all, which callers must treat as "no
        /// kills" rather than dividing by it.
        /// </summary>
        public static double ExpectedSecondsPerKill(in CombatStats stats, in MonsterDefinition monster, long rawMilliAttack, float codexDamageMultiplier)
        {
            double perSwing = ExpectedMilliDamagePerSwing(in stats, in monster, rawMilliAttack, codexDamageMultiplier);
            if (perSwing <= 0.0) return double.PositiveInfinity;

            double swingsPerSecond = 1000.0 / AttackIntervalMs(in stats);
            double milliDps = perSwing * swingsPerSecond;
            if (milliDps <= 0.0) return double.PositiveInfinity;

            long monsterMilliHp = (long)ContentRegistry.GetScaledMonsterMaxHp(monster.Id) * 1000L;
            return monsterMilliHp / milliDps;
        }
    }
}
