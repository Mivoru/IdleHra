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

// Content Pipeline: parses server/GameData/*.json into ContentRegistry's/
// ActiveSkillEngine's flat struct arrays before anything else starts - an
// uncaught InvalidOperationException here is the intended fast-fail/
// crash-on-boot behavior for malformed or missing content data.
ContentRegistry.Initialize();
ActiveSkillEngine.Initialize();

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

// Modul: dedicated retry-configured options for every engine that opens its
// own explicit Serializable transaction - see RetryingDbContextOptions for
// why this is not applied to the shared factory above. Covers both
// transient network failures (Npgsql's default detection) and Postgres
// Serializable-isolation conflicts, which are expected and recoverable
// under concurrent write load rather than genuine faults.
var retryConfiguredOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
    .UseNpgsql(connectionString, npgsqlOptions =>
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 6,
            maxRetryDelay: TimeSpan.FromSeconds(8),
            errorCodesToAdd: new[]
            {
                Npgsql.PostgresErrorCodes.SerializationFailure,
                Npgsql.PostgresErrorCodes.DeadlockDetected
            }))
    .Options;
serviceCollection.AddSingleton(new RetryingDbContextOptions(retryConfiguredOptions));

// Modul: MockOAuthTokenValidator performs no cryptographic verification -
// see its own doc comment. A real deployment must register a real
// IOAuthTokenValidator (Google tokeninfo / Apple JWKS) before accepting
// real OAuth links or logins.
serviceCollection.AddSingleton<IOAuthTokenValidator, MockOAuthTokenValidator>();

var jwtSecretKey = Environment.GetEnvironmentVariable("JWT_SECRET_KEY");
if (jwtSecretKey == null)
{
    bool isProductionForJwt = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Production";
    if (isProductionForJwt)
    {
        throw new InvalidOperationException("JWT_SECRET_KEY must be set when DOTNET_ENVIRONMENT is Production.");
    }
    jwtSecretKey = AuthenticationDefaults.LocalDevelopmentFallback;
}

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

// Modul: kept alive for the process lifetime via this top-level variable -
// EventListener subscriptions are not rooted by the EventSource they
// listen to, so an unreferenced instance is eligible for GC (silently
// ending the subscription) the moment nothing else holds it.
var broadcastLatencyProfiler = new BroadcastLatencyProfiler();

