using System;

namespace FolkIdle.Server.Engine
{
    // Modul: larder. The single authority on what counts as auto-eat food and
    // how much each tier heals. The per-tier table is the GDD's cooking
    // table (Module "Cooking", section 3.2).
    //
    // Modul: THE COOKED FOODS ARE GONE, 2026-09-25. Items 194-203 were the
    // ten cooked_*_tN_food dishes. Their recipes were deleted long ago, so
    // nothing could grant one, and production held none. The owner chose to
    // delete them rather than add a cooking profession. The heal table stays:
    // raw fish eat on it, as the legacy "_food_consumable" items do. What used
    // to be "the cooked dish of tier N" is now "a fish from region N", and
    // FirstRawFishOfTier answers that question.
    //
    // This file exists because two things were wrong before it:
    //
    // 1. AlchemyCompendium.IsValidConsumable classified food by the BaseId
    //    marker "_food_consumable". None of the ten real foods carry that
    //    marker - they end in "_food" - so every one of them was an invalid
    //    consumable. Since ValidateConsumableRequest failure calls
    //    TerminateSessionForSecurity, eating any actual crafted food would
    //    have force-disconnected the player. (The four items that DO carry
    //    "_food_consumable" - roasted_perch, viper_stew, bear_stew,
    //    yeti_platter, ids 372-375 - are not produced by any recipe or drop,
    //    so they were the only foods the game accepted and the only foods
    //    nobody could obtain.) Both families are honoured here.
    //
    // 2. SimulationEngine's auto-eat step scored every occupied slot at a
    //    hardcoded 50000 milli-HP. Its "pick the highest-healing food" logic
    //    was therefore a tie on every comparison, so it always drained slot 1
    //    first, and a tier-10 Astral Ambrosia Roast (82000 HP per the GDD)
    //    restored the same 50 HP as a tier-1 minnow.
    public static class FoodRegistry
    {
        // The GDD's flat HP recovery per tier, in the same milli-HP units the
        // combat simulation uses for PlayerHp everywhere else (the engine
        // works in thousandths, hence PlayerHp / 1000 on the outbound packet).
        private static readonly int[] _healPayoutFlatHp =
        {
            40,      // food_t01 Seared Minnow Platter
            120,     // food_t02 Bog Nightshade Stew
            310,     // food_t03 Salted Mud Carp
            750,     // food_t04 Mountain Pike Roast
            1720,    // food_t05 Steppe Salmon Bake
            3840,    // food_t06 Maple Glazed Cod
            8450,    // food_t07 Deep Mire Eel Broth
            18200,   // food_t08 Karst Catfish Gumbo
            38900,   // food_t09 Glacial Shark Steak
            82000    // food_t10 Astral Ambrosia Roast
        };

        public static int TierCount => _healPayoutFlatHp.Length;

        /// <summary>
        /// The lowest-id raw fish whose region tier is <paramref name="tier"/>,
        /// or 0 if no fishing node drops one. Every fish of a tier heals the
        /// same, so any of them is the tier's food.
        /// </summary>
        public static int FirstRawFishOfTier(int tier)
        {
            int best = 0;
            foreach (int id in ContentRegistry.RawFishItemIds)
            {
                if (ContentRegistry.ItemDefinitions[id - 1].RegionTier == tier && (best == 0 || id < best))
                {
                    best = id;
                }
            }
            return best;
        }

        // True for anything the larder will accept: raw fish, and the legacy
        // "_food_consumable" family by its BaseId marker.
        public static bool IsFood(int itemId)
        {
            if (itemId <= 0 || itemId > ContentRegistry.ItemDefinitions.Length)
            {
                return false;
            }

            // Modul: raw fish is food HERE TOO.
            //
            // GetHealMilliHp below learned this and IsFood did not, which is
            // worse than neither knowing: the larder asks IsFood, so stocking a
            // Sunlit Perch was refused outright while the eating code was
            // perfectly ready to consume one. Two predicates for one question,
            // and only one of them updated.
            if (ContentRegistry.IsRawFish(itemId))
            {
                return true;
            }

            return ContentRegistry.GetItemBaseId(itemId).Contains("_food");
        }

        // Milli-HP restored by one unit of this food. Zero for a non-food, so
        // the auto-eat comparison can score an empty or bogus slot at 0 and
        // never pick it.
        //
        // Raw fish heals this share of what the same-tier cooked dish would.
        // Cooking is not in the design list, so this is not a penalty pushing
        // players towards a profession that does not exist - it is simply what
        // a fish is worth.
        private const int RawFishHealPercent = 60;

