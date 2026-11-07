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
    /// Build presets (owner, 2026-11-07): saving stores the current War ranks,
    /// applying goes through the real respec and commit, and a preset that no
    /// longer fits is refused before anything is spent.
    /// </summary>
    [Collection("Postgres collection")]
    public class BuildPresetTests
    {
        private readonly PostgresTestFixture _fixture;

        public BuildPresetTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private async Task<long> CreatePlayerAsync(int skillPoints, int level = 30)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"preset_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = level,
                AvailableSkillPoints = skillPoints,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        /// <summary>Single-rank targets the commit accepts from an empty tree, cheapest first.</summary>
        private static List<byte[]> OneRankTargets()
        {
            var list = new List<byte[]>();
            foreach (var node in WarTreeRegistry.All())
            {
                var target = new byte[WarTreeRegistry.Slots];
                target[node.Id] = 1;
                if (WarTreeRegistry.PlanReason(new byte[WarTreeRegistry.Slots], target, 1000, out _) == null) list.Add(target);
            }
            return list;
        }

        private async Task<byte[]> RanksAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await SkillTreeEngine.LoadRanksAsync(db, playerId);
        }

        private async Task<PlayerRecord> PlayerAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
        }

        [Fact]
        public void TheRankEncoding_RoundTrips_AndDropsWhatIsNotAWarNode()
        {
            var target = OneRankTargets()[0];
            string encoded = BuildPresetRules.EncodeWarRanks(target);
            Assert.Equal(target, BuildPresetRules.DecodeWarRanks(encoded));
            Assert.Equal(new byte[WarTreeRegistry.Slots], BuildPresetRules.DecodeWarRanks("0:5,999:1,junk,3:x"));
            Assert.Equal("Build 2", BuildPresetRules.CleanName("   ", 2));
            Assert.Equal(BuildPresetRules.MaxNameLength, BuildPresetRules.CleanName(new string('a', 60), 1).Length);
        }

        [Fact]
        public async Task SavingStoresTheCurrentTree_AndAppliesWithACommitWhenItOnlyAdds()
        {
            var targets = OneRankTargets();
            long playerId = await CreatePlayerAsync(skillPoints: 100);
            var engine = new BuildPresetEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            var tree = new SkillTreeEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());

            Assert.Null((await tree.CommitAsync(playerId, targets[0])).Reason);
            var saved = await engine.SaveAsync(playerId, 1, "Glass cannon", 10, 20, 0, 5);
            Assert.NotNull(saved);
            Assert.Equal("Glass cannon", saved!.Name);
            Assert.Equal(1, saved.Levels[Array.IndexOf(targets[0], (byte)1)]);
            Assert.Equal(20, saved.DEX);

            // Respec back to nothing (the free respec), then apply: only adds, so no respec is spent.
            Assert.Null((await tree.RespecAsync(playerId)).Reason);
            var applied = await engine.ApplyTreeAsync(playerId, 1, payWithGold: false);
            Assert.Null(applied.Reason);
            Assert.True(applied.Changed);
            Assert.False(applied.Respecced);
            var held1 = await RanksAsync(playerId);
            Assert.True(BuildPresetRules.SameWar(targets[0], held1));

            // Applying again changes nothing.
            var again = await engine.ApplyTreeAsync(playerId, 1, payWithGold: false);
            Assert.Null(again.Reason);
            Assert.False(again.Changed);

            var list = await engine.ListAsync(playerId);
            Assert.Single(list);
            Assert.True(await engine.DeleteAsync(playerId, 1));
            Assert.Empty(await engine.ListAsync(playerId));
        }

        [Fact]
        public async Task ADifferentTree_IsRespeccedWithTheFreeRespec_ThenBought()
        {
            var targets = OneRankTargets();
            Assert.True(targets.Count >= 2, "the tree has fewer than two first-row nodes");
            long playerId = await CreatePlayerAsync(skillPoints: 100);
            var engine = new BuildPresetEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            var tree = new SkillTreeEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());

            // Save build B, then hold build A.
            Assert.Null((await tree.CommitAsync(playerId, targets[1])).Reason);
            await engine.SaveAsync(playerId, 2, "B", 0, 0, 0, 0);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // Put the player on A without spending the free respec.
                await db.PlayerSkillTreeNodes.Where(r => r.PlayerId == playerId).ExecuteDeleteAsync();
            }
            Assert.Null((await tree.CommitAsync(playerId, targets[0])).Reason);
            int pointsBefore = (await PlayerAsync(playerId)).AvailableSkillPoints;

            var applied = await engine.ApplyTreeAsync(playerId, 2, payWithGold: false);

            Assert.Null(applied.Reason);
            Assert.True(applied.Respecced);
            Assert.Equal(0L, applied.GoldSpent);
            var held2 = await RanksAsync(playerId);
            Assert.True(BuildPresetRules.SameWar(targets[1], held2));
            var player = await PlayerAsync(playerId);
            Assert.True(player.FreeRespecUsed);
            // A's cost came back and B's was paid.
            Assert.Equal(pointsBefore + WarTreeRegistry.PointsSpent(targets[0]) - WarTreeRegistry.PointsSpent(targets[1]),
                player.AvailableSkillPoints);
        }

        [Fact]
        public async Task ABuildThatNoLongerFits_IsRefusedBeforeTheRespec()
        {
            var targets = OneRankTargets();
            long playerId = await CreatePlayerAsync(skillPoints: 100);
            var engine = new BuildPresetEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            var tree = new SkillTreeEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());

            int costA = WarTreeRegistry.PointsSpent(targets[0]);
            int owed = -costA;
            Assert.Null((await tree.CommitAsync(playerId, targets[1])).Reason);
            await engine.SaveAsync(playerId, 3, "B", 0, 0, 0, 0);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // A rebirth-like state: a different node held, and no points to spare.
                await db.PlayerSkillTreeNodes.Where(r => r.PlayerId == playerId).ExecuteDeleteAsync();
                await db.PlayerRecords.Where(p => p.Id == playerId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.AvailableSkillPoints, costA));
            }
            Assert.Null((await tree.CommitAsync(playerId, targets[0])).Reason);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // Fewer points than build B costs, even with A refunded.
                await db.PlayerRecords.Where(p => p.Id == playerId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.AvailableSkillPoints, owed));
            }

            var applied = await engine.ApplyTreeAsync(playerId, 3, payWithGold: false);

            Assert.NotNull(applied.Reason);
            Assert.StartsWith("This build no longer fits", applied.Reason);
            Assert.False(applied.Respecced);
            Assert.False((await PlayerAsync(playerId)).FreeRespecUsed);
            var held3 = await RanksAsync(playerId);
            Assert.True(BuildPresetRules.SameWar(targets[0], held3));
        }

        [Fact]
        public async Task AnEmptySlot_IsRefused()
        {
            long playerId = await CreatePlayerAsync(skillPoints: 10);
            var engine = new BuildPresetEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            Assert.Equal("That preset is empty.", (await engine.ApplyTreeAsync(playerId, 1, false)).Reason);
            Assert.Equal("No such preset.", (await engine.ApplyTreeAsync(playerId, 9, false)).Reason);
            Assert.Null(await engine.SaveAsync(playerId, 0, "x", 0, 0, 0, 0));
        }
    }
}
