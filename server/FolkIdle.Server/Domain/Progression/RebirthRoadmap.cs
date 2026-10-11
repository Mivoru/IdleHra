using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>One reward on the rebirth roadmap.</summary>
    /// <param name="Kind">skill_points, xp, pet_slot, avatar, pet, title, badge.</param>
    /// <param name="Art">A sprite path for an avatar or a pet, else null.</param>
    public sealed record RebirthRoadmapReward(string Kind, string Name, string Detail, string? Art);

    public sealed record RebirthRoadmapStep(int Rebirth, IReadOnlyList<RebirthRoadmapReward> Rewards);

    /// <summary>
    /// Modul: THE ROADMAP IS THE RULES, READ OUT (owner, 2026-10-11: "players
    /// need to clearly see the benefits ... a roadmap what a player gets each
    /// rebirth"). Every step is built from RebirthRules and the registries the
    /// rebirth itself grants from - the skill points, the milestone avatars,
    /// the pet slot, the Phoenix and the title - so the screen can never
    /// promise a reward the engine does not pay, or miss one it does.
    /// </summary>
    public static class RebirthRoadmap
    {
        public static IReadOnlyList<RebirthRoadmapStep> Build()
        {
            var phoenix = PetRegistry.Find(PetRegistry.PhoenixId);
            var title = TitleRegistry.Find(TitleRegistry.RebirthTitleSlug);
            var steps = new List<RebirthRoadmapStep>(RebirthRules.MaxRebirths);
            for (int n = 1; n <= RebirthRules.MaxRebirths; n++)
            {
                var rewards = new List<RebirthRoadmapReward>
                {
                    new("skill_points", $"+{RebirthRules.SkillPointsPerRebirth} skill points",
                        $"{RebirthRules.SkillPointsFor(n)} in all, for good", null),
                };
                if (n == 1)
                {
                    rewards.Add(new("xp", $"+{RebirthRules.ReLevelXpBonusPct}% combat XP",
                        "on every level below your best - from now on", null));
                }
                if (n == RebirthRules.SecondPetSlotRebirths)
                {
                    rewards.Add(new("pet_slot", "Second pet slot", "on every character", null));
                }
                foreach (var avatar in RebirthRules.AvatarMilestones.Where(a => a.Rebirths == n))
                {
                    rewards.Add(new("avatar", avatar.Name, "avatar", avatar.Art));
                }
                if (n == RebirthRules.MaxRebirths)
                {
                    if (phoenix != null)
                    {
                        rewards.Add(new("pet", phoenix.Name, string.Join(", ", phoenix.Bonuses.Select(PetRegistry.Describe)), phoenix.Art));
                    }
                    if (title != null)
                    {
                        rewards.Add(new("title", title.DisplayName, "title", null));
                    }
                    rewards.Add(new("badge", "Gold badge", "and a glowing name", null));
                }
                steps.Add(new RebirthRoadmapStep(n, rewards));
            }
            return steps;
        }
    }
}
