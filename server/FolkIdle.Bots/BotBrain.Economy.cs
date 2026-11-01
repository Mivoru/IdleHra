using System.Text.Json;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Bots
{
    /// <summary>
    /// The village, crafting (tools and potions), the potion shelves, the
    /// forge, the Workshop, the Great Works, and the small claims a player
    /// makes on the way past: loot chests, mail, quest steps, achievements.
    ///
    /// Modul: ONE MAIN CHARACTER, SO MATERIALS COST FIGHTING TIME. Until the
    /// roster has a second character, every log, ore and herb is gathered by
    /// the character that fights. A chore that is short of a gatherable
    /// material sets a MATERIAL GOAL; ChooseActivity turns it into a trip of
    /// at most MaterialTripMinutes, and the next material trip waits three
    /// times as long as the last one took (at least 20 game minutes) - so the
    /// bot spends at most about a quarter of its time gathering, as
    /// GatheringShareTests' own design target (a fifth at the river) intends.
    /// A fixed two-hour wait was tried first: a Forge needs logs AND ore, the
    /// logs took 9 game minutes, and the ore then waited two hours.
    /// </summary>
    public sealed partial class BotBrain
    {
        private const double MaterialTripMinutes = 45;
        private double _lastMaterialTripHours = 0.1;
        private double MaterialTripCooldownHours => Math.Max(20.0 / 60, _lastMaterialTripHours * 3);

        private sealed record MaterialGoal(int ItemId, int Quantity, string Purpose);
        private MaterialGoal? _materialGoal;

        private bool _chestRuleSet;
        private int _chestRuleRegion;
        private readonly Dictionary<int, double> _buildingSentAt = new();
        private double _lastPotionDrunk = -10;
        private int _rerollAffixCursor;

        private const int ProfessionWoodcutting = 0, ProfessionMining = 1, ProfessionHerbalism = 3;

        private async Task EconomyChoresAsync(CancellationToken token)
        {
            if (!_chestRuleSet || _chestRuleRegion != _state.HighestUnlockedRegion) await ChestRuleAsync(token);
            if (Due("quests", 30)) await ClaimQuestsAsync(token);
            if (Due("achievements", 60)) await ClaimAchievementsAsync(token);
            if (Due("mail", 120)) await ClaimMailAsync(token);
            if (Due("lootchests", 60)) await OpenLootChestsAsync(token);
            if (Due("village", 30)) await VillageAsync(token);
            if (Due("crafting", 30)) await CraftAsync(token);
            if (Due("potions", 10)) PotionShelves();
            if (_state.ForgeLevel > 0 && Due("forge", 60)) await ForgeAsync(token);
            if (Due("workshop", 120)) await WorkshopAsync(token);
            if (Due("greatworks", 360)) await GreatWorksAsync(token);
            if (Due("bulksell", 360)) await BulkSellAsync(token);
        }

        // ------------------------------------------------------------------
        // Material goals and the trips they become
        // ------------------------------------------------------------------

        /// <summary>The best reachable node of any gathering kind that drops <paramref name="itemId"/>.</summary>
        private long NodeDropping(int itemId)
        {
            foreach (int profession in new[] { ProfessionWoodcutting, ProfessionMining, ProfessionHerbalism, ProfessionFishing })
            {
                long node = NodeFor(itemId, profession);
                if (node > 0) return node;
            }
            return 0;
        }

        private void WantMaterial(int itemId, int quantity, string purpose)
        {
            if (itemId <= 0 || quantity <= 0) return;
            // The first goal of a pass wins; chores run in priority order.
            if (_materialGoal != null && _materialGoal.ItemId != itemId && _materialGoalSetAt == Now) return;
            if (NodeDropping(itemId) <= 0) return;
            _materialGoal = new MaterialGoal(itemId, quantity, purpose);
            _materialGoalSetAt = Now;
        }

        private double _materialGoalSetAt;

        /// <summary>Called by ChooseActivity: a material trip, when one is wanted and allowed.</summary>
        private long MaterialTrip()
        {
            if (_materialGoal is not { } goal) return 0;
            if (Now - _lastMaterialTripEnded < MaterialTripCooldownHours) return 0;
            int held = Held(goal.ItemId);
            if (held >= goal.Quantity) { _materialGoal = null; return 0; }
            long node = NodeDropping(goal.ItemId);
            if (node <= 0) { _materialGoal = null; return 0; }
            StartTrip(new Trip(node, $"gathering {ContentRegistry.GetItemBaseId(goal.ItemId)} for {goal.Purpose}", goal.ItemId, goal.Quantity, Now, MaterialTripMinutes));
            _materialGoal = null;
            return node;
        }

        // ------------------------------------------------------------------
        // Chest rules
        // ------------------------------------------------------------------

        /// <summary>
        /// Before the Forge: sell Common and below on the way in, the rule the
        /// "Set an auto-sell rule" quest step teaches. With a Forge, spares are
        /// fusion fodder, so only gear of regions two or more below the open one
        /// is sold on the way in (up to Rare).
        /// </summary>
        private async Task ChestRuleAsync(CancellationToken token)
        {
            int open = Math.Max(1, (int)_state.HighestUnlockedRegion);
            object body = _state.ForgeLevel == 0
                ? new { AutoSalvageBelowTier = 1 }
                : new { AutoSalvageBelowTier = 0, AutoSalvageRegionTiers = Enumerable.Range(1, 5).Select(r => r <= open - 2 ? 3 : 0).ToArray() };
            var (status, _) = await _client.PostAsync("/api/v1/chest/settings", body, token);
            _chestRuleSet = status is >= 200 and < 300;
            _chestRuleRegion = _state.HighestUnlockedRegion;
            _chestForgeRule = _state.ForgeLevel > 0;
        }

        private bool _chestForgeRule;

        private async Task BulkSellAsync(CancellationToken token)
        {
            if (_state.ForgeLevel > 0 && !_chestForgeRule) _chestRuleSet = false;
            int open = Math.Max(1, (int)_state.HighestUnlockedRegion);
            if (open < 3) return;
            var regions = Enumerable.Range(1, open - 2).ToArray();
            var (status, body) = await _client.PostAsync("/api/v1/chest/bulk-sell", new { maxQualityTier = 3, regions }, token);
            if (status is >= 200 and < 300 && body is { } b && b.Int("RemovedCount") > 0)
            {
                Event($"bulk-sold {b.Int("RemovedCount")} old pieces (regions 1-{open - 2}, up to Rare) for {b.Long("GoldGained")} gold");
                _inventoryDirty = true;
            }
        }

        // ------------------------------------------------------------------
        // Claims
        // ------------------------------------------------------------------

        private async Task ClaimQuestsAsync(CancellationToken token)
        {
            var line = await _client.GetAsync("/api/v1/quests", token);
            if (line is not { } root) return;
            foreach (var step in root.Arr("Steps"))
            {
                if (!step.Bool("Claimable")) continue;
                string id = step.Str("Id");
                var (status, _) = await _client.PostAsync($"/api/v1/quests/{Uri.EscapeDataString(id)}/claim", new { }, token);
                if (status is >= 200 and < 300) { Event($"quest claimed: {id}"); Count("quests_claimed"); }
            }
        }

        private readonly Dictionary<int, int> _achievementClaimedTier = new();

        private async Task ClaimAchievementsAsync(CancellationToken token)
        {
            var rows = await _client.GetAsync("/api/v1/achievements/snapshot", token);
            if (rows is not { } list) return;
            foreach (var a in list.Items())
            {
                int id = a.Int("AchievementId");
                int tier = a.Int("CompletedTier");
                bool claimable = (!a.Bool("IsClaimed") && tier > 0) || (a.Int("NextTierTarget") > 0 && a.Long("CurrentProgress") >= a.Int("NextTierTarget"));
                if (!claimable || id <= 0 || _achievementClaimedTier.GetValueOrDefault(id, -1) >= tier) continue;
                _achievementClaimedTier[id] = tier;
                Enqueue(new ClientCommandPacket { Command = CommandType.ClaimAchievementReward, TargetAchievementId = (uint)id },
                    $"claim achievement {a.Str("Title")} (tier {tier})", 60, () => Count("achievements_claimed"));
            }
        }

        private readonly HashSet<long> _mailClaimed = new();

        private async Task ClaimMailAsync(CancellationToken token)
        {
            var rows = await _client.GetAsync("/api/v1/mailbox/list", token);
            if (rows is not { } list) return;
            foreach (var m in list.Items().Take(5))
            {
                long id = m.Long("Id");
                if (id <= 0 || !_mailClaimed.Add(id)) continue;
                Enqueue(new ClientCommandPacket { Command = CommandType.ClaimMailItem, TargetId = id },
                    $"claim mail {id} ({m.Str("BaseItemId")} x{m.Int("Quantity")}, {m.Long("GoldAttachment")} gold)", 60, () => _inventoryDirty = true);
            }
        }

        private async Task OpenLootChestsAsync(CancellationToken token)
        {
            var shelf = await _client.GetAsync("/api/v1/chests", token);
            if (shelf is not { } s) return;
            foreach (var row in s.Arr("Chests"))
            {
                int count = row.Int("Count");
                if (count <= 0) continue;
                var (status, body) = await _client.PostAsync("/api/v1/chests/open", new { Tier = row.Int("Tier"), Count = Math.Min(count, 5) }, token);
                if (status is < 200 or >= 300 || body is not { } b) continue;
                var opened = b.Arr("Opened").ToList();
                long gold = opened.Sum(o => o.Long("Gold"));
                int diamonds = opened.Sum(o => o.Int("Diamonds"));
                int pieces = opened.Count(o => o.Has("Piece"));
                int tickets = opened.Count(o => o.Str("DungeonTicket").Length > 0);
                Event($"opened {opened.Count} x {row.Str("Name")}: {gold} gold, {diamonds} diamonds, {pieces} pieces, {tickets} dungeon tickets");
                for (int i = 0; i < opened.Count; i++) Count("loot_chests_opened");
                _inventoryDirty = true;
            }
        }

        // ------------------------------------------------------------------
        // The village
        // ------------------------------------------------------------------

        private const int BuildingForge = 1, BuildingInn = 2, BuildingBreeding = 3, BuildingLumberjack = 5, BuildingMine = 7, BuildingWarehouse = 8, BuildingTownHall = 9, BuildingWorkshop = 10;

        private int BuildingLevel(int id) => id switch
        {
            BuildingForge => _state.ForgeLevel,
            BuildingInn => _state.InnLevel,
            BuildingBreeding => _state.BreedingLevel,
            BuildingLumberjack => _state.LumberjackLevel,
            BuildingMine => _state.MineLevel,
            BuildingWarehouse => _state.WarehouseLevel,
            BuildingTownHall => _state.TownHallLevel,
            BuildingWorkshop => _state.CraftingWorkshopLevel,
            _ => 0,
        };

        private static string BuildingName(int id) => id switch
        {
            BuildingForge => "Forge", BuildingInn => "Inn", BuildingBreeding => "Breeding Grounds", BuildingLumberjack => "Lumberjack",
            BuildingMine => "Mine", BuildingWarehouse => "Warehouse", BuildingTownHall => "Town Hall", BuildingWorkshop => "Crafting Workshop", _ => $"building {id}",
        };

        /// <summary>
        /// How urgent a building's next level is: lower first. The Forge and the
        /// Town Hall lead (fusion, and the ceiling of everything else), the two
        /// producers follow (materials while the character fights), the Inn and
        /// the Breeding Grounds wait for the breeding gate.
        /// </summary>
        private int BuildingRank(int id, int level)
        {
            int offset = id switch
            {
                BuildingForge or BuildingTownHall => 0,
                BuildingLumberjack or BuildingMine or BuildingWorkshop => 1,
                BuildingWarehouse => 2,
                BuildingInn or BuildingBreeding => _state.CurrentLevel >= 40 ? 1 : 4,
                _ => 5,
            };
            return level + offset;
        }

        private async Task VillageAsync(CancellationToken token)
        {
            // Modul: AN UPGRADE TAKES TIME (VillageInfrastructures.UpgradeCompletesAtEpoch),
            // so the level on the snapshot rises only when it finishes. The first
            // version read "level unchanged a pass later" as a refusal and sent
            // the same upgrade again - which the server accepted, raising
            // buildings two levels at once. A building just sent is left alone for
            // 15 game minutes instead; a refused one simply costs that wait.
            var quote = await _client.GetAsync("/api/v1/village/quote", token);
            if (quote is not { } q) return;
            var options = new List<(int Id, int Level, int Rank, List<(string Item, int Need, int Held)> Lines)>();
            foreach (var b in q.Arr("Buildings"))
            {
                int id = b.Int("BuildingId");
                int level = b.Int("CurrentLevel");
                if (_buildingSentAt.TryGetValue(id, out double sentAt) && Now - sentAt < 0.25) continue;
                var lines = b.Arr("Lines").Select(l => (l.Str("ItemId"), l.Int("Quantity"), l.Int("Held"))).ToList();
                if (lines.Count == 0) continue;
                options.Add((id, level, BuildingRank(id, level), lines));
            }
            if (options.Count == 0) return;

            static (int Id, int Level, int Rank, List<(string Item, int Need, int Held)> Lines) wanted0(List<(int Id, int Level, int Rank, List<(string Item, int Need, int Held)> Lines)> o)
                => o.OrderBy(x => x.Rank).ThenBy(x => x.Id).First();

            bool Affordable(List<(string Item, int Need, int Held)> lines)
                => lines.All(l => l.Item == "gold" ? _state.Gold >= l.Need : l.Held >= l.Need);

            _goldReserve = wanted0(options).Lines.Where(l => l.Item == "gold").Sum(l => (long)l.Need);
            var pick = options.Where(o => Affordable(o.Lines)).OrderBy(o => o.Rank).ThenBy(o => o.Id).FirstOrDefault();
            var wanted = options.OrderBy(o => o.Rank).ThenBy(o => o.Id).First();
            if (pick.Lines != null && pick.Rank <= wanted.Rank + 1)
            {
                _buildingSentAt[pick.Id] = Now;
                Enqueue(new ClientCommandPacket { Command = CommandType.UpgradeBuilding, TargetBuildingId = (uint)pick.Id },
                    $"upgrade {BuildingName(pick.Id)} to {pick.Level + 1} ({string.Join(", ", pick.Lines.Select(l => $"{l.Need} {l.Item}"))})", 30,
                    () => { _inventoryDirty = true; Count("building_upgrades_sent"); });
                return;
            }

            // The building that matters most is short of something gatherable.
            if (wanted.Lines.Where(l => l.Item == "gold").All(l => _state.Gold >= l.Need))
            {
                foreach (var (item, need, held) in wanted.Lines.Where(l => l.Item != "gold" && l.Held < l.Need))
                {
                    if (!ContentRegistry.TryGetItemDefinitionByBaseId(item, out var def)) continue;
                    WantMaterial(def.Id, need - held + Held(def.Id), $"{BuildingName(wanted.Id)} {wanted.Level + 1}");
                    break;
                }
            }
        }

        /// <summary>Gold the bot keeps for the next building it wants - the delve and the forge spend only above it.</summary>
        private long GoldReserve => _goldReserve;
        private long _goldReserve;

        // ------------------------------------------------------------------
        // Crafting: tools and potions
        // ------------------------------------------------------------------

        private sealed record Recipe(int ResultId, string ResultBase, int Profession, int Level, int Mat1, int Need1, int Have1, int Mat2, int Need2, int Have2);

        private static readonly string[] ToolKinds = { "fishing_rod", "axe", "pickaxe" };

        private async Task CraftAsync(CancellationToken token)
        {
            var snapshot = await _client.GetAsync("/api/v1/crafting/recipes", token);
            if (snapshot is not { } snap) return;
            int level = snap.Int("PlayerLevel", _state.CurrentLevel);
            var recipes = snap.Arr("Recipes").Select(r => new Recipe(
                r.Int("ResultItemId"), r.Str("ResultBaseItemId"), r.Int("ProfessionType"), r.Int("RequiredLevel"),
                r.Int("Mat1Id"), r.Int("Mat1Count"), r.Int("Mat1CurrentStock"),
                r.Int("Mat2Id"), r.Int("Mat2Count"), r.Int("Mat2CurrentStock"))).Where(r => r.Level <= level).ToList();

            static bool Affordable(Recipe r, int batch) => r.Have1 >= r.Need1 * batch && (r.Mat2 <= 0 || r.Have2 >= r.Need2 * batch);

            // Tools: the best recipe of each kind the bot does not already own at that region tier or above.
            var pieces = Pieces();
            foreach (string kind in ToolKinds)
            {
                var ofKind = recipes.Where(r => r.Profession == 3 && r.ResultBase.Contains(kind + "_tool")).OrderByDescending(r => ItemRegion(r.ResultBase)).ThenByDescending(r => r.Need1).ToList();
                if (ofKind.Count == 0) continue;
                int ownedRegion = pieces.Where(p => p.BaseId.Contains(kind + "_tool")).Select(p => ItemRegion(p.BaseId)).DefaultIfEmpty(0).Max();
                var better = ofKind.Where(r => ItemRegion(r.ResultBase) > ownedRegion).ToList();
                if (better.Count == 0) continue;
                var craftable = better.FirstOrDefault(r => Affordable(r, 1));
                if (craftable != null)
                {
                    Enqueue(new ClientCommandPacket { Command = CommandType.InitializeCrafting, TargetId = craftable.ResultId, DepositQuantity = 1 },
                        $"craft {craftable.ResultBase}", 35, () => { _inventoryDirty = true; Count("tools_crafted"); Expire("gear"); });
                    return;
                }
                // The plain recipe of the best tier: gather for it.
                var plain = better.Where(r => ItemRegion(r.ResultBase) == ItemRegion(better[0].ResultBase)).OrderBy(r => r.Need1).First();
                if (plain.Have1 < plain.Need1) WantMaterial(plain.Mat1, plain.Need1, plain.ResultBase);
                else if (plain.Mat2 > 0 && plain.Have2 < plain.Need2) WantMaterial(plain.Mat2, plain.Need2, plain.ResultBase);
                break; // one tool at a time, the rod first
            }

            // Potions: brew what the herbs allow, strong first; herb for three more when fewer than two are left.
            var potions = recipes.Where(r => r.Profession == 5 && WantedPotion(r.ResultId)).OrderByDescending(r => r.Mat2 > 0 ? 1 : 0).ToList();
            foreach (var r in potions)
            {
                int batch = Math.Min(10, Math.Min(r.Have1 / Math.Max(1, r.Need1), r.Mat2 > 0 ? r.Have2 / Math.Max(1, r.Need2) : int.MaxValue));
                if (batch <= 0) continue;
                Enqueue(new ClientCommandPacket { Command = CommandType.InitializeCrafting, TargetId = r.ResultId, DepositQuantity = (uint)batch },
                    $"brew {batch} x {r.ResultBase}", 40, () => { _inventoryDirty = true; for (int i = 0; i < batch; i++) Count("potions_brewed"); });
                return;
            }
            int stock = PotionsHeld();
            if (stock < 2 && _state.CurrentLevel >= 5)
            {
                var plainPotion = potions.Where(r => r.Mat2 <= 0).OrderByDescending(r => r.Level).FirstOrDefault();
                if (plainPotion != null) WantMaterial(plainPotion.Mat1, plainPotion.Need1 * 3, plainPotion.ResultBase);
            }
        }

        /// <summary>The potions the bot brews: the XP elixir always, armour from level 20, attack speed from 40, bossbane from 80.</summary>
        private static bool WantedPotion(int itemId)
            => PotionRegistry.TryGet(itemId, out var p) && p.Kind is PotionKind.Hunt or PotionKind.Ironbark or PotionKind.Swiftroot or PotionKind.Bossbane;

        private int PotionsHeld()
            => Stacks().Where(s => s.ItemId > 0 && PotionRegistry.TryGet(s.ItemId, out _)).Sum(s => s.Quantity)
               + _state.PotionShelf1_Count + _state.PotionShelf2_Count + _state.PotionShelf3_Count;

        /// <summary>
        /// Stock the rewards shelf (slot 0) with the XP elixir and the combat
        /// shelf (slot 1) with the best combat potion held, strong first; turn
        /// auto-drink on for a stocked shelf, and drink once when nothing has
        /// been drunk for an hour.
        /// </summary>
        private void PotionShelves()
        {
            var held = Stacks().Where(s => s.ItemId > 0 && s.Quantity > 0 && PotionRegistry.TryGet(s.ItemId, out _))
                .Select(s => (s.ItemId, s.Quantity, Def: PotionRegistry.TryGet(s.ItemId, out var d) ? d : default)).ToList();
            var shelves = new[] { (_state.PotionShelf1_ItemId, _state.PotionShelf1_Count), (_state.PotionShelf2_ItemId, _state.PotionShelf2_Count), (_state.PotionShelf3_ItemId, _state.PotionShelf3_Count) };
            foreach (int slot in new[] { PotionSlot.Rewards, PotionSlot.Combat })
            {
                var best = held.Where(h => h.Def.Slot == slot && (slot != PotionSlot.Rewards || h.Def.Kind == PotionKind.Hunt))
                    .OrderByDescending(h => h.Def.Strong).ThenByDescending(h => h.Def.Magnitude).FirstOrDefault();
                var (shelfItem, shelfCount) = shelves[slot];
                if (best.ItemId != 0 && (shelfCount == 0 || shelfItem == best.ItemId))
                {
                    Enqueue(new ClientCommandPacket { Command = CommandType.StockPotionShelf, TargetSlotIndex = (uint)slot, ConsumableItemId = (uint)best.ItemId, DepositQuantity = (uint)best.Quantity },
                        $"potion shelf {slot}: {best.Quantity} x {ContentRegistry.GetItemBaseId(best.ItemId)}", 45, () => _inventoryDirty = true);
                    return;
                }
                if (shelfCount > 0 && (_state.PotionAutoDrinkMask & (1 << slot)) == 0)
                {
                    Enqueue(new ClientCommandPacket { Command = CommandType.SetPotionAutoDrink, TargetSlotIndex = (uint)slot, IsBuy = 1 }, $"potion shelf {slot}: auto-drink on", 45);
                    return;
                }
                if (shelfCount > 0 && Now - _lastPotionDrunk > 1.0 && slot == PotionSlot.Rewards)
                {
                    _lastPotionDrunk = Now;
                    Enqueue(new ClientCommandPacket { Command = CommandType.DrinkPotion, TargetSlotIndex = (uint)slot }, $"drink from shelf {slot}", 45, () => Count("potions_drunk_by_hand"));
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // The forge
        // ------------------------------------------------------------------

        /// <summary>
        /// Fuse every stack of three or more identical spares (same piece, same
        /// rarity, not worn) of the region being fought, as far as the Forge's
        /// level allows - one FuseStack, the server picks the pieces and
        /// charges the fees. Above twice the gold reserve, one auto reroll of
        /// the worn weapon's affixes (ten tries, stop at Rare or better), round
        /// robin over its affixes.
        /// </summary>
        private async Task ForgeAsync(CancellationToken token)
        {
            var forge = await _client.GetAsync("/api/v1/forge/inventory", token);
            if (forge is not { } f) return;
            int region = Math.Max(1, (int)_state.HighestUnlockedRegion);
            var owned = f.Arr("OwnedEquipment").Select(e => new
            {
                Id = e.Long("Id"), BaseId = e.Str("BaseItemId"), Tier = e.Int("QualityTier"), Worn = e.Bool("IsEquipped"),
                Affixes = e.Obj("Affixes").ValueKind == JsonValueKind.Object ? e.Obj("Affixes").EnumerateObject().Select(p => p.Name).ToList() : new List<string>(),
            }).ToList();

            var stack = owned.Where(e => !e.Worn && ItemRegion(e.BaseId) >= region - 1 && e.Tier + 1 <= _state.ForgeLevel)
                .GroupBy(e => (e.BaseId, e.Tier)).Where(g => g.Count() >= 3)
                .OrderByDescending(g => ItemRegion(g.Key.BaseId)).ThenByDescending(g => g.Key.Tier).FirstOrDefault();
            if (stack != null)
            {
                int toTier = Math.Min(_state.ForgeLevel, stack.Key.Tier + 3);
                long goldBefore = _state.Gold;
                Enqueue(new ClientCommandPacket { Command = CommandType.FuseStack, TargetId = stack.First().Id, QualityTier = (byte)toTier },
                    $"fuse stack {stack.Key.BaseId} x{stack.Count()} from tier {stack.Key.Tier} up to {toTier} (gold {goldBefore})", 40,
                    () => { _inventoryDirty = true; Count("fusions"); Expire("gear"); });
                return;
            }

            var weapon = owned.FirstOrDefault(e => e.Worn && Pieces().Any(p => p.Id == e.Id && p.ByChar == 0 && p.InSlot == 0));
            if (weapon != null && weapon.Affixes.Count > 0 && _state.Gold > Math.Max(20_000, GoldReserve * 2) && Due("reroll", 240))
            {
                int index = _rerollAffixCursor++ % weapon.Affixes.Count;
                Enqueue(new ClientCommandPacket
                {
                    Command = CommandType.RerollItemAffix, TargetId = weapon.Id, LimitPrice = index,
                    RerollOperationKind = 0, RerollAutoMaxAttempts = 10, RerollStopMinRarity = 3, RerollStopAffixIndex = 0,
                }, $"auto reroll affix {index} ({weapon.Affixes[index]}) of {weapon.BaseId}, gold {_state.Gold}", 55,
                () => { Count("reroll_runs"); _projectionDirty = true; });
            }
        }

        // ------------------------------------------------------------------
        // The Workshop and the Great Works
        // ------------------------------------------------------------------

        private static readonly string[] AffixPreference = { "lifesteal", "crit", "dmg", "atk", "attack", "hp", "health", "armor", "armour", "speed" };

        private async Task WorkshopAsync(CancellationToken token)
        {
            var view = await _client.GetAsync("/api/v1/workshop", token);
            if (view is not { } v || v.Int("WorkshopLevel") <= 0) return;
            var commission = v.Obj("Commission");
            if (commission.ValueKind == JsonValueKind.Object)
            {
                if (!commission.Bool("Ready")) return;
                var (status, body) = await _client.PostAsync("/api/v1/workshop/collect", new { }, token);
                if (status is >= 200 and < 300 && body is { } b && b.Obj("Collected").ValueKind == JsonValueKind.Object)
                {
                    var c = b.Obj("Collected");
                    Event($"workshop collected {c.Str("BaseItemId")} ({c.Str("RarityName")})");
                    Count("workshop_pieces");
                    _inventoryDirty = true;
                    Expire("gear");
                }
                return;
            }

            // A commission for the slot whose worn piece is weakest, in the highest unlocked region that is affordable.
            var worn = Pieces().Where(p => p.Equipped && p.ByChar == 0 && p.InSlot is >= 0 and <= 7).ToDictionary(p => p.InSlot, p => PieceScore(p.BaseId, p.Tier));
            foreach (var region in v.Arr("Regions").Where(r => r.Bool("Unlocked") && r.Bool("Affordable")).OrderByDescending(r => r.Int("Region")))
            {
                var piece = region.Arr("Pieces")
                    .Select(p => (Id: p.Int("ItemId"), BaseId: p.Str("BaseItemId"), Affixes: p.Arr("Affixes").Select(a => a.GetString() ?? "").ToList()))
                    .OrderBy(p => worn.GetValueOrDefault(FolkIdle.Server.Domain.Combat.EquipmentSlotEngine.ResolveSlotIndex(p.BaseId), 0))
                    .FirstOrDefault();
                if (piece.Id <= 0 || piece.Affixes.Count == 0) continue;
                string affix = AffixPreference.Select(want => piece.Affixes.FirstOrDefault(a => a.Contains(want, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(a => a != null) ?? piece.Affixes[0];
                var (status, body) = await _client.PostAsync("/api/v1/workshop/commission", new { ItemId = piece.Id, AffixId = affix }, token);
                string result = body is { } b2 ? b2.Str("Result") : status.ToString();
                Event($"workshop commission {piece.BaseId} with {affix} in region {region.Int("Region")}: {result}");
                if (result == "Ok") { Count("workshop_commissions"); _inventoryDirty = true; }
                return;
            }
        }

        /// <summary>Logs and ore beyond 1,500 of a region's common pair go into that region's monument.</summary>
        private async Task GreatWorksAsync(CancellationToken token)
        {
            var view = await _client.GetAsync("/api/v1/great-works", token);
            if (view is not { } v) return;
            foreach (var w in v.Arr("Works"))
            {
                if (w.Int("NextCost") <= 0) continue;
                int region = w.Int("Region");
                foreach (var (material, held) in new[] { (0, w.Int("HeldLog")), (1, w.Int("HeldOre")) })
                {
                    int spare = held - 1500;
                    if (spare <= 0) continue;
                    Enqueue(new ClientCommandPacket { Command = CommandType.DepositGreatWork, TargetId = region, SecondaryId = material, DepositQuantity = (uint)spare },
                        $"great work {w.Str("Name")}: deposit {spare} {(material == 0 ? w.Str("LogItem") : w.Str("OreItem"))}", 65,
                        () => { _inventoryDirty = true; Count("great_work_deposits"); });
                    return;
                }
            }
        }
    }
}
