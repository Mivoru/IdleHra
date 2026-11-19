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
    /// Modul: OFF UNLESS FOLKIDLE_NAV=on. The owner approved Souls and the gold
    /// research on 2026-11-19 and production runs with the flag on (ops/oracle
    /// .env); the flag stays so the mode can be closed again without a deploy.
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

        // --- who stands at each depth ------------------------------------------

        /// <summary>What a guardian does besides hitting: one rule each, so a build that loses to one beats another.</summary>
        public enum Ability : byte
        {
            None = 0,
            /// <summary>Vampire: heals 1.5% of its health with every blow it lands.</summary>
            Lifedrinker = 1,
            /// <summary>Two-Hearted Hag: a second heart - rises once with 40% of its health.</summary>
            SecondHeart = 2,
            /// <summary>Nightmare: sits on your chest - you fight with 60% of your health.</summary>
            Nightmare = 3,
            /// <summary>Will-o'-wisp: a quarter of your swings pass through it, and heal nothing.</summary>
            Elusive = 4,
            /// <summary>Lady Midday: every blow 8% harder than the last.</summary>
            NoonHeat = 5,
            /// <summary>Pale Maiden: each of your hits costs you 2% of your own health.</summary>
            Mirror = 6,
            /// <summary>Dog-Headed Giant: plain hits lose 40%; crits pass in full.</summary>
            IronHide = 7,
            /// <summary>Spindle Hag: no crit lands as a crit.</summary>
            Hex = 8,
            /// <summary>Drowned One: your healing is cut by 80%.</summary>
            Drowning = 9,
            /// <summary>Black Hound: your swings are 25% slower.</summary>
            GraveChill = 10,
            /// <summary>Veles: the warden of every 25th depth - more health and harder blows.</summary>
            Warden = 11,
        }

        /// <summary>A guardian of Nav: its own name and art, and one ability.</summary>
        public sealed record Guardian(string Key, string Name, Ability Ability, string AbilityName, string AbilityText)
        {
            /// <summary>The name at the start of a sentence: "The Vampire", but "Lady Midday" and "Veles".</summary>
            public string Subject => Name.StartsWith("Lady ", StringComparison.Ordinal) || Name == "Veles" ? Name : "The " + Name;
        }

        /// <summary>
        /// The ten servants of Nav, in the order they stand: depth 1 meets the
        /// first, depth 11 the first again, stronger.
        /// </summary>
        /// <remarks>
        /// Modul: OWN CREATURES, NOT REGION MONSTERS AGAIN. The first build lent
        /// each depth a region monster's art (Malakor at depth 5, 30, 55...), so
        /// the endless mode showed the same 25 faces as the rest of the game.
        /// The owner asked for Nav's own (2026-11-19). They stay out of
        /// monsters.json on purpose: that file is the five regions' 25 (codex,
        /// hunting, pets and drops all walk it), and a guardian is fought only
        /// here. Names avoid what the game already uses - Koschei is a dungeon
        /// boss, Vodnik a race.
        ///
        /// Modul: ENGLISH NAMES (owner, 2026-11-19): the game is in English, so
        /// the creatures are named in it - Vampire, not Upir; the keys (and the
        /// art files) keep the folklore word. Kikimora is a pet already.
        ///
        /// Modul: AN ABILITY PER GUARDIAN IS THE REASON TO OWN TWO BUILDS. Iron
        /// hide rewards crit, Hex punishes it; Drowning punishes a lifesteal
        /// tank, Noon heat a slow killer. A record stops where the build's weak
        /// guardian stands, and a preset made for that guardian moves it.
        /// </remarks>
        public static readonly Guardian[] Guardians =
        {
            new("upir", "Vampire", Ability.Lifedrinker, "Lifedrinker", "Heals 1.5% of its health with every blow it lands."),
            new("strzyga", "Two-Hearted Hag", Ability.SecondHeart, "Second heart", "Rises once more with 40% of its health."),
            new("mora", "Nightmare", Ability.Nightmare, "Crushing dream", "Sits on your chest: you fight with 60% of your health."),
            new("wisp", "Will-o'-wisp", Ability.Elusive, "Elusive", "A quarter of your swings pass through it and heal nothing."),
            new("poludnica", "Lady Midday", Ability.NoonHeat, "Noon heat", "Every blow lands 8% harder than the last."),
            new("navka", "Pale Maiden", Ability.Mirror, "Mirror", "Each of your hits costs you 2% of your own health."),
            new("psoglav", "Dog-Headed Giant", Ability.IronHide, "Iron hide", "Plain hits lose 40%; crits pass in full."),
            new("kikimora", "Spindle Hag", Ability.Hex, "Hex", "No crit lands as a crit."),
            new("utopenec", "Drowned One", Ability.Drowning, "Drowning", "Your healing is cut by 80%."),
            new("hound", "Black Hound", Ability.GraveChill, "Grave chill", "Your swings are 25% slower."),
        };

        /// <summary>The warden every <see cref="WardenEvery"/>th depth.</summary>
        public static readonly Guardian Warden = new("veles", "Veles", Ability.Warden, "Lord of Nav", "Almost twice the health, blows 20% harder, triple Souls.");

        // Modul: MEASURED 2026-11-19 on 15 synthetic characters x 20 seeds: the
        // abilities cost 4-9 depths (~8%) against the first build's region
        // monsters. Veles at x2.5 health cost 7-13 and parked every record at
        // 24/49/74 - a wall ten depths thick - so he is x1.8 now.
        // On a local restore of the 2026-11-18 backup (5 runs each): Mivoru 35
        // (was 40; stopped by Navka, Kikimora), (pro)boss 10-11 (was 15;
        // Upir, Strzyga), Tomda 9-16 (was 13; Lady Midday, Black Hound). Most
        // of the drop for the two weaker accounts is armour, not abilities:
        // every guardian fights with Malakor's, where the first build lent
        // depths 6-25 the softer region 1-4 monsters. Upir 2 -> 1.5%, Navka
        // 3 -> 2%, the Hound 0.7 -> 0.75 after the first pass, where Navka
        // ended every one of Mivoru's runs at depth 36.
        public const int WardenEvery = 25;
        public const double WardenHp = 1.8;
        public const double WardenAttack = 1.2;
        public const double WardenSouls = 3.0;

        public static Guardian GuardianFor(int depth)
        {
            int d = Math.Max(1, depth);
            if (d % WardenEvery == 0) return Warden;
            return Guardians[(d - 1) % Guardians.Length];
        }

        /// <summary>
        /// What to change when this guardian ended a run: one line the run
        /// screen prints under "why it stopped", so a record that will not
        /// move points at the build that would move it.
        /// </summary>
        public static string Hint(Ability ability, bool fell) => ability switch
        {
            Ability.Lifedrinker => fell ? "The Vampire heals as it hits you: more health or lifesteal outlasts it." : "The Vampire heals every blow: only fast damage outruns it.",
            Ability.SecondHeart => "The Two-Hearted Hag rises once more: it needs 40% more damage than its depth.",
            Ability.Nightmare => "The Nightmare takes 40% of your health: health and armour (Barkskin, CON) win here.",
            Ability.Elusive => "A quarter of your swings pass through the wisp: raw damage and attack speed.",
            Ability.NoonHeat => fell ? "Lady Midday's blows keep growing: end the fight sooner or bring more health." : "Lady Midday's heat grows every blow: more damage ends it before it peaks.",
            Ability.Mirror => "The Pale Maiden costs you health per hit: fewer, harder hits and lifesteal pay it back.",
            Ability.IronHide => "The Giant's hide stops plain hits: crit chance gets through in full.",
            Ability.Hex => "The Spindle Hag's hex stops crits: plain damage beats her - crit gear adds nothing here.",
            Ability.Drowning => "The Drowned One cuts your healing: armour and health instead of lifesteal.",
            Ability.GraveChill => "The Black Hound slows your swings: attack speed (Quick roots) answers the chill.",
            Ability.Warden => fell ? "Veles hits hard: Barkskin and Second breath carry you through his depth." : "Veles has almost twice the health: Rootstrength and Grave luck break the wall.",
            _ => fell ? "More health or armour keeps you standing." : "More damage clears it inside the clock.",
        };

        // --- titles and news -----------------------------------------------------

        /// <summary>Title for the first Veles a character beats (depth 25 of any Circle).</summary>
        public const string VelesTitleSlug = "nav_veles";

        /// <summary>Titles for opening a Circle: (circle opened, slug, name, colour).</summary>
        public static readonly (int Circle, string Slug, string Name, string Color)[] CircleTitles =
        {
            (2, "nav_circle2", "Walker of Nav", "#5fb8a8"),
            (3, "nav_circle3", "Root-King", "#7f9fe0"),
            (5, "nav_circle5", "Lord of the Underworld", "#c084fc"),
        };

        public static Guardian? GuardianByKey(string key)
            => key == Warden.Key ? Warden : Guardians.FirstOrDefault(g => g.Key == key);

        /// <summary>The ability numbers the simulator reads, beside the text the screen shows.</summary>
        public const double LifedrinkerPct = 1.5;
        public const double SecondHeartPct = 40.0;
        public const double NightmareHealthFactor = 0.6;
        public const double ElusiveMissShare = 0.25;
        public const double NoonHeatPerBlow = 0.08;
        public const double MirrorPctOfHealth = 2.0;
        public const double IronHidePlainFactor = 0.6;
        public const double DrowningHealFactor = 0.2;
        public const double GraveChillSpeedFactor = 0.75;

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

        public static long SoulsForDepth(int circle, int depth, double soulBonusPct, bool restless = false)
            => (long)Math.Ceiling(SoulsForDepth(depth) * Math.Pow(CircleSouls, Math.Max(0, circle - 1)) * (1.0 + soulBonusPct / 100.0)
                * (depth % WardenEvery == 0 ? WardenSouls : 1.0) * (restless ? RestlessSouls : 1.0));

        // --- charges (runs) --------------------------------------------------

        /// <summary>A run costs one charge; one comes back every 20 minutes, offline too.</summary>
        public const int ChargeSeconds = 20 * 60;

        /// <summary>At most this many wait: four hours of charges.</summary>
        public const int ChargeCap = 12;

        /// <summary>Banked charges and the anchor after time has passed (settled on read, like the dungeon tickets).</summary>
        public static (int Charges, long Anchor) SettleCharges(int banked, long anchorEpoch, long nowEpoch)
            => SettleCharges(banked, anchorEpoch, nowEpoch, ChargeCap, ChargeSeconds);

        /// <summary>The cap and the seconds a charge takes, after Deep well and Swift return.</summary>
        public static (int Cap, int Seconds) ChargeRules(int wellLevel, int swiftLevel)
            => (ChargeCap + Math.Clamp(wellLevel, 0, 6), ChargeSeconds - (60 * Math.Clamp(swiftLevel, 0, 8)));

        public static (int Charges, long Anchor) SettleCharges(int banked, long anchorEpoch, long nowEpoch, int cap, int seconds)
        {
            seconds = Math.Max(60, seconds);
            if (banked >= cap) return (Math.Max(banked, cap), nowEpoch);
            if (anchorEpoch <= 0 || anchorEpoch > nowEpoch) return (Math.Max(0, banked), nowEpoch);
            long earned = (nowEpoch - anchorEpoch) / seconds;
            if (earned <= 0) return (banked, anchorEpoch);
            int charges = (int)Math.Min(cap, banked + earned);
            long anchor = charges >= cap ? nowEpoch : anchorEpoch + (earned * seconds);
            return (charges, anchor);
        }

        // --- the Workshop (Souls, no top level) ------------------------------

        public sealed record WorkshopUpgrade(string Key, string Name, string Effect, double PerLevel, long BaseCost, int MaxLevel)
        {
            /// <summary>For a bane: the guardian it answers. Locked until that guardian has stopped one of your runs.</summary>
            public string Guardian { get; init; } = string.Empty;
        }

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
            new("bond", "Spirit bond", "+3% to everything your spirits give", 3.0, 60, 0),
            // The banes: one per guardian, each wearing down that guardian's
            // one rule. Locked until the guardian has ended one of your runs, so
            // the "why it stopped" line points at something you can buy.
            new("bane_upir", "Garlic wreath", "-4% to the Vampire's Lifedrinker", BanePerLevelPct, BaneBaseCost, BaneMaxLevel) { Guardian = "upir" },
            new("bane_strzyga", "Aspen stake", "-4% to the Two-Hearted Hag's second heart", BanePerLevelPct, BaneBaseCost, BaneMaxLevel) { Guardian = "strzyga" },
            new("bane_mora", "Knife under the pillow", "-4% to the Nightmare's crushing dream", BanePerLevelPct, BaneBaseCost, BaneMaxLevel) { Guardian = "mora" },
            new("bane_wisp", "Fern-flower lantern", "-4% to the Will-o'-wisp's elusiveness", BanePerLevelPct, BaneBaseCost, BaneMaxLevel) { Guardian = "wisp" },
            new("bane_poludnica", "Linden shade", "-4% to Lady Midday's noon heat", BanePerLevelPct, BaneBaseCost, BaneMaxLevel) { Guardian = "poludnica" },
            new("bane_navka", "Wormwood sprig", "-4% to the Pale Maiden's mirror", BanePerLevelPct, BaneBaseCost, BaneMaxLevel) { Guardian = "navka" },
            new("bane_psoglav", "Ash-wood spear", "-4% to the Dog-Headed Giant's iron hide", BanePerLevelPct, BaneBaseCost, BaneMaxLevel) { Guardian = "psoglav" },
            new("bane_kikimora", "Hen-god stone", "-4% to the Spindle Hag's hex", BanePerLevelPct, BaneBaseCost, BaneMaxLevel) { Guardian = "kikimora" },
            new("bane_utopenec", "Miller's charm", "-4% to the Drowned One's drowning", BanePerLevelPct, BaneBaseCost, BaneMaxLevel) { Guardian = "utopenec" },
            new("bane_hound", "Rowan collar", "-4% to the Black Hound's grave chill", BanePerLevelPct, BaneBaseCost, BaneMaxLevel) { Guardian = "hound" },
        };

        /// <summary>
        /// Modul: BANES ARE BOUGHT, NOT FOUND (2026-11-19, owner: "fix every
        /// weakness"). The first build named the guardian that stopped a run
        /// and said what would beat it, and then offered nothing to buy for it:
        /// the hint was advice about gear. A bane takes 4% off one guardian's
        /// rule a level, up to 60% at level 15 (Root-craft pushes it a little
        /// further, 75% at most), so a record that a guardian walls is a
        /// decision - wear it down, or out-build it - instead of a wait.
        ///
        /// MEASURED (14 simulated days on the 2026-11-19 backup, 39 descents a
        /// day): at 20 levels from 40 Souls Mivoru had every bane at 80% by day
        /// seven and the ten guardians had become one guardian. Fifteen levels
        /// from 150 keep 40% of every rule - the restless one's at 60% - so a
        /// build still loses to its weak guardian; Tomda maxes his first bane
        /// on day three.
        /// </summary>
        public const double BanePerLevelPct = 4.0;
        public const int BaneMaxLevel = 15;
        public const long BaneBaseCost = 150;
        public const double BaneMaxReductionPct = 75.0;

        public static WorkshopUpgrade? BaneFor(string guardianKey) => Workshop.FirstOrDefault(w => w.Guardian == guardianKey);

        /// <summary>The bit a guardian sets in NavProgress.GuardiansMet when it first stops a run (its place among the ten).</summary>
        public static int GuardianBit(string guardianKey)
        {
            for (int i = 0; i < Guardians.Length; i++) if (Guardians[i].Key == guardianKey) return i;
            return -1;
        }

        public static bool HasMet(int guardiansMet, string guardianKey)
        {
            int bit = GuardianBit(guardianKey);
            return bit >= 0 && (guardiansMet & (1 << bit)) != 0;
        }

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

        /// <param name="MaxLevel">0 = no top.</param>
        /// <param name="OpensAtCircle">The Circle that must be open before the line can be researched.</param>
        /// <param name="LevelsPerCircle">When above 0, the line's top grows with the Circles: this many levels per Circle opened from <paramref name="OpensAtCircle"/> on, up to MaxLevel.</param>
        /// <param name="GoldFactor">Multiplies the gold price - the lines that reach outside Nav cost more.</param>
        public sealed record ResearchLine(string Key, string Name, string Effect, double PerLevel, int MaxLevel = 0, int OpensAtCircle = 1, int LevelsPerCircle = 0, double GoldFactor = 1.0)
        {
            /// <summary>True for a line whose bonus counts in the rest of the game, not only in Nav.</summary>
            public bool Outward { get; init; }
        }

        /// <remarks>
        /// Modul: NINE LINES, NOT THREE (2026-11-19). Three lines that all said
        /// "more of the Workshop" were a gold sink with no decision in it. The
        /// six new ones each change how you play: more runs (Deep well, Swift
        /// return), more spirits (Ancestors' call), and from Circle 2 three that
        /// reach OUTSIDE Nav - combat gold, combat XP, loot luck.
        ///
        /// Modul: THE OUTWARD LINES ARE SMALL AND GATED, MEASURED. On the
        /// 2026-11-19 backup the three players already have +97..101% combat XP,
        /// +13..128% gold and +25..62% loot luck, and gold multipliers multiply
        /// (CombatGoldReward.FactorsFor): Mivoru's 95M gold an hour on Malakor
        /// gains 1% per level - 160M a week - against a level that costs
        /// 100M x 1.5^level, so the line pays itself back only while it is
        /// shallow. The XP line adds 1 point to a +100% sum (0.5% more XP a
        /// level); levels stall at 135-140, so it buys a few levels, not
        /// regions. Luck is half a point a level from Circle 3. The top of each
        /// grows with the Circles (10 levels per Circle from the one that opens
        /// it, 25 at most): the bonus follows how deep you went, not only your
        /// gold.
        /// </remarks>
        public static readonly ResearchLine[] Research =
        {
            new("souls", "Soul lore", "+5% Souls", 5.0),
            new("craft", "Root-craft", "+1% to every Workshop effect", 1.0),
            new("thrift", "Volkhv's thrift", "-2% Workshop prices (up to -60%)", 2.0, MaxLevel: 30),
            new("well", "Deep well", "+1 descent can wait (up to +6)", 1.0, MaxLevel: 6),
            new("swift", "Swift return", "A descent comes back 1 minute sooner (up to 8)", 1.0, MaxLevel: 8),
            new("call", "Ancestors' call", "+1 spirit walks with you (up to +3)", 1.0, MaxLevel: 3),
            new("bounty", "Veles' bounty", "+1% combat gold everywhere", 1.0, MaxLevel: 25, OpensAtCircle: 2, LevelsPerCircle: 10, GoldFactor: 2.0) { Outward = true },
            new("memory", "Memory of the dead", "+1% combat XP everywhere", 1.0, MaxLevel: 25, OpensAtCircle: 2, LevelsPerCircle: 10, GoldFactor: 2.0) { Outward = true },
            new("luck", "Mokosh's thread", "+0.5% loot luck everywhere", 0.5, MaxLevel: 20, OpensAtCircle: 3, LevelsPerCircle: 10, GoldFactor: 2.0) { Outward = true },
        };

        /// <summary>The top level a line can reach with this many Circles open: 0 = no top, -1 = not open yet.</summary>
        public static int ResearchCap(ResearchLine line, int highestCircle)
        {
            if (highestCircle < line.OpensAtCircle) return -1;
            if (line.LevelsPerCircle <= 0) return line.MaxLevel;
            int byCircle = line.LevelsPerCircle * (highestCircle - line.OpensAtCircle + 1);
            return line.MaxLevel > 0 ? Math.Min(line.MaxLevel, byCircle) : byCircle;
        }

        /// <summary>Gold for the next level of a line.</summary>
        public static long ResearchGold(ResearchLine line, int currentLevel)
        {
            double gold = ResearchGold(currentLevel) * line.GoldFactor;
            return gold >= long.MaxValue / 2 ? long.MaxValue / 2 : (long)gold;
        }

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

        // --- spirits of Nav (benched ancestors) ---------------------------------

        /// <summary>
        /// Modul: SPIRITS OF NAV (owner, 2026-11-19: "the spirits too"). The
        /// best adults who are NOT in the three slots walk beside every descent:
        /// each aptitude point above <see cref="SpiritFloor"/> adds a little,
        /// each aptitude its own thing - Strength damage, Endurance health,
        /// Skill attack speed, Fortune Souls. Breeding had no goal left after
        /// 50/50/50/50; now the fourth, fifth and sixth such child still count.
        ///
        /// MEASURED on the 2026-11-19 backup (best three benched adults):
        /// (pro)boss 50/50/50/50 x3 = +36% damage and health, +9% speed;
        /// Tomda (35/24/34/34, 32/25/31/25, 29/20/29/27) = +14% damage; Mivoru
        /// (26s) = +7%. +36% damage is about three depths (guardian health grows
        /// 10% a depth) - a reason to breed, not a replacement for the Workshop.
        /// Three spirits, six with Ancestors' call; the floor is 20 because the
        /// first generations start at 4 and a 20 is an ordinary bred adult.
        /// </summary>
        public const int SpiritFloor = 20;
        public const int SpiritsBase = 3;
        public const double SpiritDamagePerPoint = 0.4;
        public const double SpiritHealthPerPoint = 0.4;
        public const double SpiritSpeedPerPoint = 0.1;
        public const double SpiritSoulsPerPoint = 0.4;

        /// <summary>One spirit: a benched adult's four aptitudes.</summary>
        public readonly record struct Spirit(string Name, int Strength, int Skill, int Endurance, int Fortune)
        {
            public int Total => Strength + Skill + Endurance + Fortune;
        }

        /// <summary>What a set of spirits adds, after Spirit bond: (damage %, health %, attack speed %, Souls %).</summary>
        public static (double Damage, double Health, double Speed, double Souls) SpiritBonus(IEnumerable<Spirit> spirits, double bondPct = 0)
        {
            double d = 0, h = 0, sp = 0, so = 0;
            foreach (var s in spirits)
            {
                d += Math.Max(0, s.Strength - SpiritFloor) * SpiritDamagePerPoint;
                h += Math.Max(0, s.Endurance - SpiritFloor) * SpiritHealthPerPoint;
                sp += Math.Max(0, s.Skill - SpiritFloor) * SpiritSpeedPerPoint;
                so += Math.Max(0, s.Fortune - SpiritFloor) * SpiritSoulsPerPoint;
            }
            double bond = 1.0 + bondPct / 100.0;
            return (d * bond, h * bond, sp * bond, so * bond);
        }

        /// <summary>The spirits that walk: the best <paramref name="count"/> by aptitude total.</summary>
        public static List<Spirit> ChooseSpirits(IEnumerable<Spirit> benched, int count)
            => benched.OrderByDescending(s => s.Total).ThenByDescending(s => s.Strength).Take(Math.Max(0, count)).ToList();

        // --- the week: omen and board ---------------------------------------------

        /// <summary>Weeks since the Unix epoch, Monday 00:00 UTC to Monday (1970-01-01 was a Thursday).</summary>
        public static int WeekOf(long epochSeconds) => (int)Math.Floor(((epochSeconds / 86400.0) + 3.0) / 7.0);

        /// <summary>When week <paramref name="week"/> ends, epoch seconds.</summary>
        public static long WeekEndsEpoch(int week) => (((long)(week + 1) * 7) - 3) * 86400L;

        /// <summary>
        /// Modul: THE WEEKLY OMEN. One of the ten guardians is restless each
        /// week: its rule is half again as strong, and its depths pay double
        /// Souls. The same record is a different climb from week to week, and
        /// the week's bane is the obvious buy.
        /// </summary>
        public const double RestlessAbility = 1.5;
        public const double RestlessSouls = 2.0;

        public static Guardian RestlessGuardian(int week) => Guardians[((week % Guardians.Length) + Guardians.Length) % Guardians.Length];

        /// <summary>
        /// The share of a guardian's rule left after its bane and the omen: 1
        /// is the rule as written, 0 is no rule at all.
        /// </summary>
        public static double AbilityStrength(double baneReductionPct, bool restless)
            => Math.Max(0.0, 1.0 - (Math.Clamp(baneReductionPct, 0, BaneMaxReductionPct) / 100.0)) * (restless ? RestlessAbility : 1.0);

        /// <summary>
        /// Modul: THE WEEKLY BOARD PAYS ITS TOP THREE (2026-11-19). The all-time
        /// board only ever moves for one person; "deepest this week" starts
        /// from zero every Monday. Rank 1 an Ancient chest, 2 a Gold, 3 a
        /// Silver, paid the next time the player opens Nav - only when at least
        /// two players descended that week, so a lone runner is not paid for
        /// showing up. The first week won also grants a title.
        /// </summary>
        public const int WeeklyMinRunners = 2;
        public const string WeeklyTitleSlug = "nav_weekly";

        // --- frames for Souls ------------------------------------------------------

        /// <summary>A frame bought with Souls; <paramref name="Circle"/> must be open and <paramref name="Depth"/> reached in some Circle.</summary>
        public sealed record NavFrame(string Id, string Name, long Price, int Circle, int Depth);

        /// <summary>
        /// Modul: SOULS BUY SOMETHING YOU WEAR (2026-11-19). Souls had one use,
        /// the Workshop, so after a few weeks a record was only a number. Four
        /// bound frames, each behind a place in Nav and a price that competes
        /// with a Workshop level - showing where you have been, never power.
        /// </summary>
        public static readonly NavFrame[] Frames =
        {
            // Prices from the simulated Souls a day: ~6k (Tomda) to ~40k
            // (Mivoru) on day one, 70k-500k by day fourteen.
            new("frame_nav_roots", "Roots of Nav", 5_000, 1, 10),
            new("frame_nav_bone", "Bone Lantern", 20_000, 1, 25),
            new("frame_nav_veles", "Veles' Horns", 60_000, 1, 50),
            new("frame_nav_crown", "Crown of the Underworld", 200_000, 2, 25),
        };

        public static NavFrame? FrameById(string id) => Frames.FirstOrDefault(f => f.Id == id);

        public static IEnumerable<CosmeticDefinition> FrameCosmetics()
            => Frames.Select(f => new CosmeticDefinition(f.Id, CosmeticKind.Frame, CosmeticRegistry.Legendary, f.Name, null, Bound: true));

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
