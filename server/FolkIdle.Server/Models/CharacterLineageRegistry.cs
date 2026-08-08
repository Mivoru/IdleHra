using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    [Table("character_lineage_registry")]
    public class CharacterLineageRegistry
    {
        [Key]
        [ForeignKey("Character")]
        public Guid CharacterId { get; set; }
        
        public Guid? ParentPaternalId { get; set; }
        public Guid? ParentMaternalId { get; set; }
        
        public int GenerationIndex { get; set; }
        public long GeneticVector { get; set; }

        // Modul 13.4.3: set when BreedingEngine's grand-mutation roll (separate
        // from GeneticSplicingEngine's per-locus allele noise) triggers on this
        // child's birth. Persisted as a marker only for now - not yet consumed
        // by RaceAttributeGrowth/StatsCalculator, matching the existing
        // LocusSpeed/LocusCrit/LocusYield loci which are also bred but not yet
        // read anywhere downstream.
        public bool IsEpicMutation { get; set; }

        // Modul 13.4.3: set by BreedingEngine when both candidate parents share
        // a common ancestor within 2 generations (parent-child pairing, or full/
        // half siblings sharing a parent). Consumed by RaceAttributeGrowth to
        // apply a -25% level-up growth penalty for this character's lifetime.
        public bool IsInbred { get; set; }

        // Modul: APTITUDES. Four values a lineage carries and improves across
        // generations - Strength (combat), Skill (gathering and crafting),
        // Endurance (health and armour), Fortune (luck). See
        // BreedingAptitudes for the rules and LONG_GAME_SPEC part 3 for why
        // they exist at all: level and gear are the only axes this game has and
        // the rollover wipes both, so without these a season leaves nothing
        // behind but diamonds.
        //
        // FOUR COLUMNS RATHER THAN PACKED INTO GeneticVector, which is already
        // a bit-packed long owned by GeneticSplicingEngine. Sharing that field
        // would mean two systems agreeing forever about where each other's bits
        // live, for the sake of saving twelve bytes a row.
        //
        // Ints rather than bytes only because EF and Postgres both prefer them
        // and the cap is 50 either way.
        public int AptitudeStrength { get; set; } = BreedingAptitudes.StartingValue;
        public int AptitudeSkill { get; set; } = BreedingAptitudes.StartingValue;
        public int AptitudeEndurance { get; set; } = BreedingAptitudes.StartingValue;
        public int AptitudeFortune { get; set; } = BreedingAptitudes.StartingValue;

        /// <summary>The four, in BreedingAptitudes index order.</summary>
        public int[] AptitudeVector() => new[]
        {
            AptitudeStrength, AptitudeSkill, AptitudeEndurance, AptitudeFortune,
        };

        public void SetAptitudeVector(int[] v)
        {
            if (v is null || v.Length < BreedingAptitudes.Count) return;
            AptitudeStrength = v[BreedingAptitudes.Strength];
            AptitudeSkill = v[BreedingAptitudes.Skill];
            AptitudeEndurance = v[BreedingAptitudes.Endurance];
            AptitudeFortune = v[BreedingAptitudes.Fortune];
        }

        /// <summary>
        /// Marked by the player as one to carry into the next season.
        ///
        /// A FLAG SET DURING THE SEASON, not an answer given at the rollover.
        /// The rollover runs server-side with every client disconnected, so
        /// there is nobody there to ask - and a prompt on the way back in would
        /// arrive after the season it belongs to had already ended. Marking is
        /// what gives the last week weight; see
        /// HallOfAncestorsRules.ChooseSurvivors for how ties and overruns
        /// resolve, and why the main character can never be the one let go.
        ///
        /// Survives the rollover with the row, so a member kept once stays
        /// marked - the choice was about them, not about that particular season.
        /// </summary>
        public bool IsKeptAtRollover { get; set; }

        public CharacterRecord? Character { get; set; }
    }
}
