using System;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

Console.WriteLine("Initializing FolkIdle Server Engine...");

NetworkPacketLayoutGuard.Validate();
if (args.Length > 0 && args[0] == "--layout-check")
{
    Console.WriteLine("Network packet layout guard passed.");
    return;
}

var serviceCollection = new ServiceCollection();
var connectionString = Environment.GetEnvironmentVariable("FOLKIDLE_DB_CONN");
if (connectionString == null)
{
    bool isProduction = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Production";
    if (isProduction)
    {
        throw new InvalidOperationException("FOLKIDLE_DB_CONN must be set when DOTNET_ENVIRONMENT is Production.");
    }
    connectionString = ConnectionStringDefaults.LocalDevelopmentFallback;
}
serviceCollection.AddDbContextFactory<FolkIdleDbContext>(options =>
    options.UseNpgsql(connectionString));
serviceCollection.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

var redisConfiguration = ConfigurationOptions.Parse(Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379");
redisConfiguration.AbortOnConnectFail = false;
redisConfiguration.ConnectRetry = 1;
redisConfiguration.SyncTimeout = 1000;
serviceCollection.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConfiguration));
serviceCollection.AddSingleton<RedisSessionCache>();
serviceCollection.AddSingleton<RedisPlayerSessionLock>();

// Hosted Services removed

var serviceProvider = serviceCollection.BuildServiceProvider();

if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Production")
{
    using var seedScope = serviceProvider.CreateScope();
    using var seedDb = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext();
    await DbSeeder.SeedAllAsync(seedDb);
}

var redisMultiplexer = serviceProvider.GetRequiredService<IConnectionMultiplexer>();
TelemetryStreamer.ConfigureRedis(redisMultiplexer);

var networkSystem = new NetworkBroadcastSystem(serviceProvider, "http://localhost:8080/");
var lootEngine = new LootTableEngine();
var checkpointManager = new StateCheckpointManager(serviceProvider);
var forgeEngine = new ForgeSplicingEngine(serviceProvider);
var playerRegistry = new PlayerSessionRegistry();
var antiCheatTelemetryEngine = new AntiCheatTelemetryEngine(serviceProvider, redisMultiplexer, playerRegistry);
var marketEngine = new MarketOrderBookEngine(serviceProvider, playerRegistry);
var guildEngine = new GuildContributionEngine(serviceProvider);
var escrowEngine = new MarketEscrowEngine(serviceProvider, playerRegistry);
var mailboxEngine = new MailboxAndBankEngine(serviceProvider, playerRegistry);
var rerollEngine = new AffixRerollEngine(serviceProvider);
var breedingEngine = new BreedingEngine(serviceProvider, playerRegistry);
var guildLogisticsEngine = new GuildLogisticsEngine(serviceProvider, playerRegistry);
var craftingEngine = new CraftingEngine(serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>(), playerRegistry);
var worldBossEngine = new WorldBossEngine(serviceProvider, playerRegistry);
worldBossEngine.EnsureSnapshotAsync().GetAwaiter().GetResult();
var villageBuildingEngine = new VillageBuildingEngine(serviceProvider, playerRegistry);
var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
var mentorshipEngine = new MentorshipEngine(serviceProvider, playerRegistry);
var guildWarEngine = new GuildWarEngine(serviceProvider);
var guildMatchmakingEngine = new GuildMatchmakingEngine(serviceProvider);
var chronoCoreEngine = new ChronoCoreEngine(serviceProvider, playerRegistry);
var legacyStoreEngine = new LegacyStoreEngine(serviceProvider, playerRegistry);
var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(serviceProvider, playerRegistry);
var guildCombatSimulationEngine = new GuildCombatSimulationEngine(serviceProvider, playerRegistry);
var guildRaidEngine = new GuildRaidEngine(serviceProvider, playerRegistry);
var redisWriteBehindEngine = new RedisWriteBehindEngine(serviceProvider, redisMultiplexer);
var liveOpsTickEngine = new LiveOpsTickEngine(serviceProvider, playerRegistry, worldBossEngine);
var pushNotificationTriggerEngine = new PushNotificationTriggerEngine(serviceProvider, redisMultiplexer);
var compliancePurgeEngine = new CompliancePurgeEngine(serviceProvider, redisMultiplexer);
var leaderboardCronEngine = new LeaderboardCronEngine(serviceProvider, redisMultiplexer);
var billingVerificationEngine = new BillingVerificationEngine(serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>(), serviceProvider.GetRequiredService<RedisSessionCache>(), playerRegistry);

