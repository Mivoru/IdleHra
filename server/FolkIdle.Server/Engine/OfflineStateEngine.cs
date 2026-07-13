using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public class OfflineStateEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private const double MaxBankedChronoSeconds = 604800.0; // 7 days

        public OfflineStateEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task ReconcileOfflineStateAsync(long playerId, CancellationToken cancellationToken)
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            // Execute in Serializable transaction to strictly fence state calculation
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);

            var player = await db.PlayerRecords
                .FirstOrDefaultAsync(p => p.Id == playerId, cancellationToken);

            if (player == null)
            {
                return;
            }

            var chronoReg = await db.AccountChronoRegistries
                .FirstOrDefaultAsync(c => c.AccountId == player.PlayerGuid, cancellationToken);

            if (chronoReg == null)
            {
                return;
            }

            long currentTicks = Stopwatch.GetTimestamp();
            long lastSync = chronoReg.LastClockSyncEpoch;
            
            if (lastSync <= 0)
            {
                chronoReg.LastClockSyncEpoch = currentTicks;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            long elapsedTicks = currentTicks - lastSync;
            if (elapsedTicks <= 0)
            {
                return; // Time didn't move or went backward
            }

            double elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
            int inventoryCapacity = 50; // Hardcoded baseline capacity for now, in production query from upgrades
            int currentInventoryCount = await db.EquipmentInstances.CountAsync(e => e.PlayerId == playerId, cancellationToken);

            // Fixed-point mathematical aggregation
            // Example passive generation: 1 resource drop per 300 seconds
            double expectedDrops = elapsedSeconds / 300.0;
            int wholeDrops = (int)Math.Floor(expectedDrops);

            int spaceAvailable = Math.Max(0, inventoryCapacity - currentInventoryCount);

            if (wholeDrops > spaceAvailable)
            {
                // Saturation occurred. 
                int allowedDrops = spaceAvailable;
                double consumedSeconds = allowedDrops * 300.0;
                double overflowSeconds = elapsedSeconds - consumedSeconds;

                // Add drops up to capacity
                for (int i = 0; i < allowedDrops; i++)
                {
                    db.EquipmentInstances.Add(new EquipmentInstance
                    {
                        PlayerId = player.Id,
                        BaseItemId = "offline_drop",
                        QualityTier = 1,
                        AffixPayload = "{}"
                    });
                }

                // Capped refund logic
                double newBanked = player.BankedChronoSeconds + overflowSeconds;
                player.BankedChronoSeconds = Math.Min(newBanked, MaxBankedChronoSeconds);
            }
            else
            {
                // Full generation
                for (int i = 0; i < wholeDrops; i++)
                {
                    db.EquipmentInstances.Add(new EquipmentInstance
                    {
                        PlayerId = player.Id,
                        BaseItemId = "offline_drop",
                        QualityTier = 1,
                        AffixPayload = "{}"
                    });
                }
                
                // Add remaining partial time to ChronoBank
                double fractionalSeconds = (expectedDrops - wholeDrops) * 300.0;
                double newBanked = player.BankedChronoSeconds + fractionalSeconds;
                player.BankedChronoSeconds = Math.Min(newBanked, MaxBankedChronoSeconds);
            }

            // Also grant linear passive XP
            player.CurrentXp += (long)(elapsedSeconds * 2.0); // Example 2 XP / sec

            chronoReg.LastClockSyncEpoch = currentTicks;

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
