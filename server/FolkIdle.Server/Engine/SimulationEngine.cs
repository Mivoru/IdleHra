using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Network;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public struct EngineMetricsPayload
    {
        public long TotalDriftMs;
        public long TotalTicksProcessed;
        public long LastExecutionTimeMs;
        public long ThrottledPacketsDropped;

        // Prometheus histogram for folkidle_tick_duration_milliseconds (see
        // NetworkBroadcastSystem's /metrics handler). Buckets are cumulative
        // (le semantics - each bucket counts every observation less than or
        // equal to its bound), matching the standard Prometheus histogram
        // exposition format. TotalTicksProcessed above doubles as the
        // histogram's _count.
        public long TickDurationBucketCount10Ms;
        public long TickDurationBucketCount25Ms;
        public long TickDurationBucketCount50Ms;
        public long TickDurationBucketCount100Ms;
        public long TickDurationBucketCount250Ms;
        public long TickDurationBucketCountInf;
        public long TickDurationSumMs;
    }

    public class SimulationEngine
    {
        private const int TickIntervalMs = 100; // 10 Hz
        private const double TickIntervalSeconds = TickIntervalMs / 1000.0;
        private readonly LootTableEngine _lootEngine;
        private readonly StateCheckpointManager _checkpointManager;
        private readonly NetworkBroadcastSystem _networkSystem;
        private readonly ForgeSplicingEngine _forgeEngine;
        private readonly MarketOrderBookEngine _marketEngine;
        private readonly PlayerSessionRegistry _playerRegistry;
        private readonly GuildContributionEngine _guildEngine;
        private readonly MarketEscrowEngine _escrowEngine;
        private readonly MailboxAndBankEngine _mailboxEngine;
        private readonly AffixRerollEngine _rerollEngine;
        private readonly BreedingEngine _breedingEngine;
        private readonly VillageBuildingEngine _villageBuildingEngine;
        private readonly VillageManagementEngine _villageManagementEngine;
        private readonly GuildLogisticsEngine _guildLogisticsEngine;
        private readonly CraftingEngine _craftingEngine;
        private readonly WorldBossEngine _worldBossEngine;
        private readonly MentorshipEngine _mentorshipEngine;
        private readonly GuildWarEngine _guildWarEngine;
        private readonly ChronoCoreEngine _chronoCoreEngine;
        private readonly LegacyStoreEngine _legacyStoreEngine;
        private readonly GuildLogisticsDepotEngine _guildLogisticsDepotEngine;
        private readonly GuildCombatSimulationEngine _guildCombatSimulationEngine;
        private readonly GuildRaidEngine? _guildRaidEngine;
        private readonly EquipmentSlotEngine? _equipmentSlotEngine;
        private readonly AntiCheatTelemetryEngine _antiCheatTelemetryEngine;
        private readonly PushNotificationTriggerEngine _pushNotificationTriggerEngine;
        private readonly CompliancePurgeEngine _compliancePurgeEngine;
        private readonly BillingVerificationEngine _billingVerificationEngine;
        private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<FolkIdleDbContext> _contextFactory;
        private readonly StackExchange.Redis.IConnectionMultiplexer _redis;
        private readonly GlobalTournamentMeshService? _tournamentMeshService;
        private readonly TelemetryStreamingEngine _telemetryStreamingEngine;
        private bool _isRunning;
        private Thread? _engineThread;
        private Thread? _battlePassWorkerThread;
        private int _ticksSinceLastBroadcast = 0;
        private readonly System.Collections.Concurrent.ConcurrentQueue<TickStatePayload> _readyLogins = new();

        private EngineMetricsPayload _metrics;
        public ref EngineMetricsPayload GetMetrics() => ref _metrics;

        public bool IsRunning => _isRunning;

        public static int ActiveGlobalEventId { get; private set; }

        public SimulationEngine(LootTableEngine lootEngine, StateCheckpointManager checkpointManager, NetworkBroadcastSystem networkSystem, ForgeSplicingEngine forgeEngine, MarketOrderBookEngine marketEngine, PlayerSessionRegistry playerRegistry, GuildContributionEngine guildEngine, MarketEscrowEngine escrowEngine, MailboxAndBankEngine mailboxEngine, AffixRerollEngine rerollEngine, BreedingEngine breedingEngine, GuildLogisticsEngine guildLogisticsEngine, CraftingEngine craftingEngine, WorldBossEngine worldBossEngine, VillageBuildingEngine villageBuildingEngine, VillageManagementEngine villageManagementEngine, MentorshipEngine mentorshipEngine, GuildWarEngine guildWarEngine, ChronoCoreEngine chronoCoreEngine, LegacyStoreEngine legacyStoreEngine, GuildLogisticsDepotEngine guildLogisticsDepotEngine, GuildCombatSimulationEngine guildCombatSimulationEngine, AntiCheatTelemetryEngine antiCheatTelemetryEngine, PushNotificationTriggerEngine pushNotificationTriggerEngine, CompliancePurgeEngine compliancePurgeEngine, BillingVerificationEngine billingVerificationEngine, StackExchange.Redis.IConnectionMultiplexer redis, Microsoft.EntityFrameworkCore.IDbContextFactory<FolkIdleDbContext> contextFactory, GuildRaidEngine? guildRaidEngine = null, EquipmentSlotEngine? equipmentSlotEngine = null)
        {
            _lootEngine = lootEngine;
            _checkpointManager = checkpointManager;
            _networkSystem = networkSystem;
            _forgeEngine = forgeEngine;
            _marketEngine = marketEngine;
            _playerRegistry = playerRegistry;
            _guildEngine = guildEngine;
            _escrowEngine = escrowEngine;
            _mailboxEngine = mailboxEngine;
            _rerollEngine = rerollEngine;
            _breedingEngine = breedingEngine;
            _villageBuildingEngine = villageBuildingEngine;
            _guildLogisticsEngine = guildLogisticsEngine;
            _craftingEngine = craftingEngine;
            _worldBossEngine = worldBossEngine;
            _mentorshipEngine = mentorshipEngine;
            _guildWarEngine = guildWarEngine;
            _chronoCoreEngine = chronoCoreEngine;
            _legacyStoreEngine = legacyStoreEngine;
            _guildLogisticsDepotEngine = guildLogisticsDepotEngine;
            _guildCombatSimulationEngine = guildCombatSimulationEngine;
            _guildRaidEngine = guildRaidEngine;
            _equipmentSlotEngine = equipmentSlotEngine;
            _villageManagementEngine = villageManagementEngine;
            _antiCheatTelemetryEngine = antiCheatTelemetryEngine;
            _pushNotificationTriggerEngine = pushNotificationTriggerEngine;
            _compliancePurgeEngine = compliancePurgeEngine;
            _billingVerificationEngine = billingVerificationEngine;
            _contextFactory = contextFactory;
            _redis = redis;
            if (redis != null)
            {
                _tournamentMeshService = new GlobalTournamentMeshService(contextFactory, new DistributedLockManager(redis));
            }
            _telemetryStreamingEngine = new TelemetryStreamingEngine(contextFactory, _liveSessionContexts);
            // Wire split-brain disconnect callback so StateCheckpointManager can force-close sockets.
            _networkSystem.RegisterCheckpointManager(_checkpointManager);
        }

        public void Start()
        {
            _isRunning = true;
            _telemetryStreamingEngine.Start();
            _engineThread = new Thread(EngineLoop)
            {
                IsBackground = true,
                Name = "SimulationTickThread"
            };
            _engineThread.Start();
            
            _battlePassWorkerThread = new Thread(BattlePassWorkerLoop)
            {
                IsBackground = true,
                Name = "BattlePassWorkerThread"
            };
            _battlePassWorkerThread.Start();
        }

        public void Stop()
        {
            _isRunning = false;
            _engineThread?.Join();
            _battlePassWorkerThread?.Join();
            _telemetryStreamingEngine.StopAndDrain();
        }

        public void ExecuteDataDrainage()
        {
            _isRunning = false;
            _engineThread?.Join();
            _battlePassWorkerThread?.Join();
            
            lock (_activePlayers)
            {
                var allPlayers = _activePlayers.Values.ToArray();
                var chunks = allPlayers.Chunk(200).ToArray();

                var drainTask = Task.Run(() =>
                {
                    Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, chunk =>
                    {
                        _checkpointManager.FlushBatch(chunk).GetAwaiter().GetResult();
                    });
                });

                if (!drainTask.Wait(1500))
                {
                    Console.WriteLine("PANIC: Drainage timeout limit reached (1500ms). Forcing ungraceful exit.");
                }

                TelemetryStreamer.CompleteWriter();
            }
        }

        public void ShutdownGracefully()
        {
            Console.WriteLine("[SimulationEngine] Initiating graceful shutdown...");
            _isRunning = false;
            
            // Abort the 10 Hz subtick loop step execution
            _engineThread?.Join();
            _telemetryStreamingEngine.StopAndDrain();
            
            lock (_activePlayers)
            {
                var allPlayers = _activePlayers.Values.ToArray();
                var chunks = allPlayers.Chunk(100).ToArray();

                foreach (var chunk in chunks)
                {
                    // Synchronously pass them down in isolated 100-record chunks
                    _checkpointManager.FlushBatch(chunk).GetAwaiter().GetResult();
                }

                Console.WriteLine("[SimulationEngine] Graceful shutdown and state flush complete.");
            }
        }

        private readonly System.Collections.Generic.Dictionary<long, TickStatePayload> _activePlayers = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext> _liveSessionContexts = new();

        // Modul: GuildId -> active member PlayerIds. Maintained incrementally
        // on every _activePlayers add/remove below rather than derived by
        // scanning _activePlayers - the four guild-scoped notification
        // queues (GuildUpdateQueue, GuildLogisticsDepotUpdateQueue,
        // GuildCombatSimulationUpdateQueue, GuildRaidBossUpdateQueue) used to
        // do exactly that scan, once per dequeued event, every 100ms tick:
        // O(events_per_tick x active_player_count) instead of O(guild_size).
        // A player's GuildId changes at session boundaries (login,
        // disconnect) and, since GuildManagementEngine exists, mid-session
        // via the GuildMembershipChangeQueue drain in the tick loop - that
        // drain is the ONLY mid-session mutation path, and it goes through
        // the same AddToGuildIndex/RemoveFromGuildIndex helpers as the
        // session-boundary sites, so the index can never drift from the
        // live TickStatePayload.GuildId values.
        private readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<long>> _guildMembersIndex = new();

        // Adds playerId to _guildMembersIndex[guildId] - called only at
        // session-start (login, benchmark injection), never per tick, so the
        // occasional List<long> allocation on a guild's first active member
        // is outside the zero-allocation 10 Hz tick constraint (that
        // constraint applies to the four read/dequeue loops below, which
        // only ever iterate an already-allocated list).
        private void AddToGuildIndex(long guildId, long playerId)
        {
            if (guildId <= 0) return;

            if (!_guildMembersIndex.TryGetValue(guildId, out var members))
            {
                members = new System.Collections.Generic.List<long>();
                _guildMembersIndex[guildId] = members;
            }

            if (!members.Contains(playerId))
            {
                members.Add(playerId);
            }
        }

        // Removes playerId from _guildMembersIndex[guildId] - called only at
        // session-end (disconnect, security termination, validation-failure
        // eviction), never per tick.
        private void RemoveFromGuildIndex(long guildId, long playerId)
        {
            if (guildId <= 0) return;

            if (_guildMembersIndex.TryGetValue(guildId, out var members))
            {
                members.Remove(playerId);
                if (members.Count == 0)
                {
                    _guildMembersIndex.Remove(guildId);
                }
            }
        }

        // Test-only observability (via InternalsVisibleTo) for the
        // guild-membership drain: how many ReloadState packets the drain
        // has issued, and whether a player currently sits in a guild's
        // index bucket. The tick thread owns both structures; tests poll
        // these after enqueueing a GuildMembershipChangeNotification and
        // must tolerate a tick's worth of latency, not expect synchronous
        // visibility.
        internal long GuildMembershipReloadStatesIssued;

        internal bool IsPlayerInGuildIndex(long guildId, long playerId)
        {
            lock (_activePlayers)
            {
                return _guildMembersIndex.TryGetValue(guildId, out var members) && members.Contains(playerId);
            }
        }

        internal long GetActivePlayerGuildId(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.GuildId : -1;
            }
        }

        // Test-only observability for tick-thread exception isolation:
        // GatheringProgressTicks is a simple, RNG-free, monotonically
        // increasing counter while a gathering activity is active, making
        // it a clean proxy for "the tick thread is still alive and still
        // processing this specific player" across repeated real ticks.
        internal int GetActivePlayerGatheringProgressTicks(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.GatheringProgressTicks : -1;
            }
        }

        internal bool IsActivePlayerPresent(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.ContainsKey(playerId);
            }
        }

        internal bool IsActivePlayerSuspended(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) && payload.IsSuspended;
            }
        }

        internal int GetActivePlayerLastCommandResultCode(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.LastCommandResultCode : -1;
            }
        }

        // Modul: single entry point for adding a player to _activePlayers -
        // keeps _guildMembersIndex synchronized so no add site can forget
        // the index update. See RemoveActivePlayer for the matching removal
        // path.
        private void AddActivePlayer(TickStatePayload payload)
        {
            _activePlayers[payload.PlayerId] = payload;
            AddToGuildIndex(payload.GuildId, payload.PlayerId);

            // Modul: caches this player's GuildId directly on their
            // WebSocketSession so NetworkBroadcastSystem can route guild-
            // channel chat messages (BroadcastGuildChatMessage) without
            // needing a reference back into this class's own
            // _guildMembersIndex - see ChatEngine/NetworkBroadcastSystem's
            // own comments for why a per-session cached value was chosen
            // over that alternative.
            _networkSystem.UpdateSessionGuildId(payload.PlayerId, payload.GuildId);
        }

        // Modul: single entry point for removing a player from
        // _activePlayers - replaces every bare _activePlayers.Remove(id)
        // call in this file so _guildMembersIndex cannot drift out of sync
        // with _activePlayers (a player left in the guild index after
        // disconnect would keep receiving guild broadcast writes into a
        // TickStatePayload that no longer exists in _activePlayers, which
        // GetValueRefOrNullRef already guards against, but would still leak
        // the index entry itself indefinitely).
        private void RemoveActivePlayer(long playerId)
        {
            if (_activePlayers.TryGetValue(playerId, out var payload))
            {
                RemoveFromGuildIndex(payload.GuildId, playerId);
            }
            _activePlayers.Remove(playerId);
        }

        public void InjectVirtualPlayer(TickStatePayload payload)
        {
            lock (_activePlayers)
            {
                AddActivePlayer(payload);
                _liveSessionContexts.TryAdd(payload.PlayerId, new LiveSessionContext(payload.PlayerId, payload.AccountId));
            }
        }

        public void InjectBenchmarkCommand(long playerId, ClientCommandPacket packet)
        {
            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = playerId, Packet = packet });
        }

        private void TerminateSessionForSecurity(long playerId)
        {
            RemoveActivePlayer(playerId);
            _liveSessionContexts.TryRemove(playerId, out _);
            _playerRegistry.UnregisterPlayer(playerId);
            _networkSystem.PurgeTokensForPlayer(playerId);
            _networkSystem.ForceDisconnect(playerId);
        }

        // Modul: replaces every bare `Task.Run(async () => {...})` fire-and-
        // forget dispatch in the command dispatch table below. A bare
        // Task.Run there meant any exception inside it (a DB failure, a
        // transient Npgsql error, a null ref) became an unobserved task
        // exception - silently dropped by the CLR, never logged, and for
        // command handlers that gate a client-visible state transition
        // (CommandType.Login above all - see the comment on that branch)
        // this looked exactly like a hang: the client's socket sits waiting
        // for a StateUpdatePacket that will never arrive, with no error
        // surfaced anywhere. This helper guarantees every dispatch is
        // observed: failures are logged, and if playerIdToDisconnectOnFailure
        // is nonzero that player's connection is force-severed instead of
        // being left to hang silently.
        //
        // Deliberately (context, playerId, action) rather than the more
        // natural-reading (action, context) order - action must stay the
        // LAST parameter so every call site's existing multi-line lambda
        // body and its closing `});` are untouched by this refactor; only
        // the opening `Task.Run(async () => {` line changes to
        // `SafeDispatchAsync("Context", playerId, async () => {`, which is
        // what makes converting ~30 call sites mechanically safe rather
        // than requiring a hand match of nested braces at every one.
        private void SafeDispatchAsync(string context, long playerIdToDisconnectOnFailure, Func<Task> action)
        {
            _ = SafeDispatchAsyncCore(context, playerIdToDisconnectOnFailure, action);
        }

        private async Task SafeDispatchAsyncCore(string context, long playerIdToDisconnectOnFailure, Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SafeDispatchAsync[{context}] failed: {ex.Message}");
                if (playerIdToDisconnectOnFailure != 0)
                {
                    _networkSystem.ForceDisconnect(playerIdToDisconnectOnFailure);
                }
            }
        }

        private static uint ClampWorldBossHpToUInt(long value)
        {
            if (value <= 0)
            {
                return 0;
            }

            return value > uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static uint ComputeSkillCooldownRemainingMs(in TickStatePayload payload, int skillId)
        {
            long remaining = ActiveSkillEngine.GetSkillCooldownExpiresAtMs(in payload, skillId) - Environment.TickCount64;
            if (remaining <= 0) return 0;
            return remaining > uint.MaxValue ? uint.MaxValue : (uint)remaining;
        }

        private static uint ResolveChronoEngineStatus(ref TickStatePayload payload)
        {
            if (payload.IsChronoAccelerating && (payload.SpeedMultiplier == 2 || payload.SpeedMultiplier == 4))
            {
                return 2U;
            }

            return payload.BankedChronoSeconds > 0.0 ? 1U : 0U;
        }

        private static unsafe int ReadActiveStatusModifier(ref StatusEffectBuffer buffer, int index)
        {
            if (index < 0 || index >= 8)
            {
                return 0;
            }

            fixed (int* modifiers = buffer.ActiveModifiers)
            {
                return modifiers[index];
            }
        }

        private static unsafe byte[] CopyDeviceTokenBytes(ref ClientCommandPacket packet)
        {
            byte[] token = new byte[64];
            fixed (byte* source = packet.DeviceTokenBytes)
            {
                for (int i = 0; i < token.Length; i++)
                {
                    token[i] = source[i];
                }
            }
            return token;
        }

        private void BattlePassWorkerLoop()
        {
            while (IsRunning)
            {

                // Throttle to max 50 ops per second (i.e. ~1 op per 20ms).
                // We'll run every 20ms and do exactly 1 op.
                Thread.Sleep(20);

                if (GlobalEngineState.IsEraTransitionActive) continue;

                foreach (var kvp in _liveSessionContexts)
                {
                    if (kvp.Value.TryDequeueBattlePassClaim(out var req))
                    {
                        var t = ExecuteBattlePassClaimAsync(kvp.Key, req.TargetMilestoneIndex, req.AccumulatedSeasonalXp, req.ActiveChroniclePassLevel);
                        t.GetAwaiter().GetResult();
                        
                        _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand
                        {
                            PlayerId = kvp.Key,
                            Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                        });
                        break; // Only process one item every 20ms globally to strictly enforce the 50 ops/sec cap.
                    }
                }
            }
        }

        private void EngineLoop()
        {
            Stopwatch stopwatch = new Stopwatch();

            int benchmarkTickCount = 0;
            long benchmarkStartAllocated = 0;
            double benchmarkTotalMs = 0;
            double benchmarkPeakMs = 0;
            bool isBenchmarking = Environment.GetEnvironmentVariable("RUN_BENCHMARK") == "true";

            if (isBenchmarking)
            {
                benchmarkStartAllocated = GC.GetAllocatedBytesForCurrentThread();
            }

            while (IsRunning)
            {
                if (GlobalEngineState.IsEraTransitionActive)
                {
                    while (_networkSystem.CommandQueue.TryDequeue(out _)) { }
                    Thread.Sleep(100);
                    continue;
                }

                long tickStartTimestamp = Stopwatch.GetTimestamp();
                stopwatch.Restart();

                if (isBenchmarking)
                {
                    FolkIdle.Server.Benchmark.EngineStressTester.InjectCommandFlood(this);
                }

                // Read the authoritative LiveOps event selected by the background ticker.
                ActiveGlobalEventId = GlobalEngineState.ActiveEventType;

                while (_playerRegistry.MarketMatchQueue.TryDequeue(out var notification))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, notification.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.AddGold(notification.GoldDelta);
                        if (notification.NewEquipmentInstanceId.HasValue)
                        {
                            currentPayload.InventorySpaceRemaining--;
                        }
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.BirthNotificationQueue.TryDequeue(out var birthNotification))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, birthNotification.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.VillagePopulation++;
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.WorldBossAttemptUpdateQueue.TryDequeue(out var worldBossAttemptUpdate))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, worldBossAttemptUpdate.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.WorldBossAttemptCount = worldBossAttemptUpdate.AttemptCount;
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.MasteryUpdateQueue.TryDequeue(out var masteryUpdate))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, masteryUpdate.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        // Modul 13 fix: was gated on raw literals (1, 3, 4) that predate
                        // RaceIds and never matched it - Vila updates (RaceId=2) were
                        // silently dropped entirely, and RaceId 3/4 mislabeled Draugr's
                        // and Kobold's levels as Vila's/Draugr's respectively.
                        if (masteryUpdate.RaceId == RaceIds.Human) currentPayload.HumanMasteryLevel = masteryUpdate.MasteryLevel;
                        else if (masteryUpdate.RaceId == RaceIds.Vila) currentPayload.VilaMasteryLevel = masteryUpdate.MasteryLevel;
                        else if (masteryUpdate.RaceId == RaceIds.Draugr) currentPayload.DraugrMasteryLevel = masteryUpdate.MasteryLevel;
                        else if (masteryUpdate.RaceId == RaceIds.Kobold) currentPayload.KoboldMasteryLevel = masteryUpdate.MasteryLevel;
                        else if (masteryUpdate.RaceId == RaceIds.Vodnik) currentPayload.VodnikMasteryLevel = masteryUpdate.MasteryLevel;
                        else if (masteryUpdate.RaceId == RaceIds.Moosleute) currentPayload.MoosleuteMasteryLevel = masteryUpdate.MasteryLevel;
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.ForgeUpgradeQueue.TryDequeue(out var forgeUpgrade))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, forgeUpgrade.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.ForgeUpgradeCount++;
                        if (forgeUpgrade.ResultingQualityTier > currentPayload.HighestForgeSynthesisTier)
                        {
                            currentPayload.HighestForgeSynthesisTier = forgeUpgrade.ResultingQualityTier;
                        }
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.EquipmentSlotUpdateQueue.TryDequeue(out var equipUpdate))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, equipUpdate.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.EquippedWeaponId = equipUpdate.EquippedWeaponId;
                        currentPayload.EquippedArmorId = equipUpdate.EquippedArmorId;
                        currentPayload.CachedEquippedFlatAttack = equipUpdate.EquippedFlatAttack;
                        currentPayload.CachedEquippedFlatDefense = equipUpdate.EquippedFlatDefense;
                        currentPayload.CachedEquippedCritBonus = equipUpdate.EquippedCritBonus;
                        currentPayload.CachedEquippedLuckBonus = equipUpdate.EquippedLuckBonus;
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.CodexMultiplierUpdateQueue.TryDequeue(out var codexMultiplierUpdate))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, codexMultiplierUpdate.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.CachedCodexYieldMultiplier = codexMultiplierUpdate.YieldMultiplier;
                        currentPayload.CachedCodexDamageMultiplier = codexMultiplierUpdate.DamageMultiplier;
                    }
                }

                while (_playerRegistry.RegionCompletionUpdateQueue.TryDequeue(out var regionCompletionUpdate))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, regionCompletionUpdate.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.CompletedAreaFlags |= regionCompletionUpdate.CompletedRegionFlags;
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.CombatLootDropQueue.TryDequeue(out var combatLootDrop))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, combatLootDrop.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        if (combatLootDrop.ConsumedInventorySlot && currentPayload.InventorySpaceRemaining > 0)
                        {
                            currentPayload.InventorySpaceRemaining--;
                        }
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.CraftingCompletionQueue.TryDequeue(out var craftCompletion))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, craftCompletion.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        QuestEngine.IncrementProgress(ref currentPayload, QuestEngine.QuestTypeCraftItems, 1);

                        if (currentPayload.ActiveGuildWarId > 0 && ContentRegistry.ItemDefinitions.Length >= craftCompletion.CraftedItemId)
                        {
                            var def = ContentRegistry.ItemDefinitions[craftCompletion.CraftedItemId - 1];
                            if (def.RegionTier >= 5)
                            {
                                int wp = 50 * def.RegionTier;
                                _guildWarEngine.GuildWarPointQueue.Enqueue(new GuildWarPointEvent
                                {
                                    MatchId = currentPayload.ActiveGuildWarId,
                                    GuildId = currentPayload.GuildId,
                                    Front = 1,
                                    Points = wp
                                });
                            }
                        }
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.GuildMembershipChangeQueue.TryDequeue(out var membershipChange))
                {
                    // GuildManagementEngine committed a membership change to
                    // the database on a background thread; fold it into the
                    // tick thread's own state here. Both the old and new
                    // index entries are updated via the same helpers every
                    // session-boundary site uses, so _guildMembersIndex
                    // stays consistent with the live TickStatePayload.GuildId
                    // for the four guild-scoped broadcast loops below. An
                    // offline player has no _activePlayers entry and no
                    // index entries to fix - the database is already
                    // authoritative and their next login loads the new
                    // GuildId - so the null-ref path intentionally does
                    // nothing.
                    ref var membershipPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, membershipChange.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref membershipPayload))
                    {
                        RemoveFromGuildIndex(membershipChange.OldGuildId, membershipChange.PlayerId);
                        AddToGuildIndex(membershipChange.NewGuildId, membershipChange.PlayerId);
                        membershipPayload.GuildId = membershipChange.NewGuildId;
                        membershipPayload.IsDirty = true;
                        _networkSystem.UpdateSessionGuildId(membershipChange.PlayerId, membershipChange.NewGuildId);

                        // ReloadState forces the client to re-pull its full
                        // state so guild-scoped UI reflects the new
                        // membership immediately rather than on next login.
                        _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand
                        {
                            PlayerId = membershipChange.PlayerId,
                            Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                        });
                        System.Threading.Interlocked.Increment(ref GuildMembershipReloadStatesIssued);
                    }
                }

                // Modul: generic client error-feedback channel drain - see
                // CommandResultNotification's own comment. Zero-allocation:
                // pure struct field writes against an already-resolved ref
                // into _activePlayers, matching the guild-membership drain
                // immediately above.
                while (_playerRegistry.CommandResultQueue.TryDequeue(out var commandResult))
                {
                    ref var resultPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, commandResult.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref resultPayload))
                    {
                        resultPayload.LastCommandResultCode = commandResult.ResultCode;
                        unchecked { resultPayload.LastCommandResultTick++; }
                        resultPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.GuildUpdateQueue.TryDequeue(out var guildUpdate))
                {
                    // Real-time updates for guild members - O(guild_size)
                    // via _guildMembersIndex instead of O(active_player_count).
                    if (_guildMembersIndex.TryGetValue(guildUpdate.GuildId, out var guildUpdateMembers))
                    {
                        foreach (long memberId in guildUpdateMembers)
                        {
                            ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, memberId);
                            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                            {
                                if (guildUpdate.IsMining)
                                {
                                    currentPayload.CachedMiningMonolithLevel = guildUpdate.NewLevel;
                                }
                                else
                                {
                                    currentPayload.CachedWoodcuttingMonolithLevel = guildUpdate.NewLevel;
                                }
                                currentPayload.IsDirty = true;
                            }
                        }
                    }
                }

                while (_playerRegistry.InfrastructureUpdateQueue.TryDequeue(out var updateNotif))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, updateNotif.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.ForgeLevel = updateNotif.ForgeLevel;
                        currentPayload.InnLevel = updateNotif.InnLevel;
                        currentPayload.BreedingLevel = updateNotif.BreedingLevel;
                        currentPayload.AcademyLevel = updateNotif.AcademyLevel;
                        currentPayload.CurrentPopulationCount = updateNotif.CurrentPopulationCount;
                        currentPayload.VillagePopulation = updateNotif.CurrentPopulationCount;
                        currentPayload.CachedCurrentToolTier = updateNotif.CurrentToolTier;
                        currentPayload.CachedInnMaturationBonus = updateNotif.InnMaturationBonus;
                        currentPayload.CachedMaxPopulationCapacity = updateNotif.MaxPopulationCapacity;
                        currentPayload.LumberjackLevel = updateNotif.LumberjackLevel;
                        currentPayload.QuarryLevel = updateNotif.QuarryLevel;
                        currentPayload.MineLevel = updateNotif.MineLevel;
                        currentPayload.WarehouseLevel = updateNotif.WarehouseLevel;
                        currentPayload.PendingUpgradeBuildingId = updateNotif.PendingUpgradeBuildingId;
                        currentPayload.PendingUpgradeCompletesAtEpoch = updateNotif.PendingUpgradeCompletesAtEpoch;
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.MentorshipUpdateQueue.TryDequeue(out var mentorshipUpdate))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, mentorshipUpdate.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.CachedMentorCount++; 
                    }
                }

                while (_playerRegistry.QuarantineNotificationQueue.TryDequeue(out var quarantineNotification))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, quarantineNotification.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.Quarantine_Active = true;
                        currentPayload.IsQuarantined = true;
                    }
                }

                while (_playerRegistry.ChronoAccelerationQueue.TryDequeue(out var chronoNotif))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, chronoNotif.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        double newBanked = currentPayload.BankedChronoSeconds + chronoNotif.SecondsToAdd;
                        if (newBanked > ChronoBufferEngine.MaxBankedChronoSeconds) newBanked = ChronoBufferEngine.MaxBankedChronoSeconds;
                        currentPayload.BankedChronoSeconds = newBanked;
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.LegacyStoreUpdateQueue.TryDequeue(out var legacyNotif))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, legacyNotif.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.SetLegacyShards(legacyNotif.LegacyShardBalance);
                        currentPayload.CitizenMultiSlotsUnlocked = legacyNotif.CitizenMultiSlotsUnlocked;
                        if (legacyNotif.HasLegacyPerksUpdate)
                        {
                            currentPayload.CachedLegacyPerks = legacyNotif.LegacyPerks;
                        }
                    }
                }

                while (_playerRegistry.BillingSyncQueue.TryDequeue(out var billingSyncNotif))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, billingSyncNotif.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.SetPremiumCurrency(billingSyncNotif.PremiumDiamondsBalance);
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.GuildLogisticsDepotUpdateQueue.TryDequeue(out var depotNotif))
                {
                    if (_guildMembersIndex.TryGetValue(depotNotif.GuildId, out var depotMembers))
                    {
                        foreach (long memberId in depotMembers)
                        {
                            ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, memberId);
                            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                            {
                                currentPayload.GuildLogisticsCurrentStock = depotNotif.CurrentStock;
                                currentPayload.GuildLogisticsTargetRequirement = depotNotif.TargetRequirement;
                                currentPayload.CachedGuildLogisticsLevel = depotNotif.Level;
                            }
                        }
                    }
                }

                while (_playerRegistry.GuildCombatSimulationUpdateQueue.TryDequeue(out var combatNotif))
                {
                    // Two guilds are in this match - a player's fixed
                    // per-session GuildId can only ever match one of them,
                    // so no dedup is needed when both index lookups happen
                    // to return non-empty lists.
                    if (_guildMembersIndex.TryGetValue(combatNotif.AttackingGuildId, out var attackingMembers))
                    {
                        foreach (long memberId in attackingMembers)
                        {
                            ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, memberId);
                            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                            {
                                currentPayload.CombatSimulationMatchId = combatNotif.MatchId;
                                currentPayload.CombatSimulationTurnCounter = combatNotif.TurnCounter;
                                currentPayload.CombatSimulationDamageDelta = combatNotif.DamageDelta;
                            }
                        }
                    }

                    if (combatNotif.DefendingGuildId != combatNotif.AttackingGuildId &&
                        _guildMembersIndex.TryGetValue(combatNotif.DefendingGuildId, out var defendingMembers))
                    {
                        foreach (long memberId in defendingMembers)
                        {
                            ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, memberId);
                            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                            {
                                currentPayload.CombatSimulationMatchId = combatNotif.MatchId;
                                currentPayload.CombatSimulationTurnCounter = combatNotif.TurnCounter;
                                currentPayload.CombatSimulationDamageDelta = combatNotif.DamageDelta;
                            }
                        }
                    }
                }

                while (_playerRegistry.GuildRaidBossUpdateQueue.TryDequeue(out var raidNotif))
                {
                    if (_guildMembersIndex.TryGetValue(raidNotif.GuildId, out var raidMembers))
                    {
                        foreach (long memberId in raidMembers)
                        {
                            ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, memberId);
                            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                            {
                                currentPayload.CachedGuildRaidTier = raidNotif.RaidTier;
                                currentPayload.CachedGuildRaidBossCurrentHp = raidNotif.RaidBossCurrentHp;
                                currentPayload.CachedGuildRaidBossMaxHp = raidNotif.RaidBossMaxHp;
                            }
                        }
                    }
                }

                while (_playerRegistry.MentorshipContractUpdateQueue.TryDequeue(out var mentorshipNotif))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, mentorshipNotif.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.ActiveMentorPlayerId = mentorshipNotif.MentorPlayerId;
                        currentPayload.MentorshipExpBonusMultiplier = mentorshipNotif.ExpBonusMultiplier;
                        currentPayload.ActiveMentorshipContractCount = mentorshipNotif.ActiveContractCount;
                        if (mentorshipNotif.XpPenaltyExpiresEpoch > 0)
                        {
                            currentPayload.XpPenaltyExpiresEpoch = mentorshipNotif.XpPenaltyExpiresEpoch;
                        }
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.MailClaimRequestQueue.TryDequeue(out var req))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, req.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        if (req.HasItem && currentPayload.InventorySpaceRemaining <= 0)
                        {
                            SafeDispatchAsync("MailClaim.Reject", req.PlayerId, async () => { await _mailboxEngine.CommitMailClaimAsync(req.MailId, false); });
                        }
                        else
                        {
                            if (req.HasItem) currentPayload.InventorySpaceRemaining--;
                            currentPayload.AddGold(req.GoldAttachment);
                            currentPayload.IsDirty = true;
                            SafeDispatchAsync("MailClaim.Accept", req.PlayerId, async () => { await _mailboxEngine.CommitMailClaimAsync(req.MailId, true); });
                        }
                    }
                }

                while (_playerRegistry.BankWithdrawRequestQueue.TryDequeue(out var req))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, req.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        if (currentPayload.InventorySpaceRemaining <= 0)
                        {
                            SafeDispatchAsync("BankWithdraw.Reject", req.PlayerId, async () => { await _mailboxEngine.CommitBankWithdrawAsync(req.BankId, false); });
                        }
                        else
                        {
                            currentPayload.InventorySpaceRemaining--;
                            currentPayload.IsDirty = true;
                            SafeDispatchAsync("BankWithdraw.Accept", req.PlayerId, async () => { await _mailboxEngine.CommitBankWithdrawAsync(req.BankId, true); });
                        }
                    }
                }

                while (_networkSystem.CommandQueue.TryDequeue(out var cmdWrapper))
                {
                    var cmd = cmdWrapper.Packet;
                    long routingPlayerId = cmdWrapper.PlayerId;

                    if (cmd.Command == CommandType.InitiateNodeMigration)
                    {
                        long pId = routingPlayerId;
                        if (_activePlayers.ContainsKey(pId))
                        {
                            // OUTBOUND Trigger
                            if (_liveSessionContexts.TryGetValue(pId, out var sessionContext))
                            {
                                if (sessionContext.TryStartMigration())
                                {
                                    ref var payload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, pId);
                                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref payload))
                                    {
                                        payload.IsSuspended = true; // Halts local processing in the 10 Hz loop (which runs after commands)
                                        var stateDump = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new System.ReadOnlySpan<TickStatePayload>(ref payload)).ToArray();
                                        
                                        uint token = cmd.MigrationToken;
                                        SafeDispatchAsync("NodeMigration.Outbound", pId, async () => {
                                            if (_redis != null && _redis.IsConnected)
                                            {
                                                var redisDb = _redis.GetDatabase();
                                                await redisDb.StringSetAsync($"migration:{token}", stateDump, System.TimeSpan.FromSeconds(30));
                                            }
                                        });

                                        TerminateSessionForSecurity(pId);
                                    }
                                }
                            }
                        }
                        else
                        {
                            // INBOUND Handshake
                            uint token = cmd.MigrationToken;
                            _playerRegistry.RegisterPlayer(pId);
                            SafeDispatchAsync("NodeMigration.Inbound", pId, async () => {
                                if (_redis != null && _redis.IsConnected)
                                {
                                    var redisDb = _redis.GetDatabase();
                                    var redisVal = await redisDb.StringGetDeleteAsync($"migration:{token}");
                                    if (redisVal.HasValue)
                                    {
                                        byte[] stateDump = redisVal!;
                                        TickStatePayload payload;
                                        // Restrict the ref struct span to a synchronous scope
                                        unsafe
                                        {
                                            fixed (byte* ptr = stateDump)
                                            {
                                                payload = System.Runtime.InteropServices.MemoryMarshal.Read<TickStatePayload>(new System.ReadOnlySpan<byte>(ptr, stateDump.Length));
                                            }
                                        }
                                        payload.IsSuspended = false;
                                        
                                        _readyLogins.Enqueue(payload);
                                    }
                                    else
                                    {
                                        _networkSystem.ForceDisconnect(pId);
                                    }
                                }
                                else
                                {
                                    _networkSystem.ForceDisconnect(pId);
                                }
                            });
                        }
                        continue;
                    }
                    else if (cmd.Command == CommandType.ConsumeConsumableAsset)
                    {
                        long pId = routingPlayerId;
                        uint itemId = cmd.ConsumableItemId;
                        if (_liveSessionContexts.TryGetValue(pId, out var sessionContext))
                        {
                            // Validate with ClientCommandValidator
                            ref var payload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, pId);
                            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref payload))
                            {
                                if (!ClientCommandValidator.ValidateConsumableRequest(ref cmd, sessionContext))
                                {
                                    TerminateSessionForSecurity(pId);
                                    continue;
                                }
                            }

                            SafeDispatchAsync("ConsumeConsumable", pId, async () => {
                                using var context = await _contextFactory.CreateDbContextAsync();
                                using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                                try
                                {
                                    var itemIdStr = itemId.ToString();
                                    var items = await context.EquipmentInstances.FromSqlInterpolated($"SELECT * FROM equipment_instances WHERE PlayerId = {pId} AND BaseItemId = {itemIdStr} FOR UPDATE").ToListAsync();
                                    if (items.Count > 0)
                                    {
                                        var targetItem = items[0];
                                        context.EquipmentInstances.Remove(targetItem);
                                        
                                        var affixes = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, int>>(targetItem.AffixPayload) ?? new System.Collections.Generic.Dictionary<string, int>();
                                        
                                        uint bitmask = 0;
                                        var signal = new ConsumableApplicationSignal
                                        {
                                            StatusEffectModifierBitmask = 0,
                                            DurationTicks = 600
                                        };

                                        if (affixes.TryGetValue("HealingMultiplier", out int healMult))
                                        {
                                            bitmask |= 1; 
                                            unsafe
                                            {
                                                signal.ActiveModifiers[0] = healMult;
                                            }
                                        }
                                        if (affixes.TryGetValue("PotencyMultiplier", out int potMult))
                                        {
                                            bitmask |= 2;
                                            unsafe
                                            {
                                                signal.ActiveModifiers[1] = potMult;
                                            }
                                        }

                                        signal.StatusEffectModifierBitmask = bitmask;
                                        sessionContext.ConsumableIngestionQueue.Enqueue(signal);

                                        await context.SaveChangesAsync();
                                        await transaction.CommitAsync();
                                    }
                                    else
                                    {
                                        await transaction.RollbackAsync();
                                    }
                                }
                                catch
                                {
                                    await transaction.RollbackAsync();
                                }
                            });
                        }
                        continue;
                    }
                    else if (cmd.Command == CommandType.Login)
                    {
                        long tId = cmd.TargetId;
                        _playerRegistry.RegisterPlayer(tId);
                        SafeDispatchAsync("Login", tId, async () => {
                            var payload = await _checkpointManager.LoadPlayerState(tId);
                            
                            long currentUnixTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            if (!ClientCommandValidator.ValidateLoginTime(ref payload, currentUnixTimestamp))
                            {
                                _playerRegistry.UnregisterPlayer(tId);
                                _networkSystem.ForceDisconnect(routingPlayerId);
                                return;
                            }

                            await using (var offlineDb = await _contextFactory.CreateDbContextAsync())
                            {
                                payload = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(offlineDb, payload, currentUnixTimestamp);
                            }

                            // Modul: persist the offline catch-up immediately
                            // rather than waiting for the next regular
                            // checkpoint boundary (~5 minutes of active play)
                            // or disconnect - a substantial multi-day
                            // catch-up sitting only in memory until then
                            // would be lost entirely if the server crashed
                            // or the player disconnected before that
                            // boundary was ever reached. Only worth the
                            // extra write when ExtrapolateOfflineProgressAsync
                            // actually applied a delta (IsDirty set) - a
                            // same-second relogin with nothing to catch up
                            // does not need one.
                            if (payload.IsDirty)
                            {
                                _checkpointManager.FlushStateAndAdvance(ref payload);
                            }

                            _readyLogins.Enqueue(payload);
                        });
                        continue;
                    }

                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, routingPlayerId);

                    if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        continue;
                    }

                    bool isInternalCommand = cmd.Command == CommandType.ReloadState;
                    bool isChronoManipulationCommand = cmd.Command == CommandType.ActivateChronoBoost ||
                        cmd.Command == CommandType.ConsumeTimeWarpCore;

                    // Epoch interception gate: reject commands from desynchronized clients.
                    if (!isInternalCommand && !isChronoManipulationCommand && !ClientCommandValidator.ValidateEpochSynchronization(ref currentPayload, ref cmd))
                    {
                        TerminateSessionForSecurity(routingPlayerId);
                        continue;
                    }

                    if (!isInternalCommand && !ClientCommandValidator.ValidateCommand(ref currentPayload, (byte)cmd.Command))
                    {
                        TerminateSessionForSecurity(routingPlayerId);
                        continue;
                    }

                    if (!isInternalCommand && !ClientCommandValidator.ValidateNoAntiCheatPayload(ref currentPayload, ref cmd))
                    {
                        _antiCheatTelemetryEngine.RequestShadowBan(routingPlayerId, 54, 2);
                        continue;
                    }

                    if (!isInternalCommand && !ClientCommandValidator.ValidateNoPushCompliancePayload(ref currentPayload, ref cmd))
                    {
                        TerminateSessionForSecurity(routingPlayerId);
                        continue;
                    }

                    if (cmd.Command == CommandType.AntiCheatChallengeResponse)
                    {
                        if (!ClientCommandValidator.ValidateAntiCheatChallengeResponse(ref currentPayload, ref cmd))
                        {
                            _antiCheatTelemetryEngine.RequestShadowBan(routingPlayerId, 54, 3);
                        }
                        else
                        {
                            currentPayload.ActiveChallengeAnswered = 1;
                            currentPayload.ActiveChallengeSeed = 0;
                        }
                    }
                    else if (cmd.Command == CommandType.MarketListItem || cmd.Command == CommandType.MarketBuyItem)
                    {
                        if (!ClientCommandValidator.ValidateMarketCommands(ref currentPayload, (byte)cmd.Command, cmd.TargetId, cmd.LimitPrice))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }
                        
                        currentPayload.IsSuspended = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);

                        long pId = currentPayload.PlayerId;
                        long targetId = cmd.TargetId;
                        long price = cmd.LimitPrice;
                        bool isBuy = cmd.Command == CommandType.MarketBuyItem;
                        bool hasSpace = currentPayload.InventorySpaceRemaining > 0;

                        SafeDispatchAsync("Market.EscrowOrder", pId, async () => {
                            if (isBuy)
                            {
                                await _escrowEngine.BuyItemAsync(pId, targetId, hasSpace);
                            }
                            else
                            {
                                await _escrowEngine.ListItemAsync(pId, targetId, price);
                            }
                            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                        });
                    }
                    else if (cmd.Command == CommandType.ChangeActivity)
                    {
                        if (!ClientCommandValidator.ValidateChangeActivityRequest(ref currentPayload, cmd.TargetId))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        currentPayload.ActiveActivityId = cmd.TargetId;
                        currentPayload.CurrentProgressTicks = 0;
                        currentPayload.CurrentMonsterId = 0;
                        currentPayload.CurrentMonsterHp = 0;
                        currentPayload.CombatTargetTickAccumulator = 0;
                        currentPayload.GatheringProgressTicks = 0;
                    }
                    else if (cmd.Command == CommandType.ContributeToGuild)
                    {
                        if (!ClientCommandValidator.ValidateGuildContributions(ref currentPayload, cmd.LimitPrice))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long guildId = currentPayload.GuildId;
                        long quantity = cmd.LimitPrice;
                        int itemDefinitionId = (int)cmd.TargetId;
                        long pId = currentPayload.PlayerId;

                        if (guildId > 0 && quantity > 0)
                        {
                            SafeDispatchAsync("Guild.Contribution", pId, async () => {
                                await _guildLogisticsEngine.ExecuteGuildContributionAsync(pId, guildId, quantity, itemDefinitionId);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.ExecuteForgeFusion)
                    {
                        if (!ClientCommandValidator.ValidateFusionCommand(ref currentPayload, cmd.TargetId, cmd.SecondaryId, cmd.TertiaryId))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        currentPayload.IsSuspended = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);
                        
                        long pId = currentPayload.PlayerId;
                        long cTargetId = cmd.TargetId;
                        long cSecId = cmd.SecondaryId;
                        long cTerId = cmd.TertiaryId;

                        SafeDispatchAsync("Forge.Fusion", pId, async () => {
                            var result = await _forgeEngine.ExecuteFusionAsync(pId, cTargetId, cSecId, cTerId);
                            if (result == ForgeSplicingResult.InvalidRequest)
                            {
                                _networkSystem.ForceDisconnect(pId);
                                return;
                            }
                            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                        });
                    }
                    else if (cmd.Command == CommandType.RerollItemAffix)
                    {
                        if (!ClientCommandValidator.ValidateAffixReroll(ref currentPayload, cmd.TargetId, cmd.LimitPrice))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        currentPayload.IsSuspended = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);
                        
                        long pId = currentPayload.PlayerId;
                        long cTargetId = cmd.TargetId;
                        int affixIndex = cmd.LimitPrice;

                        SafeDispatchAsync("Affix.Reroll", pId, async () => {
                            await _rerollEngine.ExecuteRerollAsync(pId, cTargetId, affixIndex);
                            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                        });
                    }
                    else if (cmd.Command == CommandType.ExecuteBreeding)
                    {
                        if (!ClientCommandValidator.ValidateBreedingRequest(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        var patId = cmd.TargetGuid;
                        var matId = cmd.SecondaryGuid;

                        SafeDispatchAsync("Breeding.Execute", pId, async () => {
                            await _breedingEngine.ExecuteBreedingAsync(pId, patId, matId);
                        });
                    }
                    else if (cmd.Command == CommandType.InitializeCrafting)
                    {
                        long pId = currentPayload.PlayerId;
                        int resultItemId = (int)cmd.TargetId;
                        
                        SafeDispatchAsync("Crafting.Initialize", pId, async () => {
                            await _craftingEngine.ExecuteCraftingAsync(pId, resultItemId);
                        });
                    }
                    else if (cmd.Command == CommandType.CraftItem)
                    {
                        if (!ClientCommandValidator.ValidateCraftingRequest(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        uint recipeId = cmd.TargetRecipeId;
                        uint slotIndex = cmd.CraftingSlotIndex;
                        uint tickToken = (uint)currentPayload.LogicEpochCounter;
                        
                        SafeDispatchAsync("Crafting.Equipment", pId, async () => {
                            await _craftingEngine.ExecuteEquipmentCraftingAsync(pId, recipeId, slotIndex, tickToken);
                        });
                    }
                    else if (cmd.Command == CommandType.UpgradeBuilding)
                    {
                        if (!ClientCommandValidator.ValidateVillageManagementRequest(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        uint buildingId = cmd.TargetBuildingId;
                        
                        SafeDispatchAsync("Village.UpgradeBuilding", pId, async () => {
                            await _villageManagementEngine.ExecuteUpgradeBuildingAsync(pId, buildingId);
                        });
                    }
                    else if (cmd.Command == CommandType.EvictVillager)
                    {
                        if (!ClientCommandValidator.ValidateVillageManagementRequest(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        uint villagerSlot = cmd.TargetVillagerSlot;

                        SafeDispatchAsync("Village.EvictVillager", pId, async () => {
                            await _villageManagementEngine.ExecuteEvictVillagerAsync(pId, villagerSlot);
                        });
                    }
                    else if (cmd.Command == CommandType.UpgradeTool)
                    {
                        if (!ClientCommandValidator.ValidateUpgradeRequest(ref currentPayload, (byte)cmd.Command, 0))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        
                        SafeDispatchAsync("Village.UpgradeTool", pId, async () => {
                            await _villageBuildingEngine.ExecuteUpgradeToolAsync(pId);
                        });
                    }
                    else if (cmd.Command == CommandType.AssignMentor)
                    {
                        // TODO: Add validator check if needed, but the prompt says: ValidateMentorshipAssignment in ClientCommandValidator
                        if (!ClientCommandValidator.ValidateMentorshipAssignment(ref currentPayload, cmd.TargetGuid, (int)cmd.LimitPrice))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        Guid charId = cmd.TargetGuid;
                        int slotIndex = cmd.LimitPrice;
                        
                        SafeDispatchAsync("Mentorship.AssignMentor", pId, async () => {
                            await _mentorshipEngine.ExecuteAssignMentorAsync(pId, charId, slotIndex);
                            // Trigger full reload so mentor count reflects accurately from DB
                            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                        });
                    }
                    else if (cmd.Command == CommandType.EstablishMentorship)
                    {
                        if (!ClientCommandValidator.ValidateMentorshipRequest(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long menteePlayerId = currentPayload.PlayerId;
                        long mentorPlayerId = cmd.TargetPlayerId;

                        SafeDispatchAsync("Mentorship.Establish", menteePlayerId, async () => {
                            var result = await _mentorshipEngine.EstablishMentorshipContractAsync(menteePlayerId, mentorPlayerId);
                            if (result == MentorshipContractResult.InvalidRequest)
                            {
                                _networkSystem.PurgeTokensForPlayer(menteePlayerId);
                                _networkSystem.ForceDisconnect(menteePlayerId);
                            }
                        });
                    }
                    else if (cmd.Command == CommandType.TerminateMentorship)
                    {
                        if (!ClientCommandValidator.ValidateMentorshipRequest(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long requestingPlayerId = currentPayload.PlayerId;
                        long counterpartyPlayerId = cmd.TargetPlayerId;

                        SafeDispatchAsync("Mentorship.Terminate", requestingPlayerId, async () => {
                            await _mentorshipEngine.ExecuteTerminateMentorshipAsync(requestingPlayerId, counterpartyPlayerId);
                        });
                    }
                    else if (cmd.Command == CommandType.ContributeToWarSupply)
                    {
                        if (currentPayload.GuildId > 0 && currentPayload.ActiveGuildWarId > 0 && cmd.SecondaryId > 0 && cmd.TertiaryId > 0)
                        {
                            currentPayload.IsSuspended = true;
                            _checkpointManager.FlushStateAndAdvance(ref currentPayload);
                            _guildWarEngine.SupplyChainQueue.Enqueue(new GuildWarSupplyContribution
                            {
                                PlayerId = currentPayload.PlayerId,
                                CommodityId = cmd.SecondaryId,
                                QuantityToBurn = cmd.TertiaryId
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.PlaceLimitOrder)
                    {
                        currentPayload.IsSuspended = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);

                        long pId = currentPayload.PlayerId;
                        bool isBuy = cmd.IsBuy == 1;
                        long instanceId = cmd.TargetId;
                        long price = cmd.LimitPrice;
                        int qualityTier = cmd.QualityTier;
                        string baseItemId = isBuy ? $"ItemType_{cmd.TargetId}" : ""; 

                        SafeDispatchAsync("Market.LimitOrder", pId, async () => {
                            await _marketEngine.PlaceLimitOrderAsync(pId, isBuy, instanceId, price, baseItemId, qualityTier);
                            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                        });
                    }
                    else if (cmd.Command == CommandType.ClaimMailItem)
                    {
                        if (!ClientCommandValidator.ValidateMailCommands(ref currentPayload, (byte)cmd.Command, cmd.TargetId))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        long mailId = cmd.TargetId;
                        SafeDispatchAsync("Mail.Claim", pId, async () => {
                            await _mailboxEngine.ClaimMailItemAsync(pId, mailId);
                        });
                    }
                    else if (cmd.Command == CommandType.ClaimAchievementReward)
                    {
                        if (!ClientCommandValidator.ValidateAchievementClaimRequest(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        uint achievementId = cmd.TargetAchievementId;

                        if (_liveSessionContexts.TryGetValue(pId, out var sessionContext))
                        {
                            _playerRegistry.AchievementClaimQueue.Enqueue(new AchievementClaimRequest
                            {
                                PlayerId = pId,
                                AchievementId = achievementId,
                                LiveSession = sessionContext
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.ClaimBattlePassReward)
                    {
                        if (!ClientCommandValidator.ValidateBattlePassClaimRequest(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        uint milestoneIndex = cmd.TargetMilestoneIndex;
                        uint seasonalXp = currentPayload.AccumulatedSeasonalXp;
                        uint passLevel = currentPayload.ActiveChroniclePassLevel;

                        if (_liveSessionContexts.TryGetValue(pId, out var context))
                        {
                            var req = new BattlePassClaimRequest
                            {
                                TargetMilestoneIndex = milestoneIndex,
                                AccumulatedSeasonalXp = seasonalXp,
                                ActiveChroniclePassLevel = passLevel
                            };
                            context.TryEnqueueBattlePassClaim(in req);
                        }
                    }
                    else if (cmd.Command == CommandType.DepositToBank)
                    {
                        long pId = currentPayload.PlayerId;
                        long instanceId = cmd.TargetId;
                        SafeDispatchAsync("Bank.Deposit", pId, async () => {
                            await _mailboxEngine.DepositToBankAsync(pId, instanceId);
                        });
                    }
                    else if (cmd.Command == CommandType.WithdrawFromBank)
                    {
                        long pId = currentPayload.PlayerId;
                        long bankId = cmd.TargetId;
                        SafeDispatchAsync("Bank.Withdraw", pId, async () => {
                            await _mailboxEngine.WithdrawFromBankAsync(pId, bankId);
                        });
                    }
                    else if (cmd.Command == CommandType.ActivateChronoBoost)
                    {
                        uint bankedSeconds = (uint)ChronoBufferEngine.ClampBankedSeconds(currentPayload.BankedChronoSeconds);
                        if (!ClientCommandValidator.ValidateChronoManipulation(ref currentPayload, ref cmd, bankedSeconds))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        ActivateChronoAcceleration(ref currentPayload, (int)cmd.RequestedSpeedMultiplier);
                    }
                    else if (cmd.Command == CommandType.ConsumeTimeWarpCore)
                    {
                        uint bankedSeconds = (uint)ChronoBufferEngine.ClampBankedSeconds(currentPayload.BankedChronoSeconds);
                        if (!ClientCommandValidator.ValidateChronoManipulation(ref currentPayload, ref cmd, bankedSeconds))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        uint requestedSeconds = cmd.ChronoWarpDurationSeconds != 0 ? cmd.ChronoWarpDurationSeconds : cmd.ChronoSecondsRequested;
                        uint remainingBuffTicks = 0U;
                        int potencyModifierPct = 0;
                        if (_liveSessionContexts.TryGetValue(currentPayload.PlayerId, out var chronoSessionContext))
                        {
                            remainingBuffTicks = chronoSessionContext.ActiveStatusEffects.RemainingBuffDurationTicks;
                            potencyModifierPct = ReadActiveStatusModifier(ref chronoSessionContext.ActiveStatusEffects, 1);
                        }

                        ExecuteInstantTimeWarp(ref currentPayload, requestedSeconds, cmd.ChronoTargetSlot, remainingBuffTicks, potencyModifierPct);
                        
                        _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = routingPlayerId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                    }
                    else if (cmd.Command == CommandType.RegisterGuildDefense)
                    {
                        if (!ClientCommandValidator.ValidateGuildWarAction(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        RegisterGuildDefenseAsync(currentPayload.GuildId).GetAwaiter().GetResult();
                    }
                    else if (cmd.Command == CommandType.SubmitShardAttack)
                    {
                        if (!ClientCommandValidator.ValidateGuildWarAction(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        var attackResult = SubmitShardAttackAsync(currentPayload.GuildId, currentPayload.GlobalNodeRemainingHp, cmd.TargetMatchUuid, cmd.ClientPredictedDamage, cmd.IsBuy != 0).GetAwaiter().GetResult();
                        var meshResult = attackResult.Response;
                        if (meshResult.ProcessingStatus == 1U || meshResult.ProcessingStatus == 2U || meshResult.ProcessingStatus == 4U)
                        {
                            TelemetryStreamer.TryWrite(new TelemetryEvent
                            {
                                PlayerId = currentPayload.PlayerId,
                                EventType = 3,
                                Value1 = 50,
                                Value2 = (int)meshResult.ProcessingStatus,
                                Timestamp = Environment.TickCount64
                            });
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        if (meshResult.ProcessingStatus == 0U)
                        {
                            currentPayload.ActiveCrossShardMatchId = cmd.TargetMatchUuid;
                            currentPayload.GlobalNodeRemainingHp = meshResult.GlobalNodeRemainingHp;
                            currentPayload.ActiveMatchMmr = attackResult.ActiveMatchMmr;
                            currentPayload.IsDirty = true;
                        }
                    }
                    else if (cmd.Command == CommandType.ReportTelemetryBurst)
                    {
                        if (!ClientCommandValidator.ValidateTelemetryBurst(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        _telemetryStreamingEngine.EnqueueClientTelemetryBurst(currentPayload.AccountId, currentPayload.PlayerId, cmd);
                    }
                    else if (cmd.Command == CommandType.PingNetworkDiagnostics)
                    {
                        if (!ClientCommandValidator.ValidatePingNetworkDiagnostics(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }
                        
                        currentPayload.NetworkDiagnosticsToken = cmd.NetworkDiagnosticsToken;
                        currentPayload.IsDirty = true;
                        continue;
                    }
                    else if (cmd.Command == CommandType.ContributeToGuild)
                    {
                        currentPayload.IsSuspended = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);

                        long pId = currentPayload.PlayerId;
                        long guildId = cmd.SecondaryId; 
                        bool isGold = cmd.TargetId == 0;
                        long instanceId = cmd.TargetId;
                        long goldAmount = cmd.LimitPrice; 

                        SafeDispatchAsync("Guild.ContributeGoldOrEquipment", pId, async () => {
                            if (isGold)
                            {
                                await _guildEngine.ContributeGoldAsync(pId, guildId, goldAmount);
                            }
                            else
                            {
                                await _guildEngine.ContributeEquipmentAsync(pId, guildId, instanceId);
                            }
                            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                        });
                    }
                    else if (cmd.Command == CommandType.ReloadState)
                    {
                        currentPayload.IsSuspended = false;
                    }
                    else if (cmd.Command == CommandType.ConsumeChronoCore)
                    {
                        if (!ClientCommandValidator.ValidateChronoCommands(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        long chronoCoreItemId = cmd.TargetId;

                        SafeDispatchAsync("Chrono.ConsumeCore", pId, async () => {
                            await _chronoCoreEngine.ConsumeChronoCoreAsync(pId, chronoCoreItemId);
                        });
                    }
                    else if (cmd.Command == CommandType.PurchaseLegacyUnlocks)
                    {
                        if (!ClientCommandValidator.ValidateLegacyStoreRequest(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        uint unlockId = cmd.TargetUnlockId;
                        uint slotIndex = cmd.RequestedSlotIndex;

                        SafeDispatchAsync("Legacy.PurchaseUnlock", pId, async () => {
                            await _legacyStoreEngine.PurchaseLegacyUnlockAsync(pId, unlockId, slotIndex);
                        });
                    }
                    else if (cmd.Command == CommandType.DepositGuildMaterial)
                    {
                        if (!ClientCommandValidator.ValidateGuildDepositRequest(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        long guildId = currentPayload.GuildId;
                        uint materialId = cmd.MaterialId;
                        uint quantity = cmd.DepositQuantity;

                        SafeDispatchAsync("Guild.DepositMaterial", pId, async () => {
                            await _guildLogisticsDepotEngine.DepositMaterialAsync(pId, guildId, materialId, quantity);
                        });
                    }
                    else if (cmd.Command == CommandType.LaunchGuildRaid)
                    {
                        long raidGuildId = currentPayload.GuildId;
                        long raidRequestingPlayerId = currentPayload.PlayerId;
                        if (raidGuildId > 0 && _guildRaidEngine != null)
                        {
                            // No single player to disconnect on failure here -
                            // raidGuildId identifies a guild, not a player, and
                            // passing it as playerIdToDisconnectOnFailure would
                            // force-disconnect whichever unrelated player, if
                            // any, happens to share that numeric id. Leader-only
                            // enforcement happens inside TryStartRaidAsync
                            // itself, against the locked GuildMembers row - a
                            // non-leader's request simply rolls back with no
                            // effect, matching every other rejected-command
                            // path in this engine.
                            SafeDispatchAsync("Guild.LaunchRaid", 0L, async () => {
                                await _guildRaidEngine.TryStartRaidAsync(raidGuildId, raidRequestingPlayerId);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.EquipItem)
                    {
                        long equipPlayerId = currentPayload.PlayerId;
                        long equipItemId = cmd.TargetId;
                        if (equipItemId > 0 && _equipmentSlotEngine != null)
                        {
                            SafeDispatchAsync("Equipment.Equip", equipPlayerId, async () => {
                                await _equipmentSlotEngine.EquipItemAsync(equipPlayerId, equipItemId);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.UnequipItem)
                    {
                        long unequipPlayerId = currentPayload.PlayerId;
                        bool isArmorSlot = cmd.IsBuy != 0;
                        if (_equipmentSlotEngine != null)
                        {
                            SafeDispatchAsync("Equipment.Unequip", unequipPlayerId, async () => {
                                await _equipmentSlotEngine.UnequipItemAsync(unequipPlayerId, isArmorSlot);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.ExecuteCombatTurn)
                    {
                        if (!ClientCommandValidator.ValidateCombatTurnRequest(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        long guildId = currentPayload.GuildId;
                        ClientCommandPacket capturedCommand = cmd;

                        SafeDispatchAsync("GuildCombat.ExecuteTurn", pId, async () => {
                            var result = await _guildCombatSimulationEngine.ExecuteCombatTurnAsync(pId, guildId, capturedCommand);
                            if (result == GuildCombatTurnResult.InvalidRequest || result == GuildCombatTurnResult.NotFound)
                            {
                                _networkSystem.PurgeTokensForPlayer(pId);
                                _networkSystem.ForceDisconnect(pId);
                            }
                        });
                    }
                    else if (cmd.Command == CommandType.ToggleChronoAcceleration)
                    {
                        int requestedMultiplier = (int)cmd.TargetId;
                        if (requestedMultiplier == 1 || requestedMultiplier == 2 || requestedMultiplier == 4)
                        {
                            if (currentPayload.AccumulatedTimeBankMs > 0)
                            {
                                currentPayload.SpeedMultiplier = requestedMultiplier;
                                currentPayload.IsDirty = true;
                            }
                            else if (requestedMultiplier == 1)
                            {
                                currentPayload.SpeedMultiplier = 1;
                                currentPayload.IsDirty = true;
                            }
                        }
                    }
                    else if (cmd.Command == CommandType.UpdateAutoEatThreshold)
                    {
                        int thresholdValue = cmd.LimitPrice;
                        if (!ClientCommandValidator.ValidateCombatConfiguration(ref currentPayload, thresholdValue))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }
                        currentPayload.AutoEatThreshold = thresholdValue;
                        currentPayload.IsDirty = true;
                    }
                    else if (cmd.Command == CommandType.AttackWorldBoss)
                    {
                        if (!ClientCommandValidator.ValidateWorldBossAttackRequest(
                            ref currentPayload,
                            ref cmd,
                            WorldBossEngine.ActiveBossInstanceId,
                            _worldBossEngine.IsBossDead(),
                            _worldBossEngine.IsEventActive))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        // Modul 06/15: Auto-Eat food depletion also closes a
                        // player's World Boss battle session, alongside the
                        // 300-second cap enforced inside WorldBossEngine itself.
                        bool attackAutoEatDepleted = currentPayload.Food1_Count <= 0 && currentPayload.Food2_Count <= 0 && currentPayload.Food3_Count <= 0;
                        _worldBossEngine.QueueAttack(currentPayload.PlayerId, cmd.TargetedBossId, cmd.ClientPredictedDamage, attackAutoEatDepleted);
                    }
                    else if (cmd.Command == CommandType.RegisterPushToken)
                    {
                        if (!ClientCommandValidator.ValidateDeviceRegistrationRequest(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        byte[] deviceToken = CopyDeviceTokenBytes(ref cmd);
                        _pushNotificationTriggerEngine.QueueDeviceRegistration(currentPayload.PlayerId, deviceToken, cmd.TargetPlatformFamily);
                    }
                    else if (cmd.Command == CommandType.TriggerGdprPurge)
                    {
                        if (!ClientCommandValidator.ValidateGdprPurgeRequest(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        _compliancePurgeEngine.QueueGdprPurge(currentPayload.PlayerId);
                        TerminateSessionForSecurity(routingPlayerId);
                    }
                    else if (cmd.Command == CommandType.SwitchLanguage)
                    {
                        if (!ClientCommandValidator.ValidateLanguageSwitchRequest(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        currentPayload.ActiveLanguageState = cmd.TargetLanguageId;
                        currentPayload.IsDirty = true;
                    }
                    else if (cmd.Command == CommandType.RegisterWorldBossDamage)
                    {
                        if (!ClientCommandValidator.ValidateWorldBossRegistration(ref currentPayload, cmd.TargetId))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        bool registerAutoEatDepleted = currentPayload.Food1_Count <= 0 && currentPayload.Food2_Count <= 0 && currentPayload.Food3_Count <= 0;
                        _worldBossEngine.RegisterDamage(currentPayload.PlayerId, cmd.TargetId, registerAutoEatDepleted);
                    }
                    else if (cmd.Command == CommandType.Logout)
                    {
                        currentPayload.LastLogoutTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        currentPayload.IsDirty = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);
                        _playerRegistry.UnregisterPlayer(cmd.TargetId);
                        currentPayload.IsSuspended = true;
                        RemoveActivePlayer(routingPlayerId);
                    }
                    else if (cmd.Command == CommandType.SubmitPurchaseReceipt)
                    {
                        long pId = currentPayload.PlayerId;
                        string transactionId = "";
                        unsafe {
                            byte* ptr = cmd.RawTransactionReceipt;
                            transactionId = System.Text.Encoding.UTF8.GetString(ptr, 64).TrimEnd('\0');
                        }
                        string productId = $"Product_{cmd.TargetProductIdHash}";

                        // Modul: the reward amount is no longer taken from
                        // cmd.LimitPrice - a client could previously claim
                        // any premiumAmount it liked over this WebSocket
                        // command with zero receipt verification, the exact
                        // spoofing vector this task exists to close.
                        // VerifyPurchaseAsync now always resolves the amount
                        // itself from productId via
                        // ResolvePremiumDiamondsForProduct. This 64-byte
                        // WebSocket packet was never able to carry a real
                        // signed store receipt anyway (see
                        // RawTransactionReceipt's fixed size) - real
                        // purchase verification is VerifyReceiptAsync,
                        // reached only through the REST
                        // /api/v1/billing/verify endpoint, which can carry
                        // an arbitrarily large base64 receipt body.
                        SafeDispatchAsync("Billing.VerifyPurchase", pId, async () => {
                            bool success = await _billingVerificationEngine.VerifyPurchaseAsync(pId, transactionId, productId);
                            if (success) {
                                _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                            }
                        });
                    }
                    else if (cmd.Command == CommandType.SyncBillingStatus)
                    {
                        // Modul: reconciles the live in-memory
                        // TickStatePayload.PremiumCurrency against the
                        // database-authoritative PlayerRecords.
                        // PremiumDiamonds - the client calls this after
                        // returning from a store purchase flow that was
                        // verified through the REST
                        // /api/v1/billing/verify endpoint (see
                        // BillingVerificationEngine.VerifyReceiptAsync),
                        // which writes directly to the database and never
                        // touches this session's in-memory payload. Reads
                        // the balance rather than re-running verification
                        // on a stored receipt, since no such "pending
                        // unapplied record" is ever persisted here - every
                        // receipt is verified synchronously at submission
                        // time by BillingVerificationEngine, either via
                        // that REST endpoint or via
                        // CommandType.SubmitPurchaseReceipt above.
                        long syncPlayerId = currentPayload.PlayerId;
                        SafeDispatchAsync("Billing.SyncStatus", syncPlayerId, async () =>
                        {
                            await using var syncDb = await _contextFactory.CreateDbContextAsync();
                            int? balance = await syncDb.PlayerRecords
                                .AsNoTracking()
                                .Where(p => p.Id == syncPlayerId)
                                .Select(p => (int?)p.PremiumDiamonds)
                                .SingleOrDefaultAsync();

                            if (balance.HasValue)
                            {
                                _playerRegistry.BillingSyncQueue.Enqueue(new BillingSyncNotification
                                {
                                    PlayerId = syncPlayerId,
                                    PremiumDiamondsBalance = balance.Value
                                });
                            }
                        });
                    }
                    else if (cmd.Command == CommandType.ReportUiContextSwitch)
                    {
                        currentPayload.ActiveUiContextBitmask = cmd.ActiveUiContextBitmask;
                        currentPayload.IsDirty = true;
                    }
                    else if (cmd.Command == CommandType.RequestUnlockSkill)
                    {
                        if (!ClientCommandValidator.ValidateSkillCommand(ref currentPayload, cmd.TargetId, (byte)cmd.Command))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        int unlockSkillId = (int)cmd.TargetId;
                        uint unlockSkillBit = 1u << (unlockSkillId - 1);
                        bool alreadyUnlocked = (currentPayload.UnlockedSkillsBitmask & unlockSkillBit) != 0;

                        if (!alreadyUnlocked && ActiveSkillEngine.TryGetSkill(unlockSkillId, out var unlockDef) &&
                            currentPayload.AvailableSkillPoints >= unlockDef.RequiredSkillPointCost)
                        {
                            currentPayload.AvailableSkillPoints -= unlockDef.RequiredSkillPointCost;
                            currentPayload.UnlockedSkillsBitmask |= unlockSkillBit;
                            currentPayload.IsDirty = true;

                            long unlockPlayerId = currentPayload.PlayerId;
                            long unlockEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            SafeDispatchAsync("Skill.PersistUnlock", unlockPlayerId, async () =>
                            {
                                try
                                {
                                    await using var context = await _contextFactory.CreateDbContextAsync();
                                    context.PlayerSkillUnlocks.Add(new PlayerSkillUnlock
                                    {
                                        PlayerId = unlockPlayerId,
                                        SkillId = unlockSkillId,
                                        UnlockedAtEpoch = unlockEpoch
                                    });
                                    await context.SaveChangesAsync();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Failed to persist skill unlock for player {unlockPlayerId}, skill {unlockSkillId}: {ex.Message}");
                                }
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.RequestCastSkill)
                    {
                        if (!ClientCommandValidator.ValidateSkillCommand(ref currentPayload, cmd.TargetId, (byte)cmd.Command))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        int castSkillId = (int)cmd.TargetId;
                        currentPayload.LastSkillCastResultTick++;
                        currentPayload.LastSkillCastId = (byte)castSkillId;
                        currentPayload.LastSkillCastSuccess = 0;

                        if (ActiveSkillEngine.TryGetSkill(castSkillId, out var castDef))
                        {
                            uint castSkillBit = 1u << (castSkillId - 1);
                            bool isUnlocked = (currentPayload.UnlockedSkillsBitmask & castSkillBit) != 0;
                            long nowMs = Environment.TickCount64;
                            long cooldownExpiresAt = ActiveSkillEngine.GetSkillCooldownExpiresAtMs(in currentPayload, castSkillId);
                            bool offCooldown = nowMs >= cooldownExpiresAt;
                            bool hasMana = currentPayload.CurrentMana >= castDef.ManaCost;

                            if (isUnlocked && offCooldown && hasMana)
                            {
                                currentPayload.CurrentMana -= castDef.ManaCost;
                                ActiveSkillEngine.SetSkillCooldownExpiresAtMs(ref currentPayload, castSkillId, nowMs + castDef.CooldownMs);
                                currentPayload.PendingSkillDamageMultiplier = castDef.DamageMultiplierPct / 100f;
                                currentPayload.LastSkillCastSuccess = 1;
                            }
                        }

                        currentPayload.IsDirty = true;
                    }
                }

                while (_readyLogins.TryDequeue(out var readyState))
                {
                    if (!_playerRegistry.IsPlayerOnline(readyState.PlayerId))
                    {
                        continue;
                    }
                    readyState.IsSuspended = false;
                    AddActivePlayer(readyState);
                    _liveSessionContexts.TryAdd(readyState.PlayerId, new LiveSessionContext(readyState.PlayerId, readyState.AccountId));
                }

                foreach (var kvp in _activePlayers)
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, kvp.Key);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload) && !currentPayload.IsSuspended)
                    {
                        if (currentPayload.ActiveChallengeSeed != 0 &&
                            currentPayload.ActiveChallengeAnswered == 0 &&
                            Environment.TickCount64 - currentPayload.ActiveChallengeIssuedAtMs > 500L)
                        {
                            currentPayload.IsQuarantined = true;
                            currentPayload.Quarantine_Active = true;
                            currentPayload.ActiveChallengeAnswered = 1;
                            _antiCheatTelemetryEngine.RequestShadowBan(currentPayload.PlayerId, 54, 4);
                        }

                        if (_liveSessionContexts.TryGetValue(kvp.Key, out var sessionContext))
                        {
                            while (sessionContext.ConsumableIngestionQueue.TryDequeue(out var signal))
                            {
                                sessionContext.ActiveStatusEffects.ActiveStatusEffectModifierBitmask = signal.StatusEffectModifierBitmask;
                                sessionContext.ActiveStatusEffects.RemainingBuffDurationTicks = signal.DurationTicks;
                                unsafe
                                {
                                    for (int i = 0; i < 8; i++)
                                    {
                                        sessionContext.ActiveStatusEffects.ActiveModifiers[i] = signal.ActiveModifiers[i];
                                    }
                                }
                            }
                            if (sessionContext.ActiveStatusEffects.RemainingBuffDurationTicks > 0)
                            {
                                sessionContext.ActiveStatusEffects.RemainingBuffDurationTicks--;
                                if (sessionContext.ActiveStatusEffects.RemainingBuffDurationTicks == 0)
                                {
                                    sessionContext.ActiveStatusEffects.ActiveStatusEffectModifierBitmask = 0;
                                    unsafe
                                    {
                                        for (int i = 0; i < 8; i++)
                                            sessionContext.ActiveStatusEffects.ActiveModifiers[i] = 0;
                                    }
                                }
                            }
                        }

                        // Modul: tick-thread exception isolation. This
                        // foreach runs on the single dedicated tick thread -
                        // an uncaught exception here previously propagated
                        // straight out of EngineLoop and killed the whole
                        // process, taking down every connected player's
                        // session at once over one corrupt payload. The
                        // try/catch itself costs nothing when no exception
                        // is thrown (the .NET JIT does not allocate or
                        // branch-cost a try region on the non-throwing
                        // path), so this does not violate the 10 Hz loop's
                        // zero-allocation discipline - only the actual
                        // catch body (an exceptional, not-every-tick path)
                        // allocates, the same way any other error-logging
                        // call in this codebase does.
                        //
                        // Cannot call RemoveActivePlayer here - this block
                        // is iterating _activePlayers itself via foreach,
                        // and mutating the dictionary mid-enumeration would
                        // throw InvalidOperationException on the very next
                        // MoveNext, defeating the isolation this exists to
                        // provide. Setting IsSuspended instead relies on
                        // this loop's own guard above (line ~1962) to skip
                        // the player on every subsequent tick without
                        // touching the collection's structure; ForceDisconnect
                        // only touches NetworkBroadcastSystem's own
                        // _connectedClients dictionary, never _activePlayers,
                        // so it is safe to call mid-enumeration too.
                        try
                        {
                            ProcessTick(ref currentPayload);
                            _checkpointManager.TrackState(ref currentPayload);
                        }
                        catch (Exception tickException)
                        {
                            Console.WriteLine($"Tick processing failed for PlayerId {currentPayload.PlayerId}: {tickException.Message}");
                            currentPayload.IsSuspended = true;
                            currentPayload.IsDirty = true;
                            _networkSystem.ForceDisconnect(currentPayload.PlayerId);
                        }
                    }
                }

                _ticksSinceLastBroadcast++;
                if (_ticksSinceLastBroadcast >= 10)
                {
                    _metrics.ThrottledPacketsDropped = _networkSystem.GetThrottledCounter();
                    _ticksSinceLastBroadcast = 0;

                    long broadcastSnapshotStartTimestamp = Stopwatch.GetTimestamp();
                    FolkIdleEventSource.Log.BroadcastSnapshotStart(_activePlayers.Count);

                    foreach (var kvp in _activePlayers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, kvp.Key);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                        {
                            if (currentPayload.ActiveChallengeSeed == 0 || currentPayload.ActiveChallengeAnswered != 0)
                            {
                                currentPayload.ActiveChallengeSeed = AntiCheatTelemetryEngine.GenerateChallengeSeed(currentPayload.PlayerId, currentPayload.LogicEpochCounter, _metrics.TotalTicksProcessed);
                                currentPayload.ActiveChallengeIssuedAtMs = Environment.TickCount64;
                                currentPayload.ActiveChallengeAnswered = 0;
                            }

                            byte audioTrackId = 1;
                            if (currentPayload.ActiveActivityId > 0)
                            {
                                if (ContentRegistry.TryGetGatheringNode(currentPayload.ActiveActivityId, out _))
                                {
                                    audioTrackId = 2;
                                }
                                else if (currentPayload.ActiveActivityId == 9999) // World Boss
                                {
                                    audioTrackId = 4;
                                }
                                else
                                {
                                    audioTrackId = 3;
                                }
                            }

                            uint statBitmask = 0;
                            uint statDurTicks = 0;
                            if (_liveSessionContexts.TryGetValue(kvp.Key, out var sessionContextForPacket))
                            {
                                statBitmask = sessionContextForPacket.ActiveStatusEffects.ActiveStatusEffectModifierBitmask;
                                statDurTicks = sessionContextForPacket.ActiveStatusEffects.RemainingBuffDurationTicks;
                            }

                            long packetSerializationStartTimestamp = Stopwatch.GetTimestamp();

                            // Modul: Combat System Overhaul - recomputed here
                            // with the exact same StatsCalculator.Calculate
                            // call and parameter sourcing ProcessTick's own
                            // combat resolution uses (see the identical call
                            // near the monster-attack block), so the
                            // Accuracy/Armor/BlockStrength values broadcast
                            // to the client can never drift from what
                            // actually governed that tick's combat rolls.
                            // TickStatePayload does not cache CombatStats
                            // across the tick/broadcast boundary - this
                            // mirrors the existing "recompute per site from
                            // raw fields" pattern already used at every
                            // other StatsCalculator.Calculate call site.
                            int broadcastActiveAgePhase = 1;
                            int broadcastActiveRaceId = 0;
                            if (currentPayload.Slot1_CharacterId != System.Guid.Empty)
                            {
                                broadcastActiveAgePhase = currentPayload.Slot1_AgePhase;
                                broadcastActiveRaceId = (int)(currentPayload.Slot1_GeneticVector & 0xFF);
                            }
                            var broadcastCombatStats = StatsCalculator.Calculate(currentPayload.STR, currentPayload.DEX, currentPayload.CON, currentPayload.LCK, currentPayload.ActiveOffensivePotionId, currentPayload.ActiveDefensivePotionId, broadcastActiveAgePhase, currentPayload.CompletedAreaFlags, broadcastActiveRaceId, currentPayload.HumanMasteryLevel, currentPayload.VilaMasteryLevel, currentPayload.DraugrMasteryLevel, currentPayload.CachedEquippedFlatAttack, currentPayload.CachedEquippedFlatDefense, currentPayload.CachedEquippedCritBonus, currentPayload.CachedEquippedLuckBonus, currentPayload.IsEpicMutation, currentPayload.LocusSpeed, currentPayload.LocusCrit);

                            // Modul: onboarding signal - true only while the
                            // account's first character exists but has never
                            // aged a single tick, matching the
                            // CharacterRecord.AgeTicks == 0 condition
                            // UiLoginWindow/UiTutorialController key off of.
                            byte isFreshAccount = (currentPayload.Slot1_CharacterId != System.Guid.Empty && currentPayload.Slot1_AgeTicks == 0) ? (byte)1 : (byte)0;

                            StateUpdatePacket packet = new StateUpdatePacket
                            {
                                PlayerId = currentPayload.PlayerId,
                                ActiveActivityId = currentPayload.ActiveActivityId,
                                CurrentProgressTicks = currentPayload.CurrentProgressTicks,
                                RequiredProgressTicks = currentPayload.RequiredProgressTicks,
                                InventorySpaceRemaining = currentPayload.InventorySpaceRemaining,
                                CurrentMonsterId = currentPayload.CurrentMonsterId,
                                CurrentMonsterHp = currentPayload.CurrentMonsterHp / 1000,
                                PlayerHp = currentPayload.PlayerHp / 1000,
                                Quarantine_Active = currentPayload.Quarantine_Active ? (byte)1 : (byte)0,
                                CurrentLevel = currentPayload.CurrentLevel,
                                CurrentXp = currentPayload.CurrentXp,
                                WoodcuttingMasteryXp = currentPayload.WoodcuttingMasteryXp,
                                WoodcuttingMasteryLevel = currentPayload.WoodcuttingMasteryLevel,
                                MiningMasteryXp = currentPayload.MiningMasteryXp,
                                MiningMasteryLevel = currentPayload.MiningMasteryLevel,
                                GatheringProgressTicks = currentPayload.GatheringProgressTicks,
                                CompletedAreaFlags = currentPayload.CompletedAreaFlags,
                                ClaimedAchievementFlags = currentPayload.ClaimedAchievementFlags,
                                HumanMasteryLevel = currentPayload.HumanMasteryLevel,
                                VilaMasteryLevel = currentPayload.VilaMasteryLevel,
                                DraugrMasteryLevel = currentPayload.DraugrMasteryLevel,
                                VillagePopulation = currentPayload.VillagePopulation,
                                AccumulatedTimeBankMs = currentPayload.AccumulatedTimeBankMs,
                                BankedChronoSeconds = currentPayload.BankedChronoSeconds,
                                LogicEpochCounter = (uint)(currentPayload.LogicEpochCounter & 0xFFFFFFFF),
                                PremiumCurrencyBalance = (uint)currentPayload.PremiumCurrency,
                                LegacyShardBalance = currentPayload.LegacyShardBalance,
                                IsChronoAccelerating = currentPayload.IsChronoAccelerating ? (byte)1 : (byte)0,
                                ActiveBankedChronoSeconds = (uint)Math.Max(0, Math.Min(uint.MaxValue, currentPayload.BankedChronoSeconds)),
                                CurrentSimulationSpeedMultiplier = (byte)Math.Clamp(currentPayload.SpeedMultiplier, 1, 4),
                                VisualBankedChronoSeconds = (uint)ChronoBufferEngine.ClampBankedSeconds(currentPayload.BankedChronoSeconds),
                                ActiveChronoEngineStatus = ResolveChronoEngineStatus(ref currentPayload),
                                ActiveChronoLockExpirationTicks = (ulong)Math.Max(0L, currentPayload.ActiveChronoLockExpirationTicks),
                                VisualActiveMatchMmr = (uint)Math.Max(0, currentPayload.ActiveMatchMmr),
                                GlobalNodeRemainingHp = currentPayload.GlobalNodeRemainingHp <= 0L
                                    ? 0U
                                    : (currentPayload.GlobalNodeRemainingHp > uint.MaxValue ? uint.MaxValue : (uint)currentPayload.GlobalNodeRemainingHp),
                                ActiveMatchId = currentPayload.ActiveCrossShardMatchId,
                                AutoEatThreshold = currentPayload.AutoEatThreshold,
                                STR = currentPayload.STR,
                                DEX = currentPayload.DEX,
                                CON = currentPayload.CON,
                                LCK = currentPayload.LCK,
                                EquippedWeaponId = currentPayload.EquippedWeaponId,
                                EquippedWeaponAffixLocked = currentPayload.EquippedWeaponAffixLocked ? (byte)1 : (byte)0,
                                EquippedArmorId = currentPayload.EquippedArmorId,
                                EquippedArmorAffixLocked = currentPayload.EquippedArmorAffixLocked ? (byte)1 : (byte)0,
                                CachedMiningMonolithLevel = currentPayload.CachedMiningMonolithLevel,
                                CachedWoodcuttingMonolithLevel = currentPayload.CachedWoodcuttingMonolithLevel,
                                ActiveOffensivePotionId = currentPayload.ActiveOffensivePotionId,
                                OffensivePotionDurationMs = currentPayload.OffensivePotionDurationMs,
                                ActiveDefensivePotionId = currentPayload.ActiveDefensivePotionId,
                                DefensivePotionDurationMs = currentPayload.DefensivePotionDurationMs,
                                WorldBossMaxHp = _worldBossEngine.BossMaxHp,
                                WorldBossCurrentHp = ClampWorldBossHpToUInt(_worldBossEngine.BossCurrentHp),
                                ActiveEventType = (byte)ActiveGlobalEventId,
                                IsFreshAccount = isFreshAccount,
                                PlayerAccuracyRating = broadcastCombatStats.AccuracyRating,
                                PlayerArmorRating = broadcastCombatStats.FlatPhysicalArmor,
                                PlayerBlockStrengthPct = broadcastCombatStats.BlockStrengthPct,
                                LastCommandResultCode = currentPayload.LastCommandResultCode,
                                LastCommandResultTick = currentPayload.LastCommandResultTick,
                                CitizenMultiSlotsUnlocked = currentPayload.CitizenMultiSlotsUnlocked,
                                GuildLogisticsCurrentStock = currentPayload.GuildLogisticsCurrentStock,
                                GuildLogisticsTargetRequirement = currentPayload.GuildLogisticsTargetRequirement,
                                CombatSimulationMatchId = currentPayload.CombatSimulationMatchId,
                                CombatSimulationTurnCounter = currentPayload.CombatSimulationTurnCounter,
                                CombatSimulationDamageDelta = currentPayload.CombatSimulationDamageDelta,
                                ActiveMentorPlayerId = currentPayload.ActiveMentorPlayerId,
                                MentorshipExpBonusMultiplier = currentPayload.MentorshipExpBonusMultiplier,
                                ForgeLevel = currentPayload.ForgeLevel,
                                InnLevel = currentPayload.InnLevel,
                                BreedingLevel = currentPayload.BreedingLevel,
                                AcademyLevel = currentPayload.AcademyLevel,
                                CurrentPopulationCount = currentPayload.CurrentPopulationCount,
                                ActiveStatusEffectModifierBitmask = statBitmask,
                                RemainingBuffDurationTicks = statDurTicks,
                                ActiveChroniclePassLevel = currentPayload.ActiveChroniclePassLevel,
                                AccumulatedSeasonalXp = currentPayload.AccumulatedSeasonalXp,
                                ActiveChallengeSeed = currentPayload.ActiveChallengeSeed,
                                IsQuarantineActive = (currentPayload.Quarantine_Active || currentPayload.IsQuarantined) ? (byte)1 : (byte)0,
                                NotificationQueueStateLength = (byte)Math.Clamp(GlobalEngineState.NotificationQueueStateLength, 0, 255),
                                ActiveLanguageState = currentPayload.ActiveLanguageState == 0 ? (byte)1 : currentPayload.ActiveLanguageState,
                                ActiveAudioTrackId = audioTrackId,
                                TotalAchievementsClaimedCount = currentPayload.TotalAchievementsClaimedCount,
                                ActiveMasteryBitmask = _liveSessionContexts.TryGetValue(currentPayload.PlayerId, out var mCtx) ? mCtx.ActiveMasteryBitmask : 0,
                                NetworkDiagnosticsToken = currentPayload.NetworkDiagnosticsToken,
                                VisualActiveConnectionThroughput = (uint)GlobalEngineState.ActiveConnectionThroughput,
                                CurrentNodeMemoryLoadMetrics = (uint)(GC.GetTotalMemory(false) / 1024),
                                Gold = currentPayload.CurrentGold,
                                WorldBossAttemptCount = currentPayload.WorldBossAttemptCount,
                                WorldBossEventState = _worldBossEngine.EventState,
                                WorldBossEventEndEpoch = _worldBossEngine.EventEndEpoch,
                                GuildLogisticsLevel = currentPayload.CachedGuildLogisticsLevel,
                                GuildRaidTier = currentPayload.CachedGuildRaidTier,
                                GuildRaidBossCurrentHp = currentPayload.CachedGuildRaidBossCurrentHp,
                                GuildRaidBossMaxHp = currentPayload.CachedGuildRaidBossMaxHp,
                                LumberjackLevel = currentPayload.LumberjackLevel,
                                QuarryLevel = currentPayload.QuarryLevel,
                                MineLevel = currentPayload.MineLevel,
                                WarehouseLevel = currentPayload.WarehouseLevel,
                                CachedWoodStock = currentPayload.CachedWoodStock,
                                CachedStoneStock = currentPayload.CachedStoneStock,
                                CachedIronOreStock = currentPayload.CachedIronOreStock,
                                PendingUpgradeBuildingId = currentPayload.PendingUpgradeBuildingId,
                                PendingUpgradeCompletesAtEpoch = currentPayload.PendingUpgradeCompletesAtEpoch,
                                UnlockedSkillsBitmask = currentPayload.UnlockedSkillsBitmask,
                                CurrentMana = currentPayload.CurrentMana,
                                MaxMana = ActiveSkillEngine.ComputeMaxMana(currentPayload.CurrentLevel),
                                AvailableSkillPoints = currentPayload.AvailableSkillPoints,
                                Skill1CooldownRemainingMs = ComputeSkillCooldownRemainingMs(in currentPayload, 1),
                                Skill2CooldownRemainingMs = ComputeSkillCooldownRemainingMs(in currentPayload, 2),
                                Skill3CooldownRemainingMs = ComputeSkillCooldownRemainingMs(in currentPayload, 3),
                                Skill4CooldownRemainingMs = ComputeSkillCooldownRemainingMs(in currentPayload, 4),
                                LastSkillCastId = currentPayload.LastSkillCastId,
                                LastSkillCastSuccess = currentPayload.LastSkillCastSuccess,
                                LastSkillCastResultTick = currentPayload.LastSkillCastResultTick,
                                OfflineElapsedSeconds = currentPayload.OfflineElapsedSeconds,
                                OfflineGoldEarned = currentPayload.OfflineGoldEarned,
                                OfflineXpEarned = currentPayload.OfflineXpEarned,
                                OfflineMaterialDropsGranted = currentPayload.OfflineMaterialDropsGranted,
                                OfflineSummaryTick = currentPayload.OfflineSummaryTick,
                                TicksSinceLastFlush = currentPayload.TicksSinceLastFlush
                            };
                            // Modul: this packet carries currentPayload's own
                            // private data (gold, stats, equipment, mana,
                            // skill cooldowns) - it must go to that player's
                            // own connection only. Broadcast(ref packet)
                            // sends to every connected socket regardless of
                            // whose data it is, which both leaked every
                            // player's private state to every other
                            // connected player and, for N active players,
                            // fired N times per active player per broadcast
                            // cycle (N-squared unawaited concurrent SendAsync
                            // calls against the same sockets) - discovered
                            // via the Chaos Tester load test, where 50 real
                            // connections produced zero successful chat
                            // round-trips despite 100 percent successful
                            // handshakes, because this flood of concurrent
                            // sends against the same WebSocket instances
                            // (which .NET does not allow) was corrupting
                            // socket send state well before chat's own,
                            // correctly-serialized broadcast ever got a
                            // chance to run cleanly.
                            _networkSystem.SendToPlayer(currentPayload.PlayerId, ref packet);
                            currentPayload.NetworkDiagnosticsToken = 0; // Clear it so it only echoes once

                            long packetSerializationElapsedMicroseconds = (Stopwatch.GetTimestamp() - packetSerializationStartTimestamp) * 1_000_000L / Stopwatch.Frequency;
                            FolkIdleEventSource.Log.PacketSerializationLatency(currentPayload.PlayerId, packetSerializationElapsedMicroseconds);
                        }
                    }

                    long broadcastSnapshotElapsedMicroseconds = (Stopwatch.GetTimestamp() - broadcastSnapshotStartTimestamp) * 1_000_000L / Stopwatch.Frequency;
                    FolkIdleEventSource.Log.BroadcastSnapshotEnd(broadcastSnapshotElapsedMicroseconds, _activePlayers.Count);
                }

                stopwatch.Stop();
                long tickEndTimestamp = Stopwatch.GetTimestamp();
                _metrics.TotalTicksProcessed++;
                long tickElapsedForMetricsMs = stopwatch.ElapsedMilliseconds;
                _metrics.LastExecutionTimeMs = tickElapsedForMetricsMs;
                _metrics.TickDurationSumMs += tickElapsedForMetricsMs;
                if (tickElapsedForMetricsMs <= 10) _metrics.TickDurationBucketCount10Ms++;
                if (tickElapsedForMetricsMs <= 25) _metrics.TickDurationBucketCount25Ms++;
                if (tickElapsedForMetricsMs <= 50) _metrics.TickDurationBucketCount50Ms++;
                if (tickElapsedForMetricsMs <= 100) _metrics.TickDurationBucketCount100Ms++;
                if (tickElapsedForMetricsMs <= 250) _metrics.TickDurationBucketCount250Ms++;
                _metrics.TickDurationBucketCountInf++;

                if (isBenchmarking)
                {
                    double tickElapsedMs = (tickEndTimestamp - tickStartTimestamp) * 1000.0 / Stopwatch.Frequency;
                    benchmarkTotalMs += tickElapsedMs;
                    if (tickElapsedMs > benchmarkPeakMs) benchmarkPeakMs = tickElapsedMs;
                    benchmarkTickCount++;

                    if (benchmarkTickCount == 100)
                    {
                        long endAllocated = GC.GetAllocatedBytesForCurrentThread();
                        long deltaAllocated = endAllocated - benchmarkStartAllocated;
                        double avgMs = benchmarkTotalMs / 100.0;
                        
                        Console.WriteLine($"[METRICS] Average Tick: {avgMs:F3} ms | Peak Tick: {benchmarkPeakMs:F3} ms | Thread Allocated: {deltaAllocated} bytes");

                        benchmarkStartAllocated = GC.GetAllocatedBytesForCurrentThread();
                        benchmarkTotalMs = 0;
                        benchmarkPeakMs = 0;
                        benchmarkTickCount = 0;
                    }
                }

                var elapsedMs = (int)stopwatch.ElapsedMilliseconds;
                var sleepTime = TickIntervalMs - elapsedMs;

                if (sleepTime > 0)
                {
                    Thread.Sleep(sleepTime);
                }
            }
        }

        private static void ActivateChronoAcceleration(ref TickStatePayload payload, int multiplier)
        {
            if (multiplier != 2 && multiplier != 4)
            {
                return;
            }

            int bankedSeconds = ChronoBufferEngine.ClampBankedSeconds(payload.BankedChronoSeconds);
            if (bankedSeconds <= 0)
            {
                payload.SpeedMultiplier = 1;
                payload.IsChronoAccelerating = false;
                payload.ActiveChronoSpeedMultiplier = 1.0;
                payload.ActiveChronoLockExpirationTicks = 0L;
                return;
            }

            payload.SpeedMultiplier = multiplier;
            payload.IsChronoAccelerating = true;
            payload.ActiveChronoSpeedMultiplier = multiplier;
            long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            payload.ActiveChronoLockExpirationTicks = now + (long)System.Math.Floor(bankedSeconds / (double)(multiplier - 1));
            payload.IsDirty = true;
        }

        private static void ExecuteInstantTimeWarp(ref TickStatePayload payload, uint requestedSeconds, uint targetSlot, uint remainingBuffTicks, int potencyModifierPct)
        {
            int bankedSeconds = ChronoBufferEngine.ClampBankedSeconds(payload.BankedChronoSeconds);
            int warpSeconds = (int)System.Math.Min(requestedSeconds, (uint)bankedSeconds);
            if (warpSeconds <= 0)
            {
                return;
            }

            payload.BankedChronoSeconds = System.Math.Max(0.0, payload.BankedChronoSeconds - warpSeconds);
            if (payload.BankedChronoSeconds <= 0.0)
            {
                payload.SpeedMultiplier = 1;
                payload.IsChronoAccelerating = false;
                payload.ActiveChronoSpeedMultiplier = 1.0;
                payload.ActiveChronoLockExpirationTicks = 0L;
            }

            long totalTicks = (long)warpSeconds * 10L;
            if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out var gatheringNode))
            {
                ApplyGatheringWarp(ref payload, gatheringNode, totalTicks, warpSeconds, remainingBuffTicks, potencyModifierPct);
            }
            else
            {
                ApplyCombatWarp(ref payload, totalTicks, targetSlot, warpSeconds, remainingBuffTicks, potencyModifierPct);
            }

            payload.IsDirty = true;
        }

        private static void ApplyGatheringWarp(ref TickStatePayload payload, GatheringNodeDefinition gatheringNode, long totalTicks, int warpSeconds, uint remainingBuffTicks, int potencyModifierPct)
        {
            int masteryLevel = gatheringNode.ProfessionType == 0 ? payload.WoodcuttingMasteryLevel : payload.MiningMasteryLevel;
            int requiredTicks = gatheringNode.BaseTickThreshold - (masteryLevel * 2) - payload.CachedCurrentToolTier;
            // Modul: Logistics achievement family's stackable claim reward
            // (Phase: Full-Stack Production Polish, Part 2.3) - a flat
            // percent reduction in the tick threshold, i.e. a gathering
            // speed boost. Applied multiplicatively after the additive
            // mastery/tool reductions above, matching how percentage
            // bonuses are layered everywhere else in this codebase (see
            // e.g. RaceMasteryResolver's own bonuses).
            if (payload.CachedLogisticsGatheringSpeedBonusPct > 0)
            {
                requiredTicks -= (requiredTicks * payload.CachedLogisticsGatheringSpeedBonusPct) / 100;
            }
            if (requiredTicks < 2) requiredTicks = 2;

            long progressedTicks = payload.GatheringProgressTicks + totalTicks;
            long completedCycles = progressedTicks / requiredTicks;
            payload.GatheringProgressTicks = (int)(progressedTicks % requiredTicks);
            payload.RequiredProgressTicks = requiredTicks;

            if (completedCycles <= 0)
            {
                return;
            }

            payload.HarvestLoopCount += completedCycles;

            double integratedBuffMultiplier = CalculateIntegratedBuffMultiplier(warpSeconds, remainingBuffTicks, potencyModifierPct);
            long masteryXp = (long)Math.Floor(completedCycles * gatheringNode.BaseMasteryXpReward * integratedBuffMultiplier);
            ApplyBulkMasteryXp(ref payload, gatheringNode.ProfessionType, masteryXp);
            AddSeasonalXp(ref payload, ClampLongToInt(masteryXp));

            long expectedDrops = CalculateExpectedWarpDrops(ref payload, completedCycles, gatheringNode.ProfessionType, integratedBuffMultiplier);
            ConsumeInventorySlots(ref payload, expectedDrops);
        }

        private static void ApplyCombatWarp(ref TickStatePayload payload, long totalTicks, uint targetSlot, int warpSeconds, uint remainingBuffTicks, int potencyModifierPct)
        {
            if (payload.ActiveActivityId <= 0 || ContentRegistry.Monsters.Length == 0)
            {
                return;
            }

            int monsterId = payload.CurrentMonsterId > 0 ? payload.CurrentMonsterId : (payload.ActiveActivityId > ContentRegistry.Monsters.Length ? 1 : (int)payload.ActiveActivityId);
            if (monsterId <= 0 || monsterId > ContentRegistry.Monsters.Length)
            {
                monsterId = 1;
            }

            var monster = ContentRegistry.Monsters[monsterId - 1];
            int attacksPerKill = EstimateAttacksPerKill(ref payload, monsterId);
            long ticksPerAttack = 15L;
            long ticksPerKill = System.Math.Max(1L, attacksPerKill * ticksPerAttack);
            long completedKills = totalTicks / ticksPerKill;
            payload.CombatTargetTickAccumulator = (int)(totalTicks % ticksPerKill);

            if (completedKills <= 0)
            {
                return;
            }

            int warpActiveRaceId = payload.Slot1_CharacterId != System.Guid.Empty ? (int)(payload.Slot1_GeneticVector & 0xFF) : 0;
            int warpActiveAgePhase = payload.Slot1_CharacterId != System.Guid.Empty ? payload.Slot1_AgePhase : 1;
            var warpCombatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, warpActiveAgePhase, payload.CompletedAreaFlags, warpActiveRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedEquippedFlatAttack, payload.CachedEquippedFlatDefense, payload.CachedEquippedCritBonus, payload.CachedEquippedLuckBonus, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit);

            // Modul: expected incoming damage over this warp period, mirroring
            // the live tick's monster crit formula (5% base + 0.5% per region
            // tier, 1.5x crit multiplier reduced by Vodnik's CritMitigationPct)
            // and the offline-projection's food-depletion model. If available
            // Food1-3 stock cannot sustain the full warp period, completedKills
            // is scaled down to whatever was actually survivable.
            if (warpSeconds > 0)
            {
                int warpMonsterRegionTier = ContentRegistry.GetMonsterRegionTier(monsterId);
                float warpMonsterCritChance = 0.05f + (warpMonsterRegionTier * 0.005f);
                float warpMitigatedCritMult = Math.Max(1.0f, 1.5f - (warpCombatStats.CritMitigationPct / 100f));
                float warpExpectedCritMultiplier = 1.0f + warpMonsterCritChance * (warpMitigatedCritMult - 1.0f);

                long warpRawIncomingMilliDamage = (long)(ContentRegistry.GetScaledMonsterAttackPower(monsterId) * 1000 * warpExpectedCritMultiplier);
                long warpNetIncomingMilliDamage = Math.Max(1000L, warpRawIncomingMilliDamage - (warpCombatStats.FlatPhysicalArmor * 1000L));

                double warpMonsterAttacksPerSecond = monster.AttackIntervalMs > 0 ? 1000.0 / monster.AttackIntervalMs : 0.0;
                double warpExpectedIncomingMilliDps = warpNetIncomingMilliDamage * warpMonsterAttacksPerSecond;

                if (warpExpectedIncomingMilliDps > 0.0)
                {
                    // Modul: the player's own max-HP pool is a "free" absorption
                    // buffer before any food is ever needed, mirroring the live
                    // tick's Auto-Eat threshold trigger - without this, a
                    // character with no food stocked would be treated as unable
                    // to survive any combat time at all.
                    int warpLineageId = payload.SelectedLineageId;
                    if (warpLineageId < 0 || warpLineageId >= ProgressionEngine.Lineages.Length) warpLineageId = 0;
                    var warpLineage = ProgressionEngine.Lineages[warpLineageId];
                    long warpBaseMilliHp = 100000L;
                    long warpEffectiveMilliHp = warpBaseMilliHp + (warpBaseMilliHp * warpLineage.HpScalePerLevelPct * payload.CurrentLevel / 100) + (warpCombatStats.MaxHp * 1000L);

                    double warpTotalIncomingMilliDamage = warpExpectedIncomingMilliDps * warpSeconds;
                    long warpTotalFoodUnits = payload.Food1_Count + payload.Food2_Count + payload.Food3_Count;
                    double warpTotalHealCapacityMilliHp = warpEffectiveMilliHp + ((double)warpTotalFoodUnits * 50000);

                    if (warpTotalIncomingMilliDamage > warpTotalHealCapacityMilliHp)
                    {
                        double survivableSeconds = warpTotalHealCapacityMilliHp / warpExpectedIncomingMilliDps;
                        if (survivableSeconds < 0.0) survivableSeconds = 0.0;

                        double survivableFraction = survivableSeconds / warpSeconds;
                        completedKills = (long)(completedKills * survivableFraction);

                        ConsumeFoodStock(ref payload, warpTotalFoodUnits);
                    }
                    else
                    {
                        long warpFoodUnitsConsumed = (long)Math.Ceiling(warpTotalIncomingMilliDamage / 50000.0);
                        ConsumeFoodStock(ref payload, warpFoodUnitsConsumed);
                    }
                }
            }

            if (completedKills <= 0)
            {
                payload.CurrentMonsterId = monsterId;
                payload.CurrentMonsterHp = ContentRegistry.GetScaledMonsterMaxHp(monsterId) * 1000;
                return;
            }

            int finalXpMultiplier = GlobalEngineState.GlobalXpMultiplier;
            if (payload.CurrentLevel < 50 && payload.CachedMentorCount > 0)
            {
                finalXpMultiplier += payload.CachedMentorCount * 5;
            }

            finalXpMultiplier += RaceMasteryResolver.GetHumanXpBonusPct(payload.HumanMasteryLevel);
            finalXpMultiplier += LegacyPerkResolver.GetXpBonusPct(payload.CachedLegacyPerks);

            if (payload.ActiveMentorPlayerId > 0 && payload.MentorshipExpBonusMultiplier > 1.0)
            {
                finalXpMultiplier = (int)(finalXpMultiplier * payload.MentorshipExpBonusMultiplier);
            }

            double integratedBuffMultiplier = CalculateIntegratedBuffMultiplier(warpSeconds, remainingBuffTicks, potencyModifierPct);
            long xpGain = (long)Math.Floor(completedKills * monster.BaseXpReward * finalXpMultiplier * integratedBuffMultiplier / 100.0);

            ApplyBulkExperience(ref payload, xpGain, warpActiveRaceId);
            AddSeasonalXp(ref payload, ClampLongToInt(xpGain));

            long goldReward = completedKills * monster.BaseGoldReward * GlobalEngineState.GlobalGoldDropMultiplier / 100L;

            // Modul 13.4.3: Human's innate +5% Gold acquisition passive, mirrored
            // for the offline warp path.
            goldReward = (long)(goldReward * (1.0f + warpCombatStats.GoldAcquisitionMultiplierPct / 100f));
            goldReward = (long)(goldReward * (1.0f + LegacyPerkResolver.GetGoldBonusPct(payload.CachedLegacyPerks) / 100f));

            if (goldReward > 0)
            {
                payload.AddGold(goldReward);
                payload.RedisPendingGoldDelta += goldReward;
                payload.RequiresRedisFlush = true;
            }

            long expectedDrops = CalculateExpectedCombatWarpDrops(ref payload, completedKills, integratedBuffMultiplier);
            ConsumeInventorySlots(ref payload, expectedDrops);

            // Modul: equipment drop requests, safely bounded by kill count and
            // available inventory space, mirroring OfflineSimulationEngine's
            // identical safeguard against flooding CombatLootEngine's queue.
            int warpEquipmentDropsToGrant = (int)Math.Min(completedKills, Math.Max(0, payload.InventorySpaceRemaining));
            for (int i = 0; i < warpEquipmentDropsToGrant; i++)
            {
                CombatLootEngine.DropRequestQueue.Enqueue(new CombatLootDropRequest
                {
                    PlayerId = payload.PlayerId,
                    MonsterId = monsterId,
                    LootLuckPct = warpCombatStats.LootLuckPct
                });
            }
            payload.InventorySpaceRemaining -= warpEquipmentDropsToGrant;

            payload.CurrentMonsterId = monsterId;
            payload.CurrentMonsterHp = ContentRegistry.GetScaledMonsterMaxHp(monsterId) * 1000;
        }

        // Modul: drains Food1-3 in a fixed order, mirroring
        // OfflineSimulationEngine.ConsumeFoodStock's identical logic for the
        // instant-warp path.
        private static void ConsumeFoodStock(ref TickStatePayload payload, long unitsToConsume)
        {
            if (unitsToConsume <= 0) return;

            long fromSlot1 = Math.Min(unitsToConsume, payload.Food1_Count);
            payload.Food1_Count -= (int)fromSlot1;
            unitsToConsume -= fromSlot1;
            if (unitsToConsume <= 0) return;

            long fromSlot2 = Math.Min(unitsToConsume, payload.Food2_Count);
            payload.Food2_Count -= (int)fromSlot2;
            unitsToConsume -= fromSlot2;
            if (unitsToConsume <= 0) return;

            long fromSlot3 = Math.Min(unitsToConsume, payload.Food3_Count);
            payload.Food3_Count -= (int)fromSlot3;
        }

        private static double CalculateIntegratedBuffMultiplier(int warpSeconds, uint remainingBuffTicks, int potencyModifierPct)
        {
            if (warpSeconds <= 0 || remainingBuffTicks == 0 || potencyModifierPct <= 0)
            {
                return 1.0;
            }

            double buffSeconds = Math.Min(warpSeconds, remainingBuffTicks / 10.0);
            double baseSeconds = Math.Max(0.0, warpSeconds - buffSeconds);
            double boostedMultiplier = 1.0 + Math.Min(500, potencyModifierPct) / 100.0;
            return ((buffSeconds * boostedMultiplier) + baseSeconds) / warpSeconds;
        }

        private static int EstimateAttacksPerKill(ref TickStatePayload payload, int monsterId)
        {
            int decayedStrength = payload.STR <= 0 ? 0 : (int)System.Math.Floor(System.Math.Log(payload.STR + 1.0) * 1000.0);
            long expectedDamage = 15000L + decayedStrength + (payload.CurrentLevel * 750L);
            if (expectedDamage < 1000L) expectedDamage = 1000L;
            long monsterHp = (long)ContentRegistry.GetScaledMonsterMaxHp(monsterId) * 1000L;
            long attacks = (monsterHp + expectedDamage - 1L) / expectedDamage;
            if (attacks <= 0L) return 1;
            if (attacks > int.MaxValue) return int.MaxValue;
            return (int)attacks;
        }

        private static long CalculateExpectedWarpDrops(ref TickStatePayload payload, long completedCycles, int professionType, double integratedBuffMultiplier)
        {
            int warpGatherActiveAgePhase = 1;
            int warpGatherActiveRaceId = 0;
            if (payload.Slot1_CharacterId != System.Guid.Empty)
            {
                warpGatherActiveAgePhase = payload.Slot1_AgePhase;
                warpGatherActiveRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);
            }
            var warpGatherCombatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, warpGatherActiveAgePhase, payload.CompletedAreaFlags, warpGatherActiveRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedEquippedFlatAttack, payload.CachedEquippedFlatDefense, payload.CachedEquippedCritBonus, payload.CachedEquippedLuckBonus, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit);

            int monolithLevel = professionType == 0 ? payload.CachedWoodcuttingMonolithLevel : payload.CachedMiningMonolithLevel;
            double yieldBonusPct = System.Math.Min(monolithLevel, 50);
            double decayedLuckPct = payload.LCK <= 0 ? 0.0 : System.Math.Log(payload.LCK + 1.0) * 2.5;
            double raceMasteryYieldBonusPct;
            if (professionType == 1)
            {
                // Modul 13.4.3: Kobold's innate baseline (not mastery-scaled) added
                // alongside the mastery-scaled bonus.
                raceMasteryYieldBonusPct = RaceMasteryResolver.GetKoboldOreDuplicationBonusPct(payload.KoboldMasteryLevel) + warpGatherCombatStats.MiningOreDuplicationBonusPct;
            }
            else
            {
                raceMasteryYieldBonusPct = RaceMasteryResolver.GetMoosleuteDoubleHarvestBonusPct(payload.MoosleuteMasteryLevel) + warpGatherCombatStats.WoodcuttingYieldBonusPct;
            }
            double multiplier = GlobalEngineState.GlobalDropMultiplier + yieldBonusPct + decayedLuckPct + raceMasteryYieldBonusPct;
            if (ActiveGlobalEventId == 1)
            {
                multiplier += 20.0;
            }

            // Modul 13.4.3: LocusYield mirrors the live-tick gathering block's
            // +4 percentage points per point bonus for the offline warp path.
            multiplier += payload.LocusYield * 4.0;

            // Modul 13.4.3: CombatStats.LootLuckPct multiplicatively scales the
            // whole warp yield multiplier, matching the live-tick gathering
            // block and FinalChance = BaseChance * (1 + LootLuckPct / 100.0).
            double warpLootLuckFactor = 1.0 + (warpGatherCombatStats.LootLuckPct / 100.0);

            return (long)System.Math.Floor(completedCycles * System.Math.Max(0.0, multiplier) * integratedBuffMultiplier * warpLootLuckFactor / 100.0);
        }

        private static long CalculateExpectedCombatWarpDrops(ref TickStatePayload payload, long completedKills, double integratedBuffMultiplier)
        {
            double decayedLuckPct = payload.LCK <= 0 ? 0.0 : System.Math.Log(payload.LCK + 1.0) * 2.5;
            double multiplier = GlobalEngineState.GlobalDropMultiplier + decayedLuckPct;
            return (long)System.Math.Floor(completedKills * System.Math.Max(0.0, multiplier) * integratedBuffMultiplier / 100.0);
        }

        private static void ConsumeInventorySlots(ref TickStatePayload payload, long expectedDrops)
        {
            if (expectedDrops <= 0 || payload.InventorySpaceRemaining <= 0)
            {
                return;
            }

            int consumed = expectedDrops >= payload.InventorySpaceRemaining ? payload.InventorySpaceRemaining : (int)expectedDrops;
            payload.InventorySpaceRemaining -= consumed;
        }

        private static void ApplyBulkMasteryXp(ref TickStatePayload payload, int professionType, long masteryXp)
        {
            if (masteryXp <= 0)
            {
                return;
            }

            if (professionType == 0)
            {
                payload.WoodcuttingMasteryXp = ClampLongToInt((long)payload.WoodcuttingMasteryXp + masteryXp);
                int requiredMasteryXp = 50 * (payload.WoodcuttingMasteryLevel + 1) * (payload.WoodcuttingMasteryLevel + 1);
                while (payload.WoodcuttingMasteryXp >= requiredMasteryXp)
                {
                    payload.WoodcuttingMasteryXp -= requiredMasteryXp;
                    payload.WoodcuttingMasteryLevel++;
                    requiredMasteryXp = 50 * (payload.WoodcuttingMasteryLevel + 1) * (payload.WoodcuttingMasteryLevel + 1);
                }
                return;
            }

            payload.MiningMasteryXp = ClampLongToInt((long)payload.MiningMasteryXp + masteryXp);
            int requiredMiningXp = 50 * (payload.MiningMasteryLevel + 1) * (payload.MiningMasteryLevel + 1);
            while (payload.MiningMasteryXp >= requiredMiningXp)
            {
                payload.MiningMasteryXp -= requiredMiningXp;
                payload.MiningMasteryLevel++;
                requiredMiningXp = 50 * (payload.MiningMasteryLevel + 1) * (payload.MiningMasteryLevel + 1);
            }
        }

        private static void ApplyBulkExperience(ref TickStatePayload payload, long xpGain, int activeRaceId = 0)
        {
            if (xpGain <= 0)
            {
                return;
            }

            // Modul 13.4.3: -20% character XP generation while an early
            // mentorship termination penalty is active (see MentorshipEngine).
            if (payload.XpPenaltyExpiresEpoch > System.DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                xpGain = (long)(xpGain * 0.8);
            }

            payload.CurrentXp = System.Math.Max(0L, payload.CurrentXp + xpGain);
            int levelsGained = 0;
            while (payload.CurrentLevel > 0)
            {
                long requiredXp = 100L * payload.CurrentLevel * payload.CurrentLevel;
                if (payload.CurrentXp < requiredXp)
                {
                    break;
                }

                payload.CurrentXp -= requiredXp;
                payload.CurrentLevel++;
                levelsGained++;
            }

            RaceAttributeGrowth.ApplyLevelUpGrowth(ref payload, activeRaceId, levelsGained);

            // Active Skill Tree: one skill point per level gained, spent via
            // RequestUnlockSkill (see ActiveSkillEngine).
            if (levelsGained > 0)
            {
                payload.AvailableSkillPoints += levelsGained;
            }
        }

        private static int ClampLongToInt(long value)
        {
            if (value <= 0L) return 0;
            if (value >= int.MaxValue) return int.MaxValue;
            return (int)value;
        }

        private async Task RegisterGuildDefenseAsync(long guildId)
        {
            if (guildId <= 0)
            {
                return;
            }

            await using var context = await _contextFactory.CreateDbContextAsync();
            await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                var guild = await context.GuildRecords
                    .FromSqlRaw("SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE", guildId)
                    .FirstOrDefaultAsync();
                if (guild == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                var roster = await context.GuildDefenseRosters
                    .FromSqlRaw("SELECT * FROM \"GuildDefenseRosters\" WHERE \"GuildId\" = {0} FOR UPDATE", guildId)
                    .FirstOrDefaultAsync();
                string payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    guild.GuildMMR,
                    guild.ActiveMembers,
                    guild.CurrentTier,
                    guild.MiningMonolithLevel,
                    guild.WoodcuttingMonolithLevel
                });

                if (roster == null)
                {
                    context.GuildDefenseRosters.Add(new GuildDefenseRoster
                    {
                        GuildId = guildId,
                        RegionShardId = (int)Math.Abs(guildId % 1024L),
                        DefensiveStatsJson = payload
                    });
                }
                else
                {
                    roster.RegionShardId = (int)Math.Abs(guildId % 1024L);
                    roster.DefensiveStatsJson = payload;
                }

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private async Task<(SyncMatchStateResponseBuffer Response, int ActiveMatchMmr)> SubmitShardAttackAsync(long guildId, long currentRemainingHp, Guid matchUuid, uint damage, bool isFinalBlow)
        {
            if (_tournamentMeshService == null)
            {
                return (new SyncMatchStateResponseBuffer(3U, currentRemainingHp), 0);
            }

            var request = new SyncMatchStateRequestBuffer(matchUuid, guildId, damage, isFinalBlow);
            var response = await _tournamentMeshService.SyncMatchStateAsync(request);
            int activeMatchMmr = 0;
            if (response.ProcessingStatus == 0U)
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                var snapshot = await context.GuildMatchmakingSnapshots
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.MatchUuid == matchUuid);
                if (snapshot != null)
                {
                    activeMatchMmr = snapshot.ActiveMatchMmr;
                }
            }

            return (response, activeMatchMmr);
        }

        private async Task<bool> ExecuteBattlePassClaimAsync(long playerId, uint milestoneIndex, uint seasonalXp, uint passLevel)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            try
            {
                var player = await context.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();
                if (player == null || milestoneIndex >= 50)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                var pass = await context.PlayerChroniclePasses
                    .FromSqlRaw("SELECT * FROM \"PlayerChroniclePasses\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();

                if (pass == null)
                {
                    pass = new PlayerChroniclePass
                    {
                        PlayerId = playerId,
                        PassLevel = 0,
                        AccumulatedXp = 0,
                        ClaimedMilestonesBitmask = 0UL
                    };
                    context.PlayerChroniclePasses.Add(pass);
                }

                if (pass.AccumulatedXp < seasonalXp)
                {
                    pass.AccumulatedXp = (int)Math.Min(int.MaxValue, seasonalXp);
                }

                if (pass.PassLevel < passLevel)
                {
                    pass.PassLevel = (int)Math.Min(50U, passLevel);
                }

                int requiredXp = checked((int)((milestoneIndex + 1U) * 1000U));
                if (pass.AccumulatedXp < requiredXp)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                ulong milestoneBit = 1UL << (int)milestoneIndex;
                if ((pass.ClaimedMilestonesBitmask & milestoneBit) != 0UL)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                pass.ClaimedMilestonesBitmask |= milestoneBit;
                int resolvedLevel = (int)Math.Min(50U, Math.Max(milestoneIndex + 1U, (uint)(pass.AccumulatedXp / 1000)));
                if (pass.PassLevel < resolvedLevel)
                {
                    pass.PassLevel = resolvedLevel;
                }

                int qualityTier = 1 + (int)(milestoneIndex / 10U);
                context.EquipmentInstances.Add(new EquipmentInstance
                {
                    PlayerId = playerId,
                    BaseItemId = $"chronicle_free_{milestoneIndex + 1U}",
                    QualityTier = qualityTier,
                    AffixPayload = "{}",
                    IsAffixLocked = false
                });

                if (player.PremiumDiamonds > 0)
                {
                    context.EquipmentInstances.Add(new EquipmentInstance
                    {
                        PlayerId = playerId,
                        BaseItemId = $"chronicle_premium_{milestoneIndex + 1U}",
                        QualityTier = qualityTier,
                        AffixPayload = "{}",
                        IsAffixLocked = false
                    });
                }

                context.EventHorizonPremiumLedgers.Add(new EventHorizonPremiumLedger
                {
                    TransactionId = $"chronicle_{playerId}_{milestoneIndex + 1U}",
                    PlayerId = playerId,
                    PreviousBalance = player.PremiumDiamonds,
                    NewBalance = player.PremiumDiamonds,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Battle pass claim failed for player {playerId}: {ex.Message}");
                return false;
            }
        }

        private static void AddSeasonalXp(ref TickStatePayload payload, int xp)
        {
            if (xp <= 0)
            {
                return;
            }

            ulong nextXp = payload.AccumulatedSeasonalXp + (ulong)(uint)xp;
            if (nextXp > int.MaxValue)
            {
                nextXp = int.MaxValue;
            }

            payload.AccumulatedSeasonalXp = (uint)nextXp;
            uint level = payload.AccumulatedSeasonalXp / 1000U;
            if (level > 50U)
            {
                level = 50U;
            }

            if (payload.ActiveChroniclePassLevel < level)
            {
                payload.ActiveChroniclePassLevel = level;
            }

            payload.IsDirty = true;
        }

        public void ProcessTick(ref TickStatePayload payload)
        {
            int localXpMultiplier = GlobalEngineState.GlobalXpMultiplier;
            int localDropMultiplier = GlobalEngineState.GlobalDropMultiplier;

            if (payload.SpeedMultiplier <= 0) payload.SpeedMultiplier = 1;

            bool validChronoSpeed = payload.SpeedMultiplier == 2 || payload.SpeedMultiplier == 4;
            long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool chronoAccelerating = payload.IsChronoAccelerating &&
                validChronoSpeed &&
                payload.BankedChronoSeconds > 0.0 &&
                (payload.ActiveChronoLockExpirationTicks == 0L || payload.ActiveChronoLockExpirationTicks > now);

            if (payload.IsChronoAccelerating && !chronoAccelerating)
            {
                payload.SpeedMultiplier = 1;
                payload.IsChronoAccelerating = false;
                payload.ActiveChronoSpeedMultiplier = 1.0;
                payload.ActiveChronoLockExpirationTicks = 0L;
            }

            // All register operations are on unmanaged value-type fields — 0 allocations.
            int extraIterations = payload.SpeedMultiplier > 4 ? 3 : payload.SpeedMultiplier - 1;
            if (extraIterations < 0) extraIterations = 0;

            // Normal tick (i = 0)
            if (payload.ActiveActivityId > 0 && payload.InventorySpaceRemaining <= 0)
            {
                payload.SpeedMultiplier = 1;
                extraIterations = 0;
            }

            if (payload.Quarantine_Active) return;

            ProcessPassiveVillageTick(ref payload, TickIntervalSeconds, now);
            ProcessSubTick(ref payload, localXpMultiplier, localDropMultiplier, _guildWarEngine.GuildWarPointQueue, _liveSessionContexts);

            if (chronoAccelerating)
            {
                for (int i = 0; i < extraIterations; i++)
                {
                    if (payload.ActiveActivityId > 0 && payload.InventorySpaceRemaining <= 0)
                    {
                        payload.SpeedMultiplier = 1;
                        break;
                    }

                    ProcessPassiveVillageTick(ref payload, TickIntervalSeconds, now);
                    ProcessSubTick(ref payload, localXpMultiplier, localDropMultiplier, _guildWarEngine.GuildWarPointQueue, _liveSessionContexts);
                }

                payload.BankedChronoSeconds -= (payload.SpeedMultiplier - 1) * TickIntervalSeconds;
                if (payload.BankedChronoSeconds <= 0.0)
                {
                    payload.BankedChronoSeconds = 0.0;
                    payload.SpeedMultiplier = 1;
                    payload.IsChronoAccelerating = false;
                    payload.ActiveChronoSpeedMultiplier = 1.0;
                    payload.ActiveChronoLockExpirationTicks = 0L;
                }

                payload.IsDirty = true;
                return;
            }

            // Extra iterations (i > 0)
            for (int i = 0; i < extraIterations; i++)
            {
                if (payload.ActiveActivityId > 0 && payload.InventorySpaceRemaining <= 0)
                {
                    payload.SpeedMultiplier = 1;
                    break;
                }

                if (payload.AccumulatedTimeBankMs >= 100)
                {
                    payload.AccumulatedTimeBankMs -= 100;
                    ProcessPassiveVillageTick(ref payload, TickIntervalSeconds, now);
                    ProcessSubTick(ref payload, localXpMultiplier, localDropMultiplier, _guildWarEngine.GuildWarPointQueue, _liveSessionContexts);
                }
                else
                {
                    payload.SpeedMultiplier = 1;
                    break;
                }
            }
        }

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        // Zero-allocation: pure struct field arithmetic, no LINQ, no DB access.
        // Independent of ActiveActivityId - runs every call regardless of what
        // the player is currently doing, unlike ProcessSubTick.
        // Tolerance absorbing float32 summation drift (e.g. repeatedly adding
        // 0.01f can land just under a whole-unit threshold after thousands of
        // ticks) without being large enough to trigger a spurious extra unit.
        private const float ProductionAccumulatorEpsilon = 1e-4f;

        internal static void ProcessPassiveVillageTick(ref TickStatePayload payload, double deltaTimeSeconds, long nowEpoch)
        {
            // Modul 16: live completion of a matured upgrade for players who
            // are already online, so the progress bar/Upgrade button react at
            // the exact moment the timer elapses instead of only refreshing
            // on the player's next explicit action - VillageManagementEngine.
            // ResolveMaturedUpgradesAsync is still the DB-level source of
            // truth (reconciled before any new upgrade is granted), this is
            // purely a same-tick in-memory mirror of that same completion.
            if (payload.PendingUpgradeBuildingId != 0 && nowEpoch >= payload.PendingUpgradeCompletesAtEpoch)
            {
                ApplyMaturedUpgradeInMemory(ref payload);
            }

            long maxStorage = VillageManagementEngine.CalculateWarehouseMaxStorage(payload.WarehouseLevel);

            float woodRate = payload.LumberjackLevel * VillageManagementEngine.LumberjackWoodRatePerLevel;
            if (woodRate > 0f && payload.CachedWoodStock < maxStorage)
            {
                payload.AccumulatedWood += (float)(woodRate * deltaTimeSeconds);
            }

            float stoneRate = payload.QuarryLevel * VillageManagementEngine.QuarryStoneRatePerLevel;
            if (stoneRate > 0f && payload.CachedStoneStock < maxStorage)
            {
                payload.AccumulatedStone += (float)(stoneRate * deltaTimeSeconds);
            }

            float ironRate = payload.MineLevel * VillageManagementEngine.MineIronRatePerLevel;
            if (ironRate > 0f && payload.CachedIronOreStock < maxStorage)
            {
                payload.AccumulatedIron += (float)(ironRate * deltaTimeSeconds);
            }

            while (payload.AccumulatedWood >= 1.0f - ProductionAccumulatorEpsilon)
            {
                payload.AccumulatedWood -= 1.0f;
                payload.CachedWoodStock++;
                payload.PendingWoodDelta++;
                payload.IsDirty = true;
            }

            while (payload.AccumulatedStone >= 1.0f - ProductionAccumulatorEpsilon)
            {
                payload.AccumulatedStone -= 1.0f;
                payload.CachedStoneStock++;
                payload.PendingStoneDelta++;
                payload.IsDirty = true;
            }

            while (payload.AccumulatedIron >= 1.0f - ProductionAccumulatorEpsilon)
            {
                payload.AccumulatedIron -= 1.0f;
                payload.CachedIronOreStock++;
                payload.PendingIronDelta++;
                payload.IsDirty = true;
            }
        }

        // Modul 16: mirrors VillageManagementEngine's BuildingId -> cached
        // level field mapping (BuildInfrastructureNotificationAsync). Pure
        // struct field arithmetic, no allocations.
        private static void ApplyMaturedUpgradeInMemory(ref TickStatePayload payload)
        {
            switch (payload.PendingUpgradeBuildingId)
            {
                case VillageManagementEngine.ForgeBuildingId:
                    payload.ForgeLevel++;
                    payload.CachedCurrentToolTier = payload.ForgeLevel;
                    break;
                case VillageManagementEngine.InnBuildingId:
                    payload.InnLevel++;
                    payload.CachedMaxPopulationCapacity = VillageManagementEngine.CalculatePopulationCapacity(payload.InnLevel);
                    payload.CachedInnMaturationBonus = payload.InnLevel;
                    break;
                case VillageManagementEngine.BreedingGroundsBuildingId:
                    payload.BreedingLevel++;
                    break;
                case VillageManagementEngine.MentorshipAcademyBuildingId:
                    payload.AcademyLevel++;
                    break;
                case VillageManagementEngine.LumberjackBuildingId:
                    payload.LumberjackLevel++;
                    break;
                case VillageManagementEngine.QuarryBuildingId:
                    payload.QuarryLevel++;
                    break;
                case VillageManagementEngine.MineBuildingId:
                    payload.MineLevel++;
                    break;
                case VillageManagementEngine.WarehouseBuildingId:
                    payload.WarehouseLevel++;
                    break;
            }

            payload.PendingUpgradeBuildingId = 0;
            payload.PendingUpgradeCompletesAtEpoch = 0;
            payload.IsDirty = true;
        }

        private static bool ProcessAgeSlot(ref System.Guid characterId, ref long ageTicks, ref int agePhase)
        {
            if (characterId == System.Guid.Empty) return false;
            
            ageTicks++;
            int newPhase = agePhase;
            // E.g., 36000 ticks = 1 hour real-time at 10Hz
            if (ageTicks >= 108000) newPhase = 3;
            else if (ageTicks >= 72000) newPhase = 2;
            else if (ageTicks >= 36000) newPhase = 1;
            else newPhase = 0;

            if (newPhase != agePhase)
            {
                agePhase = newPhase;
                return true;
            }
            return false;
        }

        private static void ProcessSubTick(ref TickStatePayload payload, int localXpMultiplier, int localDropMultiplier, System.Collections.Concurrent.ConcurrentQueue<GuildWarPointEvent> guildWarPointQueue, System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext> liveSessionContexts)
        {
            if (payload.ActiveActivityId <= 0 || payload.InventorySpaceRemaining <= 0)
            {
                return;
            }

            payload.TicksSinceLastFlush++;
            payload.IsDirty = true;

            bool stateFlashed = false;
            stateFlashed |= ProcessAgeSlot(ref payload.Slot1_CharacterId, ref payload.Slot1_AgeTicks, ref payload.Slot1_AgePhase);
            stateFlashed |= ProcessAgeSlot(ref payload.Slot2_CharacterId, ref payload.Slot2_AgeTicks, ref payload.Slot2_AgePhase);
            stateFlashed |= ProcessAgeSlot(ref payload.Slot3_CharacterId, ref payload.Slot3_AgeTicks, ref payload.Slot3_AgePhase);
            if (stateFlashed)
            {
                payload.IsDirty = true; // Flashes state to client implicitly via network loop
            }

            // Active Skill Tree: passive mana regen, unconditional like potion
            // duration below - runs regardless of gathering/combat activity
            // type so mana is topped up between casts.
            int maxMana = ActiveSkillEngine.ComputeMaxMana(payload.CurrentLevel);
            if (payload.CurrentMana < maxMana)
            {
                payload.CurrentMana += ActiveSkillEngine.ManaRegenPerTick;
                if (payload.CurrentMana > maxMana) payload.CurrentMana = maxMana;
            }

            if (payload.OffensivePotionDurationMs > 0)
            {
                payload.OffensivePotionDurationMs -= 100;
                if (payload.OffensivePotionDurationMs <= 0)
                {
                    payload.OffensivePotionDurationMs = 0;
                    payload.ActiveOffensivePotionId = 0;
                }
            }

            if (payload.DefensivePotionDurationMs > 0)
            {
                payload.DefensivePotionDurationMs -= 100;
                if (payload.DefensivePotionDurationMs <= 0)
                {
                    payload.DefensivePotionDurationMs = 0;
                    payload.ActiveDefensivePotionId = 0;
                }
            }

            // Child Maturation Sub-tick (Breeding Loop)
            if (payload.ActiveChildMaturationMs > 0)
            {
                int decrementValue = (int)Math.Floor(100 * (1 + payload.CachedInnMaturationBonus * 0.20f));
                payload.ActiveChildMaturationMs -= decrementValue;
                if (payload.ActiveChildMaturationMs <= 0)
                {
                    payload.ActiveChildMaturationMs = 0;
                }
            }

            if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out var gatheringNode))
            {
                int masteryLevel = gatheringNode.ProfessionType == 0 ? payload.WoodcuttingMasteryLevel : payload.MiningMasteryLevel;
                int requiredTicks = gatheringNode.BaseTickThreshold - (masteryLevel * 2) - payload.CachedCurrentToolTier;
                if (requiredTicks < 2) requiredTicks = 2;
                payload.RequiredProgressTicks = requiredTicks;
                payload.GatheringProgressTicks++;

                if (payload.GatheringProgressTicks >= requiredTicks)
                {
                    payload.GatheringProgressTicks = 0;
                    payload.HarvestLoopCount++;

                    int masteryXpGain = gatheringNode.BaseMasteryXpReward;
                    if (gatheringNode.ProfessionType == 0)
                    {
                        payload.WoodcuttingMasteryXp += masteryXpGain;
                        AddSeasonalXp(ref payload, masteryXpGain);
                        int requiredMasteryXp = 50 * (payload.WoodcuttingMasteryLevel + 1) * (payload.WoodcuttingMasteryLevel + 1);
                        while (payload.WoodcuttingMasteryXp >= requiredMasteryXp)
                        {
                            payload.WoodcuttingMasteryXp -= requiredMasteryXp;
                            payload.WoodcuttingMasteryLevel++;
                            requiredMasteryXp = 50 * (payload.WoodcuttingMasteryLevel + 1) * (payload.WoodcuttingMasteryLevel + 1);
                        }
                    }
                    else
                    {
                        payload.MiningMasteryXp += masteryXpGain;
                        AddSeasonalXp(ref payload, masteryXpGain);
                        int requiredMasteryXp = 50 * (payload.MiningMasteryLevel + 1) * (payload.MiningMasteryLevel + 1);
                        while (payload.MiningMasteryXp >= requiredMasteryXp)
                        {
                            payload.MiningMasteryXp -= requiredMasteryXp;
                            payload.MiningMasteryLevel++;
                            requiredMasteryXp = 50 * (payload.MiningMasteryLevel + 1) * (payload.MiningMasteryLevel + 1);
                        }
                    }

                    // Loot roll
                    var lootTable = ContentRegistry.GetLootTable(gatheringNode.ActivityId);
                    if (lootTable.Length > 0 && payload.InventorySpaceRemaining > 0)
                    {
                        int gatherActiveAgePhase = 1;
                        int gatherActiveRaceId = 0;
                        if (payload.Slot1_CharacterId != System.Guid.Empty)
                        {
                            gatherActiveAgePhase = payload.Slot1_AgePhase;
                            gatherActiveRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);
                        }
                        var gatherCombatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, gatherActiveAgePhase, payload.CompletedAreaFlags, gatherActiveRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedEquippedFlatAttack, payload.CachedEquippedFlatDefense, payload.CachedEquippedCritBonus, payload.CachedEquippedLuckBonus, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit);

                        int monolithLevel = gatheringNode.ProfessionType == 0 ? payload.CachedWoodcuttingMonolithLevel : payload.CachedMiningMonolithLevel;
                        float yieldBonusPct = Math.Min(monolithLevel * 1.0f, 50.0f);
                        int additionalYieldBonus = (int)(100f * (yieldBonusPct / 100f)); // Add to multiplier

                        // Modul 13: Kobold ore duplication (Mining) / Moosleute yield
                        // bonus. Fishing (ProfessionType 2) and Herbalism
                        // (ProfessionType 3) fall through to the Moosleute
                        // branch below along with Woodcutting - Kobold's ore
                        // duplication is intentionally Mining-specific, and
                        // no dedicated racial bonus exists yet for Fishing/
                        // Herbalism, so Moosleute's "double harvest" is
                        // applied to them as the closest available bonus
                        // rather than granting neither profession any
                        // racial yield bonus at all.
                        if (gatheringNode.ProfessionType == 1)
                        {
                            additionalYieldBonus += (int)RaceMasteryResolver.GetKoboldOreDuplicationBonusPct(payload.KoboldMasteryLevel);
                            // Modul 13.4.3: Kobold's innate baseline (not mastery-scaled).
                            additionalYieldBonus += (int)gatherCombatStats.MiningOreDuplicationBonusPct;
                        }
                        else
                        {
                            additionalYieldBonus += (int)RaceMasteryResolver.GetMoosleuteDoubleHarvestBonusPct(payload.MoosleuteMasteryLevel);
                            // Modul 13.4.3: Moosleute's innate baseline (not mastery-scaled).
                            additionalYieldBonus += (int)gatherCombatStats.WoodcuttingYieldBonusPct;
                        }

                        if (ActiveGlobalEventId == 1) // GoldenHarvest
                        {
                            additionalYieldBonus += 20;
                        }

                        // Modul 13.4.3: LocusYield (bred genetic trait, see
                        // GeneticSplicingEngine/BreedingEngine) adds +4 percentage
                        // points of extra harvest roll count per point, same units
                        // as the race-mastery bonuses above.
                        additionalYieldBonus += payload.LocusYield * 4;

                        // Modul: LootLuckPct no longer multiplies the roll COUNT
                        // (which previously inflated absolute yield of every
                        // table entry, common trash and rare drops alike, in
                        // fixed proportion - a placebo that never actually
                        // shifted rarity odds). Roll count now stays driven only
                        // by monolith/race/event/LocusYield bonuses; luck
                        // instead adds a flat weight bonus to every entry below,
                        // which mathematically favors low-weight (rare) entries
                        // far more than high-weight (common/trash) ones, since a
                        // fixed addition is a much larger relative increase for
                        // a small base weight than a large one.
                        int luckWeightBonus = (int)(gatherCombatStats.LootLuckPct * 0.1f);
                        if (luckWeightBonus < 0) luckWeightBonus = 0;

                        int totalWeight = 0;
                        for (int i = 0; i < lootTable.Length; i++) totalWeight += lootTable[i].Weight + luckWeightBonus;
                        if (totalWeight > 0)
                        {
                            int multiplier = (int)((localDropMultiplier + additionalYieldBonus) * payload.CachedCodexYieldMultiplier);
                            int guaranteedRolls = multiplier / 100;
                            int fractionalBonus = multiplier % 100;
                            int rollsToExecute = guaranteedRolls;
                            if (fractionalBonus > 0 && Random.Shared.Next(100) < fractionalBonus)
                            {
                                rollsToExecute++;
                            }
                            for (int r = 0; r < rollsToExecute; r++)
                            {
                                if (payload.InventorySpaceRemaining <= 0) break;
                                int roll = Random.Shared.Next(totalWeight);
                                int currentWeight = 0;
                                for (int i = 0; i < lootTable.Length; i++)
                                {
                                    currentWeight += lootTable[i].Weight + luckWeightBonus;
                                    if (roll < currentWeight)
                                    {
                                        // Modul 04: Kobold's packed-weight penalty -
                                        // anything other than raw ores/refined bars
                                        // consumes 2 virtual capacity slots instead
                                        // of 1. Breaching the cap drops this item
                                        // (and stops this cycle's remaining rolls
                                        // entirely, matching "0% efficiency" on
                                        // overflow) while gold/XP already granted
                                        // above are preserved.
                                        int itemWeight = 1;
                                        if (gatherActiveRaceId == RaceIds.Kobold)
                                        {
                                            string droppedBaseId = ContentRegistry.GetMaterialString(lootTable[i].ItemId);
                                            bool isOreOrBar = droppedBaseId.Contains("_ore_") || droppedBaseId.Contains("_bar_");
                                            if (!isOreOrBar) itemWeight = 2;
                                        }

                                        if (itemWeight > payload.InventorySpaceRemaining)
                                        {
                                            r = rollsToExecute;
                                            break;
                                        }

                                        payload.InventorySpaceRemaining -= itemWeight;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                return;
            }

            int fallbackId = payload.ActiveActivityId > ContentRegistry.Monsters.Length ? 1 : (int)payload.ActiveActivityId;

            int lineageId = payload.SelectedLineageId;
            if (lineageId < 0 || lineageId >= ProgressionEngine.Lineages.Length) lineageId = 0;
            var lineage = ProgressionEngine.Lineages[lineageId];

            int activeAgePhase = 1;
            int activeRaceId = 0;
            if (payload.Slot1_CharacterId != System.Guid.Empty)
            {
                activeAgePhase = payload.Slot1_AgePhase;
                activeRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);
            }

            var combatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, activeAgePhase, payload.CompletedAreaFlags, activeRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedEquippedFlatAttack, payload.CachedEquippedFlatDefense, payload.CachedEquippedCritBonus, payload.CachedEquippedLuckBonus, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit);
            
            long baseMilliHp = 100000L;
            long effectiveMilliHp = baseMilliHp + (baseMilliHp * lineage.HpScalePerLevelPct * payload.CurrentLevel / 100) + (combatStats.MaxHp * 1000L);
            int effectiveMaxHp = (int)effectiveMilliHp;

            if (payload.PlayerHp <= 0)
            {
                payload.PlayerHp = effectiveMaxHp;
            }

            if (payload.CurrentMonsterId <= 0)
            {
                payload.CurrentMonsterId = fallbackId;
                payload.CurrentMonsterHp = ContentRegistry.GetScaledMonsterMaxHp(payload.CurrentMonsterId) * 1000;
                payload.CombatTargetTickAccumulator = 0;
            }

            payload.CombatTargetTickAccumulator++;

            var activeMonster = ContentRegistry.Monsters[payload.CurrentMonsterId - 1];

            // Player attacks monster
            int playerAttackSpeedMs = (int)(1500 * (1.0f - combatStats.AttackSpeedPct));
            if (playerAttackSpeedMs < 200) playerAttackSpeedMs = 200; // Hard cap attack speed

            if ((payload.CombatTargetTickAccumulator * 100) % playerAttackSpeedMs == 0)
            {
                // Step 1 (Hit Determination). AccuracyRating (DEX-derived,
                // see StatsCalculator) and the monster's content-authored
                // DodgeRating replace the previous fixed 100/100 placeholder
                // pair - a 0-DEX/0-DodgeRating pairing reproduces the exact
                // old fixed-midpoint hit chance, so this is a pure extension,
                // not a rebalance of existing content.
                float attackerAccuracy = 100f + combatStats.AccuracyRating;
                float defenderDodge = 100f + activeMonster.DodgeRating;
                float hitChance = Math.Clamp(attackerAccuracy / defenderDodge, 0.05f, 0.95f);

                if (Random.Shared.NextDouble() <= hitChance)
                {
                    // Step 2 (Crit Check)
                    float critMult = 1.0f;
                    if (Random.Shared.NextDouble() <= (combatStats.CritChancePct / 100.0f))
                    {
                        critMult = 1.5f;
                    }

                    long effectiveMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(in combatStats, lineage.DamageScalePerLevelPct, payload.CurrentLevel);

                    // Modul: Prestige "combat speed" perk (LegacyPerkResolver) -
                    // applied as a flat percent boost to effective damage
                    // output per attack rather than to the attack-interval
                    // tick cadence itself (AttackIntervalMs governs the
                    // shared per-monster pacing loop below and is not a
                    // per-player value), which is a materially equivalent
                    // DPS increase without touching that shared cadence math.
                    int legacyCombatSpeedBonusPct = LegacyPerkResolver.GetCombatSpeedBonusPct(payload.CachedLegacyPerks);
                    if (legacyCombatSpeedBonusPct > 0)
                    {
                        effectiveMilliAttack += (effectiveMilliAttack * legacyCombatSpeedBonusPct) / 100;
                    }
                    int rawDamage = (int)(effectiveMilliAttack * critMult);

                    // Active Skill Tree: a successful RequestCastSkill sets this
                    // for exactly one attack resolution, then it is consumed
                    // (reset to 0) here - "injected into the next tick's
                    // StatsCalculator combat resolution" per the task.
                    if (payload.PendingSkillDamageMultiplier > 0f)
                    {
                        rawDamage = (int)(rawDamage * payload.PendingSkillDamageMultiplier);
                        payload.PendingSkillDamageMultiplier = 0f;
                    }

                    // Step 3 (Mitigation). Monsters carry no block stat (no
                    // shields modeled on the PvE monster side), so this stays
                    // a pure armor subtraction - activeMonster.Armor is now
                    // sourced from content data instead of a hardcoded 0.
                    int defenderArmor = activeMonster.Armor;
                    int netDamage = Math.Max(1000, rawDamage - (defenderArmor * 1000));
                    netDamage = (int)(netDamage * payload.CachedCodexDamageMultiplier);

                    payload.CurrentMonsterHp -= netDamage;

                    // Sprint 38: Lifesteal
                    if (combatStats.LifestealPct > 0)
                    {
                        int lifestealAmount = (int)(netDamage * combatStats.LifestealPct);
                        payload.PlayerHp += lifestealAmount;
                        if (payload.PlayerHp > effectiveMaxHp) payload.PlayerHp = effectiveMaxHp;
                    }

                }
            }

            // Monster attacks player
            if (payload.CurrentMonsterHp > 0 && (payload.CombatTargetTickAccumulator * 100) % activeMonster.AttackIntervalMs == 0)
            {
                // Step 1 (Hit Determination). Monsters have no authored
                // accuracy stat (their content data only defines DodgeRating
                // and Armor, both defensive), so attackerAccuracy stays the
                // fixed baseline; combatStats.DodgeChancePct (defensive
                // potions, Vila's innate racial passive) is the player's own
                // defensive stat and was already wired here.
                float attackerAccuracy = 100f;
                float defenderDodge = 100f + combatStats.DodgeChancePct;
                float hitChance = Math.Clamp(attackerAccuracy / defenderDodge, 0.05f, 0.95f);

                if (Random.Shared.NextDouble() <= hitChance)
                {
                    // Step 2 (Monster Crit Check): 5% base + 0.5% per region
                    // tier (region now resolved via
                    // ContentRegistry.GetMonsterRegionTier, which uses each
                    // monster's authored RegionTier instead of wrapping ids
                    // 31+ back onto tiers 1-5). Vodnik's innate
                    // CritMitigationPct subtracts directly from the crit
                    // damage multiplier, floored at 1.0 so mitigation can never
                    // make a crit deal less than a normal hit.
                    int monsterRegionTier = ContentRegistry.GetMonsterRegionTier(payload.CurrentMonsterId);
                    float monsterCritChance = 0.05f + (monsterRegionTier * 0.005f);
                    float monsterCritMult = 1.0f;
                    if (Random.Shared.NextDouble() <= monsterCritChance)
                    {
                        monsterCritMult = Math.Max(1.0f, 1.5f - (combatStats.CritMitigationPct / 100f));
                    }

                    int rawDamage = (int)(ContentRegistry.GetScaledMonsterAttackPower(payload.CurrentMonsterId) * 1000 * monsterCritMult);

                    // Step 3+4 (Armor then Block, combined): armor subtracts
                    // flat milli-damage, BlockStrengthPct (CON-derived, see
                    // StatsCalculator) then reduces what remains
                    // multiplicatively - a shield/bulk stat that shaves a
                    // fraction off whatever armor did not already stop,
                    // rather than stacking as another flat subtraction.
                    // Clamped below 100% so a high-CON build can reduce a hit
                    // close to the floor but never to true zero damage.
                    float blockStrengthFraction = Math.Clamp(combatStats.BlockStrengthPct / 100f, 0f, 0.75f);
                    int armorMitigatedDamage = rawDamage - (combatStats.FlatPhysicalArmor * 1000);
                    int finalDamage = Math.Max(1000, (int)(armorMitigatedDamage * (1f - blockStrengthFraction)));

                    payload.PlayerHp -= finalDamage;

                }
            }

            // Step 5 (Auto-Eat)
            if (payload.PlayerHp > 0 && payload.PlayerHp <= (payload.AutoEatThreshold / 100.0f) * effectiveMaxHp)
            {
                int bestFoodIndex = 0;
                int highestHeal = 0;

                int heal1 = payload.Food1_ItemId > 0 ? 50000 : 0;
                int heal2 = payload.Food2_ItemId > 0 ? 50000 : 0;
                int heal3 = payload.Food3_ItemId > 0 ? 50000 : 0;

                if (payload.Food1_Count > 0 && heal1 > highestHeal) { bestFoodIndex = 1; highestHeal = heal1; }
                if (payload.Food2_Count > 0 && heal2 > highestHeal) { bestFoodIndex = 2; highestHeal = heal2; }
                if (payload.Food3_Count > 0 && heal3 > highestHeal) { bestFoodIndex = 3; highestHeal = heal3; }

                if (bestFoodIndex == 1) { payload.Food1_Count--; payload.PlayerHp += highestHeal; }
                else if (bestFoodIndex == 2) { payload.Food2_Count--; payload.PlayerHp += highestHeal; }
                else if (bestFoodIndex == 3) { payload.Food3_Count--; payload.PlayerHp += highestHeal; }
                else
                {
                    if (liveSessionContexts.TryGetValue(payload.PlayerId, out var telemetrySessionContext))
                    {
                        telemetrySessionContext.UpdateAccountId(payload.AccountId);
                        telemetrySessionContext.WriteTelemetryEvent(
                            TelemetryStreamingEngine.PackTelemetryMetric(
                                TelemetryStreamingEngine.KpiAutoEatDepletedHaltHash,
                                payload.ActiveActivityId));
                    }

                    payload.ActiveActivityId = 0;
                    payload.CurrentMonsterHp = 0;
                    payload.CurrentMonsterId = 0;
                    payload.CombatTargetTickAccumulator = 0;
                }

                if (payload.PlayerHp > effectiveMaxHp) payload.PlayerHp = effectiveMaxHp;
            }

            if (payload.PlayerHp <= 0)
            {
                payload.PlayerHp = effectiveMaxHp;
                payload.CurrentMonsterId = 0;
                payload.CurrentMonsterHp = 0;
                payload.CombatTargetTickAccumulator = 0;
                payload.ActiveActivityId = 0;
                return;
            }

            if (payload.CurrentMonsterHp <= 0 && payload.ActiveActivityId > 0)
            {
                int finalXpMultiplier = localXpMultiplier;
                if (payload.CurrentLevel < 50 && payload.CachedMentorCount > 0)
                {
                    finalXpMultiplier += payload.CachedMentorCount * 5;
                }

                finalXpMultiplier += RaceMasteryResolver.GetHumanXpBonusPct(payload.HumanMasteryLevel);
            finalXpMultiplier += LegacyPerkResolver.GetXpBonusPct(payload.CachedLegacyPerks);

                if (payload.ActiveMentorPlayerId > 0 && payload.MentorshipExpBonusMultiplier > 1.0)
                {
                    finalXpMultiplier = (int)(finalXpMultiplier * payload.MentorshipExpBonusMultiplier);
                }

                int seasonalCombatXp = activeMonster.BaseXpReward * finalXpMultiplier / 100;
                ProgressionEngine.ProcessMonsterDeath(ref payload, activeMonster.BaseXpReward, finalXpMultiplier, ActiveGlobalEventId, activeRaceId);
                AddSeasonalXp(ref payload, seasonalCombatXp);
                
                if (liveSessionContexts.TryGetValue(payload.PlayerId, out var sessionCtx))
                {
                    sessionCtx.ThreadSafeAddMonsterKill();
                }

                QuestEngine.IncrementProgress(ref payload, QuestEngine.QuestTypeKillMonsters, 1);

                long goldReward = (activeMonster.BaseGoldReward * (long)GlobalEngineState.GlobalGoldDropMultiplier) / 100L;
                // Modul 13.4.3: Human's innate +5% Gold acquisition passive.
                goldReward = (long)(goldReward * (1.0f + combatStats.GoldAcquisitionMultiplierPct / 100f));
                goldReward = (long)(goldReward * (1.0f + LegacyPerkResolver.GetGoldBonusPct(payload.CachedLegacyPerks) / 100f));
                if (goldReward > 0)
                {
                    payload.AddGold(goldReward);
                    payload.RedisPendingGoldDelta += goldReward;
                    payload.RequiresRedisFlush = true;
                    payload.IsDirty = true;
                }

                // Codex Integration (Sprint 38)
                int codexRaceId = 0;
                if (payload.Slot1_CharacterId != System.Guid.Empty)
                {
                    codexRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);
                }
                
                CodexEngine.KillEventQueue.Enqueue(new KillEvent
                {
                    PlayerId = payload.PlayerId,
                    MonsterId = payload.CurrentMonsterId,
                    RaceId = codexRaceId,
                    GainedXp = seasonalCombatXp
                });

                bool isRegionalBoss = activeMonster.Id % 6 == 0;

                if (payload.ActiveGuildWarId > 0)
                {
                    int wp = isRegionalBoss ? 500 : 10;
                    guildWarPointQueue.Enqueue(new GuildWarPointEvent
                    {
                        MatchId = payload.ActiveGuildWarId,
                        GuildId = payload.GuildId,
                        Front = 0,
                        Points = wp
                    });
                }

                // Modul 03: 0.05% flat Premium Diamond drop from standard/elite
                // monsters, guaranteed 10-diamond cluster from Regional Bosses
                // (same activeMonster.Id % 6 == 0 heuristic used for Guild War
                // Combat Vanguard WP above). PremiumCurrency is updated directly
                // in-memory here (no DB access needed on the hot path) and
                // persisted on the next checkpoint flush like gold.
                if (isRegionalBoss)
                {
                    payload.SetPremiumCurrency(payload.PremiumCurrency + 10);
                    payload.IsDirty = true;
                }
                else if (Random.Shared.NextDouble() < 0.0005)
                {
                    payload.SetPremiumCurrency(payload.PremiumCurrency + 1);
                    payload.IsDirty = true;
                }

                // Modul 03/10/11/12: equipment drop roll request. ProcessSubTick
                // is static, so this enqueues onto CombatLootEngine's static
                // queue (mirroring CodexEngine.KillEventQueue) rather than
                // calling an instance method directly - CombatLootEngine's own
                // background poll loop performs the actual DB insert.
                CombatLootEngine.DropRequestQueue.Enqueue(new CombatLootDropRequest
                {
                    PlayerId = payload.PlayerId,
                    MonsterId = payload.CurrentMonsterId,
                    LootLuckPct = combatStats.LootLuckPct
                });

                var lootTable = ContentRegistry.GetLootTable(activeMonster.LootTableId);
                if (lootTable.Length > 0 && payload.InventorySpaceRemaining > 0)
                {
                    int totalWeight = 0;
                    for (int i = 0; i < lootTable.Length; i++) totalWeight += lootTable[i].Weight;
                    
                    if (totalWeight > 0)
                    {
                        int multiplier = (int)(localDropMultiplier * payload.CachedCodexYieldMultiplier);
                        int guaranteedRolls = multiplier / 100;
                        int fractionalBonus = multiplier % 100;

                        int rollsToExecute = guaranteedRolls;
                        if (fractionalBonus > 0 && Random.Shared.Next(100) < fractionalBonus)
                        {
                            rollsToExecute++;
                        }

                        for (int r = 0; r < rollsToExecute; r++)
                        {
                            if (payload.InventorySpaceRemaining <= 0) break;

                            int roll = Random.Shared.Next(totalWeight);
                            int currentWeight = 0;
                            for (int i = 0; i < lootTable.Length; i++)
                            {
                                currentWeight += lootTable[i].Weight;
                                if (roll < currentWeight)
                                {
                                    // Modul 04: Kobold's packed-weight penalty,
                                    // mirroring the gathering loot roll above.
                                    int itemWeight = 1;
                                    if (activeRaceId == RaceIds.Kobold)
                                    {
                                        string droppedBaseId = ContentRegistry.GetMaterialString(lootTable[i].ItemId);
                                        bool isOreOrBar = droppedBaseId.Contains("_ore_") || droppedBaseId.Contains("_bar_");
                                        if (!isOreOrBar) itemWeight = 2;
                                    }

                                    if (itemWeight > payload.InventorySpaceRemaining)
                                    {
                                        r = rollsToExecute;
                                        break;
                                    }

                                    payload.InventorySpaceRemaining -= itemWeight;
                                    break;
                                }
                            }
                        }
                    }
                }

                payload.CurrentMonsterId = fallbackId;
                payload.CurrentMonsterHp = ContentRegistry.GetScaledMonsterMaxHp(payload.CurrentMonsterId) * 1000;
                payload.CombatTargetTickAccumulator = 0;
            }
        }
    }
}
