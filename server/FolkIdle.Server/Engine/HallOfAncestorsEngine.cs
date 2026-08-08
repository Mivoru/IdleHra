using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The Hall of Ancestors: the roster that outlives a season.
    ///
    /// Three jobs, and they were three separate absences:
    ///
    /// - **Field a member.** Nothing in this server had ever changed a
    ///   CharacterRecord.SlotIndex after the row was created, so every child
    ///   bred past the third slot was permanently unplayable - breeding stock
    ///   and nothing else. That is the exact opposite of the design, which says
    ///   the child you breed at the end of a season is the character you BEGIN
    ///   the next one with.
    /// - **Mark who carries**, which is the decision the cap exists to create.
    /// - **Buy a slot**, ten to fourteen.
    ///
    /// The cull itself lives in SeasonalRotationEngine, because that is where a
    /// season ends; the rule it culls by is HallOfAncestorsRules, which is pure.
    /// </summary>
    public sealed class HallOfAncestorsEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;

        public HallOfAncestorsEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        /// <summary>
        /// Marks or unmarks a member as one to carry.
        ///
        /// No cap check. A player may mark everyone they own: the cap is
        /// resolved by ranking at the rollover, and refusing an eleventh mark
        /// would mean explaining a limit whose value depends on a purchase they
        /// might make in the meantime.
        /// </summary>
        public async Task SetKeptAsync(long playerId, Guid characterId, bool kept)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            try
            {
                // Ownership is checked through the CHARACTER, because
                // character_lineage_registry carries no PlayerId of its own -
                // it is keyed on the character and inherits ownership from it.
                bool owned = await db.CharacterRecords
                    .AsNoTracking()
                    .AnyAsync(c => c.Id == characterId && c.PlayerId == playerId);

                if (!owned)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 73, Value2 = 1, Timestamp = Environment.TickCount64 });
                    return;
                }

                var lineage = await db.CharacterLineages.FirstOrDefaultAsync(l => l.CharacterId == characterId);
                if (lineage == null)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 73, Value2 = 2, Timestamp = Environment.TickCount64 });
                    return;
                }

                lineage.IsKeptAtRollover = kept;
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Hall SetKept failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Buys one more slot with diamonds.
        ///
        /// Serializable with the player row locked, the same shape as
        /// InheritanceEngine.PurchaseLevelAsync and for the same reason: two
        /// concurrent purchases reading one balance would both pass the check
        /// and the second slot would be free.
        /// </summary>
        public async Task PurchaseSlotAsync(long playerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var owner = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();

                if (owner == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                long cost = HallOfAncestorsRules.NextSlotCostDiamonds(owner.AncestorSlotsPurchased);
                if (cost <= 0L)
                {
                    // Zero means "all four bought", never "free".
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 72, Value2 = 1, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                    return;
                }

                if (owner.PremiumDiamonds < cost)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 72, Value2 = 2, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.InsufficientMaterials);
                    return;
                }

                owner.PremiumDiamonds -= (int)cost;
                owner.AncestorSlotsPurchased++;

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Hall PurchaseSlot failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Puts a member into one of the three playable character slots,
        /// swapping out whoever was there.
        ///
        /// NOTHING IN THIS SERVER COULD DO THIS BEFORE. SlotIndex was written
        /// once, at creation, and never again - so a bred child landed at the
        /// end of the roster and stayed there forever, unplayable. The whole
        /// season loop the design describes ("begin the next season with your
        /// best child") ran through a door that did not exist.
        ///
        /// A SWAP, not a move: the character already in the target slot takes
        /// the incoming one's old index. Anything else would either leave two
        /// characters on one slot or open a hole in the first three, and both
        /// break StateCheckpointManager's "position IS SlotIndex" contract.
        ///
        /// The target slot must be UNLOCKED by the Town Hall. Slots 2 and 3 are
        /// bought with village levels, and letting a swap sidestep that would
        /// make the building optional.
        /// </summary>
        public async Task AssignSlotAsync(long playerId, Guid characterId, int targetSlotIndex)
        {
            if (targetSlotIndex < 0 || targetSlotIndex >= CharacterSlotEngine.MaxCharacterSlots)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 75, Value2 = 1, Timestamp = Environment.TickCount64 });
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                int townHallLevel = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId
                             && v.BuildingId == Domain.Progression.VillageManagementEngine.TownHallBuildingId)
                    .Select(v => v.CurrentLevel)
                    .FirstOrDefaultAsync();

                if (!CharacterSlotEngine.IsSlotUnlocked(targetSlotIndex, townHallLevel))
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 75, Value2 = 2, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                var roster = await db.CharacterRecords
                    .FromSqlRaw("SELECT * FROM characters WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                    .ToListAsync();

                var incoming = roster.FirstOrDefault(c => c.Id == characterId);
                if (incoming == null)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 75, Value2 = 3, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                if (incoming.SlotIndex == targetSlotIndex)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                // An escrowed character is mid-trade and its ownership is not
                // settled; moving it into a playable slot would field somebody
                // who may be about to belong to someone else.
                if (incoming.IsLockedInEscrow)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 75, Value2 = 4, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                int vacated = incoming.SlotIndex;
                var displaced = roster.FirstOrDefault(c => c.SlotIndex == targetSlotIndex && c.Id != characterId);

                incoming.SlotIndex = targetSlotIndex;
                if (displaced != null)
                {
                    displaced.SlotIndex = vacated;

                    // The bench is not a place to be doing anything. A benched
                    // character keeping its activity would leave the occupancy
                    // mutex holding a job nobody is working - and the next
                    // character told to take that job would be refused for a
                    // collision with somebody who is not even fielded.
                    displaced.ActiveActivityId = 0L;
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Hall AssignSlot failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Culls the roster to the cap. Called by SeasonalRotationEngine inside
        /// its transaction, so it opens none of its own.
        ///
        /// Deletes the CHARACTER as well as the lineage row. Half of a member
        /// is worse than none: a character with no lineage is skipped by the
        /// breeding roster and cannot breed, and a lineage row with no character
        /// is a name in a family tree that nothing can play or pair.
        /// </summary>
        public static async Task CullToCapAsync(FolkIdleDbContext db, CancellationToken cancellationToken)
        {
            var players = await db.PlayerRecords
                .AsNoTracking()
                .Select(p => new { p.Id, p.PlayerGuid, p.AncestorSlotsPurchased })
                .ToListAsync(cancellationToken);

            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                int cap = HallOfAncestorsRules.CapFor(player.AncestorSlotsPurchased);

                var characters = await db.CharacterRecords
                    .Where(c => c.PlayerId == player.Id)
                    .ToListAsync(cancellationToken);

                if (characters.Count <= cap) continue;

                var characterIds = characters.Select(c => c.Id).ToList();
                var lineages = await db.CharacterLineages
                    .Where(l => characterIds.Contains(l.CharacterId))
                    .ToListAsync(cancellationToken);

                var lineageById = lineages.ToDictionary(l => l.CharacterId);

                var members = new List<HallOfAncestorsRules.Member>(characters.Count);
                for (int c = 0; c < characters.Count; c++)
                {
                    lineageById.TryGetValue(characters[c].Id, out var lineage);

                    members.Add(new HallOfAncestorsRules.Member(
                        characters[c].Id,
                        characters[c].Id == player.PlayerGuid,
                        lineage?.IsKeptAtRollover ?? false,
                        lineage?.IsEpicMutation ?? false,
                        // A character with no lineage row ranks at zero rather
                        // than being excluded: it is still a real character, and
                        // excluding it from the ranking would mean culling it
                        // without ever considering it.
                        lineage is null ? 0 : lineage.AptitudeVector().Sum(),
                        lineage?.GenerationIndex ?? 0));
                }

                var survivors = new HashSet<Guid>(HallOfAncestorsRules.ChooseSurvivors(members, cap));

                var releasedCharacters = characters.Where(c => !survivors.Contains(c.Id)).ToList();
                if (releasedCharacters.Count == 0) continue;

                var releasedIds = releasedCharacters.Select(c => c.Id).ToHashSet();

                db.CharacterLineages.RemoveRange(lineages.Where(l => releasedIds.Contains(l.CharacterId)));
                db.CharacterRecords.RemoveRange(releasedCharacters);

                // A survivor whose parent was let go keeps the id in its own
                // parent columns. That is deliberate: it is a fact about where
                // the blood came from, the inbreeding check is null-safe and
                // HasValue-guarded, and rewriting history to keep a foreign key
                // tidy would erase the only record that a generation happened.

                // The surviving roster has to occupy 0..n-1 with no holes,
                // because every consumer treats position and SlotIndex as the
                // same thing. Ordered by the ranking that just decided who
                // stays, so a player comes back to their best in slot 1.
                var order = HallOfAncestorsRules.ChooseSurvivors(
                    members.Where(m => survivors.Contains(m.CharacterId)).ToList(), cap);

                for (int rank = 0; rank < order.Count; rank++)
                {
                    var kept = characters.First(c => c.Id == order[rank]);
                    kept.SlotIndex = rank;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
