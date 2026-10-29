using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    // The skill tree screen's REST side (docs/design/SKILL_TREE_REWORK.md,
    // phase 1). The definition, a draft preview and an atomic commit. The old
    // PurchaseSkillTreeLevel opcode stays for a bundle that has not reloaded;
    // both end in the same row locks.
    //
    // Modul: THE PREVIEW IS THE SERVER'S ANSWER, NOT THE CLIENT'S. Draft mode
    // has to say which nodes a draft opens and what it does to the stats. A
    // client that worked either out would hold a second copy of the gates and
    // a second damage model, so it asks: the gates through
    // SkillTreeRegistry.PlanReason/BlockedReason, the stats through
    // CharacterStatSheet on a copy of the live payload.
    public partial class NetworkBroadcastSystem
    {
        /// <summary>Routes /api/v1/skilltree*; true when it answered.</summary>
        private async Task<bool> TryHandleSkillTreeAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/skilltree", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;

            if (requestPath == "/api/v1/skilltree" && method == "GET") { await HandleSkillTreeDefinition(context); return true; }
            if (requestPath == "/api/v1/skilltree/preview" && method == "POST") { await HandleSkillTreePreview(context); return true; }
            if (requestPath == "/api/v1/skilltree/commit" && method == "POST") { await HandleSkillTreeCommit(context); return true; }
            return false;
        }

        /// <summary>The tree's shape and text. Public: it is the same for everyone.</summary>
        internal static object DescribeSkillTree()
        {
            var nodes = Enumerable.Range(0, SkillTreeRegistry.NodeCount).Select(id =>
            {
                var node = SkillTreeRegistry.Nodes[id];
                return new
                {
                    Id = id,
                    Name = SkillTreeRegistry.GetName(id),
                    Icon = node.Icon,
                    Blurb = SkillTreeRegistry.GetBlurb(id),
                    Kind = node.Kind,
                    Column = node.Column,
                    BridgeColumn = node.BridgeColumn,
                    Row = node.Row,
                    Lane = node.Lane,
                    MaxLevel = node.MaxLevel,
                    Fork = node.Fork,
                    ColumnPointsGate = node.ColumnPointsGate,
                    Costs = node.Costs,
                    Effects = Enumerable.Range(1, node.MaxLevel).Select(l => SkillTreeRegistry.EffectText(id, l)).ToArray(),
                    Pending = SkillTreeRegistry.IsEffectPending(id),
                };
            }).ToArray();

            var columns = Enumerable.Range(0, SkillTreeRegistry.RootCount)
                .Select(id => new { Id = id, Name = SkillTreeRegistry.GetName(id), Icon = SkillTreeRegistry.ColumnIcons[id] })
                .ToArray();

            return new { Columns = columns, Nodes = nodes };
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

        /// <summary>Reads { "Levels": [20 ints], "Slot": n }; null when the body is not that.</summary>
        private static async Task<(byte[]? Levels, int Slot)> ReadSkillTreeDraftAsync(HttpListenerContext context)
        {
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                int slot = 0;
                if (parsed.RootElement.TryGetProperty("Slot", out var s) && s.TryGetInt32(out int sv)) slot = Math.Clamp(sv, 0, 2);
                if (!parsed.RootElement.TryGetProperty("Levels", out var arr) || arr.ValueKind != JsonValueKind.Array) return (null, slot);
                if (arr.GetArrayLength() != SkillTreeRegistry.NodeCount) return (null, slot);

                var levels = new byte[SkillTreeRegistry.NodeCount];
                int i = 0;
                foreach (var el in arr.EnumerateArray())
                {
                    if (!el.TryGetInt32(out int v) || v < 0 || v > SkillTreeRegistry.MaxLevelOf(i)) return (null, slot);
                    levels[i++] = (byte)v;
                }
                return (levels, slot);
            }
            catch (JsonException) { return (null, 0); }
        }

        /// <summary>
        /// POST { Levels, Slot }: is the draft legal, what does it cost, which
        /// nodes are open on top of it, and the stat sheet before and after.
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
                    current = await SkillTreeEngine.LoadLevelsAsync(db, playerId);
                    var player = await db.PlayerRecords.FindAsync(playerId);
                    points = player?.AvailableSkillPoints ?? 0;
                }

                string? reason = SkillTreeRegistry.PlanReason(current, draft, points, out int cost);
                int left = points - (reason == null ? cost : 0);
                var basis = reason == null ? draft : current;

                var nodes = Enumerable.Range(0, SkillTreeRegistry.NodeCount).Select(id => new
                {
                    Id = id,
                    Blocked = SkillTreeRegistry.BlockedReason(id, basis, left),
                    NextCost = SkillTreeRegistry.GetUpgradeCost(id, basis[id]),
                }).ToArray();

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
                        for (int id = 0; id < SkillTreeRegistry.NodeCount; id++) SimulationEngine.SetSkillTreeLevel(ref payload, id, current[id]);
                        before = CharacterStatSheet.Build(payload, slot);
                        if (reason == null)
                        {
                            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++) SimulationEngine.SetSkillTreeLevel(ref payload, id, draft[id]);
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
    }
}
