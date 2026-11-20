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
        // Modul: task 102, "My orders". It was read-only while the book had no
        // cancel for equipment; POST /api/v1/market/cancel (below) is that
        // cancel now, and each row here carries the OrderId it takes.
        private async Task HandleMarketOwnOrders(HttpListenerContext context)
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
                var orders = await MarketOrderBookEngine.FetchOwnOpenOrdersAsync(db, playerId);

                var rows = new System.Collections.Generic.List<MarketOwnOrderResponse>(orders.Count);
                foreach (var order in orders)
                {
                    rows.Add(new MarketOwnOrderResponse
                    {
                        OrderId = order.Id,
                        OrderType = order.OrderType,
                        BaseItemId = order.BaseItemId,
                        QualityTier = order.QualityTier,
                        Price = order.Price,
                        CreatedAtEpoch = order.CreatedAtEpoch,
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, rows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Market own orders error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: CANCEL ONE OF MY ORDERS. REST, like the cosmetic market's
        // cancel and the My orders read beside it - not a wire command,
        // because nothing it changes is tick state: the piece and the gold
        // both live in rows, so there is nothing to flush first. A refusal
        // answers 200 with its Result (the "silent rollback" rule); only a
        // malformed body is a 400. On success the session reloads, which
        // flushes and then reads the refunded gold row back - the
        // MarketMatchQueue display credit is NOT used as well, or the
        // header would show a refund twice.
        private async Task HandleMarketCancelOrder(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                long orderId;
                try
                {
                    using var parsed = JsonDocument.Parse(await ReadBodyAsync(context));
                    if (!parsed.RootElement.TryGetProperty("OrderId", out var idEl)
                        || !idEl.TryGetInt64(out orderId)
                        || orderId <= 0)
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
                var outcome = await FolkIdle.Server.Domain.Economy.MarketEscrowEngine.CancelOrderAsync(db, playerId, orderId);

                if (outcome.Result == FolkIdle.Server.Domain.Economy.MarketCancelResult.Ok)
                {
                    CommandQueue.Enqueue(new PlayerCommand
                    {
                        PlayerId = playerId,
                        Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
                {
                    Result = outcome.Result.ToString(),
                    outcome.ReturnedEquipmentId,
                    outcome.RefundedGold,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Market cancel error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        // Modul 40: marketplace browser page. Uses the authenticated HTTP
        // snapshot pattern established by HandleForgeInventorySnapshot /
        // HandleGuildLogisticsSnapshot / HandleGlobalLeaderboard for
        // variable-length, on-demand player data rather than a fixed-layout
        // WebSocket packet - a paginated result set has no natural fixed
        // size, so it does not fit StateUpdatePacket's binary layout the way
        // scalar per-tick fields do.
        /// <summary>
        /// Parses "1,3,5" into a bounded set, ignoring anything out of range or
        /// unparseable rather than rejecting the whole request - a stale client
        /// sending a slot index that no longer exists (the offhand was 6) should
        /// see an unfiltered market, not an error.
        /// </summary>
        private static System.Collections.Generic.HashSet<int> ParseIdSet(string? raw, int min, int max)
        {
            var parsed = new System.Collections.Generic.HashSet<int>();
            if (string.IsNullOrWhiteSpace(raw)) return parsed;

            foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out int value) && value >= min && value <= max)
                {
                    parsed.Add(value);
                }
            }

            return parsed;
        }

        /// <summary>
        /// Price history for one item at one rarity.
        ///
        /// Reads historical_market_archives, which has recorded every completed
        /// trade with BaseItemId, QualityTier, ExecutionPrice and a millisecond
        /// timestamp since the market shipped - so this is retroactively
        /// correct for trades that already happened, and needs no new table.
        /// (The handoff said no completed trade was recorded anywhere; the
        /// archive is written by both MarketEscrowEngine and
        /// MarketOrderBookEngine.)
        /// </summary>
        private async Task HandleMarketPriceHistory(HttpListenerContext context)
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
                string baseItemId = query["baseItemId"] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(baseItemId) || baseItemId.Length > 255)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (!int.TryParse(query["qualityTier"], out int qualityTier))
                {
                    qualityTier = -1; // every rarity
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // Milliseconds - ExecutionTimestampEpoch is written with
                // ToUnixTimeMilliseconds, and comparing it against seconds
                // would silently place every trade in the future.
                long nowMs = GameClock.UtcNowOffset.ToUnixTimeMilliseconds();
                const long DayMs = 86_400_000L;
                long windowStartMs = nowMs - (DayMs * 30L);

                var trades = await db.HistoricalMarketArchives
                    .AsNoTracking()
                    .Where(a => a.BaseItemId == baseItemId
                        && (qualityTier < 0 || a.QualityTier == qualityTier)
                        && a.ExecutionPrice > 0
                        && a.ExecutionTimestampEpoch >= windowStartMs)
                    .OrderBy(a => a.ExecutionTimestampEpoch)
                    .Select(a => new MarketPricePointResponse { Epoch = a.ExecutionTimestampEpoch, Price = a.ExecutionPrice })
                    .ToListAsync();

                var response = new MarketPriceHistoryResponse
                {
                    BaseItemId = baseItemId,
                    QualityTier = qualityTier,
                    Points = trades,
                    TradeCount = trades.Count
                };

                if (trades.Count > 0)
                {
                    long sum = 0L;
                    long low = long.MaxValue;
                    long high = long.MinValue;
                    for (int i = 0; i < trades.Count; i++)
                    {
                        long price = trades[i].Price;
                        sum += price;
                        if (price < low) low = price;
                        if (price > high) high = price;
                    }

                    response.LastPrice = trades[^1].Price;
                    response.AveragePrice = sum / trades.Count;
                    response.LowPrice = low;
                    response.HighPrice = high;

                    response.ChangeDayPct = await ComputeChangePctAsync(db, baseItemId, qualityTier, response.LastPrice, nowMs - DayMs);
                    response.ChangeWeekPct = await ComputeChangePctAsync(db, baseItemId, qualityTier, response.LastPrice, nowMs - (DayMs * 7L));
                    response.ChangeMonthPct = await ComputeChangePctAsync(db, baseItemId, qualityTier, response.LastPrice, nowMs - (DayMs * 30L));
                }

                // The seller's own numbers. Mirrors MarketEscrowEngine's
                // brackets exactly - if those move, this must move with them.
                long sellerWealth = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                    .Select(c => c.Quantity)
                    .FirstOrDefaultAsync();

                response.FeePct = sellerWealth > 5_000_000 ? 15 : sellerWealth >= 500_000 ? 8 : 5;

                long guildId = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.GuildId)
                    .FirstOrDefaultAsync();

                if (guildId > 0)
                {
                    int taxRatePct = await db.GuildRecords
                        .AsNoTracking()
                        .Where(g => g.Id == guildId)
                        .Select(g => g.TaxRatePct)
                        .FirstOrDefaultAsync();

                    response.GuildTaxPct = Math.Clamp(taxRatePct, GuildRecord.MinTaxRatePct, GuildRecord.MaxTaxRatePct);
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Market price history error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Change from the last trade before <paramref name="sinceMs"/> to now.
        ///
        /// Null rather than zero when nothing traded before the window opened.
        /// A market with three days of history has no honest month-over-month
        /// figure, and "0%" would claim it does - that is the difference
        /// between "unchanged" and "unknown", and a price chart that confuses
        /// them is worse than one that omits the number.
        /// </summary>
        private static async Task<double?> ComputeChangePctAsync(FolkIdleDbContext db, string baseItemId, int qualityTier, long lastPrice, long sinceMs)
        {
            long baseline = await db.HistoricalMarketArchives
                .AsNoTracking()
                .Where(a => a.BaseItemId == baseItemId
                    && (qualityTier < 0 || a.QualityTier == qualityTier)
                    && a.ExecutionPrice > 0
                    && a.ExecutionTimestampEpoch < sinceMs)
                .OrderByDescending(a => a.ExecutionTimestampEpoch)
                .Select(a => a.ExecutionPrice)
                .FirstOrDefaultAsync();

            if (baseline <= 0L) return null;

            return (lastPrice - baseline) * 100.0 / baseline;
        }

        private async Task HandleMarketBrowserListings(HttpListenerContext context)
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
                string baseItemId = query["baseItemId"] ?? string.Empty;
                int.TryParse(query["pageIndex"], out int pageIndex);
                if (!int.TryParse(query["pageSize"], out int pageSize))
                {
                    pageSize = 24;
                }

                // Modul: EVERY FILTER IS OPTIONAL NOW.
                //
                // This endpoint used to 400 without an exact BaseItemId, and
                // matched QualityTier exactly as well - so the only question a
                // player could ask was "is this precise item at this precise
                // rarity for sale", which nobody can ask about a marketplace
                // they have not seen. No filters at all is the default and it
                // returns the whole book, paginated.
                // Modul: a comma-separated SET of slots and of region tiers,
                // because the filter is a row of checkboxes rather than a
                // dropdown - "helmets, chests and leggings" is one question, and
                // a single-value parameter made it three round trips.
                //
                // `slotIndex` (singular) is still accepted so an older client
                // keeps working; it simply becomes a set of one.
                var slotIndices = ParseIdSet(query["slotIndexes"] ?? query["slotIndex"], 0, EquipmentSlotEngine.SlotCount - 1);
                var regionTiers = ParseIdSet(query["tiers"], 1, ContentRegistry.LocationCount);

                if (!int.TryParse(query["minQualityTier"], out int minQualityTier))
                {
                    minQualityTier = 0;
                }

                if (!int.TryParse(query["maxQualityTier"], out int maxQualityTier))
                {
                    maxQualityTier = ForgeSplicingEngine.MaxQualityTier;
                }

                string sortBy = query["sortBy"] ?? "price";
                bool descending = query["descending"] == "1" || string.Equals(query["descending"], "true", StringComparison.OrdinalIgnoreCase);

                if (!ClientCommandValidator.ValidateMarketBrowserQuery(playerId, pageIndex, pageSize))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (minQualityTier < 0) minQualityTier = 0;
                if (maxQualityTier > ForgeSplicingEngine.MaxQualityTier) maxQualityTier = ForgeSplicingEngine.MaxQualityTier;
                if (maxQualityTier < minQualityTier) maxQualityTier = minQualityTier;
                if (sortBy != "price" && sortBy != "rarity" && sortBy != "name") sortBy = "price";

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                bool isQuarantined = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.IsQuarantined || p.Quarantine_Active)
                    .SingleOrDefaultAsync();

                var page = await MarketOrderBookEngine.BrowseActiveListingsAsync(db, new MarketOrderBookEngine.MarketBrowseQuery
                {
                    BaseItemId = baseItemId,
                    SlotIndices = slotIndices,
                    RegionTiers = regionTiers,
                    MinQualityTier = minQualityTier,
                    MaxQualityTier = maxQualityTier,
                    IsQuarantined = isQuarantined,
                    PageIndex = pageIndex,
                    PageSize = pageSize,
                    SortBy = sortBy,
                    Descending = descending,
                });

                var rows = new System.Collections.Generic.List<MarketListingResponse>(page.Listings.Count);
                for (int i = 0; i < page.Listings.Count; i++)
                {
                    rows.Add(new MarketListingResponse
                    {
                        OrderId = page.Listings[i].Id,
                        BaseItemId = page.Listings[i].BaseItemId,
                        QualityTier = page.Listings[i].QualityTier,
                        Price = page.Listings[i].Price,
                        CreatedAtEpoch = page.Listings[i].CreatedAtEpoch
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                // An envelope rather than a bare array: without TotalCount the
                // browser cannot draw a pager, and "did I reach the end" is not
                // answerable from a full page.
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new MarketBrowseResponse
                {
                    Listings = rows,
                    TotalCount = page.TotalCount,
                    PageIndex = pageIndex,
                    PageSize = pageSize,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Market browser listings error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }
    }
}
