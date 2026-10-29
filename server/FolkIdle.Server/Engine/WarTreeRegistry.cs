using System;
using System.Collections.Generic;

namespace FolkIdle.Server.Engine
{
    /// <summary>What a War node's ranks add to; see WarTreeRegistry.</summary>
    public enum WarEffect
    {
        /// <summary>A mechanic, wired in rework slice 2.3. Until then the node is pending and cannot be bought.</summary>
        Mechanic,
        DamagePct,
        CritChancePoints,
        CritDamagePct,
        AttackIntervalPct,
        MaxHpPct,
        ArmourPct,
        DodgePoints,
        BlockPoints,
        AccuracyPoints,
        LootLuckPct,
        GoldPct,
        XpPct,
        MaterialStackPct,
        RarityElevationPct,
        BossDamagePct,
        MonsterArmourCutPct,
        /// <summary>Chance per hit, in tenths of a percent, that the hit lands for double.</summary>
        CrushingChance,
        /// <summary>Extra damage, in tenths of a percent, on a hit against a monster at full health.</summary>
        FirstStrikePct,
        /// <summary>Extra damage, in tenths of a percent, against a monster below WarEffects.ExecuteBelowPct of its health.</summary>
        ExecutePct,
        /// <summary>Chance, in tenths of a percent, that a crit lands twice.</summary>
        DoubleStrikeChance,
        /// <summary>Chance, in tenths of a percent, that a crit repeats at WarEffects.EchoShare of its damage.</summary>
        EchoChance,
        /// <summary>Attack interval cut, in tenths of a percent of the crit chance.</summary>
        FrenzyOfCrit,
    }

    /// <summary>
    /// The War half of the reworked skill tree (docs/design/SKILL_TREE_REWORK.md
    /// sections 3-4): four columns of five tiers, forks, three bridges and a
    /// keystone per column, 35 nodes at ids 20-54 of the payload's rank buffer.
    /// </summary>
    /// <remarks>
    /// Modul: BUILT BESIDE TODAY'S TREE, NOT IN PLACE OF IT (slice 2.2). A
    /// payload whose SkillTreeVersion is 1 fights with these effects and every
    /// other payload with today's, so the harness can measure the War half on
    /// the same character without one live player noticing. Slice 2.5 flips
    /// the version for everyone, refunds the old nodes and deletes the old
    /// rules.
    ///
    /// The ids do not overlap today's 0-19 on purpose: a player's old ranks
    /// stay readable until the switch refunds them, and nothing has to be
    /// rewritten under a live session.
    ///
    /// Numbers are per rank, in tenths (15 = 1.5% or 1.5 points), and are the
    /// design doc's starting values - the harness tunes them in slice 2.4.
    /// </remarks>
    public static class WarTreeRegistry
    {
        public const int FirstId = 20;
        public const int Count = 35;

        public const int ColumnFortune = 0;
        public const int ColumnMight = 1;
        public const int ColumnPrecision = 2;
        public const int ColumnGuard = 3;
        public const int ColumnCount = 4;

        /// <summary>Points a column must hold before each row opens.</summary>
        public static readonly int[] RowGates = { 0, 4, 10, 16, 26 };

        /// <summary>A bridge needs this many points in BOTH columns it joins.</summary>
        public const int BridgeGate = 10;

        public static readonly string[] ColumnNames = { "Fortune", "Might", "Precision", "Guard" };
        public static readonly string[] ColumnIcons = { "col_fortune", "col_might", "col_precision", "col_guard" };

        // Node ids, in table order.
        public const int Scavenger = 20, GoldenTouch = 21, TrophyHunter = 22, Plenty = 23, Rarity = 24, QuickStudy = 25, CodexScholar = 26, GoldenFleece = 27;
        public const int Edge = 28, FirstStrike = 29, CrushingBlow = 30, Executioner = 31, Bossbreaker = 32, HeavyHand = 33, Thunderer = 34, Ruin = 35;
        public const int KeenEye = 36, CruelEdge = 37, Relentless = 38, Echo = 39, Frenzy = 40, SteadyHand = 41, DoubleStrike = 42, PerfectSequence = 43;
        public const int Vitality = 44, Ironhide = 45, Bloodthirst = 46, Evasion = 47, ShieldWall = 48, Riposte = 49, LastStand = 50, LivingFortress = 51;
        public const int KnowYourPrey = 52, PiercingCrit = 53, Counterstroke = 54;

        /// <summary>The table, indexed by rank-buffer slot; null where no War node sits.</summary>
        public static readonly WarNodeDef?[] Nodes = Build();

