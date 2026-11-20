using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Network;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: moved out of SimulationEngine.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md).
    public partial class SimulationEngine
    {
        // Modul: the instant time-warp subsystem lived here and is gone with
        // the chrono bank that funded it - ActivateChronoAcceleration,
        // ExecuteInstantTimeWarp, ApplyGatheringWarp, ApplyCombatWarp and the
        // six helpers only they called (ConsumeFoodStock,
        // CalculateIntegratedBuffMultiplier, EstimateTicksPerKill,
        // CalculateExpectedWarpDrops, CalculateExpectedCombatWarpDrops,
        // ConsumeInventorySlots). All of it was reachable only from commands 47
        // and 48, which no longer exist.
        //
        // Deleted rather than left unreferenced on purpose: this codebase has
        // twice shipped a "dead code" list naming files that turned out to be
        // already deleted, and an orphaned analytic projection of the tick is
        // exactly the thing that drifts silently from the tick it mirrors.

        internal static void ApplyBulkMasteryXp(ref TickStatePayload payload, int professionType, long masteryXp)
        {
            if (masteryXp <= 0)
            {
                return;
            }

            // Modul: every profession has its own track. This used to be
            // `professionType == 0 ? Woodcutting : Mining`, duplicated in the
            // realtime path as well - so Fishing (2) and Herbalism (3) both
            // levelled MINING. Fishing a node in band 3000 raised the player's
            // mining level, and Fishing had no field to display at all.
            //
            // The mapping now exists exactly once, here and in
            // GetMasteryLevel, so a fifth profession cannot land in the wrong
            // track by being the one nobody added a branch for.
            switch (professionType)
            {
                case 0:
                    AdvanceMastery(ref payload.WoodcuttingMasteryXp, ref payload.WoodcuttingMasteryLevel, masteryXp);
                    break;
                case 1:
                    AdvanceMastery(ref payload.MiningMasteryXp, ref payload.MiningMasteryLevel, masteryXp);
                    break;
                case 2:
                    AdvanceMastery(ref payload.FishingMasteryXp, ref payload.FishingMasteryLevel, masteryXp);
                    break;
                default:
                    AdvanceMastery(ref payload.HerbalismMasteryXp, ref payload.HerbalismMasteryLevel, masteryXp);
                    break;
            }
        }

        // The rarest entry in a gathering table is the LAST one - every node is
        // authored common-then-rare. Scaling by its own weight keeps the boost
        // proportional: +100% doubles a 10-weight rare's share rather than
        // handing every table the same flat number regardless of how rare its
        // rare actually is.
        private static int WeightOf(ReadOnlySpan<LootTableEntry> table, int index, int luckWeightBonus, int rareWeightBonus)
        {
            int weight = table[index].Weight + luckWeightBonus;
            if (rareWeightBonus > 0 && index == table.Length - 1 && table.Length > 1)
            {
                weight += table[index].Weight * rareWeightBonus / 100;
            }

            return weight;
        }

        internal static int GetMasteryLevel(ref TickStatePayload payload, int professionType)
        {
            return professionType switch
            {
                0 => payload.WoodcuttingMasteryLevel,
                1 => payload.MiningMasteryLevel,
                2 => payload.FishingMasteryLevel,
                _ => payload.HerbalismMasteryLevel
            };
        }

        // 50 * (level + 1)^2, as long rather than int: the curve passes
        // int.MaxValue somewhere past level 6500, and an overflow there would
        // wrap the requirement negative and level the player up forever.
        private static long MasteryXpForLevel(int level)
        {
            return 50L * (level + 1L) * (level + 1L);
        }

        private static void AdvanceMastery(ref int xp, ref int level, long gain)
        {
            xp = ClampLongToInt((long)xp + gain);
            long required = MasteryXpForLevel(level);
            while (xp >= required)
            {
                xp -= (int)required;
                level++;
                required = MasteryXpForLevel(level);
            }
        }

        /// <summary>
        /// Gathering progress, yield and grants for one tick. Extracted
        /// verbatim from ProcessSubTick's second activity branch.
        ///
        /// Modul: the branch's trailing `return;` stays at the CALL SITE -
        /// returning from this method cannot make ProcessSubTick return, and a
        /// gathering character that fell through would start fighting
        /// monster 1 while gathering (the defect PR #7 fixed for crafting).
        ///
        /// Callable ONLY from ProcessSubTick: the slot register's
        /// swap/try/finally discipline lives one level up in
        /// ProcessAllSlotSubTicks.
        /// </summary>
        private static void RunGatheringTick(ref TickStatePayload payload, in GatheringNodeDefinition gatheringNode, int localDropMultiplier)
        {
            // Modul: the speed and the yield are each ONE function now,
            // RequiredGatherTicks and GatheringYieldFor, asked by this tick and
            // by OfflineSimulationEngine alike (offline parity, 2026-09-30).
            int requiredTicks = RequiredGatherTicks(ref payload, in gatheringNode);
            payload.RequiredProgressTicks = requiredTicks;
            payload.GatheringProgressTicks++;

            if (payload.GatheringProgressTicks >= requiredTicks)
            {
                payload.GatheringProgressTicks = 0;
                payload.HarvestLoopCount++;
                payload.LifetimeHarvests++;

                // Modul: Scholar pays per harvest (ScholarRate, owner decision
                // 2026-09-30): this harvest pays for one more with the crown's
                // chance - mastery XP and loot rolls both - which is what the
                // offline projection draws for a whole window. A rate on the
                // reward, not a faster tick.
                int paidHarvests = (int)ScholarRate.RewardUnits(Random.Shared, 1, ScholarRate.BonusPermille(in payload));

                int masteryXpGain = gatheringNode.BaseMasteryXpReward;
                ApplyBulkMasteryXp(ref payload, gatheringNode.ProfessionType, (long)masteryXpGain * paidHarvests);
                AddSeasonalXp(ref payload, masteryXpGain);

                Domain.Progression.SeasonalEventEarning.Roll(
                    ref payload, Domain.Progression.SeasonalEventEarning.Source.Gather, paidHarvests,
                    GameClock.UtcNowOffset.ToUnixTimeSeconds(), Random.Shared);

                // Loot roll
                var lootTable = ContentRegistry.GetLootTable(gatheringNode.ActivityId);

                // Modul: DEEP VEIN AND MOONLIT CATCH (Hearth keystones, step 4.5).
                // Every KeystoneEvery-th completed harvest adds one roll of the
                // node's rarest entry. Counted on LifetimeHarvests, which the
                // offline projection advances by the same harvests, so a window
                // away pays exactly the 20ths it covers (RareKeystoneRolls).
                // Moonlit Catch was "rare find doubles while offline" in the
                // design and was changed by the owner (2026-10-30): that broke
                // the rule that an hour away pays what an hour watched pays.
                if (payload.LifetimeHarvests % HearthTreeRegistry.KeystoneEvery == 0
                    && HearthTreeRegistry.Resolve(in payload).RareRollFor(gatheringNode.ProfessionType))
                {
                    int rarest = RarestEntryIndex(lootTable);
                    if (rarest >= 0)
                    {
                        int rareQuantity = RollGatherQuantity(Random.Shared, in lootTable[rarest]);
                        CombatLootEngine.GatheringGrantQueue.Enqueue(new GatheredMaterialGrant
                        {
                            PlayerId = payload.PlayerId,
                            ActivityId = payload.ActiveActivityId,
                            ItemId = lootTable[rarest].ItemId,
                            Quantity = rareQuantity,
                        });
                        GatheringGrantLedger.NoteEnqueued(rareQuantity);
                    }
                }

                if (lootTable.Length > 0)
                {
                    GatheringYield yield = GatheringYieldFor(ref payload, in gatheringNode, localDropMultiplier);
                    int luckWeightBonus = yield.LuckWeightBonus;

                    int totalWeight = 0;
                    for (int i = 0; i < lootTable.Length; i++) totalWeight += lootTable[i].Weight + luckWeightBonus;
                    if (totalWeight > 0)
                    {
                        int multiplier = yield.MultiplierPct;
                        int guaranteedRolls = multiplier / 100;
                        int fractionalBonus = multiplier % 100;
                        int rollsToExecute = 0;
                        for (int paid = 0; paid < paidHarvests; paid++)
                        {
                            rollsToExecute += guaranteedRolls;
                            if (fractionalBonus > 0 && Random.Shared.Next(100) < fractionalBonus)
                            {
                                rollsToExecute++;
                            }
                        }
                        for (int r = 0; r < rollsToExecute; r++)
                        {
                            int roll = Random.Shared.Next(totalWeight);
                            int currentWeight = 0;
                            for (int i = 0; i < lootTable.Length; i++)
                            {
                                currentWeight += lootTable[i].Weight + luckWeightBonus;
                                if (roll < currentWeight)
                                {
                                    // Modul: THE GATHERED ITEM IS ACTUALLY
                                    // GRANTED. This block used to compute a
                                    // Kobold carry weight, spend a backpack
                                    // slot and break - with no write to
                                    // CommodityRecords anywhere on the
                                    // gathering path. The winner was picked
                                    // and dropped on the floor.
                                    //
                                    // The Kobold penalty went with the
                                    // backpack: it was a rule about carrying
                                    // capacity, and there is no capacity to
                                    // penalise now that storage is one
                                    // unlimited chest.
                                    int grantQuantity = RollGatherQuantity(Random.Shared, in lootTable[i]);

                                    CombatLootEngine.GatheringGrantQueue.Enqueue(new GatheredMaterialGrant
                                    {
                                        PlayerId = payload.PlayerId,
                                        ActivityId = payload.ActiveActivityId,
                                        ItemId = lootTable[i].ItemId,
                                        Quantity = grantQuantity
                                    });
                                    GatheringGrantLedger.NoteEnqueued(grantQuantity);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// How many 10 Hz ticks one harvest of this node takes for the active
        /// register - the ONE composition of the speed terms, asked by the
        /// live tick and by the offline projection alike.
        /// </summary>
        /// <remarks>
        /// Modul: extracted 2026-09-30 for offline parity. The offline path
        /// held a line-for-line copy of this argument list - correct on the
        /// day it was checked, and exactly the kind of copy this codebase has
        /// lost three times to a term added on one side only.
        ///
        /// Deferred Part 5 Implementation, Parts 1/3. The required-tick math
        /// (the tool family's percentage speed bonus, mastery, the village
        /// production building's +5 percent per level) lives in
        /// GatheringToolEngine.ComputeRequiredTicks - pure integer arithmetic,
        /// zero allocation on this 10Hz path. Only Woodcutting and Mining have
        /// a village production building; Fishing and Herbalism get no
        /// acceleration rather than silently borrowing the Mine's. The tool is
        /// the one that matches the job: this passed CachedCurrentToolTier
        /// once, which was the forge building's level - so an axe sped up
        /// fishing and owning no tool made no difference either way.
        /// </remarks>
        internal static int RequiredGatherTicks(ref TickStatePayload payload, in GatheringNodeDefinition gatheringNode)
        {
            var terms = GatherSpeedTermsFor(ref payload, gatheringNode.ProfessionType);
            return GatheringToolEngine.ComputeRequiredTicks(gatheringNode.BaseTickThreshold, terms.MasteryLevel, terms.ToolTier, terms.VillageLevel, terms.ExtraPct);
        }

        /// <summary>
        /// The speed terms for one profession on the active character. The
        /// tick (RequiredGatherTicks) and the stat sheet both read this, so the
        /// sheet cannot print a speed the tick does not use.
        /// </summary>
        internal static GatherSpeedTerms GatherSpeedTermsFor(ref TickStatePayload payload, int professionType)
        {
            int masteryLevel = GetMasteryLevel(ref payload, professionType);
            int villageProductionLevel = professionType switch
            {
                0 => payload.LumberjackLevel,
                1 => payload.MineLevel,
                _ => 0
            };
            // Modul: herbs are picked by hand (owner, 2026-10-30) - profession 3
            // has no tool, so it reads 0 here. Before herbalism came back the
            // fall-through handed it the ROD's tier, and a fisher's rod would
            // have sped up a herbalist who never wore it.
            int toolTier = professionType switch
            {
                0 => payload.AxeToolTier,
                1 => payload.PickaxeToolTier,
                2 => payload.RodToolTier,
                _ => 0
            };
            // Modul: THE HEARTH HALF, NOT THE HARVEST BOUGH (2026-10-30). The War
            // switch emptied slot 14, so this term read 0 for everyone; the
            // Hearth speed nodes replace it, capped at the old Harvest's +16%.
            int extraPct = ToolSpeedPctFor(in payload, professionType)
                + HearthTreeRegistry.Resolve(in payload).SpeedTenthsPctFor(professionType) / 10
                + BloodlineBonuses.GatherSpeedBonusPct(payload.Aptitude_Skill, TraitTotals.From(payload.TraitMask))
                // The character's pet (PetRegistry).
                + payload.CachedAffixTotals.GatherSpeedTenthsPct / 10
                // Gatherer's Draught, the gathering potion slot (any profession).
                + PotionRegistry.MagnitudeOf(payload.ActiveGatheringPotionId, PotionKind.Gatherer)
                // The Gathering 5 inheritance milestone.
                + InheritanceMilestones.GatherSpeedBonusPct(in payload);
            return new GatherSpeedTerms(masteryLevel, toolTier, villageProductionLevel, extraPct);
        }

        /// <summary>Mastery XP the profession still needs for its next level (at least 1).</summary>
        internal static long MasteryXpToNextLevel(ref TickStatePayload payload, int professionType)
        {
            (int xp, int level) = professionType switch
            {
                0 => (payload.WoodcuttingMasteryXp, payload.WoodcuttingMasteryLevel),
                1 => (payload.MiningMasteryXp, payload.MiningMasteryLevel),
                2 => (payload.FishingMasteryXp, payload.FishingMasteryLevel),
                _ => (payload.HerbalismMasteryXp, payload.HerbalismMasteryLevel)
            };
            return Math.Max(1L, MasteryXpForLevel(level) - xp);
        }

        /// <summary>
        /// The per-harvest yield terms - the ONE composition, asked by the live
        /// tick and by the offline projection alike.
        /// </summary>
        /// <remarks>
        /// Modul: extracted 2026-09-30 for offline parity. The offline copy had
        /// kept only the codex multiplier and the yield trait, and had lost the
        /// monolith, both race bonuses, the Golden Harvest event and the global
        /// drop multiplier - so an hour away gathered measurably less than the
        /// same hour watched for any character that had one of them.
        /// </remarks>
        internal static GatheringYield GatheringYieldFor(ref TickStatePayload payload, in GatheringNodeDefinition gatheringNode, int localDropMultiplier)
        {
            int gatherActiveAgePhase = 1;
            int gatherActiveRaceId = 0;
            if (payload.Slot1_CharacterId != System.Guid.Empty)
            {
                gatherActiveAgePhase = payload.Slot1_AgePhase;
                gatherActiveRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);
            }
            var gatherCombatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, gatherActiveAgePhase, payload.CompletedAreaFlags, gatherActiveRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, AffixFamilies.Totals(in payload), payload.IsEpicMutation, TraitTotals.From(payload.TraitMask), payload.CachedSetIds);

            int monolithLevel = gatheringNode.ProfessionType switch
            {
                0 => payload.CachedWoodcuttingMonolithLevel,
                1 => payload.CachedMiningMonolithLevel,
                _ => 0
            };
            float yieldBonusPct = Math.Min(monolithLevel * 1.0f, 50.0f);
            int additionalYieldBonus = (int)(100f * (yieldBonusPct / 100f)); // Add to multiplier

            // Modul 13: Kobold ore duplication (Mining) / Moosleute yield
            // bonus. Fishing (ProfessionType 2) and Herbalism
            // (ProfessionType 3) fall through to the Moosleute
            // branch below along with Woodcutting - Kobold's ore
            // duplication is intentionally Mining-specific, and
            // no dedicated racial bonus exists yet for Fishing/
            // Herbalism, so Moosleute's "double harvest" is
            // applied to them as the closest available bonus
            // rather than granting neither profession any
            // racial yield bonus at all.
            if (gatheringNode.ProfessionType == 1)
            {
                additionalYieldBonus += (int)RaceMasteryResolver.GetKoboldOreDuplicationBonusPct(payload.KoboldMasteryLevel);
                // Modul 13.4.3: Kobold's innate baseline (not mastery-scaled).
                additionalYieldBonus += (int)gatherCombatStats.MiningOreDuplicationBonusPct;
            }
            else
            {
                additionalYieldBonus += (int)RaceMasteryResolver.GetMoosleuteDoubleHarvestBonusPct(payload.MoosleuteMasteryLevel);
                // Modul 13.4.3: Moosleute's innate baseline (not mastery-scaled).
                additionalYieldBonus += (int)gatherCombatStats.WoodcuttingYieldBonusPct;
            }

            if (ActiveGlobalEventId == 1) // GoldenHarvest
            {
                additionalYieldBonus += 20;
            }

            // Modul: yield traits, which replaced the Yield gene on
            // 2026-09-13 - percentage points of extra harvest rolls, the
            // same units as the race-mastery bonuses above.
            additionalYieldBonus += BloodlineBonuses.GatherYieldBonusPct(TraitTotals.From(payload.TraitMask));
            // Aptitude milestones: Skill's Deft Hands.
            additionalYieldBonus += AptitudeMilestones.GatherYieldPct(payload.Aptitude_Skill);

            // Modul: task 84 - the Great Works. Whole percentage points from
            // every built stage, capped at GreatWorksRegistry.MaxYieldPct
            // (PowerCeilingTests). It is added HERE, in the one composition, so
            // the live harvest and the offline projection both pay it, and it
            // survives a rebirth because the stages live in their own table.
            additionalYieldBonus += GreatWorksRegistry.YieldPct(payload.GreatWorksStagesPacked);

            // The character's pet (a monster pet's +gathering yield), in the
            // same whole percentage points, so live and offline both pay it.
            additionalYieldBonus += payload.CachedAffixTotals.GatherYieldTenthsPct / 10;

            // Modul: the inheritance "Gathering yield" stat, +2 points of
            // extra harvest rolls per level. Until 2026-10-29 it was bought
            // with diamonds, stored, loaded and shown - and read by nothing,
            // so a full stat (~19.8k diamonds) paid zero. Added to the same
            // pool as every other yield bonus, BEFORE the codex multiplier:
            // the codex's 2x cap bounds only the codex's own factor
            // (CodexEngine.MaxYieldMultiplier), never this sum.
            additionalYieldBonus += InheritanceRegistry.GetBonusPct(payload.Inherit_GatheringYield);

            // Modul: the worn tools' gather_yield_pct, which ToolLoadoutResolver
            // summed, the checkpoint stored and the packet shipped - and no
            // simulation code read (found 2026-10-29). Affix magnitudes are
            // tenths of a percent (the tooltip divides by ten, and so does the
            // pet's GatherYieldTenthsPct just above), so /10 is the number the
            // player was shown.
            additionalYieldBonus += ToolYieldPctFor(in payload, gatheringNode.ProfessionType) / 10;

            // Modul: the Hearth half's yield and "pays twice" nodes (2026-10-30),
            // extra harvest rolls in the same whole points as everything above,
            // added here so the live harvest and the offline projection both pay
            // them. Tenths, so /10 like the tool affix.
            var hearth = HearthTreeRegistry.Resolve(in payload);
            additionalYieldBonus += hearth.YieldTenthsFor(gatheringNode.ProfessionType) / 10;

            // Modul: LootLuckPct no longer multiplies the roll COUNT
            // (which previously inflated absolute yield of every
            // table entry, common trash and rare drops alike, in
            // fixed proportion - a placebo that never actually
            // shifted rarity odds). Roll count stays driven only
            // by monolith/race/event/trait bonuses; luck instead adds
            // a flat weight bonus to every entry, which favors
            // low-weight (rare) entries far more than high-weight ones,
            // since a fixed addition is a much larger relative increase
            // for a small base weight than a large one.
            return new GatheringYield
            {
                MultiplierPct = (int)((localDropMultiplier + additionalYieldBonus) * payload.CachedCodexYieldMultiplier),
                // Modul: gather_rare_find_pct rides the one rare-favouring
                // lever gathering already has - LootLuckPct's flat weight bonus,
                // which lifts low-weight (rare) entries far more than common
                // ones - in the same tenths-of-a-percent units. Before
                // 2026-10-29 the affix was summed, stored and read by nothing.
                // The Gathering 10 inheritance milestone doubles the whole
                // rare-favouring bonus.
                LuckWeightBonus = InheritanceMilestones.RareFindWeight(in payload, GatheringYield.LuckWeightBonusFor(gatherCombatStats.LootLuckPct
                    + ToolRareFindPctFor(in payload, gatheringNode.ProfessionType) / 10f
                    + hearth.RareFindTenthsPctFor(gatheringNode.ProfessionType) / 10f
                    // Kupala Night, in the same tenths as the Hearth's rare find.
                    + PotionRegistry.MagnitudeOf(payload.ActiveGatheringPotionId, PotionKind.Kupala) / 10f)),
            };
        }

        /// <summary>The worn tool's speed affix for a profession, in WHOLE percent (the tooltip shows it so since TASK_BOARD 121).</summary>
        internal static int ToolSpeedPctFor(in TickStatePayload payload, int professionType) => AffixFamilies.CraftScaled(in payload, professionType switch
        {
            0 => payload.ToolAxeSpeedPct,
            1 => payload.ToolPickaxeSpeedPct,
            2 => payload.ToolRodSpeedPct,
            _ => 0,
        });

        /// <summary>The worn tool's yield affix for a profession: axe woodcutting, pickaxe mining, rod fishing.</summary>
        internal static int ToolYieldPctFor(in TickStatePayload payload, int professionType) => AffixFamilies.CraftScaled(in payload, professionType switch
        {
            0 => payload.ToolAxeYieldPct,
            1 => payload.ToolPickaxeYieldPct,
            2 => payload.ToolRodYieldPct,
            _ => 0,
        });

        /// <summary>The worn tool's rare-find affix for a profession, as ToolYieldPctFor.</summary>
        internal static int ToolRareFindPctFor(in TickStatePayload payload, int professionType) => AffixFamilies.CraftScaled(in payload, professionType switch
        {
            0 => payload.ToolAxeRareFindPct,
            1 => payload.ToolPickaxeRareFindPct,
            2 => payload.ToolRodRareFindPct,
            _ => 0,
        });

        /// <summary>The entry with the lowest weight - the "rare table" a Hearth keystone rolls - or -1 for an empty table.</summary>
        internal static int RarestEntryIndex(ReadOnlySpan<LootTableEntry> table)
        {
            int best = -1;
            for (int i = 0; i < table.Length; i++)
                if (table[i].Weight > 0 && (best < 0 || table[i].Weight < table[best].Weight)) best = i;
            return best;
        }

        /// <summary>How many keystone rare rolls a run of harvests from <paramref name="lifetimeBefore"/> to <paramref name="lifetimeAfter"/> covers.</summary>
        internal static long RareKeystoneRolls(long lifetimeBefore, long lifetimeAfter)
            => lifetimeAfter / HearthTreeRegistry.KeystoneEvery - lifetimeBefore / HearthTreeRegistry.KeystoneEvery;

        /// <summary>The quantity one winning gathering roll grants: its authored range, or one.</summary>
        internal static int RollGatherQuantity(Random rng, in LootTableEntry entry)
            => entry.MaxQuantity > entry.MinQuantity
                ? rng.Next(Math.Max(1, entry.MinQuantity), entry.MaxQuantity + 1)
                : 1;

        /// <summary>
        /// Crafting-as-a-job progress for one tick. Extracted verbatim from
        /// ProcessSubTick's first activity branch.
        ///
        /// Modul: the branch's `return;` stays at the CALL SITE in
        /// ProcessSubTick, beside the comment that explains it (PR #7,
        /// ProcessSubTickDispatchTests) - a return inside this method could
        /// not stop ProcessSubTick falling through into combat.
        ///
        /// Callable ONLY from ProcessSubTick: the slot register's
        /// swap/try/finally discipline lives one level up in
        /// ProcessAllSlotSubTicks.
        /// </summary>
        private static void RunCraftingProgressTick(ref TickStatePayload payload, in ContentRegistry.RecipeDefinition craftingRecipe)
        {
            // Modul: crafting as an assignable job. CraftingTimeMs was
            // authored on all 104 recipes and read by nothing - a craft
            // was instant and needed no character. It is now a job like
            // any other: one assigned character, real elapsed time, and
            // it repeats until the player stops it or runs out of
            // materials (CraftingEngine refuses the craft, the tick keeps
            // counting, and the halt shows up as nothing being produced).
            int craftTicks = CraftTicksFor(in payload, in craftingRecipe);

            payload.RequiredProgressTicks = craftTicks;
            payload.GatheringProgressTicks++;

            if (payload.GatheringProgressTicks >= craftTicks)
            {
                payload.GatheringProgressTicks = 0;
                payload.HarvestLoopCount++;

                // Modul: Scholar pays per completion (ScholarRate): this one
                // is worth one more craft with the crown's chance - materials
                // spent and all, as the offline window's extra attempts are.
                long paidCrafts = ScholarRate.RewardUnits(Random.Shared, 1, ScholarRate.BonusPermille(in payload));
                for (long craft = 0; craft < paidCrafts; craft++)
                {
                    CraftingTickQueue.Enqueue(new CraftTickCompletion
                    {
                        PlayerId = payload.PlayerId,
                        ResultItemId = craftingRecipe.ResultItemId
                    });
                }
            }
        }

        /// <summary>
        /// Ticks one craft of this recipe takes as a job. Asked by the live
        /// tick and by the offline projection, so "a craft every N seconds"
        /// is one number.
        /// </summary>
        internal static int CraftTicksFor(in ContentRegistry.RecipeDefinition craftingRecipe)
        {
            int craftTicks = craftingRecipe.CraftingTimeMs / 100;
            return craftTicks < MinCraftTicks ? MinCraftTicks : craftTicks;
        }

        /// <summary>
        /// The recipe's ticks for this character: the Hearth half's craft-time
        /// cut (Quick Hands, Batch Work; at most 16%) on top of the recipe's own.
        /// The live job and the offline window both ask this one.
        /// </summary>
        internal static int CraftTicksFor(in TickStatePayload payload, in ContentRegistry.RecipeDefinition craftingRecipe)
        {
            int baseTicks = CraftTicksFor(in craftingRecipe);
            int cut = Math.Clamp(HearthTreeRegistry.Resolve(in payload).CraftTimeTenthsPct, 0, 900);
            int ticks = (int)((long)baseTicks * (1000 - cut) / 1000);
            return ticks < MinCraftTicks ? MinCraftTicks : ticks;
        }
    }
}
