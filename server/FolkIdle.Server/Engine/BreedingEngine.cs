using System;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public class BreedingEngine
    {
        // Modul 13.4.3: Breeding Grounds gold tax, cooldown, and mutation tuning.
        // Cost scales linearly with generation (matches the existing
        // VillageManagementEngine.CalculateUpgradeCost style - a simple,
        // easily-tunable formula rather than an unbounded exponential).
        // PUBLIC because the Breeding Lab preview endpoint quotes this price
        // before the player commits to it, and a preview that computes the cost
        // from its own copy of the number is a preview that can lie.
        public const long BaseBreedingCostGold = 500L;
        private const long BreedingCooldownSeconds = 3600L;
        private const double EpicMutationChance = 0.05;

        /// <summary>The gold a pairing costs, given the older parent's generation.</summary>
        public static long CostFor(int maxGenerationIndex)
            => BaseBreedingCostGold * (Math.Max(0, maxGenerationIndex) + 1);

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

                // Modul: breeding pairs. The paternal/maternal labels used to be
                // positional only - any two characters could breed, including
                // two of the same sex, because no sex existed. Now that every
                // race arrives as a male/female pair, the labels have to mean
                // what they say or the pair is not a pair.
                if (pChar.IsFemale || !mChar.IsFemale)
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

                long breedingCost = CostFor(maxGen);
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

                // Modul: the epic roll now lives with the rest of the breeding
                // rules, and is WORSE between relatives - 5% ordinarily, 1% for
                // a related pairing. See BreedingAptitudes.
                bool isEpicMutation = BreedingAptitudes.RollEpic(isInbred, Random.Shared);

                // Modul: APTITUDES. Each of the four is inherited from ONE
                // parent, weighted by how strong that parent is in it, then
                // mutated. Crossing two specialists therefore produces a child
                // good at both, which is what makes marrying difference rather
                // than similarity the strategy - see BreedingAptitudes.
                int[] childAptitudes = BreedingAptitudes.Breed(
                    pLineage.AptitudeVector(),
                    mLineage.AptitudeVector(),
                    isInbred,
                    isEpicMutation,
                    Random.Shared);

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
                    IsLockedInEscrow = false,
                    // Modul: a newborn goes to the END of the roster. It used to
                    // take SlotIndex's default of 0 - the main character's slot -
                    // and StateCheckpointManager orders by SlotIndex then Id, so
                    // a level-1 child could sort ahead of its own parent and
                    // become the character whose gear hydrates the register.
                    SlotIndex = await CharacterGrantEngine.NextFreeSlotIndexAsync(dbContext, playerId),
                    // Modul: breeding pairs. A coin flip. Without a sex of its
                    // own every child would default to male and a lineage would
                    // be unable to breed past its founding pair.
                    IsFemale = Random.Shared.Next(2) == 1
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
                newLineage.SetAptitudeVector(childAptitudes);

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

        /// <summary>
        /// THE STANDARD PAIR: one of the player's heroes and somebody from the
        /// village. See LONG_GAME_SPEC part 3.
        ///
        /// Why this exists at all: a child inherits each aptitude from one
        /// parent, so it can never exceed the best value already in the pair,
        /// and mutation drifts at about +0.15 a generation. Crossing your own
        /// characters therefore converges - the village is the only thing that
        /// puts a number into a bloodline that was not already in it. Until
        /// this method existed the gene pool filled up every season and nothing
        /// could marry into it.
        ///
        /// ONLY THE HERO NEEDS LEVEL 50. Requiring it of both parents would
        /// mean levelling two characters to fifty for one roll of the dice,
        /// which is double the grind for the same child.
        /// </summary>
        public async Task ExecuteHeroVillagerBreedingAsync(long playerId, Guid heroId, long newcomerId)
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
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 69, Value2 = 1, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                // Same locking discipline as the character pairing above: both
                // sides of the pair and the hero's lineage row, held for the
                // life of the transaction, so two concurrent attempts cannot
                // both spend the same villager.
                var hero = await dbContext.CharacterRecords
                    .FromSqlRaw("SELECT * FROM characters WHERE \"Id\" = {0} FOR UPDATE", heroId)
                    .FirstOrDefaultAsync();

                var newcomer = await dbContext.VillageNewcomers
                    .FromSqlRaw("SELECT * FROM village_newcomers WHERE \"Id\" = {0} FOR UPDATE", newcomerId)
                    .FirstOrDefaultAsync();

                var heroLineage = await dbContext.CharacterLineages
                    .FromSqlRaw("SELECT * FROM character_lineage_registry WHERE \"CharacterId\" = {0} FOR UPDATE", heroId)
                    .FirstOrDefaultAsync();

                if (hero == null || newcomer == null || heroLineage == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                if (hero.PlayerId != playerId || newcomer.PlayerId != playerId)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                if (hero.AgePhase < 1 || hero.Level < 50 || hero.IsLockedInEscrow)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 69, Value2 = 2, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                // ONE CHILD PER VILLAGER, FOREVER. Without this a single lucky
                // twenty fathers the whole roster and the gene pool collapses
                // back onto one ancestor - the exact opposite of what a pool is
                // for. An elder stays on the roster as a record of the blood
                // that came in; they simply cannot marry again.
                if (newcomer.IsElder)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 69, Value2 = 3, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (hero.IsBreedingActive && hero.BreedingCooldownEndEpoch <= nowEpoch) hero.IsBreedingActive = false;

                if (hero.IsBreedingActive)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 69, Value2 = 4, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                if (hero.IsFemale == newcomer.IsFemale)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 69, Value2 = 5, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                var heroVec = new GeneticVector(heroLineage.GeneticVector);
                if (heroVec.LocusRace.Dominant != newcomer.RaceId)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 69, Value2 = 6, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                // A villager is generation zero by definition - they are the
                // outside, and the outside has no pedigree here - so the child's
                // generation and the price both key off the hero alone.
                int maxGen = heroLineage.GenerationIndex;
                long breedingCost = CostFor(maxGen);

                var goldRecord = await dbContext.CommodityRecords
                    .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();

                if (goldRecord == null || goldRecord.Quantity < breedingCost)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 69, Value2 = 7, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                goldRecord.Quantity -= breedingCost;

                // A HERO x VILLAGER PAIRING IS NEVER INBRED, and no relatedness
                // check runs here. A newcomer has no parents in this world and
                // marries exactly once (IsElder, above), so there is no ancestor
                // to share and no half-sibling through them to find later.
                // Children of the same HERO by different villagers still share
                // that hero, and the character-pairing check above catches them
                // the ordinary way.
                const bool isInbred = false;
                bool isEpicMutation = BreedingAptitudes.RollEpic(isInbred, Random.Shared);

                long heroGenome = heroLineage.GeneticVector;
                long villagerGenome = newcomer.Genome();
                int[] heroAptitudes = heroLineage.AptitudeVector();
                int[] villagerAptitudes = newcomer.AptitudeVector();

                // Splicing takes a paternal and a maternal genome and the labels
                // have to mean what they say, so whichever of the two is male
                // goes in first. The child construction below stores the hero's
                // id in the matching parent column and leaves the other null -
                // the villager is not a CharacterRecord and never will be.
                bool heroIsFather = !hero.IsFemale;

                long childGenome = heroIsFather
                    ? GeneticSplicingEngine.Breed(heroGenome, villagerGenome, maxGen)
                    : GeneticSplicingEngine.Breed(villagerGenome, heroGenome, maxGen);

                int[] childAptitudes = heroIsFather
                    ? BreedingAptitudes.Breed(heroAptitudes, villagerAptitudes, isInbred, isEpicMutation, Random.Shared)
                    : BreedingAptitudes.Breed(villagerAptitudes, heroAptitudes, isInbred, isEpicMutation, Random.Shared);

                hero.IsBreedingActive = true;
                hero.BreedingCooldownEndEpoch = nowEpoch + BreedingCooldownSeconds;
                newcomer.IsElder = true;

                var childId = Guid.NewGuid();
                dbContext.CharacterRecords.Add(new CharacterRecord
                {
                    Id = childId,
                    PlayerId = playerId,
                    Level = 1,
                    AgePhase = 0,
                    IsLockedInEscrow = false,
                    SlotIndex = await CharacterGrantEngine.NextFreeSlotIndexAsync(dbContext, playerId),
                    IsFemale = Random.Shared.Next(2) == 1
                });

                var newLineage = new CharacterLineageRegistry
                {
                    CharacterId = childId,
                    // Only the hero's side is recorded. The village is wiped at
                    // the rollover and the lineage registry is not, so a column
                    // pointing at a villager would dangle within ninety days -
                    // and the aptitudes they contributed are already in the
                    // child, which is the part that was meant to survive.
                    ParentPaternalId = heroIsFather ? heroId : null,
                    ParentMaternalId = heroIsFather ? null : heroId,
                    GenerationIndex = maxGen + 1,
                    GeneticVector = childGenome,
                    IsEpicMutation = isEpicMutation,
                    IsInbred = isInbred
                };
                newLineage.SetAptitudeVector(childAptitudes);

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
                Console.WriteLine($"Hero-villager breeding failed: {ex.Message}");
            }
        }
    }
}