        public static WarNodeDef? Get(int id) => (uint)id < (uint)Nodes.Length ? Nodes[id] : null;

        public static IEnumerable<WarNodeDef> All()
        {
            foreach (var node in Nodes) if (node != null) yield return node;
        }

        private static WarNodeDef?[] Build()
        {
            var t = new WarNodeDef?[SkillRankBuffer.Length];
            void Add(int id, string name, string icon, int column, int row, int lane, string kind, int max, int cost, WarEffect effect, int perRank, string text, int fork = -1, int bridge = -1, int[]? ranks = null)
                => t[id] = new WarNodeDef
                {
                    Id = id, Name = name, Icon = icon, Column = column, Row = row, Lane = lane, Kind = kind,
                    MaxLevel = max, CostPerRank = cost, Effect = effect, PerRankTenths = perRank, EffectText = text,
                    Fork = fork, BridgeColumn = bridge, RankValues = ranks,
                };

            // ---- Fortune: farms richer, and grows stronger slowly --------------
            Add(Scavenger, "Scavenger", "scavenger", ColumnFortune, 0, 0, "Minor", 5, 1, WarEffect.LootLuckPct, 15, "+{0}% loot luck");
            Add(GoldenTouch, "Golden Touch", "golden_touch", ColumnFortune, 0, 1, "Minor", 5, 1, WarEffect.GoldPct, 30, "+{0}% gold from kills");
            Add(TrophyHunter, "Trophy Hunter", "trophy_hunter", ColumnFortune, 1, -1, "Notable", 3, 2, WarEffect.Mechanic, 0, "Bosses pay more gold and always drop a material");
            Add(Plenty, "Plenty", "plenty", ColumnFortune, 2, 0, "Fork", 5, 1, WarEffect.MaterialStackPct, 25, "+{0}% material stack size", fork: Rarity);
            Add(Rarity, "Rarity", "rarity", ColumnFortune, 2, 1, "Fork", 5, 1, WarEffect.RarityElevationPct, 15, "+{0}% chance a drop rolls one rarity higher", fork: Plenty);
            Add(QuickStudy, "Quick Study", "quick_study", ColumnFortune, 3, 0, "Minor", 5, 1, WarEffect.XpPct, 10, "+{0}% XP");
            Add(CodexScholar, "Codex Scholar", "codex_scholar", ColumnFortune, 3, 1, "Notable", 3, 2, WarEffect.Mechanic, 0, "Kills count extra toward codex levels");
            Add(GoldenFleece, "Golden Fleece", "golden_fleece", ColumnFortune, 4, -1, "Keystone", 1, 5, WarEffect.Mechanic, 0, "Every 100th kill drops an item two tiers up");

            // ---- Might: hits hard, breaks armour, kills bosses ----------------
            Add(Edge, "Edge", "edge", ColumnMight, 0, 0, "Minor", 5, 1, WarEffect.DamagePct, 15, "+{0}% damage");
            // Modul: NOT ARMOUR (owner, 2026-10-29). Breach cut monster armour and
            // Crushing Blow ignored it, and measured, armour lets 94-99% of a
            // Rare-geared hit through (penetration affixes cancel it), so both
            // were worth 0.4-1.6% DPS together. Crushing Blow doubles a hit
            // instead; First Strike rewards the opening hit, which pays in
            // short farming fights and barely moves a long boss wall.
            Add(FirstStrike, "First Strike", "breach", ColumnMight, 0, 1, "Minor", 5, 1, WarEffect.FirstStrikePct, 250, "+{0}% damage on the first hit against each monster");
            Add(CrushingBlow, "Crushing Blow", "crushing_blow", ColumnMight, 1, -1, "Notable", 3, 2, WarEffect.CrushingChance, 0, "{0}% chance a hit lands for double damage", ranks: new[] { 30, 60, 100 });
            Add(Executioner, "Executioner", "executioner", ColumnMight, 2, 0, "Fork", 5, 1, WarEffect.ExecutePct, 40, "+{0}% damage against monsters below 35% health", fork: Bossbreaker);
            Add(Bossbreaker, "Bossbreaker", "bossbreaker", ColumnMight, 2, 1, "Fork", 5, 1, WarEffect.BossDamagePct, 40, "+{0}% damage against region and world bosses", fork: Executioner);
            Add(HeavyHand, "Heavy Hand", "heavy_hand", ColumnMight, 3, 0, "Minor", 5, 1, WarEffect.DamagePct, 15, "+{0}% damage");
            Add(Thunderer, "Thunderer", "thunderer", ColumnMight, 3, 1, "Notable", 3, 2, WarEffect.Mechanic, 0, "Opens a boss fight with a heavy blow");
            Add(Ruin, "Ruin", "ruin", ColumnMight, 4, -1, "Keystone", 1, 5, WarEffect.Mechanic, 0, "Every 8th hit lands far harder; attack speed counts half");

            // ---- Precision: crits, tempo, repeated hits ------------------------
            Add(KeenEye, "Keen Eye", "keen_eye", ColumnPrecision, 0, 0, "Minor", 5, 1, WarEffect.CritChancePoints, 5, "+{0} crit chance");
            Add(CruelEdge, "Cruel Edge", "cruel_edge", ColumnPrecision, 0, 1, "Minor", 5, 1, WarEffect.CritDamagePct, 40, "+{0}% crit damage");
            Add(Relentless, "Relentless", "relentless", ColumnPrecision, 1, -1, "Notable", 3, 2, WarEffect.AttackIntervalPct, 30, "-{0}% attack interval");
            Add(Echo, "Echo", "echo", ColumnPrecision, 2, 0, "Fork", 5, 1, WarEffect.EchoChance, 80, "{0}% chance a crit repeats at half damage", fork: Frenzy);
            // Modul: FRENZY IS A CONVERSION, NOT A STATE. "After a crit, the next
            // swing comes sooner" needs a timer, and the player's and the
            // monster's swings share one accumulator - shifting it would move
            // the monster's swings too, and the projection could not price it.
            // So crit chance buys swing speed directly: the crit build still
            // gets faster, live and away alike.
            Add(Frenzy, "Frenzy", "frenzy", ColumnPrecision, 2, 1, "Fork", 5, 1, WarEffect.FrenzyOfCrit, 60, "Attack interval -{0}% of your crit chance", fork: Echo);
            Add(SteadyHand, "Steady Hand", "steady_hand", ColumnPrecision, 3, 0, "Minor", 5, 1, WarEffect.AccuracyPoints, 50, "+{0} accuracy");
            Add(DoubleStrike, "Double Strike", "double_strike", ColumnPrecision, 3, 1, "Notable", 3, 2, WarEffect.DoubleStrikeChance, 0, "{0}% chance a critical hit lands twice", ranks: new[] { 80, 160, 250 });
            Add(PerfectSequence, "Perfect Sequence", "perfect_sequence", ColumnPrecision, 4, -1, "Keystone", 1, 5, WarEffect.Mechanic, 0, "Every 6th swing is a guaranteed heavy crit");

            // ---- Guard: survives, and turns defence into damage ----------------
            Add(Vitality, "Vitality", "vitality", ColumnGuard, 0, 0, "Minor", 5, 1, WarEffect.MaxHpPct, 25, "+{0}% max health");
            Add(Ironhide, "Ironhide", "ironhide", ColumnGuard, 0, 1, "Minor", 5, 1, WarEffect.ArmourPct, 25, "+{0}% armour");
            Add(Bloodthirst, "Bloodthirst", "bloodthirst", ColumnGuard, 1, -1, "Notable", 3, 2, WarEffect.Mechanic, 0, "A share of the damage you deal heals you");
            Add(Evasion, "Evasion", "evasion", ColumnGuard, 2, 0, "Fork", 5, 1, WarEffect.DodgePoints, 15, "+{0} dodge", fork: ShieldWall);
            Add(ShieldWall, "Shield Wall", "shield_wall", ColumnGuard, 2, 1, "Fork", 5, 1, WarEffect.BlockPoints, 20, "+{0} block", fork: Evasion);
            Add(Riposte, "Riposte", "riposte", ColumnGuard, 3, 0, "Notable", 3, 2, WarEffect.Mechanic, 0, "A dodged or blocked blow answers back");
            Add(LastStand, "Last Stand", "last_stand", ColumnGuard, 3, 1, "Notable", 1, 3, WarEffect.Mechanic, 0, "Once an hour, a killing blow leaves you at 1 health");
            Add(LivingFortress, "Living Fortress", "living_fortress", ColumnGuard, 4, -1, "Keystone", 1, 5, WarEffect.Mechanic, 0, "Crit chance becomes block and the block cap rises; you never crit");

            // ---- Bridges: 10 points in both columns they join ------------------
            Add(KnowYourPrey, "Know Your Prey", "know_your_prey", ColumnFortune, 2, 2, "Bridge", 3, 2, WarEffect.Mechanic, 0, "More damage against monsters you know well", bridge: ColumnMight);
            Add(PiercingCrit, "Piercing Crit", "piercing_crit", ColumnMight, 2, 2, "Bridge", 3, 2, WarEffect.Mechanic, 0, "A crit ignores more monster armour", bridge: ColumnPrecision);
            Add(Counterstroke, "Counterstroke", "counterstroke", ColumnPrecision, 2, 2, "Bridge", 3, 2, WarEffect.Mechanic, 0, "After you dodge or block, the next swing is likelier to crit", bridge: ColumnGuard);

            return t;
        }