// Modul: "+" is HttpListener's wildcard-bind prefix - listens on every
// network interface, not just loopback. A prefix of "http://localhost:8080/"
// only accepts connections arriving on the loopback interface; inside a
// container, Kubernetes' liveness/readiness probes and all other pod
// traffic arrive on the pod's real network interface (its assigned pod IP),
// never through loopback, so a loopback-only bind makes the listener
// completely unreachable from outside the container while the process
// itself reports as running - every probe fails with connection refused
// and the pod is killed and restarted in an infinite loop. The wildcard
// bind still accepts loopback connections too, so this is not a regression
// for local, non-containerized development.
var networkSystem = new NetworkBroadcastSystem(serviceProvider, jwtSecretKey, "http://+:8080/");
var lootEngine = new LootTableEngine();
var checkpointManager = new StateCheckpointManager(serviceProvider);
var playerRegistry = new PlayerSessionRegistry();
var forgeEngine = new ForgeSplicingEngine(serviceProvider, playerRegistry);
var antiCheatTelemetryEngine = new AntiCheatTelemetryEngine(serviceProvider, redisMultiplexer, playerRegistry, networkSystem);
var marketEngine = new MarketOrderBookEngine(serviceProvider, playerRegistry);
var guildEngine = new GuildContributionEngine(serviceProvider);
var escrowEngine = new MarketEscrowEngine(serviceProvider, playerRegistry);
var mailboxEngine = new MailboxAndBankEngine(serviceProvider, playerRegistry);
var rerollEngine = new AffixRerollEngine(serviceProvider);
var breedingEngine = new BreedingEngine(serviceProvider, playerRegistry);
var guildLogisticsEngine = new GuildLogisticsEngine(serviceProvider, playerRegistry);
var guildWarEngine = new GuildWarEngine(serviceProvider);
var guildWarSnapshotEngine = new GuildWarSnapshotEngine(serviceProvider);
var craftingEngine = new CraftingEngine(serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>(), playerRegistry, serviceProvider.GetRequiredService<RetryingDbContextOptions>(), guildWarEngine);
var worldBossEngine = new WorldBossEngine(serviceProvider, playerRegistry);
worldBossEngine.EnsureSnapshotAsync().GetAwaiter().GetResult();
var villageBuildingEngine = new VillageBuildingEngine(serviceProvider, playerRegistry);
var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
var mentorshipEngine = new MentorshipEngine(serviceProvider, playerRegistry);
var guildMatchmakingEngine = new GuildMatchmakingEngine(serviceProvider);
var chronoCoreEngine = new ChronoCoreEngine(serviceProvider, playerRegistry);
var legacyStoreEngine = new LegacyStoreEngine(serviceProvider, playerRegistry);
var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(serviceProvider, playerRegistry);
var guildCombatSimulationEngine = new GuildCombatSimulationEngine(serviceProvider, playerRegistry);
var guildRaidEngine = new GuildRaidEngine(serviceProvider, playerRegistry);
var equipmentSlotEngine = new EquipmentSlotEngine(serviceProvider, playerRegistry);
var combatLootEngine = new CombatLootEngine(serviceProvider, playerRegistry);
var redisWriteBehindEngine = new RedisWriteBehindEngine(serviceProvider, redisMultiplexer);
var liveOpsTickEngine = new LiveOpsTickEngine(serviceProvider, playerRegistry, worldBossEngine);
var pushNotificationTriggerEngine = new PushNotificationTriggerEngine(serviceProvider, redisMultiplexer);
var compliancePurgeEngine = new CompliancePurgeEngine(serviceProvider, redisMultiplexer);
var leaderboardCronEngine = new LeaderboardCronEngine(serviceProvider, redisMultiplexer);
// Modul: MockIapReceiptValidator performs no cryptographic verification -
// see its own doc comment. Production instead uses
// ProductionIapReceiptValidator, which verifies each receipt's signature
// against a store public key resolved through SecretRotationManager (a
// file path injected via FOLKIDLE_IAP_GOOGLE_PUBLIC_KEY_PATH /
// FOLKIDLE_IAP_APPLE_PUBLIC_KEY_PATH, never the key itself in an
// environment variable - see SecretRotationManager's own doc comment).
// Matches every other local-dev-fallback-vs-Production split already in
// this file (connectionString/jwtSecretKey above).
bool isProductionForIap = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Production";
IIapReceiptValidator iapReceiptValidator = isProductionForIap
    ? new ProductionIapReceiptValidator(
        new SecretRotationManager("FOLKIDLE_IAP_GOOGLE_PUBLIC_KEY_PATH"),
        new SecretRotationManager("FOLKIDLE_IAP_APPLE_PUBLIC_KEY_PATH"))
    : new MockIapReceiptValidator();
var billingVerificationEngine = new BillingVerificationEngine(serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>(), serviceProvider.GetRequiredService<RedisSessionCache>(), playerRegistry, serviceProvider.GetRequiredService<RetryingDbContextOptions>(), iapReceiptValidator, networkSystem);
networkSystem.RegisterBillingVerificationEngine(billingVerificationEngine);

networkSystem.RegisterAntiCheatTelemetryEngine(antiCheatTelemetryEngine);

var engine = new SimulationEngine(lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine, escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine, villageBuildingEngine, villageManagementEngine, mentorshipEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine, guildLogisticsDepotEngine, guildCombatSimulationEngine, antiCheatTelemetryEngine, pushNotificationTriggerEngine, compliancePurgeEngine, billingVerificationEngine, redisMultiplexer, serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>(), guildRaidEngine, equipmentSlotEngine);
networkSystem.RegisterSimulationEngine(engine);
var timeBankService = new TimeBankService(engine, checkpointManager);

mailboxEngine.StartCleanupCron();
liveOpsTickEngine.StartCron();
pushNotificationTriggerEngine.StartCron();
guildWarEngine.StartCron();
guildWarSnapshotEngine.StartCron();
guildMatchmakingEngine.StartCron();
guildRaidEngine.StartCron();

var codexSvc = new CodexEngine(serviceProvider, playerRegistry);
var achSvc = new AchievementEngine(serviceProvider, playerRegistry);
var ecoTelemetrySvc = new EcoTelemetryEngine(serviceProvider);
var seasonEraSvc = new SeasonalRotationEngine(serviceProvider);
codexSvc.StartCron();
combatLootEngine.StartCron();
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
