using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

// Modul: web client port, Phase 1. Dumps the wire contract as JSON on stdout
// so client_web can GENERATE its TypeScript types instead of mirroring 151
// StateUpdatePacket fields by hand - see PacketJsonCodec.ExportSchemaJson and
// client_web/scripts/generate-protocol.mjs.
//
// FIRST statement in the program, above even the startup banner, because the
// generator consumes raw stdout - one stray Console.WriteLine ahead of it and
// the JSON no longer parses. It must also run with no database, no Redis and
// no content registry: type generation happens in CI and on a fresh checkout,
// where none of those exist.
if (args.Length > 0 && args[0] == "--dump-protocol")
{
    NetworkPacketLayoutGuard.Validate();
    Console.Out.Write(PacketJsonCodec.ExportSchemaJson());
    return;
}

Console.WriteLine("Initializing FolkIdle Server Engine...");

NetworkPacketLayoutGuard.Validate();
if (args.Length > 0 && args[0] == "--layout-check")
{
    Console.WriteLine("Network packet layout guard passed.");
    return;
}

// Modul: one-shot migration entrypoint for ops/jobs/apply-migrations.yaml -
// applies every pending EF Core migration and exits. Runs as its own
// short-lived container BEFORE the server Deployment rollout proceeds, so
// multiple server replicas never race to migrate the same database at
// startup (the normal server boot path below deliberately never
// auto-migrates for exactly that reason). An uncaught exception here exits
// non-zero, failing the K8s Job and halting the rollout.
if (args.Length > 0 && args[0] == "--migrate")
{
    var migrationConnectionString = Environment.GetEnvironmentVariable("FOLKIDLE_DB_CONN");
    if (string.IsNullOrEmpty(migrationConnectionString))
    {
        throw new InvalidOperationException("FOLKIDLE_DB_CONN must be set to run --migrate.");
    }

    var migrationOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
        .UseNpgsql(migrationConnectionString)
        .Options;
    await using (var migrationContext = new FolkIdleDbContext(migrationOptions))
    {
        await migrationContext.Database.MigrateAsync();
    }
    Console.WriteLine("Database migrations applied successfully.");
    return;
}

// Modul: dev fixture. Provisions a known, fully-kitted account for driving the
// client by hand or through the MCP Play Mode harness - three characters, all
// seven equip slots filled, Town Hall 5, materials and gold.
//
// Double-guarded on purpose, because unlike --migrate and --lift-quarantine
// this one WRITES A KNOWN PASSWORD: it needs the explicit flag AND
// FOLKIDLE_ALLOW_DEV_SEED in the environment. A production host that somehow
// received the flag still refuses. See DevFixtureSeeder.
if (args.Length > 0 && args[0] == "--seed-dev")
{
    if (Environment.GetEnvironmentVariable("FOLKIDLE_ALLOW_DEV_SEED") != "1")
    {
        Console.WriteLine("--seed-dev refused: set FOLKIDLE_ALLOW_DEV_SEED=1 to confirm this is not a production database.");
        return;
    }

    // The fixture references real BaseItemIds and material ids, so the content
    // registry has to be live before it runs.
    ContentRegistry.Initialize();

    var seedConnectionString = Environment.GetEnvironmentVariable("FOLKIDLE_DB_CONN") ?? ConnectionStringDefaults.LocalDevelopmentFallback;
    var seedOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
        .UseNpgsql(seedConnectionString)
        .Options;

    await using (var seedContext = new FolkIdleDbContext(seedOptions))
    {
        await seedContext.Database.MigrateAsync();
        long seededPlayerId = await DevFixtureSeeder.SeedAsync(seedContext);
        Console.WriteLine($"Dev fixture ready. PlayerId {seededPlayerId}, login {DevFixtureSeeder.Email} / {DevFixtureSeeder.Password}");
    }
    return;
}

