using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public enum MentorshipContractResult
    {
        Established = 0,
        InvalidRequest = 1,
        ConstraintViolation = 2
    }

    public class MentorshipEngine
    {
        private const int MentorMinimumLevel = 10;
        private const double ContractExpBonusMultiplier = 1.15;
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public MentorshipEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task ExecuteAssignMentorAsync(long playerId, Guid characterId, int slotIndex)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                int academyLevel = await dbContext.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.MentorshipAcademyBuildingId)
                    .Select(v => (int?)v.CurrentLevel)
                    .SingleOrDefaultAsync() ?? 0;

                if (academyLevel <= 0 || slotIndex >= academyLevel)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 22, Value2 = 3, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                // Explicit FOR UPDATE lock on the target character to verify it belongs to the player
                var character = await dbContext.CharacterRecords
                    .FromSqlRaw("SELECT * FROM characters WHERE Id = {0} AND PlayerId = {1} FOR UPDATE", characterId, playerId)
                    .SingleOrDefaultAsync();

                if (character == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                // Explicit FOR UPDATE lock on assignments
                var existingAssignment = await dbContext.MentorshipAcademyAssignments
                    .FromSqlRaw("SELECT * FROM MentorshipAcademyAssignments WHERE PlayerId = {0} AND SlotIndex = {1} FOR UPDATE", playerId, slotIndex)
                    .SingleOrDefaultAsync();

                var currentCount = await dbContext.MentorshipAcademyAssignments
                    .CountAsync(m => m.PlayerId == playerId);

                if (existingAssignment != null)
                {
                    existingAssignment.CharacterId = characterId;
                    dbContext.MentorshipAcademyAssignments.Update(existingAssignment);
                }
                else
                {
                    if (currentCount >= academyLevel)
                    {
                        await transaction.RollbackAsync();
                        return;
                    }
                    var newAssignment = new MentorshipAcademyAssignment
                    {
                        PlayerId = playerId,
                        CharacterId = characterId,
                        SlotIndex = slotIndex
                    };
                    dbContext.MentorshipAcademyAssignments.Add(newAssignment);
                }

                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.MentorshipUpdateQueue.Enqueue(new MentorshipUpdateNotification { PlayerId = playerId });
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
            }
        }

        public async Task<MentorshipContractResult> EstablishMentorshipContractAsync(long menteePlayerId, long mentorPlayerId)
        {
            if (menteePlayerId <= 0 || mentorPlayerId <= 0 || menteePlayerId == mentorPlayerId)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = menteePlayerId, EventType = 3, Value1 = 28, Value2 = 1, Timestamp = Environment.TickCount64 });
                return MentorshipContractResult.InvalidRequest;
            }

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            try
            {
                long firstLockId = menteePlayerId < mentorPlayerId ? menteePlayerId : mentorPlayerId;
                long secondLockId = menteePlayerId < mentorPlayerId ? mentorPlayerId : menteePlayerId;

                var firstPlayer = await dbContext.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", firstLockId)
                    .SingleOrDefaultAsync();
                var secondPlayer = await dbContext.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", secondLockId)
                    .SingleOrDefaultAsync();

                if (firstPlayer == null || secondPlayer == null)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = menteePlayerId, EventType = 3, Value1 = 28, Value2 = 2, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return MentorshipContractResult.InvalidRequest;
                }

                var mentor = firstPlayer.Id == mentorPlayerId ? firstPlayer : secondPlayer;
                if (mentor.CurrentLevel < MentorMinimumLevel)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = menteePlayerId, EventType = 4, Value1 = 28, Value2 = 1, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return MentorshipContractResult.ConstraintViolation;
                }

                var existingContract = await dbContext.MentorshipContracts
                    .FromSqlRaw("SELECT * FROM \"MentorshipContracts\" WHERE \"MenteePlayerId\" = {0} FOR UPDATE", menteePlayerId)
                    .SingleOrDefaultAsync();

                if (existingContract != null)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = menteePlayerId, EventType = 3, Value1 = 28, Value2 = 3, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return MentorshipContractResult.ConstraintViolation;
                }

                int menteeAcademyLevel = await dbContext.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == menteePlayerId && v.BuildingId == VillageManagementEngine.MentorshipAcademyBuildingId)
                    .Select(v => (int?)v.CurrentLevel)
                    .SingleOrDefaultAsync() ?? 0;

                int activeMenteeContractCount = await dbContext.MentorshipContracts
                    .AsNoTracking()
                    .CountAsync(m => m.MenteePlayerId == menteePlayerId || m.MentorPlayerId == menteePlayerId);

                if (menteeAcademyLevel <= 0 || activeMenteeContractCount >= menteeAcademyLevel)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = menteePlayerId, EventType = 3, Value1 = 28, Value2 = 6, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return MentorshipContractResult.InvalidRequest;
                }

                dbContext.MentorshipContracts.Add(new MentorshipContract
                {
                    MentorPlayerId = mentorPlayerId,
                    MenteePlayerId = menteePlayerId,
                    ExpBonusMultiplier = ContractExpBonusMultiplier,
                    TimestampEstablished = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });

                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.MentorshipContractUpdateQueue.Enqueue(new MentorshipContractUpdateNotification
                {
                    PlayerId = menteePlayerId,
                    MentorPlayerId = mentorPlayerId,
                    ExpBonusMultiplier = ContractExpBonusMultiplier,
                    ActiveContractCount = ClampByte(activeMenteeContractCount + 1)
                });

                return MentorshipContractResult.Established;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Mentorship contract failed: {ex.Message}");
                return MentorshipContractResult.InvalidRequest;
            }
        }

        private static byte ClampByte(int value)
        {
            if (value <= 0) return 0;
            if (value >= byte.MaxValue) return byte.MaxValue;
            return (byte)value;
        }
    }
}
