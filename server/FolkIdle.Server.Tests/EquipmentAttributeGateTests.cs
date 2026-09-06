using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// GEAR ASKS SOMETHING OF THE CHARACTER WEARING IT — 2026-09-06.
    ///
    /// Attributes became a choice, then got identities, curves and milestone
    /// tracks - and still had no consequence. A pure-Vigour character could
    /// wield the best weapon in the game, so "specialise" only ever meant "and
    /// also get everything else anyway".
    ///
    /// The two things this has to get right are the two ways a requirement
    /// system goes wrong: too high and it is a wall nobody can pass, too low and
    /// it changes nothing. Both are measured here against the points a player
    /// actually holds at the level they meet the gear.
    /// </summary>
    public class EquipmentAttributeGateTests
    {
        private readonly ITestOutputHelper _o;

        public EquipmentAttributeGateTests(ITestOutputHelper o)
        {
            _o = o;
            ContentRegistry.Initialize();
        }

        /// <summary>Points a character holds at the level a region is entered.</summary>
        private static int PointsAtRegion(int region)
        {
            int level = ((region - 1) * 20) + 1;
            int earned = RaceAttributeGrowth.AttributePointsPerLevel * (level - 1);
            int starting = 0;
            for (int a = 0; a < AttributeRegistry.Count; a++) starting += AttributeRegistry.StartingValue(a);
            return earned + starting;
        }

        [Fact]
        public void AFullSetIsAffordableAndStillLeavesAChoice()
        {
            _o.WriteLine("region  requirement  full set (x4)  points held  left over");

            for (int region = 1; region <= 5; region++)
            {
                int requirement = EquipmentAttributeGate.RequirementFor(region);
                int fullSet = requirement * AttributeRegistry.Count;
                int held = PointsAtRegion(region);
                int spare = held - fullSet;

                _o.WriteLine($"{region,6}  {requirement,11}  {fullSet,13}  {held,11}  {spare,9}");

                // Modul: TOO HIGH IS A WALL. A player arriving at a region must
                // be able to wear that region's gear in every slot - the gate is
                // meant to make the choice binding, not to lock content behind
                // an arithmetic nobody was told about.
                Assert.True(spare >= 0,
                    $"region {region} demands {fullSet} points of a character who holds {held} - that is a wall, not a requirement.");

                // Modul: TOO LOW IS NOTHING. At least a third of what a player
                // holds has to remain free, or the "choice" is a formality with
                // one legal answer.
                Assert.True(spare >= held / 3,
                    $"region {region} leaves only {spare} of {held} points free - there is no choice left to make.");
            }
        }

        [Fact]
        public void TheFirstTwoRegionsAreNeverAWallForANewPlayer()
        {
            // Modul: REGION 1 ASKS FOR NOTHING AT ALL.
            //
            // A character starts with every attribute at ZERO - PlayerRecord's
            // Base* columns have no initialiser - and holds no points until it
            // levels. So any requirement whatever on region-1 gear means a new
            // player cannot equip the first weapon the game hands them, which is
            // the closed entrance this project has already shipped once.
            //
            // This assertion was written against an assumed 50/50/50/25 start,
            // read off the one legacy account that has it. The live database has
            // three accounts at zero. The test caught the gate; the gate caught
            // the assumption.
            //
            // A brand-new character starts at 50/50/50/25 having placed nothing,
            // and region 1 is where they learn what an attribute even is - so
            // nothing there may ask for a single point. This game has shipped a
            // closed entrance once already.
            //
            // Region 2 is different: it is reached at about level 21 with 140
            // points in hand, so a requirement that bites there is the system
            // starting to work rather than a wall. Fortune starts at 25 against
            // the others' 50, so a region-2 ring is the FIRST piece in the game
            // that asks for anything - which is a good place for it to be.
            Assert.Equal(0, EquipmentAttributeGate.RequirementFor(1));

            for (int attribute = 0; attribute < AttributeRegistry.Count; attribute++)
            {
                Assert.Equal(0, AttributeRegistry.StartingValue(attribute));
            }

            // A level-1 character with nothing placed must be able to wear every
            // region-1 piece in the catalogue, asked of the real content rather
            // than of the formula.
            foreach (var item in ContentRegistry.ItemDefinitions.ToArray())
            {
                if (item.RegionTier != 1) continue;
                string baseId = ContentRegistry.GetItemBaseId(item.Id);
                if (AffixRegistry.ResolveSlot(baseId) == EquipmentSlotKind.Unknown) continue;

                Assert.True(EquipmentAttributeGate.CanWear(baseId, 0, 0, 0, 0),
                    $"a brand-new character cannot equip {baseId} - that is a closed entrance.");
            }
        }

        [Fact]
        public void EverySlotAsksForSomethingExceptTheTools()
        {
            var combat = new[]
            {
                EquipmentSlotKind.Weapon, EquipmentSlotKind.Helmet, EquipmentSlotKind.Chest,
                EquipmentSlotKind.Leggings, EquipmentSlotKind.Gloves, EquipmentSlotKind.Boots,
                EquipmentSlotKind.Amulet, EquipmentSlotKind.Ring,
            };

            foreach (var slot in combat)
            {
                int attribute = EquipmentAttributeGate.AttributeForSlot(slot);
                _o.WriteLine($"{slot,-10} -> {AttributeRegistry.NameOf(attribute)}");
                Assert.InRange(attribute, 0, AttributeRegistry.Count - 1);
            }

            // Modul: gathering is NOT gated by a combat stat. A player who
            // cannot equip an axe cannot gather, cannot craft, and cannot buy
            // their way out of it - that is a dead end rather than a choice, and
            // this game has built one of those before.
            Assert.Equal(-1, EquipmentAttributeGate.AttributeForSlot(EquipmentSlotKind.Tool));

            // ALL FOUR attributes must be demanded by something, or the ones
            // nobody needs are back to being dump stats with extra steps.
            var demanded = combat.Select(EquipmentAttributeGate.AttributeForSlot).Distinct().ToList();
            Assert.Equal(AttributeRegistry.Count, demanded.Count);
        }

        [Fact]
        public void TheGateRefusesAndPermitsTheRightCharacters()
        {
            // A real region-5 weapon out of the catalogue rather than a made-up
            // id, so this breaks if the slot resolver ever stops recognising one.
            // ItemDefinition is a packed struct with no BaseId - the string
            // lives in a parallel table, reached by id.
            string weapon = ContentRegistry.ItemDefinitions.ToArray()
                .Where(i => i.RegionTier == 5 && i.FlatAttackPower > 0)
                .Select(i => ContentRegistry.GetItemBaseId(i.Id))
                .FirstOrDefault(id => AffixRegistry.ResolveSlot(id) == EquipmentSlotKind.Weapon)
                ?? string.Empty;

            Assert.False(string.IsNullOrEmpty(weapon), "no region-5 weapon found in the catalogue.");

            var (attribute, minimum) = EquipmentAttributeGate.RequirementOf(weapon);
            _o.WriteLine($"{weapon} needs {minimum} {AttributeRegistry.NameOf(attribute)}");

            Assert.Equal(AttributeRegistry.Might, attribute);
            Assert.Equal(EquipmentAttributeGate.RequirementFor(5), minimum);

            // One point short is a refusal; exactly enough is a yes.
            Assert.False(EquipmentAttributeGate.CanWear(weapon, minimum - 1, 999, 999, 999));
            Assert.True(EquipmentAttributeGate.CanWear(weapon, minimum, 0, 0, 0));

            // And the OTHER three attributes are irrelevant to this piece, which
            // is what makes a specialist able to wear their own gear.
            Assert.True(EquipmentAttributeGate.CanWear(weapon, minimum, 0, 0, 0));
        }

        [Fact]
        public void ASpecialistCanStillWearTheirOwnSpecialty()
        {
            // Modul: the point of the whole system. A character who poured
            // everything into Might must be able to wield the best weapon in the
            // game even if they can wear nothing else - otherwise the gate
            // punishes specialising, which is the opposite of what it is for.
            int atRegion5 = EquipmentAttributeGate.RequirementFor(5);
            string weapon = ContentRegistry.ItemDefinitions.ToArray()
                .Where(i => i.RegionTier == 5)
                .Select(i => ContentRegistry.GetItemBaseId(i.Id))
                .First(id => AffixRegistry.ResolveSlot(id) == EquipmentSlotKind.Weapon);

            Assert.True(EquipmentAttributeGate.CanWear(weapon, atRegion5 * 3, 0, 0, 0),
                "a pure-Might character cannot wield a region-5 weapon.");
        }
    }
}
