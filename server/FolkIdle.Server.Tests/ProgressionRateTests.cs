using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// How long an hour of combat actually buys.
    ///
    /// The report was "level 127, 2,091,564 gold, 7,790 kills after about an
    /// hour on region 1", and the handoff's reading of it was that something
    /// other than the kill reward must be paying out, because 7,790 Field Mice
    /// at 16 XP is a eighty-seventh of the XP that level needs.
    ///
    /// That reading is checkable and it is wrong. Every monster in the game
    /// pays XP = MaxHp/5 and gold = MaxHp/20 exactly, so kill rewards fix the
    /// XP-to-gold ratio at 4:1 no matter which monsters were killed - and
    /// 10.9M XP against 2.09M gold is 5.2:1, which is 4:1 plus the ordinary XP
    /// multipliers. The rewards are consistent with the tables. What is not
    /// consistent is 7,790 kills producing 2.09M gold: that is 268 gold a kill,
    /// and nothing in region 1 pays more than 4.
    ///
    /// So the player was not on region 1 for the hour. They killed their way
    /// out of it, and the question is how fast that happened - which is a
    /// question about DPS against monster HP, not about reward arithmetic.
    /// These tests measure it, headlessly, at the real tick rate.
    /// </summary>
    public class ProgressionRateTests
    {
        private readonly ITestOutputHelper _output;

        public ProgressionRateTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
            ActiveSkillEngine.Initialize();
        }

        private const int TicksPerSecond = 10;

        private static int FirstFoodItemId()
        {
            foreach (int id in ContentRegistry.RawFishItemIds)
            {
                return id;
            }

            throw new InvalidOperationException("no food in the catalogue to stock the larder with");
        }

        private sealed class SimResult
        {
            public int Level;
            public long Gold;
            public long Kills;
            public long Seconds;
            public Dictionary<int, long> KillsByMonster = new();
        }

        /// <summary>
        /// A brand-new character, exactly as the account creation path leaves
        /// one, parked on a monster for a given number of simulated seconds.
        ///
        /// Auto-advance mirrors what a player does rather than what the server
        /// does: when the current monster stops being worth fighting, they move
        /// up. Without it the simulation would measure a wall the player never
        /// actually hits.
        /// </summary>
        private static TickStatePayload FreshPayload(int startMonsterId)
        {
            var payload = new TickStatePayload
            {
                PlayerId = 1,
                CurrentLevel = 1,
                CurrentXp = 0,
                SelectedLineageId = 1, // Warrior - the damage lineage, so this is the fast case
                Slot1_CharacterId = Guid.NewGuid(),
                ActiveActivityId = startMonsterId,
                CurrentMonsterId = startMonsterId,
                CurrentMonsterHp = (long)ContentRegistry.GetScaledMonsterMaxHp(startMonsterId) * 1000L,
                InventorySpaceRemaining = int.MaxValue,
                // MILLI-hp, like CurrentMonsterHp and like the engine's own
                // baseMilliHp of 100000. Seeding this as 100 gives a character
                // with a tenth of one hit point, who dies to the first swing,
                // which clears ActiveActivityId and makes the whole hour
                // measure nothing.
                PlayerHp = 100_000,
                TownHallLevel = 1,
                // Enough food that starvation never becomes the thing being
                // measured - this test is about how fast monsters die, not about
                // the larder. Auto-eat needs all three: a stocked count, a real
                // food ITEM (the heal comes from FoodRegistry, not from the
                // count), and a threshold to fire at. With any one missing the
                // character dies in about thirty seconds, combat clears itself,
                // and an hour measures one kill.
                Food1_ItemId = FirstFoodItemId(),
                Food1_Count = 1_000_000,
                AutoEatThreshold = 50,
            };
            payload.SetGold(0);
            return payload;
        }

        private SimResult Simulate(int startMonsterId, int seconds, bool autoAdvance)
        {
            var payload = FreshPayload(startMonsterId);

            var queue = new ConcurrentQueue<GuildWarPointEvent>();
            var contexts = new ConcurrentDictionary<long, LiveSessionContext>();

            var result = new SimResult { Seconds = seconds };
            int lastMonsterId = startMonsterId;

            for (int tick = 0; tick < seconds * TicksPerSecond; tick++)
            {
                long hpBefore = payload.CurrentMonsterHp;
                int idBefore = payload.CurrentMonsterId;

                SimulationEngine.ProcessSubTick(ref payload, 100, 100, queue, contexts);

                // A kill is the only thing that refills the bar.
                if (payload.CurrentMonsterHp > hpBefore && idBefore > 0)
                {
                    result.Kills++;
                    result.KillsByMonster.TryGetValue(idBefore, out long prior);
                    result.KillsByMonster[idBefore] = prior + 1;
                }

                // Drain the queues this path feeds, so a long run does not grow
                // unbounded memory inside the test host.
                while (queue.TryDequeue(out _)) { }
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
                while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }

                if (autoAdvance)
                {
                    int next = NextTargetForLevel(payload.CurrentLevel);
                    if (next != lastMonsterId)
                    {
                        payload.ActiveActivityId = next;
                        payload.CurrentMonsterId = next;
                        payload.CurrentMonsterHp = (long)ContentRegistry.GetScaledMonsterMaxHp(next) * 1000L;
                        payload.CombatTargetTickAccumulator = 0;
                        lastMonsterId = next;
                    }
                }
            }

            result.Level = payload.CurrentLevel;
            result.Gold = payload.CurrentGold;
            return result;
        }

        /// <summary>
        /// The design's own pacing: twenty levels a region, five regions, the
        /// fifth running to 100 and beyond. A player follows the content.
        /// </summary>
        private static int NextTargetForLevel(int level)
        {
            int region = Math.Clamp((level - 1) / 20 + 1, 1, 5);
            int within = Math.Clamp((level - 1) % 20 / 5, 0, 3);
            return ContentRegistry.FirstCanonicalMonsterId + (region - 1) * ContentRegistry.MonstersPerRegion + within;
        }

        /// <summary>
        /// The measurement the handoff needed and did not have. Not an
        /// assertion about a target rate - just the number, printed, so the
        /// balance conversation starts from evidence.
        /// </summary>
        [Fact]
        public void OneHourOfCombatIsMeasured()
        {
            var result = Simulate(ContentRegistry.FirstCanonicalMonsterId, 3600, autoAdvance: true);

            _output.WriteLine($"after 1 hour: level {result.Level}, {result.Gold:N0} gold, {result.Kills:N0} kills");
            foreach (var pair in result.KillsByMonster)
            {
                _output.WriteLine($"  {ContentRegistry.GetMonsterName(pair.Key)} x{pair.Value:N0}");
            }

            Assert.True(result.Kills > 0, "an hour of combat killed nothing at all");
        }

        /// <summary>
        /// The regression guard, stated as the design's own claim.
        ///
        /// Modul: seasons. The game is meant to outlast a three-month season -
        /// four regions in season one and the fifth in season two or three - so
        /// an hour of combat should be a dent, not a chapter. Region 1 alone is
        /// modelled at 2.5 hours bare.
        ///
        /// The bound is deliberately loose. It exists to catch another
        /// order-of-magnitude break like the one that produced "level 127 in an
        /// hour", not to freeze a balance number that is expected to move.
        /// </summary>
        [Fact]
        public void AnHourDoesNotFinishTheGame()
        {
            var result = Simulate(ContentRegistry.FirstCanonicalMonsterId, 3600, autoAdvance: true);

            Assert.True(
                result.Level <= 25,
                $"an hour of combat reached level {result.Level} with {result.Gold:N0} gold over {result.Kills:N0} kills; "
                + "a season-length curve should not clear a region in an hour");
        }

        /// <summary>
        /// The first monster in the game, in isolation. That single number is
        /// what the whole pacing model rests on.
        ///
        /// It was about twenty seconds and is now about seventy-five, because
        /// monster health was tripled across the whole ladder deliberately.
        /// Field Mouse's ATTACK was left alone in the same pass, and that
        /// exemption is load-bearing rather than an oversight: the first fight
        /// of a new account happens with nothing equipped, and at 25 damage a
        /// swing against a 100-point bar - with one bite of food every 2.5
        /// seconds returning 12 - a new player dies before landing a single
        /// kill. The measurement said 300 seconds a kill, which was the
        /// simulation reporting that the game had no entrance.
        ///
        /// Drops are rolled PER KILL, so tripling kill time cut drops per hour
        /// to a third until EquipmentDropChance was tripled to match. The two
        /// numbers move together; changing one alone silently retunes the other.
        /// </summary>
        [Fact]
        public void TheFirstMonsterTakesAboutSeventyFiveSeconds()
        {
            var result = Simulate(ContentRegistry.FirstCanonicalMonsterId, 300, autoAdvance: false);
            double secondsPerKill = 300.0 / Math.Max(1, result.Kills);

            _output.WriteLine($"{result.Kills} Field Mice in 300 s = {secondsPerKill:F1} s each, ending at level {result.Level}");

            Assert.InRange(secondsPerKill, 40.0, 110.0);
        }

        /// <summary>
        /// THE ONE THAT MATTERS: playing an hour and banking an hour must be
        /// worth about the same.
        ///
        /// They were not. The live tick subtracts the monster's armour from
        /// every swing and rolls to hit; the offline projection did neither and
        /// the warp estimate read no equipment at all. So the projections paid
        /// out for combat that could not have happened, by a margin that grew
        /// with region because armour does - and an idle game whose offline
        /// path pays better than its live one has no reason to be played.
        ///
        /// Asserted against the LIVE simulation rather than against a constant,
        /// so it keeps holding when the balance changes.
        /// </summary>
        [Theory]
        // Modul: the first TWO only.
        //
        // This drove all four of region 1's regulars against a bare level-1
        // character. Two of them now kill it - deliberately: the third and
        // fourth monsters of a region are sized so that walking up to them in
        // starter gear is fatal, which is what makes gear the gate rather than
        // a speed setting. A dead character lands no kills, so those two cases
        // measured nothing and reported it as a disagreement between the
        // projection and the tick.
        //
        // What this test is FOR is that the two models agree, and the two
        // survivable monsters prove that as well as four did.
        [InlineData(91)] // Field Mouse
        [InlineData(92)] // Horned Rabbit
        public void ProjectedKillRateMatchesTheLiveOne(int monsterId)
        {
            var payload = FreshPayload(monsterId);
            var lineage = ProgressionEngine.Lineages[payload.SelectedLineageId];
            var stats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, 0, 0, 1, 0, 0, 0, 0, 0, payload.CachedAffixTotals, false, 0, 0, payload.CachedSetIds);
            long rawMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(in stats, lineage.DamageScalePerLevelPct, payload.CurrentLevel);

            var monster = ContentRegistry.Monsters[monsterId - 1];
            double projected = CombatDamageModel.ExpectedSecondsPerKill(in stats, in monster, rawMilliAttack, payload.CachedCodexDamageMultiplier);

            Assert.False(double.IsInfinity(projected), "the projection says this monster can never be killed");

            // Run the live simulation long enough for the projection to be
            // testable. A level-1 character on a region-3 monster is grinding
            // through armour at the one-hit-point floor and needs hours per
            // kill - which is a legitimate answer, and the point is that BOTH
            // models say so. A fixed 600 s window would only ever show zero
            // kills and prove nothing.
            int seconds = (int)Math.Clamp(projected * 20.0, 600.0, 250_000.0);

            var live = Simulate(monsterId, seconds, autoAdvance: false);
            Assert.True(live.Kills > 0, $"no kills in {seconds} s against a projection of {projected:F0} s/kill");

            double liveSecondsPerKill = (double)seconds / live.Kills;

            _output.WriteLine(
                $"{ContentRegistry.GetMonsterName(monsterId)}: live {liveSecondsPerKill:F1} s/kill over {live.Kills} kills in {seconds} s, projected {projected:F1} s/kill");

            // The live figure carries the character's levelling during the run
            // and per-swing variance, so this is a "same order, not drifting"
            // bound rather than an equality.
            Assert.InRange(projected, liveSecondsPerKill * 0.5, liveSecondsPerKill * 1.6);
        }

        /// <summary>
        /// THE BALANCE AUDIT: how long each region actually takes.
        ///
        /// Not a pass/fail - a measurement, printed, for a conversation that
        /// has so far been had entirely with estimates. Every previous number
        /// about this game's pacing (the "87x too fast" report, the "59 days to
        /// level 100" that drove the last curve rework) was derived on paper.
        /// This drives the real tick.
        ///
        /// Gear is the part a naked simulation gets wrong. A character with no
        /// weapon cannot kill a region-3 monster at all - armour alone stops
        /// them - so measuring an undressed run measures a wall no player hits.
        /// Each region is therefore run with ITS OWN tier's weapon and armour
        /// base power (12/36/108/324/972 attack, 8/24/72/216/648 defence),
        /// which is the gear a player arriving there would be wearing, and no
        /// affixes at all - so this is a FLOOR on player power and a CEILING on
        /// the time. A real character rolls affixes and set bonuses on top.
        /// </summary>
        [Theory]
        [InlineData(1, 12, 8)]
        [InlineData(2, 36, 24)]
        [InlineData(3, 108, 72)]
        [InlineData(4, 324, 216)]
        [InlineData(5, 972, 648)]
        public void HowLongARegionTakes(int region, int weaponAttack, int armourDefence)
        {
            int firstMonster = ContentRegistry.FirstCanonicalMonsterId + (region - 1) * ContentRegistry.MonstersPerRegion;
            int startLevel = ((region - 1) * 20) + 1;

            // The four regulars, not the boss - a boss is a gate you pass once,
            // not the thing you grind.
            double totalSecondsPerKill = 0.0;
            long xpPerKill = 0;
            long goldPerKill = 0;

            for (int i = 0; i < 4; i++)
            {
                var monster = ContentRegistry.Monsters[firstMonster + i - 1];
                var payload = FreshPayload(firstMonster + i);
                payload.CurrentLevel = startLevel;
                payload.CachedAffixTotals.FlatAttack = weaponAttack;
                payload.CachedAffixTotals.FlatDefense = armourDefence;

                var stats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, 0, 0, 1, 0, 0, 0, 0, 0, payload.CachedAffixTotals, false, 0, 0, payload.CachedSetIds);
                var lineage = ProgressionEngine.Lineages[payload.SelectedLineageId];
                long raw = StatsCalculator.ComputeEffectiveMilliAttack(in stats, lineage.DamageScalePerLevelPct, payload.CurrentLevel);

                totalSecondsPerKill += CombatDamageModel.ExpectedSecondsPerKill(in stats, in monster, raw, 1f);
                xpPerKill += monster.BaseXpReward;
                goldPerKill += monster.BaseGoldReward;
            }

            double avgSecondsPerKill = totalSecondsPerKill / 4.0;
            double avgXp = xpPerKill / 4.0;
            double avgGold = goldPerKill / 4.0;

            // Modul: THE HEALTH POOL, measured rather than assumed.
            //
            // Every attempt to size the food economy so far has stood in a
            // guess for this - "100 plus armour rating" - because nobody had
            // ever computed what a player at a region actually has. It decides
            // the whole larder: food heals a share of max HP, and incoming
            // damage is only meaningful as a fraction of the bar it empties.
            // Printed here because this test already builds the tier-
            // appropriate character the question is about.
            {
                var poolPayload = FreshPayload(firstMonster);
                poolPayload.CurrentLevel = startLevel;
                poolPayload.CachedAffixTotals.FlatAttack = weaponAttack;
                poolPayload.CachedAffixTotals.FlatDefense = armourDefence;

                // Modul: LEVEL THE CHARACTER, do not just set the number.
                //
                // Setting CurrentLevel directly leaves STR, DEX, CON and LCK at
                // zero, so this printed a 100 HP health pool for every region -
                // the same figure at level 1 and level 81 - and that reading was
                // briefly taken for a finding about the game. It is a finding
                // about the fixture. The bar grows through CON, which
                // RaceAttributeGrowth adds per level gained and StatsCalculator
                // pays at 15 HP a point, so a character has to be walked up to
                // its level for the number to mean anything.
                RaceAttributeGrowth.ApplyLevelUpGrowth(ref poolPayload, activeRaceId: 1, levelsGained: startLevel - 1);
                var poolStats = StatsCalculator.Calculate(poolPayload.STR, poolPayload.DEX, poolPayload.CON, poolPayload.LCK, 0, 0, 1, 0, 0, 0, 0, 0, poolPayload.CachedAffixTotals, false, 0, 0, poolPayload.CachedSetIds);
                var poolLineage = ProgressionEngine.Lineages[poolPayload.SelectedLineageId];
                long baseMilliHp = 100_000L;
                long effectiveMilliHp = baseMilliHp
                    + (baseMilliHp * poolLineage.HpScalePerLevelPct * poolPayload.CurrentLevel / 100)
                    + (poolStats.MaxHp * 1000L);

                // Modul: AND THE SAME BAR WITH GEAR ON IT.
                //
                // The bar above is the FLOOR - this whole test dresses its
                // character in base power and no affixes on purpose. But the
                // health pool is the number the food economy is sized against,
                // and `flat_hp` is the only affix that touches it, so a floor
                // was being used where a reading was wanted: the gathering
                // share was knowingly recorded as "a ceiling" for exactly this
                // reason. Both are printed, because the answer is a range and
                // presenting either end of it alone is how the last three
                // numbers in this document went stale.
                long gearedMilliHp = effectiveMilliHp;
                if (AffixRegistry.TryGetDefinition("flat_hp", out var flatHpAffix))
                {
                    // Five pieces carrying a health roll at the rarity the
                    // region expects - the same loadout GatheringShareTests
                    // models on the armour and damage sides.
                    var rarity = region switch
                    {
                        1 => AffixRarity.Common,
                        2 => AffixRarity.Uncommon,
                        3 => AffixRarity.Rare,
                        4 => AffixRarity.Epic,
                        _ => AffixRarity.Legendary,
                    };
                    gearedMilliHp += 5L * AffixRegistry.CalculateMagnitude(flatHpAffix, region, rarity) * 1000L;
                }

                var strongest = ContentRegistry.Monsters[firstMonster + 3 - 1];
                // Asked of the model, not re-derived - this was the fourth copy
                // of `raw - armour`, and it would have gone on printing that
                // after the engine stopped doing it.
                long netMilliPerHit = CombatDamageModel.Mitigate(
                    strongest.AttackPower * 1000L,
                    poolStats.FlatPhysicalArmor,
                    CombatDamageModel.PlayerArmourHalvingConstant(region));
                double incomingPerSecond = netMilliPerHit * (1000.0 / strongest.AttackIntervalMs);

                _output.WriteLine(
                    $" region {region}: health pool {effectiveMilliHp / 1000.0,10:N0} hp bare, {gearedMilliHp / 1000.0,10:N0} hp with five health rolls, " +
                    $"armour {poolStats.FlatPhysicalArmor,6}, strongest regular hits {netMilliPerHit / 1000.0,8:N0} net = " +
                    $"{incomingPerSecond / effectiveMilliHp:P2} of the bare bar per second ({incomingPerSecond / gearedMilliHp:P2} geared)");
            }

            // XP the region's twenty levels demand, from the real curve.
            long xpNeeded = 0;
            for (int level = startLevel - 1; level < startLevel + 19; level++)
            {
                xpNeeded += ProgressionEngine.GetRequiredXpForLevel(level);
            }

            double kills = xpNeeded / avgXp;
            double minutes = kills * avgSecondsPerKill / 60.0;
            double goldEarned = kills * avgGold;

            _output.WriteLine(
                $"region {region}: {avgSecondsPerKill,6:F1} s/kill, {kills,8:F0} kills, "
                + $"{minutes,7:F0} min ({minutes / 60.0:F1} h), {goldEarned,12:N0} gold, "
                + $"{goldEarned / Math.Max(1.0, minutes * 60.0),8:F1} gold/sec");

            Assert.True(minutes > 0, "a region that takes no time is not a region");
        }

        /// <summary>
        /// Gold per second, against what gold BUYS.
        ///
        /// A rate is meaningless on its own - the question is whether an hour of
        /// killing pays for a reroll, ten rerolls, or none. Printed beside the
        /// reroll cost curve so the two can be compared at a glance.
        /// </summary>
        [Fact]
        public void WhatAnHourOfGoldBuys()
        {
            _output.WriteLine("reroll gold cost by REGION tier, first attempt:");
            foreach (int tier in new[] { 1, 2, 3, 4, 5 })
            {
                _output.WriteLine($"  tier {tier,2}: {AffixRegistry.CalculateRerollGoldCost(tier, 0, false),12:N0}");
            }

            _output.WriteLine("");
            _output.WriteLine("the same reroll after N consecutive attempts (region tier 3):");
            foreach (int attempt in new[] { 0, 5, 10, 20 })
            {
                _output.WriteLine($"  attempt {attempt,2}: {AffixRegistry.CalculateRerollGoldCost(3, attempt, false),12:N0}");
            }
        }

        /// <summary>
        /// Armour is subtracted, at every region.
        ///
        /// The theory above can only cover region 1, because a level-1
        /// character on a region-3 monster dies rather than grinds and the two
        /// models then differ in KIND, not in degree - the live one says "you
        /// died", the projection says "10,737 seconds a kill". Both are right.
        ///
        /// What has to hold everywhere is the thing that was actually missing:
        /// that the projection accounts for armour at all. It did not, and the
        /// error was worth 2.7x on region 1 and far more further in, always in
        /// favour of the offline path. Stated as a comparison against the same
        /// monster with its armour removed, so it cannot be satisfied by a
        /// coincidence of constants.
        /// </summary>
        [Theory]
        [InlineData(91)]
        [InlineData(96)]
        [InlineData(101)]
        [InlineData(106)]
        [InlineData(111)]
        public void TheProjectionPaysForArmour(int monsterId)
        {
            var payload = FreshPayload(monsterId);
            var stats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, 0, 0, 1, 0, 0, 0, 0, 0, payload.CachedAffixTotals, false, 0, 0, payload.CachedSetIds);
            long rawMilliAttack = 200_000L; // a mid-game weapon, so armour matters but does not floor the hit

            var armoured = ContentRegistry.Monsters[monsterId - 1];
            var bare = armoured;
            bare.Armor = 0;

            double withArmour = CombatDamageModel.ExpectedMilliDamagePerSwing(in stats, in armoured, rawMilliAttack, 1f);
            double withoutArmour = CombatDamageModel.ExpectedMilliDamagePerSwing(in stats, in bare, rawMilliAttack, 1f);

            _output.WriteLine($"{ContentRegistry.GetMonsterName(monsterId)} (armour {armoured.Armor}): {withArmour:F0} vs {withoutArmour:F0} milli per swing");

            Assert.True(armoured.Armor > 0, "this monster carries no armour, so the test proves nothing");
            Assert.True(
                withArmour < withoutArmour,
                $"armour {armoured.Armor} changed nothing - the projection is ignoring it again");
        }
    }
}
