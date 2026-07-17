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

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;
        private CancellationTokenSource _cts = new();

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
        private const double MaterialDropChance = 0.35;
        private const double MeleeWeaponDropChance = 0.0050;
        private const double RangedWeaponDropChance = 0.0040;
        private const double MagicWeaponDropChance = 0.0040;
        private const double HelperDropChance = 0.0033;

        // Modul: approximate backpack capacity used purely to decide
        // whether an equipment drop must be redirected to overflow
        // handling - mirrors StateCheckpointManager's default 20-slot
        // Backpack (see InventorySpaceRemaining's default initializer);
        // the Human race's variable bonus slots are intentionally not
        // modeled here since this check only needs to be conservative,
        // never exact.
        private const int BaseBackpackCapacity = 20;

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

            try
            {
                int backpackCount = await dbContext.EquipmentInstances.CountAsync(e => e.PlayerId == playerId);
                bool backpackFull = backpackCount >= BaseBackpackCapacity;

                // Roll 1: Crafting Materials, quantity bounded by the loot
                // table entry's authored [MinQuantity, MaxQuantity] range.
                if (Random.Shared.NextDouble() < MaterialDropChance)
                {
                    int monsterLootTableId = GetMonsterLootTableId(monsterId);
                    LootTableEntry[] lootTable = ContentRegistry.GetLootTable(monsterLootTableId).ToArray();
                    if (lootTable.Length > 0)
                    {
                        await GrantMaterialDropAsync(dbContext, playerId, lootTable);
                    }
                }

                // Rolls 2-5: Melee, Ranged, Magic, Helper - each fully
                // independent of the others and of the materials roll
                // above.
                await TryRollEquipmentCategoryAsync(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, backpackFull, "_melee_weapon_slot_", MeleeWeaponDropChance);
                await TryRollEquipmentCategoryAsync(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, backpackFull, "_ranged_weapon_slot_", RangedWeaponDropChance);
                await TryRollEquipmentCategoryAsync(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, backpackFull, "_magic_weapon_slot_", MagicWeaponDropChance);
                await TryRollEquipmentCategoryAsync(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, backpackFull, "_helper_offhand_", HelperDropChance);

                // Regional Bosses always guarantee at least one extra armor
                // piece on top of whatever the independent rolls produced,
                // preserving the previous "boss kills feel rewarding" floor.
                if (isRegionalBoss)
                {
                    await TryRollEquipmentCategoryAsync(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, backpackFull, "_armor_slot_", 1.0);
                }

                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.CombatLootDropQueue.Enqueue(new CombatLootDropNotification
                {
                    PlayerId = playerId,
                    ConsumedInventorySlot = true
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
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
        private static async Task GrantMaterialDropAsync(FolkIdleDbContext dbContext, long playerId, LootTableEntry[] lootTable)
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
        private async Task TryRollEquipmentCategoryAsync(FolkIdleDbContext dbContext, long playerId, int monsterId, int monsterRegion, float lootLuckPct, bool backpackFull, string categorySubstring, double dropChance)
        {
            if (Random.Shared.NextDouble() >= dropChance) return;

            int chosenItemId = SelectRegionalEquipmentItemId(monsterRegion, categorySubstring);
            if (chosenItemId == 0) return;

            int tier = RarityTier.RollTier(lootLuckPct);
            string baseItemId = ContentRegistry.GetItemBaseId(chosenItemId);
            bool isWeapon = baseItemId.Contains("_weapon_slot_");

            if (backpackFull)
            {
                LootTableEntry[] monsterLootTable = ContentRegistry.GetLootTable(GetMonsterLootTableId(monsterId)).ToArray();
                string scrapMaterialId = monsterLootTable.Length > 0
                    ? ContentRegistry.GetItemBaseId(monsterLootTable[0].ItemId)
                    : "iron_ore";
                int scrapValue = Math.Max(1, tier * monsterRegion);
                await Domain.Economy.InventoryAndStashSystem.DepositToStashAsync(dbContext, playerId, scrapMaterialId, scrapValue);
                return;
            }

            string affixPayload = BuildAffixPayload(tier, monsterRegion, isWeapon);
            dbContext.EquipmentInstances.Add(new EquipmentInstance
            {
                BaseItemId = baseItemId,
                PlayerId = playerId,
                QualityTier = tier,
                AffixPayload = affixPayload,
                IsAffixLocked = false
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

        // Modul 14: secondary attribute formulas. Flat HP and Flat Defense use
        // the exact GDD formulas given; Flat Attack mirrors Defense's shape
        // (no explicit attack formula was given) so weapon drops scale
        // consistently with armor drops. Crit/Luck percentage affixes use an
        // invented but documented base-plus-per-tier-step growth increment,
        // since the GDD does not specify exact base/growth numbers for them.
        private static string BuildAffixPayload(int tier, int region, bool isWeapon)
        {
            int affixCount = RarityTier.GetAffixCount(tier);
            var affixes = new Dictionary<string, int>();

            double tierScale = tier - 1;
            int flatHp = (int)Math.Floor(15.0 * region * Math.Pow(1.22, tierScale));
            int flatDefense = (int)Math.Floor(2.0 * region * Math.Pow(1.18, tierScale));
            int flatAttack = (int)Math.Floor(3.0 * region * Math.Pow(1.20, tierScale));
            int critPct = 10 + (int)(tierScale * 2);
            int luckPct = 5 + (int)(tierScale * 1);

            // Priority order: the gear-slot-appropriate primary stat first,
            // then HP, then crit, then luck - truncated to affixCount.
            var orderedKeys = isWeapon
                ? new[] { ("1", flatAttack), ("5", flatHp), ("3", critPct), ("4", luckPct) }
                : new[] { ("2", flatDefense), ("5", flatHp), ("3", critPct), ("4", luckPct) };

            int slotsFilled = 0;
            for (int i = 0; i < orderedKeys.Length && slotsFilled < affixCount; i++)
            {
                affixes[orderedKeys[i].Item1] = orderedKeys[i].Item2;
                slotsFilled++;
            }

            // Tier 13-14 grant a 5th affix slot; add the secondary gear-slot
            // stat (defense on weapons, attack on armor) as a minor bonus.
            if (affixCount >= 5 && slotsFilled < affixCount)
            {
                string secondaryKey = isWeapon ? "2" : "1";
                int secondaryValue = isWeapon ? flatDefense : flatAttack;
                affixes[secondaryKey] = secondaryValue;
            }

            return JsonSerializer.Serialize(affixes);
        }
    }
}
