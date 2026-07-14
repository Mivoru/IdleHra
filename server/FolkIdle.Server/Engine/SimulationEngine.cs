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

        public SimulationEngine(LootTableEngine lootEngine, StateCheckpointManager checkpointManager, NetworkBroadcastSystem networkSystem, ForgeSplicingEngine forgeEngine, MarketOrderBookEngine marketEngine, PlayerSessionRegistry playerRegistry, GuildContributionEngine guildEngine, MarketEscrowEngine escrowEngine, MailboxAndBankEngine mailboxEngine, AffixRerollEngine rerollEngine, BreedingEngine breedingEngine, GuildLogisticsEngine guildLogisticsEngine, CraftingEngine craftingEngine, WorldBossEngine worldBossEngine, VillageBuildingEngine villageBuildingEngine, VillageManagementEngine villageManagementEngine, MentorshipEngine mentorshipEngine, GuildWarEngine guildWarEngine, ChronoCoreEngine chronoCoreEngine, LegacyStoreEngine legacyStoreEngine, GuildLogisticsDepotEngine guildLogisticsDepotEngine, GuildCombatSimulationEngine guildCombatSimulationEngine, AntiCheatTelemetryEngine antiCheatTelemetryEngine, PushNotificationTriggerEngine pushNotificationTriggerEngine, CompliancePurgeEngine compliancePurgeEngine, BillingVerificationEngine billingVerificationEngine, StackExchange.Redis.IConnectionMultiplexer redis, Microsoft.EntityFrameworkCore.IDbContextFactory<FolkIdleDbContext> contextFactory, GuildRaidEngine? guildRaidEngine = null)
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

        public void InjectVirtualPlayer(TickStatePayload payload)
        {
            lock (_activePlayers)
            {
                _activePlayers[payload.PlayerId] = payload;
                _liveSessionContexts.TryAdd(payload.PlayerId, new LiveSessionContext(payload.PlayerId, payload.AccountId));
            }
        }

        public void InjectBenchmarkCommand(long playerId, ClientCommandPacket packet)
        {
            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = playerId, Packet = packet });
        }

        private void TerminateSessionForSecurity(long playerId)
        {
            _activePlayers.Remove(playerId);
            _liveSessionContexts.TryRemove(playerId, out _);
            _playerRegistry.UnregisterPlayer(playerId);
            _networkSystem.PurgeTokensForPlayer(playerId);
            _networkSystem.ForceDisconnect(playerId);
        }

        private static uint ClampWorldBossHpToUInt(long value)
        {
            if (value <= 0)
            {
                return 0;
            }

            return value > uint.MaxValue ? uint.MaxValue : (uint)value;
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

                while (_playerRegistry.CodexMultiplierUpdateQueue.TryDequeue(out var codexMultiplierUpdate))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, codexMultiplierUpdate.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.CachedCodexYieldMultiplier = codexMultiplierUpdate.YieldMultiplier;
                        currentPayload.CachedCodexDamageMultiplier = codexMultiplierUpdate.DamageMultiplier;
                    }
                }

                while (_playerRegistry.CraftingCompletionQueue.TryDequeue(out var craftCompletion))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, craftCompletion.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
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

                while (_playerRegistry.GuildUpdateQueue.TryDequeue(out var guildUpdate))
                {
                    // Real-time updates for guild members
                    foreach (var kvp in _activePlayers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, kvp.Key);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload) && currentPayload.GuildId == guildUpdate.GuildId)
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
                    }
                }

                while (_playerRegistry.GuildLogisticsDepotUpdateQueue.TryDequeue(out var depotNotif))
                {
                    foreach (var kvp in _activePlayers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, kvp.Key);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload) && currentPayload.GuildId == depotNotif.GuildId)
                        {
                            currentPayload.GuildLogisticsCurrentStock = depotNotif.CurrentStock;
                            currentPayload.GuildLogisticsTargetRequirement = depotNotif.TargetRequirement;
                            currentPayload.CachedGuildLogisticsLevel = depotNotif.Level;
                        }
                    }
                }

                while (_playerRegistry.GuildCombatSimulationUpdateQueue.TryDequeue(out var combatNotif))
                {
                    foreach (var kvp in _activePlayers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, kvp.Key);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload) &&
                            (currentPayload.GuildId == combatNotif.AttackingGuildId || currentPayload.GuildId == combatNotif.DefendingGuildId))
                        {
                            currentPayload.CombatSimulationMatchId = combatNotif.MatchId;
                            currentPayload.CombatSimulationTurnCounter = combatNotif.TurnCounter;
                            currentPayload.CombatSimulationDamageDelta = combatNotif.DamageDelta;
                        }
                    }
                }

                while (_playerRegistry.GuildRaidBossUpdateQueue.TryDequeue(out var raidNotif))
                {
                    foreach (var kvp in _activePlayers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, kvp.Key);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload) && currentPayload.GuildId == raidNotif.GuildId)
                        {
                            currentPayload.CachedGuildRaidTier = raidNotif.RaidTier;
                            currentPayload.CachedGuildRaidBossCurrentHp = raidNotif.RaidBossCurrentHp;
                            currentPayload.CachedGuildRaidBossMaxHp = raidNotif.RaidBossMaxHp;
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
                    }
                }

                while (_playerRegistry.MailClaimRequestQueue.TryDequeue(out var req))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, req.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        if (req.HasItem && currentPayload.InventorySpaceRemaining <= 0)
                        {
                            Task.Run(async () => { await _mailboxEngine.CommitMailClaimAsync(req.MailId, false); });
                        }
                        else
                        {
                            if (req.HasItem) currentPayload.InventorySpaceRemaining--;
                            currentPayload.AddGold(req.GoldAttachment);
                            currentPayload.IsDirty = true;
                            Task.Run(async () => { await _mailboxEngine.CommitMailClaimAsync(req.MailId, true); });
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
                            Task.Run(async () => { await _mailboxEngine.CommitBankWithdrawAsync(req.BankId, false); });
                        }
                        else
                        {
                            currentPayload.InventorySpaceRemaining--;
                            currentPayload.IsDirty = true;
                            Task.Run(async () => { await _mailboxEngine.CommitBankWithdrawAsync(req.BankId, true); });
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
                                        Task.Run(async () => {
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
                            Task.Run(async () => {
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

                            Task.Run(async () => {
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
                        Task.Run(async () => {
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
                            _activePlayers.Remove(routingPlayerId);
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

                        Task.Run(async () => {
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
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long guildId = currentPayload.GuildId;
                        long quantity = cmd.LimitPrice;
                        int itemDefinitionId = (int)cmd.TargetId;
                        long pId = currentPayload.PlayerId;

                        if (guildId > 0 && quantity > 0)
                        {
                            Task.Run(async () => {
                                await _guildLogisticsEngine.ExecuteGuildContributionAsync(pId, guildId, quantity, itemDefinitionId);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.ExecuteForgeFusion)
                    {
                        if (!ClientCommandValidator.ValidateFusionCommand(ref currentPayload, cmd.TargetId, cmd.SecondaryId, cmd.TertiaryId))
                        {
                            _activePlayers.Remove(routingPlayerId);
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

                        Task.Run(async () => {
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
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        currentPayload.IsSuspended = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);
                        
                        long pId = currentPayload.PlayerId;
                        long cTargetId = cmd.TargetId;
                        int affixIndex = cmd.LimitPrice;

                        Task.Run(async () => {
                            await _rerollEngine.ExecuteRerollAsync(pId, cTargetId, affixIndex);
                            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                        });
                    }
                    else if (cmd.Command == CommandType.ExecuteBreeding)
                    {
                        if (!ClientCommandValidator.ValidateBreedingRequest(ref currentPayload, ref cmd))
                        {
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        var patId = cmd.TargetGuid;
                        var matId = cmd.SecondaryGuid;

                        Task.Run(async () => {
                            await _breedingEngine.ExecuteBreedingAsync(pId, patId, matId);
                        });
                    }
                    else if (cmd.Command == CommandType.InitializeCrafting)
                    {
                        long pId = currentPayload.PlayerId;
                        int resultItemId = (int)cmd.TargetId;
                        
                        Task.Run(async () => {
                            await _craftingEngine.ExecuteCraftingAsync(pId, resultItemId);
                        });
                    }
                    else if (cmd.Command == CommandType.CraftItem)
                    {
                        if (!ClientCommandValidator.ValidateCraftingRequest(ref currentPayload, ref cmd))
                        {
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        uint recipeId = cmd.TargetRecipeId;
                        uint slotIndex = cmd.CraftingSlotIndex;
                        uint tickToken = (uint)currentPayload.LogicEpochCounter;
                        
                        Task.Run(async () => {
                            await _craftingEngine.ExecuteEquipmentCraftingAsync(pId, recipeId, slotIndex, tickToken);
                        });
                    }
                    else if (cmd.Command == CommandType.UpgradeBuilding)
                    {
                        if (!ClientCommandValidator.ValidateVillageManagementRequest(ref currentPayload, ref cmd))
                        {
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        uint buildingId = cmd.TargetBuildingId;
                        
                        Task.Run(async () => {
                            await _villageManagementEngine.ExecuteUpgradeBuildingAsync(pId, buildingId);
                        });
                    }
                    else if (cmd.Command == CommandType.EvictVillager)
                    {
                        if (!ClientCommandValidator.ValidateVillageManagementRequest(ref currentPayload, ref cmd))
                        {
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        uint villagerSlot = cmd.TargetVillagerSlot;

                        Task.Run(async () => {
                            await _villageManagementEngine.ExecuteEvictVillagerAsync(pId, villagerSlot);
                        });
                    }
                    else if (cmd.Command == CommandType.UpgradeTool)
                    {
                        if (!ClientCommandValidator.ValidateUpgradeRequest(ref currentPayload, (byte)cmd.Command, 0))
                        {
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        
                        Task.Run(async () => {
                            await _villageBuildingEngine.ExecuteUpgradeToolAsync(pId);
                        });
                    }
                    else if (cmd.Command == CommandType.AssignMentor)
                    {
                        // TODO: Add validator check if needed, but the prompt says: ValidateMentorshipAssignment in ClientCommandValidator
                        if (!ClientCommandValidator.ValidateMentorshipAssignment(ref currentPayload, cmd.TargetGuid, (int)cmd.LimitPrice))
                        {
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        Guid charId = cmd.TargetGuid;
                        int slotIndex = cmd.LimitPrice;
                        
                        Task.Run(async () => {
                            await _mentorshipEngine.ExecuteAssignMentorAsync(pId, charId, slotIndex);
                            // Trigger full reload so mentor count reflects accurately from DB
                            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                        });
                    }
                    else if (cmd.Command == CommandType.EstablishMentorship)
                    {
                        if (!ClientCommandValidator.ValidateMentorshipRequest(ref currentPayload, ref cmd))
                        {
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long menteePlayerId = currentPayload.PlayerId;
                        long mentorPlayerId = cmd.TargetPlayerId;

                        Task.Run(async () => {
                            var result = await _mentorshipEngine.EstablishMentorshipContractAsync(menteePlayerId, mentorPlayerId);
                            if (result == MentorshipContractResult.InvalidRequest)
                            {
                                _networkSystem.PurgeTokensForPlayer(menteePlayerId);
                                _networkSystem.ForceDisconnect(menteePlayerId);
                            }
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

                        Task.Run(async () => {
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
                        Task.Run(async () => {
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
                        Task.Run(async () => {
                            await _mailboxEngine.DepositToBankAsync(pId, instanceId);
                        });
                    }
                    else if (cmd.Command == CommandType.WithdrawFromBank)
                    {
                        long pId = currentPayload.PlayerId;
                        long bankId = cmd.TargetId;
                        Task.Run(async () => {
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

                        Task.Run(async () => {
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
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        long chronoCoreItemId = cmd.TargetId;

                        Task.Run(async () => {
                            await _chronoCoreEngine.ConsumeChronoCoreAsync(pId, chronoCoreItemId);
                        });
                    }
                    else if (cmd.Command == CommandType.PurchaseLegacyUnlocks)
                    {
                        if (!ClientCommandValidator.ValidateLegacyStoreRequest(ref currentPayload, ref cmd))
                        {
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        uint unlockId = cmd.TargetUnlockId;
                        uint slotIndex = cmd.RequestedSlotIndex;

                        Task.Run(async () => {
                            await _legacyStoreEngine.PurchaseLegacyUnlockAsync(pId, unlockId, slotIndex);
                        });
                    }
                    else if (cmd.Command == CommandType.DepositGuildMaterial)
                    {
                        if (!ClientCommandValidator.ValidateGuildDepositRequest(ref currentPayload, ref cmd))
                        {
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        long guildId = currentPayload.GuildId;
                        uint materialId = cmd.MaterialId;
                        uint quantity = cmd.DepositQuantity;

                        Task.Run(async () => {
                            await _guildLogisticsDepotEngine.DepositMaterialAsync(pId, guildId, materialId, quantity);
                        });
                    }
                    else if (cmd.Command == CommandType.LaunchGuildRaid)
                    {
                        long raidGuildId = currentPayload.GuildId;
                        if (raidGuildId > 0 && _guildRaidEngine != null)
                        {
                            Task.Run(async () => {
                                await _guildRaidEngine.TryStartRaidAsync(raidGuildId);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.ExecuteCombatTurn)
                    {
                        if (!ClientCommandValidator.ValidateCombatTurnRequest(ref currentPayload, ref cmd))
                        {
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.PurgeTokensForPlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        long pId = currentPayload.PlayerId;
                        long guildId = currentPayload.GuildId;
                        ClientCommandPacket capturedCommand = cmd;

                        Task.Run(async () => {
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
                            _activePlayers.Remove(routingPlayerId);
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

                        _worldBossEngine.QueueAttack(currentPayload.PlayerId, cmd.TargetedBossId, cmd.ClientPredictedDamage);
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
                            _activePlayers.Remove(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        _worldBossEngine.RegisterDamage(currentPayload.PlayerId, cmd.TargetId);
                    }
                    else if (cmd.Command == CommandType.Logout)
                    {
                        currentPayload.LastLogoutTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        currentPayload.IsDirty = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);
                        _playerRegistry.UnregisterPlayer(cmd.TargetId);
                        currentPayload.IsSuspended = true;
                        _activePlayers.Remove(routingPlayerId);
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
                        int premiumAmount = (int)cmd.LimitPrice;
                        
                        Task.Run(async () => {
                            bool success = await _billingVerificationEngine.VerifyPurchaseAsync(pId, transactionId, productId, premiumAmount);
                            if (success) {
                                _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                            }
                        });
                    }
                    else if (cmd.Command == CommandType.SyncBillingStatus)
                    {
                        // No-op for now. Acknowledge and continue.
                        currentPayload.IsDirty = true;
                    }
                    else if (cmd.Command == CommandType.ReportUiContextSwitch)
                    {
                        currentPayload.ActiveUiContextBitmask = cmd.ActiveUiContextBitmask;
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
                    _activePlayers[readyState.PlayerId] = readyState;
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

                        ProcessTick(ref currentPayload);
                        _checkpointManager.TrackState(ref currentPayload);
                    }
                }

                _ticksSinceLastBroadcast++;
                if (_ticksSinceLastBroadcast >= 10)
                {
                    _metrics.ThrottledPacketsDropped = _networkSystem.GetThrottledCounter();
                    _ticksSinceLastBroadcast = 0;

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
                                CachedIronOreStock = currentPayload.CachedIronOreStock
                            };
                            _networkSystem.Broadcast(ref packet);
                            currentPayload.NetworkDiagnosticsToken = 0; // Clear it so it only echoes once
                        }
                    }
                }

                stopwatch.Stop();
                long tickEndTimestamp = Stopwatch.GetTimestamp();
                _metrics.TotalTicksProcessed++;
                _metrics.LastExecutionTimeMs = stopwatch.ElapsedMilliseconds;

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
            int attacksPerKill = EstimateAttacksPerKill(ref payload, monster);
            long ticksPerAttack = 15L;
            long ticksPerKill = System.Math.Max(1L, attacksPerKill * ticksPerAttack);
            long completedKills = totalTicks / ticksPerKill;
            payload.CombatTargetTickAccumulator = (int)(totalTicks % ticksPerKill);

            if (completedKills <= 0)
            {
                return;
            }

            int finalXpMultiplier = GlobalEngineState.GlobalXpMultiplier;
            if (payload.CurrentLevel < 50 && payload.CachedMentorCount > 0)
            {
                finalXpMultiplier += payload.CachedMentorCount * 5;
            }

            finalXpMultiplier += RaceMasteryResolver.GetHumanXpBonusPct(payload.HumanMasteryLevel);

            if (payload.ActiveMentorPlayerId > 0 && payload.MentorshipExpBonusMultiplier > 1.0)
            {
                finalXpMultiplier = (int)(finalXpMultiplier * payload.MentorshipExpBonusMultiplier);
            }

            double integratedBuffMultiplier = CalculateIntegratedBuffMultiplier(warpSeconds, remainingBuffTicks, potencyModifierPct);
            long xpGain = (long)Math.Floor(completedKills * monster.BaseXpReward * finalXpMultiplier * integratedBuffMultiplier / 100.0);
            ApplyBulkExperience(ref payload, xpGain);
            AddSeasonalXp(ref payload, ClampLongToInt(xpGain));

            long goldReward = completedKills * monster.BaseGoldReward * GlobalEngineState.GlobalGoldDropMultiplier / 100L;
            if (goldReward > 0)
            {
                payload.AddGold(goldReward);
                payload.RedisPendingGoldDelta += goldReward;
                payload.RequiresRedisFlush = true;
            }

            long expectedDrops = CalculateExpectedCombatWarpDrops(ref payload, completedKills, integratedBuffMultiplier);
            ConsumeInventorySlots(ref payload, expectedDrops);
            payload.CurrentMonsterId = monsterId;
            payload.CurrentMonsterHp = monster.MaxHp * 1000;
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

        private static int EstimateAttacksPerKill(ref TickStatePayload payload, MonsterDefinition monster)
        {
            int decayedStrength = payload.STR <= 0 ? 0 : (int)System.Math.Floor(System.Math.Log(payload.STR + 1.0) * 1000.0);
            long expectedDamage = 15000L + decayedStrength + (payload.CurrentLevel * 750L);
            if (expectedDamage < 1000L) expectedDamage = 1000L;
            long monsterHp = (long)monster.MaxHp * 1000L;
            long attacks = (monsterHp + expectedDamage - 1L) / expectedDamage;
            if (attacks <= 0L) return 1;
            if (attacks > int.MaxValue) return int.MaxValue;
            return (int)attacks;
        }

        private static long CalculateExpectedWarpDrops(ref TickStatePayload payload, long completedCycles, int professionType, double integratedBuffMultiplier)
        {
            int monolithLevel = professionType == 0 ? payload.CachedWoodcuttingMonolithLevel : payload.CachedMiningMonolithLevel;
            double yieldBonusPct = System.Math.Min(monolithLevel, 50);
            double decayedLuckPct = payload.LCK <= 0 ? 0.0 : System.Math.Log(payload.LCK + 1.0) * 2.5;
            double raceMasteryYieldBonusPct = professionType == 1
                ? RaceMasteryResolver.GetKoboldOreDuplicationBonusPct(payload.KoboldMasteryLevel)
                : RaceMasteryResolver.GetMoosleuteDoubleHarvestBonusPct(payload.MoosleuteMasteryLevel);
            double multiplier = GlobalEngineState.GlobalDropMultiplier + yieldBonusPct + decayedLuckPct + raceMasteryYieldBonusPct;
            if (ActiveGlobalEventId == 1)
            {
                multiplier += 20.0;
            }

            return (long)System.Math.Floor(completedCycles * System.Math.Max(0.0, multiplier) * integratedBuffMultiplier / 100.0);
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

        private static void ApplyBulkExperience(ref TickStatePayload payload, long xpGain)
        {
            if (xpGain <= 0)
            {
                return;
            }

            payload.CurrentXp = System.Math.Max(0L, payload.CurrentXp + xpGain);
            while (payload.CurrentLevel > 0)
            {
                long requiredXp = 100L * payload.CurrentLevel * payload.CurrentLevel;
                if (payload.CurrentXp < requiredXp)
                {
                    break;
                }

                payload.CurrentXp -= requiredXp;
                payload.CurrentLevel++;
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

            ProcessPassiveVillageTick(ref payload, TickIntervalSeconds);
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

                    ProcessPassiveVillageTick(ref payload, TickIntervalSeconds);
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
                    ProcessPassiveVillageTick(ref payload, TickIntervalSeconds);
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

        internal static void ProcessPassiveVillageTick(ref TickStatePayload payload, double deltaTimeSeconds)
        {
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
                        int monolithLevel = gatheringNode.ProfessionType == 0 ? payload.CachedWoodcuttingMonolithLevel : payload.CachedMiningMonolithLevel;
                        float yieldBonusPct = Math.Min(monolithLevel * 1.0f, 50.0f);
                        int additionalYieldBonus = (int)(100f * (yieldBonusPct / 100f)); // Add to multiplier

                        // Modul 13: Kobold ore duplication (Mining) / Moosleute yield
                        // bonus. No dedicated Herbalism profession exists in this
                        // codebase (only Woodcutting=0 and Mining=1), so Moosleute's
                        // "double harvest" is applied to Woodcutting as the closest
                        // available gathering profession.
                        if (gatheringNode.ProfessionType == 1)
                        {
                            additionalYieldBonus += (int)RaceMasteryResolver.GetKoboldOreDuplicationBonusPct(payload.KoboldMasteryLevel);
                        }
                        else
                        {
                            additionalYieldBonus += (int)RaceMasteryResolver.GetMoosleuteDoubleHarvestBonusPct(payload.MoosleuteMasteryLevel);
                        }

                        if (ActiveGlobalEventId == 1) // GoldenHarvest
                        {
                            additionalYieldBonus += 20;
                        }

                        int totalWeight = 0;
                        for (int i = 0; i < lootTable.Length; i++) totalWeight += lootTable[i].Weight;
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
                                    currentWeight += lootTable[i].Weight;
                                    if (roll < currentWeight)
                                    {
                                        payload.InventorySpaceRemaining--;
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

            var combatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, activeAgePhase, payload.CompletedAreaFlags, activeRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel);
            
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
                payload.CurrentMonsterHp = ContentRegistry.Monsters[payload.CurrentMonsterId - 1].MaxHp * 1000;
                payload.CombatTargetTickAccumulator = 0;
            }

            payload.CombatTargetTickAccumulator++;

            var activeMonster = ContentRegistry.Monsters[payload.CurrentMonsterId - 1];

            // Player attacks monster
            int playerAttackSpeedMs = (int)(1500 * (1.0f - combatStats.AttackSpeedPct));
            if (playerAttackSpeedMs < 200) playerAttackSpeedMs = 200; // Hard cap attack speed

            if ((payload.CombatTargetTickAccumulator * 100) % playerAttackSpeedMs == 0)
            {
                // Step 1 (Hit Determination)
                float attackerAccuracy = 100f; // Placeholder until Accuracy is defined
                float defenderDodge = 100f; // Placeholder
                float hitChance = Math.Clamp(attackerAccuracy / defenderDodge, 0.05f, 0.95f);

                if (Random.Shared.NextDouble() <= hitChance)
                {
                    // Step 2 (Crit Check)
                    float critMult = 1.0f;
                    if (Random.Shared.NextDouble() <= (combatStats.CritChancePct / 100.0f))
                    {
                        critMult = 1.5f;
                    }

                    long baseMilliAttack = 15000L;
                    long effectiveMilliAttack = baseMilliAttack + (baseMilliAttack * lineage.DamageScalePerLevelPct * payload.CurrentLevel / 100) + (combatStats.FlatMeleeDamage * 1000L);
                    int rawDamage = (int)(effectiveMilliAttack * critMult);

                    // Step 3 (Mitigation)
                    int defenderArmor = 0;
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
                // Step 1 (Hit Determination)
                float attackerAccuracy = 100f; 
                float defenderDodge = 100f; 
                float hitChance = Math.Clamp(attackerAccuracy / defenderDodge, 0.05f, 0.95f);

                if (Random.Shared.NextDouble() <= hitChance)
                {
                    int rawDamage = activeMonster.AttackPower * 1000;
                    
                    // Step 3 (Mitigation)
                    int netDamage = Math.Max(1000, rawDamage - (combatStats.FlatPhysicalArmor * 1000));
                    
                    // Step 4 (Shield Check)
                    int blockStrength = 0;
                    int finalDamage = Math.Max(0, netDamage - (blockStrength * 1000));

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

                if (payload.ActiveMentorPlayerId > 0 && payload.MentorshipExpBonusMultiplier > 1.0)
                {
                    finalXpMultiplier = (int)(finalXpMultiplier * payload.MentorshipExpBonusMultiplier);
                }

                int seasonalCombatXp = activeMonster.BaseXpReward * finalXpMultiplier / 100;
                ProgressionEngine.ProcessMonsterDeath(ref payload, activeMonster.BaseXpReward, finalXpMultiplier, ActiveGlobalEventId);
                AddSeasonalXp(ref payload, seasonalCombatXp);
                
                if (liveSessionContexts.TryGetValue(payload.PlayerId, out var sessionCtx))
                {
                    sessionCtx.ThreadSafeAddMonsterKill();
                }

                long goldReward = (activeMonster.BaseGoldReward * (long)GlobalEngineState.GlobalGoldDropMultiplier) / 100L;
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

                if (payload.ActiveGuildWarId > 0)
                {
                    int wp = (activeMonster.Id % 6 == 0) ? 500 : 10;
                    guildWarPointQueue.Enqueue(new GuildWarPointEvent
                    {
                        MatchId = payload.ActiveGuildWarId,
                        GuildId = payload.GuildId,
                        Front = 0,
                        Points = wp
                    });
                }

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
                                    payload.InventorySpaceRemaining--;
                                    break;
                                }
                            }
                        }
                    }
                }

                payload.CurrentMonsterId = fallbackId;
                payload.CurrentMonsterHp = ContentRegistry.Monsters[payload.CurrentMonsterId - 1].MaxHp * 1000;
                payload.CombatTargetTickAccumulator = 0;
            }
        }
    }
}
