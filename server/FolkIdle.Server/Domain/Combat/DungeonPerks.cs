using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// What the run's dodges have done to the party so far. Every number is a
    /// percent; positive helps the party. They last for this run only.
    /// </summary>
    public sealed class DungeonMods
    {
        public int DamagePct;
        public int CritChancePct;
        public int CritDamagePct;
        public int MaxHpPct;
        /// <summary>Less damage taken; negative is more.</summary>
        public int ArmorPct;
        public int SpeedPct;
        /// <summary>Tenths of a percent of max health healed on every swing.</summary>
        public int LeechTenths;
        public int ExtraMeals;
        /// <summary>Meals heal this much more.</summary>
        public int FeastPct;
        /// <summary>Share of the enemy's swings that miss, accumulated so it is exact, not luck.</summary>
        public int EvadePct;
        /// <summary>More time for each swipe of a telegraph.</summary>
        public int QuickstepPct;
        /// <summary>Bloodlust: no more meals this run.</summary>
        public bool NoMeals;
        /// <summary>Every bonus taken, in order (the schools count these).</summary>
        public List<string> Taken { get; } = new();
        /// <summary>Set bonuses unlocked, by id.</summary>
        public HashSet<string> Sets { get; } = new();
        /// <summary>Bumped on every change, so cached swings know to recompute.</summary>
        public int Version;

        public int CountOf(DungeonSchool school) => Taken.Count(id => DungeonPerks.SchoolOf(id) == school);
    }

    /// <summary>The four schools a bonus belongs to; two or three of one school unlock its set bonus.</summary>
    public enum DungeonSchool { None = 0, Berserk = 1, Guardian = 2, Blood = 3, Storm = 4 }

    /// <summary>
    /// One bonus or penalty, as the client shows it on a card or a banner.
    /// Unlocks names the set bonus this card would complete, empty when none.
    /// </summary>
    public sealed record DungeonPerkView(string Id, string Name, string Line, bool Penalty,
        string School = "", bool Risky = false, string Unlocks = "");

    /// <summary>A set bonus: what two (or three) cards of one school add.</summary>
    public sealed record DungeonSetView(string Id, string School, int Cards, string Name, string Line);

    /// <summary>
    /// The dodge minigame's rules (owner, 2026-10-31): the enemy telegraphs a
    /// sequence of swipes; a clean dodge offers TWO bonuses to choose from, a
    /// miss takes a heavy blow and a random penalty. All of it lasts for the
    /// run only, and every bonus has a cap.
    /// </summary>
    /// <remarks>
    /// Modul: THE CAPS ARE THE ANTI-BOT. The server knows the right swipes and
    /// judges the answer itself, so a client cannot claim a dodge - but a bot
    /// can read the arrows and swipe them. A capped bonus bounds what that buys:
    /// at the caps a run is clearly easier, never trivially won.
    ///
    /// Modul: SCHOOLS (owner, 2026-10-31: "the player should think about the
    /// build during the run"). Every bonus was a plain number pulling the same
    /// way, so the pick hardly mattered. Now each card belongs to a school, two
    /// and three cards of one school unlock a set bonus, one card of each offer
    /// comes from the school the player leads (finish the build or turn), and
    /// about one offer in four carries a risky card: strong, with a price.
    /// A run has at most four picks, so a build is chosen by the second one.
    /// </remarks>
    public static class DungeonPerks
    {
        private sealed record Def(string Id, string Name, string Line, bool Penalty, Func<DungeonMods, bool> Open, Action<DungeonMods> Apply,
            DungeonSchool School = DungeonSchool.None, bool Risky = false);

        // Modul: SIZES DOUBLED (owner, 2026-10-31) when the run went from eight
        // telegraphs to four: measured on the owner's account, all dodged
        // against all missed moved the result by one stage at most, so a
        // dodge did not matter. Each step is clamped at its cap or floor.
        private static readonly Def[] Bonuses =
        {
            new("fury", "Fury", "+25% damage for the rest of the run.", false, m => m.DamagePct < 100, m => m.DamagePct = Math.Min(100, m.DamagePct + 25), DungeonSchool.Berserk),
            new("keen", "Keen Eye", "+10% critical chance.", false, m => m.CritChancePct < 40, m => m.CritChancePct = Math.Min(40, m.CritChancePct + 10), DungeonSchool.Berserk),
            new("brutal", "Brutality", "+50% critical damage.", false, m => m.CritDamagePct < 150, m => m.CritDamagePct = Math.Min(150, m.CritDamagePct + 50), DungeonSchool.Berserk),
            new("vigour", "Vigour", "+25% max health, and the new health is healed now.", false, m => m.MaxHpPct < 75, m => m.MaxHpPct = Math.Min(75, m.MaxHpPct + 25), DungeonSchool.Guardian),
            new("iron", "Iron Skin", "Take 20% less damage.", false, m => m.ArmorPct < 60, m => m.ArmorPct = Math.Min(60, m.ArmorPct + 20), DungeonSchool.Guardian),
            new("bastion", "Bastion", "+15% max health and take 10% less damage.", false, m => m.MaxHpPct < 75 || m.ArmorPct < 60,
                m => { m.MaxHpPct = Math.Min(75, m.MaxHpPct + 15); m.ArmorPct = Math.Min(60, m.ArmorPct + 10); }, DungeonSchool.Guardian),
            new("leech", "Leech", "Every swing heals 2% of max health.", false, m => m.LeechTenths < 60, m => m.LeechTenths = Math.Min(60, m.LeechTenths + 20), DungeonSchool.Blood),
            new("wind", "Second Wind", "Heal 50% of max health now and gain one more meal.", false, m => m.ExtraMeals < 2 && !m.NoMeals, m => m.ExtraMeals += 1, DungeonSchool.Blood),
            new("feast", "Feast", "Meals heal 50% more.", false, m => m.FeastPct < 100 && !m.NoMeals, m => m.FeastPct = Math.Min(100, m.FeastPct + 50), DungeonSchool.Blood),
            new("swift", "Swiftness", "+20% attack speed.", false, m => m.SpeedPct < 60, m => m.SpeedPct = Math.Min(60, m.SpeedPct + 20), DungeonSchool.Storm),
            new("evade", "Evasion", "One enemy swing in five misses.", false, m => m.EvadePct < 40, m => m.EvadePct = Math.Min(40, m.EvadePct + 20), DungeonSchool.Storm),
            new("quickstep", "Quickstep", "Each swipe of a dodge gets 25% more time.", false, m => m.QuickstepPct < 50, m => m.QuickstepPct = Math.Min(50, m.QuickstepPct + 25), DungeonSchool.Storm),

            // Risky cards: once a run each, strong, with a price. They count for their school.
            new("glass", "Glass Cannon", "+60% damage, but -30% max health.", false, m => !m.Taken.Contains("glass"),
                m => { m.DamagePct += 60; m.MaxHpPct -= 30; }, DungeonSchool.Berserk, Risky: true),
            new("bulwark", "Bulwark", "Take 40% less damage, but -25% attack speed.", false, m => !m.Taken.Contains("bulwark"),
                m => { m.ArmorPct += 40; m.SpeedPct -= 25; }, DungeonSchool.Guardian, Risky: true),
            new("bloodlust", "Bloodlust", "Every swing heals 4% more of max health, but no more meals this run.", false, m => !m.Taken.Contains("bloodlust"),
                m => { m.LeechTenths += 40; m.NoMeals = true; }, DungeonSchool.Blood, Risky: true),
            new("reckless", "Reckless", "+40% attack speed, but take 25% more damage.", false, m => !m.Taken.Contains("reckless"),
                m => { m.SpeedPct += 40; m.ArmorPct -= 25; }, DungeonSchool.Storm, Risky: true),
        };

        private static readonly Def[] Penalties =
        {
            new("staggered", "Staggered", "-20% damage for the rest of the run.", true, m => m.DamagePct > -60, m => m.DamagePct = Math.Max(-60, m.DamagePct - 20)),
            new("cracked", "Cracked Armour", "Take 20% more damage.", true, m => m.ArmorPct > -60, m => m.ArmorPct = Math.Max(-60, m.ArmorPct - 20)),
            new("slowed", "Slowed", "-15% attack speed.", true, m => m.SpeedPct > -45, m => m.SpeedPct = Math.Max(-45, m.SpeedPct - 15)),
            new("wounded", "Wounded", "-20% max health.", true, m => m.MaxHpPct > -60, m => m.MaxHpPct = Math.Max(-60, m.MaxHpPct - 20)),
        };

        /// <summary>Set bonus ids, read by the runner.</summary>
        public const string Executioner = "executioner", Frenzy = "frenzy", LastStand = "laststand", Unshakable = "unshakable",
            Bloodbath = "bloodbath", Sanguine = "sanguine", Flow = "flow", EyeOfStorm = "eye";

        public static readonly DungeonSetView[] SetBonuses =
        {
            new(Executioner, "Berserk", 2, "Executioner", "+50% damage to an enemy below 30% health."),
            new(Frenzy, "Berserk", 3, "Frenzy", "+30% attack speed while below half health."),
            new(LastStand, "Guardian", 2, "Last Stand", "The first blow that would kill the party leaves it at 30% health instead."),
            new(Unshakable, "Guardian", 3, "Unshakable", "A missed dodge lands half the blow and brings no curse."),
            new(Bloodbath, "Blood", 2, "Bloodbath", "Every wave cleared heals 30% of max health."),
            new(Sanguine, "Blood", 3, "Sanguine", "Healing from swings is doubled."),
            new(Flow, "Storm", 2, "Flow", "Every clean dodge from now on heals 20% of max health."),
            new(EyeOfStorm, "Storm", 3, "Eye of the Storm", "+30% attack speed."),
        };

        /// <summary>Share of offers (in percent) whose second card is a risky one, when one is left.</summary>
        public const int RiskyOfferPct = 25;

        public static DungeonSchool SchoolOf(string id) => Find(id)?.School ?? DungeonSchool.None;

        public static DungeonPerkView View(string id) => View(id, null);

        private static DungeonPerkView View(string id, DungeonMods? mods)
        {
            var d = Find(id);
            if (d == null) return new DungeonPerkView(id, id, string.Empty, false);
            string unlocks = string.Empty;
            if (mods != null && d.School != DungeonSchool.None)
            {
                int next = mods.CountOf(d.School) + 1;
                var set = SetBonuses.FirstOrDefault(s => s.School == d.School.ToString() && s.Cards == next);
                if (set != null) unlocks = $"{set.Name}: {set.Line}";
            }
            return new DungeonPerkView(d.Id, d.Name, d.Line, d.Penalty, d.School == DungeonSchool.None ? string.Empty : d.School.ToString(), d.Risky, unlocks);
        }

        private static Def? Find(string id) => Bonuses.Concat(Penalties).FirstOrDefault(d => d.Id == id);

        /// <summary>
        /// Two different bonuses whose caps are not reached (fewer only when the
        /// caps are). The first offer shows two schools. Later, one card comes
        /// from the school the player leads (ties broken at random) and the
        /// other from another school; about one offer in four makes that other
        /// card a risky one.
        /// </summary>
        public static IReadOnlyList<DungeonPerkView> Offer(DungeonMods mods, Random rng)
        {
            var open = Bonuses.Where(b => b.Open(mods)).OrderBy(_ => rng.Next()).ToList();
            var plain = open.Where(b => !b.Risky).ToList();
            var risky = open.Where(b => b.Risky).ToList();
            var picks = new List<Def>();

            var counts = Enum.GetValues<DungeonSchool>().Where(s => s != DungeonSchool.None)
                .Select(s => (School: s, Count: mods.CountOf(s))).Where(x => x.Count > 0).ToList();
            if (counts.Count > 0)
            {
                int most = counts.Max(x => x.Count);
                var leaders = counts.Where(x => x.Count == most).Select(x => x.School).OrderBy(_ => rng.Next()).ToList();
                foreach (var lead in leaders)
                {
                    var own = plain.FirstOrDefault(b => b.School == lead);
                    if (own != null) { picks.Add(own); break; }
                }
            }
            if (picks.Count == 0 && plain.Count > 0) picks.Add(plain[0]);

            DungeonSchool first = picks.Count > 0 ? picks[0].School : DungeonSchool.None;
            Def? second = null;
            if (rng.Next(100) < RiskyOfferPct) second = risky.FirstOrDefault(b => b.School != first);
            second ??= plain.FirstOrDefault(b => b.School != first && !picks.Contains(b));
            second ??= plain.FirstOrDefault(b => !picks.Contains(b));
            second ??= risky.FirstOrDefault(b => !picks.Contains(b));
            if (second != null) picks.Add(second);
            return picks.Select(b => View(b.Id, mods)).ToList();
        }

        /// <summary>A random penalty whose floor is not reached, or null when all are.</summary>
        public static DungeonPerkView? RollPenalty(DungeonMods mods, Random rng)
        {
            var open = Penalties.Where(p => p.Open(mods)).ToList();
            return open.Count == 0 ? null : View(open[rng.Next(open.Count)].Id);
        }

        /// <summary>Applies a bonus or penalty by id; false when it does not exist or its cap is reached.</summary>
        public static bool Apply(DungeonMods mods, string id)
        {
            var d = Find(id);
            if (d == null || !d.Open(mods)) return false;
            d.Apply(mods);
            if (!d.Penalty) mods.Taken.Add(d.Id);
            mods.Version++;
            return true;
        }

        /// <summary>
        /// The set bonuses the cards taken so far unlock and that were not yet
        /// unlocked; marks them unlocked and applies the plain-number ones.
        /// </summary>
        public static IReadOnlyList<DungeonSetView> UnlockSets(DungeonMods mods)
        {
            var fresh = new List<DungeonSetView>();
            foreach (var set in SetBonuses)
            {
                if (mods.Sets.Contains(set.Id)) continue;
                if (mods.CountOf(Enum.Parse<DungeonSchool>(set.School)) < set.Cards) continue;
                mods.Sets.Add(set.Id);
                if (set.Id == EyeOfStorm) mods.SpeedPct += 30;
                mods.Version++;
                fresh.Add(set);
            }
            return fresh;
        }

        // --- the telegraph ---------------------------------------------------

        public static readonly string[] Directions = { "Up", "Down", "Left", "Right" };

        /// <summary>
        /// How many swipes a telegraph asks for: two on stages 1-4, then one
        /// more every four stages, at most five; the boss asks one more (six).
        /// Owner, 2026-10-31 (second pass): fewer telegraphs, each longer and
        /// faster - one swipe was too easy to matter.
        /// </summary>
        public static int SequenceLength(int stage, bool boss) => Math.Min(boss ? 6 : 5, 2 + (Math.Clamp(stage, 1, 15) - 1) / 4 + (boss ? 1 : 0));

        /// <summary>Time for each swipe: 1.0 s on stage 1 down to 0.55 s on stage 15.</summary>
        public static int MsPerSwipe(int stage) => 1000 - 32 * (Math.Clamp(stage, 1, 15) - 1);

        /// <summary>Reading time before the first swipe is due.</summary>
        public const int ReadMs = 600;

        /// <summary>Allowance for the round trip: the window is the player's, the network's lag is not held against them.</summary>
        public const int LatencySlackMs = 1200;

        /// <summary>Faster than this per swipe is not a hand.</summary>
        public const int FastestSwipeMs = 90;

        public static string[] Telegraph(int stage, bool boss, Random rng)
        {
            var dirs = new string[SequenceLength(stage, boss)];
            for (int i = 0; i < dirs.Length; i++) dirs[i] = Directions[rng.Next(Directions.Length)];
            return dirs;
        }

        public static int WindowMs(int stage, bool boss, int quickstepPct = 0)
            => ReadMs + (int)(SequenceLength(stage, boss) * MsPerSwipe(stage) * (1 + quickstepPct / 100.0));

        /// <summary>The server's verdict on an answer: the right swipes, in order, within the window.</summary>
        public static bool Judge(string[] expected, IReadOnlyList<string>? answer, int windowMs, double elapsedMs)
        {
            if (answer == null || answer.Count != expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
                if (!string.Equals(expected[i], answer[i], StringComparison.OrdinalIgnoreCase)) return false;
            return elapsedMs <= windowMs + LatencySlackMs && elapsedMs >= expected.Length * FastestSwipeMs;
        }
    }
}
