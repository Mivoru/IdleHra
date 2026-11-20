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
        // Modul: THE PROMPT MUST NOT COST WHAT IT WARNS ABOUT (2026-10-20). One
        // live account held 243,096 pieces, and the inventory download for it
        // was tens of megabytes that phones abandoned half way ("Unable to write
        // data to the transport connection"). Counting needs one GROUP BY over
        // two columns; the rows never leave the database.
        private async Task HandleChestSummary(HttpListenerContext context)
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

                var worn = await VillageChestEngine.LoadWornEquipmentIdsAsync(db, playerId);
                long[] wornIds = worn.Count == 0 ? new long[] { 0L } : worn.ToArray();

                int total = await db.EquipmentInstances.AsNoTracking().CountAsync(e => e.PlayerId == playerId);
                var groups = await db.EquipmentInstances.AsNoTracking()
                    .Where(e => e.PlayerId == playerId && !e.IsAffixLocked && e.VaultLockRegion == 0 && !wornIds.Contains(e.Id))
                    .GroupBy(e => new { e.BaseItemId, e.QualityTier })
                    .Select(g => new { g.Key.BaseItemId, g.Key.QualityTier, Count = g.Count() })
                    .ToListAsync();

                var rows = new System.Collections.Generic.Dictionary<(int, int), int>();
                foreach (var g in groups)
                {
                    var key = (ContentRegistry.GetRegionTierForBaseId(g.BaseItemId), g.QualityTier);
                    rows[key] = rows.GetValueOrDefault(key) + g.Count;
                }

                var response = new ChestSummaryResponse
                {
                    Total = total,
                    ApproxInventoryBytes = (long)total * ApproxInventoryBytesPerPiece,
                    MaxSweepableQualityTier = VillageChestEngine.MaxSweepableQualityTier,
                };
                foreach (var ((region, tier), count) in rows.OrderBy(r => r.Key.Item1).ThenBy(r => r.Key.Item2))
                {
                    response.Sweepable.Add(new ChestSummaryRow { Region = region, QualityTier = tier, Count = count });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chest summary error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Task 79: GET /api/v1/player/gold-ledger. Spending by category over 7
        /// days, 30 days and since recording began, read from gold_spend_daily
        /// (GoldLedger). Sorted by the all-time amount, biggest sink first.
        /// Phase 2 adds income by source (gold_income_daily) and the material
        /// flow (material_flow_daily), each with its own "recorded since".
        /// </summary>
        private async Task HandleGoldLedger(HttpListenerContext context)
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

                long lifetime = await db.PlayerRecords.AsNoTracking()
                    .Where(p => p.Id == playerId).Select(p => p.LifetimeGoldSpent).FirstOrDefaultAsync();
                var rows = await db.GoldSpendDaily.AsNoTracking().Where(g => g.PlayerId == playerId).ToListAsync();

                var today = DateOnly.FromDateTime(GameClock.UtcNow);
                var response = new GoldLedgerResponse
                {
                    LifetimeSpent = lifetime,
                    RecordedSince = rows.Count == 0 ? null : rows.Min(r => r.Day).ToString("yyyy-MM-dd"),
                    Categories = rows
                        .GroupBy(r => r.Category)
                        .Select(g => new GoldLedgerCategoryResponse
                        {
                            Category = Enum.IsDefined(typeof(GoldSpendCategory), g.Key) ? ((GoldSpendCategory)g.Key).ToString() : $"Other{g.Key}",
                            Last7Days = g.Where(r => r.Day > today.AddDays(-7)).Sum(r => r.Amount),
                            Last30Days = g.Where(r => r.Day > today.AddDays(-30)).Sum(r => r.Amount),
                            SinceRecorded = g.Sum(r => r.Amount),
                        })
                        .OrderByDescending(c => c.SinceRecorded)
                        .ToList(),
                };

                // Task 79, phase 2: income by source, and the material flow.
                var incomeRows = await db.GoldIncomeDaily.AsNoTracking().Where(g => g.PlayerId == playerId).ToListAsync();
                response.IncomeRecordedSince = incomeRows.Count == 0 ? null : incomeRows.Min(r => r.Day).ToString("yyyy-MM-dd");
                response.Income = incomeRows
                    .GroupBy(r => r.Source)
                    .Select(g => new GoldLedgerCategoryResponse
                    {
                        Category = Enum.IsDefined(typeof(GoldIncomeSource), g.Key) ? ((GoldIncomeSource)g.Key).ToString() : $"Other{g.Key}",
                        Last7Days = g.Where(r => r.Day > today.AddDays(-7)).Sum(r => r.Amount),
                        Last30Days = g.Where(r => r.Day > today.AddDays(-30)).Sum(r => r.Amount),
                        SinceRecorded = g.Sum(r => r.Amount),
                    })
                    .OrderByDescending(c => c.SinceRecorded)
                    .ToList();

                var materialRows = await db.MaterialFlowDaily.AsNoTracking().Where(m => m.PlayerId == playerId).ToListAsync();
                response.MaterialsRecordedSince = materialRows.Count == 0 ? null : materialRows.Min(r => r.Day).ToString("yyyy-MM-dd");
                response.Materials = materialRows
                    .GroupBy(r => (r.ItemId, r.Direction))
                    .Select(g => new MaterialFlowResponse
                    {
                        ItemId = g.Key.ItemId,
                        Direction = Enum.IsDefined(typeof(MaterialFlowDirection), g.Key.Direction) ? ((MaterialFlowDirection)g.Key.Direction).ToString() : $"Other{g.Key.Direction}",
                        Last7Days = g.Where(r => r.Day > today.AddDays(-7)).Sum(r => r.Amount),
                        Last30Days = g.Where(r => r.Day > today.AddDays(-30)).Sum(r => r.Amount),
                        SinceRecorded = g.Sum(r => r.Amount),
                    })
                    .OrderByDescending(m => m.SinceRecorded)
                    .ToList();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gold ledger error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Task 69: the plan a stack fusion would carry out now, from the same
        /// planner and the same fee the fusion uses (ForgeSplicingEngine
        /// .PlanStack / FusionFee), so the preview cannot quote one price and
        /// the anvil charge another. A GET, so no stripe lock - it writes
        /// nothing.
        /// </summary>
        private async Task HandleForgeStackPreview(HttpListenerContext context)
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

                if (!long.TryParse(context.Request.QueryString["item"], out long sampleId) || sampleId <= 0
                    || !int.TryParse(context.Request.QueryString["to"], out int toTier)
                    || toTier < 2 || toTier > ForgeSplicingEngine.MaxQualityTier)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var preview = await new ForgeSplicingEngine(_serviceProvider).PreviewStackFusionAsync(playerId, sampleId, toTier);
                if (preview == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var (baseItemId, plan) = preview.Value;
                var response = new ForgeStackPreviewResponse
                {
                    BaseItemId = baseItemId,
                    FromTier = plan.FromTier,
                    CeilingTier = plan.CeilingTier,
                    ForgeLevel = plan.ForgeLevel,
                    TotalFusions = plan.TotalFusions,
                    GoldCost = plan.GoldCost,
                    GoldAvailable = plan.GoldAvailable,
                    StoppedByGold = plan.StoppedByGold,
                    StoppedByCap = plan.StoppedByCap,
                };
                for (int tier = plan.FromTier; tier <= Math.Max(plan.FromTier, plan.CeilingTier) && tier < plan.CountsAfter.Length; tier++)
                {
                    if (plan.CountsAfter[tier] > 0) response.Result.Add(new ForgeStackTierCount { Tier = tier, Count = plan.CountsAfter[tier] });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Forge stack preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleChestBulkAction(HttpListenerContext context, bool sell)
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

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));

                if (!payload.TryGetProperty("maxQualityTier", out var tierElement)
                    || tierElement.ValueKind != JsonValueKind.Number)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                int maxQualityTier = tierElement.GetInt32();
                if (maxQualityTier < 1 || maxQualityTier > VillageChestEngine.MaxSweepableQualityTier)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // Optional: which regions' gear (1-5). Absent = all of them.
                var regions = new System.Collections.Generic.List<int>();
                if (payload.TryGetProperty("regions", out var regionsElement) && regionsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in regionsElement.EnumerateArray())
                    {
                        if (r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out int region) && region >= 1 && region <= 5 && !regions.Contains(region))
                        {
                            regions.Add(region);
                        }
                    }
                }

                var outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                    db, playerId, maxQualityTier, sell, regions);
                // Modul: a lost race is retried on a fresh context (the failed
                // one still tracks the rolled-back rows); a sweep that still
                // fails says so instead of reporting an empty success.
                for (int attempt = 2; attempt <= Domain.Shared.TransientDbConflict.DefaultAttempts && outcome.LostRace; attempt++)
                {
                    using var retryScope = _serviceProvider.CreateScope();
                    var retryDb = retryScope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                        retryDb, playerId, maxQualityTier, sell, regions);
                }

                // Modul: THE GOLD HAS TO REACH THE LIVE SESSION, and it must
                // not be banked twice on the way.
                //
                // The engine already credited CommodityRecords["gold"], the
                // authoritative row. But a player doing this is logged in - it
                // is a button on a screen - and their session holds a separate
                // unbanked CurrentGold that the wire reports and the checkpoint
                // later applies to that row AS AN INCREMENT. So the payload
                // needs its displayed total corrected, and its pending delta
                // left strictly alone: adding to RedisPendingGoldDelta here
                // would credit the row a second time at the next checkpoint.
                //
                // That asymmetry is the whole reason this is a different queue
                // from AutoSalvageQueue, which does the exact opposite - see
                // ChestSaleGoldNotification.
                if (outcome.GoldGained > 0L)
                {
                    _playerSessionRegistry?.ChestSaleGoldQueue.Enqueue(new ChestSaleGoldNotification
                    {
                        PlayerId = playerId,
                        GoldGained = outcome.GoldGained
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new ChestBulkActionResponse
                {
                    Success = !outcome.Failed,
                    RemovedCount = outcome.RemovedCount,
                    GoldGained = outcome.GoldGained,
                    SkippedWornCount = outcome.SkippedWornCount
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chest bulk action error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Reads (GET) or sets (POST) the auto-salvage floor.
        ///
        /// Opt-in and off by default, the same shape as email consent: quality
        /// tier 1 is a real item a new player wants, so changing nothing until
        /// the player asks is the only safe default.
        /// </summary>
        private async Task HandleChestSettings(HttpListenerContext context)
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

                var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);

                    // Modul: BOTH FIELDS ARE OPTIONAL, AND AT LEAST ONE IS
                    // REQUIRED. The Settings screen and exercise.mjs post only
                    // AutoSalvageBelowTier. The Chest's rules panel (task 81)
                    // posts both. A field that is absent keeps its stored
                    // value, so an older caller cannot wipe the region rules
                    // just by not knowing about them.
                    int tier = player.AutoSalvageBelowTier;
                    int regionTiers = player.AutoSalvageRegionTiers;
                    try
                    {
                        using var parsed = JsonDocument.Parse(body);
                        bool hasTier = parsed.RootElement.TryGetProperty("AutoSalvageBelowTier", out var tierElement);
                        bool hasRegions = parsed.RootElement.TryGetProperty("AutoSalvageRegionTiers", out var regionsElement);
                        if (!hasTier && !hasRegions)
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }

                        if (hasTier)
                        {
                            if (tierElement.ValueKind != JsonValueKind.Number)
                            {
                                context.Response.StatusCode = 400;
                                context.Response.Close();
                                return;
                            }
                            tier = tierElement.GetInt32();
                        }

                        if (hasRegions)
                        {
                            var perRegion = new List<int>();
                            if (regionsElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var element in regionsElement.EnumerateArray())
                                {
                                    if (element.ValueKind != JsonValueKind.Number) { perRegion = null; break; }
                                    perRegion.Add(element.GetInt32());
                                }
                            }
                            else
                            {
                                perRegion = null;
                            }

                            // TryPack refuses a wrong count or an out-of-range
                            // tier, for the same reason as the check below.
                            if (perRegion == null || !ChestSalvageRules.TryPack(perRegion, out regionTiers))
                            {
                                context.Response.StatusCode = 400;
                                context.Response.Close();
                                return;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    // Refused rather than clamped. Clamping a 14 down to 6 would
                    // silently give the player a setting they did not choose,
                    // and this one destroys items.
                    if (tier < 0 || tier > VillageChestEngine.MaxSweepableQualityTier)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    player.AutoSalvageBelowTier = tier;
                    player.AutoSalvageRegionTiers = regionTiers;
                    await db.SaveChangesAsync();

                    // The loot engine reads this off the live payload (see
                    // TickStatePayload.AutoSalvageBelowTier), which is hydrated
                    // at login - so without this the setting would not take
                    // effect until the player signed out and back in, and would
                    // look like a toggle that does nothing.
                    _playerSessionRegistry?.ChestSettingsQueue.Enqueue(new ChestSettingsNotification
                    {
                        PlayerId = playerId,
                        AutoSalvageBelowTier = tier,
                        AutoSalvageRegionTiers = regionTiers
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new ChestSettingsResponse
                {
                    AutoSalvageBelowTier = player.AutoSalvageBelowTier,
                    AutoSalvageRegionTiers = ChestSalvageRules.Unpack(player.AutoSalvageRegionTiers),
                    MaxSweepableQualityTier = VillageChestEngine.MaxSweepableQualityTier
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chest settings error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Sells or bins one thing from the village chest.
        ///
        /// One handler for both because they differ only in whether gold is
        /// paid - the lookup, the ownership check and the transaction are
        /// identical, and duplicating them would be duplicating the part that
        /// destroys a player's property.
        ///
        /// REST rather than a WebSocket command deliberately: the caller needs
        /// to be told HOW MUCH GOLD it got, and the fixed-layout command packet
        /// has no reply channel. It is also an explicit, deliberate action - a
        /// player pressing "sell" is waiting for the answer.
        /// </summary>
        private async Task HandleChestAction(HttpListenerContext context, bool sell)
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

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));

                VillageChestEngine.ChestActionResult result = VillageChestEngine.ChestActionResult.Busy;
                long gold = 0L;

                // An equipment id and a material id are different shapes, so
                // which one is present decides the operation rather than a
                // separate "kind" field that could disagree with the payload.
                bool isEquipment = payload.TryGetProperty("equipmentId", out var equipmentElement);
                bool isMaterial = !isEquipment && payload.TryGetProperty("itemId", out _);
                if (isEquipment || isMaterial)
                {
                    // Modul: a lost race (Busy) is retried on a fresh context -
                    // the failed one still tracks the rolled-back rows.
                    for (int attempt = 1; attempt <= Domain.Shared.TransientDbConflict.DefaultAttempts && result == VillageChestEngine.ChestActionResult.Busy; attempt++)
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                        if (isEquipment)
                        {
                            (result, gold) = await VillageChestEngine.RemoveEquipmentAsync(
                                db, playerId, equipmentElement.GetInt64(), sell);
                        }
                        else
                        {
                            long quantity = payload.TryGetProperty("quantity", out var q) ? q.GetInt64() : 0L;
                            (result, gold) = await VillageChestEngine.RemoveMaterialAsync(
                                db, playerId, payload.GetProperty("itemId").GetString() ?? string.Empty, quantity, sell);
                        }
                    }
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new ChestActionResponse
                {
                    Success = result == VillageChestEngine.ChestActionResult.Success,
                    GoldGained = gold,
                    Reason = result.ToString()
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chest action error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Toggles one item's affix lock. Mirrors HandleChestAction's shape
        /// exactly, including the ChestActionResult it reports back, so the
        /// screen has one vocabulary for everything it can ask of an item.
        /// </summary>
        private async Task HandleChestToggleLock(HttpListenerContext context)
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

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));

                if (!payload.TryGetProperty("equipmentId", out var equipmentElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var (result, locked) = await VillageChestEngine.ToggleAffixLockAsync(
                    db, playerId, equipmentElement.GetInt64());

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new ChestLockResponse
                {
                    Success = result == VillageChestEngine.ChestActionResult.Success,
                    Locked = locked,
                    Reason = result.ToString()
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chest lock error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 21: on-demand snapshot for the client Forge crafting/reroll panels.
        // StateUpdatePacket is fixed-size and carries scalars only, so the player's
        // full owned-equipment list and per-recipe material stock (both variable
        // length) are served here instead, following the same authenticated
        // read-only HTTP pattern as HandleGuildLogisticsSnapshot/HandleGlobalLeaderboard.
        private async Task HandleForgeInventorySnapshot(HttpListenerContext context)
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

                var ownedEquipment = await db.EquipmentInstances
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

                var materialQuantities = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToDictionaryAsync(c => c.ItemId, c => c.Quantity);

                // All ELEVEN slots - eight combat, then axe, pickaxe, rod.
                var wornLoadouts = await db.CharacterRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                await transaction.CommitAsync();

                // Item id -> (character slot, equipment slot). The array is in
                // equipment-slot order: eight combat slots, then the three tools.
                var wornBy = new System.Collections.Generic.Dictionary<long, (int CharacterSlot, int EquipSlot)>();
                foreach (var c in wornLoadouts)
                {
                    var loadout = new[]
                    {
                        c.EquippedWeaponId, c.EquippedHelmetId, c.EquippedChestId, c.EquippedGlovesId,
                        c.EquippedLeggingsId, c.EquippedBootsId, c.EquippedAmuletId, c.EquippedRingId,
                        c.EquippedAxeId, c.EquippedPickaxeId, c.EquippedRodId,
                    };
                    for (int equipSlot = 0; equipSlot < loadout.Length; equipSlot++)
                    {
                        if (loadout[equipSlot] is long id) wornBy[id] = (c.SlotIndex, equipSlot);
                    }
                }

                var response = new ForgeInventorySnapshotResponse();

                foreach (var item in ownedEquipment)
                {
                    var affixes = new System.Collections.Generic.Dictionary<string, int>();
                    bool jsonLockFlag = false;

                    if (!string.IsNullOrWhiteSpace(item.AffixPayload) &&
                        System.Text.Json.Nodes.JsonNode.Parse(item.AffixPayload) is System.Text.Json.Nodes.JsonObject affixObject)
                    {
                        foreach (var kvp in affixObject)
                        {
                            if (kvp.Value is not System.Text.Json.Nodes.JsonValue affixValue)
                            {
                                continue;
                            }

                            if (kvp.Key == "is_affix_locked")
                            {
                                jsonLockFlag = affixValue.TryGetValue(out bool lockedFlag) && lockedFlag;
                                continue;
                            }

                            if (affixValue.TryGetValue(out int magnitude))
                            {
                                affixes[kvp.Key] = magnitude;
                            }
                        }
                    }

                    response.OwnedEquipment.Add(new ForgeEquipmentInstanceResponse
                    {
                        Id = item.Id,
                        BaseItemId = item.BaseItemId,
                        QualityTier = item.QualityTier,
                        IsAffixLocked = item.IsAffixLocked || jsonLockFlag,
                        IsEquipped = wornBy.ContainsKey(item.Id),
                        EquippedByCharacterSlot = wornBy.TryGetValue(item.Id, out var wearer) ? wearer.CharacterSlot : -1,
                        EquippedInSlotIndex = wornBy.TryGetValue(item.Id, out var worn) ? worn.EquipSlot : -1,
                        RerollGoldCost = AffixRerollEngine.GoldCostFor(item.BaseItemId, item.QualityTier, refine: false),
                        RefineGoldCost = AffixRerollEngine.GoldCostFor(item.BaseItemId, item.QualityTier, refine: true),
                        Affixes = affixes
                    });
                }

                // Modul: the Forge no longer serves recipes. It fuses and
                // rerolls what the player looted; making equipment out of ore
                // was the second crafting system, and it is gone. Recipes stay
                // on /api/v1/crafting/recipes, which serves the tool tree.
                // response.Recipes is left on the DTO and simply comes back
                // empty - removing it would be a wire break for a client that
                // has not reloaded yet, and it costs two bytes of JSON.

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Forge inventory snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }
    }
}
