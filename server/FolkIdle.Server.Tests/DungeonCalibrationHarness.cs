using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
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
    /// Calibration instrument for the three dungeons with dodges and the party
    /// (2026-10-31): loads a REAL account from a restored production copy and
    /// reports, per dungeon, the highest stage cleared alone and with the
    /// party, with every telegraph missed and with every one dodged, plus what
    /// each champion brings and what the XP and gold dungeons pay that account.
    /// Skipped unless FOLKIDLE_CALIBRATION_DB is set (FOLKIDLE_CALIBRATION_PLAYER
    /// picks the account, default 8).
    /// </summary>
    [Collection("Static loot queues")]
    public class DungeonCalibrationHarness
    {
        private readonly ITestOutputHelper _output;

        public DungeonCalibrationHarness(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static string? Conn => Environment.GetEnvironmentVariable("FOLKIDLE_CALIBRATION_DB");

        [Fact]
        public async Task Print_TheDungeonsAgainstARealAccount()
        {
            if (string.IsNullOrEmpty(Conn)) return;
            long playerId = long.Parse(Environment.GetEnvironmentVariable("FOLKIDLE_CALIBRATION_PLAYER") ?? "8");

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(o => o.UseNpgsql(Conn));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());
            services.AddSingleton(new RetryingDbContextOptions(new DbContextOptionsBuilder<FolkIdleDbContext>().UseNpgsql(Conn).Options));
            var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync("localhost:6379,abortConnect=false");
            services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(redis);
            services.AddSingleton(new RedisPlayerSessionLock(redis));
            var provider = services.BuildServiceProvider();
            var manager = new StateCheckpointManager(provider);

            TickStatePayload real = await manager.LoadPlayerState(playerId);
            real.DefeatedRegionBossMask = 0x1F;
            int fish = FoodRegistry.FirstRawFishOfTier(5);
            _output.WriteLine($"player {playerId}: level {real.CurrentLevel}, STR {real.STR} DEX {real.DEX} CON {real.CON}, rebirths {real.RenownedRebirths}");

            // What each champion brings: their own gear, the account's level and attributes.
            var slots = new System.Collections.Generic.List<int> { 0 };
            if (real.Slot2_CharacterId != Guid.Empty) slots.Add(1);
            if (real.Slot3_CharacterId != Guid.Empty) slots.Add(2);
            MonsterDefinition malakor = ContentRegistry.Monsters[RaceUnlockRegistry.GetRegionBossMonsterId(5) - 1];
            foreach (int slot in slots)
            {
                var p = real;
                SimulationEngine.SwapSlotIntoActiveRegister(ref p, slot);
                var stats = SimulationEngine.LiveCombatStats(in p);
                long hp = SimulationEngine.EffectiveMaxMilliHpFor(in p, in stats) / 1000;
                long atk = SimulationEngine.EffectiveMilliAttackFor(ref p, in stats, SimulationEngine.LineageOf(in p).DamageScalePerLevelPct);
                var swing = HuntingProjection.PlayerSwing.For(in p, in stats, in malakor, atk);
                int interval = SimulationEngine.LiveAttackIntervalMs(in p, in stats);
                int worn = new[] { p.EquippedWeaponId, p.EquippedHelmetId, p.EquippedArmorId, p.EquippedLeggingsId, p.EquippedBootsId, p.EquippedGlovesId, p.EquippedAmuletId, p.EquippedRingId }.Count(id => id > 0);
                _output.WriteLine($"champion {slot + 1}: {worn}/8 combat slots worn, max HP {hp}, swing vs Malakor {swing.Mean / 1000:F0} every {interval} ms = {swing.Mean / 1000 * 1000 / Math.Max(1, interval):F0} DPS, crit {swing.PCrit + swing.PDouble:P0}");
            }

            // Highest stage cleared, per dungeon and scenario.
            foreach (var kind in DungeonBoss.All)
            {
                foreach (var (label, party) in new[] { ("solo", new[] { 0 }), ($"party{slots.Count}", slots.ToArray()) })
                {
                    if (label != "solo" && slots.Count == 1) continue;
                    foreach (var mode in new[] { "none", "missed", "dodged" })
                    {
                        var line = new System.Text.StringBuilder($"{kind,-9} {label,-6} {mode,-6}:");
                        int highest = 0;
                        for (int stage = 1; stage <= DungeonSimulator.StageCount; stage++)
                        {
                            var r = Play(real, kind, stage, fish, party, mode);
                            line.Append(r.Won ? $" {stage}={r.Seconds:F0}s/{r.MealsEaten}m" : $" {stage}=X{(r.ReachedBoss ? $"{r.BossHpLeftPct}%" : $"w{r.WavesCleared}")}");
                            if (r.Won) highest = stage; else break;
                        }
                        _output.WriteLine(line.ToString());
                        _output.WriteLine($"{kind,-9} {label,-6} {mode,-6}: highest {highest}");
                    }
                }
            }

            // What the XP and gold dungeons pay this account per stage.
            await using var db = await provider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContextAsync();
            long hourlyXp = DungeonEngine.BestHuntingXpPerHour(in real, 100, 0);
            long hourlyGold = await GoldRewardScale.HourlyIncomeAsync(db, playerId, DateTime.UtcNow);
            _output.WriteLine($"income: best hunting {hourlyXp:N0} XP/h, gold {hourlyGold:N0}/h");
            foreach (int stage in new[] { 1, 5, 10, 15 })
                _output.WriteLine($"stage {stage,2}: XP {DungeonEngine.XpFor(stage, hourlyXp, true):N0}, gold {DungeonEngine.GoldFor(stage, hourlyGold, true):N0} ({DungeonEngine.IncomeMinutes(stage)} min)");
        }

        /// <summary>A run with every telegraph answered one way: "none" = no telegraphs at all.</summary>
        private static DungeonRunResult Play(TickStatePayload payload, DungeonKind kind, int stage, int fish, int[] party, string mode)
        {
            var runner = new DungeonRunner(payload, kind, stage, fish, 9999, party, challenges: mode != "none");
            var rng = new Random(stage * 31 + (int)kind);
            runner.Advance();
            int guard = 0;
            while (runner.Result == null && guard++ < 20)
            {
                if (mode == "missed") runner.Miss(rng);
                else
                {
                    runner.Dodge();
                    // A sensible pick: damage first, then survival, as a player would.
                    var offer = DungeonPerks.Offer(runner.Mods, rng);
                    string[] order = { "fury", "brutal", "keen", "swift", "iron", "vigour", "leech", "wind" };
                    var pick = offer.OrderBy(o => Array.IndexOf(order, o.Id)).FirstOrDefault();
                    if (pick == null || !runner.Choose(pick.Id)) runner.Resume();
                }
                runner.Advance();
            }
            return runner.Result!;
        }
    }
}
