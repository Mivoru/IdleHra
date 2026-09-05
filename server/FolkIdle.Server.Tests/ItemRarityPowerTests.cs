using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// What an item's fourteen quality tiers are actually worth.
    ///
    /// Reported 2026-09-04: "there isn't much difference between them, and the
    /// biggest difference is in tiers and not the 14 rarities". This file is
    /// the arithmetic behind that, printed rather than argued, and it is the
    /// BASELINE any change to rarity has to be measured against.
    ///
    /// THE CONSTRAINT THIS EXISTS TO SERVE. The ask is not "make rarity
    /// stronger" - that would inflate player power, and the monster ladder, the
    /// XP curve and the measured ~13h to level 100 are all pinned against
    /// today's power. The ask is to REDISTRIBUTE: take weight out of the item's
    /// region tier and put it into its quality tier, holding the total. That
    /// needs no monster retune at all if it holds, and "roughly the same" is
    /// not checkable after the fact - so the numbers have to exist before the
    /// change, which is what this file is for.
    ///
    /// These tests deliberately assert very little. Two of them pin facts that
    /// phase F is expected to CHANGE, so they will fail when it lands - that is
    /// the point of a characterisation test, and the failure message says what
    /// to do about it.
    ///
    /// Fixture-free: pure content and pure arithmetic.
    /// </summary>
    public class ItemRarityPowerTests
    {
        private readonly ITestOutputHelper _output;

        public ItemRarityPowerTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        // The drop weights from CombatLootEngine.RollTier, restated. Tier 1 is
        // a flat 100 that luck never scales; tiers 2-14 are the explicit table.
        // Restated rather than read because they are private there - and
        // because a second list that must agree is how this repo catches a
        // table changing under a test that would otherwise follow it anywhere.
        private static readonly double[] TierWeights =
        {
            0.0,
            100.0,   // 1  Normal
            50.0,    // 2  Common
            25.0,    // 3  Uncommon
            12.5,    // 4  Rare
            5.0,     // 5  Ultra Rare
            2.5,     // 6  Epic
            1.0,     // 7  Legendary
            0.5,     // 8  Mythic
            0.1,     // 9  Relic
            0.05,    // 10 Ancient
            0.01,    // 11 Divine
            0.005,   // 12 Demonic
            0.001,   // 13 Godly
            0.0001,  // 14 Transcendent
        };

        private static readonly string[] TierNames =
        {
            "", "Normal", "Common", "Uncommon", "Rare", "Ultra Rare", "Epic", "Legendary",
            "Mythic", "Relic", "Ancient", "Divine", "Demonic", "Godly", "Transcendent",
        };

        /// <summary>Authored weapon attack by region: the only geometric term in an item's power.</summary>
        private static int WeaponBaseAttack(int regionTier)
        {
            int best = 0;
            foreach (var item in ContentRegistry.ItemDefinitions)
            {
                if (item.FlatAttackPower > best && item.RegionTier == regionTier)
                {
                    best = item.FlatAttackPower;
                }
            }
            return best;
        }

        /// <summary>
        /// Expected magnitude of ONE freshly rolled affix of a given definition,
        /// averaged over the affix-rarity table.
        ///
        /// Sampled through the real RollAffixRarity and CalculateMagnitude
        /// rather than restating either, so this measures the system instead of
        /// a description of it. 200k samples puts the mean well inside a
        /// percent, which is finer than any decision made from this table.
        /// </summary>
        private static double ExpectedMagnitude(in AffixDefinition definition, int regionTier, int itemRarityTier, int samples = 200_000)
        {
            long total = 0;
            for (int i = 0; i < samples; i++)
            {
                total += AffixRegistry.CalculateMagnitude(
                    definition, regionTier, AffixRegistry.RollAffixRarity(itemRarityTier));
            }
            return (double)total / samples;
        }

        private readonly record struct WeaponPower(double Base, double PercentUplift, double FlatAdded)
        {
            /// <summary>One scalar, so tiers can be compared: base plus flat, scaled by the percentage affixes.</summary>
            public double Effective => (Base + FlatAdded) * (1.0 + PercentUplift);
        }

        /// <summary>
        /// Expected power of a weapon at a given region and quality tier.
        ///
        /// A weapon's legal affix pool is eight entries and RollAffixes prefers
        /// one it does not already carry, so up to five rolls are five DISTINCT
        /// affixes drawn uniformly from those eight. The expectation per roll is
        /// therefore the mean over the pool.
        /// </summary>
        private static WeaponPower ExpectedWeaponPower(int regionTier, int qualityTier)
        {
            int affixCount = RarityTier.GetAffixCount(qualityTier);

            var weaponAffixes = new List<AffixDefinition>();
            foreach (var definition in AffixRegistry.Definitions)
            {
                if ((definition.AllowedSlots & EquipmentSlotMask.Weapon) != 0)
                {
                    weaponAffixes.Add(definition);
                }
            }

            double meanPercentTenths = 0;
            double meanFlat = 0;
            int percentCount = 0, flatCount = 0;

            foreach (var definition in weaponAffixes)
            {
                double expected = ExpectedMagnitude(definition, regionTier, qualityTier, samples: 40_000);
                if (definition.Law == AffixScalingLaw.Percentage)
                {
                    meanPercentTenths += expected;
                    percentCount++;
                }
                else
                {
                    meanFlat += expected;
                    flatCount++;
                }
            }

            int poolSize = weaponAffixes.Count;
            // Per roll, the chance of drawing a percentage affix is its share of
            // the pool, and likewise for the flat one.
            double perRollPercentTenths = percentCount == 0 ? 0 : (meanPercentTenths / percentCount) * percentCount / poolSize;
            double perRollFlat = flatCount == 0 ? 0 : (meanFlat / flatCount) * flatCount / poolSize;

            return new WeaponPower(
                WeaponBaseAttack(regionTier) * RarityTier.PowerMultiplier(qualityTier),
                affixCount * perRollPercentTenths / 1000.0, // tenths of a percent -> fraction
                affixCount * perRollFlat);
        }

        /// <summary>
        /// Expected power under the OLD rules, for the before/after column.
        ///
        /// Restates the two things phase F changed and nothing else: base power
        /// ignored the quality tier, and affix rarity ignored it too. Everything
        /// else runs through the same live code as the new figure, so the
        /// comparison is of the change and not of two different models.
        /// </summary>
        private static WeaponPower OldExpectedWeaponPower(int regionTier, int qualityTier)
        {
            int affixCount = RarityTier.GetAffixCount(qualityTier);

            var weaponAffixes = new List<AffixDefinition>();
            foreach (var definition in AffixRegistry.Definitions)
            {
                if ((definition.AllowedSlots & EquipmentSlotMask.Weapon) != 0) weaponAffixes.Add(definition);
            }

            double percentTenths = 0, flat = 0;
            foreach (var definition in weaponAffixes)
            {
                long total = 0;
                const int samples = 40_000;
                for (int i = 0; i < samples; i++)
                {
                    // The old call: no item tier, so the flat rarity table.
                    total += AffixRegistry.CalculateMagnitude(definition, regionTier, AffixRegistry.RollAffixRarity());
                }
                double expected = (double)total / samples;
                if (definition.Law == AffixScalingLaw.Percentage) percentTenths += expected; else flat += expected;
            }

            int poolSize = weaponAffixes.Count;
            return new WeaponPower(
                WeaponBaseAttack(regionTier), // no quality multiplier
                affixCount * (percentTenths / poolSize) / 1000.0,
                affixCount * (flat / poolSize));
        }

        /// <summary>
        /// The chance that the best of N drops is exactly tier t.
        ///
        /// A player does not wear a random drop - they wear the best one they
        /// have found for that slot. P(best &lt;= t) = F(t)^N, so the mass on
        /// each tier is the difference of two powers of the CDF. This is the
        /// only honest way to ask "what is equipped", and it is what the
        /// neutrality target has to be measured against.
        /// </summary>
        private static double[] BestOfNTierMass(int drops)
        {
            double total = TierWeights.Sum();
            var cdf = new double[15];
            double running = 0;
            for (int tier = 1; tier <= 14; tier++)
            {
                running += TierWeights[tier] / total;
                cdf[tier] = running;
            }

            var mass = new double[15];
            for (int tier = 1; tier <= 14; tier++)
            {
                double below = tier == 1 ? 0.0 : Math.Pow(cdf[tier - 1], drops);
                mass[tier] = Math.Pow(cdf[tier], drops) - below;
            }
            return mass;
        }

        [Fact]
        public void HowMuchStrongerThePlayerGets_WhichIsExactlyTheMonsterBuffOwed()
        {
            var report = new StringBuilder();
            report.AppendLine("Player power before and after the rarity rework, by how many drops");
            report.AppendLine("they have seen for the slot. A player wears the BEST of N, not a");
            report.AppendLine("random one, so this is the distribution that matters.");
            report.AppendLine();
            report.AppendLine("Anchored at tier 1: a Normal item is worth exactly what it was, and");
            report.AppendLine("nothing anyone already owns got weaker. The whole change is upward,");
            report.AppendLine("which is why a monster buff is owed rather than optional.");
            report.AppendLine();

            const int region = 3; // mid-game, where a live account actually sits
            var oldPower = new double[15];
            var newPower = new double[15];
            for (int tier = 1; tier <= 14; tier++)
            {
                oldPower[tier] = OldExpectedWeaponPower(region, tier).Effective;
                newPower[tier] = ExpectedWeaponPower(region, tier).Effective;
            }

            report.AppendLine("  drops seen   typical tier   power before   power after   INFLATION");
            double worst = 0;
            foreach (int drops in new[] { 1, 10, 50, 200, 1000, 5000 })
            {
                double[] mass = BestOfNTierMass(drops);
                double before = 0, after = 0, meanTier = 0;
                for (int tier = 1; tier <= 14; tier++)
                {
                    before += mass[tier] * oldPower[tier];
                    after += mass[tier] * newPower[tier];
                    meanTier += mass[tier] * tier;
                }
                double inflation = after / before;
                if (inflation > worst) worst = inflation;
                report.AppendLine($"  {drops,10}   {meanTier,12:F2}   {before,12:F1}   {after,11:F1}   {inflation,8:F3}x");
            }

            report.AppendLine();
            report.AppendLine($"  WORST-CASE INFLATION ACROSS THESE: {worst:F3}x");
            report.AppendLine();
            report.AppendLine("  THE MONSTER BUFF THIS OWES. Player damage rises by the figure above,");
            report.AppendLine("  so monster HEALTH has to rise by the same factor for a kill to take");
            report.AppendLine("  as long as it did. Monster ATTACK must NOT be raised with it: the");
            report.AppendLine("  player's defensive gear inflated by the same amount, so raising");
            report.AppendLine("  attack too would buff monsters twice.");
            report.AppendLine();
            report.AppendLine("  AND XP MUST NOT FOLLOW HEALTH. Every monster pays XP = MaxHp/5, so");
            report.AppendLine("  scaling health alone would hand out proportionally more XP for the");
            report.AppendLine("  same real time and quietly speed up levelling - which is the exact");
            report.AppendLine("  interaction ProgressionRateTests exists to catch.");

            _output.WriteLine(report.ToString());

            // A sanity band, not a target. If this ever reads far outside it the
            // curve was retuned and the monster buff needs recomputing.
            Assert.InRange(worst, 1.0, 3.0);
        }

        /// <summary>
        /// The compensating monster health buff, read back OUT OF THE CONTENT.
        ///
        /// Every canonical monster paid XP = MaxHp / 5 exactly until 2026-09-05.
        /// The buff raised health and deliberately left XP alone, so the ratio
        /// the content now carries IS the multiplier that was applied:
        /// buff = MaxHp / (5 * BaseXpReward). Derived rather than restated, so
        /// this test cannot drift from the file it is describing.
        /// </summary>
        private static double AppliedMonsterBuff(int region)
        {
            int id = ContentRegistry.FirstCanonicalMonsterId + (region - 1) * ContentRegistry.MonstersPerRegion;
            var m = ContentRegistry.Monsters[id - 1];
            return (double)m.MaxHp / (5.0 * m.BaseXpReward);
        }

        [Fact]
        public void WhatTheMonsterBuffActuallyRestored_KillTimeAndXpRate()
        {
            var report = new StringBuilder();
            report.AppendLine("The rarity rework made a geared player stronger; the monster health");
            report.AppendLine("buff gives the fight its length back. This is what is left over.");
            report.AppendLine();
            report.AppendLine("XP and gold were NOT raised with health, on purpose. Had they been,");
            report.AppendLine("XP per second would have risen by the same factor and levelling would");
            report.AppendLine("have run away - which is the whole reason the buff exists.");
            report.AppendLine();
            report.AppendLine("  region   player power   monster hp   residual kill-time   XP/sec");
            report.AppendLine("            inflation       buff          change            change");

            // Drops seen for a slot, by the region a player is fighting in.
            // Region is the only proxy content has for "how much have you
            // played", and it is a good one: you cannot reach region 5 without
            // having killed your way through the four before it.
            var dropsByRegion = new Dictionary<int, int> { { 1, 10 }, { 2, 50 }, { 3, 200 }, { 4, 1000 }, { 5, 5000 } };

            for (int region = 1; region <= 5; region++)
            {
                double[] mass = BestOfNTierMass(dropsByRegion[region]);
                double before = 0, after = 0;
                for (int tier = 1; tier <= 14; tier++)
                {
                    before += mass[tier] * OldExpectedWeaponPower(region, tier).Effective;
                    after += mass[tier] * ExpectedWeaponPower(region, tier).Effective;
                }

                double inflation = after / before;
                double buff = AppliedMonsterBuff(region);

                // Kill time scales as monster health over player damage.
                double killTime = buff / inflation;
                // XP per kill is unchanged, so XP per second moves inversely
                // with kill time.
                double xpRate = 1.0 / killTime;

                report.AppendLine(
                    $"  {region,6}   {inflation,11:F3}x   {buff,9:F3}x   {killTime,17:F3}x   {xpRate,8:F3}x");
            }

            report.AppendLine();
            report.AppendLine("  Region 1 is deliberately unbuffed. A brand-new player has seen about");
            report.AppendLine("  one drop and is 1.07x stronger, and this game has already shipped a");
            report.AppendLine("  closed entrance once - a new account that followed onboarding's first");
            report.AppendLine("  instruction died to the first monster and the tutorial never moved.");
            report.AppendLine("  A veteran farming region 1 finds it easier than before, which is what");
            report.AppendLine("  a tutorial region should do.");
            report.AppendLine();
            report.AppendLine("  Regions 3 and 5 are capped BELOW their measured inflation by");
            report.AppendLine("  Test_Content_EveryMonsterDiesInsideTheAttentionSpan, which models a");
            report.AppendLine("  player ON ARRIVAL - no affixes, no set bonuses - and refuses a regular");
            report.AppendLine("  monster that takes over 180s for them. So the most heavily geared");
            report.AppendLine("  players keep some of the speed-up. Slowing them further would price an");
            report.AppendLine("  arriving player out of the region, which is the worse failure.");

            _output.WriteLine(report.ToString());

            // The residual must not go the wrong way: no region may end up
            // SLOWER to level than it was, or the buff has overshot and the
            // rarity rework has been undone by its own compensation.
            for (int region = 1; region <= 5; region++)
            {
                double[] mass = BestOfNTierMass(dropsByRegion[region]);
                double before = 0, after = 0;
                for (int tier = 1; tier <= 14; tier++)
                {
                    before += mass[tier] * OldExpectedWeaponPower(region, tier).Effective;
                    after += mass[tier] * ExpectedWeaponPower(region, tier).Effective;
                }
                double xpRate = (after / before) / AppliedMonsterBuff(region);
                Assert.True(xpRate >= 0.95,
                    $"region {region} now levels at {xpRate:F3}x its old rate - the monster buff overshot " +
                    "and is taking back more than the rarity rework gave.");
            }
        }

        [Fact]
        public void PrintTheDropDistribution_AndWhereItsMedianFalls()
        {
            double total = TierWeights.Sum();
            var report = new StringBuilder();
            report.AppendLine("What a drop actually IS, at zero loot luck:");
            report.AppendLine();
            report.AppendLine("  tier  name             share      cumulative");

            double cumulative = 0;
            int medianTier = 0;
            for (int tier = 1; tier <= 14; tier++)
            {
                double share = TierWeights[tier] / total;
                cumulative += share;
                if (medianTier == 0 && cumulative >= 0.5) medianTier = tier;
                report.AppendLine($"  {tier,4}  {TierNames[tier],-15} {share,8:P3}   {cumulative,8:P3}");
            }

            report.AppendLine();
            report.AppendLine($"  MEDIAN DROP IS TIER {medianTier} ({TierNames[medianTier]}).");
            report.AppendLine();
            report.AppendLine("  Worth reading twice: half of everything that drops is the LOWEST");
            report.AppendLine("  tier in the game, and 89% of it is one of the bottom three - which");
            report.AppendLine("  GetAffixCount treats as identical. So the tiers a player meets are");
            report.AppendLine("  overwhelmingly the ones that are mechanically the same.");
            report.AppendLine();
            report.AppendLine("  A player does not WEAR a median drop, though - they wear the best");
            report.AppendLine("  of many. That is why the neutrality target in phase F is the median");
            report.AppendLine("  of what is EQUIPPED, and this table is the input to it, not the");
            report.AppendLine("  answer.");

            _output.WriteLine(report.ToString());

            Assert.Equal(1, medianTier);
        }

        [Fact]
        public void PrintExpectedWeaponPowerAtEveryTier_TheBaselinePhaseFMustNotMove()
        {
            var report = new StringBuilder();
            report.AppendLine("Expected weapon power by quality tier. BASELINE, before any change.");
            report.AppendLine();

            foreach (int region in new[] { 1, 5 })
            {
                report.AppendLine($"  REGION {region} - authored base attack {WeaponBaseAttack(region)}");
                report.AppendLine("   tier  name            affixes   base   +flat   +pct     effective   vs tier 1");
                double tierOnePower = 0;
                for (int tier = 1; tier <= 14; tier++)
                {
                    WeaponPower power = ExpectedWeaponPower(region, tier);
                    if (tier == 1) tierOnePower = power.Effective;
                    report.AppendLine(
                        $"   {tier,4}  {TierNames[tier],-14} {RarityTier.GetAffixCount(tier),6}  " +
                        $"{power.Base,6:F0} {power.FlatAdded,7:F1} {power.PercentUplift,7:P1}  " +
                        $"{power.Effective,10:F1}   {power.Effective / tierOnePower,7:F3}x");
                }
                report.AppendLine();
            }

            double region1Top = ExpectedWeaponPower(1, 14).Effective;
            double region1Bottom = ExpectedWeaponPower(1, 1).Effective;
            double region5Bottom = ExpectedWeaponPower(5, 1).Effective;

            report.AppendLine("  THE HEADLINE, and it is the player's complaint stated as a number:");
            report.AppendLine();
            report.AppendLine($"    the whole 14-tier rarity ladder, at one region:  {region1Top / region1Bottom,6:F2}x");
            report.AppendLine($"    one region step to the next (base attack x3):    {3.0,6:F2}x");
            report.AppendLine($"    region 1 to region 5, at the same tier:          {region5Bottom / region1Bottom,6:F2}x");
            report.AppendLine();
            report.AppendLine("  A full rarity ladder is now worth exactly one region step. It was");
            report.AppendLine("  1.48x against 3.00x before 2026-09-04 - less than half a region -");
            report.AppendLine("  which is what the player meant by 'the biggest difference is in");
            report.AppendLine("  tiers and not the 14 rarities'.");
            report.AppendLine();
            report.AppendLine("  Rarity is an axis of progression now, and still never overtakes");
            report.AppendLine("  playing the game: a Transcendent from one region below loses to a");
            report.AppendLine("  Normal from two regions above, because 3.00 < 9.00.");

            _output.WriteLine(report.ToString());

            // THE TARGET, not a characterisation: a full rarity ladder is worth
            // one region step, which is the decision taken on 2026-09-04. If
            // this fails, either RarityTier.TopTierPowerMultiplier or the
            // affix-rarity bias moved - and the monster buff owed has to be
            // recomputed with it.
            double rarityLadder = region1Top / region1Bottom;
            Assert.InRange(rarityLadder, 2.85, 3.15);
        }

        [Fact]
        public void NoTwoAdjacentQualityTiersAreMechanicallyIdentical()
        {
            // BEFORE 2026-09-04 THIS LIST HAD NINE ENTRIES OUT OF THIRTEEN.
            // Affix count was the only thing an item quality tier controlled,
            // and GetAffixCount buckets fourteen tiers into five values - so
            // Normal and Common were the same item with different coloured
            // text, and so were Godly and Transcendent, at the very top of the
            // ladder where it should matter most.
            //
            // Affix count still buckets. What ended the problem is that quality
            // now also scales base power smoothly (RarityTier.PowerMultiplier)
            // and biases affix rarity (AffixRegistry.RollAffixRarity(int)), so
            // every step up the ladder is worth something even where the affix
            // count does not change.
            var identical = new List<string>();
            var report = new StringBuilder();
            report.AppendLine("  tier -> tier   power step");

            for (int tier = 1; tier < 14; tier++)
            {
                double lower = ExpectedWeaponPower(1, tier).Effective;
                double upper = ExpectedWeaponPower(1, tier + 1).Effective;
                double step = upper / lower;
                report.AppendLine($"  {TierNames[tier],-13} -> {TierNames[tier + 1],-13} {step,6:F3}x");

                // One percent is well outside the sampling noise on these
                // figures and far below anything a player would notice, so it
                // separates "identical" from "small" without being fragile.
                if (step < 1.01) identical.Add($"{TierNames[tier]} == {TierNames[tier + 1]}");
            }

            _output.WriteLine(report.ToString());
            Assert.True(identical.Count == 0,
                "these adjacent tiers are still mechanically identical: " + string.Join(", ", identical));
        }

        [Fact]
        public void QualityTierNowScalesBasePower_AndUsedToContributeNothing()
        {
            // This is the lever that made the change possible at all. Affixes
            // alone could not carry it: a weapon rolls almost entirely
            // Percentage-law affixes, worth 2.4% to 17% in total, against a base
            // attack that triples every region. Measured before the change, the
            // entire fourteen-tier ladder was worth 1.48x for that reason.
            foreach (int region in new[] { 1, 2, 3, 4, 5 })
            {
                double baseAtWorst = ExpectedWeaponPower(region, 1).Base;
                double baseAtBest = ExpectedWeaponPower(region, 14).Base;

                Assert.True(baseAtBest > baseAtWorst,
                    $"region {region}: quality tier contributes nothing to base power again");
                Assert.Equal(RarityTier.TopTierPowerMultiplier, baseAtBest / baseAtWorst, 2);
            }

            _output.WriteLine("Weapon base attack by region, at tier 1: " +
                string.Join(" / ", Enumerable.Range(1, 5).Select(WeaponBaseAttack)));
            _output.WriteLine($"A Transcendent carries {RarityTier.TopTierPowerMultiplier:F2}x the base power");
            _output.WriteLine("of a Normal from the same region. It carried exactly the same until 2026-09-04.");
        }

        [Fact]
        public void RollAffixesNowReadsTheItemRarityTierItIsHanded()
        {
            // AffixRegistry.RollAffixes took an itemRarityTier and NEVER READ
            // IT - a dead parameter, which in this codebase almost always means
            // an intended influence that was never wired. This test used to
            // assert the two were indistinguishable; it now asserts the
            // relationship that replaced it.
            //
            // The mechanism is best-of-N rather than a second weight table: a
            // higher tier gets more attempts at each affix rarity roll and
            // keeps the best, so it can never produce a magnitude the base
            // table could not - it just stops handing you the bottom of it.
            const int affixCount = 3;
            const int region = 3;
            const int samples = 20_000;

            double MeanTotal(int itemRarityTier)
            {
                long total = 0;
                var destination = new Dictionary<string, int>(8);
                for (int i = 0; i < samples; i++)
                {
                    destination.Clear();
                    AffixRegistry.RollAffixes(
                        "eq_steel_claymore_melee_weapon_slot_base", region, itemRarityTier, affixCount, destination);
                    foreach (var pair in destination) total += pair.Value;
                }
                return (double)total / samples;
            }

            double atNormal = MeanTotal(1);
            double atTranscendent = MeanTotal(14);

            _output.WriteLine("mean summed affix magnitude at 3 affixes, region 3:");
            _output.WriteLine($"  itemRarityTier = 1  (Normal):        {atNormal:F2}");
            _output.WriteLine($"  itemRarityTier = 14 (Transcendent):  {atTranscendent:F2}");
            _output.WriteLine($"  worth {atTranscendent / atNormal:F2}x on affix magnitude alone");

            Assert.True(atTranscendent > atNormal * 1.10,
                $"the item rarity tier is worth only {atTranscendent / atNormal:F3}x on its affixes - " +
                "RollAffixRarity(int) is not being reached, or its bias has been flattened");
        }

    }
}
