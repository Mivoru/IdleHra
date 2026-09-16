using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What a bloodline adds to a character's attack, health and gathering -
    /// aptitudes first, then traits - in ONE place for the live tick and the
    /// offline projection.
    ///
    /// Modul: these were hand-written twice, and the copies had drifted: the
    /// offline projection never applied the Strength aptitude at all. Both
    /// engines call this now, and BloodlineBonusesTests fails if either stops.
    /// </summary>
    public static class BloodlineBonuses
    {
        public static long ApplyAttack(long milliAttack, int strengthAptitude, in TraitTotals traits)
        {
            long effective = milliAttack;
            if (strengthAptitude > 0)
            {
                effective += (long)(effective * BreedingAptitudes.BonusPercentFor(strengthAptitude) / 100f);
            }
            if (traits.AttackPct != 0)
            {
                effective += effective * traits.AttackPct / 100;
            }
            return Math.Max(0L, effective);
        }

        public static long ApplyMaxHp(long milliHp, int enduranceAptitude, in TraitTotals traits)
        {
            long effective = milliHp;
            effective += (long)(effective * BreedingAptitudes.BonusPercentFor(enduranceAptitude) / 100f);
            if (traits.MaxHpPct != 0)
            {
                effective += effective * traits.MaxHpPct / 100;
            }
            return Math.Max(1L, effective);
        }

        public static int GatherSpeedBonusPct(int skillAptitude, in TraitTotals traits)
            => (int)BreedingAptitudes.BonusPercentFor(skillAptitude) + traits.GatherSpeedPct;

        public static int GatherYieldBonusPct(in TraitTotals traits) => traits.GatherYieldPct;
    }
}
