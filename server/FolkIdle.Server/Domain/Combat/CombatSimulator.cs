using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// Phase 2b.2 (roadmap 5): an away window fought with the live tick's own
    /// code, swing by swing, instead of an expected-value model of it.
    ///
    /// Modul: HOW IT IS BOTH EXACT AND FAST. Ticking RunCombatTick 36,000 times
    /// an hour costs 330-610 ms for a 12-hour window (measured 2026-11-02),
    /// against a 50 ms budget, because every tick re-derives the character's
    /// stats. But most ticks do nothing except count: no swing crosses its
    /// interval, nobody eats, no ration falls due. So the simulator runs the
    /// REAL RunCombatTick on every tick where something can happen, and jumps
    /// over the quiet ticks between them in one step (<see cref="SkipQuietTicks"/>),
    /// applying exactly what those ticks would have: the fight clock, the food
    /// buff's regeneration, and the cooldown and ration counters.
    ///
    /// Because the jump never skips a tick that rolls dice, a seeded window
    /// draws the same numbers in the same order as the same window ticked one
    /// tick at a time - so the two must agree EXACTLY, not just on average.
    /// CombatSimulatorTests holds that for every parity row; it is the guard
    /// against <see cref="QuietTicksAhead"/> ever calling a tick quiet that is not.
    ///
    /// Everything the tick would send (event feed, loot and codex queues, boss
    /// notes, pet drops) lands in the <see cref="Tally"/> instead
    /// (SimulationEngine.Simulating), so a simulated window reaches no live
    /// queue; the caller decides what to pay.
    /// </summary>
    internal static class CombatSimulator
    {
        /// <summary>What a simulated window would have sent, counted instead.</summary>
        internal sealed class Tally
        {
            public int Kills;
            public long PaidKills;
            public int BossKills;
            public int AscensionClears;
            public int SeasonalBossClears;
            public readonly List<CombatLootDropRequest> DropRequests = new();
            public readonly List<KillEvent> CodexKills = new();
            public readonly List<string> PetDrops = new();
            // Modul: phase 2b.4. The notes the live kill hands its engines, kept
            // whole rather than counted, so an away window can REPLAY them
            // (OfflineSimulationEngine) - an Ascension step cleared away is
            // marked on the payload by the tick, and a count alone would leave
            // its titles and frames unpaid.
            public readonly List<(int Region, int MonsterId, int Level, bool Ate, int FightTenths)> BossKillNotes = new();
            public readonly List<(int Region, int Step, int MonsterId)> AscensionNotes = new();
            public readonly List<SeasonalBossClearNote> SeasonalBossNotes = new();
        }

        internal readonly record struct Outcome(long TicksRun, long TicksTicked, bool Died, bool LeftTheFight, Tally Tally)
        {
            public double SecondsFought => TicksRun / 10.0;
        }

        /// <summary>
        /// Fights <paramref name="ticks"/> ticks of the payload's current
        /// activity, or until the character dies or leaves the fight (goes
        /// fishing when the larder runs out). <paramref name="tickEveryTick"/>
        /// turns the jump off - the reference the tests compare against.
        /// </summary>
        internal static Outcome Run(ref TickStatePayload payload, long ticks, Random rng, bool tickEveryTick = false)
        {
            var tally = new Tally();
            var guildWar = new ConcurrentQueue<GuildWarPointEvent>();
            var noSessions = new ConcurrentDictionary<long, LiveSessionContext>();
            long activity = payload.ActiveActivityId;
            long run = 0, ticked = 0;
            bool died = false, left = false;

            var previous = SimulationEngine.BeginSimulation(tally);
            try
            {
                while (run < ticks)
                {
                    SimulationEngine.RunCombatTick(ref payload, 100, 100, guildWar, noSessions, rng);
                    run++;
                    ticked++;
                    if (payload.ActiveActivityId == 0) { died = true; break; }
                    if (payload.ActiveActivityId != activity) { left = true; break; }

                    if (tickEveryTick) continue;
                    long quiet = Math.Min(QuietTicksAhead(in payload, out int maxHp), ticks - run);
                    if (quiet > 0)
                    {
                        SkipQuietTicks(ref payload, quiet, maxHp);
                        run += quiet;
                    }
                }
            }
            finally
            {
                SimulationEngine.EndSimulation(previous);
            }
            return new Outcome(run, ticked, died, left, tally);
        }

        /// <summary>
        /// How many of the next ticks would change nothing but the fight clock,
        /// the food buff's regeneration and the per-tick counters. Every
        /// condition below mirrors one branch of RunCombatTick, in its order;
        /// any doubt answers 0, which only costs speed.
        /// </summary>
        internal static long QuietTicksAhead(in TickStatePayload payload, out int maxHp)
        {
            maxHp = 0;
            // The top of the tick: the fishing rule, a rested bar, a death, a spawn.
            if (payload.ActivityHaltReason == Network.ActivityHaltReason.OutOfFood
                && AutomationRules.CanGoFishing(in payload, out _)) return 0;
            if (payload.RestedHpPending) return 0;
            if (payload.PlayerHp <= 0) return 0;
            if (payload.CurrentMonsterId <= 0 || payload.CurrentMonsterHp <= 0) return 0;
            if (payload.ActiveActivityId <= 0) return 0;

            var stats = SimulationEngine.LiveCombatStats(in payload);
            // Every return below this line is followed by the next tick, on a
            // payload that differs only in fields the stats never read.
            SimulationEngine.CarryStatsIntoNextTick(in stats);
            maxHp = SimulationEngine.BarAsInt(SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats));
            var monster = ContentRegistry.Monsters[payload.CurrentMonsterId - 1];
            int playerInterval = SimulationEngine.LiveAttackIntervalMs(in payload, in stats);

            // Auto-eat: while the cooldown runs, the tick only counts it down;
            // once it is out, a bar at or under the threshold eats. Regeneration
            // only ever raises the bar between swings, so a bar above the
            // threshold now stays above it until the next blow lands.
            long limit = long.MaxValue;
            bool atThreshold = payload.PlayerHp <= (payload.AutoEatThreshold / 100.0f) * maxHp;
            if (atThreshold)
            {
                bool idleEmptyLarder = payload.ActivityHaltReason == Network.ActivityHaltReason.OutOfFood
                    && payload.Food1_Count <= 0 && payload.Food2_Count <= 0 && payload.Food3_Count <= 0;
                if (!idleEmptyLarder)
                {
                    // Cooldown c: c ticks count it down, the next one eats.
                    limit = payload.AutoEatCooldownTicks;
                    if (limit <= 0) return 0;
                }
            }

            // Rations: the tick that brings the counter to the interval pays one.
            int region = ContentRegistry.GetMonsterRegionTier(payload.CurrentMonsterId);
            int rationInterval = FoodRegistry.RationIntervalTicks(region);
            if (rationInterval <= 0)
            {
                if (payload.RationTicksSinceMeal != 0 || payload.Hungry) return 0;
            }
            else if (!payload.Hungry)
            {
                limit = Math.Min(limit, rationInterval - payload.RationTicksSinceMeal - 1L);
            }
            else if (FoodRegistry.PickRationSlot(
                payload.Food1_ItemId, payload.Food1_Count, payload.Food2_ItemId, payload.Food2_Count,
                payload.Food3_ItemId, payload.Food3_Count, region, out _) != 0)
            {
                return 0;
            }
            if (limit <= 0) return 0;

            // The next swing, either side's. The interval test is cheap integer
            // arithmetic, so walk it rather than re-derive its closed form.
            long quiet = 0;
            int accumulator = payload.CombatTargetTickAccumulator;
            while (quiet < limit)
            {
                int next = accumulator + (int)quiet + 1;
                if (SimulationEngine.HasCrossedInterval(next, playerInterval)) break;
                if (SimulationEngine.HasCrossedInterval(next, monster.AttackIntervalMs)) break;
                quiet++;
                // A fight that never swings (an interval of 0) has nothing to wait for.
                if (quiet >= 600) break;
            }
            return quiet;
        }

        /// <summary>
        /// Applies <paramref name="count"/> quiet ticks at once - exactly what
        /// RunCombatTick does on a tick where no swing, bite or ration happens.
        /// <paramref name="maxHp"/> is the bar QuietTicksAhead derived from the
        /// same payload - quiet ticks change nothing it depends on.
        /// </summary>
        internal static void SkipQuietTicks(ref TickStatePayload payload, long count, int maxHp)
        {
            if (count <= 0) return;

            // The food buff's regeneration, a fixed amount a tick, capped at the
            // bar: k capped additions are one capped addition of k.
            if (payload.ActiveFoodBuffId > 0 && payload.PlayerHp > 0 && payload.PlayerHp < maxHp)
            {
                int regenPerTick = maxHp / Shared.ConsumableEngine.FoodRegenDivisor;
                if (regenPerTick < 1) regenPerTick = 1;
                payload.PlayerHp = SimulationEngine.HealClamped(payload.PlayerHp, regenPerTick * count, maxHp);
            }

            payload.CombatTargetTickAccumulator = (int)(payload.CombatTargetTickAccumulator + count);
            payload.LastStandCooldownRemaining = (int)Math.Max(0L, payload.LastStandCooldownRemaining - count);
            payload.AutoEatCooldownTicks = (int)Math.Max(0L, payload.AutoEatCooldownTicks - count);

            int rationInterval = FoodRegistry.RationIntervalTicks(ContentRegistry.GetMonsterRegionTier(payload.CurrentMonsterId));
            if (rationInterval > 0 && !payload.Hungry)
            {
                payload.RationTicksSinceMeal = (int)(payload.RationTicksSinceMeal + count);
            }
        }
    }
}
