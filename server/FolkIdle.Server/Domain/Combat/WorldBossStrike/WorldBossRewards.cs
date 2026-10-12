using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>One weekly damage milestone. Gold is in MINUTES of the player's own income (GoldRewardScale).</summary>
    public sealed record WorldBossMilestone(
        int Index, long Damage, int GoldMinutes, int Diamonds,
        string? AvatarId, string? TitleSlug, string? PetId);

    /// <summary>A world boss avatar: bound (never in a chest, never on the market), art under /sprites/.</summary>
    public sealed record WorldBossAvatar(string Id, string Name, string Art);

    /// <summary>
    /// Every reward Chernobog pays, in one place. Spec:
    /// docs/superpowers/specs/2026-10-13-chernobog-world-boss-design.md, section 3.
    /// </summary>
    /// <remarks>
    /// Modul: THE OWNER'S NUMBERS (2026-10-13), and why they are shaped this way.
    ///
    /// Gold scales with the player's own hourly income because a flat 250,000
    /// is nothing to an account earning 8M an hour - the owner's own - and a
    /// fortune to a new one. Diamonds do NOT scale, and the five milestones add
    /// up to exactly the Delve's weekly ceiling (60), so the boss cannot quietly
    /// out-pay the game's calibrated diamond tap; WorldBossRewardTests asserts
    /// the sum against DelveRegistry rather than trusting this comment.
    ///
    /// Avatars, titles and the pet are paid ONCE per account, ever. Gold and
    /// diamonds are paid every week the milestone is crossed again.
    ///
    /// The upper thresholds are estimates from two real strikes on production
    /// (17,280 and 2,199, 2026-10-12). They are meant to be re-measured from a
    /// full week's attempt rows and retuned here.
    /// </remarks>
    public static class WorldBossRewards
    {
        public const string SenderName = "Chernobog";

        public static readonly WorldBossAvatar SvarogAvatar = new("avatar_wb_svarog", "Svarog", "WorldBoss/avatars/svarog.webp");
        public static readonly WorldBossAvatar SerpentAvatar = new("avatar_wb_black_serpent", "Black Serpent", "WorldBoss/avatars/black_serpent.webp");
        public static readonly WorldBossAvatar ChernobogAvatar = new("avatar_wb_chernobog", "Chernobog", "WorldBoss/avatars/chernobog.webp");
        public static readonly IReadOnlyList<WorldBossAvatar> Avatars = new[] { SvarogAvatar, SerpentAvatar, ChernobogAvatar };

        // Permanent titles, earned by a milestone.
        public const string SerpentBaneSlug = "wb_serpent_bane";
        public const string ChainerSlug = "wb_chainer_of_the_dark";

        // Temporary titles: worn by last week's top three until the next payout.
        public const string FirstPlaceSlug = "wb_svarogs_hammer";
        public const string SecondPlaceSlug = "wb_dazhbogs_light";
        public const string ThirdPlaceSlug = "wb_simargls_wing";
        public static readonly IReadOnlyList<string> PlaceTitleSlugs = new[] { FirstPlaceSlug, SecondPlaceSlug, ThirdPlaceSlug };

        /// <summary>Hours of income the weekly top three receive, by place.</summary>
        public static readonly IReadOnlyList<int> PlaceGoldHours = new[] { 3, 2, 1 };

        public static readonly IReadOnlyList<WorldBossMilestone> Milestones = new[]
        {
            new WorldBossMilestone(0, 10_000, 15, 5, null, null, null),
            new WorldBossMilestone(1, 25_000, 30, 10, null, null, null),
            new WorldBossMilestone(2, 60_000, 60, 15, SvarogAvatar.Id, null, null),
            new WorldBossMilestone(3, 150_000, 120, 15, SerpentAvatar.Id, SerpentBaneSlug, null),
            new WorldBossMilestone(4, 400_000, 180, 15, ChernobogAvatar.Id, ChainerSlug, null),
            new WorldBossMilestone(5, 1_000_000, 0, 0, null, null, Progression.PetRegistry.BlackSerpentId),
        };

        /// <summary>The kill reward: hours of income, and one Legendary cosmetic chest, to every registered player.</summary>
        public const int KillGoldHours = 2;

        /// <summary>A registered player flagged for sandbagging gets this share of the kill gold, and no chest.</summary>
        public const int SandbaggerKillSharePct = 25;

        /// <summary>The milestones the total <paramref name="weeklyDamage"/> has reached that <paramref name="paidMask"/> has not paid.</summary>
        public static IEnumerable<WorldBossMilestone> NewlyReached(long weeklyDamage, int paidMask)
            => Milestones.Where(m => weeklyDamage >= m.Damage && (paidMask & (1 << m.Index)) == 0);

        public static WorldBossAvatar? FindAvatar(string? id) => Avatars.FirstOrDefault(a => a.Id == id);

        /// <summary>
        /// What Chernobog says in World chat about a sandbagger (owner, 2026-10-13:
        /// one of the three, at random - short, and a little humiliating).
        /// </summary>
        public static readonly IReadOnlyList<string> SandbaggerTaunts = new[]
        {
            "{name} struck me like a sleepy moth on Monday. The forge thanks you for your... restraint.",
            "Ha! {name} hid their strength until Wednesday. Svarog saw. So did I.",
            "{name} tried to make me smaller. I only grow fonder of mortals like you.",
        };

        public static string Taunt(string name, int roll)
            => SandbaggerTaunts[Math.Abs(roll) % SandbaggerTaunts.Count].Replace("{name}", name);
    }
}
