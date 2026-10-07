using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// OWNER RULE (2026-09-30): an hour away pays what an hour watched pays -
    /// kills, XP, gold, food and survival. The offline projection is an
    /// expected-value model of RunCombatTick, so every difference is a bug.
    ///
    /// Each row runs the REAL RunCombatTick for an hour (three independent
    /// hours, averaged, because the tick rolls dice) and the offline projection
    /// (OfflineSimulationEngine.ProjectCombat) for the same hour from the same
    /// payload, and asserts they agree. Rows: bare and geared characters in all
    /// five regions, fed and hungry; a fast killer; a first-clear boss; and a
    /// lifesteal + Bloodthirst build.
    /// </summary>
    public class OfflineCombatParityTests
    {
        private const int LiveHours = 6;
        private const long Hour = 3600;

        private readonly ITestOutputHelper _output;

        public OfflineCombatParityTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        /// <summary>A regular from the middle of each region: 93, 98, 103, 108, 113.</summary>
        private static int RegularOf(int region) => ContentRegistry.FirstCanonicalMonsterId + ((region - 1) * 5) + 2;

        /// <summary>
        /// A human at the region's reference level, bare or in the region's
        /// wall-required gear (the loadout BossGearBenchmark prices).
        /// </summary>
        internal static TickStatePayload Character(int region, bool geared, int monsterId, int food)
        {
            int level = BossGearBenchmark.ReferenceLevelForRegion(region);
            var payload = new TickStatePayload
            {
                PlayerId = 978_201L,
                CurrentLevel = level,
                ActiveActivityId = monsterId,
                AutoEatThreshold = 50,
                Food1_ItemId = FoodRegistry.FirstRawFishOfTier(ContentRegistry.GetMonsterRegionTier(monsterId)),
                Food1_Count = food,
                CachedCodexDamageMultiplier = 1f,
                CachedCodexYieldMultiplier = 1f,
                InventorySpaceRemaining = 1000,
            };
            RaceAttributeGrowth.GetGrowthPerLevel(RaceIds.Human, out int str, out int dex, out int con, out int lck);
            payload.STR = 50 + (str * (level - 1));
            payload.DEX = 50 + (dex * (level - 1));
            payload.CON = 50 + (con * (level - 1));
            payload.LCK = 25 + (lck * (level - 1));
            if (geared)
            {
                payload.CachedAffixTotals = BossGearBenchmark.BuildEquippedTotals(new ReferenceLoadout(
                    level, region, BossFirstClearRules.RequiredQualityTierFor(region), BossFirstClearRules.RequiredAffixRarityFor(region)));
            }
            // Every boss behind the character is beaten, so a regular's region
            // is open and a boss row is a farm unless the row says otherwise.
            for (int r = RaceUnlockRegistry.FirstRegion; r < ContentRegistry.GetMonsterRegionTier(monsterId); r++)
            {
                payload.DefeatedRegionBossMask = BossFirstClearRules.MarkDefeated(
                    payload.DefeatedRegionBossMask, RaceUnlockRegistry.GetRegionBossMonsterId(r));
            }
            var stats = SimulationEngine.LiveCombatStats(in payload);
            payload.PlayerHp = (int)SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);
            return payload;
        }

        internal readonly record struct HourResult(double Kills, double Xp, double Gold, double Food, double SecondsAlive, double DeathShare);

        /// <summary>Total XP a payload moved through, levels included.</summary>
        private static long XpBetween(int levelBefore, long xpBefore, int levelAfter, long xpAfter)
        {
            long total = -xpBefore;
            for (int l = levelBefore; l < levelAfter; l++) total += ProgressionEngine.GetRequiredXpForLevel(l);
            return total + xpAfter;
        }

        private static int FoodCount(in TickStatePayload p) => p.Food1_Count + p.Food2_Count + p.Food3_Count;

        /// <summary>The live tick, rolls and all, for <see cref="LiveHours"/> independent hours, averaged.</summary>
        internal static HourResult RunLive(TickStatePayload start)
        {
            var guildWar = new ConcurrentQueue<GuildWarPointEvent>();
            var sessions = new ConcurrentDictionary<long, LiveSessionContext>();
            double kills = 0, xp = 0, gold = 0, food = 0, alive = 0, deaths = 0;
            try
            {
                for (int run = 0; run < LiveHours; run++)
                {
                    var payload = start;
                    int ticks = 0;
                    for (; ticks < HuntingProjection.HorizonTicks; ticks++)
                    {
                        int fleeceBefore = payload.KillsSinceFleece;
                        SimulationEngine.RunCombatTick(ref payload, 100, 100, guildWar, sessions);
                        // Every live kill moves the Golden Fleece counter (up
                        // one, or back to 0 from 99), and a tick holds at most
                        // one kill. A one-shot kill respawns the monster at the
                        // same full health, so the health cannot count it, and
                        // the static drop queue is shared with every other test.
                        if (payload.KillsSinceFleece != fleeceBefore) kills++;
                        if (payload.ActiveActivityId == 0)
                        {
                            deaths++;
                            ticks++;
                            break;
                        }
                    }
                    alive += ticks / 10.0;
                    xp += XpBetween(start.CurrentLevel, start.CurrentXp, payload.CurrentLevel, payload.CurrentXp);
                    gold += payload.CurrentGold - start.CurrentGold;
                    food += FoodCount(in start) - FoodCount(in payload);
                }
            }
            finally
            {
                // Static queues (server/CLAUDE.md): leave nothing for another test's worker.
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
                while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
            }
            return new HourResult(kills / LiveHours, xp / LiveHours, gold / LiveHours, food / LiveHours, alive / LiveHours, deaths / LiveHours);
        }

        internal static HourResult RunOffline(TickStatePayload payload)
        {
            int monsterId = (int)payload.ActiveActivityId;
            var before = payload;
            try
            {
                var outcome = OfflineSimulationEngine.ProjectCombat(ref payload, monsterId, Hour);
                return new HourResult(
                    outcome.Kills,
                    XpBetween(before.CurrentLevel, before.CurrentXp, payload.CurrentLevel, payload.CurrentXp),
                    payload.CurrentGold - before.CurrentGold,
                    FoodCount(in before) - FoodCount(in payload),
                    outcome.SecondsFought,
                    outcome.Died ? 1.0 : 0.0);
            }
            finally
            {
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            }
        }

        public static IEnumerable<object[]> Rows()
        {
            for (int region = 1; region <= 5; region++)
            {
                foreach (bool geared in new[] { false, true })
                {
                    yield return new object[] { $"r{region} {(geared ? "geared" : "bare")} fed", region, geared, RegularOf(region), 20_000, 0 };
                    yield return new object[] { $"r{region} {(geared ? "geared" : "bare")} hungry", region, geared, RegularOf(region), 0, 0 };
                }
            }
            // A fast killer: region-5 gear on region 1's first monster.
            yield return new object[] { "fast killer r5 gear on 91", 5, true, ContentRegistry.FirstCanonicalMonsterId, 0, 0 };
            // A first-clear boss: region-2 gear against region 1's boss, never beaten.
            yield return new object[] { "first-clear boss r1", 2, true, RaceUnlockRegistry.GetRegionBossMonsterId(1), 20_000, 1 };
            // The same boss bare: fed, it gets through the first clear on the
            // larder; hungry, the wall holds and the character dies in it.
            yield return new object[] { "first-clear r1 bare fed", 1, false, RaceUnlockRegistry.GetRegionBossMonsterId(1), 20_000, 1 };
            yield return new object[] { "first-clear wall r1 hungry", 1, false, RaceUnlockRegistry.GetRegionBossMonsterId(1), 0, 1 };
            // A lifesteal build: 10% lifesteal and Bloodthirst, no food.
            yield return new object[] { "lifesteal r2 hungry", 2, true, RegularOf(2), 0, 2 };
            yield return new object[] { "lifesteal r3 fed", 3, true, RegularOf(3), 20_000, 2 };
            // The skill tree's killer: Relentless, Precision, Cruelty, Guile,
            // Double Strike, a 5-piece burning set, and every XP term.
            yield return new object[] { "skill tree + burn r3 on r2", 3, true, RegularOf(2), 20_000, 3 };
            yield return new object[] { "skill tree + burn r5 on 91", 5, true, ContentRegistry.FirstCanonicalMonsterId, 0, 3 };
            // Thorns and the damage cap: a 5-piece Eternal Dreadnought.
            yield return new object[] { "thorns + cap r4 fed", 4, true, RegularOf(4), 20_000, 4 };
            // The food buff's regen and a Death Ward, with no larder.
            yield return new object[] { "food buff + ward r3 hungry", 3, true, RegularOf(3), 0, 5 };
            // A Warrior levelling through the hour: the bar and the swing grow.
            yield return new object[] { "levelling warrior r1", 1, false, ContentRegistry.FirstCanonicalMonsterId + 1, 20_000, 6 };
        }

        private static TickStatePayload Build(int region, bool geared, int monsterId, int food, int variant)
        {
            var payload = Character(region, geared, monsterId, food);
            if (variant == 1)
            {
                // The boss row: the boss has never been beaten.
                payload.DefeatedRegionBossMask = 0;
            }
            else if (variant == 2)
            {
                payload.CachedAffixTotals.LifestealTenthsPct += 100;
                payload.Skill_Bloodthirst = 5;
            }
            else if (variant == 3)
            {
                payload.Skill_Relentless = 8;
                payload.Skill_CritChance = 10;
                payload.Skill_CritDamage = 10;
                payload.Skill_Guile = 8;
                payload.Skill_DoubleStrike = 1;
                payload.Skill_XpGain = 10;
                payload.HumanMasteryLevel = 10;
                payload.CachedMentorCount = 2;
                payload.CurrentLevel = 45; // the mentor term needs a level under 50
                payload.CachedSetIds = SetOf(1);
            }
            else if (variant == 4)
            {
                payload.CachedSetIds = SetOf(SetBonusEngine.EternalDreadnoughtSetId);
            }
            else if (variant == 5)
            {
                // Modul: content holds no Death Ward since 2026-10-07, so the
                // ward points at a test id. The parity question - does the
                // offline projection rescue the same blow the tick does - does
                // not depend on where the ward came from.
                ConsumableEngine.UseDeathWardItemIdForTests(999_001);
                payload.ActiveFoodBuffId = 1;
                payload.ActiveDefensivePotionId = ConsumableEngine.DeathWardItemId;
            }
            else if (variant == 6)
            {
                payload.SelectedLineageId = 1;
                payload.CurrentLevel = 2;
            }
            var stats = SimulationEngine.LiveCombatStats(in payload);
            payload.PlayerHp = (int)SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);
            return payload;
        }

        private static EquippedSetIds SetOf(int setId)
        {
            int piece = EquippedSetIds.Pack(setId, SetBonusEngine.ReferenceQualityTier);
            return new EquippedSetIds { Weapon = piece, Helmet = piece, Chest = piece, Gloves = piece, Leggings = piece };
        }

        [Theory]
        [MemberData(nameof(Rows))]
        public void AnHourAway_PaysWhatAnHourWatchedPays(string row, int region, bool geared, int monsterId, int food, int variant)
        {
            var payload = Build(region, geared, monsterId, food, variant);
            var live = RunLive(payload);
            var offline = RunOffline(payload);

            _output.WriteLine(
                $"{row,-28} kills {offline.Kills,7:F0} / {live.Kills,7:F0} ({Ratio(offline.Kills, live.Kills)}) | "
                + $"xp {offline.Xp,10:F0} / {live.Xp,10:F0} ({Ratio(offline.Xp, live.Xp)}) | "
                + $"gold {offline.Gold,9:F0} / {live.Gold,9:F0} ({Ratio(offline.Gold, live.Gold)}) | "
                + $"food {offline.Food,6:F0} / {live.Food,6:F0} ({Ratio(offline.Food, live.Food)}) | "
                + $"alive {offline.SecondsAlive,5:F0}s / {live.SecondsAlive,5:F0}s, died {offline.DeathShare:F0} / {live.DeathShare:F2}");

            // The survival verdict itself: a death in every live hour or in none.
            Assert.True(live.DeathShare is 0.0 or 1.0, $"the live tick died in {live.DeathShare:P0} of its hours - pick a row that is not a coin toss");
            Assert.Equal(live.DeathShare, offline.DeathShare);

            // ±5% on every figure. The absolute allowances below matter only
            // where 5% is less than one event: a hungry character that lives
            // 45 seconds makes 1-5 kills, and whether the last one lands before
            // the death is a roll, so "one kill's worth" (of XP and gold too) is
            // the honest resolution there; a few bites for the larder; and five
            // seconds of a short life, which is one or two swings either way.
            double xpPerKill = live.Kills > 0 ? live.Xp / live.Kills : 0;
            double goldPerKill = live.Kills > 0 ? live.Gold / live.Kills : 0;
            AssertClose("kills", offline.Kills, live.Kills, 1.0);
            AssertClose("xp", offline.Xp, live.Xp, xpPerKill);
            AssertClose("gold", offline.Gold, live.Gold, goldPerKill);
            AssertClose("food", offline.Food, live.Food, 3.0);
            AssertClose("seconds alive", offline.SecondsAlive, live.SecondsAlive, 5.0);
        }

        private static string Ratio(double offline, double live)
            => live == 0 ? (offline == 0 ? "  =  " : " inf ") : $"{offline / live:F2}x";

        private static void AssertClose(string what, double offline, double live, double absoluteAllowance)
        {
            double slack = Math.Max(live * 0.05, absoluteAllowance);
            Assert.True(Math.Abs(offline - live) <= slack, $"{what}: offline {offline:F1}, live {live:F1}");
        }
    }
}
