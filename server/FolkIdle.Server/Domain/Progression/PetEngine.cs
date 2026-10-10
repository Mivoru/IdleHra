using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>A rare pet found on the tick, waiting to be saved.</summary>
    public readonly record struct PetDropNote(long PlayerId, string PetId);

    public enum PetAssignResult
    {
        Ok = 0,
        NotOwned = 1,
        NotYourCharacter = 2,
        Failed = 3,
    }

    public sealed record PetView(
        string Id, string Name, int Source, int EventId, string Art,
        IReadOnlyList<string> Bonuses, bool Owned, Guid? CharacterId);

    /// <summary>
    /// Owning and placing pets. Every write runs under the PlayerRecords row
    /// lock, and the two unique indexes on player_pets are the second guard:
    /// a pet is owned once, and a character carries one.
    ///
    /// Modul: A PLACEMENT RE-STATS THE CHARACTER THE WAY AN EQUIP DOES. The
    /// pet's bonuses live on the character's EquippedAffixTotals, so moving a
    /// pet publishes the same EquipmentSlotUpdateNotification an equip does,
    /// for each character it touched - otherwise the bonus would only arrive
    /// at the next login, the exact "computed but not loaded" shape this
    /// codebase keeps finding.
    /// </summary>
    public static class PetEngine
    {
        public static readonly ConcurrentQueue<PetDropNote> Drops = new();

        /// <summary>Gives a pet unless the account already has it. True if it was new.</summary>
        public static async Task<bool> GrantAsync(FolkIdleDbContext db, long playerId, string petId, DateTime utcNow)
        {
            if (PetRegistry.Find(petId) == null) return false;
            int inserted = await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_pets (\"PlayerId\", \"PetId\", \"CharacterId\", \"AcquiredAtUtc\") " +
                "VALUES ({0}, {1}, NULL, {2}) ON CONFLICT (\"PlayerId\", \"PetId\") DO NOTHING",
                playerId, petId, DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
            return inserted > 0;
        }

        public static async Task<bool> OwnsAsync(FolkIdleDbContext db, long playerId, string petId)
            => await db.PlayerPets.AnyAsync(p => p.PlayerId == playerId && p.PetId == petId);

        /// <summary>
        /// Puts <paramref name="petId"/> on <paramref name="characterId"/>, or
        /// rests it when the character is null. A character that already had a
        /// pet hands it back to rest; a pet that followed someone else leaves
        /// them. Returns the notifications to publish (one per character whose
        /// totals changed).
        /// </summary>
        public static async Task<(PetAssignResult Result, List<EquipmentSlotUpdateNotification> Updates)> AssignAsync(
            FolkIdleDbContext db, long playerId, string petId, Guid? characterId)
        {
            var updates = new List<EquipmentSlotUpdateNotification>();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId);

            var pet = await db.PlayerPets.FirstOrDefaultAsync(p => p.PlayerId == playerId && p.PetId == petId);
            if (pet == null)
            {
                await tx.RollbackAsync();
                return (PetAssignResult.NotOwned, updates);
            }

            if (characterId.HasValue)
            {
                bool mine = await db.CharacterRecords.AnyAsync(c => c.Id == characterId.Value && c.PlayerId == playerId);
                if (!mine)
                {
                    await tx.RollbackAsync();
                    return (PetAssignResult.NotYourCharacter, updates);
                }
            }

            var touched = new HashSet<Guid>();
            if (pet.CharacterId.HasValue) touched.Add(pet.CharacterId.Value);
            if (characterId.HasValue)
            {
                touched.Add(characterId.Value);
                // The character's previous pet goes back to rest, first - the
                // one-pet-per-character index would refuse the move otherwise.
                var previous = await db.PlayerPets.FirstOrDefaultAsync(p => p.CharacterId == characterId.Value && p.Id != pet.Id);
                if (previous != null)
                {
                    previous.CharacterId = null;
                    await db.SaveChangesAsync();
                }
            }

            pet.CharacterId = characterId;
            await db.SaveChangesAsync();

            foreach (var id in touched)
            {
                var character = await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
                if (character != null) updates.Add(await EquipmentSlotEngine.BuildNotificationAsync(db, character));
            }

            await tx.CommitAsync();
            // The account's tools, read after the commit like an equip's.
            for (int i = 0; i < updates.Count; i++)
            {
                updates[i] = await EquipmentSlotEngine.WithAccountToolsAsync(db, updates[i]);
            }
            return (PetAssignResult.Ok, updates);
        }

        /// <summary>Every pet there is, with what this account owns and where each one is.</summary>
        public static async Task<List<PetView>> ViewAsync(FolkIdleDbContext db, long playerId)
        {
            var owned = await db.PlayerPets.AsNoTracking()
                .Where(p => p.PlayerId == playerId)
                .ToDictionaryAsync(p => p.PetId, p => p.CharacterId, StringComparer.Ordinal);
            // A pet whose character is gone (a rebirth, an ancestor let go)
            // reads as resting: it follows nobody who can use it, and the next
            // placement overwrites the stale id.
            var living = (await db.CharacterRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .Select(c => c.Id)
                .ToListAsync()).ToHashSet();
            foreach (var key in owned.Keys.ToList())
            {
                if (owned[key] is Guid g && !living.Contains(g)) owned[key] = null;
            }
            return PetRegistry.All.Select(p => new PetView(
                p.Id, p.Name, (int)p.Source, p.EventId, p.Art,
                p.Bonuses.Select(PetRegistry.Describe).ToList(),
                owned.ContainsKey(p.Id),
                owned.TryGetValue(p.Id, out var at) ? at : null)).ToList();
        }

        /// <summary>
        /// Saves pets found on the tick. Called from the tick's drain through
        /// SafeDispatch, one task per note. An event pet already owned is kept
        /// as is - the drop simply found nothing new. A MONSTER pet already
        /// owned pays its duplicate diamonds by mail instead (owner,
        /// 2026-10-10), because with a pool of ten a long-played account meets
        /// mostly duplicates and "nothing" would make the roll feel dead.
        /// </summary>
        public static async Task SaveDropAsync(IDbContextFactory<FolkIdleDbContext> factory, PlayerSessionRegistry registry, PetDropNote note)
        {
            await using var db = await factory.CreateDbContextAsync();
            var outcome = await SaveDropCoreAsync(db, note, DateTime.UtcNow);
            if (outcome == DropOutcome.New)
            {
                registry.EnqueueCommandResult(note.PlayerId, (byte)FolkIdle.Server.Network.CommandResultCode.EventPetFound);
            }
            else if (outcome == DropOutcome.Duplicate)
            {
                registry.EnqueueCommandResult(note.PlayerId, (byte)FolkIdle.Server.Network.CommandResultCode.PetDuplicateDiamonds);
            }
        }

        public enum DropOutcome { Nothing = 0, New = 1, Duplicate = 2 }

        /// <summary>The save itself, without the session - what the tests call.</summary>
        internal static async Task<DropOutcome> SaveDropCoreAsync(FolkIdleDbContext db, PetDropNote note, DateTime utcNow)
        {
            var pet = PetRegistry.Find(note.PetId);
            if (pet == null) return DropOutcome.Nothing;
            if (await GrantAsync(db, note.PlayerId, note.PetId, utcNow)) return DropOutcome.New;
            if (pet.Source != PetSource.Monster) return DropOutcome.Nothing;

            // Modul: DIAMONDS GO BY MAIL. The live payload owns the diamond
            // balance and this runs off the tick, so writing PlayerRecords here
            // would be overwritten by the next checkpoint - mail is how an
            // off-tick worker pays a diamond safely (as the seasonal boss does).
            db.MailboxInstances.Add(new MailboxInstance
            {
                PlayerId = note.PlayerId,
                BaseItemId = string.Empty,
                Quantity = 0,
                DiamondAttachment = PetRegistry.DuplicateDiamonds,
                SenderName = pet.Name,
                MessageText = $"{pet.Name} found you again. You already have one, so here are {PetRegistry.DuplicateDiamonds} diamonds instead.",
                ReceivedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync();
            return DropOutcome.Duplicate;
        }
    }
}
