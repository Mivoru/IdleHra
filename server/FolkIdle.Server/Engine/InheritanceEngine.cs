using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Buys inheritance levels with diamonds, and reads them back at login.
    ///
    /// See <see cref="InheritanceRegistry"/> for what the stats are and
    /// PlayerInheritanceStat for why they survive a season.
    /// </summary>
    public sealed class InheritanceEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;

        public InheritanceEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        /// <summary>The six levels this player owns, indexed by stat id.</summary>
        public static async Task<byte[]> LoadLevelsAsync(FolkIdleDbContext db, long playerId)
        {
            var levels = new byte[InheritanceRegistry.StatCount];

            var rows = await db.PlayerInheritanceStats
                .AsNoTracking()
                .Where(r => r.PlayerId == playerId)
                .ToListAsync();

            for (int i = 0; i < rows.Count; i++)
            {
                if (!InheritanceRegistry.IsValidStat(rows[i].StatId)) continue;
                levels[rows[i].StatId] = (byte)Math.Clamp(rows[i].Level, 0, InheritanceRegistry.MaxLevel);
            }

            return levels;
        }

        /// <summary>
        /// Spends diamonds on one level of one stat.
        ///
        /// Diamonds live on PlayerRecords."PremiumDiamonds" and NOWHERE else -
        /// the affix reroll once spent them from a CommodityRecords row that
        /// nothing in this server has ever created, and every purchase was
        /// silently rejected as unaffordable. One store, read under FOR UPDATE
        /// in the same transaction that writes the level, so two concurrent
        /// purchases cannot both pass the balance check.
        /// </summary>
        public async Task PurchaseLevelAsync(long playerId, int statId)
        {
            if (!InheritanceRegistry.IsValidStat(statId))
            {
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var strategy = db.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                try
                {
                    var owner = await db.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (owner == null)
                    {
                        await transaction.RollbackAsync();
                        _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.TargetNotFound);
                        return;
                    }

                    var row = await db.PlayerInheritanceStats
                        .FromSqlRaw("SELECT * FROM \"player_inheritance_stats\" WHERE \"PlayerId\" = {0} AND \"StatId\" = {1} FOR UPDATE", playerId, statId)
                        .SingleOrDefaultAsync();

                    int currentLevel = row?.Level ?? 0;
                    if (currentLevel >= InheritanceRegistry.MaxLevel)
                    {
                        await transaction.RollbackAsync();
                        _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                        return;
                    }

                    long cost = InheritanceRegistry.GetUpgradeCost(currentLevel);
                    if (cost <= 0L || owner.PremiumDiamonds < cost)
                    {
                        await transaction.RollbackAsync();
                        _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.InsufficientMaterials);
                        return;
                    }

                    owner.PremiumDiamonds -= (int)cost;

                    if (row == null)
                    {
                        db.PlayerInheritanceStats.Add(new PlayerInheritanceStat
                        {
                            PlayerId = playerId,
                            StatId = statId,
                            Level = 1,
                        });
                    }
                    else
                    {
                        row.Level = currentLevel + 1;
                    }

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // The live payload owns PremiumCurrency and the checkpoint
                    // writes it back with a plain assignment, so deducting only
                    // in the database would be refunded by the next flush.
                    // BillingSyncNotification carries the authoritative balance
                    // onto the tick thread, which is the only one allowed to
                    // touch it - the same hand-off the reroll used.
                    _playerRegistry?.BillingSyncQueue.Enqueue(new BillingSyncNotification
                    {
                        PlayerId = playerId,
                        PremiumDiamondsBalance = owner.PremiumDiamonds,
                    });

                    _playerRegistry?.InheritanceSyncQueue.Enqueue(new InheritanceSyncNotification
                    {
                        PlayerId = playerId,
                        StatId = statId,
                        NewLevel = (byte)(currentLevel + 1),
                    });

                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.Success);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine($"Inheritance purchase failed for player {playerId}: {ex.Message}");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                }
            });
        }
    }

    /// <summary>
    /// Carries a committed inheritance purchase back to the tick thread that
    /// owns the live payload - the same pattern every other DB-side mutation
    /// uses to reach a running session.
    /// </summary>
    public struct InheritanceSyncNotification
    {
        public long PlayerId;
        public int StatId;
        public byte NewLevel;
    }
}