// Modul: anti-cheat false positive. An operator path to lift a quarantine.
//
// AntiCheatTelemetryEngine sets Quarantine_Active on a heuristic verdict, and
// that verdict was permanent: SimulationEngine.ProcessSingleTick returns early
// while the flag is set so the account stops progressing entirely, the socket
// is force-closed on every login, and NO code path anywhere in this codebase
// ever set the flag back to false. A wrongly flagged player - and the detector
// did wrongly flag them, see that engine's own comments - had no route back,
// no appeal, and no support tool. A ban system that cannot be reversed is a
// bug in the ban system regardless of how accurate the detector becomes.
// Modul: reconciles the diamonds the regional-boss bug handed out.
//
// `monsterId % 6 == 0` decided who was a boss, and a boss kill granted ten
// premium diamonds. No real boss matched it; monsters 96, 102, 108 and 114 did,
// as did every sixth legacy monster. Anyone who ground one of those was paid
// premium currency for it - at the measured kill rate, roughly twenty thousand
// an hour.
//
// The debt is computable EXACTLY rather than estimated: monster_codex_entries
// stores a per-player, per-monster kill count, which is the same event that
// paid out. Sum the kills on the affected ids, multiply by ten.
//
// DRY RUN BY DEFAULT. It prints what it would take and changes nothing unless
// --apply is passed, because this removes currency from real accounts and the
// operator should see the list first. Balances are clamped at zero: a player
// who already spent the diamonds ends at nothing rather than in debt, which is
// the kinder side of an error that was ours.
if (args.Length > 0 && args[0] == "--reconcile-boss-diamonds")
{
    bool apply = args.Contains("--apply");

    var reconcileConnectionString = Environment.GetEnvironmentVariable("FOLKIDLE_DB_CONN") ?? ConnectionStringDefaults.LocalDevelopmentFallback;
    var reconcileOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
        .UseNpgsql(reconcileConnectionString)
        .Options;

    await using var reconcileDb = new FolkIdleDbContext(reconcileOptions);

    // The exact set the old heuristic rewarded: divisible by six AND not a
    // real boss. Asking ContentRegistry keeps this honest if the canonical
    // boss set ever changes.
    ContentRegistry.Initialize();
    var wronglyPaid = Enumerable.Range(1, ContentRegistry.LastCanonicalMonsterId)
        .Where(id => id % 6 == 0 && !ContentRegistry.IsRegionalBoss(id))
        .ToArray();

    Console.WriteLine($"Monsters that wrongly paid a boss bounty: {string.Join(", ", wronglyPaid)}");

    var debts = await reconcileDb.Set<MonsterCodexEntry>()
        .AsNoTracking()
        .Where(e => wronglyPaid.Contains(e.MonsterId))
        .GroupBy(e => e.PlayerId)
        .Select(g => new { PlayerId = g.Key, Kills = g.Sum(e => e.KillCount) })
        .ToListAsync();

    if (debts.Count == 0)
    {
        Console.WriteLine("No account earned diamonds from the bug.");
        return;
    }

    const int DiamondsPerBossKill = 10;
    long total = 0;

    foreach (var debt in debts.OrderByDescending(d => d.Kills))
    {
        long owed = (long)debt.Kills * DiamondsPerBossKill;
        total += owed;

        var player = await reconcileDb.PlayerRecords.FirstOrDefaultAsync(p => p.Id == debt.PlayerId);
        int balance = player?.PremiumDiamonds ?? 0;
        long taken = Math.Min(owed, balance);

        Console.WriteLine(
            $"  player {debt.PlayerId}: {debt.Kills} kills = {owed} diamonds granted, " +
            $"balance {balance}, would take {taken}");

        if (apply && player != null)
        {
            player.PremiumDiamonds = (int)Math.Max(0, balance - owed);
        }
    }

    Console.WriteLine($"{debts.Count} account(s), {total} diamonds granted in total.");

    if (apply)
    {
        await reconcileDb.SaveChangesAsync();
        Console.WriteLine("Applied.");
    }
    else
    {
        Console.WriteLine("Dry run - nothing changed. Re-run with --apply to take them.");
    }

    return;
}

if (args.Length > 1 && args[0] == "--lift-quarantine")
{
    if (!long.TryParse(args[1], out long quarantinedPlayerId))
    {
        Console.WriteLine("Usage: --lift-quarantine <playerId>");
        return;
    }

    var liftConnectionString = Environment.GetEnvironmentVariable("FOLKIDLE_DB_CONN") ?? ConnectionStringDefaults.LocalDevelopmentFallback;
    var liftOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
        .UseNpgsql(liftConnectionString)
        .Options;

    await using (var liftContext = new FolkIdleDbContext(liftOptions))
    {
        var quarantinedPlayer = await liftContext.PlayerRecords.FirstOrDefaultAsync(p => p.Id == quarantinedPlayerId);
        if (quarantinedPlayer == null)
        {
            Console.WriteLine($"No player {quarantinedPlayerId}.");
            return;
        }

        quarantinedPlayer.IsQuarantined = false;
        quarantinedPlayer.Quarantine_Active = false;

        // The shadow ban also froze the player's open market listings, which
        // must be released with it or the account returns with its economy
        // still locked.
        await liftContext.Database.ExecuteSqlRawAsync(
            "UPDATE \"MarketOrderRecords\" SET \"IsQuarantined\" = FALSE WHERE \"SellerId\" = {0}",
            quarantinedPlayerId);

        await liftContext.SaveChangesAsync();
        Console.WriteLine($"Quarantine lifted for player {quarantinedPlayerId}.");
    }
    return;
}