        /// <summary>
        /// Effect kinds the combat code reads today. A node of any other kind is
        /// pending: drawn, described, and refused, so no player pays for a bonus
        /// that does nothing (the "computed, never consumed" trap).
        /// </summary>
        private static readonly HashSet<WarEffect> Wired = new()
        {
            WarEffect.DamagePct, WarEffect.CritChancePoints, WarEffect.CritDamagePct, WarEffect.AttackIntervalPct,
            WarEffect.MaxHpPct, WarEffect.ArmourPct, WarEffect.DodgePoints, WarEffect.BlockPoints, WarEffect.AccuracyPoints,
            WarEffect.FirstStrikePct, WarEffect.CrushingChance, WarEffect.BossDamagePct, WarEffect.ExecutePct,
            WarEffect.DoubleStrikeChance, WarEffect.EchoChance, WarEffect.FrenzyOfCrit,
        };

        public static bool IsPending(int id) => Get(id) is { } n && !Wired.Contains(n.Effect);

        public static int CostOf(int id, int currentLevel)
            => Get(id) is { } n && currentLevel >= 0 && currentLevel < n.MaxLevel ? n.CostPerRank : 0;

        /// <summary>Points spent in a column; a bridge counts for both columns it joins.</summary>
        public static int PointsInColumn(ReadOnlySpan<byte> levels, int column)
        {
            int total = 0;
            foreach (var n in All())
            {
                if (n.Column != column && n.BridgeColumn != column) continue;
                total += Math.Min((int)levels[n.Id], n.MaxLevel) * n.CostPerRank;
            }
            return total;
        }

