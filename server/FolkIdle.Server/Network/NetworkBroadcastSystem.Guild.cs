using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{
    // Modul: moved out of NetworkBroadcastSystem.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md). Routing stays in RouteCoreAsync.
    public partial class NetworkBroadcastSystem
    {
        private async Task HandleGuildWarUnlock(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // Evaluates rather than reading a cache, so the screen shows
                // today's count - and if this is the request that crosses the
                // floor, it records the unlock like any other evaluator would.
                var status = await GuildWarUnlock.EvaluateAsync(db);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildWarUnlockResponse
                {
                    Unlocked = status.Unlocked,
                    QualifyingPlayers = status.QualifyingPlayers,
                    RequiredPlayers = GuildWarUnlock.RequiredQualifyingPlayers,
                    QualifyingGuilds = status.QualifyingGuilds,
                    RequiredGuilds = GuildWarUnlock.RequiredGuilds,
                    RequiredMembersPerGuild = GuildWarUnlock.RequiredMembersPerGuild,
                    MinimumLevel = LeaderboardTierRegistry.MinimumRankedLevel
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild war unlock error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildShardMatch(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                long guildId = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.GuildId)
                    .FirstOrDefaultAsync();

                GuildShardMatchResponse? payload = null;

                if (guildId > 0)
                {
                    var match = await db.GuildMatchmakingSnapshots
                        .AsNoTracking()
                        .Where(m => !m.IsComplete && (m.AttackerGuildId == guildId || m.DefenderGuildId == guildId))
                        .OrderBy(m => m.TournamentGroupIndex)
                        .FirstOrDefaultAsync();

                    if (match != null)
                    {
                        payload = new GuildShardMatchResponse
                        {
                            MatchUuid = match.MatchUuid.ToString(),
                            ActiveMatchMmr = match.ActiveMatchMmr,
                            GlobalNodeRemainingHp = match.GlobalNodeRemainingHp,
                            IsAttacker = match.AttackerGuildId == guildId
                        };
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                // A player with no guild, or a guild with no running match,
                // gets `null` rather than a 404 - "there is no match" is a
                // normal answer to this question, not a failure to answer it.
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild shard match error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildLogisticsSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
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

        // Modul: resolves the requesting player's own GuildId first (never
        // a client-supplied one), then lists every GuildMembers row sharing
        // that GuildId - a player can only ever see their own guild's
        // roster. IsOnline is resolved directly from this pod's own
        // _connectedClients (the same dictionary BroadcastChatMessage/
        // UpdateSessionGuildId already read/write), not a database column
        // - guild membership is persistent, but presence is a live,
        // in-memory fact. PlayerSessionRegistry is deliberately not used
        // here - it is never registered in Program.cs's DI container
        // (only ever constructed directly and passed to specific engine
        // constructors), so resolving it via _serviceProvider.
        // GetRequiredService would throw at runtime.
        private async Task HandleGuildRoster(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
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

                if (guildId <= 0)
                {
                    await transaction.CommitAsync();
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new System.Collections.Generic.List<GuildRosterEntryResponse>());
                    context.Response.Close();
                    return;
                }

                var members = await db.GuildMembers
                    .AsNoTracking()
                    .Where(m => m.GuildId == guildId)
                    .OrderByDescending(m => m.Role)
                    .ThenByDescending(m => m.ContributionPoints)
                    .ToListAsync();

                await transaction.CommitAsync();

                int viewerRole = members.FirstOrDefault(m => m.PlayerId == playerId)?.Role ?? -1;
                int elders = members.Count(m => m.Role == GuildManagementEngine.RoleElder);
                int wardens = members.Count(m => m.Role == GuildManagementEngine.RoleWarden);
                bool RoomFor(int role) => role == GuildManagementEngine.RoleElder ? elders < GuildManagementEngine.MaxElders
                    : role == GuildManagementEngine.RoleWarden ? wardens < GuildManagementEngine.MaxWardens : true;

                var entries = new System.Collections.Generic.List<GuildRosterEntryResponse>(members.Count);
                foreach (var member in members)
                {
                    bool other = member.PlayerId != playerId;
                    int up = member.Role + 1, down = member.Role - 1;
                    entries.Add(new GuildRosterEntryResponse
                    {
                        PlayerId = member.PlayerId,
                        Role = member.Role,
                        ContributionPoints = member.ContributionPoints,
                        IsOnline = _connectedClients.ContainsKey(member.PlayerId),
                        CanPromote = other && GuildManagementEngine.MayAssign(viewerRole, member.Role, up) && RoomFor(up),
                        CanDemote = other && GuildManagementEngine.MayAssign(viewerRole, member.Role, down),
                        CanKick = other && viewerRole >= GuildManagementEngine.RoleWarden && viewerRole > member.Role,
                        CanTransfer = other && viewerRole == GuildManagementEngine.RoleChieftain,
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild roster error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: A GUILD SEEN FROM OUTSIDE, opened from a member's profile. The
        // directory is for guildless players only (task 107), so this is how a
        // guild member looks at another guild: by id, read-only, and with only
        // the fields a stranger may see - no treasury gold, no depot, no
        // per-member contribution, no applications. See PublicProfiles.
        private async Task HandleGuildView(HttpListenerContext context)
        {
            try
            {
                long requesterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (requesterId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!long.TryParse(query["id"], out long guildId) || guildId <= 0)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await PublicProfiles.BuildGuildAsync(db, guildId, requesterId, id => _connectedClients.ContainsKey(id));
                if (view == null)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleGuildKick(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; context.Response.Close(); return; }
            try
            {
                long kickerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (kickerId <= 0) { context.Response.StatusCode = 401; context.Response.Close(); return; }

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));
                if (!payload.TryGetProperty("targetPlayerId", out var targetEl)) { context.Response.StatusCode = 400; context.Response.Close(); return; }
                long targetId = targetEl.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException());

                bool ok = await guildManagementEngine.KickMemberAsync(kickerId, targetId);
                context.Response.StatusCode = ok ? 200 : 403;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild kick error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleGuildPromote(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; context.Response.Close(); return; }
            try
            {
                long promoterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (promoterId <= 0) { context.Response.StatusCode = 401; context.Response.Close(); return; }

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));
                if (!payload.TryGetProperty("targetPlayerId", out var targetEl)) { context.Response.StatusCode = 400; context.Response.Close(); return; }
                long targetId = targetEl.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException());

                bool ok = await guildManagementEngine.PromoteMemberAsync(promoterId, targetId);
                context.Response.StatusCode = ok ? 200 : 403;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild promote error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        // Modul: THE CHIEFTAIN HANDS THE GUILD ON (owner, 2026-11-03). Same
        // shape as promote/demote: 200 done, 403 refused (not the Chieftain, or
        // the target is not in the guild).
        private async Task HandleGuildTransfer(HttpListenerContext context)
        {
            try
            {
                long chieftainId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (chieftainId <= 0) { context.Response.StatusCode = 401; context.Response.Close(); return; }

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));
                if (!payload.TryGetProperty("targetPlayerId", out var targetEl)) { context.Response.StatusCode = 400; context.Response.Close(); return; }
                long targetId = targetEl.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException());

                bool ok = await guildManagementEngine.TransferLeadershipAsync(chieftainId, targetId);
                context.Response.StatusCode = ok ? 200 : 403;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild transfer error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleGuildDemote(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; context.Response.Close(); return; }
            try
            {
                long demoterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (demoterId <= 0) { context.Response.StatusCode = 401; context.Response.Close(); return; }

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));
                if (!payload.TryGetProperty("targetPlayerId", out var targetEl)) { context.Response.StatusCode = 400; context.Response.Close(); return; }
                long targetId = targetEl.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException());

                bool ok = await guildManagementEngine.DemoteMemberAsync(demoterId, targetId);
                context.Response.StatusCode = ok ? 200 : 403;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild demote error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleGuildList(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);

                int skip = 0;
                int take = 25;
                if (int.TryParse(query["skip"], out int parsedSkip)) skip = parsedSkip;
                if (int.TryParse(query["take"], out int parsedTake)) take = parsedTake;

                // Clamped rather than rejected: an out-of-range page is a
                // client bug, not an attack, and returning a usable first page
                // beats a 400 the UI has to special-case.
                if (skip < 0) skip = 0;
                if (take < 1) take = 1;
                if (take > GuildListMaxTake) take = GuildListMaxTake;

                string nameFilter = (query["name"] ?? string.Empty).Trim();

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var guildQuery = db.GuildRecords.AsNoTracking();

                if (nameFilter.Length > 0)
                {
                    // Case-insensitive contains, so a half-remembered name still
                    // finds the guild - the entire point of this endpoint.
                    string lowered = nameFilter.ToLowerInvariant();
                    guildQuery = guildQuery.Where(g => g.Name.ToLower().Contains(lowered));
                }

                var rows = await guildQuery
                    .OrderByDescending(g => g.ActiveMembers)
                    .ThenBy(g => g.Id)
                    .Skip(skip)
                    .Take(take)
                    .Select(g => new GuildDirectoryEntryResponse
                    {
                        GuildId = g.Id,
                        Name = g.Name,
                        CurrentTier = g.CurrentTier,
                        ActiveMembers = g.ActiveMembers,
                        MaxMembers = g.MaxMembers,
                        GuildMMR = g.GuildMMR,
                        TaxRatePct = g.TaxRatePct,
                        JoinType = g.JoinType,
                        MinApplicationLevel = g.MinApplicationLevel
                    })
                    .ToListAsync();

                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, rows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild list lookup error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: UI audit follow-up - see this route's registration comment
        // for why this is HTTP rather than a WS CommandType. Delegates
        // entirely to GuildManagementEngine.CreateGuildAsync, which already
        // handles the level-20 gate, blank/oversized name rejection, and the
        // Serializable-isolation name-uniqueness guard - this handler only
        // translates its long? result into an HTTP response.
        private async Task HandleGuildCreate(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                if (!payload.TryGetProperty("guildName", out var guildNameElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string guildName = guildNameElement.GetString() ?? string.Empty;

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                var outcome = await guildManagementEngine.CreateGuildAsync(playerId, guildName);
                if (outcome.GuildId <= 0)
                {
                    // Modul: the refusal now says which rule was broken. This
                    // used to be a bare 409 with no body for four different
                    // reasons, so the player was told "Could not create" and
                    // had no way to discover that guilds need level 20.
                    string reason = outcome.Refusal switch
                    {
                        GuildManagementEngine.GuildCreateRefusal.AlreadyInAGuild
                            => "You are already in a guild. Leave it before founding another.",
                        GuildManagementEngine.GuildCreateRefusal.LevelTooLow
                            => $"Guilds open at level {outcome.RequiredLevel}. You are level {outcome.CurrentLevel}.",
                        GuildManagementEngine.GuildCreateRefusal.NameTaken
                            => "That name is taken.",
                        _ => $"That name will not do - one to {GuildManagementEngine.MaxGuildNameLength} characters.",
                    };

                    context.Response.StatusCode = 409;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildCreateRefusalResponse { Reason = reason });
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildCreateResponse { GuildId = outcome.GuildId });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild create error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: UI audit follow-up - see this route's registration comment
        // for the "join = self-service join-by-name" semantics. Guild name
        // -> id resolution happens inline (no separate lookup endpoint
        // exists for guilds, unlike Friends' username resolve) since there
        // is no "browse guilds" UI that would want the id on its own yet.
        private async Task HandleGuildJoin(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                if (!payload.TryGetProperty("guildName", out var guildNameElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string guildName = guildNameElement.GetString() ?? string.Empty;

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                long guildId = await db.GuildRecords
                    .AsNoTracking()
                    .Where(g => g.Name == guildName)
                    .Select(g => g.Id)
                    .FirstOrDefaultAsync();

                if (guildId <= 0)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                bool joined = await guildManagementEngine.JoinGuildAsync(playerId, guildId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildJoinResponse { Joined = joined });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild join error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: task 94 - see the route's registration comment. A refusal
        // (not in a guild, or a race with a kick) is a 409 WITH a reason, not
        // a silent 200: the player pressed a button and must be told why
        // nothing happened.
        private async Task HandleGuildLeave(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                var outcome = await guildManagementEngine.LeaveAsync(playerId);

                context.Response.StatusCode = outcome.Left ? 200 : 409;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildLeaveResponse
                {
                    Left = outcome.Left,
                    ClosedGuild = outcome.ClosedGuild,
                    SuccessorPlayerId = outcome.SuccessorPlayerId,
                    Reason = outcome.Left ? string.Empty : "You are not in a guild.",
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild leave error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildLeavePreview(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                var preview = await guildManagementEngine.PreviewLeaveAsync(playerId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildLeavePreviewResponse
                {
                    InGuild = preview.InGuild,
                    IsLeader = preview.IsLeader,
                    ClosesGuild = preview.ClosesGuild,
                    SuccessorPlayerId = preview.SuccessorPlayerId,
                    RemainingMembers = preview.RemainingMembers,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild leave preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: Play Mode audit fix. Leader-only list of this player's
        // guild's pending GuildApplications, joined against PlayerRecords
        // for a real Username (mirrors HandleFriendsList's exact reasoning
        // for not surfacing a bare numeric Id). Anyone who isn't the
        // guild's Leader (including players with no guild) gets an empty
        // list rather than a 403, matching HandleGuildRoster's "no guild"
        // convention.
        private async Task HandleGuildApplicationsPending(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var membership = await db.GuildMembers.AsNoTracking().FirstOrDefaultAsync(m => m.PlayerId == playerId);
                if (membership == null || membership.Role < GuildManagementEngine.RoleWarden)
                {
                    await transaction.CommitAsync();
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new System.Collections.Generic.List<GuildApplicationEntryResponse>());
                    context.Response.Close();
                    return;
                }

                var applications = await db.GuildApplications
                    .AsNoTracking()
                    .Where(a => a.GuildId == membership.GuildId)
                    .OrderBy(a => a.CreatedAtEpoch)
                    .ToListAsync();

                var applicantIds = applications.Select(a => a.PlayerId).ToList();
                var applicants = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => applicantIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => p);

                await transaction.CommitAsync();

                var entries = new System.Collections.Generic.List<GuildApplicationEntryResponse>(applications.Count);
                foreach (var application in applications)
                {
                    applicants.TryGetValue(application.PlayerId, out var applicantProfile);
                    entries.Add(new GuildApplicationEntryResponse
                    {
                        Id = application.Id,
                        PlayerId = application.PlayerId,
                        Username = applicantProfile?.Username ?? "(unknown player)",
                        ApplicantLevel = application.ApplicantLevel,
                        CreatedAtEpoch = application.CreatedAtEpoch
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild applications pending error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildApplicationApprove(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                if (!payload.TryGetProperty("applicationId", out var applicationIdElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                long applicationId = applicationIdElement.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                bool approved = await guildManagementEngine.ApproveApplicationAsync(playerId, applicationId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildApplicationActionResponse { Success = approved });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild application approve error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildApplicationReject(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                if (!payload.TryGetProperty("applicationId", out var applicationIdElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                long applicationId = applicationIdElement.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                bool rejected = await guildManagementEngine.RejectApplicationAsync(playerId, applicationId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildApplicationActionResponse { Success = rejected });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild application reject error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildDepotDonate(HttpListenerContext context)
        {
            try
            {
                long requesterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (requesterId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                string itemId = payload.GetProperty("itemId").GetString() ?? "";
                int quantity = 0;
                var qProp = payload.GetProperty("quantity");
                if (qProp.ValueKind == JsonValueKind.Number)
                {
                    quantity = qProp.GetInt32();
                }
                else if (qProp.ValueKind == JsonValueKind.String)
                {
                    int.TryParse(qProp.GetString(), out quantity);
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.PlayerId == requesterId);
                if (member == null)
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                var engine = new GuildContributionEngine(_serviceProvider, _playerSessionRegistry);
                bool success = await engine.ContributeDepotMaterialAsync(requesterId, member.GuildId, itemId, quantity);

                context.Response.StatusCode = success ? 200 : 400;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling depot donate: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleGuildBuffsActivate(HttpListenerContext context)
        {
            try
            {
                long requesterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (requesterId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                string buffType = payload.GetProperty("buffType").GetString() ?? "";
                int tier = payload.GetProperty("tier").GetInt32();
                string path = payload.GetProperty("path").GetString() ?? "common"; // "common" or "rare"

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.PlayerId == requesterId);
                if (member == null)
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                var engine = new GuildContributionEngine(_serviceProvider, _playerSessionRegistry);
                bool success = await engine.ActivateGuildBuffAsync(requesterId, member.GuildId, buffType, tier, path);

                context.Response.StatusCode = success ? 200 : 400;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling buff activate: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}
