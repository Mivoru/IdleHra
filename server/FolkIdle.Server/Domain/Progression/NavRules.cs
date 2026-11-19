using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The Descent into Nav (proposal 2026-11-19, docs/design/AUDIT_AND_ENDGAME_2026_11_19.md
    /// section 4): an endless ladder of depths below the World Tree's roots.
    /// Every number of the mode lives here, so tuning is one file.
    /// </summary>
    /// <remarks>
    /// Modul: THE ENDLESS NUMBER. Every other ladder in the game has a top -
    /// region 5, Ascension step 10, dungeon stage 15, aptitude 50 - and on
    /// production the best account killed the last boss in 0.9 s and one
    /// account finished breeding in six days. A depth has no top: guardian
    /// health grows 10% a depth, so every 10% more damage is one depth more,
    /// forever.
    ///
    /// Modul: NAV-ONLY BONUSES. The Workshop and the research make you stronger
    /// INSIDE Nav and nowhere else, so building the mode does not re-open the
    /// balance of regions 1-5, the dungeon or the world boss.
    ///
    /// Modul: OFF UNLESS FOLKIDLE_NAV=on. Souls and the gold research are
    /// economy decisions the owner has not taken yet; production stays closed
    /// until they are.
    /// </remarks>
    public static class NavRules
    {
        // --- the gate -------------------------------------------------------

        public static bool Enabled() => Enabled(Environment.GetEnvironmentVariable("FOLKIDLE_NAV"));

        public static bool Enabled(string? flag) => string.Equals(flag, "on", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Nav opens with Malakor's fall: depth 1 is a guardian of his size, so
        /// a character who has not beaten him would clear nothing and spend a
        /// descent for zero Souls (the dev fixture did, 2026-11-19).
        /// </summary>
        public static bool IsOpen(byte defeatedRegionBossMask)
            => Combat.BossFirstClearRules.IsDefeated(defeatedRegionBossMask, Engine.RaceUnlockRegistry.GetRegionBossMonsterId(Engine.RaceUnlockRegistry.LastRegion));

        public const string LockedReason = "Nav opens when you beat Malakor, the Shadow Citadel's boss.";

        // --- the guardians --------------------------------------------------

        /// <summary>Health of the depth-1 guardian of Circle 1, whole hit points: Malakor's, rounded up.</summary>
        public const double FirstGuardianHp = 1_000_000;

        /// <summary>Guardian health per depth.</summary>
        public const double HpGrowth = 1.10;

        /// <summary>Guardian attack per depth, in the same units as a monster's AttackPower.</summary>
        public const double AttackGrowth = 1.07;

        /// <summary>The depth-1 guardian's attack: four fifths of Malakor's (355,200).</summary>
        public const double FirstGuardianAttack = 284_000;

        /// <summary>A guardian swings every two seconds, like most bosses.</summary>
        public const int GuardianIntervalMs = 2000;

        /// <summary>Seconds of fight clock a guardian may last before the run ends.</summary>
        public const double SecondsPerGuardian = 30.0;

        /// <summary>Each Circle multiplies guardian health and attack by this.</summary>
        public const double CircleStrength = 5.0;

        /// <summary>Each Circle multiplies Souls by this.</summary>
        public const double CircleSouls = 3.0;

        /// <summary>The depth that opens the next Circle.</summary>
        public const int CircleOpensAt = 50;

        /// <summary>A run stops here whatever happens, so a request is always bounded.</summary>
        public const int MaxDepthPerRun = 2000;

        public static double GuardianHp(int circle, int depth)
            => FirstGuardianHp * Math.Pow(HpGrowth, depth - 1) * Math.Pow(CircleStrength, Math.Max(0, circle - 1));

        public static double GuardianAttack(int circle, int depth)
            => FirstGuardianAttack * Math.Pow(AttackGrowth, depth - 1) * Math.Pow(CircleStrength, Math.Max(0, circle - 1));

        /// <summary>The canonical monster whose art and armour a depth's guardian borrows: the 25 cycle, region 5 first.</summary>
        public static int GuardianTemplateId(int depth)
        {
            // Malakor's region first (111-115), then back up through the others.
            int[] order = { 111, 112, 113, 114, 115, 106, 107, 108, 109, 110, 101, 102, 103, 104, 105, 96, 97, 98, 99, 100, 91, 92, 93, 94, 95 };
            return order[(Math.Max(1, depth) - 1) % order.Length];
        }

        // --- the elite modifiers ---------------------------------------------

        public enum Elite : byte { None = 0, Armoured = 1, Swift = 2, Brute = 3, Frail = 4 }

        /// <summary>A depth's modifier, fixed by the run's seed so a run replays the same.</summary>
        public static Elite EliteFor(int runSeed, int depth)
        {
            if (depth < 5) return Elite.None;
            uint h = unchecked((uint)(runSeed * 73856093) ^ (uint)(depth * 19349663));
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h % 100) switch
            {
                < 55 => Elite.None,
                < 70 => Elite.Armoured,
                < 82 => Elite.Swift,
                < 94 => Elite.Brute,
                _ => Elite.Frail,
            };
        }

        /// <summary>(health factor, attack factor, guardian speed factor) of a modifier.</summary>
        public static (double Hp, double Attack, double Speed) EliteFactors(Elite elite) => elite switch
        {
            Elite.Armoured => (1.5, 1.0, 1.0),
            Elite.Swift => (1.0, 1.0, 1.6),
            Elite.Brute => (1.0, 1.7, 1.0),
            Elite.Frail => (0.6, 1.0, 1.0),
            _ => (1.0, 1.0, 1.0),
        };

        public static string EliteName(Elite elite) => elite switch
        {
            Elite.Armoured => "Armoured",
            Elite.Swift => "Swift",
            Elite.Brute => "Brute",
            Elite.Frail => "Frail",
            _ => string.Empty,
        };

        // --- Souls -----------------------------------------------------------

        /// <summary>Souls for clearing one depth of Circle 1, before bonuses.</summary>
        public static long SoulsForDepth(int depth) => (long)Math.Ceiling(1.0 + Math.Pow(depth, 1.3) / 4.0);

        public static long SoulsForDepth(int circle, int depth, double soulBonusPct)
            => (long)Math.Ceiling(SoulsForDepth(depth) * Math.Pow(CircleSouls, Math.Max(0, circle - 1)) * (1.0 + soulBonusPct / 100.0));

        // --- charges (runs) --------------------------------------------------

        /// <summary>A run costs one charge; one comes back every 20 minutes, offline too.</summary>
        public const int ChargeSeconds = 20 * 60;

        /// <summary>At most this many wait: four hours of charges.</summary>
        public const int ChargeCap = 12;

        /// <summary>Banked charges and the anchor after time has passed (settled on read, like the dungeon tickets).</summary>
        public static (int Charges, long Anchor) SettleCharges(int banked, long anchorEpoch, long nowEpoch)
        {
            if (banked >= ChargeCap) return (ChargeCap, nowEpoch);
            if (anchorEpoch <= 0 || anchorEpoch > nowEpoch) return (Math.Max(0, banked), nowEpoch);
            long earned = (nowEpoch - anchorEpoch) / ChargeSeconds;
            if (earned <= 0) return (banked, anchorEpoch);
            int charges = (int)Math.Min(ChargeCap, banked + earned);
            long anchor = charges >= ChargeCap ? nowEpoch : anchorEpoch + earned * ChargeSeconds;
            return (charges, anchor);
        }

        // --- the Workshop (Souls, no top level) ------------------------------

        public sealed record WorkshopUpgrade(string Key, string Name, string Effect, double PerLevel, long BaseCost, int MaxLevel);

        /// <summary>
        /// Cost grows 12% a level, effect stays flat: the shape that never runs
        /// out (The Tower's workshop). MaxLevel 0 = no top.
        /// </summary>
        public const double WorkshopCostGrowth = 1.12;

        public static readonly WorkshopUpgrade[] Workshop =
        {
            new("strength", "Rootstrength", "+3% damage in Nav", 3.0, 10, 0),
            new("bark", "Barkskin", "+3% health in Nav", 3.0, 10, 0),
            new("quick", "Quick roots", "+1% attack speed in Nav", 1.0, 25, 50),
            new("grave", "Grave luck", "+2% crit damage in Nav", 2.0, 20, 0),
            new("harvest", "Soul harvest", "+3% Souls", 3.0, 30, 0),
            new("fall", "Long fall", "Start runs one depth deeper (up to half your record)", 1.0, 100, 0),
            new("breath", "Second breath", "One revive a run per 10 levels (up to 3)", 0.1, 500, 30),
        };

        public static WorkshopUpgrade? WorkshopByKey(string key) => Workshop.FirstOrDefault(w => w.Key == key);

        public static long WorkshopCost(WorkshopUpgrade upgrade, int currentLevel, double discountPct = 0)
        {
            double cost = upgrade.BaseCost * Math.Pow(WorkshopCostGrowth, currentLevel) * (1.0 - Math.Clamp(discountPct, 0, 60) / 100.0);
            return cost >= long.MaxValue / 2 ? long.MaxValue / 2 : (long)Math.Ceiling(cost);
        }

        /// <summary>The upgrade's total effect at a level, after the research that scales every effect.</summary>
        public static double WorkshopEffect(WorkshopUpgrade upgrade, int level, double effectBonusPct = 0)
            => upgrade.PerLevel * Math.Max(0, level) * (1.0 + effectBonusPct / 100.0);

        // --- the Volkhv's research (gold, timed) -------------------------------

        public sealed record ResearchLine(string Key, string Name, string Effect, double PerLevel);

        public static readonly ResearchLine[] Research =
        {
            new("souls", "Soul lore", "+5% Souls", 5.0),
            new("craft", "Root-craft", "+1% to every Workshop effect", 1.0),
            new("thrift", "Volkhv's thrift", "-2% Workshop prices (up to -60%)", 2.0),
        };

        public static ResearchLine? ResearchByKey(string key) => Research.FirstOrDefault(r => r.Key == key);

        /// <summary>
        /// Gold for the next level: 50M x 1.5^level. Level 15 costs 22B, level 20
        /// 166B - the first sink that grows with income (2026-11: ~20B minted a
        /// week, every older sink flat).
        /// </summary>
        public static long ResearchGold(int currentLevel)
        {
            double gold = 50_000_000d * Math.Pow(1.5, currentLevel);
            return gold >= long.MaxValue / 2 ? long.MaxValue / 2 : (long)gold;
        }

        /// <summary>Seconds the next level takes: 2 h x 1.25^level.</summary>
        public static long ResearchSeconds(int currentLevel) => (long)(2 * 3600 * Math.Pow(1.25, currentLevel));

        // --- milestones --------------------------------------------------------

        /// <summary>First-time depths of a Circle that pay a loot chest.</summary>
        public static readonly int[] MilestoneDepths = { 10, 25, 50 };

        /// <summary>A bit per (circle, milestone): circle 1 uses bits 0-2, circle 2 bits 3-5 ... up to circle 21.</summary>
        public static int MilestoneBit(int circle, int milestoneIndex) => ((circle - 1) * MilestoneDepths.Length) + milestoneIndex;

        // --- packing ("key:value,key:value") -----------------------------------

        public static Dictionary<string, int> Unpack(string? packed)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(packed)) return map;
            foreach (var part in packed.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                if (int.TryParse(part.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
                    map[part[..colon]] = value;
            }
            return map;
        }

        public static string Pack(IReadOnlyDictionary<string, int> map)
        {
            var sb = new StringBuilder();
            foreach (var (key, value) in map.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (value <= 0) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(key).Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