networkSystem.RegisterAntiCheatTelemetryEngine(antiCheatTelemetryEngine);

var engine = new SimulationEngine(lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine, escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine, villageBuildingEngine, villageManagementEngine, mentorshipEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine, guildLogisticsDepotEngine, guildCombatSimulationEngine, antiCheatTelemetryEngine, pushNotificationTriggerEngine, compliancePurgeEngine, billingVerificationEngine, redisMultiplexer, serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>());
var timeBankService = new TimeBankService(engine, checkpointManager);

mailboxEngine.StartCleanupCron();
liveOpsTickEngine.StartCron();
pushNotificationTriggerEngine.StartCron();
guildWarEngine.StartCron();
guildMatchmakingEngine.StartCron();
guildRaidEngine.StartCron();

var codexSvc = new CodexEngine(serviceProvider, playerRegistry);
var achSvc = new AchievementEngine(serviceProvider, playerRegistry);
var ecoTelemetrySvc = new EcoTelemetryEngine(serviceProvider);
var seasonEraSvc = new SeasonalRotationEngine(serviceProvider);
codexSvc.StartCron();
achSvc.StartCron();
ecoTelemetrySvc.StartCron();
seasonEraSvc.StartCron();
redisWriteBehindEngine.StartCron();
leaderboardCronEngine.StartCron();

AppDomain.CurrentDomain.ProcessExit += (s, e) => 
{
    Console.WriteLine("Shutting down engine securely...");
    GlobalEngineState.IsShuttingDown = true;
    engine.ShutdownGracefully();
    redisWriteBehindEngine.StopAndFlushAsync().GetAwaiter().GetResult();
    networkSystem.Stop();
};

System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, (ctx) =>
{
    Console.WriteLine("SIGTERM trapped...");
    ctx.Cancel = true; // Prevent default termination
    GlobalEngineState.IsShuttingDown = true;
    engine.ShutdownGracefully();
    redisWriteBehindEngine.StopAndFlushAsync().GetAwaiter().GetResult();
    networkSystem.Stop();
    Environment.Exit(0);
});

Console.CancelKeyPress += (s, e) =>
{
    Console.WriteLine("Termination requested...");
    e.Cancel = true;
    GlobalEngineState.IsShuttingDown = true;
    engine.ShutdownGracefully();
    redisWriteBehindEngine.StopAndFlushAsync().GetAwaiter().GetResult();
    networkSystem.Stop();
    Environment.Exit(0);
};

var cts = new CancellationTokenSource();
TelemetryStreamer.StartConsumerAsync(cts.Token);

bool isBenchmarking = Environment.GetEnvironmentVariable("RUN_BENCHMARK") == "true";

if (isBenchmarking)
{
    Console.WriteLine("Initializing Benchmark Mode...");
    GlobalEngineState.IsColdBootRecoveryComplete = true; // Skip recovery in benchmark.
    FolkIdle.Server.Benchmark.EngineStressTester.SetupVirtualSessions(engine);
    engine.Start();
}
else
{
    // Cold boot recovery: reconstruct sessions before opening gateway.
    var coldRecovery = new ColdRecoveryCoordinator(serviceProvider, playerRegistry, checkpointManager);
    await coldRecovery.StartAsync(CancellationToken.None);

    networkSystem.Start();
    engine.Start();
}

Console.WriteLine("Engine started. Press Ctrl+C to exit.");

while (engine.IsRunning)
{
    Thread.Sleep(1000);
}

// checkpointManager.FlushAllGracefully(); // Handled by ShutdownGracefully
Console.WriteLine("FolkIdle Server shutdown complete.");
