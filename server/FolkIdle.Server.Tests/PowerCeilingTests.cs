using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// WHAT DOES A MAXED CHARACTER MULTIPLY UP TO? - 2026-09-06.
    ///
    /// Nothing in this codebase had ever asked. Every bonus is individually
    /// reasonable and each one was reviewed on its own; the PRODUCT of them was
    /// nobody's job, so it was only ever discovered when a player said the game
    /// felt wrong. Twice in one day:
    ///
    ///   codex yield    +0.5% per codex level, uncapped -> 71.9x, and one
    ///                  character out-earned the entire material sink of the
    ///                  game twice an hour
    ///   codex damage   +1.0% per codex level, uncapped -> 142.8x, and gear,
    ///                  affixes, sets and the whole rarity ladder together came
    ///                  to 0.7% of a swing
    ///
    /// Both were found by a player, not by a test. This is the test.
    ///
    /// It is a LEDGER, not a balance opinion: it prints every multiplicative
    /// lever with its documented maximum and the running product, and fails when
    /// the total leaves its band. A new bonus that forgets to bound itself moves
    /// the total, and the diff shows exactly which line did it.
    ///
    /// The reference point is stated rather than assumed. Two levers do not have
    /// a hard ceiling by design - the codex curves keep growing forever, slowly -
    /// so they are evaluated at a named codex level sum and that number is part
    /// of the assertion. "Unbounded" is not the same as "unmeasured".
    /// </summary>
    public class PowerCeilingTests
    {
        private readonly ITestOutputHelper _o;

        public PowerCeilingTests(ITestOutputHelper o)
        {
            _o = o;
            ContentRegistry.Initialize();
        }

        /// <summary>
        /// The codex level sum this ledger is quoted at. 50,000 is over three
        /// times the largest ever seen on the live server (14,178) - so it is a
        /// pessimistic reading of a curve that never formally stops.
        /// </summary>
        private const int ReferenceCodexLevelSum = 50_000;

        /// <summary>The top rarity a region-5 affix can roll at.</summary>
        private const int TopRegion = 5;

        private const AffixRarity TopRarity = AffixRarity.Legendary;

        /// <summary>A tier-14 item rolls five affixes - RarityTier.GetAffixCount.</summary>
        private static int MaxAffixRolls => RarityTier.GetAffixCount(RarityTier.Transcendent);

        private static int TopMagnitude(string affixId)
        {
            Assert.True(AffixRegistry.TryGetDefinition(affixId, out var def), $"{affixId} is not in the registry.");
            return AffixRegistry.CalculateMagnitude(def, TopRegion, TopRarity);
        }

        private sealed record Lever(string Name, double Multiplier, string Source);

        [Fact]
        public void TheDamageLedgerStaysInsideItsBand()
        {
            var levers = new List<Lever>();

            // 1. Weapon damage percentages. melee/range/magic all sum into
            //    EquipmentDamagePct, and a weapon can carry five rolls.
            double weaponPct = TopMagnitude("melee_dmg_pct") * MaxAffixRolls;
            levers.Add(new Lever("weapon damage affixes", 1.0 + weaponPct / 100.0,
                $"{MaxAffixRolls} x melee_dmg_pct at region {TopRegion} {TopRarity}"));

            // 2. Inheritance damage - a hard cap by design.
            double inheritPct = InheritanceRegistry.MaxLevel * InheritanceRegistry.PercentPerLevel;
            levers.Add(new Lever("inheritance damage", 1.0 + inheritPct / 100.0,
                $"InheritanceRegistry.MaxLevel {InheritanceRegistry.MaxLevel} x {InheritanceRegistry.PercentPerLevel}%"));

            // 3. The codex damage curve. No ceiling by design - quoted at the
            //    reference sum, which is what makes this line honest.
            double codex = CodexEngine.DamageMultiplierFor(ReferenceCodexLevelSum);
            levers.Add(new Lever("codex damage", codex,
                $"CodexEngine.DamageMultiplierFor({ReferenceCodexLevelSum:N0}) - a CURVE, not a cap"));

            // 4. Crit. Expected value, not the crit itself: a crit multiplier
            //    only pays on the share of swings that crit.
            double critChancePct = Math.Min(100.0, TopMagnitude("crit_chance_pct") * MaxAffixRolls);
            double critMultiplier = 1.5 + (TopMagnitude("crit_dmg_pct") * MaxAffixRolls) / 100.0;
            double critExpected = 1.0 + (critChancePct / 100.0) * (critMultiplier - 1.0);
            levers.Add(new Lever("crit, expected", critExpected,
                $"{critChancePct:F0}% chance at {critMultiplier:F2}x"));

            // 5. Attack speed, as a rate multiplier. Capped deliberately -
            //    see CombatDamageModel.MaxAttackSpeedReduction, which exists
            //    because affixes alone used to reach the 200 ms floor.
            double speed = 1.0 / (1.0 - CombatDamageModel.MaxAttackSpeedReduction);
            levers.Add(new Lever("attack speed", speed,
                $"MaxAttackSpeedReduction {CombatDamageModel.MaxAttackSpeedReduction:P0}"));

            // 6. The set bonus that multiplies damage.
            double setFire = 1.0 + SetBonusEngine.Evaluate(FullSet()).FireDamageMultiplierPct / 100.0;
            levers.Add(new Lever("set: fire damage", setFire, "SetBonusEngine, a full set"));

            // 7. The attribute milestone tracks, which are new as of 2026-09-06
            //    and are exactly the kind of addition this ledger exists to
            //    catch. Might's whole track plus Finesse's crit damage, at a
            //    character who has taken both to the top rung.
            var maxed = StatsCalculator.Calculate(
                AttributeRegistry.Thresholds[^1], AttributeRegistry.Thresholds[^1],
                AttributeRegistry.Thresholds[^1], AttributeRegistry.Thresholds[^1],
                0, 0, 1, 0, 0, 0, 0, 0);
            var bare = StatsCalculator.Calculate(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0);

            levers.Add(new Lever("attribute milestones: damage",
                1.0 + (maxed.EquipmentDamagePct - bare.EquipmentDamagePct) / 100.0,
                "Might's track, all five rungs"));

            // 8. Armour penetration, which did nothing at all until today. It is
            //    a mitigation lever rather than a damage one, so it is measured
            //    as what it actually buys against the toughest monster in the
            //    game - and it can never do better than ignoring armour, which
            //    is a natural ceiling of about 1.4x.
            var malakor = ContentRegistry.Monsters[ContentRegistry.LastCanonicalMonsterId - 1];
            int halving = CombatDamageModel.MonsterArmourHalvingConstant(malakor.RegionTier);
            double withoutPen = CombatDamageModel.Mitigate(1_000_000L, malakor.Armor, halving, 0);
            double withPen = CombatDamageModel.Mitigate(1_000_000L, malakor.Armor, halving, maxed.FlatArmorPenetration);
            levers.Add(new Lever("armour penetration", withPen / withoutPen,
                $"{maxed.FlatArmorPenetration} penetration against Malakor's {malakor.Armor} armour"));

            double product = 1.0;
            _o.WriteLine("lever                        multiplier   running   source");
            foreach (var lever in levers)
            {
                product *= lever.Multiplier;
                _o.WriteLine($"{lever.Name,-26} {lever.Multiplier,10:F2}x {product,9:F1}x   {lever.Source}");
            }

            _o.WriteLine("");
            _o.WriteLine($"TOTAL DAMAGE MULTIPLIER over a bare baseline: {product:N0}x");
            _o.WriteLine($"  (for scale: one region step is 3.00x, and the whole 14-tier rarity ladder is {RarityTier.TopTierPowerMultiplier:F2}x)");

            // THE BAND, ANCHORED TO THE CONTENT IT HAS TO CLIMB.
            //
            // A bare number here would be my opinion. The meaningful question
            // is how player power grows against the LADDER: a maxed character
            // fights region 5 and a bare one fights region 1, so the two curves
            // have to stay in the same neighbourhood. Grow much faster and the
            // endgame is a formality; much slower and it is a wall.
            long weakest = ContentRegistry.GetScaledMonsterMaxHp(ContentRegistry.FirstCanonicalMonsterId);
            long strongest = ContentRegistry.GetScaledMonsterMaxHp(ContentRegistry.LastCanonicalMonsterId);
            double contentLadder = (double)strongest / weakest;
            double headroom = product / contentLadder;

            _o.WriteLine($"  the monster ladder spans {contentLadder:N0}x, so a maxed character carries {headroom:F1}x headroom over it");

            // This is the assertion that would have caught the 142x codex
            // multiplier before a player did. At the old linear curve the same
            // ledger came to ~87,000x against a 750x ladder - 116x headroom,
            // which is what "every monster dies to one swing" looks like as a
            // number.
            Assert.InRange(headroom, 0.5, 10.0);

            // And a floor under the absolute figure: if the levers are ever
            // nerfed into decoration there is nothing to build toward, however
            // well they track the ladder.
            Assert.True(product > 10.0, $"the whole progression is worth only {product:F1}x.");

            // And no SINGLE lever may be the whole game. This is the specific
            // shape that failed twice: one term so large that everything beside
            // it is noise. A lever worth more than the product of all the others
            // is not a bonus, it is the system.
            foreach (var lever in levers)
            {
                double everythingElse = product / lever.Multiplier;
                Assert.True(lever.Multiplier < everythingElse,
                    $"'{lever.Name}' is {lever.Multiplier:F1}x against {everythingElse:F1}x for every other lever combined - "
                    + "it has become the whole of combat. That is the codex damage defect of 2026-09-06, again.");
            }
        }

        [Fact]
        public void TheYieldLedgerStaysInsideItsBand()
        {
            // The same question on the economy side, where the sinks are FIXED:
            // every recipe in the crafting tree costs 383,553 units however long
            // anyone plays, so a runaway here does not make the game fast, it
            // deletes a system. That is why this one is capped and the damage
            // curve is not - see CodexEngine.MaxYieldMultiplier.
            var levers = new List<Lever>
            {
                new("codex yield", CodexEngine.MaxYieldMultiplier, "CodexEngine.MaxYieldMultiplier - a CAP"),
                new("gathering mastery speed",
                    1.0 + GatheringToolEngine.GetMasterySpeedBonusPct(200) / 100.0,
                    "GetMasterySpeedBonusPct(200), a very long-played profession"),
                new("best tool in the game",
                    1.0 + GatheringToolEngine.GetToolSpeedBonusPct(10) / 100.0,
                    "tier 10, Voidbark - costs 69,862 units to craft"),
                new("village production",
                    1.0 + 12 * GatheringToolEngine.VillageYieldBonusPctPerLevel / 100.0,
                    "a level-12 Lumberjack or Mine"),
            };

            double product = 1.0;
            _o.WriteLine("lever                        multiplier   running   source");
            foreach (var lever in levers)
            {
                product *= lever.Multiplier;
                _o.WriteLine($"{lever.Name,-26} {lever.Multiplier,10:F2}x {product,9:F1}x   {lever.Source}");
            }

            _o.WriteLine("");
            _o.WriteLine($"TOTAL YIELD MULTIPLIER over a bare baseline: {product:N0}x");

            // Modul: THIS PRODUCT IS NOT A THROUGHPUT, deliberately.
            //
            // The speed levers divide into a tick threshold that floors at
            // MinRequiredTicks, so multiplying them overstates real harvest rate
            // by a wide margin - GatheringEconomyTests measures the throughput
            // itself, against every sink in the game, and reports 20x from a new
            // player to a fully maxed one.
            //
            // What this ledger is for is the LEVERS: a new bonus that forgets to
            // bound itself moves this total, and the single-lever rule below
            // catches the specific shape that failed twice. The band is wide on
            // purpose, because the number it guards is not the balance figure.
            Assert.InRange(product, 5.0, 1_000.0);

            foreach (var lever in levers)
            {
                double everythingElse = product / lever.Multiplier;
                Assert.True(lever.Multiplier < everythingElse,
                    $"'{lever.Name}' is {lever.Multiplier:F1}x against {everythingElse:F1}x for the rest - "
                    + "that is the 71.9x codex yield defect of 2026-09-06, again.");
            }
        }

        [Fact]
        public void EveryUnboundedMultiplierIsAStatedCurve()
        {
            // Modul: the rule this file exists to enforce, as a property.
            //
            // A multiplier may be capped, or it may be a diminishing curve. What
            // it may NOT be is linear and uncapped - that is what both codex
            // multipliers were, and linear-and-uncapped against any content
            // ladder ends in one number swamping the game.
            //
            // Checked by measuring the curve rather than reading the source: at
            // ten times the input, a bounded or diminishing lever must pay less
            // than ten times the output.
            (string Name, Func<int, double> At)[] curves =
            {
                ("codex damage", n => CodexEngine.DamageMultiplierFor(n)),
                ("gathering mastery speed", n => 1.0 + GatheringToolEngine.GetMasterySpeedBonusPct(n) / 100.0),
            };

            foreach (var (name, at) in curves)
            {
                double low = at(500) - 1.0;
                double high = at(5_000) - 1.0;
                double growth = high / low;

                _o.WriteLine($"{name}: 10x the input buys {growth:F2}x the bonus");
                Assert.True(growth < 10.0,
                    $"{name} grows at least linearly - ten times the input paid {growth:F1}x. "
                    + "Cap it or curve it before it becomes the whole game.");
            }
        }

        private static Span<int> FullSet()
        {
            // Seven worn pieces of one set, which is the largest tier
            // SetBonusEngine defines.
            var ids = new int[EquippedSetIds.SlotCount];
            for (int i = 0; i < ids.Length; i++) ids[i] = 1;
            return ids;
        }
    }
}
