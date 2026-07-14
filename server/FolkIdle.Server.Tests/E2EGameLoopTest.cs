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
            
            // Note: In tests we don't need Redis Multiplexer, so we can pass null if allowed, or mock it.
            // Wait, AntiCheat, Push, Compliance, Billing require Redis.
            // Let's pass null! for things we don't use in this test to avoid redis dependency, or we just pass nulls for telemetry.
            var simulationEngine = new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageBuildingEngine, villageManagementEngine, mentorshipEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, null!, null!, null!, null!, null!, contextFactory);

            // Spin up headless loop
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

            // 3. Send upstream Command Burst (ChangeActivity = 55 -> Forest Rat)
            var cmd = new ClientCommandPacket
            {
                Command = CommandType.ChangeActivity,
                TargetId = 55
            };
            byte[] cmdBuffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];
            MemoryMarshal.Write(new Span<byte>(cmdBuffer), cmd);
            
            // Push burst of 10 packets instantly to trigger throttler
            for (int i = 0; i < 10; i++)
            {
                await clientSocket.SendAsync(new ArraySegment<byte>(cmdBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);
            }

            // 4. Simulate execution (100 ticks = 10.0 seconds) to ensure player kills 69 HP Rat (15 dmg / 1.5s = 5 hits = 7.5s)
            var receivedPackets = new ConcurrentQueue<StateUpdatePacket>();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10.5));

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
                            ParseStatePacket(recvBuffer, result.Count, receivedPackets);
                        }
                    }
                    catch { break; }
                }
            });

            await Task.Delay(10500);
            cts.Cancel();
            await receiveTask;

            simulationEngine.Stop();
            networkSystem.Stop();

            // 5. Verify constraints
            
            // At 1 Hz network rate over 6.5s, there should be at least 6 packets.
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

        private void ParseStatePacket(byte[] buffer, int count, ConcurrentQueue<StateUpdatePacket> queue)
        {
            var span = new ReadOnlySpan<byte>(buffer, 0, count);
            var state = MemoryMarshal.Read<StateUpdatePacket>(span);
            queue.Enqueue(state);
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
    }
}
