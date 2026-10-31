using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Achievements PR 1 (2026-10-31): the Book's "closest to done" strip -
    /// nearest first, across open chapters and lifetime tiers, and never a
    /// goal whose number would read wrong.
    /// </summary>
    public class DeedClosenessTests
    {
        private static LifetimeAchievementBank.AchievementView Lifetime(int id, long progress, int completed)
        {
            var view = new LifetimeAchievementBank.AchievementView
            {
                Id = id,
                Title = AchievementMilestones.TitleFor(id),
                Progress = progress,
                CompletedTier = completed,
            };
            foreach (var t in AchievementMilestones.TiersFor(id))
                view.Tiers.Add(new LifetimeAchievementBank.TierView { Name = t.Name, Goal = t.Goal, Diamonds = t.Diamonds });
            return view;
        }

        [Fact]
        public void TheNearestGoalLeads_AndUntouchedOnesAreNotClose()
        {
            // Chapter I open: 60 of 100 wood, level 3 of 10.
            var context = default(DeedContext) with { WoodStock = 60, Level = 3 };
            var lifetime = new List<LifetimeAchievementBank.AchievementView>
            {
                Lifetime(AchievementMilestones.LogisticsAchievementId, 9_000, 0), // 90% of 10,000
                Lifetime(AchievementMilestones.TreasuryAchievementId, 0, 0),      // nothing yet
            };

            var closest = DeedCloseness.Closest(context, doneMask: 0, lifetime);

            Assert.Equal("lifetime-" + AchievementMilestones.LogisticsAchievementId, closest[0].Id);
            Assert.Equal("hundred-logs", closest[1].Id);
            Assert.DoesNotContain(closest, g => g.Id == "lifetime-" + AchievementMilestones.TreasuryAchievementId);
            Assert.All(closest, g => Assert.InRange(g.Fraction, 0.0001, 0.9999));
            Assert.True(closest.Count <= DeedCloseness.DefaultCount);
        }

        [Fact]
        public void MasterSmithsRarityTiersAreLeftOut()
        {
            // Two fusion tiers done; the next gates on rarity 10, progress counts fusions.
            var lifetime = new List<LifetimeAchievementBank.AchievementView>
            {
                Lifetime(AchievementMilestones.ForgingAchievementId, 7, 2),
            };
            var closest = DeedCloseness.Closest(default(DeedContext) with { Level = 50 }, doneMask: 0, lifetime);
            Assert.DoesNotContain(closest, g => g.Id == "lifetime-" + AchievementMilestones.ForgingAchievementId);
        }
    }
}
