using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Calibration instrument, not a regression test: how often does the LIVE
    /// tick kill a character the hunting advisor says holds with food? Skipped
    /// unless FOLKIDLE_CALIBRATION_DB is set (a bot-run database or a restored
    /// production copy); FOLKIDLE_SURVIVAL_PLAYERS lists the player ids and
    /// FOLKIDLE_SURVIVAL_SCALES the percent of the attributes each is fought at,
    /// which is what produces fights near the edge of holding.
    /// </summary>
    [Collection("Static loot queues")]
    public class HuntingSurvivalHarness
    {
        private const int Seeds = 20;
        private readonly ITestOutputHelper _output;
        private static string? Conn => Environment.GetEnvironmentVariable("FOLKIDLE_CALIBRATION_DB");

        public HuntingSurvivalHarness(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        [Fact]
        public async Task Print_HowOftenAFightThatHoldsKills()
        {
            if (string.IsNullOrEmpty(Conn)) return;
            long[] players = (Environment.GetEnvironmentVariable("FOLKIDLE_SURVIVAL_PLAYERS") ?? "1").Split(',').Select(long.Parse).ToArray();
            int[] scales = (Environment.GetEnvironmentVariable("FOLKIDLE_SURVIVAL_SCALES") ?? "100,80,60").Split(',').Select(int.Parse).ToArray();

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(o => o.UseNpgsql(Conn));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());
            services.AddSingleton(new RetryingDbContextOptions(new DbContextOptionsBuilder<FolkIdleDbContext>().UseNpgsql(Conn).Options));
            var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync("localhost:6379,abortConnect=false");
            services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(redis);
            services.AddSingleton(new RedisPlayerSessionLock(redis));
            var manager = new StateCheckpointManager(services.BuildServiceProvider());

            _output.WriteLine("player;scale;level;reborn;monster;bar;bite;eat_at;monster_hit;monster_crit;s_per_kill;no_food;with_food;deaths_full;deaths_low;minutes_to_death");
            foreach (long id in players)
            {
                TickStatePayload real = await manager.LoadPlayerState(id);
                foreach (int scale in scales)
                {
                    foreach (int monsterId in Enumerable.Range(91, 25))
                    {
                        var p = real;
                        p.STR = real.STR * scale / 100;
                        p.DEX = real.DEX * scale / 100;
                        p.CON = real.CON * scale / 100;
                        p.LCK = real.LCK * scale / 100;
                        // The larder a fed character walks in with: the region's own fish.
                        p.Food1_ItemId = FoodRegistry.FirstRawFishOfTier(ContentRegistry.GetMonsterRegionTier(monsterId));
                        p.Food1_Count = 20_000;
                        p.Food2_Count = 0;
                        p.Food3_Count = 0;
                        if (int.TryParse(Environment.GetEnvironmentVariable("FOLKIDLE_SURVIVAL_EAT_AT"), out int eatAt)) p.AutoEatThreshold = (byte)eatAt;
                        var e = HuntingProjection.Project(in p, monsterId, 100, 0);
                        if (!e.CanDamage || !e.SurvivesWithFood) continue;

                        var stats = SimulationEngine.LiveCombatStats(in p);
                        long bar = SimulationEngine.EffectiveMaxMilliHpFor(in p, in stats);
                        int region = ContentRegistry.GetMonsterRegionTier(monsterId);
                        long attack = BossFirstClearRules.AttackPowerFor(p.DefeatedRegionBossMask, monsterId);
                        long hit = SimulationEngine.LandedMonsterMilliDamage(attack, 1f, in stats, region, bar);
                        long crit = SimulationEngine.LandedMonsterMilliDamage(attack, SimulationEngine.MonsterCritMultiplier(in stats), in stats, region, bar);
                        // FOLKIDLE_SURVIVAL_MIN_CRIT: only fights whose monster crit takes at least this percent of the bar.
                        if (int.TryParse(Environment.GetEnvironmentVariable("FOLKIDLE_SURVIVAL_MIN_CRIT"), out int minCrit) && crit * 100 < bar * minCrit) continue;
                        long bite = InheritanceMilestones.FoodHealMilliHp(in p, p.Food1_ItemId, bar);

                        int full = 0, low = 0;
                        double minutes = 0;
                        for (int seed = 0; seed < Seeds; seed++)
                        {
                            int t = Fight(p, monsterId, (int)Math.Min(int.MaxValue, bar), seed);
                            if (t >= 0) { full++; minutes += t / 600.0; }
                            // A bar of 40%: what the character has left walking in from another fight.
                            if (Fight(p, monsterId, (int)Math.Min(int.MaxValue, bar * 2 / 5), seed) >= 0) low++;
                        }
                        _output.WriteLine($"{id};{scale};{p.CurrentLevel};{p.RenownedRebirths};{monsterId};{bar / 1000};{bite / 1000};{p.AutoEatThreshold};{hit / 1000};{crit / 1000};{e.SecondsPerKill:F1};{e.SurvivesWithoutFood};{e.SurvivesWithFood};{full};{low};{(full > 0 ? minutes / full : 0):F1}");
                    }
                }
            }
        }

        /// <summary>An hour on the live tick. The tick of the first death, or -1.</summary>
        private static int Fight(TickStatePayload p, int monsterId, int startHp, int seed)
        {
            p.ActiveActivityId = monsterId;
            p.CurrentMonsterId = 0;
            p.PlayerHp = startHp;
            p.RestedHpPending = false;
            p.ActivityHaltReason = 0;
            p.AutoEatCooldownTicks = 0;
            p.LastStandCooldownRemaining = 0;
            byte deaths = p.LastDeathTick;
            var rng = new Random(seed);
            var war = new ConcurrentQueue<GuildWarPointEvent>();
            var sessions = new ConcurrentDictionary<long, LiveSessionContext>();
            try
            {
                for (int tick = 0; tick < HuntingProjection.HorizonTicks; tick++)
                {
                    SimulationEngine.RunCombatTick(ref p, 100, 100, war, sessions, rng);
                    if (p.LastDeathTick != deaths) return tick;
                }
                return -1;
            }
            finally
            {
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            }
        }
    }
}
