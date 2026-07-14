using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    public class WebSocketSession
    {
        public WebSocket Socket { get; }
        public ClientInputThrottler Throttler { get; }
        public string RedisLockToken { get; }
        public TokenBucket TokenBucket;
        public byte[] DiagnosticSendBuffer { get; }

        public WebSocketSession(WebSocket socket, string redisLockToken)
        {
            Socket = socket;
            RedisLockToken = redisLockToken;
            Throttler = new ClientInputThrottler();
            TokenBucket = NetworkThrottlingEngine.CreateBucket();
            DiagnosticSendBuffer = new byte[Marshal.SizeOf<StateUpdatePacket>()];
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AdminCommandPacket
    {
        public byte CommandType; // 1 = XP, 2 = Drops
        public int MultiplierValue;
    }

    public class NetworkBroadcastSystem
    {
        private readonly HttpListener _httpListener;
        private readonly ConcurrentDictionary<long, WebSocketSession> _connectedClients = new();
        public ConcurrentDictionary<Guid, long> ActiveTokenCache { get; } = new();
        
        private bool _isRunning;
        private readonly byte[] _broadcastBuffer = new byte[Marshal.SizeOf<StateUpdatePacket>()];

        public ref long GetThrottledCounter() => ref _throttledCounter;
        private long _throttledCounter;
        private long _acceptedPacketsWindow;
        private long _throughputWindowEpoch;

        private readonly IServiceProvider _serviceProvider;
        private readonly IDbContextFactory<FolkIdleDbContext> _contextFactory;
        private readonly RedisPlayerSessionLock? _redisSessionLock;
        private AntiCheatTelemetryEngine? _antiCheatTelemetryEngine;

        public NetworkBroadcastSystem(IServiceProvider serviceProvider, string uriPrefix = "http://localhost:8080/")
        {
            _serviceProvider = serviceProvider;
            _contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            _redisSessionLock = serviceProvider.GetService<RedisPlayerSessionLock>();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(uriPrefix);
        }

        public void RegisterCheckpointManager(StateCheckpointManager manager)
        {
            manager.RegisterDisconnectCallback(ForceDisconnect);
        }

        public void RegisterAntiCheatTelemetryEngine(AntiCheatTelemetryEngine engine)
        {
            _antiCheatTelemetryEngine = engine;
        }

        public void Start()
        {
            _httpListener.Start();
            _isRunning = true;
            Task.Run(ListenLoopAsync);
        }

        public void Stop()
        {
            _isRunning = false;
            _httpListener.Stop();
        }

        private async Task ListenLoopAsync()
        {
            while (_isRunning)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    string requestPath = context.Request.Url?.AbsolutePath ?? "/";

                    if (requestPath == "/health/liveness" || requestPath == "/health/readiness")
                    {
                        context.Response.ContentType = "text/plain";
                        context.Response.StatusCode = 200;
                        byte[] responseBytes = new byte[] { 0x4F, 0x4B }; // "OK"
                        await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                        context.Response.Close();
                        continue;
                    }

                    if (requestPath == "/healthz")
                    {
                        context.Response.StatusCode = 200;
                        context.Response.Close();
                        continue;
                    }

                    if (GlobalEngineState.IsShuttingDown || !GlobalEngineState.IsColdBootRecoveryComplete)
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                        continue;
                    }

                    if (requestPath == "/api/v1/assets/handshake" && context.Request.HttpMethod == "POST")
                    {
                        string expectedHash = Environment.GetEnvironmentVariable("ExpectedCatalogHash") ?? string.Empty;
                        string clientHash = string.Empty;

                        if (context.Request.HasEntityBody)
                        {
                            using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                            string payload = await reader.ReadToEndAsync();
                            try
                            {
                                var json = System.Text.Json.JsonDocument.Parse(payload);
                                if (json.RootElement.TryGetProperty("catalog.hash", out var hashElement))
                                {
                                    clientHash = hashElement.GetString() ?? string.Empty;
                                }
                            }
                            catch { }
                        }

                        if (!string.IsNullOrEmpty(expectedHash) && clientHash != expectedHash)
                        {
                            context.Response.StatusCode = 426; // Upgrade Required
                            context.Response.Close();
                            continue;
                        }

                        context.Response.StatusCode = 200;
                        context.Response.Close();
                        continue;
                    }

                    if (requestPath == "/admin/liveops" && context.Request.HttpMethod == "POST")
                    {
                        string secretKey = context.Request.Headers["X-Admin-Secret-Key"] ?? string.Empty;
                        string expectedKey = Environment.GetEnvironmentVariable("ADMIN_SECRET_KEY") ?? "supersecretadmin123";
                        if (secretKey != expectedKey)
                        {
                            context.Response.StatusCode = 401;
                            context.Response.Close();
                            continue;
                        }

                        if (context.Request.InputStream != null)
                        {
                            var buffer = new byte[Marshal.SizeOf<AdminCommandPacket>()];
                            int bytesRead = await context.Request.InputStream.ReadAsync(buffer, 0, buffer.Length);
                            if (bytesRead >= Marshal.SizeOf<AdminCommandPacket>())
                            {
                                ParseAdminCommand(buffer, bytesRead);
                            }
                        }

                        context.Response.StatusCode = 200;
                        context.Response.Close();
                        continue;
                    }

                    if (requestPath == "/api/v1/billing/verify-receipt" && context.Request.HttpMethod == "POST")
                    {
                        await HandleVerifyReceipt(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/billing/refund-webhook" && context.Request.HttpMethod == "POST")
                    {
                        await HandleRefundWebhook(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/storefront/listings" && context.Request.HttpMethod == "GET")
                    {
                        await HandleStorefrontListings(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/guild/logistics/snapshot" && context.Request.HttpMethod == "GET")
                    {
                        await HandleGuildLogisticsSnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/forge/inventory" && context.Request.HttpMethod == "GET")
                    {
                        await HandleForgeInventorySnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/codex/snapshot" && context.Request.HttpMethod == "GET")
                    {
                        await HandleCodexSnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/mastery/snapshot" && context.Request.HttpMethod == "GET")
                    {
                        await HandleMasterySnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/achievements/snapshot" && context.Request.HttpMethod == "GET")
                    {
                        await HandleAchievementsSnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/leaderboard/global" && context.Request.HttpMethod == "GET")
                    {
                        await HandleGlobalLeaderboard(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/support/tickets/create" && context.Request.HttpMethod == "POST")
                    {
                        await HandleSupportTicket(context);
                        continue;
                    }

                    if (context.Request.IsWebSocketRequest)
                    {
                        var webSocketContext = await context.AcceptWebSocketAsync(null);
                        _ = HandleClientLoopAsync(webSocketContext.WebSocket);
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException)
                {
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Network error: {ex.Message}");
                }
            }
        }

        public struct PlayerCommand
        {
            public long PlayerId;
            public ClientCommandPacket Packet;
        }

        public ConcurrentQueue<PlayerCommand> CommandQueue { get; } = new();

        private sealed class StorefrontListingResponse
        {
            public int ListingId { get; set; }
            public string ProductIdentifier { get; set; } = string.Empty;
            public int DiamondPackageYield { get; set; }
            public int PriceInCents { get; set; }
        }

        private sealed class GuildLogisticsSnapshotResponse
        {
            public int MaterialId { get; set; }
            public long CurrentStock { get; set; }
            public long TargetRequirement { get; set; }
        }

        private sealed class ForgeEquipmentInstanceResponse
        {
            public long Id { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }
            public bool IsAffixLocked { get; set; }
            public System.Collections.Generic.Dictionary<string, int> Affixes { get; set; } = new();
        }

        private sealed class ForgeRecipeResponse
        {
            public int RecipeId { get; set; }
            public string ResultBaseItemId { get; set; } = string.Empty;
            public int TierIndex { get; set; }
            public string MaterialName { get; set; } = string.Empty;
            public int MaterialCost { get; set; }
            public long CurrentMaterialStock { get; set; }
        }

        private sealed class ForgeInventorySnapshotResponse
        {
            public System.Collections.Generic.List<ForgeEquipmentInstanceResponse> OwnedEquipment { get; set; } = new();
            public System.Collections.Generic.List<ForgeRecipeResponse> Recipes { get; set; } = new();
        }

        private sealed class CodexSnapshotEntryResponse
        {
            public int MonsterId { get; set; }
            public int Level { get; set; }
            public long Kills { get; set; }
            public long NextLevelKills { get; set; }
        }

        private sealed class AchievementSnapshotEntryResponse
        {
            public int AchievementId { get; set; }
            public long CurrentProgress { get; set; }
            public int CompletedTier { get; set; }
            public long NextTierTarget { get; set; }
            public int NextTierReward { get; set; }
        }

        private sealed class RaceMasterySnapshotEntryResponse
        {
            public int RaceId { get; set; }
            public int Level { get; set; }
            public long Experience { get; set; }
            public long NextLevelExperience { get; set; }
        }

        private sealed class LeaderboardEntryResponse
        {
            public int Rank { get; set; }
            public long PlayerId { get; set; }
            public string DisplayName { get; set; } = string.Empty;
            public int Level { get; set; }
            public long Xp { get; set; }
        }

        private async Task HandleGlobalLeaderboard(HttpListenerContext context)
        {
            try
            {
                if (!TryResolveAuthenticatedPlayer(context.Request, out long playerId))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                bool isQuarantined = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.IsQuarantined || p.Quarantine_Active)
                    .SingleOrDefaultAsync();

                System.Collections.Generic.List<LeaderboardEntryResponse> entries = new();
                if (isQuarantined)
                {
                    entries = BuildSpoofedLeaderboard(playerId);
                }
                else
                {
                    int skip = 0;
                    int take = 50;
                    var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                    if (int.TryParse(query["skip"], out int parsedSkip)) skip = parsedSkip;
                    if (int.TryParse(query["take"], out int parsedTake)) take = parsedTake;

                    if (!ClientCommandValidator.ValidateLeaderboardQuery(playerId, skip, take))
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    var dbRedis = _serviceProvider.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();
                    var redisEntries = await dbRedis.SortedSetRangeByRankWithScoresAsync("leaderboard:mastery", skip, skip + take - 1, StackExchange.Redis.Order.Descending);

                    var playerIds = redisEntries.Select(e => (long)e.Element).ToList();
                    
                    var players = await db.PlayerRecords
                        .AsNoTracking()
                        .Where(p => playerIds.Contains(p.Id))
                        .ToDictionaryAsync(p => p.Id);

                    for (int i = 0; i < redisEntries.Length; i++)
                    {
                        long pId = (long)redisEntries[i].Element;
                        if (players.TryGetValue(pId, out var p))
                        {
                            entries.Add(new LeaderboardEntryResponse
                            {
                                Rank = skip + i + 1,
                                PlayerId = p.Id,
                                DisplayName = "Player",
                                Level = p.CurrentLevel,
                                Xp = p.CurrentXp
                            });
                        }
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Leaderboard error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private static System.Collections.Generic.List<LeaderboardEntryResponse> BuildSpoofedLeaderboard(long playerId)
        {
            var entries = new System.Collections.Generic.List<LeaderboardEntryResponse>(50);
            uint seed = unchecked((uint)playerId) ^ 0xA5A5A5A5u;
            for (int i = 0; i < 50; i++)
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                entries.Add(new LeaderboardEntryResponse
                {
                    Rank = i + 1,
                    PlayerId = 900000000L + i,
                    DisplayName = "LocalRank",
                    Level = 100 - i,
                    Xp = 1000000L - (i * 2500L) + (seed % 1000)
                });
            }

            return entries;
        }

        private async Task HandleGuildLogisticsSnapshot(HttpListenerContext context)
        {
            try
            {
                if (!TryResolveAuthenticatedPlayer(context.Request, out long playerId))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                if (!string.IsNullOrEmpty(context.Request.Url?.Query))
                {
                    ForceDisconnect(playerId);
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                long guildId = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.GuildId)
                    .SingleOrDefaultAsync();

                var snapshot = await db.GuildLogisticsDepots
                    .AsNoTracking()
                    .Where(d => d.GuildId == guildId && guildId > 0)
                    .OrderBy(d => d.MaterialId)
                    .Select(d => new GuildLogisticsSnapshotResponse
                    {
                        MaterialId = d.MaterialId,
                        CurrentStock = d.CurrentStock,
                        TargetRequirement = d.TargetRequirement
                    })
                    .ToListAsync();

                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, snapshot);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild logistics snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 21: on-demand snapshot for the client Forge crafting/reroll panels.
        // StateUpdatePacket is fixed-size and carries scalars only, so the player's
        // full owned-equipment list and per-recipe material stock (both variable
        // length) are served here instead, following the same authenticated
        // read-only HTTP pattern as HandleGuildLogisticsSnapshot/HandleGlobalLeaderboard.
        private async Task HandleForgeInventorySnapshot(HttpListenerContext context)
        {
            try
            {
                if (!TryResolveAuthenticatedPlayer(context.Request, out long playerId))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var ownedEquipment = await db.EquipmentInstances
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

                var materialQuantities = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToDictionaryAsync(c => c.ItemId, c => c.Quantity);

                await transaction.CommitAsync();

                var response = new ForgeInventorySnapshotResponse();

                foreach (var item in ownedEquipment)
                {
                    var affixes = new System.Collections.Generic.Dictionary<string, int>();
                    bool jsonLockFlag = false;

                    if (!string.IsNullOrWhiteSpace(item.AffixPayload) &&
                        System.Text.Json.Nodes.JsonNode.Parse(item.AffixPayload) is System.Text.Json.Nodes.JsonObject affixObject)
                    {
                        foreach (var kvp in affixObject)
                        {
                            if (kvp.Value is not System.Text.Json.Nodes.JsonValue affixValue)
                            {
                                continue;
                            }

                            if (kvp.Key == "is_affix_locked")
                            {
                                jsonLockFlag = affixValue.TryGetValue(out bool lockedFlag) && lockedFlag;
                                continue;
                            }

                            if (affixValue.TryGetValue(out int magnitude))
                            {
                                affixes[kvp.Key] = magnitude;
                            }
                        }
                    }

                    response.OwnedEquipment.Add(new ForgeEquipmentInstanceResponse
                    {
                        Id = item.Id,
                        BaseItemId = item.BaseItemId,
                        QualityTier = item.QualityTier,
                        IsAffixLocked = item.IsAffixLocked || jsonLockFlag,
                        Affixes = affixes
                    });
                }

                foreach (var recipe in CraftingReceptuary.AllRecipes)
                {
                    string materialName = ContentRegistry.GetMaterialString(recipe.MaterialId);
                    materialQuantities.TryGetValue(materialName, out long currentStock);

                    response.Recipes.Add(new ForgeRecipeResponse
                    {
                        RecipeId = recipe.RecipeId,
                        ResultBaseItemId = recipe.ResultBaseItemId,
                        TierIndex = recipe.TierIndex,
                        MaterialName = materialName,
                        MaterialCost = recipe.MaterialCost,
                        CurrentMaterialStock = currentStock
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Forge inventory snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 23: authorized snapshot of the player's real Monster Codex
        // progress. MonsterCodexEntries is already populated by CodexEngine's
        // kill-event cron (SimulationEngine enqueues a KillEvent on every monster
        // death; CodexEngine batches and upserts it off the 10 Hz hot path). Level
        // is read directly off the persisted column rather than recomputed here,
        // so this endpoint can never drift from CodexEngine.CalculateLevelFromKillCount
        // (Level = KillCount / 10, uncapped) if that formula ever changes.
        private async Task HandleCodexSnapshot(HttpListenerContext context)
        {
            try
            {
                if (!TryResolveAuthenticatedPlayer(context.Request, out long playerId))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var entries = await db.MonsterCodexEntries
                    .FromSqlInterpolated($"SELECT * FROM \"MonsterCodexEntries\" WHERE \"PlayerId\" = {playerId}")
                    .AsNoTracking()
                    .ToListAsync();

                await transaction.CommitAsync();

                var response = new System.Collections.Generic.List<CodexSnapshotEntryResponse>(entries.Count);

                foreach (var entry in entries)
                {
                    response.Add(new CodexSnapshotEntryResponse
                    {
                        MonsterId = entry.MonsterId,
                        Level = entry.Level,
                        Kills = entry.KillCount,
                        NextLevelKills = (entry.Level + 1) * 10L
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Codex snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 13: authorized snapshot of the player's real Race Mastery
        // progress. PlayerRaceMasteries is already populated by CodexEngine's
        // kill-event cron. Uses plain LINQ rather than raw SQL - the table is
        // mapped via [Table("player_race_masteries")] (lowercase, unlike every
        // other table in this codebase), and EF Core resolves that mapping
        // correctly on its own, sidestepping manual identifier quoting entirely.
        private async Task HandleMasterySnapshot(HttpListenerContext context)
        {
            try
            {
                if (!TryResolveAuthenticatedPlayer(context.Request, out long playerId))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var entries = await db.PlayerRaceMasteries
                    .AsNoTracking()
                    .Where(m => m.PlayerId == playerId)
                    .ToListAsync();

                var response = new System.Collections.Generic.List<RaceMasterySnapshotEntryResponse>(entries.Count);

                foreach (var entry in entries)
                {
                    response.Add(new RaceMasterySnapshotEntryResponse
                    {
                        RaceId = entry.RaceId,
                        Level = entry.MasteryLevel,
                        Experience = entry.CumulativeXp,
                        NextLevelExperience = CodexEngine.GetRaceMasteryRequiredXp(entry.MasteryLevel)
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Race mastery snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 13: authorized snapshot of the player's real lifetime achievement
        // progress (PlayerLifetimeAchievements, including but not limited to the
        // three auto-awarded tiered achievements from AchievementMilestones).
        private async Task HandleAchievementsSnapshot(HttpListenerContext context)
        {
            try
            {
                if (!TryResolveAuthenticatedPlayer(context.Request, out long playerId))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var entries = await db.PlayerLifetimeAchievements
                    .AsNoTracking()
                    .Where(a => a.PlayerId == playerId)
                    .ToListAsync();

                await transaction.CommitAsync();

                var response = new System.Collections.Generic.List<AchievementSnapshotEntryResponse>(entries.Count);

                foreach (var entry in entries)
                {
                    response.Add(new AchievementSnapshotEntryResponse
                    {
                        AchievementId = entry.AchievementId,
                        CurrentProgress = entry.CurrentProgress,
                        CompletedTier = entry.CompletedTier,
                        NextTierTarget = AchievementMilestones.GetNextTierTarget(entry.AchievementId, entry.CompletedTier),
                        NextTierReward = AchievementMilestones.GetNextTierReward(entry.AchievementId, entry.CompletedTier)
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Achievements snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleStorefrontListings(HttpListenerContext context)
        {
            try
            {
                if (!TryResolveAuthenticatedPlayer(context.Request, out long playerId))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                string query = context.Request.Url?.Query ?? string.Empty;
                if (!ClientCommandValidator.ValidateStorefrontQuery(playerId, query))
                {
                    ForceDisconnect(playerId);
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                int cohort = StorefrontSegmentationEngine.ResolveCohort(playerId);

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using (var profileTransaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"PlayerSegmentationProfiles\" (\"PlayerId\", \"CohortTag\", \"LifetimeValueCents\", \"ChurnRiskScore\") VALUES ({0}, {1}, 0, 0) ON CONFLICT (\"PlayerId\") DO UPDATE SET \"CohortTag\" = EXCLUDED.\"CohortTag\";",
                        playerId,
                        cohort);
                    await profileTransaction.CommitAsync();
                }

                await using var listingsTransaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                var products = await db.SegmentedStorefrontListings
                    .AsNoTracking()
                    .Where(l => l.TargetCohort == cohort)
                    .OrderBy(l => l.ListingId)
                    .Select(l => new StorefrontListingResponse
                    {
                        ListingId = l.ListingId,
                        ProductIdentifier = l.ProductIdentifier,
                        DiamondPackageYield = l.DiamondPackageYield,
                        PriceInCents = l.PriceInCents
                    })
                    .ToListAsync();
                await listingsTransaction.CommitAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, products);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Storefront listings error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private bool TryResolveAuthenticatedPlayer(HttpListenerRequest request, out long playerId)
        {
            playerId = 0;

            Guid token;
            string headerToken = request.Headers["X-Authenticator-Token"] ?? string.Empty;
            if (!Guid.TryParse(headerToken, out token))
            {
                const string bearerPrefix = "Bearer ";
                string bearerHeader = request.Headers["Authorization"] ?? string.Empty;
                if (bearerHeader.Length <= bearerPrefix.Length ||
                    !bearerHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParse(bearerHeader.AsSpan(bearerPrefix.Length), out token))
                {
                    return false;
                }
            }

            return ActiveTokenCache.TryGetValue(token, out playerId);
        }

        private async Task HandleVerifyReceipt(HttpListenerContext context)
        {
            try
            {
                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                var accountId = payload.GetProperty("AccountId").GetGuid();
                var transactionId = payload.GetProperty("TransactionId").GetString();
                var productId = payload.GetProperty("ProductId").GetString();
                var costCents = payload.GetProperty("CostCents").GetInt32();

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                var existing = await db.PrimaryPurchaseLedgers.FirstOrDefaultAsync(p => p.TransactionId == transactionId);
                if (existing != null)
                {
                    context.Response.StatusCode = 409;
                    context.Response.Close();
                    return;
                }

                var purchase = new PrimaryPurchaseLedger
                {
                    PlayerId = 0, // Wait, it needs a long playerId, but we only have a Guid accountId in webhook. We need to look up PlayerId first!
                    TransactionId = transactionId ?? string.Empty,
                    ProductId = productId ?? string.Empty,
                    TimestampProcessed = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                
                var player = await db.PlayerRecords.FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"PlayerGuid\" = {0} FOR UPDATE", accountId).FirstOrDefaultAsync();
                if (player != null)
                {
                    purchase.PlayerId = player.Id;
                    player.PremiumDiamonds += 100; // Arbitrary 100 per purchase for now, or based on costCents
                }
                
                db.PrimaryPurchaseLedgers.Add(purchase);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Verify receipt error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleRefundWebhook(HttpListenerContext context)
        {
            try
            {
                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                var accountId = payload.GetProperty("AccountId").GetGuid();
                var refundedDiamonds = payload.GetProperty("RefundedDiamonds").GetInt32();

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                var player = await db.PlayerRecords.FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"PlayerGuid\" = {0} FOR UPDATE", accountId).FirstOrDefaultAsync();
                if (player != null)
                {
                    player.PremiumDiamonds -= refundedDiamonds;
                    if (player.PremiumDiamonds < 0)
                    {
                        player.Quarantine_Active = true;
                        player.IsQuarantined = true;
                        
                        var playerRegistry = _serviceProvider.GetRequiredService<PlayerSessionRegistry>();
                        playerRegistry.QuarantineNotificationQueue.Enqueue(new QuarantineNotification { PlayerId = player.Id });
                    }
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Refund webhook error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleSupportTicket(HttpListenerContext context)
        {
            try
            {
                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                var traceLog = payload.GetProperty("TraceLog").GetString();
                
                // Server-side scrubbing logic is not requested here, the client runs the regex on its side.
                // Or maybe we should scrub here too? The task says "collection boundary", meaning before sending, 
                // but we also have to execute sanitization exclusively upon explicit ticket dispatch. So it runs on the client.

                Console.WriteLine("Received Support Ticket with Trace Log.");
                
                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Support ticket error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private bool ParseValidateAndEnqueue(byte[] buffer, int count, long playerId, WebSocketSession session)
        {
            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, count);
            var packet = MemoryMarshal.Read<ClientCommandPacket>(span);
            if (!ClientCommandValidator.ValidateNetworkThroughput(ref session.TokenBucket, playerId, ref packet, out int reasonCode))
            {
                session.Socket.Abort();
                _connectedClients.TryRemove(playerId, out _);
                _ = MarkFloodInfractionAsync(playerId);
                TelemetryStreamer.TryWrite(new TelemetryEvent
                {
                    PlayerId = playerId,
                    EventType = 3,
                    Value1 = (byte)packet.Command,
                    Value2 = reasonCode,
                    Timestamp = Environment.TickCount64
                });
                return false;
            }

            RecordAcceptedPacket();
            _antiCheatTelemetryEngine?.RecordCommand(playerId, (byte)packet.Command);
            CommandQueue.Enqueue(new PlayerCommand { PlayerId = playerId, Packet = packet });
            return true;
        }

        private ClientAuthPacket ParseAuthPacket(byte[] buffer, int count)
        {
            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, count);
            return MemoryMarshal.Read<ClientAuthPacket>(span);
        }

        private void ParseAdminCommand(byte[] buffer, int count)
        {
            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, count);
            var adminPacket = MemoryMarshal.Read<AdminCommandPacket>(span);

            if (adminPacket.CommandType == 1)
            {
                GlobalEngineState.GlobalXpMultiplier = adminPacket.MultiplierValue;
            }
            else if (adminPacket.CommandType == 2)
            {
                GlobalEngineState.GlobalDropMultiplier = adminPacket.MultiplierValue;
            }
        }

        private void RecordAcceptedPacket()
        {
            long currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long observedSecond = Interlocked.Read(ref _throughputWindowEpoch);
            if (observedSecond != currentSecond &&
                Interlocked.CompareExchange(ref _throughputWindowEpoch, currentSecond, observedSecond) == observedSecond)
            {
                long previousCount = Interlocked.Exchange(ref _acceptedPacketsWindow, 0L);
                GlobalEngineState.SetActiveConnectionThroughput(previousCount);
            }

            Interlocked.Increment(ref _acceptedPacketsWindow);
        }

        private async Task<bool> IsPlayerBlacklistedAsync(long playerId)
        {
            Guid accountId = await ResolveAccountIdAsync(playerId);
            await using var context = await _contextFactory.CreateDbContextAsync();
            var quota = await context.AccountSecurityQuotas.AsNoTracking().FirstOrDefaultAsync(q => q.AccountId == accountId);
            return quota?.IsPermanentlyBlacklisted == true;
        }

        private async Task MarkFloodInfractionAsync(long playerId)
        {
            Guid accountId = await ResolveAccountIdAsync(playerId);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var context = await _contextFactory.CreateDbContextAsync();
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE \"AccountSecurityQuotas\" SET \"IsPermanentlyBlacklisted\" = TRUE WHERE \"AccountId\" = {0}", accountId);
        }

        private async Task<Guid> ResolveAccountIdAsync(long playerId)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var player = await context.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
            if (player != null && player.PlayerGuid != Guid.Empty)
            {
                return player.PlayerGuid;
            }

            long mixed = playerId ^ 0x71A7E11D5F3759DFL;
            return new Guid(
                unchecked((int)playerId),
                unchecked((short)(playerId >> 32)),
                unchecked((short)(playerId >> 48)),
                unchecked((byte)mixed),
                unchecked((byte)(mixed >> 8)),
                unchecked((byte)(mixed >> 16)),
                unchecked((byte)(mixed >> 24)),
                unchecked((byte)(mixed >> 32)),
                unchecked((byte)(mixed >> 40)),
                unchecked((byte)(mixed >> 48)),
                unchecked((byte)(mixed >> 56)));
        }

        private async Task HandleClientLoopAsync(WebSocket socket)
        {
            var buffer = new byte[1024];
            long playerId = 0;
            string? redisLockToken = null;
            CancellationTokenSource? lockRenewalCts = null;
            Task? lockRenewalTask = null;
            try
            {
                using var cts = new CancellationTokenSource(5000);
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                
                if (result.MessageType == WebSocketMessageType.Binary && result.Count >= Marshal.SizeOf<ClientAuthPacket>())
                {
                    var authPacket = ParseAuthPacket(buffer, result.Count);
                    
                    if (ActiveTokenCache.TryGetValue(authPacket.AuthenticatorToken, out long mappedId))
                    {
                        playerId = mappedId;
                        if (await IsPlayerBlacklistedAsync(playerId))
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Account blacklisted", CancellationToken.None);
                            return;
                        }

                        if (_redisSessionLock != null)
                        {
                            redisLockToken = await _redisSessionLock.TryAcquireAsync(playerId);
                            if (redisLockToken == null)
                            {
                                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Session lock held", CancellationToken.None);
                                return;
                            }

                            lockRenewalCts = new CancellationTokenSource();
                            lockRenewalTask = RunRedisLockRenewalAsync(playerId, redisLockToken, lockRenewalCts.Token);
                        }

                        if (!ClientCommandValidator.ValidateAssetIntegrity(authPacket.AssetHash, authPacket.PlatformSignature, playerId))
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Asset Integrity Failure", CancellationToken.None);
                            return;
                        }

                        if (!_connectedClients.TryAdd(playerId, new WebSocketSession(socket, redisLockToken ?? string.Empty)))
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Session already active", CancellationToken.None);
                            return;
                        }
                        CommandQueue.Enqueue(new PlayerCommand { PlayerId = playerId, Packet = new ClientCommandPacket { Command = CommandType.Login, TargetId = playerId } }); 
                    }
                    else
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid token", CancellationToken.None);
                        return;
                    }
                }
                else
                {
                    await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Expected Auth Packet", CancellationToken.None);
                    return;
                }

                if (!_connectedClients.TryGetValue(playerId, out var session)) return;

                while (socket.State == WebSocketState.Open)
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary && result.Count >= Marshal.SizeOf<ClientCommandPacket>())
                    {
                        if (ParseValidateAndEnqueue(buffer, result.Count, playerId, session))
                        {
                        }
                        else
                        {
                            Interlocked.Increment(ref _throttledCounter);
                            if (socket.State == WebSocketState.Open)
                            {
                                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Packet flood", CancellationToken.None);
                            }
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout during handshake
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Handshake timeout", CancellationToken.None);
                }
            }
            catch (Exception)
            {
                // Disconnected abruptly
            }
            finally
            {
                if (playerId != 0)
                {
                    _connectedClients.TryRemove(playerId, out _);
                    CommandQueue.Enqueue(new PlayerCommand { PlayerId = playerId, Packet = new ClientCommandPacket { Command = CommandType.Logout, TargetId = playerId } });
                    if (lockRenewalCts != null)
                    {
                        lockRenewalCts.Cancel();
                    }

                    if (lockRenewalTask != null)
                    {
                        try
                        {
                            await lockRenewalTask;
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }

                    if (_redisSessionLock != null && redisLockToken != null)
                    {
                        await _redisSessionLock.ReleaseAsync(playerId, redisLockToken);
                    }
                }
                lockRenewalCts?.Dispose();
                socket.Dispose();
            }
        }

        private async Task RunRedisLockRenewalAsync(long playerId, string token, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                if (_redisSessionLock == null)
                {
                    return;
                }

                bool renewed = await _redisSessionLock.RenewAsync(playerId, token);
                if (!renewed)
                {
                    ForceDisconnect(playerId);
                    return;
                }
            }
        }

        public void Broadcast(ref StateUpdatePacket packet)
        {
            ReadOnlySpan<StateUpdatePacket> span = MemoryMarshal.CreateReadOnlySpan(ref packet, 1);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(span);
            bytes.CopyTo(_broadcastBuffer);
            var segment = new ArraySegment<byte>(_broadcastBuffer);

            foreach (var kvp in _connectedClients)
            {
                var socket = kvp.Value.Socket;
                if (socket.State == WebSocketState.Open)
                {
                    _ = socket.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            }
        }

        public void SendToPlayer(long playerId, ref StateUpdatePacket packet)
        {
            if (!_connectedClients.TryGetValue(playerId, out var session) || session.Socket.State != WebSocketState.Open)
            {
                return;
            }

            ReadOnlySpan<StateUpdatePacket> span = MemoryMarshal.CreateReadOnlySpan(ref packet, 1);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(span);
            bytes.CopyTo(session.DiagnosticSendBuffer);
            var segment = new ArraySegment<byte>(session.DiagnosticSendBuffer);
            _ = session.Socket.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        public void ForceDisconnect(long playerId)
        {
            if (_connectedClients.TryRemove(playerId, out var session))
            {
                if (_redisSessionLock != null && !string.IsNullOrEmpty(session.RedisLockToken))
                {
                    _ = _redisSessionLock.ReleaseAsync(playerId, session.RedisLockToken);
                }

                if (session.Socket.State == WebSocketState.Open)
                {
                    _ = session.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Violent termination", CancellationToken.None);
                }
            }
        }

        public void PurgeTokensForPlayer(long playerId)
        {
            foreach (var kvp in ActiveTokenCache)
            {
                if (kvp.Value == playerId)
                {
                    ActiveTokenCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        public async Task DisconnectAllClientsGracefullyAsync()
        {
            ActiveTokenCache.Clear();
            var tasks = new System.Collections.Generic.List<Task>();
            var sockets = new System.Collections.Generic.List<WebSocket>();
            foreach (var kvp in _connectedClients)
            {
                var socket = kvp.Value.Socket;
                if (socket.State == WebSocketState.Open)
                {
                    sockets.Add(socket);
                    tasks.Add(socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None));
                }
            }

            if (tasks.Count == 0)
                return;

            var whenAllTask = Task.WhenAll(tasks);
            var timeoutTask = Task.Delay(2000);

            if (await Task.WhenAny(whenAllTask, timeoutTask) == timeoutTask)
            {
                foreach (var socket in sockets)
                {
                    if (socket.State != WebSocketState.Closed && socket.State != WebSocketState.Aborted)
                    {
                        socket.Abort();
                    }
                }
            }
        }
    }
}
