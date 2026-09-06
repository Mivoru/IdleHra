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
        /// ARMOUR REDUCES, IT DOES NOT SUBTRACT.
        ///
        /// This was `max(1 hp, raw - armour)` in four separate places, and flat
        /// subtraction has a shape nobody wants: damage falls off a cliff at
        /// armour == attack and does nothing at all past it. The same monster
        /// was harmless to a player in best-in-slot gear and lethal to one three
        /// pieces behind, so every balance number in the game had to be tuned
        /// for the geared case and simply hoped for on everyone else. It also
        /// forced monster attack to be derived from the armour table rather than
        /// from anything about the player, because a hit below the armour it
        /// faced did nothing.
        ///
        /// The curve is `raw * K / (K + armour)`: never zero, never full, and
        /// smooth. K is the armour value that halves damage, so it says what
        /// "well armoured" MEANS at this point in the game - and it has to come
        /// from the content, not from the defender, or armour would cancel out
        /// of its own formula and stop being a stat at all.
        ///
        /// Both constants below are keyed to the monster's region, which is
        /// available at every one of the four call sites.
        /// </summary>
        /// <param name="penetration">
        /// ARMOUR PENETRATION, WHICH DID NOTHING UNTIL 2026-09-06.
        ///
        /// `FlatArmorPenetration` was granted by Might, rolled as the
        /// `armor_pen_flat` affix and summed into CombatStats - and this method,
        /// the only place armour is ever applied, never took it. The live
        /// account is carrying 1,122 of it on its weapon for no effect
        /// whatever. A stat that is granted, rolled, displayed and never read is
        /// this codebase's most expensive recurring defect, and this is the
        /// third one found in a day.
        ///
        /// IT RAISES THE HALVING CONSTANT rather than subtracting from armour,
        /// which matters because the two are on wildly different scales:
        /// monster armour runs 1 to 62, while one affix rolls above a thousand.
        /// Subtracting would let a single roll erase every monster's armour in
        /// the game and turn a stat into a switch.
        ///
        /// `K + pen` instead means penetration has smoothly diminishing returns
        /// and a hard natural ceiling - it can approach "ignore armour" and can
        /// never do better than that, so it is worth stacking without ever being
        /// worth stacking exclusively. At region 5 (K=150) against Malakor's 62
        /// armour: no penetration lets 71% through, 500 lets 91% through, and
        /// 5,000 lets 99% - a 1.4x span across the whole possible range.
        /// </param>
        public static long Mitigate(long rawMilliDamage, int armour, int halvingConstant, int penetration = 0)
        {
            if (rawMilliDamage <= 0L) return 0L;
            if (armour <= 0 || halvingConstant <= 0) return rawMilliDamage;

            if (penetration > 0) halvingConstant += penetration;

            long reduced = (long)((double)rawMilliDamage * halvingConstant / (halvingConstant + (double)armour));

            // The live tick's floor, kept: a swing that connects always removes
            // at least one whole hit point, however heavy the armour.
            return reduced < 1000L ? 1000L : reduced;
        }

        /// <summary>
        /// What counts as heavy armour ON A MONSTER at this region. Authored
        /// monster armour runs 10 per region tier, so three times that puts a
        /// region-appropriate monster at a 25 percent reduction: enough that
        /// armour is worth reading on a monster card, far from enough to make
        /// one unkillable.
        /// </summary>
        public static int MonsterArmourHalvingConstant(int regionTier)
        {
            int tier = regionTier < 1 ? 1 : regionTier;
            return 30 * tier;
        }

        /// <summary>
        /// What counts as heavy armour ON A PLAYER fighting in this region: the
        /// best armour the region itself authors, which is 40 and triples per
        /// region. A player in best-in-slot gear takes half damage, one at half
        /// that gear takes two thirds, one who has over-geared takes a third.
        ///
        /// That spread is the entire point. Under subtraction the same three
        /// players took nothing, everything, and nothing.
        /// </summary>
        public static int PlayerArmourHalvingConstant(int regionTier)
        {
            int tier = regionTier < 1 ? 1 : regionTier;
            int constant = 40;
            for (int i = 1; i < tier; i++) constant *= 3;
            return constant;
        }

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
            long net = Mitigate(raw, monster.Armor, MonsterArmourHalvingConstant(monster.RegionTier),
                stats.FlatArmorPenetration);

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
        /// <summary>
        /// ATTACK SPEED WAS READ AS A FRACTION AND WRITTEN AS A PERCENT.
        ///
        /// Every other percentage stat on CombatStats is consumed by dividing
        /// by 100 - crit chance, block strength, lifesteal. This one was not:
        /// it was `1500 * (1 - AttackSpeedPct)` directly, so a value shaped like
        /// "11 percent" meant eleven HUNDRED percent.
        ///
        /// StatsCalculator feeds it from two places and both are percent-
        /// shaped. DEX contributes `dex * 0.05` against a documented "+0.05%
        /// Attack Speed per point", and affix totals arrive in tenths of a
        /// percent divided by ten. So a character passed DEX 20 - which is
        /// level ten or so - and the interval went negative and slammed into
        /// the 200 ms floor.
        ///
        /// EVERY PLAYER PAST ABOUT LEVEL TEN WAS ATTACKING AT 200 ms INSTEAD OF
        /// 1500. Seven and a half times the intended rate, for the whole game.
        /// It is the answer to a live report of reaching level 49 in an hour
        /// with 86,000 gold, of bosses in every region dying "like butter" to
        /// starting gear, and of monsters whose health bar never appeared to
        /// move - they were dying inside a single swing.
        ///
        /// It also silently invalidated every pacing figure ever measured here,
        /// because the model computes with DEX 0 and therefore never left
        /// 1500 ms while real characters never stayed there.
        ///
        /// Read as a percentage now, like its siblings, and CAPPED: attack
        /// speed is multiplicative with everything else a player stacks, so
        /// without a ceiling the affixes alone reach the floor again. Sixty
        /// percent is a real, large investment - it nearly triples damage over
        /// time - and it cannot become an eighth of a second.
        /// </summary>
        public const float MaxAttackSpeedReduction = 0.60f;

        public static int AttackIntervalMs(in CombatStats stats)
        {
            float reduction = Math.Clamp(stats.AttackSpeedPct / 100f, 0f, MaxAttackSpeedReduction);
            // Rounded rather than truncated: 1500 * (1 - 0.6) lands a hair
            // under 600 in float, and a cap that reports 599 invites someone to
            // "fix" the constant.
            int intervalMs = (int)Math.Round(1500.0 * (1.0 - reduction));
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

            int intervalMs = AttackIntervalMs(in stats);
            double swingsPerSecond = 1000.0 / intervalMs;
            double milliDps = perSwing * swingsPerSecond;
            if (milliDps <= 0.0) return double.PositiveInfinity;

            long monsterMilliHp = (long)ContentRegistry.GetScaledMonsterMaxHp(monster.Id) * 1000L;

            // Modul: THE FIRST SWING AT A NEW MONSTER COSTS A FULL INTERVAL.
            //
            // The live tick zeroes CombatTargetTickAccumulator when a monster
            // dies and the next one spawns, so the swing timer restarts and the
            // player waits the whole interval before landing anything. This
            // projection modelled continuous swinging and therefore ran fast by
            // exactly one interval per kill.
            //
            // That was invisible while a kill took minutes and is not now: with
            // fights tuned to seconds it is a sixth of the fight, and an offline
            // projection that pays better than the live tick is the precise
            // failure the single damage model was built to end.
            return (monsterMilliHp / milliDps) + (intervalMs / 1000.0);
        }
    }
}
