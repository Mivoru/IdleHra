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
            var levels = new byte[SkillTreeRegistry.NodeCount];

            var rows = await db.PlayerSkillTreeNodes
                .AsNoTracking()
                .Where(r => r.PlayerId == playerId)
                .ToListAsync();

            for (int i = 0; i < rows.Count; i++)
            {
                int id = rows[i].BranchId;
                if (!SkillTreeRegistry.IsValidNode(id)) continue;
                levels[id] = (byte)Math.Clamp(rows[i].Level, 0, SkillTreeRegistry.MaxLevelOf(id));
            }

            return levels;
        }

        /// <summary>
        /// Gives back what a player overpaid when the root cap moved from 20 to
        /// 10.
        ///
        /// The three-ring tree needs roots to be the cheap layer, so their cap
        /// halved and the ceiling moved into the boughs and crowns above. A
        /// player who had already bought level 14 of Fortune paid for four
        /// levels that no longer exist, and silently deleting them would be
        /// taking points a player earned.
        ///
        /// NATURALLY IDEMPOTENT: it only touches rows above the cap, and it
        /// leaves them at the cap - so the second run finds nothing. That is
        /// why this can sit on the load path instead of being a migration that
        /// has to be remembered.
        /// </summary>
        public static async Task<int> ReconcileRootCapAsync(FolkIdleDbContext db, long playerId)
        {
            var overCap = await db.PlayerSkillTreeNodes
                .Where(r => r.PlayerId == playerId
                         && r.BranchId < SkillTreeRegistry.RootCount
                         && r.Level > SkillTreeRegistry.RootMaxLevel)
                .ToListAsync();

            if (overCap.Count == 0) return 0;

            int refund = 0;
            foreach (var row in overCap)
            {
                // What the levels above the new cap cost under the OLD curve,
                // which is the curve they were actually bought at.
                for (int level = SkillTreeRegistry.RootMaxLevel; level < row.Level; level++)
                {
                    refund += (level / 5) + 1;
                }
                row.Level = SkillTreeRegistry.RootMaxLevel;
            }

            var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
            if (player != null) player.AvailableSkillPoints += refund;

            await db.SaveChangesAsync();
            return refund;
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
            if (!SkillTreeRegistry.IsValidNode(branchId))
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

                    // Modul: THE WHOLE TREE, not just this node.
                    //
                    // Boughs have prerequisites and an exclusive sibling, and
                    // crowns have a prerequisite of their own - none of which
                    // can be judged from one row. Read inside the transaction
                    // so two purchases racing on the same fork cannot both see
                    // an untaken sibling and both take it.
                    var allRows = await db.PlayerSkillTreeNodes
                        .FromSqlRaw("SELECT * FROM \"player_skill_tree\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                        .ToListAsync();

                    var levels = new byte[SkillTreeRegistry.NodeCount];
                    foreach (var row in allRows)
                    {
                        if (!SkillTreeRegistry.IsValidNode(row.BranchId)) continue;
                        levels[row.BranchId] =
                            (byte)Math.Clamp(row.Level, 0, SkillTreeRegistry.MaxLevelOf(row.BranchId));
                    }

                    if (!SkillTreeRegistry.CanPurchase(branchId, levels, player.AvailableSkillPoints))
                    {
                        await transaction.RollbackAsync();
                        return;
                    }

                    int currentLevel = node?.Level ?? 0;
                    int cost = SkillTreeRegistry.GetUpgradeCost(branchId, currentLevel);

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
