namespace FolkIdle.Server.Engine
{
    public static class StorefrontSegmentationEngine
    {
        public const int Control = 0;
        public const int VariantA = 1;
        public const int VariantB = 2;

        private const uint StaticCohortSeed = 0x45F01D47U;

        public static int ResolveCohort(long playerId)
        {
            uint hash = MurmurHash3.Hash64(playerId, StaticCohortSeed);
            return (int)(hash % 3U);
        }

        public static bool IsValidCohort(int cohort)
        {
            return cohort >= Control && cohort <= VariantB;
        }
    }
}
