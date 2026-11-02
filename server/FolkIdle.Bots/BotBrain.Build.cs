using System.Text.Json;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Bots
{
    /// <summary>
    /// Attributes, skill tree and gear, chosen by the server's own numbers.
    ///
    /// Modul: THE PROJECTION DECIDES, NOT A RULE OF THUMB (bot mistakes
    /// 4.1.2-4.1.4, 2026-11-01). The bot spent attributes 2:1 Might/Vigour,
    /// bought the cheapest skill node and compared gear on base ATK + DEF, so a
    /// Legendary with useless affixes beat a rare with lifesteal. Every choice
    /// now asks /api/v1/dev/projection/whatif - HuntingProjection on a copy of
    /// the live payload with the change applied - and takes the one that
    /// scores best (BuildScore). The server's formula is the only one; the bot
    /// keeps no copy of it.
    /// </summary>
    public sealed partial class BotBrain
    {
        private int _skillTreeTriedAtPoints = -1;
        private readonly HashSet<long> _gearTried = new();

        private async Task BuildChoresAsync(CancellationToken token)
        {
            if (_state.AvailableSkillPoints > 0 && _state.AvailableSkillPoints != _skillTreeTriedAtPoints)
            {
                _skillTreeTriedAtPoints = _state.AvailableSkillPoints;
                await SpendSkillPointsAsync("war", token);
            }
            if (Due("hearth", 120)) await SpendSkillPointsAsync("hearth", token);
            if (_state.UnspentAttributePoints > 0) await ChooseAttributesAsync(token);
            if (Due("gear", 15)) await ChooseGearAsync(token);
        }

        // ------------------------------------------------------------------
        // Scoring
        // ------------------------------------------------------------------

        /// <summary>The monsters a build is judged against: every monster of the open regions and the next one's first.</summary>
        private int[] BuildMonsters()
        {
            int region = Math.Max(1, (int)_state.HighestUnlockedRegion);
            int last = Math.Min(ContentRegistry.LastCanonicalMonsterId, ContentRegistry.FirstCanonicalMonsterId + region * ContentRegistry.MonstersPerRegion);
            return Enumerable.Range(ContentRegistry.FirstCanonicalMonsterId, last - ContentRegistry.FirstCanonicalMonsterId + 1).ToArray();
        }

        /// <summary>
        /// What a build is worth: the XP/h of the best fight that holds, raised
        /// 30% for each GATE it opens - the open region's boss if not beaten,
        /// and the next region's first monster - plus a sliver of the kills
        /// before death across all monsters, so that between two builds with
        /// equal XP the sturdier one wins.
        /// </summary>
        private double BuildScore(JsonElement scenario)
        {
            bool hasFood = LarderCount > 0;
            int region = Math.Max(1, (int)_state.HighestUnlockedRegion);
            int boss = RaceUnlockRegistry.GetRegionBossMonsterId(region);
            int nextFirst = ContentRegistry.FirstCanonicalMonsterId + region * ContentRegistry.MonstersPerRegion;
            double bestXp = 0, sturdiness = 0;
            int gates = 0;
            foreach (var e in scenario.Arr("Monsters"))
            {
                int id = e.Int("MonsterId");
                bool holds = e.Bool("CanDamage") && (e.Bool("SurvivesWithoutFood") || (hasFood && e.Bool("SurvivesWithFood")));
                if (holds && MonsterRegion(id) <= region) bestXp = Math.Max(bestXp, e.Num("XpPerHour"));
                if (holds && ((id == boss && !BossFirstClearRules.IsDefeated(_state.DefeatedRegionBossMask, boss)) || id == nextFirst)) gates++;
                sturdiness += Math.Min(1000, e.Int("KillsBeforeDeathWithoutFood"));
            }
            return bestXp * (1 + 0.3 * gates) + sturdiness * 0.01;
        }

        /// <summary>Projects each scenario; null when the server could not (no session, dev tools off).</summary>
        private async Task<List<JsonElement>?> WhatIfAsync(IReadOnlyList<object> scenarios, CancellationToken token)
        {
            var (status, body) = await _client.PostAsync("/api/v1/dev/projection/whatif", new { Slot = 0, Monsters = BuildMonsters(), Scenarios = scenarios }, token);
            var list = body is { } b ? b.Arr("Scenarios").ToList() : new List<JsonElement>();
            if (body is { } nb && nb.Str("Reason") == "NoSession") _noSessionSeen = true;
            if (status is >= 200 and < 300 && list.Count == scenarios.Count) return list;
            if (Due("whatif-failed", 120))
                Event($"what-if projection unavailable (HTTP {status}, {(body is { } x ? x.GetRawText()[..Math.Min(120, x.GetRawText().Length)] : "no body")}); falling back to rules");
            return null;
        }

        // ------------------------------------------------------------------
        // Attributes
        // ------------------------------------------------------------------

        private static readonly string[] AttributeNames = { "Might", "Finesse", "Vigour", "Fortune" };
        private ClientCommandPacket? _attributeChoice;
        private int _attributeChoiceFor = -1;

        /// <summary>
        /// Requirements first: every attribute up to what the open region's
        /// gear asks (EquipmentAttributeGate), because a drop the character
        /// cannot wear is worth nothing. The rest goes, a third of the points at
        /// a time, wherever the what-if projection scores best - decided on the
        /// REST side (ChooseAttributesAsync) and sent here.
        /// </summary>
        private bool TryAttributeCommand(out ClientCommandPacket packet)
        {
            packet = default;
            int[] attrs = { _state.STR, _state.DEX, _state.CON, _state.LCK };
            int region = Math.Max(1, (int)_state.HighestUnlockedRegion);
            int points = _state.UnspentAttributePoints;

            int need = EquipmentAttributeGate.RequirementFor(region);
            int lowest = Enumerable.Range(0, 4).OrderBy(i => attrs[i]).First();
            if (attrs[lowest] < need)
            {
                int amount = Math.Min(points, need - attrs[lowest]);
                packet = new ClientCommandPacket { Command = CommandType.SpendAttributePoint, TargetId = lowest, LimitPrice = amount };
                Event($"attributes: {amount} into {AttributeNames[lowest]} (region {region} gear asks {need})");
                return true;
            }

            if (_attributeChoice is { } choice && _attributeChoiceFor == points)
            {
                packet = choice;
                _attributeChoice = null;
                return true;
            }
            return false;
        }

        private async Task ChooseAttributesAsync(CancellationToken token)
        {
            int points = _state.UnspentAttributePoints;
            if (points <= 0 || _attributeChoiceFor == points && _attributeChoice != null) return;
            int chunk = Math.Max(1, (points + 2) / 3);
            int[] attrs = { _state.STR, _state.DEX, _state.CON, _state.LCK };
            var scenarios = new List<object>();
            for (int i = 0; i < 4; i++)
            {
                var a = (int[])attrs.Clone();
                a[i] += chunk;
                scenarios.Add(new { Attributes = a });
            }
            var answers = await WhatIfAsync(scenarios, token);
            int pick;
            if (answers == null)
            {
                pick = attrs[0] <= attrs[2] * 2 ? 0 : 2; // the old 2:1 rule, when the server cannot say
            }
            else
            {
                var scores = answers.Select(BuildScore).ToArray();
                pick = Enumerable.Range(0, 4).OrderByDescending(i => scores[i]).ThenBy(i => i == 3 ? 1 : 0).First();
            }
            _attributeChoice = new ClientCommandPacket { Command = CommandType.SpendAttributePoint, TargetId = pick, LimitPrice = chunk };
            _attributeChoiceFor = points;
            Event($"attributes: {chunk} of {points} into {AttributeNames[pick]}{(answers == null ? " (no projection: 2:1 rule)" : " (best projected score)")}");
        }

        // ------------------------------------------------------------------
        // Skill tree
        // ------------------------------------------------------------------

        /// <summary>
        /// Buys node after node through the server's preview (which owns
        /// legality), each time the open node with the best projected score
        /// gain per point. Nodes the projection cannot see (gathering, gold)
        /// score no gain; when nothing gains, the cheapest open node of the
        /// lowest row is taken, as before, so the points are never left idle.
        /// The Hearth half (craft points) has no combat effect and is always
        /// bought cheapest first.
        /// </summary>
        private async Task SpendSkillPointsAsync(string half, CancellationToken token)
        {
            string query = half == "hearth" ? "?half=hearth" : "";
            var treeState = await _client.GetAsync("/api/v1/skilltree/state", token);
            if (treeState is not { } st) return;
            int[] levels = st.Arr("Levels").Select(e => e.GetInt32()).ToArray();
            var definition = await _client.GetAsync("/api/v1/skilltree" + query, token);
            if (definition is not { } def) return;
            var rowOf = def.Arr("Nodes").ToDictionary(n => n.Int("Id"), n => n.Int("Row"));
            var nameOf = def.Arr("Nodes").ToDictionary(n => n.Int("Id"), n => n.Str("Name"));

            bool changed = false;
            double baseScore = -1;
            var bought = new List<string>();
            for (int guard = 0; guard < 200; guard++)
            {
                var (status, preview) = await _client.PostAsync("/api/v1/skilltree/preview", new { Levels = levels, Slot = 0, Half = half }, token);
                if (status is < 200 or >= 300 || preview is not { } p) break;
                if (p.Str("Reason").Length > 0) break;
                int left = p.Int("PointsLeft");
                var open = p.Arr("Nodes")
                    .Where(n => !n.Has("Blocked"))
                    .Select(n => (Id: n.Int("Id"), Cost: n.Int("NextCost")))
                    .Where(n => n.Cost > 0 && n.Cost <= left)
                    .ToList();
                if (open.Count == 0) break;

                (int Id, int Cost) pick = default;
                if (half == "war")
                {
                    var ranks = Ranks(levels);
                    var scenarios = new List<object> { new { SkillRanks = ranks } };
                    foreach (var n in open.Take(63))
                    {
                        var r = new Dictionary<string, int>(ranks) { [n.Id.ToString()] = levels[n.Id] + 1 };
                        scenarios.Add(new { SkillRanks = r });
                    }
                    var answers = await WhatIfAsync(scenarios, token);
                    if (answers != null)
                    {
                        baseScore = BuildScore(answers[0]);
                        var gains = open.Take(63).Select((n, i) => (n, Gain: (BuildScore(answers[i + 1]) - baseScore) / n.Cost)).ToList();
                        var best = gains.OrderByDescending(g => g.Gain).First();
                        if (best.Gain > 0.0001) pick = best.n;
                    }
                }
                if (pick.Cost == 0)
                    pick = open.OrderBy(n => rowOf.GetValueOrDefault(n.Id, 99)).ThenBy(n => n.Cost).ThenBy(n => n.Id).First();

                levels[pick.Id]++;
                bought.Add(nameOf.GetValueOrDefault(pick.Id, pick.Id.ToString()));
                changed = true;
            }

            if (!changed) return;
            var (commitStatus, result) = await _client.PostAsync("/api/v1/skilltree/commit", new { Levels = levels, Half = half }, token);
            string why = result is { } r2 && r2.Str("Reason").Length > 0 ? r2.Str("Reason") : "ok";
            Event($"skill tree ({half}) commit {commitStatus}: {why}; bought {string.Join(", ", bought)}");
            _projectionDirty = true;
        }

        private static Dictionary<string, int> Ranks(int[] levels)
        {
            var d = new Dictionary<string, int>();
            for (int i = 0; i < levels.Length && i < SkillRankBuffer.Length; i++)
                if (levels[i] > 0) d[i.ToString()] = levels[i];
            return d;
        }

        // ------------------------------------------------------------------
        // Gear
        // ------------------------------------------------------------------

        private sealed record Piece(long Id, string BaseId, int Tier, bool Equipped, int ByChar, int InSlot, int Vault, bool Locked);

        private List<Piece> Pieces()
        {
            if (_inventory is not { } inv) return new();
            return inv.Arr("Equipment").Select(e => new Piece(
                e.Long("Id"), e.Str("BaseItemId"), e.Int("QualityTier"), e.Bool("IsEquipped"),
                e.Int("EquippedByCharacterSlot", -1), e.Int("EquippedInSlotIndex", -1), e.Int("VaultLockRegion"), e.Bool("IsLocked"))).ToList();
        }

        private static double PieceScore(string baseItemId, int tier)
        {
            if (!ContentRegistry.TryGetItemDefinitionByBaseId(baseItemId, out var def)) return 0;
            return (def.FlatAttackPower + def.FlatDefenseRating) * RarityTier.PowerMultiplier(tier);
        }

        private int LifeHighestRegion => _inventory is { } inv ? inv.Int("LifeHighestRegion") : 0;

        private bool Wearable(Piece p)
            => !p.Equipped && (p.Vault == 0 || p.Vault <= LifeHighestRegion)
               && EquipmentAttributeGate.CanWear(p.BaseId, _state.STR, _state.DEX, _state.CON, _state.LCK);

        /// <summary>
        /// For each combat slot, the three best unworn candidates by base
        /// power are projected against the worn set; the best gain of more than
        /// 1% is worn. Tools (slots 8-10) have no combat effect and are worn
        /// by region tier, then rarity.
        /// </summary>
        private async Task ChooseGearAsync(CancellationToken token)
        {
            var pieces = Pieces();
            if (pieces.Count == 0) return;
            var worn = pieces.Where(p => p.Equipped && p.ByChar == 0 && p.InSlot >= 0).GroupBy(p => p.InSlot).ToDictionary(g => g.Key, g => g.First());

            // Tools first: no projection needed.
            foreach (int toolSlot in new[] { EquipmentSlotEngine.SlotAxe, EquipmentSlotEngine.SlotPickaxe, EquipmentSlotEngine.SlotRod })
            {
                var best = pieces.Where(p => Wearable(p) && EquipmentSlotEngine.ResolveSlotIndex(p.BaseId) == toolSlot)
                    .OrderByDescending(p => ItemRegion(p.BaseId)).ThenByDescending(p => p.Tier).FirstOrDefault();
                if (best == null) continue;
                if (worn.TryGetValue(toolSlot, out var current) && (ItemRegion(current.BaseId), current.Tier).CompareTo((ItemRegion(best.BaseId), best.Tier)) >= 0) continue;
                Enqueue(new ClientCommandPacket { Command = CommandType.EquipItem, TargetId = best.Id }, $"equip tool {best.BaseId} tier {best.Tier}", 20, () => _inventoryDirty = true);
                return;
            }

            var candidates = pieces.Where(Wearable)
                .Select(p => (Piece: p, Slot: EquipmentSlotEngine.ResolveSlotIndex(p.BaseId)))
                .Where(c => c.Slot is >= 0 and <= EquipmentSlotEngine.SlotRing && !_gearTried.Contains(c.Piece.Id))
                .GroupBy(c => c.Slot)
                .SelectMany(g => g.OrderByDescending(c => PieceScore(c.Piece.BaseId, c.Piece.Tier)).Take(3))
                .ToList();
            if (candidates.Count == 0) return;

            var scenarios = new List<object> { new { } };
            scenarios.AddRange(candidates.Take(63).Select(c => (object)new { Wear = new[] { c.Piece.Id } }));
            var answers = await WhatIfAsync(scenarios, token);
            if (answers == null)
            {
                // No projection: the loot row's rule, as before.
                var fallback = candidates.Where(c => !worn.TryGetValue(c.Slot, out var cur) || PieceScore(c.Piece.BaseId, c.Piece.Tier) > PieceScore(cur.BaseId, cur.Tier))
                    .OrderByDescending(c => PieceScore(c.Piece.BaseId, c.Piece.Tier)).FirstOrDefault();
                if (fallback.Piece != null)
                    Enqueue(new ClientCommandPacket { Command = CommandType.EquipItem, TargetId = fallback.Piece.Id }, $"equip {fallback.Piece.BaseId} tier {fallback.Piece.Tier} (base power; no projection)", 20, () => { _inventoryDirty = true; _projectionDirty = true; });
                return;
            }

            double baseScore = BuildScore(answers[0]);
            var scored = candidates.Take(63).Select((c, i) => (c, Score: BuildScore(answers[i + 1]))).ToList();
            foreach (var s in scored.Where(s => s.Score <= baseScore * 1.01)) _gearTried.Add(s.c.Piece.Id);
            var pick = scored.Where(s => s.Score > baseScore * 1.01).OrderByDescending(s => s.Score).FirstOrDefault();
            if (pick.c.Piece == null) return;
            _gearTried.Clear(); // the worn set changes, so every old verdict is stale
            Enqueue(new ClientCommandPacket { Command = CommandType.EquipItem, TargetId = pick.c.Piece.Id },
                $"equip {pick.c.Piece.BaseId} tier {pick.c.Piece.Tier} in slot {pick.c.Slot}: projected score {baseScore:0} -> {pick.Score:0}",
                20, () => { _inventoryDirty = true; _projectionDirty = true; });
        }

        private static int ItemRegion(string baseId)
            => ContentRegistry.TryGetItemDefinitionByBaseId(baseId, out var d) ? Math.Max(1, (int)d.RegionTier) : 1;
    }
}
