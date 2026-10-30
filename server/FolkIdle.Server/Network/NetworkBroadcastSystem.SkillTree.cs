using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    // The skill tree screen's REST side (docs/design/SKILL_TREE_REWORK.md). The
    // definition, a draft preview, an atomic commit and the respec. The old
    // PurchaseSkillTreeLevel and RespecSkillTree opcodes stay for a bundle that
    // has not reloaded; all of them end in the same row locks.
    //
    // Modul: THE WAR HALF SINCE SLICE 2.5. Every answer here is
    // WarTreeRegistry's: 35 nodes at ids 20-54 of a 64-slot rank array. Today's
    // tree (ids 0-19) is refunded on load (SkillTreeEngine.ReconcileWarSwitchAsync)
    // and is no longer on the wire.
    //
    // Modul: THE PREVIEW IS THE SERVER'S ANSWER, NOT THE CLIENT'S. Draft mode
    // has to say which nodes a draft opens and what it does to the stats. A
    // client that worked either out would hold a second copy of the gates and
    // a second damage model, so it asks: the gates through
    // WarTreeRegistry.PlanReason/BlockedReason, the stats through
    // CharacterStatSheet on a copy of the live payload.
    public partial class NetworkBroadcastSystem
    {
        /// <summary>Routes /api/v1/skilltree*; true when it answered.</summary>
        private async Task<bool> TryHandleSkillTreeAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/skilltree", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;

            if (requestPath == "/api/v1/skilltree" && method == "GET") { await HandleSkillTreeDefinition(context); return true; }
            if (requestPath == "/api/v1/skilltree/state" && method == "GET") { await HandleSkillTreeState(context); return true; }
            if (requestPath == "/api/v1/skilltree/preview" && method == "POST") { await HandleSkillTreePreview(context); return true; }
            if (requestPath == "/api/v1/skilltree/commit" && method == "POST") { await HandleSkillTreeCommit(context); return true; }
            if (requestPath == "/api/v1/skilltree/respec" && method == "POST") { await HandleSkillTreeRespec(context); return true; }
            return false;
        }

        /// <summary>The tree's shape and text. Public: it is the same for everyone.</summary>
        internal static object DescribeSkillTree()
        {
            var nodes = WarTreeRegistry.All().Select(n =>
            {
                bool bridge = n.Kind == "Bridge";
                int gate = bridge ? WarTreeRegistry.BridgeGate : WarTreeRegistry.RowGates[n.Row];
                string blurb = bridge
                    ? $"A bridge: needs {gate} points in both {WarTreeRegistry.ColumnNames[n.Column]} and {WarTreeRegistry.ColumnNames[n.BridgeColumn]}, and its points count for both."
                    : gate > 0
                        ? $"Opens once {WarTreeRegistry.ColumnNames[n.Column]} holds {gate} points."
                        : $"Open from the start.";
                if (n.Fork >= 0) blurb += $" A fork with {WarTreeRegistry.Get(n.Fork)!.Name}: take one, and the other closes until a respec.";
                return new
                {
                    n.Id,
                    n.Name,
                    n.Icon,
                    Blurb = blurb,
                    n.Kind,
                    n.Column,
                    n.BridgeColumn,
                    n.Row,
                    n.Lane,
                    n.MaxLevel,
                    n.Fork,
                    ColumnPointsGate = gate,
                    Costs = Enumerable.Repeat(n.CostPerRank, n.MaxLevel).ToArray(),
                    Effects = Enumerable.Range(1, n.MaxLevel).Select(l => WarTreeRegistry.EffectAt(n.Id, l)).ToArray(),
                    Pending = WarTreeRegistry.IsPending(n.Id),
                };
            }).ToArray();

            var columns = Enumerable.Range(0, WarTreeRegistry.ColumnCount)
                .Select(id => new { Id = id, Name = WarTreeRegistry.ColumnNames[id], Icon = WarTreeRegistry.ColumnIcons[id] })
                .ToArray();

            return new
            {
                Columns = columns,
                Nodes = nodes,
                Slots = WarTreeRegistry.Slots,
                RowGates = WarTreeRegistry.RowGates,
                WarTreeRegistry.BridgeGate,
            };
        }

        private static async Task HandleSkillTreeDefinition(HttpListenerContext context)
        {
            try { await WriteJsonAsync(context, DescribeSkillTree()); }
            catch (Exception ex)
            {
                Console.WriteLine($"Skill tree definition error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally { context.Response.Close(); }
        }

        /// <summary>
        /// GET: the player's ranks, points and respec state, and what a gold
        /// respec would cost. Read from the database, which every purchase and
        /// respec commits before the tick hears of it.
        /// </summary>
        private async Task HandleSkillTreeState(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                byte[] ranks = await SkillTreeEngine.LoadRanksAsync(db, playerId);
                var player = await db.PlayerRecords.FindAsync(playerId);
                if (player == null) { context.Response.StatusCode = 404; return; }
                long gold = await db.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                    .Select(c => c.Quantity)
                    .FirstOrDefaultAsync();
                int spent = WarTreeRegistry.PointsSpent(ranks);

                await WriteJsonAsync(context, new
                {
                    Levels = Array.ConvertAll(ranks, b => (int)b),
                    player.AvailableSkillPoints,
                    player.FreeRespecUsed,
                    player.PaidRespecGrants,
                    PointsSpent = spent,
                    RespecGoldPrice = WarTreeRegistry.RespecGoldPrice(spent, player.CurrentLevel),
                    Gold = gold,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skill tree state error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally { context.Response.Close(); }
        }

        /// <summary>Reads { "Levels": [Slots ints], "Slot": n }; null when the body is not that.</summary>
        private static async Task<(byte[]? Levels, int Slot)> ReadSkillTreeDraftAsync(HttpListenerContext context)
        {
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                int slot = 0;
                if (parsed.RootElement.TryGetProperty("Slot", out var s) && s.TryGetInt32(out int sv)) slot = Math.Clamp(sv, 0, 2);
                if (!parsed.RootElement.TryGetProperty("Levels", out var arr) || arr.ValueKind != JsonValueKind.Array) return (null, slot);
                if (arr.GetArrayLength() != WarTreeRegistry.Slots) return (null, slot);

                var levels = new byte[WarTreeRegistry.Slots];
                int i = 0;
                foreach (var el in arr.EnumerateArray())
                {
                    int max = WarTreeRegistry.Get(i)?.MaxLevel ?? 0;
                    if (!el.TryGetInt32(out int v) || v < 0 || v > max) return (null, slot);
                    levels[i++] = (byte)v;
                }
                return (levels, slot);
            }
            catch (JsonException) { return (null, 0); }
        }

        /// <summary>
        /// POST { Levels, Slot }: is the draft legal, what does it cost, which
        /// nodes are open on top of it, each column's points, and the stat
        /// sheet before and after.
        /// </summary>
        private async Task HandleSkillTreePreview(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                var (draft, slot) = await ReadSkillTreeDraftAsync(context);
                if (draft == null) { context.Response.StatusCode = 400; return; }

                byte[] current;
                int points;
                using (var scope = _serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    current = await SkillTreeEngine.LoadRanksAsync(db, playerId);
                    var player = await db.PlayerRecords.FindAsync(playerId);
                    points = player?.AvailableSkillPoints ?? 0;
                }

                string? reason = WarTreeRegistry.PlanReason(current, draft, points, out int cost);
                int left = points - (reason == null ? cost : 0);
                var basis = reason == null ? draft : current;

                var nodes = WarTreeRegistry.All().Select(n => new
                {
                    n.Id,
                    Blocked = WarTreeRegistry.BlockedReason(n.Id, basis, left),
                    NextCost = WarTreeRegistry.CostOf(n.Id, basis[n.Id]),
                }).ToArray();
                var columnPoints = Enumerable.Range(0, WarTreeRegistry.ColumnCount)
                    .Select(c => WarTreeRegistry.PointsInColumn(basis, c))
                    .ToArray();

                StatSheetResponse? before = null, after = null;
                if (_playerSessionRegistry != null)
                {
                    var order = new PayloadSnapshotOrder { PlayerId = playerId };
                    _playerSessionRegistry.PayloadSnapshotQueue.Enqueue(order);
                    var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(3000));
                    var snapshot = finished == order.Completion.Task ? order.Completion.Task.Result : null;
                    if (snapshot != null)
                    {
                        var payload = snapshot.Value.Payload;
                        SimulationEngine.SwapSlotIntoActiveRegister(ref payload, slot);
                        payload.SkillTreeVersion = WarTreeRegistry.CurrentVersion;
                        foreach (var n in WarTreeRegistry.All()) payload.SkillRanks[n.Id] = current[n.Id];
                        before = CharacterStatSheet.Build(payload, slot);
                        if (reason == null)
                        {
                            foreach (var n in WarTreeRegistry.All()) payload.SkillRanks[n.Id] = draft[n.Id];
                            after = CharacterStatSheet.Build(payload, slot);
                        }
                    }
                }

                await WriteJsonAsync(context, new
                {
                    Reason = reason,
                    Cost = reason == null ? cost : 0,
                    PointsLeft = left,
                    Nodes = nodes,
                    ColumnPoints = columnPoints,
                    Before = before,
                    After = after,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skill tree preview error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally { context.Response.Close(); }
        }

        /// <summary>POST { Levels }: buys the whole draft or nothing. A refusal answers 200 with its Reason.</summary>
        private async Task HandleSkillTreeCommit(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                var (draft, _) = await ReadSkillTreeDraftAsync(context);
                if (draft == null) { context.Response.StatusCode = 400; return; }

                var engine = new SkillTreeEngine(_serviceProvider, _playerSessionRegistry);
                var result = await engine.CommitAsync(playerId, draft);
                await WriteJsonAsync(context, result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skill tree commit error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally { context.Response.Close(); }
        }

        /// <summary>
        /// POST { Gold }: refunds the whole tree - with the free respec or a
        /// paid grant, or, when Gold is true, for WarTreeRegistry.RespecGoldPrice
        /// gold (decision D3). A refusal answers 200 with its Reason.
        /// </summary>
        private async Task HandleSkillTreeRespec(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                bool payWithGold = false;
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (parsed.RootElement.TryGetProperty("Gold", out var g) && g.ValueKind == JsonValueKind.True) payWithGold = true;
                }
                catch (JsonException) { context.Response.StatusCode = 400; return; }

                var engine = new SkillTreeEngine(_serviceProvider, _playerSessionRegistry);
                var result = await engine.RespecAsync(playerId, payWithGold);
                await WriteJsonAsync(context, result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skill tree respec error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally { context.Response.Close(); }
        }
    }
}
