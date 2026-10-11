using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE LOGISTICS ACHIEVEMENT COULD NOT BE EARNED (task 116,
    /// 2026-10-11). The harvest counter was per character and never loaded,
    /// so every session restarted it at zero and the checkpoint overwrote the
    /// achievement row with that session's count - production's best of 133
    /// accounts was 169 harvests against a first tier of 10,000, and
    /// Progress -> Insights read 0 harvests an hour for everyone. The forge
    /// upgrade counter had the same missing load. These pin the round trip.
    /// </summary>
    [Collection("Postgres collection")]
    public class LifetimeCounterPersistenceTests
    {
        private readonly PostgresTestFixture _fixture;

        public LifetimeCounterPersistenceTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task SeedAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 10 });
            await db.SaveChangesAsync();
        }

        private async Task<long> ProgressAsync(long playerId, int achievementId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerLifetimeAchievements.AsNoTracking()
                .Where(a => a.PlayerId == playerId && a.AchievementId == achievementId)
                .Select(a => a.CurrentProgress)
                .FirstOrDefaultAsync();
        }

        [Fact]
        public async Task HarvestsAndForgeUpgradesAccumulateAcrossSessions()
        {
            const long playerId = 950_116_001L;
            await SeedAsync(playerId);
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);

            var first = await checkpoints.LoadPlayerState(playerId);
            first.LifetimeHarvests += 6_000;
            first.ForgeUpgradeCount += 7;
            first.IsDirty = true;
            Assert.True(await checkpoints.FlushState(first));

            var second = await checkpoints.LoadPlayerState(playerId);
            Assert.Equal(6_000L, second.LifetimeHarvests);
            Assert.Equal(7, second.ForgeUpgradeCount);

            second.LifetimeHarvests += 5_000;
            second.IsDirty = true;
            Assert.True(await checkpoints.FlushState(second));

            Assert.Equal(11_000L, await ProgressAsync(playerId, AchievementMilestones.LogisticsAchievementId));
            Assert.Equal(7L, await ProgressAsync(playerId, AchievementMilestones.ForgingAchievementId));
            // 11,000 crosses the first Logistics tier; neither session alone did.
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var row = await db.PlayerLifetimeAchievements.AsNoTracking()
                .FirstAsync(a => a.PlayerId == playerId && a.AchievementId == AchievementMilestones.LogisticsAchievementId);
            Assert.Equal(1, row.CompletedTier);
        }

        /// <summary>
        /// A payload loaded before the row last moved must not write its
        /// smaller count back over the newer one.
        /// </summary>
        [Fact]
        public async Task AStalePayloadDoesNotLowerTheCount()
        {
            const long playerId = 950_116_002L;
            await SeedAsync(playerId);
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);

            var stale = await checkpoints.LoadPlayerState(playerId);
            var fresh = await checkpoints.LoadPlayerState(playerId);
            fresh.LifetimeHarvests = 400;
            fresh.IsDirty = true;
            Assert.True(await checkpoints.FlushState(fresh));

            stale.LifetimeHarvests = 30;
            stale.IsDirty = true;
            // The epoch gate refuses this flush today; Math.Max is the second
            // line, so either outcome must leave the newer count standing.
            await checkpoints.FlushState(stale);

            Assert.Equal(400L, await ProgressAsync(playerId, AchievementMilestones.LogisticsAchievementId));
        }

        /// <summary>The offline projection raises the account counter by the harvests it completed.</summary>
        [Fact]
        public void OfflineGatheringCountsTowardTheLifetimeTotal()
        {
            ContentRegistry.Initialize();
            Assert.True(ContentRegistry.TryGetGatheringNode(ActivityIdBands.WoodcuttingBand + 1, out GatheringNodeDefinition node));
            var payload = new TickStatePayload { PlayerId = 1, LifetimeHarvests = 100 };

            var projection = OfflineSimulationEngine.CalculateGatheringProjection(ref payload, node, 3_600, new Random(116));

            Assert.True(projection.Actions > 0);
            Assert.Equal(100 + projection.Actions, payload.LifetimeHarvests);
        }
    }
}
