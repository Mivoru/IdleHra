using System;
using System.Collections.Generic;
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
    public partial class NetworkBroadcastSystem
    {
        /// <summary>
        /// POST /api/v1/dev/projection/whatif - dev only (FOLKIDLE_DEV_TOOLS).
        ///
        /// The hunting projection (/api/v1/combat/projection) for the live
        /// character with one or more HYPOTHETICAL changes: other attribute
        /// values, other worn pieces, other skill ranks. Each scenario is
        /// projected on its own copy of the live payload, against the monsters
        /// asked for.
        ///
        /// Body: { Slot, Monsters: [ids], Scenarios: [ { Attributes: [s,d,c,l]?,
        /// Wear: [equipment instance ids]?, SkillRanks: { "nodeId": rank }? } ] }
        /// Answer: { Scenarios: [ { Monsters: [HuntingEstimate...] } ] }, in the
        /// order asked.
        ///
        /// Modul: THE PLAYTEST BOTS DECIDE BY THE SERVER'S NUMBERS (2026-11-01).
        /// The bot compared gear on base ATK + DEF, spent attributes 2:1 and
        /// bought the cheapest skill node, so a Legendary with useless affixes
        /// beat a rare with lifesteal (PLAYTEST_BOTS_REPORT.md 4.1.2-4.1.4).
        /// A copy of the server's formula in the bot would be a second source
        /// of truth; this asks the one the tick uses. It changes nothing: the
        /// payload is a snapshot copy and the gear totals are computed from a
        /// detached character record. Dev only because a player's screens do
        /// not offer it - it is a measuring tool, not a feature.
        /// </summary>
        private async Task HandleDevProjectionWhatIf(HttpListenerContext context, long playerId)
        {
            int slot = 0;
            var monsterIds = new List<int>();
            var scenarios = new List<JsonElement>();
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = parsed.RootElement;
                if (root.TryGetProperty("Slot", out var s) && s.TryGetInt32(out int sv)) slot = Math.Clamp(sv, 0, 2);
                if (root.TryGetProperty("Monsters", out var m) && m.ValueKind == JsonValueKind.Array)
                    foreach (var e in m.EnumerateArray().Take(ContentRegistry.Monsters.Length))
                        if (e.TryGetInt32(out int id) && id >= ContentRegistry.FirstCanonicalMonsterId && id <= ContentRegistry.Monsters.Length) monsterIds.Add(id);
                if (root.TryGetProperty("Scenarios", out var sc) && sc.ValueKind == JsonValueKind.Array)
                    foreach (var e in sc.EnumerateArray().Take(64)) scenarios.Add(e.Clone());
            }
            catch (JsonException)
            {
                context.Response.StatusCode = 400;
                return;
            }
            if (monsterIds.Count == 0)
                for (int id = ContentRegistry.FirstCanonicalMonsterId; id <= ContentRegistry.Monsters.Length; id++) monsterIds.Add(id);

            if (_playerSessionRegistry == null) { context.Response.StatusCode = 503; return; }
            var order = new PayloadSnapshotOrder { PlayerId = playerId };
            _playerSessionRegistry.PayloadSnapshotQueue.Enqueue(order);
            var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(3000));
            var snapshot = finished == order.Completion.Task ? order.Completion.Task.Result : null;
            if (snapshot == null)
            {
                context.Response.StatusCode = 409;
                await WriteJsonAsync(context, new { Reason = "NoSession" });
                return;
            }

            var live = snapshot.Value.Payload;
            SimulationEngine.SwapSlotIntoActiveRegister(ref live, slot);
            Guid characterId = slot switch { 1 => live.Slot2_CharacterId, 2 => live.Slot3_CharacterId, _ => live.Slot1_CharacterId };

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var character = characterId == Guid.Empty ? null : await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == characterId);

            var answers = new List<object>();
            foreach (var scenario in scenarios)
            {
                var payload = live;
                if (scenario.TryGetProperty("Attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
                {
                    var a = attrs.EnumerateArray().Select(e => e.TryGetInt32(out int v) ? Math.Max(0, v) : 0).ToArray();
                    if (a.Length == 4) { payload.STR = a[0]; payload.DEX = a[1]; payload.CON = a[2]; payload.LCK = a[3]; }
                }
                if (scenario.TryGetProperty("SkillRanks", out var ranks) && ranks.ValueKind == JsonValueKind.Object)
                {
                    // As /skilltree/preview does: ranks are read under the current
                    // tree version, and a payload still on an older one ignores them.
                    payload.SkillTreeVersion = WarTreeRegistry.CurrentVersion;
                    foreach (var p in ranks.EnumerateObject())
                        if (int.TryParse(p.Name, out int node) && node >= 0 && node < SkillRankBuffer.Length && p.Value.TryGetInt32(out int rank))
                            payload.SkillRanks[node] = (byte)Math.Clamp(rank, 0, 255);
                }
                if (character != null && scenario.TryGetProperty("Wear", out var wear) && wear.ValueKind == JsonValueKind.Array)
                {
                    var ids = wear.EnumerateArray().Select(e => e.TryGetInt64(out long v) ? v : 0).Where(v => v > 0).Take(8).ToList();
                    var pieces = await db.EquipmentInstances.AsNoTracking()
                        .Where(e => ids.Contains(e.Id) && e.PlayerId == playerId)
                        .Select(e => new { e.Id, e.BaseItemId })
                        .ToListAsync();
                    var draft = new CharacterRecord
                    {
                        Id = character.Id,
                        EquippedWeaponId = character.EquippedWeaponId,
                        EquippedHelmetId = character.EquippedHelmetId,
                        EquippedChestId = character.EquippedChestId,
                        EquippedGlovesId = character.EquippedGlovesId,
                        EquippedLeggingsId = character.EquippedLeggingsId,
                        EquippedBootsId = character.EquippedBootsId,
                        EquippedAmuletId = character.EquippedAmuletId,
                        EquippedRingId = character.EquippedRingId,
                    };
                    foreach (var piece in pieces)
                    {
                        switch (EquipmentSlotEngine.ResolveSlotIndex(piece.BaseItemId))
                        {
                            case EquipmentSlotEngine.SlotWeapon: draft.EquippedWeaponId = piece.Id; break;
                            case EquipmentSlotEngine.SlotHelmet: draft.EquippedHelmetId = piece.Id; break;
                            case EquipmentSlotEngine.SlotChest: draft.EquippedChestId = piece.Id; break;
                            case EquipmentSlotEngine.SlotGloves: draft.EquippedGlovesId = piece.Id; break;
                            case EquipmentSlotEngine.SlotLeggings: draft.EquippedLeggingsId = piece.Id; break;
                            case EquipmentSlotEngine.SlotBoots: draft.EquippedBootsId = piece.Id; break;
                            case EquipmentSlotEngine.SlotAmulet: draft.EquippedAmuletId = piece.Id; break;
                            case EquipmentSlotEngine.SlotRing: draft.EquippedRingId = piece.Id; break;
                        }
                    }
                    var (totals, setIds) = await EquipmentSlotEngine.ComputeEquippedTotalsAsync(db, draft);
                    payload.CachedAffixTotals = totals;
                    payload.CachedSetIds = setIds;
                }

                answers.Add(new
                {
                    Monsters = monsterIds.Select(id =>
                    {
                        var e = HuntingProjection.Project(in payload, id, snapshot.Value.GlobalXpMultiplier, snapshot.Value.ActiveGlobalEventId);
                        return new HuntingEstimateResponse
                        {
                            MonsterId = e.MonsterId,
                            CanDamage = e.CanDamage,
                            SecondsPerKill = Math.Round(e.SecondsPerKill, 1),
                            SecondsPerKillLow = Math.Round(e.SecondsPerKillLow, 1),
                            SecondsPerKillHigh = Math.Round(e.SecondsPerKillHigh, 1),
                            XpPerHour = e.XpPerHour,
                            GoldPerHour = e.GoldPerHour,
                            SurvivesWithFood = e.SurvivesWithFood,
                            SurvivesWithoutFood = e.SurvivesWithoutFood,
                            KillsBeforeDeathWithoutFood = e.KillsBeforeDeathWithoutFood,
                            FoodPerHour = Math.Round(e.FoodPerHour, 1),
                        };
                    }).ToList(),
                });
            }

            await WriteJsonAsync(context, new { Scenarios = answers });
        }
    }
}
