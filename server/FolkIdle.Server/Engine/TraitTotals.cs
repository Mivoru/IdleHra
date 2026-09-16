using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What a trait mask adds up to, capped per stat.
    ///
    /// Modul: EVERY MULTIPLIER DECLARES A CAP (CLAUDE.md, PowerCeilingTests).
    /// Three traits could stack two attack traits; the cap keeps a bloodline
    /// "noticeable, not decisive" beside aptitudes that top out at +45%.
    /// A value type computed on demand: fourteen definitions and a few ifs, so
    /// no cache that could go stale when a character is fielded.
    /// </summary>
    public readonly struct TraitTotals
    {
        public const int PositiveCap = 20;
        public const int NegativeFloor = -15;

        public int MaxHpPct { get; init; }
        public int AttackPct { get; init; }
        public int GatherSpeedPct { get; init; }
        public int GatherYieldPct { get; init; }
        public int CritChancePoints { get; init; }
        public int AttackSpeedPct { get; init; }
        public int DodgePoints { get; init; }
        public int LifestealPct { get; init; }
        public int RarityElevationPoints { get; init; }

        public static TraitTotals From(long mask)
        {
            if ((mask & TraitRegistry.KnownBitsMask) == 0L) return default;

            int hp = 0, attack = 0, gatherSpeed = 0, gatherYield = 0, crit = 0, speed = 0, dodge = 0, lifesteal = 0, rarity = 0;
            foreach (var t in TraitRegistry.All)
            {
                if (!TraitRegistry.Has(mask, t.Bit)) continue;
                switch (t.Effect)
                {
                    case TraitEffect.MaxHpPct: hp += t.Value; break;
                    case TraitEffect.AttackPct: attack += t.Value; break;
                    case TraitEffect.GatherSpeedPct: gatherSpeed += t.Value; break;
                    case TraitEffect.GatherYieldPct: gatherYield += t.Value; break;
                    case TraitEffect.CritChancePoints: crit += t.Value; break;
                    case TraitEffect.AttackSpeedPct: speed += t.Value; break;
                    case TraitEffect.DodgePoints: dodge += t.Value; break;
                    case TraitEffect.LifestealPct: lifesteal += t.Value; break;
                    case TraitEffect.RarityElevationPoints: rarity += t.Value; break;
                }
            }

            return new TraitTotals
            {
                MaxHpPct = Clamp(hp),
                AttackPct = Clamp(attack),
                GatherSpeedPct = Clamp(gatherSpeed),
                GatherYieldPct = Clamp(gatherYield),
                CritChancePoints = Clamp(crit),
                AttackSpeedPct = Clamp(speed),
                DodgePoints = Clamp(dodge),
                LifestealPct = Clamp(lifesteal),
                RarityElevationPoints = Clamp(rarity),
            };
        }

        private static int Clamp(int value) => Math.Clamp(value, NegativeFloor, PositiveCap);
    }
}
