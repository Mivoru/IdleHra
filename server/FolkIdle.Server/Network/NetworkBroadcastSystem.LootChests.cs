using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    public partial class NetworkBroadcastSystem
    {
        // Loot chests (CHESTS_DUNGEONS_DIAMONDS.md section 1). REST, like the
        // Workshop and the Delve: a handful of requests, the database is the one
        // authority, nothing is added to StateUpdatePacket.
        private async Task<bool> TryHandleLootChestsAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/chests", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;
            if (requestPath == "/api/v1/chests" && method == "GET") { await HandleLootChestView(context); return true; }
            if (requestPath == "/api/v1/chests/open" && method == "POST") { await HandleLootChestOpen(context); return true; }
            return false;
        }

        private sealed record LootChestShelfRow(int Tier, string Name, int Count, string[] Odds);

        private async Task<LootChestShelfRow[]> ShelfAsync(FolkIdleDbContext db, long playerId)
        {
            var counts = await LootChestEngine.CountsAsync(db, playerId);
            bool tickets = await ChestsPayDungeonTicketsAsync(db, playerId);
            return Enum.GetValues<LootChestTier>()
                .Select(t => new LootChestShelfRow((int)t, t.ToString(), counts.GetValueOrDefault(t), LootChestRules.OddsLines(t, tickets).ToArray()))
                .ToArray();
        }

        // Modul: chests pay dungeon tickets only to someone who can see the
        // dungeons - while they are admin-only, a ticket for a gate nobody can
        // open would be a reward line that means nothing.
        private async Task<bool> ChestsPayDungeonTicketsAsync(FolkIdleDbContext db, long playerId)
            => MaySeeDungeon(await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId));

        private async Task HandleLootChestView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await WriteJsonAsync(context, new { Chests = await ShelfAsync(db, playerId), LootChestEngine.MaxDiamondsPerWeek });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Loot chest view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>POST /api/v1/chests/open { "Tier": 2, "Count": 5 } - opens up to Count of that tier.</summary>
        private async Task HandleLootChestOpen(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                int tier = 0, count = 1;
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (parsed.RootElement.TryGetProperty("Tier", out var t) && t.TryGetInt32(out int tv)) tier = tv;
                    if (parsed.RootElement.TryGetProperty("Count", out var c) && c.TryGetInt32(out int cv)) count = cv;
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                if (tier < 1 || tier > 4) { context.Response.StatusCode = 400; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                bool tickets = await ChestsPayDungeonTicketsAsync(db, playerId);
                var opened = await LootChestEngine.OpenAsync(db, playerId, (LootChestTier)tier, count, DateTime.UtcNow, Random.Shared, tickets);
                db.ChangeTracker.Clear();

                await WriteJsonAsync(context, new
                {
                    Result = opened.Count == 0 ? "NothingToOpen" : "Ok",
                    Opened = opened.Select(o => new
                    {
                        Tier = (int)o.Tier,
                        o.Region,
                        Materials = o.Roll.Materials.Select(m => new { m.ItemId, m.Quantity }).ToArray(),
                        Gold = o.Roll.Gold + o.Roll.GoldInsteadOfDiamonds,
                        o.Roll.Diamonds,
                        Piece = o.Piece,
                        o.DungeonTicket,
                    }).ToArray(),
                    Chests = await ShelfAsync(db, playerId),
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Loot chest open error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>Dev only: POST /api/v1/dev/chests/grant { "Tier": 2, "Region": 1 } - for exercise.mjs.</summary>
        private async Task HandleDevLootChestGrant(HttpListenerContext context, long playerId)
        {
            int tier = 1, region = 1;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("Tier", out var t) && t.TryGetInt32(out int tv)) tier = tv;
                if (parsed.RootElement.TryGetProperty("Region", out var r) && r.TryGetInt32(out int rv)) region = rv;
            }
            catch (JsonException) { }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            long id = await LootChestEngine.GrantAsync(db, playerId, (LootChestTier)Math.Clamp(tier, 1, 4), region, LootChestSource.Dev, DateTime.UtcNow);
            await WriteJsonAsync(context, new { Id = id });
        }

        // POST /api/v1/dev/materials/grant {BaseId, Quantity}. Dev only, like
        // every /dev/ route; answers with the stack after the change.
        private async Task HandleDevMaterialGrant(HttpListenerContext context, long playerId)
        {
            string baseId = string.Empty;
            long quantity = 0;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("BaseId", out var b)) baseId = b.GetString() ?? string.Empty;
                if (parsed.RootElement.TryGetProperty("Quantity", out var q) && q.TryGetInt64(out long qv)) quantity = qv;
            }
            catch (JsonException) { }
            if (!ContentRegistry.TryGetItemDefinitionByBaseId(baseId, out _) || quantity == 0)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            long have = await db.CommodityRecords.Where(c => c.PlayerId == playerId && c.ItemId == baseId).Select(c => c.Quantity).FirstOrDefaultAsync();
            long delta = Math.Max(quantity, -have);
            long after = delta == 0 ? have : await CommodityLedger.AddAsync(db, playerId, baseId, delta);
            await WriteJsonAsync(context, new { Quantity = after });
        }
    }
}
