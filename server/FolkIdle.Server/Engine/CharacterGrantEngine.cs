using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    // Modul: breeding pairs. The one place characters are handed to a player,
    // so the starter roster and the race-unlock reward cannot drift apart.
    //
    // Races are granted as a MALE/FEMALE PAIR rather than a single character.
    // A lone character of a race is a dead end: BreedingEngine requires both
    // parents to share a race, and there is no other way to obtain a second
    // character of a non-Human race (accounts are created Human and cross-race
    // breeding is refused). Granting one Vila would therefore have handed the
    // player a race they could look at and never propagate. A pair is a
    // founding population.
    public static class CharacterGrantEngine
    {
        // A new account's starting roster: one male and one female Human. The
        // male takes the player's PlayerGuid, because that id is what
        // StateCheckpointManager resolves as Slot1 and what the rest of the
        // codebase treats as "the main character".
        //
        // The female lands in slot 2, which is locked until Town Hall 3 - she
        // exists to breed with from the start, and becomes assignable to her own
        // activity once the village can support a second worker.
        public static void SeedStarterHumanPair(FolkIdleDbContext db, long playerId, Guid mainCharacterId)
        {
            AddCharacter(db, playerId, mainCharacterId, RaceIds.Human, isFemale: false, slotIndex: 0);
            AddCharacter(db, playerId, Guid.NewGuid(), RaceIds.Human, isFemale: true, slotIndex: 1);
        }

        // The race-unlock reward. Both characters are adults so they can work
        // and breed immediately - handing over children as a boss reward would
        // read as a penalty - and both are idle, because assigning an activity
        // here could collide with an existing character and violate
        // CharacterSlotEngine's one-character-per-activity rule.
        public static async Task GrantRacePairAsync(FolkIdleDbContext db, long playerId, byte raceId, CancellationToken cancellationToken)
        {
            var usedSlotIndices = await db.CharacterRecords
                .Where(c => c.PlayerId == playerId)
                .Select(c => c.SlotIndex)
                .ToListAsync(cancellationToken);

            int maleSlot = NextFreeSlotIndex(usedSlotIndices);
            usedSlotIndices.Add(maleSlot);
            int femaleSlot = NextFreeSlotIndex(usedSlotIndices);

            AddCharacter(db, playerId, Guid.NewGuid(), raceId, isFemale: false, slotIndex: maleSlot);
            AddCharacter(db, playerId, Guid.NewGuid(), raceId, isFemale: true, slotIndex: femaleSlot);
        }

        // Slot indices beyond CharacterSlotEngine.MaxCharacterSlots are legal
        // and expected: a player who has beaten several bosses owns more
        // characters than they can field at once. Those wait in the roster for
        // a slot to be freed, which is the roster screen's job - they are not
        // lost, and they can still be bred.
        private static int NextFreeSlotIndex(System.Collections.Generic.List<int> used)
        {
            int slotIndex = 0;
            while (used.Contains(slotIndex))
            {
                slotIndex++;
            }
            return slotIndex;
        }

        private static void AddCharacter(FolkIdleDbContext db, long playerId, Guid characterId, byte raceId, bool isFemale, int slotIndex)
        {
            db.CharacterRecords.Add(new CharacterRecord
            {
                Id = characterId,
                PlayerId = playerId,
                Level = 1,
                AgePhase = 1,
                AgeTicks = 0L,
                SlotIndex = slotIndex,
                ActiveActivityId = 0L,
                IsFemale = isFemale
            });

            // Race occupies the genome's first locus, dominant AND recessive,
            // so the pair breeds true rather than reverting through a recessive
            // half neither parent carries.
            var genome = new GeneticVector(0L);
            genome.LocusRace = new Locus { Dominant = raceId, Recessive = raceId };

            db.CharacterLineages.Add(new CharacterLineageRegistry
            {
                CharacterId = characterId,
                GenerationIndex = 0,
                GeneticVector = genome.RawValue
            });
        }
    }
}
