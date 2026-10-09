using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Domain.Progression
{
    public enum CosmeticResult
    {
        Ok = 0,
        /// <summary>No unopened, unlisted chest of that rarity.</summary>
        NoChest,
        /// <summary>The id names no cosmetic, or names a chest where an avatar/frame was asked for.</summary>
        UnknownCosmetic,
        /// <summary>A real avatar/frame the player owns no un-listed copy of.</summary>
        NotOwned,
        PlayerNotFound,
    }

    public sealed record OwnedCosmetic(long Id, string DefinitionId, byte Kind, byte Rarity, bool IsListed);

    public sealed record CosmeticsView(
        string? Result,
        int[] Chests,
        IReadOnlyList<OwnedCosmetic> Owned,
        string? EquippedAvatarId,
        string? EquippedFrameId,
        OpenedCosmetic? Opened);

    public sealed record OpenedCosmetic(long Id, string DefinitionId, byte Kind, byte Rarity, string Name);

    /// <summary>What another player wears, for chat and lists. Race and sex
    /// pick the default portrait when no avatar is worn. The worn title rides
    /// here too - it is shown on exactly the rows that show the portrait, so
    /// one batched lookup serves both.</summary>
    public sealed record WornCosmetics(long PlayerId, string? AvatarId, string? FrameId, int RaceId, bool IsFemale,
        string? Title, string? TitleColor);

    /// <summary>
    /// Task 54: owning, opening and wearing cosmetics. See CosmeticRegistry for
    /// what exists and CosmeticGrantEngine for the level chest.
    ///
    /// Raw SQL on a snake_case table with PascalCase quoted columns (see
    /// CURRENT_IMPLEMENTATION_STATE.md §3): `cosmetic_items`, and
    /// `"PlayerRecords"` quoted.
    /// </summary>
    public static class CosmeticEngine
    {
        public static async Task<CosmeticsView> ViewAsync(FolkIdleDbContext db, long playerId, string? result = null, OpenedCosmetic? opened = null)
        {
            var player = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == playerId)
                .Select(p => new { p.EquippedAvatarId, p.EquippedFrameId })
                .SingleOrDefaultAsync();

            var rows = await db.CosmeticItems.AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .OrderBy(c => c.Id)
                .ToListAsync();

            var chests = new int[CosmeticRegistry.MaxRarity + 1];
            var owned = new List<OwnedCosmetic>();
            foreach (var row in rows)
            {
                // A row whose definition the registry no longer knows is kept
                // and not shown, as TitleEngine does with a retired slug.
                if (CosmeticRegistry.Find(row.DefinitionId) == null) continue;
                if (row.Kind == (byte)CosmeticKind.Chest)
                {
                    if (!row.IsListed && row.Rarity <= CosmeticRegistry.MaxRarity) chests[row.Rarity]++;
                }
                owned.Add(new OwnedCosmetic(row.Id, row.DefinitionId, row.Kind, row.Rarity, row.IsListed));
            }

            return new CosmeticsView(result, chests, owned, player?.EquippedAvatarId, player?.EquippedFrameId, opened);
        }

        /// <summary>
        /// Opens the player's oldest un-listed chest of <paramref name="rarity"/>:
        /// the chest row is deleted and the rolled avatar/frame inserted, in one
        /// transaction. Answers NoChest, visibly, when there is none.
        /// </summary>
        public static async Task<(CosmeticResult Result, OpenedCosmetic? Opened)> OpenAsync(
            FolkIdleDbContext db, long playerId, int rarity, Random random, DateTime utcNow)
        {
            if (rarity < CosmeticRegistry.Common || rarity > CosmeticRegistry.MaxRarity)
            {
                return (CosmeticResult.NoChest, null);
            }

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var chest = await db.CosmeticItems
                .FromSqlRaw(
                    "SELECT * FROM cosmetic_items WHERE \"PlayerId\" = {0} AND \"Kind\" = {1} AND \"Rarity\" = {2} " +
                    "AND NOT \"IsListed\" ORDER BY \"Id\" LIMIT 1 FOR UPDATE",
                    playerId, (byte)CosmeticKind.Chest, (byte)rarity)
                .SingleOrDefaultAsync();
            if (chest == null)
            {
                await tx.RollbackAsync();
                return (CosmeticResult.NoChest, null);
            }

            // The chest's rarity sets the odds; the roll picks the result
            // (CosmeticRegistry.ChestContentPermille).
            int resultRarity = CosmeticRegistry.RollChestContent(rarity, random.NextDouble());
            var def = CosmeticRegistry.PickFromChest(resultRarity, random);
            db.CosmeticItems.Remove(chest);
            var item = new CosmeticItem
            {
                PlayerId = playerId,
                Kind = (byte)def.Kind,
                DefinitionId = def.Id,
                Rarity = (byte)def.Rarity,
                Source = chest.Source,
                AcquiredAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            };
            db.CosmeticItems.Add(item);
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            return (CosmeticResult.Ok, new OpenedCosmetic(item.Id, def.Id, item.Kind, item.Rarity, def.Name));
        }

        /// <summary>
        /// Wears an avatar or frame by definition id, or goes back to the
        /// default with null. Only a definition the player owns an un-listed
        /// row of can be worn - a listed copy is on its way to someone else.
        /// </summary>
        public static async Task<CosmeticResult> EquipAsync(FolkIdleDbContext db, long playerId, CosmeticKind kind, string? definitionId)
        {
            if (kind != CosmeticKind.Avatar && kind != CosmeticKind.Frame) return CosmeticResult.UnknownCosmetic;

            var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
            if (player == null) return CosmeticResult.PlayerNotFound;

            if (definitionId != null)
            {
                var def = CosmeticRegistry.Find(definitionId);
                if (def == null || def.Kind != kind) return CosmeticResult.UnknownCosmetic;
                bool owned = await db.CosmeticItems.AsNoTracking()
                    .AnyAsync(c => c.PlayerId == playerId && c.DefinitionId == definitionId && !c.IsListed);
                if (!owned) return CosmeticResult.NotOwned;
            }

            if (kind == CosmeticKind.Avatar) player.EquippedAvatarId = definitionId;
            else player.EquippedFrameId = definitionId;
            await db.SaveChangesAsync();
            return CosmeticResult.Ok;
        }

        /// <summary>
        /// Inserts one chest row per rarity in <paramref name="rarities"/> on
        /// the CALLER's context and transaction, and answers the new ids in the
        /// same order. Raw SQL so it adds nothing to the caller's change
        /// tracker - the loot worker's retry path saves that tracker again.
        /// </summary>
        public static async Task<List<long>> InsertChestsAsync(
            FolkIdleDbContext db, long playerId, IReadOnlyList<int> rarities, CosmeticSource source, DateTime utcNow)
        {
            var ids = new List<long>(rarities.Count);
            var at = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            foreach (int rarity in rarities)
            {
                var inserted = await db.Database.SqlQueryRaw<long>(
                    "INSERT INTO cosmetic_items (\"PlayerId\", \"Kind\", \"DefinitionId\", \"Rarity\", \"Source\", \"AcquiredAtUtc\", \"IsListed\") " +
                    "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, false) RETURNING \"Id\" AS \"Value\"",
                    playerId, (byte)CosmeticKind.Chest, CosmeticRegistry.ChestId(rarity), (byte)rarity, (byte)source, at)
                    .ToListAsync();
                ids.Add(inserted.Single());
            }
            return ids;
        }

        /// <summary>
        /// Pays the level chests owed at <paramref name="level"/> - one per
        /// LevelsPerChest - that have not been paid yet, and records them paid.
        /// Idempotent under the PlayerRecords row lock: a repeated or stale
        /// request pays nothing. Answers the rarities granted, with their ids.
        ///
        /// The level comes from the caller (the live payload), not from the
        /// row: the row's CurrentLevel lags by up to a checkpoint. The larger
        /// of the two is used, so a stale request cannot hold anything back.
        /// </summary>
        public static async Task<List<(long Id, int Rarity)>> GrantLevelChestsAsync(
            FolkIdleDbContext db, long playerId, int level, Random random, DateTime utcNow)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var row = await db.Database.SqlQueryRaw<LevelChestRow>(
                    "SELECT \"CurrentLevel\", \"LevelChestsGranted\" FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE",
                    playerId)
                .ToListAsync();
            if (row.Count == 0)
            {
                await tx.RollbackAsync();
                return new List<(long, int)>();
            }

            int owed = CosmeticRegistry.LevelChestsOwed(Math.Max(level, row[0].CurrentLevel));
            int paid = row[0].LevelChestsGranted;
            if (owed <= paid)
            {
                await tx.RollbackAsync();
                return new List<(long, int)>();
            }

            var rarities = new List<int>(owed - paid);
            for (int i = paid; i < owed; i++) rarities.Add(CosmeticRegistry.RollLevelChest(random.NextDouble()));

            var ids = await InsertChestsAsync(db, playerId, rarities, CosmeticSource.Level, utcNow);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"PlayerRecords\" SET \"LevelChestsGranted\" = {0} WHERE \"Id\" = {1}", owed, playerId);
            await tx.CommitAsync();

            return ids.Select((id, i) => (id, rarities[i])).ToList();
        }

        private sealed class LevelChestRow
        {
            public int CurrentLevel { get; set; }
            public int LevelChestsGranted { get; set; }
        }

        /// <summary>
        /// What each of <paramref name="playerIds"/> wears, for chat and lists.
        /// Unknown ids are left out. A worn id the registry no longer knows
        /// reads as the default.
        /// </summary>
        public static async Task<List<WornCosmetics>> WornAsync(FolkIdleDbContext db, IReadOnlyCollection<long> playerIds)
        {
            if (playerIds.Count == 0) return new List<WornCosmetics>();

            var players = await db.PlayerRecords.AsNoTracking()
                .Where(p => playerIds.Contains(p.Id))
                .Select(p => new { p.Id, p.EquippedAvatarId, p.EquippedFrameId, p.ActiveTitleSlug })
                .ToListAsync();

            // The default portrait is the MAIN character's - SlotIndex 0, the
            // one StateCheckpointManager loads as Slot1 - and race is the low
            // byte of its genetic vector, exactly as the tick reads it.
            var mains = await db.CharacterRecords.AsNoTracking()
                .Where(c => playerIds.Contains(c.PlayerId) && !c.IsLockedInEscrow)
                .OrderBy(c => c.SlotIndex)
                .Select(c => new { c.PlayerId, c.SlotIndex, c.IsFemale, Vector = c.Lineage != null ? c.Lineage.GeneticVector : 0L })
                .ToListAsync();
            var mainByPlayer = mains.GroupBy(m => m.PlayerId).ToDictionary(g => g.Key, g => g.First());

            return players.Select(p =>
            {
                mainByPlayer.TryGetValue(p.Id, out var main);
                var title = TitleRegistry.Find(p.ActiveTitleSlug);
                return new WornCosmetics(
                    p.Id,
                    CosmeticRegistry.Find(p.EquippedAvatarId)?.Id,
                    CosmeticRegistry.Find(p.EquippedFrameId)?.Id,
                    main == null ? 0 : (int)(main.Vector & 0xFF),
                    main?.IsFemale ?? false,
                    title?.DisplayName,
                    title?.Color);
            }).ToList();
        }
    }
}
