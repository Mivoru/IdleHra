using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    // Expeditions (2026-11-13): /api/v1/expeditions* - see ExpeditionRules and
    // ExpeditionEngine. REST like the Delve and the loot chests: nothing here
    // touches the tick's payload (a benched ancestor is not on it), and the
    // chest a claim stages is opened on the Chest screen as any other.
    public partial class NetworkBroadcastSystem
    {
        private async Task<bool> TryHandleExpeditionsAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/expeditions", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return true; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                long now = GameClock.UtcNowOffset.ToUnixTimeSeconds();

                if (requestPath == "/api/v1/expeditions" && method == "GET")
                {
                    await WriteJsonAsync(context, await ExpeditionEngine.BoardAsync(db, playerId, now));
                    return true;
                }

                if (requestPath == "/api/v1/expeditions/start" && method == "POST")
                {
                    Guid characterId;
                    int hours;
                    try
                    {
                        string body = await ReadBodyAsync(context);
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        var root = parsed.RootElement;
                        hours = IntOf(root, "Hours");
                        string? id = root.TryGetProperty("CharacterId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                        if (!Guid.TryParse(id, out characterId)) { context.Response.StatusCode = 400; return true; }
                    }
                    catch (JsonException) { context.Response.StatusCode = 400; return true; }

                    var result = await ExpeditionEngine.StartAsync(db, playerId, characterId, hours, now);
                    await WriteJsonAsync(context, new { Result = result.ToString() });
                    return true;
                }

                if (requestPath == "/api/v1/expeditions/claim" && method == "POST")
                {
                    long id;
                    try
                    {
                        string body = await ReadBodyAsync(context);
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        id = parsed.RootElement.TryGetProperty("Id", out var e) && e.TryGetInt64(out long v) ? v : 0;
                    }
                    catch (JsonException) { context.Response.StatusCode = 400; return true; }

                    var claim = await ExpeditionEngine.ClaimAsync(db, playerId, id, now, GameClock.UtcNow, Random.Shared);
                    await WriteJsonAsync(context, claim);
                    return true;
                }

                if (requestPath == "/api/v1/expeditions/buy-slot" && method == "POST")
                {
                    var (result, price) = await ExpeditionEngine.BuySlotAsync(db, playerId);
                    if (result == ExpeditionSlotPurchase.Bought)
                    {
                        // The gold left the database; the live wallet follows on reload.
                        CommandQueue.Enqueue(new PlayerCommand { PlayerId = playerId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                    }
                    await WriteJsonAsync(context, new { Result = result.ToString(), Price = price });
                    return true;
                }

                if (requestPath == "/api/v1/expeditions/dev/return" && method == "POST")
                {
                    if (!DevToolsEnabled()) { context.Response.StatusCode = 404; return true; }
                    int n = await ExpeditionEngine.DevReturnAllAsync(db, playerId, now);
                    await WriteJsonAsync(context, new { Returned = n });
                    return true;
                }

                context.Response.StatusCode = 404;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Expeditions error: {ex}");
                context.Response.StatusCode = 500;
                return true;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}
