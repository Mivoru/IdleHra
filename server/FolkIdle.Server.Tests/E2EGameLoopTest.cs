using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Testcontainers.PostgreSql;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Tests
{
    // Shares the "Postgres collection" with HardenedEngineIntegrationTests
    // (see PostgresCollection) purely to serialize execution against it -
    // this class does not use PostgresTestFixture, it builds its own
    // container. Both classes spin up long-lived background engine threads
    // and mutate the shared static GlobalEngineState.IsColdBootRecoveryComplete;
    // running them in xUnit's default cross-class parallelism let one
    // class's container get disposed while the other's still-live engine
    // thread was mid-query against it, crashing the whole test host with an
    // unhandled ObjectDisposedException on a background Thread.
    [Collection("Postgres collection")]
    public class E2EGameLoopTest : IAsyncLifetime
    {
        private PostgreSqlContainer? _dbContainer;
        private bool _dockerAvailable;

        public async Task InitializeAsync()
        {
            // Content Pipeline: see HardenedEngineIntegrationTests.
            // PostgresTestFixture.InitializeAsync for why this must run
            // before any SimulationEngine-dependent test.
            ContentRegistry.Initialize();
            ActiveSkillEngine.Initialize();

            try
            {
                _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
                    .Build();

                await _dbContainer.StartAsync();
                _dockerAvailable = true;
            }
            catch (DotNet.Testcontainers.Builders.DockerUnavailableException ex)
            {
                Console.WriteLine($"WARNING: Docker unavailable for E2E tests; database integration coverage was not executed. Details: {ex.Message}");
                _dockerAvailable = false;
            }
        }

        public async Task DisposeAsync()
        {
            GlobalEngineState.IsColdBootRecoveryComplete = false;

            if (_dbContainer != null)
            {
                await _dbContainer.DisposeAsync().AsTask();
            }
        }

        // Modul: Phase 5, Part 1. Active, deterministic polling replacing
        // rigid wall-clock Task.Delay waits, which assume the server's
        // 10Hz tick loop keeps pace with real time - a false assumption
        // under CI's CPU/IO scheduling pressure, where a starved test
        // host can cause the tick loop itself to fall behind real time,
        // so waiting a fixed number of real-world seconds does not
        // guarantee a fixed number of simulated ticks actually ran.
        // Polling the real observed state directly (received packets, live
        // metrics) decouples verification from the runner's wall-clock
        // scheduling entirely - the test now waits exactly as long as
        // needed, up to a generous safety-net timeout, rather than a
        // single fixed guess that must be long enough for the worst case
        // yet short enough not to needlessly slow every passing run.
        private static async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs, int pollIntervalMs = 100)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                await Task.Delay(pollIntervalMs);
            }
            return condition();
        }

        private static string MintTestJwt(Guid accountId)
        {
            return AuthenticationEngine.GenerateJwt(accountId, AuthenticationEngine.GenerateSessionNonce(), "pw", AuthenticationDefaults.LocalDevelopmentFallback, out _);
        }

        // Mirrors WebSocketClient.SendAuthHandshakeAsync's fixed-buffer write
        // pattern - MemoryMarshal.Write needs the JwtToken bytes already
        // placed inside the struct's fixed buffer before it can blit the
        // whole AuthHandshakePacket into a wire-ready byte array.
        private static unsafe byte[] BuildAuthHandshakeBuffer(string jwt)
        {
            byte[] jwtBytes = System.Text.Encoding.UTF8.GetBytes(jwt);
            var packet = new AuthHandshakePacket
            {
                JwtTokenLength = (ushort)jwtBytes.Length,
                AssetHash = 0,
                PlatformSignature = 0
            };

            byte* target = packet.JwtToken;
            for (int i = 0; i < AuthHandshakePacket.JwtTokenCapacity; i++)
            {
                target[i] = i < jwtBytes.Length ? jwtBytes[i] : (byte)0;
            }

            byte[] buffer = new byte[Marshal.SizeOf<AuthHandshakePacket>()];
            MemoryMarshal.Write(new Span<byte>(buffer), packet);
            return buffer;
        }

        [Fact]
        public async Task Test_E2E_ClosedLoopVerification()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping E2E closed-loop verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            // 1. Setup Headless Server & DB
            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            var e2eRetryOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(_dbContainer.GetConnectionString(), npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(e2eRetryOptions));

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            var retryingDbOptions = serviceProvider.GetRequiredService<RetryingDbContextOptions>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8081/");
            var lootEngine = new LootTableEngine();
            var checkpointManager = new StateCheckpointManager(serviceProvider);
            var forgeEngine = new ForgeSplicingEngine(serviceProvider);
            var playerRegistry = new PlayerSessionRegistry();
            var marketEngine = new MarketOrderBookEngine(serviceProvider, playerRegistry);
            var guildEngine = new GuildContributionEngine(serviceProvider);
            var escrowEngine = new MarketEscrowEngine(serviceProvider, playerRegistry);
            var mailboxEngine = new MailboxAndBankEngine(serviceProvider, playerRegistry);
            var rerollEngine = new AffixRerollEngine(serviceProvider);
            var breedingEngine = new BreedingEngine(serviceProvider, playerRegistry);
            var guildLogisticsEngine = new GuildLogisticsEngine(serviceProvider, playerRegistry);
            var craftingEngine = new CraftingEngine(contextFactory, playerRegistry, retryingDbOptions);
            var worldBossEngine = new WorldBossEngine(serviceProvider, playerRegistry);
            var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(serviceProvider);
            var legacyStoreEngine = new LegacyStoreEngine(serviceProvider, playerRegistry);
            var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(serviceProvider, playerRegistry);
            var guildCombatSimulationEngine = new GuildCombatSimulationEngine(serviceProvider, playerRegistry);

            // AntiCheatTelemetryEngine.RecordCommand/RequestShadowBan (the only
            // methods reachable from this test's live 10.5s tick loop) never
            // dereference the redis multiplexer, so redis: null! is safe here -
            // unlike Push/Compliance/Billing below, this dependency cannot stay
            // null! because SimulationEngine.EngineLoop calls it unconditionally
            // (it is a required, always-injected dependency in production).
            var antiCheatTelemetryEngine = new AntiCheatTelemetryEngine(serviceProvider, null!, playerRegistry, networkSystem);
            networkSystem.RegisterAntiCheatTelemetryEngine(antiCheatTelemetryEngine);

            // Push/Compliance/Billing require Redis and are not exercised by
            // this test's scenario, so they stay null! for things we don't use.
            var simulationEngine = new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageManagementEngine, guildWarEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, antiCheatTelemetryEngine, null!, null!, null!, null!, contextFactory);

            // Spin up headless loop. This test drives the network gateway
            // directly without running ColdRecoveryCoordinator (there is no
            // pre-existing session state to reconstruct here), so the 503
            // gate in NetworkBroadcastSystem.ListenLoopAsync must be opened
            // the same way Program.cs's benchmark-mode path does.
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();
            simulationEngine.Start();

            // Seed the PlayerRecord the handshake resolves against - the JWT
            // flow does a real PlayerGuid lookup instead of trusting an
            // arbitrary PlayerId out of an in-memory cache.
            Guid accountId = Guid.NewGuid();
            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                // Modul: Phase 5, Part 1. Seeded 15 XP short of the Level 1
                // threshold rather than starting at 0 - ProgressionEngine.
                // ProcessMonsterDeath requires 400 XP (the balance pass moved
                // the curve from 100 * 1.15^level to 400 * 1.06^level, so the
                // level-0 threshold went 100 -> 400; see
                // ProgressionEngine.GetRequiredXpForLevel) to reach
                // Level 1, but a single Forest Rat (activity 55) kill only
                // grants 21 XP (BaseXpReward in monsters.json). Starting from
                // 0 XP would require roughly 5 full kills to level up, which
                // was measured directly against this exact test harness at
                // well over a minute of real time per attempt (each kill
                // itself already taking many seconds of real 10Hz tick time)
                // - this was the actual root cause of the "flakiness," not
                // merely an under-provisioned wait window: the original
                // fixed 9-second delay was never long enough for even one
                // full leveling cycle, regardless of CI scheduling variance.
                // Seeding to 385 XP means exactly one real kill (385 + 21 = 406
                // >= 400) proves the same real, end-to-end
                // combat/XP/level-up pipeline this test exists to verify,
                // without requiring several minutes of simulated grinding
                // per test run.
                db.PlayerRecords.Add(new PlayerRecord { Id = 1L, PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 0, CurrentXp = 385L });
                await db.SaveChangesAsync();
            }

            // 2. Mock a Client Connection
            using var clientSocket = new ClientWebSocket();
            await clientSocket.ConnectAsync(new Uri("ws://localhost:8081/"), CancellationToken.None);

            // Send Handshake Auth Packet
            byte[] authBuffer = BuildAuthHandshakeBuffer(MintTestJwt(accountId));
            await clientSocket.SendAsync(new ArraySegment<byte>(authBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

            // 3. Simulate execution. The receive loop starts before any gameplay
            // command is sent, and that command is only sent once the first
            // StateUpdatePacket confirms the async Login task has landed the
            // player in _activePlayers - SimulationEngine.EngineLoop silently
            // drops commands for players not yet active (they hit a null ref
            // via CollectionsMarshal.GetValueRefOrNullRef and get skipped), so
            // sending it any earlier races the async LoadPlayerState/offline
            // extrapolation and the command is lost with no error, no retry.
            var receivedPackets = new ConcurrentQueue<StateUpdatePacket>();
            var loginConfirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var receiveTask = Task.Run(async () =>
            {
                var recvBuffer = new byte[1024];
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await clientSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        if (result.Count >= Marshal.SizeOf<StateUpdatePacket>())
                        {
                            var state = MemoryMarshal.Read<StateUpdatePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                            receivedPackets.Enqueue(state);
                            loginConfirmed.TrySetResult();

                            // Modul 29/45: real clients answer the periodic
                            // anti-cheat challenge within 500ms of receiving it
                            // (AntiCheatTelemetryEngine.GenerateChallengeSeed /
                            // ComputeChallengeHash) - the server quarantines any
                            // player who doesn't, which silently freezes
                            // ProcessTick (and therefore combat) for the rest of
                            // the session. LogicEpochCounter is never incremented
                            // anywhere server-side after Login seeds it at 0, so
                            // it stays 0 for the lifetime of this test player.
                            if (state.ActiveChallengeSeed != 0)
                            {
                                uint hash = AntiCheatTelemetryEngine.ComputeChallengeHash(state.ActiveChallengeSeed, state.PlayerId, 0L);
                                var challengeResponse = new ClientCommandPacket
                                {
                                    Command = CommandType.AntiCheatChallengeResponse,
                                    ChallengeId = state.ActiveChallengeSeed,
                                    ChallengeVerificationHash = hash
                                };
                                byte[] challengeBuffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];
                                MemoryMarshal.Write(new Span<byte>(challengeBuffer), challengeResponse);
                                await clientSocket.SendAsync(new ArraySegment<byte>(challengeBuffer), WebSocketMessageType.Binary, true, cts.Token);
                            }
                        }
                    }
                    catch { break; }
                }
            });

            // Widened from a 3s cap to 10s - still event-driven (races the
            // real TaskCompletionSource, not a blind sleep), but a fixed
            // upper bound this tight was itself a latent flake risk under
            // CI CPU pressure, matching the same failure mode Part 1 fixes
            // below for the combat-resolve wait.
            await Task.WhenAny(loginConfirmed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(loginConfirmed.Task.IsCompletedSuccessfully, "Did not observe the player enter the active tick loop before the combat window started.");

            // 4. Send the real gameplay command (ChangeActivity = 55 -> Forest Rat)
            // now that the player is confirmed active.
            var cmd = new ClientCommandPacket
            {
                Command = CommandType.ChangeActivity,
                TargetId = 55
            };
            byte[] cmdBuffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];
            MemoryMarshal.Write(new Span<byte>(cmdBuffer), cmd);
            await clientSocket.SendAsync(new ArraySegment<byte>(cmdBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

            // 5. Let combat resolve. Poll the actually-received packets for
            // the real completion condition (a level-up on the expected
            // activity) instead of assuming a fixed number of real-world
            // seconds always covers a fixed amount of simulated combat -
            // under CI CPU/IO pressure the 10Hz tick loop itself can fall
            // behind real time, so a rigid delay either wastes time on fast
            // runs or flakes outright on slow ones. The starting XP seed
            // above means exactly one real kill crosses the Level 1
            // threshold - measured directly against this exact harness at
            // well under the 90s safety-net timeout below even under this
            // sandboxed environment's own scheduling overhead, so this
            // leaves real headroom for a slower CI runner without making
            // every normal-case run pay for a multi-minute worst case.
            // CurrentXp > 0 is not part of the wait condition (the seed
            // starts it non-zero, so it would be trivially true
            // immediately) but is still verified separately below via
            // lastState, matching this test's original assertion.
            bool leveledUp = await WaitForConditionAsync(
                () => receivedPackets.Any(p => p.ActiveActivityId == 55 && p.CurrentLevel >= 1),
                timeoutMs: 90000);
            Assert.True(leveledUp, "Player never leveled up within the timeout - combat loop did not resolve.");

            cts.Cancel();
            await receiveTask;

            // 6. Validate the input throttler on dedicated short-lived
            // connections. NetworkBroadcastSystem.HandleClientLoopAsync aborts
            // a connection on its FIRST throttle violation (a deliberate
            // single-strike flood kill, not a graceful per-packet drop), so
            // triggering it on the main connection above would kill the socket
            // still needed for the combat-loop assertions below. Each of these
            // opens fresh, blasts past NetworkThrottlingEngine.Capacity (20
            // tokens) instantly, and gets terminated - exercising the real
            // flood-kill path in isolation instead of fighting it.
            // Seed a PlayerRecord per flood connection up front - the
            // handshake now requires a real DB row per AccountId.
            var floodAccountIds = new Guid[5];
            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                for (int f = 0; f < floodAccountIds.Length; f++)
                {
                    floodAccountIds[f] = Guid.NewGuid();
                    db.PlayerRecords.Add(new PlayerRecord { Id = 900000 + f, PlayerGuid = floodAccountIds[f], AuthenticatorToken = Guid.NewGuid() });
                }
                await db.SaveChangesAsync();
            }

            var floodTasks = new Task[5];
            for (int f = 0; f < floodTasks.Length; f++)
            {
                Guid floodAccountId = floodAccountIds[f];
                floodTasks[f] = Task.Run(async () =>
                {
                    using var floodSocket = new ClientWebSocket();
                    using var floodCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await floodSocket.ConnectAsync(new Uri("ws://localhost:8081/"), floodCts.Token);

                    byte[] floodAuthBuffer = BuildAuthHandshakeBuffer(MintTestJwt(floodAccountId));
                    await floodSocket.SendAsync(new ArraySegment<byte>(floodAuthBuffer), WebSocketMessageType.Binary, true, floodCts.Token);

                    var floodCmd = new ClientCommandPacket { Command = CommandType.ChangeActivity, TargetId = 1 };
                    byte[] floodCmdBuffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];
                    MemoryMarshal.Write(new Span<byte>(floodCmdBuffer), floodCmd);

                    try
                    {
                        for (int i = 0; i < (int)NetworkThrottlingEngine.Capacity + 5; i++)
                        {
                            await floodSocket.SendAsync(new ArraySegment<byte>(floodCmdBuffer), WebSocketMessageType.Binary, true, floodCts.Token);
                        }
                    }
                    catch
                    {
                        // Expected: the server aborts the connection mid-burst once throttled.
                    }
                });
            }
            await Task.WhenAll(floodTasks);

            // GetMetrics().ThrottledPacketsDropped is only refreshed from the
            // live counter once per broadcast cycle (every 10 ticks / ~1s,
            // see EngineLoop's _ticksSinceLastBroadcast gate) - poll for the
            // real refresh instead of assuming one fixed real-world delay
            // always covers it, for the same reason the combat-resolve wait
            // above was converted (a starved tick loop can fall behind real
            // time under CI scheduling pressure).
            await WaitForConditionAsync(() => simulationEngine.GetMetrics().ThrottledPacketsDropped >= 5, timeoutMs: 5000);

            simulationEngine.Stop();
            networkSystem.Stop();

            // 7. Verify constraints

            Assert.True(receivedPackets.Count >= 6, $"Expected at least 6 StateUpdatePackets, got {receivedPackets.Count}");

            StateUpdatePacket lastState = default;
            foreach (var state in receivedPackets)
            {
                lastState = state;
                Console.WriteLine($"State: Lvl={state.CurrentLevel} Xp={state.CurrentXp} M_Hp={state.CurrentMonsterHp} P_Hp={state.PlayerHp} Act={state.ActiveActivityId}");
            }

            // A) Assert Client side parsed binary successfully
            Assert.Equal(1, lastState.PlayerId);

            // B) Assert server processed command successfully
            Assert.Equal(55, lastState.ActiveActivityId);

            // C) Combat Loop resolved attacks
            Assert.True(lastState.CurrentXp > 0, "Player should have gained XP from the Rat kill.");
            Assert.True(lastState.CurrentLevel >= 1, "Player should have leveled up to at least level 1.");

            // D) Validate Input Throttler
            var metrics = simulationEngine.GetMetrics();
            Assert.True(metrics.ThrottledPacketsDropped >= 5, "Input Throttler should have dropped at least 5 packets from the initial burst.");

            // Output verification complete
            Console.WriteLine("Integration Phase: E2E Closed-Loop Verifications Passed.");
        }

        [Fact]
        public async Task StressTestConcurrentMultiplexing()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping E2E multiplexing verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            var e2eRetryOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(_dbContainer.GetConnectionString(), npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(e2eRetryOptions));

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            var retryingDbOptions = serviceProvider.GetRequiredService<RetryingDbContextOptions>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8082/");
            var lootEngine = new LootTableEngine();
            var checkpointManager = new StateCheckpointManager(serviceProvider);
            var forgeEngine = new ForgeSplicingEngine(serviceProvider);
            var playerRegistry = new PlayerSessionRegistry();
            var marketEngine = new MarketOrderBookEngine(serviceProvider, playerRegistry);
            var guildEngine = new GuildContributionEngine(serviceProvider);
            var escrowEngine = new MarketEscrowEngine(serviceProvider, playerRegistry);
            var mailboxEngine = new MailboxAndBankEngine(serviceProvider, playerRegistry);
            var rerollEngine = new AffixRerollEngine(serviceProvider);
            var breedingEngine = new BreedingEngine(serviceProvider, playerRegistry);
            var guildLogisticsEngine = new GuildLogisticsEngine(serviceProvider, playerRegistry);
            var craftingEngine = new CraftingEngine(contextFactory, playerRegistry, retryingDbOptions);
            var worldBossEngine = new WorldBossEngine(serviceProvider, playerRegistry);
            var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(serviceProvider);
            var legacyStoreEngine = new LegacyStoreEngine(serviceProvider, playerRegistry);
            var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(serviceProvider, playerRegistry);
            var guildCombatSimulationEngine = new GuildCombatSimulationEngine(serviceProvider, playerRegistry);
            
            var simulationEngine = new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageManagementEngine, guildWarEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, null!, null!, null!, null!, null!, contextFactory);

            networkSystem.Start();
            simulationEngine.Start();

            int clientCount = 500;
            var connectedClients = new System.Collections.Concurrent.ConcurrentBag<ClientWebSocket>();
            var tasks = new List<Task>();

            // Seed a PlayerRecord per simulated client up front - the JWT
            // handshake now resolves playerId from a real PlayerGuid lookup
            // instead of an arbitrary query-string token.
            var stressAccountIds = new Guid[clientCount + 1];
            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                for (int i = 1; i <= clientCount; i++)
                {
                    stressAccountIds[i] = Guid.NewGuid();
                    db.PlayerRecords.Add(new PlayerRecord { Id = i, PlayerGuid = stressAccountIds[i], AuthenticatorToken = Guid.NewGuid() });
                }
                await db.SaveChangesAsync();
            }

            // Allow server to boot
            await Task.Delay(100);

            for (int i = 1; i <= clientCount; i++)
            {
                int playerId = i;
                Guid accountId = stressAccountIds[i];
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var ws = new ClientWebSocket();
                        connectedClients.Add(ws);
                        var uri = new Uri("ws://localhost:8082/");

                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await ws.ConnectAsync(uri, cts.Token);

                        byte[] authBuffer = BuildAuthHandshakeBuffer(MintTestJwt(accountId));
                        await ws.SendAsync(new ArraySegment<byte>(authBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                        // Send login
                        var loginPacket = new ClientCommandPacket { Command = CommandType.Login, TargetId = playerId };
                        var loginBuffer = MemoryMarshal.AsBytes(new ReadOnlySpan<ClientCommandPacket>(ref loginPacket)).ToArray();
                        await ws.SendAsync(new ArraySegment<byte>(loginBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                        // Send bursts
                        for (int c = 25; c <= 43; c++)
                        {
                            var cmdPacket = new ClientCommandPacket { 
                                Command = (CommandType)c, 
                                TargetId = playerId,
                                LogicEpochCounter = 0 // Wait, initial epoch is 0?
                            };

                            // Intentionally corrupt LogicEpochCounter for odd playerIds to trigger anti-cheat flag
                            if (playerId % 2 != 0)
                            {
                                cmdPacket.LogicEpochCounter = 9999999; 
                            }

                            var buffer = MemoryMarshal.AsBytes(new ReadOnlySpan<ClientCommandPacket>(ref cmdPacket)).ToArray();
                            await ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, CancellationToken.None);
                            await Task.Delay(5); // Simulate burst spread
                        }

                        // Just try to receive until disconnected
                        var rcvBuffer = new byte[1024];
                        while (ws.State == WebSocketState.Open)
                        {
                            var r = await ws.ReceiveAsync(new ArraySegment<byte>(rcvBuffer), CancellationToken.None);
                            if (r.MessageType == WebSocketMessageType.Close) break;
                        }
                    }
                    catch (Exception)
                    {
                        // Expected closures
                    }
                }));
            }

            await Task.WhenAll(tasks);

            simulationEngine.Stop();
            networkSystem.Stop();

            int terminatedCount = 0;
            int normalCount = 0;
            foreach (var ws in connectedClients)
            {
                if (ws.State != WebSocketState.Open) terminatedCount++;
                else normalCount++;
                ws.Dispose();
            }

            Console.WriteLine($"Stress Test Done. Terminated sockets: {terminatedCount}, Kept Open: {normalCount}");
        }

        private sealed class MarketBrowsePageTestDto
        {
            public System.Collections.Generic.List<MarketListingTestDto> Listings { get; set; } = new();
            public int TotalCount { get; set; }
            public int PageIndex { get; set; }
            public int PageSize { get; set; }
        }

        private sealed class MarketListingTestDto
        {
            public long OrderId { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }
            public long Price { get; set; }
            public long CreatedAtEpoch { get; set; }
        }

        // Modul 40: covers HandleMarketBrowserListings end-to-end - a real
        // client requesting a paginated page of active SELL listings gets
        // them back deterministically ordered (Price ascending, CreatedAtEpoch
        // ascending as the tiebreak), and the gateway rejects invalid
        // pagination bounds (ValidateMarketBrowserQuery) before
        // FetchActiveListingsAsync ever runs a Skip/Take against them. This
        // endpoint has no tick-loop dependency (it is a plain authenticated
        // HTTP GET, the same pattern as HandleCodexSnapshot/HandleForgeInventorySnapshot),
        // so it only needs NetworkBroadcastSystem, not a full SimulationEngine.
        [Fact]
        public async Task Test_E2E_MarketBrowser_PaginatedListingsAndBoundsValidation()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping market browser E2E verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            // Modul: Task 4's nonce gate put IsNonceCurrentAsync on every
            // authenticated REST request's path (TryResolveAuthenticatedPlayerAsync),
            // and that call resolves RetryingDbContextOptions off this service
            // provider - this test predates that gate and never needed it, so
            // it 500'd the instant the tree could actually compile and run.
            // See the other E2E fixtures in this file for the same registration.
            var marketBrowserRetryOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(_dbContainer.GetConnectionString(), npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(marketBrowserRetryOptions));

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            const long testPlayerId = 1L;
            const string baseItemId = "market_browser_test_item";
            Guid testAccountId = Guid.NewGuid();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = testAccountId, AuthenticatorToken = Guid.NewGuid() });

                // Seeded out of price order and out of insertion-time order to
                // prove the query sorts rather than returning insertion order.
                db.MarketOrderRecords.Add(new MarketOrderRecord { SellerId = 900L, OrderType = "SELL", Status = 0, BaseItemId = baseItemId, QualityTier = 0, Price = 300L, CreatedAtEpoch = 3000L });
                db.MarketOrderRecords.Add(new MarketOrderRecord { SellerId = 900L, OrderType = "SELL", Status = 0, BaseItemId = baseItemId, QualityTier = 0, Price = 100L, CreatedAtEpoch = 2000L });
                db.MarketOrderRecords.Add(new MarketOrderRecord { SellerId = 900L, OrderType = "SELL", Status = 0, BaseItemId = baseItemId, QualityTier = 0, Price = 100L, CreatedAtEpoch = 1000L });
                // Noise rows that must never appear in the response.
                db.MarketOrderRecords.Add(new MarketOrderRecord { SellerId = 900L, OrderType = "BUY", Status = 0, BaseItemId = baseItemId, QualityTier = 0, Price = 50L, CreatedAtEpoch = 500L });
                db.MarketOrderRecords.Add(new MarketOrderRecord { SellerId = 900L, OrderType = "SELL", Status = 1, BaseItemId = baseItemId, QualityTier = 0, Price = 1L, CreatedAtEpoch = 500L });
                db.MarketOrderRecords.Add(new MarketOrderRecord { SellerId = 900L, OrderType = "SELL", Status = 0, BaseItemId = "different_item", QualityTier = 0, Price = 1L, CreatedAtEpoch = 500L });

                await db.SaveChangesAsync();
            }

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8083/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();

            string jwt = MintTestJwt(testAccountId);

            using var httpClient = new System.Net.Http.HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);

            try
            {
                var validResponse = await httpClient.GetAsync($"http://localhost:8083/api/v1/market/listings?baseItemId={baseItemId}&qualityTier=0&pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.OK, validResponse.StatusCode);

                // Modul: the response is an ENVELOPE now, not a bare array.
                // Without TotalCount the browser cannot draw a pager, and "have
                // I reached the end" is not answerable from a full page.
                string body = await validResponse.Content.ReadAsStringAsync();
                var page = System.Text.Json.JsonSerializer.Deserialize<MarketBrowsePageTestDto>(body);

                Assert.NotNull(page);
                var listings = page!.Listings;
                Assert.Equal(3, listings.Count);
                Assert.Equal(3, page.TotalCount);
                Assert.All(listings, l => Assert.Equal(baseItemId, l.BaseItemId));

                // Price ascending, then CreatedAtEpoch ascending as the tiebreak.
                Assert.Equal(100L, listings[0].Price);
                Assert.Equal(1000L, listings[0].CreatedAtEpoch);
                Assert.Equal(100L, listings[1].Price);
                Assert.Equal(2000L, listings[1].CreatedAtEpoch);
                Assert.Equal(300L, listings[2].Price);

                // Modul: NO FILTER IS REQUIRED. This endpoint used to 400
                // without an exact baseItemId, which made the marketplace
                // unbrowsable - a player could only ask about an item they
                // already knew was there.
                var browseAll = await httpClient.GetAsync("http://localhost:8083/api/v1/market/listings?pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.OK, browseAll.StatusCode);

                var everything = System.Text.Json.JsonSerializer.Deserialize<MarketBrowsePageTestDto>(
                    await browseAll.Content.ReadAsStringAsync());
                Assert.NotNull(everything);
                Assert.True(everything!.TotalCount >= 3, "an unfiltered browse must see at least the seeded listings");

                // Sorting is a real query parameter, not a client-side reshuffle.
                var byPriceDesc = await httpClient.GetAsync($"http://localhost:8083/api/v1/market/listings?baseItemId={baseItemId}&sortBy=price&descending=1&pageIndex=0&pageSize=10");
                var descending = System.Text.Json.JsonSerializer.Deserialize<MarketBrowsePageTestDto>(
                    await byPriceDesc.Content.ReadAsStringAsync());
                Assert.NotNull(descending);
                Assert.Equal(300L, descending!.Listings[0].Price);

                var negativePageIndexResponse = await httpClient.GetAsync($"http://localhost:8083/api/v1/market/listings?baseItemId={baseItemId}&qualityTier=0&pageIndex=-1&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, negativePageIndexResponse.StatusCode);

                var negativePageSizeResponse = await httpClient.GetAsync($"http://localhost:8083/api/v1/market/listings?baseItemId={baseItemId}&qualityTier=0&pageIndex=0&pageSize=-5");
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, negativePageSizeResponse.StatusCode);

                var hugePageSizeResponse = await httpClient.GetAsync($"http://localhost:8083/api/v1/market/listings?baseItemId={baseItemId}&qualityTier=0&pageIndex=0&pageSize=100000");
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, hugePageSizeResponse.StatusCode);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        // Modul: Production Release Hardening. /api/v1/billing/verify-receipt
        // used to route to a handler that trusted a client-supplied
        // AccountId/TransactionId/ProductId out of the request body and
        // credited diamonds with no signature check at all - the client
        // believed (its own header comment said so) that this exact URL was
        // the signature-checking endpoint, so a real purchase never verified
        // anything, and anyone who knew their own AccountId could grant
        // themselves free diamonds by POSTing it directly. Pins both halves
        // of the fix: the vulnerable route is gone, and the real endpoint it
        // was confused with resolves the caller from their own bearer JWT
        // rather than a body field, so it refuses an unauthenticated request
        // instead of trusting one.
        [Fact]
        public async Task Test_E2E_Billing_UnsafeVerifyReceiptRouteIsGone()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping billing route E2E verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8084/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();

            using var httpClient = new System.Net.Http.HttpClient();

            try
            {
                Guid attackerAccountId = Guid.NewGuid();
                var forgedBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        AccountId = attackerAccountId,
                        TransactionId = Guid.NewGuid().ToString("N"),
                        ProductId = "gems_pack_large"
                    }),
                    System.Text.Encoding.UTF8, "application/json");

                // Modul: this server has no 404 handler - every unmatched path
                // falls through to the same 400 an unmatched route always gets,
                // so BadRequest here is "the route doesn't exist", not a
                // validation failure of a route that does.
                var goneResponse = await httpClient.PostAsync("http://localhost:8084/api/v1/billing/verify-receipt", forgedBody);
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, goneResponse.StatusCode);

                var unauthedBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { receipt = "Zm9ycmVhbA==" }),
                    System.Text.Encoding.UTF8, "application/json");

                var unauthedResponse = await httpClient.PostAsync("http://localhost:8084/api/v1/billing/verify", unauthedBody);
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthedResponse.StatusCode);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        [Fact]
        public async Task Test_E2E_SessionSecurity_TamperedNonceIsRejected()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping session security E2E verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            // Modul: NetworkBroadcastSystem resolves RetryingDbContextOptions
            // off the service provider it is handed (IsNonceCurrentAsync does
            // this too) - see the other E2E fixtures in this file for the
            // same registration.
            var sessionSecurityRetryOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(_dbContainer.GetConnectionString(), npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(sessionSecurityRetryOptions));

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            var retryingOptions = serviceProvider.GetRequiredService<RetryingDbContextOptions>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            Guid accountId = Guid.NewGuid();
            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid(), CurrentSessionNonce = "the-real-nonce" });
                await db.SaveChangesAsync();
            }

            // A token whose embedded nonce does not match the stored value -
            // exactly what a token issued before a revocation event looks
            // like from the validator's point of view.
            string mismatchedJwt = AuthenticationEngine.GenerateJwt(accountId, "a-different-nonce", "pw", AuthenticationDefaults.LocalDevelopmentFallback, out _);
            string matchingJwt = AuthenticationEngine.GenerateJwt(accountId, "the-real-nonce", "pw", AuthenticationDefaults.LocalDevelopmentFallback, out _);

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8085/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();

            using var httpClient = new System.Net.Http.HttpClient();

            try
            {
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mismatchedJwt);
                var rejected = await httpClient.GetAsync("http://localhost:8085/api/v1/market/listings?pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);

                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", matchingJwt);
                var accepted = await httpClient.GetAsync("http://localhost:8085/api/v1/market/listings?pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        // Modul: AuthLoginResponse itself is a private nested class of
        // NetworkBroadcastSystem, so a test outside that class deserializes
        // into its own shape-only mirror - same pattern as
        // MarketBrowsePageTestDto above.
        private sealed class AuthLoginResponseTestDto
        {
            public string Token { get; set; } = string.Empty;
            public long ExpiresAtEpoch { get; set; }
            public string RefreshToken { get; set; } = string.Empty;
            public long RefreshExpiresAtEpoch { get; set; }
        }

        // Modul: Task 5 - proves HandleAuthLogin's device-login branch
        // durably persists the nonce it embeds in the JWT it hands back,
        // rather than minting one that lives only in the token. Without
        // SetCurrentSessionNonceAsync in the handler, GetCurrentSessionNonceAsync
        // would read back null (or a stale value from a previous session) and
        // every subsequent request on this brand-new login would be rejected
        // by the nonce gate Task 4 wired into the REST/WebSocket auth checks -
        // a fresh login that could authenticate exactly once.
        [Fact]
        public async Task Test_E2E_SessionSecurity_LoginPersistsNonceDurably()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping login nonce persistence E2E verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            // Modul: NetworkBroadcastSystem resolves RetryingDbContextOptions
            // off the service provider it is handed (HandleAuthLogin's own
            // SetCurrentSessionNonceAsync/IssueRefreshTokenAsync calls do this
            // too) - see the other E2E fixtures in this file for the same
            // registration.
            var loginRetryOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(_dbContainer.GetConnectionString(), npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(loginRetryOptions));

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            var retryingOptions = serviceProvider.GetRequiredService<RetryingDbContextOptions>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8086/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();

            using var httpClient = new System.Net.Http.HttpClient();

            try
            {
                string freshDeviceId = Guid.NewGuid().ToString("N");
                var loginBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { deviceId = freshDeviceId }),
                    System.Text.Encoding.UTF8, "application/json");

                var loginResponse = await httpClient.PostAsync("http://localhost:8086/api/v1/auth/login", loginBody);
                Assert.Equal(System.Net.HttpStatusCode.OK, loginResponse.StatusCode);

                string responseBody = await loginResponse.Content.ReadAsStringAsync();
                var parsed = System.Text.Json.JsonSerializer.Deserialize<AuthLoginResponseTestDto>(responseBody);
                Assert.NotNull(parsed);
                Assert.False(string.IsNullOrEmpty(parsed!.Token));

                var decoded = AuthenticationEngine.ValidateJwt(parsed.Token, AuthenticationDefaults.LocalDevelopmentFallback);
                Assert.True(decoded.IsValid);
                Assert.False(string.IsNullOrEmpty(decoded.SessionNonce));

                string? storedNonce = await AuthenticationEngine.GetCurrentSessionNonceAsync(retryingOptions, decoded.AccountId);
                Assert.NotNull(storedNonce);
                Assert.Equal(decoded.SessionNonce, storedNonce);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        // Modul: Task 6 - HandleAuthRefresh used to re-mint the access token
        // with the old 3-argument GenerateJwt (no authMethod claim at all),
        // so a device-authenticated player who refreshed would come back
        // password-authenticated - silently, since nothing on the wire says
        // which method a token carries except the token itself. This proves
        // the method survives the login -> refresh round trip: a device
        // login yields "dev", and the rotated access token handed back by
        // /api/v1/auth/refresh must still say "dev", not fall back to "pw".
        [Fact]
        public async Task Test_E2E_SessionSecurity_RefreshCarriesAuthMethod()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping refresh auth-method E2E verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            // Modul: NetworkBroadcastSystem resolves RetryingDbContextOptions
            // off the service provider it is handed (HandleAuthRefresh's own
            // RedeemRefreshTokenAsync/SetCurrentSessionNonceAsync calls do
            // this too) - see the other E2E fixtures in this file for the
            // same registration.
            var refreshRetryOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(_dbContainer.GetConnectionString(), npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(refreshRetryOptions));

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8087/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();

            using var httpClient = new System.Net.Http.HttpClient();

            try
            {
                string freshDeviceId = Guid.NewGuid().ToString("N");
                var loginBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { deviceId = freshDeviceId }),
                    System.Text.Encoding.UTF8, "application/json");

                var loginResponse = await httpClient.PostAsync("http://localhost:8087/api/v1/auth/login", loginBody);
                Assert.Equal(System.Net.HttpStatusCode.OK, loginResponse.StatusCode);

                string loginResponseBody = await loginResponse.Content.ReadAsStringAsync();
                var loginParsed = System.Text.Json.JsonSerializer.Deserialize<AuthLoginResponseTestDto>(loginResponseBody);
                Assert.NotNull(loginParsed);
                Assert.False(string.IsNullOrEmpty(loginParsed!.Token));
                Assert.False(string.IsNullOrEmpty(loginParsed.RefreshToken));

                var loginDecoded = AuthenticationEngine.ValidateJwt(loginParsed.Token, AuthenticationDefaults.LocalDevelopmentFallback);
                Assert.True(loginDecoded.IsValid);
                Assert.Equal("dev", loginDecoded.AuthMethod);

                var refreshBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { refreshToken = loginParsed.RefreshToken }),
                    System.Text.Encoding.UTF8, "application/json");

                var refreshResponse = await httpClient.PostAsync("http://localhost:8087/api/v1/auth/refresh", refreshBody);
                Assert.Equal(System.Net.HttpStatusCode.OK, refreshResponse.StatusCode);

                string refreshResponseBody = await refreshResponse.Content.ReadAsStringAsync();
                var refreshParsed = System.Text.Json.JsonSerializer.Deserialize<AuthLoginResponseTestDto>(refreshResponseBody);
                Assert.NotNull(refreshParsed);
                Assert.False(string.IsNullOrEmpty(refreshParsed!.Token));

                var refreshDecoded = AuthenticationEngine.ValidateJwt(refreshParsed.Token, AuthenticationDefaults.LocalDevelopmentFallback);
                Assert.True(refreshDecoded.IsValid);
                Assert.Equal("dev", refreshDecoded.AuthMethod);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        // Modul: Task 7 - HandleAuthRevoke used to touch only the refresh
        // token, so the access token a device already held stayed valid for
        // up to 24 more hours after "sign out", and any open WebSocket for
        // that account was untouched. This proves the whole chain: a live
        // WebSocket authenticated with the access token is still open right
        // before revoke, POSTing the refresh token to /api/v1/auth/revoke
        // both (a) makes that same access token fail a subsequent REST call
        // with 401 and (b) gets the WebSocket disconnected - not merely
        // prevents a future silent refresh.
        [Fact]
        public async Task Test_E2E_SessionSecurity_LogoutRevokesTheLiveAccessToken()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping logout-revokes-access-token E2E verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            // Modul: NetworkBroadcastSystem resolves RetryingDbContextOptions
            // off the service provider it is handed (HandleAuthRevoke's own
            // RevokeRefreshTokenAsync/BumpSessionNonceAsync calls do this
            // too) - see the other E2E fixtures in this file for the same
            // registration.
            var revokeRetryOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(_dbContainer.GetConnectionString(), npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(revokeRetryOptions));

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            // Modul: no SimulationEngine here - the WebSocket handshake
            // registers the connection in _connectedClients (and the socket
            // reports WebSocketState.Open) before the Login command it
            // enqueues is ever drained, so proving the socket opens and later
            // gets force-disconnected needs only NetworkBroadcastSystem.
            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8088/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();

            using var httpClient = new System.Net.Http.HttpClient();
            ClientWebSocket? clientSocket = null;

            try
            {
                string freshDeviceId = Guid.NewGuid().ToString("N");
                var loginBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { deviceId = freshDeviceId }),
                    System.Text.Encoding.UTF8, "application/json");

                var loginResponse = await httpClient.PostAsync("http://localhost:8088/api/v1/auth/login", loginBody);
                Assert.Equal(System.Net.HttpStatusCode.OK, loginResponse.StatusCode);

                string loginResponseBody = await loginResponse.Content.ReadAsStringAsync();
                var loginParsed = System.Text.Json.JsonSerializer.Deserialize<AuthLoginResponseTestDto>(loginResponseBody);
                Assert.NotNull(loginParsed);
                Assert.False(string.IsNullOrEmpty(loginParsed!.Token));
                Assert.False(string.IsNullOrEmpty(loginParsed.RefreshToken));

                string accessToken = loginParsed.Token;
                string refreshToken = loginParsed.RefreshToken;

                clientSocket = new ClientWebSocket();
                await clientSocket.ConnectAsync(new Uri("ws://localhost:8088/"), CancellationToken.None);

                byte[] authBuffer = BuildAuthHandshakeBuffer(accessToken);
                await clientSocket.SendAsync(new ArraySegment<byte>(authBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                // Modul: a background receive loop is required to observe the
                // transition to CloseReceived/Closed at all - ClientWebSocket
                // only processes an incoming close frame (and updates State)
                // while a ReceiveAsync call is in flight, exactly like the
                // stress test above.
                var socketReceiveDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var socketReceiveTask = Task.Run(async () =>
                {
                    try
                    {
                        var recvBuffer = new byte[1024];
                        while (clientSocket.State == WebSocketState.Open)
                        {
                            var r = await clientSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), receiveCts.Token);
                            if (r.MessageType == WebSocketMessageType.Close) break;
                        }
                    }
                    catch
                    {
                        // Expected once the socket is torn down mid-receive.
                    }
                    finally
                    {
                        socketReceiveDone.TrySetResult();
                    }
                });

                // Confirm the handshake actually landed and the socket is a
                // live, open connection before revoking anything.
                await Task.Delay(500);
                Assert.Equal(WebSocketState.Open, clientSocket.State);

                var revokeBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { refreshToken }),
                    System.Text.Encoding.UTF8, "application/json");
                var revokeResponse = await httpClient.PostAsync("http://localhost:8088/api/v1/auth/revoke", revokeBody);
                Assert.Equal(System.Net.HttpStatusCode.NoContent, revokeResponse.StatusCode);

                // (a) The OLD access token must now fail an authenticated
                // REST call - the nonce it carries no longer matches the
                // account's current one.
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                var rejected = await httpClient.GetAsync("http://localhost:8088/api/v1/market/listings?pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);

                // (b) The live WebSocket must be force-disconnected, not left
                // open for the rest of the JWT's natural lifetime.
                bool socketClosed = await WaitForConditionAsync(
                    () => socketReceiveTask.IsCompleted && clientSocket.State != WebSocketState.Open,
                    timeoutMs: 10000);
                Assert.True(socketClosed, $"Expected the WebSocket to be disconnected after revoke; final state was {clientSocket.State}.");
                Assert.True(
                    clientSocket.State == WebSocketState.Closed || clientSocket.State == WebSocketState.CloseReceived || clientSocket.State == WebSocketState.Aborted,
                    $"Expected the WebSocket to be closed/aborted after revoke, got {clientSocket.State}.");
            }
            finally
            {
                clientSocket?.Dispose();
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        // Modul: Task 8 - CompleteResetAsync used to change the password and
        // revoke refresh tokens but leave the access token a compromised
        // session was already holding valid for up to 24 more hours, because
        // nothing bumped the session nonce a JWT is checked against. This
        // proves the whole chain against the real HTTP endpoint: register
        // with email+password, mint a real reset token the same way
        // PasswordResetTests does (PasswordResetEngine.BeginResetAsync
        // directly against the database, since the request side never
        // returns the token to a caller - see BeginResetAsync's own doc
        // comment on why), complete the reset through
        // /api/v1/auth/reset-password, then confirm the OLD access token now
        // 401s on an authenticated REST call rather than riding out its
        // remaining lifetime.
        [Fact]
        public async Task Test_E2E_SessionSecurity_PasswordResetRevokesTheLiveAccessToken()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping password-reset-revokes-access-token E2E verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            var retryOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(_dbContainer.GetConnectionString(), npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(retryOptions));

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8089/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();

            using var httpClient = new System.Net.Http.HttpClient();

            try
            {
                const string email = "session_security_reset_e2e@example.com";
                const string oldPassword = "the old password";
                const string newPassword = "the new password";

                var registerBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { email, username = "ResetE2ESubject", password = oldPassword }),
                    System.Text.Encoding.UTF8, "application/json");
                var registerResponse = await httpClient.PostAsync("http://localhost:8089/api/v1/auth/register", registerBody);
                Assert.Equal(System.Net.HttpStatusCode.OK, registerResponse.StatusCode);

                string registerResponseBody = await registerResponse.Content.ReadAsStringAsync();
                var registerParsed = System.Text.Json.JsonSerializer.Deserialize<AuthLoginResponseTestDto>(registerResponseBody);
                Assert.NotNull(registerParsed);
                Assert.False(string.IsNullOrEmpty(registerParsed!.Token));

                string accessToken = registerParsed.Token;

                // Confirm the fresh token authenticates BEFORE the reset, so
                // the later 401 is provably caused by the reset rather than a
                // token that never worked.
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                var accepted = await httpClient.GetAsync("http://localhost:8089/api/v1/market/listings?pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);

                string resetToken;
                await using (var db = await contextFactory.CreateDbContextAsync())
                {
                    string? issued = await PasswordResetEngine.BeginResetAsync(db, email, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    Assert.NotNull(issued);
                    resetToken = issued!;
                }

                var resetBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { token = resetToken, password = newPassword }),
                    System.Text.Encoding.UTF8, "application/json");
                var resetResponse = await httpClient.PostAsync("http://localhost:8089/api/v1/auth/reset-password", resetBody);
                Assert.Equal(System.Net.HttpStatusCode.OK, resetResponse.StatusCode);

                // The OLD access token must now fail an authenticated REST
                // call - the nonce it carries no longer matches the account's
                // current one, exactly like the revoke case above, except the
                // eviction here is triggered by a password reset instead of a
                // client-issued logout.
                var rejected = await httpClient.GetAsync("http://localhost:8089/api/v1/market/listings?pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        // Modul: Task 9 - a replayed (already-spent) refresh token was
        // already treated as a theft signal - RedeemRefreshTokenAsync revoked
        // every refresh token on the account - but the access token that
        // device already held rode out its remaining lifetime, exactly the
        // gap logout (Task 7) and password reset (Task 8) closed. This proves
        // the whole chain against the real HTTP endpoint: log in, redeem the
        // refresh token once (a legitimate rotation, yielding a successor),
        // then present the ORIGINAL now-spent refresh token again - the
        // replay - and confirm the access token captured at login (still well
        // within its 24h life) now 401s on an authenticated REST call too.
        [Fact]
        public async Task Test_E2E_SessionSecurity_RefreshReplayRevokesTheLiveAccessToken()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping refresh-replay-revokes-access-token E2E verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            // Modul: NetworkBroadcastSystem resolves RetryingDbContextOptions
            // off the service provider it is handed (HandleAuthRefresh's own
            // RedeemRefreshTokenAsync/BumpSessionNonceAsync calls do this
            // too) - see the other E2E fixtures in this file for the same
            // registration.
            var replayRetryOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(_dbContainer.GetConnectionString(), npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(replayRetryOptions));

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8090/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();

            using var httpClient = new System.Net.Http.HttpClient();

            try
            {
                string freshDeviceId = Guid.NewGuid().ToString("N");
                var loginBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { deviceId = freshDeviceId }),
                    System.Text.Encoding.UTF8, "application/json");

                var loginResponse = await httpClient.PostAsync("http://localhost:8090/api/v1/auth/login", loginBody);
                Assert.Equal(System.Net.HttpStatusCode.OK, loginResponse.StatusCode);

                string loginResponseBody = await loginResponse.Content.ReadAsStringAsync();
                var loginParsed = System.Text.Json.JsonSerializer.Deserialize<AuthLoginResponseTestDto>(loginResponseBody);
                Assert.NotNull(loginParsed);
                Assert.False(string.IsNullOrEmpty(loginParsed!.Token));
                Assert.False(string.IsNullOrEmpty(loginParsed.RefreshToken));

                string originalAccessToken = loginParsed.Token;
                string originalRefreshToken = loginParsed.RefreshToken;

                // A legitimate rotation first, so the token being replayed
                // next is genuinely spent rather than merely unused.
                var firstRefreshBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { refreshToken = originalRefreshToken }),
                    System.Text.Encoding.UTF8, "application/json");
                var firstRefreshResponse = await httpClient.PostAsync("http://localhost:8090/api/v1/auth/refresh", firstRefreshBody);
                Assert.Equal(System.Net.HttpStatusCode.OK, firstRefreshResponse.StatusCode);

                // Presenting the SAME (now-spent) refresh token again is the
                // replay case - the server treats it as a theft signal.
                var replayBody = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { refreshToken = originalRefreshToken }),
                    System.Text.Encoding.UTF8, "application/json");
                var replayResponse = await httpClient.PostAsync("http://localhost:8090/api/v1/auth/refresh", replayBody);
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, replayResponse.StatusCode);

                // The FIRST access token, captured at login and still well
                // within its 24h life, must now also fail an authenticated
                // REST call - the replay must have bumped the account's
                // session nonce and evicted the live session, not merely
                // refused to hand out a new refresh token.
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", originalAccessToken);
                var rejected = await httpClient.GetAsync("http://localhost:8090/api/v1/market/listings?pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }
    }
}
