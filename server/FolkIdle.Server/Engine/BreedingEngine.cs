using System;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public class BreedingEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public BreedingEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task ExecuteBreedingAsync(long playerId, Guid paternalId, Guid maternalId)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                int breedingLevel = await dbContext.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.BreedingGroundsBuildingId)
                    .Select(v => (int?)v.CurrentLevel)
                    .SingleOrDefaultAsync() ?? 0;

                if (breedingLevel <= 0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 15, Value2 = 4, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                // Lock parent rows across BOTH characters and character_lineage_registry
                var pChar = await dbContext.CharacterRecords
                    .FromSqlRaw("SELECT * FROM characters WHERE Id = {0} FOR UPDATE", paternalId)
                    .FirstOrDefaultAsync();
                
                var mChar = await dbContext.CharacterRecords
                    .FromSqlRaw("SELECT * FROM characters WHERE Id = {0} FOR UPDATE", maternalId)
                    .FirstOrDefaultAsync();

                var pLineage = await dbContext.CharacterLineages
                    .FromSqlRaw("SELECT * FROM character_lineage_registry WHERE CharacterId = {0} FOR UPDATE", paternalId)
                    .FirstOrDefaultAsync();

                var mLineage = await dbContext.CharacterLineages
                    .FromSqlRaw("SELECT * FROM character_lineage_registry WHERE CharacterId = {0} FOR UPDATE", maternalId)
                    .FirstOrDefaultAsync();

                if (pChar == null || mChar == null || pLineage == null || mLineage == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                if (pChar.PlayerId != playerId || mChar.PlayerId != playerId)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                if (pChar.AgePhase < 1 || mChar.AgePhase < 1 || pChar.Level < 50 || mChar.Level < 50)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                if (pChar.IsLockedInEscrow || mChar.IsLockedInEscrow)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                var pVec = new GeneticVector(pLineage.GeneticVector);
                var mVec = new GeneticVector(mLineage.GeneticVector);

                if (pVec.LocusRace.Dominant != mVec.LocusRace.Dominant)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                int maxGen = Math.Max(pLineage.GenerationIndex, mLineage.GenerationIndex);
                long childGenome = GeneticSplicingEngine.Breed(pLineage.GeneticVector, mLineage.GeneticVector, maxGen);

                var childId = Guid.NewGuid();
                var newChar = new CharacterRecord
                {
                    Id = childId,
                    PlayerId = playerId,
                    Level = 1,
                    AgePhase = 0,
                    IsLockedInEscrow = false
                };

                var newLineage = new CharacterLineageRegistry
                {
                    CharacterId = childId,
                    ParentPaternalId = paternalId,
                    ParentMaternalId = maternalId,
                    GenerationIndex = maxGen + 1,
                    GeneticVector = childGenome
                };

                dbContext.CharacterRecords.Add(newChar);
                dbContext.CharacterLineages.Add(newLineage);
                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.BirthNotificationQueue.Enqueue(new BirthNotification
                {
                    PlayerId = playerId,
                    ChildCharacterId = childId,
                    GeneticVector = childGenome
                });
            }
            catch
            {
                await transaction.RollbackAsync();
            }
        }
    }
}
