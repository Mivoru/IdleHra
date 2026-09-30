using System;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>What one monster is worth to this character, per the advisor.</summary>
    public readonly struct HuntingEstimate
    {
        public int MonsterId { get; init; }

        /// <summary>False when no swing can hurt it - every other field is then 0.</summary>
        public bool CanDamage { get; init; }

        /// <summary>The expected seconds a kill takes, and an 80% band around it.</summary>
        public double SecondsPerKill { get; init; }
        public double SecondsPerKillLow { get; init; }
        public double SecondsPerKillHigh { get; init; }

        public long XpPerHour { get; init; }
        public long GoldPerHour { get; init; }

        /// <summary>
        /// Survives an hour of back-to-back fights, eating from the larder the
        /// character actually holds. A death ends the activity on the live
        /// tick, so this is the difference between farming and a death card.
        /// </summary>
        public bool SurvivesWithFood { get; init; }

        /// <summary>The same hour with an empty larder.</summary>
        public bool SurvivesWithoutFood { get; init; }

        /// <summary>How many kills an empty larder lasts, when it does not last the hour.</summary>
        public int KillsBeforeDeathWithoutFood { get; init; }

        /// <summary>Bites the larder spends in that hour (0 when no food is needed).</summary>
        public double FoodPerHour { get; init; }
    }

    /// <summary>
    /// Task 78: the hunting advisor. For one monster, what this character's
    /// kills take, pay and cost, projected from the live payload.
    /// </summary>
    /// <remarks>
    /// Modul: A TICK SIMULATION OF EXPECTED VALUES, NOT A THIRD DAMAGE MODEL.
    /// Every figure comes from the functions the live tick itself calls -
    /// SimulationEngine.LiveCombatStats, EffectiveMaxMilliHpFor,
    /// EffectiveMilliAttackFor, LiveAttackIntervalMs, LiveCritChancePct,
    /// LiveCritMultiplier, LiveKillXpMultiplierPct, HasCrossedInterval;
    /// CombatDamageModel.Mitigate and HitChance; BossFirstClearRules;
    /// FoodRegistry; CombatGoldReward. The swing timing, the order inside a
    /// tick (player, monster, auto-eat, death, kill) and the reset of the swing
    /// clock on respawn are RunCombatTick's. Each roll is replaced by its
    /// expectation, so the answer is deterministic. HuntingProjectionTests
    /// runs the REAL RunCombatTick against it and fails if they drift.
    ///
    /// Left out, each on purpose: the food buff's regen and Death Ward (both
    /// timed consumables - an estimate is about standing gear), Last Stand
    /// (once an hour), and Thunderer (the first boss swing only). All of them
    /// help the player, so the estimate errs toward caution.
    /// </remarks>
    public static class HuntingProjection
    {
        private const int TickMs = 100;
        public const int HorizonTicks = 60 * 60 * 1000 / TickMs;

        /// <summary>z for an 80% two-sided band.</summary>
        private const double BandZ = 1.2816;

        public static HuntingEstimate Project(
            in TickStatePayload payload, int monsterId, int globalXpMultiplier, int activeGlobalEventId)
        {
            if (monsterId < 1 || monsterId > ContentRegistry.Monsters.Length)
            {
                return new HuntingEstimate { MonsterId = monsterId };
            }

            MonsterDefinition monster = ContentRegistry.Monsters[monsterId - 1];
            TickStatePayload copy = payload;
            CombatStats stats = SimulationEngine.LiveCombatStats(in copy);
            long maxMilliHp = SimulationEngine.EffectiveMaxMilliHpFor(in copy, in stats);
            long rawMilliAttack = SimulationEngine.EffectiveMilliAttackFor(
                ref copy, in stats, SimulationEngine.LineageOf(in copy).DamageScalePerLevelPct);

            var swing = PlayerSwing.For(in copy, in stats, in monster, rawMilliAttack);
            if (swing.Mean <= 0.0)
            {
                return new HuntingEstimate { MonsterId = monsterId };
            }

            long monsterMilliHp = BossFirstClearRules.MaxHpFor(copy.DefeatedRegionBossMask, monsterId, copy.Skill_FirstBlood) * 1000L;
            int playerIntervalMs = SimulationEngine.LiveAttackIntervalMs(in copy, in stats);

            var withFood = Simulate(in copy, in stats, in monster, swing, playerIntervalMs, monsterMilliHp, maxMilliHp, withFood: true);
            var noFood = Simulate(in copy, in stats, in monster, swing, playerIntervalMs, monsterMilliHp, maxMilliHp, withFood: false);

            // Seconds per kill: the cycle the simulation measured, and a band
            // from the swing count's spread (hit, crit and Double Strike rolls).
            // Swings to kill ~ HP/mean with standard deviation sqrt(n)*sd/mean.
            double swings = monsterMilliHp / swing.Mean;
            double swingSd = Math.Sqrt(Math.Max(1.0, swings)) * swing.StdDev / swing.Mean;
            double lowSwings = Math.Max(1.0, Math.Ceiling(swings - (BandZ * swingSd)));
            double highSwings = Math.Max(lowSwings, Math.Ceiling(swings + (BandZ * swingSd)));
            double secondsPerKill = withFood.Kills > 0
                ? withFood.TicksFought * TickMs / 1000.0 / withFood.Kills
                : Math.Ceiling(swings) * playerIntervalMs / 1000.0;

            double killsPerHour = 3600.0 / secondsPerKill;

            long xpPerKill = XpPerKill(in copy, in monster, globalXpMultiplier, activeGlobalEventId);
            long goldPerKill = CombatGoldReward.PerKill(in copy, in monster, stats.GoldAcquisitionMultiplierPct);

            return new HuntingEstimate
            {
                MonsterId = monsterId,
                CanDamage = true,
                SecondsPerKill = secondsPerKill,
                SecondsPerKillLow = Math.Min(secondsPerKill, lowSwings * playerIntervalMs / 1000.0),
                SecondsPerKillHigh = Math.Max(secondsPerKill, highSwings * playerIntervalMs / 1000.0),
                XpPerHour = (long)(killsPerHour * xpPerKill),
                GoldPerHour = (long)(killsPerHour * goldPerKill),
                SurvivesWithFood = !withFood.Died,
                SurvivesWithoutFood = !noFood.Died,
                KillsBeforeDeathWithoutFood = noFood.Died ? noFood.Kills : 0,
                FoodPerHour = withFood.Died
                    ? withFood.Bites * (3600.0 / Math.Max(1.0, withFood.TicksFought * TickMs / 1000.0))
                    : withFood.Bites,
            };
        }

        /// <summary>
        /// XP one kill pays: the live multiplier, then ProcessMonsterDeath's own
        /// two terms (Blood Moon's +15, the mentorship penalty's 80%).
        /// </summary>
        private static long XpPerKill(in TickStatePayload payload, in MonsterDefinition monster, int globalXpMultiplier, int activeGlobalEventId)
        {
            int multiplier = SimulationEngine.LiveKillXpMultiplierPct(in payload, globalXpMultiplier);
            if (activeGlobalEventId == 2) multiplier += 15;
            long xp = (long)monster.BaseXpReward * multiplier / 100;
            if (payload.XpPenaltyExpiresEpoch > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                xp = (long)(xp * 0.8f);
            }
            return xp;
        }

        internal readonly struct SimResult
        {
            public int Kills { get; init; }
            public int TicksFought { get; init; }
            public bool Died { get; init; }
            public int Bites { get; init; }
        }

        /// <summary>
        /// One hour of back-to-back fights from a full bar, in RunCombatTick's
        /// order, with each roll replaced by its expectation.
        /// </summary>
        internal static SimResult Simulate(
            in TickStatePayload payload, in CombatStats stats, in MonsterDefinition monster, PlayerSwing swing,
            int playerIntervalMs, long monsterMaxMilliHp, long maxMilliHp, bool withFood)
        {
            double monsterSwing = SimulationEngine.ExpectedMonsterMilliDamagePerSwing(in stats, monster.Id, payload.DefeatedRegionBossMask, maxMilliHp);
            double thornsPerSwing = stats.SetThornsReflectionActive
                ? monsterSwing * SimulationEngine.ThornsReflectionFraction
                : 0.0;

            // The larder the character holds, best heal first, as auto-eat picks.
            Span<(int heal, int count)> larder = stackalloc (int, int)[3];
            larder[0] = (withFood ? FoodRegistry.GetHealMilliHp(payload.Food1_ItemId, maxMilliHp) : 0, payload.Food1_Count);
            larder[1] = (withFood ? FoodRegistry.GetHealMilliHp(payload.Food2_ItemId, maxMilliHp) : 0, payload.Food2_Count);
            larder[2] = (withFood ? FoodRegistry.GetHealMilliHp(payload.Food3_ItemId, maxMilliHp) : 0, payload.Food3_Count);
            double eatAt = (payload.AutoEatThreshold / 100.0f) * maxMilliHp;

            double playerHp = maxMilliHp;
            double monsterHp = monsterMaxMilliHp;
            int accumulator = 0;
            int eatCooldown = 0;
            int kills = 0;
            int bites = 0;
            long lifestealCeiling = maxMilliHp / 100;

            for (int tick = 1; tick <= HorizonTicks; tick++)
            {
                accumulator++;

                if (SimulationEngine.HasCrossedInterval(accumulator, playerIntervalMs))
                {
                    monsterHp -= swing.Mean;
                    playerHp = Math.Min(maxMilliHp, playerHp + swing.ExpectedHeal(lifestealCeiling));
                }

                if (monsterHp > 0 && SimulationEngine.HasCrossedInterval(accumulator, monster.AttackIntervalMs))
                {
                    playerHp -= monsterSwing;
                    monsterHp -= thornsPerSwing;
                }

                if (eatCooldown > 0)
                {
                    eatCooldown--;
                }
                else if (playerHp > 0 && playerHp <= eatAt)
                {
                    int best = -1;
                    for (int i = 0; i < 3; i++)
                    {
                        if (larder[i].count > 0 && larder[i].heal > 0 && (best < 0 || larder[i].heal > larder[best].heal)) best = i;
                    }
                    if (best >= 0)
                    {
                        larder[best].count--;
                        playerHp = Math.Min(maxMilliHp, playerHp + larder[best].heal);
                        eatCooldown = SimulationEngine.AutoEatCooldownTicks;
                        bites++;
                    }
                }

                if (playerHp <= 0)
                {
                    return new SimResult { Kills = kills, TicksFought = tick, Died = true, Bites = bites };
                }

                if (monsterHp <= 0)
                {
                    kills++;
                    monsterHp = monsterMaxMilliHp;
                    accumulator = 0;
                }
            }

            return new SimResult { Kills = kills, TicksFought = HorizonTicks, Died = false, Bites = bites };
        }

        /// <summary>
        /// One player swing as a distribution over its four outcomes - miss,
        /// hit, crit, doubled crit - each landed through the live armour, codex
        /// and set-fire steps, plus burn.
        /// </summary>
        internal readonly struct PlayerSwing
        {
            public double Mean { get; init; }
            public double StdDev { get; init; }

            // Per-outcome landed damage and probability, for the heal terms,
            // which are capped per hit and so cannot be taken off the mean.
            private readonly double _pHit, _pCrit, _pDouble;
            private readonly double _normal, _crit, _double;
            private readonly float _lifestealPct;
            private readonly long _bloodthirstTenths;
            // Landed damage includes burn; the heals are on the hit before it.
            private readonly double _burnFactor;

            private PlayerSwing(double pHit, double pCrit, double pDouble, double normal, double crit, double dbl, float lifestealPct, long bloodthirstTenths, double burnFactor)
            {
                _burnFactor = burnFactor;
                _pHit = pHit; _pCrit = pCrit; _pDouble = pDouble;
                _normal = normal; _crit = crit; _double = dbl;
                _lifestealPct = lifestealPct; _bloodthirstTenths = bloodthirstTenths;

                double pN = pHit * (1 - pCrit);
                double pC = pHit * pCrit * (1 - pDouble);
                double pD = pHit * pCrit * pDouble;
                double mean = (pN * normal) + (pC * crit) + (pD * dbl);
                double second = (pN * normal * normal) + (pC * crit * crit) + (pD * dbl * dbl);
                Mean = mean;
                StdDev = Math.Sqrt(Math.Max(0.0, second - (mean * mean)));
            }

            public static PlayerSwing For(in TickStatePayload payload, in CombatStats stats, in MonsterDefinition monster, long rawMilliAttack)
            {
                double pHit = CombatDamageModel.HitChance(in stats, in monster);
                double pCrit = SimulationEngine.LiveCritChancePct(in payload, in stats) / 100.0;
                float critMult = SimulationEngine.LiveCritMultiplier(in payload, in stats);
                double pDouble = payload.Skill_DoubleStrike > 0
                    ? Math.Clamp(SkillTreeRegistry.GetBonusPercent(SkillTreeRegistry.CrownDoubleStrike, payload.Skill_DoubleStrike) / 100.0, 0.0, 1.0)
                    : 0.0;

                double normal = Landed(in payload, in stats, in monster, rawMilliAttack, 1.0f);
                double crit = Landed(in payload, in stats, in monster, rawMilliAttack, critMult);
                double dbl = Landed(in payload, in stats, in monster, rawMilliAttack, critMult * 2f);

                long bloodthirst = payload.Skill_Bloodthirst > 0
                    ? (long)SkillTreeRegistry.GetBonusTenthsOfPercent(SkillTreeRegistry.BoughBloodthirst, payload.Skill_Bloodthirst)
                    : 0L;
                double burnFactor = stats.SetBurnApplicationActive ? 1.0 + SimulationEngine.BurnDamageFraction : 1.0;
                return new PlayerSwing(pHit, pCrit, pDouble, normal, crit, dbl, stats.LifestealPct, bloodthirst, burnFactor);
            }

            /// <summary>Landed damage of one connecting swing, burn included.</summary>
            private static double Landed(in TickStatePayload payload, in CombatStats stats, in MonsterDefinition monster, long rawMilliAttack, float critMult)
            {
                int raw = (int)(rawMilliAttack * critMult);
                int net = (int)CombatDamageModel.Mitigate(
                    raw, monster.Armor, CombatDamageModel.MonsterArmourHalvingConstant(monster.RegionTier), stats.FlatArmorPenetration);
                net = (int)(net * payload.CachedCodexDamageMultiplier);
                if (stats.SetFireDamageMultiplierPct > 0f)
                {
                    net = (int)(net * (1f + (stats.SetFireDamageMultiplierPct / 100f)));
                }
                int burn = stats.SetBurnApplicationActive ? (int)(net * SimulationEngine.BurnDamageFraction) : 0;
                return net + burn;
            }

            /// <summary>
            /// Health one swing gives back on average: Bloodthirst on the hit,
            /// lifesteal capped at 1% of the bar per hit - both on the post-armour
            /// figure before burn, as the tick applies them.
            /// </summary>
            public double ExpectedHeal(long lifestealCeiling)
            {
                if (_lifestealPct <= 0f && _bloodthirstTenths <= 0) return 0.0;
                double pN = _pHit * (1 - _pCrit);
                double pC = _pHit * _pCrit * (1 - _pDouble);
                double pD = _pHit * _pCrit * _pDouble;
                return (pN * HealOn(_normal, lifestealCeiling)) + (pC * HealOn(_crit, lifestealCeiling)) + (pD * HealOn(_double, lifestealCeiling));
            }

            private double HealOn(double landed, long lifestealCeiling)
            {
                double hit = landed / _burnFactor;
                double heal = 0.0;
                if (_bloodthirstTenths > 0) heal += hit * _bloodthirstTenths / 1000.0;
                if (_lifestealPct > 0f) heal += Math.Min(lifestealCeiling, hit * (_lifestealPct / 100.0));
                return heal;
            }        }
    }
}
