using System;

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
