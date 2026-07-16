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
        private readonly PushNotificationTriggerEngine _pushNotificationTriggerEngine;

        // Modul: push trigger types for the two events this engine now
        // wires PushNotificationTriggerEngine into - previously fully
        // built infrastructure with zero callers anywhere in the codebase.
        private const byte PushTriggerTypeWorldBossWindowOpen = 2;
        private const byte PushTriggerTypeDailyReset = 3;

        private long _lastSeenUtcDateKey = -1L;
        private bool _isRunning;

        public LiveOpsTickEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry, WorldBossEngine worldBossEngine, PushNotificationTriggerEngine pushNotificationTriggerEngine)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
            _worldBossEngine = worldBossEngine;
            _pushNotificationTriggerEngine = pushNotificationTriggerEngine;
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
                        await EvaluateWorldBossEventWindowAsync();
                        await EvaluateDailyResetAsync();
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

        // World boss event windows run from the 1st-7th and the 15th-22nd of each month (UTC).
        // Outside those windows the boss is dormant; if not defeated by the window's last day
        // at 23:59:59 UTC, the encounter is finalized as failed.
        private async Task EvaluateWorldBossEventWindowAsync()
        {
            await _worldBossEngine.EnsureSnapshotAsync();

            DateTimeOffset now = DateTimeOffset.UtcNow;
            int day = now.Day;
            bool inWindowA = day >= 1 && day <= 7;
            bool inWindowB = day >= 15 && day <= 22;
            bool shouldBeActive = inWindowA || inWindowB;

            if (shouldBeActive && !_worldBossEngine.IsEventActive)
            {
                int windowEndDay = inWindowA ? 7 : 22;
                var windowEnd = new DateTimeOffset(now.Year, now.Month, windowEndDay, 23, 59, 59, TimeSpan.Zero);
                await _worldBossEngine.ActivateEventWindowAsync(windowEnd.ToUnixTimeSeconds());

                // Modul: wires the previously-dead PushNotificationTriggerEngine
                // into the one moment a currently-offline player would
                // actually want to hear about - the boss window opening.
                // Scheduled for every currently-online player at "now" (the
                // trigger poll picks these up within its own 1-second
                // cadence) - offline players are not targeted here since
                // there is no per-player scheduling ahead of a window whose
                // open date is itself dynamic (month-dependent), unlike the
                // daily reset below, which has a fixed, predictable next
                // occurrence.
                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long[] onlinePlayerIdsForBossAlert = _playerRegistry.GetOnlinePlayerIds();
                for (int i = 0; i < onlinePlayerIdsForBossAlert.Length; i++)
                {
                    await _pushNotificationTriggerEngine.ScheduleTriggerAsync(onlinePlayerIdsForBossAlert[i], nowEpoch, PushTriggerTypeWorldBossWindowOpen, "world_boss_window_open");
                }
            }
            else if (!shouldBeActive && _worldBossEngine.IsEventActive)
            {
                await _worldBossEngine.FinalizeEventAsFailedAsync();
            }

            if (_worldBossEngine.IsEventActive)
            {
                long[] onlinePlayerIds = _playerRegistry.GetOnlinePlayerIds();
                await _worldBossEngine.ScaleActiveBossAsync(onlinePlayerIds);

                if (_worldBossEngine.IsBossDead())
                {
                    await _worldBossEngine.ProcessDefeatedBossAsync();
                }
            }
        }

        // Modul: daily reset loop - detects the UTC-midnight day-key
        // rollover (see QuestEngine.GetUtcDateKey, the same boundary daily
        // quests and the daily login reward use) and notifies every
        // currently-online player that new quests are available. Offline
        // players are not targeted proactively here; QuestEngine's own
        // lazy regeneration at their next login is the actual source of
        // truth for "quests reset," this is purely an engagement nudge for
        // players already connected when the boundary crosses.
        // _lastSeenUtcDateKey starts at -1 so the very first tick after
        // process startup never fires a spurious reset notification (it
        // just records "today" as the baseline).
        private async Task EvaluateDailyResetAsync()
        {
            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long currentDateKey = QuestEngine.GetUtcDateKey(nowEpoch);

            if (_lastSeenUtcDateKey < 0)
            {
                _lastSeenUtcDateKey = currentDateKey;
                return;
            }

            if (currentDateKey == _lastSeenUtcDateKey)
            {
                return;
            }

            _lastSeenUtcDateKey = currentDateKey;

            long[] onlinePlayerIdsForDailyReset = _playerRegistry.GetOnlinePlayerIds();
            for (int i = 0; i < onlinePlayerIdsForDailyReset.Length; i++)
            {
                await _pushNotificationTriggerEngine.ScheduleTriggerAsync(onlinePlayerIdsForDailyReset[i], nowEpoch, PushTriggerTypeDailyReset, "daily_quest_reset");
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
