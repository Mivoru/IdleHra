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
        /// <summary>
        /// Modul: THERE IS NO PER-BAND FUSION CEILING ANY MORE.
        ///
        /// This capped region 1-2 gear at rarity 5 and region 3-4 at 10, and it
        /// was the likeliest thing behind "I press fuse and get an error": the
        /// forge screen checked only the global maximum, so it offered a fusion
        /// on ordinary starter gear that the server refused, and the refusal
        /// came back as "already at maximum tier" next to a 5 out of 14.
        ///
        /// The rule was also hard to defend on its own terms. Fusion consumes
        /// three identical pieces at the same rarity - the cost IS the rule -
        /// and a player who has assembled three of something has earned the
        /// result whatever region it came from. A second, invisible ceiling on
        /// top of that only stops people from using the gear they have.
        ///
        /// Kept as a function returning the global cap rather than deleted, so
        /// the callers that ask "what is the ceiling for this region" keep
        /// asking one place and get one answer.
        /// </summary>
        public static int GetMaxForgeTierForRegion(int regionTier)
            => ForgeSplicingEngine.MaxQualityTier;

        /// <summary>
        /// The largest batch one craft request may ask for. Clamped HERE rather
        /// than trusted from the client: batchSize multiplies both the material
        /// cost and the items produced, so an unclamped value is a request to
        /// mint arbitrary equipment in one call.
        /// </summary>
        public const int MaxCraftBatchSize = 10;

        // Modul: batchSize, 2026-09-01. One call can now produce N units for N
        // times the materials, so a player is not obliged to click ten times or
        // leave a character assigned to a bench to get ten tools.
        //
        // The refund skill roll below deliberately stays ONE ROLL FOR THE WHOLE
        // CALL. That keeps its existing "the craft was free" shape and is
        // expected-value neutral against ten separate crafts - the same chance
        // pays out ten times the materials instead of ten chances paying one
        // each - so batching changes variance and not the economy.
        public async Task ExecuteCraftingAsync(long playerId, int recipeResultItemId, int batchSize = 1)
        {
            if (batchSize < 1) batchSize = 1;
            if (batchSize > MaxCraftBatchSize) batchSize = MaxCraftBatchSize;

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

                byte race = await ReadCrafterRaceAsync(context, player);

                int quantityProduced = batchSize + KoboldExtraUnits(Random.Shared, in recipe, race);

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
                // Modul: Craft, the Insight bough - "sometimes costs you
                // nothing".
                //
                // THE BRANCH PROMISED SOMETHING THIS GAME CANNOT DO. Its first
                // wording was "crafting finishes sooner", and crafting here is
                // instantaneous - ExecuteCraftingAsync has no duration to
                // shorten and PlayerCraftingSlot.CompletionEpoch is a column
                // nothing has ever written. Rather than invent a timer so a
                // node could reduce it, the node now does the half of its
                // promise the game actually has.
                //
                // ONE ROLL FOR THE WHOLE CRAFT, not one per material: "this
                // craft was free" is a thing a player can notice, while "one of
                // your two inputs was refunded" is a rounding error they will
                // never see.
                int craftLevel = await ReadCraftBoughLevelAsync(context, playerId);
                bool materialsRefunded = CraftIsFree(Random.Shared, craftLevel);

                // Modul: the cost scales with the batch and the whole thing is
                // one transaction, so a batch a player cannot afford consumes
                // NOTHING rather than partially completing. Ten units are ten
                // units' materials or no deal - a half-paid batch would be the
                // worse failure, because the player would have spent the
                // materials and have nothing to show for the shortfall.
                int mat1Required = recipe.Mat1Count * batchSize;
                int mat2Required = recipe.Mat2Count * batchSize;

                if (!materialsRefunded && recipe.Mat1Id > 0 && mat1Required > 0)
                {
                    string mat1ItemId = ContentRegistry.GetItemBaseId(recipe.Mat1Id);
                    if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, mat1ItemId, mat1Required))
                    {
                        await transaction.RollbackAsync();
                        return (false, 0);
                    }
                }

                if (!materialsRefunded && recipe.Mat2Id > 0 && mat2Required > 0)
                {
                    string mat2ItemId = ContentRegistry.GetItemBaseId(recipe.Mat2Id);
                    if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, mat2ItemId, mat2Required))
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
                await GrantCraftedOutputAsync(context, playerId, recipe, quantityProduced + MasterArtisanExtraUnits(Random.Shared));

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
                // Funnel step 4, after the commit. See FunnelRecorder.
                Engine.FunnelRecorder.Record(playerId, Engine.FunnelStep.FirstCraft);

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
            // Modul: the MasterArtisan unit is the CALLER's now
            // (MasterArtisanExtraUnits), rolled once per craft, so the offline
            // catch-up - which grants a whole window's crafts in one call -
            // rolls it as often as the live job does rather than once.
            if (quantityProduced <= 0 || recipe.ResultItemId <= 0) return;

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

                // Modul: the drop record (task 26) - a craft creates pieces too,
                // always Normal, so counts only. One upsert for the batch,
                // inside the craft's transaction.
                var craftTally = new DropTally();
                for (int i = 0; i < quantityProduced; i++) craftTally.Count(DropSource.Craft, regionTier, RarityTier.Normal);
                await DropRecord.WriteAsync(context, playerId, craftTally, DateTime.UtcNow);

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

            // Modul: an upsert, not "FOR UPDATE, then insert if missing" - see
            // CommodityLedger for why the missing-row branch was a race.
            await CommodityLedger.AddAsync(context, playerId, resultBaseId, quantityProduced);
        }

        // Modul: MasterArtisan finally does something. GlobalEventType 3 was
        // scheduled by the rotation like any other event, but no code anywhere
        // on the server read it - for a quarter of every rotation the game
        // announced an event with no effect, and the client banner had to say
        // so. This mirrors DiamondStar's hook in ForgeSplicingEngine: one
        // comparison, at the point the bonus applies.
        internal static int MasterArtisanExtraUnits(Random rng)
            => SimulationEngine.ActiveGlobalEventId == MasterArtisanEventId
               && rng.Next(100) < MasterArtisanBonusYieldPct ? 1 : 0;

        // Kobold passive: 10% chance to duplicate bar outcome in smelting (Prof 2).
        private const int KoboldSmeltingDuplicationPct = 10;

        internal static int KoboldExtraUnits(Random rng, in ContentRegistry.RecipeDefinition recipe, byte race)
            => recipe.ProfessionType == 2 && race == RaceIds.Kobold
               && rng.Next(100) < KoboldSmeltingDuplicationPct ? 1 : 0;

        /// <summary>Craft, the Insight bough: whether this craft costs nothing.</summary>
        internal static bool CraftIsFree(Random rng, int craftBoughLevel)
            => craftBoughLevel > 0
               && rng.NextDouble() * 100.0 < Engine.SkillTreeRegistry.GetBonusPercent(
                   Engine.SkillTreeRegistry.BoughCraft, craftBoughLevel);

        private static Task<int> ReadCraftBoughLevelAsync(FolkIdleDbContext context, long playerId)
            => context.PlayerSkillTreeNodes
                .Where(n => n.PlayerId == playerId && n.BranchId == Engine.SkillTreeRegistry.BoughCraft)
                .Select(n => n.Level)
                .FirstOrDefaultAsync();

        // The race a craft is judged by: the account's MAIN character
        // (PlayerGuid), whichever slot holds the job - which is what the live
        // job has always read.
        private static async Task<byte> ReadCrafterRaceAsync(FolkIdleDbContext context, PlayerRecord player)
        {
            var charRecord = await context.CharacterRecords.FirstOrDefaultAsync(c => c.PlayerId == player.Id && c.Id == player.PlayerGuid);
            long geneticVector = 0;
            if (charRecord != null)
            {
                var lineage = await context.CharacterLineages.FirstOrDefaultAsync(l => l.CharacterId == charRecord.Id);
                if (lineage != null)
                {
                    geneticVector = lineage.GeneticVector;
                }
            }

            return new GeneticVector(geneticVector).LocusRace.Dominant;
        }

        /// <summary>What an offline window of a crafting job produced.</summary>
        internal readonly record struct OfflineCraftResult(long Crafts, long UnitsProduced, long UnitsGranted);

        /// <summary>
        /// An offline window of the crafting JOB: <paramref name="attempts"/>
        /// craft completions, each judged exactly as
        /// <see cref="ExecuteCraftingAsync"/> judges one live completion (a
        /// batch of one) - the Craft bough's free roll, the material check, the
        /// Kobold unit, the MasterArtisan unit - against one locked read of the
        /// two materials, then consumed, granted and counted in one write.
        /// </summary>
        /// <remarks>
        /// Modul: THE OFFLINE CRAFTER USED TO FIGHT MONSTER 1, 2026-09-30.
        ///
        /// OfflineSimulationEngine asked TryGetGatheringNode and otherwise ran
        /// the combat projection - so a character left crafting fell into
        /// CalculateCombatProjection with an ActiveActivityId of 5000+, past the
        /// monster table, and fought fallback monster 1 for the whole window:
        /// gold, XP and loot for a fight it never had, and not one craft. The
        /// defect PR #7 fixed in the live tick (ProcessSubTickDispatchTests),
        /// alive in the path that runs while the player is away.
        ///
        /// A refused craft does not stop the job, live or here: the tick keeps
        /// counting, and a later completion whose free roll succeeds still
        /// crafts. So the loop runs every attempt rather than breaking at the
        /// first shortfall - unless no free roll is possible at all.
        /// </remarks>
        internal static async Task<OfflineCraftResult> ExecuteOfflineCraftsAsync(
            FolkIdleDbContext context, long playerId, ContentRegistry.RecipeDefinition recipe, long attempts, Random rng)
        {
            if (attempts <= 0 || recipe.ResultItemId <= 0) return default;

            var ownTransaction = context.Database.CurrentTransaction == null
                ? await context.Database.BeginTransactionAsync(IsolationLevel.Serializable)
                : null;
            try
            {
                var player = await context.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                if (player == null) return default;

                byte race = await ReadCrafterRaceAsync(context, player);
                int craftLevel = await ReadCraftBoughLevelAsync(context, playerId);

                string? mat1 = recipe.Mat1Id > 0 && recipe.Mat1Count > 0 ? ContentRegistry.GetItemBaseId(recipe.Mat1Id) : null;
                string? mat2 = recipe.Mat2Id > 0 && recipe.Mat2Count > 0 ? ContentRegistry.GetItemBaseId(recipe.Mat2Id) : null;
                bool sharedMaterial = mat1 != null && mat1 == mat2;
                long have1 = mat1 != null ? (await InventoryAndStashSystem.LockAndReadBalanceAsync(context, playerId, mat1)).Total : 0L;
                long have2 = mat2 != null && !sharedMaterial ? (await InventoryAndStashSystem.LockAndReadBalanceAsync(context, playerId, mat2)).Total : 0L;

                long spent1 = 0L, spent2 = 0L, crafts = 0L, produced = 0L, artisan = 0L;
                for (long attempt = 0; attempt < attempts; attempt++)
                {
                    if (!CraftIsFree(rng, craftLevel))
                    {
                        long need1 = mat1 != null ? recipe.Mat1Count : 0L;
                        long need2 = mat2 != null ? recipe.Mat2Count : 0L;
                        bool affordable = sharedMaterial
                            ? have1 - spent1 - spent2 >= need1 + need2
                            : have1 - spent1 >= need1 && have2 - spent2 >= need2;
                        if (!affordable)
                        {
                            if (craftLevel <= 0) break;
                            continue;
                        }
                        spent1 += need1;
                        spent2 += need2;
                    }

                    crafts++;
                    produced += 1 + KoboldExtraUnits(rng, in recipe, race);
                    artisan += MasterArtisanExtraUnits(rng);
                }

                if (crafts == 0)
                {
                    return default;
                }

                // One consume per material for the whole window. Cannot fail
                // against the balance read above, which is locked FOR UPDATE
                // in this same transaction - but if it ever does, nothing is
                // granted either.
                if ((spent1 > 0 && !await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, mat1!, spent1))
                    || (spent2 > 0 && !await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, mat2!, spent2)))
                {
                    if (ownTransaction != null) await ownTransaction.RollbackAsync();
                    context.ChangeTracker.Clear();
                    return default;
                }

                long granted = produced + artisan;
                await GrantCraftedOutputAsync(context, playerId, recipe, (int)Math.Min(int.MaxValue, granted));
                player.TotalItemsCrafted += produced;

                await context.SaveChangesAsync();
                if (ownTransaction != null) await ownTransaction.CommitAsync();

                Engine.FunnelRecorder.Record(playerId, Engine.FunnelStep.FirstCraft);
                return new OfflineCraftResult(crafts, produced, granted);
            }
            finally
            {
                if (ownTransaction != null) await ownTransaction.DisposeAsync();
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
