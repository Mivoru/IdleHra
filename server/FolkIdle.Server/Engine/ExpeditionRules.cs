using System;
using FolkIdle.Server.Domain.Economy;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Expeditions (owner approved 2026-11-13, docs/design/ENDGAME_CONTENT_2026_11_13.md
    /// section 4A): a benched grown ancestor goes away for a few hours and
    /// brings back one loot chest of the account's highest region.
    /// </summary>
    /// <remarks>
    /// Modul: WHY. The Hall holds hundreds of bred adults and only three can
    /// play. An expedition gives every good child a second use and the account
    /// a check-in goal, and it makes aptitudes count off the main slot.
    ///
    /// Modul: SIZED AGAINST WHAT ALREADY PAYS CHESTS. The Dungeon pays up to six
    /// chest-bearing runs a day (2 tickets x 3 gates). Expeditions are at most
    /// three slots of at least four hours, and a twelve-hour run - the one that
    /// fits a sleeping player - pays a Silver chest, a Gold one at best. A
    /// player checking twice a day gets three to six chests, mostly Bronze and
    /// Silver. Chests already keep their diamonds under the weekly ceiling and
    /// price their materials in minutes of reference gathering, so the faucet is
    /// bounded in the units the economy already uses (gold inflation is an open
    /// owner question; this adds no gold of its own).
    /// </remarks>
    public static class ExpeditionRules
    {
        public static readonly int[] Durations = { 4, 8, 12 };

        /// <summary>Breeding Grounds level from which the third slot opens.</summary>
        public const int ThirdSlotGroundsLevel = 8;

        /// <summary>Expedition slots for a Breeding Grounds level: none without Grounds, 2, then 3.</summary>
        public static int SlotsFor(int groundsLevel) => groundsLevel <= 0 ? 0 : groundsLevel >= ThirdSlotGroundsLevel ? 3 : 2;

        public static bool IsDuration(int hours) => Array.IndexOf(Durations, hours) >= 0;

        // Modul: SLOTS FOR GOLD, PRICED IN HOURS OF THE PLAYER'S OWN INCOME
        // (owner, 2026-11-13: "something worth spending gold on, the price
        // following what the player earns"). Two of the three endgame accounts
        // spent under a quarter of what they earned
        // (docs/design/GOLD_ECONOMY_2026_11_13.md). A flat price is a fortune at
        // one income and pocket change at another; hours of the player's own
        // seven-day income (LootChestEngine.GoldPerMinuteAsync, the chest's
        // measure) cost everyone the same effort.
        public const int MaxBoughtSlots = 2;
        public static readonly int[] BoughtSlotPriceHours = { 24, 72 };

        /// <summary>Slots with gold-bought ones added; still none without the Grounds.</summary>
        public static int SlotsFor(int groundsLevel, int bought)
            => SlotsFor(groundsLevel) == 0 ? 0 : SlotsFor(groundsLevel) + Math.Clamp(bought, 0, MaxBoughtSlots);

        /// <summary>The next bought slot's price, or 0 when both are bought.</summary>
        public static long NextSlotPrice(int bought, double goldPerMinute)
            => bought >= MaxBoughtSlots ? 0L : (long)Math.Ceiling(goldPerMinute * 60.0 * BoughtSlotPriceHours[Math.Max(0, bought)] / 1000.0) * 1000L;

        // Modul: PROVISIONS, A GOLD SINK THAT BUYS A BETTER HAUL (owner,
        // 2026-11-13: "upgraded expeditions for gold, priced by what the player
        // earns"). Provisioning a party costs half the trip's length in hours of
        // the player's own income (2 / 4 / 6 h for 4 / 8 / 12 h trips) and adds
        // ProvisionUpgradePct to the chance of the better chest - on a 4 h trip,
        // which otherwise always pays Bronze, it opens a Silver at that chance.
        // It never pays a tier the trip could not reach, so the chest faucet's
        // ceiling (ChestFor) is unchanged; only its mix moves.
        public const int ProvisionUpgradePct = 25;

        /// <summary>Hours of the player's own income that provisioning a trip of this length costs.</summary>
        public static int ProvisionPriceHours(int tripHours) => Math.Max(1, tripHours / 2);

        /// <summary>The provisions price for a trip of this length at this income, rounded up to 1,000.</summary>
        public static long ProvisionPrice(int tripHours, double goldPerMinute)
            => (long)Math.Ceiling(goldPerMinute * 60.0 * ProvisionPriceHours(tripHours) / 1000.0) * 1000L;

        /// <summary>Titles for expeditions claimed (a long-term goal for the bench), at-or-past and idempotent.</summary>
        public static readonly (int Count, string Slug, string Name)[] TitleMilestones =
        {
            (10, "expedition_10", "Pathfinder"),
            (50, "expedition_50", "Far-Strider"),
            (100, "expedition_100", "Lord of Long Roads"),
        };

        /// <summary>
        /// The upgrade chance, in percent, a party's aptitude total adds: 0 at
        /// the founder's 16, +30 at 200 (50 in all four), linear between.
        /// </summary>
        public static int AptitudeUpgradePct(int aptitudeTotal)
            => Math.Clamp((aptitudeTotal - 16) * 30 / (200 - 16), 0, 30);

        /// <summary>
        /// The chest a return pays: 4 h Bronze; 8 h Bronze, Silver at 25% +
        /// aptitude; 12 h Silver, Gold at 15% + aptitude. Provisions add
        /// ProvisionUpgradePct, and give a 4 h trip a Silver at that chance.
        /// </summary>
        public static LootChestTier ChestFor(int hours, int aptitudeTotal, int roll0To99, bool provisioned = false)
        {
            int bonus = AptitudeUpgradePct(aptitudeTotal) + (provisioned ? ProvisionUpgradePct : 0);
            return hours switch
            {
                >= 12 => roll0To99 < 15 + bonus ? LootChestTier.Gold : LootChestTier.Silver,
                >= 8 => roll0To99 < 25 + bonus ? LootChestTier.Silver : LootChestTier.Bronze,
                _ => provisioned && roll0To99 < ProvisionUpgradePct ? LootChestTier.Silver : LootChestTier.Bronze,
            };
        }
    }
}
