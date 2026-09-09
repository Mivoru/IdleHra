using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// THE DELVE: the rules, and nothing that touches a database or a socket.
    ///
    /// Modul: THIS EXISTS BECAUSE GOLD STOPPED BEING A CURRENCY.
    ///
    /// Measured by GoldSinkAffordabilityTests before a line of this was
    /// written: a player keeping up in region 5 earns 369,000 gold an hour, and
    /// every RECURRING sink in the game costs under 3% of that - a reroll is
    /// 2.7%, a fusion fee 1.1%, a child 0.1%. The only heavy sink, the village
    /// feast, is one-off per villager. So gold at the top of the game is a
    /// counter rather than a currency, and no amount of retuning the existing
    /// sinks fixes that: they are all attached to actions a player takes a
    /// handful of times.
    ///
    /// The opposite is true at the bottom - "I earned 100,000 overnight and
    /// five rerolls took all of it" is the report those tests were written for
    /// - so a flat price is wrong in both directions. The entry fee here is
    /// pinned to the player's own region income instead, at roughly forty
    /// minutes of it, which is the same number at every stage of the game.
    ///
    /// WHY THE REWARD IS NOT ALSO SCALED BY REGION. It is scaled by the
    /// CHARACTER, which is better: the floor requirements below are absolute,
    /// so how deep a run goes is decided by the attribute sheet the player
    /// built. A region-1 character clears three or four floors; a maxed one can
    /// reach eight. That makes the Delve a reason to place attribute points
    /// rather than a second currency faucet bolted onto the side of the game.
    ///
    /// WHY DIAMONDS ARE CAPPED PER WEEK. Diamonds are a PURCHASED currency -
    /// the smallest pack is 500 and the premium Chronicle pass costs 950. An
    /// uncapped gold-to-diamond conversion would turn the surplus this exists
    /// to absorb into free premium currency and undercut the store. The ceiling
    /// is MaxDiamondsPerWeek, and past it a run still pays (see
    /// ConsolationGoldFraction) so the SINK keeps working after the TAP closes.
    /// A tap is much harder to take away than to never open.
    /// </summary>
    public static class DelveRegistry
    {
        /// <summary>Floors in one run. Reaching the eighth is a full clear.</summary>
        public const int FloorCount = 8;

        /// <summary>Doors offered on each floor. One is chosen; the rest are never resolved.</summary>
        public const int DoorsPerFloor = 3;

        /// <summary>
        /// Failed checks a run survives. The third failure ends it with nothing
        /// banked - which is what makes the bank-or-push decision a decision.
        /// </summary>
        public const int LanternCharges = 3;

        /// <summary>
        /// Embers convert to diamonds at this rate on banking. Chosen so a full
        /// eight-floor clear pays about 20 diamonds and a typical bank at floor
        /// five pays 6 - three full clears reach the weekly ceiling.
        /// </summary>
        public const int EmbersPerDiamond = 18;

        /// <summary>
        /// Modul: THE TAP'S CEILING, and it ships in the first version.
        ///
        /// 60 a week is about 780 across a 90-day season - less than one
        /// premium Chronicle pass (950) and a sixth of the smallest purchasable
        /// pack per week. Enough that grinding it is worth doing, nowhere near
        /// enough to replace buying, which is the line this has to stay under.
        /// </summary>
        public const int MaxDiamondsPerWeek = 60;

        /// <summary>
        /// What a run pays once the weekly ceiling is reached: this fraction of
        /// the entry fee back, scaled by how deep the run went.
        ///
        /// Modul: NOT ZERO, AND NOT A REFUSAL. Refusing entry at the ceiling
        /// would close the sink exactly when it is working - the player with
        /// gold to burn is the player who has already hit the cap. Paying part
        /// of the fee back keeps the run worth playing while the diamonds stop,
        /// and it still destroys gold on net at every depth.
        /// </summary>
        public const double ConsolationGoldFraction = 0.5;

        /// <summary>
        /// Gold to enter, by the player's highest unlocked region.
        ///
        /// About forty minutes of that region's own income, from the table
        /// GoldSinkAffordabilityTests prints:
        ///
        ///   region 1   10,440 g/h  ->    7,000
        ///   region 2   25,560 g/h  ->   17,000
        ///   region 3   62,280 g/h  ->   42,000
        ///   region 4  152,100 g/h  ->  100,000
        ///   region 5  369,000 g/h  ->  250,000
        ///
        /// Asserted back against measured income in
        /// GoldSinkAffordabilityTests.TheDelveCostsAboutAnEveningPerRegion, in
        /// minutes of play, because a number a test prints is not a number a
        /// test checks.
        /// </summary>
        private static readonly long[] EntryFeeByRegion = { 7_000, 17_000, 42_000, 100_000, 250_000 };

        public static long EntryFeeForRegion(int highestUnlockedRegion)
        {
            int index = Math.Clamp(highestUnlockedRegion, 1, EntryFeeByRegion.Length) - 1;
            return EntryFeeByRegion[index];
        }

        /// <summary>
        /// The attribute value a floor asks for. 20 at the mouth, 269 at the
        /// bottom, on a 1.45 geometric step.
        ///
        /// Modul: PINNED TO THE MILESTONE LADDER, not invented. AttributeRegistry
        /// .Thresholds is 25/60/120/200/300, so floor 2 is roughly the first
        /// milestone, floor 5 the third, and floor 8 sits just under the last -
        /// a character that has maxed one attribute can reliably run that
        /// attribute's doors to the bottom, and no character can walk every
        /// door of floor 8. That is the intended shape: depth is bought with
        /// BREADTH of attributes, which is the one thing the sheet could not
        /// previously be spent on.
        /// </summary>
        public static int RequirementForFloor(int floor)
        {
            int f = Math.Clamp(floor, 1, FloorCount);
            return (int)Math.Round(20.0 * Math.Pow(1.45, f - 1));
        }

        /// <summary>
        /// Chance that a check against <paramref name="attributeValue"/> passes
        /// on the given floor.
        ///
        /// Modul: A DIMINISHING CURVE, DELIBERATELY - PowerCeilingTests calls
        /// linear-and-uncapped the one shape that is never allowed, and this is
        /// a lever a player can pour three hundred points into.
        ///
        /// (v - r) / (v + r) is 0 when the sheet exactly meets the floor and
        /// approaches 1 as it outgrows it, so the odds move fast at the start
        /// and barely at the end.
        ///
        /// Modul: THE CEILING IS THE CURVE'S OWN ASYMPTOTE, not a clamp bolted
        /// on top. 0.5 + 0.4 * edge cannot exceed 0.90 however large the sheet
        /// gets, so no attribute total ever makes a run free - and the first
        /// draft's MaxSuccessChance of 0.92 was a number the clamp could never
        /// reach, which is a constant that reads like a rule and enforces
        /// nothing. The floor of 0.25 IS a real clamp, and it fires: it keeps a
        /// hopeless door a gamble rather than a wall.
        /// </summary>
        public static double SuccessChance(int attributeValue, int floor)
        {
            double v = Math.Max(0, attributeValue);
            double r = RequirementForFloor(floor);
            if (v + r <= 0) return MinSuccessChance;

            double edge = (v - r) / (v + r);
            return Math.Clamp(0.5 + 0.4 * edge, MinSuccessChance, MaxSuccessChance);
        }

        public const double MinSuccessChance = 0.25;

        /// <summary>The value SuccessChance converges on, never exceeds, and only reaches in the limit.</summary>
        public const double MaxSuccessChance = 0.90;

        /// <summary>
        /// Chance that a door SHOWS which attribute it wants.
        ///
        /// Modul: FORTUNE'S SECOND HOME. LCK's whole identity was the loot roll
        /// - rarer drops and the elevation chance - which is invisible in the
        /// moment and impossible to feel. Here it is the difference between
        /// choosing and guessing, decided before every door, and the player
        /// watches it happen.
        ///
        /// Same square root the rest of Fortune's effects use, so a point is
        /// worth less the more you hold and the reveal rate can never reach
        /// certainty: 55% bare, 77% at 100 Fortune, and the 90% cap arrives at
        /// about 253 - just inside the last milestone, so the curve is still
        /// paying across the whole range a sheet can actually reach.
        /// </summary>
        public static double DoorRevealChance(int fortune)
        {
            double bonus = 0.022 * Math.Sqrt(Math.Max(0, fortune));
            return Math.Clamp(0.55 + bonus, 0.55, 0.90);
        }

        /// <summary>Embers banked for clearing a floor: 5, 10, 15 ... 40.</summary>
        public static int EmbersForClearingFloor(int floor) => 5 * Math.Clamp(floor, 1, FloorCount);

        /// <summary>Embers a run holds having cleared every floor up to and including this one.</summary>
        public static int CumulativeEmbers(int floorsCleared)
        {
            int total = 0;
            for (int f = 1; f <= Math.Clamp(floorsCleared, 0, FloorCount); f++) total += EmbersForClearingFloor(f);
            return total;
        }

        /// <summary>
        /// The multiplier applied to banked embers when walking out after this
        /// many floors. 1.00 at one floor, 2.05 at eight.
        ///
        /// Modul: THIS IS THE WHOLE GAME. Pushing raises the multiplier on
        /// everything already banked, so a failure costs more the deeper it
        /// happens - and the arithmetic is public, so the decision is a real
        /// one rather than a guess. It is the tension Farkle runs on, and the
        /// reason this is not a slot machine with extra steps.
        /// </summary>
        public static double BankMultiplier(int floorsCleared)
            => 1.0 + 0.15 * (Math.Clamp(floorsCleared, 1, FloorCount) - 1);

        /// <summary>Diamonds a run pays out if banked now, before the weekly ceiling is applied.</summary>
        public static int DiamondsForBanking(int floorsCleared)
        {
            if (floorsCleared <= 0) return 0;
            double embers = CumulativeEmbers(floorsCleared) * BankMultiplier(floorsCleared);
            return (int)Math.Floor(embers / EmbersPerDiamond);
        }

        /// <summary>
        /// Gold returned instead of diamonds once the weekly ceiling is spent,
        /// scaled by depth so a deep run is still worth more than a shallow one.
        /// </summary>
        public static long ConsolationGold(long entryFee, int floorsCleared)
        {
            if (floorsCleared <= 0) return 0;
            double depth = (double)Math.Clamp(floorsCleared, 1, FloorCount) / FloorCount;
            return (long)Math.Floor(entryFee * ConsolationGoldFraction * depth);
        }
    }
}
