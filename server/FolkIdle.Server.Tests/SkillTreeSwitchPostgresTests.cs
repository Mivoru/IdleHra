using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Rework slice 2.5, against a real Postgres: the switch refunds today's
    /// tree on load, exactly once, and the War half commits, respecs and sells
    /// a respec for gold.
    /// </summary>
    [Collection("Postgres collection")]
    public class SkillTreeSwitchPostgresTests
    {
        private readonly PostgresTestFixture _fixture;

        public SkillTreeSwitchPostgresTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<long> CreatePlayerAsync(int points, int level, bool freeRespecUsed, long gold = 0)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"tree_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                AvailableSkillPoints = points,
                CurrentLevel = level,
                FreeRespecUsed = freeRespecUsed,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            if (gold > 0)
            {
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = gold });
                await db.SaveChangesAsync();
            }
            return player.Id;
        }

        [Fact]
        public async Task The_switch_refunds_todays_tree_once_and_grants_a_free_respec()
        {
            long playerId = await CreatePlayerAsync(points: 3, level: 40, freeRespecUsed: true);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerSkillTreeNodes.Add(new PlayerSkillTreeNode { PlayerId = playerId, BranchId = SkillTreeRegistry.BranchCritChance, Level = 10 });
                db.PlayerSkillTreeNodes.Add(new PlayerSkillTreeNode { PlayerId = playerId, BranchId = SkillTreeRegistry.BoughGuile, Level = 3 });
                await db.SaveChangesAsync();
            }
            int expected = SkillTreeEngine.LegacyRefundOf(new[] { (SkillTreeRegistry.BranchCritChance, 10), (SkillTreeRegistry.BoughGuile, 3) });
            Assert.True(expected > 0);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                Assert.Equal(expected, await SkillTreeEngine.ReconcileWarSwitchAsync(db, playerId));
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                Assert.Equal(0, await SkillTreeEngine.ReconcileWarSwitchAsync(db, playerId));

            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await check.PlayerRecords.SingleAsync(p => p.Id == playerId);
            Assert.Equal(3 + expected, player.AvailableSkillPoints);
            Assert.False(player.FreeRespecUsed);
            Assert.False(await check.PlayerSkillTreeNodes.AnyAsync(n => n.PlayerId == playerId));
        }

        [Fact]
        public async Task Several_keystones_are_all_refunded_once_on_load_and_one_is_left_alone()
        {
            long many = await CreatePlayerAsync(points: 2, level: 90, freeRespecUsed: true);
            long one = await CreatePlayerAsync(points: 2, level: 90, freeRespecUsed: true);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                foreach (int id in new[] { WarTreeRegistry.Ruin, WarTreeRegistry.PerfectSequence, WarTreeRegistry.GoldenFleece })
                    db.PlayerSkillTreeNodes.Add(new PlayerSkillTreeNode { PlayerId = many, BranchId = id, Level = 1 });
                db.PlayerSkillTreeNodes.Add(new PlayerSkillTreeNode { PlayerId = many, BranchId = WarTreeRegistry.Edge, Level = 5 });
                db.PlayerSkillTreeNodes.Add(new PlayerSkillTreeNode { PlayerId = one, BranchId = WarTreeRegistry.Ruin, Level = 1 });
                await db.SaveChangesAsync();
            }
            int keystoneCost = WarTreeRegistry.Get(WarTreeRegistry.Ruin)!.CostPerRank;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(3 * keystoneCost, await SkillTreeEngine.ReconcileSingleKeystoneAsync(db, many));
                Assert.Equal(0, await SkillTreeEngine.ReconcileSingleKeystoneAsync(db, one));
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                Assert.Equal(0, await SkillTreeEngine.ReconcileSingleKeystoneAsync(db, many));

            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            var p = await check.PlayerRecords.SingleAsync(x => x.Id == many);
            Assert.Equal(2 + 3 * keystoneCost, p.AvailableSkillPoints);
            Assert.False(p.FreeRespecUsed);
            var left = await check.PlayerSkillTreeNodes.Where(n => n.PlayerId == many).Select(n => n.BranchId).ToListAsync();
            Assert.Equal(new[] { WarTreeRegistry.Edge }, left);
            var untouched = await check.PlayerRecords.SingleAsync(x => x.Id == one);
            Assert.Equal(2, untouched.AvailableSkillPoints);
            Assert.True(untouched.FreeRespecUsed);
            Assert.True(await check.PlayerSkillTreeNodes.AnyAsync(n => n.PlayerId == one && n.BranchId == WarTreeRegistry.Ruin));
        }

        [Fact]
        public void A_second_keystone_is_blocked_and_a_plan_cannot_buy_two()
        {
            var levels = new byte[WarTreeRegistry.Slots];
            levels[WarTreeRegistry.Ruin] = 1;
            string? reason = WarTreeRegistry.BlockedReason(WarTreeRegistry.PerfectSequence, levels, 500);
            Assert.NotNull(reason);
            Assert.Contains("Ruin", reason);
            Assert.Contains("Only one keystone", reason);

            // A plan that fills two columns to their keystones stops at the second.
            var current = new byte[WarTreeRegistry.Slots];
            var target = new byte[WarTreeRegistry.Slots];
            foreach (var n in WarTreeRegistry.All())
                if (!WarTreeRegistry.IsPending(n.Id) && n.Fork < n.Id && (n.Column == WarTreeRegistry.ColumnMight || n.Column == WarTreeRegistry.ColumnPrecision) && n.Kind != "Bridge")
                    target[n.Id] = (byte)n.MaxLevel;
            string? plan = WarTreeRegistry.PlanReason(current, target, 500, out _);
            Assert.NotNull(plan);
            Assert.Contains("Only one keystone", plan);

            // One keystone alone is still fine.
            foreach (var n in WarTreeRegistry.All())
                if (n.Column == WarTreeRegistry.ColumnPrecision) target[n.Id] = 0;
            Assert.Null(WarTreeRegistry.PlanReason(current, target, 500, out _));
        }

        [Fact]
        public async Task A_root_bought_past_the_old_cap_is_refunded_at_the_curve_it_was_bought_at()
        {
            // Levels 10-13 of a root under the old cap of 20 cost 3, 3, 3, 3.
            int capped = SkillTreeEngine.LegacyRefundOf(new[] { (SkillTreeRegistry.BranchCritChance, 10) });
            int over = SkillTreeEngine.LegacyRefundOf(new[] { (SkillTreeRegistry.BranchCritChance, 14) });
            Assert.Equal(capped + 12, over);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task A_war_draft_commits_and_the_free_respec_takes_it_back()
        {
            long playerId = await CreatePlayerAsync(points: 10, level: 20, freeRespecUsed: false);
            var engine = new SkillTreeEngine(_fixture.ServiceProvider);

            var target = new byte[WarTreeRegistry.Slots];
            target[WarTreeRegistry.Edge] = 3;
            target[WarTreeRegistry.KeenEye] = 2;
            var commit = await engine.CommitAsync(playerId, target);
            Assert.Null(commit.Reason);
            Assert.Equal(5, commit.Spent);

            // An old-tree id is not a node any more.
            var stale = (byte[])target.Clone();
            stale[SkillTreeRegistry.BranchCritChance] = 1;
            Assert.NotNull((await engine.CommitAsync(playerId, stale)).Reason);

            var respec = await engine.RespecAsync(playerId);
            Assert.Null(respec.Reason);
            Assert.Equal(5, respec.Refunded);

            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await check.PlayerRecords.SingleAsync(p => p.Id == playerId);
            Assert.Equal(10, player.AvailableSkillPoints);
            Assert.True(player.FreeRespecUsed);
            Assert.False(await check.PlayerSkillTreeNodes.AnyAsync(n => n.PlayerId == playerId));
        }

        [Fact]
        public async Task A_gold_respec_debits_the_price_and_refuses_when_short()
        {
            int level = 30;
            long price = WarTreeRegistry.RespecGoldPrice(4, level);
            long playerId = await CreatePlayerAsync(points: 4, level: level, freeRespecUsed: true, gold: price + 7);
            var engine = new SkillTreeEngine(_fixture.ServiceProvider);
            var target = new byte[WarTreeRegistry.Slots];
            target[WarTreeRegistry.Vitality] = 4;
            Assert.Null((await engine.CommitAsync(playerId, target)).Reason);

            // No free respec and no grant: only gold can pay.
            Assert.NotNull((await engine.RespecAsync(playerId)).Reason);

            var paid = await engine.RespecAsync(playerId, payWithGold: true);
            Assert.Null(paid.Reason);
            Assert.Equal(price, paid.GoldSpent);

            await using (var check = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(7, (await check.CommodityRecords.SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold")).Quantity);
                Assert.Equal(4, (await check.PlayerRecords.SingleAsync(p => p.Id == playerId)).AvailableSkillPoints);
            }

            // Buy again; 7 gold is not enough for another.
            Assert.Null((await engine.CommitAsync(playerId, target)).Reason);
            var refused = await engine.RespecAsync(playerId, payWithGold: true);
            Assert.NotNull(refused.Reason);
            Assert.Equal(0, refused.GoldSpent);
        }
    }
}
