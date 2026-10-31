using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The diamond rework (owner M1, I1, A2 - 2026-10-31): the one reward table,
    /// the migration that moves stored tiers and inheritance levels without
    /// paying twice or taking anything back, and the one economy model.
    /// </summary>
    [Collection("Postgres collection")]
    public class DiamondReworkTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public DiamondReworkTests(PostgresTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Fact]
        public void NoTierPaysMoreThanALegendaryAndEveryTierPaysABand()
        {
            int[] bands = { AchievementMilestones.BronzeDiamonds, AchievementMilestones.SilverDiamonds, AchievementMilestones.GoldDiamonds, AchievementMilestones.LegendaryDiamonds };
            foreach (int id in AchievementMilestones.LifetimeAchievementIds)
            {
                foreach (var tier in AchievementMilestones.TiersFor(id))
                {
                    Assert.Contains(tier.Diamonds, bands);
                }
            }
        }

        [Fact]
        public void TheMigrationMapsEveryOldThresholdToItsTierNow()
        {
            // The old four-tier ladders' thresholds, evaluated on the new ones.
            long[] oldTreasury = { 100_000L, 5_000_000L, 100_000_000L, 2_500_000_000L };
            for (int k = 1; k <= 4; k++)
                Assert.Equal(AchievementMilestones.EvaluateTreasuryTier(oldTreasury[k - 1]), AchievementMilestones.MigratedTier(AchievementMilestones.TreasuryAchievementId, k));

            Assert.Equal(AchievementMilestones.EvaluateForgingTier(50, 0), AchievementMilestones.MigratedTier(AchievementMilestones.ForgingAchievementId, 1));
            Assert.Equal(AchievementMilestones.EvaluateForgingTier(500, 0), AchievementMilestones.MigratedTier(AchievementMilestones.ForgingAchievementId, 2));
            Assert.Equal(AchievementMilestones.EvaluateForgingTier(0, 10), AchievementMilestones.MigratedTier(AchievementMilestones.ForgingAchievementId, 3));
            Assert.Equal(AchievementMilestones.EvaluateForgingTier(0, 14), AchievementMilestones.MigratedTier(AchievementMilestones.ForgingAchievementId, 4));

            // Already paid stays paid: a migrated tier crossed again pays nothing.
            Assert.Equal(0, AchievementMilestones.GetDiamondsForTiersCrossed(AchievementMilestones.TreasuryAchievementId, 7, 7));
        }

        [Fact]
        public async Task TheMigrationSqlDoesWhatTheRulesSay()
        {
            const long playerId = 950_310_001L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.PlayerLifetimeAchievements.Add(new PlayerLifetimeAchievement { PlayerId = playerId, AchievementId = AchievementMilestones.TreasuryAchievementId, CompletedTier = 2 });
                db.PlayerLifetimeAchievements.Add(new PlayerLifetimeAchievement { PlayerId = playerId, AchievementId = AchievementMilestones.ForgingAchievementId, CompletedTier = 3 });
                db.PlayerLifetimeAchievements.Add(new PlayerLifetimeAchievement { PlayerId = playerId, AchievementId = AchievementMilestones.LogisticsAchievementId, CompletedTier = 2 });
                for (int stat = 0; stat < 4; stat++)
                    db.PlayerInheritanceStats.Add(new PlayerInheritanceStat { PlayerId = playerId, StatId = stat, Level = new[] { 0, 1, 11, 20 }[stat] });
                await db.SaveChangesAsync();

                // Only this player's rows move in the shared test database.
                foreach (string sql in new[] { DiamondReworkSql.RemapTreasury, DiamondReworkSql.RemapForging, DiamondReworkSql.HalveInheritance })
                    await db.Database.ExecuteSqlRawAsync(sql.TrimEnd().TrimEnd(';') + $" AND \"PlayerId\" = {playerId};");
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var tiers = await verify.PlayerLifetimeAchievements.AsNoTracking().Where(a => a.PlayerId == playerId).ToDictionaryAsync(a => a.AchievementId, a => a.CompletedTier);
            Assert.Equal(AchievementMilestones.MigratedTier(AchievementMilestones.TreasuryAchievementId, 2), tiers[AchievementMilestones.TreasuryAchievementId]);
            Assert.Equal(AchievementMilestones.MigratedTier(AchievementMilestones.ForgingAchievementId, 3), tiers[AchievementMilestones.ForgingAchievementId]);
            Assert.Equal(2, tiers[AchievementMilestones.LogisticsAchievementId]);

            var levels = await verify.PlayerInheritanceStats.AsNoTracking().Where(s => s.PlayerId == playerId).OrderBy(s => s.StatId).Select(s => s.Level).ToListAsync();
            Assert.Equal(new[] { 0, 1, 6, 10 }, levels);
            // Never less bonus than before: old levels were worth 2% each.
            int[] old = { 0, 1, 11, 20 };
            for (int i = 0; i < old.Length; i++)
                Assert.True(InheritanceRegistry.GetBonusPct(levels[i]) >= old[i] * 2);
        }

        /// <summary>
        /// The one model (owner I2): free diamonds a week against the price of
        /// each inheritance step and the one-off achievement total.
        /// </summary>
        [Fact]
        public void TheEconomyModel_Report()
        {
            int weekly = DelveRegistry.MaxDiamondsPerWeek + DelveRegistry.MaxDeepDiamondsPerWeek;
            long toFive = 0, toTen = 0;
            for (int level = 0; level < InheritanceRegistry.MaxLevel; level++)
            {
                long cost = InheritanceRegistry.GetUpgradeCost(level);
                if (level < 5) toFive += cost;
                toTen += cost;
            }
            long allSix = toTen * InheritanceRegistry.StatCount;
            int achievements = AchievementMilestones.LifetimeAchievementIds.Sum(id => AchievementMilestones.TiersFor(id).Sum(t => t.Diamonds));

            var report = new StringBuilder();
            report.AppendLine($"free diamonds a week (Delve {DelveRegistry.MaxDiamondsPerWeek} + Deep {DelveRegistry.MaxDeepDiamondsPerWeek}): {weekly}");
            report.AppendLine($"first level: {InheritanceRegistry.GetUpgradeCost(0)} ({InheritanceRegistry.GetUpgradeCost(0) / (double)weekly:F1} weeks)");
            report.AppendLine($"one stat to level 5: {toFive} ({toFive / (double)weekly:F1} weeks)");
            report.AppendLine($"one stat to level 10: {toTen} ({toTen / (double)weekly:F1} weeks)");
            report.AppendLine($"all six to level 10: {allSix} ({allSix / (double)weekly / 52:F1} years)");
            report.AppendLine($"every lifetime achievement tier, once: {achievements} diamonds ({achievements / (double)weekly:F1} weeks of taps)");
            _output.WriteLine(report.ToString());

            // A step a week to begin with, a felt goal in a couple of months, and
            // still a sink measured in years when everything is wanted.
            Assert.True(InheritanceRegistry.GetUpgradeCost(0) <= weekly);
            Assert.InRange(toFive / (double)weekly, 4, 12);
            Assert.True(allSix / (double)weekly / 52 >= 2.0);
        }
    }
}
