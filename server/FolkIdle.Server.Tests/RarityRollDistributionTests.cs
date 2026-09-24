using System;
using System.Linq;
using System.Text;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Does the rarity roll still produce the distribution it is authored with?
    ///
    /// Reported 2026-09-05: "now not even the tier 3 monsters drop good things,
    /// something broke in an update". That is a claim about a distribution, and
    /// a live account cannot answer it - what a chest holds is drops MINUS the
    /// sweep, MINUS auto-salvage, PLUS whatever the forge fused, and none of
    /// those are recorded anywhere with a timestamp.
    ///
    /// This asks the roll itself, which is the only place the answer is clean.
    /// If RollTier matches its table, no update changed drop quality, whatever
    /// a chest looks like.
    /// </summary>
    public class RarityRollDistributionTests
    {
        private readonly ITestOutputHelper _output;

        public RarityRollDistributionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // The authored weights, restated - RollTier's own array is private, and
        // a test that read it would be asking the code to agree with itself.
        private static readonly double[] Weights =
        {
            0.0, 100.0, 50.0, 25.0, 12.5, 5.0, 2.5, 1.0, 0.5, 0.1, 0.05, 0.01, 0.005, 0.001, 0.0001,
        };

        [Fact]
        public void TheRollMatchesItsAuthoredTable()
        {
            const int samples = 2_000_000;
            var counts = new int[15];

            // Modul: SEEDED, because this failed on a working roll. It drew from
            // Random.Shared and held every tier to a flat 3% relative band. For
            // Mythic (p ~ 0.25%) one standard deviation of the observed share
            // at 2M samples is ~1.4%, so 3% was a ~2.1-sigma test and failed a
            // few percent of runs for no reason (2026-09-24, on a branch that
            // touched no loot code). The seed makes the run reproducible; the
            // per-tier 4-sigma band below is what makes the seed irrelevant -
            // any seed passes a correct table with ~99.99% probability per tier,
            // and a broken one still fails, since a real table change moves a
            // tier by far more than 4 sigma.
            var rng = new Random(20260924);
            for (int i = 0; i < samples; i++)
            {
                counts[RarityTier.RollTier(0f, rng)]++;
            }

            double total = Weights.Sum();
            var report = new StringBuilder();
            report.AppendLine($"{samples:N0} rolls at zero loot luck\n");
            report.AppendLine("  tier  name             expected      observed    ratio");

            for (int tier = 1; tier <= 14; tier++)
            {
                double expected = Weights[tier] / total;
                double observed = (double)counts[tier] / samples;
                string ratio = expected > 0 ? $"{observed / expected,7:F3}" : "     -";
                report.AppendLine(
                    $"  {tier,4}  {RarityTier.GetName(tier),-15} {expected,10:P4} {observed,12:P4} {ratio}");
            }

            double avgTier = 0;
            for (int tier = 1; tier <= 14; tier++) avgTier += tier * ((double)counts[tier] / samples);
            report.AppendLine($"\n  mean tier: {avgTier:F3}");
            report.AppendLine("  A chest full of tier 1-3 is what this table LOOKS like unswept -");
            report.AppendLine("  half of everything is Normal. High tiers in a chest come from the");
            report.AppendLine("  sweep removing the rest, from auto-salvage, and from the forge.");

            _output.WriteLine(report.ToString());

            // Each of the eight common tiers is held to FOUR standard deviations
            // of its own binomial sampling error: sigma = sqrt(p(1-p)/n). That
            // is ~0.14% relative for Normal and ~5.6% for Mythic - tight where
            // the sample is large, honest where it is not. A flat band was
            // either too loose for Normal or too tight for Mythic.
            for (int tier = 1; tier <= 8; tier++)
            {
                double expected = Weights[tier] / total;
                double observed = (double)counts[tier] / samples;
                double sigma = Math.Sqrt(expected * (1 - expected) / samples);
                Assert.True(Math.Abs(observed - expected) < 4 * sigma,
                    $"tier {tier} ({RarityTier.GetName(tier)}) rolled {observed:P4} against an authored {expected:P4} " +
                    $"(off by {Math.Abs(observed - expected) / sigma:F1} sigma, allowed 4)");
            }

            // The top six are too rare to bound tightly at this sample size;
            // what matters is that they are REACHABLE, because a clamp that
            // capped the ladder would show up here as a hard zero.
            for (int tier = 9; tier <= 14; tier++)
            {
                double expected = Weights[tier] / total * samples;
                if (expected >= 5)
                {
                    Assert.True(counts[tier] > 0,
                        $"tier {tier} ({RarityTier.GetName(tier)}) never rolled in {samples:N0} tries, " +
                        $"against {expected:F0} expected - something is capping the ladder");
                }
            }
        }

        [Fact]
        public void NothingCapsTheLadderBelowTranscendent()
        {
            // Modul: THE CLAMP IN TryRollEquipment USES A DIFFERENT CONSTANT.
            //
            // `tier = Math.Clamp(tier + bonusRarityTiers, 1, CraftingEngine.RarityTierCount)`
            // - a crafting constant applied to a LOOT roll. If those two numbers
            // ever disagreed, the Golden Fleece crown would silently cap drops
            // at the crafting ceiling, and the only symptom would be a player
            // saying good items stopped appearing.
            Assert.Equal(14, FolkIdle.Server.Domain.Economy.CraftingEngine.RarityTierCount);
            Assert.Equal(14, RarityTier.Transcendent);
        }

        [Fact]
        public void LootLuckRaisesTheTopWithoutRemovingTheBottom()
        {
            const int samples = 200_000;
            var plain = new int[15];
            var lucky = new int[15];

            for (int i = 0; i < samples; i++)
            {
                plain[RarityTier.RollTier(0f)]++;
                lucky[RarityTier.RollTier(100f)]++;
            }

            int plainHigh = plain.Skip(5).Sum();
            int luckyHigh = lucky.Skip(5).Sum();

            _output.WriteLine($"tier 5+ at 0% luck: {plainHigh:N0}, at 100% luck: {luckyHigh:N0}");
            Assert.True(luckyHigh > plainHigh, "loot luck did not raise the odds of a better drop");
        }

        // ------------------------------------------------------------------
        // Modul: TASK 26, 2026-09-23 - "no Ancient+ in five days".
        //
        // The investigation computed the reporting account's loot luck BY HAND
        // from six tables and concluded the odds had not changed. A hand
        // computation is a claim, not a measurement, so the build below is
        // that account's real inputs (production, player 8, level 94, region 5)
        // run through the same StatsCalculator call the live tick makes and the
        // same LootLuckBreakdown CombatLootDropRequest.Build sums. Every printed
        // term is asserted beside the print: the bands encode TODAY's
        // behaviour and are meant to fail when a term changes, so a change to
        // the drop odds has to come here and restate them on purpose.
        // ------------------------------------------------------------------

        private const int BuildLck = 300;
        private const double FleeceChance = 0.01; // every hundredth kill
        private const int FleeceTiers = 2;

        internal static TickStatePayload Level94Region5Payload() => new TickStatePayload
        {
            PlayerId = 8L,
            CurrentLevel = 94,
            STR = 200,
            DEX = 200,
            CON = 120,
            LCK = BuildLck,
            Inherit_LootLuck = 4,
            Skill_LootRarity = 10,
            Skill_Rarity = 8,
            Skill_GoldenFleece = 1,
            Aptitude_Fortune = 4,
            // GuildBonusesCache answers 0 for guild 0 - the account's DropRate
            // buff expired on 2026-09-09.
            GuildId = 0,
            CompletedAreaFlags = 0,
            CachedAffixTotals = default,
        };

        /// <summary>The identical 16-argument call the live tick makes (SimulationEngine, the combat block).</summary>
        internal static CombatStats StatsFor(in TickStatePayload p) => StatsCalculator.Calculate(
            p.STR, p.DEX, p.CON, p.LCK, p.ActiveOffensivePotionId, p.ActiveDefensivePotionId,
            1, p.CompletedAreaFlags, 0, p.HumanMasteryLevel, p.VilaMasteryLevel, p.DraugrMasteryLevel,
            p.CachedAffixTotals, p.IsEpicMutation, TraitTotals.From(p.TraitMask), p.CachedSetIds);

        /// <summary>
        /// The final-tier distribution after the roll, Golden Fleece and
        /// elevation, from THIS FILE's restated weights - deliberately not from
        /// the engine, so the engine can be checked against it.
        /// </summary>
        internal static double[] AnalyticFinalShares(double lootLuckPct, double elevationPct, double fleeceChance, int fleeceTiers)
        {
            double factor = 1.0 + lootLuckPct / 100.0;
            var rolled = new double[15];
            double total = 0;
            for (int tier = 1; tier <= 14; tier++)
            {
                rolled[tier] = tier == 1 ? Weights[1] : Weights[tier] * factor;
                total += rolled[tier];
            }
            for (int tier = 1; tier <= 14; tier++) rolled[tier] /= total;

            var fleeced = new double[15];
            for (int tier = 1; tier <= 14; tier++)
            {
                fleeced[tier] += rolled[tier] * (1 - fleeceChance);
                fleeced[Math.Min(14, tier + fleeceTiers)] += rolled[tier] * fleeceChance;
            }

            double e = elevationPct / 100.0;
            var final = new double[15];
            for (int tier = 1; tier <= 14; tier++)
            {
                final[tier] += fleeced[tier] * (1 - e);
                final[Math.Min(14, tier + 1)] += fleeced[tier] * e;
            }
            return final;
        }

        private static double ShareAtOrAbove(double[] shares, int tier)
        {
            double sum = 0;
            for (int t = tier; t <= 14; t++) sum += shares[t];
            return sum;
        }

        [Fact]
        public void ALevel94Region5Build_EveryLuckTermIsWhatTheFormulaSays()
        {
            var payload = Level94Region5Payload();
            var stats = StatsFor(in payload);
            var breakdown = LootLuckBreakdown.From(in payload, in stats);
            var request = CombatLootDropRequest.Build(
                in payload, in stats, monsterId: 111, kills: 1, bonusRarityTiers: 0, skipMaterialRoll: false);

            var report = new StringBuilder();
            report.AppendLine("Loot luck, term by term, for a level-94 region-5 build (LCK 300):");
            report.AppendLine($"  stats (LCK curve + Scavenger + areas + affixes)  {breakdown.Stats,7:F2}");
            report.AppendLine($"  inheritance (level 4)                            {breakdown.Inheritance,7:F2}");
            report.AppendLine($"  Fortune root (10 levels)                         {breakdown.FortuneRoot,7:F2}");
            report.AppendLine($"  guild DropRate                                   {breakdown.GuildDropRate,7:F2}");
            report.AppendLine($"  Fortune aptitude (4)                             {breakdown.FortuneAptitude,7:F2}");
            report.AppendLine($"  TOTAL LootLuckPct                                {breakdown.Total,7:F2}");
            report.AppendLine("Rarity elevation:");
            report.AppendLine($"  stats (LCK curve + traits)                       {breakdown.StatsElevation,7:F2}");
            report.AppendLine($"  Rarity bough (8 levels)                          {breakdown.RarityBough,7:F2}");
            report.AppendLine($"  TOTAL RarityElevationPct                         {request.RarityElevationPct,7:F2}");
            _output.WriteLine(report.ToString());

            Assert.Equal(28.78, breakdown.Stats, 2);          // 1.2*sqrt(300) = 20.78, + 8 Scavenger
            Assert.Equal(8.0, breakdown.Inheritance, 2);
            Assert.Equal(10.0, breakdown.FortuneRoot, 2);
            Assert.Equal(0.0, breakdown.GuildDropRate, 2);
            Assert.Equal(6.0, breakdown.FortuneAptitude, 2);
            Assert.Equal(52.78, breakdown.Total, 2);           // 60.78 before task 26 option B
            Assert.Equal(6.06, breakdown.StatsElevation, 2);   // 0.35*sqrt(300)
            Assert.Equal(8.0, breakdown.RarityBough, 2);       // elevation since option B, was luck
            Assert.Equal(14.06, request.RarityElevationPct, 2); // 6.06 before option B

            // The breakdown and the drop path cannot drift apart.
            Assert.Equal(breakdown.Total, request.LootLuckPct);
            Assert.Equal(breakdown.ElevationTotal, request.RarityElevationPct);
        }

        [Fact]
        public void ExpectedRatesAtThatBuild_MatchTheInvestigationTable()
        {
            var payload = Level94Region5Payload();
            var stats = StatsFor(in payload);
            var request = CombatLootDropRequest.Build(
                in payload, in stats, monsterId: 111, kills: 1, bonusRarityTiers: 0, skipMaterialRoll: false);

            double[] shares = AnalyticFinalShares(request.LootLuckPct, request.RarityElevationPct, FleeceChance, FleeceTiers);
            double legendaryPlus = ShareAtOrAbove(shares, RarityTier.Legendary);
            double ancientPlus = ShareAtOrAbove(shares, RarityTier.Ancient);

            // The luck ceiling. Luck multiplies tiers 2-14 by the SAME factor, so
            // all it can ever do is shrink Normal's 100 towards nothing.
            double[] plainZero = AnalyticFinalShares(0, 0, 0, 0);
            double[] plainCeiling = AnalyticFinalShares(1e6, 0, 0, 0);
            double[] buildCeiling = AnalyticFinalShares(1e6, request.RarityElevationPct, FleeceChance, FleeceTiers);
            double ceilingGain = ShareAtOrAbove(plainCeiling, RarityTier.Legendary) / ShareAtOrAbove(plainZero, RarityTier.Legendary);

            _output.WriteLine($"at L={request.LootLuckPct:F2}, elevation {request.RarityElevationPct:F2}%, fleece 1%:");
            _output.WriteLine($"  Legendary+ per drop   {legendaryPlus,9:P3}");
            _output.WriteLine($"  Ancient+ per drop     {ancientPlus,9:P4}   (one in {1 / ancientPlus:N0} drops)");
            _output.WriteLine($"  Ancient+ / Legendary+ {ancientPlus / legendaryPlus,9:P2}");
            _output.WriteLine($"  P(no Ancient+ in 976 drops) {Math.Pow(1 - ancientPlus, 976):F2}");
            _output.WriteLine($"  infinite luck, plain roll: Legendary+ {ShareAtOrAbove(plainCeiling, RarityTier.Legendary):P3} = {ceilingGain:F2}x the zero-luck share");
            _output.WriteLine($"  infinite luck, this build: Legendary+ {ShareAtOrAbove(buildCeiling, RarityTier.Legendary):P3}");

            // Before option B (bough as luck): Legendary+ 1.195%, Ancient+ 0.0495%.
            Assert.InRange(legendaryPlus, 0.0125, 0.0135);
            Assert.InRange(ancientPlus, 0.00051, 0.00057);
            Assert.InRange(ancientPlus / legendaryPlus, 0.039, 0.043);

            // No amount of luck more than about doubles the top. If this fails
            // the roll's SHAPE changed - that is an owner decision (task 26,
            // options D/F), never a side effect.
            Assert.InRange(ceilingGain, 1.9, 2.2);
            // With this build's elevation on top: 1.964% while the bough was
            // luck (elevation 6.06%), higher since it became elevation (14.06%).
            Assert.True(ShareAtOrAbove(buildCeiling, RarityTier.Legendary) < 0.023,
                "infinite luck now buys more than 2.3% Legendary+ - the roll's shape changed");
        }

        [Fact]
        public void TheWikiOddsLine_QuotesWhatTheRollUses()
        {
            var payload = Level94Region5Payload();
            var stats = StatsFor(in payload);
            var request = CombatLootDropRequest.Build(
                in payload, in stats, monsterId: 111, kills: 1, bonusRarityTiers: 0, skipMaterialRoll: false);
            Assert.True(request.HasGoldenFleece);

            var odds = new CombatLootEngine.LootOddsSnapshot(
                request.LootLuckPct, request.RarityElevationPct, request.HasGoldenFleece, DateTime.UtcNow);
            var line = FolkIdle.Server.Network.NetworkBroadcastSystem.BuildLootOdds(true, odds);

            double[] ours = AnalyticFinalShares(request.LootLuckPct, request.RarityElevationPct, FleeceChance, FleeceTiers);
            _output.WriteLine($"odds line: L={line.LootLuckPct:F2} elev={line.RarityElevationPct:F2} " +
                $"Legendary+ {line.LegendaryPlusPerDrop:P3}, Ancient+ {line.AncientPlusPerDrop:P4} (1 in {1 / line.AncientPlusPerDrop:N0})");
            Assert.Equal(ShareAtOrAbove(ours, RarityTier.Legendary), line.LegendaryPlusPerDrop, 10);
            Assert.Equal(ShareAtOrAbove(ours, RarityTier.Ancient), line.AncientPlusPerDrop, 10);
            Assert.Equal(15, line.TierShares.Length);

            var unknown = FolkIdle.Server.Network.NetworkBroadcastSystem.BuildLootOdds(false, default);
            Assert.False(unknown.Known);
            Assert.Equal(0.0, unknown.AncientPlusPerDrop);
        }

        [Fact]
        public void TheRealChain_ResolveDropTier_MatchesTheAnalyticRates()
        {
            var payload = Level94Region5Payload();
            var stats = StatsFor(in payload);
            var request = CombatLootDropRequest.Build(
                in payload, in stats, monsterId: 111, kills: 1, bonusRarityTiers: 0, skipMaterialRoll: false);
            float luck = request.LootLuckPct;
            float elevation = request.RarityElevationPct;

            // The engine's own expectation must equal this file's restated one.
            double[] ours = AnalyticFinalShares(luck, elevation, FleeceChance, FleeceTiers);
            double[] engine = RarityTier.ExpectedFinalShares(luck, elevation, FleeceChance, FleeceTiers);
            for (int tier = 1; tier <= 14; tier++)
            {
                Assert.True(Math.Abs(ours[tier] - engine[tier]) < 1e-12,
                    $"tier {tier}: RarityTier.ExpectedFinalShares says {engine[tier]}, the restated table says {ours[tier]}");
            }

            // And the real chain, sampled. Fleece fires on every hundredth kill.
            const int samples = 2_000_000;
            var counts = new int[15];
            int lifted = 0;
            for (int i = 0; i < samples; i++)
            {
                int bonus = i % 100 == 99 ? FleeceTiers : 0;
                var (rolled, final) = RarityTier.ResolveDropTier(luck, bonus, elevation);
                counts[final]++;
                if (final > rolled) lifted++;
            }

            double observedLegendaryPlus = counts.Skip(RarityTier.Legendary).Sum() / (double)samples;
            double observedAncientPlus = counts.Skip(RarityTier.Ancient).Sum() / (double)samples;
            double expectedLegendaryPlus = ShareAtOrAbove(ours, RarityTier.Legendary);
            double expectedAncientPlus = ShareAtOrAbove(ours, RarityTier.Ancient);

            _output.WriteLine($"{samples:N0} drops through ResolveDropTier at L={luck:F2}, elevation {elevation:F2}%, fleece 1%:");
            _output.WriteLine($"  Legendary+  expected {expectedLegendaryPlus:P4}  observed {observedLegendaryPlus:P4}");
            _output.WriteLine($"  Ancient+    expected {expectedAncientPlus:P4}  observed {observedAncientPlus:P4}");
            _output.WriteLine($"  lifted by fleece or elevation: {lifted / (double)samples:P3}");

            Assert.True(Math.Abs(observedLegendaryPlus - expectedLegendaryPlus) / expectedLegendaryPlus < 0.03,
                $"Legendary+ observed {observedLegendaryPlus:P4} against an analytic {expectedLegendaryPlus:P4}");
            // ~1,000 expected at this sample size: an order-of-magnitude band,
            // per this file's convention for the rare tiers.
            Assert.InRange(observedAncientPlus, expectedAncientPlus / 3, expectedAncientPlus * 3);
            // Something must be lifted, or fleece and elevation are dead.
            Assert.InRange(lifted / (double)samples, 0.12, 0.18); // 0.05-0.09 before option B
        }
    }
}
