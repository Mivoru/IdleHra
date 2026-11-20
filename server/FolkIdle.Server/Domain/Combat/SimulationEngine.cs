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
        // Modul: MICROSECONDS, NOT MILLISECONDS (2026-11-03). The sum used to
        // add stopwatch.ElapsedMilliseconds, which truncates - and a tick
        // costs 0.05-0.5 ms, so almost every tick added 0 and the exported
        // _sum read about a tenth of the truth (lockstep P0 measured a "mean"
        // of 0.02 ms against a p50 of 0.19). /metrics divides by 1000.
        public long TickDurationSumMicros;

        // Task 43, 2d: see TickPacer.
        public long TicksDropped;
        public long CatchUpTicks;
    }

    public partial class SimulationEngine
    {
        private const int TickIntervalMs = 100; // 10 Hz

        // Modul: the chance of 1 diamond on an ORDINARY (non-boss) kill, 0.01%
        // (owner decision 2026-09-28, down from 0.05%). Regional bosses pay
        // none (BossDiamondTests). OrdinaryKillDiamondTests pins it.
        //
        // It is a PER-KILL RATE and nothing else, on every path: the live tick
        // rolls it per kill, and the offline catch-up draws Binomial(kills,
        // this chance) for its window (owner rule 2026-09-30: the same per
        // hour online and away). There is NO weekly cap on it (owner, same
        // day), so what it pays a week is kills-per-hour x hours played,
        // online or not - about 0.36 an hour at one kill a second. This
        // comment used to quote that as "~60 a week", a figure that assumed
        // only watched hours paid; do not read it as a ceiling.
        internal const double OrdinaryKillDiamondChance = 0.0001;

        // Modul: set bonuses made real. Magnitudes for the 4-piece effects,
        // which had none because nothing consumed them. Chosen to be worth
        // chasing without eclipsing the flat 2-piece core: a burn adding a
        // quarter of the hit that applied it, and thorns returning a fifth of
        // what actually landed. Both are deliberately fractions of a real
        // number already computed on the same path, so neither needs its own
        // scaling curve to stay relevant across the five regions.
        internal const float BurnDamageFraction = 0.25f;
        internal const float ThornsReflectionFraction = 0.20f;

        // The Eternal Dreadnought 4-piece's cooldown reduction. Applied to the
        // cooldown stamped after a successful cast.
        private const float SetCooldownReductionFraction = 0.20f;

        // The Eternal Dreadnought 4-piece's per-hit damage ceiling, as a share
        // of effective max HP. At 20 percent a wearer always survives at least
        // five consecutive hits from full, which is the point: it buys the
        // auto-eat larder the window it needs to respond, without making the
        // wearer immortal against sustained damage.
        internal const float SetDamageCapMaxHpFraction = 0.20f;

        // Modul: the floor a clamped health subtraction lands on. Anything at
        // or below zero is death, so the exact depth is irrelevant - what matters
        // is that it cannot wrap round into a positive bar, which is how the
        // deadliest monster in the game once dealt 1 HP a hit. PlayerHp is a
        // long since 2026-11-08, but the floor stays: a hit can still be huge.
        private const long MinimumRepresentableHp = long.MinValue / 2;
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
        // Ticks between snapshot broadcasts: 10 (1 Hz) times the dev tick speed.
        private int _ticksPerBroadcast = 10;

        // Lockstep (docs/design/LOCKSTEP_SIM.md): the tick being run, the last
        // tick that broadcast, and a one-tick request to broadcast.
        private long _lockstepTick;
        private long _lastLockstepBroadcastTick = -1;
        private bool _forceBroadcastThisTick;
        private DateTime _lockstepLastDay;
        private readonly System.Collections.Concurrent.ConcurrentQueue<TickStatePayload> _readyLogins = new();

        private EngineMetricsPayload _metrics;
        public ref EngineMetricsPayload GetMetrics() => ref _metrics;

        // Modul: TICK PERCENTILES FROM A ROLLING WINDOW (task 43). The
        // histogram buckets are cumulative since boot and in whole
        // milliseconds, so they can say "99% of all ticks ever were <= 25 ms"
        // but not what p99 was over the last minute - which is the number the
        // task's "tick p99 under 25 ms in production" is judged on, and the
        // only one that moves after a deploy. The last 600 ticks (one minute at
        // 10 Hz) in microseconds; written by the tick alone, copied and sorted
        // by the /metrics scrape. A torn read of one slot mid-write costs one
        // sample's accuracy, not correctness.
        internal const int RecentTickWindow = 600;
        private readonly int[] _recentTickMicros = new int[RecentTickWindow];
        private long _recentTickCount;

        internal readonly record struct TickPercentiles(int Samples, double P50Ms, double P95Ms, double P99Ms, double MaxMs);

        internal TickPercentiles GetRecentTickPercentiles()
        {
            int n = (int)Math.Min(Interlocked.Read(ref _recentTickCount), RecentTickWindow);
            return ComputeTickPercentiles(_recentTickMicros, n);
        }

        internal static TickPercentiles ComputeTickPercentiles(int[] micros, int n)
        {
            if (n == 0) return new TickPercentiles(0, 0, 0, 0, 0);
            var copy = new int[n];
            Array.Copy(micros, copy, n);
            Array.Sort(copy);
            double At(double q) => copy[Math.Min(n - 1, (int)Math.Ceiling(q * n) - 1)] / 1000.0;
            return new TickPercentiles(n, At(0.50), At(0.95), At(0.99), copy[n - 1] / 1000.0);
        }

        // For /metrics: checkpoint back-pressure beside the tick time.
        internal StateCheckpointManager CheckpointManager => _checkpointManager;

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
            // Checkpoint acks come back through the registry, like every other
            // cross-thread hand-off into the tick (task 43).
            _checkpointManager.BindAckQueue(_playerRegistry.FlushAckQueue);
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
                DrainCheckpointWriterForShutdown();
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

        // Modul: the writer drains BEFORE the batch flush, and its acks are
        // applied first. A queued logout or command flush must still commit,
        // and a payload whose in-flight flushes committed without the tick
        // seeing the acks carries an epoch behind the database - FlushBatch's
        // sieve would skip it in silence. Called with the tick stopped.
        private void DrainCheckpointWriterForShutdown()
        {
            if (!_checkpointManager.DrainWriter(TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine("[SimulationEngine] Checkpoint writer did not drain within 10 s; continuing with the batch flush.");
            }
            CheckpointAckTickCoordinator.Drain(_playerRegistry, _activePlayers, _checkpointManager);
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
                DrainCheckpointWriterForShutdown();
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

        // Test-only observability (via InternalsVisibleTo) for the
        // guild-membership drain: how many ReloadState packets the drain
        // has issued, and whether a player currently sits in a guild's
        // index bucket. The tick thread owns both structures; tests poll
        // these after enqueueing a GuildMembershipChangeNotification and
        // must tolerate a tick's worth of latency, not expect synchronous
        // visibility.
        internal long GuildMembershipReloadStatesIssued;

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
            Interlocked.Increment(ref _inFlightDispatches);
            bool gating = IsWorldGatingDispatch(context);
            if (gating) Interlocked.Increment(ref _inFlightGatingDispatches);
            _ = SafeDispatchAsyncCore(context, playerIdToDisconnectOnFailure, action, gating);
        }

        // The dispatches that decide WHO and WHAT is in the world - a login, an
        // activity change, a consumable, a node migration. Lockstep waits for
        // these before every tick; the rest (village production writes, forge,
        // market, mail) only before a bot wakes or the clock jumps. Waiting for
        // all of them every tick held the first lockstep run to x33: a village
        // production write is a database round trip on most ticks.
        private static bool IsWorldGatingDispatch(string context)
            => context == "Login" || context == "Character.ChangeActivity" || context == "ConsumeConsumable" || context.StartsWith("NodeMigration", StringComparison.Ordinal);

        private int _inFlightGatingDispatches;

        // Work the tick handed to the thread pool and has not seen finish
        // (logins above all). Lockstep waits for it - see LockstepBeforeTick.
        private int _inFlightDispatches;

        private async Task SafeDispatchAsyncCore(string context, long playerIdToDisconnectOnFailure, Func<Task> action, bool gating = false)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Modul: A DATABASE CONFLICT REFUSES THE COMMAND, IT DOES NOT
                // KICK THE PLAYER (found by the playtest bots, 2026-11-02). A
                // Serializable write that loses a race with the checkpoint flush
                // throws 40001, and this handler answered it by force-
                // disconnecting - 15-24 times per bot in 63 game days, each one a
                // player thrown to the reconnect screen after an ordinary click.
                // The kick exists so a state transition that failed (Login above
                // all) cannot leave the client waiting for a packet that never
                // comes; an ordinary command that lost a race has changed nothing,
                // so it is refused with "try again in a moment" (TransactionPending)
                // and the session lives. Login keeps the kick.
                if (playerIdToDisconnectOnFailure != 0 && context != "Login" && TransientDbConflict.IsConflict(ex))
                {
                    Console.WriteLine($"SafeDispatchAsync[{context}] lost a database race (refused, session kept): {ex.Message}");
                    _playerRegistry.EnqueueCommandResult(playerIdToDisconnectOnFailure, (byte)CommandResultCode.TransactionPending);
                    return;
                }

                Console.WriteLine($"SafeDispatchAsync[{context}] failed: {ex.Message}");
                if (playerIdToDisconnectOnFailure != 0)
                {
                    _networkSystem.ForceDisconnect(playerIdToDisconnectOnFailure);
                }
            }
            finally
            {
                if (gating) Interlocked.Decrement(ref _inFlightGatingDispatches);
                Interlocked.Decrement(ref _inFlightDispatches);
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
                [CommandType.FuseStack] = ForgeTickCoordinator.HandleFuseStack,
                [CommandType.ExecuteBreeding] = BreedingTickCoordinator.HandleExecuteBreeding,
                [CommandType.ExecuteVillagerBreeding] = BreedingTickCoordinator.HandleExecuteVillagerBreeding,
                [CommandType.InitializeCrafting] = CraftingTickCoordinator.HandleInitializeCrafting,
                [CommandType.CraftItem] = CraftingTickCoordinator.HandleCraftItem,
                [CommandType.UpgradeTool] = CraftingTickCoordinator.HandleUpgradeTool,
                [CommandType.EquipItem] = EquipmentTickCoordinator.HandleEquipItem,
                [CommandType.UnequipItem] = EquipmentTickCoordinator.HandleUnequipItem,
                [CommandType.StockFoodSlot] = LarderTickCoordinator.HandleStockFoodSlot,
                [CommandType.StockPotionShelf] = PotionShelfTickCoordinator.HandleStock,
                [CommandType.DrinkPotion] = PotionShelfTickCoordinator.HandleDrink,
                [CommandType.SetPotionAutoDrink] = PotionShelfTickCoordinator.HandleAutoDrink,
                [CommandType.UpdateAutoEatThreshold] = LarderTickCoordinator.HandleUpdateAutoEatThreshold,
                [CommandType.SpendAttributePoint] = AttributeTickCoordinator.HandleSpendAttributePoint,
                [CommandType.StartBossAscension] = BossAscensionTickCoordinator.HandleStartBossAscension,
                [CommandType.DepositGreatWork] = GreatWorksTickCoordinator.HandleDepositGreatWork,
                [CommandType.BuyEventShopItem] = EventShopTickCoordinator.HandleBuy,
                [CommandType.StartSeasonalBoss] = SeasonalBossTickCoordinator.HandleStart,
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
                [CommandType.SetSimulationSpeed] = ClientSessionTickCoordinator.HandleRetiredSimulationSpeed,
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
            // Modul: the dev-only bot multiplier (TickPacer.ResolveTickSpeed) shortens
            // the WALL interval only; each tick is still TickIntervalMs of game time.
            // Set once at startup (Program.cs) so the game clock and the
            // tick can never run at different speeds.
            int tickSpeed = GameClock.Speed;
            if (tickSpeed > 1 && SimLockstep.Conductor == null)
            {
                Console.WriteLine($"[SimulationEngine] DEV tick speed x{tickSpeed} (FOLKIDLE_TICK_SPEED) - ticks AND GameClock run {tickSpeed}x the wall clock.");
            }
            var pacer = new TickPacer(TickIntervalMs * Stopwatch.Frequency / 1000 / tickSpeed, TickPacer.MaxCatchUpTicks * tickSpeed);
            // Modul: THE BROADCAST STAYS ON WALL TIME. Every broadcast issues a
            // fresh anti-cheat challenge, and every answer spends a token from
            // the session's wall-clock bucket. Broadcasting every 10 TICKS at
            // x10 meant ten answers a second, which ValidateNetworkThroughput
            // read as a flood and aborted the socket about four seconds after
            // login - with no log line, because a flood abort is silent. One
            // snapshot a wall second is also all a bot or a screen needs.
            _ticksPerBroadcast = 10 * tickSpeed;

            // Modul: LOCKSTEP REPLACES THE PACER, NOT THE TICK (2026-11-03). With
            // a conductor the engine does not sleep: it waits for the bots due
            // before a tick, then runs it at once. The tick body is untouched -
            // the same code production runs - and the periodic broadcast is off:
            // a snapshot goes out only in the tick before a wake, because at
            // lockstep speed one per game second would be a packet flood.
            var conductor = SimLockstep.Conductor;
            if (conductor != null)
            {
                _ticksPerBroadcast = int.MaxValue;
                Console.WriteLine($"[SimulationEngine] LOCKSTEP (FOLKIDLE_SIM_LOCKSTEP) - the engine waits for the bots; wake quantum {conductor.QuantumTicks} ticks.");
            }

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
                    pacer.Reset();
                    continue;
                }

                if (conductor != null && !LockstepBeforeTick(conductor))
                {
                    continue;
                }

                long tickStartTimestamp = Stopwatch.GetTimestamp();
                pacer.OnTickStarted(tickStartTimestamp);
                stopwatch.Restart();

                // Modul: THE TICK HOLDS THE LOCK EVERY OTHER READER TAKES.
                //
                // Every off-thread accessor of _activePlayers (the test hooks
                // below the constructor, InjectVirtualPlayer, the drain and
                // shutdown paths) says lock (_activePlayers) - and the tick,
                // the one thread that mutates it all the time, never did. So
                // the locks excluded each other and nothing else. A test that
                // called InjectVirtualPlayer while the engine ran added a key
                // in the middle of the tick's foreach, and the next MoveNext
                // threw "Collection was modified" OUTSIDE the per-player
                // try/catch, which ended EngineLoop and, on a bare thread,
                // the whole test host. Taken once per tick, uncontended in
                // production (nothing else takes it while the engine runs);
                // released before the sleep so a hook waits one tick at most.
                // The body is deliberately not re-indented.
                bool tickLockTaken = false;
                Monitor.Enter(_activePlayers, ref tickLockTaken);
                try
                {

                if (isBenchmarking)
                {
                    FolkIdle.Server.Benchmark.EngineStressTester.InjectCommandFlood(this);
                }

                // Read the authoritative LiveOps event selected by the background ticker.
                ActiveGlobalEventId = GlobalEngineState.ActiveEventType;

                // First, before anything reads an epoch or installs a reloaded
                // payload - see CheckpointAckTickCoordinator for why the order
                // against StateReloadQueue matters.
                CheckpointAckTickCoordinator.Drain(_playerRegistry, _activePlayers, _checkpointManager);

                MarketTickCoordinator.DrainMatchNotifications(_playerRegistry, _activePlayers, _safeDispatch, _contextFactory);

                BreedingTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                WorldBossTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers);

                WorldBossTickCoordinator.DrainStrikeOrders(_playerRegistry, _activePlayers, _worldBossEngine);
                WorldBossTickCoordinator.DrainDuelQuotes(_playerRegistry, _activePlayers);

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
                        // Only an update that resolved the account's tools may
                        // write them - see EquipmentSlotEngine.WithCharacterToolsAsync.
                        if (equipUpdate.ToolsResolved)
                        {
                            currentPayload.AxeToolTier = equipUpdate.AxeToolTier;
                            currentPayload.PickaxeToolTier = equipUpdate.PickaxeToolTier;
                            currentPayload.RodToolTier = equipUpdate.RodToolTier;
                            currentPayload.ToolAxeSpeedPct = equipUpdate.ToolAxeSpeedPct;
                            currentPayload.ToolPickaxeSpeedPct = equipUpdate.ToolPickaxeSpeedPct;
                            currentPayload.ToolRodSpeedPct = equipUpdate.ToolRodSpeedPct;
                            currentPayload.ToolAxeYieldPct = equipUpdate.ToolAxeYieldPct;
                            currentPayload.ToolPickaxeYieldPct = equipUpdate.ToolPickaxeYieldPct;
                            currentPayload.ToolRodYieldPct = equipUpdate.ToolRodYieldPct;
                            currentPayload.ToolAxeRareFindPct = equipUpdate.ToolAxeRareFindPct;
                            currentPayload.ToolPickaxeRareFindPct = equipUpdate.ToolPickaxeRareFindPct;
                            currentPayload.ToolRodRareFindPct = equipUpdate.ToolRodRareFindPct;
                        }
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
                    // Modul: the second half of the kind-of-work check (see the
                    // ChangeActivity handler). The row was already written;
                    // leaving the payload alone is enough, because the
                    // checkpoint writes the live activity back over it.
                    if (CharacterSlotEngine.IsKindOfWorkTakenByParkedSlot(in currentPayload, activityChange.TargetActivityId))
                    {
                        SwapSlotIntoActiveRegister(ref currentPayload, targetSlotIndex);
                        _playerRegistry.EnqueueCommandResult(activityChange.PlayerId, (byte)Network.CommandResultCode.NodeOccupied);
                        continue;
                    }
                    ApplyActivityChangeToPayload(ref currentPayload, activityChange.TargetActivityId);
                    SwapSlotIntoActiveRegister(ref currentPayload, targetSlotIndex);
                }

                // Modul: larder. LarderEngine has taken the food from the chest;
                // this adds it to the live slot (a delta - see
                // LarderTickCoordinator), so restocking mid-fight takes effect
                // on the next tick, and the checkpoint persists it.
                //
                // (The crafting drain that used to sit here, with its own
                // "Modul: crafting as an assignable job" comment, moved to
                // CraftingTickCoordinator.cs along with that comment - kept in
                // one place now instead of two, per code review on Task 1.20.)
                CraftingTickCoordinator.DrainCraftingTicks(_safeDispatch, _craftingEngine);

                // The village production the tick made - written off the tick
                // under the Warehouse cap (see ProcessPassiveVillageTick).
                VillageTickCoordinator.DrainProductionGrants(_safeDispatch, _contextFactory);

                LarderTickCoordinator.DrainNotifications(_playerRegistry, _activePlayers, _safeDispatch, _larderEngine);
                DungeonTickCoordinator.DrainXpGrants(_playerRegistry, _activePlayers);
                PotionShelfTickCoordinator.DrainDeltas(_activePlayers, _safeDispatch, _contextFactory);

                // Task 88: rebirths asked for over REST - suspend, flush, and
                // let the flush's continuation reset and reload.
                FolkIdle.Server.Domain.Progression.RebirthTickCoordinator.Drain(_playerRegistry, _activePlayers, _checkpointManager);

                GuildFanoutTickCoordinator.DrainWarScoreboard(_playerRegistry, _activePlayers, _guildMembersIndex);

                InventoryCensusTickCoordinator.DrainCensus(_playerRegistry, _activePlayers);

                InventoryCensusTickCoordinator.DrainLootDrops(_playerRegistry, _activePlayers);

                VillageChestTickCoordinator.DrainAutoSalvage(_playerRegistry, _activePlayers);

                VillageChestTickCoordinator.DrainChestSaleGold(_playerRegistry, _activePlayers);

                VillageChestTickCoordinator.DrainChestSettings(_playerRegistry, _activePlayers);

                AutomationRulesTickCoordinator.Drain(_playerRegistry, _activePlayers);

                PayloadSnapshotTickCoordinator.Drain(_playerRegistry, _activePlayers);

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

                GreatWorksTickCoordinator.DrainUpdates(_activePlayers);
                // A purchase whose save failed or found the item already owned
                // gives its price back. A player who logged out in the
                // milliseconds between spend and save is not in the map and
                // the refund is dropped - accepted, the window is one save.
                EventShopTickCoordinator.DrainRefunds(_activePlayers);
                EventShopTickCoordinator.DrainPetDrops(_playerRegistry, _safeDispatch, _contextFactory);
                SeasonalBossTickCoordinator.DrainClears(_playerRegistry, _safeDispatch, _contextFactory);

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
                                        // Modul: A DUMP FROM ANOTHER BUILD IS NOT THIS PAYLOAD. The
                                        // dump is the raw struct, so any layout change (a new field,
                                        // the rank buffer growing to 128) makes an old node's dump the
                                        // wrong size, and MemoryMarshal.Read would throw on a short one
                                        // or misread a long one. Disconnect instead: the client signs
                                        // in again and the session loads from the database.
                                        if (stateDump.Length != System.Runtime.CompilerServices.Unsafe.SizeOf<TickStatePayload>())
                                        {
                                            _networkSystem.ForceDisconnect(pId);
                                            return;
                                        }
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
                                        await transaction.SafeRollbackAsync();
                                    }
                                }
                                catch
                                {
                                    await transaction.SafeRollbackAsync();
                                }
                            });
                        }
                        continue;
                    }
                    else if (cmd.Command == CommandType.Login)
                    {
                        long tId = cmd.TargetId;
                        _playerRegistry.RegisterPlayer(tId);
                        // Funnel steps 11-12 (a day-1 / day-7 return) and the
                        // re-arm of the per-session guard. See FunnelRecorder.
                        FunnelRecorder.BeginSession(tId);
                        SafeDispatchAsync("Login", tId, async () => {
                            // Modul: A LOGIN READS THE ROW ONLY AFTER THE LAST
                            // SESSION'S LOGOUT HAS WRITTEN IT (task 43). The
                            // logout flush is queued on CheckpointWriter now,
                            // not committed before this command is even read -
                            // so an F5 (socket closes, socket opens) could load
                            // the pre-logout row, and the logout's commit would
                            // then leave this new session one epoch behind the
                            // database: a false split-brain, mailed and
                            // disconnected. The fence waits for this player's
                            // queued jobs, and only theirs.
                            await _checkpointManager.WaitForPendingFlushesAsync(tId);
                            var payload = await _checkpointManager.LoadPlayerState(tId);

                            long currentUnixTimestamp = GameClock.UtcNowOffset.ToUnixTimeSeconds();
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

                            // Task 54: any level chest the account is owed -
                            // the backfill on a first login after release, and
                            // anything a missed grant left behind. Idempotent.
                            CosmeticGrantEngine.NoteLevel(payload.PlayerId, payload.CurrentLevel);

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
                        case CommandGateVerdict.RefuseStale:
                            // The command never runs; the player is told, and
                            // the next broadcast carries the current epoch.
                            _playerRegistry.EnqueueCommandResult(routingPlayerId, (byte)CommandResultCode.StaleClientState);
                            currentPayload.IsDirty = true;
                            continue;
                    }

                    // Modul: A REBIRTH IN FLIGHT TAKES NO COMMANDS (task 88).
                    // The payload is the old life, already flushed and about
                    // to be replaced by the reset. Anything that flushed it
                    // again - a market command, a ReloadState an engine
                    // enqueued - would queue a stale snapshot behind the
                    // rebirth, which the epoch fence then refuses as a split
                    // brain (compensation mail, forced disconnect). Answered,
                    // not dropped: the client is told its state is stale, and
                    // the reload arrives a moment later. Logout alone passes,
                    // and its branch knows not to flush (below).
                    if (currentPayload.RebirthPending && cmd.Command != CommandType.Logout)
                    {
                        if (cmd.Command != CommandType.ReloadState)
                        {
                            _playerRegistry.EnqueueCommandResult(routingPlayerId, (byte)CommandResultCode.StaleClientState);
                        }
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
                                if (AntiCheatTelemetryEngine.ChallengeQuarantineEnforced)
                                {
                                    currentPayload.IsQuarantined = true;
                                    currentPayload.Quarantine_Active = true;
                                    _antiCheatTelemetryEngine?.RequestShadowBan(routingPlayerId, 54, 3);
                                }
                                else
                                {
                                    long nowMs = GameClock.MonotonicMs;
                                    AntiCheatTelemetryEngine.LogChallengeRun(
                                        routingPlayerId, 3,
                                        nowMs - currentPayload.LastClientCommandAtMs,
                                        nowMs - currentPayload.ActiveChallengeIssuedAtMs,
                                        currentPayload.LogicEpochCounter);
                                    currentPayload.ConsecutiveChallengeMisses = 0;
                                }
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

                            // Modul: ONE CHARACTER PER KIND OF WORK, checked
                            // against the LIVE payload. The database row lags
                            // the session by up to a checkpoint: a death, an
                            // automation rule or a deploy still in the queue
                            // changes the payload first. Checked here so a
                            // refusal costs no database round trip, and again
                            // when the change is applied (the drain), because
                            // two deploys sent in one breath both pass here.
                            long[] liveActivities = LiveSlotActivities(ref currentPayload);
                            int liveSlot = ResolveSlotIndexForCharacter(ref currentPayload, characterId);
                            if (liveSlot >= 0 && CharacterSlotEngine.IsActivityOccupiedByAnotherSlot(liveActivities, liveSlot, targetActivityId))
                            {
                                _playerRegistry.EnqueueCommandResult(routingPlayerId, (byte)CommandResultCode.NodeOccupied);
                                continue;
                            }

                            SafeDispatchAsync("Character.ChangeActivity", pId, async () => {
                                var resultCode = await ChangeCharacterActivityAsync(pId, characterId, targetActivityId, liveActivities, liveSlot);
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
                            // The legacy path is slot 1 in the register; the
                            // same rule applies to it.
                            if (CharacterSlotEngine.IsKindOfWorkTakenByParkedSlot(in currentPayload, cmd.TargetId))
                            {
                                _playerRegistry.EnqueueCommandResult(routingPlayerId, (byte)CommandResultCode.NodeOccupied);
                                continue;
                            }
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
                        //
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
                        //
                        // Modul: CHECKPOINTS OFF THE TICK THREAD (task 43). The
                        // flush now commits on CheckpointWriter and the reload
                        // is its continuation, so "flush first, then reload" is
                        // still the order - the load runs on the writer after
                        // the commit and sees every earlier checkpoint of this
                        // player. A reload runs even when the flush fails
                        // (FlushJob.RunThenOnFailure), as it always did: an
                        // engine has already committed a change the player must
                        // see. The failed flush's gold comes back via its ack,
                        // which the tick applies BEFORE this payload lands, and
                        // StateReloadMerge carries it across. A continuation
                        // that throws disconnects the player, as SafeDispatch
                        // did here before.
                        long reloadPlayerId = currentPayload.PlayerId;
                        var reloadCheckpoints = _checkpointManager;
                        var reloadRegistry = _playerRegistry;
                        _checkpointManager.RequestFlush(ref currentPayload, FlushReason.Reload, then: async () =>
                        {
                            var reloaded = await reloadCheckpoints.LoadPlayerState(reloadPlayerId);
                            reloaded.IsSuspended = false;
                            reloadRegistry.StateReloadQueue.Enqueue(reloaded);
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
                        currentPayload.LastLogoutTimestamp = GameClock.UtcNowOffset.ToUnixTimeSeconds();
                        currentPayload.IsDirty = true;
                        // Modul: task 88. A rebirth in flight has already
                        // flushed everything this payload holds, and is about
                        // to reset the rows it would write; flushing the old
                        // life now would only queue a stale snapshot behind the
                        // reset for the epoch fence to refuse. The next login
                        // waits for the rebirth on the writer
                        // (WaitForPendingFlushesAsync) like any other flush.
                        //
                        // Modul: checkpoints off the tick thread (task 43). The
                        // last flush of a session is queued like every other;
                        // CheckpointWriter retries it (a logout has no next
                        // tick to retry on), then banks its gold alone, then
                        // writes a CHECKPOINT-DEADLETTER line - where a failed
                        // synchronous flush here used to lose it in silence.
                        // The next Login waits for it: see the Login branch.
                        if (!currentPayload.RebirthPending)
                        {
                            _checkpointManager.RequestFlush(ref currentPayload, FlushReason.Logout);
                        }
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
                            GameClock.MonotonicMs - currentPayload.ActiveChallengeIssuedAtMs > AntiCheatTelemetryEngine.ChallengeResponseWindowMs)
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
                                GameClock.MonotonicMs - currentPayload.LastClientCommandAtMs
                                    <= AntiCheatTelemetryEngine.ChallengeResponseWindowMs;

                            if (!clientWasTalking)
                            {
                                currentPayload.ConsecutiveChallengeMisses = 0;
                            }
                            else if (++currentPayload.ConsecutiveChallengeMisses
                                     >= AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit)
                            {
                                if (AntiCheatTelemetryEngine.ChallengeQuarantineEnforced)
                                {
                                    currentPayload.IsQuarantined = true;
                                    currentPayload.Quarantine_Active = true;
                                    _antiCheatTelemetryEngine?.RequestShadowBan(currentPayload.PlayerId, 54, 4);
                                }
                                else
                                {
                                    long nowMs = GameClock.MonotonicMs;
                                    AntiCheatTelemetryEngine.LogChallengeRun(
                                        currentPayload.PlayerId, 4,
                                        nowMs - currentPayload.LastClientCommandAtMs,
                                        nowMs - currentPayload.ActiveChallengeIssuedAtMs,
                                        currentPayload.LogicEpochCounter);
                                    currentPayload.ConsecutiveChallengeMisses = 0;
                                }
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
                            var eventsBefore = LockstepEventProbe.Capture(in currentPayload);
                            ProcessTick(ref currentPayload);
                            _checkpointManager.TrackState(ref currentPayload);
                            SimLockstep.Conductor?.NotifyAll(currentPayload.PlayerId, LockstepEventProbe.Diff(eventsBefore, in currentPayload), _lockstepTick);
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

                if (_ticksSinceLastBroadcast < int.MaxValue) _ticksSinceLastBroadcast++;
                if (_ticksSinceLastBroadcast >= _ticksPerBroadcast || _forceBroadcastThisTick)
                {
                    _metrics.ThrottledPacketsDropped = _networkSystem.GetThrottledCounter();
                    _ticksSinceLastBroadcast = 0;
                    _forceBroadcastThisTick = false;
                    _lastLockstepBroadcastTick = _lockstepTick;

                    long broadcastSnapshotStartTimestamp = Stopwatch.GetTimestamp();
                    FolkIdleEventSource.Log.BroadcastSnapshotStart(_activePlayers.Count);

                    foreach (var kvp in _activePlayers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_activePlayers, kvp.Key);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                        {
                            // Modul: ONE CHALLENGE A GAME SECOND UNDER LOCKSTEP TOO (20-bot
                            // smoke, 2026-11-03). Lockstep broadcasts in the tick before ANY
                            // bot's wake, which with twenty bots is nearly every tick; a fresh
                            // challenge each time meant up to ten answers a game second, each
                            // spending a token from a bucket that refills five a second - and
                            // the flood abort closed seven sockets without a word. Production
                            // issues one a second (one broadcast a second); so does this.
                            bool challengeDue = conductor == null
                                || GameClock.MonotonicMs - currentPayload.ActiveChallengeIssuedAtMs >= 1000;
                            if ((currentPayload.ActiveChallengeSeed == 0 || currentPayload.ActiveChallengeAnswered != 0) && challengeDue)
                            {
                                currentPayload.ActiveChallengeSeed = AntiCheatTelemetryEngine.GenerateChallengeSeed(currentPayload.PlayerId, currentPayload.LogicEpochCounter, _metrics.TotalTicksProcessed);
                                currentPayload.ActiveChallengeIssuedAtMs = GameClock.MonotonicMs;

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

                            // Modul: Combat System Overhaul - the
                            // Accuracy/Armor/BlockStrength values broadcast to
                            // the client must never drift from what governed
                            // that tick's combat rolls. TickStatePayload does
                            // not cache CombatStats across the tick/broadcast
                            // boundary, so it is recomputed here - through
                            // LiveCombatStats itself since 2026-10-29. This
                            // used to be a hand-copied Calculate call, and a
                            // copy is exactly what would have shown the
                            // Fortitude armour the tick fights with as absent.
                            var broadcastCombatStats = LiveCombatStats(in currentPayload);

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
                                PlayerHp = (int)Math.Min(int.MaxValue, currentPayload.PlayerHp / 1000L),

                                // Modul: the same call the spawn makes, so the
                                // bar's maximum and the monster's actual health
                                // come from one rule. The client used to
                                // hand-copy this as `MaxHp * 5` and get First
                                // Blood and endgame scaling wrong.
                                // SpawnHpFor, so an armed Ascension step or seasonal
                                // boss tier draws the health it really spawned with.
                                CurrentMonsterMaxHp = currentPayload.CurrentMonsterId > 0
                                    ? (int)(SpawnHpFor(in currentPayload) / 1000L)
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
                                LogicEpochCounter = (uint)(currentPayload.LogicEpochCounter & 0xFFFFFFFF),
                                PremiumCurrencyBalance = (uint)currentPayload.PremiumCurrency,
                                EventCurrency = (uint)Math.Max(0, currentPayload.EventCurrency),
                                EventCurrencyEarnedToday = (ushort)Math.Clamp(currentPayload.EventCurrencyEarnedToday, 0, ushort.MaxValue),
                                SeasonalBossTier = currentPayload.SeasonalBossTier,
                                SeasonalBossClearedMask = currentPayload.SeasonalBossClearedMask,
                                LegacyShardBalance = currentPayload.LegacyShardBalance,
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
                                ActiveGatheringPotionId = currentPayload.ActiveGatheringPotionId,
                                GatheringPotionDurationMs = currentPayload.GatheringPotionDurationMs,
                                PotionShelf1_ItemId = (ushort)Math.Clamp(currentPayload.PotionShelf1_ItemId, 0, ushort.MaxValue),
                                PotionShelf1_Count = (ushort)Math.Clamp(currentPayload.PotionShelf1_Count, 0, ushort.MaxValue),
                                PotionShelf2_ItemId = (ushort)Math.Clamp(currentPayload.PotionShelf2_ItemId, 0, ushort.MaxValue),
                                PotionShelf2_Count = (ushort)Math.Clamp(currentPayload.PotionShelf2_Count, 0, ushort.MaxValue),
                                PotionShelf3_ItemId = (ushort)Math.Clamp(currentPayload.PotionShelf3_ItemId, 0, ushort.MaxValue),
                                PotionShelf3_Count = (ushort)Math.Clamp(currentPayload.PotionShelf3_Count, 0, ushort.MaxValue),
                                PotionAutoDrinkMask = currentPayload.PotionAutoDrinkMask,
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
                                ToolGatherSpeedPct = ToolLoadoutResolver.ClampSum(currentPayload.ToolAxeSpeedPct, currentPayload.ToolPickaxeSpeedPct, currentPayload.ToolRodSpeedPct),
                                // The wire keeps the three tools' sums - no client
                                // reads them, and the per-tool split is the tick's.
                                ToolGatherYieldPct = ToolLoadoutResolver.ClampSum(currentPayload.ToolAxeYieldPct, currentPayload.ToolPickaxeYieldPct, currentPayload.ToolRodYieldPct),
                                ToolRareFindPct = ToolLoadoutResolver.ClampSum(currentPayload.ToolAxeRareFindPct, currentPayload.ToolPickaxeRareFindPct, currentPayload.ToolRodRareFindPct),
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
                                    Engine.AchievementMilestones.EvaluateTreasuryTier(Engine.GoldLedger.KnownLifetimeSpent(currentPayload.PlayerId))
                                    + Engine.AchievementMilestones.EvaluateForgingTier(
                                        currentPayload.ForgeUpgradeCount, currentPayload.HighestForgeSynthesisTier)
                                    + Engine.AchievementMilestones.EvaluateLogisticsTier(currentPayload.LifetimeHarvests)
                                    + Engine.AchievementMilestones.EvaluateAlchemistTier(currentPayload.LifetimePotionsDrunk)),
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
                                // Task 51: personal records, hydrated at login.
                                BestHit = currentPayload.BestHit,
                                BossBestKillTenthsR1 = (ushort)Math.Clamp(currentPayload.BossBestKillTenthsR1, 0, ushort.MaxValue),
                                BossBestKillTenthsR2 = (ushort)Math.Clamp(currentPayload.BossBestKillTenthsR2, 0, ushort.MaxValue),
                                BossBestKillTenthsR3 = (ushort)Math.Clamp(currentPayload.BossBestKillTenthsR3, 0, ushort.MaxValue),
                                BossBestKillTenthsR4 = (ushort)Math.Clamp(currentPayload.BossBestKillTenthsR4, 0, ushort.MaxValue),
                                BossBestKillTenthsR5 = (ushort)Math.Clamp(currentPayload.BossBestKillTenthsR5, 0, ushort.MaxValue),
                                AscensionStep = currentPayload.AscensionStep,
                                LastDeathMonsterId = currentPayload.LastDeathMonsterId,
                                LastDeathTick = currentPayload.LastDeathTick,
                                LastHitWasCrit = currentPayload.LastHitWasCrit,
                                EquippedWeaponKind = currentPayload.EquippedWeaponKind,
                                TicksSinceLastFlush = currentPayload.TicksSinceLastFlush,
                                // Modul: task 45. The EFFECTIVE offline cap,
                                // Vodnik extension included, so the client's
                                // local "they stop earning in 1 h" notification
                                // never mirrors the 12 h rule itself. Derived
                                // from VodnikMasteryLevel, which the login
                                // hydrates, so a relogin cannot read it as 0.
                                OfflineCapSeconds = (int)OfflineSimulationEngine.EffectiveOfflineCapSeconds(
                                    currentPayload.VodnikMasteryLevel, currentPayload.GreatWorksStagesPacked)
                            };
                            WriteParkedSlotBars(ref packet, in currentPayload);
                            WriteSeasonalEvent(ref packet);
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

                }
                finally
                {
                    if (tickLockTaken)
                    {
                        Monitor.Exit(_activePlayers);
                    }
                }

                stopwatch.Stop();
                long tickEndTimestamp = Stopwatch.GetTimestamp();
                long tickMicros = (tickEndTimestamp - tickStartTimestamp) * 1_000_000L / Stopwatch.Frequency;
                _recentTickMicros[(int)(_recentTickCount % RecentTickWindow)] = (int)Math.Min(tickMicros, int.MaxValue);
                Interlocked.Increment(ref _recentTickCount);
                _metrics.TotalTicksProcessed++;
                long tickElapsedForMetricsMs = stopwatch.ElapsedMilliseconds;
                _metrics.LastExecutionTimeMs = tickElapsedForMetricsMs;
                _metrics.TickDurationSumMicros += tickMicros;
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

                // Modul: Task 43, 2d. Paced against an absolute schedule, not
                // "interval minus this tick's duration" - that lost every
                // overrun and every oversleep for good, so live progress ran
                // below 10 Hz whenever the box was busy. See TickPacer.
                if (conductor != null)
                {
                    conductor.CompleteTick(_lockstepTick);
                    // The game day turned during this tick (or during the jump
                    // that led to it): wake whoever asked for the daily reset.
                    var gameDay = GameClock.UtcNow.Date;
                    if (gameDay != _lockstepLastDay)
                    {
                        if (_lockstepLastDay != default) conductor.NotifyEveryone(SimWakeReason.DailyReset, _lockstepTick);
                        _lockstepLastDay = gameDay;
                    }
                    continue;
                }

                long delay = pacer.DelayUntilNextTick(Stopwatch.GetTimestamp());
                _metrics.TicksDropped = pacer.TicksDropped;
                _metrics.CatchUpTicks = pacer.CatchUpTicks;
                if (delay > 0)
                {
                    // Round up: waking early would start the tick before it is due.
                    int sleepMs = (int)((delay * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
                    Thread.Sleep(sleepMs);
                }
            }
        }

        /// <summary>Checkpoint jobs allowed in the queue before lockstep stops starting ticks.</summary>
        internal const int LockstepCheckpointQueueBound = 16;

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
        // Characters 2 and 3's bars onto the wire, from their parked state.
        // Called outside the slot loop, so the register holds slot 1 and the
        // parked copies are slots 2 and 3 in order.
        // The calendar's view of the seasonal event, for the chip and the theme.
        private static void WriteSeasonalEvent(ref StateUpdatePacket packet)
        {
            var (current, phase) = Domain.Progression.SeasonalEventRegistry.Current(GameClock.UtcNowOffset);
            packet.SeasonalEventId = (byte)(current?.Id ?? 0);
            packet.SeasonalEventPhase = (byte)phase;
        }

        internal static void ApplyBulkExperience(ref TickStatePayload payload, long xpGain, int activeRaceId = 0)
        {
            if (xpGain <= 0)
            {
                return;
            }

            // Modul 13.4.3: -20% character XP generation while an early
            // mentorship termination penalty is active (see MentorshipEngine).
            if (payload.XpPenaltyExpiresEpoch > GameClock.UtcNowOffset.ToUnixTimeSeconds())
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
            if (levelsGained > 0) CosmeticGrantEngine.NoteLevel(payload.PlayerId, payload.CurrentLevel);

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
                    await transaction.SafeRollbackAsync();
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
                await transaction.SafeRollbackAsync();
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

        public void ProcessTick(ref TickStatePayload payload)
        {
            int localXpMultiplier = GlobalEngineState.GlobalXpMultiplier;
            int localDropMultiplier = GlobalEngineState.GlobalDropMultiplier;

            long now = GameClock.UtcNowOffset.ToUnixTimeSeconds();

            // Task 85: the automation notes (6, 7) are exempt for the same
            // reason - a rule acted and the player should get to see that.
            if (payload.ActiveActivityId > 0 && !Network.ActivityHaltReason.IsStandingNote(payload.ActivityHaltReason))
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

            // Task 87: a kill that missed an Ascension step's time limit is ANSWERED,
            // never left for the player to infer from a boss that simply came
            // back. The combat tick is static and holds no registry, so it leaves
            // the verdict on the payload and this instance method sends it. (A
            // CLEAR is announced by CosmeticGrantEngine once its reward is saved.)
            if (payload.AscensionPendingResult != 0)
            {
                _playerRegistry.EnqueueCommandResult(payload.PlayerId, (byte)CommandResultCode.AscensionTooSlow);
                payload.AscensionPendingResult = 0;
            }

            // Modul: NO SIMULATION SPEED (owner, 2026-09-28). The chrono-funded
            // 2x/4x branch went with the chrono bank; the second path that sat
            // here replayed AccumulatedTimeBankMs at up to 4x, and nothing had
            // filled that bank since the chrono deletion - only nine accounts
            // still held a leftover balance (the largest about 6.8 days), which
            // was 4x progress for as long as it lasted. Offline catch-up already
            // pays the same XP and loot as being online, so the Store's speed
            // buttons offered nothing honest. One tick is one tick.
        }

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        // Zero-allocation: pure struct field arithmetic, no LINQ, no DB access.
        // Independent of ActiveActivityId - runs every call regardless of what
        // the player is currently doing, unlike ProcessSubTick.
        // Tolerance absorbing float32 summation drift (e.g. repeatedly adding
        // 0.01f can land just under a whole-unit threshold after thousands of
        // ticks) without being large enough to trigger a spurious extra unit.
        private const float ProductionAccumulatorEpsilon = 1e-4f;

        /// <summary>One minute of ticks between production writes.</summary>
        internal const int VillageProductionGrantIntervalTicks = 600;

        /// <summary>
        /// Village production the tick has made and not yet written. Drained on
        /// the engine loop (VillageTickCoordinator.DrainProductionGrants) into
        /// VillageManagementEngine.GrantProductionAsync - the Warehouse cap and
        /// the material ledger, the offline window's own grant.
        /// </summary>
        public static readonly System.Collections.Concurrent.ConcurrentQueue<VillageProductionGrant> VillageProductionQueue = new();

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
        internal const int TickDurationMs = 100;

        /// <summary>The swing interval with no attack speed at all (CombatDamageModel.AttackIntervalMs).</summary>
        internal const int BaseAttackIntervalMs = 1500;

        /// <summary>What zero dodge leaves a monster short of a certain hit: 95% at zero.</summary>
        internal const float MonsterHitCeilingGap = 0.05f;

        internal static bool HasCrossedInterval(int tickAccumulator, int intervalMs)
        {
            if (intervalMs <= 0) return false;
            if (tickAccumulator <= 0) return false;

            long elapsedMs = (long)tickAccumulator * TickDurationMs;
            long previousMs = elapsedMs - TickDurationMs;

            return (elapsedMs / intervalMs) > (previousMs / intervalMs);
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

        // Modul: widened from private to internal so the Progression-domain
        // tick coordinators (InheritanceTickCoordinator, SkillTreeTickCoordinator)
        // can call this without duplicating the switch. No behaviour change -
        // same assembly, same tick-thread-only call graph.
        internal static void SetSkillTreeLevel(ref TickStatePayload payload, int branchId, byte level)
        {
            // Modul: by id into the rank buffer, not a switch over named
            // fields. The switch had to grow by a case for every node and
            // silently dropped an id it did not list - the shape of the
            // "written, never read" defects this codebase keeps finding.
            if ((uint)branchId < (uint)SkillRankBuffer.Length) payload.SkillRanks[branchId] = level;
        }

        internal static byte GetSkillTreeLevel(in TickStatePayload payload, int nodeId)
            => (uint)nodeId < (uint)SkillRankBuffer.Length ? payload.SkillRanks[nodeId] : (byte)0;

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

        // Modul: phase 2b.2 (roadmap 5). While CombatSimulator replays an away
        // window through RunCombatTick, everything the tick would SEND - the
        // event feed, loot and codex queues, boss notes, announcements, pet
        // drops - is counted into this tally instead, so an offline window
        // never reaches a live queue and the caller decides what to pay. The
        // game state and every dice roll are untouched, which is what keeps a
        // simulated window identical to the same window ticked live.
        [ThreadStatic] private static CombatSimulator.Tally? t_simulating;

        /// <summary>The tally of the simulation running on this thread, or null on the live tick.</summary>
        internal static CombatSimulator.Tally? Simulating => t_simulating;

        // Modul: phase 2b.3. CombatSimulator derives the character's stats
        // after every tick it runs, to find the next swing (QuietTicksAhead),
        // and the next tick derived them again from the same inputs - the
        // derivation was the largest single cost of a simulated window. The
        // quiet ticks it skips in between touch only the fight clock, health
        // and the cooldown and ration counters, none of which the stats read,
        // so the simulator hands its copy to the next tick instead. The live
        // tick never sets this. CombatSimulatorTests' byte-for-byte equality
        // is the guard: a stale copy would change a swing and part the runs.
        [ThreadStatic] private static bool t_hasCarriedStats;
        [ThreadStatic] private static CombatStats t_carriedStats;

        /// <summary>What a profession's gathering speed is built from.</summary>
        internal readonly record struct GatherSpeedTerms(int MasteryLevel, int ToolTier, int VillageLevel, int ExtraPct)
        {
            public int TotalPct => GatheringToolEngine.TotalSpeedBonusPct(MasteryLevel, ToolTier, VillageLevel, ExtraPct);
        }

        /// <summary>What one harvest rolls: the roll count in percent, and luck's flat weight.</summary>
        internal readonly struct GatheringYield
        {
            /// <summary>Rolls per harvest x100: one guaranteed roll per whole hundred, the remainder a percent chance of one more.</summary>
            public int MultiplierPct { get; init; }
            /// <summary>Added to every loot-table entry's weight (LootLuckPct / 10).</summary>
            public int LuckWeightBonus { get; init; }

            /// <summary>LootLuckPct as a flat weight bonus: a tenth of it, never negative.</summary>
            public static int LuckWeightBonusFor(float lootLuckPct)
            {
                int bonus = (int)(lootLuckPct * 0.1f);
                return bonus < 0 ? 0 : bonus;
            }
        }
    }
}
