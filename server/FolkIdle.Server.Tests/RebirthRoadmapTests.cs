using System;
using System.IO;
using System.Linq;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The Rebirth screen's roadmap (owner, 2026-10-11) is the rules read out.
    /// These hold it to what RebirthEngine actually pays, so a reward added to
    /// one and not the other fails here rather than on a player's screen.
    /// </summary>
    public class RebirthRoadmapTests
    {
        [Fact]
        public void EveryRebirthUpToTheCapHasAStep_AndEachPaysItsSkillPoints()
        {
            var steps = RebirthRoadmap.Build();
            Assert.Equal(Enumerable.Range(1, RebirthRules.MaxRebirths), steps.Select(s => s.Rebirth));
            // Skill points through the tenth; the bloodline ceiling on every one.
            Assert.All(steps.Where(s => s.Rebirth <= RebirthRules.RewardedRebirths), s => Assert.Contains(s.Rewards, r => r.Kind == "skill_points"));
            Assert.All(steps.Where(s => s.Rebirth > RebirthRules.RewardedRebirths), s => Assert.DoesNotContain(s.Rewards, r => r.Kind == "skill_points"));
            Assert.All(steps, s => Assert.Contains(s.Rewards, r => r.Kind == "bloodline" && r.Name.EndsWith(" " + BreedingAptitudes.CeilingFor(s.Rebirth))));
            Assert.Contains(steps[^1].Rewards, r => r.Kind == "bloodline" && r.Name.EndsWith(" " + BreedingAptitudes.MaxValue));
        }

        [Fact]
        public void TheMilestonesAreWhereTheEngineGrantsThem()
        {
            var steps = RebirthRoadmap.Build().ToDictionary(s => s.Rebirth);
            foreach (var avatar in RebirthRules.AvatarMilestones)
            {
                Assert.Contains(steps[avatar.Rebirths].Rewards, r => r.Kind == "avatar" && r.Art == avatar.Art);
            }
            Assert.Contains(steps[RebirthRules.SecondPetSlotRebirths].Rewards, r => r.Kind == "pet_slot");
            var last = steps[RebirthRules.RewardedRebirths].Rewards;
            Assert.Contains(last, r => r.Kind == "pet" && r.Name == "Phoenix");
            Assert.Contains(last, r => r.Kind == "title");
            Assert.Contains(last, r => r.Kind == "badge");
            Assert.Contains(steps[1].Rewards, r => r.Kind == "xp");
        }

        [Fact]
        public void EveryRoadmapPictureExistsOnDisk()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "client_web"))) dir = dir.Parent;
            string root = Path.Combine(dir!.FullName, "client", "Assets", "Images", "SpritesWeb");
            foreach (var reward in RebirthRoadmap.Build().SelectMany(s => s.Rewards).Where(r => r.Art != null))
            {
                Assert.True(File.Exists(Path.Combine(root, reward.Art!)), reward.Art);
            }
        }
    }
}
