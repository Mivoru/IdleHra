using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public static class GeneticSplicingEngine
    {
        public static long Breed(long paternalGenome, long maternalGenome, int maxGeneration)
        {
            var pVec = new GeneticVector(paternalGenome);
            var mVec = new GeneticVector(maternalGenome);
            var cVec = new GeneticVector(0);

            // Modul: RACE DOES NOT MUTATE, and letting it was a live defect.
            //
            // The mutation below flips the low five bits (^ 0x1F). On a quality
            // locus that is the whole point - a jump of up to 31 in Speed, Crit
            // or Yield. On RACE it produces a SPECIES THAT DOES NOT EXIST: a
            // Human is race 1, and 1 ^ 0x1F is 30, against six real races. The
            // child then has no mastery table, no innate passives and no
            // artwork, and nothing anywhere logs it.
            //
            // It fires on about 1.5% of pairings at generation 0, which is why
            // it presented as an intermittent test rather than a bug report -
            // Test_HeroVillager_MarriesAndTheVillagerBecomesAnElder failed once
            // in a full suite run with "expected 1, actual 30" and passed on a
            // re-run.
            //
            // The intent was already written down one method below:
            // ApplyInbreedingDegradation says "never LocusRace - a genetic
            // defect changes the child's potential, not its species". The
            // mutation path simply never got the same treatment, because it is
            // applied uniformly inside SpliceLocus to all four loci.
            //
            // BreedingEngine already refuses to pair two different races, so a
            // child's species is fully determined by its parents and there is
            // nothing for a roll to decide.
            cVec.LocusRace = SpliceLocus(pVec.LocusRace, mVec.LocusRace, maxGeneration, allowMutation: false);
            cVec.LocusSpeed = SpliceLocus(pVec.LocusSpeed, mVec.LocusSpeed, maxGeneration);
            cVec.LocusCrit = SpliceLocus(pVec.LocusCrit, mVec.LocusCrit, maxGeneration);
            cVec.LocusYield = SpliceLocus(pVec.LocusYield, mVec.LocusYield, maxGeneration);

            return cVec.RawValue;
        }

        // Modul 13.4.3: applied by BreedingEngine when the two candidate parents
        // share an ancestor within 2 generations. Degrades only the "quality"
        // loci (Speed/Crit/Yield), never LocusRace - a genetic defect changes
        // the child's potential, not its species.
        public static long ApplyInbreedingDegradation(long genome)
        {
            var vec = new GeneticVector(genome);
            vec.LocusSpeed = DegradeLocus(vec.LocusSpeed);
            vec.LocusCrit = DegradeLocus(vec.LocusCrit);
            vec.LocusYield = DegradeLocus(vec.LocusYield);
            return vec.RawValue;
        }

        // Modul: read-only preview of Breed()'s possible outcomes for a
        // single locus, used by the Breeding Lab preview endpoint. Enumerates
        // all 4 non-mutated (pAllele, mAllele) combinations - each parent
        // independently contributes either its Dominant or Recessive allele
        // with equal probability, exactly mirroring SpliceLocus's own
        // "childLocus.Dominant = max(pAllele, mAllele)" rule - rather than
        // sampling, so this is the exact achievable range absent a mutation
        // roll, not a statistical approximation. MutationChancePct uses the
        // identical formula SpliceLocus itself rolls against.
        public static void PreviewLocus(Locus pLocus, Locus mLocus, int maxGeneration, out byte minDominant, out byte maxDominant, out double mutationChancePct)
        {
            byte c1 = MaxByte(pLocus.Dominant, mLocus.Dominant);
            byte c2 = MaxByte(pLocus.Dominant, mLocus.Recessive);
            byte c3 = MaxByte(pLocus.Recessive, mLocus.Dominant);
            byte c4 = MaxByte(pLocus.Recessive, mLocus.Recessive);

            minDominant = MinByte(MinByte(c1, c2), MinByte(c3, c4));
            maxDominant = MaxByte(MaxByte(c1, c2), MaxByte(c3, c4));

            double pMut = Math.Max(0.001, 0.015 * Math.Pow(1.12, -maxGeneration));
            mutationChancePct = pMut * 100.0;
        }

        private static byte MaxByte(byte a, byte b) => a >= b ? a : b;
        private static byte MinByte(byte a, byte b) => a <= b ? a : b;

        private static Locus DegradeLocus(Locus locus)
        {
            return new Locus
            {
                Dominant = (byte)(locus.Dominant - (locus.Dominant / 4)),
                Recessive = (byte)(locus.Recessive - (locus.Recessive / 4))
            };
        }

        /// <param name="allowMutation">
        /// False for LocusRace. See the call site: the mutation flips the low
        /// five bits, which on a species id produces one that does not exist.
        /// </param>
        private static Locus SpliceLocus(Locus pLocus, Locus mLocus, int maxGeneration, bool allowMutation = true)
        {
            byte pAllele = Random.Shared.NextDouble() > 0.5 ? pLocus.Dominant : pLocus.Recessive;
            byte mAllele = Random.Shared.NextDouble() > 0.5 ? mLocus.Dominant : mLocus.Recessive;

            Locus childLocus = new Locus();
            if (pAllele >= mAllele)
            {
                childLocus.Dominant = pAllele;
                childLocus.Recessive = mAllele;
            }
            else
            {
                childLocus.Dominant = mAllele;
                childLocus.Recessive = pAllele;
            }

            double pMut = Math.Max(0.001, 0.015 * Math.Pow(1.12, -maxGeneration));
            if (allowMutation && Random.Shared.NextDouble() < pMut)
            {
                childLocus.Dominant = (byte)(childLocus.Dominant ^ 0x1F);
                childLocus.Recessive = (byte)(childLocus.Recessive ^ 0x1F);
            }

            return childLocus;
        }
    }
}
