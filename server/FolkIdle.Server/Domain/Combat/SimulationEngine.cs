using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Network;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Combat
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

        // Modul: set bonuses made real. Magnitudes for the 4-piece effects,
        // which had none because nothing consumed them. Chosen to be worth
        // chasing without eclipsing the flat 2-piece core: a burn adding a
        // quarter of the hit that applied it, and thorns returning a fifth of
        // what actually landed. Both are deliberately fractions of a real
        // number already computed on the same path, so neither needs its own
        // scaling curve to stay relevant across the five regions.
        private const float BurnDamageFraction = 0.25f;
        private const float ThornsReflectionFraction = 0.20f;

        // The Eternal Dreadnought 4-piece's cooldown reduction. Applied to the
        // cooldown stamped after a successful cast.
        private const float SetCooldownReductionFraction = 0.20f;

        // The Eternal Dreadnought 4-piece's per-hit damage ceiling, as a share
        // of effective max HP. At 20 percent a wearer always survives at least
        // five consecutive hits from full, which is the point: it buys the
        // auto-eat larder the window it needs to respond, without making the
        // wearer immortal against sustained damage.
        private const float SetDamageCapMaxHpFraction = 0.20f;
        private const double TickIntervalSeconds = TickIntervalMs / 1000.0;
        private readonly LootTableEngine _lootEngine;
        private readonly InheritanceEngine? _inheritanceEngine;
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

        // Modul: larder. Optional, matching the _equipmentSlotEngine /
        // _relationshipEngine convention, so the many test fixtures that
        // construct this engine directly keep compiling.
        private readonly LarderEngine? _larderEngine;
        private readonly WorldBossEngine _worldBossEngine;
        private readonly MentorshipEngine _mentorshipEngine;
        private readonly GuildWarEngine _guildWarEngine;
        private readonly ChronoCoreEngine _chronoCoreEngine;
        private readonly LegacyStoreEngine _legacyStoreEngine;
        private readonly GuildLogisticsDepotEngine _guildLogisticsDepotEngine;
        private readonly GuildCombatSimulationEngine _guildCombatSimulationEngine;
        private readonly GuildRaidEngine? _guildRaidEngine;
        private readonly EquipmentSlotEngine? _equipmentSlotEngine;
        private readonly RelationshipEngine? _relationshipEngine;
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

        // Modul: inventory census. The base backpack size before
        // RaceMasteryResolver's Human vault bonus - the same 20 hydration has
        // always used, named once so the census fallback and hydration cannot
        // drift apart.
        public const int DefaultBackpackCapacity = 20;

        // Modul: crafting as an assignable job. The 10Hz tick counts the time
        // and this queue carries the finished craft out to CraftingEngine,
        // which owns the transaction. Same shape as CombatLootEngine's drop
        // queue: no DB work, no allocation and no await on the hot path.
        public static readonly System.Collections.Concurrent.ConcurrentQueue<CraftTickCompletion> CraftingTickQueue = new();


        // Modul: warp equipment drops used to be bounded by free backpack
        // slots. With unlimited storage that bound is gone, so an explicit one
        // replaces it - this only stops a very long warp from flooding
        // CombatLootEngine's queue in a single resolve, it is not a cap on
        // what the player keeps.
        public const int MaxWarpEquipmentDropsPerResolve = 500;

        // A craft can never be faster than a fifth of a second, however cheap
        // its recipe - the tick counts in tenths and a zero would make the
        // completion branch fire on every single tick.
        public const int MinCraftTicks = 2;

        // A character out of combat refills from empty in this many seconds.
        // Long enough that dying still costs real time, short enough that it
        // is not a reason to close the game.
        public const int BaselineOutOfCombatRegenSeconds = 120;

        public static int ActiveGlobalEventId { get; private set; }

        public SimulationEngine(LootTableEngine lootEngine, StateCheckpointManager checkpointManager, NetworkBroadcastSystem networkSystem, ForgeSplicingEngine forgeEngine, MarketOrderBookEngine marketEngine, PlayerSessionRegistry playerRegistry, GuildContributionEngine guildEngine, MarketEscrowEngine escrowEngine, MailboxAndBankEngine mailboxEngine, AffixRerollEngine rerollEngine, BreedingEngine breedingEngine, GuildLogisticsEngine guildLogisticsEngine, CraftingEngine craftingEngine, WorldBossEngine worldBossEngine, VillageBuildingEngine villageBuildingEngine, VillageManagementEngine villageManagementEngine, MentorshipEngine mentorshipEngine, GuildWarEngine guildWarEngine, ChronoCoreEngine chronoCoreEngine, LegacyStoreEngine legacyStoreEngine, GuildLogisticsDepotEngine guildLogisticsDepotEngine, GuildCombatSimulationEngine guildCombatSimulationEngine, AntiCheatTelemetryEngine antiCheatTelemetryEngine, PushNotificationTriggerEngine pushNotificationTriggerEngine, CompliancePurgeEngine compliancePurgeEngine, BillingVerificationEngine billingVerificationEngine, StackExchange.Redis.IConnectionMultiplexer redis, Microsoft.EntityFrameworkCore.IDbContextFactory<FolkIdleDbContext> contextFactory, GuildRaidEngine? guildRaidEngine = null, EquipmentSlotEngine? equipmentSlotEngine = null, RelationshipEngine? relationshipEngine = null, LarderEngine? larderEngine = null, InheritanceEngine? inheritanceEngine = null)
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
            _larderEngine = larderEngine;
            _inheritanceEngine = inheritanceEngine;
            _worldBossEngine = worldBossEngine;
            _mentorshipEngine = mentorshipEngine;
            _guildWarEngine = guildWarEngine;
            _chronoCoreEngine = chronoCoreEngine;
            _legacyStoreEngine = legacyStoreEngine;
            _guildLogisticsDepotEngine = guildLogisticsDepotEngine;
            _guildCombatSimulationEngine = guildCombatSimulationEngine;
            _guildRaidEngine = guildRaidEngine;
            _equipmentSlotEngine = equipmentSlotEngine;
            _relationshipEngine = relationshipEngine;
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

        // Modul: Deploy activation fix. Test-only observability for the
        // ActivityChangeQueue drain - the live payload is tick-thread owned,
        // so a test cannot read it directly and must poll through here,
        // tolerating a tick's worth of latency.
        internal long GetActivePlayerActiveActivityId(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.ActiveActivityId : -1L;
            }
        }

        internal int GetActivePlayerCurrentMonsterId(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.CurrentMonsterId : -1;
            }
        }

        // Modul: Guild War scoreboard sync. Test-only observability for the
        // GuildWarScoreboardQueue drain, same tick-thread-ownership reason as
        // the two hooks above.
        internal int GetActivePlayerGuildCombatVanguardPoints(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.GuildCombatVanguardPoints : -1;
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

        // Test-only observability (via InternalsVisibleTo) for the
        // Full-Stack Production Hardening Phase 3, Part 1 session-registry
        // leak fix - proves RemoveActivePlayer actually clears
        // _liveSessionContexts on every disconnect path, not just
        // TerminateSessionForSecurity's old explicit call.
        internal bool IsLiveSessionContextPresent(long playerId)
        {
            return _liveSessionContexts.ContainsKey(playerId);
        }

        // Test-only observability (via InternalsVisibleTo) for the Part 5
        // command-result ring buffer - returns all 4 slots (not just the
        // newest, unlike GetActivePlayerLastCommandResultCode) so a test
        // can assert every buffered rejection survived, in what order,
        // and which slot a wraparound append overwrote. Allocates a small
        // array - fine here since this is test-only diagnostic code, never
        // reachable from the 10Hz tick hot path itself.
        internal (byte code, uint tick)[] GetActivePlayerCommandResultSlots(long playerId)
        {
            lock (_activePlayers)
            {
                if (!_activePlayers.TryGetValue(playerId, out var payload))
                {
                    return Array.Empty<(byte, uint)>();
                }

                return new (byte, uint)[]
                {
                    (payload.CommandResultSlot0.ResultCode, payload.CommandResultSlot0.ResultTick),
                    (payload.CommandResultSlot1.ResultCode, payload.CommandResultSlot1.ResultTick),
                    (payload.CommandResultSlot2.ResultCode, payload.CommandResultSlot2.ResultTick),
                    (payload.CommandResultSlot3.ResultCode, payload.CommandResultSlot3.ResultTick)
                };
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
                if (!_activePlayers.TryGetValue(playerId, out var payload))
                {
                    return -1;
                }

                // Modul: reports whichever of the 4 ring-buffer slots holds
                // the highest ResultTick (the most recently appended entry) -
                // preserves this helper's original "most recent rejection"
                // semantics now that a scalar no longer exists.
                CommandResultEntry newest = payload.CommandResultSlot0;
                if (payload.CommandResultSlot1.ResultTick > newest.ResultTick) newest = payload.CommandResultSlot1;
                if (payload.CommandResultSlot2.ResultTick > newest.ResultTick) newest = payload.CommandResultSlot2;
                if (payload.CommandResultSlot3.ResultTick > newest.ResultTick) newest = payload.CommandResultSlot3;
                return newest.ResultCode;
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
        //
        // Modul: Full-Stack Production Hardening Phase 3, Part 1. Also the
        // single authoritative choke point for _liveSessionContexts and
        // PlayerSessionRegistry cleanup - previously only
        // TerminateSessionForSecurity cleared _liveSessionContexts, so a
        // normal Logout leaked one entry per session forever (unbounded
        // growth with total historical logins, not concurrent players).
        // Worse, every one of the ~21 anti-cheat/validation-failure sites
        // called RemoveActivePlayer directly (never TerminateSessionForSecurity),
        // which removed the _activePlayers entry immediately - so the
        // deferred Logout command NetworkBroadcastSystem's socket-closure
        // finally block enqueues afterward was silently dropped by the
        // command loop's null-ref guard (_activePlayers no longer has the
        // entry), meaning _playerRegistry.UnregisterPlayer never ran for a
        // kicked player and PlayerSessionRegistry.IsPlayerOnline reported
        // them online forever. Folding both removals in here, on the same
        // O(1) ConcurrentDictionary operations, closes every disconnect
        // path (clean logout, anti-cheat kick, crash-detected-via-Logout)
        // through one place - no call site can bypass it.
        private void RemoveActivePlayer(long playerId)
        {
            if (_activePlayers.TryGetValue(playerId, out var payload))
            {
                RemoveFromGuildIndex(payload.GuildId, playerId);
            }
            _activePlayers.Remove(playerId);
            _liveSessionContexts.TryRemove(playerId, out _);
            _playerRegistry.UnregisterPlayer(playerId);

            // Modul: broadcast dirty-checking. Folded in here for exactly the
            // reason this method exists: the last-sent-packet cache is one more
            // per-session structure that would otherwise grow with total
            // historical logins rather than concurrent players. Dropping it
            // also guarantees a reconnecting client gets a full snapshot
            // immediately instead of being compared against state it no longer
            // holds.
            RemoveBroadcastCacheEntry(playerId);

            // Modul: anti-cheat false positive. Command-timing profiles used to
            // outlive the session that produced them, so the ring buffer mixed
            // this session's cadence with the last one's and treated the offline
            // gap between them as a normal interval.
            _antiCheatTelemetryEngine?.ForgetPlayer(playerId);
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
            // Modul: _liveSessionContexts/_playerRegistry cleanup now lives
            // inside RemoveActivePlayer itself (see that method's own doc
            // comment) - this method only adds the token purge on top of
            // the same choke point every other kick/disconnect site uses.
            RemoveActivePlayer(playerId);
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
                        currentPayload.IsDirty = true;
                    }
                    else if (notification.GoldDelta != 0L)
                    {
                        // Modul: market settlement rescue, 2026-08-01.
                        //
                        // MarketEscrowEngine chooses between crediting the
                        // database directly and posting here, based on whether
                        // the seller was online AT THAT MOMENT. If they logged
                        // out between that check and this drain - a window of up
                        // to one tick plus the escrow transaction's tail - this
                        // used to dequeue the notification, find no payload, and
                        // silently drop it. The database was never credited on
                        // that path, so the seller permanently lost the proceeds
                        // of a completed sale with no error and no telemetry.
                        //
                        // Falling back to the offline path closes it. Crediting
                        // the row directly is safe precisely because the player
                        // is NOT active: nothing holds a live CurrentGold that
                        // this could race, and hydration reads this row at their
                        // next login.
                        long rescuePlayerId = notification.PlayerId;
                        long rescueGold = notification.GoldDelta;

                        SafeDispatchAsync("Market.SettlementRescue", 0L, async () =>
                        {
                            await using var rescueDb = await _contextFactory.CreateDbContextAsync();

                            var goldRow = await rescueDb.CommodityRecords
                                .FirstOrDefaultAsync(c => c.PlayerId == rescuePlayerId && c.ItemId == "gold");

                            if (goldRow == null)
                            {
                                rescueDb.CommodityRecords.Add(new Models.CommodityRecord
                                {
                                    PlayerId = rescuePlayerId,
                                    ItemId = "gold",
                                    Quantity = rescueGold
                                });
                            }
                            else
                            {
                                goldRow.Quantity += rescueGold;
                            }

                            await rescueDb.SaveChangesAsync();
                        });
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
                        // Modul: per-character equipment. Equipment used to be
                        // account-wide, so this could write straight into the
                        // payload. Now it has to land on the slot that owns the
                        // character - otherwise equipping a helmet on the miner
                        // would re-stat the swordsman. Same register discipline
                        // the tick loop uses: load the owning slot, apply, put it
                        // back.
                        int equipSlotIndex = ResolveSlotIndexForCharacter(ref currentPayload, equipUpdate.CharacterId);
                        if (equipSlotIndex < 0)
                        {
                            continue;
                        }

                        SwapSlotIntoActiveRegister(ref currentPayload, equipSlotIndex);

                        currentPayload.EquippedWeaponId = equipUpdate.EquippedWeaponId;
                        currentPayload.EquippedHelmetId = equipUpdate.EquippedHelmetId;
                        currentPayload.EquippedArmorId = equipUpdate.EquippedChestId;
                        currentPayload.EquippedGlovesId = equipUpdate.EquippedGlovesId;
                        currentPayload.EquippedLeggingsId = equipUpdate.EquippedLeggingsId;
                        currentPayload.EquippedBootsId = equipUpdate.EquippedBootsId;
                        currentPayload.EquippedAmuletId = equipUpdate.EquippedAmuletId;
                        currentPayload.EquippedRingId = equipUpdate.EquippedRingId;
                        currentPayload.AxeToolTier = equipUpdate.AxeToolTier;
                        currentPayload.PickaxeToolTier = equipUpdate.PickaxeToolTier;
                        currentPayload.RodToolTier = equipUpdate.RodToolTier;
                        currentPayload.ToolGatherSpeedPct = equipUpdate.ToolGatherSpeedPct;
                        currentPayload.ToolGatherYieldPct = equipUpdate.ToolGatherYieldPct;
                        currentPayload.ToolRareFindPct = equipUpdate.ToolRareFindPct;
                        currentPayload.CachedAffixTotals = equipUpdate.AffixTotals;
                        currentPayload.CachedSetIds = equipUpdate.SetIds;

                        SwapSlotIntoActiveRegister(ref currentPayload, equipSlotIndex);

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

                // Modul: race unlock feedback. ORs the newly granted race into
                // the live mask so the next outbound packet carries it and the
                // client can announce it. An offline player needs nothing here:
                // the row is already committed and login hydrates the mask from
                // it.
                while (_playerRegistry.RaceUnlockQueue.TryDequeue(out var raceUnlock))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, raceUnlock.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        if (raceUnlock.RaceId >= 1 && raceUnlock.RaceId <= 8)
                        {
                            currentPayload.UnlockedRaceBitmask |= (byte)(1 << (raceUnlock.RaceId - 1));
                            currentPayload.IsDirty = true;
                        }
                    }
                }

                // Modul: Deploy activation fix, generalised for multi-slot.
                // Applies a committed activity change to the live payload.
                //
                // This used to skip any change whose character was not Slot1,
                // on the (then true) grounds that the payload modelled exactly
                // one running activity. That made assigning a second or third
                // character a no-op for the whole session: the row was written,
                // the queue entry was dropped here, and nothing ever ran. Now
                // that every unlocked slot is simulated, the change is routed
                // to whichever slot owns the character.
                while (_playerRegistry.ActivityChangeQueue.TryDequeue(out var activityChange))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, activityChange.PlayerId);
                    if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        continue;
                    }

                    int targetSlotIndex = ResolveSlotIndexForCharacter(ref currentPayload, activityChange.CharacterId);
                    if (targetSlotIndex < 0)
                    {
                        continue;
                    }

                    // Load the owning slot, apply, put it back - the same
                    // register discipline the tick loop uses, so a change to
                    // slot 3 cannot clobber slot 1's fight.
                    SwapSlotIntoActiveRegister(ref currentPayload, targetSlotIndex);
                    ApplyActivityChangeToPayload(ref currentPayload, activityChange.TargetActivityId);
                    SwapSlotIntoActiveRegister(ref currentPayload, targetSlotIndex);
                }

                // Modul: larder. LarderEngine has already committed the slot to
                // PlayerRecords; this is the hand-off that makes it live for the
                // running session, so restocking mid-fight takes effect on the
                // next tick rather than at the next login.
                // Modul: crafting as an assignable job. Drained next to every
                // other cross-engine queue, so a finished craft costs the tick
                // one dequeue and CraftingEngine does the rest off the hot
                // path.
                while (CraftingTickQueue.TryDequeue(out var craftCompletion))
                {
                    long craftPlayerId = craftCompletion.PlayerId;
                    int craftResultItemId = craftCompletion.ResultItemId;
                    SafeDispatchAsync("Crafting.Job", craftPlayerId, async () => {
                        await _craftingEngine.ExecuteCraftingAsync(craftPlayerId, craftResultItemId);
                    });
                }

                while (_playerRegistry.LarderSlotUpdateQueue.TryDequeue(out var larderUpdate))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, larderUpdate.PlayerId);
                    if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        continue;
                    }

                    switch (larderUpdate.SlotIndex)
                    {
                        case 0:
                            currentPayload.Food1_ItemId = larderUpdate.ItemId;
                            currentPayload.Food1_Count = larderUpdate.Count;
                            break;
                        case 1:
                            currentPayload.Food2_ItemId = larderUpdate.ItemId;
                            currentPayload.Food2_Count = larderUpdate.Count;
                            break;
                        case 2:
                            currentPayload.Food3_ItemId = larderUpdate.ItemId;
                            currentPayload.Food3_Count = larderUpdate.Count;
                            break;
                    }

                    // Modul: halt reasons. Stocking food is the direct answer to
                    // an OutOfFood halt, so clear the banner as soon as there is
                    // something to eat. The activity itself still needs
                    // redeploying - only the player can decide that - so this
                    // does not restart it.
                    if (currentPayload.ActivityHaltReason == Network.ActivityHaltReason.OutOfFood && larderUpdate.Count > 0)
                    {
                        currentPayload.ActivityHaltReason = Network.ActivityHaltReason.None;
                    }

                    currentPayload.IsDirty = true;
                }

                // Modul: Guild War scoreboard sync. Fans one authoritative
                // per-guild snapshot out to every online member of that guild
                // via the tick-thread-owned guild index, so a scoreboard costs
                // one query per warring guild rather than one per member.
                while (_playerRegistry.GuildWarScoreboardQueue.TryDequeue(out var warScoreboard))
                {
                    if (!_guildMembersIndex.TryGetValue(warScoreboard.GuildId, out var warMembers))
                    {
                        continue;
                    }

                    for (int memberIndex = 0; memberIndex < warMembers.Count; memberIndex++)
                    {
                        ref var memberPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, warMembers[memberIndex]);
                        if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref memberPayload))
                        {
                            continue;
                        }

                        // Modul: Guild War scoreboard sync. A concluded war
                        // clears rather than freezing its final score on screen.
                        // The client decides "a war is on" from
                        // ActiveGuildWarId > 0, so that has to go to zero too or
                        // the panel keeps rendering a finished match as live.
                        if (warScoreboard.WarEnded)
                        {
                            memberPayload.ActiveGuildWarId = 0L;
                            memberPayload.GuildCombatVanguardPoints = 0;
                            memberPayload.GuildProductionLogisticsPoints = 0;
                            memberPayload.GuildGatheringSupplyChainPoints = 0;
                            memberPayload.EnemyCombatVanguardPoints = 0;
                            memberPayload.EnemyProductionLogisticsPoints = 0;
                            memberPayload.EnemyGatheringSupplyChainPoints = 0;
                            memberPayload.CachedWarMultiplier = 0f;
                            memberPayload.IsDirty = true;
                            continue;
                        }

                        memberPayload.GuildCombatVanguardPoints = warScoreboard.OurCombatVanguardPoints;
                        memberPayload.GuildProductionLogisticsPoints = warScoreboard.OurProductionLogisticsPoints;
                        memberPayload.GuildGatheringSupplyChainPoints = warScoreboard.OurGatheringSupplyChainPoints;
                        memberPayload.EnemyCombatVanguardPoints = warScoreboard.EnemyCombatVanguardPoints;
                        memberPayload.EnemyProductionLogisticsPoints = warScoreboard.EnemyProductionLogisticsPoints;
                        memberPayload.EnemyGatheringSupplyChainPoints = warScoreboard.EnemyGatheringSupplyChainPoints;
                        memberPayload.CachedWarMultiplier = warScoreboard.ScoreShare;
                        memberPayload.IsDirty = true;
                    }
                }

                // Modul: THE FULL-BACKPACK DEAD END.
                //
                // InventorySpaceRemaining was refreshed from exactly two
                // places: the session load, and a loot drop carrying a census.
                // ProcessSubTick's first line returns when it is 0, so a full
                // backpack stopped combat, which stopped loot, which stopped
                // the only thing that could recount - while depositing to the
                // bank, claiming mail or selling on the market all changed the
                // database without touching the live payload. The player freed
                // slots, watched the number stay at 0, and had no way back
                // short of reconnecting.
                //
                // Found by driving the real UI: the dev fixture at 20/20
                // accepted ChangeActivity, set ActiveActivityId to 91, and
                // CurrentMonsterId never left 0.
                while (_playerRegistry.InventoryCensusQueue.TryDequeue(out var census))
                {
                    ref var censusPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, census.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref censusPayload))
                    {
                        // Modul: VESTIGIAL SINCE THE BACKPACK WAS REMOVED.
                        //
                        // Materials go to the unbounded village chest and
                        // equipment to the bank or to scrap, so there is no
                        // per-character carrying capacity left to run out of.
                        // The field survives because a dozen gathering and
                        // loot loops still decrement it defensively and the
                        // wire still carries it; reporting full capacity keeps
                        // every one of those a no-op without touching them, and
                        // keeps the packet layout unchanged.
                        //
                        // It is deliberately NOT deleted in the same pass that
                        // changed the loot routing - one behaviour change at a
                        // time, and the layout guard pins this packet's size.
                        int capacity = censusPayload.InventoryCapacity > 0 ? censusPayload.InventoryCapacity : DefaultBackpackCapacity;
                        censusPayload.InventorySpaceRemaining = capacity;

                        // Clearing the halt here as well as recomputing the
                        // number: the tick that follows only clears it when an
                        // activity is running, and a player who just made room
                        // should not keep reading "everything is stopped"
                        // until the next kill lands.
                        if (censusPayload.InventorySpaceRemaining > 0 &&
                            censusPayload.ActivityHaltReason == Network.ActivityHaltReason.InventoryFull)
                        {
                            censusPayload.ActivityHaltReason = Network.ActivityHaltReason.None;
                        }

                        censusPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.CombatLootDropQueue.TryDequeue(out var combatLootDrop))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, combatLootDrop.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        // Modul: inventory census. Assign from the truth when
                        // the loot engine sent one. The decrement below is only
                        // the fallback for a notification with no census (the
                        // no-loot-table early-out path), and even then the next
                        // real kill corrects it - the old behaviour was decrement
                        // only, forever, which made every backpack read as full
                        // after 20 kills and silently discarded all loot for the
                        // rest of the session.
                        // Modul: the backpack is gone. Storage is one
                        // unlimited village chest, so this counter is pinned
                        // at capacity and gates nothing. It used to be
                        // `capacity - OccupiedSlots`, and OccupiedSlots now
                        // counts the CHEST - an unbounded number measured
                        // against a 20 slot ceiling. Twenty stacks in and
                        // every player was permanently "full": gathering
                        // dropped nothing, and the halt banner said
                        // EVERYTHING IS STOPPED.
                        currentPayload.InventorySpaceRemaining =
                            currentPayload.InventoryCapacity > 0 ? currentPayload.InventoryCapacity : DefaultBackpackCapacity;
                        currentPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.ShardAttackResultQueue.TryDequeue(out var shardAttackResult))
                {
                    // Security statuses are resolved here rather than in the
                    // dispatching lambda - see the SubmitShardAttack handler.
                    if (shardAttackResult.ProcessingStatus == 1U
                        || shardAttackResult.ProcessingStatus == 2U
                        || shardAttackResult.ProcessingStatus == 4U)
                    {
                        TelemetryStreamer.TryWrite(new TelemetryEvent
                        {
                            PlayerId = shardAttackResult.PlayerId,
                            EventType = 3,
                            Value1 = 50,
                            Value2 = (int)shardAttackResult.ProcessingStatus,
                            Timestamp = Environment.TickCount64
                        });
                        TerminateSessionForSecurity(shardAttackResult.PlayerId);
                        continue;
                    }

                    if (shardAttackResult.ProcessingStatus != 0U)
                    {
                        continue;
                    }

                    ref var shardPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, shardAttackResult.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref shardPayload))
                    {
                        shardPayload.ActiveCrossShardMatchId = shardAttackResult.MatchUuid;
                        shardPayload.GlobalNodeRemainingHp = shardAttackResult.GlobalNodeRemainingHp;
                        shardPayload.ActiveMatchMmr = shardAttackResult.ActiveMatchMmr;
                        shardPayload.IsDirty = true;
                    }
                }

                while (_playerRegistry.CraftingCompletionQueue.TryDequeue(out var craftCompletion))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, craftCompletion.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        QuestEngine.IncrementProgress(ref currentPayload, QuestEngine.QuestTypeCraftItems, 1);

                        // Mirrors the increment CraftingEngine already committed
                        // to PlayerRecords, so the wire counter tracks the craft
                        // instead of standing at its login value all session.
                        currentPayload.LifetimeItemsCrafted += craftCompletion.Quantity;

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
                //
                // Modul: Full-Stack Production Hardening Phase 3, Part 5.
                // Appends into the 4-slot ring buffer instead of
                // overwriting a single scalar - the previous single-slot
                // design meant a client that missed one broadcast (e.g.
                // across a reconnect gap) while two or more commands were
                // rejected back to back would only ever see the last one,
                // silently losing the earlier rejection's feedback. The
                // ring-buffer append itself must happen here on the tick
                // thread (not inside PlayerSessionRegistry.EnqueueCommandResult,
                // which runs on arbitrary background SafeDispatchAsync
                // threads and has no safe ref access to TickStatePayload) -
                // CommandResultTickCounter is a per-player monotonically
                // increasing counter, never reset, so the client can always
                // tell which slots are newer than what it has already
                // displayed and in what order to apply them.
                while (_playerRegistry.CommandResultQueue.TryDequeue(out var commandResult))
                {
                    ref var resultPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, commandResult.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref resultPayload))
                    {
                        unchecked { resultPayload.CommandResultTickCounter++; }
                        var newEntry = new CommandResultEntry { ResultCode = commandResult.ResultCode, ResultTick = resultPayload.CommandResultTickCounter };
                        switch (resultPayload.CommandResultRingWriteIndex)
                        {
                            case 0: resultPayload.CommandResultSlot0 = newEntry; break;
                            case 1: resultPayload.CommandResultSlot1 = newEntry; break;
                            case 2: resultPayload.CommandResultSlot2 = newEntry; break;
                            default: resultPayload.CommandResultSlot3 = newEntry; break;
                        }
                        resultPayload.CommandResultRingWriteIndex = (byte)((resultPayload.CommandResultRingWriteIndex + 1) & 3);
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
                        currentPayload.TownHallLevel = updateNotif.TownHallLevel;
                        currentPayload.CraftingWorkshopLevel = updateNotif.CraftingWorkshopLevel;
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
                    else if (chronoNotif.SecondsToAdd > 0.0)
                    {
                        // Modul: chrono grant rescue, 2026-08-01.
                        //
                        // ChronoCoreEngine consumes the core and COMMITS before
                        // posting here, so dropping this notification means the
                        // player paid an item and received nothing. Unlike the
                        // snapshot-style notifications around it - which merely
                        // mirror a value the database already holds - this one
                        // carries a DELTA, and a dropped delta is destroyed
                        // value rather than a stale display.
                        //
                        // Latent rather than live today: command 24 is the only
                        // producer and no Chrono Core item exists in the
                        // catalogue yet, so this cannot currently fire. Fixed
                        // now because it becomes live the moment that content is
                        // authored, and nothing about authoring an item would
                        // prompt anyone to re-examine this drain.
                        long chronoPlayerId = chronoNotif.PlayerId;
                        double chronoSeconds = chronoNotif.SecondsToAdd;

                        SafeDispatchAsync("Chrono.GrantRescue", 0L, async () =>
                        {
                            await using var chronoDb = await _contextFactory.CreateDbContextAsync();

                            var chronoOwner = await chronoDb.PlayerRecords
                                .FirstOrDefaultAsync(p => p.Id == chronoPlayerId);

                            if (chronoOwner == null) return;

                            double rescued = chronoOwner.BankedChronoSeconds + chronoSeconds;
                            if (rescued > ChronoBufferEngine.MaxBankedChronoSeconds)
                            {
                                rescued = ChronoBufferEngine.MaxBankedChronoSeconds;
                            }

                            chronoOwner.BankedChronoSeconds = rescued;
                            await chronoDb.SaveChangesAsync();
                        });
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

                while (_playerRegistry.InheritanceSyncQueue.TryDequeue(out var inheritNotif))
                {
                    ref var inheritPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, inheritNotif.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref inheritPayload))
                    {
                        SetInheritanceLevel(ref inheritPayload, inheritNotif.StatId, inheritNotif.NewLevel);
                        inheritPayload.IsDirty = true;
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
                        {
                            currentPayload.AddGold(req.GoldAttachment);
                            currentPayload.IsDirty = true;
                            SafeDispatchAsync("MailClaim.Accept", req.PlayerId, async () => { await _mailboxEngine.CommitMailClaimAsync(req.PlayerId, req.MailId, true); });
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
                            _playerRegistry.EnqueueCommandResult(req.PlayerId, (byte)CommandResultCode.InventoryFull);
                            SafeDispatchAsync("BankWithdraw.Reject", req.PlayerId, async () => { await _mailboxEngine.CommitBankWithdrawAsync(req.PlayerId, req.BankId, false); });
                        }
                        else
                        {
                            currentPayload.InventorySpaceRemaining--;
                            currentPayload.IsDirty = true;
                            SafeDispatchAsync("BankWithdraw.Accept", req.PlayerId, async () => { await _mailboxEngine.CommitBankWithdrawAsync(req.PlayerId, req.BankId, true); });
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

                                // Modul: Deferred Part 5 Implementation,
                                // Part 2. Food/potion/Death Ward item ids
                                // apply their buff slots directly on the
                                // tick-thread payload here (command-time
                                // string classification, per-tick effects
                                // stay pure int); non-consumable ids fall
                                // through to the legacy status-effect
                                // dispatch below unchanged.
                                if (ConsumableEngine.TryApplyConsumable(ref payload, (int)itemId))
                                {
                                    continue;
                                }
                            }

                            SafeDispatchAsync("ConsumeConsumable", pId, async () => {
                                using var context = await _contextFactory.CreateDbContextAsync();
                                using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                                try
                                {
                                    // Identifiers must be double-quoted: the table is
                                    // "EquipmentInstances", and an unquoted identifier folds
                                    // to lowercase in Postgres, so this queried a relation
                                    // that does not exist. itemId is also bound as an int
                                    // rather than via ToString(), which compared text against
                                    // an integer column. Either fault threw, and this
                                    // lambda's failure handler force-disconnects the player.
                                    int consumableBaseItemId = (int)itemId;
                                    var items = await context.EquipmentInstances.FromSqlInterpolated($"SELECT * FROM \"EquipmentInstances\" WHERE \"PlayerId\" = {pId} AND \"BaseItemId\" = {consumableBaseItemId} FOR UPDATE").ToListAsync();
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
                        _antiCheatTelemetryEngine?.RequestShadowBan(routingPlayerId, 54, 2);
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
                            _antiCheatTelemetryEngine?.RequestShadowBan(routingPlayerId, 54, 3);
                        }
                        else
                        {
                            currentPayload.ActiveChallengeAnswered = 1;
                            currentPayload.ActiveChallengeSeed = 0;

                            // Modul: challenge response policy. Any answered
                            // challenge clears the run, so misses have to be
                            // consecutive to escalate.
                            currentPayload.ConsecutiveChallengeMisses = 0;
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
                        // The chest is unlimited, so a buy always has room.
                        bool hasSpace = true;

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

                        // Modul: gathering is locked to where you have been.
                        // A node in the Abyssal Breach is not workable by a
                        // character who has never fought there - that is what
                        // made the five locations decoration. Refused with a
                        // result code rather than a disconnect: this is state,
                        // not a malformed command.
                        int requestedLocation = ContentRegistry.GetNodeLocation(cmd.TargetId);
                        if (requestedLocation > currentPayload.HighestLocationReached)
                        {
                            _playerRegistry.EnqueueCommandResult(routingPlayerId, (byte)CommandResultCode.LocationLocked);
                            continue;
                        }

                        // Modul: region progression. A region opens when the
                        // previous region's boss falls, and until then its
                        // monsters cannot be picked at all. Nothing used to
                        // stop this: a level-4 character could target Malakor,
                        // and the only thing in the way was the fight itself.
                        //
                        // Gated here rather than deeper because this is the one
                        // place a target is CHOSEN. The kill path raises
                        // HighestLocationReached, so leaving entry open would
                        // have let a player unlock every gathering node in the
                        // game by landing one lucky hit in region 5.
                        //
                        // Only combat-band ids are checked, and only canonical
                        // ones: the 90 legacy monsters carry RegionTiers 1-10
                        // that are not this progression, and refusing them here
                        // would lock away content that was never behind a boss.
                        if (cmd.TargetId >= ActivityIdBands.CombatFirst && cmd.TargetId <= ActivityIdBands.CombatLast)
                        {
                            int targetRegion = ContentRegistry.GetCanonicalLocation((int)cmd.TargetId);
                            if (targetRegion > 0 && targetRegion > currentPayload.HighestUnlockedRegion)
                            {
                                _playerRegistry.EnqueueCommandResult(routingPlayerId, (byte)CommandResultCode.RegionLocked);
                                continue;
                            }
                        }

                        // Modul: Architecture Overhaul, Part 2. A non-empty
                        // TargetGuid selects which of the player's up to 3
                        // characters is changing activity; the slot-level
                        // gate and cross-character occupancy mutex are only
                        // meaningful once a specific character is named, so
                        // legacy/single-character requests (TargetGuid ==
                        // Guid.Empty) keep applying straight to the live
                        // session payload exactly as before.
                        if (cmd.TargetGuid != Guid.Empty)
                        {
                            long pId = currentPayload.PlayerId;
                            Guid characterId = cmd.TargetGuid;
                            long targetActivityId = cmd.TargetId;

                            SafeDispatchAsync("Character.ChangeActivity", pId, async () => {
                                var resultCode = await ChangeCharacterActivityAsync(pId, characterId, targetActivityId);
                                _playerRegistry?.EnqueueCommandResult(pId, (byte)resultCode);
                                if (resultCode == Network.CommandResultCode.Success)
                                {
                                    // Modul: Deploy activation fix. This used to
                                    // enqueue only a ReloadState, which does not
                                    // reload anything - it clears IsSuspended and
                                    // nothing else. So the new activity was
                                    // written to the characters row correctly and
                                    // then ignored by the live session for the
                                    // rest of the connection: pressing Deploy
                                    // appeared to work and combat never started.
                                    // Hand the change to the tick thread, which
                                    // owns the payload, and let it apply the same
                                    // live mutation the single-character branch
                                    // below already performs.
                                    _playerRegistry?.ActivityChangeQueue.Enqueue(new ActivityChangeNotification
                                    {
                                        PlayerId = pId,
                                        CharacterId = characterId,
                                        TargetActivityId = targetActivityId
                                    });
                                }
                            });
                        }
                        else
                        {
                            ApplyActivityChangeToPayload(ref currentPayload, cmd.TargetId);
                        }
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
                    else if (cmd.Command == CommandType.AddFriend)
                    {
                        long pId = currentPayload.PlayerId;
                        long targetId = cmd.TargetPlayerId;
                        if (_relationshipEngine != null)
                        {
                            SafeDispatchAsync("Relationship.AddFriend", pId, async () => {
                                await _relationshipEngine.AddFriendAsync(pId, targetId);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.RemoveFriend)
                    {
                        long pId = currentPayload.PlayerId;
                        long targetId = cmd.TargetPlayerId;
                        if (_relationshipEngine != null)
                        {
                            SafeDispatchAsync("Relationship.RemoveFriend", pId, async () => {
                                await _relationshipEngine.RemoveFriendAsync(pId, targetId);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.BlockPlayer)
                    {
                        long pId = currentPayload.PlayerId;
                        long targetId = cmd.TargetPlayerId;
                        if (_relationshipEngine != null)
                        {
                            SafeDispatchAsync("Relationship.BlockPlayer", pId, async () => {
                                await _relationshipEngine.BlockPlayerAsync(pId, targetId);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.UnblockPlayer)
                    {
                        long pId = currentPayload.PlayerId;
                        long targetId = cmd.TargetPlayerId;
                        if (_relationshipEngine != null)
                        {
                            SafeDispatchAsync("Relationship.UnblockPlayer", pId, async () => {
                                await _relationshipEngine.UnblockPlayerAsync(pId, targetId);
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

                        // Modul: reroll operations, 2026-08-01. Everything below
                        // is copied off the command struct BEFORE the lambda, so
                        // the closure never captures `cmd` - it is a ref-local
                        // over tick-owned memory that will have been reused by
                        // the time the continuation runs.
                        var rerollOperation = (Engine.RerollOperation)cmd.RerollOperationKind;
                        uint autoMaxAttempts = cmd.RerollAutoMaxAttempts;
                        byte stopMinRarity = cmd.RerollStopMinRarity;
                        byte stopAffixIndex = cmd.RerollStopAffixIndex;

                        SafeDispatchAsync("Affix.Reroll", pId, async () => {
                            if (autoMaxAttempts == 0U)
                            {
                                await _rerollEngine.ExecuteRerollAsync(pId, cTargetId, affixIndex, rerollOperation);
                            }
                            else
                            {
                                // The affix id is carried as a 1-based index into
                                // AffixRegistry.Definitions rather than a string,
                                // because the packet is fixed-layout. 0 means
                                // "any stat".
                                string? requiredAffixId = null;
                                if (stopAffixIndex > 0 && stopAffixIndex <= Engine.AffixRegistry.Definitions.Length)
                                {
                                    requiredAffixId = Engine.AffixRegistry.Definitions[stopAffixIndex - 1].Id;
                                }

                                var stopCondition = new Engine.AutoRerollStopCondition(
                                    (Engine.AffixRarity)(stopMinRarity < 1 ? 1 : stopMinRarity),
                                    requiredAffixId);

                                // The client's attempt count is a request, not a
                                // bound - AutoRerollPlanner clamps it, because an
                                // unbounded loop of Serializable transactions is a
                                // self-inflicted denial of service.
                                await _rerollEngine.ExecuteAutoRerollAsync(
                                    pId, cTargetId, affixIndex, rerollOperation, stopCondition, (int)autoMaxAttempts);
                            }

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
                    // Modul: CommandType.CraftItem is RETIRED, along with the
                    // equipment recipes it carried. Equipment is monster loot
                    // and tools are crafted - see CraftingEngine. A client
                    // still sending it is an old bundle rather than an attack,
                    // so it is ignored rather than treated as a protocol
                    // violation: disconnecting a stale tab teaches nobody
                    // anything and looks like the game is broken.
                    else if (cmd.Command == CommandType.CraftItem)
                    {
                        // Deliberately empty.
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
                        if (!ClientCommandValidator.ValidatePlaceLimitOrderRequest(ref currentPayload, ref cmd))
                        {
                            RemoveActivePlayer(routingPlayerId);
                            _networkSystem.ForceDisconnect(routingPlayerId);
                            continue;
                        }

                        currentPayload.IsSuspended = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);

                        long pId = currentPayload.PlayerId;
                        bool isBuy = cmd.IsBuy == 1;
                        long instanceId = cmd.TargetId;
                        long price = cmd.LimitPrice;
                        int qualityTier = cmd.QualityTier;
                        // Modul: Play Mode audit fix. This used to synthesize a
                        // bogus "ItemType_{TargetId}" string that never matched
                        // any real MarketEquipmentInstance.BaseItemId - every BUY
                        // limit order placed through the real wire protocol was
                        // permanently unmatchable (only the direct-call unit test
                        // passed a real baseItemId, bypassing this dispatcher
                        // entirely). TargetId is the same numeric ContentRegistry
                        // item id used by ConsumableEngine/CombatLootEngine -
                        // resolving it here is the same GetItemBaseId lookup they
                        // already use, not a new convention.
                        string baseItemId = isBuy ? ContentRegistry.GetItemBaseId((int)cmd.TargetId) : "";

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
                    else if (cmd.Command == CommandType.PurchaseInheritanceLevel)
                    {
                        // Modul: inheritance stats. Dispatched off the tick like
                        // every other DB-transactional command; the balance
                        // check, the deduction and the level write all resolve
                        // in one Serializable FOR UPDATE transaction.
                        //
                        // TargetId carries the stat id, the way the skill
                        // commands carry a skill id on it. The engine validates
                        // the range itself and refuses rather than
                        // disconnecting - a stat id is a menu choice, not a
                        // capability claim, so a stale client asking for stat 9
                        // deserves a rejection and not a kick.
                        long inheritPlayerId = currentPayload.PlayerId;
                        int inheritStatId = (int)cmd.TargetId;
                        SafeDispatchAsync("Inheritance.Purchase", inheritPlayerId, async () => {
                            if (_inheritanceEngine != null) await _inheritanceEngine.PurchaseLevelAsync(inheritPlayerId, inheritStatId);
                        });
                    }
                    else if (cmd.Command == CommandType.PurchaseBattlePass)
                    {
                        // Modul: Comprehensive Game System Audit, Part 4.3.
                        // Premium track unlock via in-game PremiumDiamonds -
                        // dispatched off the tick thread like every other
                        // DB-transactional command; balance check, deduction,
                        // and PremiumUnlocked flag all resolve inside one
                        // Serializable FOR UPDATE transaction server-side.
                        long pId = currentPayload.PlayerId;
                        SafeDispatchAsync("BattlePass.Purchase", pId, async () => {
                            await ExecutePassPurchaseAsync(pId);
                        });
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

                        // Dispatched off-thread like every other database
                        // command. This previously ran as
                        // RegisterGuildDefenseAsync(...).GetAwaiter().GetResult(),
                        // which blocked the 10 Hz tick - for EVERY player - on a
                        // Serializable transaction taking two FOR UPDATE row
                        // locks. UiGuildWarPanel sends this from a button, so any
                        // player could stall the whole simulation for as long as
                        // those locks took to acquire, and blocking the tick
                        // thread while EF holds locks is a deadlock shape as well
                        // as a latency one.
                        //
                        // Safe to fire and forget: it returns nothing and mutates
                        // no payload state, so there is no result to thread back
                        // through a notification queue.
                        long guildDefenseGuildId = currentPayload.GuildId;
                        SafeDispatchAsync("GuildWar.RegisterDefense", currentPayload.PlayerId, async () =>
                        {
                            await RegisterGuildDefenseAsync(guildDefenseGuildId);
                        });
                    }
                    else if (cmd.Command == CommandType.SubmitShardAttack)
                    {
                        if (!ClientCommandValidator.ValidateGuildWarAction(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        // Dispatched off-thread, result threaded back through
                        // ShardAttackResultQueue and applied at the drain below.
                        //
                        // This was the last GetAwaiter().GetResult() in the tick
                        // loop: a cross-shard network round trip executed
                        // synchronously, which would have stalled every player's
                        // simulation on one player's request. It could not follow
                        // the plain fire-and-forget shape the other commands use,
                        // because it writes three fields back into the payload -
                        // and only the tick thread may touch a payload.
                        //
                        // The security-violation statuses (1, 2, 4) are carried
                        // back rather than acted on in the lambda for the same
                        // reason: TerminateSessionForSecurity mutates tick-owned
                        // state, so the drain performs it.
                        long shardPlayerId = currentPayload.PlayerId;
                        long shardGuildId = currentPayload.GuildId;
                        long shardNodeHp = currentPayload.GlobalNodeRemainingHp;
                        System.Guid shardMatchUuid = cmd.TargetMatchUuid;
                        uint shardPredictedDamage = cmd.ClientPredictedDamage;
                        bool shardIsFinalBlow = cmd.IsBuy != 0;

                        SafeDispatchAsync("GuildWar.SubmitShardAttack", shardPlayerId, async () =>
                        {
                            var attackResult = await SubmitShardAttackAsync(
                                shardGuildId,
                                shardNodeHp,
                                shardMatchUuid,
                                shardPredictedDamage,
                                shardIsFinalBlow);

                            _playerRegistry.ShardAttackResultQueue.Enqueue(new ShardAttackResultNotification
                            {
                                PlayerId = shardPlayerId,
                                ProcessingStatus = attackResult.Response.ProcessingStatus,
                                MatchUuid = shardMatchUuid,
                                GlobalNodeRemainingHp = attackResult.Response.GlobalNodeRemainingHp,
                                ActiveMatchMmr = attackResult.ActiveMatchMmr
                            });
                        });
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
                    else if (cmd.Command == CommandType.ContributeGuildTreasury)
                    {
                        if (!ClientCommandValidator.ValidateGuildTreasuryContribution(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        currentPayload.IsSuspended = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);

                        long pId = currentPayload.PlayerId;
                        // Modul: Play Mode audit fix. Previously trusted
                        // cmd.SecondaryId as the target guild id directly -
                        // a player could donate their own gold/equipment
                        // toward ANY guild's tier, not just their own.
                        // Derives from the player's own live GuildId instead,
                        // matching how the materials/Monolith contribution
                        // branch already resolves guild membership.
                        long guildId = currentPayload.GuildId;
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
                        // Modul: per-character equipment. TargetGuid names which
                        // character puts the item on. Guid.Empty - what every
                        // client that predates the roster sends - resolves to the
                        // main character, so old behaviour is preserved exactly.
                        System.Guid equipCharacterId = cmd.TargetGuid;
                        if (equipItemId > 0 && _equipmentSlotEngine != null)
                        {
                            SafeDispatchAsync("Equipment.Equip", equipPlayerId, async () => {
                                await _equipmentSlotEngine.EquipItemAsync(equipPlayerId, equipItemId, equipCharacterId);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.UnequipItem)
                    {
                        long unequipPlayerId = currentPayload.PlayerId;
                        // Modul: per-character equipment. Wire mapping widened
                        // from three slots to six. TargetId now carries the slot
                        // index directly (0 Weapon, 1 Helmet, 2 Chest, 3 Gloves,
                        // 4 Leggings, 5 Boots).
                        //
                        // The one legacy case that must keep working is a client
                        // that predates this and sends TargetId 0 with the old
                        // IsBuy flag meaning weapon(0)/armor(1): TargetId 0 plus
                        // IsBuy set is therefore read as the Chest slot, which is
                        // where the old single "Armor" slot's contents now live.
                        int unequipSlot = cmd.TargetId == 0L && cmd.IsBuy != 0
                            ? EquipmentSlotEngine.SlotChest
                            : (int)cmd.TargetId;
                        System.Guid unequipCharacterId = cmd.TargetGuid;
                        if (_equipmentSlotEngine != null)
                        {
                            SafeDispatchAsync("Equipment.Unequip", unequipPlayerId, async () => {
                                await _equipmentSlotEngine.UnequipItemAsync(unequipPlayerId, unequipSlot, unequipCharacterId);
                            });
                        }
                    }
                    else if (cmd.Command == CommandType.StockFoodSlot)
                    {
                        // Modul: larder. Deliberately does NOT terminate the
                        // session on a bad request. Every field here is
                        // player-chosen from a UI list (which slot, which food,
                        // how many), so a stale client sending a food id that no
                        // longer exists is a mistake to report, not evidence of
                        // tampering - and TerminateSessionForSecurity for a
                        // mis-click is exactly the failure mode that made eating
                        // food force-disconnect players before AlchemyCompendium
                        // was fixed. LarderEngine validates and reports through
                        // the CommandResult ring buffer instead.
                        long larderPlayerId = currentPayload.PlayerId;
                        int larderSlot = (int)cmd.TargetSlotIndex;
                        int larderFoodId = (int)cmd.ConsumableItemId;
                        int larderQuantity = (int)Math.Min(cmd.DepositQuantity, (uint)Network.LarderLimits.SlotCapacity);

                        if (_larderEngine != null)
                        {
                            SafeDispatchAsync("Larder.StockFoodSlot", larderPlayerId, async () => {
                                await _larderEngine.ExecuteStockFoodSlotAsync(larderPlayerId, larderSlot, larderFoodId, larderQuantity);
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

                        // Modul: larder. This used to write the live payload and
                        // nothing else, so a player's chosen auto-eat threshold
                        // was silently discarded at every logout and reverted to
                        // the default on the next login.
                        if (_larderEngine != null)
                        {
                            long thresholdPlayerId = currentPayload.PlayerId;
                            int persistedThreshold = thresholdValue;
                            SafeDispatchAsync("Larder.PersistAutoEatThreshold", thresholdPlayerId, async () => {
                                await _larderEngine.PersistAutoEatThresholdAsync(thresholdPlayerId, persistedThreshold);
                            });
                        }
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
                    // CommandType.RegisterWorldBossDamage (19) was retired here.
                    // It was a second entry point into the same
                    // WorldBossEngine.QueueAttack that AttackWorldBoss already
                    // reaches, but with weaker validation: it took the damage
                    // figure straight out of cmd.TargetId and only clamped it,
                    // where AttackWorldBoss validates the boss instance id, that
                    // the event is live and that the boss is not already dead.
                    // No client path ever sent it, so it was pure attack surface.
                    else if (cmd.Command == CommandType.Logout)
                    {
                        currentPayload.LastLogoutTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        currentPayload.IsDirty = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);
                        currentPayload.IsSuspended = true;
                        // Modul: RemoveActivePlayer now clears
                        // PlayerSessionRegistry registration itself - see
                        // its own doc comment.
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

                        // Modul: Production Release Hardening, Part 1. This
                        // used to build productId as the literal string
                        // "Product_{hash}", which could never match a real
                        // GameBalanceConfig.json key
                        // (ResolvePremiumDiamondsForProduct's dictionary
                        // lookup would always miss, silently resolving to 0
                        // diamonds) - previously broken for every purchase
                        // submitted through this command, a real financial
                        // blocker. TryResolveProductIdFromHash resolves the
                        // client-computed FNV-1a hash back to the real
                        // product id via ContentRegistry's own reverse
                        // lookup table (built once at boot, see
                        // ContentRegistry.Initialize), never throwing on an
                        // unresolved hash.
                        //
                        // Bulletproof fallback: if the hash does not
                        // resolve (a stale client build, a corrupted
                        // packet, or simply hash 0 from an
                        // uninitialized/never-set client field), fall back
                        // to treating transactionId itself as a cleartext
                        // product id - this WebSocket command's only other
                        // string payload - and accept it if it is a real,
                        // known catalog entry. Genuine cryptographic
                        // signed-receipt verification (where a cleartext
                        // product id is extracted from a verified Apple/
                        // Google payload) already exists as a separate,
                        // correct path - VerifyReceiptAsync, reached only
                        // through the REST /api/v1/billing/verify endpoint,
                        // which is the only place a real signed receipt can
                        // actually be carried (this 64-byte WebSocket
                        // packet never could). Neither branch here ever
                        // throws - an unresolved product id simply falls
                        // through to VerifyPurchaseAsync's own existing
                        // premiumAmount <= 0 rejection.
                        if (!ContentRegistry.TryResolveProductIdFromHash(cmd.TargetProductIdHash, out string productId))
                        {
                            productId = transactionId;
                        }

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

                                // Modul: set bonuses made real. The Eternal
                                // Dreadnought 4-piece shortens the cooldown it
                                // stamps here. Derived from the payload's
                                // cached set ids rather than a duplicate flag,
                                // so CachedSetIds stays the single source of
                                // truth for what the character is wearing.
                                int effectiveCooldownMs = castDef.CooldownMs;
                                if (SetBonusEngine.Evaluate(in currentPayload.CachedSetIds).CooldownReductionActive)
                                {
                                    effectiveCooldownMs = (int)(effectiveCooldownMs * (1f - SetCooldownReductionFraction));
                                }

                                ActiveSkillEngine.SetSkillCooldownExpiresAtMs(ref currentPayload, castSkillId, nowMs + effectiveCooldownMs);
                                float statusSynergyMultiplier = ActiveSkillEngine.ApplyStatusSynergy(ref currentPayload, castSkillId);
                                currentPayload.PendingSkillDamageMultiplier = (castDef.DamageMultiplierPct / 100f) * statusSynergyMultiplier;
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
                        // Modul: challenge response policy. This used to
                        // quarantine on the FIRST challenge that went unanswered
                        // for 500ms - see AntiCheatTelemetryEngine for why that
                        // was a latency detector rather than a cheat detector.
                        // A miss now only advances a counter; the account is
                        // only quarantined once the client has failed a run of
                        // them, which a client that cannot compute the hash
                        // still reaches within about a minute.
                        if (currentPayload.ActiveChallengeSeed != 0 &&
                            currentPayload.ActiveChallengeAnswered == 0 &&
                            Environment.TickCount64 - currentPayload.ActiveChallengeIssuedAtMs > AntiCheatTelemetryEngine.ChallengeResponseWindowMs)
                        {
                            // Marked answered so the next broadcast issues a
                            // fresh challenge rather than re-counting this one.
                            currentPayload.ActiveChallengeAnswered = 1;
                            currentPayload.ConsecutiveChallengeMisses++;

                            if (currentPayload.ConsecutiveChallengeMisses >= AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit)
                            {
                                currentPayload.IsQuarantined = true;
                                currentPayload.Quarantine_Active = true;
                                _antiCheatTelemetryEngine?.RequestShadowBan(currentPayload.PlayerId, 54, 4);
                            }
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

                                // Pinned so the answer is judged against the
                                // state the client was actually shown.
                                currentPayload.ActiveChallengeIssuedEpoch = currentPayload.LogicEpochCounter;
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
                            var broadcastCombatStats = StatsCalculator.Calculate(currentPayload.STR, currentPayload.DEX, currentPayload.CON, currentPayload.LCK, currentPayload.ActiveOffensivePotionId, currentPayload.ActiveDefensivePotionId, broadcastActiveAgePhase, currentPayload.CompletedAreaFlags, broadcastActiveRaceId, currentPayload.HumanMasteryLevel, currentPayload.VilaMasteryLevel, currentPayload.DraugrMasteryLevel, currentPayload.CachedAffixTotals, currentPayload.IsEpicMutation, currentPayload.LocusSpeed, currentPayload.LocusCrit, currentPayload.CachedSetIds);

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
                                InventoryCapacity = currentPayload.InventoryCapacity > 0 ? currentPayload.InventoryCapacity : DefaultBackpackCapacity,

                                // Modul: larder + halt reasons. Narrowed to
                                // ushort on the wire, clamped rather than cast,
                                // so a payload value that somehow exceeded the
                                // slot cap truncates to the cap instead of
                                // wrapping to a small number and telling the
                                // player they have 3 apples when they have
                                // 65539.
                                Food1_ItemId = (ushort)Math.Clamp(currentPayload.Food1_ItemId, 0, ushort.MaxValue),
                                Food1_Count = (ushort)Math.Clamp(currentPayload.Food1_Count, 0, LarderLimits.SlotCapacity),
                                Food2_ItemId = (ushort)Math.Clamp(currentPayload.Food2_ItemId, 0, ushort.MaxValue),
                                Food2_Count = (ushort)Math.Clamp(currentPayload.Food2_Count, 0, LarderLimits.SlotCapacity),
                                Food3_ItemId = (ushort)Math.Clamp(currentPayload.Food3_ItemId, 0, ushort.MaxValue),
                                Food3_Count = (ushort)Math.Clamp(currentPayload.Food3_Count, 0, LarderLimits.SlotCapacity),
                                ActivityHaltReason = currentPayload.ActivityHaltReason,

                                CurrentMonsterId = currentPayload.CurrentMonsterId,
                                CurrentMonsterHp = (int)(currentPayload.CurrentMonsterHp / 1000L),
                                PlayerHp = currentPayload.PlayerHp / 1000,
                                Quarantine_Active = currentPayload.Quarantine_Active ? (byte)1 : (byte)0,
                                CurrentLevel = currentPayload.CurrentLevel,
                                CurrentXp = currentPayload.CurrentXp,
                                WoodcuttingMasteryXp = currentPayload.WoodcuttingMasteryXp,
                                WoodcuttingMasteryLevel = currentPayload.WoodcuttingMasteryLevel,
                                MiningMasteryXp = currentPayload.MiningMasteryXp,
                                MiningMasteryLevel = currentPayload.MiningMasteryLevel,
                                FishingMasteryXp = currentPayload.FishingMasteryXp,
                                FishingMasteryLevel = currentPayload.FishingMasteryLevel,
                                HerbalismMasteryXp = currentPayload.HerbalismMasteryXp,
                                HerbalismMasteryLevel = currentPayload.HerbalismMasteryLevel,
                                GatheringProgressTicks = currentPayload.GatheringProgressTicks,
                                CompletedAreaFlags = currentPayload.CompletedAreaFlags,
                                HighestLocationReached = (byte)currentPayload.HighestLocationReached,
                                HighestUnlockedRegion = (byte)currentPayload.HighestUnlockedRegion,
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
                                CurrentSimulationSpeedMultiplier = (byte)Math.Clamp(currentPayload.SpeedMultiplier, 1, 4),
                                VisualBankedChronoSeconds = (uint)ChronoBufferEngine.ClampBankedSeconds(currentPayload.BankedChronoSeconds),
                                ActiveChronoLockExpirationTicks = (ulong)Math.Max(0L, currentPayload.ActiveChronoLockExpirationTicks),
                                GlobalNodeRemainingHp = currentPayload.GlobalNodeRemainingHp <= 0L
                                    ? 0U
                                    : (currentPayload.GlobalNodeRemainingHp > uint.MaxValue ? uint.MaxValue : (uint)currentPayload.GlobalNodeRemainingHp),
                                // Modul: Play Mode audit fix. Never assigned
                                // here despite the field existing on the wire
                                // and StateCheckpointManager now hydrating it
                                // (see LoadPlayerState's own comment) - every
                                // client always saw 0 regardless of a real
                                // active war, permanently gating UiGuildWarPanel
                                // into its "No Active War" state and blocking
                                // ContributeToWarSupply's own gate. The 6
                                // GuildCombatVanguardPoints/.../CachedWarMultiplier
                                // scoreboard fields have the same "never
                                // written to TickStatePayload at all" gap one
                                // layer further back - a real follow-up, out
                                // of scope here (needs a new periodic
                                // GuildWarMatches->TickStatePayload sync loop,
                                // not just a missing packet-copy line).
                                ActiveGuildWarId = currentPayload.ActiveGuildWarId,

                                // Modul: Guild War scoreboard sync. These seven
                                // fields existed on the wire and were read by
                                // UiGuildWarPanel, but were never assigned here -
                                // the second half of the same gap that left
                                // TickStatePayload unwritten. Both ends are wired
                                // now: GuildWarEngine.RunScoreboardSyncLoopAsync
                                // pushes real GuildWarMatches totals into the
                                // payload, and this copies them out to the client.
                                GuildCombatVanguardPoints = currentPayload.GuildCombatVanguardPoints,
                                GuildProductionLogisticsPoints = currentPayload.GuildProductionLogisticsPoints,
                                GuildGatheringSupplyChainPoints = currentPayload.GuildGatheringSupplyChainPoints,
                                EnemyCombatVanguardPoints = currentPayload.EnemyCombatVanguardPoints,
                                EnemyProductionLogisticsPoints = currentPayload.EnemyProductionLogisticsPoints,
                                EnemyGatheringSupplyChainPoints = currentPayload.EnemyGatheringSupplyChainPoints,
                                CachedWarMultiplier = currentPayload.CachedWarMultiplier,
                                AutoEatThreshold = currentPayload.AutoEatThreshold,
                                STR = currentPayload.STR,
                                DEX = currentPayload.DEX,
                                CON = currentPayload.CON,
                                LCK = currentPayload.LCK,
                                EquippedWeaponId = currentPayload.EquippedWeaponId,
                                EquippedWeaponAffixLocked = currentPayload.EquippedWeaponAffixLocked ? (byte)1 : (byte)0,
                                EquippedChestId = currentPayload.EquippedArmorId,

                                // Modul: 6-slot equipment sync. The three slots
                                // that previously had nowhere to go on the wire -
                                // helmets, gloves and boots existed as items and
                                // rolled slot-correct affixes, but no packet
                                // field carried them, so the client could not
                                // show them even once they became equippable.
                                EquippedHelmetId = currentPayload.EquippedHelmetId,
                                EquippedGlovesId = currentPayload.EquippedGlovesId,
                                EquippedBootsId = currentPayload.EquippedBootsId,
                                EquippedAmuletId = currentPayload.EquippedAmuletId,
                                EquippedRingId = currentPayload.EquippedRingId,
                                UnlockedRaceBitmask = currentPayload.UnlockedRaceBitmask,

                                // Modul: inheritance stats. Caught missing by
                                // StateUpdatePacketFieldCoverageTests, which
                                // exists because a field added to the packet and
                                // to the payload but never copied between them
                                // reads as a permanent zero on the client - a
                                // bonus the player paid diamonds for and cannot
                                // see.
                                Inherit_Damage = currentPayload.Inherit_Damage,
                                Inherit_MaxHp = currentPayload.Inherit_MaxHp,
                                Inherit_XpGain = currentPayload.Inherit_XpGain,
                                Inherit_GoldGain = currentPayload.Inherit_GoldGain,
                                Inherit_GatheringYield = currentPayload.Inherit_GatheringYield,
                                Inherit_LootLuck = currentPayload.Inherit_LootLuck,

                                // Modul: roster registers. Characters 2 and 3
                                // read straight from their parked slot state -
                                // the register holds slot 1 at broadcast time,
                                // so these are the only place the other two
                                // characters appear on the wire at all.
                                // Clamped rather than cast, so an id outside the
                                // 16-bit space would saturate visibly instead of
                                // wrapping to a different activity.
                                Slot2ActivityId = (ushort)Math.Clamp(currentPayload.Slot2Activity.ActiveActivityId, 0, ushort.MaxValue),
                                Slot3ActivityId = (ushort)Math.Clamp(currentPayload.Slot3Activity.ActiveActivityId, 0, ushort.MaxValue),
                                Slot2ActivityHaltReason = currentPayload.Slot2Activity.ActivityHaltReason,
                                Slot3ActivityHaltReason = currentPayload.Slot3Activity.ActivityHaltReason,
                                EquippedArmorAffixLocked = currentPayload.EquippedArmorAffixLocked ? (byte)1 : (byte)0,
                                EquippedLeggingsId = currentPayload.EquippedLeggingsId,
                                EquippedLeggingsAffixLocked = currentPayload.EquippedLeggingsAffixLocked ? (byte)1 : (byte)0,
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
                                CommandResult0_Code = currentPayload.CommandResultSlot0.ResultCode,
                                CommandResult0_Tick = currentPayload.CommandResultSlot0.ResultTick,
                                CommandResult1_Code = currentPayload.CommandResultSlot1.ResultCode,
                                CommandResult1_Tick = currentPayload.CommandResultSlot1.ResultTick,
                                CommandResult2_Code = currentPayload.CommandResultSlot2.ResultCode,
                                CommandResult2_Tick = currentPayload.CommandResultSlot2.ResultTick,
                                CommandResult3_Code = currentPayload.CommandResultSlot3.ResultCode,
                                CommandResult3_Tick = currentPayload.CommandResultSlot3.ResultTick,
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
                                CachedMaxPopulationCapacity = currentPayload.CachedMaxPopulationCapacity,
                                CachedCurrentToolTier = currentPayload.CachedCurrentToolTier,
                                AxeToolTier = currentPayload.AxeToolTier,
                                PickaxeToolTier = currentPayload.PickaxeToolTier,
                                RodToolTier = currentPayload.RodToolTier,
                                ToolGatherSpeedPct = currentPayload.ToolGatherSpeedPct,
                                ToolGatherYieldPct = currentPayload.ToolGatherYieldPct,
                                ToolRareFindPct = currentPayload.ToolRareFindPct,
                                CachedInnMaturationBonus = currentPayload.CachedInnMaturationBonus,
                                CachedMentorCount = currentPayload.CachedMentorCount,
                                ActiveChildMaturationMs = currentPayload.ActiveChildMaturationMs,
                                Slot1_CharacterId = currentPayload.Slot1_CharacterId,
                                Slot1_AgeTicks = currentPayload.Slot1_AgeTicks,
                                Slot1_AgePhase = currentPayload.Slot1_AgePhase,
                                Slot2_CharacterId = currentPayload.Slot2_CharacterId,
                                Slot2_AgeTicks = currentPayload.Slot2_AgeTicks,
                                Slot2_AgePhase = currentPayload.Slot2_AgePhase,
                                Slot3_CharacterId = currentPayload.Slot3_CharacterId,
                                Slot3_AgeTicks = currentPayload.Slot3_AgeTicks,
                                Slot3_AgePhase = currentPayload.Slot3_AgePhase,
                                Slot1_RaceId = (byte)(currentPayload.Slot1_GeneticVector & 0xFF),
                                Slot2_RaceId = (byte)(currentPayload.Slot2_GeneticVector & 0xFF),
                                Slot3_RaceId = (byte)(currentPayload.Slot3_GeneticVector & 0xFF),
                                ActiveStatusEffectModifierBitmask = statBitmask,
                                RemainingBuffDurationTicks = statDurTicks,
                                ActiveChallengeSeed = currentPayload.ActiveChallengeSeed,
                                ActiveLanguageState = currentPayload.ActiveLanguageState == 0 ? (byte)1 : currentPayload.ActiveLanguageState,
                                ActiveAudioTrackId = audioTrackId,
                                TotalItemsCraftedCount = (uint)Math.Clamp(currentPayload.LifetimeItemsCrafted, 0L, uint.MaxValue),
                                NetworkDiagnosticsToken = currentPayload.NetworkDiagnosticsToken,
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
                                TownHallLevel = (byte)currentPayload.TownHallLevel,
                                CraftingWorkshopLevel = currentPayload.CraftingWorkshopLevel,
                                LegacyPerksBitmask = currentPayload.CachedLegacyPerks,
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
                                OfflineSlot1Gold = currentPayload.OfflineSlot1Gold,
                                OfflineSlot1Xp = currentPayload.OfflineSlot1Xp,
                                OfflineSlot1Drops = currentPayload.OfflineSlot1Drops,
                                OfflineSlot2Gold = currentPayload.OfflineSlot2Gold,
                                OfflineSlot2Xp = currentPayload.OfflineSlot2Xp,
                                OfflineSlot2Drops = currentPayload.OfflineSlot2Drops,
                                OfflineSlot3Gold = currentPayload.OfflineSlot3Gold,
                                OfflineSlot3Xp = currentPayload.OfflineSlot3Xp,
                                OfflineSlot3Drops = currentPayload.OfflineSlot3Drops,
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
                            // Modul: broadcast dirty-checking. This used to send
                            // unconditionally to every connected player, ten
                            // times a second - 695 bytes x 10Hz is ~7 KB/s per
                            // player whether or not a single field changed, or
                            // about 55 Mbps at a thousand concurrent players
                            // sitting idle.
                            //
                            // Deliberately NOT gated on TickStatePayload.IsDirty
                            // despite that flag existing: IsDirty is owned by
                            // StateCheckpointManager, which uses it to decide
                            // whether to persist to Postgres/Redis and RESETS it
                            // when it does. Consuming it here would silently
                            // skip saves - real data loss to save bandwidth.
                            //
                            // Instead the packet is compared against the last
                            // one actually sent to this player. That is correct
                            // by construction (if the bytes match, the client
                            // already has this exact state) and needs no new
                            // flag threaded through the hundred sites that
                            // mutate the payload.
                            if (ShouldSendStateUpdate(currentPayload.PlayerId, ref packet))
                            {
                                _networkSystem.SendToPlayer(currentPayload.PlayerId, ref packet);
                            }
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

        // Modul: Architecture Overhaul, Part 2. Multi-character position
        // mutex. Authoritative against the "characters" table (not the
        // in-memory tick payload, which only ever tracks one live session
        // per player) so the occupancy check and the slot-level gate both
        // survive across logins and stay correct under concurrent
        // ChangeActivity commands from the same account. Row-locks every
        // character belonging to the player for the duration of the check
        // so two simultaneous requests targeting the same node cannot both
        // observe it as free.
        // Modul: Deploy activation fix. The one place a live activity switch
        // is applied, shared by the single-character command branch and the
        // multi-character ActivityChangeQueue drain so the two can never
        // diverge again. Pure value-type field writes on a payload the tick
        // thread already owns - no allocation, no locking.
        //
        // Every counter reset here is load-bearing: leaving CurrentMonsterId
        // or CurrentMonsterHp behind would carry the previous target's
        // half-dead health onto the new one, and a stale
        // CombatTargetTickAccumulator would land a free hit on arrival.
        // Modul: multi-slot simulation. Which slot holds this character, or -1
        // if the player is not carrying them in an unlocked slot this session.
        private static int ResolveSlotIndexForCharacter(ref TickStatePayload payload, System.Guid characterId)
        {
            if (characterId == System.Guid.Empty) return -1;
            if (payload.Slot1_CharacterId == characterId) return 0;
            if (payload.Slot2_CharacterId == characterId) return 1;
            if (payload.Slot3_CharacterId == characterId) return 2;
            return -1;
        }

        private static void ApplyActivityChangeToPayload(ref TickStatePayload payload, long targetActivityId)
        {
            payload.ActiveActivityId = targetActivityId;
            payload.CurrentProgressTicks = 0;
            payload.CurrentMonsterId = 0;
            payload.CurrentMonsterHp = 0;
            payload.CombatTargetTickAccumulator = 0;
            payload.GatheringProgressTicks = 0;

            // Modul: halt reasons. Deploying is the player's answer to
            // whatever stopped them, so the reason must not survive it - an
            // "out of food" banner that persists after a restock-and-redeploy
            // is worse than none at all. Clearing here rather than in the tick
            // loop also covers the multi-character queue drain, which routes
            // through this same method.
            payload.ActivityHaltReason = Network.ActivityHaltReason.None;

            payload.IsDirty = true;
        }

        internal async Task<Network.CommandResultCode> ChangeCharacterActivityAsync(long playerId, Guid characterId, long targetActivityId)
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                var characters = await db.CharacterRecords
                    .FromSqlInterpolated($"SELECT * FROM \"characters\" WHERE \"PlayerId\" = {playerId} FOR UPDATE")
                    .ToListAsync();

                var requesting = characters.Find(c => c.Id == characterId);
                if (requesting == null)
                {
                    await transaction.RollbackAsync();
                    return Network.CommandResultCode.TargetNotFound;
                }

                // Modul: Town Hall slot gating. Was the main character's
                // CurrentLevel; the second and third slots now hang off the
                // Town Hall's level instead - see CharacterSlotEngine for why
                // level was the wrong axis.
                int townHallLevel = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.TownHallBuildingId)
                    .Select(v => (int?)v.CurrentLevel)
                    .SingleOrDefaultAsync() ?? 0;

                if (!CharacterSlotEngine.IsSlotUnlocked(requesting.SlotIndex, townHallLevel))
                {
                    await transaction.RollbackAsync();
                    return Network.CommandResultCode.LevelTooLow;
                }

                long[] activeActivityIds = new long[CharacterSlotEngine.MaxCharacterSlots];
                for (int i = 0; i < characters.Count; i++)
                {
                    int slot = characters[i].SlotIndex;
                    if (slot >= 0 && slot < CharacterSlotEngine.MaxCharacterSlots)
                    {
                        activeActivityIds[slot] = characters[i].ActiveActivityId;
                    }
                }

                if (CharacterSlotEngine.IsActivityOccupiedByAnotherSlot(activeActivityIds, requesting.SlotIndex, targetActivityId))
                {
                    await transaction.RollbackAsync();
                    return Network.CommandResultCode.NodeOccupied;
                }

                requesting.ActiveActivityId = targetActivityId;
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                return Network.CommandResultCode.Success;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
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
            int masteryLevel = GetMasteryLevel(ref payload, gatheringNode.ProfessionType);

            // Modul: THE WARP PATH IGNORED YOUR TOOLS.
            //
            // The live tick a few thousand lines down resolves the tool that
            // matches the job - axe for woodcutting, pickaxe for mining, rod
            // for fishing - and runs it through GatheringToolEngine, which is
            // where the +10% to +200% speed ladder lives. This path subtracted
            // CachedCurrentToolTier, the FORGE BUILDING'S LEVEL, and applied no
            // speed bonus at all.
            //
            // So a player with a full set of Voidbark tools gathered at their
            // forge level while offline or warping, and every hour away threw
            // away the entire reason to craft tools. Same shape as the food
            // heal above and the three damage models before it: the live tick
            // learned something and the projection beside it did not.
            int villageProductionLevel = gatheringNode.ProfessionType switch
            {
                0 => payload.LumberjackLevel,
                1 => payload.MineLevel,
                _ => 0
            };
            int toolTier = gatheringNode.ProfessionType switch
            {
                0 => payload.AxeToolTier,
                1 => payload.PickaxeToolTier,
                _ => payload.RodToolTier
            };
            int requiredTicks = GatheringToolEngine.ComputeRequiredTicks(
                gatheringNode.BaseTickThreshold, masteryLevel, toolTier, villageProductionLevel);
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
            long ticksPerKill = EstimateTicksPerKill(ref payload, monsterId);
            if (ticksPerKill == long.MaxValue)
            {
                // Cannot hurt it at all - warping past a monster the character
                // cannot kill must bank nothing rather than divide by it.
                return;
            }

            long completedKills = totalTicks / ticksPerKill;
            payload.CombatTargetTickAccumulator = (int)(totalTicks % ticksPerKill);

            if (completedKills <= 0)
            {
                return;
            }

            int warpActiveRaceId = payload.Slot1_CharacterId != System.Guid.Empty ? (int)(payload.Slot1_GeneticVector & 0xFF) : 0;
            int warpActiveAgePhase = payload.Slot1_CharacterId != System.Guid.Empty ? payload.Slot1_AgePhase : 1;
            var warpCombatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, warpActiveAgePhase, payload.CompletedAreaFlags, warpActiveRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds);

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
                payload.CurrentMonsterHp = (long)ContentRegistry.GetScaledMonsterMaxHp(monsterId) * 1000L;
                return;
            }

            int finalXpMultiplier = GlobalEngineState.GlobalXpMultiplier;
            if (payload.CurrentLevel < 50 && payload.CachedMentorCount > 0)
            {
                finalXpMultiplier += payload.CachedMentorCount * 5;
            }

            finalXpMultiplier += RaceMasteryResolver.GetHumanXpBonusPct(payload.HumanMasteryLevel);
            finalXpMultiplier += LegacyPerkResolver.GetXpBonusPct(payload.CachedLegacyPerks);
            finalXpMultiplier += InheritanceRegistry.GetBonusPct(payload.Inherit_XpGain);

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
                // Modul: inheritance. A permanent, season-crossing multiplier.
                goldReward = (long)(goldReward * (1.0f + InheritanceRegistry.GetBonusPct(payload.Inherit_GoldGain) / 100f));

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
            int warpEquipmentDropsToGrant = (int)Math.Min(completedKills, MaxWarpEquipmentDropsPerResolve);
            for (int i = 0; i < warpEquipmentDropsToGrant; i++)
            {
                CombatLootEngine.DropRequestQueue.Enqueue(new CombatLootDropRequest
                {
                    PlayerId = payload.PlayerId,
                    MonsterId = monsterId,
                    LootLuckPct = warpCombatStats.LootLuckPct + InheritanceRegistry.GetBonusPct(payload.Inherit_LootLuck)
                });
            }

            payload.CurrentMonsterId = monsterId;
            payload.CurrentMonsterHp = (long)ContentRegistry.GetScaledMonsterMaxHp(monsterId) * 1000L;
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

        /// <summary>
        /// Expected TICKS to kill this monster, for the instant-warp path.
        ///
        /// This was a fourth-hand damage model:
        /// `15000 + ln(STR + 1) * 1000 + level * 750`, with a fixed 15-tick
        /// cadence. It read no equipment whatsoever - a warp with a Legendary
        /// weapon killed at exactly the speed of a warp bare-handed - subtracted
        /// no armour, applied no hit roll, and its log-decayed STR term matches
        /// nothing else in the codebase, where STR is worth a flat `str * 2`
        /// melee damage.
        ///
        /// Now it asks CombatDamageModel, like the live tick and the offline
        /// projection do. Returning ticks rather than attacks also drops the
        /// hardcoded 1.5 s cadence, so attack-speed bonuses finally apply to a
        /// warp.
        /// </summary>
        private static long EstimateTicksPerKill(ref TickStatePayload payload, int monsterId)
        {
            if (monsterId < 1 || monsterId > ContentRegistry.Monsters.Length) return long.MaxValue;

            var monster = ContentRegistry.Monsters[monsterId - 1];

            int lineageId = payload.SelectedLineageId;
            if (lineageId < 0 || lineageId >= ProgressionEngine.Lineages.Length) lineageId = 0;
            var lineage = ProgressionEngine.Lineages[lineageId];

            int activeAgePhase = payload.Slot1_CharacterId != System.Guid.Empty ? payload.Slot1_AgePhase : 1;
            int activeRaceId = payload.Slot1_CharacterId != System.Guid.Empty ? (int)(payload.Slot1_GeneticVector & 0xFF) : 0;

            var stats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, activeAgePhase, payload.CompletedAreaFlags, activeRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds);

            long rawMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(in stats, lineage.DamageScalePerLevelPct, payload.CurrentLevel, InheritanceRegistry.GetBonusPct(payload.Inherit_Damage));
            double secondsPerKill = CombatDamageModel.ExpectedSecondsPerKill(in stats, in monster, rawMilliAttack, payload.CachedCodexDamageMultiplier);

            if (double.IsInfinity(secondsPerKill) || secondsPerKill <= 0.0) return long.MaxValue;

            double ticks = secondsPerKill * 10.0;
            if (ticks >= long.MaxValue) return long.MaxValue;
            return System.Math.Max(1L, (long)System.Math.Ceiling(ticks));
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
            var warpGatherCombatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, warpGatherActiveAgePhase, payload.CompletedAreaFlags, warpGatherActiveRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds);

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
            // Modul: the backpack is gone - storage is one unlimited village
            // chest. Kept as a no-op rather than deleted so the two warp
            // resolvers still read as "this is where slots used to be spent",
            // and so nothing can quietly start draining the counter again.
            _ = payload;
            _ = expectedDrops;
        }

        internal static void ApplyBulkMasteryXp(ref TickStatePayload payload, int professionType, long masteryXp)
        {
            if (masteryXp <= 0)
            {
                return;
            }

            // Modul: every profession has its own track. This used to be
            // `professionType == 0 ? Woodcutting : Mining`, duplicated in the
            // realtime path as well - so Fishing (2) and Herbalism (3) both
            // levelled MINING. Fishing a node in band 3000 raised the player's
            // mining level, and Fishing had no field to display at all.
            //
            // The mapping now exists exactly once, here and in
            // GetMasteryLevel, so a fifth profession cannot land in the wrong
            // track by being the one nobody added a branch for.
            switch (professionType)
            {
                case 0:
                    AdvanceMastery(ref payload.WoodcuttingMasteryXp, ref payload.WoodcuttingMasteryLevel, masteryXp);
                    break;
                case 1:
                    AdvanceMastery(ref payload.MiningMasteryXp, ref payload.MiningMasteryLevel, masteryXp);
                    break;
                case 2:
                    AdvanceMastery(ref payload.FishingMasteryXp, ref payload.FishingMasteryLevel, masteryXp);
                    break;
                default:
                    AdvanceMastery(ref payload.HerbalismMasteryXp, ref payload.HerbalismMasteryLevel, masteryXp);
                    break;
            }
        }

        // The rarest entry in a gathering table is the LAST one - every node is
        // authored common-then-rare. Scaling by its own weight keeps the boost
        // proportional: +100% doubles a 10-weight rare's share rather than
        // handing every table the same flat number regardless of how rare its
        // rare actually is.
        private static int WeightOf(ReadOnlySpan<LootTableEntry> table, int index, int luckWeightBonus, int rareWeightBonus)
        {
            int weight = table[index].Weight + luckWeightBonus;
            if (rareWeightBonus > 0 && index == table.Length - 1 && table.Length > 1)
            {
                weight += table[index].Weight * rareWeightBonus / 100;
            }

            return weight;
        }

        internal static int GetMasteryLevel(ref TickStatePayload payload, int professionType)
        {
            return professionType switch
            {
                0 => payload.WoodcuttingMasteryLevel,
                1 => payload.MiningMasteryLevel,
                2 => payload.FishingMasteryLevel,
                _ => payload.HerbalismMasteryLevel
            };
        }

        // 50 * (level + 1)^2, as long rather than int: the curve passes
        // int.MaxValue somewhere past level 6500, and an overflow there would
        // wrap the requirement negative and level the player up forever.
        private static long MasteryXpForLevel(int level)
        {
            return 50L * (level + 1L) * (level + 1L);
        }

        private static void AdvanceMastery(ref int xp, ref int level, long gain)
        {
            xp = ClampLongToInt((long)xp + gain);
            long required = MasteryXpForLevel(level);
            while (xp >= required)
            {
                xp -= (int)required;
                level++;
                required = MasteryXpForLevel(level);
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
                // Modul: the warp path must stay identical to the live-tick
                // formula, so it calls the one authority rather than mirroring
                // it - see ProgressionEngine.GetRequiredXpForLevel.
                long requiredXp = ProgressionEngine.GetRequiredXpForLevel(payload.CurrentLevel);
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

        // Modul: Comprehensive Game System Audit, Part 4.3. Unlocks the
        // Chronicle Pass premium track by deducting
        // ChroniclePassEconomy.PremiumPassPriceDiamonds from the player's
        // PremiumDiamonds inside one Serializable FOR UPDATE transaction.
        // Rejections (already unlocked, insufficient balance) surface
        // through the generic command-result channel so UiCommandResultToast
        // can display them.
        internal async Task<bool> ExecutePassPurchaseAsync(long playerId)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            try
            {
                var player = await context.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();
                if (player == null)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                var pass = await context.PlayerChroniclePasses
                    .FromSqlRaw("SELECT * FROM \"PlayerChroniclePasses\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();

                if (pass != null && pass.PremiumUnlocked)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry.EnqueueCommandResult(playerId, (byte)CommandResultCode.GenericValidationFailure);
                    return false;
                }

                if (player.PremiumDiamonds < ChroniclePassEconomy.PremiumPassPriceDiamonds)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry.EnqueueCommandResult(playerId, (byte)CommandResultCode.InsufficientGold);
                    return false;
                }

                int previousBalance = player.PremiumDiamonds;
                player.PremiumDiamonds -= ChroniclePassEconomy.PremiumPassPriceDiamonds;

                if (pass == null)
                {
                    pass = new PlayerChroniclePass
                    {
                        PlayerId = playerId,
                        PassLevel = 0,
                        AccumulatedXp = 0,
                        ClaimedMilestonesBitmask = 0UL,
                        PremiumUnlocked = true
                    };
                    context.PlayerChroniclePasses.Add(pass);
                }
                else
                {
                    pass.PremiumUnlocked = true;
                }

                context.EventHorizonPremiumLedgers.Add(new EventHorizonPremiumLedger
                {
                    TransactionId = $"pass_purchase_{playerId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                    PlayerId = playerId,
                    PreviousBalance = previousBalance,
                    NewBalance = player.PremiumDiamonds,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });

                await context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Modul: Play Mode audit fix. This transaction runs off the
                // tick thread against its own DbContext - without this, the
                // diamond deduction landed in Postgres but the live
                // in-memory TickStatePayload.PremiumCurrency (and the
                // packet built from it) never changed, so a player who
                // just spent 950 diamonds would see their old balance
                // until their next reconnect. BillingSyncNotification/
                // BillingSyncQueue already exists for exactly this
                // PlayerId+new-balance push (see the IAP billing path's
                // own use of it) - reused directly rather than adding a
                // near-duplicate notification type.
                _playerRegistry.BillingSyncQueue.Enqueue(new BillingSyncNotification
                {
                    PlayerId = playerId,
                    PremiumDiamondsBalance = player.PremiumDiamonds
                });

                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Battle pass purchase failed for player {playerId}: {ex.Message}");
                return false;
            }
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

                // Modul: Comprehensive Game System Audit, Part 4.2/4.3.
                // Premium rewards now gate on a real purchased unlock
                // (pass.PremiumUnlocked, set by ExecutePassPurchaseAsync
                // after deducting the pass price) - previously merely
                // HOLDING 1+ diamonds unlocked this branch without ever
                // spending anything. Premium milestones additionally pay
                // out the ChroniclePassEconomy diamond schedule, whose
                // 50-tier sum strictly exceeds the pass price - the
                // self-sustaining loop where a fully active player's
                // season rewards cover the next season's purchase.
                int previousDiamondBalance = player.PremiumDiamonds;
                if (pass.PremiumUnlocked)
                {
                    context.EquipmentInstances.Add(new EquipmentInstance
                    {
                        PlayerId = playerId,
                        BaseItemId = $"chronicle_premium_{milestoneIndex + 1U}",
                        QualityTier = qualityTier,
                        AffixPayload = "{}",
                        IsAffixLocked = false
                    });

                    int diamondReward = ChroniclePassEconomy.GetPremiumDiamondReward((int)milestoneIndex);
                    if (diamondReward > 0)
                    {
                        player.PremiumDiamonds += diamondReward;
                    }
                }

                context.EventHorizonPremiumLedgers.Add(new EventHorizonPremiumLedger
                {
                    TransactionId = $"chronicle_{playerId}_{milestoneIndex + 1U}",
                    PlayerId = playerId,
                    PreviousBalance = previousDiamondBalance,
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
            if (payload.ActiveActivityId > 0 && payload.ActivityHaltReason != Network.ActivityHaltReason.OutOfFood)
            {
                // Running and earning: whatever stopped the player last has
                // been resolved, so the reason must not linger.
                //
                // OutOfFood is exempt because it no longer stops anything - it
                // is a standing warning that the character is fighting without
                // healing. Clearing it here would erase it on the very next
                // tick and the player would never see it. It is cleared where
                // it stops being true: when food is eaten, and when the larder
                // is stocked.
                payload.ActivityHaltReason = Network.ActivityHaltReason.None;
            }

            if (payload.Quarantine_Active) return;

            ProcessPassiveVillageTick(ref payload, TickIntervalSeconds, now);
            ProcessAllSlotSubTicks(ref payload, localXpMultiplier, localDropMultiplier, _guildWarEngine.GuildWarPointQueue, _liveSessionContexts);

            if (chronoAccelerating)
            {
                for (int i = 0; i < extraIterations; i++)
                {
                    ProcessPassiveVillageTick(ref payload, TickIntervalSeconds, now);
                    ProcessAllSlotSubTicks(ref payload, localXpMultiplier, localDropMultiplier, _guildWarEngine.GuildWarPointQueue, _liveSessionContexts);
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
                    ProcessAllSlotSubTicks(ref payload, localXpMultiplier, localDropMultiplier, _guildWarEngine.GuildWarPointQueue, _liveSessionContexts);
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

            // Modul: Economy Polish, Part 2. Town Hall passive gold on the
            // live tick: the accumulator gains the hourly rate once per
            // tick, so 36000 accumulated units (36000 ticks = one hour)
            // pay out exactly one hour's rate - pure integer arithmetic,
            // no floats, no drift, zero allocation. Gold flows through the
            // same AddGold/RedisPendingGoldDelta channel combat gold uses,
            // landing in the CommodityRecords gold row at flush.
            long townHallRate = VillageManagementEngine.GetTownHallGoldRatePerHour(payload.TownHallLevel);
            if (townHallRate > 0L)
            {
                payload.TownHallGoldAccumulator += townHallRate;
                long wholeGold = payload.TownHallGoldAccumulator / 36000L;
                if (wholeGold > 0L)
                {
                    payload.TownHallGoldAccumulator -= wholeGold * 36000L;
                    payload.AddGold(wholeGold);
                    payload.RedisPendingGoldDelta += wholeGold;
                    payload.RequiresRedisFlush = true;
                    payload.IsDirty = true;
                }
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

        // Modul: broadcast dirty-checking. The last StateUpdatePacket actually
        // sent to each player, plus the tick it went out on.
        //
        // A struct value in a Dictionary, so a lookup and a store are copies
        // rather than heap allocations - this runs once per player per tick.
        // Entries are dropped in RemoveBroadcastCacheEntry when a session ends,
        // so the dictionary tracks connected players rather than growing
        // forever.
        private struct LastBroadcast
        {
            public Network.StateUpdatePacket Packet;
            public long TickIndex;
        }

        private readonly Dictionary<long, LastBroadcast> _lastBroadcastByPlayer = new Dictionary<long, LastBroadcast>();

        // Forced resend interval. The client interpolates between the two most
        // recent snapshots (see VisualSyncProxy), so it must never go long
        // without one or motion stutters and the save-trust indicator starves.
        // One second at 10Hz keeps a fully idle player at ~695 B/s instead of
        // ~7 KB/s - a 90 percent reduction - while staying well inside the
        // interpolation window.
        private const int BroadcastKeepaliveTicks = 10;

        // True when this packet differs from the last one sent to this player,
        // or when the keepalive interval has elapsed. Records what it approves
        // so the next call compares against it.
        private bool ShouldSendStateUpdate(long playerId, ref Network.StateUpdatePacket packet)
        {
            long currentTick = _metrics.TotalTicksProcessed;

            if (_lastBroadcastByPlayer.TryGetValue(playerId, out LastBroadcast previous))
            {
                if (!ShouldDispatchStateUpdate(ref packet, ref previous.Packet, currentTick - previous.TickIndex))
                {
                    return false;
                }
            }

            _lastBroadcastByPlayer[playerId] = new LastBroadcast { Packet = packet, TickIndex = currentTick };
            return true;
        }

        // The decision itself, as a pure function of the two packets and how
        // long it has been since the last send.
        //
        // Split out from the dictionary bookkeeping above so it can be tested
        // directly: the keepalive in particular is the kind of thing that
        // silently does not fire and is invisible until a client's
        // interpolation buffer starves in production.
        public static bool ShouldDispatchStateUpdate(
            ref Network.StateUpdatePacket current,
            ref Network.StateUpdatePacket lastSent,
            long ticksSinceLastSend)
        {
            if (ticksSinceLastSend >= BroadcastKeepaliveTicks)
            {
                return true;
            }

            return !StateUpdatePacketsAreEquivalent(ref current, ref lastSent);
        }

        // Byte-compares two packets while ignoring TicksSinceLastFlush.
        //
        // That field increments EVERY tick by design (it is the client's
        // data-staleness indicator), so a naive comparison would find every
        // packet different and the dirty check would save nothing at all. It is
        // normalised to zero on both sides rather than excluded by offset,
        // because an offset-based skip would silently break the moment anyone
        // reorders the struct.
        internal static bool StateUpdatePacketsAreEquivalent(ref Network.StateUpdatePacket left, ref Network.StateUpdatePacket right)
        {
            Network.StateUpdatePacket normalizedLeft = left;
            Network.StateUpdatePacket normalizedRight = right;
            normalizedLeft.TicksSinceLastFlush = 0;
            normalizedRight.TicksSinceLastFlush = 0;

            ReadOnlySpan<byte> leftBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref normalizedLeft, 1));
            ReadOnlySpan<byte> rightBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref normalizedRight, 1));

            return leftBytes.SequenceEqual(rightBytes);
        }

        // Modul: broadcast dirty-checking. Called when a session ends so the
        // cache does not accumulate an entry per player who has ever connected.
        // Also forces the next packet after a reconnect to be a full send,
        // which is correct: a returning client has no snapshot at all.
        public void RemoveBroadcastCacheEntry(long playerId)
        {
            _lastBroadcastByPlayer.Remove(playerId);
        }

        // Modul: multi-slot simulation. Everything in the old ProcessSubTick
        // prologue that belongs to the ACCOUNT rather than to one character:
        // character aging, mana regeneration, potion/food buff countdowns, and
        // child maturation. It has to run exactly once per tick.
        //
        // With three characters now driving ProcessSubTick up to three times a
        // tick, leaving these here would have aged every character three times
        // as fast, tripled mana regen, and expired potions three times quicker
        // the moment a second slot was assigned - a silent, invisible speedup
        // of unrelated systems as a side effect of a UI action.
        //
        // The activity guard is preserved as "any slot is working": before
        // multi-slot, an idle player's potions did not tick down and their
        // characters did not age, and that behaviour is deliberately unchanged
        // rather than quietly fixed as part of this refactor.
        private static void ProcessAccountTick(ref TickStatePayload payload)
        {
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

            // Modul: Constitution made real. StatsCalculator has always
            // documented CON as granting "+0.1 Out-of-Combat HP Regen/sec" and
            // has always computed CombatStats.OutOfCombatHpRegen - and nothing
            // anywhere read it, so the stat was pure decoration.
            //
            // OUT of combat means exactly that: only while no activity is
            // running. Regenerating during a fight would silently undercut the
            // auto-eat larder, which is the intended sustain mechanic and the
            // thing every halt reason is built around.
            //
            // PlayerHp is milli-HP, and the stat is HP per SECOND at 10Hz, so
            // one tick is stat * 1000 / 10 = stat * 100.
            // Modul: OUT OF COMBAT MEANS OUT OF COMBAT.
            //
            // This used to require ActiveActivityId == 0 - completely idle. A
            // character chopping wood or standing at a bench is not fighting
            // anything, and healed at exactly zero. Combined with the second
            // half of this fix it meant a character who came out of a fight
            // hurt stayed hurt until the player noticed and stopped them doing
            // anything at all.
            if (!ActivityIdBands.IsCombatActivity(payload.ActiveActivityId))
            {
                // Resolved the same way the combat path does it - slot 1 is the
                // active character, and its race is the low byte of the
                // genetic vector.
                int regenAgePhase = payload.Slot1_AgePhase;
                int regenRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);

                CombatStats regenStats = StatsCalculator.Calculate(
                    payload.STR, payload.DEX, payload.CON, payload.LCK,
                    payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId,
                    regenAgePhase, payload.CompletedAreaFlags,
                    regenRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel,
                    payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation,
                    payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds);

                int regenMaxHp = 100000 + (regenStats.MaxHp * 1000);
                if (payload.PlayerHp < regenMaxHp)
                {
                    // Modul: and there is always a baseline. The stat is zero
                    // for a character with no gear and no perks, so the whole
                    // branch used to be a no-op for exactly the players who
                    // most need it. Everyone recovers a share of their own
                    // maximum per second; the stat adds to that rather than
                    // being the entire supply of it.
                    int baselinePerTick = regenMaxHp / (BaselineOutOfCombatRegenSeconds * 10);
                    if (baselinePerTick < 1) baselinePerTick = 1;

                    payload.PlayerHp += baselinePerTick + (int)(regenStats.OutOfCombatHpRegen * 100f);
                    if (payload.PlayerHp > regenMaxHp) payload.PlayerHp = regenMaxHp;
                    payload.IsDirty = true;
                }
            }

            // Modul: Deferred Part 5 Implementation, Part 2. The inline
            // offensive/defensive countdown moved into
            // ConsumableEngine.TickBuffCountdowns (unchanged semantics)
            // and gained the food-buff slot - one unit-testable,
            // zero-allocation method for all three expirations.
            ConsumableEngine.TickBuffCountdowns(ref payload);

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
        }

        // Modul: multi-slot simulation. Swaps character slot `slotIndex`'s
        // parked state into the payload's active register (the flat
        // ActiveActivityId / PlayerHp / CurrentMonsterId / Slot1_* fields) and,
        // called a second time with the same index, swaps it straight back.
        //
        // Being its own inverse is the safety property that matters here: the
        // per-slot loop cannot leave a foreign character loaded in the register
        // even if the body between the two calls returns early, because both
        // calls sit in the same straight-line block. Slot 0 is a no-op, so the
        // register holds the main character whenever the loop is not mid-slot -
        // which is what the outbound packet, the checkpoint flush and the
        // offline extrapolation all assume.
        //
        // Pure unmanaged field exchanges: no allocation, no boxing.
        // True when the tick that just elapsed carried the combat clock across
        // a multiple of intervalMs. Each tick is TickIntervalMs of game time, so
        // "how many attacks should have happened by now" is elapsed/interval,
        // and an attack is due whenever that count increases.
        //
        // Exact for any interval, unlike the modulo test it replaces, which
        // silently required the interval to divide a multiple of the tick.
        private const int TickDurationMs = 100;

        private static bool HasCrossedInterval(int tickAccumulator, int intervalMs)
        {
            if (intervalMs <= 0) return false;
            if (tickAccumulator <= 0) return false;

            long elapsedMs = (long)tickAccumulator * TickDurationMs;
            long previousMs = elapsedMs - TickDurationMs;

            return (elapsedMs / intervalMs) > (previousMs / intervalMs);
        }

        // internal: OfflineSimulationEngine needs the same swap to catch up
        // characters 2 and 3. Before that it only ever simulated whatever was
        // in the active register, which is always slot 1 - so an assigned
        // second character earned nothing at all while the player was away.
        internal static void SwapSlotIntoActiveRegister(ref TickStatePayload payload, int slotIndex)
        {
            if (slotIndex == 0)
            {
                return;
            }

            if (slotIndex == 1)
            {
                SwapRegisterWith(ref payload, ref payload.Slot2Activity, ref payload.Slot2_CharacterId, ref payload.Slot2_AgeTicks, ref payload.Slot2_AgePhase, ref payload.Slot2_GeneticVector);
            }
            else
            {
                SwapRegisterWith(ref payload, ref payload.Slot3Activity, ref payload.Slot3_CharacterId, ref payload.Slot3_AgeTicks, ref payload.Slot3_AgePhase, ref payload.Slot3_GeneticVector);
            }
        }

        private static void SwapRegisterWith(
            ref TickStatePayload payload,
            ref CharacterActivityState parked,
            ref System.Guid parkedCharacterId,
            ref long parkedAgeTicks,
            ref int parkedAgePhase,
            ref long parkedGeneticVector)
        {
            Swap(ref payload.ActiveActivityId, ref parked.ActiveActivityId);
            Swap(ref payload.CurrentProgressTicks, ref parked.CurrentProgressTicks);
            Swap(ref payload.RequiredProgressTicks, ref parked.RequiredProgressTicks);
            Swap(ref payload.CurrentMonsterId, ref parked.CurrentMonsterId);
            Swap(ref payload.CurrentMonsterHp, ref parked.CurrentMonsterHp);
            Swap(ref payload.PlayerHp, ref parked.PlayerHp);
            Swap(ref payload.CombatTargetTickAccumulator, ref parked.CombatTargetTickAccumulator);
            Swap(ref payload.TargetStatusEffectBitmask, ref parked.TargetStatusEffectBitmask);
            Swap(ref payload.GatheringProgressTicks, ref parked.GatheringProgressTicks);
            Swap(ref payload.HarvestLoopCount, ref parked.HarvestLoopCount);
            Swap(ref payload.ActivityHaltReason, ref parked.ActivityHaltReason);

            // Modul: per-character equipment. Gear and its derived totals travel
            // with the character, or every slot would fight in slot 1's armour.
            Swap(ref payload.EquippedWeaponId, ref parked.EquippedWeaponId);
            Swap(ref payload.EquippedHelmetId, ref parked.EquippedHelmetId);
            Swap(ref payload.EquippedArmorId, ref parked.EquippedChestId);
            Swap(ref payload.EquippedGlovesId, ref parked.EquippedGlovesId);
            Swap(ref payload.EquippedLeggingsId, ref parked.EquippedLeggingsId);
            Swap(ref payload.EquippedBootsId, ref parked.EquippedBootsId);
            Swap(ref payload.EquippedAmuletId, ref parked.EquippedAmuletId);
            Swap(ref payload.EquippedRingId, ref parked.EquippedRingId);
            Swap(ref payload.EquippedWeaponAffixLocked, ref parked.EquippedWeaponAffixLocked);
            Swap(ref payload.EquippedArmorAffixLocked, ref parked.EquippedArmorAffixLocked);
            Swap(ref payload.EquippedLeggingsAffixLocked, ref parked.EquippedLeggingsAffixLocked);
            Swap(ref payload.CachedAffixTotals, ref parked.CachedAffixTotals);
            Swap(ref payload.CachedSetIds, ref parked.CachedSetIds);

            // Identity travels with the activity: combat stats are derived from
            // the active character's race, age phase and genetic loci, so a
            // slot has to fight as itself rather than as slot 1. The Slot1_*
            // fields ARE the register's identity, so this exchanges them with
            // the parked slot's own Slot2_*/Slot3_* fields.
            Swap(ref payload.Slot1_CharacterId, ref parkedCharacterId);
            Swap(ref payload.Slot1_AgeTicks, ref parkedAgeTicks);
            Swap(ref payload.Slot1_AgePhase, ref parkedAgePhase);
            Swap(ref payload.Slot1_GeneticVector, ref parkedGeneticVector);
        }

        private static void Swap<T>(ref T left, ref T right) where T : struct
        {
            T temporary = left;
            left = right;
            right = temporary;
        }

        // Modul: multi-slot simulation. Runs one 10Hz activity step for every
        // character the player has unlocked AND assigned.
        //
        // Before this, only slot 1 was ever simulated - slots 2 and 3 held a
        // persisted ActiveActivityId that nothing acted on, so assigning a
        // second character produced no gold, no drops and no progress of any
        // kind. The occupancy mutex in CharacterSlotEngine guarantees no two
        // slots share an activity id, so the three passes can never
        // double-count the same node or monster.
        private static void ProcessAllSlotSubTicks(ref TickStatePayload payload, int localXpMultiplier, int localDropMultiplier, System.Collections.Concurrent.ConcurrentQueue<GuildWarPointEvent> guildWarPointQueue, System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext> liveSessionContexts)
        {
            int unlockedSlots = CharacterSlotEngine.GetUnlockedSlotCount(payload.TownHallLevel);

            // The old ProcessSubTick bailed out before its prologue whenever the
            // player was idle or their backpack was full, so aging, mana regen
            // and potion countdowns did not advance in those states. That is
            // preserved verbatim rather than quietly corrected here - the
            // generalisation is only from "slot 1 is working" to "any unlocked
            // slot is working".
            if (!HasAnyWorkingSlot(ref payload, unlockedSlots))
            {
                return;
            }

            ProcessAccountTick(ref payload);

            for (int slotIndex = 0; slotIndex < unlockedSlots; slotIndex++)
            {
                // Slots 2 and 3 only run when a real character occupies them.
                // Slot 0 is exempt: it is the pre-existing single-character path
                // and injected virtual players legitimately have no
                // CharacterRecord, so requiring one there would stop them.
                if (slotIndex > 0 && !SlotHoldsCharacter(ref payload, slotIndex))
                {
                    continue;
                }

                // Modul: slot register hardening, 2026-08-01.
                //
                // The swap-back is in a finally so it cannot be skipped. The
                // pair is what keeps the active register consistent with what
                // the payload believes it holds; if ProcessSubTick threw, the
                // second swap was lost and the register kept the WRONG slot's
                // character, gear and combat state while every later reader
                // assumed slot 0. That corruption would outlive the exception
                // and be attributed to something else entirely.
                SwapSlotIntoActiveRegister(ref payload, slotIndex);
                try
                {
                    ProcessSubTick(ref payload, localXpMultiplier, localDropMultiplier, guildWarPointQueue, liveSessionContexts);
                }
                finally
                {
                    SwapSlotIntoActiveRegister(ref payload, slotIndex);
                }
            }
        }

        /// <summary>
        /// Writes one inheritance level onto the live payload.
        ///
        /// A switch rather than an indexer because the payload is a blittable
        /// struct on the wire - see TickStatePayload - so the six levels are six
        /// fields, not an array.
        /// </summary>
        private static void SetInheritanceLevel(ref TickStatePayload payload, int statId, byte level)
        {
            switch (statId)
            {
                case InheritanceRegistry.StatDamage: payload.Inherit_Damage = level; break;
                case InheritanceRegistry.StatMaxHp: payload.Inherit_MaxHp = level; break;
                case InheritanceRegistry.StatXpGain: payload.Inherit_XpGain = level; break;
                case InheritanceRegistry.StatGoldGain: payload.Inherit_GoldGain = level; break;
                case InheritanceRegistry.StatGatheringYield: payload.Inherit_GatheringYield = level; break;
                case InheritanceRegistry.StatLootLuck: payload.Inherit_LootLuck = level; break;
            }
        }

        private static bool SlotHoldsCharacter(ref TickStatePayload payload, int slotIndex)
        {
            return slotIndex switch
            {
                0 => payload.Slot1_CharacterId != System.Guid.Empty,
                1 => payload.Slot2_CharacterId != System.Guid.Empty,
                2 => payload.Slot3_CharacterId != System.Guid.Empty,
                _ => false
            };
        }

        // True when at least one unlocked slot has a character on an activity
        // and there is backpack room to put its yield. Reads the parked slots
        // directly rather than swapping, since a swap per probe would be pure
        // overhead on the common single-character path.
        private static bool HasAnyWorkingSlot(ref TickStatePayload payload, int unlockedSlots)
        {
            // Modul: the backpack no longer gates work. This used to return
            // false with no room left, which stopped every slot on the account
            // at once - the account-wide version of the same wall
            // ProcessSubTick had.

            // Slot 0: exactly the original condition, no character requirement.
            if (payload.ActiveActivityId > 0)
            {
                return true;
            }

            if (unlockedSlots > 1 && payload.Slot2Activity.ActiveActivityId > 0 && payload.Slot2_CharacterId != System.Guid.Empty)
            {
                return true;
            }

            if (unlockedSlots > 2 && payload.Slot3Activity.ActiveActivityId > 0 && payload.Slot3_CharacterId != System.Guid.Empty)
            {
                return true;
            }

            return false;
        }

        // internal, not private: the tick's whole reward path lives in here and
        // there was no way to observe an hour of it without a Postgres
        // container, a socket and twenty-six constructor dependencies. It takes
        // its two collaborators as parameters already, so a test can drive it
        // headlessly with empty ones - see ProgressionRateTests, which is how
        // "levelling is too fast" stopped being a report and became a number.
        internal static void ProcessSubTick(ref TickStatePayload payload, int localXpMultiplier, int localDropMultiplier, System.Collections.Concurrent.ConcurrentQueue<GuildWarPointEvent> guildWarPointQueue, System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext> liveSessionContexts)
        {
            // Modul: multi-slot simulation. Guard kept byte-for-byte identical
            // to the pre-multi-slot original. It deliberately does NOT also
            // require a character id: injected virtual players (the benchmark
            // stress tester, several integration tests) run an activity with no
            // CharacterRecord behind them, and adding that condition here
            // silently stopped them ticking. Whether a SLOT holds a character is
            // the slot loop's business, not this function's - see
            // ProcessAllSlotSubTicks.
            //
            // The account-level prologue that used to live here now runs once
            // per tick in ProcessAccountTick.
            // Modul: THE BACKPACK NO LONGER GATES THE SIMULATION.
            //
            // This line used to read `|| payload.InventorySpaceRemaining <= 0`,
            // which stopped combat, gathering, XP - everything - the moment a
            // character had twenty things. It also made CombatLootEngine's own
            // graceful-degradation path unreachable dead code: that engine
            // already knew how to scrap an overflowing drop into stackable
            // material, but no tick ever ran to ask it.
            //
            // Loot now routes by WHAT IT IS. Materials go to the unbounded
            // village chest; equipment goes to the bank if it clears the keep
            // threshold and scraps into material if it does not. There is no
            // capacity left to run out of, so there is nothing here to gate on.
            if (payload.ActiveActivityId <= 0)
            {
                return;
            }

            payload.IsDirty = true;


            if (ContentRegistry.TryGetRecipeByActivityId(payload.ActiveActivityId, out var craftingRecipe))
            {
                // Modul: crafting as an assignable job. CraftingTimeMs was
                // authored on all 104 recipes and read by nothing - a craft
                // was instant and needed no character. It is now a job like
                // any other: one assigned character, real elapsed time, and
                // it repeats until the player stops it or runs out of
                // materials (CraftingEngine refuses the craft, the tick keeps
                // counting, and the halt shows up as nothing being produced).
                int craftTicks = craftingRecipe.CraftingTimeMs / 100;
                if (craftTicks < MinCraftTicks) craftTicks = MinCraftTicks;

                payload.RequiredProgressTicks = craftTicks;
                payload.GatheringProgressTicks++;

                if (payload.GatheringProgressTicks >= craftTicks)
                {
                    payload.GatheringProgressTicks = 0;
                    payload.HarvestLoopCount++;
                    CraftingTickQueue.Enqueue(new CraftTickCompletion
                    {
                        PlayerId = payload.PlayerId,
                        ResultItemId = craftingRecipe.ResultItemId
                    });
                }
            }
            else if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out var gatheringNode))
            {
                int masteryLevel = GetMasteryLevel(ref payload, gatheringNode.ProfessionType);

                // Modul: Deferred Part 5 Implementation, Parts 1/3. The
                // required-tick math (legacy flat reductions + the tool
                // family's percentage speed bonus + the village production
                // building's +5 percent per level) lives in
                // GatheringToolEngine.ComputeRequiredTicks - pure integer
                // arithmetic over unmanaged payload ids, zero allocation on
                // this 10Hz path. Lumberjack accelerates Woodcutting, Mine
                // accelerates Mining.
                // Only Woodcutting and Mining have a village production
                // building. Fishing and Herbalism get no acceleration rather
                // than silently borrowing the Mine's.
                int villageProductionLevel = gatheringNode.ProfessionType switch
                {
                    0 => payload.LumberjackLevel,
                    1 => payload.MineLevel,
                    _ => 0
                };
                // Modul: the tool that matches the job. This passed
                // CachedCurrentToolTier, which was the forge building's level -
                // so an axe sped up fishing, a rod sped up mining, and owning
                // no tool at all made no difference either way.
                int toolTier = gatheringNode.ProfessionType switch
                {
                    0 => payload.AxeToolTier,
                    1 => payload.PickaxeToolTier,
                    _ => payload.RodToolTier
                };
                int requiredTicks = GatheringToolEngine.ComputeRequiredTicks(gatheringNode.BaseTickThreshold, masteryLevel, toolTier, villageProductionLevel);
                payload.RequiredProgressTicks = requiredTicks;
                payload.GatheringProgressTicks++;

                if (payload.GatheringProgressTicks >= requiredTicks)
                {
                    payload.GatheringProgressTicks = 0;
                    payload.HarvestLoopCount++;

                    int masteryXpGain = gatheringNode.BaseMasteryXpReward;
                    ApplyBulkMasteryXp(ref payload, gatheringNode.ProfessionType, masteryXpGain);
                    AddSeasonalXp(ref payload, masteryXpGain);

                    // Loot roll
                    var lootTable = ContentRegistry.GetLootTable(gatheringNode.ActivityId);
                    if (lootTable.Length > 0)
                    {
                        int gatherActiveAgePhase = 1;
                        int gatherActiveRaceId = 0;
                        if (payload.Slot1_CharacterId != System.Guid.Empty)
                        {
                            gatherActiveAgePhase = payload.Slot1_AgePhase;
                            gatherActiveRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);
                        }
                        var gatherCombatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, gatherActiveAgePhase, payload.CompletedAreaFlags, gatherActiveRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds);

                        int monolithLevel = gatheringNode.ProfessionType switch
                        {
                            0 => payload.CachedWoodcuttingMonolithLevel,
                            1 => payload.CachedMiningMonolithLevel,
                            _ => 0
                        };
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
                                        // Modul: THE GATHERED ITEM IS ACTUALLY
                                        // GRANTED. This block used to compute a
                                        // Kobold carry weight, spend a backpack
                                        // slot and break - with no write to
                                        // CommodityRecords anywhere on the
                                        // gathering path. The winner was picked
                                        // and dropped on the floor.
                                        //
                                        // The Kobold penalty went with the
                                        // backpack: it was a rule about carrying
                                        // capacity, and there is no capacity to
                                        // penalise now that storage is one
                                        // unlimited chest.
                                        int grantQuantity = lootTable[i].MaxQuantity > lootTable[i].MinQuantity
                                            ? Random.Shared.Next(Math.Max(1, lootTable[i].MinQuantity), lootTable[i].MaxQuantity + 1)
                                            : 1;

                                        CombatLootEngine.GatheringGrantQueue.Enqueue(new GatheredMaterialGrant
                                        {
                                            PlayerId = payload.PlayerId,
                                            ActivityId = payload.ActiveActivityId,
                                            ItemId = lootTable[i].ItemId,
                                            Quantity = grantQuantity
                                        });
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

            var combatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, activeAgePhase, payload.CompletedAreaFlags, activeRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds);
            
            long baseMilliHp = 100000L;
            long effectiveMilliHp = baseMilliHp + (baseMilliHp * lineage.HpScalePerLevelPct * payload.CurrentLevel / 100) + (combatStats.MaxHp * 1000L);
            // Modul: inheritance, applied to the whole pool for the same reason
            // the damage bonus is applied last - a flat addition would stop
            // mattering.
            effectiveMilliHp += effectiveMilliHp * InheritanceRegistry.GetBonusPct(payload.Inherit_MaxHp) / 100L;
            int effectiveMaxHp = (int)effectiveMilliHp;

            // Modul: Deferred Part 5 Implementation, Part 2. Active food
            // buff: flat HP regeneration while in combat - 2 percent of
            // effective max HP per second (effectiveMaxHp / 500 per 10Hz
            // tick, minimum 1 milli-HP). Pure integer arithmetic on
            // unmanaged payload fields - zero allocation.
            if (payload.ActiveFoodBuffId > 0 && payload.PlayerHp > 0 && payload.PlayerHp < effectiveMaxHp)
            {
                int regenPerTick = effectiveMaxHp / ConsumableEngine.FoodRegenDivisor;
                if (regenPerTick < 1) regenPerTick = 1;
                payload.PlayerHp += regenPerTick;
                if (payload.PlayerHp > effectiveMaxHp) payload.PlayerHp = effectiveMaxHp;
            }

            if (payload.PlayerHp <= 0)
            {
                payload.PlayerHp = effectiveMaxHp;
            }

            if (payload.CurrentMonsterId <= 0)
            {
                payload.CurrentMonsterId = fallbackId;
                payload.CurrentMonsterHp = (long)ContentRegistry.GetScaledMonsterMaxHp(payload.CurrentMonsterId) * 1000L;
                payload.CombatTargetTickAccumulator = 0;
            }

            payload.CombatTargetTickAccumulator++;

            var activeMonster = ContentRegistry.Monsters[payload.CurrentMonsterId - 1];

            // Player attacks monster
            int playerAttackSpeedMs = (int)(1500 * (1.0f - combatStats.AttackSpeedPct));
            if (playerAttackSpeedMs < 200) playerAttackSpeedMs = 200; // Hard cap attack speed

            // Modul: attack cadence fix, 2026-08-02.
            //
            // Was `(accumulator * 100) % intervalMs == 0`, which only fires when
            // elapsed time lands EXACTLY on the interval - so any interval that
            // is not a divisor of a multiple of 100 fired at the least common
            // multiple instead. Attack speed bonuses therefore made the player
            // attack SLOWER: +5% meant 1425ms intended but 5700ms actual (4x
            // slower), +10% was 2x, +33% was 25x. Only bonuses landing on a
            // multiple of 100 behaved. attack_speed_pct is a rerollable affix,
            // so players were paying gold and diamonds to get worse.
            //
            // Now fires when an interval BOUNDARY was crossed during this tick,
            // which is exact for any interval and needs no extra payload state.
            if (HasCrossedInterval(payload.CombatTargetTickAccumulator, playerAttackSpeedMs))
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
                        critMult = StatsCalculator.ComputeCritMultiplier(combatStats);
                    }

                    long effectiveMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(in combatStats, lineage.DamageScalePerLevelPct, payload.CurrentLevel, InheritanceRegistry.GetBonusPct(payload.Inherit_Damage));

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

                    // Modul: set bonuses made real. The Chiming Steel 4-piece
                    // grants FireDamageMultiplierPct, which reached CombatStats
                    // and was read by nothing - so the whole 4-piece tier paid
                    // out zero. Applied here rather than to rawDamage so it
                    // scales the post-armour figure, matching how the codex
                    // multiplier directly above it behaves.
                    if (combatStats.SetFireDamageMultiplierPct > 0f)
                    {
                        netDamage = (int)(netDamage * (1f + (combatStats.SetFireDamageMultiplierPct / 100f)));
                    }

                    payload.CurrentMonsterHp -= netDamage;

                    // Modul: set bonuses made real. Burn - the Chiming Steel
                    // 4-piece's damage-over-time half. Deliberately modelled as
                    // a deterministic fraction of the hit that applied it,
                    // resolved immediately, rather than as a timed DoT: this
                    // combat loop has no per-target effect timers, and adding a
                    // scheduler for one effect would be a much larger change
                    // than the effect is worth. The player-visible result is
                    // the same - a matching 4-piece set burns for extra damage.
                    if (combatStats.SetBurnApplicationActive)
                    {
                        int burnDamage = (int)(netDamage * BurnDamageFraction);
                        if (burnDamage > 0)
                        {
                            payload.CurrentMonsterHp -= burnDamage;
                            payload.TargetStatusEffectBitmask |= ActiveSkillEngine.StatusFlagBurning;
                        }
                    }

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
            // Same boundary-crossing test as the player above - monster
            // AttackIntervalMs values are content-authored and equally free to
            // be non-multiples of the 100ms tick.
            if (payload.CurrentMonsterHp > 0 && HasCrossedInterval(payload.CombatTargetTickAccumulator, activeMonster.AttackIntervalMs))
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

                    // Computed in long, then saturated. AttackPower * 1000 * 1.5
                    // overflows int for the highest-tier authored monsters
                    // (Perun's Shattered Aspect sits at 5,368,903 AP, which
                    // reaches 8.05e9 on a crit against an int ceiling of
                    // 2.15e9). The wrapped value went negative, the Math.Max
                    // floor below caught it, and the deadliest monster in the
                    // game dealt exactly 1 HP per hit - the inverse of the
                    // spawn-already-dead bug on the HP side.
                    long rawDamageLong = (long)(ContentRegistry.GetScaledMonsterAttackPower(payload.CurrentMonsterId) * 1000L * monsterCritMult);
                    int rawDamage = rawDamageLong >= int.MaxValue ? int.MaxValue : (int)rawDamageLong;

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

                    // Modul: set effect rework. The Eternal Dreadnought 4-piece
                    // caps any single hit at a share of max HP.
                    //
                    // This replaced CcImmunityActive, which could never fire
                    // because the game has no player-facing crowd control. The
                    // cap targets the failure mode this game actually has:
                    // burst. Region bosses sit at ~2.5x the attack power of
                    // their region's regular monsters, so what ends a run is one
                    // large hit, not accumulated chip damage - and the auto-eat
                    // larder can only respond BETWEEN hits, never during one.
                    //
                    // Applied after armour and block so it is a true ceiling
                    // rather than another mitigation term, and before the
                    // subtraction so thorns below reflects the capped figure -
                    // the set cannot turn its own defence into extra offence.
                    if (combatStats.SetDamageCapActive)
                    {
                        int damageCeiling = (int)(effectiveMaxHp * SetDamageCapMaxHpFraction);
                        if (damageCeiling > 0 && finalDamage > damageCeiling)
                        {
                            finalDamage = damageCeiling;
                        }
                    }

                    payload.PlayerHp -= finalDamage;

                    // Modul: set bonuses made real. Thorns - the Eternal
                    // Dreadnought 4-piece. Reflects a fraction of what actually
                    // landed (post-armour, post-block), so a heavily armoured
                    // build reflects less rather than more; reflecting the raw
                    // pre-mitigation figure would make stacking armour and
                    // thorns together absurd.
                    //
                    // Only reflects while a monster is actually alive, so the
                    // final blow cannot reflect into an already-dead target and
                    // drive CurrentMonsterHp further negative.
                    if (combatStats.SetThornsReflectionActive && payload.CurrentMonsterHp > 0)
                    {
                        int reflectedDamage = (int)(finalDamage * ThornsReflectionFraction);
                        if (reflectedDamage > 0)
                        {
                            payload.CurrentMonsterHp -= reflectedDamage;
                        }
                    }
                }
            }

            // Step 5 (Auto-Eat)
            if (payload.PlayerHp > 0 && payload.PlayerHp <= (payload.AutoEatThreshold / 100.0f) * effectiveMaxHp)
            {
                int bestFoodIndex = 0;
                int highestHeal = 0;

                // Modul: larder. These were all a hardcoded 50000 milli-HP,
                // which made the "highest-healing food" selection below a tie
                // on every comparison - so it always drained slot 1 first
                // regardless of what the player had loaded, and a tier-10
                // Astral Ambrosia Roast (82000 HP per the GDD) restored the
                // same 50 HP as a tier-1 minnow. FoodRegistry lookups on the
                // cooked id block are integer arithmetic on a static array -
                // no allocation on this per-tick path.
                // Modul: a share of the bar, not a fixed number of points. See
                // FoodRegistry on why an authored heal against a growing wound
                // made food nearly free for three regions and unaffordable in
                // the fifth. effectiveMaxHp is already milli-HP here, which is
                // the unit the registry expects.
                int heal1 = FoodRegistry.GetHealMilliHp(payload.Food1_ItemId, (long)effectiveMaxHp);
                int heal2 = FoodRegistry.GetHealMilliHp(payload.Food2_ItemId, (long)effectiveMaxHp);
                int heal3 = FoodRegistry.GetHealMilliHp(payload.Food3_ItemId, (long)effectiveMaxHp);

                if (payload.Food1_Count > 0 && heal1 > highestHeal) { bestFoodIndex = 1; highestHeal = heal1; }
                if (payload.Food2_Count > 0 && heal2 > highestHeal) { bestFoodIndex = 2; highestHeal = heal2; }
                if (payload.Food3_Count > 0 && heal3 > highestHeal) { bestFoodIndex = 3; highestHeal = heal3; }

                if (bestFoodIndex == 1) { payload.Food1_Count--; payload.PlayerHp += highestHeal; }
                else if (bestFoodIndex == 2) { payload.Food2_Count--; payload.PlayerHp += highestHeal; }
                else if (bestFoodIndex == 3) { payload.Food3_Count--; payload.PlayerHp += highestHeal; }

                if (bestFoodIndex > 0)
                {
                    if (payload.ActivityHaltReason == Network.ActivityHaltReason.OutOfFood)
                    {
                        payload.ActivityHaltReason = Network.ActivityHaltReason.None;
                    }
                }
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

                    // Modul: an empty larder no longer ENDS the activity.
                    //
                    // This used to stop combat outright the first time health
                    // crossed the auto-eat threshold with nothing to eat -
                    // which meant that at a 50% threshold a character with a
                    // full health bar and no food stopped at half health,
                    // having never been in danger. Food was therefore not a
                    // sustain system but a licence to play at all, and the
                    // only way to notice was that everything went quiet.
                    //
                    // Now the character simply does not heal. It keeps
                    // fighting and, if it loses, dies and respawns - which the
                    // death branch below already handles and which the player
                    // can see happening. The halt reason is still reported so
                    // "you are out of food" remains visible; it is now a
                    // warning rather than a full stop.
                    payload.ActivityHaltReason = Network.ActivityHaltReason.OutOfFood;
                }

                if (payload.PlayerHp > effectiveMaxHp) payload.PlayerHp = effectiveMaxHp;
            }

            if (payload.PlayerHp <= 0)
            {
                // Modul: Deferred Part 5 Implementation, Part 2. Death
                // Ward Elixir - intercepts the lethal blow BEFORE the
                // respawn reset: the player revives in place at 20 percent
                // max HP, keeps their activity, and the ward consumes
                // itself. Pure int compare against a cached item id - zero
                // allocation on this combat path.
                if (!ConsumableEngine.TryInterceptLethalDamage(ref payload, effectiveMaxHp))
                {
                    payload.PlayerHp = effectiveMaxHp;
                    payload.CurrentMonsterId = 0;
                    payload.CurrentMonsterHp = 0;
                    payload.CombatTargetTickAccumulator = 0;
                    payload.ActiveActivityId = 0;
                    // Modul: halt reasons. A full-HP character sitting idle
                    // looked exactly like one that had never been deployed.
                    payload.ActivityHaltReason = Network.ActivityHaltReason.Died;
                    // Modul: lifetime statistics. The only place in the server
                    // where a player death is recognised, so the only place
                    // this can be counted. Intercepted lethal damage (the Death
                    // Ward branch above) is not a death and is not counted.
                    payload.LifetimeDeaths++;
                    payload.IsDirty = true;
                    return;
                }
            }

            if (payload.CurrentMonsterHp <= 0 && payload.ActiveActivityId > 0)
            {
                // Modul: Chilled/Vulnerable are scoped to the currently
                // fought monster - clear on kill/respawn so a status never
                // leaks onto the next monster.
                payload.TargetStatusEffectBitmask = 0;

                int finalXpMultiplier = localXpMultiplier;
                if (payload.CurrentLevel < 50 && payload.CachedMentorCount > 0)
                {
                    finalXpMultiplier += payload.CachedMentorCount * 5;
                }

                finalXpMultiplier += RaceMasteryResolver.GetHumanXpBonusPct(payload.HumanMasteryLevel);
            finalXpMultiplier += LegacyPerkResolver.GetXpBonusPct(payload.CachedLegacyPerks);
            finalXpMultiplier += InheritanceRegistry.GetBonusPct(payload.Inherit_XpGain);

                if (payload.ActiveMentorPlayerId > 0 && payload.MentorshipExpBonusMultiplier > 1.0)
                {
                    finalXpMultiplier = (int)(finalXpMultiplier * payload.MentorshipExpBonusMultiplier);
                }

                int seasonalCombatXp = activeMonster.BaseXpReward * finalXpMultiplier / 100;
                ProgressionEngine.ProcessMonsterDeath(ref payload, activeMonster.BaseXpReward, finalXpMultiplier, ActiveGlobalEventId, activeRaceId);

                // Modul: reaching a location unlocks its gathering. One kill is
                // the whole requirement - if you can fight here, you can work
                // here. Raised live as well as at hydration so the node list
                // opens up the moment the kill lands, not on next login.
                int killedLocation = ContentRegistry.GetCanonicalLocation(activeMonster.Id);
                if (killedLocation > payload.HighestLocationReached)
                {
                    payload.HighestLocationReached = killedLocation;
                }

                // Modul: region progression. Felling a region's boss opens the
                // next region - to enter, and to wear its gear. Raised live for
                // the same reason the line above is: the reward for a boss is
                // the door opening, and a door that opens on next login does
                // not read as a reward at all.
                //
                // Only ever raised, never recomputed from the codex here. The
                // codex write for this kill happens on CodexEngine's own cron
                // and has not landed yet, so asking it now would answer with
                // the state before the boss died. Hydration reconciles from the
                // codex; this keeps the live session honest in between.
                int clearedBossRegion = RaceUnlockRegistry.GetRegionForBossMonsterId(activeMonster.Id);
                if (clearedBossRegion > 0
                    && clearedBossRegion < RaceUnlockRegistry.LastRegion
                    && payload.HighestUnlockedRegion < clearedBossRegion + 1)
                {
                    payload.HighestUnlockedRegion = clearedBossRegion + 1;
                }

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
                // Modul: inheritance. A permanent, season-crossing multiplier.
                goldReward = (long)(goldReward * (1.0f + InheritanceRegistry.GetBonusPct(payload.Inherit_GoldGain) / 100f));
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

                // Modul: one shared definition. This site and CombatLootEngine
                // both used `% 6 == 0`, copied between them on the reasoning
                // that it kept "regional boss" consistent - which it did, at
                // the wrong monsters. See ContentRegistry.IsRegionalBoss.
                bool isRegionalBoss = ContentRegistry.IsRegionalBoss(activeMonster.Id);

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
                // (ContentRegistry.IsRegionalBoss, shared with Guild War
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
                    LootLuckPct = combatStats.LootLuckPct + InheritanceRegistry.GetBonusPct(payload.Inherit_LootLuck)
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
                payload.CurrentMonsterHp = (long)ContentRegistry.GetScaledMonsterMaxHp(payload.CurrentMonsterId) * 1000L;
                payload.CombatTargetTickAccumulator = 0;
            }
        }
    }
}
