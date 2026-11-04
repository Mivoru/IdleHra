using System.Text.Json;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Bots
{
    /// <summary>
    /// What to fight, when to fish or gather, and what goes in the larder.
    ///
    /// The fight is Combat's "best XP/h" pick (huntingEstimate.ts bestPick)
    /// from the server's HuntingProjection, plus the region boss once the
    /// projection says the fight holds. Fishing and gathering are TRIPS: the
    /// main character leaves the fight for one node until a target count or a
    /// time limit, then goes back.
    /// </summary>
    public sealed partial class BotBrain
    {
        private JsonElement? _inventory;
        private bool _inventoryDirty = true;
        private JsonElement? _projection;
        private bool _projectionDirty = true;
        private int _projectionLevel = -1;

        /// <summary>
        /// Modul: A REFUSAL IS FORGOTTEN WHEN THE CHARACTER CHANGES (bot
        /// mistake 4.1.6). A node refused because its location was not reached
        /// yet stayed banned for the rest of the run after it opened. A refusal
        /// now holds only while level, region, location and the death count are
        /// what they were, and only for a cooldown (RefusalMemory).
        /// </summary>
        private readonly RefusalMemory _refused = new();
        private long _pendingActivity;
        private double _pendingActivityAtHours;
        private int _deathsSeen;
        private int _allSetAsideLoggedAtLevel = -1;

        private RefusalConditions RefusalNow
            => new(_state.CurrentLevel, _state.HighestLocationReached, _state.HighestUnlockedRegion, _deathsSeen);

        /// <summary>Records a refusal with its reason and makes the next turn re-read the inventory and the fight offer.</summary>
        private void MarkRefused(long key, string what)
        {
            double? cooldown = _refused.Mark(key, RefusalNow, Now);
            _projectionDirty = _inventoryDirty = true;
            Count("refusals");
            Event(cooldown is double h
                ? $"{what} was refused (halt {_state.ActivityHaltReason}, result {_state.CommandResult0_Code}); strike {_refused.Strikes(key)}, retry in {h * 60:0.#} game min"
                : $"{what} was refused (halt {_state.ActivityHaltReason}, result {_state.CommandResult0_Code}); strike {_refused.Strikes(key)} of {RefusalMemory.MaxStrikes}, no retry until level, location, region or a death changes");
        }

        private int _deathsAtLevel;
        private int _deathLevel = -1;
        private long _lastDeathMonster;
        private byte _lastDeathTick;
        private readonly Dictionary<long, int> _avoidUntilLevel = new();

        private async Task RefreshAsync(CancellationToken token)
        {
            if (_inventoryDirty || Due("inventory", _trip != null ? 2 : 10))
            {
                _inventory = await _client.GetAsync("/api/v1/player/inventory", token);
                _inventoryDirty = false;
            }
            if (_projectionDirty || _state.CurrentLevel != _projectionLevel || Due("projection", 5))
            {
                var projection = await _client.GetAsync("/api/v1/combat/projection?slot=0", token);
                if (projection is { } pr && pr.Str("Reason") == "NoSession") _noSessionSeen = true;
                else if (projection != null) _projection = projection;
                _projectionDirty = false;
                _projectionLevel = _state.CurrentLevel;
            }
            TrackDeaths();
        }

        private bool IsRefused(long activityId) => _refused.IsRefused(activityId, RefusalNow, Now);

        /// <summary>Two deaths to one monster at one level: leave it until the next level.</summary>
        private void TrackDeaths()
        {
            if (_state.LastDeathTick == _lastDeathTick) return;
            bool first = _lastDeathTick == 0 && _deathLevel < 0;
            _lastDeathTick = _state.LastDeathTick;
            // A death (and the respawn after it) forgets every refusal: the
            // offer is looked at again from scratch.
            _deathsSeen++;
            _projectionDirty = true;
            if (first) return;
            long monster = _state.LastDeathMonsterId;
            if (_deathLevel != _state.CurrentLevel || _lastDeathMonster != monster)
            {
                _deathLevel = _state.CurrentLevel;
                _lastDeathMonster = monster;
                _deathsAtLevel = 0;
            }
            _deathsAtLevel++;
            _projectionDirty = true;
            if (_deathsAtLevel >= 2 && monster > 0)
            {
                _avoidUntilLevel[monster] = _state.CurrentLevel + 1;
                Event($"died twice to {MonsterName(monster)} at level {_state.CurrentLevel}: leaving it until level {_state.CurrentLevel + 1}");
            }
        }

        private bool Avoided(long monsterId)
            => _avoidUntilLevel.TryGetValue(monsterId, out int until) && _state.CurrentLevel < until;

        // ------------------------------------------------------------------
        // Inventory reads
        // ------------------------------------------------------------------

        private IEnumerable<(string BaseId, int ItemId, int Quantity)> Stacks()
        {
            if (_inventory is not { } inv) yield break;
            foreach (var s in inv.Arr("Stacks"))
            {
                string baseId = s.Str("ItemId");
                int id = ContentRegistry.TryGetItemDefinitionByBaseId(baseId, out var d) ? d.Id : 0;
                yield return (baseId, id, s.Int("Quantity"));
            }
        }

        private int Held(int itemId) => itemId <= 0 ? 0 : Stacks().Where(s => s.ItemId == itemId).Sum(s => s.Quantity);
        private int Held(string baseId) => Stacks().Where(s => s.BaseId == baseId).Sum(s => s.Quantity);

        private int FoodInInventory() => Stacks().Where(s => s.ItemId > 0 && FoodRegistry.IsFood(s.ItemId)).Sum(s => s.Quantity);

        private int LarderCount => _state.Food1_Count + _state.Food2_Count + _state.Food3_Count;

        /// <summary>The best food tier in the larder or the chest; 0 with none.</summary>
        private int BestFoodTierHeld()
        {
            int best = 0;
            foreach (var (id, count) in new[] { (_state.Food1_ItemId, _state.Food1_Count), (_state.Food2_ItemId, _state.Food2_Count), (_state.Food3_ItemId, _state.Food3_Count) })
                if (id > 0 && count > 0) best = Math.Max(best, FoodRegistry.GetTier(id));
            foreach (var s in Stacks())
                if (s.ItemId > 0 && s.Quantity > 0 && FoodRegistry.IsFood(s.ItemId)) best = Math.Max(best, FoodRegistry.GetTier(s.ItemId));
            return best;
        }

        // ------------------------------------------------------------------
        // The larder
        // ------------------------------------------------------------------

        private const int LarderSlotCapacity = 9999;

        /// <summary>
        /// Modul: THE BEST FOOD IN THE LARDER, NOT THE BIGGEST STACK (bot
        /// mistakes 4.1.1 and 4.1.5). The old rule was Larder's quickLoad - the
        /// biggest stack first - so a hundred fresh region-2 fish waited behind
        /// three slots of region-1 fish and never got eaten. And it loaded at
        /// most 200 per slot when a slot holds 9,999 (Larder.svelte
        /// SLOT_CAPACITY). Now: a chest food of a better tier than the worst
        /// loaded slot replaces that slot (StockFoodSlot sends the old food
        /// back to the chest), and a food already loaded is topped up.
        /// </summary>
        private bool TryFoodCommand(out ClientCommandPacket packet)
        {
            packet = default;
            var slots = new[]
            {
                (Index: 0, ItemId: _state.Food1_ItemId, Count: _state.Food1_Count),
                (Index: 1, ItemId: _state.Food2_ItemId, Count: _state.Food2_Count),
                (Index: 2, ItemId: _state.Food3_ItemId, Count: _state.Food3_Count),
            };
            var foods = Stacks().Where(s => s.ItemId > 0 && s.Quantity > 0 && FoodRegistry.IsFood(s.ItemId))
                .OrderByDescending(s => FoodRegistry.GetTier(s.ItemId)).ThenByDescending(s => s.Quantity).ToList();

            foreach (var food in foods)
            {
                int tier = FoodRegistry.GetTier(food.ItemId);
                var same = slots.FirstOrDefault(sl => sl.ItemId == food.ItemId && sl.Count > 0);
                int index;
                int room;
                if (same.ItemId != 0)
                {
                    index = same.Index;
                    room = LarderSlotCapacity - same.Count;
                }
                else
                {
                    var empty = slots.Where(sl => sl.ItemId == 0 || sl.Count == 0).Select(sl => (int?)sl.Index).FirstOrDefault();
                    if (empty is int e)
                    {
                        index = e;
                    }
                    else
                    {
                        var worst = slots.OrderBy(sl => FoodRegistry.GetTier(sl.ItemId)).ThenBy(sl => sl.Count).First();
                        if (FoodRegistry.GetTier(worst.ItemId) >= tier) continue;
                        index = worst.Index;
                    }
                    room = LarderSlotCapacity;
                }
                int quantity = Math.Min(food.Quantity, room);
                if (quantity <= 0) continue;
                Event($"larder: {quantity} x {food.BaseId} (tier {tier}) into slot {index}");
                packet = new ClientCommandPacket
                {
                    Command = CommandType.StockFoodSlot,
                    ConsumableItemId = (uint)food.ItemId,
                    TargetSlotIndex = (uint)index,
                    DepositQuantity = (uint)quantity,
                };
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Trips: fishing and gathering
        // ------------------------------------------------------------------

        /// <summary>A trip away from the fight: one node, until the target or the time limit.</summary>
        private sealed record Trip(long NodeId, string Purpose, int ItemId, int Target, double StartedAt, double MaxGameMinutes);

        private Trip? _trip;
        private double _lastMaterialTripEnded = -10;

        private bool TripDone(Trip trip)
        {
            int have = trip.ItemId == 0 ? FoodInInventory() : Held(trip.ItemId);
            return have >= trip.Target || (Now - trip.StartedAt) * 60 >= trip.MaxGameMinutes;
        }

        private void EndTrip(string why)
        {
            if (_trip == null) return;
            int have = _trip.ItemId == 0 ? FoodInInventory() : Held(_trip.ItemId);
            Event($"{_trip.Purpose} trip over ({why}): {have}/{_trip.Target} after {(Now - _trip.StartedAt) * 60:0} game min");
            if (_trip.ItemId != 0)
            {
                _lastMaterialTripEnded = Now;
                _lastMaterialTripHours = Now - _trip.StartedAt;
            }
            _trip = null;
            _inventoryDirty = true;
        }

        private void StartTrip(Trip trip)
        {
            _trip = trip;
            Count(trip.ItemId == 0 ? "fishing_trips" : "gathering_trips");
            Event($"{trip.Purpose} trip: node {trip.NodeId} for {trip.Target} (at most {trip.MaxGameMinutes:0} game min)");
        }

        /// <summary>The nodes whose loot table drops <paramref name="itemId"/>, best reachable first.</summary>
        private long NodeFor(int itemId, int professionType)
        {
            int reach = Math.Max(1, (int)_state.HighestLocationReached);
            long best = 0;
            int bestLocation = 0;
            foreach (var node in ContentRegistry.GatheringNodes.ToArray())
            {
                if (node.ProfessionType != professionType || IsRefused(node.ActivityId)) continue;
                int location = ContentRegistry.GetNodeLocation(node.ActivityId);
                if (location < 1 || location > reach) continue;
                bool drops = itemId == 0;
                if (!drops)
                {
                    foreach (var entry in ContentRegistry.GetLootTable((int)node.ActivityId))
                        if (entry.ItemId == itemId) { drops = true; break; }
                }
                if (drops && location > bestLocation)
                {
                    best = node.ActivityId;
                    bestLocation = location;
                }
            }
            return best;
        }

        private const int ProfessionFishing = 2;

        /// <summary>
        /// Modul: FISH FOR HOURS OF THE FIGHT, NOT A FIXED 200 (bot mistake
        /// 4.1.7). Three game hours of what the chosen fight eats - the
        /// projection's FoodPerHour, or the ration rate from region 2 on, which
        /// is a fish every RationIntervalTicks - within 150 and 3,000.
        /// </summary>
        private int FishingTarget()
        {
            int region = Math.Max(1, (int)_state.HighestUnlockedRegion);
            double perHour = 0;
            long monster = _state.ActiveActivityId;
            if (_projection is { } proj)
                foreach (var e in proj.Arr("Monsters"))
                    if (e.Int("MonsterId") == monster) perHour = e.Num("FoodPerHour");
            int interval = FoodRegistry.RationIntervalTicks(region);
            double rations = interval > 0 ? 36000.0 / interval : 0;
            return (int)Math.Clamp((perHour + rations) * 3, 150, 3000);
        }

        // ------------------------------------------------------------------
        // The fight
        // ------------------------------------------------------------------

        private bool TryFightCommand(out ClientCommandPacket packet)
        {
            packet = default;
            if (_state.Slot1_CharacterId == Guid.Empty) return false;
            if (_busyUntil > Now) return false; // a dungeon or ascension fight owns the character

            if (_pendingActivity != 0)
            {
                if (_state.ActiveActivityId == _pendingActivity)
                {
                    _pendingActivity = 0; // confirmed
                }
                else if (Now - _pendingActivityAtHours > 8.0 / 3600)
                {
                    // Not taken. A death also clears the activity, and is not a refusal.
                    if (_state.ActivityHaltReason != ActivityHaltReason.Died)
                    {
                        MarkRefused(_pendingActivity, $"activity {_pendingActivity}");
                        if (_trip != null && _trip.NodeId == _pendingActivity) EndTrip("refused");
                    }
                    _pendingActivity = 0;
                }
                else
                {
                    return false;
                }
            }

            long target = ChooseActivity();
            if (target <= 0 || target == _state.ActiveActivityId) return false;

            _pendingActivity = target;
            _pendingActivityAtHours = Now;
            packet = new ClientCommandPacket { Command = CommandType.ChangeActivity, TargetId = target, TargetGuid = _state.Slot1_CharacterId };
            Event(_trip != null
                ? $"{_trip.Purpose} at node {target}"
                : $"fight {MonsterName(target)} ({target}){HpText()}, level {_state.CurrentLevel}, larder {LarderCount}");
            return true;
        }

        private string HpText() => _state.PlayerMaxHp > 0 ? $" at HP {_state.PlayerHp}/{_state.PlayerMaxHp}" : "";

        /// <summary>
        /// Modul: LEAVE THE CHARACTER FIGHTING (2026-11-01). A casual or idle
        /// bot's first check-in started a herb trip and logged out in the
        /// middle of it, so the idle bot picked yarrow at level 1 for its whole
        /// 24-hour absence. A player closing the tab leaves the character on
        /// the best fight; in the last ten game minutes of a check-in the bot
        /// ends a material trip and does the same. A fishing trip ends too once
        /// a hundred fish are caught - the idle bot otherwise fished for its
        /// whole 12-hour offline window (49,515 fish, no XP).
        /// </summary>
        private bool LeavingSoon => _persona.HoursLeftInCheckIn(Now) < 10.0 / 60;

        private long ChooseActivity()
        {
            if (_trip != null && LeavingSoon && (_trip.ItemId != 0 || FoodInInventory() >= 100)) EndTrip("check-in ending: back to the fight before logging out");
            if (_trip != null)
            {
                if (TripDone(_trip)) EndTrip("target reached or time up");
                else return _trip.NodeId;
            }

            int larder = LarderCount;
            int carried = FoodInInventory();
            bool hasFood = larder > 0;

            // Better fish are within reach than any the bot holds, and the
            // region being opened needs them: fetch them now, with food left.
            long newRiver = NodeFor(0, ProfessionFishing);
            int riverTier = ContentRegistry.GetNodeLocation(newRiver);
            int heldTier = BestFoodTierHeld();
            int region = Math.Max(1, (int)_state.HighestUnlockedRegion);
            if (newRiver > 0 && riverTier > heldTier && riverTier >= 2 && region >= riverTier)
            {
                StartTrip(new Trip(newRiver, $"fishing (new river, tier {riverTier} over held tier {heldTier})", 0, FishingTarget(), Now, 90));
                return newRiver;
            }

            // A chore is short of a gatherable material (village, tools, potions).
            if (!LeavingSoon && _state.CurrentLevel >= 3)
            {
                long material = MaterialTrip();
                if (material > 0) return material;
            }

            // A bot about to be away for hours with no fight that holds without
            // food: fish for the absence first (the food command loads it).
            if (!_persona.AlwaysOn && !LeavingSoon && newRiver > 0 && !AnySafeFight() && larder + carried < AbsenceFood())
            {
                StartTrip(new Trip(newRiver, "fishing (food for the time away)", 0, AbsenceFood(), Now, 60));
                return newRiver;
            }

            long scout = ChooseScout(hasFood);
            long fight = scout > 0 ? scout : ChooseMonster(hasFood);
            if (fight > 0) return fight;

            if (larder == 0 && carried == 0 && newRiver > 0)
            {
                Event($"no fight holds without food and the larder is empty: going fishing (level {_state.CurrentLevel})");
                StartTrip(new Trip(newRiver, "fishing (out of food)", 0, FishingTarget(), Now, 90));
                return newRiver;
            }
            return 0;
        }

        private int _scoutFailedAtLevel = -100;
        private const int ScoutRetryLevels = 2;

        /// <summary>
        /// One kill in the next location, to open its gathering.
        ///
        /// Modul: THE REGION-2 CATCH-22 (24 h run, 2026-11-01). A location's
        /// fishing opens on the first kill THERE (ApplyKillProgression), and
        /// region 2's fights need region 2's fish. A sensible player tries one
        /// fight at full health to get through that door, so the bot does: the
        /// first monster of the next location, when the projection gives at
        /// least one kill before death (or holds with food). A death there
        /// waits two levels before the next try.
        /// </summary>
        private long ChooseScout(bool hasFood)
        {
            int reached = Math.Max(1, (int)_state.HighestLocationReached);
            int open = Math.Max(1, (int)_state.HighestUnlockedRegion);
            if (reached >= open) return 0;

            int firstMonster = ContentRegistry.FirstCanonicalMonsterId + reached * ContentRegistry.MonstersPerRegion;
            if (_state.ActivityHaltReason == ActivityHaltReason.Died && _state.LastDeathMonsterId == firstMonster
                && _scoutFailedAtLevel != _state.CurrentLevel)
            {
                _scoutFailedAtLevel = _state.CurrentLevel;
                Event($"scouting location {reached + 1} failed at level {_state.CurrentLevel}; next try at level {_state.CurrentLevel + ScoutRetryLevels}");
                Count("scout_deaths");
            }
            if (_state.CurrentLevel < _scoutFailedAtLevel + ScoutRetryLevels) return 0;

            if (_projection is not { } proj) return 0;
            foreach (var e in proj.Arr("Monsters"))
            {
                if (e.Int("MonsterId") != firstMonster) continue;
                bool canDamage = e.Bool("CanDamage");
                int killsBeforeDeath = e.Int("KillsBeforeDeathWithoutFood");
                if (canDamage && (killsBeforeDeath >= 1 || hasFood))
                {
                    if (_state.ActiveActivityId != firstMonster)
                        Event($"scouting location {reached + 1}: one fight with {MonsterName(firstMonster)} to open its gathering (projected kills before death {killsBeforeDeath}, food {hasFood})");
                    return firstMonster;
                }
            }
            return 0;
        }

        private long ChooseMonster(bool hasFood)
        {
            if (_projection is not { } proj) return 0;
            int region = Math.Max(1, (int)_state.HighestUnlockedRegion);

            var estimates = proj.Arr("Monsters").Select(e => new
            {
                Id = (long)e.Int("MonsterId"),
                CanDamage = e.Bool("CanDamage"),
                Safe = e.Bool("SurvivesWithoutFood"),
                WithFood = e.Bool("SurvivesWithFood"),
                XpPerHour = e.Num("XpPerHour"),
            })
            .Where(e => !IsRefused(e.Id) && !Avoided(e.Id) && MonsterRegion(e.Id) is > 0 and var r && r <= region)
            .ToList();

            // The region boss of the highest open region, once the fight holds.
            int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
            if (bossId > 0 && !BossFirstClearRules.IsDefeated(_state.DefeatedRegionBossMask, bossId))
            {
                var boss = estimates.FirstOrDefault(e => e.Id == bossId);
                if (boss != null && boss.CanDamage && (boss.Safe || (hasFood && boss.WithFood))) return bossId;
            }

            // Modul: LEAVING, PICK A FIGHT THAT HOLDS WITHOUT FOOD (x100 run,
            // 2026-11-01). The idle bot logged out on a fight that held only
            // with food; offline the larder ran dry, the character died, and the
            // next 12-hour window paid +0 XP. A player closing the tab for a long
            // time leaves the character where it cannot die.
            if (LeavingSoon)
            {
                var safe = estimates.Where(e => e.CanDamage && e.Safe).OrderByDescending(e => e.XpPerHour).ThenBy(e => e.Id).FirstOrDefault();
                if (safe != null) return safe.Id;
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
            long easiest = hasFood ? estimates.Where(e => e.CanDamage).OrderBy(e => e.Id).Select(e => e.Id).FirstOrDefault() : 0;
            if (easiest > 0 || estimates.Any(e => e.CanDamage)) return easiest;

            // Modul: NEVER SET EVERY FIGHT ASIDE (night 2026-11-04, val-x20).
            // "Died twice: leave it until the next level" for every monster in
            // reach, plus one refused fight, left no fight at all - and the next
            // level needs a fight, so eight bots stood idle for 71 game hours.
            // When the deaths rule has set aside everything in reach that is not
            // in a refusal cooldown, the easiest of those comes back: dying to
            // it is a cost the deaths column shows, standing still is a dead
            // run. A fight still in its refusal cooldown waits for the cooldown.
            var setAside = proj.Arr("Monsters")
                .Select(e => (Id: (long)e.Int("MonsterId"), CanDamage: e.Bool("CanDamage"), Safe: e.Bool("SurvivesWithoutFood"), WithFood: e.Bool("SurvivesWithFood")))
                .Where(e => e.CanDamage && Avoided(e.Id) && !IsRefused(e.Id) && MonsterRegion(e.Id) is > 0 and var r && r <= region)
                .OrderByDescending(e => e.Safe || (hasFood && e.WithFood)).ThenBy(e => e.Id)
                .ToList();
            if (setAside.Count == 0 || (!hasFood && !setAside[0].Safe)) return 0;
            if (_allSetAsideLoggedAtLevel != _state.CurrentLevel)
            {
                _allSetAsideLoggedAtLevel = _state.CurrentLevel;
                Count("all_fights_set_aside");
                Event($"every fight in reach is set aside or refused at level {_state.CurrentLevel}: back to {MonsterName(setAside[0].Id)} rather than standing still");
            }
            return setAside[0].Id;
        }

        private bool AnySafeFight()
            => _projection is { } p && p.Arr("Monsters").Any(e => e.Bool("CanDamage") && e.Bool("SurvivesWithoutFood")
                   && MonsterRegion(e.Int("MonsterId")) <= Math.Max(1, (int)_state.HighestUnlockedRegion));

        /// <summary>Food for the offline window ahead: the current fight's use per hour times the hours until the next check-in, capped at the offline cap (12 h).</summary>
        private int AbsenceFood()
        {
            double hours = Math.Min(12, _persona.CheckInEveryGameHours);
            return (int)Math.Clamp(FishingTarget() / 3.0 * hours, 150, 9999);
        }

        /// <summary>The projection's XP/h of the best fight that holds - the yardstick for "is this worth leaving the fight for".</summary>
        private double BestXpPerHour()
        {
            if (_projection is not { } proj) return 0;
            bool hasFood = LarderCount > 0;
            return proj.Arr("Monsters")
                .Where(e => e.Bool("CanDamage") && (e.Bool("SurvivesWithoutFood") || (hasFood && e.Bool("SurvivesWithFood"))))
                .Select(e => e.Num("XpPerHour")).DefaultIfEmpty(0).Max();
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
