using System;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public class BreedingEngine
    {
        // Modul 13.4.3: Breeding Grounds gold tax, cooldown, and mutation tuning.
        // Cost scales linearly with generation (matches the existing
        // VillageManagementEngine.CalculateUpgradeCost style - a simple,
        // easily-tunable formula rather than an unbounded exponential).
        private const long BaseBreedingCostGold = 500L;
        private const long BreedingCooldownSeconds = 3600L;
        private const double EpicMutationChance = 0.05;

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
                    .FromSqlRaw("SELECT * FROM characters WHERE \"Id\" = {0} FOR UPDATE", paternalId)
                    .FirstOrDefaultAsync();

                var mChar = await dbContext.CharacterRecords
                    .FromSqlRaw("SELECT * FROM characters WHERE \"Id\" = {0} FOR UPDATE", maternalId)
                    .FirstOrDefaultAsync();

                var pLineage = await dbContext.CharacterLineages
                    .FromSqlRaw("SELECT * FROM character_lineage_registry WHERE \"CharacterId\" = {0} FOR UPDATE", paternalId)
                    .FirstOrDefaultAsync();

                var mLineage = await dbContext.CharacterLineages
                    .FromSqlRaw("SELECT * FROM character_lineage_registry WHERE \"CharacterId\" = {0} FOR UPDATE", maternalId)
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

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Lazily clear a parent's IsBreedingActive flag once its cooldown
                // has actually elapsed, rather than requiring a separate sweep.
                if (pChar.IsBreedingActive && pChar.BreedingCooldownEndEpoch <= nowEpoch) pChar.IsBreedingActive = false;
                if (mChar.IsBreedingActive && mChar.BreedingCooldownEndEpoch <= nowEpoch) mChar.IsBreedingActive = false;

                if (pChar.IsBreedingActive || mChar.IsBreedingActive)
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

                long breedingCost = BaseBreedingCostGold * (maxGen + 1);
                var goldRecord = await dbContext.CommodityRecords
                    .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();

                if (goldRecord == null || goldRecord.Quantity < breedingCost)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 15, Value2 = 5, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                goldRecord.Quantity -= breedingCost;

                // Modul 13.4.3: inbreeding check within 2 generations of the
                // prospective child, using data already loaded above (no extra
                // query needed) - a direct parent-child pairing (one candidate
                // parent is literally the other's own parent), or full/half
                // siblings sharing a common parent of their own.
                bool isInbred = paternalId == mLineage.ParentPaternalId || paternalId == mLineage.ParentMaternalId
                    || maternalId == pLineage.ParentPaternalId || maternalId == pLineage.ParentMaternalId
                    || (pLineage.ParentPaternalId.HasValue && (pLineage.ParentPaternalId == mLineage.ParentPaternalId || pLineage.ParentPaternalId == mLineage.ParentMaternalId))
                    || (pLineage.ParentMaternalId.HasValue && (pLineage.ParentMaternalId == mLineage.ParentPaternalId || pLineage.ParentMaternalId == mLineage.ParentMaternalId));

                long childGenome = GeneticSplicingEngine.Breed(pLineage.GeneticVector, mLineage.GeneticVector, maxGen);
                if (isInbred)
                {
                    childGenome = GeneticSplicingEngine.ApplyInbreedingDegradation(childGenome);
                }

                bool isEpicMutation = Random.Shared.NextDouble() < EpicMutationChance;

                pChar.IsBreedingActive = true;
                pChar.BreedingCooldownEndEpoch = nowEpoch + BreedingCooldownSeconds;
                mChar.IsBreedingActive = true;
                mChar.BreedingCooldownEndEpoch = nowEpoch + BreedingCooldownSeconds;

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
                    GeneticVector = childGenome,
                    IsEpicMutation = isEpicMutation,
                    IsInbred = isInbred
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
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Breeding failed: {ex.Message}");
            }
        }
    }
}
