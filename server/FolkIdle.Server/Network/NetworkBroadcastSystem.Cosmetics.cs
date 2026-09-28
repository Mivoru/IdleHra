using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    // Task 54: the cosmetics endpoints. REST, not the wire - nothing here is
    // on StateUpdatePacket, and every POST runs under the router's per-account
    // stripe lock. A refusal answers 200 with its Result so the screen can say
    // why (the "silent rollback" rule), never a bare 4xx for a state race.
    public partial class NetworkBroadcastSystem
    {
        /// <summary>Routes /api/v1/cosmetics*; true when it answered.</summary>
        private async Task<bool> TryHandleCosmeticsAsync(HttpListenerContext context, string requestPath)
        {
            string method = context.Request.HttpMethod;

            if (requestPath.StartsWith("/api/v1/market/cosmetics", StringComparison.Ordinal))
            {
                if (requestPath == "/api/v1/market/cosmetics" && method == "GET") { await HandleCosmeticListings(context); return true; }
                if (method == "POST" && (requestPath == "/api/v1/market/cosmetics/list"
                    || requestPath == "/api/v1/market/cosmetics/buy"
                    || requestPath == "/api/v1/market/cosmetics/cancel"))
                {
                    await HandleCosmeticMarketAction(context, requestPath);
                    return true;
                }
                return false;
            }

            if (!requestPath.StartsWith("/api/v1/cosmetics", StringComparison.Ordinal)) return false;

            if (requestPath == "/api/v1/cosmetics/catalogue" && method == "GET") { await HandleCosmeticsCatalogue(context); return true; }
            if (requestPath == "/api/v1/cosmetics" && method == "GET") { await HandleCosmeticsView(context); return true; }
            if (requestPath == "/api/v1/cosmetics/open" && method == "POST") { await HandleCosmeticsOpen(context); return true; }
            if (requestPath == "/api/v1/cosmetics/equip" && method == "POST") { await HandleCosmeticsEquip(context); return true; }
            if (requestPath == "/api/v1/cosmetics/worn" && method == "GET") { await HandleCosmeticsWorn(context); return true; }
            return false;
        }

        private static async Task WriteJsonAsync(HttpListenerContext context, object value)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, value);
        }

        private async Task HandleCosmeticsCatalogue(HttpListenerContext context)
        {
            try
            {
                // Public content, like /gamedata: no player state in it.
                await WriteJsonAsync(context, new
                {
                    RarityNames = CosmeticRegistry.RarityNames,
                    Items = CosmeticRegistry.All.Select(d => new { d.Id, Kind = (byte)d.Kind, d.Rarity, d.Name, d.Art }),
                    ChestChancePerKill = Enumerable.Range(0, CosmeticRegistry.MaxRarity + 1)
                        .Select(r => r == 0 ? 0.0 : CosmeticRegistry.ChestChancePerKill(r)),
                    CosmeticRegistry.LevelsPerChest,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cosmetics catalogue error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleCosmeticsView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await WriteJsonAsync(context, await CosmeticEngine.ViewAsync(db, playerId));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cosmetics view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>{ "Rarity": 1-4 } - opens one chest of that rarity.</summary>
        private async Task HandleCosmeticsOpen(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                int rarity;
                try
                {
                    using var parsed = JsonDocument.Parse(await ReadBodyAsync(context));
                    if (!parsed.RootElement.TryGetProperty("Rarity", out var el) || !el.TryGetInt32(out rarity))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var (result, opened) = await CosmeticEngine.OpenAsync(db, playerId, rarity, Random.Shared, DateTime.UtcNow);
                await WriteJsonAsync(context, await CosmeticEngine.ViewAsync(db, playerId, result.ToString(), opened));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cosmetics open error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>{ "Kind": 1 avatar | 2 frame, "Id": definition id or null for the default }.</summary>
        private async Task HandleCosmeticsEquip(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                int kind;
                string? id;
                try
                {
                    using var parsed = JsonDocument.Parse(await ReadBodyAsync(context));
                    var root = parsed.RootElement;
                    if (!root.TryGetProperty("Kind", out var kindEl) || !kindEl.TryGetInt32(out kind)
                        || !root.TryGetProperty("Id", out var idEl))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }
                    if (idEl.ValueKind == JsonValueKind.Null) id = null;
                    else if (idEl.ValueKind == JsonValueKind.String) id = idEl.GetString();
                    else { context.Response.StatusCode = 400; return; }
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var result = await CosmeticEngine.EquipAsync(db, playerId, (CosmeticKind)kind, id);
                await WriteJsonAsync(context, await CosmeticEngine.ViewAsync(db, playerId, result.ToString()));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cosmetics equip error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// ?ids=1,2,3 - what each of those players wears, for chat. Capped so a
        /// crafted query cannot ask for the whole table.
        /// </summary>
        private async Task HandleCosmeticsWorn(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                var ids = (query["ids"] ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => long.TryParse(s, out long v) ? v : 0L)
                    .Where(v => v > 0)
                    .Distinct()
                    .Take(MaxWornLookup)
                    .ToList();

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await WriteJsonAsync(context, new { Players = await CosmeticEngine.WornAsync(db, ids) });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cosmetics worn error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private const int MaxWornLookup = 100;

        /// <summary>?kind=0|1|2&amp;rarity=1-4 - the cheapest listings, plus all of mine.</summary>
        private async Task HandleCosmeticListings(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                int? kind = int.TryParse(query["kind"], out int k) ? k : null;
                int? rarity = int.TryParse(query["rarity"], out int r) ? r : null;

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await WriteJsonAsync(context, new
                {
                    Listings = await FolkIdle.Server.Domain.Economy.CosmeticMarketEngine.ListingsAsync(db, playerId, kind, rarity),
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cosmetic listings error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// list { CosmeticItemId, Price } / buy { ListingId } / cancel { ListingId }.
        /// Answers 200 with the Result and the caller's refreshed cosmetics, so
        /// the screen can say why a refusal happened.
        /// </summary>
        private async Task HandleCosmeticMarketAction(HttpListenerContext context, string requestPath)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                long id, price = 0;
                try
                {
                    using var parsed = JsonDocument.Parse(await ReadBodyAsync(context));
                    var root = parsed.RootElement;
                    string idField = requestPath.EndsWith("/list", StringComparison.Ordinal) ? "CosmeticItemId" : "ListingId";
                    if (!root.TryGetProperty(idField, out var idEl) || !idEl.TryGetInt64(out id))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }
                    if (idField == "CosmeticItemId"
                        && (!root.TryGetProperty("Price", out var priceEl) || !priceEl.TryGetInt64(out price)))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                FolkIdle.Server.Domain.Economy.CosmeticMarketResult result;

                if (requestPath.EndsWith("/list", StringComparison.Ordinal))
                {
                    result = await FolkIdle.Server.Domain.Economy.CosmeticMarketEngine.ListAsync(db, playerId, id, price, DateTime.UtcNow);
                }
                else if (requestPath.EndsWith("/cancel", StringComparison.Ordinal))
                {
                    result = await FolkIdle.Server.Domain.Economy.CosmeticMarketEngine.CancelAsync(db, playerId, id);
                }
                else
                {
                    (result, _) = await FolkIdle.Server.Domain.Economy.CosmeticMarketEngine.BuyAsync(
                        db, _playerSessionRegistry, playerId, id, DateTime.UtcNow);

                    // The buyer's gold moved in the database, out of band from
                    // the live payload - the Delve's reason for ReloadState.
                    if (result == FolkIdle.Server.Domain.Economy.CosmeticMarketResult.Ok)
                    {
                        CommandQueue.Enqueue(new PlayerCommand
                        {
                            PlayerId = playerId,
                            Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                        });
                    }
                }

                var view = await CosmeticEngine.ViewAsync(db, playerId);
                await WriteJsonAsync(context, new { Result = result.ToString(), Cosmetics = view });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cosmetic market error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// Dev tool: { "Rarity": 1-4 } puts one chest in the caller's hands
        /// through the real insert, so exercise.mjs can open one on every run
        /// instead of after thousands of kills.
        /// </summary>
        private async Task HandleDevCosmeticChest(HttpListenerContext context, long playerId)
        {
            int rarity = CosmeticRegistry.Common;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("Rarity", out var el) && el.TryGetInt32(out int r)) rarity = r;
            }
            catch (JsonException) { }

            if (rarity < CosmeticRegistry.Common || rarity > CosmeticRegistry.MaxRarity)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await CosmeticEngine.InsertChestsAsync(db, playerId, new[] { rarity }, CosmeticSource.Dev, DateTime.UtcNow);
            await WriteJsonAsync(context, await CosmeticEngine.ViewAsync(db, playerId));
        }
    }
}
