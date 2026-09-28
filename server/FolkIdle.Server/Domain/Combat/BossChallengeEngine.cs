using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Domain.Combat
{
    public sealed record BossChallengeGrant(BossChallenge Challenge, long ChestId, int ChestRarity);

    public sealed record BossChallengeRow(int Region, int BossMonsterId, int ChestRarity, IReadOnlyList<BossChallengeStatus> Challenges);

    public sealed record BossChallengeStatus(string Id, string Title, string Description, bool Completed);

    /// <summary>
    /// Task 55: turning a boss kill into completed challenges and their chests.
    /// Called off the tick by CosmeticGrantEngine, which owns the chest grant
    /// and the loot-feed notice; the tick only records what the fight was.
    ///
    /// Raw SQL on snake_case tables with PascalCase quoted columns
    /// (CURRENT_IMPLEMENTATION_STATE.md §3).
    /// </summary>
    public static class BossChallengeEngine
    {
        public static async Task<List<BossChallengeGrant>> JudgeKillAsync(
            FolkIdleDbContext db, long playerId, int region, int level, bool ateDuringFight, int fightTenths, DateTime utcNow)
        {
            var met = BossChallengeRegistry.Met(region, level, ateDuringFight, fightTenths);
            var grants = new List<BossChallengeGrant>();
            if (met.Count == 0) return grants;

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var at = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            int rarity = BossChallengeRegistry.RewardChestRarityFor(region);

            foreach (var challenge in met)
            {
                int inserted = await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO boss_challenge_completions (\"PlayerId\", \"Region\", \"Challenge\", \"CompletedAtUtc\") " +
                    "VALUES ({0}, {1}, {2}, {3}) ON CONFLICT (\"PlayerId\", \"Region\", \"Challenge\") DO NOTHING",
                    playerId, (byte)region, (byte)challenge, at);
                if (inserted == 0) continue;

                long chestId = (await CosmeticEngine.InsertChestsAsync(db, playerId, new[] { rarity }, CosmeticSource.Challenge, utcNow)).Single();
                grants.Add(new BossChallengeGrant(challenge, chestId, rarity));
            }

            await tx.CommitAsync();
            return grants;
        }

        public static async Task<List<BossChallengeRow>> ViewAsync(FolkIdleDbContext db, long playerId)
        {
            var done = await db.BossChallengeCompletions.AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .Select(c => new { c.Region, c.Challenge })
                .ToListAsync();
            var doneSet = done.Select(d => (d.Region, d.Challenge)).ToHashSet();

            var rows = new List<BossChallengeRow>();
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                var statuses = BossChallengeRegistry.All
                    .Select(c =>
                    {
                        var def = BossChallengeRegistry.Describe(c, region);
                        return new BossChallengeStatus(c.ToString(), def.Title, def.Description, doneSet.Contains(((byte)region, (byte)c)));
                    })
                    .ToList();
                rows.Add(new BossChallengeRow(region, RaceUnlockRegistry.GetRegionBossMonsterId(region),
                    BossChallengeRegistry.RewardChestRarityFor(region), statuses));
            }
            return rows;
        }
    }
}
