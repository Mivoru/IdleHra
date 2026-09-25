using System;
using System.Data;
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

        // Injectable so a test can stand on a dormant calendar day whatever
        // today happens to be.
        private readonly Func<DateTimeOffset> _clock;

        public LiveOpsTickEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry, WorldBossEngine worldBossEngine, PushNotificationTriggerEngine pushNotificationTriggerEngine, Func<DateTimeOffset>? clock = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
            _worldBossEngine = worldBossEngine;
            _pushNotificationTriggerEngine = pushNotificationTriggerEngine;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
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

        // Modul: ONE ENCOUNTER A WEEK, MONDAY TO SUNDAY UTC, BACK TO BACK (owner,
        // 2026-09-25; see WorldBossCalendar). Was the 1st-7th and 15th-22nd.
        //
        // With windows back to back, "is it inside a window" stopped being the
        // question - it always is. The question is "has THIS WEEK's encounter
        // happened yet", and the snapshot answers it: an encounter's
        // EventEndEpoch is its week's Sunday 23:59:59, and a defeat leaves that
        // epoch in place. So:
        //   - an active encounter whose end has passed is closed as failed;
        //   - no active encounter, and the snapshot's end is not this week's,
        //     opens this week's;
        //   - a boss defeated on Wednesday stays defeated until Monday, because
        //     its end IS this week's and nothing reopens it.
        internal async Task EvaluateWorldBossEventWindowAsync()
        {
            await _worldBossEngine.EnsureSnapshotAsync();

            DateTimeOffset now = _clock();
            long nowEpoch = now.ToUnixTimeSeconds();
            long thisWeekEnd = WorldBossCalendar.WeekEndEpoch(now);

            // Modul: a dev-opened window counts as a window. Without this a
            // window opened by the dev route would be closed on the next
            // 60-second tick, which is why no SQL poke could ever open one.
            // See WorldBossEngine.OpenManualWindowAsync.
            bool manualWindowOpen = _worldBossEngine.IsManualWindowOpen(nowEpoch);

            if (!manualWindowOpen && _worldBossEngine.IsEventActive && nowEpoch > _worldBossEngine.EventEndEpoch)
            {
                // The week (or a lapsed dev window) ran out with the boss alive.
                await _worldBossEngine.FinalizeEventAsFailedAsync();
            }

            bool thisWeeksEncounterPending = !manualWindowOpen && _worldBossEngine.EventEndEpoch != thisWeekEnd;
            bool shouldOpen = !_worldBossEngine.IsEventActive && (manualWindowOpen || thisWeeksEncounterPending);

            if (shouldOpen)
            {
                long windowEndEpoch = manualWindowOpen ? _worldBossEngine.ManualWindowEndEpoch : thisWeekEnd;
                await _worldBossEngine.ActivateEventWindowAsync(windowEndEpoch);

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
                long alertEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long[] onlinePlayerIdsForBossAlert = _playerRegistry.GetOnlinePlayerIds();
                for (int i = 0; i < onlinePlayerIdsForBossAlert.Length && _pushNotificationTriggerEngine != null; i++)
                {
                    await _pushNotificationTriggerEngine.ScheduleTriggerAsync(onlinePlayerIdsForBossAlert[i], alertEpoch, PushTriggerTypeWorldBossWindowOpen, "world_boss_window_open");
                }
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

            // Modul: THE DAILY WORLD BOSS STRIKE COMES BACK HERE (2026-09-25). The
            // engine resets a row's count when the day moves on, and login loads
            // only today's - but an online player's payload still holds
            // yesterday's count, and the tick refuses a spent budget in memory.
            // Without this, a player online over midnight would keep a grey
            // Strike button until they relogged.
            long[] onlinePlayerIdsForBossReset = _playerRegistry.GetOnlinePlayerIds();
            for (int i = 0; i < onlinePlayerIdsForBossReset.Length; i++)
            {
                _playerRegistry.WorldBossAttemptUpdateQueue.Enqueue(new WorldBossAttemptUpdateNotification
                {
                    PlayerId = onlinePlayerIdsForBossReset[i],
                    AttemptCount = 0,
                    SessionEndsEpoch = 0
                });
            }

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