// Modul: content-validation entrypoint for the CI pipeline - exercises the
// exact same ContentRegistry/ActiveSkillEngine parse-and-validate path the
// real server boot runs, then exits. A malformed GameData JSON exits
// non-zero here, failing the build before a broken image is ever pushed,
// instead of only surfacing as a crash-looping pod at rollout time.
// ops/validate_content.py is the fast Python pre-check mirroring the same
// structural rules; this flag is the authoritative parity check.
if (args.Length > 0 && args[0] == "--validate-content")
{
    ContentRegistry.Initialize();
    ActiveSkillEngine.Initialize();
    Console.WriteLine("Content validation passed.");
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

// Modul: THE MOCK OAUTH VALIDATOR WAS REGISTERED IN PRODUCTION.
//
// MockOAuthTokenValidator performs no cryptographic verification and its own
// comment says never to register it outside local development - and this line
// registered it unconditionally. /api/v1/auth/login is unauthenticated by
// design and accepts an oauthProviderToken, so the live server would have taken
// "mock:Google:{id}" from anybody as proof of identity.
//
// Not yet exploitable, and only by luck: nothing in the client can link an
// OAuth identity, so the lookup finds no row. That luck ends the day OAuth
// ships, and a Google `sub` is not a secret.
//
// Production therefore gets a validator that refuses everything - the same
// fail-closed shape as the admin endpoint's missing key. Swap in a real
// Google tokeninfo / Apple JWKS implementation when OAuth is actually wanted;
// until then it does not work, which is where it already was.
// Modul: the password reset needs somebody to hand the mail to.
//
// FAILS CLOSED IN PRODUCTION, like the OAuth validator below and the admin
// endpoint's missing key. Falling back to the console sender on a live server
// would mean printing password reset links into the server's own log while
// telling every player "check your email" - worse than the feature not
// existing, because it looks like it works.
//
// Development gets the console sender, which is what makes the whole flow
// drivable with no provider account at all: the link is printed, and whoever
// is testing pastes it.
string resendApiKey = Environment.GetEnvironmentVariable("FOLKIDLE_RESEND_API_KEY") ?? string.Empty;
string mailFromAddress = Environment.GetEnvironmentVariable("FOLKIDLE_MAIL_FROM") ?? string.Empty;
bool isProductionForMail = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Production";

if (resendApiKey.Length > 0 && mailFromAddress.Length > 0)
{
    serviceCollection.AddSingleton<IEmailSender>(provider =>
        new ResendEmailSender(
            provider.GetRequiredService<IHttpClientFactory>(), resendApiKey, mailFromAddress));
}
else if (isProductionForMail)
{
    serviceCollection.AddSingleton<IEmailSender, DisabledEmailSender>();
}
else
{
    serviceCollection.AddSingleton<IEmailSender, ConsoleEmailSender>();
}

bool isProductionForOAuth = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Production";
if (isProductionForOAuth)
{
    serviceCollection.AddSingleton<IOAuthTokenValidator, DisabledOAuthTokenValidator>();
}
else
{
    serviceCollection.AddSingleton<IOAuthTokenValidator, MockOAuthTokenValidator>();
}

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

// Modul: registers IHttpClientFactory - required by
// ProductionIapReceiptValidator's live store-verification calls
// (Google Play Developer API / Apple App Store Server API) so those
// calls pool and reuse handlers instead of a fresh HttpClient/socket
// per purchase, per IHttpClientFactory's own documented socket-exhaustion
// guidance. Registered unconditionally (cheap, no local-dev/Production
// split needed) so it is available to resolve below regardless of
// which IIapReceiptValidator implementation ends up constructed.
serviceCollection.AddHttpClient();

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
networkSystem.RegisterPlayerSessionRegistry(playerRegistry);
var forgeEngine = new ForgeSplicingEngine(serviceProvider, playerRegistry);
var antiCheatTelemetryEngine = new AntiCheatTelemetryEngine(serviceProvider, redisMultiplexer, playerRegistry, networkSystem);
var marketEngine = new MarketOrderBookEngine(serviceProvider, playerRegistry);
var guildEngine = new GuildContributionEngine(serviceProvider, playerRegistry);
var escrowEngine = new MarketEscrowEngine(serviceProvider, playerRegistry);
var mailboxEngine = new MailboxAndBankEngine(serviceProvider, playerRegistry);
var rerollEngine = new AffixRerollEngine(serviceProvider, playerRegistry);
var breedingEngine = new BreedingEngine(serviceProvider, playerRegistry);
var guildLogisticsEngine = new GuildLogisticsEngine(serviceProvider, playerRegistry);
var guildWarEngine = new GuildWarEngine(serviceProvider);
var guildWarSnapshotEngine = new GuildWarSnapshotEngine(serviceProvider);
var larderEngine = new LarderEngine(serviceProvider, playerRegistry);
// Modul: inheritance stats - the permanent, season-crossing bonuses diamonds buy.
var inheritanceEngine = new InheritanceEngine(serviceProvider, playerRegistry);
var skillTreeEngine = new SkillTreeEngine(serviceProvider, playerRegistry);
// Modul: the Hall of Ancestors - the roster that outlives a season, and the
// only thing in this server that can move a character between playable slots.
var hallOfAncestorsEngine = new HallOfAncestorsEngine(serviceProvider, playerRegistry);
var craftingEngine = new CraftingEngine(serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>(), playerRegistry, serviceProvider.GetRequiredService<RetryingDbContextOptions>(), guildWarEngine);
var worldBossEngine = new WorldBossEngine(serviceProvider, playerRegistry);
worldBossEngine.EnsureSnapshotAsync().GetAwaiter().GetResult();
var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
var guildMatchmakingEngine = new GuildMatchmakingEngine(serviceProvider);
var legacyStoreEngine = new LegacyStoreEngine(serviceProvider, playerRegistry);
var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(serviceProvider, playerRegistry);
var guildCombatSimulationEngine = new GuildCombatSimulationEngine(serviceProvider, playerRegistry);
var guildRaidEngine = new GuildRaidEngine(serviceProvider, playerRegistry);
var equipmentSlotEngine = new EquipmentSlotEngine(serviceProvider, playerRegistry);
var relationshipEngine = new RelationshipEngine(serviceProvider, playerRegistry);
var combatLootEngine = new CombatLootEngine(serviceProvider, playerRegistry);
var redisWriteBehindEngine = new RedisWriteBehindEngine(serviceProvider, redisMultiplexer);
var pushNotificationTriggerEngine = new PushNotificationTriggerEngine(serviceProvider, redisMultiplexer);
var liveOpsTickEngine = new LiveOpsTickEngine(serviceProvider, playerRegistry, worldBossEngine, pushNotificationTriggerEngine);
var compliancePurgeEngine = new CompliancePurgeEngine(serviceProvider, redisMultiplexer);
var leaderboardCronEngine = new LeaderboardCronEngine(serviceProvider, redisMultiplexer);
var guildManagementEngine = new GuildManagementEngine(serviceProvider.GetRequiredService<RetryingDbContextOptions>(), playerRegistry);
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
        new SecretRotationManager("FOLKIDLE_IAP_APPLE_PUBLIC_KEY_PATH"),
        serviceProvider.GetRequiredService<IHttpClientFactory>())
    : new MockIapReceiptValidator();
