using System;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Shared;

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
    /// kills take, pay and cost, projected from the live payload. Also the
    /// fight the OFFLINE projection runs (OfflineSimulationEngine.ProjectCombat):
    /// one expected-value model of RunCombatTick, two callers.
    /// </summary>
    /// <remarks>
    /// Modul: A TICK SIMULATION OF EXPECTED VALUES, NOT A THIRD DAMAGE MODEL.
    /// Every figure comes from the functions the live tick itself calls -
    /// SimulationEngine.LiveCombatStats, EffectiveMaxMilliHpFor,
    /// EffectiveMilliAttackFor, LiveAttackIntervalMs, LiveCritChancePct,
    /// LiveCritMultiplier, LiveKillXpMultiplierPct, HasCrossedInterval,
    /// ExpectedMonsterMilliDamagePerSwing; CombatDamageModel.Mitigate and
    /// HitChance; BossFirstClearRules; FoodRegistry; CombatGoldReward. The swing
    /// timing, the order inside a tick (regen, player, monster, auto-eat, death,
    /// kill) and the reset of the swing clock on respawn are RunCombatTick's.
    /// Each roll is replaced by its expectation - or, for how many swings a
    /// fight takes, by its exact distribution (SwingsToKill) - so the answer is
    /// deterministic. HuntingProjectionTests and OfflineCombatParityTests run
    /// the REAL RunCombatTick against it and fail if they drift.
    ///
    /// Left out of the ADVISOR, each on purpose: the food buff's regen and
    /// Death Ward (both timed consumables - an estimate is about standing
    /// gear). The offline projection includes both, because they are what the
    /// live tick would have done in that window. Left out of both: Last Stand
    /// (the tick reads it only at the top of a tick with the bar already at or
    /// below zero, which the death branch at the bottom of the previous tick
    /// never leaves - in practice only a session's very first tick) and
    /// Thunderer (armed only on a session's first spawn, never on a respawn).
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

            // The advisor prices standing gear: no timed consumables (see remarks).
            var setup = FightSetup.For(in payload, monsterId, timedEffects: false);
            if (!setup.CanDamage)
            {
                return new HuntingEstimate { MonsterId = monsterId };
            }

            var withFood = FightState.FullBar(in payload, in setup, withFood: true);
            Advance(ref withFood, in setup, HorizonTicks);
            var noFood = FightState.FullBar(in payload, in setup, withFood: false);
            Advance(ref noFood, in setup, HorizonTicks);

            // Seconds per kill: the cycle the simulation measured, and a band
            // from the swing count's spread (hit, crit and Double Strike rolls).
            // Swings to kill ~ HP/mean with standard deviation sqrt(n)*sd/mean.
            PlayerSwing swing = setup.Swing;
            int playerIntervalMs = setup.PlayerIntervalMs;
            double swings = setup.FarmMilliHp / swing.Mean;
            double swingSd = Math.Sqrt(Math.Max(1.0, swings)) * swing.StdDev / swing.Mean;
            double lowSwings = Math.Max(1.0, Math.Ceiling(swings - (BandZ * swingSd)));
            double highSwings = Math.Max(lowSwings, Math.Ceiling(swings + (BandZ * swingSd)));
            double secondsPerKill = withFood.Kills > 0
                ? withFood.Ticks * TickMs / 1000.0 / withFood.Kills
                : Math.Ceiling(swings) * playerIntervalMs / 1000.0;

            double killsPerHour = 3600.0 / secondsPerKill;

            MonsterDefinition monster = setup.Monster;
            long xpPerKill = XpPerKill(in payload, in monster, globalXpMultiplier, activeGlobalEventId);
            long goldPerKill = CombatGoldReward.PerKill(in payload, in monster, setup.Stats.GoldAcquisitionMultiplierPct);

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
                KillsBeforeDeathWithoutFood = noFood.Died ? (int)noFood.Kills : 0,
                FoodPerHour = withFood.Died
                    ? withFood.Bites * (3600.0 / Math.Max(1.0, withFood.Ticks * TickMs / 1000.0))
                    : withFood.Bites,
            };
        }

        /// <summary>
        /// XP one kill pays: the live multiplier, then ProcessMonsterDeath's own
        /// two terms (Blood Moon's +15, the mentorship penalty's 80%), with the
        /// same integer truncation per kill.
        /// </summary>
        internal static long XpPerKill(in TickStatePayload payload, in MonsterDefinition monster, int globalXpMultiplier, int activeGlobalEventId)
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

        /// <summary>
        /// Everything about a fight that does not change while it is fought:
        /// both swings, the bar, the larder's heal per bite, and - for a
        /// first-clear boss - the boss both before and after it falls.
        /// </summary>
        internal readonly struct FightSetup
        {
            public bool CanDamage { get; init; }
            public MonsterDefinition Monster { get; init; }
            public CombatStats Stats { get; init; }
            public long MaxMilliHp { get; init; }
            public int PlayerIntervalMs { get; init; }
            public PlayerSwing Swing { get; init; }
            public double EatAtMilliHp { get; init; }
            public long LifestealCeiling { get; init; }
            public int Heal1 { get; init; }
            public int Heal2 { get; init; }
            public int Heal3 { get; init; }
            public double ThornsFraction { get; init; }

            /// <summary>The food buff's regen, per tick (timed effects only).</summary>
            public int RegenPerTick { get; init; }

            /// <summary>A Death Ward in the defensive slot (timed effects only).</summary>
            public bool DeathWardArmed { get; init; }

            public long FirstClearMilliHp { get; init; }
            public double FirstClearMonsterSwing { get; init; }
            public SwingsToKill? FirstClearKill { get; init; }
            public long FarmMilliHp { get; init; }
            public double FarmMonsterSwing { get; init; }
            public SwingsToKill? FarmKill { get; init; }

            /// <param name="timedEffects">
            /// The food buff's regen and a Death Ward. The offline projection
            /// includes them - they are what the live tick would have done in
            /// that window. The advisor leaves them out (see the class remarks).
            /// </param>
            public static FightSetup For(in TickStatePayload payload, int monsterId, bool timedEffects)
            {
                MonsterDefinition monster = ContentRegistry.Monsters[monsterId - 1];
                TickStatePayload copy = payload;
                CombatStats stats = SimulationEngine.LiveCombatStats(in copy);
                long maxMilliHp = SimulationEngine.EffectiveMaxMilliHpFor(in copy, in stats);
                long rawMilliAttack = SimulationEngine.EffectiveMilliAttackFor(
                    ref copy, in stats, SimulationEngine.LineageOf(in copy).DamageScalePerLevelPct);

                var swing = PlayerSwing.For(in copy, in stats, in monster, rawMilliAttack);
                if (swing.Mean <= 0.0)
                {
                    return new FightSetup { CanDamage = false, Monster = monster, Stats = stats, MaxMilliHp = maxMilliHp };
                }

                // Modul: A FIRST-CLEAR BOSS IS BIGGER UNTIL IT FALLS, THEN IT IS
                // NOT. The live tick spawns it through BossFirstClearRules on the
                // payload's mask and marks the mask at the kill, so exactly one
                // fight is at first-clear health and attack and every respawn
                // after it is the farmable boss. Both are priced here; the state
                // says which one is standing.
                byte mask = copy.DefeatedRegionBossMask;
                byte farmMask = BossFirstClearRules.MarkDefeated(mask, monsterId);
                long firstClearHp = BossFirstClearRules.MaxHpFor(mask, monsterId, copy.Skill_FirstBlood) * 1000L;
                long farmHp = BossFirstClearRules.MaxHpFor(farmMask, monsterId, copy.Skill_FirstBlood) * 1000L;
                bool thorns = stats.SetThornsReflectionActive;
                int effectiveMaxHp = (int)maxMilliHp;

                // Thorns end fights between the player's swings, which a swing
                // count cannot describe - those fights track health instead.
                SwingsToKill? firstClearKill = thorns ? null : SwingsToKill.For(in swing, firstClearHp);
                SwingsToKill? farmKill = thorns ? null
                    : farmHp == firstClearHp ? firstClearKill
                    : SwingsToKill.For(in swing, farmHp);

                return new FightSetup
                {
                    CanDamage = true,
                    Monster = monster,
                    Stats = stats,
                    MaxMilliHp = maxMilliHp,
                    PlayerIntervalMs = SimulationEngine.LiveAttackIntervalMs(in copy, in stats),
                    Swing = swing,
                    EatAtMilliHp = (copy.AutoEatThreshold / 100.0f) * effectiveMaxHp,
                    LifestealCeiling = effectiveMaxHp / 100,
                    Heal1 = FoodRegistry.GetHealMilliHp(copy.Food1_ItemId, maxMilliHp),
                    Heal2 = FoodRegistry.GetHealMilliHp(copy.Food2_ItemId, maxMilliHp),
                    Heal3 = FoodRegistry.GetHealMilliHp(copy.Food3_ItemId, maxMilliHp),
                    ThornsFraction = thorns ? SimulationEngine.ThornsReflectionFraction : 0.0,
                    RegenPerTick = timedEffects && copy.ActiveFoodBuffId > 0
                        ? Math.Max(1, effectiveMaxHp / ConsumableEngine.FoodRegenDivisor)
                        : 0,
                    DeathWardArmed = timedEffects
                        && ConsumableEngine.DeathWardItemId > 0
                        && copy.ActiveDefensivePotionId == ConsumableEngine.DeathWardItemId,
                    FirstClearMilliHp = firstClearHp,
                    FirstClearMonsterSwing = SimulationEngine.ExpectedMonsterMilliDamagePerSwing(in stats, monsterId, mask, maxMilliHp),
                    FirstClearKill = firstClearKill,
                    FarmMilliHp = farmHp,
                    FarmMonsterSwing = SimulationEngine.ExpectedMonsterMilliDamagePerSwing(in stats, monsterId, farmMask, maxMilliHp),
                    FarmKill = farmKill,
                };
            }
        }

        /// <summary>
        /// Where a projected fight stands: the player's bar, the monster, the
        /// swing clock, the larder, and the running totals.
        /// </summary>
        internal struct FightState
        {
            public double PlayerHp;
            public double MonsterHp;
            public int SwingsThisFight;
            public int SwingsNeeded;
            public int Accumulator;
            public int EatCooldown;
            public int Food1, Food2, Food3;
            public bool WithFood;
            public bool FirstClearPending;
            public bool DeathWardUp;
            public bool DeathWardUsed;
            public long FirstClearFights;
            public long FarmFights;
            public long Kills;
            public long FirstClearKills;
            public long Bites;
            public long Ticks;
            public bool Died;

            /// <summary>
            /// Auto-eat wanted a bite and the larder was empty - the moment the
            /// live tick raises ActivityHaltReason.OutOfFood.
            /// </summary>
            public bool Starved;

            /// <summary>
            /// Task 85: stop at the end of the tick that starved, because a
            /// rule (AutomationRules.FishWhenLarderDry) will move the character.
            /// Off for the advisor and for a character without the rule, which
            /// fight on unhealed as the live tick does.
            /// </summary>
            public bool StopWhenStarved;

            /// <summary>A fresh fight from a full bar, as the advisor prices one.</summary>
            public static FightState FullBar(in TickStatePayload payload, in FightSetup setup, bool withFood)
                => Begin(in payload, in setup, setup.MaxMilliHp, withFood);

            /// <summary>
            /// A fresh fight from <paramref name="playerMilliHp"/> - the bar the
            /// character logged off with, for the offline projection. At or below
            /// zero is a full bar, as the tick's own top-of-tick reset makes it.
            /// </summary>
            public static FightState Begin(in TickStatePayload payload, in FightSetup setup, double playerMilliHp, bool withFood)
            {
                var state = new FightState
                {
                    PlayerHp = playerMilliHp > 0 ? Math.Min(playerMilliHp, setup.MaxMilliHp) : setup.MaxMilliHp,
                    Food1 = payload.Food1_Count,
                    Food2 = payload.Food2_Count,
                    Food3 = payload.Food3_Count,
                    WithFood = withFood,
                    FirstClearPending = BossFirstClearRules.IsFirstClearPending(payload.DefeatedRegionBossMask, setup.Monster.Id),
                    DeathWardUp = setup.DeathWardArmed,
                };
                StartFight(ref state, in setup);
                return state;
            }
        }

        /// <summary>A new monster: its health, its swing count, and the swing clock at zero.</summary>
        private static void StartFight(ref FightState s, in FightSetup setup)
        {
            s.Accumulator = 0;
            s.SwingsThisFight = 0;
            SwingsToKill? kill;
            long fightIndex;
            if (s.FirstClearPending)
            {
                s.MonsterHp = setup.FirstClearMilliHp;
                kill = setup.FirstClearKill;
                fightIndex = s.FirstClearFights++;
            }
            else
            {
                s.MonsterHp = setup.FarmMilliHp;
                kill = setup.FarmKill;
                fightIndex = s.FarmFights++;
            }
            s.SwingsNeeded = kill?.Sample(fightIndex) ?? 0;
        }

        /// <summary>
        /// Up to <paramref name="ticks"/> ticks of back-to-back fights, in
        /// RunCombatTick's order - food buff regen, player swing, monster swing,
        /// auto-eat, death (and a Death Ward), kill and respawn with the swing
        /// clock reset - each roll replaced by its expectation. Stops at a
        /// death. Callable repeatedly on one state, so the offline projection
        /// can re-derive the character between stretches (a level gained).
        /// </summary>
        /// <remarks>
        /// Modul: HOW LONG A FIGHT TAKES IS A DISTRIBUTION, NOT A MEAN. Counting
        /// how many MEAN swings a monster's health holds is exact only when every
        /// swing lands the same. With misses and crits, and the swing clock reset
        /// at every kill, a monster that dies in one to three swings takes a
        /// different number of ticks on average than one that always takes two -
        /// the offline projection's old seconds-per-kill (a mean divided into the
        /// health) read a region-5 character one-shotting region 1 as a third
        /// of its real kill rate. So each fight draws its swing count from
        /// SwingsToKill, the exact distribution of the live rolls, along a
        /// golden-ratio sequence: deterministic, and within a few dozen fights
        /// the counts are the distribution's own. Fights where thorns can land
        /// the last blow, or that take so many swings the mean is exact enough,
        /// track health instead.
        /// </remarks>
        internal static void Advance(ref FightState s, in FightSetup setup, int ticks)
        {
            PlayerSwing swing = setup.Swing;
            double swingHeal = swing.ExpectedHeal(setup.LifestealCeiling);
            double max = setup.MaxMilliHp;
            int effectiveMaxHp = (int)setup.MaxMilliHp;

            for (int i = 0; i < ticks && !s.Died; i++)
            {
                s.Ticks++;

                if (setup.RegenPerTick > 0 && s.PlayerHp > 0 && s.PlayerHp < max)
                {
                    s.PlayerHp = Math.Min(max, s.PlayerHp + setup.RegenPerTick);
                }

                s.Accumulator++;
                bool monsterDown = false;

                if (SimulationEngine.HasCrossedInterval(s.Accumulator, setup.PlayerIntervalMs))
                {
                    s.PlayerHp = Math.Min(max, s.PlayerHp + swingHeal);
                    if (s.SwingsNeeded > 0)
                    {
                        s.SwingsThisFight++;
                        monsterDown = s.SwingsThisFight >= s.SwingsNeeded;
                    }
                    else
                    {
                        s.MonsterHp -= swing.Mean;
                        monsterDown = s.MonsterHp <= 0;
                    }
                }

                if (!monsterDown && SimulationEngine.HasCrossedInterval(s.Accumulator, setup.Monster.AttackIntervalMs))
                {
                    double monsterSwing = s.FirstClearPending ? setup.FirstClearMonsterSwing : setup.FarmMonsterSwing;
                    s.PlayerHp -= monsterSwing;
                    if (setup.ThornsFraction > 0 && s.SwingsNeeded == 0)
                    {
                        s.MonsterHp -= monsterSwing * setup.ThornsFraction;
                        monsterDown = s.MonsterHp <= 0;
                    }
                }

                if (s.EatCooldown > 0)
                {
                    s.EatCooldown--;
                }
                else if (s.WithFood && s.PlayerHp > 0 && s.PlayerHp <= setup.EatAtMilliHp)
                {
                    // Best heal first, as auto-eat picks.
                    int best = 0, heal = 0;
                    if (s.Food1 > 0 && setup.Heal1 > heal) { best = 1; heal = setup.Heal1; }
                    if (s.Food2 > 0 && setup.Heal2 > heal) { best = 2; heal = setup.Heal2; }
                    if (s.Food3 > 0 && setup.Heal3 > heal) { best = 3; heal = setup.Heal3; }
                    if (best > 0)
                    {
                        if (best == 1) s.Food1--; else if (best == 2) s.Food2--; else s.Food3--;
                        s.PlayerHp = Math.Min(max, s.PlayerHp + heal);
                        s.EatCooldown = SimulationEngine.AutoEatCooldownTicks;
                        s.Bites++;
                    }
                    else
                    {
                        // The live tick's `bestFoodIndex == 0` branch: OutOfFood.
                        s.Starved = true;
                    }
                }

                if (s.PlayerHp <= 0)
                {
                    if (s.DeathWardUp)
                    {
                        // ConsumableEngine.TryInterceptLethalDamage: up at a fifth of the bar.
                        s.PlayerHp = Math.Max(1, effectiveMaxHp / ConsumableEngine.DeathWardReviveDivisor);
                        s.DeathWardUp = false;
                        s.DeathWardUsed = true;
                    }
                    else
                    {
                        s.Died = true;
                        return;
                    }
                }

                if (monsterDown)
                {
                    s.Kills++;
                    if (s.FirstClearPending) s.FirstClearKills++;
                    s.FirstClearPending = false;
                    StartFight(ref s, in setup);
                }

                // Modul: task 85. At the END of the starving tick, after its
                // death and kill: the live tick answers the rule at the top of
                // the next tick (RunCombatTick), so everything this tick did
                // still counts on both paths.
                if (s.Starved && s.StopWhenStarved)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// How many swings a monster takes to die, as a distribution over the
        /// live rolls (miss, hit, crit, doubled crit). Built by convolving one
        /// swing's outcomes over the monster's health in fine buckets; each
        /// outcome's damage is split between its two neighbouring buckets so
        /// the bucketing keeps the mean exact.
        /// </summary>
        internal sealed class SwingsToKill
        {
            private const int Buckets = 4096;

            /// <summary>Past this many expected swings the mean is exact enough (the ceiling's bias is under 2%), and health is tracked instead.</summary>
            private const double MaxExpectedSwings = 60.0;

            private const int MaxSwings = 4000;
            private const double GoldenRatioConjugate = 0.6180339887498949;

            private readonly double[] _cdf;

            private SwingsToKill(double[] cdf) => _cdf = cdf;

            /// <summary>The expected swing count.</summary>
            public double Mean
            {
                get
                {
                    double mean = 0, previous = 0;
                    for (int i = 0; i < _cdf.Length; i++)
                    {
                        mean += (i + 1) * (_cdf[i] - previous);
                        previous = _cdf[i];
                    }
                    return mean;
                }
            }

            /// <summary>The fight's swing count: the median first, then the golden-ratio walk.</summary>
            public int Sample(long fightIndex)
            {
                double u = (0.5 + (fightIndex * GoldenRatioConjugate)) % 1.0;
                int lo = 0, hi = _cdf.Length - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (_cdf[mid] >= u) hi = mid; else lo = mid + 1;
                }
                return lo + 1;
            }

            public static SwingsToKill? For(in PlayerSwing swing, long monsterMilliHp)
            {
                if (monsterMilliHp <= 0 || swing.Mean <= 0 || monsterMilliHp / swing.Mean > MaxExpectedSwings)
                {
                    return null;
                }

                double unit = (double)monsterMilliHp / Buckets;
                Span<double> p = stackalloc double[3] { swing.PNormal, swing.PCrit, swing.PDouble };
                Span<double> d = stackalloc double[3] { swing.NormalDamage / unit, swing.CritDamage / unit, swing.DoubleDamage / unit };
                double pMiss = Math.Max(0.0, 1.0 - swing.PNormal - swing.PCrit - swing.PDouble);

                var cur = new double[Buckets];
                var next = new double[Buckets];
                cur[0] = 1.0;
                int top = 0;
                double absorbedTotal = 0.0;
                var cdf = new System.Collections.Generic.List<double>();

                for (int n = 1; n <= MaxSwings && absorbedTotal < 1.0 - 1e-9; n++)
                {
                    Array.Clear(next, 0, Buckets);
                    int newTop = 0;
                    double absorbed = 0.0;
                    for (int i = 0; i <= top; i++)
                    {
                        double mass = cur[i];
                        if (mass <= 0) continue;
                        next[i] += mass * pMiss;
                        if (pMiss > 0 && i > newTop) newTop = i;
                        for (int o = 0; o < 3; o++)
                        {
                            if (p[o] <= 0) continue;
                            double at = i + d[o];
                            double m = mass * p[o];
                            if (at >= Buckets) { absorbed += m; continue; }
                            int lo = (int)at;
                            double frac = at - lo;
                            next[lo] += m * (1 - frac);
                            if (lo > newTop) newTop = lo;
                            if (lo + 1 >= Buckets)
                            {
                                absorbed += m * frac;
                            }
                            else
                            {
                                next[lo + 1] += m * frac;
                                if (lo + 1 > newTop) newTop = lo + 1;
                            }
                        }
                    }
                    (cur, next) = (next, cur);
                    top = newTop;
                    absorbedTotal += absorbed;
                    cdf.Add(Math.Min(1.0, absorbedTotal));
                }

                // Whatever mass the cut-off left behind is folded into the last count.
                cdf[^1] = 1.0;
                return new SwingsToKill(cdf.ToArray());
            }
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

            // Per-outcome landed damage and probability, for the heal terms
            // (capped per hit, so not derivable from the mean) and for
            // SwingsToKill.
            private readonly double _pHit, _pCrit, _pDouble;
            private readonly double _normal, _crit, _double;
            private readonly float _lifestealPct;
            private readonly long _bloodthirstTenths;
            // Landed damage includes burn; the heals are on the hit before it.
            private readonly double _burnFactor;

            /// <summary>P(a plain hit), P(a crit that is not doubled), P(a doubled crit).</summary>
            public double PNormal => _pHit * (1 - _pCrit);
            public double PCrit => _pHit * _pCrit * (1 - _pDouble);
            public double PDouble => _pHit * _pCrit * _pDouble;
            public double NormalDamage => _normal;
            public double CritDamage => _crit;
            public double DoubleDamage => _double;

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
                return (PNormal * HealOn(_normal, lifestealCeiling)) + (PCrit * HealOn(_crit, lifestealCeiling)) + (PDouble * HealOn(_double, lifestealCeiling));
            }

            private double HealOn(double landed, long lifestealCeiling)
            {
                double hit = landed / _burnFactor;
                double heal = 0.0;
                if (_bloodthirstTenths > 0) heal += hit * _bloodthirstTenths / 1000.0;
                if (_lifestealPct > 0f) heal += Math.Min(lifestealCeiling, hit * (_lifestealPct / 100.0));
                return heal;
            }
        }
    }
}
