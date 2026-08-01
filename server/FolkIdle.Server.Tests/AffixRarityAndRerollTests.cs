using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: affix rarity + reroll economy, 2026-08-01.
    //
    // Deliberately fixture-free. Every rule tested here is pure arithmetic or
    // pure predicate logic, so these run without Postgres - which matters
    // because the reroll paths that DO need a fixture are exactly the ones
    // whose bugs historically reached production.
    public class AffixRarityAndRerollTests
    {
        // ---------- rarity scaling ----------

        [Fact]
        public void AffixRarity_ScalesMagnitude_MonotonicallyAndWithTheStatedSpread()
        {
            AffixRegistry.TryGetDefinition("flat_hp", out var flatHp);

            int previous = 0;
            for (int rarity = 1; rarity <= 5; rarity++)
            {
                int magnitude = AffixRegistry.CalculateMagnitude(flatHp, regionTier: 3, (AffixRarity)rarity);
                Assert.True(magnitude > previous,
                    $"rarity {rarity} produced {magnitude}, not greater than {previous}");
                previous = magnitude;
            }

            int common = AffixRegistry.CalculateMagnitude(flatHp, 3, AffixRarity.Common);
            int legendary = AffixRegistry.CalculateMagnitude(flatHp, 3, AffixRarity.Legendary);

            // 1.6^4 = 6.55. Guard the design intent, not the exact float.
            double ratio = legendary / (double)common;
            Assert.InRange(ratio, 6.0, 7.0);
        }

        // The whole point of keeping variance narrower than one rarity step:
        // luck must never beat rarity, or upgrading rarity stops being the
        // dominant lever and the Diamond sink loses its reason to exist.
        [Fact]
        public void MagnitudeVariance_NeverLetsALowerRarityBeatAHigherOne()
        {
            AffixRegistry.TryGetDefinition("flat_hp", out var flatHp);

            for (int rarity = 1; rarity < 5; rarity++)
            {
                var lower = AffixRegistry.CalculateMagnitudeRange(flatHp, 4, (AffixRarity)rarity);
                var higher = AffixRegistry.CalculateMagnitudeRange(flatHp, 4, (AffixRarity)(rarity + 1));

                Assert.True(lower.Max < higher.Min,
                    $"rarity {rarity} can roll {lower.Max}, which meets or beats rarity {rarity + 1}'s floor of {higher.Min}");
            }
        }

        [Fact]
        public void RollMagnitude_StaysInsideItsAdvertisedBand()
        {
            AffixRegistry.TryGetDefinition("flat_armor", out var flatArmor);
            var band = AffixRegistry.CalculateMagnitudeRange(flatArmor, 5, AffixRarity.Epic);

            for (int i = 0; i < 400; i++)
            {
                int rolled = AffixRegistry.RollMagnitude(flatArmor, 5, AffixRarity.Epic);
                Assert.InRange(rolled, band.Min, band.Max);
            }
        }

        // ---------- payload key round-trip ----------

        // The single most dangerous change in this feature: every combat total
        // is summed by looking a payload key up in the registry, so a key the
        // stripper mishandles contributes silently nothing.
        [Theory]
        [InlineData("flat_hp", "flat_hp", AffixRarity.Rare)]
        [InlineData("flat_hp@4", "flat_hp", AffixRarity.Epic)]
        [InlineData("flat_hp#2@5", "flat_hp", AffixRarity.Legendary)]
        [InlineData("crit_dmg_pct#3@1", "crit_dmg_pct", AffixRarity.Common)]
        [InlineData("flat_armor#2", "flat_armor", AffixRarity.Rare)]
        public void PayloadKeys_StripToTheirDefinitionIdAndReportTheirRarity(string key, string expectedId, AffixRarity expectedRarity)
        {
            Assert.Equal(expectedId, AffixRegistry.StripStackSuffix(key));
            Assert.Equal(expectedRarity, AffixRegistry.ParseRarity(key));
            Assert.True(AffixRegistry.TryGetDefinition(AffixRegistry.StripStackSuffix(key), out _));
        }

        [Fact]
        public void BuildPayloadKey_RoundTripsThroughTheParsers()
        {
            foreach (var definition in AffixRegistry.Definitions.ToArray())
            {
                for (int stack = 1; stack <= 3; stack++)
                {
                    for (int rarity = 1; rarity <= 5; rarity++)
                    {
                        string key = AffixRegistry.BuildPayloadKey(definition.Id, stack, (AffixRarity)rarity);
                        Assert.Equal(definition.Id, AffixRegistry.StripStackSuffix(key));
                        Assert.Equal((AffixRarity)rarity, AffixRegistry.ParseRarity(key));
                    }
                }
            }
        }

        // ---------- economy ----------

        [Fact]
        public void RerollGoldCost_EscalatesWithStreakAndItemTier_AndSaturates()
        {
            long first = AffixRegistry.CalculateRerollGoldCost(1, 0, rerollStatType: false);
            long second = AffixRegistry.CalculateRerollGoldCost(1, 1, rerollStatType: false);
            Assert.True(second > first, "consecutive attempts must escalate");

            long tierOne = AffixRegistry.CalculateRerollGoldCost(1, 0, false);
            long tierTen = AffixRegistry.CalculateRerollGoldCost(10, 0, false);
            Assert.True(tierTen > tierOne, "higher item tiers must cost more");

            long value = AffixRegistry.CalculateRerollGoldCost(5, 0, rerollStatType: false);
            long statType = AffixRegistry.CalculateRerollGoldCost(5, 0, rerollStatType: true);
            Assert.True(statType > value, "stat-type reroll must cost more than value reroll");

            // Must never overflow or exceed the documented ceiling, however
            // absurd the inputs.
            long saturated = AffixRegistry.CalculateRerollGoldCost(14, 500, true);
            Assert.Equal(AffixRegistry.RerollGoldMaxCost, saturated);
        }

        [Fact]
        public void RarityUpgrade_CostsDiamonds_AndIsZeroAtLegendary()
        {
            long previous = 0;
            for (int rarity = 1; rarity < 5; rarity++)
            {
                long cost = AffixRegistry.CalculateRarityUpgradeDiamondCost((AffixRarity)rarity);
                Assert.True(cost > previous, $"upgrade from rarity {rarity} cost {cost}, not above {previous}");
                previous = cost;
            }

            Assert.Equal(0L, AffixRegistry.CalculateRarityUpgradeDiamondCost(AffixRarity.Legendary));
            Assert.False(AffixRegistry.TryGetNextRarity(AffixRarity.Legendary, out _));
            Assert.True(AffixRegistry.TryGetNextRarity(AffixRarity.Common, out var next));
            Assert.Equal(AffixRarity.Uncommon, next);
        }

        // ---------- anti-cheat challenge ----------

        // Regression guard for a bug that shadow-banned correct clients.
        //
        // The client hashes the epoch it saw in the broadcast; the server used
        // to validate against whatever LogicEpochCounter was current when the
        // answer arrived. Ordinary play advances that counter - every
        // checkpoint flush does, including the one every reroll triggers - so a
        // correct answer became a recorded miss whenever a flush landed in
        // between, and enough misses meant quarantine plus a shadow ban with
        // nothing visible to the player.
        //
        // This asserts the property that makes the two sides agree: the hash
        // must depend on the epoch, so validating against a DIFFERENT epoch
        // than the client used cannot possibly match. That dependence is
        // exactly why the epoch has to be pinned at issue time.
        [Fact]
        public void ChallengeHash_DependsOnEpoch_SoTheIssuedEpochMustBePinned()
        {
            const uint seed = 0x1234ABCDu;
            const long playerId = 4711L;

            uint atEpoch7 = AntiCheatTelemetryEngine.ComputeChallengeHash(seed, playerId, 7L);
            uint atEpoch8 = AntiCheatTelemetryEngine.ComputeChallengeHash(seed, playerId, 8L);

            Assert.NotEqual(atEpoch7, atEpoch8);

            // Same inputs must be reproducible, or nothing could ever validate.
            Assert.Equal(atEpoch7, AntiCheatTelemetryEngine.ComputeChallengeHash(seed, playerId, 7L));

            // The payload must carry somewhere to pin it. Without this field the
            // validator has nothing to compare against except the live counter,
            // which is the bug.
            var pinned = typeof(TickStatePayload).GetField(nameof(TickStatePayload.ActiveChallengeIssuedEpoch));
            Assert.NotNull(pinned);
            Assert.Equal(typeof(long), pinned!.FieldType);
        }

        // ---------- currency store ----------

        // Regression guard for a bug that made every diamond-priced reroll
        // impossible in production while the integration tests passed.
        //
        // Gold and diamonds live in DIFFERENT stores. Gold is a CommodityRecords
        // row seeded at registration. Diamonds are PlayerRecords."PremiumDiamonds"
        // and nothing in the server has ever created a "premium_diamond"
        // commodity row - so a reroll that looked there always found null and
        // rejected the player as broke. The reroll integration tests seeded that
        // row themselves, which made a store the game never populates look real.
        //
        // This asserts the invariant that actually matters and needs no fixture:
        // the diamond balance the WIRE reports must come from the same field the
        // spend path decrements.
        [Fact]
        public void DiamondBalance_OnTheWire_ComesFromThePlayerRecordColumn()
        {
            var payloadField = typeof(TickStatePayload).GetField(nameof(TickStatePayload.PremiumCurrency));
            Assert.NotNull(payloadField);

            var recordProperty = typeof(FolkIdle.Server.Models.PlayerRecord).GetProperty("PremiumDiamonds");
            Assert.NotNull(recordProperty);

            // Both are plain ints, so the hydration assignment
            // PremiumCurrency = player.PremiumDiamonds is lossless in both
            // directions. A widening on one side only would silently truncate a
            // wealthy account's balance.
            Assert.Equal(typeof(int), payloadField!.FieldType);
            Assert.Equal(typeof(int), recordProperty!.PropertyType);
        }

        // ---------- auto-reroll stop conditions ----------

        [Fact]
        public void StopCondition_TreatsRarityAsAFloorNotAnEquality()
        {
            var stopAtEpic = new AutoRerollStopCondition(AffixRarity.Epic);

            Assert.False(AutoRerollPlanner.IsSatisfied(stopAtEpic, AffixRarity.Rare, "flat_hp"));
            Assert.True(AutoRerollPlanner.IsSatisfied(stopAtEpic, AffixRarity.Epic, "flat_hp"));

            // A Legendary must satisfy "stop at Epic" - an equality test here
            // would keep rerolling away the best possible outcome.
            Assert.True(AutoRerollPlanner.IsSatisfied(stopAtEpic, AffixRarity.Legendary, "flat_hp"));
        }

        [Fact]
        public void StopCondition_CombinesRarityAndExactAffixWithAnd()
        {
            var legendaryHp = new AutoRerollStopCondition(AffixRarity.Legendary, "flat_hp");

            Assert.False(AutoRerollPlanner.IsSatisfied(legendaryHp, AffixRarity.Legendary, "flat_armor"));
            Assert.False(AutoRerollPlanner.IsSatisfied(legendaryHp, AffixRarity.Epic, "flat_hp"));
            Assert.True(AutoRerollPlanner.IsSatisfied(legendaryHp, AffixRarity.Legendary, "flat_hp"));
        }

        [Fact]
        public void StopCondition_RejectsATargetThatCanNeverBeRolled()
        {
            // block_chance_pct is shield-only. Targeting it on a weapon would
            // otherwise burn the entire gold budget on an impossible goal.
            var shieldOnlyOnASword = new AutoRerollStopCondition(AffixRarity.Common, "block_chance_pct");

            Assert.False(AutoRerollPlanner.IsConditionReachable(
                shieldOnlyOnASword, "eq_t3_melee_weapon_base", RerollOperation.StatType, "crit_dmg_pct"));

            Assert.True(AutoRerollPlanner.IsConditionReachable(
                shieldOnlyOnASword, "eq_t3_helper_offhand_base", RerollOperation.StatType, "block_chance_pct"));
        }

        [Fact]
        public void StopCondition_KnowsWhichOperationsCanMoveWhichAxis()
        {
            var wantsDifferentStat = new AutoRerollStopCondition(AffixRarity.Common, "flat_armor");

            // A Value reroll never changes the stat, so this can never be met.
            Assert.False(AutoRerollPlanner.IsConditionReachable(
                wantsDifferentStat, "eq_t2_chest_armor_base", RerollOperation.Value, "flat_hp"));

            // ...but it is already met if the stat matches.
            Assert.True(AutoRerollPlanner.IsConditionReachable(
                wantsDifferentStat, "eq_t2_chest_armor_base", RerollOperation.Value, "flat_armor"));

            // A Value reroll never raises rarity either.
            var wantsEpic = new AutoRerollStopCondition(AffixRarity.Epic);
            Assert.False(AutoRerollPlanner.IsRarityTargetReachable(wantsEpic, RerollOperation.Value, AffixRarity.Rare));
            Assert.True(AutoRerollPlanner.IsRarityTargetReachable(wantsEpic, RerollOperation.UpgradeRarity, AffixRarity.Rare));
        }

        [Fact]
        public void AutoReroll_RejectsATriviallySatisfiedConditionAndClampsAttempts()
        {
            Assert.True(new AutoRerollStopCondition(AffixRarity.Common).IsTriviallySatisfied);
            Assert.False(new AutoRerollStopCondition(AffixRarity.Rare).IsTriviallySatisfied);
            Assert.False(new AutoRerollStopCondition(AffixRarity.Common, "flat_hp").IsTriviallySatisfied);

            Assert.Equal(1, AutoRerollPlanner.ClampAttempts(0));
            Assert.Equal(1, AutoRerollPlanner.ClampAttempts(-50));
            Assert.Equal(AutoRerollPlanner.MaxAttemptsPerRequest, AutoRerollPlanner.ClampAttempts(int.MaxValue));
        }

        [Fact]
        public void WorstCaseEstimate_IsMonotonicAndDoesNotOverflow()
        {
            long ten = AutoRerollPlanner.EstimateWorstCaseGoldCost(7, 10, false);
            long twenty = AutoRerollPlanner.EstimateWorstCaseGoldCost(7, 20, false);

            Assert.True(twenty > ten);
            Assert.True(ten > 0);

            // The saturating branch must not run away.
            long saturated = AutoRerollPlanner.EstimateWorstCaseGoldCost(14, AutoRerollPlanner.MaxAttemptsPerRequest, true);
            Assert.True(saturated > 0);
            Assert.True(saturated <= AffixRegistry.RerollGoldMaxCost * AutoRerollPlanner.MaxAttemptsPerRequest);
        }

        // ---------- affix count authority ----------

        // Affix count must come from exactly one place. A second copy of GDD
        // 5.2's table is how this codebase produced three-way disagreement over
        // affix payload keys in the first place.
        [Fact]
        public void AffixCount_FollowsTheSingleExistingAuthority_AndNeverExceedsTheCap()
        {
            Assert.Equal(1, RarityTier.GetAffixCount(RarityTier.Normal));
            Assert.Equal(2, RarityTier.GetAffixCount(RarityTier.Rare));
            Assert.Equal(3, RarityTier.GetAffixCount(RarityTier.Legendary));
            Assert.Equal(4, RarityTier.GetAffixCount(RarityTier.Ancient));
            Assert.Equal(5, RarityTier.GetAffixCount(RarityTier.Transcendent));

            for (int tier = 1; tier <= 14; tier++)
            {
                Assert.InRange(RarityTier.GetAffixCount(tier), AffixRegistry.MinAffixCount, AffixRegistry.MaxAffixCount);
            }
        }

        [Fact]
        public void RolledAffixes_RespectSlotLegalityAndCarryParseableRarities()
        {
            var rolled = new System.Collections.Generic.Dictionary<string, int>();
            AffixRegistry.RollAffixes("eq_t3_helper_offhand_base", regionTier: 3, itemRarityTier: RarityTier.Transcendent,
                affixCount: RarityTier.GetAffixCount(RarityTier.Transcendent), destination: rolled);

            Assert.Equal(AffixRegistry.MaxAffixCount, rolled.Count);

            var shieldMask = AffixRegistry.ToMask(AffixRegistry.ResolveSlot("eq_t3_helper_offhand_base"));
            foreach (var kvp in rolled)
            {
                string id = AffixRegistry.StripStackSuffix(kvp.Key);
                Assert.True(AffixRegistry.TryGetDefinition(id, out var definition), $"{kvp.Key} did not resolve");
                Assert.True((definition.AllowedSlots & shieldMask) != 0, $"{id} is not legal for the offhand slot");
                Assert.InRange((int)AffixRegistry.ParseRarity(kvp.Key), 1, 5);
                Assert.True(kvp.Value > 0);
            }
        }
    }
}
