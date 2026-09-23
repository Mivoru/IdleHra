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

        // Modul: the floor a clamped health subtraction lands on. PlayerHp is an
        // int in milli-HP and incoming damage is a long since the boss wall went
        // per-region, so a lethal hit can exceed the whole int range. Anything at
        // or below zero is death, so the exact depth is irrelevant - what matters
        // is that it cannot wrap round into a positive bar, which is how the
        // deadliest monster in the game once dealt 1 HP a hit.
        private const int MinimumRepresentableHp = int.MinValue / 2;
        private const double TickIntervalSeconds = TickIntervalMs / 1000.0;
        private readonly LootTableEngine _lootEngine;
        private readonly InheritanceEngine? _inheritanceEngine;
        private readonly HallOfAncestorsEngine? _hallOfAncestorsEngine;
        private readonly SkillTreeEngine? _skillTreeEngine;
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
        private readonly VillageManagementEngine _villageManagementEngine;
        private readonly GuildLogisticsEngine _guildLogisticsEngine;
        private readonly CraftingEngine _craftingEngine;

        // Modul: larder. Optional, matching the _equipmentSlotEngine /
        // _relationshipEngine convention, so the many test fixtures that
        // construct this engine directly keep compiling.
        private readonly LarderEngine? _larderEngine;
        private readonly WorldBossEngine _worldBossEngine;
        private readonly GuildWarEngine _guildWarEngine;
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

        // Modul: SafeDispatchAsync, handed to the tick coordinators as a value.
        //
        // A coordinator must never own its own async dispatch - CLAUDE.md's
        // cron-worker trap is that a bare Task.Run swallows its exception and
        // the feature simply stops existing, server-wide, with no log. So the
        // ONE implementation is passed down rather than reimplemented per
        // coordinator, and it is cached here rather than built per call: this
        // is a 10Hz loop, and a fresh delegate per drain per tick is an
        // allocation on the hot path for nothing.
        private readonly Action<string, long, Func<Task>> _safeDispatch;

        // Modul: the command dispatch table and the two session-ending
        // delegates its handlers are handed. Cached here, built once, for the
        // same reason as _safeDispatch above: the command loop runs at 10Hz
        // forever. The handlers are static coordinator methods, so the table
        // holds no reference to this instance; the two delegates below are
        // the ONLY way a handler can end a session, and both are this
        // class's own methods, so session lifecycle still has one owner.
        private readonly Action<long> _terminateSessionForSecurity;
        private readonly Action<long> _removeActivePlayer;

        // The three instance methods moved handlers dispatch to off the tick,
        // cached for the same reason. Each is called from inside the handler's
        // SafeDispatch lambda exactly as the inline branch called the method.
        private readonly Func<long, Task> _registerGuildDefense;
        private readonly Func<long, long, Guid, uint, bool, Task<(SyncMatchStateResponseBuffer Response, int ActiveMatchMmr)>> _submitShardAttack;
        private readonly Func<long, Task<bool>> _executePassPurchase;
        private readonly System.Collections.Generic.Dictionary<CommandType, CommandHandler> _commandHandlers = BuildCommandHandlers();
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
        /// <summary>
        /// The most a crit chance can ever be, in percent. A probability, so
        /// there is nothing above 100 - see the clamp's own note at the crit
        /// roll for why it had to be said out loud.
        /// </summary>
        public const float MaxCritChancePct = 100f;

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

        public SimulationEngine(LootTableEngine lootEngine, StateCheckpointManager checkpointManager, NetworkBroadcastSystem networkSystem, ForgeSplicingEngine forgeEngine, MarketOrderBookEngine marketEngine, PlayerSessionRegistry playerRegistry, GuildContributionEngine guildEngine, MarketEscrowEngine escrowEngine, MailboxAndBankEngine mailboxEngine, AffixRerollEngine rerollEngine, BreedingEngine breedingEngine, GuildLogisticsEngine guildLogisticsEngine, CraftingEngine craftingEngine, WorldBossEngine worldBossEngine, VillageManagementEngine villageManagementEngine, GuildWarEngine guildWarEngine, LegacyStoreEngine legacyStoreEngine, GuildLogisticsDepotEngine guildLogisticsDepotEngine, GuildCombatSimulationEngine guildCombatSimulationEngine, AntiCheatTelemetryEngine antiCheatTelemetryEngine, PushNotificationTriggerEngine pushNotificationTriggerEngine, CompliancePurgeEngine compliancePurgeEngine, BillingVerificationEngine billingVerificationEngine, StackExchange.Redis.IConnectionMultiplexer redis, Microsoft.EntityFrameworkCore.IDbContextFactory<FolkIdleDbContext> contextFactory, GuildRaidEngine? guildRaidEngine = null, EquipmentSlotEngine? equipmentSlotEngine = null, RelationshipEngine? relationshipEngine = null, LarderEngine? larderEngine = null, InheritanceEngine? inheritanceEngine = null, SkillTreeEngine? skillTreeEngine = null, HallOfAncestorsEngine? hallOfAncestorsEngine = null)
        {
            _lootEngine = lootEngine;
            _checkpointManager = checkpointManager;
            _networkSystem = networkSystem;
            _safeDispatch = SafeDispatchAsync;
            _terminateSessionForSecurity = TerminateSessionForSecurity;
            _removeActivePlayer = RemoveActivePlayer;
            _registerGuildDefense = RegisterGuildDefenseAsync;
            _submitShardAttack = SubmitShardAttackAsync;
            _executePassPurchase = ExecutePassPurchaseAsync;
            _forgeEngine = forgeEngine;
            _marketEngine = marketEngine;
            _playerRegistry = playerRegistry;
            _guildEngine = guildEngine;
            _escrowEngine = escrowEngine;
            _mailboxEngine = mailboxEngine;
            _rerollEngine = rerollEngine;
            _breedingEngine = breedingEngine;
            _guildLogisticsEngine = guildLogisticsEngine;
            _craftingEngine = craftingEngine;
            _larderEngine = larderEngine;
            _inheritanceEngine = inheritanceEngine;
            _skillTreeEngine = skillTreeEngine;
            _hallOfAncestorsEngine = hallOfAncestorsEngine;
            _worldBossEngine = worldBossEngine;
            _guildWarEngine = guildWarEngine;
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

        /// <summary>
        /// Commands the SERVER enqueues into its own command queue, which
        /// therefore carry a zeroed ClientCommandPacket rather than a client's
        /// synchronized one.
        ///
        /// Modul: LOGOUT BELONGS HERE AND WAS MISSING, WHICH COST EVERY PLAYER
        /// UP TO FIVE MINUTES ON EVERY DISCONNECT.
        ///
        /// No client sends opcode 6 - grep the web client, it has no Logout at
        /// all. Its only producer is NetworkBroadcastSystem's socket-closure
        /// finally block, and that packet leaves LogicEpochCounter at its
        /// default 0. Any payload past its fifth checkpoint is further from 0
        /// than EpochDriftTolerance, so the epoch gate read the server's own
        /// shutdown packet as a desynchronized client and answered with
        /// TerminateSessionForSecurity - which drops the session WITHOUT the
        /// flush the Logout handler exists to perform. Reported as attribute
        /// points reverting after F5; it was never only the attributes, and the
        /// telemetry it left behind said "split brain".
        /// </summary>
        public static bool IsServerInternalCommand(CommandType command)
            => command == CommandType.ReloadState || command == CommandType.Logout;

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

        // Modul: ComputeSkillCooldownRemainingMs is gone with the four active
        // skills it reported on. See SkillTreeRegistry - they were replaced by a
        // passive tree because, measured, they were +90% damage for clicking
        // every three seconds in a game whose premise is not clicking.

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

        internal static unsafe byte[] CopyDeviceTokenBytes(ref ClientCommandPacket packet)
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

        // Modul: the command dispatch table. One line per CommandType that has
        // left EngineLoop's if/else chain for a coordinator. The branches that
        // STAY inline (node migration, consumables and Login before the gate;
        // the challenge response, ChangeActivity, ReloadState and Logout after
        // it) are deliberately absent - see the comment on each of those
        // branches for why.
        private static System.Collections.Generic.Dictionary<CommandType, CommandHandler> BuildCommandHandlers()
        {
            return new System.Collections.Generic.Dictionary<CommandType, CommandHandler>
            {
                [CommandType.PurchaseLegacyUnlocks] = LegacyStoreTickCoordinator.HandlePurchaseLegacyUnlocks,
                [CommandType.MarketListItem] = MarketTickCoordinator.HandleMarketListOrBuy,
                [CommandType.MarketBuyItem] = MarketTickCoordinator.HandleMarketListOrBuy,
                [CommandType.PlaceLimitOrder] = MarketTickCoordinator.HandlePlaceLimitOrder,
                [CommandType.UpgradeBuilding] = VillageTickCoordinator.HandleUpgradeBuilding,
                [CommandType.EvictVillager] = VillageTickCoordinator.HandleEvictVillager,
                [CommandType.RecruitVillager] = VillageTickCoordinator.HandleRecruitOrDismissVillager,
                [CommandType.DismissNewcomer] = VillageTickCoordinator.HandleRecruitOrDismissVillager,
                [CommandType.ContributeToGuild] = GuildTickCoordinator.HandleContributeToGuild,
                [CommandType.ContributeGuildTreasury] = GuildTickCoordinator.HandleContributeGuildTreasury,
                [CommandType.DepositGuildMaterial] = GuildTickCoordinator.HandleDepositGuildMaterial,
                [CommandType.ContributeToWarSupply] = GuildWarTickCoordinator.HandleContributeToWarSupply,
                [CommandType.RegisterGuildDefense] = GuildWarTickCoordinator.HandleRegisterGuildDefense,
                [CommandType.SubmitShardAttack] = GuildWarTickCoordinator.HandleSubmitShardAttack,
                [CommandType.LaunchGuildRaid] = GuildWarTickCoordinator.HandleLaunchGuildRaid,
                [CommandType.ExecuteCombatTurn] = GuildWarTickCoordinator.HandleExecuteCombatTurn,
                [CommandType.AddFriend] = RelationshipTickCoordinator.HandleAddFriend,
                [CommandType.RemoveFriend] = RelationshipTickCoordinator.HandleRemoveFriend,
                [CommandType.BlockPlayer] = RelationshipTickCoordinator.HandleBlockPlayer,
                [CommandType.UnblockPlayer] = RelationshipTickCoordinator.HandleUnblockPlayer,
                [CommandType.ExecuteForgeFusion] = ForgeTickCoordinator.HandleExecuteForgeFusion,
                [CommandType.RerollItemAffix] = ForgeTickCoordinator.HandleRerollItemAffix,
                [CommandType.ExecuteBreeding] = BreedingTickCoordinator.HandleExecuteBreeding,
                [CommandType.ExecuteVillagerBreeding] = BreedingTickCoordinator.HandleExecuteVillagerBreeding,
                [CommandType.InitializeCrafting] = CraftingTickCoordinator.HandleInitializeCrafting,
                [CommandType.CraftItem] = CraftingTickCoordinator.HandleCraftItem,
                [CommandType.UpgradeTool] = CraftingTickCoordinator.HandleUpgradeTool,
                [CommandType.EquipItem] = EquipmentTickCoordinator.HandleEquipItem,
                [CommandType.UnequipItem] = EquipmentTickCoordinator.HandleUnequipItem,
                [CommandType.StockFoodSlot] = LarderTickCoordinator.HandleStockFoodSlot,
                [CommandType.UpdateAutoEatThreshold] = LarderTickCoordinator.HandleUpdateAutoEatThreshold,
                [CommandType.SpendAttributePoint] = AttributeTickCoordinator.HandleSpendAttributePoint,
                [CommandType.RespecAttributes] = AttributeTickCoordinator.HandleRespecAttributes,
                [CommandType.PurchaseSkillTreeLevel] = SkillTreeTickCoordinator.HandlePurchaseSkillTreeLevel,
                [CommandType.RespecSkillTree] = SkillTreeTickCoordinator.HandleRespecSkillTree,
                [CommandType.RequestUnlockSkill] = SkillTreeTickCoordinator.HandleRetiredActiveSkill,
                [CommandType.RequestCastSkill] = SkillTreeTickCoordinator.HandleRetiredActiveSkill,
                [CommandType.PurchaseInheritanceLevel] = InheritanceTickCoordinator.HandlePurchaseInheritanceLevel,
                [CommandType.PurchaseAncestorSlot] = InheritanceTickCoordinator.HandleHallOfAncestors,
                [CommandType.KeepAncestor] = InheritanceTickCoordinator.HandleHallOfAncestors,
                [CommandType.ReleaseAncestor] = InheritanceTickCoordinator.HandleHallOfAncestors,
                [CommandType.AssignCharacterSlot] = InheritanceTickCoordinator.HandleHallOfAncestors,
                [CommandType.ClaimBattlePassReward] = BillingTickCoordinator.HandleClaimBattlePassReward,
                [CommandType.PurchaseBattlePass] = BillingTickCoordinator.HandlePurchaseBattlePass,
                [CommandType.SubmitPurchaseReceipt] = BillingTickCoordinator.HandleSubmitPurchaseReceipt,
                [CommandType.SyncBillingStatus] = BillingTickCoordinator.HandleSyncBillingStatus,
                [CommandType.ClaimMailItem] = MailTickCoordinator.HandleClaimMailItem,
                [CommandType.ClaimAchievementReward] = MailTickCoordinator.HandleClaimAchievementReward,
                [CommandType.DepositToBank] = MailTickCoordinator.HandleRetiredBank,
                [CommandType.WithdrawFromBank] = MailTickCoordinator.HandleRetiredBank,
                [CommandType.AssignMentor] = MentorshipTickCoordinator.HandleRetiredMentorship,
                [CommandType.EstablishMentorship] = MentorshipTickCoordinator.HandleRetiredMentorship,
                [CommandType.TerminateMentorship] = MentorshipTickCoordinator.HandleRetiredMentorship,
                [CommandType.AttackWorldBoss] = WorldBossTickCoordinator.HandleAttackWorldBoss,
                [CommandType.ReportTelemetryBurst] = ClientSessionTickCoordinator.HandleReportTelemetryBurst,
                [CommandType.PingNetworkDiagnostics] = ClientSessionTickCoordinator.HandlePingNetworkDiagnostics,
                [CommandType.RegisterPushToken] = ClientSessionTickCoordinator.HandleRegisterPushToken,
                [CommandType.TriggerGdprPurge] = ClientSessionTickCoordinator.HandleTriggerGdprPurge,
                [CommandType.SwitchLanguage] = ClientSessionTickCoordinator.HandleSwitchLanguage,
                [CommandType.ReportUiContextSwitch] = ClientSessionTickCoordinator.HandleReportUiContextSwitch,
                [CommandType.SetSimulationSpeed] = ClientSessionTickCoordinator.HandleSetSimulationSpeed,
            };
        }

        // Built per dequeued command, on the stack: a ref struct cannot be
        // cached. Every value in it is either the routing id or a readonly
        // field/cached delegate of this instance.
        private CommandCoordinatorContext BuildCommandContext(long routingPlayerId)
        {
            return new CommandCoordinatorContext
            {
                RoutingPlayerId = routingPlayerId,
                SafeDispatch = _safeDispatch,
                TerminateSessionForSecurity = _terminateSessionForSecurity,
                RemoveActivePlayer = _removeActivePlayer,
                NetworkSystem = _networkSystem,
                PlayerRegistry = _playerRegistry,
                LegacyStoreEngine = _legacyStoreEngine,
                CheckpointManager = _checkpointManager,
                EscrowEngine = _escrowEngine,
                MarketEngine = _marketEngine,
                VillageManagementEngine = _villageManagementEngine,
                GuildLogisticsEngine = _guildLogisticsEngine,
                GuildEngine = _guildEngine,
                GuildLogisticsDepotEngine = _guildLogisticsDepotEngine,
                GuildWarEngine = _guildWarEngine,
                GuildRaidEngine = _guildRaidEngine,
                GuildCombatSimulationEngine = _guildCombatSimulationEngine,
                RegisterGuildDefense = _registerGuildDefense,
                SubmitShardAttack = _submitShardAttack,
                RelationshipEngine = _relationshipEngine,
                ForgeEngine = _forgeEngine,
                RerollEngine = _rerollEngine,
                BreedingEngine = _breedingEngine,
                CraftingEngine = _craftingEngine,
                EquipmentSlotEngine = _equipmentSlotEngine,
                LarderEngine = _larderEngine,
                SkillTreeEngine = _skillTreeEngine,
                InheritanceEngine = _inheritanceEngine,
                HallOfAncestorsEngine = _hallOfAncestorsEngine,
                LiveSessionContexts = _liveSessionContexts,
                ExecutePassPurchase = _executePassPurchase,
                BillingVerificationEngine = _billingVerificationEngine,
                ContextFactory = _contextFactory,
                MailboxEngine = _mailboxEngine,
                WorldBossEngine = _worldBossEngine,
                TelemetryStreamingEngine = _telemetryStreamingEngine,
                PushNotificationTriggerEngine = _pushNotificationTriggerEngine,
                CompliancePurgeEngine = _compliancePurgeEngine,
            };
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

                MarketTickCoordinator.DrainMatchNotifications(_playerRegistry, _activePlayers, _safeDispatch, _contextFactory);

                BreedingTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                WorldBossTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                RaceProgressionTickCoordinator.DrainMasteryUpdates(_playerRegistry, _activePlayers);

                ForgeTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

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
                        currentPayload.EquippedWeaponKind = equipUpdate.EquippedWeaponKind;
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

                CodexTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                RegionProgressionTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                RaceProgressionTickCoordinator.DrainRaceUnlocks(_playerRegistry, _activePlayers);

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
                // Modul: reloaded payloads, applied on the thread that owns
                // the dictionary. Drained before the activity queue so a
                // reload cannot land on top of an activity change made in the
                // same frame and undo it.
                while (_playerRegistry.StateReloadQueue.TryDequeue(out var reloadedPayload))
                {
                    if (_activePlayers.ContainsKey(reloadedPayload.PlayerId))
                    {
                        // Modul: THE RELOAD USED TO ERASE THE ANSWER TO THE
                        // COMMAND THAT CAUSED IT.
                        //
                        // A reroll, a fusion, a market trade, a village upgrade
                        // and a craft all end by enqueuing ReloadState, and this
                        // replaces the live payload with one built from the
                        // database. The command result ring lives on the payload
                        // and in no table, and the player is suspended until the
                        // reload lands - so the server's reply was written into
                        // the ring, never broadcast, and then overwritten with
                        // zeros.
                        //
                        // Measured in a browser: a reroll changed the affix, took
                        // the gold, and produced no message of any kind - not for
                        // a single reroll, not for a fifty-attempt run, and not
                        // even for a stop condition the server refused outright.
                        ref var livePayload = ref System.Runtime.InteropServices.CollectionsMarshal
                            .GetValueRefOrNullRef(_activePlayers, reloadedPayload.PlayerId);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref livePayload))
                        {
                            StateReloadMerge.CarryLiveOnlyFields(in livePayload, ref reloadedPayload);
                        }

                        AddActivePlayer(reloadedPayload);
                    }
                }

                while (_playerRegistry.ActivityChangeQueue.TryDequeue(out var activityChange))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, activityChange.PlayerId);
                    if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        continue;
                    }

                    // Modul: LastClientCommandAtMs used to be stamped here and
                    // nowhere else, which made the anti-cheat's "was this client
                    // talking" test answer for activity changes only. It is
                    // stamped at the command routing site now - one writer, on
                    // the path every client command actually takes.

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
                //
                // (The crafting drain that used to sit here, with its own
                // "Modul: crafting as an assignable job" comment, moved to
                // CraftingTickCoordinator.cs along with that comment - kept in
                // one place now instead of two, per code review on Task 1.20.)
                CraftingTickCoordinator.DrainCraftingTicks(_safeDispatch, _craftingEngine);

                LarderTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                GuildFanoutTickCoordinator.DrainWarScoreboard(_playerRegistry, _activePlayers, _guildMembersIndex);

                InventoryCensusTickCoordinator.DrainCensus(_playerRegistry, _activePlayers);

                InventoryCensusTickCoordinator.DrainLootDrops(_playerRegistry, _activePlayers);

                VillageChestTickCoordinator.DrainAutoSalvage(_playerRegistry, _activePlayers);

                VillageChestTickCoordinator.DrainChestSaleGold(_playerRegistry, _activePlayers);

                VillageChestTickCoordinator.DrainChestSettings(_playerRegistry, _activePlayers);

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

                CraftingTickCoordinator.DrainCraftingCompletions(_playerRegistry, _activePlayers, _guildWarEngine.GuildWarPointQueue);

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

                CommandResultTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                GuildFanoutTickCoordinator.DrainGuildUpdates(_playerRegistry, _activePlayers, _guildMembersIndex);

                VillageTickCoordinator.DrainInfrastructureUpdates(_playerRegistry, _activePlayers);

                VillageTickCoordinator.DrainRecruitmentUpdates(_playerRegistry, _activePlayers);

                MentorshipTickCoordinator.DrainMentorshipUpdates(_playerRegistry, _activePlayers);

                while (_playerRegistry.QuarantineNotificationQueue.TryDequeue(out var quarantineNotification))
                {
                    ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, quarantineNotification.PlayerId);
                    if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                    {
                        currentPayload.Quarantine_Active = true;
                        currentPayload.IsQuarantined = true;
                    }
                }

                LegacyStoreTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                InheritanceTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                SkillTreeTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                BillingTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                GuildFanoutTickCoordinator.DrainLogisticsDepotUpdates(_playerRegistry, _activePlayers, _guildMembersIndex);

                GuildFanoutTickCoordinator.DrainCombatSimulationUpdates(_playerRegistry, _activePlayers, _guildMembersIndex);

                GuildFanoutTickCoordinator.DrainRaidBossUpdates(_playerRegistry, _activePlayers, _guildMembersIndex);

                MentorshipTickCoordinator.DrainContractUpdates(_playerRegistry, _activePlayers);

                MailTickCoordinator.DrainClaimRequests(_playerRegistry, _activePlayers, _safeDispatch, _mailboxEngine);

                while (_networkSystem.CommandQueue.TryDequeue(out var cmdWrapper))
                {
                    var cmd = cmdWrapper.Packet;
                    long routingPlayerId = cmdWrapper.PlayerId;

                    // Modul: THESE THREE STAY INLINE AND RUN BEFORE THE GATE,
                    // deliberately not in the dispatch table. Node migration,
                    // consumables and Login either have no live payload to gate
                    // against yet or are cross-shard session infrastructure;
                    // moving them behind CommandGate would change what is
                    // validated, and moving them to a coordinator would hand it
                    // session lifecycle.
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

                    // Modul: the anti-cheat / epoch gate lives in CommandGate
                    // now, with its comments; the order of its four checks is
                    // the contract and CommandGateOrderingTests pins it. The
                    // verdict is ACTED ON here because ending a session is
                    // SimulationEngine's own work, not the gate's.
                    switch (CommandGate.Evaluate(ref currentPayload, ref cmd))
                    {
                        case CommandGateVerdict.Terminate:
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        case CommandGateVerdict.ShadowBan:
                            _antiCheatTelemetryEngine?.RequestShadowBan(routingPlayerId, 54, 2);
                            continue;
                    }

                    // Modul: the dispatch table is tried FIRST, then the
                    // branches that deliberately stay inline below. Each
                    // CommandType appears in exactly one of the two places, so
                    // the order between them decides nothing. `continue` after
                    // the handler is what every moved branch's own `continue`
                    // meant: nothing follows this chain in the loop body.
                    if (_commandHandlers.TryGetValue(cmd.Command, out var commandHandler))
                    {
                        var commandContext = BuildCommandContext(routingPlayerId);
                        commandHandler(ref currentPayload, ref cmd, in commandContext);
                        continue;
                    }

                    if (cmd.Command == CommandType.AntiCheatChallengeResponse)
                    {
                        // Modul: STAYS INLINE, deliberately not in the dispatch
                        // table. It is the anti-cheat machinery the gate above
                        // belongs to, and it quarantines and shadow-bans -
                        // powers that stay with SimulationEngine.
                        //
                        // Modul: A LATE ANSWER IS NOT A CONFESSION.
                        //
                        // This branch used to quarantine PERMANENTLY on the
                        // first rejected answer, while a challenge that went
                        // completely unanswered only advanced a counter and
                        // needed four in a row. Backwards: the client that
                        // says nothing is the one there is no evidence about,
                        // and the client that replies a moment late is almost
                        // always racing the window rather than cheating.
                        //
                        // It fired on a real account mid auto-reroll - a burst
                        // of perfectly legitimate commands, one reply landing
                        // after the window closed - and stopped that account
                        // dead with no way back short of editing the database.
                        //
                        // Three outcomes now. Stale is ignored outright: an
                        // answer to a challenge that is already closed carries
                        // no information, because an honest client and a
                        // dishonest one produce it identically. A genuinely
                        // wrong answer joins the same consecutive run a miss
                        // does, so acting on it needs a PATTERN.
                        var verdict = ClientCommandValidator.JudgeAntiCheatChallengeResponse(ref currentPayload, ref cmd);

                        if (verdict == ClientCommandValidator.ChallengeAnswerVerdict.Stale)
                        {
                            continue;
                        }

                        if (verdict == ClientCommandValidator.ChallengeAnswerVerdict.Rejected)
                        {
                            if (++currentPayload.ConsecutiveChallengeMisses
                                >= AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit)
                            {
                                currentPayload.IsQuarantined = true;
                                currentPayload.Quarantine_Active = true;
                                _antiCheatTelemetryEngine?.RequestShadowBan(routingPlayerId, 54, 3);
                            }
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
                    else if (cmd.Command == CommandType.ChangeActivity)
                    {
                        // Modul: STAYS INLINE, deliberately not in the dispatch
                        // table. It drives the active register (ActivityChangeQueue,
                        // ApplyActivityChangeToPayload) - the swap discipline
                        // ProcessAllSlotSubTicks depends on, which no coordinator
                        // owns.
                        //
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
                    else if (cmd.Command == CommandType.ReloadState)
                    {
                        // Modul: STAYS INLINE, deliberately not in the dispatch
                        // table. Server-internal session lifecycle: its result
                        // lands through StateReloadQueue, whose drain calls
                        // AddActivePlayer - the pair belongs with whatever owns
                        // _activePlayers.
                        //
                        // Modul: RELOAD NOW ACTUALLY RELOADS.
                        //
                        // This set IsSuspended = false and nothing else. Every
                        // engine that changes the database out-of-band - reroll,
                        // fusion, market, village, crafting - finishes by
                        // enqueuing ReloadState precisely so the live payload
                        // picks the change up. None of them did. The in-memory
                        // payload kept its old gold, its old inventory counts
                        // and its old equipment, and the only way to see the
                        // truth was to sign in again.
                        //
                        // Reported as "I have to press F5 for the gold to
                        // update, and a reroll does not deduct straight away".
                        // Exactly right, and it was never only gold.
                        //
                        // Modul: FLUSH FIRST, THEN RELOAD - and the first
                        // version of this did not, which broke combat.
                        //
                        // The reasoning was "the command that scheduled the work
                        // already suspended and flushed, so the database holds
                        // this player's tick state". True of reroll, fusion and
                        // the market; NOT true of every site that enqueues a
                        // ReloadState. Where it was not true the reload replaced
                        // the live payload with an older one and threw away
                        // whatever the tick held - which a player meets as
                        // "deployed to Wild Boar, and nothing is happening",
                        // because the activity they had just chosen was in
                        // memory and nowhere else.
                        //
                        // Flushing first is safe precisely BECAUSE gold is
                        // persisted as a delta rather than an absolute (see
                        // StateCheckpointManager's own comment on
                        // RedisPendingGoldDelta): writing this payload out
                        // cannot overwrite the deduction an engine just made,
                        // it only banks the coins earned since the last flush.
                        // A second flush after an already-flushed command is a
                        // no-op, because the delta has been zeroed.
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);

                        // Modul: THE RESULT COMES BACK THROUGH A QUEUE, because
                        // the tick thread owns _activePlayers.
                        //
                        // It is a plain Dictionary, and the first version of
                        // this wrote the reloaded payload into it directly from
                        // the reload task - a data race against the tick
                        // iterating that same dictionary. Every other
                        // cross-thread hand-off in this file goes through a
                        // queue for exactly this reason; see ActivityChangeQueue
                        // and the comment on the Deploy path, which fixed this
                        // same class of bug and produced this same symptom.
                        //
                        // The player stays suspended until the reload lands,
                        // which is correct - their state is in flight - and the
                        // drain clears it.
                        long reloadPlayerId = currentPayload.PlayerId;
                        SafeDispatchAsync("ReloadState", reloadPlayerId, async () => {
                            var reloaded = await _checkpointManager.LoadPlayerState(reloadPlayerId);
                            reloaded.IsSuspended = false;
                            _playerRegistry.StateReloadQueue.Enqueue(reloaded);
                        });
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
                        // Modul: STAYS INLINE, deliberately not in the dispatch
                        // table. Server-internal session lifecycle: it is the
                        // flush-and-remove path CLAUDE.md records as broken by
                        // the epoch gate once already, and removing a player
                        // is SimulationEngine's job, not a coordinator's.
                        currentPayload.LastLogoutTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        currentPayload.IsDirty = true;
                        _checkpointManager.FlushStateAndAdvance(ref currentPayload);
                        currentPayload.IsSuspended = true;
                        // Modul: RemoveActivePlayer now clears
                        // PlayerSessionRegistry registration itself - see
                        // its own doc comment.
                        RemoveActivePlayer(routingPlayerId);
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

                            // Modul: A SILENT CLIENT IS A BACKGROUNDED ONE, NOT
                            // A CHEATING ONE.
                            //
                            // This is an IDLE game, largely played on phones,
                            // and a phone that locks its screen or switches
                            // apps throttles the tab's timers - so a client
                            // that is behaving perfectly cannot answer inside a
                            // fifteen-second window. Four of those in a row is
                            // four ordinary interruptions, and it was a
                            // PERMANENT quarantine: the account stopped
                            // simulating entirely, silently, and the player's
                            // only clue was that nothing happened.
                            //
                            // Confirmed from the live log - "QUARANTINE applied
                            // to player 8 (reason 54, detail 4)" - on a real
                            // account, playing normally, twice.
                            //
                            // A cheating client cannot stay silent: faking
                            // state is pointless unless it also SENDS
                            // something. So a miss only counts against a client
                            // that was otherwise talking during the window.
                            // Silence resets the run rather than building it -
                            // there is no evidence either way, and this
                            // codebase has already shipped one anti-cheat that
                            // punished people for how they play.
                            bool clientWasTalking =
                                Environment.TickCount64 - currentPayload.LastClientCommandAtMs
                                    <= AntiCheatTelemetryEngine.ChallengeResponseWindowMs;

                            if (!clientWasTalking)
                            {
                                currentPayload.ConsecutiveChallengeMisses = 0;
                            }
                            else if (++currentPayload.ConsecutiveChallengeMisses
                                     >= AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit)
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
                            var broadcastCombatStats = StatsCalculator.Calculate(currentPayload.STR, currentPayload.DEX, currentPayload.CON, currentPayload.LCK, currentPayload.ActiveOffensivePotionId, currentPayload.ActiveDefensivePotionId, broadcastActiveAgePhase, currentPayload.CompletedAreaFlags, broadcastActiveRaceId, currentPayload.HumanMasteryLevel, currentPayload.VilaMasteryLevel, currentPayload.DraugrMasteryLevel, currentPayload.CachedAffixTotals, currentPayload.IsEpicMutation, TraitTotals.From(currentPayload.TraitMask), currentPayload.CachedSetIds);

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

                                // Modul: the same call the spawn makes, so the
                                // bar's maximum and the monster's actual health
                                // come from one rule. The client used to
                                // hand-copy this as `MaxHp * 5` and get First
                                // Blood and endgame scaling wrong.
                                CurrentMonsterMaxHp = currentPayload.CurrentMonsterId > 0
                                    ? (int)BossFirstClearRules.MaxHpFor(
                                        currentPayload.DefeatedRegionBossMask,
                                        currentPayload.CurrentMonsterId,
                                        currentPayload.Skill_FirstBlood)
                                    : 0,
                                PlayerMaxHp = (int)(currentPayload.CachedEffectiveMaxHp / 1000L),
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
                                DefeatedRegionBossMask = currentPayload.DefeatedRegionBossMask,
                                HumanMasteryLevel = currentPayload.HumanMasteryLevel,
                                VilaMasteryLevel = currentPayload.VilaMasteryLevel,
                                DraugrMasteryLevel = currentPayload.DraugrMasteryLevel,
                                VillagePopulation = currentPayload.VillagePopulation,
                                AccumulatedTimeBankMs = currentPayload.AccumulatedTimeBankMs,
                                LogicEpochCounter = (uint)(currentPayload.LogicEpochCounter & 0xFFFFFFFF),
                                PremiumCurrencyBalance = (uint)currentPayload.PremiumCurrency,
                                LegacyShardBalance = currentPayload.LegacyShardBalance,
                                CurrentSimulationSpeedMultiplier = (byte)Math.Clamp(currentPayload.SpeedMultiplier, 1, 4),
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
                                WorldBossSessionEndsEpoch = currentPayload.WorldBossSessionEndsEpoch,
                                WorldBossEventState = _worldBossEngine.EventState,
                                WorldBossEventEndEpoch = _worldBossEngine.EventEndEpoch,
                                WorldBossBrokenPlateMask = _worldBossEngine.BrokenPlateMask,
                                WorldBossWeakPlate = _worldBossEngine.WeakPlate,
                                GuildLogisticsLevel = currentPayload.CachedGuildLogisticsLevel,
                                GuildRaidTier = currentPayload.CachedGuildRaidTier,
                                GuildRaidBossCurrentHp = currentPayload.CachedGuildRaidBossCurrentHp,
                                GuildRaidBossMaxHp = currentPayload.CachedGuildRaidBossMaxHp,
                                LumberjackLevel = currentPayload.LumberjackLevel,
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
                                AvailableSkillPoints = currentPayload.AvailableSkillPoints,
                                UnspentAttributePoints = currentPayload.UnspentAttributePoints,
                                SkillTree_LootRarity = currentPayload.Skill_LootRarity,
                                SkillTree_WorldBossDamage = currentPayload.Skill_WorldBossDamage,
                                SkillTree_CritChance = currentPayload.Skill_CritChance,
                                SkillTree_CritDamage = currentPayload.Skill_CritDamage,
                                SkillTree_XpGain = currentPayload.Skill_XpGain,
                                SkillTree_Plenty = currentPayload.Skill_Plenty,
                                SkillTree_Rarity = currentPayload.Skill_Rarity,
                                SkillTree_FirstBlood = currentPayload.Skill_FirstBlood,
                                SkillTree_TrophyHunter = currentPayload.Skill_TrophyHunter,
                                SkillTree_Guile = currentPayload.Skill_Guile,
                                SkillTree_Relentless = currentPayload.Skill_Relentless,
                                SkillTree_Bloodthirst = currentPayload.Skill_Bloodthirst,
                                SkillTree_Fortitude = currentPayload.Skill_Fortitude,
                                SkillTree_Craft = currentPayload.Skill_Craft,
                                SkillTree_Harvest = currentPayload.Skill_Harvest,
                                SkillTree_GoldenFleece = currentPayload.Skill_GoldenFleece,
                                SkillTree_Thunderer = currentPayload.Skill_Thunderer,
                                SkillTree_DoubleStrike = currentPayload.Skill_DoubleStrike,
                                SkillTree_LastStand = currentPayload.Skill_LastStand,
                                SkillTree_Scholar = currentPayload.Skill_Scholar,
                                FreeRespecUsed = currentPayload.FreeRespecUsed,
                                PaidRespecGrants = currentPayload.PaidRespecGrants,
                                Aptitude_Strength = currentPayload.Aptitude_Strength,
                                Aptitude_Skill = currentPayload.Aptitude_Skill,
                                Aptitude_Endurance = currentPayload.Aptitude_Endurance,
                                Aptitude_Fortune = currentPayload.Aptitude_Fortune,
                                // Modul: the achievement-toast signal. Pure
                                // functions of counters already on the
                                // payload, so this costs three comparisons
                                // and no DB read. See StateUpdatePacket.
                                AchievementTierTotal = (byte)(
                                    Engine.AchievementMilestones.EvaluateTreasuryTier(currentPayload.CurrentGold)
                                    + Engine.AchievementMilestones.EvaluateForgingTier(
                                        currentPayload.ForgeUpgradeCount, currentPayload.HighestForgeSynthesisTier)
                                    + Engine.AchievementMilestones.EvaluateLogisticsTier(currentPayload.HarvestLoopCount)),
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
                                OfflineMaterialsLostToFullWarehouse = currentPayload.OfflineMaterialsLostToFullWarehouse,
                                OfflineSummaryTick = currentPayload.OfflineSummaryTick,
                                // Modul: the victory and death cards. Copied
                                // straight across like the offline summary
                                // above - the tick bytes are edges the client
                                // compares against its own last-seen value.
                                LastVictoryMonsterId = currentPayload.LastVictoryMonsterId,
                                LastVictoryDurationSeconds = currentPayload.LastVictoryDurationSeconds,
                                LastVictoryGold = currentPayload.LastVictoryGold,
                                LastVictoryXp = currentPayload.LastVictoryXp,
                                LastVictoryTick = currentPayload.LastVictoryTick,
                                LastDeathMonsterId = currentPayload.LastDeathMonsterId,
                                LastDeathTick = currentPayload.LastDeathTick,
                                LastHitWasCrit = currentPayload.LastHitWasCrit,
                                EquippedWeaponKind = currentPayload.EquippedWeaponKind,
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

        // Modul: the instant time-warp subsystem lived here and is gone with
        // the chrono bank that funded it - ActivateChronoAcceleration,
        // ExecuteInstantTimeWarp, ApplyGatheringWarp, ApplyCombatWarp and the
        // six helpers only they called (ConsumeFoodStock,
        // CalculateIntegratedBuffMultiplier, EstimateTicksPerKill,
        // CalculateExpectedWarpDrops, CalculateExpectedCombatWarpDrops,
        // ConsumeInventorySlots). All of it was reachable only from commands 47
        // and 48, which no longer exist.
        //
        // Deleted rather than left unreferenced on purpose: this codebase has
        // twice shipped a "dead code" list naming files that turned out to be
        // already deleted, and an orphaned analytic projection of the tick is
        // exactly the thing that drifts silently from the tick it mirrors.

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

            long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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

            if (payload.Quarantine_Active)
            {
                // Say so. See ActivityHaltReason.Quarantined - this returned
                // silently, which is how an account could sit deployed and
                // motionless with the screen unable to explain why.
                payload.ActivityHaltReason = Network.ActivityHaltReason.Quarantined;
                return;
            }

            ProcessPassiveVillageTick(ref payload, TickIntervalSeconds, now);
            ProcessAllSlotSubTicks(ref payload, localXpMultiplier, localDropMultiplier, _guildWarEngine.GuildWarPointQueue, _liveSessionContexts);

            // Modul: the chrono-funded 2x/4x branch used to sit here, running the
            // whole sub-tick body N times for free and charging the bank. It is
            // gone with the bank. What remains below is the OTHER acceleration
            // path, which is unrelated and must not be confused with it: it pays
            // for every extra iteration out of AccumulatedTimeBankMs at 100ms
            // each, so it can only ever replay time the server already owed the
            // player. SpeedMultiplier belongs to that path, not to chrono.
            //
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
            // Modul: the thresholds live in AgePhaseCurve now. They used to be
            // four literals here and four more in OfflineSimulationEngine,
            // under a comment promising the two would stay identical.
            int newPhase = AgePhaseCurve.PhaseFor(ageTicks);

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
            // Modul: mana regen removed with the skills that spent it. It was
            // never a constraint anyway - 10 a second against a rotation that
            // wanted 12 - which is part of why those skills were as strong as
            // they were.

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
                    TraitTotals.From(payload.TraitMask), payload.CachedSetIds);

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

        /// <summary>
        /// How hard this character hits, in milli, before any per-swing roll.
        ///
        /// ONE AUTHORITY, deliberately. This used to live inline inside the
        /// player-attack branch, which was fine while the live tick was the
        /// only thing that swung. The world boss now needs the same figure -
        /// it used to take a damage number the CLIENT computed about itself -
        /// and two copies of "how hard does this player hit" is exactly the
        /// shape of defect this codebase keeps paying for.
        ///
        /// The crit multiplier is NOT applied here: it is rolled per swing and
        /// belongs to the swing, not to the character.
        /// </summary>
        private static long EffectiveMilliAttackFor(ref TickStatePayload payload, in CombatStats combatStats, int damageScalePerLevelPct)
        {
            long effective = StatsCalculator.ComputeEffectiveMilliAttack(
                in combatStats,
                damageScalePerLevelPct,
                payload.CurrentLevel,
                InheritanceRegistry.GetBonusPct(payload.Inherit_Damage)
                    + (FolkIdle.Server.Engine.GuildBonusesCache.GetBuffTier(payload.GuildId, "Damage") * 2));

            // Modul: Prestige "combat speed" perk (LegacyPerkResolver) -
            // applied as a flat percent boost to effective damage output per
            // attack rather than to the attack-interval tick cadence itself
            // (AttackIntervalMs governs the shared per-monster pacing loop and
            // is not a per-player value), which is a materially equivalent DPS
            // increase without touching that shared cadence math.
            int legacyCombatSpeedBonusPct = LegacyPerkResolver.GetCombatSpeedBonusPct(payload.CachedLegacyPerks);
            if (legacyCombatSpeedBonusPct > 0)
            {
                effective += (effective * legacyCombatSpeedBonusPct) / 100;
            }

            // Modul: the bloodline - the Strength aptitude, then attack traits -
            // on the pre-armour figure. Shared with the offline projection, which
            // used to skip Strength entirely: see BloodlineBonuses.
            effective = BloodlineBonuses.ApplyAttack(effective, payload.Aptitude_Strength, TraitTotals.From(payload.TraitMask));

            return effective;
        }

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
        /// <summary>
        /// Ticks between auto-eat bites, at 10 Hz - so one bite every two and a
        /// half seconds.
        ///
        /// Chosen against the monster cadence rather than picked round: a
        /// monster swings every two seconds, so a player recovers roughly one
        /// bite for every hit taken. Sustain, and a pace at which armour and
        /// health decide whether the bar holds - which was the whole point of
        /// bounding it.
        /// </summary>
        internal const int AutoEatCooldownTicks = 25;

        /// <summary>
        /// One hour at 10 Hz - Last Stand's cooldown. See the crown's use site;
        /// without it the effect is flat immortality rather than a reprieve.
        /// </summary>
        internal const int LastStandCooldownTicks = 36_000;

        /// <summary>Kills between Golden Fleece payouts.</summary>
        internal const int GoldenFleeceKillInterval = 100;

        /// <summary>How far above its due a Golden Fleece drop rolls.</summary>
        internal const int GoldenFleeceBonusTiers = 2;

        /// <summary>
        /// Thunderer, the Giantslayer crown: a boss fight opens with a free
        /// blow at five times the weapon.
        ///
        /// REUSES PendingSkillDamageMultiplier, which the four removed active
        /// skills left behind - the field was still on the payload and still
        /// consumed on the next hit, with nothing left in the game to set it.
        /// So the crown needed a mechanism that already existed and worked,
        /// and this is it. Expressed as a multiplier on the next swing rather
        /// than as an out-of-band hit, so it goes through the same to-hit,
        /// armour, lifesteal and kill-check path everything else does.
        /// </summary>
        private static void ArmThundererIfBoss(ref TickStatePayload payload)
        {
            if (payload.Skill_Thunderer <= 0) return;
            if (RaceUnlockRegistry.GetRegionForBossMonsterId(payload.CurrentMonsterId) <= 0) return;

            payload.PendingSkillDamageMultiplier = SkillTreeRegistry.GetBonusPercent(
                SkillTreeRegistry.CrownThunderer, payload.Skill_Thunderer) / 100f;
        }

        // Modul: widened from private to internal so the Progression-domain
        // tick coordinators (InheritanceTickCoordinator, SkillTreeTickCoordinator)
        // can call this without duplicating the switch. No behaviour change -
        // same assembly, same tick-thread-only call graph.
        internal static void SetSkillTreeLevel(ref TickStatePayload payload, int branchId, byte level)
        {
            switch (branchId)
            {
                case SkillTreeRegistry.BranchLootRarity: payload.Skill_LootRarity = level; break;
                case SkillTreeRegistry.BranchWorldBossDamage: payload.Skill_WorldBossDamage = level; break;
                case SkillTreeRegistry.BranchCritChance: payload.Skill_CritChance = level; break;
                case SkillTreeRegistry.BranchCritDamage: payload.Skill_CritDamage = level; break;
                case SkillTreeRegistry.BranchXpGain: payload.Skill_XpGain = level; break;
                case SkillTreeRegistry.BoughPlenty: payload.Skill_Plenty = level; break;
                case SkillTreeRegistry.BoughRarity: payload.Skill_Rarity = level; break;
                case SkillTreeRegistry.BoughFirstBlood: payload.Skill_FirstBlood = level; break;
                case SkillTreeRegistry.BoughTrophyHunter: payload.Skill_TrophyHunter = level; break;
                case SkillTreeRegistry.BoughGuile: payload.Skill_Guile = level; break;
                case SkillTreeRegistry.BoughRelentless: payload.Skill_Relentless = level; break;
                case SkillTreeRegistry.BoughBloodthirst: payload.Skill_Bloodthirst = level; break;
                case SkillTreeRegistry.BoughFortitude: payload.Skill_Fortitude = level; break;
                case SkillTreeRegistry.BoughCraft: payload.Skill_Craft = level; break;
                case SkillTreeRegistry.BoughHarvest: payload.Skill_Harvest = level; break;
                case SkillTreeRegistry.CrownGoldenFleece: payload.Skill_GoldenFleece = level; break;
                case SkillTreeRegistry.CrownThunderer: payload.Skill_Thunderer = level; break;
                case SkillTreeRegistry.CrownDoubleStrike: payload.Skill_DoubleStrike = level; break;
                case SkillTreeRegistry.CrownLastStand: payload.Skill_LastStand = level; break;
                case SkillTreeRegistry.CrownScholar: payload.Skill_Scholar = level; break;
            }
        }

        // Modul: widened from private to internal so InheritanceTickCoordinator
        // (Domain.Progression) can call this. No behaviour change.
        internal static void SetInheritanceLevel(ref TickStatePayload payload, int statId, byte level)
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
                RunCraftingProgressTick(ref payload, in craftingRecipe);

                // Modul: dispatch exclusivity, 2026-09-17. This branch had no
                // return, unlike the gathering branch immediately below it -
                // so a crafting character fell through into full combat
                // resolution every tick, against fallbackId 1 (every crafting
                // ActivityId lives in ActivityIdBands.CraftingBand, 5000+,
                // always above ContentRegistry.Monsters.Length). The craft
                // itself completed correctly; a whole silent second combat
                // session ran alongside it - real damage, XP, gold, loot,
                // kills - for every character ever assigned to a crafting
                // job. See ProcessSubTickDispatchTests for the regression
                // guard.
                return;
            }
            else if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out var gatheringNode))
            {
                RunGatheringTick(ref payload, in gatheringNode, localDropMultiplier);
                return;
            }

            RunCombatTick(ref payload, localXpMultiplier, localDropMultiplier, guildWarPointQueue, liveSessionContexts);
        }

        /// <summary>
        /// Monster combat resolution for one tick - spawn, the player's swing,
        /// the monster's reply, auto-eat, death, and kill rewards. Extracted
        /// verbatim from ProcessSubTick, where it is what runs when neither
        /// activity branch above it claimed the tick.
        ///
        /// Modul: THE EARLY `return` IN THE DEATH BRANCH IS LOAD-BEARING. It
        /// is the only thing that keeps "died this tick" and "killed something
        /// this tick" mutually exclusive within one call - the kill-reward
        /// block below is reached only because a death returned before it. It
        /// sits inside the `!TryInterceptLethalDamage` block on purpose, so a
        /// Death Ward intercept is NOT a death and can still land a kill in
        /// the same tick. Do not split this into always-both-called
        /// ResolveDeath/ResolveKillReward methods; the exclusivity would have
        /// to be reproduced explicitly and there is nothing today that would
        /// catch it if it were reproduced wrongly. Returning from THIS method
        /// is the same as returning from ProcessSubTick only because this call
        /// is the last statement there - keep it last.
        ///
        /// Callable ONLY from ProcessSubTick. The slot register's
        /// swap/try/finally discipline lives one level up in
        /// ProcessAllSlotSubTicks; a caller that bypasses it leaves the wrong
        /// character's gear, HP and activity "active" for every subsequent
        /// read after any throw in here.
        /// </summary>
        private static void RunCombatTick(
            ref TickStatePayload payload,
            int localXpMultiplier,
            int localDropMultiplier,
            System.Collections.Concurrent.ConcurrentQueue<GuildWarPointEvent> guildWarPointQueue,
            System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext> liveSessionContexts)
        {
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

            var combatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, activeAgePhase, payload.CompletedAreaFlags, activeRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation, TraitTotals.From(payload.TraitMask), payload.CachedSetIds);

            // Modul: the base pool is a CURVE now, not a constant - see
            // ProgressionEngine.BaseMilliHpForLevel. A flat 100 against monster
            // attack that goes up 4.2x a region is why region 5 one-shot
            // everybody.
            long baseMilliHp = ProgressionEngine.BaseMilliHpForLevel(payload.CurrentLevel);
            long effectiveMilliHp = baseMilliHp + (baseMilliHp * lineage.HpScalePerLevelPct * payload.CurrentLevel / 100) + (combatStats.MaxHp * 1000L);
            // Modul: inheritance, applied to the whole pool for the same reason
            // the damage bonus is applied last - a flat addition would stop
            // mattering.
            effectiveMilliHp += effectiveMilliHp * InheritanceRegistry.GetBonusPct(payload.Inherit_MaxHp) / 100L;
            // Modul: Fortitude, the Cruelty bough - more health, layered the
            // same additive-percent way inheritance is just above.
            effectiveMilliHp += effectiveMilliHp * (long)SkillTreeRegistry.GetBonusTenthsOfPercent(
                SkillTreeRegistry.BoughFortitude, payload.Skill_Fortitude) / 1000L;

            // Modul: the bloodline's health - Endurance, then health traits -
            // layered the same additive-percent way as inheritance and the tree
            // above. Shared with the offline projection: see BloodlineBonuses.
            effectiveMilliHp = BloodlineBonuses.ApplyMaxHp(effectiveMilliHp, payload.Aptitude_Endurance, TraitTotals.From(payload.TraitMask));
            int effectiveMaxHp = (int)effectiveMilliHp;

            // Modul: and the client is told what the bar's maximum IS. Every
            // term above this line feeds it, and all of it used to be discarded
            // at the end of the tick - see TickStatePayload.CachedEffectiveMaxHp.
            payload.CachedEffectiveMaxHp = effectiveMilliHp;

            // Modul: the same, for the attack side. Computed unconditionally
            // rather than inside the swing branch, because the world boss
            // resolves outside the swing and must not re-derive it.
            payload.CachedEffectiveMilliAttack =
                EffectiveMilliAttackFor(ref payload, in combatStats, lineage.DamageScalePerLevelPct);

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
                // Modul: Last Stand, the Cruelty crown - once an hour, the blow
                // that would kill you leaves you at one health instead.
                //
                // ONCE AN HOUR, tracked on the payload against the same tick
                // clock everything else here uses. Without the cooldown it
                // would be flat immortality, because this branch is reached
                // every time the pool empties and death here already only costs
                // a reset to full - so the crown has to buy something narrower:
                // surviving AT one health, which is a real state a player can
                // then heal out of.
                if (payload.Skill_LastStand > 0 && payload.LastStandCooldownRemaining <= 0)
                {
                    payload.PlayerHp = 1;
                    payload.LastStandCooldownRemaining = LastStandCooldownTicks;
                }
                else
                {
                    payload.PlayerHp = effectiveMaxHp;
                }
            }

            if (payload.CurrentMonsterId <= 0)
            {
                payload.CurrentMonsterId = fallbackId;
                ArmThundererIfBoss(ref payload);
                payload.CurrentMonsterHp = BossFirstClearRules.MaxHpFor(payload.DefeatedRegionBossMask, payload.CurrentMonsterId, payload.Skill_FirstBlood) * 1000L;
                payload.CombatTargetTickAccumulator = 0;
            }

            payload.CombatTargetTickAccumulator++;

            var activeMonster = ContentRegistry.Monsters[payload.CurrentMonsterId - 1];

            // Player attacks monster
            // Modul: the second copy of the interval formula, and the one that
            // decides what actually happens. See CombatDamageModel - attack
            // speed is a PERCENT like every other *Pct on CombatStats, and this
            // read it as a fraction, so every character past about DEX 20 sat
            // on the 200 ms floor at seven and a half times the intended swing
            // rate.
            int playerAttackSpeedMs = CombatDamageModel.AttackIntervalMs(in combatStats);

            // Modul: Relentless, the Precision bough - you swing faster, and
            // everything else in this loop scales off how often you hit.
            //
            // Applied to the resolved interval rather than to CombatStats,
            // because the tree is an account bonus and CombatStats describes
            // the gear. Floored at 200 ms, the same floor AttackIntervalMs
            // itself enforces - a swing rate the tick cannot deliver would
            // just silently round away, and the pacing model does not know
            // about sub-tick swings.
            if (payload.Skill_Relentless > 0)
            {
                float faster = SkillTreeRegistry.GetBonusPercent(
                    SkillTreeRegistry.BoughRelentless, payload.Skill_Relentless) / 100f;
                playerAttackSpeedMs = Math.Max(200, (int)(playerAttackSpeedMs * (1f - faster)));
            }

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
                    // Modul: skill tree. Precision adds percentage POINTS of
                    // crit chance and Cruelty adds to the multiplier - both are
                    // applied here rather than inside StatsCalculator because
                    // the tree is a player-account bonus, not a property of the
                    // gear the stats are computed from.
                    // Modul: CRIT CHANCE HAD NO CEILING, found by
                    // PowerCeilingTests on its first run, 2026-09-06.
                    //
                    // It sums from DEX, Vila mastery, five affix rolls, the
                    // bred crit locus and this tree branch, and nothing clamped
                    // the total - so `NextDouble() <= chance/100` was a
                    // guaranteed crit forever past 100. Five crit_chance rolls
                    // on one region-5 Legendary weapon already exceed it on
                    // their own, which turned crit from variance into a flat
                    // multiplier and made every further crit-chance roll a dead
                    // affix nobody was told about.
                    //
                    // Clamped rather than rebalanced: 100% is the honest
                    // ceiling for a probability, it takes nothing away from
                    // anyone (you cannot crit more often than always), and it
                    // makes the excess visible to the ledger instead of
                    // silently wasted.
                    float treeCritChance = Math.Clamp(
                        combatStats.CritChancePct
                            + SkillTreeRegistry.GetBonusPercent(SkillTreeRegistry.BranchCritChance, payload.Skill_CritChance),
                        0f,
                        MaxCritChancePct);

                    // Modul: and the client is told, so a crit can look like
                    // one. Cleared on every swing rather than only set, or a
                    // single crit would paint every later hit yellow.
                    payload.LastHitWasCrit = 0;

                    if (Random.Shared.NextDouble() <= (treeCritChance / 100.0f))
                    {
                        payload.LastHitWasCrit = 1;
                        critMult = StatsCalculator.ComputeCritMultiplier(combatStats)
                            + (SkillTreeRegistry.GetBonusPercent(SkillTreeRegistry.BranchCritDamage, payload.Skill_CritDamage) / 100f)
                            // Modul: Guile, the Precision bough. Stacks with
                            // Cruelty rather than replacing it - a player who
                            // took the crit fork should feel both.
                            + (SkillTreeRegistry.GetBonusPercent(SkillTreeRegistry.BoughGuile, payload.Skill_Guile) / 100f);

                        // Modul: Double Strike, the Precision crown. Expressed
                        // as extra multiplier rather than a second swing,
                        // because a second swing here would need its own
                        // to-hit, its own lifesteal and its own kill check -
                        // three places to get out of step for an effect the
                        // player experiences as "that one hit harder".
                        if (payload.Skill_DoubleStrike > 0
                            && Random.Shared.NextDouble()
                               <= (SkillTreeRegistry.GetBonusPercent(
                                       SkillTreeRegistry.CrownDoubleStrike, payload.Skill_DoubleStrike) / 100.0f))
                        {
                            critMult *= 2f;
                        }
                    }

                    // Modul: computed once per tick and cached, because the
                    // world boss needs the same number and a second derivation
                    // of it would be a second authority over how hard this
                    // character hits. See EffectiveMilliAttackFor.
                    long effectiveMilliAttack = payload.CachedEffectiveMilliAttack;

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
                    // shields modeled on the PvE monster side), so armour is the
                    // whole of it - activeMonster.Armor is sourced from content
                    // data rather than a hardcoded 0.
                    //
                    // Modul: it REDUCES. This was `max(1 hp, raw - armour*1000)`
                    // and it was the fifth copy of that rule - the one inside
                    // the live tick, which is the one that decides what actually
                    // happens. It went on subtracting for a few minutes after
                    // CombatDamageModel stopped, and the two disagreed by nearly
                    // a factor of two: every projection in the game said a kill
                    // took half as long as it did.
                    int defenderArmor = activeMonster.Armor;
                    // Modul: and the player's armour PENETRATION, which this
                    // call never passed and nothing else applied - so Might's
                    // second effect and every armor_pen_flat affix in the game
                    // were worth exactly nothing. See Mitigate's own note.
                    int netDamage = (int)CombatDamageModel.Mitigate(
                        rawDamage,
                        defenderArmor,
                        CombatDamageModel.MonsterArmourHalvingConstant(activeMonster.RegionTier),
                        combatStats.FlatArmorPenetration);
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

                    // Modul: Bloodthirst, the Cruelty bough - a share of the
                    // damage dealt comes back as health.
                    //
                    // On the POST-armour figure, so it scales with what you
                    // actually did rather than with what you swung for, and
                    // capped at the health pool because overhealing into a
                    // bar the client scales against observedMaxPlayerHp would
                    // draw a health bar longer than the bar.
                    if (payload.Skill_Bloodthirst > 0 && netDamage > 0)
                    {
                        long healed = (long)netDamage
                            * SkillTreeRegistry.GetBonusTenthsOfPercent(
                                SkillTreeRegistry.BoughBloodthirst, payload.Skill_Bloodthirst) / 1000L;
                        if (healed > 0)
                        {
                            payload.PlayerHp = (int)Math.Min(
                                effectiveMaxHp, payload.PlayerHp + healed);
                        }
                    }

                    // Modul: set bonuses made real. Burn - the Chiming Steel
                    // 4-piece's damage-over-time half. Deliberately modelled as
                    // a deterministic fraction of the hit that applied it,
                    // resolved immediately, rather than as a timed DoT: this
                    // combat loop has no per-target effect timers, and adding a
                    // scheduler for one effect would be a much larger change
                    // than the effect is worth. The player-visible result is
                    // the same - a matching 4-piece set burns for extra damage.
                    // Modul: hoisted out of the block below only so the combat
                    // event at the end of this swing can say the burn happened.
                    // The arithmetic is unchanged.
                    int burnDamageDealt = 0;
                    if (combatStats.SetBurnApplicationActive)
                    {
                        int burnDamage = (int)(netDamage * BurnDamageFraction);
                        if (burnDamage > 0)
                        {
                            payload.CurrentMonsterHp -= burnDamage;
                            payload.TargetStatusEffectBitmask |= ActiveSkillEngine.StatusFlagBurning;
                            burnDamageDealt = burnDamage;
                        }
                    }

                    // Modul: LIFESTEAL HEALED SEVEN HUNDRED PERCENT OF THE HIT.
                    //
                    // `netDamage * LifestealPct` with no divide by 100, and the
                    // stat arrives as a percentage - so a 7% lifesteal affix
                    // healed seven TIMES the damage the swing dealt. One hit
                    // refilled the bar from anywhere, always, and the auto-eat
                    // larder never fired because health never fell.
                    //
                    // Reported from a live playtest as "weird regeneration": a
                    // player in three low-rarity pieces from the first monster
                    // in the game killed the region 1, 2 and 3 bosses without
                    // dropping below half health. It is the third stat in this
                    // codebase found being written as a percent and read as a
                    // fraction; attack speed was the last one, four hours ago.
                    //
                    // The cap is the second half and it is not paranoia. Damage
                    // grows faster than the health pool does, so even a correct
                    // percentage of a large hit is a full heal at depth -
                    // lifesteal has to be sustain, not immunity, or it makes
                    // every fight after the first one unloseable.
                    long lifestealHealed = 0;
                    if (combatStats.LifestealPct > 0)
                    {
                        long lifestealAmount = (long)(netDamage * (combatStats.LifestealPct / 100f));
                        long lifestealCeiling = effectiveMaxHp / 100; // 1% of the bar per hit
                        if (lifestealAmount > lifestealCeiling) lifestealAmount = lifestealCeiling;

                        payload.PlayerHp += (int)lifestealAmount;
                        if (payload.PlayerHp > effectiveMaxHp) payload.PlayerHp = effectiveMaxHp;
                        lifestealHealed = lifestealAmount;
                    }

                    // Modul: AND THE PLAYER IS TOLD WHAT JUST HAPPENED.
                    //
                    // Everything above resolved a blow and then discarded the
                    // detail, leaving the client to infer the whole fight from
                    // the difference between two CurrentMonsterHp snapshots.
                    // Measured 2026-09-04: snapshots arrive every ~1090ms and a
                    // geared character kills an early monster every ~1400ms, so
                    // across 27 consecutive snapshots that field took exactly
                    // ONE value and there was nothing to infer. See
                    // ResponseCombatEventPacket.
                    //
                    // Reported in whole hit points, and burn is folded into the
                    // hit that applied it - it is not a second swing, and a log
                    // that split it would read as one.
                    CombatEventFeed.Publish(
                        payload.PlayerId,
                        payload.CurrentMonsterId,
                        Network.ResponseCombatEventPacket.KindPlayerHit,
                        (int)((netDamage + burnDamageDealt) / 1000L),
                        (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L),
                        (byte)((payload.LastHitWasCrit == 1 ? Network.ResponseCombatEventPacket.FlagCrit : 0)
                             | (burnDamageDealt > 0 ? Network.ResponseCombatEventPacket.FlagBurn : 0)));

                    // Separate line, because it is health arriving rather than
                    // damage leaving - folding it into the hit above would make
                    // the two indistinguishable in a log.
                    if (lifestealHealed >= 1000L)
                    {
                        CombatEventFeed.Publish(
                            payload.PlayerId,
                            payload.CurrentMonsterId,
                            Network.ResponseCombatEventPacket.KindLifesteal,
                            (int)(lifestealHealed / 1000L),
                            (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L));
                    }
                }
                else
                {
                    // Modul: A MISS IS THE ONE EVENT NO HEALTH DIFFERENCE CAN
                    // IMPLY. It moves nothing, so before this feed existed it
                    // was invisible by construction - the fight simply appeared
                    // to pause. Accuracy and the monster's dodge rating are both
                    // real stats, and this is the only place they are ever
                    // visible.
                    CombatEventFeed.Publish(
                        payload.PlayerId,
                        payload.CurrentMonsterId,
                        Network.ResponseCombatEventPacket.KindPlayerMiss,
                        0,
                        (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L));
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
                    // Modul: AND IT STAYS A LONG, 2026-09-12.
                    //
                    // Saturating at int.MaxValue was a correct fix for the
                    // overflow described above and a CEILING on the boss wall
                    // nobody had noticed: 2.147e9 milli-damage is about twelve
                    // times Malakor's authored attack, and the per-region wall
                    // needs more than that to out-pace a level-100 larder. The
                    // saturation would have silently eaten the difference and
                    // the wall would have looked tuned while being capped.
                    //
                    // Nothing downstream needs an int: Mitigate already takes a
                    // long, and the only int in the chain is PlayerHp, which is
                    // assigned through a clamped subtraction below.
                    long rawDamage = (long)(BossFirstClearRules.AttackPowerFor(payload.DefeatedRegionBossMask, payload.CurrentMonsterId) * 1000L * monsterCritMult);

                    // Step 3+4 (Armor then Block, combined): armor subtracts
                    // flat milli-damage, BlockStrengthPct (CON-derived, see
                    // StatsCalculator) then reduces what remains
                    // multiplicatively - a shield/bulk stat that shaves a
                    // fraction off whatever armor did not already stop,
                    // rather than stacking as another flat subtraction.
                    // Clamped below 100% so a high-CON build can reduce a hit
                    // close to the floor but never to true zero damage.
                    float blockStrengthFraction = Math.Clamp(combatStats.BlockStrengthPct / 100f, 0f, 0.75f);
                    // Modul: armour REDUCES. See CombatDamageModel.Mitigate -
                    // this was `raw - armour * 1000`, which meant a player one
                    // tier behind took the full hit and a player in best-in-slot
                    // took the 1 HP floor, with nothing in between.
                    long armorMitigatedDamage = CombatDamageModel.Mitigate(
                        rawDamage,
                        combatStats.FlatPhysicalArmor,
                        CombatDamageModel.PlayerArmourHalvingConstant(monsterRegionTier));
                    long finalDamage = Math.Max(1000L, (long)(armorMitigatedDamage * (1f - blockStrengthFraction)));

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
                        long damageCeiling = (long)(effectiveMaxHp * SetDamageCapMaxHpFraction);
                        if (damageCeiling > 0 && finalDamage > damageCeiling)
                        {
                            finalDamage = damageCeiling;
                        }
                    }

                    // Modul: clamped, because finalDamage is a long now and
                    // PlayerHp is an int. Any value at or below zero is death, so
                    // flooring the result costs nothing and an unclamped cast
                    // could wrap a lethal hit into a positive health bar - the
                    // exact shape of the overflow bug this block already records.
                    long playerHpAfterHit = payload.PlayerHp - finalDamage;
                    payload.PlayerHp = playerHpAfterHit < MinimumRepresentableHp
                        ? MinimumRepresentableHp
                        : (int)playerHpAfterHit;

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
                    long thornsReflected = 0;
                    if (combatStats.SetThornsReflectionActive && payload.CurrentMonsterHp > 0)
                    {
                        long reflectedDamage = (long)(finalDamage * ThornsReflectionFraction);
                        if (reflectedDamage > 0)
                        {
                            payload.CurrentMonsterHp -= reflectedDamage;
                            thornsReflected = reflectedDamage;
                        }
                    }

                    // Modul: the incoming half of the fight log.
                    //
                    // BLOCKED IS A REAL THING HERE and only here: monsters carry
                    // no block stat, so a player's swing is never blocked, but
                    // the player's own BlockStrengthPct (CON-derived) shaves a
                    // fraction off what armour did not already stop. Armour
                    // itself is deliberately NOT an event - it reduces every hit
                    // rather than stopping any, so it is a smaller number on
                    // this line, not a line of its own.
                    CombatEventFeed.Publish(
                        payload.PlayerId,
                        payload.CurrentMonsterId,
                        Network.ResponseCombatEventPacket.KindMonsterHit,
                        (int)Math.Min(int.MaxValue, finalDamage / 1000),
                        (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L),
                        (byte)((monsterCritMult > 1.0f ? Network.ResponseCombatEventPacket.FlagCrit : 0)
                             | (blockStrengthFraction > 0f ? Network.ResponseCombatEventPacket.FlagBlocked : 0)));

                    if (thornsReflected > 0)
                    {
                        CombatEventFeed.Publish(
                            payload.PlayerId,
                            payload.CurrentMonsterId,
                            Network.ResponseCombatEventPacket.KindPlayerHit,
                            (int)Math.Min(int.MaxValue, thornsReflected / 1000),
                            (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L),
                            Network.ResponseCombatEventPacket.FlagThorns);
                    }
                }
                else
                {
                    // The monster swung and the player's dodge took it. Same
                    // argument as the player's own miss above: nothing moves, so
                    // without this line the fight looks like it stopped.
                    CombatEventFeed.Publish(
                        payload.PlayerId,
                        payload.CurrentMonsterId,
                        Network.ResponseCombatEventPacket.KindMonsterMiss,
                        0,
                        (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L));
                }
            }

            // Step 5 (Auto-Eat)
            //
            // Modul: A BITE HAS A COOLDOWN. This used to fire on every tick, so
            // a stocked larder healed ten times a second - up to two hundred
            // percent of the health bar per second at the low end. Nothing
            // could kill a player who owned fish, so a boss was a check on
            // inventory rather than on equipment, and every attempt to make
            // gear the gate failed against it.
            //
            // At one bite every AutoEatCooldownTicks, healing has a ceiling and
            // the question becomes whether the player survives BETWEEN bites -
            // which is what armour and health are for.
            // Modul: Last Stand's hour, counted down on the same path auto-eat
            // uses - one place where per-tick cooldowns tick.
            if (payload.LastStandCooldownRemaining > 0)
            {
                payload.LastStandCooldownRemaining--;
            }

            if (payload.AutoEatCooldownTicks > 0)
            {
                payload.AutoEatCooldownTicks--;
            }
            else if (payload.PlayerHp > 0 && payload.PlayerHp <= (payload.AutoEatThreshold / 100.0f) * effectiveMaxHp)
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
                    payload.AutoEatCooldownTicks = AutoEatCooldownTicks;

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
                // Read before anything below can clear it.
                int deathMonsterId = payload.CurrentMonsterId;

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

                    // Modul: WHO killed you, not just that something did.
                    //
                    // Captured HERE because the next four lines wipe it -
                    // CurrentMonsterId is cleared as part of the respawn, so
                    // by the time any broadcast runs the killer is gone. A
                    // death card that cannot name the monster is a shrug.
                    payload.LastDeathMonsterId = deathMonsterId;
                    payload.LastDeathTick++;
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
            finalXpMultiplier += (int)SkillTreeRegistry.GetBonusPercent(SkillTreeRegistry.BranchXpGain, payload.Skill_XpGain);
            finalXpMultiplier += FolkIdle.Server.Engine.GuildBonusesCache.GetBuffTier(payload.GuildId, "Exp") * 2;

                // Modul: and the same removal on the live-kill path. Two copies
                // of one bonus, which is why it is worth saying twice that the
                // payload fields behind it are fossils.

                // Modul: THE DEATH IS THE ONLY MOMENT A FAST FIGHT HAS.
                //
                // A geared character kills an early monster inside a single
                // snapshot, so the health bar never animates and the monster is
                // simply gone and replaced. This line is what tells the client
                // a kill happened at all, as opposed to the target silently
                // changing - which is what it looked like before.
                //
                // Amount carries the xp this kill is about to pay, read from
                // the content reward rather than recomputed: the multiplied
                // figure is not known until ProcessMonsterDeath has run, and a
                // second copy of that arithmetic is exactly the drift this
                // codebase keeps paying for.
                CombatEventFeed.Publish(
                    payload.PlayerId,
                    activeMonster.Id,
                    Network.ResponseCombatEventPacket.KindKill,
                    activeMonster.BaseXpReward,
                    0);

                int seasonalCombatXp = activeMonster.BaseXpReward * finalXpMultiplier / 100;
                long victoryXpBefore = payload.CurrentXp;
                ProgressionEngine.ProcessMonsterDeath(ref payload, activeMonster.BaseXpReward, finalXpMultiplier, ActiveGlobalEventId, activeRaceId);
                // Modul: what this ONE kill paid, for the victory card. Read as
                // a delta rather than recomputed - ProcessMonsterDeath applies
                // the event multiplier, the race passive and the level-up
                // curve, and a second copy of that arithmetic would drift.
                long victoryXpEarned = System.Math.Max(0L, payload.CurrentXp - victoryXpBefore);

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

                // Modul: and the boss stops being a first clear.
                //
                // Set separately from the door above, and deliberately NOT
                // behind its `< LastRegion` guard: clearing region 5's boss
                // opens no sixth region, so a mask folded into that condition
                // would leave the last boss in the game permanently at
                // first-clear stats.
                bool wasFirstClearForThisPlayer =
                    BossFirstClearRules.IsFirstClearPending(payload.DefeatedRegionBossMask, activeMonster.Id);

                payload.DefeatedRegionBossMask =
                    BossFirstClearRules.MarkDefeated(payload.DefeatedRegionBossMask, activeMonster.Id);

                // Modul: FIRST BLOOD ON A REGION BOSS GOES TO THE WHOLE WORLD.
                //
                // Asked for directly: "when someone is the first player to beat
                // a boss, congratulate them in chat". Two claims, and only the
                // weaker one can be made from inside a tick: this player's own
                // first clear is on the payload, and whether they are the first
                // in the WORLD needs a durable, contended check that a 10Hz
                // loop has no business doing - so the announcement says what is
                // true rather than what would be nicer to say.
                //
                // BossFirstClearAnnouncer keeps the world-first claim honest by
                // holding it in Redis, where every pod can see the same answer.
                if (wasFirstClearForThisPlayer && clearedBossRegion > 0)
                {
                    BossFirstClearAnnouncer.Announce(payload.PlayerId, activeMonster.Id);

                    // Modul: and the card that says what just happened.
                    //
                    // The duration comes from CombatTargetTickAccumulator,
                    // which is zeroed when a monster spawns and incremented
                    // once per tick - so it already IS the length of this
                    // fight, in tenths. Nothing else in the payload knows when
                    // the fight started.
                    //
                    // Gold and xp are filled in further down, after the reward
                    // arithmetic that has not run yet at this point; this only
                    // opens the record.
                    payload.LastVictoryMonsterId = activeMonster.Id;
                    payload.LastVictoryDurationSeconds = (int)(payload.CombatTargetTickAccumulator * TickIntervalSeconds);
                    payload.LastVictoryGold = 0L;
                    payload.LastVictoryXp = victoryXpEarned;
                    payload.LastVictoryTick++;

                    payload.IsDirty = true;
                }

                AddSeasonalXp(ref payload, seasonalCombatXp);

                if (liveSessionContexts.TryGetValue(payload.PlayerId, out var sessionCtx))
                {
                    sessionCtx.ThreadSafeAddMonsterKill();
                }

                QuestEngine.IncrementProgress(ref payload, QuestEngine.QuestTypeKillMonsters, 1);

                // Modul: Golden Fleece, the Fortune crown - every hundredth
                // kill drops an item two rarity tiers above its due.
                //
                // Counted even when the crown is not taken, so that TAKING it
                // does not hand the player a hundred-kill head start they did
                // not earn, and so untaking it (a respec) does not reset a
                // counter they were watching.
                payload.KillsSinceFleece++;
                int fleeceTiers = 0;
                if (payload.KillsSinceFleece >= GoldenFleeceKillInterval)
                {
                    payload.KillsSinceFleece = 0;
                    if (payload.Skill_GoldenFleece > 0) fleeceTiers = GoldenFleeceBonusTiers;
                }

                long goldReward = (activeMonster.BaseGoldReward * (long)GlobalEngineState.GlobalGoldDropMultiplier) / 100L;
                // Modul 13.4.3: Human's innate +5% Gold acquisition passive.
                goldReward = (long)(goldReward * (1.0f + combatStats.GoldAcquisitionMultiplierPct / 100f));
                goldReward = (long)(goldReward * (1.0f + LegacyPerkResolver.GetGoldBonusPct(payload.CachedLegacyPerks) / 100f));
            goldReward = (long)(goldReward * (1.0f + FolkIdle.Server.Engine.GuildBonusesCache.GetBuffTier(payload.GuildId, "Gold") * 0.02f));
                // Modul: inheritance. A permanent, season-crossing multiplier.
                goldReward = (long)(goldReward * (1.0f + InheritanceRegistry.GetBonusPct(payload.Inherit_GoldGain) / 100f));

                // Modul: Trophy Hunter on the LIVE kill path as well as the
                // offline one above. A bonus that only pays while the player is
                // asleep is the kind of divergence this codebase has shipped
                // before, in gathering.
                if (payload.Skill_TrophyHunter > 0
                    && RaceUnlockRegistry.GetRegionForBossMonsterId(activeMonster.Id) > 0)
                {
                    goldReward = (long)(goldReward * (1.0f + SkillTreeRegistry.GetBonusPercent(
                        SkillTreeRegistry.BoughTrophyHunter, payload.Skill_TrophyHunter) / 100f));
                }

                if (goldReward > 0)
                {
                    payload.AddGold(goldReward);
                    payload.RedisPendingGoldDelta += goldReward;
                    payload.RequiresRedisFlush = true;
                    payload.IsDirty = true;
                }

                // Modul: the victory card's gold, written only when THIS kill
                // is the one it was opened for. Guarded on the monster id
                // rather than on a bool, so a later ordinary kill of the same
                // boss cannot overwrite the first clear's figures.
                if (payload.LastVictoryMonsterId == activeMonster.Id && payload.LastVictoryGold == 0L)
                {
                    payload.LastVictoryGold = goldReward;
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

                // Modul: counted HERE as well as sixty lines below, because the
                // live server insisted on something the source says is
                // impossible - codex kills climbing while the loot enqueue in
                // this same straight-line block never fired. One of those two
                // observations is wrong, and only the running process can say
                // which.
                CombatLootEngine.NoteCodexKill();

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
                // Modul: the luck sum used to be written out here, and a second,
                // shorter copy of it lived in OfflineSimulationEngine. The two
                // drifted, which is why offline drops were measurably worse -
                // see CombatLootDropRequest.Build, which is now the only place
                // either path composes one.
                CombatLootEngine.DropRequestQueue.Enqueue(CombatLootDropRequest.Build(
                    in payload,
                    in combatStats,
                    payload.CurrentMonsterId,
                    kills: 1,
                    bonusRarityTiers: fleeceTiers,
                    skipMaterialRoll: false));

                // Modul: THE OTHER HALF OF THE LOOT TELEMETRY.
                //
                // CombatLootEngine reports what it DRAINS. Without a matching
                // count of what the tick ENQUEUES, a silent loot path still has
                // two possible causes and no way to separate them - which is
                // exactly where a day went. These two numbers must track each
                // other; a gap is the bug, and which side the gap is on says
                // which half to look at.
                CombatLootEngine.NoteKillEnqueued();

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
                payload.CurrentMonsterHp = BossFirstClearRules.MaxHpFor(payload.DefeatedRegionBossMask, payload.CurrentMonsterId, payload.Skill_FirstBlood) * 1000L;
                payload.CombatTargetTickAccumulator = 0;
            }
        }

        /// <summary>
        /// Gathering progress, yield and grants for one tick. Extracted
        /// verbatim from ProcessSubTick's second activity branch.
        ///
        /// Modul: the branch's trailing `return;` stays at the CALL SITE -
        /// returning from this method cannot make ProcessSubTick return, and a
        /// gathering character that fell through would start fighting
        /// monster 1 while gathering (the defect PR #7 fixed for crafting).
        ///
        /// Callable ONLY from ProcessSubTick: the slot register's
        /// swap/try/finally discipline lives one level up in
        /// ProcessAllSlotSubTicks.
        /// </summary>
        private static void RunGatheringTick(ref TickStatePayload payload, in GatheringNodeDefinition gatheringNode, int localDropMultiplier)
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
            int requiredTicks = GatheringToolEngine.ComputeRequiredTicks(gatheringNode.BaseTickThreshold, masteryLevel, toolTier, villageProductionLevel, payload.ToolGatherSpeedPct
                + SkillTreeRegistry.GetBonusTenthsOfPercent(
                    SkillTreeRegistry.BoughHarvest, payload.Skill_Harvest) / 10
                + BloodlineBonuses.GatherSpeedBonusPct(payload.Aptitude_Skill, TraitTotals.From(payload.TraitMask)));
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
                    var gatherCombatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, gatherActiveAgePhase, payload.CompletedAreaFlags, gatherActiveRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation, TraitTotals.From(payload.TraitMask), payload.CachedSetIds);

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

                    // Modul: yield traits, which replaced the Yield gene on
                    // 2026-09-13 - percentage points of extra harvest rolls, the
                    // same units as the race-mastery bonuses above.
                    additionalYieldBonus += BloodlineBonuses.GatherYieldBonusPct(TraitTotals.From(payload.TraitMask));

                    // Modul: LootLuckPct no longer multiplies the roll COUNT
                    // (which previously inflated absolute yield of every
                    // table entry, common trash and rare drops alike, in
                    // fixed proportion - a placebo that never actually
                    // shifted rarity odds). Roll count now stays driven only
                    // by monolith/race/event/trait bonuses; luck
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
        }

        /// <summary>
        /// Crafting-as-a-job progress for one tick. Extracted verbatim from
        /// ProcessSubTick's first activity branch.
        ///
        /// Modul: the branch's `return;` stays at the CALL SITE in
        /// ProcessSubTick, beside the comment that explains it (PR #7,
        /// ProcessSubTickDispatchTests) - a return inside this method could
        /// not stop ProcessSubTick falling through into combat.
        ///
        /// Callable ONLY from ProcessSubTick: the slot register's
        /// swap/try/finally discipline lives one level up in
        /// ProcessAllSlotSubTicks.
        /// </summary>
        private static void RunCraftingProgressTick(ref TickStatePayload payload, in ContentRegistry.RecipeDefinition craftingRecipe)
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
    }
}
