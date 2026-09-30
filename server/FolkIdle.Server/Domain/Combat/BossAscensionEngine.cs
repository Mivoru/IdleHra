using System;
using System.Collections.Concurrent;
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
    /// <summary>What one newly cleared step paid: a title, and at a frame step a frame.</summary>
    public sealed record AscensionReward(int Region, int Step, string TitleSlug, string TitleName, string? FrameId, string? FrameName);

    public sealed record AscensionStepView(
        int Step, string Summary, string Kind, int Value, int TimeLimitSeconds, string[] Effects, bool Cleared, bool Startable,
        string RewardTitle, string? RewardFrame);

    public sealed record AscensionBossView(
        int Region, int BossMonsterId, bool BossDefeated, int HighestStep, int NextStep,
        IReadOnlyList<AscensionStepView> Steps);

    /// <summary>
    /// Task 87: recording an Ascension clear off the tick, and reading the
    /// ladder back for the client. The tick only NOTES the clear
    /// (<see cref="Clears"/>); CosmeticGrantEngine's worker drains it here.
    ///
    /// Modul: THE REWARD IS PAID INSIDE THE TRANSACTION THAT RAISES THE ROW.
    /// boss_ascension_progress is locked FOR UPDATE, every step above the stored
    /// highest up to the cleared one is paid, and the row is raised - so a
    /// replayed note, a second tab and a crash between the tick's clear and the
    /// worker all leave each title and frame paid exactly once, with no separate
    /// "was it paid" flag to drift from the progress it describes.
    ///
    /// Raw SQL on snake_case tables with PascalCase quoted columns
    /// (CURRENT_IMPLEMENTATION_STATE.md §3).
    /// </summary>
    public static class BossAscensionEngine
    {
        public readonly record struct ClearNote(long PlayerId, int Region, int Step, int BossMonsterId);

        /// <summary>Filled on the tick thread at a cleared step, drained by CosmeticGrantEngine.</summary>
        public static readonly ConcurrentQueue<ClearNote> Clears = new();

        public static void NoteClear(long playerId, int region, int step, int bossMonsterId)
            => Clears.Enqueue(new ClearNote(playerId, region, step, bossMonsterId));

        /// <summary>Everything newly earned by clearing <paramref name="step"/>; empty when it was already cleared.</summary>
        public static async Task<List<AscensionReward>> RecordClearAsync(
            FolkIdleDbContext db, long playerId, int region, int step, DateTime utcNow)
        {
            var paid = new List<AscensionReward>();
            if (!BossAscensionRegistry.IsValidRegion(region) || !BossAscensionRegistry.IsValidStep(step)) return paid;

            var at = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO boss_ascension_progress (\"PlayerId\", \"Region\", \"HighestStep\", \"UpdatedAtUtc\") " +
                "VALUES ({0}, {1}, 0, {2}) ON CONFLICT (\"PlayerId\", \"Region\") DO NOTHING",
                playerId, (short)region, at);

            var highest = (await db.Database.SqlQueryRaw<short>(
                "SELECT \"HighestStep\" AS \"Value\" FROM boss_ascension_progress " +
                "WHERE \"PlayerId\" = {0} AND \"Region\" = {1} FOR UPDATE",
                playerId, (short)region).ToListAsync()).Single();

            if (step <= highest)
            {
                await tx.RollbackAsync();
                return paid;
            }

            for (int s = highest + 1; s <= step; s++)
            {
                string titleSlug = BossAscensionRegistry.TitleSlug(region, s);
                await TitleEngine.GrantAsync(db, playerId, titleSlug, utcNow);

                string? frameId = BossAscensionRegistry.RewardFrameIdFor(region, s);
                string? frameName = null;
                if (frameId != null)
                {
                    var def = CosmeticRegistry.Find(frameId)!;
                    frameName = def.Name;
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO cosmetic_items (\"PlayerId\", \"Kind\", \"DefinitionId\", \"Rarity\", \"Source\", \"AcquiredAtUtc\", \"IsListed\") " +
                        "SELECT {0}, {1}, {2}, {3}, {4}, {5}, false WHERE NOT EXISTS " +
                        "(SELECT 1 FROM cosmetic_items WHERE \"PlayerId\" = {0} AND \"DefinitionId\" = {2})",
                        playerId, (short)CosmeticKind.Frame, frameId, (short)def.Rarity, (short)CosmeticSource.Ascension, at);
                }

                paid.Add(new AscensionReward(region, s, titleSlug, BossAscensionRegistry.TitleName(region, s), frameId, frameName));
            }

            await db.Database.ExecuteSqlRawAsync(
                "UPDATE boss_ascension_progress SET \"HighestStep\" = {0}, \"UpdatedAtUtc\" = {1} " +
                "WHERE \"PlayerId\" = {2} AND \"Region\" = {3}",
                (short)step, at, playerId, (short)region);

            await tx.CommitAsync();
            return paid;
        }

        /// <summary>
        /// DEV TOOLS ONLY (exercise.mjs): puts one boss's ladder back to
        /// <paramref name="step"/> - the progress row, and the titles and frames
        /// of every step above it - so a check that climbs a rung leaves the
        /// fixture as it found it.
        /// </summary>
        public static async Task DevRestoreAsync(FolkIdleDbContext db, long playerId, int region, int step, bool? bossDefeated = null)
        {
            if (!BossAscensionRegistry.IsValidRegion(region)) return;
            step = Math.Clamp(step, 0, BossAscensionRegistry.MaxStep);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            // The fixture has not beaten the boss a ladder needs beaten; a check
            // marks it beaten for the length of the run and takes the mark back.
            if (bossDefeated == true)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO monster_codex_entries (\"PlayerId\", \"MonsterId\", \"KillCount\", \"FirstDrawnRarity\", \"Level\") " +
                    "VALUES ({0}, {1}, 1, 0, 0) ON CONFLICT (\"PlayerId\", \"MonsterId\") DO UPDATE SET \"KillCount\" = GREATEST(monster_codex_entries.\"KillCount\", 1)",
                    playerId, RaceUnlockRegistry.GetRegionBossMonsterId(region));
            }
            else if (bossDefeated == false)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM monster_codex_entries WHERE \"PlayerId\" = {0} AND \"MonsterId\" = {1}",
                    playerId, RaceUnlockRegistry.GetRegionBossMonsterId(region));
            }
            for (int s = step + 1; s <= BossAscensionRegistry.MaxStep; s++)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM player_titles WHERE \"PlayerId\" = {0} AND \"TitleSlug\" = {1}",
                    playerId, BossAscensionRegistry.TitleSlug(region, s));
                string? frame = BossAscensionRegistry.RewardFrameIdFor(region, s);
                if (frame != null)
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "DELETE FROM cosmetic_items WHERE \"PlayerId\" = {0} AND \"DefinitionId\" = {1}", playerId, frame);
                }
            }
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM boss_ascension_progress WHERE \"PlayerId\" = {0} AND \"Region\" = {1}", playerId, (short)region);
            if (step > 0)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO boss_ascension_progress (\"PlayerId\", \"Region\", \"HighestStep\", \"UpdatedAtUtc\") VALUES ({0}, {1}, {2}, {3})",
                    playerId, (short)region, (short)step, DateTime.UtcNow);
            }
            await tx.CommitAsync();
        }

        /// <summary>The player's highest cleared step per boss, packed for the payload's cache (see BossAscensionRegistry.HighestStepOf).</summary>
        public static async Task<int> LoadPackedAsync(FolkIdleDbContext db, long playerId)
        {
            var rows = await db.BossAscensionProgress.AsNoTracking()
                .Where(p => p.PlayerId == playerId)
                .Select(p => new { p.Region, p.HighestStep })
                .ToListAsync();
            int packed = 0;
            foreach (var row in rows) packed = BossAscensionRegistry.WithHighestStep(packed, row.Region, row.HighestStep);
            return packed;
        }

        public static async Task<List<AscensionBossView>> ViewAsync(FolkIdleDbContext db, long playerId)
        {
            int packed = await LoadPackedAsync(db, playerId);

            var bossIds = Enumerable.Range(BossAscensionRegistry.FirstRegion, BossAscensionRegistry.LastRegion - BossAscensionRegistry.FirstRegion + 1)
                .Select(RaceUnlockRegistry.GetRegionBossMonsterId).ToList();
            var defeated = (await db.MonsterCodexEntries.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.KillCount > 0 && bossIds.Contains(c.MonsterId))
                .Select(c => c.MonsterId)
                .ToListAsync()).ToHashSet();

            var rows = new List<AscensionBossView>();
            for (int region = BossAscensionRegistry.FirstRegion; region <= BossAscensionRegistry.LastRegion; region++)
            {
                int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
                bool beaten = defeated.Contains(bossId);
                int highest = BossAscensionRegistry.HighestStepOf(packed, region);
                int next = Math.Min(highest + 1, BossAscensionRegistry.MaxStep);

                var steps = new List<AscensionStepView>();
                foreach (var def in BossAscensionRegistry.Steps)
                {
                    string? frame = BossAscensionRegistry.RewardFrameIdFor(region, def.Step);
                    steps.Add(new AscensionStepView(
                        def.Step, def.Summary, def.Kind.ToString(), def.Value,
                        BossAscensionRegistry.TimeLimitSecondsFor(region, def.Step),
                        BossAscensionRegistry.DescribeEffects(region, def.Step),
                        Cleared: def.Step <= highest,
                        Startable: beaten && def.Step <= highest + 1,
                        RewardTitle: BossAscensionRegistry.TitleName(region, def.Step),
                        RewardFrame: frame == null ? null : BossAscensionRegistry.FrameName(region, def.Step)));
                }
                rows.Add(new AscensionBossView(region, bossId, beaten, highest, next, steps));
            }
            return rows;
        }
    }
}
