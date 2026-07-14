namespace FolkIdle.Server.Engine
{
    // Modul 13: stateless, allocation-free passive-modifier lookups for Race
    // Mastery levels. Every method is a pure threshold check - callers pass in
    // whichever TickStatePayload.*MasteryLevel field is relevant and get back a
    // plain numeric modifier to fold into their own calculation. No lookup here
    // performs I/O, allocates, or mutates state.
    //
    // Fomoiri is intentionally absent: it does not exist anywhere in RaceIds or
    // the genetics/breeding system, so there is no mastery level to gate a
    // damage-reduction bonus on.
    public static class RaceMasteryResolver
    {
        public static int GetHumanXpBonusPct(int humanMasteryLevel)
        {
            if (humanMasteryLevel >= 50) return 15;
            if (humanMasteryLevel >= 10) return 12;
            return 0;
        }

        public static int GetHumanVaultBonusSlots(int humanMasteryLevel)
        {
            return humanMasteryLevel >= 25 ? 5 : 0;
        }

        public static float GetVilaCritBonusPct(int vilaMasteryLevel)
        {
            return vilaMasteryLevel >= 50 ? 8f : 0f;
        }

        public static float GetDraugrLifestealBonusPct(int draugrMasteryLevel)
        {
            if (draugrMasteryLevel >= 50) return 12f;
            if (draugrMasteryLevel >= 10) return 10f;
            return 0f;
        }

        public static float GetKoboldOreDuplicationBonusPct(int koboldMasteryLevel)
        {
            return koboldMasteryLevel >= 10 ? 20f : 0f;
        }

        public static float GetMoosleuteDoubleHarvestBonusPct(int moosleuteMasteryLevel)
        {
            return moosleuteMasteryLevel >= 10 ? 16f : 0f;
        }

        // The brief's example value (10 hours) is below the current 12-hour
        // universal baseline (OfflineSimulationEngine.MaxOfflineSeconds), which
        // would make this a downgrade rather than an extension. 18 hours is a
        // clearly-larger, tunable placeholder that honors the "extend AFK
        // timeout" intent instead.
        public static long GetVodnikExtendedOfflineSeconds(int vodnikMasteryLevel, long baseOfflineSeconds)
        {
            const long ExtendedOfflineSeconds = 18L * 3600L;
            return vodnikMasteryLevel >= 25 ? System.Math.Max(baseOfflineSeconds, ExtendedOfflineSeconds) : baseOfflineSeconds;
        }
    }
}
