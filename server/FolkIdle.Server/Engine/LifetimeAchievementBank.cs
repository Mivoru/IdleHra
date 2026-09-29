using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The Lifetime chapter of the Book of Deeds (task 57): the four tiered
    /// achievements, read as named tiers, and the one of them that is paid on
    /// read.
    ///
    /// Treasury, Master Smith and Logistics are paid by the checkpoint when a
    /// tier is crossed, because their counters live on the tick. Monster
    /// Slayer's counter is the codex, which the Book already sums - so it is
    /// paid here, the same way the Book banks a Seal: when the page is read.
    /// </summary>
    public static class LifetimeAchievementBank
    {
        public sealed class TierView
        {
            public string Name { get; set; } = string.Empty;
            public string Goal { get; set; } = string.Empty;
            public int Diamonds { get; set; }
            public bool Reached { get; set; }
        }

        public sealed class AchievementView
        {
            public int Id { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public long Progress { get; set; }
            public int CompletedTier { get; set; }
            public List<TierView> Tiers { get; set; } = new();
        }

        /// <summary>
        /// Pays Monster Slayer once, when the codex says ten thousand kills.
        /// Returns the diamonds paid (0 when already paid or not yet earned)
        /// and the balance after, for the live session's sync.
        ///
        /// Modul: A CONDITIONAL UPDATE IS THE LOCK. The Book is read on a GET,
        /// and GETs are not serialised per account (HttpRouter's striped lock
        /// covers mutations only), so two tabs reading it at once must not
        /// both pay. The tier moves 0 -> 1 in one statement and only the
        /// request that moved it pays.
        /// </summary>
        public static async Task<(int Paid, int Balance)> BankMonsterSlayerAsync(FolkIdleDbContext db, long playerId, long totalKills)
        {
            if (totalKills < AchievementMilestones.MonsterKillThreshold) return (0, 0);

            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO player_lifetime_achievements (""PlayerId"", ""AchievementId"", ""CurrentProgress"", ""IsClaimed"", ""CompletedTier"")
VALUES ({playerId}, {AchievementMilestones.MonsterKillAchievementId}, 0, FALSE, 0)
ON CONFLICT (""PlayerId"", ""AchievementId"") DO NOTHING");

            int moved = await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE player_lifetime_achievements
SET ""CompletedTier"" = 1, ""IsClaimed"" = TRUE, ""CurrentProgress"" = {totalKills}
WHERE ""PlayerId"" = {playerId} AND ""AchievementId"" = {AchievementMilestones.MonsterKillAchievementId} AND ""CompletedTier"" < 1");

            if (moved != 1)
            {
                await tx.RollbackAsync();
                return (0, 0);
            }

            var balance = await db.Database.SqlQuery<int>($@"
UPDATE ""PlayerRecords"" SET ""PremiumDiamonds"" = ""PremiumDiamonds"" + {AchievementMilestones.MonsterKillReward}
WHERE ""Id"" = {playerId}
RETURNING ""PremiumDiamonds"" AS ""Value""").ToListAsync();

            await tx.CommitAsync();
            return (AchievementMilestones.MonsterKillReward, balance.FirstOrDefault());
        }

        /// <summary>The four achievements as the Book shows them.</summary>
        public static async Task<List<AchievementView>> ReadAsync(FolkIdleDbContext db, long playerId, long totalKills)
        {
            var rows = await db.PlayerLifetimeAchievements.AsNoTracking()
                .Where(a => a.PlayerId == playerId)
                .ToDictionaryAsync(a => a.AchievementId);

            var result = new List<AchievementView>(AchievementMilestones.LifetimeAchievementIds.Length);
            foreach (int id in AchievementMilestones.LifetimeAchievementIds)
            {
                rows.TryGetValue(id, out var row);
                int completed = row?.CompletedTier ?? 0;
                long progress = id == AchievementMilestones.MonsterKillAchievementId ? totalKills : row?.CurrentProgress ?? 0;

                var view = new AchievementView
                {
                    Id = id,
                    Title = AchievementMilestones.TitleFor(id),
                    Description = AchievementMilestones.DescriptionFor(id),
                    Progress = progress,
                    CompletedTier = completed,
                };
                var tiers = AchievementMilestones.TiersFor(id);
                for (int i = 0; i < tiers.Count; i++)
                {
                    view.Tiers.Add(new TierView
                    {
                        Name = tiers[i].Name,
                        Goal = tiers[i].Goal,
                        Diamonds = tiers[i].Diamonds,
                        Reached = completed >= i + 1,
                    });
                }
                result.Add(view);
            }
            return result;
        }
    }
}
