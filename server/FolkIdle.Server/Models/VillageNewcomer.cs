using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// Somebody who has come to live in the village, and the blood they carry.
    ///
    /// THE VILLAGE IS THE GENE POOL. Breeding takes each aptitude from one
    /// parent, so a child can never exceed the best value already in the pair,
    /// and mutation drifts at +0.15 a generation - not a road to anywhere on
    /// its own. Outside blood is what actually moves a bloodline, and these are
    /// where it comes from.
    ///
    /// NOT VillageResident, which is a different and older thing: that one is a
    /// work slot with an efficiency modifier and no identity at all. These are
    /// people with genes. Two concepts, two tables, and naming them apart is
    /// cheaper than explaining the collision to every future reader.
    ///
    /// SEASONAL, unlike the lineage. Newcomers are wiped at the rollover along
    /// with the village they came to; only BORN CHILDREN carry forward. A
    /// recruit is seasonal labour and family is permanent - which also means
    /// carrying a bloodline forward requires having actually bred during the
    /// season, and that is the behaviour worth rewarding.
    /// </summary>
    [Table("village_newcomers")]
    public class VillageNewcomer
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        /// <summary>A lineage/race id, for the portrait and for the race locus.</summary>
        public int RaceId { get; set; }

        public bool IsFemale { get; set; }

        /// <summary>
        /// Rolled on arrival against the Inn - see
        /// BreedingAptitudes.RollVillager. Never above the villager ceiling of
        /// 20, which is the whole of the two-phase climb: the village carries a
        /// line to twenty, and everything past that is generations of selection.
        /// </summary>
        public int AptitudeStrength { get; set; }
        public int AptitudeSkill { get; set; }
        public int AptitudeEndurance { get; set; }
        public int AptitudeFortune { get; set; }

        public long ArrivedAtEpoch { get; set; }

        /// <summary>
        /// Set once this newcomer has been a parent. They stay as a record of
        /// the blood that entered the line but cannot breed again - otherwise
        /// one lucky twenty would father the whole roster and the gene pool
        /// would collapse back onto a single ancestor, which is the exact
        /// opposite of what a gene pool is for.
        /// </summary>
        public bool IsElder { get; set; }

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

        /// <summary>The sum, for ranking a roster at a glance.</summary>
        public int AptitudeTotal()
            => AptitudeStrength + AptitudeSkill + AptitudeEndurance + AptitudeFortune;

        /// <summary>
        /// The genome a newcomer brings to a pairing: race in the first locus,
        /// dominant AND recessive, and nothing anywhere else.
        ///
        /// EXACTLY WHAT A FRESH CHARACTER CARRIES - see CharacterGrantEngine,
        /// which builds a starter the same way. The consequence is deliberate
        /// and worth stating: Speed/Crit/Yield only ever grow by mutation, so a
        /// hero who has accumulated them will see a child inherit at most one
        /// of each pair from them and a zero from the villager. Outside blood
        /// raises aptitudes and dilutes the loci. That is the trade, not a bug,
        /// and it is why crossing your own line still has a use.
        /// </summary>
        public long Genome()
        {
            var genome = new GeneticVector(0L);
            genome.LocusRace = new Locus { Dominant = (byte)RaceId, Recessive = (byte)RaceId };
            return genome.RawValue;
        }
    }
}
