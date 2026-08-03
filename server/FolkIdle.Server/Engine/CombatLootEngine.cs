using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    // Modul 03/10/11/12: 14-tier rarity system for combat-dropped equipment.
    // Tier 1 (Normal) is the unweighted remainder probability; tiers 2-14 are
    // the explicit GDD weights below. This reconciles the brief's "tier 1 to
    // 14" range with its 13-name weighted list (Common..Transcendent) - Normal
    // is the base tier every drop falls back to when nothing rarer rolls.
    public static class RarityTier
    {
        public const int Normal = 1;
        public const int Common = 2;
        public const int Uncommon = 3;
        public const int Rare = 4;
        public const int UltraRare = 5;
        public const int Epic = 6;
        public const int Legendary = 7;
        public const int Mythic = 8;
        public const int Relic = 9;
        public const int Ancient = 10;
        public const int Divine = 11;
        public const int Demonic = 12;
        public const int Godly = 13;
        public const int Transcendent = 14;

        // Index 0 unused (tiers are 1-based); index 1 (Normal) is never read
        // directly - RollTier computes it as the remainder of the other 13.
        private static readonly double[] _explicitWeights = new double[]
        {
            0.0,    // unused
            0.0,    // Normal - remainder, computed in RollTier
            50.0,   // Common
            25.0,   // Uncommon
            12.5,   // Rare
            5.0,    // Ultra Rare
            2.5,    // Epic
            1.0,    // Legendary
            0.5,    // Mythic
            0.1,    // Relic
            0.05,   // Ancient
            0.01,   // Divine
            0.005,  // Demonic
            0.001,  // Godly
            0.0001  // Transcendent
        };

        // Modul 03: FinalChance = BaseChance * (1 + LootLuckPct / 100.0),
        // applied to every explicitly-weighted tier (2-14). Tier 1 (Normal)
        // keeps a flat, unscaled weight so its share of the total shrinks as
        // luck raises the other tiers - "shifting generation logic smoothly
        // into higher rarity thresholds" without needing renormalization.
        public static int RollTier(float lootLuckPct)
        {
            const double normalBaseWeight = 100.0;
            double luckFactor = 1.0 + (lootLuckPct / 100.0);

            Span<double> effectiveWeights = stackalloc double[15]; // 1-14, index 0 unused
            effectiveWeights[1] = normalBaseWeight;
            double totalWeight = normalBaseWeight;

            for (int tier = 2; tier <= 14; tier++)
            {
                double weight = _explicitWeights[tier] * luckFactor;
                effectiveWeights[tier] = weight;
                totalWeight += weight;
            }

            double roll = Random.Shared.NextDouble() * totalWeight;
            double cumulative = 0.0;
            for (int tier = 1; tier <= 14; tier++)
            {
                cumulative += effectiveWeights[tier];
                if (roll < cumulative)
                {
                    return tier;
                }
            }

            return RarityTier.Normal;
        }

        public static int GetAffixCount(int tier)
        {
            if (tier <= 3) return 1;
            if (tier <= 6) return 2;
            if (tier <= 9) return 3;
            if (tier <= 12) return 4;
            return 5;
        }
    }

    public struct CombatLootDropNotification
    {
        public long PlayerId;
        public bool ConsumedInventorySlot;

        // Modul: inventory census. ConsumedInventorySlot above is set on every
        // kill regardless of what actually dropped, and the tick thread's drain
        // decrements InventorySpaceRemaining by one for each - a counter that
        // only ever falls. After 20 kills every player's backpack read as full
        // and all loot was silently discarded for the rest of the session,
        // whether or not a single item had dropped. Nothing restored the
        // counter; only a relogin did, because hydration reset it to capacity
        // without looking at the backpack either.
        //
        // These two fields replace that guess with the truth. OccupiedSlots is
        // counted inside the same Serializable transaction that granted the
        // loot, so it accounts for stacking (a second Iron Ore joins an existing
        // row and occupies no new slot), for items sold or consumed since the
        // last kill, and for equipment moved onto the character.
        public bool HasSlotCensus;
        public int OccupiedSlots;
    }

    // Modul: Guild War scoreboard sync. GuildWarMatches has always held the
    // real running war totals, but nothing ever copied them back into each
    // member's live TickStatePayload - so all six scoreboard fields (and the
    // war multiplier) were permanently zero on every client during a real
    // war, which read as a broken feature rather than as a missing sync.
    // This is the missing link: one notification per guild per sync pass,
    // drained by the tick thread onto every online member of that guild.
    public struct GuildWarScoreboardNotification
    {
        public long GuildId;
        public int OurCombatVanguardPoints;
        public int OurProductionLogisticsPoints;
        public int OurGatheringSupplyChainPoints;
        public int EnemyCombatVanguardPoints;
        public int EnemyProductionLogisticsPoints;
        public int EnemyGatheringSupplyChainPoints;

        // This guild's share of the combined war score, 0..1. See
        // GuildWarEngine.RunScoreboardSyncLoopAsync for why the previously
        // undefined CachedWarMultiplier field carries this.
        public float ScoreShare;

        // Modul: Guild War scoreboard sync. Set when the guild's war has ENDED.
        // The sync loop only ever published for matches with IsActive, and
        // nothing anywhere cleared ActiveGuildWarId or the six point totals when
        // a war finished - so the payload kept the final score forever and the
        // client, which decides "a war is on" from ActiveGuildWarId > 0, went on
        // showing a concluded war as live until the player relogged.
        public bool WarEnded;
    }

    // Modul: Deploy activation fix. Carries a committed activity change from
    // the background dispatch that persisted it back to the tick thread that
    // owns the live payload. CharacterId is included so the tick can confirm
    // the change actually applies to the character the live session is
    // simulating (Slot1) rather than blindly retargeting the fight.
    public struct ActivityChangeNotification
    {
        public long PlayerId;
        public Guid CharacterId;
        public long TargetActivityId;
    }

    // Modul 03/10/11/12: an equipment drop roll request from the 10 Hz tick.
    // ProcessSubTick is a static method (matching CodexEngine.KillEventQueue's
    // established convention) so it enqueues onto this static queue directly
    // rather than needing an instance reference to CombatLootEngine.
    public struct CombatLootDropRequest
    {
        public long PlayerId;
        public int MonsterId;
        public float LootLuckPct;
    }

    // Modul 03/10/11/12: rolls and persists combat-kill equipment/diamond
    // drops. Drains CombatLootDropQueue on a background poll loop (mirrors
    // CodexEngine's KillEventQueue/StartCron pattern) since the 10 Hz tick
    // thread cannot perform DB access directly.
    public class CombatLootEngine
    {
        public static readonly ConcurrentQueue<CombatLootDropRequest> DropRequestQueue = new();

        // Modul: inventory census. How many backpack slots a player actually
        // occupies right now.
        //
        // The backpack is stack-based: CommodityRecords holds one row per
        // material type with an unbounded quantity, so a thousand Iron Ore
        // occupy one slot, not a thousand. Equipment does not stack - each
        // EquipmentInstance is its own slot - except for the at most three
        // pieces currently worn, which live on the character rather than in the
        // pack.
        //
        // Two COUNTs per kill. That is the price of an inventory number that is
        // correct; the previous free version was a one-way counter that made the
        // game stop producing loot after 20 kills.
        public static async Task<int> CountOccupiedBackpackSlotsAsync(FolkIdleDbContext db, long playerId)
        {
            int materialStacks = await db.CommodityRecords
                .AsNoTracking()
                .CountAsync(c => c.PlayerId == playerId && c.Quantity > 0);

            int ownedEquipment = await db.EquipmentInstances
                .AsNoTracking()
                .CountAsync(e => e.PlayerId == playerId);

            // Modul: per-character equipment. Worn gear lives on the character
            // now, and every character's worn pieces are off the player's back -
            // three characters in full kit means up to eighteen items that are
            // not taking backpack slots. Counting only the main character's
            // three would have shrunk the apparent backpack by everything the
            // other two were wearing.
            var wornSlots = await db.CharacterRecords
                .AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .Select(c => new
                {
                    c.EquippedWeaponId,
                    c.EquippedHelmetId,
                    c.EquippedChestId,
                    c.EquippedGlovesId,
                    c.EquippedLeggingsId,
                    c.EquippedBootsId,
                    c.EquippedOffhandId
                })
                .ToListAsync();

            int wornCount = 0;
            for (int i = 0; i < wornSlots.Count; i++)
            {
                var character = wornSlots[i];
                if (character.EquippedWeaponId.HasValue) wornCount++;
                if (character.EquippedHelmetId.HasValue) wornCount++;
                if (character.EquippedChestId.HasValue) wornCount++;
                if (character.EquippedGlovesId.HasValue) wornCount++;
                if (character.EquippedLeggingsId.HasValue) wornCount++;
                if (character.EquippedBootsId.HasValue) wornCount++;
                if (character.EquippedOffhandId.HasValue) wornCount++;
            }

            int backpackEquipment = ownedEquipment - wornCount;
            if (backpackEquipment < 0) backpackEquipment = 0;

            return materialStacks + backpackEquipment;
        }

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;
        private CancellationTokenSource _cts = new();

        // Modul: Loot Event Feed staging buffer - see
        // ProcessMonsterLootDropAsync for why drops are held until commit.
        private readonly List<Network.ResponseLootDropPacket> _pendingDrops = new(8);

        public CombatLootEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(3000, stoppingToken);

                while (DropRequestQueue.TryDequeue(out var request))
                {
                    await ProcessMonsterLootDropAsync(request.PlayerId, request.MonsterId, request.LootLuckPct);
                }
            }
        }

        // Modul: Architecture Overhaul, Part 3. Independent Multi-Drop
        // Evaluation. A single kill previously guaranteed exactly one
        // equipment roll (100% base chance, only rarity was randomized).
        // Every category below now rolls independently, so a kill can
        // award materials and several equipment pieces in the same event,
        // or none at all. Baselines sit in the GDD's stated 0.33%-0.50%
        // band; materials roll far more often since they are the common
        // case the crafting economy depends on.
        // Modul: drop preview, 2026-08-02. Public so the loot endpoint quotes
        // the SAME numbers the roller uses. A copy in the API layer would be a
        // second source of truth about drop rates, and players would eventually
        // be shown chances the server does not honour.
        public const double MaterialDropChance = 0.35;
        public const double MeleeWeaponDropChance = 0.0050;
        public const double RangedWeaponDropChance = 0.0040;
        public const double MagicWeaponDropChance = 0.0040;
        public const double HelperDropChance = 0.0033;

        // Modul: approximate backpack capacity used purely to decide
        // whether an equipment drop must be redirected to overflow
        // handling - mirrors StateCheckpointManager's default 20-slot
        // Backpack (see InventorySpaceRemaining's default initializer);
        // the Human race's variable bonus slots are intentionally not
        // modeled here since this check only needs to be conservative,
        // never exact.
        // Mirrors MailboxAndBankEngine's own maxBankSlots. Kept as a named
        // constant here rather than a literal so the two are findable together
        // - they are the same limit seen from the two ends of the same store.
        private const int BankEquipmentCapacity = 100;

        /// <summary>
        /// The lowest quality tier worth a bank slot. Anything below scraps.
        ///
        /// Ultra Rare (5) by measurement, not by feel: equipment drops at 1.63%
        /// of kills and a level-appropriate kill takes about 2.1 seconds, so a
        /// running character produces roughly 28 pieces an hour. Tier 5+ is
        /// 4.7% of the rarity table, which is about 1.3 an hour into a
        /// 100-slot bank - some seventy hours unattended. Tier 4+ would be 3.1
        /// an hour and about thirty hours, which also clears the target but
        /// fills the bank with pieces nobody looks at.
        /// </summary>
        public const int KeepEquipmentMinimumTier = RarityTier.UltraRare;

        private async Task ProcessMonsterLootDropAsync(long playerId, int monsterId, float lootLuckPct)
        {
            int monsterRegion = ContentRegistry.GetMonsterRegionTier(monsterId);
            if (monsterRegion < 1) monsterRegion = 1;

            // Modul 03: no dedicated "IsRegionalBoss" flag exists anywhere in
            // ContentRegistry - this reuses the exact heuristic already
            // established for Guild War Combat Vanguard WP (activeMonster.Id
            // % 6 == 0), so "regional boss" means the same thing everywhere.
            bool isRegionalBoss = monsterId % 6 == 0;

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            // Modul: Loot Event Feed. Drops are staged here and only
            // published once the transaction actually commits - announcing a
            // drop the database then rolled back would leave the player
            // looking at loot they do not own. Reused across calls (this
            // worker is single-threaded: ExecuteAsync drains the queue one
            // request at a time), so the feed costs no per-kill allocation
            // once the list has grown to its steady-state size.
            _pendingDrops.Clear();

            try
            {
                // Modul: THE BACKPACK IS GONE. Loot is routed by what it is,
                // not by how much room is left.
                //
                // The old model gave a character twenty slots shared between
                // material types and equipment instances, and measured filling
                // at roughly 28 equipment drops an hour - so an idle session
                // hit the wall in about forty minutes and then stopped
                // producing anything at all. No capacity fixes that: eight
                // hours unattended needs 224 slots, and 224 rows of loot is
                // not an inventory anyone wants to sort.
                //
                // Materials go straight to the village chest, which is
                // unbounded and is what crafting, the forge and the market all
                // already spend from.
                //
                // Equipment cannot follow them - the chest is (ItemId,
                // Quantity) and carries no affixes, while every equipment
                // instance has its own affix roll, which is also why two
                // identical-looking pieces are genuinely different objects and
                // cannot stack. So equipment goes to the BANK, which does
                // carry AffixPayload, and only if it clears the keep
                // threshold; everything below scraps into stackable material.
                //
                // At the measured 28 drops an hour, keeping Ultra Rare and
                // above puts 1.3 an hour into a 100-slot bank - about 77 hours
                // - while the other 95% becomes chest material the player can
                // actually use. The session never stops.
                int bankedEquipmentCount = await dbContext.BankEquipmentInstances.CountAsync(e => e.PlayerId == playerId);
                bool bankFull = bankedEquipmentCount >= BankEquipmentCapacity;

                // Roll 1: Crafting Materials, quantity bounded by the loot
                // table entry's authored [MinQuantity, MaxQuantity] range.
                if (Random.Shared.NextDouble() < MaterialDropChance)
                {
                    int monsterLootTableId = GetMonsterLootTableId(monsterId);
                    LootTableEntry[] lootTable = ContentRegistry.GetLootTable(monsterLootTableId).ToArray();
                    if (lootTable.Length > 0)
                    {
                        await GrantMaterialDropAsync(dbContext, playerId, monsterId, lootTable);
                    }
                }

                // Rolls 2-5: Melee, Ranged, Magic, Helper - each fully
                // independent of the others and of the materials roll
                // above.
                await TryRollEquipmentCategoryAsync(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, bankFull, "_melee_weapon_slot_", MeleeWeaponDropChance);
                await TryRollEquipmentCategoryAsync(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, bankFull, "_ranged_weapon_slot_", RangedWeaponDropChance);
                await TryRollEquipmentCategoryAsync(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, bankFull, "_magic_weapon_slot_", MagicWeaponDropChance);
                await TryRollEquipmentCategoryAsync(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, bankFull, "_helper_offhand_", HelperDropChance);

                // Regional Bosses always guarantee at least one extra armor
                // piece on top of whatever the independent rolls produced,
                // preserving the previous "boss kills feel rewarding" floor.
                if (isRegionalBoss)
                {
                    await TryRollEquipmentCategoryAsync(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, bankFull, "_armor_slot_", 1.0);
                }

                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                // Modul: inventory census. Counted after the commit, on the
                // same context, so it reflects everything this kill granted.
                // Two cheap COUNT queries once per kill - the alternative was a
                // counter that made the game unplayable after 20 kills.
                int occupiedSlots = await CountOccupiedBackpackSlotsAsync(dbContext, playerId);

                _playerRegistry.CombatLootDropQueue.Enqueue(new CombatLootDropNotification
                {
                    PlayerId = playerId,
                    ConsumedInventorySlot = true,
                    HasSlotCensus = true,
                    OccupiedSlots = occupiedSlots
                });

                for (int i = 0; i < _pendingDrops.Count; i++)
                {
                    _playerRegistry.OutboundLootDropQueue.Enqueue(_pendingDrops[i]);
                }
                _pendingDrops.Clear();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _pendingDrops.Clear();
                Console.WriteLine($"Combat loot drop failed: {ex.Message}");
            }
        }

        // Modul: no direct MonsterId -> LootTableId lookup existed on
        // ContentRegistry outside of the full MonsterDefinition record -
        // this mirrors the exact lookup SimulationEngine's own combat tick
        // already performs (index by Id - 1 into the Monsters span).
        private static int GetMonsterLootTableId(int monsterId)
        {
            var monsters = ContentRegistry.Monsters;
            if (monsterId < 1 || monsterId > monsters.Length)
            {
                return 0;
            }
            return monsters[monsterId - 1].LootTableId;
        }

        // Modul: Architecture Overhaul, Part 3. Grants a single weighted
        // material entry from the monster's loot table at a quantity drawn
        // from its authored range; MaxQuantity <= 0 (every entry authored
        // before this pass) preserves the legacy one-unit-per-success
        // behavior instead of rolling an empty range.
        // Modul: Loot Event Feed. Instance (was static) so it can publish
        // the drop it just granted onto OutboundLootDropQueue - the player
        // needs to be told WHAT dropped, which is the whole point of the
        // feed, and only this method knows which loot-table entry won the
        // weighted roll.
        private async Task GrantMaterialDropAsync(FolkIdleDbContext dbContext, long playerId, int monsterId, LootTableEntry[] lootTable)
        {
            int totalWeight = 0;
            for (int i = 0; i < lootTable.Length; i++) totalWeight += lootTable[i].Weight;
            if (totalWeight <= 0) return;

            int roll = Random.Shared.Next(totalWeight);
            int cumulative = 0;
            for (int i = 0; i < lootTable.Length; i++)
            {
                cumulative += lootTable[i].Weight;
                if (roll >= cumulative) continue;

                var entry = lootTable[i];
                int quantity = entry.MaxQuantity > 0
                    ? Random.Shared.Next(Math.Max(1, entry.MinQuantity), entry.MaxQuantity + 1)
                    : 1;

                // Modul: GetMaterialString only covers the original 6
                // hardcoded gathering materials (ids 1-6) and falls back
                // to "unknown" for everything else - the 501-525 monster
                // loot range (ids 250+) needs GetItemBaseId, the same
                // full-catalog lookup equipment drops already use.
                string materialItemId = ContentRegistry.GetItemBaseId(entry.ItemId);
                var existing = await dbContext.CommodityRecords
                    .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {materialItemId} FOR UPDATE")
                    .SingleOrDefaultAsync();

                if (existing == null)
                {
                    dbContext.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = materialItemId, Quantity = quantity });
                }
                else
                {
                    existing.Quantity += quantity;
                }

                PublishLootDrop(playerId, monsterId, entry.ItemId, quantity, qualityTier: 0, Network.ResponseLootDropPacket.DropKindMaterial);
                return;
            }
        }

        // Modul: Architecture Overhaul, Part 3. One independent
        // category roll. On success, picks a random matching-category
        // regional item, rolls its rarity tier, and either creates the
        // EquipmentInstance directly (Backpack has room) or scraps it into
        // the monster's own regional raw material deposited straight into
        // the infinite Village Stash (Backpack is full) - the drop is
        // never silently discarded during an offline catch-up burst.
        private async Task TryRollEquipmentCategoryAsync(FolkIdleDbContext dbContext, long playerId, int monsterId, int monsterRegion, float lootLuckPct, bool bankFull, string categorySubstring, double dropChance)
        {
            if (Random.Shared.NextDouble() >= dropChance) return;

            int chosenItemId = SelectRegionalEquipmentItemId(monsterRegion, categorySubstring);
            if (chosenItemId == 0) return;

            int tier = RarityTier.RollTier(lootLuckPct);
            string baseItemId = ContentRegistry.GetItemBaseId(chosenItemId);

            // Below the keep threshold, or with nowhere left to put it: the
            // piece is scrapped into stackable material rather than dropped on
            // the floor. The player still SEES it happen - the loot feed
            // carries a scrap event - so a session's output stays legible
            // instead of quietly becoming nothing.
            //
            // The threshold is the whole reason this stays sustainable. At the
            // measured 28 drops an hour, Ultra Rare and above is 4.7% of them,
            // so about 1.3 an hour reaches a 100-slot bank; the rest becomes
            // material. Scrapping a Godly piece because the bank happens to be
            // full is a real loss, which is why the bank check is separate and
            // reported differently below.
            if (tier < KeepEquipmentMinimumTier || bankFull)
            {
                LootTableEntry[] monsterLootTable = ContentRegistry.GetLootTable(GetMonsterLootTableId(monsterId)).ToArray();
                string scrapMaterialId = monsterLootTable.Length > 0
                    ? ContentRegistry.GetItemBaseId(monsterLootTable[0].ItemId)
                    : "iron_ore";

                // Rarer pieces scrap into more material, so a Legendary that
                // could not be kept is still worth more than a Common that was
                // never going to be.
                int scrapValue = Math.Max(1, tier * monsterRegion);
                await Domain.Economy.InventoryAndStashSystem.DepositToStashAsync(dbContext, playerId, scrapMaterialId, scrapValue);

                int scrapItemId = monsterLootTable.Length > 0 ? monsterLootTable[0].ItemId : 0;
                // The TIER is carried on the scrap event too, so the loot log
                // can say "a Legendary was scrapped" rather than reporting a
                // pile of ore with no history.
                PublishLootDrop(playerId, monsterId, scrapItemId, scrapValue, (byte)tier, Network.ResponseLootDropPacket.DropKindScrap);
                return;
            }

            // Kept pieces go to the BANK, not to a backpack - it is the only
            // store that carries AffixPayload, and the affixes are what make
            // one Sentry Helm different from another.
            string affixPayload = BuildAffixPayload(tier, monsterRegion, baseItemId);
            dbContext.BankEquipmentInstances.Add(new BankEquipmentInstance
            {
                BaseItemId = baseItemId,
                PlayerId = playerId,
                QualityTier = tier,
                AffixPayload = affixPayload,
                IsAffixLocked = false
            });

            PublishLootDrop(playerId, monsterId, chosenItemId, 1, (byte)tier, Network.ResponseLootDropPacket.DropKindEquipment);
        }

        // Modul: Loot Event Feed. Enqueue only - the actual socket write is
        // NetworkBroadcastSystem's job (it owns the connections; this engine
        // never touches one). Called from inside the drop transaction but
        // only AFTER the row has been added, and the enqueued items are
        // discarded if that transaction later rolls back - see
        // ProcessMonsterLootDropAsync's own note on _pendingDrops.
        private void PublishLootDrop(long playerId, int monsterId, int itemId, int quantity, byte qualityTier, byte dropKind)
        {
            if (itemId <= 0 || quantity <= 0) return;

            _pendingDrops.Add(new Network.ResponseLootDropPacket
            {
                PlayerId = playerId,
                ItemId = itemId,
                Quantity = quantity,
                MonsterId = monsterId,
                QualityTier = qualityTier,
                DropKind = dropKind
            });
        }

        // Modul 10/11/12: there is no hand-authored per-monster equipment drop
        // table anywhere in this codebase. This derives one deterministically
        // from ContentRegistry.ItemDefinitions' existing RegionTier field
        // (already used for potion tiering) - every weapon/armor item whose
        // RegionTier matches the killed monster's region is a valid candidate.
        // Reservoir sampling of 1 keeps this a single allocation-free pass
        // over the bounded static item table.
        private static int SelectRegionalEquipmentItemId(int monsterRegion, string categorySubstring)
        {
            ReadOnlySpan<ItemDefinition> items = ContentRegistry.ItemDefinitions;
            int matchCount = 0;
            int chosenId = 0;

            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].RegionTier != monsterRegion) continue;

                string baseItemId = ContentRegistry.GetItemBaseId(items[i].Id);
                if (!baseItemId.Contains(categorySubstring)) continue;

                matchCount++;
                if (Random.Shared.Next(matchCount) == 0)
                {
                    chosenId = items[i].Id;
                }
            }

            return chosenId;
        }

        // Modul: Affix System Unification. Rolls from AffixRegistry (GDD
        // Module 14 section 1.3) rather than the previous hand-rolled
        // four-candidate list keyed "1".."5".
        //
        // The old version had two fatal problems. It wrote numeric keys that
        // AffixRerollEngine did not understand and that EquipmentSlotEngine
        // only partially read - "5" (flat HP) was written by every single drop
        // and read by nothing at all, so the GDD's headline flat affix never
        // applied. And it ignored slot legality entirely, so it could only
        // produce attack/defense/crit/luck regardless of what the GDD says
        // each slot may roll.
        private static string BuildAffixPayload(int tier, int region, string baseItemId)
        {
            int affixCount = RarityTier.GetAffixCount(tier);
            var affixes = new Dictionary<string, int>(affixCount);
            AffixRegistry.RollAffixes(baseItemId, region, tier, affixCount, affixes);
            return JsonSerializer.Serialize(affixes);
        }
    }
}