var billingVerificationEngine = new BillingVerificationEngine(serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>(), serviceProvider.GetRequiredService<RedisSessionCache>(), playerRegistry, serviceProvider.GetRequiredService<RetryingDbContextOptions>(), iapReceiptValidator, networkSystem);
networkSystem.RegisterBillingVerificationEngine(billingVerificationEngine);

networkSystem.RegisterAntiCheatTelemetryEngine(antiCheatTelemetryEngine);

var engine = new SimulationEngine(lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine, escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine, villageManagementEngine, guildWarEngine, legacyStoreEngine, guildLogisticsDepotEngine, guildCombatSimulationEngine, antiCheatTelemetryEngine, pushNotificationTriggerEngine, compliancePurgeEngine, billingVerificationEngine, redisMultiplexer, serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>(), guildRaidEngine, equipmentSlotEngine, relationshipEngine, larderEngine, inheritanceEngine, skillTreeEngine, hallOfAncestorsEngine);
networkSystem.RegisterSimulationEngine(engine);
var timeBankService = new TimeBankService(engine, checkpointManager);

mailboxEngine.StartCleanupCron();
liveOpsTickEngine.StartCron();
pushNotificationTriggerEngine.StartCron();
// Modul: Guild War scoreboard sync. PlayerSessionRegistry is constructed after
// GuildWarEngine, the same ordering problem RegisterSimulationEngine and
// RegisterPlayerSessionRegistry already solve - handed over explicitly here,
// before the cron starts, so the scoreboard loop has somewhere to publish to.
guildWarEngine.RegisterPlayerSessionRegistry(playerRegistry);
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
// The world-first claim is settled in Redis, off the simulation tick - see
// BossFirstClearAnnouncer for why the tick cannot ask the question itself.
FolkIdle.Server.Domain.Combat.BossFirstClearAnnouncer.Redis = redisMultiplexer;

// Announcements name the player rather than their database id - see
// PlayerNameResolver for why the tick-side callers read a cache instead.
FolkIdle.Server.Engine.PlayerNameResolver.ContextFactory =
    serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

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
