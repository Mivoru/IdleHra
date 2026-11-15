using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Network
{
    // Modul: DAILY MISSIONS' THREE ROUTES (owner, 2026-11-15; step 2 of
    // docs/design/CHRONICLE_PASS.md). GET draws today on its first call and
    // writes the row, so - unlike most GETs here - it mutates; the insert is
    // ON CONFLICT DO NOTHING, which makes two concurrent first reads harmless
    // without the router's account lock. The rules live in MissionEngine.
    public partial class NetworkBroadcastSystem
    {
        private async Task<bool> TryHandleMissionsAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/missions", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;
            if (requestPath == "/api/v1/missions" && method == "GET") { await HandleMissions(context, "view"); return true; }
            if (requestPath == "/api/v1/missions/claim" && method == "POST") { await HandleMissions(context, "claim"); return true; }
            if (requestPath == "/api/v1/missions/reroll" && method == "POST") { await HandleMissions(context, "reroll"); return true; }
            return false;
        }

        private async Task HandleMissions(HttpListenerContext context, string action)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                int slot = -1;
                if (action == "reroll")
                {
                    try
                    {
                        string body = await ReadBodyAsync(context);
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        if (!parsed.RootElement.TryGetProperty("Slot", out var s) || !s.TryGetInt32(out slot))
                        {
                            context.Response.StatusCode = 400;
                            return;
                        }
                    }
                    catch (JsonException) { context.Response.StatusCode = 400; return; }
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                bool deepEnabled = _serviceProvider.GetService<DelveDeepSettings>()?.Enabled ?? false;
                bool worldBossActive = _worldBossEngine?.IsEventActive ?? false;
                var utcNow = GameClock.UtcNow;
                Func<Task<MissionFacts?>> facts = () => MissionEngine.LoadFactsAsync(db, playerId, deepEnabled, worldBossActive);

                object? answer;
                if (action == "view")
                {
                    answer = await MissionEngine.ViewAsync(db, playerId, facts, utcNow);
                }
                else if (action == "claim")
                {
                    var (outcome, xp, view) = await MissionEngine.ClaimAsync(db, playerId, facts, utcNow);
                    answer = view == null ? null : new { Outcome = outcome.ToString(), XpGained = xp, Day = view };
                }
                else
                {
                    var (outcome, view) = await MissionEngine.RerollAsync(db, playerId, slot, facts, utcNow);
                    answer = view == null ? null : new { Outcome = outcome.ToString(), Day = view };
                }

                if (answer == null) { context.Response.StatusCode = 404; return; }
                await WriteJsonAsync(context, answer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Missions {action} error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}
