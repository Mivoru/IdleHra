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
        /// <summary>Bumped on every change, so cached swings know to recompute.</summary>
        public int Version;
    }

    /// <summary>One bonus or penalty, as the client shows it on a card or a banner.</summary>
    public sealed record DungeonPerkView(string Id, string Name, string Line, bool Penalty);

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
    /// </remarks>
    public static class DungeonPerks
    {
        private sealed record Def(string Id, string Name, string Line, bool Penalty, Func<DungeonMods, bool> Open, Action<DungeonMods> Apply);

        private static readonly Def[] Bonuses =
        {
            new("fury", "Fury", "+15% damage for the rest of the run.", false, m => m.DamagePct < 75, m => m.DamagePct += 15),
            new("keen", "Keen Eye", "+6% critical chance.", false, m => m.CritChancePct < 30, m => m.CritChancePct += 6),
            new("brutal", "Brutality", "+30% critical damage.", false, m => m.CritDamagePct < 120, m => m.CritDamagePct += 30),
            new("vigour", "Vigour", "+15% max health, and the new health is healed now.", false, m => m.MaxHpPct < 60, m => m.MaxHpPct += 15),
            new("iron", "Iron Skin", "Take 10% less damage.", false, m => m.ArmorPct < 40, m => m.ArmorPct += 10),
            new("leech", "Leech", "Every swing heals 1% of max health.", false, m => m.LeechTenths < 40, m => m.LeechTenths += 10),
            new("swift", "Swiftness", "+10% attack speed.", false, m => m.SpeedPct < 40, m => m.SpeedPct += 10),
            new("wind", "Second Wind", "Heal 35% of max health now and gain one more meal.", false, m => m.ExtraMeals < 2, m => m.ExtraMeals += 1),
        };

        private static readonly Def[] Penalties =
        {
            new("staggered", "Staggered", "-10% damage for the rest of the run.", true, m => m.DamagePct > -40, m => m.DamagePct -= 10),
            new("cracked", "Cracked Armour", "Take 10% more damage.", true, m => m.ArmorPct > -40, m => m.ArmorPct -= 10),
            new("slowed", "Slowed", "-8% attack speed.", true, m => m.SpeedPct > -32, m => m.SpeedPct -= 8),
            new("wounded", "Wounded", "-10% max health.", true, m => m.MaxHpPct > -40, m => m.MaxHpPct -= 10),
        };

        public static DungeonPerkView View(string id)
        {
            var d = Find(id);
            return d == null ? new DungeonPerkView(id, id, string.Empty, false) : new DungeonPerkView(d.Id, d.Name, d.Line, d.Penalty);
        }

        private static Def? Find(string id) => Bonuses.Concat(Penalties).FirstOrDefault(d => d.Id == id);

        /// <summary>Two different bonuses whose caps are not reached (fewer only when the caps are).</summary>
        public static IReadOnlyList<DungeonPerkView> Offer(DungeonMods mods, Random rng)
            => Bonuses.Where(b => b.Open(mods)).OrderBy(_ => rng.Next()).Take(2).Select(b => View(b.Id)).ToList();

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
            mods.Version++;
            return true;
        }

        // --- the telegraph ---------------------------------------------------

        public static readonly string[] Directions = { "Up", "Down", "Left", "Right" };

        /// <summary>
        /// How many swipes a telegraph asks for: one on stages 1-4, then one
        /// more every four stages, at most four; the boss asks one more.
        /// </summary>
        public static int SequenceLength(int stage, bool boss) => Math.Min(boss ? 5 : 4, 1 + (Math.Clamp(stage, 1, 15) - 1) / 4 + (boss ? 1 : 0));

        /// <summary>Time for each swipe: 1.4 s on stage 1 down to 0.77 s on stage 15.</summary>
        public static int MsPerSwipe(int stage) => 1400 - 45 * (Math.Clamp(stage, 1, 15) - 1);

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

        public static int WindowMs(int stage, bool boss) => ReadMs + SequenceLength(stage, boss) * MsPerSwipe(stage);

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
