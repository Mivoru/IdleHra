using System;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>What one Great Work is: its name, what each stage pays, and where it stands on the Home map.</summary>
    public readonly record struct GreatWorkDefinition(
        int Region, string Name, int YieldPctPerStage, int OfflineMinutesPerStage);

    /// <summary>
    /// Task 84 (owner decision 2026-09-30): the long material sink. Five SOLO
    /// monuments, one per region, five stages each; every stage eats the region's
    /// own common log and ore and pays one small PERMANENT bonus that survives a
    /// rebirth.
    ///
    /// Modul: EVERY NUMBER LIVES HERE AND THE BONUSES ARE DERIVED FROM STAGES.
    /// Nothing stores a bonus. The tick caches only the packed stage counts and
    /// asks <see cref="YieldPct"/> / <see cref="OfflineMinutes"/> at the moment it
    /// needs one, so a retuned per-stage figure moves every player at once and the
    /// stored fact (a stage was built) can never disagree with what it pays.
    /// Both bonuses are read by the SAME helpers on the live and the offline path
    /// (owner rule "offline = online"): the yield through
    /// SimulationEngine.GatheringYieldFor - the one composition the live tick and
    /// OfflineSimulationEngine both call - and the cap through
    /// OfflineSimulationEngine.EffectiveOfflineCapSeconds, which the offline
    /// window, the wire's OfflineCapSeconds and OfflineCapNotifier all call.
    /// PowerCeilingTests holds both against <see cref="MaxYieldPct"/> and
    /// <see cref="MaxOfflineMinutes"/>.
    ///
    /// The bonuses are HARD CAPPED by construction (five stages of a fixed
    /// figure): no lever here is linear-and-uncapped.
    /// </summary>
    public static class GreatWorksRegistry
    {
        public const int FirstRegion = 1;
        public const int LastRegion = 5;
        public const int StageCount = 5;

        /// <summary>Bits per region in the packed stage cache: stages are 0-5.</summary>
        private const int BitsPerRegion = 3;

        /// <summary>
        /// What each stage eats, in units of the region's materials (owner
        /// range: 50,000 to 2,000,000). The FIRST stage is a start a player can
        /// make in a day or two; the last is the long game. Every unit is a
        /// common log or common ore of the monument's own region.
        /// </summary>
        public static readonly long[] StageCosts = { 50_000L, 150_000L, 400_000L, 1_000_000L, 2_000_000L };

        public static readonly string[] StageNames = { "Foundation", "Pillars", "Walls", "Roof", "Beacon" };

        /// <summary>
        /// The five monuments. Yield is in whole percentage points of extra
        /// harvest rolls (the unit of every other yield bonus in
        /// GatheringYieldFor); offline is minutes of extra away-time cap.
        /// Regions 1, 3 and 5 pay yield, 2, 4 and 5 pay away-time, so the
        /// choice of where to spend a week of gathering is a real one.
        /// </summary>
        public static readonly GreatWorkDefinition[] Monuments =
        {
            new(1, "Birchwood Cairn", YieldPctPerStage: 1, OfflineMinutesPerStage: 0),
            new(2, "Willow Hearth", YieldPctPerStage: 0, OfflineMinutesPerStage: 15),
            new(3, "Acacia Gate", YieldPctPerStage: 1, OfflineMinutesPerStage: 0),
            new(4, "Frostpine Beacon", YieldPctPerStage: 0, OfflineMinutesPerStage: 15),
            new(5, "The Ebon Crown", YieldPctPerStage: 1, OfflineMinutesPerStage: 15),
        };

        public static bool IsValidRegion(int region) => region >= FirstRegion && region <= LastRegion;

        public static GreatWorkDefinition MonumentOf(int region) => Monuments[region - FirstRegion];

        /// <summary>Which material a deposit names: 0 the region's common log, 1 its common ore.</summary>
        public const int MaterialLog = 0;
        public const int MaterialOre = 1;

        /// <summary>
        /// The item a deposit spends. Read from VillageManagementEngine's
        /// tier table - the same pairing the gathering loot tables and the
        /// guild buffs use - rather than restated, so a monument can never ask
        /// for a material no node in its region pays.
        /// </summary>
        public static string MaterialFor(int region, int kind)
        {
            var (log, ore, _, _) = VillageManagementEngine.GetTierMaterials((region - FirstRegion) * 5);
            return kind == MaterialOre ? ore : log;
        }

        public static bool IsValidMaterialKind(int kind) => kind == MaterialLog || kind == MaterialOre;

        // ----- the packed stage cache (three bits a region) -------------------

        public static int StageOf(int packed, int region)
            => IsValidRegion(region) ? (packed >> ((region - FirstRegion) * BitsPerRegion)) & 0x7 : 0;

        public static int WithStage(int packed, int region, int stage)
        {
            if (!IsValidRegion(region)) return packed;
            int shift = (region - FirstRegion) * BitsPerRegion;
            return (packed & ~(0x7 << shift)) | (Math.Clamp(stage, 0, StageCount) << shift);
        }

        // ----- what the stages pay --------------------------------------------

        /// <summary>Extra harvest-roll percentage points from every built stage.</summary>
        public static int YieldPct(int packed)
        {
            int total = 0;
            foreach (var m in Monuments) total += m.YieldPctPerStage * StageOf(packed, m.Region);
            return total;
        }

        /// <summary>Extra minutes on the offline cap from every built stage.</summary>
        public static int OfflineMinutes(int packed)
        {
            int total = 0;
            foreach (var m in Monuments) total += m.OfflineMinutesPerStage * StageOf(packed, m.Region);
            return total;
        }

        /// <summary>The ceiling of <see cref="YieldPct"/>: every monument complete. PowerCeilingTests asserts against it.</summary>
        public static int MaxYieldPct { get { int t = 0; foreach (var m in Monuments) t += m.YieldPctPerStage * StageCount; return t; } }

        /// <summary>The ceiling of <see cref="OfflineMinutes"/>.</summary>
        public static int MaxOfflineMinutes { get { int t = 0; foreach (var m in Monuments) t += m.OfflineMinutesPerStage * StageCount; return t; } }

        /// <summary>What ONE stage of a monument pays, as a sentence the panel shows.</summary>
        public static string DescribeStageBonus(int region)
        {
            var m = MonumentOf(region);
            var parts = new System.Collections.Generic.List<string>();
            if (m.YieldPctPerStage > 0) parts.Add($"+{m.YieldPctPerStage}% gathering yield");
            if (m.OfflineMinutesPerStage > 0) parts.Add($"+{m.OfflineMinutesPerStage} min offline limit");
            return string.Join(" and ", parts);
        }
    }
}
