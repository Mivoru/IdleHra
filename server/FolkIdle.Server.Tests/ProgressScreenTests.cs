using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Tasks 56 and 57, the Progress screen: statistics from ten-minute
    /// samples, the collection log, the Lifetime chapter and the hidden deeds.
    /// </summary>
    [Collection("Postgres collection")]
    public class ProgressScreenTests
    {
        private readonly PostgresTestFixture _fixture;

        public ProgressScreenTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private static PlayerStatSample At(DateTime t, long kills, long gold, long xp = 0) =>
            new() { PlayerId = 1, AtUtc = t, Kills = kills, Gold = gold, Xp = xp };

        // ------------------------------------------------------------------
        // Task 56: rates and style (pure).
        // ------------------------------------------------------------------

        [Fact]
        public void ARateIsTheDifferenceOverTheTimeBetweenTheReadings()
        {
            var now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
            var points = new List<PlayerStatSample>
            {
                At(now.AddMinutes(-70), 0, 1000),
                At(now.AddMinutes(-60), 100, 2000),
                At(now.AddMinutes(-30), 150, 1500), // spent 500 in here
                At(now, 220, 4000),
            };

            var hour = StatSampler.RateOver(points, now, "1 hour", 3600);
            Assert.Equal(3600, hour.CoveredSeconds);
            Assert.Equal(120, hour.KillsPerHour);        // 220 - 100 over exactly an hour
            Assert.Equal(2500, hour.GoldEarned);          // only the rise
            Assert.Equal(500, hour.GoldSpent);            // the fall, kept apart

            var tenMinutes = StatSampler.RateOver(points, now, "10 min", 600);
            // No reading exactly ten minutes ago: it measures from the last one
            // before the window (30 min ago), which still covers all of it.
            Assert.Equal(600, tenMinutes.CoveredSeconds);
            Assert.Equal(140, tenMinutes.KillsPerHour);  // 70 kills over half an hour
        }

        [Fact]
        public void ASingleReadingIsNoRateAtAll()
        {
            var now = DateTime.UtcNow;
            var rate = StatSampler.RateOver(new[] { At(now, 5, 5) }, now, "1 hour", 3600);
            Assert.Equal(0, rate.CoveredSeconds);
            Assert.Equal(0, rate.KillsPerHour);
        }

        [Fact]
        public void StyleIsTheShareOfCharacterSamplesPerActivity()
        {
            var now = DateTime.UtcNow;
            var samples = new[]
            {
                new PlayerStatSample { AtUtc = now.AddHours(-1), Fighting = 2, Gathering = 1 },
                new PlayerStatSample { AtUtc = now.AddMinutes(-10), Fighting = 1, Crafting = 1, Idle = 1 },
                new PlayerStatSample { AtUtc = now.AddDays(-3), Gathering = 3 }, // outside 24 h
            };
            var day = StatSampler.StyleOver(samples, now, "24 hours", TimeSpan.FromHours(24));
            Assert.Equal(2, day.Samples);
            Assert.Equal(50, day.FightingPct);
            Assert.Equal(100, day.FightingPct + day.GatheringPct + day.CraftingPct + day.IdlePct);

            var week = StatSampler.StyleOver(samples, now, "7 days", TimeSpan.FromDays(7));
            Assert.Equal(3, week.Samples);
            Assert.True(week.GatheringPct > day.GatheringPct);
        }

        [Fact]
        public void CumulativeXpCountsEveryLevelClimbed()
        {
            Assert.Equal(40, StatSampler.CumulativeXp(1, 40));
            Assert.Equal(ProgressionEngine.GetRequiredXpForLevel(1) + ProgressionEngine.GetRequiredXpForLevel(2) + 7,
                StatSampler.CumulativeXp(3, 7));
        }

        private async Task SeedPlayerAsync(long playerId, long gold = 5_000, int kills = 0)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            foreach (var sql in new[]
            {
                "DELETE FROM player_stat_samples WHERE \"PlayerId\" = {0}",
                "DELETE FROM player_collection WHERE \"PlayerId\" = {0}",
                "DELETE FROM player_lifetime_achievements WHERE \"PlayerId\" = {0}",
                "DELETE FROM player_funnel_events WHERE \"PlayerId\" = {0}",
                "DELETE FROM monster_codex_entries WHERE \"PlayerId\" = {0}",
                "DELETE FROM \"EquipmentInstances\" WHERE \"PlayerId\" = {0}",
                "DELETE FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0}",
                "DELETE FROM \"PlayerRecords\" WHERE \"Id\" = {0}",
            })
            {
                await db.Database.ExecuteSqlRawAsync(sql, playerId);
            }

            db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 3, CurrentXp = 10, PremiumDiamonds = 100 });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = gold });
            if (kills > 0)
            {
                db.MonsterCodexEntries.Add(new MonsterCodexEntry { PlayerId = playerId, MonsterId = ContentRegistry.FirstCanonicalMonsterId, KillCount = kills });
            }
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task TheSamplerWritesOneRowPerPlayerAndPrunesOldOnes()
        {
            const long playerId = 984000001L;
            await SeedPlayerAsync(playerId, gold: 12_345, kills: 77);
            var now = DateTime.UtcNow;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerStatSamples.Add(new PlayerStatSample { PlayerId = playerId, AtUtc = now - StatSampler.Retention - TimeSpan.FromHours(1) });
                await db.SaveChangesAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(1, await StatSampler.SampleAsync(db, new[] { playerId }, now));
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await verify.PlayerStatSamples.AsNoTracking().Where(s => s.PlayerId == playerId).ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(77, row.Kills);
            Assert.Equal(12_345, row.Gold);
            Assert.Equal(StatSampler.CumulativeXp(3, 10), row.Xp);
        }

        [Fact]
        public async Task InsightsCloseTheWindowWithAReadingTakenNow()
        {
            const long playerId = 984000002L;
            await SeedPlayerAsync(playerId, gold: 3_000, kills: 60);
            var now = DateTime.UtcNow;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerStatSamples.Add(new PlayerStatSample { PlayerId = playerId, AtUtc = now.AddMinutes(-30), Kills = 30, Gold = 1_000, Fighting = 1 });
                db.PlayerFunnelEvents.Add(new PlayerFunnelEvent { PlayerId = playerId, Step = (short)FunnelStep.FirstKill, At = now.AddDays(-1) });
                db.PlayerFunnelEvents.Add(new PlayerFunnelEvent { PlayerId = playerId, Step = (short)FunnelStep.Registered, At = now.AddDays(-2) });
                await db.SaveChangesAsync();
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var view = await StatSampler.BuildInsightsAsync(read, playerId, now);

            var hour = view.Rates.Single(r => r.Window == "1 hour");
            Assert.Equal(60, hour.KillsPerHour, 0);       // 30 kills in 30 minutes
            Assert.Equal(2_000, hour.GoldEarned);
            Assert.Equal(new[] { "Arrived in the valley", "First kill" }, view.Timeline.Select(t => t.Text).ToArray());
            Assert.Equal(100, view.Style.Single(s => s.Window == "24 hours").FightingPct);
        }

        // ------------------------------------------------------------------
        // Task 57: the collection log.
        // ------------------------------------------------------------------

        [Fact]
        public async Task TheCollectionOnlyEverRisesAndRemembersWhatWasSold()
        {
            const long playerId = 984000003L;
            await SeedPlayerAsync(playerId);
            string piece = "eq_linen_hood_helmet_armor_slot_base";
            var now = DateTime.UtcNow;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await CollectionLog.RecordAsync(db, playerId, new Dictionary<string, int> { [piece] = 6 }, now);
                await CollectionLog.RecordAsync(db, playerId, new Dictionary<string, int> { [piece] = 3 }, now);
                // Not collectable: a material and an unknown id are skipped.
                await CollectionLog.RecordAsync(db, playerId, new Dictionary<string, int> { ["birch_log"] = 9, ["eq_nothing"] = 9 }, now);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var rows = await db.PlayerCollectionEntries.AsNoTracking().Where(c => c.PlayerId == playerId).ToListAsync();
                var row = Assert.Single(rows);
                Assert.Equal(6, row.BestTier);
            }

            // Something in the Chest now is folded in on read.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.EquipmentInstances.Add(new EquipmentInstance { PlayerId = playerId, BaseItemId = "eq_steel_helm_helmet_armor_slot_base", QualityTier = 9, AffixPayload = "{}" });
                await db.SaveChangesAsync();
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var view = await CollectionLog.BuildAsync(read, playerId, now);
            Assert.Equal(75, view.PiecesTotal);
            Assert.Equal(2, view.PiecesOwned);
            var regionOne = view.Regions.Single(r => r.Region == 1);
            Assert.Equal(15, regionOne.PiecesTotal);
            Assert.Equal(6, regionOne.Pieces.Single(p => p.BaseItemId == piece).BestTier);
            Assert.Equal(9, regionOne.Pieces.Single(p => p.BaseItemId == "eq_steel_helm_helmet_armor_slot_base").BestTier);
            Assert.All(view.Regions, r => Assert.Equal(ContentRegistry.MonstersPerRegion, r.MonstersTotal));
        }

        // ------------------------------------------------------------------
        // Task 57: the Lifetime chapter and the hidden deeds.
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(AchievementMilestones.TreasuryAchievementId)]
        [InlineData(AchievementMilestones.ForgingAchievementId)]
        [InlineData(AchievementMilestones.LogisticsAchievementId)]
        public void TheBookQuotesExactlyWhatTheCheckpointPays(int id)
        {
            var tiers = AchievementMilestones.TiersFor(id);
            Assert.Equal(4, tiers.Count);
            for (int n = 1; n <= tiers.Count; n++)
            {
                Assert.Equal(AchievementMilestones.GetDiamondsForTiersCrossed(id, 0, n), tiers.Take(n).Sum(t => t.Diamonds));
            }
            Assert.Equal(tiers.Count, tiers.Select(t => t.Name).Distinct().Count());
        }

        [Fact]
        public async Task MonsterSlayerPaysOnceAtTenThousandKills()
        {
            const long playerId = 984000004L;
            await SeedPlayerAsync(playerId);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var early = await LifetimeAchievementBank.BankMonsterSlayerAsync(db, playerId, AchievementMilestones.MonsterKillThreshold - 1);
                Assert.Equal(0, early.Paid);
            }

            var results = new List<int>();
            for (int i = 0; i < 3; i++)
            {
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                results.Add((await LifetimeAchievementBank.BankMonsterSlayerAsync(db, playerId, AchievementMilestones.MonsterKillThreshold)).Paid);
            }
            Assert.Equal(new[] { AchievementMilestones.MonsterKillReward, 0, 0 }, results.ToArray());

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
            Assert.Equal(100 + AchievementMilestones.MonsterKillReward, player.PremiumDiamonds);
            var view = await LifetimeAchievementBank.ReadAsync(verify, playerId, AchievementMilestones.MonsterKillThreshold);
            var slayer = view.Single(v => v.Id == AchievementMilestones.MonsterKillAchievementId);
            Assert.True(slayer.Tiers.Single().Reached);
            Assert.Equal(4, view.Count);
        }

        [Fact]
        public void AHiddenDeedIsQuestionMarksUntilItIsDone()
        {
            var fresh = default(DeedContext);
            var veteran = fresh with { TotalDeaths = 150, DelveDeepestFloor = 25, BestHit = 200_000, HighestRarityOwned = 14, ChildrenBred = 12, DefeatedRegionBossMask = 0b11111 };

            Assert.NotEmpty(DeedRegistry.Hidden);
            Assert.Equal(DeedRegistry.Hidden.Count, DeedRegistry.Hidden.Select(h => h.Id).Distinct().Count());
            foreach (var deed in DeedRegistry.Hidden)
            {
                Assert.True(deed.Progress(fresh) < deed.Target, $"{deed.Id} is done on a brand-new account");
                Assert.True(deed.Progress(veteran) >= deed.Target, $"{deed.Id} cannot be finished");
                Assert.False(string.IsNullOrWhiteSpace(deed.Category));
            }
        }
    }
}
