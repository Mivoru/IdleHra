using System;
using System.Collections.Concurrent;
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

namespace FolkIdle.Server.Tests
{
    public class E2EGameLoopTest : IAsyncLifetime
    {
        private PostgreSqlContainer? _dbContainer;
        private bool _dockerAvailable;

        public async Task InitializeAsync()
        {
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
            
            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, "http://localhost:8081/");
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
            var craftingEngine = new CraftingEngine(contextFactory, playerRegistry);
            var worldBossEngine = new WorldBossEngine(serviceProvider, playerRegistry);
            var villageBuildingEngine = new VillageBuildingEngine(serviceProvider, playerRegistry);
            var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
            var mentorshipEngine = new MentorshipEngine(serviceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(serviceProvider);
            var chronoCoreEngine = new ChronoCoreEngine(serviceProvider, playerRegistry);
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
                villageBuildingEngine, villageManagementEngine, mentorshipEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, antiCheatTelemetryEngine, null!, null!, null!, null!, contextFactory);

            // Spin up headless loop. This test drives the network gateway
            // directly without running ColdRecoveryCoordinator (there is no
            // pre-existing session state to reconstruct here), so the 503
            // gate in NetworkBroadcastSystem.ListenLoopAsync must be opened
            // the same way Program.cs's benchmark-mode path does.
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();
            simulationEngine.Start();

            // Seed Token Cache
            var token = Guid.NewGuid();
            networkSystem.ActiveTokenCache.TryAdd(token, new CachedTokenEntry { PlayerId = 1L, ExpirationEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600L });

            // 2. Mock a Client Connection
            using var clientSocket = new ClientWebSocket();
            await clientSocket.ConnectAsync(new Uri("ws://localhost:8081/"), CancellationToken.None);

            // Send Handshake Auth Packet
            var authPacket = new ClientAuthPacket { PlayerGuid = Guid.NewGuid(), AuthenticatorToken = token, EpochExpirationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            byte[] authBuffer = new byte[Marshal.SizeOf<ClientAuthPacket>()];
            MemoryMarshal.Write(new Span<byte>(authBuffer), authPacket);
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

            await Task.WhenAny(loginConfirmed.Task, Task.Delay(TimeSpan.FromSeconds(3)));
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

            // 5. Let combat resolve (69 HP Rat, 15 dmg / 1.5s = 5 hits = 7.5s).
            await Task.Delay(TimeSpan.FromSeconds(9));
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
            var floodTasks = new Task[5];
            for (int f = 0; f < floodTasks.Length; f++)
            {
                int floodPlayerId = 900000 + f;
                floodTasks[f] = Task.Run(async () =>
                {
                    using var floodSocket = new ClientWebSocket();
                    var floodToken = Guid.NewGuid();
                    networkSystem.ActiveTokenCache.TryAdd(floodToken, new CachedTokenEntry { PlayerId = floodPlayerId, ExpirationEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600L });

                    using var floodCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await floodSocket.ConnectAsync(new Uri("ws://localhost:8081/"), floodCts.Token);

                    var floodAuth = new ClientAuthPacket { PlayerGuid = Guid.NewGuid(), AuthenticatorToken = floodToken, EpochExpirationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    byte[] floodAuthBuffer = new byte[Marshal.SizeOf<ClientAuthPacket>()];
                    MemoryMarshal.Write(new Span<byte>(floodAuthBuffer), floodAuth);
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
            // live counter once per broadcast cycle (every 10 ticks / ~1s, see
            // EngineLoop's _ticksSinceLastBroadcast gate) - wait a full cycle
            // so that refresh actually lands before reading it below.
            await Task.Delay(1500);

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
            
            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, "http://localhost:8082/");
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
            var craftingEngine = new CraftingEngine(contextFactory, playerRegistry);
            var worldBossEngine = new WorldBossEngine(serviceProvider, playerRegistry);
            var villageBuildingEngine = new VillageBuildingEngine(serviceProvider, playerRegistry);
            var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
            var mentorshipEngine = new MentorshipEngine(serviceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(serviceProvider);
            var chronoCoreEngine = new ChronoCoreEngine(serviceProvider, playerRegistry);
            var legacyStoreEngine = new LegacyStoreEngine(serviceProvider, playerRegistry);
            var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(serviceProvider, playerRegistry);
            var guildCombatSimulationEngine = new GuildCombatSimulationEngine(serviceProvider, playerRegistry);
            
            var simulationEngine = new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageBuildingEngine, villageManagementEngine, mentorshipEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, null!, null!, null!, null!, null!, contextFactory);

            networkSystem.Start();
            simulationEngine.Start();

            int clientCount = 500;
            var connectedClients = new System.Collections.Concurrent.ConcurrentBag<ClientWebSocket>();
            var tasks = new List<Task>();
            
            // Allow server to boot
            await Task.Delay(100);

            for (int i = 1; i <= clientCount; i++)
            {
                int playerId = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var ws = new ClientWebSocket();
                        connectedClients.Add(ws);
                        var uri = new Uri($"ws://localhost:8082/?token={Guid.NewGuid()}");
                        
                        // Fake token for test
                        networkSystem.ActiveTokenCache.TryAdd(Guid.Parse(uri.Query.Split('=')[1]), new CachedTokenEntry { PlayerId = playerId, ExpirationEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600L });

                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await ws.ConnectAsync(uri, cts.Token);

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

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            const long testPlayerId = 1L;
            const string baseItemId = "market_browser_test_item";

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });

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

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, "http://localhost:8083/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();

            var token = Guid.NewGuid();
            networkSystem.ActiveTokenCache.TryAdd(token, new CachedTokenEntry { PlayerId = testPlayerId, ExpirationEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600L });

            using var httpClient = new System.Net.Http.HttpClient();
            httpClient.DefaultRequestHeaders.Add("X-Authenticator-Token", token.ToString());

            try
            {
                var validResponse = await httpClient.GetAsync($"http://localhost:8083/api/v1/market/listings?baseItemId={baseItemId}&qualityTier=0&pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.OK, validResponse.StatusCode);

                string body = await validResponse.Content.ReadAsStringAsync();
                var listings = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<MarketListingTestDto>>(body);

                Assert.NotNull(listings);
                Assert.Equal(3, listings!.Count);
                Assert.All(listings, l => Assert.Equal(baseItemId, l.BaseItemId));

                // Price ascending, then CreatedAtEpoch ascending as the tiebreak.
                Assert.Equal(100L, listings[0].Price);
                Assert.Equal(1000L, listings[0].CreatedAtEpoch);
                Assert.Equal(100L, listings[1].Price);
                Assert.Equal(2000L, listings[1].CreatedAtEpoch);
                Assert.Equal(300L, listings[2].Price);

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
    }
}
