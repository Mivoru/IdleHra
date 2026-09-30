using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    // Task 83: Workshop commissions. REST, not the wire, for the reasons
    // WorkshopCommissionEngine gives - nothing here is on StateUpdatePacket and
    // no opcode is spent. Every POST runs under the router's per-account stripe
    // lock (a double-tapped "Commission" waits for the first and is then
    // answered Busy). A refusal answers 200 with the whole view and its Result,
    // so the screen can say why and redraw from the server's truth in one
    // round trip - never a bare 4xx for a state race.
    public partial class NetworkBroadcastSystem
    {
        /// <summary>Routes /api/v1/workshop*; true when it answered.</summary>
        private async Task<bool> TryHandleWorkshopAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/workshop", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;

            if (requestPath == "/api/v1/workshop" && method == "GET") { await HandleWorkshopView(context); return true; }
            if (requestPath == "/api/v1/workshop/commission" && method == "POST") { await HandleWorkshopCommission(context); return true; }
            if (requestPath == "/api/v1/workshop/collect" && method == "POST") { await HandleWorkshopCollect(context); return true; }
            return false;
        }

        private static long NowEpoch() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private async Task HandleWorkshopView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await WorkshopCommissionEngine.ViewAsync(db, playerId, NowEpoch());
                if (view == null) { context.Response.StatusCode = 404; return; }
                await WriteJsonAsync(context, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Workshop view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>{ "ItemId": n, "AffixId": "crit_chance_pct" } - places a commission.</summary>
        /// <remarks>
        /// Modul: THE AFFIX TRAVELS AS ITS ID, NOT AS AN INDEX. Auto-reroll's
        /// "stop on stat" crosses the wire as a position in
        /// AffixRegistry.Definitions because ClientCommandPacket is
        /// fixed-layout, and ten of its twelve entries once drifted from the
        /// client's copy. A JSON body has no such constraint, so the string the
        /// server itself served in the view comes back unchanged.
        /// </remarks>
        private async Task HandleWorkshopCommission(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                int itemId = 0;
                string? affixId = null;
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (parsed.RootElement.TryGetProperty("ItemId", out var item) && item.TryGetInt32(out int iv)) itemId = iv;
                    if (parsed.RootElement.TryGetProperty("AffixId", out var affix) && affix.ValueKind == JsonValueKind.String) affixId = affix.GetString();
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                long now = NowEpoch();
                var result = await WorkshopCommissionEngine.StartAsync(db, playerId, itemId, affixId, now);

                db.ChangeTracker.Clear();
                var view = await WorkshopCommissionEngine.ViewAsync(db, playerId, now, result.ToString());
                if (view == null) { context.Response.StatusCode = 404; return; }
                await WriteJsonAsync(context, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Workshop commission error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleWorkshopCollect(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                long now = NowEpoch();
                var (result, piece) = await WorkshopCommissionEngine.CollectAsync(db, playerId, now, Random.Shared);

                db.ChangeTracker.Clear();
                var view = await WorkshopCommissionEngine.ViewAsync(db, playerId, now, result.ToString(), piece);
                if (view == null) { context.Response.StatusCode = 404; return; }
                await WriteJsonAsync(context, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Workshop collect error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// Dev only: POST /api/v1/dev/workshop/finish { "Refund": true } - the
        /// running commission is ready now, and with Refund its price is given
        /// back, so exercise.mjs can round-trip one on the fixture.
        /// </summary>
        private async Task HandleDevWorkshopFinish(HttpListenerContext context, long playerId)
        {
            bool refund = false;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("Refund", out var r) && r.ValueKind == JsonValueKind.True) refund = true;
            }
            catch (JsonException) { }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            long now = NowEpoch();
            var result = await WorkshopCommissionEngine.DevFinishAsync(db, playerId, now, refund);
            db.ChangeTracker.Clear();
            await WriteJsonAsync(context, (object?)await WorkshopCommissionEngine.ViewAsync(db, playerId, now, result.ToString()) ?? new { Result = result.ToString() });
        }
    }
}