        public static string? BlockedReason(int id, ReadOnlySpan<byte> levels, int availablePoints)
        {
            if (Get(id) is not { } n) return "No such skill.";
            if (levels.Length < SkillRankBuffer.Length) return "Skill tree not loaded.";
            if (IsPending(id)) return "Not in the game yet - coming soon.";
            if (levels[id] >= n.MaxLevel) return "Already at its limit.";

            if (n.Kind == "Bridge")
            {
                int a = PointsInColumn(levels, n.Column), b = PointsInColumn(levels, n.BridgeColumn);
                if (a < BridgeGate || b < BridgeGate)
                    return $"Needs {BridgeGate} points in both {ColumnNames[n.Column]} and {ColumnNames[n.BridgeColumn]}.";
            }
            else
            {
                int gate = RowGates[n.Row];
                int have = PointsInColumn(levels, n.Column);
                if (have < gate) return $"Needs {gate} points in {ColumnNames[n.Column]}; {have} so far.";
            }

            if (n.Fork >= 0 && levels[n.Fork] > 0) return $"{Nodes[n.Fork]!.Name} was taken instead. One branch per fork.";

            if (availablePoints < n.CostPerRank) return $"Costs {n.CostPerRank} points; you have {availablePoints}.";
            return null;
        }

        /// <summary>The War half's PlanReason: replays purchases row by row, bridges with their row.</summary>
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
                // Bridges last within their row: their gate reads two columns'
                // points, which the row's ordinary nodes may still be raising.
                foreach (bool bridges in new[] { false, true })
                {
                    foreach (var n in All())
                    {
                        if (n.Row != row || (n.Kind == "Bridge") != bridges) continue;
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
            }
            return null;
        }

