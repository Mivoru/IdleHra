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

        // Modul: the names, server-side. The client has had this list since it
        // shipped and the server had only the constants, so anything the server
        // wanted to SAY about a rarity had to ship a number and hope.
        private static readonly string[] _tierNames =
        {
            "", "Normal", "Common", "Uncommon", "Rare", "Ultra Rare", "Epic",
            "Legendary", "Mythic", "Relic", "Ancient", "Divine", "Demonic",
            "Godly", "Transcendent",
        };

        public static string GetName(int tier)
            => tier >= 1 && tier < _tierNames.Length ? _tierNames[tier] : $"tier {tier}";

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
                    c.EquippedAmuletId,
                    c.EquippedRingId
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
                if (character.EquippedAmuletId.HasValue) wornCount++;
                if (character.EquippedRingId.HasValue) wornCount++;
            }

            int backpackEquipment = ownedEquipment - wornCount;
            if (backpackEquipment < 0) backpackEquipment = 0;

            return materialStacks + backpackEquipment;
        }

        // Modul: gathering grants. Static and drained by this engine's own
        // poll loop, exactly like DropRequestQueue above - SimulationEngine
        // holds a LootTableEngine, not this class, so an instance call from
        // the tick was never available.
        public static readonly ConcurrentQueue<GatheredMaterialGrant> GatheringGrantQueue = new();

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

                while (GatheringGrantQueue.TryDequeue(out var gathered))
                {
                    await GrantGatheredMaterialAsync(gathered.PlayerId, gathered.ActivityId, gathered.ItemId, gathered.Quantity);
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

        // Modul: ONE equipment roll against the monster's own table, replacing
        // four per-category rolls against a region-wide pool.
        //
        // The four constants this replaces were 0.50/0.40/0.40/0.33 percent -
        // 1.63% of kills produced a piece of gear, and every one of those pieces
        // was a weapon or an offhand, because armour was on a separate boss-only
        // roll. See EquipmentDropTable for the rest of what that cost.
        //
        // 2% is deliberately a shade above the old total: the pool now includes
        // the armour a character actually needs for five of its seven slots, so
        // holding the rate flat would have made each individual slot rarer than
        // it was. Per-slot rates are no longer a knob at all - a monster's table
        // is rolled uniformly, and which slots that monster offers is the table's
        // business.
        //
        // Modul: 2% -> 5%, from a live playtest and then from the arithmetic.
        //
        // Per HOUR the old rate was already even - three and a half to five
        // pieces in every region, because kill times are tuned to be flat. Per
        // REGION it was not, because regions are not the same length, and
        // region 1 is two and a bit hours: EIGHT drops for SEVEN slots. A new
        // player was dressed once, exactly, with nothing spare.
        //
        // Fusion is what settles it. It takes THREE IDENTICAL pieces at the
        // same rarity, and region 1 produced 2.5 of each item type spread
        // across fourteen rarity tiers - so the system did not merely feel slow
        // to a new player, it was unreachable, and nothing on screen said so.
        //
        // Pacing is safe: the model already assumes the player holds the best
        // weapon their region authors, so more drops move real play TOWARD that
        // assumption rather than past it. This is the opposite of the attack
        // speed defect, which multiplied a number the model never saw.
        //
        // One rate for regulars and bosses alike. A boss is killed once per
        // region, so a separate rate would be a knob with nothing on the end
        // of it. Test_Drops_HowLongUntilAPlayerIsDressed prints what this
        // buys, and is the thing to re-read before touching it again.
        //
        // Modul: 15%, not 5% - and this is a THIRD change nobody asked for on
        // its own. Monster HP was tripled to make the ladder bite, which
        // triples how long a kill takes, and a drop is rolled PER KILL. Left at
        // 5% the raise would have quietly undone the drop-rate decision made
        // two changes earlier, so drops per HOUR are held where they were
        // rather than being cut to a third by a side effect.
        //
        // If a slower drip is wanted, this is the one line to change - the
        // difficulty raise does not depend on it.
        public const double EquipmentDropChance = 0.150;

        // Modul: approximate backpack capacity used purely to decide
        // whether an equipment drop must be redirected to overflow
        // handling - mirrors StateCheckpointManager's default 20-slot
        // Backpack (see InventorySpaceRemaining's default initializer);
        // the Human race's variable bonus slots are intentionally not
        // modeled here since this check only needs to be conservative,
        // never exact.
        // Modul: the village chest is UNBOUNDED, so nothing here counts slots.
        //
        // There was a keep-threshold and a 100-slot cap in an earlier version
        // of this pass. Both are gone: a cap only forces the engine to decide
        // what to destroy, and that decision belongs to the player. The chest
        // grows; the player sells or bins what they do not want.

        private async Task ProcessMonsterLootDropAsync(long playerId, int monsterId, float lootLuckPct)
        {
            int monsterRegion = ContentRegistry.GetMonsterRegionTier(monsterId);
            if (monsterRegion < 1) monsterRegion = 1;

            // Modul: ContentRegistry.IsRegionalBoss, not `% 6 == 0`. The old
            // heuristic matched no real boss and four ordinary monsters - see
            // that method for what it cost.
            bool isRegionalBoss = ContentRegistry.IsRegionalBoss(monsterId);

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

                // Roll 2: equipment, from THIS monster's table. Independent of
                // the materials roll above, so a kill can pay both, either or
                // neither.
                TryRollEquipment(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, EquipmentDropChance);

                // Regional bosses always drop one piece on top of that, which
                // is the whole of what makes a boss kill worth walking to. It
                // comes from the boss's own table, so - unlike the armour-only
                // roll this replaces - what a boss guarantees is whatever that
                // boss is authored to carry.
                if (isRegionalBoss)
                {
                    TryRollEquipment(dbContext, playerId, monsterId, monsterRegion, lootLuckPct, 1.0);
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

        // Modul: one equipment roll against the monster's authored table.
        // On success, picks uniformly from what THIS monster drops, rolls the
        // rarity tier and creates the EquipmentInstance.
        //
        // No longer async: it never awaited anything. The DbSet.Add below is
        // synchronous and the single SaveChangesAsync happens once for the whole
        // kill in the caller, so the four awaits this replaces each cost a state
        // machine to return an already-completed task.
        private void TryRollEquipment(FolkIdleDbContext dbContext, long playerId, int monsterId, int monsterRegion, float lootLuckPct, double dropChance)
        {
            if (Random.Shared.NextDouble() >= dropChance) return;

            ReadOnlySpan<int> table = EquipmentDropTable.GetDrops(monsterId);
            if (table.Length == 0) return;

            int chosenItemId = table[Random.Shared.Next(table.Length)];
            if (chosenItemId == 0) return;

            int tier = RarityTier.RollTier(lootLuckPct);
            string baseItemId = ContentRegistry.GetItemBaseId(chosenItemId);

            // Modul: NOTHING IS EVER DESTROYED ON THE WAY IN.
            //
            // An earlier version of this scrapped anything below a rarity
            // threshold into material. That was wrong, and wrong in a way the
            // player would have felt immediately: the forge upgrades a piece by
            // consuming two others, so a low-tier drop is not junk - it is the
            // fuel for raising a better one. Auto-scrapping it destroys the
            // upgrade path to save a slot that no longer needs saving.
            //
            // Every piece is stored. Whether it is worth keeping is the
            // player's call, made in the chest with a sell or a bin, not the
            // server's made silently at 3am.
            string affixPayload = BuildAffixPayload(tier, monsterRegion, baseItemId);

            // Modul: EquipmentInstances, NOT the bank.
            //
            // An earlier version of this pass routed loot to
            // BankEquipmentInstances because that table carries AffixPayload
            // and I had mistaken the backpack's cap for a property of its
            // table. It is not - the cap lived in the census and the tick gate,
            // both of which are gone. Routing to the bank quietly broke
            // EQUIPPING: EquipmentSlotEngine reads EquipmentInstances, as do
            // forge fusion, affix reroll and market listing, so a player could
            // loot a Legendary and never be able to wear, upgrade or sell it.
            //
            // This table is the chest's equipment half. It is unbounded now.
            dbContext.EquipmentInstances.Add(new EquipmentInstance
            {
                BaseItemId = baseItemId,
                PlayerId = playerId,
                QualityTier = tier,
                AffixPayload = affixPayload,
                IsAffixLocked = false
            });

            PublishLootDrop(playerId, monsterId, chosenItemId, 1, (byte)tier, Network.ResponseLootDropPacket.DropKindEquipment);

            // Modul: A RARE DROP IS WORTH SAYING OUT LOUD.
            //
            // Asked for directly: "when someone gets a tier 8 or better, write
            // them a congratulation in chat". The reroll path has announced its
            // Epic-and-above results since it shipped and drops - the far more
            // common way a player meets a rare item - said nothing at all.
            //
            // Eight of fourteen, so it fires for roughly the top half of the
            // rarity ladder and stays rare enough to mean something. Enqueued
            // rather than sent: the queue is bounded and drained by the chat
            // dispatch worker, so an unlucky flood drops announcements instead
            // of stalling the loot path.
            if (tier >= AnnounceableRarityTier)
            {
                Domain.Social.ChatEngine.EnqueueSystemAnnouncement(
                    $"{PlayerNameResolver.GetCachedOrFallback(playerId)} found a {RarityTier.GetName(tier)} {Readable(baseItemId)} " +
                    $"from {ContentRegistry.GetMonsterName(monsterId)}. Congratulations!");
            }
        }

        /// <summary>
        /// "golden_birch_axe_tool" -> "Golden Birch Axe". The catalogue speaks
        /// in slugs and an announcement has to speak in words.
        /// </summary>
        private static string Readable(string baseItemId)
        {
            if (string.IsNullOrEmpty(baseItemId)) return "item";

            var builder = new System.Text.StringBuilder(baseItemId.Length);
            foreach (string part in baseItemId.Split('_'))
            {
                if (part.Length == 0 || part == "tool" || part == "slot") continue;
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1) builder.Append(part, 1, part.Length - 1);
            }
            return builder.Length > 0 ? builder.ToString() : baseItemId;
        }

        /// <summary>
        /// Rarity 8 of 14 - the top half of the ladder. High enough that a line
        /// in global chat is an event rather than noise.
        /// </summary>
        public const int AnnounceableRarityTier = 8;

        // Modul: Loot Event Feed. Enqueue only - the actual socket write is
        // NetworkBroadcastSystem's job (it owns the connections; this engine
        // never touches one). Called from inside the drop transaction but
        // only AFTER the row has been added, and the enqueued items are
        // discarded if that transaction later rolls back - see
        // ProcessMonsterLootDropAsync's own note on _pendingDrops.
        /// <summary>
        /// Grants one gathered material and publishes it to the loot feed.
        ///
        /// Modul: GATHERING GRANTED NOTHING. The 10Hz tick rolled the node's
        /// loot table, picked a winning entry, decremented a backpack counter
        /// and then simply broke out of the loop - there was no write to
        /// CommodityRecords anywhere on the gathering path and no queue
        /// carrying one. Chopping wood for an hour produced mastery XP and not
        /// a single log. The offline catch-up DID grant materials, which is why
        /// the gap survived: logging back in after a night away filled the
        /// chest, so the chest was never empty enough to make anyone look.
        ///
        /// The publish is the other half of the same fix - combat has had a
        /// visible drop feed since the loot event feed landed and gathering
        /// showed nothing at all, so there was no way to tell a working node
        /// from a broken one.
        /// </summary>
        public async Task GrantGatheredMaterialAsync(long playerId, long activityId, int itemId, int quantity)
        {
            if (itemId <= 0 || quantity <= 0) return;

            string materialItemId = ContentRegistry.GetItemBaseId(itemId);
            if (string.IsNullOrEmpty(materialItemId)) return;

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var strategy = dbContext.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                dbContext.ChangeTracker.Clear();
                using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);

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

                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            });

            // MonsterId doubles as "where this came from" on the wire; a
            // gathering node id is as much an origin as a monster id is.
            _playerRegistry.OutboundLootDropQueue.Enqueue(new Network.ResponseLootDropPacket
            {
                PlayerId = playerId,
                ItemId = itemId,
                Quantity = quantity,
                MonsterId = (int)activityId,
                QualityTier = 0,
                DropKind = Network.ResponseLootDropPacket.DropKindMaterial
            });
        }

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

        // Modul: SelectRegionalEquipmentItemId lived here. It scanned the whole
        // item catalogue per roll for anything in the region whose BaseId
        // contained a category substring, which is the region-wide pool that
        // EquipmentDropTable replaces - and the substring match is where the
        // "_ranged_" / "_range_" typo silently deleted every canonical bow from
        // the game. The tables are built once at first use now, so the per-kill
        // cost is an index into an int[] instead of a 437-entry scan.

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
