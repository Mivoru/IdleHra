using System;
using System.Collections.Concurrent;
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
    /// Calibration instrument, not a regression test: loads a REAL character
    /// from a restored production copy and fights every region boss at every
    /// Ascension step with the live tick, then repeats it for a maxed endgame
    /// profile built on the same character. Skipped unless
    /// FOLKIDLE_CALIBRATION_DB holds a connection string (and
    /// FOLKIDLE_CALIBRATION_PLAYER a player id).
    ///
    /// Modul: why a real character. The owner beat Malakor at Ascension 10 on
    /// 2026-10-07 with no rebirth, partial inheritance and mixed gear, while
    /// the ladder had been tuned against BossGearBenchmark's reference
    /// loadouts. The reference models what gear does; it does not model
    /// affixes rolled high, skill points, a bloodline, a set. Calibrating on
    /// the account that broke the ladder is the only anchor that cannot be
    /// argued with.
    /// </summary>
    public class AscensionCalibrationHarness
    {
        private readonly ITestOutputHelper _output;

        public AscensionCalibrationHarness(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static string? Conn => Environment.GetEnvironmentVariable("FOLKIDLE_CALIBRATION_DB");

        [Fact]
        public async Task Print_TheLadderAgainstARealCharacter()
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
            var manager = new StateCheckpointManager(services.BuildServiceProvider());

            TickStatePayload real = await manager.LoadPlayerState(playerId);
            _output.WriteLine($"player {playerId}: level {real.CurrentLevel}, rebirths {real.RenownedRebirths}, inherit dmg {real.Inherit_Damage} hp {real.Inherit_MaxHp}");

            TickStatePayload maxed = real;
            maxed.Inherit_Damage = 20;
            maxed.Inherit_MaxHp = 20;
            maxed.RenownedRebirths = 12;
            // Skills and aptitudes stay the real character's: several boughs
            // trade defence for offence, and forcing every one to the cap made
            // the "maxed" profile die where the real one lived.
            // Field by field, the better of the real gear and a Transcendent
            // region-5 Legendary reference: a maxed profile must dominate the real
            // one in every stat, or it can lose where the real one wins.
            object best = real.CachedAffixTotals;
            object reference = BossGearBenchmark.BuildEquippedTotals(
                new ReferenceLoadout(real.CurrentLevel, 5, RarityTier.Transcendent, AffixRarity.Legendary));
            foreach (var f in typeof(EquippedAffixTotals).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (f.FieldType == typeof(int)) f.SetValue(best, Math.Max((int)f.GetValue(best)!, (int)f.GetValue(reference)!));
                else if (f.FieldType == typeof(float)) f.SetValue(best, Math.Max((float)f.GetValue(best)!, (float)f.GetValue(reference)!));
                else if (f.FieldType == typeof(long)) f.SetValue(best, Math.Max((long)f.GetValue(best)!, (long)f.GetValue(reference)!));
                else if (f.FieldType == typeof(double)) f.SetValue(best, Math.Max((double)f.GetValue(best)!, (double)f.GetValue(reference)!));
            }
            maxed.CachedAffixTotals = (EquippedAffixTotals)best;

            // The headroom: the largest attack multiplier (health rising as
            // attack^HpExponent, the split the ladder uses) each profile still
            // beats, per boss. This is what the ladder is solved against.
            foreach (var (name, profile) in new[] { ("real", real), ("maxed", maxed) })
            {
                var line = new System.Text.StringBuilder($"headroom {name,-6}:");
                for (int region = BossAscensionRegistry.FirstRegion; region <= BossAscensionRegistry.LastRegion; region++)
                {
                    double lo = 1.0, hi = 1.0;
                    if (!Beats(profile, region, 1.0)) { line.Append($" r{region}=<1"); continue; }
                    while (hi < 1e5 && Beats(profile, region, hi)) { lo = hi; hi *= 2; }
                    for (int i = 0; i < 12; i++) { double mid = Math.Sqrt(lo * hi); if (Beats(profile, region, mid)) lo = mid; else hi = mid; }
                    line.Append($" r{region}=x{lo:F2}");
                }
                _output.WriteLine(line.ToString());
            }

            foreach (var (name, profile) in new[] { ("real", real), ("maxed", maxed) })
            {
                for (int region = BossAscensionRegistry.FirstRegion; region <= BossAscensionRegistry.LastRegion; region++)
                {
                    var line = new System.Text.StringBuilder($"{name,-6} r{region}:");
                    for (int step = 0; step <= BossAscensionRegistry.MaxStep; step++)
                    {
                        var (won, seconds, limit) = Fight(profile, region, step);
                        line.Append(won ? $" A{step}={seconds:F0}s" : $" A{step}=X");
                        if (step > 0 && limit > 0 && won && seconds > limit) line.Append($"(>{limit})");
                    }
                    _output.WriteLine(line.ToString());
                }
            }
        }

        private const double HpExponent = 0.68;

        private static bool Beats(TickStatePayload payload, int region, double attackMultiplier)
        {
            int atkPct = (int)Math.Round((attackMultiplier - 1.0) * 100.0);
            int hpPct = (int)Math.Round((Math.Pow(attackMultiplier, HpExponent) - 1.0) * 100.0);
            BossAscensionRegistry.CalibrationOverride = new AscensionModifiers(atkPct, hpPct, 0);
            try
            {
                return Fight(payload, region, 1).Won;
            }
            finally
            {
                BossAscensionRegistry.CalibrationOverride = null;
            }
        }

        /// <summary>The live tick until the boss falls, the character dies, or 30 minutes pass.</summary>
        private static (bool Won, double Seconds, int Limit) Fight(TickStatePayload payload, int region, int step)
        {
            int boss = RaceUnlockRegistry.GetRegionBossMonsterId(region);
            payload.ActiveActivityId = boss;
            payload.CurrentMonsterId = 0;
            payload.CurrentMonsterHp = 0;
            payload.ActivityHaltReason = 0;
            payload.AutoEatThreshold = 60;
            payload.Food1_ItemId = FoodRegistry.FirstRawFishOfTier(5);
            payload.Food1_Count = 9_999;
            payload.Food2_Count = 0;
            payload.Food3_Count = 0;
            payload.AscensionRegion = (byte)region;
            payload.AscensionStep = (byte)step;
            payload.AscensionCharacterId = payload.Slot1_CharacterId;
            payload.DefeatedRegionBossMask = 0x1F;
            var stats = SimulationEngine.LiveCombatStats(in payload);
            payload.PlayerHp = (int)SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);

            var guildWar = new ConcurrentQueue<GuildWarPointEvent>();
            var sessions = new ConcurrentDictionary<long, LiveSessionContext>();
            int limit = step > 0 ? BossAscensionRegistry.TimeLimitSecondsFor(region, step) : 0;
            try
            {
                for (int tick = 1; tick <= 18_000; tick++)
                {
                    // Every kill moves the Golden Fleece counter (up, or back to
                    // zero on a fleece) - a kill signal that a one-tick fight
                    // cannot hide, unlike a health comparison.
                    int killsBefore = payload.KillsSinceFleece;
                    SimulationEngine.RunCombatTick(ref payload, 100, 100, guildWar, sessions);
                    if (payload.KillsSinceFleece != killsBefore) return (true, tick / 10.0, limit);
                    if (payload.ActiveActivityId == 0 || payload.PlayerHp <= 0) return (false, tick / 10.0, limit);
                }
                return (false, 1800, limit);
            }
            finally
            {
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            }
        }
    }
}
