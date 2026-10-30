using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FolkIdle.Server.Engine
{
    /// <summary>What a Hearth node's ranks add to; see HearthTreeRegistry.</summary>
    public enum HearthEffect
    {
        /// <summary>Gathering speed, in tenths of a percent, for one profession or a pair.</summary>
        SpeedWood,
        SpeedMine,
        SpeedFish,
        SpeedHerb,
        SpeedWoodMine,
        SpeedFishHerb,
        /// <summary>Extra harvest rolls, in tenths of a percentage point, for wood and ore or fish and herbs.</summary>
        YieldWoodMine,
        YieldFishHerb,
        /// <summary>Rare find (the gathering luck weight), in tenths of a percent.</summary>
        RareWoodMine,
        RareFishHerb,
        /// <summary>Chance, in tenths of a percent, that a craft keeps its materials (the old Craft bough).</summary>
        KeepMaterialsPct,
        /// <summary>The reward rate on both paths, in tenths of a percent = permille (the old Scholar crown, through ScholarRate).</summary>
        OfflineRatePct,
        XpPct,
        // ---- Pending: drawn and described, refused until a reader exists ----
        CraftTimePct,
        CommissionRewardPct,
        CraftRarityPct,
        SalvageGoldPct,
        GreatWorksPct,
        BreedingCostPct,
        AptitudeMutationPct,
        ChildGrowthPct,
        /// <summary>Breeding cooldown cut, in tenths of a percent (Nursery, since 2026-10-30).</summary>
        BreedingCooldownPct,
        MentorXpPct,
        /// <summary>Keystones: the rank is the flag (step 4.5, or 4.6 for Masterwork).</summary>
        DeepVein,
        MoonlitCatch,
        Masterwork,
        BloodlineMemory,
    }

    /// <summary>
    /// The Hearth half of the skill tree (docs/design/ROADMAP_2026_10_30.md
    /// section 7.3): four columns in the War half's shape, 32 nodes at ids
    /// 56-87 of the payload's rank buffer, paid with craft points.
    /// </summary>
    /// <remarks>
    /// Modul: THE OLD INSIGHT LIMB COMES BACK HERE AT ITS OLD SIZE, NOT BIGGER
    /// (owner, 2026-10-30). The War switch emptied Craft, Harvest and Scholar,
    /// and their readers kept reading slots 13, 14 and 19, which nothing can
    /// raise any more. This half replaces those readers: gathering speed tops
    /// out at +16% a profession (the old Harvest), the keep-materials chance at
    /// 10% (the old Craft), the reward rate at +25% (the old Scholar).
    ///
    /// The yield and "pays twice" nodes start at HALF the proposal's numbers on
    /// purpose. #211 made loaded gatherers about 1.5x faster on 2026-10-29, and
    /// task 131 measures that a week later; stacking a full yield column on an
    /// economy still moving would be the "another 1.5x" roadmap 7.5 warns of.
    /// "Pays twice" is extra harvest rolls, the same units as yield, so its
    /// expectation is exactly its chance and the offline projection prices it.
    ///
    /// Rare find is twice the proposal's 1% a rank: gathering turns luck into
    /// a loot-table weight at a tenth, truncated (GatheringYield.LuckWeightBonusFor),
    /// so +5% was half a weight point that showed or vanished with the
    /// player's other luck - StatAuditTests caught Prospector and Netmaker
    /// moving nothing. At 2% a rank the full node is one whole weight point.
    ///
    /// Paid with craft points (HearthPoints), never with combat points, and no
    /// bridges into War. Numbers are per rank, in tenths, as WarTreeRegistry.
    /// </remarks>
    public static class HearthTreeRegistry
    {
        public const int FirstId = 56;
        public const int Count = 32;

        public const int ColumnWoodStone = 0;
        public const int ColumnWaterHerb = 1;
        public const int ColumnWorkshop = 2;
        public const int ColumnKin = 3;
        public const int ColumnCount = 4;

        /// <summary>Points a column must hold before each row opens - the War half's gates.</summary>
        public static readonly int[] RowGates = WarTreeRegistry.RowGates;

        public static readonly string[] ColumnNames = { "Wood & Stone", "Water & Herb", "Workshop", "Kin" };
        public static readonly string[] ColumnIcons = { "col_wood_stone", "col_water_herb", "col_workshop", "col_kin" };

        // Node ids, in table order.
        public const int SharpAxe = 56, HardPick = 57, SeamSense = 58, SteadySwing = 59, Clearcut = 60, Prospector = 61, DoubleHaul = 62, DeepVein = 63;
        public const int PatientLine = 64, GreenThumb = 65, GoodWaters = 66, StillWater = 67, KeenAngler = 68, Netmaker = 69, FullNet = 70, MoonlitCatch = 71;
        public const int QuickHands = 72, Thrifty = 73, Ledger = 74, BatchWork = 75, FineWork = 76, Salvager = 77, Patron = 78, Masterwork = 79;
        public const int Studious = 80, Homebody = 81, LongAbsence = 82, Matchmaker = 83, StrongBlood = 84, Nursery = 85, Mentor = 86, BloodlineMemory = 87;

        /// <summary>The table, indexed by rank-buffer slot; null where no Hearth node sits.</summary>
        public static readonly HearthNodeDef?[] Nodes = Build();

        public static HearthNodeDef? Get(int id) => (uint)id < (uint)Nodes.Length ? Nodes[id] : null;

        public static IEnumerable<HearthNodeDef> All()
        {
            foreach (var node in Nodes) if (node != null) yield return node;
        }

        private static HearthNodeDef?[] Build()
        {
            var t = new HearthNodeDef?[SkillRankBuffer.Length];
            void Add(int id, string name, string icon, int column, int row, int lane, string kind, int max, int cost, HearthEffect effect, int perRank, string text, int fork = -1, int[]? ranks = null)
                => t[id] = new HearthNodeDef
                {
                    Id = id, Name = name, Icon = icon, Column = column, Row = row, Lane = lane, Kind = kind,
                    MaxLevel = max, CostPerRank = cost, Effect = effect, PerRankTenths = perRank, EffectText = text,
                    Fork = fork, RankValues = ranks,
                };

            // ---- Wood & Stone: the axe and the pick ------------------------------
            Add(SharpAxe, "Sharp Axe", "sharp_axe", ColumnWoodStone, 0, 0, "Minor", 5, 1, HearthEffect.SpeedWood, 20, "+{0}% woodcutting speed");
            Add(HardPick, "Hard Pick", "hard_pick", ColumnWoodStone, 0, 1, "Minor", 5, 1, HearthEffect.SpeedMine, 20, "+{0}% mining speed");
            Add(SeamSense, "Seam Sense", "seam_sense", ColumnWoodStone, 1, -1, "Notable", 3, 2, HearthEffect.YieldWoodMine, 0, "+{0}% wood and ore yield", ranks: new[] { 20, 40, 60 });
            Add(SteadySwing, "Steady Swing", "steady_swing", ColumnWoodStone, 2, 0, "Fork", 5, 1, HearthEffect.SpeedWoodMine, 12, "+{0}% woodcutting and mining speed", fork: Clearcut);
            Add(Clearcut, "Clearcut", "clearcut", ColumnWoodStone, 2, 1, "Fork", 5, 1, HearthEffect.YieldWoodMine, 15, "+{0}% wood and ore yield", fork: SteadySwing);
            Add(Prospector, "Prospector", "prospector", ColumnWoodStone, 3, 0, "Minor", 5, 1, HearthEffect.RareWoodMine, 20, "+{0}% rare find for wood and ore");
            Add(DoubleHaul, "Double Haul", "double_haul", ColumnWoodStone, 3, 1, "Notable", 3, 2, HearthEffect.YieldWoodMine, 0, "{0}% chance a harvest of wood or ore pays twice", ranks: new[] { 30, 60, 90 });
            Add(DeepVein, "Deep Vein", "deep_vein", ColumnWoodStone, 4, -1, "Keystone", 1, 5, HearthEffect.DeepVein, 1, "Every 20th harvest also rolls the rare table");

            // ---- Water & Herb: the rod and the sickle ----------------------------
            Add(PatientLine, "Patient Line", "patient_line", ColumnWaterHerb, 0, 0, "Minor", 5, 1, HearthEffect.SpeedFish, 20, "+{0}% fishing speed");
            Add(GreenThumb, "Green Thumb", "green_thumb", ColumnWaterHerb, 0, 1, "Minor", 5, 1, HearthEffect.SpeedHerb, 20, "+{0}% herbalism speed");
            Add(GoodWaters, "Good Waters", "good_waters", ColumnWaterHerb, 1, -1, "Notable", 3, 2, HearthEffect.YieldFishHerb, 0, "+{0}% fish and herb yield", ranks: new[] { 20, 40, 60 });
            Add(StillWater, "Still Water", "still_water", ColumnWaterHerb, 2, 0, "Fork", 5, 1, HearthEffect.SpeedFishHerb, 12, "+{0}% fishing and herbalism speed", fork: KeenAngler);
            Add(KeenAngler, "Keen Angler", "keen_angler", ColumnWaterHerb, 2, 1, "Fork", 5, 1, HearthEffect.RareFishHerb, 30, "+{0}% rare find for fish and herbs", fork: StillWater);
            Add(Netmaker, "Netmaker", "netmaker", ColumnWaterHerb, 3, 0, "Minor", 5, 1, HearthEffect.RareFishHerb, 20, "+{0}% rare find for fish and herbs");
            Add(FullNet, "Full Net", "full_net", ColumnWaterHerb, 3, 1, "Notable", 3, 2, HearthEffect.YieldFishHerb, 0, "{0}% chance a catch of fish or herbs pays twice", ranks: new[] { 30, 60, 90 });
            // Modul: NOT "DOUBLES WHILE OFFLINE" (owner, 2026-10-30). That broke
            // the rule that an hour away pays what an hour watched pays.
            Add(MoonlitCatch, "Moonlit Catch", "moonlit_catch", ColumnWaterHerb, 4, -1, "Keystone", 1, 5, HearthEffect.MoonlitCatch, 1, "Every 20th catch is a rare find");

            // ---- Workshop: the bench -----------------------------------------------
            Add(QuickHands, "Quick Hands", "quick_hands", ColumnWorkshop, 0, 0, "Minor", 5, 1, HearthEffect.CraftTimePct, 20, "-{0}% craft time");
            Add(Thrifty, "Thrifty", "thrifty", ColumnWorkshop, 0, 1, "Minor", 5, 1, HearthEffect.KeepMaterialsPct, 20, "{0}% chance a craft keeps its materials");
            Add(Ledger, "Ledger", "ledger", ColumnWorkshop, 1, -1, "Notable", 3, 2, HearthEffect.CommissionRewardPct, 0, "+{0}% commission reward", ranks: new[] { 50, 100, 150 });
            Add(BatchWork, "Batch Work", "batch_work", ColumnWorkshop, 2, 0, "Fork", 5, 1, HearthEffect.CraftTimePct, 12, "-{0}% craft time", fork: FineWork);
            Add(FineWork, "Fine Work", "fine_work", ColumnWorkshop, 2, 1, "Fork", 5, 1, HearthEffect.CraftRarityPct, 10, "{0}% chance a crafted item rolls one rarity higher", fork: BatchWork);
            Add(Salvager, "Salvager", "salvager", ColumnWorkshop, 3, 0, "Minor", 5, 1, HearthEffect.SalvageGoldPct, 30, "+{0}% auto-salvage gold");
            Add(Patron, "Patron", "patron", ColumnWorkshop, 3, 1, "Notable", 3, 2, HearthEffect.GreatWorksPct, 0, "+{0}% Great Works progress per contribution", ranks: new[] { 50, 100, 150 });
            Add(Masterwork, "Masterwork", "masterwork", ColumnWorkshop, 4, -1, "Keystone", 1, 5, HearthEffect.Masterwork, 1, "A crafted item offers three legal affixes; keep one");

            // ---- Kin: the family and the time away -------------------------------
            Add(Studious, "Studious", "studious", ColumnKin, 0, 0, "Minor", 5, 1, HearthEffect.XpPct, 10, "+{0}% XP");
            Add(Homebody, "Homebody", "homebody", ColumnKin, 0, 1, "Minor", 5, 1, HearthEffect.OfflineRatePct, 25, "+{0}% reward rate, online and away");
            Add(LongAbsence, "Long Absence", "long_absence", ColumnKin, 1, -1, "Notable", 3, 2, HearthEffect.OfflineRatePct, 0, "+{0}% reward rate, online and away", ranks: new[] { 40, 80, 125 });
            Add(Matchmaker, "Matchmaker", "matchmaker", ColumnKin, 2, 0, "Fork", 5, 1, HearthEffect.BreedingCostPct, 50, "-{0}% breeding cost", fork: StrongBlood);
            Add(StrongBlood, "Strong Blood", "strong_blood", ColumnKin, 2, 1, "Fork", 5, 1, HearthEffect.AptitudeMutationPct, 20, "+{0}% chance a child's aptitude rolls +1", fork: Matchmaker);
            // Modul: NOT CHILD GROWTH (owner, 2026-10-30). Childhood lasts one
            // hour (AgePhaseCurve.ChildEndTicks), so +20% growth saved about ten
            // minutes - a node worth nothing. It cuts the breeding cooldown
            // instead, which is the Kin column's real loop.
            Add(Nursery, "Nursery", "nursery", ColumnKin, 3, 0, "Minor", 5, 1, HearthEffect.BreedingCooldownPct, 50, "-{0}% breeding cooldown");
            // Modul: NOT "CHARACTERS BELOW YOUR MAIN'S LEVEL" (2026-10-30). Level
            // and XP are the account's - the slot swap does not move them - so
            // that line could never pay. It pays while a child is growing up in
            // a slot (children age only on their own work), which is the moment
            // the Kin column is about.
            Add(Mentor, "Mentor", "mentor", ColumnKin, 3, 1, "Notable", 3, 2, HearthEffect.MentorXpPct, 0, "+{0}% XP while a child works in one of your slots", ranks: new[] { 50, 100, 150 });
            Add(BloodlineMemory, "Bloodline Memory", "bloodline_memory", ColumnKin, 4, -1, "Keystone", 1, 5, HearthEffect.BloodlineMemory, 1, "A newborn inherits +10% of each parent's aptitude");

            return t;
        }

        /// <summary>
        /// Effect kinds the simulation reads today. A node of any other kind is
        /// pending: drawn, described, and refused, so no player pays for a bonus
        /// that does nothing (the "computed, never consumed" trap).
        /// </summary>
        private static readonly HashSet<HearthEffect> Wired = new()
        {
            HearthEffect.SpeedWood, HearthEffect.SpeedMine, HearthEffect.SpeedFish, HearthEffect.SpeedHerb,
            HearthEffect.SpeedWoodMine, HearthEffect.SpeedFishHerb,
            HearthEffect.YieldWoodMine, HearthEffect.YieldFishHerb,
            HearthEffect.RareWoodMine, HearthEffect.RareFishHerb,
            HearthEffect.KeepMaterialsPct, HearthEffect.OfflineRatePct, HearthEffect.XpPct,
            HearthEffect.DeepVein, HearthEffect.MoonlitCatch, HearthEffect.CraftTimePct, HearthEffect.SalvageGoldPct,
            HearthEffect.BreedingCostPct, HearthEffect.AptitudeMutationPct, HearthEffect.GreatWorksPct, HearthEffect.BreedingCooldownPct,
            HearthEffect.MentorXpPct, HearthEffect.BloodlineMemory,
        };

        /// <summary>Deep Vein and Moonlit Catch fire on every Nth completed harvest.</summary>
        public const int KeystoneEvery = 20;

        public static bool IsPending(int id) => Get(id) is { } n && !Wired.Contains(n.Effect);

        /// <summary>Craft points a set of ranks cost, Hearth nodes only.</summary>
        public static int PointsSpent(ReadOnlySpan<byte> ranks)
        {
            int total = 0;
            foreach (var n in All())
                if (n.Id < ranks.Length) total += Math.Min((int)ranks[n.Id], n.MaxLevel) * n.CostPerRank;
            return total;
        }

        public static int CostOf(int id, int currentLevel)
            => Get(id) is { } n && currentLevel >= 0 && currentLevel < n.MaxLevel ? n.CostPerRank : 0;

        /// <summary>Craft points spent in a column.</summary>
        public static int PointsInColumn(ReadOnlySpan<byte> levels, int column)
        {
            int total = 0;
            foreach (var n in All())
                if (n.Column == column) total += Math.Min((int)levels[n.Id], n.MaxLevel) * n.CostPerRank;
            return total;
        }

        /// <summary>Why one more rank of a node cannot be bought, or null - WarTreeRegistry.BlockedReason without bridges.</summary>
        public static string? BlockedReason(int id, ReadOnlySpan<byte> levels, int availablePoints)
        {
            if (Get(id) is not { } n) return "No such skill.";
            if (levels.Length < SkillRankBuffer.Length) return "Skill tree not loaded.";
            if (IsPending(id)) return "Not in the game yet - coming soon.";
            if (levels[id] >= n.MaxLevel) return "Already at its limit.";

            int gate = RowGates[n.Row];
            int have = PointsInColumn(levels, n.Column);
            if (have < gate) return $"Needs {gate} points in {ColumnNames[n.Column]}; {have} so far.";

            if (n.Fork >= 0 && levels[n.Fork] > 0) return $"{Nodes[n.Fork]!.Name} was taken instead. One branch per fork.";

            if (availablePoints < n.CostPerRank) return $"Costs {n.CostPerRank} craft points; you have {availablePoints}.";
            return null;
        }

        /// <summary>The Hearth half's PlanReason: replays purchases row by row, as the War half's does.</summary>
        public static string? PlanReason(ReadOnlySpan<byte> current, ReadOnlySpan<byte> target, int availablePoints, out int cost)
        {
            cost = 0;
            if (current.Length < SkillRankBuffer.Length || target.Length < SkillRankBuffer.Length) return "Skill tree not loaded.";
            Span<byte> levels = stackalloc byte[SkillRankBuffer.Length];
            current.CopyTo(levels);

            foreach (var n in All())
                if (target[n.Id] < current[n.Id]) return $"{n.Name} cannot go down. Use a respec to take points back.";

            int points = availablePoints;
            for (int row = 0; row < RowGates.Length; row++)
            {
                foreach (var n in All())
                {
                    if (n.Row != row) continue;
                    while (levels[n.Id] < target[n.Id])
                    {
                        string? reason = BlockedReason(n.Id, levels, points);
                        if (reason != null) return $"{n.Name}: {reason}";
                        points -= n.CostPerRank;
                        cost += n.CostPerRank;
                        levels[n.Id]++;
                    }
                }
            }
            return null;
        }

        /// <summary>A node's effect at a level, as the screen prints it.</summary>
        public static string EffectAt(int id, int level)
        {
            if (Get(id) is not { } n || level <= 0) return string.Empty;
            double value = n.ValueAt(Math.Min(level, n.MaxLevel)) / 10.0;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, n.EffectText,
                value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Every wired Hearth effect of a payload, summed, in tenths.</summary>
        public static HearthEffects Resolve(in TickStatePayload payload)
        {
            var e = new HearthEffects();
            foreach (var n in All())
            {
                int rank = Math.Min((int)payload.SkillRanks[n.Id], n.MaxLevel);
                if (rank <= 0 || !Wired.Contains(n.Effect)) continue;
                int v = n.ValueAt(rank);
                switch (n.Effect)
                {
                    case HearthEffect.SpeedWood: e.SpeedWoodTenthsPct += v; break;
                    case HearthEffect.SpeedMine: e.SpeedMineTenthsPct += v; break;
                    case HearthEffect.SpeedFish: e.SpeedFishTenthsPct += v; break;
                    case HearthEffect.SpeedHerb: e.SpeedHerbTenthsPct += v; break;
                    case HearthEffect.SpeedWoodMine: e.SpeedWoodTenthsPct += v; e.SpeedMineTenthsPct += v; break;
                    case HearthEffect.SpeedFishHerb: e.SpeedFishTenthsPct += v; e.SpeedHerbTenthsPct += v; break;
                    case HearthEffect.YieldWoodMine: e.YieldWoodMineTenths += v; break;
                    case HearthEffect.YieldFishHerb: e.YieldFishHerbTenths += v; break;
                    case HearthEffect.RareWoodMine: e.RareWoodMineTenthsPct += v; break;
                    case HearthEffect.RareFishHerb: e.RareFishHerbTenthsPct += v; break;
                    case HearthEffect.KeepMaterialsPct: e.KeepMaterialsTenthsPct += v; break;
                    case HearthEffect.OfflineRatePct: e.RewardRatePermille += v; break;
                    case HearthEffect.XpPct: e.XpTenthsPct += v; break;
                    case HearthEffect.CraftTimePct: e.CraftTimeTenthsPct += v; break;
                    case HearthEffect.SalvageGoldPct: e.SalvageGoldTenthsPct += v; break;
                    case HearthEffect.MentorXpPct: e.MentorXpTenthsPct += v; break;
                    case HearthEffect.DeepVein: e.DeepVein = true; break;
                    case HearthEffect.MoonlitCatch: e.MoonlitCatch = true; break;
                }
            }
            return e;
        }

        /// <summary>
        /// A Hearth node's stored level, read from the table inside the caller's
        /// transaction. Breeding and crafting are database commands, not tick
        /// work, so they read the row rather than the live payload.
        /// </summary>
        public static Task<int> StoredLevelAsync(FolkIdle.Server.Models.FolkIdleDbContext db, long playerId, int nodeId)
            => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                System.Linq.Queryable.Select(
                    System.Linq.Queryable.Where(db.PlayerSkillTreeNodes, n => n.PlayerId == playerId && n.BranchId == nodeId),
                    n => n.Level));

        /// <summary>A node's value at a stored level, clamped to its ranks.</summary>
        public static int ValueAtLevel(int nodeId, int level)
            => Get(nodeId) is { } n ? n.ValueAt(Math.Clamp(level, 0, n.MaxLevel)) : 0;

        /// <summary>The Thrifty level a set of stored rows holds - crafting reads the table, not the payload.</summary>
        public static int KeepMaterialsTenthsPct(int thriftyLevel)
            => Nodes[Thrifty]!.ValueAt(Math.Clamp(thriftyLevel, 0, Nodes[Thrifty]!.MaxLevel));
    }

    public sealed class HearthNodeDef
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Icon { get; init; } = string.Empty;
        public int Column { get; init; }
        public int Row { get; init; }
        public int Lane { get; init; } = -1;
        /// <summary>"Minor", "Notable", "Fork" or "Keystone".</summary>
        public string Kind { get; init; } = "Minor";
        public int MaxLevel { get; init; }
        public int CostPerRank { get; init; }
        /// <summary>The other side of this node's fork, or -1.</summary>
        public int Fork { get; init; } = -1;
        public HearthEffect Effect { get; init; }
        /// <summary>Per rank, in tenths of a percent or of a point.</summary>
        public int PerRankTenths { get; init; }
        /// <summary>The effect at a level; {0} is the total for a stat node.</summary>
        public string EffectText { get; init; } = string.Empty;
        /// <summary>The value at each rank when it is not linear; null for rank x PerRankTenths.</summary>
        public int[]? RankValues { get; init; }

        public int ValueAt(int rank)
            => rank <= 0 ? 0 : RankValues is { } list ? list[Math.Min(rank, list.Length) - 1] : rank * PerRankTenths;
    }

    /// <summary>A payload's Hearth stat effects, summed, in tenths (15 = 1.5).</summary>
    public struct HearthEffects
    {
        public int SpeedWoodTenthsPct;
        public int SpeedMineTenthsPct;
        public int SpeedFishTenthsPct;
        public int SpeedHerbTenthsPct;
        public int YieldWoodMineTenths;
        public int YieldFishHerbTenths;
        public int RareWoodMineTenthsPct;
        public int RareFishHerbTenthsPct;
        public int KeepMaterialsTenthsPct;
        /// <summary>Tenths of a percent of the reward rate, which is permille: 250 = +25%.</summary>
        public int RewardRatePermille;
        public int XpTenthsPct;
        /// <summary>Craft time cut, in tenths of a percent (Quick Hands, Batch Work).</summary>
        public int CraftTimeTenthsPct;
        /// <summary>Extra auto-salvage gold, in tenths of a percent (Salvager).</summary>
        public int SalvageGoldTenthsPct;
        /// <summary>XP while a child works in a slot, in tenths of a percent (Mentor).</summary>
        public int MentorXpTenthsPct;
        /// <summary>Keystones: every KeystoneEvery-th harvest of wood or ore (Deep Vein) or fish or herbs (Moonlit Catch) adds a roll of the node's rarest entry.</summary>
        public bool DeepVein;
        public bool MoonlitCatch;

        /// <summary>Whether a keystone adds the rare roll for this profession.</summary>
        public readonly bool RareRollFor(int professionType) => professionType <= 1 ? DeepVein : MoonlitCatch;

        /// <summary>Gathering speed for a profession (0 wood, 1 mine, 2 fish, 3 herb), in tenths of a percent.</summary>
        public readonly int SpeedTenthsPctFor(int professionType) => professionType switch
        {
            0 => SpeedWoodTenthsPct,
            1 => SpeedMineTenthsPct,
            2 => SpeedFishTenthsPct,
            _ => SpeedHerbTenthsPct,
        };

        /// <summary>Extra harvest rolls for a profession, in tenths of a percentage point.</summary>
        public readonly int YieldTenthsFor(int professionType)
            => professionType <= 1 ? YieldWoodMineTenths : YieldFishHerbTenths;

        /// <summary>Rare find for a profession, in tenths of a percent.</summary>
        public readonly int RareFindTenthsPctFor(int professionType)
            => professionType <= 1 ? RareWoodMineTenthsPct : RareFishHerbTenthsPct;
    }
}
