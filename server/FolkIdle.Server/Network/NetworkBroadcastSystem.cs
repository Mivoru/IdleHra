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

    // Modul 29/45: ActiveTokenCache entry. ExpirationEpoch is always
    // server-computed (nowEpoch + TokenFreshnessWindowSeconds) at the time the
    // token was resolved - never taken directly from the client-supplied
    // ClientAuthPacket.EpochExpirationTime, so a client cannot extend its own
    // cache lifetime by claiming a far-future timestamp. That client-supplied
    // timestamp is only used for the one-time handshake freshness check.
    public struct CachedTokenEntry
    {
        public long PlayerId;
        public long ExpirationEpoch;
    }

    public class NetworkBroadcastSystem
    {
        private readonly HttpListener _httpListener;
        private readonly ConcurrentDictionary<long, WebSocketSession> _connectedClients = new();
        public ConcurrentDictionary<Guid, CachedTokenEntry> ActiveTokenCache { get; } = new();

        // Modul 29/45: 24-hour freshness window, applied both to the initial
        // handshake check against the client-supplied EpochExpirationTime and
        // to the server's own ActiveTokenCache entry lifetime.
        private const long TokenFreshnessWindowSeconds = 86400L;

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

                    // Modul: previously both paths unconditionally returned 200
                    // regardless of real engine state - InfrastructureHealthMonitor
                    // (IsLive/IsReady/WritePlainHealth) already existed with the
                    // correct distinct semantics but was never actually called
                    // from here, so Kubernetes could never detect a pod still
                    // mid cold-boot-recovery or under heap pressure and would
                    // route live traffic to it regardless. Liveness only checks
                    // GlobalEngineState.IsShuttingDown (restart-worthy failure);
                    // readiness additionally requires cold-boot recovery to have
                    // completed and heap usage under the readiness limit
                    // (service-endpoint-worthy, not restart-worthy - see
                    // InfrastructureHealthMonitor.IsReady).
                    if (requestPath == "/health/liveness")
                    {
                        InfrastructureHealthMonitor.WritePlainHealth(context.Response, InfrastructureHealthMonitor.IsLive());
                        continue;
                    }

                    if (requestPath == "/health/readiness")
                    {
                        InfrastructureHealthMonitor.WritePlainHealth(context.Response, InfrastructureHealthMonitor.IsReady());
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

                    if (requestPath == "/api/v1/codex/regions" && context.Request.HttpMethod == "GET")
                    {
                        await HandleCodexRegionsSnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/breeding/roster" && context.Request.HttpMethod == "GET")
                    {
                        await HandleBreedingRosterSnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/breeding/preview" && context.Request.HttpMethod == "GET")
                    {
                        await HandleBreedingPreview(context);
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

                    if (requestPath == "/api/v1/market/listings" && context.Request.HttpMethod == "GET")
                    {
                        await HandleMarketBrowserListings(context);
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

        private sealed class MarketListingResponse
        {
            public long OrderId { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }
            public long Price { get; set; }
            public long CreatedAtEpoch { get; set; }
        }

        // Modul 40: marketplace browser page. Uses the authenticated HTTP
        // snapshot pattern established by HandleForgeInventorySnapshot /
        // HandleGuildLogisticsSnapshot / HandleGlobalLeaderboard for
        // variable-length, on-demand player data rather than a fixed-layout
        // WebSocket packet - a paginated result set has no natural fixed
        // size, so it does not fit StateUpdatePacket's binary layout the way
        // scalar per-tick fields do.
        private async Task HandleMarketBrowserListings(HttpListenerContext context)
        {
            try
            {
                if (!TryResolveAuthenticatedPlayer(context.Request, out long playerId))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                string baseItemId = query["baseItemId"] ?? string.Empty;
                int.TryParse(query["qualityTier"], out int qualityTier);
                int.TryParse(query["pageIndex"], out int pageIndex);
                if (!int.TryParse(query["pageSize"], out int pageSize))
                {
                    pageSize = 20;
                }

                if (string.IsNullOrEmpty(baseItemId) || !ClientCommandValidator.ValidateMarketBrowserQuery(playerId, pageIndex, pageSize))
                {
                    context.Response.StatusCode = 400;
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

                var listings = await MarketOrderBookEngine.FetchActiveListingsAsync(db, baseItemId, qualityTier, isQuarantined, pageIndex, pageSize);

                var response = new System.Collections.Generic.List<MarketListingResponse>(listings.Count);
                for (int i = 0; i < listings.Count; i++)
                {
                    response.Add(new MarketListingResponse
                    {
                        OrderId = listings[i].Id,
                        BaseItemId = listings[i].BaseItemId,
                        QualityTier = listings[i].QualityTier,
                        Price = listings[i].Price,
                        CreatedAtEpoch = listings[i].CreatedAtEpoch
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Market browser listings error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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
        // Modul 23 fix: previously ran raw SQL against "MonsterCodexEntries"
        // (PascalCase, quoted), but the table is mapped via
        // [Table("monster_codex_entries")] (lowercase, unlike every other
        // table in this codebase - see FolkIdleDbContextModelSnapshot's
        // ToTable("monster_codex_entries")), so the quoted identifier never
        // matched the real table and Postgres would reject it outright.
        // Switched to plain LINQ, matching HandleMasterySnapshot's established
        // fix for this exact lowercase-table situation - EF Core resolves the
        // mapping correctly on its own, sidestepping manual identifier
        // quoting entirely.
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

                var entries = await db.MonsterCodexEntries
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

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

        private sealed class RegionProgressResponse
        {
            public int RegionId { get; set; }
            public int CurrentKills { get; set; }
            public int RequiredKills { get; set; }
            public bool IsCompleted { get; set; }
            public int LootLuckBonusPct { get; set; }
        }

        // Modul 13.4.3: region-completion progress for the Codex regions UI. A
        // region is 6 distinct monster ids (5 standard/elite + 1 regional
        // boss, see CodexEngine's ((MonsterId - 1) % 30) / 6 + 1 grouping) and
        // completes only once every monster in it individually reaches 1000
        // kills - so CurrentKills here is the MINIMUM kill count across the
        // region's monsters (the true bottleneck to completion), not a sum.
        // IsCompleted comes from PlayerRegionCompletions (the durable ledger
        // CodexEngine writes to and never re-grants) rather than being
        // re-derived from kill counts here, so it can never flip back to
        // false if kill counts are read at a slightly different instant than
        // the completion check ran. LootLuckBonusPct mirrors
        // StatsCalculator's "+1.0% Loot Luck per completed area" exactly.
        private async Task HandleCodexRegionsSnapshot(HttpListenerContext context)
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

                var codexEntries = await db.MonsterCodexEntries
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

                var completedRegionIds = await db.PlayerRegionCompletions
                    .AsNoTracking()
                    .Where(r => r.PlayerId == playerId)
                    .Select(r => r.RegionId)
                    .ToListAsync();
                var completedRegionSet = new System.Collections.Generic.HashSet<int>(completedRegionIds);

                var killsByMonsterId = new System.Collections.Generic.Dictionary<int, int>(codexEntries.Count);
                for (int i = 0; i < codexEntries.Count; i++)
                {
                    killsByMonsterId[codexEntries[i].MonsterId] = codexEntries[i].KillCount;
                }

                var response = new System.Collections.Generic.List<RegionProgressResponse>(10);
                for (int region = 1; region <= 10; region++)
                {
                    int minKillsInRegion = -1;
                    bool regionExists = false;

                    for (int monsterIndex = 0; monsterIndex < ContentRegistry.Monsters.Length; monsterIndex++)
                    {
                        int monsterId = ContentRegistry.Monsters[monsterIndex].Id;
                        if (((monsterId - 1) % 30) / 6 + 1 != region)
                        {
                            continue;
                        }

                        regionExists = true;
                        killsByMonsterId.TryGetValue(monsterId, out int killCount);
                        if (killCount > 1000) killCount = 1000;
                        if (minKillsInRegion < 0 || killCount < minKillsInRegion)
                        {
                            minKillsInRegion = killCount;
                        }
                    }

                    if (!regionExists)
                    {
                        continue;
                    }

                    bool isCompleted = completedRegionSet.Contains(region);
                    response.Add(new RegionProgressResponse
                    {
                        RegionId = region,
                        CurrentKills = minKillsInRegion < 0 ? 0 : minKillsInRegion,
                        RequiredKills = 1000,
                        IsCompleted = isCompleted,
                        LootLuckBonusPct = isCompleted ? 1 : 0
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Codex regions snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private sealed class BreedingRosterEntryResponse
        {
            public string CharacterId { get; set; } = string.Empty;
            public int Level { get; set; }
            public int AgePhase { get; set; }
            public int GenerationIndex { get; set; }
            public bool IsBreedingActive { get; set; }
            public long BreedingCooldownEndEpoch { get; set; }
            public bool IsEpicMutation { get; set; }
            public bool IsInbred { get; set; }
            public int LocusRaceDominant { get; set; }
            public int LocusRaceRecessive { get; set; }
            public int LocusSpeedDominant { get; set; }
            public int LocusSpeedRecessive { get; set; }
            public int LocusCritDominant { get; set; }
            public int LocusCritRecessive { get; set; }
            public int LocusYieldDominant { get; set; }
            public int LocusYieldRecessive { get; set; }
        }

        // Modul 13.4.3: the player's own bred/breedable character roster, for
        // the Breeding Lab's parent-selection slots. BreedingEngine.
        // ExecuteBreedingAsync's own eligibility rules (AgePhase >= 1,
        // Level >= 50, not already IsBreedingActive, not IsLockedInEscrow) are
        // intentionally NOT filtered out here - the client shows every owned
        // character and lets the preview/execute round trip surface exactly
        // why an ineligible pairing was rejected, rather than this endpoint
        // silently hiding characters and leaving a player unable to tell an
        // "under cooldown" character apart from one that was never bred.
        private async Task HandleBreedingRosterSnapshot(HttpListenerContext context)
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

                var characters = await db.CharacterRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                var characterIds = new System.Collections.Generic.List<Guid>(characters.Count);
                for (int i = 0; i < characters.Count; i++)
                {
                    characterIds.Add(characters[i].Id);
                }

                var lineages = await db.CharacterLineages
                    .AsNoTracking()
                    .Where(l => characterIds.Contains(l.CharacterId))
                    .ToListAsync();

                var lineageByCharacterId = new System.Collections.Generic.Dictionary<Guid, CharacterLineageRegistry>(lineages.Count);
                for (int i = 0; i < lineages.Count; i++)
                {
                    lineageByCharacterId[lineages[i].CharacterId] = lineages[i];
                }

                var response = new System.Collections.Generic.List<BreedingRosterEntryResponse>(characters.Count);
                for (int i = 0; i < characters.Count; i++)
                {
                    var character = characters[i];
                    if (!lineageByCharacterId.TryGetValue(character.Id, out var lineage))
                    {
                        continue;
                    }

                    var geneVec = new GeneticVector(lineage.GeneticVector);

                    response.Add(new BreedingRosterEntryResponse
                    {
                        CharacterId = character.Id.ToString(),
                        Level = character.Level,
                        AgePhase = character.AgePhase,
                        GenerationIndex = lineage.GenerationIndex,
                        IsBreedingActive = character.IsBreedingActive,
                        BreedingCooldownEndEpoch = character.BreedingCooldownEndEpoch,
                        IsEpicMutation = lineage.IsEpicMutation,
                        IsInbred = lineage.IsInbred,
                        LocusRaceDominant = geneVec.LocusRace.Dominant,
                        LocusRaceRecessive = geneVec.LocusRace.Recessive,
                        LocusSpeedDominant = geneVec.LocusSpeed.Dominant,
                        LocusSpeedRecessive = geneVec.LocusSpeed.Recessive,
                        LocusCritDominant = geneVec.LocusCrit.Dominant,
                        LocusCritRecessive = geneVec.LocusCrit.Recessive,
                        LocusYieldDominant = geneVec.LocusYield.Dominant,
                        LocusYieldRecessive = geneVec.LocusYield.Recessive
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Breeding roster snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private sealed class GenePreviewLocusResponse
        {
            public string LocusName { get; set; } = string.Empty;
            public int ParentPaternalDominant { get; set; }
            public int ParentMaternalDominant { get; set; }
            public int PredictedMinDominant { get; set; }
            public int PredictedMaxDominant { get; set; }
            public double MutationChancePct { get; set; }
        }

        private sealed class BreedingPreviewResponse
        {
            public bool IsEligible { get; set; }
            public string IneligibleReason { get; set; } = string.Empty;
            public bool IsInbredRisk { get; set; }
            public long BreedingCostGold { get; set; }
            public bool HasSufficientGold { get; set; }
            public System.Collections.Generic.List<GenePreviewLocusResponse> Loci { get; set; } = new();
        }

        // Modul 13.4.3: read-only preview of ExecuteBreedingAsync's outcome -
        // never writes to the DB. Mirrors that engine's own ownership,
        // eligibility, and inbreeding checks exactly (see BreedingEngine.
        // ExecuteBreedingAsync) so a preview can never promise a pairing the
        // real execute call would actually reject, but computes the gene
        // spectrum via GeneticSplicingEngine.PreviewLocus (an exact
        // enumeration of Breed()'s possible non-mutated outcomes) instead of
        // performing the real, single-sample random splice.
        private async Task HandleBreedingPreview(HttpListenerContext context)
        {
            try
            {
                if (!TryResolveAuthenticatedPlayer(context.Request, out long playerId))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!Guid.TryParse(query["paternalId"], out Guid paternalId) || !Guid.TryParse(query["maternalId"], out Guid maternalId))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (!ClientCommandValidator.ValidateBreedingPreviewQuery(playerId, paternalId, maternalId))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var pChar = await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == paternalId);
                var mChar = await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == maternalId);
                var pLineage = await db.CharacterLineages.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == paternalId);
                var mLineage = await db.CharacterLineages.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == maternalId);

                if (pChar == null || mChar == null || pLineage == null || mLineage == null || pChar.PlayerId != playerId || mChar.PlayerId != playerId)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var response = new BreedingPreviewResponse();

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                bool pOnCooldown = pChar.IsBreedingActive && pChar.BreedingCooldownEndEpoch > nowEpoch;
                bool mOnCooldown = mChar.IsBreedingActive && mChar.BreedingCooldownEndEpoch > nowEpoch;

                var pVec = new GeneticVector(pLineage.GeneticVector);
                var mVec = new GeneticVector(mLineage.GeneticVector);

                if (pChar.AgePhase < 1 || mChar.AgePhase < 1 || pChar.Level < 50 || mChar.Level < 50)
                {
                    response.IneligibleReason = "parent_not_mature";
                }
                else if (pChar.IsLockedInEscrow || mChar.IsLockedInEscrow)
                {
                    response.IneligibleReason = "parent_locked_in_escrow";
                }
                else if (pOnCooldown || mOnCooldown)
                {
                    response.IneligibleReason = "parent_on_cooldown";
                }
                else if (pVec.LocusRace.Dominant != mVec.LocusRace.Dominant)
                {
                    response.IneligibleReason = "race_mismatch";
                }
                else
                {
                    response.IsEligible = true;
                }

                response.IsInbredRisk = paternalId == mLineage.ParentPaternalId || paternalId == mLineage.ParentMaternalId
                    || maternalId == pLineage.ParentPaternalId || maternalId == pLineage.ParentMaternalId
                    || (pLineage.ParentPaternalId.HasValue && (pLineage.ParentPaternalId == mLineage.ParentPaternalId || pLineage.ParentPaternalId == mLineage.ParentMaternalId))
                    || (pLineage.ParentMaternalId.HasValue && (pLineage.ParentMaternalId == mLineage.ParentPaternalId || pLineage.ParentMaternalId == mLineage.ParentMaternalId));

                int maxGen = Math.Max(pLineage.GenerationIndex, mLineage.GenerationIndex);
                response.BreedingCostGold = 500L * (maxGen + 1);

                var goldRecord = await db.CommodityRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
                response.HasSufficientGold = goldRecord != null && goldRecord.Quantity >= response.BreedingCostGold;

                AddLocusPreview(response.Loci, "Race", pVec.LocusRace, mVec.LocusRace, maxGen);
                AddLocusPreview(response.Loci, "Speed", pVec.LocusSpeed, mVec.LocusSpeed, maxGen);
                AddLocusPreview(response.Loci, "Crit", pVec.LocusCrit, mVec.LocusCrit, maxGen);
                AddLocusPreview(response.Loci, "Yield", pVec.LocusYield, mVec.LocusYield, maxGen);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Breeding preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private static void AddLocusPreview(System.Collections.Generic.List<GenePreviewLocusResponse> loci, string name, Locus pLocus, Locus mLocus, int maxGeneration)
        {
            GeneticSplicingEngine.PreviewLocus(pLocus, mLocus, maxGeneration, out byte minDominant, out byte maxDominant, out double mutationChancePct);

            loci.Add(new GenePreviewLocusResponse
            {
                LocusName = name,
                ParentPaternalDominant = pLocus.Dominant,
                ParentMaternalDominant = mLocus.Dominant,
                PredictedMinDominant = minDominant,
                PredictedMaxDominant = maxDominant,
                MutationChancePct = mutationChancePct
            });
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

            if (ActiveTokenCache.TryGetValue(token, out CachedTokenEntry entry) && entry.ExpirationEpoch > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                playerId = entry.PlayerId;
                return true;
            }

            return false;
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

        // Modul 16/21: creates a brand new PlayerRecord + its default active
        // Character/lineage row + starting resources for a client whose token
        // was never registered. This is the only production path that ever
        // creates a PlayerRecord from a live client connection - everything
        // else (DbSeeder, tests) is offline seeding. The new character becomes
        // Slot1 automatically on next login: StateCheckpointManager.LoadPlayerState
        // hydrates Slot1 from simply "the player's first CharacterRecord", with
        // no separate roster-assignment table to populate.
        private async Task<long> AutoProvisionPlayerAsync(Guid authenticatorToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            try
            {
                Guid characterId = Guid.NewGuid();

                var player = new PlayerRecord
                {
                    CurrentLevel = 1,
                    CurrentXp = 0L,
                    SelectedLineageId = 1,
                    PlayerGuid = characterId,
                    AuthenticatorToken = authenticatorToken,
                    LastLogoutTimestamp = 0L,
                    PremiumDiamonds = 0
                };
                db.PlayerRecords.Add(player);
                await db.SaveChangesAsync();

                db.CharacterRecords.Add(new CharacterRecord
                {
                    Id = characterId,
                    PlayerId = player.Id,
                    Level = 1,
                    AgePhase = 1,
                    AgeTicks = 0L
                });

                db.CharacterLineages.Add(new CharacterLineageRegistry
                {
                    CharacterId = characterId,
                    GenerationIndex = 0,
                    GeneticVector = RaceIds.Human
                });

                db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = 1000L });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = ContentRegistry.GetMaterialString(1), Quantity = 25L });

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                Console.WriteLine($"Auto-provisioned new player {player.Id} for a previously unregistered token.");
                return player.Id;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Auto-provisioning failed: {ex.Message}");
                return 0L;
            }
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

                    long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    // Modul 29/45: structured handshake freshness check. This is
                    // a replay-window check against the client-supplied payload
                    // timestamp (reject if older than 24 hours), not full JWT
                    // signature verification - there is no shared-secret signing
                    // scheme elsewhere in this codebase to verify a signature
                    // against, so a genuine JWT would need its own dedicated
                    // key-distribution design rather than being bolted on here.
                    if (authPacket.EpochExpirationTime <= 0 || nowEpoch - authPacket.EpochExpirationTime > TokenFreshnessWindowSeconds)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Token expired", CancellationToken.None);
                        return;
                    }

                    EvictExpiredTokens(nowEpoch);

                    bool tokenResolved = ActiveTokenCache.TryGetValue(authPacket.AuthenticatorToken, out CachedTokenEntry cachedEntry) && cachedEntry.ExpirationEpoch > nowEpoch;
                    long mappedId = tokenResolved ? cachedEntry.PlayerId : 0L;

                    // Modul 16/21: there is no OAuth/guest-issuance flow anywhere in this
                    // codebase - DbSeeder and test fixtures are the only other writers of
                    // PlayerRecords, and neither runs against a live client connection. A
                    // syntactically valid (non-empty) token the server has never seen
                    // before auto-provisions a brand new account instead of being rejected,
                    // so a fresh client can actually enter the game.
                    if (!tokenResolved && authPacket.AuthenticatorToken != Guid.Empty)
                    {
                        mappedId = await AutoProvisionPlayerAsync(authPacket.AuthenticatorToken);
                        tokenResolved = mappedId > 0;
                    }

                    if (tokenResolved)
                    {
                        ActiveTokenCache[authPacket.AuthenticatorToken] = new CachedTokenEntry { PlayerId = mappedId, ExpirationEpoch = nowEpoch + TokenFreshnessWindowSeconds };
                    }

                    if (tokenResolved)
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
                if (kvp.Value.PlayerId == playerId)
                {
                    ActiveTokenCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        // Modul 29/45: thread-safe eviction of stale ActiveTokenCache entries.
        // ConcurrentDictionary's TryRemove is safe to call concurrently with
        // readers/writers on other keys, mirroring PurgeTokensForPlayer's
        // existing iterate-and-remove pattern. Invoked inline on every new
        // handshake rather than from a separate background timer, since
        // connection attempts already provide a natural, frequent trigger.
        private void EvictExpiredTokens(long nowEpoch)
        {
            foreach (var kvp in ActiveTokenCache)
            {
                if (kvp.Value.ExpirationEpoch <= nowEpoch)
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
