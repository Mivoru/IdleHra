using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

            // Modul: LastClockSyncEpoch is a real Unix epoch second value
            // everywhere else it is written (ChronoBufferEngine.
            // ProcessLoginHandshake, StateCheckpointManager's login/flush
            // paths both pass DateTimeOffset.UtcNow.ToUnixTimeSeconds()) -
            // this method must read and write the exact same time base.
            // Stopwatch.GetTimestamp() measures monotonic ticks since an
            // arbitrary reference (process/OS-dependent, resets across
            // restarts), which is numerically incompatible with a stored
            // Unix epoch second value - comparing the two here previously
            // made elapsedTicks reliably negative in real usage, silently
            // disabling offline reconciliation entirely.
            long currentTicks = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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

            double elapsedSeconds = elapsedTicks;

            // Modul: mirrors StateCheckpointManager.FlushState's own
            // InventorySpaceRemaining formula exactly (20 base slots plus
            // RaceMasteryResolver.GetHumanVaultBonusSlots) - previously
            // hardcoded to 50 here, silently diverging from the real
            // capacity a live TickStatePayload uses. A player under the
            // real (lower) capacity could have offline drops overflow into
            // banked seconds too late, or a player past 50 real slots
            // (Human mastery >= 25) could accept offline drops the client
            // then has nowhere to place.
            int humanMasteryLevel = await db.PlayerRaceMasteries
                .Where(m => m.PlayerId == playerId && m.RaceId == RaceIds.Human)
                .Select(m => (int?)m.MasteryLevel)
                .FirstOrDefaultAsync(cancellationToken) ?? 0;
            int inventoryCapacity = 20 + RaceMasteryResolver.GetHumanVaultBonusSlots(humanMasteryLevel);
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
