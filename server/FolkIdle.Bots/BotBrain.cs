using System.Text.Json;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Bots
{
    /// <summary>
    /// The "optimal" persona: plays the way a careful player who follows the
    /// game's own advice plays, and nothing the client does not offer.
    ///
    /// Each decision mirrors something a screen already recommends, so the bot
    /// measures the game as it is presented, not a strategy nobody is shown:
    /// - the fight is Combat's "best XP/h" pick (huntingEstimate.ts bestPick)
    ///   from the server's own HuntingProjection, plus the region boss as soon
    ///   as that projection says the fight holds;
    /// - a drop is worn when it beats the worn piece by the loot row's rule
    ///   (lootCompare.ts isUpgrade: base ATK + DEF scaled by rarity, then tier);
    /// - the larder is loaded the way Larder's one-press "Load" loads it;
    /// - attribute points first meet the gear requirement of the region being
    ///   fought and the next one, then go 2:1 into Might and Vigour;
    /// - skill points buy the cheapest open node, lowest row first;
    /// - finished quest steps are claimed, and the chest sells Common drops on
    ///   the way in so the backpack never fills.
    ///
    /// Modul: ONE COMMAND PER DECISION, WITH JITTER. ValidateCommand's 100 ms
    /// rule reads a burst as something other than a person, and the macro
    /// detector bans on a low coefficient of variation in command timing. The
    /// loop sends at most one WebSocket command per pass and sleeps a random
    /// 1.2-3.0 s between passes.
    /// </summary>
    public sealed class BotBrain
    {
        private const int CommandSpacingMinMs = 1200;
        private const int CommandSpacingMaxMs = 3000;
        private static readonly TimeSpan InventoryRefresh = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ProjectionRefresh = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan QuestRefresh = TimeSpan.FromSeconds(60);

        private readonly BotClient _client;
        private readonly MilestoneLog _log;
        private readonly Random _rng;
        private readonly string _name;

        private JsonElement? _inventory;
        private DateTime _inventoryAt = DateTime.MinValue;
        private JsonElement? _projection;
        private DateTime _projectionAt = DateTime.MinValue;
        private DateTime _questsAt = DateTime.MinValue;
        private int _projectionLevel = -1;
        private bool _chestRuleSet;
        private int _skillTreeTriedAtPoints = -1;
        private readonly HashSet<long> _refusedMonsters = new();
        private long _pendingActivity;
        private DateTime _pendingActivityAt;

        private readonly Persona _persona;
        private bool _wasCheckedIn = true;
        private bool _cappedThisGap;

        public BotBrain(BotClient client, MilestoneLog log, int seed, string name, Persona persona)
        {
            _client = client;
            _log = log;
            _rng = new Random(seed);
            _name = name;
            _persona = persona;
        }

        public async Task RunAsync(DateTime stopAtUtc, CancellationToken token)
        {
            while (!token.IsCancellationRequested && DateTime.UtcNow < stopAtUtc && _client.IsOpen)
            {
                if (_client.TryGetState(out var state))
                {
                    _log.Observe(_name, state);
                    try
                    {
                        double gameHours = _log.GameHours;
                        bool checkedIn = _persona.IsCheckedIn(gameHours);
                        if (checkedIn != _wasCheckedIn)
                        {
                            _wasCheckedIn = checkedIn;
                            _cappedThisGap = false;
                            _log.Event(_name, checkedIn ? $"check-in (level {state.CurrentLevel})" : "away");
                            if (checkedIn)
                            {
                                // Fresh eyes: everything is looked at again.
                                _inventoryAt = _projectionAt = _questsAt = DateTime.MinValue;
                                _skillTreeTriedAtPoints = -1;
                            }
                        }

                        if (checkedIn)
                        {
                            await RestChoresAsync(state, token);
                            await OneCommandAsync(state, token);
                        }
                        else if (!_cappedThisGap && state.OfflineCapSeconds > 0
                                 && _persona.HoursAway(gameHours) * 3600 > state.OfflineCapSeconds)
                        {
                            // The offline cap: a session away this long would earn nothing more.
                            _cappedThisGap = true;
                            if (state.ActiveActivityId != 0 && state.Slot1_CharacterId != Guid.Empty)
                            {
                                await _client.SendCommandAsync(new ClientCommandPacket { Command = CommandType.ChangeActivity, TargetId = 0, TargetGuid = state.Slot1_CharacterId }, token);
                                _log.Event(_name, $"offline cap reached ({state.OfflineCapSeconds / 3600.0:0.#} h): activity stopped until the next check-in");
                                _log.Count(_name, "offline_caps_hit");
                            }
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        Console.WriteLine($"[{_name}] REST failed: {ex.Message}");
                    }
                }
                await Task.Delay(_rng.Next(CommandSpacingMinMs, CommandSpacingMaxMs), token);
            }
        }

        // ------------------------------------------------------------------
        // REST: what a player does on screens without a WebSocket command
        // ------------------------------------------------------------------

        private async Task RestChoresAsync(StateUpdatePacket state, CancellationToken token)
        {
            if (!_chestRuleSet)
            {
                // Sell Common (tier 1) and below on the way in - the safe rule
                // the "Set an auto-sell rule" quest step teaches.
                var (status, _) = await _client.PostAsync("/api/v1/chest/settings", new { AutoSalvageBelowTier = 1 }, token);
                _chestRuleSet = status is >= 200 and < 300;
            }

            if (DateTime.UtcNow - _inventoryAt > InventoryRefresh)
            {
                _inventory = await _client.GetAsync("/api/v1/player/inventory", token);
                _inventoryAt = DateTime.UtcNow;
            }

            if (DateTime.UtcNow - _projectionAt > ProjectionRefresh || state.CurrentLevel != _projectionLevel)
            {
                _projection = await _client.GetAsync("/api/v1/combat/projection?slot=0", token);
                _projectionAt = DateTime.UtcNow;
                _projectionLevel = state.CurrentLevel;
            }

            if (DateTime.UtcNow - _questsAt > QuestRefresh)
            {
                _questsAt = DateTime.UtcNow;
                await ClaimQuestsAsync(token);
            }

            if (state.AvailableSkillPoints > 0 && state.AvailableSkillPoints != _skillTreeTriedAtPoints)
            {
                _skillTreeTriedAtPoints = state.AvailableSkillPoints;
                await SpendSkillPointsAsync(token);
            }
        }

        private async Task ClaimQuestsAsync(CancellationToken token)
        {
            var line = await _client.GetAsync("/api/v1/quests", token);
            if (line is not { } root || !root.TryGetProperty("Steps", out var steps)) return;
            foreach (var step in steps.EnumerateArray())
            {
                if (!step.TryGetProperty("Claimable", out var claimable) || !claimable.GetBoolean()) continue;
                string id = step.GetProperty("Id").GetString() ?? "";
                var (status, _) = await _client.PostAsync($"/api/v1/quests/{Uri.EscapeDataString(id)}/claim", new { }, token);
                if (status is >= 200 and < 300) _log.Event(_name, $"quest claimed: {id}");
            }
        }

        /// <summary>
        /// Cheapest open node, lowest row first, through the server's own
        /// preview - the bot never decides legality itself.
        /// </summary>
        private async Task SpendSkillPointsAsync(CancellationToken token)
        {
            var treeState = await _client.GetAsync("/api/v1/skilltree/state", token);
            if (treeState is not { } st) return;
            int[] levels = st.GetProperty("Levels").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var definition = await _client.GetAsync("/api/v1/skilltree", token);
            if (definition is not { } def) return;
            var rowOf = def.GetProperty("Nodes").EnumerateArray().ToDictionary(n => n.GetProperty("Id").GetInt32(), n => n.GetProperty("Row").GetInt32());

            bool changed = false;
            for (int guard = 0; guard < 200; guard++)
            {
                var (status, preview) = await _client.PostAsync("/api/v1/skilltree/preview", new { Levels = levels, Slot = 0, Half = "war" }, token);
                if (status is < 200 or >= 300 || preview is not { } p) break;
                if (p.TryGetProperty("Reason", out var reason) && reason.ValueKind == JsonValueKind.String) break;
                int left = p.GetProperty("PointsLeft").GetInt32();
                var pick = p.GetProperty("Nodes").EnumerateArray()
                    .Where(n => n.GetProperty("Blocked").ValueKind == JsonValueKind.Null)
                    .Select(n => (Id: n.GetProperty("Id").GetInt32(), Cost: n.GetProperty("NextCost").GetInt32()))
                    .Where(n => n.Cost > 0 && n.Cost <= left)
                    .OrderBy(n => rowOf.GetValueOrDefault(n.Id, 99)).ThenBy(n => n.Cost).ThenBy(n => n.Id)
                    .FirstOrDefault();
                if (pick.Cost == 0) break;
                levels[pick.Id]++;
                changed = true;
            }

            if (!changed) return;
            var (commitStatus, result) = await _client.PostAsync("/api/v1/skilltree/commit", new { Levels = levels, Half = "war" }, token);
            string why = result is { } r && r.TryGetProperty("Reason", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString()! : "ok";
            _log.Event(_name, $"skill tree commit {commitStatus}: {why}");
        }

        // ------------------------------------------------------------------
        // WebSocket: at most one command per pass, most urgent first
        // ------------------------------------------------------------------

        private async Task OneCommandAsync(StateUpdatePacket state, CancellationToken token)
        {
            if (state.UnspentAttributePoints > 0 && TryAttributeCommand(state, out var attribute))
            {
                await _client.SendCommandAsync(attribute, token);
                return;
            }
            // While fishing the catch stays in the chest until the trip is
            // done, or every fish would go straight to the larder and the trip
            // would never end.
            if (!_fishing && TryFoodCommand(state, out var food))
            {
                await _client.SendCommandAsync(food, token);
                _inventoryAt = DateTime.MinValue;
                return;
            }
            if (TryEquipCommand(state, out var equip))
            {
                await _client.SendCommandAsync(equip, token);
                _inventoryAt = DateTime.MinValue;
                _projectionAt = DateTime.MinValue;
                return;
            }
            if (TryFightCommand(state, out var fight))
            {
                await _client.SendCommandAsync(fight, token);
            }
        }

        private static readonly string[] AttributeNames = { "STR", "DEX", "CON", "LCK" };

        private bool TryAttributeCommand(StateUpdatePacket state, out ClientCommandPacket packet)
        {
            packet = default;
            int[] attrs = { state.STR, state.DEX, state.CON, state.LCK };
            int region = Math.Max(1, (int)state.HighestUnlockedRegion);
            int points = state.UnspentAttributePoints;

            // Requirement of the gear being fought for, then of the next region's.
            foreach (int targetRegion in new[] { region, region + 1 })
            {
                int need = EquipmentAttributeGate.RequirementFor(targetRegion);
                int lowest = Enumerable.Range(0, 4).OrderBy(i => attrs[i]).First();
                if (attrs[lowest] < need)
                {
                    int amount = Math.Min(points, need - attrs[lowest]);
                    packet = new ClientCommandPacket { Command = CommandType.SpendAttributePoint, TargetId = lowest, LimitPrice = amount };
                    return true;
                }
            }

            // The rest 2:1 into Might (damage) and Vigour (health).
            int id = attrs[0] <= attrs[2] * 2 ? 0 : 2;
            packet = new ClientCommandPacket { Command = CommandType.SpendAttributePoint, TargetId = id, LimitPrice = points };
            _log.Event(_name, $"attributes: {points} into {AttributeNames[id]}");
            return true;
        }

        private const int LarderSlotCapacity = 200;

        private bool TryFoodCommand(StateUpdatePacket state, out ClientCommandPacket packet)
        {
            packet = default;
            if (_inventory is not { } inv) return false;
            var slots = new (int Index, int ItemId, int Count)[]
            {
                (0, state.Food1_ItemId, state.Food1_Count),
                (1, state.Food2_ItemId, state.Food2_Count),
                (2, state.Food3_ItemId, state.Food3_Count),
            };

            // Larder.svelte's quickLoad: the biggest food stack, into the slot
            // already holding it or the first empty one, up to capacity.
            var foods = inv.GetProperty("Stacks").EnumerateArray()
                .Select(s => (BaseId: s.GetProperty("ItemId").GetString() ?? "", Quantity: s.GetProperty("Quantity").GetInt32()))
                .Select(s => (s.BaseId, s.Quantity, Id: ContentRegistry.TryGetItemDefinitionByBaseId(s.BaseId, out var d) ? d.Id : 0))
                .Where(s => s.Id > 0 && s.Quantity > 0 && FoodRegistry.IsFood(s.Id))
                .OrderByDescending(s => s.Quantity);
            foreach (var food in foods)
            {
                var target = slots.FirstOrDefault(sl => sl.ItemId == food.Id && sl.Count < LarderSlotCapacity);
                if (target.ItemId == 0) target = slots.FirstOrDefault(sl => sl.ItemId == 0);
                if (target.ItemId == 0 && slots.All(sl => sl.ItemId != 0)) continue;
                int room = LarderSlotCapacity - (target.ItemId == food.Id ? target.Count : 0);
                int quantity = Math.Min(food.Quantity, room);
                if (quantity <= 0) continue;
                _log.Event(_name, $"larder: {quantity} x {food.BaseId} into slot {target.Index}");
                packet = new ClientCommandPacket
                {
                    Command = CommandType.StockFoodSlot,
                    ConsumableItemId = (uint)food.Id,
                    TargetSlotIndex = (uint)target.Index,
                    DepositQuantity = (uint)quantity,
                };
                return true;
            }
            return false;
        }

        private static double PieceScore(string baseItemId, int tier)
        {
            if (!ContentRegistry.TryGetItemDefinitionByBaseId(baseItemId, out var def)) return 0;
            return (def.FlatAttackPower + def.FlatDefenseRating) * RarityTier.PowerMultiplier(tier);
        }

        private bool TryEquipCommand(StateUpdatePacket state, out ClientCommandPacket packet)
        {
            packet = default;
            if (_inventory is not { } inv) return false;
            int lifeRegion = inv.TryGetProperty("LifeHighestRegion", out var lr) && lr.ValueKind == JsonValueKind.Number ? lr.GetInt32() : 0;

            var pieces = inv.GetProperty("Equipment").EnumerateArray().Select(e => new
            {
                Id = e.GetProperty("Id").GetInt64(),
                BaseId = e.GetProperty("BaseItemId").GetString() ?? "",
                Tier = e.GetProperty("QualityTier").GetInt32(),
                Equipped = e.GetProperty("IsEquipped").GetBoolean(),
                ByChar = e.GetProperty("EquippedByCharacterSlot").GetInt32(),
                InSlot = e.GetProperty("EquippedInSlotIndex").GetInt32(),
                Vault = e.TryGetProperty("VaultLockRegion", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0,
            }).ToList();

            var worn = pieces.Where(p => p.Equipped && p.ByChar == 0 && p.InSlot >= 0).ToDictionary(p => p.InSlot, p => p);
            var best = pieces
                .Where(p => !p.Equipped && (p.Vault == 0 || p.Vault <= lifeRegion))
                .Select(p => new { Piece = p, Slot = EquipmentSlotEngine.ResolveSlotIndex(p.BaseId), Score = PieceScore(p.BaseId, p.Tier) })
                .Where(c => c.Slot >= 0 && EquipmentAttributeGate.CanWear(c.Piece.BaseId, state.STR, state.DEX, state.CON, state.LCK))
                .Where(c =>
                {
                    if (!worn.TryGetValue(c.Slot, out var current)) return true;
                    double now = PieceScore(current.BaseId, current.Tier);
                    return c.Score > now || (c.Score == now && c.Piece.Tier > current.Tier);
                })
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();
            if (best == null) return false;

            packet = new ClientCommandPacket { Command = CommandType.EquipItem, TargetId = best.Piece.Id };
            _log.Event(_name, $"equip {best.Piece.BaseId} tier {best.Piece.Tier} in slot {best.Slot}");
            return true;
        }

        /// <summary>Food the bot fishes up to before it goes back to fighting: one larder slot.</summary>
        private const int FishingTarget = LarderSlotCapacity;

        private bool _fishing;

        private int FoodInInventory()
        {
            if (_inventory is not { } inv) return 0;
            return inv.GetProperty("Stacks").EnumerateArray()
                .Select(s => (BaseId: s.GetProperty("ItemId").GetString() ?? "", Quantity: s.GetProperty("Quantity").GetInt32()))
                .Where(s => ContentRegistry.TryGetItemDefinitionByBaseId(s.BaseId, out var d) && FoodRegistry.IsFood(d.Id))
                .Sum(s => s.Quantity);
        }

        /// <summary>
        /// Fight, or fish when no fight holds without food and there is no food
        /// left - the loop the larder makes of the game. A new account starts
        /// with ten fish and, measured 2026-11-01, cannot kill a Field Mouse
        /// without food once they are eaten (HuntingProjection: 0 kills before
        /// death), so a bot that never fishes measures nothing past its first
        /// few kills.
        /// </summary>
        private bool TryFightCommand(StateUpdatePacket state, out ClientCommandPacket packet)
        {
            packet = default;
            if (state.Slot1_CharacterId == Guid.Empty) return false;

            if (_pendingActivity != 0)
            {
                if (state.ActiveActivityId == _pendingActivity)
                {
                    _pendingActivity = 0; // confirmed
                }
                else if (DateTime.UtcNow - _pendingActivityAt > TimeSpan.FromSeconds(8))
                {
                    // Not taken. A death also clears the activity, and is not a refusal.
                    if (state.ActivityHaltReason != ActivityHaltReason.Died)
                    {
                        _refusedMonsters.Add(_pendingActivity);
                        _log.Event(_name, $"activity {_pendingActivity} was refused (halt {state.ActivityHaltReason}, result {state.CommandResult0_Code}); skipping it");
                    }
                    _pendingActivity = 0;
                }
                else
                {
                    return false;
                }
            }

            int larder = state.Food1_Count + state.Food2_Count + state.Food3_Count;
            int carried = FoodInInventory();
            long target;
            if (_fishing)
            {
                if (carried >= FishingTarget)
                {
                    _fishing = false;
                    _log.Event(_name, $"fishing done: {carried} food carried");
                    return false; // the food command loads it on the next pass
                }
                target = ChooseFishingNode(state);
            }
            else
            {
                long scout = ChooseScout(state, larder > 0);
                target = scout > 0 ? scout : ChooseMonster(state, larder > 0);
                if (target <= 0 && larder == 0 && carried == 0)
                {
                    _fishing = true;
                    _log.Event(_name, $"no fight holds without food and the larder is empty: going fishing (level {state.CurrentLevel})");
                    _log.Count(_name, "fishing_trips");
                    target = ChooseFishingNode(state);
                }
            }

            if (target <= 0 || target == state.ActiveActivityId) return false;

            _pendingActivity = target;
            _pendingActivityAt = DateTime.UtcNow;
            packet = new ClientCommandPacket { Command = CommandType.ChangeActivity, TargetId = target, TargetGuid = state.Slot1_CharacterId };
            _log.Event(_name, _fishing
                ? $"fish at node {target}"
                : $"fight monster {target} ({MonsterName(target)}) at HP {state.PlayerHp}/{state.PlayerMaxHp}, level {state.CurrentLevel}, larder {larder}");
            return true;
        }

        /// <summary>
        /// The best fishing node the character can reach: the highest location
        /// not past the snapshot's HighestLocationReached (the rule
        /// ChangeActivity and AutomationRules apply), since a region's fish
        /// heal by that region's tier. A refused node is skipped.
        /// </summary>
        private long ChooseFishingNode(StateUpdatePacket state)
        {
            var nodes = ContentRegistry.GatheringNodes;
            long best = 0;
            int bestLocation = 0;
            int reach = Math.Max(1, (int)state.HighestLocationReached);
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].ProfessionType != 2 || _refusedMonsters.Contains(nodes[i].ActivityId)) continue;
                int location = ContentRegistry.GetNodeLocation(nodes[i].ActivityId);
                if (location < 1 || location > reach) continue;
                if (location > bestLocation)
                {
                    best = nodes[i].ActivityId;
                    bestLocation = location;
                }
            }
            return best;
        }

        private int _scoutFailedAtLevel = -100;
        private const int ScoutRetryLevels = 2;

        /// <summary>
        /// One kill in the next location, to open its gathering.
        ///
        /// Modul: THE REGION-2 CATCH-22 (24 h run, 2026-11-01). A location's
        /// fishing opens on the first kill THERE (ApplyKillProgression), and
        /// region 2's fights need region 2's fish: on region-1 fish all three
        /// bots were told "you would die" by every region-2 monster and farmed
        /// region 1 to level 30 with region 2 open since level 18-28. A sensible
        /// player tries one fight at full health to get through that door, so
        /// the bot does: the first monster of the next location, when the
        /// projection gives at least one kill before death (or holds with
        /// food). A death there waits two levels before the next try.
        /// </summary>
        private long ChooseScout(StateUpdatePacket state, bool hasFood)
        {
            int reached = Math.Max(1, (int)state.HighestLocationReached);
            int open = Math.Max(1, (int)state.HighestUnlockedRegion);
            if (reached >= open) return 0;

            int firstMonster = ContentRegistry.FirstCanonicalMonsterId + reached * ContentRegistry.MonstersPerRegion;
            if (state.ActivityHaltReason == ActivityHaltReason.Died && state.LastDeathMonsterId == firstMonster
                && _scoutFailedAtLevel != state.CurrentLevel)
            {
                _scoutFailedAtLevel = state.CurrentLevel;
                _log.Event(_name, $"scouting location {reached + 1} failed at level {state.CurrentLevel}; next try at level {state.CurrentLevel + ScoutRetryLevels}");
                _log.Count(_name, "scout_deaths");
            }
            if (state.CurrentLevel < _scoutFailedAtLevel + ScoutRetryLevels) return 0;

            if (_projection is not { } proj || !proj.TryGetProperty("Monsters", out var monsters)) return 0;
            foreach (var e in monsters.EnumerateArray())
            {
                if (e.GetProperty("MonsterId").GetInt32() != firstMonster) continue;
                bool canDamage = e.GetProperty("CanDamage").GetBoolean();
                int killsBeforeDeath = e.TryGetProperty("KillsBeforeDeathWithoutFood", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : 0;
                bool holdsWithFood = hasFood && e.GetProperty("SurvivesWithFood").GetBoolean();
                if (canDamage && (killsBeforeDeath >= 1 || holdsWithFood || hasFood))
                {
                    if (state.ActiveActivityId != firstMonster)
                        _log.Event(_name, $"scouting location {reached + 1}: one fight with {MonsterName(firstMonster)} to open its gathering (projected kills before death {killsBeforeDeath}, food {hasFood})");
                    return firstMonster;
                }
            }
            return 0;
        }

        private long ChooseMonster(StateUpdatePacket state, bool hasFood)
        {
            if (_projection is not { } proj || !proj.TryGetProperty("Monsters", out var monsters)) return 0;
            int region = Math.Max(1, (int)state.HighestUnlockedRegion);

            var estimates = monsters.EnumerateArray().Select(e => new
            {
                Id = (long)e.GetProperty("MonsterId").GetInt32(),
                CanDamage = e.GetProperty("CanDamage").GetBoolean(),
                Safe = e.GetProperty("SurvivesWithoutFood").GetBoolean(),
                WithFood = e.GetProperty("SurvivesWithFood").GetBoolean(),
                XpPerHour = e.GetProperty("XpPerHour").GetDouble(),
            })
            .Where(e => !_refusedMonsters.Contains(e.Id) && MonsterRegion(e.Id) is > 0 and var r && r <= region)
            .ToList();

            // The region boss of the highest open region, once the fight holds.
            int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
            if (bossId > 0 && !BossFirstClearRules.IsDefeated(state.DefeatedRegionBossMask, bossId))
            {
                var boss = estimates.FirstOrDefault(e => e.Id == bossId);
                if (boss != null && boss.CanDamage && (boss.Safe || (hasFood && boss.WithFood))) return bossId;
            }

            // huntingEstimate.ts bestPick: most XP/h among fights that hold;
            // ties keep the earlier (easier) monster.
            var best = estimates
                .Where(e => e.CanDamage && (e.Safe || (hasFood && e.WithFood)))
                .OrderByDescending(e => e.XpPerHour).ThenBy(e => e.Id)
                .FirstOrDefault();
            if (best != null) return best.Id;

            // Nothing holds. With food in hand the easiest monster still pays
            // (and the deaths column shows the cost); without it, 0 sends the
            // bot fishing.
            return hasFood ? estimates.Where(e => e.CanDamage).OrderBy(e => e.Id).Select(e => e.Id).FirstOrDefault() : 0;
        }

        private static int MonsterRegion(long monsterId)
        {
            var all = ContentRegistry.Monsters;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Id == monsterId) return all[i].RegionTier;
            }
            return 0;
        }

        private static string MonsterName(long monsterId)
        {
            try { return ContentRegistry.GetMonsterName((int)monsterId); }
            catch { return "?"; }
        }
    }
}
