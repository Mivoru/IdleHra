using System;
using System.Data;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public sealed class LiveOpsTickEngine
    {
        private const int TickIntervalMs = 100;
        private const int ScaleIntervalTicks = 600;
        private const long WeeklyRotationSeconds = 604800L;
        private const long PhaseDurationSeconds = WeeklyRotationSeconds / 4L;

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;
        private readonly WorldBossEngine _worldBossEngine;

        private bool _isRunning;

        public LiveOpsTickEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry, WorldBossEngine worldBossEngine)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
            _worldBossEngine = worldBossEngine;
        }

        public void StartCron()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            _ = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            int tickCounter = ScaleIntervalTicks;

            while (_isRunning)
            {
                try
                {
                    if (tickCounter >= ScaleIntervalTicks)
                    {
                        tickCounter = 0;
                        await UpdateActiveEventRotationAsync();
                        await _worldBossEngine.EnsureSnapshotAsync();

                        int ccu = _playerRegistry.GetOnlinePlayerCount();
                        await _worldBossEngine.ScaleActiveBossAsync(ccu);

                        if (_worldBossEngine.IsBossDead())
                        {
                            await _worldBossEngine.ProcessDefeatedBossAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LiveOps ticker failed: {ex.Message}");
                }

                tickCounter++;
                await Task.Delay(TickIntervalMs);
            }
        }

        private async Task UpdateActiveEventRotationAsync()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long phaseIndex = (now % WeeklyRotationSeconds) / PhaseDurationSeconds;
            byte eventType = (byte)(phaseIndex + 1L);
            uint modifierMask = 1u << (int)phaseIndex;
            long phaseStart = now - (now % PhaseDurationSeconds);
            long phaseEnd = phaseStart + PhaseDurationSeconds;

            GlobalEngineState.ActiveEventType = eventType;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            try
            {
                var rotation = await db.LiveOpsEventRotations
                    .FromSqlRaw("SELECT * FROM \"LiveOpsEventRotations\" WHERE \"EventId\" = {0} FOR UPDATE", (int)eventType)
                    .SingleOrDefaultAsync();

                if (rotation == null)
                {
                    rotation = new LiveOpsEventRotation
                    {
                        EventId = eventType,
                        EventType = eventType,
                        ModifierBitmask = modifierMask,
                        EndTimestamp = phaseEnd
                    };
                    db.LiveOpsEventRotations.Add(rotation);
                }
                else
                {
                    rotation.EventType = eventType;
                    rotation.ModifierBitmask = modifierMask;
                    rotation.EndTimestamp = phaseEnd;
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"LiveOps rotation update failed: {ex.Message}");
            }
        }
    }
}
