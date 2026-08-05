using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;
using System.Data;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Economy
{
    public class CraftingEngine
    {
        private readonly IDbContextFactory<FolkIdleDbContext> _contextFactory;
        private readonly PlayerSessionRegistry _playerRegistry;
        private readonly GuildWarEngine? _guildWarEngine;
        private readonly RetryingDbContextOptions _retryingDbOptions;

        public CraftingEngine(IDbContextFactory<FolkIdleDbContext> contextFactory, PlayerSessionRegistry playerRegistry, RetryingDbContextOptions retryingDbOptions, GuildWarEngine? guildWarEngine = null)
        {
            _contextFactory = contextFactory;
            _playerRegistry = playerRegistry;
            _retryingDbOptions = retryingDbOptions;
            _guildWarEngine = guildWarEngine;
        }

        // Modul: Full-Stack Expansion, Part 4. The 14 rarity tiers'
        // baseline weights (tier 0 Common 50.0 through tier 13
        // Transcendent 0.0001) - the same strictly-decreasing geometric
        // family CombatLootEngine's combat-drop table uses, so crafted and
        // dropped rarity distributions stay in one economy.
        public const int RarityTierCount = 14;

        // Modul: Full-Stack Expansion, Part 4. Rolls a crafted item's
        // bonus rarity tier (0-13) with exactly zero managed heap
        // allocations: the 14 cumulative weight bounds are built in a
        // stackalloc double[14] and evaluated entirely on the stack -
        // no arrays, no LINQ, no boxing. craftingSkill and workshopLevel
        // shift probability weight toward high-tier outcomes by
        // compounding a per-tier multiplier (each successive tier's
        // weight is scaled by the multiplier one more time than the tier
        // below it), so higher inputs flatten the baseline decay without
        // ever making a lower tier less likely than a higher one.
        // seedRandomValue is the caller-supplied uniform [0, 1) roll -
        // passing it in keeps this function pure and deterministic for
        // tests while the live call site feeds Random.Shared.NextDouble().
        public static int RollCraftedRarity(int craftingSkill, int workshopLevel, double seedRandomValue)
        {
            if (craftingSkill < 0) craftingSkill = 0;
            if (workshopLevel < 0) workshopLevel = 0;
            if (seedRandomValue < 0.0) seedRandomValue = 0.0;
            if (seedRandomValue >= 1.0) seedRandomValue = 0.9999999999;

            Span<double> cumulativeBounds = stackalloc double[RarityTierCount];

            double tierMultiplier = 1.0 + craftingSkill * 0.002 + workshopLevel * 0.05;

            double weight = 50.0;
            double compounded = 1.0;
            double runningTotal = 0.0;
            for (int tier = 0; tier < RarityTierCount; tier++)
            {
                runningTotal += weight * compounded;
                cumulativeBounds[tier] = runningTotal;

                // Baseline decay mirrors the established drop-table curve:
                // halving-to-fifthing steps from 50.0 down to 0.0001.
                weight *= tier switch
                {
                    0 => 0.5,     // 50 -> 25
                    1 => 0.5,     // 25 -> 12.5
                    2 => 0.4,     // 12.5 -> 5
                    3 => 0.5,     // 5 -> 2.5
                    4 => 0.4,     // 2.5 -> 1
                    5 => 0.5,     // 1 -> 0.5
                    6 => 0.5,     // 0.5 -> 0.25
                    7 => 0.4,     // 0.25 -> 0.1
                    8 => 0.5,     // 0.1 -> 0.05
                    9 => 0.2,     // 0.05 -> 0.01
                    10 => 0.5,    // 0.01 -> 0.005
                    11 => 0.2,    // 0.005 -> 0.001
                    _ => 0.1      // 0.001 -> 0.0001
                };
                compounded *= tierMultiplier;
            }

            double roll = seedRandomValue * runningTotal;
            for (int tier = 0; tier < RarityTierCount; tier++)
            {
                if (roll < cumulativeBounds[tier])
                {
                    return tier;
                }
            }

            return RarityTierCount - 1;
        }

        // Modul: Full-Stack Expansion, Part 4. Hard forge/affix-upgrade tier
        // caps by the item's structural gear band - two region tiers per band:
        // band 1 caps at tier 5, band 2 at 10, and bands 3+ at the global
        // MaxQualityTier ceiling (the task's nominal caps of 15/20/25 exceed
        // the 14-tier system's hard maximum of 13 and clamp to it).
        // ForgeSplicingEngine rejects any fusion whose target already sits at
        // its band cap.
        //
        // This is a property of the ITEM, not a gate on the player, which is
        // why the region-unlock rework left it alone. It used to be phrased as
        // EquipmentLevelGate.DeriveRequiredLevel(regionTier, 0) < 20 - which
        // was only ever (regionTier - 1) * 10 < 20 with the quality term zeroed
        // out, i.e. an arithmetic trick for "first two regions" wearing the
        // costume of a level check. Same bands, same numbers, said directly:
        // reading it the old way invited someone to "unify" it with a
        // progression gate it never belonged to.
        public static int GetMaxForgeTierForRegion(int regionTier)
        {
            if (regionTier <= 2) return 5;
            if (regionTier <= 4) return 10;
            return ForgeSplicingEngine.MaxQualityTier;
        }

        public async Task ExecuteCraftingAsync(long playerId, int recipeResultItemId)
        {
            if (!ContentRegistry.TryGetRecipe(recipeResultItemId, out var recipe))
            {
                return;
            }

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            // Modul: the delegate returns (success, quantity) instead of
            // throwing-and-catching for the expected "insufficient
            // materials" outcome - that is a normal business result, not a
            // failure, and must not be retried. A genuine Serializable
            // conflict or transient failure is left to propagate out of the
            // delegate so CreateExecutionStrategy retries it; this method no
            // longer swallows exceptions itself, matching every other
            // fire-and-forget dispatch site's SafeDispatchAsync wrapper.
            (bool success, int quantityProduced) = await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var player = await context.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                if (player == null) return (false, 0);

                var charRecord = await context.CharacterRecords.FirstOrDefaultAsync(c => c.PlayerId == playerId && c.Id == player.PlayerGuid);
                long geneticVector = 0;
                if (charRecord != null)
                {
                    var lineage = await context.CharacterLineages.FirstOrDefaultAsync(l => l.CharacterId == charRecord.Id);
                    if (lineage != null)
                    {
                        geneticVector = lineage.GeneticVector;
                    }
                }

                var gv = new GeneticVector(geneticVector);
                byte race = gv.LocusRace.Dominant;

                int quantityProduced = 1;

                // Kobold passive: 10% chance to duplicate bar outcome in smelting (Prof 2)
                if (recipe.ProfessionType == 2 && race == RaceIds.Kobold)
                {
                    if (Random.Shared.Next(100) < 10)
                    {
                        quantityProduced++;
                    }
                }

                // Modul: the Vodník passive used to be pointed at from here,
                // "moved to the item metadata payload in
                // ExecuteEquipmentCraftingAsync". That method is gone with the
                // equipment recipes, so the pointer went with it - a comment
                // naming a method nobody can find is worse than none.

                // Modul: Full-Stack Expansion, Part 3. Check and deduct
                // materials through the unified Backpack+Stash interface -
                // availability is the combined balance, the Backpack drains
                // first, and the remainder comes seamlessly out of the
                // Village Stash inside this same Serializable transaction.
                // Modul: Crafting Tree UI. These two lookups used to stringify
                // the raw numeric Mat1Id/Mat2Id ("93", "129"). Nothing in this
                // game has ever stored a commodity under a numeric key -
                // every writer of CommodityRecords/VillageStashInstances uses
                // a BaseId slug (AuthenticationEngine seeds
                // GetMaterialString(1), CombatLootEngine grants
                // GetItemBaseId(entry.ItemId), VillageManagementEngine spends
                // "raw_log"/"copper_ore", gold is "gold"). So the unified
                // balance lookup could never match a row,
                // TryConsumeUnifiedAsync always reported insufficient
                // materials, and every one of the 103 recipes in
                // ContentRegistry was silently unfulfillable no matter how
                // much of the input material the player actually held.
                //
                // Same shape as the PlaceLimitOrder BUY bug fixed earlier: a
                // numeric content id used directly as a real game-object
                // identity string instead of being resolved through
                // GetItemBaseId first.
                if (recipe.Mat1Id > 0 && recipe.Mat1Count > 0)
                {
                    string mat1ItemId = ContentRegistry.GetItemBaseId(recipe.Mat1Id);
                    if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, mat1ItemId, recipe.Mat1Count))
                    {
                        await transaction.RollbackAsync();
                        return (false, 0);
                    }
                }

                if (recipe.Mat2Id > 0 && recipe.Mat2Count > 0)
                {
                    string mat2ItemId = ContentRegistry.GetItemBaseId(recipe.Mat2Id);
                    if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, mat2ItemId, recipe.Mat2Count))
                    {
                        await transaction.RollbackAsync();
                        return (false, 0);
                    }
                }

                // Modul: crafting output. This was the missing half of the
                // recipe loop: the materials were consumed, the transaction
                // committed, a completion notification was enqueued - and the
                // crafted item was never granted anywhere. The tick-thread
                // drain only bumps a quest counter and guild-war points, so
                // every one of the 103 recipes destroyed its inputs and
                // produced nothing. (CraftingCompletionNotification's own
                // comment shows the intent was for the tick thread to "adjust
                // inventory balances" - it never did.)
                //
                // Granted inside the same Serializable transaction as the
                // consumption, so a craft is all-or-nothing rather than able
                // to eat materials and then fail to pay out.
                await GrantCraftedOutputAsync(context, playerId, recipe, quantityProduced);

                // Modul: lifetime statistics. Counted inside the same
                // transaction as the grant, so the counter cannot disagree with
                // what the player actually received - a craft that rolls back
                // rolls this back with it.
                if (player != null)
                {
                    player.TotalItemsCrafted += quantityProduced;
                }

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return (true, quantityProduced);
            });

            if (success)
            {
                // Enqueue completion
                _playerRegistry.CraftingCompletionQueue.Enqueue(new CraftingCompletionNotification
                {
                    PlayerId = playerId,
                    CraftedItemId = recipe.ResultItemId,
                    Quantity = quantityProduced
                });
            }
        }

        // Modul: crafting output. Where a crafted item lands depends on what it
        // is, and the recipe's ProfessionType is the authority on that:
        //   2 Smelting  -> metal bars, stackable
        //   3 Equipment -> a real EquipmentInstance
        //   4 Cooking   -> food consumables, stackable
        //   5 Alchemy   -> potions, stackable
        // Stackables go to CommodityRecords (the backpack), keyed by BaseId
        // exactly like every other commodity writer in this codebase.
        // GlobalEventType.MasterArtisan. A flat chance of one extra unit from
        // any craft - it applies to bars, food, potions and equipment alike,
        // so no profession is left out of its own event.
        private const int MasterArtisanEventId = 3;
        private const int MasterArtisanBonusYieldPct = 25;

        private static async Task GrantCraftedOutputAsync(FolkIdleDbContext context, long playerId, ContentRegistry.RecipeDefinition recipe, int quantityProduced)
        {
            if (quantityProduced <= 0 || recipe.ResultItemId <= 0) return;

            // Modul: MasterArtisan finally does something. GlobalEventType 3
            // was scheduled by the rotation like any other event, but no code
            // anywhere on the server read it - for a quarter of every rotation
            // the game announced an event with no effect, and the client
            // banner had to say so. This mirrors DiamondStar's hook in
            // ForgeSplicingEngine: one comparison, at the point the bonus
            // applies.
            if (SimulationEngine.ActiveGlobalEventId == MasterArtisanEventId &&
                Random.Shared.Next(100) < MasterArtisanBonusYieldPct)
            {
                quantityProduced++;
            }

            string resultBaseId = ContentRegistry.GetItemBaseId(recipe.ResultItemId);
            if (string.IsNullOrEmpty(resultBaseId)) return;

            const int EquipmentAssemblyProfession = 3;
            if (recipe.ProfessionType == EquipmentAssemblyProfession)
            {
                // GDD Module 14 section 2: forged equipment is a structural
                // base "before affix attachment or rarity modification occurs",
                // so a craft yields Normal rarity. Rarity is raised afterwards
                // through the Forge's fusion system, not at the bench. Affixes
                // still roll, because even Normal grants one (GDD 5.2).
                int regionTier = ResolveRegionTierForItem(recipe.ResultItemId);

                for (int i = 0; i < quantityProduced; i++)
                {
                    var rolled = new Dictionary<string, int>();
                    AffixRegistry.RollAffixes(resultBaseId, regionTier, itemRarityTier: RarityTier.Normal, affixCount: RarityTier.GetAffixCount(RarityTier.Normal), destination: rolled);

                    context.EquipmentInstances.Add(new EquipmentInstance
                    {
                        BaseItemId = resultBaseId,
                        PlayerId = playerId,
                        QualityTier = RarityTier.Normal,
                        AffixPayload = System.Text.Json.JsonSerializer.Serialize(rolled),
                        IsAffixLocked = false
                    });
                }
                return;
            }

            var existing = await context.CommodityRecords
                .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {resultBaseId} FOR UPDATE")
                .SingleOrDefaultAsync();

            if (existing == null)
            {
                context.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = resultBaseId, Quantity = quantityProduced });
            }
            else
            {
                existing.Quantity += quantityProduced;
            }
        }

        private static int ResolveRegionTierForItem(int itemId)
        {
            ReadOnlySpan<ItemDefinition> items = ContentRegistry.ItemDefinitions;
            if (itemId < 1 || itemId > items.Length) return 1;

            int authored = items[itemId - 1].RegionTier;
            return authored > 0 ? authored : 1;
        }

        // Modul: ExecuteEquipmentCraftingAsync IS GONE, with CraftingReceptuary
        // behind it.
        //
        // EQUIPMENT IS MONSTER LOOT AND TOOLS ARE CRAFTED. Nothing is both.
        // This method was the one path that broke that rule: three recipes
        // turning ore into armour, a second crafting system beside the real
        // 31-recipe tool tree above, reachable from its own opcode and its own
        // REST surface. It survived this long because it was written first and
        // because the duplication was logged as a cleanup item rather than as
        // the content decision it actually was.
        //
        // ExecuteCraftingAsync is the one that stays: ContentRegistry recipes,
        // ten tiers of axe, pickaxe and rod, driven by a character assigned to
        // the job on the Crafting screen.
    }
}
