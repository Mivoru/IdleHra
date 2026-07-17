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

            cVec.LocusRace = SpliceLocus(pVec.LocusRace, mVec.LocusRace, maxGeneration);
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

        private static Locus SpliceLocus(Locus pLocus, Locus mLocus, int maxGeneration)
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
            if (Random.Shared.NextDouble() < pMut)
            {
                childLocus.Dominant = (byte)(childLocus.Dominant ^ 0x1F);
                childLocus.Recessive = (byte)(childLocus.Recessive ^ 0x1F);
            }

            return childLocus;
        }
    }
}
