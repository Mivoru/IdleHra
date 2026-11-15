using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Network
{
    // Modul: THE CHRONICLE PASS'S TWO ROUTES (owner, 2026-11-15; step 3 of
    // docs/design/CHRONICLE_PASS.md). GET is read-only - the XP is summed from
    // the daily missions' banked days, never stored here. The rules live in
    // ChronicleRegistry and ChronicleEngine.
    public partial class NetworkBroadcastSystem
    {
        private async Task<bool> TryHandleChronicleAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/chronicle", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;
            if (requestPath == "/api/v1/chronicle" && method == "GET") { await HandleChronicleView(context); return true; }
            if (requestPath == "/api/v1/chronicle/claim" && method == "POST") { await HandleChronicleClaim(context); return true; }
            return false;
        }

        private async Task HandleChronicleView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await WriteJsonAsync(context, await ChronicleEngine.ViewAsync(db, playerId, GameClock.UtcNow));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chronicle view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        // POST /api/v1/chronicle/claim { Level? } - one level, or everything ready.
        private async Task HandleChronicleClaim(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                int? level = null;
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (parsed.RootElement.TryGetProperty("Level", out var l) && l.TryGetInt32(out int lv)) level = lv;
                }
                catch (JsonException) { context.Response.StatusCode = 400; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var utcNow = GameClock.UtcNow;
                var result = await ChronicleEngine.ClaimAsync(db, playerId, utcNow, level);

                if (result.Outcome == ChronicleEngine.ClaimOutcome.Ok)
                {
                    // Both balances moved in the DATABASE and the checkpoint
                    // writes both from the live payload - tell the session, as
                    // the login week does.
                    if (result.Gold > 0)
                    {
                        CommandQueue.Enqueue(new PlayerCommand
                        {
                            PlayerId = playerId,
                            Packet = new ClientCommandPacket { Command = CommandType.ReloadState },
                        });
                    }
                    if (result.Diamonds > 0)
                    {
                        _playerSessionRegistry?.BillingSyncQueue.Enqueue(new BillingSyncNotification
                        {
                            PlayerId = playerId,
                            PremiumDiamondsBalance = result.DiamondBalance,
                        });
                    }
                }

                db.ChangeTracker.Clear();
                await WriteJsonAsync(context, new
                {
                    Outcome = result.Outcome.ToString(),
                    result.LevelsClaimed,
                    result.Gold,
                    result.Diamonds,
                    result.Tickets,
                    Chests = result.Chests ?? Array.Empty<string>(),
                    Keepsakes = result.Keepsakes ?? Array.Empty<string>(),
                    result.OverflowChests,
                    Pass = await ChronicleEngine.ViewAsync(db, playerId, utcNow),
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chronicle claim error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}
