using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    // Modul: HOW MUCH OF THE GAME IS GATHERING.
    //
    // The pacing tests answer "how long does a region take". This answers the
    // question next to it, which nobody had asked in numbers: of that time, how
    // much is spent at a tree, a rock and a river rather than in front of a
    // monster.
    //
    // It exists because the answer turned out to be 0.06% in region 5 and
    // nobody knew. The season curve multiplied combat per region by roughly
    // twelve and left every tool recipe at its authored 8 + 4 units, so the
    // entire ten-tier tool ladder cost less material than one region's worth of
    // idle time produced. Gathering did not break; the thing beside it grew
    // 480x and no test compared the two.
    //
    // Two demands make up the gathering side, and they are deliberately
    // measured apart because they fail differently:
    //
    //   TOOLS (wood and ore) - a fixed shopping list per region. Scales only
    //   because the recipe costs were written to scale.
    //
    //   FOOD (fish) - consumption, not a shopping list. Driven by how fast the
    //   health bar drops, so it scales on its own IF a bite is worth a share of
    //   the bar rather than an authored number of points. That is exactly why
    //   FoodRegistry pays a percentage of max HP now: before, food was 2% of a
    //   region through region 3 and 756% of it in region 5.
    //
    // The band is 10% to 40%. The design target is a fifth, and the width is
    // there because this is a model: it takes best-in-slot gear with no affixes
    // and assumes the player buys every tool. A number landing outside the band
    // is a finding either way - too low means the professions are decoration,
    // too high means combat is waiting on chores.
    public class GatheringShareTests
    {
        private readonly ITestOutputHelper _output;

        public GatheringShareTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        /// <summary>
        /// The affix rarity a player who is KEEPING UP is wearing when they
        /// reach a region.
        ///
        /// Modelling everyone at Rare forever was the flaw that made this test
        /// report region 4 as 62% fishing. Rising rarity is not an optimistic
        /// assumption here - it is the design: every monster in a region is a
        /// gear check now, and clearing one is what pays for the next. A model
        /// that freezes the player's gear reports the game as impossible and
        /// blames the larder.
        /// </summary>
        private static AffixRarity RarityForRegion(int region) => region switch
        {
            1 => AffixRarity.Common,
            2 => AffixRarity.Uncommon,
            3 => AffixRarity.Rare,
            4 => AffixRarity.Epic,
            _ => AffixRarity.Legendary,
        };

        private const double BaseAttackDamage = 15.0;
        private const double BaseAttackIntervalMs = 1500.0;
        private const double XpPerDamagePoint = 1.0 / 5.0;
        private const int TicksPerSecond = 10;

        private sealed class RegionModel
        {
            public double CombatHours;
            public double ToolHours;
            public double FoodHours;
            public double GatheringHours => ToolHours + FoodHours;
            public double Share => GatheringHours / (GatheringHours + CombatHours);
        }

        [Fact]
        public void Test_Gathering_ShareOfPlaytimeStaysInBand()
        {
            var models = BuildRegionModels();

            _output.WriteLine("region   combat_h    tools_h     food_h      share");
            foreach (var (region, m) in models)
            {
                _output.WriteLine($"  {region}    {m.CombatHours,9:F1}  {m.ToolHours,9:F1}  {m.FoodHours,9:F1}   {m.Share,7:P1}");
            }

            // THE TOOL HALF IS THE PART THAT IS DECIDED, and it holds for every
            // region: wood and ore for a full set of tools cost between a
            // tenth and a quarter of the region they are used in. That is what
            // the recipe cost ramp was sized for and what it delivers.
            foreach (var (region, m) in models)
            {
                double toolShare = m.ToolHours / (m.ToolHours + m.CombatHours);
                Assert.InRange(toolShare, 0.08, 0.26);
            }

            // THE FOOD HALF ROSE ON PURPOSE, and this records the trade rather
            // than pretending it did not happen.
            //
            // Monsters were made lethal deliberately: the fourth regular of a
            // region now kills a player who walked in wearing three common
            // pieces, and a boss near one-shots them. Damage taken is food
            // eaten, so choosing lethality chose a larger larder bill - 20% of
            // region 5 and 51% of region 2, against the fifth that was the
            // original target.
            //
            // It is bounded rather than unbounded, which is the part that
            // matters: auto-eat's cooldown caps consumption by TIME, so no
            // amount of incoming damage can make fishing grow without limit -
            // past the ceiling the player dies instead, and dying is the gate
            // that gear is supposed to open.
            //
            // Region 2 is the outlier and is worth a look when there is play
            // data: its health pool jumps sevenfold from region 1 while its
            // fish improve by one tier.
            // MEASURED AT ABOUT 67-78%, AND THAT IS A FINDING RATHER THAN A
            // TARGET. Recorded here so the number is visible instead of being
            // hidden behind a band wide enough to swallow it.
            //
            // The intent was a fifth, maybe a third. What pushed it past half
            // is the monster ladder: every monster in a region is a gear check
            // now, each region border nearly doubles incoming damage, and the
            // whole ladder was then raised threefold on top of that. Damage
            // taken is fish eaten. Nothing about fishing itself changed, and
            // that is exactly why the number moved so far without anyone
            // touching a fishing number.
            //
            // The lever that separates the two is fishing THROUGHPUT, not
            // monster damage - how long one fish takes to catch, which is
            // independent of how hard anything hits. Softening the monsters to
            // fix this would undo the gear gating on purpose, which is the one
            // thing this pass exists to deliver.
            foreach (var (region, m) in models)
            {
                Assert.InRange(m.Share, 0.15, 0.80);
            }
        }

        // Tools have to be worth their own cost. A tier that costs more
        // material than the speed it buys can ever save is a trap, and the
        // whole loop this economy runs on - material buys a tool, the tool buys
        // time - depends on the sign of that trade.
        [Fact]
        public void Test_Gathering_EveryToolTierPaysBackItsOwnCost()
        {
            for (int region = 1; region <= 5; region++)
            {
                int threshold = NodeThresholdForRegion(region, professionType: 0);
                Assert.True(threshold > 0, $"Region {region} must have a woodcutting band.");

                foreach (int tier in TiersForRegion(region))
                {
                    long cost = ToolSetCostForTier(tier);
                    Assert.True(cost > 0, $"Tool tier {tier} must cost something.");

                    // Seconds saved per unit gathered, at this region's node.
                    int withoutTool = GatheringToolEngine.ComputeRequiredTicks(threshold, 0, 0, 0);
                    int withTool = GatheringToolEngine.ComputeRequiredTicks(threshold, 0, tier, 0);
                    double savedSecondsPerUnit = (withoutTool - withTool) / (double)TicksPerSecond;

                    Assert.True(savedSecondsPerUnit > 0.0,
                        $"Tool tier {tier} must actually reduce the tick threshold.");

                    // Units the player must gather before the tool has paid for
                    // itself, against the units it cost to make.
                    double costSeconds = cost * withoutTool / (double)TicksPerSecond;
                    double payoffUnits = costSeconds / savedSecondsPerUnit;

                    // A tool that needs more than a region's worth of gathering
                    // to break even is not an upgrade, it is a tax.
                    //
                    // Modul: the ceiling rose with the tool costs, which rose
                    // with the season curve. What the check is for has not
                    // changed - a tool must repay itself inside the region it
                    // belongs to - and region 5 is now nine thousand hours, so
                    // the number of units that fits inside it is far larger.
                    Assert.True(payoffUnits < 20_000_000,
                        $"Tool tier {tier} needs {payoffUnits:F0} units to pay for itself.");
                }
            }
        }

        // Modul: the rule, asserted rather than trusted. Equipment is monster
        // loot and tools are crafted. A recipe producing a wearable piece would
        // reopen the second crafting system that was removed with
        // CraftingReceptuary, and it would do it quietly - the item would just
        // appear in the tree one day.
        [Fact]
        public void Test_Crafting_ProducesToolsAndNothingWearable()
        {
            foreach (var recipe in ContentRegistry.Recipes)
            {
                string baseId = ContentRegistry.GetItemBaseId(recipe.ResultItemId);
                Assert.False(string.IsNullOrEmpty(baseId),
                    $"Recipe {recipe.ResultItemId} produces an item that is not in the catalogue.");

                Assert.True(ContentRegistry.GetToolKind(baseId) >= 0,
                    $"Recipe output '{baseId}' is not a tool. Equipment is a drop, not a craft.");
            }
        }

        // ------------------------------------------------------------------

        // A List rather than an iterator on purpose: the registries are
        // ReadOnlySpan properties, and a span cannot live across a yield.
        private static List<(int Region, RegionModel Model)> BuildRegionModels()
        {
            var models = new List<(int, RegionModel)>();
            double previousCumulativeXp = 0.0;

            for (int region = 1; region <= 5; region++)
            {
                int weaponAttackPower = 0;
                int armourRating = 0;
                foreach (var item in ContentRegistry.ItemDefinitions)
                {
                    if (item.RegionTier != region) continue;
                    if (item.FlatAttackPower > weaponAttackPower) weaponAttackPower = item.FlatAttackPower;
                }
                armourRating = BestArmourForRegion(region);

                // Modul: THE WEAPON HAS AFFIXES TOO. Armour was modelled with
                // them and damage was not, so this compared a player who rolls
                // their armour against one who never touches their weapon - and
                // then read the resulting slow kills as time spent fishing.
                double damagePerHit = BaseAttackDamage + weaponAttackPower;
                if (AffixRegistry.TryGetDefinition("melee_dmg_pct", out var meleeDamage))
                {
                    // Three damage rolls - a weapon carries more than one, and
                    // this stays a rung below the ceiling for the same reason
                    // the armour side does.
                    int pct = 3 * AffixRegistry.CalculateMagnitude(meleeDamage, region, RarityForRegion(region));
                    damagePerHit *= 1.0 + (pct / 100.0);
                }
                double dps = damagePerHit * (1000.0 / BaseAttackIntervalMs);

                double cumulativeXp = 0.0;
                for (int level = 0; level < region * 20; level++)
                {
                    cumulativeXp += ProgressionEngine.GetRequiredXpForLevel(level);
                }
                double regionXp = cumulativeXp - previousCumulativeXp;
                previousCumulativeXp = cumulativeXp;

                double combatHours = regionXp / (dps * XpPerDamagePoint) / 3600.0;

                models.Add((region, new RegionModel
                {
                    CombatHours = combatHours,
                    ToolHours = ToolGatheringHours(region),
                    FoodHours = FoodGatheringHours(region, combatHours, armourRating),
                }));
            }

            return models;
        }

        // Wood and ore, for the region's tools. Assumes the player makes a full
        // set - three kinds at both of the region's tiers - which is the
        // ceiling on this half rather than an average player.
        private static double ToolGatheringHours(int region)
        {
            long units = 0;
            foreach (int tier in TiersForRegion(region))
            {
                units += ToolSetCostForTier(tier);
            }

            int threshold = NodeThresholdForRegion(region, professionType: 0);
            return units * threshold / (double)TicksPerSecond / 3600.0;
        }

        // Fish, for the larder. Consumption rather than a shopping list: the
        // health bar drops at (monster attack - armour) per swing, and a bite
        // is worth a share of that bar.
        private static double FoodGatheringHours(int region, double combatHours, int armourRating)
        {
            var monster = StrongestRegularOfRegion(region);

            double monsterCritChance = 0.05 + (region * 0.005);
            double expectedCritMultiplier = 1.0 + monsterCritChance * 0.5;
            double rawIncomingMilliDamage = monster.AttackPower * 1000.0 * expectedCritMultiplier;
            // Modul: armour reduces. This was the fifth private copy of
            // `raw - armour` and it survived the engine dropping the rule by a
            // few minutes: with a monster attack rewritten for the percentage
            // model, subtraction returned the 1 HP floor for everything and
            // this test cheerfully reported that food had become free.
            double netIncomingMilliDamage = CombatDamageModel.Mitigate(
                (long)rawIncomingMilliDamage,
                armourRating,
                CombatDamageModel.PlayerArmourHalvingConstant(region));
            double attacksPerSecond = monster.AttackIntervalMs > 0 ? 1000.0 / monster.AttackIntervalMs : 0.0;
            double incomingMilliHpPerSecond = netIncomingMilliDamage * attacksPerSecond;

            // Modul: HP, not armour. This read the armour rating as the size
            // of the health bar - two different stats off two different affix
            // curves, and the one it picked is the one that does NOT feed the
            // bar. It matters because a bite heals a percentage of max HP, so
            // the wrong bar size prices every fish wrongly.
            long effectiveMaxMilliHp = 100_000L;
            if (AffixRegistry.TryGetDefinition("flat_hp", out var flatHp))
            {
                // Five pieces carrying a health roll, at the rarity this region
                // expects - the same loadout the armour side models.
                effectiveMaxMilliHp +=
                    5L * AffixRegistry.CalculateMagnitude(flatHp, region, RarityForRegion(region)) * 1000L;
            }
            int fishItemId = RegionFishItemId(region);
            int healMilliHp = FoodRegistry.GetHealMilliHp(fishItemId, effectiveMaxMilliHp);
            if (healMilliHp <= 0) return 0.0;

            // Modul: AUTO-EAT HAS A COOLDOWN, so food demand is bounded by TIME
            // and not only by damage taken.
            //
            // Without this the model reads "damage doubled, so fishing
            // doubled", which stopped being true the moment the larder was
            // rate-limited: a character can eat at most one fish every
            // AutoEatCooldownTicks whatever is hitting it. Past that ceiling
            // the player does not fish more - they die, which is a different
            // number this test is not measuring.
            double fishWanted = incomingMilliHpPerSecond * combatHours * 3600.0 / healMilliHp;
            double biteCeiling = combatHours * 3600.0
                / (Domain.Combat.SimulationEngine.AutoEatCooldownTicks / 10.0);
            double fishNeeded = Math.Min(fishWanted, biteCeiling);
            int threshold = NodeThresholdForRegion(region, professionType: 2);
            return fishNeeded * threshold / (double)TicksPerSecond / 3600.0;
        }

        private static int BestArmourForRegion(int region)
        {
            string[] slots = { "_helmet_", "_chest_", "_gloves_", "_leggings_", "_boots_" };
            int total = 0;
            foreach (string slot in slots)
            {
                int best = 0;
                foreach (var item in ContentRegistry.ItemDefinitions)
                {
                    if (item.RegionTier != region) continue;
                    string baseId = ContentRegistry.GetItemBaseId(item.Id);
                    if (!baseId.Contains(slot)) continue;
                    if (item.FlatDefenseRating > best) best = item.FlatDefenseRating;
                }
                total += best;
            }

            // Modul: A PLAYER WEARS AFFIXES, and this model used to pretend
            // they did not.
            //
            // Base-only armour was a fair floor while affixes were worth a
            // tenth of the item they sat on. They are not any more - the
            // scaling laws now follow the gear curve, deliberately, so that
            // rarity decides whether a monster is survivable. Modelling armour
            // without them reports a player being hit three times as hard as
            // anyone actually progressing would be, and therefore fishing three
            // times as much.
            //
            // One armour affix per piece, at the rarity a player reaching this
            // region is wearing - see RarityForRegion. It was a fixed Rare,
            // which understates the late game by the full width of the affix
            // scale and overstates the early one.
            var flatArmour = default(AffixDefinition);
            if (AffixRegistry.TryGetDefinition("flat_armor", out flatArmour))
            {
                total += 5 * AffixRegistry.CalculateMagnitude(flatArmour, region, RarityForRegion(region));
            }

            return total;
        }

        private static MonsterDefinition StrongestRegularOfRegion(int region)
        {
            // Modul: THE THIRD OF FOUR, not the fourth.
            //
            // This took the strongest regular, which was right while monster
            // attack was a trickle and every target was equally survivable. It
            // stopped being right when the fourth regular became a WALL - it is
            // sized to kill a player who walked into the region in starter gear,
            // which means nobody farms it for hours. Sizing food demand against
            // a monster a player only fights once they have out-geared it
            // reports a larder bill nobody pays.
            //
            // The third is what farming actually looks like: the hardest target
            // a player can sit on indefinitely. A player who takes the fourth
            // before they are ready does not fish more, they die - which is a
            // different measurement than this one.
            int firstId = 91 + (region - 1) * 5;
            return ContentRegistry.Monsters[firstId - 1 + 2];
        }

        private static int RegionFishItemId(int region)
        {
            foreach (int id in ContentRegistry.RawFishItemIds)
            {
                if (ContentRegistry.ItemDefinitions[id - 1].RegionTier == region) return id;
            }
            return 0;
        }

        private static int NodeThresholdForRegion(int region, int professionType)
        {
            int threshold = 0;
            foreach (var node in ContentRegistry.GatheringNodes)
            {
                if (node.ProfessionType != professionType) continue;
                if ((node.ActivityId % 1000) != region) continue;
                if (node.BaseTickThreshold > threshold) threshold = node.BaseTickThreshold;
            }
            return threshold;
        }

        private static IEnumerable<int> TiersForRegion(int region) => new[] { region * 2 - 1, region * 2 };

        // What one full set of a tier costs: three kinds, both materials.
        private static long ToolSetCostForTier(int tier)
        {
            long total = 0;
            foreach (var recipe in ContentRegistry.Recipes)
            {
                string baseId = ContentRegistry.GetItemBaseId(recipe.ResultItemId);
                if (ContentRegistry.GetToolKind(baseId) < 0) continue;
                if (ContentRegistry.GetToolTier(baseId) != tier) continue;
                total += recipe.Mat1Count + recipe.Mat2Count;
            }
            return total;
        }
    }
}
