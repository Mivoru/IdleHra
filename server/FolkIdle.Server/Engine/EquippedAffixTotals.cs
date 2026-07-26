namespace FolkIdle.Server.Engine
{
    // Modul: Affix System Unification. The summed affix contribution of every
    // equipped item, in one unmanaged value type.
    //
    // Previously this was four loose ints (attack/defense/crit/luck) threaded
    // through a tuple, the notification queue, TickStatePayload and a
    // 22-parameter StatsCalculator.Calculate call. Four was not enough to carry
    // the GDD's twelve affixes, so eight of them had nowhere to go and silently
    // did nothing. Bundling them keeps adding a stat to one place instead of
    // five.
    //
    // Percentage fields are TENTHS of a percent throughout, because the GDD
    // specifies growth increments of 0.5%, 1.5% and 0.3% per tier which whole
    // percent cannot represent without destroying the curve. Flat fields are
    // whole points.
    public struct EquippedAffixTotals
    {
        public int FlatAttack;
        public int FlatDefense;
        public int FlatHp;
        public int FlatArmorPenetration;

        public int DamageTenthsPct;
        public int AttackSpeedTenthsPct;
        public int CritChanceTenthsPct;
        public int CritDamageTenthsPct;
        public int LifestealTenthsPct;
        public int DodgeTenthsPct;
        public int BlockTenthsPct;
        public int LootLuckTenthsPct;
    }
}
