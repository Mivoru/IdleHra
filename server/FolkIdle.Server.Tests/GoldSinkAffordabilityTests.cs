using System;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// What things cost, measured in MINUTES OF PLAY rather than in gold.
    ///
    /// Reported from play: "I earned 100,000 gold overnight and about five
    /// rerolls took all of it." That was exact, and no test could have said so,
    /// because every cost in this game was pinned against its own formula -
    /// "1000 * 1.5^tier, yes, that is 1000 * 1.5^tier" - and never against what
    /// a player can actually earn.
    ///
    /// Gold income is knowable: every monster pays MaxHp/20, so a region's rate
    /// falls straight out of the content tables. This measures each sink
    /// against that and asserts the answer in time, which is the unit a player
    /// actually feels.
    /// </summary>
    public class GoldSinkAffordabilityTests
    {
        private readonly ITestOutputHelper _output;

        public GoldSinkAffordabilityTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        /// <summary>
        /// A kill every twenty seconds against the strongest regular of the
        /// region - a player who is keeping up, not one who is grinding the
        /// first monster forever.
        /// </summary>
        /// <remarks>
        /// Modul: THIS IS THE EARLY GAME, AND ONLY THE EARLY GAME. It was called
        /// plain `KillsPerHour` until task 37 (2026-09-24) and every sink in the
        /// file was priced against it - about 369k gold an hour in region 5,
        /// while the one account at the top measured 5-14M. It still prices the
        /// early-game facts below correctly; the top of the game is
        /// <see cref="TopOfGameGoldPerHour"/>.
        /// </remarks>
        private const double EarlyGameKillsPerHour = 180.0;

        private static double GoldPerHour(int region)
        {
            int strongestRegular = ContentRegistry.FirstCanonicalMonsterId
                                   + (region - 1) * ContentRegistry.MonstersPerRegion + 3;
            return ContentRegistry.Monsters[strongestRegular - 1].BaseGoldReward * EarlyGameKillsPerHour;
        }

        private static double MinutesOfPlay(double cost, int region) => cost / GoldPerHour(region) * 60.0;

        // ------------------------------------------------------------------
        // Modul: THE TOP OF THE GAME, MEASURED RATHER THAN ASSUMED (task 37,
        // 2026-09-24).
        //
        // The brief (docs/superpowers/plans/2026-09-23-task-37-late-game-gold-
        // sink-design.md §1.2) read the one account at the top (player 8,
        // level 94, region 5) off EcoTelemetryLedgers two ways, and the two do
        // not agree:
        //
        //   - two single offline catch-up snapshots (+170.9M, +81.3M) implied
        //     5-14M/h, "working figure 10M/h";
        //   - the 22-day series 2026-09-01 -> 09-23 moved +633.8M up and at
        //     least -152.1M down: gross >= 785.9M over 528 h = 1.49M/h.
        //
        // The 180-kills model above says 369k. This profile is the model the
        // offline catch-up actually pays by - CombatDamageModel seconds per
        // kill, EconomyDecisions' per-kill base - for that account's REAL sheet
        // and REAL gear, read from production on 2026-09-24. It lands at about
        // 1.6M/h, on the 22-day figure, and six times under the 10M peak. The
        // model's absolute ceiling (the attack-speed cap, every swing lethal)
        // is about 10M/h on the Death Knight, so the peak is combat only for a
        // character that one-shots it, and this one takes four swings. The
        // telemetry cannot separate combat from chest sales (brief §1.1: about
        // 620M of sellable stock); the peak snapshots are most likely those.
        //
        // So the plan's "within 3x of 10M/h" does not hold for the real sheet,
        // and the assertion is against the long-run gross instead. The
        // Deep's prices do not read this number - the stake is a share of
        // HOLDINGS - but every "minutes of top income" figure in this file does.
        // ------------------------------------------------------------------

        /// <summary>
        /// The peak the brief used as its working figure (brief §1.2, two offline
        /// catch-up snapshots, 2026-09-18 and 2026-09-22). Kept to be asserted
        /// ABOVE what combat can pay, not as a target.
        /// </summary>
        internal const double MeasuredPeakIncomePerHour = 10_000_000.0;

        /// <summary>
        /// Gross gold income over 2026-09-01 -> 09-23 (brief §1.2 table: +633.8M
        /// and at least -152.1M, netted per 10-minute snapshot, over 22 days).
        /// A lower bound, because opposite moves inside one snapshot cancel.
        /// </summary>
        internal const double MeasuredLongRunGrossPerHour = (633_800_000.0 + 152_100_000.0) / (22.0 * 24.0);

        /// <summary>
        /// The account's codex level sum ("monster_codex_entries", player 8,
        /// 2026-09-24): 17,660 across 25 monsters, which the 2026-09-06 curve
        /// turns into 6.32x.
        /// </summary>
        private const int TopCodexLevelSum = 17_660;

        /// <summary>Inheritance damage level 4 (player_inheritance_stats, StatId 0, 2026-09-24).</summary>
        private const int TopInheritDamageLevel = 4;

        /// <summary>The strongest region-5 regular - the Death Knight, 2,050 base gold.</summary>
        private static int TopMonsterId =>
            ContentRegistry.FirstCanonicalMonsterId + (5 - 1) * ContentRegistry.MonstersPerRegion + 3;

        /// <summary>
        /// The eight combat pieces the live account wore, read from production
        /// ("EquipmentInstances" joined through characters, player 8, the
        /// character on activity 111) on 2026-09-24: BaseItemId, QualityTier
        /// and the AffixPayload verbatim. Tools are absent on purpose - slots
        /// 8-10 carry no combat stat.
        /// </summary>
        private static readonly (string BaseId, int Quality, string Affixes)[] TopAccountLoadout =
        {
            ("eq_abyssal_dark_scepter_magic_weapon_slot_base", 13, "{\"magic_dmg_pct@5\":77,\"crit_dmg_pct@5\":154,\"crit_dmg_pct#3@5\":179,\"crit_dmg_pct#2@5\":161,\"lifesteal_pct@5\":16}"),
            ("eq_dreadnought_helm_helmet_armor_slot_base", 10, "{\"crit_chance_pct@5\":26,\"crit_chance_pct#2@5\":24,\"crit_chance_pct#3@5\":23,\"crit_chance_pct#4@5\":22}"),
            ("eq_dread_carapace_chest_armor_slot_base", 10, "{\"flat_armor#2@5\":3340,\"flat_hp@5\":2304,\"flat_hp#2@5\":2092,\"flat_armor@5\":3253}"),
            ("eq_dread_tassets_leggings_armor_slot_base", 9, "{\"flat_hp@5\":2577,\"dodge_chance_pct#2@5\":22,\"dodge_chance_pct@5\":18}"),
            ("eq_doom_sabatons_boots_armor_slot_base", 10, "{\"attack_speed_pct#2@5\":35,\"flat_armor@5\":3540,\"dodge_chance_pct@5\":24,\"attack_speed_pct@5\":36}"),
            ("eq_doom_claws_gloves_armor_slot_base", 10, "{\"attack_speed_pct#4@5\":28,\"attack_speed_pct@5\":28,\"attack_speed_pct#2@5\":33,\"attack_speed_pct#3@5\":34}"),
            ("eq_doom_gorget_amulet_slot_base", 10, "{\"flat_hp@5\":2355,\"flat_hp#2@5\":2095,\"flat_armor#2@5\":2663,\"flat_armor@5\":3074}"),
            ("eq_dread_signet_ring_1/2_slot_base", 12, "{\"block_chance_pct@5\":40,\"block_chance_pct#2@5\":41,\"block_chance_pct#4@5\":47,\"block_chance_pct#3@5\":49}"),
        };

        /// <summary>
        /// Seconds a kill takes for the top reference sheet: the live account's
        /// attributes (RarityRollDistributionTests.Level94Region5Payload, the
        /// same production inputs task 26 used) wearing its real loadout,
        /// folded in by the equip path's own EquipmentSlotEngine.AddAffixTotals
        /// and scaled by the loot engine's RarityTier.PowerMultiplier.
        /// </summary>
        /// <remarks>
        /// Modul: THE FIRST CUT OF THIS WAS WRONG BY 17x, AND THAT WAS THE POINT.
        /// It dressed the sheet in ProgressionRateTests' region-5 BASE power
        /// (972 attack, no affixes) and read 10 s a kill = 0.6M/h against a
        /// measured 10M. The live account's gloves and boots carry six attack
        /// speed rolls, its helmet four crit-chance rolls and its tier-13
        /// scepter three crit-damage rolls: the affixes are most of its damage,
        /// so a floor model is not a model of the top at all.
        /// </remarks>
        internal static double TopOfGameSecondsPerKill()
        {
            var payload = RarityRollDistributionTests.Level94Region5Payload();
            payload.SelectedLineageId = 1; // the live account's lineage (PlayerRecords, 2026-09-24)

            EquippedAffixTotals totals = default;
            foreach (var (baseId, quality, affixes) in TopAccountLoadout)
            {
                Assert.True(ContentRegistry.TryGetItemDefinitionByBaseId(baseId, out var item), $"{baseId} is no longer in the catalogue");
                double power = RarityTier.PowerMultiplier(quality);
                totals.FlatAttack += (int)Math.Round(item.FlatAttackPower * power);
                totals.FlatDefense += (int)Math.Round(item.FlatDefenseRating * power);
                FolkIdle.Server.Domain.Combat.EquipmentSlotEngine.AddAffixTotals(affixes, ref totals);
            }
            payload.CachedAffixTotals = totals;

            var stats = RarityRollDistributionTests.StatsFor(in payload);
            var lineage = FolkIdle.Server.Engine.ProgressionEngine.Lineages[payload.SelectedLineageId];
            // The same four-argument call the offline catch-up makes.
            long raw = StatsCalculator.ComputeEffectiveMilliAttack(
                in stats, lineage.DamageScalePerLevelPct, payload.CurrentLevel,
                InheritanceRegistry.GetBonusPct(TopInheritDamageLevel));
            var monster = ContentRegistry.Monsters[TopMonsterId - 1];
            return CombatDamageModel.ExpectedSecondsPerKill(in stats, in monster, raw, CodexEngine.DamageMultiplierFor(TopCodexLevelSum));
        }

        /// <summary>
        /// Gold an hour at the top: kills an hour from the damage model, times
        /// the per-kill base both kill paths read (EconomyDecisions, 75%) and
        /// the sheet's own gold-acquisition percentage. No guild buff, legacy
        /// perk or inheritance - the account has none of them live.
        /// </summary>
        internal static double TopOfGameGoldPerHour()
        {
            return 3600.0 / TopOfGameSecondsPerKill() * TopGoldPerKill();
        }

        private static double TopGoldPerKill()
        {
            var payload = RarityRollDistributionTests.Level94Region5Payload();
            var stats = RarityRollDistributionTests.StatsFor(in payload);
            var monster = ContentRegistry.Monsters[TopMonsterId - 1];
            return EconomyDecisions.BaseCombatGold(monster.BaseGoldReward)
                   * (1.0 + stats.GoldAcquisitionMultiplierPct / 100.0);
        }

        private static double MinutesOfTopIncome(double cost) => cost / TopOfGameGoldPerHour() * 60.0;

        /// <summary>
        /// The profile must land within 3x of what production measured over the
        /// long run. If it does not, the MODEL is wrong - not the player - and
        /// nothing may be priced off it until the reason is found.
        /// </summary>
        [Fact]
        public void TheTopOfGameIncomeProfileMatchesWhatProductionMeasured()
        {
            double secondsPerKill = TopOfGameSecondsPerKill();
            double perHour = TopOfGameGoldPerHour();
            _output.WriteLine(
                $"top of game: {secondsPerKill:F2} s/kill on the Death Knight = {3600.0 / secondsPerKill:N0} kills/h = {perHour:N0} gold/h");
            _output.WriteLine(
                $"  measured: long-run gross {MeasuredLongRunGrossPerHour:N0}/h, peak snapshots {MeasuredPeakIncomePerHour:N0}/h; early-game model {GoldPerHour(5):N0}/h");

            Assert.InRange(perHour, MeasuredLongRunGrossPerHour / 3.0, MeasuredLongRunGrossPerHour * 3.0);
        }

        /// <summary>
        /// Modul: the brief's 10M/h peak is NOT this account's combat, and this
        /// says so in a way that fails if the model ever moves enough to make it
        /// so. The absolute ceiling - one lethal swing per interval at the
        /// attack-speed cap, since the swing timer restarts on every spawn - is
        /// printed beside it: about 10M/h on the Death Knight, reachable only by
        /// a character that one-shots a 69,700 HP monster, which this one (four
        /// swings a kill) is nowhere near.
        /// </summary>
        [Fact]
        public void TheTopAccountsCombatIsNotThePeakTheBriefWorkedFrom()
        {
            int fastestIntervalMs = (int)Math.Round(1500.0 * (1.0 - CombatDamageModel.MaxAttackSpeedReduction));
            double ceiling = 3600.0 / (fastestIntervalMs / 1000.0) * TopGoldPerKill();
            double perHour = TopOfGameGoldPerHour();

            _output.WriteLine($"the account: {perHour:N0} gold/h; combat ceiling on the Death Knight: {ceiling:N0} gold/h (a lethal swing every {fastestIntervalMs} ms)");
            Assert.True(perHour < MeasuredPeakIncomePerHour / 3.0,
                $"the top sheet now earns {perHour:N0}/h from combat, within 3x of the 10M/h peak - re-read brief §1.2 and restate this file's anchor");
        }

        private readonly record struct SinkRow(string Name, double Cost, bool Repeatable, double MinMinutes, double MaxMinutes);

        /// <summary>The rows of the table, in gold, each with the band its minutes must sit in.</summary>
        private static SinkRow[] TopOfGameSinks() => new[]
        {
            // Repeatable, and meant to be cheap per click: volume is the sink.
            new SinkRow("affix reroll (r5)", AffixRegistry.CalculateRerollGoldCost(5, 0, false), true, 0.0, 1.0),
            // The fusion fee at the last fusable tier (13 -> 14). Formula restated
            // from ForgeSplicingEngine, as AFusionFeeIsSmallerThan... does.
            new SinkRow("fusion (into tier 14)", Math.Ceiling(200.0 * Math.Pow(1.35, 13)), true, 0.0, 1.0),
            // One-off per building level.
            new SinkRow("village level 20", FolkIdle.Server.Domain.Progression.VillageManagementEngine.CalculateUpgradeCost(20), false, 5.0, 60.0),
            // Modul: the feast climbs with use, but it is NOT a repeatable sink:
            // the Inn's population cap bounds how many recruits a season can
            // take, so it cannot absorb income for ever. Counted as one-off.
            new SinkRow("feast n=16", VillagerArrivalRules.RecruitCostGold(16), false, 60.0, 600.0),
            new SinkRow("feast n=20", VillagerArrivalRules.RecruitCostGold(20), false, 300.0, 3_000.0),
            new SinkRow("feast n=25", VillagerArrivalRules.RecruitCostGold(25), false, 3_000.0, 30_000.0),
            new SinkRow("Delve gate (r5)", DelveRegistry.EntryFeeForRegion(5), true, 2.0, 30.0),
            // Modul: THE DEEP (task 37 phase 1). A descent's first toll is the
            // stake, a share of HOLDINGS - so it is priced for the account that
            // holds the hoard, and repeats every run. At the top account's 492M
            // it is 2.46M, about an hour and a half of top income.
            new SinkRow("Deep descent (492M held)", DelveRegistry.Stake(DelveRegistry.EntryFeeForRegion(5), TopAccountHeld), true, 30.0, 600.0),
        };

        /// <summary>What the top account held on 2026-09-23 (brief §1.1), which the spec's worked prices use.</summary>
        internal const long TopAccountHeld = 492_000_000;

        /// <summary>
        /// Stake + tolls from floor 9 down to <paramref name="deepestFloor"/>,
        /// plus <paramref name="refills"/> lanterns - one push, as spec §3.3
        /// prices it.
        /// </summary>
        private static double DeepPushCost(long stake, int deepestFloor, int refills)
        {
            double total = 0;
            for (int floor = DelveRegistry.FirstDeepFloor; floor <= deepestFloor; floor++) total += DelveRegistry.TollForFloor(stake, floor);
            for (int k = 0; k < refills; k++) total += DelveRegistry.LanternRefillPrice(stake, k);
            return total;
        }

        /// <summary>
        /// Spec §3.3's worked prices and §6's affordability bands, asserted.
        ///
        /// Modul: THE BANDS ARE THE SPEC'S, STATED AT ITS 10M/h, and they hold
        /// there. At the honest top-of-game profile (about 1.67M/h, see above)
        /// the same pushes are six times as many hours - printed beside them -
        /// because the Deep's price is a share of HOLDINGS, not of income: it
        /// is sized to drain a hoard, and at the top account's hoard it does.
        /// Which income to state the bands against is for the owner; the gold
        /// figures are asserted either way.
        /// </summary>
        [Fact]
        public void TheDeepsWorkedPricesAreTheSpecs()
        {
            long stake = DelveRegistry.Stake(DelveRegistry.EntryFeeForRegion(5), TopAccountHeld);
            Assert.Equal(2_460_000, stake);

            foreach (var (floor, spec) in new[] { (12, 31_400_000.0), (16, 66_000_000.0), (20, 150_000_000.0) })
            {
                double cost = DeepPushCost(stake, floor, refills: 3);
                _output.WriteLine(
                    $"to floor {floor} + 3 refills: {cost:N0}g = {cost / MeasuredPeakIncomePerHour:F1} h at 10M/h, " +
                    $"{cost / TopOfGameGoldPerHour():F1} h at the profile's {TopOfGameGoldPerHour():N0}/h");
                Assert.InRange(cost, spec * 0.97, spec * 1.03);
            }

            Assert.InRange(DeepPushCost(stake, 12, 3) / MeasuredPeakIncomePerHour, 0.5, 4.0);
            Assert.InRange(DeepPushCost(stake, 20, 3) / MeasuredPeakIncomePerHour, 8.0, 24.0);

            // A small holder pays the region floor, and floor 12 is out of reach without income.
            long small = DelveRegistry.Stake(DelveRegistry.EntryFeeForRegion(5), 1_000_000);
            Assert.Equal(DelveRegistry.EntryFeeForRegion(5), small);
            Assert.True(DeepPushCost(small, 12, 0) > 1_000_000);
        }

        /// <summary>
        /// Every gold sink, priced in MINUTES OF TOP INCOME, printed and
        /// asserted per row - a number a test prints is not a number it checks.
        /// </summary>
        [Fact]
        public void TheGoldSinkTableAtTheTop()
        {
            _output.WriteLine($"top income {TopOfGameGoldPerHour():N0} gold/h");

            foreach (var row in TopOfGameSinks())
            {
                double minutes = MinutesOfTopIncome(row.Cost);
                _output.WriteLine($"  {row.Name,-24} {row.Cost,16:N0}g = {minutes,9:F2} min of top income{(row.Repeatable ? "  (repeatable)" : "")}");
                Assert.InRange(minutes, row.MinMinutes, row.MaxMinutes);
            }
        }

        /// <summary>
        /// THE ASSERTION TASK 37 EXISTS FOR: at least one REPEATABLE sink must
        /// absorb 30% or more of an hour of top income.
        ///
        /// Modul: on 2026-09-24 none did - the dearest repeatable price at the
        /// top was the Delve's 250k gate, a few percent of an hour - which is
        /// the owner's "earning 100M is easy" stated as a number. The Deep
        /// (task 37 phase 1) turned it green: a descent's stake is a share of
        /// holdings, and it repeats every run.
        /// </summary>
        [Fact]
        public void ARepeatableSinkAbsorbsAThirdOfAnHourAtTheTop()
        {
            double best = 0;
            string bestName = "none";
            foreach (var row in TopOfGameSinks())
            {
                if (!row.Repeatable) continue;
                double share = MinutesOfTopIncome(row.Cost) / 60.0;
                if (share > best) { best = share; bestName = row.Name; }
            }

            _output.WriteLine($"dearest repeatable sink: {bestName}, {best:P1} of an hour");
            Assert.True(best >= 0.30,
                $"the dearest repeatable sink ({bestName}) absorbs {best:P1} of an hour of top income; at least 30% is required");
        }

        /// <summary>
        /// THE DELVE, measured against the income that pays for it.
        ///
        /// Modul: this is the assertion the whole feature is built on. Every
        /// recurring sink in this file costs under 3% of an hour at the top of
        /// the game - a reroll 2.7%, a fusion fee 1.1% - which is why gold
        /// stopped being a currency there. The Delve is priced at roughly forty
        /// minutes of the player's OWN region, so it is the same weight for a
        /// new player and a finished one, and it is the first sink whose share
        /// does not collapse as income grows.
        ///
        /// Asserted, not printed. A number a test prints is not a number a test
        /// checks, and this file exists because that distinction was learned
        /// the expensive way.
        /// </summary>
        [Fact]
        public void TheDelveCostsAboutAnEveningPerRegion()
        {
            double previousShare = -1;

            for (int region = 1; region <= 5; region++)
            {
                long fee = DelveRegistry.EntryFeeForRegion(region);
                double minutes = MinutesOfPlay(fee, region);
                double share = fee / GoldPerHour(region);

                _output.WriteLine($"region {region} Delve: {fee:N0}g = {minutes:F0} min of region-{region} play ({share:P0} of an hour)");

                // Long enough to be felt, short enough that a run is an
                // evening's decision rather than a week's saving.
                Assert.InRange(minutes, 25.0, 55.0);

                // Modul: THE SHARE MUST NOT COLLAPSE. That is the single defect
                // every other sink in this file has - each one is a flat price
                // against geometric income, so it is punishing in region 2 and
                // decoration in region 5. Holding the share roughly level
                // across regions is the whole point of a per-region fee table,
                // and a hand-edited entry that broke it would otherwise be
                // invisible until a player noticed.
                if (previousShare >= 0)
                {
                    Assert.InRange(share / previousShare, 0.75, 1.35);
                }
                previousShare = share;
            }

            // And the top of the game specifically: the feast is the only
            // existing sink with any weight there, and it is one-off per
            // villager. This one repeats.
            Assert.True(DelveRegistry.EntryFeeForRegion(5) > DelveRegistry.EntryFeeForRegion(1) * 20,
                "the fee has to grow with income, or it becomes decoration exactly where it is needed");
        }

        [Fact]
        public void ARerollIsMinutesOfPlay()
        {
            // Modul: CalculateRerollGoldCost takes a REGION tier, 1-5, not the
            // fourteen-step item rarity. This read 8 and 14 - "item rarity 8",
            // per the comment it carried - and no region is either, so both
            // calls fell through the cost table's default arm and priced
            // everything at the same flat 10,000. That made a region-2 reroll
            // read as 23.5 minutes of region-2 play and left this guard failing
            // for three weeks while the live game charged 2,000 there, which is
            // what the engine resolves from the item's own RegionTier.
            //
            // The gear that can be rerolled is RegionTier 1-5 in items.json, so
            // an item and the region it drops in are the same number here.
            long cost = AffixRegistry.CalculateRerollGoldCost(2, consecutiveAttempts: 0, rerollStatType: false);
            double minutes = MinutesOfPlay(cost, region: 2);

            _output.WriteLine($"region-2 reroll: {cost:N0}g = {minutes:F1} min of region-2 play");
            Assert.InRange(minutes, 0.1, 10.0);

            // And the top of the ladder, against the income of the region that
            // produces it. A hundred-attempt chase for a Legendary affix has to
            // stay inside an evening, not a month.
            long topCost = AffixRegistry.CalculateRerollGoldCost(5, 0, false);
            double topChaseHours = topCost * 100.0 / GoldPerHour(5);
            _output.WriteLine($"region-5 reroll: {topCost:N0}g, 100 attempts = {topChaseHours:F1} h of region-5 play");
            Assert.InRange(topChaseHours, 0.2, 8.0);
        }

        /// <summary>
        /// The three items are what a fusion costs. The gold is a fee on top,
        /// and a fee that outweighs the thing it is attached to is a second
        /// gate wearing a fee's clothes.
        /// </summary>
        [Fact]
        public void AFusionFeeIsSmallerThanAssemblingTheItemsItConsumes()
        {
            for (int tier = 1; tier <= 10; tier++)
            {
                double fee = Math.Ceiling(200.0 * Math.Pow(1.35, tier));
                double minutes = MinutesOfPlay(fee, region: 2);
                _output.WriteLine($"fusion at tier {tier}: {fee:N0}g = {minutes:F1} min");
                Assert.InRange(minutes, 0.05, 20.0);
            }
        }

        /// <summary>
        /// A village level is a long-term investment and is allowed to be the
        /// dearest thing here - but a single level of a single building should
        /// not be most of an evening.
        /// </summary>
        [Fact]
        public void AVillageLevelIsAnInvestmentNotAnEvening()
        {
            for (int level = 0; level <= 10; level++)
            {
                double minutes = MinutesOfPlay(
                    FolkIdle.Server.Domain.Progression.VillageManagementEngine.CalculateUpgradeCost(level),
                    region: 2);
                _output.WriteLine($"village level {level} -> {level + 1}: {minutes:F1} min");
                Assert.InRange(minutes, 0.5, 90.0);
            }
        }

        /// <summary>
        /// The rates the assertions above are measured against, printed every
        /// run. A number that moves silently is how the reroll curve got to
        /// fifty-three minutes a roll without anyone noticing.
        /// </summary>
        [Fact]
        public void TheMeasuredIncomeIsPrinted()
        {
            for (int region = 1; region <= 5; region++)
            {
                _output.WriteLine($"region {region}: {GoldPerHour(region):N0} gold/hour");
                Assert.True(GoldPerHour(region) > 0);
            }

            // Income has to RISE across the game, or a later region is a pay cut.
            for (int region = 2; region <= 5; region++)
            {
                Assert.True(
                    GoldPerHour(region) > GoldPerHour(region - 1),
                    $"region {region} pays less than region {region - 1}");
            }
        }
    }
}
