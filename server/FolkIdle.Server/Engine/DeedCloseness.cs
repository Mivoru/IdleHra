using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// "Closest to done" for the top of the Book of Deeds (achievements PR 1,
    /// 2026-10-31): the unfinished goals nearest their target, across the open
    /// chapters and the lifetime tiers, so the Book answers "what next" in one
    /// place instead of the player comparing six progress bars.
    /// </summary>
    /// <remarks>
    /// Modul: ONLY GOALS WITH A REAL FRACTION. A one-step deed ("wear a
    /// weapon") is 0% or done, so it would never rank and only clutter; and
    /// Master Smith's last two tiers gate on a fused RARITY while its progress
    /// counts fusions - comparing the two would print "312 / 10". Both are left
    /// out rather than shown wrong. Pure arithmetic over what the snapshot
    /// already computed, so it is tested without a database.
    /// </remarks>
    public static class DeedCloseness
    {
        public sealed record Goal(string Kind, string Id, string Title, string Screen, long Current, long Target)
        {
            public double Fraction => Target <= 0 ? 0.0 : Math.Min(1.0, (double)Current / Target);
        }

        public const int DefaultCount = 3;

        public static List<Goal> Closest(
            DeedContext context,
            int doneMask,
            IReadOnlyList<LifetimeAchievementBank.AchievementView> lifetime,
            int count = DefaultCount)
        {
            var goals = new List<Goal>();
            foreach (var chapter in DeedRegistry.Chapters)
            {
                if (!DeedRegistry.IsOpen(chapter.Index, doneMask) || DeedRegistry.IsComplete(chapter, context)) continue;
                foreach (var deed in chapter.Deeds)
                {
                    long current = deed.Progress(context);
                    if (deed.Target <= 1 || current >= deed.Target) continue;
                    goals.Add(new Goal("deed", deed.Id, deed.Title, deed.Screen, Math.Max(0, current), deed.Target));
                }
            }

            foreach (var a in lifetime)
            {
                if (a.Id == AchievementMilestones.ForgingAchievementId && a.CompletedTier >= 2) continue;
                long target = AchievementMilestones.GetNextTierTarget(a.Id, a.CompletedTier);
                if (target <= 1 || a.Progress >= target || a.CompletedTier >= a.Tiers.Count) continue;
                string tierName = a.Tiers[a.CompletedTier].Name;
                string title = tierName == a.Title ? a.Title : $"{a.Title}: {tierName}";
                goals.Add(new Goal("lifetime", $"lifetime-{a.Id}", title, "progression", Math.Max(0, a.Progress), target));
            }

            // Nearest first; a goal with nothing done yet is not "close".
            return goals.Where(g => g.Current > 0)
                .OrderByDescending(g => g.Fraction)
                .ThenBy(g => g.Target - g.Current)
                .Take(count)
                .ToList();
        }
    }
}
