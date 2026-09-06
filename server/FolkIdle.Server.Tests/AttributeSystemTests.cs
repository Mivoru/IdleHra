using System;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// THE FOUR ATTRIBUTES, REWORKED - 2026-09-06.
    ///
    /// They became a choice earlier the same day, which is what made it obvious
    /// how little they did. Two of their eleven effects were DEAD:
    /// `FlatArmorPenetration`, granted by Might and rolled as an affix, was
    /// never passed to the only method that applies armour; and
    /// `FlatRangedDamage`, Finesse's headline effect, is read by nothing in
    /// combat at all - so Finesse was a strictly better Might with three bonuses
    /// attached and Fortune was a dump stat.
    ///
    /// This pins the rework: identities that differ, curves that cannot run
    /// away, and a milestone table that reaches fields something already reads.
    /// </summary>
    public class AttributeSystemTests
    {
        private readonly ITestOutputHelper _o;

        public AttributeSystemTests(ITestOutputHelper o)
        {
            _o = o;
            ContentRegistry.Initialize();
        }

        private static CombatStats StatsFor(int str, int dex, int con, int lck)
            => StatsCalculator.Calculate(str, dex, con, lck, 0, 0, 1, 0, 0, 0, 0, 0);

        [Fact]
        public void ArmourPenetrationFinallyDoesSomething()
        {
            // Modul: the defect, as the test that would have caught it. Every
            // point of Might's penetration and every armor_pen_flat affix ever
            // rolled was worth nothing, because Mitigate never took the term.
            var malakor = ContentRegistry.Monsters[ContentRegistry.LastCanonicalMonsterId - 1];
            int k = CombatDamageModel.MonsterArmourHalvingConstant(malakor.RegionTier);
            const long raw = 1_000_000L;

            long none = CombatDamageModel.Mitigate(raw, malakor.Armor, k, 0);
            long some = CombatDamageModel.Mitigate(raw, malakor.Armor, k, 500);
            long lots = CombatDamageModel.Mitigate(raw, malakor.Armor, k, 5_000);

            _o.WriteLine($"Malakor armour {malakor.Armor}, K {k}");
            _o.WriteLine($"  no penetration {none / 10_000.0:F1}% through");
            _o.WriteLine($"  500            {some / 10_000.0:F1}%");
            _o.WriteLine($"  5,000          {lots / 10_000.0:F1}%");

            Assert.True(some > none, "armour penetration does nothing - this is the 2026-09-06 defect, back.");
            Assert.True(lots > some);

            // It can approach ignoring armour and can never do better, so it is
            // worth stacking and never worth stacking exclusively.
            Assert.True(lots <= raw, "penetration produced more than the unmitigated hit.");

            // Diminishing by construction: the second 500 is worth less than the
            // first. That is what `K + pen` buys over subtracting from armour,
            // which one affix roll could take to zero.
            long first = some - none;
            long second = CombatDamageModel.Mitigate(raw, malakor.Armor, k, 1_000) - some;
            Assert.True(second < first, "penetration is not diminishing - a big roll could erase armour entirely.");
        }

        [Fact]
        public void TheAttributesHaveDifferentIdentities()
        {
            // 100 points into one attribute at a time, so what each buys is
            // visible side by side. Before the rework Finesse gave everything
            // Might did AND accuracy, crit and attack speed.
            var might = StatsFor(150, 50, 50, 25);
            var finesse = StatsFor(50, 150, 50, 25);
            var vigour = StatsFor(50, 50, 150, 25);
            var fortune = StatsFor(50, 50, 50, 125);

            _o.WriteLine($"Might   attack {might.FlatMeleeDamage}, penetration {might.FlatArmorPenetration}");
            _o.WriteLine($"Finesse accuracy {finesse.AccuracyRating}, crit {finesse.CritChancePct:F1}%, speed {finesse.AttackSpeedPct:F1}%");
            _o.WriteLine($"Vigour  health {vigour.MaxHp}, armour {vigour.FlatPhysicalArmor}, block {vigour.BlockStrengthPct:F1}%");
            _o.WriteLine($"Fortune loot {fortune.LootLuckPct:F1}%, forge {fortune.ForgeSuccessPct:F1}%");

            // Might is the damage attribute, and it is the ONLY damage
            // attribute - Finesse's old +2 ranged damage was read by nothing.
            Assert.True(might.FlatMeleeDamage > finesse.FlatMeleeDamage);
            Assert.True(might.FlatArmorPenetration > finesse.FlatArmorPenetration);

            // Finesse is the one that lands blows.
            Assert.True(finesse.AccuracyRating > might.AccuracyRating);
            Assert.True(finesse.CritChancePct > might.CritChancePct);
            Assert.True(finesse.AttackSpeedPct > might.AttackSpeedPct);

            Assert.True(vigour.MaxHp > might.MaxHp);
            Assert.True(fortune.LootLuckPct > might.LootLuckPct);

            // Modul: NO ATTRIBUTE MAY BE STRICTLY BETTER THAN ANOTHER, which is
            // exactly what Finesse was. Each of the four must beat all three
            // others at something.
            var all = new[] { might, finesse, vigour, fortune };
            Assert.True(might.FlatMeleeDamage == all.Max(s => s.FlatMeleeDamage));
            Assert.True(finesse.CritChancePct == all.Max(s => s.CritChancePct));
            Assert.True(vigour.MaxHp == all.Max(s => s.MaxHp));
            Assert.True(fortune.LootLuckPct == all.Max(s => s.LootLuckPct));
        }

        [Fact]
        public void ThePercentageEffectsDiminishAndTheFlatOnesDoNot()
        {
            // A level pays 7 points and nothing spends them for you, so a
            // long-played character holds hundreds in one attribute. At the old
            // flat 0.1% a point that is +59% crit chance from Finesse alone -
            // the linear-and-uncapped shape PowerCeilingTests refuses.
            _o.WriteLine("points   crit%   speed%   attack   health");
            foreach (int points in new[] { 25, 50, 100, 300, 600 })
            {
                var s = StatsFor(points, points, points, points);
                _o.WriteLine($"{points,6}  {s.CritChancePct,6:F1}  {s.AttackSpeedPct,6:F1}  {s.FlatMeleeDamage,7}  {s.MaxHp,7}");
            }

            // Curved: ten times the points buys less than ten times the crit.
            float low = StatsFor(50, 60, 50, 25).CritChancePct;
            float high = StatsFor(50, 600, 50, 25).CritChancePct;
            Assert.True(high / low < 10f, $"crit chance grew {high / low:F1}x for 10x the points - that is linear.");

            // Flat: attack power and health stay linear, because they race
            // content that grows geometrically. A curve there would make them
            // stop mattering rather than stop running away.
            int lowAttack = StatsFor(60, 50, 50, 25).FlatMeleeDamage;
            int highAttack = StatsFor(600, 50, 50, 25).FlatMeleeDamage;
            Assert.Equal(10, highAttack / lowAttack);
        }

        [Fact]
        public void EveryMilestoneMovesAStatSomethingReads()
        {
            // Modul: THE CONSTRAINT THE TABLE WAS WRITTEN UNDER. A milestone
            // list inventing new mechanics would be twenty fresh chances at this
            // codebase's most expensive defect - a stat computed and never
            // consumed. Every rung has to move a field the live tick reads, and
            // the way to prove that is to cross each threshold and watch
            // something change.
            _o.WriteLine("attribute  at   name                 what moved");

            foreach (var milestone in AttributeRegistry.Milestones)
            {
                // Modul: COMPARE ACROSS TWO POINTS, NOT ONE.
                //
                // The first version of this test compared threshold-1 against
                // threshold, and PASSED while the milestone code was not wired
                // in at all - because every per-point effect also moves when a
                // point is added, so the disjunction below was satisfied by the
                // linear terms alone. It was a test that could not fail, and it
                // hid exactly the defect it was written for.
                //
                // The fix is to measure the STEP against the slope: the change
                // across the threshold must be bigger than the change one point
                // earlier, which only a discrete rung can produce.
                int below = milestone.Threshold - 1;
                int at = milestone.Threshold;

                CombatStats a = milestone.Attribute switch
                {
                    AttributeRegistry.Might => StatsFor(below, 0, 0, 0),
                    AttributeRegistry.Finesse => StatsFor(0, below, 0, 0),
                    AttributeRegistry.Vigour => StatsFor(0, 0, below, 0),
                    _ => StatsFor(0, 0, 0, below),
                };
                CombatStats b = milestone.Attribute switch
                {
                    AttributeRegistry.Might => StatsFor(at, 0, 0, 0),
                    AttributeRegistry.Finesse => StatsFor(0, at, 0, 0),
                    AttributeRegistry.Vigour => StatsFor(0, 0, at, 0),
                    _ => StatsFor(0, 0, 0, at),
                };

                CombatStats control = milestone.Attribute switch
                {
                    AttributeRegistry.Might => StatsFor(below - 1, 0, 0, 0),
                    AttributeRegistry.Finesse => StatsFor(0, below - 1, 0, 0),
                    AttributeRegistry.Vigour => StatsFor(0, 0, below - 1, 0),
                    _ => StatsFor(0, 0, 0, below - 1),
                };

                // The ordinary per-point slope, one point below the rung.
                float slope = Fingerprint(b, a) - Fingerprint(a, control);

                bool moved = slope > 0.001f
                    || Math.Abs(b.EquipmentDamagePct - a.EquipmentDamagePct) > 0.001f
                    || b.FlatArmorPenetration != a.FlatArmorPenetration
                    || Math.Abs(b.AttackSpeedPct - a.AttackSpeedPct) > 0.001f
                    || b.AccuracyRating != a.AccuracyRating
                    || Math.Abs(b.EquipmentCritDamagePct - a.EquipmentCritDamagePct) > 0.001f
                    || Math.Abs(b.CritChancePct - a.CritChancePct) > 0.001f
                    || b.MaxHp != a.MaxHp
                    || b.FlatPhysicalArmor != a.FlatPhysicalArmor
                    || Math.Abs(b.OutOfCombatHpRegen - a.OutOfCombatHpRegen) > 0.001f
                    || Math.Abs(b.CritMitigationPct - a.CritMitigationPct) > 0.001f
                    || Math.Abs(b.LootLuckPct - a.LootLuckPct) > 0.001f
                    || Math.Abs(b.WoodcuttingYieldBonusPct - a.WoodcuttingYieldBonusPct) > 0.001f
                    || Math.Abs(b.GoldAcquisitionMultiplierPct - a.GoldAcquisitionMultiplierPct) > 0.001f
                    || Math.Abs(b.ForgeSuccessPct - a.ForgeSuccessPct) > 0.001f;

                _o.WriteLine($"{AttributeRegistry.NameOf(milestone.Attribute),-9} {milestone.Threshold,3}  {milestone.Name,-19}  {milestone.Effect}");

                Assert.True(slope > 0.001f,
                    $"crossing {milestone.Name} at {milestone.Threshold} moved the stats no more than an ordinary point did - "
                    + "the rung is a promise with nothing behind it. (This is the shape that shipped unwired once.)");
                Assert.True(moved);
            }
        }

        /// <summary>
        /// A single number summarising everything a milestone could move, so a
        /// STEP can be told from a SLOPE. Percentages and flat values are summed
        /// raw - this is a change detector, not a power figure.
        /// </summary>
        private static float Fingerprint(in CombatStats higher, in CombatStats lower)
            => (higher.EquipmentDamagePct - lower.EquipmentDamagePct)
             + (higher.FlatArmorPenetration - lower.FlatArmorPenetration)
             + (higher.AttackSpeedPct - lower.AttackSpeedPct)
             + (higher.AccuracyRating - lower.AccuracyRating)
             + (higher.EquipmentCritDamagePct - lower.EquipmentCritDamagePct)
             + (higher.CritChancePct - lower.CritChancePct)
             + (higher.MaxHp - lower.MaxHp)
             + (higher.FlatPhysicalArmor - lower.FlatPhysicalArmor)
             + (higher.OutOfCombatHpRegen - lower.OutOfCombatHpRegen)
             + (higher.CritMitigationPct - lower.CritMitigationPct)
             + (higher.LootLuckPct - lower.LootLuckPct)
             + (higher.WoodcuttingYieldBonusPct - lower.WoodcuttingYieldBonusPct)
             + (higher.GoldAcquisitionMultiplierPct - lower.GoldAcquisitionMultiplierPct)
             + (higher.ForgeSuccessPct - lower.ForgeSuccessPct);

        [Fact]
        public void TheTrackIsFiveRungsPerAttributeAndTheyRise()
        {
            for (int attribute = 0; attribute < AttributeRegistry.Count; attribute++)
            {
                var track = AttributeRegistry.Milestones.Where(m => m.Attribute == attribute).ToList();
                Assert.Equal(AttributeRegistry.Thresholds.Length, track.Count);

                for (int i = 1; i < track.Count; i++)
                {
                    Assert.True(track[i].Threshold > track[i - 1].Threshold,
                        $"{AttributeRegistry.NameOf(attribute)}'s track does not rise at rung {i}.");
                }

                // Every attribute uses the same thresholds, so the four tracks
                // are comparable at a glance - "two rungs up Might" means the
                // same commitment as two rungs up Fortune.
                Assert.Equal(AttributeRegistry.Thresholds, track.Select(m => m.Threshold).ToArray());
            }

            Assert.Equal(0, AttributeRegistry.MilestonesReached(24));
            Assert.Equal(1, AttributeRegistry.MilestonesReached(25));
            Assert.Equal(5, AttributeRegistry.MilestonesReached(300));
            Assert.Equal(5, AttributeRegistry.MilestonesReached(10_000));
        }
    }
}