        // Modul: FOOD WAS A FLAT NUMBER AGAINST A GROWING WOUND, and that made
        // the larder a step function rather than a system.
        //
        // A tier's heal is authored above as an absolute figure - 40 HP at the
        // bottom, 82,000 at the top, a factor of 2,050 across ten tiers. The
        // damage it has to answer grows faster than that and, worse, it grows
        // in JUMPS: incoming damage is (monster attack - your armour), so for
        // as long as armour outpaces the region you take the 1 HP floor and
        // food is nearly free, and the moment it does not, demand explodes.
        // Measured against the season curve, fishing was 2% of a region's
        // playtime through region 3 and 756% of it in region 5. Neither number
        // describes a mechanic anyone designed.
        //
        // A share of MAX HP does not have that shape. Max HP is the same
        // quantity the damage is measured against, so a bite is worth a
        // constant fraction of the bar it refills no matter which region it is
        // eaten in, and food demand tracks "how fast is my health bar dropping"
        // instead of tracking the gap between two authored tables.
        //
        // The flat table stays as a FLOOR, which is what keeps the early game
        // recognisable: at level 1 a tier-1 minnow is worth more than 5% of a
        // 100 HP bar, so the authored number wins and nothing changes until the
        // player is big enough for the percentage to mean more.
        //
        // Tier still matters, and matters more than it did: a better fish is a
        // bigger share as well as a bigger number, so the reason to fish deeper
        // water is that you spend less time fishing.
        // Modul: 10, was 5.
        //
        // Raised as one half of a pair. Monster attack now sits ABOVE the
        // armour it faces, so ordinary hits land for a real amount instead of
        // the 1 HP floor with the crits carrying everything - which reads as
        // steady pressure rather than a lottery. That change spends food
        // one-for-one, and the health pool it is spent against is now measured
        // rather than guessed: CON grows about two points a level and pays 15
        // HP each, so a character runs roughly 100 HP at the start of the game
        // and 2,500 by region 5.
        //
        // For gathering to stay near a fifth of playtime, one fish has to cover
        // something like thirty-five seconds of combat. At five percent a tier
        // it covered ten. Twelve buys the attack change without handing the
        // larder the whole game, and it keeps deeper water worth fishing: at
        // twenty a tier-5 fish would already restore the whole bar and every
        // tier above it would be wasted, which is the wrong shape for a
        // profession the player is meant to keep investing in.
        public const int HealPercentOfMaxHpPerTier = 12;

        /// <summary>
        /// What one unit of this food restores to a character with the given
        /// effective max HP, in milli-HP. Pass 0 for the authored floor alone.
        /// </summary>
        public static int GetHealMilliHp(int itemId, long effectiveMaxMilliHp)
        {
            int flat = GetHealMilliHp(itemId);
            if (flat <= 0 || effectiveMaxMilliHp <= 0)
            {
                return flat;
            }

            int tier = GetTier(itemId);
            if (tier <= 0)
            {
                return flat;
            }

            long share = effectiveMaxMilliHp * tier * HealPercentOfMaxHpPerTier / 100L;
            if (share > int.MaxValue) share = int.MaxValue;
            return share > flat ? (int)share : flat;
        }

        /// <summary>
        /// The food tier an item eats at, 1-10, or 0 if it is not food. Shared
        /// by the payout lookup and the max-HP share above so a food cannot be
        /// worth one tier in one and another tier in the other.
        /// </summary>
        public static int GetTier(int itemId)
        {
            if (itemId <= 0 || itemId > ContentRegistry.ItemDefinitions.Length)
            {
                return 0;
            }

            if (ContentRegistry.IsRawFish(itemId))
            {
                int fishTier = ContentRegistry.ItemDefinitions[itemId - 1].RegionTier;
                return Math.Clamp(fishTier, 1, _healPayoutFlatHp.Length);
            }

            if (!ContentRegistry.GetItemBaseId(itemId).Contains("_food"))
            {
                return 0;
            }

            return Math.Clamp(ContentRegistry.ItemDefinitions[itemId - 1].RegionTier, 1, _healPayoutFlatHp.Length);
        }

        public static int GetHealMilliHp(int itemId)
        {
            if (itemId <= 0 || itemId > ContentRegistry.ItemDefinitions.Length)
            {
                return 0;
            }

            // Modul: raw fish is food. A caught fish heals by the tier of the
            // water it came out of, on the same curve cooked food uses - just
            // lower, because nobody cooked it. Without this the larder refused
            // every fish in the game and the only edible item was a recipe
            // output from a profession the design does not have.
            if (ContentRegistry.IsRawFish(itemId))
            {
                int fishTier = ContentRegistry.ItemDefinitions[itemId - 1].RegionTier;
                int fishIndex = Math.Clamp(fishTier - 1, 0, _healPayoutFlatHp.Length - 1);
                return _healPayoutFlatHp[fishIndex] * RawFishHealPercent * 1000 / 100;
            }

            // Legacy "_food_consumable" items carry no authored heal value, so
            // their RegionTier stands in for a cooking tier. Clamped into the
            // table rather than extrapolated - inventing a curve for four
            // unobtainable items would be worse than reusing the real one.
            string baseId = ContentRegistry.GetItemBaseId(itemId);
            if (!baseId.Contains("_food"))
            {
                return 0;
            }

            int regionTier = ContentRegistry.ItemDefinitions[itemId - 1].RegionTier;
            int tierIndex = Math.Clamp(regionTier - 1, 0, _healPayoutFlatHp.Length - 1);
            return _healPayoutFlatHp[tierIndex] * 1000;
        }

