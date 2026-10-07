using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    // The quest line (owner, 2026-10-07). REST, not the wire: nothing here is on
    // StateUpdatePacket and no opcode is spent. A claim runs under the router's
    // per-account stripe lock like every other POST, and QuestLineEngine's own
    // SQL guard makes a double claim pay once even without it. A refusal answers
    // 200 with the whole view and its Result, so the panel can say why and redraw
    // from the server's truth in one round trip - never a bare 4xx for a state
    // race (the "silent rollback" rule).
    public partial class NetworkBroadcastSystem
    {
        /// <summary>Routes /api/v1/quests*; true when it answered.</summary>
        private async Task<bool> TryHandleQuestLineAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/quests", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;

            if (requestPath == "/api/v1/quests" && method == "GET") { await HandleQuestLineView(context); return true; }

            const string prefix = "/api/v1/quests/";
            const string suffix = "/claim";
            if (method == "POST" && requestPath.StartsWith(prefix, StringComparison.Ordinal)
                && requestPath.EndsWith(suffix, StringComparison.Ordinal)
                && requestPath.Length > prefix.Length + suffix.Length)
            {
                string stepId = requestPath.Substring(prefix.Length, requestPath.Length - prefix.Length - suffix.Length);
                await HandleQuestLineClaim(context, stepId);
                return true;
            }
            return false;
        }

        private async Task HandleQuestLineView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await QuestLineEngine.ViewAsync(db, playerId);
                if (view == null) { context.Response.StatusCode = 404; return; }
                await WriteJsonAsync(context, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Quest line view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleQuestLineClaim(HttpListenerContext context, string stepId)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var result = await QuestLineEngine.ClaimAsync(db, playerId, stepId);

                // Modul: gold and materials moved in the database, out of band
                // from the live payload, so the session has to be told or the
                // header keeps the old balance until the next sign-in. Same
                // answer every off-tick engine here uses (see HandleDelveAction).
                if (result == QuestClaimResult.Ok)
                {
                    CommandQueue.Enqueue(new PlayerCommand
                    {
                        PlayerId = playerId,
                        Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                    });
                }

                db.ChangeTracker.Clear();
                var view = await QuestLineEngine.ViewAsync(db, playerId, result.ToString());
                if (view == null) { context.Response.StatusCode = 404; return; }
                await WriteJsonAsync(context, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Quest line claim error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// Dev only: POST /api/v1/dev/quests/unclaim { "StepId": "fuse" } - takes
        /// back what the claim paid, so exercise.mjs can claim on the fixture
        /// and leave it as it found it.
        /// </summary>
        private async Task HandleDevQuestUnclaim(HttpListenerContext context, long playerId)
        {
            string stepId = string.Empty;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("StepId", out var s) && s.ValueKind == JsonValueKind.String) stepId = s.GetString() ?? string.Empty;
            }
            catch (JsonException) { }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            bool undone = await QuestLineEngine.DevUnclaimAsync(db, playerId, stepId);
            if (undone)
            {
                CommandQueue.Enqueue(new PlayerCommand
                {
                    PlayerId = playerId,
                    Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                });
            }
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Unclaimed = undone });
        }
    }
}
