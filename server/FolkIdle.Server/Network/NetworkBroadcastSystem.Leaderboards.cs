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
        private async Task HandleGlobalLeaderboard(HttpListenerContext context)
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

                    // How many players are ranked in total, which is what
                    // decides whether a tier pays anything at all - see the
                    // population floor in LeaderboardTierRegistry. Read here so
                    // the board can show an HONEST reward next to each rank
                    // instead of a headline figure the payout would refuse.
                    long rankedPopulation = await dbRedis.SortedSetLengthAsync("leaderboard:mastery");

                    var playerIds = redisEntries.Select(e => (long)e.Element).ToList();
                    
                    var players = await db.PlayerRecords
                        .AsNoTracking()
                        .Where(p => playerIds.Contains(p.Id))
                        .ToDictionaryAsync(p => p.Id);

                    // Modul: THE BOARD RANKS BY PROGRESS, so it has to SHOW
                    // progress. The rank order is level, then the hardest
                    // monster ever put down, then kills of it - and a board
                    // that sorts by something it does not display is a board
                    // whose order looks arbitrary.
                    //
                    // Read back out of the composite score rather than
                    // re-queried: the score IS the ranking inputs packed
                    // together (LeaderboardCronEngine.CompositeScore), so
                    // unpacking it cannot disagree with the order.
                    var progressByPlayer = new System.Collections.Generic.Dictionary<long, (int Hardest, int Kills)>();
                    foreach (var entry in redisEntries)
                    {
                        long packed = (long)entry.Score;
                        progressByPlayer[(long)entry.Element] = (
                            (int)((packed / 1_000_000L) % 10_000L),
                            (int)(packed % 1_000_000L));
                    }

                    for (int i = 0; i < redisEntries.Length; i++)
                    {
                        long pId = (long)redisEntries[i].Element;
                        if (players.TryGetValue(pId, out var p))
                        {
                            entries.Add(new LeaderboardEntryResponse
                            {
                                Rank = skip + i + 1,
                                PlayerId = p.Id,

                                // Modul: leaderboard names, 2026-08-01. Was the
                                // literal string "Player" for every row, so the
                                // entire global leaderboard read as fifty
                                // identical entries and could not tell anyone
                                // apart - while PlayerRecords."Username" sat on
                                // the very record already loaded into this
                                // dictionary two lines up.
                                //
                                // Username is nullable (accounts created before
                                // it existed), so it falls back to the id rather
                                // than rendering an empty row.
                                DisplayName = string.IsNullOrWhiteSpace(p.Username) ? $"Player #{p.Id}" : p.Username!,
                                Level = p.CurrentLevel,
                                Xp = p.CurrentXp,
                                HardestMonsterId = progressByPlayer.TryGetValue(p.Id, out var progress) ? progress.Hardest : 0,
                                HardestMonsterName = progressByPlayer.TryGetValue(p.Id, out var named) && named.Hardest > 0
                                    ? ContentRegistry.GetMonsterName(named.Hardest)
                                    : string.Empty,
                                KillsOfHardest = progressByPlayer.TryGetValue(p.Id, out var killed) ? killed.Kills : 0,

                                // Resolved from the ONE table that also decides
                                // the payout, so the colour a player sees and
                                // the diamonds they receive come from the same
                                // place. WeeklyDiamonds is what this rank earns
                                // AT THE CURRENT POPULATION - so on a small
                                // server it honestly reads 0 rather than
                                // advertising a prize the floor will refuse.
                                TierId = LeaderboardTierRegistry.TierIdForRank(skip + i + 1),
                                TierName = LeaderboardTierRegistry.TierForRank(skip + i + 1)?.Name ?? string.Empty,
                                WeeklyDiamonds = LeaderboardTierRegistry.WeeklyDiamondsFor(skip + i + 1, (int)rankedPopulation)
                            });
                        }
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (StackExchange.Redis.RedisException ex)
            {
                // Redis is an optional dependency everywhere else in this
                // server (Program.cs: "session locking, write-behind, and
                // telemetry streaming simply no-op" without one) - the
                // leaderboard ZSET is exactly that kind of write-behind
                // cache, so an unreachable Redis should degrade to "no
                // ranked data yet", not a 500 that looks like a real server
                // bug to the client.
                Console.WriteLine($"Leaderboard unavailable (Redis): {ex.Message}");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new System.Collections.Generic.List<LeaderboardEntryResponse>());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Leaderboard error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: Comprehensive Game System Audit, Part 3.2. Global guild
        // leaderboard read endpoint - mirrors HandleGlobalLeaderboard's
        // exact shape (authenticated GET, skip/take pagination through
        // the same ValidateLeaderboardQuery bounds, Redis ZSET populated
        // by LeaderboardCronEngine.SyncGuildLeaderboardAsync, DB hydration
        // of display fields).
        private async Task HandleGuildLeaderboard(HttpListenerContext context)
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
                var redisEntries = await dbRedis.SortedSetRangeByRankWithScoresAsync("leaderboard:guilds", skip, skip + take - 1, StackExchange.Redis.Order.Descending);

                var guildIds = redisEntries.Select(e => (long)e.Element).ToList();

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var guilds = await db.GuildRecords
                    .AsNoTracking()
                    .Where(g => guildIds.Contains(g.Id))
                    .ToDictionaryAsync(g => g.Id);

                var entries = new System.Collections.Generic.List<GuildLeaderboardEntryResponse>(redisEntries.Length);
                for (int i = 0; i < redisEntries.Length; i++)
                {
                    long gId = (long)redisEntries[i].Element;
                    if (guilds.TryGetValue(gId, out var g))
                    {
                        entries.Add(new GuildLeaderboardEntryResponse
                        {
                            Rank = skip + i + 1,
                            GuildId = g.Id,
                            Name = g.Name,
                            GuildTier = g.CurrentTier,
                            GuildMMR = g.GuildMMR
                        });
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (StackExchange.Redis.RedisException ex)
            {
                // See HandleGlobalLeaderboard's matching catch - Redis is an
                // optional write-behind cache everywhere else in this
                // server, so being unreachable should degrade to "no ranked
                // guild data yet", not a 500.
                Console.WriteLine($"Guild leaderboard unavailable (Redis): {ex.Message}");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new System.Collections.Generic.List<GuildLeaderboardEntryResponse>());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild leaderboard error: {ex.Message}");
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

        private async Task HandleWorldBossBoard(HttpListenerContext context)
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
                var view = await FolkIdle.Server.Domain.Combat.WorldBossStrike.WorldBossBoard.ViewAsync(db, playerId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"World boss board error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleDeepestLeaderboard(HttpListenerContext context)
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
                var rows = await FolkIdle.Server.Domain.Economy.DeepestBoard.TopAsync(db, GameClock.UtcNow);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Entries = rows });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Deepest board error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleBloodlineWeekBoard(HttpListenerContext context)
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
                var rows = await FolkIdle.Server.Domain.Progression.BloodlineWeekBoard.TopAsync(db, GameClock.UtcNowOffset);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Entries = rows });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bloodline week board error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleLifeRecordBoard(HttpListenerContext context)
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
                var rows = await FolkIdle.Server.Domain.Progression.LifeRecordBoard.TopAsync(db);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Entries = rows });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Life record board error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleStatsOnline(HttpListenerContext context)
        {
            try
            {
                int count = _connectedClients.Count;
                string json = "{\"OnlineCount\":" + count + "}";
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                // Modul: this answered 500 and logged nothing - the silent-failure
                // shape CLAUDE.md warns about. Say why, in the file's own style.
                Console.WriteLine($"Stats online failed: {ex.Message}");
                if (context != null && context.Response != null)
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
            }
        }
    }
}