        // Modul: RATIONS, owner request 2026-10-07 ("fishing must matter again").
        //
        // Food demand used to be driven ONLY by damage taken. That is the right
        // model for sustain, and the wrong one for an economy: a character whose
        // affixes, rebirth power and armour keep it above the eat threshold
        // never takes a bite, so the larder filled once and fishing stopped
        // mattering. Production on 2026-10-07: a level-78 account sat on
        // 3 x 9,999 and had not fished in days, while GatheringShareTests (which
        // models gear with no affixes) still reported fishing at 20-42% of
        // playtime. The model was right about the gear it modelled; real
        // players are past it.
        //
        // A ration is upkeep, not healing: a fighting character eats one fish
        // every RationIntervalTicks whatever its health, and the fish restores
        // NOTHING. It deliberately does not heal, so the sustain ceiling (the
        // real difficulty knob, see the auto-eat cooldown) and the "Starved"
        // boss challenge (AteThisFight) are exactly what they were. A ration is
        // one fish of the monster's region or later, and doubles for every tier
        // below (RationCost) - otherwise the cheapest region-1 minnow at the
        // fastest node would feed region 5, and a flat refusal would have made
        // the 30,000 early fish a real player already held worthless overnight.
        //
        // Region 1 eats no rations: a new player owns ten starter fish and has
        // not been taught to fish yet, and region 1 is where that happens.
        //
        // Intervals: one fish every 10 s in region 2 down to every 6 s in
        // region 5 - 360 to 600 fish an hour. A region-typical node with
        // mastery and a tool catches roughly four times that, so about a fifth
        // of playtime goes to the river, GatheringShareTests' own design target.
        // A full larder (3 x 9,999) is 50-80 hours of fighting.
        //
        // Unpaid, the character is HUNGRY: it keeps fighting (an idle game must
        // not go quiet), auto-eat still heals from whatever is loaded, but every
        // kill pays HungryRewardPct of its XP and gold. Shown to the player as
        // the existing OutOfFood warning.
        public static int RationIntervalTicks(int monsterRegionTier) => monsterRegionTier switch
        {
            <= 1 => 0,
            2 => 100,
            3 => 80,
            4 => 70,
            _ => 60,
        };

        /// <summary>Share of a kill's XP and gold a hungry character is paid.</summary>
        public const int HungryRewardPct = 50;

        /// <summary>
        /// Fish a ration costs in food of <paramref name="foodTier"/> against a
        /// monster of <paramref name="monsterRegionTier"/>: one fish of the
        /// region or later, and twice as many for every tier below it (a
        /// region-2 eel in region 4 is four fish a ration). So a stockpile of
        /// early fish is still food - it just runs out fast - and the river of
        /// the region being fought in is always the cheap way to eat. 0 = not food.
        /// </summary>
        public static int RationCost(int foodTier, int monsterRegionTier)
        {
            if (foodTier <= 0) return 0;
            int below = monsterRegionTier - foodTier;
            return below <= 0 ? 1 : 1 << Math.Min(below, 20);
        }

        /// <summary>
        /// The larder slot (1-3) a ration is taken from, and how many fish it
        /// takes. Cheapest ration first; between equally cheap slots the lower
        /// tier, so a ration never burns the best fish while a poorer one that
        /// still costs a single fish is loaded. 0 when no slot holds enough for
        /// one ration - the character goes hungry.
        /// </summary>
        public static int PickRationSlot(int item1, int count1, int item2, int count2, int item3, int count3, int monsterRegionTier, out int cost)
        {
            int best = 0;
            int bestCost = int.MaxValue;
            int bestTier = int.MaxValue;
            Consider(1, item1, count1);
            Consider(2, item2, count2);
            Consider(3, item3, count3);
            cost = best == 0 ? 0 : bestCost;
            return best;

            void Consider(int slot, int itemId, int count)
            {
                int tier = GetTier(itemId);
                int slotCost = RationCost(tier, monsterRegionTier);
                if (slotCost <= 0 || count < slotCost) return;
                if (slotCost > bestCost || (slotCost == bestCost && tier >= bestTier)) return;
                best = slot;
                bestCost = slotCost;
                bestTier = tier;
            }
        }
    }
}