        /// <summary>Every War stat effect of a payload, summed, in tenths.</summary>
        public static WarEffects Resolve(in TickStatePayload payload)
        {
            var e = new WarEffects();
            if (payload.SkillTreeVersion != 1) return e;
            foreach (var n in All())
            {
                int rank = Math.Min((int)payload.SkillRanks[n.Id], n.MaxLevel);
                if (rank <= 0 || n.Effect == WarEffect.Mechanic) continue;
                int v = n.ValueAt(rank);
                switch (n.Effect)
                {
                    case WarEffect.DamagePct: e.DamageTenthsPct += v; break;
                    case WarEffect.CritChancePoints: e.CritChanceTenths += v; break;
                    case WarEffect.CritDamagePct: e.CritDamageTenthsPct += v; break;
                    case WarEffect.AttackIntervalPct: e.AttackIntervalTenthsPct += v; break;
                    case WarEffect.MaxHpPct: e.MaxHpTenthsPct += v; break;
                    case WarEffect.ArmourPct: e.ArmourTenthsPct += v; break;
                    case WarEffect.DodgePoints: e.DodgeTenths += v; break;
                    case WarEffect.BlockPoints: e.BlockTenths += v; break;
                    case WarEffect.AccuracyPoints: e.AccuracyTenths += v; break;
                    case WarEffect.FirstStrikePct: e.FirstStrikeTenthsPct += v; break;
                    case WarEffect.BossDamagePct: e.BossDamageTenthsPct += v; break;
                    case WarEffect.ExecutePct: e.ExecuteTenthsPct += v; break;
                    case WarEffect.DoubleStrikeChance: e.DoubleStrikeTenths += v; break;
                    case WarEffect.EchoChance: e.EchoTenths += v; break;
                    case WarEffect.FrenzyOfCrit: e.FrenzyTenths += v; break;
                    case WarEffect.CrushingChance: e.CrushingChanceTenths += v; break;
                }
            }
            return e;
        }
    }

    /// <summary>One War node; see WarTreeRegistry.</summary>
    public sealed class WarNodeDef
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Icon { get; init; } = string.Empty;
        public int Column { get; init; }
        /// <summary>The second column a bridge joins, or -1.</summary>
        public int BridgeColumn { get; init; } = -1;
        public int Row { get; init; }
        public int Lane { get; init; } = -1;
        /// <summary>"Minor", "Notable", "Fork", "Bridge" or "Keystone".</summary>
        public string Kind { get; init; } = "Minor";
        public int MaxLevel { get; init; }
        public int CostPerRank { get; init; }
        /// <summary>The other side of this node's fork, or -1.</summary>
        public int Fork { get; init; } = -1;
        public WarEffect Effect { get; init; }
        /// <summary>Per rank, in tenths of a percent or of a point.</summary>
        public int PerRankTenths { get; init; }
        /// <summary>The effect at a level; {0} is the total for a stat node.</summary>
        public string EffectText { get; init; } = string.Empty;
        /// <summary>The value at each rank when it is not linear (Crushing Blow's 3 / 6 / 10%); null for rank x PerRankTenths.</summary>
        public int[]? RankValues { get; init; }

        public int ValueAt(int rank)
            => rank <= 0 ? 0 : RankValues is { } list ? list[Math.Min(rank, list.Length) - 1] : rank * PerRankTenths;
    }

    /// <summary>A payload's War stat effects, summed, in tenths (15 = 1.5).</summary>
    public struct WarEffects
    {
        public int DamageTenthsPct;
        public int CritChanceTenths;
        public int CritDamageTenthsPct;
        public int AttackIntervalTenthsPct;
        public int MaxHpTenthsPct;
        public int ArmourTenthsPct;
        public int DodgeTenths;
        public int BlockTenths;
        public int AccuracyTenths;
        public int FirstStrikeTenthsPct;
        public int CrushingChanceTenths;
        public int BossDamageTenthsPct;
        public int ExecuteTenthsPct;
        public int DoubleStrikeTenths;
        public int EchoTenths;
        public int FrenzyTenths;

        /// <summary>Echo repeats a crit at this share of its damage, and never echoes an echo.</summary>
        public const double EchoShare = 0.5;

        public readonly double EchoChance => Math.Clamp(EchoTenths / 1000.0, 0.0, 1.0);

        /// <summary>Executioner's line: a monster below this share of its health takes the bonus.</summary>
        public const int ExecuteBelowPct = 35;

        /// <summary>
        /// Monster health as the swing count sees it under Executioner: the
        /// last ExecuteBelowPct of the bar falls faster by the bonus, which is
        /// the same as that part of the bar being smaller.
        /// </summary>
        public readonly long ExecuteAdjustedHp(long milliHp)
        {
            if (ExecuteTenthsPct <= 0 || milliHp <= 0) return milliHp;
            double tail = milliHp * ExecuteBelowPct / 100.0;
            return (long)(milliHp - tail + tail / (1.0 + ExecuteTenthsPct / 1000.0));
        }

        /// <summary>Crushing Blow doubles a hit: the multiplier it rolls.</summary>
        public const int CrushingMultiplier = 2;

        /// <summary>Crushing Blow's chance as a probability, 0-1.</summary>
        public readonly double CrushingChance => Math.Clamp(CrushingChanceTenths / 1000.0, 0.0, 1.0);
    }
}
