using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Spends skill points on the tree.
    ///
    /// Points are earned one per ACCOUNT level and live on
    /// PlayerRecords."AvailableSkillPoints" - the same column the four active
    /// skills used, kept because the points were the good part of that system.
    /// What they buy is passive now; see SkillTreeRegistry.
    /// </summary>
    public sealed class SkillTreeEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;

        public SkillTreeEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public static async Task<byte[]> LoadLevelsAsync(FolkIdleDbContext db, long playerId)
        {
            var levels = new byte[SkillTreeRegistry.BranchCount];

            var rows = await db.PlayerSkillTreeNodes
                .AsNoTracking()
                .Where(r => r.PlayerId == playerId)
                .ToListAsync();

            for (int i = 0; i < rows.Count; i++)
            {
                if (!SkillTreeRegistry.IsValidBranch(rows[i].BranchId)) continue;
                levels[rows[i].BranchId] = (byte)Math.Clamp(rows[i].Level, 0, SkillTreeRegistry.MaxLevel);
            }

            return levels;
        }

        /// <summary>
        /// Buys one level of one branch.
        ///
        /// THE POINTS AND THE LEVEL MOVE IN ONE TRANSACTION, under a row lock
        /// on the player. Two purchases racing each other would otherwise both
        /// read the same balance and both spend it - the same double-spend
        /// shape the market's escrow is hardened against, and skill points are
        /// no less real for being earned rather than bought.
        /// </summary>
        public async Task PurchaseLevelAsync(long playerId, int branchId)
        {
            if (!SkillTreeRegistry.IsValidBranch(branchId))
            {
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                try
                {
                    var player = await db.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (player == null)
                    {
                        await transaction.RollbackAsync();
                        return;
                    }

                    var node = await db.PlayerSkillTreeNodes
                        .FromSqlRaw("SELECT * FROM \"player_skill_tree\" WHERE \"PlayerId\" = {0} AND \"BranchId\" = {1} FOR UPDATE", playerId, branchId)
                        .SingleOrDefaultAsync();

                    int currentLevel = node?.Level ?? 0;
                    if (currentLevel >= SkillTreeRegistry.MaxLevel)
                    {
                        await transaction.RollbackAsync();
                        return;
                    }

                    int cost = SkillTreeRegistry.GetUpgradeCost(currentLevel);
                    if (cost <= 0 || player.AvailableSkillPoints < cost)
                    {
                        await transaction.RollbackAsync();
                        return;
                    }

                    player.AvailableSkillPoints -= cost;

                    if (node == null)
                    {
                        node = new PlayerSkillTreeNode { PlayerId = playerId, BranchId = branchId, Level = 1 };
                        db.PlayerSkillTreeNodes.Add(node);
                    }
                    else
                    {
                        node.Level = currentLevel + 1;
                    }

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // The tick thread owns the payload, so the new level
                    // reaches it through the queue rather than being written
                    // across threads - the pattern every other engine here uses.
                    _playerRegistry?.SkillTreeSyncQueue.Enqueue(new SkillTreeSyncNotification
                    {
                        PlayerId = playerId,
                        BranchId = branchId,
                        NewLevel = (byte)node.Level,
                        RemainingSkillPoints = player.AvailableSkillPoints,
                    });
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
        }
    }

    /// <summary>
    /// A bought level on its way back to the tick thread, which owns the
    /// payload the level has to land on.
    /// </summary>
    public struct SkillTreeSyncNotification
    {
        public long PlayerId;
        public int BranchId;
        public byte NewLevel;
        public int RemainingSkillPoints;
    }
}
