using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>One expedition as the screen shows it.</summary>
    public sealed record ExpeditionView(long Id, int Slot, Guid CharacterId, string Name, int Region, int Hours,
        int AptitudeTotal, long StartedAtEpoch, long ReturnsAtEpoch, bool Returned);

    /// <summary>An ancestor who could set out now.</summary>
    public sealed record ExpeditionCandidate(Guid CharacterId, string Name, int RaceId, int AptitudeTotal, int UpgradePct);

    public sealed record ExpeditionBoard(int Slots, int Region, int[] Durations, List<ExpeditionView> Active, List<ExpeditionCandidate> Candidates,
        int Claimed = 0, string NextTitle = "", int NextTitleAt = 0);

    public enum ExpeditionStartResult { Started, NoSlot, NotEligible, BadDuration, NoGrounds }

    public sealed record ExpeditionClaim(bool Ok, string Tier, int Region, string Reason, int Claimed = 0, string Title = "");

    /// <summary>Expeditions: start, list, claim. The rules are ExpeditionRules.</summary>
    public static class ExpeditionEngine
    {
        private static async Task<int> GroundsLevelAsync(FolkIdleDbContext db, long playerId)
            => await db.VillageInfrastructures.AsNoTracking()
                .Where(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.BreedingGroundsBuildingId)
                .Select(v => v.CurrentLevel).FirstOrDefaultAsync();

        private static async Task<int> RegionAsync(FolkIdleDbContext db, long playerId)
        {
            var defeated = await RegionUnlockGate.LoadDefeatedBossesAsync(db, playerId);
            return Math.Clamp(RegionUnlockGate.HighestUnlockedRegion(defeated), 1, 5);
        }

        /// <summary>Whether this character is away right now (a row exists, claimed or not).</summary>
        public static Task<bool> IsAwayAsync(FolkIdleDbContext db, Guid characterId)
            => db.Expeditions.AsNoTracking().AnyAsync(e => e.CharacterId == characterId);

        /// <summary>The ids of every character this player has away.</summary>
        public static async Task<HashSet<Guid>> AwayIdsAsync(FolkIdleDbContext db, long playerId)
            => (await db.Expeditions.AsNoTracking().Where(e => e.PlayerId == playerId).Select(e => e.CharacterId).ToListAsync()).ToHashSet();

        public static async Task<ExpeditionBoard> BoardAsync(FolkIdleDbContext db, long playerId, long nowEpoch)
        {
            int slots = ExpeditionRules.SlotsFor(await GroundsLevelAsync(db, playerId));
            int region = await RegionAsync(db, playerId);
            var rows = await db.Expeditions.AsNoTracking().Where(e => e.PlayerId == playerId).OrderBy(e => e.Slot).ToListAsync();
            var names = await db.CharacterRecords.AsNoTracking().Where(c => c.PlayerId == playerId)
                .Select(c => new { c.Id, c.Name, c.SlotIndex, c.AgePhase, c.IsLockedInEscrow }).ToListAsync();
            var nameOf = names.ToDictionary(n => n.Id, n => n.Name);

            var active = rows.Select(r => new ExpeditionView(r.Id, r.Slot, r.CharacterId,
                nameOf.TryGetValue(r.CharacterId, out var n) ? n : string.Empty, r.Region, r.Hours, r.AptitudeTotal,
                r.StartedAtEpoch, r.ReturnsAtEpoch, r.ReturnsAtEpoch <= nowEpoch)).ToList();

            var away = rows.Select(r => r.CharacterId).ToHashSet();
            var benched = names.Where(c => c.SlotIndex >= CharacterSlotEngine.MaxCharacterSlots && c.AgePhase >= AgePhaseCurve.Adult
                                           && !c.IsLockedInEscrow && !away.Contains(c.Id)).Select(c => c.Id).ToList();
            var lineages = await db.CharacterLineages.AsNoTracking().Where(l => benched.Contains(l.CharacterId)).ToListAsync();
            var candidates = lineages
                .Select(l => new ExpeditionCandidate(l.CharacterId, nameOf.TryGetValue(l.CharacterId, out var nm) ? nm : string.Empty,
                    (int)(l.GeneticVector & 0xFF), l.AptitudeVector().Sum(), ExpeditionRules.AptitudeUpgradePct(l.AptitudeVector().Sum())))
                .OrderByDescending(c => c.AptitudeTotal).ThenBy(c => c.Name).Take(30).ToList();

            int claimedSoFar = await db.PlayerRecords.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.ExpeditionsClaimed).FirstOrDefaultAsync();
            var next = ExpeditionRules.TitleMilestones.FirstOrDefault(m => m.Count > claimedSoFar);
            return new ExpeditionBoard(slots, region, ExpeditionRules.Durations, active, candidates,
                claimedSoFar, next.Name ?? string.Empty, next.Count);
        }

        public static async Task<ExpeditionStartResult> StartAsync(FolkIdleDbContext db, long playerId, Guid characterId, int hours, long nowEpoch)
        {
            if (!ExpeditionRules.IsDuration(hours)) return ExpeditionStartResult.BadDuration;
            int slots = ExpeditionRules.SlotsFor(await GroundsLevelAsync(db, playerId));
            if (slots == 0) return ExpeditionStartResult.NoGrounds;

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var character = await db.CharacterRecords
                .FromSqlRaw("SELECT * FROM characters WHERE \"Id\" = {0} AND \"PlayerId\" = {1} FOR UPDATE", characterId, playerId)
                .SingleOrDefaultAsync();
            if (character == null || character.SlotIndex < CharacterSlotEngine.MaxCharacterSlots
                || character.AgePhase < AgePhaseCurve.Adult || character.IsLockedInEscrow)
            {
                await tx.SafeRollbackAsync();
                return ExpeditionStartResult.NotEligible;
            }

            var used = await db.Expeditions.Where(e => e.PlayerId == playerId).ToListAsync();
            if (used.Any(e => e.CharacterId == characterId))
            {
                await tx.SafeRollbackAsync();
                return ExpeditionStartResult.NotEligible;
            }
            int slot = Enumerable.Range(0, slots).FirstOrDefault(s => used.All(e => e.Slot != s), -1);
            if (slot < 0)
            {
                await tx.SafeRollbackAsync();
                return ExpeditionStartResult.NoSlot;
            }

            var lineage = await db.CharacterLineages.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == characterId);
            long returns = nowEpoch + hours * 3600L;
            db.Expeditions.Add(new Expedition
            {
                PlayerId = playerId, CharacterId = characterId, Slot = slot, Region = await RegionAsync(db, playerId), Hours = hours,
                AptitudeTotal = lineage?.AptitudeVector().Sum() ?? 0, StartedAtEpoch = nowEpoch, ReturnsAtEpoch = returns,
            });
            // Away means away: resting for breeding until the return (the
            // breeding gate already refuses a resting parent and says so).
            character.IsBreedingActive = true;
            character.BreedingCooldownEndEpoch = Math.Max(character.BreedingCooldownEndEpoch, returns);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return ExpeditionStartResult.Started;
        }

        public static async Task<ExpeditionClaim> ClaimAsync(FolkIdleDbContext db, long playerId, long expeditionId, long nowEpoch, DateTime utcNow, Random rng)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var row = await db.Expeditions
                .FromSqlRaw("SELECT * FROM \"Expeditions\" WHERE \"Id\" = {0} AND \"PlayerId\" = {1} FOR UPDATE", expeditionId, playerId)
                .SingleOrDefaultAsync();
            if (row == null)
            {
                await tx.SafeRollbackAsync();
                return new ExpeditionClaim(false, string.Empty, 0, "That expedition is not yours, or was already claimed.");
            }
            if (row.ReturnsAtEpoch > nowEpoch)
            {
                await tx.SafeRollbackAsync();
                return new ExpeditionClaim(false, string.Empty, row.Region, "Not back yet.");
            }

            var tier = ExpeditionRules.ChestFor(row.Hours, row.AptitudeTotal, rng.Next(100));
            LootChestEngine.Stage(db, playerId, tier, row.Region, LootChestSource.Expedition, utcNow);
            db.Expeditions.Remove(row);
            await db.SaveChangesAsync();

            // The count and its titles, in the same transaction (GrantAsync joins
            // it and is idempotent, so at-or-past is safe to ask every claim).
            int claimed = (await db.Database.SqlQueryRaw<int>(
                "UPDATE \"PlayerRecords\" SET \"ExpeditionsClaimed\" = \"ExpeditionsClaimed\" + 1 WHERE \"Id\" = {0} RETURNING \"ExpeditionsClaimed\" AS \"Value\"",
                playerId).ToListAsync()).FirstOrDefault();
            string title = string.Empty;
            foreach (var m in ExpeditionRules.TitleMilestones)
            {
                if (claimed >= m.Count && await TitleEngine.GrantAsync(db, playerId, m.Slug, utcNow)) title = m.Name;
            }
            await tx.CommitAsync();
            return new ExpeditionClaim(true, tier.ToString(), row.Region, string.Empty, claimed, title);
        }

        /// <summary>DEV TOOLS ONLY (exercise.mjs): brings every expedition of this player back now.</summary>
        public static Task<int> DevReturnAllAsync(FolkIdleDbContext db, long playerId, long nowEpoch)
        {
            return db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE characters SET ""BreedingCooldownEndEpoch"" = {nowEpoch}
                WHERE ""Id"" IN (SELECT ""CharacterId"" FROM ""Expeditions"" WHERE ""PlayerId"" = {playerId}) AND ""BreedingCooldownEndEpoch"" > {nowEpoch};
                UPDATE ""Expeditions"" SET ""ReturnsAtEpoch"" = {nowEpoch} WHERE ""PlayerId"" = {playerId} AND ""ReturnsAtEpoch"" > {nowEpoch};");
        }
    }
}
