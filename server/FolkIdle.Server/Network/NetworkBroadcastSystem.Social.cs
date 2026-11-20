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
        /// <summary>
        /// Task 110e. The newest world, announcement and (own) guild messages,
        /// oldest first, so a player signing in does not read "Nothing in this
        /// channel yet" over a channel that was busy a minute ago. Filtering is
        /// ChatHistory.ReadRecentAsync's: the reader's current guild only, and
        /// nothing from anyone the reader has blocked. Whispers are not here.
        /// </summary>
        private async Task HandleChatRecent(HttpListenerContext context)
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

                var recent = await ChatHistory.ReadRecentAsync(db, playerId);

                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, recent);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chat history error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// One row per person this player has exchanged messages with, newest
        /// first, with the last line and an unread count.
        /// </summary>
        private async Task HandleConversationList(HttpListenerContext context)
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

                // Modul: the pair is stored sorted, so "my threads" is the rows
                // where I am either end. The counterpart is then whichever end
                // is not me - which is why sender and recipient are kept
                // alongside the sorted pair rather than derived from it.
                var mine = await db.ConversationMessages
                    .AsNoTracking()
                    .Where(m => m.LowPlayerId == playerId || m.HighPlayerId == playerId)
                    .OrderByDescending(m => m.SentAtEpochMs)
                    .ToListAsync();

                var counterpartIds = mine
                    .Select(m => m.LowPlayerId == playerId ? m.HighPlayerId : m.LowPlayerId)
                    .Distinct()
                    .ToList();

                var names = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => counterpartIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => p.Username);

                await transaction.CommitAsync();

                var summaries = new System.Collections.Generic.List<ConversationSummaryResponse>(counterpartIds.Count);
                foreach (long other in counterpartIds)
                {
                    var thread = mine.Where(m => m.LowPlayerId == other || m.HighPlayerId == other).ToList();
                    var latest = thread[0]; // already ordered newest first

                    summaries.Add(new ConversationSummaryResponse
                    {
                        PlayerId = other,
                        Username = names.TryGetValue(other, out string? name) && name != null ? name : "(unknown player)",
                        LastMessage = latest.MessageText,
                        LastMessageAtEpochMs = latest.SentAtEpochMs,
                        LastMessageWasMine = latest.SenderPlayerId == playerId,
                        // Unread means addressed TO me and not yet opened. A
                        // message cannot be unread by the person who sent it.
                        UnreadCount = thread.Count(m => m.RecipientPlayerId == playerId && m.ReadAtEpochMs == null),
                        IsOnline = _connectedClients.ContainsKey(other)
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, summaries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Conversation list error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// One thread, oldest-last, capped. `withPlayerId` names the other
        /// person; `before` pages backwards through time.
        /// </summary>
        private async Task HandleConversationHistory(HttpListenerContext context)
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

                if (!long.TryParse(context.Request.QueryString["withPlayerId"], out long otherPlayerId) || otherPlayerId <= 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                // Paging is by timestamp rather than offset: new messages
                // arrive while a player scrolls, and an offset would then skip
                // or repeat a line as the window shifts under it.
                long before = long.TryParse(context.Request.QueryString["before"], out long b) && b > 0
                    ? b
                    : long.MaxValue;

                const int PageSize = 50;

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var (low, high) = ConversationMessage.PairKey(playerId, otherPlayerId);

                var page = await db.ConversationMessages
                    .AsNoTracking()
                    .Where(m => m.LowPlayerId == low && m.HighPlayerId == high && m.SentAtEpochMs < before)
                    .OrderByDescending(m => m.SentAtEpochMs)
                    .Take(PageSize)
                    .ToListAsync();

                await transaction.CommitAsync();

                // Returned oldest-first, which is the order a conversation is
                // read in. The query runs newest-first because that is what the
                // index serves and what paging needs.
                page.Reverse();

                var response = page.Select(m => new ConversationMessageResponse
                {
                    Id = m.Id,
                    SenderPlayerId = m.SenderPlayerId,
                    Mine = m.SenderPlayerId == playerId,
                    MessageText = m.MessageText,
                    SentAtEpochMs = m.SentAtEpochMs,
                    Read = m.ReadAtEpochMs != null
                }).ToList();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Conversation history error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>Marks everything the caller has RECEIVED in one thread as read.</summary>
        private async Task HandleConversationMarkRead(HttpListenerContext context)
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

                if (!payload.TryGetProperty("withPlayerId", out var withProp)
                    || !withProp.TryGetInt64(out long otherPlayerId)
                    || otherPlayerId <= 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var (low, high) = ConversationMessage.PairKey(playerId, otherPlayerId);
                long nowEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Modul: RecipientPlayerId == playerId is what makes this safe
                // to expose. The caller can only ever mark their own incoming
                // messages read - naming somebody else's thread marks nothing,
                // because no row in it is addressed to them.
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE conversation_messages SET \"ReadAtEpochMs\" = {0} " +
                    "WHERE \"LowPlayerId\" = {1} AND \"HighPlayerId\" = {2} " +
                    "AND \"RecipientPlayerId\" = {3} AND \"ReadAtEpochMs\" IS NULL",
                    nowEpochMs, low, high, playerId);

                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Conversation mark-read error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleFriendsList(HttpListenerContext context)
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

                var relationships = await db.PlayerRelationships
                    .AsNoTracking()
                    .Where(r => r.PlayerId == playerId)
                    .ToListAsync();

                var targetIds = relationships.Select(r => r.TargetPlayerId).ToList();
                var targets = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => targetIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => p);

                await transaction.CommitAsync();

                var response = new System.Collections.Generic.List<FriendEntryResponse>(relationships.Count);
                foreach (var rel in relationships)
                {
                    targets.TryGetValue(rel.TargetPlayerId, out var target);
                    response.Add(new FriendEntryResponse
                    {
                        PlayerId = rel.TargetPlayerId,
                        Username = target?.Username ?? "(unknown player)",
                        Level = target?.CurrentLevel ?? 0,
                        IsBlocked = rel.RelationType == RelationType.Blocked,

                        // Live connection table rather than a persisted column:
                        // a stored "is online" flag goes stale the moment a
                        // process dies without a clean logout, and would then
                        // claim someone is online forever.
                        //
                        // POD-LOCAL. _connectedClients is this pod's own
                        // WebSocket table, so a friend connected to a different
                        // pod reads as offline. Correct for a single-pod
                        // deployment, which is what this runs as today; a
                        // multi-pod answer needs a Redis presence key, and
                        // inventing one here would be a second source of truth
                        // about who is online. Recorded rather than faked.
                        IsOnline = _connectedClients.ContainsKey(rel.TargetPlayerId)
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Friends list error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: UI audit follow-up. AddFriend/BlockPlayer (RelationshipEngine,
        // WebSocketClient.SendAddFriendCommandZeroAlloc/SendBlockPlayerCommandZeroAlloc)
        // only ever took a raw numeric TargetPlayerId with no client-side way
        // to discover it - this resolves the one piece of public information
        // a player would actually type, their username, to that Id.
        // It is now case-insensitive using ILike so players don't have to match case perfectly.
        private async Task HandlePlayerResolve(HttpListenerContext context)
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
                string username = (query["username"] ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(username))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                long targetPlayerId = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Username != null && EF.Functions.ILike(p.Username, username))
                    .Select(p => p.Id)
                    .FirstOrDefaultAsync();

                await transaction.CommitAsync();

                if (targetPlayerId <= 0)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new PlayerResolveResponse { PlayerId = targetPlayerId });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player resolve error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: ONE COMPACT ANSWER, built by PublicProfiles.BuildProfileAsync.
        // This used to serialise every CharacterRecord on the account whole
        // (185 on the dev fixture) plus raw equipment entities, and the client
        // drew a blank "Level" per bred child. A profile opens on a tap, so its
        // cost is paid by a waiting thumb - see PublicProfiles for the shape.
        private async Task HandlePlayerProfile(HttpListenerContext context)
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
                if (!long.TryParse(query["id"], out long targetId) || targetId <= 0)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await PublicProfiles.BuildProfileAsync(db, targetId, id => _connectedClients.ContainsKey(id));
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
                Console.WriteLine($"Profile fetch error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleJourney(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var rows = await Engine.JourneyBoard.LoadAsync(db, playerId);
                if (rows == null)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                // The Dungeon row's hint names the nearest next stage, forecast
                // (2026-11-13) - only while the player is online, because the
                // forecast fights with the live payload.
                int dungeonRow = rows.FindIndex(r => r.Key == "dungeon");
                if (dungeonRow >= 0)
                {
                    string? hint = await DungeonRoadHintAsync(db, playerId);
                    if (hint != null) rows[dungeonRow] = rows[dungeonRow] with { Hint = hint };
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Rows = rows });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Journey error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task WriteTitlesAsync(HttpListenerContext context, FolkIdleDbContext db, long playerId, string? result)
        {
            var player = await db.PlayerRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playerId);
            var titles = await FolkIdle.Server.Domain.Progression.TitleEngine.ListAsync(db, playerId);
            var active = FolkIdle.Server.Domain.Progression.TitleRegistry.Find(player?.ActiveTitleSlug);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
            {
                Result = result,
                Titles = titles.Select(t => new { t.Slug, t.Name, t.EarnedAtUtc }),
                Active = active == null ? null : new { active.Slug, Name = active.DisplayName },
            });
        }

        private async Task HandleTitlesView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await WriteTitlesAsync(context, db, playerId, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Titles view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// Wear a title, or clear it with { "Slug": null }. An unearned or
        /// unknown slug answers 200 with its Result - NotEarned / UnknownTitle -
        /// and changes nothing, so the screen can say why.
        /// </summary>
        private async Task HandleTitleSet(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                string? slug;
                try
                {
                    using var parsed = JsonDocument.Parse(await ReadBodyAsync(context));
                    if (!parsed.RootElement.TryGetProperty("Slug", out var slugElement)) { context.Response.StatusCode = 400; return; }
                    if (slugElement.ValueKind == JsonValueKind.Null) slug = null;
                    else if (slugElement.ValueKind == JsonValueKind.String) slug = slugElement.GetString();
                    else { context.Response.StatusCode = 400; return; }
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var result = await FolkIdle.Server.Domain.Progression.TitleEngine.SetActiveAsync(db, playerId, slug);
                await WriteTitlesAsync(context, db, playerId, result.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Title set error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}
