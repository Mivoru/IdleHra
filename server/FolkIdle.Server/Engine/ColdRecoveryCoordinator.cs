using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    // Activated strictly during node cold boots when transient caches are uninitialized.
    // Keeps the API gateway closed (HTTP 503) via GlobalEngineState.IsColdBootRecoveryComplete
    // until all stable-state T_stable sessions are fully reconstructed in parallel batches.
    public class ColdRecoveryCoordinator
    {
        private const int BatchSize = 100;

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;
        private readonly StateCheckpointManager _checkpointManager;

        public ColdRecoveryCoordinator(
            IServiceProvider serviceProvider,
            PlayerSessionRegistry playerRegistry,
            StateCheckpointManager checkpointManager)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
            _checkpointManager = checkpointManager;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("ColdRecoveryCoordinator: Beginning cold boot session reconstruction. Gateway is closed.");
            GlobalEngineState.IsColdBootRecoveryComplete = false;

            await ReconstructSessionCacheAsync(cancellationToken);

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            Console.WriteLine("ColdRecoveryCoordinator: Cold boot reconstruction complete. Gateway is now open.");
        }

        private async Task ReconstructSessionCacheAsync(CancellationToken cancellationToken)
        {
            List<long> playerIds;

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                // Fetch all T_stable player IDs using Serializable isolation.
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                try
                {
                    playerIds = await db.PlayerRecords
                        .Where(p => !p.Quarantine_Active)
                        .Select(p => p.Id)
                        .ToListAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                }
                catch
                {
                    await tx.RollbackAsync(cancellationToken);
                    throw;
                }
            }

            Console.WriteLine($"ColdRecoveryCoordinator: Reconstructing {playerIds.Count} sessions in batches of {BatchSize}.");

            // Process in throttled parallel batches to prevent connection pool exhaustion.
            int offset = 0;
            while (offset < playerIds.Count)
            {
                if (cancellationToken.IsCancellationRequested) break;

                int end = Math.Min(offset + BatchSize, playerIds.Count);
                var batch = playerIds.GetRange(offset, end - offset);

                var tasks = new Task[batch.Count];
                for (int i = 0; i < batch.Count; i++)
                {
                    long playerId = batch[i];
                    tasks[i] = Task.Run(async () =>
                    {
                        try
                        {
                            // Load T_stable state from Postgres.
                            var payload = await _checkpointManager.LoadPlayerState(playerId);
                            // Enqueue into LoginQueue so the 10 Hz SimulationEngine adopts the session.
                            _playerRegistry.LoginQueue.Enqueue(payload.PlayerId);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"ColdRecoveryCoordinator: Failed to reconstruct session for player {playerId}: {ex.Message}");
                        }
                    }, cancellationToken);
                }

                await Task.WhenAll(tasks);
                Console.WriteLine($"ColdRecoveryCoordinator: Batch complete [{offset + 1}-{end}] of {playerIds.Count}.");
                offset = end;
            }
        }
    }
}
