using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// WHAT AN ITEM ASKS OF THE CHARACTER WEARING IT — 2026-09-06.
    ///
    /// Attributes became a choice this morning and then got identities, curves
    /// and milestone tracks. What they still did not have was a CONSEQUENCE: a
    /// pure-Vigour character could wield the best weapon in the game, so
    /// "specialise" only ever meant "and also get everything else anyway".
    ///
    /// Every RPG that makes attributes matter does this the same way - a minimum
    /// on the item - because it is the one rule that makes a choice binding
    /// without taking anything away from anyone. You are not punished for
    /// spreading; you are simply unable to wear what you have not paid for.
    ///
    /// DERIVED, NOT AUTHORED, for the same reason MonsterDefenceCurve is:
    /// seventy-eight gear pieces hand-edited in JSON is seventy-eight chances to
    /// mistype a number, and the flat-content defect the canonical monsters
    /// shipped with is exactly what that looks like when it goes wrong.
    ///
    /// WHICH ATTRIBUTE, BY SLOT
    ///
    ///   Weapon                      Might     - you have to be able to swing it
    ///   Helmet, Chest, Leggings     Vigour    - the heavy plate
    ///   Gloves, Boots               Finesse   - the pieces you move in
    ///   Amulet, Ring                Fortune   - the pieces that are luck itself
    ///   Tools                       none      - gathering is not gated by a
    ///                                           combat stat, and a player who
    ///                                           cannot chop wood cannot craft
    ///                                           their way out of it
    ///
    /// Spread across all four so a full set needs all four, which sets a FLOOR
    /// and leaves everything above it as the actual choice. At region 5 that
    /// floor is 400 of the roughly 735 points a level-81 character holds - a bit
    /// over half, with the rest free.
    ///
    /// CHECKED WHEN EQUIPPING, NOT CONTINUOUSLY. Nothing a player is already
    /// wearing is ever stripped: the live account has 595 unspent points and
    /// attributes still at their starting values, and enforcing this on the tick
    /// would have undressed it completely at the next deploy. A respec can
    /// therefore leave a character wearing something they could not put back on,
    /// which is the standard shape of this rule and is a consequence the player
    /// chose rather than one the server sprang on them.
    /// </summary>
    public static class EquipmentAttributeGate
    {
        /// <summary>
        /// Points required per region tier ABOVE THE FIRST.
        ///
        /// Modul: REGION 1 ASKS FOR NOTHING, and that is load-bearing rather
        /// than generous. A character begins with every attribute at ZERO - the
        /// Base* columns have no initialiser and a registration gets zeroes -
        /// and holds no points to place until it levels. Any requirement at all
        /// on region-1 gear means a brand-new player cannot equip the first
        /// weapon the game hands them, which is the closed entrance this project
        /// has already shipped once.
        ///
        /// From region 2 it rises 20 a tier. Region 2 is reached at about level
        /// 21 with 140 points in hand, so a full set costs 80 of them and leaves
        /// 60 free - and that ratio holds across the whole game: at every region
        /// a full set is roughly 57% of what a player holds, and the remaining
        /// 43% is the choice. EquipmentAttributeGateTests prints the table.
        /// </summary>
        public const int RequirementPerRegionTier = 20;

        public static int RequirementFor(int regionTier)
        {
            if (regionTier < 2) return 0;
            return RequirementPerRegionTier * (regionTier - 1);
        }

        /// <summary>
        /// Which attribute a slot asks for, or -1 for the slots that ask for
        /// nothing.
        /// </summary>
        public static int AttributeForSlot(EquipmentSlotKind slot) => slot switch
        {
            EquipmentSlotKind.Weapon => AttributeRegistry.Might,
            EquipmentSlotKind.Helmet => AttributeRegistry.Vigour,
            EquipmentSlotKind.Chest => AttributeRegistry.Vigour,
            EquipmentSlotKind.Leggings => AttributeRegistry.Vigour,
            EquipmentSlotKind.Gloves => AttributeRegistry.Finesse,
            EquipmentSlotKind.Boots => AttributeRegistry.Finesse,
            EquipmentSlotKind.Amulet => AttributeRegistry.Fortune,
            EquipmentSlotKind.Ring => AttributeRegistry.Fortune,
            _ => -1,
        };

        /// <summary>
        /// The requirement an item places, as (attribute, minimum). Attribute is
        /// -1 when the item asks for nothing - tools, and anything whose slot
        /// cannot be resolved.
        /// </summary>
        public static (int Attribute, int Minimum) RequirementOf(string baseItemId)
        {
            var slot = AffixRegistry.ResolveSlot(baseItemId);
            int attribute = AttributeForSlot(slot);
            if (attribute < 0) return (-1, 0);

            int regionTier = ContentRegistry.GetRegionTierForBaseId(baseItemId);
            return (attribute, RequirementFor(regionTier));
        }

        /// <summary>
        /// Whether a character holding these four attributes may wear the item.
        /// </summary>
        public static bool CanWear(string baseItemId, int str, int dex, int con, int lck)
        {
            var (attribute, minimum) = RequirementOf(baseItemId);
            if (attribute < 0 || minimum <= 0) return true;

            int held = attribute switch
            {
                AttributeRegistry.Might => str,
                AttributeRegistry.Finesse => dex,
                AttributeRegistry.Vigour => con,
                _ => lck,
            };

            return held >= minimum;
        }
    }
}
